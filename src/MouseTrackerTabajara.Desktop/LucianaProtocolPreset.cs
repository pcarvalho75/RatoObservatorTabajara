using System.Collections.Immutable;
using MouseTrackerTabajara.Core;

namespace MouseTrackerTabajara.Desktop;

public static class LucianaProtocolPreset
{
    public static ImmutableArray<string> Groups { get; } = ["treated wildtype","vehicle wildtype","treated mutant","vehicle mutant"];
    public static ImmutableArray<string> StageIds { get; }=["day1-visible-with-flag","day2-visible-no-flag","day3-hidden-submerged","day4-hidden-submerged","day5-hidden-submerged","day6-platform-removed-former-site-probe"];
    public const string Guidance="Confirmed protocol: SAME animal across six consecutive days; four trials/day on days1–5, one day6 probe (21 expected slots per actual animal). No mice, recordings, durations or confirmations inferred. Day1 visible WITH flag; day2 visible NO flag; days3–5 hidden/submerged; day6 platform removed. Day6 ROI is FORMER platform location; site entry never implies mounting. Inspect flag-related settings locally.";
    public static bool PopulateFresh(ExperimentPlanDraft draft)
    {
        if(draft.Animals.Count!=0||draft.Contexts.Count!=0||draft.Trials.Count!=0||draft.Title.Length!=0||draft.PrimaryComparison.Length!=0||draft.DesignConfirmed||draft.RosterConfirmed||draft.UnitDesign!=StudyUnitDesign.Unknown)return false;
        draft.Title="Luciana six-day protocol — unconfirmed local setup";
        for(var day=1;day<=6;day++)draft.Contexts.Add(new(){Id=StageIds[day-1],Day=day,Phase=day==6?TrialMode.Probe:TrialMode.Training});
        return true;
    }
    public static bool AssignGroup(ExperimentPlanDraft draft,IEnumerable<ExperimentAnimalDraft> selected,string group)
    {
        if(!Groups.Contains(group,StringComparer.Ordinal))return false;
        var rows=selected.Where(a=>draft.Animals.Contains(a)&&!string.IsNullOrWhiteSpace(a.Id)).Distinct().ToArray();if(rows.Length==0)return false;
        foreach(var animal in rows){animal.Group=group;animal.AllocationConfirmed=false;}
        return true;
    }
    public static bool AppendExpectedTrials(ExperimentPlanDraft draft,ExperimentAnimalDraft animal,out int added)
    {
        added=0;if(!draft.Animals.Contains(animal)||string.IsNullOrWhiteSpace(animal.Id)||draft.Animals.Count(a=>a.Id.Equals(animal.Id,StringComparison.OrdinalIgnoreCase))!=1)return false;
        var stages=new List<ExperimentContextDraft>();
        for(var day=1;day<=6;day++){var matches=draft.Contexts.Where(c=>c.Id.Equals(StageIds[day-1],StringComparison.OrdinalIgnoreCase)).ToArray();if(matches.Length!=1||matches[0].Day!=day||matches[0].Phase!=(day==6?TrialMode.Probe:TrialMode.Training))return false;stages.Add(matches[0]);}
        var existing=draft.Trials.Where(t=>t.AnimalId.Equals(animal.Id,StringComparison.OrdinalIgnoreCase)&&stages.Any(c=>c.Id.Equals(t.ContextId,StringComparison.OrdinalIgnoreCase))).ToArray();
        if(existing.GroupBy(t=>(t.ContextId.ToUpperInvariant(),t.Order)).Any(g=>g.Count()>1))return false;
        var ids=draft.Trials.Select(t=>t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var stage in stages)for(var order=1;order<=(stage.Day==6?1:4);order++)
        {
            if(existing.Any(t=>t.ContextId.Equals(stage.Id,StringComparison.OrdinalIgnoreCase)&&t.Order==order))continue;
            var basis="luciana-slot:"+animal.Id.Length+":"+animal.Id+":day"+stage.Day+":trial"+order;var id=basis;var suffix=1;while(!ids.Add(id))id=basis+":expected"+suffix++;
            draft.Trials.Add(new(){Id=id,AnimalId=animal.Id,ContextId=stage.Id,Order=order,MissingReason="Expected human protocol trial; recording not assigned."});added++;
        }
        return true;
    }
}
