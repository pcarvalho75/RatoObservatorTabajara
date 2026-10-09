using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;

namespace MouseTrackerTabajara.Validation.Tests;

/// <summary>Explicit benchmark input generation; never invokes a tracking detector.
/// Retains all files, including incomplete generation attempts. No default large unit test.</summary>
public sealed class ThirtyRecordingPerformanceFixture
{
    public sealed record ExpectedFrame(string VideoId, int Frame, bool MissingReference, bool Disputed,
        bool NotVisible, double? PositionErrorPixels, bool? FalseAccepted, bool? MissedVisible);
    public sealed record ExpectedEndpoint(string VideoId, int StartFrame, int EndFrame,
        double? PathDifferenceDiameters, double? SiteOccupancyDifference, bool? WithinTolerances);
    public sealed record Workload(IndependentValidationSnapshot Snapshot, IndependentValidationPlan Plan,
        ValidationSampling Sampling, ImmutableArray<ExpectedFrame> ExpectedFrames, ImmutableArray<ExpectedEndpoint> ExpectedEndpoints);
    public sealed record Receipt(string Kind, string Identity, string Sha256);
    public string Folder { get; }
    public ImmutableArray<Workload> Workloads { get; }
    public ImmutableArray<Receipt> Receipts { get; }
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public ThirtyRecordingPerformanceFixture(string folder, int recordingCount = 30, int framesPerRecording = 1879)
    {
        if (recordingCount is < 1 or > 30 || framesPerRecording is < 24 or > 1879) throw new ArgumentOutOfRangeException(nameof(recordingCount));
        Folder = System.IO.Path.GetFullPath(folder);
        if (Directory.Exists(Folder)) throw new IOException("Use a fresh evidence folder; existing evidence is never overwritten.");
        Directory.CreateDirectory(Folder);
        File.WriteAllText(System.IO.Path.Combine(Folder, "generation-started.json"), JsonDefaults.Serialize(new { recordingCount, framesPerRecording, seed = 1602026, syntheticToolingOnly = true, producer = "constructed controls; no detector execution" }));
        var assets = ImmutableArray.CreateBuilder<VideoAsset>(); var runs = ImmutableArray.CreateBuilder<ValidationFrozenRun>();
        var receipts = ImmutableArray.CreateBuilder<Receipt>();
        for (var source = 0; source < recordingCount; source++)
        {
            var id = $"performance-source-{source:D2}"; var path = System.IO.Path.Combine(Folder, id + ".avi");
            using (var writer = new VideoWriter(path, VideoWriter.FourCC('F','F','V','1'), 10, new Size(64,64)))
            {
                if (!writer.IsOpened()) throw new IOException("Synthetic FFV1 writer unavailable.");
                for (var frame = 0; frame < framesPerRecording; frame++)
                { using var image = new Mat(64,64,MatType.CV_8UC3,new Scalar(20 + source * 5, 40 + frame % 160, 200)); writer.Write(image); }
            }
            var metadata = OpenCvTracker.Probe(path);
            if (metadata.FrameCount != framesPerRecording || metadata.Width != 64 || metadata.Height != 64 || metadata.Fps != 10) throw new InvalidDataException("Synthetic source population/geometry differs.");
            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            var asset = new VideoAsset { Id = id, Path = path, SourceSha256 = sha, Metadata = metadata,
                Settings = new() { Cx=.5, Cy=.5, Rx=.4, Ry=.4, ArenaConfirmed=true, Platform=new(.625,.5,.01) } };
            assets.Add(asset); receipts.Add(new("source",id,sha));
            var points = Enumerable.Range(0,framesPerRecording).Select(f => f % 13 == 0
                ? new TrackPoint(f,f/10d,null,null,PointStatus.Missing,0) : new TrackPoint(f,f/10d,42,32,PointStatus.Tracked,1)).ToList();
            var raw = new AnalysisRun { Id=id+"-constructed-run", VideoId=id, SourceSha256=sha, CreatedUtc=Epoch,
                AlgorithmContract=AlgorithmIdentity.Contract, SoftwareVersion="synthetic-constructed-performance-control-v1",
                Metadata=metadata, Settings=asset.Settings, Points=points, NominalTimingVerified=true,
                Metrics=MetricCalculator.Calculate(points,metadata,asset.Settings) };
            var corrections = Enumerable.Range(0,framesPerRecording).Select(f => JsonDefaults.Serialize(new ManualCorrection(raw.Id,f,40,32,"Constructed exact-center correction control; not an inspection claim",Epoch))).ToImmutableArray();
            var rawJson=JsonDefaults.Serialize(raw); var correctionHash=MaterialFingerprint.Hash(corrections);
            // Actual executing dependency receipt, distinct from the declared synthetic producer.
            var binary=IndependentValidationSnapshot.Hash(string.Join("\n",new[] { typeof(IndependentValidationComparison).Assembly,typeof(MetricCalculator).Assembly,typeof(OpenCvTracker).Assembly,typeof(ValidationHistoryStore).Assembly }
                .Select(a=>a.GetName().Name+":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))).ToLowerInvariant())));
            runs.Add(new(id,sha,JsonDefaults.Serialize(asset),rawJson,corrections,IndependentValidationSnapshot.Hash(rawJson),correctionHash,raw.AlgorithmContract,raw.SoftwareVersion,binary,[]));
            receipts.Add(new("run",id,IndependentValidationSnapshot.Hash(rawJson))); receipts.Add(new("corrections",id,correctionHash));
        }
        if (assets.Select(a=>a.SourceSha256).Distinct().Count()!=recordingCount) throw new InvalidDataException("Synthetic source bytes are not distinct.");
        var producer=IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(runs.ToImmutable()));
        var initial=new IndependentValidationSnapshot(System.IO.Path.Combine(Folder,"constructed-no-live-database"),producer,IndependentValidationSnapshot.Hash(""),runs.ToImmutable(),assets.ToImmutable(),[]);
        var workloads=ImmutableArray.CreateBuilder<Workload>();
        foreach (var target in new[] { ValidationMeasurementTarget.RawAutomatic,ValidationMeasurementTarget.InspectedCorrected })
        {
            var plan=new IndependentValidationPlan { Id="performance-"+target, SyntheticToolingOnly=true,MeasurementTarget=target,Seed=1602026,
                RandomFramesPerRecording=framesPerRecording,DifficultIntervalsPerRecording=3,IntervalFrames=10,SecondReviewFraction=1,
                Recordings=assets.Select((a,i)=>new ValidationRecording(a.Id,"synthetic-session-"+i,i%3==0?ValidationSplit.Development:i%3==1?ValidationSplit.IndependentValidation:ValidationSplit.Unspecified,i%3==0,i%3==1,"synthetic-declared-protocol","synthetic-allocation-"+(i%2))).ToImmutableArray() };
            var sampling=IndependentValidationSampling.Generate(initial,plan); var sampleHash=IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(sampling));
            var history=ImmutableArray.CreateBuilder<ValidationHistoryEntry>(); var expected=ImmutableArray.CreateBuilder<ExpectedFrame>();
            foreach(var key in sampling.Cases.SelectMany(c=>Enumerable.Range(c.StartFrame,c.EndFrame-c.StartFrame+1).Select(f=>(c.VideoId,Frame:f))).Distinct().OrderBy(k=>k.VideoId,StringComparer.Ordinal).ThenBy(k=>k.Frame))
            {
                var missing=key.Frame%19==0; var disputed=!missing&&key.Frame%23==0; var invisible=!missing&&!disputed&&key.Frame%17==0;
                if(!missing) foreach(var lane in new[] {ReferenceReviewerLane.First,ReferenceReviewerLane.Second})
                {
                    var reference=new IndependentReference(key.VideoId,key.Frame,lane,"synthetic-"+lane,invisible?ReferenceVisibility.NotVisible:ReferenceVisibility.Visible,invisible?null:disputed&&lane==ReferenceReviewerLane.Second?46:40,invisible?null:32,ReferenceEvent.None,"Constructed reference control; not held-out researcher evidence",[]);
                    reference.Validate(assets.Single(a=>a.Id==key.VideoId).Metadata); var json=JsonDefaults.Serialize(reference);
                    history.Add(new(history.Count+1,"reference",IndependentValidationComparison.ReferenceKey(sampleHash,reference),1,json,IndependentValidationSnapshot.Hash(json),reference.Reviewer,reference.Reason,Epoch));
                }
                var accepted=target==ValidationMeasurementTarget.InspectedCorrected||key.Frame%13!=0;
                expected.Add(new(key.VideoId,key.Frame,missing,disputed,invisible,
                    !missing&&!disputed&&!invisible&&accepted?target==ValidationMeasurementTarget.RawAutomatic?2:0:null,
                    !missing&&!disputed&&invisible?accepted:null,!missing&&!disputed&&!invisible?!accepted:null));
            }
            var snapshot=initial with { History=history.ToImmutable(),ReferenceFingerprint=IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(history.ToImmutable())) };
            // Independent closed-form endpoint truth: both trajectories are constant, so path difference is zero.
            // Reference x40 is inside the tiny platform and raw x42 is outside; corrected x40 is inside.
            // Any unresolved/invisible reference or raw rejected row holds a continuous endpoint.
            var endpoints=sampling.Cases.Where(c=>!c.RandomSample).Select(c=>
            {
                var rows=expected.Where(e=>e.VideoId==c.VideoId&&e.Frame>=c.StartFrame&&e.Frame<=c.EndFrame).ToArray();
                var held=rows.Any(e=>e.MissingReference||e.Disputed||e.NotVisible||e.MissedVisible==true);
                return new ExpectedEndpoint(c.VideoId,c.StartFrame,c.EndFrame,held?null:0,held?null:target==ValidationMeasurementTarget.RawAutomatic?1:0,held?null:target==ValidationMeasurementTarget.InspectedCorrected);
            }).ToImmutableArray();
            var workload=new Workload(snapshot,plan,sampling,expected.ToImmutable(),endpoints); workloads.Add(workload);
            foreach(var material in new[] { ("plan",JsonDefaults.Serialize(plan)),("sampling",JsonDefaults.Serialize(sampling)),("references",JsonDefaults.Serialize(history.ToImmutable())),("projection",JsonDefaults.Serialize(new { frames=expected.ToImmutable(),endpoints })),("inputs",JsonDefaults.Serialize(workload)) })
            { File.WriteAllText(System.IO.Path.Combine(Folder,target+"-"+material.Item1+".json"),material.Item2);receipts.Add(new(material.Item1,target.ToString(),IndependentValidationSnapshot.Hash(material.Item2))); }
        }
        Workloads=workloads.ToImmutable();Receipts=receipts.ToImmutable();
        File.WriteAllText(System.IO.Path.Combine(Folder,"manifest.json"),JsonDefaults.Serialize(Receipts));
    }

    public static Workload Read(string folder, ValidationMeasurementTarget target) => JsonDefaults.Deserialize<Workload>(File.ReadAllText(System.IO.Path.Combine(folder,target+"-inputs.json")));
}

