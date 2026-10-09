using System.IO;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Validation.Tests;

public sealed class ThirtyRecordingPerformanceSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public void SmallRetainedWorkloadHasExactConstructedTruthAndReplayableInputs()
    {
        var folder=System.IO.Path.Combine(Directory.GetCurrentDirectory(),"artifacts","validation-performance-fixture","smoke-"+Guid.NewGuid().ToString("N"));
        var fixture=new ThirtyRecordingPerformanceFixture(folder,3,24); output.WriteLine("retained evidence="+folder);
        var repeat=new ThirtyRecordingPerformanceFixture(folder+"-repeat",3,24);
        output.WriteLine("retained deterministic repeat="+repeat.Folder);
        Assert.Equal(fixture.Receipts.Where(r=>r.Kind is "source" or "run" or "corrections" or "plan"),repeat.Receipts.Where(r=>r.Kind is "source" or "run" or "corrections" or "plan"));
        Assert.Equal(2,fixture.Workloads.Length); Assert.Equal(3,fixture.Receipts.Count(r=>r.Kind=="source"));
        foreach(var receipt in fixture.Receipts.Where(r=>r.Kind=="source"))
            Assert.Equal(receipt.Sha256,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(System.IO.Path.Combine(folder,receipt.Identity+".avi")))).ToLowerInvariant());
        foreach(var workload in fixture.Workloads)
        {
            Assert.True(workload.Plan.SyntheticToolingOnly); Assert.Equal(72,workload.Snapshot.Imported.Sum(a=>a.Metadata.FrameCount));
            Assert.Equal(3,workload.Plan.Recordings.Select(r=>r.Split).Distinct().Count());
            Assert.Equal(JsonDefaults.Serialize(workload.Sampling),JsonDefaults.Serialize(IndependentValidationSampling.Generate(workload.Snapshot,workload.Plan)));
            var replay=ThirtyRecordingPerformanceFixture.Read(folder,workload.Plan.MeasurementTarget);
            Assert.Equal(JsonDefaults.Serialize(workload),JsonDefaults.Serialize(replay));
            var report=IndependentValidationComparison.Compare(replay.Snapshot,replay.Plan,replay.Sampling);
            Assert.Equal(72,report.FullSourcePopulation); Assert.Equal(72,report.SampledUniqueFrames);
            foreach(var truth in replay.ExpectedFrames)
            {
                var actual=report.Frames.Single(f=>f.VideoId==truth.VideoId&&f.Frame==truth.Frame);
                Assert.Equal(truth.PositionErrorPixels,actual.PositionErrorPixels); Assert.Equal(truth.FalseAccepted,actual.FalseAccepted);
                Assert.Equal(truth.MissedVisible,actual.MissedVisible); Assert.Equal(truth.Disputed,actual.Disagreement);
                if(truth.MissingReference||truth.Disputed) Assert.NotEmpty(actual.Holds);
            }
            foreach(var truth in replay.ExpectedEndpoints)
            {
                var actual=report.Endpoints.Single(e=>e.VideoId==truth.VideoId&&e.StartFrame==truth.StartFrame&&e.EndFrame==truth.EndFrame);
                Assert.Equal(truth.PathDifferenceDiameters,actual.PathDifferenceDiameters);
                Assert.Equal(truth.SiteOccupancyDifference,actual.SiteOccupancyDifference);
                Assert.Equal(truth.WithinTolerances,actual.WithinTolerances);
                if(truth.WithinTolerances is null) Assert.NotEmpty(actual.Holds);
            }
            foreach(var receipt in fixture.Receipts.Where(r=>r.Identity==workload.Plan.MeasurementTarget.ToString()))
                Assert.Equal(receipt.Sha256,IndependentValidationSnapshot.Hash(File.ReadAllText(System.IO.Path.Combine(folder,receipt.Identity+"-"+receipt.Kind+".json"))));
            Assert.Contains(report.Holds,h=>h.Contains("Synthetic tooling"));
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(()=>IndependentValidationComparison.Compare(replay.Snapshot,replay.Plan,replay.Sampling,cancelled.Token));
            output.WriteLine($"target={workload.Plan.MeasurementTarget}; frames={report.SampledUniqueFrames}; position joined={report.PositionJoinedFrames}; max error={report.MaximumPositionErrorPixels}; plan={workload.Sampling.PlanFingerprint}; sample={IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(workload.Sampling))}");
        }
        Assert.Throws<IOException>(()=>new ThirtyRecordingPerformanceFixture(folder,3,24));
    }
}
