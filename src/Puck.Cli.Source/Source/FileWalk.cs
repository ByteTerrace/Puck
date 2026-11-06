namespace Puck.Cli.Source;

// The file walk shared by the verbs that address the tree by path. Inside a git work tree it reads git's view of
// the tree: the files git tracks plus the untracked files no ignore rule hides (`git ls-files --cached --others
// --exclude-standard`), so ignored scratch, build output and agent worktrees never answer as live code, whatever
// directory holds them. Outside a work tree, or under a root git itself ignores, it walks the disk and prunes
// SkipDirectories. The include/exclude globs, an optional extension filter and an optional per-file gate decide
// the rest. The result is ordinal-sorted absolute paths, each file admitted exactly once.
public static class FileWalk {
    // The disk walk's prune set: generated, vendored and artifact directories, pruned at every depth below a root
    // that is outside a git work tree or that git ignores. A root inside a work tree uses git's ignore rules
    // instead. Naming one as a root still walks it, and so does naming a file inside it.
    public static readonly HashSet<string> SkipDirectories = new(comparer: StringComparer.OrdinalIgnoreCase) {
        ".git", ".tmp", "artifacts", "BenchmarkDotNet.Artifacts", "bin", "node_modules", "obj", "publish",
    };

    // The walk as a verb's help describes it, so no help text keeps a copy of the rules or the prune list.
    internal static string WalkSummary => $"""
        The walk reads git's view of the tree: tracked files and untracked files no ignore rule
        hides, so ignored scratch, build output and agent worktrees are left out. Outside a git
        work tree, or under a root git ignores, it walks the disk instead and skips {string.Join(
            separator: ", ",
            values: SkipDirectories.Order(comparer: StringComparer.OrdinalIgnoreCase)
        )},
        agent worktrees under .claude/worktrees and directory links. Name an ignored directory
        or file as a root to read it anyway.
        """;

    private static bool HasExtension(string path, string? extension) =>
        ((extension is null) || path.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: extension
        ));
    // The `.claude/worktrees` pair, matched as a pair so a `worktrees` directory anywhere else stays in.
    private static bool IsAgentWorktreeRoot(string parent, string name) =>
        (string.Equals(
            a: name,
            b: "worktrees",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )
        && string.Equals(
            a: Path.GetFileName(path: parent),
            b: ".claude",
            comparisonType: StringComparison.OrdinalIgnoreCase
        ));
    private static bool MatchesAny(IReadOnlyList<CliGlob> globs, string fullPath) {
        if (globs.Count == 0) {
            return false;
        }

        var rel = CliPaths.RelForGlob(fullPath: fullPath);
        var name = Path.GetFileName(path: fullPath);

        foreach (var g in globs) {
            if (g.IsMatch(value: (g.BasenameOnly
                ? name
                : rel))) {
                return true;
            }
        }

        return false;
    }
    // Directory-name form of MatchesAny: only the basename globs apply, since a path-form glob is written
    // against a file's relative path.
    private static bool MatchesAnyName(IReadOnlyList<CliGlob> globs, string name) {
        foreach (var g in globs) {
            if (
                g.BasenameOnly &&
                g.IsMatch(value: name)
            ) {
                return true;
            }
        }

        return false;
    }
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
                WarnUnreadable(
                    exception: ex,
                    path: current,
                    verb: verb
                );

                continue;
            }

            foreach (var entry in entries) {
                if (Directory.Exists(path: entry)) {
                    var name = Path.GetFileName(path: entry);

                    // A '/'-less --not glob names a basename, and a directory has one too: pruning here is
                    // what makes `--not publish` exclude the directory rather than nothing. Path-form globs
                    // stay a file filter. Agent worktrees (.claude/worktrees) hold live duplicate checkouts
                    // whose copies answer a query as if they were live consumers, so they are pruned for
                    // every verb; naming one as a root still walks it. A directory link (a junction or symbolic
                    // link) is never followed: one pointing into the tree answers every query twice, and one whose
                    // target is gone cannot be read at all.
                    if (
                        !SkipDirectories.Contains(item: name) &&
                        (0 == (File.GetAttributes(path: entry) & FileAttributes.ReparsePoint)) &&
                        !MatchesAnyName(
                        globs: exclude,
                        name: name
                    ) &&
                        !IsAgentWorktreeRoot(
                        name: name,
                        parent: current
                    )
                    ) {
                        stack.Push(item: entry);
                    }

                    continue;
                }

                AdmitWalked(
                    acc: acc,
                    admit: admit,
                    entry: entry,
                    exclude: exclude,
                    extension: extension,
                    include: include,
                    seen: seen
                );
            }
        }
    }
    // The file filter both walks share: extension, include and exclude globs, the once-per-path set and the gate.
    private static void AdmitWalked(
        string entry,
        IReadOnlyList<CliGlob> include,
        IReadOnlyList<CliGlob> exclude,
        string? extension,
        Func<string, bool>? admit,
        List<string> acc,
        HashSet<string> seen
    ) {
        if (
            HasExtension(
                extension: extension,
                path: entry
            ) &&
            ((include.Count == 0) || MatchesAny(
                fullPath: entry,
                globs: include
            )) &&
            !MatchesAny(
                fullPath: entry,
                globs: exclude
            ) &&
            seen.Add(item: entry) &&
            (admit?.Invoke(arg: entry) ?? true)
        ) {
            acc.Add(item: entry);
        }
    }
    // Git's view of the files under `dir`, relative to it with forward slashes, or false when the disk walk owns
    // `dir`: it is outside a work tree, git ignores it (a root named on purpose, such as a scratch or bin
    // directory), or git cannot run.
    private static bool TryListGitView(string dir, out string[] relative) {
        relative = [];

        try {
            // check-ignore answers 0 for an ignored path, 1 for a kept one, and 128 outside a work tree.
            if (CliGit.Run(dir, "check-ignore", "-q", ".").ExitCode != 1) {
                return false;
            }

            var listing = CliGit.Run(dir, "ls-files", "-z", "--cached", "--others", "--exclude-standard");

            if (listing.ExitCode != 0) {
                return false;
            }

            relative = listing.Stdout.Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: '\0'
            );

            return true;
        } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) {
            return false;
        }
    }
    private static void WalkGitView(
        string dir,
        string[] relative,
        IReadOnlyList<CliGlob> include,
        IReadOnlyList<CliGlob> exclude,
        string? extension,
        Func<string, bool>? admit,
        List<string> acc,
        HashSet<string> seen
    ) {
        foreach (var path in relative) {
            // A trailing slash names an untracked nested repository, which git lists as one entry and never enters.
            if (path.EndsWith(value: '/')) {
                continue;
            }

            var segments = path.Split(separator: '/');
            var prunedByName = false;

            // A '/'-less --not glob prunes a directory by its basename, as the disk walk does.
            for (var i = 0; (i < (segments.Length - 1)) && !prunedByName; i++) {
                prunedByName = MatchesAnyName(
                    globs: exclude,
                    name: segments[i]
                );
            }

            if (prunedByName) {
                continue;
            }

            var entry = Path.GetFullPath(path: Path.Combine(
                path1: dir,
                path2: path
            ));

            // A tracked file deleted from the working tree, a submodule's gitlink and a directory link are not files.
            if (!File.Exists(path: entry)) {
                continue;
            }

            AdmitWalked(
                acc: acc,
                admit: admit,
                entry: entry,
                exclude: exclude,
                extension: extension,
                include: include,
                seen: seen
            );
        }
    }

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
                if (!HasExtension(
                    extension: extension,
                    path: full
                )) {
                    Console.Error.WriteLine(value: $"{verb}: skipping {CliPaths.ToDisplay(fullPath: full)}: not a {extension} file.");

                    continue;
                }

                if (
                    !MatchesAny(
                    fullPath: full,
                    globs: exclude
                ) &&
                    seen.Add(item: full) &&
                    (admit?.Invoke(arg: full) ?? true)
                ) {
                    acc.Add(item: full);
                }

                continue;
            }

            if (!Directory.Exists(path: full)) {
                Console.Error.WriteLine(value: $"{verb}: no such file or directory: {CliPaths.ToDisplay(fullPath: full)}");

                missing = true;

                continue;
            }

            if (TryListGitView(
                dir: full,
                relative: out var relative
            )) {
                WalkGitView(
                    acc: acc,
                    admit: admit,
                    dir: full,
                    exclude: exclude,
                    extension: extension,
                    include: include,
                    relative: relative,
                    seen: seen
                );

                continue;
            }

            WalkDirectory(
                acc: acc,
                admit: admit,
                dir: full,
                exclude: exclude,
                extension: extension,
                include: include,
                seen: seen,
                verb: verb
            );
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
}
