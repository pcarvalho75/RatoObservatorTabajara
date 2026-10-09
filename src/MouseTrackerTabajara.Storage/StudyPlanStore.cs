using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Storage;

public sealed record SavedStudyPlan(long Revision, string Fingerprint, string RawJson, string Reviewer, string Reason, DateTime CreatedUtc)
{
    public StudyPlan Plan => JsonDefaults.Deserialize<StudyPlan>(RawJson);
}

/// <summary>Append-only expected study plans; saving a plan never edits source runs or recording metadata.</summary>
public sealed class StudyPlanStore
{
    private readonly string _path;
    public StudyPlanStore(StudyStore store) => _path = store.DatabasePath;
    private SqliteConnection Open(bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, DefaultTimeout = 5, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    public SavedStudyPlan Save(StudyPlan plan, string reviewer, string reason, long expectedCurrentRevision) => Save(plan, reviewer, reason, expectedCurrentRevision, null);

    /// <summary>Same reviewed save; the optional guard sees the registered recordings inside the write transaction,
    /// after the revision check and before the insert, and refuses the revision by throwing.</summary>
    public SavedStudyPlan Save(StudyPlan plan, string reviewer, string reason, long expectedCurrentRevision, Action<IReadOnlyList<VideoAsset>>? recordingGuard)
    {
        plan.Validate();
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Saving the study plan requires a reviewer and reason.");
        var json = JsonDefaults.Serialize(plan);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS study_plans(revision INTEGER PRIMARY KEY,fingerprint TEXT NOT NULL,json TEXT NOT NULL,reviewer TEXT NOT NULL,reason TEXT NOT NULL,created_utc TEXT NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COALESCE(MAX(revision),0) FROM study_plans";
        var current = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (current != expectedCurrentRevision) throw new InvalidOperationException("Study plan changed since it was opened. Reload before saving a revision.");
        recordingGuard?.Invoke(StudyStore.ReadVideos(connection, transaction));
        var saved = new SavedStudyPlan(checked(current + 1), fingerprint, json, reviewer.Trim(), reason.Trim(), DateTime.UtcNow);
        command.CommandText = "INSERT INTO study_plans(revision,fingerprint,json,reviewer,reason,created_utc) VALUES($revision,$hash,$json,$reviewer,$reason,$utc)";
        command.Parameters.AddWithValue("$revision", saved.Revision); command.Parameters.AddWithValue("$hash", saved.Fingerprint);
        command.Parameters.AddWithValue("$json", saved.RawJson); command.Parameters.AddWithValue("$reviewer", saved.Reviewer);
        command.Parameters.AddWithValue("$reason", saved.Reason); command.Parameters.AddWithValue("$utc", saved.CreatedUtc.ToString("O"));
        command.ExecuteNonQuery();
        command.Parameters.Clear(); command.CommandText = "INSERT INTO audit(created_utc,action,json) VALUES($utc,'study_plan_saved',$json)";
        command.Parameters.AddWithValue("$utc", saved.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(new { saved.Revision, saved.Fingerprint, saved.Reviewer, saved.Reason }));
        command.ExecuteNonQuery(); transaction.Commit(); return saved;
    }
    public ImmutableArray<SavedStudyPlan> History()
    {
        using var connection = Open(true); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='study_plans'";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return [];
        command.CommandText = "SELECT revision,fingerprint,json,reviewer,reason,created_utc FROM study_plans ORDER BY revision";
        using var reader = command.ExecuteReader(); var rows = ImmutableArray.CreateBuilder<SavedStudyPlan>();
        while (reader.Read())
        {
            var saved = new SavedStudyPlan(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(saved.RawJson))).ToLowerInvariant() != saved.Fingerprint)
                throw new InvalidDataException("Stored study plan fingerprint differs from its immutable material.");
            saved.Plan.Validate(); rows.Add(saved);
        }
        return rows.ToImmutable();
    }
    public SavedStudyPlan? Latest() => History().LastOrDefault();
}
