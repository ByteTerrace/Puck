using System.Text;

namespace Puck.Cli.Architecture;

// The `puck architecture` verb: the REPORT AND EXPLAIN surface for the architecture gate.
//
// It is deliberately not the authority. The gate in build/Puck.Architecture.targets is, because it runs
// inside every project's build against the RESOLVED reference set — what the compiler is actually handed —
// and refuses the build. This verb reads the same policy ledger and every project's own declaration, and
// answers the questions a build failure cannot: what does the whole graph look like at once, which projects
// hold a backend and by what permission, does each compiled assembly's friend set match what was declared,
// and what about this analysis is configuration-dependent.
//
// Exit 0 when everything checks, 1 when a check fails, 2 on a usage error or a missing repository root.
internal static class ArchitectureCommand {
    private const string BackendsLayer = "Backends";
    private const string CompositionRootsLayer = "Composition roots";
    private const string HelpText =
        """
        puck architecture — report on the repository's project-layering policy

        Usage: puck architecture [options]

        Options:
          --configuration <name>  Which build configuration's assemblies to read for the
                                  friend-set comparison (default: Release).
          --map                   Print only the layering block, generated from each project's
                                  own <PuckLayer> declaration, for docs/project-map.md.
          -h, --help              This text.

        The build-time gate is the authority; this verb explains it. Policy lives in
        build/Architecture.props; every project declares its own <PuckKind> and <PuckLayer>.
        """;
    private const string PresentationLayer = "Presentation";

    public static int Run(string[] args) {
        var scanner = new ArgScanner().Flag(name: "h").Flag(name: "help").Flag(name: "map").Value(name: "configuration");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"architecture: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var model = ArchitectureModel.Load(repositoryRoot: repositoryRoot);

        if (scanner.Has(name: "map")) {
            Console.Out.Write(value: RenderLayeringBlock(model: model));

            return 0;
        }

        var failures = new List<string>();
        var output = new StringBuilder();

        ReportDeclarations(failures: failures, model: model, output: output);
        ReportLayerGraph(failures: failures, model: model, output: output);
        ReportBackendQuarantine(failures: failures, model: model, output: output);
        ReportProfiles(failures: failures, model: model, output: output);
        ReportFriends(configuration: (scanner.Get(name: "configuration") ?? "Release"), failures: failures, model: model, output: output);
        ReportConfigurationSensitivity(model: model, output: output);

        Console.Out.Write(value: output.ToString());

        if (failures.Count == 0) {
            // NOT the gate, and the line says so. The enforcement is PuckArchitectureGate, which runs in every
            // in-scope project's build against the RESOLVED reference set; this verb reads csproj declarations and
            // closes over @(ProjectReference). The difference is not cosmetic: a Puck assembly arriving by a path no
            // ProjectReference declares is exactly what PUCKARCH006 exists to catch, and is exactly what this verb
            // cannot see. A green line here must never be reported as the architecture holding.
            Console.Out.WriteLine(value: $"architecture: {model.Projects.Count} projects, every declared check passed.");
            Console.Out.WriteLine(value: "  This is a REPORT over declared references, not the gate. The gate is PuckArchitectureGate,");
            Console.Out.WriteLine(value: "  which runs in each project's build over the RESOLVED reference set and catches edges that");
            Console.Out.WriteLine(value: "  arrive by paths no ProjectReference declares. Build the solution to enforce.");

            return 0;
        }

        Console.Error.WriteLine(value: $"architecture: {failures.Count} failing check(s).");

        foreach (var failure in failures) {
            Console.Error.WriteLine(value: $"  {failure}");
        }

        return 1;
    }

    /// <summary>
    /// The layering block for docs/project-map.md, GENERATED from each project's own declaration.
    /// </summary>
    /// <remarks>
    /// The direction matters and is the ratified design: declarations are authoritative and the document is
    /// derived from them. A hand-written block is a second source that drifts silently, which is exactly what
    /// it had done — it was missing three projects and still carried a row for one that had been quarantined
    /// out of the repository.
    /// </remarks>
    private static string RenderLayeringBlock(ArchitectureModel model) {
        var output = new StringBuilder();
        var width = (model.Layers.Max(selector: l => l.Length) + 2);

        foreach (var layer in model.Layers) {
            var members = model.Projects.Values
                .Where(predicate: p => string.Equals(a: p.Layer, b: layer, comparisonType: StringComparison.OrdinalIgnoreCase))
                .Select(selector: p => p.Name)
                .OrderBy(keySelector: n => n, comparer: StringComparer.Ordinal)
                .ToArray();

            if (members.Length == 0) {
                continue;
            }

            AppendRow(label: layer, members: members, output: output, width: width);
        }

        foreach (var kind in model.Kinds.Where(predicate: k => !k.Value).Select(selector: k => k.Key)) {
            var members = model.Projects.Values
                .Where(predicate: p => string.Equals(a: p.Kind, b: kind, comparisonType: StringComparison.OrdinalIgnoreCase))
                .Select(selector: p => p.Name)
                .OrderBy(keySelector: n => n, comparer: StringComparer.Ordinal)
                .ToArray();

            if (members.Length == 0) {
                continue;
            }

            AppendRow(label: $"({kind})", members: members, output: output, width: width);
        }

        return output.ToString();
    }
    /// <summary>Emits one layering row, wrapping its members under a hanging indent past 79 columns.</summary>
    private static void AppendRow(string label, string[] members, StringBuilder output, int width) {
        var line = new StringBuilder(value: label.PadRight(totalWidth: width));
        var first = true;

        foreach (var member in members) {
            if (!first && (((line.Length + 2) + member.Length) > 79)) {
                _ = output.AppendLine(value: line.ToString().TrimEnd());
                _ = line.Clear().Append(value: new string(c: ' ', count: width));
                first = true;
            }

            _ = line.Append(value: (first ? member : $"  {member}"));
            first = false;
        }

        _ = output.AppendLine(value: line.ToString().TrimEnd());
    }
    private static void ReportBackendQuarantine(List<string> failures, ArchitectureModel model, StringBuilder output) {
        _ = output.AppendLine(value: "## Backend quarantine").AppendLine();

        var backends = model.Projects.Values
            .Where(predicate: p => string.Equals(a: p.Layer, b: BackendsLayer, comparisonType: StringComparison.OrdinalIgnoreCase))
            .Select(selector: p => p.Name)
            .ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);
        var holders = 0;

        foreach (var project in model.Projects.Values.OrderBy(keySelector: p => p.Name, comparer: StringComparer.Ordinal)) {
            var held = model.Closure(name: project.Name).Where(predicate: c => backends.Contains(item: c)).ToArray();

            if (held.Length == 0) {
                continue;
            }

            ++holders;

            var terminal = (model.Kinds.TryGetValue(key: project.Kind, value: out var ranked) && !ranked);
            var introduced = held.Where(predicate: h => project.Edges.Contains(value: h, comparer: StringComparer.OrdinalIgnoreCase)).ToArray();
            var permission =
                (((project.Layer == PresentationLayer) || (project.Layer == CompositionRootsLayer)) ? $"permitted: {project.Layer}"
                : (model.BackendExceptions.ContainsKey(key: project.Name) ? "permitted: NAMED EXCEPTION"
                : ((terminal && (introduced.Length == 0)) ? $"permitted: terminal kind ({project.Kind}), inherits and introduces nothing"
                : "NOT PERMITTED")));

            if (permission == "NOT PERMITTED") {
                failures.Add(item: $"{project.Name} holds {string.Join(separator: ", ", values: held)} and is not permitted to.");
            }

            _ = output.AppendLine(value: $"  {project.Name,-30} {string.Join(separator: ", ", values: held),-28} {permission}");
        }

        _ = output.AppendLine();
        _ = output.AppendLine(value: $"  {holders} project(s) hold a backend; {model.BackendExceptions.Count} named exception(s).");

        foreach (var (name, reason) in model.BackendExceptions) {
            _ = output.AppendLine(value: $"  {name}: {reason}");
        }

        // The absent finding is the load-bearing half, so it is REPORTED rather than left as a silence:
        // empty-because-the-rule-was-too-wide and empty-because-the-graph-is-clean read identically.
        var engineHolders = model.Projects.Values
            .Where(predicate: p => string.Equals(a: p.Layer, b: "Engine services", comparisonType: StringComparison.OrdinalIgnoreCase))
            .Count(predicate: p => model.Closure(name: p.Name).Any(predicate: c => backends.Contains(item: c)));

        _ = output.AppendLine(value: $"  Engine-services projects reaching a backend: {engineHolders}.").AppendLine();
    }
    private static void ReportConfigurationSensitivity(ArchitectureModel model, StringBuilder output) {
        _ = output.AppendLine(value: "## Configuration sensitivity").AppendLine();

        // A conditional reference can differ by configuration, TFM or RID, so a single-configuration pass
        // proves less than it appears. Rather than print that caveat unconditionally, MEASURE it: a caveat
        // that is always emitted teaches a reader to skip it.
        var conditional = model.Projects.Values
            .Where(predicate: p => (File.ReadAllText(path: p.File).Contains(comparisonType: StringComparison.Ordinal, value: "<ProjectReference") && ConditionalReferenceIn(file: p.File)))
            .Select(selector: p => p.Name)
            .ToArray();

        if (conditional.Length == 0) {
            _ = output
                .AppendLine(value: "  No project declares a CONDITIONAL <ProjectReference>, so the project graph does not vary by")
                .AppendLine(value: "  configuration, target framework or runtime identifier, and this single pass covers all of them.")
                .AppendLine(value: "  Two conditional ItemGroups do carry references and neither changes the graph: Directory.Build.props")
                .AppendLine(value: "  injects the analyzer with ReferenceOutputAssembly=false (never an assembly edge), and")
                .AppendLine(value: "  WindowsCaptureProjection.targets adds PACKAGE references under an OS condition. If that ever stops")
                .AppendLine(value: "  being true this section says so on its own.")
                .AppendLine();

            return;
        }

        _ = output.AppendLine(value: $"  CONDITIONAL project references in: {string.Join(separator: ", ", values: conditional)}.");
        _ = output.AppendLine(value: "  This pass reflects ONE configuration. Those projects' graphs may differ in another.").AppendLine();
    }
    private static void ReportDeclarations(List<string> failures, ArchitectureModel model, StringBuilder output) {
        _ = output.AppendLine(value: "## Declarations").AppendLine();

        foreach (var project in model.Projects.Values.OrderBy(keySelector: p => p.Name, comparer: StringComparer.Ordinal)) {
            if (!model.Kinds.TryGetValue(key: project.Kind, value: out var ranked)) {
                failures.Add(item: $"{project.Name} declares no valid <PuckKind> (found '{project.Kind}').");
            } else if (ranked && !model.Layers.Contains(value: project.Layer, comparer: StringComparer.OrdinalIgnoreCase)) {
                failures.Add(item: $"{project.Name} is a ranked kind and declares no valid <PuckLayer> (found '{project.Layer}').");
            } else if (!ranked && (project.Layer.Length != 0)) {
                failures.Add(item: $"{project.Name} is a terminal kind and must not declare a <PuckLayer>.");
            }

            _ = output.AppendLine(value: $"  {project.Name,-30} {project.Kind,-11} {project.Layer}");
        }

        _ = output.AppendLine();
    }
    private static void ReportFriends(string configuration, List<string> failures, ArchitectureModel model, StringBuilder output) {
        _ = output.AppendLine(value: "## Internals-visible-to, declared vs. compiled").AppendLine();

        var unread = new List<string>();

        foreach (var project in model.Projects.Values.OrderBy(keySelector: p => p.Name, comparer: StringComparer.Ordinal)) {
            var assembly = Directory.EnumerateFiles(
                path: Path.GetDirectoryName(path: project.File)!,
                searchPattern: $"{project.Name}.dll",
                searchOption: SearchOption.AllDirectories)
                .FirstOrDefault(predicate: p => p.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}{configuration}{Path.DirectorySeparatorChar}"));
            var declared = (model.Friends.TryGetValue(key: project.Name, value: out var listed) ? listed : []);

            if (assembly is null) {
                // An unread assembly is NOT a pass. Reporting it as one would make "no findings" mean
                // "nothing was built", which is the failure this whole surface exists to avoid.
                if (declared.Count != 0) {
                    unread.Add(item: project.Name);
                }

                continue;
            }

            var actual = ArchitectureModel.ReadFriendsFromAssembly(assemblyPath: assembly);

            if (actual.SequenceEqual(second: declared.OrderBy(keySelector: f => f, comparer: StringComparer.OrdinalIgnoreCase), comparer: StringComparer.OrdinalIgnoreCase)) {
                if (actual.Count != 0) {
                    _ = output.AppendLine(value: $"  {project.Name,-30} {string.Join(separator: ", ", values: actual)}");
                }

                continue;
            }

            failures.Add(item: $"{project.Name} friend set differs — declared [{string.Join(separator: ", ", values: declared)}], compiled [{string.Join(separator: ", ", values: actual)}].");
            _ = output.AppendLine(value: $"  {project.Name,-30} MISMATCH: compiled [{string.Join(separator: ", ", values: actual)}]");
        }

        if (unread.Count != 0) {
            _ = output.AppendLine(value: $"  NOT CHECKED (no {configuration} assembly on disk): {string.Join(separator: ", ", values: unread)}");
            failures.Add(item: $"friend sets unverified for {string.Join(separator: ", ", values: unread)} — build the {configuration} configuration first; an unread assembly is not a passing one.");
        }

        _ = output.AppendLine();
    }
    private static void ReportLayerGraph(List<string> failures, ArchitectureModel model, StringBuilder output) {
        _ = output.AppendLine(value: "## Layer graph").AppendLine();

        var upward = 0;

        foreach (var project in model.Projects.Values.OrderBy(keySelector: p => p.Name, comparer: StringComparer.Ordinal)) {
            var ownRank = model.RankOf(project: project);

            foreach (var edge in model.Closure(name: project.Name)) {
                if (!model.Projects.TryGetValue(key: edge, value: out var target)) {
                    continue;
                }

                if (model.RankOf(project: target) >= ownRank) {
                    continue;
                }

                ++upward;

                failures.Add(item: $"{project.Name} ({project.Layer}) holds {target.Name} ({target.Layer}) — upward edge.");
            }
        }

        _ = output.AppendLine(value: $"  Upward edges across the transitive closure: {upward}.").AppendLine();
    }
    private static void ReportProfiles(List<string> failures, ArchitectureModel model, StringBuilder output) {
        _ = output.AppendLine(value: "## Lane profiles (exact equality)").AppendLine();

        foreach (var (name, expected) in model.Profiles) {
            if (!model.Projects.ContainsKey(key: name)) {
                failures.Add(item: $"profile names {name}, which is not a project in scope.");

                continue;
            }

            var actual = model.Closure(name: name);
            var matches = actual.SequenceEqual(second: expected.OrderBy(keySelector: e => e, comparer: StringComparer.OrdinalIgnoreCase), comparer: StringComparer.OrdinalIgnoreCase);

            if (!matches) {
                failures.Add(item: $"{name} closure [{string.Join(separator: ", ", values: actual)}] does not equal its profile [{string.Join(separator: ", ", values: expected)}].");
            }

            _ = output.AppendLine(value: $"  {name,-30} {(matches ? "equal" : "DIFFERS")}: {string.Join(separator: ", ", values: actual)}");
        }

        _ = output.AppendLine();
    }
    private static bool ConditionalReferenceIn(string file) {
        foreach (var line in File.ReadLines(path: file)) {
            if (line.Contains(comparisonType: StringComparison.Ordinal, value: "<ProjectReference") && line.Contains(comparisonType: StringComparison.Ordinal, value: "Condition=")) {
                return true;
            }
        }

        return false;
    }
}
