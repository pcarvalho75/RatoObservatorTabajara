using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using Xunit;

namespace MouseTrackerTabajara.Tests;

public sealed class StudyPlanTests
{
    private static StudyPlan Plan() => new()
    {
        Title = "Synthetic expected study", RosterConfirmed = true, DesignConfirmed = true, UnitDesign = StudyUnitDesign.IndependentAnimals,
        Animals = [new() { Id = "A", Alias = "Mouse 1", Group = "control", IdentityConfirmed = true, AllocationConfirmed = true },
            new() { Id = "B", Alias = "Mouse 2", Group = "treated", IdentityConfirmed = true, AllocationConfirmed = true }],
        Contexts = [new() { Id = "probe-day1", Phase = TrialMode.Probe, RequestedExposureSeconds = 60, ComparisonDurationSeconds = 30,
            WindowConfirmed = true, CommonCoordinateReference = "pool-reference-1", AlignmentConfirmed = true, Orientation = StudyOrientation.Room, TemporalBinEdges = [0, 10, 20, 30] }],
        Trials = [new() { Id = "trial-A", AnimalId = "A", ContextId = "probe-day1", RecordingId = "video-A" },
            new() { Id = "trial-B", AnimalId = "B", ContextId = "probe-day1", MissingReason = "Not yet supplied" }]
    };
    private static VideoAsset Asset() => new()
    {
        Id = "video-A", AnimalId = "A", AnimalIdConfirmed = true, GroupName = "control", Phase = TrialMode.Probe,
        SourceSha256 = new string('a', 64), Metadata = new(200, 200, 10, 2), Settings = new() { ArenaConfirmed = true }
    };

    [Fact] public void ExpectedMissingTrialAndUnassignedFileStayVisibleWithoutInventingCompleteness()
    {
        var plan = Plan(); var asset = Asset(); var extra = asset with { Id = "not-planned", AnimalId = "C" };
        var rows = StudyPlanInventory.Build(plan, [asset, extra]);
        Assert.Equal(2, rows.Length); Assert.Empty(rows[0].Holds); Assert.Null(rows[1].Recording);
        Assert.Contains(rows[1].Holds, h => h.Contains("Not yet supplied"));
        Assert.Equal("not-planned", Assert.Single(StudyPlanInventory.Unassigned(plan, [asset, extra])).Id);
    }

    [Fact] public void ConflictingAnimalGroupPhaseAndExcludedRecordingHaveSeparateHolds()
    {
        var bad = Asset() with { AnimalId = "B", AnimalIdConfirmed = false, GroupName = "treated", Phase = TrialMode.Training,
            Included = false, ExclusionReason = "Damaged source" };
        var row = StudyPlanInventory.Build(Plan(), [bad])[0];
        Assert.Equal(4, row.Holds.Length); Assert.Contains(row.Holds, h => h.Contains("identity"));
        Assert.Contains(row.Holds, h => h.Contains("group")); Assert.Contains(row.Holds, h => h.Contains("phase/day/trial"));
        Assert.Contains(row.Holds, h => h.Contains("Damaged source"));
    }

    [Fact] public void DuplicateRecordingOrBiologicalTrialSlotCannotInflateExpectedRoster()
    {
        var plan = Plan(); var first = plan.Trials[0]; var second = plan.Trials[1];
        Assert.Throws<ArgumentException>(() => (plan with { Trials = [first, second with { RecordingId = first.RecordingId }] }).Validate());
        Assert.Throws<ArgumentException>(() => (plan with { Trials = [first, second with { AnimalId = "a", ContextId = first.ContextId }] }).Validate());
        Assert.Throws<ArgumentException>(() => (plan with { Animals = plan.Animals.Add(plan.Animals[0] with { Id = "a" }) }).Validate());
    }

    [Fact] public void UnknownPlanNeverAutomaticallyConfirmsIdentityWindowOrAllocation()
    {
        var original = Plan(); var plan = original with { RosterConfirmed = false, DesignConfirmed = false, UnitDesign = StudyUnitDesign.Unknown,
            Animals = [original.Animals[0] with { IdentityConfirmed = false, AllocationConfirmed = false }, original.Animals[1]],
            Contexts = [original.Contexts[0] with { WindowConfirmed = false, AlignmentConfirmed = false }] };
        var holds = StudyPlanInventory.Build(plan, [Asset()])[0].Holds;
        Assert.Equal(5, holds.Length); Assert.False(plan.DesignConfirmed); Assert.False(plan.RosterConfirmed);
        Assert.False(plan.Animals[0].IdentityConfirmed); Assert.False(plan.Contexts[0].WindowConfirmed);
    }

    [Fact] public void PrespecifiedBinsAndConfirmedWindowRejectOverrunAndChangingExposure()
    {
        var plan = Plan(); var context = plan.Contexts[0];
        Assert.Throws<ArgumentException>(() => (plan with { Contexts = [context with { ComparisonStartSeconds = 40 }] }).Validate());
        Assert.Throws<ArgumentException>(() => (plan with { Contexts = [context with { TemporalBinEdges = [0, 20, 10, 30] }] }).Validate());
        Assert.Throws<ArgumentException>(() => (plan with { Contexts = [context with { RequestedExposureSeconds = 0 }] }).Validate());
        Assert.Throws<ArgumentException>(() => (plan with { Contexts = [context with { CommonCoordinateReference = "" }] }).Validate());
        Assert.Equal(60, context.RequestedExposureSeconds);
    }

    [Fact] public void PlansAreAppendOnlyAuditedAndRawRunBytesStayIdenticalAcrossReopen()
    {
        using var scratch = new Scratch(); var store = new StudyStore(Path.Combine(scratch.Folder, "study.db")); var asset = store.AddOrGet(Asset());
        var points = new List<TrackPoint> { new(0, 0, 80, 80, PointStatus.Tracked, 1), new(1, .1, null, null, PointStatus.Missing, 0) };
        var run = new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = asset.Settings,
            Points = points, Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings) };
        store.SaveRun(run); var raw = JsonDefaults.Serialize(store.LatestRun(asset.Id));
        var plans = new StudyPlanStore(store); Assert.Empty(plans.History());
        var first = plans.Save(Plan(), "Reviewer 1", "Prespecified synthetic plan", 0);
        var second = plans.Save(Plan() with { Title = "Documented revision" }, "Reviewer 2", "Correct study title", 1);
        var reopened = new StudyPlanStore(new StudyStore(store.DatabasePath)); var history = reopened.History();
        Assert.Equal(2, history.Length); Assert.Equal(first.RawJson, history[0].RawJson); Assert.Equal(first.Fingerprint, history[0].Fingerprint);
        Assert.Equal(second.Revision, reopened.Latest()!.Revision); Assert.Equal(2, history[0].Plan.Animals.Length);
        Assert.Equal(raw, JsonDefaults.Serialize(store.LatestRun(asset.Id))); Assert.Equal("control", store.Videos()[0].GroupName);
        using var connection = new SqliteConnection("Data Source=" + store.DatabasePath); connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit WHERE action='study_plan_saved'"; Assert.Equal(2L, command.ExecuteScalar());
        Assert.Throws<InvalidOperationException>(() => plans.Save(Plan(), "Reviewer", "Stale editor", 1));
        Assert.Equal(2, plans.History().Length);
    }

    [Fact] public void ConfirmedSpatialReferenceNamesItsExactSourceAndGeometry()
    {
        var plan = Plan(); var trial = plan.Trials[0]; var asset = Asset();
        Assert.Throws<ArgumentException>(() => (plan with { Trials = [trial with { SourceReferenceConfirmed = true }, plan.Trials[1]] }).Validate());
        var confirmed = trial with { SourceReferenceConfirmed = true, ReferenceSourceSha256 = asset.SourceSha256, ReferenceSettingsHash = SettingsIdentity.Hash(asset.Settings) };
        (plan with { Trials = [confirmed, plan.Trials[1]] }).Validate();
        Assert.Throws<ArgumentException>(() => (plan with { Trials = [confirmed with { RecordingId = null }, plan.Trials[1]] }).Validate());
        Assert.Throws<ArgumentException>(() => (plan with { Trials = [confirmed with { RoomRotationRadians = double.NaN }, plan.Trials[1]] }).Validate());
        Assert.Equal(asset.SourceSha256, confirmed.ReferenceSourceSha256);
    }

    [Fact] public async Task TwoConcurrentEditorsCannotSilentlyOverwriteOneAnother()
    {
        using var scratch = new Scratch(); var store = new StudyStore(Path.Combine(scratch.Folder, "study.db"));
        var first = new StudyPlanStore(store); var second = new StudyPlanStore(store); first.Save(Plan(), "Initial reviewer", "Initial plan", 0);
        var edits = await Task.WhenAll(new[] { first, second }.Select((editor, i) => Task.Run(() =>
        {
            try { editor.Save(Plan() with { Title = "Editor " + i }, "Reviewer " + i, "Concurrent revision", 1); return true; }
            catch (InvalidOperationException) { return false; }
        })));
        Assert.Single(edits, saved => saved); Assert.Equal(2, first.History().Length);
    }

    [Fact] public void ChangedStoredPlanMaterialFailsIntegrityInsteadOfDroppingExpectedAnimals()
    {
        using var scratch = new Scratch(); var store = new StudyStore(Path.Combine(scratch.Folder, "study.db")); var plans = new StudyPlanStore(store);
        plans.Save(Plan(), "Reviewer", "Synthetic plan", 0);
        using var connection = new SqliteConnection("Data Source=" + store.DatabasePath); connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE study_plans SET json='{}' WHERE revision=1"; command.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => plans.History());
    }
}
