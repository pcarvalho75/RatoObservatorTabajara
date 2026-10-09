using System.Collections.Immutable;
using System.IO;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using OpenCvSharp;

namespace MouseTrackerTabajara.Validation.Tests;

public sealed record S14SplitTruth(int Recordings, int Population, int Sampled, int References, int PositionJoins, int RandomPositionJoins,
    int MissingReference, int NotJoined, int ToleranceFailures, int FalseAccepted, int ExcludedFrames, double Mean, double Maximum);

/// <summary>Declared synthetic truth; counts fixed independently before split-summary implementation.</summary>
public sealed class S14SplitBoundaryFixture : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "s14-split-boundary-" + Guid.NewGuid().ToString("N"));
    public StudyStore Store { get; }
    public ImmutableArray<VideoAsset> Assets { get; }
    public ImmutableArray<AnalysisRun> Runs { get; }
    public S14SplitBoundaryFixture()
    {
        Directory.CreateDirectory(Folder); Store = new(Path.Combine(Folder, "synthetic.morrisdb")); var workspace = new WorkspaceService(Store);
        var assets = ImmutableArray.CreateBuilder<VideoAsset>(); var runs = ImmutableArray.CreateBuilder<AnalysisRun>();
        // Names deliberately contradict declared roles; the frozen plan alone defines split membership.
        var names = new[] { "PRIVATE-independent-WT.avi", "PRIVATE-development-KO.avi", "PRIVATE-unspecified-A.avi", "PRIVATE-excluded-B.avi" };
        for (var recording = 0; recording < 4; recording++)
        {
            var count = recording == 0 ? 200 : 20; var path = Path.Combine(Folder, names[recording]);
            using (var writer = new VideoWriter(path, VideoWriter.FourCC('F','F','V','1'), 10, new OpenCvSharp.Size(120,100)))
            {
                if (!writer.IsOpened()) throw new InvalidOperationException("Synthetic encoder unavailable.");
                for (var frame = 0; frame < count; frame++)
                { using var image = new Mat(100,120,MatType.CV_8UC3,new Scalar(20+recording*25,50+frame%100,180-recording*20)); Cv2.Circle(image,new OpenCvSharp.Point(40,50),4,Scalar.Black,-1); writer.Write(image); }
            }
            var asset = workspace.Import(path) with { AnimalId = "PRIVATE-animal-" + recording, AnimalIdConfirmed = true, GroupName = recording % 2 == 0 ? "WT" : "KO",
                Included = recording != 3, ExclusionReason = recording == 3 ? "PRIVATE WT KO marked exclusion; retained" : "", Settings = new() { Cx=.5,Cy=.5,Rx=.4,Ry=.4,ArenaConfirmed=true,Platform=new(.5,.5,.04) } };
            Store.SaveVideo(asset); assets.Add(asset);
            var x = recording switch { 0 => 40, 1 => 50, 2 => 44, _ => 60 };
            var points = Enumerable.Range(0,count).Select(f => recording == 1 && f == 19 ? new TrackPoint(f,f/10d,null,null,PointStatus.Missing,0) : new TrackPoint(f,f/10d,x,50,PointStatus.Tracked,1)).ToList();
            var run = new AnalysisRun { VideoId=asset.Id,SourceSha256=asset.SourceSha256,Metadata=asset.Metadata,Settings=asset.Settings,Points=points,
                Metrics=MetricCalculator.Calculate(points,asset.Metadata,asset.Settings),AlgorithmContract=AlgorithmIdentity.Contract,NominalTimingVerified=true };
            Store.SaveRun(run); runs.Add(run);
            if (recording == 1) foreach (var f in Enumerable.Range(0,15).Append(19)) Store.SaveCorrection(run,new(run.Id,f,40,50,"Synthetic repaired output; not raw automatic accuracy",DateTime.UtcNow));
            if (recording == 3) for (var f=0;f<20;f++) Store.SaveCorrection(run,new(run.Id,f,40,50,"Synthetic excluded repair retained separately",DateTime.UtcNow));
        }
        Assets=assets.ToImmutable(); Runs=runs.ToImmutable();
        if (Assets.Select(a=>a.SourceSha256).Distinct().Count()!=4) throw new InvalidOperationException("Synthetic source bytes must differ.");
    }
    public IndependentValidationPlan Plan(ValidationMeasurementTarget target) => new()
    {
        Id="S14-"+target, SyntheticToolingOnly=true,MeasurementTarget=target,RandomFramesPerRecording=200,DifficultIntervalsPerRecording=100,IntervalFrames=400,SecondReviewFraction=1,Seed=14062026,
        Tolerances=new(3,2,.25,.1,.05,.8), Recordings=[
            new(Assets[0].Id,"PRIVATE-session-dev",ValidationSplit.Development,true,false,"synthetic-probe","WT"),
            new(Assets[1].Id,"PRIVATE-session-independent",ValidationSplit.IndependentValidation,false,true,"synthetic-probe","KO"),
            new(Assets[2].Id,"PRIVATE-session-unspecified",ValidationSplit.Unspecified,false,false,"synthetic-probe","WT"),
            new(Assets[3].Id,"PRIVATE-session-excluded",ValidationSplit.IndependentValidation,false,true,"synthetic-probe","KO")]
    };
    public (IndependentValidationPlan Plan, ValidationSampling Sampling, ValidationComparisonReport Report) Compare(ValidationMeasurementTarget target, ValidationSplit? only = null)
    {
        var plan=Plan(target);if(only.HasValue)plan=plan with {Id=plan.Id+"-"+only.Value,Recordings=plan.Recordings.Where(r=>r.Split==only.Value).ToImmutableArray()};var history=new ValidationHistoryStore(Store.DatabasePath);history.Save("plan",plan.Id,JsonDefaults.Serialize(plan),"S14 reviewer","Synthetic frozen roles and numerical tolerances",0);
        var sampling=IndependentValidationSampling.Generate(IndependentValidationSnapshot.Read(Store.DatabasePath),plan);
        history.Save("sampling",plan.Id,JsonDefaults.Serialize(sampling),"S14 reviewer","Full-population random sample plus duplicate-short-interval disclosure",0);
        var hash=IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(sampling));
        for(var recording=0;recording<4;recording++) for(var frame=0;frame<Assets[recording].Metadata.FrameCount;frame++)
        {
            if(!plan.Recordings.Any(r=>r.VideoId==Assets[recording].Id)||recording==1&&frame==15)continue;
            var visibility=recording==1&&frame==17?ReferenceVisibility.NotVisible:recording==1&&frame==18?ReferenceVisibility.Ambiguous:ReferenceVisibility.Visible;
            foreach(var lane in new[]{ReferenceReviewerLane.First,ReferenceReviewerLane.Second})
            {
                var x=visibility==ReferenceVisibility.Visible?(double?)(recording==1&&frame==16&&lane==ReferenceReviewerLane.Second?70:40):null;
                var reference=new IndependentReference(Assets[recording].Id,frame,lane,lane==ReferenceReviewerLane.First?"Independent first reviewer":"Independent second reviewer",visibility,x,x.HasValue?50:null,ReferenceEvent.None,"Synthetic independent truth; producer coordinates hidden",[]);
                reference.Validate(Assets[recording].Metadata);history.Save("reference",IndependentValidationComparison.ReferenceKey(hash,reference),JsonDefaults.Serialize(reference),reference.Reviewer,reference.Reason,0);
            }
        }
        return(plan,sampling,IndependentValidationComparison.Compare(IndependentValidationSnapshot.Read(Store.DatabasePath),plan,sampling));
    }
    public static ImmutableDictionary<ValidationSplit,S14SplitTruth> Truth(ValidationMeasurementTarget target) => new Dictionary<ValidationSplit,S14SplitTruth>
    {
        [ValidationSplit.Development]=new(1,200,200,200,200,200,0,0,0,0,0,0,0),
        [ValidationSplit.Unspecified]=new(1,20,20,20,20,20,0,0,20,0,0,4,4),
        [ValidationSplit.IndependentValidation]=target==ValidationMeasurementTarget.RawAutomatic?new(2,40,40,39,35,35,1,5,35,1,20,110d/7,20):new(2,40,40,39,36,36,1,4,0,1,20,0,0)
    }.ToImmutableDictionary();
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(Folder,true);}
}
