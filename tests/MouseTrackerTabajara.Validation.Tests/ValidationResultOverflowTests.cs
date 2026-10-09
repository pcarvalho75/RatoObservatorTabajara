using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
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

public class ValidationResultOverflowTests(ITestOutputHelper output)
{
    private const int Sources = 30;
    private const int FramesPerSource = 8;
    private static readonly string EvidenceFolder = Path.Combine(Repository(), ".workmap", "logs", "G09-validation-gui", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + "-overflow");

    [Fact]
    public void ExpandedThirtySourceEndpointsStayReachableAtMinimumWindow()
    {
        Directory.CreateDirectory(EvidenceFolder);
        Exception? error = null;
        var lines = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                Prove(lines);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(4)));
        foreach (var line in lines) output.WriteLine(line);
        File.AppendAllLines(Path.Combine(EvidenceFolder, "receipt.txt"), lines);
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Prove(List<string> evidence)
    {
        using var study = new SyntheticStudy();
        var assets = new List<VideoAsset>();
        for (var source = 0; source < Sources; source++)
            assets.Add(study.Add($"synthetic-overflow-{source:00}.avi", FramesPerSource, (byte)(20 + source)));
        var window = new IndependentValidationWindow(study.Store.DatabasePath, false);
        try
        {
            Layout(window);
            for (var source = 0; source < window.Draft.Count; source++)
            {
                var draft = window.Draft[source];
                var split = source % 3 == 0 ? ValidationSplit.Development : source % 3 == 1 ? ValidationSplit.IndependentValidation : ValidationSplit.Unspecified;
                draft.Session = "synthetic-session-" + source.ToString(CultureInfo.InvariantCulture);
                draft.ProtocolIdentity = "synthetic-protocol";
                draft.Allocation = "synthetic-allocation-" + (source % 2).ToString(CultureInfo.InvariantCulture);
                draft.Split = split;
                draft.UsedForTuning = split == ValidationSplit.Development;
                draft.IndependenceConfirmed = split == ValidationSplit.IndependentValidation;
            }
            window.RecordingsInput.Items.Refresh();
            window.SyntheticInput.IsChecked = true;
            window.ReviewerInput.Text = "Reviewer";
            window.ReasonInput.Text = "Thirty-source endpoint overflow";
            SetNumber(window, "Random frames", "1");
            SetNumber(window, "Difficult intervals", "3");
            SetNumber(window, "Interval frames", "2");
            SetNumber(window, "Second review fraction", "0");
            Click(window, "Save split and tolerance plan");
            Assert.True(window.Snapshot.Plan is not null, window.StatusText);
            Click(window, "Generate disclosed frozen samples");
            var sampling = window.Snapshot.Sampling ?? throw new InvalidOperationException(window.StatusText);
            var hash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(sampling));
            var history = new ValidationHistoryStore(study.Store.DatabasePath);
            foreach (var key in sampling.Cases.SelectMany(sample => Enumerable.Range(sample.StartFrame, sample.EndFrame - sample.StartFrame + 1).Select(frame => (sample.VideoId, Frame: frame))).Distinct())
                study.SaveReference(history, hash, assets.Single(asset => asset.Id == key.VideoId), key.Frame, 32);
            Tabs(window).SelectedIndex = 2;
            Click(window, "Compare frozen reference and producer");
            Pump(window.PendingComparison);
            var compared = window.Report ?? throw new InvalidOperationException(window.StatusText);
            Assert.Equal(Sources * 3, compared.Endpoints.Length);
            Assert.True(compared.Frames.Length > 40, "frames " + compared.Frames.Length);
            window.ReviewerInput.Text = "Reviewer";
            window.ReasonInput.Text = "Synthetic pixel disclosure";
            window.PixelDisclosureInput.IsChecked = true;
            Host(window, root =>
            {
                var report = window.Report!;
                var expander = Expander(root);
                Assert.False(expander.IsExpanded);
                expander.IsExpanded = true;
                root.UpdateLayout();
                Pump(Task.Delay(40));
                root.UpdateLayout();
                var scroll = SummaryScroll(expander, List(root));
                Assert.True(double.IsFinite(scroll.ViewportHeight) && scroll.ViewportHeight >= 36, "viewport " + scroll.ViewportHeight);
                Assert.True(double.IsFinite(scroll.ExtentHeight) && scroll.ExtentHeight > scroll.ViewportHeight + 20, "extent " + scroll.ExtentHeight + " viewport " + scroll.ViewportHeight);
                Assert.True(scroll.ScrollableHeight > 0);
                var scrollBounds = Bounds(scroll, root);
                Assert.True(scrollBounds.Top >= -0.5 && scrollBounds.Bottom <= root.ActualHeight + 0.5, "summary " + scrollBounds.Bottom + " root " + root.ActualHeight);
                scroll.ScrollToVerticalOffset(0);
                root.UpdateLayout();
                foreach (var role in new[] { "Development", "Independent validation", "Unspecified" })
                {
                    var card = Bounds(Named(root, ValidationViewNames.RoleCardPrefix + role), root);
                    Assert.True(card.Top >= -0.5 && card.Bottom <= root.ActualHeight + 0.5 && card.Height > 24, role + " " + card);
                }
                var pooled = Bounds(Named(root, ValidationViewNames.PooledDescriptor), root);
                Assert.True(pooled.Top >= -0.5 && pooled.Bottom <= root.ActualHeight + 0.5, "pooled " + pooled);
                var list = List(root);
                var expandedList = Bounds(list, root);
                AssertFiniteList(list, root, expandedList);
                list.ScrollIntoView(list.Items[0]);
                root.UpdateLayout();
                var topContainers = Items(list).ToList();
                list.ScrollIntoView(list.Items[list.Items.Count - 1]);
                root.UpdateLayout();
                Pump(Task.Delay(30));
                var bottomContainers = Items(list).ToList();
                Assert.True(bottomContainers.Any(container => topContainers.Contains(container)), "top " + topContainers.Count + " bottom " + bottomContainers.Count);
                AssertFiniteList(list, root, Bounds(list, root));
                list.ScrollIntoView(list.SelectedItem);
                list.UpdateLayout();
                var detail = DetailScroll(root);
                var detailBounds = Bounds(detail, root);
                Assert.True(detailBounds.Top >= -0.5 && detailBounds.Bottom <= root.ActualHeight + 0.5 && detailBounds.Height > 24, "detail " + detailBounds);
                var open = OpenButton(root);
                BringFullyIntoView(open, detail, root);
                var texts = Texts(Logical((DependencyObject)expander.Content)).ToList();
                Assert.Equal(report.Endpoints.Length, texts.Count(text => text.Contains("continuous frames", StringComparison.Ordinal)));
                Assert.True(ValidationSplitSummaries.TryRead(report, out var summaries, out var hold), hold);
                foreach (var role in ValidationSplitSummaries.DeclaredRoles)
                    Assert.Contains(texts, text => text == ValidationSplitSummaryView.Counts(summaries.Single(summary => summary.Role == role)));
                foreach (var reportHold in report.Holds) Assert.Contains(texts, text => text == reportHold);
                Assert.Contains(texts, text => text == ValidationSplitSummaries.PositionDenominatorText);
                var imported = window.Snapshot.Imported.ToArray();
                foreach (var endpoint in report.Endpoints)
                {
                    var label = imported.Single(asset => asset.Id == endpoint.VideoId).Filename;
                    Assert.Contains(texts, text => text.Contains(label, StringComparison.Ordinal) && text.Contains("continuous frames " + endpoint.StartFrame.ToString(CultureInfo.InvariantCulture) + "–" + endpoint.EndFrame.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
                }
                var blocks = Logical((DependencyObject)expander.Content).OfType<TextBlock>().ToList();
                Assert.Equal(texts.Count, blocks.Count);
                foreach (var block in blocks) BringFullyIntoView(block, scroll, root);
                var afterScroll = Bounds(list, root);
                Assert.True(Math.Abs(afterScroll.Top - expandedList.Top) < 0.5 && Math.Abs(afterScroll.Height - expandedList.Height) < 0.5, "list moved " + expandedList + " -> " + afterScroll);
                AssertFiniteList(list, root, afterScroll);
                BringFullyIntoView(open, detail, root);
                var selected = (ValidationFrameRow)list.SelectedItem;
                open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window.PendingFrame);
                Assert.Equal(selected.Frame, window.DisplayedFrame);
                Assert.Equal(selected.VideoId, window.DisplayedVideoId);
                ComparisonTab(root).SelectedIndex = 2;
                root.UpdateLayout();
                Save(root, Path.Combine(EvidenceFolder, "synthetic-overflow-expanded-1050x700.png"));
                scroll.ScrollToVerticalOffset(0);
                expander.IsExpanded = false;
                root.UpdateLayout();
                var collapsedList = Bounds(list, root);
                AssertFiniteList(list, root, collapsedList);
                var other = list.Items.OfType<ValidationFrameRow>().First(row => row.Frame != selected.Frame || row.VideoId != selected.VideoId);
                list.SelectedItem = other;
                list.ScrollIntoView(other);
                list.UpdateLayout();
                var collapsedDetail = string.Join("\n", Texts(Visuals(detail)));
                Assert.Contains("exact frame " + other.Frame.ToString(CultureInfo.InvariantCulture), collapsedDetail, StringComparison.Ordinal);
                Assert.Contains("synthetic-overflow", collapsedDetail, StringComparison.Ordinal);
                BringFullyIntoView(OpenButton(root), DetailScroll(root), root);
                window.Blinded = true;
                root.UpdateLayout();
                Pump(Task.Delay(40));
                root.UpdateLayout();
                AssertBlind(root);
                list = List(root);
                var blindedRow = list.Items.OfType<ValidationFrameRow>().Single(row => row.VideoId == other.VideoId && row.Frame == other.Frame);
                list.SelectedItem = blindedRow;
                list.ScrollIntoView(blindedRow);
                list.UpdateLayout();
                var blindedDetail = string.Join("\n", Texts(Visuals(DetailScroll(root))));
                Assert.Contains("Recording ", blindedDetail, StringComparison.Ordinal);
                Assert.Contains("exact frame " + other.Frame.ToString(CultureInfo.InvariantCulture), blindedDetail, StringComparison.Ordinal);
                Assert.DoesNotContain("synthetic-overflow", blindedDetail, StringComparison.Ordinal);
                Assert.DoesNotContain(".avi", blindedDetail, StringComparison.Ordinal);
                var blindedOpen = OpenButton(root);
                BringFullyIntoView(blindedOpen, DetailScroll(root), root);
                blindedOpen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window.PendingFrame);
                Assert.Equal(other.Frame, window.DisplayedFrame);
                Assert.Equal(other.VideoId, window.DisplayedVideoId);
                ComparisonTab(root).SelectedIndex = 2;
                root.UpdateLayout();
                expander = Expander(root);
                expander.IsExpanded = true;
                root.UpdateLayout();
                Pump(Task.Delay(30));
                root.UpdateLayout();
                scroll = SummaryScroll(expander, list);
                Assert.True(scroll.ExtentHeight > scroll.ViewportHeight + 20, "blind extent " + scroll.ExtentHeight + " viewport " + scroll.ViewportHeight);
                var blindedTexts = Texts(Logical((DependencyObject)expander.Content)).ToList();
                Assert.Equal(report.Endpoints.Length, blindedTexts.Count(text => text.Contains("continuous frames", StringComparison.Ordinal)));
                Assert.All(blindedTexts, text =>
                {
                    Assert.DoesNotContain("synthetic-overflow", text, StringComparison.Ordinal);
                    Assert.DoesNotContain(".avi", text, StringComparison.Ordinal);
                });
                foreach (var endpoint in report.Endpoints)
                {
                    var index = Array.FindIndex(imported, asset => asset.Id == endpoint.VideoId) + 1;
                    var label = "Recording " + index.ToString("000", CultureInfo.InvariantCulture);
                    Assert.Contains(blindedTexts, text => text.Contains(label, StringComparison.Ordinal) && text.Contains("continuous frames " + endpoint.StartFrame.ToString(CultureInfo.InvariantCulture) + "–" + endpoint.EndFrame.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
                }
                foreach (var block in Logical((DependencyObject)expander.Content).OfType<TextBlock>()) BringFullyIntoView(block, scroll, root);
                AssertFiniteList(list, root, Bounds(list, root));
                BringFullyIntoView(OpenButton(root), DetailScroll(root), root);
                AssertBlind(root);
                Save(root, Path.Combine(EvidenceFolder, "synthetic-overflow-blind-expanded-1050x700.png"));
                evidence.Add("PASS 90 endpoint lines scroll inside " + scroll.ViewportHeight.ToString("0.#", CultureInfo.InvariantCulture) + " of extent " + scroll.ExtentHeight.ToString("0.#", CultureInfo.InvariantCulture)
                    + "; expanded list " + expandedList.Height.ToString("0.#", CultureInfo.InvariantCulture) + "; collapsed list " + collapsedList.Height.ToString("0.#", CultureInfo.InvariantCulture)
                    + "; root " + root.ActualWidth.ToString("0.#", CultureInfo.InvariantCulture) + "x" + root.ActualHeight.ToString("0.#", CultureInfo.InvariantCulture)
                    + "; frames " + report.Frames.Length + ". Evidence " + EvidenceFolder);
            });
        }
        finally { window.Close(); }
    }

    private static void AssertFiniteList(ValidationResultListView list, FrameworkElement root, System.Windows.Rect bounds)
    {
        Assert.True(bounds.Top >= -0.5 && bounds.Bottom <= root.ActualHeight + 0.5, "list " + bounds + " root " + root.ActualHeight);
        Assert.True(list.ActualHeight >= 48 && list.ActualHeight < root.ActualHeight, "list height " + list.ActualHeight);
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
        Assert.True(ScrollViewer.GetCanContentScroll(list));
        var realized = Items(list).Count();
        // Item-scroll panels realize a page beyond the visible rows. The bound stays far below the full result.
        var room = Math.Max(24, (int)Math.Ceiling(list.ActualHeight / 28d) + 16);
        Assert.True(realized >= 1 && realized <= room && realized < list.Items.Count, "realized " + realized + " room " + room + " height " + list.ActualHeight.ToString("0.#", CultureInfo.InvariantCulture) + " of " + list.Items.Count);
        DependencyObject? parent = VisualTreeHelper.GetParent(list);
        while (parent is not null)
        {
            Assert.IsNotType<ScrollViewer>(parent);
            parent = VisualTreeHelper.GetParent(parent);
        }
    }

    private static void AssertBlind(FrameworkElement root)
    {
        foreach (var element in Visuals(root).Concat(Logical(root)).OfType<FrameworkElement>())
        {
            var text = element switch { TextBlock block => block.Text, TextBox box => box.Text, _ => "" };
            var name = AutomationProperties.GetName(element);
            Assert.DoesNotContain("synthetic-overflow", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".avi", text, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-overflow", name, StringComparison.Ordinal);
            Assert.DoesNotContain(".avi", name, StringComparison.Ordinal);
            if (element.ToolTip is string tip)
            {
                Assert.DoesNotContain("synthetic-overflow", tip, StringComparison.Ordinal);
                Assert.DoesNotContain(".avi", tip, StringComparison.Ordinal);
            }
        }
    }

    private static void BringFullyIntoView(FrameworkElement line, ScrollViewer scroll, FrameworkElement root)
    {
        ScrollTo(line, scroll, alignBottom: false);
        var top = line.TranslatePoint(new System.Windows.Point(0, 0), scroll).Y;
        var overlap = Math.Min(top + line.ActualHeight, scroll.ViewportHeight) - Math.Max(top, 0);
        var want = Math.Min(line.ActualHeight, scroll.ViewportHeight);
        Assert.True(overlap >= want - 2, "visible " + overlap + " of " + want + " viewport " + scroll.ViewportHeight + " top " + top + " '" + (line is TextBlock block ? block.Text : line.ToString()) + "'");
        ScrollTo(line, scroll, alignBottom: true);
        var bottom = line.TranslatePoint(new System.Windows.Point(0, line.ActualHeight), scroll).Y;
        Assert.True(bottom <= scroll.ViewportHeight + 1.5 && bottom > 0, "bottom " + bottom + " viewport " + scroll.ViewportHeight);
        var inRoot = Bounds(line, root);
        Assert.True(inRoot.Top < root.ActualHeight + 0.5 && inRoot.Bottom > -0.5, "outside root " + inRoot);
    }

    private static void ScrollTo(FrameworkElement line, ScrollViewer scroll, bool alignBottom)
    {
        var y = line.TranslatePoint(new System.Windows.Point(0, alignBottom ? line.ActualHeight : 0), scroll).Y;
        var target = alignBottom ? scroll.ViewportHeight : 0;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + y - target);
        scroll.UpdateLayout();
    }

    private static ScrollViewer SummaryScroll(Expander expander, ListBox list)
    {
        ScrollViewer? found = null;
        for (DependencyObject? node = expander; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer scroll) { found = scroll; break; }
        Assert.NotNull(found);
        for (DependencyObject? node = list; node is not null; node = VisualTreeHelper.GetParent(node))
            Assert.False(ReferenceEquals(node, found));
        return found;
    }

    private static void Host(Window window, Action<FrameworkElement> inspect)
    {
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        var parameters = new HwndSourceParameters("g09-overflow") { WindowStyle = 0, ExtendedWindowStyle = 0x08000000, PositionX = -32000, PositionY = -32000, Width = 1050, Height = 700 };
        using var hwnd = new HwndSource(parameters) { RootVisual = root };
        try
        {
            root.Measure(new System.Windows.Size(1050, 700));
            root.Arrange(new System.Windows.Rect(0, 0, 1050, 700));
            root.UpdateLayout();
            Pump(Task.Delay(60));
            root.UpdateLayout();
            inspect(root);
        }
        finally
        {
            hwnd.RootVisual = null;
            window.Content = root;
        }
    }

    private static void Save(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap(1050, 700, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Expander Expander(DependencyObject root) => Visuals(root).OfType<Expander>().Single(expander => AutomationProperties.GetName(expander) == ValidationViewNames.CountDetails);
    private static Button OpenButton(DependencyObject root) => (Button)Named(root, ValidationViewNames.OpenSource);
    private static ScrollViewer DetailScroll(DependencyObject root) => (ScrollViewer)Named(root, ValidationViewNames.SelectedDetail);
    private static ValidationResultListView List(DependencyObject root) => Visuals(root).OfType<ValidationResultListView>().Single();
    private static IEnumerable<ListBoxItem> Items(ListBox list) => Visuals(list).OfType<ListBoxItem>();
    private static FrameworkElement Named(DependencyObject root, string name) => Visuals(root).OfType<FrameworkElement>().Single(element => AutomationProperties.GetName(element) == name);
    private static System.Windows.Rect Bounds(FrameworkElement element, FrameworkElement root)
    {
        var origin = element.TransformToAncestor(root).Transform(new System.Windows.Point(0, 0));
        return new System.Windows.Rect(origin, element.RenderSize);
    }
    private static TabControl Tabs(Window window) => Logical(window).OfType<TabControl>().Single();
    private static TabControl ComparisonTab(DependencyObject root) => Logical(root).OfType<TabControl>().Single();
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
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "g09-overflow-" + Guid.NewGuid().ToString("N"));
        public StudyStore Store { get; }
        public SyntheticStudy() { Directory.CreateDirectory(Folder); Store = new StudyStore(Path.Combine(Folder, "study.morrisdb")); }
        public VideoAsset Add(string fileName, int frames, byte blue)
        {
            var path = Path.Combine(Folder, fileName);
            using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new OpenCvSharp.Size(64, 64)))
            {
                Assert.True(writer.IsOpened());
                for (var frame = 0; frame < frames; frame++)
                {
                    using var image = new Mat(64, 64, MatType.CV_8UC3, new Scalar(blue, 40, 20));
                    writer.Write(image);
                }
            }
            var imported = new WorkspaceService(Store).Import(path);
            var asset = imported with { AnimalId = "synthetic", AnimalIdConfirmed = false, GroupName = "synthetic-group", Included = true, Settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .4, Ry = .4, Platform = new(.5, .5, .04), ArenaConfirmed = true } };
            Store.SaveVideo(asset);
            var points = Enumerable.Range(0, frames).Select(frame => new TrackPoint(frame, frame / asset.Metadata.Fps, 32, 32, PointStatus.Tracked, 1)).ToList();
            var run = new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = asset.Settings, Points = points, Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings), AlgorithmContract = AlgorithmIdentity.Contract, NominalTimingVerified = true };
            Store.SaveRun(run);
            return asset;
        }
        public void SaveReference(ValidationHistoryStore history, string hash, VideoAsset asset, int frame, int x)
        {
            var reference = new IndependentReference(asset.Id, frame, ReferenceReviewerLane.First, "Reviewer", ReferenceVisibility.Visible, x, 32, ReferenceEvent.None, "Synthetic reference", []);
            reference.Validate(asset.Metadata);
            history.Save("reference", IndependentValidationComparison.ReferenceKey(hash, reference), JsonDefaults.Serialize(reference), reference.Reviewer, reference.Reason, 0);
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Folder, true); }
    }
}
