using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Calibration;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.RegionScenario.Tests;

public class RegionScenarioNativeTests(ITestOutputHelper output)
{
    [Fact]
    public void NativeWindowListsEveryScenarioAndPreservesSourcePrivacyAndFreshness()
    {
        Exception? error = null; var evidence = new List<string>();
        var thread = new Thread(() => { try { Proof(evidence); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "Bounded native region proof completed.");
        foreach (var line in evidence) output.WriteLine(line);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Proof(List<string> evidence)
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tabajara-region-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var app = new App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        MainWindow? main = null; RegionScenarioWindow? window = null;
        try
        {
            var database = System.IO.Path.Combine(folder, "synthetic.morrisdb");
            var store = new StudyStore(database); var workspace = new WorkspaceService(store);
            var video = System.IO.Path.Combine(folder, "PRIVATE-region-source.avi");
            using (var writer = new VideoWriter(video, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new OpenCvSharp.Size(200, 160)))
            {
                Assert.True(writer.IsOpened());
                for (var frame = 0; frame < 5; frame++)
                {
                    using var painted = new Mat(160, 200, MatType.CV_8UC3, new Scalar(40, 80, 160));
                    writer.Write(painted);
                }
            }
            var imported = workspace.Import(video);
            Assert.Equal(200, imported.Metadata.Width); Assert.Equal(160, imported.Metadata.Height);
            Assert.Equal(10, imported.Metadata.Fps, 6); Assert.Equal(5, imported.Metadata.FrameCount);
            var settings = new TrackingSettings { Cx = .5, Cy = .5, Rx = .4, Ry = .25, ArenaConfirmed = true, Platform = new(.5, .5, .05) };
            var asset = imported with { Settings = settings, AnimalId = "A", AnimalIdConfirmed = true, GroupName = "WT", Phase = TrialMode.Probe, Day = 1, Trial = 1, Included = true, ExclusionReason = "" };
            store.SaveVideo(asset);
            var points = new List<TrackPoint> { At(0, 111, 80), At(1, 100, 80), At(2, 100, 80), At(3, 111, 80), At(4, 100, 85) };
            var run = new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = settings, Points = points, Metrics = MetricCalculator.Calculate(points, asset.Metadata, settings), NominalTimingVerified = true, AlgorithmContract = AlgorithmIdentity.Contract };
            store.SaveRun(run);
            store.RecordBiologicalEvent(asset, BiologicalEventKind.WaterEntry, 0, "Synthetic release", "Reviewer");
            store.SaveProtocolWindow(asset, new() { RequestedExposureSeconds = .5, ReleaseConfirmed = true, ReleaseFrame = 0, WindowConfirmed = true, Reason = "Synthetic probe window", Reviewer = "Reviewer" });
            store.RecordReview(run, true);
            Assert.True(ExperimentPlanAdapter.CurrentInspection(AtlasSnapshot.Read(database).Recordings.Single()));
            var plan = new StudyPlan
            {
                Title = "Synthetic region plan", UnitDesign = StudyUnitDesign.RepeatedTrialsWithinAnimals, DesignConfirmed = true, RosterConfirmed = true, Exploratory = false,
                PrimaryComparison = "Synthetic allocation",
                Animals = [new() { Id = "A", Group = "WT", IdentityConfirmed = true, AllocationConfirmed = true }, new() { Id = "B", Group = "KO", IdentityConfirmed = true, AllocationConfirmed = true, Excluded = true, ExclusionReason = "Measured WT exclusion" }, new() { Id = "C", Group = "KO", IdentityConfirmed = true, AllocationConfirmed = true }],
                Contexts = [new() { Id = "probe-1", Phase = TrialMode.Probe, Day = 1, RequestedExposureSeconds = .5, ComparisonDurationSeconds = .5, WindowConfirmed = true, CommonCoordinateReference = "Synthetic room", Orientation = StudyOrientation.Room, AlignmentConfirmed = true }],
                Trials = [new() { Id = "slot-a", AnimalId = "A", ContextId = "probe-1", Order = 1, RecordingId = asset.Id, SourceReferenceConfirmed = true, ReferenceSourceSha256 = asset.SourceSha256, ReferenceSettingsHash = StudyStore.SettingsHash(settings) },
                    new() { Id = "slot-b", AnimalId = "B", ContextId = "probe-1", Order = 1, MissingReason = "Excluded from acquisition" },
                    new() { Id = "slot-c", AnimalId = "C", ContextId = "probe-1", Order = 1, MissingReason = "Not acquired" }]
            };
            var savedPlan = new StudyPlanStore(store).Save(plan, "Reviewer", "Synthetic expected region plan", 0);
            var profilePath = System.IO.Path.Combine(folder, "measured-profile.json");
            var profile = Profile(asset.SourceSha256, 1); CalibrationProfileFile.Save(profile, profilePath);
            var profileBytes = File.ReadAllBytes(profilePath);
            var otherProfile = System.IO.Path.Combine(folder, "other-profile.json");
            CalibrationProfileFile.Save(Profile(asset.SourceSha256, 2), otherProfile);
            var sourceBytes = File.ReadAllBytes(video); var sourceHash = Sha(video); var runJson = LatestRunJson(database, asset.Id);
            Assert.Equal(asset.SourceSha256, sourceHash, StringComparer.OrdinalIgnoreCase);

            var model = new MainViewModel(workspace); main = new MainWindow(model); main.Show();
            var sidebar = Find<Button>(main).Single(b => b.Name == "RegionScenarioButton");
            Assert.Equal("Region and calibration scenarios", ((TextBlock)sidebar.Content).Text);
            sidebar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window = System.Windows.Application.Current.Windows.OfType<RegionScenarioWindow>().Single();
            Assert.Equal(1050, window.MinWidth); Assert.Equal(700, window.MinHeight);
            Layout(window);
            Add(window, "undocumented", "A perturbation outside the range written before calculation.", RegionScenarioKind.PlatformCenterExtent, "12", "0", "0", "0", "-5", "5");
            Assert.Empty(window.Scenarios); Assert.Contains("documented finite range", window.StatusText, StringComparison.Ordinal);
            Add(window, "center-plus-12", "Documented WT center shift before any endpoint was calculated.", RegionScenarioKind.PlatformCenterExtent, "12", "0", "0", "0", "-12", "12");
            Add(window, "radius-minus-6", "Six source pixels less platform radius, documented before calculation.", RegionScenarioKind.PlatformCenterExtent, "0", "0", "-6", "0", "-12", "12");
            Add(window, "mask-unrun", "A pool change that would affect detection is retained and was not run.", RegionScenarioKind.PoolDetectionMaskUnrun, "0", "0", "0", "8", "-12", "12");
            Add(window, "collapsed-radius", "A documented radius reduction that leaves the permitted measurement domain.", RegionScenarioKind.PlatformCenterExtent, "0", "0", "-10", "0", "-12", "12");
            window.PoolCenterX.Text = "50"; window.PoolCenterY.Text = "40"; window.TargetX.Text = "50"; window.TargetY.Text = "20";
            Add(window, "measured-homography", "Zero control shift of the measured scale homography, documented before calculation.", RegionScenarioKind.CalibrationControlUncertainty, "0", "0", "0", "0", "-1", "1", "", "0", "0", "0", "0");
            Add(window, "bad-independent-check", "Twenty source pixels of control N, expected to fail the independent check.", RegionScenarioKind.CalibrationControlUncertainty, "20", "0", "0", "0", "-20", "20", "N");
            Add(window, "duplicate-control", "Shifting NE onto NW fails the fit and stays in the set.", RegionScenarioKind.CalibrationControlUncertainty, "-160", "0", "0", "0", "-160", "20", "NE");
            Assert.Equal(7, window.Scenarios.Count); Assert.False(window.Fresh); Assert.Null(window.Result);
            window.ProfilePath.Text = profilePath;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ComputationGate = token => release.Task.WaitAsync(token);
            var first = window.ComputeAsync(); var second = window.ComputeAsync();
            window.ResponsiveMark = "responsive"; Assert.Equal("responsive", window.ResponsiveMark); Assert.False(second.IsCompleted);
            release.TrySetResult(true); Pump(second); Pump(first);
            Assert.True(window.Fresh, window.StatusText); Assert.Single(EvidenceFiles(folder));
            window.ComputationGate = null;
            var rendered = Text(window);
            foreach (var name in new[] { "original", "center-plus-12", "radius-minus-6", "mask-unrun", "collapsed-radius", "measured-homography", "bad-independent-check", "duplicate-control" })
                Assert.Contains(name, rendered, StringComparison.Ordinal);
            Assert.Contains(RegionScenarioWording.FrozenCoordinateMode, rendered, StringComparison.Ordinal);
            Assert.Contains(RegionScenarioWording.DetectionUnrunMode, rendered, StringComparison.Ordinal);
            Assert.Contains(RegionScenarioWording.CalibrationMode, rendered, StringComparison.Ordinal);
            Assert.Contains("not a confidence interval", rendered, StringComparison.Ordinal);
            Assert.Contains("Documented WT center shift", rendered, StringComparison.Ordinal);
            Assert.Contains("-0.100", rendered, StringComparison.Ordinal);
            Assert.Contains("0.300", rendered, StringComparison.Ordinal);
            Assert.Contains("weight 1", rendered, StringComparison.Ordinal);
            Assert.Contains("excluded", rendered, StringComparison.Ordinal);
            Assert.Contains("missing", rendered, StringComparison.Ordinal);
            Assert.Equal(7, Find<Button>(window).Count(b => Equals(b.Content, "Open source frame")));
            var computed = window.Result!.Recordings.Single().Set!;
            Assert.Equal(.3, computed.Original.PlatformSiteSeconds!.Value, 9);
            Assert.Equal(.2, computed.Scenarios.Single(s => s.Name == "center-plus-12").Endpoints!.PlatformSiteSeconds!.Value, 9);
            Assert.Equal(-1, computed.Scenarios.Single(s => s.Name == "center-plus-12").DeltaFromOriginal!.PlatformSiteEntries);
            Assert.True(computed.Scenarios.Single(s => s.Name == "mask-unrun").Held);
            Assert.True(computed.Scenarios.Single(s => s.Name == "collapsed-radius").Held);
            Assert.True(computed.Scenarios.Single(s => s.Name == "bad-independent-check").Held);
            Assert.True(computed.Scenarios.Single(s => s.Name == "duplicate-control").Held);
            var homography = computed.Scenarios.Single(s => s.Name == "measured-homography");
            Assert.False(homography.Held); Assert.Equal(55.5, homography.Coordinates[0].RoomXcm!.Value, 6); Assert.Equal(40, homography.Coordinates[0].RoomYcm!.Value, 6);
            Assert.Equal("cm", homography.Endpoints!.DistanceUnit);
            var collection = window.Result.Collections.Single(c => c.ScenarioName == RegionScenarioWording.OriginalName);
            Assert.Equal(1, collection.Summary.Animals.Single(a => a.AnimalId == "A").Weight);
            Assert.Equal(.3, collection.Summary.Animals.Single(a => a.AnimalId == "A").AnimalMean!.Value, 9);
            Assert.Equal("excluded", collection.Summary.Animals.Single(a => a.AnimalId == "B").Status);
            Assert.Equal("missing", collection.Summary.Animals.Single(a => a.AnimalId == "C").Status);
            Assert.Equal(0, window.Result.Collections.Single(c => c.ScenarioName == "collapsed-radius").Summary.Animals.Single(a => a.AnimalId == "A").Weight);
            var evidencePath = window.Result.EvidencePath; var evidenceBytes = File.ReadAllBytes(evidencePath);
            var evidenceText = System.Text.Encoding.UTF8.GetString(evidenceBytes);
            Assert.Contains("Synthetic region plan", evidenceText, StringComparison.Ordinal);
            Assert.Contains("Synthetic probe window", evidenceText, StringComparison.Ordinal);
            Assert.Contains(RegionScenarioWording.ComparisonScope, evidenceText, StringComparison.Ordinal);
            Assert.Contains("frozen_material_sha256", evidenceText, StringComparison.Ordinal);
            var implementation = ScenarioImplementationProbe.Capture();
            Assert.Contains(implementation.CoreAssemblySha256, evidenceText, StringComparison.Ordinal);
            Assert.Contains(implementation.SourceMaterialSha256, evidenceText, StringComparison.Ordinal);
            Assert.Equal(implementation.CanonicalSha256, window.Result.Identity.Implementation.CanonicalSha256);
            RegionScenarioEvidenceStore.Authenticate(JsonDefaults.Deserialize<RegionScenarioEvidenceDocument>(evidenceText));
            Assert.Null(ScenarioImplementationProbe.ReplayHold(window.Result.Identity.Implementation, implementation));
            Assert.Contains("111", window.Result.Replay.Recordings.Single().MeasuredRunJson, StringComparison.Ordinal);
            Assert.Contains("Synthetic region plan", window.Result.Replay.PlanJson, StringComparison.Ordinal);
            var matched = window.Result.Recordings.Single().Windows.Single(item => item.Name == RegionScenarioWording.OriginalName);
            Assert.True(matched.Supported); Assert.False(matched.FractionalBoundary);
            Assert.Equal(.3, matched.Comparison!.PlatformSiteSeconds!.Value, 9);
            Assert.Equal(matched.FullRun!.PlatformSiteSeconds, matched.Comparison.PlatformSiteSeconds);
            Assert.Equal(runJson, LatestRunJson(database, asset.Id)); Assert.Equal(sourceBytes, File.ReadAllBytes(video));

            Find<Button>(window).Single(b => b.Tag is SourceFrameRequest request && request.ScenarioName == "center-plus-12").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var viewer = window.LastViewer!; Pump(viewer.Pending); Assert.False(viewer.Held); Assert.Equal(0, viewer.DisplayedFrame);
            Assert.Equal(1050, viewer.MinWidth); Assert.Equal(700, viewer.MinHeight);
            var image = new FormatConvertedBitmap(viewer.DisplayedBitmap!, PixelFormats.Bgra32, null, 0); var pixel = new byte[4];
            image.CopyPixels(new Int32Rect(10, 10, 1, 1), pixel, 4, 0);
            Assert.InRange(Math.Abs(pixel[0] - 40), 0, 2); Assert.InRange(Math.Abs(pixel[1] - 80), 0, 2); Assert.InRange(Math.Abs(pixel[2] - 160), 0, 2);
            var pool = viewer.Overlay.Children.OfType<Ellipse>().Single(e => Equals(e.Tag, "scenario-pool"));
            Assert.Equal(20, Canvas.GetLeft(pool), 6); Assert.Equal(40, Canvas.GetTop(pool), 6); Assert.Equal(160, pool.Width, 6); Assert.Equal(80, pool.Height, 6);
            var platform = viewer.Overlay.Children.OfType<Ellipse>().Single(e => Equals(e.Tag, "scenario-platform"));
            Assert.Equal(102, Canvas.GetLeft(platform), 6); Assert.Equal(75, Canvas.GetTop(platform), 6); Assert.Equal(20, platform.Width, 6); Assert.Equal(10, platform.Height, 6);
            var marker = viewer.Overlay.Children.OfType<Ellipse>().Single(e => Equals(e.Tag, "active-source-marker"));
            Assert.Equal(111, Canvas.GetLeft(marker) + 9, 6); Assert.Equal(80, Canvas.GetTop(marker) + 9, 6);
            Assert.Contains("PRIVATE", viewer.StatusText + viewer.Title + IdentityText(viewer), StringComparison.Ordinal);

            window.Blinded = true; Layout(window); AssertPrivate(window); AssertPrivate(viewer);
            Assert.Contains(BlindIdentity.ReasonWithheld, Text(window), StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE", IdentityText(viewer), StringComparison.Ordinal);
            Assert.Contains("Named scenario", viewer.StatusText, StringComparison.Ordinal);
            Assert.False(window.TryUnblind("", "")); Assert.True(window.Blinded); AssertPrivate(window);
            var reviewer = Find<TextBox>(window).Single(t => Equals(t.ToolTip, "Unblinding reviewer"));
            var reason = Find<TextBox>(window).Single(t => Equals(t.ToolTip, "Reason for unblinding this view"));
            reviewer.Clear(); reason.Clear(); window.Blinded = false; Assert.True(window.Blinded); AssertPrivate(window); AssertPrivate(viewer);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER deny_region_unblind BEFORE INSERT ON audit WHEN NEW.action='region_scenario_unblinded' BEGIN SELECT RAISE(ABORT,'synthetic denied audit'); END";
                command.ExecuteNonQuery();
                reviewer.Text = "Reviewer"; reason.Text = "Audit must precede exposure"; window.Blinded = false;
                Assert.True(window.Blinded); AssertPrivate(window); AssertPrivate(viewer);
                command.CommandText = "DROP TRIGGER deny_region_unblind"; command.ExecuteNonQuery();
            }
            reviewer.Text = "Reviewer"; reason.Text = "Reasoned synthetic region inspection"; window.Blinded = false; Assert.False(window.Blinded); Layout(window);
            Assert.Contains("PRIVATE", IdentityText(viewer), StringComparison.Ordinal);
            Assert.Equal(1L, AuditCount(database, "region_scenario_unblinded"));
            window.Blinded = true; Layout(window); AssertPrivate(window); AssertPrivate(viewer);
            viewer.Close(); window.LastViewer?.Close();

            Click(window, "Compute frozen scenarios"); Pump(window.PendingComputation);
            Assert.True(window.Fresh); Assert.Equal(2, EvidenceFiles(folder).Length); Assert.Equal(evidenceBytes, File.ReadAllBytes(evidencePath));
            Assert.Equal(sourceBytes, File.ReadAllBytes(video)); Assert.Equal(runJson, LatestRunJson(database, asset.Id));
            sourceBytes[^1] ^= 1; File.WriteAllBytes(video, sourceBytes);
            Click(window, "Compute frozen scenarios"); Pump(window.PendingComputation);
            Assert.False(window.Fresh); Assert.Contains("Source bytes no longer match", window.StatusText, StringComparison.Ordinal);
            Assert.Equal(2, EvidenceFiles(folder).Length); sourceBytes[^1] ^= 1; File.WriteAllBytes(video, sourceBytes);
            Click(window, "Compute frozen scenarios"); Pump(window.PendingComputation); Assert.True(window.Fresh);
            var edited = settings with { Platform = new PlatformSite(.5, .5, .04) };
            store.SaveRun(new AnalysisRun { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = edited, Points = points, Metrics = MetricCalculator.Calculate(points, asset.Metadata, edited), NominalTimingVerified = true, AlgorithmContract = AlgorithmIdentity.Contract });
            Pump(window.CheckFreshnessAsync()); Assert.False(window.Fresh); Assert.Contains("settings", window.StatusText, StringComparison.Ordinal);
            Click(window, "Compute frozen scenarios"); Pump(window.PendingComputation); Assert.True(window.Fresh);
            new StudyPlanStore(store).Save(plan with { Title = "Synthetic region plan revised" }, "Reviewer", "Plan revision must stale the region result", savedPlan.Revision);
            Pump(window.CheckFreshnessAsync()); Assert.False(window.Fresh); Assert.Contains("plan", window.StatusText, StringComparison.Ordinal);
            Click(window, "Compute frozen scenarios"); Pump(window.PendingComputation); Assert.True(window.Fresh);
            window.ProfilePath.Text = otherProfile; Pump(window.CheckFreshnessAsync());
            Assert.False(window.Fresh); Assert.Contains("calibration profile", window.StatusText, StringComparison.Ordinal);
            Assert.Equal(sourceHash, Sha(video), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(profileBytes, File.ReadAllBytes(profilePath));
            evidence.Add("PASS native region button, seven prespecified scenarios, independent center/radius/homography consequences, held detection/fit/check rows, equal-animal collection, append-only evidence.");
            evidence.Add("PASS cancel/latest generation left one evidence file, source/run bytes unchanged, stale source/settings/plan/profile cleared freshness, viewer pixel and ellipse, short-label blind with failed audit.");
        }
        finally
        {
            window?.Close(); main?.Close();
            app.Shutdown();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            RegionScenarioMathTests.DeleteScratch(folder, "tabajara-region-native-");
        }
    }

    private static TrackPoint At(int frame, int x, int y) => new(frame, frame / 10d, x, y, PointStatus.Tracked, 1);
    private static readonly Matrix3 KnownScale = new(.5, 0, 0, 0, .5, 0, 0, 0, 1);
    private static MeasuredLandmark Mark(string id, double x, double y) => new(id, new(x, y), KnownScale.Map(new(x, y)));
    private static CalibrationProfile Profile(string sha, int revision) => WaterPlaneCalibration.Fit(new("synthetic-camera", 200, 160, sha, 0, revision,
        [Mark("NW", 20, 20), Mark("NE", 180, 20), Mark("SW", 20, 140), Mark("SE", 180, 140), Mark("N", 100, 30), Mark("W", 30, 80)],
        [Mark("C", 100, 80), Mark("E", 150, 80)], .01, .01, true, "Reviewer"));
    private static void Add(RegionScenarioWindow window, string name, string rationale, RegionScenarioKind kind, string dx, string dy, string extent, string ry, string min, string max, string landmark = "", string roomDx = "0", string roomDy = "0", string roomMin = "0", string roomMax = "0")
    {
        window.NameInput.Text = name; window.RationaleInput.Text = rationale; window.KindInput.SelectedItem = kind;
        window.UnitInput.SelectedItem = DisclosedUnit.SourcePixels; window.OrientationInput.SelectedItem = StudyOrientation.Room;
        window.DeltaX.Text = dx; window.DeltaY.Text = dy; window.DeltaExtent.Text = extent; window.DeltaRy.Text = ry;
        window.RangeMin.Text = min; window.RangeMax.Text = max; window.LandmarkId.Text = landmark;
        window.RoomDeltaX.Text = roomDx; window.RoomDeltaY.Text = roomDy; window.RoomMin.Text = roomMin; window.RoomMax.Text = roomMax;
        Click(window, "Add named scenario");
    }
    private static void Click(System.Windows.Window window, string content) => Find<Button>(window).Single(b => Equals(b.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string Text(System.Windows.Window window) => string.Join("\n", Find<TextBlock>(window).Select(t => t.Text));
    private static string IdentityText(RegionScenarioSourceViewer viewer) => string.Join("\n", Find<TextBlock>(viewer).Select(t => t.Text));
    private static void AssertPrivate(System.Windows.Window window)
    {
        foreach (var block in Find<TextBlock>(window))
        {
            Assert.DoesNotContain("PRIVATE", block.Text, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"\b(WT|KO)\b", block.Text);
            Assert.NotEqual("A", block.Text.Trim()); Assert.NotEqual("B", block.Text.Trim());
            Assert.DoesNotContain("Animal A", block.Text, StringComparison.Ordinal); Assert.DoesNotContain("Animal B", block.Text, StringComparison.Ordinal);
        }
        foreach (var box in Find<ComboBox>(window))
            foreach (var item in box.Items.OfType<ComboBoxItem>())
                if (item.Content is string content) Assert.DoesNotContain("PRIVATE", content, StringComparison.Ordinal);
    }
    private static string[] EvidenceFiles(string folder)
    {
        var evidence = System.IO.Path.Combine(folder, RegionScenarioEvidenceStore.FolderName);
        return Directory.Exists(evidence) ? Directory.GetFiles(evidence, "region-scenario-*.json") : [];
    }
    private static string Sha(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static string LatestRunJson(string database, string videoId)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM runs WHERE video_id=$id ORDER BY rowid DESC LIMIT 1"; command.Parameters.AddWithValue("$id", videoId);
        return (string)command.ExecuteScalar()!;
    }
    private static long AuditCount(string database, string action)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit WHERE action=$action"; command.Parameters.AddWithValue("$action", action);
        return (long)command.ExecuteScalar()!;
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject => Descendants(root).OfType<T>();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Layout(System.Windows.Window window)
    { var root = (FrameworkElement)window.Content; root.Measure(new System.Windows.Size(1050, 700)); root.Arrange(new System.Windows.Rect(0, 0, 1050, 700)); root.UpdateLayout(); }
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Assert.True(task.IsCompleted); task.GetAwaiter().GetResult();
    }
}
