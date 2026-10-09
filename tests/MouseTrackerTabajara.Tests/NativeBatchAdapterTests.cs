using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Batching;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Tests;

public sealed class NativeBatchAdapterTests(ITestOutputHelper output)
{
    [Fact]
    public void ProgressKeepsCollectionPercentAndDoesNotRegressTerminalState()
    {
        var identities = new[]
        {
            new FrozenRecordingIdentity("a", 0, "a.avi"),
            new FrozenRecordingIdentity("b", 1, "b.avi")
        };
        var legacy = new List<BatchProgress>();
        var typed = new List<RecordingBatchProgress>();
        var coordinator = new RecordingProgressCoordinator(identities, new InlineProgress<BatchProgress>(legacy.Add), new InlineProgress<RecordingBatchProgress>(typed.Add), TimeSpan.Zero);

        coordinator.Report(Update("missing", 0, BatchItemState.Running, 50));
        coordinator.Report(Update("a", 0, BatchItemState.Running, 80, completedJobs: 99));
        Assert.Equal(80, typed[^1].ItemPercent);
        Assert.Equal(0, typed[^1].CollectionPercent);
        Assert.Equal(0, typed[^1].CompletedRecordings);
        Assert.Equal(0, legacy[^1].CurrentPercent);
        Assert.Equal("Processing", legacy[^1].Status);
        Assert.Equal("a.avi", legacy[^1].Filename);

        coordinator.Report(Update("b", 77, BatchItemState.Running, 10));
        Assert.Equal(1, typed[^1].InputIndex);
        Assert.Equal("b.avi", legacy[^1].Filename);
        Assert.Equal(0, typed[^1].CollectionPercent);
        Assert.Equal(0, typed[^1].CompletedRecordings);

        coordinator.Report(Update("a", 0, BatchItemState.Completed, 100));
        Assert.Equal(BatchItemState.Completed, typed[^1].ItemState);
        Assert.Equal(50, typed[^1].CollectionPercent);
        Assert.Equal(1, typed[^1].CompletedRecordings);
        Assert.Equal(50, legacy[^1].CurrentPercent);
        Assert.Equal(1, legacy[^1].Complete);
        Assert.Equal("Completed", legacy[^1].Status);
        var delivered = typed.Count;

        coordinator.Report(Update("a", 0, BatchItemState.Running, 5));
        coordinator.Report(Update("a", 0, BatchItemState.Failed, 0, message: "late"));
        Assert.Equal(delivered, typed.Count);
        Assert.Equal(BatchItemState.Completed, typed[^1].ItemState);
        Assert.Equal(50, legacy[^1].CurrentPercent);
        Assert.Equal("Completed", legacy[^1].Status);

        coordinator.ReportFinal("a", BatchItemState.Failed, "authoritative", 100);
        Assert.Equal(BatchItemState.Failed, typed[^1].ItemState);
        Assert.Equal("Failed: authoritative", legacy[^1].Status);
        Assert.Equal(0, legacy[^1].Complete);
        Assert.Equal(50, legacy[^1].CurrentPercent);
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.ReportFinal("a", BatchItemState.Running, null, null));
    }

    [Fact]
    public void ThrottledRunningUpdatesStillDeliverTerminalStates()
    {
        var now = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        var legacy = new List<BatchProgress>();
        var coordinator = new RecordingProgressCoordinator(
            [new FrozenRecordingIdentity("a", 0, "a.avi"), new FrozenRecordingIdentity("b", 1, "b.avi")],
            new InlineProgress<BatchProgress>(legacy.Add),
            null,
            TimeSpan.FromSeconds(5),
            () => now);

        coordinator.Report(Update("a", 0, BatchItemState.Running, 10));
        coordinator.Report(Update("a", 0, BatchItemState.Running, 20));
        Assert.Single(legacy);
        Assert.Equal("Processing", legacy[0].Status);
        coordinator.Report(Update("b", 1, BatchItemState.Completed, 100));
        Assert.Equal("Completed", legacy[^1].Status);
        Assert.Equal(50, legacy[^1].CurrentPercent);
        Assert.Equal(1, legacy[^1].Complete);
        now = now.AddSeconds(5);
        coordinator.Report(Update("a", 0, BatchItemState.Running, 30));
        Assert.Equal(3, legacy.Count);
        Assert.Equal("Processing", legacy[^1].Status);
        Assert.Equal(50, legacy[^1].CurrentPercent);
        Assert.Equal(1, legacy[^1].Complete);
    }

    [Fact]
    public void ProgressObserverExceptionIsRememberedAndDoesNotStopDelivery()
    {
        var legacy = new List<string>();
        var coordinator = new RecordingProgressCoordinator(
            [new FrozenRecordingIdentity("a", 0, "a.avi")],
            new InlineProgress<BatchProgress>(progress =>
            {
                legacy.Add(progress.Status);
                if (progress.Status == "Processing")
                    throw new InvalidOperationException("observer boom");
            }),
            null,
            TimeSpan.Zero);

        coordinator.Report(Update("a", 0, BatchItemState.Running, 10));
        coordinator.Report(Update("a", 0, BatchItemState.Completed, 100));
        Assert.Equal(["Processing", "Completed"], legacy);
        Assert.Equal("observer boom", coordinator.ProgressObserverError("a"));
    }

    [Fact]
    public async Task OverlappingStudyBatchIsRejectedBeforeDecodersStart()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var assets = Enumerable.Range(0, 4).Select(index => Register(store, scratch.Folder, index)).ToArray();
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoder = new DelegateDecoder((asset, _, _, _) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
                twoInside.TrySetResult();
            Hold(release);
            return MakeRun(asset, 1);
        });
        var request = Request(decoder, parallelism: 2);
        var running = workspace.AnalyzeAsync(assets, null, request, CancellationToken.None);
        try
        {
            Assert.Same(twoInside.Task, await Task.WhenAny(twoInside.Task, Task.Delay(TimeSpan.FromSeconds(15))));
            StudyBatchInProgressException? rejected = null;
            try
            {
                await workspace.AnalyzeAsync(assets, null, request, CancellationToken.None);
                Assert.Fail("The overlapping batch started.");
            }
            catch (StudyBatchInProgressException ex)
            {
                rejected = ex;
            }

            var alternate = new WorkspaceService(new StudyStore(store.DatabasePath.ToUpperInvariant()));
            try
            {
                await alternate.AnalyzeAsync(assets, null, request, CancellationToken.None);
                Assert.Fail("A differently cased study path bypassed the gate.");
            }
            catch (StudyBatchInProgressException ex)
            {
                Assert.Equal(2, ex.ActiveDecoderLimit);
                Assert.Contains("did not start", ex.Message, StringComparison.Ordinal);
            }

            Assert.NotNull(rejected);
            Assert.Equal(2, rejected.ActiveDecoderLimit);
            Assert.Contains("did not start", rejected.Message, StringComparison.Ordinal);
            Assert.Equal(2, Volatile.Read(ref entered));
        }
        finally
        {
            release.TrySetResult();
        }

        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(4, outcome.Completed);
        Assert.Equal(0, outcome.ActiveDecodersAtCompletion);
        Assert.Equal(2, outcome.ObservedMaximumActiveDecoders);
        Assert.InRange(outcome.ObservedMaximumActiveJobs, 1, 2);
        var replay = await workspace.AnalyzeAsync(assets, null, Request(new DelegateDecoder((_, _, _, _) => throw new InvalidOperationException("replay decoded")), parallelism: 2));
        Assert.Equal(4, replay.Reused);
        Assert.Equal(0, replay.ObservedMaximumActiveDecoders);
    }

    [Fact]
    public async Task ResourceBudgetCapsDecodersBelowParallelism()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var assets = Enumerable.Range(0, 4).Select(index => Register(store, scratch.Folder, index)).ToArray();
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoder = new DelegateDecoder((asset, _, _, _) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
                twoInside.TrySetResult();
            Hold(release);
            return MakeRun(asset, 1);
        });
        var running = workspace.AnalyzeAsync(assets, null, Request(decoder, parallelism: 4, budget: 2), CancellationToken.None);
        try
        {
            Assert.Same(twoInside.Task, await Task.WhenAny(twoInside.Task, Task.Delay(TimeSpan.FromSeconds(15))));
            await Task.Delay(100);
            Assert.Equal(2, Volatile.Read(ref entered));
        }
        finally
        {
            release.TrySetResult();
        }

        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(4, outcome.Completed);
        Assert.Equal(4, outcome.MaxDegreeOfParallelism);
        Assert.Equal(2, outcome.EffectiveDecoderCap);
        Assert.Equal(2, outcome.ObservedMaximumActiveJobs);
        Assert.Equal(2, outcome.ObservedMaximumActiveDecoders);
        Assert.Equal(0, outcome.ActiveDecodersAtCompletion);
        Assert.Empty(outcome.Errors);
    }

    [Fact]
    public async Task ThirtyInjectedJobsOverlapOnlyUpToTheExplicitLimit()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var assets = Enumerable.Range(0, 30).Select(index => Register(store, scratch.Folder, index)).ToArray();
        var markers = assets.Select((asset, index) => (asset.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoder = new DelegateDecoder((asset, _, _, _) =>
        {
            if (Interlocked.Increment(ref entered) == 4)
                full.TrySetResult();
            Hold(release);
            return MakeRun(asset, markers[asset.Id]);
        });
        var running = workspace.AnalyzeAsync(assets, null, Request(decoder, parallelism: 4), CancellationToken.None);
        try
        {
            Assert.Same(full.Task, await Task.WhenAny(full.Task, Task.Delay(TimeSpan.FromSeconds(15))));
            await Task.Delay(50);
            Assert.Equal(4, Volatile.Read(ref entered));
        }
        finally
        {
            release.TrySetResult();
        }

        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(30, outcome.Completed);
        Assert.Equal(4, outcome.ObservedMaximumActiveDecoders);
        Assert.Equal(4, outcome.ObservedMaximumActiveJobs);
        Assert.Equal(0, outcome.ActiveDecodersAtCompletion);
        Assert.Empty(outcome.Errors);
        Assert.Equal(assets.Select(asset => asset.Id), outcome.Recordings.Select(item => item.VideoId));
        for (var index = 0; index < assets.Length; index++)
            Assert.Equal(index, store.LatestRun(assets[index].Id)!.Points[0].X);
    }

    [Fact]
    public async Task CancellationAfterPublicationKeepsThatRunAndCancelsTheRest()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var assets = Enumerable.Range(0, 3).Select(index => Register(store, scratch.Folder, index)).ToArray();
        using var cancellation = new CancellationTokenSource();
        string? savedId = null;
        var publisher = new DelegatePublisher((run, _) =>
        {
            store.SaveRun(run);
            savedId = run.VideoId;
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        });
        var interrupted = await workspace.AnalyzeAsync(assets, null, Request(new DelegateDecoder((asset, _, _, _) => MakeRun(asset, 4)), publisher: publisher, parallelism: 1), cancellation.Token);
        Assert.True(interrupted.Cancelled);
        Assert.Equal(1, interrupted.Completed);
        Assert.Empty(interrupted.Errors);
        Assert.Single(interrupted.Recordings, item => item.State == BatchItemState.Completed);
        Assert.Equal(2, interrupted.Recordings.Count(item => item.State == BatchItemState.Cancelled));
        Assert.DoesNotContain(interrupted.Recordings, item => item.State == BatchItemState.Failed);
        Assert.NotNull(savedId);
        Assert.Equal(1, Scalar(store, "SELECT COUNT(*) FROM runs WHERE video_id=$id", ("$id", savedId)));
        Assert.Equal(0, interrupted.ActiveDecodersAtCompletion);

        var resumed = await workspace.AnalyzeAsync(assets, null, Request(new DelegateDecoder((asset, _, _, _) => MakeRun(asset, 4)), parallelism: 1));
        Assert.Equal(3, resumed.Completed);
        Assert.Equal(1, resumed.Reused);
        Assert.Empty(resumed.Errors);
        Assert.Equal(1, Scalar(store, "SELECT COUNT(*) FROM runs WHERE video_id=$id", ("$id", savedId)));
        Assert.Equal(3, Scalar(store, "SELECT COUNT(*) FROM runs"));
    }

    [Fact]
    public async Task AlreadyCancelledBatchPublishesNothing()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var assets = Enumerable.Range(0, 2).Select(index => Register(store, scratch.Folder, index)).ToArray();
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var outcome = await workspace.AnalyzeAsync(assets, null, Request(new DelegateDecoder((asset, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return MakeRun(asset, 1);
        })), cancellation.Token);
        Assert.Equal(0, calls);
        Assert.Equal(0, outcome.Completed);
        Assert.True(outcome.Cancelled);
        Assert.Empty(outcome.Errors);
        Assert.All(outcome.Recordings, item => Assert.Equal(BatchItemState.Cancelled, item.State));
        Assert.Equal(0, Scalar(store, "SELECT COUNT(*) FROM runs"));
        Assert.Equal(0, outcome.ActiveDecodersAtCompletion);
    }

    [Fact]
    public async Task EmptyBatchAndInvalidBudgetsDoNotStartWorkOrStickTheGate()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var calls = 0;
        var decoder = new DelegateDecoder((asset, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return MakeRun(asset, 1);
        });
        var empty = await workspace.AnalyzeAsync([], null, Request(decoder), CancellationToken.None);
        Assert.Equal(0, empty.Completed);
        Assert.Empty(empty.Recordings);
        Assert.Empty(empty.Errors);
        Assert.False(empty.Cancelled);
        Assert.Equal(0, calls);

        var asset = Register(store, scratch.Folder, 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => workspace.AnalyzeAsync([asset], null, new NativeBatchRequest { ResourceBudget = 0 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => workspace.AnalyzeAsync([asset], null, new NativeBatchRequest { MaxDegreeOfParallelism = 0 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => workspace.AnalyzeAsync([asset], null, new NativeBatchRequest { ProgressMinimumInterval = TimeSpan.FromMilliseconds(-1) }, CancellationToken.None));
        Assert.Equal(0, calls);
        var outcome = await workspace.AnalyzeAsync([asset], null, Request(decoder, parallelism: 1));
        Assert.Equal(1, outcome.Completed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SettingsSnapshotIgnoresLaterEditsToTheCallerList()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var asset = Register(store, scratch.Folder, 0, Arena(31));
        var frozenHash = StudyStore.SettingsHash(asset.Settings);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenExclusions = -1;
        var decoder = new DelegateDecoder((frozen, _, _, _) =>
        {
            started.TrySetResult();
            Hold(release);
            seenExclusions = frozen.Settings.Exclusions.Count;
            return MakeRun(frozen, 7);
        });
        var running = workspace.AnalyzeAsync([asset], null, Request(decoder, parallelism: 1), CancellationToken.None);
        try
        {
            Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(15))));
            asset.Settings.Exclusions.Add(new ExclusionRegion(.1, .1, .2, .2));
            Assert.NotEqual(frozenHash, StudyStore.SettingsHash(asset.Settings));
        }
        finally
        {
            release.TrySetResult();
        }

        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, seenExclusions);
        Assert.Empty(outcome.Errors);
        var stored = store.LatestRun(asset.Id)!;
        Assert.Empty(stored.Settings.Exclusions);
        Assert.Equal(31, stored.Settings.Threshold);
        Assert.Equal(frozenHash, StudyStore.SettingsHash(stored.Settings));
        Assert.Equal(7, stored.Points[0].X);
    }

    [Fact]
    public async Task PublicationIsAwaitedBeforeTheBatchReturns()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var asset = Register(store, scratch.Folder, 0);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new DelegatePublisher(async (run, _) =>
        {
            started.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(15));
            store.SaveRun(run);
        });
        var running = workspace.AnalyzeAsync([asset], null, Request(new DelegateDecoder((item, _, _, _) => MakeRun(item, 2)), publisher: publisher, parallelism: 1));
        try
        {
            Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(15))));
            Assert.Equal(0, Scalar(store, "SELECT COUNT(*) FROM runs"));
            Assert.False(running.IsCompleted);
        }
        finally
        {
            gate.TrySetResult();
        }

        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, outcome.Completed);
        Assert.True(outcome.Recordings[0].Published);
        Assert.Equal(1, Scalar(store, "SELECT COUNT(*) FROM runs"));
    }

    [Fact]
    public async Task ChangedMissingAndMidRunSourcesFailOnlyThoseClips()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var good = Register(store, scratch.Folder, 0);
        var changed = Register(store, scratch.Folder, 1);
        var missing = Register(store, scratch.Folder, 2);
        var moved = Register(store, scratch.Folder, 3);
        File.WriteAllBytes(changed.Path, Encoding.UTF8.GetBytes("changed-bytes"));
        File.Delete(missing.Path);
        var movedPath = Path.Combine(scratch.Folder, "moved-away.bin");
        File.Move(moved.Path, movedPath);
        var calls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var outcome = await workspace.AnalyzeAsync([good, changed, missing, moved], null, Request(new DelegateDecoder((asset, _, _, _) =>
        {
            calls.AddOrUpdate(asset.Id, 1, (_, count) => count + 1);
            return MakeRun(asset, 3);
        }), parallelism: 4));
        Assert.Equal(1, calls[good.Id]);
        Assert.False(calls.ContainsKey(changed.Id));
        Assert.False(calls.ContainsKey(missing.Id));
        Assert.False(calls.ContainsKey(moved.Id));
        Assert.Equal(3, outcome.Errors.Count);
        Assert.Contains("changed", outcome.Errors[changed.Id], StringComparison.Ordinal);
        Assert.Contains("Original video is missing.", outcome.Errors[missing.Id], StringComparison.Ordinal);
        Assert.Contains("Original video is missing.", outcome.Errors[moved.Id], StringComparison.Ordinal);
        Assert.DoesNotContain(good.Id, outcome.Errors.Keys);
        Assert.Equal(1, Scalar(store, "SELECT COUNT(*) FROM runs"));
        Assert.Equal(good.SourceSha256, store.LatestRun(good.Id)!.SourceSha256);

        var during = Register(store, scratch.Folder, 4);
        var sibling = Register(store, scratch.Folder, 5);
        var hashes = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hasher = new DelegateHasher(path =>
        {
            var count = hashes.AddOrUpdate(path, 1, (_, value) => value + 1);
            if (string.Equals(path, during.Path, StringComparison.OrdinalIgnoreCase) && count >= 2)
                return new string('b', 64);
            return OpenCvTracker.HashFile(path);
        });
        var mid = await workspace.AnalyzeAsync([during, sibling], null, Request(new DelegateDecoder((asset, _, _, _) => MakeRun(asset, 5)), hasher, parallelism: 2));
        Assert.Contains("during processing", mid.Errors[during.Id], StringComparison.Ordinal);
        Assert.DoesNotContain(sibling.Id, mid.Errors.Keys);
        Assert.Null(store.LatestRun(during.Id));
        Assert.NotNull(store.LatestRun(sibling.Id));
        Assert.Equal(sibling.SourceSha256, store.LatestRun(sibling.Id)!.SourceSha256);
    }

    [Fact]
    public async Task MissingSourceCanRelinkOnlyWhenTheHashStillMatches()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var asset = Register(store, scratch.Folder, 0);
        var stale = asset;
        var moved = Path.Combine(scratch.Folder, "relinked.bin");
        File.Move(asset.Path, moved);
        var relinked = store.AddOrGet(asset with { Path = moved });
        Assert.Equal(asset.Id, relinked.Id);
        Assert.Equal(Path.GetFullPath(moved), Path.GetFullPath(relinked.Path));
        string? seen = null;
        var outcome = await workspace.AnalyzeAsync([stale], null, Request(new DelegateDecoder((item, path, _, _) =>
        {
            seen = path;
            return MakeRun(item, 6);
        })));
        Assert.Empty(outcome.Errors);
        var resolved = seen ?? throw new InvalidOperationException("The decoder path was not recorded.");
        Assert.Equal(Path.GetFullPath(moved), Path.GetFullPath(resolved));
        Assert.Equal(1, Scalar(store, "SELECT COUNT(*) FROM runs WHERE video_id=$id", ("$id", asset.Id)));

        var mismatch = Register(store, scratch.Folder, 1);
        var mismatchStale = mismatch;
        var mismatchPath = Path.Combine(scratch.Folder, "relinked-mismatch.bin");
        File.Move(mismatch.Path, mismatchPath);
        store.AddOrGet(mismatch with { Path = mismatchPath });
        File.WriteAllBytes(mismatchPath, Encoding.UTF8.GetBytes("different-bytes"));
        var calls = 0;
        var rejected = await workspace.AnalyzeAsync([mismatchStale], null, Request(new DelegateDecoder((item, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return MakeRun(item, 6);
        })));
        Assert.Equal(0, calls);
        Assert.Contains("Relinked source", rejected.Errors[mismatch.Id], StringComparison.Ordinal);
        Assert.Null(store.LatestRun(mismatch.Id));
    }

    [Fact]
    public async Task PostCommitAuditFailureStaysStoredAndIsNotRepublished()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var workspace = new WorkspaceService(store);
        var asset = Register(store, scratch.Folder, 0);
        // The trigger is installed after batch_started, inside the first publication.
        var publisher = new DelegatePublisher((run, _) =>
        {
            Execute(store, "CREATE TRIGGER audit_fail BEFORE INSERT ON audit BEGIN SELECT RAISE(ABORT, 'audit failed after commit'); END;");
            store.SaveRun(run);
            return ValueTask.CompletedTask;
        });
        BatchOutcome outcome;
        try
        {
            outcome = await workspace.AnalyzeAsync([asset], null, Request(new DelegateDecoder((item, _, _, _) => MakeRun(item, 8)), publisher: publisher, parallelism: 1));
        }
        finally
        {
            Execute(store, "DROP TRIGGER IF EXISTS audit_fail;");
        }

        Assert.Empty(outcome.Errors);
        Assert.Equal(BatchItemState.Completed, outcome.Recordings[0].State);
        Assert.True(outcome.Recordings[0].CompletedAnalysis);
        Assert.Contains(outcome.Warnings, warning => warning.Contains("will not be published again", StringComparison.Ordinal) && warning.Contains("audit failed after commit", StringComparison.Ordinal));
        Assert.Equal(1, Scalar(store, "SELECT COUNT(*) FROM runs"));
        var runId = store.LatestRun(asset.Id)!.Id;
        var opened = new WorkspaceService(new StudyStore(path)).ReadPersistedRecordingEvidence();
        var evidence = Assert.Single(opened, item => item.VideoId == asset.Id);
        Assert.Equal(RecordingAttempts.AuditWarning, evidence.LatestAttempt);
        Assert.True(evidence.LatestAttemptIsCompletedAnalysis);
        Assert.Equal(runId, evidence.MatchingRunId);
        Assert.Contains("will not be published again", evidence.Warning, StringComparison.Ordinal);

        var calls = 0;
        var resumed = await workspace.AnalyzeAsync([asset], null, Request(new DelegateDecoder((_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("republish decoded a committed run");
        }), parallelism: 1));
        Assert.Equal(0, calls);
        Assert.Equal(1, resumed.Reused);
        Assert.Empty(resumed.Errors);
        Assert.Equal(runId, store.LatestRun(asset.Id)!.Id);
        Assert.Equal(1, Scalar(store, "SELECT COUNT(*) FROM runs"));
        var retained = Assert.Single(new WorkspaceService(new StudyStore(path)).ReadPersistedRecordingEvidence(), item => item.VideoId == asset.Id);
        Assert.Contains("will not be published again", retained.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedPublicationAndDecoderFailuresKeepTheEarlierSuccess()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var good = Register(store, scratch.Folder, 0);
        var rejected = Register(store, scratch.Folder, 1);
        var statuses = new List<string>();
        var publisher = new DelegatePublisher((run, _) =>
        {
            if (run.VideoId == rejected.Id)
                throw new IOException("disk full");
            store.SaveRun(run);
            return ValueTask.CompletedTask;
        });
        var published = await workspace.AnalyzeAsync([good, rejected], new InlineProgress<BatchProgress>(progress => statuses.Add(progress.Status)), Request(new DelegateDecoder((asset, _, _, _) => MakeRun(asset, asset.Id == good.Id ? 11 : 12)), publisher: publisher, parallelism: 1));
        Assert.Contains("disk full", published.Errors[rejected.Id], StringComparison.Ordinal);
        Assert.DoesNotContain(published.Warnings, warning => warning.Contains("will not be published again", StringComparison.Ordinal));
        Assert.DoesNotContain(good.Id, published.Errors.Keys);
        Assert.Equal(11, store.LatestRun(good.Id)!.Points[0].X);
        Assert.Null(store.LatestRun(rejected.Id));
        Assert.Equal(0, published.ActiveDecodersAtCompletion);

        var broken = Register(store, scratch.Folder, 2);
        var incomplete = Register(store, scratch.Folder, 3);
        var swapped = Register(store, scratch.Folder, 4);
        var isolated = await workspace.AnalyzeAsync([good, broken, incomplete, swapped], new InlineProgress<BatchProgress>(progress => statuses.Add(progress.Status)), Request(new DelegateDecoder((asset, _, _, _) =>
        {
            if (asset.Id == broken.Id)
                throw new InvalidOperationException("decoder broke");
            if (asset.Id == incomplete.Id)
                return new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = asset.Settings, Points = [], NominalTimingVerified = false, AlgorithmContract = AlgorithmIdentity.Contract };
            if (asset.Id == swapped.Id)
                return MakeRun(good, 99);
            return MakeRun(asset, 11);
        }), parallelism: 1));
        Assert.Contains("decoder broke", isolated.Errors[broken.Id], StringComparison.Ordinal);
        Assert.Contains("Incomplete analysis was not published.", isolated.Errors[incomplete.Id], StringComparison.Ordinal);
        Assert.Contains("different recording", isolated.Errors[swapped.Id], StringComparison.Ordinal);
        Assert.DoesNotContain(good.Id, isolated.Errors.Keys);
        Assert.Equal(1, Scalar(store, "SELECT COUNT(*) FROM runs"));
        Assert.Equal(11, store.LatestRun(good.Id)!.Points[0].X);
        Assert.Contains(statuses, status => status.StartsWith("Failed:", StringComparison.Ordinal) && status.Contains("decoder broke", StringComparison.Ordinal));
        Assert.Equal(0, isolated.ActiveDecodersAtCompletion);
    }

    [Fact]
    public async Task CompletionOrderDoesNotChangeRecordingAssociation()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var assets = Enumerable.Range(0, 4).Select(index => Register(store, scratch.Folder, index)).ToArray();
        var oddPublicationsCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oddFinished = 0;
        var committed = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var publisher = new DelegatePublisher((run, _) =>
        {
            store.SaveRun(run);
            committed[run.VideoId] = run.Id;
            return ValueTask.CompletedTask;
        });
        var progress = new InlineProgress<RecordingBatchProgress>(update =>
        {
            if (update.ItemState != BatchItemState.Completed || update.InputIndex % 2 == 0) return;
            Assert.True(committed.TryGetValue(update.VideoId, out var runId));
            Assert.Equal(runId, store.LatestRun(update.VideoId)!.Id);
            // Completed is emitted only after actual committed publication and its completion ordinal.
            if (Interlocked.Increment(ref oddFinished) == 2) oddPublicationsCompleted.TrySetResult();
        });
        var outcome = await workspace.AnalyzeAsync(assets, null, Request(new DelegateDecoder((asset, path, _, _) =>
        {
            var index = int.Parse(Path.GetFileNameWithoutExtension(path).Split('-')[1], CultureInfo.InvariantCulture);
            if (index % 2 == 0) Hold(oddPublicationsCompleted);
            return MakeRun(asset, index);
        }), publisher: publisher, parallelism: 4, recordingProgress: progress));
        Assert.Empty(outcome.Warnings);
        Assert.Equal(assets.Select(asset => asset.Id), outcome.Recordings.Select(item => item.VideoId));
        Assert.True(outcome.Recordings[1].CompletionOrdinal < outcome.Recordings[0].CompletionOrdinal);
        for (var index = 0; index < assets.Length; index++)
        {
            Assert.Equal(index, outcome.Recordings[index].InputIndex);
            Assert.Equal(index, store.LatestRun(assets[index].Id)!.Points[0].X);
        }
    }

    [Fact]
    public async Task DuplicateRecordingIdsKeepTheFirstAsset()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var first = Register(store, scratch.Folder, 0);
        var otherPath = Path.Combine(scratch.Folder, "other.bin");
        File.WriteAllBytes(otherPath, Encoding.UTF8.GetBytes("other"));
        var twin = first with { Path = otherPath, SourceSha256 = OpenCvTracker.HashFile(otherPath) };
        string? seen = null;
        var outcome = await workspace.AnalyzeAsync([first, twin], null, Request(new DelegateDecoder((asset, path, _, _) =>
        {
            seen = path;
            return MakeRun(asset, 1);
        }), parallelism: 1));
        Assert.Single(outcome.Recordings);
        var resolved = seen ?? throw new InvalidOperationException("The decoder path was not recorded.");
        Assert.Equal(Path.GetFullPath(first.Path), Path.GetFullPath(resolved));
        Assert.Equal(first.SourceSha256, store.LatestRun(first.Id)!.SourceSha256);
    }

    [Fact]
    public async Task LegacyPercentIsCollectionCompletionWhileClipPercentStaysSeparate()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var assets = Enumerable.Range(0, 2).Select(index => Register(store, scratch.Folder, index)).ToArray();
        var legacy = new List<BatchProgress>();
        var typed = new List<RecordingBatchProgress>();
        await workspace.AnalyzeAsync(assets, new InlineProgress<BatchProgress>(legacy.Add), Request(new DelegateDecoder((asset, _, progress, _) =>
        {
            progress?.Report(80);
            return MakeRun(asset, 1);
        }), parallelism: 1, recordingProgress: new InlineProgress<RecordingBatchProgress>(typed.Add)));
        Assert.Contains(typed, item => item.ItemPercent == 80 && item.CollectionPercent == 0);
        Assert.Contains(legacy, item => item.Status == "Completed" && item.Complete == 1 && item.Total == 2 && item.CurrentPercent == 50);
        Assert.Equal(100, legacy[^1].CurrentPercent);
        Assert.Equal(2, legacy[^1].Complete);
        Assert.Equal("Completed", legacy[^1].Status);
    }

    [Fact]
    public async Task ProgressObserverExceptionKeepsTheStoredRun()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var workspace = new WorkspaceService(store);
        var assets = Enumerable.Range(0, 2).Select(index => Register(store, scratch.Folder, index)).ToArray();
        var outcome = await workspace.AnalyzeAsync(assets, new InlineProgress<BatchProgress>(progress =>
        {
            if (progress.Filename == assets[1].Filename && progress.Status == "Processing")
                throw new InvalidOperationException("grid failed");
        }), Request(new DelegateDecoder((asset, _, _, _) => MakeRun(asset, 4)), parallelism: 1));
        Assert.Empty(outcome.Errors);
        Assert.Equal(2, outcome.Completed);
        Assert.Contains("grid failed", outcome.Recordings[1].ProgressObserverError, StringComparison.Ordinal);
        Assert.Contains(outcome.Warnings, warning => warning.Contains(assets[1].Id, StringComparison.Ordinal) && warning.Contains("grid failed", StringComparison.Ordinal));
        Assert.Equal(2, Scalar(store, "SELECT COUNT(*) FROM runs"));
        var reopened = new WorkspaceService(new StudyStore(path)).ReadPersistedRecordingEvidence();
        var evidence = Assert.Single(reopened, item => item.VideoId == assets[1].Id);
        Assert.Contains("grid failed", evidence.ProgressObserverError, StringComparison.Ordinal);
        Assert.True(evidence.LatestAttemptIsCompletedAnalysis);
    }

    [Fact]
    public async Task DisposalErrorDoesNotDropASuccessfulPublication()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var asset = Register(store, scratch.Folder, 0);
        var outcome = await workspace.AnalyzeAsync([asset], null, Request(
            new DelegateDecoder((item, _, _, _) => MakeRun(item, 2)),
            parallelism: 1,
            openResource: (_, _) => new ValueTask<IAsyncDisposable?>(new ThrowDispose("decoder release failed"))));
        Assert.Empty(outcome.Errors);
        Assert.Equal(BatchItemState.Completed, outcome.Recordings[0].State);
        Assert.True(outcome.Recordings[0].CompletedAnalysis);
        Assert.Contains("decoder release failed", outcome.Recordings[0].DisposalError, StringComparison.Ordinal);
        Assert.Contains(outcome.Warnings, warning => warning.Contains("decoder release failed", StringComparison.Ordinal));
        Assert.NotNull(store.LatestRun(asset.Id));
        Assert.Equal(0, outcome.ActiveDecodersAtCompletion);
    }

    [Fact]
    public async Task RestartEvidenceComesFromTheStoreAndJournal()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var workspace = new WorkspaceService(store);
        var assets = Enumerable.Range(0, 4).Select(index => Register(store, scratch.Folder, index)).ToArray();
        await workspace.AnalyzeAsync([assets[0], assets[1]], null, Request(new DelegateDecoder((asset, _, _, _) =>
        {
            if (asset.Id == assets[1].Id)
                throw new InvalidOperationException("decoder broke");
            return MakeRun(asset, 1);
        }), parallelism: 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await workspace.AnalyzeAsync([assets[2]], null, Request(new DelegateDecoder((_, _, _, _) => throw new InvalidOperationException("cancelled clip was decoded"))), cancellation.Token);

        var evidence = new WorkspaceService(new StudyStore(path)).ReadPersistedRecordingEvidence();
        var completed = Assert.Single(evidence, item => item.VideoId == assets[0].Id);
        Assert.Equal(RecordingAttempts.Completed, completed.LatestAttempt);
        Assert.True(completed.LatestAttemptIsCompletedAnalysis);
        Assert.Equal(store.LatestRun(assets[0].Id)!.Id, completed.MatchingRunId);
        var failed = Assert.Single(evidence, item => item.VideoId == assets[1].Id);
        Assert.Equal(RecordingAttempts.Failed, failed.LatestAttempt);
        Assert.False(failed.LatestAttemptIsCompletedAnalysis);
        Assert.False(failed.MatchingRunIntact);
        Assert.Contains("decoder broke", failed.Error, StringComparison.Ordinal);
        var cancelled = Assert.Single(evidence, item => item.VideoId == assets[2].Id);
        Assert.Equal(RecordingAttempts.Cancelled, cancelled.LatestAttempt);
        Assert.False(cancelled.LatestAttemptIsCompletedAnalysis);
        Assert.False(cancelled.MatchingRunIntact);
        var untouched = Assert.Single(evidence, item => item.VideoId == assets[3].Id);
        Assert.Equal(RecordingAttempts.NotAnalyzed, untouched.LatestAttempt);
        Assert.False(untouched.LatestAttemptIsCompletedAnalysis);
    }

    [Fact]
    public async Task ChangedSourceAfterSuccessKeepsTheEarlierRunVisibleAsFailedAttempt()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var workspace = new WorkspaceService(store);
        var asset = Register(store, scratch.Folder, 0);
        await workspace.AnalyzeAsync([asset], null, Request(new DelegateDecoder((item, _, _, _) => MakeRun(item, 15)), parallelism: 1));
        var before = store.LatestRun(asset.Id)!;
        File.WriteAllBytes(asset.Path, Encoding.UTF8.GetBytes("mutated-source"));
        var calls = 0;
        var second = await workspace.AnalyzeAsync([asset], null, Request(new DelegateDecoder((item, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return MakeRun(item, 16);
        })));
        Assert.Equal(0, calls);
        Assert.Contains("changed", second.Errors[asset.Id], StringComparison.Ordinal);
        Assert.Equal(1, Scalar(store, "SELECT COUNT(*) FROM runs WHERE video_id=$id", ("$id", asset.Id)));
        var after = store.LatestRun(asset.Id)!;
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(15, after.Points[0].X);
        var evidence = Assert.Single(new WorkspaceService(new StudyStore(path)).ReadPersistedRecordingEvidence(), item => item.VideoId == asset.Id);
        Assert.Equal(RecordingAttempts.Failed, evidence.LatestAttempt);
        Assert.False(evidence.LatestAttemptIsCompletedAnalysis);
        Assert.True(evidence.MatchingRunIntact);
        Assert.Equal(before.Id, evidence.MatchingRunId);
        Assert.Contains("earlier matching run", evidence.Note, StringComparison.Ordinal);
        Assert.Contains("not a completed analysis", evidence.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JournalAgreesWithStoredRunsAndRejectsCorruption()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var workspace = new WorkspaceService(store);
        var asset = Register(store, scratch.Folder, 0);
        await workspace.AnalyzeAsync([asset], null, Request(new DelegateDecoder((item, _, _, _) => MakeRun(item, 1)), parallelism: 1));
        var journal = path + ".recording-operations.json";
        File.Delete(journal);
        var fromRun = Assert.Single(new WorkspaceService(new StudyStore(path)).ReadPersistedRecordingEvidence());
        Assert.Equal(RecordingAttempts.Completed, fromRun.LatestAttempt);
        Assert.True(fromRun.LatestAttemptIsCompletedAnalysis);

        await workspace.AnalyzeAsync([asset], null, Request(new DelegateDecoder((_, _, _, _) => throw new InvalidOperationException("should reuse"))));
        Execute(store, "DELETE FROM runs;");
        var inconsistent = Assert.Single(NativeBatchAdapter.ReadEvidence(new StudyStore(path)));
        Assert.Equal(RecordingAttempts.Inconsistent, inconsistent.LatestAttempt);
        Assert.False(inconsistent.LatestAttemptIsCompletedAnalysis);
        Assert.Contains("not stored", inconsistent.Error, StringComparison.Ordinal);

        File.WriteAllText(journal, "{not json");
        var unreadable = Assert.Throws<InvalidDataException>(() => NativeBatchAdapter.ReadEvidence(store));
        Assert.Contains("unreadable", unreadable.Message, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(journal, "{\"Schema\":2,\"Entries\":[]}");
        var unsupported = Assert.Throws<InvalidDataException>(() => NativeBatchAdapter.ReadEvidence(store));
        Assert.Contains("schema", unsupported.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(store.Videos());
    }

    [Fact]
    public async Task ChangedSettingsAndLegacyAlgorithmAreNotReused()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var asset = Register(store, scratch.Folder, 0, Arena(31));
        string? reused = null;
        var first = await workspace.AnalyzeAsync([asset], null, Request(new DelegateDecoder((item, _, _, _) => MakeRun(item, 4)), parallelism: 1));
        var originalId = store.LatestRun(asset.Id)!.Id;
        var edited = asset with { Settings = Arena(35) };
        store.SaveVideo(edited);
        var calls = 0;
        var changed = await workspace.AnalyzeAsync([edited], new InlineProgress<BatchProgress>(progress => reused ??= progress.Status == "Reused matching immutable run" ? progress.Status : reused), Request(new DelegateDecoder((item, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return MakeRun(item, 5);
        }), parallelism: 1));
        Assert.Equal(0, changed.Reused);
        Assert.Equal(1, calls);
        Assert.Equal(2, Scalar(store, "SELECT COUNT(*) FROM runs WHERE video_id=$id", ("$id", asset.Id)));
        Assert.Equal(1, Scalar(store, "SELECT COUNT(*) FROM runs WHERE id=$id", ("$id", originalId)));
        Assert.Equal(35, store.LatestRun(asset.Id)!.Settings.Threshold);
        Assert.Null(reused);
        Assert.Equal(1, first.Completed);

        var latest = store.LatestRun(asset.Id)!;
        store.SaveRun(latest with { Id = Guid.NewGuid().ToString("N"), AlgorithmContract = "legacy-contract" });
        calls = 0;
        var replay = await workspace.AnalyzeAsync([edited], new InlineProgress<BatchProgress>(progress =>
        {
            if (progress.Status == "Reused matching immutable run")
                reused = progress.Status;
        }), Request(new DelegateDecoder((item, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return MakeRun(item, 6);
        }), parallelism: 1));
        Assert.Equal(0, replay.Reused);
        Assert.Equal(1, calls);
        Assert.Null(reused);
    }

    [Fact]
    public async Task OpenCvIsolatesAGoodClipFromMissingAndChangedSources()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var goodPath = Path.Combine(scratch.Folder, "good.avi");
        var missingPath = Path.Combine(scratch.Folder, "missing.avi");
        var changedPath = Path.Combine(scratch.Folder, "changed.avi");
        WriteClip(goodPath, 0);
        WriteClip(missingPath, 1);
        WriteClip(changedPath, 2);
        var assets = ImportClips(workspace, store, [goodPath, missingPath, changedPath]);
        File.Delete(missingPath);
        File.WriteAllBytes(changedPath, Encoding.UTF8.GetBytes("not-the-original-video"));
        var outcome = await workspace.AnalyzeAsync(assets);
        Assert.Equal(1, outcome.Completed);
        Assert.Equal(2, outcome.Errors.Count);
        Assert.DoesNotContain(assets[0].Id, outcome.Errors.Keys);
        Assert.Contains("Original video is missing.", outcome.Errors[assets[1].Id], StringComparison.Ordinal);
        Assert.Contains("changed", outcome.Errors[assets[2].Id], StringComparison.Ordinal);
        var run = store.LatestRun(assets[0].Id)!;
        Assert.Equal(assets[0].Id, run.VideoId);
        Assert.Equal(assets[0].SourceSha256, run.SourceSha256);
        Assert.Equal(12, run.Metrics.TrackedFrames);
        Assert.Equal(0, outcome.ActiveDecodersAtCompletion);
        Assert.InRange(outcome.ObservedMaximumActiveDecoders, 1, outcome.EffectiveDecoderCap);
    }

    [Fact(Timeout = 180000)]
    public async Task OpenCvThirtyVideosOverlapAtTheProductionCap()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var workspace = new WorkspaceService(store);
        var paths = new string[30];
        for (var index = 0; index < paths.Length; index++)
        {
            paths[index] = Path.Combine(scratch.Folder, $"mouse-{index:00}.avi");
            WriteClip(paths[index], index);
        }

        var assets = ImportClips(workspace, store, paths);
        var entered = 0;
        var release = new ManualResetEventSlim(false);
        var decoder = new DelegateDecoder((asset, path, progress, cancellation) =>
        {
            var now = Interlocked.Increment(ref entered);
            if (now >= 2)
                release.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15), cancellation))
                throw new TimeoutException("Two OpenCV decoders did not overlap.");
            var frozen = string.Equals(Path.GetFullPath(asset.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ? asset : asset with { Path = path };
            return OpenCvTracker.Analyze(frozen, progress, cancellation);
        });
        var clock = Stopwatch.StartNew();
        var outcome = await workspace.AnalyzeAsync(assets, null, Request(decoder, parallelism: 2));
        clock.Stop();
        var line = FormattableString.Invariant($"P01 opencv30 elapsed_ms={clock.ElapsedMilliseconds} peak_decoders={outcome.ObservedMaximumActiveDecoders} peak_jobs={outcome.ObservedMaximumActiveJobs} completed={outcome.Completed}");
        output.WriteLine(line);
        Console.WriteLine(line);
        File.AppendAllText(MeasurementLog, line + Environment.NewLine);
        Assert.Equal(30, outcome.Completed);
        Assert.Equal(0, outcome.Reused);
        Assert.Empty(outcome.Errors);
        Assert.Equal(2, outcome.ObservedMaximumActiveDecoders);
        Assert.Equal(2, outcome.ObservedMaximumActiveJobs);
        Assert.Equal(0, outcome.ActiveDecodersAtCompletion);
        foreach (var asset in assets)
        {
            var run = store.LatestRun(asset.Id)!;
            Assert.Equal(asset.Id, run.VideoId);
            Assert.Equal(asset.SourceSha256, run.SourceSha256);
            Assert.Equal(12, run.Metrics.TrackedFrames);
        }
    }

    [Fact(Timeout = 180000)]
    public async Task MeasureOneAndTwoWorkersOnTheSameSyntheticClips()
    {
        using var scratch = new Scratch();
        var paths = new string[30];
        for (var index = 0; index < paths.Length; index++)
        {
            paths[index] = Path.Combine(scratch.Folder, $"mouse-{index:00}.avi");
            WriteClip(paths[index], index);
        }

        var warmupStore = new StudyStore(Path.Combine(scratch.Folder, "warmup.morrisdb"));
        var warmup = new WorkspaceService(warmupStore);
        await warmup.AnalyzeAsync(ImportClips(warmup, warmupStore, [paths[0]]), null, new NativeBatchRequest { MaxDegreeOfParallelism = 1, ProgressMinimumInterval = TimeSpan.Zero });

        var one = await Measure(1, paths, Path.Combine(scratch.Folder, "one.morrisdb"));
        var two = await Measure(2, paths, Path.Combine(scratch.Folder, "two.morrisdb"));
        output.WriteLine(one.Line);
        output.WriteLine(two.Line);
        Console.WriteLine(one.Line);
        Console.WriteLine(two.Line);
        File.AppendAllText(MeasurementLog, one.Line + Environment.NewLine + two.Line + Environment.NewLine);

        Assert.Equal(30, one.Outcome.Completed);
        Assert.Equal(30, two.Outcome.Completed);
        Assert.Empty(one.Outcome.Errors);
        Assert.Empty(two.Outcome.Errors);
        Assert.Equal(1, one.Outcome.ObservedMaximumActiveJobs);
        Assert.Equal(1, one.Outcome.ObservedMaximumActiveDecoders);
        Assert.InRange(two.Outcome.ObservedMaximumActiveJobs, 1, 2);
        Assert.InRange(two.Outcome.ObservedMaximumActiveDecoders, 1, 2);
        Assert.Equal(0, one.Outcome.ActiveDecodersAtCompletion);
        Assert.Equal(0, two.Outcome.ActiveDecodersAtCompletion);
        Assert.Equal(one.Signatures, two.Signatures);
        Assert.Contains("not a biological sample size", one.Outcome.CountInterpretation, StringComparison.Ordinal);
    }

    private async Task<(BatchOutcome Outcome, string Line, Dictionary<string, string> Signatures)> Measure(int workers, IReadOnlyList<string> paths, string databasePath)
    {
        var store = new StudyStore(databasePath);
        var workspace = new WorkspaceService(store);
        var assets = ImportClips(workspace, store, paths);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var beforeWs = process.WorkingSet64;
        var beforeGc = GC.GetTotalMemory(false);
        var beforeAlloc = GC.GetTotalAllocatedBytes(true);
        var peak = new PeakBox { Value = beforeWs };
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    process.Refresh();
                    var value = process.WorkingSet64;
                    while (true)
                    {
                        var seen = Volatile.Read(ref peak.Value);
                        if (value <= seen || Interlocked.CompareExchange(ref peak.Value, value, seen) == seen)
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        var clock = Stopwatch.StartNew();
        BatchOutcome outcome;
        try
        {
            outcome = await workspace.AnalyzeAsync(assets, null, new NativeBatchRequest { MaxDegreeOfParallelism = workers, ProgressMinimumInterval = TimeSpan.Zero });
        }
        finally
        {
            clock.Stop();
            await stop.CancelAsync();
            await sampler;
        }

        process.Refresh();
        var afterWs = process.WorkingSet64;
        var afterGc = GC.GetTotalMemory(false);
        var afterAlloc = GC.GetTotalAllocatedBytes(true);
        var peakWs = Math.Max(Volatile.Read(ref peak.Value), Math.Max(beforeWs, afterWs));
        var line = FormattableString.Invariant($"P01 measurement workers={workers} elapsed_ms={clock.ElapsedMilliseconds} peak_jobs={outcome.ObservedMaximumActiveJobs} peak_decoders={outcome.ObservedMaximumActiveDecoders} working_set_before={beforeWs} working_set_peak={peakWs} working_set_after={afterWs} gc_bytes_before={beforeGc} gc_bytes_after={afterGc} allocated_bytes={afterAlloc - beforeAlloc}");
        var signatures = assets.ToDictionary(asset => asset.Filename, asset => Signature(store.LatestRun(asset.Id)!), StringComparer.Ordinal);
        return (outcome, line, signatures);
    }

    private static string MeasurementLog => Path.Combine(Path.GetTempPath(), "p01-native-batch-measurements.txt");

    private static void Hold(TaskCompletionSource gate)
    {
        if (!gate.Task.Wait(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("The test gate was not released.");
    }

    private static BatchProgressUpdate Update(string id, int index, BatchItemState state, double? percent, int completedJobs = 99, string? message = null) => new()
    {
        JobId = id,
        InputIndex = index,
        State = state,
        TotalJobs = 99,
        QueuedJobs = 99,
        RunningJobs = 99,
        CompletedJobs = completedJobs,
        ReusedJobs = 99,
        FailedJobs = 99,
        CancelledJobs = 99,
        ActiveJobs = 99,
        ParallelismLimit = 99,
        ItemPercent = percent,
        Message = message ?? state.ToString()
    };

    private static TrackingSettings Arena(int threshold = 30) => new()
    {
        Cx = .5,
        Cy = .5,
        Rx = .4,
        Ry = .4,
        ArenaConfirmed = true,
        Threshold = threshold
    };

    private static VideoAsset Register(StudyStore store, string folder, int index, TrackingSettings? settings = null)
    {
        var path = Path.Combine(folder, $"clip-{index:00}.bin");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(FormattableString.Invariant($"clip-{index:00}-{Guid.NewGuid():N}")));
        var asset = new VideoAsset
        {
            Path = path,
            SourceSha256 = OpenCvTracker.HashFile(path),
            Metadata = new VideoMetadata(200, 200, 10, 12),
            Settings = settings ?? Arena(),
            AnimalId = FormattableString.Invariant($"clip-{index:00}")
        };
        store.SaveVideo(asset);
        return asset;
    }

    private static AnalysisRun MakeRun(VideoAsset asset, int marker)
    {
        var first = (int)Math.Ceiling(asset.Settings.StartSeconds * asset.Metadata.Fps);
        var last = Math.Min(asset.Metadata.FrameCount, asset.Settings.EndSeconds is { } end ? (int)Math.Ceiling(end * asset.Metadata.Fps) : asset.Metadata.FrameCount);
        var points = Enumerable.Range(first, last - first).Select(frame => new TrackPoint(frame, frame / asset.Metadata.Fps, marker, 100, PointStatus.Tracked, 1)).ToList();
        return new AnalysisRun
        {
            VideoId = asset.Id,
            SourceSha256 = asset.SourceSha256,
            Metadata = asset.Metadata,
            Settings = asset.Settings,
            Points = points,
            Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings),
            NominalTimingVerified = true,
            AlgorithmContract = AlgorithmIdentity.Contract
        };
    }

    private static void WriteClip(string path, int animal)
    {
        using var writer = new VideoWriter(path, FourCC.MJPG, 10, new Size(200, 200));
        Assert.True(writer.IsOpened());
        for (var frame = 0; frame < 12; frame++)
        {
            using var image = new Mat(200, 200, MatType.CV_8UC3, new Scalar(160, 85, 10));
            Cv2.Circle(image, new Point(60 + animal + frame * 3, 90), 8, new Scalar(5, 5, 5), -1);
            writer.Write(image);
        }
    }

    private static List<VideoAsset> ImportClips(WorkspaceService workspace, StudyStore store, IEnumerable<string> paths)
    {
        var assets = new List<VideoAsset>();
        foreach (var path in paths)
        {
            var asset = workspace.Import(path) with { Settings = Arena() };
            store.SaveVideo(asset);
            assets.Add(asset);
        }

        return assets;
    }

    private static NativeBatchRequest Request(
        IRecordingAnalysisDecoder? decoder = null,
        IRecordingSourceHasher? hasher = null,
        IRecordingRunPublisher? publisher = null,
        int parallelism = 2,
        int? budget = null,
        Func<VideoAsset, CancellationToken, ValueTask<IAsyncDisposable?>>? openResource = null,
        IProgress<RecordingBatchProgress>? recordingProgress = null) => new()
    {
        MaxDegreeOfParallelism = parallelism,
        ResourceBudget = budget,
        ProgressMinimumInterval = TimeSpan.Zero,
        Decoder = decoder,
        Hasher = hasher,
        Publisher = publisher,
        OpenResource = openResource,
        RecordingProgress = recordingProgress
    };

    private static long Scalar(StudyStore store, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open(store);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(StudyStore store, string sql)
    {
        using var connection = Open(store);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(StudyStore store)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, DefaultTimeout = 5 }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static string Signature(AnalysisRun run)
    {
        return JsonDefaults.Serialize(new { run.SourceSha256, run.Metadata, run.Settings, run.AlgorithmContract, run.Points, run.Metrics, run.MaximumTimestampErrorSeconds, run.NominalTimingVerified });
    }

    private sealed class PeakBox
    {
        public long Value;
    }

    private sealed class DelegateDecoder(Func<VideoAsset, string, IProgress<double>?, CancellationToken, AnalysisRun> analyze) : IRecordingAnalysisDecoder
    {
        public AnalysisRun Analyze(VideoAsset frozenAsset, string resolvedPath, IProgress<double>? progress, CancellationToken cancellation)
            => analyze(frozenAsset, resolvedPath, progress, cancellation);
    }

    private sealed class DelegateHasher(Func<string, string> hash) : IRecordingSourceHasher
    {
        public string HashFile(string path) => hash(path);
    }

    private sealed class DelegatePublisher(Func<AnalysisRun, CancellationToken, ValueTask> publish) : IRecordingRunPublisher
    {
        public ValueTask PublishAsync(AnalysisRun run, CancellationToken cancellation) => publish(run, cancellation);
    }

    private sealed class ThrowDispose(string message) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException(message));
    }
}
