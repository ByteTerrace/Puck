namespace Puck.Cli.Affected;

/// <summary>
/// One tree of the repository that selection reads files from: the working tree, or the tree a revision recorded. The
/// stand-in map reads projects, shader sources and indexed C# through it and never through the file system, so a file
/// deleted since the base is placed by what the base's tree said about it. Paths are repository-relative with forward
/// slashes.
/// </summary>
internal interface IAffectedTree {
    /// <summary>Gets the repository root, the directory every path is relative to.</summary>
    string Root { get; }

    /// <summary>Lists every file under a directory, at any depth.</summary>
    /// <param name="directory">The directory, repository-relative.</param>
    /// <returns>The files, repository-relative, ordinal order.</returns>
    IReadOnlyList<string> Files(string directory);
    /// <summary>Reads a file's text.</summary>
    /// <param name="path">The file, repository-relative.</param>
    /// <returns>The text, or <see langword="null"/> when the tree holds no such file.</returns>
    string? ReadText(string path);
}
/// <summary>The working tree: files as they are on disk now, untracked ones included, with each project's build output
/// (<c>bin</c>, <c>obj</c>) left out.</summary>
/// <param name="root">The repository root.</param>
internal sealed class AffectedWorkingTree(string root) : IAffectedTree {
    /// <inheritdoc/>
    public string Root { get; } = Path.GetFullPath(path: root);

    /// <inheritdoc/>
    public IReadOnlyList<string> Files(string directory) {
        var full = Path.Combine(path1: Root, path2: directory);

        if (!Directory.Exists(path: full)) {
            return [];
        }

        return [.. Directory.EnumerateFiles(path: full, searchOption: SearchOption.AllDirectories, searchPattern: "*")
            .Select(selector: path => Path.GetRelativePath(path: path, relativeTo: Root).Replace(newChar: '/', oldChar: '\\'))
            .Where(predicate: path => !IsBuildOutput(directory: directory, path: path))
            .Order(comparer: StringComparer.Ordinal)];
    }
    /// <inheritdoc/>
    public string? ReadText(string path) {
        var full = Path.Combine(path1: Root, path2: path);

        return (File.Exists(path: full)
            ? File.ReadAllText(path: full)
            : null);
    }

    private static bool IsBuildOutput(string directory, string path) {
        var rest = path[(directory.Length + 1)..];

        return (
            rest.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "bin/") ||
            rest.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "obj/")
        );
    }
}
/// <summary>The tree a revision recorded, read through git: its file list once (<c>git ls-tree</c>), and each file's
/// text when first asked for (<c>git show &lt;revision&gt;:&lt;path&gt;</c>), kept for the tree's lifetime.</summary>
/// <param name="root">The repository root.</param>
/// <param name="revision">The revision.</param>
internal sealed class AffectedRevisionTree(string root, string revision) : IAffectedTree {
    private readonly Lazy<IReadOnlyList<string>> m_files = new(valueFactory: () => [.. CliGit.Run(root, "ls-tree", "-r", "--name-only", revision).Stdout
        .Split(separator: '\n')
        .Select(selector: static line => line.TrimEnd(trimChar: '\r'))
        .Where(predicate: static line => (line.Length > 0))
        .Order(comparer: StringComparer.Ordinal)]);
    private readonly Dictionary<string, string?> m_texts = new(comparer: StringComparer.Ordinal);

    /// <inheritdoc/>
    public string Root { get; } = Path.GetFullPath(path: root);

    /// <inheritdoc/>
    public IReadOnlyList<string> Files(string directory) {
        var prefix = (directory + "/");

        return [.. m_files.Value.Where(predicate: path => path.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix))];
    }
    /// <inheritdoc/>
    public string? ReadText(string path) {
        if (!m_texts.TryGetValue(key: path, value: out var text)) {
            var shown = CliGit.Run(Root, "show", $"{revision}:{path}");

            text = ((shown.ExitCode == 0)
                ? shown.Stdout
                : null);
            m_texts[path] = text;
        }

        return text;
    }
}
