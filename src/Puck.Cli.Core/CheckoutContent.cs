using Puck.Abstractions;
using System.Security.Cryptography;
using System.Text;
using Puck.Assets;
using Puck.Confinement;

namespace Puck.Cli;

/// <summary>
/// Reads what a checkout holds under a set of roots, for identities computed from their inputs at run time and never
/// recorded: git's committed entry for each root in <c>HEAD</c> (a directory's tree id covers every committed file
/// inside it), and, for every path under a root that <c>git status</c> reports as modified, staged, deleted or
/// untracked, the path, its status and the SHA-256 of the bytes on disk. Files git ignores are not inputs.
/// A tree with no <c>.git</c> entry of its own, such as a <c>git archive</c> extraction (even one inside another
/// repository), has no commit to read: every file under a root that the tree's own <c>.gitignore</c> rules keep (and
/// never <c>bin</c> or <c>obj</c> output) is read from disk as a change, so ignored output a build writes beside its
/// sources is no input.
/// <para>
/// Two checkouts that read the same snapshot hold the same content under those roots. The converse does not hold:
/// the same bytes committed in one checkout and uncommitted in another read differently, so a difference is a reason
/// to look, not proof of different bytes.
/// </para>
/// </summary>
public static class CheckoutContent {
    /// <summary>One path <c>git status</c> reports under a root.</summary>
    /// <param name="Entry">The porcelain entry, <c>XY path</c>.</param>
    /// <param name="Path">The path, forward-slashed and relative to the checkout.</param>
    /// <param name="Content">The SHA-256 of the bytes on disk, or <c>absent</c> for a deleted file.</param>
    public sealed record Change(string Entry, string Path, string Content);
    /// <summary>What a checkout holds under a set of roots.</summary>
    /// <param name="Committed">The raw <c>git ls-tree -z HEAD -- roots</c> listing.</param>
    /// <param name="Changes">The changed paths, ordered by entry.</param>
    public sealed record Snapshot(string Committed, IReadOnlyList<Change> Changes) {
        /// <summary>Hashes what the snapshot holds under one of its roots alone.</summary>
        /// <param name="root">A root the snapshot was read over, such as <c>.claude/agents</c>.</param>
        /// <returns>32 lowercase hexadecimal digits; a root absent from both <c>HEAD</c> and the disk hashes to the
        /// same value in every checkout.</returns>
        public string KeyOf(string root) {
            using var hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

            void Append(string text) =>
                hash.AppendData(data: Encoding.UTF8.GetBytes(s: $"{text}\0"));

            Append(text: root);
            foreach (var line in Committed.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0')) {
                var tab = line.IndexOf(value: '\t');

                if ((tab >= 0) && (line[(tab + 1)..] == root)) { Append(text: line); }
            }
            foreach (var change in Changes) {
                if ((change.Path == root) || change.Path.StartsWith(comparisonType: StringComparison.Ordinal, value: (root + "/"))) { Append(text: $"{change.Entry}\0{change.Content}"); }
            }
            return Convert.ToHexStringLower(inArray: hash.GetHashAndReset()[..16]);
        }
    }

    // The files under the roots that the tree's own ignore rules keep, as git judges them: an archive carries its
    // .gitignore files, so a throwaway empty repository pointed at the tree lists exactly what a checkout of it would
    // not ignore. A build that writes ignored output beside its sources (compiled shaders) then changes no input. Null
    // when git cannot answer, which leaves the bin/obj prune alone.
    private static HashSet<string>? Unignored(string repositoryRoot, IReadOnlyList<string> roots) {
        DirectoryInfo? index = null;

        try {
            index = Directory.CreateTempSubdirectory(prefix: "puck-tree-index-");
            if (!TryGit(arguments: ["init", "--bare", "--quiet"], reason: out _, repositoryRoot: index.FullName, stdout: out _)
                || !TryGit(
                    arguments: [$"--git-dir={index.FullName}", "--work-tree=.", "-c", "maintenance.auto=false", "-c", "gc.auto=0", "--literal-pathspecs", "ls-files", "-z", "--others", "--exclude-standard", "--", .. roots],
                    reason: out _,
                    repositoryRoot: repositoryRoot,
                    stdout: out var listed
                )) {
                return null;
            }

            return listed.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0').ToHashSet(comparer: StringComparer.Ordinal);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        } finally {
            if (index is not null) {
                try { ConfinedDirectory.RemoveTree(path: index.FullName); } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) { }
            }
        }
    }
    // The tree's own bytes: every file under a root that the tree's ignore rules keep is a change, hashed from disk;
    // build output (bin, obj) is left out even when git cannot judge the rules.
    private static bool TryReadDisk(string repositoryRoot, IReadOnlyList<string> roots, out Snapshot snapshot, out string reason) {
        snapshot = new Snapshot(Changes: [], Committed: string.Empty);

        var changes = new SortedDictionary<string, Change>(comparer: StringComparer.Ordinal);
        var kept = Unignored(repositoryRoot: repositoryRoot, roots: roots);

        void Add(string relative) {
            if ((kept is not null) && !kept.Contains(item: relative)) { return; }
            var content = ContentPin.OfFile(path: Path.Combine(
                path1: repositoryRoot,
                path2: relative
            )).Hex;

            changes[relative] = new Change(Content: content, Entry: $"tree {relative}", Path: relative);
        }

        try {
            foreach (var root in roots) {
                var full = Path.Combine(
                    path1: repositoryRoot,
                    path2: root
                );

                if (File.Exists(path: full)) {
                    Add(relative: root);
                } else if (Directory.Exists(path: full)) {
                    var pending = new Stack<string>();

                    pending.Push(item: root);

                    while (pending.TryPop(result: out var directory)) {
                        var absolute = Path.Combine(
                            path1: repositoryRoot,
                            path2: directory
                        );

                        foreach (var file in Directory.EnumerateFiles(path: absolute)) {
                            Add(relative: $"{directory}/{Path.GetFileName(path: file)}");
                        }
                        foreach (var child in Directory.EnumerateDirectories(path: absolute)) {
                            var name = Path.GetFileName(path: child);

                            if (name is not ("bin" or "obj")) {
                                pending.Push(item: $"{directory}/{name}");
                            }
                        }
                    }
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = $"could not read the tree's files: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        snapshot = new Snapshot(Changes: [.. changes.Values], Committed: string.Empty);
        reason = string.Empty;

        return true;
    }
    private static bool TryGit(string repositoryRoot, IReadOnlyList<string> arguments, out string stdout, out string reason) {
        ChildProcessResult result;

        try {
            result = CliGit.Run(
                arguments: [.. arguments],
                repository: repositoryRoot
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            stdout = string.Empty;
            reason = $"git could not start: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        stdout = result.Stdout;

        if (result.ExitCode != 0) {
            reason = $"git {arguments.First(predicate: static argument => !argument.StartsWith(value: '-'))} exited {result.ExitCode}: {result.Stderr.Trim().ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    /// <summary>Reads what the checkout holds under <paramref name="roots"/>.</summary>
    /// <param name="repositoryRoot">The checkout, which must be a git working tree with a <c>HEAD</c> commit.</param>
    /// <param name="roots">The paths to read, relative to the checkout.</param>
    /// <param name="snapshot">What the checkout holds, or an empty snapshot on failure.</param>
    /// <param name="reason">Why the checkout could not be read, or empty on success.</param>
    /// <returns><see langword="true"/> when both git queries answered and every changed file could be read.</returns>
    public static bool TryRead(string repositoryRoot, IReadOnlyList<string> roots, out Snapshot snapshot, out string reason) {
        snapshot = new Snapshot(Changes: [], Committed: string.Empty);

        // A tree with no .git entry of its own (a git archive extraction) would make git resolve the enclosing
        // repository, whose HEAD and status say nothing about this tree's files, so such a tree is read from its disk.
        if (!CliGit.HasOwnGitEntry(repositoryRoot: repositoryRoot)) {
            return TryReadDisk(reason: out reason, repositoryRoot: repositoryRoot, roots: roots, snapshot: out snapshot);
        }
        if (!TryGit(
            arguments: ["--literal-pathspecs", "ls-tree", "-z", "HEAD", "--", .. roots],
            reason: out reason,
            repositoryRoot: repositoryRoot,
            stdout: out var committed
        )) {
            return false;
        }
        if (!TryGit(
            // Reading another checkout (the main checkout an owner works in) takes no index lock and rewrites no index.
            arguments: ["--literal-pathspecs", "--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames", "--", .. roots],
            reason: out reason,
            repositoryRoot: repositoryRoot,
            stdout: out var changed
        )) {
            return false;
        }

        // Each porcelain -z entry is "XY <path>" and --no-renames keeps it to one path, so the entries sort into a
        // stable order whatever order git walked the tree in.
        var entries = changed.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\0'
        ).Where(predicate: static entry => (entry.Length > 3)).Order(comparer: StringComparer.Ordinal);
        var changes = new List<Change>();

        foreach (var entry in entries) {
            var path = Path.Combine(
                path1: repositoryRoot,
                path2: entry[3..]
            );
            string content;

            try {
                content = ContentPin.OfFile(path: path).Hex;
            } catch (Exception exception) when ((exception is FileNotFoundException or DirectoryNotFoundException)) {
                content = "absent";
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                reason = $"could not read the changed file {entry[3..]}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

                return false;
            }

            changes.Add(item: new Change(Content: content, Entry: entry, Path: entry[3..]));
        }

        snapshot = new Snapshot(Changes: changes, Committed: committed);

        return true;
    }
}
