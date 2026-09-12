using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Corpus-wide coverage for <see cref="PuckLinter.LintReferences"/>'s basis-vs-module policy: every
/// shipped world (root, basis-bearing non-root, or basis-less module) lints clean of reference-family findings,
/// and a genuine typo inside a basis-declaring document's own rules is still caught, with a real span.</summary>
public class LinterCoverageTests {
    public static TheoryData<string> GetShippedWorldFiles() => ShippedWorlds.Files();

    private static readonly string[] ReferenceLintCodes = [
        PuckDiagnosticCodes.LintUnresolvedState, PuckDiagnosticCodes.LintUnresolvedPrototype,
        PuckDiagnosticCodes.LintUnresolvedPlacementParent, PuckDiagnosticCodes.LintUnresolvedView,
        PuckDiagnosticCodes.LintUnknownChannelPrefix, PuckDiagnosticCodes.UnresolvedParent,
        PuckDiagnosticCodes.CompositionRefused,
    ];

    [Theory]
    [MemberData(nameof(GetShippedWorldFiles))]
    public void EveryShippedWorldLintsCleanOfReferenceFindings(string relativePath) {
        var fullPath = Path.Combine(ShippedWorlds.FindDirectory(), relativePath);
        var decompiled = WorldDecompiler.Decompile(File.ReadAllText(fullPath));

        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(decompiled, diagnostics: diagnostics);
        Assert.NotNull(parseResult.Value);

        var sourceMap = new SourceMap();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(fullPath),
            sourceMap: sourceMap,
            diagnostics: diagnostics
        );
        Assert.NotNull(loweringResult.Value);

        PuckLinter.LintReferences(loweringResult.Value, sourceMap, diagnostics, sourcePath: fullPath);

        var referenceFindings = diagnostics.Where(d => ReferenceLintCodes.Contains(d.Code)).ToList();
        Assert.True(
            referenceFindings.Count == 0,
            $"{relativePath} expected no reference-lint findings, got:{Environment.NewLine}"
                + string.Join(Environment.NewLine, referenceFindings.Select(d => $"{d.Code} {d.Span}: {d.Message}"))
        );
    }

    [Fact]
    public void RootDocumentReportsItsOwnMisspelledStateRowWithARealSpan() {
        var fullPath = Path.Combine(ShippedWorlds.FindDirectory(), "puck.world.json");
        var decompiled = WorldDecompiler.Decompile(File.ReadAllText(fullPath));

        // "claimTicks" is a state row the root itself declares and reads inside a rule's own `when` clause —
        // corrupting this one read (never the row declaration, and never another read of the same row) isolates
        // exactly one bad reference in an otherwise-clean document.
        const string original = "when claimTicks > 0";
        const string misspelled = "when claimTicksXTYPO > 0";
        var typoAt = decompiled.IndexOf(original, StringComparison.Ordinal);
        Assert.True(typoAt >= 0, $"expected fixture text '{original}' in the decompiled root — update this test if the world's own rule text changed.");
        var mutated = string.Concat(decompiled.AsSpan(0, typoAt), misspelled, decompiled.AsSpan(typoAt + original.Length));

        var parseDiagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(mutated, diagnostics: parseDiagnostics);
        Assert.NotNull(parseResult.Value);

        var sourceMap = new SourceMap();
        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(fullPath),
            sourceMap: sourceMap,
            diagnostics: loweringDiagnostics
        );
        Assert.NotNull(loweringResult.Value);

        var referenceDiagnostics = new DiagnosticBag();
        PuckLinter.LintReferences(loweringResult.Value, sourceMap, referenceDiagnostics, sourcePath: fullPath);

        var findings = referenceDiagnostics.Where(d => d.Code == PuckDiagnosticCodes.LintUnresolvedState).ToList();
        var finding = Assert.Single(findings);
        Assert.Contains("claimTicksXTYPO", finding.Message, StringComparison.Ordinal);
        Assert.True(finding.Span.Line > 0, "expected a real line:column, not a whole-document SourceSpan.None finding");
    }

    // A root that declares `schema` and no `basis` is still a root: a basis-shaped discriminator would leave every
    // standalone world (backgammon, chinese-checkers, moth, study) with neither reference lint nor semantic
    // validation, so a misspelled row inside its own rules would be reported by nothing.
    [Fact]
    public void StandaloneRootWithNoBasisReportsItsOwnMisspelledStateRow() {
        var fullPath = Path.Combine(ShippedWorlds.FindDirectory(), "games/backgammon.world.json");
        var decompiled = WorldDecompiler.Decompile(File.ReadAllText(fullPath));

        const string original = "when checkerPoint[w0] !=";
        const string misspelled = "when checkerPointXTYPO[w0] !=";
        var typoAt = decompiled.IndexOf(original, StringComparison.Ordinal);
        Assert.True(typoAt >= 0, $"expected fixture text '{original}' in the decompiled world — update this test if its own rule text changed.");
        var mutated = string.Concat(decompiled.AsSpan(0, typoAt), misspelled, decompiled.AsSpan(typoAt + original.Length));

        var parseDiagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(mutated, diagnostics: parseDiagnostics);
        Assert.NotNull(parseResult.Value);

        var sourceMap = new SourceMap();
        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(fullPath),
            sourceMap: sourceMap,
            diagnostics: loweringDiagnostics
        );
        Assert.NotNull(loweringResult.Value);
        Assert.Null(loweringResult.Value["basis"]);
        Assert.True(WorldSemanticValidator.IsRootDocument(loweringResult.Value));

        var diagnostics = new DiagnosticBag();
        PuckLinter.LintReferences(loweringResult.Value, sourceMap, diagnostics, sourcePath: fullPath);
        WorldSemanticValidator.ValidateComposedWorld(loweringResult.Value, sourceMap, diagnostics, sourcePath: fullPath);

        var lintFinding = Assert.Single(diagnostics, d => d.Code == PuckDiagnosticCodes.LintUnresolvedState);
        Assert.Contains("checkerPointXTYPO", lintFinding.Message, StringComparison.Ordinal);
        Assert.True(lintFinding.Span.Line > 0, "expected a real line:column, not a whole-document SourceSpan.None finding");

        Assert.Contains(diagnostics, d => d.Code == PuckDiagnosticCodes.SemanticValidation && d.Message.Contains("checkerPointXTYPO", StringComparison.Ordinal));
    }

    // The mirror of the case above: a module declares no `schema` and no `basis`, so it is validated by neither
    // verb — composing it as a root would report as missing every field its importer supplies.
    [Fact]
    public void ModuleWithNoSchemaAndNoBasisIsNotARoot() {
        var fullPath = Path.Combine(ShippedWorlds.FindDirectory(), "games/chess.world.json");

        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fullPath))!.AsObject();

        Assert.Null(document["schema"]);
        Assert.Null(document["basis"]);
        Assert.False(WorldSemanticValidator.IsRootDocument(document));
    }

    [Fact]
    public void ModuleReferencingOnlyARootDeclaredNameYieldsNoFindingWhenLintedStandalone() {
        // games/freecell.world.json declares no basis and reads names games/solitaire.world.json's own body
        // supplies when it imports freecell as a sibling — a standalone lint pass over freecell alone cannot see
        // that supplying document, so it must stay silent rather than guess.
        var fullPath = Path.Combine(ShippedWorlds.FindDirectory(), "games/freecell.world.json");
        var decompiled = WorldDecompiler.Decompile(File.ReadAllText(fullPath));

        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(decompiled, diagnostics: diagnostics);
        Assert.NotNull(parseResult.Value);

        var sourceMap = new SourceMap();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(fullPath),
            sourceMap: sourceMap,
            diagnostics: diagnostics
        );
        Assert.NotNull(loweringResult.Value);
        Assert.Null(loweringResult.Value["basis"]);

        PuckLinter.LintReferences(loweringResult.Value, sourceMap, diagnostics, sourcePath: fullPath);

        Assert.DoesNotContain(diagnostics, d => ReferenceLintCodes.Contains(d.Code));
    }
}
