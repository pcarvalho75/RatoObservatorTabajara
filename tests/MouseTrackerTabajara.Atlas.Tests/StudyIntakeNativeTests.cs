using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Atlas.Tests;

public class StudyIntakeNativeTests(ITestOutputHelper output)
{
    private const string ChildVariable = "TABAJARA_INTAKE_NATIVE";
    private const string AuthoredSentinel = "AUTHORED-SENTINEL-7Q";

    /// <summary>WPF allows one Application per process, so the native proof runs in an isolated child test host.</summary>
    [Fact] public void MainWindowIntakeIsBlindedReviewedCancelableAndAtomic()
    {
        if (Environment.GetEnvironmentVariable(ChildVariable) != "1") { RunIsolated(); return; }
        Exception? error = null; var evidence = new List<string>();
        var thread = new Thread(() => { try { Proof(evidence); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "Bounded native intake proof completed.");
        foreach (var line in evidence) output.WriteLine(line);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private void RunIsolated()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path && File.Exists(path) ? path : "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "test", typeof(StudyIntakeNativeTests).Assembly.Location, "--nologo", "--filter", "FullyQualifiedName=" + typeof(StudyIntakeNativeTests).FullName + "." + nameof(MainWindowIntakeIsBlindedReviewedCancelableAndAtomic),
            "--logger", "console;verbosity=detailed" }) start.ArgumentList.Add(argument);
        start.Environment[ChildVariable] = "1";
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            Assert.True(process.WaitForExit(240000), "Isolated native intake proof finished within 240 seconds.");
            var text = stdout.GetAwaiter().GetResult(); output.WriteLine(text); output.WriteLine(stderr.GetAwaiter().GetResult());
            Assert.Equal(0, process.ExitCode);
            Assert.Matches(new Regex(@"Total tests:\s*1\b[\s\S]*Passed:\s*1\b|Failed:\s+0, Passed:\s+1\b"), text);
            Assert.DoesNotMatch(new Regex(@"Failed:\s*[1-9]"), text);
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); } }
    }

    private static void Proof(List<string> evidence)
    {
        using var s = new IntakeScenario();
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var root = App.FindRepositoryFile("MouseTrackerTabajara.sln") is { } solution ? Path.GetDirectoryName(solution)! : Path.GetTempPath();
        var artifacts = Path.Combine(root, "artifacts", "study-intake", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(artifacts);
        evidence.Add("evidence directory=" + artifacts);
        var tables = new[] { "runs", "corrections", "reviews", "inclusion_decisions", "geometry_revisions", "biological_events", "protocol_windows" };
        var beforeTables = tables.ToDictionary(t => t, s.Dump); var beforeVideos = s.Store.Videos().ToDictionary(v => v.Id, v => JsonDefaults.Serialize(v));
        var sourceHashes = s.AllVideoFiles().ToDictionary(p => p, IntakeScenario.Sha);
        MainWindow? main = null;
        try
        {
            var model = new MainViewModel(s.Workspace) { Blind = true }; main = new MainWindow(model); app.MainWindow = main; main.Show(); Pump(main.InitializeAsync());
            var statuses = new List<string>(); model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Status)) statuses.Add(model.Status); };
            var button = (Button)main.FindName("StudyIntakeButton"); Assert.Equal("Bulk intake", button.Content);
            var mainRoot = Layout(main, 1050, 700); Render(mainRoot, Path.Combine(artifacts, "main-1050x700.png"));
            var viewer = (FrameworkElement)main.FindName("ViewerViewport"); var projectRow = (FrameworkElement)VisualTreeHelper.GetParent(button);
            var buttonBounds = Bounds(button, mainRoot); var withButton = (Viewer: viewer.ActualHeight, Row: projectRow.ActualHeight);
            button.Visibility = Visibility.Collapsed; Layout(main, 1050, 700); var without = (Viewer: viewer.ActualHeight, Row: projectRow.ActualHeight);
            button.Visibility = Visibility.Visible; Layout(main, 1050, 700);
            evidence.Add($"main window 1050x700 client {mainRoot.ActualWidth}x{mainRoot.ActualHeight}: intake button {buttonBounds}; project row/viewer height with button {withButton}, without {without}");
            Assert.True(buttonBounds.Left >= 0 && buttonBounds.Right <= mainRoot.ActualWidth && buttonBounds.Bottom <= mainRoot.ActualHeight && buttonBounds.Width > 0, "Intake action is visible at 1050x700.");
            Assert.Equal(without, withButton); // the new action costs no workspace height at the minimum size
            foreach (var name in new[] { "NewProjectButton", "OpenProjectButton", "SaveProjectButton", "ImportButton" })
            { var other = Bounds((FrameworkElement)main.FindName(name), mainRoot); Assert.False(other.IntersectsWith(buttonBounds), name + " stays separate from the intake action."); }
            var footer = (FrameworkElement)VisualTreeHelper.GetParent(Find<TextBlock>(main).Single(t => t.Text == "Local processing")); var footerTop = Bounds(footer, mainRoot).Top;
            evidence.Add("pre-existing sidebar clipping at 1050x700 (unchanged by intake): " + string.Join("; ", Find<Button>((DependencyObject)main.FindName("BrandSidebar"))
                .Select(b => (Label: b.Content as string ?? (b.Content as TextBlock)?.Text ?? "", Bottom: Bounds(b, mainRoot).Bottom)).Select(b => $"{b.Label} {(b.Bottom <= footerTop ? "visible" : "clipped")}")));

            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var window = main.IntakeWindow ?? throw new InvalidOperationException("Main window did not open the intake window.");
            Assert.Same(main, window.Owner); Assert.True(window.Blinded); Assert.Equal((1050d, 700d), (window.MinWidth, window.MinHeight));
            // Blind: the path and its picker are hidden (not cleared); choosing a manifest needs an audited reveal.
            Assert.False(window.ManifestPathInput.IsVisible || window.ChooseButton.IsVisible || window.ReviewerInput.IsVisible || window.ReasonInput.IsVisible);
            Assert.True(window.ManifestHiddenNotice.IsVisible && window.AuthoredHiddenNotice.IsVisible); Assert.Contains("Reveal metadata", window.StatusText);
            Assert.True(window.TryUnblind("Synthetic reviewer", "Choose the synthetic manifest")); Assert.True(window.ManifestPathInput.IsVisible && window.ChooseButton.IsVisible);
            window.ManifestPathInput.Text = s.Manifest; window.ReviewerInput.Text = AuthoredSentinel + " reviewer"; window.ReasonInput.Text = AuthoredSentinel + " reason M-COMPLETE";
            window.Blinded = true;
            Assert.False(window.ManifestPathInput.IsVisible || window.ChooseButton.IsVisible || window.ReviewerInput.IsVisible || window.ReasonInput.IsVisible);
            Assert.Equal(s.Manifest, window.ManifestPathInput.Text); // the internal selection is preserved
            Click(window.PreviewButton); Pump(window.PendingOperation);
            var preview = window.Preview ?? throw new InvalidOperationException(window.StatusText);
            Assert.False(preview.Blocked, StudyIntakeTests.Describe(preview));
            foreach (var expected in new[] { "Manifest rows 33", "animals 4 (3 new to the plan)", "video files 33 (3 already imported, 30 to register)", "expected slots 84 (4 × 21)", "mapped after apply 33", "explicitly missing 51", "blocking conflicts 0", "plan revision 1 → 2", "Group counts hidden while blind" })
                Assert.Contains(expected, window.SummaryText);
            Assert.Equal((51, 4, 33), (window.MissingGrid.Items.Count, window.AnimalGrid.Items.Count, window.RowGrid.Items.Count));
            var blindText = AllText(window, 1050, 700);
            AssertNoPrivateText(s, blindText);
            Assert.Contains("Animal 001", blindText); Assert.Contains("hidden while blind", blindText);
            Render(Layout(window, 1050, 700), Path.Combine(artifacts, "intake-blinded-preview-1050x700.png"));
            Assert.False(window.ApplyButton.IsEnabled); Click(window.ApplyButton); Pump(window.PendingOperation);
            Assert.Contains("Reveal metadata", window.StatusText); Assert.Null(window.LastReceipt); Assert.Equal(1, new StudyPlanStore(s.Store).Latest()!.Revision);
            // A failing preview while blind does not echo the (private) path from the exception.
            window.ManifestPathInput.Text = Path.Combine(s.Folder, "M-COMPLETE missing manifest.csv"); Click(window.PreviewButton); Pump(window.PendingOperation);
            Assert.Null(window.Preview); AssertNoPrivateText(s, window.StatusText); Assert.Contains("Preview failed", window.StatusText);
            window.ManifestPathInput.Text = s.Manifest; Click(window.PreviewButton); Pump(window.PendingOperation); Assert.NotNull(window.Preview);
            Assert.False(window.TryUnblind("", "")); Assert.True(window.Blinded);
            Assert.True(window.TryUnblind("Synthetic reviewer", "Review synthetic intake identities")); Assert.False(window.Blinded);
            Assert.True(window.ReviewerInput.IsVisible); Assert.Equal(AuthoredSentinel + " reviewer", window.ReviewerInput.Text); // hidden, not lost
            Assert.Contains("study_intake_unblinded", s.Dump("audit"));
            Assert.Contains(window.AnimalGrid.Items.Cast<StudyIntakeWindow.AnimalRow>(), r => r.Animal == "M-COMPLETE" && r.Group == "treated wildtype" && r.Mapped == "21/21");
            Assert.Contains(window.AnimalGrid.Items.Cast<StudyIntakeWindow.AnimalRow>(), r => r.Animal == "M-PARTIAL" && r.Mapped == "7/21" && r.Updates == "group vehicle wildtype, litter L1");
            Assert.Contains("treated wildtype 1; vehicle wildtype 1; treated mutant 1; vehicle mutant 1", window.SummaryText);
            Render(Layout(window, 1050, 700), Path.Combine(artifacts, "intake-revealed-preview-1050x700.png"));
            var intakeRoot = (FrameworkElement)window.Content; var apply = Bounds(window.ApplyButton, intakeRoot); var tabs = Bounds(Find<TabControl>(window).Single(), intakeRoot);
            evidence.Add($"intake window 1050x700 client {intakeRoot.ActualWidth}x{intakeRoot.ActualHeight}: apply button {apply}; tabs {tabs}");
            Assert.True(apply.Left >= 0 && apply.Top >= 0 && apply.Right <= intakeRoot.ActualWidth && apply.Bottom <= intakeRoot.ActualHeight, "Apply action fits at 1050x700.");
            Assert.True(tabs.Height >= 180 && tabs.Right <= intakeRoot.ActualWidth && tabs.Bottom <= apply.Top, "Preview tabs keep usable height above the apply bar at 1050x700.");

            window.ReviewerInput.Text = "Synthetic reviewer"; window.ReasonInput.Text = "Native synthetic bulk intake";
            Assert.False(window.ApplyButton.IsEnabled); Pump(window.ApplyAsync());
            Assert.Equal(StudyIntakeOutcome.Refused, window.LastReceipt!.Outcome); Assert.NotNull(window.Preview);
            window.ReviewedInput.IsChecked = true; Assert.True(window.ApplyButton.IsEnabled);

            // Owner gate: while the main window is busy (as during batch processing) apply is refused before anything is written.
            model.Busy = true; Click(window.ApplyButton); Pump(window.PendingOperation);
            Assert.Contains("main window is processing", window.StatusText); Assert.Equal(StudyIntakeOutcome.Refused, window.LastReceipt.Outcome);
            Assert.True(model.Busy); Assert.False(main.IntakeHoldsBusy); Assert.Equal(1, new StudyPlanStore(s.Store).Latest()!.Revision); model.Busy = false;

            // Re-blind during apply, then fail with an exception quoting private paths/IDs: the late status stays blind-safe.
            // While apply holds the owner gate, main processing, import, metadata edits, project save and a second intake are gated.
            var snapshot = Path.Combine(s.Folder, "during-intake.tabajara"); Task? save = null; var gated = "";
            window.ApplyCheckpoint = (stage, i, _) =>
            {
                if (stage != StudyIntakeCheckpoint.BeforeImport || i != 0) return Task.CompletedTask;
                window.Dispatcher.Invoke(() =>
                {
                    window.Blinded = true;
                    var analyzeAll = Find<Button>(main).Single(b => Equals(b.Content, "Analyze all"));
                    gated = $"busy={model.Busy} held={main.IntakeHoldsBusy} import={((Button)main.FindName("ImportButton")).IsEnabled} analyzeAll={analyzeAll.IsEnabled} " +
                        $"intake={button.IsEnabled} gridReadOnly={((DataGrid)main.FindName("TrialGrid")).IsReadOnly} canChangeProject={main.CanChangeProject}";
                    Click(analyzeAll); // a processing request through the real handler returns without starting a batch
                    save = main.SaveProcessedProjectAsync(snapshot);
                });
                return Task.FromException(new IOException($"Synthetic failure reading {s.Paths[("M-COMPLETE", 3, 2)]} for M-COMPLETE ({AuthoredSentinel})"));
            };
            Click(window.ApplyButton); Pump(window.PendingOperation);
            evidence.Add("owner gates during apply: " + gated);
            Assert.Equal("busy=True held=True import=False analyzeAll=False intake=False gridReadOnly=True canChangeProject=False", gated);
            Assert.True(save!.IsFaulted && save.Exception!.InnerException is InvalidOperationException); Assert.False(File.Exists(snapshot));
            Assert.Equal(StudyIntakeOutcome.Failed, window.LastReceipt.Outcome); Assert.Empty(window.LastReceipt.RegisteredRecordingIds);
            Assert.Contains("Synthetic failure reading", window.LastReceipt.Message); // the receipt keeps the detail …
            Assert.True(window.Blinded); AssertNoPrivateText(s, window.StatusText); Assert.Contains("Intake outcome: Failed", window.StatusText); // … the blind display does not
            AssertNoPrivateText(s, AllText(window, 1050, 700));
            Assert.False(model.Busy || main.IntakeHoldsBusy); Assert.Equal(1, new StudyPlanStore(s.Store).Latest()!.Revision);
            Assert.True(window.TryUnblind("Synthetic reviewer", "Continue synthetic intake"));

            // Cancel from the native Cancel button while sources are being registered.
            Click(window.PreviewButton); Pump(window.PendingOperation); window.ReviewedInput.IsChecked = true;
            window.ApplyCheckpoint = (stage, i, _) =>
            {
                if (stage == StudyIntakeCheckpoint.AfterImport && i == 1) window.Dispatcher.Invoke(() => { Assert.True(window.CancelButton.IsEnabled); Click(window.CancelButton); });
                return Task.CompletedTask;
            };
            Click(window.ApplyButton); Pump(window.PendingOperation);
            Assert.Equal(StudyIntakeOutcome.Cancelled, window.LastReceipt.Outcome); Assert.Equal(2, window.LastReceipt.RegisteredRecordingIds.Length);
            Assert.Null(window.Preview); Assert.Equal(1, new StudyPlanStore(s.Store).Latest()!.Revision); Assert.False(model.Busy || main.IntakeHoldsBusy);

            // Child close at AfterImport: the close is held, apply cancels at its next safe point, the receipt is recorded, then the window closes.
            Click(window.PreviewButton); Pump(window.PendingOperation); Assert.Equal(28, window.Preview!.PendingImports); window.ReviewedInput.IsChecked = true;
            var childClosed = Closed(window); var heldOpen = "";
            window.ApplyCheckpoint = (stage, i, _) =>
            {
                if (stage == StudyIntakeCheckpoint.AfterImport && i == 1)
                    window.Dispatcher.Invoke(() => { window.Close(); heldOpen = $"visible={window.IsVisible} closeRequested={window.CloseRequested} busy={window.Busy}"; });
                return Task.CompletedTask;
            };
            Click(window.ApplyButton); Pump(childClosed);
            evidence.Add("child close at AfterImport: " + heldOpen);
            Assert.Equal("visible=True closeRequested=True busy=True", heldOpen);
            Assert.Equal(StudyIntakeOutcome.Cancelled, window.LastReceipt.Outcome); Assert.Equal(2, window.LastReceipt.RegisteredRecordingIds.Length);
            Assert.Equal(StudyIntakeOutcome.Cancelled, StudyIntakeReconciler.Receipts(s.Store)[^1].Receipt.Outcome);
            Assert.Null(main.IntakeWindow); Assert.True(main.IsVisible); Assert.False(model.Busy || main.IntakeHoldsBusy); Assert.Equal(1, new StudyPlanStore(s.Store).Latest()!.Revision);

            // Owner close inside the plan transaction: both windows stay open until the committed revision's receipt is recorded, then both close.
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window = main.IntakeWindow ?? throw new InvalidOperationException("Intake did not reopen.");
            Assert.True(window.TryUnblind("Synthetic reviewer", "Apply synthetic intake")); window.ManifestPathInput.Text = s.Manifest;
            Click(window.PreviewButton); Pump(window.PendingOperation); Assert.Equal((26, 7), (window.Preview!.PendingImports, window.Preview.ExistingRecordings));
            window.ReviewerInput.Text = "Synthetic reviewer"; window.ReasonInput.Text = "Native synthetic bulk intake"; window.ReviewedInput.IsChecked = true;
            var ownerClosed = Closed(main); var intakeClosed = Closed(window); var intake = window; heldOpen = "";
            window.ApplyCheckpoint = (stage, _, _) =>
            {
                if (stage == StudyIntakeCheckpoint.InPlanTransaction)
                    main.Dispatcher.Invoke(() => { main.Close(); heldOpen = $"owner={main.IsVisible} intake={intake.IsVisible} closeRequested={intake.CloseRequested}"; });
                return Task.CompletedTask;
            };
            Click(window.ApplyButton); Pump(ownerClosed); Pump(intakeClosed);
            evidence.Add("owner close inside plan transaction: " + heldOpen);
            Assert.Equal("owner=True intake=True closeRequested=True", heldOpen);
            Assert.True(window.LastReceipt!.Outcome == StudyIntakeOutcome.Applied, window.LastReceipt.Message); Assert.Equal(2, window.LastReceipt.PlanRevisionAfter);
            Assert.Equal(26, window.LastReceipt.RegisteredRecordingIds.Length);
            Assert.Equal(StudyIntakeOutcome.Applied, StudyIntakeReconciler.Receipts(s.Store)[^1].Receipt.Outcome);
            Assert.False(main.IsVisible || window.IsVisible);
            var plan = new StudyPlanStore(s.Store).Latest()!.Plan;
            Assert.Equal(84, plan.Trials.Count(t => t.AnimalId != "OTHER-1")); Assert.Equal(33, plan.Trials.Count(t => t.AnimalId != "OTHER-1" && t.RecordingId is not null));
            Assert.False(plan.RosterConfirmed); Assert.All(plan.Animals.Where(a => a.Id != "OTHER-1"), a => Assert.False(a.IdentityConfirmed || a.AllocationConfirmed));
            Assert.Equal(s.Store.Videos().Count, model.Rows.Count); Assert.Equal(34, model.Rows.Count);
            Assert.Contains(statuses, t => t.Contains("Closing after bulk intake records its outcome", StringComparison.Ordinal));
            Assert.Contains(statuses, t => t.Contains("Bulk intake saved plan revision 2", StringComparison.Ordinal));

            // The study reopens in a fresh main window: repeat preview is idempotent and apply stays disabled.
            model = new MainViewModel(s.Workspace) { Blind = true }; main = new MainWindow(model); app.MainWindow = main; main.Show(); Pump(main.InitializeAsync());
            ((Button)main.FindName("StudyIntakeButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window = main.IntakeWindow ?? throw new InvalidOperationException("Intake did not open in the reopened window.");
            Assert.True(window.TryUnblind("Synthetic reviewer", "Repeat synthetic preview")); window.ManifestPathInput.Text = s.Manifest;
            Click(window.PreviewButton); Pump(window.PendingOperation);
            Assert.False(window.Preview!.HasChanges); Assert.Contains("nothing to apply", window.StatusText); window.ReviewedInput.IsChecked = true; Assert.False(window.ApplyButton.IsEnabled);
            Assert.Equal(new[] { StudyIntakeOutcome.Applied, StudyIntakeOutcome.Cancelled, StudyIntakeOutcome.Cancelled, StudyIntakeOutcome.Failed, StudyIntakeOutcome.Refused }.Select(o => o.ToString()),
                window.ReceiptGrid.Items.Cast<StudyIntakeWindow.ReceiptRow>().Select(r => r.Outcome));
            window.ReviewerInput.Text = AuthoredSentinel; window.ReasonInput.Text = AuthoredSentinel + " M-PARTIAL";
            window.Blinded = true; AssertNoPrivateText(s, AllText(window, 1050, 700));
            Render(Layout(window, 1050, 700), Path.Combine(artifacts, "intake-blinded-after-apply-1050x700.png"));

            foreach (var table in tables) Assert.Equal(beforeTables[table], s.Dump(table));
            var after = s.Store.Videos().ToDictionary(v => v.Id); foreach (var (id, json) in beforeVideos) Assert.Equal(json, JsonDefaults.Serialize(after[id]));
            foreach (var (path, hash) in sourceHashes) Assert.Equal(hash, IntakeScenario.Sha(path));
            var receipts = StudyIntakeReconciler.Receipts(s.Store);
            File.WriteAllText(Path.Combine(artifacts, "receipts.json"), JsonDefaults.Serialize(receipts.Select(r => new { r.AuditId, r.Action, r.Receipt })));
            File.WriteAllText(Path.Combine(artifacts, "applied-plan-summary.txt"),
                $"plan revisions={new StudyPlanStore(s.Store).History().Length}\nanimals={plan.Animals.Length}\ntrials={plan.Trials.Length}\nmapped={plan.Trials.Count(t => t.RecordingId is not null)}\n" +
                $"recordings={s.Store.Videos().Count}\nroster_confirmed={plan.RosterConfirmed}\ndesign_confirmed={plan.DesignConfirmed}\n");
            evidence.Add($"receipts={string.Join(",", receipts.Select(r => r.Action + "#" + r.AuditId))}; plan revision 2 trials={plan.Trials.Length}; recordings={s.Store.Videos().Count}");
        }
        finally { main?.IntakeWindow?.Close(); main?.Close(); app.Shutdown(); }
    }

    private static void AssertNoPrivateText(IntakeScenario s, string text)
    {
        foreach (var secret in IntakeScenario.PrivateText.Concat([AuthoredSentinel, s.Folder, s.Manifest, Path.GetFileName(s.Manifest)]).Concat(s.Paths.Values))
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }
    private static Task Closed(Window window) { var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult(); return closed.Task; }

    /// <summary>Every rendered text of the window across all tabs (visible text blocks and every visible text box, including the manifest path when shown).</summary>
    private static string AllText(StudyIntakeWindow window, double width, double height)
    {
        var parts = new List<string> { window.SummaryText, window.StatusText };
        var tabs = Find<TabControl>(window).Single();
        for (var i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i; Layout(window, width, height);
            parts.AddRange(Find<TextBlock>(window).Where(t => t.IsVisible).Select(t => t.Text)); parts.AddRange(Find<TextBox>(window).Where(t => t.IsVisible).Select(t => t.Text));
        }
        tabs.SelectedIndex = 0; Layout(window, width, height);
        foreach (var grid in new ItemsControl[] { window.ConflictGrid, window.MissingGrid, window.AnimalGrid, window.RowGrid, window.ReceiptGrid, window.ChangeList })
            parts.AddRange(grid.Items.Cast<object>().Select(item => item.ToString() ?? ""));
        return string.Join("\n", parts);
    }
    private static Rect Bounds(FrameworkElement element, FrameworkElement root) => element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    /// <summary>Sizes the actual shown window; the content then occupies the real client area (smaller than the outer size).</summary>
    private static FrameworkElement Layout(Window window, double width, double height)
    {
        window.Width = width; window.Height = height; window.UpdateLayout(); Pump(Task.Delay(40));
        var root = (FrameworkElement)window.Content; root.UpdateLayout(); return root;
    }
    /// <summary>Renders the window's template root (the full client area) so content margins are not cropped.</summary>
    private static void Render(FrameworkElement content, string path)
    {
        DependencyObject node = content; while (VisualTreeHelper.GetParent(node) is { } parent and not Window) node = parent;
        var root = (FrameworkElement)node; var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false)); timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Assert.True(task.IsCompleted, "Native dispatcher operation completed within 60 seconds."); task.GetAwaiter().GetResult();
    }
}
