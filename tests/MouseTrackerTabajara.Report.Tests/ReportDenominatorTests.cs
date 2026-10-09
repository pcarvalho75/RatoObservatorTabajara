using System.Collections.Immutable;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;

namespace MouseTrackerTabajara.Report.Tests;

public sealed partial class WholeStudyReportTests
{
    private static ReportEndpointDenominator ScalarDenominator(ReportContext context, string endpoint) => context.Denominators.Single(d => d.Endpoint.StartsWith(endpoint, StringComparison.Ordinal));
    private static void ReplaceFirstRun(Fixture fixture, bool unknownFirstBin, bool missingPlatform)
    {
        var asset = fixture.Assets[0] with { Settings = fixture.Assets[0].Settings with { Platform = missingPlatform ? null : fixture.Assets[0].Settings.Platform } };
        fixture.Store.SaveVideo(asset);
        var points = fixture.Runs[0].Points.Select(p => unknownFirstBin && p.Frame < 15 ? p with { X = null, Y = null, Status = PointStatus.Missing } : p).ToList();
        var run = fixture.Runs[0] with { Id = Guid.NewGuid().ToString("N"), Settings = asset.Settings, Points = points, Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings) };
        fixture.Store.SaveRun(run); fixture.Store.RecordReview(run, true);
        var plans = new StudyPlanStore(fixture.Store); var saved = plans.Latest()!;
        plans.Save(saved.Plan with { Trials = saved.Plan.Trials.Select(t => t.RecordingId == asset.Id ? t with { ReferenceSettingsHash = StudyStore.SettingsHash(asset.Settings) } : t).ToImmutableArray() }, "Reviewer", "Synthetic endpoint support fixture", saved.Revision);
    }
    [Fact] public void WholeWindowScalarTrialDenominatorDoesNotRequireEveryBinObserved()
    {
        using var fixture = new Fixture(); ReplaceFirstRun(fixture, true, false);
        var snapshot = AtlasSnapshot.Read(fixture.Store.DatabasePath); var result = ExperimentPlanAdapter.Build(snapshot, fixture.Plan.Contexts[0].Id); var temporal = TemporalProfiles.Build(result);
        var whole = temporal.AnimalDays.Single(d => d.AnimalId == "A" && d.ContextId == result.Context.Id); var trial = whole.Trials.Single(t => t.TrialId == "PRIVATE-trial-0");
        Assert.Equal(10d / 270, trial.AdjacentSpeedDiametersPerSecond!.Value, 10); Assert.Equal(0, trial.WallObservedFraction); Assert.True(trial.EligibleObservation);
        var empty = temporal.TrialBins.Single(b => b.TrialId == trial.TrialId && b.StartSeconds == 0); Assert.Equal(0, empty.KnownSeconds); Assert.Null(empty.AdjacentSpeedDiametersPerSecond); Assert.Null(empty.WallObservedFraction);
        Assert.NotNull(whole.Speed); Assert.NotNull(whole.Wall);
        var report = fixture.Compute().Document.Contexts[0];
        foreach (var endpoint in new[] { "adjacent speed pool diameters/s", "wall observed fraction" })
        { var denominator = ScalarDenominator(report, endpoint); Assert.Equal(7, denominator.IncludedAnimals); Assert.Equal(17, denominator.IncludedTrials); Assert.Equal(2, denominator.HeldAnimals); Assert.Equal(2, denominator.HeldTrials); Assert.Equal(1, denominator.ExcludedAnimals); Assert.Equal(2, denominator.ExcludedTrials); }
        Assert.Equal(whole.Speed, report.IndividualDays.Single(d => d.Animal == "A").Speed); Assert.Equal(whole.Wall, report.IndividualDays.Single(d => d.Animal == "A").Wall);
    }
    [Fact] public void MissingSiteDoesNotSuppressSupportedSpeedWallAndExpectedRepeatsRemainRequired()
    {
        using var fixture = new Fixture(); ReplaceFirstRun(fixture, false, true);
        var report = fixture.Compute().Document.Contexts[0]; var animal = report.IndividualDays.Single(d => d.Animal == "A");
        Assert.Null(animal.Occupancy); Assert.Null(animal.Proximity); Assert.NotNull(animal.Speed); Assert.NotNull(animal.Wall); Assert.NotEmpty(animal.Holds);
        foreach (var endpoint in new[] { "adjacent speed pool diameters/s", "wall observed fraction" }) { var d = ScalarDenominator(report, endpoint); Assert.Equal(7, d.IncludedAnimals); Assert.Equal(17, d.IncludedTrials); }
        foreach (var endpoint in new[] { "site occupancy fraction", "proximity pool diameters" }) { var d = ScalarDenominator(report, endpoint); Assert.Equal(6, d.IncludedAnimals); Assert.Equal(16, d.IncludedTrials); }
        Assert.Null(report.IndividualDays.Single(d => d.Animal == "PRIVATE-animal-7").Speed); // one inspected repeat cannot repair the other held repeat
        var plans = new StudyPlanStore(fixture.Store); var saved = plans.Latest()!;
        plans.Save(saved.Plan with { Trials = saved.Plan.Trials.Add(new() { Id = "missing-expected-repeat", AnimalId = "A", ContextId = fixture.Plan.Contexts[0].Id, Order = 99, MissingReason = "Synthetic missing expected repeat" }) }, "Reviewer", "Expected repeat remains missing", saved.Revision);
        report = fixture.Compute().Document.Contexts[0]; Assert.Null(report.IndividualDays.Single(d => d.Animal == "A").Speed); Assert.Null(report.IndividualDays.Single(d => d.Animal == "A").Wall);
        foreach (var endpoint in new[] { "adjacent speed pool diameters/s", "wall observed fraction" }) { var d = ScalarDenominator(report, endpoint); Assert.Equal(6, d.IncludedAnimals); Assert.Equal(3, d.HeldAnimals); Assert.Equal(17, d.IncludedTrials); Assert.Equal(22, d.ExpectedTrials); Assert.Equal(3, d.HeldTrials); }
        Assert.Contains(report.Trials, t => t.Id == "missing-expected-repeat" && t.VideoId is null && !t.Holds.IsEmpty);
    }
}
