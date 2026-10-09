using System.Collections.Immutable;
using MouseTrackerTabajara.Calibration;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Application;

public static class CalibrationScenarioMath
{
    public static CalibrationMeasurementResult Evaluate(CalibrationProfile? profile, NamedRegionScenario scenario, IReadOnlyList<TrackPoint> points,
        VideoMetadata metadata, string sourceSha256)
    {
        scenario.Validate();
        if (scenario.Kind != RegionScenarioKind.CalibrationControlUncertainty) throw new ArgumentException("This helper only evaluates calibration scenarios.");
        if (profile is null)
            return Hold("Measured calibration profile is missing. No centimeter transform was fabricated from detector geometry.");
        if (!string.Equals(profile.Request.ReferenceSourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
            return Hold("Measured profile reference source does not match this recording. The profile was not applied.", profile);
        if (profile.Request.ImageWidth != metadata.Width || profile.Request.ImageHeight != metadata.Height)
            return Hold("Measured profile resolution does not match the frozen source. The profile was not applied.", profile);
        var shifts = scenario.ControlShifts.IsDefault ? [] : scenario.ControlShifts;
        if (shifts.Any(shift => !profile.Request.ControlLandmarks.Any(landmark => landmark.Id == shift.LandmarkId)))
            return Hold("A control shift names a landmark that is not in the measured profile.", profile);
        var controls = profile.Request.ControlLandmarks.Select(landmark =>
        {
            var shift = shifts.FirstOrDefault(item => item.LandmarkId == landmark.Id);
            return shift is null ? landmark : landmark with
            {
                ImagePixels = new Point2(landmark.ImagePixels.X + shift.ImageDeltaX, landmark.ImagePixels.Y + shift.ImageDeltaY),
                RoomCentimeters = new Point2(landmark.RoomCentimeters.X + shift.RoomDeltaXCm, landmark.RoomCentimeters.Y + shift.RoomDeltaYCm)
            };
        }).ToImmutableArray();
        CalibrationProfile fitted;
        try { fitted = WaterPlaneCalibration.Fit(profile.Request with { ControlLandmarks = controls }); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Hold("Calibration fit failed and stays in the scenario set. " + ex.Message, profile);
        }
        if (!fitted.EligibleForMeasuredCoordinates)
            return Hold("Measured fit is held: " + string.Join(" ", fitted.HoldReasons) + " Coordinates were not transformed.", profile, fitted.Id);
        var coordinates = ImmutableArray.CreateBuilder<TransformedCoordinate>();
        var selected = new List<PlaneSample>();
        var notes = new List<string>();
        var targetReady = scenario.PoolCenterXcm is { } && scenario.PoolCenterYcm is not null && scenario.TargetXcm is not null && scenario.TargetYcm is not null;
        foreach (var point in points)
        {
            if (!point.Accepted)
            {
                coordinates.Add(new(point.Frame, null, null, null, null));
                selected.Add(new(point.Frame, null, null, false));
                continue;
            }
            try
            {
                var room = fitted.ToRoom(new Point2(point.X!.Value, point.Y!.Value));
                double? targetX = null, targetY = null;
                if (targetReady)
                {
                    var aligned = WaterPlaneCalibration.ToTargetAligned(room, new(scenario.PoolCenterXcm!.Value, scenario.PoolCenterYcm!.Value), new(scenario.TargetXcm!.Value, scenario.TargetYcm!.Value));
                    targetX = aligned.X; targetY = aligned.Y;
                }
                coordinates.Add(new(point.Frame, room.X, room.Y, targetX, targetY));
                if (scenario.Orientation == StudyOrientation.TargetAligned)
                    selected.Add(new(point.Frame, targetX, targetY, targetX is not null));
                else selected.Add(new(point.Frame, room.X, room.Y, true));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                notes.Add("Frame " + point.Frame.ToString(System.Globalization.CultureInfo.InvariantCulture) + " could not be transformed and stays unknown.");
                coordinates.Add(new(point.Frame, null, null, null, null));
                selected.Add(new(point.Frame, null, null, false));
            }
        }
        if (scenario.Orientation == StudyOrientation.TargetAligned && !targetReady)
            return Hold("Target orientation was declared, but the measured pool center and target were not both supplied. Room coordinates were not substituted.", profile, fitted.Id);
        TrialEndpointSet endpoints;
        try
        {
            endpoints = RegionScenarioMath.MeasurePlane(selected, metadata.Fps, "cm",
                scenario.Orientation == StudyOrientation.TargetAligned ? 0d : scenario.PoolCenterXcm,
                scenario.Orientation == StudyOrientation.TargetAligned ? 0d : scenario.PoolCenterYcm,
                scenario.PlatformXcm is null ? null : OrientedPlatform(scenario, true),
                scenario.PlatformYcm is null ? null : OrientedPlatform(scenario, false),
                scenario.PlatformRadiusCm);
        }
        catch (ArgumentException ex)
        {
            return Hold("Transformed coordinates stayed held. " + ex.Message, profile, fitted.Id) with { Coordinates = coordinates.ToImmutable() };
        }
        if (scenario.PlatformRadiusCm is null) notes.Add("Measured platform center and radius in centimeters were not supplied. Detector diameter was not used as a centimeter transform.");
        var outlier = Outlier(profile, fitted, points);
        return new CalibrationMeasurementResult(false, notes.ToImmutableArray(), profile.Id, profile.Request.Revision, fitted.Id,
            endpoints, outlier.Frame, outlier.Reason, coordinates.ToImmutable());
    }

    private static double? OrientedPlatform(NamedRegionScenario scenario, bool horizontal)
    {
        if (scenario.PlatformXcm is null || scenario.PlatformYcm is null || scenario.PlatformRadiusCm is null) return null;
        if (scenario.Orientation == StudyOrientation.Room) return horizontal ? scenario.PlatformXcm : scenario.PlatformYcm;
        if (scenario.PoolCenterXcm is null || scenario.PoolCenterYcm is null || scenario.TargetXcm is null || scenario.TargetYcm is null) return null;
        var aligned = WaterPlaneCalibration.ToTargetAligned(new(scenario.PlatformXcm.Value, scenario.PlatformYcm.Value),
            new(scenario.PoolCenterXcm.Value, scenario.PoolCenterYcm.Value), new(scenario.TargetXcm.Value, scenario.TargetYcm.Value));
        return horizontal ? aligned.X : aligned.Y;
    }

    public static WindowedTrialMeasurement MeasureWindow(NamedRegionScenario scenario, CalibrationMeasurementResult measured, ReleasedComparisonWindow window, double fps)
    {
        if (measured.Held || measured.Endpoints is null)
            return WindowedTrialMeasurement.Held(scenario.Name, measured.Holds.FirstOrDefault() ?? "Measured calibration is held.");
        var target = scenario.Orientation == StudyOrientation.TargetAligned;
        var samples = measured.Coordinates.Select(coordinate => target
            ? new PlaneSample(coordinate.Frame, coordinate.TargetXcm, coordinate.TargetYcm, coordinate.TargetXcm is not null && coordinate.TargetYcm is not null)
            : new PlaneSample(coordinate.Frame, coordinate.RoomXcm, coordinate.RoomYcm, coordinate.RoomXcm is not null && coordinate.RoomYcm is not null)).ToArray();
        double? platformX = null, platformY = null;
        if (scenario.PlatformXcm is not null && scenario.PlatformYcm is not null && scenario.PlatformRadiusCm is > 0)
        {
            platformX = OrientedPlatform(scenario, true);
            platformY = OrientedPlatform(scenario, false);
        }
        return RegionScenarioComparison.MeasurePlane(samples, fps, window, scenario.Name, "cm",
            target ? 0d : scenario.PoolCenterXcm, target ? 0d : scenario.PoolCenterYcm, platformX, platformY, scenario.PlatformRadiusCm, measured.Endpoints);
    }

    private static (int? Frame, string Reason) Outlier(CalibrationProfile baseline, CalibrationProfile fitted, IReadOnlyList<TrackPoint> points)
    {
        int? frame = null; var best = -1d;
        if (!baseline.EligibleForMeasuredCoordinates)
        {
            var first = points.FirstOrDefault(point => point.Accepted);
            return first is null ? (null, "No accepted frame is available to inspect.") : (first.Frame, "Baseline profile is held. The first accepted frame remains inspectable.");
        }
        foreach (var point in points)
        {
            if (!point.Accepted) continue;
            frame ??= point.Frame;
            try
            {
                var before = baseline.ToRoom(new Point2(point.X!.Value, point.Y!.Value));
                var after = fitted.ToRoom(new Point2(point.X.Value, point.Y.Value));
                var moved = before.DistanceTo(after);
                if (moved > best) { best = moved; frame = point.Frame; }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { frame ??= point.Frame; }
        }
        return frame is null ? (null, "No accepted frame is available to inspect.")
            : (frame, best <= 1e-9 ? "No transformed coordinate moved beyond the baseline fit. The first accepted frame remains inspectable."
                : "Accepted frame with the largest room-coordinate displacement from the baseline fit.");
    }

    private static CalibrationMeasurementResult Hold(string reason, CalibrationProfile? profile = null, string fitId = "") =>
        new(true, [reason], profile?.Id ?? "", profile?.Request.Revision ?? 0, fitId, null, null, "", []);
}
