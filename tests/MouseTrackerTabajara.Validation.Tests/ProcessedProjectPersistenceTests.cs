using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Batching;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
using MouseTrackerTabajara.Storage;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.Validation.Tests;

public sealed class ProcessedProjectPersistenceTests(ITestOutputHelper output)
{
    private static string Folder()=>System.IO.Path.Combine(System.IO.Path.GetTempPath(),"tabajara-project-persistence-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task ThirtyNativeSourcesTwoGroupsPartialSnapshotReopenAndResumePreserveAllMaterial()
    {
        var folder=Folder();var source=new ThirtyRecordingPerformanceFixture(folder,30,24);output.WriteLine("retained evidence="+folder);
        var store=new StudyStore(System.IO.Path.Combine(folder,"working.db"));var workspace=new WorkspaceService(store);
        var assets=source.Workloads[0].Snapshot.Imported.Select((video,index)=>workspace.Import(video.Path) with
        {AnimalId=$"synthetic-animal-{index:D2}",AnimalIdConfirmed=true,GroupName=index%2==0?"synthetic-group-A":"synthetic-group-B",Day=2,Trial=index+1,Settings=video.Settings}).ToArray();
        foreach(var asset in assets)store.SaveVideo(asset);
        var plan=new StudyPlan{Title="Synthetic30source persisted roster",Animals=assets.Select(a=>new PlannedAnimal{Id=a.AnimalId,Group=a.GroupName}).ToImmutableArray(),Contexts=[new(){Id="context",Phase=TrialMode.Training}],Trials=assets.Select((a,i)=>new PlannedTrial{Id=$"trial-{i}",AnimalId=a.AnimalId,ContextId="context",RecordingId=a.Id}).ToImmutableArray()};
        var savedPlan=new StudyPlanStore(store).Save(plan,"Synthetic reviewer","Persisted synthetic plan",0);
        var partial=await workspace.AnalyzeAsync(assets.Take(10),null,new NativeBatchRequest{MaxDegreeOfParallelism=2});
        Assert.Equal(10,partial.Completed);Assert.Empty(partial.Errors);Assert.Equal(0,partial.ActiveDecodersAtCompletion);
        var first=store.LatestRun(assets[0].Id)!;store.SaveCorrection(first,new(first.Id,1,32,32,"Synthetic manual control",DateTime.UtcNow));
        store.RecordBiologicalEvent(assets[0],BiologicalEventKind.WaterEntry,1,"Synthetic event control","Synthetic reviewer");
        var rawBefore=assets.Take(10).ToDictionary(a=>a.Id,a=>JsonDefaults.Serialize(store.LatestRun(a.Id)));
        var destination=System.IO.Path.Combine(folder,"partial.tabajara");
        // Keep a writer connection open and committed material in live WAL; whole SQLite backup must include it.
        using(var live=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=store.DatabasePath,Pooling=false}.ToString()))
        {
            live.Open();using var command=live.CreateCommand();command.CommandText="PRAGMA wal_autocheckpoint=0;CREATE TABLE synthetic_optional_material(value TEXT);INSERT INTO synthetic_optional_material VALUES('committed-live-WAL');";command.ExecuteNonQuery();
            Assert.True(File.Exists(store.DatabasePath+"-wal"));var receipt=ProcessedProject.SaveSnapshot(store.DatabasePath,destination);
            Assert.Equal(30,receipt.Recordings);Assert.Equal(10,receipt.StoredRunRecordings);Assert.True(receipt.OperationalHistoryEmbedded);
            File.WriteAllText(System.IO.Path.Combine(folder,"partial-receipt.json"),JsonDefaults.Serialize(receipt));
        }
        var reopened=ProcessedProject.OpenProject(destination);Assert.Equal(30,reopened.Videos().Count);Assert.Equal(2,reopened.Videos().Select(a=>a.GroupName).Distinct().Count());
        Assert.Equal(savedPlan.RawJson,new StudyPlanStore(reopened).Latest()!.RawJson);Assert.Single(reopened.Corrections(first.Id));
        Assert.Equal(JsonDefaults.Serialize(store.Videos()),JsonDefaults.Serialize(reopened.Videos()));
        Assert.Equal(JsonDefaults.Serialize(store.BiologicalEventHistory(assets[0].Id)),JsonDefaults.Serialize(reopened.BiologicalEventHistory(assets[0].Id)));
        using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=destination,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
        {connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT value FROM synthetic_optional_material";Assert.Equal("committed-live-WAL",command.ExecuteScalar());}
        var resumed=await new WorkspaceService(reopened).AnalyzeAsync(reopened.Videos(),null,new NativeBatchRequest{MaxDegreeOfParallelism=2});
        Assert.Equal(10,resumed.Reused);Assert.Equal(30,resumed.Completed);Assert.Equal(20,resumed.Recordings.Count(r=>r.State==BatchItemState.Completed));Assert.Empty(resumed.Errors);Assert.Equal(0,resumed.ActiveDecodersAtCompletion);
        foreach(var prior in rawBefore)Assert.Equal(prior.Value,JsonDefaults.Serialize(reopened.LatestRun(prior.Key)));
        Assert.All(reopened.Videos(),a=>CompletedRecordingContract.Validate(reopened.LatestRun(a.Id)!));
        var final=ProcessedProject.SaveSnapshot(destination,System.IO.Path.Combine(folder,"completed.tabajara"));Assert.Equal(30,final.StoredRunRecordings);
        var executable=App.FindRepositoryFile("src/MouseTrackerTabajara.Desktop/bin/Release/net10.0-windows/MouseTrackerTabajara.exe");Assert.NotNull(executable);
        var screenshot=System.IO.Path.Combine(folder,"saved-project-process-reopen.png");var diagnostics=System.IO.Path.Combine(folder,"saved-project-process-reopen.log");
        var launch=new ProcessStartInfo(executable!){UseShellExecute=false,CreateNoWindow=true};
        foreach(var argument in new[]{"--study",final.ProjectPath,"--smoke-screenshot",screenshot,"--diagnostics",diagnostics})launch.ArgumentList.Add(argument);
        using(var desktop=Process.Start(launch)!)
        {
            try{await desktop.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));Assert.Equal(0,desktop.ExitCode);Assert.True(File.Exists(screenshot));}
            finally{if(!desktop.HasExited){desktop.Kill(true);await desktop.WaitForExitAsync();}}
        }
        foreach(var receipt in source.Receipts.Where(r=>r.Kind=="source"))
            Assert.Equal(receipt.Sha256,MouseTrackerTabajara.Tracking.OpenCvTracker.HashFile(System.IO.Path.Combine(folder,receipt.Identity+".avi")));
        // Missing originals never erase saved raw/corrected data; no source read is attempted by open.
        var missing=reopened.Videos()[0];reopened.SaveVideo(missing with{Path=System.IO.Path.Combine(folder,"not-present.avi")});
        var unavailable=ProcessedProject.OpenProject(destination);Assert.NotNull(unavailable.LatestRun(missing.Id));
        Assert.Equal("source_unavailable",new WorkspaceService(unavailable).ReadPersistedRecordingEvidence().Single(e=>e.VideoId==missing.Id).ReuseIneligibilityReason);
        output.WriteLine($"PASS actualOpenCV partial10 -> snapshot -> reopen -> reuse10/new20; groups2; raw runs/settings/metadata/correction/event/plan/optionalWAL material retained; final={final.ProjectPath}");
    }

    [Fact]
    public void CollisionForeignAndCorruptProjectsAreRefusedWithoutChangingBytes()
    {
        var folder=Folder();Directory.CreateDirectory(folder);var source=System.IO.Path.Combine(folder,"source.tabajara");ProcessedProject.CreateNew(source);
        var before=File.ReadAllBytes(source);Assert.Throws<IOException>(()=>ProcessedProject.SaveSnapshot(source,source));
        var destination=System.IO.Path.Combine(folder,"existing.tabajara");File.WriteAllText(destination,"retain");Assert.Throws<IOException>(()=>ProcessedProject.SaveSnapshot(source,destination));Assert.Equal("retain",File.ReadAllText(destination));
        var foreign=System.IO.Path.Combine(folder,"foreign.db");using(var db=new SqliteConnection($"Data Source={foreign};Pooling=False")){db.Open();using var command=db.CreateCommand();command.CommandText="CREATE TABLE foreign_data(value TEXT)";command.ExecuteNonQuery();}
        var foreignBytes=File.ReadAllBytes(foreign);Assert.Throws<InvalidDataException>(()=>ProcessedProject.OpenProject(foreign));Assert.Equal(foreignBytes,File.ReadAllBytes(foreign));
        Assert.ThrowsAny<Exception>(()=>ProcessedProject.OpenProject(destination));Assert.Equal(before,File.ReadAllBytes(source));
        var orphan=System.IO.Path.Combine(folder,"orphan.tabajara");File.WriteAllText(orphan+"-wal","retained recovery");Assert.Throws<StudyRecoveryNeededException>(()=>ProcessedProject.CreateNew(orphan));Assert.False(File.Exists(orphan));
        output.WriteLine("retained collision/foreign/orphan evidence="+folder);
    }

    [Fact]
    public async Task CancellationAndSnapshotLeaseDoNotDiscardCompletedEvidence()
    {
        var folder=Folder();var source=new ThirtyRecordingPerformanceFixture(folder,1,24);var store=new StudyStore(System.IO.Path.Combine(folder,"study.db"));var workspace=new WorkspaceService(store);var asset=workspace.Import(source.Workloads[0].Snapshot.Imported[0].Path);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var cancellation=new CancellationTokenSource();
        var task=workspace.AnalyzeAsync([asset],null,new NativeBatchRequest{OpenResource=async(_,token)=>{started.SetResult();await Task.Delay(Timeout.Infinite,token);return null;}},cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.Throws<StudyBatchInProgressException>(()=>ProcessedProject.SaveSnapshot(store.DatabasePath,System.IO.Path.Combine(folder,"busy.tabajara")));
        cancellation.Cancel();var cancelled=await task;Assert.True(cancelled.Cancelled);Assert.Null(store.LatestRun(asset.Id));
        var receipt=ProcessedProject.SaveSnapshot(store.DatabasePath,System.IO.Path.Combine(folder,"cancelled.tabajara"));Assert.True(receipt.OperationalHistoryEmbedded);
        var reopened=ProcessedProject.OpenProject(receipt.ProjectPath);Assert.Contains(new WorkspaceService(reopened).ReadPersistedRecordingEvidence(),e=>e.History.Any(a=>a.State==RecordingAttempts.Cancelled));
        output.WriteLine("retained cancelled/lease evidence="+folder);
    }

    [Fact]
    public void NativeProjectActionsGuardReviewAndBusyThenReopenMissingSourceSafely()
    {
        if(Environment.GetEnvironmentVariable("TABAJARA_PROJECT_PROBE")!="lifecycle"){RunIsolatedNative("lifecycle");return;}
        Exception? failure=null;var notes=new List<string>();var thread=new Thread(()=>
        {
            App? app=null;MainWindow? window=null;
            try
            {
                var folder=Folder();var fixture=new ThirtyRecordingPerformanceFixture(folder,1,24);notes.Add("native evidence="+folder);
                var store=new StudyStore(System.IO.Path.Combine(folder,"native.db"));var raw=fixture.Workloads[0].Snapshot.Runs[0];store.SaveVideo(raw.Asset with{Path=System.IO.Path.Combine(folder,"unavailable.avi")});store.SaveRun(raw.RawRun);
                app=new App();app.InitializeComponent();SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var model=new MainViewModel(new WorkspaceService(store)){Blind=true};window=new MainWindow(model);app.MainWindow=window;window.Show();Pump(window.InitializeAsync());
                foreach(var name in new[]{"NewProjectButton","OpenProjectButton","SaveProjectButton"})Assert.IsType<Button>(window.FindName(name));
                var destination=System.IO.Path.Combine(folder,"native-saved.tabajara");
                model.Busy=true;Assert.False(window.CanChangeProject);Assert.Throws<InvalidOperationException>(()=>window.OpenProjectAsync(destination).GetAwaiter().GetResult());Assert.False(File.Exists(destination));model.Busy=false;
                var review=new Window{Owner=window};review.Show();Assert.False(window.CanChangeProject);Assert.Throws<InvalidOperationException>(()=>window.NewProjectAsync(destination).GetAwaiter().GetResult());review.Close();
                var save=window.SaveProcessedProjectAsync(destination);Pump(save);Assert.Equal(1,save.Result.StoredRunRecordings);
                var open=window.OpenProjectAsync(destination);Pump(open);window=open.Result;
                var reopened=(MainViewModel)window.DataContext;Assert.True(reopened.Blind);Assert.Single(reopened.Rows);Assert.NotNull(reopened.Rows[0].Run);
                Assert.Contains("source",reopened.Status,StringComparison.OrdinalIgnoreCase);Assert.Contains("held",reopened.Status,StringComparison.OrdinalIgnoreCase);
                Assert.Null(((Image)window.FindName("FrameImage")).Source);
                window.Width=1050;window.Height=700;var root=(FrameworkElement)window.Content;root.Measure(new Size(1050,700));root.Arrange(new Rect(0,0,1050,700));root.UpdateLayout();Pump(Task.Delay(30));
                var bitmap=new RenderTargetBitmap(1050,700,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(System.IO.Path.Combine(folder,"project-minimum.png")))encoder.Save(stream);
                var fresh=System.IO.Path.Combine(folder,"new-native.tabajara");var create=window.NewProjectAsync(fresh);Pump(create);window=create.Result;Assert.Empty(((MainViewModel)window.DataContext).Rows);Assert.True(File.Exists(fresh));
                notes.Add("PASS native project buttons; busy/review gates; snapshot/open/new; blind state carried; unavailable source keeps raw data and clears pixels.");
            }
            catch(Exception e){failure=e;}
            finally{window?.Close();app?.Shutdown();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(90)));foreach(var note in notes)output.WriteLine(note);if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task KilledCommittedChildCanBeSnapshottedWithoutLosingItsInterruptedAttempt()
    {
        var executable=App.FindRepositoryFile("tests/MouseTrackerTabajara.CrashProbe/bin/Release/net10.0/MouseTrackerTabajara.CrashProbe.exe");Assert.NotNull(executable);
        var folder=Folder();Directory.CreateDirectory(folder);output.WriteLine("retained killed-child evidence="+folder);
        var launch=new ProcessStartInfo(executable!){UseShellExecute=false,CreateNoWindow=true};launch.ArgumentList.Add(folder);launch.ArgumentList.Add("commit-blocked");
        using var child=Process.Start(launch)!;
        try
        {
            var ready=System.IO.Path.Combine(folder,"ready.json");var deadline=DateTime.UtcNow.AddSeconds(30);
            while(!File.Exists(ready)&&DateTime.UtcNow<deadline&&!child.HasExited)await Task.Delay(50);
            Assert.True(File.Exists(ready));child.Kill(true);await child.WaitForExitAsync();
            var source=System.IO.Path.Combine(folder,"study.db");var saved=ProcessedProject.SaveSnapshot(source,System.IO.Path.Combine(folder,"recovered.tabajara"));
            var reopened=ProcessedProject.OpenProject(saved.ProjectPath);var evidence=Assert.Single(new WorkspaceService(reopened).ReadPersistedRecordingEvidence());
            Assert.Equal(RecordingAttempts.Interrupted,evidence.LatestAttempt);Assert.True(evidence.HistoricalRunIntact);Assert.Single(reopened.Videos());Assert.NotNull(reopened.LatestRun(reopened.Videos()[0].Id));Assert.NotEmpty(evidence.History);
        }
        finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
    }
    private static void Pump(Task task)
    {
        var dispatcher=Dispatcher.CurrentDispatcher;var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(30)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false));timer.Start();Dispatcher.PushFrame(frame);timer.Stop();Assert.True(task.IsCompleted);task.GetAwaiter().GetResult();
    }
    [Fact]
    public void ImmediateNativeGridEditSaveOpenIncludesLastAnimalGroupAndTrial()
    {
        if(Environment.GetEnvironmentVariable("TABAJARA_PROJECT_PROBE")!="edit"){RunIsolatedNative("edit");return;}
        Exception? failure=null;var notes=new List<string>();var thread=new Thread(()=>
        {
            App? app=null;MainWindow? window=null;Task? saving=null;
            try
            {
                var folder=Folder();Directory.CreateDirectory(folder);notes.Add("immediate-edit evidence="+folder);
                var store=new StudyStore(System.IO.Path.Combine(folder,"edit.db"));store.SaveVideo(new VideoAsset{Id="edit-control",Path=System.IO.Path.Combine(folder,"missing.avi"),SourceSha256=new string('a',64),Metadata=new(64,64,10,24),AnimalId="initial-animal",GroupName="initial-group"});
                app=new App();app.InitializeComponent();SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var model=new MainViewModel(new WorkspaceService(store));window=new MainWindow(model);app.MainWindow=window;window.Show();Pump(window.InitializeAsync());
                var grid=(DataGrid)window.FindName("TrialGrid");var row=model.Rows[0];
                void Edit(string header,string text,bool finish)
                {
                    var column=grid.Columns.IndexOf(grid.Columns.Single(c=>Equals(c.Header,header)));grid.SelectedItem=row;grid.ScrollIntoView(row,grid.Columns[column]);grid.UpdateLayout();grid.CurrentCell=new DataGridCellInfo(row,grid.Columns[column]);grid.UpdateLayout();
                    DependencyObject? cell=grid.Columns[column].GetCellContent(row);while(cell is not null&&cell is not DataGridCell)cell=VisualTreeHelper.GetParent(cell);
                    Assert.NotNull(cell);((DataGridCell)cell!).Focus();Assert.True(grid.BeginEdit(),$"column{column}, grid readonly={grid.IsReadOnly}, column readonly={grid.Columns[column].IsReadOnly}");
                    var editor=Assert.IsType<TextBox>(grid.Columns[column].GetCellContent(row));editor.Text=text;editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    if(finish)Assert.True(grid.CommitEdit(DataGridEditingUnit.Cell,true));
                }
                Edit("Animal","final-animal",true);Edit("Group","final-group",true);Edit("Trial","7",false);
                var destination=System.IO.Path.Combine(folder,"last-edit.tabajara");var save=window.SaveProcessedProjectAsync(destination);saving=save;
                // No dispatcher pump has run since edit: backup must never outrun final metadata persistence.
                var boundary=store.Videos()[0];File.WriteAllText(System.IO.Path.Combine(folder,"save-boundary-metadata.json"),JsonDefaults.Serialize(boundary));
                Assert.Equal("final-animal",boundary.AnimalId);Assert.Equal("final-group",boundary.GroupName);Assert.Equal(7,boundary.Trial);
                window.Close();Assert.True(window.IsVisible); // Publication in flight must not be abandoned on close.
                Pump(save);var open=window.OpenProjectAsync(destination);Pump(open);window=open.Result;
                var restored=((MainViewModel)window.DataContext).Rows[0].Asset;
                Assert.Equal("final-animal",restored.AnimalId);Assert.Equal("final-group",restored.GroupName);Assert.Equal(7,restored.Trial);
                var finalModel=(MainViewModel)window.DataContext;var finalGrid=(DataGrid)window.FindName("TrialGrid");var finalRow=finalModel.Rows[0];var groupColumn=finalGrid.Columns.Single(c=>Equals(c.Header,"Group"));
                finalGrid.SelectedItem=finalRow;finalGrid.ScrollIntoView(finalRow,groupColumn);finalGrid.UpdateLayout();finalGrid.CurrentCell=new(finalRow,groupColumn);finalGrid.UpdateLayout();
                DependencyObject? groupCell=groupColumn.GetCellContent(finalRow);while(groupCell is not null&&groupCell is not DataGridCell)groupCell=VisualTreeHelper.GetParent(groupCell);
                Assert.NotNull(groupCell);((DataGridCell)groupCell!).Focus();Assert.True(finalGrid.BeginEdit());var finalEditor=Assert.IsType<TextBox>(groupColumn.GetCellContent(finalRow));finalEditor.Text="closed-group";finalEditor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                window.Close();Assert.Equal("closed-group",ProcessedProject.OpenProject(destination).Videos()[0].GroupName);
                notes.Add("PASS actual DataGrid edits animal/group/trial -> synchronous commit boundary -> background drain -> snapshot -> close/open.");
            }
            catch(Exception e){failure=e;}
            finally{if(saving is not null&&!saving.IsCompleted)Pump(saving);window?.Close();app?.Shutdown();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(90)));foreach(var note in notes)output.WriteLine(note);if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [Fact]
    public void FailedPublicationRetainsStageAndOriginalRatherThanPublishingPartialHistory()
    {
        var folder=Folder();Directory.CreateDirectory(folder);var source=System.IO.Path.Combine(folder,"source.tabajara");ProcessedProject.CreateNew(source);
        var original=File.ReadAllBytes(source);File.WriteAllText(source+".recording-operations.json","deliberately invalid synthetic operational document");
        var destination=System.IO.Path.Combine(folder,"failed.tabajara");Assert.ThrowsAny<System.Text.Json.JsonException>(()=>ProcessedProject.SaveSnapshot(source,destination));
        Assert.False(File.Exists(destination));Assert.Equal(original,File.ReadAllBytes(source));Assert.NotEmpty(Directory.GetFiles(folder,"failed.tabajara.partial-*"));Assert.True(File.Exists(destination+".snapshot-owner"));
        output.WriteLine("retained failed publication="+folder);
    }
    private void RunIsolatedNative(string mode)
    {
        var executable=App.FindRepositoryFile("tests/MouseTrackerTabajara.ProjectProbe/bin/Release/net10.0-windows/MouseTrackerTabajara.ProjectProbe.exe");Assert.NotNull(executable);
        var launch=new ProcessStartInfo(executable!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};launch.ArgumentList.Add(mode);launch.Environment["TABAJARA_PROJECT_PROBE"]=mode;
        using var process=Process.Start(launch)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try{Assert.True(process.WaitForExit(90000));output.WriteLine(stdout.GetAwaiter().GetResult());output.WriteLine(stderr.GetAwaiter().GetResult());Assert.Equal(0,process.ExitCode);}
        finally{if(!process.HasExited){process.Kill(true);process.WaitForExit(10000);}}
    }
}




