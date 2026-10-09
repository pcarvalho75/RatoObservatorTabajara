using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MouseTrackerTabajara.Application;

namespace MouseTrackerTabajara.Desktop;

public static class ValidationViewNames
{
    public const string RoleSummary = "Validation role summary";
    public const string RoleCardPrefix = "Validation role card ";
    public const string PooledDescriptor = "Validation pooled descriptor";
    public const string AccuracyBoundary = "Validation sampling accuracy boundary";
    public const string ResultList = "Validation result list";
    public const string SelectedDetail = "Validation selected frame";
    public const string CountDetails = "Validation role count details";
    public const string MeasurementTarget = "Validation measurement target";
    public const string AllocationAudit = "Validation allocation audit";
    public const string LegacyRecompute = "Validation recomputed role summaries";
    public const string OpenSource = "Open selected validation source";
}

public sealed class ValidationFrameRow
{
    public required string VideoId { get; init; }
    public required int Frame { get; init; }
    public required string Headline { get; init; }
    public required string Tooltip { get; init; }
    public required string AutomationName { get; init; }
    public required string SamplingNote { get; init; }
    public required bool Blinded { get; init; }
    public required ValidationFrameResult Result { get; init; }
    public override string ToString() => Headline;
}

public sealed class ValidationResultListView : ListBox
{
    public ValidationResultListView()
    {
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Item);
        VirtualizingPanel.SetCacheLengthUnit(this, VirtualizationCacheLengthUnit.Item);
        VirtualizingPanel.SetCacheLength(this, new VirtualizationCacheLength(0));
        ScrollViewer.SetCanContentScroll(this, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(HeightProperty, 28d));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        ItemContainerStyle = style;
        BorderBrush = ValidationComparisonSurface.Line;
        BorderThickness = new Thickness(1);
        Background = Brushes.White;
        Tag = ValidationViewNames.ResultList;
        AutomationProperties.SetName(this, ValidationViewNames.ResultList);
    }

    // A recycled container must not keep the previous frame's label or tooltip.
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is not ListBoxItem container || item is not ValidationFrameRow row) return;
        container.Content = new TextBlock { Text = row.Headline, ToolTip = row.Tooltip, Margin = new Thickness(6, 3, 6, 3), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        container.ToolTip = null;
        container.Tag = "validation-result-row";
        AutomationProperties.SetName(container, row.AutomationName);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ListBoxItem container)
        {
            container.Content = null;
            container.ToolTip = null;
            container.Tag = null;
            AutomationProperties.SetName(container, "");
        }
        base.ClearContainerForItemOverride(element, item);
    }
}

public sealed class ValidationComparisonSurface : Grid
{
    public static readonly SolidColorBrush Paper = Freeze(Color.FromRgb(0xF6, 0xF3, 0xEB));
    public static readonly SolidColorBrush Ink = Freeze(Color.FromRgb(0x1C, 0x24, 0x2B));
    public static readonly SolidColorBrush Muted = Freeze(Color.FromRgb(0x5C, 0x65, 0x6B));
    public static readonly SolidColorBrush Line = Freeze(Color.FromRgb(0xD5, 0xCF, 0xC3));
    public static readonly SolidColorBrush Card = Freeze(Color.FromRgb(0xFF, 0xFC, 0xF7));
    private static readonly SolidColorBrush Development = Freeze(Color.FromRgb(0x2F, 0x4F, 0x6F));
    private static readonly SolidColorBrush Independent = Freeze(Color.FromRgb(0x0F, 0x6E, 0x56));
    private static readonly SolidColorBrush Unspecified = Freeze(Color.FromRgb(0x8A, 0x5A, 0x12));
    private static readonly SolidColorBrush Banner = Freeze(Color.FromRgb(0xF3, 0xE6, 0xC8));
    private const double ResultListMinHeight = 72;
    private const double SelectedDetailMaxHeight = 108;
    private readonly StackPanel _summary = new();
    private readonly StackPanel _detail = new();
    private readonly ScrollViewer _summaryHost;
    private readonly ScrollViewer _selectedDetail;
    public ValidationResultListView Results { get; } = new();
    public Action<ValidationFrameRow>? OpenSource { get; set; }

    public ValidationComparisonSurface()
    {
        Background = Paper;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = ResultListMinHeight });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // One line per continuous endpoint must not grow this Auto row past the list and the selected frame.
        _summaryHost = new ScrollViewer { Content = _summary, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _selectedDetail = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SelectedDetailMaxHeight, Margin = new Thickness(4, 4, 4, 0), Tag = ValidationViewNames.SelectedDetail };
        AutomationProperties.SetName(_selectedDetail, ValidationViewNames.SelectedDetail);
        Children.Add(_summaryHost);
        SetRow(Results, 1);
        Results.Margin = new Thickness(4, 2, 4, 0);
        Children.Add(Results);
        SetRow(_selectedDetail, 2);
        Children.Add(_selectedDetail);
        Results.SelectionChanged += (_, _) => ShowDetail(Results.SelectedItem as ValidationFrameRow);
        Results.MouseDoubleClick += (_, _) => { if (Results.SelectedItem is ValidationFrameRow row) OpenSource?.Invoke(row); };
        Clear();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Height) && availableSize.Height > 0)
        {
            var reserve = ResultListMinHeight + Results.Margin.Top + Results.Margin.Bottom
                + SelectedDetailMaxHeight + _selectedDetail.Margin.Top + _selectedDetail.Margin.Bottom;
            var limit = Math.Max(48, availableSize.Height - reserve);
            if (Math.Abs(_summaryHost.MaxHeight - limit) > 0.5) _summaryHost.MaxHeight = limit;
        }
        return base.MeasureOverride(availableSize);
    }

    public void Clear()
    {
        _summary.Children.Clear();
        Results.ItemsSource = null;
        ShowDetail(null);
    }

    public void Present(ValidationComparisonReport report, bool blinded, bool recomputedAbsentSummaries, IReadOnlyDictionary<string, string> recordingLabels, IReadOnlyList<string> allocationLines, IReadOnlyDictionary<(string VideoId, int Frame), string> samplingNotes)
    {
        _summary.Children.Clear();
        if (recomputedAbsentSummaries) _summary.Children.Add(Notice(ValidationSplitSummaryView.RecomputedAbsentSummaries, ValidationViewNames.LegacyRecompute, Banner));
        if (!ValidationSplitSummaries.TryRead(report, out var summaries, out var hold))
        {
            _summary.Children.Add(Paragraph(hold, 13, Ink));
            Results.ItemsSource = null;
            ShowDetail(null);
            return;
        }
        var cards = new UniformGrid { Columns = ValidationSplitSummaries.DeclaredRoles.Length, Tag = ValidationViewNames.RoleSummary };
        AutomationProperties.SetName(cards, ValidationViewNames.RoleSummary);
        foreach (var role in ValidationSplitSummaries.DeclaredRoles) cards.Children.Add(RoleCard(summaries.Single(summary => summary.Role == role)));
        _summary.Children.Add(cards);
        var secondary = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        secondary.Children.Add(Named(ValidationSplitSummaryView.MeasurementTargetLine(report.MeasurementTarget), ValidationViewNames.MeasurementTarget, 12, Ink));
        secondary.Children.Add(Named(ValidationSplitSummaryView.PooledDescriptor(report), ValidationViewNames.PooledDescriptor, 12, Muted));
        secondary.Children.Add(Named(ValidationSplitSummaryView.AccuracyBoundary, ValidationViewNames.AccuracyBoundary, 12, Muted));
        if (allocationLines.Count > 0)
        {
            var audits = new StackPanel { Tag = ValidationViewNames.AllocationAudit };
            AutomationProperties.SetName(audits, ValidationViewNames.AllocationAudit);
            foreach (var line in allocationLines) audits.Children.Add(Paragraph(line, 12, Ink));
            secondary.Children.Add(audits);
        }
        var excluded = report.Frames.Where(frame => frame.Excluded).GroupBy(frame => frame.VideoId, StringComparer.Ordinal).ToArray();
        if (blinded && excluded.Length > 0) secondary.Children.Add(Paragraph("Recording exclusion is retained; allocation reason hidden.", 12, Ink));
        if (!blinded)
            foreach (var group in excluded) secondary.Children.Add(Paragraph("Excluded recording: " + group.First().ExclusionReason, 12, Ink));
        secondary.Children.Add(Paragraph("Sampled source and frame order. Select one row for its exact error, join, and source.", 12, Muted));
        _summary.Children.Add(secondary);
        var details = new StackPanel();
        details.Children.Add(Paragraph("Full-source frames " + report.FullSourcePopulation + "; sampled frames " + report.SampledUniqueFrames + "; recorded references " + report.ReferenceRecordedFrames + ".", 12, Ink));
        foreach (var role in ValidationSplitSummaries.DeclaredRoles) details.Children.Add(Paragraph(ValidationSplitSummaryView.Counts(summaries.Single(summary => summary.Role == role)), 12, Ink));
        details.Children.Add(Paragraph(ValidationSplitSummaries.PositionDenominatorText, 12, Muted));
        foreach (var reportHold in report.Holds) details.Children.Add(Paragraph(reportHold, 12, Ink));
        details.Children.Add(Paragraph(ValidationSplitSummaryView.ResearcherInterpretation(report.Interpretation), 12, Muted));
        foreach (var endpoint in report.Endpoints)
        {
            var label = recordingLabels[endpoint.VideoId];
            details.Children.Add(Paragraph(label + " continuous frames " + endpoint.StartFrame + "–" + endpoint.EndFrame + ": path difference " + Number(endpoint.PathDifferenceDiameters) + " diameters; site occupancy difference " + Number(endpoint.SiteOccupancyDifference) + ". Not whole-trial biological validation.", 12, Ink));
        }
        details.Children.Add(Paragraph("Producer, reference, plan, and comparison-code receipts stay on the saved report. This screen does not reprint those fingerprints.", 12, Muted));
        var expander = new Expander { Header = "Full counts, holds, and provenance", Content = details, IsExpanded = false, Margin = new Thickness(4, 2, 4, 2), Tag = ValidationViewNames.CountDetails };
        AutomationProperties.SetName(expander, ValidationViewNames.CountDetails);
        _summary.Children.Add(expander);
        var rows = report.Frames.Select(frame => Row(frame, recordingLabels[frame.VideoId], samplingNotes.GetValueOrDefault((frame.VideoId, frame.Frame), ""), blinded)).ToArray();
        Results.ItemsSource = null;
        Results.ItemsSource = rows;
        if (rows.Length > 0) Results.SelectedIndex = 0;
        else ShowDetail(null);
    }

    private void ShowDetail(ValidationFrameRow? row)
    {
        _detail.Children.Clear();
        if (row is null)
        {
            _detail.Children.Add(Paragraph("Select a sampled frame.", 12, Muted));
            return;
        }
        var frame = row.Result;
        _detail.Children.Add(Paragraph(row.Headline, 13, Ink));
        _detail.Children.Add(Paragraph("Position error " + (frame.PositionErrorPixels is double error ? error.ToString("0.###", CultureInfo.InvariantCulture) + " px" : "held") + ".", 12, Ink));
        _detail.Children.Add(Paragraph("False accepted " + (frame.FalseAccepted?.ToString() ?? "held") + ". Disagreement " + frame.Disagreement + ". References " + (frame.ReferenceIds.IsDefaultOrEmpty ? "none" : string.Join(", ", frame.ReferenceIds)) + ".", 12, Ink));
        _detail.Children.Add(Paragraph(row.SamplingNote, 12, Muted));
        _detail.Children.Add(Paragraph(HoldText(frame, row.Blinded), 12, Ink));
        _detail.Children.Add(Paragraph(ExclusionText(frame, row.Blinded), 12, Ink));
        var open = new Button { Content = "Open failure/reference original frame", Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, Tag = ValidationViewNames.OpenSource };
        AutomationProperties.SetName(open, ValidationViewNames.OpenSource);
        open.Click += (_, _) => OpenSource?.Invoke(row);
        _detail.Children.Add(open);
    }

    private static ValidationFrameRow Row(ValidationFrameResult frame, string recordingLabel, string samplingNote, bool blinded)
    {
        var headline = Headline(recordingLabel, frame);
        return new ValidationFrameRow
        {
            VideoId = frame.VideoId, Frame = frame.Frame, Headline = headline, Tooltip = headline, SamplingNote = samplingNote, Blinded = blinded, Result = frame,
            AutomationName = "Validation result " + recordingLabel + " frame " + frame.Frame.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string Headline(string recordingLabel, ValidationFrameResult frame)
    {
        var marks = new List<string>();
        if (frame.Excluded) marks.Add("excluded");
        if (frame.ReferenceIds.IsEmpty) marks.Add("missing reference");
        // Same hold ValidationSplitSummaries counts. A missing position error is not an unresolved reference.
        var unresolvedReference = frame.Holds.Any(hold => hold.StartsWith("Independent reviewers disagree; unresolved reference is held.", StringComparison.Ordinal));
        if (unresolvedReference) marks.Add("unresolved disagreement");
        else if (frame.Disagreement) marks.Add("reference resolved");
        if (frame.Visibility == ReferenceVisibility.Ambiguous) marks.Add("ambiguous");
        if (frame.Visibility == ReferenceVisibility.NotVisible) marks.Add("not visible");
        if (frame.FalseAccepted == true) marks.Add("false accepted");
        if (frame.PositionPass == false) marks.Add("tolerance failure");
        if (frame.MissedVisible == true || frame.Holds.Any(hold => hold.StartsWith("No producer frame join in analyzed interval; source frame remains sampled.", StringComparison.Ordinal))) marks.Add("producer join unavailable");
        if (!frame.PositionErrorPixels.HasValue && !frame.ReferenceIds.IsEmpty && frame.Visibility != ReferenceVisibility.Ambiguous && frame.Visibility != ReferenceVisibility.NotVisible) marks.Add("not joined");
        var error = frame.PositionErrorPixels is double value ? value.ToString("0.###", CultureInfo.InvariantCulture) + " px" : "held";
        return recordingLabel + " · exact frame " + frame.Frame.ToString(CultureInfo.InvariantCulture) + "; error " + error + (marks.Count == 0 ? "" : "; " + string.Join("; ", marks));
    }

    private static string HoldText(ValidationFrameResult frame, bool blinded)
    {
        if (frame.Holds.IsEmpty) return "Prespecified comparison available.";
        return blinded ? "Reference/producer join held; inspect source and independent histories." : string.Join("\n", frame.Holds);
    }

    private static string ExclusionText(ValidationFrameResult frame, bool blinded)
    {
        if (!frame.Excluded) return "Not marked excluded.";
        return blinded ? "Recording exclusion is retained; allocation reason hidden." : "Excluded recording: " + frame.ExclusionReason;
    }

    private static Border RoleCard(ValidationSplitSummary summary)
    {
        var name = ValidationViewNames.RoleCardPrefix + ValidationSplitSummaryView.RoleName(summary.Role);
        var text = new TextBlock { Text = ValidationSplitSummaryView.Compact(summary), TextWrapping = TextWrapping.Wrap, Foreground = Ink, FontSize = 13, Margin = new Thickness(8, 6, 8, 6) };
        var card = new Border
        {
            Background = Card, BorderBrush = summary.Role switch
            {
                ValidationSplit.Development => Development,
                ValidationSplit.IndependentValidation => Independent,
                _ => Unspecified
            },
            BorderThickness = new Thickness(4, 1, 1, 1), Margin = new Thickness(4, 2, 4, 2), Child = text, Tag = name
        };
        AutomationProperties.SetName(card, name);
        return card;
    }

    private static Border Notice(string text, string name, Brush background)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ink, FontSize = 12, Margin = new Thickness(8, 4, 8, 4) };
        var notice = new Border { Background = background, Margin = new Thickness(4, 2, 4, 2), Child = block, Tag = name };
        AutomationProperties.SetName(notice, name);
        return notice;
    }

    private static TextBlock Named(string text, string name, double size, Brush foreground)
    {
        var block = Paragraph(text, size, foreground);
        block.Tag = name;
        AutomationProperties.SetName(block, name);
        return block;
    }

    private static TextBlock Paragraph(string text, double size, Brush foreground) => new() { Text = text, FontSize = size, Foreground = foreground, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 1, 4, 1) };

    private static string Number(double? value) => value is null ? "held" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
