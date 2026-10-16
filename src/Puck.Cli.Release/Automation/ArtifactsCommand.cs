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
    /// (<see cref="CliTestRun.WithoutBuildTree"/>), writes its TRX report and its Cobertura coverage report beside it
    /// (<c>coverage</c> merges every run's), and dumps and ends a run that stops making progress.</summary>
    /// <param name="assembly">The test assembly.</param>
    /// <param name="results">The results directory, absolute.</param>
    /// <param name="report">The TRX report's file name in it; the coverage report takes its name with the
    /// <c>.cobertura.xml</c> extension.</param>
    /// <param name="settings">The coverage settings file, absolute (<c>build/CodeCoverage.config</c>).</param>
    /// <returns>The arguments.</returns>
    public static string[] TestWindowsArguments(string assembly, string results, string report, string settings) => [
        assembly,
        .. CliTestRun.CpuSelection,
        .. CliTestRun.WithoutBuildTree,
        .. CliTestRun.Report(directory: results, fileName: report),
        .. CliTestRun.Coverage(output: Path.Combine(path1: results, path2: Path.ChangeExtension(path: report, extension: ".cobertura.xml")), settings: settings),
        .. CliTestRun.HangDump(timeout: TimeSpan.FromMinutes(minutes: 15)),
    ];

    /// <summary>Splits the test assemblies into <paramref name="count"/> shards round-robin: in ordinal order, the
    /// assembly at index <c>i</c> goes to shard <c>i</c> modulo <paramref name="count"/>. The shards together hold every
    /// assembly exactly once, each in ordinal order, and the split depends only on the set of assemblies, so every
    /// runner computes the same one.</summary>
    /// <param name="assemblies">The test assemblies, as paths.</param>
    /// <param name="count">The number of shards.</param>
    /// <returns>The shards, in index order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than one.</exception>
    /// <exception cref="ArgumentException"><paramref name="assemblies"/> names one assembly twice.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> Partition(IReadOnlyList<string> assemblies, int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(other: 1, value: count);
        if (assemblies.Distinct(comparer: StringComparer.OrdinalIgnoreCase).Count() != assemblies.Count) { throw new ArgumentException(message: "An assembly is listed twice.", paramName: nameof(assemblies)); }
        var ordered = assemblies.Order(comparer: StringComparer.Ordinal).ToArray();
        var shards = Enumerable.Range(count: count, start: 0).Select(selector: _ => new List<string>()).ToArray();

        for (var index = 0; (index < ordered.Length); index++) {
            shards[(index % count)].Add(item: ordered[index]);
        }
        return shards;
    }
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
            destination: "artifacts/batteries/hgb",
            source: "src/Puck.HumbleGamingBrick.Post/bin/Release/net11.0"
        );
        CliFiles.CopyDirectory(
            destination: "artifacts/batteries/agb",
            source: "src/Puck.AdvancedGamingBrick.Post/bin/Release/net11.0"
        );
        CliFiles.CopyDirectory(
            destination: "artifacts/batteries/hgd",
            source: "src/Puck.HumbleGamingDeck.Post/bin/Release/net11.0"
        );
        foreach (var project in new[] { "Puck.World.Azure.Tests", "Puck.World.Schema.Tests", "Puck.World.Silo.Tests" }) {
            CliFiles.CopyDirectory(
                destination: $"artifacts/world-tests/{project}",
                source: $"tests/{project}/bin/Release/net11.0"
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
        var files = new[] { "src", "tests" }.SelectMany(selector: parent => Directory.EnumerateDirectories(path: Path.Combine(
            path1: root,
            path2: parent
        )).Order(comparer: StringComparer.Ordinal)).Select(selector: project => Path.Combine(
            path1: project,
            path2: "bin/Release"
        )).Where(predicate: Directory.Exists).SelectMany(selector: output => Directory.EnumerateFiles(
            path: output,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        ).Order(comparer: StringComparer.Ordinal)).ToArray();
        // Hashing reads every name in the outputs, most of them hard links to one another, so it runs on every core;
        // which file is kept and which recorded as a copy still follows the walk order alone.
        var hashes = new string[files.Length];

        Parallel.For(
            body: index => { hashes[index] = ContentPin.OfFile(path: files[index]).Hex; },
            fromInclusive: 0,
            toExclusive: files.Length
        );
        for (var index = 0; (index < files.Length); index++) {
            var file = files[index];
            var relative = RepositoryRelative(
                path: file,
                root: root
            );
            var hash = hashes[index];

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
    private static async Task<int> TestWindowsAsync(int shard, int shards) {
        if ((shards < 1) || (shard < 0) || (shard >= shards)) { throw new ArgumentOutOfRangeException(paramName: nameof(shard), message: $"Shard {shard} is not one of {shards} (zero-based)."); }
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
        // The manifest's ordinal order numbers the reports, so a report's name is the same whichever shard runs it.
        var manifest = selected.Order(comparer: StringComparer.Ordinal).ToArray();
        var assemblies = Partition(assemblies: manifest, count: shards)[shard];
        // Two assemblies at a time. Each run's output is printed whole when it finishes, so two runs never interleave.
        // Every assembly runs whatever another one did, so one CI run names every failing assembly instead of the first.
        // The platform exits nonzero for a run that discovered no test; a hardware-only assembly may still skip every
        // case. Each run's selection is TestWindowsArguments'.
        const int Concurrency = 2;
        var console = new Lock();
        var failures = new List<string>();

        Console.WriteLine(value: $"Shard {shard} of {shards} (zero-based): {assemblies.Count} of {manifest.Length} test assemblies, {Concurrency} at a time on {Environment.ProcessorCount} processors:");
        foreach (var name in assemblies.Select(selector: Path.GetFileNameWithoutExtension)) {
            Console.WriteLine(value: $"  {name}");
        }
        await Parallel.ForEachAsync(
            body: async (assembly, cancellationToken) => {
                var name = Path.GetFileNameWithoutExtension(path: assembly);
                // The results directory is passed whole: a test host resolves a relative one from where it stands, which
                // for some assemblies is their own output directory, and their reports would miss the upload.
                var run = await CliProcess.RunAsync(
                    arguments: TestWindowsArguments(assembly: assembly, report: $"{Array.IndexOf(array: manifest, value: assembly):D3}-{name}.trx", results: Path.Combine(path1: root, path2: Results), settings: Path.Combine(path1: root, path2: "build/CodeCoverage.config")),
                    cancellationToken: cancellationToken,
                    capture: true,
                    fileName: "dotnet",
                    workingDirectory: root
                );
                var coverage = Path.Combine(path1: root, path2: Results, path3: $"{Array.IndexOf(array: manifest, value: assembly):D3}-{name}.cobertura.xml");

                // The shard uploads its results; the compact form is what `coverage` needs, at a fraction of the size.
                if (File.Exists(path: coverage)) { CoverageReport.Compact(report: coverage); }
                lock (console) {
                    Console.Write(value: run.Stdout);
                    Console.Error.Write(value: run.Stderr);
                    if (run.ExitCode != 0) { failures.Add(item: $"{Path.GetFileName(path: assembly)} exited with code {run.ExitCode}."); }
                }
            },
            parallelOptions: new ParallelOptions { MaxDegreeOfParallelism = Concurrency },
            source: assemblies
        );
        if (failures.Count != 0) {
            failures.Sort(comparer: StringComparer.Ordinal);
            Console.Error.WriteLine(value: $"{failures.Count} of {assemblies.Count} compiled test assemblies failed:");
            foreach (var failure in failures) { Console.Error.WriteLine(value: $"  {failure}"); }
            return 1;
        }
        Console.WriteLine(value: $"Verified {assemblies.Count} compiled test assemblies without solution restore or workload installation.");
        return 0;
    }
    // A live project is a src/ project the solution builds; the verdict also refuses one that no run loaded.
    private static int Coverage(string results) {
        var root = RepositoryPaths.RequireRoot();
        var directory = Path.GetFullPath(path: Path.Combine(path1: root, path2: results));
        var reports = Directory.EnumerateFiles(path: directory, searchOption: SearchOption.AllDirectories, searchPattern: "*.*")
            .Where(predicate: static path => (path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".cobertura.xml") || path.EndsWith(comparisonType: StringComparison.Ordinal, value: CoverageReport.CompactExtension)))
            .Order(comparer: StringComparer.Ordinal)
            .ToArray();

        if (reports.Length == 0) { throw new InvalidDataException(message: $"No coverage reports under {results}."); }
        var gpuOnly = CoverageReport.ParseGpuOnly(text: File.ReadAllText(path: Path.Combine(path1: root, path2: CoverageReport.GpuOnlyList)));
        var projects = System.Xml.Linq.XDocument.Load(uri: Path.Combine(path1: root, path2: "Puck.slnx"))
            .Descendants(name: "Project")
            .Select(selector: static project => ((string?)project.Attribute(name: "Path") ?? ""))
            .Where(predicate: static path => path.StartsWith(comparisonType: StringComparison.Ordinal, value: "src/"))
            .Select(selector: static path => path[..(path.LastIndexOf(value: '/') + 1)]);
        var files = CoverageReport.Merge(reports: reports);

        Console.WriteLine(value: $"Merged {reports.Length} coverage reports.");
        using var report = new StringWriter();
        var complete = CoverageReport.Judge(files: files, gpuOnly: gpuOnly, projects: projects, writer: report);

        File.WriteAllText(path: Path.Combine(path1: directory, path2: "coverage.txt"), contents: report.ToString());
        Console.Write(value: report.ToString());
        return (complete ? 0 : 1);
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
        var shardOption = new Option<int>(name: "--shard") { DefaultValueFactory = static _ => 0, Description = "The zero-based shard of the manifest to run." };
        var shardsOption = new Option<int>(name: "--shards") { DefaultValueFactory = static _ => 1, Description = "The number of shards the manifest splits into, dealt round-robin in ordinal order." };
        var testWindows = new Command(
            description: "Run the archived test assemblies through the producer's manifest, or one shard of it.",
            name: "test-windows"
        ) { shardOption, shardsOption };
        var testWorld = new Command(
            description: "Run the compiled world authentication and recovery tests (Linux).",
            name: "test-world"
        );
        var resultsOption = new Option<string>(name: "--results") { DefaultValueFactory = static _ => "artifacts/test-results", Description = "The directory holding every shard's Cobertura reports." };
        var coverage = new Command(
            description: "Merge the test runs' coverage reports; fail unless every live line outside the GPU-only list is covered.",
            name: "coverage"
        ) { resultsOption };
        var command = new Command(
            description: "Capture, restore, and test the compiled Release outputs of one build.",
            name: "artifacts"
        ) { capture, restore, testWindows, testWorld, coverage };

        capture.SetAction(action: (_, _) => CaptureAsync());
        restore.SetAction(action: (_, _) => RestoreAsync());
        testWindows.SetAction(action: (parseResult, _) => TestWindowsAsync(
            shard: parseResult.GetValue(option: shardOption),
            shards: parseResult.GetValue(option: shardsOption)
        ));
        testWorld.SetAction(action: (_, _) => TestWorldAsync());
        coverage.SetAction(action: parseResult => Coverage(results: parseResult.GetValue(option: resultsOption)!));
        return command;
    }
}
