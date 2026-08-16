using Microsoft.CodeAnalysis.CSharp;

namespace Puck.Cli.Source;

// Every .cs file of a file set, parsed exactly ONCE. The scan analyzers all share one instance, so a
// full sweep parses the tree a single time instead of once per analyzer; the declarations inventory parses the
// same way over a walk of its own.
internal sealed class SourceCorpus {
    private SourceCorpus(IReadOnlyList<ParsedFile> files) {
        Files = files;
    }

    public int FileCount => Files.Count;
    public IReadOnlyList<ParsedFile> Files { get; }

    // The scan and format roots: a working-directory-relative (or absolute) directory, artifact
    // directories pruned.
    public static SourceCorpus? TryLoad(string rootArgument) {
        if (!SourceFiles.TryEnumerate(files: out var files, rootArgument: rootArgument, scanRoot: out var scanRoot)) {
            return null;
        }

        return Parse(files: files, relativeTo: scanRoot);
    }
    // An already-enumerated file set, with the base its display paths are shown against.
    public static SourceCorpus Parse(IReadOnlyList<string> files, string relativeTo) {
        var parsed = new List<ParsedFile>(capacity: files.Count);

        foreach (var file in files) {
            var text = File.ReadAllText(path: file);
            var root = CSharpSyntaxTree.ParseText(text: text).GetRoot();

            parsed.Add(item: new ParsedFile(Relative: CliPaths.ToDisplay(fullPath: file, relativeTo: relativeTo), Root: root, Text: text));
        }

        return new SourceCorpus(files: parsed);
    }
}
