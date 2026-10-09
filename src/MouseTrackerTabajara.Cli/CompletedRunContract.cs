using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Cli;

public static class CompletedRunContract
{
    public static readonly string SoftwareVersion = new AnalysisRun().SoftwareVersion;

    public static readonly string[] WithheldBiologicalEndpoints =
    [
        "distance", "mean_speed", "wall_observed_percent", "target_observed_percent",
        "platform_site_seconds", "platform_site_entries", "first_observed_site_visit_seconds",
        "first_visit_lower_seconds", "first_visit_upper_seconds", "mean_proximity_diameters",
        "quadrant_seconds_nw", "quadrant_seconds_ne", "quadrant_seconds_sw", "quadrant_seconds_se"
    ];

    public static (int FirstFrame, int LastExclusive) ExpectedInterval(VideoMetadata metadata, TrackingSettings settings) =>
        CompletedRecordingContract.Interval(metadata, settings);

    public static void Ensure(AnalysisRun run, VideoMetadata metadata, TrackingSettings expectedSettings, string expectedSourceSha256, string expectedAlgorithmContract, string expectedSoftwareVersion)
    {
        if (run.Points is null) throw new InvalidDataException("Analysis has no point list.");
        if (run.Metrics is null) throw new InvalidDataException("Analysis has no measurements.");
        if (!string.Equals(run.SourceSha256, expectedSourceSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Analysis source hash does not match the frozen recording.");
        if (!string.Equals(run.AlgorithmContract, expectedAlgorithmContract, StringComparison.Ordinal))
            throw new InvalidDataException("Analysis algorithm contract does not match the frozen plan.");
        if (!string.Equals(run.SoftwareVersion, expectedSoftwareVersion, StringComparison.Ordinal))
            throw new InvalidDataException("Analysis software version does not match the frozen code identity.");
        if (run.AppliedCorrections is { Count: > 0 })
            throw new InvalidDataException("Analysis has applied corrections. Replay requires the automatic run.");
        if (!SameMetadata(run.Metadata, metadata))
            throw new InvalidDataException("Analysis metadata does not match the probed recording.");
        EnsureSettingsMatch(expectedSettings, run.Settings);
        if (!run.NominalTimingVerified) throw new InvalidDataException("Nominal timing was not verified.");
        _ = ExpectedInterval(metadata, expectedSettings);
        CompletedRecordingContract.Validate(run);
        RequireDerivedEndpoints(run);
    }

    public static void RequireDerivedEndpoints(AnalysisRun run)
    {
        var expected = MetricCalculator.Calculate(run.Points, run.Metadata, run.Settings);
        var actual = run.Metrics;
        RequireClose(actual.Distance, expected.Distance, "distance");
        if (!string.Equals(actual.DistanceUnit, expected.DistanceUnit, StringComparison.Ordinal))
            throw new InvalidDataException("Distance unit does not match the calculated path unit.");
        RequireNullable(actual.MeanSpeed, expected.MeanSpeed, "mean_speed");
        RequireNullable(actual.MeanProximityDiameters, expected.MeanProximityDiameters, "mean_proximity_diameters");
        RequireNullable(actual.WallObservedPercent, expected.WallObservedPercent, "wall_observed_percent");
        RequireNullable(actual.TargetObservedPercent, expected.TargetObservedPercent, "target_observed_percent");
        RequireNullable(actual.PlatformSiteSeconds, expected.PlatformSiteSeconds, "platform_site_seconds");
        if (actual.PlatformSiteEntries != expected.PlatformSiteEntries)
            throw new InvalidDataException("Platform-site entries do not match the calculated path.");
        RequireNullable(actual.FirstObservedSiteVisitSeconds, expected.FirstObservedSiteVisitSeconds, "first_observed_site_visit_seconds");
        RequireNullable(actual.FirstVisitLowerSeconds, expected.FirstVisitLowerSeconds, "first_visit_lower_seconds");
        RequireNullable(actual.FirstVisitUpperSeconds, expected.FirstVisitUpperSeconds, "first_visit_upper_seconds");
        RequireNullable(actual.SiteVisitTime, expected.SiteVisitTime, "site_visit_time");
        if (actual.SiteVisitObserved != expected.SiteVisitObserved || actual.SiteVisitStatus != expected.SiteVisitStatus)
            throw new InvalidDataException("First-visit history does not match the calculated event history.");
        if (!string.Equals(actual.TargetQuadrant, expected.TargetQuadrant, StringComparison.Ordinal))
            throw new InvalidDataException("Target quadrant does not match the calculated occupancy.");
        RequireBounds(actual.TargetFullTrialPercentBounds, expected.TargetFullTrialPercentBounds, "target_full_trial_percent_bounds");
        RequireMap(actual.QuadrantObservedPercent, expected.QuadrantObservedPercent, "quadrant_observed_percent");
        RequireBoundsMap(actual.QuadrantFullTrialPercentBounds, expected.QuadrantFullTrialPercentBounds, "quadrant_full_trial_percent_bounds");
        RequireEligibility(actual.EndpointEligibility, expected.EndpointEligibility);
    }

    public static void EnsureSettingsMatch(TrackingSettings expected, TrackingSettings actual)
    {
        if (actual is null) throw new InvalidDataException("Analysis settings are missing.");
        expected.Validate();
        actual.Validate();
        if (!SamePreserved(expected, actual) || expected.Threshold != actual.Threshold || expected.Invert != actual.Invert || expected.Blur != actual.Blur
            || expected.MinArea != actual.MinArea || expected.MaxArea != actual.MaxArea || expected.MaxExtension != actual.MaxExtension || expected.MaxJump != actual.MaxJump)
            throw new InvalidDataException("Analysis settings do not match the frozen scenario settings.");
    }

    public static bool SamePreserved(TrackingSettings baseline, TrackingSettings applied) =>
        baseline.Cx == applied.Cx && baseline.Cy == applied.Cy && baseline.Rx == applied.Rx && baseline.Ry == applied.Ry
        && baseline.StartSeconds == applied.StartSeconds && baseline.EndSeconds == applied.EndSeconds && baseline.DiameterCm == applied.DiameterCm
        && baseline.WallBoundary == applied.WallBoundary && baseline.ArenaConfirmed == applied.ArenaConfirmed
        && SamePlatform(baseline.Platform, applied.Platform) && SameExclusions(baseline.Exclusions, applied.Exclusions);

    public static bool SameMetadata(VideoMetadata actual, VideoMetadata expected) =>
        actual is not null && actual.Width == expected.Width && actual.Height == expected.Height && actual.FrameCount == expected.FrameCount && actual.Fps.Equals(expected.Fps);

    private static bool SamePlatform(PlatformSite? left, PlatformSite? right) =>
        left is null ? right is null : right is not null && left.X == right.X && left.Y == right.Y && left.Radius == right.Radius;

    private static bool SameExclusions(List<ExclusionRegion>? left, List<ExclusionRegion>? right)
    {
        var a = left ?? [];
        var b = right ?? [];
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Left != b[i].Left || a[i].Top != b[i].Top || a[i].Right != b[i].Right || a[i].Bottom != b[i].Bottom) return false;
        return true;
    }

    private static void RequireClose(double actual, double expected, string name)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-9)
            throw new InvalidDataException($"Calculated {name} does not match the analysis.");
    }

    private static void RequireNullable(double? actual, double? expected, string name)
    {
        if (actual.HasValue != expected.HasValue || (actual.HasValue && (!double.IsFinite(actual.Value) || Math.Abs(actual.Value - expected!.Value) > 1e-9)))
            throw new InvalidDataException($"Calculated {name} does not match the analysis.");
    }

    private static void RequireBounds(PercentBounds? actual, PercentBounds? expected, string name)
    {
        if (actual is null != expected is null || (actual is not null && expected is not null && (Math.Abs(actual.Lower - expected.Lower) > 1e-9 || Math.Abs(actual.Upper - expected.Upper) > 1e-9)))
            throw new InvalidDataException($"Calculated {name} does not match the analysis.");
    }

    private static void RequireMap(Dictionary<string, double?>? actual, Dictionary<string, double?>? expected, string name)
    {
        actual ??= [];
        expected ??= [];
        if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var value) || value.HasValue != pair.Value.HasValue || (value.HasValue && Math.Abs(value.Value - pair.Value!.Value) > 1e-9)))
            throw new InvalidDataException($"Calculated {name} does not match the analysis.");
    }

    private static void RequireBoundsMap(Dictionary<string, PercentBounds>? actual, Dictionary<string, PercentBounds>? expected, string name)
    {
        actual ??= [];
        expected ??= [];
        if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var value) || Math.Abs(value.Lower - pair.Value.Lower) > 1e-9 || Math.Abs(value.Upper - pair.Value.Upper) > 1e-9))
            throw new InvalidDataException($"Calculated {name} does not match the analysis.");
    }

    private static void RequireEligibility(Dictionary<string, EndpointEligibility>? actual, Dictionary<string, EndpointEligibility>? expected)
    {
        actual ??= [];
        expected ??= [];
        if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var value) || value.Eligible != pair.Value.Eligible || !string.Equals(value.Reason, pair.Value.Reason, StringComparison.Ordinal)))
            throw new InvalidDataException("Endpoint eligibility does not match the calculated first-visit and path history.");
    }
}
