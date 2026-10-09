using System.Collections.Immutable;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.SpatialViews;

namespace MouseTrackerTabajara.Desktop;

public sealed record QuestionScalar(string Trial,string Animal,string Group,string Endpoint,string Unit,double? Value,double WindowSeconds,bool Excluded,ImmutableArray<string> Support);
public sealed record QuestionAnimal(string Animal,string Group,string Endpoint,string Unit,int ExpectedTrials,int SupportedTrials,double? Value,double? WindowSeconds,bool Excluded,ImmutableArray<string> Support);
public sealed record QuestionGroup(string Group,string Endpoint,string Unit,int ExpectedAnimals,int IncludedAnimals,int HeldAnimals,int ExcludedAnimals,int ExpectedTrials,int SupportedTrials,int ExcludedTrials,double? Mean,double? Minimum,double? Maximum,double? WindowSeconds,ImmutableArray<string> Contributors,ImmutableArray<string> Support);
public sealed record QuestionDifference(string GroupA,string GroupB,string Endpoint,string Unit,double? Difference,string Support);
public sealed record QuestionCoordinate(string Trial,string Animal,string VideoId,string SourceHash,string RunId,int Frame,double SourceSeconds,double? AfterReleaseSeconds,double? AfterComparisonSeconds,double Fps,int Width,int Height,int? RawX,int? RawY,string RawStatus,int? X,int? Y,string Status,double? AdjacentDistance,bool? SitePresence,bool? WallPresence,string? Quadrant,bool Excluded,string Scope);
public sealed record QuestionPath(string Trial,ImmutableArray<TrajectorySegment> Segments,int Width,int Height,string Support);
public sealed record QuestionDensity(string Trial,ImmutableArray<double> KnownFractions,double UnknownFraction,string Support);
public sealed record ReportQuestionData(ImmutableArray<QuestionScalar> Trials,ImmutableArray<QuestionAnimal> Animals,ImmutableArray<QuestionGroup> Groups,ImmutableArray<QuestionDifference> Differences,ImmutableArray<QuestionCoordinate> Coordinates,ImmutableArray<QuestionPath> Paths,ImmutableArray<QuestionDensity> Densities);

/// <summary>Report projection of accepted temporal/window/geometry producers. No detector, gap bridge or physical calibration inference.</summary>
public static class ReportQuestions
{
    private const string TrainingTaskScope="Complete confirmed-mounting training trials: equal trial means within each animal, then equal animals; actual task exposures may differ. This is not a fixed-time comparison; individual durations remain in trial CSV.";
    private static bool TaskEndpoint(string endpoint)=>endpoint is "adjacent speed" or "proximity";
    public static ReportQuestionData Build(ExperimentResult result,TemporalStudy temporal,CancellationToken cancellation)
    {
        var scalars=ImmutableArray.CreateBuilder<QuestionScalar>();var coordinates=ImmutableArray.CreateBuilder<QuestionCoordinate>();var paths=ImmutableArray.CreateBuilder<QuestionPath>();var densities=ImmutableArray.CreateBuilder<QuestionDensity>();
        var context=result.Context;var plan=result.Plan;var confirmedTaskTrials=new HashSet<string>(StringComparer.Ordinal);
        var duplicated=result.Trials.Where(t=>t.Recording is not null).GroupBy(t=>t.Recording!.Asset.SourceSha256,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var trial in result.Trials)
        {
            cancellation.ThrowIfCancellationRequested();var animal=plan.Animals.Single(a=>a.Id.Equals(trial.Slot.AnimalId,StringComparison.OrdinalIgnoreCase));var row=trial.Recording;AnalysisRun? run=null;
            try{if(row?.RawRun is {} recorded&&CompletedRecordingContract.TryValidate(recorded,out _))run=row.EffectiveRun();}catch(Exception){run=null;}
            var whole=temporal.AnimalDays.Where(d=>d.ContextId==context.Id).SelectMany(d=>d.Trials).SingleOrDefault(t=>t.TrialId==trial.Slot.Id);
            var excluded=animal.Excluded||row?.Asset.Included==false;var common=ImmutableArray.CreateBuilder<string>();
            if(whole is null||!whole.EligibleObservation)common.Add("Confirmed whole-trial window/source/inspection support unavailable; complete expected repeats remain required.");
            if(excluded)common.Add("Excluded participant/trial retained.");if(row is not null&&duplicated.Contains(row.Asset.SourceSha256))common.Add("Duplicate source bytes cannot supply another trial or animal weight.");
            var duration=whole is null?0:whole.EndSeconds-whole.StartSeconds;
            WindowedTrialMeasurement? windowed=null;ReleasedComparisonWindow? window=null;
            if(row is not null&&run is not null)
            {
                window=ReleasedComparisonWindow.Resolve(ExperimentPlanAdapter.Protocol(result.Snapshot,row.Asset.Id),context,run.Metadata,ExperimentPlanAdapter.EffectiveEvents(result.Snapshot,row.Asset.Id),row.Asset.Phase,ExperimentPlanAdapter.CurrentInspection(row));
                windowed=RegionScenarioComparison.Measure(run.Points,run.Metadata,run.Settings with{DiameterCm=null},window,"report confirmed window");
                if(context.Phase==TrialMode.Training&&whole?.EligibleObservation==true&&window.Supported&&window.TerminationReason=="Confirmed platform mounting")confirmedTaskTrials.Add(trial.Slot.Id);
            }
            void Add(string endpoint,string unit,double? value,string missing)
            {var holds=common.ToImmutable();if(!value.HasValue)holds=holds.Add(missing);scalars.Add(new(trial.Slot.Id,animal.Id,animal.Group,endpoint,unit,holds.IsEmpty?value:null,duration,excluded,holds));}
            Add("site occupancy","fraction",whole?.TargetOccupancyFraction,"Platform site/accepted position exposure unavailable.");
            Add("proximity","pool diameters",whole?.MeanProximityDiameters,"Platform site/accepted position exposure unavailable.");
            Add("adjacent speed","pool diameters/s",whole?.AdjacentSpeedDiametersPerSecond,"Adjacent accepted-motion exposure unavailable.");
            Add("wall presence","observed fraction",whole?.WallObservedFraction,"Accepted position exposure unavailable.");
            Add("observed adjacent distance","pool diameters",windowed?.Comparison?.Distance,windowed?.DistanceNote??"Window distance is held; no fractional/gap bridge distance invented.");
            Add("site time","seconds",windowed?.Comparison?.PlatformSiteSeconds,"Configured site/confirmed window unavailable.");
            Add("wall time","seconds",whole?.WallObservedFraction is { } wall?wall*whole.KnownSeconds:null,"Accepted position exposure unavailable.");
            Add("known position time","seconds",whole?.KnownSeconds,"Confirmed window unavailable.");
            Measurements? areaWindow=null;
            if(run is not null&&window?.Supported==true&&windowed?.FractionalBoundary==false&&context.Orientation==StudyOrientation.Room&&trial.Slot.RoomRotationRadians==0)
            {var slice=run.Points.Where(p=>p.Frame/run.Metadata.Fps>=window.OriginSeconds-1e-9&&(p.Frame+1d)/run.Metadata.Fps<=window.OriginSeconds+window.PlannedSeconds+1e-9).ToArray();if(slice.Length>0)areaWindow=MetricCalculator.Calculate(slice,run.Metadata,run.Settings with{DiameterCm=null});}
            foreach(var quadrant in new[]{"NW","NE","SW","SE"})Add("source quadrant "+quadrant+" time","seconds",areaWindow?.QuadrantSeconds.GetValueOrDefault(quadrant),"Whole-frame confirmed window and unrotated room/source quadrant scope required; fractional/aligned quadrant time not invented.");
            if(row is null||run is null){paths.Add(new(trial.Slot.Id,[],0,0,"Planned source/run missing; no coordinate or trajectory zero substituted."));continue;}
            var origin=window?.Supported==true?window.OriginSeconds:run.Points.FirstOrDefault()?.TimeSeconds??0;var end=window?.Supported==true?origin+window.PlannedSeconds:run.Points.LastOrDefault()?.TimeSeconds+1/run.Metadata.Fps??origin;
            var release=ExperimentPlanAdapter.Protocol(result.Snapshot,row.Asset.Id)?.ReleaseFrame;var selected=run.Points.Where(p=>(p.Frame+1d)/run.Metadata.Fps>origin+1e-9&&p.TimeSeconds<end-1e-9).ToArray();var raw=row.RawRun!.Points.ToDictionary(p=>p.Frame);TrackPoint? previous=null;
            foreach(var point in selected)
            {
                cancellation.ThrowIfCancellationRequested();var original=raw[point.Frame];Measurements? singleton=point.Accepted?MetricCalculator.Calculate([point],run.Metadata,run.Settings with{DiameterCm=null}):null;
                double? step=previous is {Accepted:true}&&point.Accepted&&point.Frame==previous.Frame+1&&window?.Supported==true&&!windowed!.FractionalBoundary?MetricCalculator.Calculate([previous,point],run.Metadata,run.Settings with{DiameterCm=null}).Distance:null;
                coordinates.Add(new(trial.Slot.Id,animal.Id,row.Asset.Id,row.Asset.SourceSha256,row.NavigationId,point.Frame,point.TimeSeconds,release.HasValue&&ExperimentPlanAdapter.Protocol(result.Snapshot,row.Asset.Id)?.ReleaseConfirmed==true?point.TimeSeconds-release.Value/run.Metadata.Fps:null,window?.Supported==true?point.TimeSeconds-origin:null,run.Metadata.Fps,run.Metadata.Width,run.Metadata.Height,original.X,original.Y,original.Status.ToString(),point.X,point.Y,point.Status.ToString(),step,point.Accepted&&run.Settings.Platform is not null?ArenaGeometry.InsidePlatformSite(point.X!.Value,point.Y!.Value,run.Settings,run.Metadata.Width,run.Metadata.Height):null,singleton?.WallObservedPercent is { } presence?presence==100:null,singleton?.QuadrantSeconds.Single(q=>q.Value>0).Key,excluded,window?.Supported==true?"confirmed effective comparison window; stored inspected corrected coordinates, raw columns retained":"raw decoder interval only; comparison held"));previous=point;
            }
            var pointsByFrame=selected.ToDictionary(p=>p.Frame);var input=trial.Map?.Input;
            if(input is not null&&window?.Supported==true)input=input with{PlannedSeconds=window.PlannedSeconds,Frames=input.Frames.Where(f=>pointsByFrame.ContainsKey(f.Frame)).ToImmutableArray()};
            var segments=input is null?ImmutableArray<TrajectorySegment>.Empty:SpatialCalculator.Trajectory(input).Select(s=>new TrajectorySegment(s.Vertices.Where(v=>pointsByFrame.ContainsKey(v.Frame)).Select(v=>v with{X=pointsByFrame[v.Frame].X!.Value,Y=pointsByFrame[v.Frame].Y!.Value}).ToImmutableArray())).Where(s=>!s.Vertices.IsEmpty).ToImmutableArray();
            paths.Add(new(trial.Slot.Id,segments,run.Metadata.Width,run.Metadata.Height,input is not null?"Accepted normalized-producer gap segments; displayed original source pixels; no unknown bridge.":"Stored coordinates only; trajectory scope/source unavailable."));
            if(input is not null&&!input.Frames.IsEmpty){var density=SpatialCalculator.Trial(input);densities.Add(new(trial.Slot.Id,density.KnownFractions,density.UnknownFraction,(common.Count==0?"Known-position fraction of effective window; normalized pool-radii grid/orientation, unknown mass retained.":"Stored position density only; endpoint/group support held.")+FormattableString.Invariant($" Source interval {origin:R} to {end:R} s; {(window?.Supported==true?"effective comparison exposure":"raw decoder scope; comparison held")}.")));}
        }
        var animals=scalars.GroupBy(t=>(t.Animal,t.Group,t.Endpoint,t.Unit)).Select(g=>
        {
            var rows=g.ToArray();var holds=rows.SelectMany(r=>r.Support).Distinct().ToImmutableArray();
            var task=TaskEndpoint(g.Key.Endpoint)&&rows.All(r=>confirmedTaskTrials.Contains(r.Trial));var varied=rows.Select(r=>r.WindowSeconds).Distinct().Count()>1;
            if(varied&&!task)holds=holds.Add("Expected repeats have different effective windows; mean held.");
            var value=holds.IsEmpty?rows.Average(r=>r.Value!.Value):(double?)null;
            return new QuestionAnimal(g.Key.Animal,g.Key.Group,g.Key.Endpoint,g.Key.Unit,rows.Length,rows.Count(r=>r.Value.HasValue),value,value.HasValue&&!varied?rows[0].WindowSeconds:null,plan.Animals.Single(a=>a.Id.Equals(g.Key.Animal,StringComparison.OrdinalIgnoreCase)).Excluded,task&&value.HasValue?holds.Add(TrainingTaskScope):holds);
        }).ToImmutableArray();
        var groups=animals.GroupBy(a=>(a.Group,a.Endpoint,a.Unit)).Select(g=>
        {
            var rows=g.ToArray();var valid=rows.Where(a=>a.Value.HasValue&&!a.Excluded).ToArray();var holds=ImmutableArray.CreateBuilder<string>();var task=valid.Length>0&&valid.All(a=>a.Support.Contains(TrainingTaskScope));
            if(valid.Length==0)holds.Add("No supported complete-repeat animals.");if(valid.Select(a=>a.WindowSeconds).Distinct().Count()>1&&!task)holds.Add("Included animals have different effective windows; group mean held.");
            return new QuestionGroup(g.Key.Group,g.Key.Endpoint,g.Key.Unit,rows.Length,valid.Length,rows.Count(a=>!a.Value.HasValue&&!a.Excluded),rows.Count(a=>a.Excluded),rows.Sum(a=>a.ExpectedTrials),rows.Sum(a=>a.SupportedTrials),scalars.Count(t=>t.Group==g.Key.Group&&t.Endpoint==g.Key.Endpoint&&t.Excluded),holds.Count==0?valid.Average(a=>a.Value!.Value):null,holds.Count==0?valid.Min(a=>a.Value):null,holds.Count==0?valid.Max(a=>a.Value):null,holds.Count==0&&!task?valid[0].WindowSeconds:null,valid.Select(a=>a.Animal).ToImmutableArray(),task?holds.ToImmutable().Add(TrainingTaskScope):holds.ToImmutable());
        }).ToImmutableArray();
        var differences=ImmutableArray.CreateBuilder<QuestionDifference>();foreach(var endpoint in groups.GroupBy(g=>(g.Endpoint,g.Unit)))
        {var rows=endpoint.OrderBy(g=>g.Group,StringComparer.Ordinal).ToArray();for(var a=0;a<rows.Length;a++)for(var b=a+1;b<rows.Length;b++){var task=rows[a].Support.Contains(TrainingTaskScope)&&rows[b].Support.Contains(TrainingTaskScope);var comparable=rows[a].Mean.HasValue&&rows[b].Mean.HasValue&&(task||rows[a].WindowSeconds.HasValue&&rows[a].WindowSeconds==rows[b].WindowSeconds);differences.Add(new(rows[a].Group,rows[b].Group,endpoint.Key.Endpoint,endpoint.Key.Unit,comparable?rows[a].Mean-rows[b].Mean:null,comparable?(task?TrainingTaskScope+" ":"")+"Equal-animal conditional descriptive difference; no p-value, confidence interval or memory inference.":"Matched unit/effective window and supported animals in both groups required."));}}
        return new(scalars.ToImmutable(),animals,groups,differences.ToImmutable(),coordinates.ToImmutable(),paths.ToImmutable(),densities.ToImmutable());
    }
}
