using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Tracking;

namespace MouseTrackerTabajara.Cli;

public static class ReplayCodeIdentityProbe
{
    public static ReplayCodeIdentity Capture(string nativeVersion)
    {
        _ = OpenCvTracker.NativeVersion;
        if (string.IsNullOrWhiteSpace(nativeVersion))
            throw new PlanRejectedException("The native tracker did not report a version. A version string was not stored as a code fingerprint.");
        var identity = new ReplayCodeIdentity
        {
            AlgorithmContract = AlgorithmIdentity.Contract,
            SoftwareVersion = CompletedRunContract.SoftwareVersion,
            NativeVersion = nativeVersion,
            Assemblies =
            [
                Fingerprint(typeof(ReplayCommand).Assembly),
                Fingerprint(typeof(AnalysisRun).Assembly),
                Fingerprint(typeof(OpenCvTracker).Assembly),
                Fingerprint(typeof(CompletedRecordingContract).Assembly)
            ],
            NativeBinaries = CaptureNativeBinaries()
        };
        RequireSame(identity, identity);
        return identity;
    }

    public static string CanonicalSha256(ReplayCodeIdentity identity)
    {
        var assemblies = (identity.Assemblies ?? []).OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => item.Name + "|" + item.Sha256 + "|" + item.ModuleVersionId);
        var natives = (identity.NativeBinaries ?? []).OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => item.Name + "|" + item.Sha256);
        var text = string.Join("\n",
        [
            identity.ReplayContract,
            identity.AlgorithmContract,
            identity.SoftwareVersion,
            identity.NativeVersion,
            identity.SettingsHashMethod,
            "assemblies",
            .. assemblies,
            "natives",
            .. natives
        ]);
        return ReplayHashes.Sha256Hex(Encoding.UTF8.GetBytes(text));
    }

    public static void RequireSame(ReplayCodeIdentity? recorded, ReplayCodeIdentity actual)
    {
        if (recorded is null)
            throw new PlanRejectedException("Recorded code identity is missing. Output was left unchanged.");
        if (!string.Equals(recorded.ReplayContract, actual.ReplayContract, StringComparison.Ordinal)
            || !string.Equals(recorded.AlgorithmContract, actual.AlgorithmContract, StringComparison.Ordinal)
            || !string.Equals(recorded.SoftwareVersion, actual.SoftwareVersion, StringComparison.Ordinal)
            || !string.Equals(recorded.NativeVersion, actual.NativeVersion, StringComparison.Ordinal)
            || !string.Equals(recorded.SettingsHashMethod, actual.SettingsHashMethod, StringComparison.Ordinal))
            throw new PlanRejectedException("Recorded code identity does not match this process. A version string is not a code fingerprint. Output was left unchanged.");
        RequireAssemblies(recorded.Assemblies, actual.Assemblies);
        RequireNatives(recorded.NativeBinaries, actual.NativeBinaries);
    }

    public static AssemblyFingerprint Fingerprint(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        var location = assembly.Location;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            throw new PlanRejectedException($"Assembly '{name}' has no file that can be fingerprinted. Output was left unchanged.");
        var module = assembly.Modules.FirstOrDefault() ?? throw new PlanRejectedException($"Assembly '{name}' has no module identity. Output was left unchanged.");
        return new AssemblyFingerprint
        {
            Name = name,
            Sha256 = ReplayHashes.Sha256File(location),
            ModuleVersionId = module.ModuleVersionId.ToString("D")
        };
    }

    private static void RequireAssemblies(List<AssemblyFingerprint>? recorded, List<AssemblyFingerprint>? actual)
    {
        var expected = new[] { "MouseTrackerTabajara.Cli", "MouseTrackerTabajara.Core", "MouseTrackerTabajara.Tracking", "MouseTrackerTabajara.Application" };
        var left = IndexAssemblies(recorded, "Recorded");
        var right = IndexAssemblies(actual, "Actual");
        if (left.Count != expected.Length || right.Count != expected.Length || expected.Any(name => !left.ContainsKey(name) || !right.ContainsKey(name)))
            throw new PlanRejectedException("Code identity must name the CLI, Core, Tracking, and application-validation assemblies. Output was left unchanged.");
        foreach (var name in expected)
        {
            var recordedItem = left[name];
            var actualItem = right[name];
            if (!IsHash(recordedItem.Sha256) || !IsHash(actualItem.Sha256)
                || !Guid.TryParseExact(recordedItem.ModuleVersionId, "D", out _)
                || !Guid.TryParseExact(actualItem.ModuleVersionId, "D", out _)
                || !string.Equals(recordedItem.Sha256, actualItem.Sha256, StringComparison.Ordinal)
                || !string.Equals(recordedItem.ModuleVersionId, actualItem.ModuleVersionId, StringComparison.OrdinalIgnoreCase))
                throw new PlanRejectedException($"Assembly identity for {name} does not match this process. Output was left unchanged.");
        }
    }

    private static Dictionary<string, AssemblyFingerprint> IndexAssemblies(List<AssemblyFingerprint>? items, string label)
    {
        var index = new Dictionary<string, AssemblyFingerprint>(StringComparer.Ordinal);
        foreach (var item in items ?? [])
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Name) || !index.TryAdd(item.Name, item))
                throw new PlanRejectedException($"{label} assembly identity is missing or duplicated. Output was left unchanged.");
        }
        return index;
    }

    private static void RequireNatives(List<NativeBinaryFingerprint>? recorded, List<NativeBinaryFingerprint>? actual)
    {
        var left = IndexNatives(recorded, "Recorded");
        var right = IndexNatives(actual, "Actual");
        if (!right.Keys.Any(name => name.Equals("OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase))
            || !right.Keys.Any(name => name.StartsWith("opencv_videoio_ffmpeg", StringComparison.OrdinalIgnoreCase)))
            throw new PlanRejectedException("The loaded OpenCV native binaries were not both identified. Output was left unchanged.");
        if (left.Count != right.Count || left.Keys.Any(name => !right.ContainsKey(name)))
            throw new PlanRejectedException("Recorded native binary identity does not match the binaries loaded by this process. Output was left unchanged.");
        foreach (var pair in left)
        {
            if (!IsHash(pair.Value.Sha256) || !string.Equals(pair.Value.Sha256, right[pair.Key].Sha256, StringComparison.Ordinal))
                throw new PlanRejectedException($"Native binary identity for {pair.Key} does not match this process. Output was left unchanged.");
        }
    }

    private static Dictionary<string, NativeBinaryFingerprint> IndexNatives(List<NativeBinaryFingerprint>? items, string label)
    {
        var index = new Dictionary<string, NativeBinaryFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items ?? [])
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Name) || !index.TryAdd(item.Name, item))
                throw new PlanRejectedException($"{label} native binary identity is missing or duplicated. Output was left unchanged.");
        }
        return index;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static List<NativeBinaryFingerprint> CaptureNativeBinaries()
    {
        var loaded = LoadedNativeModules();
        if (!loaded.Keys.Any(name => name.Equals("OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase)))
            throw new PlanRejectedException("OpenCvSharpExtern.dll is not loaded. Replay did not invent a native binary identity.");
        if (!loaded.Keys.Any(name => name.StartsWith("opencv_videoio_ffmpeg", StringComparison.OrdinalIgnoreCase)))
        {
            var externPath = loaded.First(pair => pair.Key.Equals("OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase)).Value;
            var directory = Path.GetDirectoryName(externPath) ?? "";
            var sibling = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "opencv_videoio_ffmpeg*.dll").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
            if (sibling is null || LoadLibrary(sibling) == 0)
                throw new PlanRejectedException("The FFmpeg decoder used with OpenCvSharpExtern.dll is not loaded. Replay did not invent a native binary identity.");
            loaded = LoadedNativeModules();
        }
        return loaded.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new NativeBinaryFingerprint { Name = pair.Key, Sha256 = ReplayHashes.Sha256File(pair.Value) })
            .ToList();
    }

    private static Dictionary<string, string> LoadedNativeModules()
    {
        var loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            string name;
            string path;
            try
            {
                name = module.ModuleName ?? "";
                path = module.FileName ?? "";
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                continue;
            }
            if (name.Equals("OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase) || name.StartsWith("opencv_videoio_ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new PlanRejectedException($"Loaded native module '{name}' has no file that can be fingerprinted.");
                loaded[name] = path;
            }
        }
        return loaded;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibrary(string path);
}
