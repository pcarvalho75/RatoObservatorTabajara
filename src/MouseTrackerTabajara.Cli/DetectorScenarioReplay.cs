using System.Globalization;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Cli;

public static class DetectorScenarioReplay
{
    public static int Run(ReplayInvocation invocation, ReplayCommandContext context)
    {
        Report(context, null, "checking_sources", 0, null, null);
        var plan = ReplayPlanLoader.Load(invocation.PlanPath, context.Tracker);
        var output = Path.GetFullPath(invocation.OutputPath);
        ReplayPaths.EnsureOutputSafe(output, plan, invocation.CancelWhenPath);
        if (string.IsNullOrWhiteSpace(context.Tracker.NativeVersion))
            throw new PlanRejectedException("The native tracker did not report a version.");
        var actualIdentity = context.CaptureIdentity?.Invoke() ?? ReplayCodeIdentityProbe.Capture(context.Tracker.NativeVersion);
        using var lease = OutputLease.Acquire(output);
        var manifestPath = Path.Combine(output, "replay-manifest.json");
        var existing = OpenOutput(output, manifestPath, plan);
        if (existing is not null)
        {
            ReplayCodeIdentityProbe.RequireSame(existing.CodeIdentity, actualIdentity);
            ResumeState.VerifyAndAdopt(output, existing, plan, context.Tracker, ReplayCodeIdentityProbe.CanonicalSha256(existing.CodeIdentity));
        }
        var manifest = existing ?? CreateManifest(plan, actualIdentity);
        EnsureFrozen(output, plan);
        BindProvenance(manifest, plan, existing is null ? actualIdentity : null);
        if (existing is not null)
        {
            foreach (var item in manifest.Cases)
                if (item.Status == ReplayStatuses.Completed) item.Reused = true;
            DiscardStaging(output);
        }
        SaveManifest(manifestPath, manifest);
        Report(context, manifest, "ready", manifest.PlannedCaseCount, null, null);

        var stop = StopReason.None;
        for (var index = 0; index < manifest.Cases.Count; index++)
        {
            var item = manifest.Cases[index];
            if (item.Status is ReplayStatuses.Completed or ReplayStatuses.Failed) continue;
            var completed = manifest.Cases.Count(candidate => candidate.Status == ReplayStatuses.Completed);
            if (context.Cancellation.IsCancellationRequested)
            {
                Stop(manifest, index, "operator_cancelled", completed);
                stop = StopReason.Cancelled;
                break;
            }
            var cancelError = CancelWhen.Read(invocation.CancelWhenPath, completed, out var cancel);
            if (cancelError is not null)
            {
                Stop(manifest, index, "cancel_file_invalid", completed);
                context.Error.WriteLine(cancelError);
                stop = StopReason.Usage;
                break;
            }
            if (cancel)
            {
                Stop(manifest, index, "operator_cancelled", completed);
                stop = StopReason.Cancelled;
                break;
            }
            var recording = plan.Recordings.First(candidate => candidate.RecordingId == item.RecordingId);
            var scenario = plan.Scenarios.First(candidate => candidate.Name == item.ScenarioName);
            Report(context, manifest, "case", manifest.PlannedCaseCount, item.RecordingId, item.ScenarioName);
            try
            {
                RunCase(output, manifest, item, recording, scenario, plan, context);
                context.Error.WriteLine($"completed {item.RecordingId} {item.ScenarioName}");
            }
            catch (OperationCanceledException exception)
            {
                PublishTerminal(output, item, recording, scenario, plan, ReplayStatuses.Cancelled, published: false, run: null, differences: null, metrics: null, exception, context.Tracker, ReplayCodeIdentityProbe.CanonicalSha256(manifest.CodeIdentity));
                Stop(manifest, index + 1, "operator_cancelled", manifest.Cases.Count(candidate => candidate.Status == ReplayStatuses.Completed));
                context.Error.WriteLine($"cancelled {item.RecordingId} {item.ScenarioName}");
                stop = StopReason.Cancelled;
                SaveManifest(manifestPath, manifest);
                break;
            }
            catch (Exception exception) when (exception is not PlanRejectedException)
            {
                PublishTerminal(output, item, recording, scenario, plan, ReplayStatuses.Failed, published: false, run: exception.Data["rejected-run"] as AnalysisRun, differences: null, metrics: null, exception, context.Tracker, ReplayCodeIdentityProbe.CanonicalSha256(manifest.CodeIdentity));
                context.Error.WriteLine($"failed {item.RecordingId} {item.ScenarioName}");
            }
            SaveManifest(manifestPath, manifest);
            Report(context, manifest, "saved", manifest.PlannedCaseCount, item.RecordingId, item.ScenarioName);
            if (stop != StopReason.None) break;
        }
        if (stop != StopReason.None) SaveManifest(manifestPath, manifest);
        var code = ExitCode(manifest, stop);
        Report(context, manifest, "finished", manifest.PlannedCaseCount, null, null);
        context.Out.WriteLine(JsonDefaults.Serialize(Summary(manifest, manifestPath, code)));
        return code;
    }

    private static void Report(ReplayCommandContext context, ReplayManifestDocument? manifest, string phase, int planned, string? recordingId, string? scenarioName)
    {
        if (context.Progress is null) return;
        try
        {
            context.Progress(new ReplayProgress(
                phase,
                planned,
                manifest?.Cases.Count(item => item.Status == ReplayStatuses.Completed) ?? 0,
                manifest?.Cases.Count(item => item.Status == ReplayStatuses.Failed) ?? 0,
                manifest?.Cases.Count(item => item.Status == ReplayStatuses.Unrun) ?? 0,
                manifest?.Cases.Count(item => item.Status == ReplayStatuses.Cancelled) ?? 0,
                recordingId,
                scenarioName));
        }
        catch (Exception)
        {
            context.Error.WriteLine("progress observer failed and was ignored");
        }
    }

    private static ReplayManifestDocument? OpenOutput(string output, string manifestPath, ResolvedPlan plan)
    {
        if (!Directory.Exists(output)) return null;
        var entries = Directory.GetFileSystemEntries(output).Select(Path.GetFullPath).ToList();
        if (entries.Count == 0) return null;
        var frozenPath = Path.GetFullPath(Path.Combine(output, "plan.frozen.json"));
        var leasePath = Path.GetFullPath(Path.Combine(output, ReplayOutputGuard.LeaseFileName));
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            frozenPath, Path.GetFullPath(manifestPath), Path.GetFullPath(manifestPath + ".tmp"),
            Path.GetFullPath(Path.Combine(output, ".staging")), Path.GetFullPath(Path.Combine(output, "cases")),
            leasePath
        };
        foreach (var entry in entries)
            if (!allowed.Contains(entry))
                throw new PlanRejectedException($"Output directory contains unexpected entry '{Path.GetFileName(entry)}'. It was left unchanged.");
        if (!File.Exists(frozenPath))
        {
            if (entries.All(entry => string.Equals(entry, leasePath, StringComparison.OrdinalIgnoreCase))) return null;
            throw new PlanRejectedException("Output directory is not a replay folder. It was left unchanged.");
        }
        var frozen = File.ReadAllBytes(frozenPath);
        if (!frozen.AsSpan().SequenceEqual(plan.Bytes))
            throw new PlanRejectedException("Frozen plan bytes do not match this plan. Output was left unchanged.");
        if (!File.Exists(manifestPath))
        {
            if (Directory.Exists(Path.Combine(output, "cases")) || Directory.Exists(Path.Combine(output, ".staging")))
                throw new PlanRejectedException("Output has case files without a manifest. It was left unchanged.");
            return null;
        }
        ReplayManifestDocument manifest;
        try { manifest = JsonDefaults.Deserialize<ReplayManifestDocument>(File.ReadAllText(manifestPath)); }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or System.Text.Json.JsonException)
        { throw new PlanRejectedException("Replay manifest is unreadable. It was left unchanged."); }
        if (!string.Equals(manifest.Schema, ReplayClaims.ManifestSchema, StringComparison.Ordinal) || !string.Equals(manifest.PlanSha256, plan.Sha256, StringComparison.Ordinal))
            throw new PlanRejectedException("Replay manifest does not match this frozen plan. It was left unchanged.");
        return manifest;
    }

    private static void EnsureFrozen(string output, ResolvedPlan plan)
    {
        Directory.CreateDirectory(output);
        var frozenPath = Path.Combine(output, "plan.frozen.json");
        if (!File.Exists(frozenPath))
        {
            File.WriteAllBytes(frozenPath, plan.Bytes);
            return;
        }
        if (!File.ReadAllBytes(frozenPath).AsSpan().SequenceEqual(plan.Bytes))
            throw new PlanRejectedException("Frozen plan bytes do not match this plan. Output was left unchanged.");
    }

    private static void RunCase(string output, ReplayManifestDocument manifest, ManifestCase item, ResolvedRecording recording, ReplayPlanScenario scenario, ResolvedPlan plan, ReplayCommandContext context)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        var sourceHash = context.Tracker.HashFile(recording.VideoPath);
        var baselineHash = context.Tracker.HashFile(recording.BaselinePath);
        if (!string.Equals(sourceHash, recording.SourceSha256, StringComparison.Ordinal) || !string.Equals(baselineHash, recording.BaselineSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Source or baseline identity changed after the plan was frozen. This case was not analyzed.");
        var applied = ReplayPlanLoader.Apply(recording.BaselineSettings, scenario);
        var index = item.Attempts.Count + 1;
        if (index > 100) throw new InvalidDataException("This case already has 100 attempts. A new attempt was not created.");
        var destination = ReplayPaths.AttemptDirectory(output, recording.RecordingId, scenario.Name!, index);
        if (Directory.Exists(destination))
            throw new InvalidDataException("The attempt directory already exists and was not overwritten.");
        var stagingRoot = StagingRoot(output);
        var staging = Path.Combine(stagingRoot, recording.RecordingId + "__" + scenario.Name + "__" + index.ToString(CultureInfo.InvariantCulture));
        ReplayOutputGuard.DeleteContained(staging, stagingRoot, "staging cleanup");
        ReplayOutputGuard.RequireCleanupTarget(staging, stagingRoot, "staging directory");
        Directory.CreateDirectory(staging);
        AnalysisRun? run = null;
        try
        {
            run = context.Tracker.Analyze(new VideoAsset
            {
                Id = recording.RecordingId,
                Path = recording.VideoPath,
                SourceSha256 = recording.SourceSha256,
                Metadata = recording.Metadata,
                Settings = applied,
                AnimalId = recording.AnimalId ?? "",
                AnimalIdConfirmed = recording.AnimalIdConfirmed ?? false
            }, context.Cancellation);
            CompletedRunContract.Ensure(run, recording.Metadata, applied, recording.SourceSha256, AlgorithmIdentity.Contract, CompletedRunContract.SoftwareVersion);
        }
        catch (OperationCanceledException)
        {
            ReplayOutputGuard.DeleteContained(staging, stagingRoot, "staging cleanup");
            throw;
        }
        catch (Exception exception)
        {
            ReplayOutputGuard.DeleteContained(staging, stagingRoot, "staging cleanup");
            if (run is not null && exception is InvalidDataException or ArgumentException) exception.Data["rejected-run"] = run;
            throw;
        }
        var published = run ?? throw new InvalidDataException("The tracker returned no analysis.");
        try
        {
            ExportWriter.SaveRun(published, staging);
            var differences = EndpointChanges.Build(recording.Baseline.Metrics, published.Metrics);
            var record = CaseRecord(index, ReplayStatuses.Completed, published: true, recording, scenario, plan, applied, published, exception: null, OtherFiles(staging, context.Tracker), ReplayCodeIdentityProbe.CanonicalSha256(manifest.CodeIdentity));
            File.WriteAllText(Path.Combine(staging, "replay-case.json"), JsonDefaults.Serialize(record));
            MovePublished(output, staging, destination);
            Assign(item, ReplayStatuses.Completed, published: true, "completed", record, differences, published.Metrics, ManifestAttemptFrom(record, recording.RecordingId, scenario.Name!, index, HashAll(destination, context.Tracker)));
        }
        catch (OperationCanceledException)
        {
            ReplayOutputGuard.DeleteContained(staging, stagingRoot, "staging cleanup");
            throw;
        }
        catch (Exception exception)
        {
            ReplayOutputGuard.DeleteContained(staging, stagingRoot, "staging cleanup");
            if (!Directory.Exists(destination)) exception.Data["rejected-run"] = published;
            throw;
        }
    }

    private static void PublishTerminal(string output, ManifestCase item, ResolvedRecording recording, ReplayPlanScenario scenario, ResolvedPlan plan, string status, bool published, AnalysisRun? run, List<EndpointDifference>? differences, Measurements? metrics, Exception exception, IReplayTracker tracker, string codeIdentitySha256)
    {
        var index = item.Attempts.Count + 1;
        var destination = ReplayPaths.AttemptDirectory(output, recording.RecordingId, scenario.Name!, index);
        if (Directory.Exists(destination))
        {
            item.Status = ReplayStatuses.Failed;
            item.Published = false;
            item.Reason = "attempt directory already existed and was not overwritten";
            item.ErrorType = exception.GetType().Name;
            item.ErrorMessage = Clip(exception.Message);
            return;
        }
        var stagingRoot = StagingRoot(output);
        var staging = Path.Combine(stagingRoot, "terminal-" + recording.RecordingId + "__" + scenario.Name + "__" + index.ToString(CultureInfo.InvariantCulture));
        ReplayOutputGuard.DeleteContained(staging, stagingRoot, "staging cleanup");
        ReplayOutputGuard.RequireCleanupTarget(staging, stagingRoot, "staging directory");
        Directory.CreateDirectory(staging);
        if (run is not null && status == ReplayStatuses.Failed)
            File.WriteAllText(Path.Combine(staging, "rejected-analysis.json"), JsonDefaults.Serialize(run));
        var applied = ReplayPlanLoader.Apply(recording.BaselineSettings, scenario);
        var record = CaseRecord(index, status, published, recording, scenario, plan, applied, run, exception, OtherFiles(staging, tracker), codeIdentitySha256);
        File.WriteAllText(Path.Combine(staging, "replay-case.json"), JsonDefaults.Serialize(record));
        MovePublished(output, staging, destination);
        Assign(item, status, published, status, record, differences, metrics, ManifestAttemptFrom(record, recording.RecordingId, scenario.Name!, index, HashAll(destination, tracker)));
    }

    private static void Assign(ManifestCase item, string status, bool published, string reason, ReplayAttemptRecord record, List<EndpointDifference>? differences, Measurements? metrics, ManifestAttempt attempt)
    {
        item.Status = status;
        item.Published = published;
        item.Reused = false;
        item.Reason = reason;
        item.ErrorType = record.ErrorType;
        item.ErrorMessage = record.ErrorMessage;
        item.EndpointDifferences = differences;
        item.ScenarioMetrics = metrics;
        item.Attempts.Add(attempt);
    }

    private static ReplayAttemptRecord CaseRecord(int index, string status, bool published, ResolvedRecording recording, ReplayPlanScenario scenario, ResolvedPlan plan, TrackingSettings applied, AnalysisRun? run, Exception? exception, List<ReplayHashedFile> files, string codeIdentitySha256) => new()
    {
        Index = index,
        Status = status,
        Published = published,
        RecordingId = recording.RecordingId,
        ScenarioName = scenario.Name!,
        PlanSha256 = plan.Sha256,
        SourceSha256 = recording.SourceSha256,
        BaselineSha256 = recording.BaselineSha256,
        AlgorithmContract = AlgorithmIdentity.Contract,
        SoftwareVersion = run?.SoftwareVersion ?? CompletedRunContract.SoftwareVersion,
        RunId = run?.Id,
        AppliedSettingsSha256 = ReplayPlanLoader.SettingsHash(applied),
        ErrorType = exception?.GetType().Name,
        ErrorMessage = exception is null ? null : Clip(exception.Message),
        MaximumTimestampErrorSeconds = run?.MaximumTimestampErrorSeconds,
        CodeIdentitySha256 = codeIdentitySha256,
        Files = files
    };

    private static ManifestAttempt ManifestAttemptFrom(ReplayAttemptRecord record, string recordingId, string scenario, int index, List<ReplayHashedFile> allFiles) => new()
    {
        Index = index,
        Status = record.Status,
        Published = record.Published,
        RelativeDirectory = ReplayPaths.RelativeAttempt(recordingId, scenario, index),
        RunId = record.RunId,
        ErrorType = record.ErrorType,
        ErrorMessage = record.ErrorMessage,
        Files = allFiles
    };

    private static List<ReplayHashedFile> OtherFiles(string directory, IReplayTracker tracker) =>
        Directory.GetFiles(directory).Select(path => new ReplayHashedFile { Name = Path.GetFileName(path), Sha256 = tracker.HashFile(path) }).OrderBy(file => file.Name, StringComparer.Ordinal).ToList();

    private static List<ReplayHashedFile> HashAll(string directory, IReplayTracker tracker) => OtherFiles(directory, tracker);

    private static void Stop(ReplayManifestDocument manifest, int from, string reason, int completed)
    {
        for (var index = from; index < manifest.Cases.Count; index++)
        {
            var item = manifest.Cases[index];
            if (item.Status is ReplayStatuses.Completed or ReplayStatuses.Failed) continue;
            if (item.Attempts.Count == 0)
            {
                item.Status = ReplayStatuses.Unrun;
                item.Published = false;
                item.Reason = reason;
            }
            else if (item.Status == ReplayStatuses.Cancelled) item.Reason = reason;
        }
        manifest.Interruptions.Add(new ManifestInterruption
        {
            Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Reason = reason,
            PlanIndex = from,
            CompletedCases = completed
        });
    }

    private static ReplayManifestDocument CreateManifest(ResolvedPlan plan, ReplayCodeIdentity actualIdentity)
    {
        var manifest = new ReplayManifestDocument();
        BindProvenance(manifest, plan, actualIdentity);
        var planIndex = 0;
        foreach (var recording in plan.Recordings)
        {
            foreach (var scenario in plan.Scenarios)
            {
                var applied = ReplayPlanLoader.Apply(recording.BaselineSettings, scenario);
                manifest.Cases.Add(new ManifestCase
                {
                    PlanIndex = planIndex++,
                    RecordingId = recording.RecordingId,
                    ScenarioName = scenario.Name!,
                    AnimalId = recording.AnimalId,
                    AnimalIdProvided = recording.AnimalIdProvided,
                    AnimalIdSource = recording.AnimalIdSource,
                    Preserved = Preserved(recording.BaselineSettings, recording.Baseline.Metrics.DistanceUnit),
                    Detector = Detector(scenario, applied)
                });
            }
        }
        manifest.PlannedCaseCount = manifest.Cases.Count;
        return manifest;
    }

    private static void BindProvenance(ReplayManifestDocument manifest, ResolvedPlan plan, ReplayCodeIdentity? replacementIdentity)
    {
        manifest.Schema = ReplayClaims.ManifestSchema;
        manifest.PlanSha256 = plan.Sha256;
        manifest.PlanPath = plan.PlanPath;
        manifest.BiologicalN = null;
        manifest.BiologicalNStatement = ReplayClaims.NoBiologicalN;
        manifest.RecordingCount = plan.Recordings.Count;
        manifest.ScenarioCount = plan.Scenarios.Count;
        manifest.PlannedCaseCount = plan.Recordings.Count * plan.Scenarios.Count;
        manifest.SelectionPolicy = ReplayClaims.SelectionPolicy;
        manifest.EndpointDifferenceScope = ReplayClaims.RawDifferences;
        manifest.ClaimLimits = ReplayClaims.All.ToList();
        if (replacementIdentity is not null) manifest.CodeIdentity = replacementIdentity;
        manifest.Recordings = plan.Recordings.Select(recording => new ManifestRecording
        {
            RecordingId = recording.RecordingId,
            VideoPath = recording.VideoPath,
            SourceSha256 = recording.SourceSha256,
            Metadata = recording.Metadata,
            BaselinePath = recording.BaselinePath,
            BaselineSha256 = recording.BaselineSha256,
            BaselineRunId = recording.Baseline.Id,
            BaselineSettingsSha256 = recording.BaselineSettingsSha256,
            AnimalId = recording.AnimalId,
            AnimalIdProvided = recording.AnimalIdProvided,
            AnimalIdConfirmed = recording.AnimalIdConfirmed,
            AnimalIdSource = recording.AnimalIdSource,
            GroupName = recording.GroupName,
            Phase = recording.Phase,
            Day = recording.Day,
            Trial = recording.Trial,
            ProvenanceNote = recording.ProvenanceNote,
            BaselineMetrics = recording.Baseline.Metrics
        }).ToList();
        manifest.Scenarios = plan.Scenarios.Select(scenario => new ManifestScenario
        {
            Name = scenario.Name!,
            OriginalDetectorSettings = scenario.OriginalDetectorSettings,
            Threshold = scenario.Threshold,
            Invert = scenario.Invert,
            Blur = scenario.Blur,
            MinArea = scenario.MinArea,
            MaxArea = scenario.MaxArea,
            MaxExtension = scenario.MaxExtension,
            MaxJump = scenario.MaxJump
        }).ToList();
        foreach (var item in manifest.Cases)
        {
            var recording = plan.Recordings.First(candidate => candidate.RecordingId == item.RecordingId);
            var scenario = plan.Scenarios.First(candidate => candidate.Name == item.ScenarioName);
            var applied = ReplayPlanLoader.Apply(recording.BaselineSettings, scenario);
            item.AnimalId = recording.AnimalId;
            item.AnimalIdProvided = recording.AnimalIdProvided;
            item.AnimalIdSource = recording.AnimalIdSource;
            item.Preserved = Preserved(recording.BaselineSettings, recording.Baseline.Metrics.DistanceUnit);
            item.Detector = Detector(scenario, applied);
        }
    }

    private static PreservedBaseline Preserved(TrackingSettings settings, string distanceUnit) => new()
    {
        Cx = settings.Cx, Cy = settings.Cy, Rx = settings.Rx, Ry = settings.Ry,
        StartSeconds = settings.StartSeconds, EndSeconds = settings.EndSeconds,
        TimeWindow = "decoder_interval", TimeWindowStatement = ReplayClaims.DecoderWindow,
        DiameterCm = settings.DiameterCm,
        DistanceUnit = distanceUnit, WallBoundary = settings.WallBoundary, ArenaConfirmed = settings.ArenaConfirmed,
        Platform = settings.Platform is null ? null : new PlatformSite(settings.Platform.X, settings.Platform.Y, settings.Platform.Radius),
        Exclusions = (settings.Exclusions ?? []).Select(region => new ExclusionRegion(region.Left, region.Top, region.Right, region.Bottom)).ToList()
    };

    private static DetectorChoice Detector(ReplayPlanScenario scenario, TrackingSettings applied) => new()
    {
        OriginalDetectorSettings = scenario.OriginalDetectorSettings,
        Threshold = scenario.Threshold, Invert = scenario.Invert, Blur = scenario.Blur,
        MinArea = scenario.MinArea, MaxArea = scenario.MaxArea, MaxExtension = scenario.MaxExtension, MaxJump = scenario.MaxJump,
        AppliedThreshold = applied.Threshold, AppliedInvert = applied.Invert, AppliedBlur = applied.Blur,
        AppliedMinArea = applied.MinArea, AppliedMaxArea = applied.MaxArea, AppliedMaxExtension = applied.MaxExtension, AppliedMaxJump = applied.MaxJump,
        AppliedSettingsSha256 = ReplayPlanLoader.SettingsHash(applied)
    };

    private static void SaveManifest(string path, ReplayManifestDocument manifest)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new PlanRejectedException("The manifest path has no directory. It was not written.");
        Directory.CreateDirectory(directory);
        var frozen = Path.Combine(directory, "plan.frozen.json");
        if (!File.Exists(frozen)) throw new PlanRejectedException("The frozen plan is missing. The manifest was not written.");
        var temporary = path + ".tmp";
        ReplayOutputGuard.RequireCleanupTarget(temporary, directory, "manifest temporary");
        ReplayOutputGuard.RequireCleanupTarget(path, directory, "manifest");
        File.WriteAllText(temporary, JsonDefaults.Serialize(manifest));
        if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
        else File.Move(temporary, path);
    }

    private static string StagingRoot(string output)
    {
        var staging = Path.Combine(output, ".staging");
        ReplayOutputGuard.RequireCleanupTarget(staging, output, "staging root");
        Directory.CreateDirectory(staging);
        return staging;
    }

    private static void MovePublished(string output, string staging, string destination)
    {
        var cases = Path.Combine(output, "cases");
        if (!Directory.Exists(cases))
        {
            ReplayOutputGuard.RequireCleanupTarget(cases, output, "cases root");
            Directory.CreateDirectory(cases);
        }
        ReplayOutputGuard.MoveContainedDirectory(staging, Path.Combine(output, ".staging"), destination, cases);
    }

    private static void DiscardStaging(string output)
    {
        var staging = Path.Combine(output, ".staging");
        if (!Directory.Exists(staging)) return;
        ReplayOutputGuard.DeleteContained(staging, output, "staging cleanup");
    }

    private static int ExitCode(ReplayManifestDocument manifest, StopReason stop)
    {
        if (stop == StopReason.Usage) return 1;
        if (stop == StopReason.Cancelled) return 3;
        if (manifest.Cases.Any(item => item.Status == ReplayStatuses.Failed)) return 4;
        if (manifest.Cases.Count > 0 && manifest.Cases.All(item => item.Status == ReplayStatuses.Completed)) return 0;
        return 4;
    }

    private static ReplaySummary Summary(ReplayManifestDocument manifest, string manifestPath, int code) => new()
    {
        Result = code switch { 0 => "completed", 1 => "usage", 3 => "cancelled", _ => "failed" },
        Manifest = manifestPath,
        PlanSha256 = manifest.PlanSha256,
        PlannedCases = manifest.PlannedCaseCount,
        Completed = manifest.Cases.Count(item => item.Status == ReplayStatuses.Completed),
        Failed = manifest.Cases.Count(item => item.Status == ReplayStatuses.Failed),
        Unrun = manifest.Cases.Count(item => item.Status == ReplayStatuses.Unrun),
        Cancelled = manifest.Cases.Count(item => item.Status == ReplayStatuses.Cancelled),
        Reused = manifest.Cases.Count(item => item.Reused),
        BiologicalN = null,
        SelectionPolicy = manifest.SelectionPolicy
    };

    private static string Clip(string message) => message.Length <= 1000 ? message : message[..1000];

    private enum StopReason { None, Cancelled, Usage }
}

public static class EndpointChanges
{
    public static List<EndpointDifference> Build(Measurements baseline, Measurements scenario)
    {
        if (!string.Equals(baseline.DistanceUnit, scenario.DistanceUnit, StringComparison.Ordinal))
            throw new InvalidDataException("Distance units changed between the frozen baseline and the scenario.");
        var rows = new List<EndpointDifference>();
        Add(rows, "coverage_percent", baseline.CoveragePercent, scenario.CoveragePercent, "percent");
        Add(rows, "tracked_frames", baseline.TrackedFrames, scenario.TrackedFrames, "frames");
        Add(rows, "total_frames", baseline.TotalFrames, scenario.TotalFrames, "frames");
        Add(rows, "tracked_seconds", baseline.TrackedSeconds, scenario.TrackedSeconds, "seconds");
        Add(rows, "unknown_seconds", baseline.UnknownSeconds, scenario.UnknownSeconds, "seconds");
        Add(rows, "duration_seconds", baseline.DurationSeconds, scenario.DurationSeconds, "seconds");
        Add(rows, "longest_gap_seconds", baseline.LongestGapSeconds, scenario.LongestGapSeconds, "seconds");
        Add(rows, "adjacent_motion_seconds", baseline.AdjacentMotionSeconds, scenario.AdjacentMotionSeconds, "seconds");
        foreach (var status in Enum.GetValues<PointStatus>())
        {
            var key = status.ToString().ToLowerInvariant();
            baseline.StatusCounts.TryGetValue(key, out var baselineCount);
            scenario.StatusCounts.TryGetValue(key, out var scenarioCount);
            Add(rows, "status_count_" + key, baselineCount, scenarioCount, "frames");
        }
        return rows;
    }

    private static void Add(List<EndpointDifference> rows, string name, double? baseline, double? scenario, string unit)
    {
        if (CompletedRunContract.WithheldBiologicalEndpoints.Contains(name, StringComparer.Ordinal))
            throw new InvalidDataException($"Biological endpoint difference '{name}' is withheld without a confirmed window contract.");
        rows.Add(new EndpointDifference
        {
            Name = name,
            Baseline = baseline,
            Scenario = scenario,
            Difference = baseline.HasValue && scenario.HasValue ? scenario.Value - baseline.Value : null,
            Unit = unit
        });
    }
}

internal static class ResumeState
{
    public static void VerifyAndAdopt(string output, ReplayManifestDocument manifest, ResolvedPlan plan, IReplayTracker tracker, string codeIdentitySha256)
    {
        if (manifest.Cases.Count != plan.Recordings.Count * plan.Scenarios.Count)
            throw new PlanRejectedException("Manifest case count does not match the frozen plan. It was left unchanged.");
        var index = 0;
        foreach (var recording in plan.Recordings)
        {
            foreach (var scenario in plan.Scenarios)
            {
                var item = manifest.Cases[index];
                if (item.PlanIndex != index || item.RecordingId != recording.RecordingId || item.ScenarioName != scenario.Name)
                    throw new PlanRejectedException("Manifest case order does not match the frozen plan. It was left unchanged.");
                VerifyCase(output, item, recording, scenario, plan, tracker, codeIdentitySha256);
                index++;
            }
        }
        var casesRoot = Path.Combine(output, "cases");
        if (!Directory.Exists(casesRoot)) return;
        if (Directory.GetFiles(casesRoot).Length > 0)
            throw new PlanRejectedException("The cases folder contains unexpected files. They were left unchanged.");
        var expectedRecordings = plan.Recordings.Select(recording => recording.RecordingId).ToHashSet(StringComparer.Ordinal);
        foreach (var folder in Directory.GetDirectories(casesRoot))
            if (!expectedRecordings.Contains(Path.GetFileName(folder)))
                throw new PlanRejectedException($"Unexpected recording folder '{Path.GetFileName(folder)}' was left unchanged.");
    }

    private static void VerifyCase(string output, ManifestCase item, ResolvedRecording recording, ReplayPlanScenario scenario, ResolvedPlan plan, IReplayTracker tracker, string codeIdentitySha256)
    {
        var root = ReplayPaths.CaseRoot(output, recording.RecordingId, scenario.Name!);
        var listed = item.Attempts.Select(attempt => attempt.Index).ToList();
        for (var position = 0; position < listed.Count; position++)
            if (listed[position] != position + 1)
                throw new PlanRejectedException($"Attempts for '{recording.RecordingId}/{scenario.Name}' are not a dense 1-based sequence.");
        if (!Directory.Exists(root))
        {
            if (item.Attempts.Count > 0) throw new PlanRejectedException($"Published attempts for '{recording.RecordingId}/{scenario.Name}' are missing. Output was left unchanged.");
            if (item.Status is not (ReplayStatuses.Unrun)) throw new PlanRejectedException($"Case '{recording.RecordingId}/{scenario.Name}' has status '{item.Status}' without an attempt.");
            return;
        }
        if (Directory.GetFiles(root).Length > 0)
            throw new PlanRejectedException($"Case folder '{recording.RecordingId}/{scenario.Name}' contains unexpected files. They were left unchanged.");
        var folders = Directory.GetDirectories(root).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToList();
        var expectedNames = listed.Select(value => FormattableString.Invariant($"attempt-{value:0000}")).ToHashSet(StringComparer.Ordinal);
        var nextName = FormattableString.Invariant($"attempt-{listed.Count + 1:0000}");
        foreach (var folder in folders!)
            if (!expectedNames.Contains(folder!) && folder != nextName)
                throw new PlanRejectedException($"Unexpected attempt folder '{folder}' was left unchanged.");
        foreach (var attempt in item.Attempts)
        {
            var directory = RequireAttemptDirectory(output, recording.RecordingId, scenario.Name!, attempt);
            VerifyFiles(directory, attempt.Files, tracker);
            EnsureAllowed(attempt.Status, attempt.Files);
            VerifyCaseFile(directory, attempt, recording, scenario, plan, tracker, requireContract: attempt.Status == ReplayStatuses.Completed, codeIdentitySha256);
        }
        if (item.Status is ReplayStatuses.Completed or ReplayStatuses.Failed)
        {
            if (folders.Contains(nextName))
                throw new PlanRejectedException($"Terminal case '{recording.RecordingId}/{scenario.Name}' has an extra attempt folder. It was left unchanged.");
            return;
        }
        if (!folders.Contains(nextName))
        {
            if (item.Attempts.Count == 0 && item.Status != ReplayStatuses.Unrun)
                throw new PlanRejectedException($"Case '{recording.RecordingId}/{scenario.Name}' is '{item.Status}' without an attempt.");
            if (item.Attempts.Count > 0 && item.Status != ReplayStatuses.Cancelled)
                throw new PlanRejectedException($"Case '{recording.RecordingId}/{scenario.Name}' does not match its attempt history.");
            return;
        }
        Adopt(output, item, recording, scenario, plan, tracker, listed.Count + 1, codeIdentitySha256);
    }

    private static void Adopt(string output, ManifestCase item, ResolvedRecording recording, ReplayPlanScenario scenario, ResolvedPlan plan, IReplayTracker tracker, int index, string codeIdentitySha256)
    {
        var directory = ReplayPaths.AttemptDirectory(output, recording.RecordingId, scenario.Name!, index);
        var record = ReadCase(directory);
        if (record.Index != index || record.RecordingId != recording.RecordingId || record.ScenarioName != scenario.Name || record.PlanSha256 != plan.Sha256
            || record.SourceSha256 != recording.SourceSha256 || record.BaselineSha256 != recording.BaselineSha256 || record.AlgorithmContract != AlgorithmIdentity.Contract
            || !string.Equals(record.CodeIdentitySha256, codeIdentitySha256, StringComparison.Ordinal))
            throw new PlanRejectedException($"Unpublished attempt '{recording.RecordingId}/{scenario.Name}' does not match the frozen plan or code identity. It was left unchanged.");
        var files = Directory.GetFiles(directory).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var listed = record.Files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        if (!files.SetEquals(listed.Append("replay-case.json")))
            throw new PlanRejectedException($"Unpublished attempt '{recording.RecordingId}/{scenario.Name}' files do not match its case record. They were left unchanged.");
        foreach (var file in record.Files)
        {
            var hash = tracker.HashFile(Path.Combine(directory, file.Name));
            if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                throw new PlanRejectedException($"Unpublished attempt file '{file.Name}' changed. It was left unchanged.");
        }
        EnsureAllowed(record.Status, record.Files.Append(new ReplayHashedFile { Name = "replay-case.json", Sha256 = "" }).ToList());
        AnalysisRun? run = null;
        List<EndpointDifference>? differences = null;
        Measurements? metrics = null;
        if (record.Status == ReplayStatuses.Completed)
        {
            run = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(Path.Combine(directory, "analysis.json")));
            var applied = ReplayPlanLoader.Apply(recording.BaselineSettings, scenario);
            CompletedRunContract.Ensure(run, recording.Metadata, applied, recording.SourceSha256, AlgorithmIdentity.Contract, CompletedRunContract.SoftwareVersion);
            differences = EndpointChanges.Build(recording.Baseline.Metrics, run.Metrics);
            metrics = run.Metrics;
        }
        else if (record.Status is not (ReplayStatuses.Failed or ReplayStatuses.Cancelled))
            throw new PlanRejectedException("An unpublished attempt has an unknown status. It was left unchanged.");
        item.Attempts.Add(new ManifestAttempt
        {
            Index = index,
            Status = record.Status,
            Published = record.Published,
            RelativeDirectory = ReplayPaths.RelativeAttempt(recording.RecordingId, scenario.Name!, index),
            RunId = record.RunId,
            ErrorType = record.ErrorType,
            ErrorMessage = record.ErrorMessage,
            Files = Directory.GetFiles(directory).Select(path => new ReplayHashedFile { Name = Path.GetFileName(path)!, Sha256 = tracker.HashFile(path) }).OrderBy(file => file.Name, StringComparer.Ordinal).ToList()
        });
        item.Status = record.Status;
        item.Published = record.Published;
        item.Reason = "adopted_unpublished_attempt";
        item.ErrorType = record.ErrorType;
        item.ErrorMessage = record.ErrorMessage;
        item.EndpointDifferences = differences;
        item.ScenarioMetrics = metrics;
        _ = run;
    }

    private static void VerifyCaseFile(string directory, ManifestAttempt attempt, ResolvedRecording recording, ReplayPlanScenario scenario, ResolvedPlan plan, IReplayTracker tracker, bool requireContract, string codeIdentitySha256)
    {
        var record = ReadCase(directory);
        if (record.Index != attempt.Index || record.Status != attempt.Status || record.Published != attempt.Published || record.RecordingId != recording.RecordingId || record.ScenarioName != scenario.Name
            || record.PlanSha256 != plan.Sha256 || record.SourceSha256 != recording.SourceSha256 || record.BaselineSha256 != recording.BaselineSha256
            || !string.Equals(record.CodeIdentitySha256, codeIdentitySha256, StringComparison.Ordinal))
            throw new PlanRejectedException($"Attempt {attempt.Index} for '{recording.RecordingId}/{scenario.Name}' does not match the frozen identity. It was left unchanged.");
        if (requireContract)
        {
            var run = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(Path.Combine(directory, "analysis.json")));
            var applied = ReplayPlanLoader.Apply(recording.BaselineSettings, scenario);
            try { CompletedRunContract.Ensure(run, recording.Metadata, applied, recording.SourceSha256, AlgorithmIdentity.Contract, CompletedRunContract.SoftwareVersion); }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            { throw new PlanRejectedException($"Published analysis no longer satisfies the complete-run contract: {exception.Message} It was left unchanged."); }
            if (!string.Equals(run.Id, attempt.RunId, StringComparison.Ordinal))
                throw new PlanRejectedException("Published analysis identity changed. It was left unchanged.");
        }
        _ = tracker;
    }

    private static ReplayAttemptRecord ReadCase(string directory)
    {
        var path = Path.Combine(directory, "replay-case.json");
        if (!File.Exists(path)) throw new PlanRejectedException($"Attempt is missing replay-case.json: {directory}");
        try { return JsonDefaults.Deserialize<ReplayAttemptRecord>(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
        { throw new PlanRejectedException($"Attempt case file is unreadable: {directory}"); }
    }

    private static string RequireAttemptDirectory(string output, string recordingId, string scenario, ManifestAttempt attempt)
    {
        if (string.IsNullOrWhiteSpace(attempt.RelativeDirectory) || attempt.RelativeDirectory.Contains('\\', StringComparison.Ordinal) || Path.IsPathRooted(attempt.RelativeDirectory))
            throw new PlanRejectedException("Manifest attempt path is unsafe. Output was left unchanged.");
        var parts = attempt.RelativeDirectory.Split('/');
        if (parts.Any(part => part is "" or "." or "..")) throw new PlanRejectedException("Manifest attempt path is unsafe. Output was left unchanged.");
        var listed = Path.GetFullPath(Path.Combine([output, .. parts]));
        var expected = Path.GetFullPath(ReplayPaths.AttemptDirectory(output, recordingId, scenario, attempt.Index));
        if (!string.Equals(listed, expected, StringComparison.OrdinalIgnoreCase) || !ReplayPaths.IsInside(listed, output))
            throw new PlanRejectedException("Manifest attempt path does not match its recording, scenario, and index.");
        return expected;
    }

    private static void VerifyFiles(string directory, List<ReplayHashedFile> files, IReplayTracker tracker)
    {
        if (files.Count == 0) throw new PlanRejectedException("Published attempt has no file hashes. Output was left unchanged.");
        var names = files.Select(file => file.Name).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) throw new PlanRejectedException("Attempt file list has duplicate names.");
        var onDisk = Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var listed = names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!onDisk.SequenceEqual(listed, StringComparer.Ordinal))
            throw new PlanRejectedException($"Attempt files at '{directory}' do not match the manifest. They were left unchanged.");
        foreach (var file in files)
        {
            if (file.Name.Length == 0 || file.Name is "." or ".." || file.Name.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new PlanRejectedException("Attempt file name is unsafe.");
            var hash = tracker.HashFile(Path.Combine(directory, file.Name));
            if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                throw new PlanRejectedException($"Attempt file '{file.Name}' changed after publication. It was left unchanged.");
        }
    }

    public static void EnsureAllowed(string status, IReadOnlyList<ReplayHashedFile> files)
    {
        var names = files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        bool Has(string name) => names.Contains(name);
        if (status == ReplayStatuses.Completed)
        {
            if (!Has("replay-case.json") || !Has("analysis.json") || !Has("coordinates.csv") || !Has("report.html") || !Has("measurement-scope.txt") || names.Count != 5)
                throw new PlanRejectedException("A completed attempt must contain only replay-case.json, analysis.json, coordinates.csv, report.html, and measurement-scope.txt.");
        }
        else if (status == ReplayStatuses.Failed)
        {
            if (!Has("replay-case.json") || Has("analysis.json") || Has("coordinates.csv") || Has("report.html") || names.Any(name => name is not ("replay-case.json" or "rejected-analysis.json")))
                throw new PlanRejectedException("A failed attempt must not be published as a completed analysis.");
        }
        else if (status == ReplayStatuses.Cancelled)
        {
            if (names.Count != 1 || !Has("replay-case.json"))
                throw new PlanRejectedException("A cancelled attempt must contain only replay-case.json.");
        }
        else throw new PlanRejectedException($"Attempt status '{status}' is unknown.");
    }
}
