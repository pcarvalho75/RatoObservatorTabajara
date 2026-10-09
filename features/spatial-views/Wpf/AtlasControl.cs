using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MouseTrackerTabajara.SpatialViews.Wpf;

public sealed class AtlasNavigationEventArgs : EventArgs
{
    public required string RunId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string SnapshotRevision { get; init; }
    public required int Frame { get; init; }
}

/// <summary>Native collection view. Source playback is delegated to the host by exact run/revision/frame.</summary>
public sealed class AtlasControl : UserControl
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(20, 62, 89));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(86, 111, 127));
    private readonly StackPanel _body = new() { Margin = new Thickness(20) };
    private readonly TextBlock _navigationStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted };
    private SpatialStudy? _study;
    private bool _blinded;
    public event EventHandler<AtlasNavigationEventArgs>? FrameRequested;
    public int RecordingCardCount { get; private set; }
    public int MissingAnimalCardCount { get; private set; }
    public Exception? LastNavigationError { get; private set; }
    public bool Blinded { get => _blinded; set { _blinded = value; Rebuild(); } }
    public AtlasControl() => Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(243, 247, 250)) };
    public void Load(SpatialStudy study) { ArgumentNullException.ThrowIfNull(study); _study = study; Rebuild(); }

    public void NavigateToFirstFrame(TrialSpatialMap map)
    {
        var stored = _study?.Animals.SelectMany(a => a.Input.Trials).FirstOrDefault(t => t.Input.RunId == map.Input.RunId && t.Input.SnapshotRevision == map.Input.SnapshotRevision && t.Input.SourceSha256 == map.Input.SourceSha256);
        if (stored is null)
            throw new InvalidOperationException("Recording is outside this frozen atlas.");
        var first = stored.Input.Frames.FirstOrDefault(f => f.EndSeconds > 0 && f.StartSeconds < stored.Input.PlannedSeconds) ?? stored.Input.Frames.FirstOrDefault();
        if (first is null) throw new InvalidOperationException("This recording has no source frame to navigate to.");
        FrameRequested?.Invoke(this, new() { RunId = stored.Input.RunId, SourceSha256 = stored.Input.SourceSha256, SnapshotRevision = stored.Input.SnapshotRevision, Frame = first.Frame });
    }

    private void Rebuild()
    {
        _body.Children.Clear(); RecordingCardCount = 0; MissingAnimalCardCount = 0;
        if (_study is null) { _body.Children.Add(Text("Import a study to inspect its individual trajectories.", 18)); return; }
        _body.Children.Add(Text(_blinded ? "Blinded trajectory review" : "Every animal, every recording", 26, true));
        var recordings = _study.Animals.Sum(a => a.Input.Trials.Length);
        var identities = _study.Animals.Where(a => a.Input.IdentityConfirmed).Select(a => a.Input.AnimalId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        _body.Children.Add(Text($"{recordings} recordings  ·  {identities} confirmed animal IDs  ·  {_study.Animals.Count(a => a.Eligible)} eligible animal maps", 13));
        _body.Children.Add(Text("Position time uses the planned exposure. Unknown time remains explicit. A frozen view requires recomputation after edits.", 12));
        _navigationStatus.Text = ""; _body.Children.Add(_navigationStatus);
        if (!_blinded && !_study.Groups.IsEmpty)
        {
            _body.Children.Add(Text("Comparable group maps", 20, true)); var maps = new WrapPanel();
            foreach (var group in _study.Groups)
            {
                var contents = new StackPanel(); contents.Children.Add(Text(group.Group, 17, true));
                contents.Children.Add(Text($"{group.IncludedAnimalIds.Length} animals contribute  ·  {group.HeldAnimalIds.Length} held", 12));
                contents.Children.Add(DrawMap(group, _study.SharedKnownFractionMaximum));
                contents.Children.Add(Text($"Unknown position time: {group.UnknownFraction:P1}  ·  planned exposure: {group.PlannedSeconds:0.##} s", 11));
                contents.Children.Add(Text($"Shared color range: 0–{_study.SharedKnownFractionMaximum:P1} of planned time per cell", 10));
                maps.Children.Add(Card(contents));
            }
            _body.Children.Add(maps);
        }
        _body.Children.Add(Text("Individual recording atlas", 20, true)); var atlas = new WrapPanel();
        var items = _study.Animals.SelectMany(a => a.Input.Trials.Select(t => (Animal: a, Trial: t))).OrderBy(p => p.Trial.Input.RunId, StringComparer.Ordinal).ToArray();
        foreach (var (animal, trial) in items)
        {
            RecordingCardCount++; var contents = new StackPanel();
            contents.Children.Add(Text(_blinded ? $"Recording {RecordingCardCount:000}" : animal.Input.DisplayAlias, 16, true));
            if (!_blinded) contents.Children.Add(Text(animal.Input.Group + "  ·  " + trial.Input.ContextKey, 11));
            contents.Children.Add(DrawTrajectory(trial));
            contents.Children.Add(Text($"Unknown position time: {trial.UnknownFraction:P1}", 11));
            contents.Children.Add(Text(animal.Eligible ? "Contributes to the animal map" : _blinded ? "Study comparison held; allocation details hidden during review." : string.Join(" ", animal.HoldReasons), 10));
            var review = new Button { Content = "Review recording", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6), IsEnabled = !trial.Input.Frames.IsEmpty };
            review.Click += (_, _) =>
            {
                try { LastNavigationError = null; _navigationStatus.Text = ""; NavigateToFirstFrame(trial); }
                catch (Exception ex) { LastNavigationError = ex; _navigationStatus.Text = _blinded ? "Unable to open this recording. Check the study and source integrity." : ex.Message; }
            };
            contents.Children.Add(review); atlas.Children.Add(Card(contents));
        }
        foreach (var animal in _study.Animals.Where(a => a.Input.Trials.IsEmpty))
        {
            MissingAnimalCardCount++; var contents = new StackPanel();
            contents.Children.Add(Text(_blinded ? $"Missing recording set {MissingAnimalCardCount:000}" : animal.Input.DisplayAlias, 16, true));
            contents.Children.Add(Text("No recordings imported for this planned animal.", 12));
            if (!_blinded) contents.Children.Add(Text(string.Join(" ", animal.HoldReasons), 10));
            atlas.Children.Add(Card(contents));
        }
        _body.Children.Add(atlas);
        _body.Children.Add(Text("Descriptive development view. Tracking review does not establish independent position accuracy or a biological group conclusion.", 11));
    }

    private static Canvas PlotCanvas() => new() { Width = 260, Height = 180, ClipToBounds = true, Margin = new Thickness(0, 10, 0, 8), Background = new SolidColorBrush(Color.FromRgb(229, 241, 248)) };
    private static System.Windows.Point Position(GridDefinition grid, double x, double y) => new((x - grid.Left) / (grid.Right - grid.Left) * 260, (y - grid.Top) / (grid.Bottom - grid.Top) * 180);
    private static Canvas DrawTrajectory(TrialSpatialMap map)
    {
        var canvas = PlotCanvas();
        foreach (var segment in SpatialCalculator.Trajectory(map.Input))
        {
            var line = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(205, 98, 40)), StrokeThickness = 1.7 };
            foreach (var vertex in segment.Vertices) line.Points.Add(Position(map.Input.Grid, vertex.X, vertex.Y));
            if (segment.Vertices.Length == 1)
            {
                var p = line.Points[0]; var dot = new Ellipse { Width = 4, Height = 4, Fill = line.Stroke }; Canvas.SetLeft(dot, p.X - 2); Canvas.SetTop(dot, p.Y - 2); canvas.Children.Add(dot);
            }
            else canvas.Children.Add(line);
        }
        return canvas;
    }
    private static Canvas DrawMap(SpatialGroupMap map, double maximum)
    {
        var canvas = PlotCanvas(); var width = 260d / map.Grid.Columns; var height = 180d / map.Grid.Rows;
        for (var i = 0; i < map.KnownFractions.Length; i++)
        {
            var intensity = maximum > 0 ? Math.Clamp(map.KnownFractions[i] / maximum, 0, 1) : 0;
            var color = Color.FromRgb((byte)(240 - 216 * intensity), (byte)(247 - 126 * intensity), (byte)(250 - 88 * intensity));
            var cell = new Rectangle { Width = width + .1, Height = height + .1, Fill = new SolidColorBrush(color) };
            Canvas.SetLeft(cell, i % map.Grid.Columns * width); Canvas.SetTop(cell, i / map.Grid.Columns * height); canvas.Children.Add(cell);
        }
        return canvas;
    }
    private static Border Card(UIElement child) => new() { Width = 294, Padding = new Thickness(16), Margin = new Thickness(0, 0, 12, 12), Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(211, 225, 232)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = child };
    private static TextBlock Text(string value, double size, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = size >= 16 ? Ink : Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
}
