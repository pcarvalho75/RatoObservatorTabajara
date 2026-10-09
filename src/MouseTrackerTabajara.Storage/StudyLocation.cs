namespace MouseTrackerTabajara.Storage;

public sealed record StudyLocationChoice(string DatabasePath, string? Note);

public sealed class StudyRecoveryNeededException : InvalidOperationException
{
    public StudyRecoveryNeededException(string preferredPath, string legacyPath, IReadOnlyList<string> orphanSidecars)
        : base(BuildMessage(CandidateList(preferredPath, legacyPath), orphanSidecars))
    {
        PreferredPath = preferredPath;
        LegacyPath = legacyPath;
        OrphanSidecars = CopySidecars(orphanSidecars);
    }

    public StudyRecoveryNeededException(string databasePath, IReadOnlyList<string> orphanSidecars)
        : base(BuildMessage(RequiredPath(databasePath), orphanSidecars))
    {
        ExactPath = databasePath;
        PreferredPath = databasePath;
        LegacyPath = "";
        OrphanSidecars = CopySidecars(orphanSidecars);
    }

    // Set only for an operator-selected path. Default resolution leaves this unset and names both candidates.
    public string? ExactPath { get; }
    public string PreferredPath { get; }
    public string LegacyPath { get; }
    public IReadOnlyList<string> OrphanSidecars { get; }

    private static string RequiredPath(string databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Name the database path.", nameof(databasePath));
        return databasePath;
    }

    private static string CandidateList(string preferredPath, string legacyPath)
    {
        ArgumentNullException.ThrowIfNull(preferredPath);
        ArgumentNullException.ThrowIfNull(legacyPath);
        return preferredPath + " and " + legacyPath;
    }

    private static string[] CopySidecars(IReadOnlyList<string> orphanSidecars)
    {
        ArgumentNullException.ThrowIfNull(orphanSidecars);
        if (orphanSidecars.Count == 0 || orphanSidecars.Any(static path => string.IsNullOrWhiteSpace(path)))
            throw new ArgumentException("Name at least one orphan sidecar.", nameof(orphanSidecars));
        return orphanSidecars.ToArray();
    }

    private static string BuildMessage(string refusedPaths, IReadOnlyList<string> orphanSidecars)
    {
        return "Study recovery is required before any study can be opened. Refusing "
            + refusedPaths
            + " because these SQLite sidecar files have no main database: "
            + string.Join(", ", CopySidecars(orphanSidecars))
            + ". No file was created, copied, renamed, deleted, or recovered.";
    }
}

public static class StudyLocation
{
    public const string ProductDirectoryName = "MouseTrackerTabajara";
    public const string LegacyDirectoryName = "MorrisLabNative";
    public const string StudyFileName = "luciana.study.db";
    public const string LegacyStudyFileName = "luciana.morrisdb";

    public static string PreferredDatabase(string localApplicationData) =>
        Path.Combine(localApplicationData, ProductDirectoryName, StudyFileName);

    public static string LegacyDatabase(string localApplicationData) =>
        Path.Combine(localApplicationData, LegacyDirectoryName, LegacyStudyFileName);

    public static string DiagnosticsLog(string localApplicationData) =>
        Path.Combine(localApplicationData, ProductDirectoryName, "startup.log");

    // Presence is the main database file. A lone -wal or -shm is not a database.
    public static bool DatabasePresent(string path) => File.Exists(path);

    // Full path, or a recovery exception. Call before creating the directory or opening SQLite.
    public static string GuardAgainstOrphanSidecars(string databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        var orphans = OrphanSidecars(fullPath).ToArray();
        if (orphans.Length > 0) throw new StudyRecoveryNeededException(fullPath, orphans);
        return fullPath;
    }

    public static StudyLocationChoice Resolve(string preferredPath, string legacyPath)
    {
        preferredPath = Path.GetFullPath(preferredPath);
        legacyPath = Path.GetFullPath(legacyPath);
        var orphans = OrphanSidecars(preferredPath).Concat(OrphanSidecars(legacyPath)).ToArray();
        // Stop before a path is chosen. Opening a missing main file would create an empty database beside unrecovered bytes.
        if (orphans.Length > 0) throw new StudyRecoveryNeededException(preferredPath, legacyPath, orphans);
        var preferred = DatabasePresent(preferredPath);
        var legacy = DatabasePresent(legacyPath);
        if (!preferred && legacy)
            return new(legacyPath, "Opened the existing study in place at " + legacyPath + ". The file was not copied.");
        if (preferred && legacy)
            return new(preferredPath, "Opened " + preferredPath + ". Another study file remains at " + legacyPath + " and was not changed.");
        return new(preferredPath, null);
    }

    private static IEnumerable<string> OrphanSidecars(string path)
    {
        if (File.Exists(path)) yield break;
        if (File.Exists(path + "-wal")) yield return path + "-wal";
        if (File.Exists(path + "-shm")) yield return path + "-shm";
    }
}
