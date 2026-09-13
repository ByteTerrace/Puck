using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary><see cref="PuckFormatter"/> against every shipped world: formatting a decompiled document must never
/// change what it compiles to, must be idempotent, and must leave the decompiler's one-time-import header as the
/// first line.</summary>
public class FormatterRoundTripTests {
    private static JsonNode CompileToJson(string source, string fullPath, string relativePath) {
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source,
            diagnostics: diagnostics
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: $"Parse errors for {relativePath}:{Environment.NewLine}{diagnostics.FormatReport(source)}"
        );
        Assert.NotNull(@object: parseResult.Value);

        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(path: fullPath),
            diagnostics: loweringDiagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(
            condition: loweringDiagnostics.HasErrors,
            userMessage: $"Lowering errors for {relativePath}:{Environment.NewLine}{loweringDiagnostics.FormatReport(source)}"
        );
        Assert.NotNull(@object: loweringResult.Value);

        return loweringResult.Value!;
    }

    [MemberData(nameof(GetShippedWorldFiles))]
    [Theory]
    public void DecompileFormatCompileRoundTripsToTheOriginalDocument(string relativePath) {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );
        var originalJsonText = File.ReadAllText(path: fullPath);
        var originalNode = JsonNode.Parse(originalJsonText);

        Assert.NotNull(@object: originalNode);

        var decompiled = WorldDecompiler.Decompile(jsonText: originalJsonText);
        var formatted = PuckFormatter.Format(decompiled);
        var recompiledNode = CompileToJson(
            fullPath: fullPath,
            relativePath: relativePath,
            source: formatted
        );

        var originalCanonical = System.Text.Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: originalNode));
        var recompiledCanonical = System.Text.Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: recompiledNode));

        var mismatch = JsonMismatch.Find(
            JsonNode.Parse(originalCanonical),
            JsonNode.Parse(recompiledCanonical),
            $"{relativePath}:"
        );

        Assert.Null(@object: mismatch);
    }
    [MemberData(nameof(GetShippedWorldSources))]
    [Theory]
    public void FormattingACommittedSourceIsIdempotent(string relativePath) {
        var pass1 = PuckFormatter.Format(File.ReadAllText(path: Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        )));
        var pass2 = PuckFormatter.Format(pass1);

        Assert.Equal(
            actual: pass2,
            expected: pass1
        );
    }
    [MemberData(nameof(GetShippedWorldFiles))]
    [Theory]
    public void FormattingIsIdempotent(string relativePath) {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );
        var decompiled = WorldDecompiler.Decompile(jsonText: File.ReadAllText(path: fullPath));

        var pass1 = PuckFormatter.Format(decompiled);
        var pass2 = PuckFormatter.Format(pass1);

        Assert.Equal(
            actual: pass2,
            expected: pass1
        );
    }
    [MemberData(nameof(GetShippedWorldSources))]
    [Theory]
    public void FormattingNeverChangesWhatACommittedSourceCompilesTo(string relativePath) {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );
        var source = File.ReadAllText(path: fullPath);
        var formatted = PuckFormatter.Format(source);

        var unformattedCanonical = System.Text.Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: CompileToJson(
            fullPath: fullPath,
            relativePath: relativePath,
            source: source
        )));
        var formattedCanonical = System.Text.Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: CompileToJson(
            fullPath: fullPath,
            relativePath: relativePath,
            source: formatted
        )));

        var mismatch = JsonMismatch.Find(
            JsonNode.Parse(unformattedCanonical),
            JsonNode.Parse(formattedCanonical),
            $"{relativePath}:"
        );

        Assert.Null(@object: mismatch);
    }
    // Isolates the formatter from the decompiler: comparing the unformatted and formatted decompiled sources
    // against each other, rather than against the original JSON, fails only when formatting itself drifts.
    [Theory]
    [MemberData(nameof(GetShippedWorldFiles))]
    public void FormattingNeverChangesWhatADocumentCompilesTo(string relativePath) {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );
        var decompiled = WorldDecompiler.Decompile(jsonText: File.ReadAllText(path: fullPath));
        var formatted = PuckFormatter.Format(decompiled);

        var unformattedNode = CompileToJson(
            fullPath: fullPath,
            relativePath: relativePath,
            source: decompiled
        );
        var formattedNode = CompileToJson(
            fullPath: fullPath,
            relativePath: relativePath,
            source: formatted
        );

        var unformattedCanonical = System.Text.Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: unformattedNode));
        var formattedCanonical = System.Text.Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: formattedNode));

        var mismatch = JsonMismatch.Find(
            JsonNode.Parse(unformattedCanonical),
            JsonNode.Parse(formattedCanonical),
            $"{relativePath}:"
        );

        Assert.Null(@object: mismatch);
    }
    public static TheoryData<string> GetShippedWorldFiles() => ShippedWorlds.Files();
    public static TheoryData<string> GetShippedWorldSources() => ShippedWorlds.Sources();
    [MemberData(nameof(GetShippedWorldFiles))]
    [Theory]
    public void TheFirstLineAfterFormattingIsTheHeaderComment(string relativePath) {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );
        var decompiled = WorldDecompiler.Decompile(jsonText: File.ReadAllText(path: fullPath));
        var formatted = PuckFormatter.Format(decompiled);

        var firstLine = formatted.Split('\n')[0];

        Assert.Equal(
            actual: firstLine,
            expected: "// Bootstrapped from a Puck world document. The '.puck' source is canonical: edit it and"
        );
    }

}
