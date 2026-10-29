using Puck.Testing;
using Xunit;

namespace Puck.Cli.Testing;

/// <summary>A small git checkout one law owns, under a temporary directory: files are written by forward-slashed
/// relative name, every git call must succeed, and disposal clears the read-only bits git puts on its objects before
/// deleting the whole directory.</summary>
internal sealed class GitScratchCheckout : IDisposable {
    internal sealed record TreeFile(string RelativePath, byte[] Bytes, DateTime LastWriteTimeUtc);
    internal sealed record TreeSnapshot(IReadOnlyList<string> Directories, IReadOnlyList<TreeFile> Files);

    private static readonly Lazy<TreeSnapshot> InitializedRepository = new(valueFactory: static () => {
        using var directory = new TemporaryDirectory(prefix: "puck-git-template-");
        var root = directory.PathOf(name: "checkout");

        _ = Directory.CreateDirectory(path: root);
        // A commit otherwise starts `git maintenance run --auto --detach`, which on recent git prunes worktree
        // registrations and repacks in the background: it changes what a law observes by git version and races the
        // directory's teardown.
        string[][] commands = [
            ["init", "--quiet", "--initial-branch=main"],
            ["config", "maintenance.auto", "false"],
            ["config", "gc.auto", "0"],
        ];

        foreach (var arguments in commands) {
            var result = CliGit.Run(
                arguments: arguments,
                repository: root
            );

            Assert.True(
                condition: (result.ExitCode == 0),
                userMessage: $"git {string.Join(separator: ' ', values: arguments)} exited {result.ExitCode}: {result.Stderr}"
            );
        }

        return CaptureSnapshot(root: root);
    });

    private readonly TemporaryDirectory m_directory = new(prefix: "puck-git-law-");

    /// <summary>Initializes a new instance of the <see cref="GitScratchCheckout"/> class: an empty repository on
    /// branch <c>main</c> that git never maintains on its own.</summary>
    public GitScratchCheckout()
        : this(snapshot: InitializedRepository.Value) {
    }
    internal GitScratchCheckout(TreeSnapshot snapshot) {
        _ = Directory.CreateDirectory(path: Root);
        RestoreSnapshot(root: Root, snapshot: snapshot);
    }

    /// <summary>Makes <paramref name="repository"/> an empty git repository on branch <c>main</c> that git never
    /// maintains on its own: the one way a law creates a scratch repository.</summary>
    /// <param name="repository">The existing directory to initialize.</param>
    public static void Initialize(string repository) => RestoreSnapshot(root: repository, snapshot: InitializedRepository.Value);
    internal static TreeSnapshot CaptureSnapshot(string root) {
        var directories = Directory.EnumerateDirectories(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*")
            .Select(selector: directory => Path.GetRelativePath(path: directory, relativeTo: root))
            .Order(comparer: StringComparer.Ordinal)
            .ToArray();
        var files = Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*")
            .Select(selector: file => new TreeFile(
                Bytes: File.ReadAllBytes(path: file),
                LastWriteTimeUtc: File.GetLastWriteTimeUtc(path: file),
                RelativePath: Path.GetRelativePath(path: file, relativeTo: root)
            ))
            .OrderBy(keySelector: static file => file.RelativePath.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".git") ? 1 : 0)
            .ThenBy(comparer: StringComparer.Ordinal, keySelector: static file => file.RelativePath)
            .ToArray();

        return new TreeSnapshot(Directories: directories, Files: files);
    }
    internal static void RestoreSnapshot(TreeSnapshot snapshot, string root) {
        _ = Directory.CreateDirectory(path: root);
        foreach (var directory in snapshot.Directories) {
            _ = Directory.CreateDirectory(path: Path.Combine(path1: root, path2: directory));
        }
        foreach (var file in snapshot.Files) {
            var path = Path.Combine(path1: root, path2: file.RelativePath);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
            File.WriteAllBytes(bytes: file.Bytes, path: path);
            File.SetLastWriteTimeUtc(lastWriteTimeUtc: file.LastWriteTimeUtc, path: path);
        }
    }

    /// <summary>Gets the checkout's absolute root.</summary>
    public string Root => Path.Combine(
        path1: m_directory.RootPath,
        path2: "checkout"
    );

    /// <summary>Stages everything and commits it, returning the new commit's full object name.</summary>
    /// <param name="message">The commit message.</param>
    /// <returns>The commit's full object name.</returns>
    public string Commit(string message) {
        _ = Git("add", "--all");
        _ = Git("-c", "user.name=law", "-c", "user.email=law@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "--message", message);
        var headFile = Path.Combine(path1: Root, path2: ".git/HEAD");

        if (File.Exists(path: headFile)) {
            var head = File.ReadAllText(path: headFile).Trim();
            var resolved = (head.StartsWith(comparisonType: StringComparison.Ordinal, value: "ref: ")
                ? Path.Combine(path1: Root, path2: ".git", path3: head[5..])
                : null);

            if ((resolved is not null) && File.Exists(path: resolved)) {
                var sha = File.ReadAllText(path: resolved).Trim();

                if (sha.Length == 40) {
                    return sha;
                }
            }
        }

        return Git("rev-parse", "HEAD").Trim();
    }
    public void Dispose() {
        foreach (var file in Directory.EnumerateFiles(path: m_directory.RootPath, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            File.SetAttributes(
                fileAttributes: FileAttributes.Normal,
                path: file
            );
        }

        m_directory.Dispose();
    }
    /// <summary>Runs git in the checkout and requires exit code zero.</summary>
    /// <param name="arguments">The git arguments.</param>
    /// <returns>Git's standard output.</returns>
    public string Git(params string[] arguments) {
        _ = Directory.CreateDirectory(path: Root);

        var result = CliGit.Run(
            arguments: arguments,
            repository: Root
        );

        Assert.True(
            condition: (result.ExitCode == 0),
            userMessage: $"git {string.Join(separator: ' ', values: arguments)} exited {result.ExitCode}: {result.Stderr}"
        );

        return result.Stdout;
    }
    /// <summary>Returns the text of <paramref name="name"/> in the checkout.</summary>
    /// <param name="name">The forward-slashed relative path.</param>
    /// <returns>The file's text.</returns>
    public string Read(string name) => File.ReadAllText(path: Path.Combine(path1: Root, path2: name));
    /// <summary>Writes <paramref name="text"/> to <paramref name="name"/> in the checkout.</summary>
    /// <param name="name">The forward-slashed relative path.</param>
    /// <param name="text">The file's text.</param>
    public void Write(string name, string text) => m_directory.WriteText(
        name: $"checkout/{name}",
        text: text
    );
    /// <summary>Adds a linked worktree beside the checkout, inside the directory this fixture deletes.</summary>
    /// <param name="name">The worktree directory name.</param>
    /// <param name="revision">An existing branch or commit to check out.</param>
    /// <param name="detached">Whether to detach HEAD at the revision.</param>
    /// <returns>The worktree's absolute path.</returns>
    public string AddWorktree(string name, string revision, bool detached = false) {
        var path = Path.Combine(path1: m_directory.RootPath, path2: $"worktrees/{name}");

        _ = Git(["worktree", "add", .. (detached ? new[] { "--detach" } : Array.Empty<string>()), path, revision]);
        return path;
    }
}
