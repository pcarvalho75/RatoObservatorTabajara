using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;
using MouseTrackerTabajara.Core;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Validation.Tests;

public sealed class UpdateUiHandoffTests(ITestOutputHelper output)
{
    private sealed class Client:IUpdateClient
    {
        public int Checks;public Func<CancellationToken,Task<UpdateRelease?>> Check= _=>Task.FromResult<UpdateRelease?>(Release);
        public static UpdateRelease Release=>new("0.1.0-dev.13","v0.1.0-dev.13",new("https://github.com/pcarvalho75/RatoObservatorTabajara/releases/tag/v0.1.0-dev.13"),new("https://github.com/pcarvalho75/RatoObservatorTabajara/releases/download/v0.1.0-dev.13/portable.zip"),100,new string('a',64));
        public PreparedUpdate Prepared=new("0.1.0-dev.13","synthetic-install","synthetic-install/desktop/MouseTrackerTabajara.exe");
        public Task<UpdateRelease?> CheckAsync(string version,bool channel,CancellationToken token){Checks++;return Check(token);}
        public Task<PreparedUpdate> PrepareAsync(UpdateRelease release,IProgress<UpdateProgress>? progress,CancellationToken token)=>Task.FromResult(Prepared);
    }
    [Fact] public async Task NativeUpdatesStaySeparateAndHandoffPreservesProjectCopiesAndIdleGuards()
    {
        const string childVariable="TABAJARA_S45_CHILD";
        if(Environment.GetEnvironmentVariable(childVariable)!="1")
        {
            var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"test",typeof(UpdateUiHandoffTests).Assembly.Location,"--filter","FullyQualifiedName="+typeof(UpdateUiHandoffTests).FullName+"."+nameof(NativeUpdatesStaySeparateAndHandoffPreservesProjectCopiesAndIdleGuards),"--logger","console;verbosity=detailed"})start.ArgumentList.Add(arg);
            start.Environment[childVariable]="1";using var child=Process.Start(start)!;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
            try{using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(120));await child.WaitForExitAsync(deadline.Token);output.WriteLine(await stdout);output.WriteLine(await stderr);Assert.Equal(0,child.ExitCode);}
            finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
            return;
        }
        Exception? failure=null;var thread=new Thread(()=>{try{Proof();}catch(Exception ex){failure=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(150)));if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Proof()
    {
        var folder=Path.Combine(Path.GetTempPath(),"update-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var app=new App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? main=null;UpdateWindow? window=null;
        try
        {
            var database=Path.Combine(folder,"PRIVATE-WT-KO-study.tabajara");ProcessedProject.CreateNew(database);var store=ProcessedProject.OpenProject(database);
            var source=Path.Combine(folder,"synthetic-source.bin");File.WriteAllText(source,"Synthetic update history source");
            var asset=new VideoAsset{Path=source,SourceSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant(),Metadata=new(200,200,10,2),Settings=new(){Cx=.5,Cy=.5,Rx=.4,Ry=.4,ArenaConfirmed=true}};store.SaveVideo(asset);
            var points=new List<TrackPoint>{new(0,0,10,10,PointStatus.Tracked,1),new(1,.1,11,10,PointStatus.Tracked,1)};
            var raw=new AnalysisRun{VideoId=asset.Id,SourceSha256=asset.SourceSha256,Metadata=asset.Metadata,Settings=asset.Settings,Points=points,Metrics=MetricCalculator.Calculate(points,asset.Metadata,asset.Settings),AlgorithmContract=AlgorithmIdentity.Contract,NominalTimingVerified=true};store.SaveRun(raw);
            var model=new MainViewModel(new WorkspaceService(store)){Blind=true};var status=model.Status;main=new(model);var client=new Client();main.ConfigureUpdateService(client,Path.Combine(folder,"preferences"));
            main.UpdateVerifier=(_,t)=>{t.ThrowIfCancellationRequested();return Task.CompletedTask;};main.UpdateShortcut=(_,_)=>{};main.UpdateWarning=_=>{};
            main.Show();Pump(main.InitializeAsync());Assert.Equal(0,client.Checks);status=model.Status;
            ((Button)main.FindName("UpdatesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window=main.UpdatesWindow!;Assert.NotNull(window);
            window.Width=1050;window.Height=700;window.UpdateLayout();Assert.IsType<ScrollViewer>(window.Content);
            var viewport=(ScrollViewer)window.Content;window.CheckButton.BringIntoView();window.UpdateLayout();Assert.True(window.CheckButton.Focusable);Assert.True(window.CheckButton.Focus());
            var bounds=window.CheckButton.TransformToAncestor(viewport).TransformBounds(new Rect(0,0,window.CheckButton.ActualWidth,window.CheckButton.ActualHeight));Assert.True(bounds.Left>=0&&bounds.Right<=viewport.ActualWidth&&bounds.Top>=0&&bounds.Bottom<=viewport.ActualHeight);
            var repository=new DirectoryInfo(AppContext.BaseDirectory);while(repository is not null&&!Directory.Exists(Path.Combine(repository.FullName,"src","MouseTrackerTabajara.Desktop")))repository=repository.Parent;
            var evidence=Path.Combine(repository!.FullName,".workmap","logs","S45-update-ui","native-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(evidence);
            var bitmap=new RenderTargetBitmap(1050,700,96,96,PixelFormats.Pbgra32);bitmap.Render(viewport);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(evidence,"updates-1050x700.png")))encoder.Save(output);File.WriteAllText(Path.Combine(evidence,"fixture-path.txt"),folder);
            window.CheckButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window.PendingOperation);Assert.NotNull(window.Available);window.PrepareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window.PendingOperation);Assert.NotNull(window.Prepared);
            window.PrereleasesInput.IsChecked=false;Assert.Null(window.Available);Assert.Null(window.Prepared);
            main.SetUpdateOptions(new(false,false));Pump(main.CheckAutomaticUpdatesAsync());Assert.Equal(1,client.Checks);
            var delayed=new TaskCompletionSource<UpdateRelease?>();client.Check=_=>delayed.Task;main.SetUpdateOptions(new(true,true));var automatic=main.CheckAutomaticUpdatesAsync();main.SetUpdateOptions(new(false,true));delayed.SetResult(Client.Release);Pump(automatic);Assert.DoesNotContain("available",main.UpdateStatusText,StringComparison.OrdinalIgnoreCase);Assert.Equal(status,model.Status);
            client.Check=_=>Task.FromResult<UpdateRelease?>(Client.Release);Pump(window.CheckAsync());Pump(window.PrepareAsync());
            model.Busy=true;Assert.ThrowsAsync<InvalidOperationException>(()=>main.RestartPreparedUpdateAsync(client.Prepared,window)).GetAwaiter().GetResult();model.Busy=false;
            var other=new Window{Owner=main};other.Show();Assert.ThrowsAsync<InvalidOperationException>(()=>main.RestartPreparedUpdateAsync(client.Prepared,window)).GetAwaiter().GetResult();other.Close();
            var original=SharedBytes(database);string? working=null;main.UpdateLauncher=(_,project,_,_)=>{working=project;return Task.FromResult(false);};
            Exception? launchFailure=null;try{Pump(main.RestartPreparedUpdateAsync(client.Prepared,window));}catch(Exception ex){launchFailure=ex;}
            Assert.IsType<InvalidOperationException>(launchFailure);Assert.True(main.IsVisible);Assert.False(model.Busy);Assert.True(((CheckBox)main.FindName("BlindReview")).IsEnabled);Assert.Equal(original,SharedBytes(database));
            Assert.NotNull(working);Assert.NotEqual(database,working);Assert.Contains("update-working",working);Assert.Single(Directory.GetFiles(folder,"*.before-update-*.tabajara"));Assert.True(File.Exists(working));
            var pristine=Directory.GetFiles(folder,"*.before-update-*.tabajara").Single();Assert.Equal(JsonDefaults.Serialize(raw),JsonDefaults.Serialize(ProcessedProject.OpenProject(pristine).LatestRun(asset.Id)));Assert.Equal(JsonDefaults.Serialize(raw),JsonDefaults.Serialize(ProcessedProject.OpenProject(working!).LatestRun(asset.Id)));
            var invalid=Path.Combine(folder,"invalid-identity");Directory.CreateDirectory(invalid);File.WriteAllText(Path.Combine(invalid,"build-identity.json"),"invalid json");main.ReadUpdateIdentity(invalid);Assert.False(main.UpdateIdentityVerified);var checks=client.Checks;Pump(main.CheckAutomaticUpdatesAsync());Pump(window.CheckAsync());Assert.Equal(checks,client.Checks);Assert.Null(window.Available);main.ReadUpdateIdentity(Path.Combine(folder,"development-missing"));Assert.True(main.UpdateIdentityVerified);
            Pump(window.CheckAsync());Pump(window.PrepareAsync());
            using(var cancelled=new CancellationTokenSource()){main.UpdateVerifier=(_,_)=>{cancelled.Cancel();return Task.FromCanceled(cancelled.Token);};Exception? cancelledError=null;try{Pump(main.RestartPreparedUpdateAsync(client.Prepared,window,cancelled.Token));}catch(Exception ex){cancelledError=ex;}Assert.IsAssignableFrom<OperationCanceledException>(cancelledError);}
            Assert.True(main.IsVisible);Assert.False(model.Busy);main.UpdateVerifier=(_,_)=>Task.CompletedTask;
            var shortcutCalled=false;main.UpdateShortcut=(_,project)=>{Assert.Equal(working,project);shortcutCalled=true;};
            using(var lateCancel=new CancellationTokenSource()){main.UpdateLauncher=(_,project,_,_)=>{working=project;lateCancel.Cancel();return Task.FromResult(true);};Exception? cancelledError=null;try{Pump(main.RestartPreparedUpdateAsync(client.Prepared,window,lateCancel.Token));}catch(Exception ex){cancelledError=ex;}Assert.IsAssignableFrom<OperationCanceledException>(cancelledError);Assert.True(main.IsVisible);Assert.False(shortcutCalled);Assert.False(model.Busy);}
            main.UpdateLauncher=(_,project,ready,_)=>{working=project;var info=MainWindow.BuildUpdateStartInfo(client.Prepared,project,ready,model.Blind);Assert.Contains("--blind-review",info.ArgumentList);Assert.Contains(project,info.ArgumentList);Assert.False(info.UseShellExecute);return Task.FromResult(true);};window.RestartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window.PendingOperation);Assert.True(shortcutCalled);Assert.False(main.IsVisible);Assert.Equal(original,SharedBytes(database));
        }
        finally{window?.Close();main?.Close();app.Shutdown();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();File.WriteAllText(Path.Combine(folder,"retained-evidence.txt"),"Synthetic update UI/handoff evidence; no real project data.\n");}
    }
    private static byte[] SharedBytes(string path){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var copy=new MemoryStream();stream.CopyTo(copy);return copy.ToArray();}
    private static void Pump(Task task)
    {
        if(!task.IsCompleted){var dispatcher=Dispatcher.CurrentDispatcher;var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(60)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false));timer.Start();Dispatcher.PushFrame(frame);timer.Stop();}
        Assert.True(task.IsCompleted);task.GetAwaiter().GetResult();
    }
}
