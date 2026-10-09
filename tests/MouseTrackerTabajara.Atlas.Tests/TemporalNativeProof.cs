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

internal static class TemporalNativeProof
{
    private sealed class DelayedCalculator : IExperimentCalculator
    {
        public ManualResetEventSlim Entered { get; } = new(); public ManualResetEventSlim Release { get; } = new(); public string? Context;
        public ExperimentResult Calculate(AtlasSnapshot snapshot, string context, CancellationToken cancellation)
        {
            if (Context == context) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Bounded delayed calculation fixture."); }
            return new NativeExperimentCalculator().Calculate(snapshot, context, cancellation);
        }
    }
    public static void Run(StudyStore store, List<string> evidence)
    {
        var assets = store.Videos().ToArray(); var planned = new List<PlannedTrial>(); var animals = new List<PlannedAnimal>();
        for (var i = 0; i < assets.Length; i++)
        {
            var day = i < 15 ? 1 : 2; var id = "MX" + i % 15;
            assets[i] = assets[i] with { AnimalId = id, AnimalIdConfirmed = true, Day = day, Trial = 1, GroupName = i % 15 < 7 ? "WT" : "KO", Included = true };
            store.SaveVideo(assets[i]); store.RecordReview(store.LatestRun(assets[i].Id)!, true);
            if (day == 1) animals.Add(new() { Id = id, Alias = "WT KO informative alias", Group = assets[i].GroupName, IdentityConfirmed = true, AllocationConfirmed = true });
            planned.Add(new() { Id = "temporal-" + i, AnimalId = id, ContextId = "PRIVATE-day-" + day, Order = 1, RecordingId = assets[i].Id,
                SourceReferenceConfirmed = true, ReferenceSourceSha256 = assets[i].SourceSha256, ReferenceSettingsHash = StudyStore.SettingsHash(assets[i].Settings) });
        }
        var plan = new StudyPlan { UnitDesign = StudyUnitDesign.RepeatedTrialsWithinAnimals, DesignConfirmed = true, RosterConfirmed = true, Exploratory = true,
            Animals = animals.ToImmutableArray(), Trials = planned.ToImmutableArray(), Contexts = Enumerable.Range(1, 2).Select(day => new PlannedContext
            { Id = "PRIVATE-day-" + day, Day = day, Phase = TrialMode.Probe, RequestedExposureSeconds = 3, ComparisonDurationSeconds = 3, TemporalBinEdges = [0, .15, .45, 1.5, 3], WindowConfirmed = true, AlignmentConfirmed = true,
                Orientation = StudyOrientation.Room, CommonCoordinateReference = "Normalized room" }).ToImmutableArray() };
        var plans = new StudyPlanStore(store); plans.Save(plan, "Reviewer", "Prespecified repeated mice across days", plans.Latest()!.Revision);
        var window = new ExperimentWindow(store.DatabasePath, false, false);
        try
        {
            Pump(window.PendingCompute); Assert.NotNull(window.TemporalResult); Assert.True(window.TemporalResult!.ConnectWithinAnimal); Assert.Equal(30, window.TemporalResult.AnimalDays.Length);
            var tabs = Find<TabControl>(window).Single(); tabs.SelectedIndex = 2; Layout(window);
            Assert.Contains(Find<Line>(window), line => Equals(line.Tag, "within-mouse-day-line"));
            var point = Find<Button>(window).First(button => button.Tag is TemporalAnimalBin bin && bin.ContextId == "PRIVATE-day-1" && bin.StartSeconds == 0 && bin.EndSeconds == .15);
            point.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window);
            var source = Find<Button>(window).Single(button => Equals(button.Content, "Open frozen temporal trial/bin source"));
            var trace = (TemporalBinProfile)source.Tag; Assert.Equal(5, trace.FirstFrame); Assert.Equal(6, trace.LastFrame);
            source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window.PendingSource); Assert.Equal(5, window.LastSourceViewer!.DisplayedFrame);
            var index = Array.FindIndex(assets, a => a.Id == trace.Recording!.Asset.Id); Pixels(window.LastSourceViewer.DisplayedBitmap!, index, 5);
            var marker = window.LastSourceViewer.SourceOverlay.Children.OfType<Ellipse>().Single(e => Equals(e.Tag, "active-source-marker")); Assert.Equal(105d, Canvas.GetLeft(marker) + 9);
            var next = Descendants((DependencyObject)window.LastSourceViewer.Content).OfType<Button>().Single(b => Equals(b.Content, "Next source frame"));
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window.LastSourceViewer.PendingNavigation); Assert.Equal(6, window.LastSourceViewer.DisplayedFrame); Pixels(window.LastSourceViewer.DisplayedBitmap!, index, 6);
            Assert.Contains(window.TemporalResult.Visits, visit => visit.ExactSeconds is null && visit.History == EventHistory.EarlierVisitPossibleInGap);
            Assert.DoesNotContain(window.TemporalResult.Visits, visit => visit.History != EventHistory.ObservedExact && visit.ExactSeconds.HasValue);
            if (Environment.GetEnvironmentVariable("TABAJARA_TEMPORAL_PROOF_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                foreach (var size in new[] { new Size(1050, 700), new Size(1440, 960) })
                {
                    var content = (FrameworkElement)window.Content; content.Measure(size); content.Arrange(new Rect(0, 0, size.Width, size.Height)); content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(System.IO.Path.Combine(directory, $"synthetic-temporal-{size.Width}x{size.Height}.png")); encoder.Save(file);
                }
                Layout(window);
            }
            window.Blinded = true; Pump(window.PendingCompute); tabs.SelectedIndex = 2; Layout(window);
            foreach (var text in Find<TextBlock>(window)) { Assert.DoesNotContain("PRIVATE", text.Text); Assert.DoesNotContain("informative", text.Text); Assert.DoesNotMatch(@"\b(WT|KO)\b", text.Text); }
            foreach (var element in Find<FrameworkElement>(window)) if (element.ToolTip is string tip) { Assert.DoesNotContain("MX", tip); Assert.DoesNotMatch(@"\b(WT|KO)\b", tip); }
            var exportPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(store.DatabasePath)!, "temporal-blinded.csv");
            TemporalProfileExport.Save(exportPath, window.TemporalResult!, window.Result!.Plan, true, window.Snapshot.Revision);
            var exported = File.ReadAllText(exportPath); Assert.DoesNotContain("PRIVATE", exported); Assert.DoesNotContain("MX", exported); Assert.DoesNotMatch(@"\b(WT|KO)\b", exported);
            Assert.True(window.TryUnblind("Reviewer", "Reasoned temporal identity review")); Pump(window.PendingCompute);
            var independent = plan with { UnitDesign = StudyUnitDesign.IndependentAnimals }; plans.Save(independent, "Reviewer", "Independent cohort interpretation", window.ExpectedPlanRevision); window.Reload(); Pump(window.PendingCompute); tabs.SelectedIndex = 2; Layout(window);
            Assert.False(window.TemporalResult!.ConnectWithinAnimal); Assert.DoesNotContain(Find<Line>(window), line => Equals(line.Tag, "within-mouse-day-line")); Assert.Contains("Independent cohorts", window.TemporalResult.Interpretation);
            var unknown = independent with { UnitDesign = StudyUnitDesign.Unknown, DesignConfirmed = false }; plans.Save(unknown, "Reviewer", "Unknown design remains descriptive", window.ExpectedPlanRevision); window.Reload(); Pump(window.PendingCompute);
            Assert.NotEmpty(window.Result!.Study.Groups); Assert.False(window.TemporalResult!.ConnectWithinAnimal); Assert.Contains("learning and inference are withheld", window.TemporalResult.Interpretation);
            evidence.Add("PASS actual prespecified temporal/across-day controls retain repeated mouse identity, independent cohorts are not connected, unknown design permits descriptive maps while learning/inference stay held; source bin5..6 reaches independent pixels/marker and indeterminate visits are not successes; short labels/tooltips stay opaque.");
        }
        finally { window.Close(); }
        var delayed = new DelayedCalculator { Context = "PRIVATE-day-1" }; var pending = new ExperimentWindow(store.DatabasePath, false, false, delayed);
        try
        {
            WaitEntered(delayed); Assert.True(pending.Busy); Assert.Null(pending.Result);
            var heartbeat = false; pending.Dispatcher.BeginInvoke(() => heartbeat = true); Pump(Task.Delay(30)); Assert.True(heartbeat);
            var firstTask = pending.PendingCompute; pending.SelectContext("PRIVATE-day-2"); Assert.Null(pending.Result); delayed.Release.Set(); Pump(pending.PendingCompute); Pump(firstTask);
            Assert.Equal("PRIVATE-day-2", pending.Result!.Context.Id);
            delayed.Entered.Reset(); delayed.Release.Reset(); pending.SelectContext("PRIVATE-day-1"); WaitEntered(delayed);
            var changed = unknownPlan(plans.Latest()!.Plan); plans.Save(changed, "Reviewer", "Changed during blocked computation", pending.ExpectedPlanRevision);
            delayed.Release.Set(); Pump(pending.PendingCompute); Assert.True(pending.Held); Assert.Null(pending.Result); Assert.Null(pending.TemporalResult);
            delayed.Context = null; pending.Reload(); Pump(pending.PendingCompute); Assert.NotNull(pending.Result);
            delayed.Context = "PRIVATE-day-1"; delayed.Entered.Reset(); delayed.Release.Reset(); pending.SelectContext("PRIVATE-day-1"); WaitEntered(delayed);
            var bytes = File.ReadAllBytes(assets[0].Path); bytes[^1] ^= 1; File.WriteAllBytes(assets[0].Path, bytes); delayed.Release.Set(); Pump(pending.PendingCompute);
            Assert.True(pending.Held); Assert.Null(pending.Result); bytes[^1] ^= 1; File.WriteAllBytes(assets[0].Path, bytes);
            delayed.Context = null; pending.Reload(); Pump(pending.PendingCompute); Assert.NotNull(pending.Result);
            delayed.Context = "PRIVATE-day-1"; delayed.Entered.Reset(); delayed.Release.Reset(); pending.SelectContext("PRIVATE-day-1"); WaitEntered(delayed);
            Find<Button>(pending).Single(button => Equals(button.Content, "Cancel calculation")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(pending.Held); Assert.Null(pending.Result); delayed.Release.Set(); Pump(pending.PendingCompute); Assert.False(pending.Busy); Assert.True(pending.Held); Assert.Null(pending.TemporalResult);
            delayed.Context = null; pending.Reload(); Pump(pending.PendingCompute); Assert.NotNull(pending.Result);
            evidence.Add("PASS actual delayed calculator leaves dispatcher responsive; context change wins over obsolete result; plan/source changes DURING work clear/hold result instead of capturing a changed post-stamp as current; explicit reload restores current view.");
        }
        finally { delayed.Release.Set(); pending.Close(); Pump(pending.PendingCompute); delayed.Entered.Dispose(); delayed.Release.Dispose(); }
        static StudyPlan unknownPlan(StudyPlan value) => value with { Title = value.Title + " revised" };
    }
    private static void WaitEntered(DelayedCalculator calculator)
    { for (var i = 0; i < 200 && !calculator.Entered.IsSet; i++) Pump(Task.Delay(10)); Assert.True(calculator.Entered.IsSet); }
    private static IEnumerable<T> Find<T>(Window window) where T : DependencyObject => Descendants((DependencyObject)window.Content).OfType<T>();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Pixels(BitmapSource source, int recording, int frame)
    { var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0); var pixel = new byte[4]; image.CopyPixels(new Int32Rect(10, 10, 1, 1), pixel, 4, 0); Assert.InRange(Math.Abs(pixel[0] - (20 + 3 * frame + recording)), 0, 1); Assert.InRange(Math.Abs(pixel[1] - (40 + 3 * frame)), 0, 1); Assert.InRange(Math.Abs(pixel[2] - (230 - 3 * frame)), 0, 1); }
    private static void Layout(Window window)
    { var root = (FrameworkElement)window.Content; root.Measure(new Size(1050, 700)); root.Arrange(new Rect(0, 0, 1050, 700)); root.UpdateLayout(); Pump(Task.Delay(30)); root.UpdateLayout(); }
    private static void Pump(Task task)
    { var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false)); timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Assert.True(task.IsCompleted); task.GetAwaiter().GetResult(); }
}
