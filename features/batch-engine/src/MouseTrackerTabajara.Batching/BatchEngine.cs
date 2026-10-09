using System.Globalization;

namespace MouseTrackerTabajara.Batching;

/// <summary>
/// Runs recording jobs with a hard parallelism cap, exact per-job evidence, and
/// publication serialized so the host can commit complete runs one at a time.
/// Caller cancellation completes the returned task normally: already published items stay
/// completed, unpublished work is not stored, and every admitted worker is awaited.
/// </summary>
public sealed class BatchEngine
{
    public const int MaxDetailedNoticesPerItem = 8;
    private readonly BatchEngineOptions _options;

    public BatchEngine(BatchEngineOptions? options = null)
    {
        _options = BatchEngineOptions.Checked(options);
    }

    public int MaxDegreeOfParallelism => _options.MaxDegreeOfParallelism;
    public BatchFailurePolicy FailurePolicy => _options.FailurePolicy;

    public Task<BatchRunResult<TResult>> RunAsync<TInput, TResult>(
        BatchRequest<TInput, TResult> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Jobs);
        ArgumentNullException.ThrowIfNull(request.ResolveIdentity);
        ArgumentNullException.ThrowIfNull(request.Process);
        ArgumentNullException.ThrowIfNull(request.Publish);
        var jobs = request.Jobs.ToArray();
        ValidateJobs(jobs);
        return new Runner<TInput, TResult>(_options, request, jobs, cancellationToken).ExecuteAsync();
    }

    private static void ValidateJobs<TInput>(IReadOnlyList<BatchJob<TInput>> jobs)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicates = new SortedSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < jobs.Count; index++)
        {
            var job = jobs[index] ?? throw new ArgumentException($"Job at index {index} is null.", nameof(BatchRequest<TInput, object>.Jobs));
            if (string.IsNullOrWhiteSpace(job.Id) || !string.Equals(job.Id, job.Id.Trim(), StringComparison.Ordinal))
                throw new ArgumentException($"Job at index {index} needs a stable non-blank identity with no leading or trailing whitespace.", nameof(BatchRequest<TInput, object>.Jobs));
            if (!seen.TryAdd(job.Id, index))
                duplicates.Add(job.Id);
        }

        if (duplicates.Count > 0)
        {
            throw new ArgumentException(
                "Duplicate job identities: " + string.Join(", ", duplicates) + ". Each recording needs one stable identity; duplicates were not started.",
                nameof(BatchRequest<TInput, object>.Jobs));
        }
    }

    private sealed class Runner<TInput, TResult>
    {
        private readonly BatchEngineOptions _options;
        private readonly BatchRequest<TInput, TResult> _request;
        private readonly BatchJob<TInput>[] _jobs;
        private readonly CancellationToken _cancellation;
        private readonly Slot[] _items;
        private readonly SemaphoreSlim _slots;
        private readonly SemaphoreSlim _publish = new(1, 1);
        private readonly SemaphoreSlim _cacheGate = new(1, 1);
        private readonly object _gate = new();
        private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _epoch;
        private int _active;
        private int _maxActive;
        private int _queued;
        private int _running;
        private int _completed;
        private int _reused;
        private int _failed;
        private int _cancelled;
        private int _completionOrdinal;
        private int _stopAfterFailure;

        public Runner(BatchEngineOptions options, BatchRequest<TInput, TResult> request, BatchJob<TInput>[] jobs, CancellationToken cancellation)
        {
            _options = options;
            _request = request;
            _jobs = jobs;
            _cancellation = cancellation;
            _items = new Slot[jobs.Length];
            for (var index = 0; index < jobs.Length; index++)
                _items[index] = new Slot();
            _queued = jobs.Length;
            _slots = new SemaphoreSlim(options.MaxDegreeOfParallelism, options.MaxDegreeOfParallelism);
        }

        public async Task<BatchRunResult<TResult>> ExecuteAsync()
        {
            if (_jobs.Length == 0)
                return EmptyResult();

            EmitInitial();
            var workers = new List<Task>(Math.Min(_jobs.Length, _options.MaxDegreeOfParallelism));
            try
            {
                await ScheduleAsync(workers).ConfigureAwait(false);
            }
            finally
            {
                CancelRemainingQueued();
                try
                {
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FailStrandedRunning("Worker failed: " + OneLine(ex.Message));
                }

                _slots.Dispose();
                _publish.Dispose();
                _cacheGate.Dispose();
            }

            EnsureInvariant();
            return Build();
        }

        private async Task ScheduleAsync(List<Task> workers)
        {
            for (var index = 0; index < _jobs.Length; index++)
            {
                if (StopAdmitting)
                    break;

                var holdsSlot = false;
                try
                {
                    await _slots.WaitAsync(_cancellation).ConfigureAwait(false);
                    holdsSlot = true;
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }

                if (StopAdmitting)
                {
                    ReleaseIfHeld(ref holdsSlot);
                    break;
                }

                Admission admission;
                try
                {
                    admission = await AdmitAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    ReleaseIfHeld(ref holdsSlot);
                    break;
                }
                catch (Exception ex)
                {
                    ReleaseIfHeld(ref holdsSlot);
                    Finish(index, BatchItemState.Failed, "Resource budget hook failed: " + OneLine(ex.Message), default, false, "Resource budget hook failed: " + OneLine(ex.Message));
                    continue;
                }

                if (StopAdmitting || admission.Denied)
                {
                    if (admission.Lease is not null)
                        await DisposeResourceAsync(admission.Lease, index).ConfigureAwait(false);
                    ReleaseIfHeld(ref holdsSlot);
                    if (admission.Denied && !StopAdmitting)
                    {
                        const string refused = "Resource budget refused admission while no job was active.";
                        Finish(index, BatchItemState.Failed, refused, default, false, refused);
                    }

                    if (StopAdmitting)
                        break;
                    continue;
                }

                var jobIndex = index;
                var lease = admission.Lease;
                EnterActiveAndMarkRunning(jobIndex);
                try
                {
                    workers.Add(Task.Run(() => ExecuteJobAsync(jobIndex, lease), CancellationToken.None));
                    holdsSlot = false;
                }
                catch (Exception ex)
                {
                    Finish(jobIndex, BatchItemState.Failed, "Could not start worker: " + OneLine(ex.Message), default, false, "Could not start worker: " + OneLine(ex.Message));
                    DecrementActive();
                    await DisposeResourceAsync(lease, jobIndex).ConfigureAwait(false);
                    ReleaseIfHeld(ref holdsSlot);
                }
            }
        }

        private async Task<Admission> AdmitAsync()
        {
            var hook = _options.AcquireResource;
            if (hook is null)
                return new Admission(null, false);

            while (true)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _stopAfterFailure) != 0)
                    return new Admission(null, false);

                var snapshot = TakeSnapshot();
                var lease = await hook(snapshot, _cancellation).ConfigureAwait(false);
                if (lease is not null)
                    return new Admission(lease, false);
                if (snapshot.ActiveJobs == 0)
                    return new Admission(null, true);
                await WaitPulseAsync(snapshot.Epoch).ConfigureAwait(false);
            }
        }

        private async Task ExecuteJobAsync(int index, IAsyncDisposable? lease)
        {
            try
            {
                await RunCoreAsync(index).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Finish(index, BatchItemState.Failed, "Worker failed: " + OneLine(ex.Message), default, false, "Worker failed: " + OneLine(ex.Message));
            }
            finally
            {
                await DisposeResourceAsync(lease, index).ConfigureAwait(false);
                LeaveActiveAndReleaseSlot();
            }
        }

        private async Task RunCoreAsync(int index)
        {
            if (_cancellation.IsCancellationRequested)
            {
                Finish(index, BatchItemState.Cancelled, "Cancelled before processing.", default, false, "Cancelled before processing.");
                return;
            }

            var job = _jobs[index];
            CacheIdentity? identity;
            try
            {
                identity = _request.ResolveIdentity(job);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                Finish(index, BatchItemState.Cancelled, "Cancelled before processing.", default, false, "Cancelled before processing.");
                return;
            }
            catch (Exception ex)
            {
                Finish(index, BatchItemState.Failed, "Identity resolution failed: " + OneLine(ex.Message), default, false, "Identity resolution failed: " + OneLine(ex.Message));
                return;
            }

            if (identity is null)
            {
                Finish(index, BatchItemState.Failed, "Identity resolution returned no cache identity.", default, false, "Identity resolution returned no cache identity.");
                return;
            }

            lock (_gate)
                _items[index].Identity = identity;

            if (_cancellation.IsCancellationRequested)
            {
                Finish(index, BatchItemState.Cancelled, "Cancelled before processing.", default, false, "Cancelled before processing.");
                return;
            }

            BatchCachedCompletion<TResult>? cached;
            try
            {
                cached = await FindCachedAsync(job.Id, identity).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                Finish(index, BatchItemState.Cancelled, "Cancelled before processing.", default, false, "Cancelled before processing.");
                return;
            }
            catch (Exception ex)
            {
                Finish(index, BatchItemState.Failed, "Cache lookup failed: " + OneLine(ex.Message), default, false, "Cache lookup failed: " + OneLine(ex.Message));
                return;
            }

            if (cached is not null && cached.Result is not null && cached.Identity is not null && cached.Identity.Matches(identity))
            {
                Finish(index, BatchItemState.Reused, null, cached.Result, false, "Reused matching source, settings and algorithm.");
                return;
            }

            if (cached is not null)
            {
                var notice = cached.Identity is null || cached.Result is null
                    ? "Cache entry had no result and was ignored."
                    : "Cache entry ignored because source, settings, or algorithm identity did not match.";
                Note(index, notice, false, null);
            }

            if (_cancellation.IsCancellationRequested)
            {
                Finish(index, BatchItemState.Cancelled, "Cancelled before processing.", default, false, "Cancelled before processing.");
                return;
            }

            var context = new BatchExecutionContext<TInput>
            {
                JobId = job.Id,
                InputIndex = index,
                Input = job.Input,
                Identity = identity,
                Progress = new ProgressPipe(this, index)
            };

            IAsyncDisposable? scope = null;
            try
            {
                if (_request.OpenResource is not null)
                {
                    try
                    {
                        scope = await _request.OpenResource(context, _cancellation).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                    {
                        Finish(index, BatchItemState.Cancelled, "Cancelled while opening resources.", default, false, "Cancelled while opening resources.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Finish(index, BatchItemState.Failed, "Resource open failed: " + OneLine(ex.Message), default, false, "Resource open failed: " + OneLine(ex.Message));
                        return;
                    }
                }

                BatchWorkResult<TResult>? work;
                try
                {
                    work = await _request.Process(context, _cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    Finish(index, BatchItemState.Cancelled, "Cancelled during processing.", default, false, "Cancelled during processing.");
                    return;
                }
                catch (Exception ex)
                {
                    if (_cancellation.IsCancellationRequested)
                        Finish(index, BatchItemState.Cancelled, "Cancelled during processing. Observed error: " + OneLine(ex.Message), default, false, "Cancelled during processing.");
                    else
                        Finish(index, BatchItemState.Failed, OneLine(ex.Message), default, false, OneLine(ex.Message));
                    return;
                }

                if (_cancellation.IsCancellationRequested)
                {
                    Finish(index, BatchItemState.Cancelled, "Cancelled before publication.", default, false, "Cancelled before publication.");
                    return;
                }

                if (work is null || !work.IsComplete || work.Result is null)
                {
                    var reason = work is null
                        ? "Processor returned no completion. Nothing was published."
                        : work.IsComplete
                            ? "Completed result was null and was not published."
                            : "Incomplete result was not published: " + work.IncompleteReason;
                    Finish(index, BatchItemState.Failed, reason, default, false, reason);
                    return;
                }

                try
                {
                    await PublishAsync(new BatchPublication<TResult>(job.Id, index, identity, work.Result)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    Finish(index, BatchItemState.Cancelled, "Publication was cancelled before the run was stored.", default, false, "Publication was cancelled before the run was stored.");
                    return;
                }
                catch (Exception ex)
                {
                    Finish(index, BatchItemState.Failed, "Publication failed: " + OneLine(ex.Message), default, false, "Publication failed: " + OneLine(ex.Message));
                    return;
                }

                Finish(index, BatchItemState.Completed, null, work.Result, true, "Completed");
            }
            finally
            {
                await DisposeResourceAsync(scope, index).ConfigureAwait(false);
            }
        }

        private async ValueTask<BatchCachedCompletion<TResult>?> FindCachedAsync(string jobId, CacheIdentity identity)
        {
            if (_request.Cache is null)
                return null;
            await _cacheGate.WaitAsync(_cancellation).ConfigureAwait(false);
            try
            {
                return _request.Cache.Find(jobId, identity);
            }
            finally
            {
                _cacheGate.Release();
            }
        }

        private async ValueTask PublishAsync(BatchPublication<TResult> publication)
        {
            await _publish.WaitAsync(_cancellation).ConfigureAwait(false);
            try
            {
                _cancellation.ThrowIfCancellationRequested();
                await _request.Publish(publication, _cancellation).ConfigureAwait(false);
            }
            finally
            {
                _publish.Release();
            }
        }

        private void ReportProgress(int index, BatchItemProgressReport? report)
        {
            BatchProgressUpdate update;
            lock (_gate)
            {
                var slot = _items[index];
                if (slot.State is not (BatchItemState.Queued or BatchItemState.Running))
                {
                    AddNoticeLocked(slot, "Progress arrived after the item finished and was ignored.");
                    update = Capture(index, slot.LastStatus, false, null);
                }
                else if (report is null)
                {
                    const string detail = "Progress report was null and was ignored.";
                    AddNoticeLocked(slot, detail);
                    update = Capture(index, slot.LastStatus, true, detail);
                }
                else if (!TryNormalizePercent(report.Percent, out var percent))
                {
                    var detail = "Malformed progress percent '" + report.Percent.ToString(CultureInfo.InvariantCulture) + "' was ignored.";
                    AddNoticeLocked(slot, detail);
                    update = Capture(index, slot.LastStatus, true, detail);
                }
                else
                {
                    slot.LastValidPercent = percent;
                    slot.LastStatus = report.Status;
                    update = Capture(index, report.Status, false, null);
                }
            }

            Emit(update);
        }

        private void EnterActiveAndMarkRunning(int index)
        {
            BatchProgressUpdate update;
            lock (_gate)
            {
                var slot = _items[index];
                if (slot.State != BatchItemState.Queued)
                    throw new InvalidOperationException("Internal batch state error: job " + _jobs[index].Id + " left the queue twice.");
                _active++;
                if (_active > _maxActive)
                    _maxActive = _active;
                AddCount(BatchItemState.Queued, -1);
                slot.State = BatchItemState.Running;
                AddCount(BatchItemState.Running, 1);
                slot.LastStatus = "Running";
                update = Capture(index, "Running", false, null);
            }

            Emit(update);
        }

        private bool Finish(int index, BatchItemState next, string? error, TResult? result, bool published, string? message)
        {
            if (published && next != BatchItemState.Completed)
                throw new InvalidOperationException("Only a completed item can be marked published.");

            BatchProgressUpdate update;
            lock (_gate)
            {
                var slot = _items[index];
                if (!CanFinish(slot.State, next))
                    return false;
                AddCount(slot.State, -1);
                slot.State = next;
                slot.Error = error;
                slot.Result = result;
                slot.Published = published;
                slot.CompletionOrdinal = ++_completionOrdinal;
                slot.LastStatus = message;
                AddCount(next, 1);
                update = Capture(index, message, false, null);
            }

            if (next == BatchItemState.Failed && _options.FailurePolicy == BatchFailurePolicy.StopScheduling)
                Volatile.Write(ref _stopAfterFailure, 1);
            Emit(update);
            return true;
        }

        private static bool CanFinish(BatchItemState current, BatchItemState next)
        {
            if (current is BatchItemState.Completed or BatchItemState.Reused or BatchItemState.Failed or BatchItemState.Cancelled)
                return false;
            if (current == BatchItemState.Queued)
                return next is BatchItemState.Cancelled or BatchItemState.Failed;
            return current == BatchItemState.Running
                && next is BatchItemState.Completed or BatchItemState.Reused or BatchItemState.Failed or BatchItemState.Cancelled;
        }

        private void CancelRemainingQueued()
        {
            var reason = _cancellation.IsCancellationRequested
                ? "Cancelled before start."
                : Volatile.Read(ref _stopAfterFailure) != 0
                    ? "Not started because a previous item failed and scheduling stopped."
                    : null;
            if (reason is null)
                return;
            for (var index = 0; index < _items.Length; index++)
                CancelIfStillQueued(index, reason);
        }

        private void CancelIfStillQueued(int index, string reason)
        {
            BatchProgressUpdate? update = null;
            lock (_gate)
            {
                var slot = _items[index];
                if (slot.State != BatchItemState.Queued)
                    return;
                AddCount(BatchItemState.Queued, -1);
                slot.State = BatchItemState.Cancelled;
                slot.Error = reason;
                slot.CompletionOrdinal = ++_completionOrdinal;
                slot.LastStatus = reason;
                AddCount(BatchItemState.Cancelled, 1);
                update = Capture(index, reason, false, null);
            }

            if (update is not null)
                Emit(update);
        }

        private void FailStrandedRunning(string error)
        {
            for (var index = 0; index < _items.Length; index++)
                Finish(index, BatchItemState.Failed, error, default, false, error);
        }

        private void Note(int index, string notice, bool malformed, string? detail)
        {
            BatchProgressUpdate update;
            lock (_gate)
            {
                AddNoticeLocked(_items[index], notice);
                update = Capture(index, _items[index].LastStatus, malformed, detail);
            }

            Emit(update);
        }

        private void EmitInitial()
        {
            for (var index = 0; index < _jobs.Length; index++)
            {
                BatchProgressUpdate update;
                lock (_gate)
                    update = Capture(index, "Queued", false, null);
                Emit(update);
            }
        }

        private void Emit(BatchProgressUpdate update)
        {
            var progress = _request.Progress;
            if (progress is null)
                return;
            try
            {
                progress.Report(update);
            }
            catch (Exception ex)
            {
                lock (_gate)
                    AddNoticeLocked(_items[update.InputIndex], "Progress observer threw: " + OneLine(ex.Message));
            }
        }

        private BatchProgressUpdate Capture(int index, string? message, bool malformed, string? malformedDetail)
        {
            var slot = _items[index];
            return new BatchProgressUpdate
            {
                JobId = _jobs[index].Id,
                InputIndex = index,
                State = slot.State,
                TotalJobs = _jobs.Length,
                QueuedJobs = _queued,
                RunningJobs = _running,
                CompletedJobs = _completed,
                ReusedJobs = _reused,
                FailedJobs = _failed,
                CancelledJobs = _cancelled,
                ActiveJobs = _active,
                ParallelismLimit = _options.MaxDegreeOfParallelism,
                ItemPercent = slot.LastValidPercent,
                Message = message,
                ProgressMalformed = malformed,
                MalformedDetail = malformedDetail,
                Notices = slot.Notices.ToArray(),
                SuppressedNoticeCount = slot.SuppressedNotices
            };
        }

        private BatchResourceSnapshot TakeSnapshot()
        {
            lock (_gate)
            {
                return new BatchResourceSnapshot(
                    _active,
                    _options.MaxDegreeOfParallelism,
                    _queued,
                    _completed + _reused + _failed + _cancelled,
                    _jobs.Length,
                    _epoch);
            }
        }

        private Task WaitPulseAsync(int seenEpoch)
        {
            Task waiting;
            lock (_gate)
            {
                if (_epoch != seenEpoch)
                    return Task.CompletedTask;
                waiting = _pulse.Task;
            }

            return waiting.WaitAsync(_cancellation);
        }

        private void LeaveActiveAndReleaseSlot()
        {
            DecrementActive();
            _slots.Release();
        }

        private void DecrementActive()
        {
            TaskCompletionSource pulse;
            lock (_gate)
            {
                if (_active > 0)
                    _active--;
                _epoch++;
                pulse = _pulse;
                _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            pulse.TrySetResult();
        }

        private void ReleaseIfHeld(ref bool holdsSlot)
        {
            if (!holdsSlot)
                return;
            _slots.Release();
            holdsSlot = false;
        }

        private bool StopAdmitting =>
            _cancellation.IsCancellationRequested || Volatile.Read(ref _stopAfterFailure) != 0;

        private void AddCount(BatchItemState state, int delta)
        {
            switch (state)
            {
                case BatchItemState.Queued: _queued += delta; break;
                case BatchItemState.Running: _running += delta; break;
                case BatchItemState.Completed: _completed += delta; break;
                case BatchItemState.Reused: _reused += delta; break;
                case BatchItemState.Failed: _failed += delta; break;
                case BatchItemState.Cancelled: _cancelled += delta; break;
                default: throw new InvalidOperationException("Unknown batch item state " + state + ".");
            }
        }

        private static void AddNoticeLocked(Slot slot, string notice)
        {
            if (slot.Notices.Count < MaxDetailedNoticesPerItem)
                slot.Notices.Add(OneLine(notice));
            else
                slot.SuppressedNotices++;
        }

        private async ValueTask DisposeResourceAsync(IAsyncDisposable? resource, int index)
        {
            if (resource is null)
                return;
            try
            {
                await resource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    var slot = _items[index];
                    var message = OneLine(ex.Message);
                    slot.DisposalError = slot.DisposalError is null ? message : slot.DisposalError + " | " + message;
                }
            }
        }

        private void EnsureInvariant()
        {
            lock (_gate)
            {
                var sum = _queued + _running + _completed + _reused + _failed + _cancelled;
                if (sum != _jobs.Length || _queued != 0 || _running != 0 || _active != 0)
                {
                    throw new InvalidOperationException(
                        "Batch accounting invariant failed: "
                        + "queued=" + _queued
                        + " running=" + _running
                        + " completed=" + _completed
                        + " reused=" + _reused
                        + " failed=" + _failed
                        + " cancelled=" + _cancelled
                        + " active=" + _active
                        + " total=" + _jobs.Length + ".");
                }

                for (var index = 0; index < _items.Length; index++)
                {
                    var slot = _items[index];
                    var terminal = slot.State is BatchItemState.Completed or BatchItemState.Reused or BatchItemState.Failed or BatchItemState.Cancelled;
                    if (!terminal)
                        throw new InvalidOperationException("Job " + _jobs[index].Id + " ended in state " + slot.State + ".");
                    if (slot.Published != (slot.State == BatchItemState.Completed))
                        throw new InvalidOperationException("Job " + _jobs[index].Id + " publication flag does not match state " + slot.State + ".");
                    if (slot.State is BatchItemState.Completed or BatchItemState.Reused && slot.Result is null)
                        throw new InvalidOperationException("Job " + _jobs[index].Id + " succeeded without a result.");
                    if (slot.State == BatchItemState.Failed && string.IsNullOrWhiteSpace(slot.Error))
                        throw new InvalidOperationException("Job " + _jobs[index].Id + " failed without a visible error.");
                    if (slot.State == BatchItemState.Cancelled && string.IsNullOrWhiteSpace(slot.Error))
                        throw new InvalidOperationException("Job " + _jobs[index].Id + " was cancelled without a visible reason.");
                }
            }
        }

        private BatchRunResult<TResult> Build()
        {
            var items = new BatchItemResult<TResult>[_jobs.Length];
            for (var index = 0; index < _jobs.Length; index++)
            {
                var slot = _items[index];
                items[index] = new BatchItemResult<TResult>
                {
                    JobId = _jobs[index].Id,
                    InputIndex = index,
                    State = slot.State,
                    Identity = slot.Identity,
                    Result = slot.Result,
                    Published = slot.Published,
                    Error = slot.Error,
                    DisposalError = slot.DisposalError,
                    Notices = slot.Notices.ToArray(),
                    SuppressedNoticeCount = slot.SuppressedNotices,
                    CompletionOrdinal = slot.CompletionOrdinal,
                    LastValidPercent = slot.LastValidPercent
                };
            }

            return new BatchRunResult<TResult>
            {
                Items = items,
                CompletedCount = items.Count(item => item.State == BatchItemState.Completed),
                ReusedCount = items.Count(item => item.State == BatchItemState.Reused),
                FailedCount = items.Count(item => item.State == BatchItemState.Failed),
                CancelledCount = items.Count(item => item.State == BatchItemState.Cancelled),
                CancellationRequested = _cancellation.IsCancellationRequested,
                StoppedAfterFailure = Volatile.Read(ref _stopAfterFailure) != 0,
                MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
                ObservedMaximumActiveJobs = _maxActive
            };
        }

        private BatchRunResult<TResult> EmptyResult() => new()
        {
            Items = [],
            CompletedCount = 0,
            ReusedCount = 0,
            FailedCount = 0,
            CancelledCount = 0,
            CancellationRequested = _cancellation.IsCancellationRequested,
            StoppedAfterFailure = false,
            MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
            ObservedMaximumActiveJobs = 0
        };

        private static bool TryNormalizePercent(double percent, out double normalized)
        {
            const double slack = 1e-9;
            if (double.IsNaN(percent) || double.IsInfinity(percent) || percent < -slack || percent > 100d + slack)
            {
                normalized = 0;
                return false;
            }

            normalized = Math.Clamp(percent, 0d, 100d);
            return true;
        }

        private static string OneLine(string? message)
        {
            var text = string.IsNullOrWhiteSpace(message) ? "The operation failed without a message." : message.Trim();
            var newline = text.IndexOfAny(['\r', '\n']);
            if (newline >= 0)
                text = text[..newline].TrimEnd();
            const int max = 2000;
            if (text.Length > max)
                text = text[..max] + " (truncated)";
            return text;
        }

        private readonly record struct Admission(IAsyncDisposable? Lease, bool Denied);

        private sealed class Slot
        {
            public BatchItemState State = BatchItemState.Queued;
            public CacheIdentity? Identity;
            public TResult? Result;
            public bool Published;
            public string? Error;
            public string? DisposalError;
            public List<string> Notices { get; } = new();
            public int SuppressedNotices;
            public int? CompletionOrdinal;
            public double? LastValidPercent;
            public string? LastStatus;
        }

        private sealed class ProgressPipe(Runner<TInput, TResult> runner, int index) : IProgress<BatchItemProgressReport>
        {
            public void Report(BatchItemProgressReport value) => runner.ReportProgress(index, value);
        }
    }
}
