using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A refusal drawn about one effect of a rule names that effect's own line, not the line of the
/// <c>rule</c> that carries it.</summary>
/// <remarks>Driven from the table: every described construct whose document member lies under
/// <c>rules[].effects[]</c> owes a probe here, so an arm family added to the vocabulary fails by name rather than
/// going unexercised. Each probe authors three effects and makes the third refuse, so an answer of the rule's own
/// line — which is what an unregistered effect pointer resolves to — is a different line from the expected
/// one.</remarks>
public class ConstructEffectLineLawTests {
    private const string EffectsPrefix = "rules[].effects[]";
    // The row every probe reads, and the one name no probe declares.
    private const string Missing = "missingRow";
    private const string Rows = """
                slot hp = 1
                slot flag = 0
                table deck {
                    a = 1
                }
        """;

    // The third effect of each described arm, written so that it names `missingRow` and nothing else does. The
    // first two effects are always ordinary writes, so a refusal reported at either of them, or at the rule, is a
    // different line.
    private static readonly Dictionary<string, string> Thirds = new(comparer: StringComparer.Ordinal) {
        ["claim"] = "claim missingRow as item {\n        flag = 1\n    }",
        ["claim pair"] = "claim pair missingRow between left, right as link {\n        flag = 1\n    }",
        ["deal"] = "deal 1 from missingRow to deck",
        ["draw"] = "draw missingRow to deck",
        ["if"] = "if missingRow == 1 {\n        flag = 1\n    }",
        ["for each"] = "for each item in missingRow {\n        flag = 1\n    }",
        ["onFailure"] = "transaction {\n        flag = 1\n    } onFailure {\n        missingRow = 1\n    }",
        ["push"] = "push missingRow = hp",
        ["release"] = "release missingRow",
        ["remove"] = "remove missingRow",
        ["schedule"] = "schedule missingRow in 5s",
        ["shuffle"] = "shuffle missingRow with missingRow",
        ["transaction"] = "transaction {\n        missingRow = 1\n    }",
        ["transform"] = "transform boardCombine(row: missingRow, operation: Copy)",
    };

    private static string Probe(string third) => $"schema: \"puck.world.definition.v1\"\n\nstate {{\n    world {{\n{Rows}\n    }}\n}}\n\nrule \"r\" {{\n    hp = 1\n    flag = 1\n    {third}\n}}\n";
    // The 1-based line of the one statement that names the undeclared row.
    private static int RefusingLine(string source) {
        var lines = source.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n');

        for (var index = 0; (index < lines.Length); index++) {
            if (lines[index].Contains(
                comparisonType: StringComparison.Ordinal,
                value: Missing
            )) {
                return (index + 1);
            }
        }

        Assert.Fail(message: $"no line of the probe names '{Missing}':\n{source}");

        return 0;
    }
    // Every 0-based start line the real language server publishes about the undeclared row. A finding about a name
    // the document does not declare needs a resolvable directory, so the probe is written to a file.
    private static async Task<IReadOnlyList<int>> PublishedLinesAsync(string source, string keyword) {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-effect-line-{Guid.NewGuid():N}"
        );

        _ = Directory.CreateDirectory(path: directory);

        var path = Path.Combine(
            path1: directory,
            path2: $"{keyword}.puck"
        );

        try {
            await File.WriteAllTextAsync(
                contents: source,
                path: path
            ).ConfigureAwait(continueOnCapturedContext: true);

            return [.. (await LanguageServerClient.PublishedAsync(
                text: source,
                uri: new Uri(uriString: path).AbsoluteUri
            ).ConfigureAwait(continueOnCapturedContext: true))
                .Where(predicate: static diagnostic => diagnostic.Message.Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: Missing
                ))
                .Select(selector: static diagnostic => diagnostic.Line)];
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }

    public static TheoryData<string> Arms() => new(values: WorldConstructs.Table.Constructs
        .Where(predicate: static construct => construct.DocumentMember.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: EffectsPrefix
    ))
        .Select(selector: static construct => construct.Keyword)
        .Distinct(comparer: StringComparer.Ordinal)
        .Order(comparer: StringComparer.Ordinal));
    [MemberData(nameof(Arms))]
    [Theory]
    public void ARefusalAboutOneEffectNamesThatEffectsLine(string keyword) {
        Assert.True(
            condition: Thirds.TryGetValue(
                key: keyword,
                value: out var third
            ),
            userMessage: $"'{keyword}' is a described effect arm with no probe here, so nothing would fail if its refusal reported the rule's line"
        );

        var source = Probe(third: third!);
        var expected = RefusingLine(source: source);
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
            userMessage: $"nothing refused the '{keyword}' probe:\n{source}"
        );
        Assert.True(
            condition: (refusals[0].Span.Line == expected),
            userMessage: $"'{keyword}' refuses on line {expected}, and the refusal reports line {refusals[0].Span.Line}: {string.Join(
                separator: " | ",
                values: refusals.Select(selector: static refusal => $"{refusal.Code} @{refusal.Span.Line} {refusal.Message}")
            )}"
        );
    }
    // The reference lint resolves a row name written on `state`, `comparandState`, `fromState` or an operand
    // field, so the arms it can draw a finding about are the ones whose effect node carries one — which is why
    // this axis is two named probes rather than the whole arm table: a top-level effect and one nested inside a
    // `transaction`. Both ranges come from the same walk, so a registration the emitter drops moves them.
    [InlineData("remove")]
    [InlineData("transaction")]
    [Theory]
    public async Task TheLanguageServerRangesAnEffectFindingOnThatEffectsLine(string keyword) {
        var source = Probe(third: Thirds[key: keyword]);
        var expected = RefusingLine(source: source);
        var lines = await PublishedLinesAsync(
            keyword: keyword,
            source: source
        ).ConfigureAwait(continueOnCapturedContext: true);

        Assert.True(
            condition: (lines.Count > 0),
            userMessage: $"the language server published no finding naming '{Missing}' for the '{keyword}' probe:\n{source}"
        );
        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: lines.Distinct().Order()
            ),
            expected: (expected - 1).ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
        );
    }
}
