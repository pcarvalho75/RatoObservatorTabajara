using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;
using Xunit.Abstractions;
namespace MouseTrackerTabajara.Validation.Tests;
public class StartupHostIsolationTests(ITestOutputHelper output)
{
    private string Folder(){var p=Path.Combine(Path.GetTempPath(),"tabajara-s41-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);output.WriteLine("retained synthetic evidence="+p);return p;}
    private static Process Launch(string folder,string name,string? diagnostics)
    {
        var exe=App.FindRepositoryFile("src/MouseTrackerTabajara.Desktop/bin/Release/net10.0-windows/MouseTrackerTabajara.exe");Assert.NotNull(exe);
        var start=new ProcessStartInfo(exe!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in new[]{"--study",Path.Combine(folder,name+".tabajara"),"--smoke-screenshot",Path.Combine(folder,name+".png")})start.ArgumentList.Add(arg);
        if(diagnostics is not null){start.ArgumentList.Add("--diagnostics");start.ArgumentList.Add(diagnostics);}return Process.Start(start)!;
    }
    private async Task<int> Finish(Process child)
    {
        var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
        try{using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));await child.WaitForExitAsync(deadline.Token);output.WriteLine(await stdout);output.WriteLine(await stderr);return child.ExitCode;}
        finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
    }
    [Fact] public async Task ActualApphostExplicitStudyDiagnosticsAndSmokeRemainOperational()
    {
        var folder=Folder();var log=Path.Combine(folder,"explicit.log");using var child=Launch(folder,"explicit",log);Assert.Equal(0,await Finish(child));
        Assert.True(File.Exists(Path.Combine(folder,"explicit.png")));Assert.Contains("startup, layout and screenshot completed",File.ReadAllText(log));Assert.Empty(new StudyStore(Path.Combine(folder,"explicit.tabajara")).Videos());
    }
    [Fact] public async Task ConcurrentActualApphostsUseSeparateDefaultSessionLogs()
    {
        var folder=Folder();using var first=Launch(folder,"first",null);using var second=Launch(folder,"second",null);var firstPid=first.Id;var secondPid=second.Id;
        var results=await Task.WhenAll(Finish(first),Finish(second));Assert.All(results,x=>Assert.Equal(0,x));
        var logs=Path.GetDirectoryName(StudyLocation.DiagnosticsLog(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))!;
        var one=Assert.Single(Directory.GetFiles(logs,$"startup-{firstPid}-*.log"));var two=Assert.Single(Directory.GetFiles(logs,$"startup-{secondPid}-*.log"));Assert.NotEqual(one,two);
        Assert.Contains("startup, layout and screenshot completed",File.ReadAllText(one));Assert.Contains("startup, layout and screenshot completed",File.ReadAllText(two));output.WriteLine("retained default process logs="+one+";"+two);
        Assert.True(File.Exists(Path.Combine(folder,"first.png")));Assert.True(File.Exists(Path.Combine(folder,"second.png")));
    }
    [Fact] public async Task CompetingExplicitLogHandleReturnsControlledFailureWithoutMutation()
    {
        var folder=Folder();var log=Path.Combine(folder,"held.log");var content=System.Text.Encoding.UTF8.GetBytes("retained competing logger evidence");
        using(var owner=new FileStream(log,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
        {
            owner.Write(content);owner.Flush(true);using var child=Launch(folder,"blocked",log);Assert.Equal(1,await Finish(child));Assert.True(owner.CanWrite);Assert.False(File.Exists(Path.Combine(folder,"blocked.png")));
        }
        Assert.Equal(content,File.ReadAllBytes(log));Assert.False(File.Exists(Path.Combine(folder,"blocked.tabajara")));
    }
    [Fact] public async Task EmbeddedConcreteAppOwnsOnlyHostWindowAndDoesNotCreateDefaultDiagnostics()
    {
        const string variable="TABAJARA_S41_EMBEDDED_CHILD";
        if(Environment.GetEnvironmentVariable(variable)!="1")
        {
            var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"test",typeof(StartupHostIsolationTests).Assembly.Location,"--filter","FullyQualifiedName="+typeof(StartupHostIsolationTests).FullName+"."+nameof(EmbeddedConcreteAppOwnsOnlyHostWindowAndDoesNotCreateDefaultDiagnostics),"--logger","console;verbosity=detailed"})start.ArgumentList.Add(arg);start.Environment[variable]="1";
            using var child=Process.Start(start)!;Assert.Equal(0,await Finish(child));return;
        }
        Exception? error=null;var thread=new Thread(()=>
        {
            try
            {
                Assert.NotEqual(typeof(App).Assembly,System.Reflection.Assembly.GetEntryAssembly());
                var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);var logFolder=Path.GetDirectoryName(StudyLocation.DiagnosticsLog(local))!;
                string[] Before()=>Directory.Exists(logFolder)?Directory.GetFiles(logFolder,$"startup-{Environment.ProcessId}-*.log"):[];
                var before=Before();var app=new App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;var sentinel=new Window{Title="Synthetic embedded host sentinel",Width=300,Height=200};app.MainWindow=sentinel;
                try{sentinel.Show();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);Assert.Same(sentinel,app.MainWindow);Assert.Single(app.Windows.Cast<Window>());Assert.Equal(before,Before());Assert.NotNull(app.Resources["Ink"]);}
                finally{sentinel.Close();app.Shutdown();}
            }
            catch(Exception ex){error=ex;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(30)));if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
