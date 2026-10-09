using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;
using Xunit.Abstractions;
namespace MouseTrackerTabajara.Atlas.Tests;

public class MainSidebarAccessibilityTests(ITestOutputHelper output)
{
    [Fact] public async Task MinimumAndLargeWorkspaceSidebarActionsScrollIntoHitTestAndKeyboardReach()
    {
        const string child="TABAJARA_S32_SIDEBAR_CHILD";
        if(Environment.GetEnvironmentVariable(child)!="1")
        {
            var p=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var a in new[]{"test",typeof(MainSidebarAccessibilityTests).Assembly.Location,"--filter","FullyQualifiedName="+typeof(MainSidebarAccessibilityTests).FullName+"."+nameof(MinimumAndLargeWorkspaceSidebarActionsScrollIntoHitTestAndKeyboardReach),"--logger","console;verbosity=detailed"})p.ArgumentList.Add(a);
            p.Environment[child]="1";using var process=Process.Start(p)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            try{Assert.True(process.WaitForExit(180000));output.WriteLine(await stdout);output.WriteLine(await stderr);Assert.Equal(0,process.ExitCode);}finally{if(!process.HasExited)process.Kill(true);}return;
        }
        Exception? error=null;var lines=new List<string>();var thread=new Thread(()=>{try{Proof(lines);}catch(Exception e){error=e;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(120)));foreach(var line in lines)output.WriteLine(line);if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Proof(List<string> lines)
    {
        var folder=Path.Combine(Path.GetTempPath(),"tabajara-s32-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var store=new StudyStore(Path.Combine(folder,"empty.tabajara"));var app=new App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var main=new MainWindow(new MainViewModel(new WorkspaceService(store)));app.MainWindow=main;
        try
        {
            main.Show();main.Activate();foreach(var size in new[]{new Size(1050,700),new Size(1440,960)})
            {
                main.Width=size.Width;main.Height=size.Height;Pump();var root=(FrameworkElement)main.Content;var scroll=(ScrollViewer)main.FindName("SidebarActionsScroll");
                Assert.True(scroll.ViewportHeight>50&&double.IsFinite(scroll.ViewportHeight));
                var footer=Descendants(root).OfType<TextBlock>().Single(t=>t.Text=="Local processing");var footerBounds=Bounds(footer,root);Assert.True(footerBounds.Bottom<=root.ActualHeight);
                foreach(var label in new[]{"Independent reference validation","Detector sensitivity","Region and calibration scenarios","Whole-study report"})
                {
                    var button=Descendants(scroll).OfType<Button>().Single(b=>b.Content is string s?s==label:b.Content is TextBlock t&&t.Text==label);
                    button.BringIntoView();Pump();Assert.True(button.Focus());Pump();Assert.True(button.IsKeyboardFocused);Assert.True(button.IsHitTestVisible);var caption=Descendants(button).OfType<TextBlock>().Single(t=>t.Text==label);Assert.Equal(TextWrapping.Wrap,caption.TextWrapping);Assert.Equal(TextTrimming.None,caption.TextTrimming);Assert.True(caption.ActualWidth>0&&caption.ActualHeight>0);
                    var bounds=Bounds(button,scroll);Assert.True(bounds.Top>=-1&&bounds.Bottom<=scroll.ActualHeight+1,$"{label}: {bounds} vs viewport {scroll.ActualHeight}");Assert.True(bounds.Width>100&&bounds.Right<=scroll.ActualWidth+1);
                    var center=button.TranslatePoint(new Point(button.ActualWidth/2,button.ActualHeight/2),root);var hit=root.InputHitTest(center) as DependencyObject;Assert.True(hit is not null&&IsWithin(hit,button),label+" mouse hit targets its actual visible action");
                    Assert.True(button.TranslatePoint(new Point(0,button.ActualHeight),root).Y<=footerBounds.Top+1,label+" stays above footer (one DIP layout rounding tolerance)");
                    lines.Add($"{size}: {label} visible bounds={bounds}; keyboard focus and mouse hit PASS; footerTop={footerBounds.Top}; offset={scroll.VerticalOffset}");
                }
                scroll.ScrollToTop();Pump();var before=scroll.VerticalOffset;scroll.LineDown();Pump();Assert.True(scroll.VerticalOffset>before||scroll.ScrollableHeight==0);scroll.ScrollToEnd();Pump();
                Assert.True(((FrameworkElement)main.FindName("ProductWordmark")).ActualHeight>0);Assert.True(((Button)main.FindName("StudyIntakeButton")).IsVisible);
            }
        }
        finally{main.Close();app.Shutdown();}
    }
    private static void Pump(){Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);}
    private static Rect Bounds(FrameworkElement e,Visual root)=>e.TransformToAncestor(root).TransformBounds(new Rect(0,0,e.ActualWidth,e.ActualHeight));
    private static bool IsWithin(DependencyObject e,DependencyObject target){for(var p=e;p is not null;p=VisualTreeHelper.GetParent(p))if(ReferenceEquals(p,target))return true;return false;}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject d){for(int i=0;i<VisualTreeHelper.GetChildrenCount(d);i++){var c=VisualTreeHelper.GetChild(d,i);yield return c;foreach(var n in Descendants(c))yield return n;}}
}
