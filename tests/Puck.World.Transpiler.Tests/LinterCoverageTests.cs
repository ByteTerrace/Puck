using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
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
        var sourceMap = new SourceMap();
        var loweringResult = WorldCompiler.Compile(
            basePath: Path.GetDirectoryName(path: fullPath),
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: diagnostics,
            source: File.ReadAllText(path: fullPath),
            sourceMap: sourceMap
        );

        Assert.NotNull(@object: loweringResult.Json);

        PuckLinter.LintReferences(
            loweringResult.Json,
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
        var sourceMap = new SourceMap();
        var loweringResult = WorldCompiler.Compile(
            basePath: Path.GetDirectoryName(path: fullPath),
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: diagnostics,
            source: decompiled,
            sourceMap: sourceMap
        );

        Assert.NotNull(@object: loweringResult.Json);

        PuckLinter.LintReferences(
            loweringResult.Json,
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
        var sourceMap = new SourceMap();
        var loweringResult = WorldCompiler.Compile(
            basePath: Path.GetDirectoryName(path: fullPath),
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: diagnostics,
            source: decompiled,
            sourceMap: sourceMap
        );

        Assert.NotNull(@object: loweringResult.Json);
        Assert.Null(@object: loweringResult.Json["basis"]);

        PuckLinter.LintReferences(
            loweringResult.Json,
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
            path2: "games/dominoes.world.json"
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
        const string Original = "when $tick >= claimTicks";
        const string Misspelled = "when $tick >= claimTicksXTYPO";
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

        var sourceMap = new SourceMap();
        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldCompiler.Compile(
            basePath: Path.GetDirectoryName(path: fullPath),
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: loweringDiagnostics,
            source: mutated,
            sourceMap: sourceMap
        );

        Assert.NotNull(@object: loweringResult.Json);

        var referenceDiagnostics = new DiagnosticBag();

        PuckLinter.LintReferences(
            loweringResult.Json,
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

        var sourceMap = new SourceMap();
        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldCompiler.Compile(
            basePath: Path.GetDirectoryName(path: fullPath),
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: loweringDiagnostics,
            source: mutated,
            sourceMap: sourceMap
        );

        Assert.NotNull(@object: loweringResult.Json);
        Assert.Null(@object: loweringResult.Json["basis"]);
        Assert.True(condition: WorldSemanticValidator.IsRootDocument(loweredJson: loweringResult.Json));

        var diagnostics = new DiagnosticBag();

        PuckLinter.LintReferences(
            loweringResult.Json,
            sourceMap,
            diagnostics,
            sourcePath: fullPath
        );
        WorldSemanticValidator.ValidateComposedWorld(
            loweringResult.Json,
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

    // A fold names its family on the instruction and reads rows from a subprogram body, so the reference check
    // reaches both or a misbound fold lints clean.
    [Fact]
    public void AFoldsFamilyAndItsBodysReadsAreResolvedLikeAnyOtherRead() {
        var document = System.Text.Json.Nodes.JsonNode.Parse(json: /*lang=json*/ """
            {
              "schema": "puck.world.definition.v1",
              "state": { "world": [ { "name": "score", "kind": "Int", "value": 0 } ] },
              "rules": [
                {
                  "name": "tally",
                  "effects": [
                    {
                      "$type": "setState",
                      "state": "score",
                      "expression": {
                        "instructions": [ { "op": "Count", "family": "handXTYPO", "binder": "c", "subprogram": 0 } ],
                        "subprograms": [ { "name": "c", "arity": 0, "instructions": [ { "op": "Operand", "name": "bonusXTYPO" } ] } ]
                      }
                    }
                  ]
                }
              ]
            }
            """)!.AsObject();
        var diagnostics = new DiagnosticBag();

        PuckLinter.LintReferences(
            document,
            null,
            diagnostics,
            sourcePath: "fold.puck"
        );

        var messages = diagnostics
            .Where(predicate: static finding => (finding.Code == PuckDiagnosticCodes.LintUnresolvedState))
            .Select(selector: static finding => finding.Message)
            .ToList();

        Assert.Contains(
            collection: messages,
            expected: "Unresolved state row 'handXTYPO'."
        );
        Assert.Contains(
            collection: messages,
            expected: "Unresolved state row 'bonusXTYPO'."
        );
    }

    private static readonly string[] ReferenceLintCodes = [
        PuckDiagnosticCodes.LintUnresolvedState, PuckDiagnosticCodes.LintUnresolvedPrototype,
        PuckDiagnosticCodes.LintUnresolvedPlacementParent, PuckDiagnosticCodes.LintUnresolvedView,
        PuckDiagnosticCodes.LintUnknownChannelPrefix, PuckDiagnosticCodes.UnresolvedParent,
        PuckDiagnosticCodes.CompositionRefused,
    ];
}
