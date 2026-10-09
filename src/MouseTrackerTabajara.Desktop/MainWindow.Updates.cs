using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MouseTrackerTabajara.Application;

namespace MouseTrackerTabajara.Desktop;

public partial class MainWindow
{
    private readonly HttpClient _updateHttp=new(){Timeout=TimeSpan.FromMinutes(25)};
    private readonly CancellationTokenSource _updateLifetime=new();
    private readonly DispatcherTimer _updateTimer=new(){Interval=TimeSpan.FromHours(12)};
    private bool _updateChecking,_updateHandoff,_updatesClosed,_handoffBlind;
    private CancellationTokenSource? _automaticCheck;
    private UpdateOptions _updateOptions=new();
    private string _updateSettingsRoot=UpdateLocalStorage.Root;
    public UpdateWindow? UpdatesWindow{get;private set;}
    public IUpdateClient UpdateClient{get;private set;}=null!;
    public string UpdateVersion{get;private set;}="0.0.0";
    public bool UpdateIdentityVerified{get;private set;}=true;
    public bool InteractiveUpdatesActivated{get;private set;}
    public string UpdateStatusText=>UpdateStatus.Text;
    public Func<PreparedUpdate,string,string,CancellationToken,Task<bool>> UpdateLauncher{get;set;}=null!;
    public Func<PreparedUpdate,CancellationToken,Task> UpdateVerifier{get;set;}=(p,t)=>Task.Run(()=>ReleaseUpdater.ValidatePrepared(p,t),t);
    public Action<PreparedUpdate,string> UpdateShortcut{get;set;}=null!;
    public Action<string> UpdateWarning{get;set;}=text=>MessageBox.Show(text,"Application update",MessageBoxButton.OK,MessageBoxImage.Information);
    private void InitializeUpdates()
    {
        UpdateShortcut=(p,project)=>CreateStartMenuShortcut(p,project,_handoffBlind);
        UpdateLauncher=(p,project,ready,t)=>LaunchAndConfirmAsync(p,project,ready,_handoffBlind,t);
        ReadUpdateIdentity(AppContext.BaseDirectory);
        _updateOptions=UpdateLocalStorage.Read(_updateSettingsRoot);
        UpdateClient=new NativeUpdateClient(new ReleaseUpdater(_updateHttp,Path.Combine(UpdateLocalStorage.Root,"versions")));
        UpdateStatus.Text=UpdateIdentityVerified?"Updates: "+(UpdateVersion=="0.0.0"?"development build, version unknown":UpdateVersion):"Installed identity unverified; update checks/install held. Open Updates for the trusted repair page.";
        _updateTimer.Tick+=async(_,_)=>await CheckAutomaticUpdatesAsync();
        Closing+=(_,e)=>{if(_updateHandoff)e.Cancel=true;};
        Closed+=(_,_)=>{_updatesClosed=true;_updateTimer.Stop();_automaticCheck?.Cancel();_updateLifetime.Cancel();_updateHttp.Dispose();_updateLifetime.Dispose();};
    }
    public void ReadUpdateIdentity(string appDirectory)
    {
        try
        {
            var basePath=Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
            foreach(var directory in new[]{basePath,Path.GetDirectoryName(basePath)})
                if(directory is not null)try{_ = File.GetAttributes(Path.Combine(directory,"build-identity.json"));}catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
            UpdateVersion=ReleaseUpdater.ReadCurrentVersion(appDirectory,"0.0.0");UpdateIdentityVerified=true;
        }
        catch(Exception){UpdateVersion="0.0.0";UpdateIdentityVerified=false;}
    }
    public void ConfigureUpdateService(IUpdateClient client,string settingsRoot)
    {UpdateClient=client;_updateSettingsRoot=settingsRoot;_updateOptions=UpdateLocalStorage.Read(settingsRoot);}
    public void SetUpdateOptions(UpdateOptions options)
    {
        _updateOptions=options;UpdateStatus.Text="Updates: "+(UpdateVersion=="0.0.0"?"development build, version unknown":UpdateVersion);
        if(!options.Automatic){_updateTimer.Stop();_automaticCheck?.Cancel();}else if(IsVisible)_updateTimer.Start();
        UpdateLocalStorage.Write(_updateSettingsRoot,options);
    }
    public UpdateOptions UpdateSettings=>_updateOptions;
    public void StartAutomaticUpdates()
    {InteractiveUpdatesActivated=true;if(!_updateOptions.Automatic)return;_updateTimer.Start();_=CheckAutomaticUpdatesAsync();}
    public async Task CheckAutomaticUpdatesAsync()
    {
        if(_updatesClosed||!UpdateIdentityVerified||!_updateOptions.Automatic||_updateChecking||_updateLifetime.IsCancellationRequested)return;
        _updateChecking=true;var channel=_updateOptions.IncludePrereleases;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);_automaticCheck=timeout;timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var release=await UpdateClient.CheckAsync(UpdateVersion,channel,timeout.Token);
            if(!_updatesClosed&&_updateOptions.Automatic&&channel==_updateOptions.IncludePrereleases)UpdateStatus.Text=release is null?"Updates: "+(UpdateVersion=="0.0.0"?"development build, version unknown":UpdateVersion):"Update available: "+release.Version+" · open Updates";
        }
        catch(Exception){/* Automatic offline/no-update checks are quiet and do not alter study status. */}
        finally{_automaticCheck=null;_updateChecking=false;}
    }
    private void OpenUpdates(object sender,RoutedEventArgs e)
    {
        if(UpdatesWindow is {IsVisible:true} existing){existing.Activate();return;}
        var window=new UpdateWindow(this,UpdateClient,UpdateVersion){Owner=this};UpdatesWindow=window;
        window.Closed+=(_,_)=>{if(ReferenceEquals(UpdatesWindow,window))UpdatesWindow=null;};window.Show();
    }
    private void RequireUpdateIdle(UpdateWindow? allowed)
    {
        if(_model.Busy&&!_updateHandoff||OwnedWindows.Cast<Window>().Any(w=>!ReferenceEquals(w,allowed)))
            throw new InvalidOperationException("Finish processing and close other review/report windows before restarting.");
        if(allowed is not null&&!ReferenceEquals(allowed.Owner,this))throw new InvalidOperationException("Update window belongs to another workspace.");
    }
    public async Task<ProcessedProjectReceipt> RestartPreparedUpdateAsync(PreparedUpdate prepared,UpdateWindow? allowed=null,CancellationToken token=default)
    {
        if(_updateHandoff)throw new InvalidOperationException("An update handoff is already active.");
        if(!UpdateIdentityVerified)throw new InvalidDataException("Installed identity is unverified; repair the installation from the trusted release page.");
        RequireUpdateIdle(allowed);CommitPendingProjectEdits();var original=_model.StudyPath;
        _handoffBlind=_model.Blind;BlindReview.IsEnabled=false;_updateHandoff=true;_model.Busy=true;_projectSnapshotActive=true;
        try
        {
            await DrainProjectEditsAsync();RequireUpdateIdle(allowed);token.ThrowIfCancellationRequested();
            await UpdateVerifier(prepared,token);RequireUpdateIdle(allowed);token.ThrowIfCancellationRequested();
            var checkpoint=Path.Combine(Path.GetDirectoryName(original)!,Path.GetFileNameWithoutExtension(original)+".before-update-"+Guid.NewGuid().ToString("N")+".tabajara");
            var backup=await Task.Run(()=>ProcessedProject.SaveSnapshot(original,checkpoint),token);
            var working=Path.Combine(Path.GetDirectoryName(original)!,Path.GetFileNameWithoutExtension(original)+".update-working-"+Guid.NewGuid().ToString("N")+".tabajara");
            var receipt=await Task.Run(()=>ProcessedProject.SaveSnapshot(backup.ProjectPath,working),token);
            await UpdateVerifier(prepared,token);
            RequireUpdateIdle(allowed);token.ThrowIfCancellationRequested();
            if(!string.Equals(original,_model.StudyPath,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Workspace changed during update handoff.");
            var ready=Path.Combine(UpdateLocalStorage.SafeDirectory("handoffs",_updateSettingsRoot),Guid.NewGuid().ToString("N")+".json");
            if(!await UpdateLauncher(prepared,receipt.ProjectPath,ready,token))throw new InvalidOperationException("The new version did not confirm startup. Current workspace remains open; the checkpoint is retained.");
            token.ThrowIfCancellationRequested();RequireUpdateIdle(allowed);
            try{UpdateShortcut(prepared,receipt.ProjectPath);}catch(Exception){UpdateWarning("The new version is ready, but its Start Menu shortcut could not be created. The old installation and pristine checkpoint remain available. Launch the installed version from its version folder; do not use the old downloaded shortcut.");}
            UpdateStatus.Text="New version ready with a separate project checkpoint.";
            _projectSnapshotActive=false;_model.Busy=false;_updateHandoff=false;allowed?.CloseAfterHandoff();Close();return receipt;
        }
        catch{UpdateStatus.Text="Update restart held. Current workspace and any checkpoint are retained.";throw;}
        finally{_projectSnapshotActive=false;_model.Busy=false;_updateHandoff=false;BlindReview.IsEnabled=true;}
    }
    private static void CreateStartMenuShortcut(PreparedUpdate update,string project,bool blind)
    {
        var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"RatoObservatorTabajara");
        UpdateLocalStorage.EnsureDirectory(directory);
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Shortcut folder cannot be redirected.");
        var path=Path.Combine(directory,"Rato Observator Tabajara.lnk");
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Shortcut cannot be redirected.");
        if(project.Contains('"'))throw new IOException("Project path is invalid.");
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("Windows shortcut service unavailable."))!;
        object? shortcut=null;
        try{dynamic link=shell.CreateShortcut(path);shortcut=link;link.TargetPath=update.ExecutablePath;link.Arguments="--study \""+project+"\""+(blind?" --blind-review":"");link.WorkingDirectory=Path.GetDirectoryName(update.ExecutablePath);link.Description="Rato Observator Tabajara · verified installed version";link.Save();}
        finally{if(shortcut is not null)System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);}
    }
    public static ProcessStartInfo BuildUpdateStartInfo(PreparedUpdate update,string project,string ready,bool blind)
    {
        var start=new ProcessStartInfo(update.ExecutablePath){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(update.ExecutablePath)!};
        foreach(var arg in new[]{"--study",project,"--update-ready",ready})start.ArgumentList.Add(arg);if(blind)start.ArgumentList.Add("--blind-review");return start;
    }
    private static async Task<bool> LaunchAndConfirmAsync(PreparedUpdate update,string project,string ready,bool blind,CancellationToken token)
    {
        var start=new ProcessStartInfo(update.ExecutablePath){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(update.ExecutablePath)!};
        foreach(var arg in new[]{"--study",project,"--update-ready",ready})start.ArgumentList.Add(arg);if(blind)start.ArgumentList.Add("--blind-review");
        using var child=Process.Start(start)??throw new IOException("New version could not be launched.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while(!timeout.IsCancellationRequested)
        {
            if(child.HasExited)return false;
            if(File.Exists(ready))
            {
                var signal=JsonSerializer.Deserialize<UpdateReadySignal>(File.ReadAllText(ready));
                return !child.HasExited&&signal is not null&&signal.ProcessId==child.Id&&signal.StartUtcTicks==child.StartTime.ToUniversalTime().Ticks&&signal.Nonce==Path.GetFileNameWithoutExtension(ready)&&signal.Version==update.Version&&signal.Blind==blind&&string.Equals(signal.ProjectPath,Path.GetFullPath(project),StringComparison.OrdinalIgnoreCase);
            }
            try{await Task.Delay(50,timeout.Token);}catch(OperationCanceledException){return false;}
        }
        return false;
    }
}

public sealed record UpdateReadySignal(int ProcessId,long StartUtcTicks,string Nonce,string Version,string ProjectPath,bool Blind);
public partial class App
{
    private static void SignalUpdateReady(string? path,string? project,bool blind)
    {
        if(path is null)return;
        if(project is null)throw new InvalidDataException("Update startup must name its separate project copy.");
        var root=UpdateLocalStorage.SafeDirectory("handoffs");var full=Path.GetFullPath(path);
        if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Path.GetDirectoryName(full)!=root||!Guid.TryParseExact(Path.GetFileNameWithoutExtension(full),"N",out _)||File.Exists(full))
            throw new InvalidDataException("Update readiness path is not a fresh controlled handoff token.");
        var temporary=full+".tmp";using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {using var process=Process.GetCurrentProcess();JsonSerializer.Serialize(stream,new UpdateReadySignal(process.Id,process.StartTime.ToUniversalTime().Ticks,Path.GetFileNameWithoutExtension(full),ReleaseUpdater.ReadCurrentVersion(AppContext.BaseDirectory,"0.0.0"),Path.GetFullPath(project),blind));stream.Flush(true);}
        File.Move(temporary,full,false);
    }
}
