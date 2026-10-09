# Batch engine integration guide

`MouseTrackerTabajara.Batching` schedules a collection of recording jobs with a hard parallelism cap, stable identities, and exact per-job evidence. It does not reference WPF, OpenCV, SQLite, or the application namespaces. The host supplies the processor, the cache, and the only database write.

This checkout still calls those types `MorrisLab.*`. Another lane is renaming them. Retarget the snippets below to the names that land; do not treat the snippets as already compiled into `MorrisLab.Application`.

## Tested in this project

The Release build of `features/batch-engine/MouseTrackerTabajara.Batching.slnx` is warnings-as-errors and uses synthetic jobs only. No footage, WPF, or production project is referenced.

| Behavior | Test |
|---|---|
| 30 jobs overlap and the active count stays at an explicit limit of 4 | `ThirtyItemRunOverlapsAtTheExplicitLimit` |
| Limit 1 never overlaps; the default limit is 2 and does overlap | `LimitOneNeverOverlapsAndDefaultLimitIsTwo` |
| `0`, negative limits, and an undefined failure policy throw | `ZeroAndNegativeLimitsAndUndefinedPolicyAreRejected` |
| Empty input completes without calling the processor | `EmptyInputCompletesWithoutCallingTheProcessor` |
| Duplicate, blank, and null identities throw before any job starts | `DuplicateAndBlankIdentitiesDoNotStart` |
| Identity comparison is ordinal and case-sensitive | `CaseSensitiveIdentitiesAreDistinct` |
| Cancellation keeps published jobs, drains workers, disposes leases, and a later run reuses only matching source, settings, and algorithm identity | `CancellationRetainsPublishedWorkAndResumeReusesOnlyMatchingIdentity` |
| A token that is already cancelled starts nothing | `AlreadyCancelledRequestDoesNotStartJobs` |
| One identity failure, one thrown processor error, a null completion, and an incomplete result stay on those jobs; siblings still publish | `ItemFailuresIncompleteResultsAndBadIdentityStayOnTheRightJob` |
| Malformed progress is retained, capped, and does not replace another job's percent | `MalformedProgressIsKeptVisibleWithoutCorruptingAnotherItem` |
| A publication exception fails only that job, and publication callbacks do not overlap | `PublicationFailureIsIsolatedAndPublicationIsSerialized` |
| Completion order `1,0,3,2,...` still stores each result on its input identity | `OutOfOrderCompletionStaysBoundToInputIdentity` |
| A resource hook can tighten the cap below the parallelism limit, and every lease is disposed | `ResourceBudgetCapsOverlapAndDisposesEveryLease` |
| A hook that always refuses while nothing is active fails each job instead of deadlocking | `IdleResourceDenialFailsEveryItemWithoutHanging` |
| A throwing hook fails the job being admitted; later jobs still run | `ResourceHookExceptionFailsThatItemAndContinues` |
| `StopScheduling` lets the jobs already inside the limit finish and does not start the rest | `StopPolicyFinishesWorkAlreadyRunningAndStartsNothingNew` |
| Dispose failures remain visible after a successful publish | `DisposalErrorsStayVisibleOnThePublishedItem` |
| A cache exception fails that job and does not drop it | `CacheLookupFailureDoesNotDropTheItem` |

`BatchRunResult<T>.CountInterpretation` is part of the result on every run, including the empty run. Completed, reused, failed, cancelled, and active-job counts are work items. They are not a biological sample size. Frame counts and the parallelism limit are not animal N.

## Contracts the host implements

```text
BatchEngine
  MaxDegreeOfParallelism     default 2, minimum 1
  FailurePolicy              Continue (default) or StopScheduling
  AcquireResource            optional; return a lease, or null to wait

BatchRequest<TInput,TResult>
  Jobs                       stable caller ids; duplicates throw before work starts
  ResolveIdentity            fresh source + settings + algorithm for this attempt
  Process                    returns Complete only for a finished result
  Publish                    sole write path; serialized; throw only if not stored
  Cache                      optional Find(jobId, requiredIdentity)
  OpenResource               optional per-job scope, disposed after the job
  Progress                   per-job state plus collection counts
```

Reuse happens only when `CacheIdentity.Matches` succeeds for source, settings, and algorithm together (`StringComparison.Ordinal`). A stored row whose identity differs is a miss. The miss is recorded on that item's `Notices`, and the job is processed. Reused jobs are not published again.

`BatchWorkResult.Incomplete`, a null completion, and a processor exception are not published. Caller cancellation after a successful `Publish` return keeps the item `Completed`. Cancellation before `Publish` returns leaves it `Cancelled` and unpublished. `RunAsync` does not throw `OperationCanceledException` for an ordinary cancel; it returns the per-job states.

`Publish` is invoked one at a time. The host can call `StudyStore.SaveRun` inside it without adding its own job-wide lock. The engine does not open the database and does not mutate study rows.

Progress percent must be finite and on `[0, 100]`. Values within `1e-9` of that interval are clamped. Anything else is ignored, kept on the item as a notice, and does not change that item's last valid percent or any other item. Notices are capped at `BatchEngine.MaxDetailedNoticesPerItem` (8), and `SuppressedNoticeCount` shows the remainder.

The resource hook sees `BatchResourceSnapshot.ActiveJobs` jobs already admitted, not the job it is deciding. Return null to wait for one of them to finish. Return null while `ActiveJobs` is 0 and that job fails visibly. Do not block inside the hook waiting for this batch; the scheduler is the waiter.

## Current serial queue

`WorkspaceService.AnalyzeAsync` in `src/MorrisLab.Application/WorkspaceService.cs` is the behavior to preserve:

- It snapshots `assets.DistinctBy(a => a.Id)` before work starts.
- It runs the whole loop on one background task. Clips do not overlap.
- For each asset it loads `Store.LatestRun(asset.Id, asset.Settings)` and reuses that run only when `AlgorithmContract == AlgorithmIdentity.Contract` and `SourceSha256` equals a fresh `OpenCvTracker.HashFile(asset.Path)`.
- A reuse does not call `Store.SaveRun`.
- `OpenCvTracker.Analyze` either returns a finished `AnalysisRun` or throws. Decoder and hash failures throw `InvalidDataException` and do not return a partial run. The loop's `catch (Exception)` records `errors[asset.Id]` and does not save.
- The loop checks the token again before `Store.SaveRun`. A cancel keeps runs already saved and does not save the in-flight one.
- Per-clip exceptions continue the queue. Cancel breaks it.
- `BatchOutcome.Completed` counts both newly saved runs and reused runs. `Reused` is the subset that was reused. The existing 30-clip test expects `Completed == 30` and `Reused == 1` on resume, then `Reused == 30` on replay.

`StudyStore.SaveRun` inserts one immutable run inside a transaction and commits before `Audit`. `LatestRun(videoId, matchingSettings)` keeps the latest row whose settings hash matches. Settings identity for the cache is `StudyStore.SettingsHash`.

The desktop shell in `MainWindow.RunBatchAsync` listens to one `BatchProgress` value (`Complete`, `Total`, `Filename`, `CurrentPercent`, `Status`) and writes one status string plus `MainViewModel.Progress`. `TrialRow` has no per-recording batch state. `OpenCvTracker.Analyze` reports percent on a 0–100 scale.

## Proposed host adapter

Proposed, not compiled here. Keep `DistinctBy` so one video id is one job. The engine rejects duplicate ids before starting; that is a backstop, not a second biological sample.

`BatchOutcome.Completed` must stay "saved plus reused" so the current resume test keeps its meaning:

```csharp
using MouseTrackerTabajara.Batching;

private sealed class StudyRunCache(StudyStore store, IReadOnlyDictionary<string, VideoAsset> assets)
    : IBatchCompletionCache<AnalysisRun>
{
    public BatchCachedCompletion<AnalysisRun>? Find(string jobId, CacheIdentity requiredIdentity)
    {
        var asset = assets[jobId];
        // Latest run whose settings hash matches the asset's current settings.
        // The engine still refuses the row unless source, settings and algorithm all match.
        var existing = store.LatestRun(asset.Id, asset.Settings);
        if (existing is null)
            return null;
        return new BatchCachedCompletion<AnalysisRun>(
            new CacheIdentity(existing.SourceSha256, StudyStore.SettingsHash(existing.Settings), existing.AlgorithmContract),
            existing);
    }
}

public async Task<BatchOutcome> AnalyzeAsync(
    IEnumerable<VideoAsset> assets,
    IProgress<BatchProgress>? progress,
    CancellationToken cancellation = default)
{
    var snapshot = assets.DistinctBy(asset => asset.Id).ToArray();
    var byId = snapshot.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
    Store.Audit("batch_started", new { Videos = snapshot.Select(video => video.Id), Count = snapshot.Length });

    var engine = new BatchEngine(new BatchEngineOptions
    {
        MaxDegreeOfParallelism = 2,
        FailurePolicy = BatchFailurePolicy.Continue
    });
    var batch = await engine.RunAsync(new BatchRequest<VideoAsset, AnalysisRun>
    {
        Jobs = snapshot.Select(asset => new BatchJob<VideoAsset>(asset.Id, asset)).ToArray(),
        ResolveIdentity = job => new CacheIdentity(
            OpenCvTracker.HashFile(job.Input.Path),
            StudyStore.SettingsHash(job.Input.Settings),
            AlgorithmIdentity.Contract),
        Cache = new StudyRunCache(Store, byId),
        Process = (ctx, token) =>
        {
            var run = OpenCvTracker.Analyze(
                ctx.Input,
                new InlineProgress<double>(percent => ctx.Progress.Report(new BatchItemProgressReport(percent, "Processing"))),
                token);
            return ValueTask.FromResult(BatchWorkResult<AnalysisRun>.Complete(run));
        },
        Publish = (publication, token) =>
        {
            // Throw only when the run is not committed. SaveRun commits, then audits.
            // An audit failure after commit must not be reported as a rejected run.
            token.ThrowIfCancellationRequested();
            Store.SaveRun(publication.Result);
            return ValueTask.CompletedTask;
        },
        Progress = progress is null ? null : new InlineProgress<BatchProgressUpdate>(update => progress.Report(new BatchProgress(
            update.CompletedJobs + update.ReusedJobs,
            update.TotalJobs,
            byId[update.JobId].Filename,
            update.ItemPercent ?? (update.State is BatchItemState.Completed or BatchItemState.Reused ? 100d : 0d),
            update.State + (update.Message is null ? "" : ": " + update.Message))))
    }, cancellation);

    var errors = batch.Items
        .Where(item => item.State == BatchItemState.Failed)
        .ToDictionary(item => item.JobId, item => item.Error ?? "Analysis failed.", StringComparer.Ordinal);
    foreach (var item in batch.Items.Where(item => item.State == BatchItemState.Failed))
        Store.Audit("analysis_failed", new { item.JobId, Error = item.Error });

    var outcome = new BatchOutcome(
        batch.CompletedCount + batch.ReusedCount,
        batch.ReusedCount,
        batch.CancellationRequested,
        errors);
    Store.Audit("batch_finished", outcome);
    return outcome;
}
```

Write the study database only from `Publish`, and from the caller after `RunAsync` returns. Do not call `SaveRun`, `SaveVideo`, or `Audit` from `Process` or from a progress handler. Those run while other recordings are still being decoded. `Process` should only read the video file, which is what `OpenCvTracker.Analyze` does today.

`ResolveIdentity` hashes the file on the worker, so a recording that changes between runs stops matching. Changing settings changes `StudyStore.SettingsHash`. Changing `AlgorithmIdentity.Contract`, or replaying over a row stored with another contract, stops matching. That is the same triple the serial queue checks.

`FailurePolicy.Continue` matches today's per-clip `catch`. `StopScheduling` is available when a later host wants to stop admitting clips after the first failed one. It is not what `AnalyzeAsync` does now. Jobs already admitted still finish; jobs not started become `Cancelled` with a visible reason, not silent gaps.

Do not add `batch.CompletedCount`, `snapshot.Length`, or a frame count to an animal N. Group summaries belong to the study-analysis lane and must weight animals.

### Publication rule

`StudyStore.SaveRun` is already atomic for one run: the insert commits or the exception leaves the transaction uncommitted. Call it inside `Publish` and do not catch a failure from the insert. If `Audit` after `Commit` throws, the run is already stored; swallowing that secondary failure, or recording it outside the batch result, avoids a second insert on resume. The engine treats every exception from `Publish` as "not stored".

Reused runs must not be inserted again. `runs.id` is a primary key and a second `SaveRun` of the same id throws. The engine does not call `Publish` for `Reused`.

### Resource budget

The tested hook admits with a lease, waits by returning null, fails the job visibly when it refuses while `ActiveJobs` is 0, and always disposes the lease. `ActiveJobs` does not include the job being considered. This is the pattern covered by `ResourceBudgetCapsOverlapAndDisposesEveryLease`:

```csharp
AcquireResource = (snapshot, _) =>
{
    if (snapshot.ActiveJobs >= budget)
        return ValueTask.FromResult<IAsyncDisposable?>(null);
    return ValueTask.FromResult<IAsyncDisposable?>(lease);
};
```

A byte or pixel cap for real recordings is proposed and not measured here. The host would do the same thing: return null while admitted work already holds the budget, and dispose the lease to release it. The engine waits and retries. It does not read video dimensions.

## Proposed per-recording progress

Proposed, not added to `MainWindow.xaml` or `TrialRow`. The current UI is one string and one bar:

```csharp
var progress = new Progress<BatchProgress>(update =>
{
    _model.Progress = update.CurrentPercent;
    _model.Status = $"{update.Complete}/{update.Total} complete  •  {(_model.Blind ? "Recording" : update.Filename)}  •  {update.CurrentPercent:F0}%  •  {update.Status}";
});
```

That bar is the active clip, not the collection, and the status line does not keep a failed clip's error once the next clip reports. Build `Progress<BatchProgressUpdate>` on the UI thread so reports marshal back to it. Inside the handler, find the row by `update.JobId` (the video id) and set row state. Do not block, and do not wait for the batch from that handler.

Add to `TrialRow`, proposed:

```csharp
public BatchItemState ItemState { get; set; } = BatchItemState.Queued;
public double? ItemPercent { get; set; }
public string? ItemMessage { get; set; }
```

Suggested grid columns, after the existing Review column:

```xml
<DataGridTextColumn Header="Batch" Binding="{Binding ItemState}" IsReadOnly="True" Width="90"/>
<DataGridTextColumn Header="Clip %" Binding="{Binding ItemPercent, StringFormat={}{0:F0}}" IsReadOnly="True" Width="70"/>
<DataGridTextColumn Header="Batch detail" Binding="{Binding ItemMessage}" IsReadOnly="True" Width="220"/>
```

Drive the existing bar from collection progress, not from the clip percent:

```csharp
var terminal = update.CompletedJobs + update.ReusedJobs + update.FailedJobs + update.CancelledJobs;
_model.Progress = update.TotalJobs == 0 ? 0 : 100d * terminal / update.TotalJobs;
```

Keep the clip percent on the row. A `Failed` row keeps `ItemMessage` equal to the error after the batch moves on. A `Cancelled` row keeps its reason. Blind review can keep showing "Recording" in the shared status line; the grid already hides the file name through `DisplayName`.

Say "recordings" in that status line. Do not relabel the completed-job count as mice, and do not treat a full bar as tracking accuracy or as a group result.

## Build

From this checkout:

```text
dotnet test features/batch-engine/MouseTrackerTabajara.Batching.slnx -c Release
```

The library targets `net10.0`. Warnings are errors through the repository `Directory.Build.props`. Test binaries and `features/batch-engine/artifacts/` are ignored.
