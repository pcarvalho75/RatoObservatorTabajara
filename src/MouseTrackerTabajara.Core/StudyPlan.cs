using System.Collections.Immutable;

namespace MouseTrackerTabajara.Core;

public enum StudyUnitDesign { Unknown, IndependentAnimals, RepeatedTrialsWithinAnimals }
public enum StudyOrientation { Unconfirmed, Room, TargetAligned }

public sealed record PlannedAnimal
{
    public string Id { get; init; } = "";
    public string Alias { get; init; } = "";
    public string Group { get; init; } = "";
    public bool IdentityConfirmed { get; init; }
    public bool AllocationConfirmed { get; init; }
    public string Cage { get; init; } = "";
    public string Litter { get; init; } = "";
    public bool Excluded { get; init; }
    public string ExclusionReason { get; init; } = "";
}

public sealed record PlannedContext
{
    public string Id { get; init; } = "";
    public TrialMode Phase { get; init; }
    public int Day { get; init; } = 1;
    public double RequestedExposureSeconds { get; init; }
    public double ComparisonStartSeconds { get; init; }
    public double ComparisonDurationSeconds { get; init; }
    public ImmutableArray<double> TemporalBinEdges { get; init; } = [];
    public bool WindowConfirmed { get; init; }
    public string CommonCoordinateReference { get; init; } = "";
    public StudyOrientation Orientation { get; init; }
    public bool AlignmentConfirmed { get; init; }
}

public sealed record PlannedTrial
{
    public string Id { get; init; } = "";
    public string AnimalId { get; init; } = "";
    public string ContextId { get; init; } = "";
    public int Order { get; init; } = 1;
    public string? RecordingId { get; init; }
    public string MissingReason { get; init; } = "";
    public bool SourceReferenceConfirmed { get; init; }
    public string ReferenceSourceSha256 { get; init; } = "";
    public string ReferenceSettingsHash { get; init; } = "";
    public double RoomRotationRadians { get; init; }
}

/// <summary>Expected trials are entered explicitly. Imported recordings never generate confirmation.</summary>
public sealed record StudyPlan
{
    public string Title { get; init; } = "";
    public StudyUnitDesign UnitDesign { get; init; }
    public bool DesignConfirmed { get; init; }
    public bool RosterConfirmed { get; init; }
    public bool Exploratory { get; init; } = true;
    public string PrimaryComparison { get; init; } = "";
    public ImmutableArray<PlannedAnimal> Animals { get; init; } = [];
    public ImmutableArray<PlannedContext> Contexts { get; init; } = [];
    public ImmutableArray<PlannedTrial> Trials { get; init; } = [];

    public void Validate()
    {
        if (!Enum.IsDefined(UnitDesign) || Animals.IsDefault || Contexts.IsDefault || Trials.IsDefault)
            throw new ArgumentException("Study plan collections/design are invalid.");
        if (Animals.Any(a => a is null) || Contexts.Any(c => c is null) || Trials.Any(t => t is null))
            throw new ArgumentException("Study plan entries cannot be null.");
        if (Animals.Length > 10000 || Contexts.Length > 10000 || Trials.Length > 10000)
            throw new ArgumentException("A study plan supports at most 10,000 entries per collection.");
        Unique(Animals.Select(a => a.Id), "animal"); Unique(Contexts.Select(c => c.Id), "context"); Unique(Trials.Select(t => t.Id), "trial");
        if (DesignConfirmed && (UnitDesign == StudyUnitDesign.Unknown || string.IsNullOrWhiteSpace(PrimaryComparison) && !Exploratory))
            throw new ArgumentException("Confirm the experimental unit and state the primary comparison, or label the plan exploratory.");
        if (RosterConfirmed && (Animals.IsEmpty || Trials.IsEmpty)) throw new ArgumentException("An empty expected roster cannot be confirmed.");
        foreach (var animal in Animals)
        {
            if (animal.AllocationConfirmed && (!animal.IdentityConfirmed || string.IsNullOrWhiteSpace(animal.Group)))
                throw new ArgumentException("Confirmed allocation requires a confirmed animal identity and group.");
            if (animal.Excluded && string.IsNullOrWhiteSpace(animal.ExclusionReason)) throw new ArgumentException("Planned exclusions require a reason.");
        }
        foreach (var context in Contexts)
        {
            if (!Enum.IsDefined(context.Phase) || !Enum.IsDefined(context.Orientation) || context.Day < 1 || context.TemporalBinEdges.IsDefault)
                throw new ArgumentException("Context phase, day, orientation or temporal bins are invalid.");
            if (!FiniteNonnegative(context.RequestedExposureSeconds) || !FiniteNonnegative(context.ComparisonStartSeconds) || !FiniteNonnegative(context.ComparisonDurationSeconds))
                throw new ArgumentException("Planned times must be finite and nonnegative.");
            if (context.WindowConfirmed && (context.Phase == TrialMode.Unspecified || context.RequestedExposureSeconds <= 0 || context.ComparisonDurationSeconds <= 0 ||
                context.ComparisonStartSeconds + context.ComparisonDurationSeconds > context.RequestedExposureSeconds + 1e-9))
                throw new ArgumentException("A confirmed comparison interval must lie within its stated planned exposure.");
            if (context.AlignmentConfirmed && (context.Orientation == StudyOrientation.Unconfirmed || string.IsNullOrWhiteSpace(context.CommonCoordinateReference)))
                throw new ArgumentException("Confirmed alignment requires a named common reference and orientation.");
            if (!context.TemporalBinEdges.IsEmpty)
            {
                if (context.TemporalBinEdges.Length < 2 || context.TemporalBinEdges[0] != 0 ||
                    Math.Abs(context.TemporalBinEdges[^1] - context.ComparisonDurationSeconds) > 1e-9 ||
                    context.TemporalBinEdges.Any(e => !FiniteNonnegative(e)) ||
                    context.TemporalBinEdges.Zip(context.TemporalBinEdges.Skip(1)).Any(pair => pair.First >= pair.Second))
                    throw new ArgumentException("Temporal bins must increase from zero to the planned comparison duration.");
            }
        }
        var animals = Animals.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contexts = Contexts.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recordings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trial in Trials)
        {
            if (!animals.Contains(trial.AnimalId) || !contexts.Contains(trial.ContextId) || trial.Order < 1)
                throw new ArgumentException("Every trial must name a planned animal/context and positive order.");
            // Length prefixes avoid collisions when researcher labels contain separators.
            var key = trial.AnimalId.Length + ":" + trial.AnimalId + trial.ContextId.Length + ":" + trial.ContextId + ":" + trial.Order;
            if (!slots.Add(key)) throw new ArgumentException("Animal/context/trial order is duplicated.");
            if (trial.RecordingId is { } id && (string.IsNullOrWhiteSpace(id) || !recordings.Add(id)))
                throw new ArgumentException("A recording can fill only one planned trial slot.");
            if (!double.IsFinite(trial.RoomRotationRadians) || Math.Abs(trial.RoomRotationRadians) > Math.PI * 2)
                throw new ArgumentException("Source room rotation must be a finite angle within one full turn.");
            if (trial.SourceReferenceConfirmed && (trial.RecordingId is null || !Hash(trial.ReferenceSourceSha256) || !Hash(trial.ReferenceSettingsHash)))
                throw new ArgumentException("Confirmed source references must identify the assigned source and geometry/settings hashes.");
        }
    }
    private static bool FiniteNonnegative(double value) => double.IsFinite(value) && value >= 0;
    private static bool Hash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static void Unique(IEnumerable<string> values, string label)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values.Any(v => string.IsNullOrWhiteSpace(v) || !ids.Add(v))) throw new ArgumentException("Planned " + label + " identifiers are blank or duplicated.");
    }
}

public sealed record PlannedTrialInventory(PlannedTrial Trial, VideoAsset? Recording, ImmutableArray<string> Holds);

public static class StudyPlanInventory
{
    public static ImmutableArray<VideoAsset> Unassigned(StudyPlan plan, IEnumerable<VideoAsset> imported)
    {
        plan.Validate();
        var assigned = plan.Trials.Where(t => t.RecordingId is not null).Select(t => t.RecordingId!).ToHashSet(StringComparer.Ordinal);
        return imported.Where(v => !assigned.Contains(v.Id)).ToImmutableArray();
    }

    public static ImmutableArray<PlannedTrialInventory> Build(StudyPlan plan, IEnumerable<VideoAsset> imported)
    {
        plan.Validate();
        var recordings = imported.ToDictionary(v => v.Id, StringComparer.Ordinal);
        return plan.Trials.Select(trial =>
        {
            var animal = plan.Animals.Single(a => string.Equals(a.Id, trial.AnimalId, StringComparison.OrdinalIgnoreCase));
            var context = plan.Contexts.Single(c => string.Equals(c.Id, trial.ContextId, StringComparison.OrdinalIgnoreCase));
            var holds = ImmutableArray.CreateBuilder<string>();
            recordings.TryGetValue(trial.RecordingId ?? "", out var recording);
            if (!plan.RosterConfirmed) holds.Add("Expected trial roster is unconfirmed.");
            if (!animal.IdentityConfirmed) holds.Add("Animal identity is unconfirmed.");
            if (!animal.AllocationConfirmed) holds.Add("Group allocation is unconfirmed.");
            if (animal.Excluded) holds.Add("Animal excluded: " + animal.ExclusionReason);
            if (!context.WindowConfirmed) holds.Add("Planned comparison window is unconfirmed.");
            if (!context.AlignmentConfirmed) holds.Add("Common spatial reference is unconfirmed.");
            if (recording is null) holds.Add("Planned recording missing" + (string.IsNullOrWhiteSpace(trial.MissingReason) ? "; reason not recorded." : ": " + trial.MissingReason));
            else
            {
                if (!recording.AnimalIdConfirmed || !string.Equals(recording.AnimalId, animal.Id, StringComparison.OrdinalIgnoreCase)) holds.Add("Recording animal identity does not match its planned slot.");
                if (!string.Equals(recording.GroupName, animal.Group, StringComparison.Ordinal)) holds.Add("Recording group differs from planned allocation.");
                if (recording.Phase != context.Phase || recording.Day != context.Day || recording.Trial != trial.Order) holds.Add("Recording phase/day/trial differs from its planned context.");
                if (!recording.Included) holds.Add("Recording excluded: " + (string.IsNullOrWhiteSpace(recording.ExclusionReason) ? "reason not recorded" : recording.ExclusionReason));
            }
            return new PlannedTrialInventory(trial, recording, holds.ToImmutable());
        }).ToImmutableArray();
    }
}
