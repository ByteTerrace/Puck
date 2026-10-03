using Microsoft.Extensions.FileSystemGlobbing;
using Puck.Cli.Baselines;

namespace Puck.Cli.Affected;

/// <summary>One project as selection sees it: where it lives and what it references.</summary>
/// <param name="Directory">The project's directory, repository-relative with forward slashes and no trailing slash.</param>
/// <param name="Name">The project's name.</param>
/// <param name="References">Every project it references, build-order-only references included.</param>
/// <param name="IsSuite">Whether it is a test suite: a project under <c>tests/</c>.</param>
internal sealed record AffectedProject(string Directory, string Name, IReadOnlyList<string> References, bool IsSuite);
/// <summary>One canary as selection sees it: its directory and every file its manifest names.</summary>
/// <param name="Directory">The canary's directory, repository-relative with forward slashes.</param>
/// <param name="Files">The worlds, scripts and fixtures its legs name, repository-relative with forward slashes.</param>
/// <param name="Id">The canary's id.</param>
/// <param name="RequiresGpu">Whether the canary renders on a GPU, which makes it a reason to run parity.</param>
internal sealed record AffectedCanary(string Directory, IReadOnlyList<string> Files, string Id, bool RequiresGpu);
/// <summary>What a change needs run.</summary>
/// <param name="Baselines">The committed baseline artifacts, in ordinal name order, checked by the gate.</param>
/// <param name="Canaries">The canary ids, ordinal order.</param>
/// <param name="CanaryChecks">The canary ids whose manifests need strict loading and listing, ordinal order.</param>
/// <param name="Catalog">Whether the shipped world catalog must be checked against a fresh tree compile
/// (<c>puck compile --tree … --check</c>): a shipped world source, or code whose output the compile writes, changed.</param>
/// <param name="Everything">Whether build infrastructure changed, which reaches every suite.</param>
/// <param name="Parity">Whether <c>puck parity</c> must run: a chosen canary renders on a GPU, so the change reaches
/// the render path parity compares across backends.</param>
/// <param name="Suites">The test suites, ordinal order.</param>
/// <param name="Unmapped">Changed World sources the coverage index does not know, so no canary could be chosen for
/// them; ordinal order.</param>
/// <param name="Deleted">World sources deleted since the base that neither the current index nor the one the base
/// recorded names, so no canary could be chosen for them and no recording ever can; ordinal order.</param>
/// <param name="Worlds">Changed <c>.puck</c> sources that declare <c>test</c> blocks, for <c>puck test</c>; ordinal
/// order.</param>
internal sealed record AffectedPlan(IReadOnlyList<BaselineArtifact> Baselines, IReadOnlyList<string> Canaries, IReadOnlyList<string> CanaryChecks, bool Catalog, bool Everything, bool Parity, IReadOnlyList<string> Suites, IReadOnlyList<string> Unmapped, IReadOnlyList<string> Worlds, IReadOnlyList<string> Deleted);
/// <summary>
/// Chooses the suites and canaries a set of changed files needs, and nothing wider. Suites follow the project graph:
/// a changed project and every project that references it, transitively. Canaries follow what they were recorded
/// executing (the coverage index) plus the files their manifests name. A change the rules cannot place is reported,
/// never silently widened into "run everything". A file the index cannot know, such as a project file or a shader, is
/// placed through the indexed sources it stands for.
/// </summary>
internal static class AffectedSelection {
    // Changing any of these changes how every project builds, so it reaches every suite; it reaches the canaries only
    // when it is an input of the World build (worldInput), since a canary runs nothing but that build.
    private static readonly string[] BuildInfrastructure = ["build/", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "Puck.slnx", "NuGet.config"];
    // Trees whose changes no suite or canary observes: prose, agent material, CI orchestration, editors, quarantine.
    private static readonly string[] Inert = ["docs/", ".claude/", ".github/", "editors/", "experimental/"];

    private static bool StartsWithAny(string path, string[] prefixes) => prefixes.Any(predicate: prefix => path.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: prefix
    ));
    private static bool IsInert(string path) =>
        (StartsWithAny(path: path, prefixes: Inert) || path.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".md"
        ));

    private const string LockFileName = "packages.lock.json";

    private static bool IsUnder(string path, string directory) => path.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: (directory + "/")
    );

    /// <summary>Selects what the changed files need.</summary>
    /// <param name="changed">The changed files, repository-relative with forward slashes.</param>
    /// <param name="projects">Every project in the graph.</param>
    /// <param name="canaries">Every canary.</param>
    /// <param name="coverage">The coverage index: each source file the recording saw, with the canary ids that executed
    /// it — an empty set for a file none executed. Empty when never recorded.</param>
    /// <param name="consumersOf">The projects whose sources name a file that belongs to no project, such as a test
    /// data directory; empty when none do.</param>
    /// <param name="worldClosure">The projects the World executable is built from, itself included.</param>
    /// <param name="worldInput">Whether a build-infrastructure path is an input of the World build (one of the roots
    /// <see cref="WorldArtifactClosure"/> walks and the World build key hashes): a change to it may change every World
    /// a canary boots, so it selects every canary, and a change to any other such path selects none.</param>
    /// <param name="declaresTests">Whether a changed <c>.puck</c> source declares <c>test</c> blocks.</param>
    /// <param name="catalogInputs">Whether a changed file is an input of the shipped catalog's tree compile.</param>
    /// <param name="canariesReaching">The canaries whose manifest worlds and fixtures reach a changed file through the
    /// documents they name (<see cref="AffectedDocuments"/>); none when none do.</param>
    /// <param name="standInsFor">The indexed sources a changed file the index does not know stands for
    /// (<see cref="AffectedStandIns"/>): their canaries are its canaries, and a file with an indexed stand-in is not
    /// unmapped.</param>
    /// <param name="compiledUnchanged">Whether a changed shipped world file compiles to the same document value at the
    /// base and the head (<see cref="AffectedCompiledWorlds"/>): a canary boots the compiled world, so such a file reaches
    /// its suites and the catalog check but no canary, and it is never unmapped.</param>
    /// <param name="triviaOnly">Whether a C# edit changes only trivia, so reaches no execution.</param>
    /// <param name="proseOnly">Whether a manifest edit changes only prose, so needs its strict load/list check.</param>
    /// <param name="deleted">The changed files deleted since the base, or <see langword="null"/> for none: nothing reads
    /// them, and one no index places is listed in <see cref="AffectedPlan.Deleted"/>, never as unmapped.</param>
    /// <param name="recorded">The index as the base recorded it, which places a deleted file the current index no longer
    /// names, or <see langword="null"/> for none.</param>
    /// <param name="recordedStandInsFor">The stand-ins a deleted file had in the tree the base recorded
    /// (<see cref="AffectedStandIns"/> over <see cref="AffectedRevisionTree"/>), looked up in <paramref name="recorded"/>
    /// and then the current index, or <see langword="null"/> for none.</param>
    /// <param name="recordedCanariesReaching">The canaries whose manifest worlds and fixtures reached a deleted file
    /// through the documents the base's tree held (<see cref="AffectedDocuments"/> over
    /// <see cref="AffectedRevisionTree"/>), or <see langword="null"/> for none; a deleted file is never reached in the
    /// working tree.</param>
    /// <returns>The plan.</returns>
    public static AffectedPlan Select(
        IReadOnlyList<string> changed,
        IReadOnlyList<AffectedProject> projects,
        IReadOnlyList<AffectedCanary> canaries,
        IReadOnlyDictionary<string, IReadOnlySet<string>> coverage,
        Func<string, IReadOnlyList<string>> consumersOf,
        IReadOnlySet<string> worldClosure,
        Func<string, bool> declaresTests,
        Func<string, string?, bool> catalogInputs,
        Func<string, IReadOnlyList<string>> standInsFor,
        Func<string, IReadOnlySet<string>> canariesReaching,
        Func<string, bool> worldInput,
        Func<string, bool>? compiledUnchanged = null,
        Func<string, bool>? triviaOnly = null,
        Func<string, bool>? proseOnly = null,
        IReadOnlySet<string>? deleted = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? recorded = null,
        Func<string, IReadOnlyList<string>>? recordedStandInsFor = null,
        Func<string, IReadOnlySet<string>>? recordedCanariesReaching = null
    ) {
        var catalog = false;
        var everything = false;
        var worldBuild = false;
        var seeds = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        // Projects a change reaches alone, without the projects that reference them: a project's own restore lock.
        var alone = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var selected = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var checks = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var effective = new List<string>();
        var unmapped = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var gone = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var worlds = new SortedSet<string>(comparer: StringComparer.Ordinal);
        // Deepest directory first, so a nested project owns its files rather than its parent.
        var owners = projects.OrderByDescending(keySelector: static project => project.Directory.Length).ToArray();

        foreach (var path in changed) {
            if (triviaOnly?.Invoke(arg: path) == true) {
                continue;
            }
            if ((proseOnly?.Invoke(arg: path) == true) && (AffectedManifestProse.Id(path: path) is { } id)) {
                _ = checks.Add(item: id);
                continue;
            }
            effective.Add(item: path);

            if (StartsWithAny(path: path, prefixes: BuildInfrastructure)) {
                everything = true;
                worldBuild |= worldInput(arg: path);

                continue;
            }

            // A restore lock pins the packages its own project builds against and nothing else: it reaches that
            // project's suite alone, and the canaries only when that project is part of the World build.
            if (string.Equals(a: Path.GetFileName(path: path), b: LockFileName, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                if (owners.FirstOrDefault(predicate: project => IsUnder(path: path, directory: project.Directory)) is { } lockOwner) {
                    _ = alone.Add(item: lockOwner.Name);
                    worldBuild |= worldClosure.Contains(item: lockOwner.Name);
                }

                continue;
            }

            if (IsInert(path: path)) {
                continue;
            }

            var isDeleted = (deleted?.Contains(item: path) ?? false);

            if (
                !isDeleted &&
                path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".puck") &&
                declaresTests(arg: path)
            ) {
                _ = worlds.Add(item: path);
            }

            if (compiledUnchanged?.Invoke(arg: path) == true) {
                var unchangedOwner = owners.FirstOrDefault(predicate: project => IsUnder(path: path, directory: project.Directory));

                catalog |= catalogInputs(arg1: path, arg2: unchangedOwner?.Name);
                if (unchangedOwner is not null) {
                    _ = seeds.Add(item: unchangedOwner.Name);
                }

                continue;
            }

            var named = false;

            foreach (var canary in canaries) {
                if (
                    IsUnder(path: path, directory: canary.Directory) ||
                    canary.Files.Contains(value: path, comparer: StringComparer.Ordinal)
                ) {
                    named = true;
                    _ = selected.Add(item: canary.Id);
                }
            }

            // A deleted file is reached through the documents the base's tree held, which named it; the working tree's
            // documents can name it no longer.
            var reaching = (isDeleted
                ? (recordedCanariesReaching?.Invoke(arg: path) ?? new HashSet<string>())
                : canariesReaching(arg: path));

            selected.UnionWith(other: reaching);

            var mapped = (
                coverage.TryGetValue(key: path, value: out var executedBy) ||
                (isDeleted && (recorded?.TryGetValue(key: path, value: out executedBy) ?? false)) ||
                named ||
                (reaching.Count > 0)
            );

            if (executedBy is not null) {
                selected.UnionWith(other: executedBy);
            } else {
                // A deleted file stands for what the tree the base recorded said it stood for, in the index that base
                // recorded; a file the working tree holds stands for what it says now, in the current index.
                var standIns = (isDeleted
                    ? (recordedStandInsFor?.Invoke(arg: path) ?? [])
                    : standInsFor(arg: path));

                foreach (var standIn in standIns) {
                    if (
                        (isDeleted && (recorded?.TryGetValue(key: standIn, value: out var standInExecutedBy) ?? false)) ||
                        coverage.TryGetValue(key: standIn, value: out standInExecutedBy)
                    ) {
                        mapped = true;
                        selected.UnionWith(other: standInExecutedBy);
                    }
                }
            }

            var owner = owners.FirstOrDefault(predicate: project => IsUnder(path: path, directory: project.Directory));

            catalog |= catalogInputs(arg1: path, arg2: owner?.Name);

            if (owner is null) {
                seeds.UnionWith(other: consumersOf(arg: path));

                continue;
            }

            _ = seeds.Add(item: owner.Name);

            if (
                worldClosure.Contains(item: owner.Name) &&
                !mapped
            ) {
                _ = (isDeleted
                    ? gone.Add(item: path)
                    : unmapped.Add(item: path));
            }
        }

        if (worldBuild) {
            selected.UnionWith(other: canaries.Select(selector: static canary => canary.Id));
        }

        var parity = canaries.Any(predicate: canary => (canary.RequiresGpu && selected.Contains(item: canary.Id)));
        IEnumerable<AffectedProject> reached = projects;

        if (!everything) {
            var dependents = new Dictionary<string, List<string>>(comparer: StringComparer.OrdinalIgnoreCase);

            foreach (var project in projects) {
                foreach (var reference in project.References) {
                    if (!dependents.TryGetValue(key: reference, value: out var list)) {
                        list = [];
                        dependents[reference] = list;
                    }

                    list.Add(item: project.Name);
                }
            }

            var affected = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(collection: seeds);

            while (pending.TryDequeue(result: out var name)) {
                if (!affected.Add(item: name)) {
                    continue;
                }

                foreach (var dependent in dependents.GetValueOrDefault(defaultValue: [], key: name)) {
                    pending.Enqueue(item: dependent);
                }
            }

            affected.UnionWith(other: alone);
            reached = projects.Where(predicate: project => affected.Contains(item: project.Name));
        }

        var reachedProjects = reached.Select(selector: static project => project.Name).ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);
        var baselines = BaselinesCommand.Artifacts.Where(predicate: artifact => {
            var inputs = new Matcher(comparisonType: StringComparison.Ordinal);

            inputs.AddIncludePatterns(artifact.Inputs);
            return (reachedProjects.Contains(item: artifact.Project) || inputs.Match(files: effective).HasMatches);
        }).OrderBy(keySelector: static artifact => artifact.Name, comparer: StringComparer.Ordinal);

        return new AffectedPlan(
            Baselines: [.. baselines],
            Canaries: [.. selected],
            CanaryChecks: [.. checks],
            Catalog: (catalog || everything),
            Everything: everything,
            Parity: parity,
            Suites: [.. reached.Where(predicate: static project => project.IsSuite).Select(selector: static project => project.Name).Order(comparer: StringComparer.Ordinal)],
            Unmapped: [.. unmapped],
            Worlds: [.. worlds],
            Deleted: [.. gone]
        );
    }
}
