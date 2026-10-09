using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MouseTrackerTabajara.Cli;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Tracking;
using Xunit;

namespace MouseTrackerTabajara.Sensitivity.Tests;

public partial class SensitivityNativeProof
{
    [Fact]
    public async Task SharedFreezeOwnershipRejectsOtherCallersAndRecovers()
    {
        var evidence = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "tabajara-sensitivity-ownership-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside-evidence");
        var decoyStaging = Path.Combine(root, "decoy-name", "freeze-staging");
        var decoyPrevious = Path.Combine(root, "decoy-name", "freeze.previous");
        Process? interrupt = null;
        string? alias = null;
        string? escape = null;
        string? previousJunction = null;
        try
        {
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "keep");
            Directory.CreateDirectory(decoyStaging);
            Directory.CreateDirectory(decoyPrevious);
            File.WriteAllText(Path.Combine(decoyStaging, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(decoyPrevious, "keep.txt"), "keep");
            var first = CreateStudy(Path.Combine(root, "study-one"));
            var second = CreateStudy(Path.Combine(root, "study-two"));
            FreezeOnce(first, "owner-plan");
            var workspace = SensitivityCatalog.WorkspaceFor(first);
            var freezeDirectory = Path.Combine(workspace, "freeze");
            var staging = Path.Combine(workspace, "freeze-staging");
            var previous = Path.Combine(workspace, "freeze.previous");
            var published = RelativeSnapshot(freezeDirectory);
            Directory.CreateDirectory(staging);
            var partial = Path.Combine(staging, "partial-receipt.txt");
            File.WriteAllText(partial, "keep-partial");
            var gate = new OwnerGate();
            var owner = Task.Run(() => SensitivityCatalog.Freeze(AtlasSnapshot.Read(first), [Draft("owner-refresh")], workspace, gate));
            var entered = await Task.WhenAny(gate.Entered.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(gate.Entered.Task, entered);
            Assert.Equal("keep-partial", File.ReadAllText(partial));
            AssertRelative(published, freezeDirectory);
            Assert.Contains("owner-plan", File.ReadAllText(Path.Combine(freezeDirectory, "plan.json")), StringComparison.Ordinal);
            Assert.DoesNotContain("owner-refresh", File.ReadAllText(Path.Combine(freezeDirectory, "plan.json")), StringComparison.Ordinal);

            var acquireDenied = Assert.Throws<InvalidDataException>(() => SensitivityFreezeLease.Acquire(workspace));
            Assert.Equal(SensitivityFreezeLease.ConflictMessage, acquireDenied.Message);
            var callerDenied = Assert.Throws<InvalidDataException>(() =>
            {
                using var lost = SensitivityCatalog.Freeze(AtlasSnapshot.Read(first), [Draft("contender-plan")], workspace, new NativeReplayTracker());
                lost.Acknowledge();
            });
            Assert.Equal(SensitivityFreezeLease.ConflictMessage, callerDenied.Message);
            var report = Path.Combine(root, "contend-report.txt");
            var contended = RunHost("contend", first, report);
            var reportText = File.ReadAllText(report);
            Assert.Equal(3, contended.Code);
            Assert.Contains(SensitivityFreezeLease.ConflictMessage, reportText, StringComparison.Ordinal);
            Assert.StartsWith("REJECTED", reportText, StringComparison.Ordinal);
            FreezeOnce(second, "workspace-two");
            Assert.Equal("keep-partial", File.ReadAllText(partial));
            AssertRelative(published, freezeDirectory);
            Assert.False(Directory.Exists(previous));
            Assert.Contains("workspace-two", File.ReadAllText(Path.Combine(SensitivityCatalog.WorkspaceFor(second), "freeze", "plan.json")), StringComparison.Ordinal);
            Assert.DoesNotContain("workspace-two", File.ReadAllText(Path.Combine(freezeDirectory, "plan.json")), StringComparison.Ordinal);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(decoyStaging, "keep.txt")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(decoyPrevious, "keep.txt")));

            gate.Release.TrySetResult();
            using (var refreshed = await owner)
            {
                refreshed.Acknowledge();
                Assert.Contains("owner-refresh", File.ReadAllText(Path.Combine(freezeDirectory, "plan.json")), StringComparison.Ordinal);
                Assert.DoesNotContain("contender-plan", File.ReadAllText(Path.Combine(freezeDirectory, "plan.json")), StringComparison.Ordinal);
            }
            AssertSettledFreeze(workspace);
            var inventory = SensitivityCatalog.ReadInventory(workspace, SensitivityCatalog.TryLoad(workspace)!);
            Assert.Contains(inventory!, row => row.RecordingId == "rec-a" && row.Availability == "eligible");
            evidence.Add("PASS a same-workspace caller and a second process fail before touching the owner's staging, previous tree, or publication, while a different workspace freezes independently.");

            var restored = RelativeSnapshot(freezeDirectory);
            Directory.CreateDirectory(staging);
            File.WriteAllText(partial, "keep-partial");
            var ready = Path.Combine(root, "interrupt-ready.txt");
            interrupt = StartHost("interrupt", workspace, ready);
            WaitForFile(ready);
            Assert.False(Directory.Exists(freezeDirectory));
            AssertRelative(restored, previous);
            Assert.Equal("keep-partial", File.ReadAllText(partial));
            var duringDeath = RunHost("contend", first, report);
            Assert.Equal(3, duringDeath.Code);
            Assert.Contains(SensitivityFreezeLease.ConflictMessage, File.ReadAllText(report), StringComparison.Ordinal);
            Assert.False(Directory.Exists(freezeDirectory));
            AssertRelative(restored, previous);
            Assert.Equal("keep-partial", File.ReadAllText(partial));
            interrupt.Kill(entireProcessTree: true);
            Assert.True(interrupt.WaitForExit(10000));
            interrupt.Dispose();
            interrupt = null;
            WaitUntilFree(workspace);
            var stopped = Assert.Throws<InvalidDataException>(() => SensitivityCatalog.Freeze(AtlasSnapshot.Read(first), [Draft("recovery-stop")], workspace, new StopAfterRecoveryTracker()));
            Assert.Contains("Stopped after interrupted publication recovery.", stopped.Message, StringComparison.Ordinal);
            AssertRelative(restored, freezeDirectory);
            Assert.False(Directory.Exists(previous));
            Assert.Equal("keep-partial", File.ReadAllText(partial));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
            evidence.Add("PASS process death releases the exclusive handle, a contender still fails while that handle is open, and the next owner restores the publication without deleting partial staging.");

            var abandoned = SensitivityCatalog.Freeze(AtlasSnapshot.Read(first), [Draft("abandoned-plan")], workspace, new NativeReplayTracker());
            var liveBytes = RelativeSnapshot(freezeDirectory);
            var previousBytes = RelativeSnapshot(previous);
            Assert.Contains("abandoned-plan", File.ReadAllText(Path.Combine(freezeDirectory, "plan.json")), StringComparison.Ordinal);
            abandoned.ReleaseHandleWithoutRollback();
            var late = Assert.Throws<InvalidDataException>(() => abandoned.Revert());
            Assert.Equal(SensitivityFreezeLease.ReleasedMessage, late.Message);
            var both = Assert.Throws<InvalidDataException>(() =>
            {
                using var later = SensitivityCatalog.Freeze(AtlasSnapshot.Read(first), [Draft("later-plan")], workspace, new NativeReplayTracker());
                later.Acknowledge();
            });
            Assert.Equal(SensitivityFreezeLease.AmbiguousMessage, both.Message);
            AssertRelative(liveBytes, freezeDirectory);
            AssertRelative(previousBytes, previous);
            var leasePath = Path.Combine(workspace, SensitivityFreezeLease.FileName);
            File.WriteAllText(leasePath, "pid=1");
            using (var held = SensitivityFreezeLease.Acquire(workspace))
            {
                var pidDenied = Assert.Throws<InvalidDataException>(() => SensitivityFreezeLease.Acquire(workspace));
                Assert.Equal(SensitivityFreezeLease.ConflictMessage, pidDenied.Message);
                AssertRelative(liveBytes, freezeDirectory);
                AssertRelative(previousBytes, previous);
            }
            AssertRelative(liveBytes, freezeDirectory);
            AssertRelative(previousBytes, previous);
            abandoned.Dispose();
            evidence.Add("PASS a late rollback after the handle is released leaves both parked trees unchanged, and a process-id marker is not ownership.");

            var secondWorkspace = SensitivityCatalog.WorkspaceFor(second);
            var secondFreeze = Path.Combine(secondWorkspace, "freeze");
            var secondPublished = RelativeSnapshot(secondFreeze);
            alias = Path.Combine(root, "workspace-alias");
            Assert.True(TryJunction(alias, secondWorkspace), "The workspace junction could not be created.");
            var aliasDenied = Assert.Throws<InvalidDataException>(() => SensitivityFreezeLease.Acquire(alias));
            Assert.Contains("reparse", aliasDenied.Message, StringComparison.OrdinalIgnoreCase);
            AssertRelative(secondPublished, secondFreeze);
            Directory.Delete(alias, recursive: false);
            alias = null;

            Directory.CreateDirectory(Path.Combine(secondWorkspace, "freeze-staging"));
            escape = Path.Combine(secondWorkspace, "freeze-staging", "escape");
            Assert.True(TryJunction(escape, outside), "The staging descendant junction could not be created.");
            File.WriteAllText(Path.Combine(secondWorkspace, "freeze-staging", "partial-receipt.txt"), "keep-partial");
            var descendant = Assert.Throws<InvalidDataException>(() =>
            {
                using var lost = SensitivityCatalog.Freeze(AtlasSnapshot.Read(second), [Draft("reparse-plan")], secondWorkspace, new NativeReplayTracker());
                lost.Acknowledge();
            });
            Assert.Contains("Nothing was deleted", descendant.Message, StringComparison.Ordinal);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
            Assert.Equal("keep-partial", File.ReadAllText(Path.Combine(secondWorkspace, "freeze-staging", "partial-receipt.txt")));
            Assert.True(Directory.Exists(escape));
            AssertRelative(secondPublished, secondFreeze);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(decoyStaging, "keep.txt")));

            var saved = Path.Combine(root, "saved-live");
            Directory.Move(secondFreeze, saved);
            previousJunction = Path.Combine(secondWorkspace, "freeze.previous");
            Assert.True(TryJunction(previousJunction, outside), "The previous-tree junction could not be created.");
            var parked = Assert.Throws<InvalidDataException>(() =>
            {
                using var lost = SensitivityCatalog.Freeze(AtlasSnapshot.Read(second), [Draft("parked-plan")], secondWorkspace, new NativeReplayTracker());
                lost.Acknowledge();
            });
            Assert.Contains("reparse", parked.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
            Assert.True(Directory.Exists(previousJunction));
            Assert.False(Directory.Exists(secondFreeze));
            AssertRelative(secondPublished, saved);
            evidence.Add("PASS reparse aliases and same-named folders outside the workspace are rejected before recursive deletion, and the prior publication stays in place.");
        }
        finally
        {
            if (interrupt is not null)
            {
                if (!interrupt.HasExited)
                {
                    interrupt.Kill(entireProcessTree: true);
                    interrupt.WaitForExit(10000);
                }
                interrupt.Dispose();
            }
            RemoveJunction(alias);
            RemoveJunction(escape);
            RemoveJunction(previousJunction);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
        foreach (var line in evidence) output.WriteLine(line);
    }

    private static void ProveTwoWindowFreezeOwnership(List<string> evidence)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tabajara-sensitivity-windows-" + Guid.NewGuid().ToString("N"));
        SensitivityWindow? first = null;
        SensitivityWindow? second = null;
        try
        {
            var database = CreateStudy(folder);
            FreezeOnce(database, "window-owner");
            var workspace = SensitivityCatalog.WorkspaceFor(database);
            var freezeDirectory = Path.Combine(workspace, "freeze");
            var published = RelativeSnapshot(freezeDirectory);
            first = OpenSensitive(database);
            Unblind(first, "Own the shared freeze");
            var gate = new OwnerGate();
            first.Tracker = gate;
            first.ScenarioNameBox.Text = "window-refresh";
            first.RationaleBox.Text = "The first window still owns this workspace.";
            first.ThresholdBox.Text = "40";
            Click(first, "Save named scenario");
            Click(first, "Freeze plan and identities");
            Pump(gate.Entered.Task, 30);
            var partial = Path.Combine(workspace, "freeze-staging", "partial-receipt.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
            File.WriteAllText(partial, "keep-partial");
            second = OpenSensitive(database);
            Unblind(second, "Contend for the same workspace");
            second.ScenarioNameBox.Text = "window-contender";
            second.RationaleBox.Text = "This window must fail before changing the owner.";
            second.ThresholdBox.Text = "40";
            Click(second, "Save named scenario");
            Click(second, "Freeze plan and identities");
            Pump(second.PendingRun, 60);
            Assert.True(second.Held, second.StatusText);
            Assert.Contains(SensitivityFreezeLease.ConflictMessage, second.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain("Plan frozen", second.StatusText, StringComparison.Ordinal);
            AssertSettledWindowPlan(second, workspace, "window-owner");
            Assert.Equal("keep-partial", File.ReadAllText(partial));
            AssertRelative(published, freezeDirectory);
            Assert.DoesNotContain("window-contender", File.ReadAllText(Path.Combine(freezeDirectory, "plan.json")), StringComparison.Ordinal);
            second.Close();
            second = null;
            Assert.Equal("keep-partial", File.ReadAllText(partial));
            AssertRelative(published, freezeDirectory);
            first.Cancel();
            gate.Release.TrySetResult();
            Pump(first.PendingRun, 60);
            Assert.Contains("cancelled", first.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Plan frozen", first.StatusText, StringComparison.Ordinal);
            Assert.Equal("keep-partial", File.ReadAllText(partial));
            AssertRelative(published, freezeDirectory);
            Assert.False(Directory.Exists(Path.Combine(workspace, "freeze.previous")));
            evidence.Add("PASS a second window fails before touching the owner's publication, and its close or the owner's cancel leaves that publication and partial staging in place.");

            second = OpenSensitive(database);
            Unblind(second, "Contend after the owner published");
            second.ScenarioNameBox.Text = "window-contender";
            second.RationaleBox.Text = "This window must not announce a frozen plan.";
            second.ThresholdBox.Text = "40";
            Click(second, "Save named scenario");
            AssertSettledWindowPlan(second, workspace, "window-owner");
            var post = new PostPublishGate(workspace, "window-post");
            first.Tracker = post;
            first.ScenarioNameBox.Text = "window-post";
            first.RationaleBox.Text = "The owner pauses after publication, still holding the workspace.";
            first.ThresholdBox.Text = "40";
            Click(first, "Save named scenario");
            Click(first, "Freeze plan and identities");
            Pump(post.Entered.Task, 30);
            var previousDirectory = Path.Combine(workspace, "freeze.previous");
            Assert.True(Directory.Exists(previousDirectory));
            AssertRelative(published, previousDirectory);
            var publishedDuringHold = File.ReadAllText(Path.Combine(freezeDirectory, "plan.json"));
            Assert.Contains("window-post", publishedDuringHold, StringComparison.Ordinal);
            Assert.DoesNotContain("window-contender", publishedDuringHold, StringComparison.Ordinal);
            Assert.Equal(["window-owner"], first.Freeze!.Scenarios.Select(item => item.Name).ToArray());
            Assert.False(SensitivityCatalog.SamePlanBytes(workspace, first.Freeze));
            Assert.DoesNotContain("Plan frozen", first.StatusText, StringComparison.Ordinal);
            Click(second, "Freeze plan and identities");
            Pump(second.PendingRun, 60);
            Assert.True(second.Held, second.StatusText);
            Assert.Contains(SensitivityFreezeLease.ConflictMessage, second.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain("Plan frozen", second.StatusText, StringComparison.Ordinal);
            Assert.Equal(["window-owner"], second.Freeze!.Scenarios.Select(item => item.Name).ToArray());
            Assert.False(SensitivityCatalog.SamePlanBytes(workspace, second.Freeze));
            AssertRelative(published, previousDirectory);
            Assert.Equal(publishedDuringHold, File.ReadAllText(Path.Combine(freezeDirectory, "plan.json")));
            first.Cancel();
            post.Release.TrySetResult();
            Pump(first.PendingRun, 60);
            Assert.Contains("cancelled", first.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Plan frozen", first.StatusText, StringComparison.Ordinal);
            AssertSettledWindowPlan(first, workspace, "window-owner");
            Assert.Contains(SensitivityFreezeLease.ConflictMessage, second.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain("Plan frozen", second.StatusText, StringComparison.Ordinal);
            AssertSettledWindowPlan(second, workspace, "window-owner");
            AssertRelative(published, freezeDirectory);
            Assert.False(Directory.Exists(previousDirectory));
            var restoredPlan = File.ReadAllText(Path.Combine(freezeDirectory, "plan.json"));
            Assert.DoesNotContain("window-contender", restoredPlan, StringComparison.Ordinal);
            Assert.DoesNotContain("window-post", restoredPlan, StringComparison.Ordinal);
            second.Close();
            second = null;
            AssertRelative(published, freezeDirectory);
            evidence.Add("PASS a second window fails while the owner is paused after publication; it does not announce a frozen plan, and the owner's cancel restores only the prior publication.");
        }
        finally
        {
            second?.Close();
            first?.Close();
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static string CreateStudy(string folder)
    {
        Directory.CreateDirectory(folder);
        var database = Path.Combine(folder, "synthetic.morrisdb");
        var store = new StudyStore(database);
        Import(store, folder, "rec-a", "PRIVATE-WT-KO-A.avi", 0, "A", "WT", "Informative WT KO alias", true, DetectorSettings());
        return database;
    }

    private static void FreezeOnce(string database, string scenario)
    {
        using var publication = SensitivityCatalog.Freeze(AtlasSnapshot.Read(database), [Draft(scenario)], SensitivityCatalog.WorkspaceFor(database), new NativeReplayTracker());
        publication.Acknowledge();
    }

    private static SensitivityScenarioDraft Draft(string name) => new()
    {
        Name = name,
        Rationale = "Prespecified synthetic freeze ownership scenario.",
        Threshold = 40
    };

    private static Dictionary<string, byte[]> RelativeSnapshot(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertRelative(Dictionary<string, byte[]> before, string directory)
    {
        var after = RelativeSnapshot(directory);
        Assert.Equal(before.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase), after.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
    }

    private static void AssertSettledWindowPlan(SensitivityWindow window, string workspace, params string[] names)
    {
        Assert.NotNull(window.Freeze);
        Assert.Equal(names, window.Freeze.Scenarios.Select(item => item.Name).ToArray());
        Assert.True(SensitivityCatalog.SamePlanBytes(workspace, window.Freeze));
    }

    private static string HostExe()
    {
        var exe = Path.ChangeExtension(typeof(SensitivityOwnershipHost).Assembly.Location, ".exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("The ownership host executable is missing.", exe);
        return exe;
    }

    private static Process StartHost(params string[] args)
    {
        var exe = HostExe();
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("The ownership host did not start.");
    }

    private static (int Code, string Stdout, string Stderr) RunHost(params string[] args)
    {
        using var process = StartHost(args);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10000);
            throw new TimeoutException("The ownership host did not exit. " + stderr.GetAwaiter().GetResult());
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void WaitForFile(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return;
            Thread.Sleep(20);
        }
        throw new TimeoutException("The ownership host did not become ready.");
    }

    private static void WaitUntilFree(string workspace)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var lease = SensitivityFreezeLease.Acquire(workspace);
                return;
            }
            catch (Exception exception)
            {
                last = exception;
                Thread.Sleep(50);
            }
        }
        throw new TimeoutException("The released freeze handle was not recoverable. " + last?.Message);
    }

    private static void RemoveJunction(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || !Directory.Exists(link)) return;
        try { Directory.Delete(link, recursive: false); } catch (IOException) { }
    }

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

    private sealed class OwnerGate : IReplayTracker
    {
        private readonly NativeReplayTracker _inner = new();
        private int _hashes;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string NativeVersion => _inner.NativeVersion;
        public string HashFile(string path)
        {
            if (Interlocked.Increment(ref _hashes) == 1)
            {
                Entered.TrySetResult();
                if (!Release.Task.Wait(TimeSpan.FromSeconds(90))) throw new TimeoutException("The owner freeze was not released.");
            }
            return _inner.HashFile(path);
        }
        public VideoMetadata Probe(string path) => _inner.Probe(path);
        public AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation) => _inner.Analyze(asset, cancellation);
    }

    private sealed class PostPublishGate : IReplayTracker
    {
        private readonly NativeReplayTracker _inner = new();
        private readonly string _liveDocument;
        private readonly string _planName;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PostPublishGate(string workspace, string planName)
        {
            _liveDocument = Path.Combine(workspace, "freeze", "sensitivity-freeze.json");
            _planName = planName;
        }
        public string NativeVersion => _inner.NativeVersion;
        public string HashFile(string path)
        {
            if (File.Exists(_liveDocument) && File.ReadAllText(_liveDocument).Contains(_planName, StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                if (!Release.Task.Wait(TimeSpan.FromSeconds(90))) throw new TimeoutException("The post-publication owner freeze was not released.");
            }
            return _inner.HashFile(path);
        }
        public VideoMetadata Probe(string path) => _inner.Probe(path);
        public AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation) => _inner.Analyze(asset, cancellation);
    }

    private sealed class StopAfterRecoveryTracker : IReplayTracker
    {
        public string NativeVersion => "synthetic-recovery";
        public string HashFile(string path) => throw new InvalidDataException("Stopped after interrupted publication recovery.");
        public VideoMetadata Probe(string path) => throw new InvalidDataException("Stopped after interrupted publication recovery.");
        public AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation) => throw new InvalidDataException("Stopped after interrupted publication recovery.");
    }
}
