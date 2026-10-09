using System.IO;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Cli;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
using Window = System.Windows.Window;

namespace MouseTrackerTabajara.Stability.Tests;

public sealed class NativeStabilityTests(ITestOutputHelper output)
{
    [Fact] public void ActualNativeReplayLoadsReportsTracesAndPreservesFailurePrivacyFreshnessCancellation()
    {
        Exception? error = null; var messages = new List<string>();
        var thread = new Thread(() => { try { Proof(messages); } catch (Exception ex) { error = ex; } }); thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "Synthetic native stability proof timed out."); foreach (var message in messages) output.WriteLine(message);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Proof(List<string> messages)
    {
        var folder = Path.Combine(Repository(), "artifacts", "stability", "native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var recordings = new List<ReplayPlanRecording>();
        for (var index = 0; index < 2; index++)
        {
            var path = Path.Combine(folder, index == 0 ? "WT_camera_A.avi" : "KO_camera_B.avi");
            using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new OpenCvSharp.Size(160, 120)))
            {
                Assert.True(writer.IsOpened()); for (var frame = 0; frame < 10; frame++) { using var image = new Mat(120, 160, MatType.CV_8UC3, new Scalar(180 + frame + index * 10, 190, 200)); if (frame != 3) Cv2.Circle(image, new(50 + frame * 3, 60), 7, new Scalar(5, 5, 5), -1); writer.Write(image); }
            }
            var asset = new VideoAsset { Id = "recording" + index, Path = path, SourceSha256 = OpenCvTracker.HashFile(path), Metadata = OpenCvTracker.Probe(path), Settings = new() { Cx = .5, Cy = .5, Rx = .45, Ry = .4, Threshold = 40, Blur = 1, MinArea = 20, MaxArea = 5000, ArenaConfirmed = true, Platform = new(.7, .5, .06) } };
            var run = OpenCvTracker.Analyze(asset); var baseline = Path.Combine(folder, "baseline" + index); ExportWriter.SaveRun(run, baseline); var runPath = Path.Combine(baseline, "analysis.json");
            recordings.Add(new() { RecordingId = asset.Id, VideoPath = path, ExpectedSourceSha256 = asset.SourceSha256, ExpectedMetadata = new() { Width = 160, Height = 120, Fps = 10, FrameCount = 10 }, BaselineAnalysisPath = runPath, BaselineSha256 = OpenCvTracker.HashFile(runPath), AnimalId = index == 0 ? "WT" : "KO", AnimalIdConfirmed = true, GroupName = index == 0 ? "A" : "B", Phase = "probe", Day = 1, Trial = 1 });
        }
        var planPath = Path.Combine(folder, "plan.json"); File.WriteAllText(planPath, JsonDefaults.Serialize(new ReplayPlanDocument { Schema = ReplayClaims.PlanSchema, AlgorithmContract = AlgorithmIdentity.Contract, Recordings = recordings, Scenarios = [new() { Name = "original" }, new() { Name = "adverse", Threshold = 0 }, new() { Name = "WT_failed", Threshold = 254 }] }));
        var replay = Path.Combine(folder, "replay"); var exit = ReplayCommand.Execute(["replay", planPath, replay], new ReplayCommandContext { Tracker = new FailNamedNativeTracker(), Out = new StringWriter(), Error = new StringWriter() }); Assert.Equal(4, exit);
        var manifestPath = Path.Combine(replay, "replay-manifest.json"); var snapshot = DetectorStabilityAdapter.Read(manifestPath); Assert.Equal(6, snapshot.Report.Trials.Length);
        Assert.All(snapshot.Report.Trials, t => Assert.False(t.Supported)); Assert.Equal(2, snapshot.Report.Trials.Count(t => t.Scenario == "WT_failed")); Assert.All(snapshot.Report.Trials.Where(t => t.Scenario == "WT_failed"), t => Assert.Contains(t.Holds, h => h.Contains("failed")));
        var sourceHashes = recordings.Select(r => OpenCvTracker.HashFile(r.VideoPath!)).ToArray(); var baselineHashes = recordings.Select(r => OpenCvTracker.HashFile(r.BaselineAnalysisPath!)).ToArray();
        var store = new StudyStore(Path.Combine(folder, "study.db"));
        foreach (var recording in recordings)
        {
            var run = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(recording.BaselineAnalysisPath!));
            store.SaveVideo(new() { Id = recording.RecordingId!, Path = recording.VideoPath!, SourceSha256 = recording.ExpectedSourceSha256!, Metadata = run.Metadata, Settings = run.Settings, AnimalId = recording.AnimalId!, AnimalIdConfirmed = true, GroupName = recording.GroupName!, Phase = TrialMode.Probe }); store.SaveRun(run);
        }
        var reference = store.Videos().First();
        for (var index = 0; index < 28; index++)
        {
            var id = index == 1 ? "excluded" : "missing" + index.ToString("00"); var path = Path.Combine(folder, id + ".avi");
            using (var writer = new VideoWriter(path, VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new OpenCvSharp.Size(160, 120))) { Assert.True(writer.IsOpened()); for (var frame = 0; frame < 10; frame++) { using var image = new Mat(120, 160, MatType.CV_8UC3, new Scalar(100 + index * 3, 110, 120)); writer.Write(image); } }
            store.SaveVideo(reference with { Id = id, AnimalId = index == 1 ? "KO_excluded" : "WT_missing" + index, Path = path, SourceSha256 = OpenCvTracker.HashFile(path), Metadata = OpenCvTracker.Probe(path), Included = index != 1, ExclusionReason = index != 1 ? "" : "WT_camera_A prespecified exclusion" });
        }
        var catalogWorkspace = Path.Combine(folder, "catalog-workspace"); var drafts = new List<SensitivityScenarioDraft> { new() { Name = "original", Rationale = "Prespecified reference" }, new() { Name = "adverse", Rationale = "Adverse threshold retained", Threshold = 0 }, new() { Name = "WT_failed", Rationale = "Injected failure stays visible", Threshold = 254 } };
        using (var freeze = SensitivityCatalog.Freeze(AtlasSnapshot.Read(store.DatabasePath), drafts, catalogWorkspace, new FailNamedNativeTracker())) { freeze.Acknowledge(); }
        var freezePath = Path.Combine(catalogWorkspace, "freeze", "sensitivity-freeze.json");
        var unrun = SensitivityStabilityAdapter.Read(freezePath, store.DatabasePath); Assert.Equal(90, unrun.Report.Trials.Length); Assert.Contains(unrun.Report.Trials, row => row.Holds.Any(h => h.Contains("missing"))); Assert.Contains(unrun.Report.Trials, row => row.Excluded);
        Assert.Equal(4, ReplayCommand.Execute(["replay", Path.Combine(catalogWorkspace, "freeze", "plan.json"), Path.Combine(catalogWorkspace, "replay")], new ReplayCommandContext { Tracker = new FailNamedNativeTracker(), Out = new StringWriter(), Error = new StringWriter() }));
        snapshot = SensitivityStabilityAdapter.Read(freezePath, store.DatabasePath); Assert.Equal(90, snapshot.Report.Trials.Length);
        var activeManifestPath = Path.Combine(catalogWorkspace, "replay", "replay-manifest.json");
        var regionPath = RegionEvidence(store, folder);
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        StabilityWindow? window = null;
        try
        {
            window = new StabilityWindow(store, true); window.Show(); window.Width = 1050; window.Height = 700; window.UpdateLayout(); window.EvidencePath.Text = freezePath;
            Click(window.LoadButton); Pump(window.PendingOperation); Assert.NotNull(window.Evidence); Assert.False(window.IsStale); Assert.Equal(90, window.Evidence.Report.Trials.Length); Assert.Equal(30, store.Videos().Count); Assert.False(window.PopulationDiagnostics.IsExpanded);
            Assert.True(window.EvidencePath.Visibility == Visibility.Collapsed); var labels = string.Join("|", Descendants(window).OfType<TextBlock>().Select(t => t.Text).Concat(Descendants(window).OfType<ListBoxItem>().Select(i => i.Content?.ToString())));
            foreach (var secret in new[] { "WT", "KO", "camera_A", "camera_B", "WT_failed", recordings[0].ExpectedSourceSha256! }) Assert.DoesNotContain(secret, labels);
            window.OutputPath.Text = Path.Combine(folder, "blind-report"); Click(window.ExportButton); Pump(window.PendingOperation); var blind = File.ReadAllText(Path.Combine(window.OutputPath.Text, "stability-input.json")); Assert.DoesNotContain("WT", blind); Assert.DoesNotContain("KO", blind); Assert.DoesNotContain(".avi", blind);
            window.SelectCase(89); window.UpdateLayout(); Pump(Task.Delay(40)); Assert.False(window.TraceButton.IsEnabled); SaveImage(window, Path.Combine(folder, "stability-30recording-blind-minimum.png"));
            window.SelectCase(0); Click(window.TraceButton); Assert.NotNull(window.LastTrace); Pump(window.LastTrace.PendingOperation); Assert.Equal(0, window.LastTrace.DisplayedFrame); Assert.NotNull(window.LastTrace.DisplayedBitmap);
            // Independently encoded fixture: BGR background at top-left is (180,190,200); JPEG decoder presentation preserves it within tolerance.
            var bitmap = new FormatConvertedBitmap(window.LastTrace.DisplayedBitmap, PixelFormats.Bgra32, null, 0); var pixels = new byte[4]; bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixels, 4, 0); Assert.InRange(pixels[0], 175, 185); Assert.InRange(pixels[2], 195, 205);
            window.LastTrace.Close(); Assert.Equal(0, window.LastTrace.Statistics.ActiveSessions);
            Click(window.TraceButton); Assert.NotNull(window.LastTrace); Pump(window.LastTrace.PendingOperation); var firstTrace = window.LastTrace;
            window.SelectCase(1); Click(window.TraceButton); Assert.NotNull(window.LastTrace); Pump(window.LastTrace.PendingOperation); var secondTrace = window.LastTrace;
            Assert.NotSame(firstTrace, secondTrace); Assert.False(firstTrace.IsVisible); Assert.Equal(0, firstTrace.Statistics.ActiveSessions); Assert.Equal(1, secondTrace.Statistics.ActiveSessions);
            Assert.False(window.TryRevealMetadata("", "reason")); Assert.True(window.IsBlinded);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + store.DatabasePath)) { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER block_stability_reveal BEFORE INSERT ON audit BEGIN SELECT RAISE(ABORT,'synthetic audit failure'); END"; command.ExecuteNonQuery(); }
            Assert.False(window.TryRevealMetadata("reviewer", "reason")); Assert.True(window.IsBlinded);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + store.DatabasePath)) { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TRIGGER block_stability_reveal"; command.ExecuteNonQuery(); }
            Assert.True(window.TryRevealMetadata("synthetic reviewer", "verify full frozen evidence")); Assert.False(window.IsBlinded);
            window.OutputPath.Text = Path.Combine(folder, "full-report"); Click(window.ExportButton); Pump(window.PendingOperation); var full = System.Text.Json.JsonSerializer.Deserialize<StabilityReport>(File.ReadAllText(Path.Combine(window.OutputPath.Text, "stability-input.json")))!;
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(snapshot.Report), System.Text.Json.JsonSerializer.Serialize(StabilityReportWriter.ReplayAggregation(Path.Combine(window.OutputPath.Text, "stability-input.json"))));
            Assert.Throws<IOException>(() => StabilityReportWriter.Save(window.OutputPath.Text, snapshot.Report, false));
            var manifestBytes = File.ReadAllBytes(activeManifestPath); File.AppendAllText(activeManifestPath, " "); Pump(window.CheckFreshnessAsync()); Assert.True(window.IsStale); Assert.False(window.ExportButton.IsEnabled);
            File.WriteAllBytes(activeManifestPath, manifestBytes); Click(window.LoadButton); Pump(window.PendingOperation); Assert.False(window.IsStale);
            var saved = window.Evidence; using var started = new ManualResetEventSlim(); window.Loader = (_, token) => { started.Set(); token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)); token.ThrowIfCancellationRequested(); return snapshot; };
            Click(window.LoadButton); Assert.True(started.Wait(TimeSpan.FromSeconds(2))); Click(window.CancelButton); Pump(window.PendingOperation); Assert.Same(saved, window.Evidence); Assert.True(window.IsStale);
            window.UpdateLayout(); Pump(Task.Delay(40)); SaveImage(window, Path.Combine(folder, "stability-minimum.png"));
            window.Loader = (path, token) => path.EndsWith("stability-input.json", StringComparison.Ordinal) ? StabilitySavedReplay.Read(path, token) : RegionStabilityAdapter.Read(path, store.DatabasePath, token);
            window.EvidencePath.Text = regionPath; Click(window.LoadButton); Pump(window.PendingOperation); Assert.NotNull(window.Evidence); Assert.Equal(30, window.Evidence.Report.Trials.Length);
            window.SelectCase(0); Assert.False(window.PopulationDiagnostics.IsExpanded); Assert.Equal(.5, window.Evidence.Report.Trials[0].Changed.Value); Assert.Equal(5, window.Evidence.Report.Trials[0].Trace!.OutlierFrame);
            Click(window.TraceButton); Assert.NotNull(window.LastTrace); Pump(window.LastTrace.PendingOperation); var regionTrace = window.LastTrace; Assert.Equal(5, regionTrace.DisplayedFrame); Assert.NotNull(regionTrace.DisplayedBitmap);
            window.UpdateLayout(); Pump(Task.Delay(40)); SaveImage(window, Path.Combine(folder, "stability-30recording-outlier-minimum.png"));
            window.OutputPath.Text = Path.Combine(folder, "region-authenticated-report"); Click(window.ExportButton); Pump(window.PendingOperation); Assert.Equal("completed", window.PublicationState);
            window.EvidencePath.Text = Path.Combine(window.OutputPath.Text, "stability-input.json"); Click(window.LoadButton); Pump(window.PendingOperation); Assert.NotNull(window.Evidence); Assert.False(window.IsStale); Assert.Equal(30, window.Evidence.Report.Trials.Length);
            SupportedGuiProof(folder);
            File.AppendAllText(activeManifestPath, " "); Pump(window.CheckFreshnessAsync()); Assert.False(snapshot.IsCurrent());
            Assert.Equal(sourceHashes, recordings.Select(r => OpenCvTracker.HashFile(r.VideoPath!))); Assert.Equal(baselineHashes, recordings.Select(r => OpenCvTracker.HashFile(r.BaselineAnalysisPath!)));
            window.Close(); Assert.False(secondTrace.IsVisible); Assert.Equal(0, secondTrace.Statistics.ActiveSessions); Assert.False(regionTrace.IsVisible); Assert.Equal(0, regionTrace.Statistics.ActiveSessions);
            using (var stream = new FileStream(recordings[0].VideoPath!, FileMode.Append, FileAccess.Write)) stream.WriteByte(0);
            var sourceHeld = DetectorStabilityAdapter.Read(manifestPath); Assert.Equal(6, sourceHeld.Report.Trials.Length); Assert.All(sourceHeld.Report.Trials, row => { Assert.Null(row.Trace); Assert.Contains(row.Holds, h => h.Contains("verification failed")); });
            messages.Add("Evidence: " + folder); messages.Add("Actual30recording native catalog:2analyzed FFV1sources×3scenarios;4complete +2injected native-adapter failure;27missing automatic baselines+1excluded recording ×3scenarios retained as84held/unrun rows (90total). Raw biological holds preserved. WPF minimum/trace pixels/privacy/audit/export/replay/cancel/source hashes verified.");
        }
        finally { window?.Close(); app.Shutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
    }
    private static string RegionEvidence(StudyStore store, string folder)
    {
        var snapshot = AtlasSnapshot.Read(store.DatabasePath); var identity = RegionScenarioBaseline.Capture(snapshot, null); var material = RegionScenarioBaseline.Material(snapshot, null);
        ImmutableArray<NamedRegionScenario> scenarios = [new() { Name = "site-shift", Rationale = "Prespecified synthetic platform center −40source pixels", Kind = RegionScenarioKind.PlatformCenterExtent, CenterDeltaX = -40, ComponentRange = new(DisclosedUnit.SourcePixels, -40, 40) }];
        var recordings = snapshot.Recordings.Select(row => row.EffectiveRun() is { } run ? new RecordingScenarioComputation { VideoId = row.Asset.Id,
            Set = RegionScenarioMath.Evaluate(run.Points, run.Metadata, run.Settings, scenarios, RegionScenarioBaseline.Header(identity, identity.Sources.Single(s => s.VideoId == row.Asset.Id))) }
            : new RecordingScenarioComputation { VideoId = row.Asset.Id, RecordingHolds = ["No automatic baseline; imported case stays unrun."] }).ToImmutableArray();
        var computation = new RegionScenarioComputation { Identity = identity, Scenarios = scenarios, Recordings = recordings, Replay = material };
        var json = JsonDefaults.Serialize(computation); var materialJson = JsonDefaults.Serialize(material);
        static string Digest(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
        return RegionScenarioEvidenceStore.Append(folder, new() { Identity = identity, ComputationJson = json, ComputationSha256 = Digest(json), FrozenMaterialJson = materialJson, FrozenMaterialSha256 = Digest(materialJson) });
    }
    private static void SupportedGuiProof(string folder)
    {
        var fixture = new SupportedWindowTests.Fixture(); var window = new StabilityWindow(fixture.Store, true);
        try
        {
            window.Show(); window.Width = 1050; window.Height = 700; window.EvidencePath.Text = fixture.EvidencePath; Click(window.LoadButton); Pump(window.PendingOperation);
            Assert.NotNull(window.Evidence); Assert.Contains(window.Evidence.Report.Contrasts, c => c.Conclusion.Contains("Unstable")); Assert.Equal(20, window.Evidence.Report.Trials.Length);
            var index = Array.FindIndex(window.Evidence.Report.Trials.ToArray(), row => row.AnimalId == "outlier" && row.Scenario == "large-shift"); window.SelectCase(index); Assert.True(window.TraceButton.IsEnabled);
            Click(window.TraceButton); Assert.NotNull(window.LastTrace); Pump(window.LastTrace.PendingOperation); Assert.Equal(0, window.LastTrace.DisplayedFrame); Assert.NotNull(window.LastTrace.DisplayedBitmap);
            var text = string.Join("|", Descendants(window).OfType<TextBlock>().Select(t => t.Text)); foreach (var secret in new[] { "WT", "KO", "outlier", "A1", fixture.Document.Identity.Sources[0].Sha256 }) Assert.DoesNotContain(secret, text);
            window.UpdateLayout(); Pump(Task.Delay(40)); SaveImage(window, Path.Combine(folder, "supported-outlier-blind-1050.png"));
            window.OutputPath.Text = Path.Combine(fixture.Folder, "blind-gui-report"); Click(window.ExportButton); Pump(window.PendingOperation); Assert.Equal("completed", window.PublicationState); var blind = File.ReadAllText(Path.Combine(window.OutputPath.Text, "stability-input.json")); Assert.DoesNotContain("WT", blind); Assert.DoesNotContain("KO", blind);
            Assert.True(window.TryRevealMetadata("Synthetic reviewer", "Inspect complete supported frozen producer material"));
            window.OutputPath.Text = Path.Combine(fixture.Folder, "full-gui-report"); Click(window.ExportButton); Pump(window.PendingOperation);
            window.EvidencePath.Text = Path.Combine(window.OutputPath.Text, "stability-input.json"); Click(window.LoadButton); Pump(window.PendingOperation); Assert.NotNull(window.Evidence); Assert.Contains(window.Evidence.Report.Contrasts, c => c.Conclusion.Contains("Unstable"));
            window.Width = 1440; window.Height = 960; window.UpdateLayout(); Pump(Task.Delay(40)); SaveImage(window, Path.Combine(folder, "supported-authenticated-replay-1440.png"));
            File.WriteAllText(Path.Combine(folder, "supported-fixture-location.txt"), fixture.Folder);
        }
        finally { var viewer = window.LastTrace; window.Close(); if (viewer is not null) Assert.Equal(0, viewer.Statistics.ActiveSessions); }
    }
    private sealed class FailNamedNativeTracker : IReplayTracker
    {
        private readonly NativeReplayTracker _native = new(); public string NativeVersion => _native.NativeVersion; public string HashFile(string path) => _native.HashFile(path); public VideoMetadata Probe(string path) => _native.Probe(path);
        public AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation) => asset.Settings.Threshold == 254 ? throw new InvalidDataException("Injected synthetic case failure retained") : _native.Analyze(asset, cancellation);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump(Task task) { var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) }; timer.Tick += (_, _) => frame.Continue = false; timer.Start(); task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); timer.Stop(); Assert.True(task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void SaveImage(Window window, string path) { var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream); }
    private static string Repository() { var folder = new DirectoryInfo(AppContext.BaseDirectory); while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "MouseTrackerTabajara.sln"))) folder = folder.Parent; return folder?.FullName ?? throw new DirectoryNotFoundException(); }
}
