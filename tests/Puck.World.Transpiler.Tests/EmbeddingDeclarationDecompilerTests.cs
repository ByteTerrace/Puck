using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Decompiler coverage for embedding declarations: spaces, Vector rows, embeds pairs, transforms, and lock-aware literals.</summary>
public class EmbeddingDeclarationDecompilerTests {
    // A `lore` space of `dimensions`, and then `rows` as the world's rows.
    private static string Lore(int dimensions, string rows) => $$"""
        {
            "schema": "puck.world.definition.v1",
            "state": {
                "spaces": [
                    { "name": "lore", "model": "text-embedding-3-small", "revision": "1", "dimensions": {{dimensions}} }
                ],
                "world": [
                    {{rows}}
                ]
            }
        }
        """;

    private const string Memories = $$"""{ "name": "memories", "kind": "Vector", "space": "lore", "cells": [ { "key": "k1", "value": "{{WorldSources.SampleVector}}" } ] }""";

    /// <summary>A document, whether the lock carries <c>hello world</c> in both directions, and what the print must
    /// say.</summary>
    private sealed record Print(string Json, bool Locked, string[] Printed);

    private static readonly Dictionary<string, Print> Prints = new(comparer: StringComparer.Ordinal) {
        ["a spaces block and a Vector slot"] = new(
            Json: Lore(dimensions: 256, rows: """{ "name": "s", "kind": "Vector", "space": "lore" }"""),
            Locked: false,
            Printed: []
        ),
        ["a Vector cell with no lock prints its vector literal"] = new(
            Json: Lore(dimensions: 8, rows: Memories),
            Locked: false,
            Printed: ["k1 = vector(\"fwAAAAAAAAA\")"]
        ),
        ["a Vector cell the lock carries prints the text it embeds"] = new(
            Json: Lore(dimensions: 8, rows: Memories),
            Locked: true,
            Printed: ["embed(\"hello world\")"]
        ),
        ["a text row and its companion vectors print as one embeds table"] = new(
            Json: Lore(dimensions: 8, rows: """
                { "name": "loreLog", "kind": "Text", "cells": [ { "key": "entry1", "value": "hello world" } ] },
                { "name": "companionVectors", "kind": "Vector", "space": "lore", "cells": [ { "key": "entry1", "value": "fwAAAAAAAAA" } ] }
                """),
            Locked: true,
            Printed: ["table loreLog embeds(companionVectors)"]
        ),
    };

    public static TheoryData<string> PrintNames() => new(values: Prints.Keys);
    [MemberData(nameof(PrintNames))]
    [Theory]
    public void AnEmbeddingDeclarationPrintsAndRoundTrips(string name) {
        var print = Prints[name];
        var printed = WorldSources.AssertRoundTrips(
            embeddings: (print.Locked ? WorldSources.LoreLock("text-embedding-3-small", "hello world") : null),
            original: Assert.IsType<JsonObject>(@object: JsonNode.Parse(json: print.Json))
        );
        var missing = print.Printed.Where(predicate: text => !printed.Contains(comparisonType: StringComparison.Ordinal, value: text)).ToArray();

        Assert.True(
            condition: (missing.Length == 0),
            userMessage: $"{name}: missing [{string.Join(separator: " | ", values: missing)}]{Environment.NewLine}{printed}"
        );
    }
}
