using System.Collections.Immutable;
using System.IO;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Cli;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

/// <summary>Actual native replay evidence. It deliberately cannot grant biological eligibility.</summary>
public static class DetectorStabilityAdapter
{
    public static StabilityEvidenceSnapshot Read(string manifestPath, CancellationToken cancellation = default)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var manifestFile = StabilityEvidenceSnapshot.Freeze(manifestPath);
        var manifest = JsonDefaults.Deserialize<ReplayManifestDocument>(File.ReadAllText(manifestPath));
        if (manifest.Schema != ReplayClaims.ManifestSchema) throw new InvalidDataException("Unsupported detector evidence schema.");
        var frozen = Path.Combine(root, "plan.frozen.json");
        var planFile = StabilityEvidenceSnapshot.Freeze(frozen);
        if (planFile.Sha256 != manifest.PlanSha256) throw new InvalidDataException("Frozen detector plan hash differs.");
        ResolvedPlan plan;
        try
        {
            // plan.frozen.json preserves original bytes, including relative paths rooted at the original plan directory.
            var origin = Path.GetFullPath(manifest.PlanPath);
            if (!File.Exists(origin) || StabilityEvidenceSnapshot.Freeze(origin).Sha256 != manifest.PlanSha256) throw new InvalidDataException("Original frozen plan resolution context is absent or changed.");
            plan = ReplayPlanLoader.Load(origin, new NativeReplayTracker());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HeldManifest(manifest, manifestFile, planFile, "Frozen detector source/baseline verification failed: " + exception.Message, cancellation);
        }
        if (manifest.Recordings.Count != plan.Recordings.Count || manifest.Scenarios.Count != plan.Scenarios.Count || manifest.Cases.Count != manifest.PlannedCaseCount || manifest.PlannedCaseCount != plan.Recordings.Count * plan.Scenarios.Count)
            throw new InvalidDataException("Incomplete detector matrix; planned cases must remain represented.");
        var files = ImmutableArray.CreateBuilder<StabilityFileIdentity>(); files.Add(manifestFile); files.Add(planFile);
        files.Add(StabilityEvidenceSnapshot.Freeze(plan.PlanPath));
        var rows = ImmutableArray.CreateBuilder<StabilityTrial>();
        var code = ReplayCodeIdentityProbe.CanonicalSha256(manifest.CodeIdentity);
        var codeHolds = new List<string>();
        try { ReplayCodeIdentityProbe.RequireSame(manifest.CodeIdentity, ReplayCodeIdentityProbe.Capture(new NativeReplayTracker().NativeVersion)); }
        catch (Exception) { codeHolds.Add("Current replay code/native identity differs from frozen evidence."); }
        var matrix = new HashSet<(string, string)>();
        foreach (var item in manifest.Cases)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!matrix.Add((item.RecordingId, item.ScenarioName))) throw new InvalidDataException("Duplicate detector case.");
            var recording = plan.Recordings.Single(r => r.RecordingId == item.RecordingId);
            var scenario = plan.Scenarios.Single(s => s.Name == item.ScenarioName);
            var holds = codeHolds.ToList(); holds.Add("Raw decoder interval only; confirmed biological endpoint adapter absent.");
            if (item.Status != ReplayStatuses.Completed) holds.Add("Detector case " + item.Status + ": " + item.Reason + "; " + item.ErrorMessage);
            var baselineFile = StabilityEvidenceSnapshot.Freeze(recording.BaselinePath); files.Add(baselineFile); files.Add(StabilityEvidenceSnapshot.Freeze(recording.VideoPath));
            StabilityTrace? trace = new(recording.VideoPath, recording.SourceSha256, recording.BaselinePath, recording.BaselineSha256,
                recording.Baseline.Points[0].Frame, recording.Baseline.Points[^1].Frame);
            if (item.Attempts.Count > 0)
            {
                var attempt = item.Attempts[^1];
                var directory = Path.GetFullPath(Path.Combine(root, attempt.RelativeDirectory));
                if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(attempt.RelativeDirectory)) throw new InvalidDataException("Unsafe detector trace path.");
                foreach (var file in attempt.Files)
                {
                    if (Path.GetFileName(file.Name) != file.Name) throw new InvalidDataException("Unsafe detector file name.");
                    var identity = StabilityEvidenceSnapshot.Freeze(Path.Combine(directory, file.Name));
                    if (identity.Sha256 != file.Sha256) throw new InvalidDataException("Published detector evidence bytes changed."); files.Add(identity);
                }
                var caseRecord = JsonDefaults.Deserialize<ReplayAttemptRecord>(File.ReadAllText(Path.Combine(directory, "replay-case.json")));
                if (caseRecord.RecordingId != recording.RecordingId || caseRecord.ScenarioName != scenario.Name || caseRecord.PlanSha256 != plan.Sha256 || caseRecord.SourceSha256 != recording.SourceSha256 || caseRecord.BaselineSha256 != recording.BaselineSha256 || caseRecord.CodeIdentitySha256 != code || caseRecord.Status != item.Status)
                    throw new InvalidDataException("Detector attempt identity differs from frozen matrix.");
                if (item.Status == ReplayStatuses.Completed)
                {
                    var runPath = Path.Combine(directory, "analysis.json"); var runFile = StabilityEvidenceSnapshot.Freeze(runPath);
                    var run = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(runPath));
                    CompletedRunContract.Ensure(run, recording.Metadata, ReplayPlanLoader.Apply(recording.BaselineSettings, scenario), recording.SourceSha256, AlgorithmIdentity.Contract, CompletedRunContract.SoftwareVersion);
                    trace = new(recording.VideoPath, recording.SourceSha256, runPath, runFile.Sha256, run.Points[0].Frame, run.Points[^1].Frame);
                }
            }
            else if (item.Status != ReplayStatuses.Unrun) throw new InvalidDataException("Terminal detector case lacks attempt evidence.");
            var scope = new StabilityScope("Raw decoder interval", recording.Phase ?? "Unspecified", recording.Day ?? 0,
                $"{recording.BaselineSettings.StartSeconds:R}–{recording.BaselineSettings.EndSeconds:R}", "percent", "image coordinates", "coverage_percent (raw decoder)");
            var identityMaterial = new StabilityIdentity(recording.SourceSha256, recording.Baseline.Id, "automatic baseline; corrections not applied", plan.Sha256, scope.Window, recording.BaselineSettingsSha256, "none", code);
            var baseline = recording.Baseline.Metrics.CoveragePercent; var changed = item.ScenarioMetrics?.CoveragePercent;
            rows.Add(new(recording.RecordingId + "/" + item.ScenarioName, recording.RecordingId, recording.AnimalId ?? "Unconfirmed " + recording.RecordingId,
                recording.GroupName ?? "", item.ScenarioName, "native detector retracking; raw decoder metrics", JsonDefaults.Serialize(scenario), scope, identityMaterial, identityMaterial,
                new(baseline, null, null), new(changed, null, null), false, false, holds.ToImmutableArray(), trace) { OriginalScope = scope, AnalyzedFrames = item.ScenarioMetrics?.TotalFrames, PlannedSeconds = recording.Baseline.Metrics.DurationSeconds, UnknownSeconds = item.ScenarioMetrics?.UnknownSeconds, NumericRanges = DeclaredRanges(scenario) });
        }
        if (!manifestFile.Equals(StabilityEvidenceSnapshot.Freeze(manifestPath))) throw new InvalidDataException("Detector evidence changed during load.");
        var inputs = ImmutableArray.CreateBuilder<StabilityProducerInput>();
        inputs.Add(new("detector-replay-manifest", manifestFile.Path, manifestFile.Sha256, File.ReadAllText(manifestPath)));
        inputs.Add(new("detector-frozen-plan", planFile.Path, planFile.Sha256, File.ReadAllText(frozen)));
        foreach (var recording in plan.Recordings) inputs.Add(new("automatic-baseline", recording.BaselinePath, recording.BaselineSha256, File.ReadAllText(recording.BaselinePath)));
        return new(ResultStability.Build(rows, cancellation) with { Inputs = inputs.ToImmutable() }, files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToImmutableArray());
    }
    private static StabilityEvidenceSnapshot HeldManifest(ReplayManifestDocument manifest, StabilityFileIdentity manifestFile, StabilityFileIdentity planFile, string reason, CancellationToken cancellation)
    {
        var rows = manifest.Cases.Select(item =>
        {
            var recording = manifest.Recordings.SingleOrDefault(r => r.RecordingId == item.RecordingId);
            var scope = new StabilityScope("Unvalidated raw decoder matrix", recording?.Phase ?? "Unspecified", recording?.Day ?? 0, "unvalidated decoder interval", "raw units held", "image coordinates", "raw endpoint held");
            var identity = new StabilityIdentity(recording?.SourceSha256 ?? "", recording?.BaselineRunId ?? "", "automatic baseline", manifest.PlanSha256, "raw decoder interval", recording?.BaselineSettingsSha256 ?? "", "none", ReplayCodeIdentityProbe.CanonicalSha256(manifest.CodeIdentity));
            return new StabilityTrial(item.RecordingId + "/" + item.ScenarioName, item.RecordingId, recording?.AnimalId ?? "Unconfirmed " + item.RecordingId, recording?.GroupName ?? "", item.ScenarioName,
                "unvalidated detector manifest description; numerical endpoints held", "Plan retained; source/baseline verification failed", scope, identity, identity, new(null, null, null), new(null, null, null), false, false,
                [reason, "Manifest describes case status " + item.Status + "; " + item.Reason + "; " + item.ErrorMessage], null) { OriginalScope = scope };
        });
        return new(ResultStability.Build(rows, cancellation) with { Inputs = [new("detector-replay-manifest", manifestFile.Path, manifestFile.Sha256, File.ReadAllText(manifestFile.Path)), new("detector-frozen-plan", planFile.Path, planFile.Sha256, File.ReadAllText(planFile.Path))] }, [manifestFile, planFile]);
    }
    public static ImmutableArray<StabilityDeclaredRange> DeclaredRanges(ReplayPlanScenario scenario)
    {
        var ranges = ImmutableArray.CreateBuilder<StabilityDeclaredRange>();
        void Add(string name, string unit, double? value) { if (value is double number) ranges.Add(new(name, unit, number, number)); }
        Add("threshold", "grayscale", scenario.Threshold); Add("invert", "boolean", scenario.Invert is bool invert ? invert ? 1 : 0 : null); Add("blur", "pixels", scenario.Blur);
        Add("min_area", "pixel area", scenario.MinArea); Add("max_area", "pixel area", scenario.MaxArea); Add("max_extension", "pixels", scenario.MaxExtension); Add("max_jump", "pixels per frame", scenario.MaxJump);
        return ranges.ToImmutable();
    }
}
