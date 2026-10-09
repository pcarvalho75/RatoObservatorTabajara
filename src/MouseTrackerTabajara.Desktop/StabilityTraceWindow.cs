using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Tracking;

namespace MouseTrackerTabajara.Desktop;

public sealed class StabilityTraceWindow : Window, IDisposable
{
    private readonly StabilityTrace _trace;
    private readonly FrameDecoderPool _decoder = new(1);
    private readonly Canvas _overlay = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _frame = new() { Width = 85 };
    private readonly AnalysisRun? _run;
    private int _generation;
    private bool _disposed;
    public BitmapSource? DisplayedBitmap => _image.Source as BitmapSource;
    public int? DisplayedFrame { get; private set; }
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    public string Status => _status.Text;
    public FrameDecoderStatistics Statistics => _decoder.Statistics;
    public StabilityTraceWindow(StabilityTrial trial, IEnumerable<StabilityProducerInput>? inputs = null)
    {
        _trace = trial.Trace ?? throw new InvalidDataException("This frozen case has no authenticated source trace.");
        if (_trace.FirstFrame < 0 || _trace.LastFrame < _trace.FirstFrame) throw new InvalidDataException("Frozen frame range invalid.");
        var runJson = _trace.RunJson;
        if (runJson.Length == 0 && _trace.RunPath.Length == 0) runJson = inputs?.SingleOrDefault(input => input.Kind == "corrected-coordinate-run" && input.Sha256 == _trace.RunSha256)?.Json ?? "";
        if (!string.IsNullOrWhiteSpace(runJson))
        {
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(runJson)));
            if (hash != _trace.RunSha256) throw new InvalidDataException("Frozen coordinate run material differs.");
            _run = JsonDefaults.Deserialize<AnalysisRun>(runJson);
        }
        else if (!string.IsNullOrWhiteSpace(_trace.RunPath))
        {
            if (OpenCvTracker.HashFile(_trace.RunPath) != _trace.RunSha256) throw new InvalidDataException("Frozen run file changed.");
            _run = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(_trace.RunPath));
        }
        if (_run is not null) { CompletedRecordingContract.Validate(_run); if (_run.SourceSha256 != _trace.SourceSha256 || _trace.LastFrame >= _run.Metadata.FrameCount) throw new InvalidDataException("Run/source/frame trace mismatch."); }
        Title = "Frozen case source trace"; Width = 1050; Height = 700; MinWidth = 900; MinHeight = 600;
        var root = new DockPanel { Margin = new(16), Background = Brushes.WhiteSmoke };
        var controls = new StackPanel { Orientation = Orientation.Horizontal }; _frame.Text = (_trace.OutlierFrame ?? _trace.FirstFrame).ToString(System.Globalization.CultureInfo.InvariantCulture);
        controls.Children.Add(new TextBlock { Text = $"Exact source frames {_trace.FirstFrame}–{_trace.LastFrame}. Pixels are not automatically redacted. ", TextWrapping = TextWrapping.Wrap });
        controls.Children.Add(_frame); var seek = new Button { Content = "Load frame" }; seek.Click += (_, _) => PendingOperation = DisplayAsync(); controls.Children.Add(seek);
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls); DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        var plane = new Grid(); plane.Children.Add(_image); plane.Children.Add(_overlay); root.Children.Add(new Viewbox { Child = plane }); Content = root;
        Loaded += (_, _) => PendingOperation = DisplayAsync(); Closed += (_, _) => Dispose();
    }
    public async Task DisplayAsync()
    {
        var generation = ++_generation; _image.Source = null; _overlay.Children.Clear(); DisplayedFrame = null;
        try
        {
            if (!int.TryParse(_frame.Text, out var frame) || frame < _trace.FirstFrame || frame > _trace.LastFrame) throw new InvalidDataException("Outside frozen trace range.");
            var bytes = await Task.Run(() => _decoder.Read(_trace.SourcePath, frame, expectedSha256: _trace.SourceSha256));
            if (_disposed || generation != _generation) return;
            var bitmap = CameraFrameMaterial.Bitmap(bytes);
            if (_run is not null && (bitmap.PixelWidth != _run.Metadata.Width || bitmap.PixelHeight != _run.Metadata.Height)) throw new InvalidDataException("Decoded resolution differs from the frozen run.");
            var plane = (Grid)_image.Parent; plane.Width = bitmap.PixelWidth; plane.Height = bitmap.PixelHeight;
            _overlay.Width = bitmap.PixelWidth; _overlay.Height = bitmap.PixelHeight; _image.Source = bitmap; DisplayedFrame = frame;
            if (_trace.MeasurementSettingsJson.Length > 0)
            {
                var settings = JsonDefaults.Deserialize<TrackingSettings>(_trace.MeasurementSettingsJson); settings.Validate();
                var pool = new Ellipse { Width = 2 * settings.Rx * bitmap.PixelWidth, Height = 2 * settings.Ry * bitmap.PixelHeight, Stroke = Brushes.SkyBlue, StrokeThickness = 2 };
                Canvas.SetLeft(pool, (settings.Cx - settings.Rx) * bitmap.PixelWidth); Canvas.SetTop(pool, (settings.Cy - settings.Ry) * bitmap.PixelHeight); _overlay.Children.Add(pool);
                if (settings.Platform is { } site)
                {
                    var ry = site.Radius * bitmap.PixelWidth * settings.Ry * bitmap.PixelHeight / (settings.Rx * bitmap.PixelWidth);
                    var marker = new Ellipse { Width = 2 * site.Radius * bitmap.PixelWidth, Height = 2 * ry, Stroke = Brushes.Gold, StrokeThickness = 2 };
                    Canvas.SetLeft(marker, (site.X - site.Radius) * bitmap.PixelWidth); Canvas.SetTop(marker, site.Y * bitmap.PixelHeight - ry); _overlay.Children.Add(marker);
                }
            }
            var point = _run?.Points.SingleOrDefault(p => p.Frame == frame);
            if (point is { Accepted: true, X: int x, Y: int y })
            {
                var marker = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.OrangeRed, StrokeThickness = 2 }; Canvas.SetLeft(marker, x - 6); Canvas.SetTop(marker, y - 6); _overlay.Children.Add(marker);
            }
            _status.Text = point is null ? "Exact authenticated source frame; no scenario coordinate marker supplied." : $"Exact authenticated source frame {frame}; frozen coordinate status {point.Status}. Accepted coordinates remain conditional on their inspection contract.";
            if (_trace.OutlierFrame == frame && _trace.TransformEvidenceJson.Length > 0)
            {
                var coordinate = JsonDefaults.Deserialize<TransformedCoordinate>(_trace.TransformEvidenceJson);
                if (coordinate.Frame != frame) throw new InvalidDataException("Transform evidence belongs to another source frame.");
                _status.Text += $" Selected producer outlier transform at source frame{coordinate.Frame}: room({coordinate.RoomXcm:0.####},{coordinate.RoomYcm:0.####})cm; target({coordinate.TargetXcm:0.####},{coordinate.TargetYcm:0.####})cm. Absent target coordinates stay withheld; room and target are distinct.";
            }
        }
        catch (Exception) { if (!_disposed && generation == _generation) { _image.Source = null; _overlay.Children.Clear(); DisplayedFrame = null; _status.Text = "Source trace held: source/run/frame integrity unavailable. No fabricated marker."; } }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; ++_generation; _decoder.Dispose(); }
}
