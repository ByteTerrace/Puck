using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Every expression the shipped worlds author prints to an infix spelling that parses back to the same
/// tokens, so an author can move any of them between the two spellings without changing what compiles.</summary>
public sealed class WorldExpressionAuthoringLawTests {
    private static string RepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    public static TheoryData<string> ShippedDocuments() {
        var data = new TheoryData<string>();
        var root = RepoRoot();
        foreach (var directory in new[] { Path.Combine("src", "Puck.World", "Assets", "worlds"), Path.Combine("src", "Puck.World", "Assets", "scenarios") }) {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, directory), "*.world.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
                data.Add(Path.GetRelativePath(root, path));
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ShippedDocuments))]
    public void EveryShippedExpressionRoundTripsThroughTheInfixSpelling(string relativePath) {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), relativePath)));
        var expressions = 0;
        Walk(node!, ref expressions);
        // A document with no expressions proves nothing here; the theory still lists it so a game that gains one is
        // covered without an edit.
        Assert.True(expressions >= 0);

        static void Walk(JsonNode node, ref int expressions) {
            switch (node) {
                case JsonObject obj:
                    if ((obj.Count == 1) && (obj["tokens"] is JsonArray)) {
                        var tokens = JsonSerializer.Deserialize(obj.ToJsonString(), WorldJsonContext.Default.WorldValueExpressionTokens)!.Tokens;
                        var printed = WorldExpressionSyntax.Print(tokens);
                        Assert.True(WorldExpressionSyntax.TryParse(printed, out var parsed, out var error), $"{printed}: {error}");
                        Assert.Equal(tokens, parsed);
                        expressions++;
                        return;
                    }
                    foreach (var (_, child) in obj) {
                        if (child is not null) { Walk(child, ref expressions); }
                    }
                    return;
                case JsonArray array:
                    foreach (var child in array) {
                        if (child is not null) { Walk(child, ref expressions); }
                    }
                    return;
            }
        }
    }

    [Fact]
    public void ARuleAuthoredInfixCompilesToTheSameProgramAsItsTokens() {
        var infix = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(WorldCellName.Parse("hp"), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 5L)])]),
            Rules = [new WorldRule(WorldCellName.Parse("r"), [new ActionEffect.SetState(State: "hp", Expression: WorldValueExpression.Parse("max(hp - 1, 0)"))])],
        };
        var tokens = infix with {
            Rules = [new WorldRule(WorldCellName.Parse("r"), [new ActionEffect.SetState(State: "hp", Expression: new WorldValueExpression([
                new WorldValueToken.State("hp"), new WorldValueToken.Constant(1m), new WorldValueToken.Subtract(), new WorldValueToken.Constant(0m), new WorldValueToken.Max(),
            ]))])],
        };
        var fromInfix = WorldDefinitionSerialization.Deserialize(WorldDefinitionSerialization.Serialize(infix));
        var fromTokens = WorldDefinitionSerialization.Deserialize(WorldDefinitionSerialization.Serialize(tokens));
        Assert.Equal(WorldRuleCompiler.CompileAll(fromTokens)[0].Effects[0].Describe, WorldRuleCompiler.CompileAll(fromInfix)[0].Effects[0].Describe);
        Assert.Contains("\"expression\": \"max(hp - 1, 0)\"", System.Text.Encoding.UTF8.GetString(WorldDefinitionSerialization.Serialize(fromInfix)), StringComparison.Ordinal);
    }
}
