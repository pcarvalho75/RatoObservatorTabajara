using System.Globalization;
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

public sealed class SensitivityViewRequest
{
    public required string RecordingLabel { get; init; }
    public required string ScenarioLabel { get; init; }
    public required string Status { get; init; }
    public required string VideoPath { get; init; }
    public required string SourceSha256 { get; init; }
    public required VideoMetadata Metadata { get; init; }
    public required TrackingSettings PreservedSettings { get; init; }
    public required IReadOnlyList<TrackPoint> BaselinePoints { get; init; }
    public IReadOnlyList<TrackPoint> ScenarioPoints { get; init; } = [];
    public IReadOnlyList<ManualCorrection> Corrections { get; init; } = [];
    public bool AllowPixels { get; init; }
    public bool AllowScenarioMarkers { get; init; }
    public bool AllowBaselineMarkers { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>Exact frozen source frames for one detector-sensitivity case. Pixels are not redacted.</summary>
public sealed class SensitivitySourceViewer : Window
{
    private readonly FrameDecoderPool _decoder = new(1);
    private readonly Grid _plane = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _identity = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _seek = new() { Width = 70, Text = "0" };
    private SensitivityViewRequest? _request;
    private string _state = "scenario";
    private int _generation;
    private bool _closed;
    public Task PendingFrame { get; private set; } = Task.CompletedTask;
    public int? DisplayedFrame { get; private set; }
    public string? DisplayedSourceSha256 { get; private set; }
    public BitmapSource? DisplayedBitmap => _image.Source as BitmapSource;
    public Canvas SourceOverlay => _overlay;
    public string StatusText => _status.Text;
    public string IdentityText => _identity.Text;
    public string State => _state;
    public bool Held { get; private set; }
    public bool ResourcesReleased { get; private set; }
    public FrameDecoderStatistics DecoderStatistics => _decoder.Statistics;

    public SensitivitySourceViewer(bool blinded)
    {
        Title = "Mouse Tracker Tabajara — Sensitivity source";
        Width = 1100;
        Height = 800;
        MinWidth = 1050;
        MinHeight = 700;
        Background = new SolidColorBrush(Color.FromRgb(243, 247, 250));
        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Frozen scenario source", FontSize = 22, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Baseline, scenario, and correction are separate. A missing path is not filled in. The decoder interval is not a biological release.", TextWrapping = TextWrapping.Wrap });
        var states = new WrapPanel();
        states.Children.Add(StateButton("Baseline path", "baseline"));
        states.Children.Add(StateButton("Scenario path", "scenario"));
        states.Children.Add(StateButton("Correction state", "correction"));
        var previous = new Button { Content = "Previous source frame" };
        previous.Click += (_, _) => { if (DisplayedFrame is int frame) ShowFrame(frame - 1); };
        var next = new Button { Content = "Next source frame" };
        next.Click += (_, _) => { if (DisplayedFrame is int frame) ShowFrame(frame + 1); };
        var seek = new Button { Content = "Seek exact frame" };
        seek.Click += (_, _) =>
        {
            if (int.TryParse(_seek.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame)) ShowFrame(frame);
            else Hold("Enter an integer source frame.");
        };
        states.Children.Add(previous);
        states.Children.Add(next);
        states.Children.Add(_seek);
        states.Children.Add(seek);
        header.Children.Add(states);
        header.Children.Add(_identity);
        header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        _plane.Children.Add(_image);
        _plane.Children.Add(_overlay);
        root.Children.Add(new Border { Background = Brushes.DarkSlateGray, Child = new Viewbox { Stretch = Stretch.Uniform, Child = _plane } });
        Content = root;
        if (blinded) _identity.Text = "Blinded source review. Labels stay opaque. Pixels are not redacted.";
        Closed += (_, _) => Release();
    }

    public void Show(SensitivityViewRequest request)
    {
        _request = request;
        _generation++;
        Held = false;
        _state = "scenario";
        _identity.Text = $"{request.RecordingLabel} · {request.ScenarioLabel} · {request.Status}";
        if (!request.AllowPixels)
        {
            Hold(string.IsNullOrWhiteSpace(request.Note) ? "Source review held. Markers were not invented." : request.Note);
            return;
        }
        ShowFrame(0);
    }

    public void ShowBaseline() => SetState("baseline");
    public void ShowScenario() => SetState("scenario");
    public void ShowCorrection() => SetState("correction");
    public void ShowFrame(int frame) => PendingFrame = ShowFrameAsync(frame);

    public async Task ShowFrameAsync(int frame)
    {
        var request = _request;
        if (request is null || _closed) return;
        if (!request.AllowPixels) { Hold(request.Note); return; }
        var generation = ++_generation;
        try
        {
            if (frame < 0 || frame >= request.Metadata.FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
            var path = request.VideoPath;
            var expected = request.SourceSha256;
            var bytes = await Task.Run(() => _decoder.Read(path, frame, expectedSha256: expected)).ConfigureAwait(true);
            if (_closed || generation != _generation || !ReferenceEquals(_request, request)) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _plane.Width = request.Metadata.Width;
            _plane.Height = request.Metadata.Height;
            _image.Source = bitmap;
            DisplayedFrame = frame;
            DisplayedSourceSha256 = expected;
            _seek.Text = frame.ToString(CultureInfo.InvariantCulture);
            Held = false;
            Draw(request, frame);
        }
        catch (Exception)
        {
            if (!_closed && generation == _generation) Hold("Source or frame identity did not match the frozen case. Pixels and markers were withheld.");
        }
    }

    private void SetState(string state)
    {
        _state = state;
        if (_request is null || Held || DisplayedFrame is not int frame || ! _request.AllowPixels) return;
        Draw(_request, frame);
    }

    private void Draw(SensitivityViewRequest request, int frame)
    {
        _overlay.Children.Clear();
        DrawPool(request);
        if (_state == "correction")
        {
            if (request.Corrections.Count == 0)
            {
                _status.Text = "Correction state: this automatic case has no correction evidence. Markers were not invented.";
                return;
            }
            foreach (var correction in request.Corrections)
            {
                if (correction.Frame != frame || correction.X is not int correctedX || correction.Y is not int correctedY) continue;
                AddMarker(correctedX, correctedY, "correction-marker", Brushes.MediumPurple);
            }
            _status.Text = "Correction state. These marks are not the scenario path and not a biological release.";
            return;
        }
        var scenarioState = _state == "scenario";
        if (scenarioState && !request.AllowScenarioMarkers)
        {
            _status.Text = string.IsNullOrWhiteSpace(request.Note) ? "Scenario markers withheld. No path was invented." : request.Note;
            return;
        }
        if (!scenarioState && !request.AllowBaselineMarkers)
        {
            _status.Text = "Baseline markers withheld. The frozen baseline did not validate.";
            return;
        }
        var points = scenarioState ? request.ScenarioPoints : request.BaselinePoints;
        DrawPath(points, scenarioState ? "scenario" : "baseline");
        var active = points.SingleOrDefault(point => point.Frame == frame);
        if (active is { Accepted: true, X: int acceptedX, Y: int acceptedY }) AddMarker(acceptedX, acceptedY, "active-source-marker", Brushes.LightGreen);
        else if (active is { Status: PointStatus.Jump or PointStatus.Ambiguous, TentativeX: int tentativeX, TentativeY: int tentativeY })
            AddMarker(tentativeX, tentativeY, "gap-marker", Brushes.Orange);
        var unknown = points.Count(point => point.Status is PointStatus.Missing or PointStatus.Jump or PointStatus.Ambiguous or PointStatus.Excluded);
        _status.Text = $"{(scenarioState ? "Scenario" : "Baseline")} frame {frame.ToString(CultureInfo.InvariantCulture)}. Accepted path only. Unknown or gap frames: {unknown.ToString(CultureInfo.InvariantCulture)}. Not a biological release.";
    }

    private void DrawPool(SensitivityViewRequest request)
    {
        var settings = request.PreservedSettings;
        var width = request.Metadata.Width;
        var height = request.Metadata.Height;
        var rx = settings.Rx * width;
        var ry = settings.Ry * height;
        var pool = new Ellipse { Width = Math.Max(1, 2 * rx), Height = Math.Max(1, 2 * ry), Stroke = Brushes.LightSkyBlue, StrokeThickness = 2, Tag = "preserved-pool" };
        Canvas.SetLeft(pool, settings.Cx * width - rx);
        Canvas.SetTop(pool, settings.Cy * height - ry);
        _overlay.Children.Add(pool);
    }

    private void DrawPath(IReadOnlyList<TrackPoint> points, string state)
    {
        Polyline? segment = null;
        TrackPoint? prior = null;
        foreach (var point in points.OrderBy(item => item.Frame))
        {
            if (!point.Accepted || point.X is not int x || point.Y is not int y) { segment = null; prior = null; continue; }
            if (segment is null || prior is null || point.Frame != prior.Frame + 1)
            {
                segment = new Polyline { Stroke = state == "scenario" ? Brushes.Coral : Brushes.White, StrokeThickness = 2, Tag = state + "-path" };
                _overlay.Children.Add(segment);
            }
            segment.Points.Add(new System.Windows.Point(x, y));
            prior = point;
        }
    }

    private void AddMarker(int x, int y, string tag, Brush stroke)
    {
        var marker = new Ellipse { Width = 18, Height = 18, Stroke = stroke, StrokeThickness = 2, Fill = Brushes.Transparent, Tag = tag };
        Canvas.SetLeft(marker, x - 9);
        Canvas.SetTop(marker, y - 9);
        _overlay.Children.Add(marker);
    }

    private Button StateButton(string content, string state)
    {
        var button = new Button { Content = content };
        button.Click += (_, _) => SetState(state);
        return button;
    }

    public void ClearSensitive()
    {
        _generation++;
        _request = null;
        Held = true;
        _image.Source = null;
        _overlay.Children.Clear();
        DisplayedFrame = null;
        DisplayedSourceSha256 = null;
        _identity.Text = "";
        _status.Text = "";
        _seek.Text = "";
    }

    private void Hold(string message)
    {
        _generation++;
        Held = true;
        _image.Source = null;
        _overlay.Children.Clear();
        DisplayedFrame = null;
        DisplayedSourceSha256 = null;
        _status.Text = message;
    }

    private void Release()
    {
        if (ResourcesReleased) return;
        _closed = true;
        _generation++;
        _decoder.Dispose();
        ResourcesReleased = true;
    }
}
