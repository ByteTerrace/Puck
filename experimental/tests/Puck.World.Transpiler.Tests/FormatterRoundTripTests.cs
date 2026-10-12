using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Formatting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary><see cref="PuckPrinter"/> against every shipped world: formatting a decompiled document must never
/// change what it compiles to, must be idempotent, and must leave the decompiler's one-time-import header as the
/// first line.</summary>
public class FormatterRoundTripTests {
    private static JsonNode CanonicalCompile(string source, string fullPath, string relativePath) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            imports: ImportHandling.Ignore,
            source: source,
            sourcePath: fullPath
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: $"Compile errors for {relativePath}:{Environment.NewLine}{compilation.Diagnostics.FormatReport(source)}"
        );

        return Canonical(node: compilation.RequireJson());
    }
    private static JsonNode Canonical(JsonNode node) => JsonNode.Parse(json: System.Text.Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: node)))!;

    public static TheoryData<string> GetShippedWorldFiles() => ShippedWorlds.Files();
    [MemberData(nameof(GetShippedWorldFiles))]
    [Theory]
    public void AFormattedDecompilationIsHeadedIdempotentAndCompilesToTheOriginalDocument(string relativePath) {
        var fullPath = ShippedWorlds.PathOf(relativePath: relativePath);
        var originalJsonText = File.ReadAllText(path: fullPath);
        var decompiled = WorldDecompiler.Decompile(jsonText: originalJsonText);
        var formatted = PuckFormat.Format(source: decompiled);

        Assert.Equal(
            actual: formatted.Split('\n')[0],
            expected: "// Bootstrapped from a Puck world document. The '.puck' source is canonical: edit it and"
        );
        Assert.Equal(
            actual: PuckFormat.Format(source: formatted),
            expected: formatted
        );

        var original = Canonical(node: JsonNode.Parse(originalJsonText)!);
        var mismatch = JsonMismatch.Find(
            actual: CanonicalCompile(
                fullPath: fullPath,
                relativePath: relativePath,
                source: formatted
            ),
            expected: original,
            path: $"{relativePath}:"
        );

        if (mismatch is null) {
            return;
        }

        // Compiling the unformatted decompilation isolates the formatter from the decompiler: only when it still
        // compiles to the original did the formatting itself move the document.
        var decompilerMismatch = JsonMismatch.Find(
            actual: CanonicalCompile(
                fullPath: fullPath,
                relativePath: relativePath,
                source: decompiled
            ),
            expected: original,
            path: $"{relativePath}:"
        );

        Assert.Fail(message: ((decompilerMismatch is null)
            ? $"formatting changed what the decompilation compiles to: {mismatch}"
            : $"the decompilation does not compile to the original before formatting: {decompilerMismatch}"
        ));
    }
}
