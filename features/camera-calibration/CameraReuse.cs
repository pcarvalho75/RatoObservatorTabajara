using System.Collections.Immutable;

namespace MouseTrackerTabajara.Calibration;

public sealed record FiducialObservation(string Id, Point2 ImagePixels);
public sealed record CameraObservation(string CameraId, int ImageWidth, int ImageHeight,
    string SourceSha256, int SourceFrame, bool StationaryMarkersConfirmed,
    ImmutableArray<FiducialObservation> Fiducials);
public sealed record ShiftTolerances(double MaximumTranslationPixels, double MaximumResidualPixels);
public sealed record CameraReuseDecision(string ProfileId, string ObservedSourceSha256, int ObservedFrame,
    bool CanReuse, double? TranslationX, double? TranslationY, double? TranslationMagnitude,
    double? MaximumResidualPixels, int MatchedMarkers, ImmutableArray<string> HoldReasons);

public static class CameraReuse
{
    public static CameraReuseDecision Evaluate(CalibrationProfile profile, CameraObservation observation, ShiftTolerances tolerances)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(observation); ArgumentNullException.ThrowIfNull(tolerances);
        if (!double.IsFinite(tolerances.MaximumTranslationPixels) || tolerances.MaximumTranslationPixels < 0 ||
            !double.IsFinite(tolerances.MaximumResidualPixels) || tolerances.MaximumResidualPixels <= 0)
            throw new ArgumentException("Supply finite prespecified camera-shift tolerances.");
        if (string.IsNullOrWhiteSpace(observation.SourceSha256) || observation.SourceSha256.Length != 64 || !observation.SourceSha256.All(Uri.IsHexDigit) || observation.SourceFrame < 0)
            throw new ArgumentException("Observation requires a source SHA256 and zero-based source frame.");
        if (observation.Fiducials.IsDefault || observation.Fiducials.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id)) ||
            observation.Fiducials.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != observation.Fiducials.Length)
            throw new ArgumentException("Observation markers must have unique nonempty identities.");
        var holds = ImmutableArray.CreateBuilder<string>();
        if (!profile.EligibleForMeasuredCoordinates) holds.AddRange(profile.HoldReasons);
        if (observation.CameraId != profile.Request.CameraId) holds.Add("Camera identity differs; select or confirm a different profile.");
        if (observation.ImageWidth != profile.Request.ImageWidth || observation.ImageHeight != profile.Request.ImageHeight)
            holds.Add("Source resolution differs; the profile cannot be silently rescaled.");
        if (!observation.StationaryMarkersConfirmed) holds.Add("Confirm these are stationary camera-registration markers, not animals or water reflections.");
        if (holds.Any(p => p.StartsWith("Source resolution", StringComparison.Ordinal))) return Result(null, null, null, null, 0);
        foreach (var marker in observation.Fiducials) WaterPlaneCalibration.ValidateImagePoint(marker.ImagePixels, observation.ImageWidth, observation.ImageHeight);
        var reference = profile.Request.ControlLandmarks.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var observed = observation.Fiducials.ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (observed.Keys.Any(id => !reference.ContainsKey(id))) holds.Add("Unknown marker identity in the observation.");
        var matches = reference.Keys.Where(observed.ContainsKey).Order(StringComparer.Ordinal).ToArray();
        if (matches.Length != reference.Count) holds.Add("Some reference markers are absent; camera compatibility is incomplete.");
        if (matches.Length < 3) { holds.Add("At least3 matched stationary markers are needed for a camera-shift check."); return Result(null, null, null, null, matches.Length); }
        var shifts = matches.Select(id => new Point2(observed[id].ImagePixels.X - reference[id].ImagePixels.X,
            observed[id].ImagePixels.Y - reference[id].ImagePixels.Y)).ToArray();
        var translation = new Point2(Median(shifts.Select(p => p.X)), Median(shifts.Select(p => p.Y)));
        var magnitude = translation.DistanceTo(new(0, 0));
        var maximumResidual = shifts.Max(p => p.DistanceTo(translation));
        if (magnitude > tolerances.MaximumTranslationPixels) holds.Add("Camera translation exceeds the prespecified tolerance; recalibrate and confirm geometry.");
        if (maximumResidual > tolerances.MaximumResidualPixels) holds.Add("Marker displacement is not consistent with translation; inspect camera rotation, scale or incorrect landmarks.");
        return Result(translation.X, translation.Y, magnitude, maximumResidual, matches.Length);

        CameraReuseDecision Result(double? dx, double? dy, double? magnitudeValue, double? residual, int count) =>
            new(profile.Id, observation.SourceSha256, observation.SourceFrame, holds.Count == 0, dx, dy, magnitudeValue, residual, count, holds.ToImmutable());
    }
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray(); var center = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[center - 1] + sorted[center]) / 2 : sorted[center];
    }
}
