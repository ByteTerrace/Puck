using System.Text.Json.Nodes;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Diagnostics that must be reachable, singular, and correctly coded: one error per mistake, at the span of
/// the thing that is wrong, under a code no other report site also uses.</summary>
public class DiagnosticsFixTests {
    private static DiagnosticBag Parse(string body) {
        var source = $"schema: \"puck.world.def.v1\"\n\n{body}";
        var diagnostics = new DiagnosticBag();
        PuckParser.ParseDocumentWithDiagnostics(source, diagnostics: diagnostics);
        return diagnostics;
    }

    private static (JsonObject Json, DiagnosticBag Diagnostics) Compile(string body) {
        var source = $"schema: \"puck.world.def.v1\"\n\n{body}";
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source, diagnostics: diagnostics);
        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(parseResult.Value!, diagnostics: diagnostics);
        return (lowered.Value ?? [], diagnostics);
    }

    private static string[] Codes(DiagnosticBag diagnostics) =>
        [.. diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => d.Code)];

    // ---- one mistake, one error ------------------------------------------------------------------------------

    [Fact]
    public void ChainedComparisonReportsOnceAndResynchronizes() {
        var diagnostics = Parse("""
            rule "r" {
                when a[x] < b[y] < c[z]
                hp[0] = 1
            }
            """);

        Assert.Equal([PuckDiagnosticCodes.ChainedComparison], Codes(diagnostics));
    }

    [Fact]
    public void UnknownKindAfterAsReportsOnceAndResynchronizes() {
        var diagnostics = Parse("""
            rule "r" {
                when a[x] == b[y] as Floaty
                hp[0] = 1
            }
            """);

        Assert.Equal([PuckDiagnosticCodes.UnknownKindAnnotation], Codes(diagnostics));
    }

    [Fact]
    public void UnknownKindAfterColonReportsTheSameCodeAsTheAsSpelling() {
        // The ': Kind' spelling is the one the decompiler writes, so its diagnostic must name the rule too.
        var diagnostics = Parse("""
            rule "r" {
                when a[x] == b[y] : Floaty
                hp[0] = 1
            }
            """);

        Assert.Contains(diagnostics, d => d.Code == PuckDiagnosticCodes.UnknownKindAnnotation);
    }

    // ---- reachability -----------------------------------------------------------------------------------------

    [Fact]
    public void RowReferenceThatIsAnExpressionReportsPuck003() {
        var diagnostics = Parse("""
            rule "r" {
                countdown someRow[a] + 1
                hp[0] = 1
            }
            """);

        Assert.Contains(diagnostics, d => d.Code == PuckDiagnosticCodes.RowReferenceExpected);
    }

    [Fact]
    public void StrayOptionBlockNamesTheBlockItNeeds() {
        var diagnostics = Parse("""
            option "stray" {
                score: 1
            }
            """);

        var errors = diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Contains(errors, d => d.Code == PuckDiagnosticCodes.DecisionStructure);
        Assert.Equal(3, errors[0].Span.Line);
    }

    [Fact]
    public void StrayEffectStatementNamesTheBlockItNeeds() {
        var diagnostics = Parse("push tally = 1");

        Assert.Contains(diagnostics, d => d.Code == PuckDiagnosticCodes.EffectOutsideEffectsBody);
    }

    [Fact]
    public void AKeywordNameStaysLegalAsAnOrdinaryProperty() {
        // `when: 120` is a real capture-row field; only the keyword's own shapes are refused outside their block.
        var diagnostics = Parse("""
            captures {
                when: 120
            }
            """);

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport());
    }

    // ---- rule properties are not cell assignments -------------------------------------------------------------

    [Fact]
    public void ModeAndForEachSpelledWithEqualsStayRuleProperties() {
        var (json, diagnostics) = Compile("""
            rule "mode-equals" {
                mode = Edge
                forEach = "pieceCode"
                hp[0] = 1
            }
            """);

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport());
        var rule = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(json["rules"])[0]);
        Assert.Equal("Edge", rule["mode"]!.GetValue<string>());
        Assert.Equal("pieceCode", rule["forEach"]!.GetValue<string>());
        Assert.Single(Assert.IsType<JsonArray>(rule["effects"]));
    }

    // ---- units ------------------------------------------------------------------------------------------------

    [Fact]
    public void ScheduleAcceptsEveryUnitTheSecondsDimensionAdmits() {
        var (json, diagnostics) = Compile("""
            rule "r" {
                schedule respawn[a] in 500ms
            }
            """);

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport());
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(json["rules"])[0])["effects"])[0]);
        Assert.Equal(0.5m, effect["delaySeconds"]!.GetValue<decimal>());
    }

    [Fact]
    public void OrbitPitchAndYawConvertDegreesButABarePropertyOfThatNameDoesNot() {
        var (converted, convertedDiagnostics) = Compile("""
            views {
                seatRig "r" {
                    operations: [
                        orbit(distance: 2.5m, pitch: 12deg, yaw: 110deg)
                    ]
                }
            }
            """);
        Assert.False(convertedDiagnostics.HasErrors, convertedDiagnostics.FormatReport());

        var (_, refusedDiagnostics) = Compile("""
            poses {
                rows: [ { name: "isolated", yaw: 110deg, pitch: 12deg } ]
            }
            """);

        Assert.Contains(refusedDiagnostics, d => d.Code == PuckDiagnosticCodes.UnitOnUnknownField);
    }

    [Fact]
    public void PercentIsCheckedAgainstTheFieldTableLikeEveryOtherUnit() {
        var (_, refused) = Compile("""
            placements {
                placement "p" {
                    prototype: x
                    yawDegrees: 45%
                }
            }
            """);
        Assert.Contains(refused, d => d.Code == PuckDiagnosticCodes.UnitNotAdmitted);

        var (json, accepted) = Compile("""
            look {
                alpha: 50%
            }
            """);
        Assert.False(accepted.HasErrors, accepted.FormatReport());
        Assert.Equal(0.5, Assert.IsType<JsonObject>(json["look"])["alpha"]!.GetValue<double>());
    }

    // ---- code registry ----------------------------------------------------------------------------------------

    [Fact]
    public void EveryDeclaredCodeIsDeclaredOnlyOnce() {
        var codes = typeof(PuckDiagnosticCodes)
            .GetFields()
            .Where(static f => f.IsLiteral && (f.FieldType == typeof(string)))
            .Select(static f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(PuckDiagnosticCodes.DuplicateRowId, PuckDiagnosticCodes.CompositionRefused);
    }

    [Fact]
    public void AFileLevelDiagnosticRendersWithoutALineOrAQuotedSourceLine() {
        var diagnostics = new DiagnosticBag();
        diagnostics.ReportError(PuckDiagnosticCodes.SemanticValidation, "something the document says, nowhere in particular", SourceSpan.None);

        var report = diagnostics.FormatReport(sourceText: "// a header comment\nschema: \"x\"\n", filePath: "w.puck");

        Assert.DoesNotContain("(1,1)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("a header comment", report, StringComparison.Ordinal);
    }
}
