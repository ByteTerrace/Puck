using System.Security.Cryptography;
using System.Text;
using Puck.Assets;
using Puck.Hosting;

namespace Puck.Cli;

/// <summary>
/// Reads what a checkout holds under a set of roots, for identities computed from their inputs at run time and never
/// recorded: git's committed entry for each root in <c>HEAD</c> (a directory's tree id covers every committed file
/// inside it), and, for every path under a root that <c>git status</c> reports as modified, staged, deleted or
/// untracked, the path, its status and the SHA-256 of the bytes on disk. Files git ignores are not inputs.
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
            foreach (var line in Committed.Split(separator: '\0', options: StringSplitOptions.RemoveEmptyEntries)) {
                var tab = line.IndexOf(value: '\t');

                if ((tab >= 0) && (line[(tab + 1)..] == root)) { Append(text: line); }
            }
            foreach (var change in Changes) {
                if ((change.Path == root) || change.Path.StartsWith(comparisonType: StringComparison.Ordinal, value: (root + "/"))) { Append(text: $"{change.Entry}\0{change.Content}"); }
            }
            return Convert.ToHexStringLower(inArray: hash.GetHashAndReset()[..16]);
        }
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
            reason = $"git {arguments[1]} exited {result.ExitCode}: {result.Stderr.Trim().ReplaceLineEndings(replacementText: " ")}";

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

        if (!TryGit(
            arguments: ["--literal-pathspecs", "ls-tree", "-z", "HEAD", "--", .. roots],
            reason: out reason,
            repositoryRoot: repositoryRoot,
            stdout: out var committed
        )) {
            return false;
        }
        if (!TryGit(
            arguments: ["--literal-pathspecs", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames", "--", .. roots],
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
