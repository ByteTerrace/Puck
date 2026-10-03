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

    /// <summary>Reads the working tree's changes against <paramref name="since"/>: tracked files that differ from it,
    /// staged or not, plus untracked files git does not ignore; and of those, the ones deleted since it.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="since">The base revision.</param>
    /// <param name="changed">Every changed path, repository-relative, in ordinal order.</param>
    /// <param name="deleted">The changed paths deleted since the base.</param>
    /// <param name="error">Why git could not answer, or empty.</param>
    /// <returns><see langword="true"/> when the changes were read.</returns>
    internal static bool TryReadChanged(string repositoryRoot, string since, out IReadOnlyList<string> changed, out IReadOnlySet<string> deleted, out string error) {
        changed = [];
        deleted = new HashSet<string>(comparer: StringComparer.Ordinal);

        var diff = CliGit.Run(repositoryRoot, "diff", "--name-only", "--no-renames", since);

        if (diff.ExitCode != 0) {
            error = $"git diff against '{since}' failed: {diff.Stderr.Trim()}";

            return false;
        }

        var removed = CliGit.Run(repositoryRoot, "diff", "--name-only", "--no-renames", "--diff-filter=D", since);

        if (removed.ExitCode != 0) {
            error = $"git diff against '{since}' failed: {removed.Stderr.Trim()}";

            return false;
        }

        var untracked = CliGit.Run(repositoryRoot, "ls-files", "--others", "--exclude-standard");

        if (untracked.ExitCode != 0) {
            error = $"git ls-files failed: {untracked.Stderr.Trim()}";

            return false;
        }

        changed = [.. Lines(text: diff.Stdout).Concat(second: Lines(text: untracked.Stdout)).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal)];
        deleted = Lines(text: removed.Stdout).ToHashSet(comparer: StringComparer.Ordinal);
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

        if (!TryReadChanged(changed: out changed, deleted: out var deleted, error: out error, repositoryRoot: repositoryRoot, since: since)) {
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
        var recorded = ((deleted.Count > 0)
            ? AffectedCoverage.ReadAt(repositoryRoot: repositoryRoot, revision: since)
            : null);
        // Reading every canary world is the cost of this map, so it is built only for a change a document can reach; the
        // base's map, over the tree the base recorded, only for a deleted file one can reach.
        var workingTree = new AffectedWorkingTree(root: repositoryRoot);
        using var baseTree = new AffectedRevisionTree(revision: since, root: repositoryRoot);
        // Each tree's kernels and post-process packages are read once, for both its documents' reach and its stand-ins.
        var workingShaders = new AffectedShaders(projects: projects, tree: workingTree);
        var baseShaders = new AffectedShaders(projects: projects, tree: baseTree);
        var reachedBy = new Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>>(valueFactory: () => AffectedDocuments.ReachedBy(
            canaries: canaries,
            packageFiles: workingShaders.FilesOf,
            tree: workingTree
        ));
        var recordedReachedBy = new Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>>(valueFactory: () => AffectedDocuments.ReachedBy(
            canaries: canaries,
            packageFiles: baseShaders.FilesOf,
            tree: baseTree
        ));
        IReadOnlySet<string> none = new HashSet<string>();

        bool Reachable(string path) => ReachableExtensions.Any(predicate: extension => path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: extension));

        // The paths the World build reads, the same roots its build key hashes: a build-infrastructure change outside
        // them leaves every World a canary boots unchanged. Walked only when such a change is present.
        var worldRoots = new Lazy<IReadOnlyList<string>>(valueFactory: () => WorldArtifactClosure.Walk(repositoryRoot: repositoryRoot).Roots);

        bool WorldInput(string path) => worldRoots.Value.Any(predicate: root => (string.Equals(a: path, b: root, comparisonType: StringComparison.Ordinal) || path.StartsWith(comparisonType: StringComparison.Ordinal, value: (root + "/"))));

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
            canariesReaching: path => ((Reachable(path: path) && reachedBy.Value.TryGetValue(key: path, value: out var reaching))
                ? reaching
                : none),
            standInsFor: AffectedStandIns.Create(
                documented: source => reachedBy.Value.ContainsKey(key: source),
                indexed: [.. coverage.Keys],
                projects: projects,
                shaders: workingShaders,
                tree: workingTree
            ),
            worldClosure: closure,
            worldInput: WorldInput,
            compiledUnchanged: AffectedCompiledWorlds.Unchanged(changed: changed, repositoryRoot: repositoryRoot, since: since).Contains,
            triviaOnly: path => AffectedCSharpTrivia.IsUnchanged(after: workingTree, before: baseTree, path: path),
            proseOnly: path => AffectedManifestProse.IsUnchanged(after: workingTree, before: baseTree, path: path),
            deleted: deleted,
            // A file deleted since the base is placed through the index the base recorded, which is the only one that
            // can still name it, or through the stand-ins the base's own tree gave it there.
            recorded: recorded,
            recordedStandInsFor: ((recorded is null)
                ? null
                : AffectedStandIns.Create(
                    documented: source => recordedReachedBy.Value.ContainsKey(key: source),
                    indexed: [.. recorded.Keys],
                    projects: projects,
                    shaders: baseShaders,
                    tree: baseTree
                )),
            // A deleted file is reached through the documents the base's tree held.
            recordedCanariesReaching: path => ((Reachable(path: path) && recordedReachedBy.Value.TryGetValue(key: path, value: out var reaching))
                ? reaching
                : none)
        );

        return true;
    }

    // The game's build writes the catalog from the sources build/WorldAssets.targets passes.
    private static int BuildCatalog(string repositoryRoot, string[] arguments) {
        var build = CliProcess.RunCaptured(
            arguments: arguments,
            fileName: "dotnet",
            input: string.Empty,
            timeout: TimeSpan.FromMinutes(minutes: 30),
            workingDirectory: repositoryRoot
        );

        if (build.ExitCode != 0) {
            Console.Error.WriteLine(value: $"affected: Puck.World did not build, so its catalog cannot be checked:{Environment.NewLine}{build.Stdout}");

            return CliExit.Failed;
        }

        return CliExit.Success;
    }
    private static string[] CatalogBuildArguments() => ["build", "--disable-build-servers", "src/Puck.World/Puck.World.csproj", "-c", "Release", CliOptions.NoNodeReuse, "-v", "q", "-nologo"];

    /// <summary>Returns the shipped catalog check's arguments, with repository-relative, forward-slashed paths.</summary>
    /// <returns>The arguments both the printed plan and the in-process check use from the repository root.</returns>
    public static string[] CatalogCheckArguments() => ["compile", "--tree", ShippedTree, "--output", ShippedCatalog, "--check"];
    /// <summary>The strict manifest check's shared printed and executed arguments.</summary>
    public static string[] CanaryCheckArguments(IReadOnlyList<string> ids) => ["canary", "--list", .. ids];
    /// <summary>Strictly loads and lists every prose-edited manifest through the root command, without a World.</summary>
    public static int CheckCanaries(IReadOnlyList<string> ids, RootCommand root) => ((ids.Count == 0) ? CliExit.Success :
        PuckRootCommand.Invoke(args: CanaryCheckArguments(ids: ids), root: root));
    /// <summary>Builds the shipped catalog and dispatches its check through the root command.</summary>
    /// <param name="build">Runs the prerequisite build with the supplied dotnet arguments.</param>
    /// <param name="root">The command tree that runs the catalog check.</param>
    /// <returns>The build's failure, or the check's exit code when the build succeeds.</returns>
    public static int CheckCatalog(Func<string[], int> build, RootCommand root) {
        var exit = build(CatalogBuildArguments());

        return ((exit == 0)
            ? PuckRootCommand.Invoke(args: CatalogCheckArguments(), root: root)
            : exit);
    }

    /// <summary>Resolves the base a plan compares against: <paramref name="since"/> as given, or with
    /// <paramref name="mergeBase"/> the merge base of <c>HEAD</c> and that revision, so commits the target gained after
    /// the branch left it never count as the branch's change.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="since">The base revision, or <see langword="null"/> for <c>HEAD</c>.</param>
    /// <param name="mergeBase">The revision to take the merge base with, or <see langword="null"/>.</param>
    /// <param name="resolved">The base to compare against.</param>
    /// <param name="error">Why no base could be resolved, or empty.</param>
    /// <returns><see langword="true"/> when a base was resolved.</returns>
    internal static bool TryResolveBase(string repositoryRoot, string? since, string? mergeBase, out string resolved, out string error) {
        resolved = (since ?? "HEAD");
        error = string.Empty;

        if (mergeBase is null) {
            return true;
        }

        if (since is not null) {
            error = "--since and --merge-base each name the base; pass one.";

            return false;
        }

        if (!CliGit.TryMergeBase(first: "HEAD", mergeBase: out resolved, repository: repositoryRoot, second: mergeBase)) {
            error = $"HEAD and '{mergeBase}' have no merge base, or '{mergeBase}' does not resolve.";

            return false;
        }

        return true;
    }

    // A plan line is repository-relative and runs from the root, so --run runs its in-process commands from the root
    // whatever directory the verb starts in.
    private static int Execute(string repositoryRoot, AffectedPlan plan, bool gpu) {
        var caller = Environment.CurrentDirectory;

        Environment.CurrentDirectory = repositoryRoot;

        try {
            return ExecuteAtRoot(gpu: gpu, plan: plan, repositoryRoot: repositoryRoot);
        } finally {
            Environment.CurrentDirectory = caller;
        }
    }

    /// <summary>The shared Release suite build, which leaves no MSBuild node behind (<see cref="CliOptions.NoNodeReuse"/>).</summary>
    public static string[] BuildArguments(string suite) => ["build", $"tests/{suite}/{suite}.csproj", "-c", "Release", CliOptions.NoNodeReuse, "-v", "q", "-nologo"];
    /// <summary>The shared Release suite run over the binaries a build already wrote. Microsoft.Testing.Platform hands
    /// every option it does not own to the test application, which refuses MSBuild switches, so the run builds nothing
    /// and takes none.</summary>
    public static string[] TestArguments(string suite) => ["test", "--project", $"tests/{suite}/{suite}.csproj", "-c", "Release", "--no-build"];

    /// <summary>The selection of a suite's CPU tests: every test whose class does not carry the <c>Gpu</c> trait.</summary>
    public static readonly string[] CpuSelection = ["--filter-not-trait", "Category=Gpu"];

    private static int ExecuteAtRoot(string repositoryRoot, AffectedPlan plan, bool gpu) {
        var failed = new List<string>();

        if (CheckCanaries(ids: plan.CanaryChecks, root: PuckRootCommand.Create(clock: TimeProvider.System)) != 0) {
            failed.Add(item: "canary manifests");
        }

        // Each suite builds first, leaving no MSBuild node behind (CliOptions.NoNodeReuse), and then runs its CPU tests
        // over the binaries that build wrote: Microsoft.Testing.Platform hands any option it does not own to the test
        // application, which refuses MSBuild switches. A plain run selects exactly what CI's does, so an explicit tier such
        // as Maths' Deep and Exhaustive stays out, and the Gpu trait keeps device laws out (CpuSelection). The capture's
        // post-exit drain bounds any process that inherited its pipes. The platform prints each failure with its message
        // and stack, and the run's summary counts after it.
        foreach (var suite in plan.Suites) {
            var build = CliProcess.RunCaptured(
                arguments: BuildArguments(suite: suite),
                fileName: "dotnet",
                input: string.Empty,
                timeout: TimeSpan.FromMinutes(minutes: 30),
                workingDirectory: repositoryRoot
            );

            if (build.ExitCode != 0) {
                Console.Out.WriteLine(value: $"affected: {suite} FAILED — the build exited {build.ExitCode}");
                foreach (var line in Lines(text: build.Stdout).Concat(second: Lines(text: build.Stderr))) {
                    Console.Out.WriteLine(value: $"  {line}");
                }

                failed.Add(item: suite);
                continue;
            }

            var run = CliProcess.RunCaptured(
                arguments: [.. TestArguments(suite: suite), .. CpuSelection],
                fileName: "dotnet",
                input: string.Empty,
                timeout: TimeSpan.FromMinutes(minutes: 30),
                workingDirectory: repositoryRoot
            );
            var output = Lines(text: run.Stdout);

            Console.Out.WriteLine(value: $"affected: {suite} {((run.ExitCode == 0) ? "passed" : "FAILED")} — {CliTestRun.Summary(output: output)}");

            // A failed suite's whole report follows its verdict line: every failure with its message and stack, or
            // the build errors that stopped it.
            if (run.ExitCode != 0) {
                foreach (var line in output.Concat(second: Lines(text: run.Stderr))) {
                    Console.Out.WriteLine(value: $"  {line}");
                }

                failed.Add(item: suite);
            }
        }

        foreach (var world in plan.Worlds) {
            if (PuckRootCommand.Invoke(args: ["test", world]) != 0) {
                failed.Add(item: world);
            }
        }

        if (plan.Catalog && (CheckCatalog(
            build: arguments => BuildCatalog(arguments: arguments, repositoryRoot: repositoryRoot),
            root: PuckRootCommand.Create(clock: TimeProvider.System)
        ) != 0)) {
            failed.Add(item: "catalog");
        }

        // The canaries boot real Worlds and parity holds both GPU backends, so they run only when asked for, one after
        // the other, after every CPU check.
        if (
            gpu &&
            (plan.Canaries.Count > 0) &&
            (PuckRootCommand.Invoke(args: ["canary", .. plan.Canaries]) != 0)
        ) {
            failed.Add(item: "canaries");
        }

        if (
            gpu &&
            plan.Parity &&
            (PuckRootCommand.Invoke(args: ["parity"]) != 0)
        ) {
            failed.Add(item: "parity");
        }

        if (failed.Count > 0) {
            Console.Error.WriteLine(value: $"affected: failed: {string.Join(separator: ", ", values: failed)}");

            return CliExit.Failed;
        }

        return CliExit.Success;
    }

    /// <summary>Writes a plan as the verb prints it: one line per chosen suite (<c>suite</c>), world (<c>test</c>, run with
    /// <c>puck test</c>) and canary (<c>canary</c>); the catalog, followed by the build and check <c>--run</c> makes of it; parity; then
    /// each unmapped and deleted source with the note that explains it.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="into">The writer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> or <paramref name="into"/> is <see langword="null"/>.</exception>
    public static void Describe(AffectedPlan plan, TextWriter into) {
        ArgumentNullException.ThrowIfNull(argument: plan);
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var suite in plan.Suites) {
            into.WriteLine(value: $"suite {suite}");
        }

        foreach (var world in plan.Worlds) {
            into.WriteLine(value: $"test {world}");
        }

        foreach (var canary in plan.Canaries) {
            into.WriteLine(value: $"canary {canary}");
        }

        foreach (var canary in plan.CanaryChecks) {
            into.WriteLine(value: $"canary-check {canary}");
        }
        if (plan.CanaryChecks.Count > 0) {
            into.WriteLine(value: $"puck {string.Join(separator: ' ', value: CanaryCheckArguments(ids: plan.CanaryChecks))}");
        }

        if (plan.Catalog) {
            into.WriteLine(value: $"catalog {ShippedCatalog}");
            into.WriteLine(value: $"dotnet {string.Join(separator: ' ', value: CatalogBuildArguments())}");
            into.WriteLine(value: $"puck {string.Join(separator: ' ', value: CatalogCheckArguments())}");
        }

        if (plan.Parity) {
            into.WriteLine(value: "parity");
        }

        foreach (var baseline in plan.Baselines) {
            into.WriteLine(value: $"baseline {baseline.Name}");
            into.WriteLine(value: $"puck {string.Join(separator: ' ', value: baseline.CheckArguments())}");
        }

        foreach (var path in plan.Unmapped) {
            into.WriteLine(value: $"unmapped {path}");
        }

        if (plan.Unmapped.Count > 0) {
            into.WriteLine(value: $"affected: {plan.Unmapped.Count} World source(s) are missing from {CoveragePath}, so no canary was chosen for them; record coverage with `puck affected --record` when the owner asks for a full run.");
        }

        foreach (var path in plan.Deleted) {
            into.WriteLine(value: $"deleted {path}");
        }

        if (plan.Deleted.Count > 0) {
            into.WriteLine(value: $"affected: {plan.Deleted.Count} deleted World source(s) are placed by neither {CoveragePath} nor the index the base recorded, directly or through the stand-ins the base's tree gave them, so no canary was chosen for them; a recording cannot place a file that no longer exists, and their projects' suites still run.");
        }
    }

    private static int Run(string? since, string? mergeBase, bool run, bool gpu, bool record) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refuse(verb: Verb, what: Environment.CurrentDirectory, why: "is not inside the Puck repository.");
        }

        if (
            gpu &&
            !run
        ) {
            return CliExit.Refuse(verb: Verb, what: "--gpu", why: "adds the canaries and parity to what --run runs, so it needs --run.");
        }

        if (record) {
            using var scratch = RunDirectory.Create(prefix: "puck-affected-");
            var exit = Record(repositoryRoot, typeof(AffectedCommand).Assembly.Location, scratch.Path);
            // A failed build or inner canary run keeps the recording World and transcript; coverage is unchanged.
            scratch.Conclude(passed: (exit == CliExit.Success));
            return exit;
        }

        if (!TryResolveBase(error: out var baseError, mergeBase: mergeBase, repositoryRoot: repositoryRoot, resolved: out var resolved, since: since)) {
            return CliExit.Refuse(verb: Verb, what: (mergeBase ?? (since ?? "HEAD")), why: baseError);
        }

        if (!TryPlan(changed: out var changed, error: out var error, plan: out var plan, repositoryRoot: repositoryRoot, since: resolved)) {
            return CliExit.Refuse(verb: Verb, what: resolved, why: error);
        }

        Console.Out.WriteLine(value: ((mergeBase is null)
            ? $"affected: {changed.Count} changed file(s) against {resolved}."
            : $"affected: {changed.Count} changed file(s) against {resolved[..12]}, the merge base of HEAD and {mergeBase}."));

        if (plan!.Everything) {
            Console.Out.WriteLine(value: "affected: build infrastructure changed, which reaches every suite.");
        }

        Describe(
            into: Console.Out,
            plan: plan
        );

        return (run
            ? Execute(gpu: gpu, plan: plan, repositoryRoot: repositoryRoot)
            : CliExit.Success
        );
    }

    /// <summary>Refreshes coverage only after the recording build and inner canary run succeed.</summary>
    internal static int Record(string repositoryRoot, string cli, string scratch, Func<IReadOnlyList<string>, TimeSpan, CliProcessResult>? execute = null) =>
        (AffectedCoverage.TryRecord(canaryExit: out _, cli: cli, error: out var error, execute: execute, repositoryRoot: repositoryRoot, scratch: scratch)
            ? CliExit.Success
            : CliExit.Refuse(verb: Verb, what: CoveragePath, why: error));
    /// <summary>Creates <c>--merge-base</c>, the revision whose merge base with <c>HEAD</c> a change is read against;
    /// <c>puck affected</c> and <c>puck gate</c> share it.</summary>
    /// <param name="description">What the verb compares against the merge base.</param>
    /// <returns>The option.</returns>
    internal static Option<string> MergeBase(string description) => new(name: "--merge-base") { Description = description };
    /// <summary>Creates <c>--gpu</c>, which adds the chosen canaries and parity to a run; <c>puck affected</c> and
    /// <c>puck gate</c> share it.</summary>
    /// <returns>The option.</returns>
    internal static Option<bool> Gpu() => new(name: "--gpu") { Description = "Also run the chosen canaries and then parity, one after the other, after the CPU checks: real-World and GPU work, so run it on a machine with no competing build or GPU load." };

    public static Command Create() {
        var sinceOption = new Option<string>(name: "--since") { Description = "The base revision the working tree is compared against (default: HEAD, so only uncommitted changes)." };
        var mergeBaseOption = MergeBase(description: "Compare the working tree against the merge base of HEAD and this revision, so what the target gained after the branch left it is not the branch's change. Excludes --since.");
        var runOption = new Option<bool>(name: "--run") { Description = "Build and run the chosen suites, then puck test on the chosen worlds, then the catalog check." };
        var gpuOption = Gpu();
        var recordOption = new Option<bool>(name: "--record") { Description = $"Record {CoveragePath}: build a World that records the methods it compiles, run the full canary set on it, and map each canary's methods to source files. A full run; do it when the owner asks for one." };
        var command = new Command(
            description: "Name the test suites and canaries a change needs, and with --run run exactly those.",
            name: Verb
        ) { sinceOption, mergeBaseOption, runOption, gpuOption, recordOption };

        command.Detail(detail: $"""
              Changed files are the working tree against --since (or against the merge base of HEAD
              and --merge-base), staged or not, plus untracked files.
              A suite is chosen when its project, or any project it references (build-order-only
              references included), owns a changed file; a file in a directory no project owns chooses
              the projects whose sources name that directory. A canary is chosen when a changed file is
              one its manifest names, lies in its directory, or is a source the canary executed when
              coverage was last recorded ({CoveragePath}), or is a file the manifest's documents reach:
              the layers, neighbour worlds and graph documents a world names, and the pass shaders a
              graph document declares with their includes. Parity is chosen with any GPU canary. A file no
              canary can execute is placed through the indexed sources it stands for: a project file or
              NativeMethods list through its project's sources, a shader source or
              include through the C# that names each kernel whose include closure reaches it, in the
              kernel's project or one its build references, a post-process package's stage sources and
              its frame interface through the canaries whose worlds name the package in views.post, or
              the C# that draws post-process packages when none does, and a file puck schema writes through
              the sources declaring the types it is generated from. A changed
              World source neither the index nor a stand-in places is listed as unmapped rather than
              widening the run. A file deleted since --since is placed by the index the base recorded,
              directly or through the stand-ins the base's tree gave it, or by the canaries whose
              documents reached it in the base's tree; one none of these places is listed as deleted,
              never unmapped. A restore lock reaches its own project's suite alone, and every canary
              only when its project is one the World is built from. Build infrastructure (build/,
              Directory.Build.*, Directory.Packages.props, global.json, Puck.slnx, NuGet.config)
              reaches every suite, and every canary only when the file is an input of the World build.
              A changed .puck or .world.json under {ShippedTree} reaches no canary when its stem's
              document compiles to the same value at the base and in the working tree: object member
              order and number spelling do not matter; array order does. Both paths of a JSON-to-source
              replacement are judged. Its owner's suites, catalog check and changed test blocks still
              run, and the path is neither unmapped nor unplaced deleted. Libraries, compositions,
              missing documents and failed compilations keep ordinary selection.
              A C# edit with equivalent syntax after stripping trivia reaches no suite or canary.
              Comments, XML documentation, whitespace and regions are ignored; other directive tokens
              must match. Files with conditional directives, parse errors, additions and deletions are
              not judged. A manifest edit confined to root title and binding selects a canary-check
              line and puck canary --list <id...>, which --run strictly loads without booting a World.
              Gate repository checks remain: format, lengths, comment-smells and docs links, with
              docs citations in the GPU gate.
              Changing build infrastructure (build/, Directory.Build.*, global.json, Puck.slnx) chooses
              every suite. A changed .puck source that declares test blocks is run with puck test, and
              prints as a test line. A catalog line names the game's Release catalog, followed by the
              dotnet build and puck compile --check commands --run uses, runnable from the repository root;
              it holds no test worlds. Prose, .claude/, .github/, editors/ and experimental/ choose nothing.

              A baseline is chosen when its owning test project is reached or a changed or deleted
              file matches its declared data inputs. Each baseline line names its artifact and is
              followed by the exact puck baselines <artifact> --check command the gate runs.

              --run runs manifest checks, the suites, the worlds and the catalog check; --run --gpu then runs the chosen
              canaries and parity, one after the other. Baseline checks run only through puck gate.

              Exit codes: 0 planned or every chosen check passed, 1 a chosen check failed, 2 refused.
            """);
        command.SetAction(action: parseResult => Run(
            gpu: parseResult.GetValue(option: gpuOption),
            mergeBase: parseResult.GetValue(option: mergeBaseOption),
            record: parseResult.GetValue(option: recordOption),
            run: parseResult.GetValue(option: runOption),
            since: parseResult.GetValue(option: sinceOption)
        ));

        return command;
    }
}
