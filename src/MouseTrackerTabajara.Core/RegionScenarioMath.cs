using System.Collections.Immutable;
using System.Globalization;

namespace MouseTrackerTabajara.Core;

/// <summary>
/// Measurement-region consequences on frozen coordinates. Parameter search and memory scores are intentionally absent.
/// </summary>
public static class RegionScenarioWording
{
    public const string CodeContract = AlgorithmIdentity.Contract + ";region-scenario-measurement-v1";
    public const string FrozenCoordinateMode =
        "Frozen-coordinate measurement. This recomputes region measurements on the stored points. It is not native retracking and it is not the effect of changing the detector mask.";
    public const string DetectionUnrunMode =
        "Detection-affecting pool geometry is a separate unrun component. The detector mask was not changed and native retracking did not occur.";
    public const string CalibrationMode =
        "Frozen-coordinate measurement transform. Measured planar fit and independent checks only. Not native retracking. Room and target orientations stay separate. Centimeter values are not derived from detector geometry or pool diameter.";
    public const string EnvelopeLabel =
        "Named-scenario envelope across the prespecified variants that produced this endpoint. This is not a confidence interval, not sampling uncertainty, and not the missing-position bound.";
    public const string MissingPositionLabel =
        "Missing-position bound from unknown frames inside this trial. This is not a scenario envelope and not sampling uncertainty.";
    public const string ParameterPolicy =
        "Scenario names, rationales, and ranges were supplied before calculation. Parameters were not selected from group endpoints. No memory score is computed.";
    public const string Weighting =
        "Equal animal weights. Each eligible animal has weight 1. Trials are averaged within an animal before animals are averaged. Frames and recordings are not weights.";
    public const string OriginalName = "original";
    public const string FullDecoderScope =
        "Full decoder interval. This is the raw measurement on stored points. It is not the confirmed comparison-window trial value.";
    public const string ComparisonScope =
        "Confirmed release-relative planned comparison. The planned duration stays the denominator. Fractional frame overlap counts, and unobserved time inside the window stays unknown mass. The planned trial is not shortened to observed frames, and this is not the full decoder interval.";
    public const string ComparisonDistanceHold =
        "Path length is held unless the planned window is exactly whole decoded frames. No fractional path was invented, and the planned duration was not shortened to the observed frames.";
    public const string ComparisonSubstitutionHold =
        "Full-run measurements stay separate and are not the trial value.";
    public const string EffectiveExposureLimit =
        "Post-termination decoder time is not valid exposure. Requested exposure was not rewritten.";
}

public enum RegionScenarioKind
{
    PlatformCenterExtent,
    PoolMeasurementEllipse,
    PoolDetectionMaskUnrun,
    CalibrationControlUncertainty
}

public enum DisclosedUnit { SourcePixels, Normalized, Centimeters }

public readonly record struct DisclosedRange(DisclosedUnit Unit, double Minimum, double Maximum)
{
    public bool Contains(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum;
    public void Validate(string name)
    {
        if (!Enum.IsDefined(Unit) || !double.IsFinite(Minimum) || !double.IsFinite(Maximum) || Minimum > Maximum)
            throw new ArgumentException(name + " range must be finite, ordered, and in a disclosed unit.");
    }
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Unit} [{Minimum:0.###}, {Maximum:0.###}]");
}

public sealed record ControlCoordinateShift(string LandmarkId, double ImageDeltaX, double ImageDeltaY, double RoomDeltaXCm, double RoomDeltaYCm);

public sealed record NamedRegionScenario
{
    public required string Name { get; init; }
    public required string Rationale { get; init; }
    public required RegionScenarioKind Kind { get; init; }
    public DisclosedUnit Unit { get; init; } = DisclosedUnit.SourcePixels;
    public double CenterDeltaX { get; init; }
    public double CenterDeltaY { get; init; }
    public double ExtentDelta { get; init; }
    public double PoolRyDelta { get; init; }
    public DisclosedRange ComponentRange { get; init; }
    public DisclosedRange RoomRange { get; init; }
    public ImmutableArray<ControlCoordinateShift> ControlShifts { get; init; } = [];
    public StudyOrientation Orientation { get; init; } = StudyOrientation.Room;
    public double? PoolCenterXcm { get; init; }
    public double? PoolCenterYcm { get; init; }
    public double? TargetXcm { get; init; }
    public double? TargetYcm { get; init; }
    public double? PlatformXcm { get; init; }
    public double? PlatformYcm { get; init; }
    public double? PlatformRadiusCm { get; init; }

    public void Validate()
    {
        var name = Name?.Trim() ?? "";
        var rationale = Rationale?.Trim() ?? "";
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("Scenario name must be a short readable label.");
        if (rationale.Length is < 1 or > 500) throw new ArgumentException("Write the measured rationale before calculation.");
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Unit) || !Enum.IsDefined(Orientation)) throw new ArgumentException("Scenario kind, unit, or orientation is not recognized.");
        if (Kind is RegionScenarioKind.PlatformCenterExtent or RegionScenarioKind.PoolMeasurementEllipse or RegionScenarioKind.PoolDetectionMaskUnrun)
        {
            if (Unit == DisclosedUnit.Centimeters) throw new ArgumentException("Platform and pool perturbations are in source pixels or normalized source fractions, not centimeters.");
            ComponentRange.Validate("Component");
            if (ComponentRange.Unit != Unit) throw new ArgumentException("The documented range unit must match the scenario unit.");
            if (!ComponentRange.Contains(CenterDeltaX) || !ComponentRange.Contains(CenterDeltaY) || !ComponentRange.Contains(ExtentDelta) || !ComponentRange.Contains(PoolRyDelta))
                throw new ArgumentException("Every perturbation must lie inside its documented finite range.");
        }
        if (Kind == RegionScenarioKind.CalibrationControlUncertainty)
        {
            if (Orientation is not (StudyOrientation.Room or StudyOrientation.TargetAligned))
                throw new ArgumentException("Declare room or target orientation before a calibration scenario is calculated.");
            ComponentRange.Validate("Image-control");
            RoomRange.Validate("Room-control");
            if (ComponentRange.Unit != DisclosedUnit.SourcePixels || RoomRange.Unit != DisclosedUnit.Centimeters)
                throw new ArgumentException("Control-image uncertainty is in source pixels and room uncertainty is in centimeters.");
            var shifts = ControlShifts.IsDefault ? [] : ControlShifts;
            if (shifts.Any(s => s is null || string.IsNullOrWhiteSpace(s.LandmarkId)) || shifts.Select(s => s.LandmarkId).Distinct(StringComparer.Ordinal).Count() != shifts.Length)
                throw new ArgumentException("Control-shift landmark identities must be nonempty and unique.");
            foreach (var shift in shifts)
            {
                if (!ComponentRange.Contains(shift.ImageDeltaX) || !ComponentRange.Contains(shift.ImageDeltaY) || !RoomRange.Contains(shift.RoomDeltaXCm) || !RoomRange.Contains(shift.RoomDeltaYCm))
                    throw new ArgumentException("Control-coordinate shifts must lie inside the documented pixel and centimeter ranges.");
            }
            RequireOptional(PoolCenterXcm, "Pool center X"); RequireOptional(PoolCenterYcm, "Pool center Y");
            RequireOptional(TargetXcm, "Target X"); RequireOptional(TargetYcm, "Target Y");
            RequireOptional(PlatformXcm, "Platform X"); RequireOptional(PlatformYcm, "Platform Y");
            if (PlatformRadiusCm is { } radius && (!double.IsFinite(radius) || radius <= 0)) throw new ArgumentException("Measured platform radius must be positive centimeters.");
            if (Orientation == StudyOrientation.TargetAligned && (PoolCenterXcm is null || PoolCenterYcm is null || TargetXcm is null || TargetYcm is null))
                throw new ArgumentException("Target orientation needs a measured pool center and target in room centimeters.");
        }
    }

    private static void RequireOptional(double? value, string name)
    {
        if (value is { } number && !double.IsFinite(number)) throw new ArgumentException(name + " must be finite when it is supplied.");
    }
}

public sealed record TrialEndpointSet
{
    public double? PlatformSiteSeconds { get; init; }
    public int? PlatformSiteEntries { get; init; }
    public double? TargetObservedPercent { get; init; }
    public double? Distance { get; init; }
    public string DistanceUnit { get; init; } = "";
    public double? MeanProximity { get; init; }
    public string ProximityUnit { get; init; } = "";
    public double? FirstObservedSiteVisitSeconds { get; init; }
    public double? FirstVisitLowerSeconds { get; init; }
    public double? FirstVisitUpperSeconds { get; init; }
    public double? SiteVisitTime { get; init; }
    public EventHistory SiteVisitStatus { get; init; }
    public bool FirstVisitWithheld { get; init; }
    public PercentBounds? TargetMissingPositionBounds { get; init; }
    public string MissingPositionLabel { get; init; } = RegionScenarioWording.MissingPositionLabel;

    public static TrialEndpointSet From(Measurements measurements) => new()
    {
        PlatformSiteSeconds = measurements.PlatformSiteSeconds,
        PlatformSiteEntries = measurements.PlatformSiteEntries,
        TargetObservedPercent = measurements.TargetObservedPercent,
        Distance = measurements.Distance,
        DistanceUnit = measurements.DistanceUnit == "cm" ? "detector-diameter units" : measurements.DistanceUnit,
        MeanProximity = measurements.MeanProximityDiameters,
        ProximityUnit = measurements.PlatformSiteSeconds is null ? "" : "pool diameters",
        FirstObservedSiteVisitSeconds = measurements.FirstObservedSiteVisitSeconds,
        FirstVisitLowerSeconds = measurements.FirstVisitLowerSeconds,
        FirstVisitUpperSeconds = measurements.FirstVisitUpperSeconds,
        SiteVisitTime = measurements.SiteVisitTime,
        SiteVisitStatus = measurements.SiteVisitStatus,
        FirstVisitWithheld = measurements.SiteVisitStatus is EventHistory.EarlierVisitPossibleInGap or EventHistory.VisitHistoryIndeterminate,
        TargetMissingPositionBounds = measurements.TargetFullTrialPercentBounds
    };
}

public readonly record struct PlaneSample(int Frame, double? X, double? Y, bool Accepted);

public sealed record EndpointDelta
{
    public double? PlatformSiteSeconds { get; init; }
    public int? PlatformSiteEntries { get; init; }
    public double? TargetObservedPercent { get; init; }
    public double? Distance { get; init; }
    public string DistanceUnit { get; init; } = "";
    public double? FirstVisitSeconds { get; init; }
    public string FirstVisitNote { get; init; } = "";
}

public sealed record ScenarioEnvelope
{
    public string Endpoint { get; init; } = "";
    public string Unit { get; init; } = "";
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public bool Numeric { get; init; }
    public ImmutableArray<string> Contributing { get; init; } = [];
    public ImmutableArray<string> HeldOutside { get; init; } = [];
    public string Label { get; init; } = RegionScenarioWording.EnvelopeLabel;
}

public sealed record RegionScenarioOutcome
{
    public string Name { get; init; } = "";
    public string Rationale { get; init; } = "";
    public RegionScenarioKind Kind { get; init; }
    public string Mode { get; init; } = "";
    public string Ranges { get; init; } = "";
    public bool Held { get; init; }
    public ImmutableArray<string> Holds { get; init; } = [];
    public TrialEndpointSet? Endpoints { get; init; }
    public EndpointDelta? DeltaFromOriginal { get; init; }
    public TrackingSettings? MeasurementSettings { get; init; }
    public int? OutlierFrame { get; init; }
    public string OutlierReason { get; init; } = "";
    public ImmutableArray<TransformedCoordinate> Coordinates { get; init; } = [];
    public string BaselineProfileId { get; init; } = "";
    public int BaselineProfileRevision { get; init; }
    public string ScenarioFitId { get; init; } = "";
}

public readonly record struct TransformedCoordinate(int Frame, double? RoomXcm, double? RoomYcm, double? TargetXcm, double? TargetYcm);

public sealed record CalibrationMeasurementResult(
    bool Held, ImmutableArray<string> Holds, string BaselineProfileId, int BaselineProfileRevision, string ScenarioFitId,
    TrialEndpointSet? Endpoints, int? OutlierFrame, string OutlierReason, ImmutableArray<TransformedCoordinate> Coordinates);

public sealed record ScenarioIdentityHeader
{
    public string OriginalRunId { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public string SettingsHash { get; init; } = "";
    public string CorrectionFingerprint { get; init; } = "";
    public string CodeContract { get; init; } = RegionScenarioWording.CodeContract;
    public string ImplementationSha256 { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public int ProfileRevision { get; init; }
    public long PlanRevision { get; init; }
    public string PlanFingerprint { get; init; } = "";
    public int ProtocolRevision { get; init; }
    public string ProtocolFingerprint { get; init; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OriginalRunId) || SourceSha256 is not { Length: 64 } || !SourceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Scenario output requires the original run and source identity.");
        if (string.IsNullOrWhiteSpace(SettingsHash) || string.IsNullOrWhiteSpace(CodeContract))
            throw new ArgumentException("Scenario output requires settings and the declared code contract.");
        if (ImplementationSha256 is not { Length: 64 } || !ImplementationSha256.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Scenario output requires the implementation assembly and source SHA256. The code contract alone does not authenticate the computation.");
    }
}

public sealed record RegionScenarioSet
{
    public ScenarioIdentityHeader Identity { get; init; } = new();
    public string ParameterPolicy { get; init; } = RegionScenarioWording.ParameterPolicy;
    public TrialEndpointSet Original { get; init; } = new();
    public TrackingSettings BaselineSettings { get; init; } = new();
    public ImmutableArray<RegionScenarioOutcome> Scenarios { get; init; } = [];
    public ImmutableArray<ScenarioEnvelope> Envelopes { get; init; } = [];
}

public sealed record ScenarioTrialContract
{
    public string TrialId { get; init; } = "";
    public string AnimalId { get; init; } = "";
    public string Group { get; init; } = "";
    public bool AnimalIdentityConfirmed { get; init; }
    public bool AllocationConfirmed { get; init; }
    public bool AnimalExcluded { get; init; }
    public string AnimalExclusionReason { get; init; } = "";
    public bool RecordingPresent { get; init; }
    public string MissingReason { get; init; } = "";
    public bool RecordingIncluded { get; init; }
    public string RecordingExclusionReason { get; init; } = "";
    public bool SlotMatchesRecording { get; init; }
    public bool SourceReferenceConfirmed { get; init; }
    public bool SourceHashMatches { get; init; }
    public bool SettingsHashMatches { get; init; }
    public bool WindowConfirmed { get; init; }
    public bool ReleaseConfirmed { get; init; }
    public bool ProtocolMatchesContext { get; init; }
    public bool CurrentInspection { get; init; }
    public bool EffectiveOutcomeClear { get; init; }
    public string EffectiveOutcomeHold { get; init; } = "";
    public bool OrientationConfirmed { get; init; }
    public StudyOrientation Orientation { get; init; }
    public string Unit { get; init; } = "";
    public double? Endpoint { get; init; }
    public bool MeasurementHeld { get; init; }
    public string MeasurementHold { get; init; } = "";

    public string? Ineligibility()
    {
        if (string.IsNullOrWhiteSpace(TrialId) || string.IsNullOrWhiteSpace(AnimalId)) return "Planned animal/trial identity is missing.";
        if (!AnimalIdentityConfirmed) return "Animal identity is unconfirmed.";
        if (!AllocationConfirmed) return "Group allocation is unconfirmed.";
        if (AnimalExcluded) return "Animal excluded: " + (string.IsNullOrWhiteSpace(AnimalExclusionReason) ? "reason not recorded" : AnimalExclusionReason);
        if (!RecordingPresent) return "Planned recording missing" + (string.IsNullOrWhiteSpace(MissingReason) ? "." : ": " + MissingReason);
        if (!RecordingIncluded) return "Recording excluded: " + (string.IsNullOrWhiteSpace(RecordingExclusionReason) ? "reason not recorded" : RecordingExclusionReason);
        if (!SlotMatchesRecording) return "Recording phase, day, or trial differs from its planned slot.";
        if (!SourceReferenceConfirmed || !SourceHashMatches || !SettingsHashMatches) return "Confirmed source reference does not match the frozen source or settings.";
        if (!WindowConfirmed || !ReleaseConfirmed || !ProtocolMatchesContext) return "Confirmed release and window do not match the planned context.";
        if (!CurrentInspection) return "Current frozen inspection is missing, invalidated, or stale.";
        if (!EffectiveOutcomeClear) return "Effective protocol outcome is held" + (string.IsNullOrWhiteSpace(EffectiveOutcomeHold) ? "." : ": " + EffectiveOutcomeHold);
        if (!OrientationConfirmed || Orientation == StudyOrientation.Unconfirmed) return "Common orientation is unconfirmed.";
        if (MeasurementHeld || Endpoint is null) return "Measurement is held" + (string.IsNullOrWhiteSpace(MeasurementHold) ? "." : ": " + MeasurementHold);
        if (string.IsNullOrWhiteSpace(Unit)) return "Endpoint unit is missing.";
        return null;
    }
}

public sealed record AnimalWeightedRow
{
    public string AnimalId { get; init; } = "";
    public string Group { get; init; } = "";
    public double Weight { get; init; }
    public string Status { get; init; } = "";
    public string Detail { get; init; } = "";
    public double? AnimalMean { get; init; }
    public ImmutableArray<string> TrialIds { get; init; } = [];
}

public sealed record GroupAnimalMean(string Group, int EligibleAnimals, double? Mean, string Note);

public sealed record CollectionSummary
{
    public bool Emitted { get; init; }
    public string Hold { get; init; } = "";
    public string Weighting { get; init; } = RegionScenarioWording.Weighting;
    public ImmutableArray<AnimalWeightedRow> Animals { get; init; } = [];
    public ImmutableArray<GroupAnimalMean> Groups { get; init; } = [];
}

public static class RegionScenarioMath
{
    public static bool TryMeasurementSettings(TrackingSettings baseline, NamedRegionScenario scenario, int width, int height, out TrackingSettings settings, out string? hold)
    {
        settings = baseline; hold = null;
        scenario.Validate();
        if (width <= 0 || height <= 0) { hold = "Source dimensions are invalid."; return false; }
        if (scenario.Kind == RegionScenarioKind.PoolDetectionMaskUnrun) { hold = RegionScenarioWording.DetectionUnrunMode; return false; }
        if (scenario.Kind == RegionScenarioKind.CalibrationControlUncertainty) { hold = "Calibration scenarios do not edit the detector ellipse."; return false; }
        try
        {
            TrackingSettings edited;
            if (scenario.Kind == RegionScenarioKind.PlatformCenterExtent)
            {
                if (baseline.Platform is not { } site) { hold = "Baseline has no platform site. The center/extent scenario stays held."; return false; }
                edited = baseline with
                {
                    Platform = new PlatformSite(
                        site.X + Scale(scenario.CenterDeltaX, scenario.Unit, width),
                        site.Y + Scale(scenario.CenterDeltaY, scenario.Unit, height),
                        site.Radius + Scale(scenario.ExtentDelta, scenario.Unit, width))
                };
            }
            else
            {
                edited = baseline with
                {
                    Cx = baseline.Cx + Scale(scenario.CenterDeltaX, scenario.Unit, width),
                    Cy = baseline.Cy + Scale(scenario.CenterDeltaY, scenario.Unit, height),
                    Rx = baseline.Rx + Scale(scenario.ExtentDelta, scenario.Unit, width),
                    Ry = baseline.Ry + Scale(scenario.PoolRyDelta, scenario.Unit, height)
                };
            }
            settings = ArenaGeometry.MergeEdit(baseline, edited);
            return true;
        }
        catch (ArgumentException ex)
        {
            hold = "Perturbed geometry is outside the permitted measurement domain. " + ex.Message;
            return false;
        }
    }

    public static TrialEndpointSet MeasurePlane(IReadOnlyList<PlaneSample> samples, double fps, string unit, double? poolCenterX, double? poolCenterY,
        double? platformX, double? platformY, double? platformRadius)
    {
        if (samples.Count == 0) throw new ArgumentException("The trial contains no frames.");
        if (!double.IsFinite(fps) || fps <= 0 || string.IsNullOrWhiteSpace(unit)) throw new ArgumentException("Plane measurement needs a positive frame rate and a unit.");
        for (var i = 1; i < samples.Count; i++) if (samples[i].Frame != samples[i - 1].Frame + 1) throw new ArgumentException("Frames must be consecutive; represent gaps explicitly.");
        var site = platformX is not null && platformY is not null && platformRadius is not null;
        if (site && (platformRadius <= 0 || !double.IsFinite(platformX!.Value) || !double.IsFinite(platformY!.Value)))
            throw new ArgumentException("Measured platform radius must be positive and finite.");
        var quadrants = poolCenterX is not null && poolCenterY is not null && double.IsFinite(poolCenterX.Value) && double.IsFinite(poolCenterY.Value);
        var counts = new Dictionary<string, int> { ["NW"] = 0, ["NE"] = 0, ["SW"] = 0, ["SE"] = 0 };
        var tracked = 0; var siteFrames = 0; var entries = 0; double distance = 0, proximity = 0;
        double? firstEntry = null, earliestUnknown = null; var unknownBeforeVisit = false;
        PlaneSample? prior = null; bool? priorInside = null;
        foreach (var sample in samples)
        {
            var time = (sample.Frame - samples[0].Frame) / fps;
            if (!sample.Accepted || sample.X is null || sample.Y is null || !double.IsFinite(sample.X.Value) || !double.IsFinite(sample.Y.Value))
            {
                earliestUnknown ??= time;
                if (firstEntry is null) unknownBeforeVisit = true;
                prior = null; priorInside = null; continue;
            }
            tracked++;
            if (quadrants)
            {
                var u = sample.X.Value - poolCenterX!.Value; var v = sample.Y.Value - poolCenterY!.Value;
                counts[(v < 0 ? "N" : "S") + (u < 0 ? "W" : "E")]++;
            }
            if (prior is { Accepted: true } && sample.Frame == prior.Value.Frame + 1)
                distance += Math.Sqrt(Math.Pow(sample.X.Value - prior.Value.X!.Value, 2) + Math.Pow(sample.Y.Value - prior.Value.Y!.Value, 2));
            prior = sample;
            if (!site) continue;
            var inside = Math.Sqrt(Math.Pow(sample.X.Value - platformX!.Value, 2) + Math.Pow(sample.Y.Value - platformY!.Value, 2)) <= platformRadius!.Value;
            proximity += Math.Sqrt(Math.Pow(sample.X.Value - platformX.Value, 2) + Math.Pow(sample.Y.Value - platformY.Value, 2));
            if (inside)
            {
                siteFrames++;
                firstEntry ??= time;
                if (priorInside is false) entries++;
            }
            priorInside = inside;
        }
        var duration = samples.Count / fps; var unknown = (samples.Count - tracked) / fps;
        string? target = null;
        if (site && quadrants)
            target = (platformY!.Value < poolCenterY!.Value ? "N" : "S") + (platformX!.Value < poolCenterX!.Value ? "W" : "E");
        EventHistory history; double? eventTime = null;
        if (!site) history = EventHistory.NotConfigured;
        else if (firstEntry.HasValue && !unknownBeforeVisit) { history = EventHistory.ObservedExact; eventTime = firstEntry; }
        else if (firstEntry.HasValue) history = EventHistory.EarlierVisitPossibleInGap;
        else if (unknown > 0) history = EventHistory.VisitHistoryIndeterminate;
        else { history = EventHistory.RightCensoredComplete; eventTime = duration; }
        PercentBounds? bounds = null;
        if (target is not null)
        {
            var seconds = counts[target] / fps;
            bounds = new PercentBounds(100 * seconds / duration, 100 * (seconds + unknown) / duration);
        }
        return new TrialEndpointSet
        {
            PlatformSiteSeconds = site ? siteFrames / fps : null,
            PlatformSiteEntries = site ? entries : null,
            TargetObservedPercent = target is not null && tracked > 0 ? 100d * counts[target] / tracked : null,
            Distance = distance, DistanceUnit = unit,
            MeanProximity = site && tracked > 0 ? proximity / tracked : null,
            ProximityUnit = site ? unit : "",
            FirstObservedSiteVisitSeconds = firstEntry,
            FirstVisitLowerSeconds = site ? unknownBeforeVisit ? earliestUnknown : firstEntry : null,
            FirstVisitUpperSeconds = site ? firstEntry : null,
            SiteVisitTime = eventTime, SiteVisitStatus = history,
            FirstVisitWithheld = history is EventHistory.EarlierVisitPossibleInGap or EventHistory.VisitHistoryIndeterminate,
            TargetMissingPositionBounds = bounds
        };
    }

    public static RegionScenarioSet Evaluate(IReadOnlyList<TrackPoint> points, VideoMetadata metadata, TrackingSettings baseline,
        IReadOnlyList<NamedRegionScenario> scenarios, ScenarioIdentityHeader identity, Func<NamedRegionScenario, CalibrationMeasurementResult>? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(points); ArgumentNullException.ThrowIfNull(metadata); ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(scenarios); identity.Validate(); baseline.Validate();
        if (scenarios.Count == 0) throw new ArgumentException("Name at least one scenario and its rationale before calculation.");
        foreach (var scenario in scenarios) scenario.Validate();
        if (scenarios.Select(s => s.Name.Trim()).Distinct(StringComparer.Ordinal).Count() != scenarios.Count)
            throw new ArgumentException("Scenario names must be unique.");
        var frozen = points.ToArray();
        var original = TrialEndpointSet.From(MetricCalculator.Calculate(frozen, metadata, baseline));
        var outcomes = ImmutableArray.CreateBuilder<RegionScenarioOutcome>();
        foreach (var scenario in scenarios)
        {
            try { outcomes.Add(One(frozen, metadata, baseline, original, scenario, calibration)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { outcomes.Add(Held(scenario, ex.Message)); }
        }
        return new RegionScenarioSet
        {
            Identity = identity, Original = original, BaselineSettings = baseline,
            Scenarios = outcomes.ToImmutable(), Envelopes = Envelopes(original, outcomes.ToImmutable())
        };
    }

    public static CollectionSummary Collect(IReadOnlyList<ScenarioTrialContract> trials)
    {
        ArgumentNullException.ThrowIfNull(trials);
        if (trials.Count == 0) return new() { Emitted = false, Hold = "No planned trials were supplied. Collection rows stay withheld." };
        var orientations = trials.Select(t => t.Orientation).Where(o => o != StudyOrientation.Unconfirmed).Distinct().ToArray();
        var units = trials.Where(t => t.Endpoint is not null && !string.IsNullOrWhiteSpace(t.Unit)).Select(t => t.Unit).Distinct(StringComparer.Ordinal).ToArray();
        string? blocked = orientations.Length > 1 ? "Room and target orientations are not comparable in one collection."
            : units.Length > 1 ? "Endpoint units are not comparable in one collection." : null;
        var rows = ImmutableArray.CreateBuilder<AnimalWeightedRow>();
        foreach (var animal in trials.GroupBy(t => t.AnimalId, StringComparer.Ordinal))
        {
            var members = animal.ToArray();
            var details = new List<string>();
            var eligible = new List<ScenarioTrialContract>();
            foreach (var trial in members)
            {
                var reason = blocked ?? trial.Ineligibility();
                if (reason is null) eligible.Add(trial);
                else details.Add(trial.TrialId + ": " + reason);
            }
            string status; double weight; double? mean;
            if (members.Any(t => t.AnimalExcluded)) { status = "excluded"; weight = 0; mean = null; }
            else if (eligible.Count > 0) { status = "eligible"; weight = 1; mean = eligible.Average(t => t.Endpoint!.Value); }
            else if (members.All(t => !t.RecordingPresent)) { status = "missing"; weight = 0; mean = null; }
            else { status = "held"; weight = 0; mean = null; }
            rows.Add(new AnimalWeightedRow
            {
                AnimalId = animal.Key, Group = members.Select(t => t.Group).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)) ?? "",
                Weight = weight, Status = status, AnimalMean = mean, TrialIds = members.Select(t => t.TrialId).ToImmutableArray(),
                Detail = details.Count == 0 ? "Eligible trials contribute to this animal mean." : string.Join(" ", details)
            });
        }
        var groups = rows.ToImmutable().GroupBy(r => r.Group, StringComparer.Ordinal).Select(group =>
        {
            var included = group.Where(r => r.Status == "eligible").ToArray();
            return new GroupAnimalMean(group.Key, included.Length, included.Length == 0 ? null : included.Average(r => r.AnimalMean!.Value),
                "Equal animal weights. Not a confidence interval, causal contrast, or memory score.");
        }).ToImmutableArray();
        return new CollectionSummary { Emitted = true, Hold = blocked ?? "", Animals = rows.ToImmutable(), Groups = groups };
    }

    private static RegionScenarioOutcome One(IReadOnlyList<TrackPoint> points, VideoMetadata metadata, TrackingSettings baseline, TrialEndpointSet original,
        NamedRegionScenario scenario, Func<NamedRegionScenario, CalibrationMeasurementResult>? calibration)
    {
        if (scenario.Kind == RegionScenarioKind.PoolDetectionMaskUnrun)
            return Inspectable(Held(scenario, RegionScenarioWording.DetectionUnrunMode) with { Mode = RegionScenarioWording.DetectionUnrunMode, Ranges = scenario.ComponentRange.ToString() }, points);
        if (scenario.Kind == RegionScenarioKind.CalibrationControlUncertainty)
        {
            if (calibration is null) return Inspectable(Held(scenario, "Measured calibration profile is missing. No centimeter transform was fabricated from detector geometry."), points);
            var measured = calibration(scenario);
            var outcome = new RegionScenarioOutcome
            {
                Name = scenario.Name.Trim(), Rationale = scenario.Rationale.Trim(), Kind = scenario.Kind,
                Mode = RegionScenarioWording.CalibrationMode,
                Ranges = scenario.ComponentRange + "; " + scenario.RoomRange + "; " + scenario.Orientation,
                Held = measured.Held, Holds = measured.Holds.IsDefault ? [] : measured.Holds,
                Endpoints = measured.Held ? null : measured.Endpoints,
                DeltaFromOriginal = measured.Held || measured.Endpoints is null ? null : Delta(original, measured.Endpoints),
                OutlierFrame = measured.OutlierFrame, OutlierReason = measured.OutlierReason,
                Coordinates = measured.Coordinates.IsDefault ? [] : measured.Coordinates,
                BaselineProfileId = measured.BaselineProfileId, BaselineProfileRevision = measured.BaselineProfileRevision,
                ScenarioFitId = measured.ScenarioFitId
            };
            return measured.Held ? Inspectable(outcome, points) : outcome;
        }
        if (!TryMeasurementSettings(baseline, scenario, metadata.Width, metadata.Height, out var settings, out var hold))
            return Inspectable(Held(scenario, hold ?? "Measurement geometry is held."), points);
        var endpoints = TrialEndpointSet.From(MetricCalculator.Calculate(points, metadata, settings));
        var changed = FirstMembershipChange(points, metadata, baseline, settings);
        return new RegionScenarioOutcome
        {
            Name = scenario.Name.Trim(), Rationale = scenario.Rationale.Trim(), Kind = scenario.Kind,
            Mode = RegionScenarioWording.FrozenCoordinateMode, Ranges = scenario.ComponentRange.ToString(),
            Endpoints = endpoints, DeltaFromOriginal = Delta(original, endpoints), MeasurementSettings = settings,
            OutlierFrame = changed.Frame, OutlierReason = changed.Reason
        };
    }

    private static (int? Frame, string Reason) FirstMembershipChange(IReadOnlyList<TrackPoint> points, VideoMetadata metadata, TrackingSettings baseline, TrackingSettings scenario)
    {
        int? first = null;
        foreach (var point in points)
        {
            if (!point.Accepted) continue;
            first ??= point.Frame;
            var before = ArenaGeometry.InsidePlatformSite(point.X!.Value, point.Y!.Value, baseline, metadata.Width, metadata.Height);
            var after = ArenaGeometry.InsidePlatformSite(point.X.Value, point.Y.Value, scenario, metadata.Width, metadata.Height);
            if (before != after) return (point.Frame, "First accepted frame whose platform-site membership differs from the frozen baseline.");
        }
        return first is null ? (null, "No accepted frame is available to inspect.") : (first, "No site-membership change. The first accepted frame remains inspectable.");
    }

    private static RegionScenarioOutcome Inspectable(RegionScenarioOutcome outcome, IReadOnlyList<TrackPoint> points)
    {
        if (outcome.OutlierFrame is not null) return outcome;
        var first = points.FirstOrDefault(point => point.Accepted);
        return first is null ? outcome : outcome with { OutlierFrame = first.Frame, OutlierReason = "The scenario is held. The first accepted frame remains inspectable." };
    }

    private static RegionScenarioOutcome Held(NamedRegionScenario scenario, string reason) => new()
    {
        Name = scenario.Name.Trim(), Rationale = scenario.Rationale.Trim(), Kind = scenario.Kind,
        Mode = scenario.Kind == RegionScenarioKind.CalibrationControlUncertainty ? RegionScenarioWording.CalibrationMode : RegionScenarioWording.FrozenCoordinateMode,
        Ranges = scenario.Kind == RegionScenarioKind.CalibrationControlUncertainty ? scenario.ComponentRange + "; " + scenario.RoomRange : scenario.ComponentRange.ToString(),
        Held = true, Holds = [reason]
    };

    private static EndpointDelta Delta(TrialEndpointSet original, TrialEndpointSet scenario)
    {
        var sameVisit = original.SiteVisitStatus == scenario.SiteVisitStatus && original.SiteVisitTime is not null && scenario.SiteVisitTime is not null
            && original.SiteVisitStatus is EventHistory.ObservedExact or EventHistory.RightCensoredComplete;
        var sameDistance = string.Equals(original.DistanceUnit, scenario.DistanceUnit, StringComparison.Ordinal) && original.Distance is not null && scenario.Distance is not null;
        return new EndpointDelta
        {
            PlatformSiteSeconds = Both(original.PlatformSiteSeconds, scenario.PlatformSiteSeconds),
            PlatformSiteEntries = original.PlatformSiteEntries is { } left && scenario.PlatformSiteEntries is { } right ? right - left : null,
            TargetObservedPercent = Both(original.TargetObservedPercent, scenario.TargetObservedPercent),
            Distance = sameDistance ? scenario.Distance - original.Distance : null,
            DistanceUnit = sameDistance ? original.DistanceUnit : "",
            FirstVisitSeconds = sameVisit ? scenario.SiteVisitTime - original.SiteVisitTime : null,
            FirstVisitNote = sameVisit ? "Both histories are the same exact or fully censored class." : "First-visit delta is held because a gap withholds the time or the histories differ."
        };
    }

    private static double? Both(double? left, double? right) => left is not null && right is not null ? right - left : null;

    private static ImmutableArray<ScenarioEnvelope> Envelopes(TrialEndpointSet original, ImmutableArray<RegionScenarioOutcome> scenarios)
    {
        (string Name, double? Value, string Unit)[] Fields(string name, TrialEndpointSet? set) => name switch
        {
            "platform_site_seconds" => [(name, set?.PlatformSiteSeconds, "s")],
            "platform_site_entries" => [(name, set?.PlatformSiteEntries is { } count ? count : null, "count")],
            "target_observed_percent" => [(name, set?.TargetObservedPercent, "percent of tracked frames")],
            "distance" => [(name, set?.Distance, set?.DistanceUnit ?? "")],
            "first_site_visit_seconds" => [(name, set is { FirstVisitWithheld: false, SiteVisitTime: not null } ? set.SiteVisitTime : null, "s")],
            _ => []
        };
        var names = new[] { "platform_site_seconds", "platform_site_entries", "target_observed_percent", "distance", "first_site_visit_seconds" };
        return names.Select(name =>
        {
            var samples = new List<(string Name, double Value, string Unit)>();
            var held = new List<string>();
            void Consider(string contributor, TrialEndpointSet? set)
            {
                var field = Fields(name, set)[0];
                if (field.Value is { } value && !string.IsNullOrWhiteSpace(field.Unit)) samples.Add((contributor, value, field.Unit));
                else held.Add(contributor);
            }
            Consider(RegionScenarioWording.OriginalName, original);
            foreach (var scenario in scenarios) Consider(scenario.Name, scenario.Held ? null : scenario.Endpoints);
            var units = samples.Select(s => s.Unit).Distinct(StringComparer.Ordinal).ToArray();
            var mixed = units.Length > 1;
            if (mixed) { held.AddRange(samples.Select(s => s.Name)); samples.Clear(); }
            if (samples.Count == 0)
                return new ScenarioEnvelope { Endpoint = name, Numeric = false, HeldOutside = held.ToImmutableArray(), Label = mixed ? RegionScenarioWording.EnvelopeLabel + " Units differ, so no numeric envelope is formed." : RegionScenarioWording.EnvelopeLabel };
            return new ScenarioEnvelope
            {
                Endpoint = name, Unit = units[0], Numeric = true, Minimum = samples.Min(s => s.Value), Maximum = samples.Max(s => s.Value),
                Contributing = samples.Select(s => s.Name).ToImmutableArray(), HeldOutside = held.ToImmutableArray()
            };
        }).ToImmutableArray();
    }

    private static double Scale(double delta, DisclosedUnit unit, int span) => unit == DisclosedUnit.SourcePixels ? delta / span : delta;
}
