using System.Globalization;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using Xunit;

namespace MouseTrackerTabajara.Tests;

public class GeometryEventsProtocolTests
{
    const string PinnedSettingsHash = "205d28d0e7906910673150c6df697f9b268a2b4f1222f2d20887818bac6a7a71";

    [Fact]
    public void DisplayTransform_PreservesLetterboxAspectAndDpi()
    {
        AssertMapped(720, 480, 1440, 960, 160, 120, 80, 60);
        AssertMapped(10, 480, 1440, 960, 160, 120, null, null);
        AssertMapped(1360, 480, 1440, 960, 160, 120, null, null);
        AssertMapped(1359, 480, 1440, 960, 160, 120, 159.875, 60);
        AssertMapped(320, 240, 640, 480, 320, 180, 160, 90);
        AssertMapped(320, 10, 640, 480, 320, 180, null, null);
        AssertMapped(640, 240, 640, 480, 320, 180, null, null);
        AssertMapped(320, 240, 640, 480, 180, 320, 90, 160);
        AssertMapped(10, 240, 640, 480, 180, 320, null, null);
        AssertMapped(200, 154.6875, 1050, 700, 1920, 1080, 200d * 64 / 35, 100d * 64 / 35);
        AssertMapped(200, 10, 1050, 700, 1920, 1080, null, null);
        AssertMapped(1050, 400, 1050, 700, 1920, 1080, null, null);
        Assert.Null(DisplayToVideo.MapDips(double.NaN, 1, 100, 100, 10, 10));
        Assert.Throws<ArgumentException>(() => DisplayToVideo.MapDips(1, 1, 0, 100, 10, 10));
        Assert.Throws<ArgumentException>(() => DisplayToVideo.MapDevicePixels(1, 1, 100, 100, 0, 10, 10));

        var current = Pool();
        var forward = ArenaGeometry.FromCorners(current, 1920, 1080, 100, 200, 1800, 900);
        var reverse = ArenaGeometry.FromCorners(current, 1920, 1080, 1800, 900, 100, 200);
        Assert.Equal(forward.Cx, reverse.Cx);
        Assert.Equal(forward.Cy, reverse.Cy);
        Assert.Equal(forward.Rx, reverse.Rx);
        Assert.Equal(forward.Ry, reverse.Ry);
        Assert.False(forward.ArenaConfirmed);
        Assert.Equal((100d + 1800) / (2 * 1920), forward.Cx, 12);
        Assert.Equal((200d + 900) / (2 * 1080), forward.Cy, 12);
        Assert.Equal((1800d - 100) / (2 * 1920), forward.Rx, 12);
        Assert.Equal((900d - 200) / (2 * 1080), forward.Ry, 12);
        Assert.Throws<ArgumentException>(() => ArenaGeometry.FromCorners(current, 200, 200, -1, 10, 80, 80));
        Assert.Throws<ArgumentException>(() => ArenaGeometry.FromCorners(current, 200, 200, 10, 10, 12, 10.4));

        var platform = ArenaGeometry.WithPlatform(current, 1920, 1080, 960, 540, 80);
        Assert.Equal(960d / 1920, platform.Platform!.X, 12);
        Assert.Equal(540d / 1080, platform.Platform.Y, 12);
        Assert.Equal(80d / (2 * 1920), platform.Platform.Radius, 12);
        Assert.False(platform.ArenaConfirmed);
        Assert.Throws<ArgumentException>(() => ArenaGeometry.WithPlatform(current, 200, 200, 200, 10, 40));
        var sized = ArenaGeometry.WithPoolDiameter(current, 120);
        Assert.Equal(120, sized.DiameterCm);
        Assert.False(sized.ArenaConfirmed);
        var confirmed = ArenaGeometry.Confirm(sized);
        Assert.True(confirmed.ArenaConfirmed);
        Assert.True(ArenaGeometry.SameGeometry(sized, confirmed));
        Assert.True(ArenaGeometry.WithPoolDiameter(confirmed, 120).ArenaConfirmed);
        Assert.False(ArenaGeometry.WithPoolDiameter(confirmed, 150).ArenaConfirmed);
    }

    [Fact]
    public void GeometryRoundTrip_KeepsTheRawRunAndPinnedSettingsHash()
    {
        var pinned = JsonDefaults.Deserialize<TrackingSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "fixtures/sample-settings.json")));
        Assert.Equal(PinnedSettingsHash, SettingsIdentity.Hash(pinned));
        Assert.Equal(PinnedSettingsHash, StudyStore.SettingsHash(pinned));

        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var asset = Asset(Pool(), TrialMode.Probe, 20);
        store.AddOrGet(asset);
        var run = MakeRun(asset, Track(0, 100, 100), Track(1, 110, 100));
        store.SaveRun(run);
        store.RecordReview(run, true);
        var rawJson = JsonDefaults.Serialize(store.LatestRun(asset.Id));
        var edited = ArenaGeometry.FromCorners(asset.Settings, 200, 200, 20, 30, 160, 150);
        var reversed = ArenaGeometry.FromCorners(asset.Settings, 200, 200, 160, 150, 20, 30);
        Assert.Equal(edited.Cx, reversed.Cx);
        Assert.Equal(0.45, edited.Cx, 12);
        Assert.Equal(0.45, edited.Cy, 12);
        Assert.Equal(0.35, edited.Rx, 12);
        Assert.Equal(0.30, edited.Ry, 12);
        Assert.False(edited.ArenaConfirmed);
        var saved = store.SaveEditedSettings(asset with { Settings = edited with { ArenaConfirmed = true } }, "opposite corners");
        Assert.False(saved.ArenaConfirmed);
        Assert.Equal(edited.Cx, saved.Cx);
        var reopened = new StudyStore(path);
        var loaded = reopened.Videos().Single().Settings;
        Assert.Equal(saved.Cx, loaded.Cx);
        Assert.Equal(saved.Cy, loaded.Cy);
        Assert.Equal(saved.Rx, loaded.Rx);
        Assert.Equal(saved.Ry, loaded.Ry);
        Assert.False(loaded.ArenaConfirmed);
        Assert.Equal((0.5 - saved.Cx) / saved.Rx, (0.5 - loaded.Cx) / loaded.Rx, 12);
        Assert.Equal(rawJson, JsonDefaults.Serialize(reopened.LatestRun(asset.Id)));
        Assert.Equal("opposite corners", Assert.Single(reopened.GeometryRevisions(asset.Id)).Reason);
        var contract = FrameContract.For(true, false);
        Assert.True(contract.EditablePreview);
        Assert.False(contract.ShowAnalyzedTrack);
        Assert.False(contract.AllowCorrection);
        Assert.False(contract.AllowReviewConfirmation);
        Assert.Equal("Editable preview. This geometry is not the analyzed run. Measurements below belong to the previous run.", contract.Label);
        Assert.Equal("Analyzed geometry. Overlays, detection mask, and measurements use this run.", FrameContract.For(true, true).Label);
        Assert.Equal("Editable preview. No analyzed run yet.", FrameContract.For(false, false).Label);
        var interior = DisplayToVideo.MapDips(400, 300, 800, 600, 200, 200);
        Assert.Equal(100, interior!.Value.X, 9);
        Assert.Equal(100, interior.Value.Y, 9);
        Assert.False(reopened.Reviewed(run.Id));
        Assert.Contains(reopened.ReviewHistory(run.Id), row => row.Confirmed && row.Contract is not null);
        var confirmed = store.SaveEditedSettings(asset with { Settings = ArenaGeometry.Confirm(loaded) }, "confirm the saved ellipse");
        Assert.True(confirmed.ArenaConfirmed);
        Assert.True(ArenaGeometry.SameGeometry(saved, confirmed));
    }

    [Fact]
    public void Corrections_RequireExactFramesAndRecomputeOnlyTheEffectiveRun()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var asset = Asset(Pool(), TrialMode.Training, 4);
        store.AddOrGet(asset);
        var run = MakeRun(asset, Track(0, 100, 100), Track(1, 110, 100), Track(2, 120, 100), Track(3, 130, 100));
        store.SaveRun(run);
        store.RecordReview(run, true);
        var storedRun = store.LatestRun(asset.Id)!;
        var rawPoints = JsonDefaults.Serialize(storedRun.Points);
        var rawMetrics = JsonDefaults.Serialize(storedRun.Metrics);
        Assert.True(store.LatestRun(asset.Id)!.Metrics.Distance > 0);

        var missingReason = Assert.Throws<ArgumentException>(() => store.SaveCorrection(run, new(run.Id, 1, 100, 100, " ", DateTime.UtcNow)));
        Assert.Contains("reason", missingReason.Message);
        var oneCoordinate = Assert.Throws<ArgumentException>(() => store.SaveCorrection(run, new(run.Id, 1, 100, null, "body", DateTime.UtcNow)));
        Assert.Contains("both coordinates", oneCoordinate.Message);
        var outsideImage = Assert.Throws<ArgumentException>(() => store.SaveCorrection(run, new(run.Id, 1, 250, 100, "body", DateTime.UtcNow)));
        Assert.Contains("outside the source image", outsideImage.Message);
        var outsidePool = Assert.Throws<ArgumentException>(() => store.SaveCorrection(run, new(run.Id, 1, 0, 0, "body", DateTime.UtcNow)));
        Assert.Contains("outside the pool", outsidePool.Message);
        var wrongRun = Assert.Throws<ArgumentException>(() => store.SaveCorrection(run, new("other-run", 1, 100, 100, "body", DateTime.UtcNow)));
        Assert.Contains("outside this run", wrongRun.Message);
        var wrongSource = Assert.Throws<InvalidOperationException>(() => store.SaveCorrection(run with { SourceSha256 = new string('b', 64) }, new(run.Id, 1, 100, 100, "body", DateTime.UtcNow)));
        Assert.Contains("does not belong", wrongSource.Message);
        var widened = run with { Settings = run.Settings with { Rx = 1, Ry = 1 } };
        var storedPool = Assert.Throws<ArgumentException>(() => store.SaveCorrection(widened, new(run.Id, 1, 0, 0, "body", DateTime.UtcNow)));
        Assert.Contains("outside the pool", storedPool.Message);
        Assert.Empty(store.Corrections(run.Id));
        Assert.True(store.Reviewed(run.Id));

        var gapped = run with { Points = [Track(0, 100, 100), Track(2, 120, 100)] };
        var nearest = Assert.Throws<ArgumentException>(() => MetricCalculator.ApplyCorrections(gapped, [new(run.Id, 1, null, null, "missing", DateTime.UtcNow)]));
        Assert.Contains("Frame 1 is not in this run", nearest.Message);
        Assert.Contains("Nearby frames are not substituted", nearest.Message);
        Assert.Equal(PointStatus.Tracked, gapped.Points[0].Status);
        Assert.Equal(2, gapped.Points[1].Frame);
        var duplicated = Assert.Throws<ArgumentException>(() => MetricCalculator.ApplyCorrections(run with { Points = [Track(0, 100, 100), Track(0, 101, 100)] }, [new(run.Id, 0, null, null, "duplicate", DateTime.UtcNow)]));
        Assert.Contains("duplicated", duplicated.Message);
        var outside = Assert.Throws<ArgumentException>(() => FrameIdentity.RequireSourceFrame(4, 4));
        Assert.Contains("Nearby frames are not substituted", outside.Message);

        store.SaveCorrection(run, new(run.Id, 1, null, null, "Exact frame excluded", DateTime.UtcNow));
        var reopened = new StudyStore(path);
        var raw = reopened.LatestRun(asset.Id)!;
        var effective = reopened.EffectiveRun(raw);
        Assert.Equal(rawPoints, JsonDefaults.Serialize(raw.Points));
        Assert.Equal(rawMetrics, JsonDefaults.Serialize(raw.Metrics));
        Assert.Equal(PointStatus.Tracked, raw.Points.Single(point => point.Frame == 1).Status);
        Assert.Equal(PointStatus.Excluded, effective.Points.Single(point => point.Frame == 1).Status);
        Assert.Null(effective.Points.Single(point => point.Frame == 1).X);
        Assert.Equal(raw.Metrics.TrackedFrames - 1, effective.Metrics.TrackedFrames);
        Assert.True(effective.Metrics.Distance < raw.Metrics.Distance);
        Assert.Equal("Exact frame excluded", Assert.Single(effective.AppliedCorrections).Reason);
        Assert.False(reopened.Reviewed(run.Id));
        var confirmed = Assert.Single(reopened.ReviewHistory(run.Id), row => row.Confirmed);
        Assert.NotNull(confirmed.Contract);
        Assert.Contains(reopened.ReviewHistory(run.Id), row => !row.Confirmed);
    }

    [Fact]
    public void BiologicalEvents_StayDistinctFromPlatformSiteEntry()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var settings = Pool() with { Platform = new PlatformSite(0.5, 0.5, 0.05) };
        var asset = Asset(settings, TrialMode.Probe, 20);
        store.AddOrGet(asset);
        var run = MakeRun(asset, Track(0, 100, 40), Track(1, 100, 100));
        store.SaveRun(run);
        var metrics = store.LatestRun(asset.Id)!.Metrics;
        Assert.Equal(1, metrics.PlatformSiteEntries);
        Assert.Contains("site entry is not confirmed platform mounting", metrics.EndpointEligibility["first_site_visit_seconds"].Reason, StringComparison.Ordinal);
        Assert.Empty(store.EffectiveBiologicalEvents(asset.Id));
        var metricJson = JsonDefaults.Serialize(metrics);

        var missingReason = Assert.Throws<ArgumentException>(() => store.RecordBiologicalEvent(asset, BiologicalEventKind.PlatformMounting, 1, " ", "Reviewer"));
        Assert.Contains("reason", missingReason.Message);
        var missingReviewer = Assert.Throws<ArgumentException>(() => store.RecordBiologicalEvent(asset, BiologicalEventKind.WaterEntry, 1, "entered", " "));
        Assert.Contains("reviewer", missingReviewer.Message);
        var outside = Assert.Throws<ArgumentException>(() => store.RecordBiologicalEvent(asset, BiologicalEventKind.RecordingEnd, 20, "ended", "Reviewer"));
        Assert.Contains("Nearby frames are not substituted", outside.Message);
        Assert.Empty(store.BiologicalEventHistory(asset.Id));

        var mounting = store.RecordBiologicalEvent(asset, BiologicalEventKind.PlatformMounting, 1, "Mouse stayed on the platform", "Reviewer");
        Assert.Equal(BiologicalEventKind.PlatformMounting, mounting.Kind);
        Assert.Equal(1, mounting.Frame);
        Assert.Equal(0.1, mounting.TimeSeconds, 9);
        Assert.Equal(1, mounting.Revision);
        var revised = store.RecordBiologicalEvent(asset, BiologicalEventKind.PlatformMounting, 3, "Mounting confirmed two frames later", "Reviewer", mounting.Id);
        Assert.Equal(mounting.Id, revised.Id);
        Assert.Equal(2, revised.Revision);
        Assert.Equal(3, revised.Frame);
        var history = store.BiologicalEventHistory(asset.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal("Mouse stayed on the platform", history[0].Reason);
        Assert.Equal(1, history[0].Revision);
        var effective = Assert.Single(store.EffectiveBiologicalEvents(asset.Id));
        Assert.Equal(revised.Id, effective.Id);
        Assert.Equal(2, effective.Revision);
        Assert.Equal("Mounting confirmed two frames later", effective.Reason);
        var kindChange = Assert.Throws<ArgumentException>(() => store.RecordBiologicalEvent(asset, BiologicalEventKind.WaterEntry, 3, "different kind", "Reviewer", mounting.Id));
        Assert.Contains("keeps the biological annotation kind", kindChange.Message);
        Assert.Equal(2, store.BiologicalEventHistory(asset.Id).Count);
        Assert.Equal(metricJson, JsonDefaults.Serialize(new StudyStore(path).LatestRun(asset.Id)!.Metrics));
        Assert.Equal(2, AuditCount(path, "biological_event_recorded"));
    }

    [Fact]
    public void ProtocolWindows_KeepRequestedExposureAndRevisions()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var asset = Asset(Pool(), TrialMode.Probe, 20);
        store.AddOrGet(asset);
        var before = asset.Settings;
        var run = MakeRun(asset, Track(0, 100, 100), Track(1, 100, 100));
        store.SaveRun(run);
        store.RecordReview(run, true);

        Assert.Throws<ArgumentException>(() => store.SaveProtocolWindow(asset, Draft(0, false, null, true)));
        Assert.Throws<ArgumentException>(() => store.SaveProtocolWindow(asset, Draft(86401, false, null, true)));
        Assert.Throws<ArgumentException>(() => store.SaveProtocolWindow(asset, Draft(30, false, null, true) with { Reason = " " }));
        Assert.Throws<ArgumentException>(() => store.SaveProtocolWindow(asset, Draft(30, false, null, true) with { Reviewer = " " }));
        var missingRelease = Assert.Throws<ArgumentException>(() => store.SaveProtocolWindow(asset, Draft(30, true, null, true)));
        Assert.Contains("Confirmed release requires a source frame", missingRelease.Message);
        var outside = Assert.Throws<ArgumentException>(() => store.SaveProtocolWindow(asset, Draft(30, true, 20, true)));
        Assert.Contains("Nearby frames are not substituted", outside.Message);
        var siteEntry = Assert.Throws<ArgumentException>(() => store.SaveProtocolWindow(asset, Draft(30, true, 2, true)));
        Assert.Equal("Confirm water entry at the release frame before confirming release. Platform-site entry is not water entry or mounting.", siteEntry.Message);
        Assert.Empty(store.ProtocolHistory(asset.Id));

        store.RecordBiologicalEvent(asset, BiologicalEventKind.WaterEntry, 4, "Released into the water", "Reviewer");
        store.RecordBiologicalEvent(asset, BiologicalEventKind.PlatformMounting, 6, "Later mounting is a separate annotation", "Reviewer");
        var saved = store.SaveProtocolWindow(asset, Draft(30, true, 4, true) with { Phase = TrialMode.Training });
        Assert.Equal(TrialMode.Probe, saved.Phase);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(30, saved.RequestedExposureSeconds);
        Assert.Equal("Planned window", saved.Reason);
        var again = store.SaveProtocolWindow(asset, Draft(30, true, 4, true));
        Assert.Equal(2, again.Revision);
        Assert.Equal(30, again.RequestedExposureSeconds);
        var history = store.ProtocolHistory(asset.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal("Planned window", history[0].Reason);
        var loaded = new StudyStore(path);
        Assert.Equal(2, loaded.LatestProtocol(asset.Id)!.Revision);
        var after = loaded.Videos().Single().Settings;
        Assert.Equal(before.StartSeconds, after.StartSeconds);
        Assert.Equal(before.EndSeconds, after.EndSeconds);
        Assert.Equal(SettingsIdentity.Hash(before), SettingsIdentity.Hash(after));
        Assert.False(loaded.Reviewed(run.Id));
        Assert.Contains(loaded.ReviewHistory(run.Id), row => row.Confirmed && row.Contract is not null);
        var outcome = ProtocolEvaluator.Evaluate(loaded.LatestProtocol(asset.Id)!, asset.Metadata, TrialMode.Probe, loaded.EffectiveBiologicalEvents(asset.Id));
        Assert.Equal(30, outcome.RequestedExposureSeconds);
        Assert.Equal("Source truncated", outcome.TerminationReason);
        Assert.Contains("Requested exposure was not shortened.", outcome.Visible);
        Assert.Contains("Recording is truncated before the planned probe exposure.", outcome.Visible);
        Assert.Contains("Platform mounting is annotated and does not shorten the probe.", outcome.Visible);
        Assert.True(outcome.ActualExposureSeconds < outcome.RequestedExposureSeconds);
    }

    [Fact]
    public void ProtocolOutcomes_AreVisibleWithoutStatisticalComparison()
    {
        var release = new[] { Mark(BiologicalEventKind.WaterEntry, 10) };
        var probe = Eval(Plan(TrialMode.Probe, 5, true, 10, true), release);
        Assert.Equal("Requested 5.000 s; actual 5.000 s; source after release 9.000 s; recording 10.000 s; termination Fixed probe exposure. Requested exposure was not shortened.", probe.Visible);
        Assert.Equal(5, probe.ActualExposureSeconds);
        Assert.Equal(5, probe.RequestedExposureSeconds);

        var mountedProbe = Eval(Plan(TrialMode.Probe, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.PlatformMounting, 20, id: "mount")]);
        Assert.Equal(5, mountedProbe.ActualExposureSeconds);
        Assert.Equal("Fixed probe exposure", mountedProbe.TerminationReason);
        Assert.Empty(mountedProbe.Holds);
        Assert.Contains(mountedProbe.Notes, note => note.Kind == ProtocolNoteKind.MountingDuringProbe);
        Assert.Contains("Platform mounting is annotated and does not shorten the probe.", mountedProbe.Visible);

        var shortRelease = new[] { Mark(BiologicalEventKind.WaterEntry, 10, 40) };
        var truncatedProbe = Eval(Plan(TrialMode.Probe, 5, true, 10, true), shortRelease, frames: 40);
        Assert.Equal("Requested 5.000 s; actual 3.000 s; source after release 3.000 s; recording 4.000 s; termination Source truncated. Requested exposure was not shortened.\nRecording is truncated before the planned probe exposure.", truncatedProbe.Visible);
        Assert.Equal(5, truncatedProbe.RequestedExposureSeconds);

        var ended = Mark(BiologicalEventKind.RecordingEnd, 20, id: "end");
        Assert.Equal(2, ended.TimeSeconds, 9);
        var endedProbe = Eval(Plan(TrialMode.Probe, 5, true, 10, true), [.. release, ended]);
        Assert.Equal(1.1, endedProbe.ActualExposureSeconds);
        Assert.Equal(1.1, endedProbe.SourceExposureSeconds);
        Assert.Equal(10, endedProbe.RecordingDurationSeconds);
        Assert.Equal("Source truncated", endedProbe.TerminationReason);
        var lastFrame = Eval(Plan(TrialMode.Probe, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.RecordingEnd, 99, id: "last")]);
        Assert.Equal("Fixed probe exposure", lastFrame.TerminationReason);
        Assert.Equal(5, lastFrame.ActualExposureSeconds);

        var training = Eval(Plan(TrialMode.Training, 5, true, 10, true), release);
        Assert.Equal("Requested 5.000 s; actual 5.000 s; source after release 9.000 s; recording 10.000 s; termination Protocol timeout. Requested exposure was not shortened.", training.Visible);
        var mounted = Eval(Plan(TrialMode.Training, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.PlatformMounting, 30, id: "mount")]);
        Assert.Equal("Requested 5.000 s; actual 2.000 s; source after release 9.000 s; recording 10.000 s; termination Confirmed platform mounting. Requested exposure was not shortened.", mounted.Visible);
        var lateMount = Eval(Plan(TrialMode.Training, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.PlatformMounting, 80, id: "late")]);
        Assert.Equal("Protocol timeout", lateMount.TerminationReason);
        Assert.Equal(5, lateMount.ActualExposureSeconds);
        Assert.DoesNotContain(lateMount.Holds, hold => hold.Contains("after the planned training timeout", StringComparison.Ordinal));
        Assert.Contains(lateMount.Notes, note => note.Kind == ProtocolNoteKind.MountingAfterWindow);
        Assert.Contains("Annotated mounting is after the planned training timeout.", lateMount.Visible);
        var earlyMount = Eval(Plan(TrialMode.Training, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.PlatformMounting, 5, id: "early")]);
        Assert.Equal("Protocol timeout", earlyMount.TerminationReason);
        Assert.DoesNotContain(earlyMount.Holds, hold => hold.Contains("before the confirmed release", StringComparison.Ordinal));
        Assert.Contains(earlyMount.Notes, note => note.Kind == ProtocolNoteKind.MountingBeforeRelease);
        Assert.Contains("Annotated mounting is before the confirmed release.", earlyMount.Visible);
        var intervention = Eval(Plan(TrialMode.Training, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.ResearcherIntervention, 40, id: "hand")]);
        Assert.Equal("Requested 5.000 s; actual 3.000 s; source after release 9.000 s; recording 10.000 s; termination Researcher intervention. Requested exposure was not shortened.\nResearcher intervention ended the trial before the planned window.", intervention.Visible);
        var truncatedTraining = Eval(Plan(TrialMode.Training, 5, true, 10, true), shortRelease, frames: 40);
        Assert.Equal("Requested 5.000 s; actual 3.000 s; source after release 3.000 s; recording 4.000 s; termination Source truncated. Requested exposure was not shortened.\nRecording is truncated before the planned training timeout.", truncatedTraining.Visible);

        var absent = Eval(Plan(TrialMode.Probe, 5, false, null, false), []);
        Assert.Equal("Requested 5.000 s; actual withheld; source after release withheld; recording 10.000 s; termination Termination is not established. Requested exposure was not shortened.\nPlanned window is not confirmed.\nRelease is not confirmed.\nActual exposure is withheld until release is confirmed.", absent.Visible);
        Assert.Null(absent.ActualExposureSeconds);
        var unspecified = Eval(Plan(TrialMode.Unspecified, 5, false, null, false), [], TrialMode.Unspecified);
        Assert.Contains("Trial phase is unspecified.", unspecified.Visible);
        var mismatched = Eval(Plan(TrialMode.Probe, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.PlatformMounting, 20, id: "mount")], TrialMode.Training);
        Assert.Equal("Fixed probe exposure", mismatched.TerminationReason);
        Assert.Equal(5, mismatched.ActualExposureSeconds);
        Assert.Contains("Stored window phase differs from the recording's current phase.", mismatched.Visible);
        Assert.Contains("does not shorten the probe", mismatched.Visible);
        Assert.DoesNotContain("Confirmed platform mounting", mismatched.Visible);
        var unmatched = Eval(Plan(TrialMode.Probe, 5, true, 10, true), []);
        Assert.Contains("Release frame does not match a water-entry annotation.", unmatched.Visible);
        Assert.Contains("actual withheld", unmatched.Visible);
        var outsideRelease = Eval(Plan(TrialMode.Probe, 5, true, 500, true), release);
        Assert.Contains("Release frame is outside the source recording.", outsideRelease.Visible);
        var beforeRelease = Eval(Plan(TrialMode.Probe, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.RecordingEnd, 5, id: "early-end")]);
        Assert.Contains("Recording ends before the confirmed release.", beforeRelease.Visible);
        Assert.Equal("Source truncated", beforeRelease.TerminationReason);
        Assert.Null(beforeRelease.ActualExposureSeconds);

        var equal = ProtocolEvaluator.Compare([("mouse-a", probe), ("mouse-b", Eval(Plan(TrialMode.Probe, 5, true, 10, true), release))]);
        Assert.True(equal.Comparable);
        Assert.Equal("Confirmed probe exposures match at 5.000 s.\nNo statistical comparison is performed.", equal.Visible);
        var unequal = ProtocolEvaluator.Compare([("mouse-a", Eval(Plan(TrialMode.Probe, 3, true, 10, true), release)), ("mouse-b", Eval(Plan(TrialMode.Probe, 6, true, 10, true), release))]);
        Assert.False(unequal.Comparable);
        Assert.Equal("Unequal probe durations: mouse-a 3.000 s, mouse-b 6.000 s.\nNo statistical comparison is performed.", unequal.Visible);
        var outsideProbe = Eval(Plan(TrialMode.Probe, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.PlatformMounting, 80, id: "after")]);
        var beforeProbe = Eval(Plan(TrialMode.Probe, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.PlatformMounting, 5, id: "before")]);
        Assert.Empty(outsideProbe.Holds);
        Assert.Empty(beforeProbe.Holds);
        Assert.Contains(outsideProbe.Notes, note => note.Kind == ProtocolNoteKind.MountingAfterWindow && note.Text == "Annotated mounting is after the planned probe end.");
        Assert.Contains(beforeProbe.Notes, note => note.Kind == ProtocolNoteKind.MountingBeforeRelease);
        var noted = ProtocolEvaluator.Compare([("mouse-a", outsideProbe), ("mouse-b", beforeProbe), ("mouse-c", mountedProbe), ("mouse-d", probe)]);
        Assert.True(noted.Comparable);
        Assert.Equal(equal.Visible, noted.Visible);
        var duringIntervention = Eval(Plan(TrialMode.Probe, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.ResearcherIntervention, 20, id: "hand")]);
        Assert.Equal("Fixed probe exposure", duringIntervention.TerminationReason);
        Assert.Equal(5, duringIntervention.ActualExposureSeconds);
        Assert.Contains(duringIntervention.Holds, hold => hold == "Researcher intervention is annotated during the confirmed probe exposure.");
        var held = ProtocolEvaluator.Compare([("mouse-a", duringIntervention), ("mouse-b", probe)]);
        Assert.False(held.Comparable);
        Assert.Equal("Trials with holds stay visible and are not dropped.\nmouse-a: Researcher intervention is annotated during the confirmed probe exposure.\nNo statistical comparison is performed.", held.Visible);
        var mixed = ProtocolEvaluator.Compare([("mouse-a", probe), ("mouse-b", training)]);
        Assert.False(mixed.Comparable);
        Assert.Equal("Mixed phases: Probe, Training.\nNo statistical comparison is performed.", mixed.Visible);
        var trainings = ProtocolEvaluator.Compare([("timeout", training), ("mounted", mounted)]);
        Assert.False(trainings.Comparable);
        Assert.Equal("Training outcomes stay with their own termination reasons.\nNo statistical comparison is performed.", trainings.Visible);
        var none = ProtocolEvaluator.Compare([]);
        Assert.False(none.Comparable);
        Assert.Equal("No trials were supplied.\nNo statistical comparison is performed.", none.Visible);
        foreach (var text in new[] { equal.Visible, unequal.Visible, held.Visible, mixed.Visible, trainings.Visible, none.Visible })
        {
            Assert.EndsWith("No statistical comparison is performed.", text);
            Assert.DoesNotContain("p-value", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("significant", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PlatformSiteEllipse_MatchesTheMetricPredicateOnUnequalAxes()
    {
        var settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .4, Ry = .4, ArenaConfirmed = true, Platform = new(0.5, 0.5, 0.05) };
        var ellipse = ArenaGeometry.SourceEllipse(settings.Platform!, settings, 300, 200);
        Assert.Equal(15, ellipse.HorizontalRadius, 9);
        Assert.Equal(10, ellipse.VerticalRadius, 9);
        Assert.Equal(135, ellipse.Left, 9);
        Assert.Equal(90, ellipse.Top, 9);
        Assert.Equal(30, ellipse.Width, 9);
        Assert.Equal(20, ellipse.Height, 9);
        Assert.True(Math.Sqrt(12d * 12d) <= 15);
        Assert.False(ArenaGeometry.InsidePlatformSite(150, 112, settings, 300, 200));
        Assert.False(ellipse.Contains(150, 112));
        Assert.True(ArenaGeometry.InsidePlatformSite(165, 100, settings, 300, 200));
        Assert.True(ellipse.Contains(165, 100));
        Assert.False(ArenaGeometry.InsidePlatformSite(166, 100, settings, 300, 200));
        Assert.True(ArenaGeometry.InsidePlatformSite(150, 110, settings, 300, 200));
        Assert.False(ArenaGeometry.InsidePlatformSite(150, 111, settings, 300, 200));

        var stretched = new TrackingSettings { Cx = .5, Cy = .5, Rx = .5, Ry = .25, ArenaConfirmed = true, Platform = new(0.5, 0.5, 0.1) };
        var stretchedEllipse = ArenaGeometry.SourceEllipse(stretched.Platform!, stretched, 160, 120);
        Assert.Equal(16, stretchedEllipse.HorizontalRadius, 9);
        Assert.Equal(6, stretchedEllipse.VerticalRadius, 9);
        for (var x = 60; x <= 100; x += 2)
        for (var y = 50; y <= 70; y += 2)
            Assert.Equal(stretchedEllipse.Contains(x, y), ArenaGeometry.InsidePlatformSite(x, y, stretched, 160, 120));

        var meta = new VideoMetadata(300, 200, 10, 2);
        var inside = MetricCalculator.Calculate([new(0, 0, 150, 100, PointStatus.Tracked, 1), new(1, 0.1, 150, 100, PointStatus.Tracked, 1)], meta, settings);
        var outside = MetricCalculator.Calculate([new(0, 0, 150, 112, PointStatus.Tracked, 1), new(1, 0.1, 150, 112, PointStatus.Tracked, 1)], meta, settings);
        Assert.True(inside.PlatformSiteSeconds > 0);
        Assert.Equal(0, outside.PlatformSiteSeconds);
        Assert.Equal(0, outside.PlatformSiteEntries);
    }

    [Fact]
    public void ProbeAnnotations_OutsideTheConfirmedExposure_UseDistinctLabels()
    {
        var release = new[] { Mark(BiologicalEventKind.WaterEntry, 10) };
        var during = Eval(Plan(TrialMode.Probe, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.PlatformMounting, 20, id: "during"), Mark(BiologicalEventKind.ResearcherIntervention, 30, id: "hand")]);
        Assert.Contains("Platform mounting is annotated and does not shorten the probe.", during.Visible);
        Assert.Contains("Researcher intervention is annotated during the confirmed probe exposure.", during.Visible);
        Assert.DoesNotContain("before the confirmed release", during.Visible);
        Assert.DoesNotContain("after the planned probe end", during.Visible);

        var before = Eval(Plan(TrialMode.Probe, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.PlatformMounting, 5, id: "early"), Mark(BiologicalEventKind.ResearcherIntervention, 4, id: "early-hand")]);
        Assert.Contains("Annotated mounting is before the confirmed release.", before.Visible);
        Assert.Contains("Annotated intervention is before the confirmed release.", before.Visible);
        Assert.DoesNotContain("does not shorten the probe", before.Visible);
        Assert.DoesNotContain("during the confirmed probe exposure", before.Visible);

        var after = Eval(Plan(TrialMode.Probe, 5, true, 10, true), [.. release, Mark(BiologicalEventKind.PlatformMounting, 80, id: "late"), Mark(BiologicalEventKind.ResearcherIntervention, 90, id: "late-hand")]);
        Assert.Contains("Annotated mounting is after the planned probe end.", after.Visible);
        Assert.Contains("Annotated intervention is after the planned probe end.", after.Visible);
        Assert.DoesNotContain("does not shorten the probe", after.Visible);
        Assert.DoesNotContain("during the confirmed probe exposure", after.Visible);
    }

    [Fact]
    public void RawRunScope_WithholdsMismatchedTrialEndpointsWithoutTrimming()
    {
        var settings = Pool() with { StartSeconds = 0 };
        var asset = Asset(settings, TrialMode.Probe, 100);
        var points = Enumerable.Range(0, 100).Select(frame => Track(frame, 100, 100)).ToList();
        var run = MakeRun(asset, points.ToArray());
        var window = Plan(TrialMode.Probe, 3, true, 20, true);
        var outcome = ProtocolEvaluator.Evaluate(window, asset.Metadata, TrialMode.Probe, [Mark(BiologicalEventKind.WaterEntry, 20)]);
        var scope = RawRunMeasurementScope.Describe(run, window, outcome);
        Assert.True(scope.TrialEndpointsWithheld);
        Assert.Equal(0, scope.DetectorStartSeconds);
        Assert.Equal(10, scope.DetectorEndSeconds);
        Assert.Equal(2, scope.BiologicalStartSeconds);
        Assert.Equal(5, scope.BiologicalEndSeconds);
        Assert.Contains("Trial endpoints are withheld", scope.Visible);
        Assert.Contains(TrialMeasurementWording.ProtocolLabelDoesNotSupply, scope.Visible);
        Assert.DoesNotContain("D3", scope.Visible);
        Assert.DoesNotContain("measurement contract", scope.Visible);
        Assert.Contains("was not shortened", scope.Visible);
        Assert.Equal(0, run.Settings.StartSeconds);
        Assert.Null(run.Settings.EndSeconds);

        var aligned = RawRunMeasurementScope.Describe(run with { Settings = settings with { StartSeconds = 2, EndSeconds = 5 } }, window, outcome);
        Assert.False(aligned.TrialEndpointsWithheld);
        Assert.Contains("meets the confirmed biological window", aligned.Visible);
        Assert.Contains(TrialMeasurementWording.AlignedRawRun, aligned.Visible);
        Assert.Contains(TrialMeasurementWording.ProtocolLabelDoesNotSupply, aligned.Visible);
        Assert.DoesNotContain("D3", aligned.Visible);
        Assert.Contains("not confirmed biological-trial measurements", aligned.Visible);

        var unlabeled = RawRunMeasurementScope.Describe(run, null, null);
        Assert.False(unlabeled.TrialEndpointsWithheld);
        Assert.Contains("No planned biological window", unlabeled.Visible);
        Assert.DoesNotContain("Trial endpoints are withheld", unlabeled.Visible);

        using var scratch = new Scratch();
        ExportWriter.SaveRun(run, scratch.Folder, scope);
        var html = File.ReadAllText(Path.Combine(scratch.Folder, "report.html"));
        Assert.Contains("Trial endpoints are withheld", html);
        Assert.Contains(TrialMeasurementWording.ProtocolLabelDoesNotSupply, html);
        Assert.DoesNotContain("D3", html);
        Assert.DoesNotContain("measurement contract", html);
        Assert.DoesNotContain("Distance: ", html);
        Assert.Contains("Mouse Tracker Tabajara", html);
        Assert.Equal(run.Metrics.Distance, JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(Path.Combine(scratch.Folder, "analysis.json"))).Metrics.Distance);
    }

    [Fact]
    public void Reviews_RejectStaleSettingsEventsAndLegacyContracts()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var settings = Pool();
        var asset = Asset(settings, TrialMode.Probe, 20);
        store.AddOrGet(asset);
        var run = MakeRun(asset, Track(0, 100, 100), Track(1, 110, 100));
        store.SaveRun(run);
        store.RecordReview(run, true);

        store.SaveVideo(asset with { Settings = settings with { Threshold = 40 } });
        Assert.False(store.Reviewed(run.Id));
        var stale = Assert.Throws<InvalidOperationException>(() => store.RecordReview(run, true));
        Assert.Contains("stale settings", stale.Message);
        Assert.Equal(1, store.ReviewHistory(run.Id).Count(row => row.Confirmed));
        store.SaveVideo(asset);
        store.RecordReview(run, true);
        Assert.True(store.Reviewed(run.Id));

        store.SaveVideo(asset with { Phase = TrialMode.Training });
        Assert.False(store.Reviewed(run.Id));
        Assert.Contains(store.ReviewHistory(run.Id), row => row.Confirmed && row.Contract is not null);
        store.SaveVideo(asset);
        store.RecordReview(run, true);

        var original = store.ReviewHistory(run.Id).Last(row => row.Confirmed).Contract ?? throw new InvalidOperationException("Confirmed review has no contract.");
        store.RecordBiologicalEvent(asset, BiologicalEventKind.WaterEntry, 2, "Release", "Reviewer");
        Assert.False(store.Reviewed(run.Id));
        InsertReview(path, run.Id, confirmed: true, JsonDefaults.Serialize(original));
        Assert.False(store.Reviewed(run.Id));
        Assert.Contains(store.ReviewHistory(run.Id), row => row.Confirmed && row.Contract is not null && row.Contract.EventFingerprint == original.EventFingerprint);

        InsertReview(path, run.Id, confirmed: true, null);
        Assert.False(store.Reviewed(run.Id));
        Assert.Null(store.ReviewHistory(run.Id).Last().Contract);
        Assert.Contains(store.ReviewHistory(run.Id), row => row.Confirmed && row.Contract is not null);

        store.SaveProtocolWindow(asset, Draft(5, false, null, true));
        Assert.False(store.Reviewed(run.Id));
        store.SaveCorrection(run, new(run.Id, 1, 105, 100, "body center", DateTime.UtcNow));
        Assert.False(new StudyStore(path).Reviewed(run.Id));
        Assert.Contains(new StudyStore(path).ReviewHistory(run.Id), row => row.Confirmed && row.Contract is not null);
    }

    static void AssertMapped(double x, double y, double viewportWidth, double viewportHeight, int sourceWidth, int sourceHeight, double? expectX, double? expectY)
    {
        var mapped = DisplayToVideo.MapDips(x, y, viewportWidth, viewportHeight, sourceWidth, sourceHeight);
        if (expectX is null)
        {
            Assert.Null(mapped);
            foreach (var dpi in new[] { 96d, 144d, 192d })
            {
                var scale = dpi / 96d;
                Assert.Null(DisplayToVideo.MapDevicePixels(x * scale, y * scale, viewportWidth * scale, viewportHeight * scale, dpi, sourceWidth, sourceHeight));
            }
            return;
        }
        Assert.NotNull(mapped);
        Assert.Equal(expectX.Value, mapped.Value.X, 9);
        Assert.Equal(expectY!.Value, mapped.Value.Y, 9);
        foreach (var dpi in new[] { 96d, 144d, 192d })
        {
            var scale = dpi / 96d;
            var device = DisplayToVideo.MapDevicePixels(x * scale, y * scale, viewportWidth * scale, viewportHeight * scale, dpi, sourceWidth, sourceHeight);
            Assert.NotNull(device);
            Assert.Equal(mapped.Value.X, device.Value.X, 9);
            Assert.Equal(mapped.Value.Y, device.Value.Y, 9);
        }
    }

    static void InsertReview(string path, string runId, bool confirmed, string? contractJson)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reviews(run_id,confirmed,created_utc,contract_json) VALUES($id,$confirmed,$utc,$contract)";
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$confirmed", confirmed ? 1 : 0);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$contract", (object?)contractJson ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    static int AuditCount(string path, string action)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit WHERE action=$action";
        command.Parameters.AddWithValue("$action", action);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static TrackingSettings Pool() => new() { Cx = .5, Cy = .5, Rx = .4, Ry = .4, ArenaConfirmed = true };

    static VideoAsset Asset(TrackingSettings settings, TrialMode phase, int frames, char sha = 'a') => new()
    {
        SourceSha256 = new string(sha, 64),
        AnimalId = "Synthetic",
        Phase = phase,
        Settings = settings,
        Metadata = new VideoMetadata(200, 200, 10, frames)
    };

    static TrackPoint Track(int frame, int x, int y) => new(frame, frame / 10d, x, y, PointStatus.Tracked, 1);

    static AnalysisRun MakeRun(VideoAsset asset, params TrackPoint[] points)
    {
        var list = points.ToList();
        return new AnalysisRun
        {
            VideoId = asset.Id,
            SourceSha256 = asset.SourceSha256,
            Metadata = asset.Metadata,
            Settings = asset.Settings,
            NominalTimingVerified = true,
            Points = list,
            Metrics = MetricCalculator.Calculate(list, asset.Metadata, asset.Settings)
        };
    }

    static BiologicalEvent Mark(BiologicalEventKind kind, int frame, int frameCount = 100, string id = "event") =>
        BiologicalEvent.Create("video", null, kind, frame, new VideoMetadata(200, 200, 10, frameCount), "observed", "reviewer", 1, DateTime.UtcNow, id);

    static ProtocolWindow Plan(TrialMode phase, double seconds, bool release, int? frame, bool confirmed) => new()
    {
        Phase = phase,
        RequestedExposureSeconds = seconds,
        ReleaseConfirmed = release,
        ReleaseFrame = frame,
        WindowConfirmed = confirmed,
        Reason = "planned",
        Reviewer = "reviewer"
    };

    static ProtocolOutcome Eval(ProtocolWindow window, IReadOnlyList<BiologicalEvent> events, TrialMode? current = null, int frames = 100) =>
        ProtocolEvaluator.Evaluate(window, new VideoMetadata(200, 200, 10, frames), current ?? window.Phase, events);

    static ProtocolWindow Draft(double seconds, bool release, int? frame, bool confirmed) => new()
    {
        RequestedExposureSeconds = seconds,
        ReleaseConfirmed = release,
        ReleaseFrame = frame,
        WindowConfirmed = confirmed,
        Reason = "Planned window",
        Reviewer = "Reviewer"
    };
}
