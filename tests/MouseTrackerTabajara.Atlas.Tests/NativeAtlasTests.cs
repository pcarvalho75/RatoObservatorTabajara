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
using MouseTrackerTabajara.SpatialViews;
using MouseTrackerTabajara.Storage;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Atlas.Tests;

public class NativeAtlasTests(ITestOutputHelper output)
{
    [Fact] public void ThirtyFrozenCardsReachActualSourcePixelsAndPreserveHoldsBlindingAndRevisions()
    {
        Exception? error = null; var evidence = new List<string>();
        var thread = new Thread(() => { try { Proof(evidence); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "Bounded native atlas proof completed.");
        foreach (var line in evidence) output.WriteLine(line);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Proof(List<string> evidence)
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tabajara-atlas-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var app = new App(); app.InitializeComponent(); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        StudyAtlasWindow? window = null;
        try
        {
            var store = new StudyStore(System.IO.Path.Combine(folder, "synthetic.morrisdb")); var workspace = new WorkspaceService(store);
            var settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .45, Ry = .45, ArenaConfirmed = true, StartSeconds = .5, EndSeconds = 3.5, Platform = new(.45, .5, .01) };
            for (var i = 0; i < 30; i++)
            {
                var path = System.IO.Path.Combine(folder, $"PRIVATE-treatment-{i:D2}.avi");
                using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new OpenCvSharp.Size(300, 200)))
                {
                    Assert.True(writer.IsOpened());
                    for (var frame = 0; frame < 40; frame++)
                    {
                        using var image = new Mat(200, 300, MatType.CV_8UC3, new Scalar(20 + 3 * frame + i, 40 + 3 * frame, 230 - 3 * frame));
                        Cv2.Circle(image, new OpenCvSharp.Point(100 + frame, 100), 7, Scalar.Black, -1); writer.Write(image);
                    }
                }
                var asset = workspace.Import(path) with { Settings = settings, AnimalId = i < 4 ? "PRIVATE-repeat" : "PRIVATE-animal-" + i,
                    AnimalIdConfirmed = i % 5 != 0, BlindedAlias = "PRIVATE-alias-" + i, GroupName = "PRIVATE-group-" + i % 2,
                    Included = i is not (3 or 7), ExclusionReason = i is 3 or 7 ? "Synthetic exclusion" : "", Phase = TrialMode.Probe };
                store.SaveVideo(asset);
                if (i is 5 or 6) continue;
                var points = Enumerable.Range(5, 30).Select(f => f is 10 or 11 ? new TrackPoint(f, f / 10d, null, null, PointStatus.Ambiguous, 2)
                    : new TrackPoint(f, f / 10d, i == 19 && f == 22 ? 265 : 100 + f, 100, PointStatus.Tracked, 1)).ToList();
                var run = new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = settings,
                    Points = points, Metrics = MetricCalculator.Calculate(points, asset.Metadata, settings), NominalTimingVerified = true, AlgorithmContract = AlgorithmIdentity.Contract };
                store.SaveRun(run); store.RecordReview(run, true);
                if (i == 0)
                {
                    // Preserve an explicit legacy case alongside the current R01 review contracts.
                    using var legacy = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
                    legacy.Open(); using var command = legacy.CreateCommand();
                    command.CommandText = "UPDATE reviews SET contract_json=NULL WHERE run_id=$run";
                    command.Parameters.AddWithValue("$run", run.Id); command.ExecuteNonQuery();
                }
                if (i == 4) store.SaveCorrection(run, new(run.Id, 5, 106, 100, "Synthetic correction revision", DateTime.UtcNow));
            }
            window = new StudyAtlasWindow(store.DatabasePath, true); Layout(window, 1050, 700);
            Assert.Equal(30, window.Atlas.RecordingCardCount); Assert.Empty(window.Snapshot.SpatialView().Groups);
            Assert.Equal(22, window.Snapshot.SpatialView().Animals.Count(a => a.Input.IdentityConfirmed));
            Assert.All(window.Snapshot.SpatialView().Animals, a => Assert.False(a.Eligible));
            Assert.Equal(30, window.Snapshot.Rows("videos").Length); Assert.Equal(28, window.Snapshot.Rows("runs").Length);
            Assert.Single(window.Snapshot.Rows("corrections")); Assert.Equal(29, window.Snapshot.Rows("reviews").Length);
            Assert.Equal(2, window.Snapshot.Recordings.Count(r => !r.Asset.Included));
            Assert.Equal(2, window.Snapshot.Recordings.Count(r => r.RawRun is null));
            Assert.Contains(window.Snapshot.Recordings, r => r.InspectionState == AtlasInspectionState.LegacyRecorded);
            Assert.Contains(window.Snapshot.Recordings, r => r.InspectionState == AtlasInspectionState.ContractRecordedUnvalidated);
            Assert.Equal(AtlasInspectionState.Invalidated, window.Snapshot.Recordings[4].InspectionState);
            Assert.Equal(AtlasInspectionState.NotRecorded, window.Snapshot.Recordings[5].InspectionState);
            Assert.Contains(window.Snapshot.Recordings, r => r.Asset.AnimalIdConfirmed && r.Asset.AnimalId == "PRIVATE-repeat");
            Assert.Contains(window.Snapshot.Recordings, r => r.Holds.Any(h => h.Contains("Provisional")));
            Assert.Contains(window.Snapshot.Recordings, r => r.EffectiveRun()?.Points.Any(p => p.X == 265) == true);
            AssertPrivateLabelsHidden(window);
            Assert.Equal(30, Descendants(window.Atlas).OfType<TextBlock>().Count(t => Equals(t.Tag, "atlas-inspection-state")));
            var buttons = Descendants((DependencyObject)window.Content).OfType<Button>().Where(b => Equals(b.Content, "Review recording")).ToArray();
            Assert.Equal(30, buttons.Length);
            var reached = new HashSet<string>();
            foreach (var button in buttons)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window.PendingNavigation); Assert.False(window.Held);
                var source = window.DisplayedSourceSha256!; Assert.True(reached.Add(source));
                var row = window.Snapshot.Recordings.Single(r => r.Asset.SourceSha256 == source);
                var i = int.Parse(System.IO.Path.GetFileNameWithoutExtension(row.Asset.Path).Split('-')[^1]);
                var expectedFrame = row.RawRun is null ? 0 : 5;
                AssertFrame(window, row, i, expectedFrame);
                var next = Descendants((DependencyObject)window.Content).OfType<Button>().Single(b => Equals(b.Content, "Next source frame"));
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window.PendingNavigation); AssertFrame(window, row, i, expectedFrame + 1);
                if (row.RawRun is not null)
                {
                    Assert.Equal(2, window.SourceOverlay.Children.OfType<Polyline>().Count());
                    Pump(window.SeekAsync(10)); Assert.Null(window.SourceOverlay.Children.OfType<Ellipse>().SingleOrDefault(e => Equals(e.Tag, "active-source-marker")));
                    Pump(window.SeekAsync(22)); AssertFrame(window, row, i, 22);
                }
                AssertPrivateLabelsHidden(window);
            }
            Assert.Equal(30, reached.Count); evidence.Add("PASS 30 actual atlas card routed Click handlers reach distinct frozen sources; exact pixel signatures, accepted markers, step buttons and explicit gaps preserved.");
            var playingRow = window.Snapshot.Recordings.Single(r => r.Asset.SourceSha256 == window.DisplayedSourceSha256);
            var playingIndex = int.Parse(System.IO.Path.GetFileNameWithoutExtension(playingRow.Asset.Path).Split('-')[^1]);
            var seekText = Descendants((DependencyObject)window.Content).OfType<TextBox>().Single(box => Equals(box.Tag, "atlas-source-frame")); seekText.Text = "5";
            var seekButton = Descendants((DependencyObject)window.Content).OfType<Button>().Single(b => Equals(b.Content, "Seek exact frame"));
            seekButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window.PendingNavigation); AssertFrame(window, playingRow, playingIndex, 5);
            var play = Descendants((DependencyObject)window.Content).OfType<Button>().Single(b => Equals(b.Content, "Play"));
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(Task.Delay(450));
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window.PendingNavigation);
            Assert.True(window.DisplayedFrame > 5); AssertFrame(window, playingRow, playingIndex, window.DisplayedFrame!.Value);
            var pausedFrame = window.DisplayedFrame; Pump(Task.Delay(150)); Assert.Equal(pausedFrame, window.DisplayedFrame);
            window.Blinded = false; Assert.True(window.Blinded); AssertPrivateLabelsHidden(window);
            Assert.False(window.TryUnblind("", "")); Assert.True(window.Blinded);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER deny_atlas_unblind BEFORE INSERT ON audit WHEN NEW.action='atlas_unblinded' BEGIN SELECT RAISE(ABORT,'synthetic denied audit'); END"; command.ExecuteNonQuery();
                Assert.False(window.TryUnblind("Reviewer", "Audit must succeed before reveal")); Assert.True(window.Blinded); AssertPrivateLabelsHidden(window);
                command.CommandText = "DROP TRIGGER deny_atlas_unblind"; command.ExecuteNonQuery();
            }
            Assert.True(window.TryUnblind("Reviewer", "Reasoned synthetic label inspection")); Layout(window, 1050, 700);
            Assert.Contains(Descendants((DependencyObject)window.Content).OfType<TextBlock>(), t => t.Text.Contains("PRIVATE"));
            window.Blinded = true; Layout(window, 1050, 700); AssertPrivateLabelsHidden(window);
            evidence.Add("PASS actual exact-seek and timer playback/pause controls preserve independently encoded frame identity; unblind/reblind replaces private labels.");
            foreach (var size in new[] { new System.Windows.Size(1050, 700), new System.Windows.Size(1440, 960) })
            {
                Layout(window, size.Width, size.Height);
                var border = Descendants((DependencyObject)window.Content).OfType<Border>().Single(b => b.MinHeight == 320);
                Assert.True(border.ActualHeight >= 320 && border.ActualWidth >= 500);
                var scroll = Descendants(window.Atlas).OfType<ScrollViewer>().Single();
                Assert.True(scroll.ScrollableHeight > 0); scroll.ScrollToEnd(); window.Atlas.UpdateLayout();
                Assert.InRange(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight), 0, 1); scroll.ScrollToTop(); window.Atlas.UpdateLayout();
                if (Environment.GetEnvironmentVariable("TABAJARA_ATLAS_PROOF_DIR") is { Length: > 0 } proofDirectory)
                {
                    Directory.CreateDirectory(proofDirectory);
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render((FrameworkElement)window.Content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var png = File.Create(System.IO.Path.Combine(proofDirectory, $"synthetic-atlas-{size.Width}x{size.Height}.png")); encoder.Save(png);
                }
                evidence.Add($"PASS native atlas {size.Width}x{size.Height} source viewport {border.ActualWidth:F1}x{border.ActualHeight:F1} DIP; blinded PRIVATE labels absent.");
            }
            var frozen = window.Snapshot; var target = frozen.Recordings[4];
            var pinButton = Descendants((DependencyObject)window.Content).OfType<Button>().Single(b => Equals(b.Content, "Pin displayed source/anchor"));
            foreach (var index in new[] { 4, 19 })
            {
                window.Atlas.NavigateToFirstFrame(window.Snapshot.Recordings[index].SpatialMap()); Pump(window.PendingNavigation);
                if (index == 4) Pump(window.SeekAsync(7));
                pinButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Assert.Throws<InvalidOperationException>(() => window.CreateSynchronizedWindow(false));
            var clockRow = window.Snapshot.Recordings[4]; var clockOther = window.Snapshot.Recordings[19];
            Assert.Throws<InvalidDataException>(() => new AtlasSynchronizedWindow(window.Snapshot,
                [new(clockRow.NavigationId, clockRow.Asset.SourceSha256, window.Snapshot.Revision, 0), new(clockOther.NavigationId, clockOther.Asset.SourceSha256, window.Snapshot.Revision, 5)], true, true));
            var sync = window.CreateSynchronizedWindow(true);
            try
            {
                Pump(sync.PendingDisplay); Assert.False(sync.Held); Assert.Equal(new[] { 7, 5 }, sync.DisplayedFrames);
                Assert.Equal(2.7, sync.MaximumClockSeconds, 8);
                var syncRoot = (FrameworkElement)sync.Content; syncRoot.Measure(new System.Windows.Size(1050, 700)); syncRoot.Arrange(new System.Windows.Rect(0, 0, 1050, 700)); syncRoot.UpdateLayout();
                Descendants(syncRoot).OfType<TextBox>().Single().Text = "1.7";
                Descendants(syncRoot).OfType<Button>().Single(b => Equals(b.Content, "Seek common seconds")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(sync.PendingDisplay); Assert.Equal(new[] { 24, 22 }, sync.DisplayedFrames);
                for (var j = 0; j < 2; j++)
                {
                    var bitmap = new FormatConvertedBitmap(sync.DisplayedBitmaps[j]!, PixelFormats.Bgra32, null, 0); var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(10, 10, 1, 1), pixel, 4, 0);
                    var synchronizedFrame = j == 0 ? 24 : 22;
                    Assert.InRange(Math.Abs(pixel[0] - (20 + 3 * synchronizedFrame + (j == 0 ? 4 : 19))), 0, 1);
                    Assert.InRange(Math.Abs(pixel[1] - (40 + 3 * synchronizedFrame)), 0, 1); Assert.InRange(Math.Abs(pixel[2] - (230 - 3 * synchronizedFrame)), 0, 1);
                    Assert.Equal(2, sync.Overlays[j].Children.OfType<Polyline>().Count());
                    var marker = sync.Overlays[j].Children.OfType<Ellipse>().Single(e => Equals(e.Tag, "active-source-marker"));
                    Assert.Equal(j == 0 ? 124d : 265d, Canvas.GetLeft(marker) + 9);
                }
                Assert.All(Descendants(syncRoot).OfType<TextBlock>(), t => Assert.DoesNotContain("PRIVATE", t.Text));
                var commonPlay = Descendants(syncRoot).OfType<Button>().Single(b => Equals(b.Content, "Play common clock"));
                commonPlay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(Task.Delay(450)); commonPlay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(sync.PendingDisplay);
                Assert.True(sync.DisplayedSeconds > 1.7);
                var commonOffset = (int)Math.Floor(sync.DisplayedSeconds!.Value * 10 + 1e-9);
                Assert.Equal(new[] { 7 + commonOffset, 5 + commonOffset }, sync.DisplayedFrames);
                Pump(sync.DisplayAsync(100)); Assert.True(sync.Held); Assert.All(sync.DisplayedBitmaps, Assert.Null);
                store.RecordReview(window.Snapshot.Recordings[19].RawRun!, false);
                Pump(sync.DisplayAsync(0)); Assert.True(sync.Held); Assert.All(sync.DisplayedBitmaps, Assert.Null);
                evidence.Add("PASS actual two-source common technical clock anchors7/5, seek at1.7s displays canonical frames24/22; independently encoded source signatures, gap paths and outlier marker265 retained; unconfirmed anchors and out-of-range common clock held. Biological synchronization remains unlicensed.");
                evidence.Add("PASS common-clock Play/Pause advances both canonical source frames together; review mutation invalidates synchronized playback and clears all pane pixels.");
            }
            finally { sync.Close(); }
            foreach (var mutation in new[] { "correction", "metadata", "protocol", "parent recompute" })
            {
                window.Recompute();
                foreach (var index in new[] { 4, 19 })
                { window.Atlas.NavigateToFirstFrame(window.Snapshot.Recordings[index].SpatialMap()); Pump(window.PendingNavigation); window.PinDisplayedSource(); }
                var paused = window.CreateSynchronizedWindow(true);
                try
                {
                    Pump(paused.PendingDisplay); Assert.False(paused.Held); Assert.All(paused.DisplayedBitmaps, Assert.NotNull);
                    var pausedRoot = (FrameworkElement)paused.Content; pausedRoot.Measure(new System.Windows.Size(1050, 700)); pausedRoot.Arrange(new System.Windows.Rect(0, 0, 1050, 700)); pausedRoot.UpdateLayout();
                    var frozenRow = window.Snapshot.Recordings[19];
                    if (mutation == "correction") store.SaveCorrection(frozenRow.RawRun!, new(frozenRow.RawRun!.Id, 6, 106, 100, "Synthetic idle correction", DateTime.UtcNow));
                    else if (mutation == "metadata") store.SaveVideo(frozenRow.Asset with { GroupName = "PRIVATE-idle-metadata" });
                    else if (mutation == "protocol")
                    {
                        store.SaveProtocolWindow(frozenRow.Asset, new ProtocolWindow { RequestedExposureSeconds = 1,
                            Reviewer = "Synthetic reviewer", Reason = "Synthetic paused protocol revision" });
                    }
                    else window.Recompute();
                    if (mutation != "parent recompute") Pump(Task.Delay(2300));
                    // No seek/display request triggered the hold: the actual paused tree must already be cleared/disabled.
                    Assert.True(paused.Held); Assert.Null(paused.DisplayedSeconds); Assert.Empty(paused.DisplayedFrames);
                    Assert.All(paused.DisplayedBitmaps, Assert.Null); Assert.All(paused.Overlays, overlay => Assert.Empty(overlay.Children));
                    Assert.All(Descendants(pausedRoot).OfType<Button>(), button => Assert.False(button.IsEnabled));
                    Assert.All(Descendants(pausedRoot).OfType<TextBox>(), input => Assert.False(input.IsEnabled));
                    Assert.DoesNotContain(Descendants(pausedRoot).OfType<TextBlock>(), text => text.Text.Contains("Source frame"));
                    Assert.Contains(Descendants(pausedRoot).OfType<TextBlock>(), text => text.Text.Contains("All panes are held"));
                    Pump(paused.DisplayAsync(0)); Assert.True(paused.Held); Assert.All(paused.DisplayedBitmaps, Assert.Null);
                    evidence.Add("PASS paused synchronized " + mutation + " invalidates without a seek; source pixels, paths and clocks cleared; controls disabled and old view cannot recover.");
                }
                finally { paused.Close(); }
            }
            window.Recompute(); frozen = window.Snapshot; target = frozen.Recordings[4]; window.Atlas.NavigateToFirstFrame(target.SpatialMap()); Pump(window.PendingNavigation);
            var rawBefore = JsonDefaults.Serialize(target.RawRun); var effectiveBefore = JsonDefaults.Serialize(target.EffectiveRun());
            var escaped = target.RawRun!; escaped.Points.Clear(); Assert.Equal(rawBefore, JsonDefaults.Serialize(target.RawRun));
            store.SaveVideo(target.Asset with { GroupName = "PRIVATE-revised" }); Assert.False(frozen.IsCurrent());
            Pump(Task.Delay(2200)); Assert.True(window.Held); Assert.Null(window.DisplayedBitmap);
            Pump(window.SeekAsync(6)); Assert.True(window.Held); Assert.Null(window.DisplayedBitmap); AssertPrivateLabelsHidden(window);
            Assert.Equal(effectiveBefore, JsonDefaults.Serialize(target.EffectiveRun()));
            window.Recompute(); Assert.NotEqual(frozen.Revision, window.Snapshot.Revision); Assert.True(window.Snapshot.IsCurrent());
            var current = window.Snapshot.Recordings[4];
            var map = current.SpatialMap(); window.Atlas.NavigateToFirstFrame(map); Pump(window.PendingNavigation); Assert.False(window.Held);
            window.Recompute(); // releases guard before intentional synthetic source mutation
            var pathToMutate = current.Asset.Path; var bytes = File.ReadAllBytes(pathToMutate); bytes[^1] ^= 1; File.WriteAllBytes(pathToMutate, bytes);
            window.Atlas.NavigateToFirstFrame(window.Snapshot.Recordings[4].SpatialMap()); Pump(window.PendingNavigation);
            Assert.True(window.Held); Assert.Null(window.DisplayedBitmap); AssertPrivateLabelsHidden(window);
            evidence.Add("PASS frozen raw/correction/review material remains immutable; metadata mutation holds stale playback; explicit recomputation replaces revision; changed source SHA256 withholds pixels.");
            window.Close(); window = null; bytes[^1] ^= 1; File.WriteAllBytes(pathToMutate, bytes);
            ExperimentNativeProof.Run(store, evidence);
            TemporalScopeProof.Run(folder, evidence);
        }
        finally { window?.Close(); app.Shutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }
    private static void AssertFrame(StudyAtlasWindow window, AtlasRecording row, int recording, int frame)
    {
        Assert.False(window.Held); Assert.Equal(frame, window.DisplayedFrame); Assert.Equal(row.Asset.SourceSha256, window.DisplayedSourceSha256);
        Assert.Contains($"Source frame {frame}", window.StatusText);
        var bitmap = new FormatConvertedBitmap(window.DisplayedBitmap!, PixelFormats.Bgra32, null, 0); var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(10, 10, 1, 1), pixel, 4, 0);
        Assert.InRange(Math.Abs(pixel[0] - (20 + 3 * frame + recording)), 0, 1);
        Assert.InRange(Math.Abs(pixel[1] - (40 + 3 * frame)), 0, 1); Assert.InRange(Math.Abs(pixel[2] - (230 - 3 * frame)), 0, 1);
        var point = row.EffectiveRun()?.Points.SingleOrDefault(p => p.Frame == frame);
        var marker = window.SourceOverlay.Children.OfType<Ellipse>().SingleOrDefault(e => Equals(e.Tag, "active-source-marker"));
        if (point is { Accepted: true }) { Assert.NotNull(marker); Assert.Equal((double)point.X!.Value, Canvas.GetLeft(marker) + 9); Assert.Equal((double)point.Y!.Value, Canvas.GetTop(marker) + 9); }
        else Assert.Null(marker);
    }
    private static void AssertPrivateLabelsHidden(StudyAtlasWindow window)
    {
        Assert.DoesNotContain("PRIVATE", window.Title);
        foreach (var text in Descendants((DependencyObject)window.Content).OfType<TextBlock>()) Assert.DoesNotContain("PRIVATE", text.Text);
        foreach (var text in Descendants((DependencyObject)window.Content).OfType<TextBlock>()) Assert.DoesNotContain("Position time uses the planned exposure", text.Text);
        foreach (var element in Descendants((DependencyObject)window.Content).OfType<FrameworkElement>())
        { if (element.ToolTip is string tip) Assert.DoesNotContain("PRIVATE", tip); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void Layout(StudyAtlasWindow window, double width, double height)
    {
        var root = (FrameworkElement)window.Content; root.Measure(new System.Windows.Size(width, height)); root.Arrange(new System.Windows.Rect(0, 0, width, height)); root.UpdateLayout(); Pump(Task.Delay(25)); root.UpdateLayout();
    }
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false)); timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Assert.True(task.IsCompleted, "Native dispatcher operation completed within 20 seconds."); task.GetAwaiter().GetResult();
    }
}
