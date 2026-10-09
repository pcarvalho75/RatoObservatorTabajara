using System.Globalization;

namespace MouseTrackerTabajara.Batching;

/// <summary>
/// Terminal and in-flight states for one recording job.
/// Completed and Reused are the only successful terminals.
/// Cancelled covers caller cancellation and jobs never started after stop-on-failure.
/// </summary>
public enum BatchItemState
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Reused = 3,
    Failed = 4,
    Cancelled = 5
}

/// <summary>
/// Continue isolates a failed recording. StopScheduling finishes work already
/// admitted, then does not start anything further.
/// </summary>
public enum BatchFailurePolicy
{
    Continue = 0,
    StopScheduling = 1
}

/// <summary>
/// Caller-supplied identity. The engine does not invent ids and does not merge duplicates.
/// </summary>
public sealed record BatchJob<TInput>(string Id, TInput Input);

/// <summary>
/// Source, settings and algorithm identity compared with ordinal, case-sensitive equality.
/// A reuse is valid only when all three match.
/// </summary>
public sealed record CacheIdentity
{
    public CacheIdentity(string source, string settings, string algorithm)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source identity is required.", nameof(source));
        if (string.IsNullOrWhiteSpace(settings))
            throw new ArgumentException("Settings identity is required.", nameof(settings));
        if (string.IsNullOrWhiteSpace(algorithm))
            throw new ArgumentException("Algorithm identity is required.", nameof(algorithm));
        Source = source;
        Settings = settings;
        Algorithm = algorithm;
    }

    public string Source { get; }
    public string Settings { get; }
    public string Algorithm { get; }

    public bool Matches(CacheIdentity? other) =>
        other is not null
        && string.Equals(Source, other.Source, StringComparison.Ordinal)
        && string.Equals(Settings, other.Settings, StringComparison.Ordinal)
        && string.Equals(Algorithm, other.Algorithm, StringComparison.Ordinal);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"source={Source}; settings={Settings}; algorithm={Algorithm}");
}

/// <summary>
/// A previously stored completion. The engine reuses it only when <see cref="Identity"/>
/// matches the identity resolved for this run.
/// </summary>
public sealed record BatchCachedCompletion<TResult>(CacheIdentity Identity, TResult Result);

/// <summary>
/// Host cache. <see cref="Find"/> may be called from a worker, one call at a time.
/// Return null when nothing is stored. Returning a different identity is a miss, not a reuse.
/// </summary>
public interface IBatchCompletionCache<TResult>
{
    BatchCachedCompletion<TResult>? Find(string jobId, CacheIdentity requiredIdentity);
}

/// <summary>
/// Percent is on the closed interval [0, 100]. Values outside that interval, and
/// non-finite values, are rejected and recorded. A slack of 1e-9 is clamped into range
/// so ordinary binary rounding is not treated as corruption.
/// </summary>
public sealed record BatchItemProgressReport(double Percent, string? Status);

/// <summary>
/// What the processor returns. Only <see cref="BatchWorkResult{TResult}.Complete"/> can be published.
/// </summary>
public sealed class BatchWorkResult<TResult>
{
    private BatchWorkResult(bool isComplete, TResult? result, string? incompleteReason)
    {
        IsComplete = isComplete;
        Result = result;
        IncompleteReason = incompleteReason;
    }

    public bool IsComplete { get; }
    public TResult? Result { get; }
    public string? IncompleteReason { get; }

    public static BatchWorkResult<TResult> Complete(TResult result)
    {
        if (result is null)
            throw new ArgumentNullException(nameof(result), "A completed result cannot be null.");
        return new BatchWorkResult<TResult>(true, result, null);
    }

    public static BatchWorkResult<TResult> Incomplete(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An incomplete result needs a visible reason.", nameof(reason));
        return new BatchWorkResult<TResult>(false, default, reason.Trim());
    }
}

/// <summary>Per-job view passed to the processor and resource opener.</summary>
public sealed class BatchExecutionContext<TInput>
{
    public required string JobId { get; init; }
    public required int InputIndex { get; init; }
    public required TInput Input { get; init; }
    public required CacheIdentity Identity { get; init; }
    public required IProgress<BatchItemProgressReport> Progress { get; init; }
}

/// <summary>
/// A finished result offered for publication. Reused jobs are not published.
/// Throwing from the publisher means the run was not stored: the item becomes Failed
/// and is not counted as completed. The callback must not commit and then throw.
/// </summary>
public sealed record BatchPublication<TResult>(
    string JobId,
    int InputIndex,
    CacheIdentity Identity,
    TResult Result);

/// <summary>
/// Live collection and per-job progress. Counts are work items, not a biological sample size.
/// </summary>
public sealed record BatchProgressUpdate
{
    public required string JobId { get; init; }
    public required int InputIndex { get; init; }
    public required BatchItemState State { get; init; }
    public required int TotalJobs { get; init; }
    public required int QueuedJobs { get; init; }
    public required int RunningJobs { get; init; }
    public required int CompletedJobs { get; init; }
    public required int ReusedJobs { get; init; }
    public required int FailedJobs { get; init; }
    public required int CancelledJobs { get; init; }
    public required int ActiveJobs { get; init; }
    public required int ParallelismLimit { get; init; }
    public double? ItemPercent { get; init; }
    public string? Message { get; init; }
    public bool ProgressMalformed { get; init; }
    public string? MalformedDetail { get; init; }
    public IReadOnlyList<string> Notices { get; init; } = [];
    public int SuppressedNoticeCount { get; init; }

    public int TerminalJobs => CompletedJobs + ReusedJobs + FailedJobs + CancelledJobs;
}

/// <summary>Final per-job evidence, in the input order the caller supplied.</summary>
public sealed record BatchItemResult<TResult>
{
    public required string JobId { get; init; }
    public required int InputIndex { get; init; }
    public required BatchItemState State { get; init; }
    public CacheIdentity? Identity { get; init; }
    public TResult? Result { get; init; }
    public bool Published { get; init; }
    public string? Error { get; init; }
    public string? DisposalError { get; init; }
    public IReadOnlyList<string> Notices { get; init; } = [];
    public int SuppressedNoticeCount { get; init; }
    public int? CompletionOrdinal { get; init; }
    public double? LastValidPercent { get; init; }
}

/// <summary>
/// Final collection evidence. Item order matches the request order.
/// Counts are jobs. They are not animal N, and neither are frame counts or the parallelism limit.
/// </summary>
public sealed record BatchRunResult<TResult>
{
    public const string CountInterpretation =
        "Completed, reused, failed, cancelled and active-job counts are work items. They are not a biological sample size. Frame counts and the parallelism limit are not animal N.";

    public required IReadOnlyList<BatchItemResult<TResult>> Items { get; init; }
    public required int CompletedCount { get; init; }
    public required int ReusedCount { get; init; }
    public required int FailedCount { get; init; }
    public required int CancelledCount { get; init; }
    public required bool CancellationRequested { get; init; }
    public required bool StoppedAfterFailure { get; init; }
    public required int MaxDegreeOfParallelism { get; init; }
    public required int ObservedMaximumActiveJobs { get; init; }
    public string CountInterpretationText { get; init; } = CountInterpretation;
}

/// <summary>
/// Admission snapshot. <see cref="ActiveJobs"/> is the number of jobs already admitted,
/// not including the job the hook is being asked to admit.
/// </summary>
public sealed record BatchResourceSnapshot(
    int ActiveJobs,
    int MaxActiveJobs,
    int QueuedJobs,
    int TerminalJobs,
    int TotalJobs,
    int Epoch);

/// <summary>
/// Return a lease to admit one more job. Return null to wait until a job finishes.
/// Returning null while <see cref="BatchResourceSnapshot.ActiveJobs"/> is 0 fails that job
/// visibly instead of waiting forever. Throw to fail the job being admitted.
/// The engine disposes the lease after the job terminates. Do not block inside the hook
/// waiting for this batch to change state; return null and let the engine wait.
/// </summary>
public delegate ValueTask<IAsyncDisposable?> BatchResourceBudgetHook(
    BatchResourceSnapshot snapshot,
    CancellationToken cancellationToken);

/// <summary>Limits and hooks for one engine instance. Each <see cref="BatchEngine.RunAsync"/> is independent.</summary>
public sealed record BatchEngineOptions
{
    public const int DefaultMaxDegreeOfParallelism = 2;
    public const int MinimumMaxDegreeOfParallelism = 1;

    public int MaxDegreeOfParallelism { get; init; } = DefaultMaxDegreeOfParallelism;
    public BatchFailurePolicy FailurePolicy { get; init; } = BatchFailurePolicy.Continue;
    public BatchResourceBudgetHook? AcquireResource { get; init; }

    public static BatchEngineOptions Checked(BatchEngineOptions? options)
    {
        options ??= new BatchEngineOptions();
        if (options.MaxDegreeOfParallelism < MinimumMaxDegreeOfParallelism)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxDegreeOfParallelism),
                options.MaxDegreeOfParallelism,
                "Maximum active jobs must be at least 1.");
        }

        if (!Enum.IsDefined(options.FailurePolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(FailurePolicy),
                options.FailurePolicy,
                "Failure policy is not a defined BatchFailurePolicy value.");
        }

        return options;
    }
}

/// <summary>One batch execution. The job list is copied; later host mutations do not reassociate results.</summary>
public sealed class BatchRequest<TInput, TResult>
{
    public required IReadOnlyList<BatchJob<TInput>> Jobs { get; init; }
    public required Func<BatchJob<TInput>, CacheIdentity> ResolveIdentity { get; init; }
    public required Func<BatchExecutionContext<TInput>, CancellationToken, ValueTask<BatchWorkResult<TResult>>> Process { get; init; }
    public required Func<BatchPublication<TResult>, CancellationToken, ValueTask> Publish { get; init; }
    public IBatchCompletionCache<TResult>? Cache { get; init; }
    public IProgress<BatchProgressUpdate>? Progress { get; init; }
    public Func<BatchExecutionContext<TInput>, CancellationToken, ValueTask<IAsyncDisposable?>>? OpenResource { get; init; }
}
