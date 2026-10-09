using System.Diagnostics;
using System.Text.Json;

namespace MouseTrackerTabajara.Application;

/// <summary>The OS-held file handle is the lease. A leftover marker file is not an active owner.</summary>
internal static class StudyProcessLease
{
    private sealed record Owner(int ProcessId, long StartUtcTicks, string BatchId);
    private static string LeasePath(string databasePath) => Path.GetFullPath(databasePath) + ".recording-batch.lock";

    public static FileStream Acquire(string databasePath, string batchId)
    {
        var path = LeasePath(databasePath);
        if (new FileInfo(path).LinkTarget is not null) throw new InvalidDataException("Study batch lease path cannot be a symbolic link.");
        FileStream stream;
        // Termination can become observable before the last kernel handle is released.
        // Retry only a positively identified dead owner; the exclusive open remains authoritative.
        for (var attempt = 0; ; attempt++)
        {
            try { stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 512, FileOptions.WriteThrough); break; }
            catch (IOException ex) when (SharingViolation(ex))
            {
                if (attempt >= 10 || !RecordedOwnerIsDead(path)) throw new StudyBatchInProgressException(databasePath);
                Thread.Sleep(50);
            }
        }
        try
        {
            using var process = Process.GetCurrentProcess();
            stream.SetLength(0);
            JsonSerializer.Serialize(stream, new Owner(process.Id, process.StartTime.ToUniversalTime().Ticks, batchId));
            stream.Flush(true);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public static bool IsHeldBy(string databasePath, int processId, long startUtcTicks, string batchId)
    {
        var path = LeasePath(databasePath);
        if (!File.Exists(path)) return false;
        try
        {
            using var available = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return false;
        }
        catch (IOException ex) when (SharingViolation(ex)) { /* A writer is actually holding the lease. */ }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        try
        {
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var owner = JsonSerializer.Deserialize<Owner>(reader);
            return owner is not null && owner.ProcessId == processId && owner.StartUtcTicks == startUtcTicks && owner.BatchId == batchId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
    private static bool RecordedOwnerIsDead(string path)
    {
        try
        {
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var owner = JsonSerializer.Deserialize<Owner>(reader);
            if (owner is null || owner.ProcessId <= 0 || owner.StartUtcTicks <= 0) return false;
            Process process;
            try { process = Process.GetProcessById(owner.ProcessId); }
            catch (ArgumentException) { return true; } // No process has this PID.
            using (process)
            {
                return process.HasExited || process.StartTime.ToUniversalTime().Ticks != owner.StartUtcTicks;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { return false; } // Unknown identity never licenses waiting or recovery.
    }
    private static bool SharingViolation(IOException ex) => (ex.HResult & 0xffff) is 32 or 33;
}
