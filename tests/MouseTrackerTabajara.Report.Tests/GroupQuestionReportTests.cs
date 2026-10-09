using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;

namespace MouseTrackerTabajara.Report.Tests;

public sealed partial class WholeStudyReportTests
{
    private static void MakeUnequalQuestionControls(Fixture fixture)
    {
        var plans=new StudyPlanStore(fixture.Store);var saved=plans.Latest()!;var changed=saved.Plan.Trials;
        for(var i=0;i<4;i++)
        {var asset=fixture.Assets[i] with{Settings=fixture.Assets[i].Settings with{Rx=.15}};fixture.Store.SaveVideo(asset);var run=fixture.Runs[i] with{Id=Guid.NewGuid().ToString("N"),Settings=asset.Settings,Metrics=MetricCalculator.Calculate(fixture.Runs[i].Points,asset.Metadata,asset.Settings)};fixture.Store.SaveRun(run);fixture.Store.RecordReview(run,true);changed=changed.Select(t=>t.RecordingId==asset.Id?t with{ReferenceSettingsHash=StudyStore.SettingsHash(asset.Settings)}:t).ToImmutableArray();}
        plans.Save(saved.Plan with{Trials=changed},"Reviewer","Known normalized unequal-repeat scalar controls",saved.Revision);
    }
    [Fact] public void GroupQuestionsUseEqualMiceAcceptedWindowsAndAllPdfPayloads()
    {
        using var fixture=new Fixture();MakeUnequalQuestionControls(fixture);var computed=fixture.Compute();var c=computed.Document.Contexts[0];var q=c.Questions!;
        var speed=q.Groups.Single(g=>g.Group=="WT"&&g.Endpoint=="adjacent speed");Assert.Equal(7d/135,speed.Mean!.Value,10);Assert.NotEqual(5d/81,speed.Mean.Value);Assert.Equal(5,speed.IncludedAnimals);Assert.Equal(12,speed.ExpectedTrials);Assert.Equal(2.5,speed.WindowSeconds);
        Assert.Equal(77d/675,q.Groups.Single(g=>g.Group=="WT"&&g.Endpoint=="observed adjacent distance").Mean!.Value,10);
        Assert.Equal(2.4,q.Groups.Single(g=>g.Group=="WT"&&g.Endpoint=="source quadrant SW time").Mean!.Value,10);Assert.Contains("source quadrant SW time",ReportQuestionExport.Payloads(computed.Document)["AREA_TIME.csv"]);
        Assert.Contains(q.Trials,t=>t.Trial=="PRIVATE-missing"&&t.Value is null&&!t.Support.IsEmpty);Assert.Contains(q.Animals,a=>a.Animal=="PRIVATE-animal-7"&&a.Value is null);Assert.Contains(q.Groups,g=>g.ExcludedAnimals>0&&g.HeldAnimals>0);
        var coords=q.Coordinates.Where(p=>p.Trial=="PRIVATE-trial-0").ToArray();Assert.Equal(25,coords.Length);Assert.Equal(10,coords[0].Frame);Assert.Equal(1,coords[0].SourceSeconds);Assert.Equal(.5,coords[0].AfterReleaseSeconds);Assert.Equal(10,coords[0].Fps);
        Assert.Null(coords.Single(p=>p.Frame==12).X);Assert.Null(coords.Single(p=>p.Frame==12).AdjacentDistance);Assert.Null(coords.Single(p=>p.Frame==13).AdjacentDistance);Assert.Equal(1d/90,coords.Single(p=>p.Frame==14).AdjacentDistance!.Value,10);
        var path=q.Paths.Single(p=>p.Trial=="PRIVATE-trial-0");Assert.True(path.Segments.Length>=2);Assert.DoesNotContain(path.Segments,s=>s.Vertices.Any(v=>v.Frame==11)&&s.Vertices.Any(v=>v.Frame==13));
        var payload=ReportQuestionExport.Payloads(computed.Document);foreach(var name in new[]{"GROUP_SCALARS.csv","GROUP_SCALARS_pt-BR.csv","TRIAL_SCALARS.csv","COORD.csv","DIST.csv","AREA_RESULT.csv","AREA_TIME.csv","context-001-trial-001_PATH.svg","context-001-trial-001_PCOORD.svg","context-001-trial-001_DENSI.svg","context-001_GROUP_DENSI.svg","context-001_GROUP_DIFFERENCE.svg"})Assert.True(payload.ContainsKey(name),name);
        foreach(var svg in payload.Where(p=>p.Key.EndsWith(".svg",StringComparison.Ordinal)))Assert.Equal("svg",System.Xml.Linq.XDocument.Parse(svg.Value).Root!.Name.LocalName);
        var pt=payload["GROUP_SCALARS_pt-BR.csv"].Split('\n').First(line=>line.Contains("\"WT\";\"adjacent speed\"",StringComparison.Ordinal));Assert.Contains("\"0,051851",pt);Assert.DoesNotContain("0.051851",pt);Assert.Contains("pool diameters/s",pt);
        var protectedDoc=StudyReportComputation.Project(computed.Document,fixture.Plan,true);foreach(var output in ReportQuestionExport.Payloads(protectedDoc).Values){Assert.DoesNotContain("PRIVATE",output);Assert.DoesNotMatch(@"\b(WT|KO)\b",output);}
        var folder=Path.Combine(fixture.Folder,"questions-full");StudyReportPublication.Publish(folder,computed.Document,computed.Material);var reopened=StudyReportPublication.Read(folder);Assert.True(reopened.Manifest.FileHashes.Count>90);
        using var input=reopened.Material!.Reconstruct();var replay=StudyReportComputation.Compute(input.Snapshot,reopened.Document.Contexts.Select(x=>x.Id));Assert.Equal(JsonDefaults.Serialize(reopened.Document),JsonDefaults.Serialize(replay.Document with{FrozenUtc=reopened.Document.FrozenUtc}));
        var legacy=computed.Document with{Contexts=computed.Document.Contexts.Select(x=>x with{Questions=null}).ToImmutableArray()};var legacyFolder=Path.Combine(fixture.Folder,"questions-legacy");StudyReportPublication.Publish(legacyFolder,legacy,computed.Material);Assert.Equal(4,StudyReportPublication.Read(legacyFolder).Manifest.FileHashes.Count);Assert.Contains("Historical report lacks",ReportQuestionExport.Html(legacy));
        File.AppendAllText(Path.Combine(folder,"COORD.csv"),"tampered");Assert.Throws<InvalidDataException>(()=>StudyReportPublication.Read(folder));
    }
    [Fact] public void NativeGroupQuestionDashboardRetainsThirtySourcePublicationAndPlanIntake()
    {
        Exception? error=null;var thread=new Thread(()=>{try{NativeQuestions();}catch(Exception ex){error=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromMinutes(3)));if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    [Fact] public void ShowcaseCapturesRetainedNativeHeatmapsBoundsAndMetricSelector()
    {
        Exception? error=null;string? receipt=null;var thread=new Thread(()=>{try{receipt=ReportShowcaseCapture.Run();}catch(Exception ex){error=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromMinutes(2)));if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();output.WriteLine(receipt);
    }
    [Fact] public void ThreeGroupsFractionalWindowsAndDuplicateSourceSafeguardsRemainExplicit()
    {
        using var fixture=new Fixture();var copy=Path.Combine(fixture.Folder,"copied-original.avi");File.Copy(fixture.Assets[0].Path,copy);Assert.Equal(fixture.Assets[0].Id,new WorkspaceService(fixture.Store).Import(copy).Id);Assert.Equal(30,fixture.Store.Videos().Count);
        Assert.Throws<ArgumentException>(()=>(fixture.Plan with{Trials=fixture.Plan.Trials.Add(fixture.Plan.Trials[0] with{Id="duplicate-planned-source",Order=99})}).Validate());
        foreach(var index in new[]{12,13})fixture.Store.SaveVideo(fixture.Assets[index] with{GroupName="THIRD"});var plans=new StudyPlanStore(fixture.Store);var saved=plans.Latest()!;
        var revised=saved.Plan with{Animals=saved.Plan.Animals.Select(a=>a.Id=="PRIVATE-animal-5"?a with{Group="THIRD"}:a).ToImmutableArray(),Contexts=saved.Plan.Contexts.Select((c,i)=>i==0?c with{ComparisonStartSeconds=.55,ComparisonDurationSeconds=2.45,TemporalBinEdges=[0,.45,1.45,2.45]}:c).ToImmutableArray()};plans.Save(revised,"Reviewer","Three groups and fractional-window control",saved.Revision);
        var q=fixture.Compute().Document.Contexts[0].Questions!;Assert.Equal(3,q.Groups.Select(g=>g.Group).Distinct().Count());Assert.Equal(3,q.Differences.Count(d=>d.Endpoint=="adjacent speed"));Assert.All(q.Groups.Where(g=>g.Endpoint=="observed adjacent distance"),g=>Assert.Null(g.Mean));Assert.Contains(q.Groups,g=>g.Endpoint=="adjacent speed"&&g.Mean.HasValue);Assert.All(q.Coordinates,c=>Assert.Null(c.AdjacentDistance));Assert.Contains(q.Trials,t=>t.Endpoint=="observed adjacent distance"&&t.Support.Any(s=>s.Contains("whole decoded frames",StringComparison.Ordinal)));
    }
    private static void NativeQuestions()
    {
        using var fixture=new Fixture();MakeUnequalQuestionControls(fixture);SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var repo=new DirectoryInfo(AppContext.BaseDirectory);while(repo is not null&&!Directory.Exists(Path.Combine(repo.FullName,"src","MouseTrackerTabajara.Desktop")))repo=repo.Parent;
        var evidence=Path.Combine(repo!.FullName,".workmap","logs","S20-group-questions","synthetic-30-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(evidence);var originals=Path.Combine(evidence,"originals");Directory.CreateDirectory(originals);
        foreach(var asset in fixture.Store.Videos()){var path=Path.Combine(originals,asset.Filename);File.Copy(asset.Path,path);fixture.Store.SaveVideo(asset with{Path=path});}
        var window=new StudyReportWindow(fixture.Store.DatabasePath,true,false);
        try
        {
            Layout(window,1050,700);Click(window,"Select all contexts");Click(window,"Preview selected frozen study");Pump(window.PendingCompute);Assert.NotNull(window.DisplayedDocument);Assert.Contains(Find<TextBlock>(window),t=>t.Text.Contains("Group overview",StringComparison.Ordinal));AssertPrivate(window);
            var selector=Find<ComboBox>(window).First(c=>Equals(c.Tag,"group-question-endpoint-selector"));Assert.Equal("adjacent speed",selector.SelectedItem);Assert.Contains("source quadrant SW time",selector.Items.Cast<string>());selector.SelectedItem="site time";Layout(window,1050,700);Assert.Contains(Find<TextBlock>(window),t=>t.Text.Contains("site time",StringComparison.Ordinal));selector.SelectedItem="adjacent speed";Layout(window,1050,700);
            var trace=Find<Canvas>(window).First(c=>Equals(c.Tag,"group-question-distribution")).Children.OfType<Button>().First();trace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window.PendingSource);Assert.NotNull(window.LastSourceViewer!.DisplayedBitmap);Assert.Equal(10,window.LastSourceViewer.DisplayedFrame);
            window.ReviewerInput.Text="Reviewer";window.ReasonInput.Text="Reasoned synthetic question export";Assert.True(window.TryUnblind(window.ReviewerInput.Text,window.ReasonInput.Text));window.DestinationInput.Text=Path.Combine(evidence,"publication-full");Click(window,"Publish frozen HTML CSV and input");Pump(window.PendingSave);Assert.Contains("Completed",window.StatusText);
            var publication=StudyReportPublication.Read(window.DestinationInput.Text);Assert.Equal(30,publication.Document.SourceAuthentication.Length);Assert.Equal(31,publication.Document.Contexts.Sum(c=>c.Trials.Length));Assert.True(File.Exists(Path.Combine(window.DestinationInput.Text,"GROUP_SCALARS_pt-BR.csv")));Assert.True(Directory.GetFiles(window.DestinationInput.Text,"*_PATH.svg").Length==31);
            Click(window,"Inspect saved historical package");Click(window,"Authenticate and replay frozen inputs");Pump(window.PendingCompute);Assert.Contains("PASS",window.StatusText);
            window.Blinded=true;AssertPrivate(window);window.Preview();Pump(window.PendingCompute);window.DestinationInput.Text=Path.Combine(evidence,"publication-blind");window.Save();Pump(window.PendingSave);Assert.True(StudyReportPublication.Read(window.DestinationInput.Text).Document.Redacted);
            foreach(var size in new[]{new System.Windows.Size(1050,700),new System.Windows.Size(1440,960)}){Layout(window,size.Width,size.Height);var root=(FrameworkElement)window.Content;window.Content=null;try{using var hwnd=new HwndSource(new HwndSourceParameters("s20-synthetic"){WindowStyle=0,ExtendedWindowStyle=0x08000000,PositionX=-32000,PositionY=-32000,Width=(int)size.Width,Height=(int)size.Height}){RootVisual=root};root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();Pump(Task.Delay(60));var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(evidence,$"synthetic-group-dashboard-{size.Width}x{size.Height}.png"));png.Save(stream);hwnd.RootVisual=null;}finally{window.Content=root;}}
            File.WriteAllText(Path.Combine(evidence,"PASS.txt"),"Actual native30-source/2-group unequal-repeat dashboard, source trace, full/blind publication, all PDF payloads, pt-BR CSV and authenticated replay PASS. Original synthetic videos retained for controller reconstruction.");
        }
        finally{window.Close();}
        var empty=new StudyStore(Path.Combine(fixture.Folder,"empty-study.morrisdb"));var intake=new StudyReportWindow(empty.DatabasePath,true,false);
        try{Layout(intake,1050,700);Assert.Contains("Create expected study plan",intake.StatusText);Click(intake,"Create expected study plan");Assert.NotNull(intake.LastPlanEditor);Assert.Empty(intake.LastPlanEditor!.Draft.Animals);Assert.Contains(Find<TextBlock>(intake),t=>t.Text.Contains("Files are not animal N",StringComparison.Ordinal));}finally{intake.Close();}
    }
}
