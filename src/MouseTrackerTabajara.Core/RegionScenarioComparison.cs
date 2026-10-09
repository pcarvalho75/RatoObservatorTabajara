using System.Globalization;

namespace MouseTrackerTabajara.Core;

public sealed record ReleasedComparisonWindow
{
    public bool Supported { get; init; }
    public string Hold { get; init; } = "";
    public int ReleaseFrame { get; init; }
    public double OriginSeconds { get; init; }
    public double PlannedSeconds { get; init; }
    public double RequestedExposureSeconds { get; init; }
    public double EffectiveExposureSeconds { get; init; }
    public string TerminationReason { get; init; } = "";
    public string ExposureNote { get; init; } = "";

    public static ReleasedComparisonWindow Resolve(ProtocolWindow? protocol, PlannedContext? context, VideoMetadata metadata,
        IReadOnlyList<BiologicalEvent> effectiveEvents, TrialMode currentPhase, bool currentInspection)
    {
        if (protocol is null || context is null)
            return Unsupported("Confirmed release-relative comparison is held because the protocol or planned context is missing.");
        if (effectiveEvents is null)
            return Unsupported("Effective water-entry, mounting, and intervention events are required. Confirmation flags alone do not open a comparison window.");
        if (!double.IsFinite(metadata.Fps) || metadata.Fps <= 0 || metadata.FrameCount <= 0)
            return Unsupported("Source timing is invalid.");
        if (protocol.Phase != context.Phase || Math.Abs(protocol.RequestedExposureSeconds - context.RequestedExposureSeconds) > 1e-9)
            return Unsupported("Stored protocol phase or requested exposure differs from the planned context.");
        if (!context.WindowConfirmed)
            return Unsupported("The planned comparison window is unconfirmed.");
        if (context.ComparisonDurationSeconds <= 0 || context.ComparisonStartSeconds < 0
            || context.ComparisonStartSeconds + context.ComparisonDurationSeconds > context.RequestedExposureSeconds + 1e-9)
            return Unsupported("The planned comparison interval does not lie within the stated planned exposure.");
        ProtocolOutcome outcome;
        try { outcome = ProtocolEvaluator.Evaluate(protocol, metadata, currentPhase, effectiveEvents); }
        catch (ArgumentException ex) { return Unsupported(ex.Message); }
        var reasons = new List<string>();
        if (!currentInspection) reasons.Add("Current frozen inspection is missing, invalidated, or stale.");
        reasons.AddRange(outcome.Holds);
        var knownActual = outcome.ActualExposureSeconds is double value && double.IsFinite(value) && value > 0 ? value : (double?)null;
        if (reasons.Count > 0 || knownActual is null)
            return FinishHold(context, outcome, reasons.Count == 0 ? "Effective exposure is withheld." : string.Join(" ", reasons));
        if (protocol.ReleaseFrame is not int release)
            return FinishHold(context, outcome, "Effective exposure has no confirmed release frame.");
        var origin = release / metadata.Fps + context.ComparisonStartSeconds;
        if (!double.IsFinite(origin) || origin < -1e-9)
            return FinishHold(context, outcome, "Comparison origin is not a finite time at or after the source start.");
        var actual = knownActual.Value;
        var planned = context.ComparisonDurationSeconds;
        var note = "";
        // Decoder frames after ProtocolEvaluator termination stay in the raw run. They are not training exposure.
        if (context.ComparisonStartSeconds > actual + 1e-9)
            return FinishHold(context, outcome, "The planned comparison starts after effective exposure ended (" + outcome.TerminationReason + "). " + RegionScenarioWording.EffectiveExposureLimit);
        if (context.ComparisonStartSeconds + planned > actual + 1e-9)
        {
            planned = actual - context.ComparisonStartSeconds;
            if (planned <= 1e-9)
                return FinishHold(context, outcome, "No effective exposure remains inside the planned comparison (" + outcome.TerminationReason + "). " + RegionScenarioWording.EffectiveExposureLimit);
            note = string.Create(CultureInfo.InvariantCulture,
                $"Effective exposure is {actual:0.000} s ({outcome.TerminationReason}). The prespecified comparison extended past termination and the denominator is only that effective exposure. {RegionScenarioWording.EffectiveExposureLimit}");
        }
        return new()
        {
            Supported = true, ReleaseFrame = release, OriginSeconds = origin, PlannedSeconds = planned,
            RequestedExposureSeconds = context.RequestedExposureSeconds, EffectiveExposureSeconds = actual,
            TerminationReason = outcome.TerminationReason, ExposureNote = note
        };
    }

    private static ReleasedComparisonWindow FinishHold(PlannedContext context, ProtocolOutcome outcome, string reason) => Unsupported(reason) with
    {
        RequestedExposureSeconds = context.RequestedExposureSeconds,
        EffectiveExposureSeconds = outcome.ActualExposureSeconds is double value && double.IsFinite(value) ? value : 0,
        TerminationReason = outcome.TerminationReason
    };

    public static ReleasedComparisonWindow Unsupported(string reason) => new()
    {
        Supported = false,
        Hold = reason + " " + RegionScenarioWording.ComparisonSubstitutionHold
    };
}

public sealed record WindowedTrialMeasurement
{
    public string Name { get; init; } = "";
    public string Scope { get; init; } = "";
    public bool Supported { get; init; }
    public string Hold { get; init; } = "";
    public double PlannedSeconds { get; init; }
    public double ObservedSeconds { get; init; }
    public double UnknownSeconds { get; init; }
    public bool FractionalBoundary { get; init; }
    public string DistanceNote { get; init; } = "";
    public double RequestedExposureSeconds { get; init; }
    public double EffectiveExposureSeconds { get; init; }
    public string TerminationReason { get; init; } = "";
    public TrialEndpointSet? FullRun { get; init; }
    public TrialEndpointSet? Comparison { get; init; }

    public static WindowedTrialMeasurement Held(string name, string reason) => new()
    {
        Name = name, Supported = false, Scope = RegionScenarioWording.FullDecoderScope,
        Hold = reason.Contains(RegionScenarioWording.ComparisonSubstitutionHold, StringComparison.Ordinal)
            ? reason : reason + " " + RegionScenarioWording.ComparisonSubstitutionHold
    };
}

public static class RegionScenarioComparison
{
    public static WindowedTrialMeasurement Measure(IReadOnlyList<TrackPoint> points, VideoMetadata metadata, TrackingSettings settings, ReleasedComparisonWindow window, string name)
    {
        ArgumentNullException.ThrowIfNull(points); ArgumentNullException.ThrowIfNull(metadata); ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(window);
        settings.Validate();
        TrialEndpointSet? full = points.Count == 0 ? null : TrialEndpointSet.From(MetricCalculator.Calculate(points, metadata, settings));
        if (!window.Supported) return Separated(name, window, full);
        var index = IndexFrames(points);
        var samples = points.Select(point => new WindowSample(point.Frame, point.X, point.Y, point.Accepted && point.X is not null && point.Y is not null)).ToArray();
        bool Inside(WindowSample sample) => ArenaGeometry.InsidePlatformSite(sample.X!.Value, sample.Y!.Value, settings, metadata.Width, metadata.Height);
        var cx = settings.Cx * metadata.Width; var cy = settings.Cy * metadata.Height;
        string Quadrant(WindowSample sample)
        {
            var u = sample.X!.Value - cx; var v = sample.Y!.Value - cy;
            return (v < 0 ? "N" : "S") + (u < 0 ? "W" : "E");
        }
        string? target = null;
        if (settings.Platform is { } site) target = (site.Y * metadata.Height < cy ? "N" : "S") + (site.X * metadata.Width < cx ? "W" : "E");
        TrialEndpointSet? Exact(IReadOnlyList<WindowSample> slice)
        {
            var frames = new TrackPoint[slice.Count];
            for (var i = 0; i < slice.Count; i++)
            {
                var sample = slice[i];
                if (!index.TryGetValue(sample.Frame, out var point))
                    throw new InvalidOperationException("Sliced frame " + sample.Frame.ToString(CultureInfo.InvariantCulture) + " is missing from the frozen trajectory.");
                var accepted = point.Accepted && point.X is not null && point.Y is not null;
                if (point.X != sample.X || point.Y != sample.Y || accepted != sample.Accepted)
                    throw new InvalidOperationException("Indexed frame " + sample.Frame.ToString(CultureInfo.InvariantCulture) + " does not match the sliced sample.");
                frames[i] = point;
            }
            return TrialEndpointSet.From(MetricCalculator.Calculate(frames, metadata, settings));
        }
        return Overlap(samples, metadata.Fps, window, name, full, settings.Platform is not null, Inside, Quadrant, target, Exact);
    }

    public static WindowedTrialMeasurement MeasurePlane(IReadOnlyList<PlaneSample> samples, double fps, ReleasedComparisonWindow window, string name, string unit,
        double? poolCenterX, double? poolCenterY, double? platformX, double? platformY, double? platformRadius, TrialEndpointSet? fullRun)
    {
        ArgumentNullException.ThrowIfNull(samples); ArgumentNullException.ThrowIfNull(window);
        if (!window.Supported) return Separated(name, window, fullRun);
        var rows = samples.Select(sample => new WindowSample(sample.Frame, sample.X, sample.Y, sample.Accepted && sample.X is not null && sample.Y is not null)).ToArray();
        var site = platformX is not null && platformY is not null && platformRadius is > 0;
        bool Inside(WindowSample sample) => Math.Sqrt(Math.Pow(sample.X!.Value - platformX!.Value, 2) + Math.Pow(sample.Y!.Value - platformY!.Value, 2)) <= platformRadius!.Value;
        string Quadrant(WindowSample sample)
        {
            var u = sample.X!.Value - poolCenterX!.Value; var v = sample.Y!.Value - poolCenterY!.Value;
            return (v < 0 ? "N" : "S") + (u < 0 ? "W" : "E");
        }
        var quadrants = poolCenterX is not null && poolCenterY is not null;
        string? target = site && quadrants ? (platformY!.Value < poolCenterY!.Value ? "N" : "S") + (platformX!.Value < poolCenterX!.Value ? "W" : "E") : null;
        TrialEndpointSet? Exact(IReadOnlyList<WindowSample> slice)
        {
            var frames = slice.Select(sample => new PlaneSample(sample.Frame, sample.X, sample.Y, sample.Accepted)).ToArray();
            return RegionScenarioMath.MeasurePlane(frames, fps, unit, quadrants ? poolCenterX : null, quadrants ? poolCenterY : null, site ? platformX : null, site ? platformY : null, site ? platformRadius : null);
        }
        return Overlap(rows, fps, window, name, fullRun, site, Inside, quadrants ? Quadrant : null, target, Exact);
    }

    private static WindowedTrialMeasurement Separated(string name, ReleasedComparisonWindow window, TrialEndpointSet? full) => WithExposure(new()
    {
        Name = name, Supported = false, Hold = window.Hold, FullRun = full, Scope = RegionScenarioWording.FullDecoderScope
    }, window);

    private static Dictionary<int, TrackPoint> IndexFrames(IReadOnlyList<TrackPoint> points)
    {
        var index = new Dictionary<int, TrackPoint>(points.Count);
        foreach (var point in points)
        {
            if (point is null || !index.TryAdd(point.Frame, point))
                throw new InvalidOperationException("Frozen trajectory frame " + (point?.Frame.ToString(CultureInfo.InvariantCulture) ?? "missing") + " is duplicated. Indexed lookup was not guessed.");
        }
        return index;
    }

    private static WindowedTrialMeasurement WithExposure(WindowedTrialMeasurement measurement, ReleasedComparisonWindow window) => measurement with
    {
        RequestedExposureSeconds = window.RequestedExposureSeconds,
        EffectiveExposureSeconds = window.EffectiveExposureSeconds,
        TerminationReason = window.TerminationReason
    };

    private static string ComparisonScopeText(ReleasedComparisonWindow window) =>
        RegionScenarioWording.ComparisonScope + (string.IsNullOrWhiteSpace(window.ExposureNote) ? "" : " " + window.ExposureNote);

    private readonly record struct WindowSample(int Frame, double? X, double? Y, bool Accepted);

    private static WindowedTrialMeasurement Overlap(IReadOnlyList<WindowSample> samples, double fps, ReleasedComparisonWindow window, string name, TrialEndpointSet? full,
        bool siteConfigured, Func<WindowSample, bool> inside, Func<WindowSample, string>? quadrant, string? target, Func<IReadOnlyList<WindowSample>, TrialEndpointSet?> exact)
    {
        if (!double.IsFinite(fps) || fps <= 0) throw new ArgumentException("Comparison measurement needs a positive frame rate.");
        var ordered = samples.OrderBy(sample => sample.Frame).ToArray();
        var origin = window.OriginSeconds; var planned = window.PlannedSeconds; var stop = origin + planned;
        var included = new List<(WindowSample Sample, double Start, double End, double Overlap)>();
        foreach (var sample in ordered)
        {
            var frameStart = sample.Frame / fps; var frameEnd = (sample.Frame + 1d) / fps;
            var overlapStart = Math.Max(origin, frameStart); var overlapEnd = Math.Min(stop, frameEnd);
            var overlap = overlapEnd - overlapStart;
            if (overlap > 1e-9) included.Add((sample, overlapStart, overlapEnd, overlap));
        }
        var exactSlice = ExactSlice(included, fps, origin, stop);
        if (exactSlice)
        {
            var sliceComparison = exact(included.Select(item => item.Sample).ToArray());
            var sliceUnknown = included.Count(item => !item.Sample.Accepted) / fps;
            var sliceObserved = Math.Max(0, planned - sliceUnknown);
            if (sliceObserved <= 1e-9 && sliceUnknown > 1e-9)
                sliceComparison = sliceComparison is null ? null : sliceComparison with { PlatformSiteSeconds = null, PlatformSiteEntries = null };
            return WithExposure(new WindowedTrialMeasurement
            {
                Name = name, Supported = true, Scope = ComparisonScopeText(window), PlannedSeconds = planned,
                ObservedSeconds = sliceObserved, UnknownSeconds = sliceUnknown, FractionalBoundary = false,
                FullRun = full, Comparison = sliceComparison
            }, window);
        }
        double known = 0, unknown = 0, site = 0, cursor = 0;
        double? firstInside = null, earliestUnknown = null; var unknownBefore = false; bool? priorInside = null; int? priorFrame = null; var entries = 0;
        var counts = new Dictionary<string, double> { ["NW"] = 0, ["NE"] = 0, ["SW"] = 0, ["SE"] = 0 };
        foreach (var item in included)
        {
            var relativeStart = item.Start - origin;
            if (relativeStart > cursor + 1e-9)
            {
                unknown += relativeStart - cursor;
                if (firstInside is null) unknownBefore = true;
                earliestUnknown ??= cursor; priorInside = null; priorFrame = null;
            }
            cursor = Math.Max(cursor, item.End - origin);
            if (!item.Sample.Accepted || item.Sample.X is null || item.Sample.Y is null)
            {
                unknown += item.Overlap;
                if (firstInside is null) unknownBefore = true;
                earliestUnknown ??= relativeStart; priorInside = null; priorFrame = null; continue;
            }
            known += item.Overlap;
            if (quadrant is not null) counts[quadrant(item.Sample)] += item.Overlap;
            if (!siteConfigured) { priorFrame = item.Sample.Frame; continue; }
            var occupied = inside(item.Sample);
            if (occupied)
            {
                site += item.Overlap;
                firstInside ??= relativeStart;
                if (priorInside is false && priorFrame == item.Sample.Frame - 1) entries++;
            }
            priorInside = occupied; priorFrame = item.Sample.Frame;
        }
        if (planned > cursor + 1e-9)
        {
            unknown += planned - cursor;
            if (firstInside is null) unknownBefore = true;
            earliestUnknown ??= cursor;
        }
        EventHistory history; double? eventTime = null;
        if (!siteConfigured) history = EventHistory.NotConfigured;
        else if (firstInside.HasValue && !unknownBefore) { history = EventHistory.ObservedExact; eventTime = firstInside; }
        else if (firstInside.HasValue) history = EventHistory.EarlierVisitPossibleInGap;
        else if (unknown > 1e-9) history = EventHistory.VisitHistoryIndeterminate;
        else { history = EventHistory.RightCensoredComplete; eventTime = planned; }
        PercentBounds? bounds = target is not null && quadrant is not null
            ? new PercentBounds(100 * counts[target] / planned, 100 * (counts[target] + unknown) / planned) : null;
        var observedSite = siteConfigured && (known > 1e-9 || unknown <= 1e-9);
        var comparison = new TrialEndpointSet
        {
            PlatformSiteSeconds = observedSite ? site : null,
            PlatformSiteEntries = observedSite ? entries : null,
            TargetObservedPercent = target is not null && quadrant is not null && known > 1e-9 ? 100d * counts[target] / known : null,
            Distance = null, DistanceUnit = "",
            FirstObservedSiteVisitSeconds = firstInside,
            FirstVisitLowerSeconds = siteConfigured ? unknownBefore ? earliestUnknown : firstInside : null,
            FirstVisitUpperSeconds = siteConfigured ? firstInside : null,
            SiteVisitTime = eventTime, SiteVisitStatus = history,
            FirstVisitWithheld = history is EventHistory.EarlierVisitPossibleInGap or EventHistory.VisitHistoryIndeterminate,
            TargetMissingPositionBounds = bounds
        };
        return WithExposure(new WindowedTrialMeasurement
        {
            Name = name, Supported = true, Scope = ComparisonScopeText(window), PlannedSeconds = planned,
            ObservedSeconds = known, UnknownSeconds = unknown, FractionalBoundary = true,
            DistanceNote = RegionScenarioWording.ComparisonDistanceHold, FullRun = full, Comparison = comparison
        }, window);
    }

    private static bool ExactSlice(List<(WindowSample Sample, double Start, double End, double Overlap)> included, double fps, double origin, double stop)
    {
        if (included.Count == 0) return false;
        for (var i = 1; i < included.Count; i++) if (included[i].Sample.Frame != included[i - 1].Sample.Frame + 1) return false;
        if (Math.Abs(included[0].Sample.Frame / fps - origin) > 1e-9) return false;
        if (Math.Abs((included[^1].Sample.Frame + 1d) / fps - stop) > 1e-9) return false;
        return included.All(item => item.Overlap >= 1d / fps - 1e-9);
    }
}
