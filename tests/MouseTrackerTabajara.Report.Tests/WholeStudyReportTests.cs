using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
using Window = System.Windows.Window;

namespace MouseTrackerTabajara.Report.Tests;

public sealed partial class WholeStudyReportTests(ITestOutputHelper output)
{
    private sealed class Fixture : IDisposable
    {
        public string Folder { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "study-report-" + Guid.NewGuid().ToString("N"));
        public StudyStore Store { get; }
        public StudyPlan Plan { get; }
        public List<VideoAsset> Assets { get; } = [];
        public List<AnalysisRun> Runs { get; } = [];
        public Fixture()
        {
            Directory.CreateDirectory(Folder); Store = new(System.IO.Path.Combine(Folder, "study.morrisdb")); var workspace = new WorkspaceService(Store);
            var contexts = new[] { new PlannedContext { Id = "PRIVATE-WT-probe-day1", Phase = TrialMode.Probe, Day = 1 }, new PlannedContext { Id = "PRIVATE-KO-probe-day2", Phase = TrialMode.Probe, Day = 2 }, new PlannedContext { Id = "PRIVATE-A-training", Phase = TrialMode.Training, Day = 1 } }
                .Select(c => c with { RequestedExposureSeconds = 3, ComparisonStartSeconds = .5, ComparisonDurationSeconds = 2.5, TemporalBinEdges = [0,.5,1.5,2.5], WindowConfirmed = true, AlignmentConfirmed = true, CommonCoordinateReference = "PRIVATE common normalized room", Orientation = StudyOrientation.Room }).ToImmutableArray();
            var animals = Enumerable.Range(0,10).Select(i => new PlannedAnimal { Id = i == 0 ? "A" : i == 1 ? "B" : "PRIVATE-animal-" + i, Group = i < 5 ? "WT" : "KO", Alias = "Informative WT KO alias", IdentityConfirmed = true, AllocationConfirmed = true, Excluded = i == 8, ExclusionReason = i == 8 ? "FREE WT KO exclusion" : "" }).ToImmutableArray();
            var trials = ImmutableArray.CreateBuilder<PlannedTrial>();
            for (var i=0;i<30;i++)
            {
                var contextIndex = i < 20 ? 0 : i < 25 ? 1 : 2; var animalIndex = i < 4 ? 0 : i < 20 ? (i-4)/2+1 : i%5;
                var order = i<4 ? i+1 : i<20 ? (i-4)%2+1 : 1; var context = contexts[contextIndex]; var animal = animals[animalIndex];
                var path = System.IO.Path.Combine(Folder,"PRIVATE-WT-KO-source-"+i+".avi");
                using(var writer = new VideoWriter(path,VideoWriter.FourCC('F','F','V','1'),10,new OpenCvSharp.Size(300,200)))
                {
                    Assert.True(writer.IsOpened()); for(var frame=0;frame<40;frame++) { using var image = new Mat(200,300,MatType.CV_8UC3,new Scalar(20+3*frame+i,40+3*frame,230-3*frame)); Cv2.Circle(image,new OpenCvSharp.Point(i==15&&frame==22?265:100+frame,100),7,Scalar.Black,-1);writer.Write(image); }
                }
                var asset=workspace.Import(path) with { AnimalId=animal.Id,AnimalIdConfirmed=true,GroupName=animal.Group,BlindedAlias="PRIVATE WT KO alias",Phase=context.Phase,Day=context.Day,Trial=order,
                    Settings=new(){Cx=.5,Cy=.5,Rx=.45,Ry=.45,ArenaConfirmed=true,StartSeconds=.5,EndSeconds=3.5,Platform=new(.65,.5,.04)} };
                Store.SaveVideo(asset); Assets.Add(asset);
                var points=Enumerable.Range(5,30).Select(f=>f==12?new TrackPoint(f,f/10d,null,null,PointStatus.Missing,0):new TrackPoint(f,f/10d,i==15&&f==22?265:100+f,100,PointStatus.Tracked,1)).ToList();
                var run=new AnalysisRun { VideoId=asset.Id,SourceSha256=asset.SourceSha256,Metadata=asset.Metadata,Settings=asset.Settings,Points=points,Metrics=MetricCalculator.Calculate(points,asset.Metadata,asset.Settings),AlgorithmContract=AlgorithmIdentity.Contract,NominalTimingVerified=true };
                Store.SaveRun(run); Runs.Add(run); Store.RecordBiologicalEvent(asset,BiologicalEventKind.WaterEntry,5,"FREE WT KO release","Reviewer");
                if(context.Phase==TrialMode.Training) Store.RecordBiologicalEvent(asset,BiologicalEventKind.PlatformMounting,25,"FREE WT KO training mounting","Reviewer");
                Store.SaveProtocolWindow(asset,new(){RequestedExposureSeconds=3,ReleaseConfirmed=true,ReleaseFrame=5,WindowConfirmed=true,Reason="FREE WT KO protocol",Reviewer="Reviewer"});Store.RecordReview(run,i!=17);
                trials.Add(new(){Id="PRIVATE-trial-"+i,AnimalId=i==0?animal.Id.ToLowerInvariant():animal.Id,ContextId=i==0?context.Id.ToUpperInvariant():context.Id,Order=order,RecordingId=asset.Id,SourceReferenceConfirmed=true,ReferenceSourceSha256=asset.SourceSha256,ReferenceSettingsHash=StudyStore.SettingsHash(asset.Settings)});
            }
            trials.Add(new(){Id="PRIVATE-missing",AnimalId=animals[9].Id,ContextId=contexts[0].Id,MissingReason="FREE WT KO not acquired"});
            Plan=new(){Title="PRIVATE WT KO study",Animals=animals,Contexts=contexts,Trials=trials.ToImmutable(),RosterConfirmed=true,DesignConfirmed=false,UnitDesign=StudyUnitDesign.Unknown};
            new StudyPlanStore(Store).Save(Plan,"Reviewer","Expected synthetic study plan",0);
        }
        public StudyReportComputed Compute()=>StudyReportComputation.Compute(AtlasSnapshot.Read(Store.DatabasePath),Plan.Contexts.Select(c=>c.Id));
        public void Dispose(){ Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(Folder,true); }
    }
    [Fact] public void ThirtySourcesPreserveEqualAnimalsDistinctContextsAndHistoricalInputs()
    {
        using var fixture=new Fixture();var computed=fixture.Compute(); Assert.Equal(3,computed.Document.Contexts.Length); var probe=computed.Document.Contexts[0];
        output.WriteLine(string.Join(" | ",probe.Trials[0].Holds)); var d=probe.Denominators.Single(d=>d.Endpoint=="normalized occupancy map"); Assert.Equal((10,7,2,1,21,16,3,2),(d.ExpectedAnimals,d.IncludedAnimals,d.HeldAnimals,d.ExcludedAnimals,d.ExpectedTrials,d.IncludedTrials,d.HeldTrials,d.ExcludedTrials));
        Assert.Contains(probe.Trials,t=>t.VideoId is null);Assert.Contains(probe.Trials,t=>t.Excluded);Assert.Contains(probe.Trials,t=>t.Id=="PRIVATE-trial-17"&&!t.Holds.IsEmpty);
        Assert.Equal(4,probe.Trials.Count(t=>t.Animal=="A"));var ko=probe.Groups.Single(g=>g.Group=="KO");Assert.Equal(2,ko.IncludedAnimals.Length);
        var index=(int)Math.Floor(((265d-150)/135+1.01)/2.02*12)+6*12;Assert.Equal(.01,ko.Map[index],10);Assert.Equal(.04,ko.UnknownFraction,10);
        Assert.Contains("unknown",probe.Interpretation);Assert.Equal(5,computed.Document.Contexts[1].Denominators[0].ExpectedAnimals);
        Assert.Empty(computed.Document.Contexts[2].Groups);Assert.Contains(computed.Document.Contexts[2].Bins,b=>b.AbsentFraction>0&&b.Occupancy is null);
        Assert.All(computed.Document.Contexts[2].Trials,t=>{Assert.Equal(3,t.RequestedExposureSeconds);Assert.Equal(2,t.ActualExposureSeconds);Assert.Contains("mounting",t.TerminationReason);});
        Assert.All(computed.Document.Contexts.SelectMany(c=>c.Bins),b=>Assert.Equal(.5,b.ComparisonOffset));
        var folder=System.IO.Path.Combine(fixture.Folder,"complete");StudyReportPublication.Publish(folder,computed.Document,computed.Material);var saved=StudyReportPublication.Read(folder);
        fixture.Store.SaveVideo(fixture.Assets[0] with {Day=99});Assert.False(computed.Snapshot.IsCurrent());
        using var input=saved.Material!.Reconstruct();Assert.Equal(computed.Snapshot.Revision,input.Snapshot.Revision);Assert.Equal(1,input.Snapshot.Recordings[0].Asset.Day);
        var replay=StudyReportComputation.Compute(input.Snapshot,saved.Document.Contexts.Select(c=>c.Id));Assert.Equal(JsonDefaults.Serialize(saved.Document),JsonDefaults.Serialize(replay.Document with {FrozenUtc=saved.Document.FrozenUtc}));
        var html=File.ReadAllText(System.IO.Path.Combine(folder,"study.html"));Assert.Contains("Comparison starts 0.5",html);Assert.Contains("Bin after release",html);Assert.Contains("held/missing/excluded",html);
        Assert.DoesNotMatch(@"\b(G4|G5|H2|H4)\b",html);Assert.Contains("Independent validation using real recordings",html);
        Assert.DoesNotMatch(@"\b(G4|G5|H2|H4)\b",StudyReportPublication.Html(computed.Document with {Boundaries="Real held-out G4/G5/H2 and H4 researcher sign-off/clean-Windows evidence remain unrun."}));
        var csv=File.ReadAllText(System.IO.Path.Combine(folder,"temporal.csv"));Assert.Contains("bin_start_after_release_seconds",csv);Assert.Contains("\"0.5\",\"0\",\"0.5\",\"0.5\",\"1\"",csv);
        File.AppendAllText(System.IO.Path.Combine(folder,"temporal.csv"),"tampered");Assert.Throws<InvalidDataException>(()=>StudyReportPublication.Read(folder));
    }
    [Fact] public async Task PublicationCancellationConcurrencyAbruptExitAndRecoveryNeverPromotePartial()
    {
        using var fixture=new Fixture();var computed=fixture.Compute();var redacted=StudyReportComputation.Project(computed.Document,fixture.Plan,true);
        var text=JsonDefaults.Serialize(redacted);Assert.DoesNotContain("PRIVATE",text);Assert.DoesNotMatch(@"\b(WT|KO)\b",text);Assert.DoesNotContain("FREE",text);Assert.DoesNotContain("Informative",text);
        var cancelled=System.IO.Path.Combine(fixture.Folder,"cancelled");using var cancellation=new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(()=>StudyReportPublication.Publish(cancelled,redacted,null,cancellation.Token,name=>cancellation.Cancel()));
        Assert.True(File.Exists(System.IO.Path.Combine(cancelled,"001-cancelled.json")));Assert.False(File.Exists(System.IO.Path.Combine(cancelled,"001-completed.json")));Assert.Throws<FileNotFoundException>(()=>StudyReportPublication.Read(cancelled));Assert.Contains("partial",StudyReportPublication.Recover(cancelled));
        var failed=System.IO.Path.Combine(fixture.Folder,"failed");Assert.Throws<IOException>(()=>StudyReportPublication.Publish(failed,redacted,null,afterPayload:_=>throw new IOException("Synthetic interrupted writer")));Assert.True(File.Exists(System.IO.Path.Combine(failed,"001-failed.json")));Assert.False(File.Exists(System.IO.Path.Combine(failed,"001-completed.json")));Assert.Contains("partial",StudyReportPublication.Recover(failed));
        var concurrent=System.IO.Path.Combine(fixture.Folder,"concurrent");using var owned=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var first=Task.Run(()=>StudyReportPublication.Publish(concurrent,redacted,null,afterPayload:name=>{if(name=="report.json"){owned.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(20)));}}));
        Assert.True(owned.Wait(TimeSpan.FromSeconds(20)));try{Assert.Throws<IOException>(()=>StudyReportPublication.Publish(concurrent,redacted,null));Assert.Throws<IOException>(()=>StudyReportPublication.Recover(concurrent));}finally{release.Set();}await first.WaitAsync(TimeSpan.FromSeconds(20));Assert.True(StudyReportPublication.Read(concurrent).Document.Redacted);
        var crashed=System.IO.Path.Combine(fixture.Folder,"crashed");var argument=System.IO.Path.Combine(fixture.Folder,"crash-report.json");File.WriteAllText(argument,JsonDefaults.Serialize(redacted));
        var probe=System.IO.Path.Combine(AppContext.BaseDirectory,"MouseTrackerTabajara.Report.CrashProbe.dll");
        var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("exec");start.ArgumentList.Add("--runtimeconfig");start.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory,"MouseTrackerTabajara.Report.Tests.runtimeconfig.json"));start.ArgumentList.Add("--depsfile");start.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory,"MouseTrackerTabajara.Report.Tests.deps.json"));start.ArgumentList.Add(probe);start.ArgumentList.Add("publication-abrupt-exit");start.ArgumentList.Add(crashed);start.ArgumentList.Add(argument);
        using var child=Process.Start(start)!;Assert.True(child.WaitForExit(20000));Assert.Equal(77,child.ExitCode);Assert.True(File.Exists(System.IO.Path.Combine(crashed,"report.json")));Assert.False(File.Exists(System.IO.Path.Combine(crashed,"001-completed.json")));Assert.Contains("Interrupted",StudyReportPublication.Recover(crashed));Assert.True(File.Exists(System.IO.Path.Combine(crashed,"002-interrupted.json")));Assert.Throws<FileNotFoundException>(()=>StudyReportPublication.Read(crashed));
        foreach(var truncated in new[]{false,true})
        { var early=System.IO.Path.Combine(fixture.Folder,truncated?"truncated-start":"owner-only");Directory.CreateDirectory(early);File.WriteAllText(System.IO.Path.Combine(early,"publication.lock"),"");if(truncated)File.WriteAllText(System.IO.Path.Combine(early,"000-started.json"),"{\"state\":");Assert.Contains("Interrupted",StudyReportPublication.Recover(early));var receipt=JsonDefaults.Deserialize<StudyReportReceipt>(File.ReadAllText(System.IO.Path.Combine(early,"002-interrupted.json")));Assert.Equal("unknown",receipt.SnapshotRevision);Assert.False(File.Exists(System.IO.Path.Combine(early,"001-completed.json")));if(truncated)Assert.Equal("{\"state\":",File.ReadAllText(System.IO.Path.Combine(early,"000-started.json"))); }
    }
    [Fact] public void ActualNativePreviewSelectTraceSaveReplayPrivacyAndStaleness()
    {
        Exception? error=null;var lines=new List<string>();var thread=new Thread(()=>{try{Native(lines);}catch(Exception ex){error=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromMinutes(4)));foreach(var line in lines)output.WriteLine(line);if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Native(List<string> evidence)
    {
        using var fixture=new Fixture();var app=new App();app.InitializeComponent();SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));var window=new StudyReportWindow(fixture.Store.DatabasePath,true,false);
        try
        {
            window.DestinationInput.Text=System.IO.Path.Combine(fixture.Folder,"PRIVATE-WT-KO-target");
            Layout(window,1050,700);Click(window,"Select all contexts");Click(window,"Preview selected frozen study");Pump(window.PendingCompute);Assert.False(window.Held);Assert.Equal(3,window.DisplayedDocument!.Contexts.Length);AssertPrivate(window);Assert.DoesNotContain(window.DestinationInput,Find<TextBox>(window));Assert.EndsWith("PRIVATE-WT-KO-target",window.DestinationInput.Text);
            foreach(var size in new[]{new System.Windows.Size(1050,700),new System.Windows.Size(1440,960)})
            {
                Layout(window,size.Width,size.Height);Assert.True(Find<ScrollViewer>(window).Max(v=>v.ViewportHeight)>300);
                if(Environment.GetEnvironmentVariable("TABAJARA_REPORT_PROOF_DIR") is {Length:>0} directory){Directory.CreateDirectory(directory);var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render((FrameworkElement)window.Content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(System.IO.Path.Combine(directory,$"synthetic-report-{size.Width}x{size.Height}.png"));png.Save(stream);}
            }
            if(Environment.GetEnvironmentVariable("TABAJARA_REPORT_PROOF_DIR") is {Length:>0} proofDirectory){File.WriteAllText(System.IO.Path.Combine(proofDirectory,"synthetic-blind-study.html"),StudyReportPublication.Html(window.DisplayedDocument!));File.WriteAllText(System.IO.Path.Combine(proofDirectory,"synthetic-blind-temporal.csv"),StudyReportPublication.Csv(window.DisplayedDocument!));}
            var reached=new HashSet<string>();var buttons=Find<Button>(window).Where(b=>Equals(b.Content,"Open authenticated planned source")).ToArray();Assert.Equal(30,buttons.Length);
            foreach(var button in buttons)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window.PendingSource);var viewer=window.LastSourceViewer!;Assert.False(viewer.Held);Assert.Equal(10,viewer.DisplayedFrame);
                Assert.True(reached.Add(viewer.DisplayedSourceSha256!));var i=fixture.Assets.FindIndex(a=>a.SourceSha256==viewer.DisplayedSourceSha256);var bytes=new byte[4];new FormatConvertedBitmap(viewer.DisplayedBitmap,PixelFormats.Bgra32,null,0).CopyPixels(new Int32Rect(5,5,1,1),bytes,4,0);Assert.InRange(Math.Abs(bytes[0]-(50+i)),0,1);var marker=viewer.SourceOverlay.Children.OfType<Ellipse>().Single(e=>Equals(e.Tag,"active-source-marker"));Assert.Equal(110d,Canvas.GetLeft(marker)+9);
                if(i==15){Pump(viewer.SeekAsync(22));marker=viewer.SourceOverlay.Children.OfType<Ellipse>().Single(e=>Equals(e.Tag,"active-source-marker"));Assert.Equal(265d,Canvas.GetLeft(marker)+9);}
            }
            Assert.Equal(30,reached.Count);
            var cellIndex=(int)Math.Floor(((265d-150)/135+1.01)/2.02*12)+6*12;
            var map=Find<Canvas>(window).First(c=>Equals(c.Tag,"study-report-map"));var cell=map.Children.OfType<Rectangle>().Single(r=>Equals(r.Tag,cellIndex));
            cell.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonDownEvent});
            Click(window,"Open exact cell contribution");Pump(window.PendingSource);Assert.Equal(22,window.LastSourceViewer!.DisplayedFrame);Assert.Equal(265d,Canvas.GetLeft(window.LastSourceViewer.SourceOverlay.Children.OfType<Ellipse>().Single(e=>Equals(e.Tag,"active-source-marker")))+9);
            window.Blinded=false;Assert.True(window.Blinded);AssertPrivate(window);
            using(var connection=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=fixture.Store.DatabasePath,Pooling=false}.ToString()))
            { connection.Open();using var command=connection.CreateCommand();command.CommandText="CREATE TRIGGER deny_report_reveal BEFORE INSERT ON audit WHEN NEW.action='study_report_unblinded' BEGIN SELECT RAISE(ABORT,'synthetic denied audit'); END";command.ExecuteNonQuery();Assert.False(window.TryUnblind("Reviewer","Denied audit must retain privacy"));Assert.True(window.Blinded);AssertPrivate(window);command.CommandText="DROP TRIGGER deny_report_reveal";command.ExecuteNonQuery(); }
            Assert.True(window.TryUnblind("Reviewer","Reasoned report publication"));Assert.False(window.Blinded);
            Assert.Contains(window.DestinationInput,Find<TextBox>(window));Assert.EndsWith("PRIVATE-WT-KO-target",window.DestinationInput.Text);
            window.DestinationInput.Text=System.IO.Path.Combine(fixture.Folder,"native-reblind-cancel");window.Save();window.Blinded=true;Pump(window.PendingSave);Assert.True(window.Blinded);Assert.False(File.Exists(System.IO.Path.Combine(window.DestinationInput.Text,"001-completed.json")));Assert.True(File.Exists(System.IO.Path.Combine(window.DestinationInput.Text,"001-cancelled.json")));Assert.False(File.Exists(System.IO.Path.Combine(window.DestinationInput.Text,"frozen-input.json")));Assert.Null(window.DisplayedDocument);
            window.Preview();Pump(window.PendingCompute);Assert.True(window.TryUnblind("Reviewer","Fresh reasoned full publication"));
            window.DestinationInput.Text=System.IO.Path.Combine(fixture.Folder,"native-full");Click(window,"Publish frozen HTML CSV and input");Pump(window.PendingSave);Assert.Contains("Completed",window.StatusText);Assert.False(StudyReportPublication.Read(window.DestinationInput.Text).Document.Redacted);
            Click(window,"Inspect saved historical package");Assert.NotNull(window.Historical);Assert.Contains("aggregate-only",window.StatusText);
            Click(window,"Authenticate and replay frozen inputs");Pump(window.PendingCompute);Assert.Contains("PASS",window.StatusText);
            var savedFull=StudyReportPublication.Read(window.DestinationInput.Text);var fullFolder=window.DestinationInput.Text;
            var mismatch=System.IO.Path.Combine(fixture.Folder,"native-old-code");var otherCode=new string('0',64);StudyReportPublication.Publish(mismatch,savedFull.Document with {CodeReceipt=otherCode},savedFull.Material! with {CodeReceipt=otherCode});
            window.DestinationInput.Text=mismatch;Click(window,"Authenticate and replay frozen inputs");Pump(window.PendingCompute);Assert.True(window.Held);Assert.Null(window.DisplayedDocument);Click(window,"Inspect saved historical package");Assert.NotNull(window.Historical);
            var originalBytes=File.ReadAllBytes(fixture.Assets[0].Path);var changedBytes=(byte[])originalBytes.Clone();changedBytes[^1]^=1;File.WriteAllBytes(fixture.Assets[0].Path,changedBytes);
            window.DestinationInput.Text=fullFolder;Click(window,"Authenticate and replay frozen inputs");Pump(window.PendingCompute);Assert.True(window.Held);Assert.Null(window.DisplayedDocument);Click(window,"Inspect saved historical package");Assert.NotNull(window.Historical);File.WriteAllBytes(fixture.Assets[0].Path,originalBytes);
            window.DestinationInput.Text=System.IO.Path.Combine(fixture.Folder,"PRIVATE-WT-KO-reblind");window.Blinded=true;AssertPrivate(window);Assert.DoesNotContain(window.DestinationInput,Find<TextBox>(window));Assert.EndsWith("PRIVATE-WT-KO-reblind",window.DestinationInput.Text);window.ContextsInput.UnselectAll();window.ContextsInput.SelectedIndex=1;Click(window,"Preview selected frozen study");Pump(window.PendingCompute);Assert.Single(window.DisplayedDocument!.Contexts);Assert.Equal(TrialMode.Probe,window.DisplayedDocument.Contexts[0].Phase);
            window.DestinationInput.Text=System.IO.Path.Combine(fixture.Folder,"native-blind");Click(window,"Publish frozen HTML CSV and input");Pump(window.PendingSave);Assert.True(StudyReportPublication.Read(window.DestinationInput.Text).Document.Redacted);Assert.Null(StudyReportPublication.Read(window.DestinationInput.Text).Material);Assert.DoesNotMatch(@"\b(WT|KO)\b",File.ReadAllText(System.IO.Path.Combine(window.DestinationInput.Text,"study.html")));
            var unavailable=fixture.Assets[0].Path;var retained=unavailable+".retained";File.Move(unavailable,retained);
            try
            {
                Click(window,"Select all contexts");window.Preview();Pump(window.PendingCompute);Assert.False(window.Held);Assert.Equal(3,window.DisplayedDocument!.Contexts.Length);Assert.Single(window.Computed!.Document.SourceAuthentication.Where(s=>s.State=="missing"));Assert.Contains(window.Computed.Document.Contexts[0].Trials,t=>t.VideoId==fixture.Assets[0].Id&&!t.Holds.IsEmpty);
                window.DestinationInput.Text=System.IO.Path.Combine(fixture.Folder,"native-partial-auth");window.Save();Pump(window.PendingSave);var partial=StudyReportPublication.Read(window.DestinationInput.Text);Assert.Equal(29,partial.Document.SourceAuthentication.Count(s=>s.State=="authenticated"));Assert.Equal(30,partial.Document.SourceAuthentication.Length);Assert.Equal(31,partial.Document.Contexts.Sum(c=>c.Trials.Length));Assert.Contains("partial",window.StatusText);Assert.Contains("Missing/mismatched/unavailable",File.ReadAllText(System.IO.Path.Combine(window.DestinationInput.Text,"study.html")));
            }
            finally{File.Move(retained,unavailable);}
            window.Preview();window.Preview();Click(window,"Cancel report operation");Pump(window.PendingCompute);Assert.True(window.Held);Assert.Null(window.DisplayedDocument);
            Click(window,"Select all contexts");window.Preview();Pump(window.PendingCompute);fixture.Store.SaveVideo(fixture.Assets[0] with {Day=9});Pump(Task.Delay(2300));Assert.True(window.Held);Assert.Null(window.DisplayedDocument);Assert.Null(window.LastSourceViewer);
            evidence.Add("PASS actual WPF all/specific-context preview,30 authenticated source buttons with independent FFV1 pixel signatures, source frame10 comparison-offset navigation and outlier frame22 marker265, full durable save/aggregate inspect/authenticated historical replay, short-label blind/reasoned reveal,1050x700 and1440x960 layouts, cancel/latest and paused metadata-stale clearing.");
        }
        finally{window.Close();app.Shutdown();}
    }
    private static void AssertPrivate(Window window){foreach(var text in Find<TextBlock>(window)){Assert.DoesNotContain("PRIVATE",text.Text);Assert.DoesNotMatch(@"\b(WT|KO)\b",text.Text);Assert.DoesNotContain("FREE",text.Text);Assert.DoesNotContain("Informative",text.Text);Assert.DoesNotMatch(@"\b(G4|G5|H2|H4)\b",text.Text);Assert.NotEqual("A",text.Text.Trim());Assert.NotEqual("B",text.Text.Trim());}foreach(var input in Find<TextBox>(window)){Assert.DoesNotContain("PRIVATE",input.Text);Assert.DoesNotMatch(@"\b(WT|KO)\b",input.Text);}foreach(var element in Find<FrameworkElement>(window)){if(element.ToolTip is string tooltip){Assert.DoesNotContain("PRIVATE",tooltip);Assert.DoesNotMatch(@"\b(WT|KO)\b",tooltip);}}}
    private static void Click(Window window,string content)=>Find<Button>(window).Single(b=>Equals(b.Content,content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Find<T>(Window window) where T:DependencyObject=>Descendants((DependencyObject)window.Content).OfType<T>();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private static void Layout(Window window,double width,double height){var root=(FrameworkElement)window.Content;root.Measure(new System.Windows.Size(width,height));root.Arrange(new System.Windows.Rect(0,0,width,height));root.UpdateLayout();Pump(Task.Delay(30));root.UpdateLayout();}
    private static void Pump(Task task){var dispatcher=Dispatcher.CurrentDispatcher;var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(45)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false));timer.Start();Dispatcher.PushFrame(frame);timer.Stop();Assert.True(task.IsCompleted);task.GetAwaiter().GetResult();}
}
