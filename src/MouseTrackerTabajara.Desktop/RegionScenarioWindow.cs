using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Calibration;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Desktop;

public sealed class RegionScenarioWindow : Window
{
    private readonly string _database;
    private readonly List<NamedRegionScenario> _scenarios = [];
    private readonly CheckBox _blind = new() { Content = "Blind review" };
    private readonly TextBox _unblindReviewer = new() { Width = 120, ToolTip = "Unblinding reviewer" };
    private readonly TextBox _unblindReason = new() { Width = 180, ToolTip = "Reason for unblinding this view" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _recordings = new() { MinWidth = 180, Tag = "scenario-recording" };
    private readonly ListBox _named = new() { MinHeight = 70, Tag = "named-scenarios" };
    private readonly StackPanel _results = new() { Tag = "scenario-results" };
    private readonly TextBox _profile = new() { Width = 280, Tag = "profile-path", ToolTip = "Measured calibration profile file" };
    private readonly DispatcherTimer _integrity = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _compute;
    private int _generation;
    private bool _closed, _checking, _blindReady, _auditReveal, _refreshing;
    public AtlasSnapshot Snapshot { get; private set; }
    public RegionScenarioComputation? Result { get; private set; }
    public bool Fresh { get; private set; }
    public string StatusText => _status.Text;
    public string ResponsiveMark { get; set; } = "";
    public Task PendingComputation { get; private set; } = Task.CompletedTask;
    public Func<CancellationToken, Task>? ComputationGate { get; set; }
    public RegionScenarioSourceViewer? LastViewer { get; private set; }
    public bool Blinded { get => _blind.IsChecked == true; set => _blind.IsChecked = value; }
    public IReadOnlyList<NamedRegionScenario> Scenarios => _scenarios;
    public TextBox NameInput { get; } = Box("scenario-name");
    public TextBox RationaleInput { get; } = Box("scenario-rationale");
    public ComboBox KindInput { get; } = new() { MinWidth = 180, Tag = "scenario-kind" };
    public ComboBox UnitInput { get; } = new() { MinWidth = 120, Tag = "scenario-unit" };
    public ComboBox OrientationInput { get; } = new() { MinWidth = 140, Tag = "scenario-orientation" };
    public TextBox DeltaX { get; } = Box("delta-x", "0");
    public TextBox DeltaY { get; } = Box("delta-y", "0");
    public TextBox DeltaExtent { get; } = Box("delta-extent", "0");
    public TextBox DeltaRy { get; } = Box("delta-ry", "0");
    public TextBox RangeMin { get; } = Box("range-min");
    public TextBox RangeMax { get; } = Box("range-max");
    public TextBox RoomMin { get; } = Box("room-min", "0");
    public TextBox RoomMax { get; } = Box("room-max", "0");
    public TextBox LandmarkId { get; } = Box("landmark-id");
    public TextBox RoomDeltaX { get; } = Box("room-dx", "0");
    public TextBox RoomDeltaY { get; } = Box("room-dy", "0");
    public TextBox PoolCenterX { get; } = Box("plane-pool-x");
    public TextBox PoolCenterY { get; } = Box("plane-pool-y");
    public TextBox TargetX { get; } = Box("plane-target-x");
    public TextBox TargetY { get; } = Box("plane-target-y");
    public TextBox PlatformX { get; } = Box("plane-platform-x");
    public TextBox PlatformY { get; } = Box("plane-platform-y");
    public TextBox PlatformRadius { get; } = Box("plane-platform-radius");
    public StackPanel ResultsPanel => _results;
    public TextBox ProfilePath => _profile;

    public RegionScenarioWindow(string database, bool blinded)
    {
        _database = database; Snapshot = AtlasSnapshot.Read(database);
        Title = "Mouse Tracker Tabajara — Region and calibration scenarios";
        Width = 1440; Height = 960; MinWidth = 1050; MinHeight = 700; Background = System.Windows.Media.Brushes.WhiteSmoke;
        foreach (RegionScenarioKind kind in Enum.GetValues<RegionScenarioKind>()) KindInput.Items.Add(kind);
        KindInput.SelectedIndex = 0;
        UnitInput.Items.Add(DisclosedUnit.SourcePixels); UnitInput.Items.Add(DisclosedUnit.Normalized); UnitInput.SelectedIndex = 0;
        OrientationInput.Items.Add(StudyOrientation.Room); OrientationInput.Items.Add(StudyOrientation.TargetAligned); OrientationInput.SelectedIndex = 0;
        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Region and calibration scenarios", FontSize = 23, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = "Name each scenario and its measured rationale and range before calculation. Frozen-coordinate measurement is not native retracking.", TextWrapping = TextWrapping.Wrap });
        var commands = new WrapPanel();
        commands.Children.Add(_blind); commands.Children.Add(_unblindReviewer); commands.Children.Add(_unblindReason); commands.Children.Add(_recordings);
        commands.Children.Add(Button("Compute frozen scenarios", (_, _) => _ = ComputeAsync()));
        commands.Children.Add(Button("Cancel scenario computation", (_, _) => CancelComputation()));
        header.Children.Add(commands);
        var form = new WrapPanel();
        form.Children.Add(Labeled("Name", NameInput)); form.Children.Add(Labeled("Rationale", RationaleInput));
        form.Children.Add(Labeled("Kind", KindInput)); form.Children.Add(Labeled("Unit", UnitInput)); form.Children.Add(Labeled("Orientation", OrientationInput));
        form.Children.Add(Labeled("Delta X", DeltaX)); form.Children.Add(Labeled("Delta Y", DeltaY));
        form.Children.Add(Labeled("Extent or Rx", DeltaExtent)); form.Children.Add(Labeled("Ry", DeltaRy));
        form.Children.Add(Labeled("Range minimum", RangeMin)); form.Children.Add(Labeled("Range maximum", RangeMax));
        form.Children.Add(Labeled("Landmark", LandmarkId)); form.Children.Add(Labeled("Room delta X cm", RoomDeltaX)); form.Children.Add(Labeled("Room delta Y cm", RoomDeltaY));
        form.Children.Add(Labeled("Room range minimum", RoomMin)); form.Children.Add(Labeled("Room range maximum", RoomMax));
        form.Children.Add(Labeled("Pool center X cm", PoolCenterX)); form.Children.Add(Labeled("Pool center Y cm", PoolCenterY));
        form.Children.Add(Labeled("Target X cm", TargetX)); form.Children.Add(Labeled("Target Y cm", TargetY));
        form.Children.Add(Labeled("Platform X cm", PlatformX)); form.Children.Add(Labeled("Platform Y cm", PlatformY)); form.Children.Add(Labeled("Platform radius cm", PlatformRadius));
        form.Children.Add(Button("Add named scenario", (_, _) => AddScenario()));
        form.Children.Add(_profile); form.Children.Add(Button("Load measured profile", (_, _) => _ = LoadProfileAsync()));
        header.Children.Add(form); header.Children.Add(_named); header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = _results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        _blind.Checked += (_, _) => { _unblindReviewer.Clear(); _unblindReason.Clear(); if (LastViewer is not null) LastViewer.ApplyBlind(true); Refresh(); };
        _blind.Unchecked += (_, _) =>
        {
            if (!_blindReady || _auditReveal) { Refresh(); return; }
            var reviewer = _unblindReviewer.Text; var reason = _unblindReason.Text; _blind.IsChecked = true; TryUnblind(reviewer, reason);
        };
        _recordings.SelectionChanged += (_, _) => { if (Fresh) Refresh(); };
        _integrity.Tick += async (_, _) => { if (_closed || _checking || Result is null) return; _checking = true; try { await CheckFreshnessAsync(); } finally { _checking = false; } };
        _integrity.Start();
        Closed += (_, _) => { _closed = true; _generation++; _compute?.Cancel(); _integrity.Stop(); LastViewer?.Close(); };
        Blinded = blinded; _blindReady = true; ReloadRecordings(); Refresh();
    }

    public bool TryUnblind(string reviewer, string reason)
    {
        if (!Blinded) return true;
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason)) { _status.Text = "Reviewer and reason required. The view remains blind."; return false; }
        try
        {
            new StudyStore(_database).Audit("region_scenario_unblinded", new { Reviewer = reviewer.Trim(), Reason = reason.Trim(), SnapshotRevision = Snapshot.Revision });
            _auditReveal = true; _blind.IsChecked = false; if (LastViewer is not null) LastViewer.ApplyBlind(false); return true;
        }
        catch (Exception) { _status.Text = "Unblinding audit failed. The view remains blind."; return false; }
        finally { _auditReveal = false; }
    }

    public void CancelComputation()
    {
        _generation++; _compute?.Cancel();
        _status.Text = "Scenario computation canceled. Frozen baseline and source bytes were not changed. No scenario evidence was appended.";
    }

    public async Task CheckFreshnessAsync()
    {
        var result = Result; if (result is null || _closed) return;
        var path = _profile.Text.Trim(); var database = _database;
        try
        {
            var reason = await Task.Run(() =>
            {
                var profile = string.IsNullOrWhiteSpace(path) ? null : CalibrationProfileFile.Load(path);
                return result.Identity.Difference(RegionScenarioBaseline.Capture(AtlasSnapshot.Read(database), profile));
            });
            if (!_closed && ReferenceEquals(Result, result) && reason is not null) Hold(reason);
        }
        catch (Exception) { if (!_closed && ReferenceEquals(Result, result)) Hold("Region scenario integrity is unavailable. Results are held."); }
    }

    public Task ComputeAsync()
    {
        if (_scenarios.Count == 0) { _status.Text = "Name at least one scenario and its rationale before calculation."; return Task.CompletedTask; }
        var generation = ++_generation; _compute?.Cancel(); _compute = new CancellationTokenSource();
        var token = _compute.Token; Result = null; Fresh = false; _results.Children.Clear(); LastViewer?.Close(); LastViewer = null;
        var scenarios = _scenarios.ToImmutableArray(); var path = _profile.Text.Trim(); var database = _database; var gate = ComputationGate;
        _status.Text = "Computing frozen region scenarios away from the dispatcher.";
        PendingComputation = RunAsync(generation, token, scenarios, path, database, gate);
        return PendingComputation;
    }

    private async Task RunAsync(int generation, CancellationToken token, ImmutableArray<NamedRegionScenario> scenarios, string profilePath, string database, Func<CancellationToken, Task>? gate)
    {
        try
        {
            var computed = await Task.Run(() => Compute(scenarios, profilePath, database, gate, token), token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed || generation != _generation || token.IsCancellationRequested) return;
                if (!string.IsNullOrWhiteSpace(computed.StaleReason)) { Hold(computed.StaleReason); return; }
                Result = computed; Fresh = true; Snapshot = AtlasSnapshot.Read(_database); Refresh();
                _status.Text = "Frozen scenarios computed. Envelopes are not confidence intervals. Changing inputs requires another explicit computation.";
            });
        }
        catch (OperationCanceledException) { await Dispatcher.InvokeAsync(() => { if (!_closed && generation == _generation) CancelComputation(); }); }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => { if (!_closed && generation == _generation) Hold(ex.Message); }); }
    }

    private static RegionScenarioComputation Compute(ImmutableArray<NamedRegionScenario> scenarios, string profilePath, string database, Func<CancellationToken, Task>? gate, CancellationToken token)
    {
        gate?.Invoke(token).GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        var snapshot = AtlasSnapshot.Read(database);
        var profileText = string.IsNullOrWhiteSpace(profilePath) ? null : File.ReadAllText(profilePath);
        var profile = profileText is null ? null : CalibrationProfileFile.Load(profilePath);
        var identity = RegionScenarioBaseline.Capture(snapshot, profile);
        token.ThrowIfCancellationRequested();
        var recordings = ImmutableArray.CreateBuilder<RecordingScenarioComputation>();
        foreach (var row in snapshot.Recordings)
        {
            token.ThrowIfCancellationRequested();
            var source = identity.Sources.Single(item => item.VideoId == row.Asset.Id);
            if (!string.Equals(source.Sha256, row.Asset.SourceSha256, StringComparison.OrdinalIgnoreCase))
                return new RegionScenarioComputation { Identity = identity, Scenarios = scenarios, StaleReason = "Source bytes no longer match the original run. Explicit recomputation was refused and no scenario evidence was appended." };
            AnalysisRun? effective;
            try { effective = row.EffectiveRun(); }
            catch (Exception ex) { recordings.Add(new RecordingScenarioComputation { VideoId = row.Asset.Id, RecordingHolds = [ex.Message] }); continue; }
            if (effective is null) { recordings.Add(new RecordingScenarioComputation { VideoId = row.Asset.Id, RecordingHolds = ["No frozen run. The recording stays listed and no measurement was invented."] }); continue; }
            var header = RegionScenarioBaseline.Header(identity, source);
            var set = RegionScenarioMath.Evaluate(effective.Points, effective.Metadata, effective.Settings, scenarios, header,
                scenario => CalibrationScenarioMath.Evaluate(profile, scenario, effective.Points, effective.Metadata, row.Asset.SourceSha256));
            var comparisonWindow = ComparisonWindowFor(snapshot, row, effective.Metadata);
            var windows = ImmutableArray.CreateBuilder<WindowedTrialMeasurement>();
            windows.Add(RegionScenarioComparison.Measure(effective.Points, effective.Metadata, effective.Settings, comparisonWindow, RegionScenarioWording.OriginalName));
            foreach (var scenario in set.Scenarios)
            {
                if (scenario.Kind == RegionScenarioKind.CalibrationControlUncertainty)
                {
                    var named = scenarios.Single(item => item.Name.Trim() == scenario.Name);
                    var measured = new CalibrationMeasurementResult(scenario.Held, scenario.Holds, scenario.BaselineProfileId, scenario.BaselineProfileRevision,
                        scenario.ScenarioFitId, scenario.Endpoints, scenario.OutlierFrame, scenario.OutlierReason, scenario.Coordinates);
                    windows.Add(CalibrationScenarioMath.MeasureWindow(named, measured, comparisonWindow, effective.Metadata.Fps));
                }
                else if (!scenario.Held && scenario.MeasurementSettings is { } edited)
                    windows.Add(RegionScenarioComparison.Measure(effective.Points, effective.Metadata, edited, comparisonWindow, scenario.Name));
                else windows.Add(WindowedTrialMeasurement.Held(scenario.Name, scenario.Holds.FirstOrDefault() ?? "Scenario measurement is held."));
            }
            recordings.Add(new RecordingScenarioComputation { VideoId = row.Asset.Id, Set = set, Windows = windows.ToImmutable() });
        }
        token.ThrowIfCancellationRequested();
        var after = RegionScenarioBaseline.Capture(AtlasSnapshot.Read(database), string.IsNullOrWhiteSpace(profilePath) ? null : CalibrationProfileFile.Load(profilePath));
        var stale = identity.Difference(after);
        if (stale is not null) return new RegionScenarioComputation { Identity = identity, Scenarios = scenarios, StaleReason = stale + " No scenario evidence was appended." };
        var replay = RegionScenarioBaseline.Material(snapshot, profileText);
        var computation = new RegionScenarioComputation
        {
            Identity = identity, Scenarios = scenarios, Recordings = recordings.ToImmutable(),
            Collections = RegionScenarioCollectionsOrEmpty(snapshot, scenarios, recordings.ToImmutable()), Replay = replay
        };
        var json = JsonDefaults.Serialize(computation);
        var materialJson = JsonDefaults.Serialize(replay);
        var document = new RegionScenarioEvidenceDocument
        {
            Identity = identity, FrozenMaterialJson = materialJson,
            FrozenMaterialSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(materialJson))).ToLowerInvariant(),
            ComputationJson = json, ComputationSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()
        };
        var evidence = RegionScenarioEvidenceStore.Append(Path.GetDirectoryName(database)!, document);
        new StudyStore(database).Audit("region_scenario_evidence_appended", new { Evidence = Path.GetFileName(evidence), document.ComputationSha256, identity.SnapshotRevision, identity.ProfileId, identity.Implementation.CanonicalSha256 });
        return computation with { EvidencePath = evidence };
    }

    private static ReleasedComparisonWindow ComparisonWindowFor(AtlasSnapshot snapshot, AtlasRecording row, VideoMetadata metadata)
    {
        var plan = ExperimentPlanAdapter.FrozenPlan(snapshot)?.Plan;
        if (plan is null) return ReleasedComparisonWindow.Unsupported("No confirmed plan supplies a comparison window.");
        var slots = plan.Trials.Where(trial => string.Equals(trial.RecordingId, row.Asset.Id, StringComparison.Ordinal)).ToArray();
        if (slots.Length != 1)
            return ReleasedComparisonWindow.Unsupported(slots.Length == 0 ? "The recording has no planned trial slot." : "The recording is planned in more than one comparison window.");
        var context = plan.Contexts.SingleOrDefault(item => string.Equals(item.Id, slots[0].ContextId, StringComparison.OrdinalIgnoreCase));
        return ReleasedComparisonWindow.Resolve(ExperimentPlanAdapter.Protocol(snapshot, row.Asset.Id), context, metadata,
            ExperimentPlanAdapter.EffectiveEvents(snapshot, row.Asset.Id), row.Asset.Phase, ExperimentPlanAdapter.CurrentInspection(row));
    }

    private static ImmutableArray<ScenarioCollectionBlock> RegionScenarioCollectionsOrEmpty(AtlasSnapshot snapshot, IReadOnlyList<NamedRegionScenario> scenarios, IReadOnlyList<RecordingScenarioComputation> recordings)
    {
        try { return RegionScenarioBaseline.Collections(snapshot, scenarios, recordings); }
        catch (Exception ex) { return [new ScenarioCollectionBlock { ContextId = "plan", ScenarioName = RegionScenarioWording.OriginalName, Summary = new CollectionSummary { Emitted = false, Hold = "Collection rows are held. " + ex.Message } }]; }
    }

    private async Task LoadProfileAsync()
    {
        var path = _profile.Text.Trim();
        try
        {
            var profile = await Task.Run(() => CalibrationProfileFile.Load(path));
            Invalidate("Measured profile " + profile.Request.Revision.ToString(CultureInfo.InvariantCulture) + " loaded. Results are stale until you compute them again.");
        }
        catch (Exception ex) { _status.Text = Blinded ? BlindIdentity.PresentStatus(ex.Message, Secrets()) : ex.Message; }
    }

    private void AddScenario()
    {
        try
        {
            var scenario = ReadScenario(); scenario.Validate();
            if (_scenarios.Any(item => string.Equals(item.Name.Trim(), scenario.Name.Trim(), StringComparison.Ordinal))) throw new ArgumentException("Scenario names must be unique.");
            _scenarios.Add(scenario); _named.Items.Add(scenario.Name.Trim());
            Invalidate("Named scenario recorded with its rationale and range. It is not calculated until you compute the frozen set.");
        }
        catch (Exception ex) { _status.Text = Blinded ? BlindIdentity.PresentStatus(ex.Message, Secrets()) : ex.Message; }
    }

    private NamedRegionScenario ReadScenario()
    {
        var kind = (RegionScenarioKind)KindInput.SelectedItem;
        var shifts = ImmutableArray<ControlCoordinateShift>.Empty;
        if (kind == RegionScenarioKind.CalibrationControlUncertainty && !string.IsNullOrWhiteSpace(LandmarkId.Text))
            shifts = [new(LandmarkId.Text.Trim(), Required(DeltaX), Required(DeltaY), Required(RoomDeltaX), Required(RoomDeltaY))];
        return new NamedRegionScenario
        {
            Name = NameInput.Text, Rationale = RationaleInput.Text, Kind = kind,
            Unit = (DisclosedUnit)UnitInput.SelectedItem, Orientation = (StudyOrientation)OrientationInput.SelectedItem,
            CenterDeltaX = Required(DeltaX), CenterDeltaY = Required(DeltaY), ExtentDelta = Required(DeltaExtent), PoolRyDelta = Required(DeltaRy),
            ComponentRange = new((DisclosedUnit)UnitInput.SelectedItem, Required(RangeMin), Required(RangeMax)),
            RoomRange = new(DisclosedUnit.Centimeters, Required(RoomMin), Required(RoomMax)),
            ControlShifts = shifts,
            PoolCenterXcm = Optional(PoolCenterX), PoolCenterYcm = Optional(PoolCenterY), TargetXcm = Optional(TargetX), TargetYcm = Optional(TargetY),
            PlatformXcm = Optional(PlatformX), PlatformYcm = Optional(PlatformY), PlatformRadiusCm = Optional(PlatformRadius)
        };
    }

    private void OpenSource(SourceFrameRequest request)
    {
        if (Result is null || !Fresh) { _status.Text = "Compute the frozen set before opening a source frame."; return; }
        var recording = Snapshot.Recordings.SingleOrDefault(row => row.Asset.Id == request.VideoId);
        var computed = Result.Recordings.SingleOrDefault(row => row.VideoId == request.VideoId);
        var scenario = computed?.Set?.Scenarios.SingleOrDefault(item => item.Name == request.ScenarioName);
        if (recording is null || scenario is null || request.Frame is not int frame) { Hold("The linked frame is outside the frozen computation."); return; }
        var region = scenario.MeasurementSettings ?? computed!.Set!.BaselineSettings;
        LastViewer?.Close();
        LastViewer = new RegionScenarioSourceViewer(Snapshot, recording, region, frame, scenario.Name, Blinded) { Owner = this };
        LastViewer.Show();
    }

    private void ReloadRecordings()
    {
        var selected = (_recordings.SelectedItem as ComboBoxItem)?.Tag as string;
        _recordings.Items.Clear();
        foreach (var row in Snapshot.Recordings)
            _recordings.Items.Add(new ComboBoxItem { Content = Blinded ? BlindIdentity.RecordingAlias(row.Asset) : row.Asset.Filename, Tag = row.Asset.Id });
        _recordings.SelectedItem = _recordings.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selected)) ?? _recordings.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _profile.Visibility = Blinded ? Visibility.Collapsed : Visibility.Visible;
            ReloadRecordings();
            RegionScenarioPartial.Render(_results, Fresh ? Result : null, (_recordings.SelectedItem as ComboBoxItem)?.Tag as string, Blinded, Secrets(), OpenSource);
        }
        finally { _refreshing = false; }
    }

    private void Invalidate(string message) { Result = null; Fresh = false; _results.Children.Clear(); _status.Text = message; }
    private void Hold(string message) { Result = null; Fresh = false; _results.Children.Clear(); LastViewer?.Close(); _status.Text = Blinded ? BlindIdentity.PresentStatus(message, Secrets()) : message; }
    private BlindSecrets Secrets() => BlindSecrets.FromAssets(Snapshot.Recordings.Select(row => row.Asset));
    private static double Required(TextBox box)
    {
        if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new ArgumentException("Enter a finite documented number before calculation.");
        return value;
    }
    private static double? Optional(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : Required(box);
    private static TextBox Box(string tag, string text = "") => new() { Width = 90, Tag = tag, Text = text, ToolTip = tag };
    private static StackPanel Labeled(string label, Control control) { var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(control); return panel; }
    private static Button Button(string content, RoutedEventHandler click) { var button = new Button { Content = content, Margin = new Thickness(0, 8, 8, 8) }; button.Click += click; return button; }
}
