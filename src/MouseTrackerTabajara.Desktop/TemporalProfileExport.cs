using System.Globalization;
using System.IO;
using System.Text;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

public static class TemporalProfileExport
{
    public static void Save(string path, TemporalStudy profile, StudyPlan plan, bool blinded, string snapshotRevision)
    {
        static string Cell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        var rows = new List<string> { "snapshot_revision,animal,context,trial,source,run,phase,day,trial_order,bin_start_after_comparison_seconds,bin_end_after_comparison_seconds,known_position_seconds,unknown_position_fraction,absent_after_termination_fraction,adjacent_motion_seconds,site_occupancy_fraction,proximity_pool_diameters,speed_pool_diameters_per_second,wall_observed_fraction,first_source_frame,last_source_frame,first_visit_state,exact_visit_after_comparison_seconds,censor_after_comparison_seconds,holds,notes,comparison_start_after_release_seconds,bin_start_after_release_seconds,bin_end_after_release_seconds" };
        foreach (var bin in profile.TrialBins)
        {
            var animal = Array.FindIndex(plan.Animals.ToArray(), a => a.Id == bin.AnimalId) + 1; var context = Array.FindIndex(plan.Contexts.ToArray(), c => c.Id == bin.ContextId) + 1;
            var slot = Array.FindIndex(plan.Trials.ToArray(), t => t.Id == bin.TrialId) + 1;
            var visit = profile.Visits.FirstOrDefault(v => v.TrialId == bin.TrialId && v.ContextId == bin.ContextId);
            var offset = plan.Contexts.Single(c => c.Id == bin.ContextId).ComparisonStartSeconds;
            var visitState = visit?.History == EventHistory.ObservedExact ? "exact site-region observation" : visit?.History == EventHistory.RightCensoredComplete ? "right-censored complete observation" : "held or indeterminate history";
            var fields = new[] { snapshotRevision, blinded ? $"Animal {animal:000}" : bin.AnimalId, blinded ? $"Context {context:000}" : bin.ContextId,
                blinded ? $"Trial {slot:000}" : bin.TrialId, blinded ? $"Source {slot:000}" : bin.Recording?.Asset.SourceSha256 ?? "", blinded ? $"Run {slot:000}" : bin.Recording?.NavigationId ?? "",
                bin.Phase.ToString(), bin.Day.ToString(CultureInfo.InvariantCulture), bin.Order.ToString(CultureInfo.InvariantCulture), Number(bin.StartSeconds), Number(bin.EndSeconds), Number(bin.KnownSeconds), Number(bin.UnknownFraction), Number(bin.AbsentExposureFraction), Number(bin.AdjacentMotionSeconds),
                Number(bin.TargetOccupancyFraction), Number(bin.MeanProximityDiameters), Number(bin.AdjacentSpeedDiametersPerSecond), Number(bin.WallObservedFraction), bin.FirstFrame?.ToString(CultureInfo.InvariantCulture) ?? "", bin.LastFrame?.ToString(CultureInfo.InvariantCulture) ?? "", visitState, Number(visit?.ExactSeconds), Number(visit?.CensorSeconds),
                blinded ? (bin.Holds.IsEmpty ? "" : "Endpoint or source evidence held") : string.Join(" | ", bin.Holds), blinded ? (bin.Notes.IsEmpty ? "" : "Nonblocking protocol annotations recorded") : string.Join(" | ", bin.Notes.Select(n => n.Text)),
                Number(offset), Number(offset + bin.StartSeconds), Number(offset + bin.EndSeconds) };
            rows.Add(string.Join(',', fields.Select(Cell)));
        }
        // Null endpoints remain empty: no zero/censoring imputation. Receipt stays tied to frozen material.
        File.WriteAllLines(path, rows, new UTF8Encoding(true));
    }
}
