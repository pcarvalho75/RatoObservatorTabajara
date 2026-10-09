using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using Xunit;

namespace MouseTrackerTabajara.Report.Tests;

public static class ReportShowcaseCapture
{
    public static string Run()
    {
        var repo=new DirectoryInfo(AppContext.BaseDirectory);while(repo is not null&&!Directory.Exists(Path.Combine(repo.FullName,"src","MouseTrackerTabajara.Desktop")))repo=repo.Parent;
        var original=Directory.GetDirectories(Path.Combine(repo!.FullName,".workmap","logs","S20-group-questions"),"synthetic-30-*").OrderByDescending(Directory.GetLastWriteTimeUtc).First(p=>File.Exists(Path.Combine(p,"publication-full","001-completed.json"))&&File.Exists(Path.Combine(p,"publication-blind","001-completed.json")));
        var package=Path.Combine(original,"publication-full");var saved=StudyReportPublication.Read(package);Assert.Equal(30,saved.Document.SourceAuthentication.Length);using var input=saved.Material!.Reconstruct();
        var folder=Path.Combine(repo.FullName,".workmap","logs","S21-showcase","native-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));var window=new StudyReportWindow(input.Snapshot.DatabasePath,true,false);
        try
        {
            window.DestinationInput.Text=package;window.InspectSaved();Assert.NotNull(window.Historical);Assert.True(window.Blinded);Layout(window,1440,960);
            var scroll=Find<ScrollViewer>(window).Single(s=>s.Content is StackPanel);var body=(UIElement)scroll.Content;
            var maps=Find<Canvas>(window).Where(c=>Equals(c.Tag,"study-report-map")).ToArray();Assert.True(maps.Length>=2);
            var bounds=Find<Expander>(window).First(e=>e.Header is string s&&s.Contains("conservative cellwise",StringComparison.Ordinal));bounds.IsExpanded=true;Layout(window,1440,960);
            scroll.ScrollToVerticalOffset(Math.Max(0,maps[0].TranslatePoint(new Point(0,0),body).Y-80));Layout(window,1440,960);
            Capture(window,folder,"synthetic-group-heatmaps-and-bounds-1440x960.png",1440,960);
            var y=maps[0].TranslatePoint(new Point(0,0),scroll).Y;Assert.InRange(y,0,scroll.ActualHeight);Assert.True(bounds.IsExpanded);
            bounds.IsExpanded=false;var selector=Find<ComboBox>(window).First(c=>Equals(c.Tag,"group-question-endpoint-selector"));selector.SelectedItem="proximity";Layout(window,1440,960);
            scroll.ScrollToVerticalOffset(Math.Max(0,selector.TranslatePoint(new Point(0,0),body).Y-65));Layout(window,1440,960);Assert.Equal("proximity",selector.SelectedItem);Assert.Contains(Find<TextBlock>(window),t=>t.Text.Contains("proximity",StringComparison.Ordinal));
            Capture(window,folder,"synthetic-proximity-distribution-1440x960.png",1440,960);
            selector.SelectedItem="observed adjacent distance";Layout(window,1050,700);scroll.ScrollToVerticalOffset(Math.Max(0,selector.TranslatePoint(new Point(0,0),body).Y-35));Layout(window,1050,700);
            Capture(window,folder,"synthetic-observed-distance-1050x700.png",1050,700);
            foreach(var text in Find<TextBlock>(window)){Assert.DoesNotContain("PRIVATE",text.Text);Assert.DoesNotMatch(@"\b(WT|KO)\b",text.Text);}
            foreach(var name in new[]{"context-001-trial-001_PATH.svg","context-001-trial-001_PCOORD.svg","context-001-trial-001_DENSI.svg","context-001_GROUP_DIFFERENCE.svg"})
            {var source=Path.Combine(original,"publication-blind",name);Assert.True(File.Exists(source));File.Copy(source,Path.Combine(folder,name));Assert.DoesNotContain("PRIVATE",File.ReadAllText(Path.Combine(folder,name)));}
            var receipt=new{sourcePublication=package,evidence=folder,syntheticOnly=true,sourceCount=30,framesPerSecond="source metadata,10fps fixture",scope="Actual WPF report scroll/selector, no mockup. Original blinded producer SVGs copied byte-for-byte; no raster renderer invented.",files=Directory.GetFiles(folder).Select(p=>new{name=Path.GetFileName(p),sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))}).ToArray()};
            File.WriteAllText(Path.Combine(folder,"receipt.json"),JsonDefaults.Serialize(receipt));return JsonDefaults.Serialize(receipt);
        }
        finally{window.Close();}
    }
    private static IEnumerable<T> Find<T>(Window window) where T:DependencyObject=>Descendants((DependencyObject)window.Content).OfType<T>();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent){for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private static void Layout(Window window,int width,int height){var root=(FrameworkElement)window.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();Pump();root.UpdateLayout();}
    private static void Pump(){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(60)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    private static void Capture(Window window,string folder,string name,int width,int height)
    {var root=(FrameworkElement)window.Content;window.Content=null;try{using var hwnd=new HwndSource(new HwndSourceParameters("s21-synthetic"){WindowStyle=0,ExtendedWindowStyle=0x08000000,PositionX=-32000,PositionY=-32000,Width=width,Height=height}){RootVisual=root};root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();Pump();var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(folder,name));encoder.Save(file);hwnd.RootVisual=null;}finally{window.Content=root;}}
}
