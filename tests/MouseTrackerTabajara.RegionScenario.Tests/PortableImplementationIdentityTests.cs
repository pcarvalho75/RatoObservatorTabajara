using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MouseTrackerTabajara.Application;
using MouseTrackerTabajara.Core;
using Xunit;
using Xunit.Abstractions;

namespace MouseTrackerTabajara.RegionScenario.Tests;

public class PortableImplementationIdentityTests(ITestOutputHelper output)
{
    [Fact]
    public void CopiedBinariesOutsideEveryCheckoutAuthenticateTheExecutingImplementation()
    {
        var root = Path.Combine(Path.GetTempPath(), "tabajara-portable-id-" + Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(root, "app");
        var hostSourceDir = Path.Combine(Path.GetTempPath(), "tabajara-portable-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDir);
        try
        {
            AssertOutsideEveryCheckout(appDir);
            CopyBinaries(AppContext.BaseDirectory, appDir);
            var application = Path.Combine(appDir, "MouseTrackerTabajara.Application.dll");
            var core = Path.Combine(appDir, "MouseTrackerTabajara.Core.dll");
            var desktop = Path.Combine(appDir, "MouseTrackerTabajara.dll");
            Assert.True(File.Exists(application));
            Assert.True(File.Exists(core));
            Assert.True(File.Exists(desktop));
            Assert.Equal(ShaFile(typeof(ScenarioImplementationProbe).Assembly.Location), ShaFile(application));
            Assert.Equal(ShaFile(typeof(RegionScenarioMath).Assembly.Location), ShaFile(core));
            Assert.Empty(Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories));
            foreach (var relative in ScenarioImplementationProbe.SourceFiles)
                Assert.False(File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))), relative);
            Assert.Null(DiscoverSourceRoot(appDir));

            var hostDll = CompileHost(hostSourceDir, appDir);
            var phase1 = Path.Combine(root, "phase1.txt");
            var first = RunHost(hostDll, appDir, phase1);
            var local = ScenarioImplementationProbe.Capture();
            AssertSameImplementation(first, local, appDir);
            Assert.Equal(8, int.Parse(first["embedded_source_count"]));
            Assert.Null(DiscoverSourceRoot(appDir));

            foreach (var relative in ScenarioImplementationProbe.SourceFiles)
            {
                var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "mutated-live-source-not-the-compiled-implementation\n" + relative + "\n");
            }
            var discovered = DiscoverSourceRoot(appDir);
            Assert.NotNull(discovered);
            Assert.True(string.Equals(Path.GetFullPath(root), discovered, StringComparison.OrdinalIgnoreCase), discovered);
            Assert.Empty(Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories));
            var phase2 = Path.Combine(root, "phase2.txt");
            var second = RunHost(hostDll, appDir, phase2);
            AssertSameImplementation(second, local, appDir);
            Assert.Equal(first["source_material_sha256"], second["source_material_sha256"]);
            Assert.Equal(first["canonical_sha256"], second["canonical_sha256"]);
            Assert.NotEqual(Convert.ToHexString(SHA256.HashData(FakePreimage(root))).ToLowerInvariant(), second["source_material_sha256"]);

            output.WriteLine("code_contract=" + local.CodeContract);
            output.WriteLine("schema=" + RegionScenarioEvidenceDocument.CurrentSchema);
            output.WriteLine("core_sha256=" + local.CoreAssemblySha256);
            output.WriteLine("core_mvid=" + local.CoreModuleVersionId);
            output.WriteLine("application_sha256=" + local.ApplicationAssemblySha256);
            output.WriteLine("application_mvid=" + local.ApplicationModuleVersionId);
            output.WriteLine("desktop_name=" + local.DesktopAssemblyName);
            output.WriteLine("desktop_sha256=" + local.DesktopAssemblySha256);
            output.WriteLine("desktop_mvid=" + local.DesktopModuleVersionId);
            output.WriteLine("source_material_sha256=" + local.SourceMaterialSha256);
            output.WriteLine("canonical_sha256=" + local.CanonicalSha256);
            output.WriteLine("portable_app=" + first["application_location"]);
            output.WriteLine("portable_core=" + first["core_location"]);
            output.WriteLine("portable_desktop=" + first["desktop_location"]);
        }
        finally
        {
            RegionScenarioMathTests.DeleteScratch(root, "tabajara-portable-id-");
            RegionScenarioMathTests.DeleteScratch(hostSourceDir, "tabajara-portable-host-");
        }
    }

    private void AssertSameImplementation(Dictionary<string, string> report, ScenarioImplementationIdentity local, string appDir)
    {
        Assert.Equal(RegionScenarioWording.CodeContract, report["code_contract"]);
        Assert.Equal(local.CodeContract, report["code_contract"]);
        Assert.Equal("accepted", report["replay"]);
        Assert.Equal("accepted", report["authenticate"]);
        Assert.Equal("held", report["changed_authenticate"]);
        Assert.Contains("declared code contract is unchanged", report["changed_hold"], StringComparison.Ordinal);
        Assert.Contains("original implementation", report["changed_hold"], StringComparison.Ordinal);
        Assert.Equal("MouseTrackerTabajara", report["desktop_name"]);
        Assert.Equal(local.CoreAssemblySha256, report["core_sha256"]);
        Assert.Equal(local.ApplicationAssemblySha256, report["application_sha256"]);
        Assert.Equal(local.DesktopAssemblySha256, report["desktop_sha256"]);
        Assert.Equal(local.CoreModuleVersionId, report["core_mvid"]);
        Assert.Equal(local.ApplicationModuleVersionId, report["application_mvid"]);
        Assert.Equal(local.DesktopModuleVersionId, report["desktop_mvid"]);
        Assert.Equal(local.SourceMaterialSha256, report["source_material_sha256"]);
        Assert.Equal(local.CanonicalSha256, report["canonical_sha256"]);
        Assert.Equal(ShaFile(Path.Combine(appDir, "MouseTrackerTabajara.Core.dll")), report["core_sha256"]);
        Assert.Equal(ShaFile(Path.Combine(appDir, "MouseTrackerTabajara.Application.dll")), report["application_sha256"]);
        Assert.Equal(ShaFile(Path.Combine(appDir, "MouseTrackerTabajara.dll")), report["desktop_sha256"]);
        Assert.Equal(Path.GetFullPath(Path.Combine(appDir, "MouseTrackerTabajara.Core.dll")), Path.GetFullPath(report["core_location"]), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(Path.Combine(appDir, "MouseTrackerTabajara.Application.dll")), Path.GetFullPath(report["application_location"]), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(Path.Combine(appDir, "MouseTrackerTabajara.dll")), Path.GetFullPath(report["desktop_location"]), StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(appDir) + Path.DirectorySeparatorChar, Path.GetFullPath(report["base_directory"]) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var recorded = new ScenarioImplementationIdentity
        {
            CodeContract = report["code_contract"],
            CoreAssemblyName = report["core_name"],
            CoreAssemblySha256 = report["core_sha256"],
            CoreModuleVersionId = report["core_mvid"],
            ApplicationAssemblyName = report["application_name"],
            ApplicationAssemblySha256 = report["application_sha256"],
            ApplicationModuleVersionId = report["application_mvid"],
            DesktopAssemblyName = report["desktop_name"],
            DesktopAssemblySha256 = report["desktop_sha256"],
            DesktopModuleVersionId = report["desktop_mvid"],
            SourceMaterialSha256 = report["source_material_sha256"],
            CanonicalSha256 = report["canonical_sha256"]
        };
        Assert.Null(ScenarioImplementationProbe.ReplayHold(recorded, local));
        var material = "{\"plan\":\"synthetic frozen material\"}";
        var computation = "{\"scenario\":\"portable-copy\"}";
        RegionScenarioEvidenceStore.Authenticate(new RegionScenarioEvidenceDocument
        {
            CodeContract = recorded.CodeContract,
            Identity = new FrozenRegionIdentity { CodeContract = recorded.CodeContract, Implementation = recorded },
            ComputationJson = computation,
            ComputationSha256 = ShaText(computation),
            FrozenMaterialJson = material,
            FrozenMaterialSha256 = ShaText(material)
        });
        var changed = recorded with { CoreAssemblySha256 = new string('b', 64) };
        changed = changed with { CanonicalSha256 = ScenarioImplementationProbe.Canonical(changed) };
        Assert.Equal(recorded.CodeContract, changed.CodeContract);
        var hold = ScenarioImplementationProbe.ReplayHold(changed, local);
        Assert.Contains("declared code contract is unchanged", hold, StringComparison.Ordinal);
        var denied = Assert.Throws<InvalidDataException>(() => RegionScenarioEvidenceStore.Authenticate(new RegionScenarioEvidenceDocument
        {
            CodeContract = recorded.CodeContract,
            Identity = new FrozenRegionIdentity { CodeContract = recorded.CodeContract, Implementation = changed },
            ComputationJson = computation,
            ComputationSha256 = ShaText(computation),
            FrozenMaterialJson = material,
            FrozenMaterialSha256 = ShaText(material)
        }));
        Assert.Contains("unchanged", denied.Message, StringComparison.Ordinal);
    }

    private Dictionary<string, string> RunHost(string hostDll, string appDir, string reportPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = appDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(hostDll);
        start.ArgumentList.Add(appDir);
        start.ArgumentList.Add(reportPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Portable host did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Portable host did not exit. " + stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        var outText = stdout.GetAwaiter().GetResult();
        var errText = stderr.GetAwaiter().GetResult();
        output.WriteLine(outText);
        output.WriteLine(errText);
        Assert.True(process.ExitCode == 0, "Portable host exited " + process.ExitCode + "\n" + outText + "\n" + errText);
        Assert.True(File.Exists(reportPath), errText);
        return File.ReadAllLines(reportPath)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts.Length == 2 ? parts[1] : "", StringComparer.Ordinal);
    }

    private static string CompileHost(string sourceDir, string appDir)
    {
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "Host.cs");
        File.WriteAllText(source, HostSource);
        var sdkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "sdk");
        var sdk = Directory.GetDirectories(sdkRoot).Select(Path.GetFileName).Where(name => name is not null && name.StartsWith("10.", StringComparison.Ordinal)).OrderBy(name => Version.Parse(name!.Split('-')[0])).Last()!;
        var csc = Path.Combine(sdkRoot, sdk, "Roslyn", "bincore", "csc.dll");
        var refRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "packs", "Microsoft.NETCore.App.Ref");
        var refVersion = Directory.GetDirectories(refRoot).Select(Path.GetFileName).Where(name => name is not null && name.StartsWith("10.", StringComparison.Ordinal)).OrderBy(name => Version.Parse(name!.Split('-')[0])).Last()!;
        var refs = Directory.GetFiles(Path.Combine(refRoot, refVersion, "ref", "net10.0"), "*.dll");
        var hostDll = Path.Combine(appDir, "PortableCaptureHost.dll");
        var response = new StringBuilder();
        response.AppendLine("-nologo");
        response.AppendLine("-target:exe");
        response.AppendLine("-nostdlib");
        response.AppendLine("-noconfig");
        response.AppendLine("-langversion:latest");
        response.AppendLine("-out:\"" + hostDll + "\"");
        response.AppendLine("\"" + source + "\"");
        foreach (var reference in refs) response.AppendLine("-r:\"" + reference + "\"");
        var responsePath = Path.Combine(sourceDir, "host.rsp");
        File.WriteAllText(responsePath, response.ToString());
        var start = new ProcessStartInfo { FileName = "dotnet", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(csc);
        start.ArgumentList.Add("@" + responsePath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("csc did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !File.Exists(hostDll))
            throw new InvalidOperationException("Portable host compile failed " + process.ExitCode + "\n" + stdout + "\n" + stderr);
        var desktopRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        var runtimeVersion = Directory.GetDirectories(desktopRuntime).Select(Path.GetFileName).Where(name => name is not null && name.StartsWith("10.", StringComparison.Ordinal)).OrderBy(name => Version.Parse(name!)).Last()!;
        File.WriteAllText(Path.Combine(appDir, "PortableCaptureHost.runtimeconfig.json"),
            "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0-windows\",\n    \"rollForward\": \"LatestMinor\",\n    \"framework\": {\n      \"name\": \"Microsoft.WindowsDesktop.App\",\n      \"version\": \"" + runtimeVersion + "\"\n    }\n  }\n}\n");
        return hostDll;
    }

    private static void CopyBinaries(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.GetDirectories(source))
            CopyBinaries(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void AssertOutsideEveryCheckout(string path)
    {
        var full = Path.GetFullPath(path);
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                throw new InvalidOperationException("Portable binaries are inside a checkout: " + dir.FullName);
        }
    }

    private static string? DiscoverSourceRoot(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        for (var depth = 0; dir is not null && depth < 10; depth++, dir = dir.Parent)
        {
            if (ScenarioImplementationProbe.SourceFiles.All(relative => File.Exists(Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar)))))
                return dir.FullName;
        }
        return null;
    }

    private static byte[] FakePreimage(string repositoryRoot)
    {
        using var buffer = new MemoryStream();
        foreach (var relative in ScenarioImplementationProbe.SourceFiles)
        {
            var bytes = File.ReadAllBytes(Path.Combine(repositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var header = Encoding.UTF8.GetBytes(relative + "\n" + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            buffer.Write(header);
            buffer.Write(bytes);
            buffer.WriteByte(0x0A);
        }
        return buffer.ToArray();
    }

    private static string ShaFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string ShaText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private const string HostSource = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using System.Reflection;
        using System.Runtime.Loader;
        using System.Text;

        static class Program
        {
            static int Main(string[] args)
            {
                try
                {
                    var appDir = Path.GetFullPath(args[0]);
                    var reportPath = Path.GetFullPath(args[1]);
                    var alc = new IsolatedBinaries(appDir);
                    var app = alc.LoadFromAssemblyPath(Path.Combine(appDir, "MouseTrackerTabajara.Application.dll"));
                    var probe = Required(app, "MouseTrackerTabajara.Application.ScenarioImplementationProbe");
                    var store = Required(app, "MouseTrackerTabajara.Application.RegionScenarioEvidenceStore");
                    var documentType = Required(app, "MouseTrackerTabajara.Application.RegionScenarioEvidenceDocument");
                    var identityType = Required(app, "MouseTrackerTabajara.Application.ScenarioImplementationIdentity");
                    var frozenType = Required(app, "MouseTrackerTabajara.Application.FrozenRegionIdentity");
                    var flags = BindingFlags.Public | BindingFlags.Static;
                    var captured = probe.GetMethod("Capture", flags)!.Invoke(null, null)!;
                    var replay = probe.GetMethod("ReplayHold", flags)!.Invoke(null, new object?[] { captured, captured });
                    if (replay is not null) throw new InvalidOperationException("Same-implementation replay held: " + replay);
                    var files = (string[])probe.GetField("SourceFiles", flags)!.GetValue(null)!;
                    var prefix = (string)probe.GetField("SourceResourcePrefix", flags)!.GetValue(null)!;
                    var names = app.GetManifestResourceNames();
                    foreach (var relative in files)
                    {
                        if (!names.Contains(prefix + relative)) throw new InvalidOperationException("Missing embedded scenario source " + relative);
                    }
                    var contract = Text(captured, "CodeContract");
                    var material = "{\"plan\":\"synthetic frozen material\"}";
                    var computation = "{\"scenario\":\"portable-copy\"}";
                    var document = Document(documentType, frozenType, contract, captured, computation, material);
                    store.GetMethod("Authenticate", flags)!.Invoke(null, new[] { document });
                    var changed = Activator.CreateInstance(identityType)!;
                    foreach (var property in identityType.GetProperties()) property.SetValue(changed, property.GetValue(captured));
                    identityType.GetProperty("CoreAssemblySha256")!.SetValue(changed, new string('b', 64));
                    var canonical = (string)probe.GetMethod("Canonical", flags)!.Invoke(null, new[] { changed })!;
                    identityType.GetProperty("CanonicalSha256")!.SetValue(changed, canonical);
                    var hold = (string?)probe.GetMethod("ReplayHold", flags)!.Invoke(null, new object?[] { changed, captured });
                    if (hold is null || hold.IndexOf("unchanged", StringComparison.Ordinal) < 0)
                        throw new InvalidOperationException("Changed implementation was not held: " + hold);
                    var changedDocument = Document(documentType, frozenType, contract, changed, computation, material);
                    try
                    {
                        store.GetMethod("Authenticate", flags)!.Invoke(null, new[] { changedDocument });
                        throw new InvalidOperationException("Changed implementation was accepted.");
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException invalid)
                    {
                        if (invalid.Message.IndexOf("unchanged", StringComparison.Ordinal) < 0) throw;
                        var coreLocation = LocationOf("MouseTrackerTabajara.Core");
                        var applicationLocation = LocationOf("MouseTrackerTabajara.Application");
                        var desktopLocation = LocationOf("MouseTrackerTabajara");
                        var lines = new[]
                        {
                            "code_contract=" + contract,
                            "core_name=" + Text(captured, "CoreAssemblyName"),
                            "core_sha256=" + Text(captured, "CoreAssemblySha256"),
                            "core_mvid=" + Text(captured, "CoreModuleVersionId"),
                            "application_name=" + Text(captured, "ApplicationAssemblyName"),
                            "application_sha256=" + Text(captured, "ApplicationAssemblySha256"),
                            "application_mvid=" + Text(captured, "ApplicationModuleVersionId"),
                            "desktop_name=" + Text(captured, "DesktopAssemblyName"),
                            "desktop_sha256=" + Text(captured, "DesktopAssemblySha256"),
                            "desktop_mvid=" + Text(captured, "DesktopModuleVersionId"),
                            "source_material_sha256=" + Text(captured, "SourceMaterialSha256"),
                            "canonical_sha256=" + Text(captured, "CanonicalSha256"),
                            "application_location=" + applicationLocation,
                            "core_location=" + coreLocation,
                            "desktop_location=" + desktopLocation,
                            "base_directory=" + AppContext.BaseDirectory,
                            "embedded_source_count=" + files.Length.ToString(),
                            "replay=accepted",
                            "authenticate=accepted",
                            "changed_hold=" + hold.Replace("\r", " ").Replace("\n", " "),
                            "changed_authenticate=held"
                        };
                        File.WriteAllLines(reportPath, lines, new UTF8Encoding(false));
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    return 1;
                }
            }

            static Type Required(Assembly assembly, string name) => assembly.GetType(name, throwOnError: true)!;

            static object Document(Type documentType, Type frozenType, string contract, object implementation, string computation, string material)
            {
                var document = Activator.CreateInstance(documentType)!;
                Set(document, "CodeContract", contract);
                Set(document, "ComputationJson", computation);
                Set(document, "ComputationSha256", Sha(computation));
                Set(document, "FrozenMaterialJson", material);
                Set(document, "FrozenMaterialSha256", Sha(material));
                var frozen = Activator.CreateInstance(frozenType)!;
                Set(frozen, "CodeContract", contract);
                Set(frozen, "Implementation", implementation);
                Set(document, "Identity", frozen);
                return document;
            }

            static void Set(object target, string name, object? value) => target.GetType().GetProperty(name)!.SetValue(target, value);
            static string Text(object target, string name) => (string)target.GetType().GetProperty(name)!.GetValue(target)!;
            static string Sha(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

            static string LocationOf(string assemblyName)
            {
                var found = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assembly => !assembly.IsDynamic && string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal))
                    .Select(assembly => assembly.Location)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (found.Length != 1) throw new InvalidOperationException(assemblyName + " locations: " + string.Join(" | ", found));
                return found[0];
            }
        }

        sealed class IsolatedBinaries : AssemblyLoadContext
        {
            private readonly string _dir;
            public IsolatedBinaries(string dir) : base("portable-scenario") { _dir = dir; }
            protected override Assembly? Load(AssemblyName assemblyName)
            {
                var name = assemblyName.Name;
                if (string.IsNullOrEmpty(name)) return null;
                var path = Path.Combine(_dir, name + ".dll");
                if (File.Exists(path)) return LoadFromAssemblyPath(path);
                return null;
            }
        }
        """;
}
