using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
namespace MouseTrackerTabajara.Application;

public sealed record RecordingAttemptEvidence(string AttemptId, string State, string? RunId, string? Error, string? Warning, string? DisposalError, string? ProgressObserverError, DateTime RecordedUtc);
internal sealed record JournalEntry
{
    public string AttemptId {get;set;}="";
    public string BatchId {get;set;}="";
    public string VideoId {get;set;}="";
    public string SourceSha256 {get;set;}="";
    public string SettingsHash {get;set;}="";
    public string AlgorithmContract {get;set;}="";
    public string Terminal {get;set;}="";
    public string? RunId {get;set;}
    public string? Error {get;set;}
    public string? Warning {get;set;}
    public string? DisposalError {get;set;}
    public string? ProgressObserverError {get;set;}
    public int OwnerProcessId {get;set;}
    public long OwnerStartUtcTicks {get;set;}
    public DateTime RecordedUtc {get;set;}
}

internal sealed class RecordingOperationJournal
{
    private const int Schema=1;
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private static readonly object GateSync=new();
    private static readonly Dictionary<string,object> Gates=new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    public RecordingOperationJournal(string databasePath)=>_path=Path.GetFullPath(databasePath)+".recording-operations.json";
    private static object Gate(string path){lock(GateSync){if(!Gates.TryGetValue(path,out var gate))Gates[path]=gate=new();return gate;}}
    public void Add(JournalEntry entry)=>AddRange([entry]);
    public void AddRange(IReadOnlyList<JournalEntry> entries)
    {
        if(entries.Count==0)return;
        lock(Gate(_path))
        {
            var document=LoadUnlocked();
            document.Entries.AddRange(entries);
            // Every transition is evidence. No success, failure, cancellation or warning is compacted away.
            Save(document);
        }
    }
    private JournalDocument LoadUnlocked()
    {
        if(!File.Exists(_path))return new();
        try
        {
            var doc=JsonSerializer.Deserialize<JournalDocument>(File.ReadAllText(_path),Json)??throw new InvalidDataException("Recording operation journal is empty.");
            if(doc.Schema!=Schema)throw new InvalidDataException("Recording operation journal schema is not supported.");
            if(doc.Entries is null || doc.Entries.Any(e=>e is null))throw new InvalidDataException("Recording operation journal has invalid entries.");
            return doc;
        }
        catch(InvalidDataException){throw;}
        catch(Exception ex){throw new InvalidDataException("Recording operation journal is unreadable.",ex);}
    }
    private void Save(JournalDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp=_path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {JsonSerializer.Serialize(stream,document,Json);stream.Flush(true);}
            if(File.Exists(_path))File.Replace(temp,_path,null);else File.Move(temp,_path);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
    public static IReadOnlyList<PersistedRecordingEvidence> Read(StudyStore store)
    {
        var journal=new RecordingOperationJournal(store.DatabasePath);
        List<JournalEntry> entries;
        lock(Gate(journal._path))entries=journal.LoadUnlocked().Entries;
        var videos=store.Videos();var byId=videos.ToDictionary(v=>v.Id,StringComparer.Ordinal);
        var ids=videos.Select(v=>v.Id).Concat(entries.Select(e=>e.VideoId)).Distinct(StringComparer.Ordinal);
        return ids.Select(id=>Interpret(store,id,byId.GetValueOrDefault(id),entries.Where(e=>e.VideoId==id).ToArray())).ToArray();
    }
    private static PersistedRecordingEvidence Interpret(StudyStore store,string id,VideoAsset? video,JournalEntry[] entries)
    {
        var latest=entries.LastOrDefault();
        var named=entries.LastOrDefault(e=>!string.IsNullOrWhiteSpace(e.RunId) && e.Terminal is RecordingAttempts.Completed or RecordingAttempts.Reused or RecordingAttempts.AuditWarning);
        var historical=StoredRecordingRuns.Find(store,id,latest?.RunId??named?.RunId);
        string? historicalError=historical.Error;
        var intact=historical.Run is { } run && CompletedRecordingContract.TryValidate(run,out historicalError);
        AnalysisRun? matching=null;string? compatibility=null;string? invalidStoredEvidence=null;
        if(video is not null)
        {
            var candidateLookup=StoredRecordingRuns.Find(store,video.Id,matchingSettings:video.Settings);
            var candidate=candidateLookup.Run;
            if(candidateLookup.Exists)
            {
                if(candidate is null) { invalidStoredEvidence=candidateLookup.Error; compatibility="invalid_stored_run"; }
                else if(!CompletedRecordingContract.TryValidate(candidate,out invalidStoredEvidence)) compatibility="invalid_stored_run";
                else if(candidate.Metadata!=video.Metadata) compatibility="metadata_changed";
                else if(candidate.SourceSha256!=video.SourceSha256) compatibility="source_identity_changed";
                else if(candidate.AlgorithmContract!=AlgorithmIdentity.Contract) compatibility="algorithm_changed";
                else if(StudyStore.SettingsHash(candidate.Settings)!=StudyStore.SettingsHash(video.Settings)) compatibility="settings_changed";
                else matching=candidate;
            }
            if(compatibility is not null) { /* Current candidate mismatch already identified. */ }
            else if(!intact)compatibility=historical.Exists?"invalid_stored_run":"no_stored_run";
            else if(historical.Run!.Settings is { } settings && StudyStore.SettingsHash(settings)!=StudyStore.SettingsHash(video.Settings))compatibility="settings_changed";
            else if(historical.Run!.AlgorithmContract!=AlgorithmIdentity.Contract)compatibility="algorithm_changed";
            else if(historical.Run!.Metadata!=video.Metadata)compatibility="metadata_changed";
            else if(historical.Run!.SourceSha256!=video.SourceSha256)compatibility="source_identity_changed";
            else if(matching is null)compatibility="no_compatible_run";
            else if(!File.Exists(video.Path))compatibility="source_unavailable";
            else
            {
                try{using var source=File.OpenRead(video.Path);if(Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant()!=video.SourceSha256)compatibility="source_changed";}
                catch(IOException){compatibility="source_unavailable";}
                catch(UnauthorizedAccessException){compatibility="source_unavailable";}
            }
        }
        else compatibility="recording_not_registered";
        var state=latest?.Terminal??(intact?RecordingAttempts.Completed:historical.Exists?RecordingAttempts.Inconsistent:RecordingAttempts.NotAnalyzed);
        var completed=false;string? error=latest?.Error;string? note=null;
        if(state is RecordingAttempts.Queued or RecordingAttempts.Running)
        {
            if(!OwnerActive(store.DatabasePath,latest!)){state=RecordingAttempts.Interrupted;error="The attempt was interrupted before durable terminal evidence was recorded.";}
        }
        else if(state is RecordingAttempts.Completed or RecordingAttempts.Reused or RecordingAttempts.AuditWarning)
        {
            if (latest is not null && string.IsNullOrWhiteSpace(latest.RunId))
            { state=RecordingAttempts.Inconsistent; error="Completed operational evidence lacks a run id."; }
            else if(intact)completed=true;
            else{state=RecordingAttempts.Inconsistent;error=historical.Exists?"Stored completed-run evidence is invalid: "+historicalError:"Operational evidence names a completed run that is not stored for this recording.";}
        }
        else if(latest is null && historical.Exists && !intact){error="Stored run evidence is invalid: "+historicalError;}
        else if(state is not (RecordingAttempts.Failed or RecordingAttempts.Cancelled or RecordingAttempts.Interrupted or RecordingAttempts.InvalidEvidence or RecordingAttempts.Inconsistent or RecordingAttempts.NotAnalyzed))
        { state=RecordingAttempts.Inconsistent; error="Unknown operational journal transition."; }
        if(intact && compatibility is not null)note="Historical completed run remains stored; current reuse is unavailable: "+compatibility+".";
        if(state==RecordingAttempts.Failed && matching is not null)note="The latest attempt failed and is not a completed analysis. An earlier matching run is still stored.";
        var warning=entries.LastOrDefault(e=>!string.IsNullOrWhiteSpace(e.Warning) && (historical.Run is null || e.RunId==historical.Run.Id))?.Warning;
        return new PersistedRecordingEvidence
        {
            VideoId=id,LatestAttempt=state,LatestAttemptIsCompletedAnalysis=completed,
            MatchingRunId=matching?.Id,MatchingRunIntact=matching is not null,
            HistoricalRunId=historical.Run?.Id,HistoricalRunIntact=intact,
            StoredRunValidationError=invalidStoredEvidence, CurrentReuseEligible=matching is not null && compatibility is null,ReuseIneligibilityReason=compatibility,
            Error=error,Warning=warning,Note=note,
            DisposalError=entries.LastOrDefault(e=>!string.IsNullOrWhiteSpace(e.DisposalError))?.DisposalError,
            ProgressObserverError=entries.LastOrDefault(e=>!string.IsNullOrWhiteSpace(e.ProgressObserverError))?.ProgressObserverError,
            History=entries.Select(e=>new RecordingAttemptEvidence(e.AttemptId,e.Terminal,e.RunId,e.Error,e.Warning,e.DisposalError,e.ProgressObserverError,e.RecordedUtc)).ToArray()
        };
    }
    private static bool OwnerActive(string path,JournalEntry entry)
    {
        if(entry.OwnerProcessId==Environment.ProcessId){using var current=Process.GetCurrentProcess();return entry.OwnerStartUtcTicks==current.StartTime.ToUniversalTime().Ticks && StudyBatchGate.IsActive(path,entry.BatchId);}
        if(entry.OwnerProcessId<=0 || entry.OwnerStartUtcTicks<=0)return false;
        try{using var process=Process.GetProcessById(entry.OwnerProcessId);return !process.HasExited && process.StartTime.ToUniversalTime().Ticks==entry.OwnerStartUtcTicks && StudyProcessLease.IsHeldBy(path,entry.OwnerProcessId,entry.OwnerStartUtcTicks,entry.BatchId);}
        catch(ArgumentException){return false;}
        catch(InvalidOperationException){return false;}
        catch(System.ComponentModel.Win32Exception){return false;}
    }
    private sealed class JournalDocument{public int Schema{get;set;}=RecordingOperationJournal.Schema;public List<JournalEntry> Entries{get;set;}=[];}
}
