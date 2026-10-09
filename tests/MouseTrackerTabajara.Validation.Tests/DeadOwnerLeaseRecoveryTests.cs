using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using MouseTrackerTabajara.Application;
using Xunit;
using Xunit.Abstractions;
namespace MouseTrackerTabajara.Validation.Tests;
public class DeadOwnerLeaseRecoveryTests(ITestOutputHelper output)
{
    private static FileStream Acquire(string path)
    {
        try{return (FileStream)typeof(WorkspaceService).Assembly.GetType("MouseTrackerTabajara.Application.StudyProcessLease")!.GetMethod("Acquire",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,[path,"recovery-test"])!;}
        catch(TargetInvocationException e) when(e.InnerException is not null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
    }
    private string Folder(){var folder=Path.Combine(Path.GetTempPath(),"tabajara-s37-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);output.WriteLine("retained evidence="+folder);return folder;}
    private static byte[] ReadMarker(string db){using var stream=new FileStream(db+".recording-batch.lock",FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var bytes=new MemoryStream();stream.CopyTo(bytes);return bytes.ToArray();}
    private static FileStream Marker(string db,int pid,long ticks)
    {
        var stream=new FileStream(db+".recording-batch.lock",FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);
        JsonSerializer.Serialize(stream,new{ProcessId=pid,StartUtcTicks=ticks,BatchId="retained-attempt"});stream.Flush(true);return stream;
    }
    [Fact] public async Task ConfirmedDeadOwnerTransientHandleRetriesWithoutBypassingExclusiveOpen()
    {
        var db=Path.Combine(Folder(),"study.db");
        // A terminated real process supplies the exact identity; a retained test handle
        // models the short post-termination OS sharing-denial interval deterministically.
        using var child=Process.Start(new ProcessStartInfo("cmd.exe","/c ping -n 30 127.0.0.1 >nul"){UseShellExecute=false,CreateNoWindow=true})!;
        var pid=child.Id;var ticks=child.StartTime.ToUniversalTime().Ticks;child.Kill(true);await child.WaitForExitAsync();
        using var marker=Marker(db,pid,ticks);var before=ReadMarker(db);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var acquire=Task.Run(()=>{started.SetResult();return Acquire(db);});await started.Task;await Task.Delay(125);Assert.False(acquire.IsCompleted);Assert.Equal(before,ReadMarker(db));marker.Dispose();
        using var recovered=await acquire;Assert.True(recovered.CanWrite);Assert.True(File.Exists(db+".recording-batch.lock"));output.WriteLine("confirmed dead PID transient held handle: original marker unchanged while blocked; exclusive acquisition after release PASS");
    }
    [Fact] public void LiveOwnerAndUnknownOwnerRefuseWithoutMutationAndDeadPersistentHandleRemainsRefused()
    {
        using var current=Process.GetCurrentProcess();foreach(var owner in new[]{(current.Id,current.StartTime.ToUniversalTime().Ticks),(0,0L),(int.MaxValue,1L)})
        {
            var db=Path.Combine(Folder(),"study.db");using var marker=Marker(db,owner.Item1,owner.Item2);var before=ReadMarker(db);
            Assert.Throws<StudyBatchInProgressException>(()=>Acquire(db));Assert.Equal(before,ReadMarker(db));Assert.True(marker.CanWrite);
            output.WriteLine($"owner PID={owner.Item1}: refusal, marker unchanged and exclusive handle retained PASS");
        }
    }
}
