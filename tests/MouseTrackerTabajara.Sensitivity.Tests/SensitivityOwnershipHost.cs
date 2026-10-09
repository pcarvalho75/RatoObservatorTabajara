using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MouseTrackerTabajara.Cli;
using MouseTrackerTabajara.Desktop;

namespace MouseTrackerTabajara.Sensitivity.Tests;

public static class SensitivityOwnershipHost
{
    public static int Main(string[] args)
    {
        if (args.Length < 1) return 2;
        try
        {
            return args[0] switch
            {
                "contend" => Contend(args),
                "interrupt" => Interrupt(args),
                _ => 2
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().Name);
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int Contend(string[] args)
    {
        var database = args[1];
        var report = args[2];
        var workspace = SensitivityCatalog.WorkspaceFor(database);
        var before = Fingerprint(workspace);
        try
        {
            var snapshot = AtlasSnapshot.Read(database);
            using var publication = SensitivityCatalog.Freeze(snapshot, [Draft("contender-plan")], workspace, new NativeReplayTracker());
            publication.Acknowledge();
            File.WriteAllText(report, "PUBLISHED\n" + before + "\n" + Fingerprint(workspace));
            return 0;
        }
        catch (Exception exception)
        {
            var after = Fingerprint(workspace);
            File.WriteAllText(report, "REJECTED\n" + exception.Message + "\n" + before + "\n" + after);
            return string.Equals(before, after, StringComparison.Ordinal) ? 3 : 4;
        }
    }

    private static int Interrupt(string[] args)
    {
        var workspace = args[1];
        var ready = args[2];
        var lease = SensitivityFreezeLease.Acquire(workspace);
        SensitivityCatalog.LeavePublicationInterrupted(lease);
        File.WriteAllText(ready, "interrupted");
        Thread.Sleep(TimeSpan.FromMinutes(2));
        lease.Dispose();
        return 0;
    }

    private static SensitivityScenarioDraft Draft(string name) => new()
    {
        Name = name,
        Rationale = "A second process must not publish over the live owner.",
        Threshold = 12
    };

    private static string Fingerprint(string workspace)
    {
        var builder = new StringBuilder();
        foreach (var name in new[] { "freeze", "freeze-staging", "freeze.previous" })
        {
            var root = Path.Combine(workspace, name);
            if (!Directory.Exists(root))
            {
                builder.Append(name).Append("|missing\n");
                continue;
            }
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(name).Append('/').Append(Path.GetRelativePath(root, file)).Append('|');
                builder.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).Append('\n');
            }
        }
        return builder.ToString();
    }
}
