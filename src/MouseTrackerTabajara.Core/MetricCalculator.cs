// Measurement port of the GPL MorrisLab Python 0.1.1 reference; see source provenance.
namespace MouseTrackerTabajara.Core;

public static class MetricCalculator
{
    public static Measurements Calculate(IReadOnlyList<TrackPoint> points, VideoMetadata meta, TrackingSettings s)
    {
        s.Validate();
        if (!double.IsFinite(meta.Fps) || meta.Fps <= 0 || meta.Width <= 0 || meta.Height <= 0) throw new ArgumentException("Invalid video timing or geometry.");
        if (points.Count == 0) throw new ArgumentException("The trial contains no frames.");
        for (var i = 1; i < points.Count; i++) if (points[i].Frame != points[i - 1].Frame + 1) throw new ArgumentException("Frames must be consecutive; represent gaps explicitly.");
        var (cx, cy, rx, ry) = (s.Cx * meta.Width, s.Cy * meta.Height, s.Rx * meta.Width, s.Ry * meta.Height);
        var qs = new Dictionary<string, int> { ["NW"] = 0, ["NE"] = 0, ["SW"] = 0, ["SE"] = 0 };
        var counts = new Dictionary<string, int>();
        var tracked = 0; var wall = 0; var adjacent = 0; var run = 0; var longest = 0; var siteFrames = 0; var entries = 0;
        double distance = 0, proximity = 0;
        double? firstEntry = null, earliestUnknown = null;
        var unknownBeforeVisit = false;
        TrackPoint? prior = null;
        bool? priorInside = null;
        foreach (var point in points)
        {
            var status = point.Status.ToString().ToLowerInvariant();
            counts[status] = counts.GetValueOrDefault(status) + 1;
            if (!point.Accepted)
            {
                earliestUnknown ??= (point.Frame - points[0].Frame) / meta.Fps;
                if (firstEntry is null) unknownBeforeVisit = true;
                longest = Math.Max(longest, ++run); prior = null; priorInside = null; continue;
            }
            var x = point.X!.Value; var y = point.Y!.Value;
            tracked++; run = 0;
            var (u, v) = ((x - cx) / rx, (y - cy) / ry);
            qs[(v < 0 ? "N" : "S") + (u < 0 ? "W" : "E")]++;
            if (Hypot(u, v) >= s.WallBoundary) wall++;
            if (prior is not null && point.Frame == prior.Frame + 1)
            {
                distance += Hypot((x - prior.X!.Value) / (2 * rx), (y - prior.Y!.Value) / (2 * ry)); adjacent++;
            }
            prior = point;
            if (s.Platform is { } p)
            {
                proximity += Hypot((x - p.X * meta.Width) / (2 * rx), (y - p.Y * meta.Height) / (2 * ry));
                var inside = ArenaGeometry.InsidePlatformSite(x, y, s, meta.Width, meta.Height);
                if (inside)
                {
                    siteFrames++;
                    firstEntry ??= (point.Frame - points[0].Frame) / meta.Fps;
                    if (priorInside is false) entries++;
                }
                priorInside = inside;
            }
        }
        var duration = points.Count / meta.Fps; var trackedSeconds = tracked / meta.Fps; var unknown = (points.Count - tracked) / meta.Fps;
        var target = s.Platform is { } site ? (site.Y * meta.Height < cy ? "N" : "S") + (site.X * meta.Width < cx ? "W" : "E") : null;
        EventHistory history; double? eventTime = null; bool? observed = null;
        if (s.Platform is null) history = EventHistory.NotConfigured;
        else if (firstEntry.HasValue && !unknownBeforeVisit) { history = EventHistory.ObservedExact; eventTime = firstEntry; observed = true; }
        else if (firstEntry.HasValue) history = EventHistory.EarlierVisitPossibleInGap;
        else if (unknown > 0) history = EventHistory.VisitHistoryIndeterminate;
        else { history = EventHistory.RightCensoredComplete; eventTime = duration; observed = false; }
        var complete = tracked > 0 && unknown == 0;
        var bounds = qs.ToDictionary(k => k.Key, k => new PercentBounds(100 * (k.Value / meta.Fps) / duration, 100 * (k.Value / meta.Fps + unknown) / duration));
        var eligible = new Dictionary<string, EndpointEligibility>();
        foreach (var key in new[] { "target_observed_percent", "wall_observed_percent", "distance", "mean_speed", "mean_proximity_diameters", "platform_site_entries" })
        {
            var needsSite = key is "target_observed_percent" or "mean_proximity_diameters" or "platform_site_entries";
            var good = complete && (!needsSite || s.Platform is not null) && (key != "mean_speed" || adjacent > 0);
            eligible[key] = new(good, good ? "Complete observation; position accuracy still requires review." : needsSite && s.Platform is null ? "Platform site not configured." : "Incomplete observation; descriptive values and occupancy bounds remain available.");
        }
        var eventGood = history is EventHistory.ObservedExact or EventHistory.RightCensoredComplete;
        eligible["first_site_visit_seconds"] = new(eventGood, eventGood ? "Event history observed; site entry is not confirmed platform mounting." : history.ToString());
        var scale = s.DiameterCm ?? 1;
        return new Measurements
        {
            TotalFrames = points.Count, TrackedFrames = tracked, CoveragePercent = 100d * tracked / points.Count,
            DurationSeconds = duration, TrackedSeconds = trackedSeconds, UnknownSeconds = unknown,
            LongestGapSeconds = longest / meta.Fps, Distance = distance * scale,
            DistanceUnit = s.DiameterCm.HasValue ? "cm" : "pool diameters",
            AdjacentMotionSeconds = adjacent / meta.Fps, MeanSpeed = adjacent > 0 ? distance * scale / (adjacent / meta.Fps) : null,
            QuadrantSeconds = qs.ToDictionary(k => k.Key, k => k.Value / meta.Fps),
            QuadrantObservedPercent = qs.ToDictionary(k => k.Key, k => tracked > 0 ? (double?)(100d * k.Value / tracked) : null),
            QuadrantFullTrialPercentBounds = bounds, WallObservedPercent = tracked > 0 ? 100d * wall / tracked : null,
            TargetQuadrant = target, TargetObservedPercent = target is not null && tracked > 0 ? 100d * qs[target] / tracked : null,
            TargetFullTrialPercentBounds = target is not null ? bounds[target] : null,
            PlatformSiteSeconds = s.Platform is not null ? siteFrames / meta.Fps : null,
            PlatformSiteEntries = s.Platform is not null ? entries : null,
            FirstObservedSiteVisitSeconds = firstEntry,
            FirstVisitLowerSeconds = unknownBeforeVisit && s.Platform is not null ? earliestUnknown : firstEntry,
            FirstVisitUpperSeconds = firstEntry, SiteVisitTime = eventTime, SiteVisitObserved = observed, SiteVisitStatus = history,
            MeanProximityDiameters = s.Platform is not null && tracked > 0 ? proximity / tracked : null,
            EndpointEligibility = eligible, StatusCounts = counts
        };
    }

    public static List<TrackPoint> ApplyCorrections(AnalysisRun run, IEnumerable<ManualCorrection> corrections)
    {
        var points = run.Points.ToList();
        foreach (var c in corrections.OrderBy(c => c.CreatedUtc).ThenBy(c => c.Frame))
        {
            if (c.RunId != run.Id) throw new ArgumentException("Correction is outside this run.");
            if (string.IsNullOrWhiteSpace(c.Reason)) throw new ArgumentException("A correction reason is required.");
            var index = FrameIdentity.IndexOfExactFrame(points, c.Frame);
            if (c.X.HasValue != c.Y.HasValue) throw new ArgumentException("Supply both coordinates or neither.");
            if (c.X is { } x && c.Y is { } y)
            {
                var s = run.Settings; var m = run.Metadata;
                if (x < 0 || y < 0 || x >= m.Width || y >= m.Height) throw new ArgumentException("Corrected point is outside the source image.");
                if (Hypot((x - s.Cx * m.Width) / (s.Rx * m.Width), (y - s.Cy * m.Height) / (s.Ry * m.Height)) > 1.01)
                    throw new ArgumentException("Corrected point is outside the pool.");
                points[index] = points[index] with { X = x, Y = y, Status = PointStatus.Manual };
            }
            else points[index] = points[index] with { X = null, Y = null, Status = PointStatus.Excluded };
        }
        return points;
    }

    public static List<ReviewMoment> ReviewQueue(AnalysisRun run)
    {
        return ConsequenceReviewQueue.Build(run);
    }
    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
}
