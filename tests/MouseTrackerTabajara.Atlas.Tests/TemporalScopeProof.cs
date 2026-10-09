using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Line = System.Windows.Shapes.Line;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using Xunit;

namespace MouseTrackerTabajara.Atlas.Tests;

internal static class TemporalScopeProof
{
    public static void Run(string directory, List<string> evidence)
    {
        var contexts = new[] {
            new PlannedContext { Id = "early", Day = 1, Phase = TrialMode.Probe, RequestedExposureSeconds = 30, ComparisonDurationSeconds = 10, TemporalBinEdges = [0, 10], ComparisonStartSeconds = 0 },
            new PlannedContext { Id = "late", Day = 2, Phase = TrialMode.Probe, RequestedExposureSeconds = 30, ComparisonDurationSeconds = 10, TemporalBinEdges = [0, 10], ComparisonStartSeconds = 20 }
        };
        var plan = new StudyPlan { UnitDesign = StudyUnitDesign.RepeatedTrialsWithinAnimals, DesignConfirmed = true,
            Animals = [new() { Id = "mouse", IdentityConfirmed = true }], Contexts = contexts.ToImmutableArray(),
            Trials = [new() { Id = "t1", AnimalId = "mouse", ContextId = "early" }, new() { Id = "t2", AnimalId = "mouse", ContextId = "late" }] };
        var bins = contexts.Select((c, i) => new TemporalBinProfile("mouse", c.Id, "t" + (i + 1), 1, c.Day, TrialMode.Probe,
            0, 10, 10, 0, 9.9, .2, .3, .4, .5, null, 0, 99, [], true, [], 0)).ToImmutableArray();
        var animals = bins.Select(b => new TemporalAnimalBin("mouse", "group", b.ContextId, b.Day, b.Phase, 0, 10, .2, .3, .4, .5, 0, [b], [])).ToImmutableArray();
        var profile = new TemporalStudy(bins, animals, animals, [], true, "Explicit repeated identity; windows remain distinct.", []);
        var control = new TemporalProfileControl(profile, plan, false, _ => { }, _ => { });
        control.Measure(new Size(1050, 700)); control.Arrange(new Rect(0, 0, 1050, 700)); control.UpdateLayout();
        Assert.DoesNotContain(Descendants(control).OfType<Line>(), line => Equals(line.Tag, "within-mouse-day-line"));
        var labels = Descendants(control).OfType<TextBlock>().Select(t => t.Text).ToArray();
        Assert.Contains(labels, text => text.Contains("comparison [20, 30) s after release", StringComparison.Ordinal));
        Assert.Contains(labels, text => text.Contains("seconds from comparison start (20 s after release)", StringComparison.Ordinal));
        Assert.DoesNotContain(labels, text => text.Contains("release-relative seconds", StringComparison.Ordinal));
        var sameWindow = plan with { Contexts = contexts.Select(c => c with { ComparisonStartSeconds = 0 }).ToImmutableArray() };
        var comparable = new TemporalProfileControl(profile, sameWindow, false, _ => { }, _ => { });
        comparable.Measure(new Size(1050, 700)); comparable.Arrange(new Rect(0, 0, 1050, 700)); comparable.UpdateLayout();
        Assert.Equal(4, Descendants(comparable).OfType<Line>().Count(line => Equals(line.Tag, "within-mouse-day-line")));
        var path = Path.Combine(directory, "shifted-scope.csv");
        TemporalProfileExport.Save(path, profile, plan, false, "frozen synthetic revision");
        var rows = File.ReadAllLines(path); var headers = rows[0].Split(',');
        var late = rows[2].Split(',').Select(cell => cell.Trim('"')).ToArray();
        Assert.Equal("20", late[Array.IndexOf(headers, "comparison_start_after_release_seconds")]);
        Assert.Equal("0", late[Array.IndexOf(headers, "bin_start_after_comparison_seconds")]);
        Assert.Equal("20", late[Array.IndexOf(headers, "bin_start_after_release_seconds")]);
        Assert.Equal("30", late[Array.IndexOf(headers, "bin_end_after_release_seconds")]);
        evidence.Add("PASS actual temporal control separates [0,10) and [20,30) release scopes, labels offset explicitly, joins only matched windows and exports both clocks.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
