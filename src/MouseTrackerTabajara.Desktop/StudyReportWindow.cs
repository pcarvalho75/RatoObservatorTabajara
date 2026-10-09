using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Desktop;

public sealed class StudyReportWindow : Window
{
    private readonly string _database;
    private readonly bool _showSourceWindows;
    private readonly ListBox _contexts = new() { SelectionMode = SelectionMode.Multiple, MinWidth = 200, MaxHeight = 110 };
    private readonly CheckBox _blind = new() { Content = "Blind metadata" };
    private readonly TextBox _reviewer = new() { Width = 130, ToolTip = "Reviewer before revealing" };
    private readonly TextBox _reason = new() { Width = 210, ToolTip = "Reason before revealing" };
    private readonly TextBox _destination = new() { MinWidth = 270, ToolTip = "Local publication or historical package folder" };
    private readonly Border _destinationHost = new();
    private readonly TextBlock _status = Text("Select planned contexts and preview frozen evidence.");
    private readonly StackPanel _body = new();
    private readonly DispatcherTimer _integrity = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cancel;
    private int _generation;
    private int _disclosureGeneration;
    private readonly object _disclosureGate = new();
    private bool _publishing;
    private bool _initializing, _auditedReveal, _closed, _checking;
    private StudyReportReconstruction? _historicalInput;
    public AtlasSnapshot Snapshot { get; private set; }
    public StudyReportComputed? Computed { get; private set; }
    public StudyReportDocument? Historical { get; private set; }
    public StudyReportDocument? DisplayedDocument { get; private set; }
    public StudyAtlasWindow? LastSourceViewer { get; private set; }
    public ExperimentWindow? LastPlanEditor { get; private set; }
    public Task PendingCompute { get; private set; } = Task.CompletedTask;
    public Task PendingSave { get; private set; } = Task.CompletedTask;
    public Task PendingSource { get; private set; } = Task.CompletedTask;
    public bool Held { get; private set; }
    public bool Busy { get; private set; }
    public bool Blinded { get => _blind.IsChecked == true; set => _blind.IsChecked = value; }
    public string StatusText => _status.Text;
    public ListBox ContextsInput => _contexts;
    public TextBox DestinationInput => _destination;
    public TextBox ReviewerInput => _reviewer;
    public TextBox ReasonInput => _reason;
    private sealed record ContextChoice(string Id, string Display);

    public StudyReportWindow(string database, bool blinded, bool showSourceWindows = true)
    {
        _database = database; _showSourceWindows = showSourceWindows; Snapshot = AtlasSnapshot.Read(database);
        Title = "Mouse Tracker Tabajara — Whole-study report"; Width = 1440; Height = 960; MinWidth = 1050; MinHeight = 700;
        var root = new DockPanel { Margin = new Thickness(16), Background = Brushes.WhiteSmoke };
        var header = new StackPanel(); header.Children.Add(Text("Whole-study report — conditional evidence and every planned contributor", 24));
        header.Children.Add(Text("Separate training/probe/day contexts and held/missing/excluded slots. No automatic scientific sign-off. Opening an original source shows unredacted pixels that may reveal identity."));
        var commands = new WrapPanel(); commands.Children.Add(_blind); commands.Children.Add(Text("Reviewer / reason to reveal")); commands.Children.Add(_reviewer); commands.Children.Add(_reason);
        commands.Children.Add(Button("Select all contexts", (_, _) => _contexts.SelectAll())); commands.Children.Add(Button("Preview selected frozen study", (_, _) => Preview()));
        commands.Children.Add(Button("Reload planned contexts", (_, _) => Reload()));
        commands.Children.Add(Button("Create expected study plan", (_, _) => CreatePlan()));
        commands.Children.Add(Button("Cancel report operation", (_, _) => { _cancel?.Cancel(); Hold("Report operation cancelled; partial publication evidence retained."); }));
        header.Children.Add(commands); header.Children.Add(_contexts);
        var publication = new WrapPanel(); publication.Children.Add(_destinationHost); publication.Children.Add(Button("Choose publication folder", (_, _) => { var dialog = new Microsoft.Win32.OpenFolderDialog(); if (dialog.ShowDialog(this) == true) _destination.Text = dialog.FolderName; }));
        publication.Children.Add(Button("Publish frozen HTML CSV and input", (_, _) => Save())); publication.Children.Add(Button("Inspect saved historical package", (_, _) => InspectSaved()));
        publication.Children.Add(Button("Authenticate and replay frozen inputs", (_, _) => ReplayAuthenticated())); publication.Children.Add(Button("Recover partial publication", (_, _) => Recover()));
        header.Children.Add(publication); header.Children.Add(_status); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root;
        _destination.Text = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MouseTrackerTabajara", "study-report-" + Guid.NewGuid().ToString("N"));
        _initializing = true; Blinded = blinded; _initializing = false;
        UpdateDestinationPrivacy();
        _blind.Checked += (_, _) => { if (_initializing) return; lock (_disclosureGate) { _disclosureGeneration++; if (_publishing) _cancel?.Cancel(); } _reviewer.Clear(); _reason.Clear(); if (LastSourceViewer is not null) LastSourceViewer.Blinded = true; ReloadContextLabels(); Present(); };
        _blind.Unchecked += (_, _) => { if (_initializing || _auditedReveal) return; var reviewer = _reviewer.Text; var reason = _reason.Text; _blind.IsChecked = true; TryUnblind(reviewer, reason); };
        ReloadContextLabels(); _contexts.SelectAll();
        _integrity.Tick += async (_, _) =>
        {
            if (_closed || _checking || Busy || Held || Historical is not null || Computed is null) return; _checking = true; var generation = _generation; var computed = Computed;
            try { var current = await Task.Run(() => Current(computed, false, CancellationToken.None)); if (!current && generation == _generation && !_closed) Hold("Study/source/code changed; current report and source views held."); }
            catch (Exception) { if (!_closed && generation == _generation) Hold("Current study integrity unavailable."); }
            finally { _checking = false; }
        };
        _integrity.Start(); Closed += (_, _) => { _closed = true; _generation++; _cancel?.Cancel(); _integrity.Stop(); LastSourceViewer?.Close();LastPlanEditor?.Close(); _historicalInput?.Dispose(); };Present();
    }
    public bool TryUnblind(string reviewer, string reason)
    {
        if (!Blinded) return true;
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason)) { _status.Text = "Reviewer and reason required; report remains blind."; return false; }
        try { new StudyStore(_database).Audit("study_report_unblinded", new { Reviewer = reviewer.Trim(), Reason = reason.Trim(), Snapshot = Snapshot.Revision }); }
        catch (Exception) { _status.Text = "Unblinding audit failed; report remains blind."; return false; }
        _auditedReveal = true; try { _blind.IsChecked = false; } finally { _auditedReveal = false; }
        lock (_disclosureGate) { _disclosureGeneration++; if (_publishing) _cancel?.Cancel(); }
        ReloadContextLabels(); Present(); _status.Text = "Reasoned unblinding recorded before report metadata reveal."; return true;
    }
    private void ReloadContextLabels()
    {
        var selected = _contexts.SelectedItems.OfType<ContextChoice>().Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        _contexts.Items.Clear(); var plan = ExperimentPlanAdapter.FrozenPlan(Snapshot)?.Plan; if (plan is null) return;
        foreach (var c in plan.Contexts) { var item = new ContextChoice(c.Id, Blinded ? $"Context {_contexts.Items.Count + 1:000} · {c.Phase} · day {c.Day}" : $"{c.Id} · {c.Phase} · day {c.Day}"); _contexts.Items.Add(item); if (selected.Contains(c.Id)) _contexts.SelectedItems.Add(item); }
        _contexts.DisplayMemberPath = "Display";
    }
    public void Preview()
    {
        var ids = _contexts.SelectedItems.OfType<ContextChoice>().Select(c => c.Id).ToArray();
        var generation = Begin(); PendingCompute = ComputeAsync(generation, ids, _cancel!.Token);
    }
    public void Reload()
    {
        _cancel?.Cancel(); _generation++; Hold("Expected context list reloaded; preview frozen results explicitly.");
        try { Snapshot = AtlasSnapshot.Read(_database); ReloadContextLabels();Present(); } catch (Exception) { Hold("Expected context list is unavailable."); }
    }
    public void CreatePlan()
    {if(Busy)return;LastPlanEditor?.Close();LastPlanEditor=new ExperimentWindow(_database,Blinded,_showSourceWindows);LastPlanEditor.Closed+=(_,_)=>{if(!_closed)Reload();};if(_showSourceWindows){LastPlanEditor.Owner=this;LastPlanEditor.Show();}}
    private int Begin()
    {
        _cancel?.Cancel(); _cancel = new(); var generation = ++_generation; Busy = true; Held = true; Computed = null; Historical = null; DisplayedDocument = null; _body.Children.Clear();
        LastSourceViewer?.Close(); LastSourceViewer = null; _historicalInput?.Dispose(); _historicalInput = null; _status.Text = "Computing frozen conditional evidence; cancellation available."; return generation;
    }
    private async Task ComputeAsync(int generation, string[] contexts, CancellationToken token)
    {
        var entered = false;
        try
        {
            await _gate.WaitAsync(token); entered = true;
            var result = await Task.Run(() => { var snapshot = AtlasSnapshot.Read(_database); return StudyReportComputation.Compute(snapshot, contexts, token); }, token);
            if (generation != _generation || _closed || token.IsCancellationRequested) return;
            Computed = result; Snapshot = result.Snapshot; Held = false; Busy = false; Present(); _status.Text = "Whole-study conditional evidence ready; all planned slots retained. Human scientific sign-off remains unrun.";
        }
        catch (OperationCanceledException) { if (generation == _generation && !_closed) Hold("Report computation cancelled."); }
        catch (Exception ex) { if (generation == _generation && !_closed) Hold(Blinded ? "Report computation held; verify expected plan and frozen source evidence." : ex.Message); }
        finally { if (entered) _gate.Release(); if (generation == _generation) Busy = false; }
    }
    private void Present()
    {
        UpdateDestinationPrivacy();
        _body.Children.Clear(); DisplayedDocument = null;
        var raw = Historical ?? Computed?.Document; if (raw is null)
        {
            if(ExperimentPlanAdapter.FrozenPlan(Snapshot) is null){_status.Text="No expected plan yet. Choose Create expected study plan to assign animals/groups and windows, then Reload planned contexts and Preview.";_body.Children.Add(Text("Start the 30-video group report",22));_body.Children.Add(Text("Import recordings and record actual animal IDs and two or more groups. Files are not animal N. Create expected study plan: enter every animal and expected trial, phase/day/order and missing slots. Confirm local identities/allocation, reviewed tracking, release/window and source reference; choose normalized orientation. Save plan with reviewer/reason, Reload planned contexts, Select all contexts and Preview. Unknown inputs stay editable and held. Biological validation is deferred for collaborator testing."));}
            return;
        }
        var plan = _historicalInput is not null ? ExperimentPlanAdapter.FrozenPlan(_historicalInput.Snapshot)?.Plan : ExperimentPlanAdapter.FrozenPlan(Snapshot)?.Plan;
        // A saved unredacted historical package is never silently revealed from the Inspect command.
        if (Blinded && !raw.Redacted && plan is null) { _body.Children.Add(Text("Unredacted historical package requires its frozen plan before protected display.")); return; }
        var document = raw.Redacted || !Blinded ? raw : StudyReportComputation.Project(raw, plan!, true); DisplayedDocument = document;
        _body.Children.Add(Text(Historical is not null ? "HISTORICAL package — aggregate-only evidence inspection; current source eligibility is not implied." : "Current frozen whole-study preview", 22));
        _body.Children.Add(Text("Conditional descriptive evidence; all missing/held/excluded cases retained. Scientific validation is deferred for collaborator testing."));
        _body.Children.Add(new Expander{Header="Interpretation and historical replay limits",Content=Text(StudyReportPublication.BoundaryText(document.Boundaries)+" "+document.ReplayMode),Margin=new Thickness(4)});
        _body.Children.Add(Text($"Original source authentication: {document.SourceAuthentication.Count(s=>s.State=="authenticated")}/{document.SourceAuthentication.Length}. Missing/mismatched/unavailable originals remain held; publication completeness is separate from source authentication."));
        var scale = document.Contexts.SelectMany(c => c.Groups.SelectMany(g => g.Map)).DefaultIfEmpty(0).Max();
        for (var ci = 0; ci < document.Contexts.Length; ci++)
        {
            var c = document.Contexts[ci]; var original = raw.Contexts[ci];
            _body.Children.Add(Text($"{c.Id} · {c.Phase} · day {c.Day}; comparison offset {c.OffsetAfterRelease:0.###} s after release, duration {c.Duration:0.###} s; {c.Orientation}", 19));
            _body.Children.Add(new Expander{Header="Context interpretation, exposure and units",Content=Text(c.Interpretation+" "+c.TemporalInterpretation+" "+c.Units),Margin=new Thickness(4)});
            _body.Children.Add(ReportQuestionDashboard.Build(c,original,trial=>OpenSource(trial)));
            var maps = new WrapPanel();
            var cellTrace = new StackPanel();
            foreach (var g in c.Groups)
            {
                var panel = new StackPanel { Margin = new Thickness(8) }; panel.Children.Add(Text($"{g.Group}: N={g.IncludedAnimals.Length}, held={g.HeldAnimals.Length}; unknown={g.UnknownFraction:0.###}"));
                var map = new Canvas { Width = 240, Height = 240, Background = Brushes.White, Tag = "study-report-map" };
                for (var i = 0; i < g.Map.Length; i++)
                {
                    var selectedCell = i; var strength = scale > 0 ? Math.Clamp(g.Map[i] / scale, 0, 1) : 0; var cell = new Rectangle { Width = 20, Height = 20, Fill = new SolidColorBrush(Color.FromRgb((byte)(240 - strength * 210), (byte)(247 - strength * 125), 210)), ToolTip = $"Cell{i}; known fraction {g.Map[i]:0.####}; upper bound {Math.Min(1,g.Map[i]+g.UnknownFraction):0.####}; click to trace every contributing trial", Tag = i };
                    cell.MouseLeftButtonDown += (_, _) =>
                    {
                        cellTrace.Children.Clear(); cellTrace.Children.Add(Text($"{g.Group} cell{selectedCell}: every contributing animal/trial. Frame counts are navigation provenance, not group weights; first/last need not be continuous."));
                        foreach (var contribution in c.CellContributions.Where(p => p.Cell == selectedCell && g.IncludedAnimals.Contains(p.Animal)))
                        {
                            cellTrace.Children.Add(Text($"{contribution.Animal}/{contribution.Trial}; {contribution.AcceptedFrameCount} accepted frame rows; frames{contribution.FirstFrame}–{contribution.LastFrame}"));
                            if (!raw.Redacted) { var trial = original.Trials.Single(t => t.Id == original.CellContributions[c.CellContributions.IndexOf(contribution)].Trial); cellTrace.Children.Add(Button("Open exact cell contribution", (_, _) => OpenSource(trial, contribution.FirstFrame))); }
                        }
                        if (cellTrace.Children.Count == 1) cellTrace.Children.Add(Text("No known-position contributors in this allocation/cell; not evidence of an absent biological behavior."));
                    };
                    Canvas.SetLeft(cell, i % 12 * 20); Canvas.SetTop(cell, i / 12 * 20); map.Children.Add(cell);
                }
                panel.Children.Add(map); maps.Children.Add(panel);
            }
            _body.Children.Add(maps); _body.Children.Add(Text($"Map shared scale maximum {scale:0.####}. Click a map cell to trace every contributing animal/trial, including outliers.")); _body.Children.Add(cellTrace);
            foreach (var difference in c.Differences)
            {
                var cells = new StackPanel(); cells.Children.Add(Text(difference.Interpretation));
                for (var i = 0; i < difference.Cells.Length; i++) cells.Children.Add(Text($"Cell{i}: lower {difference.Cells[i].Lower:0.####}, upper {difference.Cells[i].Upper:0.####}"));
                _body.Children.Add(new Expander { Header = $"{difference.GroupA} minus {difference.GroupB}: conservative cellwise missing-position bounds", Content = cells, Margin = new Thickness(4) });
            }
            _body.Children.Add(Text("Whole-window scalar counts match the reported animal/day values and whole-trial endpoint support, independently of per-bin coverage and unrelated endpoint diagnostics. Every expected repeat must support an animal mean. A valid trial does not repair another missing/held repeat; per-bin coverage remains separately listed below."));
            foreach (var d in c.Denominators) _body.Children.Add(Text($"{d.Endpoint}: animals expected/included/held/excluded {d.ExpectedAnimals}/{d.IncludedAnimals}/{d.HeldAnimals}/{d.ExcludedAnimals}; trials {d.ExpectedTrials}/{d.IncludedTrials}/{d.HeldTrials}/{d.ExcludedTrials}"));
            var distributions = new Canvas { Width = 850, Height = 120, Background = Brushes.White, Tag = "study-report-distributions" };
            distributions.Children.Add(new Line { X1 = 20, X2 = 820, Y1 = 90, Y2 = 90, Stroke = Brushes.SlateGray });
            var distributionIndex = 0;
            foreach (var d in c.IndividualDays.Where(d => d.Occupancy.HasValue)) { var dot = new Ellipse { Width = 12, Height = 12, Fill = Brushes.SteelBlue, ToolTip = $"{d.Animal}; site occupancy fraction {d.Occupancy:0.####}; trials {string.Join(",",d.TrialIds)}" }; Canvas.SetLeft(dot, 20 + 800 * d.Occupancy!.Value - 6); Canvas.SetTop(dot, 20 + distributionIndex++ % 7 * 10); distributions.Children.Add(dot); }
            _body.Children.Add(Text("Individual site occupancy distributions · fraction0 to1; vertical jitter only for visibility (every animal listed below)")); _body.Children.Add(distributions);
            var temporalPlot = new Canvas { Width = 850, Height = 150, Background = Brushes.White, Tag = "study-report-temporal" };
            foreach (var b in c.Bins.Where(b => b.Occupancy.HasValue)) { var dot = new Ellipse { Width = 7, Height = 7, Fill = Brushes.DarkCyan, ToolTip = $"{b.Animal}/{b.Trial}; {b.StartAfterComparison:0.###}–{b.EndAfterComparison:0.###} s after comparison; {b.ComparisonOffset+b.StartAfterComparison:0.###}–{b.ComparisonOffset+b.EndAfterComparison:0.###} s after release; fraction {b.Occupancy:0.####}" }; Canvas.SetLeft(dot, 20 + 800 * b.StartAfterComparison / Math.Max(.001,c.Duration)); Canvas.SetTop(dot, 120 * (1-b.Occupancy!.Value)); temporalPlot.Children.Add(dot); }
            _body.Children.Add(Text($"Prespecified temporal site occupancy · y fraction0–1; x release-relative seconds {c.OffsetAfterRelease:0.###}–{c.OffsetAfterRelease+c.Duration:0.###}; comparison-relative0–{c.Duration:0.###}. Missing values not plotted as zero; cohorts not connected.")); _body.Children.Add(temporalPlot);
            for (var ti = 0; ti < c.Trials.Length; ti++)
            {
                var trial = c.Trials[ti]; var source = original.Trials[ti]; var block = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
                block.Children.Add(Text($"{trial.Animal} / {trial.Id}: {(trial.Excluded ? "excluded" : trial.VideoId is null ? "missing planned source" : trial.Holds.IsEmpty ? "conditional source" : "held map/source evidence")}; source frames {trial.FirstFrame}–{trial.LastFrame}"));
                block.Children.Add(Text($"Requested exposure {N(trial.RequestedExposureSeconds)} s; actual exposure {N(trial.ActualExposureSeconds)} s; {trial.TerminationReason}. Training endpoints retain each trial's termination."));
                if (!trial.Holds.IsEmpty || !trial.Notes.IsEmpty) block.Children.Add(Text(string.Join(" | ",trial.Holds.Concat(trial.Notes))));
                foreach (var day in c.IndividualDays.Where(d => d.Animal == trial.Animal)) block.Children.Add(Text($"Animal/context endpoints: occupancy {N(day.Occupancy)}, proximity {N(day.Proximity)} diameters, adjacent speed {N(day.Speed)} diameters/s; unknown fraction {day.UnknownFraction:0.###}. Expected repeats remain in means."));
                foreach (var visit in c.Visits.Where(v => v.Trial == trial.Id)) block.Children.Add(Text($"Site history {visit.History}; exact {N(visit.ExactSeconds)} s / censor {N(visit.CensorSeconds)} s after comparison; offset after release {c.OffsetAfterRelease:0.###} s."));
                if (source.VideoId is not null && !raw.Redacted) { var button = Button("Open authenticated planned source", (_, _) => OpenSource(source)); button.Tag = source.Id; block.Children.Add(button); }
                _body.Children.Add(block);
            }
        }
        _body.Children.Add(Text(document.ReplayMode)); _body.Children.Add(Text($"Unassigned sources retained: {document.UnassignedRecordingIds.Length}. Frozen revision {document.SnapshotRevision[..12]}; plan/code receipts and full histories are in the immutable package."));
    }
    private void UpdateDestinationPrivacy()
    {
        _destinationHost.Child = null;
        _destinationHost.Child = Blinded ? Text("Publication folder selected · path hidden while blind") : _destination;
    }
    private static string N(double? value) => value?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "held";
    public void Save()
    {
        var computed = Computed; if (computed is null || Held || Busy || Historical is not null) { _status.Text = "Preview current frozen evidence before publication; historical inspection does not overwrite a package."; return; }
        var document = StudyReportComputation.Project(computed.Document, ExperimentPlanAdapter.FrozenPlan(computed.Snapshot)!.Plan, Blinded); var folder = _destination.Text;
        _cancel?.Cancel(); _cancel = new(); var token = _cancel.Token; var generation = ++_generation; Busy = true; _status.Text = "Exclusive durable local publication in progress; partial evidence is retained if interrupted.";
        _publishing = true; PendingSave = SaveAsync(generation, Volatile.Read(ref _disclosureGeneration), computed, document, folder, token);
    }
    private async Task SaveAsync(int generation, int disclosure, StudyReportComputed computed, StudyReportDocument document, string folder, CancellationToken token)
    {
        try
        {
            await Task.Run(() =>
            {
                StudyReportPublication.Publish(folder, document, document.Redacted ? null : computed.Material, token, name => { if (disclosure != Volatile.Read(ref _disclosureGeneration)) throw new OperationCanceledException(token); if (!Current(computed, name == "manifest.json", token)) throw new InvalidDataException("Current evidence changed during publication."); }, commit => { lock (_disclosureGate) { if (disclosure != _disclosureGeneration) throw new OperationCanceledException(token); commit(); } }, beforePayload: () => { if (disclosure != Volatile.Read(ref _disclosureGeneration)) throw new OperationCanceledException(token); if (!Current(computed, true, token)) throw new InvalidDataException("Current report changed before publication."); });
            }); // Always run ownership/terminal-receipt handling, even if disclosure was cancelled before scheduling.
            if (generation == _generation && !_closed) _status.Text = "Completed durable local inventory package; per-source authentication may be partial. Scientific researcher sign-off remains unrun.";
        }
        catch (OperationCanceledException) { if (generation == _generation && !_closed) Hold("Publication cancelled; partial evidence retained."); }
        catch (Exception ex) { if (generation == _generation && !_closed) Hold(Blinded ? "Publication held or destination already owned; partial evidence retained. Choose a new destination." : ex.Message); }
        finally { if (generation == _generation) { Busy = false; _publishing = false; } }
    }
    private static bool Current(StudyReportComputed computed, bool authenticate, CancellationToken token)
    {
        if (!computed.Snapshot.IsCurrent() || computed.Material.CodeReceipt != StudyReportMaterial.CurrentCodeReceipt() || computed.Sources.Any(s => ExperimentSourceStamp.Read(s.Path) != s)) return false;
        if (authenticate && JsonDefaults.Serialize(StudyReportComputation.Authenticate(computed.Snapshot, token)) != JsonDefaults.Serialize(computed.Document.SourceAuthentication)) return false;
        return true;
    }
    public void InspectSaved()
    {
        if (Busy) return;
        try
        {
            var saved = StudyReportPublication.Read(_destination.Text); Begin(); Busy = false;
            if (saved.Material is not null) _historicalInput = saved.Material.Reconstruct();
            Historical = saved.Document; Held = false; Present(); _status.Text = "Historical aggregate-only package inspected. Current eligibility and original pixels are not implied.";
        }
        catch (Exception) { Hold("Historical package incomplete or altered; no complete report displayed."); }
    }
    public void ReplayAuthenticated()
    {
        if (Busy) return;
        var folder = _destination.Text; var generation = Begin(); PendingCompute = ReplayAsync(generation, folder, _cancel!.Token);
    }
    private async Task ReplayAsync(int generation, string folder, CancellationToken token)
    {
        StudyReportReconstruction? input = null; var entered = false;
        try
        {
            await _gate.WaitAsync(token); entered = true;
            var replay = await Task.Run(() =>
            {
                var saved = StudyReportPublication.Read(folder); if (saved.Material is null) throw new InvalidDataException("Redacted package has no authenticated replay identities.");
                if (saved.Material.CodeReceipt != StudyReportMaterial.CurrentCodeReceipt()) throw new InvalidDataException("Saved comparator code differs; aggregate-only historical inspection remains available.");
                input = saved.Material.Reconstruct();
                foreach (var source in input.Snapshot.Recordings)
                { token.ThrowIfCancellationRequested(); if (ExperimentPlanAdapter.HashSource(source.Asset.Path, token) != source.Asset.SourceSha256) throw new InvalidDataException("Original historical source bytes are unavailable or changed; authenticated reconstruction held."); }
                var computed = StudyReportComputation.Compute(input.Snapshot, saved.Document.Contexts.Select(c => c.Id), token);
                var comparison = computed.Document with { FrozenUtc = saved.Document.FrozenUtc };
                if (JsonDefaults.Serialize(comparison) != JsonDefaults.Serialize(saved.Document)) throw new InvalidDataException("Frozen numerical replay differs from saved report.");
                return saved;
            }, token);
            if (generation != _generation || _closed || token.IsCancellationRequested) return;
            _historicalInput = input; input = null; Historical = replay.Document; Held = false; Busy = false; Present(); _status.Text = "Authenticated historical reconstruction PASS: accepted adapters reproduced saved numeric output using frozen SQL, compatible code and verified original source bytes; live Latest was not consulted.";
        }
        catch (OperationCanceledException) { if (generation == _generation && !_closed) Hold("Historical replay cancelled."); }
        catch (Exception ex) { if (generation == _generation && !_closed) Hold(Blinded ? "Authenticated historical replay held; aggregate-only inspection may remain available." : ex.Message); }
        finally { input?.Dispose(); if (entered) _gate.Release(); if (generation == _generation) Busy = false; }
    }
    public void Recover() { try { _status.Text = StudyReportPublication.Recover(_destination.Text); } catch (Exception) { _status.Text = "Recovery held: active owner or invalid publication receipts. Partial files were not overwritten."; } }
    private void OpenSource(ReportTrial trial, int? requestedFrame = null)
    {
        if (Held || Busy || trial.VideoId is null) return;
        var snapshot = _historicalInput?.Snapshot ?? Computed?.Snapshot; if (snapshot is null) return;
        if (Historical is null && Computed is { } computed && !Current(computed, false, CancellationToken.None)) { Hold("Source tracing stale; reload report."); return; }
        var row = snapshot.Recordings.Single(r => r.Asset.Id == trial.VideoId); LastSourceViewer?.Close(); LastSourceViewer = new StudyAtlasWindow(snapshot.DatabasePath, Blinded, snapshot);
        if (_showSourceWindows) { LastSourceViewer.Owner = this; LastSourceViewer.Show(); }
        PendingSource = TraceSourceAsync(LastSourceViewer, snapshot, row, requestedFrame ?? trial.FirstFrame ?? row.RawRun?.Points.FirstOrDefault()?.Frame ?? 0);
    }
    private async Task TraceSourceAsync(StudyAtlasWindow viewer, AtlasSnapshot snapshot, AtlasRecording row, int frame)
    {
        await viewer.NavigateAsync(new() { RunId = row.NavigationId, SourceSha256 = row.Asset.SourceSha256, SnapshotRevision = snapshot.Revision, Frame = frame });
        if (viewer.Held && !_closed && ReferenceEquals(viewer, LastSourceViewer))
        { if (Historical is not null) _status.Text = "Historical aggregate remains inspectable; requested original source authentication is held."; else Hold("Original source authentication failed; current report held."); }
    }
    private void Hold(string message) { Held = true; Busy = false; Computed = null; Historical = null; DisplayedDocument = null; _body.Children.Clear(); LastSourceViewer?.Close(); LastSourceViewer = null; _status.Text = Blinded ? "Frozen report evidence held; preview/reload or inspect historical package." : message; }
    private static TextBlock Text(string text, double size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    private static Button Button(string text, RoutedEventHandler handler) { var b = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(6) }; b.Click += handler; return b; }
}
