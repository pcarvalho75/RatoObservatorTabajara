using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using MouseTrackerTabajara.Application;

namespace MouseTrackerTabajara.Desktop;

public partial class MainWindow
{
    private bool _projectSnapshotActive;
    public bool CanChangeProject => !_model.Busy && OwnedWindows.Count==0;

    public async Task<MainWindow> OpenProjectAsync(string path)
    {
        RequireProjectIdle();
        CommitPendingProjectEdits();
        _model.Busy=true;
        MainWindow? replacement=null;
        try
        {
            await DrainProjectEditsAsync();
            var store=ProcessedProject.OpenProject(path);
            var model=new MainViewModel(new WorkspaceService(store)){Blind=_model.Blind};
            replacement=new MainWindow(model);replacement.BlindReview.IsChecked=model.Blind;
            await replacement.InitializeAsync();
            if(OwnedWindows.Count!=0)throw new InvalidOperationException("Close review/report windows before changing projects.");
            replacement.Show();
            if(System.Windows.Application.Current?.MainWindow==this)System.Windows.Application.Current.MainWindow=replacement;
            Close();return replacement;
        }
        catch{replacement?.Close();throw;}
        finally{_model.Busy=false;}
    }
    public async Task<MainWindow> NewProjectAsync(string path)
    {RequireProjectIdle();ProcessedProject.CreateNew(path);return await OpenProjectAsync(path);}

    public async Task<ProcessedProjectReceipt> SaveProcessedProjectAsync(string path)
    {
        RequireProjectIdle();CommitPendingProjectEdits();_model.Busy=true;_projectSnapshotActive=true;
        try
        {
            await DrainProjectEditsAsync();
            var receipt=await Task.Run(()=>ProcessedProject.SaveSnapshot(_model.StudyPath,path));
            _model.Status=$"Processed project snapshot saved: {receipt.Recordings} recordings, {receipt.StoredRunRecordings} recordings with stored runs. Continue here or open the saved snapshot. Original videos remain external.";
            return receipt;
        }
        finally{_projectSnapshotActive=false;_model.Busy=false;}
    }
    private void CommitPendingProjectEdits()
    {
        if(!TrialGrid.CommitEdit(DataGridEditingUnit.Cell,true)||!TrialGrid.CommitEdit(DataGridEditingUnit.Row,true))
            throw new InvalidOperationException("Resolve invalid recording metadata before saving or changing the project.");
        var stored=_model.Workspace.Store.Videos().ToDictionary(a=>a.Id,StringComparer.Ordinal);
        foreach(var row in _model.Rows)
            if(!stored.TryGetValue(row.Asset.Id,out var saved)||MouseTrackerTabajara.Core.JsonDefaults.Serialize(saved)!=MouseTrackerTabajara.Core.JsonDefaults.Serialize(row.Asset))
                CommitTrialMetadata(row);
    }
    private Task DrainProjectEditsAsync()=>Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle).Task;
    private void InitializeProjectLifecycle()
    {
        Closing+=(_,args)=>
        {
            if(_projectSnapshotActive){args.Cancel=true;_model.Status="Wait for the project snapshot to finish before closing.";return;}
            if(_model.Busy)return; // Existing batch close/cancellation path retains completed clips.
            try{CommitPendingProjectEdits();}
            catch(Exception){args.Cancel=true;_model.Status="Close held. Resolve invalid recording metadata before closing.";}
        };
    }
    private void RequireProjectIdle()
    {if(!CanChangeProject)throw new InvalidOperationException("Finish or cancel processing and close review/report windows before changing or snapshotting the project.");}
    private async void NewProject(object sender,RoutedEventArgs e)
    {
        try{RequireProjectIdle();var dialog=ProjectSaveDialog("New Tabajara project");if(dialog.ShowDialog(this)==true)await NewProjectAsync(dialog.FileName);}
        catch(Exception){_model.Status="New project held. Finish processing/close review windows and choose a new unused project name.";}
    }
    private async void OpenProject(object sender,RoutedEventArgs e)
    {
        try{RequireProjectIdle();var dialog=new OpenFileDialog{Title="Open Tabajara project",Filter="Tabajara/legacy projects|*.tabajara;*.db;*.morrisdb",CheckFileExists=true};if(dialog.ShowDialog(this)==true)await OpenProjectAsync(dialog.FileName);}
        catch(Exception){_model.Status="Open project held. The file must be an intact Tabajara study; finish processing and close review/report windows first. Existing project evidence was preserved.";}
    }
    private async void SaveProcessedProject(object sender,RoutedEventArgs e)
    {
        try{RequireProjectIdle();var dialog=ProjectSaveDialog("Save processed project snapshot");if(dialog.ShowDialog(this)==true)await SaveProcessedProjectAsync(dialog.FileName);}
        catch(Exception){_model.Status="Project snapshot held. Existing files were preserved; choose a new unused name after processing/review windows close.";}
    }
    private static SaveFileDialog ProjectSaveDialog(string title)=>new(){Title=title,Filter="Tabajara project|*.tabajara",DefaultExt=".tabajara",AddExtension=true,OverwritePrompt=false};
}

