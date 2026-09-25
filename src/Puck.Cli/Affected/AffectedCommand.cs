using System.CommandLine;
using Puck.Cli.Architecture;
using Puck.Cli.Canary;

namespace Puck.Cli.Affected;

/// <summary>
/// <c>puck affected</c> — names the test suites and canaries a change needs, and with <c>--run</c> runs exactly those:
/// the project graph chooses the suites, the recorded canary coverage and the manifests choose the canaries.
/// </summary>
internal static class AffectedCommand {
    /// <summary>The coverage index, repository-relative: each World source file with the canaries that executed it.</summary>
    public const string CoveragePath = "tests/Puck.Affected/canary-coverage.json";
    /// <summary>The shipped world tree, repository-relative, and the catalog its tree compile writes into the game's
    /// Release output.</summary>
    public const string ShippedTree = "src/Puck.World/Assets/worlds";
    /// <summary>The game's Release catalog of shipped worlds, repository-relative.</summary>
    public const string ShippedCatalog = "src/Puck.World/bin/Release/net10.0/Assets/worlds";

    // The projects whose code decides the bytes the tree compile writes: the world composer, the SDF baker, the shader
    // packager and the texture codecs. Everything they reference decides them too.
    private static readonly string[] CatalogSeeds = ["Puck.World.Transpiler", "Puck.SignedDistance", "Puck.Shaders", "Puck.Assets"];

    private const string Verb = "affected";

    private static string Relative(string repositoryRoot, string path) => Path.GetRelativePath(
        path: Path.GetFullPath(path: path),
        relativeTo: repositoryRoot
    ).Replace(
        newChar: '/',
        oldChar: '\\'
    );
    private static IReadOnlyList<string> Lines(string text) => [.. text.Split(separator: '\n')
        .Select(selector: static line => line.TrimEnd(trimChar: '\r'))
        .Where(predicate: static line => (line.Length > 0))];
    // Tracked files that differ from the base, staged or not, plus untracked files git does not ignore.
    private static bool TryReadChanged(string repositoryRoot, string since, out IReadOnlyList<string> changed, out string error) {
        changed = [];

        var diff = CliGit.Run(repositoryRoot, "diff", "--name-only", "--no-renames", since);

        if (diff.ExitCode != 0) {
            error = $"git diff against '{since}' failed: {diff.Stderr.Trim()}";

            return false;
        }

        var untracked = CliGit.Run(repositoryRoot, "ls-files", "--others", "--exclude-standard");

        if (untracked.ExitCode != 0) {
            error = $"git ls-files failed: {untracked.Stderr.Trim()}";

            return false;
        }

        changed = [.. Lines(text: diff.Stdout).Concat(second: Lines(text: untracked.Stdout)).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal)];
        error = string.Empty;

        return true;
    }
    // The projects whose own files name a path's containing directory, spelled by its first two segments (such as
    // `tests/Puck.World.Verdicts` or `worlds/parlor`), or by its top-level name for a file one level down.
    private static Func<string, IReadOnlyList<string>> ConsumerSearch(string repositoryRoot, IReadOnlyList<AffectedProject> projects) {
        var cache = new Dictionary<string, IReadOnlyList<string>>(comparer: StringComparer.Ordinal);

        return path => {
            var segments = path.Split(separator: '/');
            var key = ((segments.Length > 2)
                ? $"{segments[0]}/{segments[1]}"
                : segments[0]
            );

            if (cache.TryGetValue(key: key, value: out var known)) {
                return known;
            }

            var consumers = projects.Where(predicate: project => Directory.EnumerateFiles(
                path: Path.Combine(path1: repositoryRoot, path2: project.Directory),
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            ).Where(predicate: static file => ((file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".cs") ||
                file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".csproj") ||
                file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".targets")) &&
                !file.Contains(comparisonType: StringComparison.Ordinal, value: $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                !file.Contains(comparisonType: StringComparison.Ordinal, value: $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            ).Any(predicate: file => File.ReadAllText(path: file).Contains(comparisonType: StringComparison.Ordinal, value: key))).Select(selector: static project => project.Name).ToArray();

            cache[key] = consumers;

            return consumers;
        };
    }
    // The projects the seeds are built from, the seeds included.
    private static HashSet<string> Closure(ArchitectureModel model, IEnumerable<string> seeds) {
        var closure = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(collection: seeds);

        while (pending.TryPop(result: out var name)) {
            if (closure.Add(item: name) && model.Projects.TryGetValue(key: name, value: out var project)) {
                foreach (var reference in project.References) {
                    pending.Push(item: reference);
                }
            }
        }

        return closure;
    }

    /// <summary>Reads every canary as selection sees it: its directory, and the worlds, scripts and fixtures its legs
    /// name.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="canaries">The canaries, on success.</param>
    /// <param name="error">Why the manifests could not be read, or empty.</param>
    /// <returns>Whether the manifests were read.</returns>
    internal static bool TryCanaries(string repositoryRoot, out AffectedCanary[] canaries, out string error) {
        canaries = [];

        if (!CanaryManifestLoader.TryLoadAll(
            error: out error,
            manifests: out var manifests,
            refused: out _,
            repositoryRoot: repositoryRoot,
            strict: false
        )) {
            return false;
        }

        canaries = [.. manifests.Select(selector: manifest => new AffectedCanary(
            Directory: Relative(path: manifest.DirectoryPath, repositoryRoot: repositoryRoot),
            Files: [.. new[] { manifest.Positive, manifest.Discriminating }
                .SelectMany(selector: static leg => new[] { leg.WorldPath, leg.ScriptPath, leg.AuthorityWorldPath })
                .Concat(second: manifest.Fixtures)
                .OfType<string>()
                .Select(selector: path => Relative(path: path, repositoryRoot: repositoryRoot))],
            Id: manifest.Id,
            RequiresGpu: manifest.Requirements.Contains(value: "gpu", comparer: StringComparer.Ordinal)
        ))];

        return true;
    }

    // The kinds of file a canary's documents can reach: worlds, graph documents and other JSON fixtures, .puck
    // sources, and shader sources.
    private static readonly string[] ReachableExtensions = [".json", ".puck", ".hlsl", ".hlsli"];

    /// <summary>Returns every project in the repository's graph as selection sees it.</summary>
    /// <param name="model">The repository's project graph.</param>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The projects.</returns>
    internal static AffectedProject[] Projects(ArchitectureModel model, string repositoryRoot) => [.. model.Projects.Values.Select(selector: project => {
        var directory = Relative(
            path: Path.GetDirectoryName(path: project.File)!,
            repositoryRoot: repositoryRoot
        );

        return new AffectedProject(
            Directory: directory,
            IsSuite: directory.StartsWith(comparisonType: StringComparison.Ordinal, value: "tests/"),
            Name: project.Name,
            References: project.References
        );
    })];

    /// <summary>Plans what the working tree's changes against <paramref name="since"/> need.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="since">The base revision.</param>
    /// <param name="changed">The changed files the plan was made from.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="error">The refusal, or empty.</param>
    /// <returns><see langword="true"/> when the plan was made.</returns>
    public static bool TryPlan(string repositoryRoot, string since, out IReadOnlyList<string> changed, out AffectedPlan? plan, out string error) {
        plan = null;

        if (!TryReadChanged(changed: out changed, error: out error, repositoryRoot: repositoryRoot, since: since)) {
            return false;
        }

        var model = ArchitectureModel.Load(repositoryRoot: repositoryRoot);
        var projects = Projects(model: model, repositoryRoot: repositoryRoot);

        if (!TryCanaries(canaries: out var canaries, error: out error, repositoryRoot: repositoryRoot)) {
            return false;
        }

        var closure = Closure(model: model, seeds: ["Puck.World"]);
        var catalogProjects = Closure(model: model, seeds: CatalogSeeds);

        var coverage = AffectedCoverage.Read(repositoryRoot: repositoryRoot);
        // Reading every canary world is the cost of this map, so it is built only for a change a document can reach.
        var reachedBy = new Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>>(valueFactory: () => AffectedDocuments.ReachedBy(
            canaries: canaries,
            repositoryRoot: repositoryRoot
        ));
        IReadOnlySet<string> none = new HashSet<string>();

        plan = AffectedSelection.Select(
            canaries: canaries,
            changed: changed,
            consumersOf: ConsumerSearch(projects: projects, repositoryRoot: repositoryRoot),
            coverage: coverage,
            catalogInputs: (path, owner) => (path.StartsWith(comparisonType: StringComparison.Ordinal, value: (ShippedTree + "/")) ||
                (path.StartsWith(comparisonType: StringComparison.Ordinal, value: "src/Puck.World/Assets/") && (path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".hlsl") || path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".hlsli") || path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".graph.json"))) ||
                path.StartsWith(comparisonType: StringComparison.Ordinal, value: "src/Puck.Cli/Transpiler/") ||
                ((owner is not null) && catalogProjects.Contains(item: owner))),
            declaresTests: path => File.ReadLines(path: Path.Combine(path1: repositoryRoot, path2: path)).Any(predicate: static line => line.TrimStart().StartsWith(comparisonType: StringComparison.Ordinal, value: "test \"")),
            projects: projects,
            canariesReaching: path => ((ReachableExtensions.Any(predicate: extension => path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: extension)) && reachedBy.Value.TryGetValue(key: path, value: out var reaching))
                ? reaching
                : none),
            standInsFor: AffectedStandIns.Create(
                indexed: [.. coverage.Keys],
                projects: projects,
                repositoryRoot: repositoryRoot
            ),
            worldClosure: closure
        );

        return true;
    }

    // Builds the game, whose build writes the catalog, then checks the catalog against a fresh tree compile of the
    // sources build/WorldAssets.targets passes: every .puck and .world.json under the shipped tree.
    private static int CheckCatalog(string repositoryRoot) {
        var build = CliProcess.RunCaptured(
            arguments: ["build", "src/Puck.World/Puck.World.csproj", "-c", "Release", "-v", "q", "-nologo"],
            fileName: "dotnet",
            input: string.Empty,
            timeout: TimeSpan.FromMinutes(minutes: 30),
            workingDirectory: repositoryRoot
        );

        if (build.ExitCode != 0) {
            Console.Error.WriteLine(value: $"affected: Puck.World did not build, so its catalog cannot be checked:{Environment.NewLine}{build.Stdout}");

            return CliExit.Failed;
        }

        var tree = Path.Combine(path1: repositoryRoot, path2: ShippedTree);

        return PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            tree,
            "--output",
            Path.Combine(path1: repositoryRoot, path2: ShippedCatalog),
            "--check",
            .. Directory.EnumerateFiles(path: tree, searchOption: SearchOption.AllDirectories, searchPattern: "*")
                .Where(predicate: static file => (file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".puck") || file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".world.json")))
                .Order(comparer: StringComparer.Ordinal),
        ]);
    }
    private static int Execute(string repositoryRoot, AffectedPlan plan) {
        var failed = new List<string>();

        // dotnet test builds each suite and applies the settings its project binds (RunSettingsFilePath), so an
        // opt-in tier such as Maths' Deep and Exhaustive stays out exactly as it does in CI.
        foreach (var suite in plan.Suites) {
            var run = CliProcess.RunCaptured(
                arguments: ["test", Path.Combine(path1: repositoryRoot, path2: "tests", path3: suite, path4: $"{suite}.csproj"), "-c", "Release", "-v", "q", "-nologo"],
                fileName: "dotnet",
                input: string.Empty,
                timeout: TimeSpan.FromMinutes(minutes: 30),
                workingDirectory: repositoryRoot
            );
            var output = Lines(text: run.Stdout);
            var total = (output.LastOrDefault(predicate: static line => (line.TrimStart().StartsWith(comparisonType: StringComparison.Ordinal, value: "Passed!") || line.TrimStart().StartsWith(comparisonType: StringComparison.Ordinal, value: "Failed!")))?.Trim() ?? "no summary");

            Console.Out.WriteLine(value: $"affected: {suite} {((run.ExitCode == 0) ? "passed" : "FAILED")} — {total}");

            foreach (var line in output.Where(predicate: static line => (line.TrimStart().StartsWith(comparisonType: StringComparison.Ordinal, value: "Failed ") || line.Contains(comparisonType: StringComparison.Ordinal, value: " error ")))) {
                Console.Out.WriteLine(value: $"  {line.Trim()}");
            }

            if (run.ExitCode != 0) {
                failed.Add(item: suite);
            }
        }

        foreach (var world in plan.Worlds) {
            if (PuckRootCommand.Invoke(args: ["test", world]) != 0) {
                failed.Add(item: world);
            }
        }

        if (plan.Catalog && (CheckCatalog(repositoryRoot: repositoryRoot) != 0)) {
            failed.Add(item: "catalog");
        }

        if (plan.Canaries.Count > 0) {
            if (PuckRootCommand.Invoke(args: ["canary", .. plan.Canaries]) != 0) {
                failed.Add(item: "canaries");
            }
        }

        if (plan.Parity && (PuckRootCommand.Invoke(args: ["parity"]) != 0)) {
            failed.Add(item: "parity");
        }

        if (failed.Count > 0) {
            Console.Error.WriteLine(value: $"affected: failed: {string.Join(separator: ", ", values: failed)}");

            return CliExit.Failed;
        }

        return CliExit.Success;
    }
    private static int Run(string since, bool run, bool record) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refuse(verb: Verb, what: Environment.CurrentDirectory, why: "is not inside the Puck repository.");
        }

        if (record) {
            var scratch = Directory.CreateTempSubdirectory(prefix: "puck-affected-").FullName;

            return (AffectedCoverage.TryRecord(cli: typeof(AffectedCommand).Assembly.Location, error: out var recordError, repositoryRoot: repositoryRoot, scratch: scratch)
                ? CliExit.Success
                : CliExit.Refuse(verb: Verb, what: AffectedCommand.CoveragePath, why: recordError)
            );
        }

        if (!TryPlan(changed: out var changed, error: out var error, plan: out var plan, repositoryRoot: repositoryRoot, since: since)) {
            return CliExit.Refuse(verb: Verb, what: since, why: error);
        }

        Console.Out.WriteLine(value: $"affected: {changed.Count} changed file(s) against {since}.");

        if (plan!.Everything) {
            Console.Out.WriteLine(value: "affected: build infrastructure changed, which reaches every project.");
        }

        foreach (var suite in plan.Suites) {
            Console.Out.WriteLine(value: $"suite {suite}");
        }

        foreach (var world in plan.Worlds) {
            Console.Out.WriteLine(value: $"test {world}");
        }

        foreach (var canary in plan.Canaries) {
            Console.Out.WriteLine(value: $"canary {canary}");
        }

        if (plan.Catalog) {
            Console.Out.WriteLine(value: $"catalog {ShippedCatalog}");
        }

        if (plan.Parity) {
            Console.Out.WriteLine(value: "parity");
        }

        foreach (var path in plan.Unmapped) {
            Console.Out.WriteLine(value: $"unmapped {path}");
        }

        if (plan.Unmapped.Count > 0) {
            Console.Out.WriteLine(value: $"affected: {plan.Unmapped.Count} World source(s) are missing from {CoveragePath}, so no canary was chosen for them; record coverage with `puck affected --record` when the owner asks for a full run.");
        }

        return (run
            ? Execute(plan: plan, repositoryRoot: repositoryRoot)
            : CliExit.Success
        );
    }

    public static Command Create() {
        var sinceOption = new Option<string>(name: "--since") {
            DefaultValueFactory = static _ => "HEAD",
            Description = "The base revision the working tree is compared against (default: HEAD, so only uncommitted changes).",
        };
        var runOption = new Option<bool>(name: "--run") { Description = "Build and run the chosen suites, then puck test on the chosen worlds, then the chosen canaries and parity." };
        var recordOption = new Option<bool>(name: "--record") { Description = $"Record {CoveragePath}: build a World that records the methods it compiles, run the full canary set on it, and map each canary's methods to source files. A full run; do it when the owner asks for one." };
        var command = new Command(
            description: "Name the test suites and canaries a change needs, and with --run run exactly those.",
            name: Verb
        ) { sinceOption, runOption, recordOption };

        command.Detail(detail: $"""
              Changed files are the working tree against --since, staged or not, plus untracked files.
              A suite is chosen when its project, or any project it references (build-order-only
              references included), owns a changed file; a file in a directory no project owns chooses
              the projects whose sources name that directory. A canary is chosen when a changed file is
              one its manifest names, lies in its directory, or is a source the canary executed when
              coverage was last recorded ({CoveragePath}), or is a file the manifest's documents reach:
              the layers, neighbour worlds and graph documents a world names, and the pass shaders a
              graph document declares. Parity is chosen with any GPU canary. A file no
              canary can execute is placed through the indexed sources it stands for: a project file,
              restore lock or NativeMethods list through its project's sources, a shader source or
              include through the C# that names each kernel whose include closure reaches it, and a file
              puck schema writes through the sources declaring the types it is generated from. A changed
              World source neither the index nor a stand-in places is listed as unmapped rather than
              widening the run.
              Changing build infrastructure (build/, Directory.Build.*, global.json, Puck.slnx) chooses
              every suite. A changed .puck source that declares test blocks is run with puck test.
              Prose, .claude/, .github/, editors/ and experimental/ choose nothing.

              Exit codes: 0 planned or every chosen check passed, 1 a chosen check failed, 2 refused.
            """);
        command.SetAction(action: parseResult => Run(
            record: parseResult.GetValue(option: recordOption),
            run: parseResult.GetValue(option: runOption),
            since: parseResult.GetValue(option: sinceOption)!
        ));

        return command;
    }
}
