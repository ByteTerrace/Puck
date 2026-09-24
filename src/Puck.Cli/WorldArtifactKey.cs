using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Puck.Assets;
using Puck.Hosting;

namespace Puck.Cli;

/// <summary>
/// Names the source state a World build is made from, so an unchanged checkout is built once and every later run
/// reuses that build.
/// <para>
/// The key hashes, over the <see cref="WorldArtifactClosure"/> roots only: git's object id for each root in
/// <c>HEAD</c> (a directory's tree id covers every committed file inside it), and, for every path under a root that
/// <c>git status</c> reports as modified, staged, deleted or untracked, the path, its status and the SHA-256 of the
/// bytes on disk. Files git ignores are not inputs; every item the World's build names outside <c>bin</c> and
/// <c>obj</c> is tracked or untracked, never ignored. The key also carries the runtime identifier of the machine, the
/// <c>CI</c> environment variable the shared build properties read, and the build's own command line, since each
/// changes what the same sources build into.
/// </para>
/// <para>
/// A change anywhere outside the closure, such as documentation, tests or a project the World does not reference,
/// leaves the key unchanged. Two worktrees whose closures hold identical content produce the same key.
/// </para>
/// </summary>
internal static class WorldArtifactKey {
    // Bumped whenever what the key covers changes, so no entry keyed under an older rule is ever reused.
    private const string Schema = "puck.world-artifact-key.v1";

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

    /// <summary>Computes the key of the checkout's current World sources.</summary>
    /// <param name="repositoryRoot">The checkout, which must be a git working tree with a <c>HEAD</c> commit.</param>
    /// <param name="roots">The closure roots, from <see cref="WorldArtifactClosure.Walk"/>.</param>
    /// <param name="buildArguments">The build's command line, without its output directory.</param>
    /// <param name="key">The key: 32 lowercase hexadecimal digits, or empty on failure.</param>
    /// <param name="reason">Why no key could be computed, or empty on success.</param>
    /// <returns><see langword="true"/> when both git queries answered.</returns>
    public static bool TryCompute(string repositoryRoot, IReadOnlyList<string> roots, IReadOnlyList<string> buildArguments, out string key, out string reason) {
        key = string.Empty;

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

        using var hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

        void Append(string text) =>
            hash.AppendData(data: Encoding.UTF8.GetBytes(s: $"{text}\0"));

        Append(text: Schema);
        Append(text: RuntimeInformation.RuntimeIdentifier);
        Append(text: $"CI={Environment.GetEnvironmentVariable(variable: "CI")}");
        Append(text: string.Join(
            separator: '\u001f',
            values: buildArguments
        ));
        Append(text: committed);

        // Each porcelain -z entry is "XY <path>" and --no-renames keeps it to one path, so the entries sort into a
        // stable order whatever order git walked the tree in.
        var entries = changed.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\0'
        ).Where(predicate: static entry => (entry.Length > 3)).Order(comparer: StringComparer.Ordinal);

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

            Append(text: $"{entry}\0{content}");
        }

        key = Convert.ToHexStringLower(inArray: hash.GetHashAndReset()[..16]);

        return true;
    }
}
