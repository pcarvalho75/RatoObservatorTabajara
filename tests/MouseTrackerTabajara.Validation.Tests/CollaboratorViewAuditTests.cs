using System.Collections.Immutable;
using System.IO;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Validation.Tests;

/// <summary>Independent predeclared collaborator view audit. Synthetic pixels only.
/// Numeric truth is fixed by the S16 constructed controls, not by GUI implementation.</summary>
public sealed class CollaboratorViewAuditTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MinimumNativeWorkflowKeepsExactTruthPrivateAndSelectable()
    {
        if(Environment.GetEnvironmentVariable("TABAJARA_COLLABORATOR_AUDIT")!="native")
        {
            var project=App.FindRepositoryFile("tests/MouseTrackerTabajara.Validation.Tests/MouseTrackerTabajara.Validation.Tests.csproj");Assert.NotNull(project);
            var receipts=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"tabajara-collaborator-child-receipt-"+Guid.NewGuid().ToString("N"));
            var launch=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var argument in new[]{"test",project!,"-c","Release","--no-build","--no-restore","--filter","FullyQualifiedName~CollaboratorViewAuditTests","--logger","trx;LogFileName=native-child.trx","--results-directory",receipts})launch.ArgumentList.Add(argument);
            launch.Environment["TABAJARA_COLLABORATOR_AUDIT"]="native";
            using var child=Process.Start(launch)!;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
            try{Assert.True(child.WaitForExit(120000));output.WriteLine("isolated native child receipt="+receipts);output.WriteLine(await stdout);output.WriteLine(await stderr);Assert.Equal(0,child.ExitCode);}
            finally{if(!child.HasExited){child.Kill(true);child.WaitForExit(10000);}}
            return;
        }
        Exception? failure=null; var evidence=new List<string>();
        var thread=new Thread(()=>{try{Audit(evidence);}catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(90)),"Bounded synthetic GUI audit timed out.");
        foreach(var item in evidence) output.WriteLine(item);
        if(failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Audit(List<string> evidence)
    {
        var folder=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"tabajara-collaborator-view-audit-"+Guid.NewGuid().ToString("N"));
        var fixture=new ThirtyRecordingPerformanceFixture(folder,30,24);evidence.Add("retained evidence="+folder);
        var store=new StudyStore(System.IO.Path.Combine(folder,"synthetic-study.db"));
        foreach(var frozen in fixture.Workloads[0].Snapshot.Runs)
        {
            var excluded=frozen.VideoId=="performance-source-02";
            store.SaveVideo(frozen.Asset with {AnimalId="PRIVATE-WT-KO-ANIMAL",GroupName="PRIVATE-WT-KO-GROUP",Included=!excluded,ExclusionReason=excluded?"PRIVATE-WT-KO-exclusion-control":""});
            store.SaveRun(frozen.RawRun);
            foreach(var correction in frozen.Corrections) store.SaveCorrection(frozen.RawRun,JsonDefaults.Deserialize<ManualCorrection>(correction));
        }
        var history=new ValidationHistoryStore(store.DatabasePath);var plan=fixture.Workloads[0].Plan;
        var initial=IndependentValidationSnapshot.Read(store.DatabasePath);var sample=IndependentValidationSampling.Generate(initial,plan);
        history.Save("plan",plan.Id,JsonDefaults.Serialize(plan),"Synthetic reviewer","Predeclared synthetic controls",0);
        history.Save("sampling",plan.Id,JsonDefaults.Serialize(sample),"Synthetic reviewer","Frozen synthetic cases",0);
        var hash=IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(sample));
        foreach(var entry in fixture.Workloads[0].Snapshot.History)
        {
            var reference=JsonDefaults.Deserialize<IndependentReference>(entry.Json);
            history.Save("reference",IndependentValidationComparison.ReferenceKey(hash,reference),entry.Json,entry.Reviewer,entry.Reason,0);
        }
        // Resolved independent disagreement on a genuinely missing automatic row must not be called unresolved.
        var second=new IndependentReference("performance-source-00",13,ReferenceReviewerLane.Second,"Synthetic second",ReferenceVisibility.Visible,46,32,ReferenceEvent.None,"Synthetic disagreement on raw gap",[]);
        history.Save("reference",IndependentValidationComparison.ReferenceKey(hash,second),JsonDefaults.Serialize(second),second.Reviewer,second.Reason,1);
        var citations=history.History().Where(h=>h.Kind=="reference").Select(h=>(Entry:h,Ref:JsonDefaults.Deserialize<IndependentReference>(h.Json))).Where(h=>h.Ref.VideoId==second.VideoId&&h.Ref.Frame==13).GroupBy(h=>h.Ref.Lane).Select(g=>g.OrderBy(h=>h.Entry.Revision).Last().Entry.Id).ToImmutableArray();
        var consensus=second with{Lane=ReferenceReviewerLane.Consensus,Reviewer="Synthetic consensus",X=40,Reason="Resolved synthetic references; raw detector row remains missing",ConsensusReferenceIds=citations};
        history.Save("reference",IndependentValidationComparison.ReferenceKey(hash,consensus),JsonDefaults.Serialize(consensus),consensus.Reviewer,consensus.Reason,0);
        var defects=new List<string>();
        var app=new App();app.InitializeComponent();SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var main=new MainWindow(new MainViewModel(new WorkspaceService(store)));var window=new IndependentValidationWindow(store.DatabasePath,true);
        try
        {
            main.Show();window.Show();Layout(main);var buttons=Visual(main).OfType<Button>().Select(b=>b.Content?.ToString()??"").ToArray();
            foreach(var caption in new[]{"Add videos","Analyze all","Cancel","Save settings","Confirm tracking review","Independent reference validation","Whole-study report"}) Assert.Contains(caption,buttons);
            Assert.Contains(Visual(main).OfType<CheckBox>(),c=>Equals(c.Content,"Detect dark animals"));
            Render(main,System.IO.Path.Combine(folder,"main-minimum.png"));
            window.ReviewerInput.Text="Synthetic reviewer";window.ReasonInput.Text="Independent collaborator audit";
            var tabs=Visual(window).OfType<TabControl>().Single();tabs.SelectedIndex=2;Layout(window);
            window.StartComparison();Pump(window.PendingComparison);Layout(window);
            Assert.NotNull(window.Report);Assert.Equal(720,window.Report!.SampledUniqueFrames);
            Assert.Equal(570,window.Report.PositionJoinedFrames);Assert.Equal(2,window.Report.MaximumPositionErrorPixels);
            Assert.Equal(24,window.Report.Frames.Count(f=>f.Excluded));
            var unspecified=window.Report.SplitSummaries.Single(s=>s.Role==ValidationSplit.Unspecified);Assert.Equal(1,unspecified.ExcludedRecordings);Assert.Equal(24,unspecified.ExcludedSampledFrames);Assert.Equal(190,unspecified.PositionJoinedFrames);
            foreach(var truth in fixture.Workloads[0].ExpectedFrames)
            {
                var actual=window.Report.Frames.Single(f=>f.VideoId==truth.VideoId&&f.Frame==truth.Frame);
                Assert.Equal(truth.PositionErrorPixels,actual.PositionErrorPixels);Assert.Equal(truth.FalseAccepted,actual.FalseAccepted);Assert.Equal(truth.MissedVisible,actual.MissedVisible);
            }
            var results=Assert.IsAssignableFrom<ListBox>(Visual(window).Single(c=>AutomationProperties.GetName(c)=="Validation result list"));Assert.Equal(720,results.Items.Count);
            Assert.True(VirtualizingPanel.GetIsVirtualizing(results));Assert.Equal(VirtualizationMode.Recycling,VirtualizingPanel.GetVirtualizationMode(results));
            Assert.InRange(Visual(results).Count(c=>c is ListBoxItem or DataGridRow),1,99);
            Assert.True(double.IsFinite(results.ActualHeight)&&results.ActualHeight>0);evidence.Add($"result rows={results.Items.Count}; realized containers={Visual(results).Count(c=>c is ListBoxItem or DataGridRow)}; viewport height={results.ActualHeight}; no arbitrary upper viewport cap");
            results.SelectedIndex=719;Layout(window);Assert.NotNull(results.SelectedItem);
            results.ScrollIntoView(results.SelectedItem);Layout(window);
            var resolved=results.Items.OfType<ValidationFrameRow>().Single(r=>r.VideoId==second.VideoId&&r.Frame==13);
            Assert.True(resolved.Result.Disagreement);Assert.True(resolved.Result.MissedVisible);Assert.Null(resolved.Result.PositionErrorPixels);Assert.Empty(resolved.Result.Holds);
            if(resolved.Headline.Contains("unresolved disagreement",StringComparison.OrdinalIgnoreCase))defects.Add("Resolved consensus on source00/frame13 raw missing row is incorrectly labelled unresolved disagreement: "+resolved.Headline);
            if(!resolved.Headline.Contains("missed visible",StringComparison.OrdinalIgnoreCase)&&!resolved.Headline.Contains("missing automatic",StringComparison.OrdinalIgnoreCase)&&!resolved.Headline.Contains("producer join unavailable",StringComparison.OrdinalIgnoreCase))defects.Add("Source00/frame13 raw missing automatic row has no distinct unavailable-producer row label: "+resolved.Headline);
            foreach(var defect in defects)evidence.Add("BASE GUI DEFECT: "+defect);
            var visible=string.Join("\n",Visual(window).OfType<TextBlock>().Where(t=>t.IsVisible).Select(t=>t.Text));
            foreach(var role in new[]{"Development","Independent validation","Unspecified"}) Assert.Contains(role,visible,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("join",visible,StringComparison.OrdinalIgnoreCase);Assert.Contains("Denominator",string.Join("\n",Logical(window).OfType<TextBlock>().Select(t=>t.Text)),StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Visual(window),n=>AutomationProperties.GetName(n)=="Validation role summary");
            AssertPrivate(window);Render(window,System.IO.Path.Combine(folder,"validation-minimum.png"));
            var counts=Visual(window).OfType<Expander>().Single(e=>AutomationProperties.GetName(e)=="Validation role count details");counts.IsExpanded=true;Layout(window);
            var resultBounds=results.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(new Point(0,0),results.RenderSize));
            var detailBounds=Visual(window).Single(n=>AutomationProperties.GetName(n)=="Validation selected frame") as FrameworkElement;Assert.NotNull(detailBounds);
            var selectedBounds=detailBounds!.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(new Point(0,0),detailBounds.RenderSize));var clientHeight=((FrameworkElement)window.Content).ActualHeight;
            evidence.Add($"expanded result bounds={resultBounds}; selected details bounds={selectedBounds}; client height={clientHeight}");
            if(resultBounds.Bottom>clientHeight+1||selectedBounds.Bottom>clientHeight+1)defects.Add($"Expanded90endpoint full-counts content pushes list/detail out of minimum client: resultbottom={resultBounds.Bottom},detailbottom={selectedBounds.Bottom},client={clientHeight}");
            Render(window,System.IO.Path.Combine(folder,"expanded-counts-minimum.png"));counts.IsExpanded=false;Layout(window);
            var frozenReport=JsonDefaults.Serialize(window.Report);window.SaveReport();Assert.Contains(history.History(),h=>h.Kind=="report");
            window.ReplayReport();Pump(window.PendingComparison);Assert.Equal(570,window.Report!.PositionJoinedFrames);Assert.Equal(frozenReport,JsonDefaults.Serialize(window.Report));
            results=Assert.IsAssignableFrom<ListBox>(Visual(window).Single(c=>AutomationProperties.GetName(c)=="Validation result list"));results.SelectedIndex=719;Layout(window);
            var chosen=(ValidationFrameRow)results.SelectedItem;
            var detail=Assert.IsType<ScrollViewer>(Visual(window).Single(c=>AutomationProperties.GetName(c)=="Validation selected frame"));detail.ScrollToEnd();Layout(window);
            Visual(window).OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="Open selected validation source").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window.PendingFrame);Layout(window);Assert.Equal(1,tabs.SelectedIndex);Assert.Null(window.DisplayedBitmap);Assert.True(window.PixelDisclosureInput.IsVisible);
            window.PixelDisclosureInput.IsChecked=true;tabs.SelectedIndex=2;window.StartComparison();Pump(window.PendingComparison);Layout(window);
            results=Assert.IsAssignableFrom<ListBox>(Visual(window).Single(c=>AutomationProperties.GetName(c)=="Validation result list"));results.SelectedIndex=719;
            detail=Assert.IsType<ScrollViewer>(Visual(window).Single(c=>AutomationProperties.GetName(c)=="Validation selected frame"));detail.ScrollToEnd();Layout(window);
            Visual(window).OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="Open selected validation source").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window.PendingFrame);Assert.Equal(chosen.Frame,window.DisplayedFrame);Assert.Equal(chosen.VideoId,window.DisplayedVideoId);
            Layout(window);Assert.Equal(1,tabs.SelectedIndex);Assert.Contains(Visual(window).OfType<Image>(),image=>image.IsVisible&&ReferenceEquals(image.Source,window.DisplayedBitmap));
            Assert.NotNull(window.DisplayedBitmap);var pixel=new byte[4];new FormatConvertedBitmap(window.DisplayedBitmap,PixelFormats.Bgra32,null,0).CopyPixels(new Int32Rect(2,2,1,1),pixel,4,0);
            // FFV1 preserves encoded planes; VideoWriter's BGR->video conversion can round a channel.
            // Exact displayed pixels are checked against an independent sequential original-source decode,
            // not against the displayed value or a weakened guessed RGB vector.
            var chosenAsset=window.Snapshot.Imported.Single(a=>a.Id==chosen.VideoId);
            using(var capture=new OpenCvSharp.VideoCapture(chosenAsset.Path,OpenCvSharp.VideoCaptureAPIs.FFMPEG))using(var image=new OpenCvSharp.Mat())
            {
                Assert.True(capture.IsOpened());for(var frame=0;frame<=chosen.Frame;frame++)Assert.True(capture.Read(image));
                Assert.Equal(chosen.Frame+1,(int)capture.Get(OpenCvSharp.VideoCaptureProperties.PosFrames));var original=image.At<OpenCvSharp.Vec3b>(2,2);
                if(original.Item0!=pixel[0]||original.Item1!=pixel[1]||original.Item2!=pixel[2])defects.Add($"Displayed original preview differs from independent FFmpeg source pixels at source{chosen.VideoId}/frame{chosen.Frame}: originalBGR={original.Item0},{original.Item1},{original.Item2};displayBGR={pixel[0]},{pixel[1]},{pixel[2]}");
            }
            tabs.SelectedIndex=2;Layout(window);
            var retainedSelection=(ValidationFrameRow)Assert.IsAssignableFrom<ListBox>(Visual(window).Single(c=>AutomationProperties.GetName(c)=="Validation result list")).SelectedItem;
            Assert.Equal(chosen.VideoId,retainedSelection.VideoId);Assert.Equal(chosen.Frame,retainedSelection.Frame);
            var saved=JsonNode.Parse(history.History().Last(h=>h.Kind=="report").Json)!.AsObject();saved.Remove("split_summaries");saved["mean_random_position_error_pixels"]=999;
            history.Save("report",Guid.NewGuid().ToString("N"),saved.ToJsonString(),"Synthetic reviewer","Legacy scalar sentinel with absent role summaries",0);
            window.ReplayReport();Assert.Null(window.Report);Pump(window.PendingComparison);Assert.Equal(2,window.Report!.MeanRandomPositionErrorPixels);
            Assert.DoesNotContain("999",string.Join("\n",Logical(window).OfType<TextBlock>().Select(t=>t.Text)));Render(window,System.IO.Path.Combine(folder,"legacy-recomputed.png"));
            // Switch target through the public frozen history APIs; preserve references under the new sampling hash.
            var corrected=plan with{Id="collaborator-corrected",MeasurementTarget=ValidationMeasurementTarget.InspectedCorrected};
            var current=IndependentValidationSnapshot.Read(store.DatabasePath);var correctedSample=IndependentValidationSampling.Generate(current,corrected);var correctedHash=IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(correctedSample));
            history.Save("plan",corrected.Id,JsonDefaults.Serialize(corrected),"Synthetic reviewer","Explicit corrected target",0);history.Save("sampling",corrected.Id,JsonDefaults.Serialize(correctedSample),"Synthetic reviewer","Frozen corrected sample",0);
            foreach(var entry in current.History.Where(h=>h.Kind=="reference"&&h.EntityId.StartsWith(hash+":",StringComparison.Ordinal)).GroupBy(h=>h.EntityId).Select(g=>g.OrderBy(h=>h.Revision).Last()))
            {
                var reference=JsonDefaults.Deserialize<IndependentReference>(entry.Json);
                // Consensus citations are rebound to the actual independently saved revisions for this target.
                if(reference.Lane==ReferenceReviewerLane.Consensus)continue;
                history.Save("reference",IndependentValidationComparison.ReferenceKey(correctedHash,reference),entry.Json,entry.Reviewer,entry.Reason,0);
            }
            var correctedCitations=history.History().Where(h=>h.Kind=="reference"&&h.EntityId.StartsWith(correctedHash+":",StringComparison.Ordinal)).Select(h=>(Entry:h,Ref:JsonDefaults.Deserialize<IndependentReference>(h.Json))).Where(h=>h.Ref.VideoId==second.VideoId&&h.Ref.Frame==13).Select(h=>h.Entry.Id).ToImmutableArray();
            var correctedConsensus=consensus with{ConsensusReferenceIds=correctedCitations};history.Save("reference",IndependentValidationComparison.ReferenceKey(correctedHash,correctedConsensus),JsonDefaults.Serialize(correctedConsensus),correctedConsensus.Reviewer,correctedConsensus.Reason,0);
            window.Reload();window.StartComparison();Pump(window.PendingComparison);Assert.Equal(600,window.Report!.PositionJoinedFrames);Assert.Equal(0,window.Report.MaximumPositionErrorPixels);Assert.Equal(ValidationMeasurementTarget.InspectedCorrected,window.Report.MeasurementTarget);
            foreach(var truth in fixture.Workloads[1].ExpectedFrames)Assert.Equal(truth.PositionErrorPixels,window.Report.Frames.Single(f=>f.VideoId==truth.VideoId&&f.Frame==truth.Frame).PositionErrorPixels);
            AssertPrivate(window);Render(window,System.IO.Path.Combine(folder,"corrected-minimum.png"));
            window.StartComparison();Visual(window).OfType<Button>().Single(b=>Equals(b.Content,"Cancel comparison")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window.PendingComparison);Assert.Null(window.Report);Assert.True(window.Held);
            window.Reload();window.ReviewerInput.Text="Synthetic reviewer";window.ReasonInput.Text="Reasoned synthetic reveal";window.Blinded=false;Assert.False(window.Blinded);
            window.StartComparison();window.Blinded=true;Pump(window.PendingComparison);AssertPrivate(window);
            window.StartComparison();window.Close();Pump(window.PendingComparison);Assert.Null(window.Report);
            evidence.Add("PASS native1050x700 workflow route;720synthetic cases/570raw joins/max2pixels; independent per-frame truth, role denominator discovery, recycling<100containers; selected-result exact source pixels; blind logical/visual/tooltips/automation metadata; save/replay/legacy999 recompute; corrected600joins/max0; cancel/reblind/close.");
            foreach(var defect in defects)evidence.Add("FINAL GUI DEFECT: "+defect);Assert.Empty(defects);
        }
        finally{window.Close();main.Close();app.Shutdown();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();}
    }

    private static void AssertPrivate(DependencyObject root)
    {
        var sourceSecrets=root is IndependentValidationWindow window
            ? window.Snapshot.Imported.SelectMany(a=>new[]{a.Filename,a.SourceSha256,a.AnimalId,a.GroupName}).Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct().ToArray():[];
        foreach(var node in Visual(root).Concat(Logical(root)).Distinct())
        {
            var text=node switch {TextBlock t=>t.Text,TextBox t=>t.Text,ContentControl c=>c.Content as string,HeaderedItemsControl h=>h.Header as string,_=>null};
            foreach(var value in new[]{text,(node as FrameworkElement)?.ToolTip as string,AutomationProperties.GetName(node),AutomationProperties.GetHelpText(node)})
            {
                Assert.DoesNotContain("PRIVATE",value??"",StringComparison.OrdinalIgnoreCase);
                foreach(var secret in sourceSecrets)Assert.DoesNotContain(secret,value??"",StringComparison.OrdinalIgnoreCase);
            }
        }
    }
    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {yield return root;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Visual(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var nested in Logical(child))yield return nested;}
    private static void Layout(Window window){window.Width=1050;window.Height=700;var root=(FrameworkElement)window.Content;root.Measure(new Size(1050,700));root.Arrange(new Rect(0,0,1050,700));root.UpdateLayout();Pump(Task.Delay(30));}
    private static void Render(Window window,string path){var root=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap(1050,700,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);}
    private static void Pump(Task task){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(30)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};task.ContinueWith(_=>System.Windows.Application.Current.Dispatcher.BeginInvoke(()=>frame.Continue=false));timer.Start();Dispatcher.PushFrame(frame);timer.Stop();Assert.True(task.IsCompleted);task.GetAwaiter().GetResult();}
}


