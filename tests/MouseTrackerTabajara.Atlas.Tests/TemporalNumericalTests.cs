using System.IO;
using System.Collections.Immutable;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;

namespace MouseTrackerTabajara.Atlas.Tests;

public class TemporalNumericalTests
{
    internal sealed class Fixture : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "temporal-numerical-" + Guid.NewGuid().ToString("N"));
        public StudyStore Store { get; }
        public VideoAsset Asset { get; private set; }
        public AnalysisRun Run { get; private set; }
        public StudyPlan Plan { get; private set; }
        public Fixture()
        {
            Directory.CreateDirectory(Folder); var path = Path.Combine(Folder, "frames.avi");
            using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 4, new Size(100, 100)))
            { Assert.True(writer.IsOpened()); for (var f = 0; f < 12; f++) { using var image = new Mat(100, 100, MatType.CV_8UC3, new Scalar(20 + f * 3, 80, 160)); writer.Write(image); } }
            Store = new StudyStore(Path.Combine(Folder, "study.morrisdb"));
            Asset = new WorkspaceService(Store).Import(path) with { AnimalId = "QZ0", AnimalIdConfirmed = true, GroupName = "cohort", Phase = TrialMode.Probe,
                Settings = new() { Cx = .5, Cy = .5, Rx = .4, Ry = .4, WallBoundary = .5, ArenaConfirmed = true, Platform = new(.6, .5, .04), StartSeconds = .5, EndSeconds = 2.5 } };
            Store.SaveVideo(Asset);
            var points = Enumerable.Range(2, 8).Select(f => new TrackPoint(f, f / 4d, 50 + (f - 2) * 4, 50, PointStatus.Tracked, 1)).ToList();
            Run = Make(points, Asset.Settings); Store.SaveRun(Run);
            Store.RecordBiologicalEvent(Asset, BiologicalEventKind.WaterEntry, 2, "Confirmed synthetic release", "Reviewer");
            Store.SaveProtocolWindow(Asset, new() { ReleaseConfirmed = true, ReleaseFrame = 2, WindowConfirmed = true, RequestedExposureSeconds = 2, Reviewer = "Reviewer", Reason = "Fixed synthetic exposure" }); Store.RecordReview(Run, true);
            Plan = new() { UnitDesign = StudyUnitDesign.Unknown, DesignConfirmed = false, RosterConfirmed = true, Exploratory = true,
                Animals = [new() { Id = "QZ0", Alias = "Opaque", Group = "cohort", IdentityConfirmed = true, AllocationConfirmed = true }],
                Contexts = [new() { Id = "scope", Phase = TrialMode.Probe, RequestedExposureSeconds = 2, ComparisonDurationSeconds = 2, TemporalBinEdges = [0, .125, .625, 2], WindowConfirmed = true, AlignmentConfirmed = true, Orientation = StudyOrientation.Room, CommonCoordinateReference = "Normalized room" }],
                Trials = [new() { Id = "slot", AnimalId = "QZ0", ContextId = "scope", RecordingId = Asset.Id, SourceReferenceConfirmed = true, ReferenceSourceSha256 = Asset.SourceSha256, ReferenceSettingsHash = StudyStore.SettingsHash(Asset.Settings) }] };
            new StudyPlanStore(Store).Save(Plan, "Reviewer", "Prespecified synthetic bins", 0);
        }
        private AnalysisRun Make(List<TrackPoint> points, TrackingSettings settings) => new() { VideoId = Asset.Id, SourceSha256 = Asset.SourceSha256, Metadata = Asset.Metadata, Settings = settings,
            Points = points, Metrics = MetricCalculator.Calculate(points, Asset.Metadata, settings), NominalTimingVerified = true, AlgorithmContract = AlgorithmIdentity.Contract };
        public void Unknown(int frame)
        { Run = Make(Run.Points.Select(p => p.Frame == frame ? p with { X = null, Y = null, Status = PointStatus.Missing } : p).ToList(), Asset.Settings); Store.SaveRun(Run); Store.RecordReview(Run, true); }
        public void Window(TrialMode phase, PlatformSite platform, int? mounting = null)
        {
            Asset = Asset with { Phase = phase, Settings = Asset.Settings with { Platform = platform } }; Store.SaveVideo(Asset);
            Run = Make(Run.Points.ToList(), Asset.Settings); Store.SaveRun(Run);
            if (mounting.HasValue) Store.RecordBiologicalEvent(Asset, BiologicalEventKind.PlatformMounting, mounting.Value, "Confirmed synthetic mounting", "Reviewer");
            Store.SaveProtocolWindow(Asset, new() { ReleaseConfirmed = true, ReleaseFrame = 2, WindowConfirmed = true, RequestedExposureSeconds = 2, Reviewer = "Reviewer", Reason = "Preserved requested exposure" }); Store.RecordReview(Run, true);
            Plan = Plan with { Contexts = Plan.Contexts.Select(c => c with { Phase = phase }).ToImmutableArray(), Trials = Plan.Trials.Select(t => t with { ReferenceSettingsHash = StudyStore.SettingsHash(Asset.Settings) }).ToImmutableArray() };
            var plans = new StudyPlanStore(Store); plans.Save(Plan, "Reviewer", "Declared phase/reference change", plans.Latest()!.Revision);
        }
        public void Trial(string animal, int order, string name, Func<int, int> position)
        {
            var path = Path.Combine(Folder, name + ".avi");
            using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 4, new Size(100, 100)))
            { Assert.True(writer.IsOpened()); for (var f = 0; f < 12; f++) { using var image = new Mat(100, 100, MatType.CV_8UC3, new Scalar(name.Length + f * 3, 90, 180)); writer.Write(image); } }
            var asset = new WorkspaceService(Store).Import(path) with { AnimalId = animal, AnimalIdConfirmed = true, GroupName = "cohort", Phase = TrialMode.Probe, Trial = order, Settings = Asset.Settings }; Store.SaveVideo(asset);
            var points = Enumerable.Range(2, 8).Select(f => new TrackPoint(f, f / 4d, position(f), 50, PointStatus.Tracked, 1)).ToList();
            var run = new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = asset.Settings, Points = points,
                Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings), NominalTimingVerified = true, AlgorithmContract = AlgorithmIdentity.Contract };
            Store.SaveRun(run); Store.RecordBiologicalEvent(asset, BiologicalEventKind.WaterEntry, 2, "Confirmed synthetic release", "Reviewer");
            Store.SaveProtocolWindow(asset, new() { ReleaseConfirmed = true, ReleaseFrame = 2, WindowConfirmed = true, RequestedExposureSeconds = 2, Reviewer = "Reviewer", Reason = "Matched requested exposure" }); Store.RecordReview(run, true);
            if (!Plan.Animals.Any(a => a.Id == animal)) Plan = Plan with { Animals = Plan.Animals.Add(new() { Id = animal, Alias = "Opaque", Group = "cohort", IdentityConfirmed = true, AllocationConfirmed = true }) };
            Plan = Plan with { Trials = Plan.Trials.Add(new() { Id = name, AnimalId = animal, ContextId = "scope", Order = order, RecordingId = asset.Id, SourceReferenceConfirmed = true, ReferenceSourceSha256 = asset.SourceSha256, ReferenceSettingsHash = StudyStore.SettingsHash(asset.Settings) }) };
            var plans = new StudyPlanStore(Store); plans.Save(Plan, "Reviewer", "Expected repeated source", plans.Latest()!.Revision);
        }
        public ExperimentResult Result() => ExperimentPlanAdapter.Build(AtlasSnapshot.Read(Store.DatabasePath), "scope");
        public TemporalStudy Profile() => TemporalProfiles.Build(Result());
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Folder, true); }
    }
    [Fact] public void FractionalPositionAndMotionExposureUseIndependentKnownDenominators()
    {
        using var fixture = new Fixture(); var result = fixture.Result(); Assert.Single(result.Study.Groups); Assert.Contains("design is unknown", result.Interpretation);
        var profile = fixture.Profile(); Assert.False(profile.ConnectWithinAnimal);
        var first = profile.TrialBins[0]; Assert.Equal(.125, first.KnownSeconds, 10); Assert.Equal(0d, first.UnknownFraction, 10);
        Assert.Equal(.125, first.MeanProximityDiameters!.Value, 10); Assert.Equal(.2, first.AdjacentSpeedDiametersPerSecond!.Value, 10);
        var second = profile.TrialBins[1]; Assert.Equal(.5, second.KnownSeconds, 10); Assert.Equal(.25, second.TargetOccupancyFraction!.Value, 10);
        Assert.Equal(.075, second.MeanProximityDiameters!.Value, 10); Assert.Equal(.2, second.AdjacentSpeedDiametersPerSecond!.Value, 10); Assert.Equal(0d, second.WallObservedFraction!.Value, 10);
        var last = profile.TrialBins[2]; Assert.Equal(1.375, last.KnownSeconds, 10); Assert.Equal(3d / 11, last.TargetOccupancyFraction!.Value, 10);
        Assert.Equal(12.75 / 110, last.MeanProximityDiameters!.Value, 10); Assert.Equal(6d / 11, last.WallObservedFraction!.Value, 10); Assert.Equal(1.125, last.AdjacentMotionSeconds, 10);
        Assert.Equal(EventHistory.ObservedExact, profile.Visits[0].History); Assert.Equal(.5, profile.Visits[0].ExactSeconds!.Value, 10);
        fixture.Unknown(3); profile = fixture.Profile(); first = profile.TrialBins[0]; second = profile.TrialBins[1];
        Assert.Null(first.AdjacentSpeedDiametersPerSecond); Assert.Equal(.125, first.MeanProximityDiameters!.Value, 10); Assert.Equal(0d, first.WallObservedFraction!.Value, 10);
        Assert.Equal(.5, second.UnknownFraction, 10); Assert.Equal(.25, second.KnownSeconds, 10); Assert.Equal(.125, second.AdjacentMotionSeconds, 10);
        Assert.Equal(.2, second.AdjacentSpeedDiametersPerSecond!.Value, 10); Assert.Equal(EventHistory.EarlierVisitPossibleInGap, profile.Visits[0].History); Assert.Null(profile.Visits[0].ExactSeconds); Assert.Null(profile.Visits[0].CensorSeconds);
    }
    [Fact] public void TypedOutsideAnnotationsRemainVisibleWithoutExclusionButDuringInterventionHolds()
    {
        using var fixture = new Fixture();
        fixture.Store.RecordBiologicalEvent(fixture.Asset, BiologicalEventKind.ResearcherIntervention, 1, "Before release", "Reviewer");
        fixture.Store.RecordBiologicalEvent(fixture.Asset, BiologicalEventKind.PlatformMounting, 11, "After planned probe", "Reviewer"); fixture.Store.RecordReview(fixture.Run, true);
        var result = fixture.Result(); Assert.Single(result.Study.Groups); Assert.Empty(result.Trials[0].Holds);
        Assert.Contains(result.Trials[0].Notes, n => n.Kind == ProtocolNoteKind.InterventionBeforeRelease); Assert.Contains(result.Trials[0].Notes, n => n.Kind == ProtocolNoteKind.MountingAfterWindow);
        var profile = fixture.Profile(); Assert.True(profile.TrialBins[0].EligibleObservation); Assert.Equal(2, profile.TrialBins[0].Notes.Length); Assert.Equal(2, profile.TrialBins[^1].EndSeconds);
        fixture.Store.RecordBiologicalEvent(fixture.Asset, BiologicalEventKind.ResearcherIntervention, 4, "During planned probe", "Reviewer"); fixture.Store.RecordReview(fixture.Run, true);
        result = fixture.Result(); Assert.Empty(result.Study.Groups); Assert.NotEmpty(result.Trials[0].Holds);
        profile = fixture.Profile(); Assert.All(profile.TrialBins, bin => Assert.False(bin.EligibleObservation)); Assert.All(profile.TrialBins, bin => Assert.Null(bin.MeanProximityDiameters));
        Assert.Equal(2, profile.TrialBins[^1].EndSeconds); Assert.Null(profile.Visits[0].ExactSeconds);
    }
    [Fact] public void CensoredAndTrainingTerminatedExposureAreNotSuccessfulOrUnknownZeroImputations()
    {
        using var fixture = new Fixture(); fixture.Window(TrialMode.Probe, new(.85, .5, .04));
        var profile = fixture.Profile(); Assert.Equal(EventHistory.RightCensoredComplete, profile.Visits[0].History); Assert.Null(profile.Visits[0].ExactSeconds); Assert.Equal(2, profile.Visits[0].CensorSeconds);
        fixture.Unknown(3); profile = fixture.Profile(); Assert.Equal(EventHistory.VisitHistoryIndeterminate, profile.Visits[0].History); Assert.Null(profile.Visits[0].ExactSeconds); Assert.Null(profile.Visits[0].CensorSeconds);
        using var training = new Fixture(); training.Window(TrialMode.Training, new(.6, .5, .04), 6); profile = training.Profile();
        Assert.Equal(1, profile.AnimalDays[0].EndSeconds); Assert.Equal(.5, profile.AnimalDays[0].Occupancy!.Value, 10);
        var last = profile.TrialBins[^1]; Assert.False(last.EligibleObservation); Assert.Null(last.TargetOccupancyFraction); Assert.Equal(0, last.UnknownFraction, 10); Assert.Equal(8d / 11, last.AbsentExposureFraction, 10);
        Assert.Contains(last.Holds, reason => reason.Contains("termination")); Assert.Equal(2, last.EndSeconds);
    }
    [Fact] public void TemporalMeansAverageTrialsWithinMouseBeforeAnEqualMouseSummary()
    {
        using var fixture = new Fixture(); fixture.Trial("QZ0", 2, "second", f => 58 + (f - 2) * 4); fixture.Trial("QZ1", 1, "third-source", _ => 60);
        var profile = fixture.Profile(); Assert.Equal(2, profile.AnimalDays.Length);
        var a = profile.AnimalDays.Single(p => p.AnimalId == "QZ0"); var b = profile.AnimalDays.Single(p => p.AnimalId == "QZ1");
        Assert.Equal(.13125, a.Proximity!.Value, 10); Assert.Equal(0, b.Proximity!.Value, 10); Assert.Equal(.065625, profile.AnimalDays.Average(p => p.Proximity!.Value), 10);
        Assert.Equal(.625, profile.AnimalDays.Average(p => p.Occupancy!.Value), 10); Assert.Equal(0, b.Speed!.Value, 10);
    }
}
