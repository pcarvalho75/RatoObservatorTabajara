using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;

namespace MouseTrackerTabajara.Desktop;

public sealed class ValidationRecordingDraft
{
    public string VideoId { get; init; } = ""; public string Display { get; set; } = ""; public string Session { get; set; } = "";
    public ValidationSplit Split { get; set; } public bool UsedForTuning { get; set; } public bool IndependenceConfirmed { get; set; }
    public string ProtocolIdentity { get; set; } = ""; public string Allocation { get; set; } = "";
    public ValidationRecording Freeze() => new(VideoId, Session, Split, UsedForTuning, IndependenceConfirmed, ProtocolIdentity, Allocation);
}
public sealed record ValidationFrameChoice(string VideoId, int Frame, bool SecondRequired, string Display, string SamplingLabel);

public sealed class IndependentValidationWindow : Window
{
    private readonly string _database;
    private readonly ValidationHistoryStore _history;
    private readonly FrameDecoderPool _decoder = new(2);
    private readonly DispatcherTimer _integrity = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private readonly CheckBox _blind = new() { Content = "Blind metadata" }, _synthetic = new() { Content = "Synthetic tooling dataset only" }, _pixels = new() { Content = "I acknowledge original pixels may reveal identities/context" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap }, _clock = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _reviewer = new() { Width = 140 }, _reason = new() { Width = 220 };
    private readonly Dictionary<string, TextBox> _numbers = [];
    private readonly DataGrid _recordings = new() { AutoGenerateColumns = false, CanUserAddRows = false, Height = 190 };
    private readonly ListBox _samples = new() { Height = 150, DisplayMemberPath = "Display" };
    private readonly ComboBox _lane = new(), _visibility = new(), _event = new(), _measurementTarget = new();
    private readonly Grid _plane = new(); private readonly Image _image = new(); private readonly Canvas _referenceOverlay = new(); private readonly Border _viewport;
    private readonly ValidationComparisonSurface _resultsView = new(); private readonly TabItem _planTab = new() { Header = "Session split / tolerances" }, _comparisonTab = new() { Header = "Frozen comparison / failures" }, _annotationTab = new() { Header = "Independent reference annotation" };
    private bool _initializing = true, _auditedReveal, _closed, _checking;
    private int _frameGeneration, _compareGeneration;
    private bool _recomputedAbsentSummaries;
    private CancellationTokenSource? _compareCancel;
    private double? _x, _y;
    private bool _pixelDisclosureRecorded;
    private readonly Dictionary<string, (long Length, DateTime Written)> _sourceStamps = [];
    public IndependentValidationSnapshot Snapshot { get; private set; }
    public ObservableCollection<ValidationRecordingDraft> Draft { get; } = [];
    public ValidationComparisonReport? Report { get; private set; }
    public Task PendingFrame { get; private set; } = Task.CompletedTask;
    public Task PendingComparison { get; private set; } = Task.CompletedTask;
    public int? DisplayedFrame { get; private set; }
    public string? DisplayedVideoId { get; private set; }
    public BitmapSource? DisplayedBitmap => _image.Source as BitmapSource;
    public Canvas ReferenceOverlay => _referenceOverlay;
    public TextBox ReviewerInput => _reviewer; public TextBox ReasonInput => _reason;
    public ComboBox ReviewerLaneInput => _lane; public ComboBox VisibilityInput => _visibility; public ComboBox EventInput => _event;
    public ListBox SamplesInput => _samples; public CheckBox PixelDisclosureInput => _pixels; public CheckBox SyntheticInput => _synthetic;
    public DataGrid RecordingsInput => _recordings;
    public bool Blinded { get => _blind.IsChecked == true; set => _blind.IsChecked = value; }
    public bool Held { get; private set; }
    public string StatusText => _status.Text;
    public IndependentValidationWindow(string database, bool blinded)
    {
        _database = database; _history = new(database); Snapshot = IndependentValidationSnapshot.Read(database);
        Title = "Mouse Tracker Tabajara — Independent reference validation"; Width = 1440; Height = 960; MinWidth = 1050; MinHeight = 700;
        var root = new DockPanel { Margin = new Thickness(14), Background = Brushes.WhiteSmoke };
        var header = new StackPanel(); header.Children.Add(Text("Independent reference / validation tooling", 23));
        header.Children.Add(Text("Real held-out accuracy gate: UNRUN. One development recording is not held-out. Frames are not biological replicates; synthetic proof validates tooling only."));
        var commands = new WrapPanel(); commands.Children.Add(_blind); commands.Children.Add(Text("Reviewer / reason")); commands.Children.Add(_reviewer); commands.Children.Add(_reason);
        commands.Children.Add(Button("Reload frozen workspace", (_, _) => Reload())); commands.Children.Add(Button("Cancel comparison", (_, _) => { _compareCancel?.Cancel(); Hold("Comparison cancelled; no result published."); })); header.Children.Add(commands); header.Children.Add(_status); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var tabs = new TabControl(); _planTab.Content = PlanBody(); tabs.Items.Add(_planTab);
        var annotation = new Grid(); annotation.ColumnDefinitions.Add(new() { Width = new GridLength(310) }); annotation.ColumnDefinitions.Add(new());
        var controls = new StackPanel { Margin = new Thickness(6) }; controls.Children.Add(Text("Automatic positions, metrics and other independent reviewers' coordinates are hidden in reference mode."));
        controls.Children.Add(_samples); controls.Children.Add(Button("Open exact sampled source", (_, _) => OpenSelected())); controls.Children.Add(_pixels);
        _lane.ItemsSource = new[] { new Choice<ReferenceReviewerLane>(ReferenceReviewerLane.First, "First independent reviewer"), new(ReferenceReviewerLane.Second, "Second independent reviewer"), new(ReferenceReviewerLane.Consensus, "Resolve independent disagreement") }; _lane.DisplayMemberPath = "Label"; _lane.SelectedValuePath = "Value"; _lane.SelectedIndex = 0;
        _visibility.ItemsSource = new[] { new Choice<ReferenceVisibility>(ReferenceVisibility.Visible, "Visible body center"), new(ReferenceVisibility.NotVisible, "Body not visible"), new(ReferenceVisibility.Ambiguous, "Ambiguous / uncertain position") }; _visibility.DisplayMemberPath = "Label"; _visibility.SelectedValuePath = "Value"; _visibility.SelectedIndex = 0;
        _event.ItemsSource = new[] { new Choice<ReferenceEvent>(ReferenceEvent.None, "No biological event annotated"), new(ReferenceEvent.WaterEntry, "Water entry"), new(ReferenceEvent.PlatformMounting, "Platform mounting"), new(ReferenceEvent.ResearcherIntervention, "Researcher intervention"), new(ReferenceEvent.RecordingEnd, "Recording end") }; _event.DisplayMemberPath = "Label"; _event.SelectedValuePath = "Value"; _event.SelectedIndex = 0;
        controls.Children.Add(_lane); controls.Children.Add(_visibility); controls.Children.Add(_event); controls.Children.Add(Text("Click the body center on the exact displayed original frame, or record not visible/ambiguous.")); controls.Children.Add(Button("Save independent reference revision", (_, _) => SaveReference())); controls.Children.Add(_clock); annotation.Children.Add(new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _plane.Children.Add(_image); _plane.Children.Add(_referenceOverlay); _viewport = new Border { Background = Brushes.DarkSlateGray, Child = new Viewbox { Child = _plane, Stretch = Stretch.Uniform }, Margin = new Thickness(10) };
        _viewport.MouseLeftButtonDown += (_, e) => { var point = e.GetPosition(_viewport); SetReferenceFromViewport(point.X, point.Y); }; Grid.SetColumn(_viewport, 1); annotation.Children.Add(_viewport); _annotationTab.Content = annotation; tabs.Items.Add(_annotationTab);
        var comparison = new DockPanel(); var comparisonCommands = new WrapPanel(); comparisonCommands.Children.Add(Button("Compare frozen reference and producer", (_, _) => StartComparison())); comparisonCommands.Children.Add(Button("Save replayable comparison report", (_, _) => SaveReport())); comparisonCommands.Children.Add(Button("Replay last saved comparison", (_, _) => ReplayReport())); DockPanel.SetDock(comparisonCommands, Dock.Top); comparison.Children.Add(comparisonCommands); _resultsView.OpenSource = OpenResultFrame; comparison.Children.Add(_resultsView); _comparisonTab.Content = comparison; tabs.Items.Add(_comparisonTab); root.Children.Add(tabs); Content = root;
        _lane.SelectionChanged += (_, _) => { _x = _y = null; DrawReference(); }; _samples.SelectionChanged += (_, _) => { _x = _y = null; ClearFrame(); };
        _pixels.Checked += (_, _) => RecordPixelDisclosure(); _pixels.Unchecked += (_, _) => { _pixelDisclosureRecorded = false; ClearFrame(); };
        _blind.Checked += (_, _) => { if (!_initializing) Protect(); }; _blind.Unchecked += (_, _) => { if (_initializing || _auditedReveal) return; var reviewer = _reviewer.Text; var reason = _reason.Text; _blind.IsChecked = true; TryUnblind(reviewer, reason); };
        _blind.IsChecked = blinded; _initializing = false; Reload();
        _integrity.Tick += async (_, _) =>
        {
            if (_closed || _checking) return; _checking = true; var producer = Snapshot.ProducerFingerprint; var reference = Snapshot.ReferenceFingerprint;
            try { var now = await Task.Run(() => IndependentValidationSnapshot.Read(_database)); if (!_closed && Snapshot.ProducerFingerprint == producer && (now.ProducerFingerprint != producer || Report is not null && now.ReferenceFingerprint != reference || !SourcesUnchanged())) Hold("Producer/reference/source revision changed; frozen display held until reload."); }
            catch (Exception) { if (!_closed) Hold("Validation integrity unavailable; display held."); } finally { _checking = false; }
        };
        _integrity.Start(); Closed += (_, _) => { _closed = true; _frameGeneration++; _compareGeneration++; _compareCancel?.Cancel(); _integrity.Stop(); _decoder.Dispose(); };
    }
    private sealed record Choice<T>(T Value, string Label);
    private UIElement PlanBody()
    {
        var body = new StackPanel(); body.Children.Add(Text("Explicit recording/session split. Never infer independence from filenames or split frames of a development session."));
        foreach (var field in new[] { "Display", "Session", "Split", "UsedForTuning", "IndependenceConfirmed", "ProtocolIdentity", "Allocation" })
        {
            var binding = new Binding(field) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
            if (field == "Split") _recordings.Columns.Add(new DataGridComboBoxColumn { Header = "Declared split", ItemsSource = new[] { new Choice<ValidationSplit>(ValidationSplit.Unspecified, "Unspecified"), new(ValidationSplit.Development, "Development / tuning"), new(ValidationSplit.IndependentValidation, "Independent validation") }, DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValueBinding = binding });
            else if (field is "UsedForTuning" or "IndependenceConfirmed") _recordings.Columns.Add(new DataGridCheckBoxColumn { Header = field == "UsedForTuning" ? "Used for tuning" : "Independence confirmed", Binding = binding });
            else _recordings.Columns.Add(new DataGridTextColumn { Header = field == "Display" ? "Recording" : field, Binding = binding, IsReadOnly = field == "Display" });
        }
        _recordings.ItemsSource = Draft; body.Children.Add(_recordings); body.Children.Add(_synthetic);
        _measurementTarget.ItemsSource = new[] { new Choice<ValidationMeasurementTarget>(ValidationMeasurementTarget.RawAutomatic, "Raw automatic trajectory — no manual corrections"), new(ValidationMeasurementTarget.InspectedCorrected, "Inspected corrected output — includes manual repairs; not raw tracker accuracy") }; _measurementTarget.DisplayMemberPath = "Label"; _measurementTarget.SelectedValuePath = "Value"; _measurementTarget.SelectedIndex = 0; body.Children.Add(_measurementTarget);
        body.Children.Add(Text("Editable tooling examples below are NOT scientifically validated tolerances. Save prespecified values before comparison.")); var inputs = new WrapPanel();
        foreach (var (key, value) in new[] { ("Random frames", "10"), ("Difficult intervals", "2"), ("Interval frames", "10"), ("Seed", "2026"), ("Second review fraction", "0.2"), ("Position tolerance px", "5"), ("Reviewer disagreement px", "3"), ("Event tolerance s", "0.25"), ("Path tolerance diameters", "0.1"), ("Site occupancy tolerance", "0.05"), ("Minimum reference fraction", "0.8") })
        { var panel = new StackPanel { Margin = new Thickness(6) }; panel.Children.Add(Text(key)); var input = new TextBox { Text = value, Width = 160, Tag = key }; _numbers.Add(key, input); panel.Children.Add(input); inputs.Children.Add(panel); }
        body.Children.Add(inputs); body.Children.Add(Button("Save split and tolerance plan", (_, _) => SavePlan())); body.Children.Add(Button("Generate disclosed frozen samples", (_, _) => GenerateSamples())); return new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    public void Reload()
    {
        _compareCancel?.Cancel(); _compareGeneration++; _frameGeneration++; Report = null; _resultsView.Clear(); ClearFrame(); _decoder.Reset(); Snapshot = IndependentValidationSnapshot.Read(_database); Held = false;
        Draft.Clear(); var plan = Snapshot.Plan;
        if (plan is not null)
        {
            _synthetic.IsChecked = plan.SyntheticToolingOnly;
            _measurementTarget.SelectedValue = plan.MeasurementTarget;
            var values = new Dictionary<string, double> { ["Random frames"] = plan.RandomFramesPerRecording, ["Difficult intervals"] = plan.DifficultIntervalsPerRecording, ["Interval frames"] = plan.IntervalFrames, ["Seed"] = plan.Seed,
                ["Second review fraction"] = plan.SecondReviewFraction, ["Position tolerance px"] = plan.Tolerances.PositionPixels, ["Reviewer disagreement px"] = plan.Tolerances.DisagreementPixels, ["Event tolerance s"] = plan.Tolerances.EventSeconds, ["Path tolerance diameters"] = plan.Tolerances.PathDiameters, ["Site occupancy tolerance"] = plan.Tolerances.SiteOccupancyFraction, ["Minimum reference fraction"] = plan.Tolerances.MinimumReferenceFraction };
            foreach (var (key, value) in values) _numbers[key].Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        for (var i = 0; i < Snapshot.Imported.Length; i++)
        { var asset = Snapshot.Imported[i]; var declared = plan?.Recordings.FirstOrDefault(r => r.VideoId == asset.Id); Draft.Add(new() { VideoId = asset.Id, Display = Blinded ? $"Recording {i + 1:000}" : asset.Filename, Session = declared?.Session ?? "", Split = declared?.Split ?? ValidationSplit.Unspecified, UsedForTuning = declared?.UsedForTuning ?? false, IndependenceConfirmed = declared?.IndependenceConfirmed ?? false, ProtocolIdentity = declared?.ProtocolIdentity ?? "", Allocation = declared?.Allocation ?? "" }); }
        _samples.Items.Clear(); if (Snapshot.Sampling is { } sampling)
        {
            foreach (var key in sampling.Cases.SelectMany(c => Enumerable.Range(c.StartFrame, c.EndFrame - c.StartFrame + 1).Select(frame => (c.VideoId, Frame: frame))).Distinct())
            { var index = Array.FindIndex(Snapshot.Imported.ToArray(), a => a.Id == key.VideoId) + 1; var cases = sampling.Cases.Where(c => c.VideoId == key.VideoId && c.StartFrame <= key.Frame && c.EndFrame >= key.Frame).ToArray(); _samples.Items.Add(new ValidationFrameChoice(key.VideoId, key.Frame, cases.Any(c => c.SecondReviewRequired), (Blinded ? $"Recording {index:000}" : Snapshot.Imported.Single(a => a.Id == key.VideoId).Filename) + $" · source frame {key.Frame}" + (cases.Any(c => c.SecondReviewRequired) ? " · second review required" : ""), cases.Any(c => c.RandomSample) ?  $"Simple random sample; inclusion probability {cases.First(c => c.RandomSample).InclusionProbability:0.####}; source denominator {cases.First(c => c.RandomSample).PopulationFrames}; seed {sampling.Seed}" : $"Purposive continuous interval; probability undefined; source denominator {cases[0].PopulationFrames}; seed {sampling.Seed}")); }
        }
        if (Blinded) Protect(); _status.Text = "Independent references remain separate from corrections. Real held-out gate remains unrun.";
    }
    public void SavePlan()
    {
        if (Blinded) { Hold("Unblind with reviewer/reason before editing split/allocation identities."); return; }
        try
        {
            _recordings.CommitEdit(DataGridEditingUnit.Cell, true); _recordings.CommitEdit(DataGridEditingUnit.Row, true);
            int Integer(string key) => int.Parse(_numbers[key].Text, System.Globalization.CultureInfo.InvariantCulture); double Number(string key) => double.Parse(_numbers[key].Text, System.Globalization.CultureInfo.InvariantCulture);
            var plan = new IndependentValidationPlan { SyntheticToolingOnly = _synthetic.IsChecked == true, MeasurementTarget = (ValidationMeasurementTarget)_measurementTarget.SelectedValue, Seed = Integer("Seed"), RandomFramesPerRecording = Integer("Random frames"), DifficultIntervalsPerRecording = Integer("Difficult intervals"), IntervalFrames = Integer("Interval frames"), SecondReviewFraction = Number("Second review fraction"), Recordings = Draft.Select(r => r.Freeze()).ToImmutableArray(), Tolerances = new(Number("Position tolerance px"), Number("Reviewer disagreement px"), Number("Event tolerance s"), Number("Path tolerance diameters"), Number("Site occupancy tolerance"), Number("Minimum reference fraction")) };
            Snapshot.ValidatePlan(plan); var previous = Snapshot.History.LastOrDefault(h => h.Kind == "plan" && h.EntityId == plan.Id)?.Revision ?? 0;
            _history.Save("plan", plan.Id, JsonDefaults.Serialize(plan), _reviewer.Text, _reason.Text, previous); Reload();
        }
        catch (Exception ex) { _status.Text = Blinded ? "Plan save held." : ex.Message; }
    }
    public void GenerateSamples()
    {
        try { var plan = Snapshot.Plan ?? throw new InvalidOperationException("Save split/tolerances before sampling."); var sampling = IndependentValidationSampling.Generate(Snapshot, plan); _history.Save("sampling", plan.Id, JsonDefaults.Serialize(sampling), _reviewer.Text, _reason.Text, Snapshot.History.LastOrDefault(h => h.Kind == "sampling" && h.EntityId == plan.Id)?.Revision ?? 0); Reload(); }
        catch (Exception) { Hold("Sample generation held; save a valid prespecified plan and reviewer/reason."); }
    }
    private void RecordPixelDisclosure()
    {
        try { _history.Save("pixel_disclosure", Guid.NewGuid().ToString("N"), JsonDefaults.Serialize(new { OriginalPixelsCanRevealIdentity = true }), _reviewer.Text, _reason.Text, 0); _pixelDisclosureRecorded = true; }
        catch (Exception) { _pixels.IsChecked = false; _status.Text = "Pixel disclosure must be recorded with reviewer/reason before viewing source pixels."; }
    }
    public void OpenSelected() { if (_samples.SelectedItem is ValidationFrameChoice choice) PendingFrame = DisplayAsync(choice); }
    private async Task DisplayAsync(ValidationFrameChoice choice)
    {
        var generation = ++_frameGeneration; ClearFrame(); _x = _y = null;
        if (!_pixelDisclosureRecorded) { _status.Text = "Record pixel-disclosure acknowledgement before viewing original source."; return; }
        try
        {
            var source = Snapshot.Imported.Single(a => a.Id == choice.VideoId); var producer = Snapshot.ProducerFingerprint;
            var bytes = await Task.Run(() => { if (IndependentValidationSnapshot.Read(_database).ProducerFingerprint != producer) throw new InvalidDataException("Producer changed."); var pixels = _decoder.Read(source.Path, choice.Frame, expectedSha256: source.SourceSha256); if (IndependentValidationSnapshot.Read(_database).ProducerFingerprint != producer) throw new InvalidDataException("Producer changed during decode."); return pixels; });
            if (_closed || generation != _frameGeneration) return;
            using var stream = new MemoryStream(bytes); var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); _image.Source = bitmap;
            _plane.Width = source.Metadata.Width; _plane.Height = source.Metadata.Height; DisplayedFrame = choice.Frame; DisplayedVideoId = choice.VideoId;
            var file = new FileInfo(source.Path); _sourceStamps[source.Path] = (file.Length, file.LastWriteTimeUtc); _clock.Text = $"Exact source frame {choice.Frame} · {choice.Frame / source.Metadata.Fps:0.###} s. {choice.SamplingLabel}. Reference overlay only; automatic coordinates hidden."; Held = false; DrawReference();
        }
        catch (Exception) { if (!_closed && generation == _frameGeneration) Hold("Source/producer identity mismatch or decode failure; reference pixels held."); }
    }
    public void SetReferenceFromViewport(double x, double y)
    {
        if (DisplayedFrame is null || DisplayedVideoId is null || Held) return;
        var source = Snapshot.Imported.Single(a => a.Id == DisplayedVideoId); var mapped = DisplayToVideo.MapDips(x, y, _viewport.ActualWidth, _viewport.ActualHeight, source.Metadata.Width, source.Metadata.Height);
        if (mapped is null) { _status.Text = "Click is outside source pixels; nearby positions are not substituted."; return; } _x = mapped.Value.X; _y = mapped.Value.Y; _visibility.SelectedIndex = 0; DrawReference();
    }
    private void DrawReference()
    {
        _referenceOverlay.Children.Clear();
        if (DisplayedFrame is int frame && DisplayedVideoId is { } video && _lane.SelectedValue is ReferenceReviewerLane.Consensus && Snapshot.Sampling is { } sampling)
        {
            var sampleHash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(sampling));
            var rows = Snapshot.History.Where(h => h.Kind == "reference" && h.EntityId.StartsWith(sampleHash + ":", StringComparison.Ordinal)).GroupBy(h => h.EntityId).Select(g => g.OrderBy(h => h.Revision).Last())
                .Select(h => JsonDefaults.Deserialize<IndependentReference>(h.Json)).Where(r => r.VideoId == video && r.Frame == frame && r.Lane is ReferenceReviewerLane.First or ReferenceReviewerLane.Second);
            foreach (var reference in rows.Where(r => r.Visibility == ReferenceVisibility.Visible))
            {
                var independent = new Ellipse { Width = 10, Height = 10, Stroke = reference.Lane == ReferenceReviewerLane.First ? Brushes.DeepSkyBlue : Brushes.Orange, StrokeThickness = 2,
                    Tag = "independent-consensus-input", ToolTip = reference.Lane == ReferenceReviewerLane.First ? "First independent reference; automatic positions hidden" : "Second independent reference; automatic positions hidden" };
                Canvas.SetLeft(independent, reference.X!.Value - 5); Canvas.SetTop(independent, reference.Y!.Value - 5); _referenceOverlay.Children.Add(independent);
            }
        }
        if (_x is double x && _y is double y)
        { var marker = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.LimeGreen, StrokeThickness = 2, Tag = "independent-reference-marker" }; Canvas.SetLeft(marker, x - 6); Canvas.SetTop(marker, y - 6); _referenceOverlay.Children.Add(marker); }
    }
    public void SaveReference()
    {
        try
        {
            if (DisplayedFrame is not int frame || DisplayedVideoId is not { } video || Held || !SourcesUnchanged() || IndependentValidationSnapshot.Read(_database).ProducerFingerprint != Snapshot.ProducerFingerprint) throw new InvalidOperationException("Annotate an exact currently displayed source frame.");
            var sampling = Snapshot.Sampling ?? throw new InvalidOperationException("No frozen sample."); var hash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(sampling)); var lane = (ReferenceReviewerLane)_lane.SelectedValue; var visible = (ReferenceVisibility)_visibility.SelectedValue;
            var records = Snapshot.History.Where(h => h.Kind == "reference" && h.EntityId.StartsWith(hash + ":", StringComparison.Ordinal)).Select(h => (Entry: h, Ref: JsonDefaults.Deserialize<IndependentReference>(h.Json))).Where(r => r.Ref.VideoId == video && r.Ref.Frame == frame).ToArray();
            var consensusIds = lane == ReferenceReviewerLane.Consensus ? records.Where(r => r.Ref.Lane is ReferenceReviewerLane.First or ReferenceReviewerLane.Second).GroupBy(r => r.Ref.Lane).Select(g => g.OrderBy(r => r.Entry.Revision).Last().Entry.Id).ToImmutableArray() : ImmutableArray<long>.Empty;
            var reference = new IndependentReference(video, frame, lane, _reviewer.Text, visible, visible == ReferenceVisibility.Visible ? _x : null, visible == ReferenceVisibility.Visible ? _y : null, (ReferenceEvent)_event.SelectedValue, _reason.Text, consensusIds); reference.Validate(Snapshot.Imported.Single(a => a.Id == video).Metadata);
            var key = IndependentValidationComparison.ReferenceKey(hash, reference); _history.Save("reference", key, JsonDefaults.Serialize(reference), _reviewer.Text, _reason.Text, Snapshot.History.LastOrDefault(h => h.Kind == "reference" && h.EntityId == key)?.Revision ?? 0);
            Snapshot = IndependentValidationSnapshot.Read(_database); Report = null; _resultsView.Clear(); _status.Text = "Independent reference revision saved; tracker runs/corrections unchanged. Recompute comparison explicitly.";
        }
        catch (Exception) { _status.Text = "Independent annotation held; exact pixels, body/visibility, reviewer/reason and consensus provenance are required."; }
    }
    public void StartComparison(bool recomputedAbsentSummaries = false)
    {
        _recomputedAbsentSummaries = recomputedAbsentSummaries; _compareCancel?.Cancel(); var cancellation = new CancellationTokenSource(); _compareCancel = cancellation; var generation = ++_compareGeneration; Report = null; _resultsView.Clear(); _status.Text = recomputedAbsentSummaries ? ValidationSplitSummaries.AbsentSummaryHold + " Recomputing from the frozen plan, sample, and references." : "Comparing frozen independent references; previous report cleared."; PendingComparison = CompareAsync(generation, cancellation);
    }
    private async Task CompareAsync(int generation, CancellationTokenSource cancellation)
    {
        var acquired = false;
        try
        {
            await _workGate.WaitAsync(cancellation.Token); acquired = true; var snapshot = IndependentValidationSnapshot.Read(_database); var plan = snapshot.Plan ?? throw new InvalidOperationException("No plan."); var sampling = snapshot.Sampling ?? throw new InvalidOperationException("No frozen sampling.");
            var before = snapshot.Imported.Select(a => (a.Path, Stamp: ReadStamp(a.Path))).ToArray();
            var result = await Task.Run(() => IndependentValidationComparison.Compare(snapshot, plan, sampling, cancellation.Token), cancellation.Token);
            if (!snapshot.IsCurrent() || before.Any(s => ReadStamp(s.Path) != s.Stamp)) throw new InvalidDataException("Source/reference changed during comparison.");
            if (_closed || generation != _compareGeneration || cancellation.IsCancellationRequested) return; Snapshot = snapshot; Report = result; Held = false; ShowReport();
        }
        catch (OperationCanceledException) { if (!_closed && generation == _compareGeneration) Hold("Comparison cancelled; no result published."); }
        catch (Exception) { if (!_closed && generation == _compareGeneration) Hold("Comparison held; source/run/plan/reference evidence changed or remains incomplete."); }
        finally { if (acquired) _workGate.Release(); if (ReferenceEquals(_compareCancel, cancellation)) _compareCancel = null; cancellation.Dispose(); }
    }
    private void ShowReport()
    {
        if (Report is not { } report) { _resultsView.Clear(); return; }
        var recomputedAbsentSummaries = _recomputedAbsentSummaries; _recomputedAbsentSummaries = false;
        var imported = Snapshot.Imported.ToArray();
        var labels = report.Frames.Select(frame => frame.VideoId).Concat(report.Endpoints.Select(endpoint => endpoint.VideoId)).Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => { var index = Array.FindIndex(imported, asset => asset.Id == id) + 1; return Blinded ? $"Recording {index:000}" : imported.Single(asset => asset.Id == id).Filename; }, StringComparer.Ordinal);
        var notes = _samples.Items.OfType<ValidationFrameChoice>().GroupBy(choice => (choice.VideoId, choice.Frame)).ToDictionary(group => group.Key, group => group.First().SamplingLabel);
        var allocation = new List<string>();
        if (!Blinded && Snapshot.Plan is { } plan)
            foreach (var group in plan.Recordings.GroupBy(recording => recording.Allocation))
            { var ids = group.Select(recording => recording.VideoId).ToHashSet(StringComparer.Ordinal); var rows = report.Frames.Where(frame => ids.Contains(frame.VideoId)).ToArray(); allocation.Add($"Allocation audit {group.Key}: {rows.Length} sampled frames; {rows.Count(frame => !frame.Holds.IsEmpty)} held; {rows.Count(frame => frame.PositionPass == false)} position tolerance failures; {rows.Count(frame => frame.FalseAccepted == true)} false accepted; {rows.Count(frame => frame.Excluded)} marked excluded. Excluded cases are retained; no causal conclusion manufactured."); }
        _resultsView.Present(report, Blinded, recomputedAbsentSummaries, labels, allocation, notes);
        _status.Text = "Replayable comparison prepared; real held-out acceptance remains separately unrun.";
    }
    private void OpenResultFrame(ValidationFrameRow row)
    {
        var choice = _samples.Items.OfType<ValidationFrameChoice>().SingleOrDefault(candidate => candidate.VideoId == row.VideoId && candidate.Frame == row.Frame);
        if (choice is null) { _status.Text = "Sampled source frame is not in the frozen sample."; return; }
        _annotationTab.IsSelected = true;
        _samples.SelectedItem = choice; OpenSelected();
    }
    public void SaveReport()
    {
        try { if (Report is null || !Snapshot.IsCurrent()) throw new InvalidOperationException("Report stale/unavailable."); _history.Save("report", Guid.NewGuid().ToString("N"), JsonDefaults.Serialize(Report), _reviewer.Text, _reason.Text, 0); _status.Text = "Frozen comparison report saved with source/run/reference/code receipts; real held-out accuracy gate not closed."; }
        catch (Exception) { Hold("Report save held; compute current frozen evidence and record reviewer/reason."); }
    }
    public void ReplayReport()
    {
        try
        {
            Snapshot = IndependentValidationSnapshot.Read(_database); var saved = Snapshot.History.LastOrDefault(h => h.Kind == "report") ?? throw new InvalidOperationException("No saved comparison.");
            var report = JsonDefaults.Deserialize<ValidationComparisonReport>(saved.Json);
            if (report.ProducerFingerprint != Snapshot.ProducerFingerprint || report.ReferenceFingerprint != Snapshot.ReferenceFingerprint || Snapshot.Runs.Any(r => r.ComparatorBinarySha256 != report.ComparatorBinarySha256)) throw new InvalidDataException("Saved comparison evidence/code changed.");
            if (Snapshot.Plan is not { } plan || Snapshot.Sampling is not { } sampling) throw new InvalidDataException("Saved comparison plan/sample is unavailable.");
            // Recompute the frozen joins before publishing replay. A legacy report with no role summaries is not split out of its pooled mean.
            StartComparison(!ValidationSplitSummaries.TryRead(report, out _, out _));
        }
        catch (Exception) { Hold("Saved comparison is missing/stale; revalidation and explicit recomputation required."); }
    }
    private bool TryUnblind(string reviewer, string reason)
    {
        try { _history.Save("unblinding", Guid.NewGuid().ToString("N"), JsonDefaults.Serialize(new { RevealMetadata = true }), reviewer, reason, 0); _auditedReveal = true; _blind.IsChecked = false; RestorePlanVisibility(); Reload(); _reviewer.Text = reviewer; _reason.Text = reason; return true; }
        catch (Exception) { _status.Text = "Unblinding must be recorded first; labels remain protected."; return false; } finally { _auditedReveal = false; }
    }
    private void Protect()
    {
        _planTab.Content = Text("Metadata plan editing is hidden while blind; record reviewer/reason before revealing."); _reviewer.Clear(); _reason.Clear();
        for (var i = 0; i < Draft.Count; i++) Draft[i].Display = $"Recording {i + 1:000}"; _recordings.Items.Refresh(); if (Report is not null) ShowReport();
        var samples = _samples.Items.OfType<ValidationFrameChoice>().ToArray(); _samples.Items.Clear();
        foreach (var sample in samples)
        { var index = Array.FindIndex(Snapshot.Imported.ToArray(), a => a.Id == sample.VideoId) + 1; _samples.Items.Add(sample with { Display = $"Recording {index:000} · source frame {sample.Frame}" + (sample.SecondRequired ? " · second review required" : "") }); }
    }
    private void RestorePlanVisibility() { _planTab.Content = new ScrollViewer { Content = PlanBodyWithoutRebuild(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; }
    private UIElement PlanBodyWithoutRebuild()
    { if (LogicalTreeHelper.GetParent(_recordings) is Panel parent) parent.Children.Remove(_recordings); if (LogicalTreeHelper.GetParent(_synthetic) is Panel previous) previous.Children.Remove(_synthetic); if (LogicalTreeHelper.GetParent(_measurementTarget) is Panel oldTarget) oldTarget.Children.Remove(_measurementTarget); var body = new StackPanel(); body.Children.Add(_recordings); body.Children.Add(_synthetic); body.Children.Add(_measurementTarget); foreach (var input in _numbers.Values) if (LogicalTreeHelper.GetParent(input) is Panel holder) { holder.Children.Remove(input); body.Children.Add(Text((string)input.Tag)); body.Children.Add(input); } body.Children.Add(Button("Save split and tolerance plan", (_, _) => SavePlan())); body.Children.Add(Button("Generate disclosed frozen samples", (_, _) => GenerateSamples())); return body; }
    private void Hold(string message) { Held = true; _recomputedAbsentSummaries = false; _frameGeneration++; _compareGeneration++; _compareCancel?.Cancel(); ClearFrame(); Report = null; _resultsView.Clear(); _status.Text = Blinded ? "Frozen independent-validation/source evidence is held; reload required." : message; }
    private void ClearFrame() { _image.Source = null; _referenceOverlay.Children.Clear(); DisplayedFrame = null; DisplayedVideoId = null; _clock.Text = ""; }
    private static (bool Exists, long Length, DateTime Written) ReadStamp(string path) { var info = new FileInfo(path); return info.Exists ? (true, info.Length, info.LastWriteTimeUtc) : (false, 0, default); }
    private bool SourcesUnchanged() => _sourceStamps.All(s => ReadStamp(s.Key) == (true, s.Value.Length, s.Value.Written));
    private static Button Button(string caption, RoutedEventHandler handler) { var button = new Button { Content = caption, Margin = new Thickness(4), Padding = new Thickness(6) }; button.Click += handler; return button; }
    private static TextBlock Text(string value, double size = 12) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
}
