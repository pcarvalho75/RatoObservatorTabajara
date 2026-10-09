using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Tracking;

namespace MouseTrackerTabajara.Desktop;

public sealed record AtlasClockSource(string RunId, string SourceSha256, string SnapshotRevision, int AnchorFrame);

/// <summary>Explicit technical anchor clock. It is not a biological release/window/alignment contract.</summary>
public sealed class AtlasSynchronizedWindow : Window
{
    private sealed record Pane(AtlasRecording Recording, int Anchor, int AttestedLastFrame, Image Image, Canvas Overlay, TextBlock Clock);
    private readonly AtlasSnapshot _snapshot;
    private readonly FrameDecoderPool _decoder = new(4);
    private readonly List<Pane> _panes = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _integrityTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly WrapPanel _controls = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly Stopwatch _clock = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _seconds = new() { Text = "0", Width = 75 };
    private readonly Button _play = new() { Content = "Play common clock" };
    private double _playStart;
    private bool _closed, _decoding, _checkingIntegrity, _invalidated;
    private int _generation;
    public bool Held { get; private set; }
    public Task PendingDisplay { get; private set; } = Task.CompletedTask;
    public double? DisplayedSeconds { get; private set; }
    public IReadOnlyList<int> DisplayedFrames { get; private set; } = [];
    public IReadOnlyList<BitmapSource?> DisplayedBitmaps => _panes.Select(p => p.Image.Source as BitmapSource).ToArray();
    public IReadOnlyList<Canvas> Overlays => _panes.Select(p => p.Overlay).ToArray();
    public double MaximumClockSeconds { get; }
    public AtlasSynchronizedWindow(AtlasSnapshot snapshot, IReadOnlyList<AtlasClockSource> sources, bool anchorsConfirmed, bool blinded)
    {
        if (!anchorsConfirmed) throw new InvalidOperationException("Confirm technical source-frame clock anchors explicitly.");
        if (sources.Count is < 2 or > 4 || sources.Select(s => s.SourceSha256).Distinct().Count() != sources.Count)
            throw new ArgumentException("Pin two to four distinct frozen recordings for synchronized inspection.");
        _snapshot = snapshot;
        Title = "Mouse Tracker Tabajara — Synchronized source review"; Width = 1200; Height = 800; MinWidth = 1050; MinHeight = 700;
        var root = new DockPanel { Background = Brushes.WhiteSmoke, Margin = new Thickness(16) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Synchronized technical source-frame clock", FontSize = 23 });
        header.Children.Add(new TextBlock { Text = "Clock zero is each explicitly confirmed source anchor. This is not biological release alignment; planned-window group patterns and conclusions remain held.", TextWrapping = TextWrapping.Wrap });
        header.Children.Add(_status); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        _play.Click += (_, _) =>
        {
            if (_timer.IsEnabled) { Stop(); return; }
            if (Held || DisplayedSeconds is null) return;
            _playStart = DisplayedSeconds.Value; _clock.Restart(); _timer.Start(); _play.Content = "Pause common clock";
        };
        var seek = new Button { Content = "Seek common seconds" };
        seek.Click += (_, _) => { Stop(); if (double.TryParse(_seconds.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) PendingDisplay = DisplayAsync(value); else Fail(); };
        _controls.Children.Add(_play); _controls.Children.Add(_seconds); _controls.Children.Add(seek); DockPanel.SetDock(_controls, Dock.Bottom); root.Children.Add(_controls);
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var source in sources)
        {
            var row = snapshot.Recordings.SingleOrDefault(r => r.NavigationId == source.RunId && r.Asset.SourceSha256 == source.SourceSha256)
                ?? throw new InvalidDataException("Source is outside the frozen atlas.");
            var asset = row.Asset; var run = row.EffectiveRun();
            if (source.SnapshotRevision != snapshot.Revision || source.AnchorFrame < 0 || source.AnchorFrame >= asset.Metadata.FrameCount || run is null ||
                !run.NominalTimingVerified || run.SourceSha256 != asset.SourceSha256 || run.Metadata != asset.Metadata)
                throw new InvalidDataException("Frozen source anchor/timing/run contract is invalid or unverified.");
            OpenCvTracker.VerifyNominalTiming(run.Points, run.Metadata.Fps);
            MouseTrackerTabajara.Application.CompletedRecordingContract.Validate(row.RawRun!);
            if (run.Points.Count == 0 || source.AnchorFrame < run.Points[0].Frame || source.AnchorFrame > run.Points[^1].Frame)
                throw new InvalidDataException("Technical clock anchor is outside the run's attested nominal timing interval.");
            var panel = new DockPanel { Margin = new Thickness(8) };
            var label = new TextBlock { Text = blinded ? "Blinded source " + (_panes.Count + 1) : asset.Filename + " | " + asset.AnimalId, TextWrapping = TextWrapping.Wrap };
            var clock = new TextBlock { TextWrapping = TextWrapping.Wrap }; DockPanel.SetDock(label, Dock.Top); DockPanel.SetDock(clock, Dock.Bottom); panel.Children.Add(label); panel.Children.Add(clock);
            var image = new Image(); var overlay = new Canvas(); var plane = new Grid { Width = asset.Metadata.Width, Height = asset.Metadata.Height }; plane.Children.Add(image); plane.Children.Add(overlay);
            panel.Children.Add(new Viewbox { Child = plane, Stretch = Stretch.Uniform }); grid.Children.Add(panel);
            _panes.Add(new(row, source.AnchorFrame, run.Points[^1].Frame, image, overlay, clock));
        }
        MaximumClockSeconds = _panes.Min(p => (p.AttestedLastFrame - p.Anchor) / p.Recording.Asset.Metadata.Fps);
        root.Children.Add(grid); Content = root;
        _timer.Tick += (_, _) =>
        {
            if (_decoding) return;
            var seconds = _playStart + _clock.Elapsed.TotalSeconds;
            if (seconds >= MaximumClockSeconds) { Stop(); seconds = MaximumClockSeconds; }
            PendingDisplay = DisplayAsync(seconds);
        };
        _integrityTimer.Tick += async (_, _) =>
        {
            if (_closed || _invalidated || _checkingIntegrity) return;
            _checkingIntegrity = true;
            try { if (!await Task.Run(_snapshot.IsCurrent) && !_closed) InvalidateFrozenView(); }
            catch (Exception) { if (!_closed) InvalidateFrozenView(); }
            finally { _checkingIntegrity = false; }
        };
        _integrityTimer.Start();
        Closed += (_, _) => { _closed = true; Stop(); _integrityTimer.Stop(); _generation++; _decoder.Dispose(); };
        PendingDisplay = DisplayAsync(0);
    }
    public async Task DisplayAsync(double seconds)
    {
        if (_closed || _invalidated) return;
        var generation = ++_generation; _decoding = true; Clear();
        try
        {
            if (_closed || !double.IsFinite(seconds) || seconds < 0 || seconds > MaximumClockSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
            var frames = _panes.Select(p => p.Anchor + (int)Math.Floor(seconds * p.Recording.Asset.Metadata.Fps + 1e-9)).ToArray();
            var buffers = await Task.Run(() =>
            {
                if (!_snapshot.IsCurrent()) throw new InvalidDataException("Frozen study changed.");
                var bytes = _panes.Select((p, i) => _decoder.Read(p.Recording.Asset.Path, frames[i], expectedSha256: p.Recording.Asset.SourceSha256)).ToArray();
                if (!_snapshot.IsCurrent()) throw new InvalidDataException("Frozen study changed during synchronized decode."); return bytes;
            });
            if (_closed || generation != _generation) return;
            for (var i = 0; i < _panes.Count; i++)
            {
                var pane = _panes[i]; using var stream = new MemoryStream(buffers[i]);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); pane.Image.Source = bitmap;
                pane.Clock.Text = $"Source frame {frames[i]}; anchor {pane.Anchor}; common technical clock {seconds:F3} s";
                Draw(pane, frames[i]);
            }
            DisplayedFrames = frames; DisplayedSeconds = seconds; Held = false; _status.Text = "All sources displayed from one frozen revision and common technical clock. Group-pattern licensing remains held.";
        }
        catch (Exception ex) { if (!_closed && generation == _generation) { if (ex is ArgumentOutOfRangeException) Fail(); else InvalidateFrozenView(); } }
        finally { if (generation == _generation) _decoding = false; }
    }
    private static void Draw(Pane pane, int frame)
    {
        var run = pane.Recording.EffectiveRun()!; var s = run.Settings; var m = run.Metadata;
        var ellipse = new Ellipse { Width = 2 * s.Rx * m.Width, Height = 2 * s.Ry * m.Height, Stroke = Brushes.LightSkyBlue, StrokeThickness = 2 };
        Canvas.SetLeft(ellipse, (s.Cx - s.Rx) * m.Width); Canvas.SetTop(ellipse, (s.Cy - s.Ry) * m.Height); pane.Overlay.Children.Add(ellipse);
        if (s.Platform is { } site)
        {
            var radius = site.Radius * m.Width; var vertical = radius * s.Ry * m.Height / (s.Rx * m.Width);
            var shape = new Ellipse { Width = radius * 2, Height = vertical * 2, Stroke = Brushes.PeachPuff, StrokeThickness = 2 };
            Canvas.SetLeft(shape, site.X * m.Width - radius); Canvas.SetTop(shape, site.Y * m.Height - vertical); pane.Overlay.Children.Add(shape);
        }
        Polyline? line = null; TrackPoint? prior = null;
        foreach (var p in run.Points)
        {
            if (!p.Accepted) { line = null; prior = null; continue; }
            if (line is null || prior is null || p.Frame != prior.Frame + 1) { line = new() { Stroke = Brushes.Coral, StrokeThickness = 2 }; pane.Overlay.Children.Add(line); }
            line.Points.Add(new(p.X!.Value, p.Y!.Value)); prior = p;
        }
        var active = run.Points.SingleOrDefault(p => p.Frame == frame);
        if (active is { Accepted: true })
        {
            var marker = new Ellipse { Width = 18, Height = 18, Stroke = Brushes.LightGreen, StrokeThickness = 2, Tag = "active-source-marker" };
            Canvas.SetLeft(marker, active.X!.Value - 9); Canvas.SetTop(marker, active.Y!.Value - 9); pane.Overlay.Children.Add(marker);
        }
    }
    public void InvalidateFrozenView()
    {
        if (_closed || _invalidated) return;
        _invalidated = true; _generation++; _decoding = false; _integrityTimer.Stop();
        Fail(); _controls.IsEnabled = false; _decoder.Dispose();
        _status.Text = "Frozen synchronized review is stale or reset. All panes are held. Recompute the atlas and confirm fresh anchors in a new view.";
    }
    private void Clear() { foreach (var pane in _panes) { pane.Image.Source = null; pane.Overlay.Children.Clear(); pane.Clock.Text = ""; } DisplayedFrames = []; DisplayedSeconds = null; }
    private void Fail() { Stop(); Held = true; Clear(); _status.Text = "Synchronized review held. Recompute and verify frozen source/anchor/timing integrity."; }
    private void Stop() { _timer.Stop(); _clock.Stop(); _play.Content = "Play common clock"; }
}
