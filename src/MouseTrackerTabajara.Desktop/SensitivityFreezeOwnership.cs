using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using MouseTrackerTabajara.Cli;

[assembly: InternalsVisibleTo("MouseTrackerTabajara.Sensitivity.Tests")]

namespace MouseTrackerTabajara.Desktop;

/// <summary>
/// The open exclusive handle is the owner. Text in the lease file, including a process id, is not ownership.
/// </summary>
public sealed class SensitivityFreezeLease : IDisposable
{
    public const string FileName = ".sensitivity-freeze.lease";
    public const string ConflictMessage = "Another sensitivity freeze owner holds this workspace. This caller did not touch staging, previous, or publication.";
    public const string ReleasedMessage = "Sensitivity freeze ownership was released. Staging, previous, and publication were left unchanged.";
    public const string AmbiguousMessage = "The live freeze and the previous freeze are both present. Neither was deleted.";

    private readonly FileStream _handle;
    private readonly string _workspace;
    private int _disposed;

    private SensitivityFreezeLease(FileStream handle, string workspace)
    {
        _handle = handle;
        _workspace = workspace;
    }

    public string Workspace => _workspace;

    internal bool IsHeld
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            try { return _handle.CanWrite; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    public static SensitivityFreezeLease Acquire(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
            throw new InvalidDataException("The sensitivity workspace is missing. Staging, previous, and publication were left unchanged.");
        var root = Path.GetFullPath(workspace);
        var parent = Path.GetDirectoryName(root);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new InvalidDataException("The sensitivity workspace parent is missing. Staging, previous, and publication were left unchanged.");
        if (ReplayOutputGuard.IsReparse(root))
            throw new InvalidDataException("The sensitivity workspace is a reparse alias. Staging, previous, and publication were left unchanged.");
        string resolved;
        try { resolved = ReplayOutputGuard.FinalPath(root); }
        catch (PlanRejectedException exception)
        {
            throw new InvalidDataException("The sensitivity workspace could not be resolved. Staging, previous, and publication were left unchanged.", exception);
        }
        if (!SamePath(resolved, root))
            throw new InvalidDataException("The sensitivity workspace resolves through a reparse alias. Staging, previous, and publication were left unchanged.");
        Directory.CreateDirectory(root);
        if (ReplayOutputGuard.IsReparse(root))
            throw new InvalidDataException("The sensitivity workspace is a reparse alias. Staging, previous, and publication were left unchanged.");
        var leasePath = Path.Combine(root, FileName);
        if ((File.Exists(leasePath) || Directory.Exists(leasePath)) && ReplayOutputGuard.IsReparse(leasePath))
            throw new InvalidDataException("The sensitivity freeze lease is a reparse alias. Staging, previous, and publication were left unchanged.");
        FileStream stream;
        try
        {
            stream = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException exception) when (SharingConflict(exception))
        {
            throw new InvalidDataException(ConflictMessage, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The sensitivity freeze lease could not be opened exclusively. Staging, previous, and publication were left unchanged.", exception);
        }
        try
        {
            if (ReplayOutputGuard.IsReparse(leasePath))
                throw new InvalidDataException("The sensitivity freeze lease is a reparse alias. Staging, previous, and publication were left unchanged.");
            var note = "exclusive-handle"u8.ToArray();
            stream.SetLength(0);
            stream.Write(note);
            stream.Flush(true);
            return new SensitivityFreezeLease(stream, root);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal void RequireWorkspace(string workspace)
    {
        RequireHeld();
        if (!SamePath(Path.GetFullPath(workspace), _workspace))
            throw new InvalidDataException("Freeze ownership belongs to a different workspace. Staging, previous, and publication were left unchanged.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _handle.Dispose();
    }

    private void RequireHeld()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new InvalidDataException(ReleasedMessage);
        try { _handle.Flush(true); }
        catch (ObjectDisposedException)
        {
            throw new InvalidDataException(ReleasedMessage);
        }
    }

    private static bool SharingConflict(IOException exception) => (exception.HResult & 0xFFFF) is 32 or 33;

    private static bool SamePath(string left, string right)
    {
        var first = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var second = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SensitivityFreezePublication : IDisposable
{
    private readonly SensitivityFreezeLease _lease;
    private int _settled;

    internal SensitivityFreezePublication(SensitivityFreezeLease lease, SensitivityFreezeResult result)
    {
        _lease = lease;
        Result = result;
    }

    public SensitivityFreezeResult Result { get; }

    public void Acknowledge()
    {
        if (Volatile.Read(ref _settled) != 0) return;
        if (!_lease.IsHeld) throw new InvalidDataException(SensitivityFreezeLease.ReleasedMessage);
        if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0) return;
        try { SensitivityCatalog.AcknowledgeOwned(_lease); }
        catch
        {
            Interlocked.Exchange(ref _settled, 0);
            throw;
        }
    }

    public void Revert()
    {
        if (Volatile.Read(ref _settled) != 0) return;
        if (!_lease.IsHeld) throw new InvalidDataException(SensitivityFreezeLease.ReleasedMessage);
        if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0) return;
        try { SensitivityCatalog.RevertOwned(_lease); }
        catch
        {
            Interlocked.Exchange(ref _settled, 0);
            throw;
        }
    }

    internal void ReleaseHandleWithoutRollback() => _lease.Dispose();

    public void Dispose()
    {
        try
        {
            if (Volatile.Read(ref _settled) == 0 && _lease.IsHeld) Revert();
        }
        catch (Exception)
        {
            // The handle still has to be released. A refused rollback does not delete the other owner's trees.
        }
        finally
        {
            _lease.Dispose();
        }
    }
}
