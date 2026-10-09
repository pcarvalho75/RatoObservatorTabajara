using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Batching;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using Xunit;
namespace MouseTrackerTabajara.Tests;

public sealed class NativeBatchCorrectionTests
{
    [Theory]
    [InlineData("unknown-coordinate")][InlineData("partial")][InlineData("wrong-frame")][InlineData("gap")][InlineData("timestamp")]
    [InlineData("total-count")][InlineData("tracked-count")][InlineData("coverage")][InlineData("duration")][InlineData("status-count")]
    public async Task InvalidEvidenceIsRejectedAtPublicationCacheAndRestart(string defect)
    {
        using var scratch=new Scratch();var store=new StudyStore(Path.Combine(scratch.Folder,"study.db"));var asset=Register(store,scratch.Folder,"source");
        var bad=Corrupt(Complete(asset),defect);var workspace=new WorkspaceService(store);
        var rejected=await workspace.AnalyzeAsync([asset],null,Request(new Decoder(_=>bad)));
        Assert.Equal(0,rejected.Completed);Assert.Single(rejected.Errors);Assert.Null(store.LatestRun(asset.Id));
        // Existing malformed raw evidence is preserved, not silently repaired or reused.
        store.SaveRun(bad);var original=JsonDefaults.Serialize(store.LatestRun(asset.Id)!);
        var restart=Assert.Single(workspace.ReadPersistedRecordingEvidence());
        Assert.False(restart.HistoricalRunIntact);Assert.False(restart.MatchingRunIntact);Assert.False(restart.CurrentReuseEligible);Assert.False(restart.LatestAttemptIsCompletedAnalysis);
        var calls=0;var repaired=await workspace.AnalyzeAsync([asset],null,Request(new Decoder(a=>{calls++;return Complete(a);})));
        Assert.Equal(1,calls);Assert.Equal(0,repaired.Reused);Assert.Equal(1,repaired.Completed);Assert.Contains(repaired.Warnings,w=>w.Contains("Invalid cached evidence"));
        using var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+store.DatabasePath);connection.Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT json FROM runs WHERE id=$id";command.Parameters.AddWithValue("$id",bad.Id);Assert.Equal(original,(string)command.ExecuteScalar()!);
        Assert.Contains(Assert.Single(workspace.ReadPersistedRecordingEvidence()).History,e=>e.State==RecordingAttempts.InvalidEvidence && e.RunId==bad.Id);
    }

    [Fact]
    public void IntervalRoundingBoundsAndMissingRowsUseTheTrackerContract()
    {
        var metadata=new VideoMetadata(200,200,10,12);var settings=new TrackingSettings{StartSeconds=.15,EndSeconds=.76};
        Assert.Equal((2,8),CompletedRecordingContract.Interval(metadata,settings));
        Assert.Throws<InvalidDataException>(()=>CompletedRecordingContract.Interval(metadata,settings with{StartSeconds=1.2,EndSeconds=null}));
        Assert.Throws<InvalidDataException>(()=>CompletedRecordingContract.Interval(metadata,settings with{StartSeconds=double.MaxValue,EndSeconds=null}));
        Assert.Equal((0,12),CompletedRecordingContract.Interval(metadata,new(){EndSeconds=double.MaxValue}));
        var asset=new VideoAsset{SourceSha256=new string('a',64),Metadata=metadata,Settings=settings};var run=Complete(asset);
        Assert.Equal(new[]{2,3,4,5,6,7},run.Points.Select(p=>p.Frame));
        run=run with{Points=run.Points.Select(p=>p with{X=null,Y=null,Status=PointStatus.Missing,CandidateCount=0}).ToList()};
        run=run with{Metrics=MetricCalculator.Calculate(run.Points,metadata,settings)};
        CompletedRecordingContract.Validate(run);Assert.Equal(0,run.Metrics.TrackedFrames);Assert.Equal(6,run.Metrics.TotalFrames);
        Assert.False(CompletedRecordingContract.TryValidate(run with{Points=run.Points.Skip(1).ToList()},out _));
    }

    [Fact]
    public async Task HistoricalCompletionRemainsTruthfulAfterSettingsAlgorithmAndSourceChanges()
    {
        using var scratch=new Scratch();var store=new StudyStore(Path.Combine(scratch.Folder,"study.db"));var asset=Register(store,scratch.Folder,"source");var workspace=new WorkspaceService(store);
        await workspace.AnalyzeAsync([asset],null,Request(new Decoder(Complete)));var saved=store.LatestRun(asset.Id)!;var blob=JsonDefaults.Serialize(saved);
        var edited=asset with{Settings=asset.Settings with{Threshold=35}};store.SaveVideo(edited);
        var history=Assert.Single(workspace.ReadPersistedRecordingEvidence());Assert.Equal(RecordingAttempts.Completed,history.LatestAttempt);Assert.True(history.LatestAttemptIsCompletedAnalysis);
        Assert.True(history.HistoricalRunIntact);Assert.Equal(saved.Id,history.HistoricalRunId);Assert.False(history.CurrentReuseEligible);Assert.Equal("settings_changed",history.ReuseIneligibilityReason);Assert.Null(history.Error);
        store.SaveVideo(asset);File.WriteAllText(asset.Path,"Changed synthetic source");
        var source=Assert.Single(workspace.ReadPersistedRecordingEvidence());Assert.True(source.HistoricalRunIntact);Assert.False(source.CurrentReuseEligible);Assert.Equal("source_changed",source.ReuseIneligibilityReason);
        Assert.Equal(blob,JsonDefaults.Serialize(store.LatestRun(asset.Id)!));
        var legacy=Complete(asset) with{AlgorithmContract="legacy-contract"};store.SaveRun(legacy);
        // No journal for this separate registered video: the historical raw row still remains complete.
        var other=Register(store,scratch.Folder,"legacy");store.SaveRun(Complete(other) with{AlgorithmContract="legacy-contract"});
        var old=Assert.Single(workspace.ReadPersistedRecordingEvidence(),e=>e.VideoId==other.Id);Assert.True(old.HistoricalRunIntact);Assert.True(old.LatestAttemptIsCompletedAnalysis);Assert.Equal("algorithm_changed",old.ReuseIneligibilityReason);
    }

    [Fact]
    public async Task FinalObserverErrorIsRetainedWithCommittedEvidence()
    {
        using var scratch=new Scratch();var store=new StudyStore(Path.Combine(scratch.Folder,"study.db"));var asset=Register(store,scratch.Folder,"source");var completedCalls=0;
        var observer=new InlineProgress<RecordingBatchProgress>(p=>{if(p.ItemState==BatchItemState.Completed && ++completedCalls==2)throw new InvalidOperationException("final-only observer error");});
        var workspace=new WorkspaceService(store);var request=Request(new Decoder(Complete)) with{RecordingProgress=observer};
        var result=await workspace.AnalyzeAsync([asset],null,request);
        Assert.Equal(2,completedCalls);Assert.Equal(1,result.Completed);Assert.Empty(result.Errors);Assert.Contains(result.Warnings,w=>w.Contains("final-only observer error"));
        Assert.Contains("final-only observer error",result.Recordings[0].ProgressObserverError);
        var reopened=Assert.Single(new WorkspaceService(new StudyStore(store.DatabasePath)).ReadPersistedRecordingEvidence());Assert.True(reopened.HistoricalRunIntact);Assert.Contains("final-only observer error",reopened.ProgressObserverError);
    }

    [Fact]
    public async Task FailedTerminalPersistsBeforeSiblingDrainsAndRetryRetainsHistory()
    {
        using var scratch=new Scratch();var store=new StudyStore(Path.Combine(scratch.Folder,"study.db"));var failed=Register(store,scratch.Folder,"failed");var sibling=Register(store,scratch.Folder,"blocked");
        using var release=new ManualResetEventSlim();using var failedEvent=new ManualResetEventSlim();var workspace=new WorkspaceService(store);
        var request=Request(new Decoder(a=>{if(a.Id==failed.Id)throw new InvalidOperationException("persistent failure");if(!release.Wait(15000))throw new TimeoutException();return Complete(a);})) with
        {MaxDegreeOfParallelism=2,RecordingProgress=new InlineProgress<RecordingBatchProgress>(p=>{if(p.VideoId==failed.Id && p.ItemState==BatchItemState.Failed)failedEvent.Set();})};
        var running=workspace.AnalyzeAsync([failed,sibling],null,request);
        try{Assert.True(failedEvent.Wait(10000));Assert.False(running.IsCompleted);var evidence=Assert.Single(workspace.ReadPersistedRecordingEvidence(),e=>e.VideoId==failed.Id);Assert.Equal(RecordingAttempts.Failed,evidence.LatestAttempt);Assert.Contains("persistent failure",evidence.Error);Assert.Contains(evidence.History,e=>e.State==RecordingAttempts.Failed);}
        finally{release.Set();}
        await running;await workspace.AnalyzeAsync([failed],null,Request(new Decoder(Complete)));await workspace.AnalyzeAsync([failed],null,Request(new Decoder(_=>throw new InvalidOperationException("should reuse"))));
        var restarted=Assert.Single(workspace.ReadPersistedRecordingEvidence(),e=>e.VideoId==failed.Id);Assert.Equal(RecordingAttempts.Reused,restarted.LatestAttempt);
        Assert.Contains(restarted.History,e=>e.State==RecordingAttempts.Failed && e.Error!.Contains("persistent failure"));
        Assert.True(restarted.History.Select(e=>e.AttemptId).Where(id=>id.Length>0).Distinct().Count()>=3);
    }

    [Theory(Timeout=120000)]
    [InlineData("failed-blocked")][InlineData("commit-blocked")]
    public async Task KilledProcessKeepsDurableAttemptsFailuresAndCommittedRuns(string mode)
    {
        var root=Repository();var folder=Path.Combine(root,"artifacts","p01-correction","crash",mode+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var helper=Path.Combine(root,"tests","MouseTrackerTabajara.CrashProbe","bin","Release","net10.0","MouseTrackerTabajara.CrashProbe.dll");Assert.True(File.Exists(helper));
        var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add(helper);start.ArgumentList.Add(folder);start.ArgumentList.Add(mode);
        using var child=Process.Start(start)!;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while(!File.Exists(Path.Combine(folder,"ready.json"))){if(child.HasExited)throw new InvalidOperationException("Probe exited before its ready receipt: "+await stderr);await Task.Delay(20,timeout.Token);}
            child.Kill(entireProcessTree:true);await child.WaitForExitAsync(timeout.Token);
        }
        finally{if(!child.HasExited){child.Kill(entireProcessTree:true);await child.WaitForExitAsync();}}
        File.WriteAllText(Path.Combine(folder,"child-stdout.txt"),await stdout);File.WriteAllText(Path.Combine(folder,"child-stderr.txt"),await stderr);
        var store=new StudyStore(Path.Combine(folder,"study.db"));var workspace=new WorkspaceService(store);var evidence=workspace.ReadPersistedRecordingEvidence();var before=store.Videos().Select(a=>store.LatestRun(a.Id)).Where(r=>r is not null).ToDictionary(r=>r!.VideoId,r=>JsonDefaults.Serialize(r!));
        if(mode=="failed-blocked")
        {
            var failed=store.Videos().Single(a=>a.Filename=="failed.bin");var blocked=store.Videos().Single(a=>a.Filename=="blocked.bin");
            Assert.Equal(RecordingAttempts.Failed,evidence.Single(e=>e.VideoId==failed.Id).LatestAttempt);Assert.Contains("Synthetic child decoder failure",evidence.Single(e=>e.VideoId==failed.Id).Error);
            Assert.Equal(RecordingAttempts.Interrupted,evidence.Single(e=>e.VideoId==blocked.Id).LatestAttempt);Assert.False(evidence.Single(e=>e.VideoId==blocked.Id).LatestAttemptIsCompletedAnalysis);
            Assert.Single(before);
        }
        else{Assert.Single(before);Assert.Equal(RecordingAttempts.Interrupted,Assert.Single(evidence).LatestAttempt);Assert.True(Assert.Single(evidence).HistoricalRunIntact);}
        var resumed=await workspace.AnalyzeAsync(store.Videos(),null,Request(new Decoder(Complete)));Assert.Empty(resumed.Errors);Assert.Equal(1,resumed.Reused);
        foreach(var pair in before)Assert.Equal(pair.Value,JsonDefaults.Serialize(store.LatestRun(pair.Key)!));
        if(mode=="failed-blocked")Assert.Contains(workspace.ReadPersistedRecordingEvidence().Single(e=>store.Videos().Single(v=>v.Id==e.VideoId).Filename=="failed.bin").History,e=>e.State==RecordingAttempts.Failed);
        File.WriteAllText(Path.Combine(folder,"verified.json"),JsonSerializer.Serialize(new{mode,resumed.Completed,resumed.Reused,childExit=child.ExitCode,rawPreserved=before.Count}));
    }

    [Fact]
    public async Task CurrentMalformedCacheDoesNotEraseEarlierHistoricalCompletion()
    {
        using var scratch=new Scratch();var store=new StudyStore(Path.Combine(scratch.Folder,"study.db"));var asset=Register(store,scratch.Folder,"source");var workspace=new WorkspaceService(store);
        await workspace.AnalyzeAsync([asset],null,Request(new Decoder(Complete)));var original=store.LatestRun(asset.Id)!;
        store.SaveRun(Corrupt(Complete(asset),"partial"));var evidence=Assert.Single(workspace.ReadPersistedRecordingEvidence());
        Assert.True(evidence.HistoricalRunIntact);Assert.Equal(original.Id,evidence.HistoricalRunId);Assert.True(evidence.LatestAttemptIsCompletedAnalysis);
        Assert.False(evidence.CurrentReuseEligible);Assert.Equal("invalid_stored_run",evidence.ReuseIneligibilityReason);Assert.NotNull(evidence.StoredRunValidationError);Assert.Null(evidence.Error);
    }

    [Fact]
    public async Task MissingAndUnreadableNamedRowsHaveDifferentRestartErrors()
    {
        using var scratch=new Scratch();var store=new StudyStore(Path.Combine(scratch.Folder,"study.db"));var asset=Register(store,scratch.Folder,"source");var workspace=new WorkspaceService(store);
        await workspace.AnalyzeAsync([asset],null,Request(new Decoder(Complete)));var id=store.LatestRun(asset.Id)!.Id;
        using var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+store.DatabasePath);connection.Open();using var command=connection.CreateCommand();
        command.CommandText="UPDATE runs SET json='{broken' WHERE id=$id";command.Parameters.AddWithValue("$id",id);command.ExecuteNonQuery();
        var corrupt=Assert.Single(workspace.ReadPersistedRecordingEvidence());Assert.False(corrupt.HistoricalRunIntact);Assert.Contains("unreadable",corrupt.Error);Assert.DoesNotContain("not stored",corrupt.Error);
        command.CommandText="DELETE FROM runs WHERE id=$id";command.ExecuteNonQuery();
        var missing=Assert.Single(workspace.ReadPersistedRecordingEvidence());Assert.False(missing.HistoricalRunIntact);Assert.Contains("not stored",missing.Error);
    }

    [Theory]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(double.NegativeInfinity)]
    public void NonfiniteFrameTimingAndMetricCoverageNeverValidate(double value)
    {
        var asset=new VideoAsset{SourceSha256=new string('a',64),Metadata=new(200,200,10,12),Settings=new(){}};var run=Complete(asset);
        Assert.False(CompletedRecordingContract.TryValidate(run with{Points=run.Points.Select((p,i)=>i==0?p with{TimeSeconds=value}:p).ToList()},out _));
        Assert.False(CompletedRecordingContract.TryValidate(run with{Metrics=run.Metrics with{CoveragePercent=value}},out _));
    }
    [Fact]
    public async Task DisposalWarningIsDurableWhileAnotherClipRemainsBlocked()
    {
        using var scratch=new Scratch();var store=new StudyStore(Path.Combine(scratch.Folder,"study.db"));var good=Register(store,scratch.Folder,"good");var blocked=Register(store,scratch.Folder,"blocked");
        using var release=new ManualResetEventSlim();var workspace=new WorkspaceService(store);
        var request=Request(new Decoder(a=>{if(a.Id==blocked.Id && !release.Wait(15000))throw new TimeoutException();return Complete(a);})) with
        {MaxDegreeOfParallelism=2,OpenResource=(a,_)=>ValueTask.FromResult<IAsyncDisposable?>(a.Id==good.Id?new BadDisposal():null)};
        var running=workspace.AnalyzeAsync([good,blocked],null,request);
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            PersistedRecordingEvidence evidence;
            do{await Task.Delay(20,timeout.Token);evidence=workspace.ReadPersistedRecordingEvidence().Single(e=>e.VideoId==good.Id);}while(evidence.DisposalError is null);
            Assert.False(running.IsCompleted);Assert.Contains("durable disposal failure",evidence.DisposalError);Assert.True(evidence.HistoricalRunIntact);
        }
        finally{release.Set();}
        var outcome=await running;Assert.Equal(2,outcome.Completed);Assert.Equal(0,outcome.ActiveDecodersAtCompletion);Assert.Contains(outcome.Warnings,w=>w.Contains("durable disposal failure"));
    }
    private sealed class BadDisposal:IAsyncDisposable{public ValueTask DisposeAsync()=>ValueTask.FromException(new IOException("durable disposal failure"));}
    [Fact]
    public async Task PublicationVerifiesTheStoredEvidenceRatherThanOnlyItsId()
    {
        using var scratch=new Scratch();var store=new StudyStore(Path.Combine(scratch.Folder,"study.db"));var asset=Register(store,scratch.Folder,"source");
        var request=Request(new Decoder(Complete)) with{Publisher=new CorruptPublisher(store)};
        var result=await new WorkspaceService(store).AnalyzeAsync([asset],null,request);
        Assert.Equal(0,result.Completed);Assert.Single(result.Errors);Assert.Contains("exact validated stored run",result.Errors[asset.Id]);
        Assert.Single(store.LatestRun(asset.Id)!.Points); // Preserve failed-publication evidence, never rewrite it.
        Assert.False(Assert.Single(new WorkspaceService(store).ReadPersistedRecordingEvidence()).HistoricalRunIntact);
    }
    private sealed class CorruptPublisher(StudyStore store):IRecordingRunPublisher
    {public ValueTask PublishAsync(AnalysisRun run,CancellationToken cancellation){store.SaveRun(Corrupt(run,"partial"));return ValueTask.CompletedTask;}}
    private static AnalysisRun Corrupt(AnalysisRun run,string defect)=>defect switch
    {
        "unknown-coordinate"=>UnknownCoordinates(run),
        "partial"=>run with{Points=run.Points.Take(1).ToList()},
        "wrong-frame"=>run with{Points=run.Points.Select((p,i)=>i==0?p with{Frame=99,TimeSeconds=9.9}:p).ToList()},
        "gap"=>run with{Points=run.Points.Select((p,i)=>i==1?p with{Frame=2,TimeSeconds=.2}:p).ToList()},
        "timestamp"=>run with{Points=run.Points.Select((p,i)=>i==1?p with{TimeSeconds=.2}:p).ToList()},
        "total-count"=>run with{Metrics=run.Metrics with{TotalFrames=11}},"tracked-count"=>run with{Metrics=run.Metrics with{TrackedFrames=11}},
        "coverage"=>run with{Metrics=run.Metrics with{CoveragePercent=50}},"duration"=>run with{Metrics=run.Metrics with{DurationSeconds=3}},
        "status-count"=>run with{Metrics=run.Metrics with{StatusCounts=new(){{"tracked",11}}}},_=>throw new ArgumentException(defect)
    };
    private static AnalysisRun UnknownCoordinates(AnalysisRun run)
    {
        var points=run.Points.Select((p,i)=>i==0?p with{Status=PointStatus.Missing,CandidateCount=0}:p).ToList();
        return run with{Points=points,Metrics=MetricCalculator.Calculate(points,run.Metadata,run.Settings)};
    }
    private static VideoAsset Register(StudyStore store,string folder,string name)
    {var path=Path.Combine(folder,name+".bin");File.WriteAllText(path,"Synthetic "+name);var asset=new VideoAsset{Path=path,SourceSha256=new Hasher().HashFile(path),Metadata=new(200,200,10,12),Settings=new(){Cx=.5,Cy=.5,Rx=.4,Ry=.4,ArenaConfirmed=true}};store.SaveVideo(asset);return asset;}
    private static AnalysisRun Complete(VideoAsset asset)
    {var first=(int)Math.Ceiling(asset.Settings.StartSeconds*asset.Metadata.Fps);var last=asset.Settings.EndSeconds is{}end?Math.Min(asset.Metadata.FrameCount,(int)Math.Ceiling(end*asset.Metadata.Fps)):asset.Metadata.FrameCount;var points=Enumerable.Range(first,last-first).Select(i=>new TrackPoint(i,i/asset.Metadata.Fps,30,100,PointStatus.Tracked,1)).ToList();return new(){VideoId=asset.Id,SourceSha256=asset.SourceSha256,Metadata=asset.Metadata,Settings=asset.Settings,AlgorithmContract=AlgorithmIdentity.Contract,NominalTimingVerified=true,Points=points,Metrics=MetricCalculator.Calculate(points,asset.Metadata,asset.Settings)};}
    private static NativeBatchRequest Request(IRecordingAnalysisDecoder decoder)=>new(){Decoder=decoder,Hasher=new Hasher(),MaxDegreeOfParallelism=1,ProgressMinimumInterval=TimeSpan.Zero};
    private sealed class Decoder(Func<VideoAsset,AnalysisRun> analyze):IRecordingAnalysisDecoder{public AnalysisRun Analyze(VideoAsset asset,string path,IProgress<double>? progress,CancellationToken cancellation)=>analyze(asset);}
    private sealed class Hasher:IRecordingSourceHasher{public string HashFile(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}}
    private static string Repository(){for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)if(File.Exists(Path.Combine(directory.FullName,"MouseTrackerTabajara.sln")))return directory.FullName;throw new DirectoryNotFoundException("Test repository not found.");}
}
