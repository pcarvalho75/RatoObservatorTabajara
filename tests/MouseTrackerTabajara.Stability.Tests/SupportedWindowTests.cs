using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Stability.Tests;

public sealed class SupportedWindowTests(ITestOutputHelper output)
{
    internal sealed class Fixture
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "tabajara-supported-stability-" + Guid.NewGuid().ToString("N"));
        public StudyStore Store { get; }
        public string EvidencePath { get; }
        public RegionScenarioEvidenceDocument Document { get; }
        public Fixture(bool unknown = false)
        {
            Directory.CreateDirectory(Folder); Store = new(Path.Combine(Folder, "study.db"));
            var context = new PlannedContext { Id = "probe-day1", Phase = TrialMode.Probe, Day = 1, RequestedExposureSeconds = .8, ComparisonStartSeconds = .05, ComparisonDurationSeconds = .65,
                WindowConfirmed = true, AlignmentConfirmed = true, CommonCoordinateReference = "Synthetic common room/pool reference", Orientation = StudyOrientation.Room };
            ImmutableArray<PlannedAnimal> animals = [new() { Id = "A1", Group = "WT", IdentityConfirmed = true, AllocationConfirmed = true }, new() { Id = "outlier", Group = "WT", IdentityConfirmed = true, AllocationConfirmed = true },
                new() { Id = "B1", Group = "KO", IdentityConfirmed = true, AllocationConfirmed = true }, new() { Id = "missing", Group = "WT", IdentityConfirmed = true, AllocationConfirmed = true }];
            var trials = ImmutableArray.CreateBuilder<PlannedTrial>();
            for (var index = 0; index < 4; index++)
            {
                var path = Path.Combine(Folder, "synthetic-" + index + ".avi");
                int X(int frame) => index < 2 ? 58 : index == 2 ? 42 : frame < 6 ? 58 : 90;
                bool Missing(int frame) => unknown && index == 3 && frame is 3 or 4;
                using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new Size(160, 120)))
                {
                    Assert.True(writer.IsOpened()); for (var frame = 0; frame < 10; frame++) { using var image = new Mat(120, 160, MatType.CV_8UC3, new Scalar(160 + index * 10, 180, 200)); if (!Missing(frame)) Cv2.Circle(image, new(X(frame), 60), 7, new Scalar(5, 5, 5), -1); writer.Write(image); }
                }
                var animal = index < 2 ? animals[0] : index == 2 ? animals[1] : animals[2]; var order = index == 1 ? 2 : 1;
                var asset = new VideoAsset { Id = "source" + index, Path = path, SourceSha256 = OpenCvTracker.HashFile(path), Metadata = OpenCvTracker.Probe(path), AnimalId = animal.Id, AnimalIdConfirmed = true, GroupName = animal.Group,
                    Phase = TrialMode.Probe, Day = 1, Trial = order, Settings = new() { Cx = .5, Cy = .5, Rx = .45, Ry = .4, Threshold = 40, Blur = 1, MinArea = 20, MaxArea = 5000, MaxJump = 70, ArenaConfirmed = true, Platform = new(50d / 160, .5, .06) } };
                Store.SaveVideo(asset); var run = OpenCvTracker.Analyze(asset); Assert.Equal(10, run.Points.Count);
                foreach (var point in run.Points) { if (Missing(point.Frame)) Assert.False(point.Accepted); else { Assert.True(point.Accepted); Assert.Equal(X(point.Frame), point.X); Assert.Equal(60, point.Y); } }
                Store.SaveRun(run); Store.RecordBiologicalEvent(asset, BiologicalEventKind.WaterEntry, 1, "Synthetic independently encoded release frame", "Synthetic reviewer");
                Store.SaveProtocolWindow(asset, new() { RequestedExposureSeconds = .8, ReleaseConfirmed = true, ReleaseFrame = 1, WindowConfirmed = true, Reviewer = "Synthetic reviewer", Reason = "Prespecified synthetic probe exposure" });
                Store.RecordReview(run, true); // Synthetic inspection follows the independent per-frame native coordinate assertions above.
                trials.Add(new() { Id = "trial" + index, AnimalId = animal.Id, ContextId = context.Id, Order = order, RecordingId = asset.Id, SourceReferenceConfirmed = true, ReferenceSourceSha256 = asset.SourceSha256, ReferenceSettingsHash = StudyStore.SettingsHash(asset.Settings) });
            }
            trials.Add(new() { Id = "missing-slot", AnimalId = "missing", ContextId = context.Id, MissingReason = "Prespecified synthetic source not acquired" });
            var plan = new StudyPlan { Animals = animals, Contexts = [context], Trials = trials.ToImmutable(), RosterConfirmed = true, DesignConfirmed = true, UnitDesign = StudyUnitDesign.RepeatedTrialsWithinAnimals, Exploratory = true };
            new StudyPlanStore(Store).Save(plan, "Synthetic reviewer", "Prespecified independent fixture", 0);
            var snapshot = AtlasSnapshot.Read(Store.DatabasePath); var identity = RegionScenarioBaseline.Capture(snapshot, null); var material = RegionScenarioBaseline.Material(snapshot, null);
            ImmutableArray<NamedRegionScenario> scenarios = [new() { Name = "small-shift", Rationale = "Prespecified1px measured center uncertainty", Kind = RegionScenarioKind.PlatformCenterExtent, CenterDeltaX = 1, ComponentRange = new(DisclosedUnit.SourcePixels, -20, 20) },
                new() { Name = "large-shift", Rationale = "Prespecified10px center variation; adverse results retained", Kind = RegionScenarioKind.PlatformCenterExtent, CenterDeltaX = 10, ComponentRange = new(DisclosedUnit.SourcePixels, -20, 20) },
                new() { Name = "invalid-extent", Rationale = "Prespecified adverse geometry bound; invalid fit retained", Kind = RegionScenarioKind.PlatformCenterExtent, ExtentDelta = -20, ComponentRange = new(DisclosedUnit.SourcePixels, -20, 20) },
                new() { Name = "mask-unrun", Rationale = "Detection-affecting component remains explicitly unrun", Kind = RegionScenarioKind.PoolDetectionMaskUnrun, ComponentRange = new(DisclosedUnit.SourcePixels, -20, 20) }];
            var recordings = snapshot.Recordings.Select(row =>
            {
                var run = row.EffectiveRun()!; var set = RegionScenarioMath.Evaluate(run.Points, run.Metadata, run.Settings, scenarios, RegionScenarioBaseline.Header(identity, identity.Sources.Single(s => s.VideoId == row.Asset.Id)));
                var protocol = ExperimentPlanAdapter.Protocol(snapshot, row.Asset.Id); var window = ReleasedComparisonWindow.Resolve(protocol, context, run.Metadata, ExperimentPlanAdapter.EffectiveEvents(snapshot, row.Asset.Id), row.Asset.Phase, ExperimentPlanAdapter.CurrentInspection(row));
                Assert.True(window.Supported, window.Hold);
                var windows = ImmutableArray.CreateBuilder<WindowedTrialMeasurement>(); windows.Add(RegionScenarioComparison.Measure(run.Points, run.Metadata, run.Settings, window, "original"));
                foreach (var outcome in set.Scenarios) windows.Add(outcome.Held ? WindowedTrialMeasurement.Held(outcome.Name, outcome.Holds.First()) : RegionScenarioComparison.Measure(run.Points, run.Metadata, outcome.MeasurementSettings!, window, outcome.Name));
                return new RecordingScenarioComputation { VideoId = row.Asset.Id, Set = set, Windows = windows.ToImmutable() };
            }).ToImmutableArray();
            var computation = new RegionScenarioComputation { Identity = identity, Scenarios = scenarios, Recordings = recordings, Replay = material };
            var json = JsonDefaults.Serialize(computation); var materialJson = JsonDefaults.Serialize(material);
            Document = new() { Identity = identity, ComputationJson = json, ComputationSha256 = Digest(json), FrozenMaterialJson = materialJson, FrozenMaterialSha256 = Digest(materialJson) };
            EvidencePath = RegionScenarioEvidenceStore.Append(Folder, Document);
        }
        private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
    [Fact] public void ActualFrozenSupportedWindowsShowStableAndNamedOutlierReversalWithMissingAndAdverseCases()
    {
        var fixture = new Fixture(); var evidence = RegionStabilityAdapter.Read(fixture.EvidencePath, fixture.Store.DatabasePath); Assert.Equal(20, evidence.Report.Trials.Length);
        var stable = evidence.Report.Contrasts.Single(c => c.Scenario == "small-shift"); var reversal = evidence.Report.Contrasts.Single(c => c.Scenario == "large-shift");
        Assert.Contains("Stable", stable.Conclusion); Assert.Contains("Unstable", reversal.Conclusion); Assert.Equal(-.2, stable.Original.Value!.Value, 8); Assert.Equal(.125, reversal.Changed.Value!.Value, 8);
        Assert.Equal(4, stable.PlannedAnimals); Assert.Equal(3, stable.IncludedAnimals); Assert.Equal(1, stable.HeldAnimals);
        var outlier = reversal.Animals.Single(a => a.AnimalId == "outlier"); Assert.Equal(.65, outlier.Original.Value!.Value, 8); Assert.Equal(0, outlier.Changed.Value); Assert.Equal(2, reversal.Animals.Single(a => a.AnimalId == "A1").PlannedTrials);
        Assert.All(evidence.Report.Trials.Where(t => t.Scenario is "invalid-extent" or "mask-unrun" || t.AnimalId == "missing"), t => Assert.NotEmpty(t.Holds));
        Assert.All(evidence.Report.Trials.Where(t => t.Supported), t => { Assert.Equal(.65, t.PlannedSeconds!.Value, 8); Assert.Contains("release + 0.05", t.Scope.Window); Assert.Equal(10, t.AnalyzedFrames); });
        output.WriteLine("Supported fixture: " + fixture.Folder);
    }
    [Fact] public void ActualUnknownMassCrossesZeroWithoutBecomingSamplingUncertainty()
    {
        var fixture = new Fixture(true); var evidence = RegionStabilityAdapter.Read(fixture.EvidencePath, fixture.Store.DatabasePath); var contrast = evidence.Report.Contrasts.Single(c => c.Scenario == "large-shift");
        Assert.Contains("unresolved", contrast.Conclusion); Assert.True(contrast.Changed.Lower < 0); Assert.True(contrast.Changed.Upper > 0); Assert.Equal(.2, evidence.Report.Trials.First(t => t.AnimalId == "B1").UnknownSeconds!.Value, 8);
        output.WriteLine("Unknown-mass fixture: " + fixture.Folder);
    }
    [Fact] public void SupportedSavedReplayAuthenticatesScalarsBoundsAndMatchedPlanInsteadOfTrustingRows()
    {
        var fixture = new Fixture(); var evidence = RegionStabilityAdapter.Read(fixture.EvidencePath, fixture.Store.DatabasePath);
        var folder = Path.Combine(fixture.Folder, "full-report"); StabilityReportWriter.Save(folder, evidence.Report, false); var replay = StabilitySavedReplay.Read(Path.Combine(folder, "stability-input.json"));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(evidence.Report), System.Text.Json.JsonSerializer.Serialize(replay.Report));
        var index = Array.FindIndex(evidence.Report.Trials.ToArray(), t => t.Supported && t.Scenario == "large-shift");
        var row = evidence.Report.Trials[index];
        foreach (var tampered in new[] { row with { Changed = row.Changed with { Value = .123 } }, row with { Changed = row.Changed with { Upper = .321 } }, row with { Scope = row.Scope with { Unit = "cm" } }, row with { Scope = row.Scope with { Window = "shortened favorable interval" } }, row with { BaselineIdentity = row.BaselineIdentity with { Window = new string('0',64) } }, row with { Group = "forged allocation" } })
            Assert.Throws<InvalidDataException>(() => { RegionProducerReconstruction.VerifySavedScalars(evidence.Report with { Trials = evidence.Report.Trials.SetItem(index, tampered) }); });
        output.WriteLine("Authenticated supported report: " + folder);
    }
    [Fact] public void ActualSourceAndCodeChangesRetainCasesOrHoldReconstructionWithoutFakingEligibility()
    {
        var fixture = new Fixture(); var before = RegionStabilityAdapter.Read(fixture.EvidencePath, fixture.Store.DatabasePath);
        var source = fixture.Store.Videos().First(); using (var stream = new FileStream(source.Path, FileMode.Append, FileAccess.Write)) stream.WriteByte(0);
        Assert.False(before.IsCurrent()); var sourceHeld = RegionStabilityAdapter.Read(fixture.EvidencePath, fixture.Store.DatabasePath); Assert.Equal(20, sourceHeld.Report.Trials.Length);
        Assert.All(sourceHeld.Report.Trials.Where(t => t.BaselineIdentity.Source == source.SourceSha256), t => { Assert.False(t.Supported); Assert.Null(t.Trace); });
        var alteredImplementation = fixture.Document.Identity.Implementation with { SourceMaterialSha256 = new string('0', 64) }; alteredImplementation = alteredImplementation with { CanonicalSha256 = ScenarioImplementationProbe.Canonical(alteredImplementation) };
        Assert.NotNull(ScenarioImplementationProbe.ReplayHold(alteredImplementation));
        Assert.Throws<InvalidDataException>(() => RegionProducerReconstruction.Reconstruct(fixture.Document with { Identity = fixture.Document.Identity with { Implementation = alteredImplementation } }));
    }
    [Fact] public void ChangedBiologicalEventHistoryMarksCurrentResultStaleButKeepsAuthenticatedFrozenHistory()
    {
        var fixture = new Fixture(); var evidence = RegionStabilityAdapter.Read(fixture.EvidencePath, fixture.Store.DatabasePath);
        var folder = Path.Combine(fixture.Folder, "before-event-edit"); StabilityReportWriter.Save(folder, evidence.Report, false);
        var asset = fixture.Store.Videos().First(); fixture.Store.RecordBiologicalEvent(asset, BiologicalEventKind.ResearcherIntervention, 4, "Synthetic adverse intervention added after frozen report", "Synthetic reviewer");
        Assert.False(evidence.IsCurrent()); var historical = RegionStabilityAdapter.Read(fixture.EvidencePath, fixture.Store.DatabasePath); Assert.Equal(20, historical.Report.Trials.Length); Assert.All(historical.Report.Trials, t => Assert.False(t.Supported));
        Assert.Contains(historical.Report.Trials, t => t.Holds.Any(h => h.Contains("Historical")));
        var replay = StabilitySavedReplay.Read(Path.Combine(folder, "stability-input.json")); Assert.Equal(3, replay.Report.Contrasts.Single(c => c.Scenario == "large-shift").IncludedAnimals);
        var material = JsonDefaults.Deserialize<FrozenReplayMaterial>(fixture.Document.FrozenMaterialJson); var computation = JsonDefaults.Deserialize<RegionScenarioComputation>(fixture.Document.ComputationJson);
        var changedRecording = material.Recordings[0] with { Events = [] }; var changedMaterial = material with { Recordings = material.Recordings.SetItem(0, changedRecording) };
        var changedMaterialJson = JsonDefaults.Serialize(changedMaterial); var changedComputationJson = JsonDefaults.Serialize(computation with { Replay = changedMaterial });
        static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        var tampered = fixture.Document with { FrozenMaterialJson = changedMaterialJson, FrozenMaterialSha256 = Digest(changedMaterialJson), ComputationJson = changedComputationJson, ComputationSha256 = Digest(changedComputationJson) };
        Assert.Throws<InvalidDataException>(() => RegionProducerReconstruction.Reconstruct(tampered));
    }
}
