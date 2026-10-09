using System.Collections.Immutable;
using Xunit;

namespace MouseTrackerTabajara.SpatialViews.Tests;

public class SpatialTests
{
    private static readonly GridDefinition Grid = new(2, 2, -1, -1, 1, 1, "pool radii", "study26-pool", "room-N");
    private static readonly SpatialComparisonScope Scope = new(Grid, "probe-day1", "confirmed-release-fixed", 10);
    private static TrialSpatialMap Map(string id, int source, bool right = false, double unknown = 0) => SpatialCalculator.Trial(new(id,
        "run-" + id, source.ToString("x64"), "snapshot1", "transform-" + source, Scope.ContextKey, Scope.WindowBasis, 10, true, true, true, Grid,
        [new(0, 0, 10 * (1 - unknown), right ? .5 : -.5, -.5, true), ..(unknown > 0 ? new SpatialFrame[] { new(1, 10 * (1 - unknown), 10, null, null, false) } : [])]));
    private static AnimalSpatialInput Animal(string id, string group, params TrialSpatialMap[] trials) => new(id, "Alias-" + id, group, true, true,
        trials.Select(t => t.Input.TrialId).ToImmutableArray(), trials.ToImmutableArray());

    [Fact] public void KnownAndUnknownExposureSumToPlannedDurationWithoutInterpolation()
    {
        var map = Map("T1", 1, unknown: .4);
        Assert.Equal(.6, map.KnownFractions[0], 12); Assert.Equal(.4, map.UnknownFraction, 12);
        Assert.Equal(1, map.KnownFractions.Sum() + map.UnknownFraction, 12); Assert.True(map.Eligible);
        var segments = SpatialCalculator.Trajectory(map.Input); Assert.Single(segments); Assert.Single(segments[0].Vertices);
    }
    [Fact] public void MissingIntervalsAndFrameGapsNeverBecomePathSegments()
    {
        var input = Map("T1", 1).Input with { Frames = [new(0, -2, 1, -.5, -.5, true), new(3, 4, 10, .5, .5, true), new(4, 10, 12, .5, .5, true)] };
        var map = SpatialCalculator.Trial(input);
        Assert.Equal(.3, map.UnknownFraction, 12); Assert.Equal(.1, map.KnownFractions[0], 12); Assert.Equal(.6, map.KnownFractions[3], 12);
        Assert.Equal(2, SpatialCalculator.Trajectory(input).Length);
    }
    [Fact] public void ThirtyIndependentAnimalsProduceEqualAnimalGroupMapsAndSharedScale()
    {
        var animals = Enumerable.Range(0, 30).Select(i => Animal("M" + i, i < 15 ? "A" : "B", Map("T" + i, i + 1, right: i >= 15))).ToImmutableArray();
        var study = SpatialCalculator.Study(animals, Scope);
        Assert.Equal(30, study.Animals.Count(a => a.Eligible)); Assert.Equal(2, study.Groups.Length); Assert.All(study.Groups, g => Assert.Equal(15, g.IncludedAnimalIds.Length));
        Assert.Equal(1, study.Groups[0].KnownFractions[0]); Assert.Equal(1, study.Groups[1].KnownFractions[1]); Assert.Equal(1, study.SharedKnownFractionMaximum);
    }
    [Fact] public void UnequalTrialCountsDoNotOverweightAnAnimal()
    {
        var a = Animal("M1", "A", Map("T1", 1), Map("T2", 2));
        var b = Animal("M2", "A", Map("T3", 3, true));
        var group = Assert.Single(SpatialCalculator.Study([a, b], Scope).Groups);
        Assert.Equal(.5, group.KnownFractions[0]); Assert.Equal(.5, group.KnownFractions[1]); Assert.Equal(2, group.IncludedAnimalIds.Length);
        var extraTrial = a with { Trials = a.Trials.Add(Map("T4", 4)), ExpectedTrialIds = a.ExpectedTrialIds.Add("T4") };
        var repeated = Assert.Single(SpatialCalculator.Study([extraTrial, b], Scope).Groups);
        Assert.Equal(group.KnownFractions.ToArray(), repeated.KnownFractions.ToArray());
    }
    [Fact] public void DuplicateBytesSlotsAndCrossAnimalIdentityAreHeldAndRemainVisible()
    {
        var one = Map("T1", 1); var copiedBytes = Map("T2", 1);
        var repeated = SpatialCalculator.Study([Animal("M1", "A", one, copiedBytes)], Scope);
        Assert.False(repeated.Animals[0].Eligible); Assert.Empty(repeated.Groups); Assert.Contains(repeated.Animals[0].HoldReasons, r => r.Contains("Duplicate recording", StringComparison.Ordinal));
        var twoAnimals = SpatialCalculator.Study([Animal("M1", "A", one), Animal("M2", "B", copiedBytes)], Scope);
        Assert.All(twoAnimals.Animals, a => Assert.False(a.Eligible)); Assert.Empty(twoAnimals.Groups);
        var sameIdentity = SpatialCalculator.Study([Animal("M1", "A", one), Animal("m1", "B", Map("T3", 3))], Scope);
        Assert.All(sameIdentity.Animals, a => Assert.Contains(a.HoldReasons, r => r.Contains("animal identity", StringComparison.Ordinal)));
    }
    [Fact] public void PlannedMissingTrialAndUnknownRosterCannotLicenseSilentCompleteCaseMeans()
    {
        var a = Animal("M1", "A", Map("T1", 1));
        var study = SpatialCalculator.Study([a with { ExpectedTrialIds = ["T1", "T2"] }, Animal("M2", "A", Map("T3", 3))], Scope);
        var group = Assert.Single(study.Groups); Assert.Equal(["M2"], group.IncludedAnimalIds.ToArray()); Assert.Equal(["M1"], group.HeldAnimalIds.ToArray());
        var unknown = SpatialCalculator.Animal(a with { RosterConfirmed = false }, Scope);
        Assert.False(unknown.Eligible); Assert.Contains(unknown.HoldReasons, r => r.Contains("completeness is unknown", StringComparison.Ordinal));
    }
    [Fact] public void UnconfirmedIdentityReviewGeometryAndSourceTruncationRemainHeld()
    {
        var map = Map("T1", 1); var held = SpatialCalculator.Trial(map.Input with { WindowConfirmed = false, GeometryConfirmed = false, Reviewed = false, Frames = [new(0, 0, 8, -.5, -.5, true)] });
        Assert.False(held.Eligible); Assert.Equal(4, held.HoldReasons.Length); Assert.Equal(.2, held.UnknownFraction, 12);
        var animal = SpatialCalculator.Animal(Animal("M1", "A", map) with { IdentityConfirmed = false }, Scope); Assert.False(animal.Eligible);
    }
    [Fact] public void UnequalWindowsUnitsOrientationAndContextDoNotEnterOneComparison()
    {
        var map = Map("T1", 1);
        foreach (var input in new[] { map.Input with { PlannedSeconds = 12 }, map.Input with { ContextKey = "training-day1" }, map.Input with { WindowBasis = "recording-start" },
            map.Input with { Grid = Grid with { CoordinateUnit = "cm" } }, map.Input with { Grid = Grid with { Orientation = "target-aligned" } } })
        {
            var changed = SpatialCalculator.Trial(input); var animal = SpatialCalculator.Animal(Animal("M1", "A", changed), Scope);
            Assert.False(animal.Eligible); Assert.Contains(animal.HoldReasons, r => r.Contains("comparison contract", StringComparison.Ordinal));
        }
    }
    [Fact] public void PerCellDifferenceBoundsWidenWithUnknownTimeAndAreNotConfidenceIntervals()
    {
        var study = SpatialCalculator.Study([Animal("M1", "A", Map("T1", 1, unknown: .6)), Animal("M2", "B", Map("T2", 2, unknown: .3))], Scope);
        var bounds = SpatialCalculator.Difference(study.Groups[0], study.Groups[1]);
        Assert.Equal(-.6, bounds.Cells[0].Lower, 12); Assert.Equal(.3, bounds.Cells[0].Upper, 12);
        Assert.Contains("not confidence intervals", bounds.Interpretation); Assert.InRange(-.2, bounds.Cells[0].Lower, bounds.Cells[0].Upper);
        Assert.Throws<ArgumentException>(() => SpatialCalculator.Difference(study.Groups[0], study.Groups[1] with { ContextKey = "other" }));
    }
    [Fact] public void PrespecifiedTemporalBinsKeepUnknownExposureAndCoverTheWholeWindow()
    {
        var bins = SpatialCalculator.TemporalBins(Map("T1", 1, unknown: .4).Input, [0, 5, 10]);
        Assert.Equal(2, bins.Length); Assert.Equal(0, bins[0].Map.UnknownFraction, 12); Assert.Equal(.8, bins[1].Map.UnknownFraction, 12);
        Assert.Throws<ArgumentException>(() => SpatialCalculator.TemporalBins(Map("T1", 1).Input, [0, 5, 8]));
    }
    [Fact] public void InvalidAcceptedPointsTimingAndEditedCachesFailClosed()
    {
        var map = Map("T1", 1);
        Assert.Throws<ArgumentException>(() => SpatialCalculator.Trial(map.Input with { Frames = [new(0, 0, 10, 2, 0, true)] }));
        Assert.Throws<ArgumentException>(() => SpatialCalculator.Trial(map.Input with { Frames = [new(0, 0, 10, null, -.5, true)] }));
        Assert.Throws<ArgumentException>(() => SpatialCalculator.Trial(map.Input with { Frames = [new(0, 0, 6, 0, 0, true), new(1, 5, 10, 0, 0, true)] }));
        Assert.Throws<InvalidDataException>(() => SpatialCalculator.Animal(Animal("M1", "A", map with { KnownFractions = [0, 0, 0, 1] }), Scope));
        Assert.Throws<ArgumentException>(() => SpatialCalculator.Trial(map.Input with { Grid = Grid with { CoordinateUnit = "pixels" } }));
    }
    [Fact] public void ExclusionNeedsReasonAndNoCoverageThresholdLicensesReview()
    {
        var animal = Animal("M1", "A", Map("T1", 1));
        Assert.Throws<ArgumentException>(() => SpatialCalculator.Animal(animal with { Excluded = true }, Scope));
        var excluded = SpatialCalculator.Animal(animal with { Excluded = true, ExclusionReason = "Protocol intervention" }, Scope);
        Assert.False(excluded.Eligible); Assert.Contains("Excluded: Protocol intervention", excluded.HoldReasons);
        var unreviewed = SpatialCalculator.Trial(Map("T1", 1).Input with { Reviewed = false }); Assert.False(unreviewed.Eligible);
    }
}
