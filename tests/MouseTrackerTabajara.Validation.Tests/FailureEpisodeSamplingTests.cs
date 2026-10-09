using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using Xunit;

namespace MouseTrackerTabajara.Validation.Tests;

/// <summary>
/// Synthetic in-memory recordings only. Frame counts are sampling denominators, not animals.
/// Purposive intervals are not a probability sample and not an accuracy estimate.
/// </summary>
public sealed class FailureEpisodeSamplingTests
{
    [Fact]
    public void LongInitialFailurePlusDistinctLateFailureKeepsSeparateIntervals()
    {
        var rejected = Enumerable.Range(0, 30).Concat(new[] { 80, 81, 82, 83 }).ToArray();
        var statuses = new Dictionary<int, PointStatus> { [80] = PointStatus.Missing, [81] = PointStatus.Ambiguous, [82] = PointStatus.Jump, [83] = PointStatus.Excluded };
        var sampling = Generate(World(("late-gap", 100, rejected, statuses)), randomFrames: 5, difficult: 3, interval: 10);
        AssertPurposive(sampling, "late-gap", 100, (9, 18), (76, 85), (20, 29));
        var rows = Purposive(sampling, "late-gap");
        Assert.Contains("failure-episode", rows[0].Reason, StringComparison.Ordinal);
        Assert.Contains("lower-median", rows[0].Reason, StringComparison.Ordinal);
        Assert.Contains("frames 0-29", rows[0].Reason, StringComparison.Ordinal);
        Assert.Contains("frames 80-83", rows[1].Reason, StringComparison.Ordinal);
        Assert.Contains("distributed fill", rows[2].Reason, StringComparison.Ordinal);
        Assert.Equal(5, sampling.Cases.Count(c => c.RandomSample));
        Assert.All(sampling.Cases.Where(c => c.RandomSample), c => Assert.Equal(5d / 100, c.InclusionProbability));
        Assert.Contains(sampling.Cases, c => c.RandomSample && c.SelectionReason == RandomReason);
        var baseline = Generate(World(("baseline", 100, Enumerable.Range(0, 30).Concat(Enumerable.Range(80, 10)).ToArray(), null)), randomFrames: 10, difficult: 3, interval: 10);
        AssertPurposive(baseline, "baseline", 100, (9, 18), (79, 88), (20, 29));
        Assert.All(baseline.Cases.Where(c => !c.RandomSample), c => Assert.Null(c.InclusionProbability));
    }

    [Fact]
    public void BoundaryDuplicateIntervalIsReplacedByADistinctSlot()
    {
        var sampling = Generate(World(("boundary", 40, new[] { 0, 3, 35 }, null)), randomFrames: 4, difficult: 3, interval: 10);
        AssertPurposive(sampling, "boundary", 40, (0, 9), (30, 39), (5, 14));
        var rows = Purposive(sampling, "boundary");
        Assert.Equal(3, rows.Select(r => (r.Start, r.End)).Distinct().Count());
        Assert.True(rows[2].Start <= rows[0].End && rows[2].End >= rows[0].Start);
        Assert.NotEqual((rows[0].Start, rows[0].End), (rows[2].Start, rows[2].End));
        Assert.Contains("failure-episode", rows[0].Reason, StringComparison.Ordinal);
        Assert.Contains("frames 35-35", rows[1].Reason, StringComparison.Ordinal);
        Assert.Contains("distributed fill", rows[2].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreEpisodesThanBudgetSpreadAcrossTheSource()
    {
        var spread = Generate(World(("spread", 50, new[] { 0, 10, 20, 30, 40 }, null)), randomFrames: 3, difficult: 3, interval: 1);
        AssertPurposive(spread, "spread", 50, (0, 0), (20, 20), (40, 40));
        Assert.DoesNotContain(Purposive(spread, "spread"), row => row.Start is 10 or 30);
        Assert.Contains("source episode index 0 of 5", Purposive(spread, "spread")[0].Reason, StringComparison.Ordinal);
        Assert.Contains("source episode index 4 of 5", Purposive(spread, "spread")[2].Reason, StringComparison.Ordinal);

        var only = Generate(World(("median", 30, new[] { 0, 10, 20 }, null)), randomFrames: 2, difficult: 1, interval: 1);
        AssertPurposive(only, "median", 30, (10, 10));

        var replaced = Generate(World(("replace", 100, new[] { 0, 2, 4, 50, 90 }, null)), randomFrames: 2, difficult: 3, interval: 10);
        AssertPurposive(replaced, "replace", 100, (0, 9), (85, 94), (45, 54));
        Assert.Contains("failure-episode replacement", Purposive(replaced, "replace")[2].Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(Purposive(replaced, "replace"), row => row.Start == 20);
    }

    [Fact]
    public void PopulationShorterThanIntervalYieldsOneUnduplicatedInterval()
    {
        foreach (var rejected in new[] { Array.Empty<int>(), new[] { 0, 4 } })
        {
            var sampling = Generate(World(("short", 5, rejected, null)), randomFrames: 2, difficult: 4, interval: 10);
            AssertPurposive(sampling, "short", 5, (0, 4));
            var reason = Assert.Single(Purposive(sampling, "short")).Reason;
            Assert.Contains("shorter than interval length", reason, StringComparison.Ordinal);
            Assert.Contains("not duplicated", reason, StringComparison.Ordinal);
            Assert.Equal(1, sampling.Cases.Count(c => !c.RandomSample));
        }
    }

    [Fact]
    public void AllFailuresAndNoFailuresStayOnTheFullSource()
    {
        var failed = Generate(World(("all", 20, Enumerable.Range(0, 20).ToArray(), null)), randomFrames: 4, difficult: 2, interval: 4);
        AssertPurposive(failed, "all", 20, (7, 10), (4, 7));
        Assert.Contains("failure-episode", Purposive(failed, "all")[0].Reason, StringComparison.Ordinal);
        Assert.Contains("distributed fill", Purposive(failed, "all")[1].Reason, StringComparison.Ordinal);
        Assert.All(failed.Cases.Where(c => c.RandomSample), c => Assert.Equal(4d / 20, c.InclusionProbability));

        var clean = Generate(World(("none", 20, Array.Empty<int>(), null)), randomFrames: 4, difficult: 2, interval: 4);
        AssertPurposive(clean, "none", 20, (4, 7), (11, 14));
        Assert.All(Purposive(clean, "none"), row =>
        {
            Assert.Contains("distributed interval", row.Reason, StringComparison.Ordinal);
            Assert.Contains("not a successful-track-only sample", row.Reason, StringComparison.Ordinal);
        });
        Assert.Equal(4d / 20, clean.Cases.First(c => c.RandomSample).InclusionProbability);
    }

    [Fact]
    public void AcceptedFrameSplitsEpisodesAndEveryFailedStatusIsRejected()
    {
        var statuses = new Dictionary<int, PointStatus>
        {
            [0] = PointStatus.Missing, [2] = PointStatus.Ambiguous, [4] = PointStatus.Jump, [6] = PointStatus.Excluded
        };
        var sampling = Generate(World(("split", 10, new[] { 0, 2, 4, 6 }, statuses)), randomFrames: 2, difficult: 2, interval: 1);
        AssertPurposive(sampling, "split", 10, (0, 0), (6, 6));
        Assert.Contains("episode index 0 of 4", Purposive(sampling, "split")[0].Reason, StringComparison.Ordinal);
        Assert.Contains("episode index 3 of 4", Purposive(sampling, "split")[1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleRejectedFrameStaysCenteredAndSeedsDoNotMovePurposiveBounds()
    {
        var world = World(("centered", 20, new[] { 5 }, null));
        var first = Generate(world, randomFrames: 5, difficult: 1, interval: 4, seed: 2026);
        var second = Generate(world, randomFrames: 5, difficult: 1, interval: 4, seed: 2026);
        Assert.Equal(JsonDefaults.Serialize(first), JsonDefaults.Serialize(second));
        AssertPurposive(first, "centered", 20, (3, 6));
        Assert.All(first.Cases.Where(c => c.RandomSample), c => Assert.Equal(.25, c.InclusionProbability));

        var otherSeed = Generate(world, randomFrames: 5, difficult: 1, interval: 4, seed: 7);
        Assert.Equal(Purposive(first, "centered").Select(r => (r.Start, r.End)), Purposive(otherSeed, "centered").Select(r => (r.Start, r.End)));
        Assert.NotEqual(RandomSignature(first, "centered"), RandomSignature(otherSeed, "centered"));
        Assert.NotEqual(2026, FailureEpisodeSampling.SecondReviewSeed(2026));
        var scrambled = new[] { 83, -4, 0, 100, 80, 0, 29, 81, 200, 82 };
        scrambled = scrambled.Concat(Enumerable.Range(0, 30)).Reverse().ToArray();
        var direct = FailureEpisodeSampling.Select(100, 10, 3, scrambled);
        Assert.Equal(new[] { (9, 18), (76, 85), (20, 29) }, direct.Select(interval => (interval.StartFrame, interval.EndFrame)).ToArray());
    }

    [Fact]
    public void EarlierFailureLayoutDoesNotChangeLaterRandomFrames()
    {
        var calm = World(("rec-a", 40, Array.Empty<int>(), null), ("rec-b", 40, new[] { 8 }, null));
        var storm = World(("rec-a", 40, Enumerable.Range(0, 21).Concat(new[] { 35, 36, 37, 38, 39 }).ToArray(), null), ("rec-b", 40, new[] { 8 }, null));
        var calmSampling = Generate(calm, randomFrames: 6, difficult: 3, interval: 5, seed: 2026, second: .5);
        var stormSampling = Generate(storm, randomFrames: 6, difficult: 3, interval: 5, seed: 2026, second: .5);
        Assert.Equal(RandomSignature(calmSampling, "rec-a"), RandomSignature(stormSampling, "rec-a"));
        Assert.Equal(CaseSignature(calmSampling, "rec-b"), CaseSignature(stormSampling, "rec-b"));
        Assert.Equal(6d / 40, calmSampling.Cases.First(c => c.VideoId == "rec-b" && c.RandomSample).InclusionProbability);
        Assert.NotEqual(Purposive(calmSampling, "rec-a").Select(r => (r.Start, r.End)), Purposive(stormSampling, "rec-a").Select(r => (r.Start, r.End)));

        var withoutDifficult = Generate(calm, randomFrames: 6, difficult: 0, interval: 5, seed: 2026, second: .5);
        Assert.Equal(RandomSignature(calmSampling, "rec-a"), RandomSignature(withoutDifficult, "rec-a"));
        Assert.Equal(RandomSignature(calmSampling, "rec-b"), RandomSignature(withoutDifficult, "rec-b"));
        Assert.Empty(withoutDifficult.Cases.Where(c => !c.RandomSample));
    }

    [Fact]
    public void SecondReviewAndBothMeasurementTargetsStayOnTheSameBounds()
    {
        var world = World(("target", 20, new[] { 5 }, null));
        var none = Generate(world, randomFrames: 4, difficult: 1, interval: 4, second: 0);
        var all = Generate(world, randomFrames: 4, difficult: 1, interval: 4, second: 1);
        Assert.All(none.Cases, c => Assert.False(c.SecondReviewRequired));
        Assert.All(all.Cases, c => Assert.True(c.SecondReviewRequired));
        var raw = Generate(world, randomFrames: 4, difficult: 1, interval: 4, target: ValidationMeasurementTarget.RawAutomatic);
        var corrected = Generate(world, randomFrames: 4, difficult: 1, interval: 4, target: ValidationMeasurementTarget.InspectedCorrected);
        Assert.NotEqual(raw.PlanFingerprint, corrected.PlanFingerprint);
        Assert.Equal(CaseSignature(raw, "target"), CaseSignature(corrected, "target"));
        Assert.Equal((3, 6), (Purposive(raw, "target")[0].Start, Purposive(raw, "target")[0].End));
    }

    [Fact]
    public void UnanalysedRecordingAndFullPopulationSampleRetainEverySourceFrame()
    {
        var unanalysed = Generate(World(new Spec("unanalysed", 20, Array.Empty<int>(), null, false)), randomFrames: 20, difficult: 2, interval: 4);
        Assert.Empty(unanalysed.Runs);
        AssertPurposive(unanalysed, "unanalysed", 20, (4, 7), (11, 14));
        Assert.Equal(Enumerable.Range(0, 20), unanalysed.Cases.Where(c => c.RandomSample).Select(c => c.StartFrame));
        Assert.All(unanalysed.Cases.Where(c => c.RandomSample), c => Assert.Equal(1d, c.InclusionProbability));
        Assert.All(unanalysed.Cases.Where(c => c.RandomSample), c => Assert.Equal(RandomReason, c.SelectionReason));

        var full = Generate(World(("full", 8, new[] { 2 }, null)), randomFrames: 100, difficult: 1, interval: 1);
        Assert.Equal(Enumerable.Range(0, 8), full.Cases.Where(c => c.RandomSample).Select(c => c.StartFrame));
        Assert.Contains(full.Cases, c => c.RandomSample && c.StartFrame == 2 && c.InclusionProbability == 1d);
        AssertPurposive(full, "full", 8, (2, 2));
    }

    [Fact]
    public void DistributedAnchorCollisionsUseRemainingFeasibleStarts()
    {
        var sampling = Generate(World(("tight", 12, Array.Empty<int>(), null)), randomFrames: 2, difficult: 5, interval: 8);
        AssertPurposive(sampling, "tight", 12, (0, 7), (2, 9), (4, 11), (3, 10), (1, 8));
        var rows = Purposive(sampling, "tight");
        Assert.Contains("rank 1 of 5", rows[0].Reason, StringComparison.Ordinal);
        Assert.Contains("rank 3 of 5", rows[1].Reason, StringComparison.Ordinal);
        Assert.Contains("binary-midpoint", rows[3].Reason, StringComparison.Ordinal);
        Assert.Contains("binary-midpoint", rows[4].Reason, StringComparison.Ordinal);
        Assert.Equal(5, rows.Select(r => (r.Start, r.End)).Distinct().Count());
    }

    [Fact]
    public void SavedSamplingShapeAndReferenceKeyStayOnTheExistingContract()
    {
        const string literal = """
            {
              "plan_fingerprint": "abc",
              "producer_fingerprint": "def",
              "seed": 2026,
              "runs": [],
              "cases": [
                {
                  "video_id": "recording-a",
                  "start_frame": 3,
                  "end_frame": 6,
                  "random_sample": false,
                  "inclusion_probability": null,
                  "population_frames": 20,
                  "second_review_required": false,
                  "selection_reason": "Purposive continuous difficult/distributed interval; inclusion probability is not a random estimate."
                }
              ]
            }
            """;
        var sampling = JsonDefaults.Deserialize<ValidationSampling>(literal);
        var serialized = JsonDefaults.Serialize(sampling);
        Assert.Equal(serialized, JsonDefaults.Serialize(JsonDefaults.Deserialize<ValidationSampling>(serialized)));
        using var document = JsonDocument.Parse(serialized);
        Assert.Equal(new[] { "plan_fingerprint", "producer_fingerprint", "seed", "runs", "cases" }, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        var episode = Assert.Single(sampling.Cases);
        Assert.Equal(new[] { "video_id", "start_frame", "end_frame", "random_sample", "inclusion_probability", "population_frames", "second_review_required", "selection_reason" }, document.RootElement.GetProperty("cases")[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("recording-a", episode.VideoId);
        Assert.Equal(3, episode.StartFrame);
        Assert.Equal(6, episode.EndFrame);
        Assert.False(episode.RandomSample);
        Assert.Null(episode.InclusionProbability);
        Assert.Equal(20, episode.PopulationFrames);
        Assert.False(episode.SecondReviewRequired);
        var hash = IndependentValidationSnapshot.Hash(serialized);
        var reference = new IndependentReference("recording-a", 3, ReferenceReviewerLane.First, "Reviewer", ReferenceVisibility.NotVisible, null, null, ReferenceEvent.None, "Synthetic historical reference", []);
        Assert.Equal(hash + ":recording-a:3:First", IndependentValidationComparison.ReferenceKey(hash, reference));
        Assert.DoesNotContain("episode_index", serialized, StringComparison.Ordinal);
    }

    private const string RandomReason = "Disclosed simple random sample without replacement over full source recording, including unknown/unanalysed frames.";

    private readonly record struct Spec(string Id, int Population, int[] Rejected, Dictionary<int, PointStatus>? Statuses, bool IncludeRun = true);

    private static IndependentValidationSnapshot World(params Spec[] specs)
    {
        var imported = ImmutableArray.CreateBuilder<VideoAsset>();
        var runs = ImmutableArray.CreateBuilder<ValidationFrozenRun>();
        foreach (var spec in specs)
        {
            var asset = new VideoAsset { Id = spec.Id, Path = spec.Id + "-synthetic.avi", SourceSha256 = "synthetic-" + spec.Id, Metadata = new VideoMetadata(32, 32, 10, spec.Population), AnimalId = "synthetic", GroupName = "synthetic" };
            imported.Add(asset);
            if (!spec.IncludeRun) continue;
            var rejected = spec.Rejected.ToHashSet();
            var points = Enumerable.Range(0, spec.Population).Select(frame =>
            {
                if (!rejected.Contains(frame)) return new TrackPoint(frame, frame / 10d, 4, 4, PointStatus.Tracked, 1);
                var status = spec.Statuses is not null && spec.Statuses.TryGetValue(frame, out var explicitStatus) ? explicitStatus : PointStatus.Missing;
                return new TrackPoint(frame, frame / 10d, null, null, status, 0);
            }).ToList();
            var run = new AnalysisRun { Id = "run-" + spec.Id, VideoId = spec.Id, SourceSha256 = asset.SourceSha256, CreatedUtc = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc), Metadata = asset.Metadata, Points = points, NominalTimingVerified = true };
            runs.Add(new(spec.Id, asset.SourceSha256, JsonDefaults.Serialize(asset), JsonDefaults.Serialize(run), [], "raw-" + spec.Id, "corrections-" + spec.Id, AlgorithmIdentity.Contract, "0.1.0-native", "synthetic-comparator", []));
        }
        return new("synthetic-validation", "producer-synthetic", "reference-synthetic", runs.ToImmutable(), imported.ToImmutable(), []);
    }

    private static IndependentValidationSnapshot World(params (string Id, int Population, int[] Rejected, Dictionary<int, PointStatus>? Statuses)[] specs) =>
        World(specs.Select(spec => new Spec(spec.Id, spec.Population, spec.Rejected, spec.Statuses)).ToArray());

    private static ValidationSampling Generate(IndependentValidationSnapshot world, int randomFrames, int difficult, int interval, int seed = 2026, double second = 0, ValidationMeasurementTarget target = ValidationMeasurementTarget.RawAutomatic)
    {
        var plan = new IndependentValidationPlan
        {
            SyntheticToolingOnly = true, MeasurementTarget = target, RandomFramesPerRecording = randomFrames, DifficultIntervalsPerRecording = difficult,
            IntervalFrames = interval, Seed = seed, SecondReviewFraction = second, Tolerances = new(5, 3, .25, .1, .05, .8),
            Recordings = world.Imported.Select(asset => new ValidationRecording(asset.Id, "synthetic-session", ValidationSplit.Development, true, false, "synthetic-protocol", "synthetic")).ToImmutableArray()
        };
        return IndependentValidationSampling.Generate(world, plan);
    }

    private readonly record struct PurposiveRow(int Start, int End, string Reason);

    private static PurposiveRow[] Purposive(ValidationSampling sampling, string id) =>
        sampling.Cases.Where(c => c.VideoId == id && !c.RandomSample).Select(c => new PurposiveRow(c.StartFrame, c.EndFrame, c.SelectionReason)).ToArray();

    private static void AssertPurposive(ValidationSampling sampling, string id, int population, params (int Start, int End)[] expected)
    {
        var rows = sampling.Cases.Where(c => c.VideoId == id && !c.RandomSample).ToArray();
        Assert.Equal(expected, rows.Select(row => (row.StartFrame, row.EndFrame)).ToArray());
        Assert.Equal(expected.Length, rows.Select(row => (row.StartFrame, row.EndFrame)).Distinct().Count());
        Assert.All(rows, row =>
        {
            Assert.False(row.RandomSample);
            Assert.Null(row.InclusionProbability);
            Assert.Equal(population, row.PopulationFrames);
            Assert.Contains("not a random estimate", row.SelectionReason, StringComparison.Ordinal);
            Assert.InRange(row.StartFrame, 0, population - 1);
            Assert.InRange(row.EndFrame, row.StartFrame, population - 1);
        });
    }

    private static string RandomSignature(ValidationSampling sampling, string id) =>
        string.Join("|", sampling.Cases.Where(c => c.VideoId == id && c.RandomSample).OrderBy(c => c.StartFrame).Select(Format));

    private static string CaseSignature(ValidationSampling sampling, string id) =>
        string.Join("|", sampling.Cases.Where(c => c.VideoId == id).Select(Format));

    private static string Format(ValidationSamplingCase row) =>
        string.Join(":", row.VideoId, row.StartFrame, row.EndFrame, row.RandomSample, row.InclusionProbability?.ToString("R", CultureInfo.InvariantCulture) ?? "null", row.PopulationFrames, row.SecondReviewRequired, row.SelectionReason);
}
