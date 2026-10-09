using System.Collections.Immutable;
using System.IO;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

public sealed record TemporalBinProfile(string AnimalId, string ContextId, string TrialId, int Order, int Day, TrialMode Phase,
    double StartSeconds, double EndSeconds, double KnownSeconds, double UnknownFraction, double AdjacentMotionSeconds,
    double? TargetOccupancyFraction, double? MeanProximityDiameters, double? AdjacentSpeedDiametersPerSecond, double? WallObservedFraction,
    AtlasRecording? Recording, int? FirstFrame, int? LastFrame, ImmutableArray<string> Holds, bool EligibleObservation, ImmutableArray<ProtocolNote> Notes, double AbsentExposureFraction);
public sealed record TemporalAnimalBin(string AnimalId, string Group, string ContextId, int Day, TrialMode Phase, double StartSeconds, double EndSeconds,
    double? Occupancy, double? Proximity, double? Speed, double? Wall, double UnknownFraction, ImmutableArray<TemporalBinProfile> Trials, ImmutableArray<string> Holds);
public sealed record TemporalVisit(string AnimalId, string ContextId, string TrialId, int Day, int Order, TrialMode Phase,
    EventHistory History, double? ExactSeconds, double? CensorSeconds, AtlasRecording? Recording, int? Frame, string Reason);
public sealed record TemporalStudy(ImmutableArray<TemporalBinProfile> TrialBins, ImmutableArray<TemporalAnimalBin> AnimalBins, ImmutableArray<TemporalAnimalBin> AnimalDays,
    ImmutableArray<TemporalVisit> Visits, bool ConnectWithinAnimal, string Interpretation, ImmutableArray<string> Holds);

public static class TemporalProfiles
{
    public static TemporalStudy Build(ExperimentResult selected, CancellationToken cancellation = default)
    {
        var plan = selected.Plan; var bins = ImmutableArray.CreateBuilder<TemporalBinProfile>(); var whole = ImmutableArray.CreateBuilder<TemporalBinProfile>(); var visits = ImmutableArray.CreateBuilder<TemporalVisit>(); var reasons = ImmutableArray.CreateBuilder<string>();
        // Days and phase stay explicit. Different phases are never concatenated into one trajectory.
        foreach (var context in plan.Contexts.Where(c => c.Phase == selected.Context.Phase).OrderBy(c => c.Day).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var inventory = StudyPlanInventory.Build(plan, selected.Snapshot.Recordings.Select(r => r.Asset));
            if (context.TemporalBinEdges.IsEmpty) { reasons.Add(context.Id + ": temporal bins are not prespecified."); continue; }
            foreach (var item in inventory.Where(i => i.Trial.ContextId.Equals(context.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var slot = item.Trial with { AnimalId = plan.Animals.Single(a => a.Id.Equals(item.Trial.AnimalId, StringComparison.OrdinalIgnoreCase)).Id, ContextId = context.Id };
                cancellation.ThrowIfCancellationRequested(); var row = selected.Snapshot.Recordings.SingleOrDefault(r => r.Asset.Id == item.Trial.RecordingId);
                var holds = item.Holds.ToBuilder(); var notes = ImmutableArray.CreateBuilder<ProtocolNote>(); AnalysisRun? run = null; double origin = 0, actualEnd = context.ComparisonDurationSeconds;
                try
                {
                    if (row is null) throw new InvalidDataException("Planned source is missing.");
                    run = row.EffectiveRun() ?? throw new InvalidDataException("Completed source run missing."); CompletedRecordingContract.Validate(row.RawRun!);
                    if (ExperimentPlanAdapter.HashSource(row.Asset.Path, cancellation) != row.Asset.SourceSha256) throw new InvalidDataException("Original source identity changed.");
                    if (!ExperimentPlanAdapter.CurrentInspection(row)) holds.Add("Current frozen tracking inspection is unavailable.");
                    if (!item.Trial.SourceReferenceConfirmed || item.Trial.ReferenceSourceSha256 != row.Asset.SourceSha256 || item.Trial.ReferenceSettingsHash != MouseTrackerTabajara.Storage.StudyStore.SettingsHash(run.Settings))
                        holds.Add("Source/reference identity is unconfirmed or stale.");
                    var protocol = ExperimentPlanAdapter.Protocol(selected.Snapshot, row.Asset.Id) ?? throw new InvalidDataException("Confirmed protocol is missing.");
                    var outcome = ProtocolEvaluator.Evaluate(protocol, run.Metadata, row.Asset.Phase, ExperimentPlanAdapter.EffectiveEvents(selected.Snapshot, row.Asset.Id));
                    holds.AddRange(outcome.Holds);
                    notes.AddRange(outcome.Notes);
                    if (!protocol.ReleaseConfirmed || !protocol.WindowConfirmed || protocol.ReleaseFrame is null || protocol.Phase != context.Phase || Math.Abs(protocol.RequestedExposureSeconds - context.RequestedExposureSeconds) > 1e-9)
                        holds.Add("Protocol/window/phase does not match the expected context.");
                    origin = (protocol.ReleaseFrame ?? 0) / run.Metadata.Fps + context.ComparisonStartSeconds;
                    if (context.Phase == TrialMode.Training)
                        actualEnd = Math.Max(0, Math.Min(context.ComparisonDurationSeconds, (outcome.ActualExposureSeconds ?? 0) - context.ComparisonStartSeconds));
                    visits.Add(Visit(slot, context, row, run, origin, actualEnd, holds.Count == 0));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { holds.Add("Temporal source held: " + ex.Message); visits.Add(new(slot.AnimalId, context.Id, slot.Id, context.Day, slot.Order, context.Phase, EventHistory.VisitHistoryIndeterminate, null, null, row, null, "Source/window/inspection evidence is held.")); }
                for (var i = 0; i < context.TemporalBinEdges.Length - 1; i++)
                {
                    var start = context.TemporalBinEdges[i]; var end = context.TemporalBinEdges[i + 1];
                    bins.Add(Bin(slot, context, row, run, origin, start, end, actualEnd, holds.Distinct().ToImmutableArray(), notes.ToImmutable()));
                }
                whole.Add(Bin(slot, context, row, run, origin, 0, context.Phase == TrialMode.Training && actualEnd > 0 ? actualEnd : context.ComparisonDurationSeconds, actualEnd, holds.Distinct().ToImmutableArray(), notes.ToImmutable()));
            }
        }
        TemporalAnimalBin Average(IEnumerable<TemporalBinProfile> samples)
        {
            var rows = samples.OrderBy(b => b.Order).ToImmutableArray(); var first = rows[0]; var animal = plan.Animals.Single(a => a.Id.Equals(first.AnimalId, StringComparison.OrdinalIgnoreCase));
            var holds = rows.SelectMany(r => r.Holds).Distinct().ToImmutableArray();
            double? Mean(Func<TemporalBinProfile, double?> value) => rows.All(r => r.EligibleObservation && value(r).HasValue) ? rows.Average(r => value(r)!.Value) : null;
            return new TemporalAnimalBin(first.AnimalId, animal.Group, first.ContextId, first.Day, first.Phase, first.StartSeconds, first.EndSeconds,
                Mean(r => r.TargetOccupancyFraction), Mean(r => r.MeanProximityDiameters), Mean(r => r.AdjacentSpeedDiametersPerSecond), Mean(r => r.WallObservedFraction),
                rows.Average(r => r.UnknownFraction), rows, holds);
        }
        var animals = bins.GroupBy(b => (b.AnimalId, b.ContextId, b.StartSeconds, b.EndSeconds)).Select(g => Average(g)).ToImmutableArray();
        var days = whole.GroupBy(b => (b.AnimalId, b.ContextId)).Select(g => Average(g)).ToImmutableArray();
        var connect = plan.DesignConfirmed && plan.UnitDesign == StudyUnitDesign.RepeatedTrialsWithinAnimals && plan.Animals.All(a => a.IdentityConfirmed);
        var wording = connect ? "Connected within-mouse descriptive trajectories; each day/context retains its own exposure and trial order."
            : plan.DesignConfirmed && plan.UnitDesign == StudyUnitDesign.IndependentAnimals ? "Independent cohorts: individual distributions are separate; different mice are never connected as learning."
            : "Experimental design/identity is unknown: descriptive points only; learning and inference are withheld.";
        return new(bins.ToImmutable(), animals, days, visits.ToImmutable(), connect, wording + " Conditional on accepted coordinates; no causal memory score or significance test.", reasons.ToImmutable());
    }
    private static TemporalBinProfile Bin(PlannedTrial slot, PlannedContext context, AtlasRecording? row, AnalysisRun? run, double origin, double start, double end, double actualEnd, ImmutableArray<string> holds, ImmutableArray<ProtocolNote> notes)
    {
        double known = 0, target = 0, proximity = 0, wall = 0, distance = 0, motion = 0; int? first = null, last = null; var reasons = holds.ToBuilder();
        var duration = end - start; var validEnd = Math.Min(end, actualEnd);
        if (context.Phase == TrialMode.Training && end > actualEnd + 1e-9) reasons.Add("Bin extends beyond confirmed training termination; not an observed zero.");
        if (run is not null)
        {
            var s = run.Settings; var m = run.Metadata; var rx = s.Rx * m.Width; var ry = s.Ry * m.Height;
            TrackPoint? prior = null;
            foreach (var p in run.Points)
            {
                var t = p.Frame / m.Fps - origin; var overlap = Math.Max(0, Math.Min(validEnd, t + 1 / m.Fps) - Math.Max(start, t));
                if (overlap > 0) { first ??= p.Frame; last = p.Frame; }
                if (overlap > 0 && p.Accepted)
                {
                    known += overlap; var u = (p.X!.Value - s.Cx * m.Width) / rx; var v = (p.Y!.Value - s.Cy * m.Height) / ry;
                    if (Math.Sqrt(u * u + v * v) >= s.WallBoundary) wall += overlap;
                    if (s.Platform is { } site)
                    {
                        var du = (p.X.Value - site.X * m.Width) / (2 * rx); var dv = (p.Y.Value - site.Y * m.Height) / (2 * ry);
                        proximity += overlap * Math.Sqrt(du * du + dv * dv);
                        if (Math.Sqrt(du * du + dv * dv) <= site.Radius / (2 * s.Rx)) target += overlap;
                    }
                }
                if (prior is { Accepted: true } && p.Accepted && p.Frame == prior.Frame + 1)
                {
                    var previousTime = prior.Frame / m.Fps - origin;
                    var adjacent = Math.Max(0, Math.Min(validEnd, t) - Math.Max(start, previousTime));
                    if (adjacent > 0)
                    {
                        var du = (p.X!.Value - prior.X!.Value) / (2 * rx); var dv = (p.Y!.Value - prior.Y!.Value) / (2 * ry);
                        distance += Math.Sqrt(du * du + dv * dv) * adjacent * m.Fps; motion += adjacent;
                    }
                }
                prior = p;
            }
            if (s.Platform is null) reasons.Add("Platform site is unset; proximity and target occupancy are unavailable.");
        }
        else reasons.Add("Source positions are unavailable.");
        if (known == 0) reasons.Add("No accepted position exposure in this bin; position endpoints are held.");
        if (motion == 0) reasons.Add("No adjacent accepted-motion exposure; speed is unavailable.");
        var valid = holds.IsEmpty && validEnd >= end - 1e-9;
        var absent = context.Phase == TrialMode.Training ? Math.Clamp((end - Math.Max(start, Math.Min(end, actualEnd))) / duration, 0, 1) : 0;
        return new(slot.AnimalId, context.Id, slot.Id, slot.Order, context.Day, context.Phase, start, end, known, Math.Clamp(1 - known / duration - absent, 0, 1), motion,
            valid && run?.Settings.Platform is not null && known > 0 ? target / duration : null, valid && run?.Settings.Platform is not null && known > 0 ? proximity / known : null,
            valid && motion > 0 ? distance / motion : null, valid && known > 0 ? wall / known : null, row, first, last, reasons.Distinct().ToImmutableArray(), valid, notes, absent);
    }
    private static TemporalVisit Visit(PlannedTrial slot, PlannedContext context, AtlasRecording row, AnalysisRun run, double origin, double end, bool eligible)
    {
        if (!eligible || run.Settings.Platform is null) return new(slot.AnimalId, context.Id, slot.Id, context.Day, slot.Order, context.Phase, EventHistory.VisitHistoryIndeterminate, null, null, row, null, "First visit held: window/inspection/platform is unavailable.");
        var s = run.Settings; var m = run.Metadata; var site = s.Platform!; var unknown = run.Points[0].Frame / m.Fps > origin;
        foreach (var p in run.Points.Where(p => (p.Frame + 1d) / m.Fps > origin && p.Frame / m.Fps < origin + end))
        {
            if (!p.Accepted) { unknown = true; continue; }
            var u = (p.X!.Value - site.X * m.Width) / (s.Rx * m.Width); var v = (p.Y!.Value - site.Y * m.Height) / (s.Ry * m.Height);
            if (Math.Sqrt(u * u + v * v) <= site.Radius / s.Rx)
                return new(slot.AnimalId, context.Id, slot.Id, context.Day, slot.Order, context.Phase, unknown ? EventHistory.EarlierVisitPossibleInGap : EventHistory.ObservedExact,
                    unknown ? null : Math.Max(0, p.Frame / m.Fps - origin), null, row, p.Frame, unknown ? "Earlier site visit possible in unknown exposure; exact timing held." : "Observed site-region visit; mounting is not established.");
        }
        if (run.Points[^1].Frame / m.Fps + 1 / m.Fps < origin + end) unknown = true;
        return new(slot.AnimalId, context.Id, slot.Id, context.Day, slot.Order, context.Phase, unknown ? EventHistory.VisitHistoryIndeterminate : EventHistory.RightCensoredComplete,
            null, unknown ? null : end, row, run.Points[^1].Frame, unknown ? "Visit history indeterminate; missing exposure is not censoring." : "Right-censored complete observation; no successful visit is imputed.");
    }
}
