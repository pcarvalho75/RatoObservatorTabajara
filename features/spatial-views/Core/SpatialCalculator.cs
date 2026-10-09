using System.Collections.Immutable;

namespace MouseTrackerTabajara.SpatialViews;

public static class SpatialCalculator
{
    public static TrialSpatialMap Trial(SpatialTrialInput input)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(input.Grid);
        input.Grid.Validate();
        if (string.IsNullOrWhiteSpace(input.TrialId) || string.IsNullOrWhiteSpace(input.RunId) || string.IsNullOrWhiteSpace(input.SnapshotRevision) ||
            string.IsNullOrWhiteSpace(input.TransformRevision) || string.IsNullOrWhiteSpace(input.ContextKey) || string.IsNullOrWhiteSpace(input.WindowBasis) ||
            !double.IsFinite(input.PlannedSeconds) || input.PlannedSeconds <= 0 || input.Frames.IsDefault)
            throw new ArgumentException("Trial requires frozen identities, coordinate/window context and a positive planned exposure.");
        if (string.IsNullOrWhiteSpace(input.SourceSha256) || input.SourceSha256.Length != 64 || !input.SourceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Source SHA256 must contain64 hexadecimal characters.");
        input = input with { SourceSha256 = input.SourceSha256.ToLowerInvariant() };
        var cells = new double[input.Grid.CellCount]; var known = 0d; SpatialFrame? previous = null;
        foreach (var frame in input.Frames)
        {
            if (frame is null || frame.Frame < 0 || !double.IsFinite(frame.StartSeconds) || !double.IsFinite(frame.EndSeconds) || frame.EndSeconds <= frame.StartSeconds ||
                previous is not null && (frame.Frame <= previous.Frame || frame.StartSeconds < previous.EndSeconds))
                throw new ArgumentException("Canonical frames must have ordered non-overlapping exposure intervals; malformed time cannot become occupancy.");
            previous = frame;
            int? cell = null;
            if (frame.Accepted)
            {
                if (!frame.X.HasValue || !frame.Y.HasValue) throw new ArgumentException("Accepted frames need both coordinates.");
                cell = input.Grid.Cell(frame.X.Value, frame.Y.Value);
            }
            var duration = Math.Max(0, Math.Min(input.PlannedSeconds, frame.EndSeconds) - Math.Max(0, frame.StartSeconds));
            if (duration == 0 || cell is null) continue;
            cells[cell.Value] += duration / input.PlannedSeconds; known += duration / input.PlannedSeconds;
        }
        if (known > 1 + 1e-10) throw new ArgumentException("Accepted exposure exceeds the planned trial window.");
        var holds = ImmutableArray.CreateBuilder<string>();
        if (!input.WindowConfirmed) holds.Add("Trial release and planned window are unconfirmed.");
        if (!input.GeometryConfirmed) holds.Add("Coordinate transform/geometry is unconfirmed.");
        if (!input.Reviewed) holds.Add("Tracking has not been reviewed against the original recording.");
        if (input.Frames.IsEmpty || input.Frames[0].StartSeconds > 0 || input.Frames[^1].EndSeconds < input.PlannedSeconds)
            holds.Add("The source run does not cover the planned window; do not shorten exposure to improve tracking.");
        return new(input, cells.ToImmutableArray(), Math.Max(0, 1 - Math.Min(1, known)), holds.ToImmutable());
    }

    public static ImmutableArray<TrajectorySegment> Trajectory(SpatialTrialInput input)
    {
        _ = Trial(input); // Use the same canonical time and coordinate validation.
        var segments = ImmutableArray.CreateBuilder<TrajectorySegment>();
        var vertices = ImmutableArray.CreateBuilder<TrajectoryVertex>(); SpatialFrame? previous = null;
        foreach (var frame in input.Frames)
        {
            var inWindow = frame.EndSeconds > 0 && frame.StartSeconds < input.PlannedSeconds;
            if (!frame.Accepted || !inWindow || previous is not null && (frame.Frame != previous.Frame + 1 || frame.StartSeconds != previous.EndSeconds)) Flush();
            if (frame.Accepted && inWindow) vertices.Add(new(frame.Frame, Math.Max(0, frame.StartSeconds), frame.X!.Value, frame.Y!.Value));
            previous = frame;
        }
        Flush(); return segments.ToImmutable();
        void Flush() { if (vertices.Count > 0) { segments.Add(new(vertices.ToImmutable())); vertices.Clear(); } }
    }

    public static ImmutableArray<TemporalSpatialBin> TemporalBins(SpatialTrialInput input, ImmutableArray<double> boundaries)
    {
        _ = Trial(input);
        if (boundaries.IsDefault || boundaries.Length < 2 || boundaries.Length > 1000 || boundaries[0] != 0 || boundaries[^1] != input.PlannedSeconds ||
            boundaries.Any(v => !double.IsFinite(v)) || boundaries.Zip(boundaries.Skip(1)).Any(p => p.First >= p.Second) ||
            (long)input.Grid.CellCount * (boundaries.Length - 1) > 2_000_000)
            throw new ArgumentException("Prespecified bins must cover the entire planned window and respect the memory budget.");
        var output = ImmutableArray.CreateBuilder<TemporalSpatialBin>();
        for (var i = 0; i < boundaries.Length - 1; i++)
        {
            var start = boundaries[i]; var end = boundaries[i + 1];
            var shifted = input.Frames.Select(f => f with { StartSeconds = f.StartSeconds - start, EndSeconds = f.EndSeconds - start }).ToImmutableArray();
            output.Add(new(start, end, Trial(input with { Frames = shifted, PlannedSeconds = end - start })));
        }
        return output.ToImmutable();
    }

    public static AnimalSpatialMap Animal(AnimalSpatialInput input, SpatialComparisonScope scope)
    {
        ArgumentNullException.ThrowIfNull(input); ValidateScope(scope);
        if (string.IsNullOrWhiteSpace(input.AnimalId) || string.IsNullOrWhiteSpace(input.DisplayAlias) || input.ExpectedTrialIds.IsDefault || input.Trials.IsDefault)
            throw new ArgumentException("Animal requires an explicit identity/alias and expected/actual trial lists.");
        if (input.Excluded && string.IsNullOrWhiteSpace(input.ExclusionReason)) throw new ArgumentException("An exclusion reason is required.");
        var holds = ImmutableArray.CreateBuilder<string>();
        if (!input.IdentityConfirmed) holds.Add("Animal identity is provisional; biological N is withheld.");
        if (string.IsNullOrWhiteSpace(input.Group)) holds.Add("Group allocation is unspecified.");
        if (!input.RosterConfirmed || input.ExpectedTrialIds.IsEmpty) holds.Add("Planned trial roster is unconfirmed; completeness is unknown.");
        if (input.ExpectedTrialIds.Any(string.IsNullOrWhiteSpace) || input.ExpectedTrialIds.Distinct(StringComparer.Ordinal).Count() != input.ExpectedTrialIds.Length)
            throw new ArgumentException("Planned trial identities must be unique and nonempty.");
        if (input.Excluded) holds.Add("Excluded: " + input.ExclusionReason);
        var actual = input.Trials.Select(t => t.Input.TrialId).ToArray();
        if (actual.Distinct(StringComparer.Ordinal).Count() != actual.Length) holds.Add("Duplicate animal/trial slot; review its sources.");
        if (input.Trials.Select(t => t.Input.SourceSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.Trials.Length) holds.Add("Duplicate recording bytes cannot be counted as another trial.");
        if (input.ExpectedTrialIds.Except(actual, StringComparer.Ordinal).Any()) holds.Add("Some planned trials are absent; no silent complete-case selection.");
        if (actual.Except(input.ExpectedTrialIds, StringComparer.Ordinal).Any()) holds.Add("Unplanned trials are present; confirm a revised roster.");
        foreach (var map in input.Trials)
        {
            var rebuilt = Trial(map.Input);
            if (!map.KnownFractions.SequenceEqual(rebuilt.KnownFractions) || map.UnknownFraction != rebuilt.UnknownFraction || !map.HoldReasons.SequenceEqual(rebuilt.HoldReasons))
                throw new InvalidDataException("Spatial map differs from its frozen frame inputs; recompute instead of trusting edited cached values.");
            holds.AddRange(map.HoldReasons.Select(r => map.Input.TrialId + ": " + r));
            if (map.Input.Grid != scope.Grid || map.Input.ContextKey != scope.ContextKey || map.Input.WindowBasis != scope.WindowBasis || map.Input.PlannedSeconds != scope.PlannedSeconds)
                holds.Add(map.Input.TrialId + ": Coordinates, phase/context, window basis or planned exposure differ from the comparison contract.");
        }
        if (holds.Count > 0) return new(input, scope.Grid, scope.ContextKey, scope.WindowBasis, scope.PlannedSeconds, [], null, holds.Distinct(StringComparer.Ordinal).ToImmutableArray());
        var cells = Enumerable.Range(0, scope.Grid.CellCount).Select(i => input.Trials.Average(t => t.KnownFractions[i])).ToImmutableArray();
        return new(input, scope.Grid, scope.ContextKey, scope.WindowBasis, scope.PlannedSeconds, cells, input.Trials.Average(t => t.UnknownFraction), []);
    }

    public static SpatialStudy Study(ImmutableArray<AnimalSpatialInput> inputs, SpatialComparisonScope scope)
    {
        ValidateScope(scope);
        if (inputs.IsDefault) throw new ArgumentException("Supply an explicit animal inventory.");
        var animals = inputs.Select(a => Animal(a, scope)).ToArray();
        var duplicatedAnimalIds = inputs.GroupBy(a => a.AnimalId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateSources = inputs.SelectMany(a => a.Trials.Select(t => (a.AnimalId, t.Input.SourceSha256))).GroupBy(t => t.SourceSha256, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(t => t.AnimalId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < animals.Length; i++)
        {
            var extra = ImmutableArray.CreateBuilder<string>();
            if (duplicatedAnimalIds.Contains(animals[i].Input.AnimalId)) extra.Add("Duplicate/conflicting animal identity occurs in the inventory.");
            if (animals[i].Input.Trials.Any(t => duplicateSources.Contains(t.Input.SourceSha256))) extra.Add("Recording bytes are assigned to more than one animal.");
            if (extra.Count > 0) animals[i] = animals[i] with { KnownFractions = [], UnknownFraction = null, HoldReasons = animals[i].HoldReasons.AddRange(extra) };
        }
        var groups = ImmutableArray.CreateBuilder<SpatialGroupMap>();
        foreach (var group in animals.GroupBy(a => a.Input.Group, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var valid = group.Where(a => a.Eligible).OrderBy(a => a.Input.AnimalId, StringComparer.Ordinal).ToArray();
            if (valid.Length == 0) continue;
            groups.Add(new(group.Key, scope.Grid, scope.ContextKey, scope.WindowBasis, scope.PlannedSeconds,
                valid.Select(a => a.Input.AnimalId).ToImmutableArray(), group.Where(a => !a.Eligible).Select(a => a.Input.AnimalId).ToImmutableArray(),
                Enumerable.Range(0, scope.Grid.CellCount).Select(i => valid.Average(a => a.KnownFractions[i])).ToImmutableArray(), valid.Average(a => a.UnknownFraction!.Value)));
        }
        var maximum = groups.Count == 0 ? 0 : groups.SelectMany(g => g.KnownFractions).Max();
        return new(animals.ToImmutableArray(), groups.ToImmutable(), maximum);
    }

    public static GroupDifferenceBounds Difference(SpatialGroupMap a, SpatialGroupMap b)
    {
        if (a.Grid != b.Grid || a.ContextKey != b.ContextKey || a.WindowBasis != b.WindowBasis || a.PlannedSeconds != b.PlannedSeconds)
            throw new ArgumentException("Group maps have incompatible units, coordinate reference, context or exposure.");
        var cells = a.KnownFractions.Select((value, i) => new CellBounds(value - Math.Min(1, b.KnownFractions[i] + b.UnknownFraction),
            Math.Min(1, value + a.UnknownFraction) - b.KnownFractions[i])).ToImmutableArray();
        return new(a.Group, b.Group, cells);
    }
    private static void ValidateScope(SpatialComparisonScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(scope.Grid); scope.Grid.Validate();
        if (string.IsNullOrWhiteSpace(scope.ContextKey) || string.IsNullOrWhiteSpace(scope.WindowBasis) || !double.IsFinite(scope.PlannedSeconds) || scope.PlannedSeconds <= 0)
            throw new ArgumentException("Comparison must prespecify context, window basis and exposure.");
    }
}
