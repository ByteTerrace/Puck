using Microsoft.CodeAnalysis;

namespace Puck.Cli.Source;

// One parsed source file: its scan-root- or working-directory-relative display path, its syntax root,
// and the text it was parsed from.
internal readonly record struct ParsedFile(string Relative, SyntaxNode Root, string Text);
// Resolves a scan root and enumerates its *.cs files (artifact directories pruned, ordinal-ignore-case
// sorted) — the single source of the file list every scan and format phase walks. A relative root
// argument resolves against the working directory, the one rule every verb shares. Pruning tests directory
// segments BELOW the scan root, so naming a skipped directory as the root itself still scans it — the
// same override FileWalk gives a named root.
internal static class SourceFiles {
    private static readonly char[] SegmentSeparators = ['\\', '/'];
    // Span lookups into FileWalk's set — the predicate runs once per enumerated file over large trees,
    // so a segment test allocates nothing.
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> SkipLookup =
        FileWalk.SkipDirectories.GetAlternateLookup<ReadOnlySpan<char>>();

    public static bool TryEnumerate(string rootArgument, out string scanRoot, out string[] files) {
        scanRoot = string.Empty;
        files = [];

        // Path.GetFullPath throws on an empty argument; a scripted call with an unset variable gets the
        // documented exit-2 channel instead of a stack trace.
        if (string.IsNullOrWhiteSpace(value: rootArgument)) {
            Console.Error.WriteLine(value: "ERROR: scan root not found: (empty)");

            return false;
        }

        // GetFullPath both resolves a relative argument against the working directory and canonicalizes
        // the separators — the latter matters because `dotnet format` matches the project it is handed
        // against the workspace's own canonical path and formats nothing at all when the two differ.
        scanRoot = Path.GetFullPath(path: rootArgument);

        if (!Directory.Exists(path: scanRoot)) {
            Console.Error.WriteLine(value: $"ERROR: scan root not found: {scanRoot}");

            return false;
        }

        var prefixLength = scanRoot.Length;

        files = [.. Directory.EnumerateFiles(path: scanRoot, searchOption: SearchOption.AllDirectories, searchPattern: "*.cs")
            .Where(predicate: path => AdmitsBelowRoot(belowRoot: path.AsSpan(start: prefixLength)))
            .OrderBy(keySelector: static path => path, comparer: StringComparer.OrdinalIgnoreCase)];

        return true;
    }
    // The nearest ancestor directory (from `start` up) that holds a .csproj — the owning project whose
    // build closure the semantic phase compiles against and whose whitespace phase 0 formats.
    public static string? FindOwningProjectDirectory(string start) {
        for (var directory = new DirectoryInfo(path: start); (directory is not null); directory = directory.Parent) {
            if (directory.EnumerateFiles(searchPattern: "*.csproj").Any()) {
                return directory.FullName;
            }
        }

        return null;
    }

    // Directory segments only — the final segment is the file name. FileWalk's artifact set carries the
    // generated and vendored names; beyond it the corpus prunes agent worktrees (.claude/worktrees holds
    // live duplicate checkouts another session may be editing), the same pair FileWalk prunes, so the two
    // walkers cover the same tree. Tracked source elsewhere under .claude stays in the corpus.
    private static bool AdmitsBelowRoot(ReadOnlySpan<char> belowRoot) {
        var previousWasClaude = false;

        while (true) {
            var cut = belowRoot.IndexOfAny(values: SegmentSeparators);

            if (cut < 0) {
                return true;
            }

            var segment = belowRoot[..cut];

            belowRoot = belowRoot[(cut + 1)..];

            if (segment.IsEmpty) {
                continue;
            }

            if (SkipLookup.Contains(item: segment)
                || (previousWasClaude && segment.Equals(other: "worktrees", comparisonType: StringComparison.OrdinalIgnoreCase))) {
                return false;
            }

            previousWasClaude = segment.Equals(other: ".claude", comparisonType: StringComparison.OrdinalIgnoreCase);
        }
    }
}
