using System.IO;
using System.Collections.Immutable;
using System.Text.Json;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Stability.Tests;

public sealed class CompletenessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("repeat")]
    [InlineData("animal")]
    [InlineData("missing")]
    [InlineData("scenario")]
    [InlineData("held")]
    public void UnchangedProducerInputsRejectOmittedPlannedCases(string omission)
    {
        var fixture = new SupportedWindowTests.Fixture(); var report = RegionStabilityAdapter.Read(fixture.EvidencePath, fixture.Store.DatabasePath).Report;
        var folder = Path.Combine(fixture.Folder, "omitted-" + omission); StabilityReportWriter.Save(folder, report, false);
        var smaller = report with { Trials = report.Trials.Where(row => omission switch
        {
            "repeat" => row.TrialId != "trial1",
            "animal" => row.AnimalId != "outlier",
            "missing" => row.TrialId != "missing-slot",
            "scenario" => row.Scenario != "large-shift",
            _ => row.Scenario != "invalid-extent"
        }).ToImmutableArray() };
        Assert.Equal(JsonSerializer.Serialize(report.Inputs), JsonSerializer.Serialize(smaller.Inputs));
        var path = Path.Combine(folder, "stability-input.json"); File.WriteAllText(path, JsonSerializer.Serialize(smaller));
        var failure = Assert.Throws<InvalidDataException>(() => StabilitySavedReplay.Read(path));
        Assert.Contains("incomplete", failure.Message);
        output.WriteLine($"Omission={omission}; rejected={smaller.Trials.Length}/{report.Trials.Length}; evidence={folder}");
    }
    [Theory] [InlineData("extra")] [InlineData("duplicate")] [InlineData("missing-identity")] [InlineData("missing-scope")]
    public void ExtraDuplicateAndMismatchedNullCaseIdentitiesAreRejected(string mutation)
    {
        var fixture = new SupportedWindowTests.Fixture(); var report = RegionStabilityAdapter.Read(fixture.EvidencePath, fixture.Store.DatabasePath).Report;
        var missing = report.Trials.First(t => t.TrialId == "missing-slot"); Assert.Null(missing.Changed.Value);
        var changed = mutation switch
        {
            "extra" => report with { Trials = report.Trials.Add(missing with { CaseId = "unexpected-case" }) },
            "duplicate" => report with { Trials = report.Trials.Add(missing) },
            "missing-scope" => report with { Trials = report.Trials.Replace(missing, missing with { Scope = missing.Scope with { Context = "relocated-missing", Day = 99 }, OriginalScope = missing.OriginalScope! with { Context = "relocated-missing", Day = 99 } }) },
            _ => report with { Trials = report.Trials.Replace(missing, missing with { TrialId = "forged-null-slot", AnimalId = "forged-animal" }) }
        };
        Assert.Throws<InvalidDataException>(() => RegionProducerReconstruction.VerifySavedScalars(changed));
    }
    [Fact] public void MultipleRawProducerInputsRequireEveryRecordedAndUnrunCaseAndPermitIdenticalReceiptDuplicates()
    {
        var first = Raw("raw-first", 120); var second = Raw("raw-second", 170);
        var combined = ResultStability.Build(first.Trials.Concat(second.Trials)) with { Inputs = first.Inputs.AddRange(second.Inputs) };
        RegionProducerReconstruction.VerifySavedScalars(combined); Assert.Equal(8, combined.Trials.Length);
        RegionProducerReconstruction.VerifySavedScalars(combined with { Inputs = combined.Inputs.Add(first.Inputs.First(i => i.Kind.StartsWith("region-"))) });
        foreach (var report in new[] { combined with { Trials = first.Trials }, combined with { Trials = combined.Trials.RemoveAt(0) }, combined with { Trials = combined.Trials.Where(t => !t.TrialId.Contains("norun")).ToImmutableArray() } })
            Assert.Throws<InvalidDataException>(() => RegionProducerReconstruction.VerifySavedScalars(report));
    }
    private static StabilityReport Raw(string id, int background)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-completeness-raw-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); var store = new StudyStore(Path.Combine(folder, "study.db"));
        for (var index = 0; index < 2; index++)
        {
            var path = Path.Combine(folder, "source" + index + ".avi");
            using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new Size(160, 120)))
            {
                Assert.True(writer.IsOpened()); for (var frame = 0; frame < 10; frame++) { using var image = new Mat(120, 160, MatType.CV_8UC3, new Scalar(background + index * 5, 180, 200)); Cv2.Circle(image, new(50 + frame, 60), 7, Scalar.Black, -1); writer.Write(image); }
            }
            var asset = new VideoAsset { Id = id + (index == 0 ? "" : "-norun"), Path = path, SourceSha256 = OpenCvTracker.HashFile(path), Metadata = OpenCvTracker.Probe(path), Settings = new() { Cx = .5, Cy = .5, Rx = .45, Ry = .4, Threshold = 40, Blur = 1, MinArea = 20, MaxArea = 5000, ArenaConfirmed = true, Platform = new(.5, .5, .06) } };
            store.SaveVideo(asset); if (index == 0) store.SaveRun(OpenCvTracker.Analyze(asset));
        }
        var snapshot = AtlasSnapshot.Read(store.DatabasePath); var identity = RegionScenarioBaseline.Capture(snapshot, null); var material = RegionScenarioBaseline.Material(snapshot, null);
        ImmutableArray<NamedRegionScenario> scenarios = [new() { Name = "shift", Rationale = "Prespecified synthetic center uncertainty", Kind = RegionScenarioKind.PlatformCenterExtent, CenterDeltaX = 1, ComponentRange = new(DisclosedUnit.SourcePixels, -1, 1) }, new() { Name = "unrun-mask", Rationale = "Detection-affecting component held", Kind = RegionScenarioKind.PoolDetectionMaskUnrun, ComponentRange = new(DisclosedUnit.SourcePixels, -1, 1) }];
        var recordings = snapshot.Recordings.Select(row => row.EffectiveRun() is { } run ? new RecordingScenarioComputation { VideoId = row.Asset.Id, Set = RegionScenarioMath.Evaluate(run.Points, run.Metadata, run.Settings, scenarios, RegionScenarioBaseline.Header(identity, identity.Sources.Single(s => s.VideoId == row.Asset.Id))) } : new RecordingScenarioComputation { VideoId = row.Asset.Id, RecordingHolds = ["No completed raw run"] }).ToImmutableArray();
        var computation = new RegionScenarioComputation { Identity = identity, Scenarios = scenarios, Recordings = recordings, Replay = material }; var json = JsonDefaults.Serialize(computation); var materialJson = JsonDefaults.Serialize(material);
        static string Hash(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
        var pathEvidence = RegionScenarioEvidenceStore.Append(folder, new() { Identity = identity, ComputationJson = json, ComputationSha256 = Hash(json), FrozenMaterialJson = materialJson, FrozenMaterialSha256 = Hash(materialJson) });
        return RegionStabilityAdapter.Read(pathEvidence, store.DatabasePath).Report;
    }
}
