using System.Globalization;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using Xunit;

namespace MouseTrackerTabajara.Tests;

public class BlindConsequenceReviewTests
{
    [Theory]
    [InlineData("animal")]
    [InlineData("recording")]
    [InlineData("frame")]
    public void GenericDisplayWordsRemainLegalAnimalIds(string animalId)
    {
        using var scratch = new Scratch(); var store = new StudyStore(Path.Combine(scratch.Folder, "study.db"));
        var asset = store.AddOrGet(SentinelAsset() with { AnimalId = animalId, AnimalIdConfirmed = true, GroupName = "recording" });
        Assert.Matches("^Recording [0-9A-F]{8}$", asset.BlindedAlias);
        Assert.Matches("^Animal [0-9A-F]{8}$", asset.BlindedAnimalAlias);
        var reopened = new StudyStore(store.DatabasePath).Videos().Single();
        Assert.Equal(asset.BlindedAlias, reopened.BlindedAlias); Assert.Equal(asset.BlindedAnimalAlias, reopened.BlindedAnimalAlias);
    }

    [Fact]
    public void AliasesStayStableAcrossReopenAndDoNotCopySourceNames()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var asset = SentinelAsset() with { BlindedAlias = "Mouse AB12CD", BlindedAnimalAlias = "Animal KO1" };
        var stored = store.AddOrGet(asset);
        Assert.Matches("^[0-9A-F]{8}$", stored.BlindedAlias["Recording ".Length..]);
        Assert.StartsWith("Recording ", stored.BlindedAlias, StringComparison.Ordinal);
        Assert.DoesNotContain("Mouse AB12CD", stored.BlindedAlias, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SENTINEL_FILE", stored.BlindedAlias, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SENTINEL_ANIMAL", stored.BlindedAlias, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SENTINEL_GROUP", stored.BlindedAlias, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", stored.BlindedAnimalAlias);
        Assert.Equal("Animal unconfirmed", BlindIdentity.AnimalAlias(stored));
        Assert.Equal("Recording", BlindIdentity.RecordingAlias(asset with { BlindedAlias = asset.AnimalId }));
        store.SaveVideo(stored with { BlindedAlias = "KO1", BlindedAnimalAlias = "Animal KO1" });
        var reopened = new StudyStore(path).Videos().Single();
        Assert.Equal(stored.BlindedAlias, reopened.BlindedAlias);
        Assert.Equal(stored.BlindedAlias, BlindIdentity.RecordingAlias(reopened));

        var first = store.AddOrGet(SentinelAsset() with { SourceSha256 = new string('b', 64), AnimalId = "A", GroupName = "KO", AnimalIdConfirmed = true, BlindedAlias = "KO1", BlindedAnimalAlias = "Animal KO1" });
        var second = store.AddOrGet(SentinelAsset() with { SourceSha256 = new string('c', 64), AnimalId = "A", GroupName = "WT", AnimalIdConfirmed = true, BlindedAlias = "WT2", BlindedAnimalAlias = "Animal WT" });
        Assert.Matches("^[0-9A-F]{8}$", first.BlindedAnimalAlias["Animal ".Length..]);
        Assert.StartsWith("Animal ", first.BlindedAnimalAlias, StringComparison.Ordinal);
        Assert.Equal(first.BlindedAnimalAlias, second.BlindedAnimalAlias);
        Assert.Equal(first.BlindedAnimalAlias, BlindIdentity.AnimalAlias(first));
        Assert.DoesNotContain("KO", first.BlindedAlias, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KO", first.BlindedAnimalAlias, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WT", second.BlindedAlias, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PBS", first.BlindedAnimalAlias, StringComparison.OrdinalIgnoreCase);
        var run = MakeRun(first, Track(0, 40, 60), Track(1, 42, 60));
        store.SaveRun(run);
        store.RecordReview(run, true);
        store.SaveVideo(first with { BlindedAlias = "Mouse AB12CD" });
        Assert.True(store.Reviewed(run.Id));
        Assert.Equal(first.BlindedAlias, store.Videos().Single(video => video.Id == first.Id).BlindedAlias);
        store.SaveEditedSettings(first with { BlindedAlias = "", BlindedAnimalAlias = "", Settings = first.Settings with { Threshold = first.Settings.Threshold + 1 } }, "threshold");
        var edited = store.Videos().Single(video => video.Id == first.Id);
        Assert.Equal(first.BlindedAlias, edited.BlindedAlias);
        Assert.Equal(first.BlindedAnimalAlias, edited.BlindedAnimalAlias);
        var persisted = new StudyStore(path).Videos().Single(video => video.Id == first.Id);
        Assert.Equal(first.BlindedAlias, persisted.BlindedAlias);
        Assert.Equal(first.BlindedAnimalAlias, persisted.BlindedAnimalAlias);
    }

    [Fact]
    public void ShortLabelsAreProjectedWithoutErasingOrdinaryWording()
    {
        var secrets = BlindSecrets.FromAssets(
        [
            SentinelAsset() with { AnimalId = "A", GroupName = "KO" },
            SentinelAsset() with { AnimalId = "B", GroupName = "WT" },
            SentinelAsset() with { AnimalId = "PBS", GroupName = "PBS" }
        ]);
        const string ordinary = "A platform-site crossing is not water entry or mounting.";
        Assert.Equal(ordinary, BlindIdentity.Scrub(ordinary, secrets));
        Assert.Equal("The probe uses a platform.", BlindIdentity.Scrub("The probe uses a platform.", secrets));
        var embedded = BlindIdentity.Scrub("Held out KO animal A PBS alias KO1 and WT2 group B", secrets);
        foreach (var label in new[] { "KO", "WT", "PBS", "KO1", "WT2" })
            Assert.DoesNotContain(label, embedded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("platform", ordinary, StringComparison.Ordinal);
        Assert.Equal(BlindIdentity.GenericRejection, BlindIdentity.PresentStatus("Error for group KO animal A alias KO1 and PBS", secrets));
        Assert.Equal(BlindIdentity.DetailsHidden, BlindIdentity.PresentSurface("tooltip KO animal A PBS KO1", secrets));
        Assert.Equal(BlindIdentity.ReasonWithheld, BlindIdentity.ProjectAuthored("Held out KO animal A PBS alias KO1", secrets));
        Assert.Equal("Kept out of the endpoint set", BlindIdentity.ProjectAuthored("Kept out of the endpoint set", secrets));
        Assert.Equal(BlindIdentity.ReasonWithheld, BlindIdentity.ProjectAuthored("A received treatment before this trial", secrets));
    }

    [Fact]
    public void BlindedExportOmitsSentinelsPathsAndSourceHashes()
    {
        var asset = SentinelAsset() with { AnimalIdConfirmed = true, BlindedAlias = "Recording AB12CD34", BlindedAnimalAlias = "Animal 89ABCDEF", Included = false, ExclusionReason = "Kept out of the endpoint set" };
        var secrets = BlindSecrets.FromAssets([asset]);
        var export = new BlindedReviewExport
        {
            RecordingAlias = BlindIdentity.RecordingAlias(asset),
            AnimalAlias = BlindIdentity.AnimalAlias(asset),
            Phase = "Probe",
            Day = 1,
            Trial = 2,
            Included = false,
            InclusionReason = BlindIdentity.ProjectAuthored(asset.ExclusionReason, secrets),
            ReviewStale = true,
            RankingVersion = ReviewRankingEvidence.Version,
            Moments = [new(4, 6, BlindIdentity.Scrub("Unknown or ambiguous history before first observed site visit; inspect possible earlier visit and event timing.", secrets), false)],
            PixelNote = BlindIdentity.PixelNote,
            MeasurementNote = TrialMeasurementWording.ProtocolLabelDoesNotSupply
        };
        using var scratch = new Scratch();
        ReviewExportWriter.Save(scratch.Folder, export);
        var json = File.ReadAllText(Path.Combine(scratch.Folder, "blind-review.json"));
        var text = File.ReadAllText(Path.Combine(scratch.Folder, "blind-review.txt"));
        var combined = json + text;
        foreach (var secret in new[] { asset.Path, asset.Filename, asset.AnimalId, asset.GroupName, asset.SourceSha256, "SENTINEL_FILE", "SENTINEL_ANIMAL", "SENTINEL_GROUP" })
            Assert.DoesNotContain(secret, combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(export.RecordingAlias, combined, StringComparison.Ordinal);
        Assert.Contains("not redacted", combined, StringComparison.Ordinal);
        Assert.Contains(TrialMeasurementWording.ProtocolLabelDoesNotSupply, combined, StringComparison.Ordinal);
        Assert.Contains(ReviewRankingEvidence.Version, combined, StringComparison.Ordinal);
        Assert.Contains("4-6", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"path\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("animal_id", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_sha", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("group_name", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ranking_seed", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seed_material", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Seed ", text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(scratch.Folder, "analysis.json")));
    }

    [Fact]
    public void UnblindingRequiresAReasonAndIsAudited()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        Assert.Throws<ArgumentException>(() => store.RecordUnblinding("Reviewer", " "));
        Assert.Throws<ArgumentException>(() => store.RecordUnblinding(" ", "Labels needed"));
        Assert.Empty(store.UnblindingHistory());
        store.RecordUnblinding("Reviewer", "Labels needed for a stated comparison");
        var saved = Assert.Single(new StudyStore(path).UnblindingHistory());
        Assert.Equal("Labels needed for a stated comparison", saved.Reason);
        Assert.Equal("Reviewer", saved.Reviewer);
    }

    [Fact]
    public void ExclusionStaysVisibleWithoutChangingWindowEligibilityOrReview()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var asset = SentinelAsset();
        store.AddOrGet(asset);
        var run = MakeRun(asset, Track(0, 40, 60), Track(1, 42, 60), Track(2, 44, 60));
        store.SaveRun(run);
        store.RecordBiologicalEvent(asset, BiologicalEventKind.WaterEntry, 0, "Release", "Reviewer");
        var protocol = store.SaveProtocolWindow(asset, new() { RequestedExposureSeconds = 1, ReleaseConfirmed = true, ReleaseFrame = 0, WindowConfirmed = true, Reason = "Planned", Reviewer = "Reviewer" });
        store.RecordReview(run, true);
        var metrics = JsonDefaults.Serialize(store.EffectiveRun(run).Metrics);
        Assert.Throws<ArgumentException>(() => store.SaveInclusion(asset, false, " ", "Reviewer"));
        var exclusion = store.SaveInclusion(asset, false, "Kept out of the endpoint set", "Reviewer");
        var reopened = new StudyStore(path);
        var listed = Assert.Single(reopened.Videos());
        Assert.False(listed.Included);
        Assert.Equal("Kept out of the endpoint set", listed.ExclusionReason);
        Assert.Equal(exclusion.Revision, reopened.LatestInclusion(asset.Id)!.Revision);
        Assert.True(reopened.Reviewed(run.Id));
        Assert.False(reopened.ReviewStale(run.Id));
        Assert.Equal(protocol.ReleaseFrame, reopened.LatestProtocol(asset.Id)!.ReleaseFrame);
        Assert.Equal(protocol.RequestedExposureSeconds, reopened.LatestProtocol(asset.Id)!.RequestedExposureSeconds);
        Assert.Equal(metrics, JsonDefaults.Serialize(reopened.EffectiveRun(run).Metrics));
        Assert.Equal(0, listed.Settings.StartSeconds);
        Assert.Null(listed.Settings.EndSeconds);

        reopened.SaveVideo(listed with { Settings = listed.Settings with { Threshold = 40 } });
        Assert.False(reopened.Reviewed(run.Id));
        Assert.True(reopened.ReviewStale(run.Id));
        Assert.False(reopened.Videos().Single().Included);
        Assert.Equal(protocol.ReleaseFrame, reopened.LatestProtocol(asset.Id)!.ReleaseFrame);
    }

    [Fact]
    public void CoverageDoesNotConfirmReview()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var asset = SentinelAsset();
        store.AddOrGet(asset);
        var complete = MakeRun(asset, Track(0, 40, 60), Track(1, 42, 60));
        store.SaveRun(complete);
        Assert.True(complete.Metrics.CoveragePercent > 99);
        Assert.False(store.Reviewed(complete.Id));
        var sparse = MakeRun(asset with { Id = asset.Id }, new TrackPoint(0, 0, null, null, PointStatus.Missing, 0), new TrackPoint(1, .1, null, null, PointStatus.Missing, 0));
        Assert.True(sparse.Metrics.CoveragePercent < 1);
        store.SaveRun(sparse);
        store.RecordReview(sparse, true);
        Assert.True(store.Reviewed(sparse.Id));
        Assert.Null(store.LatestProtocol(asset.Id));
    }

    [Fact]
    public void ReleaseAnchorSaveKeepsTheExplicitFrame()
    {
        Assert.Throws<ArgumentException>(() => ReleaseAnchorChoice.ForSave(true, null));
        Assert.Equal(4, ReleaseAnchorChoice.ForSave(true, 4));
        Assert.Equal(4, ReleaseAnchorChoice.ForSave(false, 4));
        Assert.Null(ReleaseAnchorChoice.ForSave(false, null));

        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var asset = SentinelAsset();
        store.AddOrGet(asset);
        store.RecordBiologicalEvent(asset, BiologicalEventKind.WaterEntry, 4, "Release", "Reviewer");
        var first = store.SaveProtocolWindow(asset, Draft(30, true, ReleaseAnchorChoice.ForSave(true, 4)));
        var second = store.SaveProtocolWindow(asset, Draft(45, true, ReleaseAnchorChoice.ForSave(true, 4)));
        Assert.Equal(4, second.ReleaseFrame);
        Assert.Equal(45, second.RequestedExposureSeconds);
        Assert.Equal(first.ReleaseFrame, new StudyStore(path).LatestProtocol(asset.Id)!.ReleaseFrame);
        var history = store.ProtocolHistory(asset.Id).Count;
        var mismatch = Assert.Throws<ArgumentException>(() => store.SaveProtocolWindow(asset, Draft(45, true, 9)));
        Assert.Contains("water entry", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(history, store.ProtocolHistory(asset.Id).Count);
        Assert.Equal(4, store.LatestProtocol(asset.Id)!.ReleaseFrame);
    }

    [Fact]
    public void RankingEvidenceFollowsTheAcceptedQueueAndIgnoresLabels()
    {
        var settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .4, Ry = .4, MaxJump = 10, Platform = new(.5, .5, .05) };
        AnalysisRun Run(params TrackPoint[] points) => new() { Metadata = new(100, 100, 10, 1000), Points = points.ToList(), Settings = settings };
        TrackPoint Good(int frame, int x = 20) => new(frame, frame / 10d, x, 50, PointStatus.Tracked, 1, Area: 100);
        TrackPoint Unknown(int frame) => new(frame, frame / 10d, null, null, PointStatus.Missing, 0);

        var mixed = Run(Enumerable.Range(100, 80).Select(frame => frame == 110 ? Unknown(frame) : Good(frame, 20 + frame % 7)).ToArray());
        var evidence = ReviewRankingEvidence.Capture(mixed);
        Assert.Equal(MetricCalculator.ReviewQueue(mixed), evidence.Moments);
        Assert.Equal(ReviewRankingEvidence.Version, evidence.VersionName);
        var relabeled = ReviewRankingEvidence.Capture(mixed with { Id = "SENTINEL_GROUP", VideoId = "SENTINEL_FILE.avi", SourceSha256 = "SENTINEL_ANIMAL", CreatedUtc = DateTime.MinValue });
        Assert.Equal(evidence.VersionName, relabeled.VersionName);
        Assert.Equal(evidence.SeedMaterial, relabeled.SeedMaterial);
        Assert.Equal(evidence.Moments, relabeled.Moments);
        Assert.NotEqual(evidence.SeedMaterial, ReviewRankingEvidence.Capture(mixed with { Points = mixed.Points.Select(point => point.Frame == 100 && point.X is not null ? point with { X = point.X + 1 } : point).ToList() }).SeedMaterial);

        var audits = evidence.Moments.Where(moment => moment.RandomAudit).ToArray();
        Assert.NotEmpty(audits);
        Assert.Contains(evidence.Moments, moment => !moment.RandomAudit);
        foreach (var audit in audits)
        {
            var segment = mixed.Points.Where(point => point.Frame >= audit.StartFrame && point.Frame <= audit.EndFrame).ToArray();
            Assert.Equal(audit.EndFrame - audit.StartFrame + 1, segment.Length);
            Assert.All(segment, point => Assert.True(point.Accepted));
        }
        Assert.DoesNotContain(MetricCalculator.ReviewQueue(Run(Good(0), Unknown(1), Good(2, 80))), moment => moment.Reason.Contains("boundary crossing") || moment.Reason.Contains("Accepted position jump"));

        var first = ReviewRankingEvidence.Capture(mixed);
        var other = ReviewRankingEvidence.Capture(Run(Good(0), Good(1), Good(2)));
        var firstAgain = ReviewRankingEvidence.Capture(mixed);
        var otherAgain = ReviewRankingEvidence.Capture(Run(Good(0), Good(1), Good(2)));
        Assert.Equal(first.Moments, firstAgain.Moments);
        Assert.Equal(first.SeedMaterial, firstAgain.SeedMaterial);
        Assert.Equal(other.Moments, otherAgain.Moments);
        Assert.NotEqual(first.Moments, other.Moments);

        Assert.Empty(ReviewRankingEvidence.Capture(new()).Moments);
        var unknown = ReviewRankingEvidence.Capture(Run(Unknown(4), Unknown(5)));
        Assert.DoesNotContain(unknown.Moments, moment => moment.RandomAudit);
        Assert.Equal(MetricCalculator.ReviewQueue(Run(Unknown(4), Unknown(5))), unknown.Moments);
    }

    [Fact]
    public void InspectionsAttestTheDisplayedFrameAndRejectStaleOrForeignRuns()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var asset = store.AddOrGet(SentinelAsset());
        var other = store.AddOrGet(SentinelAsset() with { SourceSha256 = new string('b', 64) });
        var run = MakeRun(asset, Track(0, 40, 60), Track(1, 42, 60), Track(2, 44, 60));
        store.SaveRun(run);
        var protocol = store.SaveProtocolWindow(asset, Draft(5, false, null));
        var moment = new ReviewMoment(0, 2, "Queued range", false);
        var receipt = store.RecordNavigation(asset, moment.StartFrame, run.Id, moment);
        Assert.False(receipt.DisplayCompleted);
        Assert.Equal(moment.StartFrame, receipt.RequestedFrame);
        Assert.Empty(store.Inspections(asset.Id));
        var material = store.InspectionMaterialFor(run.Id);
        Assert.Throws<ArgumentException>(() => store.RecordInspection(asset, Attestation(material, 0, 0, 2, " ")));
        Assert.Throws<ArgumentException>(() => store.RecordInspection(asset, Attestation(material, 0, 0, 2) with { Reviewer = " " }));
        var mismatchedFrame = Assert.Throws<InvalidOperationException>(() => store.RecordInspection(asset, Attestation(material, 1, 0, 2)));
        Assert.Contains("source frame", mismatchedFrame.Message, StringComparison.OrdinalIgnoreCase);
        var inspection = store.RecordInspection(asset, Attestation(material, 0, 0, 2));
        Assert.Equal(0, inspection.StartFrame);
        Assert.Equal(0, inspection.EndFrame);
        Assert.Equal(0, inspection.DisplayedFrame);
        Assert.Equal(2, inspection.QueueEndFrame);
        Assert.NotEqual(inspection.EndFrame, inspection.QueueEndFrame);
        Assert.Equal(protocol.RequestedExposureSeconds, store.LatestProtocol(asset.Id)!.RequestedExposureSeconds);
        Assert.False(store.Reviewed(run.Id));
        Assert.Equal(inspection, store.Inspections(asset.Id).Single());
        Assert.Single(store.CurrentInspections(asset.Id, run.Id));

        var foreign = Assert.Throws<InvalidOperationException>(() => store.RecordInspection(other, Attestation(material, 0, 0, 0)));
        Assert.Contains("does not belong", foreign.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Inspections(other.Id));

        store.RecordReview(run, true);
        var staleReview = Assert.Throws<InvalidOperationException>(() => store.RecordInspection(asset, Attestation(material, 0, 0, 2)));
        Assert.Contains("stale", staleReview.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Review changed", staleReview.Message, StringComparison.Ordinal);
        Assert.Empty(store.CurrentInspections(asset.Id, run.Id));
        Assert.Single(store.Inspections(asset.Id));

        var reviewed = store.InspectionMaterialFor(run.Id);
        store.RecordInspection(asset, Attestation(reviewed, 0, 0, 2, "Reviewed frame"));
        store.SaveCorrection(run, new(run.Id, 1, null, null, "Unknown position", DateTime.UtcNow));
        var staleCorrection = Assert.Throws<InvalidOperationException>(() => store.RecordInspection(asset, Attestation(reviewed, 0, 0, 2, "Reviewed frame")));
        Assert.Contains("stale", staleCorrection.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Corrections changed", staleCorrection.Message, StringComparison.Ordinal);
        Assert.Empty(store.CurrentInspections(asset.Id, run.Id));
        Assert.Equal(2, store.Inspections(asset.Id).Count);

        var replacement = MakeRun(asset, Track(0, 10, 10), Track(1, 12, 10), Track(2, 14, 10));
        store.SaveRun(replacement);
        Assert.Empty(store.CurrentInspections(asset.Id, replacement.Id));
        Assert.Equal(2, store.Inspections(asset.Id).Count);
    }

    [Fact]
    public void RankingHistoryKeepsEarlierMomentsAndExportOmitsTheFingerprint()
    {
        using var scratch = new Scratch();
        var store = new StudyStore(Path.Combine(scratch.Folder, "study.morrisdb"));
        var asset = store.AddOrGet(SentinelAsset());
        var run = MakeRun(asset, Track(0, 40, 60), Track(1, 42, 60), Track(2, 80, 60));
        store.SaveRun(run);
        var evidence = ReviewRankingEvidence.Capture(store.EffectiveRun(run));
        Assert.Throws<ArgumentException>(() => store.SaveRankingSnapshot(run, evidence with { VersionName = "other" }));
        Assert.Throws<ArgumentException>(() => store.SaveRankingSnapshot(run, evidence with { SeedMaterial = new string('a', 64) }));
        Assert.Throws<ArgumentException>(() => store.SaveRankingSnapshot(run, evidence with { Moments = [] }));
        store.SaveRankingSnapshot(run, evidence);
        store.SaveRankingSnapshot(run, evidence);
        var prior = Assert.Single(store.RankingHistory(run.Id));
        Assert.Equal(evidence.SeedMaterial, prior.SeedMaterial);
        Assert.Equal(evidence.Moments, prior.Moments);
        Assert.Equal(evidence.Moments, store.LatestRanking(run.Id)!.Moments);
        store.SaveCorrection(run, new(run.Id, 1, null, null, "Unknown position", DateTime.UtcNow));
        var corrected = ReviewRankingEvidence.Capture(store.EffectiveRun(run));
        Assert.NotEqual(evidence.SeedMaterial, corrected.SeedMaterial);
        store.SaveRankingSnapshot(run, corrected);
        var history = store.RankingHistory(run.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal(prior.Moments, history[0].Moments);
        Assert.Equal(prior.SeedMaterial, history[0].SeedMaterial);
        var latest = store.LatestRanking(run.Id)!;
        Assert.NotEqual(history[0].Revision, latest.Revision);
        Assert.Equal(corrected.Moments, latest.Moments);
        Assert.Equal(corrected.SeedMaterial, latest.SeedMaterial);
        var export = new BlindedReviewExport
        {
            RecordingAlias = BlindIdentity.RecordingAlias(asset),
            AnimalAlias = BlindIdentity.AnimalAlias(asset),
            RankingVersion = latest.Version,
            Moments = latest.Moments.Select(moment => new BlindedMoment(moment.StartFrame, moment.EndFrame, moment.Reason, moment.RandomAudit)).ToList()
        };
        ReviewExportWriter.Save(scratch.Folder, export);
        var combined = File.ReadAllText(Path.Combine(scratch.Folder, "blind-review.json")) + File.ReadAllText(Path.Combine(scratch.Folder, "blind-review.txt"));
        Assert.DoesNotContain(latest.SeedMaterial, combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seed_material", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(latest.Version, combined, StringComparison.Ordinal);
        Assert.Contains(latest.Moments[0].StartFrame.ToString(CultureInfo.InvariantCulture), combined, StringComparison.Ordinal);
        Assert.Contains(latest.Moments[0].EndFrame.ToString(CultureInfo.InvariantCulture), combined, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRankingSnapshotBecomesHistoryWithoutDroppingItsMoments()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Folder, "study.morrisdb");
        var store = new StudyStore(path);
        var asset = store.AddOrGet(SentinelAsset());
        var run = MakeRun(asset, Track(0, 40, 60), Track(1, 42, 60), Track(2, 44, 60));
        store.SaveRun(run);
        var legacy = ReviewRankingEvidence.Capture(run);
        var snapshot = new RankingSnapshot { RunId = run.Id, Version = legacy.VersionName, SeedMaterial = legacy.SeedMaterial, Moments = legacy.Moments.ToList(), CreatedUtc = DateTime.UtcNow };
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ranking_snapshots(run_id,json) VALUES($id,$json)";
            command.Parameters.AddWithValue("$id", run.Id);
            command.Parameters.AddWithValue("$json", JsonDefaults.Serialize(snapshot));
            command.ExecuteNonQuery();
        }
        var reopened = new StudyStore(path);
        var history = reopened.RankingHistory(run.Id);
        var carried = Assert.Single(history);
        Assert.Equal(legacy.Moments, carried.Moments);
        Assert.Equal(legacy.SeedMaterial, carried.SeedMaterial);
        Assert.Equal(1, carried.Revision);
        Assert.NotEqual("legacy-unbound", carried.CorrectionFingerprint);
        Assert.Equal(carried.Moments, reopened.LatestRanking(run.Id)!.Moments);
        reopened.SaveCorrection(run, new(run.Id, 0, null, null, "Unknown position", DateTime.UtcNow));
        var corrected = ReviewRankingEvidence.Capture(reopened.EffectiveRun(run));
        reopened.SaveRankingSnapshot(run, corrected);
        var after = reopened.RankingHistory(run.Id);
        Assert.Equal(2, after.Count);
        Assert.Equal(legacy.Moments, after[0].Moments);
        Assert.Equal(legacy.SeedMaterial, after[0].SeedMaterial);
        Assert.Equal(corrected.Moments, reopened.LatestRanking(run.Id)!.Moments);
    }

    static InspectionAttestation Attestation(InspectionMaterial material, int displayed, int queueStart, int queueEnd, string reason = "Looked at the displayed frame", string reviewer = "Reviewer") => new()
    {
        RunId = material.RunId,
        SourceSha256 = material.SourceSha256,
        CorrectionFingerprint = material.CorrectionFingerprint,
        ReviewFingerprint = material.ReviewFingerprint,
        DisplayedFrame = displayed,
        QueueStartFrame = queueStart,
        QueueEndFrame = queueEnd,
        Reason = reason,
        Reviewer = reviewer
    };

    static VideoAsset SentinelAsset() => new()
    {
        Path = Path.Combine(Path.GetTempPath(), "SENTINEL_FILE_ALPHA.avi"),
        SourceSha256 = new string('a', 64),
        AnimalId = "SENTINEL_ANIMAL_ALPHA",
        GroupName = "SENTINEL_GROUP_ALPHA",
        Phase = TrialMode.Probe,
        Settings = new() { Cx = .5, Cy = .5, Rx = .4, Ry = .4, ArenaConfirmed = true },
        Metadata = new(160, 120, 10, 20)
    };

    static TrackPoint Track(int frame, int x, int y) => new(frame, frame / 10d, x, y, PointStatus.Tracked, 1);

    static AnalysisRun MakeRun(VideoAsset asset, params TrackPoint[] points)
    {
        var list = points.ToList();
        return new()
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

    static ProtocolWindow Draft(double seconds, bool release, int? frame) => new()
    {
        RequestedExposureSeconds = seconds,
        ReleaseConfirmed = release,
        ReleaseFrame = frame,
        WindowConfirmed = true,
        Reason = "Planned window",
        Reviewer = "Reviewer"
    };
}
