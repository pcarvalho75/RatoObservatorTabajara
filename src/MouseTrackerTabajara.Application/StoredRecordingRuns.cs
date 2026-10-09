using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
namespace MouseTrackerTabajara.Application;

internal readonly record struct StoredRunLookup(bool Exists, AnalysisRun? Run, string? Error);
internal static class StoredRecordingRuns
{
    // Application read adapter: Storage's public LatestRun API cannot retrieve a named historical row.
    public static StoredRunLookup Find(StudyStore store, string videoId, string? runId=null, TrackingSettings? matchingSettings=null)
    {
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=store.DatabasePath,Mode=SqliteOpenMode.ReadOnly,DefaultTimeout=5}.ToString());
        connection.Open(); using var command=connection.CreateCommand();
        command.CommandText=runId is null ? "SELECT json FROM runs WHERE video_id=$video ORDER BY rowid DESC LIMIT 1" : "SELECT json FROM runs WHERE video_id=$video AND id=$id";
        if(runId is null && matchingSettings is not null){command.CommandText="SELECT json FROM runs WHERE video_id=$video AND settings_hash=$settings ORDER BY rowid DESC LIMIT 1";command.Parameters.AddWithValue("$settings",StudyStore.SettingsHash(matchingSettings));}
        command.Parameters.AddWithValue("$video",videoId); if(runId is not null)command.Parameters.AddWithValue("$id",runId);
        if(command.ExecuteScalar() is not string json)return new(false,null,null);
        try{var run=JsonDefaults.Deserialize<AnalysisRun>(json);return run.VideoId==videoId ? new(true,run,null) : new(true,null,"Stored run is associated with another recording.");}
        catch(Exception ex) when(ex is System.Text.Json.JsonException or InvalidDataException){return new(true,null,"Stored run JSON is unreadable: "+ex.Message);}
    }
}
