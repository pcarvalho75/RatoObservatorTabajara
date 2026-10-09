using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.SpatialViews;

namespace MouseTrackerTabajara.Desktop;

public enum AtlasInspectionState { NotRecorded, LegacyRecorded, Invalidated, Stale, ContractRecordedUnvalidated }

public sealed record FrozenAtlasSqlRow(ImmutableDictionary<string, string?> Values)
{
    public string? Value(string key) => Values.GetValueOrDefault(key);
}

/// <summary>Immutable database material read under one SQLite read transaction, never by independent latest queries.</summary>
public sealed class AtlasSnapshot
{
    public string DatabasePath { get; }
    public string Revision { get; }
    public ImmutableDictionary<string, ImmutableArray<FrozenAtlasSqlRow>> Tables { get; }
    public ImmutableArray<AtlasRecording> Recordings { get; }
    private AtlasSnapshot(string path, ImmutableDictionary<string, ImmutableArray<FrozenAtlasSqlRow>> tables, bool buildRecordings)
    {
        DatabasePath = path; Tables = tables;
        var material = string.Join("\n", tables.OrderBy(t => t.Key, StringComparer.Ordinal).SelectMany(t =>
            t.Value.Select(r => t.Key + ":" + string.Join("|", r.Values.OrderBy(v => v.Key, StringComparer.Ordinal)
                .Select(v => v.Key.Length + ":" + v.Key + "=" + (v.Value is null ? "null" : v.Value.Length + ":" + v.Value))))));
        Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        Recordings = buildRecordings ? Rows("videos").Select(row => new AtlasRecording(this, row)).ToImmutableArray() : [];
    }
    public ImmutableArray<FrozenAtlasSqlRow> Rows(string table) => Tables.GetValueOrDefault(table, []);
    public static AtlasSnapshot Read(string path) => ReadCore(path, true);
    private static AtlasSnapshot ReadCore(string path, bool buildRecordings)
    {
        path = Path.GetFullPath(path);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 5, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = schema.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
        var tables = ImmutableDictionary.CreateBuilder<string, ImmutableArray<FrozenAtlasSqlRow>>(StringComparer.Ordinal);
        foreach (var table in new[] { "videos", "runs", "corrections", "reviews", "geometry_revisions", "biological_events", "protocol_windows", "study_plans", "camera_history",
            "study_aliases", "review_navigation", "ranking_evidence", "inclusion_decisions", "review_inspections", "ranking_snapshots", "unblinding" })
        {
            var rows = ImmutableArray.CreateBuilder<FrozenAtlasSqlRow>();
            if (names.Contains(table))
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT rowid AS snapshot_rowid,* FROM " + table + " ORDER BY rowid"; // fixed allowlist only
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var values = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
                    for (var i = 0; i < reader.FieldCount; i++) values.Add(reader.GetName(i), reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
                    rows.Add(new(values.ToImmutable()));
                }
            }
            tables.Add(table, rows.ToImmutable());
        }
        transaction.Commit();
        return new(path, tables.ToImmutable(), buildRecordings);
    }
    public bool IsCurrent() => ReadCore(DatabasePath, false).Revision == Revision;
    public SpatialStudy SpatialView()
    {
        var animals = Recordings.GroupBy(r => r.Asset.AnimalIdConfirmed && !string.IsNullOrWhiteSpace(r.Asset.AnimalId)
            ? r.Asset.AnimalId : "provisional:" + r.Asset.Id, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var rows = group.ToArray(); var first = rows[0].Asset;
            var maps = rows.Select(r => r.SpatialMap()).ToImmutableArray();
            var holds = rows.SelectMany(r => r.Holds.Select(h => r.Asset.BlindedAlias + ": " + h))
                .Append("Planned study roster, biological windows and common alignment are unconfirmed; group maps and biological conclusions are held.").Distinct().ToImmutableArray();
            var input = new AnimalSpatialInput(group.Key, string.IsNullOrWhiteSpace(first.BlindedAlias) ? "Recording" : first.BlindedAlias,
                rows.Select(r => r.Asset.GroupName).Distinct().Count() == 1 ? first.GroupName : "Conflicting allocation",
                first.AnimalIdConfirmed && !string.IsNullOrWhiteSpace(first.AnimalId), false, [], maps);
            return new AnimalSpatialMap(input, null, null, null, null, [], null, holds);
        }).ToImmutableArray();
        return new(animals, [], 0); // Imported files cannot supply a planned roster/comparison contract.
    }
}

public sealed class AtlasRecording
{
    private readonly string _assetJson;
    private readonly string? _runJson;
    private readonly ImmutableArray<string> _correctionJson;
    public AtlasSnapshot Snapshot { get; }
    public VideoAsset Asset => JsonDefaults.Deserialize<VideoAsset>(_assetJson); // callers cannot mutate frozen material
    public AnalysisRun? RawRun => _runJson is null ? null : JsonDefaults.Deserialize<AnalysisRun>(_runJson);
    public string NavigationId => RawRun?.Id ?? "unanalysed:" + Asset.Id;
    public ImmutableArray<string> Holds { get; }
    public AtlasInspectionState InspectionState { get; }
    public string InspectionLabel => ExperimentPlanAdapter.CurrentInspection(this) ? "Current frozen tracking inspection; independent accuracy is unvalidated." : InspectionState switch
    {
        AtlasInspectionState.NotRecorded => "Tracking inspection not recorded.",
        AtlasInspectionState.LegacyRecorded => "Legacy inspection recorded; material contract unavailable.",
        AtlasInspectionState.Invalidated => "Inspection invalidated; review pending.",
        AtlasInspectionState.Stale => "Inspection evidence is stale or inconsistent.",
        _ => "Inspection recorded; full material validation pending."
    };
    public AtlasRecording(AtlasSnapshot snapshot, FrozenAtlasSqlRow row)
    {
        Snapshot = snapshot; _assetJson = row.Value("json") ?? throw new InvalidDataException("Missing video snapshot material.");
        var asset = Asset;
        _runJson = snapshot.Rows("runs").LastOrDefault(r => r.Value("video_id") == asset.Id)?.Value("json");
        var raw = RawRun;
        _correctionJson = raw is null ? [] : snapshot.Rows("corrections").Where(r => r.Value("run_id") == raw.Id).Select(r => r.Value("json")!).ToImmutableArray();
        var holds = ImmutableArray.CreateBuilder<string>();
        if (!asset.Included) holds.Add("Excluded recording: " + (string.IsNullOrWhiteSpace(asset.ExclusionReason) ? "reason missing" : asset.ExclusionReason));
        if (!asset.AnimalIdConfirmed || string.IsNullOrWhiteSpace(asset.AnimalId)) holds.Add("Provisional identity; biological N withheld.");
        if (raw is null) holds.Add("No analysis; source remains inspectable.");
        else
        {
            if (raw.SourceSha256 != asset.SourceSha256 || raw.Metadata != asset.Metadata) holds.Add("Frozen run/source identity differs; analyzed playback held.");
            if (!raw.NominalTimingVerified) holds.Add("Source timing is unverified; descriptive nominal trajectory only.");
            if (JsonDefaults.Serialize(raw.Settings) != JsonDefaults.Serialize(asset.Settings)) holds.Add("Current settings differ from frozen run geometry; reanalysis required.");
            try { _ = EffectiveRun(); } catch (Exception) { holds.Add("Frozen corrections/trajectory are invalid; playback overlay held."); }
        }
        InspectionState = Inspection(snapshot, asset, raw);
        holds.Add(InspectionState switch
        {
            AtlasInspectionState.NotRecorded => "Tracking inspection not recorded.",
            AtlasInspectionState.LegacyRecorded => "Legacy tracking inspection recorded; its material contract is unavailable.",
            AtlasInspectionState.Invalidated => "Tracking inspection invalidated; review is pending.",
            AtlasInspectionState.Stale => "Tracking inspection evidence is stale or inconsistent with frozen material.",
            _ => "Tracking inspection contract recorded; contract validation remains pending."
        });
        holds.Add("Scientific approval withheld; inspection state does not license biological-window or group endpoints.");
        holds.Add("Raw detector interval only; biological-window endpoints and planned occupancy are held.");
        Holds = holds.ToImmutable();
    }
    private static AtlasInspectionState Inspection(AtlasSnapshot snapshot, VideoAsset asset, AnalysisRun? run)
    {
        if (run is null) return AtlasInspectionState.NotRecorded;
        var review = snapshot.Rows("reviews").LastOrDefault(r => r.Value("run_id") == run.Id);
        if (review is null) return AtlasInspectionState.NotRecorded;
        if (review.Value("confirmed") != "1") return AtlasInspectionState.Invalidated;
        if (run.SourceSha256 != asset.SourceSha256 || run.Metadata != asset.Metadata || JsonDefaults.Serialize(run.Settings) != JsonDefaults.Serialize(asset.Settings))
            return AtlasInspectionState.Stale;
        if (review.Value("contract_json") is not { Length: > 0 } json) return AtlasInspectionState.LegacyRecorded;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json); var contract = document.RootElement;
            bool Matches(string field, string expected) => contract.TryGetProperty(field, out var property) && property.ValueKind == System.Text.Json.JsonValueKind.String && property.GetString() == expected;
            if (!Matches("run_id", run.Id) || !Matches("video_id", asset.Id) || !Matches("source_sha256", run.SourceSha256) ||
                !Matches("settings_hash", Digest(JsonDefaults.Serialize(run.Settings)))) return AtlasInspectionState.Stale;
            var corrections = snapshot.Rows("corrections").Where(r => r.Value("run_id") == run.Id).Select(r => r.Value("json")!);
            if (!Matches("correction_fingerprint", Digest(string.Join("\n", corrections)))) return AtlasInspectionState.Stale;
            // Event/protocol validation requires the licensed evaluator; never grant scientific approval here.
            return AtlasInspectionState.ContractRecordedUnvalidated;
        }
        catch (Exception) { return AtlasInspectionState.Stale; }
    }
    private static string Digest(string material) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    public AnalysisRun? EffectiveRun()
    {
        var raw = RawRun;
        if (raw is null) return null;
        var points = MetricCalculator.ApplyCorrections(raw, _correctionJson.Select(JsonDefaults.Deserialize<ManualCorrection>));
        if (points.Count > 0)
        {
            _ = MetricCalculator.Calculate(points, raw.Metadata, raw.Settings);
            if (points.Any(p => p.Frame < 0 || p.Frame >= raw.Metadata.FrameCount)) throw new InvalidDataException("Frozen frame outside source.");
        }
        return raw with { Points = points, AppliedCorrections = _correctionJson.Select(JsonDefaults.Deserialize<ManualCorrection>).ToList() };
    }
    public TrialSpatialMap SpatialMap()
    {
        var asset = Asset; AnalysisRun? run;
        try { run = EffectiveRun(); } catch (Exception) { run = null; }
        var metadata = run?.Metadata ?? asset.Metadata; var settings = run?.Settings ?? asset.Settings;
        var points = run?.Points ?? [];
        var first = points.Count > 0 ? points[0].Frame : 0;
        var duration = points.Count > 0 ? (points[^1].Frame - first + 1d) / metadata.Fps : metadata.DurationSeconds;
        var frames = points.Select(p => new SpatialFrame(p.Frame, (p.Frame - first) / metadata.Fps, (p.Frame - first + 1d) / metadata.Fps,
            p.Accepted ? (p.X!.Value - settings.Cx * metadata.Width) / (settings.Rx * metadata.Width) : null,
            p.Accepted ? (p.Y!.Value - settings.Cy * metadata.Height) / (settings.Ry * metadata.Height) : null, p.Accepted)).ToImmutableArray();
        if (frames.IsEmpty) frames = [new(0, 0, 1d / metadata.Fps, null, null, false)];
        var extent = Math.Max(1.01, frames.Where(f => f.Accepted).Select(f => Math.Max(Math.Abs(f.X!.Value), Math.Abs(f.Y!.Value))).DefaultIfEmpty(1).Max());
        var grid = new GridDefinition(12, 12, -extent, -extent, extent, extent, "pool radii", "source:" + asset.SourceSha256, "source-camera; alignment unconfirmed");
        var input = new SpatialTrialInput(asset.Id, NavigationId, asset.SourceSha256, Snapshot.Revision,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonDefaults.Serialize(settings)))),
            $"{asset.Phase}; day {asset.Day}; trial {asset.Trial}", "raw detector interval; biological release unconfirmed", duration, false, settings.ArenaConfirmed, false, grid, frames);
        return SpatialCalculator.Trial(input);
    }
}
