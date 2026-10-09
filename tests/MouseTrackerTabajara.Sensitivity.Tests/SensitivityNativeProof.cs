using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Window = System.Windows.Window;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseTrackerTabajara.Cli;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Sensitivity.Tests;

public partial class SensitivityNativeProof(ITestOutputHelper output)
{
    [Fact]
    public void SyntheticWorkspacePreservesCasesIdentitiesAndSourcePixels()
    {
        Exception? error = null;
        var evidence = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                Proof(evidence);
                ProveConnectedLifecycle(evidence);
                ProveHistoricalInventory(evidence);
                ProveTwoWindowFreezeOwnership(evidence);
            }
            catch (Exception ex) { error = ex; }
            finally { System.Windows.Application.Current?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(8)), "Sensitivity proof did not finish.");
        foreach (var line in evidence) output.WriteLine(line);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Proof(List<string> evidence)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-sensitivity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        SensitivityWindow? window = null;
        try
        {
            var store = new StudyStore(Path.Combine(folder, "synthetic.morrisdb"));
            var settings = DetectorSettings();
            var first = Import(store, folder, "rec-a", "PRIVATE-WT-KO-A.avi", 0, "A", "WT", "Informative WT KO alias", true, settings);
            var second = Import(store, folder, "rec-b", "PRIVATE-WT-KO-B.avi", 1, "B", "KO", "Informative WT KO alias", true, settings);
            Import(store, folder, "rec-missing", "PRIVATE-WT-KO-missing.avi", 2, "A", "WT", "Informative WT KO alias", false, settings);
            var excluded = Import(store, folder, "rec-excluded", "PRIVATE-WT-KO-excluded.avi", 3, "B", "KO", "Informative WT KO alias", false, settings);
            store.SaveVideo(excluded.Asset with { Included = false, ExclusionReason = "FREE WT KO A B reason" });
            var invalid = Import(store, folder, "rec-invalid", "PRIVATE-WT-KO-invalid.avi", 4, "A", "WT", "Informative WT KO alias", false, settings);
            store.SaveRun(new AnalysisRun
            {
                Id = Guid.NewGuid().ToString("N"),
                VideoId = invalid.Asset.Id,
                SourceSha256 = invalid.Asset.SourceSha256,
                Metadata = invalid.Asset.Metadata,
                Settings = invalid.Asset.Settings,
                AlgorithmContract = AlgorithmIdentity.Contract,
                NominalTimingVerified = false
            });
            var baselineJson = ReadRunJson(store.DatabasePath, "rec-a");
            var videoBytes = File.ReadAllBytes(first.Asset.Path);
            var secondVideo = File.ReadAllBytes(second.Asset.Path);
            window = new SensitivityWindow(store.DatabasePath, true, false);
            Layout(window);
            Assert.Equal(1050, window.MinWidth);
            Assert.Equal(700, window.MinHeight);
            AssertPrivate(window);
            Assert.Contains(Texts(window), text => text.Contains("missing", StringComparison.Ordinal));
            Assert.Contains(Texts(window), text => text.Contains("excluded", StringComparison.Ordinal));
            Assert.Contains(Texts(window), text => text.Contains("invalid", StringComparison.Ordinal));
            Assert.False(window.TryUnblind("", ""));
            Assert.True(window.Blinded);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER deny_sensitivity_unblind BEFORE INSERT ON audit WHEN NEW.action='sensitivity_unblinded' BEGIN SELECT RAISE(ABORT,'synthetic denied audit'); END";
                command.ExecuteNonQuery();
                Assert.False(window.TryUnblind("Reviewer", "Audit must succeed before reveal"));
                Assert.True(window.Blinded);
                AssertPrivate(window);
                command.CommandText = "DROP TRIGGER deny_sensitivity_unblind";
                command.ExecuteNonQuery();
            }
            Assert.True(window.TryUnblind("Reviewer", "Reasoned synthetic sensitivity inspection"));
            Assert.False(window.Blinded);
            Layout(window);
            Click(window, "Add the seven prespecified families");
            window.ScenarioNameBox.Text = "retain-failure";
            window.RationaleBox.Text = "Retain an explicit detector failure beside the prespecified families.";
            window.ThresholdBox.Text = "1";
            Click(window, "Save named scenario");
            Layout(window);
            var scenarioText = string.Join("\n", Texts(window));
            foreach (var name in new[] { "original-detector", "threshold-zero", "polarity-off", "blur-3", "area-tight", "extent-1", "jump-1", "retain-failure" })
                Assert.Contains(name, scenarioText, StringComparison.Ordinal);
            Click(window, "Freeze plan and identities");
            Pump(window.PendingRun, 60);
            evidence.Add("Freeze diagnostic: visible="+window.IsVisible+" shutdown="+window.Dispatcher.HasShutdownStarted+" appMode="+System.Windows.Application.Current?.ShutdownMode+" held="+window.Held+" exit="+window.LastExitCode+" status="+window.StatusText);
            Assert.NotNull(window.Freeze);
            Assert.False(window.Held, window.StatusText);
            var frozenBaseline = File.ReadAllBytes(Path.Combine(folder, "sensitivity-workspace", "freeze", "baselines", "rec-a.json"));
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(baselineJson), frozenBaseline);
            Assert.Equal(baselineJson, ReadRunJson(store.DatabasePath, "rec-a"));
            Assert.Equal(videoBytes, File.ReadAllBytes(first.Asset.Path));
            var gate = new GateTracker();
            window.Tracker = gate;
            window.CancelAfterCompleted = 2;
            Click(window, "Run frozen replay");
            Pump(gate.Started.Task, 30);
            var responsive = false;
            window.Dispatcher.BeginInvoke(() => responsive = true);
            Pump(Task.Delay(150), 10);
            Assert.True(responsive);
            Assert.False(window.PendingRun.IsCompleted);
            gate.Release.TrySetResult();
            Pump(window.PendingRun, 120);
            Assert.Equal(3, window.LastExitCode);
            Assert.NotNull(window.Manifest);
            Assert.Equal(16, window.Manifest!.PlannedCaseCount);
            Assert.Equal(2, window.Manifest.Cases.Count(item => item.Status == ReplayStatuses.Completed));
            Assert.Contains(window.Manifest.Cases, item => item.Status == ReplayStatuses.Unrun);
            Assert.Null(window.Manifest.BiologicalN);
            Layout(window);
            Click(window, "rec-a · jump-1 · unrun");
            Pump(window.PendingView, 30);
            var unrunViewer = window.LastViewer!;
            Assert.False(unrunViewer.Held, unrunViewer.StatusText);
            AssertPixels(unrunViewer.DisplayedBitmap!, 0, 0);
            Assert.Empty(Markers(unrunViewer, "active-source-marker"));
            Assert.Empty(Markers(unrunViewer, "gap-marker"));
            Click(window, "rec-a · original-detector · completed");
            Pump(window.PendingView, 30);
            Assert.True(unrunViewer.ResourcesReleased);
            var viewer = window.LastViewer!;
            Assert.False(viewer.Held, viewer.StatusText);
            AssertPixels(viewer.DisplayedBitmap!, 0, 0);
            var original = LoadRun(folder, "rec-a", "original-detector");
            var origin = original.Points.Single(point => point.Frame == 0);
            Assert.Equal(PointStatus.Tracked, origin.Status);
            var marker = Assert.Single(Markers(viewer, "active-source-marker"));
            Assert.Equal((double)origin.X!, Canvas.GetLeft(marker) + 9);
            Assert.Equal((double)origin.Y!, Canvas.GetTop(marker) + 9);
            var published = Snapshot(Attempt(folder, "rec-a", "original-detector", 1));
            window.CancelAfterCompleted = null;
            Click(window, "Resume frozen replay");
            Pump(window.PendingRun, 180);
            Assert.Equal(4, window.LastExitCode);
            AssertSame(published, Attempt(folder, "rec-a", "original-detector", 1));
            Assert.Equal(baselineJson, ReadRunJson(store.DatabasePath, "rec-a"));
            Assert.Equal(videoBytes, File.ReadAllBytes(first.Asset.Path));
            Assert.Equal(secondVideo, File.ReadAllBytes(second.Asset.Path));
            Assert.Equal(14, window.Manifest!.Cases.Count(item => item.Status == ReplayStatuses.Completed));
            Assert.Equal(2, window.Manifest.Cases.Count(item => item.Status == ReplayStatuses.Failed));
            Assert.DoesNotContain(window.Manifest.Cases, item => item.Status == ReplayStatuses.Unrun);
            Assert.Contains(window.Manifest.Cases, item => item.ScenarioName == "retain-failure" && item.Status == ReplayStatuses.Failed && (item.ErrorMessage ?? "").Contains("Synthetic detector failure", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(Attempt(folder, "rec-a", "retain-failure", 1), "..", "attempt-0002")));
            foreach (var name in new[] { "original-detector", "threshold-zero", "polarity-off", "blur-3", "area-tight", "extent-1", "jump-1" })
                Assert.Equal(2, window.Manifest.Cases.Count(item => item.ScenarioName == name && item.Status == ReplayStatuses.Completed));
            var jump = LoadRun(folder, "rec-a", "jump-1");
            var jumped = jump.Points.First(point => point.Status == PointStatus.Jump);
            Assert.True(jump.Metrics.TrackedFrames < original.Metrics.TrackedFrames || jump.Metrics.UnknownSeconds > original.Metrics.UnknownSeconds);
            Assert.Equal(original.Settings.Cx, jump.Settings.Cx);
            Assert.Equal(original.Settings.StartSeconds, jump.Settings.StartSeconds);
            Assert.Equal(original.Settings.EndSeconds, jump.Settings.EndSeconds);
            Assert.Equal(original.Settings.DiameterCm, jump.Settings.DiameterCm);
            Assert.Equal(original.Metrics.DistanceUnit, jump.Metrics.DistanceUnit);
            foreach (var item in window.Manifest.Cases.Where(item => item.EndpointDifferences is not null))
                Assert.DoesNotContain(item.EndpointDifferences!, difference => CompletedRunContract.WithheldBiologicalEndpoints.Contains(difference.Name, StringComparer.Ordinal));
            Layout(window);
            Click(window, "rec-a · jump-1 · completed");
            Pump(window.PendingView, 30);
            viewer = window.LastViewer!;
            Assert.False(viewer.Held, viewer.StatusText);
            viewer.ShowFrame(jumped.Frame);
            Pump(viewer.PendingFrame, 30);
            AssertPixels(viewer.DisplayedBitmap!, 0, jumped.Frame);
            var gap = Assert.Single(Markers(viewer, "gap-marker"));
            Assert.Equal((double)jumped.TentativeX!, Canvas.GetLeft(gap) + 9);
            Assert.Equal((double)jumped.TentativeY!, Canvas.GetTop(gap) + 9);
            Assert.Empty(Markers(viewer, "active-source-marker"));
            Click(viewer, "Baseline path");
            var baselinePoint = first.Run.Points.Single(point => point.Frame == jumped.Frame);
            Assert.Equal(PointStatus.Tracked, baselinePoint.Status);
            var baselineMarker = Assert.Single(Markers(viewer, "active-source-marker"));
            Assert.Equal((double)baselinePoint.X!, Canvas.GetLeft(baselineMarker) + 9);
            Click(viewer, "Correction state");
            Assert.Contains("not invented", viewer.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Markers(viewer, "active-source-marker"));
            Assert.Empty(Markers(viewer, "gap-marker"));
            Click(viewer, "Scenario path");
            viewer.ShowFrame(0);
            Pump(viewer.PendingFrame, 30);
            AssertPixels(viewer.DisplayedBitmap!, 0, 0);
            Click(window, "rec-b · original-detector · completed");
            Pump(window.PendingView, 30);
            Assert.True(viewer.ResourcesReleased);
            Assert.True(viewer.DecoderStatistics.Disposals >= 1);
            Assert.Equal(0, viewer.DecoderStatistics.ActiveSessions);
            var other = window.LastViewer!;
            AssertPixels(other.DisplayedBitmap!, 1, 0);
            var reads = other.DecoderStatistics.Reads;
            Assert.True(reads >= 1);
            Layout(window);
            var rows = Find<TextBlock>(window).Where(block => Equals(block.Tag, "endpoint-row")).Select(block => block.Text).ToArray();
            Assert.NotEmpty(rows);
            Assert.All(rows, text => Assert.DoesNotContain(CompletedRunContract.WithheldBiologicalEndpoints, name => text.StartsWith(name + ":", StringComparison.Ordinal)));
            Assert.Contains(Texts(window), text => text.Contains("not a biological release", StringComparison.OrdinalIgnoreCase));
            var beforeSecondResume = Snapshot(Attempt(folder, "rec-a", "original-detector", 1));
            Click(window, "Resume frozen replay");
            Pump(window.PendingRun, 60);
            Assert.Equal(4, window.LastExitCode);
            AssertSame(beforeSecondResume, Attempt(folder, "rec-a", "original-detector", 1));
            Assert.False(File.Exists(Path.Combine(Attempt(folder, "rec-a", "retain-failure", 2), "replay-case.json")));
            var manifestPath = Path.Combine(folder, "sensitivity-workspace", "replay", "replay-manifest.json");
            var manifestText = File.ReadAllText(manifestPath);
            var nameAt = manifestText.IndexOf("\"name\": \"MouseTrackerTabajara.Cli\"", StringComparison.Ordinal);
            var shaAt = manifestText.IndexOf("\"sha256\": \"", nameAt, StringComparison.Ordinal);
            var shaStart = shaAt + "\"sha256\": \"".Length;
            manifestText = manifestText.Remove(shaStart, 64).Insert(shaStart, new string('b', 64));
            File.WriteAllText(manifestPath, manifestText);
            var corrupted = File.ReadAllBytes(manifestPath);
            var keptAnalysis = File.ReadAllBytes(Path.Combine(Attempt(folder, "rec-a", "original-detector", 1), "analysis.json"));
            Click(window, "Resume frozen replay");
            Pump(window.PendingRun, 60);
            Assert.Equal(2, window.LastExitCode);
            Assert.Contains("Assembly identity", window.StatusText, StringComparison.Ordinal);
            Assert.Equal(corrupted, File.ReadAllBytes(manifestPath));
            Assert.Equal(keptAnalysis, File.ReadAllBytes(Path.Combine(Attempt(folder, "rec-a", "original-detector", 1), "analysis.json")));
            window.Blinded = true;
            Layout(window);
            AssertPrivate(window);
            Assert.Contains(Texts(window), text => text.Contains("Recording 001", StringComparison.Ordinal));
            Assert.DoesNotContain(Texts(window), text => text.Contains("PRIVATE", StringComparison.Ordinal));
            Click(window, "Recording 001 · Scenario 001 · completed");
            Pump(window.PendingView, 30);
            var blindViewer = window.LastViewer!;
            Layout(blindViewer);
            AssertPrivate(blindViewer);
            Assert.DoesNotContain(first.Asset.SourceSha256, string.Join("\n", Texts(blindViewer)), StringComparison.OrdinalIgnoreCase);
            blindViewer.Close();
            Assert.True(blindViewer.ResourcesReleased);
            var flipped = File.ReadAllBytes(first.Asset.Path);
            flipped[^1] ^= 1;
            File.WriteAllBytes(first.Asset.Path, flipped);
            Assert.True(window.TryUnblind("Reviewer", "Inspect a changed source without revealing it as a result"));
            Click(window, "rec-a · original-detector · completed");
            Pump(window.PendingView, 30);
            Assert.NotNull(window.LastViewer);
            Assert.True(window.LastViewer!.Held, window.LastViewer.StatusText);
            Assert.Null(window.LastViewer.DisplayedBitmap);
            Assert.Empty(window.LastViewer.SourceOverlay.Children.OfType<System.Windows.Shapes.Ellipse>());
            flipped[^1] ^= 1;
            File.WriteAllBytes(first.Asset.Path, flipped);
            Pump(window.CheckIntegrityAsync(), 30);
            Assert.True(window.Held, window.StatusText);
            Assert.Contains("source identity", window.StatusText, StringComparison.OrdinalIgnoreCase);
            var manifestBeforeSnapshot = File.ReadAllBytes(manifestPath);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE videos SET json = replace(json, '\"animal_id\": \"A\"', '\"animal_id\": \"A2\"') WHERE id = 'rec-a'";
                Assert.Equal(1, command.ExecuteNonQuery());
                command.CommandText = "SELECT json FROM videos WHERE id = 'rec-a'";
                Assert.Contains("\"animal_id\": \"A2\"", (string)command.ExecuteScalar()!, StringComparison.Ordinal);
            }
            Click(window, "Resume frozen replay");
            Pump(window.PendingRun, 30);
            Assert.Equal(2, window.LastExitCode);
            Assert.Contains("Study snapshot changed", window.StatusText, StringComparison.Ordinal);
            Assert.Equal(manifestBeforeSnapshot, File.ReadAllBytes(manifestPath));
            Assert.Equal(baselineJson, ReadRunJson(store.DatabasePath, "rec-a"));
            Assert.Equal(videoBytes, File.ReadAllBytes(first.Asset.Path));
            evidence.Add("PASS synthetic WPF sensitivity workspace froze two sources, ran the seven prespecified families plus a retained failure, cancelled and resumed, and kept baseline, source, and publication bytes.");
            evidence.Add("PASS blinded tree hides WT/KO, A/B, filenames, hashes, and free notes; unblinding requires a successful audit; source and code mismatch hold playback without invented markers.");
        }
        finally
        {
            window?.Close();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static void ProveConnectedLifecycle(List<string> evidence)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-sensitivity-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        SensitivityWindow? window = null;
        try
        {
            var store = new StudyStore(Path.Combine(folder, "synthetic.morrisdb"));
            var settings = DetectorSettings();
            var first = Import(store, folder, "rec-a", "PRIVATE-WT-KO-A.avi", 0, "A", "WT", "Informative WT KO alias", true, settings);
            var second = Import(store, folder, "rec-b", "PRIVATE-WT-KO-B.avi", 1, "B", "KO", "Informative WT KO alias", true, settings);
            var firstRun = ReadRunJson(store.DatabasePath, "rec-a");
            var secondRun = ReadRunJson(store.DatabasePath, "rec-b");
            var firstVideo = File.ReadAllBytes(first.Asset.Path);
            var secondVideo = File.ReadAllBytes(second.Asset.Path);
            window = OpenSensitive(store.DatabasePath);
            Unblind(window, "Synthetic lifecycle inspection");
            window.ScenarioNameBox.Text = "lifecycle";
            window.RationaleBox.Text = "PRIVATE WT KO note recorded before the delayed blind check.";
            window.ThresholdBox.Text = "40";
            Click(window, "Save named scenario");
            Layout(window);
            Click(window, "Freeze plan and identities");
            Pump(window.PendingRun, 60);
            evidence.Add("Freeze diagnostic: visible="+window.IsVisible+" shutdown="+window.Dispatcher.HasShutdownStarted+" appMode="+System.Windows.Application.Current?.ShutdownMode+" held="+window.Held+" exit="+window.LastExitCode+" status="+window.StatusText);
            Assert.NotNull(window.Freeze);
            Assert.False(window.Held, window.StatusText);
            Assert.Equal(2, window.Freeze!.Recordings.Count);
            var workspace = Path.Combine(folder, "sensitivity-workspace");
            var freezeDirectory = Path.Combine(workspace, "freeze");
            var frozen = Snapshot(freezeDirectory);
            AssertSettledFreeze(workspace);
            var plan = window.Freeze.PlanSha256;
            var revision = window.Freeze.SnapshotRevision;

            var opens = new OpenGate();
            window.Tracker = opens;
            Click(window, "rec-a · lifecycle · unrun");
            Pump(opens.FirstEntered.Task, 30);
            Click(window, "rec-b · lifecycle · unrun");
            Pump(opens.SecondEntered.Task, 30);
            opens.ReleaseSecond.TrySetResult();
            PumpUntil(() => window.LastViewer?.IdentityText.Contains("rec-b", StringComparison.Ordinal) == true, 20);
            var current = window.LastViewer!;
            Pump(current.PendingFrame, 30);
            Assert.False(current.Held, current.StatusText);
            AssertPixels(current.DisplayedBitmap!, 1, 0);
            Assert.Equal(second.Asset.SourceSha256, current.DisplayedSourceSha256);
            Assert.Contains("rec-b", current.IdentityText, StringComparison.Ordinal);
            Assert.DoesNotContain("rec-a", current.IdentityText, StringComparison.Ordinal);
            opens.ReleaseFirst.TrySetResult();
            Pump(opens.FirstReturned.Task, 30);
            Pump(Task.Delay(1000), 15);
            Assert.Same(current, window.LastViewer);
            Assert.False(current.ResourcesReleased);
            Pump(current.PendingFrame, 30);
            AssertPixels(current.DisplayedBitmap!, 1, 0);
            Assert.Equal(second.Asset.SourceSha256, current.DisplayedSourceSha256);
            Assert.Contains("rec-b", current.IdentityText, StringComparison.Ordinal);
            Assert.DoesNotContain("rec-a", current.IdentityText, StringComparison.Ordinal);

            var delayed = new HashGate();
            window.Tracker = delayed;
            Click(window, "rec-a · lifecycle · unrun");
            Pump(delayed.Entered.Task, 30);
            Assert.Contains(Texts(window), text => text.Contains("rec-a", StringComparison.Ordinal));
            Assert.Contains(Texts(window), text => text.Contains("PRIVATE", StringComparison.Ordinal));
            Assert.Contains("rec-b", current.IdentityText, StringComparison.Ordinal);
            window.Blinded = true;
            Layout(window);
            Assert.Null(window.LastViewer);
            Assert.Equal("", current.IdentityText);
            Assert.Equal("", current.StatusText);
            Assert.Null(current.DisplayedBitmap);
            Assert.True(current.ResourcesReleased);
            AssertPrivate(window);
            Assert.DoesNotContain(Texts(window), text => text.Contains("rec-a", StringComparison.Ordinal) || text.Contains("rec-b", StringComparison.Ordinal));
            delayed.Release.TrySetResult();
            Pump(window.PendingView, 30);
            Assert.Null(window.LastViewer);
            AssertPrivate(window);
            Assert.DoesNotContain(Texts(window), text => text.Contains("rec-a", StringComparison.Ordinal) || text.Contains("PRIVATE", StringComparison.Ordinal));

            Unblind(window, "Inspect close during a delayed source open");
            window.Tracker = new NativeReplayTracker();
            Click(window, "rec-a · lifecycle · unrun");
            Pump(window.PendingView, 30);
            var shown = window.LastViewer!;
            Pump(shown.PendingFrame, 30);
            Assert.Contains("rec-a", shown.IdentityText, StringComparison.Ordinal);
            AssertPixels(shown.DisplayedBitmap!, 0, 0);
            var closing = new HashGate();
            window.Tracker = closing;
            Click(window, "rec-b · lifecycle · unrun");
            Pump(closing.Entered.Task, 30);
            Assert.Contains(Texts(window), text => text.Contains("rec-b", StringComparison.Ordinal));
            var pendingView = window.PendingView;
            var closedView = window;
            window.Close();
            window = null;
            Assert.Null(closedView.LastViewer);
            Assert.Equal("", shown.IdentityText);
            Assert.Equal("", shown.StatusText);
            Assert.Null(shown.DisplayedBitmap);
            Assert.True(shown.ResourcesReleased);
            closing.Release.TrySetResult();
            Pump(pendingView, 30);
            Assert.Null(closedView.LastViewer);
            AssertSame(frozen, freezeDirectory);
            Assert.Equal(firstRun, ReadRunJson(store.DatabasePath, "rec-a"));
            Assert.Equal(secondVideo, File.ReadAllBytes(second.Asset.Path));

            window = OpenSensitive(store.DatabasePath);
            Unblind(window, "Cancel an in-flight freeze");
            Assert.Equal(plan, window.Freeze!.PlanSha256);
            var cancelGate = new HashGate();
            window.Tracker = cancelGate;
            Click(window, "Freeze plan and identities");
            Pump(cancelGate.Entered.Task, 30);
            var responsive = false;
            window.Dispatcher.BeginInvoke(() => responsive = true);
            Pump(Task.Delay(100), 10);
            Assert.True(responsive);
            Assert.False(window.PendingRun.IsCompleted);
            window.Cancel();
            Assert.Contains("cancelled", window.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Plan frozen", window.StatusText, StringComparison.Ordinal);
            cancelGate.Release.TrySetResult();
            Pump(window.PendingRun, 60);
            Assert.False(window.Held, window.StatusText);
            Assert.Equal(plan, window.Freeze.PlanSha256);
            Assert.Equal(revision, window.Freeze.SnapshotRevision);
            Assert.Contains("cancelled", window.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Plan frozen", window.StatusText, StringComparison.Ordinal);
            AssertSame(frozen, freezeDirectory);
            AssertSettledFreeze(workspace);
            Assert.Equal(firstVideo, File.ReadAllBytes(first.Asset.Path));
            Assert.Equal(firstRun, ReadRunJson(store.DatabasePath, "rec-a"));

            var closeFreeze = new HashGate();
            window.Tracker = closeFreeze;
            Click(window, "Freeze plan and identities");
            Pump(closeFreeze.Entered.Task, 30);
            var pendingFreeze = window.PendingRun;
            window.Close();
            window = null;
            closeFreeze.Release.TrySetResult();
            Pump(pendingFreeze, 60);
            AssertSame(frozen, freezeDirectory);
            AssertSettledFreeze(workspace);
            Assert.Equal(secondRun, ReadRunJson(store.DatabasePath, "rec-b"));

            window = OpenSensitive(store.DatabasePath);
            Unblind(window, "Stamp changed during freeze preparation");
            var stampGate = new ProbeGate { DelayOnProbe = 4 };
            window.Tracker = stampGate;
            Click(window, "Freeze plan and identities");
            Pump(stampGate.Entered.Task, 30);
            var shifted = File.GetLastWriteTimeUtc(second.Asset.Path).AddMinutes(5);
            File.SetLastWriteTimeUtc(second.Asset.Path, shifted);
            Assert.Equal(shifted, File.GetLastWriteTimeUtc(second.Asset.Path));
            Assert.Equal(secondVideo, File.ReadAllBytes(second.Asset.Path));
            stampGate.Release.TrySetResult();
            Pump(window.PendingRun, 60);
            Assert.True(window.Held, window.StatusText);
            Assert.Equal("A source changed while the plan was being frozen. No new freeze was published.", window.StatusText);
            Assert.Equal(plan, window.Freeze!.PlanSha256);
            AssertSame(frozen, freezeDirectory);
            AssertSettledFreeze(workspace);
            Assert.Equal(secondVideo, File.ReadAllBytes(second.Asset.Path));
            Assert.Equal(secondRun, ReadRunJson(store.DatabasePath, "rec-b"));
            window.Close();
            window = null;

            window = OpenSensitive(store.DatabasePath);
            Unblind(window, "Bytes changed during freeze preparation");
            var hashGate = new ProbeGate { DelayOnProbe = 4 };
            window.Tracker = hashGate;
            Click(window, "Freeze plan and identities");
            Pump(hashGate.Entered.Task, 30);
            var info = new FileInfo(first.Asset.Path);
            var written = info.LastWriteTimeUtc;
            var created = info.CreationTimeUtc;
            var flipped = firstVideo.ToArray();
            flipped[^1] ^= 1;
            File.WriteAllBytes(first.Asset.Path, flipped);
            File.SetCreationTimeUtc(first.Asset.Path, created);
            File.SetLastWriteTimeUtc(first.Asset.Path, written);
            info.Refresh();
            Assert.Equal(written, info.LastWriteTimeUtc);
            Assert.Equal(created, info.CreationTimeUtc);
            Assert.NotEqual(first.Asset.SourceSha256, OpenCvTracker.HashFile(first.Asset.Path));
            hashGate.Release.TrySetResult();
            Pump(window.PendingRun, 60);
            Assert.True(window.Held, window.StatusText);
            Assert.Contains("rec-a", window.StatusText, StringComparison.Ordinal);
            Assert.Contains("No new freeze was published.", window.StatusText, StringComparison.Ordinal);
            Assert.Equal(plan, window.Freeze!.PlanSha256);
            AssertSame(frozen, freezeDirectory);
            AssertSettledFreeze(workspace);
            Assert.Equal(firstRun, ReadRunJson(store.DatabasePath, "rec-a"));
            File.WriteAllBytes(first.Asset.Path, firstVideo);
            File.SetCreationTimeUtc(first.Asset.Path, created);
            File.SetLastWriteTimeUtc(first.Asset.Path, written);
            window.Close();
            window = null;

            window = OpenSensitive(store.DatabasePath);
            Unblind(window, "Database changed during freeze preparation");
            var databaseGate = new ProbeGate { DelayOnProbe = 4 };
            window.Tracker = databaseGate;
            Click(window, "Freeze plan and identities");
            Pump(databaseGate.Entered.Task, 30);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE videos SET json = replace(json, '\"animal_id\": \"A\"', '\"animal_id\": \"A2\"') WHERE id = 'rec-a'";
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            Assert.NotEqual(revision, AtlasSnapshot.Read(store.DatabasePath).Revision);
            databaseGate.Release.TrySetResult();
            Pump(window.PendingRun, 60);
            Assert.True(window.Held, window.StatusText);
            Assert.Equal("The study snapshot changed while the plan was being frozen. No new freeze was published.", window.StatusText);
            Assert.Equal(plan, window.Freeze!.PlanSha256);
            Assert.Equal(revision, window.Freeze.SnapshotRevision);
            AssertSame(frozen, freezeDirectory);
            AssertSettledFreeze(workspace);
            Assert.Contains("\"animal_id\": \"A\"", File.ReadAllText(Path.Combine(freezeDirectory, "plan.json")), StringComparison.Ordinal);
            Assert.DoesNotContain("A2", File.ReadAllText(Path.Combine(freezeDirectory, "plan.json")), StringComparison.Ordinal);
            Assert.Equal(firstRun, ReadRunJson(store.DatabasePath, "rec-a"));
            Assert.Equal(firstVideo, File.ReadAllBytes(first.Asset.Path));
            window.Close();
            window = null;

            evidence.Add("PASS delayed freeze keeps prior publications when a source stamp, source bytes, or the study database changes, and when cancel, close, or an obsolete generation arrives before publication.");
            evidence.Add("PASS delayed unblinded open then reblind clears private labels immediately; an older source open does not replace a newer selection; close during the open publishes no viewer.");
        }
        finally
        {
            window?.Close();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static void ProveHistoricalInventory(List<string> evidence)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-sensitivity-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        SensitivityWindow? window = null;
        try
        {
            var store = new StudyStore(Path.Combine(folder, "synthetic.morrisdb"));
            var settings = DetectorSettings();
            Import(store, folder, "rec-a", "PRIVATE-WT-KO-A.avi", 0, "A", "WT", "Informative WT KO alias", true, settings);
            Import(store, folder, "rec-b", "PRIVATE-WT-KO-B.avi", 1, "B", "KO", "Informative WT KO alias", true, settings);
            Import(store, folder, "rec-missing", "PRIVATE-WT-KO-missing.avi", 2, "A", "WT", "Informative WT KO alias", false, settings);
            var excluded = Import(store, folder, "rec-excluded", "PRIVATE-WT-KO-excluded.avi", 3, "B", "KO", "Informative WT KO alias", false, settings);
            store.SaveVideo(excluded.Asset with { Included = false, ExclusionReason = "FREE WT KO A B reason" });
            var invalid = Import(store, folder, "rec-invalid", "PRIVATE-WT-KO-invalid.avi", 4, "A", "WT", "Informative WT KO alias", false, settings);
            store.SaveRun(new AnalysisRun
            {
                Id = Guid.NewGuid().ToString("N"),
                VideoId = invalid.Asset.Id,
                SourceSha256 = invalid.Asset.SourceSha256,
                Metadata = invalid.Asset.Metadata,
                Settings = invalid.Asset.Settings,
                AlgorithmContract = AlgorithmIdentity.Contract,
                NominalTimingVerified = false
            });
            window = OpenSensitive(store.DatabasePath);
            Unblind(window, "Record the historical inventory");
            window.ScenarioNameBox.Text = "history";
            window.RationaleBox.Text = "Keep every observed recording status with the frozen plan.";
            window.ThresholdBox.Text = "40";
            Click(window, "Save named scenario");
            Layout(window);
            Click(window, "Freeze plan and identities");
            Pump(window.PendingRun, 60);
            evidence.Add("Freeze diagnostic: visible="+window.IsVisible+" shutdown="+window.Dispatcher.HasShutdownStarted+" appMode="+System.Windows.Application.Current?.ShutdownMode+" held="+window.Held+" exit="+window.LastExitCode+" status="+window.StatusText);
            Assert.NotNull(window.Freeze);
            Assert.False(window.Held, window.StatusText);
            var workspace = Path.Combine(folder, "sensitivity-workspace");
            var freezeDirectory = Path.Combine(workspace, "freeze");
            var planPath = Path.Combine(freezeDirectory, "plan.json");
            var inventoryPath = Path.Combine(freezeDirectory, "inventory.json");
            var planText = File.ReadAllText(planPath);
            Assert.Contains("rec-a", planText, StringComparison.Ordinal);
            Assert.Contains("rec-b", planText, StringComparison.Ordinal);
            Assert.DoesNotContain("rec-missing", planText, StringComparison.Ordinal);
            Assert.DoesNotContain("rec-excluded", planText, StringComparison.Ordinal);
            Assert.DoesNotContain("rec-invalid", planText, StringComparison.Ordinal);
            Assert.DoesNotContain("FREE WT KO A B reason", planText, StringComparison.Ordinal);
            var inventoryHash = window.Freeze!.InventorySha256;
            Assert.Matches("^[0-9a-f]{64}$", inventoryHash);
            Assert.Equal(inventoryHash, ReplayHashes.Sha256File(inventoryPath));
            var saved = SensitivityCatalog.ReadInventory(workspace, window.Freeze);
            Assert.NotNull(saved);
            Assert.Equal(5, saved.Count);
            Assert.Contains(saved, row => row.RecordingId == "rec-missing" && row.Availability == "missing" && row.Filename == "PRIVATE-WT-KO-missing.avi");
            Assert.Contains(saved, row => row.RecordingId == "rec-excluded" && row.Availability == "excluded" && row.Reason == "FREE WT KO A B reason" && row.AnimalId == "B" && row.GroupName == "KO");
            Assert.Contains(saved, row => row.RecordingId == "rec-invalid" && row.Availability == "invalid");
            Assert.Contains(inventoryHash, string.Join("\n", Texts(window)), StringComparison.Ordinal);
            Assert.Contains(Find<Button>(window), button => Equals(button.Content, "rec-missing · history · missing"));
            Assert.Contains(Find<Button>(window), button => Equals(button.Content, "rec-excluded · history · excluded"));
            Assert.Contains(Find<Button>(window), button => Equals(button.Content, "rec-invalid · history · invalid"));
            Assert.Contains(Texts(window), text => text.Contains("FREE WT KO A B reason", StringComparison.Ordinal));
            Click(window, "rec-excluded · history · excluded");
            Pump(window.PendingView, 30);
            var refused = window.LastViewer!;
            Pump(refused.PendingFrame, 30);
            Assert.True(refused.Held, refused.StatusText);
            Assert.Null(refused.DisplayedBitmap);
            Assert.Contains("not in the frozen plan", refused.StatusText, StringComparison.Ordinal);
            Assert.Empty(refused.SourceOverlay.Children.OfType<System.Windows.Shapes.Ellipse>());
            window.Blinded = true;
            Layout(window);
            Assert.Null(window.LastViewer);
            Assert.Equal("", refused.IdentityText);
            Assert.Equal("", refused.StatusText);
            AssertPrivate(window);
            Assert.DoesNotContain(Texts(window), text => text.Contains("FREE WT KO A B reason", StringComparison.Ordinal) || text.Contains(inventoryHash, StringComparison.Ordinal));
            Unblind(window, "Confirm the saved inventory after blinding");
            var frozen = Snapshot(freezeDirectory);
            var planBytes = File.ReadAllBytes(planPath);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE videos SET json = replace(json, 'FREE WT KO A B reason', 'LIVE-EDIT-STATUS') WHERE id = 'rec-excluded'";
                Assert.Equal(1, command.ExecuteNonQuery());
                command.CommandText = "UPDATE videos SET json = replace(json, '\"included\": false', '\"included\": true') WHERE id = 'rec-excluded'";
                Assert.Equal(1, command.ExecuteNonQuery());
                command.CommandText = "DELETE FROM videos WHERE id = 'rec-missing'";
                Assert.Equal(1, command.ExecuteNonQuery());
                command.CommandText = "DELETE FROM runs WHERE video_id = 'rec-invalid'";
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            var live = SensitivityCatalog.ReadRecordings(AtlasSnapshot.Read(store.DatabasePath));
            Assert.DoesNotContain(live, row => row.RecordingId == "rec-missing");
            Assert.Contains(live, row => row.RecordingId == "rec-excluded" && row.Availability == "missing");
            Assert.Contains(live, row => row.RecordingId == "rec-invalid" && row.Availability == "missing");
            AssertSame(frozen, freezeDirectory);
            window.Close();
            window = null;
            window = OpenSensitive(store.DatabasePath);
            Unblind(window, "Read the inventory saved at freeze");
            Assert.False(window.Held, window.StatusText);
            Assert.Equal(inventoryHash, window.Freeze!.InventorySha256);
            Assert.Contains(Find<Button>(window), button => Equals(button.Content, "rec-missing · history · missing"));
            Assert.Contains(Find<Button>(window), button => Equals(button.Content, "rec-excluded · history · excluded"));
            Assert.Contains(Find<Button>(window), button => Equals(button.Content, "rec-invalid · history · invalid"));
            Assert.Contains(Texts(window), text => text.Contains("FREE WT KO A B reason", StringComparison.Ordinal));
            Assert.Contains(Texts(window), text => text.Contains("rec-missing", StringComparison.Ordinal));
            Assert.DoesNotContain(Texts(window), text => text.Contains("LIVE-EDIT-STATUS", StringComparison.Ordinal));
            AssertSame(frozen, freezeDirectory);
            File.WriteAllText(inventoryPath, File.ReadAllText(inventoryPath).Replace("FREE WT KO A B reason", "TAMPERED-STATUS", StringComparison.Ordinal));
            Assert.NotEqual(inventoryHash, ReplayHashes.Sha256File(inventoryPath));
            Assert.Equal(planBytes, File.ReadAllBytes(planPath));
            window.Close();
            window = null;
            window = OpenSensitive(store.DatabasePath);
            Assert.True(window.Held, window.StatusText);
            Assert.DoesNotContain(Texts(window), text => text.Contains("TAMPERED-STATUS", StringComparison.Ordinal) || text.Contains("LIVE-EDIT-STATUS", StringComparison.Ordinal) || text.Contains("rec-missing", StringComparison.Ordinal) || text.Contains("rec-excluded", StringComparison.Ordinal) || text.Contains("FREE WT KO A B reason", StringComparison.Ordinal));
            Unblind(window, "Inspect an unauthenticated inventory");
            Assert.True(window.Held, window.StatusText);
            Assert.Equal("Frozen recording inventory failed authentication. Current study rows were not substituted.", window.StatusText);
            Assert.Null(SensitivityCatalog.ReadInventory(workspace, window.Freeze!));
            Assert.DoesNotContain(Texts(window), text => text.Contains("TAMPERED-STATUS", StringComparison.Ordinal) || text.Contains("LIVE-EDIT-STATUS", StringComparison.Ordinal) || text.Contains("rec-missing", StringComparison.Ordinal));
            var refusal = new HashGate();
            window.Tracker = refusal;
            Click(window, "Run frozen replay");
            Pump(window.PendingRun, 30);
            Assert.Equal(2, window.LastExitCode);
            Assert.False(refusal.Entered.Task.IsCompleted);
            Assert.False(Directory.Exists(Path.Combine(workspace, "replay")));
            Assert.Equal(planBytes, File.ReadAllBytes(planPath));
            Assert.Equal("Frozen recording inventory failed authentication. Current study rows were not substituted.", window.StatusText);
            evidence.Add("PASS saved freeze inventory authenticates every observed recording and still shows held, missing, and excluded rows after the live study changes.");
        }
        finally
        {
            window?.Close();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static SensitivityWindow OpenSensitive(string database)
    {
        Assert.NotNull(System.Windows.Application.Current);
        Assert.Equal(ShutdownMode.OnExplicitShutdown, System.Windows.Application.Current.ShutdownMode);
        var window = new SensitivityWindow(database, true, false);
        Layout(window);
        return window;
    }

    private static void Unblind(SensitivityWindow window, string reason)
    {
        Assert.True(window.TryUnblind("Reviewer", reason));
        Assert.False(window.Blinded);
        Layout(window);
    }

    private static void AssertSettledFreeze(string workspace)
    {
        Assert.False(Directory.Exists(Path.Combine(workspace, "freeze-staging")));
        Assert.False(Directory.Exists(Path.Combine(workspace, "freeze.previous")));
        Assert.False(File.Exists(Path.Combine(workspace, "freeze.was-new")));
    }

    private static void PumpUntil(Func<bool> done, int seconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        timer.Tick += (_, _) =>
        {
            if (done() || DateTime.UtcNow >= deadline) { timer.Stop(); frame.Continue = false; }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.True(done(), "Dispatcher condition expired.");
    }

    private static (VideoAsset Asset, AnalysisRun Run) Import(StudyStore store, string folder, string id, string fileName, int recording, string animal, string group, string alias, bool analyze, TrackingSettings settings)
    {
        var path = Path.Combine(folder, fileName);
        using (var writer = new OpenCvSharp.VideoWriter(path, OpenCvSharp.VideoWriter.FourCC('F', 'F', 'V', '1'), 10, new OpenCvSharp.Size(160, 120)))
        {
            Assert.True(writer.IsOpened(), "Synthetic FFV1 writer did not open.");
            for (var frame = 0; frame < 4; frame++)
            {
                using var image = new OpenCvSharp.Mat(120, 160, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(30 + frame + recording * 20, 70, 180));
                OpenCvSharp.Cv2.Circle(image, new OpenCvSharp.Point(40 + frame * 8, 60), 8, new OpenCvSharp.Scalar(5, 5, 5), -1);
                writer.Write(image);
            }
        }
        var hash = OpenCvTracker.HashFile(path);
        var metadata = OpenCvTracker.Probe(path);
        Assert.Equal(4, metadata.FrameCount);
        var asset = new VideoAsset
        {
            Id = id,
            Path = path,
            SourceSha256 = hash,
            Metadata = metadata,
            Settings = settings,
            AnimalId = animal,
            AnimalIdConfirmed = true,
            GroupName = group,
            BlindedAlias = alias,
            Phase = TrialMode.Training,
            Included = true
        };
        store.SaveVideo(asset);
        if (!analyze) return (asset, new AnalysisRun());
        var run = OpenCvTracker.Analyze(new VideoAsset { Id = id, Path = path, SourceSha256 = hash, Metadata = metadata, Settings = settings });
        store.SaveRun(run);
        return (asset, run);
    }

    private static TrackingSettings DetectorSettings() => new()
    {
        Cx = .5, Cy = .5, Rx = .4, Ry = .4, Threshold = 40, Invert = true, Blur = 1, MinArea = 20, MaxArea = 5000,
        MaxExtension = 80, MaxJump = 40, ArenaConfirmed = true, WallBoundary = .8
    };

    private static void AssertPixels(BitmapSource source, int recording, int frame)
    {
        var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(2, 2, 1, 1), pixel, 4, 0);
        Assert.InRange(Math.Abs(pixel[0] - (30 + frame + recording * 20)), 0, 2);
        Assert.InRange(Math.Abs(pixel[1] - 70), 0, 2);
        Assert.InRange(Math.Abs(pixel[2] - 180), 0, 2);
    }

    private static void AssertPrivate(Window window)
    {
        foreach (var text in Texts(window))
        {
            Assert.DoesNotMatch(new Regex(@"\b(WT|KO)\b", RegexOptions.CultureInvariant), text);
            Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Informative", text, StringComparison.Ordinal);
            Assert.DoesNotContain("FREE", text, StringComparison.Ordinal);
            Assert.NotEqual("A", text.Trim());
            Assert.NotEqual("B", text.Trim());
            Assert.DoesNotContain("Animal A", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Animal B", text, StringComparison.Ordinal);
        }
        foreach (var control in Find<FrameworkElement>(window))
            if (control.ToolTip is string tip) AssertPrivateText(tip);
    }

    private static void AssertPrivateText(string text)
    {
        Assert.DoesNotMatch(new Regex(@"\b(WT|KO)\b", RegexOptions.CultureInvariant), text);
        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Texts(Window window) => Find<TextBlock>(window).Select(block => block.Text).Concat(Find<Button>(window).Select(button => button.Content as string ?? ""));

    private static IEnumerable<System.Windows.Shapes.Ellipse> Markers(SensitivitySourceViewer viewer, string tag) => viewer.SourceOverlay.Children.OfType<System.Windows.Shapes.Ellipse>().Where(item => Equals(item.Tag, tag));

    private static void Click(Window window, string content)
    {
        var button = Find<Button>(window).Single(item => Equals(item.Content, content));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static IEnumerable<T> Find<T>(Window window) where T : DependencyObject => Descendants((DependencyObject)window.Content).OfType<T>();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Layout(Window window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new System.Windows.Size(1050, 700));
        root.Arrange(new System.Windows.Rect(0, 0, 1050, 700));
        root.UpdateLayout();
        Pump(Task.Delay(30), 5);
        root.UpdateLayout();
    }

    private static void Pump(Task task, int seconds)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.True(task.IsCompleted, "Dispatcher wait expired.");
        task.GetAwaiter().GetResult();
    }

    private static AnalysisRun LoadRun(string folder, string recording, string scenario) =>
        JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(Path.Combine(Attempt(folder, recording, scenario, 1), "analysis.json")));

    private static string Attempt(string folder, string recording, string scenario, int index) =>
        Path.Combine(folder, "sensitivity-workspace", "replay", "cases", recording, scenario, index.ToString("attempt-0000", CultureInfo.InvariantCulture));

    private static string ReadRunJson(string database, string videoId)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM runs WHERE video_id=$id ORDER BY rowid DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", videoId);
        return (string)command.ExecuteScalar()!;
    }

    private static Dictionary<string, byte[]> Snapshot(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertSame(Dictionary<string, byte[]> before, string directory)
    {
        var after = Snapshot(directory);
        Assert.Equal(before.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase), after.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
    }

    private sealed class GateTracker : IReplayTracker
    {
        private readonly NativeReplayTracker _inner = new();
        private int _hashes;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string NativeVersion => _inner.NativeVersion;
        public string HashFile(string path)
        {
            if (Interlocked.Increment(ref _hashes) == 1)
            {
                Started.TrySetResult();
                if (!Release.Task.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The hash gate was not released.");
            }
            return _inner.HashFile(path);
        }
        public VideoMetadata Probe(string path) => _inner.Probe(path);
        public AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation)
        {
            if (asset.Settings.Threshold == 1) throw new InvalidDataException("Synthetic detector failure retained for this scenario.");
            return _inner.Analyze(asset, cancellation);
        }
    }

    private sealed class HashGate : IReplayTracker
    {
        private readonly NativeReplayTracker _inner = new();
        private int _hashes;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string NativeVersion => _inner.NativeVersion;
        public string HashFile(string path)
        {
            if (Interlocked.Increment(ref _hashes) == 1)
            {
                Entered.TrySetResult();
                if (!Release.Task.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Freeze hash was not released.");
            }
            return _inner.HashFile(path);
        }
        public VideoMetadata Probe(string path) => _inner.Probe(path);
        public AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation) => _inner.Analyze(asset, cancellation);
    }

    private sealed class ProbeGate : IReplayTracker
    {
        private readonly NativeReplayTracker _inner = new();
        private int _probes;
        public int DelayOnProbe { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string NativeVersion => _inner.NativeVersion;
        public string HashFile(string path) => _inner.HashFile(path);
        public VideoMetadata Probe(string path)
        {
            var metadata = _inner.Probe(path);
            if (Interlocked.Increment(ref _probes) == DelayOnProbe)
            {
                Entered.TrySetResult();
                if (!Release.Task.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Freeze preparation was not released.");
            }
            return metadata;
        }
        public AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation) => _inner.Analyze(asset, cancellation);
    }

    private sealed class OpenGate : IReplayTracker
    {
        private readonly NativeReplayTracker _inner = new();
        private int _hashes;
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string NativeVersion => _inner.NativeVersion;
        public string HashFile(string path)
        {
            var call = Interlocked.Increment(ref _hashes);
            if (call == 1)
            {
                FirstEntered.TrySetResult();
                if (!ReleaseFirst.Task.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("First source open was not released.");
            }
            else if (call == 2)
            {
                SecondEntered.TrySetResult();
                if (!ReleaseSecond.Task.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Second source open was not released.");
            }
            var hash = _inner.HashFile(path);
            if (call == 1) FirstReturned.TrySetResult();
            return hash;
        }
        public VideoMetadata Probe(string path) => _inner.Probe(path);
        public AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation) => _inner.Analyze(asset, cancellation);
    }
}
