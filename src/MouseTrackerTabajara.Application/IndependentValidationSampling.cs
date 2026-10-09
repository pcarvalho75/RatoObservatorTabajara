using System.Collections.Immutable;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Application;

public static class IndependentValidationSampling
{
    public static ValidationSampling Generate(IndependentValidationSnapshot snapshot, IndependentValidationPlan plan)
    {
        snapshot.ValidatePlan(plan);
        var random = new Random(plan.Seed);
        // Episode cardinality must not move later recordings' simple-random frames.
        // Purposive second review uses another seed and always draws one value per requested slot.
        var purposiveReview = new Random(FailureEpisodeSampling.SecondReviewSeed(plan.Seed));
        var cases = ImmutableArray.CreateBuilder<ValidationSamplingCase>();
        foreach (var declaration in plan.Recordings.OrderBy(r => r.VideoId, StringComparer.Ordinal))
        {
            var asset = snapshot.Imported.SingleOrDefault(a => a.Id == declaration.VideoId) ?? throw new ArgumentException("Sampling recording is not imported.");
            var population = asset.Metadata.FrameCount;
            var count = Math.Min(plan.RandomFramesPerRecording, population);
            var reservoir = Enumerable.Range(0, count).ToArray();
            for (var i = count; i < population; i++) { var selected = random.Next(i + 1); if (selected < count) reservoir[selected] = i; }
            foreach (var frame in reservoir.OrderBy(f => f)) cases.Add(new(asset.Id, frame, frame, true, (double)count / population, population, random.NextDouble() < plan.SecondReviewFraction, "Disclosed simple random sample without replacement over full source recording, including unknown/unanalysed frames."));
            var raw = snapshot.Runs.SingleOrDefault(r => r.VideoId == asset.Id)?.RawRun;
            var rejected = raw?.Points.Where(point => !point.Accepted).Select(point => point.Frame).ToArray() ?? [];
            var intervals = FailureEpisodeSampling.Select(population, plan.IntervalFrames, plan.DifficultIntervalsPerRecording, rejected);
            for (var slot = 0; slot < plan.DifficultIntervalsPerRecording; slot++)
            {
                var secondReview = purposiveReview.NextDouble() < plan.SecondReviewFraction;
                if (slot < intervals.Length) cases.Add(new(asset.Id, intervals[slot].StartFrame, intervals[slot].EndFrame, false, null, population, secondReview, intervals[slot].SelectionReason));
            }
        }
        var frozen = snapshot.Runs.Where(r => plan.Recordings.Any(p => p.VideoId == r.VideoId)).ToImmutableArray();
        return new(IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(plan)), snapshot.ProducerFingerprint, plan.Seed, frozen, cases.ToImmutable());
    }
}
