using System.IO;
using System.Text.Json;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Desktop;
using Xunit;

namespace MouseTrackerTabajara.Stability.Tests;

public sealed class PublicationTests
{
    private static StabilityReport Material(string id)
    {
        var scope = new StabilityScope("raw", "Unspecified", 0, "decoder interval", "percent", "image coordinates", "coverage");
        var identity = new StabilityIdentity("source", "run", "correction", "plan", "window", "region", "none", "code");
        return ResultStability.Build([new(id, id, "synthetic", "", "prespecified", "raw decoder", "range fixed", scope, identity, identity, new(5, null, null), new(6, null, null), false, false, [], null) { OriginalScope = scope }]);
    }
    [Fact] public async Task SimultaneousPublishersAtOwnershipBoundaryCannotInterleaveOrOverwrite()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-stability-publish-" + Guid.NewGuid().ToString("N")); using var barrier = new Barrier(2);
        async Task<bool> Publish(string id) => await Task.Run(() =>
        {
            try { StabilityReportWriter.Save(folder, Material(id), false, publicationStage: stage => { if (stage == "prepared") Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); }); return true; }
            catch (IOException) { return false; }
        });
        var outcomes = await Task.WhenAll(Publish("publisher-one"), Publish("publisher-two")); Assert.Single(outcomes, x => x);
        var saved = JsonSerializer.Deserialize<StabilityReport>(File.ReadAllText(Path.Combine(folder, "stability-input.json")))!; var winner = Assert.Single(saved.Trials).CaseId;
        var loser = winner == "publisher-one" ? "publisher-two" : "publisher-one";
        foreach (var file in new[] { "stability-input.json", "report.html", "trials.csv" }) { var text = File.ReadAllText(Path.Combine(folder, file)); Assert.Contains(winner, text); Assert.DoesNotContain(loser, text); }
        Assert.Contains("completed", File.ReadAllText(Path.Combine(folder, "publication.log"))); Assert.Equal(5, Directory.GetFiles(folder).Length);
        var hashes = Directory.GetFiles(folder).ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Throws<IOException>(() => StabilityReportWriter.Save(folder, Material(loser), false));
        foreach (var file in Directory.GetFiles(folder)) Assert.Equal(hashes[Path.GetFileName(file)], Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void FailureOrCancellationAfterInputWriteRetainsTruthfulTerminalAndPartialBytes(bool cancel)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-stability-partial-" + Guid.NewGuid().ToString("N")); using var token = new CancellationTokenSource();
        var exception = Record.Exception(() => StabilityReportWriter.Save(folder, Material("retained-partial"), false, token.Token, stage => { if (stage == "input-written") { if (cancel) token.Cancel(); else throw new IOException("Injected synthetic publication failure"); } }));
        if (cancel) Assert.IsType<OperationCanceledException>(exception); else Assert.IsType<IOException>(exception);
        var receipt = File.ReadAllText(Path.Combine(folder, "publication.log")); Assert.Contains(cancel ? "cancelled" : "failed", receipt); Assert.DoesNotContain("completed", receipt);
        var input = File.ReadAllBytes(Path.Combine(folder, "stability-input.json")); Assert.False(File.Exists(Path.Combine(folder, "report.html")));
        var inspected = StabilitySavedReplay.Read(Path.Combine(folder, "stability-input.json")); Assert.Contains(Assert.Single(inspected.Report.Trials).Holds, hold => hold.Contains("partial"));
        Assert.Throws<IOException>(() => StabilityReportWriter.Save(folder, Material("replacement"), false)); Assert.Equal(input, File.ReadAllBytes(Path.Combine(folder, "stability-input.json")));
    }
}
