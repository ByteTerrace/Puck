using System.CommandLine;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Puck.Cli.Automation;

/// <summary>
/// <c>puck artifacts</c> — the compiled-output archive that lets every later CI job test, verify, and deploy exactly
/// what one Windows build produced, without compiling again.
/// </summary>
internal static class ArtifactsCommand {
    private const string Archive = "artifacts/compiled-windows.zip";
    private const string Identity = "source.json";

    public static Command Create() {
        var capture = new Command(description: "Archive every compiled Release output with its source identity (Windows).", name: "capture");
        var restore = new Command(description: "Extract this commit's archive into place without compiling.", name: "restore");
        var testWindows = new Command(description: "Run the archived test assemblies through the producer's manifest.", name: "test-windows");
        var testWorld = new Command(description: "Run the compiled world authentication and recovery tests (Linux).", name: "test-world");
        var command = new Command(description: "Capture, restore, and test the compiled Release outputs of one build.", name: "artifacts") { capture, restore, testWindows, testWorld };

        capture.SetAction(action: (_, _) => CaptureAsync());
        restore.SetAction(action: (_, _) => RestoreAsync());
        testWindows.SetAction(action: (_, _) => TestWindowsAsync());
        testWorld.SetAction(action: (_, _) => TestWorldAsync());
        return command;
    }

    private static string Root() => (RepositoryPaths.FindRoot() ?? throw new DirectoryNotFoundException(message: "Run within the Puck checkout."));
    private static async Task<string> CommitAsync(string root) => (await CliProcess.RunCheckedAsync(arguments: ["rev-parse", "HEAD"], capture: true, executable: "git", root: root)).Trim();
    private static string RepositoryRelative(string root, string path) => Path.GetRelativePath(path: path, relativeTo: root).Replace(newChar: '/', oldChar: '\\');
    private static bool WithinRoot(string root, string path) => path.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: (root + Path.DirectorySeparatorChar));
    private static async Task<int> CaptureAsync() {
        if (!OperatingSystem.IsWindows()) { throw new InvalidOperationException(message: "The solution artifact is produced on Windows."); }
        var root = Root();
        var changes = (await CliProcess.RunCheckedAsync(arguments: ["status", "--porcelain", "--untracked-files=no"], capture: true, executable: "git", root: root)).Trim();

        if (changes.Length != 0) { throw new InvalidDataException(message: $"Tracked source changed while producing release artifacts:\n{changes}"); }
        var commit = await CommitAsync(root: root);
        var source = new JsonObject { ["commit"] = commit, ["configuration"] = "Release", ["platform"] = "windows", ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString() };

        CliFiles.WriteJson(path: "artifacts/runtime/source.json", value: source);
        CliFiles.CopyDirectory(destination: "artifacts/runtime/browser", source: "src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle");
        CliFiles.CopyDirectory(destination: "artifacts/batteries/hgb", source: "src/Puck.HumbleGamingBrick.Post/bin/Release/net10.0");
        CliFiles.CopyDirectory(destination: "artifacts/batteries/agb", source: "src/Puck.AdvancedGamingBrick.Post/bin/Release/net10.0");
        foreach (var project in new[] { "Puck.World.Azure.Tests", "Puck.World.Schema.Tests", "Puck.World.Tests" }) {
            CliFiles.CopyDirectory(destination: $"artifacts/world-tests/{project}", source: $"tests/{project}/bin/Release/net10.0");
        }
        using var stream = new FileStream(mode: FileMode.CreateNew, path: Archive);
        using var archive = new ZipArchive(mode: ZipArchiveMode.Create, stream: stream);
        var tests = new JsonArray();
        var contents = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var copies = new JsonObject();

        foreach (var project in new[] { "src", "tests" }.SelectMany(selector: parent => Directory.EnumerateDirectories(path: Path.Combine(path1: root, path2: parent)))) {
            var output = Path.Combine(path1: project, path2: "bin/Release");

            if (!Directory.Exists(path: output)) { continue; }
            foreach (var file in Directory.EnumerateFiles(path: output, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
                var relative = RepositoryRelative(path: file, root: root);
                string hash;

                using (var input = File.OpenRead(path: file)) { hash = Convert.ToHexStringLower(inArray: SHA256.HashData(source: input)); }
                if (contents.TryGetValue(key: hash, value: out var original)) {
                    copies[relative] = original;
                } else {
                    contents.Add(key: hash, value: relative);
                    archive.CreateEntryFromFile(compressionLevel: CompressionLevel.Fastest, entryName: relative, sourceFileName: file);
                }
                if (Path.GetFileName(path: file) == "Puck.TestAssembly") {
                    var name = File.ReadAllText(path: file).Trim();
                    var assembly = Path.Combine(path1: Path.GetDirectoryName(path: file)!, path2: name);

                    if ((name != Path.GetFileName(path: name)) || !name.EndsWith(comparisonType: StringComparison.Ordinal, value: ".dll") || !File.Exists(path: assembly)) {
                        throw new InvalidDataException(message: $"Invalid test assembly marker: {file}");
                    }
                    var test = new JsonObject { ["assembly"] = RepositoryRelative(path: assembly, root: root) };
                    var settings = Path.Combine(path1: Path.GetDirectoryName(path: file)!, path2: "Puck.TestSettings");

                    if (File.Exists(path: settings)) { test["settings"] = File.ReadAllText(path: settings).Trim().Replace(newChar: '/', oldChar: '\\'); }
                    tests.Add(item: test);
                }
            }
        }
        if (tests.Count == 0) { throw new InvalidDataException(message: "No evaluated test outputs; build with -p:PuckCaptureTestArtifacts=true."); }
        source["testAssemblies"] = tests;
        source["copies"] = copies;
        using (var writer = new StreamWriter(stream: archive.CreateEntry(entryName: Identity).Open())) { writer.Write(value: source.ToJsonString()); }
        Console.WriteLine(value: $"Captured compiled Release outputs for {commit}.");
        return 0;
    }
    private static async Task<int> RestoreAsync() {
        var root = Root();

        var (archive, source, copies, _) = await OpenArchiveAsync(occupiedIsError: true, root: root);

        using (archive) {
            foreach (var entry in archive.Entries.Where(predicate: entry => (entry.FullName != Identity))) {
                var destination = Path.Combine(path1: root, path2: entry.FullName);

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
                entry.ExtractToFile(destinationFileName: destination);
            }
            foreach (var copy in copies) {
                var destination = Path.Combine(path1: root, path2: copy.Key);

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
                File.Copy(destFileName: destination, sourceFileName: Path.Combine(path1: root, path2: copy.Value!.GetValue<string>()));
            }
        }
        Console.WriteLine(value: $"Restored compiled Release outputs for {source["commit"]}; no compilation was performed.");
        return 0;
    }
    private static async Task<int> TestWindowsAsync() {
        var root = Root();

        var (archive, source, _, paths) = await OpenArchiveAsync(occupiedIsError: false, root: root);

        archive.Dispose();
        var tests = (source["testAssemblies"]?.AsArray() ?? throw new InvalidDataException(message: "Missing evaluated test assembly manifest."));
        var selected = new Dictionary<string, string?>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var test in tests) {
            var name = (((string?)test?["assembly"]) ?? throw new InvalidDataException(message: "Missing test assembly path."));
            var path = Path.GetFullPath(path: Path.Combine(path1: root, path2: name));
            var settings = ((string?)test?["settings"]);

            if ((settings is not null) && (Path.IsPathRooted(path: settings) || !WithinRoot(path: Path.GetFullPath(path: Path.Combine(path1: root, path2: settings)), root: root) || !File.Exists(path: Path.Combine(path1: root, path2: settings)))) {
                throw new InvalidDataException(message: $"Invalid or missing test run settings: {settings}");
            }
            if (!paths.Contains(item: path) || !name.EndsWith(comparisonType: StringComparison.Ordinal, value: ".dll") || !File.Exists(path: path) || !selected.TryAdd(key: path, value: settings)) {
                throw new InvalidDataException(message: $"Invalid, missing or duplicate test assembly: {name}");
            }
        }
        if (selected.Count == 0) { throw new InvalidDataException(message: "No test assemblies selected."); }
        const string Results = "artifacts/test-results";

        if (Directory.Exists(path: Results)) { throw new IOException(message: $"Use a fresh test result directory: {Results}"); }
        Directory.CreateDirectory(path: Results);
        foreach (var (test, index) in selected.OrderBy(keySelector: entry => entry.Key, comparer: StringComparer.Ordinal).Select(selector: (test, index) => (test, index))) {
            var assembly = test.Key;
            var report = $"{index:D3}-{Path.GetFileNameWithoutExtension(path: assembly)}.trx";
            string[] settings = ((test.Value is { } runSettings) ? ["--settings", runSettings] : []);

            await CliProcess.RunCheckedAsync(arguments: ["test", assembly, .. settings, "--logger", $"trx;LogFileName={report}", "--results-directory", Results, "--filter", "Category!=Performance", "--blame-hang-timeout", "15m", "--blame-hang-dump-type", "mini"], executable: "dotnet", root: root);
            var result = (XDocument.Load(uri: Path.Combine(path1: Results, path2: report)).Root ?? throw new InvalidDataException(message: $"Missing test results: {report}"));
            var ns = result.Name.Namespace;

            // A hardware-only assembly may legitimately skip every case, but discovery must never be empty.
            if (((int?)result.Element(name: (ns + "ResultSummary"))?.Element(name: (ns + "Counters"))?.Attribute(name: "total")) is not > 0) {
                throw new InvalidDataException(message: $"No tests discovered in {assembly}.");
            }
        }
        Console.WriteLine(value: $"Verified {selected.Count} compiled test assemblies without solution restore or workload installation.");
        return 0;
    }
    private static async Task<int> TestWorldAsync() {
        const string Results = "artifacts/world-test-results";
        var root = Root();

        Directory.CreateDirectory(path: Results);
        foreach (var (project, testClass) in new[] {
            ("Puck.World.Azure.Tests", "EntraWorldAuthenticatorTests"),
            ("Puck.World.Schema.Tests", "WorldSiloDefinitionLawTests"),
            ("Puck.World.Tests", "WorldSiloLifecycleLawTests"),
        }) {
            var report = Path.Combine(path1: Results, path2: (project + ".xml"));

            if (File.Exists(path: report)) { throw new IOException(message: $"Use a fresh test report: {report}"); }
            // xUnit v3's in-process runner is portable; VSTest otherwise looks for the producer OS's apphost.
            await CliProcess.RunCheckedAsync(arguments: [$"artifacts/world-tests/{project}/{project}.dll", "-class", $"{project}.{testClass}", "-xml", report], executable: "dotnet", root: root);
            var result = (XDocument.Load(uri: report).Root?.Element(name: "assembly") ?? throw new InvalidDataException(message: $"Missing test result for {project}."));

            if ((((int?)result.Attribute(name: "total")) is not { } total) || (((int?)result.Attribute(name: "skipped")) is not { } skipped) || (total <= skipped)) {
                throw new InvalidDataException(message: $"No tests executed for {project}.{testClass}.");
            }
        }
        return 0;
    }
    // Every destination is validated before any entry is extracted.
    private static async Task<(ZipArchive Archive, JsonNode Source, JsonObject Copies, HashSet<string> Paths)> OpenArchiveAsync(bool occupiedIsError, string root) {
        var archive = ZipFile.OpenRead(archiveFileName: Archive);

        if (archive.Entries.Count(predicate: entry => (entry.FullName == Identity)) != 1) { throw new InvalidDataException(message: "Expected exactly one source identity."); }
        using var reader = new StreamReader(stream: archive.GetEntry(entryName: Identity)!.Open());
        var source = JsonNode.Parse(json: await reader.ReadToEndAsync())!;

        if ((((string?)source["commit"]) != await CommitAsync(root: root)) || (((string?)source["configuration"]) != "Release") || (((string?)source["platform"]) != "windows") || !OperatingSystem.IsWindows() || (((string?)source["architecture"]) != RuntimeInformation.ProcessArchitecture.ToString())) {
            throw new InvalidDataException(message: "Compiled artifacts do not match this commit and platform.");
        }
        var paths = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var copies = (source["copies"]?.AsObject() ?? new JsonObject());

        foreach (var name in archive.Entries.Where(predicate: entry => (entry.FullName != Identity)).Select(selector: entry => entry.FullName).Concat(second: copies.Select(selector: copy => copy.Key))) {
            var destination = Path.GetFullPath(path: Path.Combine(path1: root, path2: name));

            if ((!name.StartsWith(comparisonType: StringComparison.Ordinal, value: "src/") && !name.StartsWith(comparisonType: StringComparison.Ordinal, value: "tests/")) || !name.Contains(comparisonType: StringComparison.Ordinal, value: "/bin/Release/") || name.Contains(value: '\\') || name.Contains(value: ':') || name.Split('/').Any(predicate: part => (part is ".." or "." or "")) || !WithinRoot(path: destination, root: root) || !paths.Add(item: destination) || (occupiedIsError && File.Exists(path: destination))) {
                throw new InvalidDataException(message: $"Invalid or occupied artifact destination: {name}");
            }
        }
        foreach (var copy in copies) {
            if ((((string?)copy.Value) is not { } original) || (original == Identity) || (archive.GetEntry(entryName: original) is null)) {
                throw new InvalidDataException(message: $"Missing original artifact content for {copy.Key}.");
            }
        }
        return (archive, source, copies, paths);
    }
}
