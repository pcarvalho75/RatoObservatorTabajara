using System.Collections.Immutable;
using System.IO;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;

namespace MouseTrackerTabajara.Atlas.Tests;

public class ExperimentContractTests
{
    [Fact] public void ConfirmedReferenceUsesDeclaredRotationButIncompleteRowsAndCentralTargetsStayHeld()
    {
        var folder = Path.Combine(Path.GetTempPath(), "experiment-contract-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "synthetic.avi");
            using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 5, new Size(100, 100)))
            { Assert.True(writer.IsOpened()); for (var frame = 0; frame < 10; frame++) { using var image = new Mat(100, 100, MatType.CV_8UC3, new Scalar(50 + frame, 100, 150)); writer.Write(image); } }
            var store = new StudyStore(Path.Combine(folder, "synthetic.morrisdb")); var asset = new WorkspaceService(store).Import(path) with
            { AnimalId = "animal", AnimalIdConfirmed = true, GroupName = "group", Phase = TrialMode.Probe,
                Settings = new() { Cx = .5, Cy = .5, Rx = .4, Ry = .4, ArenaConfirmed = true, Platform = new(.7, .5, .03), StartSeconds = .2, EndSeconds = 1.8 } };
            store.SaveVideo(asset);
            var points = Enumerable.Range(1, 8).Select(f => new TrackPoint(f, f / 5d, 50 + f, 50, PointStatus.Tracked, 1)).ToList();
            var run = new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = asset.Settings, Points = points,
                Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings), AlgorithmContract = AlgorithmIdentity.Contract, NominalTimingVerified = true };
            store.SaveRun(run); store.RecordBiologicalEvent(asset, BiologicalEventKind.WaterEntry, 1, "Synthetic release", "Reviewer");
            store.SaveProtocolWindow(asset, new() { RequestedExposureSeconds = 1.6, ReleaseConfirmed = true, ReleaseFrame = 1, WindowConfirmed = true, Reviewer = "Reviewer", Reason = "Synthetic window" }); store.RecordReview(run, true);
            var plan = new StudyPlan { UnitDesign = StudyUnitDesign.IndependentAnimals, DesignConfirmed = true, RosterConfirmed = true, Exploratory = true,
                Animals = [new() { Id = "animal", Alias = "Alias", Group = "group", IdentityConfirmed = true, AllocationConfirmed = true }],
                Contexts = [new() { Id = "context", Phase = TrialMode.Probe, Day = 1, RequestedExposureSeconds = 1.6, ComparisonDurationSeconds = 1.6, WindowConfirmed = true, Orientation = StudyOrientation.Room, AlignmentConfirmed = true, CommonCoordinateReference = "Normalized room", TemporalBinEdges = [0, .8, 1.6] }],
                Trials = [new() { Id = "trial", AnimalId = "animal", ContextId = "context", RecordingId = asset.Id, SourceReferenceConfirmed = true,
                    ReferenceSourceSha256 = asset.SourceSha256, ReferenceSettingsHash = StudyStore.SettingsHash(asset.Settings), RoomRotationRadians = Math.PI / 2 }] };
            var plans = new StudyPlanStore(store); plans.Save(plan, "Reviewer", "Explicit normalized reference", 0);
            var result = ExperimentPlanAdapter.Build(AtlasSnapshot.Read(store.DatabasePath), "context"); Assert.Single(result.Study.Groups);
            var frame1 = result.Trials[0].Map!.Input.Frames[0]; Assert.InRange(Math.Abs(frame1.X!.Value), 0, 1e-10); Assert.Equal(1d / 40, frame1.Y!.Value, 10);
            var targetPlan = plan with { Contexts = plan.Contexts.Select(c => c with { Orientation = StudyOrientation.TargetAligned }).ToImmutableArray() };
            plans.Save(targetPlan, "Reviewer", "Separate target orientation", 1); result = ExperimentPlanAdapter.Build(AtlasSnapshot.Read(store.DatabasePath), "context"); Assert.Single(result.Study.Groups);
            Assert.Equal(1d / 40, result.Trials[0].Map!.Input.Frames[0].X!.Value, 10);
            var centralSettings = asset.Settings with { Platform = new(.5, .5, .03) }; asset = asset with { Settings = centralSettings }; store.SaveVideo(asset);
            run = run with { Id = Guid.NewGuid().ToString("N"), Settings = centralSettings, Metrics = MetricCalculator.Calculate(points, asset.Metadata, centralSettings) }; store.SaveRun(run); store.RecordReview(run, true);
            targetPlan = targetPlan with { Trials = targetPlan.Trials.Select(t => t with { ReferenceSettingsHash = StudyStore.SettingsHash(centralSettings) }).ToImmutableArray() };
            plans.Save(targetPlan, "Reviewer", "Central platform direction undefined", 2); result = ExperimentPlanAdapter.Build(AtlasSnapshot.Read(store.DatabasePath), "context"); Assert.Empty(result.Study.Groups); Assert.Contains(result.Trials[0].Holds, h => h.Contains("Central platform"));
            var roomPlan = targetPlan with { Contexts = targetPlan.Contexts.Select(c => c with { Orientation = StudyOrientation.Room }).ToImmutableArray() };
            plans.Save(roomPlan, "Reviewer", "Separate room reference retains central platform", 3);
            var complete = ExperimentPlanAdapter.Build(AtlasSnapshot.Read(store.DatabasePath), "context"); Assert.Single(complete.Study.Groups);
            Assert.All(complete.AnimalOccupancyBounds["animal"], b => Assert.Equal(b.Lower, b.Upper));
            var unknown = points.Select(p => p.Frame == 4 ? p with { X = null, Y = null, Status = PointStatus.Missing } : p).ToList();
            run = run with { Id = Guid.NewGuid().ToString("N"), Points = unknown, Metrics = MetricCalculator.Calculate(unknown, asset.Metadata, centralSettings) }; store.SaveRun(run); store.RecordReview(run, true);
            var partial = ExperimentPlanAdapter.Build(AtlasSnapshot.Read(store.DatabasePath), "context"); Assert.Single(partial.Study.Groups);
            Assert.Contains(partial.AnimalOccupancyBounds["animal"], b => b.Upper - b.Lower >= .125 - 1e-9);
            run = run with { Id = Guid.NewGuid().ToString("N"), Points = points.Where(p => p.Frame != 4).ToList() }; store.SaveRun(run); store.RecordReview(run, true);
            result = ExperimentPlanAdapter.Build(AtlasSnapshot.Read(store.DatabasePath), "context"); Assert.Empty(result.Study.Groups); Assert.Null(result.Trials[0].Map); Assert.Contains(result.Trials[0].Holds, h => h.Contains("Expected8") || h.Contains("Expected 8"));
            Assert.Equal("pool radii", frame1.Accepted ? result.Study.Animals[0].Grid!.CoordinateUnit : "unexpected");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }
}
