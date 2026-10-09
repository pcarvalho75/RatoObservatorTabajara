using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Desktop;

/// <summary>Native bulk intake: CSV manifest preview, reconciliation against the saved plan/recordings, and explicitly reviewed apply.</summary>
public sealed class StudyIntakeWindow : Window
{
    public static StudyIntakeProtocol LucianaProtocol { get; } = new("Luciana six-day protocol", LucianaProtocolPreset.Groups,
        Enumerable.Range(1, 6).Select(day => new StudyIntakeStage(day, LucianaProtocolPreset.StageIds[day - 1], day == 6 ? TrialMode.Probe : TrialMode.Training, day == 6 ? 1 : 4)).ToImmutableArray());
    public const string Guidance = "Columns: animal_id, group, day, trial, video_path (optional alias, cage, litter). UTF-8 CSV; quote values containing commas. " +
        "One video is one trial; each actual animal has 21 expected slots (four per day on days 1–5, one day 6 probe). Identity is never inferred from file names, " +
        "mapped trials are never overwritten, and intake confirms nothing: identity, allocation, roster, design, geometry, windows and inspection stay for local review. Original videos are only read.";

    private readonly StudyStore _store;
    private readonly StudyIntakeProtocol _protocol;
    private readonly TextBox _manifest = new() { MinWidth = 420, Tag = "intake-manifest-path", ToolTip = "Manifest CSV path" };
    private readonly CheckBox _blind = new() { Content = "Blind review", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
    private readonly TextBox _unblindReviewer = new() { Width = 120, ToolTip = "Unblinding reviewer" };
    private readonly TextBox _unblindReason = new() { Width = 200, ToolTip = "Reason for revealing intake metadata" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 4), Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x4B, 0x12)) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 6), FontSize = 13.5 };
    private readonly DataGrid _conflicts = Table(("Severity", "Severity", 90), ("Lines", "Lines", 110), ("Code", "Code", 170), ("What to do", "Message", 0));
    private readonly DataGrid _missing = Table(("Animal", "Animal", 200), ("Day", "Day", 50), ("Trial", "Trial", 50), ("Slot status", "Action", 200), ("Slot", "Slot", 0));
    private readonly DataGrid _animals = Table(("#", "Index", 45), ("Animal", "Animal", 200), ("Group", "Group", 170), ("Plan entry", "Status", 110), ("Rows", "Rows", 55), ("Mapped after apply", "Mapped", 130), ("Blank fields filled", "Updates", 0));
    private readonly DataGrid _rows = Table(("Line", "Line", 55), ("Animal", "Animal", 180), ("Day", "Day", 50), ("Trial", "Trial", 50), ("Slot action", "Action", 150), ("Recording", "Recording", 0));
    private readonly ListBox _changes = new() { MinHeight = 120 };
    private readonly DataGrid _receipts = Table(("Audit #", "Id", 70), ("Outcome", "Outcome", 100), ("Plan revision", "Revision", 110), ("Created (UTC)", "Created", 170), ("Receipt", "Message", 0));
    private readonly TextBox _reviewer = new() { Margin = new Thickness(0, 4, 4, 4), ToolTip = "Reviewer" };
    private readonly TextBox _reason = new() { Margin = new Thickness(0, 4, 4, 4), ToolTip = "Reason for this plan revision" };
    private readonly CheckBox _reviewed = new() { Content = "I reviewed every conflict, missing slot and planned change", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBlock _manifestHidden = new() { Text = "Manifest path hidden while blind", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), FontStyle = FontStyles.Italic };
    private readonly TextBlock _authoredHidden = new() { Text = "Reviewer and reason hidden while blind; reveal to review and apply.", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), FontStyle = FontStyles.Italic };
    private readonly TextBlock _reviewerLabel = Label("Reviewer"), _reasonLabel = Label("Reason");
    private readonly Button _preview, _apply, _cancel, _choose;
    private CancellationTokenSource? _operation;
    private bool _auditReveal, _ready, _ownerHeld;
    // Status is kept with a blind-safe variant and rendered for the current blind state, so late progress, failures or receipts never show identifying text while blind.
    private string _statusFull = "", _statusBlind = "";

    public StudyIntakePreview? Preview { get; private set; }
    public StudyIntakeReceipt? LastReceipt { get; private set; }
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    public bool Busy { get; private set; }
    /// <summary>A close arrived during preview/apply; the window closes as soon as that operation records its outcome.</summary>
    public bool CloseRequested { get; private set; }
    /// <summary>Owner coordination for apply: returns null when the owner granted exclusive use (its processing/metadata gates closed), otherwise a refusal.</summary>
    public Func<string?>? AcquireOwner { get; set; }
    /// <summary>Releases the owner gate after apply reached an audited outcome.</summary>
    public Action? ReleaseOwner { get; set; }
    public TextBlock ManifestHiddenNotice => _manifestHidden; public TextBlock AuthoredHiddenNotice => _authoredHidden; public Button ChooseButton => _choose;
    public bool Blinded { get => _blind.IsChecked == true; set => _blind.IsChecked = value; }
    public string StatusText => _status.Text;
    public string SummaryText => _summary.Text;
    public TextBox ManifestPathInput => _manifest; public TextBox ReviewerInput => _reviewer; public TextBox ReasonInput => _reason; public CheckBox ReviewedInput => _reviewed;
    public Button PreviewButton => _preview; public Button ApplyButton => _apply; public Button CancelButton => _cancel;
    public DataGrid ConflictGrid => _conflicts; public DataGrid MissingGrid => _missing; public DataGrid AnimalGrid => _animals; public DataGrid RowGrid => _rows; public DataGrid ReceiptGrid => _receipts;
    public ListBox ChangeList => _changes;
    /// <summary>Optional checkpoint during apply (diagnostics and failure-injection proofs). It cannot bypass any guard.</summary>
    public Func<StudyIntakeCheckpoint, int, CancellationToken, Task>? ApplyCheckpoint { get; set; }
    public event EventHandler<StudyIntakeReceipt>? IntakeApplied;

    public StudyIntakeWindow(string database, bool blinded, StudyIntakeProtocol? protocol = null)
    {
        _store = new StudyStore(database); _protocol = protocol ?? LucianaProtocol; _protocol.Validate();
        Title = "Mouse Tracker Tabajara — Bulk study intake"; Width = 1240; Height = 860; MinWidth = 1050; MinHeight = 700;
        _choose = Button("Choose manifest…", (_, _) => ChooseManifest());
        _preview = Button("Preview manifest", (_, _) => PendingOperation = PreviewAsync());
        _cancel = Button("Cancel", (_, _) => _operation?.Cancel());
        _apply = Button("Apply reviewed preview", (_, _) => PendingOperation = ApplyAsync());
        _apply.FontWeight = FontWeights.SemiBold;
        var root = new DockPanel { Margin = new Thickness(14), Background = Brushes.WhiteSmoke };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Bulk study intake — manifest preview and reconciliation", FontSize = 21 });
        header.Children.Add(new TextBlock { Text = Guidance, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 4), FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x71, 0x80)) });
        var source = new WrapPanel(); source.Children.Add(Label("Manifest")); source.Children.Add(_manifest); source.Children.Add(_manifestHidden); source.Children.Add(_choose); source.Children.Add(_preview); source.Children.Add(_cancel);
        source.Children.Add(_blind); source.Children.Add(Label("Reviewer / reason to reveal")); source.Children.Add(_unblindReviewer); source.Children.Add(_unblindReason);
        header.Children.Add(source); header.Children.Add(_status); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var width in new[] { GridLength.Auto, new GridLength(170), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) footer.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        Grid.SetColumnSpan(_reviewed, 5); _reviewed.Margin = new Thickness(6, 0, 0, 4); footer.Children.Add(_reviewed);
        foreach (var (element, column) in new (UIElement, int)[] { (_reviewerLabel, 0), (_reviewer, 1), (_reasonLabel, 2), (_reason, 3), (_apply, 4) })
        { Grid.SetRow(element, 1); Grid.SetColumn(element, column); footer.Children.Add(element); }
        Grid.SetRow(_authoredHidden, 1); Grid.SetColumn(_authoredHidden, 0); Grid.SetColumnSpan(_authoredHidden, 4); footer.Children.Add(_authoredHidden);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new DockPanel(); DockPanel.SetDock(_summary, Dock.Top); body.Children.Add(_summary);
        var tabs = new TabControl();
        foreach (var (title, content) in new (string, UIElement)[] { ("Conflicts and warnings", _conflicts), ("Missing slots", _missing), ("Animals", _animals), ("Manifest rows and slot actions", _rows), ("Planned changes", _changes), ("Intake receipts", _receipts) })
            tabs.Items.Add(new TabItem { Header = title, Content = content });
        body.Children.Add(tabs); root.Children.Add(body); Content = root;
        _manifest.TextChanged += (_, _) => { if (Preview is not null && !SamePath(Preview.Manifest.Path, _manifest.Text)) { Preview = null; Refresh("Manifest path changed; preview again before applying."); } };
        _reviewed.Checked += (_, _) => UpdateGates(); _reviewed.Unchecked += (_, _) => UpdateGates();
        _blind.Checked += (_, _) => { if (!_ready) return; _unblindReviewer.Clear(); _unblindReason.Clear(); Refresh("Blind review on; identifying intake metadata hidden."); };
        _blind.Unchecked += (_, _) => { if (!_ready || _auditReveal) { if (_ready) Refresh(null); return; } var reviewer = _unblindReviewer.Text; var reason = _unblindReason.Text; _blind.IsChecked = true; TryUnblind(reviewer, reason); };
        // Close fence: a running preview/apply is cancelled at its next safe point and the window (and an owner waiting on it)
        // closes only after the operation recorded its outcome. A plan transaction that already started finishes first.
        Closing += (_, args) =>
        {
            if (!Busy) return;
            args.Cancel = true; RequestClose();
        };
        Blinded = blinded; _ready = true; Refresh("Choose a UTF-8 CSV manifest and preview it. Nothing is written until a reviewed preview is applied.",
            "Reveal metadata with a reviewer and reason to choose a manifest. Nothing is written until a reviewed preview is applied.");
    }

    /// <summary>Closes now when idle; otherwise cancels the running operation and closes once its outcome is recorded.</summary>
    public void RequestClose()
    {
        if (!Busy) { Close(); return; }
        if (!CloseRequested) { CloseRequested = true; _operation?.Cancel(); }
        Say("Closing after the current intake step records its outcome…"); UpdateGates();
    }

    public bool TryUnblind(string reviewer, string reason)
    {
        if (!Blinded) return true;
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason)) { Say("Reviewer and reason required; intake remains blind."); return false; }
        try
        {
            _store.Audit(StudyIntakeReconciler.AuditPrefix + "unblinded", new { Reviewer = reviewer.Trim(), Reason = reason.Trim(), Preview?.PreviewId });
            _auditReveal = true; _blind.IsChecked = false; Refresh("Intake metadata revealed; the reviewer and reason are stored in the study audit."); return true;
        }
        catch (Exception) { Say("Unblinding audit failed; intake remains blind."); return false; }
        finally { _auditReveal = false; }
    }

    private void ChooseManifest()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Study manifest CSV|*.csv|All files|*.*", CheckFileExists = true, Title = "Choose study intake manifest" };
        if (dialog.ShowDialog(this) == true) _manifest.Text = dialog.FileName;
    }

    public async Task PreviewAsync()
    {
        if (Busy || CloseRequested) return;
        var path = _manifest.Text.Trim();
        if (path.Length == 0) { Refresh("Choose a manifest file first.", "Reveal metadata with a reviewer and reason to choose a manifest."); return; }
        Preview = null; _reviewed.IsChecked = false; Busy = true; var operation = new CancellationTokenSource(); _operation = operation;
        Refresh("Reading manifest and hashing listed source files…");
        try
        {
            var progress = new Progress<StudyIntakeProgress>(p => { if (Busy && !CloseRequested) Say($"{p.Stage}: {p.Done}/{p.Total} files."); });
            Preview = await StudyIntakeReconciler.PreviewAsync(_store, path, _protocol, progress, operation.Token);
            Refresh(Preview.Blocked ? $"Preview found {Preview.Conflicts.Length} blocking conflicts. Fix them in the manifest or plan and preview again; nothing was written."
                : Preview.HasChanges ? "Preview ready. Review every tab, then enter reviewer and reason and apply. Nothing has been written yet."
                : "Manifest already matches the plan and recordings; there is nothing to apply.");
        }
        catch (OperationCanceledException) { Preview = null; Refresh("Preview cancelled; nothing was written."); }
        catch (Exception ex) { Preview = null; Refresh("Preview failed: " + ex.Message, "Preview failed; nothing was written. Reveal metadata to see the manifest error."); }
        finally { Busy = false; if (ReferenceEquals(_operation, operation)) _operation = null; operation.Dispose(); UpdateGates(); CloseIfRequested(); }
    }

    public async Task ApplyAsync()
    {
        if (Busy || CloseRequested) return;
        if (Blinded) { Refresh("Reveal metadata with a reviewer and reason before applying identifying plan changes; nothing was written."); return; }
        if (Preview is not { } preview) { Refresh("Preview the manifest before applying; nothing was written."); return; }
        if (!SamePath(preview.Manifest.Path, _manifest.Text)) { Preview = null; Refresh("Manifest path changed since preview; preview again."); return; }
        // The owner's processing, metadata and project actions stay gated until this apply records its outcome.
        if (AcquireOwner?.Invoke() is { } refusal) { Refresh(refusal); return; }
        _ownerHeld = AcquireOwner is not null;
        Busy = true; UpdateGates(); var operation = new CancellationTokenSource(); _operation = operation;
        Say("Applying reviewed preview…");
        try
        {
            var receipt = await StudyIntakeReconciler.ApplyAsync(_store, preview, _reviewer.Text, _reason.Text, _reviewed.IsChecked == true, operation.Token, ApplyCheckpoint);
            LastReceipt = receipt;
            if (receipt.Outcome != StudyIntakeOutcome.Refused) { Preview = null; _reviewed.IsChecked = false; }
            // Receipt messages can quote exceptions or lines; while blind only the outcome and revision are shown (the reveal is audited).
            var blindSafe = $"Intake outcome: {receipt.Outcome}" + (receipt.PlanRevisionAfter is { } revision ? $"; plan revision {revision} saved" : "; no plan revision was written") +
                $"; {receipt.RegisteredRecordingIds.Length} sources registered by this attempt. Details hidden while blind; reveal with reviewer and reason to read the receipt.";
            Refresh(receipt.Outcome switch
            {
                StudyIntakeOutcome.Applied => receipt.Message + " Remaining confirmations are made locally in the plan editor and recording review.",
                StudyIntakeOutcome.Refused => "Apply refused: " + receipt.Message,
                StudyIntakeOutcome.NoChanges => receipt.Message,
                _ => receipt.Message + " Preview again before retrying."
            }, blindSafe);
            if (receipt.Outcome == StudyIntakeOutcome.Applied) IntakeApplied?.Invoke(this, receipt);
        }
        finally
        {
            Busy = false; if (ReferenceEquals(_operation, operation)) _operation = null; operation.Dispose();
            if (_ownerHeld) { _ownerHeld = false; ReleaseOwner?.Invoke(); }
            UpdateGates(); CloseIfRequested();
        }
    }

    private void CloseIfRequested() { if (CloseRequested) Dispatcher.BeginInvoke(Close); }

    private void Say(string full, string? blindSafe = null)
    {
        _statusFull = full; _statusBlind = blindSafe ?? full;
        _status.Text = Blinded ? _statusBlind : _statusFull;
    }

    private void Refresh(string? status, string? blindStatus = null)
    {
        if (status is not null) Say(status, blindStatus);
        var preview = Preview; var blind = Blinded;
        _status.Text = blind ? _statusBlind : _statusFull;
        // Identifying inputs (the selected path and authored reviewer/reason) are hidden, not cleared, while blind.
        _manifest.Visibility = _choose.Visibility = _reviewer.Visibility = _reason.Visibility = _reviewerLabel.Visibility = _reasonLabel.Visibility = blind ? Visibility.Collapsed : Visibility.Visible;
        _manifestHidden.Visibility = _authoredHidden.Visibility = blind ? Visibility.Visible : Visibility.Collapsed;
        var animalIndex = preview?.Animals.ToDictionary(a => a.AnimalId, a => a.Index, StringComparer.OrdinalIgnoreCase) ?? [];
        string Animal(string id) => blind ? $"Animal {animalIndex.GetValueOrDefault(id):000}" : id;
        if (preview is null) { _summary.Text = "No current preview."; _conflicts.ItemsSource = null; _missing.ItemsSource = null; _animals.ItemsSource = null; _rows.ItemsSource = null; _changes.ItemsSource = null; }
        else
        {
            var groups = blind ? "Group counts hidden while blind." : "Animals per group: " + string.Join("; ", _protocol.Groups.Select(g => $"{g} {preview.Animals.Count(a => string.Equals(a.Group, g, StringComparison.OrdinalIgnoreCase))}")) + ".";
            _summary.Text = $"Manifest rows {preview.Manifest.Rows.Length} · animals {preview.Animals.Length} ({preview.Animals.Count(a => a.New)} new to the plan) · video files {preview.Files.Length} " +
                $"({preview.ExistingRecordings} already imported, {preview.PendingImports} to register) · expected slots {preview.ExpectedSlots} ({preview.Animals.Length} × {_protocol.ExpectedSlotsPerAnimal}) · " +
                $"mapped after apply {preview.MappedSlots} · explicitly missing {preview.MissingSlots.Length} · blocking conflicts {preview.Conflicts.Length} · warnings {preview.Warnings.Length} · " +
                $"plan revision {preview.PlanRevision}{(preview.HasChanges && !preview.Blocked ? " → " + (preview.PlanRevision + 1) : " (unchanged)")}.\n{groups} " +
                "Files are not animals; group summaries weight animals, never files or frames.";
            _conflicts.ItemsSource = preview.Issues.OrderBy(i => i.Blocking ? 0 : 1).ThenBy(i => i.Lines.IsDefaultOrEmpty ? 0 : i.Lines[0])
                .Select(i => new IssueRow(i.Blocking ? "Conflict" : "Warning", i.LineLabel, i.Code, blind ? "Details hidden while blind; reveal with reviewer and reason." : i.Message)).ToArray();
            _missing.ItemsSource = preview.MissingSlots.Select(s => new SlotRow(Animal(s.AnimalId), s.Day, s.Trial, s.Action == StudyIntakeSlotAction.AddMissing ? "New slot, explicitly missing" : "Existing slot, still missing", blind ? "—" : s.TrialId, s.Line?.ToString() ?? "")).ToArray();
            _animals.ItemsSource = preview.Animals.Select(a => new AnimalRow(a.Index, Animal(a.AnimalId), blind ? "hidden while blind" : a.Group, a.New ? "new (unconfirmed)" : "existing", a.Rows, $"{a.MappedAfterApply}/{a.Expected}",
                blind ? (a.Updates.IsEmpty ? "" : "hidden") : string.Join(", ", a.Updates))).ToArray();
            var lines = preview.Slots.Where(s => s.Line is not null).ToDictionary(s => s.Line!.Value);
            _rows.ItemsSource = preview.Manifest.Rows.Select(r => lines.TryGetValue(r.Line, out var s)
                ? new SlotRow(Animal(s.AnimalId), r.Day, r.Trial, Action(s.Action, preview.Files.FirstOrDefault(f => f.Sha256 == s.Sha256)?.ExistingRecordingId is null && s.Sha256 is not null), blind ? "Recording" : r.VideoPath, r.Line.ToString())
                : new SlotRow(Animal(r.AnimalId), r.Day, r.Trial, "Not placed (see conflicts)", blind ? "Recording" : r.VideoPath, r.Line.ToString())).ToArray();
            _changes.ItemsSource = blind ? new[] { $"{preview.Changes.Length} planned changes hidden while blind; reveal with reviewer and reason to review them." } : preview.Changes.DefaultIfEmpty("No plan changes.").ToArray();
        }
        try
        {
            _receipts.ItemsSource = StudyIntakeReconciler.Receipts(_store).Reverse().Select(r => new ReceiptRow(r.AuditId, r.Receipt.Outcome.ToString(),
                r.Receipt.PlanRevisionAfter is { } after ? $"{r.Receipt.PlanRevisionBefore} → {after}" : $"{r.Receipt.PlanRevisionBefore} (unchanged)", r.Receipt.CreatedUtc.ToString("u"),
                blind ? $"{r.Receipt.RegisteredRecordingIds.Length} sources registered; details hidden while blind." : r.Receipt.Message)).ToArray();
        }
        catch (Exception) { _receipts.ItemsSource = null; }
        UpdateGates();
    }
    private static string Action(StudyIntakeSlotAction action, bool register) => action switch
    {
        StudyIntakeSlotAction.Unchanged => "Already mapped (unchanged)",
        StudyIntakeSlotAction.Fill => register ? "Register + fill empty slot" : "Fill empty slot",
        StudyIntakeSlotAction.AddAndFill => register ? "Register + new slot" : "New slot, mapped",
        StudyIntakeSlotAction.KeepMapped => "Existing mapping kept",
        _ => "Missing"
    };
    private void UpdateGates()
    {
        _preview.IsEnabled = !Busy && !CloseRequested; _choose.IsEnabled = !Busy && !CloseRequested; _cancel.IsEnabled = Busy && !CloseRequested; _manifest.IsReadOnly = Busy;
        _apply.IsEnabled = !Busy && !CloseRequested && !Blinded && Preview is { Blocked: false, HasChanges: true } && _reviewed.IsChecked == true;
        _reviewed.IsEnabled = !Busy && Preview is { Blocked: false };
    }
    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a.Trim()), Path.GetFullPath(b.Trim()), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }
    private static DataGrid Table(params (string Header, string Path, double Width)[] columns)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 160,
            EnableRowVirtualization = true, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, Background = Brushes.White };
        VirtualizingPanel.SetIsVirtualizing(grid, true);
        foreach (var (header, path, width) in columns)
        {
            var column = new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star) };
            if (width <= 0) column.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } };
            grid.Columns.Add(column);
        }
        return grid;
    }
    private static Button Button(string caption, RoutedEventHandler action) { var button = new Button { Content = caption, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) }; button.Click += action; return button; }
    private static TextBlock Label(string value) => new() { Text = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };

    public sealed record IssueRow(string Severity, string Lines, string Code, string Message);
    public sealed record SlotRow(string Animal, int Day, int Trial, string Action, string Slot, string Line) { public string Recording => Slot; }
    public sealed record AnimalRow(int Index, string Animal, string Group, string Status, int Rows, string Mapped, string Updates);
    public sealed record ReceiptRow(long Id, string Outcome, string Revision, string Created, string Message);
}
