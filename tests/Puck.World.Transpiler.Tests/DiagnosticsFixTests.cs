using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Diagnostics that must be reachable, singular, and correctly coded: one error per mistake, at the span of
/// the thing that is wrong, under a code no other report site also uses.</summary>
public class DiagnosticsFixTests {
    private static string[] Codes(DiagnosticBag diagnostics) =>
        [.. diagnostics.Where(predicate: static d => (d.Severity == DiagnosticSeverity.Error)).Select(selector: static d => d.Code)];
    private static (JsonObject Json, DiagnosticBag Diagnostics) Compile(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source,
            diagnostics: diagnostics
        );
        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value!,
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        return ((lowered.Value ?? []), diagnostics);
    }
    private static DiagnosticBag Parse(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var diagnostics = new DiagnosticBag();

        PuckParser.ParseDocumentWithDiagnostics(
            source,
            diagnostics: diagnostics
        );
        return diagnostics;
    }

    [Fact]
    public void AFileLevelDiagnosticRendersWithoutALineOrAQuotedSourceLine() {
        var diagnostics = new DiagnosticBag();

        diagnostics.ReportError(
            code: PuckDiagnosticCodes.SemanticValidation,
            message: "something the document says, nowhere in particular",
            span: SourceSpan.None
        );

        var report = diagnostics.FormatReport(
            filePath: "w.puck",
            sourceText: "// a header comment\nschema: \"x\"\n"
        );

        Assert.DoesNotContain(
            actualString: report,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "(1,1)"
        );
        Assert.DoesNotContain(
            actualString: report,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "a header comment"
        );
    }
    [Fact]
    public void AKeywordNameStaysLegalAsAnOrdinaryProperty() {
        // `when: 120` is a real capture-row field; only the keyword's own shapes are refused outside their block.
        var diagnostics = Parse(body: """
            captures {
                when: 120
            }
            """);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport()
        );
    }
    // ---- one mistake, one error ------------------------------------------------------------------------------

    [Fact]
    public void ChainedComparisonReportsOnceAndResynchronizes() {
        var diagnostics = Parse(body: """
            rule "r" {
                when a[x] < b[y] < c[z]
                hp[0] = 1
            }
            """);

        Assert.Equal(
            [PuckDiagnosticCodes.ChainedComparison],
            Codes(diagnostics: diagnostics)
        );
    }
    // ---- code registry ----------------------------------------------------------------------------------------

    [Fact]
    public void EveryDeclaredCodeIsDeclaredOnlyOnce() {
        var codes = typeof(PuckDiagnosticCodes)
            .GetFields()
            .Where(predicate: static f => (f.IsLiteral && (f.FieldType == typeof(string))))
            .Select(selector: static f => ((string)f.GetRawConstantValue()!))
            .ToArray();

        Assert.Equal(
            codes.Length,
            codes.Distinct(comparer: StringComparer.Ordinal).Count()
        );
        Assert.NotEqual(
            actual: PuckDiagnosticCodes.CompositionRefused,
            expected: PuckDiagnosticCodes.DuplicateRowId
        );
    }
    // ---- rule properties are not cell assignments -------------------------------------------------------------

    [Fact]
    public void ModeAndForEachSpelledWithEqualsStayRuleProperties() {
        var (json, diagnostics) = Compile(body: """
            rule "mode-equals" {
                mode = Edge
                forEach = "pieceCode"
                hp[0] = 1
            }
            """);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport()
        );
        var rule = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0]);

        Assert.Equal(
            "Edge",
            rule["mode"]!.GetValue<string>()
        );
        Assert.Equal(
            "pieceCode",
            rule["forEach"]!.GetValue<string>()
        );
        Assert.Single(collection: Assert.IsType<JsonArray>(@object: rule["effects"]));
    }
    [Fact]
    public void OrbitPitchAndYawConvertDegreesButABarePropertyOfThatNameDoesNot() {
        var (converted, convertedDiagnostics) = Compile(body: """
            views {
                seatRig "r" {
                    operations [
                        orbit(distance: 2.5m, pitch: 12deg, yaw: 110deg)
                    ]
                }
            }
            """);
        Assert.False(
            condition: convertedDiagnostics.HasErrors,
            userMessage: convertedDiagnostics.FormatReport()
        );

        var (_, refusedDiagnostics) = Compile(body: """
            poses {
                rows [ { name: "isolated", yaw: 110deg, pitch: 12deg } ]
            }
            """);

        Assert.Contains(
            collection: refusedDiagnostics,
            filter: d => (d.Code == PuckDiagnosticCodes.UnitOnUnknownField)
        );
    }
    [Fact]
    public void PercentIsCheckedAgainstTheFieldTableLikeEveryOtherUnit() {
        var (_, refused) = Compile(body: """
            placements {
                placement "p" {
                    prototype: x
                    yawDegrees: 45%
                }
            }
            """);
        Assert.Contains(
            collection: refused,
            filter: d => (d.Code == PuckDiagnosticCodes.UnitNotAdmitted)
        );

        var (json, accepted) = Compile(body: """
            look {
                alpha: 50%
            }
            """);
        Assert.False(
            condition: accepted.HasErrors,
            userMessage: accepted.FormatReport()
        );
        Assert.Equal(
            0.5,
            Assert.IsType<JsonObject>(@object: json["look"])["alpha"]!.GetValue<double>()
        );
    }
    // ---- reachability -----------------------------------------------------------------------------------------

    [Fact]
    public void RowReferenceThatIsAnExpressionReportsPuck003() {
        var diagnostics = Parse(body: """
            rule "r" {
                countdown someRow[a] + 1
                hp[0] = 1
            }
            """);

        Assert.Contains(
            collection: diagnostics,
            filter: d => (d.Code == PuckDiagnosticCodes.RowReferenceExpected)
        );
    }
    // ---- units ------------------------------------------------------------------------------------------------

    [Fact]
    public void ScheduleAcceptsEveryUnitTheSecondsDimensionAdmits() {
        var (json, diagnostics) = Compile(body: """
            rule "r" {
                schedule respawn[a] in 500ms
            }
            """);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport()
        );
        var effect = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0])["effects"])[0]);

        Assert.Equal(
            0.5m,
            effect["delaySeconds"]!.GetValue<decimal>()
        );
    }
    [Fact]
    public void StrayEffectStatementNamesTheBlockItNeeds() {
        var diagnostics = Parse(body: "push tally = 1");

        Assert.Contains(
            collection: diagnostics,
            filter: d => (d.Code == PuckDiagnosticCodes.EffectOutsideEffectsBody)
        );
    }
    [Fact]
    public void StrayOptionBlockNamesTheBlockItNeeds() {
        var diagnostics = Parse(body: """
            option "stray" {
                score: 1
            }
            """);

        var errors = diagnostics.Where(predicate: static d => (d.Severity == DiagnosticSeverity.Error)).ToArray();

        Assert.Contains(
            collection: errors,
            filter: d => (d.Code == PuckDiagnosticCodes.DecisionStructure)
        );
        Assert.Equal(
            3,
            errors[0].Span.Line
        );
    }
    [Fact]
    public void UnknownKindAfterAsReportsOnceAndResynchronizes() {
        var diagnostics = Parse(body: """
            rule "r" {
                when a[x] == b[y] as Floaty
                hp[0] = 1
            }
            """);

        Assert.Equal(
            [PuckDiagnosticCodes.UnknownKindAnnotation],
            Codes(diagnostics: diagnostics)
        );
    }
    [Fact]
    public void UnknownKindAfterColonReportsTheSameCodeAsTheAsSpelling() {
        // The ': Kind' spelling is the one the decompiler writes, so its diagnostic must name the rule too.
        var diagnostics = Parse(body: """
            rule "r" {
                when a[x] == b[y] : Floaty
                hp[0] = 1
            }
            """);

        Assert.Contains(
            collection: diagnostics,
            filter: d => (d.Code == PuckDiagnosticCodes.UnknownKindAnnotation)
        );
    }
}
