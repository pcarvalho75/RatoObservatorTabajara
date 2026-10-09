using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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

namespace MouseTrackerTabajara.Validation.Tests;

public class VirtualizedValidationViewTests(ITestOutputHelper output)
{
    private static readonly string EvidenceFolder = Path.Combine(Repository(), ".workmap", "logs", "G09-validation-gui", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
    static VirtualizedValidationViewTests() => Directory.CreateDirectory(EvidenceFolder);

    [Fact]
    public void MinimumWindowShowsCompactRolesAndExactSelectedFrames() => Run(MinimumWindow);

    [Fact]
    public void ManyRowsRecycleAndSelectedSourceFrameOpens() => Run(ManyRows);

    [Fact]
    public void ReblindCancelAndCloseDoNotPublishStaleOrPrivateResults() => Run(CancelAndClose);

    private void MinimumWindow(List<string> evidence)
    {
        using var study = new SyntheticStudy();
        var development = study.Add("PRIVATE-independent-looking.avi", 8, 20, animal: "ANIMAL-SECRET", group: "WT");
        var excluded = study.Add("PRIVATE-excluded.avi", 8, 50, included: false, exclusion: "EXCLUSION-SECRET", animal: "ANIMAL-SECRET", group: "KO");
        var independent = study.Add("PRIVATE-development-looking.avi", 8, 90, animal: "ANIMAL-SECRET", group: "WT");
        var unspecified = study.Add("PRIVATE-unspecified.avi", 8, 140, animal: "ANIMAL-SECRET", group: "KO");
        study.Store.SaveCorrection(study.Runs.Single(run => run.VideoId == independent.Id), new(study.Runs.Single(run => run.VideoId == independent.Id).Id, 0, 70, 50, "Synthetic manual repair", DateTime.UtcNow));
        var visibleX = new Dictionary<string, int>(StringComparer.Ordinal) { [development.Id] = 40, [excluded.Id] = 40, [independent.Id] = 70, [unspecified.Id] = 55 };
        var window = new IndependentValidationWindow(study.Store.DatabasePath, false);
        try
        {
            Layout(window);
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
            SetNumber(window, "Random frames", "8");
            SetNumber(window, "Difficult intervals", "1");
            SetNumber(window, "Interval frames", "4");
            SetNumber(window, "Second review fraction", "0");
            SetNumber(window, "Position tolerance px", "5");
            Click(window, "Save split and tolerance plan");
            Assert.True(window.Snapshot.Plan is not null, window.StatusText);
            Click(window, "Generate disclosed frozen samples");
            Assert.True(window.Snapshot.Sampling is not null, window.StatusText);
            var hash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(window.Snapshot.Sampling));
            var history = new ValidationHistoryStore(study.Store.DatabasePath);
            foreach (var asset in new[] { development, excluded, independent, unspecified }) study.AnnotatePattern(history, hash, asset, visibleX[asset.Id]);
            Tabs(window).SelectedIndex = 2;
            Layout(window);
            Click(window, "Compare frozen reference and producer");
            Pump(window.PendingComparison);
            Assert.NotNull(window.Report);
            Assert.Equal(11.25, window.Report.MeanRandomPositionErrorPixels);
            Assert.Equal(30, Role(window.Report, ValidationSplit.IndependentValidation).MeanRandomPositionErrorPixels);
            Assert.Equal(0, Role(window.Report, ValidationSplit.Development).MeanRandomPositionErrorPixels);
            var rawFrame = window.Report.Frames.Single(frame => frame.VideoId == independent.Id && frame.Frame == 0);
            Assert.Equal(30, rawFrame.PositionErrorPixels);
            Present(window, root =>
            {
                var rootBox = (FrameworkElement)root;
                foreach (var role in new[] { "Development", "Independent validation", "Unspecified" })
                {
                    var card = Named(root, ValidationViewNames.RoleCardPrefix + role);
                    var bounds = Bounds(card, rootBox);
                    Assert.True(bounds.Height > 24, role + " " + bounds);
                    Assert.True(bounds.Top >= 0 && bounds.Bottom <= rootBox.ActualHeight + 0.5, role + " bottom " + bounds.Bottom + " root " + rootBox.ActualHeight);
                }
                var pooled = Named(root, ValidationViewNames.PooledDescriptor);
                var pooledBounds = Bounds(pooled, rootBox);
                var cardBottom = new[] { "Development", "Independent validation", "Unspecified" }.Max(role => Bounds(Named(root, ValidationViewNames.RoleCardPrefix + role), rootBox).Bottom);
                Assert.True(pooledBounds.Top >= cardBottom - 1, "pooled " + pooledBounds.Top + " cards " + cardBottom);
                Assert.True(pooledBounds.Bottom <= rootBox.ActualHeight + 0.5, "pooled bottom " + pooledBounds.Bottom);
                var list = List(root);
                var listViewport = (FrameworkElement)root;
                var listBounds = Bounds(list, listViewport);
                Assert.InRange(list.ActualHeight, 48, listViewport.ActualHeight);
                Assert.True(listBounds.Top >= -0.5 && listBounds.Bottom <= listViewport.ActualHeight + 0.5,
                    $"Results list {listBounds} must fit viewport {listViewport.ActualWidth}x{listViewport.ActualHeight}.");
                var realized = Items(list).Count();
                Assert.InRange(realized, 1, 24);
                Assert.True(realized < list.Items.Count, "realized " + realized + " of " + list.Items.Count);
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
                Assert.DoesNotContain(Visuals(root).OfType<Button>(), button => button.Content is "●" or "?");
                Assert.Single(Visuals(root).OfType<Expander>());
                var visual = string.Join("\n", Texts(Visuals(root)));
                Assert.Contains("Declared role Independent validation", visual, StringComparison.Ordinal);
                Assert.Contains("30 px", visual, StringComparison.Ordinal);
                Assert.Contains("11.25 px", visual, StringComparison.Ordinal);
                Assert.Contains("mean 0 px", CardText(root, "Development"), StringComparison.Ordinal);
                Assert.Contains("RAW AUTOMATIC", visual, StringComparison.Ordinal);
                Assert.Contains("not held-out accuracy", visual, StringComparison.Ordinal);
                Assert.Contains("not animal N", visual, StringComparison.Ordinal);
                Assert.Contains("ALLOC-SECRET", visual, StringComparison.Ordinal);
                Assert.Contains("EXCLUSION-SECRET", visual, StringComparison.Ordinal);
                Assert.Contains("PRIVATE", visual, StringComparison.Ordinal);
                Assert.DoesNotContain("exclusion does not delete them", visual, StringComparison.Ordinal);
                Assert.DoesNotContain("G4", visual, StringComparison.Ordinal);
                Assert.DoesNotContain("H4", visual, StringComparison.Ordinal);
                var expander = Visuals(root).OfType<Expander>().Single();
                Assert.False(expander.IsExpanded);
                var details = string.Join("\n", Texts(Logical((DependencyObject)expander.Content)));
                Assert.Equal(1, details.Split("exclusion does not delete them").Length - 1);
                Assert.Contains("missing references", details, StringComparison.Ordinal);
                expander.IsExpanded = true;
                expander.ApplyTemplate();
                rootBox.UpdateLayout();
                Pump(Task.Delay(40));
                Assert.Contains("exclusion does not delete them", string.Join("\n", Texts(Visuals(expander))), StringComparison.Ordinal);
                expander.IsExpanded = false;
                rootBox.UpdateLayout();
                evidence.Add("minimum window cards within " + rootBox.ActualWidth + "x" + rootBox.ActualHeight + "; realized " + realized + "/" + list.Items.Count + "; list height " + list.ActualHeight.ToString("0.#", CultureInfo.InvariantCulture));
            }, Path.Combine(EvidenceFolder, "synthetic-validation-unblinded-1050x700.png"));
            Select(window, independent.Id, 1);
            Assert.Contains("missing reference", Detail(window), StringComparison.Ordinal);
            Assert.Contains("Position error held", Detail(window), StringComparison.Ordinal);
            Assert.DoesNotContain("Position error 0 px", Detail(window), StringComparison.Ordinal);
            Select(window, independent.Id, 2);
            Assert.Contains("unresolved disagreement", Detail(window), StringComparison.Ordinal);
            Assert.Contains("Disagreement True", Detail(window), StringComparison.Ordinal);
            Assert.Contains("Position error held", Detail(window), StringComparison.Ordinal);
            var disputed = window.Report.Frames.Single(frame => frame.VideoId == independent.Id && frame.Frame == 2);
            Assert.Equal(2, disputed.ReferenceIds.Length);
            Assert.Contains(string.Join(", ", disputed.ReferenceIds), Detail(window), StringComparison.Ordinal);
            Select(window, independent.Id, 3);
            Assert.Contains("false accepted", Detail(window), StringComparison.Ordinal);
            Assert.Contains("False accepted True", Detail(window), StringComparison.Ordinal);
            Select(window, independent.Id, 4);
            Assert.Contains("ambiguous", Detail(window), StringComparison.Ordinal);
            Assert.Contains("Position error held", Detail(window), StringComparison.Ordinal);
            Select(window, excluded.Id, 0);
            Assert.Contains("EXCLUSION-SECRET", Detail(window), StringComparison.Ordinal);
            Assert.Contains("excluded", Detail(window), StringComparison.Ordinal);
            Select(window, independent.Id, 0);
            Assert.Contains("Position error 30 px", Detail(window), StringComparison.Ordinal);
            Click(window, "Save replayable comparison report");
            Assert.Contains("Frozen comparison report saved", window.StatusText, StringComparison.Ordinal);
            var savedJson = new ValidationHistoryStore(study.Store.DatabasePath).History().Last(entry => entry.Kind == "report").Json;
            Assert.Contains("\"split_summaries\"", savedJson, StringComparison.Ordinal);
            window.Blinded = true;
            Layout(window);
            AssertNoSecrets(window);
            Assert.Contains("Declared role Independent validation", string.Join("\n", Texts(Logical(window))), StringComparison.Ordinal);
            Assert.Contains("30 px", string.Join("\n", Texts(Visuals(window))), StringComparison.Ordinal);
            Assert.DoesNotContain("Independent first reference is missing", string.Join("\n", Texts(Logical(window))), StringComparison.Ordinal);
            Assert.DoesNotContain("Reference visibility/position is ambiguous", string.Join("\n", Texts(Logical(window))), StringComparison.Ordinal);
            Present(window, _ => { }, Path.Combine(EvidenceFolder, "synthetic-validation-blinded-1050x700.png"));
            window.ReviewerInput.Text = "Reviewer";
            window.ReasonInput.Text = "Reasoned reveal after blinded split summary";
            window.Blinded = false;
            Assert.False(window.Blinded);
            var legacyNode = JsonNode.Parse(savedJson)!.AsObject();
            legacyNode.Remove("split_summaries");
            legacyNode["mean_random_position_error_pixels"] = 999;
            new ValidationHistoryStore(study.Store.DatabasePath).Save("report", Guid.NewGuid().ToString("N"), legacyNode.ToJsonString(), "Reviewer", "Legacy comparison without role summaries", 0);
            Tabs(window).SelectedIndex = 2;
            Click(window, "Replay last saved comparison");
            Assert.Null(window.Report);
            Assert.Contains("absent", window.StatusText, StringComparison.Ordinal);
            Pump(window.PendingComparison);
            Assert.Equal(11.25, window.Report!.MeanRandomPositionErrorPixels);
            var replayed = string.Join("\n", Texts(Logical(window)));
            Assert.Contains(ValidationSplitSummaryView.RecomputedAbsentSummaries, replayed, StringComparison.Ordinal);
            Assert.DoesNotContain("999", replayed, StringComparison.Ordinal);
            Assert.Contains("Declared role Independent validation", replayed, StringComparison.Ordinal);
            window.ReviewerInput.Text = "Reviewer";
            window.ReasonInput.Text = "Prespecified corrected target";
            TargetBox(window).SelectedIndex = 1;
            Click(window, "Save split and tolerance plan");
            Assert.Equal(ValidationMeasurementTarget.InspectedCorrected, window.Snapshot.Plan!.MeasurementTarget);
            Click(window, "Generate disclosed frozen samples");
            var correctedHash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(window.Snapshot.Sampling));
            Assert.NotEqual(hash, correctedHash);
            foreach (var asset in new[] { development, excluded, independent, unspecified }) study.AnnotatePattern(history, correctedHash, asset, visibleX[asset.Id]);
            Tabs(window).SelectedIndex = 2;
            Click(window, "Compare frozen reference and producer");
            Pump(window.PendingComparison);
            Assert.Equal(ValidationMeasurementTarget.InspectedCorrected, window.Report!.MeasurementTarget);
            Assert.Equal(0, window.Report.Frames.Single(frame => frame.VideoId == independent.Id && frame.Frame == 0).PositionErrorPixels);
            Assert.Contains("INSPECTED CORRECTED", string.Join("\n", Texts(Visuals(window))), StringComparison.Ordinal);
            Assert.Contains("not raw automatic tracker accuracy", string.Join("\n", Texts(Visuals(window))), StringComparison.Ordinal);
            Select(window, independent.Id, 0);
            Assert.Contains("Position error 0 px", Detail(window), StringComparison.Ordinal);
            Assert.Equal(40, study.Store.LatestRun(independent.Id)!.Points.Single(point => point.Frame == 0).X);
            AssertNoRoadmap(window);
            evidence.Add("PASS compact roles at 1050x700, exact missing/disputed/excluded/raw 30 px and corrected 0 px, blinded tree, legacy replay without 999. Evidence " + EvidenceFolder);
        }
        finally { window.Close(); }
    }

    private void ManyRows(List<string> evidence)
    {
        using var study = new SyntheticStudy();
        var asset = study.Add("synthetic-many-rows.avi", 200, 30);
        var window = new IndependentValidationWindow(study.Store.DatabasePath, false);
        try
        {
            Layout(window);
            var draft = Assert.Single(window.Draft);
            draft.Session = "synthetic-session";
            draft.ProtocolIdentity = "synthetic-protocol";
            draft.Allocation = "synthetic-allocation";
            draft.Split = ValidationSplit.Development;
            draft.UsedForTuning = true;
            window.RecordingsInput.Items.Refresh();
            window.SyntheticInput.IsChecked = true;
            window.ReviewerInput.Text = "Reviewer";
            window.ReasonInput.Text = "Many-row synthetic sample";
            SetNumber(window, "Random frames", "200");
            SetNumber(window, "Difficult intervals", "0");
            SetNumber(window, "Second review fraction", "0");
            Click(window, "Save split and tolerance plan");
            Click(window, "Generate disclosed frozen samples");
            Assert.NotNull(window.Snapshot.Sampling);
            var hash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(window.Snapshot.Sampling));
            var history = new ValidationHistoryStore(study.Store.DatabasePath);
            for (var frame = 0; frame < asset.Metadata.FrameCount; frame++) study.SaveReference(history, hash, asset, frame, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40);
            Tabs(window).SelectedIndex = 2;
            Click(window, "Compare frozen reference and producer");
            Pump(window.PendingComparison);
            Assert.NotNull(window.Report);
            Assert.Equal(200, window.Report.Frames.Length);
            int realizedTop = 0, realizedBottom = 0;
            bool recycled = false;
            Present(window, root =>
            {
                var list = List(root);
                var listViewport = (FrameworkElement)root;
                var listBounds = Bounds(list, listViewport);
                Assert.InRange(list.ActualHeight, 48, listViewport.ActualHeight);
                Assert.True(listBounds.Top >= -0.5 && listBounds.Bottom <= listViewport.ActualHeight + 0.5,
                    $"Results list {listBounds} must fit viewport {listViewport.ActualWidth}x{listViewport.ActualHeight}.");
                list.ScrollIntoView(list.Items[0]);
                ((FrameworkElement)root).UpdateLayout();
                var top = Items(list).ToList();
                realizedTop = top.Count;
                list.ScrollIntoView(list.Items[list.Items.Count - 1]);
                ((FrameworkElement)root).UpdateLayout();
                Pump(Task.Delay(30));
                var bottom = Items(list).ToList();
                realizedBottom = bottom.Count;
                recycled = bottom.Any(item => top.Contains(item));
                list.SelectedItem = list.Items.OfType<ValidationFrameRow>().Single(row => row.Frame == 180);
                list.ScrollIntoView(list.SelectedItem);
                ((FrameworkElement)root).UpdateLayout();
            }, Path.Combine(EvidenceFolder, "synthetic-validation-many-rows-1050x700.png"));
            Assert.InRange(realizedTop, 1, 24);
            Assert.InRange(realizedBottom, 1, 24);
            Assert.True(realizedTop < 200 && realizedBottom < 200);
            Assert.True(recycled, "top " + realizedTop + " bottom " + realizedBottom);
            Layout(window);
            window.PixelDisclosureInput.IsChecked = true;
            Click(window, "Open failure/reference original frame");
            Pump(window.PendingFrame);
            Assert.Equal(180, window.DisplayedFrame);
            Assert.Equal(asset.Id, window.DisplayedVideoId);
            Assert.NotNull(window.DisplayedBitmap);
            evidence.Add("PASS 200-row recycling top " + realizedTop + " bottom " + realizedBottom + "; opened exact source frame 180. Evidence " + EvidenceFolder);
        }
        finally { window.Close(); }
    }

    private static void CancelAndClose(List<string> evidence)
    {
        using var study = new SyntheticStudy();
        var asset = study.Add("PRIVATE-cancel-WT.avi", 6, 80, animal: "ANIMAL-SECRET", group: "KO");
        var window = new IndependentValidationWindow(study.Store.DatabasePath, false);
        var closed = false;
        try
        {
            Layout(window);
            var draft = Assert.Single(window.Draft);
            draft.Session = "PRIVATE-session";
            draft.ProtocolIdentity = "PROTOCOL-SECRET";
            draft.Allocation = "KO";
            draft.Split = ValidationSplit.Development;
            draft.UsedForTuning = true;
            window.RecordingsInput.Items.Refresh();
            window.SyntheticInput.IsChecked = true;
            window.ReviewerInput.Text = "Reviewer";
            window.ReasonInput.Text = "Cancellation synthetic plan";
            SetNumber(window, "Random frames", "6");
            SetNumber(window, "Difficult intervals", "0");
            SetNumber(window, "Second review fraction", "0");
            Click(window, "Save split and tolerance plan");
            Click(window, "Generate disclosed frozen samples");
            var hash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(window.Snapshot.Sampling));
            var history = new ValidationHistoryStore(study.Store.DatabasePath);
            for (var frame = 0; frame < 6; frame++) study.SaveReference(history, hash, asset, frame, ReferenceReviewerLane.First, "First reviewer", ReferenceVisibility.Visible, 40);
            Tabs(window).SelectedIndex = 2;
            Click(window, "Compare frozen reference and producer");
            Pump(window.PendingComparison);
            Assert.NotNull(window.Report);
            Assert.Contains("PRIVATE", string.Join("\n", Texts(Visuals(window))), StringComparison.Ordinal);
            Click(window, "Compare frozen reference and producer");
            window.Blinded = true;
            Pump(window.PendingComparison);
            Assert.NotNull(window.Report);
            Assert.False(window.Held);
            AssertNoSecrets(window);
            Click(window, "Compare frozen reference and producer");
            Click(window, "Cancel comparison");
            Pump(window.PendingComparison);
            Assert.True(window.Held);
            Assert.Null(window.Report);
            Assert.DoesNotContain("PRIVATE", string.Join("\n", Texts(Logical(window))), StringComparison.Ordinal);
            window.Reload();
            window.ReviewerInput.Text = "Reviewer";
            window.ReasonInput.Text = "Close during comparison";
            Click(window, "Compare frozen reference and producer");
            Assert.Null(window.Report);
            var pending = window.PendingComparison;
            window.Close();
            closed = true;
            Pump(pending);
            Assert.Null(window.Report);
            evidence.Add("PASS reblind published without private labels; cancel and close left no comparison result.");
        }
        finally { if (!closed) window.Close(); }
    }

    private void Run(Action<List<string>> body)
    {
        Exception? error = null;
        var lines = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                body(lines);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)));
        foreach (var line in lines) output.WriteLine(line);
        File.AppendAllLines(Path.Combine(EvidenceFolder, "receipt.txt"), lines);
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static ValidationSplitSummary Role(ValidationComparisonReport report, ValidationSplit role)
    {
        Assert.True(ValidationSplitSummaries.TryRead(report, out var summaries, out var hold), hold);
        return summaries.Single(summary => summary.Role == role);
    }

    private static void Present(Window window, Action<DependencyObject> inspect, string path)
    {
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        var parameters = new HwndSourceParameters("g09-synthetic-validation") { WindowStyle = 0, ExtendedWindowStyle = 0x08000000, PositionX = -32000, PositionY = -32000, Width = 1050, Height = 700 };
        using var hwnd = new HwndSource(parameters) { RootVisual = root };
        try
        {
            root.Measure(new System.Windows.Size(1050, 700));
            root.Arrange(new System.Windows.Rect(0, 0, 1050, 700));
            root.UpdateLayout();
            Pump(Task.Delay(60));
            root.UpdateLayout();
            inspect(root);
            var bitmap = new RenderTargetBitmap(1050, 700, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            hwnd.RootVisual = null;
            window.Content = root;
        }
    }

    private static void AssertNoSecrets(Window window)
    {
        foreach (var element in Visuals(window).Concat(Logical(window)).OfType<FrameworkElement>())
        {
            var text = element switch { TextBlock block => block.Text, TextBox box => box.Text, _ => "" };
            Assert.DoesNotContain("PRIVATE", text);
            Assert.DoesNotContain("EXCLUSION-SECRET", text);
            Assert.DoesNotContain("ALLOC-SECRET", text);
            Assert.DoesNotContain("SESSION-SECRET", text);
            Assert.DoesNotContain("PROTOCOL-SECRET", text);
            Assert.DoesNotContain("ANIMAL-SECRET", text);
            Assert.DoesNotMatch(@"\b(WT|KO)\b", text);
            if (element.ToolTip is string tip)
            {
                Assert.DoesNotContain("PRIVATE", tip);
                Assert.DoesNotContain("EXCLUSION-SECRET", tip);
                Assert.DoesNotMatch(@"\b(WT|KO)\b", tip);
            }
        }
    }

    private static void AssertNoRoadmap(Window window)
    {
        var text = string.Join("\n", Texts(Visuals(window)).Concat(Texts(Logical(window))));
        Assert.DoesNotContain("G4", text, StringComparison.Ordinal);
        Assert.DoesNotContain("H4", text, StringComparison.Ordinal);
        Assert.DoesNotContain("roadmap", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string CardText(DependencyObject root, string role) => Visuals(Named(root, ValidationViewNames.RoleCardPrefix + role)).OfType<TextBlock>().Single().Text;
    private static string Detail(Window window) => string.Join("\n", Texts(Visuals(Named(window, ValidationViewNames.SelectedDetail))));
    private static void Select(Window window, string videoId, int frame)
    {
        var list = List(window);
        list.SelectedItem = list.Items.OfType<ValidationFrameRow>().Single(row => row.VideoId == videoId && row.Frame == frame);
        list.ScrollIntoView(list.SelectedItem);
        list.UpdateLayout();
    }
    private static ValidationResultListView List(DependencyObject root) => Visuals(root).OfType<ValidationResultListView>().Single();
    private static IEnumerable<ListBoxItem> Items(ListBox list) => Visuals(list).OfType<ListBoxItem>();
    private static FrameworkElement Named(DependencyObject root, string name) => Visuals(root).OfType<FrameworkElement>().Single(element => AutomationProperties.GetName(element) == name);
    private static System.Windows.Rect Bounds(FrameworkElement element, FrameworkElement root)
    {
        var origin = element.TransformToAncestor(root).Transform(new System.Windows.Point(0, 0));
        return new System.Windows.Rect(origin, element.RenderSize);
    }
    private static ComboBox TargetBox(Window window) => Logical(window).OfType<ComboBox>().Single(box => box.Items.Cast<object>().Any(item => item.ToString()!.Contains("Raw automatic", StringComparison.Ordinal)));
    private static TabControl Tabs(Window window) => Logical(window).OfType<TabControl>().Single();
    private static void SetNumber(Window window, string key, string value) => Logical(window).OfType<TextBox>().Single(box => Equals(box.Tag, key)).Text = value;
    private static void Click(Window window, string caption) => Logical(window).OfType<Button>().Single(button => Equals(button.Content, caption)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<string> Texts(IEnumerable<DependencyObject> nodes) => nodes.OfType<TextBlock>().Select(block => block.Text);
    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        if (parent is Window window && window.Content is DependencyObject content && VisualTreeHelper.GetChildrenCount(window) == 0) parent = content;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Visuals(child)) yield return nested;
        }
    }
    private static IEnumerable<DependencyObject> Logical(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (var nested in Logical(node)) yield return nested;
        }
    }
    private static void Layout(Window window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new System.Windows.Size(1050, 700));
        root.Arrange(new System.Windows.Rect(0, 0, 1050, 700));
        root.UpdateLayout();
        Pump(Task.Delay(30));
        root.UpdateLayout();
    }
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.True(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "MouseTrackerTabajara.Desktop"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Synthetic proof repository unavailable.");
    }

    private sealed class SyntheticStudy : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "g09-validation-view-" + Guid.NewGuid().ToString("N"));
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
                    using var image = new Mat(100, 100, MatType.CV_8UC3, new Scalar(blue, 30 + frame % 200, 20 + frame % 40));
                    Cv2.Circle(image, new OpenCvSharp.Point(40, 50), 4, Scalar.Black, -1);
                    writer.Write(image);
                }
            }
            var imported = new WorkspaceService(Store).Import(path);
            var asset = imported with { AnimalId = animal, AnimalIdConfirmed = false, GroupName = group, Included = included, ExclusionReason = exclusion, Settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .4, Ry = .4, Platform = new(.5, .5, .04), ArenaConfirmed = true } };
            Store.SaveVideo(asset);
            var points = Enumerable.Range(0, frames).Select(frame => new TrackPoint(frame, frame / asset.Metadata.Fps, 40, 50, PointStatus.Tracked, 1)).ToList();
            var run = new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = asset.Settings, Points = points, Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings), AlgorithmContract = AlgorithmIdentity.Contract, NominalTimingVerified = true };
            Store.SaveRun(run);
            Assets.Add(asset);
            Runs.Add(run);
            return asset;
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
