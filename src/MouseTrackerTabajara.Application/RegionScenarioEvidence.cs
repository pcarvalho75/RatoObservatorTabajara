using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Application;

public sealed record ScenarioImplementationIdentity
{
    public string CodeContract { get; init; } = "";
    public string CoreAssemblyName { get; init; } = "";
    public string CoreAssemblySha256 { get; init; } = "";
    public string CoreModuleVersionId { get; init; } = "";
    public string ApplicationAssemblyName { get; init; } = "";
    public string ApplicationAssemblySha256 { get; init; } = "";
    public string ApplicationModuleVersionId { get; init; } = "";
    public string DesktopAssemblyName { get; init; } = "";
    public string DesktopAssemblySha256 { get; init; } = "";
    public string DesktopModuleVersionId { get; init; } = "";
    public string SourceMaterialSha256 { get; init; } = "";
    public string CanonicalSha256 { get; init; } = "";

    public bool Matches(ScenarioImplementationIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(CodeContract, other.CodeContract, StringComparison.Ordinal)
            && string.Equals(CanonicalSha256, other.CanonicalSha256, StringComparison.Ordinal)
            && string.Equals(CoreAssemblyName, other.CoreAssemblyName, StringComparison.Ordinal)
            && string.Equals(CoreAssemblySha256, other.CoreAssemblySha256, StringComparison.Ordinal)
            && string.Equals(CoreModuleVersionId, other.CoreModuleVersionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ApplicationAssemblyName, other.ApplicationAssemblyName, StringComparison.Ordinal)
            && string.Equals(ApplicationAssemblySha256, other.ApplicationAssemblySha256, StringComparison.Ordinal)
            && string.Equals(ApplicationModuleVersionId, other.ApplicationModuleVersionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(DesktopAssemblyName, other.DesktopAssemblyName, StringComparison.Ordinal)
            && string.Equals(DesktopAssemblySha256, other.DesktopAssemblySha256, StringComparison.Ordinal)
            && string.Equals(DesktopModuleVersionId, other.DesktopModuleVersionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(SourceMaterialSha256, other.SourceMaterialSha256, StringComparison.Ordinal);
    }
}

public static class ScenarioImplementationProbe
{
    public const string SourceResourcePrefix = "scenario-source/";
    public static readonly string[] SourceFiles =
    [
        "src/MouseTrackerTabajara.Application/CalibrationScenarioMath.cs",
        "src/MouseTrackerTabajara.Application/RegionScenarioEvidence.cs",
        "src/MouseTrackerTabajara.Core/RegionScenarioComparison.cs",
        "src/MouseTrackerTabajara.Core/RegionScenarioMath.cs",
        "src/MouseTrackerTabajara.Desktop/MainWindow.RegionScenarios.cs",
        "src/MouseTrackerTabajara.Desktop/RegionScenarioPartial.cs",
        "src/MouseTrackerTabajara.Desktop/RegionScenarioSource.cs",
        "src/MouseTrackerTabajara.Desktop/RegionScenarioWindow.cs"
    ];

    public static ScenarioImplementationIdentity Capture()
    {
        var core = Fingerprint(typeof(RegionScenarioMath).Assembly);
        var application = Fingerprint(typeof(ScenarioImplementationProbe).Assembly);
        var desktopAssembly = FindDesktop();
        var desktop = desktopAssembly is null ? (Name: "", Sha256: "", ModuleVersionId: "") : Fingerprint(desktopAssembly);
        var draft = new ScenarioImplementationIdentity
        {
            CodeContract = RegionScenarioWording.CodeContract,
            CoreAssemblyName = core.Name, CoreAssemblySha256 = core.Sha256, CoreModuleVersionId = core.ModuleVersionId,
            ApplicationAssemblyName = application.Name, ApplicationAssemblySha256 = application.Sha256, ApplicationModuleVersionId = application.ModuleVersionId,
            DesktopAssemblyName = desktop.Name, DesktopAssemblySha256 = desktop.Sha256, DesktopModuleVersionId = desktop.ModuleVersionId,
            SourceMaterialSha256 = Hex(SourceMaterialBytes())
        };
        return draft with { CanonicalSha256 = Canonical(draft) };
    }

    public static string Canonical(ScenarioImplementationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var text = string.Join("\n",
        [
            identity.CodeContract,
            identity.CoreAssemblyName, identity.CoreAssemblySha256, identity.CoreModuleVersionId,
            identity.ApplicationAssemblyName, identity.ApplicationAssemblySha256, identity.ApplicationModuleVersionId,
            identity.DesktopAssemblyName, identity.DesktopAssemblySha256, identity.DesktopModuleVersionId,
            identity.SourceMaterialSha256
        ]);
        return Hex(Encoding.UTF8.GetBytes(text));
    }

    public static string? ReplayHold(ScenarioImplementationIdentity recorded, ScenarioImplementationIdentity? actual = null)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        actual ??= Capture();
        if (!IsHash(recorded.CoreAssemblySha256) || !IsHash(recorded.ApplicationAssemblySha256) || !IsHash(recorded.SourceMaterialSha256) || !IsHash(recorded.CanonicalSha256)
            || (recorded.DesktopAssemblySha256 is { Length: > 0 } desktopSha && !IsHash(desktopSha)))
            return "Implementation identity is missing. The declared code contract does not authenticate the computation.";
        if (!string.Equals(recorded.CanonicalSha256, Canonical(recorded), StringComparison.Ordinal))
            return "Recorded implementation identity is internally inconsistent. Historical recomputation stays held.";
        if (!string.Equals(recorded.CodeContract, actual.CodeContract, StringComparison.Ordinal))
            return "Declared code contract differs from this process. Historical recomputation stays held.";
        if (!recorded.Matches(actual))
            return "Implementation bytes differ while the declared code contract is unchanged. Historical recomputation requires the original implementation and stays held.";
        return null;
    }

    public static string RepositoryRoot()
    {
        foreach (var start in new[]
        {
            Path.GetDirectoryName(typeof(RegionScenarioMath).Assembly.Location),
            Path.GetDirectoryName(typeof(ScenarioImplementationProbe).Assembly.Location),
            AppContext.BaseDirectory
        })
        {
            var dir = string.IsNullOrWhiteSpace(start) ? null : new DirectoryInfo(start);
            for (var depth = 0; dir is not null && depth < 10; depth++, dir = dir.Parent)
            {
                if (SourceFiles.All(relative => File.Exists(SourcePath(dir.FullName, relative)))) return dir.FullName;
            }
        }
        throw new InvalidOperationException("Scenario source checkout was not found. Capture does not read live source files.");
    }

    /// <summary>Preimage of the sources embedded when this assembly was built. Live .cs files are not the compiled implementation.</summary>
    public static byte[] SourceMaterialBytes()
    {
        var assembly = typeof(ScenarioImplementationProbe).Assembly;
        using var buffer = new MemoryStream();
        foreach (var relative in SourceFiles)
        {
            var resource = SourceResourcePrefix + relative;
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
                throw new InvalidOperationException("Executing assembly '" + (assembly.GetName().Name ?? "") + "' does not contain embedded scenario source '" + relative + "'. Live source files were not read.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();
            var header = Encoding.UTF8.GetBytes(relative + "\n" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\n");
            buffer.Write(header);
            buffer.Write(bytes);
            buffer.WriteByte(0x0A);
        }
        return buffer.ToArray();
    }

    private static string SourcePath(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static (string Name, string Sha256, string ModuleVersionId) Fingerprint(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? "";
        var location = assembly.Location;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            throw new InvalidOperationException("Assembly '" + name + "' has no file that can be fingerprinted.");
        var module = assembly.Modules.FirstOrDefault() ?? throw new InvalidOperationException("Assembly '" + name + "' has no module identity.");
        return (name, Hex(File.ReadAllBytes(location)), module.ModuleVersionId.ToString("D"));
    }

    private static Assembly? FindDesktop()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            try
            {
                if (assembly.GetType("MouseTrackerTabajara.Desktop.RegionScenarioWindow", throwOnError: false) is not null) return assembly;
            }
            catch (Exception ex) when (ex is System.Reflection.ReflectionTypeLoadException or FileNotFoundException or FileLoadException or TypeLoadException) { }
        }
        foreach (var directory in new[]
        {
            Path.GetDirectoryName(typeof(ScenarioImplementationProbe).Assembly.Location),
            Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location),
            AppContext.BaseDirectory
        })
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var path = Path.Combine(directory, "MouseTrackerTabajara.dll");
            if (!File.Exists(path)) continue;
            var loaded = Assembly.LoadFrom(path);
            if (loaded.GetType("MouseTrackerTabajara.Desktop.RegionScenarioWindow", throwOnError: false) is not null) return loaded;
        }
        return null;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed record FrozenSourceIdentity
{
    public string VideoId { get; init; } = "";
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Length { get; init; }
    public DateTime WrittenUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string RunId { get; init; } = "";
    public string RunMaterialSha256 { get; init; } = "";
    public string SettingsHash { get; init; } = "";
    public string CorrectionFingerprint { get; init; } = "";
    public int ProtocolRevision { get; init; }
    public string ProtocolFingerprint { get; init; } = "";
}

public sealed record FrozenRegionIdentity
{
    public string SnapshotRevision { get; init; } = "";
    public long PlanRevision { get; init; }
    public string PlanFingerprint { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public int ProfileRevision { get; init; }
    public string CodeContract { get; init; } = RegionScenarioWording.CodeContract;
    public ScenarioImplementationIdentity Implementation { get; init; } = new();
    public ImmutableArray<FrozenSourceIdentity> Sources { get; init; } = [];

    public string? Difference(FrozenRegionIdentity current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var reasons = new List<string>();
        if (!string.Equals(SnapshotRevision, current.SnapshotRevision, StringComparison.Ordinal)) reasons.Add("study snapshot");
        if (PlanRevision != current.PlanRevision || !string.Equals(PlanFingerprint, current.PlanFingerprint, StringComparison.Ordinal)) reasons.Add("plan");
        if (!string.Equals(ProfileId, current.ProfileId, StringComparison.Ordinal) || ProfileRevision != current.ProfileRevision) reasons.Add("calibration profile");
        if (!string.Equals(CodeContract, current.CodeContract, StringComparison.Ordinal) || !string.Equals(Implementation.CodeContract, current.Implementation.CodeContract, StringComparison.Ordinal))
            reasons.Add("code contract");
        if (!Implementation.Matches(current.Implementation)) reasons.Add("implementation identity");
        var frozenSources = Sources.IsDefault ? [] : Sources;
        var currentSources = (current.Sources.IsDefault ? [] : current.Sources).ToDictionary(s => s.VideoId, StringComparer.Ordinal);
        foreach (var source in frozenSources)
        {
            if (!currentSources.TryGetValue(source.VideoId, out var now)) { reasons.Add("source bytes"); continue; }
            if (!string.Equals(source.Sha256, now.Sha256, StringComparison.OrdinalIgnoreCase)
                || source.Length != now.Length || source.WrittenUtc != now.WrittenUtc || source.CreatedUtc != now.CreatedUtc)
                reasons.Add("source bytes");
            if (!string.Equals(source.RunId, now.RunId, StringComparison.Ordinal) || !string.Equals(source.RunMaterialSha256, now.RunMaterialSha256, StringComparison.Ordinal))
                reasons.Add("original run");
            if (!string.Equals(source.SettingsHash, now.SettingsHash, StringComparison.Ordinal)) reasons.Add("settings");
            if (!string.Equals(source.CorrectionFingerprint, now.CorrectionFingerprint, StringComparison.Ordinal)) reasons.Add("corrections");
            if (source.ProtocolRevision != now.ProtocolRevision || !string.Equals(source.ProtocolFingerprint, now.ProtocolFingerprint, StringComparison.Ordinal))
                reasons.Add("protocol window");
        }
        if (frozenSources.Length != currentSources.Count) reasons.Add("recording roster");
        if (reasons.Count == 0) return null;
        return "Frozen region scenario is stale: " + string.Join(", ", reasons.Distinct(StringComparer.Ordinal)) + ". Explicit recomputation is required.";
    }
}

public sealed record FrozenReplayRecording
{
    public string VideoId { get; init; } = "";
    public string VideoJson { get; init; } = "";
    public string MeasuredRunJson { get; init; } = "";
    public ImmutableArray<string> RunHistory { get; init; } = [];
    public ImmutableArray<string> Corrections { get; init; } = [];
    public ImmutableArray<string> Protocols { get; init; } = [];
    public ImmutableArray<string> Events { get; init; } = [];
    public ImmutableArray<string> Reviews { get; init; } = [];
}

/// <summary>Frozen inputs copied into evidence so a later edit of the live study cannot erase the calculated trial.</summary>
public sealed record FrozenReplayMaterial
{
    public int SchemaVersion { get; init; } = 1;
    public string PlanJson { get; init; } = "";
    public long PlanRevision { get; init; }
    public string PlanFingerprint { get; init; } = "";
    public ImmutableArray<string> PlanHistory { get; init; } = [];
    public string ProfileJson { get; init; } = "";
    public string ProfileSha256 { get; init; } = "";
    public ImmutableArray<FrozenReplayRecording> Recordings { get; init; } = [];
}

public sealed record RegionScenarioEvidenceDocument
{
    public const int CurrentSchema = 2;
    public int SchemaVersion { get; init; } = CurrentSchema;
    public string CodeContract { get; init; } = RegionScenarioWording.CodeContract;
    public FrozenRegionIdentity Identity { get; init; } = new();
    public string FrozenMaterialJson { get; init; } = "";
    public string FrozenMaterialSha256 { get; init; } = "";
    public string ComputationJson { get; init; } = "";
    public string ComputationSha256 { get; init; } = "";
}

public static class RegionScenarioEvidenceStore
{
    public const string FolderName = "region-scenario-evidence";

    public static string Append(string studyDirectory, RegionScenarioEvidenceDocument document)
    {
        Authenticate(document);
        var root = Path.GetFullPath(studyDirectory);
        var folder = Path.GetFullPath(Path.Combine(root, FolderName));
        if (!folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Scenario evidence must stay inside the study directory.");
        Directory.CreateDirectory(folder);
        var payload = JsonDefaults.Serialize(document);
        for (var revision = 1; revision < 100000; revision++)
        {
            var fullPath = Path.Combine(folder, "region-scenario-" + revision.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".json");
            if (File.Exists(fullPath)) continue;
            var temporary = fullPath + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    stream.Write(bytes); stream.Flush(true);
                }
                File.Move(temporary, fullPath, overwrite: false);
                return fullPath;
            }
            catch (IOException) when (File.Exists(fullPath)) { }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        throw new InvalidOperationException("Scenario evidence revision space is exhausted.");
    }

    public static void Authenticate(RegionScenarioEvidenceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != RegionScenarioEvidenceDocument.CurrentSchema)
            throw new InvalidDataException("Scenario evidence schema is not the schema 2 document.");
        if (document.CodeContract != RegionScenarioWording.CodeContract
            || string.IsNullOrWhiteSpace(document.ComputationJson) || string.IsNullOrWhiteSpace(document.FrozenMaterialJson))
            throw new ArgumentException("Scenario evidence is incomplete.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.ComputationJson))).ToLowerInvariant();
        if (!string.Equals(hash, document.ComputationSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Scenario evidence hash does not match its computation.");
        var materialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.FrozenMaterialJson))).ToLowerInvariant();
        if (!string.Equals(materialHash, document.FrozenMaterialSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Scenario evidence hash does not match its frozen replay material.");
        if (!string.Equals(document.CodeContract, document.Identity.CodeContract, StringComparison.Ordinal)
            || !string.Equals(document.CodeContract, document.Identity.Implementation.CodeContract, StringComparison.Ordinal))
            throw new InvalidDataException("Evidence code contract does not match its implementation identity.");
        var hold = ScenarioImplementationProbe.ReplayHold(document.Identity.Implementation);
        if (hold is not null) throw new InvalidDataException(hold);
    }
}
