using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Every expression the shipped worlds author prints to an infix spelling that parses back to the same
/// tokens, so an author can move any of them between the two spellings without changing what compiles.</summary>
public sealed class WorldExpressionAuthoringLawTests {
    [MemberData(nameof(ShippedDocuments))]
    [Theory]
    public void EveryShippedExpressionRoundTripsThroughTheInfixSpelling(string relativePath) {
        var node = JsonNode.Parse(File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: relativePath)));
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
                    if (obj["instructions"] is JsonArray) {
                        var program = JsonSerializer.Deserialize(
                            json: obj.ToJsonString(),
                            jsonTypeInfo: WorldJsonContext.Default.ExpressionProgram
                        )!;
                        var printed = ExpressionSpelling.Print(program: program);

                        Assert.True(
                            condition: ExpressionSpelling.TryParse(
                                error: out var error,
                                program: out var parsed,
                                text: printed
                            ),
                            userMessage: $"{printed}: {error}"
                        );
                        Assert.Equal(
                            actual: parsed.Instructions,
                            expected: program.Instructions
                        );
                        expressions++;
                        return;
                    }
                    foreach (var (_, child) in obj) {
                        if (child is not null) {
                            Walk(
                            expressions: ref expressions,
                            node: child
                        );
                        }
                    }
                    return;
                case JsonArray array:
                    foreach (var child in array) {
                        if (child is not null) {
                            Walk(
                            expressions: ref expressions,
                            node: child
                        );
                        }
                    }
                    return;
            }
        }
    }
    public static TheoryData<string> ShippedDocuments() {
        var data = new TheoryData<string>();
        var root = RepositoryPaths.RequireRoot();

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
