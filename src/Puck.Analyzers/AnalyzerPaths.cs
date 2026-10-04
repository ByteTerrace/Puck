namespace Puck.Analyzers;

/// <summary>Names the parts of a compilation input's path the same way on every host. The compiler hands an analyzer the
/// path text it was given, and a path spelled with either separator is one path: <see cref="System.IO.Path"/> reads only the
/// host's own separator, so on Linux it takes <c>X:\repo\FileLengths.json</c> for a single file name, and a ledger or manifest
/// lookup that went through it found the file on one host and not the other.</summary>
internal static class AnalyzerPaths {
    /// <summary>Returns the directory part of <paramref name="path"/>: everything before its last <c>/</c> or <c>\</c>.</summary>
    /// <param name="path">The path text, spelled with either separator.</param>
    /// <returns>The directory part, spelled as given, or an empty string when the path names no directory.</returns>
    public static string DirectoryName(string path) {
        var separator = path.LastIndexOfAny(anyOf: ['/', '\\']);

        return ((separator < 0)
            ? string.Empty
            : path.Substring(
                length: separator,
                startIndex: 0
            )
        );
    }
    /// <summary>Returns the file-name part of <paramref name="path"/>: everything after its last <c>/</c> or <c>\</c>.</summary>
    /// <param name="path">The path text, spelled with either separator.</param>
    /// <returns>The file name.</returns>
    public static string FileName(string path) => path.Substring(startIndex: (path.LastIndexOfAny(anyOf: ['/', '\\']) + 1));
}
