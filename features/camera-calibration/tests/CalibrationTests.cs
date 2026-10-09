using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Xunit;

namespace MouseTrackerTabajara.Calibration.Tests;

public class CalibrationTests
{
    private static readonly Matrix3 Known = new(.1, .003, -48, -.002, .12, -44, .0001, -.00005, 1);
    private static MeasuredLandmark Landmark(string id, double x, double y) => new(id, new(x, y), Known.Map(new(x, y)));
    private static CalibrationRequest Request() => new("camera-A", 1920, 1080, new string('a', 64), 17, 1,
        [Landmark("NW", 240, 180), Landmark("NE", 1510, 170), Landmark("SW", 220, 840), Landmark("SE", 1480, 900),
         Landmark("N", 850, 120), Landmark("W", 140, 500)],
        [Landmark("check-C", 720, 520), Landmark("check-E", 1700, 500)], .01, .01, true, "Reviewer-A");
    private static CameraObservation Observe(CalibrationProfile profile, Func<MeasuredLandmark, Point2>? move = null) => new(profile.Request.CameraId,
        profile.Request.ImageWidth, profile.Request.ImageHeight, new string('b', 64), 3, true,
        profile.Request.ControlLandmarks.Select(p => new FiducialObservation(p.Id, move is null ? p.ImagePixels : move(p))).ToImmutableArray());

    [Fact] public void MeasuredProjectiveTransformRecoversIndependentRoomCoordinatesAndInverse()
    {
        var profile = WaterPlaneCalibration.Fit(Request());
        Assert.True(profile.EligibleForMeasuredCoordinates); Assert.All(profile.Residuals, p => Assert.InRange(p.ErrorCm, 0, 1e-9));
        foreach (var p in new[] { new Point2(20, 20), new Point2(810, 770), new Point2(1900, 1060) })
        {
            Assert.InRange(profile.ToRoom(p).DistanceTo(Known.Map(p)), 0, 1e-9);
            Assert.InRange(profile.ImageToRoom.Inverse().Map(profile.ToRoom(p)).DistanceTo(p), 0, 1e-8);
        }
    }
    [Fact] public void UnconfirmedPlaneOrReviewerOrAbsentIndependentChecksWithholdMeasuredUse()
    {
        var profile = WaterPlaneCalibration.Fit(Request() with { WaterPlaneAssumptionConfirmed = false, ConfirmedBy = "", IndependentCheckLandmarks = [] });
        Assert.False(profile.EligibleForMeasuredCoordinates); Assert.Equal(3, profile.HoldReasons.Length);
        Assert.Throws<InvalidOperationException>(() => profile.ToRoom(new(500, 500)));
    }
    [Fact] public void FourControlFitCannotValidateAccuracyWithoutAnIndependentPoint()
    {
        var input = Request(); var profile = WaterPlaneCalibration.Fit(input with { ControlLandmarks = input.ControlLandmarks.Take(4).ToImmutableArray(), IndependentCheckLandmarks = [] });
        Assert.All(profile.Residuals, p => Assert.InRange(p.ErrorCm, 0, 1e-9));
        Assert.False(profile.EligibleForMeasuredCoordinates); Assert.Contains(profile.HoldReasons, s => s.Contains("fit alone", StringComparison.Ordinal));
    }
    [Fact] public void IncorrectIndependentMeasurementFailsPrespecifiedTolerance()
    {
        var input = Request(); var wrong = input.IndependentCheckLandmarks[0] with { RoomCentimeters = new(300, 400) };
        var profile = WaterPlaneCalibration.Fit(input with { IndependentCheckLandmarks = [wrong] });
        Assert.False(profile.EligibleForMeasuredCoordinates); Assert.Contains(profile.HoldReasons, p => p.StartsWith("Independent check", StringComparison.Ordinal));
    }
    [Fact] public void PoorControlFitIsHeldEvenWhenItProducesAFiniteTransform()
    {
        var input = Request(); var controls = input.ControlLandmarks.SetItem(4, input.ControlLandmarks[4] with { RoomCentimeters = input.ControlLandmarks[4].RoomCentimeters with { X = 22 } });
        var profile = WaterPlaneCalibration.Fit(input with { ControlLandmarks = controls });
        Assert.False(profile.EligibleForMeasuredCoordinates); Assert.Contains(profile.HoldReasons, p => p.StartsWith("Control landmark", StringComparison.Ordinal));
    }
    [Fact] public void DuplicatePointCheckReuseAndNoncollinearGeometryAreRejected()
    {
        var input = Request();
        Assert.Throws<ArgumentException>(() => WaterPlaneCalibration.Fit(input with { IndependentCheckLandmarks = [input.ControlLandmarks[0] with { Id = "check-distinct-name" }] }));
        Assert.Throws<ArgumentException>(() => WaterPlaneCalibration.Fit(input with { ControlLandmarks = [Landmark("a", 10, 10), Landmark("b", 20, 20), Landmark("c", 30, 30), Landmark("d", 40, 40)] }));
        Assert.Throws<ArgumentException>(() => WaterPlaneCalibration.Fit(input with { IndependentCheckLandmarks = [input.IndependentCheckLandmarks[0] with { Id = "NW" }] }));
    }
    [Fact] public void InvalidInputsAndSourceCoordinatesAreRejected()
    {
        var input = Request();
        Assert.Throws<ArgumentException>(() => WaterPlaneCalibration.Fit(input with { MaximumControlErrorCm = double.NaN }));
        Assert.Throws<ArgumentException>(() => WaterPlaneCalibration.Fit(input with { ReferenceSourceSha256 = "fake" }));
        Assert.Throws<ArgumentException>(() => WaterPlaneCalibration.Fit(input with { ReferenceFrame = -1 }));
        Assert.Throws<ArgumentException>(() => WaterPlaneCalibration.Fit(input with { ControlLandmarks = input.ControlLandmarks.SetItem(0, Landmark("NW", -1, 100)) }));
        var profile = WaterPlaneCalibration.Fit(input);
        Assert.Throws<ArgumentException>(() => profile.ToRoom(new(1920, 100)));
        Assert.Throws<ArgumentException>(() => profile.ToRoom(new(double.PositiveInfinity, 0)));
    }
    [Fact] public void ProjectiveHorizonIsRefused()
    {
        var input = Request(); var horizon = new Matrix3(1, 0, 0, 0, 1, 0, -.001, 0, 1);
        var controls = new[] { new Point2(100, 100), new Point2(500, 100), new Point2(100, 800), new Point2(500, 800) }
            .Select((p, i) => new MeasuredLandmark(i.ToString(), p, horizon.Map(p))).ToImmutableArray();
        Assert.Throws<ArgumentException>(() => WaterPlaneCalibration.Fit(input with { ControlLandmarks = controls, IndependentCheckLandmarks = [] }));
        Assert.Throws<InvalidOperationException>(() => horizon.Map(new(1000, 100)));
    }
    [Fact] public void CanonicalIdentityChangesWithConfirmationAndDimensionsButNotInsertionOrder()
    {
        var input = Request(); var original = WaterPlaneCalibration.Fit(input);
        var reversed = WaterPlaneCalibration.Fit(input with { ControlLandmarks = input.ControlLandmarks.Reverse().ToImmutableArray(), IndependentCheckLandmarks = input.IndependentCheckLandmarks.Reverse().ToImmutableArray() });
        Assert.Equal(original.Id, reversed.Id); Assert.Equal(original.ImageToRoom, reversed.ImageToRoom);
        Assert.NotEqual(original.Id, WaterPlaneCalibration.Fit(input with { Revision = 2 }).Id);
        Assert.NotEqual(original.Id, WaterPlaneCalibration.Fit(input with { ConfirmedBy = "OtherReviewer" }).Id);
        Assert.NotEqual(original.Id, WaterPlaneCalibration.Fit(input with { ImageWidth = 2000 }).Id);
    }
    [Fact] public void TargetAlignedViewDoesNotMutateRoomCoordinatesOrDistances()
    {
        var room = new Point2(8, 5); var center = new Point2(2, 3); var target = new Point2(2, 13);
        var aligned = WaterPlaneCalibration.ToTargetAligned(room, center, target);
        Assert.Equal(new Point2(2, -6), aligned); Assert.Equal(new Point2(8, 5), room);
        Assert.InRange(Math.Abs(room.DistanceTo(center) - aligned.DistanceTo(new(0, 0))), 0, 1e-12);
        Assert.Throws<ArgumentException>(() => WaterPlaneCalibration.ToTargetAligned(room, center, center));
    }
    [Fact] public void StableCameraMayReuseButTranslationOrNontranslationRequiresConfirmation()
    {
        var profile = WaterPlaneCalibration.Fit(Request()); var stable = CameraReuse.Evaluate(profile, Observe(profile), new(2, .5));
        Assert.True(stable.CanReuse); Assert.Equal(0d, stable.TranslationMagnitude);
        var translated = CameraReuse.Evaluate(profile, Observe(profile, p => new(p.ImagePixels.X + 8, p.ImagePixels.Y - 6)), new(2, .5));
        Assert.False(translated.CanReuse); Assert.Equal(10d, translated.TranslationMagnitude); Assert.Equal(0d, translated.MaximumResidualPixels);
        var shiftedOne = CameraReuse.Evaluate(profile, Observe(profile, p => new(p.ImagePixels.X + (p.Id == "NW" ? 6 : 0), p.ImagePixels.Y)), new(2, .5));
        Assert.False(shiftedOne.CanReuse); Assert.Contains(shiftedOne.HoldReasons, p => p.Contains("not consistent with translation", StringComparison.Ordinal));
    }
    [Fact] public void CameraIdentityResolutionUnconfirmedMarkersAndIncompleteObservationRemainHeld()
    {
        var profile = WaterPlaneCalibration.Fit(Request()); var observation = Observe(profile);
        Assert.False(CameraReuse.Evaluate(profile, observation with { CameraId = "camera-B" }, new(2, .5)).CanReuse);
        Assert.False(CameraReuse.Evaluate(profile, observation with { ImageWidth = 1280 }, new(2, .5)).CanReuse);
        Assert.False(CameraReuse.Evaluate(profile, observation with { StationaryMarkersConfirmed = false }, new(2, .5)).CanReuse);
        var incomplete = CameraReuse.Evaluate(profile, observation with { Fiducials = observation.Fiducials.Take(3).ToImmutableArray() }, new(2, .5));
        Assert.False(incomplete.CanReuse); Assert.Equal(3, incomplete.MatchedMarkers);
        Assert.False(CameraReuse.Evaluate(profile, observation with { Fiducials = [] }, new(2, .5)).CanReuse);
    }
    [Fact] public void InvalidCameraMarkerIdsAndTolerancesCannotDisappear()
    {
        var profile = WaterPlaneCalibration.Fit(Request()); var observation = Observe(profile);
        Assert.Throws<ArgumentException>(() => CameraReuse.Evaluate(profile, observation, new(-1, .5)));
        Assert.Throws<ArgumentException>(() => CameraReuse.Evaluate(profile, observation with { Fiducials = [observation.Fiducials[0], observation.Fiducials[0]] }, new(2, .5)));
        var unknown = observation with { Fiducials = observation.Fiducials.Add(new("unknown", new(200, 200))) };
        Assert.False(CameraReuse.Evaluate(profile, unknown, new(2, .5)).CanReuse);
    }
    [Fact] public void SavedMeasuredInputsReplayAndOverwritesOrEditedContentsAreRefused()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TabajaraCalibration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "profile.json"); var profile = WaterPlaneCalibration.Fit(Request());
            CalibrationProfileFile.Save(profile, path); var restored = CalibrationProfileFile.Load(path);
            Assert.Equal(profile.Id, restored.Id); Assert.Equal(profile.ImageToRoom, restored.ImageToRoom);
            var originalBytes = File.ReadAllBytes(path);
            Assert.Throws<IOException>(() => CalibrationProfileFile.Save(profile, path));
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(folder, "*.pending-*"));
            var edited = JsonNode.Parse(File.ReadAllText(path))!; edited["Request"]!["ConfirmedBy"] = "ChangedReviewer"; File.WriteAllText(path, edited.ToJsonString());
            Assert.Throws<InvalidDataException>(() => CalibrationProfileFile.Load(path));
            Assert.Throws<InvalidDataException>(() => CalibrationProfileFile.Save(profile with { ImageToRoom = Known }, Path.Combine(folder, "forged.json")));
        }
        finally
        {
            var full = Path.GetFullPath(folder);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("TabajaraCalibration-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid test cleanup path.");
            Directory.Delete(full, true);
        }
    }

    [Fact] public void ProjectiveFitIsStableAcrossImageScaleAndTypicalResolution()
    {
        var input = Request();
        var expected = WaterPlaneCalibration.Fit(input);
        foreach (var scale in new[] { .5, 2d, 4d })
        {
            MeasuredLandmark Scaled(MeasuredLandmark landmark) => landmark with { ImagePixels = new(landmark.ImagePixels.X * scale, landmark.ImagePixels.Y * scale) };
            var fitted = WaterPlaneCalibration.Fit(input with { ImageWidth = (int)(1920 * scale), ImageHeight = (int)(1080 * scale),
                ControlLandmarks = input.ControlLandmarks.Select(Scaled).ToImmutableArray(), IndependentCheckLandmarks = input.IndependentCheckLandmarks.Select(Scaled).ToImmutableArray() });
            Assert.True(fitted.EligibleForMeasuredCoordinates);
            var point = new Point2(670, 515);
            Assert.InRange(fitted.ToRoom(new(point.X * scale, point.Y * scale)).DistanceTo(expected.ToRoom(point)), 0, 1e-9);
        }
    }
    [Fact] public void CameraToleranceBoundaryAndInputOrderAreDeterministic()
    {
        var profile = WaterPlaneCalibration.Fit(Request());
        var observation = Observe(profile, p => new(p.ImagePixels.X + 2, p.ImagePixels.Y));
        var boundary = CameraReuse.Evaluate(profile, observation, new(2, .5));
        Assert.True(boundary.CanReuse);
        var reversed = CameraReuse.Evaluate(profile, observation with { Fiducials = observation.Fiducials.Reverse().ToImmutableArray() }, new(2, .5));
        Assert.Equal(boundary.TranslationMagnitude, reversed.TranslationMagnitude);
        Assert.Equal(boundary.MaximumResidualPixels, reversed.MaximumResidualPixels);
        Assert.Equal(boundary.HoldReasons.ToArray(), reversed.HoldReasons.ToArray());
        Assert.False(CameraReuse.Evaluate(profile, observation, new(1.999, .5)).CanReuse);
    }
}
