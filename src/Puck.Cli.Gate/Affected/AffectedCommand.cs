using System.CommandLine;
using Puck.Cli.Architecture;
using Puck.Cli.Canary;
using Puck.Cli.Host;

namespace Puck.Cli.Affected;

/// <summary>
/// <c>puck affected</c> — names the test suites and canaries a change needs, and with <c>--run</c> runs exactly those:
/// the project graph chooses the suites, and the manifests, the documents they reach and the World's own sources choose the canaries.
/// </summary>
public static class AffectedCommand {
    /// <summary>The tree of the repository's world asset packages, repository-relative.</summary>
    public const string ShippedTree = "worlds";

    private const string Verb = "affected";

    private static string Relative(string repositoryRoot, string path) => Path.GetRelativePath(
        path: Path.GetFullPath(path: path),
        relativeTo: repositoryRoot
    ).Replace(
        newChar: '/',
        oldChar: '\\'
    );

    /// <summary>Reads the working tree's changes against <paramref name="since"/>: tracked files that differ from it,
    /// staged or not, plus untracked files git does not ignore; and of those, the ones deleted since it.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="since">The base revision.</param>
    /// <param name="changed">Every changed path, repository-relative, in ordinal order.</param>
    /// <param name="deleted">The changed paths deleted since the base.</param>
    /// <param name="error">Why git could not answer, or empty.</param>
    /// <returns><see langword="true"/> when the changes were read.</returns>
    public static bool TryReadChanged(string repositoryRoot, string since, out IReadOnlyList<string> changed, out IReadOnlySet<string> deleted, out string error) {
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

        changed = [.. CliText.Lines(text: diff.Stdout).Concat(second: CliText.Lines(text: untracked.Stdout)).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal)];
        deleted = CliText.Lines(text: removed.Stdout).ToHashSet(comparer: StringComparer.Ordinal);
        error = string.Empty;

        return true;
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
    public static bool TryCanaries(string repositoryRoot, out AffectedCanary[] canaries, out string error) {
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
    public static AffectedProject[] Projects(ArchitectureModel model, string repositoryRoot) => [.. model.Projects.Values.Select(selector: project => {
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

        // Reading every canary world is the cost of this map, so it is built only for a change a document can reach; the
        // base's map, over the tree the base recorded, only for a deleted file one can reach.
        var workingTree = new AffectedWorkingTree(root: repositoryRoot);
        using var baseTree = new AffectedRevisionTree(documentTrees: AffectedRevisionExport.DocumentTrees, revision: since, root: repositoryRoot);
        // Each tree's kernels and post-process packages are read once, for its documents' reach.
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

        // Reading the base revision's documents exports its files (AffectedRevisionExport), which a stalled or failing
        // git refuses by name.
        try {
            plan = AffectedSelection.Select(
                canaries: canaries,
                changed: changed,
                consumersOf: AffectedConsumers.Search(projects: projects, repositoryRoot: repositoryRoot),
                linkedBy: AffectedConsumers.Linking(projects: projects, repositoryRoot: repositoryRoot),
                declaresTests: path => File.ReadLines(path: Path.Combine(path1: repositoryRoot, path2: path)).Any(predicate: static line => line.TrimStart().StartsWith(comparisonType: StringComparison.Ordinal, value: "test \"")),
                projects: projects,
                canariesReaching: path => ((Reachable(path: path) && reachedBy.Value.TryGetValue(key: path, value: out var reaching))
                    ? reaching
                    : none),
                worldClosure: closure,
                worldInput: WorldInput,
                compiledUnchanged: AffectedCompiledWorlds.Unchanged(changed: changed, repositoryRoot: repositoryRoot, since: since).Contains,
                triviaOnly: path => AffectedCSharpTrivia.IsUnchanged(after: workingTree, before: baseTree, path: path),
                proseOnly: path => AffectedManifestProse.IsUnchanged(after: workingTree, before: baseTree, path: path),
                deleted: deleted,
                // A deleted file is reached through the documents the base's tree held.
                recordedCanariesReaching: path => ((Reachable(path: path) && recordedReachedBy.Value.TryGetValue(key: path, value: out var reaching))
                    ? reaching
                    : none)
            );
        } catch (AffectedRevisionExportRefusedException exception) {
            error = exception.Message;

            return false;
        }

        return true;
    }

    /// <summary>The documentation check's printed and executed arguments: the whole corpus, since a change to code can
    /// break a citation in any page.</summary>
    public static readonly string[] DocsLinksArguments = ["docs", "links"];

    /// <summary>The strict manifest check's shared printed and executed arguments.</summary>
    public static string[] CanaryCheckArguments(IReadOnlyList<string> ids) => ["canary", "--list", .. ids];
    /// <summary>Strictly loads and lists every prose-edited manifest through the root command, without a World.</summary>
    public static int CheckCanaries(IReadOnlyList<string> ids, RootCommand root) => ((ids.Count == 0) ? CliExit.Success :
        CliRoot.Invoke(args: CanaryCheckArguments(ids: ids), root: root));
    /// <summary>Resolves the base a plan compares against: <paramref name="since"/> as given, or with
    /// <paramref name="mergeBase"/> the merge base of <c>HEAD</c> and that revision, so commits the target gained after
    /// the branch left it never count as the branch's change.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="since">The base revision, or <see langword="null"/> for <c>HEAD</c>.</param>
    /// <param name="mergeBase">The revision to take the merge base with, or <see langword="null"/>.</param>
    /// <param name="resolved">The base to compare against.</param>
    /// <param name="error">Why no base could be resolved, or empty.</param>
    /// <returns><see langword="true"/> when a base was resolved.</returns>
    public static bool TryResolveBase(string repositoryRoot, string? since, string? mergeBase, out string resolved, out string error) {
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
    private static int Execute(string repositoryRoot, AffectedPlan plan, bool gpu, int gpuJobs, int suiteJobs, RootCommand root) {
        var caller = Environment.CurrentDirectory;

        Environment.CurrentDirectory = repositoryRoot;

        try {
            return ExecuteAtRoot(gpu: gpu, gpuJobs: gpuJobs, plan: plan, repositoryRoot: repositoryRoot, root: root, suiteJobs: suiteJobs);
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

    // The caller holds CliLease for the heavy suite throughout this admission and execution. Sampling also
    // accounts for unleased heavy processes and memory/disk headroom; it is not mutual exclusion itself.
    internal static bool AdmitHeavySuite(string repositoryRoot, string suite) {
        return HostAdmission.Wait(cancellationToken: CancellationToken.None, clock: TimeProvider.System, device: false, error: Console.Error, heavySuite: true, probe: new HostProbe(checkoutRoot: repositoryRoot), step: suite);
    }

    // Every other verb a run takes runs through the root that invoked this one.
    private static int ExecuteAtRoot(string repositoryRoot, AffectedPlan plan, bool gpu, int gpuJobs, int suiteJobs, RootCommand root) {
        var failed = new List<string>();

        if (CheckCanaries(ids: plan.CanaryChecks, root: root) != 0) {
            failed.Add(item: "canary manifests");
        }

        if (plan.DocsLinks && (CliRoot.Invoke(args: DocsLinksArguments, root: root) != 0)) {
            failed.Add(item: "docs links");
        }

        // The suites build once and run their CPU tests side by side (AffectedSuites).
        failed.AddRange(collection: AffectedSuites.Run(
            jobs: suiteJobs,
            repositoryRoot: repositoryRoot,
            suites: plan.Suites
        ));

        foreach (var world in plan.Worlds) {
            if (CliRoot.Invoke(args: ["test", world], root: root) != 0) {
                failed.Add(item: world);
            }
        }

        // The canaries boot real Worlds, so they run only when asked for, after every CPU check, their legs side by side
        // up to --gpu-jobs on the GPU. Parity takes the parity world it compares, and no live world is one, so a run
        // that chooses parity names it without running it.
        if (
            gpu &&
            (plan.Canaries.Count > 0) &&
            (CliRoot.Invoke(args: ["canary", "--gpu-jobs", gpuJobs.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), .. plan.Canaries], root: root) != 0)
        ) {
            failed.Add(item: "canaries");
        }

        if (gpu && plan.Parity) {
            Console.Error.WriteLine(value: "affected: parity is chosen but not run: no live world is a parity world; run `puck parity --world <file>` against one.");
        }

        if (failed.Count > 0) {
            Console.Error.WriteLine(value: $"affected: failed: {string.Join(separator: ", ", values: failed)}");

            return CliExit.Failed;
        }

        return CliExit.Success;
    }

    /// <summary>Writes a plan as the verb prints it: one line per chosen suite (<c>suite</c>), world (<c>test</c>, run with
    /// <c>puck test</c>) and canary (<c>canary</c>), then the manifest and documentation checks, parity and baselines.</summary>
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

        if (plan.DocsLinks) {
            into.WriteLine(value: "docs-links");
            into.WriteLine(value: $"puck {string.Join(separator: ' ', value: DocsLinksArguments)}");
        }

        if (plan.Parity) {
            into.WriteLine(value: "parity");
        }

        foreach (var baseline in plan.Baselines) {
            into.WriteLine(value: $"baseline {baseline.Name}");
            into.WriteLine(value: $"puck {string.Join(separator: ' ', value: baseline.CheckArguments())}");
        }

    }

    /// <summary>Writes a plan as <c>puck affected --json</c> does: one record of what was compared, then one per chosen item
    /// in <see cref="Describe"/>'s order, each its <c>kind</c> (suite, test, canary, canary-check, docs-links, parity,
    /// baseline), its <c>name</c> and, where the gate runs a command for it, that <c>command</c>.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="changed">The changed files the plan was read from.</param>
    /// <param name="against">The revision the working tree was compared against.</param>
    /// <param name="output">The JSON-lines output.</param>
    public static void WriteRecords(AffectedPlan plan, int changed, string against, CliJsonLines output) {
        static System.Text.Json.Nodes.JsonObject Item(string kind, string? name, IEnumerable<string>? command = null) => new() {
            ["kind"] = kind,
            ["name"] = name,
            ["command"] = ((command is null) ? null : $"puck {string.Join(separator: ' ', values: command)}"),
        };

        output.Write(record: new System.Text.Json.Nodes.JsonObject { ["changed"] = changed, ["against"] = against, ["everything"] = plan.Everything });
        foreach (var suite in plan.Suites) { output.Write(record: Item(kind: "suite", name: suite)); }
        foreach (var world in plan.Worlds) { output.Write(record: Item(kind: "test", name: world, command: ["test", world])); }
        foreach (var canary in plan.Canaries) { output.Write(record: Item(kind: "canary", name: canary)); }
        foreach (var canary in plan.CanaryChecks) { output.Write(record: Item(kind: "canary-check", name: canary, command: CanaryCheckArguments(ids: plan.CanaryChecks))); }
        if (plan.DocsLinks) { output.Write(record: Item(kind: "docs-links", name: null, command: DocsLinksArguments)); }
        if (plan.Parity) { output.Write(record: Item(kind: "parity", name: null)); }
        foreach (var baseline in plan.Baselines) { output.Write(record: Item(kind: "baseline", name: baseline.Name, command: baseline.CheckArguments())); }
    }
    private static int Run(string? since, string? mergeBase, bool run, bool gpu, int gpuJobs, int suiteJobs, bool json, RootCommand root) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refuse(verb: Verb, what: Environment.CurrentDirectory, why: "is not inside the Puck repository.");
        }

        if (
            gpu &&
            !run
        ) {
            return CliExit.Refuse(verb: Verb, what: "--gpu", why: "adds the canaries and parity to what --run runs, so it needs --run.");
        }


        if (!TryResolveBase(error: out var baseError, mergeBase: mergeBase, repositoryRoot: repositoryRoot, resolved: out var resolved, since: since)) {
            return CliExit.Refuse(verb: Verb, what: (mergeBase ?? (since ?? "HEAD")), why: baseError);
        }

        if (!TryPlan(changed: out var changed, error: out var error, plan: out var plan, repositoryRoot: repositoryRoot, since: resolved)) {
            return CliExit.Refuse(verb: Verb, what: resolved, why: error);
        }

        using var records = CliJsonLines.Open(json: json);

        Console.Out.WriteLine(value: ((mergeBase is null)
            ? $"affected: {changed.Count} changed file(s) against {resolved}."
            : $"affected: {changed.Count} changed file(s) against {resolved[..12]}, the merge base of HEAD and {mergeBase}."));

        if (plan!.Everything) {
            Console.Out.WriteLine(value: "affected: build infrastructure changed, which reaches every suite.");
        }

        if (records is null) {
            Describe(
                into: Console.Out,
                plan: plan
            );
        } else {
            WriteRecords(against: resolved, changed: changed.Count, output: records, plan: plan);
        }

        return (run
            ? Execute(gpu: gpu, gpuJobs: gpuJobs, plan: plan, repositoryRoot: repositoryRoot, root: root, suiteJobs: suiteJobs)
            : CliExit.Success
        );
    }

    /// <summary>Creates <c>--merge-base</c>, the revision whose merge base with <c>HEAD</c> a change is read against;
    /// <c>puck affected</c> and <c>puck gate</c> share it.</summary>
    /// <param name="description">What the verb compares against the merge base.</param>
    /// <returns>The option.</returns>
    internal static Option<string> MergeBase(string description) => new(name: "--merge-base") { Description = description };
    /// <summary>Creates <c>--gpu</c>, which adds the chosen canaries to a run; <c>puck affected</c> and
    /// <c>puck gate</c> share it.</summary>
    /// <returns>The option.</returns>
    internal static Option<bool> Gpu() => new(name: "--gpu") { Description = "Also run the chosen canaries, up to --gpu-jobs legs on the GPU at once, after the CPU checks: real-World and GPU work, so run it on a machine with no competing build or GPU load." };

    /// <summary>Creates <c>puck affected</c>.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var sinceOption = new Option<string>(name: "--since") { Description = "The base revision the working tree is compared against (default: HEAD, so only uncommitted changes)." };
        var mergeBaseOption = MergeBase(description: "Compare the working tree against the merge base of HEAD and this revision, so what the target gained after the branch left it is not the branch's change. Excludes --since.");
        var runOption = new Option<bool>(name: "--run") { Description = "Build the chosen suites once and run their CPU tests side by side, then puck test on the chosen worlds." };
        var gpuOption = Gpu();
        var gpuJobsOption = CanaryGpuJobs.Create();
        var suiteJobsOption = AffectedSuites.Jobs();
        var jsonOption = CliOptions.Json(description: "Write the plan as one JSON object per line: what was compared (changed, against, everything), then each chosen item's kind, name and gate command; every text line, a run's included, goes to standard error.");
        var command = new Command(
            description: "Name the test suites and canaries a change needs, and with --run run exactly those.",
            name: Verb
        ) { sinceOption, mergeBaseOption, runOption, gpuOption, gpuJobsOption, suiteJobsOption, jsonOption };

        CliOptions.Exclusive(command, sinceOption, mergeBaseOption);

        command.Detail(detail: $"""
              Changed files are the working tree against --since (or against the merge base of HEAD
              and --merge-base), staged or not, plus untracked files.
              A suite is chosen when its project, or any project it references (build-order-only
              references included), owns a changed file; a file in a directory no project owns chooses
              the projects whose sources name that directory. A canary is chosen when a changed file is
              one its manifest names, lies in its directory, or is a file the manifest's documents reach:
              the layers, neighbour worlds and graph documents a world names, and the pass shaders a
              graph document declares with their includes. A file deleted since --since is reached by
              the canaries whose documents named it in the base's tree. Every canary is chosen when a
              changed file belongs to a project the World executable is built from, since each canary
              boots that build. Parity is chosen with any GPU canary.
              A restore lock reaches its own project's suite alone, and every canary
              only when its project is one the World is built from. Build infrastructure (build/,
              Directory.Build.*, Directory.Packages.props, global.json, Puck.slnx, NuGet.config)
              reaches every suite, and every canary only when the file is an input of the World build.
              A changed .puck or .world.json under {ShippedTree} reaches no canary when its stem's
              document compiles to the same value at the base and in the working tree: object member
              order and number spelling do not matter; array order does. Both paths of a JSON-to-source
              replacement are judged. Its owner's suites and changed test blocks still
              run. Libraries, compositions,
              missing documents and failed compilations keep ordinary selection.
              A C# edit with equivalent syntax after stripping trivia reaches no suite or canary.
              Comments, XML documentation, whitespace and regions are ignored; other directive tokens
              must match. Files with conditional directives, parse errors, additions and deletions are
              not judged. A manifest edit confined to root title and binding selects a canary-check
              line and puck canary --list <id...>, which --run strictly loads without booting a World.
              A changed page of the documentation corpus, a non-trivia C# change under src/ or tests/,
              or any deleted file selects a docs-links line and puck docs links, which --run runs over
              the whole corpus. Gate repository checks remain: format, with docs citations in the GPU
              gate.
              Changing build infrastructure (build/, Directory.Build.*, global.json, Puck.slnx) chooses
              every suite. A changed .puck source that declares test blocks is run with puck test, and
              prints as a test line. A change under .github/ chooses only the suites whose project files
              declare it (PuckAffectedInput). Prose, .claude/, editors/ and experimental/ choose no suite
              or canary.

              A baseline is chosen when its owning test project is reached or a changed or deleted
              file matches its declared data inputs. Each baseline line names its artifact and is
              followed by the exact puck baselines <artifact> --check command the gate runs.

              --run runs manifest checks, then builds the chosen suites in one build and runs the CPU
              tests of up to --suite-jobs of them at once; a heavy suite starts first and waits on the
              machine-wide heavy-suite admission before its run. Each suite prints its verdict and wall
              time as it ends. It then runs the worlds. --run --gpu then runs the chosen canaries, up to
              --gpu-jobs legs on the GPU at once, and names parity when it is chosen without running it:
              puck parity takes the parity world it compares. Baseline checks run only through puck gate.

              Exit codes: 0 planned or every chosen check passed, 1 a chosen check failed, 2 refused.
            """);
        command.SetAction(action: parseResult => Run(
            gpu: parseResult.GetValue(option: gpuOption),
            gpuJobs: parseResult.GetValue(option: gpuJobsOption),
            json: parseResult.GetValue(option: jsonOption),
            mergeBase: parseResult.GetValue(option: mergeBaseOption),
            run: parseResult.GetValue(option: runOption),
            since: parseResult.GetValue(option: sinceOption),
            suiteJobs: parseResult.GetValue(option: suiteJobsOption),
            root: ((RootCommand)parseResult.RootCommandResult.Command)
        ));

        return command;
    }
}
