using System.CommandLine;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Puck.Assets;

namespace Puck.Cli.Automation;

/// <summary>
/// <c>puck artifacts</c> — the compiled-output archive that lets every later CI job test, verify, and deploy exactly
/// what one Windows build produced, without compiling again.
/// </summary>
public static class ArtifactsCommand {
    /// <summary>Returns the <c>dotnet</c> arguments <c>test-windows</c> runs one archived test assembly with. The runner has
    /// no GPU and its tree is a restored archive rather than a build, so the run takes the CPU selection
    /// (<see cref="CliTestRun.CpuSelection"/>) and leaves out the laws that read a build tree
    /// (<see cref="CliTestRun.WithoutBuildTree"/>), writes its TRX report, and dumps and ends a run that stops making
    /// progress.</summary>
    /// <param name="assembly">The test assembly.</param>
    /// <param name="results">The results directory.</param>
    /// <param name="report">The TRX report's file name in it.</param>
    /// <returns>The arguments.</returns>
    public static string[] TestWindowsArguments(string assembly, string results, string report) => [
        assembly,
        .. CliTestRun.CpuSelection,
        .. CliTestRun.WithoutBuildTree,
        .. CliTestRun.Report(directory: results, fileName: report),
        .. CliTestRun.HangDump(timeout: TimeSpan.FromMinutes(minutes: 15)),
    ];
    private const string Archive = "artifacts/compiled-windows.zip";
    private const string Identity = "source.json";

    private static async Task<int> CaptureAsync() {
        if (!OperatingSystem.IsWindows()) { throw new InvalidOperationException(message: "The solution artifact is produced on Windows."); }
        var root = RepositoryPaths.RequireRoot();
        var changes = (await CliProcess.RunCheckedAsync(
            arguments: ["status", "--porcelain", "--untracked-files=no"],
            capture: true,
            fileName: "git",
            workingDirectory: root
        )).Trim();

        if (changes.Length != 0) { throw new InvalidDataException(message: $"Tracked source changed while producing release artifacts:\n{changes}"); }
        var commit = await CommitAsync(root: root);
        var source = new JsonObject { ["commit"] = commit, ["configuration"] = "Release", ["platform"] = "windows", ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString() };

        CliFiles.WriteJson(
            path: "artifacts/runtime/source.json",
            value: source
        );
        CliFiles.CopyDirectory(
            destination: "artifacts/runtime/browser",
            source: "src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle"
        );
        CliFiles.CopyDirectory(
            destination: "artifacts/batteries/hgb",
            source: "src/Puck.HumbleGamingBrick.Post/bin/Release/net10.0"
        );
        CliFiles.CopyDirectory(
            destination: "artifacts/batteries/agb",
            source: "src/Puck.AdvancedGamingBrick.Post/bin/Release/net10.0"
        );
        CliFiles.CopyDirectory(
            destination: "artifacts/batteries/hgd",
            source: "src/Puck.HumbleGamingDeck.Post/bin/Release/net10.0"
        );
        foreach (var project in new[] { "Puck.World.Azure.Tests", "Puck.World.Schema.Tests", "Puck.World.Silo.Tests" }) {
            CliFiles.CopyDirectory(
                destination: $"artifacts/world-tests/{project}",
                source: $"tests/{project}/bin/Release/net10.0"
            );
        }
        using var stream = new FileStream(
            mode: FileMode.CreateNew,
            path: Archive
        );
        using var archive = new ZipArchive(
            mode: ZipArchiveMode.Create,
            stream: stream
        );
        var tests = new JsonArray();
        var contents = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var copies = new JsonObject();

        // The archive's entry order and its tests array are the walk order, so the walk is ordinal: a directory listing is the
        // host's order, and two hosts would write two archives from one tree.
        foreach (var project in new[] { "src", "tests" }.SelectMany(selector: parent => Directory.EnumerateDirectories(path: Path.Combine(
            path1: root,
            path2: parent
        )).Order(comparer: StringComparer.Ordinal))) {
            var output = Path.Combine(
                path1: project,
                path2: "bin/Release"
            );

            if (!Directory.Exists(path: output)) { continue; }
            foreach (var file in Directory.EnumerateFiles(
                path: output,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            ).Order(comparer: StringComparer.Ordinal)) {
                var relative = RepositoryRelative(
                    path: file,
                    root: root
                );
                var hash = ContentPin.OfFile(path: file).Hex;

                if (contents.TryGetValue(
                    key: hash,
                    value: out var original
                )) {
                    copies[relative] = original;
                } else {
                    contents.Add(
                        key: hash,
                        value: relative
                    );
                    archive.CreateEntryFromFile(
                        compressionLevel: CompressionLevel.Fastest,
                        entryName: relative,
                        sourceFileName: file
                    );
                }
                if (Path.GetFileName(path: file) == "Puck.TestAssembly") {
                    var name = File.ReadAllText(path: file).Trim();
                    var assembly = Path.Combine(
                        path1: Path.GetDirectoryName(path: file)!,
                        path2: name
                    );

                    if (
                        (name != Path.GetFileName(path: name)) ||
                        !name.EndsWith(
                        comparisonType: StringComparison.Ordinal,
                        value: ".dll"
                    ) ||
                        !File.Exists(path: assembly)
                    ) {
                        throw new InvalidDataException(message: $"Invalid test assembly marker: {file}");
                    }
                    tests.Add(item: new JsonObject {
                        ["assembly"] = RepositoryRelative(
                        path: assembly,
                        root: root
                    ),
                    });
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
    private static async Task<string> CommitAsync(string root) => (await CliProcess.RunCheckedAsync(
        arguments: ["rev-parse", "HEAD"],
        capture: true,
        fileName: "git",
        workingDirectory: root
    )).Trim();
    // Every destination is validated before any entry is extracted.
    private static async Task<(ZipArchive Archive, JsonNode Source, JsonObject Copies, HashSet<string> Paths)> OpenArchiveAsync(bool occupiedIsError, string root) {
        var archive = ZipFile.OpenRead(archiveFileName: Archive);

        if (archive.Entries.Count(predicate: entry => (entry.FullName == Identity)) != 1) { throw new InvalidDataException(message: "Expected exactly one source identity."); }
        using var reader = new StreamReader(stream: archive.GetEntry(entryName: Identity)!.Open());
        var source = JsonNode.Parse(json: await reader.ReadToEndAsync())!;

        if (
            (((string?)source["commit"]) != await CommitAsync(root: root)) ||
            (((string?)source["configuration"]) != "Release") ||
            (((string?)source["platform"]) != "windows") ||
            !OperatingSystem.IsWindows() ||
            (((string?)source["architecture"]) != RuntimeInformation.ProcessArchitecture.ToString())
        ) {
            throw new InvalidDataException(message: "Compiled artifacts do not match this commit and platform.");
        }
        var paths = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var copies = (source["copies"]?.AsObject() ?? new JsonObject());

        foreach (var name in archive.Entries.Where(predicate: entry => (entry.FullName != Identity)).Select(selector: entry => entry.FullName).Concat(second: copies.Select(selector: copy => copy.Key))) {
            var destination = Path.GetFullPath(path: Path.Combine(
                path1: root,
                path2: name
            ));

            if (
                (!name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "src/"
            ) && !name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "tests/"
            )) ||
                !name.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "/bin/Release/"
            ) ||
                name.Contains(value: '\\') ||
                name.Contains(value: ':') ||
                name.Split('/').Any(predicate: part => (part is ".." or "." or "")) ||
                !WithinRoot(
                path: destination,
                root: root
            ) ||
                !paths.Add(item: destination) ||
                (occupiedIsError && File.Exists(path: destination))
            ) {
                throw new InvalidDataException(message: $"Invalid or occupied artifact destination: {name}");
            }
        }
        foreach (var copy in copies) {
            if (
                (((string?)copy.Value) is not { } original) ||
                (original == Identity) ||
                (archive.GetEntry(entryName: original) is null)
            ) {
                throw new InvalidDataException(message: $"Missing original artifact content for {copy.Key}.");
            }
        }
        return (archive, source, copies, paths);
    }
    private static string RepositoryRelative(string root, string path) => Path.GetRelativePath(
        path: path,
        relativeTo: root
    ).Replace(
        newChar: '/',
        oldChar: '\\'
    );
    private static async Task<int> RestoreAsync() {
        var root = RepositoryPaths.RequireRoot();

        var (archive, source, copies, _) = await OpenArchiveAsync(
            occupiedIsError: true,
            root: root
        );

        using (archive) {
            foreach (var entry in archive.Entries.Where(predicate: entry => (entry.FullName != Identity))) {
                var destination = Path.Combine(
                    path1: root,
                    path2: entry.FullName
                );

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
                entry.ExtractToFile(destinationFileName: destination);
            }
            foreach (var copy in copies) {
                var destination = Path.Combine(
                    path1: root,
                    path2: copy.Key
                );

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
                File.Copy(
                    destFileName: destination,
                    sourceFileName: Path.Combine(
                        path1: root,
                        path2: copy.Value!.GetValue<string>()
                    )
                );
            }
        }
        Console.WriteLine(value: $"Restored compiled Release outputs for {source["commit"]}; no compilation was performed.");
        return 0;
    }
    private static async Task<int> TestWindowsAsync() {
        var root = RepositoryPaths.RequireRoot();

        var (archive, source, _, paths) = await OpenArchiveAsync(
            occupiedIsError: false,
            root: root
        );

        archive.Dispose();
        var tests = (source["testAssemblies"]?.AsArray() ?? throw new InvalidDataException(message: "Missing evaluated test assembly manifest."));
        var selected = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var test in tests) {
            var name = (((string?)test?["assembly"]) ?? throw new InvalidDataException(message: "Missing test assembly path."));
            var path = Path.GetFullPath(path: Path.Combine(
                path1: root,
                path2: name
            ));

            if (
                !paths.Contains(item: path) ||
                !name.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".dll"
            ) ||
                !File.Exists(path: path) ||
                !selected.Add(item: path)
            ) {
                throw new InvalidDataException(message: $"Invalid, missing or duplicate test assembly: {name}");
            }
        }
        if (selected.Count == 0) { throw new InvalidDataException(message: "No test assemblies selected."); }
        const string Results = "artifacts/test-results";

        if (Directory.Exists(path: Results)) { throw new IOException(message: $"Use a fresh test result directory: {Results}"); }
        Directory.CreateDirectory(path: Results);
        // Two assemblies at a time, largest first, so the longest suite runs alongside the others instead of after
        // them. Each run's output is printed whole when it finishes, so two runs never interleave. Every assembly runs
        // whatever another one did, so one CI run names every failing assembly instead of the first. The platform
        // exits nonzero for a run that discovered no test; a hardware-only assembly may still skip every case. Each run's
        // selection is TestWindowsArguments'.
        var console = new Lock();
        var failures = new List<string>();
        var ordered = selected.Order(comparer: StringComparer.Ordinal).Select(selector: (assembly, index) => (assembly, index)).OrderByDescending(keySelector: item => new FileInfo(fileName: item.assembly).Length);

        await Parallel.ForEachAsync(
            body: async (item, cancellationToken) => {
                var (assembly, index) = item;
                var run = await CliProcess.RunAsync(
                    arguments: TestWindowsArguments(assembly: assembly, report: $"{index:D3}-{Path.GetFileNameWithoutExtension(path: assembly)}.trx", results: Results),
                    cancellationToken: cancellationToken,
                    capture: true,
                    fileName: "dotnet",
                    workingDirectory: root
                );

                lock (console) {
                    Console.Write(value: run.Stdout);
                    Console.Error.Write(value: run.Stderr);
                    if (run.ExitCode != 0) { failures.Add(item: $"{Path.GetFileName(path: assembly)} exited with code {run.ExitCode}."); }
                }
            },
            parallelOptions: new ParallelOptions { MaxDegreeOfParallelism = 2 },
            source: ordered
        );
        if (failures.Count != 0) {
            failures.Sort(comparer: StringComparer.Ordinal);
            Console.Error.WriteLine(value: $"{failures.Count} of {selected.Count} compiled test assemblies failed:");
            foreach (var failure in failures) { Console.Error.WriteLine(value: $"  {failure}"); }
            return 1;
        }
        Console.WriteLine(value: $"Verified {selected.Count} compiled test assemblies without solution restore or workload installation.");
        return 0;
    }
    private static async Task<int> TestWorldAsync() {
        const string Results = "artifacts/world-test-results";
        var root = RepositoryPaths.RequireRoot();

        Directory.CreateDirectory(path: Results);
        foreach (var (project, testClass) in new[] {
            ("Puck.World.Azure.Tests", "EntraWorldAuthenticatorTests"),
            ("Puck.World.Schema.Tests", "WorldSiloDefinitionLawTests"),
            ("Puck.World.Silo.Tests", "WorldSiloLifecycleLawTests"),
        }) {
            var report = (project + ".trx");

            if (File.Exists(path: Path.Combine(path1: Results, path2: report))) { throw new IOException(message: $"Use a fresh test report: {report}"); }
            // The test assembly is portable: dotnet runs the Windows build's dll here without its apphost. A run in
            // which every selected test skipped exits nonzero, so a passing run executed the class.
            await CliProcess.RunCheckedAsync(
                arguments: [$"artifacts/world-tests/{project}/{project}.dll", .. CliTestRun.Class(fullName: $"{project}.{testClass}"), .. CliTestRun.SomeTestExecutes(), .. CliTestRun.Report(directory: Results, fileName: report)],
                fileName: "dotnet",
                workingDirectory: root
            );
        }
        return 0;
    }
    private static bool WithinRoot(string root, string path) => path.StartsWith(
        comparisonType: StringComparison.OrdinalIgnoreCase,
        value: (root + Path.DirectorySeparatorChar)
    );

    public static Command Create() {
        var capture = new Command(
            description: "Archive every compiled Release output with its source identity (Windows).",
            name: "capture"
        );
        var restore = new Command(
            description: "Extract this commit's archive into place without compiling.",
            name: "restore"
        );
        var testWindows = new Command(
            description: "Run the archived test assemblies through the producer's manifest.",
            name: "test-windows"
        );
        var testWorld = new Command(
            description: "Run the compiled world authentication and recovery tests (Linux).",
            name: "test-world"
        );
        var command = new Command(
            description: "Capture, restore, and test the compiled Release outputs of one build.",
            name: "artifacts"
        ) { capture, restore, testWindows, testWorld };

        capture.SetAction(action: (_, _) => CaptureAsync());
        restore.SetAction(action: (_, _) => RestoreAsync());
        testWindows.SetAction(action: (_, _) => TestWindowsAsync());
        testWorld.SetAction(action: (_, _) => TestWorldAsync());
        return command;
    }
}
