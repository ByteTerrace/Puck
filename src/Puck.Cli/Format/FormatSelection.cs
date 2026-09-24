using Puck.Cli.Source;

namespace Puck.Cli.Format;

// Explicit targets never widen a rewrite to sibling or linked source files. Project trees still
// supply the semantic context; the selected paths alone supply the write set.
internal static class FormatSelection {
    // The sources a --file-list names, each resolved against `root` and required to be a formattable file reached
    // through no symbolic link.
    internal static string[] Read(string root, string manifest) {
        root = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: root));
        if (!Directory.Exists(path: root)) { throw new ArgumentException(message: $"Format root not found: {CliPaths.ToDisplay(fullPath: root)}"); }
        var paths = CliOptions.ReadFileList(
            baseDirectory: root,
            manifest: manifest
        );

        foreach (var path in paths) {
            var relative = CliPaths.ToDisplay(
                fullPath: path,
                relativeTo: root
            );

            if (
                !FormatSources.IsFormattable(path: path) ||
                !File.Exists(path: path)
            ) {
                throw new ArgumentException(message: $"Not a C# or .puck source on disk: {relative}");
            }
            for (var current = path; (current != root); current = Path.GetDirectoryName(path: current)!) {
                if ((File.GetAttributes(path: current) & FileAttributes.ReparsePoint) != 0) {
                    throw new ArgumentException(message: $"Formatting through a symbolic link is unsupported: {relative}");
                }
            }
        }
        return paths;
    }
    internal static int Run(string root, string configuration, string manifest, HashSet<string> selected, bool check) =>
        Run(
            check: check,
            configuration: configuration,
            root: root,
            selected: selected,
            targets: Read(
                manifest: manifest,
                root: root
            )
        );
    // Formats `targets` (full paths under `root`): each .puck source through the printer, and the C# sources through
    // the Roslyn phases. A file-based app (`#!` or `#:` directives) is formatted through a disposable project built from
    // its directives. A source outside every project directory needs no project for whitespace and the syntactic
    // passes; for a semantic pass it joins the project that compiles it, and one no project compiles goes through a
    // disposable project too.
    internal static int Run(string root, string configuration, string[] targets, HashSet<string> selected, bool check) {
        var closures = new CompileClosures();
        var standalone = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var orphans = new List<string>();
        // C# sources are formatted, phase 0 included, only when the selection names a C# pass.
        string[] sources = (FormatPasses.All.Any(predicate: pass => ((pass.Kind != FormatPassKind.Puck) && selected.Contains(item: pass.Name)))
            ? [.. targets.Where(predicate: FormatSources.IsCSharp)]
            : []);

        foreach (var target in sources) {
            if (File.ReadLines(path: target).Any(predicate: static line => (line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "#!"
            ) || line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "#:"
            )))) {
                standalone.Add(item: target);
            } else if (SourceFiles.FindOwningProjectDirectory(start: Path.GetDirectoryName(path: target)!) is null) {
                orphans.Add(item: target);
            }
        }

        if (
            (orphans.Count > 0) &&
            FormatPasses.All.Any(predicate: pass => (pass.Semantic && selected.Contains(item: pass.Name)))
        ) {
            var hosts = SemanticPhases.Hosts(
                closures: closures,
                configuration: configuration,
                orphans: orphans,
                projectRoots: []
            );

            standalone.UnionWith(other: orphans.Where(predicate: orphan => !hosts.ContainsKey(key: orphan)));
        }

        string[] ordinary = [.. sources.Where(predicate: target => !standalone.Contains(item: target))];
        var result = ((ordinary.Length == 0)
            ? CliExit.Success
            : FormatCommand.RunPhases(
                check: check,
                closures: closures,
                configuration: configuration,
                root: root,
                selected: selected,
                targets: ordinary
            )
        );

        // A disposable project that does not build, or has no Puck checkout to build in, skips its one source, named, like
        // a project whose closure cannot be trusted; the rest of the run still reports.
        foreach (var target in sources.Where(predicate: standalone.Contains)) {
            int code;

            try {
                code = FormatFileProject.Run(
                    check: check,
                    file: target,
                    selected: selected
                );
            } catch (Exception error) when ((error is InvalidOperationException or DirectoryNotFoundException)) {
                Console.Error.WriteLine(value: $"format: {CliPaths.ToDisplay(fullPath: target)}: {error.Message} — SKIPPED");
                code = CliExit.Failed;
            }

            result = Math.Max(
                val1: result,
                val2: code
            );
        }

        string[] puck = [.. targets.Where(predicate: FormatSources.IsPuck)];

        if (
            (puck.Length > 0) &&
            selected.Contains(item: "puck")
        ) {
            result = Math.Max(
                val1: result,
                val2: PuckSourcePhase.Run(
                    check: check,
                    files: puck
                )
            );
        }
        return result;
    }
}
