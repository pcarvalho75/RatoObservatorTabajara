using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Application;

public sealed record ProcessedProjectReceipt(string ProjectPath, string Sha256, int Recordings, int StoredRunRecordings, bool OperationalHistoryEmbedded);

/// <summary>A .tabajara project is SQLite, including all optional scientific tables.
/// Online backup includes committed WAL pages. Original videos remain external.
/// Publication never overwrites any project or sidecar; failed staging files are retained.</summary>
public static class ProcessedProject
{
    private const string JournalSuffix=".recording-operations.json";
    private const string EmbeddedTable="tabajara_project_files";
    public static string ValidateForOpen(string path)
    {
        var full=SafePath(path);if(!File.Exists(full))throw new FileNotFoundException("Project file is unavailable.");
        try{return ValidateDatabase(full,SqliteOpenMode.ReadOnly);}
        catch(SqliteException ex) when(ex.SqliteErrorCode==10&&File.Exists(full+"-wal"))
        {
            // After an unclean process exit SQLite may need to rebuild WAL shared-memory state.
            // Only SQLite recovery is allowed here; no application schema is created before validation.
            using var ownership=StudyBatchGate.Enter(full,0,"project-wal-recovery-"+Guid.NewGuid().ToString("N"));
            return ValidateDatabase(full,SqliteOpenMode.ReadWrite);
        }
    }
    private static string ValidateDatabase(string full,SqliteOpenMode mode)
    {
        using var database=Open(full,mode);
        using var command=database.CreateCommand();command.CommandText="PRAGMA quick_check";
        if(!Equals(command.ExecuteScalar(),"ok"))throw new InvalidDataException("Project integrity check failed.");
        command.CommandText="SELECT name FROM sqlite_master WHERE type='table'";
        var names=new HashSet<string>(StringComparer.Ordinal);using(var rows=command.ExecuteReader())while(rows.Read())names.Add(rows.GetString(0));
        if(new[]{"videos","runs","corrections","audit"}.Any(name=>!names.Contains(name)))throw new InvalidDataException("File is not a Tabajara study project.");
        // Validate material before StudyStore may perform any schema migration.
        command.CommandText="SELECT json FROM videos";using(var rows=command.ExecuteReader())while(rows.Read()){var asset=JsonDefaults.Deserialize<VideoAsset>(rows.GetString(0));asset.Settings.Validate();if(string.IsNullOrWhiteSpace(asset.Id)||string.IsNullOrWhiteSpace(asset.SourceSha256))throw new InvalidDataException("Invalid project source material.");}
        command.CommandText="SELECT json FROM runs";using(var rows=command.ExecuteReader())while(rows.Read())_ = JsonDefaults.Deserialize<AnalysisRun>(rows.GetString(0));
        if(names.Contains(EmbeddedTable)) ValidateEmbedded(database);
        return full;
    }

    public static StudyStore OpenProject(string path)
    {
        var full=ValidateForOpen(path);
        using var ownership=StudyBatchGate.Enter(full,0,"project-open-"+Guid.NewGuid().ToString("N"));
        RestoreEmbeddedJournal(full);
        return new StudyStore(full);
    }

    public static ProcessedProjectReceipt SaveSnapshot(string sourcePath,string destinationPath)
    {
        var source=ValidateForOpen(sourcePath);var destination=SafePath(destinationPath);
        if(string.Equals(source,destination,StringComparison.OrdinalIgnoreCase))throw new IOException("Save a new project snapshot, not the active file.");
        RejectCollision(destination);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var sourceOwnership=StudyBatchGate.Enter(source,0,"project-snapshot-"+Guid.NewGuid().ToString("N"));
        using var destinationOwnership=new FileStream(destination+".snapshot-owner",FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
        RejectCollision(destination);
        var stage=destination+".partial-"+Guid.NewGuid().ToString("N");
        using(var input=Open(source,SqliteOpenMode.ReadOnly))
        using(var output=Open(stage,SqliteOpenMode.ReadWriteCreate))
        {
            input.BackupDatabase(output);
            using var command=output.CreateCommand();command.CommandText=$"CREATE TABLE IF NOT EXISTS {EmbeddedTable}(name TEXT PRIMARY KEY,sha256 TEXT NOT NULL,bytes BLOB NOT NULL);DELETE FROM {EmbeddedTable} WHERE name='recording-operations';";command.ExecuteNonQuery();
            var journal=source+JournalSuffix;
            if(File.Exists(journal))
            {
                SafePath(journal);var bytes=File.ReadAllBytes(journal);
                // Keep the exact operational document, including every failed/cancelled attempt.
                using var parsed=System.Text.Json.JsonDocument.Parse(bytes);
                command.CommandText=$"INSERT INTO {EmbeddedTable}(name,sha256,bytes)VALUES('recording-operations',$hash,$bytes)";
                command.Parameters.AddWithValue("$hash",Hash(bytes));command.Parameters.AddWithValue("$bytes",bytes);command.ExecuteNonQuery();
            }
            else
            {
                // Snapshot opened before first resume may still carry its embedded original history.
                using var original=input.CreateCommand();original.CommandText=$"SELECT name FROM sqlite_master WHERE name='{EmbeddedTable}'";
                if(original.ExecuteScalar() is not null)
                { original.CommandText=$"SELECT sha256,bytes FROM {EmbeddedTable} WHERE name='recording-operations'";using var rows=original.ExecuteReader();if(rows.Read()){command.CommandText=$"INSERT INTO {EmbeddedTable}(name,sha256,bytes)VALUES('recording-operations',$hash,$bytes)";command.Parameters.Clear();command.Parameters.AddWithValue("$hash",rows.GetString(0));command.Parameters.AddWithValue("$bytes",(byte[])rows[1]);command.ExecuteNonQuery();} }
            }
            command.Parameters.Clear();command.CommandText="PRAGMA journal_mode=DELETE;PRAGMA synchronous=FULL;";command.ExecuteNonQuery();
        }
        ValidateForOpen(stage);RejectCollision(destination);File.Move(stage,destination,false);
        return Receipt(destination);
    }

    public static ProcessedProjectReceipt CreateNew(string destinationPath)
    {
        var destination=SafePath(destinationPath);RejectCollision(destination);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var seed=destination+".new-"+Guid.NewGuid().ToString("N");_ = new StudyStore(seed);
        return SaveSnapshot(seed,destination);
    }

    public static ProcessedProjectReceipt Receipt(string path)
    {
        var full=ValidateForOpen(path);using var connection=Open(full,SqliteOpenMode.ReadOnly);using var command=connection.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM videos";var count=Convert.ToInt32(command.ExecuteScalar());
        command.CommandText="SELECT COUNT(DISTINCT video_id) FROM runs";var runs=Convert.ToInt32(command.ExecuteScalar());
        command.CommandText=$"SELECT COUNT(*) FROM sqlite_master WHERE name='{EmbeddedTable}'";var embedded=Convert.ToInt32(command.ExecuteScalar())>0;
        if(embedded){command.CommandText=$"SELECT COUNT(*) FROM {EmbeddedTable} WHERE name='recording-operations'";embedded=Convert.ToInt32(command.ExecuteScalar())>0;}
        using var file=File.OpenRead(full);
        return new(full,Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(),count,runs,embedded);
    }
    private static void RestoreEmbeddedJournal(string full)
    {
        var journal=SafePath(full+JournalSuffix);if(File.Exists(journal))return;
        using var connection=Open(full,SqliteOpenMode.ReadOnly);using var command=connection.CreateCommand();
        command.CommandText=$"SELECT name FROM sqlite_master WHERE name='{EmbeddedTable}'";if(command.ExecuteScalar() is null)return;
        command.CommandText=$"SELECT bytes FROM {EmbeddedTable} WHERE name='recording-operations'";
        if(command.ExecuteScalar() is not byte[] bytes)return;
        var temporary=journal+".partial-"+Guid.NewGuid().ToString("N");
        using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){output.Write(bytes);output.Flush(true);}
        File.Move(temporary,journal,false);
    }
    private static void ValidateEmbedded(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();command.CommandText=$"SELECT name,sha256,bytes FROM {EmbeddedTable}";
        using var rows=command.ExecuteReader();while(rows.Read())if(rows.GetString(0)!="recording-operations"||Hash((byte[])rows[2])!=rows.GetString(1))throw new InvalidDataException("Project embedded history is invalid.");
    }
    private static SqliteConnection Open(string path,SqliteOpenMode mode){var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=mode,Pooling=false,DefaultTimeout=5}.ToString());connection.Open();return connection;}
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void RejectCollision(string path)
    {if(new[]{path,path+"-wal",path+"-shm",path+JournalSuffix,path+".recording-batch.lock"}.Any(p=>File.Exists(p)||Directory.Exists(p)))throw new IOException("Destination exists or has recovery sidecars; choose a new project name.");}
    private static string SafePath(string path)
    {
        var full=Path.GetFullPath(path);StudyLocation.GuardAgainstOrphanSidecars(full);
        for(var item=full;!string.IsNullOrEmpty(item);item=Path.GetDirectoryName(item))
            RejectReparse(item);
        foreach(var suffix in new[]{"-wal","-shm",JournalSuffix,".recording-batch.lock"})RejectReparse(full+suffix);
        return full;
    }
    private static void RejectReparse(string path)
    {
        ProjectReparseTags.RequireSafePathComponent(path);
    }
}

