using System.Security.Cryptography;

namespace MouseTrackerTabajara.Application;

public sealed class StudyIntakeSourceLeaseException(string message, Exception? inner=null) : IOException(message,inner);

/// <summary>Retains Windows read-share handles across plan CAS. Sources stay readable, but writes/deletes are denied.</summary>
public sealed class StudyIntakeSourceLease : IDisposable
{
    private readonly List<FileStream> _handles=[];
    private StudyIntakeSourceLease() { }
    public static StudyIntakeSourceLease Acquire(IEnumerable<StudyIntakeFile> files,CancellationToken cancellation=default)
    {
        var lease=new StudyIntakeSourceLease();var buffer=new byte[64*1024];
        try
        {
            foreach(var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                FileStream stream;
                try{stream=new FileStream(file.Path,FileMode.Open,FileAccess.Read,FileShare.Read,buffer.Length,FileOptions.SequentialScan);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
                {throw new StudyIntakeSourceLeaseException($"Source on manifest line {file.Line} is unavailable or open for writing. Close its writer and preview again.",ex);}
                lease._handles.Add(stream);
                void VerifyStats()
                {
                    var info=new FileInfo(file.Path);
                    if(!info.Exists||stream.Length!=file.Length||info.Length!=file.Length||info.LastWriteTimeUtc!=file.WrittenUtc||info.CreationTimeUtc!=file.CreatedUtc)
                        throw new StudyIntakeSourceLeaseException($"Source on manifest line {file.Line} changed since preview. Preview again.");
                }
                VerifyStats();using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);int count;
                while((count=stream.Read(buffer,0,buffer.Length))>0){cancellation.ThrowIfCancellationRequested();hash.AppendData(buffer,0,count);}
                cancellation.ThrowIfCancellationRequested();VerifyStats();
                if(!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))
                    throw new StudyIntakeSourceLeaseException($"Source bytes on manifest line {file.Line} changed since preview. Preview again.");
            }
            cancellation.ThrowIfCancellationRequested();return lease;
        }
        catch{lease.Dispose();throw;}
    }
    public void Dispose(){foreach(var stream in _handles)stream.Dispose();_handles.Clear();}
}
