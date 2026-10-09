using System.Collections.Immutable;
using System.IO;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.SpatialViews;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Desktop;

public sealed record ExperimentTrial(PlannedTrial Slot, AtlasRecording? Recording, TrialSpatialMap? Map, ImmutableArray<string> Holds, ImmutableArray<ProtocolNote> Notes = default);
public sealed record ExperimentResult(AtlasSnapshot Snapshot, StudyPlan Plan, PlannedContext Context, SpatialStudy Study,
    ImmutableArray<ExperimentTrial> Trials, ImmutableArray<AtlasRecording> Unassigned, ImmutableArray<GroupDifferenceBounds> Differences)
{
    public ImmutableDictionary<string, ImmutableArray<CellBounds>> AnimalOccupancyBounds => Study.Animals.Where(a => a.Eligible).ToImmutableDictionary(a => a.Input.AnimalId,
        a => a.KnownFractions.Select(value => new CellBounds(value, Math.Min(1, value + a.UnknownFraction!.Value))).ToImmutableArray(), StringComparer.OrdinalIgnoreCase);
    public string Interpretation => (Plan.Exploratory ? "Exploratory descriptive comparison." : "Primary comparison: " + Plan.PrimaryComparison + ".") +
        (!Plan.DesignConfirmed || Plan.UnitDesign == StudyUnitDesign.Unknown ? " Experimental design is unknown; descriptive maps do not establish learning or inference." : "") +
        " Eligible-only conditional maps; held animals/trials remain listed. Accepted-position bounds are not confidence intervals or independent accuracy proof. No inferential or causal conclusion is performed.";
}

public static class ExperimentPlanAdapter
{
    public static SavedStudyPlan? FrozenPlan(AtlasSnapshot snapshot)
    {
        SavedStudyPlan? latest = null;
        foreach (var row in snapshot.Rows("study_plans"))
        {
            var json = row.Value("json") ?? throw new InvalidDataException("Missing plan material."); var hash = MaterialFingerprint.Hash([json]);
            if (hash != row.Value("fingerprint")) throw new InvalidDataException("Frozen plan history fingerprint differs.");
            var saved = new SavedStudyPlan(long.Parse(row.Value("revision")!, System.Globalization.CultureInfo.InvariantCulture), hash, json,
                row.Value("reviewer")!, row.Value("reason")!, DateTime.Parse(row.Value("created_utc")!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
            if (saved.Revision != (latest?.Revision ?? 0) + 1 || string.IsNullOrWhiteSpace(saved.Reviewer) || string.IsNullOrWhiteSpace(saved.Reason)) throw new InvalidDataException("Frozen plan revision/reviewer history is malformed.");
            saved.Plan.Validate(); latest = saved;
        }
        return latest;
    }
    public static ImmutableArray<BiologicalEvent> EffectiveEvents(AtlasSnapshot snapshot, string videoId) => snapshot.Rows("biological_events")
        .Where(r => r.Value("video_id") == videoId).Select(r => JsonDefaults.Deserialize<BiologicalEvent>(r.Value("json")!))
        .GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.OrderBy(e => e.Revision).Last()).OrderBy(e => e.Id, StringComparer.Ordinal).ToImmutableArray();
    public static ProtocolWindow? Protocol(AtlasSnapshot snapshot, string videoId) => snapshot.Rows("protocol_windows")
        .LastOrDefault(r => r.Value("video_id") == videoId)?.Value("json") is { } json ? JsonDefaults.Deserialize<ProtocolWindow>(json) : null;
    public static bool CurrentInspection(AtlasRecording row)
    {
        var run = row.RawRun; if (run is null || !CompletedRecordingContract.TryValidate(run, out _)) return false;
        var asset = row.Asset; var snapshot = row.Snapshot;
        var latest = snapshot.Rows("reviews").LastOrDefault(r => r.Value("run_id") == run.Id);
        if (latest?.Value("confirmed") != "1" || latest.Value("contract_json") is not { } json) return false;
        try
        {
            var contract = JsonDefaults.Deserialize<ReviewContract>(json);
            var events = EffectiveEvents(snapshot, asset.Id);
            // Fingerprints use original stored JSON, not reserialized event objects.
            var effectiveIds = events.ToDictionary(e => e.Id, e => e.Revision, StringComparer.Ordinal);
            var eventJson = snapshot.Rows("biological_events").Where(r => r.Value("video_id") == asset.Id)
                .Select(r => (Json: r.Value("json")!, Event: JsonDefaults.Deserialize<BiologicalEvent>(r.Value("json")!)))
                .Where(e => effectiveIds.GetValueOrDefault(e.Event.Id) == e.Event.Revision).GroupBy(e => e.Event.Id, StringComparer.Ordinal)
                .Select(g => g.Last()).OrderBy(e => e.Event.Id, StringComparer.Ordinal).Select(e => e.Json);
            return run.SourceSha256 == asset.SourceSha256 && run.Metadata == asset.Metadata && contract.RunId == run.Id && contract.VideoId == asset.Id &&
                contract.SourceSha256 == asset.SourceSha256 && contract.SettingsHash == StudyStore.SettingsHash(run.Settings) && contract.SettingsHash == StudyStore.SettingsHash(asset.Settings) &&
                contract.CorrectionFingerprint == MaterialFingerprint.Hash(snapshot.Rows("corrections").Where(r => r.Value("run_id") == run.Id).Select(r => r.Value("json")!)) &&
                contract.EventFingerprint == MaterialFingerprint.Hash(eventJson) &&
                contract.ProtocolFingerprint == MaterialFingerprint.Hash(snapshot.Rows("protocol_windows").Where(r => r.Value("video_id") == asset.Id).TakeLast(1).Select(r => r.Value("json")!));
        }
        catch (Exception) { return false; }
    }
    public static ExperimentResult Build(AtlasSnapshot snapshot, string contextId, CancellationToken cancellation = default)
    {
        var plan = FrozenPlan(snapshot)?.Plan ?? throw new InvalidOperationException("Enter and confirm an expected plan before comparison.");
        var context = plan.Contexts.Single(c => c.Id == contextId);
        var inventory = StudyPlanInventory.Build(plan, snapshot.Recordings.Select(r => r.Asset));
        var grid = new GridDefinition(12, 12, -1.01, -1.01, 1.01, 1.01, "pool radii", context.CommonCoordinateReference.Length > 0 ? context.CommonCoordinateReference : "reference unconfirmed",
            context.Orientation.ToString() + " normalized pool coordinates; not projective calibration");
        var scope = new SpatialComparisonScope(grid, context.Id + "; " + context.Phase + "; day " + context.Day, "confirmed release-relative planned comparison", Math.Max(context.ComparisonDurationSeconds, .001));
        var trials = ImmutableArray.CreateBuilder<ExperimentTrial>();
        foreach (var item in inventory.Where(i => string.Equals(i.Trial.ContextId, context.Id, StringComparison.OrdinalIgnoreCase)))
        {
            cancellation.ThrowIfCancellationRequested();
            var holds = item.Holds.ToBuilder(); var notes = ImmutableArray.CreateBuilder<ProtocolNote>(); var row = snapshot.Recordings.SingleOrDefault(r => r.Asset.Id == item.Recording?.Id); TrialSpatialMap? map = null;
            if (context.Phase != TrialMode.Probe) holds.Add("Training retains its own termination; fixed probe group maps are held.");
            if (row is not null)
            {
                try
                {
                    var raw = row.RawRun ?? throw new InvalidDataException("No completed analysis."); CompletedRecordingContract.Validate(raw);
                    if (HashSource(row.Asset.Path, cancellation) != raw.SourceSha256) throw new InvalidDataException("Original source bytes changed; revalidation required.");
                    var run = row.EffectiveRun()!; var asset = row.Asset; var s = run.Settings; var meta = run.Metadata;
                    if (!CurrentInspection(row)) holds.Add("Tracking inspection is missing, invalidated or stale; no current material contract.");
                    if (!item.Trial.SourceReferenceConfirmed || item.Trial.ReferenceSourceSha256 != asset.SourceSha256 || item.Trial.ReferenceSettingsHash != StudyStore.SettingsHash(s))
                        holds.Add("Per-source common-reference confirmation/hash differs; camera/geometry alignment held.");
                    var events = EffectiveEvents(snapshot, asset.Id); var protocol = Protocol(snapshot, asset.Id) ?? throw new InvalidDataException("Confirmed release/protocol missing.");
                    if (protocol.VideoId != asset.Id || protocol.Revision < 1 || string.IsNullOrWhiteSpace(protocol.Reviewer) || string.IsNullOrWhiteSpace(protocol.Reason))
                        throw new InvalidDataException("Malformed frozen protocol identity/revision/reviewer evidence.");
                    if (events.Any(e => e.VideoId != asset.Id || e.Frame < 0 || e.Frame >= meta.FrameCount || e.Revision < 1 || string.IsNullOrWhiteSpace(e.Id) ||
                        !Enum.IsDefined(e.Kind) || !double.IsFinite(e.TimeSeconds) || Math.Abs(e.TimeSeconds - e.Frame / meta.Fps) > .001 || string.IsNullOrWhiteSpace(e.Reviewer) || string.IsNullOrWhiteSpace(e.Reason)))
                        throw new InvalidDataException("Malformed frozen biological event identity/timing evidence.");
                    var outcome = ProtocolEvaluator.Evaluate(protocol, meta, asset.Phase, events);
                    holds.AddRange(outcome.Holds);
                    notes.AddRange(outcome.Notes);
                    if (protocol.Phase != context.Phase || Math.Abs(protocol.RequestedExposureSeconds - context.RequestedExposureSeconds) > 1e-9)
                        holds.Add("Stored protocol phase/requested exposure differs from planned context.");
                    if (!protocol.ReleaseConfirmed || !protocol.WindowConfirmed || protocol.ReleaseFrame is null) holds.Add("Confirmed release/window missing.");
                    var rotation = item.Trial.RoomRotationRadians;
                    if (context.Orientation == StudyOrientation.TargetAligned)
                    {
                        if (s.Platform is not { } site) throw new InvalidDataException("Target alignment requires a platform direction.");
                        var px = (site.X - s.Cx) / s.Rx; var py = (site.Y - s.Cy) / s.Ry;
                        if (Math.Sqrt(px * px + py * py) < 1e-6) throw new InvalidDataException("Central platform has no target direction.");
                        rotation = -Math.Atan2(py, px);
                    }
                    var origin = (protocol.ReleaseFrame ?? 0) / meta.Fps + context.ComparisonStartSeconds;
                    var frames = run.Points.Select(p =>
                    {
                        double? x = null, y = null;
                        if (p.Accepted)
                        {
                            var u = (p.X!.Value - s.Cx * meta.Width) / (s.Rx * meta.Width); var v = (p.Y!.Value - s.Cy * meta.Height) / (s.Ry * meta.Height);
                            x = u * Math.Cos(rotation) - v * Math.Sin(rotation); y = u * Math.Sin(rotation) + v * Math.Cos(rotation);
                        }
                        return new SpatialFrame(p.Frame, p.Frame / meta.Fps - origin, (p.Frame + 1d) / meta.Fps - origin, x, y, p.Accepted);
                    }).ToImmutableArray();
                    var input = new SpatialTrialInput(item.Trial.Id, raw.Id, raw.SourceSha256, snapshot.Revision, item.Trial.ReferenceSettingsHash,
                        scope.ContextKey, scope.WindowBasis, scope.PlannedSeconds, holds.Count == 0 && context.WindowConfirmed, s.ArenaConfirmed && context.AlignmentConfirmed, CurrentInspection(row), grid, frames);
                    map = SpatialCalculator.Trial(input); holds.AddRange(map.HoldReasons);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { holds.Add("Recording comparison held: " + ex.Message); }
            }
            trials.Add(new(item.Trial, row, map, holds.Distinct().ToImmutableArray(), notes.ToImmutable()));
        }
        var inputs = plan.Animals.Select(animal =>
        {
            var slots = trials.Where(t => string.Equals(t.Slot.AnimalId, animal.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            var maps = slots.Where(t => t.Map is not null).Select(t => t.Map!).ToImmutableArray();
            return new AnimalSpatialInput(animal.Id, string.IsNullOrWhiteSpace(animal.Alias) ? "Planned animal" : animal.Alias,
                animal.AllocationConfirmed ? animal.Group : "", animal.IdentityConfirmed, plan.RosterConfirmed && slots.All(t => t.Holds.IsEmpty),
                slots.Select(t => t.Slot.Id).ToImmutableArray(), maps, animal.Excluded, animal.ExclusionReason);
        }).ToImmutableArray();
        var study = SpatialCalculator.Study(inputs, scope);
        var differences = ImmutableArray.CreateBuilder<GroupDifferenceBounds>();
        for (var a = 0; a < study.Groups.Length; a++) for (var b = a + 1; b < study.Groups.Length; b++) differences.Add(SpatialCalculator.Difference(study.Groups[a], study.Groups[b]));
        var assigned = plan.Trials.Where(t => t.RecordingId is not null).Select(t => t.RecordingId).ToHashSet(StringComparer.Ordinal);
        return new(snapshot, plan, context, study, trials.ToImmutable(), snapshot.Recordings.Where(r => !assigned.Contains(r.Asset.Id)).ToImmutableArray(), differences.ToImmutable());
    }
    internal static string HashSource(string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; int count;
        while ((count = stream.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
