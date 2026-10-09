using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.SpatialViews;
using MouseTrackerTabajara.SpatialViews.Wpf;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Desktop;

public sealed class ExperimentWindow : Window
{
    private readonly string _database;
    private readonly bool _showSourceWindows;
    private readonly CheckBox _blind = new() { Content = "Blind review" };
    private readonly TextBox _unblindReviewer = new() { Width = 120, ToolTip = "Unblinding reviewer" };
    private readonly TextBox _unblindReason = new() { Width = 180, ToolTip = "Reason for unblinding this view" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _contexts = new() { MinWidth = 200 };
    private readonly StackPanel _results = new();
    private readonly TabItem _editor = new() { Header = "Expected plan editor" };
    private readonly TabItem _temporalTab = new() { Header = "Temporal and across-day profiles" };
    private readonly DataGrid _animals = new() { AutoGenerateColumns = false, CanUserAddRows = true, MinHeight = 170 };
    private readonly DataGrid _contextGrid = new() { AutoGenerateColumns = false, CanUserAddRows = true, MinHeight = 150 };
    private readonly DataGrid _trials = new() { AutoGenerateColumns = false, CanUserAddRows = true, MinHeight = 220 };
    private readonly TextBox _reviewer = new() { MinWidth = 150 };
    private readonly TextBox _reason = new() { MinWidth = 250 };
    private readonly ComboBox _lucianaGroup = new() { ItemsSource=LucianaProtocolPreset.Groups,MinWidth=210,SelectedIndex=0,Tag="luciana-protocol-group" };
    private readonly DispatcherTimer _integrity = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _loading, _closed, _checking, _blindReady, _auditReveal, _initializing = true;
    private int _computeGeneration;
    private readonly IExperimentCalculator _calculator;
    private readonly SemaphoreSlim _computeGate = new(1, 1);
    private CancellationTokenSource? _computeCancellation;
    private readonly Dictionary<string, (long Length, DateTime Written, DateTime Created)> _sourceStamps = [];
    public AtlasSnapshot Snapshot { get; private set; }
    public ExperimentPlanDraft Draft { get; private set; }
    public long ExpectedPlanRevision { get; private set; }
    public ExperimentResult? Result { get; private set; }
    public StudyAtlasWindow? LastSourceViewer { get; private set; }
    public Task PendingSource { get; private set; } = Task.CompletedTask;
    public Task PendingCompute { get; private set; } = Task.CompletedTask;
    public bool Busy { get; private set; }
    public TemporalStudy? TemporalResult { get; private set; }
    public bool Held { get; private set; }
    public string StatusText => _status.Text;
    public bool Blinded { get => _blind.IsChecked == true; set => _blind.IsChecked = value; }
    public DataGrid AnimalsEditor => _animals; public DataGrid ContextsEditor => _contextGrid; public DataGrid TrialsEditor => _trials;
    public TextBox ReviewerInput => _reviewer; public TextBox ReasonInput => _reason;
    public ExperimentWindow(string database, bool blinded, bool showSourceWindows = true, IExperimentCalculator? calculator = null)
    {
        _database = database; _showSourceWindows = showSourceWindows; _calculator = calculator ?? new NativeExperimentCalculator(); Snapshot = AtlasSnapshot.Read(database);
        var saved = ExperimentPlanAdapter.FrozenPlan(Snapshot); Draft = ExperimentPlanDraft.From(saved?.Plan ?? new()); ExpectedPlanRevision = saved?.Revision ?? 0;
        Title = "Mouse Tracker Tabajara — Experiment plan and maps"; Width = 1440; Height = 960; MinWidth = 1050; MinHeight = 700;
        var root = new DockPanel { Margin = new Thickness(16), Background = Brushes.WhiteSmoke };
        var header = new StackPanel(); header.Children.Add(new TextBlock { Text = "Experiment — expected animals, trials and conditional maps", FontSize = 23 });
        var commands = new WrapPanel(); commands.Children.Add(_blind); commands.Children.Add(_contexts);
        commands.Children.Add(new TextBlock { Text = "Reviewer / reason to unblind", VerticalAlignment = VerticalAlignment.Center }); commands.Children.Add(_unblindReviewer); commands.Children.Add(_unblindReason);
        var recompute = Button("Reload and recompute frozen results", (_, _) => Reload()); commands.Children.Add(recompute);
        commands.Children.Add(Button("Cancel calculation", (_, _) => { _computeCancellation?.Cancel(); Hold("Calculation cancelled; computed results were not published."); }));
        commands.Children.Add(Button("Export frozen temporal profiles", (_, _) => ExportTemporal()));
        header.Children.Add(commands); header.Children.Add(_status); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "Summary and maps", Content = new ScrollViewer { Content = _results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }); tabs.Items.Add(_editor); tabs.Items.Add(_temporalTab); root.Children.Add(tabs); Content = root;
        _contexts.SelectionChanged += (_, _) => { if (!_loading) ComputeSelected(); };
        _blind.Checked += (_, _) => { if (_initializing) return; _unblindReviewer.Clear(); _unblindReason.Clear(); if (LastSourceViewer is not null) LastSourceViewer.Blinded = true; RefreshPresentation(); };
        _blind.Unchecked += (_, _) =>
        {
            if (_initializing) return;
            if (!_blindReady || _auditReveal) { RefreshPresentation(); return; }
            var reviewer = _unblindReviewer.Text; var reason = _unblindReason.Text; _blind.IsChecked = true; TryUnblind(reviewer, reason);
        };
        _integrity.Tick += async (_, _) =>
        {
            if (_closed || _checking || Held || Busy) return; _checking = true; var frozen = Snapshot; var generation = _computeGeneration; var sources = _sourceStamps.ToArray();
            try { if (!await Task.Run(() => frozen.IsCurrent() && SourcesUnchanged(sources)) && !_closed && generation == _computeGeneration && frozen.Revision == Snapshot.Revision) Hold("Frozen experiment/source changed. Reload and explicitly recompute results."); }
            catch (Exception) { if (!_closed) Hold("Experiment integrity is unavailable; results held."); }
            finally { _checking = false; }
        };
        _integrity.Start(); Closed += (_, _) => { _closed = true; _computeGeneration++; _computeCancellation?.Cancel(); _integrity.Stop(); LastSourceViewer?.Close(); };
        ConfigureEditor(); Blinded = blinded; _blindReady = true; _initializing = false; RefreshPresentation();
    }
    public bool TryUnblind(string reviewer, string reason)
    {
        if (!Blinded) return true;
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason)) { _status.Text = "Reviewer and reason required; experiment remains blind."; return false; }
        try
        {
            new StudyStore(_database).Audit("experiment_unblinded", new { Reviewer = reviewer.Trim(), Reason = reason.Trim(), SnapshotRevision = Snapshot.Revision });
            _auditReveal = true; _blind.IsChecked = false; return true;
        }
        catch (Exception) { _status.Text = "Unblinding audit failed; experiment remains blind."; return false; }
        finally { _auditReveal = false; }
    }
    private void ExportTemporal()
    {
        if (Busy || Held || Result is null || TemporalResult is null || !Snapshot.IsCurrent() || !SourcesUnchanged(_sourceStamps.ToArray())) { Hold("Frozen profiles are stale or unavailable; export held."); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Temporal profile CSV|*.csv", FileName = "temporal-profiles.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { TemporalProfileExport.Save(dialog.FileName, TemporalResult, Result.Plan, Blinded, Snapshot.Revision); _status.Text = "Exported frozen descriptive profiles with explicit denominators, null holds and current privacy projection."; }
        catch (Exception) { _status.Text = "Profile export failed; check destination permissions."; }
    }
    public void Reload()
    {
        LastSourceViewer?.Close(); LastSourceViewer = null;
        try { Snapshot = AtlasSnapshot.Read(_database); var saved = ExperimentPlanAdapter.FrozenPlan(Snapshot); Draft = ExperimentPlanDraft.From(saved?.Plan ?? new()); ExpectedPlanRevision = saved?.Revision ?? 0; Held = false; RefreshPresentation(); }
        catch (Exception) { Hold("Cannot freeze the current plan; verify material integrity."); }
    }
    public void SavePlan()
    {
        if (Blinded) { Hold("Unblind explicitly before editing identifying study plans."); return; }
        try
        {
            foreach (var grid in new[] { _animals, _contextGrid, _trials }) { grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true); }
            var saved = new StudyPlanStore(new StudyStore(_database)).Save(Draft.Freeze(), _reviewer.Text, _reason.Text, ExpectedPlanRevision);
            ExpectedPlanRevision = saved.Revision; Reload(); _status.Text = "Expected plan revision saved. Confirmed design/roster/reference and recorded inspections are separate from independent accuracy validation.";
        }
        catch (Exception ex)
        {
            if (ex is InvalidOperationException && ex.Message.Contains("changed since it was opened", StringComparison.Ordinal)) Hold(ex.Message);
            else _status.Text = Blinded ? "Plan save held." : ex.Message;
        }
    }
    public void ConfirmSelectedSourceReference()
    {
        if (Blinded || _trials.SelectedItem is not ExperimentTrialDraft slot) return;
        try
        {
            var row = Snapshot.Recordings.Single(r => r.Asset.Id == slot.RecordingId);
            if (!Snapshot.IsCurrent() || row.RawRun is not { } run || StudyStore.SettingsHash(run.Settings) != StudyStore.SettingsHash(row.Asset.Settings)) throw new InvalidDataException("Select a current analyzed geometry/source before confirming its reference.");
            slot.ReferenceSourceSha256 = row.Asset.SourceSha256; slot.ReferenceSettingsHash = StudyStore.SettingsHash(run.Settings); slot.SourceReferenceConfirmed = true; _trials.Items.Refresh();
            _status.Text = "Selected source/reference hashes bound explicitly. Its room rotation or target direction and context alignment require separate confirmation.";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void ReloadContexts()
    {
        _loading = true; _contexts.Items.Clear(); var contexts = ExperimentPlanAdapter.FrozenPlan(Snapshot)?.Plan.Contexts ?? [];
        for (var i = 0; i < contexts.Length; i++) _contexts.Items.Add(new ComboBoxItem { Content = Blinded ? $"Context {i + 1:000} · {contexts[i].Phase} · day {contexts[i].Day}" : $"{contexts[i].Id} · {contexts[i].Phase} · day {contexts[i].Day}", Tag = contexts[i].Id });
        _contexts.SelectedIndex = contexts.Length > 0 ? 0 : -1; _loading = false; ComputeSelected();
    }
    public void SelectContext(string id)
    {
        var target = _contexts.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, id));
        if (ReferenceEquals(_contexts.SelectedItem, target)) ComputeSelected(); else _contexts.SelectedItem = target;
    }
    private void ComputeSelected()
    {
        var generation = ++_computeGeneration; _computeCancellation?.Cancel(); Result = null; TemporalResult = null; _temporalTab.Content = Text("Profile calculation pending; previous plots cleared."); _results.Children.Clear(); _sourceStamps.Clear();
        LastSourceViewer?.Close(); LastSourceViewer = null;
        if (_contexts.SelectedItem is not ComboBoxItem item) { Busy = false; PendingCompute = Task.CompletedTask; _results.Children.Add(Text("No saved comparison context. Enter expected animals, context and trial slots; imported files cannot define the plan.")); return; }
        var cancellation = new CancellationTokenSource(); _computeCancellation = cancellation; Busy = true; Held = true;
        _status.Text = "Calculating frozen descriptive results; previous results cleared. Context changes supersede pending work.";
        PendingCompute = CalculateAsync(Snapshot, (string)item.Tag, generation, cancellation);
    }
    private async Task CalculateAsync(AtlasSnapshot snapshot, string context, int generation, CancellationTokenSource cancellation)
    {
        var acquired = false;
        try
        {
            await _computeGate.WaitAsync(cancellation.Token); acquired = true;
            var computed = await Task.Run(() => ExperimentComputation.Run(snapshot, context, _calculator, cancellation.Token), cancellation.Token);
            if (_closed || generation != _computeGeneration || cancellation.IsCancellationRequested || Snapshot.Revision != snapshot.Revision) return;
            if (computed.Sources.Any(source => ExperimentSourceStamp.Read(source.Path) != source)) throw new InvalidDataException("Source changed before publication.");
            Result = computed.Result;
            TemporalResult = computed.Temporal;
            foreach (var source in computed.Sources.Where(s => s.Exists)) _sourceStamps[source.Path] = (source.Length, source.Written, source.Created);
            Held = false; ShowResults();
            _temporalTab.Content = new TemporalProfileControl(computed.Temporal, computed.Result.Plan, Blinded,
                profile => { if (profile.Recording is { } row) OpenSource(row, profile.FirstFrame ?? row.RawRun?.Points.FirstOrDefault()?.Frame ?? 0); },
                visit => { if (visit.Recording is { } row) OpenSource(row, visit.Frame ?? row.RawRun?.Points.FirstOrDefault()?.Frame ?? 0); });
        }
        catch (OperationCanceledException) { if (!_closed && generation == _computeGeneration) Hold("Calculation cancelled; no results published."); }
        catch (Exception) { if (!_closed && generation == _computeGeneration) Hold("Calculation/source/context changed or is invalid; results held until explicit recomputation."); }
        finally
        {
            if (acquired) _computeGate.Release(); if (ReferenceEquals(_computeCancellation, cancellation)) _computeCancellation = null;
            cancellation.Dispose(); if (generation == _computeGeneration) Busy = false;
        }
    }
    private void ShowResults()
    {
        _results.Children.Clear(); if (Result is not { } result) return;
        _results.Children.Add(Text(Blinded ? "Blinded conditional inspection; allocation summaries hidden." : result.Interpretation));
        _results.Children.Add(Text(Blinded ? "Normalized pool radii; fixed common axes. Measured projective calibration is not supplied."
            : $"{result.Context.Phase}, day {result.Context.Day}; release-relative interval {result.Context.ComparisonStartSeconds:0.###}–{result.Context.ComparisonStartSeconds + result.Context.ComparisonDurationSeconds:0.###} s. Units: pool radii; common axes −1.01 to1.01; {result.Context.Orientation}; reference {result.Context.CommonCoordinateReference}. Not measured projective calibration."));
        _results.Children.Add(Text($"Expected animals {result.Plan.Animals.Length}; eligible {result.Study.Animals.Count(a => a.Eligible)}; held {result.Study.Animals.Count(a => !a.Eligible)}. Expected trials {result.Trials.Length}; eligible {result.Trials.Count(t => t.Holds.IsEmpty && t.Map?.Eligible == true)}; held {result.Trials.Count(t => !t.Holds.IsEmpty || t.Map?.Eligible != true)}; unassigned imports {result.Unassigned.Length}."));
        var history = new StackPanel();
        foreach (var row in Snapshot.Rows("study_plans")) history.Children.Add(Text(Blinded ? "Saved expected-plan revision " + row.Value("revision")
            : $"Revision {row.Value("revision")} · {row.Value("created_utc")} · {row.Value("reviewer")}\n{row.Value("reason")}"));
        _results.Children.Add(new Expander { Header = "Recorded plan revision history", Content = new ScrollViewer { MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = history } });
        if (!Blinded)
        {
            var groupPlots = new WrapPanel(); foreach (var map in result.Study.Groups) groupPlots.Children.Add(DrawGroup(map, result.Study.SharedKnownFractionMaximum)); _results.Children.Add(groupPlots);
            foreach (var group in result.Study.Groups)
            {
                var contributors = result.Study.Animals.Where(a => group.IncludedAnimalIds.Contains(a.Input.AnimalId)).ToArray();
                _results.Children.Add(Button($"Trace every contribution: {group.Group} ({group.IncludedAnimalIds.Length} animals)", (_, _) => ShowContributions(contributors.SelectMany(a => a.Input.Trials).Select(t => t.Input.TrialId).ToHashSet(StringComparer.Ordinal))));
            }
            foreach (var difference in result.Differences)
            {
                _results.Children.Add(Text($"{difference.GroupA} minus {difference.GroupB}: per-cell conditional bounds (not confidence intervals). Width reflects unknown position mass; eligible-only subset."));
                var plots = new WrapPanel(); plots.Children.Add(DrawBounds(difference, false)); plots.Children.Add(DrawBounds(difference, true)); _results.Children.Add(plots);
                _results.Children.Add(Button($"Trace difference: {difference.GroupA} / {difference.GroupB}", (_, _) => ShowContributions(result.Study.Animals.Where(a => a.Eligible && (a.Input.Group == difference.GroupA || a.Input.Group == difference.GroupB)).SelectMany(a => a.Input.Trials).Select(t => t.Input.TrialId).ToHashSet(StringComparer.Ordinal))));
            }
        }
        var atlas = new AtlasControl { Blinded = Blinded, Height = 500 }; atlas.Load(result.Study with { Groups = [] });
        atlas.FrameRequested += (_, args) =>
        {
            var row = result.Snapshot.Recordings.Single(r => r.NavigationId == args.RunId && r.Asset.SourceSha256 == args.SourceSha256);
            OpenSource(row, args.Frame);
        };
        _results.Children.Add(atlas);
        ShowContributions(null);
        _status.Text = "Frozen experiment results. Maps are conditional on accepted positions; held and missing entries remain visible.";
    }
    private void ShowContributions(HashSet<string>? ids)
    {
        if (Result is not { } result) return;
        var prior = _results.Children.OfType<Expander>().FirstOrDefault(e => Equals(e.Tag, "contributions")); if (prior is not null) _results.Children.Remove(prior);
        var cards = new StackPanel();
        var animalBounds = result.AnimalOccupancyBounds;
        foreach (var animal in result.Study.Animals.Where(a => a.Eligible && (ids is null || a.Input.Trials.Any(t => ids.Contains(t.Input.TrialId)))))
        {
            var index = Array.FindIndex(result.Plan.Animals.ToArray(), a => a.Id == animal.Input.AnimalId) + 1;
            var title = Blinded ? $"Animal {index:000}: conditional occupancy bounds" : animal.Input.AnimalId + ": conditional occupancy bounds";
            var body = new StackPanel(); body.Children.Add(Text("Known accepted position time is the lower bound; unknown mass expands the upper bound, capped at1. Conditional on correct accepted coordinates; not confidence intervals."));
            var bounds = new GroupDifferenceBounds("", "", animalBounds[animal.Input.AnimalId]);
            var panels = new WrapPanel(); panels.Children.Add(DrawBounds(bounds, false)); panels.Children.Add(DrawBounds(bounds, true)); body.Children.Add(panels);
            cards.Children.Add(new Expander { Header = title, Content = body });
        }
        foreach (var trial in result.Trials.Where(t => ids is null || ids.Contains(t.Slot.Id)))
        {
            var index = Array.FindIndex(result.Plan.Animals.ToArray(), a => a.Id == trial.Slot.AnimalId) + 1;
            var title = Blinded ? $"Animal {index:000} · trial {trial.Slot.Order}" : $"{trial.Slot.AnimalId} · {trial.Slot.Id} · {trial.Recording?.Asset.Filename ?? "missing source"}";
            var contents = new StackPanel(); contents.Children.Add(Text(title));
            contents.Children.Add(Text(trial.Holds.IsEmpty ? "Eligible conditional source; independent accuracy unvalidated." : Blinded ? "Comparison held; inspect frozen source and protocol." : string.Join("\n", trial.Holds)));
            if (!trial.Notes.IsDefaultOrEmpty) contents.Children.Add(Text(Blinded ? "Nonblocking protocol annotation notes remain recorded." : string.Join("\n", trial.Notes.Select(n => n.Text))));
            if (trial.Recording is { } row) contents.Children.Add(Button("Open frozen trial source", (_, _) => OpenSource(row, trial.Map?.Input.Frames.FirstOrDefault(f => f.EndSeconds > 0 && f.StartSeconds < trial.Map.Input.PlannedSeconds)?.Frame ?? row.RawRun?.Points.FirstOrDefault()?.Frame ?? 0)));
            cards.Children.Add(new Expander { Header = title, Content = contents, IsExpanded = ids is not null });
        }
        foreach (var row in result.Unassigned)
            cards.Children.Add(Button(Blinded ? "Open unassigned recording" : "Unassigned: " + row.Asset.Filename, (_, _) => OpenSource(row, row.RawRun?.Points.FirstOrDefault()?.Frame ?? 0)));
        _results.Children.Add(new Expander { Tag = "contributions", Header = ids is null ? "Expected trials, holds and unassigned imports" : "All selected map contributions, including outliers", IsExpanded = true,
            Content = new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = cards } });
    }
    private void OpenSource(AtlasRecording row, int frame)
    {
        if (Held || !Snapshot.IsCurrent()) { Hold("Frozen experiment changed; source tracing is held until recomputation."); return; }
        LastSourceViewer?.Close(); LastSourceViewer = new StudyAtlasWindow(_database, Blinded, Snapshot);
        if (_showSourceWindows) { LastSourceViewer.Owner = this; LastSourceViewer.Show(); }
        PendingSource = TraceSourceAsync(LastSourceViewer, row, frame);
    }
    private async Task TraceSourceAsync(StudyAtlasWindow viewer, AtlasRecording row, int frame)
    {
        await viewer.NavigateAsync(new() { RunId = row.NavigationId, SourceSha256 = row.Asset.SourceSha256, SnapshotRevision = Snapshot.Revision, Frame = frame });
        if (viewer.Held && !_closed && ReferenceEquals(viewer, LastSourceViewer)) Hold("Expected source identity changed or source is unavailable; experiment results held until revalidation.");
    }
    private void Hold(string message) { Held = true; Result = null; TemporalResult = null; _temporalTab.Content = Text("Frozen temporal/source evidence is held; reload to recompute."); _results.Children.Clear(); LastSourceViewer?.Close(); LastSourceViewer = null; _status.Text = Blinded ? "Frozen experiment held; reload and verify evidence." : message; }
    private static bool SourcesUnchanged(KeyValuePair<string, (long Length, DateTime Written, DateTime Created)>[] sources)
    {
        foreach (var (path, stamp) in sources) { var info = new FileInfo(path); if (!info.Exists || stamp != (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc)) return false; }
        return true;
    }
    private void RefreshPresentation()
    {
        _editor.Content = null;
        foreach (var control in new FrameworkElement[] { _animals, _contextGrid, _trials, _reviewer, _reason, _lucianaGroup })
            if (LogicalTreeHelper.GetParent(control) is Panel parent) parent.Children.Remove(control);
        _editor.Content = Blinded ? Text("Unblind explicitly to edit identifying expected study plans.") : EditorBody(); ReloadContexts();
    }
    private UIElement EditorBody()
    {
        var body = new StackPanel(); body.Children.Add(Text("Expected design is entered explicitly. Check confirmations only after verifying identities, allocation, windows and each source reference."));
        body.Children.Add(Button("Start Luciana six-day protocol (empty plan only)",(_,_)=>StartLucianaProtocol()));
        body.Children.Add(Text(LucianaProtocolPreset.Guidance));
        var allocation=new WrapPanel();allocation.Children.Add(_lucianaGroup);allocation.Children.Add(Button("Assign protocol group to selected animals (unconfirmed)",(_,_)=>AssignLucianaGroup()));body.Children.Add(allocation);
        body.Children.Add(Button("Add 21 expected trials for selected animal",(_,_)=>AddLucianaExpectedTrials()));
        var metadata = new WrapPanel();
        foreach (var (label, property, check) in new[] { ("Study title", "Title", false), ("Primary comparison", "PrimaryComparison", false), ("Design confirmed", "DesignConfirmed", true), ("Roster confirmed", "RosterConfirmed", true), ("Exploratory", "Exploratory", true) })
        {
            FrameworkElement control = check ? new CheckBox { Content = label } : new TextBox { MinWidth = 240, ToolTip = label };
            control.SetBinding(check ? CheckBox.IsCheckedProperty : TextBox.TextProperty, new Binding(property) { Source = Draft, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            if (check) metadata.Children.Add(control); else { var field = new StackPanel(); field.Children.Add(Text(label)); field.Children.Add(control); metadata.Children.Add(field); }
        }
        var design = new ComboBox { ItemsSource = Enum.GetValues<StudyUnitDesign>(), MinWidth = 180, Tag = "experiment-unit-design" }; design.SetBinding(ComboBox.SelectedItemProperty, new Binding("UnitDesign") { Source = Draft, Mode = BindingMode.TwoWay }); metadata.Children.Add(design); body.Children.Add(metadata);
        _animals.ItemsSource = Draft.Animals; _contextGrid.ItemsSource = Draft.Contexts; _trials.ItemsSource = Draft.Trials;
        body.Children.Add(Text("Animals and allocation")); body.Children.Add(_animals); body.Children.Add(Text("Explicit phase/day comparison contexts")); body.Children.Add(_contextGrid); body.Children.Add(Text("Expected trial slots and assigned recordings")); body.Children.Add(_trials);
        body.Children.Add(Button("Confirm selected source reference", (_, _) => ConfirmSelectedSourceReference()));
        body.Children.Add(Text("Reviewer")); body.Children.Add(_reviewer); body.Children.Add(Text("Reason for this plan revision")); body.Children.Add(_reason); body.Children.Add(Button("Save expected plan revision", (_, _) => SavePlan()));
        return new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    public void StartLucianaProtocol()
    {
        if(Blinded){_status.Text="Reasoned metadata reveal is required before editing an identifying plan.";return;}
        if(ExpectedPlanRevision!=0||!LucianaProtocolPreset.PopulateFresh(Draft)){_status.Text="Existing plan/draft preserved. The six-day template applies only to a fresh empty plan.";return;}
        RefreshPresentation();_status.Text="Six stage contexts added. Enter actual animals, add their 21 expected trials, and link recordings. Durations, source references, identities and experimental design remain unconfirmed.";
    }
    public void AssignLucianaGroup()
    {
        if(Blinded){_status.Text="Group assignment is hidden while blind.";return;}
        if(_lucianaGroup.SelectedItem is not string group||!LucianaProtocolPreset.AssignGroup(Draft,_animals.SelectedItems.OfType<ExperimentAnimalDraft>(),group)){_status.Text="Select actual entered animals and one protocol group. No new animals are inferred.";return;}
        _animals.Items.Refresh();_status.Text="Selected actual animals assigned to the protocol group; allocation confirmation cleared. Verify locally before confirming/saving.";
    }
    public void AddLucianaExpectedTrials()
    {
        if(Blinded){_status.Text="Expected-trial editing is hidden while blind.";return;}
        var selected=_animals.SelectedItems.OfType<ExperimentAnimalDraft>().ToArray();
        if(selected.Length!=1||!LucianaProtocolPreset.AppendExpectedTrials(Draft,selected[0],out var added)){_status.Text="Select one existing actual animal ID and matching six protocol stage contexts; ambiguous/duplicate slots are preserved and refused.";return;}
        _trials.Items.Refresh();_status.Text=$"Added {added} missing protocol slots for the same animal: four/day1–5 and one/day6. Existing mappings preserved; assign recordings and verify local metadata before saving. No confirmations changed.";
    }
    private void ConfigureEditor()
    {
        foreach (var (grid, fields) in new[] { (_animals, new[] { "Id", "Alias", "Group", "IdentityConfirmed", "AllocationConfirmed", "Cage", "Litter", "Excluded", "ExclusionReason" }),
            (_contextGrid, new[] { "Id", "Phase", "Day", "RequestedExposureSeconds", "ComparisonStartSeconds", "ComparisonDurationSeconds", "TemporalBins", "WindowConfirmed", "CommonCoordinateReference", "Orientation", "AlignmentConfirmed" }),
            (_trials, new[] { "Id", "AnimalId", "ContextId", "Order", "RecordingId", "MissingReason", "SourceReferenceConfirmed", "RoomRotationRadians" }) })
            foreach (var field in fields)
            {
                var header = System.Text.RegularExpressions.Regex.Replace(field, "([a-z])([A-Z])", "$1 $2");
                var binding = new Binding(field) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
                if (field is "Phase" or "Orientation") grid.Columns.Add(new DataGridComboBoxColumn { Header = header, ItemsSource = field == "Phase" ? Enum.GetValues<TrialMode>() : Enum.GetValues<StudyOrientation>(), SelectedItemBinding = binding });
                else if (field == "RecordingId") grid.Columns.Add(new DataGridComboBoxColumn { Header = "Assigned recording", ItemsSource = Snapshot.Recordings.Select(r => new { Id = r.Asset.Id, Label = r.Asset.Filename }).ToArray(), DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedValueBinding = binding });
                else if (field.EndsWith("Confirmed", StringComparison.Ordinal) || field == "Excluded") grid.Columns.Add(new DataGridCheckBoxColumn { Header = header, Binding = binding });
                else grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = binding });
            }
    }
    private static Button Button(string caption, RoutedEventHandler action) { var button = new Button { Content = caption, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) }; button.Click += action; return button; }
    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 6) };
    private static UIElement DrawBounds(GroupDifferenceBounds difference, bool upper)
    {
        var panel = new StackPanel(); panel.Children.Add(Text(upper ? "Upper bound" : "Lower bound")); var canvas = new Canvas { Width = 240, Height = 240 };
        for (var i = 0; i < difference.Cells.Length; i++)
        {
            var value = upper ? difference.Cells[i].Upper : difference.Cells[i].Lower; var level = (byte)(255 * Math.Abs(value));
            var cell = new Rectangle { Width = 20, Height = 20, Fill = new SolidColorBrush(value >= 0 ? Color.FromRgb(255, (byte)(255 - level), (byte)(255 - level)) : Color.FromRgb((byte)(255 - level), (byte)(255 - level), 255)) };
            Canvas.SetLeft(cell, i % 12 * 20); Canvas.SetTop(cell, i / 12 * 20); canvas.Children.Add(cell);
        }
        panel.Children.Add(canvas); return panel;
    }
    private static UIElement DrawGroup(SpatialGroupMap group, double maximum)
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(Text($"{group.Group}: {group.IncludedAnimalIds.Length} contributing animals; {group.HeldAnimalIds.Length} held. Eligible-only subset."));
        var canvas = new Canvas { Width = 240, Height = 240 };
        for (var i = 0; i < group.KnownFractions.Length; i++)
        {
            var value = maximum > 0 ? group.KnownFractions[i] / maximum : 0; var cell = new Rectangle { Width = 20, Height = 20,
                Fill = new SolidColorBrush(Color.FromRgb((byte)(240 - 216 * value), (byte)(247 - 126 * value), (byte)(250 - 88 * value))) };
            Canvas.SetLeft(cell, i % 12 * 20); Canvas.SetTop(cell, i / 12 * 20); canvas.Children.Add(cell);
        }
        panel.Children.Add(canvas); panel.Children.Add(Text($"Unknown position mass {group.UnknownFraction:P1}; shared scale0–{maximum:P1} of planned exposure/cell.")); return panel;
    }
}
