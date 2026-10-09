using System.Collections.Immutable;
using System.IO;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
namespace MouseTrackerTabajara.Desktop;
/// <summary>One recording/run/correction/review material transaction; unrelated recordings are not loaded.</summary>
public sealed class CameraRunSnapshot
{
    private readonly string _assetJson;private readonly string? _runJson;private readonly ImmutableArray<string> _corrections;
    public string DatabasePath{get;}public string VideoId{get;}public string Revision{get;}public string RunFingerprint{get;}public string CorrectionFingerprint{get;}
    public VideoAsset Asset=>JsonDefaults.Deserialize<VideoAsset>(_assetJson);
    public AnalysisRun? RawRun=>_runJson is null?null:JsonDefaults.Deserialize<AnalysisRun>(_runJson);
    private CameraRunSnapshot(string path,string id,string asset,string? run,ImmutableArray<string> corrections,IReadOnlyList<string> material)
    {DatabasePath=path;VideoId=id;_assetJson=asset;_runJson=run;_corrections=corrections;Revision=CameraHistoryStore.Digest(JsonDefaults.Serialize(material));RunFingerprint=CameraHistoryStore.Digest(run??"");CorrectionFingerprint=CameraHistoryStore.Digest(string.Join("\n",corrections));}
    public static CameraRunSnapshot Read(string path,string videoId)
    {
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=5}.ToString());connection.Open();using var transaction=connection.BeginTransaction(deferred:true);
        string? One(string sql,string key){using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;command.Parameters.AddWithValue("$id",key);return command.ExecuteScalar() as string;}
        var asset=One("SELECT json FROM videos WHERE id=$id",videoId)??throw new InvalidDataException("Registered source is missing.");var run=One("SELECT json FROM runs WHERE video_id=$id ORDER BY rowid DESC LIMIT 1",videoId);var runId=run is null?"":JsonDefaults.Deserialize<AnalysisRun>(run).Id;
        ImmutableArray<string> Many(string sql,string key){using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;command.Parameters.AddWithValue("$id",key);using var reader=command.ExecuteReader();var list=ImmutableArray.CreateBuilder<string>();while(reader.Read())list.Add(reader.GetString(0));return list.ToImmutable();}
        var corrections=Many("SELECT json FROM corrections WHERE run_id=$id ORDER BY id",runId);
        var material=new List<string>{asset,run??""};material.AddRange(corrections);
        using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="SELECT CAST(confirmed AS TEXT)||'|'||COALESCE(contract_json,'') FROM reviews WHERE run_id=$id ORDER BY id DESC LIMIT 1";command.Parameters.AddWithValue("$id",runId);material.Add(command.ExecuteScalar() as string??"");}
        material.AddRange(Many("SELECT json FROM biological_events WHERE video_id=$id ORDER BY id",videoId));material.AddRange(Many("SELECT json FROM protocol_windows WHERE video_id=$id ORDER BY id",videoId));
        transaction.Commit();return new(path,videoId,asset,run,corrections,material);
    }
    public bool IsCurrent()=>Read(DatabasePath,VideoId).Revision==Revision;
    public AnalysisRun? EffectiveRun(){var raw=RawRun;if(raw is null)return null;var points=MetricCalculator.ApplyCorrections(raw,_corrections.Select(JsonDefaults.Deserialize<ManualCorrection>));return raw with{Points=points,Metrics=MetricCalculator.Calculate(points,raw.Metadata,raw.Settings),AppliedCorrections=_corrections.Select(JsonDefaults.Deserialize<ManualCorrection>).ToList()};}
}
