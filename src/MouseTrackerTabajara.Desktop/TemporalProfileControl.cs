using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

public sealed class TemporalProfileControl : UserControl
{
    public TemporalProfileControl(TemporalStudy profile, StudyPlan plan, bool blinded, Action<TemporalBinProfile> trace, Action<TemporalVisit> traceVisit)
    {
        var body = new StackPanel { Margin = new Thickness(12) };
        var details = new Expander { Header = "Selected profile point: expected trial contributions and endpoint holds", IsExpanded = true };
        void Select(TemporalAnimalBin point)
        {
            var rows = new StackPanel();
            foreach (var trial in point.Trials)
            {
                rows.Children.Add(Text((blinded ? "Opaque animal/trial contribution" : trial.AnimalId + " · " + trial.TrialId) +
                    $" · order {trial.Order}; source frames {trial.FirstFrame?.ToString() ?? "unavailable"}–{trial.LastFrame?.ToString() ?? "unavailable"}; declared bin {trial.StartSeconds:0.###}–{trial.EndSeconds:0.###} s from comparison start ({plan.Contexts.Single(c => c.Id == trial.ContextId).ComparisonStartSeconds:0.###} s after release); accepted positions {trial.KnownSeconds:0.###} s; adjacent motion {trial.AdjacentMotionSeconds:0.###} s; unknown positions {trial.UnknownFraction:P1}; no trial exposure after termination {trial.AbsentExposureFraction:P1}"));
                if (!trial.Holds.IsEmpty) rows.Children.Add(Text(blinded ? "Some endpoint/source evidence is held; detailed allocation text hidden." : string.Join("\n", trial.Holds)));
                if (!trial.Notes.IsEmpty) rows.Children.Add(Text(blinded ? "Nonblocking protocol annotations are recorded." : string.Join("\n", trial.Notes.Select(n => n.Text))));
                var source = new Button { Content = "Open frozen temporal trial/bin source", Tag = trial, IsEnabled = trial.Recording is not null, Margin = new Thickness(4) };
                source.Click += (_, _) => trace(trial); rows.Children.Add(source);
            }
            details.Content = new ScrollViewer { Content = rows, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        body.Children.Add(Text("Prespecified temporal profiles and separate across-day observations", 22));
        body.Children.Add(Text(profile.Interpretation));
        body.Children.Add(Text("Bin occupancy: accepted site-region time / declared exposure (unknown mass separate). Proximity/wall: accepted-position time. Speed: adjacent accepted-motion time; gaps are not bridged. Units: normalized pool diameters and seconds; not measured calibration or accuracy proof."));
        foreach (var reason in profile.Holds) body.Children.Add(Text(blinded ? "Some contexts have no prespecified temporal bins; profile held." : reason));
        var endpoints = new (string Caption, string Units, Func<TemporalAnimalBin, double?> Value, bool Fraction)[]
        {
            ("Site-region occupancy", "fraction of declared bin exposure", p => p.Occupancy, true),
            ("Mean platform proximity", "pool diameters; known position-time denominator", p => p.Proximity, false),
            ("Adjacent accepted-motion speed", "pool diameters/s; known adjacent-motion denominator", p => p.Speed, false),
            ("Wall behavior", "fraction of accepted position time", p => p.Wall, true)
        };
        foreach (var context in profile.AnimalBins.GroupBy(p => p.ContextId))
        {
            var contextNumber = Array.FindIndex(plan.Contexts.ToArray(), c => c.Id == context.Key) + 1;
            var scope = plan.Contexts.Single(c => c.Id == context.Key);
            body.Children.Add(Text((blinded ? "Context " + contextNumber.ToString("000") : context.Key) + $" · {scope.Phase} · day {scope.Day} · declared exposure {scope.ComparisonDurationSeconds:0.###} s; comparison [{scope.ComparisonStartSeconds:0.###}, {scope.ComparisonStartSeconds + scope.ComparisonDurationSeconds:0.###}) s after release", 18));
            var panels = new WrapPanel();
            foreach (var endpoint in endpoints)
                panels.Children.Add(Plot(context.ToArray(), plan, blinded, endpoint.Caption, endpoint.Units, endpoint.Value, false, false, Select,
                    endpoint.Fraction ? 1 : Math.Max(.0001, profile.AnimalBins.Concat(profile.AnimalDays).Select(endpoint.Value).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(1).Max() * 1.05),
                    Math.Max(1, profile.AnimalBins.Select(p => p.EndSeconds).DefaultIfEmpty(1).Max())));
            body.Children.Add(panels);
            var individualRows = new StackPanel();
            foreach (var point in context.OrderBy(p => p.AnimalId).ThenBy(p => p.StartSeconds))
            {
                var number = Array.FindIndex(plan.Animals.ToArray(), a => a.Id == point.AnimalId) + 1;
                var row = new Button { Content = (blinded ? $"Animal {number:000}" : point.AnimalId) + $" · {point.StartSeconds:0.###}–{point.EndSeconds:0.###} s · unknown {point.UnknownFraction:P1}; inspect all trial contributions", Tag = point, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(3) };
                row.Click += (_, _) => Select(point); individualRows.Children.Add(row);
            }
            body.Children.Add(new Expander { Header = "Every individual bin, including overlapping and held points", Content = new ScrollViewer { Content = individualRows, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
            if (!blinded)
            {
                foreach (var group in context.GroupBy(p => p.Group))
                {
                    var lines = new List<string>();
                    foreach (var bin in group.GroupBy(p => (p.StartSeconds, p.EndSeconds)).OrderBy(g => g.Key.StartSeconds))
                        foreach (var endpoint in endpoints)
                        {
                            var eligible = bin.Where(p => endpoint.Value(p).HasValue).ToArray();
                            lines.Add($"{endpoint.Caption} {bin.Key.StartSeconds:0.###}–{bin.Key.EndSeconds:0.###} s: " +
                                (eligible.Length > 0 ? $"{eligible.Average(p => endpoint.Value(p)!.Value):0.###}; {eligible.Length} mice contribute, {bin.Count() - eligible.Length} held" : "held; no eligible mouse value") + "; equal-mouse mean after equal-trial means.");
                        }
                    body.Children.Add(new Expander { Header = group.Key + " · explicit contributing/held temporal denominators", Content = Text(string.Join("\n", lines)) });
                }
            }
        }
        body.Children.Add(Text("Across-day individual observations", 20));
        var dayPanels = new WrapPanel();
        foreach (var endpoint in endpoints)
            dayPanels.Children.Add(Plot(profile.AnimalDays.ToArray(), plan, blinded, endpoint.Caption, endpoint.Units, endpoint.Value, true, profile.ConnectWithinAnimal, Select,
                endpoint.Fraction ? 1 : Math.Max(.0001, profile.AnimalBins.Concat(profile.AnimalDays).Select(endpoint.Value).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(1).Max() * 1.05),
                Math.Max(1, profile.AnimalDays.Select(p => (double)p.Day).DefaultIfEmpty(1).Max())));
        body.Children.Add(dayPanels);
        body.Children.Add(details);
        body.Children.Add(Text("First site-region visits: exact observations, censoring and indeterminate histories remain separate. Censored/held values are not averaged as successful visits.", 16));
        foreach (var visit in profile.Visits.OrderBy(v => v.Day).ThenBy(v => v.Order))
        {
            var number = Array.FindIndex(plan.Animals.ToArray(), a => a.Id == visit.AnimalId) + 1;
            var label = blinded ? $"Animal {number:000}" : visit.AnimalId;
            var state = visit.History == EventHistory.ObservedExact ? $"observed site visit {visit.ExactSeconds:0.###} s"
                : visit.History == EventHistory.RightCensoredComplete ? $"right-censored at {visit.CensorSeconds:0.###} s (no observed visit)" : "visit history held/indeterminate";
            var offset = plan.Contexts.Single(c => c.Id == visit.ContextId).ComparisonStartSeconds;
            var button = new Button { Content = $"{label} · day {visit.Day} · trial {visit.Order} · {state} from comparison start ({offset:0.###} s after release)", Tag = visit, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(3), IsEnabled = visit.Recording is not null };
            button.Click += (_, _) => traceVisit(visit); body.Children.Add(button);
        }
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private static UIElement Plot(TemporalAnimalBin[] points, StudyPlan plan, bool blinded, string caption, string units, Func<TemporalAnimalBin, double?> value, bool days, bool connect, Action<TemporalAnimalBin> select, double maximum, double xMax)
    {
        var panel = new StackPanel { Width = 450, Margin = new Thickness(8) }; panel.Children.Add(Text(caption, 17)); panel.Children.Add(Text(units));
        var canvas = new Canvas { Width = 430, Height = 265, Background = Brushes.White };
        canvas.Children.Add(new Line { X1 = 35, Y1 = 195, X2 = 415, Y2 = 195, Stroke = Brushes.Gray });
        canvas.Children.Add(new Line { X1 = 35, Y1 = 15, X2 = 35, Y2 = 195, Stroke = Brushes.Gray });
        var offset = points.Length == 0 ? 0 : plan.Contexts.Single(c => c.Id == points[0].ContextId).ComparisonStartSeconds;
        var axis = Text($"0–{maximum:0.###} {units}; x: {(days ? "planned day" : $"seconds from comparison start ({offset:0.###} s after release)")} 0–{xMax:0.###}"); axis.Width = 425; Canvas.SetTop(axis, 221); canvas.Children.Add(axis);
        for (var i = 0; i <= 2; i++)
        {
            var y = 190 - i * 82.5;
            var label = new TextBlock { Text = (maximum * i / 2).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), FontSize = 10, Width = 30, TextAlignment = TextAlignment.Right };
            Canvas.SetLeft(label, 0); Canvas.SetTop(label, y - 7); canvas.Children.Add(label);
            var tick = new TextBlock { Text = (xMax * i / 2).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), FontSize = 10, Width = 30, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(tick, 20 + i * 182.5); Canvas.SetTop(tick, 201); canvas.Children.Add(tick);
        }
        foreach (var animal in points.GroupBy(p => p.AnimalId))
        {
            var number = Array.FindIndex(plan.Animals.ToArray(), a => a.Id == animal.Key) + 1;
            var brush = new SolidColorBrush(Color.FromRgb((byte)(30 + number * 37 % 180), (byte)(70 + number * 19 % 150), (byte)(90 + number * 53 % 130)));
            System.Windows.Point? previous = null; TemporalAnimalBin? prior = null;
            foreach (var point in animal.OrderBy(p => p.Day).ThenBy(p => p.StartSeconds))
            {
                var measurement = value(point); var x = 35 + (days ? point.Day : (point.StartSeconds + point.EndSeconds) / 2) / xMax * 365;
                var y = measurement.HasValue ? 190 - measurement.Value / maximum * 165 : 195;
                var position = new System.Windows.Point(x, y);
                // Only explicitly repeated identities and compatible context windows can connect across days.
                var compatible = prior is not null && point.Day > prior.Day && plan.Contexts.Single(c => c.Id == prior.ContextId).CommonCoordinateReference == plan.Contexts.Single(c => c.Id == point.ContextId).CommonCoordinateReference &&
                    plan.Contexts.Single(c => c.Id == prior.ContextId).Orientation == plan.Contexts.Single(c => c.Id == point.ContextId).Orientation &&
                    plan.Contexts.Single(c => c.Id == prior.ContextId).ComparisonStartSeconds == plan.Contexts.Single(c => c.Id == point.ContextId).ComparisonStartSeconds &&
                    (point.Phase == TrialMode.Training || (prior.StartSeconds == point.StartSeconds && prior.EndSeconds == point.EndSeconds));
                if (connect && compatible && previous.HasValue && measurement.HasValue)
                    canvas.Children.Add(new Line { X1 = previous.Value.X, Y1 = previous.Value.Y, X2 = x, Y2 = y, Stroke = brush, StrokeThickness = 1.5, Tag = "within-mouse-day-line" });
                var button = new Button { Width = 14, Height = 14, Padding = new Thickness(0), Content = measurement.HasValue ? "●" : "?", Foreground = brush, Tag = point,
                    ToolTip = (blinded ? $"Animal {number:000}" : point.AnimalId) + $" · day {point.Day} · {point.StartSeconds:0.###}–{point.EndSeconds:0.###} s from comparison start ({plan.Contexts.Single(c => c.Id == point.ContextId).ComparisonStartSeconds:0.###} s after release) · " + (measurement.HasValue ? measurement.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "endpoint held") + $" · unknown {point.UnknownFraction:P1}" };
                button.Click += (_, _) => select(point);
                Canvas.SetLeft(button, x - 7); Canvas.SetTop(button, y - 7); canvas.Children.Add(button);
                previous = measurement.HasValue ? position : null; prior = point;
            }
        }
        panel.Children.Add(canvas); return panel;
    }
    private static TextBlock Text(string value, double size = 12) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
}
