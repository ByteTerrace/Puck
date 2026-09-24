using Puck.Cli.Source;

namespace Puck.Cli.Format;

// Which files `format` owns, decided once for a tree walk, a --file-list, and a pull request's changed paths: C#
// sources other than generated ones, and .puck sources, outside FileWalk's shared prune set and outside the
// quarantined experimental trees.
internal static class FormatSources {
    // Only format prunes experimental/: search and declarations read quarantine as prior art; format never improves it.
    private static readonly string[] FormatOnlyPruned = ["experimental"];

    public static bool IsCSharp(string path) =>
        (path.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".cs"
        )
        && !path.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".g.cs"
        )
        && !path.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".generated.cs"
        ));
    public static bool IsPuck(string path) =>
        path.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".puck"
        );
    public static bool IsFormattable(string path) => (IsCSharp(path: path) || IsPuck(path: path));
    // A forward-slashed path relative to the repository root, as git reports it: formattable, contained, and under no
    // pruned directory.
    public static bool Admits(string path) =>
        (IsFormattable(path: path)
        && !path.Contains(value: '\\') && !path.Contains(value: ':')
        && !path.Split('/').Any(predicate: static part => ((part is "" or "." or "..") || IsPruned(name: part))));
    // Every formattable file under `root`, or null when `root` does not exist (already reported).
    public static List<string>? Enumerate(string root) =>
        FileWalk.Enumerate(
            admit: IsFormattable,
            exclude: [.. FormatOnlyPruned.Select(selector: static name => new CliGlob(glob: name))],
            include: [],
            roots: [root],
            verb: "format"
        );

    private static bool IsPruned(string name) =>
        (FileWalk.SkipDirectories.Contains(item: name) || FormatOnlyPruned.Contains(
            comparer: StringComparer.OrdinalIgnoreCase,
            value: name
        ));
}
