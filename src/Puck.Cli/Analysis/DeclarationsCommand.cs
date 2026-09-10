using System.CommandLine;

using Puck.Cli.Source;

namespace Puck.Cli.Analysis;

// The `puck declarations` verb: a declaration inventory read straight off the parsed syntax, with no build and
// no restore. It answers structure questions — what is declared, where, with what base list or attribute
// — and it is the tier that can see files no project compiles. Exit 0 when anything was found, 1 when
// nothing was, 2 on a usage error.
internal static class DeclarationsCommand {
    public static Command Create() {
        var attributeOption = new Option<string?>(name: "--attribute") { Description = "Keep declarations carrying an attribute whose name contains this fragment (ordinal)." };
        var baseOption = new Option<string?>(name: "--base") { Description = "Keep declarations whose base list contains this fragment (ordinal); types only." };
        var docOption = new Option<bool>(name: "--doc") { Description = "Also emit XML-doc cref targets, filtered by --name alone." };
        var excludeOption = new Option<string[]>(name: "--not") { DefaultValueFactory = static _ => [], Description = "Exclude glob (repeatable; a glob with no '/' matches a file OR directory basename)." };
        var includeOption = new Option<string[]>(name: "-g") { DefaultValueFactory = static _ => [], Description = "Include glob (repeatable; a glob with no '/' matches the basename)." };
        var jsonOption = new Option<bool>(name: "--json") { Description = "One JSON object per line instead of text." };
        var kindOption = new Option<string?>(name: "--kind") { Description = "Comma-separated kinds: class, struct, record, interface, enum, delegate, method, property, field, event, ctor. Absent means every type kind." };
        var membersOption = new Option<bool>(name: "--members") { Description = "List members inside each type (implied by a member --kind)." };
        var nameOption = new Option<string?>(name: "--name") { Description = "Keep declarations whose declared simple name contains this fragment (ordinal)." };
        var quietOption = new Option<bool>(name: "-q") { Description = "Quiet: exit code only." };
        var rootsArgument = new Argument<string[]>(name: "path") {
            Arity = ArgumentArity.ZeroOrMore,
            DefaultValueFactory = static _ => ["."],
            Description = "Roots to walk; the working directory when none is given.",
        };
        var command = new Command(description: """
            Declaration inventory read off the parsed syntax, with no build and no restore.

            Output is `path:line:col decl <kind> <qualified name>[ : <base list>]`, sorted
            by path then position. Both record forms report the kind `record`. The walk
            prunes .git, artifacts, bin, obj, node_modules, publish,
            BenchmarkDotNet.Artifacts, and agent worktrees under .claude/worktrees (name
            one as a root to inventory it).
            This tier reads syntax, not symbols: it sees every .cs file on disk, including
            the ones no project compiles, but it matches names rather than resolving them.
            Use `puck references` for who-uses-what.
            Exit codes: 0 found, 1 nothing found, 2 usage error.
            """, name: "declarations") {
            rootsArgument,
            attributeOption,
            baseOption,
            docOption,
            excludeOption,
            includeOption,
            jsonOption,
            kindOption,
            membersOption,
            nameOption,
            quietOption,
        };

        command.SetAction(action: parseResult => Run(
            attribute: parseResult.GetValue(option: attributeOption),
            baseFragment: parseResult.GetValue(option: baseOption),
            doc: parseResult.GetValue(option: docOption),
            exclude: parseResult.GetRequiredValue(option: excludeOption),
            include: parseResult.GetRequiredValue(option: includeOption),
            json: parseResult.GetValue(option: jsonOption),
            kind: parseResult.GetValue(option: kindOption),
            members: parseResult.GetValue(option: membersOption),
            name: parseResult.GetValue(option: nameOption),
            quiet: parseResult.GetValue(option: quietOption),
            roots: parseResult.GetRequiredValue(argument: rootsArgument)));

        return command;
    }

    private static int Run(
        string? attribute,
        string? baseFragment,
        bool doc,
        string[] exclude,
        string[] include,
        bool json,
        string? kind,
        bool members,
        string? name,
        bool quiet,
        string[] roots) {
        var kinds = ParseKinds(raw: kind);

        if (kinds is null) {
            return 2;
        }

        var options = new DeclarationsOptions {
            Attribute = attribute,
            Base = baseFragment,
            Doc = doc,
            Exclude = [.. exclude.Select(selector: static glob => new CliGlob(glob: glob))],
            Include = [.. include.Select(selector: static glob => new CliGlob(glob: glob))],
            Json = json,
            Kinds = kinds,
            Members = members,
            Name = name,
            Quiet = quiet,
            Roots = roots,
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
}
