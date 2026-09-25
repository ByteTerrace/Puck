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
/// <param name="Canaries">The canary ids, ordinal order.</param>
/// <param name="Catalog">Whether the shipped world catalog must be checked against a fresh tree compile
/// (<c>puck compile --tree … --check</c>): a shipped world source, or code whose output the compile writes, changed.</param>
/// <param name="Everything">Whether build infrastructure changed, which reaches every project.</param>
/// <param name="Parity">Whether <c>puck parity</c> must run: a chosen canary renders on a GPU, so the change reaches
/// the render path parity compares across backends.</param>
/// <param name="Suites">The test suites, ordinal order.</param>
/// <param name="Unmapped">Changed World sources the coverage index does not know, so no canary could be chosen for
/// them; ordinal order.</param>
/// <param name="Worlds">Changed <c>.puck</c> sources that declare <c>test</c> blocks, for <c>puck test</c>; ordinal
/// order.</param>
internal sealed record AffectedPlan(IReadOnlyList<string> Canaries, bool Catalog, bool Everything, bool Parity, IReadOnlyList<string> Suites, IReadOnlyList<string> Unmapped, IReadOnlyList<string> Worlds);
/// <summary>
/// Chooses the suites and canaries a set of changed files needs, and nothing wider. Suites follow the project graph:
/// a changed project and every project that references it, transitively. Canaries follow what they were recorded
/// executing (the coverage index) plus the files their manifests name. A change the rules cannot place is reported,
/// never silently widened into "run everything". A file the index cannot know, such as a project file or a shader, is
/// placed through the indexed sources it stands for.
/// </summary>
internal static class AffectedSelection {
    // Changing any of these changes how every project builds.
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
    /// <param name="declaresTests">Whether a changed <c>.puck</c> source declares <c>test</c> blocks.</param>
    /// <param name="catalogInputs">Whether a changed file is an input of the shipped catalog's tree compile.</param>
    /// <param name="standInsFor">The indexed sources a changed file the index does not know stands for
    /// (<see cref="AffectedStandIns"/>): their canaries are its canaries, and a file with an indexed stand-in is not
    /// unmapped.</param>
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
        Func<string, IReadOnlyList<string>> standInsFor
    ) {
        var catalog = false;
        var everything = false;
        var seeds = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var selected = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var unmapped = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var worlds = new SortedSet<string>(comparer: StringComparer.Ordinal);
        // Deepest directory first, so a nested project owns its files rather than its parent.
        var owners = projects.OrderByDescending(keySelector: static project => project.Directory.Length).ToArray();

        foreach (var path in changed) {
            if (StartsWithAny(path: path, prefixes: BuildInfrastructure)) {
                everything = true;

                continue;
            }

            if (IsInert(path: path)) {
                continue;
            }

            if (
                path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".puck") &&
                declaresTests(arg: path)
            ) {
                _ = worlds.Add(item: path);
            }

            foreach (var canary in canaries) {
                if (
                    IsUnder(path: path, directory: canary.Directory) ||
                    canary.Files.Contains(value: path, comparer: StringComparer.Ordinal)
                ) {
                    _ = selected.Add(item: canary.Id);
                }
            }

            var mapped = coverage.TryGetValue(key: path, value: out var executedBy);

            if (mapped) {
                selected.UnionWith(other: executedBy!);
            } else {
                foreach (var standIn in standInsFor(arg: path)) {
                    if (coverage.TryGetValue(key: standIn, value: out var standInExecutedBy)) {
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
                _ = unmapped.Add(item: path);
            }
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

            reached = projects.Where(predicate: project => affected.Contains(item: project.Name));
        }

        return new AffectedPlan(
            Canaries: [.. selected],
            Catalog: (catalog || everything),
            Everything: everything,
            Parity: parity,
            Suites: [.. reached.Where(predicate: static project => project.IsSuite).Select(selector: static project => project.Name).Order(comparer: StringComparer.Ordinal)],
            Unmapped: [.. unmapped],
            Worlds: [.. worlds]
        );
    }
}
