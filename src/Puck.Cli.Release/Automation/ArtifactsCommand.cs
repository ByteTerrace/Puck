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

    /// <summary>The committed table of each test assembly's measured run time in whole seconds, by assembly name, that
    /// <see cref="Partition"/> balances the shards by. <c>artifacts durations</c> records it from a run's shard
    /// reports.</summary>
    public const string DurationsTable = "TestDurations.json";
    /// <summary>The report of one <c>test-windows</c> run's measured durations, in the same shape as
    /// <see cref="DurationsTable"/>, written beside its TRX reports.</summary>
    public const string DurationsReport = "durations.json";

    /// <summary>Splits the test assemblies into <paramref name="count"/> shards of about equal recorded run time. Each
    /// assembly goes, longest first, to the shard with the least recorded time so far (the lowest index on a tie), so the
    /// shards together hold every assembly exactly once, and none ends more than the longest assembly's time past an even
    /// split. An assembly <paramref name="seconds"/> has no time for, such as a new test project, weighs the median
    /// recorded time. Each shard lists its assemblies longest first, the order it runs them in.</summary>
    /// <param name="assemblies">The test assemblies, as paths; each is weighed by its file name without extension.</param>
    /// <param name="seconds">The recorded run time of each assembly name, in seconds.</param>
    /// <param name="count">The number of shards.</param>
    /// <returns>The shards, in index order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than one.</exception>
    /// <exception cref="ArgumentException"><paramref name="assemblies"/> names one assembly twice.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> Partition(IReadOnlyList<string> assemblies, IReadOnlyDictionary<string, double> seconds, int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(other: 1, value: count);
        if (assemblies.Distinct(comparer: StringComparer.OrdinalIgnoreCase).Count() != assemblies.Count) { throw new ArgumentException(message: "An assembly is listed twice.", paramName: nameof(assemblies)); }
        var recorded = seconds.Values.Order().ToArray();
        var fallback = ((recorded.Length == 0) ? 1.0 : recorded[(recorded.Length / 2)]);
        var shards = Enumerable.Range(count: count, start: 0).Select(selector: _ => new List<string>()).ToArray();
        var totals = new double[count];

        foreach (var (assembly, weight) in assemblies
            .Select(selector: assembly => (assembly, weight: seconds.GetValueOrDefault(key: Path.GetFileNameWithoutExtension(path: assembly), defaultValue: fallback)))
            .OrderByDescending(keySelector: item => item.weight)
            .ThenBy(comparer: StringComparer.Ordinal, keySelector: item => item.assembly)) {
            var lightest = 0;

            for (var index = 1; (index < count); index++) {
                if (totals[index] < totals[lightest]) { lightest = index; }
            }
            shards[lightest].Add(item: assembly);
            totals[lightest] += weight;
        }
        return shards;
    }
    /// <summary>Reads a durations table or report: one whole or fractional number of seconds per assembly name.</summary>
    /// <param name="path">The table's path.</param>
    /// <returns>The seconds by assembly name.</returns>
    /// <exception cref="InvalidDataException">The file is not an object of nonnegative numbers.</exception>
    public static IReadOnlyDictionary<string, double> ReadDurations(string path) {
        var table = new Dictionary<string, double>(comparer: StringComparer.Ordinal);

        foreach (var (name, value) in ((CliFiles.ReadJson(path: path) as JsonObject) ?? throw new InvalidDataException(message: $"{path} is not a JSON object."))) {
            if ((value is null) || (value.GetValueKind() != System.Text.Json.JsonValueKind.Number) || !(value.GetValue<double>() >= 0)) {
                throw new InvalidDataException(message: $"{path}: {name} is not a nonnegative number of seconds.");
            }
            table.Add(key: name, value: value.GetValue<double>());
        }
        return table;
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
        var seconds = ReadDurations(path: Path.Combine(path1: root, path2: DurationsTable));
        var assemblies = Partition(assemblies: manifest, count: shards, seconds: seconds)[shard];
        // Two assemblies at a time, longest first, so the longest suite runs alongside the others instead of after
        // them. Each run's output is printed whole when it finishes, so two runs never interleave. Every assembly runs
        // whatever another one did, so one CI run names every failing assembly instead of the first. The platform
        // exits nonzero for a run that discovered no test; a hardware-only assembly may still skip every case. Each run's
        // selection is TestWindowsArguments'.
        const int Concurrency = 2;
        var console = new Lock();
        var failures = new List<string>();
        var measured = new JsonObject();

        Console.WriteLine(value: $"Shard {shard} of {shards} (zero-based): {assemblies.Count} of {manifest.Length} test assemblies, {Concurrency} at a time on {Environment.ProcessorCount} processors:");
        foreach (var name in assemblies.Select(selector: Path.GetFileNameWithoutExtension)) {
            Console.WriteLine(value: (seconds.TryGetValue(key: name!, value: out var recorded) ? $"  {name} ({recorded:0} s recorded)" : $"  {name} (no recorded time)"));
        }
        await Parallel.ForEachAsync(
            body: async (assembly, cancellationToken) => {
                var name = Path.GetFileNameWithoutExtension(path: assembly);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var run = await CliProcess.RunAsync(
                    arguments: TestWindowsArguments(assembly: assembly, report: $"{Array.IndexOf(array: manifest, value: assembly):D3}-{name}.trx", results: Results),
                    cancellationToken: cancellationToken,
                    capture: true,
                    fileName: "dotnet",
                    workingDirectory: root
                );

                lock (console) {
                    Console.Write(value: run.Stdout);
                    Console.Error.Write(value: run.Stderr);
                    measured[name] = Math.Round(digits: 1, value: clock.Elapsed.TotalSeconds);
                    if (run.ExitCode != 0) { failures.Add(item: $"{Path.GetFileName(path: assembly)} exited with code {run.ExitCode}."); }
                }
            },
            parallelOptions: new ParallelOptions { MaxDegreeOfParallelism = Concurrency },
            source: assemblies
        );
        CliFiles.WriteJson(
            path: Path.Combine(path1: Results, path2: DurationsReport),
            value: SortedByName(table: measured)
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
    // Every shard's durations report under the directory, one run's, becomes the table: each time rounded up to a whole
    // second, so a table records no sub-second noise, and an assembly two reports name is refused.
    private static int RecordDurations(string directory) {
        var root = RepositoryPaths.RequireRoot();
        var table = new JsonObject();

        foreach (var report in Directory.EnumerateFiles(path: directory, searchOption: SearchOption.AllDirectories, searchPattern: DurationsReport).Order(comparer: StringComparer.Ordinal)) {
            foreach (var (name, seconds) in ReadDurations(path: report)) {
                if (table.ContainsKey(propertyName: name)) { throw new InvalidDataException(message: $"Two durations reports under {directory} name {name}."); }
                table[name] = Math.Max(val1: 1, val2: ((int)Math.Ceiling(a: seconds)));
            }
        }
        if (table.Count == 0) { throw new InvalidDataException(message: $"No {DurationsReport} under {directory}."); }
        CliFiles.WriteJson(
            path: Path.Combine(path1: root, path2: DurationsTable),
            value: SortedByName(table: table)
        );
        Console.WriteLine(value: $"Recorded {table.Count} test assembly durations in {DurationsTable}.");
        return 0;
    }
    private static JsonObject SortedByName(JsonObject table) => new(properties: table.OrderBy(comparer: StringComparer.Ordinal, keySelector: entry => entry.Key).Select(selector: entry => KeyValuePair.Create(key: entry.Key, value: entry.Value?.DeepClone())));
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
        var shardsOption = new Option<int>(name: "--shards") { DefaultValueFactory = static _ => 1, Description = $"The number of shards the manifest splits into, balanced by {DurationsTable}." };
        var testWindows = new Command(
            description: "Run the archived test assemblies through the producer's manifest, or one shard of it.",
            name: "test-windows"
        ) { shardOption, shardsOption };
        var testWorld = new Command(
            description: "Run the compiled world authentication and recovery tests (Linux).",
            name: "test-world"
        );
        var reportsArgument = new Argument<string>(name: "directory") { Description = $"A directory holding every shard's {DurationsReport} from one test-windows run." };
        var durations = new Command(
            description: $"Record {DurationsTable} from one run's shard reports.",
            name: "durations"
        ) { reportsArgument };
        var command = new Command(
            description: "Capture, restore, and test the compiled Release outputs of one build.",
            name: "artifacts"
        ) { capture, restore, testWindows, testWorld, durations };

        capture.SetAction(action: (_, _) => CaptureAsync());
        restore.SetAction(action: (_, _) => RestoreAsync());
        testWindows.SetAction(action: (parseResult, _) => TestWindowsAsync(
            shard: parseResult.GetValue(option: shardOption),
            shards: parseResult.GetValue(option: shardsOption)
        ));
        testWorld.SetAction(action: (_, _) => TestWorldAsync());
        durations.SetAction(action: parseResult => RecordDurations(directory: parseResult.GetValue(argument: reportsArgument)!));
        return command;
    }
}
