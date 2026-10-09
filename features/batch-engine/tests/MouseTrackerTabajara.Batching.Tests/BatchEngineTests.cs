using System.Collections.Concurrent;
using Xunit;

namespace MouseTrackerTabajara.Batching.Tests;

public sealed class BatchEngineTests
{
    [Fact]
    public async Task ThirtyItemRunOverlapsAtTheExplicitLimit()
    {
        const int count = 30;
        const int limit = 4;
        var jobs = NumberedJobs(count);
        var active = 0;
        var maximum = 0;
        var violation = 0;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new List<BatchProgressUpdate>();
        var updateGate = new object();
        var request = Request(
            jobs,
            async (ctx, ct) =>
            {
                var now = Interlocked.Increment(ref active);
                lock (updateGate)
                {
                    if (now > maximum)
                        maximum = now;
                }

                if (now > limit)
                    Volatile.Write(ref violation, now);
                if (now == limit)
                {
                    var until = Environment.TickCount64 + 100;
                    while (Environment.TickCount64 < until)
                    {
                        var observed = Volatile.Read(ref active);
                        if (observed > limit)
                            Volatile.Write(ref violation, observed);
                        Thread.Yield();
                    }

                    reached.TrySetResult();
                }

                try
                {
                    await release.Task.WaitAsync(ct);
                    return BatchWorkResult<int>.Complete(ctx.InputIndex);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            progress: new InlineProgress<BatchProgressUpdate>(update =>
            {
                lock (updateGate)
                    updates.Add(update);
            }));
        var engine = new BatchEngine(new BatchEngineOptions { MaxDegreeOfParallelism = limit });
        var run = engine.RunAsync(request);
        try
        {
            await Wait(reached.Task, "Thirty jobs did not overlap at the parallelism limit.");
            Assert.Equal(limit, Volatile.Read(ref maximum));
            Assert.Equal(0, Volatile.Read(ref violation));
            Assert.Equal(limit, Volatile.Read(ref active));
            release.TrySetResult();
            var result = await Wait(run, "Thirty-item run did not finish.");
            Assert.Equal(limit, result.ObservedMaximumActiveJobs);
            Assert.Equal(limit, result.MaxDegreeOfParallelism);
            Assert.Equal(count, result.CompletedCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Equal(0, result.ReusedCount);
            Assert.Equal(0, result.CancelledCount);
            Assert.False(result.CancellationRequested);
            Assert.False(result.StoppedAfterFailure);
            Assert.Equal(BatchRunResult<int>.CountInterpretation, result.CountInterpretationText);
            AssertAssociated(result, jobs);
            for (var index = 0; index < count; index++)
            {
                Assert.Equal(BatchItemState.Completed, result.Items[index].State);
                Assert.True(result.Items[index].Published);
                Assert.Equal(index, result.Items[index].Result);
            }

            List<BatchProgressUpdate> snapshot;
            lock (updateGate)
                snapshot = updates.ToList();
            Assert.Contains(snapshot, update => update.RunningJobs == limit && update.ActiveJobs == limit);
            foreach (var update in snapshot)
            {
                Assert.InRange(update.RunningJobs, 0, limit);
                Assert.InRange(update.ActiveJobs, 0, limit);
                Assert.Equal(limit, update.ParallelismLimit);
                Assert.Equal(count, update.QueuedJobs + update.RunningJobs + update.CompletedJobs + update.ReusedJobs + update.FailedJobs + update.CancelledJobs);
            }
        }
        finally
        {
            release.TrySetResult();
            await Drain(run);
        }
    }

    [Fact]
    public async Task LimitOneNeverOverlapsAndDefaultLimitIsTwo()
    {
        var engine = new BatchEngine();
        Assert.Equal(BatchEngineOptions.DefaultMaxDegreeOfParallelism, engine.MaxDegreeOfParallelism);
        Assert.Equal(2, engine.MaxDegreeOfParallelism);
        Assert.Equal(BatchFailurePolicy.Continue, engine.FailurePolicy);

        var serial = await RunObservedOverlap(new BatchEngine(new BatchEngineOptions { MaxDegreeOfParallelism = 1 }), 30, 1);
        Assert.Equal(1, serial.Observed);
        Assert.Equal(1, serial.Result.ObservedMaximumActiveJobs);
        Assert.Equal(30, serial.Result.CompletedCount);
        AssertAssociated(serial.Result, NumberedJobs(30));

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var jobs = NumberedJobs(6);
        var run = engine.RunAsync(Request(jobs, async (ctx, ct) =>
        {
            var now = Interlocked.Increment(ref active);
            lock (release)
            {
                if (now > maximum)
                    maximum = now;
            }

            if (now == 2)
                reached.TrySetResult();
            try
            {
                await release.Task.WaitAsync(ct);
                return BatchWorkResult<int>.Complete(ctx.InputIndex);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }));
        try
        {
            await Wait(reached.Task, "Default parallelism did not overlap two jobs.");
            await Task.Delay(50);
            Assert.Equal(2, Volatile.Read(ref maximum));
            Assert.Equal(2, Volatile.Read(ref active));
            release.TrySetResult();
            var result = await Wait(run, "Default parallelism run did not finish.");
            Assert.Equal(2, result.ObservedMaximumActiveJobs);
            Assert.Equal(6, result.CompletedCount);
        }
        finally
        {
            release.TrySetResult();
            await Drain(run);
        }
    }

    [Fact]
    public void ZeroAndNegativeLimitsAndUndefinedPolicyAreRejected()
    {
        var zero = Assert.Throws<ArgumentOutOfRangeException>(() => new BatchEngine(new BatchEngineOptions { MaxDegreeOfParallelism = 0 }));
        var negative = Assert.Throws<ArgumentOutOfRangeException>(() => new BatchEngine(new BatchEngineOptions { MaxDegreeOfParallelism = -3 }));
        var policy = Assert.Throws<ArgumentOutOfRangeException>(() => new BatchEngine(new BatchEngineOptions { FailurePolicy = (BatchFailurePolicy)42 }));
        Assert.Equal(0, zero.ActualValue);
        Assert.Equal(-3, negative.ActualValue);
        Assert.Equal((BatchFailurePolicy)42, policy.ActualValue);
    }

    [Fact]
    public async Task EmptyInputCompletesWithoutCallingTheProcessor()
    {
        var calls = 0;
        var result = await new BatchEngine().RunAsync(Request(Array.Empty<BatchJob<int>>(), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(BatchWorkResult<int>.Complete(0));
        }));
        Assert.Equal(0, calls);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.CompletedCount);
        Assert.Equal(0, result.ObservedMaximumActiveJobs);
        Assert.False(result.CancellationRequested);
        Assert.Equal(BatchRunResult<int>.CountInterpretation, result.CountInterpretationText);
    }

    [Fact]
    public async Task DuplicateAndBlankIdentitiesDoNotStart()
    {
        var calls = 0;
        IReadOnlyList<BatchJob<int>> jobs = [new("a", 1), new("b", 2), new("a", 3), new("c", 4), new("b", 5)];
        var duplicate = await Assert.ThrowsAsync<ArgumentException>(() => new BatchEngine().RunAsync(Request(jobs, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(BatchWorkResult<int>.Complete(0));
        })));
        Assert.Contains("Duplicate job identities: a, b", duplicate.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("c,", duplicate.Message, StringComparison.Ordinal);

        var blank = await Assert.ThrowsAsync<ArgumentException>(() => new BatchEngine().RunAsync(Request([new BatchJob<int>("  ", 1)], (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(BatchWorkResult<int>.Complete(0));
        })));
        Assert.Contains("index 0", blank.Message, StringComparison.Ordinal);

        IReadOnlyList<BatchJob<int>> missingJobs = [new("ok", 1), null!];
        var missing = await Assert.ThrowsAsync<ArgumentException>(() => new BatchEngine().RunAsync(Request(missingJobs, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(BatchWorkResult<int>.Complete(0));
        })));
        Assert.Contains("index 1", missing.Message, StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CaseSensitiveIdentitiesAreDistinct()
    {
        var jobs = new[] { new BatchJob<int>("Mouse", 1), new BatchJob<int>("mouse", 2) };
        var result = await new BatchEngine().RunAsync(Request(jobs, (ctx, _) => ValueTask.FromResult(BatchWorkResult<int>.Complete(ctx.Input))));
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(["Mouse", "mouse"], result.Items.Select(item => item.JobId).ToArray());
        Assert.Equal(1, result.Items[0].Result);
        Assert.Equal(2, result.Items[1].Result);
    }

    [Fact]
    public async Task CancellationRetainsPublishedWorkAndResumeReusesOnlyMatchingIdentity()
    {
        const int count = 30;
        var jobs = NumberedJobs(count);
        var started = Gates(count);
        var published = Gates(count);
        var opens = new int[count];
        var closes = new int[count];
        var cache = new MapCache();
        using var cancellation = new CancellationTokenSource();
        var first = new BatchEngine(new BatchEngineOptions { MaxDegreeOfParallelism = 2 }).RunAsync(
            Request(
                jobs,
                async (ctx, ct) =>
                {
                    started[ctx.InputIndex].TrySetResult();
                    if (ctx.InputIndex >= 2)
                        await Task.Delay(Timeout.Infinite, ct);
                    return BatchWorkResult<int>.Complete(ctx.InputIndex);
                },
                (publication, _) =>
                {
                    cache.Remember(publication);
                    published[publication.InputIndex].TrySetResult();
                    return ValueTask.CompletedTask;
                },
                cache,
                open: (ctx, _) =>
                {
                    Interlocked.Increment(ref opens[ctx.InputIndex]);
                    return ValueTask.FromResult<IAsyncDisposable?>(new CallbackLease(() => Interlocked.Increment(ref closes[ctx.InputIndex])));
                }),
            cancellation.Token);

        await Wait(started[0].Task, "Job 0 did not start.");
        await Wait(started[1].Task, "Job 1 did not start.");
        await Wait(published[0].Task, "Job 0 was not published.");
        await Wait(published[1].Task, "Job 1 was not published.");
        cancellation.Cancel();
        var interrupted = await Wait(first, "Cancelled batch did not drain.");

        Assert.True(interrupted.CancellationRequested);
        Assert.Equal(2, interrupted.CompletedCount);
        Assert.Equal(0, interrupted.ReusedCount);
        Assert.Equal(0, interrupted.FailedCount);
        Assert.Equal(count - 2, interrupted.CancelledCount);
        Assert.Equal(BatchItemState.Completed, interrupted.Items[0].State);
        Assert.Equal(BatchItemState.Completed, interrupted.Items[1].State);
        Assert.True(interrupted.Items[0].Published);
        Assert.True(interrupted.Items[1].Published);
        Assert.Equal(0, interrupted.Items[0].Result);
        Assert.Equal(1, interrupted.Items[1].Result);
        for (var index = 2; index < count; index++)
        {
            Assert.Equal(BatchItemState.Cancelled, interrupted.Items[index].State);
            Assert.False(interrupted.Items[index].Published);
            Assert.False(string.IsNullOrWhiteSpace(interrupted.Items[index].Error));
            Assert.Equal(0, interrupted.Items[index].Result);
        }

        for (var index = 0; index < count; index++)
            Assert.Equal(opens[index], closes[index]);
        Assert.Equal(1, opens[0]);
        Assert.Equal(1, opens[1]);
        for (var index = 0; index < count; index++)
        {
            if (interrupted.Items[index].Error == "Cancelled before start.")
                Assert.Equal(0, opens[index]);
        }

        var resumedCalls = new ConcurrentDictionary<string, int>();
        var resumed = await new BatchEngine(new BatchEngineOptions { MaxDegreeOfParallelism = 4 }).RunAsync(Request(
            jobs,
            (ctx, _) =>
            {
                resumedCalls.AddOrUpdate(ctx.JobId, 1, (_, current) => current + 1);
                return ValueTask.FromResult(BatchWorkResult<int>.Complete(ctx.InputIndex + 1000));
            },
            cache.Remember,
            cache));
        Assert.Equal(2, resumed.ReusedCount);
        Assert.Equal(count - 2, resumed.CompletedCount);
        Assert.Equal(0, resumed.FailedCount);
        Assert.False(resumed.Items[0].Published);
        Assert.False(resumed.Items[1].Published);
        Assert.Equal(0, resumed.Items[0].Result);
        Assert.Equal(1, resumed.Items[1].Result);
        Assert.DoesNotContain(jobs[0].Id, resumedCalls.Keys);
        Assert.DoesNotContain(jobs[1].Id, resumedCalls.Keys);
        Assert.Equal(count - 2, resumedCalls.Count);

        var changedCalls = new ConcurrentDictionary<string, byte>();
        var changed = await new BatchEngine().RunAsync(Request(
            jobs,
            (ctx, _) =>
            {
                changedCalls.TryAdd(ctx.JobId, 1);
                return ValueTask.FromResult(BatchWorkResult<int>.Complete(ctx.InputIndex + 2000));
            },
            cache.Remember,
            cache,
            identity: job => job.Input switch
            {
                0 => IdentityFor(job, settings: "settings-v2"),
                2 => IdentityFor(job, source: "source-replaced-" + job.Id),
                3 => IdentityFor(job, algorithm: "Algorithm-v1"),
                _ => IdentityFor(job)
            }));
        Assert.Equal(new[] { jobs[0].Id, jobs[2].Id, jobs[3].Id }, changedCalls.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(BatchItemState.Completed, changed.Items[0].State);
        Assert.Equal(BatchItemState.Reused, changed.Items[1].State);
        Assert.Equal(BatchItemState.Completed, changed.Items[2].State);
        Assert.Equal(BatchItemState.Completed, changed.Items[3].State);
        Assert.Equal(2000, changed.Items[0].Result);
        Assert.Equal(1, changed.Items[1].Result);
        Assert.Equal(2002, changed.Items[2].Result);
        Assert.Equal(2003, changed.Items[3].Result);
        foreach (var index in new[] { 0, 2, 3 })
        {
            Assert.Contains(changed.Items[index].Notices, notice => notice.Contains("did not match", StringComparison.Ordinal));
            Assert.True(changed.Items[index].Published);
        }

        Assert.Equal(count - 3, changed.ReusedCount);
        Assert.Equal(BatchRunResult<int>.CountInterpretation, changed.CountInterpretationText);
        AssertAssociated(changed, jobs);
    }

    [Fact]
    public async Task AlreadyCancelledRequestDoesNotStartJobs()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new BatchEngine().RunAsync(Request(NumberedJobs(5), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(BatchWorkResult<int>.Complete(1));
        }), cancellation.Token);
        Assert.Equal(0, calls);
        Assert.True(result.CancellationRequested);
        Assert.Equal(5, result.CancelledCount);
        Assert.All(result.Items, item =>
        {
            Assert.Equal(BatchItemState.Cancelled, item.State);
            Assert.Equal("Cancelled before start.", item.Error);
            Assert.False(item.Published);
        });
    }

    [Fact]
    public async Task ItemFailuresIncompleteResultsAndBadIdentityStayOnTheRightJob()
    {
        var jobs = NumberedJobs(7);
        var published = new ConcurrentDictionary<string, int>();
        var result = await new BatchEngine(new BatchEngineOptions { MaxDegreeOfParallelism = 4 }).RunAsync(Request(
            jobs,
            (ctx, _) => ctx.InputIndex switch
            {
                3 => throw new InvalidOperationException("decoder failed on " + ctx.JobId),
                4 => new ValueTask<BatchWorkResult<int>>((BatchWorkResult<int>?)null!),
                5 => ValueTask.FromResult(BatchWorkResult<int>.Incomplete("partial frames")),
                _ => ValueTask.FromResult(BatchWorkResult<int>.Complete(ctx.InputIndex))
            },
            (publication, _) =>
            {
                published[publication.JobId] = publication.Result;
                return ValueTask.CompletedTask;
            },
            identity: job =>
            {
                if (job.Input == 1)
                    throw new InvalidOperationException("missing settings for " + job.Id);
                return IdentityFor(job);
            }));

        Assert.False(result.StoppedAfterFailure);
        Assert.Equal(BatchItemState.Completed, result.Items[0].State);
        Assert.Equal(0, result.Items[0].Result);
        Assert.Equal(BatchItemState.Failed, result.Items[1].State);
        Assert.Contains("missing settings for job-01", result.Items[1].Error, StringComparison.Ordinal);
        Assert.Equal(BatchItemState.Completed, result.Items[2].State);
        Assert.Equal(2, result.Items[2].Result);
        Assert.Equal(BatchItemState.Failed, result.Items[3].State);
        Assert.Contains("decoder failed on job-03", result.Items[3].Error, StringComparison.Ordinal);
        Assert.Equal(BatchItemState.Failed, result.Items[4].State);
        Assert.Contains("no completion", result.Items[4].Error, StringComparison.Ordinal);
        Assert.Equal(BatchItemState.Failed, result.Items[5].State);
        Assert.Contains("Incomplete result was not published: partial frames", result.Items[5].Error, StringComparison.Ordinal);
        Assert.Equal(BatchItemState.Completed, result.Items[6].State);
        Assert.Equal(6, result.Items[6].Result);
        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(4, result.FailedCount);
        Assert.Equal(new[] { "job-00", "job-02", "job-06" }, published.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("job-03", published.Keys);
        Assert.DoesNotContain("job-05", published.Keys);
        AssertAssociated(result, jobs);
        Assert.All(result.Items.Where(item => item.State == BatchItemState.Failed), item =>
        {
            Assert.False(item.Published);
            Assert.False(string.IsNullOrWhiteSpace(item.Error));
            Assert.Equal(0, item.Result);
        });
    }

    [Fact]
    public async Task MalformedProgressIsKeptVisibleWithoutCorruptingAnotherItem()
    {
        var jobs = NumberedJobs(3);
        var updates = new List<BatchProgressUpdate>();
        var gate = new object();
        var result = await new BatchEngine().RunAsync(Request(
            jobs,
            (ctx, _) =>
            {
                if (ctx.InputIndex == 1)
                {
                    ctx.Progress.Report(new BatchItemProgressReport(100d + 1e-12, "rounding"));
                    double[] bad = [double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1d, 100.1d, 150d, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN];
                    foreach (var percent in bad)
                        ctx.Progress.Report(new BatchItemProgressReport(percent, "bad"));
                    IProgress<BatchItemProgressReport> progress = ctx.Progress;
                    progress.Report(null!);
                }
                else if (ctx.InputIndex == 2)
                {
                    ctx.Progress.Report(new BatchItemProgressReport(10, "a"));
                    ctx.Progress.Report(new BatchItemProgressReport(20, "b"));
                    ctx.Progress.Report(new BatchItemProgressReport(42.5, "c"));
                }

                return ValueTask.FromResult(BatchWorkResult<int>.Complete(ctx.InputIndex));
            },
            progress: new InlineProgress<BatchProgressUpdate>(update =>
            {
                lock (gate)
                    updates.Add(update);
                if (update.JobId == "job-00" && update.State == BatchItemState.Running && !update.ProgressMalformed)
                    throw new InvalidOperationException("ui detached");
            })));

        Assert.All(result.Items, item => Assert.Equal(BatchItemState.Completed, item.State));
        Assert.Equal(100d, result.Items[1].LastValidPercent);
        Assert.Equal(BatchEngine.MaxDetailedNoticesPerItem, result.Items[1].Notices.Count);
        Assert.Equal(5, result.Items[1].SuppressedNoticeCount);
        Assert.Contains(result.Items[1].Notices, notice => notice.Contains("NaN", StringComparison.Ordinal));
        Assert.Contains(result.Items[1].Notices, notice => notice.Contains("Infinity", StringComparison.Ordinal));
        Assert.Contains(result.Items[1].Notices, notice => notice.Contains("-1", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Items[1].Notices, notice => notice.Contains("job-02", StringComparison.Ordinal));
        Assert.Equal(42.5, result.Items[2].LastValidPercent);
        Assert.Empty(result.Items[2].Notices);
        Assert.Equal(0, result.Items[2].SuppressedNoticeCount);
        Assert.Contains(result.Items[0].Notices, notice => notice.Contains("ui detached", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Items[2].Notices, notice => notice.Contains("ui detached", StringComparison.Ordinal));
        Assert.Equal(0, result.Items[0].Result);
        Assert.Equal(1, result.Items[1].Result);
        Assert.Equal(2, result.Items[2].Result);

        List<BatchProgressUpdate> snapshot;
        lock (gate)
            snapshot = updates.ToList();
        Assert.Contains(snapshot, update => update.InputIndex == 1 && update.ProgressMalformed && update.ItemPercent == 100d);
        Assert.Contains(snapshot, update => update.InputIndex == 2 && !update.ProgressMalformed && update.ItemPercent == 42.5);
    }

    [Fact]
    public async Task PublicationFailureIsIsolatedAndPublicationIsSerialized()
    {
        var jobs = NumberedJobs(10);
        var saved = new ConcurrentDictionary<string, int>();
        var current = 0;
        var maximum = 0;
        var result = await new BatchEngine(new BatchEngineOptions { MaxDegreeOfParallelism = 8 }).RunAsync(Request(
            jobs,
            (ctx, _) => ValueTask.FromResult(BatchWorkResult<int>.Complete(ctx.InputIndex + 50)),
            async (publication, _) =>
            {
                var now = Interlocked.Increment(ref current);
                lock (saved)
                {
                    if (now > maximum)
                        maximum = now;
                }

                await Task.Delay(15);
                Interlocked.Decrement(ref current);
                if (publication.InputIndex == 3)
                    throw new IOException("database is locked");
                saved[publication.JobId] = publication.Result;
            }));

        Assert.Equal(1, maximum);
        Assert.Equal(BatchItemState.Failed, result.Items[3].State);
        Assert.Contains("database is locked", result.Items[3].Error, StringComparison.Ordinal);
        Assert.Contains("Publication failed:", result.Items[3].Error, StringComparison.Ordinal);
        Assert.False(result.Items[3].Published);
        Assert.False(saved.ContainsKey("job-03"));
        Assert.Equal(9, result.CompletedCount);
        Assert.Equal(1, result.FailedCount);
        for (var index = 0; index < jobs.Length; index++)
        {
            if (index == 3)
                continue;
            Assert.Equal(BatchItemState.Completed, result.Items[index].State);
            Assert.Equal(index + 50, result.Items[index].Result);
            Assert.Equal(index + 50, saved[jobs[index].Id]);
            Assert.True(result.Items[index].Published);
        }

        AssertAssociated(result, jobs);
    }

    [Fact]
    public async Task OutOfOrderCompletionStaysBoundToInputIdentity()
    {
        const int count = 30;
        const int limit = 2;
        var jobs = NumberedJobs(count);
        var started = Gates(count);
        var published = Gates(count);
        var publishedOrder = new List<int>();
        var run = new BatchEngine(new BatchEngineOptions { MaxDegreeOfParallelism = limit }).RunAsync(Request(
            jobs,
            async (ctx, ct) =>
            {
                started[ctx.InputIndex].TrySetResult();
                await published[ctx.InputIndex].Task.WaitAsync(ct);
                return BatchWorkResult<int>.Complete(ctx.InputIndex + 7);
            },
            (publication, _) =>
            {
                lock (publishedOrder)
                    publishedOrder.Add(publication.InputIndex);
                return ValueTask.CompletedTask;
            }));

        var expected = new List<int>();
        try
        {
            for (var pair = 0; pair < count; pair += limit)
            {
                await Wait(started[pair].Task, "Low job in a pair did not start.");
                await Wait(started[pair + 1].Task, "High job in a pair did not start.");
                published[pair + 1].TrySetResult();
                await Wait(PublishObserved(run, publishedOrder, pair + 1), "High job was not published.");
                expected.Add(pair + 1);
                published[pair].TrySetResult();
                await Wait(PublishObserved(run, publishedOrder, pair), "Low job was not published.");
                expected.Add(pair);
            }

            var result = await Wait(run, "Out-of-order run did not finish.");
            Assert.Equal(expected, publishedOrder);
            Assert.NotEqual(Enumerable.Range(0, count).ToArray(), publishedOrder);
            AssertAssociated(result, jobs);
            for (var index = 0; index < count; index++)
            {
                Assert.Equal(BatchItemState.Completed, result.Items[index].State);
                Assert.Equal(jobs[index].Id, result.Items[index].JobId);
                Assert.Equal(index + 7, result.Items[index].Result);
                Assert.Equal(expected.IndexOf(index) + 1, result.Items[index].CompletionOrdinal);
            }
        }
        finally
        {
            foreach (var gate in published)
                gate.TrySetResult();
            await Drain(run);
        }
    }

    [Fact]
    public async Task ResourceBudgetCapsOverlapAndDisposesEveryLease()
    {
        const int count = 30;
        const int parallelism = 8;
        const int budget = 2;
        var jobs = NumberedJobs(count);
        var leases = 0;
        var disposed = 0;
        var active = 0;
        var maximum = 0;
        var violation = 0;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new BatchEngineOptions
        {
            MaxDegreeOfParallelism = parallelism,
            AcquireResource = (snapshot, _) =>
            {
                if (snapshot.ActiveJobs >= budget)
                    return ValueTask.FromResult<IAsyncDisposable?>(null);
                Interlocked.Increment(ref leases);
                return ValueTask.FromResult<IAsyncDisposable?>(new CallbackLease(() => Interlocked.Increment(ref disposed)));
            }
        };
        var run = new BatchEngine(options).RunAsync(Request(jobs, async (ctx, ct) =>
        {
            var now = Interlocked.Increment(ref active);
            lock (options)
            {
                if (now > maximum)
                    maximum = now;
            }

            if (now > budget)
                Volatile.Write(ref violation, now);
            if (now == budget)
            {
                var until = Environment.TickCount64 + 80;
                while (Environment.TickCount64 < until)
                {
                    if (Volatile.Read(ref active) > budget)
                        Volatile.Write(ref violation, Volatile.Read(ref active));
                    Thread.Yield();
                }

                reached.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(ct);
                return BatchWorkResult<int>.Complete(ctx.InputIndex);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }));
        try
        {
            await Wait(reached.Task, "Resource budget did not admit overlapping jobs.");
            Assert.Equal(budget, Volatile.Read(ref maximum));
            Assert.Equal(0, Volatile.Read(ref violation));
            release.TrySetResult();
            var result = await Wait(run, "Budgeted run did not finish.");
            Assert.Equal(budget, result.ObservedMaximumActiveJobs);
            Assert.InRange(result.ObservedMaximumActiveJobs, 2, budget);
            Assert.Equal(count, result.CompletedCount);
            Assert.Equal(count, leases);
            Assert.Equal(count, disposed);
            AssertAssociated(result, jobs);
        }
        finally
        {
            release.TrySetResult();
            await Drain(run);
        }
    }

    [Fact]
    public async Task IdleResourceDenialFailsEveryItemWithoutHanging()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var calls = 0;
        var result = await new BatchEngine(new BatchEngineOptions
        {
            MaxDegreeOfParallelism = 4,
            AcquireResource = (_, _) => ValueTask.FromResult<IAsyncDisposable?>(null)
        }).RunAsync(Request(NumberedJobs(5), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(BatchWorkResult<int>.Complete(1));
        }), timeout.Token);

        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal(0, calls);
        Assert.Equal(5, result.FailedCount);
        Assert.All(result.Items, item =>
        {
            Assert.Equal(BatchItemState.Failed, item.State);
            Assert.Equal("Resource budget refused admission while no job was active.", item.Error);
            Assert.False(item.Published);
        });
    }

    [Fact]
    public async Task ResourceHookExceptionFailsThatItemAndContinues()
    {
        var calls = 0;
        var jobs = NumberedJobs(4);
        var result = await new BatchEngine(new BatchEngineOptions
        {
            MaxDegreeOfParallelism = 1,
            AcquireResource = (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("budget unavailable");
                return ValueTask.FromResult<IAsyncDisposable?>(new CallbackLease(() => { }));
            }
        }).RunAsync(Request(jobs, (ctx, _) => ValueTask.FromResult(BatchWorkResult<int>.Complete(ctx.InputIndex))));

        Assert.Equal(BatchItemState.Failed, result.Items[0].State);
        Assert.Contains("budget unavailable", result.Items[0].Error, StringComparison.Ordinal);
        Assert.Equal(0, result.Items[0].Result);
        Assert.False(result.Items[0].Published);
        for (var index = 1; index < jobs.Length; index++)
        {
            Assert.Equal(BatchItemState.Completed, result.Items[index].State);
            Assert.Equal(index, result.Items[index].Result);
        }
    }

    [Fact]
    public async Task StopPolicyFinishesWorkAlreadyRunningAndStartsNothingNew()
    {
        const int count = 10;
        var jobs = NumberedJobs(count);
        var started = Gates(count);
        var release = Gates(count);
        var failedSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new BatchEngine(new BatchEngineOptions
        {
            MaxDegreeOfParallelism = 4,
            FailurePolicy = BatchFailurePolicy.StopScheduling
        }).RunAsync(Request(
            jobs,
            async (ctx, ct) =>
            {
                started[ctx.InputIndex].TrySetResult();
                await release[ctx.InputIndex].Task.WaitAsync(ct);
                if (ctx.InputIndex == 0)
                    throw new InvalidOperationException("clip 0 decoder failed");
                return BatchWorkResult<int>.Complete(ctx.InputIndex);
            },
            progress: new InlineProgress<BatchProgressUpdate>(update =>
            {
                if (update.JobId == "job-00" && update.State == BatchItemState.Failed)
                    failedSeen.TrySetResult();
            })));
        try
        {
            for (var index = 0; index < 4; index++)
                await Wait(started[index].Task, "Initial worker did not start.");
            release[0].TrySetResult();
            await Wait(failedSeen.Task, "The failed item was not reported.");
            Assert.False(started[4].Task.IsCompleted);
            release[1].TrySetResult();
            release[2].TrySetResult();
            release[3].TrySetResult();
            var result = await Wait(run, "Stop-policy run did not drain.");
            Assert.True(result.StoppedAfterFailure);
            Assert.False(result.CancellationRequested);
            Assert.Equal(BatchItemState.Failed, result.Items[0].State);
            Assert.Contains("clip 0 decoder failed", result.Items[0].Error, StringComparison.Ordinal);
            Assert.False(result.Items[0].Published);
            for (var index = 1; index < 4; index++)
            {
                Assert.Equal(BatchItemState.Completed, result.Items[index].State);
                Assert.Equal(index, result.Items[index].Result);
                Assert.True(result.Items[index].Published);
            }

            for (var index = 4; index < count; index++)
            {
                Assert.Equal(BatchItemState.Cancelled, result.Items[index].State);
                Assert.Equal("Not started because a previous item failed and scheduling stopped.", result.Items[index].Error);
                Assert.False(started[index].Task.IsCompleted);
            }

            Assert.Equal(3, result.CompletedCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Equal(6, result.CancelledCount);
        }
        finally
        {
            foreach (var gate in release)
                gate.TrySetResult();
            await Drain(run);
        }
    }

    [Fact]
    public async Task DisposalErrorsStayVisibleOnThePublishedItem()
    {
        var result = await new BatchEngine(new BatchEngineOptions
        {
            AcquireResource = (_, _) => ValueTask.FromResult<IAsyncDisposable?>(new ThrowingLease("budget leaked"))
        }).RunAsync(Request(
            NumberedJobs(1),
            (ctx, _) => ValueTask.FromResult(BatchWorkResult<int>.Complete(ctx.InputIndex)),
            open: (_, _) => ValueTask.FromResult<IAsyncDisposable?>(new ThrowingLease("decoder leaked"))));

        var item = Assert.Single(result.Items);
        Assert.Equal(BatchItemState.Completed, item.State);
        Assert.True(item.Published);
        Assert.Equal(0, item.Result);
        Assert.Equal("decoder leaked | budget leaked", item.DisposalError);
    }

    [Fact]
    public async Task CacheLookupFailureDoesNotDropTheItem()
    {
        var jobs = NumberedJobs(4);
        var cache = new MapCache { FailFor = id => id == "job-02" ? new InvalidOperationException("cache offline") : null };
        var calls = new ConcurrentBag<string>();
        var result = await new BatchEngine(new BatchEngineOptions { MaxDegreeOfParallelism = 3 }).RunAsync(Request(
            jobs,
            (ctx, _) =>
            {
                calls.Add(ctx.JobId);
                return ValueTask.FromResult(BatchWorkResult<int>.Complete(ctx.InputIndex));
            },
            cache: cache));

        Assert.Equal(BatchItemState.Failed, result.Items[2].State);
        Assert.Contains("Cache lookup failed:", result.Items[2].Error, StringComparison.Ordinal);
        Assert.Contains("cache offline", result.Items[2].Error, StringComparison.Ordinal);
        Assert.False(result.Items[2].Published);
        Assert.DoesNotContain("job-02", calls);
        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(["job-00", "job-01", "job-03"], result.Items.Where(item => item.State == BatchItemState.Completed).Select(item => item.JobId).ToArray());
        for (var index = 0; index < jobs.Length; index++)
        {
            if (index == 2)
                continue;
            Assert.Equal(index, result.Items[index].Result);
        }
    }

    private static async Task<OverlapObservation> RunObservedOverlap(BatchEngine engine, int count, int expectedCeiling)
    {
        var active = 0;
        var maximum = 0;
        var jobs = NumberedJobs(count);
        var result = await engine.RunAsync(Request(jobs, async (ctx, _) =>
        {
            var now = Interlocked.Increment(ref active);
            lock (jobs)
            {
                if (now > maximum)
                    maximum = now;
            }

            await Task.Yield();
            Interlocked.Decrement(ref active);
            return BatchWorkResult<int>.Complete(ctx.InputIndex);
        }));
        Assert.InRange(maximum, 1, expectedCeiling);
        return new OverlapObservation(maximum, result);
    }

    private static Task PublishObserved(Task run, List<int> publishedOrder, int index)
    {
        return Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (run.IsCompleted)
                    await run;
                lock (publishedOrder)
                {
                    if (publishedOrder.Contains(index))
                        return;
                }

                await Task.Delay(5);
            }

            throw new TimeoutException("Publication of job index " + index + " was not observed.");
        });
    }

    private static void AssertAssociated(BatchRunResult<int> result, IReadOnlyList<BatchJob<int>> jobs)
    {
        Assert.Equal(jobs.Count, result.Items.Count);
        for (var index = 0; index < jobs.Count; index++)
        {
            Assert.Equal(index, result.Items[index].InputIndex);
            Assert.Equal(jobs[index].Id, result.Items[index].JobId);
        }
    }

    private static async Task<T> Wait<T>(Task<T> task, string description)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (!ReferenceEquals(completed, task))
            throw new TimeoutException(description);
        return await task;
    }

    private static async Task Wait(Task task, string description)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (!ReferenceEquals(completed, task))
            throw new TimeoutException(description);
        await task;
    }

    private static async Task Drain(Task run)
    {
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        if (ReferenceEquals(completed, run))
        {
            try
            {
                await run;
            }
            catch (Exception ex) when (ex is not TimeoutException)
            {
            }
        }
    }

    private static BatchJob<int>[] NumberedJobs(int count) =>
        Enumerable.Range(0, count).Select(index => new BatchJob<int>($"job-{index:00}", index)).ToArray();

    private static CacheIdentity IdentityFor(BatchJob<int> job, string settings = "settings-v1", string algorithm = "algorithm-v1", string? source = null) =>
        new(source ?? "source-" + job.Id, settings, algorithm);

    private static TaskCompletionSource[] Gates(int count) =>
        Enumerable.Range(0, count).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

    private static BatchRequest<int, int> Request(
        IReadOnlyList<BatchJob<int>> jobs,
        Func<BatchExecutionContext<int>, CancellationToken, ValueTask<BatchWorkResult<int>>> process,
        Func<BatchPublication<int>, CancellationToken, ValueTask>? publish = null,
        IBatchCompletionCache<int>? cache = null,
        IProgress<BatchProgressUpdate>? progress = null,
        Func<BatchExecutionContext<int>, CancellationToken, ValueTask<IAsyncDisposable?>>? open = null,
        Func<BatchJob<int>, CacheIdentity>? identity = null) => new()
    {
        Jobs = jobs,
        ResolveIdentity = identity ?? (job => IdentityFor(job)),
        Process = process,
        Publish = publish ?? ((_, _) => ValueTask.CompletedTask),
        Cache = cache,
        Progress = progress,
        OpenResource = open
    };

    private readonly record struct OverlapObservation(int Observed, BatchRunResult<int> Result);

    private sealed class MapCache : IBatchCompletionCache<int>
    {
        private readonly Dictionary<string, BatchCachedCompletion<int>> _items = new(StringComparer.Ordinal);
        public Func<string, Exception?>? FailFor { get; init; }

        public BatchCachedCompletion<int>? Find(string jobId, CacheIdentity requiredIdentity)
        {
            if (FailFor?.Invoke(jobId) is { } error)
                throw error;
            return _items.TryGetValue(jobId, out var hit) ? hit : null;
        }

        public ValueTask Remember(BatchPublication<int> publication, CancellationToken cancellationToken = default)
        {
            _items[publication.JobId] = new BatchCachedCompletion<int>(publication.Identity, publication.Result);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CallbackLease(Action onDispose) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingLease(string message) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException(message));
    }
}
