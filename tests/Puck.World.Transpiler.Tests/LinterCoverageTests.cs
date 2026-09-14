using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Corpus-wide coverage for <see cref="PuckLinter.LintReferences"/>'s basis-vs-module policy: every
/// shipped world (root, basis-bearing non-root, or basis-less module) lints clean of reference-family findings,
/// and a genuine typo inside a basis-declaring document's own rules is still caught, with a real span.</summary>
public class LinterCoverageTests {
    [MemberData(nameof(GetShippedWorldSources))]
    [Theory]
    public void EveryCommittedSourceLintsCleanOfReferenceFindings(string relativePath) {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );

        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            File.ReadAllText(path: fullPath),
            diagnostics: diagnostics
        );

        Assert.NotNull(@object: parseResult.Value);

        var sourceMap = new SourceMap();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(path: fullPath),
            sourceMap: sourceMap,
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.NotNull(@object: loweringResult.Value);

        PuckLinter.LintReferences(
            loweringResult.Value,
            sourceMap,
            diagnostics,
            sourcePath: fullPath
        );

        var referenceFindings = diagnostics.Where(predicate: d => ReferenceLintCodes.Contains(value: d.Code)).ToList();

        Assert.True(
            condition: (referenceFindings.Count == 0),
            userMessage: ($"{relativePath} expected no reference-lint findings, got:{Environment.NewLine}"
                + string.Join(
                separator: Environment.NewLine,
                values: referenceFindings.Select(selector: d => $"{d.Code} {d.Span}: {d.Message}")
            ))
        );
    }
    [MemberData(nameof(GetShippedWorldFiles))]
    [Theory]
    public void EveryShippedWorldLintsCleanOfReferenceFindings(string relativePath) {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );
        var decompiled = WorldDecompiler.Decompile(jsonText: File.ReadAllText(path: fullPath));

        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            decompiled,
            diagnostics: diagnostics
        );

        Assert.NotNull(@object: parseResult.Value);

        var sourceMap = new SourceMap();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(path: fullPath),
            sourceMap: sourceMap,
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.NotNull(@object: loweringResult.Value);

        PuckLinter.LintReferences(
            loweringResult.Value,
            sourceMap,
            diagnostics,
            sourcePath: fullPath
        );

        var referenceFindings = diagnostics.Where(predicate: d => ReferenceLintCodes.Contains(value: d.Code)).ToList();

        Assert.True(
            condition: (referenceFindings.Count == 0),
            userMessage: ($"{relativePath} expected no reference-lint findings, got:{Environment.NewLine}"
                + string.Join(
                separator: Environment.NewLine,
                values: referenceFindings.Select(selector: d => $"{d.Code} {d.Span}: {d.Message}")
            ))
        );
    }
    public static TheoryData<string> GetShippedWorldFiles() => ShippedWorlds.Files();
    public static TheoryData<string> GetShippedWorldSources() => ShippedWorlds.Sources();
    [Fact]
    public void ModuleReferencingOnlyARootDeclaredNameYieldsNoFindingWhenLintedStandalone() {
        // games/freecell.world.json declares no basis and reads names games/solitaire.world.json's own body
        // supplies when it imports freecell as a sibling — a standalone lint pass over freecell alone cannot see
        // that supplying document, so it must stay silent rather than guess.
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: "games/freecell.world.json"
        );
        var decompiled = WorldDecompiler.Decompile(jsonText: File.ReadAllText(path: fullPath));

        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            decompiled,
            diagnostics: diagnostics
        );

        Assert.NotNull(@object: parseResult.Value);

        var sourceMap = new SourceMap();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(path: fullPath),
            sourceMap: sourceMap,
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.NotNull(@object: loweringResult.Value);
        Assert.Null(@object: loweringResult.Value["basis"]);

        PuckLinter.LintReferences(
            loweringResult.Value,
            sourceMap,
            diagnostics,
            sourcePath: fullPath
        );

        Assert.DoesNotContain(
            collection: diagnostics,
            filter: d => ReferenceLintCodes.Contains(value: d.Code)
        );
    }
    // The mirror of the case above: a module declares no `schema` and no `basis`, so it is validated by neither
    // verb — composing it as a root would report as missing every field its importer supplies.
    [Fact]
    public void ModuleWithNoSchemaAndNoBasisIsNotARoot() {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: "games/chess.world.json"
        );

        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path: fullPath))!.AsObject();

        Assert.Null(@object: document["schema"]);
        Assert.Null(@object: document["basis"]);
        Assert.False(condition: WorldSemanticValidator.IsRootDocument(loweredJson: document));
    }
    [Fact]
    public void RootDocumentReportsItsOwnMisspelledStateRowWithARealSpan() {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: "puck.world.json"
        );
        var decompiled = WorldDecompiler.Decompile(jsonText: File.ReadAllText(path: fullPath));

        // "claimTicks" is a state row the root itself declares and reads inside a rule's own `when` clause —
        // corrupting this one read (never the row declaration, and never another read of the same row) isolates
        // exactly one bad reference in an otherwise-clean document.
        const string Original = "when claimTicks > 0";
        const string Misspelled = "when claimTicksXTYPO > 0";
        var typoAt = decompiled.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: Original
        );

        Assert.True(
            condition: (typoAt >= 0),
            userMessage: $"expected fixture text '{Original}' in the decompiled root — update this test if the world's own rule text changed."
        );
        var mutated = string.Concat(
            str0: decompiled.AsSpan(
                length: typoAt,
                start: 0
            ),
            str1: Misspelled,
            str2: decompiled.AsSpan(start: (typoAt + Original.Length))
        );

        var parseDiagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            mutated,
            diagnostics: parseDiagnostics
        );

        Assert.NotNull(@object: parseResult.Value);

        var sourceMap = new SourceMap();
        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(path: fullPath),
            sourceMap: sourceMap,
            diagnostics: loweringDiagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.NotNull(@object: loweringResult.Value);

        var referenceDiagnostics = new DiagnosticBag();

        PuckLinter.LintReferences(
            loweringResult.Value,
            sourceMap,
            referenceDiagnostics,
            sourcePath: fullPath
        );

        var findings = referenceDiagnostics.Where(predicate: d => (d.Code == PuckDiagnosticCodes.LintUnresolvedState)).ToList();
        var finding = Assert.Single(collection: findings);

        Assert.Contains(
            "claimTicksXTYPO",
            finding.Message,
            StringComparison.Ordinal
        );
        Assert.True(
            condition: (finding.Span.Line > 0),
            userMessage: "expected a real line:column, not a whole-document SourceSpan.None finding"
        );
    }
    // A root that declares `schema` and no `basis` is still a root: a basis-shaped discriminator would leave every
    // standalone world (backgammon, chinese-checkers, moth, pipeline) with neither reference lint nor semantic
    // validation, so a misspelled row inside its own rules would be reported by nothing.
    [Fact]
    public void StandaloneRootWithNoBasisReportsItsOwnMisspelledStateRow() {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: "games/backgammon.world.json"
        );
        var decompiled = WorldDecompiler.Decompile(jsonText: File.ReadAllText(path: fullPath));

        const string Original = "when checkerPoint[w0] !=";
        const string Misspelled = "when checkerPointXTYPO[w0] !=";
        var typoAt = decompiled.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: Original
        );

        Assert.True(
            condition: (typoAt >= 0),
            userMessage: $"expected fixture text '{Original}' in the decompiled world — update this test if its own rule text changed."
        );
        var mutated = string.Concat(
            str0: decompiled.AsSpan(
                length: typoAt,
                start: 0
            ),
            str1: Misspelled,
            str2: decompiled.AsSpan(start: (typoAt + Original.Length))
        );

        var parseDiagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            mutated,
            diagnostics: parseDiagnostics
        );

        Assert.NotNull(@object: parseResult.Value);

        var sourceMap = new SourceMap();
        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(path: fullPath),
            sourceMap: sourceMap,
            diagnostics: loweringDiagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.NotNull(@object: loweringResult.Value);
        Assert.Null(@object: loweringResult.Value["basis"]);
        Assert.True(condition: WorldSemanticValidator.IsRootDocument(loweredJson: loweringResult.Value));

        var diagnostics = new DiagnosticBag();

        PuckLinter.LintReferences(
            loweringResult.Value,
            sourceMap,
            diagnostics,
            sourcePath: fullPath
        );
        WorldSemanticValidator.ValidateComposedWorld(
            loweringResult.Value,
            sourceMap,
            diagnostics,
            sourcePath: fullPath
        );

        var lintFinding = Assert.Single(
            collection: diagnostics,
            predicate: d => (d.Code == PuckDiagnosticCodes.LintUnresolvedState)
        );

        Assert.Contains(
            "checkerPointXTYPO",
            lintFinding.Message,
            StringComparison.Ordinal
        );
        Assert.True(
            condition: (lintFinding.Span.Line > 0),
            userMessage: "expected a real line:column, not a whole-document SourceSpan.None finding"
        );

        Assert.Contains(
            collection: diagnostics,
            filter: d => ((d.Code == PuckDiagnosticCodes.SemanticValidation) && d.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "checkerPointXTYPO"
            ))
        );
    }

    private static readonly string[] ReferenceLintCodes = [
        PuckDiagnosticCodes.LintUnresolvedState, PuckDiagnosticCodes.LintUnresolvedPrototype,
        PuckDiagnosticCodes.LintUnresolvedPlacementParent, PuckDiagnosticCodes.LintUnresolvedView,
        PuckDiagnosticCodes.LintUnknownChannelPrefix, PuckDiagnosticCodes.UnresolvedParent,
        PuckDiagnosticCodes.CompositionRefused,
    ];
}
