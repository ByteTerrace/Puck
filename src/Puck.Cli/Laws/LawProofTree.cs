using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions;
using Puck.Hosting;

namespace Puck.Cli.Laws;

/// <summary>A persistent shared-object clone, leased exclusively for one proof. The lease is outside the clone so
/// replacing a damaged clone never releases it. Managed build outputs stay at the path that produced them. Complete
/// shader artifact pairs may warm this clone from the caller or another registered worktree of the same repository;
/// the normal build independently admits their content. Restoration reuses only captured original pair identities.
/// One clone serves every worktree of a repository: it is keyed by, cloned from and fetched from the repository's common git
/// directory, never a worktree root.</summary>
internal sealed class LawProofTree(string tree, string source, string repository, FileStream lease) : IDisposable {
    private Dictionary<string, byte[]>? m_originalShaders;

    private string[] m_shaderDonors = [];

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
            return new LawProofTree(lease: handle, repository: Path.GetFullPath(path: repository), source: source, tree: Path.Combine(path1: directory, path2: "tree"));
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
    /// <summary>Warms only ignored shader artifact pairs while this proof owns the clone. The first call captures
    /// original identities; subsequent calls admit only those exact pairs from the same repository's registered
    /// worktrees. The destination's normal build remains the source/include/recipe authority.</summary>
    /// <param name="cancellationToken">Cancels discovery and copying between pairs.</param>
    public void WarmShaders(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(condition: !lease.CanRead, instance: this);
        var capture = (m_originalShaders is null);
        // An unavailable first capture must remain empty: a later call cannot adopt withheld build outputs as originals.
        m_originalShaders ??= new Dictionary<string, byte[]>(comparer: PuckPaths.Comparer);
        if (!capture && (m_originalShaders.Count == 0)) { return; }
        try {
            LawProofFiles.RequireUnlinkedPath(path: repository);
            LawProofFiles.RequireUnlinkedPath(path: Tree);
            if (capture) { m_shaderDonors = ShaderDonors(cancellationToken: cancellationToken); }
            var projectDirectories = (capture
                ? ReadGit(Tree, ["ls-files", "-z", "--", "*.csproj"], cancellationToken)
                    .Select(selector: static path => (Path.GetDirectoryName(path: path) ?? string.Empty).Replace(newChar: '/', oldChar: '\\'))
                    .Where(predicate: static path => ((path.Length > 0) && !path.StartsWith(comparisonType: StringComparison.Ordinal, value: "experimental/"))).ToHashSet(comparer: PuckPaths.Comparer)
                : m_originalShaders.Keys.Select(selector: ShaderProject).ToHashSet(comparer: PuckPaths.Comparer));
            var accepted = new HashSet<string>(comparer: PuckPaths.Comparer);

            foreach (var donor in m_shaderDonors) {
                cancellationToken.ThrowIfCancellationRequested();
                try {
                    LawProofFiles.RequireUnlinkedPath(path: donor);
                    if (!TryResolveSource(reason: out _, repository: donor, source: out var common) || !string.Equals(a: common, b: Source, comparisonType: PuckPaths.Comparison)) { continue; }
                    var candidates = ShaderArtifacts(cancellationToken: cancellationToken, projects: projectDirectories, root: donor);

                    if (candidates.Length == 0) { continue; }
                    // Git names ignored paths even before they exist. Tracked destination files are excluded.
                    var ignored = CliGit.RunAsync(repository: Tree, arguments: ["check-ignore", "-z", "--stdin"],
                        input: (string.Join(separator: "\0", values: candidates.SelectMany(selector: static path => new[] { path, (path + ".hash") })) + '\0'),
                        cancellationToken: cancellationToken).GetAwaiter().GetResult();

                    if (ignored.ExitCode is not (0 or 1)) { throw new IOException(message: ignored.Stderr.Trim()); }
                    var destinationIgnored = ignored.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0').ToHashSet(comparer: PuckPaths.Comparer);
                    var copied = 0;

                    foreach (var group in candidates.Where(predicate: path => (destinationIgnored.Contains(item: path) && destinationIgnored.Contains(item: (path + ".hash"))))
                        .GroupBy(keySelector: ShaderProject, comparer: PuckPaths.Comparer)) {
                        if (!projectDirectories.Contains(item: group.Key)) { continue; }
                        try {
                            copied += WarmProject(donor, group.Key, group, accepted, capture, cancellationToken);
                        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                            Console.Error.WriteLine(value: $"laws prove: shader warming skipped {group.Key}: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
                        }
                    }
                    if (copied != 0) {
                        Console.Error.WriteLine(value: $"laws prove: warmed {copied} shader artifact pair(s) from {CliPaths.ToDisplay(fullPath: donor)}; the normal build validates source, includes and recipe.");
                    }
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    Console.Error.WriteLine(value: $"laws prove: shader donor unavailable {CliPaths.ToDisplay(fullPath: donor)}: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
                }
            }
            if (capture) { CaptureOriginalShaders(cancellationToken: cancellationToken, projects: projectDirectories); }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"laws prove: shader warming unavailable: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }
    }

    private string[] ShaderDonors(CancellationToken cancellationToken) {
        var donors = new List<string> { repository };
        var registered = CliGit.RunAsync(repository: repository, arguments: ["worktree", "list", "--porcelain", "-z"],
            cancellationToken: cancellationToken).GetAwaiter().GetResult();

        if (registered.ExitCode == 0) {
            foreach (var entry in registered.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0')) {
                if (entry.StartsWith(comparisonType: StringComparison.Ordinal, value: "worktree ")) { donors.Add(item: entry[9..]); }
            }
        }
        return [.. donors.Select(selector: path => PuckPaths.Normalize(path: Path.GetFullPath(path: path)))
            .Where(predicate: path => !string.Equals(a: path, b: PuckPaths.Normalize(path: Tree), comparisonType: PuckPaths.Comparison)).Distinct(comparer: PuckPaths.Comparer)];
    }
    private static string ShaderProject(string artifact) {
        var marker = artifact.IndexOf(value: "/Assets/Shaders/", comparisonType: PuckPaths.Comparison);

        return ((marker < 0) ? string.Empty : artifact[..marker]);
    }
    private static string[] ShaderArtifacts(string root, HashSet<string> projects, CancellationToken cancellationToken) {
        // Restrict every donor walk to the proven tree's tracked projects, never bin/obj or arbitrary worker folders.
        var directories = projects.Select(selector: static project => (project + "/Assets/Shaders/"))
            .Where(predicate: path => Directory.Exists(path: Within(path: path, root: root))).ToArray();

        if (directories.Length == 0) { return []; }
        var artifacts = ReadGit(arguments: ["ls-files", "--others", "--ignored", "--exclude-standard", "-z", "--", .. directories], cancellationToken: cancellationToken, root: root);

        return [.. artifacts.Where(predicate: path =>
            ((path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".spv") || path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".dxil")) &&
            artifacts.Contains(item: (path + ".hash"))))];
    }
    private void CaptureOriginalShaders(HashSet<string> projects, CancellationToken cancellationToken) {
        foreach (var group in ShaderArtifacts(Tree, projects, cancellationToken).GroupBy(ShaderProject, PuckPaths.Comparer)) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!projects.Contains(item: group.Key)) { continue; }
            try {
                var path = Within(Tree, $"{group.Key}/obj/shader-publish.lock");

                if (!File.Exists(path: path)) { continue; }
                using var publication = new FileStream(access: FileAccess.ReadWrite, mode: FileMode.Open, path: path, share: FileShare.None);

                foreach (var artifact in group) {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ReadPair(Tree, artifact, out var sidecar, out _)) { m_originalShaders![artifact] = sidecar; }
                }
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Console.Error.WriteLine(value: $"laws prove: original shader identity unavailable {group.Key}: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }
        }
    }
    private static bool ReadPair(string root, string artifact, out byte[] sidecar, out byte[] bytes) {
        sidecar = bytes = [];
        var source = Within(root, Path.ChangeExtension(extension: ".hlsl", path: artifact));
        var binary = Within(path: artifact, root: root);
        var hash = Within(path: (artifact + ".hash"), root: root);

        if (!File.Exists(path: source) || !File.Exists(path: binary) || !File.Exists(path: hash)) { return false; }
        sidecar = File.ReadAllBytes(path: hash);
        bytes = File.ReadAllBytes(path: binary);
        return CompletePair(bytes: bytes, sidecar: sidecar);
    }
    private int WarmProject(string donor, string project, IEnumerable<string> artifacts, HashSet<string> accepted, bool capture, CancellationToken cancellationToken) {
        var sourceLock = Within(path: $"{project}/obj/shader-publish.lock", root: donor);
        var destinationLock = Within(root: Tree, path: $"{project}/obj/shader-publish.lock");
        // A missing source lock is not evidence of a completed normal build. Do not create files in the caller.
        if (!File.Exists(path: sourceLock)) { return 0; }
        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: destinationLock)!);
        var sourceFirst = (string.Compare(strA: sourceLock, strB: destinationLock, comparisonType: PuckPaths.Comparison) < 0);
        using var first = new FileStream(access: FileAccess.ReadWrite, mode: (sourceFirst ? FileMode.Open : FileMode.OpenOrCreate), path: (sourceFirst ? sourceLock : destinationLock), share: FileShare.None);
        using var second = new FileStream(access: FileAccess.ReadWrite, mode: (sourceFirst ? FileMode.OpenOrCreate : FileMode.Open), path: (sourceFirst ? destinationLock : sourceLock), share: FileShare.None);
        var copied = 0;

        foreach (var artifact in artifacts) {
            cancellationToken.ThrowIfCancellationRequested();
            if (accepted.Contains(item: artifact)) { continue; }
            var to = Within(root: Tree, path: artifact);
            var toSidecar = Within(root: Tree, path: (artifact + ".hash"));
            var source = Path.ChangeExtension(extension: ".hlsl", path: artifact);

            if (!File.Exists(path: Within(root: Tree, path: source)) || !ReadPair(artifact: artifact, bytes: out var bytes, root: donor, sidecar: out var sidecar)) { continue; }
            if (!capture && (!m_originalShaders!.TryGetValue(key: artifact, value: out var original) || !sidecar.AsSpan().SequenceEqual(other: original))) { continue; }
            if (File.Exists(path: to) && File.Exists(path: toSidecar) && File.ReadAllBytes(path: toSidecar).AsSpan().SequenceEqual(other: sidecar) &&
                File.ReadAllBytes(path: to).AsSpan().SequenceEqual(other: bytes)) {
                accepted.Add(item: artifact);
                continue;
            }

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: to)!);
            var token = Guid.NewGuid().ToString(format: "N");
            var temporary = Within(root: Tree, path: (artifact + $".{token}.tmp"));
            var temporarySidecar = Within(root: Tree, path: (artifact + $".hash.{token}.tmp"));

            try {
                File.WriteAllBytes(bytes: bytes, path: temporary);
                File.WriteAllBytes(bytes: sidecar, path: temporarySidecar);
                File.Delete(path: toSidecar);
                File.Move(destFileName: to, overwrite: true, sourceFileName: temporary);
                File.Move(destFileName: toSidecar, overwrite: true, sourceFileName: temporarySidecar);
                accepted.Add(item: artifact);
                copied++;
            } finally {
                File.Delete(path: temporary);
                File.Delete(path: temporarySidecar);
            }
        }
        return copied;
    }
    private static HashSet<string> ReadGit(string root, string[] arguments, CancellationToken cancellationToken) {
        var result = CliGit.RunAsync(repository: root, arguments: arguments, cancellationToken: cancellationToken).GetAwaiter().GetResult();

        if (result.ExitCode != 0) { throw new IOException(message: result.Stderr.Trim()); }
        return result.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0').ToHashSet(comparer: PuckPaths.Comparer);
    }
    private static string Within(string root, string path) {
        var boundary = (Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: root)) + Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path: Path.Combine(path1: boundary, path2: path));

        if (Path.IsPathRooted(path: path) || !full.StartsWith(value: boundary, comparisonType: PuckPaths.Comparison)) {
            throw new IOException(message: $"Shader artifact '{path}' escapes its repository.");
        }
        LawProofFiles.RequireUnlinkedPath(path: full);
        return full;
    }
    // This checks transport integrity only. The normal shader target owns source/include and effective-recipe admission.
    private static bool CompletePair(byte[] sidecar, byte[] bytes) {
        var fields = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var line in Encoding.UTF8.GetString(bytes: sidecar).Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n')) {
            var colon = line.IndexOf(value: ':');

            if ((colon < 0) || (line[..colon] is not ("source" or "recipe" or "bytecode"))) { return false; }
            var value = line[(colon + 1)..].Trim();

            if ((value.Length != 64) || value.Any(predicate: static c => (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) || !fields.TryAdd(key: line[..colon], value: value)) { return false; }
        }
        return ((fields.Count == 3) && (fields["bytecode"] == Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes))));
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
        // A proof stages its mirrored and withheld state in this clone's index (a three-way reverse writes the index),
        // and restoring the fix rewrites only the working files. Reset the index to the clone's own HEAD first, working
        // files untouched, so the checkout below rewrites only files whose content differs; a stale index would make it
        // rewrite unchanged files and recompile everything built from them.
        foreach (var arguments in new string[][] {
            ["reset", "--quiet"],
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
