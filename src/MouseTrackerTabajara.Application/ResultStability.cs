using System.Collections.Immutable;

namespace MouseTrackerTabajara.Application;

/// <summary>Consumer material; eligibility must be supplied by a validated frozen producer adapter.</summary>
public sealed record StabilityScope(string Context, string Phase, int Day, string Window, string Unit, string Orientation, string Endpoint);
public sealed record StabilityIdentity(string Source, string Run, string Correction, string Plan, string Window, string Region, string Profile, string Code);
public sealed record StabilityValue(double? Value, double? Lower, double? Upper);
public sealed record StabilityDeclaredRange(string Parameter, string Unit, double Minimum, double Maximum);
public sealed record StabilityTrace(string SourcePath, string SourceSha256, string RunPath, string RunSha256, int FirstFrame, int LastFrame)
{
    public string RunJson { get; init; } = "";
    public string MeasurementSettingsJson { get; init; } = "";
    public int? OutlierFrame { get; init; }
    public string TransformEvidenceJson { get; init; } = "";
}
public sealed record StabilityTrial(string CaseId, string TrialId, string AnimalId, string Group, string Scenario, string Mode, string DeclaredRange,
    StabilityScope Scope, StabilityIdentity BaselineIdentity, StabilityIdentity ScenarioIdentity, StabilityValue Original, StabilityValue Changed,
    bool Supported, bool Excluded, ImmutableArray<string> Holds, StabilityTrace? Trace)
{
    public StabilityScope? OriginalScope { get; init; }
    public int? AnalyzedFrames { get; init; }
    public double? PlannedSeconds { get; init; }
    public double? UnknownSeconds { get; init; }
    public string ContributionNote { get; init; } = "";
    public ImmutableArray<StabilityDeclaredRange> NumericRanges { get; init; } = [];
}
public sealed record StabilityAnimal(string AnimalId, string Group, int PlannedTrials, int IncludedTrials, StabilityValue Original, StabilityValue Changed, ImmutableArray<string> Holds);
public sealed record StabilityContrast(string Scenario, StabilityScope Scope, string GroupA, string GroupB, int PlannedAnimals, int IncludedAnimals,
    int HeldAnimals, int ExcludedAnimals, StabilityValue Original, StabilityValue Changed, string Conclusion, ImmutableArray<StabilityAnimal> Animals);
public sealed record StabilityEnvelope(StabilityScope Scope, string GroupA, string GroupB, double? Minimum, double? Maximum, ImmutableArray<string> Contributors, ImmutableArray<string> HeldScenarios)
{
    public string MembershipReferenceScenario { get; init; } = "";
    public ImmutableArray<string> MatchedAnimalIds { get; init; } = [];
    public ImmutableArray<string> MembershipChangingScenarios { get; init; } = [];
}
public sealed record StabilityProducerInput(string Kind, string Path, string Sha256, string Json);
public sealed record StabilityReport(ImmutableArray<StabilityTrial> Trials, ImmutableArray<StabilityContrast> Contrasts)
{
    public ImmutableArray<StabilityEnvelope> Envelopes { get; init; } = [];
    public ImmutableArray<StabilityProducerInput> Inputs { get; init; } = [];
    public const string Limits = "Conditional on accepted coordinates and frozen confirmed windows. Missing-position bounds are not confidence intervals. Scenario envelopes describe only the declared scenarios. Sampling uncertainty is not estimated. No independent accuracy, causal memory or inferential claim.";
}

public static class ResultStability
{
    public static StabilityReport Build(IEnumerable<StabilityTrial> material, CancellationToken cancellation = default)
    {
        var initial = material.Select(row => { cancellation.ThrowIfCancellationRequested(); return Validate(row); }).ToImmutableArray();
        var cohorts = initial.GroupBy(row => (row.Scenario, row.Scope)).ToDictionary(group => group.Key, group => group.ToArray());
        var allocations = initial.GroupBy(row => (row.Scenario, row.Scope, row.AnimalId)).ToDictionary(group => group.Key, group => group.Select(row => row.Group).Distinct(StringComparer.Ordinal).Count());
        var cohortMismatch = cohorts.ToDictionary(pair => pair.Key, pair => pair.Value.Select(row => row.BaselineIdentity.Code).Distinct(StringComparer.Ordinal).Count() > 1 || pair.Value.Select(row => row.BaselineIdentity.Plan).Distinct(StringComparer.Ordinal).Count() > 1);
        var originals = initial.GroupBy(row => (row.Scope, row.TrialId)).ToDictionary(group => group.Key, group => group.Select(row => (row.Original, row.BaselineIdentity)).Distinct().Count());
        var trials = initial.Select(t =>
        {
            var holds = t.Holds;
            if (cohortMismatch[(t.Scenario, t.Scope)])
                holds = holds.Add("Cohort frozen code or plan identities differ.");
            if (allocations[(t.Scenario, t.Scope, t.AnimalId)] > 1)
                holds = holds.Add("Animal allocation differs across planned trials.");
            if (originals[(t.Scope, t.TrialId)] > 1) holds = holds.Add("Frozen original trial material differs across named scenarios.");
            return t with { Holds = holds.Distinct(StringComparer.Ordinal).ToImmutableArray() };
        }).ToImmutableArray();
        if (trials.Select(t => t.CaseId).Distinct(StringComparer.Ordinal).Count() != trials.Length)
            throw new InvalidDataException("Duplicate stability case identity.");
        var contrasts = ImmutableArray.CreateBuilder<StabilityContrast>();
        foreach (var matrix in trials.GroupBy(t => (t.Scenario, t.Scope)))
        {
            cancellation.ThrowIfCancellationRequested();
            var animals = matrix.GroupBy(t => (t.AnimalId, t.Group)).Select(group => Animal(group.ToArray())).ToImmutableArray();
            var groups = animals.Select(a => a.Group).Where(g => g.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            for (var a = 0; a < groups.Length; a++) for (var b = a + 1; b < groups.Length; b++)
            {
                var selected = animals.Where(row => row.Group == groups[a] || row.Group == groups[b]).ToImmutableArray();
                var eligibleA = selected.Where(row => row.Group == groups[a] && row.Holds.IsEmpty).ToArray();
                var eligibleB = selected.Where(row => row.Group == groups[b] && row.Holds.IsEmpty).ToArray();
                var original = Difference(eligibleA.Select(row => row.Original), eligibleB.Select(row => row.Original));
                var changed = Difference(eligibleA.Select(row => row.Changed), eligibleB.Select(row => row.Changed));
                var excluded = matrix.Where(t => t.Excluded && (t.Group == groups[a] || t.Group == groups[b])).Select(t => t.AnimalId).Distinct(StringComparer.Ordinal).Count();
                contrasts.Add(new(matrix.Key.Scenario, matrix.Key.Scope, groups[a], groups[b], selected.Length, eligibleA.Length + eligibleB.Length,
                    selected.Count(row => !row.Holds.IsEmpty), excluded, original, changed, Conclusion(original, changed, selected.Any(row => !row.Holds.IsEmpty)), selected));
            }
        }
        var comparisons = contrasts.ToImmutable();
        var envelopes = comparisons.GroupBy(c => (c.Scope, c.GroupA, c.GroupB)).Select(group =>
        {
            // Use the explicitly named original if supplied; otherwise the largest frozen eligible cohort,
            // chosen without inspecting values. A smaller/different cohort cannot measure parameter effects alone.
            var reference = group.FirstOrDefault(c => c.Scenario == "original") ?? group.OrderByDescending(c => c.IncludedAnimals).ThenBy(c => c.Scenario, StringComparer.Ordinal).First();
            var members = reference.Animals.Where(a => a.Holds.IsEmpty).Select(a => a.AnimalId).Order(StringComparer.Ordinal).ToImmutableArray();
            bool SameMembers(StabilityContrast c) => members.SequenceEqual(c.Animals.Where(a => a.Holds.IsEmpty).Select(a => a.AnimalId).Order(StringComparer.Ordinal));
            var numeric = group.Where(c => c.Changed.Value.HasValue && SameMembers(c)).ToArray();
            return new StabilityEnvelope(group.Key.Scope, group.Key.GroupA, group.Key.GroupB, numeric.Length == 0 ? null : numeric.Min(c => c.Changed.Value),
                numeric.Length == 0 ? null : numeric.Max(c => c.Changed.Value), numeric.Select(c => c.Scenario).ToImmutableArray(), group.Where(c => c.HeldAnimals > 0 || !c.Changed.Value.HasValue || !SameMembers(c)).Select(c => c.Scenario).ToImmutableArray())
                { MembershipReferenceScenario = reference.Scenario, MatchedAnimalIds = members, MembershipChangingScenarios = group.Where(c => !SameMembers(c)).Select(c => c.Scenario).ToImmutableArray() };
        }).ToImmutableArray();
        return new(trials, comparisons) { Envelopes = envelopes };
    }

    public static StabilityTrial Validate(StabilityTrial trial)
    {
        var holds = trial.Holds.ToBuilder();
        if (trial.NumericRanges.Any(range => !double.IsFinite(range.Minimum) || !double.IsFinite(range.Maximum) || range.Minimum > range.Maximum)) holds.Add("Prespecified numeric scenario range is invalid.");
        if (trial.OriginalScope is null || trial.OriginalScope != trial.Scope) holds.Add("Original/scenario phase/day/window/unit/orientation/endpoint scope differs or is absent.");
        if (string.IsNullOrWhiteSpace(trial.AnimalId) || string.IsNullOrWhiteSpace(trial.TrialId) || string.IsNullOrWhiteSpace(trial.Group)) holds.Add("Confirmed animal/trial/allocation missing.");
        if (!trial.Supported) holds.Add("Supported confirmed-window endpoint is absent; raw decoder results are not biological trial endpoints.");
        if (trial.Excluded) holds.Add("Prespecified excluded animal/trial retained.");
        var x = trial.BaselineIdentity; var y = trial.ScenarioIdentity;
        if (new[] { x.Source, x.Run, x.Correction, x.Plan, x.Window, x.Code }.Any(string.IsNullOrWhiteSpace)) holds.Add("Frozen source/run/correction/plan/window/code identity missing.");
        // Region/profile changes are the disclosed scenario; source, raw run, corrections and protocol stay frozen.
        if (x.Source != y.Source || x.Run != y.Run || x.Correction != y.Correction || x.Plan != y.Plan || x.Window != y.Window || x.Code != y.Code)
            holds.Add("Scenario differs in frozen source/run/correction/plan/window/code identity.");
        if (!Valid(trial.Original) || !Valid(trial.Changed)) holds.Add("Conditional endpoint/bounds are absent or invalid.");
        if (new[] { trial.Scope.Context, trial.Scope.Phase, trial.Scope.Window, trial.Scope.Unit, trial.Scope.Orientation, trial.Scope.Endpoint }.Any(string.IsNullOrWhiteSpace)) holds.Add("Comparison scope is incomplete.");
        return trial with { Holds = holds.Distinct(StringComparer.Ordinal).ToImmutableArray() };
    }
    private static bool Valid(StabilityValue value) => value is { Value: double v, Lower: double lo, Upper: double hi } && double.IsFinite(v) && double.IsFinite(lo) && double.IsFinite(hi) && lo <= v && v <= hi;
    private static StabilityAnimal Animal(StabilityTrial[] rows)
    {
        var holds = rows.SelectMany(t => t.Holds).Distinct(StringComparer.Ordinal).ToImmutableArray();
        if (rows.Select(t => t.TrialId).Distinct(StringComparer.Ordinal).Count() != rows.Length) holds = holds.Add("Duplicate planned trial in this scenario.");
        var included = rows.Where(t => t.Holds.IsEmpty).ToArray();
        // A partly held animal is held as a whole. Do not silently average its remaining favorable trials.
        return new(rows[0].AnimalId, rows[0].Group, rows.Length, included.Length, holds.IsEmpty ? Mean(included.Select(t => t.Original)) : Empty,
            holds.IsEmpty ? Mean(included.Select(t => t.Changed)) : Empty, holds);
    }
    private static readonly StabilityValue Empty = new(null, null, null);
    private static StabilityValue Mean(IEnumerable<StabilityValue> input)
    {
        var rows = input.ToArray(); return rows.Length == 0 || rows.Any(v => !Valid(v)) ? Empty : new(rows.Average(v => v.Value!.Value), rows.Average(v => v.Lower!.Value), rows.Average(v => v.Upper!.Value));
    }
    private static StabilityValue Difference(IEnumerable<StabilityValue> a, IEnumerable<StabilityValue> b)
    {
        var x = Mean(a); var y = Mean(b); return !Valid(x) || !Valid(y) ? Empty : new(x.Value - y.Value, x.Lower - y.Upper, x.Upper - y.Lower);
    }
    private static int Sign(StabilityValue v) => v.Lower > 0 ? 1 : v.Upper < 0 ? -1 : 0;
    private static string Conclusion(StabilityValue original, StabilityValue changed, bool held)
    {
        if (!Valid(original) || !Valid(changed)) return "Unsupported: no matched eligible animals in both groups.";
        var prefix = held ? "Eligible-only; held population remains unresolved. " : "";
        if (Sign(original) == 0 || Sign(changed) == 0) return prefix + "Direction unresolved: missing-position interval includes zero.";
        return prefix + (Sign(original) == Sign(changed) ? "Stable direction within this declared scenario." : "Unstable: direction reverses in this declared scenario.");
    }
}
