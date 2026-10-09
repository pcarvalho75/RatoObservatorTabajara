using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MouseTrackerTabajara.Core;

/// <summary>Plain descriptions of trial measurements the protocol label does not supply.</summary>
public static class TrialMeasurementWording
{
    public const string ProtocolLabelDoesNotSupply = "The protocol label does not supply confirmed trial path, speed, occupancy, or first site visit.";
    public const string AlignedRawRun = "The detector interval meets the confirmed biological window. No separate measurement of the planned trial was computed.";
}

/// <summary>Classified study secrets. Short animal and group labels are not treated like long file names.</summary>
public sealed class BlindSecrets
{
    public List<string> Paths { get; } = [];
    public List<string> FileNames { get; } = [];
    public List<string> Hashes { get; } = [];
    public List<string> AnimalIds { get; } = [];
    public List<string> Groups { get; } = [];

    public static BlindSecrets FromAssets(IEnumerable<VideoAsset> assets)
    {
        var secrets = new BlindSecrets();
        foreach (var asset in assets)
        {
            Add(secrets.Paths, asset.Path);
            Add(secrets.FileNames, asset.Filename);
            Add(secrets.FileNames, string.IsNullOrWhiteSpace(asset.Path) ? "" : Path.GetFileNameWithoutExtension(asset.Path));
            Add(secrets.Hashes, asset.SourceSha256);
            Add(secrets.AnimalIds, asset.AnimalId);
            Add(secrets.Groups, asset.GroupName);
        }
        return secrets;
    }

    private static void Add(List<string> list, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) list.Add(value.Trim());
    }
}

/// <summary>Software-label blinding. The recording picture is not redacted.</summary>
public static class BlindIdentity
{
    public const string PixelNote = "Blind review hides software labels, file names, animal IDs, and groups. The video picture is not redacted and may still show identifying text.";
    public const string GenericRejection = "The action was rejected. Identifying details stay in the study record.";
    public const string ReasonWithheld = "The reason is stored with the study and is not shown during blind review.";
    public const string AnnotationWithheld = "Annotation text is hidden during blind review.";
    public const string DetailsHidden = "Details are hidden during blind review.";

    private static readonly Regex RecordingAliasPattern = new("^Recording [0-9A-F]{8}$", RegexOptions.CultureInvariant);
    private static readonly Regex AnimalAliasPattern = new("^Animal [0-9A-F]{8}$", RegexOptions.CultureInvariant);
    private static readonly Regex TokenPattern = new("[A-Za-z0-9]+", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ProtectedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "probe", "training", "unspecified", "included", "excluded", "recording", "animal", "frame", "review",
        "hidden", "phase", "trial", "window", "release", "protocol", "coverage", "unknown", "ambiguous",
        "platform", "water", "entry", "mounting", "visit", "site", "group", "blind", "error", "study"
    };
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "of", "to", "in", "on", "or", "is", "be", "at", "by", "we", "it", "as", "an", "the", "and", "for", "not", "are"
    };

    public static string RecordingAlias(VideoAsset asset) =>
        IsPersistedRecordingAlias(asset.BlindedAlias, asset) ? asset.BlindedAlias.Trim() : "Recording";

    public static string AnimalAlias(VideoAsset asset)
    {
        if (!asset.AnimalIdConfirmed || string.IsNullOrWhiteSpace(asset.AnimalId)) return "Animal unconfirmed";
        return IsPersistedAnimalAlias(asset.BlindedAnimalAlias, asset) ? asset.BlindedAnimalAlias.Trim() : "Animal";
    }

    public static bool IsPersistedRecordingAlias(string? alias, VideoAsset asset) =>
        IsGenerated(alias, RecordingAliasPattern) && !EmbedsIdentity(alias!.Trim(), asset);

    public static bool IsPersistedAnimalAlias(string? alias, VideoAsset asset) =>
        IsGenerated(alias, AnimalAliasPattern) && !EmbedsIdentity(alias!.Trim(), asset);

    public static string CreateRecordingAlias(VideoAsset asset) => Create("Recording ", asset, IsPersistedRecordingAlias);

    public static string CreateAnimalAlias(VideoAsset asset) => Create("Animal ", asset, IsPersistedAnimalAlias);

    public static string Scrub(string text, BlindSecrets secrets)
    {
        if (string.IsNullOrEmpty(text) || secrets is null) return text;
        var result = text;
        foreach (var secret in DistinctiveSecrets(secrets))
            result = result.Replace(secret, "hidden", StringComparison.OrdinalIgnoreCase);
        foreach (var secret in IdentitySecrets(secrets))
            result = ReplaceIdentityTokens(result, secret);
        return result;
    }

    public static string ProjectAuthored(string? text, BlindSecrets secrets)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // Authored notes can use an animal ID as a sentence's subject. The article
        // exception that keeps system prose readable must not reveal that subject.
        var shortIds = secrets.AnimalIds.Concat(secrets.Groups).Where(value => value.Trim().Length is >= 1 and <= 3).ToArray();
        if (TokenPattern.Matches(text).Any(token => shortIds.Any(id => string.Equals(token.Value, id.Trim(), StringComparison.OrdinalIgnoreCase))))
            return ReasonWithheld;
        if (ContainsShortIdentity(text, secrets) || !string.Equals(Scrub(text, secrets), text, StringComparison.Ordinal))
            return ReasonWithheld;
        return text.Trim();
    }

    public static bool ContainsShortIdentity(string? text, BlindSecrets secrets)
    {
        if (string.IsNullOrEmpty(text) || secrets is null) return false;
        foreach (var secret in secrets.AnimalIds.Concat(secrets.Groups))
        {
            if (!IsScrubbableIdentity(secret)) continue;
            foreach (Match match in TokenPattern.Matches(text))
                if (TokenCarries(text, match, secret.Trim())) return true;
        }
        return false;
    }

    public static string PresentStatus(string text, BlindSecrets secrets)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (ContainsShortIdentity(text, secrets)) return GenericRejection;
        return Scrub(text, secrets);
    }

    public static string PresentSurface(string text, BlindSecrets secrets)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (ContainsShortIdentity(text, secrets)) return DetailsHidden;
        var scrubbed = Scrub(text, secrets);
        return string.Equals(scrubbed, text, StringComparison.Ordinal) ? text : scrubbed;
    }

    private static IEnumerable<string> DistinctiveSecrets(BlindSecrets secrets) =>
        secrets.Paths.Concat(secrets.FileNames).Concat(secrets.Hashes).Concat(secrets.AnimalIds).Concat(secrets.Groups)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Where(IsDistinctiveSecret)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Length);

    private static IEnumerable<string> IdentitySecrets(BlindSecrets secrets) =>
        secrets.AnimalIds.Concat(secrets.Groups)
            .Where(IsScrubbableIdentity)
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Length);

    private static bool IsDistinctiveSecret(string value) =>
        value.Length >= 4 && !ProtectedWords.Contains(value) && !StopWords.Contains(value)
        && (value.Length >= 12 || value.Contains('_') || value.Contains('\\') || value.Contains('/') || value.Contains('.') || IsHex(value));

    private static bool IsScrubbableIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (ProtectedWords.Contains(trimmed) || StopWords.Contains(trimmed)) return false;
        return trimmed.Length is >= 1 and <= 24;
    }

    private static bool IsHex(string value) => value.Length >= 32 && value.All(Uri.IsHexDigit);

    private static string ReplaceIdentityTokens(string text, string secret) =>
        TokenPattern.Replace(text, match => TokenCarries(text, match, secret) ? "hidden" : match.Value);

    private static bool TokenCarries(string text, Match match, string secret)
    {
        var token = match.Value;
        if (secret.Length == 1)
        {
            if (token.Length > 1 && token.StartsWith(secret, StringComparison.Ordinal) && token[1..].All(char.IsDigit)) return true;
            if (!string.Equals(token, secret, StringComparison.Ordinal)) return false;
            return !IsOrdinarySentenceStart(text, match.Index);
        }
        if (token.Length > secret.Length && token.StartsWith(secret, StringComparison.OrdinalIgnoreCase) && token[secret.Length..].All(char.IsDigit))
            return true;
        return string.Equals(token, secret, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOrdinarySentenceStart(string text, int index)
    {
        var before = text[..index].TrimEnd();
        if (before.Length > 0 && before[^1] is not ('.' or '!' or '?' or '\n' or ':')) return false;
        var after = index + 1;
        while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
        return after < text.Length && char.IsLower(text[after]);
    }

    private static bool EmbedsIdentity(string alias, VideoAsset asset)
    {
        // Generic display prefixes are not identifying material. Otherwise an
        // animal literally named "animal" would make every random alias fail.
        var identityMaterial = alias.StartsWith("Recording ", StringComparison.Ordinal) ? alias[10..]
            : alias.StartsWith("Animal ", StringComparison.Ordinal) ? alias[7..] : alias;
        foreach (var secret in new[] { asset.AnimalId, asset.GroupName, asset.Filename, asset.Path, Path.GetFileNameWithoutExtension(asset.Path), asset.SourceSha256 })
        {
            if (string.IsNullOrWhiteSpace(secret)) continue;
            var trimmed = secret.Trim();
            if (trimmed.Length >= 4 && identityMaterial.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.Length is >= 1 and <= 3)
            {
                foreach (Match match in TokenPattern.Matches(identityMaterial))
                    if (TokenCarries(identityMaterial, match, trimmed)) return true;
            }
        }
        return false;
    }

    private static bool IsGenerated(string? alias, Regex pattern) =>
        !string.IsNullOrWhiteSpace(alias) && pattern.IsMatch(alias.Trim());

    private static string Create(string prefix, VideoAsset asset, Func<string, VideoAsset, bool> accept)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var alias = prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            if (accept(alias, asset)) return alias;
        }
        throw new InvalidOperationException("A safe study alias could not be stored.");
    }
}

/// <summary>The viewer frame is not a release. Confirmation uses only an explicit anchor.</summary>
public static class ReleaseAnchorChoice
{
    public static int? ForSave(bool releaseConfirmed, int? draftFrame)
    {
        if (releaseConfirmed && draftFrame is null)
            throw new ArgumentException("Set the release from the completed displayed frame before confirming it. The viewer position is not the release.");
        return draftFrame;
    }
}

/// <summary>Evidence around the accepted consequence queue. Ranking itself stays in that queue.</summary>
public sealed record ReviewRankingEvidence
{
    public const string Version = "c6-consequence-v1";
    public string VersionName { get; init; } = Version;
    public string SeedMaterial { get; init; } = "";
    public IReadOnlyList<ReviewMoment> Moments { get; init; } = [];

    public static ReviewRankingEvidence Capture(AnalysisRun run)
    {
        var moments = MetricCalculator.ReviewQueue(run);
        return new() { VersionName = Version, SeedMaterial = SeedMaterialOf(run.Points), Moments = moments };
    }

    public static string SeedMaterialOf(IReadOnlyList<TrackPoint> points)
    {
        var builder = new StringBuilder();
        foreach (var point in points)
        {
            builder.Append(point.Frame.ToString(CultureInfo.InvariantCulture));
            builder.Append('\t');
            builder.Append(point.X?.ToString(CultureInfo.InvariantCulture) ?? "");
            builder.Append('\t');
            builder.Append(point.Y?.ToString(CultureInfo.InvariantCulture) ?? "");
            builder.Append('\t');
            builder.Append(((int)point.Status).ToString(CultureInfo.InvariantCulture));
            builder.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

public sealed record InclusionDecision
{
    public string VideoId { get; init; } = "";
    public bool Included { get; init; }
    public string Reason { get; init; } = "";
    public string Reviewer { get; init; } = "";
    public int Revision { get; init; }
    public DateTime CreatedUtc { get; init; }
}

public sealed record MomentInspection
{
    public string VideoId { get; init; } = "";
    public string? RunId { get; init; }
    public int StartFrame { get; init; }
    public int EndFrame { get; init; }
    public int DisplayedFrame { get; init; }
    public int QueueStartFrame { get; init; }
    public int QueueEndFrame { get; init; }
    public string Reason { get; init; } = "";
    public string Reviewer { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public string CorrectionFingerprint { get; init; } = "";
    public string ReviewFingerprint { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
}

/// <summary>What the reviewer saw. The store rejects this when it no longer matches the stored run.</summary>
public sealed record InspectionAttestation
{
    public string RunId { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public string CorrectionFingerprint { get; init; } = "";
    public string ReviewFingerprint { get; init; } = "";
    public int DisplayedFrame { get; init; }
    public int QueueStartFrame { get; init; }
    public int QueueEndFrame { get; init; }
    public string Reason { get; init; } = "";
    public string Reviewer { get; init; } = "";
}

public sealed record InspectionMaterial(string RunId, string VideoId, string SourceSha256, string CorrectionFingerprint, string ReviewFingerprint);

/// <summary>A seek request. It does not assert that a source frame was displayed or inspected.</summary>
public sealed record NavigationReceipt
{
    public string VideoId { get; init; } = "";
    public string? RunId { get; init; }
    public int RequestedFrame { get; init; }
    public int? MomentStartFrame { get; init; }
    public int? MomentEndFrame { get; init; }
    public bool DisplayCompleted { get; init; }
    public DateTime CreatedUtc { get; init; }
}

public sealed record UnblindingRecord
{
    public string Reviewer { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
}

public sealed record RankingSnapshot
{
    public string RunId { get; init; } = "";
    public int Revision { get; init; }
    public string Version { get; init; } = "";
    public string SeedMaterial { get; init; } = "";
    public string CorrectionFingerprint { get; init; } = "";
    public List<ReviewMoment> Moments { get; init; } = [];
    public DateTime CreatedUtc { get; init; }
}

public sealed record BlindedMoment(int StartFrame, int EndFrame, string Reason, bool RandomAudit);
public sealed record BlindedInspection(int StartFrame, int EndFrame, string Reason, string Reviewer);

/// <summary>Allowlisted blinded review projection. Source identity, allocation, and raw point fingerprints stay out.</summary>
public sealed record BlindedReviewExport
{
    public string RecordingAlias { get; init; } = "";
    public string AnimalAlias { get; init; } = "";
    public string Phase { get; init; } = "";
    public int Day { get; init; }
    public int Trial { get; init; }
    public bool Included { get; init; }
    public string InclusionReason { get; init; } = "";
    public bool ReviewConfirmed { get; init; }
    public bool ReviewStale { get; init; }
    public string RankingVersion { get; init; } = "";
    public List<BlindedMoment> Moments { get; init; } = [];
    public List<BlindedInspection> Inspections { get; init; } = [];
    public string PixelNote { get; init; } = BlindIdentity.PixelNote;
    public string MeasurementNote { get; init; } = TrialMeasurementWording.ProtocolLabelDoesNotSupply;
}

public static class ReviewExportWriter
{
    public static void Save(string folder, BlindedReviewExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        Directory.CreateDirectory(folder);
        var json = JsonDefaults.Serialize(export);
        File.WriteAllText(Path.Combine(folder, "blind-review.json"), json);
        File.WriteAllText(Path.Combine(folder, "blind-review.txt"), Visible(export));
    }

    public static string Visible(BlindedReviewExport export)
    {
        var lines = new List<string>
        {
            export.RecordingAlias,
            export.AnimalAlias,
            "Phase " + export.Phase,
            "Day " + export.Day.ToString(CultureInfo.InvariantCulture),
            "Trial " + export.Trial.ToString(CultureInfo.InvariantCulture),
            export.Included ? "Included" : "Excluded",
            export.InclusionReason,
            export.ReviewConfirmed ? "Tracking review confirmed" : export.ReviewStale ? "Tracking review stale" : "Tracking review pending",
            "Ranking " + export.RankingVersion,
            export.PixelNote,
            export.MeasurementNote,
            "Coverage is not accuracy. A review decision does not change the planned window."
        };
        foreach (var moment in export.Moments)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Moment {moment.StartFrame}-{moment.EndFrame} audit={moment.RandomAudit} {moment.Reason}"));
        foreach (var inspection in export.Inspections)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Inspected {inspection.StartFrame}-{inspection.EndFrame} {inspection.Reason} {inspection.Reviewer}"));
        return string.Join(Environment.NewLine, lines);
    }
}
