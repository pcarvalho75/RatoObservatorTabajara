using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;
namespace MouseTrackerTabajara.Storage;

public sealed record CameraHistoryRow(long Ordinal,string Id,string Kind,string VideoId,string CreatedUtc,string ContentSha256,string Json);
/// <summary>Append-only camera evidence inside the study database; no other tables are changed.</summary>
public sealed class CameraHistoryStore
{
    private readonly string _connection;
    public string DatabasePath{get;}
    public CameraHistoryStore(string databasePath)
    {
        DatabasePath=Path.GetFullPath(databasePath);
        if(!File.Exists(DatabasePath))throw new FileNotFoundException("Open a registered study before camera setup.",DatabasePath);
        _connection=new SqliteConnectionStringBuilder{DataSource=DatabasePath,DefaultTimeout=5,Pooling=false}.ToString();
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="""
        CREATE TABLE IF NOT EXISTS camera_history(id TEXT PRIMARY KEY,kind TEXT NOT NULL,video_id TEXT NOT NULL,created_utc TEXT NOT NULL,content_sha256 TEXT NOT NULL,json TEXT NOT NULL);
        CREATE TRIGGER IF NOT EXISTS camera_history_no_update BEFORE UPDATE ON camera_history BEGIN SELECT RAISE(ABORT,'Camera history is immutable; append a new revision'); END;
        CREATE TRIGGER IF NOT EXISTS camera_history_no_delete BEFORE DELETE ON camera_history BEGIN SELECT RAISE(ABORT,'Camera history is immutable; prior evidence cannot be deleted'); END;
        """;command.ExecuteNonQuery();
    }
    private SqliteConnection Open(){var connection=new SqliteConnection(_connection);connection.Open();return connection;}
    public CameraHistoryRow Append<T>(string kind,string videoId,T evidence)
    {
        if(kind is not ("profile" or "preset" or "assignment" or "derived" or "preset_application"))throw new ArgumentException("Unknown camera evidence kind.");
        if(string.IsNullOrWhiteSpace(videoId))throw new ArgumentException("Source recording identity is required.");
        var id=Guid.NewGuid().ToString("N");var json=JsonDefaults.Serialize(evidence);var digest=Digest(json);var utc=DateTime.UtcNow.ToString("O");
        using var connection=Open();using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="INSERT INTO camera_history(id,kind,video_id,created_utc,content_sha256,json) VALUES($id,$kind,$video,$utc,$sha,$json)";
        foreach(var pair in new[]{("$id",id),("$kind",kind),("$video",videoId),("$utc",utc),("$sha",digest),("$json",json)})command.Parameters.AddWithValue(pair.Item1,pair.Item2);
        command.ExecuteNonQuery();command.Parameters.Clear();command.CommandText="SELECT last_insert_rowid()";var ordinal=(long)command.ExecuteScalar()!;transaction.Commit();return new(ordinal,id,kind,videoId,utc,digest,json);
    }
    public IReadOnlyList<CameraHistoryRow> Read(string? kind=null,string? videoId=null)
    {
        using var connection=Open();using var transaction=connection.BeginTransaction(deferred:true);using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT rowid,id,kind,video_id,created_utc,content_sha256,json FROM camera_history WHERE ($kind IS NULL OR kind=$kind) AND ($video IS NULL OR video_id=$video) ORDER BY rowid";
        command.Parameters.AddWithValue("$kind",(object?)kind??DBNull.Value);command.Parameters.AddWithValue("$video",(object?)videoId??DBNull.Value);
        using var reader=command.ExecuteReader();var rows=new List<CameraHistoryRow>();while(reader.Read())
        {var row=new CameraHistoryRow(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6));if(Digest(row.Json)!=row.ContentSha256)throw new InvalidDataException("Camera history material changed; reuse/derived output is held.");rows.Add(row);}reader.Close();transaction.Commit();return rows;
    }
    public static string Digest(string material)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
}
