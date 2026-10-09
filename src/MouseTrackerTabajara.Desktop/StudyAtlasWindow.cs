using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.SpatialViews.Wpf;
using MouseTrackerTabajara.Tracking;

namespace MouseTrackerTabajara.Desktop;

/// <summary>Individual frozen study review; no study roster or group endpoint is inferred from imported files.</summary>
public sealed class StudyAtlasWindow : Window
{
    private readonly string _database;
    private readonly FrameDecoderPool _decoder = new(2);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _integrityTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Stopwatch _playClock = new();
    private readonly CheckBox _blind = new() { Content = "Blind review" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _identity = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid _plane = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new();
    private readonly TextBox _seek = new() { Width = 70, Text = "0", Tag = "atlas-source-frame" };
    private readonly TextBox _unblindReviewer = new() { Width = 120, ToolTip = "Unblinding reviewer" };
    private readonly TextBox _unblindReason = new() { Width = 180, ToolTip = "Reason for unblinding this view" };
    private readonly Button _play = new() { Content = "Play" };
    private readonly List<AtlasClockSource> _clockSources = [];
    private readonly CheckBox _anchorsConfirmed = new() { Content = "Confirm pinned frames as technical clock zeros" };
    private event Action? FrozenViewReset;
    private event Action? ViewReblinded;
    private AtlasRecording? _selected;
    private AnalysisRun? _effective;
    private int _generation, _playStart;
    private bool _closed, _decoding, _checkingIntegrity, _blindReady, _auditReveal;
    public AtlasSnapshot Snapshot { get; private set; }
    public AtlasControl Atlas { get; } = new();
    public Task PendingNavigation { get; private set; } = Task.CompletedTask;
    public int? DisplayedFrame { get; private set; }
    public string? DisplayedSourceSha256 { get; private set; }
    public BitmapSource? DisplayedBitmap => _image.Source as BitmapSource;
    public Canvas SourceOverlay => _overlay;
    public string StatusText => _status.Text;
    public bool Blinded { get => _blind.IsChecked == true; set => _blind.IsChecked = value; }
    public bool Held { get; private set; }

    public StudyAtlasWindow(string database, bool blinded, AtlasSnapshot? frozenSnapshot = null)
    {
        _database = database; Snapshot = frozenSnapshot ?? AtlasSnapshot.Read(database);
        Title = "Mouse Tracker Tabajara — Study atlas"; Width = 1440; Height = 960; MinWidth = 1050; MinHeight = 700;
        Background = new SolidColorBrush(Color.FromRgb(243, 247, 250));
        var root = new DockPanel { Margin = new Thickness(16), Background = Background };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = "Study atlas — individual source review", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Raw detector source inspection. Conditional planned-window maps are available in Experiment when roster, window and per-source reference contracts are current. This viewer does not establish biological inference.", TextWrapping = TextWrapping.Wrap });
        var commands = new WrapPanel(); commands.Children.Add(_blind);
        commands.Children.Add(new TextBlock { Text = "Reviewer / reason to unblind", VerticalAlignment = VerticalAlignment.Center }); commands.Children.Add(_unblindReviewer); commands.Children.Add(_unblindReason);
        var refresh = new Button { Content = "Recompute frozen view", Margin = new Thickness(12, 0, 0, 0) };
        refresh.Click += (_, _) => Recompute(); commands.Children.Add(refresh); header.Children.Add(commands); header.Children.Add(_status);
        var pin = new Button { Content = "Pin displayed source/anchor", Margin = new Thickness(12, 0, 0, 0) };
        pin.Click += (_, _) => { try { PinDisplayedSource(); } catch (Exception) { _status.Text = "Display a valid frozen source frame before pinning; maximum four sources."; } };
        var synchronized = new Button { Content = "Open synchronized sources", Margin = new Thickness(12, 0, 0, 0) };
        synchronized.Click += (_, _) => { try { var view = CreateSynchronizedWindow(_anchorsConfirmed.IsChecked == true); view.Owner = this; view.Show(); } catch (Exception) { _status.Text = "Pin two to four verified sources and explicitly confirm their technical anchors. Biological alignment stays held."; } };
        commands.Children.Add(pin); commands.Children.Add(_anchorsConfirmed); commands.Children.Add(synchronized);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var split = new Grid(); split.ColumnDefinitions.Add(new() { Width = new GridLength(380) }); split.ColumnDefinitions.Add(new());
        split.Children.Add(Atlas);
        var viewer = new Grid { Margin = new Thickness(16, 0, 0, 0) };
        viewer.RowDefinitions.Add(new() { Height = GridLength.Auto }); viewer.RowDefinitions.Add(new()); viewer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        viewer.Children.Add(_identity);
        _plane.Children.Add(_image); _plane.Children.Add(_overlay);
        var viewport = new Border { Background = Brushes.DarkSlateGray, MinHeight = 320, Child = new Viewbox { Stretch = Stretch.Uniform, Child = _plane } };
        Grid.SetRow(viewport, 1); viewer.Children.Add(viewport);
        var controls = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var previous = new Button { Content = "Previous source frame" }; previous.Click += (_, _) => StartStep(-1);
        var next = new Button { Content = "Next source frame" }; next.Click += (_, _) => StartStep(1);
        var seek = new Button { Content = "Seek exact frame" }; seek.Click += (_, _) =>
        { Stop(); if (int.TryParse(_seek.Text, out var frame)) StartSeek(frame); else Hold("Enter an integer source frame."); };
        _play.Click += (_, _) => TogglePlayback();
        controls.Children.Add(previous); controls.Children.Add(next); controls.Children.Add(_play); controls.Children.Add(_seek); controls.Children.Add(seek);
        Grid.SetRow(controls, 2); viewer.Children.Add(controls); Grid.SetColumn(viewer, 1); split.Children.Add(viewer); root.Children.Add(split); Content = root;
        Atlas.FrameRequested += (_, args) => { Stop(); PendingNavigation = NavigateAsync(args); };
        _blind.Checked += (_, _) => { _unblindReviewer.Clear(); _unblindReason.Clear(); ViewReblinded?.Invoke(); ApplyBlind(); };
        _blind.Unchecked += (_, _) =>
        {
            if (!_blindReady || _auditReveal) { ApplyBlind(); return; }
            var reviewer = _unblindReviewer.Text; var reason = _unblindReason.Text; _blind.IsChecked = true; TryUnblind(reviewer, reason);
        };
        _timer.Tick += (_, _) =>
        {
            if (_decoding || _selected is null) return;
            var target = _playStart + (int)Math.Floor(_playClock.Elapsed.TotalSeconds * _selected.Asset.Metadata.Fps);
            if (target >= _selected.Asset.Metadata.FrameCount) { Stop(); return; }
            if (target != DisplayedFrame) StartSeek(target);
        };
        _integrityTimer.Tick += async (_, _) =>
        {
            if (_closed || _checkingIntegrity || Held) return;
            _checkingIntegrity = true; var frozen = Snapshot;
            try { if (!await Task.Run(frozen.IsCurrent) && !_closed && Snapshot.Revision == frozen.Revision) Hold("Frozen atlas is stale. Recompute before further source review."); }
            catch (Exception) { if (!_closed) Hold("Study integrity check failed; recompute before further source review."); }
            finally { _checkingIntegrity = false; }
        };
        _integrityTimer.Start();
        Closed += (_, _) => { _closed = true; Stop(); _integrityTimer.Stop(); _generation++; _decoder.Dispose(); };
        Atlas.Load(Snapshot.SpatialView()); Blinded = blinded; _blindReady = true; ApplyBlind();
        _status.Text = "Frozen individual inventory. No imported-file count establishes biological completeness.";
    }
    public bool TryUnblind(string reviewer, string reason)
    {
        if (!Blinded) return true;
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(reason)) { _status.Text = "Reviewer and reason are required before revealing labels; view remains blind."; return false; }
        try
        {
            new MouseTrackerTabajara.Storage.StudyStore(_database).Audit("atlas_unblinded", new { Reviewer = reviewer.Trim(), Reason = reason.Trim(), SnapshotRevision = Snapshot.Revision });
            _auditReveal = true; _blind.IsChecked = false; return true;
        }
        catch (Exception) { _status.Text = "Unblinding audit failed; view remains blind."; return false; }
        finally { _auditReveal = false; }
    }
    private void ApplyBlind()
    {
        Atlas.Blinded = Blinded;
        LabelRawIntervals();
        if (_selected is not null) SetIdentity();
        if (Held) _status.Text = "Source review held. Recompute and verify study/source integrity.";
    }
    public void Recompute()
    {
        FrozenViewReset?.Invoke();
        _clockSources.Clear(); _anchorsConfirmed.IsChecked = false;
        Stop(); _generation++; _decoder.Reset(); _selected = null; _effective = null; ClearDisplay();
        try { Snapshot = AtlasSnapshot.Read(_database); Atlas.Load(Snapshot.SpatialView()); LabelRawIntervals(); Held = false; _identity.Text = ""; _status.Text = "Frozen view recomputed. Source SHA256 is revalidated on navigation; group conclusions remain held."; }
        catch (Exception) { Hold("Unable to freeze current study. Check integrity and retry recomputation."); }
    }
    public void PinDisplayedSource()
    {
        if (_selected is null || DisplayedFrame is not int frame || Held || (_clockSources.Count == 4 && !_clockSources.Any(s => s.SourceSha256 == _selected.Asset.SourceSha256)) || !Snapshot.IsCurrent()) throw new InvalidOperationException("No coherent source frame to pin.");
        _clockSources.RemoveAll(s => s.SourceSha256 == _selected.Asset.SourceSha256);
        _clockSources.Add(new(_selected.NavigationId, _selected.Asset.SourceSha256, Snapshot.Revision, frame)); _anchorsConfirmed.IsChecked = false;
        _status.Text = $"{_clockSources.Count} frozen sources pinned. Confirm technical anchors before synchronized review; group alignment remains held.";
    }
    public AtlasSynchronizedWindow CreateSynchronizedWindow(bool anchorsConfirmed)
    {
        var view = new AtlasSynchronizedWindow(Snapshot, _clockSources.ToArray(), anchorsConfirmed, Blinded);
        FrozenViewReset += view.InvalidateFrozenView;
        ViewReblinded += view.Close;
        view.Closed += (_, _) => { FrozenViewReset -= view.InvalidateFrozenView; ViewReblinded -= view.Close; };
        return view;
    }
    private void LabelRawIntervals()
    {
        // This host supplies unconfirmed raw detector intervals, not licensed planned-window occupancy.
        if (Atlas.Content is ScrollViewer { Content: Panel body })
        {
            foreach (var text in body.Children.OfType<TextBlock>())
                if (text.Text.StartsWith("Position time uses the planned exposure.", StringComparison.Ordinal))
                    text.Text = "Unknown-position fractions below describe the frozen detector interval, not planned-window occupancy. A frozen view requires recomputation after edits.";
            var rows = Snapshot.Recordings.OrderBy(r => r.NavigationId, StringComparer.Ordinal).ToArray();
            var cards = body.Children.OfType<WrapPanel>().SelectMany(panel => panel.Children.OfType<Border>()).ToArray();
            if (cards.Length != rows.Length) throw new InvalidDataException("Atlas card inventory differs from frozen recording inventory.");
            for (var i = 0; i < cards.Length; i++)
                if (cards[i].Child is StackPanel contents)
                {
                    var old = contents.Children.OfType<TextBlock>().FirstOrDefault(text => Equals(text.Tag, "atlas-inspection-state"));
                    if (old is not null) contents.Children.Remove(old);
                    contents.Children.Insert(1, new TextBlock { Tag = "atlas-inspection-state", Text = rows[i].InspectionLabel, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                }
        }
    }
    public async Task NavigateAsync(AtlasNavigationEventArgs request)
    {
        Stop();
        var row = Snapshot.Recordings.SingleOrDefault(r => r.NavigationId == request.RunId && r.Asset.SourceSha256 == request.SourceSha256);
        if (row is null || request.SnapshotRevision != Snapshot.Revision) { Hold("Navigation is outside the frozen source/run/revision."); return; }
        _selected = row;
        try
        {
            var raw = row.RawRun;
            if (raw is not null && (raw.SourceSha256 != row.Asset.SourceSha256 || raw.Metadata != row.Asset.Metadata))
                throw new InvalidDataException("Frozen run/source mismatch.");
            _effective = row.EffectiveRun(); SetIdentity();
            await SeekAsync(request.Frame);
        }
        catch (Exception) { Hold("Frozen trajectory/source could not be validated. Recompute and verify source integrity."); }
    }
    private void SetIdentity()
    {
        if (_selected is null) return;
        var asset = _selected.Asset;
        _identity.Text = Blinded ? $"Blinded recording; {_selected.InspectionLabel} Raw detector interval; biological endpoints held."
            : $"{asset.Filename} | {(asset.AnimalIdConfirmed ? asset.AnimalId : "provisional identity")} | {asset.GroupName}\nFrozen run {_selected.NavigationId}; revision {Snapshot.Revision[..12]}\n" + string.Join(" ", _selected.Holds);
    }
    private void StartStep(int delta) { Stop(); if (DisplayedFrame is int frame) StartSeek(frame + delta); }
    private void StartSeek(int frame) => PendingNavigation = SeekAsync(frame);
    public async Task SeekAsync(int frame)
    {
        var row = _selected; if (row is null || _closed) return;
        var generation = ++_generation; var snapshot = Snapshot; _decoding = true; ClearDisplay();
        try
        {
            var asset = row.Asset;
            if (frame < 0 || frame >= asset.Metadata.FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
            var bytes = await Task.Run(() =>
            {
                if (!snapshot.IsCurrent()) throw new InvalidDataException("Frozen study is stale.");
                // Same accepted expected-hash decoder implementation, with atlas-owned bounded lifecycle.
                var pixels = _decoder.Read(asset.Path, frame, expectedSha256: asset.SourceSha256);
                if (!snapshot.IsCurrent()) throw new InvalidDataException("Study changed during source decode.");
                return pixels;
            });
            if (_closed || generation != _generation) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            _plane.Width = asset.Metadata.Width; _plane.Height = asset.Metadata.Height; _image.Source = bitmap;
            DisplayedFrame = frame; DisplayedSourceSha256 = asset.SourceSha256; Draw(frame); Held = false; _seek.Text = frame.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _status.Text = $"Source frame {frame} · {frame / asset.Metadata.Fps:F2} s. Frozen raw-run inspection; group conclusions held.";
        }
        catch (Exception) { if (!_closed && generation == _generation) Hold("Source/snapshot mismatch or undecodable exact frame. Recompute and revalidate; pixels withheld."); }
        finally { if (generation == _generation) _decoding = false; }
    }
    private void ClearDisplay() { _image.Source = null; _overlay.Children.Clear(); DisplayedFrame = null; DisplayedSourceSha256 = null; }
    private void Hold(string message) { Stop(); _generation++; _decoding = false; Held = true; ClearDisplay(); _status.Text = Blinded ? "Source review held. Recompute and verify study/source integrity." : message; }
    private void Draw(int frame)
    {
        _overlay.Children.Clear(); if (_selected is null) return;
        var metadata = _selected.Asset.Metadata; var settings = _effective?.Settings ?? _selected.Asset.Settings;
        var rx = settings.Rx * metadata.Width; var ry = settings.Ry * metadata.Height;
        var pool = new Ellipse { Width = 2 * rx, Height = 2 * ry, Stroke = Brushes.LightSkyBlue, StrokeThickness = 2 };
        Canvas.SetLeft(pool, settings.Cx * metadata.Width - rx); Canvas.SetTop(pool, settings.Cy * metadata.Height - ry); _overlay.Children.Add(pool);
        if (settings.Platform is { } site)
        {
            var radius = site.Radius * metadata.Width; var vertical = radius * ry / rx;
            var shape = new Ellipse { Width = 2 * radius, Height = 2 * vertical, Stroke = Brushes.PeachPuff, StrokeThickness = 2 };
            Canvas.SetLeft(shape, site.X * metadata.Width - radius); Canvas.SetTop(shape, site.Y * metadata.Height - vertical); _overlay.Children.Add(shape);
        }
        if (_effective is null) return;
        Polyline? segment = null; TrackPoint? prior = null;
        foreach (var point in _effective.Points)
        {
            if (!point.Accepted) { segment = null; prior = null; continue; }
            if (segment is null || prior is null || point.Frame != prior.Frame + 1)
            { segment = new() { Stroke = Brushes.Coral, StrokeThickness = 2 }; _overlay.Children.Add(segment); }
            segment.Points.Add(new(point.X!.Value, point.Y!.Value)); prior = point;
        }
        var active = _effective.Points.SingleOrDefault(p => p.Frame == frame);
        if (active is { Accepted: true })
        {
            var marker = new Ellipse { Width = 18, Height = 18, Stroke = Brushes.LightGreen, StrokeThickness = 2, Tag = "active-source-marker" };
            Canvas.SetLeft(marker, active.X!.Value - 9); Canvas.SetTop(marker, active.Y!.Value - 9); _overlay.Children.Add(marker);
        }
    }
    private void TogglePlayback()
    {
        if (_timer.IsEnabled) { Stop(); return; }
        if (DisplayedFrame is not int frame || _selected is null || Held) return;
        _playStart = frame; _playClock.Restart(); _timer.Start(); _play.Content = "Pause";
    }
    private void Stop() { _timer.Stop(); _playClock.Stop(); _play.Content = "Play"; }
}
