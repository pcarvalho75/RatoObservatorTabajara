using System.Collections.Immutable;

namespace MouseTrackerTabajara.Application;

/// <summary>
/// Counts for one declared split. Position errors use joined visible reference frames, not animals.
/// An empty role stays present with zero counts and null errors; those zeros are not a result.
/// </summary>
public sealed record ValidationSplitSummary(
    ValidationSplit Role,
    int RecordingCount,
    int FullSourceFrames,
    int SampledFrames,
    int ReferenceRecordedFrames,
    int PositionJoinedFrames,
    int RandomPositionJoinedFrames,
    int MissingReferenceFrames,
    int NotJoinedFrames,
    int ToleranceFailureFrames,
    int FalseAcceptedFrames,
    int ExcludedRecordings,
    int ExcludedSampledFrames,
    int AmbiguousFrames,
    int UnresolvedDisagreementFrames,
    int NotVisibleReferenceFrames,
    double? MeanRandomPositionErrorPixels,
    double? MaximumPositionErrorPixels,
    int RequestedPurposiveIntervals,
    int RealizedPurposiveIntervals,
    bool SourceTooShortForDistinctIntervals,
    string PositionDenominator,
    string PurposiveIntervalNote);

public static class ValidationSplitSummaries
{
    public const string PooledScopeLabel = "Pooled random-position mean and maximum include every declared recording in Development, Independent validation, and Unspecified, including marked exclusions. The denominator is joined visible reference frames. This pooled figure is not held-out accuracy and frames are not animal N.";
    public const string AbsentSummaryHold = "Role-specific split summaries are absent from this stored comparison and are held. Development, Independent validation, and Unspecified figures are not inferred from the pooled mean. Recompute the frozen comparison before reading a role-specific error.";
    public const string PositionDenominatorText = "Position-error mean and maximum use joined visible reference frames only. They are not animal N and they are not held-out accuracy. Ambiguous, unresolved disagreement, not-visible, missing-reference, unjoined, and excluded cases stay visible. Missing and unjoined cases are not entered as zero error. Excluded recordings remain in the mean when a visible reference joins; exclusion does not delete them.";
    public static readonly ImmutableArray<ValidationSplit> DeclaredRoles = [ValidationSplit.Development, ValidationSplit.IndependentValidation, ValidationSplit.Unspecified];

    public static bool TryRead(ValidationComparisonReport report, out ImmutableArray<ValidationSplitSummary> summaries, out string hold)
    {
        summaries = default;
        var stored = report.SplitSummaries;
        if (stored.IsDefaultOrEmpty || stored.Length != DeclaredRoles.Length || stored.Select(s => s.Role).Distinct().Count() != DeclaredRoles.Length || DeclaredRoles.Any(role => !stored.Any(s => s.Role == role)))
        {
            hold = AbsentSummaryHold;
            return false;
        }
        summaries = stored;
        hold = "";
        return true;
    }

    public static ImmutableArray<ValidationSplitSummary> Build(IndependentValidationPlan plan, IndependentValidationSnapshot snapshot, ValidationSampling sampling, IReadOnlyList<ValidationFrameResult> frames)
    {
        var summaries = ImmutableArray.CreateBuilder<ValidationSplitSummary>(DeclaredRoles.Length);
        foreach (var role in DeclaredRoles) summaries.Add(Summarize(role, plan, snapshot, sampling, frames));
        return summaries.ToImmutable();
    }

    private static ValidationSplitSummary Summarize(ValidationSplit role, IndependentValidationPlan plan, IndependentValidationSnapshot snapshot, ValidationSampling sampling, IReadOnlyList<ValidationFrameResult> frames)
    {
        var recordings = plan.Recordings.Where(r => r.Split == role).ToArray();
        var ids = recordings.Select(r => r.VideoId).ToHashSet(StringComparer.Ordinal);
        var roleFrames = frames.Where(f => ids.Contains(f.VideoId)).ToArray();
        var randomErrors = roleFrames.Where(f => f.RandomSample && f.PositionErrorPixels.HasValue).Select(f => f.PositionErrorPixels!.Value).ToArray();
        var errors = roleFrames.Where(f => f.PositionErrorPixels.HasValue).Select(f => f.PositionErrorPixels!.Value).ToArray();
        var requested = recordings.Length * plan.DifficultIntervalsPerRecording;
        var realized = sampling.Cases.Where(c => !c.RandomSample && ids.Contains(c.VideoId)).Select(c => (c.VideoId, c.StartFrame, c.EndFrame)).Distinct().Count();
        var tooShort = false;
        var truncated = false;
        var fullSource = 0;
        var excluded = 0;
        foreach (var recording in recordings)
        {
            var asset = snapshot.Imported.Single(a => a.Id == recording.VideoId);
            fullSource += asset.Metadata.FrameCount;
            if (!asset.Included) excluded++;
            if (plan.DifficultIntervalsPerRecording > MaximumDistinctWindows(asset.Metadata.FrameCount, plan.IntervalFrames)) tooShort = true;
            if (plan.DifficultIntervalsPerRecording > 0 && asset.Metadata.FrameCount < plan.IntervalFrames) truncated = true;
        }
        return new(role, recordings.Length, fullSource, roleFrames.Length, roleFrames.Count(f => !f.ReferenceIds.IsEmpty), errors.Length, randomErrors.Length,
            roleFrames.Count(MissingReference), roleFrames.Count(f => !f.PositionErrorPixels.HasValue), roleFrames.Count(f => f.PositionPass == false), roleFrames.Count(f => f.FalseAccepted == true), excluded, roleFrames.Count(f => f.Excluded),
            roleFrames.Count(f => f.Visibility == ReferenceVisibility.Ambiguous), roleFrames.Count(f => f.Holds.Any(h => h.StartsWith("Independent reviewers disagree; unresolved reference is held.", StringComparison.Ordinal))), roleFrames.Count(f => f.Visibility == ReferenceVisibility.NotVisible),
            randomErrors.Length > 0 ? randomErrors.Average() : null, errors.Length > 0 ? errors.Max() : null, requested, realized, tooShort, PositionDenominatorText, PurposiveNote(recordings.Length, requested, realized, tooShort, truncated));
    }

    private static bool MissingReference(ValidationFrameResult frame) => frame.Holds.Any(h => h.StartsWith("Independent first reference is missing", StringComparison.Ordinal) || h.StartsWith("Declared second-review reference is missing", StringComparison.Ordinal));

    /// <summary>Maximum distinct clamped windows of the requested length. This does not change sampling selection.</summary>
    internal static int MaximumDistinctWindows(int population, int intervalFrames)
    {
        if (population <= 0 || intervalFrames <= 0) return 0;
        if (population <= intervalFrames) return 1;
        return population - intervalFrames + 1;
    }

    private static string PurposiveNote(int recordings, int requested, int realized, bool tooShort, bool truncated)
    {
        if (recordings == 0) return "No recordings are declared in this role. Requested and realized purposive intervals are zero; no interval result is fabricated.";
        if (requested == 0) return "The plan requested no purposive intervals for this role. Realized " + realized + ". No interval result is fabricated. Sampling selection and stored sampling JSON are unchanged.";
        var gap = tooShort
            ? "The source is too short to realize every requested distinct purposive interval. Additional copies cannot increase the distinct interval count; this comparison preserves the frozen choices."
            : realized == requested
                ? "Requested purposive intervals match the distinct intervals realized in the frozen sample."
                : "The frozen sample's distinct purposive interval count differs from the plan request. This comparison preserves stored intervals without regenerating selection.";
        var clip = truncated ? " At least one declared source is shorter than the requested interval length, so the frozen interval is clipped to that source." : "";
        return gap + " Requested " + requested + "; realized " + realized + ". Sampling selection and stored sampling JSON are unchanged." + clip;
    }
}
