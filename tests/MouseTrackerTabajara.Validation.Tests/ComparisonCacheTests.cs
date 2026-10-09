using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Validation.Tests;

/// <summary>Equivalence and cancellation for one-pass comparison indexing.
/// Synthetic controls only. Elapsed figures are measurements of this process, not a 30-source speed claim.</summary>
public sealed class ComparisonCacheTests(ITestOutputHelper output)
{
    static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SmallRetainedWorkloadMatchesProjectionForBothTargets()
    {
        var folder = Path.Combine(RepoRoot(), "artifacts", "comparison-cache", "s16-small-" + Guid.NewGuid().ToString("N"));
        var fixture = new ThirtyRecordingPerformanceFixture(folder, 3, 24);
        output.WriteLine("retained evidence=" + folder);
        Assert.Equal(2, fixture.Workloads.Length);
        foreach (var workload in fixture.Workloads)
        {
            var replay = ThirtyRecordingPerformanceFixture.Read(folder, workload.Plan.MeasurementTarget);
            Assert.Equal(JsonDefaults.Serialize(workload), JsonDefaults.Serialize(replay));
            var report = IndependentValidationComparison.Compare(replay.Snapshot, replay.Plan, replay.Sampling);
            Assert.Equal(72, report.FullSourcePopulation);
            Assert.Equal(72, report.SampledUniqueFrames);
            Assert.Equal(66, report.ReferenceRecordedFrames);
            var expectedJoins = workload.Plan.MeasurementTarget == ValidationMeasurementTarget.RawAutomatic ? 57 : 60;
            Assert.Equal(expectedJoins, report.PositionJoinedFrames);
            Assert.Equal(expectedJoins, replay.ExpectedFrames.Count(f => f.PositionErrorPixels.HasValue));
            Assert.Equal(workload.Plan.MeasurementTarget == ValidationMeasurementTarget.RawAutomatic ? 2d : 0d, report.MaximumPositionErrorPixels);
            foreach (var truth in replay.ExpectedFrames)
            {
                var actual = report.Frames.Single(f => f.VideoId == truth.VideoId && f.Frame == truth.Frame);
                Assert.Equal(truth.PositionErrorPixels, actual.PositionErrorPixels);
                Assert.Equal(truth.FalseAccepted, actual.FalseAccepted);
                Assert.Equal(truth.MissedVisible, actual.MissedVisible);
                Assert.Equal(truth.Disputed, actual.Disagreement);
                if (truth.MissingReference || truth.Disputed) Assert.NotEmpty(actual.Holds);
            }
            foreach (var truth in replay.ExpectedEndpoints)
            {
                var actual = report.Endpoints.Single(e => e.VideoId == truth.VideoId && e.StartFrame == truth.StartFrame && e.EndFrame == truth.EndFrame);
                Assert.Equal(truth.PathDifferenceDiameters, actual.PathDifferenceDiameters);
                Assert.Equal(truth.SiteOccupancyDifference, actual.SiteOccupancyDifference);
                Assert.Equal(truth.WithinTolerances, actual.WithinTolerances);
            }
            AssertUnresolvedIsTheDisagreementHold(replay.Plan, report);
            Assert.Equal(replay.ExpectedFrames.Count(f => f.Disputed), report.SplitSummaries.Sum(s => s.UnresolvedDisagreementFrames));
            Assert.Contains(report.Holds, h => h.Contains("Synthetic tooling", StringComparison.Ordinal));
            output.WriteLine($"target={workload.Plan.MeasurementTarget}; sampled={report.SampledUniqueFrames}; joined={report.PositionJoinedFrames}; endpoints={report.Endpoints.Length}; unresolved={report.SplitSummaries.Sum(s => s.UnresolvedDisagreementFrames)}; notJoined={report.SplitSummaries.Sum(s => s.NotJoinedFrames)}");
        }
        foreach (var receipt in fixture.Receipts.Where(r => r.Kind is "source" or "run" or "corrections" or "plan"))
            output.WriteLine($"pinned-input kind={receipt.Kind}; identity={receipt.Identity}; sha256={receipt.Sha256}");
        WriteAssemblyReceipt(output, "s16-small", folder);
    }

    [Fact]
    public void PinnedControlPreservesFrameEndpointAndSplitCounts()
    {
        var scenario = PinnedControl();
        var timings = new List<string>();
        foreach (var target in new[] { ValidationMeasurementTarget.RawAutomatic, ValidationMeasurementTarget.InspectedCorrected })
        {
            var ready = scenario.ForTarget(target);
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            var report = IndependentValidationComparison.Compare(ready.Snapshot, ready.Plan, ready.Sampling);
            watch.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var again = IndependentValidationComparison.Compare(ready.Snapshot, ready.Plan, ready.Sampling);
            Assert.Equal(JsonDefaults.Serialize(report), JsonDefaults.Serialize(again));
            AssertConstructedTruth(ready, report);
            AssertUnresolvedIsTheDisagreementHold(ready.Plan, report);
            timings.Add($"target={target}; elapsedMs={watch.Elapsed.TotalMilliseconds:R}; allocatedBytes={allocated}; sampled={report.SampledUniqueFrames}; joined={report.PositionJoinedFrames}; endpoints={report.Endpoints.Length}; unresolved={report.SplitSummaries.Sum(s => s.UnresolvedDisagreementFrames)}; notJoined={report.SplitSummaries.Sum(s => s.NotJoinedFrames)}; maxError={report.MaximumPositionErrorPixels}");
        }
        foreach (var line in timings) output.WriteLine(line);
        output.WriteLine("pinned-folder=" + scenario.Folder);
        WriteAssemblyReceipt(output, "pinned-control", scenario.Folder, timings);
    }

    [Fact]
    public void LatestRevisionAndAppearanceOrderStayExact()
    {
        using var workspace = CacheWorkspace.Create();
        var asset = workspace.AddRecording(8, 11);
        workspace.AddRun(asset, frame => Point(frame, 40, 32));
        var ready = workspace.Freeze(ValidationMeasurementTarget.RawAutomatic, randomFrames: 8, intervals: 0, disagreementPixels: 100);
        var hash = SampleHash(ready.Sampling);
        var second = workspace.AddReference(hash, Reference(asset, 3, ReferenceReviewerLane.Second, "Second reviewer", ReferenceVisibility.Visible, 60), 1);
        var latest = workspace.AddReference(hash, Reference(asset, 3, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 5);
        var ignoredLower = workspace.AddReference(hash, Reference(asset, 3, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 10), 1);
        Assert.True(latest.Id < ignoredLower.Id);
        var report = CompareWithHistory(workspace, ready);
        var frame = report.Frames.Single(f => f.Frame == 3);
        Assert.Equal(0d, frame.PositionErrorPixels);
        Assert.Equal(new[] { second.Id, latest.Id }, frame.ReferenceIds.ToArray());
        Assert.DoesNotContain(ignoredLower.Id, frame.ReferenceIds);
        Assert.False(frame.Disagreement);
    }

    [Fact]
    public void EqualRevisionTieUsesTheLaterHistoryRow()
    {
        using var workspace = CacheWorkspace.Create();
        var asset = workspace.AddRecording(4, 12);
        workspace.AddRun(asset, frame => Point(frame, 40, 32));
        var ready = workspace.Freeze(ValidationMeasurementTarget.RawAutomatic, 4, 0);
        var hash = SampleHash(ready.Sampling);
        var first = workspace.AddReference(hash, Reference(asset, 1, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 10), 2);
        var later = workspace.AddReference(hash, Reference(asset, 1, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 2);
        var report = CompareWithHistory(workspace, ready);
        var frame = report.Frames.Single(f => f.Frame == 1);
        Assert.Equal(0d, frame.PositionErrorPixels);
        Assert.Equal(new[] { later.Id }, frame.ReferenceIds.ToArray());
        Assert.DoesNotContain(first.Id, frame.ReferenceIds);
    }

    [Fact]
    public void ExactConsensusIdsResolveAndMissingAutomaticPositionIsNotUnresolved()
    {
        using var workspace = CacheWorkspace.Create();
        var asset = workspace.AddRecording(8, 21);
        workspace.AddRun(asset, frame => frame == 5 || frame == 6 || frame == 7 ? new TrackPoint(frame, frame / 10d, null, null, PointStatus.Missing, 0) : Point(frame, 40, 32));
        var ready = workspace.Freeze(ValidationMeasurementTarget.RawAutomatic, 8, 0, disagreementPixels: 3);
        var hash = SampleHash(ready.Sampling);
        var firstOld = workspace.AddReference(hash, Reference(asset, 4, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 10), 1);
        var first = workspace.AddReference(hash, Reference(asset, 4, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 2);
        var second = workspace.AddReference(hash, Reference(asset, 4, ReferenceReviewerLane.Second, "Second reviewer", ReferenceVisibility.Visible, 60), 1);
        workspace.AddReference(hash, Reference(asset, 4, ReferenceReviewerLane.Consensus, "Consensus reviewer", ReferenceVisibility.Visible, 40, [firstOld.Id, second.Id]), 1);
        var unresolvedFirst = workspace.AddReference(hash, Reference(asset, 6, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 1);
        var unresolvedSecond = workspace.AddReference(hash, Reference(asset, 6, ReferenceReviewerLane.Second, "Second reviewer", ReferenceVisibility.Visible, 60), 1);
        var resolvedFirst = workspace.AddReference(hash, Reference(asset, 5, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 1);
        var resolvedSecond = workspace.AddReference(hash, Reference(asset, 5, ReferenceReviewerLane.Second, "Second reviewer", ReferenceVisibility.Visible, 60), 1);
        workspace.AddReference(hash, Reference(asset, 5, ReferenceReviewerLane.Consensus, "Consensus reviewer", ReferenceVisibility.Visible, 40, [resolvedFirst.Id, resolvedSecond.Id]), 1);
        workspace.AddReference(hash, Reference(asset, 7, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 1);
        var report = CompareWithHistory(workspace, ready);
        var wrongConsensus = report.Frames.Single(f => f.Frame == 4);
        Assert.True(wrongConsensus.Disagreement);
        Assert.Null(wrongConsensus.PositionErrorPixels);
        Assert.Contains(wrongConsensus.Holds, h => h.StartsWith("Independent reviewers disagree; unresolved reference is held.", StringComparison.Ordinal));
        Assert.DoesNotContain(firstOld.Id, wrongConsensus.ReferenceIds);
        Assert.Contains(first.Id, wrongConsensus.ReferenceIds);
        var resolved = report.Frames.Single(f => f.Frame == 5);
        Assert.True(resolved.Disagreement);
        Assert.True(resolved.MissedVisible);
        Assert.Null(resolved.PositionErrorPixels);
        Assert.Equal(3, resolved.ReferenceIds.Length);
        Assert.Contains(resolvedFirst.Id, resolved.ReferenceIds);
        Assert.Contains(resolvedSecond.Id, resolved.ReferenceIds);
        Assert.DoesNotContain(resolved.Holds, h => h.Contains("unresolved", StringComparison.OrdinalIgnoreCase));
        var unresolved = report.Frames.Single(f => f.Frame == 6);
        Assert.Contains(unresolved.Holds, h => h.StartsWith("Independent reviewers disagree; unresolved reference is held.", StringComparison.Ordinal));
        Assert.Null(unresolved.MissedVisible);
        Assert.Contains(unresolvedFirst.Id, unresolved.ReferenceIds);
        Assert.Contains(unresolvedSecond.Id, unresolved.ReferenceIds);
        var unavailable = report.Frames.Single(f => f.Frame == 7);
        Assert.True(unavailable.MissedVisible);
        Assert.DoesNotContain(unavailable.Holds, h => h.Contains("unresolved", StringComparison.OrdinalIgnoreCase));
        var development = report.SplitSummaries.Single(s => s.Role == ValidationSplit.Development);
        Assert.Equal(2, development.UnresolvedDisagreementFrames);
        Assert.Equal(report.Frames.Count(f => f.Holds.Any(h => h.StartsWith("Independent reviewers disagree; unresolved reference is held.", StringComparison.Ordinal))), development.UnresolvedDisagreementFrames);
        Assert.True(development.NotJoinedFrames > development.UnresolvedDisagreementFrames);
        foreach (var role in new[] { ValidationSplit.IndependentValidation, ValidationSplit.Unspecified })
        {
            var summary = report.SplitSummaries.Single(s => s.Role == role);
            Assert.Equal(0, summary.RecordingCount);
            Assert.Equal(0, summary.UnresolvedDisagreementFrames);
            Assert.Null(summary.MeanRandomPositionErrorPixels);
        }
    }

    [Fact]
    public void AmbiguousNotVisibleAndFailedProducerStayDistinct()
    {
        using var workspace = CacheWorkspace.Create();
        var asset = workspace.AddRecording(6, 31, included: false, exclusion: "Synthetic exclusion control");
        workspace.AddRun(asset, frame => Point(frame, 42, 32));
        var failed = workspace.AddRecording(4, 32);
        // Sampling JSON evaluates EffectiveRun, so the stored correction must name this run. Invalid timing still blocks the join.
        workspace.AddRun(failed, frame => Point(frame, 42, 32), timingVerified: false, corrections: workspace.CorrectionsFor(failed, frame => JsonDefaults.Serialize(new ManualCorrection(failed.Id + "-run", frame, 40, 32, "Invalid raw run must not publish this correction", Epoch))));
        var ready = workspace.Freeze(ValidationMeasurementTarget.InspectedCorrected, 6, 0);
        var hash = SampleHash(ready.Sampling);
        workspace.AddReference(hash, Reference(asset, 1, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Ambiguous, null), 1);
        workspace.AddReference(hash, Reference(asset, 2, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.NotVisible, null), 1);
        workspace.AddReference(hash, Reference(asset, 3, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 1);
        var report = CompareWithHistory(workspace, ready);
        var ambiguous = report.Frames.Single(f => f.VideoId == asset.Id && f.Frame == 1);
        Assert.Equal(ReferenceVisibility.Ambiguous, ambiguous.Visibility);
        Assert.Null(ambiguous.PositionErrorPixels);
        Assert.Contains(ambiguous.Holds, h => h.Contains("ambiguous", StringComparison.Ordinal));
        var hidden = report.Frames.Single(f => f.VideoId == asset.Id && f.Frame == 2);
        Assert.Equal(ReferenceVisibility.NotVisible, hidden.Visibility);
        Assert.Equal(true, hidden.FalseAccepted);
        Assert.Null(hidden.PositionErrorPixels);
        var joined = report.Frames.Single(f => f.VideoId == asset.Id && f.Frame == 3);
        Assert.Equal(2d, joined.PositionErrorPixels);
        Assert.All(report.Frames.Where(f => f.VideoId == failed.Id), f => Assert.Contains(f.Holds, h => h.Contains("Producer completed-frame evidence is invalid", StringComparison.Ordinal)));
        Assert.All(report.Frames.Where(f => f.VideoId == failed.Id), f => Assert.Null(f.PositionErrorPixels));
        Assert.All(report.Frames.Where(f => f.VideoId == asset.Id), f => Assert.True(f.Excluded));
        var development = report.SplitSummaries.Single(s => s.Role == ValidationSplit.Development);
        Assert.Equal(1, development.AmbiguousFrames);
        Assert.Equal(1, development.NotVisibleReferenceFrames);
        Assert.Equal(1, development.FalseAcceptedFrames);
        Assert.Equal(0, development.UnresolvedDisagreementFrames);
    }

    [Fact]
    public void RequestedTargetChoosesRawOrCorrectedPointsWithoutApplyingTheOther()
    {
        using var rawWorkspace = CacheWorkspace.Create();
        var rawAsset = rawWorkspace.AddRecording(4, 41);
        // A matching correction would move the center to the reference. Raw scoring must keep the stored 2 px gap.
        rawWorkspace.AddRun(rawAsset, frame => Point(frame, 42, 32), corrections: rawWorkspace.CorrectionsFor(rawAsset, frame => JsonDefaults.Serialize(new ManualCorrection(rawAsset.Id + "-run", frame, 40, 32, "Raw target must not apply corrections", Epoch))));
        var rawReady = rawWorkspace.Freeze(ValidationMeasurementTarget.RawAutomatic, 4, 0, positionPixels: 1);
        var rawHash = SampleHash(rawReady.Sampling);
        rawWorkspace.AddReference(rawHash, Reference(rawAsset, 1, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 1);
        var rawReport = CompareWithHistory(rawWorkspace, rawReady);
        Assert.Equal(2d, rawReport.Frames.Single(f => f.Frame == 1).PositionErrorPixels);
        Assert.Equal(ValidationMeasurementTarget.RawAutomatic, rawReport.MeasurementTarget);

        using var correctedWorkspace = CacheWorkspace.Create();
        var correctedAsset = correctedWorkspace.AddRecording(4, 42);
        var correctedRun = correctedWorkspace.AddRun(correctedAsset, frame => Point(frame, 42, 32), corrections: correctedWorkspace.CorrectionsFor(correctedAsset, frame => JsonDefaults.Serialize(new ManualCorrection(correctedAsset.Id + "-run", frame, 40, 32, "Corrected center", Epoch))));
        var correctedReady = correctedWorkspace.Freeze(ValidationMeasurementTarget.InspectedCorrected, 4, 0, positionPixels: 1);
        var correctedHash = SampleHash(correctedReady.Sampling);
        correctedWorkspace.AddReference(correctedHash, Reference(correctedAsset, 1, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 1);
        Assert.Equal(correctedAsset.Id + "-run", correctedRun.Id);
        var correctedReport = CompareWithHistory(correctedWorkspace, correctedReady);
        Assert.Equal(0d, correctedReport.Frames.Single(f => f.Frame == 1).PositionErrorPixels);
        Assert.Equal(ValidationMeasurementTarget.InspectedCorrected, correctedReport.MeasurementTarget);
    }

    [Fact]
    public void MismatchedCorrectionRunIdFailsWhenSamplingJsonIsHashed()
    {
        using var workspace = CacheWorkspace.Create();
        var asset = workspace.AddRecording(4, 91);
        workspace.AddRun(asset, frame => Point(frame, 42, 32), corrections: workspace.CorrectionsFor(asset, frame => JsonDefaults.Serialize(new ManualCorrection("wrong-run", frame, 40, 32, "Outside this run", Epoch))));
        var ready = workspace.Freeze(ValidationMeasurementTarget.RawAutomatic, 4, 0);
        var error = Assert.Throws<ArgumentException>(() => IndependentValidationComparison.Compare(workspace.SnapshotWith(ready), ready.Plan, ready.Sampling));
        Assert.Contains("Correction is outside this run", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedOrMissingSourceBytesHoldEveryJoinedFrame()
    {
        using var workspace = CacheWorkspace.Create();
        var asset = workspace.AddRecording(4, 51);
        workspace.AddRun(asset, frame => Point(frame, 42, 32));
        var ready = workspace.Freeze(ValidationMeasurementTarget.RawAutomatic, 4, 0);
        var hash = SampleHash(ready.Sampling);
        workspace.AddReference(hash, Reference(asset, 1, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 1);
        var intact = CompareWithHistory(workspace, ready);
        Assert.Equal(2d, intact.Frames.Single(f => f.Frame == 1).PositionErrorPixels);
        File.AppendAllText(asset.Path, "x");
        var changed = CompareWithHistory(workspace, ready);
        Assert.All(changed.Frames, f => Assert.Null(f.PositionErrorPixels));
        Assert.All(changed.Frames, f => Assert.Contains(f.Holds, h => h.Contains("Original source bytes are missing/changed", StringComparison.Ordinal)));
        var missingAsset = asset with { Path = Path.Combine(workspace.Folder, "absent-source.bin") };
        var missingSnapshot = workspace.SnapshotWith(ready, imported => imported.Select(a => a.Id == asset.Id ? missingAsset : a).ToImmutableArray());
        var missing = IndependentValidationComparison.Compare(missingSnapshot, ready.Plan, ready.Sampling);
        Assert.All(missing.Frames, f => Assert.Contains(f.Holds, h => h.Contains("Original source bytes are missing/changed", StringComparison.Ordinal)));
        Assert.Equal(0, missing.PositionJoinedFrames);
    }

    [Fact]
    public void LegacyAbsentSummariesSerializeAndStayHeld()
    {
        using var workspace = CacheWorkspace.Create();
        var asset = workspace.AddRecording(4, 61);
        workspace.AddRun(asset, frame => Point(frame, 40, 32));
        var ready = workspace.Freeze(ValidationMeasurementTarget.RawAutomatic, 4, 0);
        var hash = SampleHash(ready.Sampling);
        workspace.AddReference(hash, Reference(asset, 0, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 1);
        var report = CompareWithHistory(workspace, ready);
        Assert.Equal(3, report.SplitSummaries.Length);
        var node = JsonNode.Parse(JsonDefaults.Serialize(report))!.AsObject();
        node.Remove("split_summaries");
        node["mean_random_position_error_pixels"] = 999;
        var legacy = JsonDefaults.Deserialize<ValidationComparisonReport>(node.ToJsonString());
        Assert.True(legacy.SplitSummaries.IsDefault);
        Assert.False(ValidationSplitSummaries.TryRead(legacy, out _, out var hold));
        Assert.Contains("absent", hold, StringComparison.Ordinal);
        var serialized = JsonDefaults.Serialize(legacy);
        var again = JsonDefaults.Deserialize<ValidationComparisonReport>(serialized);
        Assert.False(ValidationSplitSummaries.TryRead(again, out _, out _));
        Assert.Equal(999, again.MeanRandomPositionErrorPixels);
        Assert.Equal(0d, report.Frames.Single(f => f.Frame == 0).PositionErrorPixels);
    }

    [Fact]
    public async Task CancellationBeforeAndDuringSourceReadDoesNotPoisonTheNextComparison()
    {
        using var workspace = CacheWorkspace.Create();
        var asset = workspace.AddRecording(6, 71);
        workspace.AddRun(asset, frame => Point(frame, 40, 32));
        var ready = workspace.Freeze(ValidationMeasurementTarget.RawAutomatic, 6, 0);
        var hash = SampleHash(ready.Sampling);
        workspace.AddReference(hash, Reference(asset, 2, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40), 1);
        var snapshot = workspace.SnapshotWith(ready);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => IndependentValidationComparison.Compare(snapshot, ready.Plan, ready.Sampling, cancelled.Token));
        }
        var afterPreCancel = IndependentValidationComparison.Compare(snapshot, ready.Plan, ready.Sampling);
        Assert.Equal(0d, afterPreCancel.Frames.Single(f => f.Frame == 2).PositionErrorPixels);

        var pipe = "tabajara-cache-cancel-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipe, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var piped = snapshot with { Imported = snapshot.Imported.Select(a => a.Id == asset.Id ? a with { Path = @"\\.\pipe\" + pipe } : a).ToImmutableArray() };
        using var during = new CancellationTokenSource();
        Exception? caught = null;
        var worker = Task.Run(() =>
        {
            try { IndependentValidationComparison.Compare(piped, ready.Plan, ready.Sampling, during.Token); }
            catch (Exception ex) { caught = ex; }
        });
        using var connected = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await server.WaitForConnectionAsync(connected.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("Comparison did not reach the source read."); }
        server.WriteByte(1);
        server.Flush();
        during.Cancel();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!worker.IsCompleted && DateTime.UtcNow < deadline)
        {
            try { server.WriteByte(2); server.Flush(); }
            catch (IOException) { break; }
            await Task.Delay(20);
        }
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<OperationCanceledException>(caught);
        var afterMidRead = IndependentValidationComparison.Compare(snapshot, ready.Plan, ready.Sampling);
        Assert.Equal(JsonDefaults.Serialize(afterPreCancel), JsonDefaults.Serialize(afterMidRead));
        output.WriteLine("cancellation pre-cancel and mid-read both threw; following comparison matched the pre-cancel success.");
    }

    [Fact]
    public void BiologicalEventsAreJoinedByKindAndNearestFrame()
    {
        using var workspace = CacheWorkspace.Create();
        var asset = workspace.AddRecording(12, 81);
        var events = ImmutableArray.Create(
            JsonDefaults.Serialize(BiologicalEvent.Create(asset.Id, asset.Id + "-run", BiologicalEventKind.WaterEntry, 1, asset.Metadata, "Synthetic water", "Producer", 1, Epoch, "water-1")),
            JsonDefaults.Serialize(BiologicalEvent.Create(asset.Id, asset.Id + "-run", BiologicalEventKind.WaterEntry, 9, asset.Metadata, "Synthetic later water", "Producer", 1, Epoch, "water-9")),
            JsonDefaults.Serialize(BiologicalEvent.Create(asset.Id, asset.Id + "-run", BiologicalEventKind.PlatformMounting, 4, asset.Metadata, "Synthetic mounting", "Producer", 1, Epoch, "mount-4")));
        workspace.AddRun(asset, frame => Point(frame, 40, 32), events: events);
        var ready = workspace.Freeze(ValidationMeasurementTarget.RawAutomatic, 12, 0, eventSeconds: .5);
        var hash = SampleHash(ready.Sampling);
        workspace.AddReference(hash, Reference(asset, 4, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40, eventKind: ReferenceEvent.WaterEntry), 1);
        workspace.AddReference(hash, Reference(asset, 5, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40, eventKind: ReferenceEvent.ResearcherIntervention), 1);
        var report = CompareWithHistory(workspace, ready);
        var water = report.Frames.Single(f => f.Frame == 4);
        Assert.Equal(.3, water.EventTimingErrorSeconds!.Value, 10);
        Assert.Equal(true, water.EventPass);
        var other = report.Frames.Single(f => f.Frame == 5);
        Assert.Null(other.EventTimingErrorSeconds);
        Assert.Contains(other.Holds, h => h.Contains("Published biological annotation is unavailable", StringComparison.Ordinal));
    }

    static void AssertUnresolvedIsTheDisagreementHold(IndependentValidationPlan plan, ValidationComparisonReport report)
    {
        foreach (var summary in report.SplitSummaries)
        {
            var ids = plan.Recordings.Where(r => r.Split == summary.Role).Select(r => r.VideoId).ToHashSet(StringComparer.Ordinal);
            var rows = report.Frames.Where(f => ids.Contains(f.VideoId)).ToArray();
            Assert.Equal(rows.Count(f => f.Holds.Any(h => h.StartsWith("Independent reviewers disagree; unresolved reference is held.", StringComparison.Ordinal))), summary.UnresolvedDisagreementFrames);
            Assert.Equal(rows.Count(f => !f.PositionErrorPixels.HasValue), summary.NotJoinedFrames);
        }
        Assert.True(report.SplitSummaries.Sum(s => s.NotJoinedFrames) > report.SplitSummaries.Sum(s => s.UnresolvedDisagreementFrames));
    }

    static void AssertConstructedTruth(ReadyComparison ready, ValidationComparisonReport report)
    {
        Assert.Equal(ready.Plan.Recordings.Length * ready.FramesPerRecording, report.FullSourcePopulation);
        Assert.Equal(report.FullSourcePopulation, report.SampledUniqueFrames);
        Assert.Equal(ready.Plan.MeasurementTarget, report.MeasurementTarget);
        var unresolved = 0;
        var notJoined = 0;
        foreach (var frame in report.Frames)
        {
            var expected = ExpectFrame(frame.Frame, ready.Plan.MeasurementTarget);
            Assert.Equal(expected.Error, frame.PositionErrorPixels);
            Assert.Equal(expected.FalseAccepted, frame.FalseAccepted);
            Assert.Equal(expected.Missed, frame.MissedVisible);
            Assert.Equal(expected.Disputed, frame.Disagreement);
            Assert.True(frame.RandomSample);
            Assert.Equal(1d, frame.InclusionProbability);
            Assert.True(frame.SecondReviewRequired);
            if (expected.Disputed) Assert.Contains(frame.Holds, h => h.StartsWith("Independent reviewers disagree; unresolved reference is held.", StringComparison.Ordinal));
            if (!frame.PositionErrorPixels.HasValue) notJoined++;
            if (frame.Holds.Any(h => h.StartsWith("Independent reviewers disagree; unresolved reference is held.", StringComparison.Ordinal))) unresolved++;
        }
        Assert.Equal(unresolved, report.SplitSummaries.Sum(s => s.UnresolvedDisagreementFrames));
        Assert.NotEqual(notJoined, unresolved);
        foreach (var endpoint in report.Endpoints)
        {
            var held = false;
            for (var frame = endpoint.StartFrame; frame <= endpoint.EndFrame; frame++)
            {
                var expected = ExpectFrame(frame, ready.Plan.MeasurementTarget);
                if (expected.Missing || expected.Disputed || expected.Invisible || expected.Missed == true) held = true;
            }
            if (held)
            {
                Assert.Null(endpoint.PathDifferenceDiameters);
                Assert.Null(endpoint.SiteOccupancyDifference);
                Assert.Null(endpoint.WithinTolerances);
            }
            else if (ready.Plan.MeasurementTarget == ValidationMeasurementTarget.RawAutomatic)
            {
                Assert.Equal(0d, endpoint.PathDifferenceDiameters);
                Assert.Equal(1d, endpoint.SiteOccupancyDifference);
                Assert.Equal(false, endpoint.WithinTolerances);
            }
            else
            {
                Assert.Equal(0d, endpoint.PathDifferenceDiameters);
                Assert.Equal(0d, endpoint.SiteOccupancyDifference);
                Assert.Equal(true, endpoint.WithinTolerances);
            }
        }
        foreach (var summary in report.SplitSummaries)
        {
            var ids = ready.Plan.Recordings.Where(r => r.Split == summary.Role).Select(r => r.VideoId).ToHashSet(StringComparer.Ordinal);
            var rows = report.Frames.Where(f => ids.Contains(f.VideoId)).ToArray();
            Assert.Equal(rows.Length, summary.SampledFrames);
            Assert.Equal(rows.Count(f => f.PositionErrorPixels.HasValue), summary.PositionJoinedFrames);
            Assert.Equal(rows.Count(f => f.Holds.Any(h => h.StartsWith("Independent reviewers disagree; unresolved reference is held.", StringComparison.Ordinal))), summary.UnresolvedDisagreementFrames);
            Assert.Equal(rows.Count(f => f.Visibility == ReferenceVisibility.NotVisible), summary.NotVisibleReferenceFrames);
            Assert.Equal(rows.Count(f => f.Visibility == ReferenceVisibility.Ambiguous), summary.AmbiguousFrames);
            Assert.Equal(rows.Count(f => !f.PositionErrorPixels.HasValue), summary.NotJoinedFrames);
            Assert.Equal(ids.Count == 0 ? 0 : rows.Count(f => f.Excluded), summary.ExcludedSampledFrames);
        }
        var excluded = report.Frames.Where(f => f.VideoId == ready.Plan.Recordings[0].VideoId).ToArray();
        Assert.All(excluded, f => Assert.True(f.Excluded));
        Assert.Contains(report.Holds, h => h.Contains("Synthetic tooling", StringComparison.Ordinal));
    }

    static Expected ExpectFrame(int frame, ValidationMeasurementTarget target)
    {
        var missing = frame % 19 == 0;
        var disputed = !missing && frame % 23 == 0;
        var invisible = !missing && !disputed && frame % 17 == 0;
        var accepted = target == ValidationMeasurementTarget.InspectedCorrected || frame % 13 != 0;
        double? error = !missing && !disputed && !invisible && accepted ? target == ValidationMeasurementTarget.RawAutomatic ? 2d : 0d : null;
        bool? falseAccepted = !missing && !disputed && invisible ? accepted : null;
        bool? missed = !missing && !disputed && !invisible ? !accepted : null;
        return new(missing, disputed, invisible, error, falseAccepted, missed);
    }

    readonly record struct Expected(bool Missing, bool Disputed, bool Invisible, double? Error, bool? FalseAccepted, bool? Missed);

    static ValidationComparisonReport CompareWithHistory(CacheWorkspace workspace, ReadyComparison ready) =>
        IndependentValidationComparison.Compare(workspace.SnapshotWith(ready), ready.Plan, ready.Sampling);

    static string SampleHash(ValidationSampling sampling) => IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(sampling));

    static TrackPoint Point(int frame, int x, int y) => new(frame, frame / 10d, x, y, PointStatus.Tracked, 1);

    static IndependentReference Reference(VideoAsset asset, int frame, ReferenceReviewerLane lane, string reviewer, ReferenceVisibility visibility, int? x, ImmutableArray<long> consensus = default, ReferenceEvent eventKind = ReferenceEvent.None)
    {
        var reference = new IndependentReference(asset.Id, frame, lane, reviewer, visibility, visibility == ReferenceVisibility.Visible ? x : null, visibility == ReferenceVisibility.Visible ? 32 : null, eventKind, "Synthetic cache reference; not held-out evidence", consensus.IsDefault ? [] : consensus);
        reference.Validate(asset.Metadata);
        return reference;
    }

    static PinnedControlScenario PinnedControl()
    {
        var folder = Path.Combine(RepoRoot(), "artifacts", "comparison-cache", "pinned-control-v1");
        Directory.CreateDirectory(folder);
        const int recordings = 6;
        const int frames = 200;
        var marker = Path.Combine(folder, "ready.txt");
        if (!File.Exists(marker))
        {
            for (var i = 0; i < recordings; i++) File.WriteAllBytes(Path.Combine(folder, $"source-{i:D2}.bin"), [checked((byte)(i + 1)), 9, 8, 7]);
            File.WriteAllText(marker, "recordings=6; frames=200; pattern=s16-constructed; no-detector");
        }
        return new PinnedControlScenario(folder, recordings, frames);
    }

    static void WriteAssemblyReceipt(ITestOutputHelper output, string label, string folder, IReadOnlyList<string>? timings = null)
    {
        var lines = new List<string>();
        foreach (var assembly in new[] { typeof(IndependentValidationComparison).Assembly, typeof(MetricCalculator).Assembly, typeof(OpenCvTracker).Assembly, typeof(ValidationHistoryStore).Assembly })
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant();
            var line = $"assembly={assembly.GetName().Name}; sha256={hash}; path={assembly.Location}";
            lines.Add(line);
            output.WriteLine(line);
        }
        var receipts = Path.Combine(RepoRoot(), "artifacts", "comparison-cache", "receipts");
        Directory.CreateDirectory(receipts);
        var name = (Environment.GetEnvironmentVariable("COMPARISON_CACHE_LABEL") ?? "run") + "-" + label + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json";
        File.WriteAllText(Path.Combine(receipts, name), JsonDefaults.Serialize(new { label, folder, assemblies = lines, timings = timings ?? [] }));
        output.WriteLine("receipt=" + Path.Combine(receipts, name));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".workmap"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test base directory.");
    }

    sealed class PinnedControlScenario(string folder, int recordings, int frames)
    {
        public string Folder { get; } = folder;
        public ReadyComparison ForTarget(ValidationMeasurementTarget target)
        {
            using var workspace = CacheWorkspace.Open(Folder);
            for (var i = 0; i < recordings; i++)
            {
                var asset = workspace.AddRecording(frames, i + 1, included: i != 0, exclusion: i == 0 ? "Synthetic excluded recording retained in the pool" : "", fileName: $"source-{i:D2}.bin");
                var run = workspace.AddRun(asset, frame => frame % 13 == 0 ? new TrackPoint(frame, frame / 10d, null, null, PointStatus.Missing, 0) : Point(frame, 42, 32));
                workspace.ReplaceCorrections(asset.Id, workspace.CorrectionsFor(asset, frame => JsonDefaults.Serialize(new ManualCorrection(run.Id, frame, 40, 32, "Constructed exact-center correction control; not an inspection claim", Epoch))));
            }
            var ready = workspace.Freeze(target, frames, 3, secondReviewFraction: 1);
            var hash = SampleHash(ready.Sampling);
            foreach (var asset in workspace.Assets)
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    var missing = frame % 19 == 0;
                    if (missing) continue;
                    var disputed = frame % 23 == 0;
                    var invisible = !disputed && frame % 17 == 0;
                    foreach (var lane in new[] { ReferenceReviewerLane.First, ReferenceReviewerLane.Second })
                    {
                        var x = invisible ? (int?)null : disputed && lane == ReferenceReviewerLane.Second ? 46 : 40;
                        workspace.AddReference(hash, Reference(asset, frame, lane, "synthetic-" + lane, invisible ? ReferenceVisibility.NotVisible : ReferenceVisibility.Visible, x), 1);
                    }
                }
            }
            return ready with { Snapshot = workspace.SnapshotWith(ready) };
        }
    }

    sealed record ReadyComparison(IndependentValidationSnapshot Snapshot, IndependentValidationPlan Plan, ValidationSampling Sampling, int FramesPerRecording);

    sealed class CacheWorkspace : IDisposable
    {
        readonly List<ValidationFrozenRun> _runs = [];
        readonly Dictionary<string, string> _runIds = new(StringComparer.Ordinal);
        long _nextId = 1;
        public string Folder { get; }
        public List<VideoAsset> Assets { get; } = [];
        public List<ValidationHistoryEntry> History { get; } = [];
        CacheWorkspace(string folder) => Folder = folder;
        public static CacheWorkspace Create() => Open(Path.Combine(RepoRoot(), "artifacts", "comparison-cache", "case-" + Guid.NewGuid().ToString("N")));
        public static CacheWorkspace Open(string folder)
        {
            Directory.CreateDirectory(folder);
            return new CacheWorkspace(folder);
        }
        public VideoAsset AddRecording(int frames, int seedByte, bool included = true, string exclusion = "", string? fileName = null)
        {
            var id = fileName is null ? "cache-source-" + Assets.Count.ToString("D2") : Path.GetFileNameWithoutExtension(fileName);
            var path = Path.Combine(Folder, fileName ?? id + ".bin");
            if (!File.Exists(path)) File.WriteAllBytes(path, [checked((byte)seedByte), 4, 5, 6]);
            var asset = new VideoAsset
            {
                Id = id, Path = path, SourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                Included = included, ExclusionReason = exclusion, Metadata = new(64, 64, 10, frames),
                Settings = new() { Cx = .5, Cy = .5, Rx = .4, Ry = .4, ArenaConfirmed = true, Platform = new(.625, .5, .01) }
            };
            Assets.Add(asset);
            return asset;
        }
        public AnalysisRun AddRun(VideoAsset asset, Func<int, TrackPoint> point, ImmutableArray<string>? corrections = null, ImmutableArray<string>? events = null, bool timingVerified = true)
        {
            var points = Enumerable.Range(0, asset.Metadata.FrameCount).Select(point).ToList();
            var run = new AnalysisRun
            {
                Id = asset.Id + "-run", VideoId = asset.Id, SourceSha256 = asset.SourceSha256, CreatedUtc = Epoch,
                AlgorithmContract = AlgorithmIdentity.Contract, SoftwareVersion = "synthetic-cache-control", Metadata = asset.Metadata,
                Settings = asset.Settings, Points = points, NominalTimingVerified = timingVerified, MaximumTimestampErrorSeconds = 0,
                Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings)
            };
            _runIds[asset.Id] = run.Id;
            var rawJson = JsonDefaults.Serialize(run);
            var storedCorrections = corrections ?? [];
            _runs.Add(new(asset.Id, asset.SourceSha256, JsonDefaults.Serialize(asset), rawJson, storedCorrections, IndependentValidationSnapshot.Hash(rawJson), MaterialFingerprint.Hash(storedCorrections), run.AlgorithmContract, run.SoftwareVersion, "pinned-cache-control", events ?? []));
            return run;
        }
        public ImmutableArray<string> CorrectionsFor(VideoAsset asset, Func<int, string> correction) =>
            Enumerable.Range(0, asset.Metadata.FrameCount).Select(correction).ToImmutableArray();
        public void ReplaceCorrections(string videoId, ImmutableArray<string> corrections)
        {
            var index = _runs.FindIndex(r => r.VideoId == videoId);
            var current = _runs[index];
            _runs[index] = current with { Corrections = corrections, CorrectionFingerprint = MaterialFingerprint.Hash(corrections) };
        }
        public ValidationHistoryEntry AddReference(string sampleHash, IndependentReference reference, int revision)
        {
            var json = JsonDefaults.Serialize(reference);
            var entry = new ValidationHistoryEntry(_nextId++, "reference", IndependentValidationComparison.ReferenceKey(sampleHash, reference), revision, json, IndependentValidationSnapshot.Hash(json), reference.Reviewer, reference.Reason, Epoch);
            History.Add(entry);
            return entry;
        }
        public ReadyComparison Freeze(ValidationMeasurementTarget target, int randomFrames, int intervals, double positionPixels = 5, double disagreementPixels = 3, double eventSeconds = .25, double secondReviewFraction = 0)
        {
            var plan = new IndependentValidationPlan
            {
                Id = "cache-" + target, SyntheticToolingOnly = true, MeasurementTarget = target, Seed = 1602026,
                RandomFramesPerRecording = randomFrames, DifficultIntervalsPerRecording = intervals, IntervalFrames = 10, SecondReviewFraction = secondReviewFraction,
                Tolerances = new(positionPixels, disagreementPixels, eventSeconds, .1, .05, .1),
                Recordings = Assets.Select((asset, index) => new ValidationRecording(asset.Id, "cache-session-" + index.ToString("D2"), index % 3 == 0 ? ValidationSplit.Development : index % 3 == 1 ? ValidationSplit.IndependentValidation : ValidationSplit.Unspecified, index % 3 == 0, index % 3 == 1, "synthetic-protocol", "synthetic-allocation")).ToImmutableArray()
            };
            var runs = _runs.ToImmutableArray();
            // Hash stored run JSON only. Serializing the frozen-run record evaluates EffectiveRun and would apply corrections before comparison.
            var producer = IndependentValidationSnapshot.Hash(string.Join("\n", runs.Select(r => r.VideoId + ":" + r.RawRunFingerprint + ":" + r.CorrectionFingerprint)));
            var snapshot = new IndependentValidationSnapshot(Path.Combine(Folder, "no-live-database"), producer, IndependentValidationSnapshot.Hash(""), runs, Assets.ToImmutableArray(), []);
            var sampling = IndependentValidationSampling.Generate(snapshot, plan);
            return new(snapshot, plan, sampling, Assets[0].Metadata.FrameCount);
        }
        public IndependentValidationSnapshot SnapshotWith(ReadyComparison ready, Func<ImmutableArray<VideoAsset>, ImmutableArray<VideoAsset>>? imported = null)
        {
            var assets = imported is null ? ready.Snapshot.Imported : imported(ready.Snapshot.Imported);
            var history = History.ToImmutableArray();
            return ready.Snapshot with { Imported = assets, History = history, ReferenceFingerprint = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(history)), Runs = _runs.ToImmutableArray() };
        }
        public void Dispose() { }
    }
}
