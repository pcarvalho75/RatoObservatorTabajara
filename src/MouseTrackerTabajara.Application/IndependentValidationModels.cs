using System.Collections.Immutable;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Application;

public enum ValidationSplit { Unspecified, Development, IndependentValidation }
public enum ValidationMeasurementTarget { RawAutomatic, InspectedCorrected }
public enum ReferenceVisibility { Visible, NotVisible, Ambiguous }
public enum ReferenceReviewerLane { First, Second, Consensus }
public enum ReferenceEvent { None, WaterEntry, PlatformMounting, ResearcherIntervention, RecordingEnd }
public sealed record ValidationRecording(string VideoId, string Session, ValidationSplit Split, bool UsedForTuning, bool IndependenceConfirmed, string ProtocolIdentity, string Allocation);
public sealed record ValidationTolerances(double PositionPixels, double DisagreementPixels, double EventSeconds, double PathDiameters, double SiteOccupancyFraction, double MinimumReferenceFraction)
{
    public void Validate()
    { if (new[] { PositionPixels, DisagreementPixels, EventSeconds, PathDiameters, SiteOccupancyFraction, MinimumReferenceFraction }.Any(v => !double.IsFinite(v) || v < 0) || PositionPixels <= 0 || DisagreementPixels <= 0 || MinimumReferenceFraction is <= 0 or > 1 || SiteOccupancyFraction > 1) throw new ArgumentException("Prespecified tolerances/denominators are invalid."); }
}
public sealed record IndependentValidationPlan
{
    public string Id { get; init; } = "validation-plan";
    public bool SyntheticToolingOnly { get; init; }
    public ValidationMeasurementTarget MeasurementTarget { get; init; } = ValidationMeasurementTarget.RawAutomatic;
    public int RandomFramesPerRecording { get; init; } = 10;
    public int DifficultIntervalsPerRecording { get; init; } = 2;
    public int IntervalFrames { get; init; } = 10;
    public int Seed { get; init; } = 2026;
    public double SecondReviewFraction { get; init; } = .2;
    public ValidationTolerances Tolerances { get; init; } = new(5, 3, .25, .1, .05, .8);
    public ImmutableArray<ValidationRecording> Recordings { get; init; } = [];
    public void Validate()
    {
        Tolerances.Validate(); if (!Enum.IsDefined(MeasurementTarget)) throw new ArgumentException("Unknown validation measurement target.");
        if (string.IsNullOrWhiteSpace(Id) || Recordings.IsDefaultOrEmpty || Recordings.Length > 10000 || RandomFramesPerRecording is < 1 or > 10000 || DifficultIntervalsPerRecording is < 0 or > 100 || IntervalFrames is < 1 or > 10000 || !double.IsFinite(SecondReviewFraction) || SecondReviewFraction is < 0 or > 1) throw new ArgumentException("Validation plan sampling/records are invalid.");
        if (Recordings.Select(r => r.VideoId).Distinct(StringComparer.Ordinal).Count() != Recordings.Length || (long)Recordings.Length * (RandomFramesPerRecording + (long)DifficultIntervalsPerRecording * IntervalFrames) > 200000) throw new ArgumentException("Recording duplication or validation sample budget exceeds200000 frame cases.");
        foreach (var row in Recordings)
        {
            if (string.IsNullOrWhiteSpace(row.VideoId) || string.IsNullOrWhiteSpace(row.Session) || string.IsNullOrWhiteSpace(row.ProtocolIdentity) || !Enum.IsDefined(row.Split)) throw new ArgumentException("Every recording requires explicit session/protocol/split information.");
            if (row.Split == ValidationSplit.IndependentValidation && (row.UsedForTuning || !row.IndependenceConfirmed)) throw new ArgumentException("A tuned/unconfirmed recording cannot be independent validation.");
        }
        if (Recordings.GroupBy(r => r.Session, StringComparer.OrdinalIgnoreCase).Any(g => g.Any(r => r.Split == ValidationSplit.Development || r.UsedForTuning) && g.Any(r => r.Split == ValidationSplit.IndependentValidation))) throw new ArgumentException("Development and validation must be separated by recording/session; never randomly split frames of a used session.");
    }
}
public sealed record ValidationFrozenRun(string VideoId, string SourceSha256, string AssetJson, string RunJson, ImmutableArray<string> Corrections,
    string RawRunFingerprint, string CorrectionFingerprint, string AlgorithmContract, string SoftwareVersion, string ComparatorBinarySha256, ImmutableArray<string> BiologicalEvents)
{
    public VideoAsset Asset => JsonDefaults.Deserialize<VideoAsset>(AssetJson);
    public AnalysisRun RawRun => JsonDefaults.Deserialize<AnalysisRun>(RunJson);
    public AnalysisRun EffectiveRun => RawRun with { Points = MetricCalculator.ApplyCorrections(RawRun, Corrections.Select(JsonDefaults.Deserialize<ManualCorrection>)) };
}
public sealed record ValidationSamplingCase(string VideoId, int StartFrame, int EndFrame, bool RandomSample, double? InclusionProbability, int PopulationFrames, bool SecondReviewRequired, string SelectionReason);
public sealed record ValidationSampling(string PlanFingerprint, string ProducerFingerprint, int Seed, ImmutableArray<ValidationFrozenRun> Runs, ImmutableArray<ValidationSamplingCase> Cases);
public sealed record IndependentReference(string VideoId, int Frame, ReferenceReviewerLane Lane, string Reviewer, ReferenceVisibility Visibility,
    double? X, double? Y, ReferenceEvent Event, string Reason, ImmutableArray<long> ConsensusReferenceIds)
{
    public void Validate(VideoMetadata metadata)
    {
        if (Frame < 0 || Frame >= metadata.FrameCount || !Enum.IsDefined(Lane) || !Enum.IsDefined(Visibility) || !Enum.IsDefined(Event) || string.IsNullOrWhiteSpace(Reviewer) || string.IsNullOrWhiteSpace(Reason) || ConsensusReferenceIds.IsDefault) throw new ArgumentException("Independent reference frame/reviewer/semantics are invalid.");
        if (Visibility == ReferenceVisibility.Visible && (!X.HasValue || !Y.HasValue) || Visibility != ReferenceVisibility.Visible && (X.HasValue || Y.HasValue)) throw new ArgumentException("Visible body center requires both coordinates; ambiguous/not-visible references cannot assert a point.");
        if (X.HasValue && (!double.IsFinite(X.Value) || !double.IsFinite(Y!.Value) || X < 0 || Y < 0 || X >= metadata.Width || Y >= metadata.Height)) throw new ArgumentException("Reference point is outside exact source pixels.");
        if (Lane == ReferenceReviewerLane.Consensus && ConsensusReferenceIds.Length < 2) throw new ArgumentException("Consensus must cite both independent reference revisions.");
    }
}
