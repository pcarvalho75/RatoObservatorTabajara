using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;

namespace MouseTrackerTabajara.Application;

public enum StudyIntakeSlotAction { Unchanged, Fill, AddAndFill, AddMissing, KeepMapped, KeepMissing }
public enum StudyIntakeOutcome { Applied, NoChanges, Refused, Stale, Failed, Cancelled }
/// <summary>InPlanTransaction runs inside the plan write transaction, after the recording guard passed; cancellation is no longer honored there.</summary>
public enum StudyIntakeCheckpoint { BeforeImport, AfterImport, BeforePlanSave, InPlanTransaction }

/// <summary>Apply found state that differs from the reviewed preview; nothing further is written.</summary>
public sealed class StudyIntakeStaleException(string message) : Exception(message);

public sealed record StudyIntakeFile(int Line, string Path, string Sha256, long Length, DateTime WrittenUtc, DateTime CreatedUtc, string? ExistingRecordingId);

public sealed record StudyIntakeSlot(string AnimalId, int Day, int Trial, string ContextId, string TrialId, StudyIntakeSlotAction Action,
    int? Line = null, string? Sha256 = null, string? RecordingId = null, string? VideoPath = null)
{
    public bool MappedAfterApply => Action is StudyIntakeSlotAction.Unchanged or StudyIntakeSlotAction.Fill or StudyIntakeSlotAction.AddAndFill or StudyIntakeSlotAction.KeepMapped;
}

public sealed record StudyIntakeAnimal(int Index, string AnimalId, string Group, bool New, int Rows, int MappedAfterApply, int Expected, ImmutableArray<string> Updates);

public sealed record StudyIntakeProgress(int Done, int Total, string Stage);

/// <summary>Immutable reviewed preview. Apply re-derives nothing: it replays these described operations onto the same plan revision.</summary>
public sealed record StudyIntakePreview
{
    public string PreviewId { get; init; } = "";
    public StudyIntakeProtocol Protocol { get; init; } = new("", [], []);
    public StudyIntakeManifest Manifest { get; init; } = new("", "", [], []);
    public long PlanRevision { get; init; }
    public string PlanFingerprint { get; init; } = "";
    public string VideosFingerprint { get; init; } = "";
    public ImmutableArray<StudyIntakeFile> Files { get; init; } = [];
    public ImmutableArray<StudyIntakeAnimal> Animals { get; init; } = [];
    public ImmutableArray<StudyIntakeSlot> Slots { get; init; } = [];
    public ImmutableArray<StudyIntakeIssue> Issues { get; init; } = [];
    public ImmutableArray<string> Changes { get; init; } = [];
    public string? NewTitle { get; init; }
    public bool ClearRosterConfirmation { get; init; }
    public ImmutableArray<PlannedContext> AddContexts { get; init; } = [];
    public ImmutableArray<PlannedAnimal> AddAnimals { get; init; } = [];
    public ImmutableArray<PlannedAnimal> UpdateAnimals { get; init; } = [];
    public DateTime CreatedUtc { get; init; }

    public bool Blocked => Issues.Any(i => i.Blocking);
    public ImmutableArray<StudyIntakeIssue> Conflicts => Issues.Where(i => i.Blocking).ToImmutableArray();
    public ImmutableArray<StudyIntakeIssue> Warnings => Issues.Where(i => !i.Blocking).ToImmutableArray();
    public int PendingImports => Files.Count(f => f.ExistingRecordingId is null);
    public int ExistingRecordings => Files.Count(f => f.ExistingRecordingId is not null);
    public int ExpectedSlots => Animals.Length * Protocol.ExpectedSlotsPerAnimal;
    public ImmutableArray<StudyIntakeSlot> MissingSlots => Slots.Where(s => !s.MappedAfterApply).ToImmutableArray();
    public int MappedSlots => Slots.Count(s => s.MappedAfterApply);
    public int Count(StudyIntakeSlotAction action) => Slots.Count(s => s.Action == action);
    public bool HasChanges => PendingImports > 0 || NewTitle is not null || ClearRosterConfirmation || !AddContexts.IsEmpty || !AddAnimals.IsEmpty || !UpdateAnimals.IsEmpty ||
        Slots.Any(s => s.Action is StudyIntakeSlotAction.Fill or StudyIntakeSlotAction.AddAndFill or StudyIntakeSlotAction.AddMissing);
}

public sealed record StudyIntakeReceipt(string ReceiptId, StudyIntakeOutcome Outcome, string PreviewId, string ManifestSha256, long PlanRevisionBefore, long? PlanRevisionAfter,
    ImmutableArray<string> RegisteredRecordingIds, string Reviewer, string Message, DateTime CreatedUtc);

/// <summary>Bulk study intake through the accepted StudyStore/StudyPlanStore contracts. Never confirms identity, allocation,
/// roster, design, geometry, windows or inspection; never overwrites mapped trials or edits existing recording metadata.</summary>
public static class StudyIntakeReconciler
{
    public const string NewSlotMissingReason = "Expected human protocol trial; recording not assigned.";
    public const string AuditPrefix = "study_intake_";

    public static async Task<StudyIntakePreview> PreviewAsync(StudyStore store, string manifestPath, StudyIntakeProtocol protocol,
        IProgress<StudyIntakeProgress>? progress = null, CancellationToken cancellation = default)
    {
        protocol.Validate();
        return await Task.Run(() =>
        {
            var manifest = StudyIntakeManifest.Read(manifestPath, protocol);
            var latest = new StudyPlanStore(store).Latest();
            var videos = store.Videos(); var registered = videos.Select(v => v.SourceSha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, StudyIntakeFile>(StringComparer.OrdinalIgnoreCase);
            var issues = manifest.Issues.ToBuilder();
            var hash = !manifest.Issues.Any(i => i.Blocking);
            var distinct = manifest.Rows.DistinctBy(r => r.VideoPath, StringComparer.OrdinalIgnoreCase).ToArray();
            for (var i = 0; i < distinct.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested(); var row = distinct[i];
                progress?.Report(new(i, distinct.Length, hash ? "Hashing source files" : "Checking source files"));
                var info = new FileInfo(row.VideoPath);
                if (!info.Exists) { issues.Add(new("missing-file", $"Line {row.Line}: video file not found: {row.VideoPath}. Fix the path or remove the row.", [row.Line])); continue; }
                if (!hash) continue;
                try
                {
                    var before = (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
                    var sha = HashFile(row.VideoPath, cancellation); info.Refresh();
                    if (before != (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc))
                    { issues.Add(new("file-changing", $"Line {row.Line}: video file changed while it was being read. Wait for copying to finish and preview again.", [row.Line])); continue; }
                    // Sources not yet registered must open in the same native decoder used for import and analysis.
                    if (!registered.Contains(sha)) try { OpenCvTracker.Probe(row.VideoPath); }
                        catch (Exception ex) when (ex is InvalidDataException or IOException or OpenCvSharp.OpenCVException)
                        {
                            issues.Add(new("undecodable-video", $"Line {row.Line}: the native video decoder cannot open this file. Check it plays; folders or names with accents or other non-ASCII characters are not supported by the decoder — move or rename it to a plain-ASCII path.", [row.Line]));
                            continue;
                        }
                    files[row.VideoPath] = new(row.Line, row.VideoPath, sha, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { issues.Add(new("unreadable-file", $"Line {row.Line}: video file could not be read ({ex.GetType().Name}). Close other programs using it and preview again.", [row.Line])); }
            }
            progress?.Report(new(distinct.Length, distinct.Length, "Reconciling with the saved plan"));
            if (!hash) issues.Add(new("content-unchecked", "Source contents were not hashed because the manifest has errors. Fix them and preview again.", [], false));
            return Reconcile(manifest with { Issues = issues.ToImmutable() }, files, latest, videos, protocol);
        }, cancellation).ConfigureAwait(false);
    }

    /// <summary>Pure reconciliation of a parsed manifest and hashed files against the current plan and registered recordings.</summary>
    public static StudyIntakePreview Reconcile(StudyIntakeManifest manifest, IReadOnlyDictionary<string, StudyIntakeFile> hashed, SavedStudyPlan? latest,
        IReadOnlyList<VideoAsset> videos, StudyIntakeProtocol protocol)
    {
        protocol.Validate();
        var issues = manifest.Issues.ToBuilder(); var changes = ImmutableArray.CreateBuilder<string>();
        var plan = latest?.Plan ?? new StudyPlan(); plan.Validate();
        var bySha = videos.GroupBy(v => v.SourceSha256, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byPath = videos.GroupBy(v => SafeFullPath(v.Path), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var files = manifest.Rows.Where(r => hashed.ContainsKey(r.VideoPath)).Select(r => hashed[r.VideoPath])
            .Select(f => f with { ExistingRecordingId = bySha.TryGetValue(f.Sha256, out var v) ? v.Id : null }).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        var fileByPath = files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var content in files.GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new("duplicate-content", "Identical video bytes (same SHA-256) appear at different paths. Repeated bytes are never a new trial; keep one row.", content.Select(f => f.Line).Order().ToImmutableArray()));
        foreach (var file in files)
            if (byPath.TryGetValue(file.Path, out var prior) && prior.Any(v => !string.Equals(v.SourceSha256, file.Sha256, StringComparison.OrdinalIgnoreCase)) && file.ExistingRecordingId is null)
                issues.Add(new("source-changed", $"Line {file.Line}: this path was imported earlier with different bytes. Source content changed; it is not treated as the same recording. Restore the original or remove the row.", [file.Line]));

        // Contexts: reuse exact protocol stages, add missing ones, refuse ambiguous or contradictory stages.
        var addContexts = ImmutableArray.CreateBuilder<PlannedContext>();
        foreach (var stage in protocol.Stages)
        {
            var match = plan.Contexts.FirstOrDefault(c => string.Equals(c.Id, stage.ContextId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                if (match.Day != stage.Day || match.Phase != stage.Phase)
                    issues.Add(new("context-conflict", $"Plan context '{match.Id}' is day {match.Day} {match.Phase}, but the protocol needs day {stage.Day} {stage.Phase}. Correct it in the plan editor.", []));
                continue;
            }
            var other = plan.Contexts.FirstOrDefault(c => c.Day == stage.Day && c.Phase == stage.Phase);
            if (other is not null)
            { issues.Add(new("context-ambiguous", $"Plan context '{other.Id}' already describes day {stage.Day} {stage.Phase}; intake uses '{stage.ContextId}'. Rename or remove one explicitly, then preview again.", [])); continue; }
            addContexts.Add(new() { Id = stage.ContextId, Day = stage.Day, Phase = stage.Phase });
            changes.Add($"Add protocol context {stage.ContextId} (day {stage.Day}, {stage.Phase}); window, duration and alignment stay unconfirmed.");
        }

        var assigned = plan.Trials.Where(t => t.RecordingId is not null).ToDictionary(t => t.RecordingId!, StringComparer.Ordinal);
        var trialIds = plan.Trials.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bySlot = plan.Trials.ToDictionary(t => (t.AnimalId.ToUpperInvariant(), t.ContextId.ToUpperInvariant(), t.Order));
        var addAnimals = ImmutableArray.CreateBuilder<PlannedAnimal>(); var updateAnimals = ImmutableArray.CreateBuilder<PlannedAnimal>();
        var animals = ImmutableArray.CreateBuilder<StudyIntakeAnimal>(); var slots = ImmutableArray.CreateBuilder<StudyIntakeSlot>();
        var index = 0;
        foreach (var group in manifest.Rows.GroupBy(r => r.AnimalId, StringComparer.OrdinalIgnoreCase))
        {
            index++; var first = group.First(); var existing = plan.Animals.FirstOrDefault(a => string.Equals(a.Id, first.AnimalId, StringComparison.OrdinalIgnoreCase));
            var id = existing?.Id ?? first.AnimalId; var updates = ImmutableArray.CreateBuilder<string>();
            string Pick(Func<StudyIntakeRow, string> select) => group.Select(select).FirstOrDefault(v => v.Length > 0) ?? "";
            var wanted = (Group: first.Group, Alias: Pick(r => r.Alias), Cage: Pick(r => r.Cage), Litter: Pick(r => r.Litter));
            string effectiveGroup;
            if (existing is null)
            {
                addAnimals.Add(new() { Id = id, Group = wanted.Group, Alias = wanted.Alias, Cage = wanted.Cage, Litter = wanted.Litter });
                changes.Add($"Add animal {id} to {wanted.Group}; identity and allocation stay unconfirmed.");
                effectiveGroup = wanted.Group;
            }
            else
            {
                var updated = existing;
                if (existing.Group.Length > 0 && !string.Equals(existing.Group, wanted.Group, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new("allocation-conflict", $"Animal {id}: plan group '{existing.Group}' differs from manifest '{wanted.Group}'. Intake never reallocates; correct the manifest or the plan explicitly.", Lines(group)));
                else if (existing.Group.Length == 0) { updated = updated with { Group = wanted.Group }; updates.Add("group " + wanted.Group); }
                foreach (var (label, current, value, apply) in new (string, string, string, Func<PlannedAnimal, string, PlannedAnimal>)[]
                    { ("alias", existing.Alias, wanted.Alias, (a, v) => a with { Alias = v }), ("cage", existing.Cage, wanted.Cage, (a, v) => a with { Cage = v }), ("litter", existing.Litter, wanted.Litter, (a, v) => a with { Litter = v }) })
                {
                    if (value.Length == 0 || string.Equals(current, value, StringComparison.Ordinal)) continue;
                    if (current.Length > 0) issues.Add(new("animal-field-conflict", $"Animal {id}: plan {label} '{current}' differs from manifest '{value}'. Existing values are never overwritten.", Lines(group)));
                    else { updated = apply(updated, value); updates.Add(label + " " + value); }
                }
                if (updates.Count > 0) { updateAnimals.Add(updated); changes.Add($"Fill blank {string.Join(", ", updates)} for existing animal {id}; confirmations unchanged."); }
                if (existing.Excluded) issues.Add(new("animal-excluded", $"Animal {id} is excluded in the plan ({existing.ExclusionReason}); its trials stay held.", Lines(group), false));
                effectiveGroup = existing.Group.Length > 0 ? existing.Group : wanted.Group;
            }

            var mapped = 0;
            foreach (var stage in protocol.Stages)
            {
                for (var order = 1; order <= stage.Trials; order++)
                {
                    var trial = bySlot.GetValueOrDefault((id.ToUpperInvariant(), stage.ContextId.ToUpperInvariant(), order));
                    var row = group.FirstOrDefault(r => r.Day == stage.Day && r.Trial == order);
                    StudyIntakeSlot slot;
                    if (row is null || !fileByPath.TryGetValue(row.VideoPath, out var file))
                    {
                        if (trial is not null) slot = new(id, stage.Day, order, stage.ContextId, trial.Id, trial.RecordingId is null ? StudyIntakeSlotAction.KeepMissing : StudyIntakeSlotAction.KeepMapped, row?.Line, null, trial.RecordingId);
                        else slot = new(id, stage.Day, order, stage.ContextId, NewTrialId(id, stage.Day, order, trialIds), StudyIntakeSlotAction.AddMissing, row?.Line);
                    }
                    else
                    {
                        var recording = file.ExistingRecordingId;
                        var trialId = trial?.Id ?? NewTrialId(id, stage.Day, order, trialIds);
                        slot = new(id, stage.Day, order, stage.ContextId, trialId, trial is null ? StudyIntakeSlotAction.AddAndFill : StudyIntakeSlotAction.Fill, row.Line, file.Sha256, recording, file.Path);
                        if (recording is not null && assigned.TryGetValue(recording, out var holder))
                        {
                            if (trial is not null && string.Equals(holder.Id, trial.Id, StringComparison.OrdinalIgnoreCase)) slot = slot with { Action = StudyIntakeSlotAction.Unchanged };
                            else issues.Add(new("recording-already-mapped", $"Line {row.Line}: this recording already fills slot {holder.Id} (animal {holder.AnimalId}). One video maps to one planned trial; intake never moves mappings.", [row.Line]));
                        }
                        else if (trial?.RecordingId is not null)
                            issues.Add(new("slot-occupied", $"Line {row.Line}: animal {id} day {stage.Day} trial {order} already holds another recording. Intake never overwrites mapped trials; edit the plan explicitly.", [row.Line]));
                        if (recording is not null) RecordingChecks(bySha[file.Sha256], row, id, effectiveGroup, stage, issues);
                    }
                    if (slot.MappedAfterApply) mapped++;
                    slots.Add(slot);
                }
                foreach (var extra in plan.Trials.Where(t => string.Equals(t.AnimalId, id, StringComparison.OrdinalIgnoreCase) && string.Equals(t.ContextId, stage.ContextId, StringComparison.OrdinalIgnoreCase) && t.Order > stage.Trials))
                    issues.Add(new("extra-slot", $"Animal {id}: existing slot {extra.Id} (day {stage.Day}, trial {extra.Order}) exceeds the protocol; preserved unchanged.", [], false));
            }
            animals.Add(new(index, id, effectiveGroup, existing is null, group.Count(), mapped, protocol.ExpectedSlotsPerAnimal, updates.ToImmutable()));
        }
        var added = slots.Count(s => s.Action is StudyIntakeSlotAction.AddAndFill or StudyIntakeSlotAction.AddMissing);
        var filled = slots.Count(s => s.Action is StudyIntakeSlotAction.Fill or StudyIntakeSlotAction.AddAndFill);
        if (added > 0) changes.Add($"Add {added} expected trial slots ({slots.Count(s => s.Action == StudyIntakeSlotAction.AddMissing)} stay explicitly missing).");
        if (filled > 0) changes.Add($"Assign {filled} recordings to empty slots; {files.Count(f => f.ExistingRecordingId is null)} new source files will be registered with manifest identity marked unconfirmed.");
        var clearRoster = plan.RosterConfirmed && (added > 0 || addAnimals.Count > 0);
        if (clearRoster) changes.Add("Clear roster confirmation: the expected roster changes and must be reconfirmed locally.");
        var title = string.IsNullOrWhiteSpace(plan.Title) && (added > 0 || filled > 0 || addAnimals.Count > 0) ? protocol.Name + " — unconfirmed bulk intake" : null;
        if (title is not null) changes.Add("Set blank plan title to '" + title + "'.");

        var videosFingerprint = VideosFingerprint(videos);
        var previewId = Hash(JsonDefaults.Serialize(new
        {
            manifest.Sha256, manifest.Path, Revision = latest?.Revision ?? 0, PlanFingerprint = latest?.Fingerprint ?? "", videosFingerprint, protocol.Name,
            Files = files.Select(f => new { f.Path, f.Sha256, f.Length, f.WrittenUtc, f.CreatedUtc })
        }));
        return new()
        {
            PreviewId = previewId, Protocol = protocol, Manifest = manifest, PlanRevision = latest?.Revision ?? 0, PlanFingerprint = latest?.Fingerprint ?? "",
            VideosFingerprint = videosFingerprint, Files = files, Animals = animals.ToImmutable(), Slots = slots.ToImmutable(), Issues = issues.ToImmutable(),
            Changes = changes.ToImmutable(), NewTitle = title, ClearRosterConfirmation = clearRoster, AddContexts = addContexts.ToImmutable(),
            AddAnimals = addAnimals.ToImmutable(), UpdateAnimals = updateAnimals.ToImmutable(), CreatedUtc = DateTime.UtcNow
        };
    }

    private static void RecordingChecks(VideoAsset recording, StudyIntakeRow row, string animalId, string group, StudyIntakeStage stage, ImmutableArray<StudyIntakeIssue>.Builder issues)
    {
        if (recording.AnimalIdConfirmed && !string.Equals(recording.AnimalId.Trim(), animalId, StringComparison.OrdinalIgnoreCase))
        { issues.Add(new("identity-conflict", $"Line {row.Line}: recording identity is confirmed as animal '{recording.AnimalId}', not '{animalId}'. Different animals are never combined.", [row.Line])); return; }
        if (!string.Equals(recording.AnimalId.Trim(), animalId, StringComparison.OrdinalIgnoreCase) || !string.Equals(recording.GroupName, group, StringComparison.Ordinal) ||
            recording.Day != stage.Day || recording.Trial != row.Trial || recording.Phase != stage.Phase)
            issues.Add(new("recording-metadata-differs", $"Line {row.Line}: the imported recording's animal/group/day/trial/phase differs from this slot. It is preserved unchanged; the trial stays held until edited and confirmed locally.", [row.Line], false));
        if (!recording.Included) issues.Add(new("recording-excluded", $"Line {row.Line}: recording is excluded ({recording.ExclusionReason}); the exclusion is preserved.", [row.Line], false));
    }

    public static async Task<StudyIntakeReceipt> ApplyAsync(StudyStore store, StudyIntakePreview preview, string reviewer, string reason, bool reviewed,
        CancellationToken cancellation = default, Func<StudyIntakeCheckpoint, int, CancellationToken, Task>? checkpoint = null)
    {
        var registered = ImmutableArray.CreateBuilder<string>(); long? after = null;
        var registeredIdentity = new Dictionary<string, RecordingIdentity>(StringComparer.Ordinal);
        StudyIntakeReceipt Receipt(StudyIntakeOutcome outcome, string message) =>
            new(Guid.NewGuid().ToString("N"), outcome, preview.PreviewId, preview.Manifest.Sha256, preview.PlanRevision, after, registered.ToImmutable(), (reviewer ?? "").Trim(), message, DateTime.UtcNow);
        StudyIntakeReceipt Record(StudyIntakeReceipt receipt)
        {
            try { store.Audit(AuditPrefix + receipt.Outcome.ToString().ToLowerInvariant(), receipt with { Message = receipt.Message + (string.IsNullOrWhiteSpace(reason) ? "" : " Reason: " + reason.Trim()) }); return receipt; }
            catch (Exception) { return receipt with { Message = receipt.Message + " Receipt audit could not be written." }; }
        }
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason)) return Record(Receipt(StudyIntakeOutcome.Refused, "Reviewer and reason are required before applying an intake."));
        if (!reviewed) return Record(Receipt(StudyIntakeOutcome.Refused, "Confirm that the preview, conflicts and missing slots were reviewed before applying."));
        if (preview.Blocked) return Record(Receipt(StudyIntakeOutcome.Refused, $"Preview has {preview.Conflicts.Length} blocking conflicts; nothing was applied."));
        if (!preview.HasChanges) return Record(Receipt(StudyIntakeOutcome.NoChanges, "Manifest already matches the plan and recordings; no revision was written."));
        try
        {
            if (await Task.Run(() => StaleReason(store, preview, cancellation), cancellation).ConfigureAwait(false) is { } stale)
                return Record(Receipt(StudyIntakeOutcome.Stale, stale + " Preview again; nothing was applied."));
            var resolved = preview.Files.Where(f => f.ExistingRecordingId is not null).ToDictionary(f => f.Sha256, f => f.ExistingRecordingId!, StringComparer.OrdinalIgnoreCase);
            var pending = preview.Files.Where(f => f.ExistingRecordingId is null).OrderBy(f => f.Line).ToArray();
            for (var i = 0; i < pending.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (checkpoint is not null) await checkpoint(StudyIntakeCheckpoint.BeforeImport, i, cancellation).ConfigureAwait(false);
                var file = pending[i];
                var slot = preview.Slots.Single(s => string.Equals(s.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase) && s.Action is StudyIntakeSlotAction.Fill or StudyIntakeSlotAction.AddAndFill);
                var group = preview.Animals.Single(a => string.Equals(a.AnimalId, slot.AnimalId, StringComparison.OrdinalIgnoreCase)).Group;
                var asset = await Task.Run(() => Register(store, file, slot, group, preview.Protocol.Stage(slot.Day)!.Phase, cancellation), cancellation).ConfigureAwait(false);
                registered.Add(asset.Id); registeredIdentity[asset.Id] = RecordingIdentity.Of(asset); resolved[file.Sha256] = asset.Id;
                if (checkpoint is not null) await checkpoint(StudyIntakeCheckpoint.AfterImport, i, cancellation).ConfigureAwait(false);
            }
            if (checkpoint is not null) await checkpoint(StudyIntakeCheckpoint.BeforePlanSave, pending.Length, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var plans = new StudyPlanStore(store); var latest = plans.Latest();
            if ((latest?.Revision ?? 0) != preview.PlanRevision || (latest?.Fingerprint ?? "") != preview.PlanFingerprint)
                return Record(Receipt(StudyIntakeOutcome.Stale, "Study plan changed during apply; no plan revision was written. Registered sources remain unassigned imports; preview again."));
            var current = latest?.Plan ?? new StudyPlan();
            var built = BuildPlan(current, preview, resolved);
            VerifyBoundedUpdate(current, built, preview);
            // Recheck bytes after all apply checkpoints and retain read-share handles through CAS and its outcome.
            // Hashing stays outside the database write transaction; Windows denies source replacement while it commits.
            using var sourceLease = await Task.Run(() => StudyIntakeSourceLease.Acquire(preview.Files, cancellation), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var marker = $" [bulk intake {preview.PreviewId[..12]}; manifest sha256 {preview.Manifest.Sha256}]";
            // The recording check runs inside the plan's write transaction, so no metadata edit can land between check and revision.
            // Once that transaction starts it finishes (or rolls back); cancellation is not honored inside it.
            var saved = await Task.Run(() => plans.Save(built, reviewer, reason.Trim() + marker, preview.PlanRevision, videos =>
            {
                VerifyRecordings(preview, videos, registeredIdentity, resolved);
                checkpoint?.Invoke(StudyIntakeCheckpoint.InPlanTransaction, pending.Length, CancellationToken.None).GetAwaiter().GetResult();
            }), CancellationToken.None).ConfigureAwait(false);
            after = saved.Revision;
            return Record(Receipt(StudyIntakeOutcome.Applied, $"Plan revision {saved.Revision} saved: {preview.Animals.Length} animals, {preview.MappedSlots}/{preview.ExpectedSlots} expected slots mapped, {preview.MissingSlots.Length} explicitly missing, {registered.Count} sources registered. No identity, allocation, roster, design, geometry, window or inspection was confirmed."));
        }
        catch (StudyIntakeSourceLeaseException ex)
        { return Record(Receipt(StudyIntakeOutcome.Stale, $"{ex.Message} No plan revision was written. {registered.Count} registered sources remain unassigned imports; preview again.")); }
        catch (StudyIntakeStaleException ex)
        { return Record(Receipt(StudyIntakeOutcome.Stale, $"{ex.Message} No plan revision was written. {registered.Count} sources registered by this attempt remain unassigned imports, unchanged; preview again.")); }
        catch (OperationCanceledException)
        { return Record(Receipt(StudyIntakeOutcome.Cancelled, $"Intake cancelled; no plan revision was written. {registered.Count} sources registered before cancellation remain unassigned imports and will be matched by content on the next preview.")); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("changed since it was opened", StringComparison.Ordinal))
        { return Record(Receipt(StudyIntakeOutcome.Stale, "Study plan changed during apply; no plan revision was written. Preview again.")); }
        catch (Exception ex)
        { return Record(Receipt(StudyIntakeOutcome.Failed, $"Intake failed ({ex.Message}); no plan revision was written. {registered.Count} sources registered before the failure remain unassigned imports.")); }
    }

    /// <summary>Replays the reviewed operations onto the unchanged plan. Every new mapping must resolve to a registered recording.</summary>
    public static StudyPlan BuildPlan(StudyPlan current, StudyIntakePreview preview, IReadOnlyDictionary<string, string> recordingBySha)
    {
        string Resolve(StudyIntakeSlot slot) => slot.RecordingId ?? (recordingBySha.TryGetValue(slot.Sha256 ?? "", out var id) ? id : throw new InvalidOperationException("A previewed source was not registered."));
        var updates = preview.UpdateAnimals.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var fills = preview.Slots.Where(s => s.Action == StudyIntakeSlotAction.Fill).ToDictionary(s => s.TrialId, StringComparer.OrdinalIgnoreCase);
        var plan = current with
        {
            Title = preview.NewTitle ?? current.Title,
            RosterConfirmed = current.RosterConfirmed && !preview.ClearRosterConfirmation,
            Animals = current.Animals.Select(a => updates.TryGetValue(a.Id, out var u) ? u : a).Concat(preview.AddAnimals).ToImmutableArray(),
            Contexts = current.Contexts.AddRange(preview.AddContexts),
            Trials = current.Trials.Select(t => fills.TryGetValue(t.Id, out var s) ? t with { RecordingId = Resolve(s), MissingReason = "" } : t)
                .Concat(preview.Slots.Where(s => s.Action is StudyIntakeSlotAction.AddAndFill or StudyIntakeSlotAction.AddMissing).Select(s => new PlannedTrial
                {
                    Id = s.TrialId, AnimalId = s.AnimalId, ContextId = s.ContextId, Order = s.Trial,
                    RecordingId = s.Action == StudyIntakeSlotAction.AddAndFill ? Resolve(s) : null,
                    MissingReason = s.Action == StudyIntakeSlotAction.AddMissing ? NewSlotMissingReason : ""
                })).ToImmutableArray()
        };
        plan.Validate();
        return plan;
    }

    private static void VerifyBoundedUpdate(StudyPlan before, StudyPlan after, StudyIntakePreview preview)
    {
        var touchedAnimals = preview.UpdateAnimals.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var touchedTrials = preview.Slots.Where(s => s.Action == StudyIntakeSlotAction.Fill).Select(s => s.TrialId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < before.Animals.Length; i++)
            if (!touchedAnimals.Contains(before.Animals[i].Id) && before.Animals[i] != after.Animals[i]) throw new InvalidOperationException("Bounded update would alter an unrelated animal.");
        for (var i = 0; i < before.Contexts.Length; i++)
            if (!ReferenceEquals(before.Contexts[i], after.Contexts[i])) throw new InvalidOperationException("Bounded update would alter an existing context.");
        for (var i = 0; i < before.Trials.Length; i++)
        {
            if (touchedTrials.Contains(before.Trials[i].Id)) { if (before.Trials[i].RecordingId is not null) throw new InvalidOperationException("Bounded update would overwrite a mapped trial."); continue; }
            if (!ReferenceEquals(before.Trials[i], after.Trials[i])) throw new InvalidOperationException("Bounded update would alter an unrelated trial.");
        }
        if (after.Animals.Any(a => a.IdentityConfirmed && !before.Animals.Any(b => b.Id == a.Id && b.IdentityConfirmed)) ||
            after.Animals.Any(a => a.AllocationConfirmed && !before.Animals.Any(b => b.Id == a.Id && b.AllocationConfirmed)) ||
            after.DesignConfirmed != before.DesignConfirmed || (after.RosterConfirmed && !before.RosterConfirmed))
            throw new InvalidOperationException("Intake cannot create confirmations.");
    }

    private static VideoAsset Register(StudyStore store, StudyIntakeFile file, StudyIntakeSlot slot, string group, TrialMode phase, CancellationToken cancellation)
    {
        var info = new FileInfo(file.Path);
        if (!info.Exists || (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) != (file.Length, file.WrittenUtc, file.CreatedUtc))
            throw new StudyIntakeStaleException($"Video file on line {file.Line} changed or moved since preview.");
        if (!string.Equals(HashFile(file.Path, cancellation), file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new StudyIntakeStaleException($"Video file on line {file.Line} has different bytes than at preview.");
        cancellation.ThrowIfCancellationRequested();
        var metadata = OpenCvTracker.Probe(file.Path);
        // Identity comes from the reviewed manifest, never the file name, and stays unconfirmed.
        var requested = new VideoAsset
        {
            Path = file.Path, SourceSha256 = file.Sha256, Metadata = metadata, AnimalId = slot.AnimalId, AnimalIdConfirmed = false,
            GroupName = group, Phase = phase, Day = slot.Day, Trial = slot.Trial
        };
        var stored = store.AddOrGet(requested);
        if (!string.Equals(stored.SourceSha256, file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Registered source identity differs from the preview.");
        // Another writer registered the same bytes after preview: that recording (and its identity) was never reviewed here.
        if (!string.Equals(stored.Id, requested.Id, StringComparison.Ordinal))
            throw new StudyIntakeStaleException($"The video on line {file.Line} was registered by another import after preview; its existing recording was left unchanged.");
        if (RecordingIdentity.Of(stored) != RecordingIdentity.Of(requested))
            throw new StudyIntakeStaleException($"The recording registered for line {file.Line} does not carry the reviewed manifest identity.");
        return stored;
    }

    /// <summary>Plan-transaction guard: recordings other than this attempt's registrations are exactly as previewed, this attempt's
    /// registrations still carry the reviewed unconfirmed identity, every new mapping resolves to the previewed bytes, and no
    /// mapped recording is confirmed as a different animal. Throws <see cref="StudyIntakeStaleException"/> to refuse the revision.</summary>
    private static void VerifyRecordings(StudyIntakePreview preview, IReadOnlyList<VideoAsset> videos, IReadOnlyDictionary<string, RecordingIdentity> registered,
        IReadOnlyDictionary<string, string> resolved)
    {
        if (VideosFingerprint(videos.Where(v => !registered.ContainsKey(v.Id))) != preview.VideosFingerprint)
            throw new StudyIntakeStaleException("Imported recordings or their identity metadata changed during apply.");
        var byId = videos.ToDictionary(v => v.Id, StringComparer.Ordinal);
        foreach (var (id, identity) in registered)
            if (!byId.TryGetValue(id, out var asset) || RecordingIdentity.Of(asset) != identity)
                throw new StudyIntakeStaleException("A source registered by this intake was edited before the plan was saved.");
        foreach (var slot in preview.Slots.Where(s => s.Action is StudyIntakeSlotAction.Fill or StudyIntakeSlotAction.AddAndFill))
        {
            var id = slot.RecordingId ?? resolved.GetValueOrDefault(slot.Sha256 ?? "");
            if (id is null || !byId.TryGetValue(id, out var asset) || !string.Equals(asset.SourceSha256, slot.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new StudyIntakeStaleException($"Line {slot.Line}: the mapped recording no longer resolves to the previewed bytes.");
            if (asset.AnimalIdConfirmed && !string.Equals(asset.AnimalId.Trim(), slot.AnimalId, StringComparison.OrdinalIgnoreCase))
                throw new StudyIntakeStaleException($"Line {slot.Line}: the recording is now confirmed as a different animal.");
        }
        foreach (var file in preview.Files)
        {
            var info = new FileInfo(file.Path);
            if (!info.Exists || (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) != (file.Length, file.WrittenUtc, file.CreatedUtc))
                throw new StudyIntakeStaleException($"Video file on line {file.Line} changed or moved during apply.");
        }
    }

    private static string? StaleReason(StudyStore store, StudyIntakePreview preview, CancellationToken cancellation)
    {
        var manifest = new FileInfo(preview.Manifest.Path);
        if (!manifest.Exists || manifest.Length > StudyIntakeManifest.MaximumBytes || Hash(File.ReadAllBytes(manifest.FullName)) != preview.Manifest.Sha256) return "Manifest changed since preview.";
        var latest = new StudyPlanStore(store).Latest();
        if ((latest?.Revision ?? 0) != preview.PlanRevision || (latest?.Fingerprint ?? "") != preview.PlanFingerprint) return "Study plan changed since preview.";
        if (VideosFingerprint(store.Videos()) != preview.VideosFingerprint) return "Imported recordings or their identity metadata changed since preview.";
        foreach (var file in preview.Files)
        {
            cancellation.ThrowIfCancellationRequested(); var info = new FileInfo(file.Path);
            if (!info.Exists || (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) != (file.Length, file.WrittenUtc, file.CreatedUtc)) return $"Video file on line {file.Line} changed or moved since preview.";
        }
        // Already-imported sources are mapped without registration, so their content is re-hashed here (new sources are re-hashed at registration).
        foreach (var file in preview.Files.Where(f => f.ExistingRecordingId is not null))
            if (!string.Equals(HashFile(file.Path, cancellation), file.Sha256, StringComparison.OrdinalIgnoreCase)) return $"Video file on line {file.Line} has different bytes than at preview.";
        return null;
    }

    /// <summary>Immutable intake receipts (success and failure) read back from the append-only audit table.</summary>
    public static ImmutableArray<(long AuditId, string Action, StudyIntakeReceipt Receipt)> Receipts(StudyStore store)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,action,json FROM audit WHERE action LIKE 'study\\_intake\\_%' ESCAPE '\\' AND action<>'study_intake_unblinded' ORDER BY id";
        using var reader = command.ExecuteReader(); var rows = ImmutableArray.CreateBuilder<(long, string, StudyIntakeReceipt)>();
        while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1), JsonDefaults.Deserialize<StudyIntakeReceipt>(reader.GetString(2))));
        return rows.ToImmutable();
    }

    public static string VideosFingerprint(IEnumerable<VideoAsset> videos) => Hash(JsonDefaults.Serialize(videos.OrderBy(v => v.Id, StringComparer.Ordinal).Select(RecordingIdentity.Of)));

    /// <summary>Recording fields that intake reconciles against; any change after preview makes the preview stale.</summary>
    private sealed record RecordingIdentity(string Id, string SourceSha256, string Path, string AnimalId, bool AnimalIdConfirmed, string GroupName, TrialMode Phase, int Day, int Trial, bool Included)
    {
        public static RecordingIdentity Of(VideoAsset v) => new(v.Id, v.SourceSha256, v.Path, v.AnimalId, v.AnimalIdConfirmed, v.GroupName, v.Phase, v.Day, v.Trial, v.Included);
    }

    internal static string HashFile(string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20]; int count;
        while ((count = stream.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string NewTrialId(string animalId, int day, int order, HashSet<string> taken)
    {
        // Same deterministic slot scheme as the Luciana editor preset, with a collision suffix.
        var basis = "luciana-slot:" + animalId.Length.ToString(CultureInfo.InvariantCulture) + ":" + animalId + ":day" + day + ":trial" + order; var id = basis; var suffix = 1;
        while (!taken.Add(id)) id = basis + ":expected" + suffix++;
        return id;
    }
    private static ImmutableArray<int> Lines(IEnumerable<StudyIntakeRow> rows) => rows.Select(r => r.Line).Order().ToImmutableArray();
    private static string SafeFullPath(string path) { try { return Path.GetFullPath(path); } catch (Exception) { return path; } }
    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
