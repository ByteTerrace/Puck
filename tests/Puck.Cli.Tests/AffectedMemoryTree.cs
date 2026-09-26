using Puck.Cli.Affected;

namespace Puck.Cli.Tests;

/// <summary>A tree held in memory, rooted where no file exists, so any read that reached the file system would find
/// nothing.</summary>
/// <param name="files">Each file's text, by repository-relative path.</param>
internal sealed class AffectedMemoryTree(Dictionary<string, string> files) : IAffectedTree {
    public string Root { get; } = Path.GetFullPath(path: Path.Combine(path1: Path.GetTempPath(), path2: "puck-affected-memory-tree-never-on-disk"));

    public IReadOnlyList<string> Files(string directory) => [.. files.Keys
        .Where(predicate: path => path.StartsWith(comparisonType: StringComparison.Ordinal, value: (directory + "/")))
        .Order(comparer: StringComparer.Ordinal)];
    public string? ReadText(string path) => files.GetValueOrDefault(key: path);
}
