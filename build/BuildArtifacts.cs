#!/usr/bin/env dotnet
#:property PublishAot=false

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using static Puck.AutomationProcess;

try {
    var root = Puck.RepositoryPaths.FindRoot() ?? throw new DirectoryNotFoundException("Run within the Puck checkout.");
    var commit = args is ["capture" or "restore" or "test-windows"] ? await RunAsync("git", ["rev-parse", "HEAD"], capture: true) : "";
    const string Archive = "artifacts/compiled-windows.zip";
    if (args is ["capture"]) {
        if (!OperatingSystem.IsWindows()) { throw new InvalidOperationException("The solution artifact is produced on Windows."); }
        var changes = await RunAsync("git", ["status", "--porcelain", "--untracked-files=no"], capture: true);
        if (changes.Length != 0) {
            throw new InvalidDataException($"Tracked source changed while producing release artifacts:\n{changes}");
        }
        var source = new JsonObject { ["commit"] = commit, ["configuration"] = "Release", ["platform"] = "windows", ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString() };
        Write("artifacts/runtime/source.json", source);
        CopyDirectory("src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle", "artifacts/runtime/browser");
        CopyDirectory("src/Puck.HumbleGamingBrick.Post/bin/Release/net10.0", "artifacts/batteries/hgb");
        CopyDirectory("src/Puck.AdvancedGamingBrick.Post/bin/Release/net10.0", "artifacts/batteries/agb");
        foreach (var project in new[] { "Puck.World.Azure.Tests", "Puck.World.Schema.Tests", "Puck.World.Tests" }) {
            CopyDirectory($"tests/{project}/bin/Release/net10.0", $"artifacts/world-tests/{project}");
        }
        using var stream = new FileStream(Archive, FileMode.CreateNew);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var tests = new JsonArray();
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        var copies = new JsonObject();
        foreach (var project in new[] { "src", "tests" }.SelectMany(parent => Directory.EnumerateDirectories(Path.Combine(root, parent)))) {
            var output = Path.Combine(project, "bin/Release");
            if (!Directory.Exists(output)) { continue; }
            foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)) {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                string hash;
                using (var input = File.OpenRead(file)) { hash = Convert.ToHexStringLower(SHA256.HashData(input)); }
                if (contents.TryGetValue(hash, out var original)) {
                    copies[relative] = original;
                } else {
                    contents.Add(hash, relative);
                    archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                }
                if (Path.GetFileName(file) == "Puck.TestAssembly") {
                    var name = File.ReadAllText(file).Trim();
                    var assembly = Path.Combine(Path.GetDirectoryName(file)!, name);
                    if (name != Path.GetFileName(name) || !name.EndsWith(".dll", StringComparison.Ordinal) || !File.Exists(assembly)) {
                        throw new InvalidDataException($"Invalid test assembly marker: {file}");
                    }
                    var test = new JsonObject { ["assembly"] = Path.GetRelativePath(root, assembly).Replace('\\', '/') };
                    var settings = Path.Combine(Path.GetDirectoryName(file)!, "Puck.TestSettings");
                    if (File.Exists(settings)) { test["settings"] = File.ReadAllText(settings).Trim().Replace('\\', '/'); }
                    tests.Add(item: test);
                }
            }
        }
        if (tests.Count == 0) { throw new InvalidDataException("No evaluated test outputs; build with -p:PuckCaptureTestArtifacts=true."); }
        source["testAssemblies"] = tests;
        source["copies"] = copies;
        using (var writer = new StreamWriter(archive.CreateEntry("source.json").Open())) { writer.Write(source.ToJsonString()); }
        Console.WriteLine($"Captured compiled Release outputs for {commit}.");
    } else if (args is ["restore" or "test-windows"]) {
        using var archive = ZipFile.OpenRead(Archive);
        if (archive.Entries.Count(entry => entry.FullName == "source.json") != 1) { throw new InvalidDataException("Expected exactly one source identity."); }
        using var reader = new StreamReader((archive.GetEntry("source.json") ?? throw new InvalidDataException("Missing source identity.")).Open());
        var source = JsonNode.Parse(await reader.ReadToEndAsync())!;
        if ((string?)source["commit"] != commit || (string?)source["configuration"] != "Release" || (string?)source["platform"] != "windows" || !OperatingSystem.IsWindows() || (string?)source["architecture"] != RuntimeInformation.ProcessArchitecture.ToString()) {
            throw new InvalidDataException("Compiled artifacts do not match this commit and platform.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copies = source["copies"]?.AsObject() ?? new JsonObject();
        // Validate every destination before extracting any entry.
        foreach (var name in archive.Entries.Where(entry => entry.FullName != "source.json").Select(entry => entry.FullName).Concat(copies.Select(copy => copy.Key))) {
            var destination = Path.GetFullPath(Path.Combine(root, name));
            if ((!name.StartsWith("src/", StringComparison.Ordinal) && !name.StartsWith("tests/", StringComparison.Ordinal)) || !name.Contains("/bin/Release/", StringComparison.Ordinal) || name.Contains('\\') || name.Contains(':') || name.Split('/').Any(part => part is ".." or "." or "") || !destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !paths.Add(destination) || (args[0] == "restore" && File.Exists(destination))) {
                throw new InvalidDataException($"Invalid or occupied artifact destination: {name}");
            }
        }
        foreach (var copy in copies) {
            if ((string?)copy.Value is not { } original || original == "source.json" || archive.GetEntry(original) is null) {
                throw new InvalidDataException($"Missing original artifact content for {copy.Key}.");
            }
        }
        if (args[0] == "test-windows") {
            var tests = source["testAssemblies"]?.AsArray() ?? throw new InvalidDataException("Missing evaluated test assembly manifest.");
            var selected = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var test in tests) {
                var name = (string?)test?["assembly"] ?? throw new InvalidDataException("Missing test assembly path.");
                var path = Path.GetFullPath(Path.Combine(root, name));
                var settings = (string?)test?["settings"];
                if (settings is not null && (Path.IsPathRooted(settings) || !Path.GetFullPath(Path.Combine(root, settings)).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(root, settings)))) {
                    throw new InvalidDataException($"Invalid or missing test run settings: {settings}");
                }
                if (!paths.Contains(path) || !name.EndsWith(".dll", StringComparison.Ordinal) || !File.Exists(path) || !selected.TryAdd(path, settings)) {
                    throw new InvalidDataException($"Invalid, missing or duplicate test assembly: {name}");
                }
            }
            if (selected.Count == 0) { throw new InvalidDataException("No test assemblies selected."); }
            const string Results = "artifacts/test-results";
            if (Directory.Exists(Results)) { throw new IOException($"Use a fresh test result directory: {Results}"); }
            Directory.CreateDirectory(Results);
            foreach (var (test, index) in selected.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select((test, index) => (test, index))) {
                var assembly = test.Key;
                var report = $"{index:D3}-{Path.GetFileNameWithoutExtension(assembly)}.trx";
                string[] settings = test.Value is { } runSettings ? ["--settings", runSettings] : [];
                await RunAsync("dotnet", ["test", assembly, .. settings, "--logger", $"trx;LogFileName={report}",
                    "--results-directory", Results, "--filter", "Category!=Performance", "--blame-hang-timeout", "15m", "--blame-hang-dump-type", "mini"]);
                var result = XDocument.Load(Path.Combine(Results, report)).Root ?? throw new InvalidDataException($"Missing test results: {report}");
                var ns = result.Name.Namespace;
                // A hardware-only assembly may legitimately skip every case, but discovery must never be empty.
                if ((int?)result.Element(ns + "ResultSummary")?.Element(ns + "Counters")?.Attribute("total") is not > 0) {
                    throw new InvalidDataException($"No tests discovered in {assembly}.");
                }
            }
            Console.WriteLine($"Verified {selected.Count} compiled test assemblies without solution restore or workload installation.");
            return 0;
        }
        foreach (var entry in archive.Entries.Where(entry => entry.FullName != "source.json")) {
            var destination = Path.Combine(root, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination);
        }
        foreach (var copy in copies) {
            var destination = Path.Combine(root, copy.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(root, copy.Value!.GetValue<string>()), destination);
        }
        Console.WriteLine($"Restored compiled Release outputs for {commit}; no compilation was performed.");
    } else if (args is ["test-world"]) {
        const string Results = "artifacts/world-test-results";
        Directory.CreateDirectory(Results);
        foreach (var (project, testClass) in new[] {
            ("Puck.World.Azure.Tests", "EntraWorldAuthenticatorTests"),
            ("Puck.World.Schema.Tests", "WorldSiloDefinitionLawTests"),
            ("Puck.World.Tests", "WorldSiloLifecycleLawTests"),
        }) {
            var report = Path.Combine(Results, project + ".xml");
            if (File.Exists(report)) { throw new IOException($"Use a fresh test report: {report}"); }
            // xUnit v3's in-process runner is portable; VSTest otherwise looks for the producer OS's apphost.
            await RunAsync("dotnet", [$"artifacts/world-tests/{project}/{project}.dll", "-class", project + "." + testClass, "-xml", report]);
            var result = XDocument.Load(report).Root?.Element("assembly") ?? throw new InvalidDataException($"Missing test result for {project}.");
            if ((int?)result.Attribute("total") is not { } total || (int?)result.Attribute("skipped") is not { } skipped || total <= skipped) {
                throw new InvalidDataException($"No tests executed for {project}.{testClass}.");
            }
        }
    } else {
        Console.WriteLine("dotnet run -c Release --file build/BuildArtifacts.cs -- <capture|restore|test-windows|test-world>");
        return args is ["--help" or "-h"] ? 0 : 2;
    }
    return 0;
} catch (Exception error) {
    Console.Error.WriteLine($"build artifacts: {error.Message}");
    return 1;
}
