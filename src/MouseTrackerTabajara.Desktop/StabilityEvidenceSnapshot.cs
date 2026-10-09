using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using MouseTrackerTabajara.Application;

namespace MouseTrackerTabajara.Desktop;

public sealed record StabilityFileIdentity(string Path, string Sha256);
public sealed record StabilityEvidenceSnapshot(StabilityReport Report, ImmutableArray<StabilityFileIdentity> Files)
{
    public Func<bool>? VerifyCurrent { get; init; }
    public static StabilityFileIdentity Freeze(string path) => new(System.IO.Path.GetFullPath(path), Hash(path));
    public bool IsCurrent() => Files.All(file => File.Exists(file.Path) && Hash(file.Path) == file.Sha256) && (VerifyCurrent?.Invoke() ?? true);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}
