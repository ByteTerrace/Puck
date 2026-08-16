using Puck.Cli.Source;

namespace Puck.Cli.Analysis;

// The `puck declarations` verb: a declaration inventory read straight off the parsed syntax, with no build and
// no restore. It answers structure questions — what is declared, where, with what base list or attribute
// — and it is the tier that can see files no project compiles. Exit 0 when anything was found, 1 when
// nothing was, 2 on a usage error.
internal static class DeclarationsCommand {
    public static int Run(string[] args) {
        var scanner = new ArgScanner()
            .Value(name: "g").Value(name: "not").Value(name: "kind").Value(name: "name").Value(name: "base").Value(name: "attribute")
            .Flag(name: "members").Flag(name: "doc").Flag(name: "json").Flag(name: "q").Flag(name: "h").Flag(name: "help");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"declarations: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        var kinds = ParseKinds(raw: scanner.Get(name: "kind"));

        if (kinds is null) {
            return 2;
        }

        var options = new DeclarationsOptions {
            Attribute = scanner.Get(name: "attribute"),
            Base = scanner.Get(name: "base"),
            Doc = scanner.Has(name: "doc"),
            Exclude = [.. scanner.GetAll(name: "not").Select(selector: static glob => new CliGlob(glob: glob))],
            Include = [.. scanner.GetAll(name: "g").Select(selector: static glob => new CliGlob(glob: glob))],
            Json = scanner.Has(name: "json"),
            Kinds = kinds,
            Members = scanner.Has(name: "members"),
            Name = scanner.Get(name: "name"),
            Quiet = scanner.Has(name: "q"),
            Roots = ((scanner.Positionals.Count > 0) ? scanner.Positionals : ["."]),
        };

        var files = FileWalk.Enumerate(
            verb: "declarations",
            roots: options.Roots,
            include: options.Include,
            exclude: options.Exclude,
            extension: ".cs");

        if (files is null) {
            return 2;
        }

        var corpus = SourceCorpus.Parse(files: files, relativeTo: Directory.GetCurrentDirectory());

        return AnalysisEmitter.Emit(records: DeclarationsWalker.Collect(corpus: corpus, options: options), json: options.Json, quiet: options.Quiet);
    }

    // The requested kinds, empty for "every type kind", or null with the error already written.
    private static IReadOnlySet<string>? ParseKinds(string? raw) {
        if (raw is null) {
            return new HashSet<string>(comparer: StringComparer.Ordinal);
        }

        var requested = raw.Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: ',')
            .Select(selector: static kind => kind.ToLowerInvariant())
            .ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var kind in requested) {
            if (!DeclarationsWalker.TypeKinds.Contains(item: kind) && !DeclarationsWalker.MemberKinds.Contains(item: kind)) {
                Console.Error.WriteLine(
                    value: $"declarations: unknown kind '{kind}' (known: {string.Join(separator: ", ", values: DeclarationsWalker.TypeKinds.Concat(second: DeclarationsWalker.MemberKinds).Order(comparer: StringComparer.Ordinal))}).");

                return null;
            }
        }

        return requested;
    }

    private const string HelpText =
        """
        declarations [path ...]   declaration inventory, parse-only (default path: cwd)

          -g <glob>          include glob (repeatable; no '/' matches basename)
          --not <glob>       exclude glob (repeatable; no '/' matches a file OR directory basename)
          --kind <k,k>       class, struct, record, interface, enum, delegate,
                             method, property, field, event, ctor
          --name <frag>      declared simple name contains frag (ordinal)
          --base <frag>      base list contains frag (types only)
          --attribute <frag> an attribute name contains frag
          --members          list members inside each type (implied by a member --kind)
          --doc              also emit XML-doc cref targets, filtered by --name alone
          --json             one JSON object per line instead of text
          -q                 quiet: exit code only
          -h / --help        this text

        Output is `path:line:col decl <kind> <qualified name>[ : <base list>]`, sorted
        by path then position. Both record forms report the kind `record`. The walk
        prunes .git, artifacts, bin, obj, node_modules, publish,
        BenchmarkDotNet.Artifacts, and agent worktrees under .claude/worktrees (name
        one as a root to inventory it).
        This tier reads syntax, not symbols: it sees every .cs file on disk, including
        the ones no project compiles, but it matches names rather than resolving them.
        Use `puck references` for who-uses-what.
        Exit codes: 0 found, 1 nothing found, 2 usage error.
        """;
}
