using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
using Window = System.Windows.Window;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MouseTrackerTabajara.Validation.Tests;

public class SplitSummaryTests(ITestOutputHelper output)
{
    [Fact]
    public void DeclaredRolesSeparateAccurateDevelopmentFromInaccurateIndependent()
    {
        using var study = new SyntheticStudy();
        var development = study.Add("independent-looking.avi", 8, 20);
        var excluded = study.Add("excluded-development.avi", 8, 50, included: false, exclusion: "Synthetic exclusion kept visible");
        var independent = study.Add("development-looking.avi", 8, 90);
        var unspecified = study.Add("unspecified-role.avi", 8, 140);
        var plan = new IndependentValidationPlan
        {
            SyntheticToolingOnly = true, Seed = 2026, RandomFramesPerRecording = 8, DifficultIntervalsPerRecording = 1, IntervalFrames = 4, SecondReviewFraction = 0,
            Tolerances = new(5, 3, .25, .1, .05, .8),
            Recordings =
            [
                new(development.Id, "session-development", ValidationSplit.Development, true, false, "probe-protocol", "synthetic-cohort"),
                new(excluded.Id, "session-development", ValidationSplit.Development, true, false, "probe-protocol", "synthetic-cohort"),
                new(independent.Id, "session-independent", ValidationSplit.IndependentValidation, false, true, "probe-protocol", "synthetic-cohort"),
                new(unspecified.Id, "session-unspecified", ValidationSplit.Unspecified, false, false, "probe-protocol", "synthetic-cohort")
            ]
        };
        var rawBefore = study.Runs.ToDictionary(run => run.VideoId, run => JsonDefaults.Serialize(run));
        var samplingBefore = JsonDefaults.Serialize(IndependentValidationSampling.Generate(IndependentValidationSnapshot.Read(study.Store.DatabasePath), plan));
        var saved = study.SavePlanAndSampling(plan);
        Assert.Equal(samplingBefore, JsonDefaults.Serialize(saved.Sampling));
        var hash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(saved.Sampling));
        study.AnnotatePattern(saved.History, hash, development, 40);
        study.AnnotatePattern(saved.History, hash, excluded, 40);
        study.AnnotatePattern(saved.History, hash, independent, 70);
        study.AnnotatePattern(saved.History, hash, unspecified, 55);
        Assert.Equal(hash + ":" + independent.Id + ":0:First", IndependentValidationComparison.ReferenceKey(hash, new(independent.Id, 0, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 70, 50, ReferenceEvent.None, "Synthetic reference", [])));
        var snapshot = IndependentValidationSnapshot.Read(study.Store.DatabasePath);
        var report = IndependentValidationComparison.Compare(snapshot, plan, saved.Sampling);
        var developmentSummary = Role(report, ValidationSplit.Development);
        var independentSummary = Role(report, ValidationSplit.IndependentValidation);
        var unspecifiedSummary = Role(report, ValidationSplit.Unspecified);
        Assert.Equal(0, developmentSummary.MeanRandomPositionErrorPixels);
        Assert.Equal(30, independentSummary.MeanRandomPositionErrorPixels);
        Assert.Equal(15, unspecifiedSummary.MeanRandomPositionErrorPixels);
        Assert.Equal(11.25, report.MeanRandomPositionErrorPixels);
        Assert.Equal(30, report.MaximumPositionErrorPixels);
        Assert.NotEqual(report.MeanRandomPositionErrorPixels, independentSummary.MeanRandomPositionErrorPixels);
        Assert.Equal(2, developmentSummary.RecordingCount);
        Assert.Equal(1, developmentSummary.ExcludedRecordings);
        Assert.Equal(8, developmentSummary.ExcludedSampledFrames);
        Assert.Equal(0, independentSummary.ExcludedSampledFrames);
        Assert.Equal(16, developmentSummary.FullSourceFrames);
        Assert.Equal(16, developmentSummary.SampledFrames);
        Assert.Equal(14, developmentSummary.ReferenceRecordedFrames);
        Assert.Equal(8, developmentSummary.PositionJoinedFrames);
        Assert.Equal(8, developmentSummary.RandomPositionJoinedFrames);
        Assert.Equal(2, developmentSummary.MissingReferenceFrames);
        Assert.Equal(8, developmentSummary.NotJoinedFrames);
        Assert.Equal(0, developmentSummary.ToleranceFailureFrames);
        Assert.Equal(2, developmentSummary.FalseAcceptedFrames);
        Assert.Equal(2, developmentSummary.AmbiguousFrames);
        Assert.Equal(2, developmentSummary.UnresolvedDisagreementFrames);
        Assert.Equal(2, developmentSummary.NotVisibleReferenceFrames);
        Assert.Equal(0, developmentSummary.MaximumPositionErrorPixels);
        Assert.Equal(2, developmentSummary.RequestedPurposiveIntervals);
        Assert.Equal(2, developmentSummary.RealizedPurposiveIntervals);
        Assert.False(developmentSummary.SourceTooShortForDistinctIntervals);
        Assert.Contains("Requested 2; realized 2", developmentSummary.PurposiveIntervalNote);
        Assert.DoesNotContain("too short", developmentSummary.PurposiveIntervalNote);
        Assert.Equal((1, 8, 8, 7, 4, 4, 1, 4, 4, 1, 0, 1, 1, 1, 30d, 30d, 1, 1), (
            independentSummary.RecordingCount, independentSummary.FullSourceFrames, independentSummary.SampledFrames, independentSummary.ReferenceRecordedFrames,
            independentSummary.PositionJoinedFrames, independentSummary.RandomPositionJoinedFrames, independentSummary.MissingReferenceFrames, independentSummary.NotJoinedFrames,
            independentSummary.ToleranceFailureFrames, independentSummary.FalseAcceptedFrames, independentSummary.ExcludedRecordings, independentSummary.AmbiguousFrames,
            independentSummary.UnresolvedDisagreementFrames, independentSummary.NotVisibleReferenceFrames, independentSummary.MeanRandomPositionErrorPixels, independentSummary.MaximumPositionErrorPixels,
            independentSummary.RequestedPurposiveIntervals, independentSummary.RealizedPurposiveIntervals));
        Assert.Equal(15, unspecifiedSummary.MeanRandomPositionErrorPixels);
        Assert.Equal(4, unspecifiedSummary.ToleranceFailureFrames);
        Assert.Equal(32, report.FullSourcePopulation);
        Assert.Equal(32, report.SampledUniqueFrames);
        Assert.Equal(16, report.PositionJoinedFrames);
        Assert.Equal(report.FullSourcePopulation, report.SplitSummaries.Sum(s => s.FullSourceFrames));
        Assert.Equal(report.SampledUniqueFrames, report.SplitSummaries.Sum(s => s.SampledFrames));
        Assert.Equal(report.Frames.Count(f => f.RandomSample && f.PositionErrorPixels.HasValue), report.SplitSummaries.Sum(s => s.RandomPositionJoinedFrames));
        Assert.Contains(report.Frames, f => f.VideoId == excluded.Id && f.Excluded && f.PositionErrorPixels == 0);
        Assert.Contains(report.Frames, f => f.VideoId == independent.Id && f.Frame == 1 && f.ReferenceIds.IsEmpty && f.PositionErrorPixels is null);
        Assert.Contains(report.Frames, f => f.VideoId == independent.Id && f.Frame == 2 && f.Disagreement && f.PositionErrorPixels is null && f.ReferenceIds.Length == 2);
        Assert.Contains(report.Frames, f => f.VideoId == independent.Id && f.Frame == 3 && f.FalseAccepted == true && f.Visibility == ReferenceVisibility.NotVisible);
        Assert.Contains(report.Frames, f => f.VideoId == independent.Id && f.Frame == 4 && f.Visibility == ReferenceVisibility.Ambiguous && f.PositionErrorPixels is null);
        Assert.Equal(snapshot.ProducerFingerprint, report.ProducerFingerprint);
        Assert.Equal(snapshot.ReferenceFingerprint, report.ReferenceFingerprint);
        Assert.Equal(saved.Sampling.PlanFingerprint, report.PlanFingerprint);
        Assert.Equal(saved.Sampling.Runs[0].ComparatorBinarySha256, report.ComparatorBinarySha256);
        Assert.Equal(ValidationMeasurementTarget.RawAutomatic, report.MeasurementTarget);
        Assert.Contains("not held-out accuracy", report.PooledScopeLabel);
        Assert.Contains("not animal N", report.PooledScopeLabel);
        Assert.Contains("including marked exclusions", report.PooledScopeLabel);
        Assert.Contains("not animal N", independentSummary.PositionDenominator);
        Assert.Contains("not held-out accuracy", independentSummary.PositionDenominator);
        Assert.Contains("development-looking", independent.Filename);
        Assert.Contains("independent-looking", development.Filename);
        Assert.Equal(samplingBefore, JsonDefaults.Serialize(saved.Sampling));
        foreach (var run in study.Runs) Assert.Equal(rawBefore[run.VideoId], JsonDefaults.Serialize(study.Store.LatestRun(run.VideoId)));
        Assert.All(study.Runs, run => Assert.Empty(study.Store.Corrections(run.Id)));
        var legacyNode = JsonNode.Parse(JsonDefaults.Serialize(report))!.AsObject();
        Assert.True(legacyNode.ContainsKey("split_summaries"));
        legacyNode.Remove("split_summaries");
        legacyNode["mean_random_position_error_pixels"] = 999;
        var legacy = JsonDefaults.Deserialize<ValidationComparisonReport>(legacyNode.ToJsonString());
        Assert.False(ValidationSplitSummaries.TryRead(legacy, out _, out var hold));
        Assert.Equal(ValidationSplitSummaries.AbsentSummaryHold, hold);
        var legacyLines = ValidationSplitSummaryView.Lines(legacy);
        Assert.Contains(legacyLines, line => line.Contains("999", StringComparison.Ordinal));
        Assert.DoesNotContain(legacyLines, line => line.Contains("Declared role", StringComparison.Ordinal));
        Assert.Contains(legacyLines, line => line.Contains("not inferred from the pooled mean", StringComparison.Ordinal));
        Assert.False(ValidationSplitSummaries.TryRead(report with { SplitSummaries = [developmentSummary] }, out _, out _));
        output.WriteLine("PASS development mean 0 px (8 random joins, 1 excluded recording retained), independent mean 30 px (4 joins), unspecified mean 15 px (4 joins), pooled mean 11.25 px over 16 joins. Legacy JSON without split_summaries held a tampered pooled 999 and did not invent role means.");
    }

    [Fact]
    public void EmptyRolesStayExplicitAndDoNotCopyAnotherMean()
    {
        using var study = new SyntheticStudy();
        var development = study.Add("only-development.avi", 4, 30);
        var plan = new IndependentValidationPlan
        {
            SyntheticToolingOnly = true, RandomFramesPerRecording = 4, DifficultIntervalsPerRecording = 0, IntervalFrames = 2, SecondReviewFraction = 0, Tolerances = new(5, 3, .25, .1, .05, .8),
            Recordings = [new(development.Id, "session-development", ValidationSplit.Development, true, false, "probe-protocol", "synthetic-cohort")]
        };
        var saved = study.SavePlanAndSampling(plan);
        var hash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(saved.Sampling));
        study.SaveReference(saved.History, hash, development, 0, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 70);
        var report = IndependentValidationComparison.Compare(IndependentValidationSnapshot.Read(study.Store.DatabasePath), plan, saved.Sampling);
        Assert.Equal(30, Role(report, ValidationSplit.Development).MeanRandomPositionErrorPixels);
        foreach (var role in new[] { ValidationSplit.IndependentValidation, ValidationSplit.Unspecified })
        {
            var summary = Role(report, role);
            Assert.Equal(0, summary.RecordingCount);
            Assert.Equal(0, summary.FullSourceFrames);
            Assert.Equal(0, summary.SampledFrames);
            Assert.Equal(0, summary.ReferenceRecordedFrames);
            Assert.Equal(0, summary.PositionJoinedFrames);
            Assert.Equal(0, summary.RandomPositionJoinedFrames);
            Assert.Equal(0, summary.MissingReferenceFrames);
            Assert.Equal(0, summary.NotJoinedFrames);
            Assert.Equal(0, summary.ToleranceFailureFrames);
            Assert.Equal(0, summary.FalseAcceptedFrames);
            Assert.Equal(0, summary.ExcludedRecordings);
            Assert.Equal(0, summary.ExcludedSampledFrames);
            Assert.Equal(0, summary.RequestedPurposiveIntervals);
            Assert.Equal(0, summary.RealizedPurposiveIntervals);
            Assert.False(summary.SourceTooShortForDistinctIntervals);
            Assert.Null(summary.MeanRandomPositionErrorPixels);
            Assert.Null(summary.MaximumPositionErrorPixels);
            Assert.Contains("No recordings are declared in this role", summary.PurposiveIntervalNote);
            Assert.Contains("no interval result is fabricated", summary.PurposiveIntervalNote);
            Assert.DoesNotContain("30", ValidationSplitSummaryView.Format(summary));
        }
        Assert.Equal(3, report.SplitSummaries.Length);
        output.WriteLine("PASS empty Independent validation and Unspecified summaries stay present with null means; development mean 30 px was not copied into them.");
    }

    [Fact]
    public void ShortSourceDisclosesRequestedVersusRealizedIntervalsWithoutChangingSamplingJson()
    {
        using var study = new SyntheticStudy();
        var development = study.Add("short-source.avi", 5, 15);
        var plan = new IndependentValidationPlan
        {
            SyntheticToolingOnly = true, RandomFramesPerRecording = 5, DifficultIntervalsPerRecording = 3, IntervalFrames = 8, SecondReviewFraction = 0, Tolerances = new(5, 3, .25, .1, .05, .8),
            Recordings = [new(development.Id, "session-development", ValidationSplit.Development, true, false, "probe-protocol", "synthetic-cohort")]
        };
        var first = IndependentValidationSampling.Generate(IndependentValidationSnapshot.Read(study.Store.DatabasePath), plan);
        var second = IndependentValidationSampling.Generate(IndependentValidationSnapshot.Read(study.Store.DatabasePath), plan);
        var samplingJson = JsonDefaults.Serialize(first);
        Assert.Equal(samplingJson, JsonDefaults.Serialize(second));
        var root = JsonNode.Parse(samplingJson)!.AsObject();
        Assert.Equal(["cases", "plan_fingerprint", "producer_fingerprint", "runs", "seed"], root.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        foreach (var item in root["cases"]!.AsArray())
            Assert.Equal(["end_frame", "inclusion_probability", "population_frames", "random_sample", "second_review_required", "selection_reason", "start_frame", "video_id"], item!.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("split_summaries", samplingJson, StringComparison.Ordinal);
        var saved = study.SavePlanAndSampling(plan);
        Assert.Equal(samplingJson, JsonDefaults.Serialize(saved.Sampling));
        var report = IndependentValidationComparison.Compare(IndependentValidationSnapshot.Read(study.Store.DatabasePath), plan, saved.Sampling);
        var summary = Role(report, ValidationSplit.Development);
        Assert.Single(saved.Sampling.Cases.Where(c => !c.RandomSample).Select(c => (c.StartFrame, c.EndFrame)).Distinct());
        Assert.Equal(3, summary.RequestedPurposiveIntervals);
        Assert.Equal(1, summary.RealizedPurposiveIntervals);
        Assert.True(summary.SourceTooShortForDistinctIntervals);
        Assert.Contains("The source is too short to realize every requested distinct purposive interval", summary.PurposiveIntervalNote);
        Assert.Contains("Requested 3; realized 1", summary.PurposiveIntervalNote);
        Assert.Contains("clipped to that source", summary.PurposiveIntervalNote);
        Assert.Contains("stored sampling JSON are unchanged", summary.PurposiveIntervalNote);
        foreach (var role in new[] { ValidationSplit.IndependentValidation, ValidationSplit.Unspecified })
        {
            var empty = Role(report, role);
            Assert.False(empty.SourceTooShortForDistinctIntervals);
            Assert.Equal(0, empty.RequestedPurposiveIntervals);
            Assert.Equal(0, empty.RealizedPurposiveIntervals);
            Assert.Null(empty.MeanRandomPositionErrorPixels);
        }
        Assert.Equal(samplingJson, JsonDefaults.Serialize(saved.Sampling));
        output.WriteLine("PASS short 5-frame source requested 3 purposive intervals, realized 1, and reported the clip. Sampling JSON keys and bytes stayed unchanged.");
    }

    [Fact]
    public void RawAutomaticAndInspectedCorrectedSummariesStaySeparate()
    {
        using var study = new SyntheticStudy();
        var independent = study.Add("corrected-independent.avi", 4, 70);
        var run = study.Runs.Single();
        var rawBefore = JsonDefaults.Serialize(study.Store.LatestRun(independent.Id));
        study.Store.SaveCorrection(run, new(run.Id, 0, 70, 50, "Synthetic manual repair", DateTime.UtcNow));
        IndependentValidationPlan PlanFor(ValidationMeasurementTarget target) => new()
        {
            Id = "target-" + target, SyntheticToolingOnly = true, MeasurementTarget = target, RandomFramesPerRecording = 4, DifficultIntervalsPerRecording = 0, IntervalFrames = 2, SecondReviewFraction = 0, Tolerances = new(5, 3, .25, .1, .05, .8),
            Recordings = [new(independent.Id, "session-independent", ValidationSplit.IndependentValidation, false, true, "probe-protocol", "synthetic-cohort")]
        };
        var rawPlan = PlanFor(ValidationMeasurementTarget.RawAutomatic);
        var correctedPlan = PlanFor(ValidationMeasurementTarget.InspectedCorrected);
        var snapshot = IndependentValidationSnapshot.Read(study.Store.DatabasePath);
        var rawSampling = IndependentValidationSampling.Generate(snapshot, rawPlan);
        var correctedSampling = IndependentValidationSampling.Generate(snapshot, correctedPlan);
        var history = new ValidationHistoryStore(study.Store.DatabasePath);
        history.Save("plan", rawPlan.Id, JsonDefaults.Serialize(rawPlan), "Reviewer", "Prespecified raw target", 0);
        history.Save("plan", correctedPlan.Id, JsonDefaults.Serialize(correctedPlan), "Reviewer", "Prespecified corrected target", 0);
        history.Save("sampling", rawPlan.Id, JsonDefaults.Serialize(rawSampling), "Reviewer", "Frozen raw sample", 0);
        history.Save("sampling", correctedPlan.Id, JsonDefaults.Serialize(correctedSampling), "Reviewer", "Frozen corrected sample", 0);
        study.SaveReference(history, IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(rawSampling)), independent, 0, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 70);
        study.SaveReference(history, IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(correctedSampling)), independent, 0, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 70);
        snapshot = IndependentValidationSnapshot.Read(study.Store.DatabasePath);
        var raw = IndependentValidationComparison.Compare(snapshot, rawPlan, rawSampling);
        var corrected = IndependentValidationComparison.Compare(snapshot, correctedPlan, correctedSampling);
        Assert.Equal(30, raw.Frames.Single(f => f.Frame == 0).PositionErrorPixels);
        Assert.Equal(0, corrected.Frames.Single(f => f.Frame == 0).PositionErrorPixels);
        Assert.Equal(30, Role(raw, ValidationSplit.IndependentValidation).MeanRandomPositionErrorPixels);
        Assert.Equal(0, Role(corrected, ValidationSplit.IndependentValidation).MeanRandomPositionErrorPixels);
        Assert.Null(Role(raw, ValidationSplit.Development).MeanRandomPositionErrorPixels);
        Assert.Null(Role(corrected, ValidationSplit.Unspecified).MeanRandomPositionErrorPixels);
        Assert.Equal(ValidationMeasurementTarget.RawAutomatic, raw.MeasurementTarget);
        Assert.Equal(ValidationMeasurementTarget.InspectedCorrected, corrected.MeasurementTarget);
        Assert.Equal(snapshot.ProducerFingerprint, raw.ProducerFingerprint);
        Assert.Equal(snapshot.ProducerFingerprint, corrected.ProducerFingerprint);
        Assert.Equal(snapshot.ReferenceFingerprint, raw.ReferenceFingerprint);
        Assert.Equal(snapshot.ReferenceFingerprint, corrected.ReferenceFingerprint);
        Assert.NotEqual(raw.PlanFingerprint, corrected.PlanFingerprint);
        Assert.Equal(rawBefore, JsonDefaults.Serialize(study.Store.LatestRun(independent.Id)));
        Assert.Equal(40, study.Store.LatestRun(independent.Id)!.Points.Single(p => p.Frame == 0).X);
        Assert.Single(study.Store.Corrections(run.Id));
        output.WriteLine("PASS raw-automatic independent mean 30 px and inspected-corrected mean 0 px stayed on separate reports. Raw stored point remained 40. Producer and reference fingerprints matched the snapshot on both targets.");
    }

    [Fact]
    public void NativeWindowSavesReplaysBlindsAndRendersMinimumSummary()
    {
        var lines = new List<string>();
        Exception? error = null;
        var thread = new Thread(() => { try { Native(lines); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)));
        foreach (var line in lines) output.WriteLine(line);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Native(List<string> evidence)
    {
        using var study = new SyntheticStudy();
        var development = study.Add("PRIVATE-independent-looking.avi", 8, 20, animal: "ANIMAL-SECRET", group: "GROUP-SECRET");
        var excluded = study.Add("PRIVATE-excluded.avi", 8, 50, included: false, exclusion: "EXCLUSION-SECRET", animal: "ANIMAL-SECRET", group: "GROUP-SECRET");
        var independent = study.Add("PRIVATE-development-looking.avi", 8, 90, animal: "ANIMAL-SECRET", group: "GROUP-SECRET");
        var unspecified = study.Add("PRIVATE-unspecified.avi", 8, 140, animal: "ANIMAL-SECRET", group: "GROUP-SECRET");
        var visibleX = new Dictionary<string, int>(StringComparer.Ordinal) { [development.Id] = 40, [excluded.Id] = 40, [independent.Id] = 70, [unspecified.Id] = 55 };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var window = new IndependentValidationWindow(study.Store.DatabasePath, false);
        try
        {
            Layout(window, 1050, 700);
            foreach (var draft in window.Draft)
            {
                var independentRole = draft.VideoId == independent.Id;
                draft.Session = draft.VideoId == independent.Id ? "SESSION-SECRET-INDEPENDENT" : draft.VideoId == unspecified.Id ? "SESSION-SECRET-UNSPECIFIED" : "SESSION-SECRET";
                draft.ProtocolIdentity = "PROTOCOL-SECRET";
                draft.Allocation = "ALLOC-SECRET";
                draft.Split = draft.VideoId == independent.Id ? ValidationSplit.IndependentValidation : draft.VideoId == unspecified.Id ? ValidationSplit.Unspecified : ValidationSplit.Development;
                draft.UsedForTuning = !independentRole && draft.VideoId != unspecified.Id;
                draft.IndependenceConfirmed = independentRole;
            }
            window.RecordingsInput.Items.Refresh();
            window.SyntheticInput.IsChecked = true;
            window.ReviewerInput.Text = "First reviewer";
            window.ReasonInput.Text = "Prespecified synthetic split summaries";
            foreach (var box in TextBlocksAndBoxes(window).OfType<TextBox>())
            {
                if (box.Tag is not string key) continue;
                box.Text = key switch
                {
                    "Random frames" => "8",
                    "Difficult intervals" => "1",
                    "Interval frames" => "4",
                    "Second review fraction" => "0",
                    "Position tolerance px" => "5",
                    "Reviewer disagreement px" => "3",
                    _ => box.Text
                };
            }
            Click(window, "Save split and tolerance plan");
            Assert.True(window.Snapshot.Plan is not null, window.StatusText);
            Click(window, "Generate disclosed frozen samples");
            Assert.True(window.Snapshot.Sampling is not null, window.StatusText);
            var sampling = window.Snapshot.Sampling;
            var hash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(sampling));
            var history = new ValidationHistoryStore(study.Store.DatabasePath);
            foreach (var asset in new[] { development, excluded, independent, unspecified }) study.AnnotatePattern(history, hash, asset, visibleX[asset.Id]);
            var tabs = Find<TabControl>(window).Single();
            tabs.SelectedIndex = 2;
            Layout(window, 1050, 700);
            Click(window, "Compare frozen reference and producer");
            Pump(window.PendingComparison);
            Assert.True(window.Report is not null, window.StatusText);
            var report = window.Report;
            Assert.Equal(11.25, report.MeanRandomPositionErrorPixels);
            Assert.Equal(30, Role(report, ValidationSplit.IndependentValidation).MeanRandomPositionErrorPixels);
            Assert.Equal(0, Role(report, ValidationSplit.Development).MeanRandomPositionErrorPixels);
            Assert.Equal(15, Role(report, ValidationSplit.Unspecified).MeanRandomPositionErrorPixels);
            var unblinded = string.Join("\n", Texts(window));
            Assert.Contains("PRIVATE", unblinded, StringComparison.Ordinal);
            Assert.Contains("ALLOC-SECRET", unblinded, StringComparison.Ordinal);
            Assert.Contains("EXCLUSION-SECRET", unblinded, StringComparison.Ordinal);
            Assert.Contains("Declared role Independent validation", unblinded, StringComparison.Ordinal);
            Click(window, "Save replayable comparison report");
            var savedJson = new ValidationHistoryStore(study.Store.DatabasePath).History().Last(h => h.Kind == "report").Json;
            Assert.Contains("\"split_summaries\"", savedJson, StringComparison.Ordinal);
            Assert.Contains("independent_validation", savedJson, StringComparison.Ordinal);
            window.Blinded = true;
            Layout(window, 1050, 700);
            var blinded = string.Join("\n", Texts(window));
            foreach (var secret in new[] { "PRIVATE", "ANIMAL-SECRET", "GROUP-SECRET", "SESSION-SECRET", "ALLOC-SECRET", "PROTOCOL-SECRET", "EXCLUSION-SECRET", "Independent first reference is missing", "Reference visibility/position is ambiguous" })
                Assert.DoesNotContain(secret, blinded, StringComparison.Ordinal);
            Assert.Contains("Declared role Development", blinded, StringComparison.Ordinal);
            Assert.Contains("Declared role Independent validation", blinded, StringComparison.Ordinal);
            Assert.Contains("Declared role Unspecified", blinded, StringComparison.Ordinal);
            Assert.Contains("not held-out accuracy", blinded, StringComparison.Ordinal);
            Assert.Contains("not animal N", blinded, StringComparison.Ordinal);
            Assert.Contains("30 px", blinded, StringComparison.Ordinal);
            Assert.Contains("11.25 px", blinded, StringComparison.Ordinal);
            Assert.Equal(1050, window.MinWidth);
            Assert.Equal(700, window.MinHeight);
            var root = (FrameworkElement)window.Content;
            Assert.Equal(window.MinWidth - root.Margin.Left - root.Margin.Right, root.ActualWidth);
            Assert.Equal(window.MinHeight - root.Margin.Top - root.Margin.Bottom, root.ActualHeight);
            var bitmap = RenderSynthetic(window, 1050, 700);
            Assert.Equal(1050, bitmap.PixelWidth);
            Assert.Equal(700, bitmap.PixelHeight);
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            Assert.Contains(pixels, pixel => pixel != 0);
            window.ReviewerInput.Text = "Reviewer";
            window.ReasonInput.Text = "Reasoned reveal after blinded split summary";
            window.Blinded = false;
            Assert.False(window.Blinded);
            var legacyNode = JsonNode.Parse(savedJson)!.AsObject();
            legacyNode.Remove("split_summaries");
            legacyNode["mean_random_position_error_pixels"] = 999;
            new ValidationHistoryStore(study.Store.DatabasePath).Save("report", Guid.NewGuid().ToString("N"), legacyNode.ToJsonString(), "Reviewer", "Legacy comparison without role summaries", 0);
            tabs.SelectedIndex = 2;
            Layout(window, 1050, 700);
            Click(window, "Replay last saved comparison");
            Pump(window.PendingComparison);
            Assert.True(window.Report is not null, window.StatusText);
            Assert.Equal(11.25, window.Report.MeanRandomPositionErrorPixels);
            Assert.Equal(30, Role(window.Report, ValidationSplit.IndependentValidation).MeanRandomPositionErrorPixels);
            Assert.Equal(0, Role(window.Report, ValidationSplit.Development).MeanRandomPositionErrorPixels);
            var replayed = string.Join("\n", Texts(window));
            Assert.Contains(ValidationSplitSummaryView.RecomputedAbsentSummaries, replayed, StringComparison.Ordinal);
            Assert.DoesNotContain("999", replayed, StringComparison.Ordinal);
            Assert.Contains("Declared role Independent validation", replayed, StringComparison.Ordinal);
            evidence.Add($"PASS native WPF split summaries: development mean 0 px, independent mean 30 px, unspecified mean 15 px, pooled mean 11.25 px. Saved JSON contained split_summaries. Blinded minimum window {window.MinWidth}x{window.MinHeight} rendered {bitmap.PixelWidth}x{bitmap.PixelHeight} and hid PRIVATE/ANIMAL-SECRET/GROUP-SECRET/SESSION-SECRET/ALLOC-SECRET/PROTOCOL-SECRET/EXCLUSION-SECRET and reference diagnostic strings while keeping named roles. Legacy replay recomputed 11.25 px and did not publish tampered pooled 999.");
        }
        finally { window.Close(); }
    }

    private static ValidationSplitSummary Role(ValidationComparisonReport report, ValidationSplit role)
    {
        Assert.True(ValidationSplitSummaries.TryRead(report, out var summaries, out var hold), hold);
        return summaries.Single(s => s.Role == role);
    }

    private static BitmapSource RenderSynthetic(Window window, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var parameters = new HwndSourceParameters("synthetic-split-summary") { WindowStyle = 0, ExtendedWindowStyle = 0x08000000, PositionX = -32000, PositionY = -32000, Width = width, Height = height };
        using var hwnd = new HwndSource(parameters) { RootVisual = content };
        content.Measure(new System.Windows.Size(width, height));
        content.Arrange(new System.Windows.Rect(0, 0, width, height));
        content.UpdateLayout();
        Pump(Task.Delay(30));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        bitmap.Freeze();
        hwnd.RootVisual = null;
        window.Content = content;
        return bitmap;
    }
    private static void Click(Window window, string caption) => Find<Button>(window).Single(b => Equals(b.Content, caption)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<string> Texts(Window window) => TextBlocksAndBoxes(window).OfType<TextBlock>().Select(block => block.Text);
    private static IEnumerable<DependencyObject> TextBlocksAndBoxes(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (var nested in TextBlocksAndBoxes(node)) yield return nested;
        }
    }
    private static IEnumerable<T> Find<T>(Window window) where T : DependencyObject => TextBlocksAndBoxes(window).OfType<T>();
    private static void Layout(Window window, double width, double height) { var root = (FrameworkElement)window.Content; root.Measure(new System.Windows.Size(width, height)); root.Arrange(new System.Windows.Rect(0, 0, width, height)); root.UpdateLayout(); Pump(Task.Delay(30)); root.UpdateLayout(); }
    private static void Pump(Task task) { var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false)); timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Assert.True(task.IsCompleted); task.GetAwaiter().GetResult(); }

    private sealed class SyntheticStudy : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "split-summary-" + Guid.NewGuid().ToString("N"));
        public StudyStore Store { get; }
        public List<VideoAsset> Assets { get; } = [];
        public List<AnalysisRun> Runs { get; } = [];
        public SyntheticStudy() { Directory.CreateDirectory(Folder); Store = new StudyStore(Path.Combine(Folder, "study.morrisdb")); }
        public VideoAsset Add(string fileName, int frames, byte blue, bool included = true, string exclusion = "", string animal = "synthetic", string group = "synthetic-group")
        {
            var path = Path.Combine(Folder, fileName);
            using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new OpenCvSharp.Size(100, 100)))
            {
                Assert.True(writer.IsOpened());
                for (var frame = 0; frame < frames; frame++)
                {
                    using var image = new Mat(100, 100, MatType.CV_8UC3, new Scalar(blue, 30 + frame, 20));
                    Cv2.Circle(image, new OpenCvSharp.Point(40, 50), 4, Scalar.Black, -1);
                    writer.Write(image);
                }
            }
            var imported = new WorkspaceService(Store).Import(path);
            var asset = imported with { AnimalId = animal, AnimalIdConfirmed = false, GroupName = group, Included = included, ExclusionReason = exclusion, Settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .4, Ry = .4, Platform = new(.5, .5, .04), ArenaConfirmed = true } };
            Store.SaveVideo(asset);
            Assert.Equal(frames, asset.Metadata.FrameCount);
            var points = Enumerable.Range(0, frames).Select(frame => new TrackPoint(frame, frame / asset.Metadata.Fps, 40, 50, PointStatus.Tracked, 1)).ToList();
            var run = new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = asset.Settings, Points = points, Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings), AlgorithmContract = AlgorithmIdentity.Contract, NominalTimingVerified = true };
            Store.SaveRun(run);
            Assets.Add(asset); Runs.Add(run); return asset;
        }
        public (ValidationSampling Sampling, ValidationHistoryStore History) SavePlanAndSampling(IndependentValidationPlan plan)
        {
            var history = new ValidationHistoryStore(Store.DatabasePath);
            var snapshot = IndependentValidationSnapshot.Read(Store.DatabasePath);
            history.Save("plan", plan.Id, JsonDefaults.Serialize(plan), "Reviewer", "Prespecified source split/tolerances", 0);
            var sampling = IndependentValidationSampling.Generate(snapshot, plan);
            history.Save("sampling", plan.Id, JsonDefaults.Serialize(sampling), "Reviewer", "Disclosed random/purposive sample", 0);
            return (sampling, history);
        }
        public void AnnotatePattern(ValidationHistoryStore history, string hash, VideoAsset asset, int visibleX)
        {
            for (var frame = 0; frame < asset.Metadata.FrameCount; frame++)
            {
                if (frame == 1) continue;
                if (frame == 2)
                {
                    SaveReference(history, hash, asset, frame, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, visibleX);
                    SaveReference(history, hash, asset, frame, ReferenceReviewerLane.Second, "Second reviewer", ReferenceVisibility.Visible, visibleX + 20);
                    continue;
                }
                if (frame == 3) { SaveReference(history, hash, asset, frame, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.NotVisible, null); continue; }
                if (frame == 4) { SaveReference(history, hash, asset, frame, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Ambiguous, null); continue; }
                SaveReference(history, hash, asset, frame, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, visibleX);
            }
        }
        public void SaveReference(ValidationHistoryStore history, string hash, VideoAsset asset, int frame, ReferenceReviewerLane lane, string reviewer, ReferenceVisibility visibility, int? x)
        {
            var reference = new IndependentReference(asset.Id, frame, lane, reviewer, visibility, visibility == ReferenceVisibility.Visible ? x : null, visibility == ReferenceVisibility.Visible ? 50 : null, ReferenceEvent.None, "Synthetic reference", []);
            reference.Validate(asset.Metadata);
            history.Save("reference", IndependentValidationComparison.ReferenceKey(hash, reference), JsonDefaults.Serialize(reference), reviewer, reference.Reason, 0);
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Folder, true); }
    }
}
