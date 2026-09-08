#!/usr/bin/env dotnet
#:property PublishAot=false

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using static Puck.AutomationProcess;

try {
    var root = Puck.RepositoryPaths.FindRoot() ?? throw new DirectoryNotFoundException("Run within the Puck checkout.");
    var commit = args is ["capture" or "restore"] ? await RunAsync("git", ["rev-parse", "HEAD"], capture: true) : "";
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
        using (var writer = new StreamWriter(archive.CreateEntry("source.json").Open())) { writer.Write(source.ToJsonString()); }
        foreach (var project in new[] { "src", "tests" }.SelectMany(parent => Directory.EnumerateDirectories(Path.Combine(root, parent)))) {
            var output = Path.Combine(project, "bin/Release");
            if (!Directory.Exists(output)) { continue; }
            foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)) {
                archive.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Fastest);
            }
        }
        Console.WriteLine($"Captured compiled Release outputs for {commit}.");
    } else if (args is ["restore"]) {
        using var archive = ZipFile.OpenRead(Archive);
        using var reader = new StreamReader((archive.GetEntry("source.json") ?? throw new InvalidDataException("Missing source identity.")).Open());
        var source = JsonNode.Parse(await reader.ReadToEndAsync())!;
        if ((string?)source["commit"] != commit || (string?)source["configuration"] != "Release" || (string?)source["platform"] != "windows" || !OperatingSystem.IsWindows() || (string?)source["architecture"] != RuntimeInformation.ProcessArchitecture.ToString()) {
            throw new InvalidDataException("Compiled artifacts do not match this commit and platform.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate every destination before extracting any entry.
        foreach (var entry in archive.Entries.Where(entry => entry.FullName != "source.json")) {
            var name = entry.FullName;
            var destination = Path.GetFullPath(Path.Combine(root, name));
            if ((!name.StartsWith("src/", StringComparison.Ordinal) && !name.StartsWith("tests/", StringComparison.Ordinal)) || !name.Contains("/bin/Release/", StringComparison.Ordinal) || name.Contains('\\') || name.Contains(':') || name.Split('/').Any(part => part is ".." or "." or "") || !destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !paths.Add(destination) || File.Exists(destination)) {
                throw new InvalidDataException($"Invalid or occupied artifact destination: {name}");
            }
        }
        foreach (var entry in archive.Entries.Where(entry => entry.FullName != "source.json")) {
            var destination = Path.Combine(root, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination);
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
        Console.WriteLine("dotnet run -c Release --file build/BuildArtifacts.cs -- <capture|restore|test-world>");
        return args is ["--help" or "-h"] ? 0 : 2;
    }
    return 0;
} catch (Exception error) {
    Console.Error.WriteLine($"build artifacts: {error.Message}");
    return 1;
}
