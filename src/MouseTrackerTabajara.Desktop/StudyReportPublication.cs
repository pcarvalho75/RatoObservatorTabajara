using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

public sealed record StudyReportReceipt(string State, DateTime CreatedUtc, string SnapshotRevision, string Detail);
public sealed record StudyReportManifest(string SnapshotRevision, string CodeReceipt, bool Redacted, ImmutableDictionary<string, string> FileHashes);
public sealed record PublishedStudyReport(StudyReportDocument Document, StudyReportMaterial? Material, StudyReportManifest Manifest);

/// <summary>Exclusive, no-overwrite publication. Completion is an immutable receipt after all durable payloads and manifest.</summary>
public static class StudyReportPublication
{
    public static void Publish(string folder, StudyReportDocument document, StudyReportMaterial? material, CancellationToken cancellation = default,
        Action<string>? afterPayload = null, Action<Action>? completionGuard = null, Action? beforePayload = null)
    {
        folder = Path.GetFullPath(folder); Directory.CreateDirectory(folder);
        using var ownership = new FileStream(Path.Combine(folder, "publication.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        WriteReceipt(folder, "000-started.json", new("started", DateTime.UtcNow, document.SnapshotRevision, "Exclusive local publication started; partial files are evidence, not completion."));
        try
        {
            beforePayload?.Invoke(); cancellation.ThrowIfCancellationRequested();
            if (!document.Redacted && (material is null || material.SnapshotRevision != document.SnapshotRevision || material.CodeReceipt != document.CodeReceipt)) throw new InvalidDataException("Full frozen input required.");
            if (document.Redacted && material is not null) throw new InvalidDataException("Redacted publication cannot include private frozen material.");
            var payloads = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["report.json"] = JsonDefaults.Serialize(document), ["study.html"] = Html(document), ["temporal.csv"] = Csv(document),
                ["frozen-input.json"] = material is null ? JsonDefaults.Serialize(new { document.SnapshotRevision, document.CodeReceipt, Omitted = document.ReplayMode }) : JsonDefaults.Serialize(material)
            };
            var hashes = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach(var extra in ReportQuestionExport.Payloads(document))payloads.Add(extra.Key,extra.Value);
            foreach (var (name, content) in payloads)
            {
                cancellation.ThrowIfCancellationRequested(); WriteNew(Path.Combine(folder, name), content); hashes.Add(name, StudyReportMaterial.Hash(content)); afterPayload?.Invoke(name);
            }
            cancellation.ThrowIfCancellationRequested(); WriteNew(Path.Combine(folder, "manifest.json"), JsonDefaults.Serialize(new StudyReportManifest(document.SnapshotRevision, document.CodeReceipt, document.Redacted, hashes.ToImmutable()))); afterPayload?.Invoke("manifest.json");
            void Complete() { cancellation.ThrowIfCancellationRequested(); WriteReceipt(folder, "001-completed.json", new("completed", DateTime.UtcNow, document.SnapshotRevision, "All payloads durable; frozen input and explicit complete/partial source authentication recorded.")); }
            if (completionGuard is null) Complete(); else completionGuard(Complete);
        }
        catch (OperationCanceledException) { WriteReceipt(folder, "001-cancelled.json", new("cancelled", DateTime.UtcNow, document.SnapshotRevision, "Cancelled before completed receipt; partial evidence retained.")); throw; }
        catch (Exception) { WriteReceipt(folder, "001-failed.json", new("failed", DateTime.UtcNow, document.SnapshotRevision, "Failed before completed receipt; partial evidence retained.")); throw; }
    }
    public static string Recover(string folder)
    {
        folder = Path.GetFullPath(folder);
        using var ownership = new FileStream(Path.Combine(folder, "publication.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(Path.Combine(folder, "001-completed.json"))) { _ = Read(folder); return "Completed package verified; historical evidence retained."; }
        var receipts = Directory.GetFiles(folder, "*-*.json").Where(p => Path.GetFileName(p).Contains("failed", StringComparison.Ordinal) || Path.GetFileName(p).Contains("cancelled", StringComparison.Ordinal)).ToArray();
        if (receipts.Length > 0) return "Failed/cancelled partial publication retained; choose a new destination to publish again.";
        var startPath = Path.Combine(folder, "000-started.json");
        StudyReportReceipt start;
        try { start = JsonDefaults.Deserialize<StudyReportReceipt>(File.ReadAllText(startPath)); if (start.State != "started" || string.IsNullOrWhiteSpace(start.SnapshotRevision)) throw new InvalidDataException("Started receipt malformed."); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { start = new("interrupted", DateTime.UtcNow, "unknown", "Ownership acquired but started receipt was absent/unreadable; all bytes retained."); }
        var recovery = Path.Combine(folder, "002-interrupted.json");
        if (!File.Exists(recovery)) WriteReceipt(folder, "002-interrupted.json", new("interrupted", DateTime.UtcNow, start.SnapshotRevision, "Recovered abandoned ownership; no completed receipt. Partial evidence retained without promoting completion."));
        return "Interrupted publication retained; choose a new destination. No complete report declared.";
    }
    public static PublishedStudyReport Read(string folder)
    {
        folder = Path.GetFullPath(folder);
        var complete = JsonDefaults.Deserialize<StudyReportReceipt>(File.ReadAllText(Path.Combine(folder, "001-completed.json")));
        var started = JsonDefaults.Deserialize<StudyReportReceipt>(File.ReadAllText(Path.Combine(folder, "000-started.json")));
        var manifest = JsonDefaults.Deserialize<StudyReportManifest>(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        if (started.State != "started" || started.SnapshotRevision != manifest.SnapshotRevision || complete.State != "completed" || complete.SnapshotRevision != manifest.SnapshotRevision ||
            !new[] { "report.json", "study.html", "temporal.csv", "frozen-input.json" }.All(manifest.FileHashes.ContainsKey)) throw new InvalidDataException("Publication receipt/manifest invalid.");
        if(manifest.FileHashes.Keys.Any(name=>Path.GetFileName(name)!=name||name.Contains(':')))throw new InvalidDataException("Publication payload path is not local/allowlisted.");
        foreach (var (file, hash) in manifest.FileHashes) if (StudyReportMaterial.Hash(File.ReadAllText(Path.Combine(folder, file))) != hash) throw new InvalidDataException("Published payload was altered: " + file);
        var report = JsonDefaults.Deserialize<StudyReportDocument>(File.ReadAllText(Path.Combine(folder, "report.json")));
        var expectedFiles=new HashSet<string>(ReportQuestionExport.Payloads(report).Keys,StringComparer.Ordinal){"report.json","study.html","temporal.csv","frozen-input.json"};
        if(!expectedFiles.SetEquals(manifest.FileHashes.Keys))throw new InvalidDataException("Publication question payload inventory differs; legacy reports require only their original payloads.");
        var material = manifest.Redacted ? null : JsonDefaults.Deserialize<StudyReportMaterial>(File.ReadAllText(Path.Combine(folder, "frozen-input.json")));
        if (report.SnapshotRevision != manifest.SnapshotRevision || report.CodeReceipt != manifest.CodeReceipt || report.Redacted != manifest.Redacted ||
            material is not null && (material.SnapshotRevision != report.SnapshotRevision || material.CodeReceipt != report.CodeReceipt)) throw new InvalidDataException("Publication identities differ.");
        return new(report, material, manifest);
    }
    private static void WriteReceipt(string folder, string name, StudyReportReceipt receipt) => WriteNew(Path.Combine(folder, name), JsonDefaults.Serialize(receipt));
    private static void WriteNew(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); var bytes = new UTF8Encoding(false).GetBytes(content); stream.Write(bytes); stream.Flush(flushToDisk: true);
    }
    private static string Number(double? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "held";
    public static string BoundaryText(string text) => text.Replace("Real held-out G4/G5/H2 and H4 researcher sign-off/clean-Windows evidence remain unrun.",
        "Independent validation using real recordings, researcher review and verification on a clean Windows installation remain unrun.", StringComparison.Ordinal);
    public static string Csv(StudyReportDocument document)
    {
        static string Cell(string s) => "\"" + (s.Length > 0 && "=+-@".Contains(s[0]) ? "'" : "") + s.Replace("\"", "\"\"") + "\"";
        static string Exact(double? d) => d?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        var output = new StringBuilder("snapshot,context,phase,day,animal,trial,order,comparison_start_after_release_seconds,bin_start_after_comparison_seconds,bin_end_after_comparison_seconds,bin_start_after_release_seconds,bin_end_after_release_seconds,known_seconds,unknown_fraction,absent_fraction,adjacent_motion_seconds,occupancy_fraction,proximity_pool_diameters,speed_pool_diameters_per_second,wall_observed_fraction,first_source_frame,last_source_frame,eligible,holds,notes,metadata_redacted,source_sha256,run_id,requested_exposure_seconds,actual_exposure_seconds,termination_reason,visit_history,exact_visit_after_comparison_seconds,censor_after_comparison_seconds,exact_visit_after_release_seconds,censor_after_release_seconds\n");
        foreach (var c in document.Contexts) foreach (var b in c.Bins)
        {
            var trial = c.Trials.Single(t => t.Id == b.Trial); var visit = c.Visits.FirstOrDefault(v => v.Trial == b.Trial);
            var fields = new[] { document.SnapshotRevision, c.Id, b.Phase.ToString(), b.Day.ToString(CultureInfo.InvariantCulture), b.Animal, b.Trial, b.Order.ToString(CultureInfo.InvariantCulture), Exact(b.ComparisonOffset), Exact(b.StartAfterComparison), Exact(b.EndAfterComparison), Exact(b.ComparisonOffset + b.StartAfterComparison), Exact(b.ComparisonOffset + b.EndAfterComparison), Exact(b.KnownSeconds), Exact(b.UnknownFraction), Exact(b.AbsentFraction), Exact(b.AdjacentMotionSeconds), Exact(b.Occupancy), Exact(b.Proximity), Exact(b.Speed), Exact(b.Wall), b.FirstFrame?.ToString(CultureInfo.InvariantCulture) ?? "", b.LastFrame?.ToString(CultureInfo.InvariantCulture) ?? "", b.Eligible.ToString(), string.Join(" | ", b.Holds), string.Join(" | ", b.Notes), document.Redacted.ToString(), trial.SourceHash ?? "", trial.RunId ?? "", Exact(trial.RequestedExposureSeconds), Exact(trial.ActualExposureSeconds), trial.TerminationReason, visit?.History.ToString() ?? "held", Exact(visit?.ExactSeconds), Exact(visit?.CensorSeconds), Exact(visit?.ExactSeconds.HasValue == true ? b.ComparisonOffset + visit.ExactSeconds : null), Exact(visit?.CensorSeconds.HasValue == true ? b.ComparisonOffset + visit.CensorSeconds : null) };
            output.AppendLine(string.Join(',', fields.Select(Cell)));
        }
        return output.ToString();
    }
    public static string Html(StudyReportDocument report)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var html = new StringBuilder("<!doctype html><html lang='en'><meta charset='utf-8'><title>Mouse Tracker Tabajara study report</title><style>body{font:16px system-ui;max-width:1150px;margin:30px auto;padding:15px;color:#183045;background:#f5f7fa}section{background:white;padding:20px;margin:20px 0;border-radius:10px}table{border-collapse:collapse;width:100%;font-size:13px}td,th{padding:7px;border:1px solid #ccd6df;text-align:left}svg{background:#f5f7fa;max-width:100%}.held{color:#a34a16}.grid{display:flex;flex-wrap:wrap;gap:20px}small{overflow-wrap:anywhere}a{color:#125b9e}</style><h1>Whole-study conditional descriptive report</h1>");
        html.Append("<p>" + E(BoundaryText(report.Boundaries)) + "</p><p>" + E(report.ReplayMode) + "</p>");
        html.Append(ReportQuestionExport.Html(report));
        html.Append("<p>Source authentication: " + report.SourceAuthentication.Count(s=>s.State=="authenticated") + " authenticated / " + report.SourceAuthentication.Length + " imported. Missing/mismatched/unavailable sources remain held; complete publication of this inventory does not certify all source bytes.</p>");
        html.Append("<details><summary>Every source authentication state</summary><table><tr><th>Recording</th><th>State</th><th>Expected / observed hash</th></tr>");foreach(var s in report.SourceAuthentication)html.Append("<tr><td>"+E(s.VideoId)+"</td><td>"+E(s.State)+"</td><td><small>"+E(s.ExpectedSha256)+" / "+E(s.ObservedSha256??"unavailable/omitted")+"</small></td></tr>");html.Append("</table></details>");
        var shared = report.Contexts.SelectMany(c => c.Groups.SelectMany(g => g.Map)).DefaultIfEmpty(0).Max();
        foreach (var c in report.Contexts)
        {
            html.Append("<section><h2>" + E(c.Id) + " — " + c.Phase + ", day " + c.Day + "</h2><p>Comparison starts " + Number(c.OffsetAfterRelease) + " s after release; duration " + Number(c.Duration) + " s; " + E(c.Orientation) + ". " + E(c.Units) + "</p><p>" + E(c.Interpretation) + "</p><p>" + E(c.TemporalInterpretation) + "</p>");
            html.Append("<table><tr><th>Endpoint</th><th>Animals expected / included / held / excluded</th><th>Trials expected / included / held / excluded</th></tr>");
            foreach (var d in c.Denominators) html.Append($"<tr><td>{E(d.Endpoint)}</td><td>{d.ExpectedAnimals} / {d.IncludedAnimals} / {d.HeldAnimals} / {d.ExcludedAnimals}</td><td>{d.ExpectedTrials} / {d.IncludedTrials} / {d.HeldTrials} / {d.ExcludedTrials}</td></tr>");
            html.Append("</table><h3>Equal-animal maps</h3><p>Shared color maximum " + Number(shared) + "; known-position fractions of planned exposure. Blank/held participants remain below. Missing-position bounds are not confidence intervals.</p><div class='grid'>");
            foreach (var g in c.Groups)
            {
                html.Append("<div><h4>" + E(g.Group) + "</h4><svg width='240' height='240' viewBox='0 0 240 240' role='img' aria-label='Conditional occupancy map'>");
                for (var i = 0; i < g.Map.Length; i++) { var strength = shared > 0 ? Math.Clamp(g.Map[i] / shared, 0, 1) : 0; html.Append($"<rect x='{i % 12 * 20}' y='{i / 12 * 20}' width='20' height='20' fill='rgb({(int)(240 - strength * 210)},{(int)(247 - strength * 125)},210)'><title>Cell{i}; known fraction {Number(g.Map[i])}; upper bound {Number(Math.Min(1,g.Map[i]+g.UnknownFraction))}</title></rect>"); }
                html.Append("</svg><p>Included animals " + g.IncludedAnimals.Length + "; held " + g.HeldAnimals.Length + "; unknown fraction " + Number(g.UnknownFraction) + "</p><p>Contributors: " + E(string.Join(", ", g.IncludedAnimals)) + "</p></div>");
            }
            html.Append("</div>");
            foreach(var difference in c.Differences)
            {
                html.Append("<details><summary>"+E(difference.GroupA)+" minus "+E(difference.GroupB)+" — conservative cellwise missing-position bounds</summary><p>"+E(difference.Interpretation)+"</p><table><tr><th>Cell</th><th>Lower</th><th>Upper</th></tr>");
                for(var i=0;i<difference.Cells.Length;i++) html.Append($"<tr><td>{i}</td><td>{Number(difference.Cells[i].Lower)}</td><td>{Number(difference.Cells[i].Upper)}</td></tr>");
                html.Append("</table></details>");
            }
            html.Append("<p>Whole-window scalar counts match the reported animal/day values and whole-trial endpoint support, independently of per-bin coverage and unrelated endpoint diagnostics. Every expected repeat must support an animal mean. A valid trial does not repair another missing/held repeat; per-bin coverage remains separately listed below.</p><h3>Individual endpoint distributions</h3><svg width='900' height='130' viewBox='0 0 900 130' role='img' aria-label='Individual site occupancy distributions'><line x1='30' y1='95' x2='850' y2='95' stroke='#183045'/><text x='30' y='120'>0 site occupancy fraction</text><text x='820' y='120'>1</text>");
            var distributionIndex = 0;
            foreach (var d in c.IndividualDays.Where(d => d.Occupancy.HasValue)) html.Append($"<circle cx='{(30+820*d.Occupancy!.Value).ToString("R",CultureInfo.InvariantCulture)}' cy='{20+distributionIndex++%7*10}' r='6' fill='#155b83'><title>{E(d.Animal)}; fraction {Number(d.Occupancy)}; vertical jitter is display only; trials {E(string.Join(",",d.TrialIds))}</title></circle>");
            html.Append("</svg><h3>Prespecified temporal profiles</h3><p>Temporal site occupancy: y fraction0–1; x release-relative seconds " + Number(c.OffsetAfterRelease) + "–" + Number(c.OffsetAfterRelease+c.Duration) + ". Missing endpoints are not plotted as zero; cohorts are not connected.</p><svg width='900' height='170' viewBox='0 0 900 170' role='img' aria-label='Temporal site occupancy with release-relative offset'><line x1='30' x2='850' y1='140' y2='140' stroke='#183045'/>");
            foreach (var b in c.Bins.Where(b=>b.Occupancy.HasValue)) html.Append($"<circle cx='{(30+820*b.StartAfterComparison/Math.Max(.001,c.Duration)).ToString("R",CultureInfo.InvariantCulture)}' cy='{(130*(1-b.Occupancy!.Value)).ToString("R",CultureInfo.InvariantCulture)}' r='3' fill='#125b83'><title>{E(b.Animal)}/{E(b.Trial)}; bin {Number(c.OffsetAfterRelease+b.StartAfterComparison)}–{Number(c.OffsetAfterRelease+b.EndAfterComparison)}s after release; occupancy{Number(b.Occupancy)}</title></circle>");
            html.Append("</svg><p>Times below retain both comparison-relative and release-relative offset. Held endpoints are blank, never zero.</p><table><tr><th>Animal / trial</th><th>Bin after comparison (s)</th><th>Bin after release (s)</th><th>Occupancy / proximity diameters / speed diameters/s</th><th>Known s / unknown fraction / absent fraction</th><th>Source frame</th></tr>");
            foreach (var b in c.Bins) html.Append($"<tr><td>{E(b.Animal)} / {E(b.Trial)}</td><td>{Number(b.StartAfterComparison)}–{Number(b.EndAfterComparison)}</td><td>{Number(b.ComparisonOffset+b.StartAfterComparison)}–{Number(b.ComparisonOffset+b.EndAfterComparison)}</td><td>{Number(b.Occupancy)} / {Number(b.Proximity)} / {Number(b.Speed)}</td><td>{Number(b.KnownSeconds)} / {Number(b.UnknownFraction)} / {Number(b.AbsentFraction)}</td><td>{b.FirstFrame}–{b.LastFrame}</td></tr>");
            html.Append("</table><h3>Map-cell contribution provenance</h3><p>Every included animal/trial supplying a known map cell is listed. First/last frame rows need not be continuous; these frame counts are navigation provenance, not animal weights.</p><table><tr><th>Cell</th><th>Animal / trial</th><th>Exact first / last source frame</th><th>Accepted frame rows</th></tr>");
            foreach(var contribution in c.CellContributions.OrderBy(v=>v.Cell)) html.Append($"<tr><td>{contribution.Cell}</td><td>{E(contribution.Animal)} / {E(contribution.Trial)}</td><td>{contribution.FirstFrame} / {contribution.LastFrame}</td><td>{contribution.AcceptedFrameCount}</td></tr>");
            html.Append("</table><h3>Every planned contributor and held/missing/excluded slot</h3><table><tr><th>Animal / trial</th><th>State</th><th>Source/run receipt and exact frames</th><th>Holds / notes</th></tr>");
            foreach (var t in c.Trials) html.Append($"<tr><td>{E(t.Animal)} / {E(t.Id)}</td><td>{(t.Excluded?"excluded":t.VideoId is null?"missing":t.Holds.IsEmpty?"current conditional source":"held")}<br>Requested {Number(t.RequestedExposureSeconds)}s / actual {Number(t.ActualExposureSeconds)}s; {E(t.TerminationReason)}</td><td><small>{E(t.SourceHash??"source identity omitted/missing")}<br>{E(t.RunId??"run identity omitted/missing")}<br>frames {t.FirstFrame}–{t.LastFrame}</small></td><td>{E(string.Join(" | ",t.Holds.Concat(t.Notes)))}</td></tr>");
            html.Append("</table><h3>Visit and censoring histories</h3><table><tr><th>Trial</th><th>History</th><th>Exact / censor after comparison (s)</th><th>Exact / censor after release (s)</th></tr>");
            foreach(var v in c.Visits) html.Append($"<tr><td>{E(v.Trial)}</td><td>{v.History}</td><td>{Number(v.ExactSeconds)} / {Number(v.CensorSeconds)}</td><td>{Number(v.ExactSeconds.HasValue?c.OffsetAfterRelease+v.ExactSeconds:null)} / {Number(v.CensorSeconds.HasValue?c.OffsetAfterRelease+v.CensorSeconds:null)}</td></tr>");
            html.Append("</table></section>");
        }
        html.Append("<section><h2>Frozen provenance and replay</h2><p>Unassigned recordings retained: " + E(string.Join(", ", report.UnassignedRecordingIds)) + "</p><small>Snapshot " + E(report.SnapshotRevision) + "<br>Plan " + E(report.PlanFingerprint) + "<br>Comparison dependency binary receipt " + E(report.CodeReceipt) + "<br>Frozen UTC " + report.FrozenUtc.ToString("O",CultureInfo.InvariantCulture) + "</small><p>Original source pixels are not embedded or redacted. Authenticated original-source inspection remains a separate native action. Legacy original producer executable hashes are unavailable; stored algorithm/software identity does not prove original producer binary.</p></section></html>");
        return html.ToString();
    }
}
