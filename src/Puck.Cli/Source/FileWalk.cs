namespace Puck.Cli.Source;

// The recursive file walk shared by the verbs that address the tree by path — the search verb's content
// search and the declarations inventory. Generated, vendored and artifact directories are pruned at every
// depth; the include/exclude globs, an optional extension filter and an optional per-file gate decide
// the rest. The result is ordinal-sorted absolute paths, each file admitted exactly once.
internal static class FileWalk {
    // Pruned at every depth of the walk, and below the scan root of the scan/format corpus
    // (SourceFiles). Naming one as a search root still searches it, and so does naming a file inside it.
    internal static readonly HashSet<string> SkipDirectories = new(comparer: StringComparer.OrdinalIgnoreCase) {
        ".git", "artifacts", "BenchmarkDotNet.Artifacts", "bin", "node_modules", "obj", "publish",
    };

    // The admitted files, or null when a root names nothing on disk — a mistyped path would otherwise
    // answer exactly the way a tree with no matches does, and the caller turns null into a usage error.
    // Every bad root is reported before the walk gives up, so one run names them all.
    public static List<string>? Enumerate(
        string verb,
        IReadOnlyList<string> roots,
        IReadOnlyList<CliGlob> include,
        IReadOnlyList<CliGlob> exclude,
        string? extension = null,
        Func<string, bool>? admit = null
    ) {
        var acc = new List<string>();

        // Overlapping roots (`search X src src/Puck.Maths`) reach the same file from more than one of them;
        // admitting each full path once keeps counts, listings and result budgets at one entry per file.
        // Windows paths compare case-insensitively.
        var seen = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var missing = false;

        foreach (var root in roots) {
            // Path.GetFullPath throws on an empty argument; a scripted call with an unset variable gets
            // the missing-path channel instead of a stack trace.
            if (string.IsNullOrWhiteSpace(value: root)) {
                Console.Error.WriteLine(value: $"{verb}: no such file or directory: (empty)");

                missing = true;

                continue;
            }

            var full = Path.GetFullPath(path: root);

            if (File.Exists(path: full)) {
                // An explicitly named file bypasses include-globs (the caller was explicit) but still
                // honors the extension filter, --not, and the per-file gate. A named file of the wrong
                // extension is called out rather than dropped, but does not fail the run: a caller may
                // legitimately hand a mixed list over.
                if (!HasExtension(path: full, extension: extension)) {
                    Console.Error.WriteLine(value: $"{verb}: skipping {CliPaths.ToDisplay(fullPath: full)}: not a {extension} file.");

                    continue;
                }

                if (!MatchesAny(globs: exclude, fullPath: full)
                    && seen.Add(item: full)
                    && (admit?.Invoke(arg: full) ?? true)) {
                    acc.Add(item: full);
                }

                continue;
            }

            if (!Directory.Exists(path: full)) {
                Console.Error.WriteLine(value: $"{verb}: no such file or directory: {CliPaths.ToDisplay(fullPath: full)}");

                missing = true;

                continue;
            }

            WalkDirectory(verb: verb, dir: full, include: include, exclude: exclude, extension: extension, admit: admit, acc: acc, seen: seen);
        }

        if (missing) {
            return null;
        }

        acc.Sort(comparer: StringComparer.Ordinal);

        return acc;
    }

    // The per-path failure channel shared by enumeration and by the callers' own file reads: one stderr
    // line, the walk continues, and the exit code still reports only whether anything was found.
    public static void WarnUnreadable(string verb, string path, Exception exception) =>
        Console.Error.WriteLine(value: $"{verb}: cannot read {CliPaths.ToDisplay(fullPath: path)}: {exception.Message}");

    private static void WalkDirectory(
        string verb,
        string dir,
        IReadOnlyList<CliGlob> include,
        IReadOnlyList<CliGlob> exclude,
        string? extension,
        Func<string, bool>? admit,
        List<string> acc,
        HashSet<string> seen
    ) {
        var stack = new Stack<string>();

        stack.Push(item: dir);

        while (stack.Count > 0) {
            var current = stack.Pop();
            IEnumerable<string> entries;

            try {
                entries = Directory.EnumerateFileSystemEntries(path: current);
            } catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException)) {
                WarnUnreadable(verb: verb, path: current, exception: ex);

                continue;
            }

            foreach (var entry in entries) {
                if (Directory.Exists(path: entry)) {
                    var name = Path.GetFileName(path: entry);

                    // A '/'-less --not glob names a basename, and a directory has one too: pruning here is
                    // what makes `--not publish` exclude the directory rather than nothing. Path-form globs
                    // stay a file filter. Agent worktrees (.claude/worktrees) hold live duplicate checkouts
                    // whose copies answer a query as if they were live consumers, so they are pruned for
                    // every verb; naming one as a root still walks it.
                    if (!SkipDirectories.Contains(item: name)
                        && !MatchesAnyName(globs: exclude, name: name)
                        && !IsAgentWorktreeRoot(parent: current, name: name)) {
                        stack.Push(item: entry);
                    }

                    continue;
                }

                if (!HasExtension(path: entry, extension: extension)) {
                    continue;
                }

                if ((include.Count > 0) && !MatchesAny(globs: include, fullPath: entry)) {
                    continue;
                }

                if (MatchesAny(globs: exclude, fullPath: entry)) {
                    continue;
                }

                if (seen.Add(item: entry) && (admit?.Invoke(arg: entry) ?? true)) {
                    acc.Add(item: entry);
                }
            }
        }
    }

    // The `.claude/worktrees` pair, matched as a pair so a `worktrees` directory anywhere else stays in.
    private static bool IsAgentWorktreeRoot(string parent, string name) =>
        (string.Equals(a: name, b: "worktrees", comparisonType: StringComparison.OrdinalIgnoreCase)
        && string.Equals(a: Path.GetFileName(path: parent), b: ".claude", comparisonType: StringComparison.OrdinalIgnoreCase));
    private static bool HasExtension(string path, string? extension) =>
        ((extension is null) || path.EndsWith(value: extension, comparisonType: StringComparison.OrdinalIgnoreCase));
    private static bool MatchesAny(IReadOnlyList<CliGlob> globs, string fullPath) {
        if (globs.Count == 0) {
            return false;
        }

        var rel = CliPaths.RelForGlob(fullPath: fullPath);
        var name = Path.GetFileName(path: fullPath);

        foreach (var g in globs) {
            if (g.IsMatch(value: (g.BasenameOnly ? name : rel))) {
                return true;
            }
        }

        return false;
    }

    // Directory-name form of MatchesAny: only the basename globs apply, since a path-form glob is written
    // against a file's relative path.
    private static bool MatchesAnyName(IReadOnlyList<CliGlob> globs, string name) {
        foreach (var g in globs) {
            if (g.BasenameOnly && g.IsMatch(value: name)) {
                return true;
            }
        }

        return false;
    }
}
