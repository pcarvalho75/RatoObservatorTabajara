using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;

namespace MouseTrackerTabajara.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private readonly SemaphoreSlim _frameLock = new(1, 1);
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly Stopwatch _playClock = new();
    private CancellationTokenSource? _batchCancel;
    private bool _ready, _loading;
    private int _frameGeneration, _playStart;
    private ShownFrame? _shown;
    private Func<Task>? _decodeGate;
    private readonly FrameDecoderPool _viewerDecoder = new(2);
    private string? _editMode;
    private System.Windows.Point? _poolCorner;
    private AnalysisRun? _current;
    private readonly Dictionary<string, ReleaseAnchorDraft> _releaseAnchors = new(StringComparer.Ordinal);
    private bool _suppressBlind;
    private ReviewMoment? _navigationMoment;
    private DisplayedInspection? _displayedInspection;
    private sealed record ReleaseAnchorDraft(int? Frame, bool Pending);
    private sealed record DisplayedInspection(string VideoId, string SourceSha256, int Frame, int Generation, InspectionMaterial Material);
    public MainWindow(MainViewModel model)
    {
        _model = model; InitializeComponent(); DataContext = model;
        InitializeProjectLifecycle();InitializeUpdates();
        TrialGrid.LoadingRow += (_, args) =>
        {
            if (args.Row.Item is TrialRow row) AutomationProperties.SetName(args.Row, row.DisplayName);
        };
        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.Busy) or nameof(MainViewModel.NotBusy)) UpdateAnnotationGates();
        };
        ReleaseConfirmedInput.Checked += (_, _) => UpdateAnnotationGates();
        ReleaseConfirmedInput.Unchecked += (_, _) => UpdateAnnotationGates();
        UpdateAnnotationGates();
        _playTimer.Tick += (_, _) =>
        {
            if (_model.Selected is null) return;
            var frame = _playStart + (int)Math.Floor(_playClock.Elapsed.TotalSeconds * _model.Selected.Asset.Metadata.Fps);
            FrameSlider.Value = Math.Min(FrameSlider.Maximum, frame);
            if (frame >= FrameSlider.Maximum) StopPlayback();
        };
        Closed += (_, _) => { StopPlayback(); _batchCancel?.Cancel(); _frameGeneration++; _shown = null; _viewerDecoder.Dispose(); };
    }

    public async Task InitializeAsync(string? video = null, string? analysis = null)
    {
        if (video is not null && File.Exists(video))
        {
            var asset = await Task.Run(() => _model.Workspace.Import(video));
            if (analysis is not null && File.Exists(analysis) && _model.Workspace.Store.LatestRun(asset.Id) is null)
            {
                var raw = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(analysis));
                if (raw.SourceSha256 != asset.SourceSha256) throw new InvalidDataException("Sample analysis does not belong to the selected video.");
                asset = asset with { Settings = raw.Settings }; _model.Workspace.Store.SaveVideo(asset);
                _model.Workspace.Store.SaveRun(raw with { VideoId = asset.Id });
            }
        }
        _model.Reload(); _ready = true; RefreshRoadmapScore(); await ShowSelectedAsync();
        await WaitForSourceReadinessAsync();
    }

    private async Task ShowSelectedAsync()
    {
        if (!_ready || _model.Selected is not { } row) return;
        StopPlayback();
        _shown = null;
        _displayedInspection = null;
        _navigationMoment = null;
        _frameGeneration++;
        UpdateAnnotationGates();
        _loading = true;
        try
        {
            _current = _model.Workspace.EffectiveRun(row.Asset.Id); row.Run = _current;
            FramePlane.Width = row.Asset.Metadata.Width; FramePlane.Height = row.Asset.Metadata.Height;
            FrameSlider.Maximum = row.Asset.Metadata.FrameCount - 1; FrameSlider.Value = Math.Min(150, FrameSlider.Maximum);
            FillSettings(row.Asset.Settings); RefreshReviewQueue(); RefreshAnnotations(); UpdateReadouts();
        }
        finally { _loading = false; }
        await DisplayFrameAsync();
    }

    private Task _pendingSourceOperation = Task.CompletedTask;
    /// <summary>Drains binding changes and the latest requested source load, including superseding selection/slider events.</summary>
    public async Task WaitForSourceReadinessAsync()
    {
        while (true)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var operation = _pendingSourceOperation; var generation = _frameGeneration;
            await operation;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (ReferenceEquals(operation, _pendingSourceOperation) && generation == _frameGeneration) return;
        }
    }
    private Task DisplayFrameAsync()
    {
        if (!_ready || _loading || _model.Selected is null) return Task.CompletedTask;
        return _pendingSourceOperation = DisplayFrameCoreAsync();
    }
    private async Task DisplayFrameCoreAsync()
    {
        if (!_ready || _loading || _model.Selected is not { } selected) return;
        var generation = ++_frameGeneration; var frame = (int)FrameSlider.Value;
        var asset = selected.Asset; var binary = BinaryView.IsChecked == true; var geometry = OverlaySettings();
        _shown = null;
        _displayedInspection = null;
        UpdateAnnotationGates();
        await _frameLock.WaitAsync();
        try
        {
            if (generation != _frameGeneration) return;
            if (_decodeGate is { } gate)
            {
                _decodeGate = null;
                await gate();
                if (generation != _frameGeneration) return;
            }
            var bytes = await Task.Run(() => _viewerDecoder.Read(asset.Path, frame, binary ? geometry : null, asset.SourceSha256));
            if (generation != _frameGeneration) return;
            if (_model.Selected is not { } current || current.Asset.Id != asset.Id || current.Asset.SourceSha256 != asset.SourceSha256) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            FrameImage.Source = bitmap;
            FrameClock.Text = $"Frame {frame}  ·  {frame / asset.Metadata.Fps:F2} / {asset.Metadata.DurationSeconds:F2} s";
            _shown = new ShownFrame(asset.Id, asset.SourceSha256, frame, generation);
            if (_current is { } run && run.VideoId == asset.Id && string.Equals(run.SourceSha256, asset.SourceSha256, StringComparison.OrdinalIgnoreCase))
                _displayedInspection = new DisplayedInspection(asset.Id, asset.SourceSha256, frame, generation, _model.Workspace.Store.InspectionMaterialFor(run.Id));
            DrawOverlay();
            UpdateAnnotationGates();
        }
        catch (Exception)
        {
            FrameImage.Source = null; FrameOverlay.Children.Clear(); _shown = null; _displayedInspection = null;
            FrameClock.Text = "Original source unavailable; navigation held.";
            _model.Status = "Saved processed data retained. Original source navigation/authentication is held until the source is available or relinked by importing identical bytes.";
            UpdateAnnotationGates();
        }
        finally { _frameLock.Release(); }
    }

    private void DrawOverlay()
    {
        if (!_ready || _model.Selected is not { } row) return;
        FrameOverlay.Children.Clear(); var s = OverlaySettings(); var m = row.Asset.Metadata; var contract = CurrentContract();
        var pool = new Ellipse { Width = 2 * s.Rx * m.Width, Height = 2 * s.Ry * m.Height, Stroke = Brushes.LightSkyBlue, StrokeThickness = 4 };
        Canvas.SetLeft(pool, (s.Cx - s.Rx) * m.Width); Canvas.SetTop(pool, (s.Cy - s.Ry) * m.Height); FrameOverlay.Children.Add(pool);
        if (s.Platform is { } site)
        {
            var ellipse = ArenaGeometry.SourceEllipse(site, s, m.Width, m.Height);
            var shape = new Ellipse { Width = ellipse.Width, Height = ellipse.Height, Stroke = Brushes.PeachPuff, Fill = new SolidColorBrush(Color.FromArgb(30, 255, 180, 137)), StrokeThickness = 4 };
            Canvas.SetLeft(shape, ellipse.Left); Canvas.SetTop(shape, ellipse.Top); FrameOverlay.Children.Add(shape);
        }
        if (_current is null || !contract.ShowAnalyzedTrack) return;
        var frame = (int)FrameSlider.Value;
        var geometry = new StreamGeometry(); TrackPoint? previous = null;
        using (var context = geometry.Open())
        {
            foreach (var p in _current.Points)
            {
                if (FullTrajectory.IsChecked != true && (p.Frame > frame || p.Frame < frame - m.Fps * 3)) continue;
                if (!p.Accepted) { previous = null; continue; }
                var location = new System.Windows.Point(p.X!.Value, p.Y!.Value);
                if (previous is null || p.Frame != previous.Frame + 1) context.BeginFigure(location, false, false);
                else context.LineTo(location, true, false);
                previous = p;
            }
        }
        geometry.Freeze(); FrameOverlay.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Stroke = new SolidColorBrush(Color.FromRgb(255, 153, 90)), StrokeThickness = 4 });
        var active = _current.Points.FirstOrDefault(p => p.Frame == frame);
        if (active is { Accepted: true })
        {
            var marker = new Ellipse { Width = 36, Height = 36, Stroke = Brushes.LightGreen, StrokeThickness = 5 };
            Canvas.SetLeft(marker, active.X!.Value - 18); Canvas.SetTop(marker, active.Y!.Value - 18); FrameOverlay.Children.Add(marker);
        }
    }

    private void FillSettings(TrackingSettings s)
    {
        ThresholdInput.Text = s.Threshold.ToString(CultureInfo.InvariantCulture); BlurInput.Text = s.Blur.ToString(CultureInfo.InvariantCulture);
        MinAreaInput.Text = s.MinArea.ToString(CultureInfo.InvariantCulture); MaxAreaInput.Text = s.MaxArea.ToString(CultureInfo.InvariantCulture);
        MaxJumpInput.Text = s.MaxJump.ToString(CultureInfo.InvariantCulture); DiameterInput.Text = s.DiameterCm?.ToString(CultureInfo.InvariantCulture) ?? "";
        StartInput.Text = s.StartSeconds.ToString(CultureInfo.InvariantCulture); EndInput.Text = s.EndSeconds?.ToString(CultureInfo.InvariantCulture) ?? "";
        InvertInput.IsChecked = s.Invert; ArenaConfirmedInput.IsChecked = s.ArenaConfirmed;
        SiteDiameterInput.Text = (s.Platform is { } p && _model.Selected is { } row ? p.Radius * row.Asset.Metadata.Width * 2 : 80).ToString(CultureInfo.InvariantCulture);
    }
    private TrackingSettings ReadSettings()
    {
        var prior = _model.Selected?.Asset.Settings ?? throw new InvalidOperationException("Select a recording.");
        static double Read(TextBox box) => double.Parse(box.Text, CultureInfo.InvariantCulture);
        var settings = prior with
        {
            Threshold = int.Parse(ThresholdInput.Text, CultureInfo.InvariantCulture), Blur = int.Parse(BlurInput.Text, CultureInfo.InvariantCulture), MinArea = Read(MinAreaInput), MaxArea = Read(MaxAreaInput), MaxJump = Read(MaxJumpInput),
            DiameterCm = string.IsNullOrWhiteSpace(DiameterInput.Text) ? null : Read(DiameterInput),
            StartSeconds = Read(StartInput), EndSeconds = string.IsNullOrWhiteSpace(EndInput.Text) ? null : Read(EndInput),
            Invert = InvertInput.IsChecked == true, ArenaConfirmed = ArenaConfirmedInput.IsChecked == true,
            Platform = prior.Platform is { } p ? p with { Radius = Read(SiteDiameterInput) / (2 * _model.Selected!.Asset.Metadata.Width) } : null
        };
        settings.Validate(); return settings;
    }
    private void UpdateReadouts()
    {
        if (_model.Selected is not { } row) return;
        SelectedTitle.Text = row.DisplayName;
        AutomationProperties.SetName(SelectedTitle, _model.Blind ? "Blinded recording " + row.DisplayName : row.DisplayName);
        var contract = CurrentContract();
        ContractLabel.Text = contract.Label;
        if (_current is null) { CoverageValue.Text = "—"; CoverageCaption.Text = "Accepted detections, not validated accuracy"; MetricsReadout.Text = "Not analyzed. Confirm geometry, trial window and detector settings."; row.Refresh(); return; }
        var scope = CurrentMeasurementScope();
        var m = _current.Metrics; CoverageValue.Text = $"{m.CoveragePercent:F1}%";
        CoverageCaption.Text = scope.TrialEndpointsWithheld ? "Raw decoder-run coverage, not the biological trial" : "Accepted detections, not validated accuracy";
        var visit = m.SiteVisitStatus is EventHistory.EarlierVisitPossibleInGap or EventHistory.VisitHistoryIndeterminate ? "Indeterminate" : m.FirstObservedSiteVisitSeconds is { } first ? $"{first:F2} s" : "Not observed / site unset";
        var bounds = m.TargetFullTrialPercentBounds is { } b ? $"Target full-trial range: {b.Lower:F1}–{b.Upper:F1}%\n" : "Set platform site for target measurements.\n";
        var descriptives = $"Raw decoder-run unknown time: {m.UnknownSeconds:F3} s\nRaw decoder-run longest gap: {m.LongestGapSeconds:F3} s";
        var endpoints = scope.TrialEndpointsWithheld
            ? descriptives
            : descriptives + $"\nPath: {m.Distance:F2} {m.DistanceUnit}\nMean speed: {m.MeanSpeed:F3} {m.DistanceUnit}/s\nWall time (observed): {m.WallObservedPercent:F1}%\nFirst site visit: {visit}\n" + bounds + string.Join("   ", m.QuadrantObservedPercent.Select(q => $"{q.Key} {q.Value:F1}%"));
        MetricsReadout.Text = scope.Visible + "\n" + endpoints;
        if (contract.EditablePreview) MetricsReadout.Text = contract.Label + "\n\n" + MetricsReadout.Text;
        ScrubBlindedReadouts();
        row.Refresh();
    }

    private RawRunMeasurementScope CurrentMeasurementScope()
    {
        if (_current is null) throw new InvalidOperationException("Analyze a recording first.");
        ProtocolWindow? window = null;
        ProtocolOutcome? outcome = null;
        if (_model.Selected is { } row)
        {
            window = _model.Workspace.Store.LatestProtocol(row.Asset.Id);
            if (window is not null)
                outcome = ProtocolEvaluator.Evaluate(window, row.Asset.Metadata, row.Asset.Phase, _model.Workspace.Store.EffectiveBiologicalEvents(row.Asset.Id));
        }
        return RawRunMeasurementScope.Describe(_current, window, outcome);
    }

    private void UpdateAnnotationGates()
    {
        if (WaterEntryButton is null) return;
        var row = _model.Selected;
        var integral = row is not null && Math.Abs(FrameSlider.Value - Math.Round(FrameSlider.Value)) <= 1e-6;
        var frame = integral ? (int)Math.Round(FrameSlider.Value) : -1;
        var ready = _model.NotBusy && row is not null && integral && DisplayedFrameMatches(row, frame);
        WaterEntryButton.IsEnabled = ready;
        MountingButton.IsEnabled = ready;
        InterventionButton.IsEnabled = ready;
        RecordingEndButton.IsEnabled = ready;
        ReviseEventButton.IsEnabled = ready;
        MarkUnknownButton.IsEnabled = ready;
        CorrectPointButton.IsEnabled = ready;
        SaveProtocolButton.IsEnabled = _model.NotBusy;
        if (UseDisplayedReleaseButton is not null) UseDisplayedReleaseButton.IsEnabled = ready;
        if (AttestInspectionButton is not null) AttestInspectionButton.IsEnabled = ready;
    }

    private bool DisplayedFrameMatches(TrialRow row, int frame) =>
        _shown is { } shown
        && shown.Generation == _frameGeneration
        && shown.VideoId == row.Asset.Id
        && shown.SourceSha256 == row.Asset.SourceSha256
        && shown.Frame == frame;

    private void CommitTrialMetadata(TrialRow row)
    {
        _model.Workspace.Store.SaveVideo(row.Asset);
        _model.Workspace.Store.Audit("metadata_edited", row.Asset);
        _model.SyncReview(row);
        if (_model.Selected?.Asset.Id == row.Asset.Id)
        {
            RefreshAnnotations();
            UpdateReadouts();
        }
        else RefreshCollectionCompatibility();
    }
    private void PersistSettings(TrackingSettings edited, string? reason)
    {
        var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
        var wasConfirmed = row.Asset.Settings.ArenaConfirmed || edited.ArenaConfirmed;
        var proposed = ArenaGeometry.MergeEdit(row.Asset.Settings, edited);
        if (SettingsIdentity.Hash(proposed) == SettingsIdentity.Hash(row.Asset.Settings))
        {
            FillSettings(row.Asset.Settings); return;
        }
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Enter a reason before saving geometry or detector settings.");
        var saved = _model.Workspace.Store.SaveEditedSettings(row.Asset with { Settings = proposed }, reason.Trim());
        row.Replace(row.Asset with { Settings = saved });
        _model.SyncReview(row);
        FillSettings(saved); RefreshReviewQueue(); UpdateReadouts(); DrawOverlay(); row.Refresh();
        _model.Status = wasConfirmed && !saved.ArenaConfirmed
            ? "Geometry saved. Pool confirmation was cleared; confirm the boundary after checking the editable preview."
            : "Settings saved. Reanalyze changed settings before confirming review.";
    }

    private async void TrialSelected(object sender, SelectionChangedEventArgs e) { if (_ready && !_loading) await ShowSelectedAsync(); }
    private async void FrameChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_ready && !_loading) await DisplayFrameAsync(); }
    private async void BinaryChanged(object sender, RoutedEventArgs e) { if (_ready) await DisplayFrameAsync(); }
    private void ViewChanged(object sender, RoutedEventArgs e) { if (_ready) DrawOverlay(); }
    private void PreviousFrame(object sender, RoutedEventArgs e) { StopPlayback(); FrameSlider.Value = Math.Max(0, FrameSlider.Value - 1); }
    private void NextFrame(object sender, RoutedEventArgs e) { StopPlayback(); FrameSlider.Value = Math.Min(FrameSlider.Maximum, FrameSlider.Value + 1); }
    private void PlayPause(object sender, RoutedEventArgs e) { if (_playTimer.IsEnabled) StopPlayback(); else { _playStart = (int)FrameSlider.Value; _playClock.Restart(); _playTimer.Start(); PlayButton.Content = "❚❚"; } }
    private void StopPlayback() { _playTimer.Stop(); _playClock.Stop(); if (PlayButton is not null) PlayButton.Content = "▶"; }
    private void SetPool(object sender, RoutedEventArgs e) { StopPlayback(); _editMode = "pool"; _poolCorner = null; ViewerHint.Text = "Click two opposite corners of the water-surface bounding box, in either order. Confirmation stays off until you save it deliberately."; FrameOverlay.Cursor = Cursors.Cross; }
    private void SetPlatform(object sender, RoutedEventArgs e) { StopPlayback(); _editMode = "platform"; ViewerHint.Text = "Click the platform center. The setting is the site ellipse's horizontal source-pixel width. Region entry is not confirmed mounting."; FrameOverlay.Cursor = Cursors.Cross; }
    private void SetCorrection(object sender, RoutedEventArgs e) { StopPlayback(); _editMode = "correction"; ViewerHint.Text = "Enter a reason, then click the center of the mouse’s body in this exact frame. The automatic track remains immutable."; FrameOverlay.Cursor = Cursors.Cross; }
    private void OverlayClicked(object sender, MouseButtonEventArgs e)
    {
        if (_editMode is null || _model.Selected is null || _model.Busy) return;
        var position = e.GetPosition(ViewerViewport);
        try { ApplyViewportPoint(position.X, position.Y); }
        catch (Exception ex) { _model.Status = ex.Message; }
    }
    private void CorrectFrame(int? x, int? y)
    {
        var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
        var contract = CurrentContract();
        if (!contract.AllowCorrection) throw new InvalidOperationException(contract.Label + " Corrections stay blocked until this geometry is reanalyzed.");
        var raw = _model.Workspace.Store.LatestRun(row.Asset.Id) ?? throw new InvalidOperationException("Analyze this recording first.");
        var frame = CurrentExactFrame();
        _model.Workspace.Store.SaveCorrection(raw, new(raw.Id, frame, x, y, RequiredReason(), DateTime.UtcNow));
        _current = _model.Workspace.Store.EffectiveRun(raw); row.Run = _current;
        _model.SyncReview(row);
        RefreshReviewQueue(); UpdateReadouts(); DrawOverlay(); row.Refresh();
        _model.Status = $"Correction saved on exact frame {frame}. Measurements were recalculated and tracking review is pending.";
    }
    private void MarkUnknown(object sender, RoutedEventArgs e) { try { CorrectFrame(null, null); } catch (Exception ex) { _model.Status = ex.Message; } }
    private void SaveSettings(object sender, RoutedEventArgs e) { try { PersistSettings(ReadSettings(), ReasonText()); } catch (Exception ex) { _model.Status = ex.Message; } }
    private void JumpToReview(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not ReviewMoment moment) return;
        try
        {
            var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
            ShowExactFrame(moment.StartFrame);
            if (Math.Abs(FrameSlider.Value - moment.StartFrame) > 1e-6)
                throw new InvalidOperationException("The review moment did not seek its canonical source frame.");
            _navigationMoment = moment;
            var receipt = _model.Workspace.Store.RecordNavigation(row.Asset, moment.StartFrame, _current?.Id, moment);
            if (receipt.DisplayCompleted)
                throw new InvalidOperationException("Navigation was recorded as a completed display.");
            _model.Status = string.Create(CultureInfo.InvariantCulture, $"Navigation requested source frame {moment.StartFrame}. Inspection is recorded only after that frame is displayed and attested.");
        }
        catch (Exception ex) { _model.Status = ex.Message; }
    }
    private void MetadataEdited(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is not TrialRow row) return;
        Dispatcher.BeginInvoke(() => { try { CommitTrialMetadata(row); } catch (Exception ex) { _model.Status = ex.Message; } }, DispatcherPriority.Background);
    }
    private void BlindChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppressBlind) return;
        if (BlindReview.IsChecked == true)
        {
            _model.Blind = true;
            ApplyBlindChrome();
            _model.Workspace.Store.Audit("blind_review_enabled", new { Enabled = true });
            RefreshAnnotations();
            RefreshInclusion();
            RefreshCollectionCompatibility();
            UpdateReadouts();
            return;
        }
        try
        {
            _model.Workspace.Store.RecordUnblinding(RequiredReviewer(), RequiredReason());
            _model.Blind = false;
            ApplyBlindChrome();
            RefreshAnnotations();
            RefreshInclusion();
            RefreshCollectionCompatibility();
            UpdateReadouts();
            _model.Status = "Blind review removed. The reason is stored with the study.";
        }
        catch (Exception ex)
        {
            _suppressBlind = true;
            BlindReview.IsChecked = true;
            _suppressBlind = false;
            _model.Blind = true;
            ApplyBlindChrome();
            _model.Status = ex.Message;
        }
    }
    private async void ImportVideos(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "Video recordings|*.mp4;*.avi;*.mov;*.mkv;*.m4v;*.wmv" };
        if (dialog.ShowDialog(this) != true) return;
        _model.Busy = true;
        try { foreach (var file in dialog.FileNames) await Task.Run(() => _model.Workspace.Import(file)); _model.Reload(); await ShowSelectedAsync(); _model.Status = "Recordings imported. Confirm mouse identities and study metadata before group interpretation."; }
        catch (Exception ex) { _model.Status = ex.Message; }
        finally { _model.Busy = false; }
    }
    private async void AnalyzeSelected(object sender, RoutedEventArgs e) { if (_model.Selected is not null) { try { PersistSettings(ReadSettings(), ReasonText()); await RunBatchAsync([_model.Selected.Asset]); } catch (Exception ex) { _model.Status = ex.Message; } } }
    private async void AnalyzeAll(object sender, RoutedEventArgs e) { try { if (_model.Selected is not null) PersistSettings(ReadSettings(), ReasonText()); await RunBatchAsync(_model.Rows.Select(r => r.Asset).ToArray()); } catch (Exception ex) { _model.Status = ex.Message; } }
    private async Task RunBatchAsync(IEnumerable<VideoAsset> assets)
    {
        if (_model.Busy) return; StopPlayback(); _model.Busy = true; _batchCancel = new();
        try
        {
            var progress = new Progress<BatchProgress>(p => { _model.Progress = p.CurrentPercent; _model.Status = $"{p.Complete}/{p.Total} complete  •  {(_model.Blind ? "Recording" : p.Filename)}  •  {p.CurrentPercent:F0}%  •  {p.Status}"; });
            var result = await _model.Workspace.AnalyzeAsync(assets, progress, _batchCancel.Token);
            _model.Reload(); await ShowSelectedAsync(); _model.Status = $"{result.Completed} completed; {result.Reused} matching runs reused; {result.Errors.Count} errors." + (result.Cancelled ? " Batch cancelled; completed runs preserved." : " Review each track before interpretation.");
        }
        catch (Exception ex) { _model.Status = ex.Message; }
        finally { _model.Busy = false; _batchCancel.Dispose(); _batchCancel = null; }
    }
    private void CancelBatch(object sender, RoutedEventArgs e) => _batchCancel?.Cancel();
    private void ConfirmReview(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = _model.Selected ?? throw new InvalidOperationException("Select a recording.");
            var raw = _model.Workspace.Store.LatestRun(row.Asset.Id) ?? throw new InvalidOperationException("Analyze first.");
            var contract = CurrentContract();
            if (!contract.AllowReviewConfirmation) throw new InvalidOperationException(contract.Label);
            if (StudyStore.SettingsHash(raw.Settings) != StudyStore.SettingsHash(row.Asset.Settings)) throw new InvalidOperationException("Reanalyze changed settings before review.");
            if (OpenCvTracker.HashFile(row.Asset.Path) != raw.SourceSha256) throw new InvalidOperationException("The original source changed.");
            if (MessageBox.Show(this, "Confirm that you inspected the original video, pool boundary, biological trial interval and uncertain tracking moments. Coverage alone does not establish accuracy.", "Record visual review", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _model.Workspace.Store.RecordReview(raw, true); _model.SyncReview(row); _model.Status = "Tracking review recorded. Study design, inclusion, and endpoint eligibility remain separate checks.";
        }
        catch (Exception ex) { _model.Status = ex.Message; }
    }
    private void ExportRun(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_model.Blind) throw new InvalidOperationException("Unblind explicitly before exporting identifying study records.");
            if (_current is null) throw new InvalidOperationException("Analyze a recording first.");
            var dialog = new OpenFolderDialog { Title = "Choose analysis export folder" };
            if (dialog.ShowDialog(this) != true) return;
            var folder = System.IO.Path.Combine(dialog.FolderName, "MouseTrackerTabajara-" + _current.Id[..8]);
            ExportCurrentRun(folder);
        }
        catch (Exception ex) { _model.Status = ex.Message; }
    }
    private void ExportCurrentRun(string folder)
    {
        if (_model.Blind) throw new InvalidOperationException("Unblind explicitly before exporting identifying study records.");
        if (_current is null) throw new InvalidOperationException("Analyze a recording first.");
        var scope = CurrentMeasurementScope();
        ExportWriter.SaveRun(_current, folder, scope);
        _model.Status = scope.TrialEndpointsWithheld
            ? "Exported the raw decoder run. Trial endpoints are withheld. " + TrialMeasurementWording.ProtocolLabelDoesNotSupply + " " + folder
            : "Exported raw decoder-run coordinates, saved analysis and HTML report to " + folder;
    }

    private sealed record ShownFrame(string VideoId, string SourceSha256, int Frame, int Generation);

    private void OpenRoadmap(object sender, RoutedEventArgs e)
    {
        var path = App.FindRepositoryFile("docs/ROADMAP.html");
        if (path is not null) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); else _model.Status = "Roadmap file not found in this development distribution.";
    }
    private void RefreshRoadmapScore()
    {
        var path = App.FindRepositoryFile("docs/ROADMAP.json");
        if (path is null) { RoadmapScore.Text = "Development build"; return; }
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var progress = json.RootElement.GetProperty("progress");
        RoadmapScore.Text = $"{progress.GetProperty("global_percent").GetDouble():F1}%  •  {progress.GetProperty("earned_points").GetInt32()}/1000";
    }
    public string BrandLayoutReport { get; private set; } = "";
    public void SaveSmokeScreenshot(string path)
    {
        var report = new StringBuilder();
        foreach (var size in new[] { new Size(1440, 960), new Size(MinWidth, MinHeight) })
        {
            RootGrid.Measure(size); RootGrid.Arrange(new Rect(0, 0, size.Width, size.Height)); RootGrid.UpdateLayout();
            report.AppendLine(DescribeBrandLayout(size));
        }
        RootGrid.Measure(new Size(1440, 960)); RootGrid.Arrange(new Rect(0, 0, 1440, 960)); RootGrid.UpdateLayout();
        BrandLayoutReport = report.ToString().TrimEnd();
        var bitmap = new RenderTargetBitmap(1440, 960, 96, 96, PixelFormats.Pbgra32); bitmap.Render(RootGrid);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var file = File.Create(path); encoder.Save(file);
    }
    private string DescribeBrandLayout(Size size)
    {
        if (Title != "Mouse Tracker Tabajara — Native WPF workspace") throw new InvalidOperationException("Window title is not the current product name.");
        if (ProductWordmark.Text != "Mouse Tracker Tabajara" || ProductWordmark.TextTrimming != TextTrimming.None || ProductWordmark.TextWrapping != TextWrapping.Wrap)
            throw new InvalidOperationException("Product wordmark is missing or can be trimmed.");
        if (StudyTitle.Text != "Luciana’s Morris water maze") throw new InvalidOperationException("The Morris water maze study title changed.");
        EnsureInside(ProductWordmark, BrandSidebar, "Product wordmark");
        EnsureInside(StudyTitle, RootGrid, "Study title");
        EnsureInside(CommandBar, RootGrid, "Commands");
        foreach (FrameworkElement child in CommandBar.Children) EnsureInside(child, RootGrid, "Command");
        var wordmark = Bounds(ProductWordmark, RootGrid); var title = Bounds(StudyTitle, RootGrid); var commands = Bounds(CommandBar, RootGrid);
        if (wordmark.IntersectsWith(title) || wordmark.IntersectsWith(commands)) throw new InvalidOperationException("Product wordmark overlaps the workspace header.");
        if (title.IntersectsWith(commands)) throw new InvalidOperationException($"Study title overlaps commands at {size.Width:F0}x{size.Height:F0}.");
        var dpi = VisualTreeHelper.GetDpi(ProductWordmark).PixelsPerDip;
        var typeface = new Typeface(ProductWordmark.FontFamily, ProductWordmark.FontStyle, ProductWordmark.FontWeight, ProductWordmark.FontStretch);
        var wrapped = new FormattedText(ProductWordmark.Text, CultureInfo.CurrentUICulture, ProductWordmark.FlowDirection, typeface, ProductWordmark.FontSize, Brushes.White, dpi)
        { MaxTextWidth = Math.Max(1, ProductWordmark.ActualWidth), Trimming = TextTrimming.None };
        if (wrapped.Height > ProductWordmark.ActualHeight + 1) throw new InvalidOperationException($"Product wordmark is clipped vertically ({wrapped.Height:F1} > {ProductWordmark.ActualHeight:F1}).");
        if (wrapped.WidthIncludingTrailingWhitespace > ProductWordmark.ActualWidth + 1) throw new InvalidOperationException($"Product wordmark is clipped horizontally ({wrapped.WidthIncludingTrailingWhitespace:F1} > {ProductWordmark.ActualWidth:F1}).");
        return FormattableString.Invariant($"size={size.Width:F0}x{size.Height:F0} wordmark={ProductWordmark.ActualWidth:F1}x{ProductWordmark.ActualHeight:F1} text={wrapped.WidthIncludingTrailingWhitespace:F1}x{wrapped.Height:F1} sidebar={BrandSidebar.ActualWidth:F1}x{BrandSidebar.ActualHeight:F1} title={StudyTitle.ActualWidth:F1} commands={CommandBar.ActualWidth:F1}x{CommandBar.ActualHeight:F1}");
    }
    private static Rect Bounds(FrameworkElement element, FrameworkElement origin)
    {
        var point = element.TranslatePoint(new System.Windows.Point(0, 0), origin);
        return new Rect(point, new Size(element.ActualWidth, element.ActualHeight));
    }
    private static void EnsureInside(FrameworkElement element, FrameworkElement bounds, string name)
    {
        var rect = Bounds(element, bounds);
        if (rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException($"{name} has no visible layout.");
        if (rect.Left < -0.5 || rect.Top < -0.5 || rect.Right > bounds.ActualWidth + 0.5 || rect.Bottom > bounds.ActualHeight + 0.5)
            throw new InvalidOperationException($"{name} extends outside {bounds.Name} ({rect.Left:F1},{rect.Top:F1},{rect.Right:F1},{rect.Bottom:F1} vs {bounds.ActualWidth:F1}x{bounds.ActualHeight:F1}).");
    }
}
