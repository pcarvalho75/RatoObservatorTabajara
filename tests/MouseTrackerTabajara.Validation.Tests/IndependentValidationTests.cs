using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
using Window = System.Windows.Window;

namespace MouseTrackerTabajara.Validation.Tests;

public class IndependentValidationTests(ITestOutputHelper output)
{
    [Fact] public void CopiedDevelopmentBytesWithNewRecordingAndSessionRemainIneligibleForIndependentValidation()
    {
        using var fixture = new Fixture();
        fixture.Sample(fixture.Plan());
        var path = System.IO.Path.Combine(fixture.Folder, "independent-copy.avi");
        File.Copy(fixture.Asset.Path, path);
        // Import and the database already reject duplicate bytes. Validate the
        // same rule on independently reconstructed/serialized snapshot material.
        Assert.Equal(fixture.Asset.Id, new WorkspaceService(fixture.Store).Import(path).Id);
        var copied = fixture.Asset with { Id = Guid.NewGuid().ToString("N"), Path = path };
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Store.SaveVideo(copied));
        Assert.NotEqual(fixture.Asset.Id, copied.Id);
        Assert.Equal(fixture.Asset.SourceSha256, copied.SourceSha256);
        var candidate = fixture.Plan() with { Id = "fresh-label", Recordings = [new(copied.Id, "different-session", ValidationSplit.IndependentValidation, false, true, "probe-protocol", "WT")] };
        var persisted = IndependentValidationSnapshot.Read(fixture.Store.DatabasePath);
        var snapshot = persisted with { Imported = persisted.Imported.Add(copied) };
        var error = Assert.Throws<ArgumentException>(() => snapshot.ValidatePlan(candidate));
        Assert.Contains("identical copied source bytes", error.Message);
        Assert.Throws<ArgumentException>(() => IndependentValidationSampling.Generate(snapshot, candidate));
    }
    private sealed class Fixture : IDisposable
    {
        public string Folder { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "independent-validation-" + Guid.NewGuid().ToString("N"));
        public StudyStore Store { get; } public VideoAsset Asset { get; } public AnalysisRun Run { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Folder); var path = System.IO.Path.Combine(Folder, "PRIVATE-WT-A.avi");
            using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new OpenCvSharp.Size(100, 100)))
            {
                Assert.True(writer.IsOpened()); for (var f = 0; f < 20; f++)
                { using var image = new Mat(100, 100, MatType.CV_8UC3, new Scalar(20 + f * 3, 60 + f * 2, 200 - f * 3)); Cv2.Circle(image, new OpenCvSharp.Point(40 + f, 50), 4, Scalar.Black, -1); writer.Write(image); }
            }
            Store = new StudyStore(System.IO.Path.Combine(Folder, "study.morrisdb")); Asset = new WorkspaceService(Store).Import(path) with { AnimalId = "A", AnimalIdConfirmed = true, GroupName = "WT", Phase = TrialMode.Probe,
                Settings = new() { Cx = .5, Cy = .5, Rx = .4, Ry = .4, Platform = new(.5, .5, .04), ArenaConfirmed = true } }; Store.SaveVideo(Asset);
            var points = Enumerable.Range(0, 20).Select(f => f == 5 ? new TrackPoint(f, f / 10d, null, null, PointStatus.Missing, 0) : new TrackPoint(f, f / 10d, f == 10 ? 80 : 42 + f, 50, PointStatus.Tracked, 1)).ToList();
            Run = new AnalysisRun { VideoId = Asset.Id, SourceSha256 = Asset.SourceSha256, Metadata = Asset.Metadata, Settings = Asset.Settings, Points = points, Metrics = MetricCalculator.Calculate(points, Asset.Metadata, Asset.Settings), AlgorithmContract = AlgorithmIdentity.Contract, NominalTimingVerified = true }; Store.SaveRun(Run);
            Store.RecordBiologicalEvent(Asset, BiologicalEventKind.WaterEntry, 2, "Synthetic producer release", "Producer reviewer");
        }
        public IndependentValidationPlan Plan(int frames = 20, double second = 0) => new() { SyntheticToolingOnly = true, RandomFramesPerRecording = frames, DifficultIntervalsPerRecording = 1, IntervalFrames = 4, SecondReviewFraction = second,
            Recordings = [new(Asset.Id, "session-one", ValidationSplit.Development, true, false, "probe-protocol", "WT")], Tolerances = new(3, 3, .1, .01, .01, .8) };
        public (ValidationSampling Sampling, ValidationHistoryStore History) Sample(IndependentValidationPlan plan)
        {
            var history = new ValidationHistoryStore(Store.DatabasePath); var before = IndependentValidationSnapshot.Read(Store.DatabasePath); history.Save("plan", plan.Id, JsonDefaults.Serialize(plan), "Reviewer", "Prespecified source split/tolerances", 0);
            var sampling = IndependentValidationSampling.Generate(before, plan); history.Save("sampling", plan.Id, JsonDefaults.Serialize(sampling), "Reviewer", "Disclosed random/purposive sample", 0); return (sampling, history);
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Folder, true); }
    }
    [Fact] public void SamplingRetainsUnknownAndMissingReferencesAndRejectsDevelopmentRelabeling()
    {
        using var fixture = new Fixture(); var plan = fixture.Plan(5); var snapshot = IndependentValidationSnapshot.Read(fixture.Store.DatabasePath);
        var a = IndependentValidationSampling.Generate(snapshot, plan); var b = IndependentValidationSampling.Generate(snapshot, plan); Assert.Equal(JsonDefaults.Serialize(a), JsonDefaults.Serialize(b));
        Assert.Equal(5, a.Cases.Count(c => c.RandomSample)); Assert.All(a.Cases.Where(c => c.RandomSample), c => Assert.Equal(.25, c.InclusionProbability));
        Assert.Contains(a.Cases, c => !c.RandomSample && c.StartFrame <= 5 && c.EndFrame >= 5 && c.InclusionProbability is null);
        var saved = fixture.Sample(plan); snapshot = IndependentValidationSnapshot.Read(fixture.Store.DatabasePath);
        var report = IndependentValidationComparison.Compare(snapshot, plan, saved.Sampling); Assert.Equal(0, report.PositionJoinedFrames); Assert.All(report.Frames, f => Assert.Null(f.PositionErrorPixels)); Assert.Contains(report.Holds, h => h.Contains("reference-completion"));
        Assert.Throws<ArgumentException>(() => snapshot.ValidatePlan(plan with { Recordings = [plan.Recordings[0] with { Split = ValidationSplit.IndependentValidation, UsedForTuning = false, IndependenceConfirmed = true }] }));
        Assert.Empty(fixture.Store.Corrections(fixture.Run.Id)); Assert.Equal(JsonDefaults.Serialize(fixture.Run), JsonDefaults.Serialize(fixture.Store.LatestRun(fixture.Asset.Id)));
    }
    [Fact] public void ExactJoinsErrorFalseAcceptedGapEventsAndDisagreementStayDistinct()
    {
        using var fixture = new Fixture(); var plan = fixture.Plan(second: 1); var saved = fixture.Sample(plan); var samplingHash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(saved.Sampling));
        void Save(int frame, ReferenceReviewerLane lane, string reviewer, double? x, ReferenceVisibility visibility = ReferenceVisibility.Visible, ReferenceEvent semantic = ReferenceEvent.None, ImmutableArray<long> consensus = default)
        {
            var reference = new IndependentReference(fixture.Asset.Id, frame, lane, reviewer, visibility, x, x.HasValue ? 50 : null, semantic, "Independent synthetic reference", consensus.IsDefault ? [] : consensus); reference.Validate(fixture.Asset.Metadata);
            var key = IndependentValidationComparison.ReferenceKey(samplingHash, reference); saved.History.Save("reference", key, JsonDefaults.Serialize(reference), reviewer, reference.Reason, saved.History.History().LastOrDefault(h => h.EntityId == key)?.Revision ?? 0);
        }
        for (var f = 0; f < 19; f++) { var visible = f == 10 ? ReferenceVisibility.NotVisible : ReferenceVisibility.Visible; Save(f, ReferenceReviewerLane.First, "First reviewer", f == 10 ? null : 40 + f, visible, f == 0 ? ReferenceEvent.WaterEntry : ReferenceEvent.None); Save(f, ReferenceReviewerLane.Second, "Second reviewer", f == 10 ? null : f == 7 ? 60 : 40 + f, visible, f == 0 ? ReferenceEvent.WaterEntry : ReferenceEvent.None); }
        var snapshot = IndependentValidationSnapshot.Read(fixture.Store.DatabasePath); var report = IndependentValidationComparison.Compare(snapshot, plan, saved.Sampling);
        Assert.Equal(20, report.SampledUniqueFrames); Assert.True(report.Frames.Single(f => f.Frame == 10).FalseAccepted); Assert.True(report.Frames.Single(f => f.Frame == 5).MissedVisible);
        Assert.Equal(2, report.Frames.Single(f => f.Frame == 3).PositionErrorPixels); Assert.True(report.Frames.Single(f => f.Frame == 3).PositionPass);
        Assert.Equal(.2, report.Frames.Single(f => f.Frame == 0).EventTimingErrorSeconds); Assert.False(report.Frames.Single(f => f.Frame == 0).EventPass);
        Assert.True(report.Frames.Single(f => f.Frame == 7).Disagreement); Assert.Null(report.Frames.Single(f => f.Frame == 7).PositionErrorPixels);
        Assert.NotEmpty(report.Frames.Single(f => f.Frame == 19).Holds); Assert.All(report.Endpoints, e => Assert.Null(e.WithinTolerances));
        var ids = report.Frames.Single(f => f.Frame == 7).ReferenceIds; Save(7, ReferenceReviewerLane.Consensus, "Consensus reviewer", 47, consensus: ids);
        snapshot = IndependentValidationSnapshot.Read(fixture.Store.DatabasePath); report = IndependentValidationComparison.Compare(snapshot, plan, saved.Sampling); Assert.True(report.Frames.Single(f => f.Frame == 7).Disagreement); Assert.Equal(2, report.Frames.Single(f => f.Frame == 7).PositionErrorPixels); Assert.Equal(3, report.Frames.Single(f => f.Frame == 7).ReferenceIds.Length);
        Assert.Contains("Real held-out", report.Interpretation); Assert.Empty(fixture.Store.Corrections(fixture.Run.Id)); Assert.Equal(JsonDefaults.Serialize(fixture.Run), JsonDefaults.Serialize(fixture.Store.LatestRun(fixture.Asset.Id)));
    }
    [Fact] public void ActualWpfReferenceModeSamplesExactPixelsAndSavesSeparateReplayableComparison()
    {
        Exception? error = null; var lines = new List<string>(); var thread = new Thread(() => { try { Native(lines); } catch (Exception ex) { error = ex; } }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromMinutes(3))); foreach (var line in lines) output.WriteLine(line); if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    [Fact] public void ContinuousReferenceEndpointsDetectBorderOccupancyAndJumpConsequences()
    {
        using var fixture = new Fixture(); var plan = fixture.Plan();
        var points = fixture.Run.Points.Select(p => p.Frame == 13 ? p with { X = 90 } : p).ToList();
        var changed = fixture.Run with { Id = Guid.NewGuid().ToString("N"), Points = points, Metrics = MetricCalculator.Calculate(points, fixture.Asset.Metadata, fixture.Asset.Settings) }; fixture.Store.SaveRun(changed);
        var snapshot = IndependentValidationSnapshot.Read(fixture.Store.DatabasePath); var generated = IndependentValidationSampling.Generate(snapshot, plan);
        var sampling = generated with { Cases = [new(fixture.Asset.Id, 11, 14, false, null, 20, false, "Prespecified synthetic continuous border/jump interval")] };
        var history = new ValidationHistoryStore(fixture.Store.DatabasePath); history.Save("plan", plan.Id, JsonDefaults.Serialize(plan), "Reviewer", "Endpoint tolerances before scoring", 0); history.Save("sampling", plan.Id, JsonDefaults.Serialize(sampling), "Reviewer", "Declared synthetic difficult interval", 0);
        var sampleHash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(sampling));
        for (var frame = 11; frame <= 14; frame++)
        { var reference = new IndependentReference(fixture.Asset.Id, frame, ReferenceReviewerLane.First, "Independent reviewer", ReferenceVisibility.Visible, 40 + frame, 50, ReferenceEvent.None, "Independent encoded truth", []); history.Save("reference", IndependentValidationComparison.ReferenceKey(sampleHash, reference), JsonDefaults.Serialize(reference), reference.Reviewer, reference.Reason, 0); }
        snapshot = IndependentValidationSnapshot.Read(fixture.Store.DatabasePath); var report = IndependentValidationComparison.Compare(snapshot, plan, sampling); var interval = Assert.Single(report.Endpoints);
        Assert.Equal(.85, interval.PathDifferenceDiameters!.Value, 10); Assert.Equal(.5, interval.SiteOccupancyDifference!.Value, 10); Assert.False(interval.WithinTolerances);
        Assert.Contains(report.Frames, f => f.Frame == 13 && f.PositionPass == false); Assert.Empty(fixture.Store.Corrections(changed.Id));
    }
    [Fact] public void ManualRepairCannotPassAsRawAutomaticAccuracy()
    {
        using var fixture = new Fixture();
        fixture.Store.SaveCorrection(fixture.Run, new(fixture.Run.Id, 3, 43, 50, "Synthetic manual repair", DateTime.UtcNow));
        foreach (var target in new[] { ValidationMeasurementTarget.RawAutomatic, ValidationMeasurementTarget.InspectedCorrected })
        {
            var plan = fixture.Plan() with { Id = "target-" + target, MeasurementTarget = target, Tolerances = new(1, 3, .1, .01, .01, .8) };
            var saved = fixture.Sample(plan); var hash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(saved.Sampling));
            var reference = new IndependentReference(fixture.Asset.Id, 3, ReferenceReviewerLane.First, "Independent reviewer", ReferenceVisibility.Visible, 43, 50, ReferenceEvent.None, "Independent encoded truth", []);
            saved.History.Save("reference", IndependentValidationComparison.ReferenceKey(hash, reference), JsonDefaults.Serialize(reference), reference.Reviewer, reference.Reason, 0);
            var report = IndependentValidationComparison.Compare(IndependentValidationSnapshot.Read(fixture.Store.DatabasePath), plan, saved.Sampling);
            Assert.Equal(target, report.MeasurementTarget); var frame = report.Frames.Single(f => f.Frame == 3);
            Assert.Equal(target == ValidationMeasurementTarget.RawAutomatic ? 2d : 0d, frame.PositionErrorPixels);
            Assert.Equal(target == ValidationMeasurementTarget.InspectedCorrected, frame.PositionPass);
            Assert.True(report.SampledUniqueFrames > report.PositionJoinedFrames);
        }
        Assert.Single(fixture.Store.Corrections(fixture.Run.Id)); Assert.Equal(45, fixture.Store.LatestRun(fixture.Asset.Id)!.Points.Single(p => p.Frame == 3).X);
    }
    [Fact] public void FrozenProducerReferenceAndSourceMutationRemainHeld()
    {
        using var fixture = new Fixture(); var plan = fixture.Plan(); var saved = fixture.Sample(plan);
        var snapshot = IndependentValidationSnapshot.Read(fixture.Store.DatabasePath); Assert.True(snapshot.IsCurrent());
        var reference = new IndependentReference(fixture.Asset.Id, 3, ReferenceReviewerLane.First, "Reviewer", ReferenceVisibility.Visible, 43, 50, ReferenceEvent.None, "Independent truth", []);
        saved.History.Save("reference", IndependentValidationComparison.ReferenceKey(IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(saved.Sampling)), reference), JsonDefaults.Serialize(reference), reference.Reviewer, reference.Reason, 0);
        Assert.False(snapshot.IsCurrent()); snapshot = IndependentValidationSnapshot.Read(fixture.Store.DatabasePath);
        using (var stream = new FileStream(fixture.Asset.Path, FileMode.Open, FileAccess.Write)) { stream.Position = stream.Length - 1; stream.WriteByte(17); }
        var report = IndependentValidationComparison.Compare(snapshot, plan, saved.Sampling);
        Assert.All(report.Frames, frame => Assert.Null(frame.PositionErrorPixels)); Assert.All(report.Frames, frame => Assert.NotEmpty(frame.Holds));
        fixture.Store.SaveCorrection(fixture.Run, new(fixture.Run.Id, 3, 43, 50, "New manual revision", DateTime.UtcNow));
        Assert.False(snapshot.IsCurrent());
        Assert.Throws<InvalidDataException>(() => IndependentValidationComparison.Compare(IndependentValidationSnapshot.Read(fixture.Store.DatabasePath), plan, saved.Sampling));
    }
    private static void Native(List<string> evidence)
    {
        using var fixture = new Fixture(); var originalRun = JsonDefaults.Serialize(fixture.Run); var app = new App(); app.InitializeComponent(); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var window = new IndependentValidationWindow(fixture.Store.DatabasePath, false);
        try
        {
            Layout(window, 1050, 700);
            var draft = Assert.Single(window.Draft); draft.Session = "session-one"; draft.ProtocolIdentity = "probe-protocol"; draft.Allocation = "WT"; draft.Split = ValidationSplit.Development; draft.UsedForTuning = true;
            window.SyntheticInput.IsChecked = true; window.ReviewerInput.Text = "First reviewer"; window.ReasonInput.Text = "Prespecified synthetic tooling validation";
            Find<Button>(window).Single(b => Equals(b.Content, "Save split and tolerance plan")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<Button>(window).Single(b => Equals(b.Content, "Generate disclosed frozen samples")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.NotNull(window.Snapshot.Sampling);
            var tabs = Find<TabControl>(window).Single(); tabs.SelectedIndex = 1; Layout(window, 1050, 700);
            window.SamplesInput.SelectedItem = window.SamplesInput.Items.OfType<ValidationFrameChoice>().First(); Find<Button>(window).Single(b => Equals(b.Content, "Open exact sampled source")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window.PendingFrame); Assert.Null(window.DisplayedBitmap);
            window.PixelDisclosureInput.IsChecked = true; Find<Button>(window).Single(b => Equals(b.Content, "Open exact sampled source")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window.PendingFrame); Assert.NotNull(window.DisplayedBitmap);
            var f = window.DisplayedFrame!.Value; var pixel = new byte[4]; new FormatConvertedBitmap(window.DisplayedBitmap, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(5, 5, 1, 1), pixel, 4, 0); Assert.InRange(Math.Abs(pixel[0] - (20 + f * 3)), 0, 1);
            Assert.Empty(window.ReferenceOverlay.Children); Assert.DoesNotContain(Find<TextBlock>(window), t => t.Text.Contains("automatic X"));
            var viewport = Find<Border>(window).Single(b => b.Background == Brushes.DarkSlateGray); var placement = DisplayToVideo.Place(viewport.ActualWidth, viewport.ActualHeight, 100, 100);
            window.SetReferenceFromViewport(placement.OffsetX + (40 + f) * placement.Scale, placement.OffsetY + 50 * placement.Scale);
            var marker = Assert.Single(window.ReferenceOverlay.Children.OfType<Ellipse>()); Assert.Equal(40d + f, Canvas.GetLeft(marker) + 6, 8);
            Find<Button>(window).Single(b => Equals(b.Content, "Save independent reference revision")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Contains(window.Snapshot.History, h => h.Kind == "reference");
            window.ReviewerLaneInput.SelectedIndex = 1; Assert.Empty(window.ReferenceOverlay.Children); window.ReviewerInput.Text = "Second reviewer";
            window.SetReferenceFromViewport(placement.OffsetX + (45 + f) * placement.Scale, placement.OffsetY + 50 * placement.Scale); window.SaveReference();
            tabs.SelectedIndex = 2; Layout(window, 1050, 700); Find<Button>(window).Single(b => Equals(b.Content, "Compare frozen reference and producer")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window.PendingComparison); Assert.NotNull(window.Report); Assert.Contains(window.Report!.Frames, row => row.Disagreement);
            tabs.SelectedIndex = 1; window.ReviewerLaneInput.SelectedIndex = 2; Layout(window, 1050, 700); Assert.Equal(2, window.ReferenceOverlay.Children.OfType<Ellipse>().Count(e => Equals(e.Tag, "independent-consensus-input")));
            window.ReviewerInput.Text = "Consensus reviewer"; window.SetReferenceFromViewport(placement.OffsetX + (40 + f) * placement.Scale, placement.OffsetY + 50 * placement.Scale); window.SaveReference();
            Assert.Contains(window.Snapshot.History, h => h.Kind == "reference" && JsonDefaults.Deserialize<IndependentReference>(h.Json).Lane == ReferenceReviewerLane.Consensus);
            tabs.SelectedIndex = 2; window.StartComparison(); Pump(window.PendingComparison); Assert.NotNull(window.Report); Assert.Contains(window.Report!.Frames, row => row.Frame == f && row.ReferenceIds.Length == 3);
            Find<Button>(window).Single(b => Equals(b.Content, "Save replayable comparison report")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Contains(new ValidationHistoryStore(fixture.Store.DatabasePath).History(), h => h.Kind == "report");
            Find<Button>(window).Single(b => Equals(b.Content, "Replay last saved comparison")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window.PendingComparison); Assert.NotNull(window.Report);
            Assert.Equal(originalRun, JsonDefaults.Serialize(fixture.Store.LatestRun(fixture.Asset.Id))); Assert.Empty(fixture.Store.Corrections(fixture.Run.Id));
            window.Blinded = true; Layout(window, 1050, 700); foreach (var text in Find<TextBlock>(window)) Assert.DoesNotContain("PRIVATE", text.Text);
            window.Blinded = false; Assert.True(window.Blinded); Assert.Contains("protected", window.StatusText);
            window.ReviewerInput.Text = "Reviewer"; window.ReasonInput.Text = "Reasoned allocation audit"; window.Blinded = false; Assert.False(window.Blinded);
            var referenceHistory = new ValidationHistoryStore(fixture.Store.DatabasePath).History(); Assert.Contains(referenceHistory, h => h.Kind == "unblinding");
            window.StartComparison(); window.StartComparison(); Find<Button>(window).Single(b => Equals(b.Content, "Cancel comparison")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window.PendingComparison); Assert.True(window.Held); Assert.Null(window.Report);
            window.Reload(); window.StartComparison(); Pump(window.PendingComparison); Assert.NotNull(window.Report);
            fixture.Store.SaveVideo(fixture.Asset with { Day = 2 }); Pump(Task.Delay(2300)); Assert.True(window.Held); Assert.Null(window.DisplayedBitmap); Assert.Null(window.Report);
            evidence.Add("PASS actual native split/tolerance and sampling controls; recorded pixel-disclosure gate; exact independently encoded source pixels and body-center mapping; automatic/other-reviewer overlay hidden; independent first/second history and disagreement comparison/report saved separately from unchanged raw run/corrections.");
            evidence.Add("PASS blinded metadata and reasoned reveal, minimum1050x700 native layout, producer mutation idle hold; real held-out accuracy remains UNRUN.");
        }
        finally { window.Close(); app.Shutdown(); }
    }
    private static IEnumerable<T> Find<T>(Window window) where T : DependencyObject => Descendants((DependencyObject)window.Content).OfType<T>();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Layout(Window window, double width, double height) { var root = (FrameworkElement)window.Content; root.Measure(new System.Windows.Size(width, height)); root.Arrange(new System.Windows.Rect(0, 0, width, height)); root.UpdateLayout(); Pump(Task.Delay(30)); root.UpdateLayout(); }
    private static void Pump(Task task) { var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false)); timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Assert.True(task.IsCompleted); task.GetAwaiter().GetResult(); }
}
