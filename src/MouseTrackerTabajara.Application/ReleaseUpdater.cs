using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MouseTrackerTabajara.Application;

public sealed record UpdateRelease(string Version, string Tag, Uri ReleaseUrl, Uri DownloadUrl, long Bytes, string Sha256);
public sealed record UpdateProgress(string Message, long BytesReceived, long TotalBytes);
public sealed record PreparedUpdate(string Version, string InstallDirectory, string ExecutablePath);

/// <summary>Downloads verified public releases into new app-only folders. Never mutates a current install or study.</summary>
public sealed class ReleaseUpdater(HttpClient http, string installRoot)
{
    private const string Repository = "pcarvalho75/RatoObservatorTabajara";
    private const long MaxArchive = 512L * 1024 * 1024, MaxExpanded = 2L * 1024 * 1024 * 1024;
    private const int MaxFiles = 5000, MaxMetadata = 2 * 1024 * 1024;
    private static readonly Regex HashPattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-dev\\.(0|[1-9][0-9]*))?$", RegexOptions.CultureInvariant);
    private sealed record Number(int Major, int Minor, int Patch, int? Development) : IComparable<Number>
    {
        public int CompareTo(Number? other)
        {
            if (other is null) return 1;
            var c = Major.CompareTo(other.Major); if (c != 0) return c;
            c = Minor.CompareTo(other.Minor); if (c != 0) return c;
            c = Patch.CompareTo(other.Patch); if (c != 0) return c;
            return Development is null ? other.Development is null ? 0 : 1 : other.Development is null ? -1 : Development.Value.CompareTo(other.Development.Value);
        }
    }
    private static Number Parse(string value)
    {
        var m = VersionPattern.Match(value);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var major) || !int.TryParse(m.Groups[2].Value, out var minor) || !int.TryParse(m.Groups[3].Value, out var patch) || m.Groups[4].Success && !int.TryParse(m.Groups[4].Value, out _))
            throw new InvalidDataException("Release version must be strict major.minor.patch or major.minor.patch-dev.N.");
        return new(major, minor, patch, m.Groups[4].Success ? int.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) : null);
    }
    private static string AssetName(string version) => $"RatoObservatorTabajara-{version}-win-x64.zip";
    private static void ExactUri(Uri uri, string path)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != path)
            throw new InvalidDataException("Release URL must be the exact approved GitHub repository path.");
    }
    private static void ValidateRelease(UpdateRelease release)
    {
        Parse(release.Version);
        if (release.Tag != release.Version && release.Tag != "v" + release.Version) throw new InvalidDataException("Release tag/version mismatch.");
        ExactUri(release.ReleaseUrl, $"/{Repository}/releases/tag/{release.Tag}");
        ExactUri(release.DownloadUrl, $"/{Repository}/releases/download/{release.Tag}/{AssetName(release.Version)}");
        if (release.Bytes <= 0 || release.Bytes >= MaxArchive || !HashPattern.IsMatch(release.Sha256)) throw new InvalidDataException("Release requires bounded positive size and SHA256 digest.");
    }
    private static HttpRequestMessage Request(Uri uri, bool metadata)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("RatoObservatorTabajara-Updater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(metadata ? "application/vnd.github+json" : "application/octet-stream"));
        if (metadata) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }
    public async Task<UpdateRelease?> CheckAsync(string currentVersion, bool includePrereleases, CancellationToken token = default)
    {
        var current = Parse(currentVersion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(new($"https://api.github.com/repos/{Repository}/releases?per_page=30"), true);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
        var bytes = await ReadBounded(response.Content, MaxMetadata, timeout.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 30) throw new InvalidDataException("Unexpected release listing.");
        UpdateRelease? best = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                if (item.GetProperty("draft").GetBoolean()) continue;
                var prerelease = item.GetProperty("prerelease").GetBoolean();
                var tag = item.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("Missing release tag."); var version = tag.StartsWith('v') ? tag[1..] : tag; var number = Parse(version);
                if (prerelease != number.Development.HasValue || prerelease && !includePrereleases || number.CompareTo(current) <= 0) continue;
                var assets = item.GetProperty("assets");
                if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 100) continue;
                var matching = assets.EnumerateArray().Where(a => a.GetProperty("name").GetString() == AssetName(version)).ToArray();
                if (matching.Length != 1) continue;
                var asset = matching[0]; var digest = asset.GetProperty("digest").GetString();
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)) continue;
                if (asset.GetProperty("state").GetString() != "uploaded") continue;
                var candidate = new UpdateRelease(version, tag, new Uri(item.GetProperty("html_url").GetString()!), new Uri(asset.GetProperty("browser_download_url").GetString()!), asset.GetProperty("size").GetInt64(), digest[7..]);
                ValidateRelease(candidate);
                if (best is null || number.CompareTo(Parse(best.Version)) > 0) best = candidate;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException) { /* Invalid candidates are never offered. */ }
        }
        return best;
    }
    private static async Task<byte[]> ReadBounded(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maximum) throw new InvalidDataException("Metadata exceeds limit.");
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false); using var output = new MemoryStream(); var buffer = new byte[65536];
        while (true) { var count = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) break; if (output.Length + count > maximum) throw new InvalidDataException("Metadata exceeds limit."); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
        return output.ToArray();
    }
    public async Task<PreparedUpdate> PrepareAsync(UpdateRelease release, IProgress<UpdateProgress>? progress = null, CancellationToken token = default)
    {
        ValidateRelease(release); token.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(installRoot); RejectReparse(root); Directory.CreateDirectory(root); RejectReparse(root);
        var stage = Path.Combine(root, ".update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage); RejectReparse(stage);
        var payload = Path.Combine(stage, "payload"); Directory.CreateDirectory(payload); RejectReparse(payload);
        var archive = Path.Combine(stage, "download.zip");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(20)); var ct = timeout.Token;
            using var request = Request(release.DownloadUrl, false);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Bytes) throw new InvalidDataException("Download Content-Length differs from release size.");
            // Keep this original handle through hash and extraction: no path reopen/replacement window.
            await using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.Asynchronous))
            {
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); var buffer = new byte[65536]; long received = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false); if (count == 0) break;
                    received = checked(received + count); if (received > release.Bytes || received >= MaxArchive) throw new InvalidDataException("Download exceeded declared size.");
                    await file.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); progress?.Report(new("Downloading verified release", received, release.Bytes));
                }
                if (received != release.Bytes) throw new InvalidDataException("Truncated release download.");
                await file.FlushAsync(ct).ConfigureAwait(false); file.Position = 0;
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
                if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release archive SHA256 mismatch.");
                file.Position = 0; await Extract(file, payload, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested(); ValidatePayload(payload, release.Version, ct);
            RejectReparse(root); RejectReparse(stage); RejectReparse(payload); ct.ThrowIfCancellationRequested();
            var ready = Path.Combine(root, release.Version + "-" + Guid.NewGuid().ToString("N"));
            Directory.Move(payload, ready); // Same-root atomic publication; destination cannot overwrite any prior install.
            File.WriteAllText(Path.Combine(stage, "prepared.json"), JsonSerializer.Serialize(new { release.Version, InstallDirectory = ready, release.Sha256 }));
            progress?.Report(new("Verified update ready", release.Bytes, release.Bytes));
            return new(release.Version, ready, Path.Combine(ready, "desktop", "MouseTrackerTabajara.exe"));
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(stage, "failed.json"), JsonSerializer.Serialize(new { Failure = ex.GetType().Name, Message = ex.Message, release.Version, release.Sha256 })); } catch (IOException) { }
            throw;
        }
    }
    private static void RejectReparse(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null) throw new InvalidDataException("Update path cannot contain links or reparse components.");
    }
    private static string SafePath(string raw, bool directory = false)
    {
        if (raw.Length == 0 || raw.Length > 240 || raw.Contains('\\') || raw.StartsWith('/') || raw.Contains(':') || raw.Any(c => c < 32 || c == 127)) throw new InvalidDataException("Unsafe package path.");
        var path = directory ? raw.TrimEnd('/') : raw;
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<','>','"','|','?','*']) >= 0) throw new InvalidDataException("Unsafe package path segment.");
            var stem = part.Split('.')[0];
            if (Regex.IsMatch(stem, "^(CON|PRN|AUX|NUL|CLOCK\\$|CONIN\\$|CONOUT\\$|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new InvalidDataException("Reserved Windows package path.");
            if (part.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || part.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) || new[]{".tabajara", ".db", ".morrisdb", ".sqlite", ".sqlite3"}.Any(x => part.EndsWith(x, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Study/data files are forbidden in app updates.");
        }
        return path;
    }
    private static void ValidateZipIndex(FileStream file, CancellationToken token)
    {
        // Bound the central directory before ZipArchive materializes entry objects.
        token.ThrowIfCancellationRequested(); if (file.Length < 22) throw new InvalidDataException("Truncated ZIP index.");
        var tail = new byte[(int)Math.Min(65557,file.Length)]; file.Position = file.Length-tail.Length; file.ReadExactly(tail);
        var index = -1;
        for(var i=tail.Length-22;i>=0;i--)
            if(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i,4))==0x06054b50 && i+22+System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i+20,2))==tail.Length){index=i;break;}
        if(index<0)throw new InvalidDataException("ZIP footer unavailable.");
        ushort U16(int offset)=>System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index+offset,2));
        uint U32(int offset)=>System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index+offset,4));
        var count=U16(10);var size=U32(12);var start=U32(16);var footer=file.Length-tail.Length+index;
        if(U16(4)!=0 || U16(6)!=0 || U16(8)!=count || count>MaxFiles || size>MaxMetadata || start==uint.MaxValue || (long)start+size>footer)throw new InvalidDataException("ZIP index exceeds supported bounds (multi-volume/ZIP64 forbidden).");
        file.Position=start;var end=(long)start+size;var header=new byte[46];var observed=0;
        while(file.Position<end)
        {
            token.ThrowIfCancellationRequested();if(++observed>MaxFiles || end-file.Position<46)throw new InvalidDataException("ZIP central entry limit.");file.ReadExactly(header);
            if(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header)!=0x02014b50)throw new InvalidDataException("ZIP central header mismatch.");
            var skip=(long)System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28,2))+System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30,2))+System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32,2));
            if(file.Position+skip>end)throw new InvalidDataException("ZIP central metadata truncated.");file.Position+=skip;
        }
        if(observed!=count)throw new InvalidDataException("ZIP central entry count mismatch.");
    }
    private static async Task Extract(FileStream file, string payload, CancellationToken token)
    {
        ValidateZipIndex(file, token); file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, true);
        if (zip.Entries.Count > MaxFiles) throw new InvalidDataException("Too many package entries.");
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var casing = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested(); var directory = entry.FullName.EndsWith('/'); var name = SafePath(entry.FullName, directory);
            if (!entries.Add(name)) throw new InvalidDataException("Duplicate Windows package path.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000 || (entry.ExternalAttributes & ((int)FileAttributes.ReparsePoint | ((int)FileAttributes.ReparsePoint << 16))) != 0) throw new InvalidDataException("Archive link/reparse entry forbidden.");
            var components = name.Split('/'); var prefix = "";
            foreach (var component in components) { prefix = prefix.Length == 0 ? component : prefix + "/" + component; if (casing.TryGetValue(prefix, out var prior) && prior != prefix) throw new InvalidDataException("Inconsistent Windows path casing."); casing[prefix] = prefix; }
            var destination = Path.GetFullPath(Path.Combine(payload, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(payload + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package escapes stage.");
            if (directory) { if (entry.Length != 0) throw new InvalidDataException("Directory payload forbidden."); Directory.CreateDirectory(destination); RejectReparse(destination); continue; }
            total = checked(total + entry.Length); if (entry.Length < 0 || total >= MaxExpanded) throw new InvalidDataException("Expanded package exceeds limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); RejectReparse(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open(); await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            var buffer = new byte[65536]; long copied = 0;
            while (true) { var count = await source.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) break; copied = checked(copied + count); if (copied > entry.Length) throw new InvalidDataException("ZIP size mismatch."); await target.WriteAsync(buffer.AsMemory(0,count),token).ConfigureAwait(false); }
            if (copied != entry.Length) throw new InvalidDataException("ZIP truncated entry.");
        }
    }
    private static void ValidatePayload(string directory, string version, CancellationToken token)
    {
        Parse(version); var root = Path.GetFullPath(directory); RejectReparse(root);
        var paths = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        var pending = new Stack<string>(); pending.Push(root); var entryCount = 0;
        while (pending.TryPop(out var parent))
        foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
        {
            token.ThrowIfCancellationRequested(); if (++entryCount > MaxFiles) throw new InvalidDataException("Prepared package entry limit."); if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Prepared package contains reparse entry.");
            var name = SafePath(Path.GetRelativePath(root,entry).Replace('\\','/'));
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
            if (!paths.TryAdd(name,name) || paths.Count > MaxFiles) throw new InvalidDataException("Prepared package duplicates/limit.");
            total = checked(total + new FileInfo(entry).Length); if (total >= MaxExpanded) throw new InvalidDataException("Expanded package exceeds limit.");
        }
        byte[] Read(string name) => ReadMetadata(Path.Combine(root,name),token);
        using var identity = JsonDocument.Parse(Read("build-identity.json")); var id = identity.RootElement;
        if (id.GetProperty("version").GetString() != version || id.GetProperty("rid").GetString() != "win-x64" || !id.GetProperty("self_contained").GetBoolean()) throw new InvalidDataException("Prepared build identity mismatch.");
        using var manifest = JsonDocument.Parse(Read("files.sha256.json"));
        if (manifest.RootElement.ValueKind != JsonValueKind.Array || manifest.RootElement.GetArrayLength() > MaxFiles) throw new InvalidDataException("Invalid package manifest.");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.RootElement.EnumerateArray())
        {
            token.ThrowIfCancellationRequested(); var name = SafePath(item.GetProperty("path").GetString()!); var bytes = item.GetProperty("bytes").GetInt64(); var expected = item.GetProperty("sha256").GetString()!;
            if (name == "files.sha256.json" || !listed.Add(name) || !paths.TryGetValue(name,out var actual) || name != actual || bytes < 0 || !HashPattern.IsMatch(expected)) throw new InvalidDataException("Manifest membership/case mismatch.");
            var path = Path.Combine(root,name.Replace('/',Path.DirectorySeparatorChar));
            using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if (stream.Length != bytes) throw new InvalidDataException("Manifest byte count mismatch.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; int count;
            while ((count = stream.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer,0,count); }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Manifest content hash mismatch.");
        }
        if (paths.Count != listed.Count+1 || !paths.TryGetValue("files.sha256.json",out var manifestCase) || manifestCase != "files.sha256.json") throw new InvalidDataException("Manifest is not complete.");
        foreach (var required in new[]{"build-identity.json","desktop/MouseTrackerTabajara.exe","desktop/MouseTrackerTabajara.dll","desktop/MouseTrackerTabajara.runtimeconfig.json","desktop/MouseTrackerTabajara.deps.json","desktop/MouseTrackerTabajara.Application.dll","desktop/MouseTrackerTabajara.Core.dll","desktop/MouseTrackerTabajara.Storage.dll","desktop/MouseTrackerTabajara.Tracking.dll","desktop/PresentationFramework.dll","desktop/coreclr.dll","desktop/hostfxr.dll","desktop/hostpolicy.dll","Start Mouse Tracker Tabajara.cmd"})
            if (!listed.Contains(required) || paths[required] != required || new FileInfo(Path.Combine(root,required)).Length == 0) throw new InvalidDataException("Required self-contained WPF payload missing.");
    }
    private static byte[] ReadMetadata(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxMetadata) throw new InvalidDataException("Package metadata exceeds limit.");
        var bytes = new byte[checked((int)stream.Length)]; var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested(); var count = stream.Read(bytes, offset, Math.Min(65536, bytes.Length-offset));
            if (count == 0) throw new EndOfStreamException(); offset += count;
        }
        token.ThrowIfCancellationRequested();
        return bytes.AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf}) ? bytes[3..] : bytes;
    }
    public static void ValidatePrepared(PreparedUpdate update, CancellationToken token = default)
    {
        if (Path.GetFullPath(update.ExecutablePath) != Path.Combine(Path.GetFullPath(update.InstallDirectory),"desktop","MouseTrackerTabajara.exe")) throw new InvalidDataException("Prepared executable path mismatch.");
        ValidatePayload(update.InstallDirectory,update.Version,token);
    }
    public static string ReadCurrentVersion(string appBaseDirectory, string? fallbackVersion = null)
    {
        var appFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appBaseDirectory));
        foreach (var folder in new[]{appFolder,Path.GetDirectoryName(appFolder)})
        {
            if (folder is null) continue; var path = Path.Combine(folder,"build-identity.json"); if (!File.Exists(path)) continue;
            try { using var document=JsonDocument.Parse(ReadMetadata(path,CancellationToken.None)); var version=document.RootElement.GetProperty("version").GetString()!; Parse(version); return version; }
            catch(Exception ex) when(ex is IOException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException) { throw new InvalidDataException("Installed build identity is invalid.",ex); }
        }
        var fallback = fallbackVersion ?? "0.0.0"; Parse(fallback); return fallback;
    }
}
