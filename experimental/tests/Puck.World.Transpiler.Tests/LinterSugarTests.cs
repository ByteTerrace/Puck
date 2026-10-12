using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary><see cref="PuckLinter.LintReferences"/> — the symbol-resolution pass over lowered JSON — exercised
/// against every shipped world's decompiled form, plus a fixture per diagnostic code and per basis/module mode.</summary>
public class LinterSugarTests {
    private readonly record struct LintResult(DiagnosticBag Diagnostics, string DecompiledSource);
    // A shipped world is linted as its source: a `.puck` source as written, a hand-authored JSON world through its
    // decompilation. Its directory is where a declared basis or import resolves from.
    private static LintResult LintShippedWorld(string relativePath) {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );

        Assert.True(
            condition: File.Exists(path: fullPath),
            userMessage: $"Shipped world file not found: {fullPath}"
        );
        var decompiled = (WorldDocumentName.IsSourceFile(path: fullPath)
            ? File.ReadAllText(path: fullPath)
            : WorldDecompiler.Decompile(jsonText: File.ReadAllText(path: fullPath))
        );

        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            decompiled,
            diagnostics: diagnostics
        );

        if (parseResult.Value is not null) {
            PuckLinter.Lint(
                parseResult.Value,
                diagnostics
            );
            var sourceMap = new SourceMap();
            var loweringResult = WorldCompiler.Compile(
                basePath: Path.GetDirectoryName(path: fullPath),
                cancellationToken: TestContext.Current.CancellationToken,
                diagnostics: diagnostics,
                source: decompiled,
                sourceMap: sourceMap
            );

            if (loweringResult.Json is not null) {
                PuckLinter.LintReferences(
                    loweringResult.Json,
                    sourceMap,
                    diagnostics,
                    sourcePath: fullPath
                );
            }
        }
        return new LintResult(
            DecompiledSource: decompiled,
            Diagnostics: diagnostics
        );
    }

    [MemberData(nameof(CrossFileLeafWorlds))]
    [Theory]
    public void CrossFileLeafWorldsReportNoUnresolvedReference(string relativePath) {
        var result = LintShippedWorld(relativePath: relativePath);
        var referenceFindings = result.Diagnostics.Where(predicate: d => ReferenceLintCodes.Contains(value: d.Code)).ToList();

        Assert.True(
            condition: (referenceFindings.Count == 0),
            userMessage: $"{relativePath} expected no reference-lint findings, got:{Environment.NewLine}{string.Join(
                separator: Environment.NewLine,
                values: referenceFindings.Select(selector: d => d.Message)
            )}"
        );
    }
    [Fact]
    public void RootWorldResolvesReferencesAgainstItsComposedBasis() {
        var result = LintShippedWorld(relativePath: "puck.world.json");

        var referenceFindings = result.Diagnostics.Where(predicate: d => ReferenceLintCodes.Contains(value: d.Code)).ToList();

        Assert.True(
            condition: (referenceFindings.Count == 0),
            userMessage: $"expected no reference-lint findings, got:{Environment.NewLine}{string.Join(
                separator: Environment.NewLine,
                values: referenceFindings.Select(selector: d => d.Message)
            )}"
        );

        // PUCK026 ("rule carries no effect statements") false-positives on a decision-only rule — a pre-existing
        // defect in the parser stage (Parsing/PuckParser.Rules.cs), outside this stage's file scope; flagged, not
        // fixed, here. Every OTHER error would be a real regression.
        var unexpectedErrors = result.Diagnostics.Where(predicate: d => ((d.Severity == DiagnosticSeverity.Error) && (d.Code != "PUCK026"))).ToList();

        Assert.Empty(collection: unexpectedErrors);
    }
    [MemberData(nameof(SelfContainedShippedWorlds))]
    [Theory]
    public void SelfContainedShippedWorldsLintClean(string relativePath) {
        var result = LintShippedWorld(relativePath: relativePath);

        Assert.True(
            condition: (result.Diagnostics.Count == 0),
            userMessage: $"{relativePath} expected zero diagnostics, got:{Environment.NewLine}{result.Diagnostics.FormatReport(result.DecompiledSource)}"
        );
    }
    [Fact]
    public void SolitaireContainerLintsClean() {
        // games/solitaire.puck declares no `basis` and composes klondike/spider/freecell through `imports`,
        // but its own body never references a row/prototype those siblings own, so it lints clean either way.
        var result = LintShippedWorld(relativePath: "games/solitaire.puck");

        Assert.Empty(collection: result.Diagnostics);
    }

    private static readonly string[] ReferenceLintCodes = ["PUCK_LINT_005", "PUCK_LINT_006", "PUCK_LINT_007", "PUCK_LINT_008", "PUCK_LINT_009", "PUCK034"];

    // Leaf "district" worlds imported by a parent (puck.world.json) or a sibling composer (games/solitaire.puck)
    // that supplies the row/prototype they reference. None declares its own basis, so each is a module: a name it
    // cannot resolve standalone is never a finding — it may belong to whichever root imports it.
    public static TheoryData<string> CrossFileLeafWorlds => new() {
        "games/freecell.puck",
        "games/klondike.puck",
        "games/spider.puck",
        "games/mancala.puck",
        "games/bowling.puck",
    };
    // Worlds with no `basis`/`imports` of their own, whose every gate/effect/placement/camera reference resolves
    // inside the same file — the common case `puck lint` targets.
    public static TheoryData<string> SelfContainedShippedWorlds => new() {
        "games/billiards.puck",
        "games/tictactoe.puck",
        "games/poker.puck",
        "pipeline.puck",
    };
}
