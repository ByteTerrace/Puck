using System.Text;
using Puck.Abstractions.Documents;
using Puck.Cli.Transpiler;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.World.Transpiler;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Formatting prints the tree a source parsed to, so it can move the bytes of a source but never the bytes
/// of the document that source compiles to — and formatting an already-formatted source changes nothing at all.
/// The law walks every tracked <c>.puck</c> in the repository, through both vocabularies, the way <c>puck fmt</c>
/// resolves them.</summary>
public sealed class FormatProjectionLawTests {
    // Quarantined: read, never built or run.
    private static readonly string[] Excluded = ["experimental"];

    private static string RepositoryRoot() {
        Assert.True(
            condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root),
            userMessage: "repository root is required"
        );

        return root!;
    }
    // The text is compiled as if it were the file it stands for, so a relative `basis`, an `import`, and the
    // embedding lock beside it all resolve — and nothing is written into the checkout.
    private static string Compile(string source, string sourcePath, string label) {
        var diagnostics = new DiagnosticBag();

        var (_, json) = CompileCommand.CompileSource(
            diagnostics: diagnostics,
            imports: ImportHandling.Validate,
            sourceMap: new SourceMap(),
            sourcePath: sourcePath,
            sourceText: source,
            vocabulary: CliVocabularyResolver.Instance.Resolve(source: source)
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: $"{label} does not compile:{Environment.NewLine}{diagnostics.FormatReport(source)}"
        );
        Assert.NotNull(@object: json);

        return Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: json!));
    }
    private static (string Path, string Source) Read(string relativePath) {
        var path = Path.Combine(
            path1: RepositoryRoot(),
            path2: relativePath.Replace(
                newChar: Path.DirectorySeparatorChar,
                oldChar: '/'
            )
        );

        return (path, File.ReadAllText(path: path));
    }
    private static string Format(string source, string label) {
        var result = PuckPrinter.Format(
            source: source,
            vocabulary: CliVocabularyResolver.Instance.Resolve(source: source)
        );

        Assert.True(
            condition: (result.Value is not null),
            userMessage: $"{label} does not parse:{Environment.NewLine}{result.Diagnostics.FormatReport(source)}"
        );

        return result.Value!;
    }

    public static TheoryData<string> Sources() {
        var root = RepositoryRoot();
        var found = new SortedSet<string>(comparer: StringComparer.Ordinal);
        // Tracked files only: a checkout also holds other worktrees and scratch under it, none of which is this
        // repository's source.
        var listing = CliProcess.RunCaptured(
            fileName: "git",
            arguments: ["-C", root, "ls-files", "-z", "--", "*.puck"],
            input: "",
            timeout: TimeSpan.FromMinutes(value: 1)
        );

        Assert.True(
            condition: (listing.ExitCode == 0),
            userMessage: $"git ls-files failed: {listing.Stderr}"
        );

        foreach (var entry in listing.Stdout.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\0'
        )) {
            var relative = entry.Trim();

            if (relative.Length == 0) {
                continue;
            }
            if (relative.Split('/').Any(predicate: segment => Excluded.Contains(value: segment, comparer: StringComparer.OrdinalIgnoreCase))) {
                continue;
            }
            found.Add(item: relative);
        }

        Assert.NotEmpty(collection: found);

        return new TheoryData<string>(values: found);
    }
    // A committed source is the printer's own output, so `puck fmt` on a clean checkout writes nothing.
    [MemberData(nameof(Sources))]
    [Theory]
    public void ACommittedSourceIsAlreadyWhatThePrinterPrints(string relativePath) {
        var (_, source) = Read(relativePath: relativePath);

        Assert.Equal(
            actual: Format(
                label: relativePath,
                source: source
            ),
            expected: source
        );
    }
    [MemberData(nameof(Sources))]
    [Theory]
    public void FormattingIsAFixedPointAndLeavesTheGeneratedDocumentUnchanged(string relativePath) {
        var (path, source) = Read(relativePath: relativePath);
        var once = Format(
            label: relativePath,
            source: source
        );

        Assert.Equal(
            actual: Format(
                label: $"{relativePath}: formatted",
                source: once
            ),
            expected: once
        );
        Assert.Equal(
            actual: Compile(
                label: $"{relativePath}: formatted",
                source: once,
                sourcePath: path
            ),
            expected: Compile(
                label: relativePath,
                source: source,
                sourcePath: path
            )
        );
    }
}
