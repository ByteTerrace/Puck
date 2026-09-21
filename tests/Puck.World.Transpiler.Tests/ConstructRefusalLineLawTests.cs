using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A refusal drawn about a described construct reports on that construct's own authored line rather than
/// on the line of whatever encloses it.</summary>
/// <remarks>Driven through the real consumer: the lowering's source map, then
/// <see cref="WorldSemanticValidator.ValidateWorld"/>, which is what turns the path a refusal names into a span. A
/// path the emitter registers under a different spelling, or a refusal that names no path at all, resolves at the
/// nearest enclosing node instead — which is the line this law pins.</remarks>
public class ConstructRefusalLineLawTests {
    private const string Rows = """
                slot hp = 1
                slot flag = 0
        """;

    private static string Doc(string body) => $"schema: \"puck.world.definition.v1\"\n\n{body}\n";
    private static string Rule(string body) => Doc(body: $"state {{\n    world {{\n{Rows}\n    }}\n}}\n\nrule \"r\" {{\n{body}\n}}");
    // The 1-based line whose first word is `keyword`.
    private static int LineOpening(string source, string keyword) {
        var lines = source.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n');

        for (var index = 0; (index < lines.Length); index++) {
            var text = lines[index].TrimStart();

            if (
                text.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: $"{keyword} "
            ) ||
                text.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: $"{keyword}("
            ) ||
                string.Equals(
                a: text,
                b: keyword,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return (index + 1);
            }
        }

        Assert.Fail(message: $"no line of the probe opens with '{keyword}':\n{source}");

        return 0;
    }

    public static TheoryData<string, string> Probes() => new() {
        {
            "placement",
            Doc(body: """
                placements {
                    placement "p" {
                        position [0, 0, 0]
                        prototype: "missingProto"
                    }
                }
                """)
        },
        { "when", Rule(body: "    when missingRow == 1\n    flag = 1") },
        { "local", Rule(body: "    local acc = missingRow + 1\n    flag = acc") },
        {
            "option",
            Rule(body: """
                    decision {
                        periodSeconds: 1s
                        option "o" {
                            score: missingRow
                            flag = 1
                        }
                    }
                """)
        },
        {
            "interrupt",
            Rule(body: """
                    decision {
                        periodSeconds: 1s
                        interrupt missingRow == 1
                        option "o" {
                            score: 1
                            flag = 1
                        }
                    }
                """)
        },
        {
            "onNoChoice",
            Rule(body: """
                    decision {
                        periodSeconds: 1s
                        option "o" {
                            score: 1
                            flag = 1
                        }
                        onNoChoice {
                            missingRow = 1
                        }
                    }
                """)
        },
    };

    [MemberData(nameof(Probes))]
    [Theory]
    public void ARefusalInsideADescribedConstructNamesThatConstructsLine(string keyword, string source) {
        var sourceMap = new SourceMap();
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source,
            sourceMap: sourceMap
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: $"the probe does not lower: {compilation.Diagnostics.FormatReport(source)}"
        );

        var diagnostics = new DiagnosticBag();

        _ = WorldSemanticValidator.ValidateWorld(
            diagnostics: diagnostics,
            loweredJson: compilation.RequireJson(),
            sourceMap: sourceMap
        );

        var refusals = diagnostics
            .Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error))
            .ToArray();

        Assert.True(
            condition: (refusals.Length > 0),
            userMessage: $"nothing refused the probe for '{keyword}':\n{source}"
        );
        var expected = LineOpening(
            keyword: keyword,
            source: source
        );

        Assert.True(
            condition: (refusals[0].Span.Line == expected),
            userMessage: $"'{keyword}' is written on line {expected}, and its refusal reports line {refusals[0].Span.Line}: {string.Join(
                separator: " | ",
                values: refusals.Select(selector: static refusal => $"{refusal.Code} @{refusal.Span.Line} {refusal.Message}")
            )}"
        );
    }
}
