using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

namespace Puck.Cli.Analysis;

// The `puck references` verb: the semantic tier. It loads the real project graph and asks the compiler which
// symbol each name means, so it sees through extension methods, `using` aliases, overload resolution and
// generic instantiation — the four places where text matching gives a wrong answer. In exchange it is
// blind to any file the project system does not compile. Exit 0 when a declaration matched, 1 when none
// did, 2 on a usage error or a workspace failure.
internal static class ReferencesCommand {
    public static int Run(string[] args) {
        var scanner = new ArgScanner()
            .Flag(name: "declarations").Flag(name: "implementers").Flag(name: "overrides").Flag(name: "derived")
            .Value(name: "containing").Flag(name: "contains").Flag(name: "i").Value(name: "kind")
            .Value(name: "solution").Value(name: "project").Value(name: "configuration")
            .Flag(name: "metadata").Flag(name: "nodoc").Flag(name: "strict").Flag(name: "allowpartial")
            .Flag(name: "json").Flag(name: "q").Flag(name: "h").Flag(name: "help");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"references: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        if (scanner.Positionals.Count != 1) {
            Console.Error.WriteLine(value: "references: expected exactly one symbol name (try -h)");

            return 2;
        }

        var filter = ParseFilter(raw: scanner.Get(name: "kind"));

        if (filter is null) {
            return 2;
        }

        if (scanner.Has(name: "contains") && scanner.Has(name: "metadata")) {
            Console.Error.WriteLine(value: "references: --contains is source-only; drop --metadata or spell the name exactly.");

            return 2;
        }

        var options = new ReferencesOptions {
            AllowPartial = scanner.Has(name: "allowpartial"),
            Configuration = (scanner.Get(name: "configuration") ?? "Debug"),
            Containing = scanner.Get(name: "containing"),
            Contains = scanner.Has(name: "contains"),
            Filter = filter.Value,
            IgnoreCase = scanner.Has(name: "i"),
            Json = scanner.Has(name: "json"),
            Metadata = scanner.Has(name: "metadata"),
            Mode = ParseMode(scanner: scanner),
            Name = scanner.Positionals[0],
            NoDoc = scanner.Has(name: "nodoc"),
            ProjectPath = scanner.Get(name: "project"),
            Quiet = scanner.Has(name: "q"),
            SolutionPath = scanner.Get(name: "solution"),
            Strict = scanner.Has(name: "strict"),
        };

        return RunAsync(options: options).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(ReferencesOptions options) {
        var target = ResolveTarget(options: options);

        if (target is null) {
            return 2;
        }

        var failures = new WorkspaceFailureSink();

        using var workspace = MSBuildWorkspace.Create(properties: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["Configuration"] = options.Configuration,
        });
        using var registration = workspace.RegisterWorkspaceFailedHandler(handler: failures.Report);
        Solution solution;

        try {
            solution = ((options.ProjectPath is null)
                ? await workspace.OpenSolutionAsync(solutionFilePath: target)
                : (await workspace.OpenProjectAsync(projectFilePath: target)).Solution);
        } catch (Exception ex) {
            // Everything the load can throw is a bad input as far as the caller is concerned — a malformed
            // solution surfaces as an XmlException, a bad project as one of several loader types — so the
            // filter is the exception type in the message, not a list of types to guess at.
            Console.Error.WriteLine(value: $"references: cannot load {CliPaths.ToDisplay(fullPath: target)}: {ex.GetType().Name}: {ex.Message}");

            return 2;
        }

        // A partly loaded solution answers "no references" exactly the way a genuinely unreferenced
        // symbol does, so a load failure is fatal unless the caller says otherwise.
        if (failures.Failed && !options.AllowPartial) {
            // The commonest cause is an unrestored tree (a fresh worktree): the design-time build then
            // resolves an incomplete reference closure, which also trips the architecture gate's lane
            // profiles. The remedy there is a restore, never accepting a partial answer.
            var unrestoredCount = solution.Projects
                .Select(selector: static project => project.FilePath)
                .Where(predicate: static path => (path is not null))
                .Distinct(comparer: StringComparer.OrdinalIgnoreCase)
                .Count(predicate: static path => !File.Exists(path: Path.Combine(path1: Path.GetDirectoryName(path: path)!, path2: "obj", path3: "project.assets.json")));

            Console.Error.WriteLine(value: ((unrestoredCount > 0)
                ? $"references: the workspace reported a load failure, and {unrestoredCount} project(s) carry no obj/project.assets.json — this tree is not restored. Run `dotnet restore` at its root and retry; --allow-partial would accept an incomplete answer instead."
                : "references: the workspace reported a load failure; the answer would be incomplete (pass --allow-partial to accept it)."));

            return 2;
        }

        var symbols = await FindTargetsAsync(options: options, solution: solution);
        var records = new List<AnalysisRecord>();
        var seen = new HashSet<(string Path, int Line, int Column, string Definition)>();

        foreach (var symbol in symbols) {
            AddDeclarations(records: records, relation: "decl", seen: seen, symbol: symbol);

            switch (options.Mode) {
                case ReferencesMode.References:
                    await AddReferencesAsync(options: options, records: records, seen: seen, solution: solution, symbol: symbol);

                    break;
                case ReferencesMode.Declarations:
                    break;
                default:
                    await AddRelatedAsync(symbol: symbol, solution: solution, mode: options.Mode, records: records, seen: seen);

                    break;
            }
        }

        return AnalysisEmitter.Emit(records: records, json: options.Json, quiet: options.Quiet);
    }
    // Declarations ordered by display string then documentation-comment id, so groups are stable across runs.
    //
    // FindSourceDeclarationsAsync's predicate overload (used even for an exact-name query) walks symbols
    // directly and sees source symbols the compiler synthesizes — record positional properties, a top-level-
    // statements file's entry-point type — that the name-keyed overload's syntax-based index omits.
    // --kind only narrows results after the fact: the search always runs at the widest filter first, since a
    // kind filter also changes which symbol sets get enumerated.
    private static async Task<List<ISymbol>> FindTargetsAsync(Solution solution, ReferencesOptions options) {
        var found = new HashSet<ISymbol>(comparer: SymbolEqualityComparer.Default);
        var comparison = (options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        bool Selects(string candidate) => (options.Contains
            ? candidate.Contains(value: options.Name, comparisonType: comparison)
            : candidate.Equals(value: options.Name, comparisonType: comparison));

        await AddEntryPointsAsync(found: found, selects: Selects, solution: solution);

        if (options.Metadata) {
            // This search is per-project and includes symbols from referenced assemblies; the source-only
            // search is the default because a metadata match then drives a full-solution reference search
            // that can only come back empty.
            foreach (var project in solution.Projects) {
                found.UnionWith(other: await SymbolFinder.FindDeclarationsAsync(
                    project: project,
                    name: options.Name,
                    ignoreCase: options.IgnoreCase,
                    filter: SymbolFilter.All));
            }
        } else {
            found.UnionWith(other: await SymbolFinder.FindSourceDeclarationsAsync(
                solution: solution,
                predicate: Selects,
                filter: SymbolFilter.All));
        }

        return [.. found
            .Where(predicate: symbol => InKind(symbol: symbol, filter: options.Filter))
            .Where(predicate: symbol => ((options.Containing is null) || symbol.ToDisplayString().Contains(value: options.Containing, comparisonType: StringComparison.Ordinal)))
            .OrderBy(keySelector: static symbol => symbol.ToDisplayString(), comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: static symbol => (symbol.GetDocumentationCommentId() ?? string.Empty), comparer: StringComparer.Ordinal)];
    }
    // The entry point of every project that has one, when the query can select it. A top-level-statements
    // file declares no type, so the compiler synthesizes `Program`/`<Main>$` with real source locations that
    // the syntax-based declaration index does not reliably carry; asking each compilation for its entry point
    // directly is exact. The name gate skips the compile for queries that could not match anyway.
    private static async Task AddEntryPointsAsync(Solution solution, Func<string, bool> selects, HashSet<ISymbol> found) {
        if (!selects(arg: "Program") && !selects(arg: "<Main>$")) {
            return;
        }

        foreach (var project in solution.Projects) {
            if ((await project.GetCompilationAsync())?.GetEntryPoint(cancellationToken: default) is not { } entryPoint) {
                continue;
            }

            foreach (var candidate in ((ISymbol?[])[entryPoint, entryPoint.ContainingType])) {
                if ((candidate is not null)
                    && candidate.Locations.Any(predicate: static location => location.IsInSource)
                    && selects(arg: candidate.Name)) {
                    _ = found.Add(item: candidate);
                }
            }
        }
    }
    // The --kind categories, decided on the found symbol rather than inside the search. Anything that is
    // neither a namespace nor a type is a member.
    private static bool InKind(ISymbol symbol, SymbolFilter filter) =>
        symbol switch {
            INamespaceSymbol => ((filter & SymbolFilter.Namespace) != 0),
            ITypeSymbol => ((filter & SymbolFilter.Type) != 0),
            _ => ((filter & SymbolFilter.Member) != 0),
        };
    private static void AddDeclarations(ISymbol symbol, string relation, List<AnalysisRecord> records, HashSet<(string, int, int, string)> seen) {
        var identity = Identity(symbol: symbol);
        var kind = symbol.Kind.ToString();
        var display = symbol.ToDisplayString();
        var emitted = false;

        foreach (var location in symbol.Locations.Where(predicate: static location => location.IsInSource)
            .Select(selector: static location => location.GetLineSpan())
            .OrderBy(keySelector: static span => span.Path, comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: static span => span.StartLinePosition.Line)
            .ThenBy(keySelector: static span => span.StartLinePosition.Character)) {
            emitted |= Add(
                path: CliPaths.ToDisplay(fullPath: location.Path),
                line: (location.StartLinePosition.Line + 1),
                column: (location.StartLinePosition.Character + 1),
                relation: relation,
                kind: kind,
                name: display,
                identity: identity,
                records: records,
                seen: seen);
        }

        // A symbol from a referenced assembly has no source location; it is still a declaration, reported
        // at line 0 of its assembly so the record shape stays uniform.
        if (!emitted && !symbol.Locations.Any(predicate: static location => location.IsInSource)) {
            _ = Add(
                path: (symbol.ContainingAssembly?.Name ?? "metadata"),
                line: 0,
                column: 0,
                relation: relation,
                kind: kind,
                name: display,
                identity: identity,
                records: records,
                seen: seen);
        }
    }
    private static async Task AddReferencesAsync(
        ISymbol symbol,
        Solution solution,
        ReferencesOptions options,
        List<AnalysisRecord> records,
        HashSet<(string, int, int, string)> seen
    ) {
        var groups = await SymbolFinder.FindReferencesAsync(symbol: symbol, solution: solution);

        foreach (var group in groups
            .OrderBy(keySelector: static group => group.Definition.ToDisplayString(), comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: static group => (group.Definition.GetDocumentationCommentId() ?? string.Empty), comparer: StringComparer.Ordinal)) {
            // The search cascades: constructing a type reports under the constructor's group, and an
            // interface-dispatched call reports under the interface's. --strict keeps only the queried
            // symbol's own group and therefore hides both.
            if (options.Strict && !SymbolEqualityComparer.Default.Equals(x: group.Definition, y: symbol)) {
                continue;
            }

            var identity = Identity(symbol: group.Definition);
            var kind = group.Definition.Kind.ToString();
            var display = group.Definition.ToDisplayString();

            foreach (var location in group.Locations
                .Where(predicate: location => (!options.NoDoc || !IsInDocumentation(location: location.Location)))
                .Select(selector: static location => location.Location.GetLineSpan())
                .OrderBy(keySelector: static span => span.Path, comparer: StringComparer.Ordinal)
                .ThenBy(keySelector: static span => span.StartLinePosition.Line)
                .ThenBy(keySelector: static span => span.StartLinePosition.Character)) {
                _ = Add(
                    path: CliPaths.ToDisplay(fullPath: location.Path),
                    line: (location.StartLinePosition.Line + 1),
                    column: (location.StartLinePosition.Character + 1),
                    relation: "ref",
                    kind: kind,
                    name: display,
                    identity: identity,
                    records: records,
                    seen: seen);
            }
        }
    }
    private static async Task AddRelatedAsync(
        ISymbol symbol,
        Solution solution,
        ReferencesMode mode,
        List<AnalysisRecord> records,
        HashSet<(string, int, int, string)> seen
    ) {
        var related = mode switch {
            ReferencesMode.Implementers => await SymbolFinder.FindImplementationsAsync(symbol: symbol, solution: solution),
            ReferencesMode.Overrides => await SymbolFinder.FindOverridesAsync(symbol: symbol, solution: solution),
            _ => await FindDerivedAsync(solution: solution, symbol: symbol),
        };
        var relation = mode switch {
            ReferencesMode.Implementers => "impl",
            ReferencesMode.Overrides => "override",
            _ => "derived",
        };

        foreach (var found in related
            .OrderBy(keySelector: static found => found.ToDisplayString(), comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: static found => (found.GetDocumentationCommentId() ?? string.Empty), comparer: StringComparer.Ordinal)) {
            AddDeclarations(records: records, relation: relation, seen: seen, symbol: found);
        }
    }
    private static async Task<IEnumerable<ISymbol>> FindDerivedAsync(ISymbol symbol, Solution solution) {
        if (symbol is not INamedTypeSymbol type) {
            return [];
        }

        return ((type.TypeKind == TypeKind.Interface)
            ? await SymbolFinder.FindDerivedInterfacesAsync(type: type, solution: solution, transitive: true)
            : await SymbolFinder.FindDerivedClassesAsync(type: type, solution: solution, transitive: true));
    }
    private static bool Add(
        string path,
        int line,
        int column,
        string relation,
        string kind,
        string name,
        string identity,
        List<AnalysisRecord> records,
        HashSet<(string, int, int, string)> seen
    ) {
        if (!seen.Add(item: (path, line, column, identity))) {
            return false;
        }

        records.Add(item: new AnalysisRecord(Column: column, Detail: null, Kind: kind, Line: line, Name: name, Path: path, Relation: relation));

        return true;
    }
    // A stable identity key for a definition, used to tell two same-position records apart.
    private static string Identity(ISymbol symbol) =>
        (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString());
    // Whether a reference location sits inside XML documentation. `<see cref="..."/>` targets come back
    // as ordinary reference locations — neither implicit nor candidate — so a symbol whose only inbound
    // references are documentation crefs is not actually used by any code.
    private static bool IsInDocumentation(Location location) {
        if (location.SourceTree is not { } tree) {
            return false;
        }

        var token = tree.GetRoot().FindToken(position: location.SourceSpan.Start, findInsideTrivia: true);

        return (token.Parent?.FirstAncestorOrSelf<DocumentationCommentTriviaSyntax>() is not null);
    }
    private static ReferencesMode ParseMode(ArgScanner scanner) {
        if (scanner.Has(name: "declarations")) {
            return ReferencesMode.Declarations;
        }

        if (scanner.Has(name: "implementers")) {
            return ReferencesMode.Implementers;
        }

        if (scanner.Has(name: "overrides")) {
            return ReferencesMode.Overrides;
        }

        return (scanner.Has(name: "derived") ? ReferencesMode.Derived : ReferencesMode.References);
    }
    private static SymbolFilter? ParseFilter(string? raw) {
        if (raw is null) {
            return SymbolFilter.Type | SymbolFilter.Member;
        }

        var filter = SymbolFilter.None;

        foreach (var token in raw.Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: ',')) {
            switch (token.ToLowerInvariant()) {
                case "t" or "type":
                    filter |= SymbolFilter.Type;
                    break;
                case "m" or "member":
                    filter |= SymbolFilter.Member;
                    break;
                case "n" or "namespace":
                    filter |= SymbolFilter.Namespace;
                    break;
                default:
                    Console.Error.WriteLine(value: $"references: unknown kind '{token}' (known: type, member, namespace).");

                    return null;
            }
        }

        // A value whose tokens are all empty (`--kind ,`) names no category. Passing that on selects
        // nothing and the search rejects it outright, so it is a usage error like any other bad value.
        if (filter == SymbolFilter.None) {
            Console.Error.WriteLine(value: "references: --kind named no symbol kind (known: type, member, namespace).");

            return null;
        }

        return filter;
    }
    // The file the workspace loader is pointed at: an explicit --project or --solution, else the nearest solution
    // walking up from the working directory and then from the executable's own directory.
    private static string? ResolveTarget(ReferencesOptions options) {
        if (options.ProjectPath is { } project) {
            return Existing(path: project);
        }

        if (options.SolutionPath is { } solution) {
            return Existing(path: solution);
        }

        var discovered = (Ascend(start: Environment.CurrentDirectory) ?? Ascend(start: AppContext.BaseDirectory));

        if (discovered is null) {
            Console.Error.WriteLine(value: $"references: no solution found above {Environment.CurrentDirectory} (pass --solution or --project).");
        }

        return discovered;
    }
    private static string? Existing(string path) {
        var full = Path.GetFullPath(path: path);

        if (File.Exists(path: full)) {
            return full;
        }

        Console.Error.WriteLine(value: $"references: not found: {CliPaths.ToDisplay(fullPath: full)}");

        return null;
    }
    // .slnx first, .sln as the fallback. Extensions are compared exactly rather than by wildcard, because a
    // "*.sln" pattern also matches ".slnx" on Windows.
    private static string? Ascend(string start) =>
        CliPaths.AscendUntil(start: start, probe: static directory => (FirstWithExtension(directory: directory, extension: ".slnx") ?? FirstWithExtension(directory: directory, extension: ".sln")));
    private static string? FirstWithExtension(DirectoryInfo directory, string extension) {
        try {
            return directory.EnumerateFiles()
                .Select(selector: static file => file.FullName)
                .Where(predicate: file => Path.GetExtension(path: file).Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: extension))
                .OrderBy(keySelector: static file => file, comparer: StringComparer.Ordinal)
                .FirstOrDefault();
        } catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException)) {
            // A directory on the way up that cannot be listed is not this walk's problem; keep ascending.
            return null;
        }
    }

    // Load diagnostics arrive on the workspace's own threads; the failure flag is read once the load has
    // completed.
    private sealed class WorkspaceFailureSink {
        private int m_failures;

        public bool Failed => (Volatile.Read(location: ref m_failures) != 0);

        public void Report(WorkspaceDiagnosticEventArgs args) {
            Console.Error.WriteLine(value: $"references: workspace: {args.Diagnostic.Message}");

            if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) {
                _ = Interlocked.Exchange(location1: ref m_failures, value: 1);
            }
        }
    }

    private const string HelpText =
        """
        references <name>   references to a source symbol, solution-wide

          --declarations      declarations only, no reference search
          --implementers      implementations of an interface or interface member
          --overrides         overrides of a virtual/abstract member
          --derived           derived types
          --containing <frag> keep declarations whose display string contains frag (ordinal)
          --contains          treat <name> as a substring, not an exact simple name
          -i                  case-insensitive name match
          --kind <k,k>        type, member, namespace (default: type,member)
          --solution <path>   default: the nearest .slnx walking up from the cwd
          --project <path>    load one project instead (narrows the closure; see below)
          --configuration <c> build configuration (default Debug)
          --metadata          also match declarations from referenced assemblies
          --no-doc            drop locations inside documentation trivia
          --strict            keep only locations whose group definition IS the queried symbol
          --allow-partial     report anyway after a workspace load failure
          --json              one JSON object per line instead of text
          -q                  quiet: exit code only
          -h / --help         this text

        Output is `path:line:col decl|ref <symbol kind> <resolved definition>`, grouped
        by definition and sorted by position within a group. The symbol on a `ref` line
        is the definition the compiler resolved, which is not always the one queried:
        constructing a type reports under its constructor, and an interface-dispatched
        call reports under the interface. `<see cref="..."/>` targets are ordinary
        references — pass --no-doc for dead-code work.

        This tier sees only what the project system compiles. Files removed from
        compilation and files in no project are invisible to it; `puck search` and
        `puck declarations` see them. Loading runs a design-time build, which writes obj/ in
        every project and needs the solution restored.
        Exit codes: 0 a declaration matched, 1 none did, 2 usage error or load failure.
        """;
}
