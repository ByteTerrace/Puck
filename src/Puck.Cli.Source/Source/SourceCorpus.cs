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

    // An already-enumerated file set, with the base its display paths are shown against. Files parse in parallel, each
    // into its own slot, so the corpus keeps the enumeration's order whatever order the parses finish in.
    public static SourceCorpus Parse(IReadOnlyList<string> files, string relativeTo) {
        var parsed = new ParsedFile[files.Count];

        Parallel.For(
            body: index => {
                var text = File.ReadAllText(path: files[index]);

                parsed[index] = new ParsedFile(
                    Relative: CliPaths.ToDisplay(
                        fullPath: files[index],
                        relativeTo: relativeTo
                    ),
                    Root: CSharpSyntaxTree.ParseText(text: text).GetRoot(),
                    Text: text
                );
            },
            fromInclusive: 0,
            toExclusive: files.Count
        );

        return new SourceCorpus(files: parsed);
    }
    // The scan and format roots: a working-directory-relative (or absolute) directory, artifact
    // directories pruned.
    public static SourceCorpus? TryLoad(string rootArgument) {
        if (!SourceFiles.TryEnumerate(
            files: out var files,
            rootArgument: rootArgument,
            scanRoot: out var scanRoot,
            verb: "scan"
        )) {
            return null;
        }

        return Parse(
            files: files,
            relativeTo: scanRoot
        );
    }
}
