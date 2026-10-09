using System.Collections.Immutable;
using System.Text.Json;
using MouseTrackerTabajara.Application;
using Xunit;

namespace MouseTrackerTabajara.Stability.Tests;

public sealed class StabilityMathTests
{
    private static readonly StabilityScope Scope = new("probe-day1", "Probe", 1, "release + [0,10) s", "s", "Room", "platform_site_seconds");
    private static readonly StabilityIdentity Identity = new("source", "run", "corrections", "plan", "window", "region", "profile", "code");
    private static StabilityTrial Row(string animal, string group, string trial, double original, double changed) =>
        new(animal + trial, trial, animal, group, "prespecified", "frozen-coordinate measurement", "radius ±2 px", Scope, Identity, Identity,
            new(original, original, original), new(changed, changed, changed), true, false, [], null) { OriginalScope = Scope };
    [Fact] public void UnequalTrialCountsStillGiveEachAnimalOneVote()
    {
        var report = ResultStability.Build(new[] { Row("mouse1", "WT", "1", 10, 10), Row("mouse1", "WT", "2", 10, 10), Row("mouse2", "WT", "3", 0, 0), Row("mouse3", "KO", "4", 4, 4) });
        var contrast = Assert.Single(report.Contrasts); Assert.Equal("KO", contrast.GroupA); Assert.Equal(-1d, contrast.Changed.Value); Assert.Equal(3, contrast.IncludedAnimals); Assert.Contains("Stable", contrast.Conclusion);
    }
    [Fact] public void NamedOutlierReversesSignWithoutBeingDropped()
    {
        var report = ResultStability.Build(new[] { Row("ordinary", "WT", "1", 6, 6), Row("outlier", "WT", "2", 6, 0), Row("reference", "KO", "3", 4, 4) });
        var contrast = Assert.Single(report.Contrasts); Assert.Equal("KO", contrast.GroupA); Assert.Equal(-2d, contrast.Original.Value); Assert.Equal(1d, contrast.Changed.Value); Assert.Contains("Unstable", contrast.Conclusion);
        Assert.Equal(0d, contrast.Animals.Single(a => a.AnimalId == "outlier").Changed.Value);
    }
    [Fact] public void MissingPositionIntervalCrossingZeroWithholdsDirection()
    {
        var unknown = Row("mouse1", "WT", "1", 6, 3) with { Changed = new(3, 3, 7) };
        var result = ResultStability.Build(new[] { unknown, Row("mouse2", "KO", "2", 4, 4) }); var contrast = Assert.Single(result.Contrasts);
        Assert.Equal(-3d, contrast.Changed.Lower); Assert.Equal(1d, contrast.Changed.Upper); Assert.Contains("unresolved", contrast.Conclusion);
    }
    [Fact] public void FailedTrialHoldsWholeAnimalAndRemainsInDenominator()
    {
        var failed = Row("mouse1", "WT", "2", 9, 9) with { Holds = ["fit failed; adverse check retained"] };
        var result = ResultStability.Build(new[] { Row("mouse1", "WT", "1", 9, 9), failed, Row("mouse2", "KO", "3", 4, 4) });
        var contrast = Assert.Single(result.Contrasts); Assert.Equal(2, contrast.PlannedAnimals); Assert.Equal(1, contrast.HeldAnimals); Assert.Contains("Unsupported", contrast.Conclusion); Assert.Equal(3, result.Trials.Length);
    }
    [Theory] [InlineData("source")] [InlineData("code")] [InlineData("window")] [InlineData("unit")]
    public void IdentityChangesAreHeld(string field)
    {
        var row = Row("mouse", "WT", "1", 6, 6);
        row = field switch { "source" => row with { ScenarioIdentity = Identity with { Source = "changed" } }, "code" => row with { ScenarioIdentity = Identity with { Code = "changed" } },
            "window" => row with { Scope = Scope with { Window = "trimmed" } }, _ => row with { Scope = Scope with { Unit = "cm" } } };
        Assert.NotEmpty(Assert.Single(ResultStability.Build([row]).Trials).Holds);
    }
    [Fact] public void RawCoverageNeverGrantsBiologicalEligibility()
    {
        var row = Row("mouse", "WT", "1", 90, 99) with { Supported = false, Mode = "raw decoder interval" }; Assert.NotEmpty(ResultStability.Build([row]).Trials[0].Holds);
    }
    [Fact] public void BlindExportRemovesShortAndInformativePrivateMaterialAndReplayIsExact()
    {
        var row = Row("WT", "KO", "A", 6, 5) with { Scenario = "B_cameraGenotype", DeclaredRange = "WT source file KO.mp4", Trace = new("KO.mp4", "privatehash", "WT.json", "privatehash", 0, 5), NumericRanges = [new("geometry_components", "source pixels", -40, 40), new("WT private parameter", "KO private units", 0, 1)] };
        var report = ResultStability.Build([row]); var blind = StabilityReportWriter.Disclose(report, true); var json = JsonSerializer.Serialize(blind);
        foreach (var token in new[] { "WT", "KO", "cameraGenotype", "privatehash", ".mp4", "WT.json" }) Assert.DoesNotContain(token, json);
        Assert.Equal(new StabilityDeclaredRange("geometry_components", "source pixels", -40, 40), blind.Trials[0].NumericRanges[0]);
        var replay = ResultStability.Build(JsonSerializer.Deserialize<StabilityReport>(JsonSerializer.Serialize(report))!.Trials);
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(replay));
    }
    [Fact] public void CancellationIsObservedBeforeAnyMatrixComparison()
    {
        using var token = new CancellationTokenSource(); token.Cancel(); Assert.Throws<OperationCanceledException>(() => ResultStability.Build([Row("mouse", "WT", "1", 6, 6)], token.Token));
    }
    [Fact] public void LostInfluentialAnimalChangesDescriptiveContrastButCannotExpandMeasurementEnvelope()
    {
        var baseline = new[] { Row("ordinary", "WT", "1", 6, 6), Row("influential", "WT", "2", 10, 10), Row("reference", "KO", "3", 4, 4) };
        var failed = baseline.Select(row => row with { CaseId = row.CaseId + "failed", Scenario = "one-failure", Holds = row.AnimalId == "influential" ? ["failed scenario retained"] : [] });
        var result = ResultStability.Build(baseline.Concat(failed)); Assert.Equal(2, result.Contrasts.Length);
        Assert.Equal(-4d, result.Contrasts.Single(c => c.Scenario == "prespecified").Changed.Value); Assert.Equal(-2d, result.Contrasts.Single(c => c.Scenario == "one-failure").Changed.Value);
        var envelope = Assert.Single(result.Envelopes); Assert.Equal(-4d, envelope.Minimum); Assert.Equal(-4d, envelope.Maximum); Assert.Equal("prespecified", Assert.Single(envelope.Contributors)); Assert.Contains("one-failure", envelope.MembershipChangingScenarios);
    }
}
