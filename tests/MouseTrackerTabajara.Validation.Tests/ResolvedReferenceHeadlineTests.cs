using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Validation.Tests;

/// <summary>
/// Native headline for the accepted S14 consensus: reviewers may disagree while a valid consensus resolves the reference.
/// A missing raw automatic position is an unavailable producer join, not another unresolved reference.
/// </summary>
public sealed class ResolvedReferenceHeadlineTests(ITestOutputHelper output)
{
    private const string UnresolvedReferenceHold = "Independent reviewers disagree; unresolved reference is held.";

    [Fact]
    public void ValidConsensusAndUnresolvedReviewersStayDistinctForRawCorrectedAndBlindText()
    {
        Exception? error = null;
        var lines = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                Run(lines);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)));
        foreach (var line in lines) output.WriteLine(line);
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Run(List<string> lines)
    {
        using var fixture = new S14SplitBoundaryFixture();
        var independent = fixture.Assets[1];
        foreach (var target in new[] { ValidationMeasurementTarget.RawAutomatic, ValidationMeasurementTarget.InspectedCorrected })
        {
            var saved = fixture.Compare(target);
            SaveAcceptedConsensus(fixture, saved.Sampling);
            var report = IndependentValidationComparison.Compare(IndependentValidationSnapshot.Read(fixture.Store.DatabasePath), saved.Plan, saved.Sampling);
            foreach (var (role, truth) in S14SplitBoundaryFixture.Truth(target)) SplitBoundaryAuditTests.AssertSummary(report, role, truth);
            var resolved = report.Frames.Single(frame => frame.VideoId == independent.Id && frame.Frame == 19);
            var unresolved = report.Frames.Single(frame => frame.VideoId == independent.Id && frame.Frame == 16);
            Assert.True(resolved.Disagreement);
            Assert.Equal(3, resolved.ReferenceIds.Length);
            Assert.DoesNotContain(resolved.Holds, hold => hold.Contains("unresolved", StringComparison.OrdinalIgnoreCase));
            Assert.True(unresolved.Disagreement);
            Assert.Contains(unresolved.Holds, hold => hold.StartsWith(UnresolvedReferenceHold, StringComparison.Ordinal));
            Assert.Null(unresolved.PositionErrorPixels);
            if (target == ValidationMeasurementTarget.RawAutomatic)
            {
                Assert.True(resolved.MissedVisible);
                Assert.Null(resolved.PositionErrorPixels);
            }
            else
            {
                Assert.False(resolved.MissedVisible);
                Assert.Equal(0, resolved.PositionErrorPixels);
            }
            Present(report, fixture, blinded: false, surface => Inspect(surface, report, independent.Id, target, blinded: false, lines));
            Present(report, fixture, blinded: true, surface => Inspect(surface, report, independent.Id, target, blinded: true, lines));
        }
    }

    private static void Inspect(ValidationComparisonSurface surface, ValidationComparisonReport report, string independentId, ValidationMeasurementTarget target, bool blinded, List<string> lines)
    {
        var rows = surface.Results.Items.OfType<ValidationFrameRow>().ToArray();
        Assert.Equal(report.Frames.Length, rows.Length);
        foreach (var row in rows)
        {
            var unresolvedReference = row.Result.Holds.Any(hold => hold.StartsWith(UnresolvedReferenceHold, StringComparison.Ordinal));
            if (unresolvedReference)
            {
                Assert.Contains("unresolved disagreement", row.Headline, StringComparison.Ordinal);
                Assert.DoesNotContain("reference resolved", row.Headline, StringComparison.Ordinal);
            }
            else if (row.Result.Disagreement)
            {
                Assert.Contains("reference resolved", row.Headline, StringComparison.Ordinal);
                Assert.DoesNotContain("unresolved disagreement", row.Headline, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("unresolved disagreement", row.Headline, StringComparison.Ordinal);
                Assert.DoesNotContain("reference resolved", row.Headline, StringComparison.Ordinal);
            }
            Assert.Equal(row.VideoId, row.Result.VideoId);
            Assert.Equal(row.Frame, row.Result.Frame);
        }
        var producerRows = rows.Where(row => row.Headline.Contains("producer join unavailable", StringComparison.Ordinal)).ToArray();
        if (target == ValidationMeasurementTarget.RawAutomatic)
        {
            var only = Assert.Single(producerRows);
            Assert.Equal(independentId, only.VideoId);
            Assert.Equal(19, only.Frame);
            Assert.Contains("not joined", only.Headline, StringComparison.Ordinal);
            Assert.Contains("error held", only.Headline, StringComparison.Ordinal);
        }
        else Assert.Empty(producerRows);

        var summary = report.SplitSummaries.Single(role => role.Role == ValidationSplit.IndependentValidation);
        var expander = Descendants(surface).OfType<Expander>().Single();
        Assert.Contains(ValidationSplitSummaryView.Counts(summary), string.Join("\n", Texts(Logical(expander))), StringComparison.Ordinal);
        var card = Descendants(surface).OfType<FrameworkElement>().Single(element => AutomationProperties.GetName(element) == ValidationViewNames.RoleCardPrefix + "Independent validation");
        Assert.Equal(ValidationSplitSummaryView.Compact(summary), Descendants(card).OfType<TextBlock>().Single().Text);
        var visible = string.Join("\n", Texts(Descendants(surface)));
        Assert.Contains(target == ValidationMeasurementTarget.RawAutomatic ? "RAW AUTOMATIC" : "INSPECTED CORRECTED", visible, StringComparison.Ordinal);

        var resolvedText = ReadRow(surface, independentId, 19);
        var unresolvedText = ReadRow(surface, independentId, 16);
        Assert.Contains("exact frame 19", resolvedText.Headline, StringComparison.Ordinal);
        Assert.Contains("exact frame 16", unresolvedText.Headline, StringComparison.Ordinal);
        Assert.Contains("unresolved disagreement", unresolvedText.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("reference resolved", unresolvedText.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("producer join unavailable", unresolvedText.Headline, StringComparison.Ordinal);
        Assert.Contains("reference resolved", resolvedText.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolved disagreement", resolvedText.Headline, StringComparison.Ordinal);
        Assert.Contains("Disagreement True", resolvedText.Detail, StringComparison.Ordinal);
        Assert.Contains("Disagreement True", unresolvedText.Detail, StringComparison.Ordinal);
        if (target == ValidationMeasurementTarget.RawAutomatic)
        {
            Assert.Contains("producer join unavailable", resolvedText.Headline, StringComparison.Ordinal);
            Assert.Contains("Position error held", resolvedText.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("Position error 0 px", resolvedText.Detail, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("producer join unavailable", resolvedText.Headline, StringComparison.Ordinal);
            Assert.Contains("Position error 0 px", resolvedText.Detail, StringComparison.Ordinal);
            Assert.Contains("error 0 px", resolvedText.Headline, StringComparison.Ordinal);
        }
        if (blinded)
        {
            Assert.All(rows, row => Assert.StartsWith("Recording ", row.Headline, StringComparison.Ordinal));
            Assert.DoesNotContain("PRIVATE", visible, StringComparison.Ordinal);
            Assert.DoesNotContain(UnresolvedReferenceHold, visible, StringComparison.Ordinal);
            Assert.DoesNotContain("Adjudicated synthetic", visible, StringComparison.Ordinal);
            Assert.DoesNotContain("Synthetic disagreement", visible, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"\b(WT|KO)\b", visible);
            Assert.Contains("Recording exclusion is retained; allocation reason hidden.", visible, StringComparison.Ordinal);
            Assert.Contains("Reference/producer join held; inspect source and independent histories.", unresolvedText.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(UnresolvedReferenceHold, unresolvedText.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(UnresolvedReferenceHold, resolvedText.Tooltip, StringComparison.Ordinal);
            Assert.StartsWith("Recording ", resolvedText.Headline, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(UnresolvedReferenceHold, unresolvedText.Detail, StringComparison.Ordinal);
            Assert.Contains("PRIVATE-development-KO.avi", resolvedText.Headline, StringComparison.Ordinal);
            Assert.DoesNotContain(UnresolvedReferenceHold, resolvedText.Detail, StringComparison.Ordinal);
        }
        lines.Add(target + " " + (blinded ? "blind" : "open") + " resolved: " + resolvedText.Headline);
        lines.Add(target + " " + (blinded ? "blind" : "open") + " unresolved: " + unresolvedText.Headline);
    }

    private static (string Headline, string Detail, string Tooltip) ReadRow(ValidationComparisonSurface surface, string videoId, int frame)
    {
        var list = surface.Results;
        var row = list.Items.OfType<ValidationFrameRow>().Single(candidate => candidate.VideoId == videoId && candidate.Frame == frame);
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        list.UpdateLayout();
        Pump(Task.Delay(40));
        list.UpdateLayout();
        var container = list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
        Assert.NotNull(container);
        var block = Assert.IsType<TextBlock>(container.Content);
        Assert.Equal(row.Headline, block.Text);
        Assert.Equal(row.Headline, block.ToolTip as string);
        Assert.Equal(row.AutomationName, AutomationProperties.GetName(container));
        var detail = Descendants(surface).OfType<FrameworkElement>().Single(element => AutomationProperties.GetName(element) == ValidationViewNames.SelectedDetail);
        return (block.Text, string.Join("\n", Texts(Descendants(detail))), block.ToolTip as string ?? "");
    }

    private static void Present(ValidationComparisonReport report, S14SplitBoundaryFixture fixture, bool blinded, Action<ValidationComparisonSurface> inspect)
    {
        var labels = report.Frames.Select(frame => frame.VideoId).Concat(report.Endpoints.Select(endpoint => endpoint.VideoId)).Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => blinded ? "Recording " + (fixture.Assets.IndexOf(fixture.Assets.Single(asset => asset.Id == id)) + 1).ToString("000", CultureInfo.InvariantCulture) : fixture.Assets.Single(asset => asset.Id == id).Filename, StringComparer.Ordinal);
        var surface = new ValidationComparisonSurface { Width = 1050, Height = 700 };
        surface.Present(report, blinded, false, labels, [], new Dictionary<(string VideoId, int Frame), string>());
        var parameters = new HwndSourceParameters("resolved-reference-headline") { WindowStyle = 0, ExtendedWindowStyle = 0x08000000, PositionX = -32000, PositionY = -32000, Width = 1050, Height = 700 };
        using var hwnd = new HwndSource(parameters) { RootVisual = surface };
        try
        {
            surface.Measure(new Size(1050, 700));
            surface.Arrange(new Rect(0, 0, 1050, 700));
            surface.UpdateLayout();
            Pump(Task.Delay(40));
            inspect(surface);
        }
        finally { hwnd.RootVisual = null; }
    }

    private static void SaveAcceptedConsensus(S14SplitBoundaryFixture fixture, ValidationSampling sampling)
    {
        var history = new ValidationHistoryStore(fixture.Store.DatabasePath);
        var hash = IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(sampling));
        var first = new IndependentReference(fixture.Assets[1].Id, 19, ReferenceReviewerLane.First, "Independent first reviewer", ReferenceVisibility.Visible, 40, 50, ReferenceEvent.None, "Synthetic first truth", []);
        var firstId = history.History().Last(entry => entry.EntityId == IndependentValidationComparison.ReferenceKey(hash, first)).Id;
        var second = first with { Lane = ReferenceReviewerLane.Second, Reviewer = "Independent second reviewer", X = 70, Reason = "Synthetic disagreement at missing automatic row" };
        var secondEntry = history.Save("reference", IndependentValidationComparison.ReferenceKey(hash, second), JsonDefaults.Serialize(second), second.Reviewer, second.Reason, 1);
        var consensus = first with { Lane = ReferenceReviewerLane.Consensus, Reviewer = "Independent consensus reviewer", Reason = "Adjudicated synthetic visible center; automatic row remains missing", ConsensusReferenceIds = [firstId, secondEntry.Id] };
        history.Save("reference", IndependentValidationComparison.ReferenceKey(hash, consensus), JsonDefaults.Serialize(consensus), consensus.Reviewer, consensus.Reason, 0);
    }

    private static IEnumerable<string> Texts(IEnumerable<DependencyObject> nodes) => nodes.OfType<TextBlock>().Select(block => block.Text);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (var nested in Logical(node)) yield return nested;
        }
    }

    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.True(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
}
