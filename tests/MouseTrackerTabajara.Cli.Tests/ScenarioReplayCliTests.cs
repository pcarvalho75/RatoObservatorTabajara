using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MouseTrackerTabajara.Cli;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Tracking;
using OpenCvSharp;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MouseTrackerTabajara.Cli.Tests;

public sealed class ScenarioReplayCliTests
{
    private const string Scenarios = """
        { "name": "original-detector" },
        { "name": "threshold-zero", "threshold": 0 },
        { "name": "polarity-off", "invert": false },
        { "name": "blur-3", "blur": 3 },
        { "name": "area-tight", "min_area": 4000, "max_area": 5000 },
        { "name": "extent-1", "max_extension": 1 },
        { "name": "jump-1", "max_jump": 1 }
        """;

    [Fact]
    public void HelpNamesReplayAndHoldsUnrunGates()
    {
        using var scratch = new Scratch();
        var top = Cli(scratch.Folder, "--help");
        Expect(0, top);
        Assert.Contains("replay <plan.json>", top.Out, StringComparison.Ordinal);
        Assert.Contains("analyze <video>", top.Out, StringComparison.Ordinal);
        var replay = Cli(scratch.Folder, "replay", "--help");
        Expect(0, replay);
        Assert.Contains("not a biological release", replay.Out, StringComparison.Ordinal);
        Assert.Contains("decoder interval", replay.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("biological start", replay.Out, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("D4", replay.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("D5", replay.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("roadmap", replay.Out, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("threshold", replay.Out, StringComparison.Ordinal);
        Assert.Contains("biological N", replay.Out, StringComparison.Ordinal);
        Assert.Contains("memory score", replay.Out, StringComparison.Ordinal);
        Assert.Contains("causal claim", replay.Out, StringComparison.Ordinal);
        Assert.Contains("group conclusion", replay.Out, StringComparison.Ordinal);
        var doc = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "detector-scenario-replay.txt"));
        Assert.Contains("D4 and D5 acceptance", doc, StringComparison.Ordinal);
        Assert.Contains("not a biological release", doc, StringComparison.Ordinal);
        var doctor = Cli(scratch.Folder, "doctor");
        Expect(0, doctor);
        Assert.Contains("OpenCV", doctor.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void CalledCliReplaysEveryScenarioAndPreservesEachBaseline()
    {
        using var scratch = new Scratch();
        var clipA = MakeClip(scratch, "recording-a", 4, WideSettings(), "study-provenance-a");
        var clipB = MakeClip(scratch, "recording-b", 8, WindowedSettings(), animalId: null);
        var plan = WritePlan(scratch, [clipA, clipB], Scenarios);
        var planBytes = File.ReadAllBytes(plan);
        var baselines = new[] { clipA, clipB }.ToDictionary(clip => clip.Id, clip => Snapshot(clip.BaselineDirectory));
        var videos = new[] { clipA, clipB }.ToDictionary(clip => clip.Id, clip => File.ReadAllBytes(clip.VideoPath));
        var first = Path.Combine(scratch.Folder, "out-1");
        var started = Cli(Path.GetTempPath(), "replay", plan, first);
        Expect(0, started);
        AssertReplay(first, clipA, clipB, planBytes);
        var firstIds = RunIds(first);
        var second = Path.Combine(scratch.Folder, "out-2");
        var repeated = Cli(Path.GetTempPath(), "replay", plan, second);
        Expect(0, repeated);
        AssertReplay(second, clipA, clipB, planBytes);
        var secondIds = RunIds(second);
        Assert.Equal(firstIds.Keys, secondIds.Keys);
        foreach (var pair in firstIds)
        {
            Assert.NotEqual(pair.Value.RunId, secondIds[pair.Key].RunId);
            Assert.Equal(pair.Value.Points, secondIds[pair.Key].Points);
        }
        var reread = RunIds(first);
        Assert.Equal(firstIds.Keys, reread.Keys);
        foreach (var pair in firstIds)
        {
            Assert.Equal(pair.Value.RunId, reread[pair.Key].RunId);
            Assert.Equal(pair.Value.Points, reread[pair.Key].Points);
        }
        foreach (var clip in new[] { clipA, clipB })
        {
            Assert.Equal(videos[clip.Id], File.ReadAllBytes(clip.VideoPath));
            AssertSame(baselines[clip.Id], clip.BaselineDirectory);
        }
    }

    [Fact]
    public void CalledCliRejectsMalformedPlansAndIncompatibleIdentity()
    {
        using var scratch = new Scratch();
        Reject(scratch, Plan(Fake("rec-1"), "{ \"name\": \"bad\", \"cx\": 0.2 }"), "geometry");
        Reject(scratch, Plan(Fake("rec-1"), "{ \"name\": \"bad\", \"start_seconds\": 1 }"), "window");
        Reject(scratch, Plan(Fake("rec-1"), "{ \"name\": \"bad\", \"diameter_cm\": 100 }"), "units");
        Reject(scratch, Plan(Fake("rec-1"), "{ \"name\": \"bad\", \"threshold\": 500 }"), "threshold");
        Reject(scratch, Plan(Fake("rec-1"), "{ \"name\": \"../escape\" }"), "traversal");
        Reject(scratch, Plan(Fake("rec-1"), "{ \"name\": \"CON\" }"), "reserved");
        Reject(scratch, Plan(Fake("rec-1") + "," + Fake("rec-1"), "{ \"name\": \"original-detector\" }"), "duplicate-id");
        Reject(scratch, Plan(Fake("rec-1"), "{ \"name\": \"one\" }, { \"name\": \"one\" }"), "duplicate-scenario");
        Reject(scratch, Plan(Fake("rec-1"), "{ \"name\": \"original-detector\" }", algorithm: "other-contract"), "algorithm");
        Reject(scratch, "{ \"schema\": \"tabajara-detector-scenario-replay-plan-v1\", \"schema\": \"again\" }", "duplicate-property");

        var clip = MakeClip(scratch, "recording-a", 4, WideSettings(), animalId: null);
        var video = File.ReadAllBytes(clip.VideoPath);
        Reject(scratch, WritePlan(scratch, [clip, clip], "{ \"name\": \"original-detector\" }", "duplicate-path.json"), "duplicate-path");
        Reject(scratch, WritePlan(scratch, [clip], "{ \"name\": \"too-small\", \"min_area\": 9000 }", "min-area.json"), "min-area");
        var wrongHashPath = WritePlan(scratch, [clip], "{ \"name\": \"original-detector\" }", "wrong-hash.json");
        var wrongHash = File.ReadAllText(wrongHashPath).Replace(clip.SourceSha, new string('c', 64), StringComparison.Ordinal);
        Assert.DoesNotContain(clip.SourceSha, wrongHash, StringComparison.Ordinal);
        File.WriteAllText(wrongHashPath, wrongHash);
        Reject(scratch, wrongHashPath, "wrong-hash");
        var wrongMetaPath = WritePlan(scratch, [clip], "{ \"name\": \"original-detector\" }", "wrong-meta.json");
        var wrongMeta = File.ReadAllText(wrongMetaPath);
        var frameToken = "\"frame_count\":" + clip.Metadata.FrameCount;
        var spacedFrameToken = "\"frame_count\": " + clip.Metadata.FrameCount;
        var replaced = wrongMeta.Contains(spacedFrameToken, StringComparison.Ordinal)
            ? wrongMeta.Replace(spacedFrameToken, "\"frame_count\": " + (clip.Metadata.FrameCount + 1), StringComparison.Ordinal)
            : wrongMeta.Replace(frameToken, "\"frame_count\":" + (clip.Metadata.FrameCount + 1), StringComparison.Ordinal);
        Assert.NotEqual(wrongMeta, replaced);
        File.WriteAllText(wrongMetaPath, replaced);
        Reject(scratch, wrongMetaPath, "wrong-meta");

        var shortcutRun = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(clip.BaselinePath)) with { Points = [clip.Baseline.Points[0]], NominalTimingVerified = true };
        var shortcutBytes = System.Text.Encoding.UTF8.GetBytes(JsonDefaults.Serialize(shortcutRun));
        File.WriteAllBytes(clip.BaselinePath, shortcutBytes);
        var shortcutPlan = WritePlan(scratch, [clip with { BaselineSha = OpenCvTracker.HashFile(clip.BaselinePath) }], "{ \"name\": \"original-detector\" }", "shortcut.json");
        Reject(scratch, shortcutPlan, "shortcut-baseline");
        Assert.Equal(shortcutBytes, File.ReadAllBytes(clip.BaselinePath));
        Assert.Equal(video, File.ReadAllBytes(clip.VideoPath));

        File.WriteAllBytes(clip.BaselinePath, clip.BaselineBytes);
        var restored = WritePlan(scratch, [clip], "{ \"name\": \"original-detector\" }", "restored.json");
        var asFile = Path.Combine(scratch.Folder, "not-a-directory");
        File.WriteAllText(asFile, "keep");
        var blocked = Cli(scratch.Folder, "replay", restored, asFile);
        Expect(2, blocked);
        Assert.Equal("keep", File.ReadAllText(asFile));
        var insideVideos = Cli(scratch.Folder, "replay", restored, Path.Combine(scratch.Folder, "videos"));
        Expect(2, insideVideos);
        Assert.Equal(video, File.ReadAllBytes(clip.VideoPath));
    }

    [Fact]
    public void CalledCliCancellationRetainsCompletedOutputAndResumeContinuesUnrunCases()
    {
        using var scratch = new Scratch();
        var first = MakeClip(scratch, "recording-a", 4, WideSettings(), animalId: null);
        var second = MakeClip(scratch, "recording-b", 4, WideSettings(), animalId: null);
        var plan = WritePlan(scratch, [first, second], "{ \"name\": \"original-detector\" }");
        var planBytes = File.ReadAllBytes(plan);
        var baselines = Snapshot(first.BaselineDirectory).Concat(Snapshot(second.BaselineDirectory)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var output = Path.Combine(scratch.Folder, "out");
        var cancel = Path.Combine(scratch.Folder, "cancel.txt");
        File.WriteAllText(cancel, "after-completed=0");
        var none = Cli(scratch.Folder, "replay", plan, output, "--cancel-when", cancel);
        Expect(3, none);
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "replay-manifest.json")));
        Assert.Equal(2, manifest.RootElement.GetProperty("cases").GetArrayLength());
        Assert.All(manifest.RootElement.GetProperty("cases").EnumerateArray(), item => Assert.Equal("unrun", item.GetProperty("status").GetString()));
        Assert.Equal(planBytes, File.ReadAllBytes(Path.Combine(output, "plan.frozen.json")));
        Assert.False(Directory.Exists(Path.Combine(output, "cases")));

        File.WriteAllText(cancel, "after-completed=1");
        var partial = Cli(scratch.Folder, "replay", plan, output, "--cancel-when", cancel);
        Expect(3, partial);
        var partialManifest = Read(output);
        Assert.Equal("completed", Case(partialManifest, "recording-a", "original-detector").GetProperty("status").GetString());
        Assert.Equal("unrun", Case(partialManifest, "recording-b", "original-detector").GetProperty("status").GetString());
        var kept = File.ReadAllBytes(Attempt(output, "recording-a", "original-detector", 1, "analysis.json"));
        var keptAgain = Cli(scratch.Folder, "replay", plan, output, "--cancel-when", cancel);
        Expect(3, keptAgain);
        Assert.Equal(kept, File.ReadAllBytes(Attempt(output, "recording-a", "original-detector", 1, "analysis.json")));
        Assert.Equal("unrun", Case(Read(output), "recording-b", "original-detector").GetProperty("status").GetString());

        File.Delete(cancel);
        var resumed = Cli(scratch.Folder, "replay", plan, output);
        Expect(0, resumed);
        Assert.Equal(kept, File.ReadAllBytes(Attempt(output, "recording-a", "original-detector", 1, "analysis.json")));
        Assert.Equal("completed", Case(Read(output), "recording-b", "original-detector").GetProperty("status").GetString());
        Assert.True(Case(Read(output), "recording-a", "original-detector").GetProperty("reused").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Read(output).RootElement.GetProperty("biological_n").ValueKind);
        foreach (var pair in baselines) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    [Fact]
    public void CalledCliRefusesChangedIdentityAndAdoptsAnUnlistedPublication()
    {
        using var scratch = new Scratch();
        var clip = MakeClip(scratch, "recording-a", 4, WideSettings(), animalId: null);
        var plan = WritePlan(scratch, [clip], "{ \"name\": \"original-detector\" }");
        var planBytes = File.ReadAllBytes(plan);
        var baselineBytes = File.ReadAllBytes(clip.BaselinePath);
        var videoBytes = File.ReadAllBytes(clip.VideoPath);
        var output = Path.Combine(scratch.Folder, "out");
        Expect(0, Cli(scratch.Folder, "replay", plan, output));
        var analysis = Attempt(output, "recording-a", "original-detector", 1, "analysis.json");
        var analysisBytes = File.ReadAllBytes(analysis);
        var runId = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(analysis)).Id;

        File.WriteAllText(plan, File.ReadAllText(plan).Replace("original-detector", "renamed-detector", StringComparison.Ordinal));
        var changedPlan = Cli(scratch.Folder, "replay", plan, output);
        Expect(2, changedPlan);
        Assert.Equal(analysisBytes, File.ReadAllBytes(analysis));
        File.WriteAllBytes(plan, planBytes);

        var flippedVideo = videoBytes.ToArray();
        flippedVideo[^1] ^= 1;
        File.WriteAllBytes(clip.VideoPath, flippedVideo);
        Expect(2, Cli(scratch.Folder, "replay", plan, output));
        Assert.Equal(analysisBytes, File.ReadAllBytes(analysis));
        Assert.Equal(flippedVideo, File.ReadAllBytes(clip.VideoPath));
        File.WriteAllBytes(clip.VideoPath, videoBytes);

        var flippedBaseline = baselineBytes.ToArray();
        flippedBaseline[^1] ^= 1;
        File.WriteAllBytes(clip.BaselinePath, flippedBaseline);
        Expect(2, Cli(scratch.Folder, "replay", plan, output));
        Assert.Equal(analysisBytes, File.ReadAllBytes(analysis));
        Assert.Equal(flippedBaseline, File.ReadAllBytes(clip.BaselinePath));
        File.WriteAllBytes(clip.BaselinePath, baselineBytes);

        var coordinates = Attempt(output, "recording-a", "original-detector", 1, "coordinates.csv");
        var coordinateBytes = File.ReadAllBytes(coordinates);
        var tampered = coordinateBytes.ToArray();
        tampered[^1] ^= 1;
        File.WriteAllBytes(coordinates, tampered);
        Expect(2, Cli(scratch.Folder, "replay", plan, output));
        Assert.Equal(tampered, File.ReadAllBytes(coordinates));
        Assert.Equal(analysisBytes, File.ReadAllBytes(analysis));
        File.WriteAllBytes(coordinates, coordinateBytes);

        var manifestPath = Path.Combine(output, "replay-manifest.json");
        var node = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var item = node["cases"]![0]!;
        item["status"] = "unrun";
        item["reason"] = "not_started";
        item["published"] = false;
        item["reused"] = false;
        item["attempts"] = new JsonArray();
        item["endpoint_differences"] = null;
        item["scenario_metrics"] = null;
        File.WriteAllText(manifestPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var adopted = Cli(scratch.Folder, "replay", plan, output);
        Expect(0, adopted);
        Assert.Equal(runId, JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(analysis)).Id);
        Assert.Equal(analysisBytes, File.ReadAllBytes(analysis));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Attempt(output, "recording-a", "original-detector", 2, "replay-case.json"))));
        Assert.Equal("completed", Case(Read(output), "recording-a", "original-detector").GetProperty("status").GetString());
        Assert.DoesNotContain("completed ", adopted.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedShortcutAndCancellationStayVisibleWhileTheNativeSiblingRemains()
    {
        using var scratch = new Scratch();
        var good = MakeClip(scratch, "good", 4, WideSettings(), animalId: null);
        var shortcut = MakeClip(scratch, "shortcut", 4, WideSettings(), animalId: null);
        var geometry = MakeClip(scratch, "geometry", 4, WideSettings(), animalId: null);
        var cancel = MakeClip(scratch, "cancel-me", 4, WideSettings(), animalId: null);
        var after = MakeClip(scratch, "after", 4, WideSettings(), animalId: null);
        var plan = WritePlan(scratch, [good, shortcut, geometry, cancel, after], "{ \"name\": \"original-detector\" }");
        var output = Path.Combine(scratch.Folder, "out");
        var tracker = new ScriptedTracker();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var first = ReplayCommand.Execute(["replay", plan, output], new ReplayCommandContext { Tracker = tracker, Out = stdout, Error = stderr });
        Assert.Equal(3, first);
        Assert.Equal("completed", Case(Read(output), "good", "original-detector").GetProperty("status").GetString());
        Assert.True(Case(Read(output), "good", "original-detector").GetProperty("published").GetBoolean());
        Assert.Equal("failed", Case(Read(output), "shortcut", "original-detector").GetProperty("status").GetString());
        Assert.Equal("failed", Case(Read(output), "geometry", "original-detector").GetProperty("status").GetString());
        Assert.False(Case(Read(output), "shortcut", "original-detector").GetProperty("published").GetBoolean());
        Assert.Equal("cancelled", Case(Read(output), "cancel-me", "original-detector").GetProperty("status").GetString());
        Assert.Equal("unrun", Case(Read(output), "after", "original-detector").GetProperty("status").GetString());
        Assert.False(File.Exists(Attempt(output, "shortcut", "original-detector", 1, "coordinates.csv")));
        Assert.True(File.Exists(Attempt(output, "shortcut", "original-detector", 1, "rejected-analysis.json")));
        var goodBytes = File.ReadAllBytes(Attempt(output, "good", "original-detector", 1, "analysis.json"));
        var shortcutCase = File.ReadAllBytes(Attempt(output, "shortcut", "original-detector", 1, "replay-case.json"));
        var rejected = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(Attempt(output, "shortcut", "original-detector", 1, "rejected-analysis.json")));
        Assert.Single(rejected.Points);
        tracker.CancelOn = false;
        stdout = new StringWriter();
        var second = ReplayCommand.Execute(["replay", plan, output], new ReplayCommandContext { Tracker = tracker, Out = stdout, Error = new StringWriter() });
        Assert.Equal(4, second);
        Assert.Equal(goodBytes, File.ReadAllBytes(Attempt(output, "good", "original-detector", 1, "analysis.json")));
        Assert.Equal(shortcutCase, File.ReadAllBytes(Attempt(output, "shortcut", "original-detector", 1, "replay-case.json")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Attempt(output, "shortcut", "original-detector", 2, "replay-case.json"))));
        Assert.Equal("completed", Case(Read(output), "cancel-me", "original-detector").GetProperty("status").GetString());
        Assert.Equal("completed", Case(Read(output), "after", "original-detector").GetProperty("status").GetString());
        Assert.True(File.Exists(Attempt(output, "cancel-me", "original-detector", 1, "replay-case.json")));
        Assert.Equal(1, tracker.Calls["good"]);
        Assert.Equal(1, tracker.Calls["shortcut"]);
        Assert.Equal(1, tracker.Calls["geometry"]);
        Assert.Equal(2, tracker.Calls["cancel-me"]);
        Assert.Equal(1, tracker.Calls["after"]);
        Assert.Equal(JsonValueKind.Null, Read(output).RootElement.GetProperty("biological_n").ValueKind);
    }

    [Fact]
    public void CalledCliRejectsMalformedBaselineResumeAndChangedCodeIdentity()
    {
        using var scratch = new Scratch();
        var clip = MakeClip(scratch, "recording-a", 4, WideSettings(), animalId: null);
        var baseline = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(clip.BaselinePath));
        var overflow = baseline with { Settings = baseline.Settings with { StartSeconds = 1e20 } };
        var overflowBytes = System.Text.Encoding.UTF8.GetBytes(JsonDefaults.Serialize(overflow));
        File.WriteAllBytes(clip.BaselinePath, overflowBytes);
        var overflowPlan = WritePlan(scratch, [clip with { BaselineSha = OpenCvTracker.HashFile(clip.BaselinePath) }], "{ \"name\": \"original-detector\" }", "overflow.json");
        var overflowRun = Cli(scratch.Folder, "replay", overflowPlan, Path.Combine(scratch.Folder, "overflow-out"));
        Expect(2, overflowRun);
        Assert.Contains("Selected interval is outside this recording.", overflowRun.Error, StringComparison.Ordinal);
        Assert.Equal(overflowBytes, File.ReadAllBytes(clip.BaselinePath));
        Assert.False(Directory.Exists(Path.Combine(scratch.Folder, "overflow-out")));

        File.WriteAllBytes(clip.BaselinePath, clip.BaselineBytes);
        var counts = baseline.Metrics.StatusCounts.ToDictionary(pair => pair.Key, pair => pair.Value);
        counts["tracked"] = 0;
        counts["missing"] = baseline.Points.Count;
        var wrongCounts = baseline with { Metrics = baseline.Metrics with { StatusCounts = counts } };
        var wrongBytes = System.Text.Encoding.UTF8.GetBytes(JsonDefaults.Serialize(wrongCounts));
        File.WriteAllBytes(clip.BaselinePath, wrongBytes);
        var wrongPlan = WritePlan(scratch, [clip with { BaselineSha = OpenCvTracker.HashFile(clip.BaselinePath) }], "{ \"name\": \"original-detector\" }", "wrong-counts.json");
        var wrongRun = Cli(scratch.Folder, "replay", wrongPlan, Path.Combine(scratch.Folder, "wrong-counts-out"));
        Expect(2, wrongRun);
        Assert.Contains("status counts", wrongRun.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(wrongBytes, File.ReadAllBytes(clip.BaselinePath));

        File.WriteAllBytes(clip.BaselinePath, clip.BaselineBytes);
        var plan = WritePlan(scratch, [clip], "{ \"name\": \"original-detector\" }", "good.json");
        var output = Path.Combine(scratch.Folder, "out");
        Expect(0, Cli(scratch.Folder, "replay", plan, output));
        var analysisPath = Attempt(output, "recording-a", "original-detector", 1, "analysis.json");
        var published = File.ReadAllBytes(analysisPath);
        var coordinates = File.ReadAllBytes(Attempt(output, "recording-a", "original-detector", 1, "coordinates.csv"));
        var frozen = File.ReadAllBytes(Path.Combine(output, "plan.frozen.json"));
        var video = File.ReadAllBytes(clip.VideoPath);
        var manifestPath = Path.Combine(output, "replay-manifest.json");
        var identityBefore = JsonNode.Parse(File.ReadAllText(manifestPath))!["code_identity"]!.ToJsonString();

        var resumed = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(analysisPath));
        var resumedCounts = resumed.Metrics.StatusCounts.ToDictionary(pair => pair.Key, pair => pair.Value);
        resumedCounts["tracked"] = 0;
        resumedCounts["missing"] = resumed.Points.Count;
        var tamperedRun = resumed with { Metrics = resumed.Metrics with { StatusCounts = resumedCounts } };
        var tamperedAnalysis = System.Text.Encoding.UTF8.GetBytes(JsonDefaults.Serialize(tamperedRun));
        File.WriteAllBytes(analysisPath, tamperedAnalysis);
        RewriteListedHash(manifestPath, "analysis.json", OpenCvTracker.HashFile(analysisPath));
        var manifestAfterTamper = File.ReadAllBytes(manifestPath);
        var resumedCli = Cli(scratch.Folder, "replay", plan, output);
        Expect(2, resumedCli);
        Assert.Contains("status counts", resumedCli.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tamperedAnalysis, File.ReadAllBytes(analysisPath));
        Assert.Equal(coordinates, File.ReadAllBytes(Attempt(output, "recording-a", "original-detector", 1, "coordinates.csv")));
        Assert.Equal(manifestAfterTamper, File.ReadAllBytes(manifestPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Attempt(output, "recording-a", "original-detector", 2, "replay-case.json"))));

        File.WriteAllBytes(analysisPath, published);
        RewriteListedHash(manifestPath, "analysis.json", OpenCvTracker.HashFile(analysisPath));
        var restoredManifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        restoredManifest["code_identity"]!["assemblies"]![0]!["sha256"] = new string('a', 64);
        var recordedTamper = System.Text.Encoding.UTF8.GetBytes(restoredManifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllBytes(manifestPath, recordedTamper);
        Directory.CreateDirectory(Path.Combine(output, ".staging"));
        var sentinel = Path.Combine(output, ".staging", "interrupted.txt");
        File.WriteAllText(sentinel, "keep");
        var identityRejected = Cli(scratch.Folder, "replay", plan, output);
        Expect(2, identityRejected);
        Assert.Contains("Assembly identity", identityRejected.Error, StringComparison.Ordinal);
        Assert.Equal(published, File.ReadAllBytes(analysisPath));
        Assert.Equal(coordinates, File.ReadAllBytes(Attempt(output, "recording-a", "original-detector", 1, "coordinates.csv")));
        Assert.Equal(frozen, File.ReadAllBytes(Path.Combine(output, "plan.frozen.json")));
        Assert.Equal(recordedTamper, File.ReadAllBytes(manifestPath));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.Equal(video, File.ReadAllBytes(clip.VideoPath));
        Assert.Equal(clip.BaselineBytes, File.ReadAllBytes(clip.BaselinePath));
        _ = identityBefore;
    }

    [Fact]
    public void SameIdentityAdoptsUnlistedAttemptAndRejectsDifferentActualIdentity()
    {
        using var scratch = new Scratch();
        var clip = MakeClip(scratch, "recording-a", 4, WideSettings(), animalId: null);
        var plan = WritePlan(scratch, [clip], "{ \"name\": \"original-detector\" }");
        var output = Path.Combine(scratch.Folder, "out");
        Expect(0, Cli(scratch.Folder, "replay", plan, output));
        AssertLiveIdentity(output);
        var manifestPath = Path.Combine(output, "replay-manifest.json");
        var before = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var identity = before["code_identity"]!.ToJsonString();
        var analysis = File.ReadAllBytes(Attempt(output, "recording-a", "original-detector", 1, "analysis.json"));
        var runId = JsonDefaults.Deserialize<AnalysisRun>(System.Text.Encoding.UTF8.GetString(analysis)).Id;
        var item = before["cases"]![0]!;
        item["status"] = "unrun";
        item["reason"] = "not_started";
        item["published"] = false;
        item["reused"] = false;
        item["attempts"] = new JsonArray();
        item["endpoint_differences"] = null;
        item["scenario_metrics"] = null;
        File.WriteAllText(manifestPath, before.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Expect(0, Cli(scratch.Folder, "replay", plan, output));
        var after = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        Assert.Equal(identity, after["code_identity"]!.ToJsonString());
        Assert.Equal("completed", after["cases"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(runId, JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(Attempt(output, "recording-a", "original-detector", 1, "analysis.json"))).Id);
        Assert.Equal(analysis, File.ReadAllBytes(Attempt(output, "recording-a", "original-detector", 1, "analysis.json")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Attempt(output, "recording-a", "original-detector", 2, "replay-case.json"))));

        var actual = ReplayCodeIdentityProbe.Capture(OpenCvTracker.NativeVersion);
        actual.Assemblies[0].Sha256 = new string('b', 64);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var rejected = ReplayCommand.Execute(["replay", plan, output], new ReplayCommandContext { Out = stdout, Error = stderr, CaptureIdentity = () => actual });
        Assert.Equal(2, rejected);
        Assert.Contains("Assembly identity", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(analysis, File.ReadAllBytes(Attempt(output, "recording-a", "original-detector", 1, "analysis.json")));
        Assert.Equal("0.1.0-native", actual.SoftwareVersion);
        Assert.NotEqual(actual.SoftwareVersion, actual.Assemblies[0].Sha256);
    }

    [Fact]
    public void ProducedMalformedEvidenceIsRetainedAndNotPublished()
    {
        using var scratch = new Scratch();
        var clip = MakeClip(scratch, "recording-a", 4, WideSettings(), animalId: null);
        var plan = WritePlan(scratch, [clip], "{ \"name\": \"original-detector\" }");
        var output = Path.Combine(scratch.Folder, "out");
        var stderr = new StringWriter();
        var code = ReplayCommand.Execute(["replay", plan, output], new ReplayCommandContext { Tracker = new OutsideCoordinateTracker(), Out = new StringWriter(), Error = stderr });
        Assert.Equal(4, code);
        Assert.Equal("failed", Case(Read(output), "recording-a", "original-detector").GetProperty("status").GetString());
        Assert.False(Case(Read(output), "recording-a", "original-detector").GetProperty("published").GetBoolean());
        Assert.True(File.Exists(Attempt(output, "recording-a", "original-detector", 1, "rejected-analysis.json")));
        Assert.False(File.Exists(Attempt(output, "recording-a", "original-detector", 1, "coordinates.csv")));
        var rejected = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(Attempt(output, "recording-a", "original-detector", 1, "rejected-analysis.json")));
        Assert.Equal(clip.Metadata.Width, rejected.Points[0].X);
        Assert.Contains("Accepted coordinates are outside the video.", Case(Read(output), "recording-a", "original-detector").GetProperty("error_message").GetString(), StringComparison.Ordinal);
        _ = stderr;
    }

    [Fact]
    public void HeldLeaseAndTwoProcessesLeaveTheLiveOutputUntouched()
    {
        using var scratch = new Scratch();
        var clip = MakeClip(scratch, "recording-a", 8, WideSettings(), animalId: null);
        var plan = WritePlan(scratch, [clip], "{ \"name\": \"original-detector\" }");
        var blocked = Path.Combine(scratch.Folder, "blocked");
        Directory.CreateDirectory(Path.Combine(blocked, ".staging"));
        var sentinel = Path.Combine(blocked, ".staging", "live.txt");
        File.WriteAllText(sentinel, "keep");
        var manifest = Path.Combine(blocked, "replay-manifest.json");
        File.WriteAllText(manifest, "do-not-rewrite");
        using (var held = new FileStream(Path.Combine(blocked, ReplayOutputGuard.LeaseFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var rejected = Cli(scratch.Folder, "replay", plan, blocked);
            Expect(2, rejected);
            Assert.Contains("output lease", rejected.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.Equal("do-not-rewrite", File.ReadAllText(manifest));
            _ = held.Length;
        }

        var output = Path.Combine(scratch.Folder, "overlap");
        var first = StartCli(scratch.Folder, plan, output);
        var suspended = false;
        try
        {
            var leasePath = Path.Combine(output, ReplayOutputGuard.LeaseFileName);
            var overlapped = false;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !first.HasExited)
            {
                if (File.Exists(leasePath))
                {
                    try
                    {
                        using var probe = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (IOException exception) when ((exception.HResult & 0xFFFF) == 32)
                    {
                        var status = NtSuspendProcess(first.Handle);
                        if (status != 0)
                            throw new InvalidOperationException($"NtSuspendProcess returned 0x{status:X8}. The live overlap was not forced.");
                        suspended = true;
                        try
                        {
                            using var confirm = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        }
                        catch (IOException confirmed) when ((confirmed.HResult & 0xFFFF) == 32)
                        {
                            overlapped = true;
                            break;
                        }
                        NtResumeProcess(first.Handle);
                        suspended = false;
                    }
                }
                Thread.Sleep(5);
            }
            Assert.True(overlapped && suspended, "The first replay did not hold the output lease long enough for a second process to overlap.");
            var manifestPath = Path.Combine(output, "replay-manifest.json");
            var manifestBefore = File.Exists(manifestPath) ? File.ReadAllBytes(manifestPath) : null;
            var stagingBefore = Directory.Exists(Path.Combine(output, ".staging"));
            var second = Cli(scratch.Folder, "replay", plan, output);
            Expect(2, second);
            Assert.Contains("output lease", second.Error, StringComparison.OrdinalIgnoreCase);
            if (manifestBefore is null) Assert.False(File.Exists(manifestPath));
            else Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
            Assert.Equal(stagingBefore, Directory.Exists(Path.Combine(output, ".staging")));
            var resumed = NtResumeProcess(first.Handle);
            if (resumed != 0)
                throw new InvalidOperationException($"NtResumeProcess returned 0x{resumed:X8}.");
            suspended = false;
            var firstResult = FinishCli(first);
            Expect(0, firstResult);
            Assert.Equal("completed", Case(Read(output), "recording-a", "original-detector").GetProperty("status").GetString());
            Assert.True(File.Exists(Attempt(output, "recording-a", "original-detector", 1, "analysis.json")));
            Assert.False(Directory.Exists(Path.GetDirectoryName(Attempt(output, "recording-a", "original-detector", 2, "replay-case.json"))));
        }
        finally
        {
            if (suspended && !first.HasExited) NtResumeProcess(first.Handle);
            if (!first.HasExited)
            {
                first.Kill(entireProcessTree: true);
                first.WaitForExit(10000);
            }
        }
    }

    [Fact]
    public void UnsafeReparseIsRejectedAndContainedTargetsRemain()
    {
        using var scratch = new Scratch();
        var output = Path.Combine(scratch.Folder, "out");
        var staging = Path.Combine(output, ".staging");
        var inside = Path.Combine(staging, "inside");
        var outside = Path.Combine(scratch.Folder, "outside");
        Directory.CreateDirectory(inside);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "keep");
        File.WriteAllText(Path.Combine(inside, "kept.txt"), "kept");
        Assert.Throws<PlanRejectedException>(() => ReplayOutputGuard.RequireCleanupTarget(Path.GetFullPath(Path.Combine(staging, "..", "..", "outside")), staging, "cleanup"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));

        var safe = Path.Combine(staging, "safe");
        var escape = Path.Combine(staging, "escape");
        var safeCreated = TryJunction(safe, inside);
        var escapeCreated = safeCreated && TryJunction(escape, outside);
        if (!safeCreated || !escapeCreated)
        {
            Console.WriteLine("REPARSE_CASE=not_permitted");
            return;
        }
        Console.WriteLine("REPARSE_CASE=ran");
        ReplayOutputGuard.RequireCleanupTarget(safe, staging, "contained reparse");
        Assert.Throws<PlanRejectedException>(() => ReplayOutputGuard.RequireCleanupTarget(escape, staging, "escaping reparse"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(inside, "kept.txt")));

        var clip = MakeClip(scratch, "recording-a", 4, WideSettings(), animalId: null);
        var plan = WritePlan(scratch, [clip], "{ \"name\": \"original-detector\" }", "reparse.json");
        var replay = Path.Combine(scratch.Folder, "replay");
        Expect(0, Cli(scratch.Folder, "replay", plan, replay));
        var analysis = File.ReadAllBytes(Attempt(replay, "recording-a", "original-detector", 1, "analysis.json"));
        var stagingLink = Path.Combine(replay, ".staging");
        if (Directory.Exists(stagingLink)) Directory.Delete(stagingLink, true);
        Assert.True(TryJunction(stagingLink, outside), "The replay staging junction could not be created.");
        var rejected = Cli(scratch.Folder, "replay", plan, replay);
        Expect(2, rejected);
        Assert.Contains("resolves outside", rejected.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        Assert.Equal(analysis, File.ReadAllBytes(Attempt(replay, "recording-a", "original-detector", 1, "analysis.json")));
    }

    private static void AssertReplay(string output, Clip clipA, Clip clipB, byte[] planBytes)
    {
        Assert.Equal(planBytes, File.ReadAllBytes(Path.Combine(output, "plan.frozen.json")));
        var manifest = Read(output);
        var root = manifest.RootElement;
        Assert.Equal(ReplayClaims.ManifestSchema, root.GetProperty("schema").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("biological_n").ValueKind);
        Assert.Equal(2, root.GetProperty("recording_count").GetInt32());
        Assert.Equal(7, root.GetProperty("scenario_count").GetInt32());
        Assert.Equal(14, root.GetProperty("planned_case_count").GetInt32());
        Assert.Contains(ReplayClaims.Held, root.GetProperty("claim_limits").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(AlgorithmIdentity.Contract, root.GetProperty("code_identity").GetProperty("algorithm_contract").GetString());
        Assert.Equal(OpenCvTracker.NativeVersion, root.GetProperty("code_identity").GetProperty("native_version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(OpenCvTracker.NativeVersion));
        Assert.Contains("withheld", root.GetProperty("endpoint_difference_scope").GetString(), StringComparison.Ordinal);
        AssertLiveIdentity(output);
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var differenceNames = item.GetProperty("endpoint_differences").EnumerateArray().Select(difference => difference.GetProperty("name").GetString()).ToArray();
            Assert.Contains("coverage_percent", differenceNames);
            Assert.DoesNotContain("distance", differenceNames);
            Assert.DoesNotContain("mean_speed", differenceNames);
            Assert.DoesNotContain("first_observed_site_visit_seconds", differenceNames);
            Assert.DoesNotContain("quadrant_seconds_nw", differenceNames);
            var window = item.GetProperty("preserved");
            Assert.Equal("decoder_interval", window.GetProperty("time_window").GetString());
            Assert.Contains("not a biological release", window.GetProperty("time_window_statement").GetString(), StringComparison.Ordinal);
        }
        foreach (var claim in root.GetProperty("claim_limits").EnumerateArray().Select(item => item.GetString() ?? ""))
        {
            Assert.DoesNotContain("D4", claim, StringComparison.Ordinal);
            Assert.DoesNotContain("D5", claim, StringComparison.Ordinal);
            Assert.DoesNotContain("roadmap", claim, StringComparison.OrdinalIgnoreCase);
        }
        AssertNoForbiddenProperties(root);
        var recordings = root.GetProperty("recordings").EnumerateArray().ToArray();
        Assert.Equal("study-provenance-a", recordings[0].GetProperty("animal_id").GetString());
        Assert.Equal("plan", recordings[0].GetProperty("animal_id_source").GetString());
        Assert.False(recordings[0].GetProperty("animal_id_confirmed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, recordings[1].GetProperty("animal_id").ValueKind);
        Assert.Equal("not_provided", recordings[1].GetProperty("animal_id_source").GetString());
        var names = new[] { "original-detector", "threshold-zero", "polarity-off", "blur-3", "area-tight", "extent-1", "jump-1" };
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(14, cases.Length);
        var index = 0;
        foreach (var id in new[] { "recording-a", "recording-b" })
        {
            foreach (var name in names)
            {
                Assert.Equal(index, cases[index].GetProperty("plan_index").GetInt32());
                Assert.Equal(id, cases[index].GetProperty("recording_id").GetString());
                Assert.Equal(name, cases[index].GetProperty("scenario_name").GetString());
                Assert.Equal("completed", cases[index].GetProperty("status").GetString());
                Assert.True(cases[index].GetProperty("published").GetBoolean());
                Assert.True(cases[index].GetProperty("endpoint_differences").GetArrayLength() > 0);
                index++;
            }
        }
        AssertClip(output, clipA, fullInterval: true);
        AssertClip(output, clipB, fullInterval: false);
    }

    private static void AssertClip(string output, Clip clip, bool fullInterval)
    {
        var original = LoadRun(output, clip.Id, "original-detector");
        var baselinePoints = Points(clip.Baseline);
        Assert.Equal(baselinePoints, Points(original));
        Assert.Equal(clip.Settings.Cx, original.Settings.Cx);
        Assert.Equal(clip.Settings.Cy, original.Settings.Cy);
        Assert.Equal(clip.Settings.Rx, original.Settings.Rx);
        Assert.Equal(clip.Settings.Ry, original.Settings.Ry);
        Assert.Equal(clip.Settings.StartSeconds, original.Settings.StartSeconds);
        Assert.Equal(clip.Settings.EndSeconds, original.Settings.EndSeconds);
        Assert.Equal(clip.Settings.DiameterCm, original.Settings.DiameterCm);
        Assert.Equal(clip.Settings.WallBoundary, original.Settings.WallBoundary);
        Assert.Equal(clip.Settings.ArenaConfirmed, original.Settings.ArenaConfirmed);
        Assert.Equal(clip.Settings.Platform?.X, original.Settings.Platform?.X);
        Assert.Equal(clip.Settings.Platform?.Y, original.Settings.Platform?.Y);
        Assert.Equal(clip.Settings.Platform?.Radius, original.Settings.Platform?.Radius);
        Assert.Equal(clip.Settings.Exclusions.Count, original.Settings.Exclusions.Count);
        Assert.Equal(clip.Id, original.VideoId);
        var fps = clip.Metadata.Fps;
        var first = fullInterval ? 0 : (int)Math.Ceiling(.1 * fps);
        var last = fullInterval ? clip.Metadata.FrameCount : Math.Min(clip.Metadata.FrameCount, (int)Math.Ceiling(.5 * fps));
        Assert.Equal(Enumerable.Range(first, last - first), original.Points.Select(point => point.Frame));
        Assert.Equal(clip.Settings.DiameterCm.HasValue ? "cm" : "pool diameters", original.Metrics.DistanceUnit);
        CompletedRunContract.Ensure(original, clip.Metadata, clip.Settings, clip.SourceSha, AlgorithmIdentity.Contract, CompletedRunContract.SoftwareVersion);
        var scope = File.ReadAllText(Attempt(output, clip.Id, "original-detector", 1, "measurement-scope.txt"));
        Assert.Contains("Raw decoder run", scope, StringComparison.Ordinal);
        Assert.Contains("only the raw decoder run", scope, StringComparison.Ordinal);

        var dark = LoadRun(output, clip.Id, "threshold-zero");
        Assert.Equal(0, dark.Settings.Threshold);
        Assert.Equal(0, dark.Metrics.TrackedFrames);
        Assert.Equal(original.Points.Count, dark.Points.Count);
        Assert.Equal(clip.Settings.Cx, dark.Settings.Cx);
        Assert.Equal(clip.Settings.StartSeconds, dark.Settings.StartSeconds);
        Assert.Equal(clip.Settings.DiameterCm, dark.Settings.DiameterCm);
        var polarity = LoadRun(output, clip.Id, "polarity-off");
        Assert.False(polarity.Settings.Invert);
        Assert.Equal(0, polarity.Metrics.TrackedFrames);
        var blur = LoadRun(output, clip.Id, "blur-3");
        Assert.Equal(3, blur.Settings.Blur);
        Assert.Equal(clip.Settings.Cx, blur.Settings.Cx);
        var area = LoadRun(output, clip.Id, "area-tight");
        Assert.Equal(4000, area.Settings.MinArea);
        Assert.Equal(5000, area.Settings.MaxArea);
        Assert.Equal(0, area.Metrics.TrackedFrames);
        var extent = LoadRun(output, clip.Id, "extent-1");
        Assert.Equal(1, extent.Settings.MaxExtension);
        Assert.Equal(0, extent.Metrics.TrackedFrames);
        var jump = LoadRun(output, clip.Id, "jump-1");
        Assert.Equal(1, jump.Settings.MaxJump);
        Assert.Equal(original.Points.Select(point => point.Frame), jump.Points.Select(point => point.Frame));
        Assert.Contains(jump.Points, point => point.Status == PointStatus.Jump);
    }

    private static AnalysisRun LoadRun(string output, string recording, string scenario) =>
        JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(Attempt(output, recording, scenario, 1, "analysis.json")));

    private static List<(int Frame, int? X, int? Y, string Status)> Points(AnalysisRun run) =>
        run.Points.Select(point => (point.Frame, point.X, point.Y, point.Status.ToString())).ToList();

    private static Dictionary<string, (string RunId, List<(int Frame, int? X, int? Y, string Status)> Points)> RunIds(string output)
    {
        var result = new Dictionary<string, (string, List<(int, int?, int?, string)>)>(StringComparer.Ordinal);
        foreach (var item in Read(output).RootElement.GetProperty("cases").EnumerateArray())
        {
            var recording = item.GetProperty("recording_id").GetString()!;
            var scenario = item.GetProperty("scenario_name").GetString()!;
            var run = LoadRun(output, recording, scenario);
            result[recording + "/" + scenario] = (run.Id, Points(run));
        }
        return result;
    }

    private static JsonDocument Read(string output) => JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "replay-manifest.json")));

    private static JsonElement Case(JsonDocument manifest, string recording, string scenario) =>
        manifest.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("recording_id").GetString() == recording && item.GetProperty("scenario_name").GetString() == scenario);

    private static string Attempt(string output, string recording, string scenario, int index, string file) =>
        Path.Combine(output, "cases", recording, scenario, $"attempt-{index:0000}", file);

    private static void AssertNoForbiddenProperties(JsonElement element)
    {
        string[] forbidden = ["p_value", "p_values", "significant", "significance", "memory_score", "group_mean", "group_means", "group_difference", "favored", "favorable", "best_scenario", "selected_scenario", "animal_n", "n_animals", "confidence_interval", "effect_size", "causal_effect"];
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, forbidden);
                AssertNoForbiddenProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) AssertNoForbiddenProperties(item);
        }
    }

    private static Clip MakeClip(Scratch scratch, string id, int frames, TrackingSettings settings, string? animalId)
    {
        var videos = Path.Combine(scratch.Folder, "videos");
        Directory.CreateDirectory(videos);
        var video = Path.Combine(videos, id + ".avi");
        using (var writer = new VideoWriter(video, FourCC.MJPG, 10, new Size(160, 120)))
        {
            Assert.True(writer.IsOpened(), "Synthetic video writer did not open.");
            for (var frame = 0; frame < frames; frame++)
            {
                using var image = new Mat(120, 160, MatType.CV_8UC3, new Scalar(170, 150, 40));
                Cv2.Circle(image, new Point(48 + frame * 3, 60), 7, new Scalar(6, 6, 6), -1);
                writer.Write(image);
            }
        }
        var hash = OpenCvTracker.HashFile(video);
        var metadata = OpenCvTracker.Probe(video);
        Assert.Equal(frames, metadata.FrameCount);
        var first = (int)Math.Ceiling(settings.StartSeconds * metadata.Fps);
        var last = Math.Min(metadata.FrameCount, settings.EndSeconds is { } end ? (int)Math.Ceiling(end * metadata.Fps) : metadata.FrameCount);
        Assert.True(first < last);
        var baselineDirectory = Path.Combine(scratch.Folder, "baselines", id);
        var baseline = OpenCvTracker.Analyze(new VideoAsset { Id = id, Path = video, SourceSha256 = hash, Metadata = metadata, Settings = settings, AnimalId = "" });
        ExportWriter.SaveRun(baseline, baselineDirectory);
        var baselinePath = Path.Combine(baselineDirectory, "analysis.json");
        return new Clip(id, video, "videos/" + id + ".avi", hash, metadata, baselinePath, "baselines/" + id + "/analysis.json", OpenCvTracker.HashFile(baselinePath), baselineDirectory, File.ReadAllBytes(baselinePath), baseline, settings, animalId);
    }

    private static TrackingSettings WideSettings() => new()
    {
        Cx = .5, Cy = .5, Rx = .4, Ry = .4, Threshold = 40, Invert = true, Blur = 1, MinArea = 20, MaxArea = 5000,
        MaxExtension = 80, MaxJump = 40, ArenaConfirmed = true, WallBoundary = .8
    };

    private static TrackingSettings WindowedSettings() => new()
    {
        Cx = .45, Cy = .52, Rx = .4, Ry = .4, Threshold = 40, Invert = true, Blur = 1, MinArea = 20, MaxArea = 5000,
        MaxExtension = 80, MaxJump = 40, ArenaConfirmed = true, WallBoundary = .75, StartSeconds = .1, EndSeconds = .5,
        DiameterCm = 120, Platform = new PlatformSite(.3, .3, .05), Exclusions = [new ExclusionRegion(.01, .01, .04, .04)]
    };

    private static string WritePlan(Scratch scratch, IReadOnlyList<Clip> clips, string scenarios, string file = "plan.json")
    {
        var provenance = (Clip clip) => clip.AnimalId is null ? "" : $",\n  \"animal_id\": {Q(clip.AnimalId)},\n  \"animal_id_confirmed\": false,\n  \"group_name\": \"cohort-label\",\n  \"phase\": \"training\",\n  \"day\": 2,\n  \"trial\": 1,\n  \"provenance_note\": \"explicit provenance\"";
        var recordings = string.Join(",\n", clips.Select(clip => $$"""
        {
          "recording_id": {{Q(clip.Id)}},
          "video_path": {{Q(clip.VideoRelative)}},
          "expected_source_sha256": {{Q(clip.SourceSha)}},
          "expected_metadata": {{JsonSerializer.Serialize(new { width = clip.Metadata.Width, height = clip.Metadata.Height, fps = clip.Metadata.Fps, frame_count = clip.Metadata.FrameCount })}},
          "baseline_analysis_path": {{Q(clip.BaselineRelative)}},
          "baseline_sha256": {{Q(clip.BaselineSha)}}{{provenance(clip)}}
        }
        """));
        var path = Path.Combine(scratch.Folder, file);
        File.WriteAllText(path, Plan(recordings, scenarios));
        return path;
    }

    private static string Plan(string recordings, string scenarios, string? algorithm = null) => $$"""
    {
      "schema": "tabajara-detector-scenario-replay-plan-v1",
      "algorithm_contract": {{Q(algorithm ?? AlgorithmIdentity.Contract)}},
      "recordings": [{{recordings}}],
      "scenarios": [{{scenarios}}]
    }
    """;

    private static string Fake(string id) => $$"""
    {
      "recording_id": "{{id}}",
      "video_path": "missing.avi",
      "expected_source_sha256": "{{new string('a', 64)}}",
      "expected_metadata": { "width": 160, "height": 120, "fps": 10, "frame_count": 4 },
      "baseline_analysis_path": "missing.json",
      "baseline_sha256": "{{new string('b', 64)}}"
    }
    """;

    private static void Reject(Scratch scratch, string planTextOrPath, string name)
    {
        var path = File.Exists(planTextOrPath) ? planTextOrPath : Path.Combine(scratch.Folder, name + ".json");
        if (!File.Exists(planTextOrPath)) File.WriteAllText(path, planTextOrPath);
        var output = Path.Combine(scratch.Folder, "rejected-" + name);
        var result = Cli(scratch.Folder, "replay", path, output);
        Expect(2, result);
        Assert.False(Directory.Exists(output), result.Error);
    }

    private static string Q(string value) => JsonSerializer.Serialize(value);

    private static Dictionary<string, byte[]> Snapshot(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertSame(Dictionary<string, byte[]> before, string directory)
    {
        var after = Snapshot(directory);
        Assert.Equal(before.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase), after.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
    }

    private static (int Code, string Out, string Error) Cli(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo(Exe())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(string.Join(' ', args));
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void Expect(int expected, (int Code, string Out, string Error) result) =>
        Assert.True(result.Code == expected, $"exit {result.Code}\nSTDOUT\n{result.Out}\nSTDERR\n{result.Error}");

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MouseTrackerTabajara.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static string Exe()
    {
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name;
        if (config is not ("Debug" or "Release")) throw new InvalidOperationException("Unexpected configuration " + config);
        var exe = Path.Combine(RepositoryRoot(), "src", "MouseTrackerTabajara.Cli", "bin", config, "net10.0", "MouseTrackerTabajara.Cli.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("CLI executable was not built.", exe);
        return exe;
    }

    private static void AssertLiveIdentity(string output)
    {
        var identity = Read(output).RootElement.GetProperty("code_identity");
        Assert.Equal(CompletedRunContract.SoftwareVersion, identity.GetProperty("software_version").GetString());
        var exeDirectory = Path.GetDirectoryName(Exe())!;
        foreach (var name in new[] { "MouseTrackerTabajara.Cli", "MouseTrackerTabajara.Core", "MouseTrackerTabajara.Tracking", "MouseTrackerTabajara.Application" })
        {
            var recorded = identity.GetProperty("assemblies").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
            var sha = recorded.GetProperty("sha256").GetString();
            Assert.Equal(64, sha!.Length);
            Assert.NotEqual(CompletedRunContract.SoftwareVersion, sha);
            Assert.Equal(ReplayHashes.Sha256File(Path.Combine(exeDirectory, name + ".dll")), sha);
            Assert.True(Guid.TryParseExact(recorded.GetProperty("module_version_id").GetString(), "D", out _));
        }
        var nativeDirectory = Path.Combine(exeDirectory, "runtimes", "win-x64", "native");
        var natives = identity.GetProperty("native_binaries").EnumerateArray().ToArray();
        var described = string.Join(", ", natives.Select(item => item.GetProperty("name").GetString()));
        Assert.Contains(natives, item => string.Equals(Path.GetFileName(item.GetProperty("name").GetString()), "OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(natives, item => (Path.GetFileName(item.GetProperty("name").GetString()) ?? "").StartsWith("opencv_videoio_ffmpeg", StringComparison.OrdinalIgnoreCase));
        foreach (var recorded in natives)
        {
            var raw = recorded.GetProperty("name").GetString() ?? "";
            var path = Path.Combine(nativeDirectory, Path.GetFileName(raw));
            Assert.True(File.Exists(path), $"Recorded native '{raw}' is not a binary beside the CLI. Recorded names: {described}.");
            Assert.Equal(ReplayHashes.Sha256File(path), recorded.GetProperty("sha256").GetString());
        }
    }

    private static void RewriteListedHash(string manifestPath, string fileName, string sha)
    {
        var node = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var files = node["cases"]![0]!["attempts"]![0]!["files"]!.AsArray();
        var match = files.Single(file => file!["name"]!.GetValue<string>() == fileName);
        match!["sha256"] = sha;
        File.WriteAllText(manifestPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Process StartCli(string workingDirectory, string plan, string output)
    {
        var start = new ProcessStartInfo(Exe())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("replay");
        start.ArgumentList.Add(plan);
        start.ArgumentList.Add(output);
        return Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
    }

    private static (int Code, string Out, string Error) FinishCli(Process process)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("replay overlap");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    private static bool TryJunction(string link, string target)
    {
        if (Directory.Exists(link)) return false;
        var start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(start);
        if (process is null) return false;
        process.WaitForExit(10000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private sealed record Clip(string Id, string VideoPath, string VideoRelative, string SourceSha, VideoMetadata Metadata, string BaselinePath, string BaselineRelative, string BaselineSha, string BaselineDirectory, byte[] BaselineBytes, AnalysisRun Baseline, TrackingSettings Settings, string? AnimalId);

    private sealed class ScriptedTracker : IReplayTracker
    {
        private readonly NativeReplayTracker _inner = new();
        public bool CancelOn { get; set; } = true;
        public Dictionary<string, int> Calls { get; } = [];
        public string NativeVersion => _inner.NativeVersion;
        public string HashFile(string path) => _inner.HashFile(path);
        public VideoMetadata Probe(string path) => _inner.Probe(path);
        public AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation)
        {
            Calls[asset.Id] = Calls.GetValueOrDefault(asset.Id) + 1;
            if (asset.Id == "shortcut")
            {
                var real = _inner.Analyze(asset, cancellation);
                return real with { Points = [real.Points[0]], NominalTimingVerified = true };
            }
            if (asset.Id == "geometry")
            {
                var real = _inner.Analyze(asset, cancellation);
                return real with { Settings = real.Settings with { Cx = .2 } };
            }
            if (asset.Id == "cancel-me" && CancelOn) throw new OperationCanceledException("operator cancelled");
            return _inner.Analyze(asset, cancellation);
        }
    }

    private sealed class OutsideCoordinateTracker : IReplayTracker
    {
        private readonly NativeReplayTracker _inner = new();
        public string NativeVersion => _inner.NativeVersion;
        public string HashFile(string path) => _inner.HashFile(path);
        public VideoMetadata Probe(string path) => _inner.Probe(path);
        public AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation)
        {
            var real = _inner.Analyze(asset, cancellation);
            var points = real.Points.ToList();
            points[0] = points[0] with { X = real.Metadata.Width, Y = 1, Status = PointStatus.Tracked };
            return real with { Points = points };
        }
    }
}

public sealed class Scratch : IDisposable
{
    private static void UnlinkReparse(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry, false);
                else File.Delete(entry);
                continue;
            }
            if ((attributes & FileAttributes.Directory) != 0) UnlinkReparse(entry);
        }
    }

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "MouseTrackerTabajaraReplay-" + Guid.NewGuid().ToString("N"));
    public Scratch() => Directory.CreateDirectory(Folder);
    public void Dispose()
    {
        if (!Directory.Exists(Folder)) return;
        var full = Path.GetFullPath(Folder);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("MouseTrackerTabajaraReplay-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid test cleanup target.");
        UnlinkReparse(full);
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(full, true);
    }
}
