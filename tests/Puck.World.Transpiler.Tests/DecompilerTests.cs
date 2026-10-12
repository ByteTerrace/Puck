using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class DecompilerTests {
    // Explicit verdict rows retain their authored identities, all three statuses, and a stopped tick grid.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 30)]
    [InlineData(1, 30)]
    [InlineData(2, 30)]
    public void ANamedVerdictAndItsScheduleRoundTripWithoutBecomingAGeneratedTest(int status, int rate) {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse(json: $$"""
            {
              "schema": "puck.world.definition.v1",
              "simulation": { "rateHz": {{rate}} },
              "schedule": { "rows": [], "settleTicks": 6 },
              "state": { "world": [{
                "name": "authoredVerdict", "kind": "Int",
                "cells": [{ "key": "ok", "value": {{status}} }, { "key": "generation", "value": 0 }],
                "verdict": { "gate": "the authored gate", "status": "ok" }
              }] }
            }
            """));
        var printed = WorldSources.AssertRoundTrips(original: original);

        Assert.Contains(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "authoredVerdict");
        Assert.DoesNotContain(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "test \"decompiled\"");
    }
    // A quoted name is printed in the reader's own string grammar, so a quote, a backslash, a line break or a control
    // character in it reads back as itself.
    [InlineData("say \"hi\"")]
    [InlineData("back\\slash")]
    [InlineData("two\nlines\tand\u0001")]
    [Theory]
    public void AQuotedRuleNameReadsBackAsItself(string name) {
        var document = WorldSources.LowerClean(body: "state {\n    world {\n        slot flag = 0\n    }\n}\n\nrule \"r\" {\n    flag = 1\n}\n");

        document["rules"]![0]!["name"] = name;
        _ = WorldSources.AssertRoundTrips(original: document);
    }
}
