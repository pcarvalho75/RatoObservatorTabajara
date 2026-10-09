using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using MouseTrackerTabajara.Application;

namespace MouseTrackerTabajara.Desktop;

public sealed record UpdateOptions(bool Automatic=true,bool IncludePrereleases=true);
public interface IUpdateClient
{
    Task<UpdateRelease?> CheckAsync(string version,bool prereleases,CancellationToken token);
    Task<PreparedUpdate> PrepareAsync(UpdateRelease release,IProgress<UpdateProgress>? progress,CancellationToken token);
}
internal sealed class NativeUpdateClient(ReleaseUpdater updater):IUpdateClient
{
    public Task<UpdateRelease?> CheckAsync(string version,bool prereleases,CancellationToken token)=>updater.CheckAsync(version,prereleases,token);
    public Task<PreparedUpdate> PrepareAsync(UpdateRelease release,IProgress<UpdateProgress>? progress,CancellationToken token)=>updater.PrepareAsync(release,progress,token);
}
internal static class UpdateLocalStorage
{
    public static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RatoObservatorTabajara");
    public static string SafeDirectory(string child,string? configuredRoot=null)
    {var root=configuredRoot??Root;EnsureDirectory(root);var path=Path.Combine(root,child);EnsureDirectory(path);return path;}
    public static void EnsureDirectory(string path)
    {
        for(var current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            if(Directory.Exists(current)&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Update settings directory cannot be redirected.");
        Directory.CreateDirectory(path);
    }
    public static UpdateOptions Read(string root)
    {try{var file=Path.Combine(root,"updates.json");if(!File.Exists(file))return new();if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||new FileInfo(file).Length>4096)return new();return JsonSerializer.Deserialize<UpdateOptions>(File.ReadAllText(file))??new();}catch(Exception){return new();}}
    public static void Write(string root,UpdateOptions options)
    {
        EnsureDirectory(root);var file=Path.Combine(root,"updates.json");
        if(File.Exists(file)&&(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("Update settings file cannot be redirected.");
        var temporary=Path.Combine(root,"updates-"+Guid.NewGuid().ToString("N")+".tmp");File.WriteAllText(temporary,JsonSerializer.Serialize(options));File.Move(temporary,file,true);
    }
}

public sealed class UpdateWindow:Window
{
    private readonly MainWindow _owner;private readonly IUpdateClient _client;private readonly string _version;
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(6)};
    private readonly TextBlock _release=new(){TextWrapping=TextWrapping.Wrap,Margin=new(6)};
    private readonly Button _check=new(){Content="Check now"},_prepare=new(){Content="Download and prepare"},_restart=new(){Content="Restart in new version"},_cancel=new(){Content="Cancel"};
    private readonly CheckBox _automatic=new(){Content="Check automatically at startup and every 12 hours"},_prereleases=new(){Content="Include pilot prereleases"};
    private CancellationTokenSource? _operation;private bool _closed,_busy,_settingsReady,_restarting,_closeRequested,_allowClose;
    public UpdateRelease? Available{get;private set;}public PreparedUpdate? Prepared{get;private set;}
    public Task PendingOperation{get;private set;}=Task.CompletedTask;
    public string StatusText=>_status.Text;public Button CheckButton=>_check;public Button PrepareButton=>_prepare;public Button RestartButton=>_restart;
    public CheckBox AutomaticInput=>_automatic;public CheckBox PrereleasesInput=>_prereleases;
    public UpdateWindow(MainWindow owner,IUpdateClient client,string version)
    {
        _owner=owner;_client=client;_version=version;Title="Application updates";Background=System.Windows.Media.Brushes.WhiteSmoke;Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20,62,89));Width=1050;Height=700;MinWidth=1050;MinHeight=700;
        var panel=new StackPanel{Margin=new(24)};panel.Children.Add(new TextBlock{Text="Application updates",FontSize=25});
        panel.Children.Add(new TextBlock{Text="Current version: "+(!owner.UpdateIdentityVerified?"installed identity unverified; updates held":version=="0.0.0"?"development build, package version unknown":version),TextWrapping=TextWrapping.Wrap,Margin=new(6)});
        panel.Children.Add(new TextBlock{Text="Trusted source: github.com/pcarvalho75/RatoObservatorTabajara. Checks send no study data or filenames. Downloads are verified into a separate version folder; the current installation is retained.",TextWrapping=TextWrapping.Wrap,Margin=new(6)});
        _automatic.IsChecked=owner.UpdateSettings.Automatic;_prereleases.IsChecked=owner.UpdateSettings.IncludePrereleases;panel.Children.Add(_automatic);panel.Children.Add(_prereleases);
        var buttons=new WrapPanel();foreach(var b in new[]{_check,_prepare,_cancel,_restart}){b.Margin=new(6);b.Padding=new(12,8,12,8);buttons.Children.Add(b);}panel.Children.Add(buttons);
        var notes=new Button{Content="Open release notes in trusted repository",Margin=new(6),Padding=new(12,8,12,8)};notes.Click+=(_,_)=>
        {var uri=Available?.ReleaseUrl??new Uri("https://github.com/pcarvalho75/RatoObservatorTabajara/releases");if(uri.Scheme=="https"&&uri.Host=="github.com"&&uri.AbsolutePath.StartsWith("/pcarvalho75/RatoObservatorTabajara/releases",StringComparison.Ordinal))Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});};panel.Children.Add(notes);
        var installed=new Button{Content="Open prepared version folder",Margin=new(6),Padding=new(12,8,12,8)};installed.Click+=(_,_)=>{if(Prepared is {} prepared)Process.Start(new ProcessStartInfo(prepared.InstallDirectory){UseShellExecute=true});};panel.Children.Add(installed);
        panel.Children.Add(_release);panel.Children.Add(_status);panel.Children.Add(new TextBlock{Text="Restart waits for processing to finish and other review/report windows to close. Pending metadata is committed, then a pristine rollback checkpoint and a separate update-working project are created. The new version opens only the working copy. If startup is not confirmed, this workspace stays open and both copies are retained. A successful handoff updates the owned per-user Start Menu shortcut; old versions remain for rollback.",TextWrapping=TextWrapping.Wrap,Margin=new(6)});
        Content=new ScrollViewer{Background=System.Windows.Media.Brushes.WhiteSmoke,Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        _check.Click+=(_,_)=>PendingOperation=CheckAsync();_prepare.Click+=(_,_)=>PendingOperation=PrepareAsync();_restart.Click+=(_,_)=>PendingOperation=RestartAsync();_cancel.Click+=(_,_)=>_operation?.Cancel();
        _automatic.Checked+=SettingsChanged;_automatic.Unchecked+=SettingsChanged;_prereleases.Checked+=SettingsChanged;_prereleases.Unchecked+=SettingsChanged;_settingsReady=true;
        Closing+=(_,e)=>{if(_busy&&_restarting&&!_allowClose){_closeRequested=true;_operation?.Cancel();e.Cancel=true;}};
        Closed+=(_,_)=>{_closed=true;_operation?.Cancel();};Gates();
    }
    private void SettingsChanged(object sender,RoutedEventArgs e)
    {if(!_settingsReady)return;if(ReferenceEquals(sender,_prereleases)){Available=null;Prepared=null;_release.Text="Channel changed; check again before downloading.";Gates();}try{_owner.SetUpdateOptions(new(_automatic.IsChecked==true,_prereleases.IsChecked==true));}catch(Exception){_status.Text="Update preferences could not be saved; changes apply this session, but could not be saved.";}}
    private void Gates(){_prereleases.IsEnabled=!_busy;_check.IsEnabled=!_busy&&_owner.UpdateIdentityVerified;_prepare.IsEnabled=!_busy&&_owner.UpdateIdentityVerified&&Available is not null;_restart.IsEnabled=!_busy&&_owner.UpdateIdentityVerified&&Prepared is not null;_cancel.IsEnabled=_busy;}
    private async Task Run(Func<CancellationToken,Task> action)
    {
        if(_busy)return;_busy=true;var cancellation=new CancellationTokenSource();_operation=cancellation;Gates();
        try{if(!_owner.UpdateIdentityVerified)throw new InvalidDataException();await action(cancellation.Token);}catch(OperationCanceledException){if(!_closed)_status.Text="Update operation cancelled. Current installation and project are retained.";}
        catch(InvalidDataException){if(!_owner.UpdateIdentityVerified){Available=null;Prepared=null;_release.Text="Installed identity unverified; use the trusted release page for repair.";}if(!_closed)_status.Text=_owner.UpdateIdentityVerified?"Update verification rejected the package/version. Current installation and project are retained.":"Installed identity is unverified. Automatic/manual updates are held; the trusted release page remains available for repair.";}
        catch(System.Net.Http.HttpRequestException){if(!_closed)_status.Text="Release service is offline or unreachable. Check connection and try again; current installation is retained.";}
        catch(UnauthorizedAccessException){if(!_closed)_status.Text="Local update or checkpoint folder permission denied. Current installation and project are retained.";}
        catch(InvalidOperationException){if(!_closed)_status.Text="Restart held: finish processing, close other review/report windows, and resolve pending metadata; current workspace/checkpoints retained. Startup must be confirmed.";}
        catch(Exception){if(!_closed)_status.Text="Local update operation failed. Current installation/project and checkpoints are retained.";}
        finally{_busy=false;_operation=null;cancellation.Dispose();if(!_closed){Gates();if(_closeRequested)Close();}}
    }
    public Task CheckAsync()=>Run(async token=>
    {Available=await _client.CheckAsync(_version,_prereleases.IsChecked==true,token);Prepared=null;if(_closed)return;_release.Text=Available is null?"No newer eligible release.":"Available: "+Available.Version+" · "+Available.Bytes+" bytes · SHA256 "+Available.Sha256;_status.Text="Release check finished.";});
    public Task PrepareAsync()=>Run(async token=>
    {var release=Available??throw new InvalidOperationException();var progress=new Progress<UpdateProgress>(p=>{if(!_closed)_status.Text=p.Message+" · "+p.BytesReceived+" / "+p.TotalBytes+" bytes";});Prepared=await _client.PrepareAsync(release,progress,token);if(!_closed)_status.Text="Verified version prepared separately. Restart when your workspace is idle.";});
    public void CloseAfterHandoff(){_allowClose=true;Close();}
    public Task RestartAsync()=>Run(async token=>{_restarting=true;try{await _owner.RestartPreparedUpdateAsync(Prepared??throw new InvalidOperationException(),this,token);}finally{_restarting=false;}});
}
