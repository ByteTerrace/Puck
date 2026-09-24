using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: every name the engine mints onto an owned identity's document, and every instance name
/// a transfer mints, is in the reserved form <see cref="GeneratedName"/> spells (<c>$</c> inside a document name,
/// <c>~</c> in an instance name that becomes a directory), so no row or instance name an author writes can equal one.
/// The mutation proof runs the same check over the spellings those sites would mint by joining with an author
/// character.</summary>
public sealed class WorldGeneratedNameLawTests {
    private static readonly Guid Device = Guid.Parse(input: "0f8fad5b-d9cb-469f-a165-70867728950e");

    private static IEnumerable<(string Site, string Name)> IdentityRows() => [
        ("identity move-speed row", WorldIdentityRows.MoveSpeed.Value),
        ("identity turn-speed row", WorldIdentityRows.TurnSpeed.Value),
        ("chat log", WorldIdentityRows.ChatLog.Value),
        ("chat inbox", WorldIdentityRows.ChatInbox.Value),
        ("controller machine slot", WorldIdentityRows.ControllerMachine(device: Device).Value),
        ("controller device slot", WorldIdentityRows.ControllerDevice(device: Device).Value),
        ("append counter", WorldIdentityRows.Sequence(row: CellName.Parse(candidate: "letters")).Value),
        ("append counter of a generated row", WorldIdentityRows.Sequence(row: WorldIdentityRows.ChatInbox).Value),
    ];
    private static List<string> Violations(IEnumerable<(string Site, string Name)> documentNames, IEnumerable<(string Site, string Name)> fileNames) {
        var violations = new List<string>();

        foreach (var (site, name) in documentNames) {
            if (!GeneratedName.IsGenerated(name: name) || GeneratedName.TryValidateAuthored(name: name, reason: out _)) {
                violations.Add(item: $"{site}: '{name}' is not in the reserved document form");
            }
        }
        foreach (var (site, name) in fileNames) {
            if (!GeneratedName.IsGeneratedFile(name: name) || GeneratedName.TryValidateAuthoredFile(name: name, reason: out _)) {
                violations.Add(item: $"{site}: '{name}' is not in the reserved file-backed form");
            }
        }

        return violations;
    }

    [Fact]
    public void EveryNameTheEngineMintsIsInTheReservedForm() {
        var violations = Violations(
            documentNames: IdentityRows(),
            fileNames: [
                ("fresh instance", WorldSessionResolver.FreshInstanceName(ordinal: 0, site: "dungeon")),
                ("fresh instance of a minted site", WorldSessionResolver.FreshInstanceName(ordinal: 7, site: "6~global7~dungeon")),
            ]
        );

        Assert.True(condition: (violations.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: violations));
        Assert.Equal(actual: WorldSessionResolver.FreshInstanceName(ordinal: 3, site: "dungeon"), expected: "dungeon~3");
        Assert.Equal(actual: WorldIdentityRows.Sequence(row: CellName.Parse(candidate: "letters")).Value, expected: "letters$seq");
    }
    // The mutation proof: the spellings these sites minted with an author character are caught.
    [Fact]
    public void TheLawCatchesASiteJoiningWithAnAuthorCharacter() => Assert.Equal(
        actual: Violations(
            documentNames: [("chat log", "chat-log"), ("append counter", "letters-seq")],
            fileNames: [("fresh instance", "dungeon-3")]
        ).Count,
        expected: 3
    );

    // A minimal document holding one authored name at a site, as raw JSON the loader parses.
    private static string DocumentWith(string site, string name) {
        var document = System.Text.Json.Nodes.JsonNode.Parse(utf8Json: WorldDefinitionSerialization.Serialize(definition: new WorldDefinition()))!.AsObject();
        var quoted = System.Text.Json.JsonSerializer.Serialize(value: name);
        var section = site switch {
            "state row" => $$"""{ "world": [ { "name": {{quoted}}, "kind": "Int" } ] }""",
            "cell key" => $$"""{ "world": [ { "name": "row", "kind": "Int", "cells": [ { "key": {{quoted}}, "value": 1 } ] } ] }""",
            "placement id" => $$"""{ "rows": [ { "id": {{quoted}}, "prototypeId": "p", "position": [0, 0, 0], "yawDegrees": 0, "scale": 1 } ] }""",
            "destination name" => $$"""[ { "name": {{quoted}}, "reference": "r", "durability": "persisted" } ]""",
            "rule name" => $$"""[ { "name": {{quoted}}, "effects": [] } ]""",
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(site)),
        };
        var member = site switch {
            "state row" or "cell key" => "state",
            "placement id" => "placements",
            "destination name" => "destinations",
            _ => "rules",
        };

        document[member] = System.Text.Json.Nodes.JsonNode.Parse(json: section);

        return document.ToJsonString();
    }
    private static bool Loads(string site, string name, out string reason) => WorldDefinitionFileSource.TryParseDocument(
        definition: out _,
        json: DocumentWith(name: name, site: site),
        reason: out reason,
        sourceName: "authored.world.json"
    );

    // The load door: an authored document name carrying '~' is refused by name at every site the loader reads, and
    // the same name spelled with '$' — the compiler's own document joiner — or '-' loads. Each refusal is the
    // control's mutation proof: only the one character differs.
    [InlineData("state row")]
    [InlineData("cell key")]
    [InlineData("placement id")]
    [InlineData("destination name")]
    [InlineData("rule name")]
    [Theory]
    public void TheLoaderRefusesAnAuthoredDocumentNameCarryingTheFileJoiner(string site) {
        Assert.False(condition: Loads(name: "a~b", reason: out var reason, site: site));
        Assert.Contains(actualString: reason, expectedSubstring: "'a~b' carries '~'");
        Assert.True(condition: Loads(name: "a$b", reason: out var generated, site: site), userMessage: generated);
        Assert.True(condition: Loads(name: "a-b", reason: out var authored, site: site), userMessage: authored);
    }
}
