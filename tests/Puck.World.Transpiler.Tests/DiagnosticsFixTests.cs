using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Diagnostics that must be reachable, singular, and correctly coded: one error per mistake, at the span of
/// the thing that is wrong, under a code no other report site also uses.</summary>
public class DiagnosticsFixTests {
    private static (JsonObject Json, DiagnosticBag Diagnostics) Compile(string body) {
        var compilation = WorldSources.Compile(source: (WorldSources.Header + body));

        return ((compilation.Json ?? []), compilation.Diagnostics);
    }
    private static DiagnosticBag Parse(string body) => WorldSources.Parse(body: body).Diagnostics;

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

    // One mistake, one error, at the thing that is wrong, under the code no other report site uses. A case marked
    // alone is the only error its source draws: the parser resynchronizes after it.
    private static readonly Dictionary<string, Refusal> Mistakes = new(comparer: StringComparer.Ordinal) {
        ["a chained comparison"] = new(
            Body: "rule \"r\" {\n    when a[x] < b[y] < c[z]\n    hp[0] = 1\n}\n",
            Code: PuckDiagnosticCodes.ChainedComparison,
            Needle: "when a[x] < b[y] < c[z]"
        ) { Alone = true },
        ["an unknown kind after `as`"] = new(
            Body: "rule \"r\" {\n    when a[x] == b[y] as Floaty\n    hp[0] = 1\n}\n",
            Code: PuckDiagnosticCodes.UnknownKindAnnotation,
            Needle: "as Floaty"
        ) { Alone = true },
        // The ': Kind' spelling is the one the decompiler writes, so its diagnostic must name the rule too.
        ["an unknown kind after `:`"] = new(
            Body: "rule \"r\" {\n    when a[x] == b[y] : Floaty\n    hp[0] = 1\n}\n",
            Code: PuckDiagnosticCodes.UnknownKindAnnotation,
            Needle: ": Floaty"
        ),
        ["a row reference that is an expression"] = new(
            Body: "rule \"r\" {\n    remove someRow[a] + 1\n    hp[0] = 1\n}\n",
            Code: PuckDiagnosticCodes.RowReferenceExpected,
            Needle: "remove someRow[a] + 1"
        ),
        ["an effect outside a rule"] = new(
            Body: "push tally = 1",
            Code: PuckDiagnosticCodes.EffectOutsideEffectsBody,
            Needle: "push tally = 1"
        ),
        ["an option outside a decision"] = new(
            Body: "option \"stray\" {\n    score: 1\n}\n",
            Code: PuckDiagnosticCodes.DecisionStructure,
            Needle: "option \"stray\""
        ),
    };

    public static TheoryData<string> MistakeNames() => new(values: Mistakes.Keys);
    [MemberData(nameof(MistakeNames))]
    [Theory]
    public void AMistakeIsReportedOnceAtItsOwnLine(string name) => WorldSources.AssertRefusedBy(
        diagnostics: Parse(body: Mistakes[name].Body),
        label: name,
        refusal: Mistakes[name]
    );
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
                forEach = pieceCode
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
            Assert.IsType<JsonObject>(@object: json["look"])["alpha"].AsNumber()!.Value
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
}
