using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Calibration;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;

namespace MouseTrackerTabajara.Stability.Tests;

public sealed class RegionEvidenceIntegrationTests
{
    [Fact] public void ActualProducerEvidenceIsLoadedWithRawWindowAndHistoricalHoldsPreserved()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-stability-region-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); var path = Path.Combine(folder, "synthetic.avi");
        using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new Size(160, 120)))
        {
            Assert.True(writer.IsOpened()); for (var frame = 0; frame < 10; frame++) { using var image = new Mat(120, 160, MatType.CV_8UC3, new Scalar(180, 190, 200)); if (frame != 3) Cv2.Circle(image, new(50 + frame * 3, 60), 7, new Scalar(5, 5, 5), -1); writer.Write(image); }
        }
        var asset = new VideoAsset { Id = "synthetic", Path = path, SourceSha256 = OpenCvTracker.HashFile(path), Metadata = OpenCvTracker.Probe(path), Settings = new() { Cx = .5, Cy = .5, Rx = .45, Ry = .4, Threshold = 40, Blur = 1, MinArea = 20, MaxArea = 5000, ArenaConfirmed = true, Platform = new(50d / 160, .5, .06) } };
        var store = new StudyStore(Path.Combine(folder, "study.db")); store.SaveVideo(asset); var run = OpenCvTracker.Analyze(asset); store.SaveRun(run);
        var snapshot = AtlasSnapshot.Read(store.DatabasePath); var identity = RegionScenarioBaseline.Capture(snapshot, null);
        ImmutableArray<NamedRegionScenario> scenarios = [new() { Name = "site-shift", Rationale = "Measured prespecified synthetic center displacement20px", Kind = RegionScenarioKind.PlatformCenterExtent, CenterDeltaX = 20, Unit = DisclosedUnit.SourcePixels, ComponentRange = new(DisclosedUnit.SourcePixels, -20, 20) }, new() { Name = "detector-mask-unrun", Rationale = "Detection-affecting component deliberately unrun", Kind = RegionScenarioKind.PoolDetectionMaskUnrun, ComponentRange = new(DisclosedUnit.SourcePixels, -1, 1) }];
        var set = RegionScenarioMath.Evaluate(run.Points, run.Metadata, run.Settings, scenarios, RegionScenarioBaseline.Header(identity, identity.Sources.Single()));
        Assert.Equal(.3, set.Original.PlatformSiteSeconds!.Value, 8); Assert.Equal(.6, set.Scenarios[0].Endpoints!.PlatformSiteSeconds!.Value, 8); Assert.True(set.Scenarios[1].Held);
        var material = RegionScenarioBaseline.Material(snapshot, null); var materialJson = JsonDefaults.Serialize(material);
        var computation = new RegionScenarioComputation { Identity = identity, Scenarios = scenarios, Recordings = [new() { VideoId = asset.Id, Set = set }], Replay = material };
        var json = JsonDefaults.Serialize(computation); var document = new RegionScenarioEvidenceDocument { Identity = identity, ComputationJson = json, ComputationSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), FrozenMaterialJson = materialJson, FrozenMaterialSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(materialJson))) };
        var evidencePath = RegionScenarioEvidenceStore.Append(folder, document); var evidence = RegionStabilityAdapter.Read(evidencePath, store.DatabasePath);
        Assert.Equal(2, evidence.Report.Trials.Length); Assert.All(evidence.Report.Trials, row => { Assert.False(row.Supported); Assert.Contains(row.Holds, hold => hold.Contains("decoder")); }); Assert.Equal(.6, evidence.Report.Trials[0].Changed.Value);
        Assert.NotNull(evidence.Report.Trials[0].Trace); Assert.True(evidence.IsCurrent());
        // Actual measured producer API on independently encoded native coordinates; synthetic water-plane material, not detector-diameter cm.
        ImmutableArray<MeasuredLandmark> controls = [new("C1", new(10, 10), new(15, 0)), new("C2", new(150, 10), new(85, 0)), new("C3", new(150, 110), new(85, 50)), new("C4", new(10, 110), new(15, 50))];
        var request = new CalibrationRequest("synthetic camera", 160, 120, asset.SourceSha256, 0, 1, controls, [new("independent", new(80, 60), new(50, 25))], .01, .5, true, "synthetic measured reviewer");
        var profile = WaterPlaneCalibration.Fit(request); Assert.True(profile.EligibleForMeasuredCoordinates);
        var calibration = new NamedRegionScenario { Name = "measured-room-shift", Rationale = "Prespecified0.2cm measured control-coordinate uncertainty", Kind = RegionScenarioKind.CalibrationControlUncertainty, ComponentRange = new(DisclosedUnit.SourcePixels, -.1, .1), RoomRange = new(DisclosedUnit.Centimeters, -.2, .2),
            ControlShifts = controls.Select(c => new ControlCoordinateShift(c.Id, 0, 0, .2, 0)).ToImmutableArray(), Orientation = StudyOrientation.Room, PoolCenterXcm = 50.2, PoolCenterYcm = 25, PlatformXcm = 35.2, PlatformYcm = 25, PlatformRadiusCm = 4.8 };
        var transformed = CalibrationScenarioMath.Evaluate(profile, calibration, run.Points, run.Metadata, asset.SourceSha256); Assert.False(transformed.Held);
        Assert.Equal(35.2, transformed.Coordinates[0].RoomXcm!.Value, 7); Assert.Equal(25, transformed.Coordinates[0].RoomYcm!.Value, 7); Assert.Null(transformed.Coordinates[3].RoomXcm); Assert.Equal(10.5, transformed.Endpoints!.Distance!.Value, 7); Assert.Equal("cm", transformed.Endpoints.DistanceUnit);
        var badProfile = WaterPlaneCalibration.Fit(request with { IndependentCheckLandmarks = [new("independent", new(80, 60), new(55, 25))] }); Assert.False(badProfile.EligibleForMeasuredCoordinates);
        var bad = CalibrationScenarioMath.Evaluate(badProfile, calibration, run.Points, run.Metadata, asset.SourceSha256); Assert.True(bad.Held); Assert.Null(bad.Endpoints);
        var measuredIdentity = RegionScenarioBaseline.Capture(snapshot, profile);
        var badScenario = calibration with { Name = "bad-independent-check", RoomRange = new(DisclosedUnit.Centimeters, -2, 2), ControlShifts = controls.Select(c => new ControlCoordinateShift(c.Id, 0, 0, 2, 0)).ToImmutableArray() };
        var measuredSet = RegionScenarioMath.Evaluate(run.Points, run.Metadata, run.Settings, [calibration, badScenario], RegionScenarioBaseline.Header(measuredIdentity, measuredIdentity.Sources.Single()), scenario => CalibrationScenarioMath.Evaluate(profile, scenario, run.Points, run.Metadata, asset.SourceSha256));
        var profilePath = Path.Combine(folder, "measured-profile.json"); CalibrationProfileFile.Save(profile, profilePath);
        var measuredMaterial = RegionScenarioBaseline.Material(snapshot, File.ReadAllText(profilePath)); var measuredMaterialJson = JsonDefaults.Serialize(measuredMaterial);
        var measuredComputation = new RegionScenarioComputation { Identity = measuredIdentity, Scenarios = [calibration, badScenario], Recordings = [new() { VideoId = asset.Id, Set = measuredSet }], Replay = measuredMaterial };
        var measuredJson = JsonDefaults.Serialize(measuredComputation); var measuredDocument = new RegionScenarioEvidenceDocument { Identity = measuredIdentity, ComputationJson = measuredJson, ComputationSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(measuredJson))), FrozenMaterialJson = measuredMaterialJson, FrozenMaterialSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(measuredMaterialJson))) };
        var measuredPath = RegionScenarioEvidenceStore.Append(folder, measuredDocument); var measuredEvidence = RegionStabilityAdapter.Read(measuredPath, store.DatabasePath); Assert.Equal(2, measuredEvidence.Report.Trials.Length); Assert.All(measuredEvidence.Report.Trials, row => Assert.False(row.Supported)); Assert.Contains(measuredEvidence.Report.Trials.Single(row => row.Scenario == "bad-independent-check").Holds, hold => hold.Contains("held"));
        Assert.Single(RegionProducerReconstruction.Reconstruct(document)); Assert.Single(RegionProducerReconstruction.Reconstruct(measuredDocument));
        RegionProducerReconstruction.VerifySavedScalars(evidence.Report); RegionProducerReconstruction.VerifySavedScalars(measuredEvidence.Report);
        var scalarTampered = evidence.Report with { Trials = evidence.Report.Trials.SetItem(0, evidence.Report.Trials[0] with { Changed = new(.9, null, null) }) };
        Assert.Throws<InvalidDataException>(() => RegionProducerReconstruction.VerifySavedScalars(scalarTampered));
        var boundsTampered = evidence.Report with { Trials = evidence.Report.Trials.SetItem(0, evidence.Report.Trials[0] with { Changed = evidence.Report.Trials[0].Changed with { Upper = .99 } }) };
        Assert.Throws<InvalidDataException>(() => RegionProducerReconstruction.VerifySavedScalars(boundsTampered));
        var savedFolder = Path.Combine(folder, "authenticated-report"); StabilityReportWriter.Save(savedFolder, measuredEvidence.Report, false);
        var savedReplay = StabilitySavedReplay.Read(Path.Combine(savedFolder, "stability-input.json")); Assert.Equal(2, savedReplay.Report.Trials.Length);
        var changedFile = Path.Combine(folder, "scalar-tampered-report.json"); File.WriteAllText(changedFile, System.Text.Json.JsonSerializer.Serialize(scalarTampered)); Assert.Throws<InvalidDataException>(() => StabilitySavedReplay.Read(changedFile));
        store.SaveEditedSettings(asset with { Settings = asset.Settings with { Threshold = 41 } }, "synthetic settings revision"); Assert.False(evidence.IsCurrent());
        var historical = RegionStabilityAdapter.Read(evidencePath, store.DatabasePath); Assert.Equal(2, historical.Report.Trials.Length); Assert.All(historical.Report.Trials, row => Assert.Contains(row.Holds, hold => hold.Contains("Historical")));
        File.WriteAllText(Path.Combine(folder, "evidence-location.txt"), evidencePath); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
