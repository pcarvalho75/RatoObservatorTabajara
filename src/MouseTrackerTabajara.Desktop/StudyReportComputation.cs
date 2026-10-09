using System.Collections.Immutable;
using System.IO;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.SpatialViews;

namespace MouseTrackerTabajara.Desktop;

public sealed record ReportTrial(string Id, string Animal, string Context, int Order, string? VideoId, string? SourceHash, string? RunId,
    int? FirstFrame, int? LastFrame, bool Excluded, ImmutableArray<string> Holds, ImmutableArray<string> Notes, double? RequestedExposureSeconds, double? ActualExposureSeconds, string TerminationReason);
public sealed record ReportAnimal(string Id, string Group, bool Excluded, bool MapIncluded, ImmutableArray<string> Holds, ImmutableArray<double> Map, double? UnknownFraction);
public sealed record ReportGroup(string Group, ImmutableArray<string> IncludedAnimals, ImmutableArray<string> HeldAnimals, ImmutableArray<double> Map, double UnknownFraction);
public sealed record ReportCellContribution(string Animal, string Trial, string VideoId, string SourceHash, string RunId, int Cell, int FirstFrame, int LastFrame, int AcceptedFrameCount);
public sealed record ReportDay(string Animal, string Context, TrialMode Phase, int Day, double? Occupancy, double? Proximity, double? Speed, double? Wall, double UnknownFraction,
    ImmutableArray<string> TrialIds, ImmutableArray<string> Holds);
public sealed record ReportBin(string Trial, string Animal, string Context, TrialMode Phase, int Day, int Order, double ComparisonOffset,
    double StartAfterComparison, double EndAfterComparison, double KnownSeconds, double UnknownFraction, double AbsentFraction, double AdjacentMotionSeconds,
    double? Occupancy, double? Proximity, double? Speed, double? Wall, int? FirstFrame, int? LastFrame, bool Eligible, ImmutableArray<string> Holds, ImmutableArray<string> Notes);
public sealed record ReportVisit(string Trial, string Animal, string Context, EventHistory History, double? ExactSeconds, double? CensorSeconds, int? Frame, string Reason);
public sealed record ReportEndpointDenominator(string Endpoint, int ExpectedAnimals, int IncludedAnimals, int HeldAnimals, int ExcludedAnimals, int ExpectedTrials, int IncludedTrials, int HeldTrials, int ExcludedTrials);
public sealed record ReportSourceAuthentication(string VideoId, string ExpectedSha256, string? ObservedSha256, string State);
public sealed record ReportContext(string Id, TrialMode Phase, int Day, double OffsetAfterRelease, double Duration, string Orientation, string Units,
    ImmutableArray<ReportTrial> Trials, ImmutableArray<ReportAnimal> Animals, ImmutableArray<ReportGroup> Groups, ImmutableArray<GroupDifferenceBounds> Differences,
    ImmutableArray<ReportDay> IndividualDays, ImmutableArray<ReportBin> Bins, ImmutableArray<ReportVisit> Visits, ImmutableArray<ReportEndpointDenominator> Denominators,
    double MapScaleMaximum, string Interpretation, string TemporalInterpretation, ImmutableArray<ReportCellContribution> CellContributions,ReportQuestionData? Questions=null);
public sealed record StudyReportDocument(string SnapshotRevision, string CodeReceipt, string PlanFingerprint, bool Redacted, DateTime FrozenUtc,
    ImmutableArray<ReportContext> Contexts, ImmutableArray<string> UnassignedRecordingIds, string Boundaries, ImmutableArray<ReportSourceAuthentication> SourceAuthentication,
    string ReplayMode = "Aggregate-only inspection; authenticated reconstruction additionally requires unredacted frozen input, compatible code and original source bytes.");
public sealed record StudyReportComputed(AtlasSnapshot Snapshot, StudyReportMaterial Material, StudyReportDocument Document, ImmutableArray<ExperimentSourceStamp> Sources);

public static class StudyReportComputation
{
    public static StudyReportComputed Compute(AtlasSnapshot snapshot, IEnumerable<string> selectedContexts, CancellationToken cancellation = default)
    {
        var saved = ExperimentPlanAdapter.FrozenPlan(snapshot) ?? throw new InvalidDataException("Confirmed expected study plan required.");
        var selected = selectedContexts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0 || selected.Any(id => !saved.Plan.Contexts.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))) throw new ArgumentException("Select planned contexts.");
        if (!snapshot.IsCurrent()) throw new InvalidDataException("Frozen input is stale.");
        var sources = snapshot.Recordings.Select(r => ExperimentSourceStamp.Read(r.Asset.Path)).ToImmutableArray();
        var authentication = Authenticate(snapshot, cancellation);
        var material = StudyReportMaterial.Freeze(snapshot); var contexts = ImmutableArray.CreateBuilder<ReportContext>();
        var temporalByPhase = new Dictionary<TrialMode, TemporalStudy>();
        foreach (var context in saved.Plan.Contexts.Where(c => selected.Contains(c.Id)))
        {
            cancellation.ThrowIfCancellationRequested(); var result = ExperimentPlanAdapter.Build(snapshot, context.Id, cancellation);
            if (!temporalByPhase.TryGetValue(context.Phase, out var temporal)) { temporal = TemporalProfiles.Build(result, cancellation); temporalByPhase.Add(context.Phase, temporal); }
            var bins = temporal.TrialBins.Where(b => b.ContextId == context.Id).ToArray(); var days = temporal.AnimalDays.Where(d => d.ContextId == context.Id).ToArray();
            var trials = result.Trials.Select(t => new ReportTrial(t.Slot.Id, saved.Plan.Animals.Single(a => a.Id.Equals(t.Slot.AnimalId, StringComparison.OrdinalIgnoreCase)).Id, context.Id, t.Slot.Order, t.Recording?.Asset.Id, t.Recording?.Asset.SourceSha256,
                t.Recording?.RawRun?.Id, bins.Where(b => b.TrialId == t.Slot.Id).Select(b => NavigationFrame(snapshot, context, b, true)).FirstOrDefault(f => f.HasValue), bins.Where(b => b.TrialId == t.Slot.Id).Select(b => NavigationFrame(snapshot, context, b, false)).LastOrDefault(f => f.HasValue),
                saved.Plan.Animals.Single(a => a.Id.Equals(t.Slot.AnimalId, StringComparison.OrdinalIgnoreCase)).Excluded || t.Recording?.Asset.Included == false, t.Holds,
                t.Notes.IsDefault ? [] : t.Notes.Select(n => n.Text).ToImmutableArray(), Exposure(snapshot, t.Recording)?.RequestedExposureSeconds, Exposure(snapshot, t.Recording)?.ActualExposureSeconds, Exposure(snapshot, t.Recording)?.TerminationReason ?? "Exposure evidence held or missing")).ToImmutableArray();
            var animals = result.Study.Animals.Select(a => new ReportAnimal(a.Input.AnimalId, a.Input.Group, a.Input.Excluded, a.Eligible, a.HoldReasons, a.KnownFractions, a.UnknownFraction)).ToImmutableArray();
            var groups = result.Study.Groups.Select(g => new ReportGroup(g.Group, g.IncludedAnimalIds, g.HeldAnimalIds, g.KnownFractions, g.UnknownFraction)).ToImmutableArray();
            var reportBins = bins.Select(b => new ReportBin(b.TrialId, b.AnimalId, b.ContextId, b.Phase, b.Day, b.Order, context.ComparisonStartSeconds,
                b.StartSeconds, b.EndSeconds, b.KnownSeconds, b.UnknownFraction, b.AbsentExposureFraction, b.AdjacentMotionSeconds, b.TargetOccupancyFraction, b.MeanProximityDiameters,
                b.AdjacentSpeedDiametersPerSecond, b.WallObservedFraction, NavigationFrame(snapshot, context, b, true), NavigationFrame(snapshot, context, b, false), b.EligibleObservation, b.Holds, b.Notes.Select(n => n.Text).ToImmutableArray())).ToImmutableArray();
            var reportDays = days.Select(d => new ReportDay(d.AnimalId, d.ContextId, d.Phase, d.Day, d.Occupancy, d.Proximity, d.Speed, d.Wall, d.UnknownFraction,
                d.Trials.Select(t => t.TrialId).ToImmutableArray(), d.Holds)).ToImmutableArray();
            var denominators = ImmutableArray.CreateBuilder<ReportEndpointDenominator>();
            void Denominator(string name, Func<ReportAnimal, bool> animalValid, Func<ReportTrial, bool> trialValid)
            {
                var expectedAnimals = animals.Where(a => trials.Any(t => t.Animal == a.Id)).ToArray();
                var excludedAnimals = expectedAnimals.Count(a => a.Excluded); var includedAnimals = expectedAnimals.Count(a => !a.Excluded && animalValid(a));
                var excludedTrials = trials.Count(t => t.Excluded); var includedTrials = trials.Count(t => !t.Excluded && trialValid(t));
                denominators.Add(new(name, expectedAnimals.Length, includedAnimals, expectedAnimals.Length - includedAnimals - excludedAnimals, excludedAnimals, trials.Length, includedTrials, trials.Length - includedTrials - excludedTrials, excludedTrials));
            }
            Denominator("normalized occupancy map", a => a.MapIncluded, t => animals.Single(a => a.Id.Equals(t.Animal, StringComparison.OrdinalIgnoreCase)).MapIncluded && result.Trials.Single(r => r.Slot.Id == t.Id).Map?.Eligible == true && t.Holds.IsEmpty);
            foreach (var endpoint in new[] { "site occupancy fraction", "proximity pool diameters", "adjacent speed pool diameters/s", "wall observed fraction" })
            {
                double? Day(ReportDay d) => endpoint switch { "site occupancy fraction" => d.Occupancy, "proximity pool diameters" => d.Proximity, "adjacent speed pool diameters/s" => d.Speed, _ => d.Wall };
                double? WholeTrial(TemporalBinProfile t) => endpoint switch { "site occupancy fraction" => t.TargetOccupancyFraction, "proximity pool diameters" => t.MeanProximityDiameters, "adjacent speed pool diameters/s" => t.AdjacentSpeedDiametersPerSecond, _ => t.WallObservedFraction };
                // AnimalDays already requires every expected whole-trial profile to support this endpoint.
                // Generic diagnostic holds can describe other endpoints; prespecified bin coverage is a separate scope.
                Denominator(endpoint + " (whole-window)", a => reportDays.Any(d => d.Animal == a.Id && Day(d).HasValue),
                    t => days.SelectMany(d => d.Trials).Any(p => p.TrialId == t.Id && p.EligibleObservation && WholeTrial(p).HasValue));
            }
            var visits = temporal.Visits.Where(v => v.ContextId == context.Id).Select(v => new ReportVisit(v.TrialId, v.AnimalId, v.ContextId, v.History, v.ExactSeconds, v.CensorSeconds, v.Frame, v.Reason)).ToImmutableArray();
            Denominator("site first-visit/censor history", a => visits.Any(v => v.Animal == a.Id) && visits.Where(v => v.Animal == a.Id).All(v => v.History is EventHistory.ObservedExact or EventHistory.RightCensoredComplete), t => visits.Any(v => v.Trial == t.Id && v.History is EventHistory.ObservedExact or EventHistory.RightCensoredComplete));
            var contributions = result.Trials.Where(t => t.Map?.Eligible == true && animals.Single(a => a.Id.Equals(t.Slot.AnimalId, StringComparison.OrdinalIgnoreCase)).MapIncluded && t.Recording is not null)
                .SelectMany(t => t.Map!.Input.Frames.Where(f => f.Accepted && Math.Min(f.EndSeconds, context.ComparisonDurationSeconds) - Math.Max(f.StartSeconds, 0) > 1e-9)
                    .GroupBy(f => t.Map.Input.Grid.Cell(f.X!.Value, f.Y!.Value)).Where(g => g.Key >= 0).Select(g => new ReportCellContribution(saved.Plan.Animals.Single(a => a.Id.Equals(t.Slot.AnimalId, StringComparison.OrdinalIgnoreCase)).Id, t.Slot.Id, t.Recording!.Asset.Id, t.Recording.Asset.SourceSha256, t.Recording.NavigationId,
                        g.Key, g.Min(f => f.Frame), g.Max(f => f.Frame), g.Count()))).ToImmutableArray();
            contexts.Add(new(context.Id, context.Phase, context.Day, context.ComparisonStartSeconds, context.ComparisonDurationSeconds, context.Orientation.ToString(), "normalized pool radii maps; proximity/path pool diameters; time seconds; no physical cm adapter",
                trials, animals, groups, result.Differences, reportDays, reportBins, visits, denominators.ToImmutable(), result.Study.SharedKnownFractionMaximum, result.Interpretation, temporal.Interpretation, contributions,ReportQuestions.Build(result,temporal,cancellation)));
        }
        cancellation.ThrowIfCancellationRequested();
        if (!snapshot.IsCurrent() || sources.Any(s => ExperimentSourceStamp.Read(s.Path) != s) || material.CodeReceipt != StudyReportMaterial.CurrentCodeReceipt()) throw new InvalidDataException("Frozen source/study/code changed during report computation.");
        var assigned = saved.Plan.Trials.Select(t => t.RecordingId).ToHashSet(StringComparer.Ordinal);
        return new(snapshot, material, new(snapshot.Revision, material.CodeReceipt, saved.Fingerprint, false, material.FrozenUtc, contexts.ToImmutable(), snapshot.Recordings.Where(r => !assigned.Contains(r.Asset.Id)).Select(r => r.Asset.Id).ToImmutableArray(),
            "Conditional descriptive output only; held/excluded/missing planned slots retained. Unknown design withholds learning/inference. Tracking coverage is not independent accuracy. Independent validation using real recordings, researcher review and verification on a clean Windows installation remain unrun.", authentication), sources);
    }
    public static ImmutableArray<ReportSourceAuthentication> Authenticate(AtlasSnapshot snapshot, CancellationToken cancellation)
    {
        return snapshot.Recordings.Select(row =>
        {
            cancellation.ThrowIfCancellationRequested(); var asset = row.Asset;
            try { if (!File.Exists(asset.Path)) return new ReportSourceAuthentication(asset.Id, asset.SourceSha256, null, "missing"); var hash = ExperimentPlanAdapter.HashSource(asset.Path, cancellation); return new(asset.Id, asset.SourceSha256, hash, hash == asset.SourceSha256 ? "authenticated" : "mismatch"); }
            catch (OperationCanceledException) { throw; } catch (Exception) { return new(asset.Id, asset.SourceSha256, null, "unavailable"); }
        }).ToImmutableArray();
    }
    // Navigation clips to the declared source-frame interval. Suppress sub-ulp boundary overlap without altering accepted endpoint calculations.
    private static ProtocolOutcome? Exposure(AtlasSnapshot snapshot, AtlasRecording? row)
    {
        if (row is null || ExperimentPlanAdapter.Protocol(snapshot, row.Asset.Id) is not { } protocol) return null;
        try { return ProtocolEvaluator.Evaluate(protocol, row.Asset.Metadata, row.Asset.Phase, ExperimentPlanAdapter.EffectiveEvents(snapshot, row.Asset.Id)); }
        catch (Exception) { return null; }
    }
    private static int? NavigationFrame(AtlasSnapshot snapshot, PlannedContext context, TemporalBinProfile bin, bool first)
    {
        var frame = first ? bin.FirstFrame : bin.LastFrame; var row = bin.Recording;
        if (!frame.HasValue || row is null || ExperimentPlanAdapter.Protocol(snapshot, row.Asset.Id)?.ReleaseFrame is not { } release) return frame;
        var boundary = release + (context.ComparisonStartSeconds + (first ? bin.StartSeconds : bin.EndSeconds)) * row.Asset.Metadata.Fps;
        return first ? Math.Max(frame.Value, (int)Math.Floor(boundary + 1e-9)) : Math.Min(frame.Value, (int)Math.Ceiling(boundary - 1e-9) - 1);
    }
    public static StudyReportDocument Project(StudyReportDocument source, StudyPlan plan, bool blinded)
    {
        if (!blinded) return source;
        string Animal(string id) => "Animal " + (Array.FindIndex(plan.Animals.ToArray(), a => a.Id == id) + 1).ToString("000");
        string Context(string id) => "Context " + (Array.FindIndex(plan.Contexts.ToArray(), c => c.Id == id) + 1).ToString("000");
        string Trial(string id) => "Trial " + (Array.FindIndex(plan.Trials.ToArray(), t => t.Id == id) + 1).ToString("000");
        string Group(string group) => "Allocation " + (Array.IndexOf(plan.Animals.Select(a => a.Group).Distinct(StringComparer.Ordinal).ToArray(), group) + 1).ToString("000");
        ImmutableArray<string> Holds(ImmutableArray<string> h) => h.IsEmpty ? [] : ["Source/window/endpoint evidence held; authored labels omitted."];
        return source with { Redacted = true, UnassignedRecordingIds = source.UnassignedRecordingIds.Select((_, i) => "Unassigned recording " + (i + 1).ToString("000")).ToImmutableArray(),
            SourceAuthentication = source.SourceAuthentication.Select((s,i) => s with { VideoId = "Recording " + (i+1).ToString("000"), ExpectedSha256 = "omitted", ObservedSha256 = null }).ToImmutableArray(),
            ReplayMode = "Redacted aggregate-only replay. Raw identity/source/run/settings hashes, authored notes and frozen SQL material omitted; authenticated historical reconstruction is unavailable from this redacted package.",
            Contexts = source.Contexts.Select(c => c with { Id = Context(c.Id), Interpretation = "Conditional eligible-only descriptive maps; no inference, learning or independent accuracy conclusion.", TemporalInterpretation = "Separate phase/day/context observations; no automatic inference.",
                Trials = c.Trials.Select(t => t with { Id = Trial(t.Id), Animal = Animal(t.Animal), Context = Context(t.Context), VideoId = t.VideoId is null ? null : "Recording " + Trial(t.Id)[6..], SourceHash = null, RunId = null, Holds = Holds(t.Holds), Notes = t.Notes.IsEmpty ? [] : ["Nonblocking protocol annotations recorded."], TerminationReason = "Protocol termination retained; authored metadata omitted." }).ToImmutableArray(),
                Animals = c.Animals.Select(a => a with { Id = Animal(a.Id), Group = Group(a.Group), Holds = Holds(a.Holds) }).ToImmutableArray(),
                Groups = c.Groups.Select(g => g with { Group = Group(g.Group), IncludedAnimals = g.IncludedAnimals.Select(Animal).ToImmutableArray(), HeldAnimals = g.HeldAnimals.Select(Animal).ToImmutableArray() }).ToImmutableArray(),
                Differences = c.Differences.Select(d => d with { GroupA = Group(d.GroupA), GroupB = Group(d.GroupB) }).ToImmutableArray(),
                IndividualDays = c.IndividualDays.Select(d => d with { Animal = Animal(d.Animal), Context = Context(d.Context), TrialIds = d.TrialIds.Select(Trial).ToImmutableArray(), Holds = Holds(d.Holds) }).ToImmutableArray(),
                Bins = c.Bins.Select(b => b with { Trial = Trial(b.Trial), Animal = Animal(b.Animal), Context = Context(b.Context), Holds = Holds(b.Holds), Notes = b.Notes.IsEmpty ? [] : ["Nonblocking protocol annotations recorded."] }).ToImmutableArray(),
                Visits = c.Visits.Select(v => v with { Trial = Trial(v.Trial), Animal = Animal(v.Animal), Context = Context(v.Context), Reason = "Exact observed, censored or held site-region history; authored text omitted." }).ToImmutableArray(),
                CellContributions = c.CellContributions.Select(v => v with { Animal = Animal(v.Animal), Trial = Trial(v.Trial), VideoId = "Recording " + Trial(v.Trial)[6..], SourceHash = "omitted", RunId = "omitted" }).ToImmutableArray(),
                Questions=c.Questions is not {} q?null:q with {
                    Trials=q.Trials.Select(t=>t with{Trial=Trial(t.Trial),Animal=Animal(t.Animal),Group=Group(t.Group),Support=Holds(t.Support)}).ToImmutableArray(),
                    Animals=q.Animals.Select(a=>a with{Animal=Animal(a.Animal),Group=Group(a.Group),Support=Holds(a.Support)}).ToImmutableArray(),
                    Groups=q.Groups.Select(g=>g with{Group=Group(g.Group),Contributors=g.Contributors.Select(Animal).ToImmutableArray(),Support=Holds(g.Support)}).ToImmutableArray(),
                    Differences=q.Differences.Select(d=>d with{GroupA=Group(d.GroupA),GroupB=Group(d.GroupB)}).ToImmutableArray(),
                    Coordinates=q.Coordinates.Select(p=>p with{Trial=Trial(p.Trial),Animal=Animal(p.Animal),VideoId="Recording "+Trial(p.Trial)[6..],SourceHash="omitted",RunId="omitted"}).ToImmutableArray(),
                    Paths=q.Paths.Select(p=>p with{Trial=Trial(p.Trial)}).ToImmutableArray(),Densities=q.Densities.Select(p=>p with{Trial=Trial(p.Trial)}).ToImmutableArray() } }).ToImmutableArray() };
    }
}
