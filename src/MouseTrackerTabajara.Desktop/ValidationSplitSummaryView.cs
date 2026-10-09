using System.Globalization;
using MouseTrackerTabajara.Application;

namespace MouseTrackerTabajara.Desktop;

public static class ValidationSplitSummaryView
{
    public const string RecomputedAbsentSummaries = "Stored comparison had no role summaries. The role figures below were recomputed from the frozen plan, sample, and references. They were not inferred from the pooled mean.";

    public static string RoleName(ValidationSplit role) => role switch
    {
        ValidationSplit.Development => "Development",
        ValidationSplit.IndependentValidation => "Independent validation",
        ValidationSplit.Unspecified => "Unspecified",
        _ => throw new ArgumentOutOfRangeException(nameof(role), "Undeclared validation role.")
    };

    public static IReadOnlyList<string> Lines(ValidationComparisonReport report, bool recomputedAbsentSummaries = false)
    {
        var label = string.IsNullOrWhiteSpace(report.PooledScopeLabel) ? ValidationSplitSummaries.PooledScopeLabel : report.PooledScopeLabel;
        var lines = new List<string>();
        if (recomputedAbsentSummaries) lines.Add(RecomputedAbsentSummaries);
        lines.Add(label);
        lines.Add("Pooled across all declared recordings, including marked exclusions: full-source frames " + report.FullSourcePopulation + "; sampled frames " + report.SampledUniqueFrames + "; recorded references " + report.ReferenceRecordedFrames + "; position joins " + report.PositionJoinedFrames + "; not joined " + (report.SampledUniqueFrames - report.PositionJoinedFrames) + ". Conditional random-position mean " + Pixels(report.MeanRandomPositionErrorPixels) + "; maximum position error " + Pixels(report.MaximumPositionErrorPixels) + ". Denominator: joined visible reference frames. Not held-out accuracy. Not animal N.");
        if (!ValidationSplitSummaries.TryRead(report, out var summaries, out var hold))
        {
            lines.Add(hold);
            return lines;
        }
        foreach (var role in ValidationSplitSummaries.DeclaredRoles)
            lines.Add(Format(summaries.Single(s => s.Role == role)));
        return lines;
    }

    public static string Format(ValidationSplitSummary summary) =>
        "Declared role " + RoleName(summary.Role) + ": recordings " + summary.RecordingCount + "; excluded recordings " + summary.ExcludedRecordings + "; excluded sampled frames " + summary.ExcludedSampledFrames + "; full-source frames " + summary.FullSourceFrames + "; sampled frames " + summary.SampledFrames + "; recorded references " + summary.ReferenceRecordedFrames + "; position joins " + summary.PositionJoinedFrames + "; random position joins " + summary.RandomPositionJoinedFrames + "; missing references " + summary.MissingReferenceFrames + "; not joined " + summary.NotJoinedFrames + "; tolerance failures " + summary.ToleranceFailureFrames + "; false accepted " + summary.FalseAcceptedFrames + "; ambiguous " + summary.AmbiguousFrames + "; unresolved disagreement " + summary.UnresolvedDisagreementFrames + "; not-visible references " + summary.NotVisibleReferenceFrames + ". Conditional random-position mean " + Pixels(summary.MeanRandomPositionErrorPixels) + "; maximum position error " + Pixels(summary.MaximumPositionErrorPixels) + ". " + summary.PositionDenominator + " " + summary.PurposiveIntervalNote;

    public const string AccuracyBoundary = "Joined visible reference frames only. Missing, unjoined, ambiguous, unresolved, not-visible, and excluded cases stay visible. Missing and unjoined cases are not entered as zero error. This is not held-out accuracy and frames are not animal N.";

    public static string Compact(ValidationSplitSummary summary) =>
        "Declared role " + RoleName(summary.Role) + ": conditional random-position mean " + Pixels(summary.MeanRandomPositionErrorPixels) + "; position joins " + summary.PositionJoinedFrames + "; tolerance failures " + summary.ToleranceFailureFrames + "; exclusions " + summary.ExcludedRecordings + " recordings, " + summary.ExcludedSampledFrames + " sampled frames.";

    public static string Counts(ValidationSplitSummary summary) =>
        "Declared role " + RoleName(summary.Role) + " counts: recordings " + summary.RecordingCount + "; excluded recordings " + summary.ExcludedRecordings + "; excluded sampled frames " + summary.ExcludedSampledFrames + "; full-source frames " + summary.FullSourceFrames + "; sampled frames " + summary.SampledFrames + "; recorded references " + summary.ReferenceRecordedFrames + "; position joins " + summary.PositionJoinedFrames + "; random position joins " + summary.RandomPositionJoinedFrames + "; missing references " + summary.MissingReferenceFrames + "; not joined " + summary.NotJoinedFrames + "; tolerance failures " + summary.ToleranceFailureFrames + "; false accepted " + summary.FalseAcceptedFrames + "; ambiguous " + summary.AmbiguousFrames + "; unresolved disagreement " + summary.UnresolvedDisagreementFrames + "; not-visible references " + summary.NotVisibleReferenceFrames + "; maximum position error " + Pixels(summary.MaximumPositionErrorPixels) + ". " + summary.PurposiveIntervalNote;

    public static string PooledDescriptor(ValidationComparisonReport report) =>
        "Pooled descriptor, secondary to the declared roles: conditional random-position mean " + Pixels(report.MeanRandomPositionErrorPixels) + "; maximum position error " + Pixels(report.MaximumPositionErrorPixels) + "; position joins " + report.PositionJoinedFrames + "; not joined " + (report.SampledUniqueFrames - report.PositionJoinedFrames) + ". Includes marked exclusions. Not held-out accuracy. Not animal N.";

    public static string MeasurementTargetLine(ValidationMeasurementTarget target) => target switch
    {
        ValidationMeasurementTarget.RawAutomatic => "Measurement target: RAW AUTOMATIC trajectory. Manual corrections are not scored as automatic accuracy.",
        ValidationMeasurementTarget.InspectedCorrected => "Measurement target: INSPECTED CORRECTED output. Manual repairs are included. This is not raw automatic tracker accuracy.",
        _ => throw new ArgumentOutOfRangeException(nameof(target), "Unknown validation measurement target.")
    };

    public static string ResearcherInterpretation(string interpretation)
    {
        var text = interpretation.Replace("no automatic G4/H4 closure", "real held-out accuracy is not closed here", StringComparison.Ordinal);
        text = text.Replace("G4/H4", "", StringComparison.Ordinal).Replace("G4", "", StringComparison.Ordinal).Replace("H4", "", StringComparison.Ordinal);
        return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Pixels(double? value) => value is null ? "held" : value.Value.ToString("0.###", CultureInfo.InvariantCulture) + " px";
}
