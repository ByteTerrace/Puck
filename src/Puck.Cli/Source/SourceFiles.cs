using Microsoft.CodeAnalysis;

namespace Puck.Cli.Source;

// One parsed source file: its scan-root- or working-directory-relative display path, its syntax root,
// and the text it was parsed from.
internal readonly record struct ParsedFile(string Relative, SyntaxNode Root, string Text);
// Resolves a scan root and enumerates its *.cs files through FileWalk, ordinal-ignore-case sorted — the
// single source of the file list every scan and format phase walks. A relative root argument resolves
// against the working directory, the one rule every verb shares.
internal static class SourceFiles {
    // The nearest ancestor directory (from `start` up) that holds a .csproj — the owning project whose
    // build closure the semantic phase compiles against.
    public static string? FindOwningProjectDirectory(string start) =>
        RepositoryPaths.Ascend(
            start: start,
            probe: static directory => (directory.EnumerateFiles(searchPattern: "*.csproj").Any()
            ? directory.FullName
            : null)
        );
    public static bool TryEnumerate(string verb, string rootArgument, out string scanRoot, out string[] files) {
        scanRoot = string.Empty;
        files = [];

        // Path.GetFullPath throws on an empty argument; a scripted call with an unset variable gets the
        // documented exit-2 channel instead of a stack trace.
        if (string.IsNullOrWhiteSpace(value: rootArgument)) {
            Console.Error.WriteLine(value: $"{verb}: root not found: (empty)");

            return false;
        }

        // GetFullPath both resolves a relative argument against the working directory and canonicalizes
        // the separators, so every file path below shares the scan root's spelling as a prefix.
        scanRoot = Path.GetFullPath(path: rootArgument);

        if (!Directory.Exists(path: scanRoot)) {
            Console.Error.WriteLine(value: $"{verb}: root not found: {CliPaths.ToDisplay(fullPath: scanRoot)}");

            return false;
        }

        var walked = FileWalk.Enumerate(
            exclude: [],
            extension: ".cs",
            include: [],
            roots: [scanRoot],
            verb: verb
        );

        if (walked is null) {
            return false;
        }

        files = [.. walked.Order(comparer: StringComparer.OrdinalIgnoreCase)];

        return true;
    }
}
