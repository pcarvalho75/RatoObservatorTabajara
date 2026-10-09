using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MouseTrackerTabajara.Cli;

public static class ReplayOutputGuard
{
    public const string LeaseFileName = ".replay-output.lease";

    public static string RequireCleanupTarget(string candidate, string root, string purpose)
    {
        var lexicalRoot = Path.GetFullPath(root);
        var lexical = Path.GetFullPath(candidate);
        if (!IsSameOrInside(lexical, lexicalRoot))
            throw new PlanRejectedException($"{purpose} is outside the intended directory. It was left unchanged.");
        if (!Exists(lexicalRoot))
            throw new PlanRejectedException($"{purpose} root does not exist. It was left unchanged.");
        var finalRoot = FinalPath(lexicalRoot);
        var final = FinalPath(lexical);
        if (!IsSameOrInside(final, finalRoot))
            throw new PlanRejectedException($"{purpose} resolves outside the intended directory. The reparse path was left unchanged.");
        return lexical;
    }

    public static void DeleteContained(string candidate, string root, string purpose)
    {
        if (!Exists(candidate)) return;
        var lexical = RequireCleanupTarget(candidate, root, purpose);
        if (IsReparse(lexical))
        {
            if ((File.GetAttributes(lexical) & FileAttributes.Directory) != 0) Directory.Delete(lexical, recursive: false);
            else File.Delete(lexical);
            return;
        }
        if (Directory.Exists(lexical))
        {
            RejectUnsafeDescendants(lexical);
            DeleteTree(lexical);
            return;
        }
        File.Delete(lexical);
    }

    public static void MoveContainedDirectory(string source, string sourceRoot, string destination, string destinationRoot)
    {
        var from = RequireCleanupTarget(source, sourceRoot, "move source");
        if (!Directory.Exists(from)) throw new PlanRejectedException("Move source is missing. Output was left unchanged.");
        if (IsReparse(from)) throw new PlanRejectedException("Move source is a reparse point and was not moved.");
        RejectUnsafeDescendants(from);
        var to = RequireCleanupTarget(destination, destinationRoot, "move destination");
        if (Exists(to)) throw new InvalidDataException("The attempt directory already exists and was not overwritten.");
        var parent = Path.GetDirectoryName(to) ?? throw new PlanRejectedException("Move destination has no parent. It was left unchanged.");
        if (!Directory.Exists(parent))
        {
            RequireCleanupTarget(parent, destinationRoot, "move destination parent");
            Directory.CreateDirectory(parent);
        }
        else if (IsReparse(parent))
        {
            var parentFinal = FinalPath(parent);
            var rootFinal = FinalPath(destinationRoot);
            if (!IsSameOrInside(parentFinal, rootFinal))
                throw new PlanRejectedException("Move destination parent resolves outside the intended directory. It was not created.");
        }
        Directory.Move(from, to);
    }

    public static bool IsSameOrInside(string candidate, string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReparse(string path)
    {
        if (!Exists(path)) return false;
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    public static string FinalPath(string path)
    {
        var existing = Path.GetFullPath(path);
        var suffix = new Stack<string>();
        while (!Exists(existing))
        {
            var name = Path.GetFileName(existing.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
                throw new PlanRejectedException("The path has no existing parent and was left unchanged.");
            suffix.Push(name);
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, existing, StringComparison.OrdinalIgnoreCase))
                throw new PlanRejectedException("The path has no existing parent and was left unchanged.");
            existing = parent;
        }
        var resolved = FinalPathOfExisting(existing);
        while (suffix.Count > 0) resolved = Path.Combine(resolved, suffix.Pop());
        return Path.GetFullPath(resolved);
    }

    public static void RejectUnsafeDescendants(string directory)
    {
        if (!Directory.Exists(directory) || IsReparse(directory)) return;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (IsReparse(entry))
                {
                    var final = FinalPathOfExisting(entry);
                    if (!IsSameOrInside(final, directory))
                        throw new PlanRejectedException($"Reparse path '{entry}' resolves outside the cleanup target. Nothing was deleted.");
                    continue;
                }
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
    }

    private static void DeleteTree(string directory)
    {
        if (IsReparse(directory))
        {
            Directory.Delete(directory, recursive: false);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).ToList())
        {
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0 || IsReparse(entry)) DeleteTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory, recursive: false);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string FinalPathOfExisting(string path)
    {
        const FileOptions backupSemantics = (FileOptions)0x02000000;
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, backupSemantics);
        }
        using (stream)
        {
            var buffer = new char[32768];
            var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, buffer.Length, 0);
            if (length <= 0 || length >= buffer.Length)
                throw new PlanRejectedException("The path could not be resolved. It was left unchanged.");
            var value = new string(buffer, 0, length).TrimEnd('\0');
            if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
                value = value.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + value[8..] : value[4..];
            return Path.GetFullPath(value);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandle(SafeFileHandle handle, char[] buffer, int length, int flags);
}

public sealed class OutputLease : IDisposable
{
    private readonly FileStream _stream;

    private OutputLease(FileStream stream) => _stream = stream;

    public static OutputLease Acquire(string output)
    {
        var directory = Path.GetFullPath(output);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ReplayOutputGuard.LeaseFileName);
        if (File.Exists(path) && ReplayOutputGuard.IsReparse(path))
            throw new PlanRejectedException("The output lease path is a reparse point. Staging was not deleted and the manifest was not overwritten.");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new OutputLease(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PlanRejectedException("Another replay process holds the output lease. This process did not delete staging or overwrite the manifest.");
        }
    }

    public void Dispose() => _stream.Dispose();
}
