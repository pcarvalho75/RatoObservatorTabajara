using System.Security.Cryptography;
using System.Text.Json;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Batching;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

if(args.Length!=2)throw new ArgumentException("Synthetic probe requires its new evidence folder and mode.");
var folder=Path.GetFullPath(args[0]);Directory.CreateDirectory(folder);
var mode=args[1];var store=new StudyStore(Path.Combine(folder,"study.db"));
var state=new ProbeState(folder);var decoder=new ProbeDecoder(state);
VideoAsset Register(string name)
{
    var path=Path.Combine(folder,name+".bin");File.WriteAllText(path,"Synthetic crash probe "+name);
    var asset=new VideoAsset{Path=path,SourceSha256=new ProbeHasher().HashFile(path),Metadata=new(200,200,10,12),Settings=new(){Cx=.5,Cy=.5,Rx=.4,Ry=.4,ArenaConfirmed=true}};
    store.SaveVideo(asset);return asset;
}
var workspace=new WorkspaceService(store);
if(mode=="failed-blocked")
{
    var prior=Register("prior");await workspace.AnalyzeAsync([prior],null,new NativeBatchRequest{Hasher=new ProbeHasher(),Decoder=decoder,MaxDegreeOfParallelism=1});
    var failed=Register("failed");var blocked=Register("blocked");
    state.Info=new {prior=prior.Id,failed=failed.Id,blocked=blocked.Id,prior_run=store.LatestRun(prior.Id)!.Id};
    decoder.Block=true;
    await workspace.AnalyzeAsync([failed,blocked],null,new NativeBatchRequest{Hasher=new ProbeHasher(),Decoder=decoder,MaxDegreeOfParallelism=2,RecordingProgress=new ProbeProgress(state)});
}
else if(mode=="commit-blocked")
{
    var asset=Register("commit");state.Info=new{committed=asset.Id};
    await workspace.AnalyzeAsync([asset],null,new NativeBatchRequest{Hasher=new ProbeHasher(),Decoder=decoder,Publisher=new CommitThenBlock(store,state),MaxDegreeOfParallelism=1});
}
else throw new ArgumentException("Unknown synthetic probe mode.");

sealed class ProbeHasher:IRecordingSourceHasher
{public string HashFile(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}}
sealed class ProbeState(string folder)
{
    public object? Info;public int Failed;public int Blocked;private int _ready;
    public void Ready(bool force=false)
    {
        if(!force && (Volatile.Read(ref Failed)==0 || Volatile.Read(ref Blocked)==0))return;
        if(Interlocked.Exchange(ref _ready,1)!=0)return;
        File.WriteAllText(Path.Combine(folder,"ready.tmp"),JsonSerializer.Serialize(Info));File.Move(Path.Combine(folder,"ready.tmp"),Path.Combine(folder,"ready.json"));
    }
    public static void WaitForKill(){Thread.Sleep(TimeSpan.FromSeconds(60));throw new TimeoutException("Controller did not terminate its synthetic helper.");}
}
sealed class ProbeDecoder(ProbeState state):IRecordingAnalysisDecoder
{
    public bool Block;
    public AnalysisRun Analyze(VideoAsset asset,string path,IProgress<double>? progress,CancellationToken cancellation)
    {
        if(Block && Path.GetFileName(path)=="failed.bin")throw new InvalidDataException("Synthetic child decoder failure.");
        if(Block && Path.GetFileName(path)=="blocked.bin"){Volatile.Write(ref state.Blocked,1);state.Ready();ProbeState.WaitForKill();}
        var points=Enumerable.Range(0,12).Select(i=>new TrackPoint(i,i/10d,30,100,PointStatus.Tracked,1)).ToList();
        return new(){VideoId=asset.Id,SourceSha256=asset.SourceSha256,Metadata=asset.Metadata,Settings=asset.Settings,AlgorithmContract=AlgorithmIdentity.Contract,NominalTimingVerified=true,Points=points,Metrics=MetricCalculator.Calculate(points,asset.Metadata,asset.Settings)};
    }
}
sealed class ProbeProgress(ProbeState state):IProgress<RecordingBatchProgress>
{public void Report(RecordingBatchProgress progress){if(progress.ItemState==BatchItemState.Failed){Volatile.Write(ref state.Failed,1);state.Ready();}}}
sealed class CommitThenBlock(StudyStore store,ProbeState state):IRecordingRunPublisher
{public ValueTask PublishAsync(AnalysisRun run,CancellationToken cancellation){store.SaveRun(run);state.Ready(true);ProbeState.WaitForKill();return ValueTask.CompletedTask;}}
