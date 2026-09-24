using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Source;

namespace Puck.Cli.Format;

// One semantic pass's findings across a run, reported once every project has been processed.
internal sealed class SemanticOutcome {
    public List<string> Corrupted { get; } = [];
    public List<string> Drifted { get; } = [];
    public List<string> NonConvergent { get; } = [];
    // Project directory name to the reason its closure was refused, reported in ordinal order.
    public SortedDictionary<string, string> RefusedProjects { get; } = new(comparer: StringComparer.Ordinal);
    public List<string> RefusedFiles { get; } = [];

    public int Ungrouped { get; set; }
    public int Unresolved { get; set; }
}
// The disk phase behind the semantic passes, `null-pattern` then `named-args`. Symbols resolve against each target
// file's owning project (its own trees plus its compile closure), never one merged compilation, so a tree-wide root
// spanning many projects still binds every project's calls correctly. Each project is parsed and compiled once for
// both passes: `null-pattern` runs first, any file it writes replaces its tree in the compilation, and `named-args`
// then binds against that. Each pass still reports on its own, in that order.
internal static class SemanticPhases {
    // Builds a compilation over the project's trees against the reference set the build itself hands the
    // compiler for `configuration` (CompileClosure), plus the two on-disk inputs that belong to the same
    // build: the SDK-generated global-usings file (obj/<configuration>/**/*.GlobalUsings.g.cs, the
    // project's implicit and explicit global usings) and any emitted generator output. Returns null and
    // sets `refusal` when the closure cannot be trusted, because a partial closure binds some calls
    // against the wrong overload rather than leaving them alone.
    private static CSharpCompilation? BuildProjectCompilation(
        CompileClosures closures,
        string projectRoot,
        string configuration,
        IEnumerable<SyntaxTree> trees,
        CSharpParseOptions parseOptions,
        out string? refusal
    ) {
        var closure = closures.Resolve(
            configuration: configuration,
            projectRoot: projectRoot
        );

        refusal = closure.Refusal;

        if (refusal is not null) {
            return null;
        }

        var objDirectory = Path.Combine(
            path1: projectRoot,
            path2: "obj",
            path3: configuration
        );
        var objFiles = (Directory.Exists(path: objDirectory)
            ? Directory.EnumerateFiles(
                path: objDirectory,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*.cs"
            ).ToList()
            : []
        );
        // A built project that emitted no global-usings file genuinely declares none; there is no default
        // set to fall back on once the closure says the build is real.
        var globalUsings = objFiles
            .Where(predicate: static path => path.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".GlobalUsings.g.cs"
        ))
            .Take(count: 1);
        // Source-generator output (interop projections and the like) is produced in memory during build and
        // is absent from disk unless the project sets EmitCompilerGeneratedFiles. When it is emitted, under
        // obj/<configuration>/**/generated/, include it so calls into generated types resolve too;
        // otherwise those calls are reported as unresolved and left positional.
        var generated = objFiles.Where(predicate: static path => path.Contains(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: $"{Path.DirectorySeparatorChar}generated{Path.DirectorySeparatorChar}"
        ));

        // The trees come from the project directory, so a compile item linked in from elsewhere is not among
        // them; the closure names every source the build compiles, and the ones the walk missed join here.
        var carried = trees.ToList();
        var walked = carried.Select(selector: static tree => Path.GetFullPath(path: tree.FilePath)).ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);
        var linked = closure.Sources.Where(predicate: source => (!walked.Contains(item: source) && File.Exists(path: source)));

        return CSharpCompilation.Create(
            assemblyName: closure.AssemblyName,
            // The closure's compile items already name the global-usings file once the project is built, so each
            // path is parsed once however many of these sources name it.
            syntaxTrees: carried.Concat(second: linked.Concat(second: globalUsings).Concat(second: generated).Distinct(comparer: StringComparer.OrdinalIgnoreCase).Select(selector: path => CSharpSyntaxTree.ParseText(
                text: File.ReadAllText(path: path),
                options: parseOptions,
                path: path
            ))),
            references: closures.MetadataReferences(closure: closure),
            options: new CSharpCompilationOptions(
                outputKind: OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true
            )
        );
    }
    // The projects that might link in `orphan`, a source outside every project directory: the projects one directory
    // below the nearest ancestor that has any, the way a shared `tests/Shared/X.cs` sits beside the test projects that
    // include it. The search ends at the root of the checkout the orphan lives in, the directory holding `.git`, and
    // not at Puck.slnx: formatting a project file needs no Puck checkout, so the boundary is the orphan's own
    // repository whatever it is.
    private static List<string> LinkingCandidates(string orphan) {
        List<string>? found = null;

        _ = RepositoryPaths.Ascend(
            probe: directory => {
                var candidates = directory.EnumerateDirectories()
                    .Where(predicate: static child => child.EnumerateFiles(searchPattern: "*.csproj").Any())
                    .Select(selector: static child => child.FullName)
                    .ToList();

                if (
                    (candidates.Count > 0) ||
                    Path.Exists(path: Path.Combine(
                        path1: directory.FullName,
                        path2: ".git"
                    ))
                ) {
                    found = candidates;

                    return directory.FullName;
                }

                return null;
            },
            start: Path.GetDirectoryName(path: Path.GetFullPath(path: orphan))!
        );

        return (found ?? []);
    }

    // Maps each of `orphans` (sources outside every project directory) that some project compiles to the root of the
    // first such project, in ordinal order, among `projectRoots` and each orphan's linking candidates. Evaluates every
    // closure involved in one batch, which also serves the run's own projects.
    internal static Dictionary<string, string> Hosts(CompileClosures closures, string configuration, IReadOnlyCollection<string> orphans, IEnumerable<string> projectRoots) {
        var candidates = projectRoots
            .Concat(second: orphans.SelectMany(selector: LinkingCandidates))
            .Select(selector: static root => Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: root)))
            .Distinct(comparer: StringComparer.OrdinalIgnoreCase)
            .Order(comparer: StringComparer.Ordinal)
            .ToList();
        var hosts = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);

        closures.Prefetch(
            configuration: configuration,
            projectRoots: candidates
        );

        foreach (var orphan in orphans) {
            var path = Path.GetFullPath(path: orphan);
            var host = candidates.FirstOrDefault(predicate: root => closures.Resolve(
                configuration: configuration,
                projectRoot: root
            ).Sources.Contains(
                comparer: StringComparer.OrdinalIgnoreCase,
                value: path
            ));

            if (host is not null) {
                hosts[path] = host;
            }
        }

        return hosts;
    }

    public static int Run(CompileClosures closures, string rootArgument, string configuration, bool nullPattern, bool namedArgs, bool check, string[]? targets = null) {
        if (
            !nullPattern &&
            !namedArgs
        ) {
            return 0;
        }

        var targetFiles = targets;

        if (
            (targetFiles is null) &&
            !SourceFiles.TryEnumerate(
            files: out targetFiles,
            rootArgument: rootArgument,
            scanRoot: out _,
            verb: "format"
        )
        ) {
            return 2;
        }

        var parseOptions = new CSharpParseOptions(languageVersion: LanguageVersion.Preview);
        var byProject = targetFiles.GroupBy(
            keySelector: static file => (SourceFiles.FindOwningProjectDirectory(start: Path.GetDirectoryName(path: Path.GetFullPath(path: file))!) ?? ""),
            comparer: StringComparer.OrdinalIgnoreCase
        ).ToList();
        var orphans = byProject.Where(predicate: static group => (group.Key.Length == 0)).SelectMany(selector: static group => group).ToList();
        var work = byProject.Where(predicate: static group => (group.Key.Length > 0)).Select(selector: static group => (Root: group.Key, Files: group.ToList())).ToList();
        var hosts = Hosts(
            closures: closures,
            configuration: configuration,
            orphans: orphans,
            projectRoots: work.Select(selector: static entry => entry.Root)
        );
        var nulls = new SemanticOutcome();
        var names = new SemanticOutcome();

        // A source outside every project directory joins the project that compiles it; one no project compiles is
        // reported rather than bound against nothing.
        foreach (var orphan in orphans) {
            if (!hosts.TryGetValue(
                key: Path.GetFullPath(path: orphan),
                value: out var host
            )) {
                nulls.Ungrouped++;
                names.Ungrouped++;

                continue;
            }

            var index = work.FindIndex(match: entry => string.Equals(
                a: entry.Root,
                b: host,
                comparisonType: StringComparison.OrdinalIgnoreCase
            ));

            if (index < 0) {
                work.Add(item: (host, []));
                index = (work.Count - 1);
            }

            work[index].Files.Add(item: orphan);
        }

        foreach (var (projectRoot, projectFiles) in work) {
            if (!SourceFiles.TryEnumerate(
                files: out var compilationFiles,
                rootArgument: projectRoot,
                scanRoot: out _,
                verb: "format"
            )) {
                nulls.Ungrouped += projectFiles.Count;
                names.Ungrouped += projectFiles.Count;

                continue;
            }

            var treesByPath = new Dictionary<string, SyntaxTree>(comparer: StringComparer.OrdinalIgnoreCase);

            foreach (var file in compilationFiles) {
                treesByPath[Path.GetFullPath(path: file)] = CSharpSyntaxTree.ParseText(
                    text: File.ReadAllText(path: file),
                    options: parseOptions,
                    path: file
                );
            }

            var compilation = BuildProjectCompilation(
                closures: closures,
                projectRoot: projectRoot,
                configuration: configuration,
                trees: treesByPath.Values,
                parseOptions: parseOptions,
                refusal: out var refusal
            );

            // A project whose closure cannot be trusted resolves only part of its source, and a pass acts on what it
            // resolves: it would rewrite whatever still binds and leave the rest — a partial answer
            // indistinguishable, in the report, from a swept file. A call that binds to a different overload because
            // the right one's assembly is absent gets named against that other signature, and the write guard only
            // counts parse errors, so the guess would be written. Decline the whole project, in every mode, so
            // --check never reports a drift the run could not honestly apply either.
            if (compilation is null) {
                var project = Path.GetFileName(path: projectRoot);

                nulls.RefusedProjects[project] = refusal!;
                names.RefusedProjects[project] = refusal!;

                foreach (var file in projectFiles) {
                    names.RefusedFiles.Add(item: CliPaths.ToDisplay(fullPath: file));
                }

                continue;
            }

            // A linked source is one of the compilation's own trees, parsed when the closure named it.
            foreach (var tree in compilation.SyntaxTrees) {
                treesByPath.TryAdd(
                    key: Path.GetFullPath(path: tree.FilePath),
                    value: tree
                );
            }

            if (nullPattern) {
                foreach (var (path, text) in NullPatternPhase.Process(
                    check: check,
                    compilation: compilation,
                    outcome: nulls,
                    parseOptions: parseOptions,
                    targets: projectFiles,
                    treesByPath: treesByPath
                )) {
                    var previous = treesByPath[path];
                    var rewritten = CSharpSyntaxTree.ParseText(
                        text: text,
                        options: parseOptions,
                        path: previous.FilePath
                    );

                    compilation = compilation.ReplaceSyntaxTree(
                        newTree: rewritten,
                        oldTree: previous
                    );
                    treesByPath[path] = rewritten;
                }
            }

            if (namedArgs) {
                NamedArgsPhase.Process(
                    check: check,
                    compilation: compilation,
                    outcome: names,
                    targets: projectFiles,
                    treesByPath: treesByPath
                );
            }
        }

        var result = 0;

        if (nullPattern) {
            result = Math.Max(
                val1: result,
                val2: NullPatternPhase.Report(
                    configuration: configuration,
                    fileCount: targetFiles.Length,
                    outcome: nulls,
                    check: check
                )
            );
        }

        if (namedArgs) {
            result = Math.Max(
                val1: result,
                val2: NamedArgsPhase.Report(
                    configuration: configuration,
                    fileCount: targetFiles.Length,
                    outcome: names,
                    check: check
                )
            );
        }

        return result;
    }
}
