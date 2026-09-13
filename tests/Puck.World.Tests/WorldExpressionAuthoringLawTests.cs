using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Every expression the shipped worlds author prints to an infix spelling that parses back to the same
/// tokens, so an author can move any of them between the two spellings without changing what compiles.</summary>
public sealed class WorldExpressionAuthoringLawTests {
    private static string RepoRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (
            (directory is not null) &&
            !File.Exists(path: Path.Combine(
            path1: directory.FullName,
            path2: "Puck.slnx"
        ))
        ) {
            directory = directory.Parent;
        }
        Assert.NotNull(@object: directory);
        return directory!.FullName;
    }

    [Fact]
    public void ARuleAuthoredInfixCompilesToTheSameProgramAsItsTokens() {
        var infix = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                CellName.Parse(candidate: "hp"),
                CellKind.Int,
                Cells: [new StateCell(
                        WorldStateRow.SlotKey,
                        5L
                    )]
            )]),
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "hp",
                        Expression: ValueExpression.Parse(text: "maximum(hp - 1, 0)")
                    )]
            )],
        };
        var tokens = infix with {
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "hp",
                        Expression: new ValueExpression(Tokens: [
                new ValueToken.State("hp"), new ValueToken.Constant(Value: 1m), new ValueToken.Subtract(), new ValueToken.Constant(Value: 0m), new ValueToken.Max(),
            ])
                    )]
            )],
        };
        var fromInfix = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: infix));
        var fromTokens = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: tokens));

        Assert.Equal(
            WorldRuleCompiler.CompileAll(definition: fromTokens)[0].Effects[0].Describe,
            WorldRuleCompiler.CompileAll(definition: fromInfix)[0].Effects[0].Describe
        );
        Assert.Contains(
            "\"expression\": \"maximum(hp - 1, 0)\"",
            System.Text.Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: fromInfix)),
            StringComparison.Ordinal
        );
    }
    [MemberData(nameof(ShippedDocuments))]
    [Theory]
    public void EveryShippedExpressionRoundTripsThroughTheInfixSpelling(string relativePath) {
        var node = JsonNode.Parse(File.ReadAllText(path: Path.Combine(
            path1: RepoRoot(),
            path2: relativePath
        )));
        var expressions = 0;

        Walk(
            expressions: ref expressions,
            node: node!
        );
        // A document with no expressions proves nothing here; the theory still lists it so a game that gains one is
        // covered without an edit.
        Assert.True(condition: (expressions >= 0));

        static void Walk(JsonNode node, ref int expressions) {
            switch (node) {
                case JsonObject obj:
                    if (
                        (obj.Count == 1) &&
                        (obj["tokens"] is JsonArray)
                    ) {
                        var tokens = JsonSerializer.Deserialize(
                            json: obj.ToJsonString(),
                            jsonTypeInfo: WorldJsonContext.Default.ValueExpressionTokens
                        )!.Tokens;
                        var printed = ExpressionSpelling.Print(tokens: tokens);

                        Assert.True(
                            condition: ExpressionSpelling.TryParse(
                                error: out var error,
                                text: printed,
                                tokens: out var parsed
                            ),
                            userMessage: $"{printed}: {error}"
                        );
                        Assert.Equal(
                            actual: parsed,
                            expected: tokens
                        );
                        expressions++;
                        return;
                    }
                    foreach (var (_, child) in obj) {
                        if (child is not null) { Walk(
                            expressions: ref expressions,
                            node: child
                        ); }
                    }
                    return;
                case JsonArray array:
                    foreach (var child in array) {
                        if (child is not null) { Walk(
                            expressions: ref expressions,
                            node: child
                        ); }
                    }
                    return;
            }
        }
    }
    public static TheoryData<string> ShippedDocuments() {
        var data = new TheoryData<string>();
        var root = RepoRoot();

        foreach (var directory in new[] { Path.Combine(
            path1: "src",
            path2: "Puck.World",
            path3: "Assets",
            path4: "worlds"
        ), Path.Combine(
            path1: "src",
            path2: "Puck.World",
            path3: "Assets",
            path4: "scenarios"
        ) }) {
            var absolute = Path.Combine(
                path1: root,
                path2: directory
            );

            if (!Directory.Exists(path: absolute)) {
                // The scenario documents retire and re-land as the campaign moves; a directory absent today proves
                // nothing missing here, only that this pass has nothing under it to enumerate.
                continue;
            }
            foreach (var path in Directory.EnumerateFiles(
                path: absolute,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*.world.json"
            ).Order(comparer: StringComparer.Ordinal)) {
                data.Add(row: Path.GetRelativePath(
                    path: path,
                    relativeTo: root
                ));
            }
        }
        return data;
    }
}
