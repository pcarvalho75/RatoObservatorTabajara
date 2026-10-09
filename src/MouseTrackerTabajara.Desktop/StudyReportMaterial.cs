using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

public sealed record StudyReportMaterial(string SnapshotRevision, string CodeReceipt, DateTime FrozenUtc,
    ImmutableDictionary<string, ImmutableArray<FrozenAtlasSqlRow>> Tables)
{
    public static StudyReportMaterial Freeze(AtlasSnapshot snapshot) => new(snapshot.Revision, CurrentCodeReceipt(), DateTime.UtcNow, snapshot.Tables);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string CurrentCodeReceipt() => Hash(string.Join("\n", new[] { typeof(StudyReportMaterial).Assembly, typeof(StudyPlan).Assembly,
        typeof(MouseTrackerTabajara.Application.CompletedRecordingContract).Assembly, typeof(MouseTrackerTabajara.Storage.StudyStore).Assembly,
        typeof(MouseTrackerTabajara.Tracking.OpenCvTracker).Assembly, typeof(MouseTrackerTabajara.SpatialViews.SpatialCalculator).Assembly, typeof(MouseTrackerTabajara.Calibration.CalibrationProfile).Assembly }
        .Distinct().OrderBy(a => a.GetName().Name, StringComparer.Ordinal).Select(a => a.GetName().Name + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))))));
    public StudyReportReconstruction Reconstruct()
    {
        var folder = Path.Combine(Path.GetTempPath(), "study-report-replay-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var database = Path.Combine(folder, "frozen.morrisdb");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                connection.Open(); using var transaction = connection.BeginTransaction();
                foreach (var (table, rows) in Tables)
                {
                    if (!AllowedTables.Contains(table)) throw new InvalidDataException("Unexpected frozen table.");
                    if (rows.IsEmpty) continue;
                    var columns = rows.SelectMany(r => r.Values.Keys).Where(c => c != "snapshot_rowid").Distinct(StringComparer.Ordinal).ToArray();
                    static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
                    using var schema = connection.CreateCommand(); schema.Transaction = transaction;
                    schema.CommandText = "CREATE TABLE " + Quote(table) + "(" + string.Join(',', columns.Select(c => Quote(c) + " TEXT")) + ")"; schema.ExecuteNonQuery();
                    foreach (var row in rows)
                    {
                        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                        insert.CommandText = "INSERT INTO " + Quote(table) + "(rowid," + string.Join(',', columns.Select(Quote)) + ") VALUES($row," + string.Join(',', columns.Select((_, i) => "$p" + i)) + ")";
                        insert.Parameters.AddWithValue("$row", long.Parse(row.Value("snapshot_rowid")!, System.Globalization.CultureInfo.InvariantCulture));
                        for (var i = 0; i < columns.Length; i++) insert.Parameters.AddWithValue("$p" + i, (object?)row.Value(columns[i]) ?? DBNull.Value);
                        insert.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
            var snapshot = AtlasSnapshot.Read(database);
            if (snapshot.Revision != SnapshotRevision) throw new InvalidDataException("Frozen reconstruction identity differs.");
            return new(folder, snapshot);
        }
        catch { Directory.Delete(folder, true); throw; }
    }
    private static readonly HashSet<string> AllowedTables = ["videos", "runs", "corrections", "reviews", "geometry_revisions", "biological_events", "protocol_windows", "study_plans", "camera_history", "study_aliases", "review_navigation", "ranking_evidence", "inclusion_decisions", "review_inspections", "ranking_snapshots", "unblinding"];
}
public sealed class StudyReportReconstruction(string folder, AtlasSnapshot snapshot) : IDisposable
{
    public AtlasSnapshot Snapshot { get; } = snapshot;
    public void Dispose() => Directory.Delete(folder, true);
}
