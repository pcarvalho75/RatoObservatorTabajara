using System.Collections.ObjectModel;
using System.Collections.Immutable;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

public sealed class ExperimentAnimalDraft
{
    public string Id { get; set; } = ""; public string Alias { get; set; } = ""; public string Group { get; set; } = "";
    public bool IdentityConfirmed { get; set; } public bool AllocationConfirmed { get; set; }
    public string Cage { get; set; } = ""; public string Litter { get; set; } = "";
    public bool Excluded { get; set; } public string ExclusionReason { get; set; } = "";
    public PlannedAnimal Freeze() => new() { Id = Id, Alias = Alias, Group = Group, IdentityConfirmed = IdentityConfirmed, AllocationConfirmed = AllocationConfirmed, Cage = Cage, Litter = Litter, Excluded = Excluded, ExclusionReason = ExclusionReason };
}
public sealed class ExperimentContextDraft
{
    public string Id { get; set; } = ""; public TrialMode Phase { get; set; } public int Day { get; set; } = 1;
    public double RequestedExposureSeconds { get; set; } public double ComparisonStartSeconds { get; set; } public double ComparisonDurationSeconds { get; set; }
    public string TemporalBins { get; set; } = ""; public bool WindowConfirmed { get; set; }
    public string CommonCoordinateReference { get; set; } = ""; public StudyOrientation Orientation { get; set; } public bool AlignmentConfirmed { get; set; }
    public PlannedContext Freeze() => new() { Id = Id, Phase = Phase, Day = Day, RequestedExposureSeconds = RequestedExposureSeconds,
        ComparisonStartSeconds = ComparisonStartSeconds, ComparisonDurationSeconds = ComparisonDurationSeconds, WindowConfirmed = WindowConfirmed,
        CommonCoordinateReference = CommonCoordinateReference, Orientation = Orientation, AlignmentConfirmed = AlignmentConfirmed,
        TemporalBinEdges = string.IsNullOrWhiteSpace(TemporalBins) ? [] : TemporalBins.Split(',').Select(s => double.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToImmutableArray() };
}
public sealed class ExperimentTrialDraft
{
    public string Id { get; set; } = ""; public string AnimalId { get; set; } = ""; public string ContextId { get; set; } = ""; public int Order { get; set; } = 1;
    public string? RecordingId { get; set; } public string MissingReason { get; set; } = "";
    public bool SourceReferenceConfirmed { get; set; } public string ReferenceSourceSha256 { get; set; } = ""; public string ReferenceSettingsHash { get; set; } = "";
    public double RoomRotationRadians { get; set; }
    public PlannedTrial Freeze() => new() { Id = Id, AnimalId = AnimalId, ContextId = ContextId, Order = Order, RecordingId = string.IsNullOrWhiteSpace(RecordingId) ? null : RecordingId,
        MissingReason = MissingReason, SourceReferenceConfirmed = SourceReferenceConfirmed, ReferenceSourceSha256 = ReferenceSourceSha256, ReferenceSettingsHash = ReferenceSettingsHash, RoomRotationRadians = RoomRotationRadians };
}
public sealed class ExperimentPlanDraft
{
    public string Title { get; set; } = ""; public StudyUnitDesign UnitDesign { get; set; }
    public bool DesignConfirmed { get; set; } public bool RosterConfirmed { get; set; } public bool Exploratory { get; set; } = true; public string PrimaryComparison { get; set; } = "";
    public ObservableCollection<ExperimentAnimalDraft> Animals { get; } = [];
    public ObservableCollection<ExperimentContextDraft> Contexts { get; } = [];
    public ObservableCollection<ExperimentTrialDraft> Trials { get; } = [];
    public StudyPlan Freeze() => new() { Title = Title, UnitDesign = UnitDesign, DesignConfirmed = DesignConfirmed, RosterConfirmed = RosterConfirmed, Exploratory = Exploratory,
        PrimaryComparison = PrimaryComparison, Animals = Animals.Select(a => a.Freeze()).ToImmutableArray(), Contexts = Contexts.Select(c => c.Freeze()).ToImmutableArray(), Trials = Trials.Select(t => t.Freeze()).ToImmutableArray() };
    public static ExperimentPlanDraft From(StudyPlan plan)
    {
        var draft = new ExperimentPlanDraft { Title = plan.Title, UnitDesign = plan.UnitDesign, DesignConfirmed = plan.DesignConfirmed, RosterConfirmed = plan.RosterConfirmed, Exploratory = plan.Exploratory, PrimaryComparison = plan.PrimaryComparison };
        foreach (var a in plan.Animals) draft.Animals.Add(new() { Id = a.Id, Alias = a.Alias, Group = a.Group, IdentityConfirmed = a.IdentityConfirmed, AllocationConfirmed = a.AllocationConfirmed, Cage = a.Cage, Litter = a.Litter, Excluded = a.Excluded, ExclusionReason = a.ExclusionReason });
        foreach (var c in plan.Contexts) draft.Contexts.Add(new() { Id = c.Id, Phase = c.Phase, Day = c.Day, RequestedExposureSeconds = c.RequestedExposureSeconds, ComparisonStartSeconds = c.ComparisonStartSeconds, ComparisonDurationSeconds = c.ComparisonDurationSeconds,
            TemporalBins = string.Join(',', c.TemporalBinEdges.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))), WindowConfirmed = c.WindowConfirmed, CommonCoordinateReference = c.CommonCoordinateReference, Orientation = c.Orientation, AlignmentConfirmed = c.AlignmentConfirmed });
        foreach (var t in plan.Trials) draft.Trials.Add(new() { Id = t.Id, AnimalId = t.AnimalId, ContextId = t.ContextId, Order = t.Order, RecordingId = t.RecordingId, MissingReason = t.MissingReason,
            SourceReferenceConfirmed = t.SourceReferenceConfirmed, ReferenceSourceSha256 = t.ReferenceSourceSha256, ReferenceSettingsHash = t.ReferenceSettingsHash, RoomRotationRadians = t.RoomRotationRadians });
        return draft;
    }
}
