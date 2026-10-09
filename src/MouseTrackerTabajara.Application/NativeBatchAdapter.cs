using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using MouseTrackerTabajara.Batching;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;

namespace MouseTrackerTabajara.Application;

public static class RecordingAttempts
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Interrupted = "Interrupted";
    public const string InvalidEvidence = "InvalidEvidence";
    public const string Completed = "Completed";
    public const string Reused = "Reused";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string AuditWarning = "AuditWarning";
    public const string Inconsistent = "Inconsistent";
    public const string NotAnalyzed = "NotAnalyzed";
    public const string CountInterpretation = BatchRunResult<int>.CountInterpretation;
}

public sealed record NativeBatchRequest
{
    public const int DefaultParallelism = BatchEngineOptions.DefaultMaxDegreeOfParallelism;
    public static NativeBatchRequest Default { get; } = new();
    public int MaxDegreeOfParallelism { get; init; } = DefaultParallelism;
    public int? ResourceBudget { get; init; }
    public TimeSpan ProgressMinimumInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public IRecordingAnalysisDecoder? Decoder { get; init; }
    public IRecordingSourceHasher? Hasher { get; init; }
    public IRecordingRunPublisher? Publisher { get; init; }
    public Func<VideoAsset, CancellationToken, ValueTask<IAsyncDisposable?>>? OpenResource { get; init; }
    public IProgress<RecordingBatchProgress>? RecordingProgress { get; init; }
}

public interface IRecordingAnalysisDecoder
{
    AnalysisRun Analyze(VideoAsset frozenAsset, string resolvedPath, IProgress<double>? progress, CancellationToken cancellation);
}

public interface IRecordingSourceHasher
{
    string HashFile(string path);
}

public interface IRecordingRunPublisher
{
    /// <summary>Throw only when the run was not stored. A throw after commit is a post-commit failure.</summary>
    ValueTask PublishAsync(AnalysisRun run, CancellationToken cancellation);
}

public sealed class StudyBatchInProgressException : InvalidOperationException
{
    public StudyBatchInProgressException(string databasePath)
        : base("Another app process is analyzing this study. The overlapping request did not start decoders, publication or attempt history.")
    {
        DatabasePath = databasePath;
        ActiveDecoderLimit = 0; // The other process's decoder budget is not inferred.
    }
    public StudyBatchInProgressException(string databasePath, int activeDecoderLimit)
        : base($"This study is already being analyzed ({databasePath}) with a decoder limit of {activeDecoderLimit}. The overlapping request was rejected and did not start decoders or publication.")
    {
        DatabasePath = databasePath;
        ActiveDecoderLimit = activeDecoderLimit;
    }

    public string DatabasePath { get; }
    public int ActiveDecoderLimit { get; }
}

public sealed record RecordingBatchProgress
{
    public required string VideoId { get; init; }
    public required int InputIndex { get; init; }
    public required BatchItemState ItemState { get; init; }
    public double? ItemPercent { get; init; }
    public string? ItemMessage { get; init; }
    public required int TotalRecordings { get; init; }
    public required int CompletedRecordings { get; init; }
    public required int ReusedRecordings { get; init; }
    public required int FailedRecordings { get; init; }
    public required int CancelledRecordings { get; init; }
    public required int RunningRecordings { get; init; }
    public required double CollectionPercent { get; init; }
    public required string CountInterpretation { get; init; }
}

public sealed record RecordingBatchEvidence
{
    public required string VideoId { get; init; }
    public required int InputIndex { get; init; }
    public required BatchItemState State { get; init; }
    public string? RunId { get; init; }
    public string? Error { get; init; }
    public string? Warning { get; init; }
    public string? DisposalError { get; init; }
    public string? ProgressObserverError { get; init; }
    public bool Published { get; init; }
    public bool CompletedAnalysis { get; init; }
    public int? CompletionOrdinal { get; init; }
}

public sealed record PersistedRecordingEvidence
{
    public required string VideoId { get; init; }
    public required string LatestAttempt { get; init; }
    public required bool LatestAttemptIsCompletedAnalysis { get; init; }
    public string? MatchingRunId { get; init; }
    public bool MatchingRunIntact { get; init; }
    public string? HistoricalRunId { get; init; }
    public bool HistoricalRunIntact { get; init; }
    public string? StoredRunValidationError { get; init; }
    public bool CurrentReuseEligible { get; init; }
    public string? ReuseIneligibilityReason { get; init; }
    public IReadOnlyList<RecordingAttemptEvidence> History { get; init; } = [];
    public string? Error { get; init; }
    public string? Warning { get; init; }
    public string? DisposalError { get; init; }
    public string? ProgressObserverError { get; init; }
    public string? Note { get; init; }
    public string CountNote { get; init; } = "Recording operation evidence. Counts are not animals or group results.";
}

public sealed class NativeBatchAdapter
{
    private readonly StudyStore _store;

    public NativeBatchAdapter(StudyStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public static IReadOnlyList<PersistedRecordingEvidence> ReadEvidence(StudyStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return RecordingOperationJournal.Read(store);
    }

    public async Task<BatchOutcome> RunAsync(
        IEnumerable<VideoAsset> assets,
        IProgress<BatchProgress>? progress,
        NativeBatchRequest request,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var cap = Math.Min(request.MaxDegreeOfParallelism, request.ResourceBudget ?? request.MaxDegreeOfParallelism);
        var batchId = Guid.NewGuid().ToString("N");
        using var exclusive = StudyBatchGate.Enter(_store.DatabasePath, cap, batchId);
        var snapshot = Snapshot(assets);
        var frozen = snapshot.Select(Freeze).ToArray();
        var byId = frozen.ToDictionary(item => item.VideoId, StringComparer.Ordinal);
        var writeLock = StudyWriteGate.For(_store.DatabasePath);
        var warnings = new List<string>();
        Audit(writeLock, "batch_started", new { Videos = frozen.Select(item => item.VideoId), Count = frozen.Length }, warnings);

        var journal = new RecordingOperationJournal(_store.DatabasePath);
        var coordinator = new RecordingProgressCoordinator(
            frozen.Select((item, index) => new FrozenRecordingIdentity(item.VideoId, index, item.Filename)).ToArray(),
            progress,
            request.RecordingProgress,
            request.ProgressMinimumInterval);
        var resolvedPaths = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var auditWarnings = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var journalFailures = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var lastTransitions = new ConcurrentDictionary<string, JournalEntry>(StringComparer.Ordinal);
        var knownRuns = new ConcurrentDictionary<string, AnalysisRun>(StringComparer.Ordinal);
        var attemptIds = frozen.ToDictionary(item => item.VideoId, _ => Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
        using var owner = System.Diagnostics.Process.GetCurrentProcess();
        var ownerStart = owner.StartTime.ToUniversalTime().Ticks;
        JournalEntry Transition(string videoId, string state, AnalysisRun? run = null, string? error = null, string? warning = null, string? disposal = null, string? observer = null)
        {
            var identity = byId[videoId];
            return new JournalEntry
            {
                AttemptId = attemptIds[videoId], BatchId = batchId, VideoId = videoId,
                SourceSha256 = identity.SourceSha256, SettingsHash = identity.SettingsHash, AlgorithmContract = identity.AlgorithmContract,
                Terminal = state, RunId = run?.Id, Error = error, Warning = warning, DisposalError = disposal, ProgressObserverError = observer,
                OwnerProcessId = Environment.ProcessId, OwnerStartUtcTicks = ownerStart, RecordedUtc = DateTime.UtcNow
            };
        }
        void Persist(JournalEntry entry)
        {
            lastTransitions[entry.VideoId] = entry;
            try { journal.Add(entry); }
            catch (Exception ex) { journalFailures[entry.VideoId] = "Operational journal transition was not saved: " + RecordingProgressCoordinator.OneLine(ex.Message); }
        }
        // Fail closed before admitting work if attempts cannot be durably registered.
        journal.AddRange(frozen.Select(item => Transition(item.VideoId, RecordingAttempts.Queued)).ToArray());
        void EngineProgress(BatchProgressUpdate update)
        {
            if (update.State is BatchItemState.Completed or BatchItemState.Reused or BatchItemState.Failed or BatchItemState.Cancelled)
            {
                knownRuns.TryGetValue(update.JobId, out var committed);
                auditWarnings.TryGetValue(update.JobId, out var warning);
                Persist(Transition(update.JobId, TerminalName(update.State, warning is not null), committed,
                    update.State is BatchItemState.Failed or BatchItemState.Cancelled ? update.Message : null, warning));
            }
            coordinator.Report(update);
            if (update.State is BatchItemState.Completed or BatchItemState.Reused or BatchItemState.Failed or BatchItemState.Cancelled && coordinator.ProgressObserverError(update.JobId) is { } observerError)
            {
                knownRuns.TryGetValue(update.JobId, out var run); auditWarnings.TryGetValue(update.JobId, out var warning);
                Persist(Transition(update.JobId, TerminalName(update.State, warning is not null), run, update.State is BatchItemState.Failed or BatchItemState.Cancelled ? update.Message : null, warning, observer: observerError));
            }
        }
        var activeDecoders = 0;
        var peakDecoders = 0;
        var hasher = request.Hasher ?? OpenCvRecordingHasher.Instance;
        var decoder = request.Decoder ?? OpenCvRecordingDecoder.Instance;
        var publisher = request.Publisher ?? new StudyStoreRunPublisher(_store);

        async ValueTask<IAsyncDisposable?> OpenDecoder(BatchExecutionContext<FrozenRecording> ctx, CancellationToken token)
        {
            var now = Interlocked.Increment(ref activeDecoders);
            int seen;
            do
            {
                seen = Volatile.Read(ref peakDecoders);
                if (now <= seen)
                    break;
            }
            while (Interlocked.CompareExchange(ref peakDecoders, now, seen) != seen);

            try
            {
                IAsyncDisposable? extra = null;
                if (request.OpenResource is not null)
                    extra = await request.OpenResource(ctx.Input.Asset, token).ConfigureAwait(false);
                return new DecoderScope(() => Interlocked.Decrement(ref activeDecoders), extra, ex =>
                {
                    if (lastTransitions.TryGetValue(ctx.JobId, out var latest))
                        Persist(latest with { DisposalError = RecordingProgressCoordinator.OneLine(ex.Message), RecordedUtc = DateTime.UtcNow });
                });
            }
            catch
            {
                Interlocked.Decrement(ref activeDecoders);
                throw;
            }
        }

        var engine = new BatchEngine(new BatchEngineOptions
        {
            MaxDegreeOfParallelism = request.MaxDegreeOfParallelism,
            FailurePolicy = BatchFailurePolicy.Continue,
            AcquireResource = request.ResourceBudget is int budget ? ResourceBudget(budget) : null
        });

        var batch = await engine.RunAsync(new BatchRequest<FrozenRecording, AnalysisRun>
        {
            Jobs = frozen.Select(item => new BatchJob<FrozenRecording>(item.VideoId, item)).ToArray(),
            ResolveIdentity = job => { journal.Add(Transition(job.Id, RecordingAttempts.Running)); return ResolveIdentity(job.Input, hasher, resolvedPaths, writeLock); },
            Cache = new StoreRunCache(_store, writeLock, byId, run => knownRuns[run.VideoId] = run,
                (videoId, run, reason) => { var warning = "Invalid cached evidence was not reused: " + reason; auditWarnings[videoId] = warning; Persist(Transition(videoId, RecordingAttempts.InvalidEvidence, run, reason, warning)); }),
            OpenResource = OpenDecoder,
            Process = (ctx, token) =>
            {
                var run = Decode(ctx, decoder, hasher, resolvedPaths, token);
                return ValueTask.FromResult(BatchWorkResult<AnalysisRun>.Complete(run));
            },
            Publish = (publication, token) => PublishAsync(publication, byId, publisher, writeLock, auditWarnings, journal, run => knownRuns[run.VideoId] = run, (publication, warning) => Transition(publication.JobId, warning is null ? RecordingAttempts.Completed : RecordingAttempts.AuditWarning, publication.Result, warning: warning), token),
            Progress = new InlineProgress<BatchProgressUpdate>(EngineProgress)
        }, cancellation).ConfigureAwait(false);

        var recordings = new List<RecordingBatchEvidence>(batch.Items.Count);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var journalEntries = new List<JournalEntry>(batch.Items.Count);
        foreach (var item in batch.Items)
        {
            var source = byId[item.JobId];
            auditWarnings.TryGetValue(item.JobId, out var auditWarning);

            var message = item.State switch
            {
                BatchItemState.Failed => item.Error,
                BatchItemState.Cancelled => item.Error,
                _ => item.Result is null ? null : "Completed"
            };
            coordinator.ReportFinal(item.JobId, item.State, message, item.LastValidPercent);
            var observerError = coordinator.ProgressObserverError(item.JobId);
            if (journalFailures.TryGetValue(item.JobId, out var journalFailure)) warnings.Add(item.JobId + ": " + journalFailure);
            if (!string.IsNullOrWhiteSpace(auditWarning))
                warnings.Add(item.JobId + ": " + auditWarning);
            if (!string.IsNullOrWhiteSpace(item.DisposalError))
                warnings.Add(item.JobId + ": decoder release failed: " + item.DisposalError);
            if (!string.IsNullOrWhiteSpace(observerError))
                warnings.Add(item.JobId + ": progress observer failed: " + observerError);
            if (item.State == BatchItemState.Failed)
                errors[item.JobId] = item.Error ?? "Analysis failed.";

            var completedAnalysis = item.Result is not null
                && !string.IsNullOrWhiteSpace(item.Result.Id)
                && item.State is BatchItemState.Completed or BatchItemState.Reused;
            recordings.Add(new RecordingBatchEvidence
            {
                VideoId = item.JobId,
                InputIndex = item.InputIndex,
                State = item.State,
                RunId = item.Result?.Id,
                Error = item.Error,
                Warning = auditWarning,
                DisposalError = item.DisposalError,
                ProgressObserverError = observerError,
                Published = item.Published,
                CompletedAnalysis = completedAnalysis,
                CompletionOrdinal = item.CompletionOrdinal
            });
            journalEntries.Add(Transition(item.JobId, TerminalName(item.State, auditWarning is not null), item.Result, item.Error, auditWarning, item.DisposalError, observerError));
        }

        try
        {
            journal.AddRange(journalEntries);
        }
        catch (Exception ex)
        {
            warnings.Add("Operational journal was not saved: " + RecordingProgressCoordinator.OneLine(ex.Message));
        }

        foreach (var failed in errors)
            Audit(writeLock, "analysis_failed", new { Id = failed.Key, Error = failed.Value }, warnings);

        var outcome = new BatchOutcome(batch.CompletedCount + batch.ReusedCount, batch.ReusedCount, batch.CancellationRequested, errors)
        {
            Recordings = recordings,
            Warnings = warnings.ToArray(),
            MaxDegreeOfParallelism = request.MaxDegreeOfParallelism,
            EffectiveDecoderCap = cap,
            ObservedMaximumActiveJobs = batch.ObservedMaximumActiveJobs,
            ObservedMaximumActiveDecoders = Volatile.Read(ref peakDecoders),
            ActiveDecodersAtCompletion = Volatile.Read(ref activeDecoders),
            CountInterpretation = batch.CountInterpretationText
        };
        try
        {
            lock (writeLock)
            {
                _store.Audit("batch_finished", new
                {
                    outcome.Completed,
                    outcome.Reused,
                    outcome.Cancelled,
                    outcome.Errors,
                    WarningCount = outcome.Warnings.Count,
                    outcome.ObservedMaximumActiveDecoders,
                    outcome.EffectiveDecoderCap
                });
            }
        }
        catch (Exception ex)
        {
            // The runs are already committed. Losing the closing audit must not hide them.
            warnings.Add("batch_finished audit failed: " + RecordingProgressCoordinator.OneLine(ex.Message));
            outcome = outcome with { Warnings = warnings.ToArray() };
        }

        return outcome;
    }

    private static void Validate(NativeBatchRequest request)
    {
        if (request.MaxDegreeOfParallelism < BatchEngineOptions.MinimumMaxDegreeOfParallelism)
            throw new ArgumentOutOfRangeException(nameof(request.MaxDegreeOfParallelism), request.MaxDegreeOfParallelism, "Maximum active recordings must be at least 1.");
        if (request.ResourceBudget is < 1)
            throw new ArgumentOutOfRangeException(nameof(request.ResourceBudget), request.ResourceBudget, "Resource budget must be at least 1 when it is set.");
        if (request.ProgressMinimumInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request.ProgressMinimumInterval), request.ProgressMinimumInterval, "Progress interval cannot be negative.");
    }

    private static VideoAsset[] Snapshot(IEnumerable<VideoAsset> assets)
    {
        var list = assets.ToArray();
        if (list.Any(asset => asset is null))
            throw new ArgumentException("A recording in the batch is null.", nameof(assets));
        return list.DistinctBy(asset => asset.Id).ToArray();
    }

    internal static FrozenRecording Freeze(VideoAsset asset)
    {
        if (asset.Id is null || string.IsNullOrWhiteSpace(asset.Id) || !string.Equals(asset.Id, asset.Id.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Each recording needs a stable id without surrounding whitespace.");
        var settings = CopySettings(asset.Settings);
        var frozenAsset = new VideoAsset
        {
            Id = asset.Id,
            Path = asset.Path,
            SourceSha256 = asset.SourceSha256,
            AnimalId = asset.AnimalId,
            BlindedAlias = asset.BlindedAlias,
            AnimalIdConfirmed = asset.AnimalIdConfirmed,
            GroupName = asset.GroupName,
            Phase = asset.Phase,
            Day = asset.Day,
            Trial = asset.Trial,
            Included = asset.Included,
            ExclusionReason = asset.ExclusionReason,
            Metadata = asset.Metadata,
            Settings = settings
        };
        return new FrozenRecording
        {
            VideoId = asset.Id,
            Path = asset.Path ?? "",
            SourceSha256 = asset.SourceSha256 ?? "",
            Filename = string.IsNullOrEmpty(asset.Path) ? "" : Path.GetFileName(asset.Path),
            Metadata = asset.Metadata,
            Settings = settings,
            SettingsHash = StudyStore.SettingsHash(settings),
            AlgorithmContract = AlgorithmIdentity.Contract,
            Asset = frozenAsset
        };
    }

    internal static TrackingSettings CopySettings(TrackingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var source = settings.Exclusions;
        ExclusionRegion[] snapshot;
        try
        {
            snapshot = source is null ? [] : source.ToArray();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("Settings changed while they were being frozen.", ex);
        }

        var exclusions = new List<ExclusionRegion>(snapshot.Length);
        foreach (var region in snapshot)
            exclusions.Add(new ExclusionRegion(region.Left, region.Top, region.Right, region.Bottom));
        return settings with
        {
            Platform = settings.Platform is null ? null : new PlatformSite(settings.Platform.X, settings.Platform.Y, settings.Platform.Radius),
            Exclusions = exclusions
        };
    }

    private CacheIdentity ResolveIdentity(FrozenRecording frozen, IRecordingSourceHasher hasher, ConcurrentDictionary<string, string> resolvedPaths, object writeLock)
    {
        try
        {
            frozen.Settings.Validate();
            _ = CompletedRecordingContract.Interval(frozen.Metadata, frozen.Settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArgumentException("Frozen settings are invalid. " + ex.Message, ex);
        }

        var path = Locate(frozen, hasher, writeLock);
        var hash = hasher.HashFile(path);
        if (frozen.SourceSha256.Length > 0 && !string.Equals(hash, frozen.SourceSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The original recording changed. Import and review the changed source before analysis.");
        resolvedPaths[frozen.VideoId] = path;
        return new CacheIdentity(hash, frozen.SettingsHash, frozen.AlgorithmContract);
    }

    private string Locate(FrozenRecording frozen, IRecordingSourceHasher hasher, object writeLock)
    {
        if (!string.IsNullOrWhiteSpace(frozen.Path) && File.Exists(frozen.Path))
            return frozen.Path;

        VideoAsset? stored;
        lock (writeLock)
            stored = _store.Videos().FirstOrDefault(video => string.Equals(video.Id, frozen.VideoId, StringComparison.Ordinal));
        if (stored is not null
            && !string.IsNullOrWhiteSpace(stored.Path)
            && File.Exists(stored.Path)
            && !SamePath(stored.Path, frozen.Path))
        {
            var hash = hasher.HashFile(stored.Path);
            if (frozen.SourceSha256.Length > 0 && !string.Equals(hash, frozen.SourceSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Relinked source does not match the frozen recording hash.");
            return stored.Path;
        }

        throw new FileNotFoundException("Original video is missing.", frozen.Path);
    }

    private static AnalysisRun Decode(
        BatchExecutionContext<FrozenRecording> context,
        IRecordingAnalysisDecoder decoder,
        IRecordingSourceHasher hasher,
        ConcurrentDictionary<string, string> resolvedPaths,
        CancellationToken cancellation)
    {
        if (!resolvedPaths.TryGetValue(context.Input.VideoId, out var path))
            throw new InvalidOperationException("The recording path was not resolved before decoding.");
        var bridge = new InlineProgress<double>(percent => context.Progress.Report(new BatchItemProgressReport(percent, "Processing")));
        var run = decoder.Analyze(context.Input.Asset, path, bridge, cancellation);
        var after = hasher.HashFile(path);
        if (!string.Equals(after, context.Identity.Source, StringComparison.Ordinal))
            throw new InvalidDataException("Recording changed during processing; this run was not published.");
        EnsurePublishable(context.Input, context.Identity, run);
        return run;
    }

    private async ValueTask PublishAsync(
        BatchPublication<AnalysisRun> publication,
        IReadOnlyDictionary<string, FrozenRecording> frozenById,
        IRecordingRunPublisher publisher,
        object writeLock,
        ConcurrentDictionary<string, string> auditWarnings,
        RecordingOperationJournal journal,
        Action<AnalysisRun> onStored,
        Func<BatchPublication<AnalysisRun>, string?, JournalEntry> publicationEntry,
        CancellationToken cancellation)
    {
        var frozen = frozenById[publication.JobId];
        EnsurePublishable(frozen, publication.Identity, publication.Result);
        Exception? error = null;
        try
        {
            await publisher.PublishAsync(publication.Result, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        var stored = false;
        lock (writeLock)
            stored = IsStored(publication.Result);

        if (stored)
        {
            onStored(publication.Result);
            string? warning = null;
            if (error is not null)
            {
                // StudyStore commits the run and then writes the audit. The engine treats every
                // publisher exception as "not stored", so a post-commit failure must not escape.
                warning = "A committed run is already stored and will not be published again. Post-commit failure: " + RecordingProgressCoordinator.OneLine(error.Message);
                auditWarnings[publication.JobId] = warning;
                try
                {
                    lock (writeLock)
                        _store.Audit("analysis_audit_warning", new { publication.Result.Id, publication.JobId, Warning = warning });
                }
                catch (Exception auditEx)
                {
                    warning += " The warning audit also failed: " + RecordingProgressCoordinator.OneLine(auditEx.Message);
                    auditWarnings[publication.JobId] = warning;
                }
            }

            try
            {
                journal.Add(publicationEntry(publication, warning));
            }
            catch (Exception journalEx)
            {
                // The run is already stored. A journal failure must not make the engine reject it.
                var journalWarning = "Operational journal was not saved: " + RecordingProgressCoordinator.OneLine(journalEx.Message);
                auditWarnings[publication.JobId] = warning is null ? journalWarning : warning + " " + journalWarning;
            }

            return;
        }

        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
        throw new InvalidOperationException("Publication returned without the exact validated stored run " + publication.Result.Id + ".");
    }

    private bool IsStored(AnalysisRun run)
    {
        var stored = StoredRecordingRuns.Find(_store, run.VideoId, run.Id).Run;
        return stored is not null && CompletedRecordingContract.TryValidate(stored, out _)
            && string.Equals(JsonDefaults.Serialize(stored), JsonDefaults.Serialize(run), StringComparison.Ordinal);
    }

    private static void EnsurePublishable(FrozenRecording frozen, CacheIdentity identity, AnalysisRun? run)
    {
        if (run is null) throw new InvalidDataException("Incomplete analysis was not published.");
        CompletedRecordingContract.Validate(run);
        if (!string.Equals(run.VideoId, frozen.VideoId, StringComparison.Ordinal))
            throw new InvalidDataException("Analysis result is associated with a different recording.");
        if (!string.Equals(run.SourceSha256, identity.Source, StringComparison.Ordinal))
            throw new InvalidDataException("Analysis result source hash does not match the frozen source.");
        if (frozen.SourceSha256.Length > 0 && !string.Equals(run.SourceSha256, frozen.SourceSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Analysis result source hash does not match the frozen source.");
        if (!string.Equals(run.AlgorithmContract, identity.Algorithm, StringComparison.Ordinal)
            || !string.Equals(run.AlgorithmContract, frozen.AlgorithmContract, StringComparison.Ordinal))
            throw new InvalidDataException("Analysis result algorithm does not match the frozen algorithm.");
        var settingsHash = StudyStore.SettingsHash(run.Settings);
        if (!string.Equals(settingsHash, identity.Settings, StringComparison.Ordinal)
            || !string.Equals(settingsHash, frozen.SettingsHash, StringComparison.Ordinal))
            throw new InvalidDataException("Analysis result settings do not match the frozen settings.");
        if (run.Metadata != frozen.Metadata)
            throw new InvalidDataException("Analysis metadata differs from the registered recording.");
    }

    private static BatchResourceBudgetHook ResourceBudget(int budget) => (snapshot, _) =>
    {
        if (snapshot.ActiveJobs >= budget)
            return new ValueTask<IAsyncDisposable?>((IAsyncDisposable?)null);
        return new ValueTask<IAsyncDisposable?>(new NoopLease());
    };

    private void Audit(object writeLock, string action, object details, List<string> warnings)
    {
        try
        {
            lock (writeLock)
                _store.Audit(action, details);
        }
        catch (Exception ex)
        {
            warnings.Add(action + " audit failed: " + RecordingProgressCoordinator.OneLine(ex.Message));
        }
    }

    private static string TerminalName(BatchItemState state, bool auditWarning) => auditWarning && state == BatchItemState.Completed
        ? RecordingAttempts.AuditWarning
        : state switch
        {
            BatchItemState.Completed => RecordingAttempts.Completed,
            BatchItemState.Reused => RecordingAttempts.Reused,
            BatchItemState.Failed => RecordingAttempts.Failed,
            BatchItemState.Cancelled => RecordingAttempts.Cancelled,
            _ => RecordingAttempts.Inconsistent
        };

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class DecoderScope(Action release, IAsyncDisposable? inner, Action<Exception> onDisposalError) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            Exception? error = null;
            if (inner is not null)
            {
                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    error = ex;
                    onDisposalError(ex);
                }
            }

            if (Interlocked.Exchange(ref _released, 1) == 0)
                release();
            if (error is not null)
                ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StoreRunCache(StudyStore store, object gate, IReadOnlyDictionary<string, FrozenRecording> frozen, Action<AnalysisRun> onFound, Action<string, AnalysisRun, string> onInvalid) : IBatchCompletionCache<AnalysisRun>
    {
        public BatchCachedCompletion<AnalysisRun>? Find(string jobId, CacheIdentity requiredIdentity)
        {
            if (!frozen.TryGetValue(jobId, out var recording))
                return null;
            AnalysisRun? existing;
            lock (gate)
                existing = store.LatestRun(recording.VideoId, recording.Settings);
            if (existing is null) return null;
            if (!CompletedRecordingContract.TryValidate(existing, out var reason)) { onInvalid(recording.VideoId, existing, reason!); return null; }
            if (existing.VideoId != recording.VideoId) { onInvalid(recording.VideoId, existing, "Stored run is associated with another recording."); return null; }
            if (existing.Metadata != recording.Metadata) return null;
            var identity = new CacheIdentity(existing.SourceSha256, StudyStore.SettingsHash(existing.Settings), existing.AlgorithmContract);
            if (identity.Matches(requiredIdentity)) onFound(existing);
            return new BatchCachedCompletion<AnalysisRun>(identity, existing);
        }
    }

    private sealed class StudyStoreRunPublisher(StudyStore store) : IRecordingRunPublisher
    {
        public ValueTask PublishAsync(AnalysisRun run, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            store.SaveRun(run);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OpenCvRecordingDecoder : IRecordingAnalysisDecoder
    {
        public static readonly OpenCvRecordingDecoder Instance = new();

        public AnalysisRun Analyze(VideoAsset frozenAsset, string resolvedPath, IProgress<double>? progress, CancellationToken cancellation)
        {
            var asset = SamePath(frozenAsset.Path, resolvedPath) ? frozenAsset : frozenAsset with { Path = resolvedPath };
            return OpenCvTracker.Analyze(asset, progress, cancellation);
        }
    }

    private sealed class OpenCvRecordingHasher : IRecordingSourceHasher
    {
        public static readonly OpenCvRecordingHasher Instance = new();
        public string HashFile(string path) => OpenCvTracker.HashFile(path);
    }
}

internal sealed record FrozenRecording
{
    public required string VideoId { get; init; }
    public required string Path { get; init; }
    public required string SourceSha256 { get; init; }
    public required string Filename { get; init; }
    public required VideoMetadata Metadata { get; init; }
    public required TrackingSettings Settings { get; init; }
    public required string SettingsHash { get; init; }
    public required string AlgorithmContract { get; init; }
    public required VideoAsset Asset { get; init; }
}

internal static class StudyBatchGate
{
    private static readonly object Sync = new();
    private sealed record ActiveBatch(int Limit, string BatchId);
    private static readonly Dictionary<string, ActiveBatch> Active = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsActive(string databasePath, string batchId) { lock (Sync) return Active.TryGetValue(Path.GetFullPath(databasePath), out var active) && active.BatchId == batchId; }

    public static IDisposable Enter(string databasePath, int decoderLimit, string batchId)
    {
        var key = Path.GetFullPath(databasePath);
        lock (Sync)
        {
            if (Active.TryGetValue(key, out var activeLimit))
                throw new StudyBatchInProgressException(key, activeLimit.Limit);
            var processLease = StudyProcessLease.Acquire(key, batchId);
            try { Active.Add(key, new ActiveBatch(decoderLimit, batchId)); }
            catch { processLease.Dispose(); throw; }
            return new Release(key, processLease);
        }
    }

    private sealed class Release(string key, FileStream processLease) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed) return;
                _disposed = true;
                Active.Remove(key);
                processLease.Dispose();
            }
        }
    }
}

internal static class StudyWriteGate
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static object For(string databasePath)
    {
        var key = Path.GetFullPath(databasePath);
        lock (Sync)
        {
            if (!Gates.TryGetValue(key, out var gate))
                Gates[key] = gate = new object();
            return gate;
        }
    }
}
