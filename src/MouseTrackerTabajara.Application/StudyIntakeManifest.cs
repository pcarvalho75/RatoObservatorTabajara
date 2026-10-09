using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Application;

/// <summary>One protocol day: its plan context id, analysis phase and expected trials per animal.</summary>
public sealed record StudyIntakeStage(int Day, string ContextId, TrialMode Phase, int Trials);

/// <summary>Human-confirmed protocol structure used to build expected slots. It never supplies animals, durations or confirmations.</summary>
public sealed record StudyIntakeProtocol(string Name, ImmutableArray<string> Groups, ImmutableArray<StudyIntakeStage> Stages)
{
    public int ExpectedSlotsPerAnimal => Stages.Sum(s => s.Trials);
    public StudyIntakeStage? Stage(int day) => Stages.FirstOrDefault(s => s.Day == day);
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Groups.IsDefaultOrEmpty || Stages.IsDefaultOrEmpty) throw new ArgumentException("Intake protocol needs a name, groups and stages.");
        if (Groups.Any(string.IsNullOrWhiteSpace) || Groups.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Groups.Length) throw new ArgumentException("Intake protocol groups are blank or duplicated.");
        if (Stages.Select(s => s.Day).Distinct().Count() != Stages.Length || Stages.Select(s => s.ContextId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Stages.Length)
            throw new ArgumentException("Intake protocol days/contexts are duplicated.");
        if (Stages.Any(s => s.Day < 1 || s.Trials < 1 || string.IsNullOrWhiteSpace(s.ContextId) || s.Phase == TrialMode.Unspecified || !Enum.IsDefined(s.Phase)))
            throw new ArgumentException("Intake protocol stages need a positive day, trials, context and phase.");
    }
}

public sealed record StudyIntakeRow(int Line, string AnimalId, string Group, int Day, int Trial, string VideoPath, string Alias, string Cage, string Litter);

/// <summary>A preview finding. Blocking findings prevent apply; warnings stay visible after apply.</summary>
public sealed record StudyIntakeIssue(string Code, string Message, ImmutableArray<int> Lines, bool Blocking = true)
{
    public string LineLabel => Lines.IsDefaultOrEmpty ? "manifest" : (Lines.Length == 1 ? "line " : "lines ") + string.Join(", ", Lines);
}

/// <summary>Typed CSV manifest: animal_id, group, day, trial, video_path (+ optional alias, cage, litter). UTF-8 only.</summary>
public sealed record StudyIntakeManifest(string Path, string Sha256, ImmutableArray<StudyIntakeRow> Rows, ImmutableArray<StudyIntakeIssue> Issues)
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    public const int MaximumRows = 10000;
    public static ImmutableArray<string> RequiredColumns { get; } = ["animal_id", "group", "day", "trial", "video_path"];
    public static ImmutableArray<string> OptionalColumns { get; } = ["alias", "cage", "litter"];

    public static StudyIntakeManifest Read(string path, StudyIntakeProtocol protocol)
    {
        var full = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("Manifest file not found.", full);
        if (info.Length > MaximumBytes) throw new InvalidDataException("Manifest exceeds 32 MB; split the study into smaller manifests.");
        return Parse(File.ReadAllBytes(full), full, protocol);
    }

    public static StudyIntakeManifest Parse(byte[] bytes, string manifestPath, StudyIntakeProtocol protocol)
    {
        protocol.Validate();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var issues = ImmutableArray.CreateBuilder<StudyIntakeIssue>();
        StudyIntakeManifest Fail(string code, string message) => new(manifestPath, sha, [], [new(code, message, [])]);
        if (bytes.Length > MaximumBytes) return Fail("manifest-too-large", "Manifest exceeds 32 MB; split the study into smaller manifests.");
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
            return Fail("manifest-encoding", "Manifest is UTF-16. Save it as UTF-8 CSV and preview again.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Fail("manifest-encoding", "Manifest is not valid UTF-8. Save it as UTF-8 CSV and preview again."); }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var baseDirectory = System.IO.Path.GetDirectoryName(manifestPath) ?? Environment.CurrentDirectory;

        var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        Dictionary<string, int>? columns = null; var rows = ImmutableArray.CreateBuilder<StudyIntakeRow>();
        for (var index = 0; index < lines.Length; index++)
        {
            var number = index + 1; var line = lines[index];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!TrySplit(line, out var fields, out var error)) { issues.Add(new("malformed-row", $"Line {number}: {error}", [number])); if (columns is null) break; continue; }
            if (columns is null)
            {
                columns = new(StringComparer.OrdinalIgnoreCase);
                for (var c = 0; c < fields.Count; c++)
                {
                    var name = fields[c].Trim();
                    if (!RequiredColumns.Contains(name, StringComparer.OrdinalIgnoreCase) && !OptionalColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
                        issues.Add(new("unknown-column", $"Header column '{name}' is not recognised. Use {string.Join(", ", RequiredColumns.Concat(OptionalColumns))}; unknown columns are never ignored silently.", [number]));
                    else if (!columns.TryAdd(name, c)) issues.Add(new("duplicate-column", $"Header column '{name}' appears twice.", [number]));
                }
                foreach (var required in RequiredColumns.Where(r => !columns.ContainsKey(r)))
                    issues.Add(new("missing-column", $"Header lacks required column '{required}'.", [number]));
                if (issues.Count > 0) break;
                continue;
            }
            if (fields.Count != columns.Count) { issues.Add(new("malformed-row", $"Line {number}: expected {columns.Count} fields, found {fields.Count}. Quote values containing commas.", [number])); continue; }
            if (rows.Count >= MaximumRows) { issues.Add(new("manifest-too-large", $"Manifest has more than {MaximumRows} rows; split it.", [number])); break; }
            string Field(string name) => columns.TryGetValue(name, out var at) ? fields[at].Trim() : "";
            var row = ValidateRow(number, Field("animal_id"), Field("group"), Field("day"), Field("trial"), Field("video_path"), Field("alias"), Field("cage"), Field("litter"), baseDirectory, protocol, issues);
            if (row is not null) rows.Add(row);
        }
        if (columns is null && issues.Count == 0) issues.Add(new("empty-manifest", "Manifest has no header or rows.", []));
        else if (columns is not null && issues.Count == 0 && rows.Count == 0) issues.Add(new("empty-manifest", "Manifest has a header but no trial rows.", []));
        CrossRowChecks(rows, issues);
        return new(manifestPath, sha, rows.ToImmutable(), issues.ToImmutable());
    }

    private static StudyIntakeRow? ValidateRow(int line, string animal, string group, string dayText, string trialText, string video, string alias, string cage, string litter,
        string baseDirectory, StudyIntakeProtocol protocol, ImmutableArray<StudyIntakeIssue>.Builder issues)
    {
        var start = issues.Count;
        if (animal.Length == 0) issues.Add(new("missing-animal", $"Line {line}: animal_id is blank. Identity is never inferred from file names.", [line]));
        else if (animal.Length > 200 || animal.Any(char.IsControl)) issues.Add(new("invalid-animal", $"Line {line}: animal_id is too long or contains control characters.", [line]));
        var canonicalGroup = protocol.Groups.FirstOrDefault(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
        if (canonicalGroup is null) issues.Add(new("invalid-group", $"Line {line}: group '{group}' is not one of: {string.Join("; ", protocol.Groups)}.", [line]));
        var dayOk = int.TryParse(dayText, NumberStyles.None, CultureInfo.InvariantCulture, out var day);
        var trialOk = int.TryParse(trialText, NumberStyles.None, CultureInfo.InvariantCulture, out var trial);
        var stage = dayOk ? protocol.Stage(day) : null;
        if (!dayOk || stage is null) issues.Add(new("invalid-day", $"Line {line}: day '{dayText}' must be a whole number {protocol.Stages.Min(s => s.Day)}–{protocol.Stages.Max(s => s.Day)}.", [line]));
        else if (!trialOk || trial < 1 || trial > stage.Trials)
            issues.Add(new("invalid-trial", $"Line {line}: day {day} ({stage.Phase}) has trial{(stage.Trials == 1 ? " 1 only" : "s 1–" + stage.Trials)}; '{trialText}' is invalid.", [line]));
        if (alias.Length > 200 || cage.Length > 200 || litter.Length > 200 || alias.Any(char.IsControl) || cage.Any(char.IsControl) || litter.Any(char.IsControl))
            issues.Add(new("invalid-optional", $"Line {line}: alias/cage/litter is too long or contains control characters.", [line]));
        string full = "";
        if (video.Length == 0) issues.Add(new("missing-path", $"Line {line}: video_path is blank.", [line]));
        else
        {
            try { full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(video) ? video : System.IO.Path.Combine(baseDirectory, video)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { issues.Add(new("invalid-path", $"Line {line}: video_path is not a valid Windows path.", [line])); }
        }
        return issues.Count == start ? new(line, animal, canonicalGroup!, day, trial, full, alias, cage, litter) : null;
    }

    private static void CrossRowChecks(ImmutableArray<StudyIntakeRow>.Builder rows, ImmutableArray<StudyIntakeIssue>.Builder issues)
    {
        foreach (var slot in rows.GroupBy(r => (Animal: r.AnimalId.ToUpperInvariant(), r.Day, r.Trial)).Where(g => g.Count() > 1))
            issues.Add(new("duplicate-slot", $"Rows map animal {slot.First().AnimalId} day {slot.Key.Day} trial {slot.Key.Trial} more than once. Keep exactly one row per trial.", Lines(slot)));
        foreach (var file in rows.GroupBy(r => r.VideoPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new("duplicate-file", $"The same video file is listed {file.Count()} times. One video is one trial.", Lines(file)));
        foreach (var animal in rows.GroupBy(r => r.AnimalId, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (label, select) in new (string, Func<StudyIntakeRow, string>)[] { ("group", r => r.Group), ("alias", r => r.Alias), ("cage", r => r.Cage), ("litter", r => r.Litter) })
            {
                var values = animal.Select(select).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                if (values.Length > 1) issues.Add(new("animal-inconsistent", $"Animal {animal.First().AnimalId}: rows disagree on {label} ({string.Join(" vs ", values)}). Different animals are never combined.", Lines(animal)));
            }
            var spellings = animal.Select(r => r.AnimalId).Distinct(StringComparer.Ordinal).ToArray();
            if (spellings.Length > 1) issues.Add(new("animal-ambiguous", $"Animal IDs {string.Join(" / ", spellings)} differ only by letter case. Use one spelling.", Lines(animal)));
        }
    }
    private static ImmutableArray<int> Lines(IEnumerable<StudyIntakeRow> rows) => rows.Select(r => r.Line).Order().ToImmutableArray();

    /// <summary>RFC 4180 fields on one physical line. Quoted fields may contain commas and doubled quotes, never line breaks.</summary>
    internal static bool TrySplit(string line, out List<string> fields, out string error)
    {
        fields = []; error = ""; var current = new StringBuilder(); var i = 0;
        while (true)
        {
            current.Clear();
            if (i < line.Length && line[i] == '"')
            {
                i++; var closed = false;
                while (i < line.Length)
                {
                    if (line[i] == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i += 2; continue; } i++; closed = true; break; }
                    current.Append(line[i++]);
                }
                if (!closed) { error = "unterminated quoted value (quoted values cannot span lines)."; return false; }
                if (i < line.Length && line[i] != ',') { error = "text follows a closing quote; double embedded quotes as \"\"."; return false; }
            }
            else
            {
                while (i < line.Length && line[i] != ',')
                {
                    if (line[i] == '"') { error = "quote inside an unquoted value; quote the whole value."; return false; }
                    current.Append(line[i++]);
                }
            }
            fields.Add(current.ToString());
            if (i >= line.Length) return true;
            i++; // comma
            if (i == line.Length) { fields.Add(""); return true; }
        }
    }
}
