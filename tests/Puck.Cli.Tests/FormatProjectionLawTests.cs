using System.Text;
using Puck.Abstractions.Documents;
using Puck.Cli.Transpiler;
using Puck.GamingBricks.Transpiler;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Formatting prints the tree a source parsed to, so it can move the bytes of a source but never the bytes
/// of the document that source compiles to — and formatting an already-formatted source changes nothing at all.
/// The law walks every tracked <c>.puck</c> in the repository, through both vocabularies, the way <c>puck format</c>
/// resolves them.</summary>
public sealed class FormatProjectionLawTests {
    // The text is compiled as if it were the file it stands for, so a relative `basis`, an `import`, and the
    // embedding lock beside it all resolve — and nothing is written into the checkout.
    private static string Compile(string source, string sourcePath, string label) {
        var diagnostics = new DiagnosticBag();

        // A world source may emit several worlds, and formatting has to leave every one of them unchanged, so the
        // compiled text is each emitted document under its world's name, in the compiler's own order.
        if (!(PuckParser.TryReadDocumentSchema(schema: out var schema, source: source) && string.Equals(
            a: schema,
            b: CartridgeVocabulary.Schema,
            comparisonType: StringComparison.Ordinal
        ))) {
            var compilation = WorldCompiler.Compile(
                allowMultiple: true,
                diagnostics: diagnostics,
                imports: ImportHandling.Validate,
                source: source,
                sourceMap: new SourceMap(),
                sourcePath: sourcePath
            );

            Assert.False(
                condition: diagnostics.HasErrors,
                userMessage: $"{label} does not compile:{Environment.NewLine}{diagnostics.FormatReport(source)}"
            );

            if (compilation.Worlds.Count > 0) {
                return string.Join(
                    separator: Environment.NewLine,
                    values: compilation.Worlds.Select(selector: static world => $"{world.Name}: {Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: world.Json))}")
                );
            }

            Assert.NotNull(@object: compilation.Json);

            return Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: compilation.Json!));
        }

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
        var path = RepositoryPaths.Resolve(relativePath: relativePath);

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

    public static TheoryData<string> Sources() => TrackedPuckSources.Under();
    // A committed source is the printer's own output, so `puck format` on a clean checkout writes nothing.
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
        var compiled = Compile(
            label: relativePath,
            source: source,
            sourcePath: path
        );

        // The compiler is a function of the text, so a source the printer leaves byte-identical compiles to the same
        // document by construction; only moved bytes need a second compilation to compare.
        if (!string.Equals(
            a: once,
            b: source,
            comparisonType: StringComparison.Ordinal
        )) {
            Assert.Equal(
                actual: Compile(
                    label: $"{relativePath}: formatted",
                    source: once,
                    sourcePath: path
                ),
                expected: compiled
            );
        }
    }
}
