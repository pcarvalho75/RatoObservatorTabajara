using System.Collections.Immutable;

namespace MouseTrackerTabajara.Application;

/// <summary>
/// Difficult intervals are purposive. Contiguous rejected frame IDs (Accepted == false),
/// sorted and de-duplicated, form episodes in ascending source order. Frames outside
/// [0, population) are ignored. The representative frame is the lower median rejected
/// frame, episodeFirst + (episodeLast - episodeFirst) / 2. The interval is centered with
/// integer half-width and clamped to a full interval, or to the whole source when the
/// population is shorter than the interval. Exact bounds are never repeated. Overlapping
/// distinct bounds are allowed. When episodes exceed the budget, attempt order is the
/// inclusive spread i * (episodeCount - 1) / (budget - 1), or the lower-median episode
/// when the budget is 1, then any unselected episodes. Remaining slots use anchors
/// rank * population / (budget + 1), 1-based rank, then a binary-midpoint scan of feasible
/// starts. A short source yields one interval and does not invent copies. Selection does
/// not use the random-frame generator. Inclusion probability stays null; frame counts are
/// not animals and these intervals are not an accuracy estimate.
/// </summary>
public static class FailureEpisodeSampling
{
    public readonly record struct PurposiveInterval(int StartFrame, int EndFrame, string SelectionReason);

    public static int SecondReviewSeed(int planSeed) => unchecked(planSeed * 1664525 + 1013904223);

    public static ImmutableArray<PurposiveInterval> Select(int populationFrames, int intervalFrames, int budget, IReadOnlyList<int> rejectedFrameIds)
    {
        if (populationFrames <= 0 || intervalFrames <= 0 || budget <= 0) return [];
        var episodes = Cluster(rejectedFrameIds, populationFrames);
        var feasible = populationFrames < intervalFrames ? 1 : populationFrames - intervalFrames + 1;
        var target = Math.Min(budget, feasible);
        var chosen = new List<PurposiveInterval>(target);
        var used = new HashSet<(int Start, int End)>();
        var shortSource = populationFrames < intervalFrames;

        void Add(int start, int end, string reason)
        {
            if (chosen.Count >= target || !used.Add((start, end))) return;
            if (shortSource) reason += $" Population {populationFrames} is shorter than interval length {intervalFrames}; only one distinct interval exists and it is not duplicated to fill the budget.";
            chosen.Add(new(start, end, reason));
        }

        var primary = SpreadIndexes(episodes.Length, budget);
        var primarySet = primary.ToHashSet();
        var attempt = new List<int>(primary);
        if (episodes.Length > budget)
            for (var index = 0; index < episodes.Length; index++)
                if (!primarySet.Contains(index)) attempt.Add(index);
        foreach (var index in attempt)
        {
            if (chosen.Count >= target) break;
            var episode = episodes[index];
            var anchor = episode.First + ((episode.Last - episode.First) / 2);
            var (start, end) = Place(populationFrames, intervalFrames, anchor);
            var reason = episodes.Length > budget && !primarySet.Contains(index)
                ? $"Purposive failure-episode replacement; source episode index {index} of {episodes.Length} replaces a duplicate interval after the spread; episode frames {episode.First}-{episode.Last}; lower-median rejected frame {anchor}; inclusion probability is not a random estimate."
                : $"Purposive failure-episode representative; source episode index {index} of {episodes.Length}; episode frames {episode.First}-{episode.Last}; lower-median rejected frame {anchor}; contiguous rejected frames in ascending order; inclusion probability is not a random estimate.";
            Add(start, end, reason);
        }
        if (chosen.Count < target)
        {
            for (var rank = 1; rank <= budget && chosen.Count < target; rank++)
            {
                var anchor = (int)((long)rank * populationFrames / (budget + 1));
                var (start, end) = Place(populationFrames, intervalFrames, anchor);
                var reason = episodes.Length == 0
                    ? $"Purposive deterministic distributed interval; 1-based rank {rank} of {budget}; anchor frame {anchor} = rank*population/(budget+1); full source, not a successful-track-only sample; inclusion probability is not a random estimate."
                    : $"Purposive deterministic distributed fill; 1-based rank {rank} of {budget}; anchor frame {anchor} = rank*population/(budget+1); fills a remaining distinct slot after episode intervals; inclusion probability is not a random estimate.";
                Add(start, end, reason);
            }
        }
        if (chosen.Count < target)
        {
            foreach (var start in BinaryStartOrder(feasible))
            {
                if (chosen.Count >= target) break;
                var end = shortSource ? populationFrames - 1 : start + intervalFrames - 1;
                Add(start, end, $"Purposive feasible-start replacement; source start frame {start}; binary-midpoint scan after distributed anchors duplicated or were already used; inclusion probability is not a random estimate.");
            }
        }
        return chosen.ToImmutableArray();
    }

    private readonly record struct Episode(int First, int Last);

    private static Episode[] Cluster(IReadOnlyList<int> rejectedFrameIds, int populationFrames)
    {
        var frames = rejectedFrameIds.Where(frame => frame >= 0 && frame < populationFrames).Distinct().OrderBy(frame => frame).ToArray();
        if (frames.Length == 0) return [];
        var episodes = new List<Episode>();
        var first = frames[0];
        var last = frames[0];
        for (var i = 1; i < frames.Length; i++)
        {
            if (frames[i] == last + 1) { last = frames[i]; continue; }
            episodes.Add(new(first, last));
            first = last = frames[i];
        }
        episodes.Add(new(first, last));
        return episodes.ToArray();
    }

    private static int[] SpreadIndexes(int episodeCount, int budget)
    {
        if (episodeCount <= 0) return [];
        if (episodeCount <= budget) return Enumerable.Range(0, episodeCount).ToArray();
        var indexes = new int[budget];
        if (budget == 1) { indexes[0] = (episodeCount - 1) / 2; return indexes; }
        var last = episodeCount - 1;
        for (var i = 0; i < budget; i++) indexes[i] = (int)((long)i * last / (budget - 1));
        return indexes;
    }

    private static (int Start, int End) Place(int populationFrames, int intervalFrames, int anchor)
    {
        if (populationFrames < intervalFrames) return (0, populationFrames - 1);
        var first = Math.Clamp(anchor - (intervalFrames / 2), 0, populationFrames - intervalFrames);
        return (first, first + intervalFrames - 1);
    }

    private static IEnumerable<int> BinaryStartOrder(int count)
    {
        if (count <= 0) yield break;
        var queue = new Queue<(int Lo, int Hi)>();
        queue.Enqueue((0, count - 1));
        while (queue.Count > 0)
        {
            var (lo, hi) = queue.Dequeue();
            if (lo > hi) continue;
            var mid = lo + ((hi - lo) / 2);
            // Each child range excludes its parent's midpoint and is disjoint
            // from its sibling; starts are unique without a population-sized bitmap.
            yield return mid;
            if (lo < mid) queue.Enqueue((lo, mid - 1));
            if (mid < hi) queue.Enqueue((mid + 1, hi));
        }
    }
}
