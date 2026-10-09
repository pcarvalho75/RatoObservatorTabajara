using System.IO;
using System.Text.Json.Nodes;
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
using Window=System.Windows.Window;

namespace MouseTrackerTabajara.Validation.Tests;

public static class S14SplitBoundaryNativeProbe
{
    public static void Run(List<string> evidence)
    {
        using var fixture=new S14SplitBoundaryFixture();var saved=fixture.Compare(ValidationMeasurementTarget.RawAutomatic);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var window=new IndependentValidationWindow(fixture.Store.DatabasePath,true);
        try
        {
            Layout(window);Find<TabControl>(window).Single().SelectedIndex=2;Layout(window);
            Click(window,"Compare frozen reference and producer");Pump(window.PendingComparison);Assert.NotNull(window.Report);
            foreach(var (role,truth) in S14SplitBoundaryFixture.Truth(ValidationMeasurementTarget.RawAutomatic))SplitBoundaryAuditTests.AssertSummary(window.Report!,role,truth);
            Assert.Equal(saved.Report.PlanFingerprint,window.Report!.PlanFingerprint);Assert.Equal(saved.Report.ReferenceFingerprint,window.Report.ReferenceFingerprint);
            Assert.Contains(Texts(window),s=>s.StartsWith("Declared role Independent validation",StringComparison.Ordinal)&&s.Contains("15.714",StringComparison.Ordinal));Assert.Contains(Texts(window),s=>s.StartsWith("Declared role Development",StringComparison.Ordinal)&&s.Contains("mean 0 px",StringComparison.Ordinal));
            AssertPrivate(window);window.ReviewerInput.Text="S14 reviewer";window.ReasonInput.Text="Synthetic blinded comparison save";
            Click(window,"Save replayable comparison report");var history=new ValidationHistoryStore(fixture.Store.DatabasePath);var stored=history.History().Last(h=>h.Kind=="report");var storedReport=JsonDefaults.Deserialize<ValidationComparisonReport>(stored.Json);
            foreach(var (role,truth) in S14SplitBoundaryFixture.Truth(ValidationMeasurementTarget.RawAutomatic))SplitBoundaryAuditTests.AssertSummary(storedReport,role,truth);
            Click(window,"Replay last saved comparison");Pump(window.PendingComparison);Assert.Equal(42d/17,window.Report!.MeanRandomPositionErrorPixels!.Value,10);AssertPrivate(window);
            window.ReviewerInput.Text="S14 reviewer";window.ReasonInput.Text="Reasoned synthetic role/source audit";window.Blinded=false;Assert.False(window.Blinded);Find<TabControl>(window).Single().SelectedIndex=2;Click(window,"Compare frozen reference and producer");Pump(window.PendingComparison);Assert.Contains(Texts(window),s=>s.Contains("Allocation audit",StringComparison.Ordinal));
            window.Blinded=true;Find<TabControl>(window).Single().SelectedIndex=2;Click(window,"Compare frozen reference and producer");Pump(window.PendingComparison);AssertPrivate(window);
            var legacy=JsonNode.Parse(stored.Json)!.AsObject();legacy.Remove("split_summaries");legacy["mean_random_position_error_pixels"]=999;
            history.Save("report",Guid.NewGuid().ToString("N"),legacy.ToJsonString(),"S14 reviewer","Synthetic legacy absent-summary replay",0);
            Click(window,"Replay last saved comparison");Assert.Null(window.Report);Assert.Contains("absent",window.StatusText,StringComparison.Ordinal);Pump(window.PendingComparison);
            Assert.Equal(42d/17,window.Report!.MeanRandomPositionErrorPixels!.Value,10);foreach(var (role,truth) in S14SplitBoundaryFixture.Truth(ValidationMeasurementTarget.RawAutomatic))SplitBoundaryAuditTests.AssertSummary(window.Report,role,truth);
            Assert.Contains(Texts(window),s=>s.Contains("not inferred from the pooled mean",StringComparison.Ordinal));Assert.DoesNotContain(Texts(window),s=>s.Contains("999",StringComparison.Ordinal));AssertPrivate(window);
            var repository=new DirectoryInfo(AppContext.BaseDirectory);while(repository is not null&&!Directory.Exists(Path.Combine(repository.FullName,"src","MouseTrackerTabajara.Desktop")))repository=repository.Parent;var folder=Path.Combine(repository?.FullName??throw new DirectoryNotFoundException("Synthetic proof repository unavailable"),".workmap","logs","S14-split-boundary-probe");Directory.CreateDirectory(folder);Render(window,Path.Combine(folder,"synthetic-split-summary-1050x700.png"));
            var samplingHash=IndependentValidationSnapshot.Hash(JsonDefaults.Serialize(saved.Sampling));Assert.All(window.Snapshot.History.Where(h=>h.Kind=="reference"),h=>Assert.StartsWith(samplingHash+":",h.EntityId));
            evidence.Add("PASS independent actual native blind/unblind/reblind summary controls: pooled42/17, independent110/7, Development0, Unspecified4; every exact field preserved in saved/replayed report. Legacy absent summaries visibly held before recomputation, tampered pooled999 not displayed; original sample/reference keys unchanged. Synthetic1050x700 PNG saved under ignored .workmap/logs/S14-split-boundary-probe.");
        }
        finally{window.Close();}
    }
    private static void AssertPrivate(Window window)
    {
        foreach(var element in Find<FrameworkElement>(window))
        { var text=element switch{TextBlock b=>b.Text,TextBox b=>b.Text,_=>""};Assert.DoesNotContain("PRIVATE",text);Assert.DoesNotMatch(@"\b(WT|KO)\b",text);if(element.ToolTip is string tip){Assert.DoesNotContain("PRIVATE",tip);Assert.DoesNotMatch(@"\b(WT|KO)\b",tip);} }
    }
    private static IEnumerable<string> Texts(Window window)=>Find<TextBlock>(window).Select(b=>b.Text);
    private static void Click(Window window,string text)=>Find<Button>(window).Single(b=>Equals(b.Content,text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Find<T>(Window window) where T:DependencyObject=>Descendants((DependencyObject)window.Content).OfType<T>();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent){for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private static void Layout(Window window){var root=(FrameworkElement)window.Content;root.Measure(new System.Windows.Size(1050,700));root.Arrange(new Rect(0,0,1050,700));root.UpdateLayout();Pump(Task.Delay(30));root.UpdateLayout();}
    private static void Render(Window window,string path)
    {
        var root=(FrameworkElement)window.Content;window.Content=null;
        try
        { var parameters=new HwndSourceParameters("s14-synthetic-summary"){WindowStyle=0,ExtendedWindowStyle=0x08000000,PositionX=-32000,PositionY=-32000,Width=1050,Height=700};using var hwnd=new HwndSource(parameters){RootVisual=root};root.Measure(new System.Windows.Size(1050,700));root.Arrange(new Rect(0,0,1050,700));root.UpdateLayout();Pump(Task.Delay(60));var bitmap=new RenderTargetBitmap(1050,700,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);png.Save(stream);hwnd.RootVisual=null; }
        finally{window.Content=root;}
    }
    private static void Pump(Task task){var dispatcher=Dispatcher.CurrentDispatcher;var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(45)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false));timer.Start();Dispatcher.PushFrame(frame);timer.Stop();Assert.True(task.IsCompleted);task.GetAwaiter().GetResult();}
}
