using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MouseTrackerTabajara.Application;

public static class StabilityReportWriter
{
    /// <summary>Aggregation-only replay of declared rows and receipt digests; does not authenticate endpoint scalars against producer algorithms.</summary>
    public static StabilityReport ReplayAggregation(string path, CancellationToken cancellation = default)
    {
        var saved = JsonSerializer.Deserialize<StabilityReport>(File.ReadAllText(path)) ?? throw new InvalidDataException("Unreadable saved stability material.");
        foreach (var input in saved.Inputs)
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input.Json))) != input.Sha256) throw new InvalidDataException("Frozen producer input bytes differ from their recorded digest.");
        return ResultStability.Build(saved.Trials, cancellation) with { Inputs = saved.Inputs };
    }
    public static StabilityReport Disclose(StabilityReport report, bool blinded)
    {
        if (!blinded) return report;
        var animals = report.Trials.Select(t => t.AnimalId).Distinct(StringComparer.Ordinal).Select((id, i) => (id, alias: $"Animal {i + 1:000}")).ToDictionary(x => x.id, x => x.alias, StringComparer.Ordinal);
        var trials = report.Trials.Select(t => t.TrialId).Distinct(StringComparer.Ordinal).Select((id, i) => (id, alias: $"Trial {i + 1:0000}")).ToDictionary(x => x.id, x => x.alias, StringComparer.Ordinal);
        var groups = report.Trials.Select(t => t.Group).Distinct(StringComparer.Ordinal).Select((id, i) => (id, alias: $"Allocation {i + 1:000}")).ToDictionary(x => x.id, x => x.alias, StringComparer.Ordinal);
        var scenarios = report.Trials.Select(t => t.Scenario).Distinct(StringComparer.Ordinal).Select((id, i) => (id, alias: $"Scenario {i + 1:000}")).ToDictionary(x => x.id, x => x.alias, StringComparer.Ordinal);
        var contexts = report.Trials.Select(t => t.Scope.Context).Distinct(StringComparer.Ordinal).Select((id, i) => (id, alias: $"Context {i + 1:000}")).ToDictionary(x => x.id, x => x.alias, StringComparer.Ordinal);
        StabilityScope SafeScope(StabilityScope scope) => scope with
        {
            Context = contexts[scope.Context], Phase = scope.Phase is "Probe" or "Training" or "probe" or "training" ? scope.Phase : "Frozen phase", Window = "Frozen comparison window",
            Orientation = scope.Orientation is "Room" or "TargetAligned" or "image coordinates" ? scope.Orientation : "Frozen orientation",
            Endpoint = scope.Endpoint is "platform_site_seconds" or "platform_site_seconds (raw decoder)" or "coverage_percent (raw decoder)" or "target_occupancy_percent" or "distance" or "first_site_visit_seconds" ? scope.Endpoint : "Conditional endpoint",
            Unit = scope.Unit is "s" or "seconds" or "cm" or "percent" or "frames" or "pool diameters" ? scope.Unit : "Frozen units"
        };
        var safe = report.Trials.Select((t, i) => t with
        {
            CaseId = $"Case {i + 1:0000}", TrialId = trials[t.TrialId], AnimalId = animals[t.AnimalId], Group = groups[t.Group], Scenario = scenarios[t.Scenario],
            DeclaredRange = "Private rationale withheld; numeric prespecified ranges shown separately.", Mode = t.Mode.Contains("native detector retracking", StringComparison.OrdinalIgnoreCase) ? "Native detector retracking; raw decoder metrics" : t.Supported ? "Frozen-coordinate conditional measurement" : "Unsupported biological endpoint; raw evidence retained",
            Scope = SafeScope(t.Scope),
            OriginalScope = null,
            ContributionNote = t.ContributionNote is "First accepted frame whose platform-site membership differs from the frozen baseline." or "No site-membership change. The first accepted frame remains inspectable." ? t.ContributionNote : t.ContributionNote.Length == 0 ? "" : "Producer-designated frozen frame; private diagnostic wording held.",
            NumericRanges = t.NumericRanges.Select(range => range with { Parameter = range.Parameter is "threshold" or "invert" or "blur" or "min_area" or "max_area" or "max_extension" or "max_jump" or "geometry_components" or "image_control" or "room_control" ? range.Parameter : "parameter", Unit = range.Unit is "source pixels" or "normalized source fractions" or "cm" or "count" or "boolean" or "grayscale" or "pixels" or "pixels per frame" or "pixel area" ? range.Unit : "declared units" }).ToImmutableArray(),
            BaselineIdentity = new("", "", "", "", "", "", "", ""), ScenarioIdentity = new("", "", "", "", "", "", "", ""), Trace = null,
            Holds = t.Holds.IsEmpty ? [] : ["Case held; identifying diagnostic material withheld."]
        }).ToImmutableArray();
        // Preserve computed eligibility and conclusions; redaction must not rerun validation against empty private identities.
        return new(safe, report.Contrasts.Select(c => c with
        {
            Scenario = scenarios[c.Scenario], GroupA = groups[c.GroupA], GroupB = groups[c.GroupB],
            Scope = SafeScope(c.Scope),
            Animals = c.Animals.Select(a => a with { AnimalId = animals[a.AnimalId], Group = groups[a.Group], Holds = a.Holds.IsEmpty ? [] : ["Animal held; private diagnostics withheld."] }).ToImmutableArray()
        }).ToImmutableArray()) { Envelopes = report.Envelopes.Select(e => e with { GroupA = groups[e.GroupA], GroupB = groups[e.GroupB], Scope = SafeScope(e.Scope), Contributors = e.Contributors.Select(s => scenarios[s]).ToImmutableArray(), HeldScenarios = e.HeldScenarios.Select(s => scenarios[s]).ToImmutableArray(), MembershipReferenceScenario = scenarios[e.MembershipReferenceScenario], MatchedAnimalIds = e.MatchedAnimalIds.Select(id => animals[id]).ToImmutableArray(), MembershipChangingScenarios = e.MembershipChangingScenarios.Select(s => scenarios[s]).ToImmutableArray() }).ToImmutableArray() };
    }

    public static void Save(string directory, StabilityReport report, bool blinded, CancellationToken cancellation = default, Action<string>? publicationStage = null)
    {
        var target = Path.GetFullPath(directory);
        // A report is a frozen publication; never replace a prior output.
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("Report destination is not empty; prior evidence remains unchanged.");
        Directory.CreateDirectory(target);
        cancellation.ThrowIfCancellationRequested(); publicationStage?.Invoke("prepared");
        // CreateNew is the publication ownership boundary across windows/processes. Retain its receipt even on failure.
        using var ownership = new FileStream(Path.Combine(target, "publication.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var receipt = new StreamWriter(ownership, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        receipt.WriteLine("started " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); ownership.Flush(true);
        try
        {
            publicationStage?.Invoke("claimed"); cancellation.ThrowIfCancellationRequested();
            var disclosed = Disclose(report, blinded);
            WriteNew("stability-input.json", JsonSerializer.Serialize(disclosed, new JsonSerializerOptions { WriteIndented = true })); publicationStage?.Invoke("input-written");
            WriteNew("report.html", Html(disclosed, blinded));
            var csv = new StringBuilder("case,animal,trial,allocation,scenario,mode,endpoint,units,original,scenario_value,lower,upper,held,reason,declared_ranges,analyzed_frames,planned_seconds,unknown_seconds,uncertainty\r\n");
            foreach (var t in disclosed.Trials)
            {
                cancellation.ThrowIfCancellationRequested();
                csv.AppendLine(string.Join(",", new[] { t.CaseId, t.AnimalId, t.TrialId, t.Group, t.Scenario, t.Mode, t.Scope.Endpoint, t.Scope.Unit,
                    N(t.Original.Value), N(t.Changed.Value), N(t.Changed.Lower), N(t.Changed.Upper), (!t.Holds.IsEmpty).ToString(), string.Join("; ", t.Holds), RangeText(t), t.AnalyzedFrames?.ToString(CultureInfo.InvariantCulture) ?? "withheld", N(t.PlannedSeconds), N(t.UnknownSeconds), "conditional missing-position bounds where supplied; sampling uncertainty not estimated" }.Select(Csv)));
            }
            WriteNew("trials.csv", csv.ToString());
            WriteNew("disclosure.txt", blinded ? "Blind export: private source identities/paths/allocation labels omitted. This redacted manifest cannot independently authenticate source material; audited full evidence export is required for that purpose." : "Full frozen trial material. StabilityReportWriter.ReplayAggregation checks embedded receipt digests and aggregates declared rows; it does not reconstruct or authenticate endpoint scalars. The accepted producer reconstruction route and source bytes are required for independent endpoint authentication.");
            receipt.WriteLine("completed " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); ownership.Flush(true);
        }
        catch (Exception exception)
        {
            receipt.WriteLine((exception is OperationCanceledException ? "cancelled " : "failed ") + exception.GetType().Name + " " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); ownership.Flush(true); throw;
        }
        void WriteNew(string name, string material)
        {
            cancellation.ThrowIfCancellationRequested();
            using var stream = new FileStream(Path.Combine(target, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true); writer.Write(material); writer.Flush(); stream.Flush(true);
        }
    }
    public static string Html(StabilityReport report, bool blinded)
    {
        var html = new StringBuilder("<!doctype html><html><meta charset='utf-8'><title>Result stability</title><style>body{font:16px Segoe UI,sans-serif;margin:2em}table{border-collapse:collapse;width:100%;margin:1em 0}td,th{border:1px solid #bbb;padding:.4em;text-align:left}pre{white-space:pre-wrap}section{margin:2em 0}</style><h1>Result stability</h1>");
        html.Append("<p>").Append(E(StabilityReport.Limits)).Append("</p><p>").Append(blinded ? "Blind disclosure. Exact source identifiers held; source pixels are not automatically redacted." : "Full evidence disclosure.").Append("</p>");
        foreach (var envelope in report.Envelopes) html.Append("<p>").Append(E($"Named-scenario contrast envelope: {envelope.GroupA} minus {envelope.GroupB}, {envelope.Scope.Endpoint}, [{N(envelope.Minimum)}, {N(envelope.Maximum)}] {envelope.Scope.Unit}. Membership reference: {envelope.MembershipReferenceScenario}; matched animal count {envelope.MatchedAnimalIds.Length}. Contributors: {string.Join(", ", envelope.Contributors)}. Held/outside: {string.Join(", ", envelope.HeldScenarios)}. Membership-changing scenarios outside this measurement envelope: {string.Join(", ", envelope.MembershipChangingScenarios)}. This range is distinct from missing-position bounds and sampling uncertainty.")).Append("</p>");
        foreach (var c in report.Contrasts)
        {
            html.Append("<section><h2>").Append(E(c.Scenario + " — " + c.Scope.Endpoint)).Append("</h2><p>").Append(E(c.Conclusion)).Append("</p><p>")
                .Append(E($"{c.GroupA} minus {c.GroupB}; animal means have equal weight. Included {c.IncludedAnimals}/{c.PlannedAnimals}; held {c.HeldAnimals}; excluded {c.ExcludedAnimals}. Original {N(c.Original.Value)} [{N(c.Original.Lower)}, {N(c.Original.Upper)}]; scenario {N(c.Changed.Value)} [{N(c.Changed.Lower)}, {N(c.Changed.Upper)}] {c.Scope.Unit}.")).Append("</p>")
                .Append("<table><tr><th>Animal</th><th>Allocation</th><th>Included/planned trials</th><th>Original</th><th>Scenario</th><th>Holds</th></tr>");
            foreach (var a in c.Animals) html.Append("<tr>").Append(Cells(a.AnimalId, a.Group, $"{a.IncludedTrials}/{a.PlannedTrials}", N(a.Original.Value), N(a.Changed.Value), string.Join("; ", a.Holds))).Append("</tr>");
            html.Append("</table></section>");
        }
        if (report.Contrasts.IsEmpty) html.Append("<p>Unsupported biological contrast: no matched supported allocation matrix. All raw/held cases remain below.</p>");
        html.Append("<h2>Every planned case and contributor</h2><table><tr><th>Case</th><th>Animal/trial</th><th>Scenario/mode</th><th>Endpoint/scope</th><th>Original</th><th>Scenario missing-position bounds</th><th>Declared range</th><th>Holds</th><th>Frozen trace</th></tr>");
        foreach (var t in report.Trials) html.Append("<tr>").Append(Cells(t.CaseId, t.AnimalId + " / " + t.TrialId, t.Scenario + " / " + t.Mode, t.Scope.ToString() + $"; denominator {t.AnalyzedFrames?.ToString() ?? "withheld"} frames / {N(t.PlannedSeconds)} seconds; unknown {N(t.UnknownSeconds)} seconds; sampling uncertainty not estimated", N(t.Original.Value), $"{N(t.Changed.Value)} [{N(t.Changed.Lower)}, {N(t.Changed.Upper)}]", t.DeclaredRange + "; " + RangeText(t), string.Join("; ", t.Holds), t.Trace?.ToString() ?? "Trace held/omitted")).Append("</tr>");
        return html.Append("</table></html>").ToString();
    }
    private static string Cells(params string[] cells) => string.Join("", cells.Select(v => "<td>" + E(v) + "</td>"));
    public static string RangeText(StabilityTrial trial) => string.Join("; ", trial.NumericRanges.Select(r => r.Parameter + " [" + N(r.Minimum) + ", " + N(r.Maximum) + "] " + r.Unit));
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string N(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) ?? "withheld";
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
