using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MouseTrackerTabajara.SpatialViews;
using MouseTrackerTabajara.SpatialViews.Wpf;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            var grid = new GridDefinition(16, 16, -1, -1, 1, 1, "pool radii", "synthetic-room-reference", "room-N");
            var scope = new SpatialComparisonScope(grid, "probe-day1", "confirmed-release-fixed", 10);
            var inputs = Enumerable.Range(0, 30).Select(i =>
            {
                var frames = Enumerable.Range(0, 100).Select(f => new SpatialFrame(f, f / 10d, (f + 1) / 10d,
                    f is >= 25 and <= 30 ? null : Math.Cos(f / 12d + i) * .75,
                    f is >= 25 and <= 30 ? null : Math.Sin(f / 15d + i) * .6, f is < 25 or > 30)).ToImmutableArray();
                var trial = SpatialCalculator.Trial(new("SECRET_RECORDING_" + i, $"run-{i:000}", i.ToString("x64"), "synthetic-snapshot1", "transform1",
                    scope.ContextKey, scope.WindowBasis, 10, true, true, true, grid, frames));
                return new AnimalSpatialInput("ANIMAL_SECRET_" + i, "Mouse " + (i + 1).ToString("00"), i < 15 ? "TREATMENT_SENTINEL_A" : "TREATMENT_SENTINEL_B", true, true, [trial.Input.TrialId], [trial]);
            }).Append(new("ANIMAL_SECRET_MISSING", "Mouse 31", "TREATMENT_SENTINEL_B", true, true, ["missing-planned-trial"], [])).ToImmutableArray();
            var study = SpatialCalculator.Study(inputs, scope); var control = new AtlasControl(); control.Load(study);
            if (control.RecordingCardCount != 30 || control.MissingAnimalCardCount != 1) throw new InvalidOperationException("Atlas omitted a recording or planned missing animal.");
            AtlasNavigationEventArgs? navigation = null; control.FrameRequested += (_, e) => navigation = e;
            Layout(control, 1280, 950);
            var button = Visuals(control).OfType<Button>().First(b => Equals(b.Content, "Review recording"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (navigation is null || navigation.RunId != "run-000" || navigation.SourceSha256 != new string('0', 64) || navigation.Frame != 0 || navigation.SnapshotRevision != "synthetic-snapshot1")
                throw new InvalidOperationException("Atlas navigation did not retain the exact source snapshot/frame.");
            control.Blinded = true; Layout(control, 1050, 700);
            var text = string.Join("\n", Visuals(control).OfType<TextBlock>().Select(t => t.Text));
            if (text.Contains("ANIMAL_SECRET", StringComparison.Ordinal) || text.Contains("SECRET_RECORDING", StringComparison.Ordinal) || text.Contains("TREATMENT_SENTINEL", StringComparison.Ordinal) || text.Contains("Mouse 01", StringComparison.Ordinal))
                throw new InvalidOperationException("Blinded native atlas leaked allocation or identifying labels.");
            var stored = study.Animals[0].Input.Trials[0];
            control.NavigateToFirstFrame(stored with { Input = stored.Input with { Frames = [new(999, 0, 10, 0, 0, true)] } });
            if (navigation!.Frame != 0) throw new InvalidOperationException("Navigation trusted a supplied frame instead of its frozen atlas.");
            control.FrameRequested += (_, _) => throw new IOException("PRIVATE_SOURCE_SENTINEL");
            Visuals(control).OfType<Button>().First(b => Equals(b.Content, "Review recording")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (control.LastNavigationError is null || Visuals(control).OfType<TextBlock>().Any(t => t.Text.Contains("PRIVATE_SOURCE_SENTINEL", StringComparison.Ordinal)))
                throw new InvalidOperationException("Blinded source failure was lost or leaked its path.");
            var friendlyInputs = inputs.Select(a => a with { Group = a.Group.EndsWith("_A", StringComparison.Ordinal) ? "Group A" : "Group B" }).ToImmutableArray();
            control.Load(SpatialCalculator.Study(friendlyInputs, scope)); control.Blinded = false;
            var root = new DockPanel { Background = Brushes.White }; var label = new TextBlock { Text = "Mouse Tracker Tabajara — native atlas component preview · SYNTHETIC DATA", FontSize = 18, Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(label, Dock.Top); root.Children.Add(label); root.Children.Add(control); Layout(root, 1280, 950);
            var output = Path.GetFullPath(args.FirstOrDefault() ?? "atlas-component.png"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var bitmap = new RenderTargetBitmap(1280, 950, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(output)) encoder.Save(file);
            Console.WriteLine("PASS:30 recording cards,1 missing animal card, exact navigation, metadata blinding, frozen-frame protection and visible/sanitized failure.");
            Console.WriteLine("Synthetic WPF component image: " + output); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Layout(FrameworkElement element, double width, double height) { element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout(); }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
