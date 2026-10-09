namespace MouseTrackerTabajara.Core;

/// <summary>Inspection heuristics, not confidence estimates or biological event confirmations.</summary>
public static class ConsequenceReviewQueue
{
    public static List<ReviewMoment> Build(AnalysisRun run)
    {
        var points = run.Points;
        if (points.Count == 0) return [];
        run.Settings.Validate();
        var meta = run.Metadata;
        if (!double.IsFinite(meta.Fps) || meta.Fps <= 0 || meta.Width <= 0 || meta.Height <= 0)
            throw new ArgumentException("Invalid review timing or geometry.");
        for (var i = 0; i < points.Count; i++)
            if (points[i].Frame < 0 || points[i].Frame >= meta.FrameCount ||
                (i > 0 && points[i].Frame != points[i - 1].Frame + 1))
                throw new ArgumentException("Review requires canonical consecutive source frames with explicit unknown positions.");
        var half = (int)Math.Clamp(Math.Ceiling(meta.Fps), 1, int.MaxValue / 4);
        var items = new List<(int Rank, ReviewMoment Moment)>();
        void Add(long start, long end, int rank, string reason) => items.Add((rank,
            new((int)Math.Max(points[0].Frame, start), (int)Math.Min(points[^1].Frame, end), reason, false)));
        bool Inside(TrackPoint p)
        {
            if (!p.Accepted || run.Settings.Platform is not { } site) return false;
            var dx = (p.X!.Value - site.X * meta.Width) / (run.Settings.Rx * meta.Width);
            var dy = (p.Y!.Value - site.Y * meta.Height) / (run.Settings.Ry * meta.Height);
            var radius = site.Radius / run.Settings.Rx;
            return dx * dx + dy * dy <= radius * radius;
        }
        var firstVisit = points.FindIndex(Inside);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!p.Accepted)
            {
                var start = i;
                while (i + 1 < points.Count && !points[i + 1].Accepted) i++;
                var critical = run.Settings.Platform is not null && (firstVisit < 0 || start < firstVisit);
                Add((long)points[start].Frame - half, (long)points[i].Frame + half, critical ? 0 : 3,
                    critical ? "Unknown or ambiguous history before first observed site visit; inspect possible earlier visit and event timing."
                        : "Unknown or ambiguous position interval; inspect occupancy and movement consequences.");
                if (points.Skip(start).Take(i - start + 1).Any(q => q.Status == PointStatus.Jump))
                    Add((long)points[start].Frame - half, (long)points[i].Frame + half, 2, "Rejected jump candidate; inspect movement and position continuity.");
                continue;
            }
            if (i == 0 || !points[i - 1].Accepted) continue;
            var prior = points[i - 1];
            if (run.Settings.Platform is not null && Inside(prior) != Inside(p))
                Add((long)prior.Frame - half, (long)p.Frame + half, 1, "Platform-site boundary crossing; inspect visit timing and site-entry count (mounting is unconfirmed).");
            var dx = (double)p.X!.Value - prior.X!.Value;
            var dy = (double)p.Y!.Value - prior.Y!.Value;
            if ((double)dx * dx + (double)dy * dy > run.Settings.MaxJump * run.Settings.MaxJump)
                Add((long)prior.Frame - half, (long)p.Frame + half, 2, "Accepted position jump; inspect movement and distance consequences.");
            if (prior.Area is > 0 && p.Area is > 0 && double.IsFinite(prior.Area.Value) && double.IsFinite(p.Area.Value) &&
                Math.Max(prior.Area.Value, p.Area.Value) / Math.Min(prior.Area.Value, p.Area.Value) >= 2)
                Add((long)prior.Frame - half, (long)p.Frame + half, 4, "Contour area changed at least twofold; inspect accepted candidate identity and position.");
        }
        // Exact-coordinate persistence for at least two seconds is only an inspection cue.
        for (var i = 0; i < points.Count; i++)
        {
            if (!points[i].Accepted) continue;
            var start = i;
            while (i + 1 < points.Count && points[i + 1].Accepted && points[i + 1].X == points[start].X && points[i + 1].Y == points[start].Y) i++;
            if (i - start + 1 >= 2L * half)
                Add(points[start].Frame, points[i].Frame, 5, "Persistent stationary accepted candidate; inspect identity and immobility (artifact is not established).");
        }
        var result = items.GroupBy(x => (x.Moment.StartFrame, x.Moment.EndFrame))
            .Select(g => (Rank: g.Min(x => x.Rank), Moment: g.First().Moment with
                { Reason = string.Join(" ", g.OrderBy(x => x.Rank).Select(x => x.Moment.Reason).Distinct()) }))
            .OrderBy(x => x.Rank).ThenBy(x => x.Moment.StartFrame).ThenBy(x => x.Moment.EndFrame)
            .Select(x => x.Moment).ToList();
        // Tile accepted runs only. Each audit is continuous, disjoint and at most two seconds plus one frame.
        var audits = new List<ReviewMoment>();
        for (var i = 0; i < points.Count; i++)
        {
            if (!points[i].Accepted) continue;
            var start = i;
            while (i + 1 < points.Count && points[i + 1].Accepted && i - start + 1 < 2L * half + 1) i++;
            audits.Add(new(points[start].Frame, points[i].Frame, "Random audit of continuous accepted positions; inspect apparently good tracking.", true));
        }
        // Seed excludes identifiers, timestamps, treatment, filenames and user-supplied source labels.
        uint seed = 2166136261;
        unchecked { foreach (var p in points) { seed = (seed ^ (uint)p.Frame) * 16777619; seed = (seed ^ (uint)(p.X ?? -1)) * 16777619; seed = (seed ^ (uint)(p.Y ?? -1)) * 16777619; seed = (seed ^ (uint)p.Status) * 16777619; } }
        for (var i = audits.Count - 1; i > 0; i--)
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            var j = (int)(seed % (uint)(i + 1));
            (audits[i], audits[j]) = (audits[j], audits[i]);
        }
        result.AddRange(audits.Take(5));
        return result;
    }
}
