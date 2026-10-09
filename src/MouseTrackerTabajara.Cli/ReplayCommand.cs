using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Tracking;

namespace MouseTrackerTabajara.Cli;

public interface IReplayTracker
{
    string NativeVersion { get; }
    string HashFile(string path);
    VideoMetadata Probe(string path);
    AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation);
}

public sealed class NativeReplayTracker : IReplayTracker
{
    public string NativeVersion => OpenCvTracker.NativeVersion;
    public string HashFile(string path) => OpenCvTracker.HashFile(path);
    public VideoMetadata Probe(string path) => OpenCvTracker.Probe(path);
    public AnalysisRun Analyze(VideoAsset asset, CancellationToken cancellation) => OpenCvTracker.Analyze(asset, progress: null, cancellation);
}

public sealed record ReplayProgress(string Phase, int Planned, int Completed, int Failed, int Unrun, int Cancelled, string? RecordingId, string? ScenarioName);

public sealed class ReplayCommandContext
{
    public IReplayTracker Tracker { get; init; } = new NativeReplayTracker();
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Error { get; init; } = Console.Error;
    public CancellationToken Cancellation { get; init; }
    public Func<ReplayCodeIdentity>? CaptureIdentity { get; init; }
    public Action<ReplayProgress>? Progress { get; init; }
}

public sealed record ReplayInvocation(string PlanPath, string OutputPath, string? CancelWhenPath, bool Help);

public static class ReplayCommand
{
    public const string TopLevelHelp = """
        Mouse Tracker Tabajara native CLI
          analyze <video> <output folder> [settings.json]
          verify <native-analysis.json> <python-analysis.json>
          doctor
          replay <plan.json> <output directory> [--cancel-when <file>]

        replay runs every prespecified detector scenario through the existing native tracker.
        Run replay --help for exit codes, preserved fields, and tested versus held gates.
        """;

    public const string ReplayHelp = """
        replay <plan.json> <output directory> [--cancel-when <file>]

        Freezes the collection plan, then runs every recording against every named scenario.
        Scenarios may change only threshold, blur, polarity (invert), area, extent, and jump.
        Pool geometry, platform geometry, exclusions, the decoder interval (start_seconds and
        end_seconds), and distance units stay on each recording's frozen baseline. That interval
        is the analyzed decoder window. It is not a biological release and not a confirmed trial
        window. The baseline file, the video, and a published attempt are not overwritten. Animal
        fields are kept only when the plan states them; file names are not animal identity and
        are not biological N.

        Every planned case stays in the manifest, including failures and unfavorable decoder
        results. Reported differences are raw decoder-run counts for one prespecified detector
        setting. Path, speed, occupancy, and first-visit differences are withheld because no
        confirmed biological window contract is supplied. The command does not emit significance,
        a memory score, a causal claim, or a group conclusion. Failed attempts are terminal
        evidence in that output directory and are not rerun. Cancelled and unrun cases resume
        only when the plan bytes, source hashes, baseline hashes, and the frozen code and native
        binary identity still match. A version string alone is not that identity. A second
        process cannot take an output directory whose replay lease is held.

        --cancel-when is observed between cases. An empty file cancels before the next case.
        after-completed=<n> cancels once n cases are already completed. Ctrl+C cancels the active
        analysis. Cancellation keeps published attempts and marks cases that have not started as unrun.

        Exit codes:
          0  every planned case is completed
          1  usage error
          2  plan or identity rejected before output was changed
          3  cancelled; completed publications retained; remaining cases unrun
          4  one or more cases failed and were retained

        Tested on short synthetic videos: frozen plan and source identity, code and native
        binary identity, immutable baseline and published bytes, malformed evidence, per-case
        failure retention, cancellation and resume, and a held cross-process output lease.
        Preserved geometry, decoder intervals, and units are part of that check.
        Not established here: desktop or study integration, real recordings, and biological
        release or trial inference.
        """;

    public static int Execute(IReadOnlyList<string> args, ReplayCommandContext? context = null)
    {
        context ??= new ReplayCommandContext();
        try
        {
            var invocation = Parse(args);
            if (invocation.Help)
            {
                context.Out.WriteLine(ReplayHelp);
                return 0;
            }
            CancelWhen.EnsureValid(invocation.CancelWhenPath);
            return DetectorScenarioReplay.Run(invocation, context);
        }
        catch (UsageException exception)
        {
            context.Error.WriteLine(exception.Message);
            context.Error.WriteLine(ReplayHelp);
            return 1;
        }
        catch (PlanRejectedException exception)
        {
            context.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static ReplayInvocation Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !string.Equals(args[0], "replay", StringComparison.Ordinal))
            throw new UsageException("The replay command is missing.");
        if (args.Count == 2 && args[1] is "--help" or "-h") return new ReplayInvocation("", "", null, Help: true);
        if (args.Count < 3) throw new UsageException("Specify a plan file and an output directory.");
        string? cancelWhen = null;
        for (var index = 3; index < args.Count; index++)
        {
            if (args[index] == "--cancel-when" && index + 1 < args.Count)
            {
                cancelWhen = args[++index];
                continue;
            }
            throw new UsageException($"Unknown replay argument '{args[index]}'.");
        }
        if (string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[2]))
            throw new UsageException("Specify a plan file and an output directory.");
        return new ReplayInvocation(args[1], args[2], cancelWhen, Help: false);
    }
}
