using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Storage;

namespace MouseTrackerTabajara.Cli;

public sealed class PlanRejectedException(string message) : Exception(message);
public sealed class UsageException(string message) : ArgumentException(message);

public static class ReplayHashes
{
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed class ReplayPlanDocument
{
    public string? Schema { get; init; }
    public string? AlgorithmContract { get; init; }
    public List<ReplayPlanRecording>? Recordings { get; init; }
    public List<ReplayPlanScenario>? Scenarios { get; init; }
}

public sealed class ReplayPlanRecording
{
    public string? RecordingId { get; init; }
    public string? VideoPath { get; init; }
    public string? ExpectedSourceSha256 { get; init; }
    public ReplayPlanMetadata? ExpectedMetadata { get; init; }
    public string? BaselineAnalysisPath { get; init; }
    public string? BaselineSha256 { get; init; }
    public string? AnimalId { get; init; }
    public bool? AnimalIdConfirmed { get; init; }
    public string? GroupName { get; init; }
    public string? Phase { get; init; }
    public int? Day { get; init; }
    public int? Trial { get; init; }
    public string? ProvenanceNote { get; init; }
}

public sealed class ReplayPlanMetadata
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public int FrameCount { get; init; }
}

public sealed class ReplayPlanScenario
{
    public string? Name { get; init; }
    public int? Threshold { get; init; }
    public bool? Invert { get; init; }
    public int? Blur { get; init; }
    public double? MinArea { get; init; }
    public double? MaxArea { get; init; }
    public int? MaxExtension { get; init; }
    public double? MaxJump { get; init; }
    public bool OriginalDetectorSettings => Threshold is null && Invert is null && Blur is null && MinArea is null && MaxArea is null && MaxExtension is null && MaxJump is null;
}

public sealed class ResolvedPlan
{
    public required byte[] Bytes { get; init; }
    public required string Sha256 { get; init; }
    public required string PlanPath { get; init; }
    public required string PlanDirectory { get; init; }
    public required IReadOnlyList<ResolvedRecording> Recordings { get; init; }
    public required IReadOnlyList<ReplayPlanScenario> Scenarios { get; init; }
}

public sealed class ResolvedRecording
{
    public required string RecordingId { get; init; }
    public required string VideoPath { get; init; }
    public required string SourceSha256 { get; init; }
    public required VideoMetadata Metadata { get; init; }
    public required string BaselinePath { get; init; }
    public required string BaselineSha256 { get; init; }
    public required AnalysisRun Baseline { get; init; }
    public required TrackingSettings BaselineSettings { get; init; }
    public required string BaselineSettingsSha256 { get; init; }
    public bool AnimalIdProvided { get; init; }
    public string? AnimalId { get; init; }
    public bool? AnimalIdConfirmed { get; init; }
    public string? GroupName { get; init; }
    public string? Phase { get; init; }
    public int? Day { get; init; }
    public int? Trial { get; init; }
    public string? ProvenanceNote { get; init; }
    public string AnimalIdSource => AnimalIdProvided ? "plan" : "not_provided";
}

public static class ReplayPlanLoader
{
    public const int MaximumPlanBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions PlanOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static ResolvedPlan Load(string planPath, IReplayTracker tracker)
    {
        var fullPlan = Path.GetFullPath(planPath);
        if (!File.Exists(fullPlan)) throw new PlanRejectedException($"Plan file is missing: {fullPlan}");
        var info = new FileInfo(fullPlan);
        if (info.Length <= 0 || info.Length > MaximumPlanBytes) throw new PlanRejectedException("Plan file is empty or larger than 2 MiB.");
        var bytes = File.ReadAllBytes(fullPlan);
        var document = Parse(bytes);
        ValidateShape(document);
        var directory = Path.GetDirectoryName(fullPlan) ?? throw new PlanRejectedException("Plan path has no directory.");
        var recordings = new List<ResolvedRecording>(document.Recordings!.Count);
        var videoPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baselinePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recording in document.Recordings)
        {
            var video = ResolveExistingFile(directory, recording.VideoPath!, "Video");
            var baselinePath = ResolveExistingFile(directory, recording.BaselineAnalysisPath!, "Baseline analysis");
            if (!videoPaths.Add(video)) throw new PlanRejectedException($"Duplicate video path '{video}'.");
            if (!baselinePaths.Add(baselinePath)) throw new PlanRejectedException($"Duplicate baseline path '{baselinePath}'.");
            if (string.Equals(video, baselinePath, StringComparison.OrdinalIgnoreCase))
                throw new PlanRejectedException($"Recording '{recording.RecordingId}' uses the same path for the video and the baseline.");
            var sourceHash = HashInput(tracker, video, $"Recording '{recording.RecordingId}' video");
            if (!string.Equals(sourceHash, recording.ExpectedSourceSha256, StringComparison.Ordinal))
                throw new PlanRejectedException($"Recording '{recording.RecordingId}' source hash does not match the plan. The video was not modified.");
            VideoMetadata probed;
            try { probed = tracker.Probe(video); }
            catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException or IOException)
            { throw new PlanRejectedException($"Recording '{recording.RecordingId}' could not be probed: {exception.Message}"); }
            var expectedMetadata = new VideoMetadata(recording.ExpectedMetadata!.Width, recording.ExpectedMetadata.Height, recording.ExpectedMetadata.Fps, recording.ExpectedMetadata.FrameCount);
            if (!CompletedRunContract.SameMetadata(probed, expectedMetadata))
                throw new PlanRejectedException($"Recording '{recording.RecordingId}' probed metadata does not match the plan.");
            var baselineHash = HashInput(tracker, baselinePath, $"Recording '{recording.RecordingId}' baseline");
            if (!string.Equals(baselineHash, recording.BaselineSha256, StringComparison.Ordinal))
                throw new PlanRejectedException($"Recording '{recording.RecordingId}' baseline hash does not match the plan. The baseline was not modified.");
            AnalysisRun baseline;
            try { baseline = JsonDefaults.Deserialize<AnalysisRun>(File.ReadAllText(baselinePath)); }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            { throw new PlanRejectedException($"Recording '{recording.RecordingId}' baseline is not a readable analysis: {exception.Message}"); }
            try
            {
                CompletedRunContract.Ensure(baseline, expectedMetadata, baseline.Settings, sourceHash, AlgorithmIdentity.Contract, CompletedRunContract.SoftwareVersion);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            { throw new PlanRejectedException($"Recording '{recording.RecordingId}' baseline is not a complete automatic run: {exception.Message}"); }
            foreach (var scenario in document.Scenarios!)
            {
                try { Apply(baseline.Settings, scenario); }
                catch (ArgumentException exception)
                { throw new PlanRejectedException($"Scenario '{scenario.Name}' is not valid for recording '{recording.RecordingId}': {exception.Message}"); }
            }
            recordings.Add(new ResolvedRecording
            {
                RecordingId = recording.RecordingId!,
                VideoPath = video,
                SourceSha256 = sourceHash,
                Metadata = probed,
                BaselinePath = baselinePath,
                BaselineSha256 = baselineHash,
                Baseline = baseline,
                BaselineSettings = baseline.Settings,
                BaselineSettingsSha256 = SettingsHash(baseline.Settings),
                AnimalIdProvided = recording.AnimalId is not null,
                AnimalId = recording.AnimalId,
                AnimalIdConfirmed = recording.AnimalId is null ? null : recording.AnimalIdConfirmed,
                GroupName = recording.GroupName,
                Phase = recording.Phase,
                Day = recording.Day,
                Trial = recording.Trial,
                ProvenanceNote = recording.ProvenanceNote
            });
        }
        return new ResolvedPlan
        {
            Bytes = bytes,
            Sha256 = ReplayHashes.Sha256Hex(bytes),
            PlanPath = fullPlan,
            PlanDirectory = directory,
            Recordings = recordings,
            Scenarios = document.Scenarios!
        };
    }

    public static TrackingSettings Apply(TrackingSettings baseline, ReplayPlanScenario scenario)
    {
        var applied = new TrackingSettings
        {
            Cx = baseline.Cx,
            Cy = baseline.Cy,
            Rx = baseline.Rx,
            Ry = baseline.Ry,
            Threshold = scenario.Threshold ?? baseline.Threshold,
            Invert = scenario.Invert ?? baseline.Invert,
            Blur = scenario.Blur ?? baseline.Blur,
            MinArea = scenario.MinArea ?? baseline.MinArea,
            MaxArea = scenario.MaxArea ?? baseline.MaxArea,
            MaxExtension = scenario.MaxExtension ?? baseline.MaxExtension,
            MaxJump = scenario.MaxJump ?? baseline.MaxJump,
            DiameterCm = baseline.DiameterCm,
            StartSeconds = baseline.StartSeconds,
            EndSeconds = baseline.EndSeconds,
            Platform = baseline.Platform is null ? null : new PlatformSite(baseline.Platform.X, baseline.Platform.Y, baseline.Platform.Radius),
            WallBoundary = baseline.WallBoundary,
            ArenaConfirmed = baseline.ArenaConfirmed,
            Exclusions = (baseline.Exclusions ?? []).Select(region => new ExclusionRegion(region.Left, region.Top, region.Right, region.Bottom)).ToList()
        };
        applied.Validate();
        if (!CompletedRunContract.SamePreserved(baseline, applied))
            throw new InvalidDataException("Scenario application changed geometry, the decoder interval, or units.");
        return applied;
    }

    public static string SettingsHash(TrackingSettings settings) => StudyStore.SettingsHash(settings);

    private static string HashInput(IReplayTracker tracker, string path, string label)
    {
        try { return tracker.HashFile(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new PlanRejectedException($"{label} could not be hashed: {exception.Message}"); }
    }

    private static ReplayPlanDocument Parse(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) span = span[3..];
        string text;
        try { text = StrictUtf8.GetString(span); }
        catch (DecoderFallbackException) { throw new PlanRejectedException("Plan file is not valid UTF-8."); }
        try { RejectDuplicateProperties(span); }
        catch (JsonException exception) { throw new PlanRejectedException(InvalidJson(exception)); }
        try
        {
            return JsonSerializer.Deserialize<ReplayPlanDocument>(text, PlanOptions) ?? throw new PlanRejectedException("Plan JSON is empty.");
        }
        catch (JsonException exception) { throw new PlanRejectedException(InvalidJson(exception)); }
    }

    private static string InvalidJson(JsonException exception)
    {
        var message = exception.Message;
        if (message.Contains("could not be mapped", StringComparison.OrdinalIgnoreCase))
            message += " Scenarios may set only name, threshold, invert, blur, min_area, max_area, max_extension, and max_jump. Geometry, decoder intervals, and units belong to the frozen baseline. start_seconds and end_seconds are not a biological release.";
        return message.Length <= 500 ? "Plan JSON is invalid: " + message : "Plan JSON is invalid: " + message[..500];
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var seen = new Stack<HashSet<string>>();
        var depth = 0;
        var values = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (depth == 0) values++;
                    depth++;
                    seen.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    if (depth == 0 || seen.Count == 0) throw new JsonException("Plan JSON object is unbalanced.");
                    depth--;
                    seen.Pop();
                    break;
                case JsonTokenType.StartArray:
                    if (depth == 0) values++;
                    depth++;
                    break;
                case JsonTokenType.EndArray:
                    if (depth == 0) throw new JsonException("Plan JSON array is unbalanced.");
                    depth--;
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString() ?? "";
                    if (seen.Count == 0 || !seen.Peek().Add(name)) throw new JsonException($"Duplicate property '{name}'.");
                    break;
                default:
                    if (depth == 0) values++;
                    break;
            }
        }
        if (values != 1 || depth != 0 || reader.BytesConsumed != utf8.Length) throw new JsonException("Plan JSON must contain one value and no trailing data.");
    }

    private static void ValidateShape(ReplayPlanDocument document)
    {
        if (!string.Equals(document.Schema, ReplayClaims.PlanSchema, StringComparison.Ordinal))
            throw new PlanRejectedException($"Plan schema must be '{ReplayClaims.PlanSchema}'.");
        if (!string.Equals(document.AlgorithmContract, AlgorithmIdentity.Contract, StringComparison.Ordinal))
            throw new PlanRejectedException("Plan algorithm contract does not match the current native tracker. A different algorithm was not run.");
        if (document.Recordings is not { Count: > 0 } || document.Scenarios is not { Count: > 0 })
            throw new PlanRejectedException("Plan needs at least one recording and one named scenario.");
        var recordingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recording in document.Recordings)
        {
            RequireToken(recording.RecordingId, "Recording id");
            if (!recordingIds.Add(recording.RecordingId!)) throw new PlanRejectedException($"Duplicate recording id '{recording.RecordingId}'.");
            RequireHash(recording.ExpectedSourceSha256, "expected_source_sha256");
            RequireHash(recording.BaselineSha256, "baseline_sha256");
            if (string.IsNullOrWhiteSpace(recording.VideoPath) || string.IsNullOrWhiteSpace(recording.BaselineAnalysisPath))
                throw new PlanRejectedException($"Recording '{recording.RecordingId}' is missing a video or baseline path.");
            var meta = recording.ExpectedMetadata ?? throw new PlanRejectedException($"Recording '{recording.RecordingId}' is missing expected metadata.");
            if (meta.Width <= 0 || meta.Height <= 0 || meta.FrameCount <= 0 || !double.IsFinite(meta.Fps) || meta.Fps <= 0)
                throw new PlanRejectedException($"Recording '{recording.RecordingId}' metadata is not a usable video identity.");
            ValidateProvenance(recording);
        }
        var scenarioNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scenario in document.Scenarios)
        {
            RequireToken(scenario.Name, "Scenario name");
            if (!scenarioNames.Add(scenario.Name!)) throw new PlanRejectedException($"Duplicate scenario name '{scenario.Name}'.");
            if (scenario.Threshold is < 0 or > 255) throw new PlanRejectedException($"Scenario '{scenario.Name}' threshold is outside 0..255.");
            if (scenario.Blur is < 0 or > 31) throw new PlanRejectedException($"Scenario '{scenario.Name}' blur is outside 0..31.");
            if (scenario.MaxExtension is < 1 or > 100000) throw new PlanRejectedException($"Scenario '{scenario.Name}' extent is outside 1..100000.");
            if (scenario.MinArea is { } min && (!double.IsFinite(min) || min < 1 || min > 1e7)) throw new PlanRejectedException($"Scenario '{scenario.Name}' has a malformed min_area.");
            if (scenario.MaxArea is { } max && (!double.IsFinite(max) || max < 1 || max > 1e7)) throw new PlanRejectedException($"Scenario '{scenario.Name}' has a malformed max_area.");
            if (scenario.MaxJump is { } jump && (!double.IsFinite(jump) || jump < 1 || jump > 100000)) throw new PlanRejectedException($"Scenario '{scenario.Name}' has a malformed max_jump.");
        }
    }

    private static void ValidateProvenance(ReplayPlanRecording recording)
    {
        if (recording.AnimalId is not null) RequireLabel(recording.AnimalId, "animal_id");
        if (recording.AnimalId is null && recording.AnimalIdConfirmed is true)
            throw new PlanRejectedException($"Recording '{recording.RecordingId}' confirms an animal id that was not provided.");
        if (recording.GroupName is not null) RequireLabel(recording.GroupName, "group_name");
        if (recording.Phase is not null && recording.Phase is not ("unspecified" or "training" or "probe"))
            throw new PlanRejectedException($"Recording '{recording.RecordingId}' phase must be unspecified, training, or probe when it is provided.");
        if (recording.Day is < 1 or > 10000) throw new PlanRejectedException($"Recording '{recording.RecordingId}' day is outside 1..10000.");
        if (recording.Trial is < 1 or > 10000) throw new PlanRejectedException($"Recording '{recording.RecordingId}' trial is outside 1..10000.");
        if (recording.ProvenanceNote is not null) RequireLabel(recording.ProvenanceNote, "provenance_note");
    }

    private static void RequireToken(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !IsSafeToken(value))
            throw new PlanRejectedException($"{label} '{value}' is empty or unsafe as an output name.");
        var stem = value.Split('.')[0];
        if (ReservedNames.Contains(stem)) throw new PlanRejectedException($"{label} '{value}' is a reserved output name.");
    }

    private static bool IsSafeToken(string value)
    {
        if (!char.IsAsciiLetterOrDigit(value[0])) return false;
        foreach (var character in value)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) return false;
        return !value.EndsWith('.') && !value.Contains("..", StringComparison.Ordinal);
    }

    private static void RequireLabel(string value, string label)
    {
        if (value.Length is 0 or > 80 || value.Any(char.IsControl))
            throw new PlanRejectedException($"{label} must be a single visible line of at most 80 characters.");
    }

    private static void RequireHash(string? value, string label)
    {
        if (value is not { Length: 64 } || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new PlanRejectedException($"{label} must be 64 lowercase hex characters.");
    }

    private static string ResolveExistingFile(string planDirectory, string path, string label)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(planDirectory, path));
        if (!File.Exists(full)) throw new PlanRejectedException($"{label} file is missing: {full}");
        return full;
    }
}

public static class ReplayPaths
{
    public static void EnsureOutputSafe(string output, ResolvedPlan plan, string? cancelWhen)
    {
        var full = Path.GetFullPath(output);
        if (File.Exists(full)) throw new PlanRejectedException("Output path is an existing file. It was left unchanged.");
        RejectOverlap(full, plan.PlanPath, "plan file");
        if (cancelWhen is not null)
        {
            var cancel = Path.GetFullPath(cancelWhen);
            if (string.Equals(cancel, full, StringComparison.OrdinalIgnoreCase) || IsInside(cancel, full))
                throw new PlanRejectedException("The cancel-when file must be outside the output directory.");
        }
        foreach (var recording in plan.Recordings)
        {
            RejectOverlap(full, recording.VideoPath, $"video '{recording.RecordingId}'");
            RejectOverlap(full, recording.BaselinePath, $"baseline '{recording.RecordingId}'");
            var baselineDirectory = Path.GetDirectoryName(recording.BaselinePath)!;
            if (string.Equals(full, Path.GetFullPath(baselineDirectory), StringComparison.OrdinalIgnoreCase) || IsInside(full, baselineDirectory))
                throw new PlanRejectedException($"Output directory is inside the folder that holds baseline '{recording.RecordingId}'.");
            var videoDirectory = Path.GetDirectoryName(recording.VideoPath)!;
            if (string.Equals(full, Path.GetFullPath(videoDirectory), StringComparison.OrdinalIgnoreCase))
                throw new PlanRejectedException($"Output directory is the folder that holds video '{recording.RecordingId}'.");
        }
    }

    public static string CaseRoot(string output, string recordingId, string scenario) => Path.Combine(output, "cases", recordingId, scenario);
    public static string AttemptDirectory(string output, string recordingId, string scenario, int index) => Path.Combine(CaseRoot(output, recordingId, scenario), FormattableString.Invariant($"attempt-{index:0000}"));
    public static string RelativeAttempt(string recordingId, string scenario, int index) => FormattableString.Invariant($"cases/{recordingId}/{scenario}/attempt-{index:0000}");

    public static bool IsInside(string candidate, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectOverlap(string output, string inputFile, string label)
    {
        var input = Path.GetFullPath(inputFile);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase) || IsInside(input, output))
            throw new PlanRejectedException($"Output directory contains the {label}. It was left unchanged.");
    }
}

internal static class CancelWhen
{
    public static void EnsureValid(string? path)
    {
        var error = Read(path, completed: 0, out _);
        if (error is not null) throw new UsageException(error);
    }

    public static string? Read(string? path, int completed, out bool cancel)
    {
        cancel = false;
        if (path is null) return null;
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full)) return "The cancel-when path is a directory.";
        if (!File.Exists(full)) return null;
        var text = File.ReadAllText(full).Trim();
        if (text.Length == 0) { cancel = true; return null; }
        if (text.Contains('\n') || text.Contains('\r')) return "The cancel-when file must be empty or one line: after-completed=<nonnegative integer>.";
        const string prefix = "after-completed=";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0)
            return "The cancel-when file must be empty or one line: after-completed=<nonnegative integer>.";
        cancel = completed >= count;
        return null;
    }
}
