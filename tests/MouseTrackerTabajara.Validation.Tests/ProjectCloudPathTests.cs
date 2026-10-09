using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Diagnostics;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Validation.Tests;

public sealed class ProjectCloudPathTests(ITestOutputHelper output)
{
    private static string WorkspaceArtifacts
    {
        get
        {
            for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
                if(File.Exists(System.IO.Path.Combine(directory.FullName,"MouseTrackerTabajara.sln")))return System.IO.Path.Combine(directory.FullName,"artifacts");
            throw new DirectoryNotFoundException("Current checked-out MouseTrackerTabajara repository was not found.");
        }
    }
    [Fact]
    public void WorkspaceNewSnapshotOpenRunsAndReportsActualCloudEvidence()
    {
        var folder=System.IO.Path.Combine(WorkspaceArtifacts,"project-cloud-audit",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);output.WriteLine("retained workspace evidence="+folder);
        ReportWorkspaceTags(output.WriteLine);
        var first=System.IO.Path.Combine(folder,"new.tabajara");var created=ProcessedProject.CreateNew(first);Assert.Equal(0,created.Recordings);
        var opened=ProcessedProject.OpenProject(first);Assert.Empty(opened.Videos());
        var saved=ProcessedProject.SaveSnapshot(first,System.IO.Path.Combine(folder,"snapshot.tabajara"));Assert.Empty(ProcessedProject.OpenProject(saved.ProjectPath).Videos());
    }
    [Fact]
    public void OnlyDocumentedCloudFamilyWithoutNameSurrogatesIsAllowed()
    {
        var type=typeof(ProcessedProject).Assembly.GetType("MouseTrackerTabajara.Application.ProjectReparseTags");Assert.NotNull(type);
        var predicate=type!.GetMethod("IsAllowedCloudTag",BindingFlags.NonPublic|BindingFlags.Static);Assert.NotNull(predicate);
        bool Allowed(uint value)=>(bool)predicate!.Invoke(null,[value])!;
        for(uint variant=0;variant<16;variant++)Assert.True(Allowed(0x9000001Au|(variant<<12)));
        foreach(var denied in new uint[]{0,0xA0000003,0xA000000C,0xA000001D,0x9000001C,0x80000021,0x80000015,0x9001001A,0xB000701A,0x12345678})Assert.False(Allowed(denied));
        output.WriteLine("PASS16CLOUDtags; mountpoint/symlink/WSL/ProjFS/legacyunused/unknown/reserved/name-surrogate variants rejected.");
    }
    [Fact]
    public async Task ActualJunctionAndSymbolicLinksRemainRejectedWithoutTouchingTargets()
    {
        var folder=System.IO.Path.Combine(WorkspaceArtifacts,"project-cloud-audit",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);output.WriteLine("retained link evidence="+folder);
        var source=System.IO.Path.Combine(folder,"original.tabajara");ProcessedProject.CreateNew(source);
        var target=System.IO.Path.Combine(folder,"target");Directory.CreateDirectory(target);var marker=System.IO.Path.Combine(target,"marker.keep");File.WriteAllText(marker,"preserve synthetic link target");
        var junction=System.IO.Path.Combine(folder,"junction");var launch=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"/c","mklink","/J",junction,target})launch.ArgumentList.Add(argument);
        using(var process=Process.Start(launch)!) {await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(0,process.ExitCode);output.WriteLine(await process.StandardOutput.ReadToEndAsync());}
        Assert.Equal(0xA0000003u,ProjectCloudAuditTag(junction));
        Assert.Throws<IOException>(()=>ProcessedProject.SaveSnapshot(source,System.IO.Path.Combine(junction,"refused.tabajara")));Assert.False(File.Exists(System.IO.Path.Combine(target,"refused.tabajara")));Assert.Equal("preserve synthetic link target",File.ReadAllText(marker));
        var link=System.IO.Path.Combine(folder,"symbolic.tabajara");
        try
        {
            File.CreateSymbolicLink(link,source);Assert.Equal(0xA000000Cu,ProjectCloudAuditTag(link));Assert.Throws<IOException>(()=>ProcessedProject.OpenProject(link));
            output.WriteLine("PASS actual symbolic link rejection.");
        }
        catch(UnauthorizedAccessException){output.WriteLine("Actual symlink creation UNRUN: host denies privilege; tag classification remains tested. No feature/privilege change attempted.");}
        catch(IOException e) when((e.HResult&0xffff)==1314){output.WriteLine("Actual symlink creation UNRUN: Win32 privilege1314 denied; tag classification remains tested. No feature/privilege change attempted.");}
        var sha=MouseTrackerTabajara.Tracking.OpenCvTracker.HashFile(source);Assert.Throws<IOException>(()=>ProcessedProject.SaveSnapshot(source,source));Assert.Equal(sha,MouseTrackerTabajara.Tracking.OpenCvTracker.HashFile(source));
    }
    [Fact]
    public async Task NativeWorkspaceNewSaveOpenRetainsSyntheticRecording()
    {
        if(Environment.GetEnvironmentVariable("TABAJARA_CLOUD_NATIVE")!="native")
        {
            var project=App.FindRepositoryFile("tests/MouseTrackerTabajara.Validation.Tests/MouseTrackerTabajara.Validation.Tests.csproj");Assert.NotNull(project);
            var receipts=System.IO.Path.Combine(WorkspaceArtifacts,"project-cloud-audit","native-receipts-"+Guid.NewGuid().ToString("N"));
            var launch=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"test",project!,"-c","Release","--no-build","--no-restore","--filter","FullyQualifiedName~NativeWorkspaceNewSaveOpen","--logger","trx;LogFileName=native-cloud.trx","--results-directory",receipts})launch.ArgumentList.Add(arg);
            launch.Environment["TABAJARA_CLOUD_NATIVE"]="native";using var process=Process.Start(launch)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));output.WriteLine(await stdout);output.WriteLine(await stderr);Assert.Equal(0,process.ExitCode);}
            finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
            return;
        }
        Exception? error=null;var lines=new List<string>();var thread=new Thread(()=>
        {
            App? app=null;MainWindow? window=null;
            try
            {
                var folder=System.IO.Path.Combine(WorkspaceArtifacts,"project-cloud-audit",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);lines.Add("native workspace evidence="+folder);ReportWorkspaceTags(lines.Add);
                var videos=new ThirtyRecordingPerformanceFixture(System.IO.Path.Combine(folder,"synthetic-videos"),1,24);
                var seed=System.IO.Path.Combine(folder,"native-seed","seed.db");
                app=new App();app.InitializeComponent();SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window=new MainWindow(new MainViewModel(new WorkspaceService(new StudyStore(seed))));app.MainWindow=window;window.Show();Pump(window.InitializeAsync());
                var create=window.NewProjectAsync(System.IO.Path.Combine(folder,"working.tabajara"));Pump(create);window=create.Result;
                var model=(MainViewModel)window.DataContext;var asset=model.Workspace.Import(videos.Workloads[0].Snapshot.Imported[0].Path) with{GroupName="synthetic-cloud-group"};model.Workspace.Store.SaveVideo(asset);model.Reload();
                var save=window.SaveProcessedProjectAsync(System.IO.Path.Combine(folder,"saved.tabajara"));Pump(save);Assert.Equal(1,save.Result.Recordings);
                var reopen=window.OpenProjectAsync(save.Result.ProjectPath);Pump(reopen);window=reopen.Result;var restored=((MainViewModel)window.DataContext).Rows;
                Assert.Single(restored);Assert.Equal(asset.SourceSha256,restored[0].Asset.SourceSha256);Assert.Equal("synthetic-cloud-group",restored[0].Asset.GroupName);
                lines.Add("PASS native New/Save/Open under current workspace ignored artifacts; synthetic source/hash/group retained.");
            }
            catch(Exception e){error=e;}
            finally{window?.Close();app?.Shutdown();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(60)));foreach(var line in lines)output.WriteLine(line);if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void Pump(Task task)
    {
        var dispatcher=Dispatcher.CurrentDispatcher;var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(20)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false));timer.Start();Dispatcher.PushFrame(frame);timer.Stop();Assert.True(task.IsCompleted);task.GetAwaiter().GetResult();
    }
    private static void ReportWorkspaceTags(Action<string> write)
    {
        var cloud=false;
        for(var directory=new DirectoryInfo(WorkspaceArtifacts);directory is not null;directory=directory.Parent)
        {
            var root=System.IO.Path.GetPathRoot(directory.FullName);
            var tag=string.Equals(directory.FullName,root,StringComparison.OrdinalIgnoreCase)?0:ProjectCloudAuditTag(directory.FullName);
            cloud|=(tag&0xFFFF0FFFu)==0x9000001Au;
            write($"workspace ancestor={directory.FullName};attrs={(uint)File.GetAttributes(directory.FullName)};actualWin32Tag=0x{tag:X8};linkTarget={directory.LinkTarget??"null"}");
        }
        write(cloud?"Actual cloud ancestor PASS: documented Cloud Files tag observed for this workspace; ordinary workflow remains required.":"Cloud-specific subproof UNRUN: this workspace has no observed Cloud Files ancestor; ordinary New/Save/Open workflow still runs.");
    }
    // Independent query in the test records the actual environment instead of pretending that
    // FILE_ATTRIBUTE_REPARSE_POINT or a null LinkTarget proves either a link or a cloud tag.
    internal static uint ProjectCloudAuditTag(string path)
    {
        var handle=FindFirstFileW(System.IO.Path.GetFullPath(path).TrimEnd('\\'),out var data);
        if(handle==new IntPtr(-1))throw new IOException("Independent reparse-tag query failed: "+Marshal.GetLastWin32Error());
        try{return(data.Attributes&0x400)!=0?data.Reserved0:0;}finally{FindClose(handle);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct FindData
    {
        public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;
        public uint SizeHigh,SizeLow,Reserved0,Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Filename;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=14)]public string Alternate;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern IntPtr FindFirstFileW(string path,out FindData data);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool FindClose(IntPtr handle);
}
