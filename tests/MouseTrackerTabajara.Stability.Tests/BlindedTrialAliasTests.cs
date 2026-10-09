using System.IO;
using System.Text.Json;
using MouseTrackerTabajara.Application;
using Xunit;

namespace MouseTrackerTabajara.Stability.Tests;

public sealed class BlindedTrialAliasTests
{
    [Fact]
    public void ATrialRetainsItsOpaqueIdentityAcrossScenariosAndPublishedOutputs()
    {
        var scope = new StabilityScope("private-context", "Probe", 1, "release + [0,10) s", "s", "Room", "platform_site_seconds");
        var identity = new StabilityIdentity("source", "run", "correction", "plan", "window", "region", "profile", "code");
        StabilityTrial Row(string trial, string scenario, double value) => new(trial + "/" + scenario, trial, "private-mouse", "private-allocation", scenario,
            "frozen-coordinate measurement", "private rationale", scope, identity, identity, new(5, 5, 5), new(value, value, value), true, false, [], null) { OriginalScope = scope };
        var report = ResultStability.Build([Row("private-trial-A", "private-radius", 4), Row("private-trial-B", "private-radius", 6),
            Row("private-trial-A", "private-center", 3), Row("private-trial-B", "private-center", 7)]);
        var blind = StabilityReportWriter.Disclose(report, true);
        Assert.Equal(blind.Trials[0].TrialId, blind.Trials[2].TrialId);
        Assert.Equal(blind.Trials[1].TrialId, blind.Trials[3].TrialId);
        Assert.NotEqual(blind.Trials[0].TrialId, blind.Trials[1].TrialId);
        Assert.Equal(4, blind.Trials.Select(t => t.CaseId).Distinct().Count());
        Assert.Equal(report.Trials.Select(t => t.Changed), blind.Trials.Select(t => t.Changed));
        var destination = Path.Combine(Path.GetTempPath(), "tabajara-blind-alias-" + Guid.NewGuid().ToString("N"));
        StabilityReportWriter.Save(destination, report, true);
        var saved = JsonSerializer.Deserialize<StabilityReport>(File.ReadAllText(Path.Combine(destination, "stability-input.json")))!;
        Assert.Equal(saved.Trials[0].TrialId, saved.Trials[2].TrialId);
        Assert.Equal(saved.Trials[1].TrialId, saved.Trials[3].TrialId);
        foreach (var name in new[] { "stability-input.json", "report.html", "trials.csv" })
            Assert.DoesNotContain("private-", File.ReadAllText(Path.Combine(destination, name)));
        var csv = File.ReadAllLines(Path.Combine(destination, "trials.csv")).Skip(1).Select(line => line.Split(',')).ToArray();
        Assert.Equal(csv[0][2], csv[2][2]);
        Assert.Equal(csv[1][2], csv[3][2]);
    }
}
