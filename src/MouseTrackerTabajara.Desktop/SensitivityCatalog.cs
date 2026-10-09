using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using MouseTrackerTabajara.Cli;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Desktop;

public sealed class SensitivityScenarioDraft
{
    public string Name { get; set; } = "";
    public string Rationale { get; set; } = "";
    public int? Threshold { get; set; }
    public bool? Invert { get; set; }
    public int? Blur { get; set; }
    public double? MinArea { get; set; }
    public double? MaxArea { get; set; }
    public int? MaxExtension { get; set; }
    public double? MaxJump { get; set; }
}

public sealed class SensitivityRecordingRow
{
    public required string RecordingId { get; init; }
    public required int Ordinal { get; init; }
    public required string Availability { get; init; }
    public required string Reason { get; init; }
    public string Filename { get; init; } = "";
    public string AnimalId { get; init; } = "";
    public string GroupName { get; init; } = "";
    public VideoAsset? Asset { get; init; }
    public string? BaselineJson { get; init; }
    public AnalysisRun? Baseline { get; init; }
}

public sealed class SensitivityFrozenInventory
{
    public int Ordinal { get; set; }
    public string RecordingId { get; set; } = "";
    public string Availability { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Filename { get; set; } = "";
    public string AnimalId { get; set; } = "";
    public string GroupName { get; set; } = "";
}

public sealed class SensitivityCaseRow
{
    public required int PlanIndex { get; init; }
    public required string RecordingId { get; init; }
    public required string ScenarioName { get; init; }
    public required int RecordingOrdinal { get; init; }
    public required int ScenarioOrdinal { get; init; }
    public required string Status { get; init; }
    public string? ErrorMessage { get; init; }
    public double? CoveragePercent { get; init; }
    public double? UnknownSeconds { get; init; }
    public string? EvidenceRelative { get; init; }
    public IReadOnlyList<EndpointDifference> Differences { get; init; } = [];
}

public sealed class SensitivityFreezeDocument
{
    public string Schema { get; set; } = SensitivityCatalog.FreezeSchema;
    public string SnapshotRevision { get; set; } = "";
    public string PlanSha256 { get; set; } = "";
    public string CodeIdentitySha256 { get; set; } = "";
    public string InventorySha256 { get; set; } = "";
    public ReplayCodeIdentity CodeIdentity { get; set; } = new();
    public List<SensitivityScenarioDraft> Scenarios { get; set; } = [];
    public List<SensitivityFrozenRecording> Recordings { get; set; } = [];
    public List<string> ClaimLimits { get; set; } = [];
}

public sealed class SensitivityFrozenRecording
{
    public string RecordingId { get; set; } = "";
    public int Ordinal { get; set; }
    public string SourceSha256 { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public string SettingsSha256 { get; set; } = "";
    public string BaselineFile { get; set; } = "";
    public string VideoPath { get; set; } = "";
}

public readonly record struct SensitivitySourceStamp(long Length, DateTime WrittenUtc, DateTime CreatedUtc);

public sealed record SensitivityFreezeResult(SensitivityFreezeDocument Document, IReadOnlyDictionary<string, SensitivitySourceStamp> SourceStamps);

/// <summary>
/// Builds the called replay plan from frozen study material.
/// Detector drafts cannot change geometry, the decoder interval, or units.
/// </summary>
public static class SensitivityCatalog
{
    public const string FreezeSchema = "tabajara-desktop-sensitivity-freeze-v1";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static IReadOnlyList<SensitivityScenarioDraft> OriginalFamilies() =>
    [
        Family("original-detector", "Keep each recording's own detector settings as the prespecified reference."),
        Family("threshold-zero", "Prespecified threshold 0. Low coverage or a noisy mask stays in the plan.", threshold: 0),
        Family("polarity-off", "Prespecified polarity off. An empty or unfavorable detection stays in the plan.", invert: false),
        Family("blur-3", "Prespecified blur 3, with geometry and the decoder interval left on the baseline.", blur: 3),
        Family("area-tight", "Prespecified tight area band. A loss of accepted points stays in the plan.", minArea: 4000, maxArea: 5000),
        Family("extent-1", "Prespecified extent 1. Rejected contours stay visible as unknown time.", maxExtension: 1),
        Family("jump-1", "Prespecified jump limit 1. Gaps and jump status stay on the case.", maxJump: 1)
    ];

    public static string WorkspaceFor(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "sensitivity-workspace");

    public static string OpaqueRecording(int ordinal) => string.Create(CultureInfo.InvariantCulture, $"Recording {ordinal:000}");
    public static string OpaqueScenario(int ordinal) => string.Create(CultureInfo.InvariantCulture, $"Scenario {ordinal:000}");

    public static IReadOnlyList<SensitivityRecordingRow> ReadRecordings(AtlasSnapshot snapshot)
    {
        var rows = new List<SensitivityRecordingRow>(snapshot.Recordings.Length);
        for (var index = 0; index < snapshot.Recordings.Length; index++)
        {
            var recording = snapshot.Recordings[index];
            VideoAsset asset;
            try { asset = recording.Asset; }
            catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
            {
                rows.Add(new SensitivityRecordingRow { RecordingId = "unreadable-" + (index + 1).ToString(CultureInfo.InvariantCulture), Ordinal = index + 1, Availability = "invalid", Reason = "Imported recording material could not be read." });
                continue;
            }
            var baselineJson = snapshot.Rows("runs").LastOrDefault(row => row.Value("video_id") == asset.Id)?.Value("json");
            rows.Add(Classify(index + 1, asset, baselineJson));
        }
        return rows;
    }

    public static void ValidateDrafts(IReadOnlyList<SensitivityScenarioDraft> drafts)
    {
        if (drafts.Count == 0) throw new InvalidDataException("Name at least one detector scenario and record its rationale before execution.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            if (!IsSafeToken(draft.Name)) throw new InvalidDataException($"Scenario name '{draft.Name}' is empty or unsafe as an output name.");
            if (!names.Add(draft.Name)) throw new InvalidDataException($"Duplicate scenario name '{draft.Name}'.");
            var rationale = draft.Rationale?.Trim() ?? "";
            if (rationale.Length is 0 or > 400 || rationale.Any(char.IsControl))
                throw new InvalidDataException($"Scenario '{draft.Name}' needs a single-line rationale before execution.");
            draft.Rationale = rationale;
            if (draft.Threshold is < 0 or > 255) throw new InvalidDataException($"Scenario '{draft.Name}' threshold is outside 0..255.");
            if (draft.Blur is < 0 or > 31) throw new InvalidDataException($"Scenario '{draft.Name}' blur is outside 0..31.");
            if (draft.MaxExtension is < 1 or > 100000) throw new InvalidDataException($"Scenario '{draft.Name}' extent is outside 1..100000.");
            if (draft.MinArea is { } min && (double.IsNaN(min) || min < 1 || min > 1e7)) throw new InvalidDataException($"Scenario '{draft.Name}' has a malformed min area.");
            if (draft.MaxArea is { } max && (double.IsNaN(max) || max < 1 || max > 1e7)) throw new InvalidDataException($"Scenario '{draft.Name}' has a malformed max area.");
            if (draft.MaxJump is { } jump && (double.IsNaN(jump) || jump < 1 || jump > 100000)) throw new InvalidDataException($"Scenario '{draft.Name}' has a malformed jump.");
        }
    }

    public static SensitivityFreezePublication Freeze(AtlasSnapshot snapshot, IReadOnlyList<SensitivityScenarioDraft> drafts, string workspace, IReplayTracker tracker, CancellationToken cancellation = default, Func<bool>? canPublish = null)
    {
        ValidateDrafts(drafts);
        RejectIfSuperseded(cancellation, canPublish);
        if (!snapshot.IsCurrent()) throw new InvalidDataException("The study snapshot changed before the plan was frozen.");
        var recordings = ReadRecordings(snapshot);
        var inventory = recordings.Select(ToInventory).ToList();
        var eligible = recordings.Where(row => row.Availability == "eligible").ToList();
        if (eligible.Count == 0) throw new InvalidDataException("No imported recording has a complete automatic baseline. Missing, invalid, and excluded rows stay listed and were not run.");
        var lease = SensitivityFreezeLease.Acquire(workspace);
        var createdStaging = false;
        try
        {
            var root = lease.Workspace;
            RejectReplayPublication(root);
            RecoverInterruptedFreeze(lease);
            if (Directory.Exists(Path.Combine(root, "freeze")) && Directory.Exists(Path.Combine(root, "freeze.previous")))
                throw new InvalidDataException(SensitivityFreezeLease.AmbiguousMessage);
            var stamps = CaptureSourceStamps(eligible);
            var prepared = new List<(SensitivityRecordingRow Row, byte[] Bytes, string SourceHash, VideoMetadata Metadata)>();
            foreach (var row in eligible)
            {
                RejectIfSuperseded(cancellation, canPublish);
                var asset = row.Asset ?? throw new InvalidDataException($"Recording '{row.RecordingId}' lost its imported identity.");
                var sourceHash = tracker.HashFile(asset.Path);
                if (!string.Equals(sourceHash, asset.SourceSha256, StringComparison.Ordinal) || !string.Equals(sourceHash, row.Baseline!.SourceSha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Recording '{row.RecordingId}' source hash changed. The video was not modified and the case was not planned.");
                VideoMetadata probed;
                try { probed = tracker.Probe(asset.Path); }
                catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException or IOException)
                { throw new InvalidDataException($"Recording '{row.RecordingId}' could not be probed: {exception.Message}"); }
                if (!CompletedRunContract.SameMetadata(probed, asset.Metadata) || !CompletedRunContract.SameMetadata(probed, row.Baseline.Metadata))
                    throw new InvalidDataException($"Recording '{row.RecordingId}' probed metadata does not match the frozen baseline.");
                foreach (var draft in drafts)
                {
                    try { ReplayPlanLoader.Apply(row.Baseline.Settings, ToScenario(draft)); }
                    catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
                    { throw new InvalidDataException($"Scenario '{draft.Name}' is not valid for recording '{row.RecordingId}': {exception.Message}"); }
                }
                prepared.Add((row, Utf8.GetBytes(row.BaselineJson!), sourceHash, probed));
            }
            EnsureStillCurrent(snapshot, stamps, prepared.Select(item => (item.Row.RecordingId, Path.GetFullPath(item.Row.Asset!.Path), item.SourceHash)), tracker, cancellation, canPublish);
            var identity = contextIdentity(tracker);
            var staging = PrepareStaging(lease);
            createdStaging = true;
            var baselineDirectory = Path.Combine(staging, "baselines");
            var frozenRecordings = new List<SensitivityFrozenRecording>();
            foreach (var item in prepared)
            {
                RejectIfSuperseded(cancellation, canPublish);
                var baselineFile = item.Row.RecordingId + ".json";
                var baselinePath = Path.Combine(baselineDirectory, baselineFile);
                File.WriteAllBytes(baselinePath, item.Bytes);
                var baselineHash = tracker.HashFile(baselinePath);
                frozenRecordings.Add(new SensitivityFrozenRecording
                {
                    RecordingId = item.Row.RecordingId,
                    Ordinal = item.Row.Ordinal,
                    SourceSha256 = item.SourceHash,
                    BaselineSha256 = baselineHash,
                    SettingsSha256 = StudyStore.SettingsHash(item.Row.Baseline!.Settings),
                    BaselineFile = "baselines/" + baselineFile,
                    VideoPath = item.Row.Asset!.Path
                });
            }
            var planPath = Path.Combine(staging, "plan.json");
            File.WriteAllBytes(planPath, PlanBytes(frozenRecordings, drafts, prepared.Select(item => item.Row).ToList()));
            var inventoryBytes = Utf8.GetBytes(JsonDefaults.Serialize(inventory));
            File.WriteAllBytes(Path.Combine(staging, "inventory.json"), inventoryBytes);
            ReplayPlanLoader.Load(planPath, tracker);
            var document = new SensitivityFreezeDocument
            {
                SnapshotRevision = snapshot.Revision,
                PlanSha256 = ReplayHashes.Sha256File(planPath),
                InventorySha256 = ReplayHashes.Sha256Hex(inventoryBytes),
                CodeIdentity = identity,
                CodeIdentitySha256 = ReplayCodeIdentityProbe.CanonicalSha256(identity),
                Scenarios = drafts.Select(CopyDraft).ToList(),
                Recordings = frozenRecordings,
                ClaimLimits = ReplayClaims.All.ToList()
            };
            File.WriteAllText(Path.Combine(staging, "sensitivity-freeze.json"), JsonDefaults.Serialize(document), Utf8);
            var result = new SensitivityFreezeResult(document, stamps);
            EnsureStillPublishable(snapshot, result, tracker, cancellation, canPublish);
            RejectReplayPublication(root);
            PublishStagedFreeze(lease);
            createdStaging = false;
            return new SensitivityFreezePublication(lease, result);
        }
        catch (Exception exception)
        {
            if (createdStaging) DiscardOwnedStaging(lease);
            lease.Dispose();
            if (exception is PlanRejectedException rejected)
                throw new InvalidDataException(rejected.Message, rejected);
            throw;
        }
    }

    public static void EnsureStillPublishable(AtlasSnapshot snapshot, SensitivityFreezeResult result, IReplayTracker tracker, CancellationToken cancellation = default, Func<bool>? canPublish = null)
    {
        var prepared = result.Document.Recordings.Select(recording => (recording.RecordingId, recording.VideoPath, recording.SourceSha256)).ToList();
        EnsureStillCurrent(snapshot, result.SourceStamps, prepared, tracker, cancellation, canPublish);
        if (!string.Equals(result.Document.SnapshotRevision, snapshot.Revision, StringComparison.Ordinal))
            throw new InvalidDataException("The study snapshot changed while the plan was being frozen. No new freeze was published.");
    }

    internal static void AcknowledgeOwned(SensitivityFreezeLease lease)
    {
        lease.RequireWorkspace(lease.Workspace);
        DeleteNamedIfPresent(lease, "freeze.previous");
        DeleteNamedIfPresent(lease, "freeze.was-new");
        DeleteNamedIfPresent(lease, "freeze-staging");
    }

    internal static void RevertOwned(SensitivityFreezeLease lease)
    {
        var workspace = lease.Workspace;
        lease.RequireWorkspace(workspace);
        var live = Path.Combine(workspace, "freeze");
        var backup = Path.Combine(workspace, "freeze.previous");
        var marker = Path.Combine(workspace, "freeze.was-new");
        if (Directory.Exists(backup))
        {
            if (Directory.Exists(live) || File.Exists(live)) DeleteNamedIfPresent(lease, "freeze");
            MoveNamed(lease, "freeze.previous", "freeze");
            DeleteNamedIfPresent(lease, "freeze.was-new");
        }
        else if (File.Exists(marker) || Directory.Exists(marker))
        {
            if (Directory.Exists(live) || File.Exists(live)) DeleteNamedIfPresent(lease, "freeze");
            DeleteNamedIfPresent(lease, "freeze.was-new");
        }
        DiscardOwnedStaging(lease);
    }

    internal static void LeavePublicationInterrupted(SensitivityFreezeLease lease)
    {
        var workspace = lease.Workspace;
        lease.RequireWorkspace(workspace);
        if (!Directory.Exists(Path.Combine(workspace, "freeze")))
            throw new InvalidDataException("No freeze publication is present to interrupt.");
        if (Directory.Exists(Path.Combine(workspace, "freeze.previous")) || File.Exists(Path.Combine(workspace, "freeze.previous")))
            throw new InvalidDataException("A previous freeze is already parked and was left unchanged.");
        MoveNamed(lease, "freeze", "freeze.previous");
    }

    public static SensitivityFreezeDocument? TryLoad(string workspace)
    {
        var path = Path.Combine(workspace, "freeze", "sensitivity-freeze.json");
        if (!File.Exists(path)) return null;
        try { return JsonDefaults.Deserialize<SensitivityFreezeDocument>(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
        { return null; }
    }

    public static bool SamePlanBytes(string workspace, SensitivityFreezeDocument freeze)
    {
        var path = Path.Combine(workspace, "freeze", "plan.json");
        return File.Exists(path) && string.Equals(ReplayHashes.Sha256File(path), freeze.PlanSha256, StringComparison.Ordinal);
    }

    public static IReadOnlyList<SensitivityFrozenInventory>? ReadInventory(string workspace, SensitivityFreezeDocument freeze)
    {
        if (freeze.InventorySha256.Length != 64) return null;
        var path = Path.Combine(workspace, "freeze", "inventory.json");
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (!string.Equals(ReplayHashes.Sha256Hex(bytes), freeze.InventorySha256, StringComparison.Ordinal)) return null;
        try
        {
            var rows = JsonDefaults.Deserialize<List<SensitivityFrozenInventory>>(Utf8.GetString(bytes));
            if (rows is null || rows.Count == 0 || rows.Any(row => string.IsNullOrWhiteSpace(row.RecordingId) || string.IsNullOrWhiteSpace(row.Availability))) return null;
            if (rows.Select(row => row.RecordingId).Distinct(StringComparer.Ordinal).Count() != rows.Count) return null;
            return rows;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
        { return null; }
    }

    public static IReadOnlyList<SensitivityCaseRow> Cases(SensitivityFreezeDocument? freeze, ReplayManifestDocument? manifest, IReadOnlyList<SensitivityRecordingRow> recordings)
    {
        var recordingOrdinal = recordings.ToDictionary(row => row.RecordingId, row => row.Ordinal, StringComparer.Ordinal);
        var scenarioOrdinal = new Dictionary<string, int>(StringComparer.Ordinal);
        if (freeze is not null)
            for (var index = 0; index < freeze.Scenarios.Count; index++) scenarioOrdinal[freeze.Scenarios[index].Name] = index + 1;
        var rows = new List<SensitivityCaseRow>();
        if (manifest is not null)
        {
            rows.AddRange(manifest.Cases.Select(item =>
            {
                var attempt = item.Attempts.LastOrDefault();
                return new SensitivityCaseRow
                {
                    PlanIndex = item.PlanIndex,
                    RecordingId = item.RecordingId,
                    ScenarioName = item.ScenarioName,
                    RecordingOrdinal = recordingOrdinal.GetValueOrDefault(item.RecordingId),
                    ScenarioOrdinal = scenarioOrdinal.GetValueOrDefault(item.ScenarioName),
                    Status = string.IsNullOrWhiteSpace(item.Status) ? ReplayStatuses.Unrun : item.Status,
                    ErrorMessage = item.ErrorMessage,
                    CoveragePercent = item.ScenarioMetrics?.CoveragePercent,
                    UnknownSeconds = item.ScenarioMetrics?.UnknownSeconds,
                    EvidenceRelative = attempt?.RelativeDirectory,
                    Differences = (item.EndpointDifferences ?? []).Where(difference => !CompletedRunContract.WithheldBiologicalEndpoints.Contains(difference.Name, StringComparer.Ordinal)).ToList()
                };
            }).OrderBy(row => row.PlanIndex));
        }
        else if (freeze is not null)
        {
            var planIndex = 0;
            foreach (var recording in freeze.Recordings.OrderBy(row => row.Ordinal))
                foreach (var scenario in freeze.Scenarios)
                    rows.Add(new SensitivityCaseRow
                    {
                        PlanIndex = planIndex++,
                        RecordingId = recording.RecordingId,
                        ScenarioName = scenario.Name,
                        RecordingOrdinal = recording.Ordinal,
                        ScenarioOrdinal = scenarioOrdinal.GetValueOrDefault(scenario.Name),
                        Status = ReplayStatuses.Unrun
                    });
        }
        if (freeze is null) return rows;
        var next = rows.Count == 0 ? 0 : rows.Max(row => row.PlanIndex) + 1;
        var known = new HashSet<string>(rows.Select(row => row.RecordingId + "\n" + row.ScenarioName), StringComparer.Ordinal);
        foreach (var recording in recordings.Where(row => row.Availability != "eligible").OrderBy(row => row.Ordinal))
            foreach (var scenario in freeze.Scenarios)
            {
                if (!known.Add(recording.RecordingId + "\n" + scenario.Name)) continue;
                rows.Add(new SensitivityCaseRow
                {
                    PlanIndex = next++,
                    RecordingId = recording.RecordingId,
                    ScenarioName = scenario.Name,
                    RecordingOrdinal = recording.Ordinal,
                    ScenarioOrdinal = scenarioOrdinal.GetValueOrDefault(scenario.Name),
                    Status = recording.Availability,
                    ErrorMessage = recording.Reason
                });
            }
        return rows.OrderBy(row => row.PlanIndex).ToList();
    }

    public static string CaseLabel(SensitivityCaseRow row, bool blinded) =>
        blinded
            ? $"{OpaqueRecording(row.RecordingOrdinal)} · {OpaqueScenario(row.ScenarioOrdinal)} · {row.Status}"
            : $"{row.RecordingId} · {row.ScenarioName} · {row.Status}";

    public static ReplayManifestDocument? ReadManifest(string workspace)
    {
        var path = Path.Combine(workspace, "replay", "replay-manifest.json");
        if (!File.Exists(path)) return null;
        try { return JsonDefaults.Deserialize<ReplayManifestDocument>(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException or FormatException)
        { return null; }
    }

    public static bool CodeMatches(SensitivityFreezeDocument freeze, ReplayManifestDocument? manifest, IReplayTracker tracker)
    {
        try
        {
            var actual = contextIdentity(tracker);
            ReplayCodeIdentityProbe.RequireSame(freeze.CodeIdentity, actual);
            if (manifest is not null) ReplayCodeIdentityProbe.RequireSame(manifest.CodeIdentity, actual);
            return true;
        }
        catch (PlanRejectedException)
        { return false; }
    }

    private static ReplayCodeIdentity contextIdentity(IReplayTracker tracker) =>
        ReplayCodeIdentityProbe.Capture(tracker.NativeVersion);

    private static SensitivityFrozenInventory ToInventory(SensitivityRecordingRow row) => new()
    {
        Ordinal = row.Ordinal,
        RecordingId = row.RecordingId,
        Availability = row.Availability,
        Reason = row.Reason,
        Filename = row.Asset?.Filename ?? "",
        AnimalId = row.Asset?.AnimalId ?? "",
        GroupName = row.Asset?.GroupName ?? ""
    };

    private static readonly HashSet<string> OwnedTargets = new(StringComparer.Ordinal)
    {
        "freeze", "freeze-staging", "freeze.previous", "freeze.was-new"
    };

    private static void RejectReplayPublication(string workspace)
    {
        var output = Path.Combine(workspace, "replay");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("Replay output already holds a frozen run. It was left unchanged.");
    }

    private static void RecoverInterruptedFreeze(SensitivityFreezeLease lease)
    {
        var workspace = lease.Workspace;
        lease.RequireWorkspace(workspace);
        var live = Path.Combine(workspace, "freeze");
        var backup = Path.Combine(workspace, "freeze.previous");
        var marker = Path.Combine(workspace, "freeze.was-new");
        if (!Directory.Exists(live) && Directory.Exists(backup)) MoveNamed(lease, "freeze.previous", "freeze");
        if (Directory.Exists(live) && !Directory.Exists(backup) && (File.Exists(marker) || Directory.Exists(marker)))
            DeleteNamedIfPresent(lease, "freeze.was-new");
    }

    private static string PrepareStaging(SensitivityFreezeLease lease)
    {
        var workspace = lease.Workspace;
        lease.RequireWorkspace(workspace);
        var staging = Path.Combine(workspace, "freeze-staging");
        if (File.Exists(staging) || Directory.Exists(staging)) DeleteNamedIfPresent(lease, "freeze-staging");
        else NamedTarget(workspace, "freeze-staging");
        var baselines = Path.Combine(staging, "baselines");
        Directory.CreateDirectory(baselines);
        if (ReplayOutputGuard.IsReparse(staging) || ReplayOutputGuard.IsReparse(baselines))
            throw new InvalidDataException("Freeze staging is a reparse alias and was left unchanged.");
        return staging;
    }

    private static void DiscardOwnedStaging(SensitivityFreezeLease lease)
    {
        try
        {
            if (!lease.IsHeld) return;
            var staging = Path.Combine(lease.Workspace, "freeze-staging");
            if (!File.Exists(staging) && !Directory.Exists(staging)) return;
            DeleteNamedIfPresent(lease, "freeze-staging");
        }
        catch (Exception)
        {
            // A refused target stays on disk. This attempt does not delete it by name.
        }
    }

    private static void DeleteNamedIfPresent(SensitivityFreezeLease lease, string name)
    {
        var workspace = lease.Workspace;
        lease.RequireWorkspace(workspace);
        var candidate = Path.Combine(workspace, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return;
        NamedTarget(workspace, name);
        ReplayOutputGuard.DeleteContained(candidate, workspace, name);
    }

    private static void MoveNamed(SensitivityFreezeLease lease, string fromName, string toName)
    {
        var workspace = lease.Workspace;
        lease.RequireWorkspace(workspace);
        var from = NamedTarget(workspace, fromName);
        var to = NamedTarget(workspace, toName);
        ReplayOutputGuard.MoveContainedDirectory(from, workspace, to, workspace);
    }

    private static void WriteNewMarker(SensitivityFreezeLease lease)
    {
        var workspace = lease.Workspace;
        lease.RequireWorkspace(workspace);
        var marker = NamedTarget(workspace, "freeze.was-new");
        if (File.Exists(marker) || Directory.Exists(marker))
            throw new InvalidDataException("The new-freeze marker is already present and was left unchanged.");
        File.WriteAllText(marker, "new");
    }

    private static string NamedTarget(string workspace, string name)
    {
        if (!OwnedTargets.Contains(name) || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            throw new InvalidDataException($"'{name}' is not a freeze ownership target. Nothing was deleted.");
        return ReplayOutputGuard.RequireCleanupTarget(Path.Combine(workspace, name), workspace, name);
    }

    private static Dictionary<string, SensitivitySourceStamp> CaptureSourceStamps(IReadOnlyList<SensitivityRecordingRow> eligible)
    {
        var stamps = new Dictionary<string, SensitivitySourceStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in eligible)
        {
            var path = Path.GetFullPath(row.Asset?.Path ?? throw new InvalidDataException($"Recording '{row.RecordingId}' lost its imported identity."));
            var info = new FileInfo(path);
            if (!info.Exists) throw new InvalidDataException($"Recording '{row.RecordingId}' source is missing. No new freeze was published.");
            stamps[info.FullName] = new SensitivitySourceStamp(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }
        return stamps;
    }

    private static void EnsureStillCurrent(AtlasSnapshot snapshot, IReadOnlyDictionary<string, SensitivitySourceStamp> stamps, IEnumerable<(string RecordingId, string Path, string SourceHash)> sources, IReplayTracker tracker, CancellationToken cancellation, Func<bool>? canPublish)
    {
        RejectIfSuperseded(cancellation, canPublish);
        var current = AtlasSnapshot.Read(snapshot.DatabasePath);
        if (!current.IsCurrent() || !string.Equals(current.Revision, snapshot.Revision, StringComparison.Ordinal))
            throw new InvalidDataException("The study snapshot changed while the plan was being frozen. No new freeze was published.");
        EnsureStamps(stamps);
        foreach (var source in sources)
        {
            RejectIfSuperseded(cancellation, canPublish);
            var hash = tracker.HashFile(source.Path);
            if (!string.Equals(hash, source.SourceHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Recording '{source.RecordingId}' changed while the plan was being frozen. No new freeze was published.");
        }
        EnsureStamps(stamps);
        current = AtlasSnapshot.Read(snapshot.DatabasePath);
        if (!current.IsCurrent() || !string.Equals(current.Revision, snapshot.Revision, StringComparison.Ordinal))
            throw new InvalidDataException("The study snapshot changed while the plan was being frozen. No new freeze was published.");
        RejectIfSuperseded(cancellation, canPublish);
    }

    private static void EnsureStamps(IReadOnlyDictionary<string, SensitivitySourceStamp> stamps)
    {
        foreach (var (path, stamp) in stamps)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != stamp.Length || info.LastWriteTimeUtc != stamp.WrittenUtc || info.CreationTimeUtc != stamp.CreatedUtc)
                throw new InvalidDataException("A source changed while the plan was being frozen. No new freeze was published.");
        }
    }

    private static void RejectIfSuperseded(CancellationToken cancellation, Func<bool>? canPublish)
    {
        cancellation.ThrowIfCancellationRequested();
        if (canPublish?.Invoke() == false) throw new OperationCanceledException();
    }

    private static void PublishStagedFreeze(SensitivityFreezeLease lease)
    {
        var workspace = lease.Workspace;
        lease.RequireWorkspace(workspace);
        var live = Path.Combine(workspace, "freeze");
        var backup = Path.Combine(workspace, "freeze.previous");
        if (Directory.Exists(live) && Directory.Exists(backup))
            throw new InvalidDataException(SensitivityFreezeLease.AmbiguousMessage);
        DeleteNamedIfPresent(lease, "freeze.was-new");
        DeleteNamedIfPresent(lease, "freeze.previous");
        if (Directory.Exists(live)) MoveNamed(lease, "freeze", "freeze.previous");
        else WriteNewMarker(lease);
        try { MoveNamed(lease, "freeze-staging", "freeze"); }
        catch
        {
            if (!Directory.Exists(live) && Directory.Exists(backup)) MoveNamed(lease, "freeze.previous", "freeze");
            try { DeleteNamedIfPresent(lease, "freeze.was-new"); }
            catch (Exception) { }
            throw;
        }
    }

    private static SensitivityRecordingRow Classify(int ordinal, VideoAsset asset, string? baselineJson)
    {
        if (!asset.Included)
            return new SensitivityRecordingRow { RecordingId = asset.Id, Ordinal = ordinal, Availability = "excluded", Reason = string.IsNullOrWhiteSpace(asset.ExclusionReason) ? "Excluded recording." : asset.ExclusionReason, Asset = asset, BaselineJson = baselineJson };
        if (!IsSafeToken(asset.Id))
            return new SensitivityRecordingRow { RecordingId = asset.Id, Ordinal = ordinal, Availability = "invalid", Reason = "Recording id is not a safe plan token.", Asset = asset, BaselineJson = baselineJson };
        if (baselineJson is null)
            return new SensitivityRecordingRow { RecordingId = asset.Id, Ordinal = ordinal, Availability = "missing", Reason = "No automatic baseline run is stored for this recording.", Asset = asset };
        AnalysisRun baseline;
        try { baseline = JsonDefaults.Deserialize<AnalysisRun>(baselineJson); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
        { return new SensitivityRecordingRow { RecordingId = asset.Id, Ordinal = ordinal, Availability = "invalid", Reason = "Baseline JSON could not be read.", Asset = asset, BaselineJson = baselineJson }; }
        if (!File.Exists(asset.Path))
            return new SensitivityRecordingRow { RecordingId = asset.Id, Ordinal = ordinal, Availability = "invalid", Reason = "Source file is missing.", Asset = asset, BaselineJson = baselineJson, Baseline = baseline };
        if (asset.Day < 1 || asset.Trial < 1)
            return new SensitivityRecordingRow { RecordingId = asset.Id, Ordinal = ordinal, Availability = "invalid", Reason = "Day or trial is not a positive provenance value.", Asset = asset, BaselineJson = baselineJson, Baseline = baseline };
        try
        {
            CompletedRunContract.Ensure(baseline, asset.Metadata, baseline.Settings, asset.SourceSha256, AlgorithmIdentity.Contract, CompletedRunContract.SoftwareVersion);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        { return new SensitivityRecordingRow { RecordingId = asset.Id, Ordinal = ordinal, Availability = "invalid", Reason = exception.Message, Asset = asset, BaselineJson = baselineJson, Baseline = baseline }; }
        return new SensitivityRecordingRow { RecordingId = asset.Id, Ordinal = ordinal, Availability = "eligible", Reason = "Complete automatic baseline.", Asset = asset, BaselineJson = baselineJson, Baseline = baseline };
    }

    private static byte[] PlanBytes(IReadOnlyList<SensitivityFrozenRecording> recordings, IReadOnlyList<SensitivityScenarioDraft> drafts, IReadOnlyList<SensitivityRecordingRow> rows)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", ReplayClaims.PlanSchema);
            writer.WriteString("algorithm_contract", AlgorithmIdentity.Contract);
            writer.WriteStartArray("recordings");
            foreach (var recording in recordings)
            {
                var row = rows.Single(item => item.RecordingId == recording.RecordingId);
                var asset = row.Asset!;
                writer.WriteStartObject();
                writer.WriteString("recording_id", recording.RecordingId);
                writer.WriteString("video_path", recording.VideoPath);
                writer.WriteString("expected_source_sha256", recording.SourceSha256);
                writer.WriteStartObject("expected_metadata");
                writer.WriteNumber("width", asset.Metadata.Width);
                writer.WriteNumber("height", asset.Metadata.Height);
                writer.WriteNumber("fps", asset.Metadata.Fps);
                writer.WriteNumber("frame_count", asset.Metadata.FrameCount);
                writer.WriteEndObject();
                writer.WriteString("baseline_analysis_path", recording.BaselineFile);
                writer.WriteString("baseline_sha256", recording.BaselineSha256);
                if (!string.IsNullOrEmpty(asset.AnimalId))
                {
                    writer.WriteString("animal_id", asset.AnimalId);
                    writer.WriteBoolean("animal_id_confirmed", asset.AnimalIdConfirmed);
                }
                if (!string.IsNullOrEmpty(asset.GroupName)) writer.WriteString("group_name", asset.GroupName);
                writer.WriteString("phase", asset.Phase switch { TrialMode.Training => "training", TrialMode.Probe => "probe", _ => "unspecified" });
                writer.WriteNumber("day", asset.Day);
                writer.WriteNumber("trial", asset.Trial);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("scenarios");
            foreach (var draft in drafts)
            {
                writer.WriteStartObject();
                writer.WriteString("name", draft.Name);
                if (draft.Threshold is { } threshold) writer.WriteNumber("threshold", threshold);
                if (draft.Invert is { } invert) writer.WriteBoolean("invert", invert);
                if (draft.Blur is { } blur) writer.WriteNumber("blur", blur);
                if (draft.MinArea is { } minArea) writer.WriteNumber("min_area", minArea);
                if (draft.MaxArea is { } maxArea) writer.WriteNumber("max_area", maxArea);
                if (draft.MaxExtension is { } extent) writer.WriteNumber("max_extension", extent);
                if (draft.MaxJump is { } jump) writer.WriteNumber("max_jump", jump);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static ReplayPlanScenario ToScenario(SensitivityScenarioDraft draft) => new()
    {
        Name = draft.Name,
        Threshold = draft.Threshold,
        Invert = draft.Invert,
        Blur = draft.Blur,
        MinArea = draft.MinArea,
        MaxArea = draft.MaxArea,
        MaxExtension = draft.MaxExtension,
        MaxJump = draft.MaxJump
    };

    private static SensitivityScenarioDraft CopyDraft(SensitivityScenarioDraft draft) => new()
    {
        Name = draft.Name,
        Rationale = draft.Rationale,
        Threshold = draft.Threshold,
        Invert = draft.Invert,
        Blur = draft.Blur,
        MinArea = draft.MinArea,
        MaxArea = draft.MaxArea,
        MaxExtension = draft.MaxExtension,
        MaxJump = draft.MaxJump
    };

    private static SensitivityScenarioDraft Family(string name, string rationale, int? threshold = null, bool? invert = null, int? blur = null, double? minArea = null, double? maxArea = null, int? maxExtension = null, double? maxJump = null) => new()
    {
        Name = name,
        Rationale = rationale,
        Threshold = threshold,
        Invert = invert,
        Blur = blur,
        MinArea = minArea,
        MaxArea = maxArea,
        MaxExtension = maxExtension,
        MaxJump = maxJump
    };

    private static bool IsSafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !char.IsAsciiLetterOrDigit(value[0])) return false;
        foreach (var character in value)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) return false;
        return !value.EndsWith('.') && !value.Contains("..", StringComparison.Ordinal) && !Reserved.Contains(value.Split('.')[0]);
    }
}
