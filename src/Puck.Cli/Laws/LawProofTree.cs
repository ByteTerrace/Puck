using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions;
using Puck.Hosting;

namespace Puck.Cli.Laws;

/// <summary>A persistent shared-object clone, leased exclusively for one proof. The lease is outside the clone so
/// replacing a damaged clone never releases it. Build outputs stay at the path that produced them. One clone serves
/// every worktree of a repository: it is keyed by, cloned from and fetched from the repository's common git
/// directory, never a worktree root.</summary>
internal sealed class LawProofTree(string tree, string source, FileStream lease) : IDisposable {
    public static string DefaultRoot => PuckUserDirectory.Resolve(name: "law-trees");

    /// <summary>The repository's common git directory this clone is keyed by and cloned from.</summary>
    public string Source { get; } = source;
    public string Tree { get; } = tree;

    public void Dispose() => lease.Dispose();
    /// <summary>The repository's common git directory, absolute: the same for every worktree of one repository.</summary>
    /// <param name="repository">Any worktree of the repository.</param>
    /// <param name="source">The common git directory, or empty when git cannot name it.</param>
    /// <param name="reason">Why it could not be resolved, or empty.</param>
    /// <returns><see langword="true"/> when resolved.</returns>
    public static bool TryResolveSource(string repository, out string source, out string reason) {
        var resolved = CliGit.Run(repository, ["rev-parse", "--path-format=absolute", "--git-common-dir"]);

        source = resolved.Stdout.Trim();
        reason = (((resolved.ExitCode == 0) && Path.IsPathFullyQualified(path: source))
            ? string.Empty
            : $"cannot resolve the repository's common git directory: {resolved.Stderr.Trim()}");
        return (reason.Length == 0);
    }
    /// <summary>The stable directory for one repository, keyed by its common git directory, including its clone and
    /// exclusive lock.</summary>
    public static string DirectoryFor(string root, string source) {
        var path = PuckPaths.Normalize(path: Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: source)));

        if (OperatingSystem.IsWindows()) { path = path.ToUpperInvariant(); }
        return Path.Combine(path1: root, path2: Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: path))));
    }
    /// <summary>Attempts an exclusive lease without waiting. An unavailable cache takes the cold scratch path.</summary>
    public static LawProofTree? TryAcquire(string root, string repository, out string reason) {
        if (!TryResolveSource(reason: out reason, repository: repository, source: out var source)) {
            return null;
        }

        var directory = DirectoryFor(root: root, source: source);

        try {
            LawProofFiles.RequireUnlinkedPath(path: directory);
            _ = Directory.CreateDirectory(path: directory);
            var path = Path.Combine(path1: directory, path2: "proof.lock");

            LawProofFiles.RequireUnlinkedPath(path: path);
            var handle = new FileStream(access: FileAccess.ReadWrite, mode: FileMode.OpenOrCreate, path: path, share: FileShare.None);

            reason = string.Empty;
            return new LawProofTree(lease: handle, source: source, tree: Path.Combine(path1: directory, path2: "tree"));
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = $"persistent proof tree is busy or unavailable ({exception.Message.ReplaceLineEndings(replacementText: " ")})";
            return null;
        }
    }
    /// <summary>Refreshes only changed tracked files and removes unignored strays. A bad cache is replaced once;
    /// inability to repair it falls back to a fresh scratch tree.</summary>
    public bool TryPrepare(string head, Func<string, string[], ChildProcessResult> git, out string reason) {
        var repository = Source;

        try {
            reason = "missing clone";
            if (Directory.Exists(path: Tree) && IsCallerClone(git: git, reason: out reason, repository: repository) && Refresh(git: git, head: head, reason: out reason, repository: repository)) {
                Console.Error.WriteLine(value: $"laws prove: reusing persistent proof tree {CliPaths.ToDisplay(fullPath: Tree)}.");
                return true;
            }

            Console.Error.WriteLine(value: $"laws prove: rebuilding persistent proof tree cold ({reason}): {CliPaths.ToDisplay(fullPath: Tree)}.");
            // Delete only the derived cache entry. The walker unlinks reparse points without visiting their targets.
            LawProofFiles.DeleteTree(path: Tree);
            var clone = git(repository, ["clone", "--shared", "--no-checkout", "--quiet", "--", repository, Tree]);

            if (clone.ExitCode != 0) {
                reason = $"clone exited {clone.ExitCode}: {clone.Stderr.Trim()}";
                return false;
            }
            return Refresh(git: git, head: head, reason: out reason, repository: repository);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = $"cannot repair persistent proof tree: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
            return false;
        }
    }

    private bool IsCallerClone(string repository, Func<string, string[], ChildProcessResult> git, out string reason) {
        reason = "invalid clone or clone source";
        if (!Directory.Exists(path: Path.Combine(path1: Tree, path2: ".git")) || (LawProofFiles.LinkedPath(tree: Tree) is not null)) { return false; }
        var origin = git(Tree, ["config", "--get", "remote.origin.url"]);

        if ((origin.ExitCode != 0) || !Path.IsPathFullyQualified(path: origin.Stdout.Trim()) || !string.Equals(
            a: Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: origin.Stdout.Trim())),
            b: Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: repository)),
            comparisonType: PuckPaths.Comparison)) { return false; }
        // The clone must be its own top level. Ask git for the way up rather than comparing spellings: a packaged host
        // redirects the per-user directory, so git can report the same directory under another path.
        var up = git(Tree, ["rev-parse", "--show-cdup"]);

        return ((up.ExitCode == 0) && (up.Stdout.Trim().Length == 0));
    }
    private bool Refresh(string repository, string head, Func<string, string[], ChildProcessResult> git, out string reason) {
        foreach (var arguments in new string[][] {
            ["fetch", "--quiet", "--no-tags", "--no-auto-maintenance", "--", repository, head],
            ["checkout", "--detach", "--force", head],
            ["clean", "-f", "-d"],
        }) {
            var result = git(Tree, arguments);

            if (result.ExitCode != 0) {
                reason = $"git {arguments[0]} exited {result.ExitCode}: {result.Stderr.Trim()}";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }
}
