using System.Collections.Immutable;
using System.IO;
using System.Text;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Atlas.Tests;

public class StudyIntakeTests(ITestOutputHelper output)
{
    private static readonly StudyIntakeProtocol Protocol = StudyIntakeWindow.LucianaProtocol;

    [Fact] public void NativeDecoderCharacterizationAsciiCommaPathsOpenNonAsciiPathsDoNot()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-intake-decoder-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ascii = Path.Combine(folder, "set A, day 1", "clip 'one'.avi"); IntakeScenario.WriteVideo(ascii, 1);
            var accented = Path.Combine(folder, "ação, dia 1", "clip.avi"); IntakeScenario.WriteVideo(accented, 2);
            Assert.Equal(10, MouseTrackerTabajara.Tracking.OpenCvTracker.Probe(ascii).FrameCount);
            Assert.ThrowsAny<Exception>(() => MouseTrackerTabajara.Tracking.OpenCvTracker.Probe(accented));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact] public void ProtocolUsesLucianaStageIdsAndTwentyOneSlots()
    {
        Assert.Equal(21, Protocol.ExpectedSlotsPerAnimal);
        Assert.Equal(LucianaProtocolPreset.StageIds, Protocol.Stages.Select(s => s.ContextId));
        Assert.Equal([4, 4, 4, 4, 4, 1], Protocol.Stages.Select(s => s.Trials));
        Assert.Equal(TrialMode.Probe, Protocol.Stage(6)!.Phase); Assert.All(Protocol.Stages.Take(5), s => Assert.Equal(TrialMode.Training, s.Phase));
        Assert.Equal(LucianaProtocolPreset.Groups, Protocol.Groups);
    }

    [Fact] public void QuotedUtf8PathsParseAndMalformedRowsAreRejectedWithLineNumbers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "intake-parse"); var manifest = Path.Combine(directory, "manifest.csv");
        var text = "\uFEFFAnimal_ID, group ,day,trial,video_path,alias\r\n" +
            "M1,Treated Wildtype,1,1,\"Dia 1, visível/vídeo 'A' (1).avi\",\"Ali, \"\"as\"\"\"\r\n" +
            "M1,treated wildtype,6,1,C:\\Estudo\\ação\\probe final.avi,\r\n" +
            "\r\n" +
            "M2,vehicle mutant,6,2,x.avi,\n" +
            "M3,wild,1,1,y.avi,\n" +
            "M4,treated mutant,7,1,z.avi,\n" +
            "M5,treated mutant,1.0,1,z2.avi,\n" +
            ",treated mutant,1,1,z3.avi,\n" +
            "M6,treated mutant,1,1,\"unterminated.avi,\n" +
            "M6,treated mutant,1,1,\"a\"b.avi,\n" +
            "M6,treated mutant,1,1,a\"b.avi,\n" +
            "M6,treated mutant,1,1\n" +
            "M7,vehicle wildtype,1,5,w.avi,\r";
        var parsed = StudyIntakeManifest.Parse(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text[1..])).ToArray(), manifest, Protocol);
        Assert.Equal(2, parsed.Rows.Length);
        var first = parsed.Rows[0];
        Assert.Equal((2, "M1", "treated wildtype", 1, 1), (first.Line, first.AnimalId, first.Group, first.Day, first.Trial));
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "Dia 1, visível/vídeo 'A' (1).avi")), first.VideoPath);
        Assert.Equal("Ali, \"as\"", first.Alias);
        Assert.Equal(@"C:\Estudo\ação\probe final.avi", parsed.Rows[1].VideoPath);
        var byLine = parsed.Issues.ToLookup(i => i.Lines.Single());
        Assert.Equal("invalid-trial", byLine[5].Single().Code); Assert.Equal("invalid-group", byLine[6].Single().Code);
        Assert.Equal("invalid-day", byLine[7].Single().Code); Assert.Equal("invalid-day", byLine[8].Single().Code);
        Assert.Equal("missing-animal", byLine[9].Single().Code);
        foreach (var line in new[] { 10, 11, 12, 13 }) Assert.Equal("malformed-row", byLine[line].Single().Code);
        Assert.Equal("invalid-trial", byLine[14].Single().Code);
        Assert.Contains("day 6 (Probe) has trial 1 only", byLine[5].Single().Message);
        Assert.All(parsed.Issues, i => Assert.True(i.Blocking));
        Assert.Equal(10, parsed.Issues.Length);
    }

    [Fact] public void HeaderEncodingAndCrossRowAmbiguitiesBlock()
    {
        var manifest = Path.Combine(Path.GetTempPath(), "intake-header", "m.csv");
        StudyIntakeManifest Parse(string text) => StudyIntakeManifest.Parse(Encoding.UTF8.GetBytes(text), manifest, Protocol);
        Assert.Equal(["unknown-column"], Parse("animal_id,group,day,trial,video_path,excluded\nA,treated mutant,1,1,a.avi,yes").Issues.Select(i => i.Code));
        Assert.Equal(["duplicate-column"], Parse("animal_id,group,day,trial,video_path,DAY\nA,treated mutant,1,1,a.avi,1").Issues.Select(i => i.Code));
        Assert.Equal(["missing-column"], Parse("animal_id,group,day,video_path\nA,treated mutant,1,a.avi").Issues.Select(i => i.Code));
        Assert.Equal(["empty-manifest"], Parse("animal_id,group,day,trial,video_path\n\n").Issues.Select(i => i.Code));
        Assert.Equal(["empty-manifest"], Parse("").Issues.Select(i => i.Code));
        Assert.Equal("manifest-encoding", StudyIntakeManifest.Parse(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("animal_id")).ToArray(), manifest, Protocol).Issues.Single().Code);
        Assert.Equal("manifest-encoding", StudyIntakeManifest.Parse([0x61, 0xC3, 0x28, 0x0A], manifest, Protocol).Issues.Single().Code);
        var cross = Parse("animal_id,group,day,trial,video_path\n" +
            "A,treated mutant,1,1,a.avi\n" +
            "a,treated mutant,1,1,b.avi\n" +
            "B,vehicle mutant,1,1,C.AVI\n" +
            "B,treated mutant,1,2,c.avi\n");
        var codes = cross.Issues.Select(i => i.Code).ToHashSet();
        Assert.Superset(new HashSet<string> { "duplicate-slot", "duplicate-file", "animal-inconsistent", "animal-ambiguous" }, codes);
        Assert.Equal(new[] { 2, 3 }, cross.Issues.Single(i => i.Code == "duplicate-slot").Lines.ToArray());
        Assert.Equal(new[] { 4, 5 }, cross.Issues.Single(i => i.Code == "duplicate-file").Lines.ToArray());
        Assert.Equal(new[] { 4, 5 }, cross.Issues.Single(i => i.Code == "animal-inconsistent").Lines.ToArray());
    }

    [Fact] public async Task BulkIntakeReconcilesCompleteAndPartialAnimalsAtomicallyAndIdempotently()
    {
        using var s = new IntakeScenario();
        var tables = new[] { "runs", "corrections", "reviews", "inclusion_decisions", "geometry_revisions", "biological_events", "protocol_windows" };
        var beforeTables = tables.ToDictionary(t => t, s.Dump);
        var beforeVideos = s.Store.Videos().ToDictionary(v => v.Id, v => JsonDefaults.Serialize(v));
        var sourceHashes = s.AllVideoFiles().ToDictionary(p => p, IntakeScenario.Sha, StringComparer.OrdinalIgnoreCase);

        var preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol);
        Assert.False(preview.Blocked, Describe(preview));
        Assert.Equal((33, 4, 33, 84, 33, 51), (preview.Manifest.Rows.Length, preview.Animals.Length, preview.Files.Length, preview.ExpectedSlots, preview.MappedSlots, preview.MissingSlots.Length));
        Assert.Equal((3, 30), (preview.ExistingRecordings, preview.PendingImports));
        Assert.Equal((1, 1, 31, 51, 0, 0), (preview.Count(StudyIntakeSlotAction.Unchanged), preview.Count(StudyIntakeSlotAction.Fill), preview.Count(StudyIntakeSlotAction.AddAndFill),
            preview.Count(StudyIntakeSlotAction.AddMissing), preview.Count(StudyIntakeSlotAction.KeepMapped), preview.Count(StudyIntakeSlotAction.KeepMissing)));
        Assert.Equal(Protocol.Groups.Order(), preview.Animals.Select(a => a.Group).Order());
        Assert.Equal([21, 7, 3, 2], preview.Animals.Select(a => a.MappedAfterApply));
        var partial = preview.Animals.Single(a => a.AnimalId == "M-PARTIAL"); Assert.False(partial.New); Assert.Equal(new[] { "group vehicle wildtype", "litter L1" }, partial.Updates.ToArray());
        Assert.Equal(3, preview.Warnings.Count(w => w.Code == "recording-metadata-differs"));
        Assert.Contains(preview.Changes, c => c.Contains("Clear roster confirmation", StringComparison.Ordinal));
        Assert.Empty(preview.AddContexts); Assert.Null(preview.NewTitle);

        var refused = await Apply(s, preview, reviewed: false); Assert.Equal(StudyIntakeOutcome.Refused, refused.Outcome);
        Assert.Equal(StudyIntakeOutcome.Refused, (await StudyIntakeReconciler.ApplyAsync(s.Store, preview, " ", "Reason", true)).Outcome);

        // Plan changed after preview.
        var concurrent = new StudyPlanStore(s.Store).Save(s.InitialPlan.Plan with { Title = "Edited concurrently" }, "Other reviewer", "Concurrent title edit", 1);
        var videoCount = s.Store.Videos().Count;
        var stale = await Apply(s, preview); Assert.Equal(StudyIntakeOutcome.Stale, stale.Outcome); Assert.Contains("plan changed", stale.Message);
        Assert.Equal(videoCount, s.Store.Videos().Count); Assert.Equal(2, new StudyPlanStore(s.Store).Latest()!.Revision);
        // Manifest changed after preview (blank line only; still refused).
        preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol); Assert.Equal(2, preview.PlanRevision);
        File.AppendAllText(s.Manifest, "\r\n");
        stale = await Apply(s, preview); Assert.Equal(StudyIntakeOutcome.Stale, stale.Outcome); Assert.Contains("Manifest changed", stale.Message);
        // Source changed after preview.
        preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol); Assert.False(preview.Blocked);
        var rewritten = s.Paths[("M-TM", 4, 2)]; IntakeScenario.WriteVideo(rewritten, 900); sourceHashes[rewritten] = IntakeScenario.Sha(rewritten);
        stale = await Apply(s, preview); Assert.Equal(StudyIntakeOutcome.Stale, stale.Outcome); Assert.Contains("changed or moved", stale.Message);
        Assert.Equal(videoCount, s.Store.Videos().Count);
        var auditAfterStale = s.Dump("audit");

        // Mid-apply failure during source registration.
        preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol);
        var failed = await Apply(s, preview, checkpoint: (stage, i, _) => stage == StudyIntakeCheckpoint.BeforeImport && i == 4 ? Task.FromException(new IOException("Synthetic disk failure")) : Task.CompletedTask);
        Assert.Equal(StudyIntakeOutcome.Failed, failed.Outcome); Assert.Equal(4, failed.RegisteredRecordingIds.Length); Assert.Null(failed.PlanRevisionAfter);
        Assert.Equal(2, new StudyPlanStore(s.Store).Latest()!.Revision); Assert.Equal(videoCount + 4, s.Store.Videos().Count);
        // Cancellation mid-registration.
        preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol); Assert.Equal(26, preview.PendingImports);
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = await Apply(s, preview, cancellation: cancel.Token, checkpoint: (stage, i, _) => { if (stage == StudyIntakeCheckpoint.AfterImport && i == 2) cancel.Cancel(); return Task.CompletedTask; });
            Assert.Equal(StudyIntakeOutcome.Cancelled, cancelled.Outcome); Assert.Equal(3, cancelled.RegisteredRecordingIds.Length);
        }
        Assert.Equal(2, new StudyPlanStore(s.Store).Latest()!.Revision);
        // Failure after all sources are registered, immediately before the single plan transaction.
        preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol); Assert.Equal(23, preview.PendingImports);
        failed = await Apply(s, preview, checkpoint: (stage, _, _) => stage == StudyIntakeCheckpoint.BeforePlanSave ? Task.FromException(new InvalidDataException("Synthetic failure before plan save")) : Task.CompletedTask);
        Assert.Equal(StudyIntakeOutcome.Failed, failed.Outcome); Assert.Equal(23, failed.RegisteredRecordingIds.Length);
        Assert.Equal(2, new StudyPlanStore(s.Store).Latest()!.Revision); Assert.Equal(videoCount + 30, s.Store.Videos().Count);

        // Recovery: every registered source is now matched by content; identity comes from the manifest and stays unconfirmed.
        preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol);
        Assert.False(preview.Blocked, Describe(preview)); Assert.Equal((0, 33), (preview.PendingImports, preview.ExistingRecordings));
        Assert.Equal(3, preview.Warnings.Count(w => w.Code == "recording-metadata-differs"));
        var videos = s.Store.Videos(); var bySha = videos.ToDictionary(v => v.SourceSha256);
        foreach (var row in preview.Manifest.Rows)
        {
            var asset = bySha[IntakeScenario.Sha(row.VideoPath)];
            if (beforeVideos.ContainsKey(asset.Id)) continue;
            Assert.Equal((row.AnimalId, false, row.Day, row.Trial), (asset.AnimalId, asset.AnimalIdConfirmed, asset.Day, asset.Trial));
            Assert.Equal(row.Day == 6 ? TrialMode.Probe : TrialMode.Training, asset.Phase);
            Assert.Equal(preview.Animals.Single(a => a.AnimalId == row.AnimalId).Group, asset.GroupName);
            Assert.Equal("", asset.BlindedAnimalAlias); Assert.StartsWith("Recording ", asset.BlindedAlias);
        }
        Assert.Equal("M-COMPLETE", bySha[IntakeScenario.Sha(s.Paths[("M-COMPLETE", 3, 2)])].AnimalId); // file is named after another animal

        var applied = await Apply(s, preview);
        Assert.Equal(StudyIntakeOutcome.Applied, applied.Outcome); Assert.Equal(3, applied.PlanRevisionAfter); Assert.Empty(applied.RegisteredRecordingIds);
        var history = new StudyPlanStore(s.Store).History(); Assert.Equal(3, history.Length);
        Assert.Contains("bulk intake " + preview.PreviewId[..12], history[2].Reason); Assert.Contains(preview.Manifest.Sha256, history[2].Reason);
        var before = history[1].Plan; var plan = history[2].Plan; plan.Validate();
        Assert.False(plan.RosterConfirmed); Assert.Equal(before.DesignConfirmed, plan.DesignConfirmed); Assert.Equal(before.Title, plan.Title);
        Assert.Equal(JsonDefaults.Serialize(before.Contexts), JsonDefaults.Serialize(plan.Contexts));
        Assert.Equal(before.Animals.Single(a => a.Id == "OTHER-1"), plan.Animals.Single(a => a.Id == "OTHER-1"));
        Assert.Equal(before.Trials.Where(t => t.AnimalId == "OTHER-1"), plan.Trials.Where(t => t.AnimalId == "OTHER-1"));
        Assert.Equal(5, plan.Animals.Length);
        foreach (var animal in plan.Animals.Where(a => a.Id != "OTHER-1")) Assert.False(animal.IdentityConfirmed || animal.AllocationConfirmed);
        var partialAnimal = plan.Animals.Single(a => a.Id == "M-PARTIAL"); Assert.Equal(("vehicle wildtype", "L1", "C-9"), (partialAnimal.Group, partialAnimal.Litter, partialAnimal.Cage));
        Assert.Equal("Comp, A", plan.Animals.Single(a => a.Id == "M-COMPLETE").Alias);
        foreach (var (animal, mapped) in new[] { ("M-COMPLETE", 21), ("M-PARTIAL", 7), ("M-TM", 3), ("M-VM", 2) })
        {
            var slots = plan.Trials.Where(t => t.AnimalId == animal).ToArray(); Assert.Equal(21, slots.Length);
            Assert.Equal(mapped, slots.Count(t => t.RecordingId is not null));
            Assert.All(slots.Where(t => t.RecordingId is null), t => Assert.False(string.IsNullOrWhiteSpace(t.MissingReason)));
            foreach (var stage in Protocol.Stages) Assert.Equal(Enumerable.Range(1, stage.Trials), slots.Where(t => t.ContextId == stage.ContextId).Select(t => t.Order).Order());
        }
        var existingPartial = before.Trials.Where(t => t.AnimalId == "M-PARTIAL").ToArray();
        Assert.Equal(existingPartial[0], plan.Trials.Single(t => t.Id == existingPartial[0].Id));
        var filled = plan.Trials.Single(t => t.Id == existingPartial[1].Id); Assert.NotNull(filled.RecordingId); Assert.Equal("", filled.MissingReason);
        foreach (var row in preview.Manifest.Rows)
        {
            var id = bySha[IntakeScenario.Sha(row.VideoPath)].Id; var trial = plan.Trials.Single(t => t.RecordingId == id);
            Assert.Equal((row.AnimalId, Protocol.Stage(row.Day)!.ContextId, row.Trial), (trial.AnimalId, trial.ContextId, trial.Order));
        }
        var inventory = StudyPlanInventory.Build(plan, s.Store.Videos()).Where(i => i.Trial.AnimalId != "OTHER-1").ToArray();
        Assert.Equal(84, inventory.Length);
        Assert.All(inventory, i => Assert.Contains("Animal identity is unconfirmed.", i.Holds));
        Assert.All(inventory, i => Assert.Contains("Expected trial roster is unconfirmed.", i.Holds));
        Assert.Equal(51, inventory.Count(i => i.Holds.Any(h => h.StartsWith("Planned recording missing", StringComparison.Ordinal))));
        Assert.Equal(33, inventory.Count(i => i.Holds.Contains("Recording animal identity does not match its planned slot.")));
        Assert.Equal(3, inventory.Count(i => i.Holds.Contains("Recording phase/day/trial differs from its planned context.")));

        // Raw runs, edits, reviews, recording metadata and source bytes preserved.
        foreach (var table in tables) Assert.Equal(beforeTables[table], s.Dump(table));
        var after = s.Store.Videos().ToDictionary(v => v.Id);
        foreach (var (id, json) in beforeVideos) Assert.Equal(json, JsonDefaults.Serialize(after[id]));
        foreach (var (path, hash) in sourceHashes) Assert.Equal(hash, IntakeScenario.Sha(path));
        Assert.StartsWith(auditAfterStale, s.Dump("audit"));

        // Repeat import of the same manifest is idempotent and writes no revision.
        var repeat = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol);
        Assert.False(repeat.HasChanges); Assert.False(repeat.Blocked); Assert.Equal((33, 51, 0), (repeat.Count(StudyIntakeSlotAction.Unchanged), repeat.Count(StudyIntakeSlotAction.KeepMissing), repeat.PendingImports));
        var none = await Apply(s, repeat); Assert.Equal(StudyIntakeOutcome.NoChanges, none.Outcome);
        Assert.Equal(3, new StudyPlanStore(s.Store).Latest()!.Revision); Assert.Equal(videoCount + 30, s.Store.Videos().Count);

        var receipts = StudyIntakeReconciler.Receipts(s.Store);
        Assert.Equal([StudyIntakeOutcome.Refused, StudyIntakeOutcome.Refused, StudyIntakeOutcome.Stale, StudyIntakeOutcome.Stale, StudyIntakeOutcome.Stale, StudyIntakeOutcome.Failed,
            StudyIntakeOutcome.Cancelled, StudyIntakeOutcome.Failed, StudyIntakeOutcome.Applied, StudyIntakeOutcome.NoChanges], receipts.Select(r => r.Receipt.Outcome));
        Assert.Equal(receipts.Select(r => r.AuditId).Order(), receipts.Select(r => r.AuditId));

        // Conflicting manifest: every rule blocks, nothing is applied.
        var changed = Path.Combine(s.Folder, "changed source.avi"); IntakeScenario.WriteVideo(changed, 950); s.Workspace.Import(changed); IntakeScenario.WriteVideo(changed, 951);
        var n3 = Path.Combine(s.Folder, "conflict", "n3.avi"); IntakeScenario.WriteVideo(n3, 962); var n3Copy = Path.Combine(s.Folder, "conflict", "n3 copy.avi"); File.Copy(n3, n3Copy);
        var n1 = Path.Combine(s.Folder, "conflict", "n1.avi"); IntakeScenario.WriteVideo(n1, 960); var n2 = Path.Combine(s.Folder, "conflict", "n2.avi"); IntakeScenario.WriteVideo(n2, 961);
        var conflicts = s.WriteManifest("conflicts.csv", [
            ("M-COMPLETE", "treated wildtype", 1, 1, n1), ("M-TM", "treated mutant", 3, 2, s.Paths[("M-COMPLETE", 1, 1)]), ("M-PARTIAL", "treated mutant", 3, 1, n2),
            ("M-TM", "treated mutant", 3, 3, n3), ("M-TM", "treated mutant", 3, 4, n3Copy), ("M-VM", "vehicle mutant", 1, 1, Path.Combine(s.Folder, "absent.avi")),
            ("M-VM", "vehicle mutant", 2, 1, s.OtherPath), ("M-VM", "vehicle mutant", 2, 2, changed)]);
        var blocked = await StudyIntakeReconciler.PreviewAsync(s.Store, conflicts, Protocol);
        Assert.True(blocked.Blocked);
        Assert.Superset(new HashSet<string> { "slot-occupied", "recording-already-mapped", "allocation-conflict", "duplicate-content", "missing-file", "identity-conflict", "source-changed" },
            blocked.Conflicts.Select(c => c.Code).ToHashSet());
        Assert.Equal(new[] { 2 }, blocked.Conflicts.Single(c => c.Code == "slot-occupied").Lines.ToArray());
        Assert.Equal(new[] { 5, 6 }, blocked.Conflicts.Single(c => c.Code == "duplicate-content").Lines.ToArray());
        Assert.Contains(blocked.Conflicts, c => c.Code == "recording-already-mapped" && c.Lines.SequenceEqual([3]));
        Assert.Contains(blocked.Conflicts, c => c.Code == "recording-already-mapped" && c.Lines.SequenceEqual([8]));
        var countBefore = s.Store.Videos().Count;
        Assert.Equal(StudyIntakeOutcome.Refused, (await Apply(s, blocked)).Outcome);
        Assert.Equal(3, new StudyPlanStore(s.Store).Latest()!.Revision); Assert.Equal(countBefore, s.Store.Videos().Count);
    }

    [Fact] public async Task RegistrationMetadataAndSourceRacesAreRefusedBeforeThePlanAndRetryApplies()
    {
        using var s = new IntakeScenario();
        var tables = new[] { "runs", "corrections", "reviews", "inclusion_decisions", "geometry_revisions", "biological_events", "protocol_windows" };
        var beforeTables = tables.ToDictionary(t => t, s.Dump);
        var sourceHashes = s.AllVideoFiles().ToDictionary(p => p, IntakeScenario.Sha, StringComparer.OrdinalIgnoreCase);
        var initialVideos = s.Store.Videos().ToDictionary(v => v.Id, v => JsonDefaults.Serialize(v));
        var preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol);
        var pending = preview.Files.Where(f => f.ExistingRecordingId is null).OrderBy(f => f.Line).ToArray(); Assert.Equal(30, pending.Length);

        // 1. A pending source's bytes change after preview with size and timestamps preserved: refused at registration by content.
        var stale = await Apply(s, preview, checkpoint: (stage, i, _) => { if (stage == StudyIntakeCheckpoint.BeforeImport && i == 1) MutatePreservingSizeAndTimes(pending[3].Path); return Task.CompletedTask; });
        Assert.Equal(StudyIntakeOutcome.Stale, stale.Outcome); Assert.Contains($"line {pending[3].Line} has different bytes", stale.Message); Assert.Equal(3, stale.RegisteredRecordingIds.Length);
        RestoreOriginal(pending[3].Path); Assert.Equal(sourceHashes[pending[3].Path], IntakeScenario.Sha(pending[3].Path));
        var orphans = s.Store.Videos().Where(v => stale.RegisteredRecordingIds.Contains(v.Id)).ToDictionary(v => v.Id, v => JsonDefaults.Serialize(v));

        // 2. Another writer registers the same bytes with a different confirmed animal while apply is at BeforeImport.
        preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol); Assert.False(preview.Blocked, Describe(preview));
        pending = preview.Files.Where(f => f.ExistingRecordingId is null).OrderBy(f => f.Line).ToArray(); Assert.Equal(27, pending.Length);
        VideoAsset? intruder = null; var target = pending[2];
        stale = await Apply(s, preview, checkpoint: (stage, i, _) =>
        {
            if (stage == StudyIntakeCheckpoint.BeforeImport && i == 2)
            { intruder = s.Workspace.Import(target.Path) with { AnimalId = "INTRUDER-9", AnimalIdConfirmed = true, GroupName = "vehicle mutant" }; s.Store.SaveVideo(intruder); }
            return Task.CompletedTask;
        });
        Assert.Equal(StudyIntakeOutcome.Stale, stale.Outcome); Assert.Contains($"line {target.Line} was registered by another import", stale.Message);
        Assert.Equal(2, stale.RegisteredRecordingIds.Length); Assert.DoesNotContain(intruder!.Id, stale.RegisteredRecordingIds);
        Assert.Equal(1, new StudyPlanStore(s.Store).Latest()!.Revision);
        var intruderStored = s.Store.Videos().Single(v => v.Id == intruder.Id); Assert.Equal(("INTRUDER-9", true), (intruderStored.AnimalId, intruderStored.AnimalIdConfirmed));
        foreach (var video in s.Store.Videos().Where(v => stale.RegisteredRecordingIds.Contains(v.Id))) orphans[video.Id] = JsonDefaults.Serialize(video);
        // The retry sees the confirmed identity conflict and blocks; the human corrects the recording locally, then the retry is unblocked.
        var blocked = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol);
        Assert.Contains(blocked.Conflicts, c => c.Code == "identity-conflict" && c.Lines.SequenceEqual([target.Line]));
        var manifestRow = blocked.Manifest.Rows.Single(r => r.Line == target.Line);
        s.Store.SaveVideo(intruderStored with { AnimalId = manifestRow.AnimalId, GroupName = blocked.Animals.Single(a => a.AnimalId == manifestRow.AnimalId).Group });
        var intruderCorrected = JsonDefaults.Serialize(s.Store.Videos().Single(v => v.Id == intruder.Id));

        // 3. This attempt's own fresh registration is confirmed as another animal before the plan transaction.
        preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol); Assert.False(preview.Blocked, Describe(preview)); Assert.Equal(24, preview.PendingImports);
        VideoAsset? own = null;
        stale = await Apply(s, preview, checkpoint: (stage, _, _) =>
        {
            if (stage == StudyIntakeCheckpoint.BeforePlanSave)
            { var fresh = s.Store.Videos().Last(); own = fresh with { AnimalId = "STRANGER-7", AnimalIdConfirmed = true }; s.Store.SaveVideo(own); }
            return Task.CompletedTask;
        });
        Assert.Equal(StudyIntakeOutcome.Stale, stale.Outcome); Assert.Contains("registered by this intake was edited", stale.Message);
        Assert.Equal(24, stale.RegisteredRecordingIds.Length); Assert.Contains(own!.Id, stale.RegisteredRecordingIds); Assert.Equal(1, new StudyPlanStore(s.Store).Latest()!.Revision);
        blocked = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol); Assert.Contains(blocked.Conflicts, c => c.Code == "identity-conflict");
        s.Store.SaveVideo(own with { AnimalId = blocked.Manifest.Rows.Single(r => IntakeScenario.Sha(r.VideoPath) == own.SourceSha256).AnimalId, AnimalIdConfirmed = false });
        foreach (var video in s.Store.Videos().Where(v => stale.RegisteredRecordingIds.Contains(v.Id) && v.Id != own.Id)) orphans[video.Id] = JsonDefaults.Serialize(video);

        // 4. Existing (pre-imported) recording metadata changes after the final pre-check: refused inside the plan transaction.
        preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol); Assert.False(preview.Blocked, Describe(preview)); Assert.Equal(0, preview.PendingImports);
        var pre = s.Store.Videos().Single(v => v.Id == preview.Files.Single(f => f.Line == 2).ExistingRecordingId);
        stale = await Apply(s, preview, checkpoint: (stage, _, _) => { if (stage == StudyIntakeCheckpoint.BeforePlanSave) s.Store.SaveVideo(pre with { GroupName = "edited concurrently" }); return Task.CompletedTask; });
        Assert.Equal(StudyIntakeOutcome.Stale, stale.Outcome); Assert.Contains("identity metadata changed during apply", stale.Message); Assert.Equal(1, new StudyPlanStore(s.Store).Latest()!.Revision);
        s.Store.SaveVideo(pre); Assert.Equal(initialVideos[pre.Id], JsonDefaults.Serialize(s.Store.Videos().Single(v => v.Id == pre.Id)));

        // 5. An already-imported source is rewritten with identical size and timestamps after preview: refused by content re-hash.
        preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol); Assert.False(preview.Blocked, Describe(preview));
        MutatePreservingSizeAndTimes(pre.Path);
        stale = await Apply(s, preview); Assert.Equal(StudyIntakeOutcome.Stale, stale.Outcome); Assert.Contains("line 2 has different bytes", stale.Message);
        RestoreOriginal(pre.Path); Assert.Equal(1, new StudyPlanStore(s.Store).Latest()!.Revision);

        // 6. Valid retry. Inside the plan transaction a concurrent metadata writer is blocked until the revision commits.
        preview = await StudyIntakeReconciler.PreviewAsync(s.Store, s.Manifest, Protocol); Assert.False(preview.Blocked, Describe(preview));
        Task? writer = null; var blockedWhileOpen = false;
        var applied = await Apply(s, preview, checkpoint: (stage, _, _) =>
        {
            if (stage == StudyIntakeCheckpoint.InPlanTransaction)
            {
                writer = Task.Run(() => { s.Store.SaveVideo(pre with { GroupName = "written after commit" }); s.Store.Audit("synthetic_concurrent_writer", new { pre.Id }); });
                blockedWhileOpen = !writer.Wait(700);
            }
            return Task.CompletedTask;
        });
        Assert.True(applied.Outcome == StudyIntakeOutcome.Applied, applied.Message); Assert.Equal(2, applied.PlanRevisionAfter);
        await writer!; Assert.True(blockedWhileOpen, "Concurrent recording writer waited for the plan transaction.");
        var audit = s.Dump("audit").Split('\n'); int Row(string action) => Array.FindIndex(audit, l => l.Contains("|" + action + "|", StringComparison.Ordinal));
        Assert.True(Array.FindLastIndex(audit, l => l.Contains("|study_plan_saved|", StringComparison.Ordinal)) < Row("synthetic_concurrent_writer"));
        s.Store.SaveVideo(pre);
        var plan = new StudyPlanStore(s.Store).Latest()!.Plan;
        Assert.Equal(33, plan.Trials.Count(t => t.AnimalId != "OTHER-1" && t.RecordingId is not null));
        Assert.Equal((manifestRow.AnimalId, Protocol.Stage(manifestRow.Day)!.ContextId, manifestRow.Trial), plan.Trials.Where(t => t.RecordingId == intruder.Id).Select(t => (t.AnimalId, t.ContextId, t.Order)).Single());

        // Orphan imports and the other writer's recording were never modified by intake; raw evidence and source bytes are preserved.
        var final = s.Store.Videos().ToDictionary(v => v.Id, v => JsonDefaults.Serialize(v));
        foreach (var (id, json) in orphans) Assert.Equal(json, final[id]);
        Assert.Equal(intruderCorrected, final[intruder.Id]);
        foreach (var (id, json) in initialVideos) Assert.Equal(json, final[id]);
        foreach (var table in tables) Assert.Equal(beforeTables[table], s.Dump(table));
        foreach (var (path, hash) in sourceHashes) Assert.Equal(hash, IntakeScenario.Sha(path));
        Assert.Equal([StudyIntakeOutcome.Stale, StudyIntakeOutcome.Stale, StudyIntakeOutcome.Stale, StudyIntakeOutcome.Stale, StudyIntakeOutcome.Stale, StudyIntakeOutcome.Applied],
            StudyIntakeReconciler.Receipts(s.Store).Select(r => r.Receipt.Outcome));
    }

    private static readonly Dictionary<string, (byte[] Bytes, DateTime Written, DateTime Created)> Originals = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Flips one byte in place, keeping the length and both timestamps (a mutation that size/time checks cannot see).</summary>
    private static void MutatePreservingSizeAndTimes(string path)
    {
        var info = new FileInfo(path); var bytes = File.ReadAllBytes(path); lock (Originals) Originals[path] = (bytes, info.LastWriteTimeUtc, info.CreationTimeUtc);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) { stream.Position = bytes.Length / 2; stream.WriteByte((byte)(bytes[bytes.Length / 2] ^ 0xFF)); }
        File.SetLastWriteTimeUtc(path, info.LastWriteTimeUtc); File.SetCreationTimeUtc(path, info.CreationTimeUtc);
        var after = new FileInfo(path); Assert.Equal((info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc), (after.Length, after.LastWriteTimeUtc, after.CreationTimeUtc));
    }
    private static void RestoreOriginal(string path)
    {
        (byte[] Bytes, DateTime Written, DateTime Created) original; lock (Originals) original = Originals[path];
        File.WriteAllBytes(path, original.Bytes); File.SetLastWriteTimeUtc(path, original.Written); File.SetCreationTimeUtc(path, original.Created);
    }

    [Fact] public async Task FreshStudyGainsUnconfirmedProtocolContextsAndAmbiguousContextsBlock()
    {
        using var s = new IntakeScenario(seedPlan: false);
        // A quoted UTF-8 accented path parses and hashes, but the native decoder cannot open it: blocked at preview, nothing written.
        var accented = s.WriteManifest("fresh accented.csv", [("F1", "treated mutant", 1, 1, s.Paths[("M-TM", 3, 1)]), ("F2", "vehicle wildtype", 6, 1, s.Paths[("M-VM", 6, 1)]), ("F2", "vehicle wildtype", 5, 1, s.AccentedPath)]);
        var undecodable = await StudyIntakeReconciler.PreviewAsync(s.Store, accented, Protocol);
        Assert.Equal(s.AccentedPath, undecodable.Manifest.Rows[2].VideoPath);
        Assert.Equal(new[] { 4 }, undecodable.Conflicts.Single(c => c.Code == "undecodable-video").Lines.ToArray()); Assert.Single(undecodable.Conflicts);
        Assert.Equal(StudyIntakeOutcome.Refused, (await Apply(s, undecodable)).Outcome); Assert.Empty(s.Store.Videos()); Assert.Null(new StudyPlanStore(s.Store).Latest());
        var manifest = s.WriteManifest("fresh.csv", [("F1", "treated mutant", 1, 1, s.Paths[("M-TM", 3, 1)]), ("F2", "vehicle wildtype", 6, 1, s.Paths[("M-VM", 6, 1)])]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StudyIntakeReconciler.PreviewAsync(s.Store, manifest, Protocol, null, new CancellationToken(true)));
        Assert.Empty(s.Store.Videos()); Assert.Null(new StudyPlanStore(s.Store).Latest());
        var preview = await StudyIntakeReconciler.PreviewAsync(s.Store, manifest, Protocol);
        Assert.False(preview.Blocked, Describe(preview)); Assert.Equal(6, preview.AddContexts.Length); Assert.NotNull(preview.NewTitle); Assert.Equal(42, preview.ExpectedSlots);
        var receipt = await Apply(s, preview); Assert.True(receipt.Outcome == StudyIntakeOutcome.Applied, receipt.Message); Assert.Equal(1, receipt.PlanRevisionAfter);
        var plan = new StudyPlanStore(s.Store).Latest()!.Plan;
        Assert.Equal(LucianaProtocolPreset.StageIds, plan.Contexts.Select(c => c.Id));
        Assert.All(plan.Contexts, c => Assert.False(c.WindowConfirmed || c.AlignmentConfirmed || c.RequestedExposureSeconds != 0));
        Assert.False(plan.RosterConfirmed || plan.DesignConfirmed); Assert.Equal(StudyUnitDesign.Unknown, plan.UnitDesign); Assert.Equal(42, plan.Trials.Length);
        // A differently named context for an existing stage is ambiguous and blocks.
        var ambiguous = plan with { Contexts = plan.Contexts.Select(c => c.Day == 6 ? c with { Id = "my-probe" } : c).ToImmutableArray(),
            Trials = plan.Trials.Select(t => t.ContextId == LucianaProtocolPreset.StageIds[5] ? t with { ContextId = "my-probe" } : t).ToImmutableArray() };
        new StudyPlanStore(s.Store).Save(ambiguous, "Reviewer", "Renamed probe context", 1);
        var blocked = await StudyIntakeReconciler.PreviewAsync(s.Store, manifest, Protocol);
        Assert.Contains(blocked.Conflicts, c => c.Code == "context-ambiguous");
    }

    [Fact] public async Task HundredsOfTrialFilesReconcileAndApplyInOneBoundedPass()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-intake-scale-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var store = new StudyStore(Path.Combine(folder, "scale.morrisdb")); var lines = new List<IntakeLine>(); var seed = 0;
            for (var animal = 0; animal < 16; animal++)
                foreach (var stage in Protocol.Stages)
                    for (var trial = 1; trial <= stage.Trials; trial++)
                    {
                        var path = Path.Combine(folder, $"cohort {animal / 4}, cage {animal}", $"clip {seed:000}.avi"); IntakeScenario.WriteVideo(path, ++seed);
                        lines.Add(new($"S{animal:00}", Protocol.Groups[animal % 4], stage.Day, trial, path));
                    }
            var manifest = Path.Combine(folder, "scale.csv");
            File.WriteAllLines(manifest, new[] { "animal_id,group,day,trial,video_path" }.Concat(lines.Select(l => $"{l.Animal},{l.Group},{l.Day},{l.Trial},\"{l.Path}\"")));
            var clock = System.Diagnostics.Stopwatch.StartNew(); var progress = new List<StudyIntakeProgress>();
            var preview = await StudyIntakeReconciler.PreviewAsync(store, manifest, Protocol, new InlineProgress<StudyIntakeProgress>(progress.Add));
            var previewSeconds = clock.Elapsed.TotalSeconds;
            Assert.False(preview.Blocked, Describe(preview)); Assert.Equal((336, 16, 336, 336, 0), (preview.Files.Length, preview.Animals.Length, preview.ExpectedSlots, preview.MappedSlots, preview.MissingSlots.Length));
            Assert.Equal(336, progress.Count(p => p.Stage.StartsWith("Hashing", StringComparison.Ordinal)));
            clock.Restart(); var receipt = await StudyIntakeReconciler.ApplyAsync(store, preview, "Synthetic reviewer", "Synthetic scale intake", true); var applySeconds = clock.Elapsed.TotalSeconds;
            Assert.True(receipt.Outcome == StudyIntakeOutcome.Applied, receipt.Message); Assert.Equal(336, receipt.RegisteredRecordingIds.Length);
            var plan = new StudyPlanStore(store).Latest()!.Plan; Assert.Equal((16, 336), (plan.Animals.Length, plan.Trials.Count(t => t.RecordingId is not null)));
            Assert.All(Protocol.Groups, g => Assert.Equal(4, plan.Animals.Count(a => a.Group == g)));
            clock.Restart(); var repeat = await StudyIntakeReconciler.PreviewAsync(store, manifest, Protocol); var repeatSeconds = clock.Elapsed.TotalSeconds;
            Assert.False(repeat.HasChanges);
            output.WriteLine($"scale 336 synthetic files: preview {previewSeconds:0.0}s, apply {applySeconds:0.0}s, repeat preview {repeatSeconds:0.0}s (single local run; not a performance claim)");
            Assert.True(previewSeconds + applySeconds + repeatSeconds < 240, "Bounded synthetic scale pass.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }
    internal static Task<StudyIntakeReceipt> Apply(IntakeScenario s, StudyIntakePreview preview, bool reviewed = true, CancellationToken cancellation = default,
        Func<StudyIntakeCheckpoint, int, CancellationToken, Task>? checkpoint = null) =>
        StudyIntakeReconciler.ApplyAsync(s.Store, preview, "Synthetic reviewer", "Synthetic bulk intake", reviewed, cancellation, checkpoint);
    internal static string Describe(StudyIntakePreview preview) => string.Join("\n", preview.Issues.Select(i => $"{i.Code} {i.LineLabel}: {i.Message}"));
}
