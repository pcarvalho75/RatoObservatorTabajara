using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;

namespace MouseTrackerTabajara.Atlas.Tests;

internal static class ExperimentNativeProof
{
    public static void Run(StudyStore store, List<string> evidence)
    {
        var assets = store.Videos().ToArray(); var runs = new List<AnalysisRun>();
        for (var i = 0; i < assets.Length; i++)
        {
            var animalIndex = i < 28 ? i / 2 : i - 14;
            var asset = assets[i] with { AnimalId = "PRIVATE-exp-animal-" + animalIndex, AnimalIdConfirmed = true, GroupName = animalIndex < 7 ? "PRIVATE-A" : "PRIVATE-B",
                Trial = i < 28 ? i % 2 + 1 : 1, Day = 1, Phase = TrialMode.Probe, Included = true, ExclusionReason = "" };
            store.SaveVideo(asset); assets[i] = asset;
            var points = Enumerable.Range(5, 30).Select(f => f is 10 or 11 ? new TrackPoint(f, f / 10d, null, null, PointStatus.Missing, 0)
                : new TrackPoint(f, f / 10d, i == 19 && f == 22 ? 265 : 100 + f, 100, PointStatus.Tracked, 1)).ToList();
            var run = new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = asset.Settings,
                Points = points, Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings), AlgorithmContract = AlgorithmIdentity.Contract, NominalTimingVerified = true };
            store.SaveRun(run); runs.Add(run);
            store.RecordBiologicalEvent(asset, BiologicalEventKind.WaterEntry, 5, "Synthetic confirmed release", "Reviewer");
            store.SaveProtocolWindow(asset, new() { RequestedExposureSeconds = 3, ReleaseConfirmed = true, ReleaseFrame = 5, WindowConfirmed = true, Reason = "Synthetic planned probe", Reviewer = "Reviewer" });
            store.RecordReview(run, true);
        }
        var experiment = new ExperimentWindow(store.DatabasePath, false, false);
        ExperimentWindow? second = null;
        try
        {
            var tabs = Find<TabControl>(experiment).Single(); tabs.SelectedIndex = 1; Layout(experiment);
            Find<TextBox>(experiment).Single(t => Equals(t.ToolTip, "Study title")).Text = "Synthetic conditional experiment";
            Find<TextBox>(experiment).Single(t => Equals(t.ToolTip, "Primary comparison")).Text = "PRIVATE-A versus PRIVATE-B";
            Find<CheckBox>(experiment).Single(c => Equals(c.Content, "Design confirmed")).IsChecked = true;
            Find<CheckBox>(experiment).Single(c => Equals(c.Content, "Roster confirmed")).IsChecked = true;
            Find<CheckBox>(experiment).Single(c => Equals(c.Content, "Exploratory")).IsChecked = false;
            Find<ComboBox>(experiment).Single(c => Equals(c.Tag, "experiment-unit-design")).SelectedItem = StudyUnitDesign.RepeatedTrialsWithinAnimals;
            foreach (var animal in assets.DistinctBy(a => a.AnimalId)) experiment.Draft.Animals.Add(new() { Id = animal.AnimalId, Alias = "PRIVATE-alias-" + animal.AnimalId, Group = animal.GroupName, IdentityConfirmed = true, AllocationConfirmed = true });
            experiment.Draft.Contexts.Add(new() { Id = "probe-day1", Phase = TrialMode.Probe, Day = 1, RequestedExposureSeconds = 3, ComparisonDurationSeconds = 3,
                TemporalBins = "0,1,2,3", WindowConfirmed = true, CommonCoordinateReference = "Room normalized pool", Orientation = StudyOrientation.Room, AlignmentConfirmed = true });
            for (var i = 0; i < 30; i++) experiment.Draft.Trials.Add(new() { Id = "trial-" + i, AnimalId = assets[i].AnimalId, ContextId = "probe-day1", Order = assets[i].Trial, RecordingId = assets[i].Id });
            Layout(experiment);
            var reference = Find<Button>(experiment).Single(b => Equals(b.Content, "Confirm selected source reference"));
            foreach (var slot in experiment.Draft.Trials) { experiment.TrialsEditor.SelectedItem = slot; reference.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(slot.SourceReferenceConfirmed); }
            experiment.ReviewerInput.Text = "Reviewer"; experiment.ReasonInput.Text = "Entered synthetic expected plan via native editors";
            Find<Button>(experiment).Single(b => Equals(b.Content, "Save expected plan revision")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(experiment.PendingCompute); Assert.Equal(1, experiment.ExpectedPlanRevision); Assert.NotNull(experiment.Result); Assert.False(experiment.Held);
            Assert.Equal(2, experiment.Result!.Study.Groups.Length); Assert.Equal(16, experiment.Result.Study.Animals.Count(a => a.Eligible));
            Assert.All(experiment.Result.Study.Groups, g => Assert.True(g.UnknownFraction > 0));
            Assert.Contains(experiment.Result.Differences[0].Cells, b => b.Upper - b.Lower > .1);
            Assert.Equal(16, experiment.Result.AnimalOccupancyBounds.Count);
            foreach (var animal in experiment.Result.Study.Animals)
                for (var cell = 0; cell < animal.KnownFractions.Length; cell++)
                { var bound = experiment.Result.AnimalOccupancyBounds[animal.Input.AnimalId][cell]; Assert.Equal(animal.KnownFractions[cell], bound.Lower); Assert.True(bound.Upper >= bound.Lower && bound.Upper <= 1); }
            Assert.Contains("Primary comparison", experiment.Result.Interpretation); Assert.Equal(30, experiment.Result.Trials.Length);
            var group = experiment.Result.Study.Groups.Single(g => g.Group == "PRIVATE-B"); Assert.Equal(9, group.IncludedAnimalIds.Length);
            var calculated = experiment.Result.Study.Animals.Where(a => group.IncludedAnimalIds.Contains(a.Input.AnimalId)).Select(a => a.KnownFractions[0]).Average();
            Assert.Equal(calculated, group.KnownFractions[0], 10);
            var outlierCell = group.Grid.Cell((265d - 150) / 135, 0);
            Assert.Equal(1d / (2 * 30 * 9), group.KnownFractions[outlierCell], 10); // independent animal-equal denominator; unequal trial counts cannot add file weight
            tabs.SelectedIndex = 0; Layout(experiment);
            if (Environment.GetEnvironmentVariable("TABAJARA_EXPERIMENT_PROOF_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                foreach (var size in new[] { new Size(1050, 700), new Size(1440, 960) })
                {
                    var content = (FrameworkElement)experiment.Content; content.Measure(size); content.Arrange(new Rect(0, 0, size.Width, size.Height)); content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(System.IO.Path.Combine(directory, $"synthetic-experiment-{size.Width}x{size.Height}.png")); encoder.Save(file);
                }
                Layout(experiment);
            }
            Find<Button>(experiment).Single(b => Equals(b.Content, "Trace difference: PRIVATE-A / PRIVATE-B")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(experiment);
            var traceButtons = Find<Button>(experiment).Where(b => Equals(b.Content, "Open frozen trial source")).ToArray(); Assert.Equal(30, traceButtons.Length);
            var reached = new HashSet<string>();
            foreach (var button in traceButtons)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(experiment.PendingSource);
                Assert.NotNull(experiment.LastSourceViewer); var viewer = experiment.LastSourceViewer!; Assert.False(viewer.Held); Assert.Equal(5, viewer.DisplayedFrame);
                Assert.True(reached.Add(viewer.DisplayedSourceSha256!)); var row = viewer.Snapshot.Recordings.Single(r => r.Asset.SourceSha256 == viewer.DisplayedSourceSha256);
                var index = Array.FindIndex(assets, a => a.Id == row.Asset.Id); AssertPixels(viewer.DisplayedBitmap!, index, 5);
                var marker = viewer.SourceOverlay.Children.OfType<Ellipse>().Single(e => Equals(e.Tag, "active-source-marker")); Assert.Equal(105d, Canvas.GetLeft(marker) + 9);
                if (index == 19) { Pump(viewer.SeekAsync(22)); AssertPixels(viewer.DisplayedBitmap!, index, 22); marker = viewer.SourceOverlay.Children.OfType<Ellipse>().Single(e => Equals(e.Tag, "active-source-marker")); Assert.Equal(265d, Canvas.GetLeft(marker) + 9); }
            }
            Assert.Equal(30, reached.Count);
            second = new ExperimentWindow(store.DatabasePath, false, false); Pump(second.PendingCompute); Assert.Equal(1, second.ExpectedPlanRevision); Assert.Equal(2, second.Result!.Study.Groups.Length);
            experiment.Draft.Exploratory = true; experiment.ReasonInput.Text = "Exploratory label check"; experiment.SavePlan(); Pump(experiment.PendingCompute); Assert.Contains("Exploratory", experiment.Result!.Interpretation);
            second.ReviewerInput.Text = "Other reviewer"; second.ReasonInput.Text = "Stale editor"; second.SavePlan(); Assert.Contains("changed since it was opened", second.StatusText); Assert.Equal(1, second.ExpectedPlanRevision); second.Close(); second = null;
            experiment.Blinded = true; Layout(experiment); AssertPrivate(experiment); experiment.Blinded = false; Assert.True(experiment.Blinded); AssertPrivate(experiment);
            Assert.False(experiment.TryUnblind("", "")); Assert.True(experiment.Blinded);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER deny_experiment_unblind BEFORE INSERT ON audit WHEN NEW.action='experiment_unblinded' BEGIN SELECT RAISE(ABORT,'synthetic denied audit'); END"; command.ExecuteNonQuery();
                Assert.False(experiment.TryUnblind("Reviewer", "Audit must precede exposure")); Assert.True(experiment.Blinded); AssertPrivate(experiment);
                command.CommandText = "DROP TRIGGER deny_experiment_unblind"; command.ExecuteNonQuery();
            }
            Assert.True(experiment.TryUnblind("Reviewer", "Reasoned synthetic plan inspection")); Layout(experiment);
            var basePlan = new StudyPlanStore(store).Latest()!.Plan;
            // Missing slots, animals, exclusions, provisional identity and unassigned imports remain visible/held.
            var heldPlan = basePlan with { Animals = basePlan.Animals.Add(new() { Id = "PRIVATE-missing", Alias = "Missing", Group = "PRIVATE-A", IdentityConfirmed = true, AllocationConfirmed = true })
                    .Select(a => a.Id == assets[0].AnimalId ? a with { Excluded = true, ExclusionReason = "Synthetic exclusion" } : a.Id == assets[2].AnimalId ? a with { IdentityConfirmed = false, AllocationConfirmed = false } : a).ToImmutableArray(),
                Trials = basePlan.Trials.Select(t => t.Id == "trial-29" ? t with { RecordingId = null, MissingReason = "Synthetic missing exposure", SourceReferenceConfirmed = false } : t).Append(new() { Id = "missing-trial", AnimalId = "PRIVATE-missing", ContextId = "probe-day1", MissingReason = "Not acquired" }).ToImmutableArray() };
            var planStore = new StudyPlanStore(store); planStore.Save(heldPlan, "Reviewer", "Missing/excluded/provisional test", experiment.ExpectedPlanRevision);
            Pump(Task.Delay(2300)); Assert.True(experiment.Held); Assert.Null(experiment.Result); experiment.Reload(); Pump(experiment.PendingCompute);
            Assert.Contains(experiment.Result!.Study.Animals, a => a.Input.AnimalId == "PRIVATE-missing" && !a.Eligible);
            Assert.Contains(experiment.Result.Study.Animals, a => a.Input.Excluded && !a.Eligible); Assert.Single(experiment.Result.Unassigned);
            Assert.Contains(experiment.Result.Trials, t => t.Recording is null && !t.Holds.IsEmpty);
            var duplicate = heldPlan with { Trials = heldPlan.Trials.Add(basePlan.Trials[0] with { Id = "duplicate", Order = 99 }) };
            Assert.Throws<ArgumentException>(() => planStore.Save(duplicate, "Reviewer", "Duplicate source cannot add weight", experiment.ExpectedPlanRevision));
            planStore.Save(basePlan, "Reviewer", "Restore eligible plan", experiment.ExpectedPlanRevision); experiment.Reload(); Pump(experiment.PendingCompute); Assert.Equal(2, experiment.Result!.Study.Groups.Length);
            store.RecordReview(runs[0], false); Pump(Task.Delay(2300)); Assert.True(experiment.Held); Assert.Null(experiment.Result); experiment.Reload(); Pump(experiment.PendingCompute);
            Assert.Contains(experiment.Result!.Trials, t => t.Slot.Id == "trial-0" && !t.Holds.IsEmpty); store.RecordReview(runs[0], true); experiment.Reload(); Pump(experiment.PendingCompute); Assert.Equal(16, experiment.Result!.Study.Animals.Count(a => a.Eligible));
            var malformed = basePlan with { Trials = basePlan.Trials.Select(t => t.Id == "trial-0" ? t with { ReferenceSettingsHash = new string('0', 64) } : t).ToImmutableArray() };
            planStore.Save(malformed, "Reviewer", "Reference mismatch", experiment.ExpectedPlanRevision); experiment.Reload(); Pump(experiment.PendingCompute); Assert.Contains(experiment.Result!.Trials, t => t.Slot.Id == "trial-0" && !t.Holds.IsEmpty);
            planStore.Save(basePlan, "Reviewer", "Restore reference", experiment.ExpectedPlanRevision); experiment.Reload(); Pump(experiment.PendingCompute);
            store.SaveProtocolWindow(assets[0], new() { RequestedExposureSeconds = 2, ReleaseConfirmed = true, ReleaseFrame = 5, WindowConfirmed = true, Reason = "Synthetic unequal requested window", Reviewer = "Reviewer" });
            Pump(Task.Delay(2300)); Assert.True(experiment.Held); Assert.Null(experiment.Result);
            store.RecordReview(runs[0], true); experiment.Reload(); Pump(experiment.PendingCompute); Assert.Contains(experiment.Result!.Trials, t => t.Slot.Id == "trial-0" && t.Holds.Any(h => h.Contains("requested exposure differs")));
            store.SaveProtocolWindow(assets[0], new() { RequestedExposureSeconds = 3, ReleaseConfirmed = true, ReleaseFrame = 5, WindowConfirmed = true, Reason = "Restore planned window", Reviewer = "Reviewer" }); store.RecordReview(runs[0], true); experiment.Reload(); Pump(experiment.PendingCompute);
            Assert.All(experiment.Result!.Trials.Where(t => t.Map is not null), t => Assert.Equal("pool radii", t.Map!.Input.Grid.CoordinateUnit));
            var wrongOrientation = basePlan with { Contexts = basePlan.Contexts.Select(c => c with { AlignmentConfirmed = false, Orientation = StudyOrientation.Unconfirmed }).ToImmutableArray() };
            planStore.Save(wrongOrientation, "Reviewer", "Unconfirmed orientation", experiment.ExpectedPlanRevision); experiment.Reload(); Pump(experiment.PendingCompute); Assert.Empty(experiment.Result!.Study.Groups);
            planStore.Save(basePlan, "Reviewer", "Restore orientation", experiment.ExpectedPlanRevision); experiment.Reload(); Pump(experiment.PendingCompute); Assert.Equal(2, experiment.Result!.Study.Groups.Length);
            var sourceBytes = File.ReadAllBytes(assets[0].Path); sourceBytes[^1] ^= 1; File.WriteAllBytes(assets[0].Path, sourceBytes);
            Pump(Task.Delay(2300)); Assert.True(experiment.Held); Assert.Null(experiment.Result); sourceBytes[^1] ^= 1; File.WriteAllBytes(assets[0].Path, sourceBytes); experiment.Reload(); Pump(experiment.PendingCompute); Assert.Equal(2, experiment.Result!.Study.Groups.Length);
            string ShortId(string id) => id == assets[0].AnimalId ? "A" : id == assets[2].AnimalId ? "B" : id;
            foreach (var asset in assets) store.SaveVideo(asset with { AnimalId = ShortId(asset.AnimalId), GroupName = asset.GroupName == "PRIVATE-A" ? "WT" : "KO", BlindedAlias = "Informative WT KO alias" });
            var shortPlan = basePlan with { Exploratory = false, PrimaryComparison = "WT versus KO", Animals = basePlan.Animals.Select(a => a with { Id = ShortId(a.Id), Group = a.Group == "PRIVATE-A" ? "WT" : "KO", Alias = "Informative WT KO alias",
                    Excluded = a.Id == assets[0].AnimalId, ExclusionReason = a.Id == assets[0].AnimalId ? "FREE WT KO A B reason" : "" }).ToImmutableArray(),
                Trials = basePlan.Trials.Select(t => t with { AnimalId = ShortId(t.AnimalId) }).ToImmutableArray() };
            planStore.Save(shortPlan, "Reviewer", "Short label privacy test", experiment.ExpectedPlanRevision); experiment.Reload(); Pump(experiment.PendingCompute); experiment.Blinded = true; Layout(experiment); AssertPrivate(experiment);
            foreach (var text in Find<TextBlock>(experiment))
            {
                Assert.DoesNotMatch(@"\b(WT|KO)\b", text.Text);
                Assert.DoesNotContain("Informative", text.Text); Assert.DoesNotContain("FREE", text.Text);
                Assert.NotEqual("A", text.Text.Trim()); Assert.NotEqual("B", text.Text.Trim()); Assert.DoesNotContain("Animal A", text.Text); Assert.DoesNotContain("Animal B", text.Text);
            }
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM audit WHERE action IN ('atlas_unblinded','experiment_unblinded')";
                Assert.Equal(2L, (long)command.ExecuteScalar()!);
            }
            evidence.Add("PASS native expected-plan editors save/reopen30-source two-group plan;16 equally weighted animals with unequal trial counts, independent outlier-cell denominator1/(2*30*9), common-scale maps and nonzero unknown-mass bounds; every actual difference-contribution button reaches independent source signatures/markers including outlier265.");
            evidence.Add("PASS primary/exploratory labels, optimistic competing editor rejection, missing/excluded/provisional/unassigned rows, duplicate source rejection, review staleness and per-source reference mismatch; blinded native metadata tree hides PRIVATE labels.");
            evidence.Add("PASS opaque blinded projection hides WT/KO, A/B animal IDs, informative aliases and free reasons; unrecorded checkbox and failed SQLite audit retain blind state, valid reviewer/reason is committed before reveal, reblinding hides editor/child labels.");
        }
        finally { second?.Close(); experiment.Close(); }
        TemporalNativeProof.Run(store, evidence);
    }
    private static void AssertPixels(BitmapSource source, int recording, int frame)
    {
        var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0); var pixel = new byte[4]; image.CopyPixels(new Int32Rect(10, 10, 1, 1), pixel, 4, 0);
        Assert.InRange(Math.Abs(pixel[0] - (20 + 3 * frame + recording)), 0, 1); Assert.InRange(Math.Abs(pixel[1] - (40 + 3 * frame)), 0, 1); Assert.InRange(Math.Abs(pixel[2] - (230 - 3 * frame)), 0, 1);
    }
    private static void AssertPrivate(ExperimentWindow window)
    { foreach (var text in Find<TextBlock>(window)) Assert.DoesNotContain("PRIVATE", text.Text); foreach (var control in Find<FrameworkElement>(window)) if (control.ToolTip is string tip) Assert.DoesNotContain("PRIVATE", tip); }
    private static IEnumerable<T> Find<T>(Window window) where T : DependencyObject => Descendants((DependencyObject)window.Content).OfType<T>();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Layout(Window window)
    { var root = (FrameworkElement)window.Content; root.Measure(new Size(1050, 700)); root.Arrange(new Rect(0, 0, 1050, 700)); root.UpdateLayout(); Pump(Task.Delay(30)); root.UpdateLayout(); }
    private static void Pump(Task task)
    { var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false)); timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Assert.True(task.IsCompleted); task.GetAwaiter().GetResult(); }
}
