using System.Diagnostics;
using System.Security.Cryptography;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;
using Xunit;

namespace MouseTrackerTabajara.Tests;

public sealed class StudyProcessLeaseTests
{
    [Fact(Timeout = 60000)]
    public async Task LiveOtherProcessCannotRewriteHistoryAndKilledOwnerReleasesStudy()
    {
        using var scratch = new Scratch();
        var helper = Path.Combine(Repository(), "tests", "MouseTrackerTabajara.CrashProbe", "bin", "Release", "net10.0", "MouseTrackerTabajara.CrashProbe.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(helper); start.ArgumentList.Add(scratch.Folder); start.ArgumentList.Add("commit-blocked");
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(scratch.Folder, "ready.json")))
            {
                if (child.HasExited) throw new InvalidOperationException("Lease probe exited early: " + await stderr);
                await Task.Delay(20, timeout.Token);
            }
            var store = new StudyStore(Path.Combine(scratch.Folder, "study.db")); var workspace = new WorkspaceService(store);
            var asset = Assert.Single(store.Videos()); var raw = JsonDefaults.Serialize(store.LatestRun(asset.Id));
            var journalPath = store.DatabasePath + ".recording-operations.json";
            var priorJournal = File.ReadAllBytes(journalPath);
            var decoder = new Decoder(); var request = new NativeBatchRequest { Decoder = decoder, Hasher = new Hasher() };
            var failure = await Assert.ThrowsAsync<StudyBatchInProgressException>(() => workspace.AnalyzeAsync([asset], null, request));
            Assert.Equal(store.DatabasePath, failure.DatabasePath); Assert.Equal(0, failure.ActiveDecoderLimit);
            Assert.Equal(0, decoder.Calls); Assert.Equal(priorJournal, File.ReadAllBytes(journalPath));
            Assert.Equal(raw, JsonDefaults.Serialize(store.LatestRun(asset.Id)));
            Assert.Equal(RecordingAttempts.Running, Assert.Single(workspace.ReadPersistedRecordingEvidence()).LatestAttempt);

            // A different study remains usable while the first lease is held.
            var other = new StudyStore(Path.Combine(scratch.Folder, "other.db"));
            var second = asset with { Id = "independent-study-recording" }; other.SaveVideo(second);
            Assert.Equal(1, (await new WorkspaceService(other).AnalyzeAsync([second], null, request)).Completed);

            child.Kill(entireProcessTree: true); await child.WaitForExitAsync(timeout.Token);
            Assert.Equal(RecordingAttempts.Interrupted, Assert.Single(workspace.ReadPersistedRecordingEvidence()).LatestAttempt);
            var resumed = await workspace.AnalyzeAsync([asset], null, request);
            Assert.Equal(1, resumed.Reused); Assert.Empty(resumed.Errors);
            Assert.Equal(raw, JsonDefaults.Serialize(store.LatestRun(asset.Id)));
            Assert.Equal(1, decoder.Calls); // Only the independent study decoded; the first committed run was reused.
            Assert.Contains(Assert.Single(workspace.ReadPersistedRecordingEvidence()).History, h => h.State == RecordingAttempts.Running);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            _ = await stdout; _ = await stderr;
        }
    }
    private sealed class Hasher : IRecordingSourceHasher
    {
        public string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    }
    private sealed class Decoder : IRecordingAnalysisDecoder
    {
        public int Calls;
        public AnalysisRun Analyze(VideoAsset asset, string path, IProgress<double>? progress, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            var points = Enumerable.Range(0, asset.Metadata.FrameCount).Select(i => new TrackPoint(i, i / asset.Metadata.Fps, 30, 100, PointStatus.Tracked, 1)).ToList();
            return new() { VideoId = asset.Id, SourceSha256 = asset.SourceSha256, Metadata = asset.Metadata, Settings = asset.Settings,
                AlgorithmContract = AlgorithmIdentity.Contract, NominalTimingVerified = true, Points = points,
                Metrics = MetricCalculator.Calculate(points, asset.Metadata, asset.Settings) };
        }
    }
    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MouseTrackerTabajara.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Native test repository not found.");
    }
}
