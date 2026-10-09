using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Storage;
using Microsoft.Win32;

namespace MouseTrackerTabajara.Desktop;

public sealed class StabilityWindow : Window, IDisposable
{
    private readonly StudyStore _store;
    private bool _blind, _disposed, _checking;
    private int _generation;
    private CancellationTokenSource? _cancel;
    private CancellationTokenSource? _publicationCancel;
    private bool _publishing;
    public string PublicationState { get; private set; } = "unrun";
    private readonly DispatcherTimer _freshness = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _cases = new();
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _population = new() { TextWrapping = TextWrapping.Wrap };
    public Expander PopulationDiagnostics { get; } = new() { Header = "Common population / all holds / frozen identities", IsExpanded = false };
    public TextBox EvidencePath { get; } = new() { MinWidth = 250 };
    public TextBox OutputPath { get; } = new() { MinWidth = 250 };
    public Button ChooseButton { get; } = new() { Content = "Choose frozen evidence" };
    public Button LoadButton { get; } = new() { Content = "Load and compare" };
    public Button CancelButton { get; } = new() { Content = "Cancel" };
    public Button ExportButton { get; } = new() { Content = "Save frozen report" };
    public Button ChooseOutputButton { get; } = new() { Content = "Choose new report folder" };
    public Button TraceButton { get; } = new() { Content = "Open selected exact source trace" };
    public Button RevealButton { get; } = new() { Content = "Reveal metadata (audited)" };
    public bool IsBlinded => _blind;
    public bool IsStale { get; private set; }
    public StabilityEvidenceSnapshot? Evidence { get; private set; }
    public string Status => _status.Text;
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    public StabilityTraceWindow? LastTrace { get; private set; }
    public Func<string, CancellationToken, StabilityEvidenceSnapshot> Loader { get; set; }
    public StabilityWindow(StudyStore store, bool blinded)
    {
        _store = store; _blind = blinded; Title = "Result stability — frozen evidence"; Width = 1150; Height = 800; MinWidth = 1050; MinHeight = 700;
        Loader = (path, token) =>
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.TryGetProperty("Trials", out _)) return StabilitySavedReplay.Read(path, token);
            if (json.RootElement.TryGetProperty("schema", out var schema) && schema.GetString() == SensitivityCatalog.FreezeSchema) return SensitivityStabilityAdapter.Read(path, store.DatabasePath, token);
            return json.RootElement.TryGetProperty("schema_version", out _) ? RegionStabilityAdapter.Read(path, store.DatabasePath, token) : DetectorStabilityAdapter.Read(path, token);
        };
        var root = new DockPanel { Margin = new(16) };
        var header = new StackPanel(); header.Children.Add(new TextBlock { Text = "Result stability", FontSize = 24 });
        header.Children.Add(new TextBlock { Text = StabilityReport.Limits, TextWrapping = TextWrapping.Wrap });
        var actions = new WrapPanel(); foreach (var button in new[] { ChooseButton, LoadButton, CancelButton, ChooseOutputButton, ExportButton, RevealButton }) { button.Margin = new(2); actions.Children.Add(button); } header.Children.Add(actions);
        header.Children.Add(new TextBlock { Text = "Frozen producer evidence" }); header.Children.Add(EvidencePath); header.Children.Add(new TextBlock { Text = "New empty report folder" }); header.Children.Add(OutputPath);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header); var footer = new ScrollViewer { Content = _status, MaxHeight = 85, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(330) }); grid.ColumnDefinitions.Add(new());
        var casePanel = new StackPanel(); casePanel.Children.Add(_details); casePanel.Children.Add(TraceButton); PopulationDiagnostics.Content = _population; casePanel.Children.Add(PopulationDiagnostics);
        grid.Children.Add(_cases); var details = new ScrollViewer { Content = casePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(12, 0, 0, 0) }; Grid.SetColumn(details, 1); grid.Children.Add(details); root.Children.Add(grid); Content = root;
        EvidencePath.TextChanged += (_, _) => { ++_generation; _cancel?.Cancel(); MarkStale("Evidence selection changed; frozen displayed report is stale."); };
        ChooseButton.Click += (_, _) => { var picker = new OpenFileDialog { Filter = "Frozen scenario JSON|*.json" }; if (picker.ShowDialog(this) == true) EvidencePath.Text = picker.FileName; };
        ChooseOutputButton.Click += (_, _) => { var picker = new OpenFolderDialog { Title = "Select an empty frozen report folder" }; if (picker.ShowDialog(this) == true) OutputPath.Text = picker.FolderName; };
        LoadButton.Click += (_, _) => PendingOperation = LoadAsync(); CancelButton.Click += (_, _) => { _cancel?.Cancel(); _publicationCancel?.Cancel(); ++_generation; MarkStale("Computation/publication cancellation requested; prior and partial evidence remains intact."); };
        ExportButton.Click += (_, _) => PendingOperation = SaveAsync();
        TraceButton.Click += (_, _) => OpenTrace(); _cases.SelectionChanged += (_, _) => RenderDetails();
        RevealButton.Click += (_, _) => RevealPrompt();
        _freshness.Tick += async (_, _) => await CheckFreshnessAsync(); Loaded += (_, _) => _freshness.Start(); Closed += (_, _) => Dispose();
        UpdateDisclosure();
    }
    public async Task LoadAsync()
    {
        _cancel?.Cancel(); _cancel?.Dispose(); _cancel = new(); var token = _cancel.Token; var generation = ++_generation; var path = EvidencePath.Text; var loader = Loader;
        _status.Text = "Validating frozen evidence and matched endpoint scope…";
        try
        {
            var evidence = await Task.Run(() => { var frozen = loader(path, token); if (!frozen.IsCurrent()) throw new InvalidDataException("Evidence changed during load."); return frozen; }, token); token.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation) return;
            Evidence = evidence; IsStale = false; Render(); _status.Text = $"All {evidence.Report.Trials.Length} cases retained. {evidence.Report.Contrasts.Length} matched contrasts. Sampling uncertainty not estimated.";
        }
        catch (OperationCanceledException) { if (!_disposed && generation == _generation) _status.Text = "Canceled; prior frozen evidence remains intact."; }
        catch (Exception) { if (!_disposed && generation == _generation) { MarkStale("Evidence held: unsupported schema or source/run/code/window integrity mismatch. No case was reclassified as completed."); } }
    }
    public async Task SaveAsync()
    {
        if (_publishing) { _status.Text = "This window already owns an active publication."; return; }
        if (Evidence is null || IsStale) { _status.Text = "Report save held; validated current evidence is required."; return; }
        var frozen = Evidence; var target = OutputPath.Text; var blind = _blind; var generation = _generation;
        _publishing = true; _publicationCancel = new(); var token = _publicationCancel.Token; PublicationState = "verifying";
        try
        {
            if (!await Task.Run(frozen.IsCurrent)) { PublicationState = "held"; MarkStale("Evidence changed before export; save held. Recompute explicitly."); return; }
            if (_disposed || generation != _generation || !ReferenceEquals(Evidence, frozen)) { PublicationState = "cancelled"; return; }
            token.ThrowIfCancellationRequested(); PublicationState = "publishing";
            await Task.Run(() => StabilityReportWriter.Save(target, frozen.Report, blind, token)); PublicationState = "completed";
            if (!_disposed && generation == _generation) _status.Text = "Frozen report saved; prior publications are never overwritten.";
        }
        catch (OperationCanceledException) { PublicationState = "cancelled"; if (!_disposed) _status.Text = "Publication cancelled; any ownership receipt and partial evidence remain. It is not a completed report."; }
        catch (Exception) { PublicationState = "failed"; if (!_disposed && generation == _generation) _status.Text = "Report publication failed; receipt/partial evidence remains. Select a new empty destination and current validated evidence."; }
        finally { _publishing = false; _publicationCancel?.Dispose(); _publicationCancel = null; }
    }
    public async Task CheckFreshnessAsync()
    {
        if (_disposed || _checking || Evidence is null || IsStale) return; _checking = true; var frozen = Evidence;
        try { if (!await Task.Run(frozen.IsCurrent) && !_disposed && ReferenceEquals(Evidence, frozen)) MarkStale("Frozen evidence/source material changed; displayed report is stale. Recompute explicitly."); }
        catch (Exception) { if (!_disposed && ReferenceEquals(Evidence, frozen)) MarkStale("Frozen evidence integrity unavailable; displayed report is stale."); }
        finally { _checking = false; }
    }
    private void MarkStale(string reason) { IsStale = true; _status.Text = reason; ExportButton.IsEnabled = false; TraceButton.IsEnabled = false; }
    private void Render()
    {
        _cases.Items.Clear(); if (Evidence is null) return;
        var report = StabilityReportWriter.Disclose(Evidence.Report, _blind);
        foreach (var trial in report.Trials) _cases.Items.Add(new ListBoxItem { Content = new TextBlock { Text = trial.CaseId + " · " + trial.Scenario + (trial.Holds.IsEmpty ? " · conditional" : " · held"), TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Black, Margin = new(3) }, Tag = Evidence.Report.Trials[_cases.Items.Count] });
        ExportButton.IsEnabled = !IsStale; TraceButton.IsEnabled = !IsStale; if (_cases.Items.Count > 0) _cases.SelectedIndex = 0;
    }
    private void RenderDetails()
    {
        if (Evidence is null) return; var index = _cases.SelectedIndex; var report = StabilityReportWriter.Disclose(Evidence.Report, _blind);
        _population.Text = string.Join("\n\n", report.Contrasts.Select(c => c.Scenario + ": " + c.Conclusion + $"\n{c.GroupA} minus {c.GroupB}. Included {c.IncludedAnimals}/{c.PlannedAnimals} equal-weight animals; held {c.HeldAnimals}; excluded {c.ExcludedAnimals}. Original {Value(c.Original)}; scenario {Value(c.Changed)} {c.Scope.Unit}.\n" + string.Join("\n", c.Animals.Select(a => $"{a.AnimalId}: {a.IncludedTrials}/{a.PlannedTrials} trials; original {Value(a.Original)}, scenario {Value(a.Changed)}; " + string.Join("; ", a.Holds)))));
        if (index >= 0 && index < report.Trials.Length)
        {
            var t = report.Trials[index]; _details.Text = $"Selected {t.CaseId}: {t.AnimalId} / {t.TrialId}\n{t.Scenario}: {t.Mode}\nOriginal {Value(t.Original)}; scenario {Value(t.Changed)} {t.Scope.Unit}.\n{t.Scope.Endpoint}; {t.Scope.Phase}, day {t.Scope.Day}; {t.Scope.Window}; {t.Scope.Orientation}.\nDenominator: {t.AnalyzedFrames?.ToString() ?? "withheld"} analyzed frames; {t.PlannedSeconds?.ToString("0.####") ?? "withheld"} seconds; unknown {t.UnknownSeconds?.ToString("0.####") ?? "withheld"} seconds.\nDecisive hold: {t.Holds.FirstOrDefault() ?? "None; conditional endpoint supported."}\nSampling uncertainty not estimated; bounds are conditional on accepted coordinates.";
            TraceButton.IsEnabled = !IsStale && Evidence.Report.Trials[index].Trace is not null;
            _details.Text += "\nContributor/region change: " + (t.ContributionNote.Length == 0 ? "No separate producer contributor diagnostic supplied." : t.ContributionNote);
            if (!t.NumericRanges.IsEmpty) _details.Text += "\nPrespecified ranges: " + StabilityReportWriter.RangeText(t);
            _population.Text += "\n\nSelected case declared range: " + t.DeclaredRange + "\nAll selected case holds: " + string.Join("; ", t.Holds);
            if (!_blind) _population.Text += $"\nFrozen source {t.BaselineIdentity.Source}; run {t.BaselineIdentity.Run}; correction {t.BaselineIdentity.Correction}; plan {t.BaselineIdentity.Plan}; protocol {t.BaselineIdentity.Window}; code {t.BaselineIdentity.Code}.\nTrace: {t.Trace?.SourcePath ?? "held"}; frames {t.Trace?.FirstFrame}–{t.Trace?.LastFrame}; outlier {t.Trace?.OutlierFrame}.";
        }
    }
    private static string Value(StabilityValue value) => value.Value is double v ? value.Lower.HasValue && value.Upper.HasValue ? $"{v:0.####} [{value.Lower:0.####}, {value.Upper:0.####}]" : $"{v:0.####} (missing-position bounds withheld/not applicable)" : "withheld";
    public void SelectCase(int index) { _cases.SelectedIndex = index; if (index >= 0 && index < _cases.Items.Count) _cases.ScrollIntoView(_cases.SelectedItem); }
    public void OpenTrace()
    {
        if (IsStale || _cases.SelectedItem is not ListBoxItem { Tag: StabilityTrial trial }) return;
        try { LastTrace?.Close(); LastTrace?.Dispose(); LastTrace = new StabilityTraceWindow(trial, Evidence?.Report.Inputs) { Owner = this }; LastTrace.Show(); }
        catch (Exception) { _status.Text = "Frozen source trace held; check authenticated source/run evidence."; }
    }
    public bool TryRevealMetadata(string reviewer, string reason)
    {
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason)) return false;
        try { _store.Audit("stability_metadata_revealed", new { Reviewer = reviewer.Trim(), Reason = reason.Trim() }); }
        catch (Exception) { _status.Text = "Metadata reveal held; successful audit is required."; return false; }
        _blind = false; UpdateDisclosure(); Render(); return true;
    }
    private void UpdateDisclosure() { EvidencePath.Visibility = OutputPath.Visibility = _blind ? Visibility.Collapsed : Visibility.Visible; RevealButton.Visibility = _blind ? Visibility.Visible : Visibility.Collapsed; }
    private void RevealPrompt()
    {
        var prompt = new Window { Title = "Audited metadata reveal", Owner = this, Width = 430, Height = 260 }; var root = new StackPanel { Margin = new(16) }; var reviewer = new TextBox(); var reason = new TextBox();
        root.Children.Add(new TextBlock { Text = "Reviewer" }); root.Children.Add(reviewer); root.Children.Add(new TextBlock { Text = "Reason" }); root.Children.Add(reason);
        var accept = new Button { Content = "Audit and reveal" }; accept.Click += (_, _) => { if (TryRevealMetadata(reviewer.Text, reason.Text)) prompt.Close(); }; root.Children.Add(accept);
        var cancel = new Button { Content = "Cancel; remain blind" }; cancel.Click += (_, _) => prompt.Close(); root.Children.Add(cancel); prompt.Content = root; prompt.ShowDialog();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; ++_generation; _cancel?.Cancel(); _publicationCancel?.Cancel(); _freshness.Stop(); LastTrace?.Close(); LastTrace?.Dispose(); }
}
