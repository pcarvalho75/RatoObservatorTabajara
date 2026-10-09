using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MouseTrackerTabajara.Core;

public enum BiologicalEventKind
{
    WaterEntry,
    PlatformMounting,
    ResearcherIntervention,
    RecordingEnd
}

public sealed record BiologicalEvent
{
    public string Id { get; init; } = "";
    public string VideoId { get; init; } = "";
    public string? RunId { get; init; }
    public BiologicalEventKind Kind { get; init; }
    public int Frame { get; init; }
    public double TimeSeconds { get; init; }
    public string Reason { get; init; } = "";
    public string Reviewer { get; init; } = "";
    public int Revision { get; init; }
    public DateTime CreatedUtc { get; init; }

    public static BiologicalEvent Create(string videoId, string? runId, BiologicalEventKind kind, int frame, VideoMetadata metadata, string reason, string reviewer, int revision, DateTime createdUtc, string id)
    {
        FrameIdentity.RequireSourceFrame(frame, metadata.FrameCount);
        if (string.IsNullOrWhiteSpace(videoId)) throw new ArgumentException("Biological annotation requires a video.");
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Biological annotation requires an id.");
        if (revision < 1) throw new ArgumentException("Biological annotation revision must start at 1.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A biological annotation requires a reason.");
        if (string.IsNullOrWhiteSpace(reviewer)) throw new ArgumentException("A biological annotation requires a reviewer.");
        if (!double.IsFinite(metadata.Fps) || metadata.Fps <= 0) throw new ArgumentException("Source frame rate is invalid.");
        return new()
        {
            Id = id,
            VideoId = videoId,
            RunId = runId,
            Kind = kind,
            Frame = frame,
            TimeSeconds = frame / metadata.Fps,
            Reason = reason.Trim(),
            Reviewer = reviewer.Trim(),
            Revision = revision,
            CreatedUtc = createdUtc
        };
    }
}

public sealed record ProtocolWindow
{
    public string VideoId { get; init; } = "";
    public int Revision { get; init; }
    public TrialMode Phase { get; init; }
    public double RequestedExposureSeconds { get; init; }
    public bool ReleaseConfirmed { get; init; }
    public int? ReleaseFrame { get; init; }
    public bool WindowConfirmed { get; init; }
    public string Reviewer { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
}

/// <summary>An annotation that stays visible and does not by itself remove a trial from comparison.</summary>
public enum ProtocolNoteKind
{
    MountingBeforeRelease,
    MountingAfterWindow,
    MountingDuringProbe,
    InterventionBeforeRelease,
    InterventionAfterWindow
}

public sealed record ProtocolNote(ProtocolNoteKind Kind, string Text);

public sealed record ProtocolOutcome
{
    public TrialMode Phase { get; init; }
    public double RequestedExposureSeconds { get; init; }
    public double? ActualExposureSeconds { get; init; }
    public double? SourceExposureSeconds { get; init; }
    public double RecordingDurationSeconds { get; init; }
    public string TerminationReason { get; init; } = "";
    public IReadOnlyList<string> Holds { get; init; } = [];
    public IReadOnlyList<ProtocolNote> Notes { get; init; } = [];
    public string Summary { get; init; } = "";
    public string Visible
    {
        get
        {
            var lines = new List<string> { Summary };
            lines.AddRange(Holds);
            lines.AddRange(Notes.Select(note => note.Text));
            return string.Join("\n", lines);
        }
    }
}

public sealed record ProtocolCompatibility
{
    public bool Comparable { get; init; }
    public string Visible { get; init; } = "";
}

public static class ProtocolEvaluator
{
    public static ProtocolOutcome Evaluate(ProtocolWindow window, VideoMetadata metadata, TrialMode currentPhase, IReadOnlyList<BiologicalEvent> effectiveEvents)
    {
        if (!double.IsFinite(metadata.Fps) || metadata.Fps <= 0 || metadata.FrameCount <= 0)
            throw new ArgumentException("Source timing is invalid.");
        if (!double.IsFinite(window.RequestedExposureSeconds) || window.RequestedExposureSeconds <= 0)
            throw new ArgumentException("Requested exposure must stay a positive planned duration.");
        var recording = metadata.FrameCount / metadata.Fps;
        var holds = new List<string>();
        if (window.Phase != currentPhase) holds.Add("Stored window phase differs from the recording's current phase.");
        if (window.Phase == TrialMode.Unspecified) holds.Add("Trial phase is unspecified.");
        if (!window.WindowConfirmed) holds.Add("Planned window is not confirmed.");
        var events = effectiveEvents ?? throw new ArgumentException("Biological annotations are required, even when none are confirmed.");
        var release = UsableRelease(window, metadata, events, holds);
        if (release is null)
        {
            holds.Add("Actual exposure is withheld until release is confirmed.");
            return Finish(window, null, null, recording, "Termination is not established", holds, [], "");
        }
        var sourceEnd = SourceEnd(metadata, events);
        var available = sourceEnd - release.Value;
        if (available < 0)
        {
            holds.Add("Recording ends before the confirmed release.");
            return Finish(window, null, null, recording, "Source truncated", holds, [], "");
        }
        if (window.Phase == TrialMode.Probe)
            return Probe(window, events, release.Value, sourceEnd, available, recording, holds);
        if (window.Phase == TrialMode.Training)
            return Training(window, events, release.Value, sourceEnd, available, recording, holds);
        return Finish(window, null, available, recording, "Termination is not established", holds, [], "");
    }

    public static ProtocolCompatibility Compare(IReadOnlyList<(string Label, ProtocolOutcome Outcome)> trials)
    {
        ArgumentNullException.ThrowIfNull(trials);
        var lines = new List<string>();
        if (trials.Count == 0) lines.Add("No trials were supplied.");
        var phases = trials.Select(t => t.Outcome.Phase).Distinct().OrderBy(p => p.ToString(), StringComparer.Ordinal).ToArray();
        var mixed = phases.Length > 1;
        if (mixed) lines.Add("Mixed phases: " + string.Join(", ", phases) + ".");
        var probes = trials.Where(t => t.Outcome.Phase == TrialMode.Probe).ToArray();
        var unequal = probes.Select(p => p.Outcome.RequestedExposureSeconds).Distinct().Count() > 1;
        if (unequal)
        {
            var described = string.Join(", ", probes.Select(p => $"{p.Label} {p.Outcome.RequestedExposureSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s"));
            lines.Add("Unequal probe durations: " + described + ".");
        }
        var held = trials.Where(t => t.Outcome.Holds.Count > 0).ToArray();
        if (held.Length > 0)
        {
            lines.Add("Trials with holds stay visible and are not dropped.");
            foreach (var trial in held)
                foreach (var hold in trial.Outcome.Holds)
                    lines.Add(trial.Label + ": " + hold);
        }
        var trainings = trials.Where(t => t.Outcome.Phase == TrialMode.Training).ToArray();
        if (trainings.Length > 0 && probes.Length == 0 && !mixed)
            lines.Add("Training outcomes stay with their own termination reasons.");
        var comparable = trials.Count > 0 && !mixed && !unequal && held.Length == 0 && probes.Length == trials.Count;
        if (comparable)
            lines.Add("Confirmed probe exposures match at " + probes[0].Outcome.RequestedExposureSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s.");
        lines.Add("No statistical comparison is performed.");
        return new() { Comparable = comparable, Visible = string.Join("\n", lines) };
    }

    private static ProtocolOutcome Probe(ProtocolWindow window, IReadOnlyList<BiologicalEvent> events, double release, double sourceEnd, double available, double recording, List<string> holds)
    {
        var plannedEnd = release + window.RequestedExposureSeconds;
        var notes = new List<ProtocolNote>();
        RouteProbeAnnotation(holds, notes, events, BiologicalEventKind.PlatformMounting, release, plannedEnd,
            "Annotated mounting is before the confirmed release.", ProtocolNoteKind.MountingBeforeRelease,
            "Platform mounting is annotated and does not shorten the probe.", ProtocolNoteKind.MountingDuringProbe,
            "Annotated mounting is after the planned probe end.", ProtocolNoteKind.MountingAfterWindow);
        RouteProbeAnnotation(holds, notes, events, BiologicalEventKind.ResearcherIntervention, release, plannedEnd,
            "Annotated intervention is before the confirmed release.", ProtocolNoteKind.InterventionBeforeRelease,
            "Researcher intervention is annotated during the confirmed probe exposure.", null,
            "Annotated intervention is after the planned probe end.", ProtocolNoteKind.InterventionAfterWindow);
        if (sourceEnd + 1e-9 < plannedEnd)
        {
            holds.Add("Recording is truncated before the planned probe exposure.");
            return Finish(window, available, available, recording, "Source truncated", holds, notes, "");
        }
        return Finish(window, window.RequestedExposureSeconds, available, recording, "Fixed probe exposure", holds, notes, "");
    }

    private static ProtocolOutcome Training(ProtocolWindow window, IReadOnlyList<BiologicalEvent> events, double release, double sourceEnd, double available, double recording, List<string> holds)
    {
        var plannedEnd = release + window.RequestedExposureSeconds;
        var notes = new List<ProtocolNote>();
        var candidates = new List<(double Time, int Rank, string Reason, string? Hold)>
        {
            (plannedEnd, 3, "Protocol timeout", null)
        };
        var mounting = Earliest(events, BiologicalEventKind.PlatformMounting);
        if (mounting is not null && mounting.TimeSeconds + 1e-9 < release)
            notes.Add(new(ProtocolNoteKind.MountingBeforeRelease, "Annotated mounting is before the confirmed release."));
        else if (mounting is not null && mounting.TimeSeconds <= plannedEnd + 1e-9)
            candidates.Add((mounting.TimeSeconds, 0, "Confirmed platform mounting", null));
        else if (mounting is not null)
            notes.Add(new(ProtocolNoteKind.MountingAfterWindow, "Annotated mounting is after the planned training timeout."));
        var intervention = Earliest(events, BiologicalEventKind.ResearcherIntervention);
        if (intervention is not null && intervention.TimeSeconds + 1e-9 < release)
            notes.Add(new(ProtocolNoteKind.InterventionBeforeRelease, "Annotated intervention is before the confirmed release."));
        else if (intervention is not null)
            candidates.Add((intervention.TimeSeconds, 1, "Researcher intervention", "Researcher intervention ended the trial before the planned window."));
        if (sourceEnd + 1e-9 < plannedEnd)
            candidates.Add((sourceEnd, 2, "Source truncated", "Recording is truncated before the planned training timeout."));
        var winner = candidates.OrderBy(c => c.Time).ThenBy(c => c.Rank).First();
        if (winner.Hold is not null) holds.Add(winner.Hold);
        var actual = winner.Reason == "Protocol timeout" ? window.RequestedExposureSeconds : winner.Time - release;
        return Finish(window, actual, available, recording, winner.Reason, holds, notes, "");
    }

    private static ProtocolOutcome Finish(ProtocolWindow window, double? actual, double? source, double recording, string termination, List<string> holds, List<ProtocolNote> notes, string extra)
    {
        var culture = CultureInfo.InvariantCulture;
        var actualText = actual is double value ? value.ToString("0.000", culture) + " s" : "withheld";
        var sourceText = source is double after ? after.ToString("0.000", culture) + " s" : "withheld";
        var summary = $"Requested {window.RequestedExposureSeconds.ToString("0.000", culture)} s; actual {actualText}; source after release {sourceText}; recording {recording.ToString("0.000", culture)} s; termination {termination}. Requested exposure was not shortened.";
        if (extra.Length > 0) summary += " " + extra;
        return new()
        {
            Phase = window.Phase,
            RequestedExposureSeconds = window.RequestedExposureSeconds,
            ActualExposureSeconds = actual,
            SourceExposureSeconds = source,
            RecordingDurationSeconds = recording,
            TerminationReason = termination,
            Holds = holds,
            Notes = notes,
            Summary = summary
        };
    }

    private static double? UsableRelease(ProtocolWindow window, VideoMetadata metadata, IReadOnlyList<BiologicalEvent> events, List<string> holds)
    {
        if (!window.ReleaseConfirmed || window.ReleaseFrame is not int frame)
        {
            holds.Add("Release is not confirmed.");
            return null;
        }
        if (frame < 0 || frame >= metadata.FrameCount)
        {
            holds.Add("Release frame is outside the source recording.");
            return null;
        }
        if (!events.Any(e => e.Kind == BiologicalEventKind.WaterEntry && e.Frame == frame))
        {
            holds.Add("Release frame does not match a water-entry annotation.");
            return null;
        }
        return frame / metadata.Fps;
    }

    private static double SourceEnd(VideoMetadata metadata, IReadOnlyList<BiologicalEvent> events)
    {
        var container = metadata.FrameCount / metadata.Fps;
        var ending = Earliest(events, BiologicalEventKind.RecordingEnd);
        if (ending is null) return container;
        // The annotated frame is included. The last source frame therefore reaches the container duration.
        var included = Math.Min(container, (ending.Frame + 1) / metadata.Fps);
        return included;
    }

    private static BiologicalEvent? Earliest(IReadOnlyList<BiologicalEvent> events, BiologicalEventKind kind) =>
        events.Where(e => e.Kind == kind).OrderBy(e => e.Frame).ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();

    private static void RouteProbeAnnotation(List<string> holds, List<ProtocolNote> notes, IReadOnlyList<BiologicalEvent> events, BiologicalEventKind kind, double release, double plannedEnd, string before, ProtocolNoteKind beforeKind, string during, ProtocolNoteKind? duringNote, string after, ProtocolNoteKind afterKind)
    {
        var matched = events.Where(item => item.Kind == kind).ToArray();
        if (matched.Length == 0) return;
        if (matched.Any(item => item.TimeSeconds + 1e-9 < release)) notes.Add(new(beforeKind, before));
        if (matched.Any(item => item.TimeSeconds + 1e-9 >= release && item.TimeSeconds <= plannedEnd + 1e-9))
        {
            if (duringNote is ProtocolNoteKind noteKind) notes.Add(new(noteKind, during));
            else holds.Add(during);
        }
        if (matched.Any(item => item.TimeSeconds > plannedEnd + 1e-9)) notes.Add(new(afterKind, after));
    }
}

public sealed record RawRunMeasurementScope
{
    public bool TrialEndpointsWithheld { get; init; }
    public string Visible { get; init; } = "";
    public double DetectorStartSeconds { get; init; }
    public double DetectorEndSeconds { get; init; }
    public double? BiologicalStartSeconds { get; init; }
    public double? BiologicalEndSeconds { get; init; }

    public static RawRunMeasurementScope Describe(AnalysisRun run, ProtocolWindow? window, ProtocolOutcome? outcome)
    {
        if (!double.IsFinite(run.Metadata.Fps) || run.Metadata.Fps <= 0 || run.Metadata.FrameCount <= 0)
            throw new ArgumentException("Raw-run scope requires source timing.");
        var culture = CultureInfo.InvariantCulture;
        var recording = run.Metadata.FrameCount / run.Metadata.Fps;
        var detectorStart = run.Settings.StartSeconds;
        var detectorEnd = run.Settings.EndSeconds ?? recording;
        var lines = new List<string>
        {
            string.Create(culture, $"Raw decoder run {detectorStart:0.000} s to {detectorEnd:0.000} s ({run.Points.Count} analyzed frames). These figures are not confirmed biological-trial measurements.")
        };
        double? biologicalStart = null;
        double? biologicalEnd = null;
        var aligned = false;
        if (window is { ReleaseConfirmed: true, ReleaseFrame: int releaseFrame } && outcome?.ActualExposureSeconds is double actual)
        {
            biologicalStart = releaseFrame / run.Metadata.Fps;
            biologicalEnd = biologicalStart.Value + actual;
            var tolerance = 0.5 / run.Metadata.Fps;
            aligned = Math.Abs(detectorStart - biologicalStart.Value) <= tolerance && Math.Abs(detectorEnd - biologicalEnd.Value) <= tolerance;
            lines.Add(string.Create(culture, $"Confirmed biological window {biologicalStart.Value:0.000} s to {biologicalEnd.Value:0.000} s ({outcome!.TerminationReason}). The detector window was not shortened."));
        }
        else if (window is not null)
            lines.Add("The planned biological window is not a confirmed release interval. Full-run endpoints are not the confirmed trial.");
        else
            lines.Add("No planned biological window is stored. Values below describe only the raw decoder run.");
        if (window is not null && aligned)
            lines.Add(TrialMeasurementWording.AlignedRawRun);
        var withhold = window is not null && !aligned;
        if (withhold)
            lines.Add("Trial endpoints are withheld. Path, speed, occupancy, and first site visit from the raw decoder run are not the confirmed trial.");
        if (window is not null)
            lines.Add(TrialMeasurementWording.ProtocolLabelDoesNotSupply);
        return new()
        {
            TrialEndpointsWithheld = withhold,
            Visible = string.Join("\n", lines),
            DetectorStartSeconds = detectorStart,
            DetectorEndSeconds = detectorEnd,
            BiologicalStartSeconds = biologicalStart,
            BiologicalEndSeconds = biologicalEnd
        };
    }
}

public static class MaterialFingerprint
{
    public static string Hash(IEnumerable<string> storedJson)
    {
        var text = string.Join("\n", storedJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}

public sealed record ReviewContract
{
    public string RunId { get; init; } = "";
    public string VideoId { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public string SettingsHash { get; init; } = "";
    public string CorrectionFingerprint { get; init; } = "";
    public string EventFingerprint { get; init; } = "";
    public string ProtocolFingerprint { get; init; } = "";
    public string Note { get; init; } = "";
}

public sealed record ReviewEntry(long Id, string RunId, bool Confirmed, DateTime CreatedUtc, ReviewContract? Contract);
