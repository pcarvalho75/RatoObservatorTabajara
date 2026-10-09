using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MouseTrackerTabajara.Storage;

public sealed record ValidationHistoryEntry(long Id, string Kind, string EntityId, int Revision, string Json, string Sha256, string Reviewer, string Reason, DateTime CreatedUtc);

/// <summary>Independent-reference append-only records. Never writes runs, corrections, tuning settings or inspection approvals.</summary>
public sealed class ValidationHistoryStore(string database)
{
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5 }.ToString()); connection.Open(); return connection;
    }
    public ValidationHistoryEntry Save(string kind, string entity, string json, string reviewer, string reason, int expectedRevision)
    {
        if (kind is not ("plan" or "sampling" or "reference" or "report" or "unblinding" or "pixel_disclosure")) throw new ArgumentException("Unsupported independent-validation record.");
        if (string.IsNullOrWhiteSpace(entity) || string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Record identity, material, reviewer and reason are required.");
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS validation_history(id INTEGER PRIMARY KEY AUTOINCREMENT,kind TEXT NOT NULL,entity_id TEXT NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL,sha256 TEXT NOT NULL,reviewer TEXT NOT NULL,reason TEXT NOT NULL,created_utc TEXT NOT NULL,UNIQUE(kind,entity_id,revision))"; command.ExecuteNonQuery();
        command.CommandText = "SELECT COALESCE(MAX(revision),0) FROM validation_history WHERE kind=$kind AND entity_id=$entity"; command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$entity", entity);
        var current = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture); if (current != expectedRevision) throw new InvalidOperationException("Independent-validation record changed. Reload before saving a revision.");
        var created = DateTime.UtcNow; var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        command.CommandText = "INSERT INTO validation_history(kind,entity_id,revision,json,sha256,reviewer,reason,created_utc) VALUES($kind,$entity,$revision,$json,$hash,$reviewer,$reason,$utc); SELECT last_insert_rowid()";
        command.Parameters.AddWithValue("$revision", checked(current + 1)); command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$reviewer", reviewer.Trim()); command.Parameters.AddWithValue("$reason", reason.Trim()); command.Parameters.AddWithValue("$utc", created.ToString("O"));
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); transaction.Commit(); return new(id, kind, entity, current + 1, json, hash, reviewer.Trim(), reason.Trim(), created);
    }
    public ImmutableArray<ValidationHistoryEntry> History()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='validation_history'";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return [];
        command.CommandText = "SELECT id,kind,entity_id,revision,json,sha256,reviewer,reason,created_utc FROM validation_history ORDER BY id";
        using var reader = command.ExecuteReader(); var rows = ImmutableArray.CreateBuilder<ValidationHistoryEntry>();
        while (reader.Read())
        {
            var row = new ValidationHistoryEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.Json))).ToLowerInvariant() != row.Sha256) throw new InvalidDataException("Independent reference material fingerprint differs."); rows.Add(row);
        }
        return rows.ToImmutable();
    }
}
