using System.Globalization;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Storage;

public sealed record GeometryRevision(long Id, DateTime CreatedUtc, string Reason, TrackingSettings Settings);

public sealed class StudyStore
{
    private readonly string _connectionString;
    public string DatabasePath { get; }
    public StudyStore(string path)
    {
        DatabasePath = StudyLocation.GuardAgainstOrphanSidecars(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, DefaultTimeout = 5 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS videos(id TEXT PRIMARY KEY,sha256 TEXT NOT NULL UNIQUE,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY,video_id TEXT NOT NULL,created_utc TEXT NOT NULL,settings_hash TEXT NOT NULL,json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS run_video ON runs(video_id,created_utc DESC);
            CREATE TABLE IF NOT EXISTS corrections(id INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT NOT NULL,frame INTEGER NOT NULL,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS reviews(id INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT NOT NULL,confirmed INTEGER NOT NULL,created_utc TEXT NOT NULL,contract_json TEXT);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY AUTOINCREMENT,created_utc TEXT NOT NULL,action TEXT NOT NULL,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS geometry_revisions(id INTEGER PRIMARY KEY AUTOINCREMENT,video_id TEXT NOT NULL,created_utc TEXT NOT NULL,reason TEXT NOT NULL,settings_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS biological_events(id INTEGER PRIMARY KEY AUTOINCREMENT,video_id TEXT NOT NULL,event_id TEXT NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS protocol_windows(id INTEGER PRIMARY KEY AUTOINCREMENT,video_id TEXT NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS inclusion_decisions(id INTEGER PRIMARY KEY AUTOINCREMENT,video_id TEXT NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS review_inspections(id INTEGER PRIMARY KEY AUTOINCREMENT,video_id TEXT NOT NULL,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS review_navigation(id INTEGER PRIMARY KEY AUTOINCREMENT,video_id TEXT NOT NULL,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ranking_snapshots(run_id TEXT PRIMARY KEY,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ranking_evidence(id INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT NOT NULL,revision INTEGER NOT NULL,correction_fingerprint TEXT NOT NULL,json TEXT NOT NULL,UNIQUE(run_id,revision));
            CREATE TABLE IF NOT EXISTS study_aliases(id INTEGER PRIMARY KEY AUTOINCREMENT,kind TEXT NOT NULL,subject_key TEXT NOT NULL,alias TEXT NOT NULL,created_utc TEXT NOT NULL,UNIQUE(kind,subject_key));
            CREATE TABLE IF NOT EXISTS unblinding(id INTEGER PRIMARY KEY AUTOINCREMENT,json TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        EnsureReviewContractColumn(connection);
        EnsureRankingEvidence(connection);
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    public static string SettingsHash(TrackingSettings settings) => SettingsIdentity.Hash(settings);

    public VideoAsset AddOrGet(VideoAsset asset)
    {
        asset.Settings.Validate();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM videos WHERE sha256=$hash"; command.Parameters.AddWithValue("$hash", asset.SourceSha256);
        if (command.ExecuteScalar() is string existing)
        {
            var prior = JsonDefaults.Deserialize<VideoAsset>(existing);
            if (!File.Exists(prior.Path) && File.Exists(asset.Path)) { prior = prior with { Path = asset.Path }; SaveVideo(prior); Audit("source_relinked", new { prior.Id, prior.SourceSha256, prior.Path }); }
            else if (NeedsAliasRepair(prior)) SaveVideo(prior);
            return ReadStored(prior.Id);
        }
        SaveVideo(asset); Audit("video_imported", new { asset.Id, asset.Path, asset.SourceSha256 }); return ReadStored(asset.Id);
    }
    public void SaveVideo(VideoAsset asset)
    {
        ValidateVideo(asset);
        InTransaction((connection, tx) =>
        {
            var existing = ReadVideo(connection, tx, asset.Id);
            var assigned = EnsureAliases(connection, tx, asset, existing);
            WriteVideo(connection, tx, assigned);
            if (existing is null) return;
            if (SettingsHash(existing.Settings) != SettingsHash(assigned.Settings))
                InvalidateReviews(connection, tx, assigned.Id, "Settings changed. Previous review cannot approve the edited geometry.");
            else if (existing.Phase != assigned.Phase)
                InvalidateReviews(connection, tx, assigned.Id, "Trial phase changed. Previous review cannot approve the new window.");
        });
    }
    public List<VideoAsset> Videos()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT json FROM videos ORDER BY rowid";
        using var reader = command.ExecuteReader(); var items = new List<VideoAsset>();
        while (reader.Read()) items.Add(JsonDefaults.Deserialize<VideoAsset>(reader.GetString(0))); return items;
    }
    /// <summary>Recording snapshot on a caller's open transaction (used only by the plan store's transactional guard).</summary>
    internal static List<VideoAsset> ReadVideos(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT json FROM videos ORDER BY rowid";
        using var reader = command.ExecuteReader(); var items = new List<VideoAsset>();
        while (reader.Read()) items.Add(JsonDefaults.Deserialize<VideoAsset>(reader.GetString(0))); return items;
    }
    public AnalysisRun? LatestRun(string videoId, TrackingSettings? matchingSettings = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = matchingSettings is null ? "SELECT json FROM runs WHERE video_id=$id ORDER BY rowid DESC LIMIT 1"
            : "SELECT json FROM runs WHERE video_id=$id AND settings_hash=$settings ORDER BY rowid DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", videoId);
        if (matchingSettings is not null) command.Parameters.AddWithValue("$settings", SettingsHash(matchingSettings));
        return command.ExecuteScalar() is string json ? JsonDefaults.Deserialize<AnalysisRun>(json) : null;
    }
    public void SaveRun(AnalysisRun run)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT json FROM videos WHERE id=$video"; command.Parameters.AddWithValue("$video", run.VideoId);
        if (command.ExecuteScalar() is not string assetJson) throw new InvalidOperationException("Register the video before publishing its run.");
        var asset = JsonDefaults.Deserialize<VideoAsset>(assetJson);
        if (run.SourceSha256 != asset.SourceSha256 || run.Metadata != asset.Metadata) throw new InvalidOperationException("Analysis source hash or metadata differs from the registered recording.");
        run.Settings.Validate();
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO runs(id,video_id,created_utc,settings_hash,json) VALUES($id,$video,$utc,$settings,$json)";
        command.Parameters.AddWithValue("$id", run.Id); command.Parameters.AddWithValue("$video", run.VideoId);
        command.Parameters.AddWithValue("$utc", run.CreatedUtc.ToString("O")); command.Parameters.AddWithValue("$settings", SettingsHash(run.Settings)); command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(run));
        command.ExecuteNonQuery(); transaction.Commit(); Audit("analysis_completed", new { run.Id, run.VideoId, run.SourceSha256, run.Settings, run.Metrics.CoveragePercent, run.MaximumTimestampErrorSeconds });
    }
    public TrackingSettings SaveEditedSettings(VideoAsset asset, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required before saving geometry or detector settings.");
        TrackingSettings saved = asset.Settings;
        InTransaction((connection, tx) =>
        {
            var stored = ReadVideo(connection, tx, asset.Id) ?? throw new InvalidOperationException("Register the video before saving geometry.");
            if (stored.SourceSha256 != asset.SourceSha256) throw new InvalidOperationException("Source identity is immutable; import changed content as a new source.");
            var merged = ArenaGeometry.MergeEdit(stored.Settings, asset.Settings);
            saved = merged;
            if (SettingsHash(merged) == SettingsHash(stored.Settings)) return;
            var updated = EnsureAliases(connection, tx, asset with { Settings = merged }, stored);
            saved = updated.Settings;
            WriteVideo(connection, tx, updated);
            InsertGeometry(connection, tx, updated.Id, merged, reason.Trim());
            InvalidateReviews(connection, tx, updated.Id, "Settings changed. Previous review cannot approve the edited geometry.");
            WriteAudit(connection, tx, "geometry_edited", new { updated.Id, Reason = reason.Trim(), Settings = merged });
        });
        return saved;
    }
    public IReadOnlyList<GeometryRevision> GeometryRevisions(string videoId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,created_utc,reason,settings_json FROM geometry_revisions WHERE video_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        var items = new List<GeometryRevision>();
        while (reader.Read())
            items.Add(new(reader.GetInt64(0), ParseUtc(reader.GetString(1)), reader.GetString(2), JsonDefaults.Deserialize<TrackingSettings>(reader.GetString(3))));
        return items;
    }
    public void SaveCorrection(AnalysisRun run, ManualCorrection correction)
    {
        InTransaction((connection, tx) =>
        {
            var stored = ReadRun(connection, tx, run.Id) ?? throw new InvalidOperationException("Correction does not belong to this source run.");
            if (stored.VideoId != run.VideoId || stored.SourceSha256 != run.SourceSha256)
                throw new InvalidOperationException("Correction does not belong to this source run.");
            _ = MetricCalculator.ApplyCorrections(stored, [correction]);
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO corrections(run_id,frame,json) VALUES($run,$frame,$json)";
            command.Parameters.AddWithValue("$run", stored.Id);
            command.Parameters.AddWithValue("$frame", correction.Frame);
            command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(correction));
            command.ExecuteNonQuery();
            InsertReview(connection, tx, stored.Id, false, null, "Frame correction invalidated review.");
            WriteAudit(connection, tx, "frame_corrected", correction);
        });
    }
    public List<ManualCorrection> Corrections(string runId)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT json FROM corrections WHERE run_id=$id ORDER BY id"; command.Parameters.AddWithValue("$id", runId);
        using var reader = command.ExecuteReader(); var items = new List<ManualCorrection>(); while (reader.Read()) items.Add(JsonDefaults.Deserialize<ManualCorrection>(reader.GetString(0))); return items;
    }
    public AnalysisRun EffectiveRun(AnalysisRun raw)
    {
        var corrections = Corrections(raw.Id);
        var points = MetricCalculator.ApplyCorrections(raw, corrections);
        return raw with { Points = points, Metrics = MetricCalculator.Calculate(points, raw.Metadata, raw.Settings), AppliedCorrections = corrections };
    }
    public BiologicalEvent RecordBiologicalEvent(VideoAsset asset, BiologicalEventKind kind, int frame, string reason, string reviewer, string? reviseEventId = null)
    {
        BiologicalEvent saved = null!;
        InTransaction((connection, tx) =>
        {
            var stored = ReadVideo(connection, tx, asset.Id) ?? throw new InvalidOperationException("Register the video before recording a biological annotation.");
            if (stored.SourceSha256 != asset.SourceSha256) throw new InvalidOperationException("Biological annotation does not belong to this source.");
            var history = ReadEvents(connection, tx, stored.Id);
            string id;
            var revision = 1;
            if (!string.IsNullOrWhiteSpace(reviseEventId))
            {
                var prior = history.Where(row => row.Event.Id == reviseEventId).OrderBy(row => row.Event.Revision).ThenBy(row => row.RowId).LastOrDefault()
                    ?? throw new ArgumentException("Biological annotation to revise was not found.");
                if (prior.Event.Kind != kind) throw new ArgumentException("A revision keeps the biological annotation kind. Record a new annotation instead.");
                id = prior.Event.Id;
                revision = prior.Event.Revision + 1;
            }
            else id = Guid.NewGuid().ToString("N");
            saved = BiologicalEvent.Create(stored.Id, LatestRunId(connection, tx, stored.Id), kind, frame, stored.Metadata, reason, reviewer, revision, DateTime.UtcNow, id);
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO biological_events(video_id,event_id,revision,json) VALUES($video,$event,$revision,$json)";
            command.Parameters.AddWithValue("$video", stored.Id);
            command.Parameters.AddWithValue("$event", saved.Id);
            command.Parameters.AddWithValue("$revision", saved.Revision);
            command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(saved));
            command.ExecuteNonQuery();
            InvalidateReviews(connection, tx, stored.Id, "Biological annotation changed. Previous review cannot approve the new event.");
            WriteAudit(connection, tx, "biological_event_recorded", saved);
        });
        return saved;
    }
    public IReadOnlyList<BiologicalEvent> BiologicalEventHistory(string videoId) =>
        ReadEvents(videoId).Select(row => row.Event).ToArray();
    public IReadOnlyList<BiologicalEvent> EffectiveBiologicalEvents(string videoId) =>
        EffectiveRows(ReadEvents(videoId)).Select(row => row.Event).ToArray();
    public ProtocolWindow SaveProtocolWindow(VideoAsset asset, ProtocolWindow draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Reason)) throw new ArgumentException("A protocol window requires a reason.");
        if (string.IsNullOrWhiteSpace(draft.Reviewer)) throw new ArgumentException("A protocol window requires a reviewer.");
        if (!double.IsFinite(draft.RequestedExposureSeconds) || draft.RequestedExposureSeconds <= 0 || draft.RequestedExposureSeconds > 86400)
            throw new ArgumentException("Requested exposure must stay a positive planned duration.");
        ProtocolWindow saved = null!;
        InTransaction((connection, tx) =>
        {
            var stored = ReadVideo(connection, tx, asset.Id) ?? throw new InvalidOperationException("Register the video before saving a protocol window.");
            if (stored.SourceSha256 != asset.SourceSha256) throw new InvalidOperationException("Protocol window does not belong to this source.");
            if (draft.ReleaseFrame is int releaseFrame) FrameIdentity.RequireSourceFrame(releaseFrame, stored.Metadata.FrameCount);
            if (draft.ReleaseConfirmed)
            {
                if (draft.ReleaseFrame is not int frame) throw new ArgumentException("Confirmed release requires a source frame.");
                var release = EffectiveRows(ReadEvents(connection, tx, stored.Id)).Select(row => row.Event);
                if (!release.Any(item => item.Kind == BiologicalEventKind.WaterEntry && item.Frame == frame))
                    throw new ArgumentException("Confirm water entry at the release frame before confirming release. Platform-site entry is not water entry or mounting.");
            }
            var prior = ReadProtocols(connection, tx, stored.Id).LastOrDefault();
            saved = new ProtocolWindow
            {
                VideoId = stored.Id,
                Revision = (prior?.Revision ?? 0) + 1,
                Phase = stored.Phase,
                RequestedExposureSeconds = draft.RequestedExposureSeconds,
                ReleaseConfirmed = draft.ReleaseConfirmed,
                ReleaseFrame = draft.ReleaseFrame,
                WindowConfirmed = draft.WindowConfirmed,
                Reviewer = draft.Reviewer.Trim(),
                Reason = draft.Reason.Trim(),
                CreatedUtc = DateTime.UtcNow
            };
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO protocol_windows(video_id,revision,json) VALUES($video,$revision,$json)";
            command.Parameters.AddWithValue("$video", stored.Id);
            command.Parameters.AddWithValue("$revision", saved.Revision);
            command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(saved));
            command.ExecuteNonQuery();
            InvalidateReviews(connection, tx, stored.Id, "Protocol window changed. Previous review cannot approve the new window.");
            WriteAudit(connection, tx, "protocol_window_saved", saved);
        });
        return saved;
    }
    public ProtocolWindow? LatestProtocol(string videoId)
    {
        using var connection = Open();
        return ReadProtocols(connection, null, videoId).LastOrDefault();
    }
    public IReadOnlyList<ProtocolWindow> ProtocolHistory(string videoId)
    {
        using var connection = Open();
        return ReadProtocols(connection, null, videoId);
    }
    public void RecordReview(AnalysisRun run, bool confirmed)
    {
        if (!confirmed)
        {
            InTransaction((connection, tx) => InsertReview(connection, tx, run.Id, false, null, "Review invalidated."));
            Audit("review_recorded", new { run.Id, Confirmed = false });
            return;
        }
        if (!run.Settings.ArenaConfirmed || !run.NominalTimingVerified) throw new InvalidOperationException("Confirm the pool boundary and verify video timing before review.");
        InTransaction((connection, tx) =>
        {
            var stored = ReadRun(connection, tx, run.Id) ?? throw new InvalidOperationException("Review cannot approve a run that is not stored.");
            var asset = ReadVideo(connection, tx, stored.VideoId) ?? throw new InvalidOperationException("Review cannot approve a run whose recording is missing.");
            if (stored.VideoId != run.VideoId || stored.SourceSha256 != run.SourceSha256 || asset.SourceSha256 != stored.SourceSha256)
                throw new InvalidOperationException("Review cannot approve a different source.");
            if (SettingsHash(asset.Settings) != SettingsHash(run.Settings) || SettingsHash(stored.Settings) != SettingsHash(run.Settings))
                throw new InvalidOperationException("Review cannot approve stale settings. Reanalyze the current geometry before confirming.");
            var contract = new ReviewContract
            {
                RunId = stored.Id,
                VideoId = stored.VideoId,
                SourceSha256 = stored.SourceSha256,
                SettingsHash = SettingsHash(stored.Settings),
                CorrectionFingerprint = Fingerprint(ReadCorrectionJson(connection, tx, stored.Id)),
                EventFingerprint = Fingerprint(EffectiveRows(ReadEvents(connection, tx, stored.VideoId)).Select(row => row.Json)),
                ProtocolFingerprint = Fingerprint(ReadProtocolJson(connection, tx, stored.VideoId).TakeLast(1)),
                Note = "Tracking review confirmed for the current geometry, corrections, events, and protocol window."
            };
            InsertReview(connection, tx, stored.Id, true, contract, contract.Note);
            WriteAudit(connection, tx, "review_recorded", new { run.Id, Confirmed = true, contract.SettingsHash, contract.EventFingerprint, contract.ProtocolFingerprint });
        });
    }
    public bool Reviewed(string runId)
    {
        using var connection = Open();
        var latest = ReadLatestReview(connection, null, runId);
        if (latest is not { Confirmed: true, Contract: { } contract }) return false;
        var stored = ReadRun(connection, null, runId);
        if (stored is null || stored.SourceSha256 != contract.SourceSha256 || SettingsHash(stored.Settings) != contract.SettingsHash) return false;
        var asset = ReadVideo(connection, null, stored.VideoId);
        if (asset is null || asset.SourceSha256 != contract.SourceSha256 || SettingsHash(asset.Settings) != contract.SettingsHash) return false;
        if (contract.CorrectionFingerprint != Fingerprint(ReadCorrectionJson(connection, null, runId))) return false;
        if (contract.EventFingerprint != Fingerprint(EffectiveRows(ReadEvents(connection, null, stored.VideoId)).Select(row => row.Json))) return false;
        if (contract.ProtocolFingerprint != Fingerprint(ReadProtocolJson(connection, null, stored.VideoId).TakeLast(1))) return false;
        return true;
    }
    public IReadOnlyList<ReviewEntry> ReviewHistory(string runId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,run_id,confirmed,created_utc,contract_json FROM reviews WHERE run_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", runId);
        using var reader = command.ExecuteReader();
        var items = new List<ReviewEntry>();
        while (reader.Read())
        {
            ReviewContract? contract = reader.IsDBNull(4) ? null : JsonDefaults.Deserialize<ReviewContract>(reader.GetString(4));
            items.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) == 1, ParseUtc(reader.GetString(3)), contract));
        }
        return items;
    }
    public bool ReviewStale(string runId) => !Reviewed(runId) && ReviewHistory(runId).Any(item => item.Confirmed);

    public InclusionDecision SaveInclusion(VideoAsset asset, bool included, string reason, string reviewer)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException(included ? "A reasoned inclusion requires a reason." : "An exclusion requires a reason.");
        if (string.IsNullOrWhiteSpace(reviewer)) throw new ArgumentException("An inclusion decision requires a reviewer.");
        InclusionDecision saved = null!;
        InTransaction((connection, tx) =>
        {
            var stored = ReadVideo(connection, tx, asset.Id) ?? throw new InvalidOperationException("Register the video before recording inclusion.");
            if (stored.SourceSha256 != asset.SourceSha256) throw new InvalidOperationException("Inclusion does not belong to this source.");
            var prior = ReadInclusions(connection, tx, stored.Id).LastOrDefault();
            var updated = stored with { Included = included, ExclusionReason = reason.Trim() };
            WriteVideo(connection, tx, updated);
            saved = new InclusionDecision
            {
                VideoId = stored.Id,
                Included = included,
                Reason = reason.Trim(),
                Reviewer = reviewer.Trim(),
                Revision = (prior?.Revision ?? 0) + 1,
                CreatedUtc = DateTime.UtcNow
            };
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO inclusion_decisions(video_id,revision,json) VALUES($video,$revision,$json)";
            command.Parameters.AddWithValue("$video", stored.Id);
            command.Parameters.AddWithValue("$revision", saved.Revision);
            command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(saved));
            command.ExecuteNonQuery();
            WriteAudit(connection, tx, "inclusion_recorded", new { saved.VideoId, saved.Included, saved.Reason, saved.Reviewer, saved.Revision });
        });
        return saved;
    }

    public InclusionDecision? LatestInclusion(string videoId)
    {
        using var connection = Open();
        return ReadInclusions(connection, null, videoId).LastOrDefault();
    }

    public IReadOnlyList<InclusionDecision> InclusionHistory(string videoId)
    {
        using var connection = Open();
        return ReadInclusions(connection, null, videoId);
    }

    public NavigationReceipt RecordNavigation(VideoAsset asset, int requestedFrame, string? runId, ReviewMoment? moment)
    {
        NavigationReceipt saved = null!;
        InTransaction((connection, tx) =>
        {
            var stored = ReadVideo(connection, tx, asset.Id) ?? throw new InvalidOperationException("Register the video before navigating a review moment.");
            if (stored.SourceSha256 != asset.SourceSha256) throw new InvalidOperationException("Navigation does not belong to this source.");
            FrameIdentity.RequireSourceFrame(requestedFrame, stored.Metadata.FrameCount);
            string? boundRun = null;
            if (!string.IsNullOrWhiteSpace(runId))
            {
                var run = ReadRun(connection, tx, runId);
                if (run is not null && run.VideoId == stored.Id && string.Equals(run.SourceSha256, stored.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    boundRun = run.Id;
            }
            saved = new NavigationReceipt
            {
                VideoId = stored.Id,
                RunId = boundRun,
                RequestedFrame = requestedFrame,
                MomentStartFrame = moment?.StartFrame,
                MomentEndFrame = moment?.EndFrame,
                DisplayCompleted = false,
                CreatedUtc = DateTime.UtcNow
            };
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO review_navigation(video_id,json) VALUES($video,$json)";
            command.Parameters.AddWithValue("$video", stored.Id);
            command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(saved));
            command.ExecuteNonQuery();
            WriteAudit(connection, tx, "review_navigation_requested", new { saved.VideoId, saved.RequestedFrame, saved.RunId, DisplayCompleted = false });
        });
        return saved;
    }

    public IReadOnlyList<NavigationReceipt> NavigationReceipts(string videoId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM review_navigation WHERE video_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        var items = new List<NavigationReceipt>();
        while (reader.Read()) items.Add(JsonDefaults.Deserialize<NavigationReceipt>(reader.GetString(0)));
        return items;
    }

    public InspectionMaterial InspectionMaterialFor(string runId)
    {
        using var connection = Open();
        var run = ReadRun(connection, null, runId) ?? throw new InvalidOperationException("Inspection material requires a stored run.");
        return InspectionMaterialFor(connection, null, run);
    }

    public MomentInspection RecordInspection(VideoAsset asset, InspectionAttestation attestation)
    {
        if (string.IsNullOrWhiteSpace(attestation.Reason)) throw new ArgumentException("An inspection requires a reviewer reason.");
        if (string.IsNullOrWhiteSpace(attestation.Reviewer)) throw new ArgumentException("An inspection requires a reviewer.");
        if (string.IsNullOrWhiteSpace(attestation.RunId)) throw new ArgumentException("An inspection requires the stored run.");
        if (attestation.QueueEndFrame < attestation.QueueStartFrame) throw new ArgumentException("An inspection range is not canonical.");
        MomentInspection saved = null!;
        InTransaction((connection, tx) =>
        {
            var stored = ReadVideo(connection, tx, asset.Id) ?? throw new InvalidOperationException("Register the video before recording an inspection.");
            if (!string.Equals(stored.SourceSha256, asset.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Inspection does not belong to this source.");
            var run = ReadRun(connection, tx, attestation.RunId) ?? throw new InvalidOperationException("Inspection cannot attest a run that is not stored.");
            if (run.VideoId != stored.Id)
                throw new InvalidOperationException("The stored run does not belong to this recording.");
            if (!string.Equals(run.SourceSha256, stored.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Inspection source does not match the stored run.");
            var material = InspectionMaterialFor(connection, tx, run);
            if (!string.Equals(attestation.SourceSha256, material.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Inspection source does not match the displayed recording.");
            if (!string.Equals(attestation.CorrectionFingerprint, material.CorrectionFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Inspection material is stale. Corrections changed after the frame was displayed.");
            if (!string.Equals(attestation.ReviewFingerprint, material.ReviewFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Inspection material is stale. Review changed after the frame was displayed.");
            var frame = FrameIdentity.RequireSourceFrame(attestation.DisplayedFrame, stored.Metadata.FrameCount);
            if (frame != attestation.QueueStartFrame)
                throw new InvalidOperationException("The displayed frame is not the review moment's source frame.");
            FrameIdentity.RequireSourceFrame(attestation.QueueEndFrame, stored.Metadata.FrameCount);
            saved = new MomentInspection
            {
                VideoId = stored.Id,
                RunId = run.Id,
                StartFrame = frame,
                EndFrame = frame,
                DisplayedFrame = frame,
                QueueStartFrame = attestation.QueueStartFrame,
                QueueEndFrame = attestation.QueueEndFrame,
                Reason = attestation.Reason.Trim(),
                Reviewer = attestation.Reviewer.Trim(),
                SourceSha256 = material.SourceSha256,
                CorrectionFingerprint = material.CorrectionFingerprint,
                ReviewFingerprint = material.ReviewFingerprint,
                CreatedUtc = DateTime.UtcNow
            };
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO review_inspections(video_id,json) VALUES($video,$json)";
            command.Parameters.AddWithValue("$video", stored.Id);
            command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(saved));
            command.ExecuteNonQuery();
            WriteAudit(connection, tx, "moment_inspected", new { saved.VideoId, saved.RunId, saved.DisplayedFrame, saved.Reviewer, saved.CorrectionFingerprint, saved.ReviewFingerprint });
        });
        return saved;
    }

    public IReadOnlyList<MomentInspection> Inspections(string videoId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM review_inspections WHERE video_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        var items = new List<MomentInspection>();
        while (reader.Read()) items.Add(JsonDefaults.Deserialize<MomentInspection>(reader.GetString(0)));
        return items;
    }

    public IReadOnlyList<MomentInspection> CurrentInspections(string videoId, string runId)
    {
        InspectionMaterial material;
        try { material = InspectionMaterialFor(runId); }
        catch (InvalidOperationException) { return []; }
        if (!string.Equals(material.VideoId, videoId, StringComparison.Ordinal)) return [];
        return Inspections(videoId).Where(item =>
            item.RunId == runId
            && string.Equals(item.SourceSha256, material.SourceSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.CorrectionFingerprint, material.CorrectionFingerprint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ReviewFingerprint, material.ReviewFingerprint, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void SaveRankingSnapshot(AnalysisRun run, ReviewRankingEvidence evidence)
    {
        if (evidence.VersionName != ReviewRankingEvidence.Version) throw new ArgumentException("Ranking evidence is not the accepted queue version.");
        InTransaction((connection, tx) =>
        {
            var stored = ReadRun(connection, tx, run.Id) ?? throw new InvalidOperationException("Ranking evidence requires a stored run.");
            if (stored.VideoId != run.VideoId || !string.Equals(stored.SourceSha256, run.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ranking evidence does not belong to this stored run.");
            var corrections = ReadCorrectionJson(connection, tx, stored.Id).ToList();
            var points = MetricCalculator.ApplyCorrections(stored, corrections.Select(JsonDefaults.Deserialize<ManualCorrection>));
            var effective = stored with { Points = points, AppliedCorrections = corrections.Select(JsonDefaults.Deserialize<ManualCorrection>).ToList() };
            var captured = ReviewRankingEvidence.Capture(effective);
            if (!string.Equals(evidence.SeedMaterial, captured.SeedMaterial, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Ranking fingerprint does not match the stored points.");
            if (!evidence.Moments.SequenceEqual(captured.Moments))
                throw new ArgumentException("Ranking evidence does not match the accepted queue.");
            var fingerprint = Fingerprint(corrections);
            var history = ReadRankingHistory(connection, tx, stored.Id);
            var duplicate = history.LastOrDefault(item =>
                item.Version == captured.VersionName
                && string.Equals(item.SeedMaterial, captured.SeedMaterial, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.CorrectionFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
                && item.Moments.SequenceEqual(captured.Moments));
            if (duplicate is not null) return;
            var snapshot = new RankingSnapshot
            {
                RunId = stored.Id,
                Revision = (history.Count == 0 ? 0 : history.Max(item => item.Revision)) + 1,
                Version = captured.VersionName,
                SeedMaterial = captured.SeedMaterial,
                CorrectionFingerprint = fingerprint,
                Moments = captured.Moments.ToList(),
                CreatedUtc = DateTime.UtcNow
            };
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO ranking_evidence(run_id,revision,correction_fingerprint,json) VALUES($id,$revision,$fingerprint,$json)";
            command.Parameters.AddWithValue("$id", stored.Id);
            command.Parameters.AddWithValue("$revision", snapshot.Revision);
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(snapshot));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO ranking_snapshots(run_id,json) VALUES($id,$json) ON CONFLICT(run_id) DO UPDATE SET json=excluded.json";
            command.Parameters.AddWithValue("$id", stored.Id);
            command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(snapshot));
            command.ExecuteNonQuery();
            WriteAudit(connection, tx, "ranking_recorded", new { snapshot.RunId, snapshot.Revision, snapshot.Version, Moments = snapshot.Moments.Count });
        });
    }

    public RankingSnapshot? LatestRanking(string runId)
    {
        using var connection = Open();
        var stored = ReadRun(connection, null, runId);
        if (stored is null) return null;
        var fingerprint = Fingerprint(ReadCorrectionJson(connection, null, runId));
        return ReadRankingHistory(connection, null, runId).LastOrDefault(item =>
            item.Version == ReviewRankingEvidence.Version
            && string.Equals(item.CorrectionFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<RankingSnapshot> RankingHistory(string runId)
    {
        using var connection = Open();
        return ReadRankingHistory(connection, null, runId);
    }

    public UnblindingRecord RecordUnblinding(string reviewer, string reason)
    {
        if (string.IsNullOrWhiteSpace(reviewer)) throw new ArgumentException("Unblinding requires a reviewer.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Unblinding requires a reason.");
        var saved = new UnblindingRecord { Reviewer = reviewer.Trim(), Reason = reason.Trim(), CreatedUtc = DateTime.UtcNow };
        InTransaction((connection, tx) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO unblinding(json) VALUES($json)";
            command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(saved));
            command.ExecuteNonQuery();
            WriteAudit(connection, tx, "unblinding_recorded", new { saved.Reviewer, saved.Reason });
        });
        return saved;
    }

    public IReadOnlyList<UnblindingRecord> UnblindingHistory()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM unblinding ORDER BY id";
        using var reader = command.ExecuteReader();
        var items = new List<UnblindingRecord>();
        while (reader.Read()) items.Add(JsonDefaults.Deserialize<UnblindingRecord>(reader.GetString(0)));
        return items;
    }

    public void Audit(string action, object details)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO audit(created_utc,action,json) VALUES($utc,$action,$json)";
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$action", action); command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(details)); command.ExecuteNonQuery();
    }

    private void InTransaction(Action<SqliteConnection, SqliteTransaction> work)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        work(connection, tx);
        tx.Commit();
    }
    private static void ValidateVideo(VideoAsset asset)
    {
        asset.Settings.Validate();
        if (asset.Day < 1 || asset.Trial < 1) throw new ArgumentException("Day and trial must be positive integers.");
        if (!asset.Included && string.IsNullOrWhiteSpace(asset.ExclusionReason)) throw new ArgumentException("An exclusion requires a reason.");
        if (asset.Metadata.Width <= 0 || asset.Metadata.Height <= 0 || asset.Metadata.FrameCount <= 0 || !double.IsFinite(asset.Metadata.Fps) || asset.Metadata.Fps <= 0)
            throw new ArgumentException("Video metadata must contain valid dimensions, frames and frame rate.");
    }
    private static void WriteVideo(SqliteConnection connection, SqliteTransaction tx, VideoAsset asset)
    {
        ValidateVideo(asset);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO videos(id,sha256,json) VALUES($id,$sha,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json WHERE videos.sha256=excluded.sha256";
        command.Parameters.AddWithValue("$id", asset.Id);
        command.Parameters.AddWithValue("$sha", asset.SourceSha256);
        command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(asset));
        if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("Source identity is immutable; import changed content as a new source.");
    }
    private static VideoAsset? ReadVideo(SqliteConnection connection, SqliteTransaction? tx, string videoId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT json FROM videos WHERE id=$id";
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteScalar() is string json ? JsonDefaults.Deserialize<VideoAsset>(json) : null;
    }
    private static AnalysisRun? ReadRun(SqliteConnection connection, SqliteTransaction? tx, string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT json FROM runs WHERE id=$id";
        command.Parameters.AddWithValue("$id", runId);
        return command.ExecuteScalar() is string json ? JsonDefaults.Deserialize<AnalysisRun>(json) : null;
    }
    private static List<string> RunIds(SqliteConnection connection, SqliteTransaction tx, string videoId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT id FROM runs WHERE video_id=$id ORDER BY rowid";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }
    private static string? LatestRunId(SqliteConnection connection, SqliteTransaction tx, string videoId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT id FROM runs WHERE video_id=$id ORDER BY rowid DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteScalar() as string;
    }
    private static void InsertGeometry(SqliteConnection connection, SqliteTransaction tx, string videoId, TrackingSettings settings, string reason)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO geometry_revisions(video_id,created_utc,reason,settings_json) VALUES($video,$utc,$reason,$json)";
        command.Parameters.AddWithValue("$video", videoId);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(settings));
        command.ExecuteNonQuery();
    }
    private void InvalidateReviews(SqliteConnection connection, SqliteTransaction tx, string videoId, string note)
    {
        foreach (var runId in RunIds(connection, tx, videoId))
        {
            if (ReadLatestReview(connection, tx, runId) is not { Confirmed: true }) continue;
            InsertReview(connection, tx, runId, false, null, note);
        }
    }
    private static void InsertReview(SqliteConnection connection, SqliteTransaction tx, string runId, bool confirmed, ReviewContract? contract, string note)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO reviews(run_id,confirmed,created_utc,contract_json) VALUES($id,$confirmed,$utc,$contract)";
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$confirmed", confirmed ? 1 : 0);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$contract", contract is null ? (object)DBNull.Value : JsonDefaults.Serialize(contract with { Note = note }));
        command.ExecuteNonQuery();
    }
    private static ReviewEntry? ReadLatestReview(SqliteConnection connection, SqliteTransaction? tx, string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT id,run_id,confirmed,created_utc,contract_json FROM reviews WHERE run_id=$id ORDER BY id DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        ReviewContract? contract = reader.IsDBNull(4) ? null : JsonDefaults.Deserialize<ReviewContract>(reader.GetString(4));
        return new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) == 1, ParseUtc(reader.GetString(3)), contract);
    }
    private static IEnumerable<string> ReadCorrectionJson(SqliteConnection connection, SqliteTransaction? tx, string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT json FROM corrections WHERE run_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", runId);
        using var reader = command.ExecuteReader();
        var items = new List<string>();
        while (reader.Read()) items.Add(reader.GetString(0));
        return items;
    }
    private List<EventRow> ReadEvents(string videoId)
    {
        using var connection = Open();
        return ReadEvents(connection, null, videoId);
    }
    private static List<EventRow> ReadEvents(SqliteConnection connection, SqliteTransaction? tx, string videoId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT id,json FROM biological_events WHERE video_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        var items = new List<EventRow>();
        while (reader.Read())
        {
            var json = reader.GetString(1);
            items.Add(new(reader.GetInt64(0), json, JsonDefaults.Deserialize<BiologicalEvent>(json)));
        }
        return items;
    }
    private static List<EventRow> EffectiveRows(List<EventRow> rows) =>
        rows.GroupBy(row => row.Event.Id, StringComparer.Ordinal)
            .Select(group => group.OrderBy(row => row.Event.Revision).ThenBy(row => row.RowId).Last())
            .OrderBy(row => row.Event.Id, StringComparer.Ordinal)
            .ToList();
    private static List<ProtocolWindow> ReadProtocols(SqliteConnection connection, SqliteTransaction? tx, string videoId)
    {
        var items = new List<ProtocolWindow>();
        foreach (var json in ReadProtocolJson(connection, tx, videoId)) items.Add(JsonDefaults.Deserialize<ProtocolWindow>(json));
        return items;
    }
    private static List<string> ReadProtocolJson(SqliteConnection connection, SqliteTransaction? tx, string videoId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT json FROM protocol_windows WHERE video_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        var items = new List<string>();
        while (reader.Read()) items.Add(reader.GetString(0));
        return items;
    }
    private static List<InclusionDecision> ReadInclusions(SqliteConnection connection, SqliteTransaction? tx, string videoId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT json FROM inclusion_decisions WHERE video_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        var items = new List<InclusionDecision>();
        while (reader.Read()) items.Add(JsonDefaults.Deserialize<InclusionDecision>(reader.GetString(0)));
        return items;
    }
    private static List<RankingSnapshot> ReadRankingHistory(SqliteConnection connection, SqliteTransaction? tx, string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT json FROM ranking_evidence WHERE run_id=$id ORDER BY revision, id";
        command.Parameters.AddWithValue("$id", runId);
        using var reader = command.ExecuteReader();
        var items = new List<RankingSnapshot>();
        while (reader.Read()) items.Add(JsonDefaults.Deserialize<RankingSnapshot>(reader.GetString(0)));
        return items;
    }
    private static InspectionMaterial InspectionMaterialFor(SqliteConnection connection, SqliteTransaction? tx, AnalysisRun run) =>
        new(run.Id, run.VideoId, run.SourceSha256, Fingerprint(ReadCorrectionJson(connection, tx, run.Id)), ReviewMaterialFingerprint(connection, tx, run.Id));
    private static string ReviewMaterialFingerprint(SqliteConnection connection, SqliteTransaction? tx, string runId)
    {
        var latest = ReadLatestReview(connection, tx, runId);
        if (latest is null) return MaterialFingerprint.Hash(["review:none"]);
        var contract = latest.Contract is null ? "" : JsonDefaults.Serialize(latest.Contract);
        return MaterialFingerprint.Hash(["review", latest.Id.ToString(CultureInfo.InvariantCulture), latest.Confirmed ? "1" : "0", contract]);
    }
    private static bool NeedsAliasRepair(VideoAsset asset) =>
        !BlindIdentity.IsPersistedRecordingAlias(asset.BlindedAlias, asset)
        || (asset.AnimalIdConfirmed && !string.IsNullOrWhiteSpace(asset.AnimalId) && !BlindIdentity.IsPersistedAnimalAlias(asset.BlindedAnimalAlias, asset));
    private VideoAsset ReadStored(string videoId)
    {
        using var connection = Open();
        return ReadVideo(connection, null, videoId) ?? throw new InvalidOperationException("The stored recording could not be read.");
    }
    private static VideoAsset EnsureAliases(SqliteConnection connection, SqliteTransaction tx, VideoAsset asset, VideoAsset? existing)
    {
        var recording = asset.BlindedAlias?.Trim() ?? "";
        if (!BlindIdentity.IsPersistedRecordingAlias(recording, asset))
        {
            var retained = existing?.BlindedAlias;
            if (retained is not null && BlindIdentity.IsPersistedRecordingAlias(retained, asset)) recording = retained.Trim();
            else
            {
                var stored = ReadStudyAlias(connection, tx, "recording", asset.Id);
                recording = stored is not null && BlindIdentity.IsPersistedRecordingAlias(stored, asset)
                    ? stored
                    : BlindIdentity.CreateRecordingAlias(asset);
            }
        }
        var animalAlias = "";
        if (asset.AnimalIdConfirmed && !string.IsNullOrWhiteSpace(asset.AnimalId))
        {
            var key = asset.AnimalId.Trim();
            var stored = ReadStudyAlias(connection, tx, "animal", key);
            if (stored is null || !BlindIdentity.IsPersistedAnimalAlias(stored, asset))
            {
                var carried = asset.BlindedAnimalAlias;
                if (carried is not null && BlindIdentity.IsPersistedAnimalAlias(carried, asset)) stored = carried.Trim();
                else if (existing?.BlindedAnimalAlias is { } prior && BlindIdentity.IsPersistedAnimalAlias(prior, asset) && string.Equals(existing.AnimalId?.Trim(), key, StringComparison.Ordinal))
                    stored = prior.Trim();
                else stored = BlindIdentity.CreateAnimalAlias(asset);
                WriteStudyAlias(connection, tx, "animal", key, stored);
            }
            animalAlias = stored;
            UpdateSiblingAnimalAliases(connection, tx, asset.Id, key, animalAlias);
        }
        WriteStudyAlias(connection, tx, "recording", asset.Id, recording);
        return asset with { BlindedAlias = recording, BlindedAnimalAlias = animalAlias };
    }
    private static string? ReadStudyAlias(SqliteConnection connection, SqliteTransaction tx, string kind, string subjectKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT alias FROM study_aliases WHERE kind=$kind AND subject_key=$key";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", subjectKey);
        return command.ExecuteScalar() as string;
    }
    private static void WriteStudyAlias(SqliteConnection connection, SqliteTransaction tx, string kind, string subjectKey, string alias)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO study_aliases(kind,subject_key,alias,created_utc) VALUES($kind,$key,$alias,$utc)
            ON CONFLICT(kind,subject_key) DO UPDATE SET alias=excluded.alias
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", subjectKey);
        command.Parameters.AddWithValue("$alias", alias);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
    private static void UpdateSiblingAnimalAliases(SqliteConnection connection, SqliteTransaction tx, string exceptVideoId, string animalId, string alias)
    {
        using var read = connection.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT id, json FROM videos";
        using var reader = read.ExecuteReader();
        var updates = new List<(string Id, string Json)>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (string.Equals(id, exceptVideoId, StringComparison.Ordinal)) continue;
            var asset = JsonDefaults.Deserialize<VideoAsset>(reader.GetString(1));
            if (!asset.AnimalIdConfirmed || !string.Equals(asset.AnimalId?.Trim(), animalId, StringComparison.Ordinal)) continue;
            if (string.Equals(asset.BlindedAnimalAlias, alias, StringComparison.Ordinal)) continue;
            updates.Add((id, JsonDefaults.Serialize(asset with { BlindedAnimalAlias = alias })));
        }
        reader.Close();
        foreach (var (id, json) in updates)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE videos SET json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$json", json);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
    }
    private void EnsureRankingEvidence(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ranking_evidence(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                correction_fingerprint TEXT NOT NULL,
                json TEXT NOT NULL,
                UNIQUE(run_id, revision));
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            SELECT snapshots.run_id, snapshots.json
            FROM ranking_snapshots AS snapshots
            WHERE NOT EXISTS (SELECT 1 FROM ranking_evidence AS evidence WHERE evidence.run_id = snapshots.run_id)
            """;
        using var reader = command.ExecuteReader();
        var legacy = new List<(string RunId, string Json)>();
        while (reader.Read()) legacy.Add((reader.GetString(0), reader.GetString(1)));
        reader.Close();
        foreach (var (runId, json) in legacy)
        {
            var snapshot = JsonDefaults.Deserialize<RankingSnapshot>(json);
            var stored = ReadRun(connection, null, runId);
            var fingerprint = "legacy-unbound";
            if (stored is not null)
            {
                var corrections = ReadCorrectionJson(connection, null, runId).ToList();
                var points = MetricCalculator.ApplyCorrections(stored, corrections.Select(JsonDefaults.Deserialize<ManualCorrection>));
                if (string.Equals(ReviewRankingEvidence.SeedMaterialOf(points), snapshot.SeedMaterial, StringComparison.OrdinalIgnoreCase))
                    fingerprint = Fingerprint(corrections);
            }
            var revision = snapshot.Revision < 1 ? 1 : snapshot.Revision;
            var bound = snapshot with { RunId = runId, Revision = revision, CorrectionFingerprint = fingerprint };
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO ranking_evidence(run_id,revision,correction_fingerprint,json) VALUES($id,$revision,$fingerprint,$json)";
            insert.Parameters.AddWithValue("$id", runId);
            insert.Parameters.AddWithValue("$revision", revision);
            insert.Parameters.AddWithValue("$fingerprint", fingerprint);
            insert.Parameters.AddWithValue("$json", JsonDefaults.Serialize(bound));
            insert.ExecuteNonQuery();
        }
    }
    private static string Fingerprint(IEnumerable<string> storedJson) => MaterialFingerprint.Hash(storedJson);
    private static void WriteAudit(SqliteConnection connection, SqliteTransaction tx, string action, object details)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO audit(created_utc,action,json) VALUES($utc,$action,$json)";
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(details));
        command.ExecuteNonQuery();
    }
    private static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void EnsureReviewContractColumn(SqliteConnection connection)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(reviews)";
        using var reader = inspect.ExecuteReader();
        var found = false;
        while (reader.Read()) if (string.Equals(reader.GetString(1), "contract_json", StringComparison.Ordinal)) found = true;
        reader.Close();
        if (found) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE reviews ADD COLUMN contract_json TEXT";
        alter.ExecuteNonQuery();
    }
    private sealed record EventRow(long RowId, string Json, BiologicalEvent Event);
}
