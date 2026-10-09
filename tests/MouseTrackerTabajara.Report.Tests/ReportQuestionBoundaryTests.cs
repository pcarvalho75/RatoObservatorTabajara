using System.Collections.Immutable;
using System.Xml.Linq;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using MouseTrackerTabajara.Core;
using Xunit;

namespace MouseTrackerTabajara.Report.Tests;

public sealed partial class WholeStudyReportTests
{
    [Fact] public void ProbeMountingDoesNotLicenseTaskMeansAndTruncatedExposureStaysHeld()
    {
        using var f=new Fixture();
        for(var i=0;i<4;i++){f.Store.RecordBiologicalEvent(f.Assets[i],BiologicalEventKind.PlatformMounting,20+i,"Synthetic probe annotation","Reviewer");f.Store.RecordReview(f.Runs[i],true);}
        var q=f.Compute().Document.Contexts[0].Questions!;var animal=q.Animals.Single(a=>a.Animal=="A"&&a.Endpoint=="adjacent speed");Assert.Equal(1d/27,animal.Value!.Value,10);Assert.Equal(2.5,animal.WindowSeconds);Assert.DoesNotContain(animal.Support,s=>s.Contains("confirmed-mounting training trials"));
        var plans=new StudyPlanStore(f.Store);var saved=plans.Latest()!;
        plans.Save(saved.Plan with{Contexts=saved.Plan.Contexts.Select((c,i)=>i==0?c with{RequestedExposureSeconds=5,ComparisonDurationSeconds=4.5,TemporalBinEdges=[0,1,4.5]}:c).ToImmutableArray()},"Reviewer","Truncated fixed probe counterexample",saved.Revision);
        for(var i=0;i<4;i++){f.Store.SaveProtocolWindow(f.Assets[i],new(){RequestedExposureSeconds=5,ReleaseConfirmed=true,ReleaseFrame=5,WindowConfirmed=true,Reason="Synthetic fixed probe longer than source",Reviewer="Reviewer"});f.Store.RecordReview(f.Runs[i],true);}
        var c=f.Compute().Document.Contexts[0];animal=c.Questions!.Animals.Single(a=>a.Animal=="A"&&a.Endpoint=="adjacent speed");Assert.Null(animal.Value);Assert.Equal(0,animal.SupportedTrials);Assert.All(c.Trials.Where(t=>t.Animal=="A"),t=>Assert.Equal("Source truncated",t.TerminationReason));
    }
    [Fact] public void DifferentConfirmedTrainingTerminationsAverageCompleteTaskTrialsAndHoldMissingEvidence()
    {
        using var f=new Fixture();var plans=new StudyPlanStore(f.Store);var saved=plans.Latest()!;
        var slots=saved.Plan.Trials;
        for(var i=0;i<8;i++)
        {
            var asset=f.Assets[i] with{Phase=TrialMode.Training,AnimalId=i<4?"A":"B",GroupName=i<4?"WT":"KO",Trial=i%4+1,Settings=f.Assets[i].Settings with{Rx=i<4?.45:.225}};f.Store.SaveVideo(asset);
            var run=f.Runs[i] with{Id=Guid.NewGuid().ToString("N"),Settings=asset.Settings,Metrics=MetricCalculator.Calculate(f.Runs[i].Points,asset.Metadata,asset.Settings)};f.Store.SaveRun(run);
            f.Store.RecordBiologicalEvent(asset,BiologicalEventKind.PlatformMounting,20+i%4,"Synthetic confirmed mounting","Reviewer");
            f.Store.SaveProtocolWindow(asset,new(){RequestedExposureSeconds=3,ReleaseConfirmed=true,ReleaseFrame=5,WindowConfirmed=true,Reason="Synthetic confirmed training",Reviewer="Reviewer"});f.Store.RecordReview(run,true);
            slots=slots.Select(t=>t.RecordingId==asset.Id?t with{AnimalId=asset.AnimalId,Order=asset.Trial,ReferenceSettingsHash=StudyStore.SettingsHash(asset.Settings)}:t).ToImmutableArray();
        }
        plans.Save(saved.Plan with{Contexts=saved.Plan.Contexts.Select((c,i)=>i==0?c with{Phase=TrialMode.Training}:c).ToImmutableArray(),Animals=saved.Plan.Animals.Select(a=>a.Id=="B"?a with{Group="KO"}:a).ToImmutableArray(),Trials=slots},"Reviewer","Confirmed training termination reproduction",saved.Revision);
        var c=f.Compute().Document.Contexts[0];var q=c.Questions!;
        var trials=q.Trials.Where(t=>t.Animal=="A"&&t.Endpoint=="adjacent speed").ToArray();Assert.Equal(4,trials.Length);Assert.All(trials,t=>{output.WriteLine(string.Join(" | ",t.Support));Assert.NotNull(t.Value);});Assert.Equal(new[]{1d,1.1,1.2,1.3},trials.Select(t=>Math.Round(t.WindowSeconds,10)));
        var animal=q.Animals.Single(a=>a.Animal=="A"&&a.Endpoint=="adjacent speed");Assert.Equal(4,animal.SupportedTrials);Assert.Equal(1d/27,animal.Value!.Value,10);Assert.Null(animal.WindowSeconds);Assert.Contains(animal.Support,s=>s.Contains("not a fixed-time comparison"));Assert.False(animal.Excluded);
        var expectedProximity=Enumerable.Range(20,4).Select(end=>Enumerable.Range(10,end-10).Where(frame=>frame!=12).Average(frame=>(95-frame)/270d)).Average();
        Assert.Equal(expectedProximity,q.Animals.Single(a=>a.Animal=="A"&&a.Endpoint=="proximity").Value!.Value,10);
        var group=q.Groups.Single(g=>g.Group=="WT"&&g.Endpoint=="adjacent speed");Assert.Equal(1d/27,group.Mean!.Value,10);Assert.Null(group.WindowSeconds);Assert.Contains(group.Support,s=>s.Contains("actual task exposures may differ"));
        Assert.Equal(2d/27,q.Groups.Single(g=>g.Group=="KO"&&g.Endpoint=="adjacent speed").Mean!.Value,10);
        Assert.Equal(1d/27,q.Differences.Single(d=>d.GroupA=="KO"&&d.GroupB=="WT"&&d.Endpoint=="adjacent speed").Difference!.Value,10);
        Assert.Null(q.Animals.Single(a=>a.Animal=="A"&&a.Endpoint=="site occupancy").Value);
        Assert.Contains("actual task exposures may differ",ReportQuestionExport.Payloads(f.Compute().Document)["GROUP_SCALARS.csv"]);
        Assert.Contains("not a fixed-time comparison",ReportQuestionExport.Html(f.Compute().Document));
        Exception? nativeError=null;var native=new Thread(()=>{try{
            ReportTrial? traced=null;var window=new System.Windows.Window{Content=ReportQuestionDashboard.Build(c,c,t=>traced=t)};
            try{Layout(window,1050,700);Assert.Contains(Find<System.Windows.Controls.TextBlock>(window),t=>t.Text.Contains("not a fixed-time comparison"));Assert.Contains(Find<System.Windows.Controls.TextBlock>(window),t=>t.Text.Contains("0.037 pool diameters/s"));Assert.Contains(Find<System.Windows.Controls.TextBlock>(window),t=>t.Text.Contains("Confirmed task durations; no fixed window"));Assert.DoesNotContain(Find<System.Windows.Controls.TextBlock>(window),t=>t.Text.Contains("helds"));
                var selector=Find<System.Windows.Controls.ComboBox>(window).Single(b=>Equals(b.Tag,"group-question-endpoint-selector"));selector.SelectedItem="proximity";Layout(window,1050,700);Assert.Contains(Find<System.Windows.Controls.TextBlock>(window),t=>t.Text.Contains("actual task exposures may differ"));
                Find<System.Windows.Controls.Button>(window).First().RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Assert.NotNull(traced);Assert.NotNull(traced.VideoId);
            }finally{window.Close();}
        }catch(Exception ex){nativeError=ex;}});native.SetApartmentState(ApartmentState.STA);native.Start();Assert.True(native.Join(TimeSpan.FromMinutes(1)));if(nativeError is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(nativeError).Throw();
        var roundtrip=JsonDefaults.Deserialize<StudyReportDocument>(JsonDefaults.Serialize(f.Compute().Document));Assert.Equal(JsonDefaults.Serialize(f.Compute().Document.Contexts),JsonDefaults.Serialize(roundtrip.Contexts));
        var computed=f.Compute();var destination=System.IO.Path.Combine(f.Folder,"confirmed-task-publication");StudyReportPublication.Publish(destination,computed.Document,computed.Material);var savedReport=StudyReportPublication.Read(destination);using(var material=savedReport.Material!.Reconstruct()){var replay=StudyReportComputation.Compute(material.Snapshot,savedReport.Document.Contexts.Select(x=>x.Id));Assert.Equal(JsonDefaults.Serialize(savedReport.Document.Contexts),JsonDefaults.Serialize(replay.Document.Contexts));}
        Assert.All(c.Trials.Where(t=>t.Animal=="A"),t=>Assert.Contains("mounting",t.TerminationReason));
        f.Store.SaveProtocolWindow(f.Assets[0] with{Phase=TrialMode.Training},new(){RequestedExposureSeconds=3,ReleaseConfirmed=false,ReleaseFrame=5,WindowConfirmed=true,Reason="Synthetic unconfirmed release",Reviewer="Reviewer"});
        var held=f.Compute().Document.Contexts[0].Questions!.Animals.Single(a=>a.Animal=="A"&&a.Endpoint=="adjacent speed");Assert.Null(held.Value);Assert.Equal(3,held.SupportedTrials);
        saved=plans.Latest()!;plans.Save(saved.Plan with{Trials=saved.Plan.Trials.Select(t=>t.RecordingId==f.Assets[0].Id?t with{RecordingId=null,SourceReferenceConfirmed=false,ReferenceSourceSha256="",ReferenceSettingsHash="",MissingReason="Synthetic missing recording"}:t).ToImmutableArray()},"Reviewer","Missing expected training repeat",saved.Revision);
        held=f.Compute().Document.Contexts[0].Questions!.Animals.Single(a=>a.Animal=="A"&&a.Endpoint=="adjacent speed");Assert.Null(held.Value);Assert.Equal(4,held.ExpectedTrials);Assert.Equal(3,held.SupportedTrials);
    }
    [Fact] public void OneExcludedRepeatHoldsMouseWithoutExcludingItAndDensityRetainsScope()
    {
        using var f=new Fixture();f.Store.SaveVideo(f.Assets[0] with{Included=false,ExclusionReason="Synthetic excluded repeat"});
        var document=f.Compute().Document;var q=document.Contexts[0].Questions!;
        var animal=q.Animals.Single(a=>a.Animal=="A"&&a.Endpoint=="adjacent speed");
        Assert.False(animal.Excluded);Assert.Null(animal.Value);Assert.Equal(4,animal.ExpectedTrials);Assert.Equal(3,animal.SupportedTrials);
        Assert.Single(q.Trials.Where(t=>t.Animal=="A"&&t.Endpoint=="adjacent speed"&&t.Excluded));
        var group=q.Groups.Single(g=>g.Group=="WT"&&g.Endpoint=="adjacent speed");Assert.Equal(1,group.HeldAnimals);Assert.Equal(0,group.ExcludedAnimals);Assert.Equal(1,group.ExcludedTrials);
        var density=q.Densities.Single(d=>d.Trial=="PRIVATE-trial-0");Assert.Contains("held",density.Support);
        var payload=ReportQuestionExport.Payloads(document);var svg=payload["context-001-trial-001_DENSI.svg"];
        Assert.Contains(density.Support,svg);Assert.Contains("planned window 2.5 s",svg);Assert.Equal("svg",XDocument.Parse(svg).Root!.Name.LocalName);
        Assert.Contains(density.Support,ReportQuestionExport.Html(document));
        var escaped=ReportQuestionExport.DensitySvg("Synthetic",density.KnownFractions,density.UnknownFraction,document.Contexts[0],support:"Held <scope> & prerequisite");
        Assert.Contains("Held <scope> & prerequisite",XDocument.Parse(escaped).Root!.Value);
        var path=q.Paths.Single(p=>p.Trial=="PRIVATE-trial-0");var first=path.Segments.First().Vertices.First();
        var synthetic=path with{Segments=[new([first with{X=0,Y=0},first with{X=100,Y=100}])]};
        var fit=XDocument.Parse(ReportQuestionExport.PathSvg(synthetic,document.Contexts[0]));
        var points=fit.Descendants().Single(e=>e.Name.LocalName=="polyline").Attribute("points")!.Value.Split(' ').Select(p=>p.Split(',').Select(s=>double.Parse(s,System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToArray();
        Assert.Equal(170,points[1][0]-points[0][0],10);Assert.Equal(170,points[1][1]-points[0][1],10);
        foreach(var name in new[]{"context-001_GROUP_DENSI.svg","context-001_GROUP_DIFFERENCE.svg"}){Assert.Contains("map animals expected/included/held/excluded",payload[name]);Assert.Contains("Probe day 1",payload[name]);XDocument.Parse(payload[name]);}
        var context=document.Contexts[0];Assert.Contains("WT map animals expected/included/held/excluded 5/4/1/0",payload["context-001_GROUP_DENSI.svg"]);Assert.Contains("KO map animals expected/included/held/excluded 5/2/2/1",payload["context-001_GROUP_DENSI.svg"]);
        var unrelated=context with{Animals=context.Animals.Add(context.Animals[0] with{Id="unrelated",Group="UNRELATED"}),Trials=context.Trials.Add(context.Trials[0] with{Id="unrelated-trial",Animal="unrelated"})};
        var pairScope=XDocument.Parse(ReportQuestionExport.DifferenceSvg(unrelated)).Descendants().Where(e=>e.Name.LocalName=="desc").Single(e=>e.Value.Contains("WT map animals")&&e.Value.Contains("KO map animals")).Value;
        Assert.Contains("WT map animals expected/included/held/excluded 5/4/1/0",pairScope);Assert.Contains("KO map animals expected/included/held/excluded 5/2/2/1",pairScope);Assert.DoesNotContain("UNRELATED",pairScope);
        var blind=StudyReportComputation.Project(document,f.Plan,true);foreach(var text in ReportQuestionExport.Payloads(blind).Values){Assert.DoesNotContain("PRIVATE",text);Assert.DoesNotMatch(@"\b(WT|KO)\b",text);Assert.DoesNotContain("FREE",text);}
        var plans=new StudyPlanStore(f.Store);var saved=plans.Latest()!;plans.Save(saved.Plan with{Animals=saved.Plan.Animals.Select(a=>a.Id=="A"?a with{Excluded=true,ExclusionReason="Excluded by researcher"}:a).ToImmutableArray()},"Reviewer","Whole-animal exclusion control",saved.Revision);
        Assert.True(f.Compute().Document.Contexts[0].Questions!.Animals.Single(a=>a.Animal=="A"&&a.Endpoint=="adjacent speed").Excluded);
    }
}
