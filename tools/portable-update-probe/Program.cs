using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Storage;

if (args.Length is < 3 or > 4 || args.Length == 4 && args[3] != "--online") throw new ArgumentException("Extracted package, matching ZIP and fresh evidence directory required; optional --online uses the real GitHub feed/download.");
var online = args.Length == 4;
var package = Path.GetFullPath(args[0]);
var archive = Path.GetFullPath(args[1]);
var evidence = Path.GetFullPath(args[2]);
if (Directory.Exists(evidence)) throw new IOException("Use fresh evidence; never overwrite prior proofs.");
Directory.CreateDirectory(evidence);
string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(); }
void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
var receipt = new Dictionary<string, object?> { ["state"] = "RUNNING", ["scope"] = online ? "Exact packaged updater with real GitHub metadata/download; synthetic study only." : "Exact package updater, synthetic study and controlled release feed; real GitHub download tested separately.", ["utc"] = DateTime.UtcNow.ToString("o") };
try
{
    var identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "build-identity.json"))).RootElement;
    var version = identity.GetProperty("version").GetString()!;
    var loaded = typeof(ReleaseUpdater).Assembly.Location;
    Require(Hash(loaded) == Hash(Path.Combine(package, "desktop", "MouseTrackerTabajara.Application.dll")), "Updater assembly differs from package.");
    var native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
    Require(Hash(native.FileName) == Hash(Path.Combine(package, "desktop", "coreclr.dll")), "Probe did not use identical packaged CoreCLR.");
    Require(ReleaseUpdater.ReadCurrentVersion(Path.Combine(package, "desktop") + Path.DirectorySeparatorChar) == version, "Current package version read incorrectly with apphost-style trailing separator.");
    var zipHash = Hash(archive);
    var bytes = new FileInfo(archive).Length;
    var assetName = $"RatoObservatorTabajara-{version}-win-x64.zip";
    var download = $"https://github.com/pcarvalho75/RatoObservatorTabajara/releases/download/v{version}/{assetName}";
    var feed = JsonSerializer.Serialize(new[] { new { tag_name = "v" + version, draft = false, prerelease = true, html_url = $"https://github.com/pcarvalho75/RatoObservatorTabajara/releases/tag/v{version}", assets = new[] { new { name = assetName, state = "uploaded", size = bytes, digest = "sha256:" + zipHash, browser_download_url = download } } } });
    var versions = Path.Combine(evidence, "versions");
    using var http = online ? new HttpClient() : new HttpClient(new LocalReleaseHandler(feed, archive, download));
    http.Timeout = TimeSpan.FromMinutes(20);
    var updater = new ReleaseUpdater(http, versions);
    var release = await updater.CheckAsync("0.0.0", true) ?? throw new InvalidDataException("Controlled newer release was not selected.");
    Require(release.Version == version, "Wrong update selected.");
    Require(await updater.CheckAsync(version, true) is null, "Equal version offered as an update.");
    var prepared = await updater.PrepareAsync(release);
    ReleaseUpdater.ValidatePrepared(prepared);
    Require(prepared.Version == version, "Prepared update version mismatch.");
    Require(Path.GetFullPath(prepared.InstallDirectory).StartsWith(versions + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Prepared install escaped isolated root.");
    Require(Hash(prepared.ExecutablePath) == Hash(Path.Combine(package, "desktop", "MouseTrackerTabajara.exe")), "Prepared executable differs from package.");
    var original = Path.Combine(evidence, "original.tabajara");
    ProcessedProject.CreateNew(original);
    var originalHash = Hash(original);
    var snapshot = Path.Combine(evidence, "update-copy.tabajara");
    var snapshotReceipt = ProcessedProject.SaveSnapshot(original, snapshot);
    var start = new ProcessStartInfo(prepared.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var value in new[] { "--study", snapshot, "--diagnostics", Path.Combine(evidence, "new-version-startup.log"), "--smoke-screenshot", Path.Combine(evidence, "new-version.png") }) start.ArgumentList.Add(value);
    start.Environment["DOTNET_HOST_TRACE"] = "1";
    start.Environment["DOTNET_HOST_TRACEFILE"] = Path.Combine(evidence, "new-version-host-trace.txt");
    using var child = Process.Start(start) ?? throw new IOException("Installed WPF apphost was not started.");
    var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    await child.WaitForExitAsync(deadline.Token);
    File.WriteAllText(Path.Combine(evidence, "new-version-stdout.txt"), await stdout);
    File.WriteAllText(Path.Combine(evidence, "new-version-stderr.txt"), await stderr);
    Require(child.ExitCode == 0, "Installed WPF apphost failed.");
    Require(File.Exists(Path.Combine(evidence, "new-version.png")), "Installed WPF screenshot absent.");
    Require(File.ReadAllText(Path.Combine(evidence, "new-version-host-trace.txt")).Contains(Path.Combine(prepared.InstallDirectory, "desktop", "coreclr.dll"), StringComparison.OrdinalIgnoreCase), "Installed WPF did not use its own runtime.");
    var handoffs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RatoObservatorTabajara", "handoffs");
    Directory.CreateDirectory(handoffs);
    var nonce = Guid.NewGuid().ToString("N"); var ready = Path.Combine(handoffs, nonce + ".json");
    var interactive = new ProcessStartInfo(prepared.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var value in new[] { "--study", snapshot, "--update-ready", ready, "--blind-review", "--diagnostics", Path.Combine(evidence, "handoff-startup.log") }) interactive.ArgumentList.Add(value);
    using var updated = Process.Start(interactive) ?? throw new IOException("Interactive installed apphost was not started.");
    try
    {
        using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!File.Exists(ready))
        {
            Require(!updated.HasExited, "Installed apphost exited before readiness.");
            await Task.Delay(100, startupDeadline.Token);
        }
        var signalBytes = File.ReadAllBytes(ready);
        using var signal = JsonDocument.Parse(signalBytes); var value = signal.RootElement;
        Require(value.GetProperty("ProcessId").GetInt32() == updated.Id && value.GetProperty("StartUtcTicks").GetInt64() == updated.StartTime.ToUniversalTime().Ticks, "Readiness process identity differs.");
        Require(value.GetProperty("Nonce").GetString() == nonce && value.GetProperty("Version").GetString() == version && value.GetProperty("Blind").GetBoolean(), "Installed version/blind context did not confirm readiness.");
        Require(string.Equals(value.GetProperty("ProjectPath").GetString(), snapshot, StringComparison.OrdinalIgnoreCase), "Readiness names a different working copy.");
        File.WriteAllBytes(Path.Combine(evidence, "actual-readiness-signal.json"), signalBytes);
        Require(updated.CloseMainWindow(), "Synthetic updated apphost did not accept normal window close.");
        using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await updated.WaitForExitAsync(closeDeadline.Token);
        Require(updated.ExitCode == 0, "Interactive updated apphost failed during normal close.");
    }
    finally
    {
        // Only this exact, newly launched synthetic test child is eligible for cleanup.
        if (!updated.HasExited) { updated.Kill(); await updated.WaitForExitAsync(); }
    }
    Require(Hash(original) == originalHash, "Original synthetic study changed during version switch.");
    Require(ProcessedProject.Receipt(snapshot).Recordings == snapshotReceipt.Recordings, "Snapshot inventory differs after new-version reopening.");
    Require(Hash(archive) == zipHash, "Original Windows archive changed.");
    receipt["state"] = "PASS"; receipt["version"] = version; receipt["windows_zip_sha256"] = zipHash;
    receipt["updater_assembly_sha256"] = Hash(loaded); receipt["prepared"] = prepared;
    receipt["original_study_sha256"] = originalHash; receipt["snapshot_recordings"] = snapshotReceipt.Recordings;
    receipt["installed_actual_wpf_startup"] = "PASS"; receipt["bundle_runtime"] = "SHA256-bound package-local CoreCLR and actual installed apphost host trace";
    receipt["actual_handoff_readiness"] = "PASS process ID/start, nonce, version, working copy and blind context; normal close";
}
catch (Exception error) { receipt["state"] = "FAIL"; receipt["error"] = error.ToString(); throw; }
finally { File.WriteAllText(Path.Combine(evidence, "updater-result.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true })); }

sealed class LocalReleaseHandler(string feed, string archive, string download) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RequestUri?.AbsoluteUri == download)
        {
            var content = new StreamContent(new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read));
            content.Headers.ContentLength = new FileInfo(archive).Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
        if (request.RequestUri?.Host != "api.github.com" || request.RequestUri.AbsolutePath != "/repos/pcarvalho75/RatoObservatorTabajara/releases") throw new InvalidDataException("Unexpected updater network request.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed, Encoding.UTF8, "application/json") });
    }
}
