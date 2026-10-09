using System.Collections.Immutable;
using System.IO;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Cli;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

/// <summary>Actual G04 frozen catalog plus replay; unavailable imported cases are never silently omitted.</summary>
public static class SensitivityStabilityAdapter
{
    public static StabilityEvidenceSnapshot Read(string freezePath, string databasePath, CancellationToken cancellation = default)
    {
        var file = StabilityEvidenceSnapshot.Freeze(freezePath); var freeze = JsonDefaults.Deserialize<SensitivityFreezeDocument>(File.ReadAllText(freezePath));
        if (freeze.Schema != SensitivityCatalog.FreezeSchema || ReplayCodeIdentityProbe.CanonicalSha256(freeze.CodeIdentity) != freeze.CodeIdentitySha256) throw new InvalidDataException("Unsupported sensitivity freeze schema/code digest.");
        var directory = Path.GetDirectoryName(file.Path)!; var workspace = Path.GetDirectoryName(directory)!;
        if (Path.GetFileName(directory) != "freeze" || !SensitivityCatalog.SamePlanBytes(workspace, freeze)) throw new InvalidDataException("Sensitivity plan path/bytes differ.");
        var snapshot = AtlasSnapshot.Read(databasePath); var sameSnapshot = snapshot.Revision == freeze.SnapshotRevision;
        var source = SensitivityCatalog.ReadManifest(workspace);
        var replayPath = Path.Combine(workspace, "replay", "replay-manifest.json");
        var rows = ImmutableArray.CreateBuilder<StabilityTrial>(); var files = ImmutableArray.CreateBuilder<StabilityFileIdentity>(); var inputs = ImmutableArray.CreateBuilder<StabilityProducerInput>();
        files.Add(file); var planFile = StabilityEvidenceSnapshot.Freeze(Path.Combine(directory, "plan.json")); files.Add(planFile);
        inputs.Add(new("desktop-sensitivity-freeze", file.Path, file.Sha256, File.ReadAllText(freezePath))); inputs.Add(new("detector-frozen-plan", planFile.Path, planFile.Sha256, File.ReadAllText(planFile.Path)));
        if (source is not null)
        {
            if (source.PlanSha256 != freeze.PlanSha256 || ReplayCodeIdentityProbe.CanonicalSha256(source.CodeIdentity) != freeze.CodeIdentitySha256) throw new InvalidDataException("Desktop freeze/replay identities differ.");
            var replay = DetectorStabilityAdapter.Read(replayPath, cancellation); rows.AddRange(replay.Report.Trials); files.AddRange(replay.Files); inputs.AddRange(replay.Report.Inputs);
        }
        else
            foreach (var recording in freeze.Recordings) foreach (var scenario in freeze.Scenarios)
                rows.Add(Held(recording.RecordingId, scenario.Name, "Native replay has not run; frozen planned case retained.", "", "", recording.SourceSha256, recording.SettingsSha256, false));
        var frozenInventory = SensitivityCatalog.ReadInventory(workspace, freeze);
        if (frozenInventory is not null)
        {
            var inventoryFile = StabilityEvidenceSnapshot.Freeze(Path.Combine(directory, "inventory.json")); files.Add(inventoryFile);
            inputs.Add(new("desktop-sensitivity-inventory", inventoryFile.Path, inventoryFile.Sha256, File.ReadAllText(inventoryFile.Path)));
            var eligibleIds = freeze.Recordings.Select(r => r.RecordingId).ToHashSet(StringComparer.Ordinal);
            foreach (var recording in frozenInventory.Where(r => !eligibleIds.Contains(r.RecordingId))) foreach (var scenario in freeze.Scenarios)
                rows.Add(Held(recording.RecordingId, scenario.Name, "Frozen observed recording " + recording.Availability + ": " + recording.Reason + "; scenario unrun.", recording.AnimalId, recording.GroupName, "", "", recording.Availability == "excluded"));
            if (!sameSnapshot)
                for (var i = 0; i < rows.Count; i++) rows[i] = rows[i] with { Holds = rows[i].Holds.Add("Historical sensitivity snapshot differs from current study; complete frozen observed inventory is retained.") };
        }
        else if (sameSnapshot)
        {
            var inventory = SensitivityCatalog.ReadRecordings(snapshot); var eligibleIds = freeze.Recordings.Select(r => r.RecordingId).ToHashSet(StringComparer.Ordinal);
            foreach (var recording in inventory.Where(r => !eligibleIds.Contains(r.RecordingId))) foreach (var scenario in freeze.Scenarios)
                rows.Add(Held(recording.RecordingId, scenario.Name, "Imported recording " + recording.Availability + ": " + recording.Reason + "; scenario unrun.", recording.Asset?.AnimalId ?? "", recording.Asset?.GroupName ?? "", recording.Asset?.SourceSha256 ?? "", recording.Asset is null ? "" : MouseTrackerTabajara.Storage.StudyStore.SettingsHash(recording.Asset.Settings), recording.Availability == "excluded"));
            var material = JsonDefaults.Serialize(snapshot.Tables); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
            inputs.Add(new("matched-frozen-study-tables", databasePath, hash, material));
        }
        else
            for (var i = 0; i < rows.Count; i++) rows[i] = rows[i] with { Holds = rows[i].Holds.Add("Historical sensitivity snapshot differs; complete frozen unavailable-import inventory is absent from this schema.") };
        var report = ResultStability.Build(rows, cancellation) with { Inputs = inputs.DistinctBy(i => (i.Kind, i.Sha256)).ToImmutableArray() };
        return new(report, files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToImmutableArray()) { VerifyCurrent = sameSnapshot ? snapshot.IsCurrent : null };

        StabilityTrial Held(string recordingId, string scenario, string reason, string animal, string group, string sourceSha, string settingsSha, bool excluded)
        {
            var scope = new StabilityScope("Raw detector inventory", "Unspecified", 0, "decoder interval not analyzed", "raw units held", "image coordinates", "raw endpoint held");
            var identity = new StabilityIdentity(sourceSha, "unrun", "automatic baseline", freeze.PlanSha256, scope.Window, settingsSha, "none", freeze.CodeIdentitySha256);
            return new(recordingId + "/" + scenario, recordingId, string.IsNullOrWhiteSpace(animal) ? "Unconfirmed " + recordingId : animal, group, scenario, "native detector scenario unrun", "Frozen prespecified detector plan", scope, identity, identity,
                new(null, null, null), new(null, null, null), false, excluded, [reason], null) { OriginalScope = scope, NumericRanges = DetectorStabilityAdapter.DeclaredRanges(ToPlan(freeze.Scenarios.Single(s => s.Name == scenario))) };
        }
    }
    private static ReplayPlanScenario ToPlan(SensitivityScenarioDraft scenario) => new() { Name = scenario.Name, Threshold = scenario.Threshold, Invert = scenario.Invert, Blur = scenario.Blur, MinArea = scenario.MinArea, MaxArea = scenario.MaxArea, MaxExtension = scenario.MaxExtension, MaxJump = scenario.MaxJump };
}
