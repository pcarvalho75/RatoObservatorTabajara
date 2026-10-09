using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Application;

public sealed record ValidationSqlRow(ImmutableDictionary<string, string?> Values)
{ public string? Value(string key) => Values.GetValueOrDefault(key); }
public sealed record IndependentValidationSnapshot(string Database, string ProducerFingerprint, string ReferenceFingerprint,
    ImmutableArray<ValidationFrozenRun> Runs, ImmutableArray<VideoAsset> Imported, ImmutableArray<ValidationHistoryEntry> History)
{
    public static IndependentValidationSnapshot Read(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString()); connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var tables = new Dictionary<string, ImmutableArray<ValidationSqlRow>>();
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var names = new HashSet<string>(StringComparer.Ordinal); using (var reader = command.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
        foreach (var table in new[] { "videos", "runs", "corrections", "reviews", "biological_events", "protocol_windows", "study_plans", "camera_history", "inclusion_decisions", "review_inspections", "ranking_snapshots", "study_aliases", "review_navigation", "ranking_evidence", "validation_history" })
        {
            var rows = ImmutableArray.CreateBuilder<ValidationSqlRow>();
            if (names.Contains(table))
            {
                command.CommandText = "SELECT rowid AS snapshot_rowid,* FROM " + table + " ORDER BY rowid";
                using var reader = command.ExecuteReader(); while (reader.Read())
                {
                    var values = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
                    for (var i = 0; i < reader.FieldCount; i++) values.Add(reader.GetName(i), reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)); rows.Add(new(values.ToImmutable()));
                }
            }
            tables.Add(table, rows.ToImmutable());
        }
        transaction.Commit();
        string RowText(ValidationSqlRow row) => string.Join("|", row.Values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key.Length + ":" + v.Key + "=" + (v.Value is null ? "null" : v.Value.Length + ":" + v.Value)));
        var producer = Hash(string.Join("\n", tables.Where(t => t.Key != "validation_history").OrderBy(t => t.Key, StringComparer.Ordinal).SelectMany(t => t.Value.Select(r => t.Key + ":" + RowText(r)))));
        var history = tables["validation_history"].Select(row =>
        {
            var entry = new ValidationHistoryEntry(long.Parse(row.Value("id")!, CultureInfo.InvariantCulture), row.Value("kind")!, row.Value("entity_id")!, int.Parse(row.Value("revision")!, CultureInfo.InvariantCulture), row.Value("json")!, row.Value("sha256")!, row.Value("reviewer")!, row.Value("reason")!, DateTime.Parse(row.Value("created_utc")!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            if (Hash(entry.Json) != entry.Sha256) throw new InvalidDataException("Independent-validation history fingerprint differs."); return entry;
        }).ToImmutableArray();
        var reference = Hash(string.Join("\n", history.Where(h => h.Kind is "plan" or "sampling" or "reference").Select(h => h.Id + ":" + h.Sha256)));
        var imported = tables["videos"].Select(r => JsonDefaults.Deserialize<VideoAsset>(r.Value("json")!)).ToImmutableArray();
        var binaries = new[] { typeof(IndependentValidationSnapshot).Assembly, typeof(MetricCalculator).Assembly, typeof(MouseTrackerTabajara.Tracking.OpenCvTracker).Assembly, typeof(ValidationHistoryStore).Assembly };
        var code = Hash(string.Join("\n", binaries.Select(assembly => assembly.GetName().Name + ":" + (File.Exists(assembly.Location) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant() : "unavailable"))));
        var runs = ImmutableArray.CreateBuilder<ValidationFrozenRun>();
        foreach (var row in tables["videos"])
        {
            var assetJson = row.Value("json")!; var asset = JsonDefaults.Deserialize<VideoAsset>(assetJson); var runJson = tables["runs"].LastOrDefault(r => r.Value("video_id") == asset.Id)?.Value("json"); if (runJson is null) continue;
            var raw = JsonDefaults.Deserialize<AnalysisRun>(runJson); var corrections = tables["corrections"].Where(r => r.Value("run_id") == raw.Id).Select(r => r.Value("json")!).ToImmutableArray();
            var events = tables["biological_events"].Where(r => r.Value("video_id") == asset.Id).Select(r => (Json: r.Value("json")!, Event: JsonDefaults.Deserialize<BiologicalEvent>(r.Value("json")!)))
                .GroupBy(e => e.Event.Id).Select(g => g.OrderBy(e => e.Event.Revision).Last()).OrderBy(e => e.Event.Id, StringComparer.Ordinal).Select(e => e.Json).ToImmutableArray();
            runs.Add(new(asset.Id, asset.SourceSha256, assetJson, runJson, corrections, Hash(runJson), MaterialFingerprint.Hash(corrections), raw.AlgorithmContract, raw.SoftwareVersion, code, events));
        }
        return new(database, producer, reference, runs.ToImmutable(), imported, history);
    }
    public bool IsCurrent()
    { var current = Read(Database); return current.ProducerFingerprint == ProducerFingerprint && current.ReferenceFingerprint == ReferenceFingerprint; }
    public IndependentValidationPlan? Plan => History.LastOrDefault(h => h.Kind == "plan") is { } row ? JsonDefaults.Deserialize<IndependentValidationPlan>(row.Json) : null;
    public ValidationSampling? Sampling => History.LastOrDefault(h => h.Kind == "sampling") is { } row ? JsonDefaults.Deserialize<ValidationSampling>(row.Json) : null;
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public void ValidatePlan(IndependentValidationPlan plan)
    {
        plan.Validate();
        var tuningDeclarations = History.Where(h => h.Kind == "plan").Select(h => JsonDefaults.Deserialize<IndependentValidationPlan>(h.Json))
            .SelectMany(p => p.Recordings).Where(r => r.UsedForTuning || r.Split == ValidationSplit.Development).ToArray();
        var tuningIds = tuningDeclarations.Select(r => r.VideoId).ToHashSet(StringComparer.Ordinal);
        var tuningSources = Imported.Where(a => tuningIds.Contains(a.Id)).Select(a => a.SourceSha256)
            .Concat(History.Where(h => h.Kind == "sampling").Select(h => JsonDefaults.Deserialize<ValidationSampling>(h.Json))
                .SelectMany(s => s.Runs).Where(r => tuningIds.Contains(r.VideoId)).Select(r => r.SourceSha256))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in plan.Recordings.Where(r => r.Split == ValidationSplit.IndependentValidation))
        {
            var currentSource = Imported.SingleOrDefault(a => a.Id == row.VideoId)?.SourceSha256;
            if (tuningIds.Contains(row.VideoId) || tuningDeclarations.Any(r => r.Session.Equals(row.Session, StringComparison.OrdinalIgnoreCase)) || currentSource is not null && tuningSources.Contains(currentSource))
                throw new ArgumentException("A source/session previously declared development/tuned cannot be relabeled as independent validation, including identical copied source bytes.");
            if (currentSource is null) throw new ArgumentException("Declared independent source is not imported.");
        }
    }
}
