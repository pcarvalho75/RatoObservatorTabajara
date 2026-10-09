using System.Security.Cryptography;
using System.IO;
using MouseTrackerTabajara.Application;
using Xunit;

namespace MouseTrackerTabajara.Atlas.Tests;

public sealed class StudyIntakeSourceLeaseTests
{
    private sealed class Files : IDisposable
    {
        public string Folder{get;}=Path.Combine(Path.GetTempPath(),"source-lease-"+Guid.NewGuid().ToString("N"));
        public Files(){Directory.CreateDirectory(Folder);}
        public StudyIntakeFile Add(int line){var path=Path.Combine(Folder,line+".bin");File.WriteAllBytes(path,Enumerable.Range(0,256).Select(i=>(byte)i).ToArray());var info=new FileInfo(path);return new(line,path,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),info.Length,info.LastWriteTimeUtc,info.CreationTimeUtc,null);}
        public void Dispose()=>Directory.Delete(Folder,true);
    }
    private static void Writable(StudyIntakeFile f){using var stream=new FileStream(f.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
    [Fact] public void WindowsLeaseDeniesWritesDeletesAndAllowsReadsUntilDisposed()
    {
        Assert.True(OperatingSystem.IsWindows());using var files=new Files();var a=files.Add(1);var b=files.Add(2);
        using(var lease=StudyIntakeSourceLease.Acquire([a,b]))
        {
            foreach(var f in new[]{a,b}){Assert.Throws<IOException>(()=>Writable(f));Assert.Throws<IOException>(()=>File.Delete(f.Path));Assert.Equal(f.Sha256,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Path))));}
        }
        Writable(a);Writable(b);File.Delete(a.Path);File.Delete(b.Path);
    }
    [Fact] public void ChangedLateBytesAndActiveWriterRefuseAndReleaseEarlierHandles()
    {
        using var files=new Files();var a=files.Add(1);var b=files.Add(2);var bytes=File.ReadAllBytes(b.Path);bytes[8]^=1;File.WriteAllBytes(b.Path,bytes);File.SetLastWriteTimeUtc(b.Path,b.WrittenUtc);File.SetCreationTimeUtc(b.Path,b.CreatedUtc);
        Assert.Throws<StudyIntakeSourceLeaseException>(()=>StudyIntakeSourceLease.Acquire([a,b]));Writable(a);Writable(b);
        using(var writer=new FileStream(a.Path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite)){Assert.Throws<StudyIntakeSourceLeaseException>(()=>StudyIntakeSourceLease.Acquire([a]));}
        Writable(a);
    }
    [Fact] public void CancellationAndEnumerationFailureReleaseAllAcquiredHandles()
    {
        using var files=new Files();var a=files.Add(1);using var cancellation=new CancellationTokenSource();
        IEnumerable<StudyIntakeFile> Cancel(){yield return a;cancellation.Cancel();yield return a;}
        Assert.Throws<OperationCanceledException>(()=>StudyIntakeSourceLease.Acquire(Cancel(),cancellation.Token));Writable(a);
        IEnumerable<StudyIntakeFile> Fail(){yield return a;throw new InvalidOperationException("Synthetic input failure");}
        Assert.Throws<InvalidOperationException>(()=>StudyIntakeSourceLease.Acquire(Fail()));Writable(a);
        using(var lease=StudyIntakeSourceLease.Acquire([a])){lease.Dispose();lease.Dispose();Writable(a);}
    }
}
