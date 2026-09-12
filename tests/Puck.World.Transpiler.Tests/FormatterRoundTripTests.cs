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
    public static TheoryData<string> GetShippedWorldFiles() => ShippedWorlds.Files();

    [Theory]
    [MemberData(nameof(GetShippedWorldFiles))]
    public void DecompileFormatCompileRoundTripsToTheOriginalDocument(string relativePath) {
        var fullPath = Path.Combine(ShippedWorlds.FindDirectory(), relativePath);
        var originalJsonText = File.ReadAllText(fullPath);
        var originalNode = JsonNode.Parse(originalJsonText);
        Assert.NotNull(originalNode);

        var decompiled = WorldDecompiler.Decompile(originalJsonText);
        var formatted = PuckFormatter.Format(decompiled);
        var recompiledNode = CompileToJson(formatted, fullPath, relativePath);

        var originalCanonical = System.Text.Encoding.UTF8.GetString(CanonicalJsonDocument.Serialize(originalNode));
        var recompiledCanonical = System.Text.Encoding.UTF8.GetString(CanonicalJsonDocument.Serialize(recompiledNode));

        var mismatch = JsonMismatch.Find(JsonNode.Parse(originalCanonical), JsonNode.Parse(recompiledCanonical), $"{relativePath}:");
        Assert.Null(mismatch);
    }

    // Isolates the formatter from the decompiler: comparing the unformatted and formatted decompiled sources
    // against each other, rather than against the original JSON, fails only when formatting itself drifts.
    [Theory]
    [MemberData(nameof(GetShippedWorldFiles))]
    public void FormattingNeverChangesWhatADocumentCompilesTo(string relativePath) {
        var fullPath = Path.Combine(ShippedWorlds.FindDirectory(), relativePath);
        var decompiled = WorldDecompiler.Decompile(File.ReadAllText(fullPath));
        var formatted = PuckFormatter.Format(decompiled);

        var unformattedNode = CompileToJson(decompiled, fullPath, relativePath);
        var formattedNode = CompileToJson(formatted, fullPath, relativePath);

        var unformattedCanonical = System.Text.Encoding.UTF8.GetString(CanonicalJsonDocument.Serialize(unformattedNode));
        var formattedCanonical = System.Text.Encoding.UTF8.GetString(CanonicalJsonDocument.Serialize(formattedNode));

        var mismatch = JsonMismatch.Find(JsonNode.Parse(unformattedCanonical), JsonNode.Parse(formattedCanonical), $"{relativePath}:");
        Assert.Null(mismatch);
    }

    [Theory]
    [MemberData(nameof(GetShippedWorldFiles))]
    public void FormattingIsIdempotent(string relativePath) {
        var fullPath = Path.Combine(ShippedWorlds.FindDirectory(), relativePath);
        var decompiled = WorldDecompiler.Decompile(File.ReadAllText(fullPath));

        var pass1 = PuckFormatter.Format(decompiled);
        var pass2 = PuckFormatter.Format(pass1);

        Assert.Equal(pass1, pass2);
    }

    [Theory]
    [MemberData(nameof(GetShippedWorldFiles))]
    public void TheFirstLineAfterFormattingIsTheHeaderComment(string relativePath) {
        var fullPath = Path.Combine(ShippedWorlds.FindDirectory(), relativePath);
        var decompiled = WorldDecompiler.Decompile(File.ReadAllText(fullPath));
        var formatted = PuckFormatter.Format(decompiled);

        var firstLine = formatted.Split('\n')[0];
        Assert.Equal("// Decompiled from a canonical Puck world document — a one-time import.", firstLine);
    }

    private static JsonNode CompileToJson(string source, string fullPath, string relativePath) {
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source, diagnostics: diagnostics);
        Assert.False(diagnostics.HasErrors, $"Parse errors for {relativePath}:{Environment.NewLine}{diagnostics.FormatReport(source)}");
        Assert.NotNull(parseResult.Value);

        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(fullPath),
            diagnostics: loweringDiagnostics
        );
        Assert.False(loweringDiagnostics.HasErrors, $"Lowering errors for {relativePath}:{Environment.NewLine}{loweringDiagnostics.FormatReport(source)}");
        Assert.NotNull(loweringResult.Value);

        return loweringResult.Value!;
    }

}
