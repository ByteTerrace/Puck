using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A document the schema refuses is refused in the author's words, at the member that failed: the refusal is
/// structured where the schema raises it (<see cref="WorldPayloadRefusal"/>), and the validator, the world loader and
/// <c>puck compile</c> all word it from that structure.</summary>
public sealed class SchemaRefusalLawTests {
    private static WorldPayloadRefusal Refuse(string json) {
        Assert.False(condition: WorldJsonPayload.TryRead(
            info: WorldJsonContext.Default.WorldDefinition,
            json: json,
            refusal: out var refusal,
            value: out _
        ));

        return refusal!;
    }

    [InlineData("""{ "host": { "width": "wide" } }""", "/host/width", "host.width: expected a whole number, found text")]
    [InlineData("""{ "host": { "width": 12.5 } }""", "/host/width", "host.width: expected a whole number, found a number")]
    [InlineData("""{ "host": { "fullscreen": 3 } }""", "/host/fullscreen", "host.fullscreen: expected true or false, found a number")]
    [InlineData("""{ "placements": { "rows": { "id": "a" } } }""", "/placements/rows", "placements.rows: expected a list, found a block")]
    [InlineData("""{ "placements": { "rows": [ { "id": "a", "yawDegrees": "north" } ] } }""", "/placements/rows/0/yawDegrees", "placements.rows[0].yawDegrees: expected a number, found text")]
    [Theory]
    public void AValueOfTheWrongKindIsRefusedAtItsPointerInTheAuthorsWords(string json, string pointer, string authored) {
        var refusal = Refuse(json: json);

        Assert.Equal(expected: pointer, actual: refusal.Pointer);
        Assert.Equal(expected: authored, actual: refusal.Authored);
        Assert.DoesNotContain(actualString: refusal.Authored, expectedSubstring: "System.");
    }
    [InlineData("""{ "placements": { "rows": [ { "id": "a", "prototypeId": "p", "position": [0, 0, 0], "yawDegrees": 0 } ] } }""", "placements.rows[0]: missing required member 'scale'")]
    [InlineData("""{ "placements": { "rows": [ { "id": "a" } ] } }""", "placements.rows[0]: missing required members 'prototypeId', 'position', 'yawDegrees', 'scale'")]
    [Theory]
    public void ARequiredMemberTheDocumentLeavesOutIsNamedAtTheObjectThatLacksIt(string json, string authored) {
        var refusal = Refuse(json: json);

        Assert.False(condition: refusal.IsWrongKind);
        Assert.Equal(expected: "/placements/rows/0", actual: refusal.Pointer);
        Assert.Equal(expected: authored, actual: refusal.Authored);
        Assert.DoesNotContain(actualString: refusal.Authored, expectedSubstring: "WorldPlacement");
    }
    [Fact]
    public void AnUnmappedMemberKeepsTheSerializersWordsWhenRequiredMembersAreAlsoMissing() {
        var refusal = Refuse(json: """{ "placements": { "rows": [ { "id": "a", "bogus": 1 } ] } }""");

        Assert.Equal(expected: "/placements/rows/0/bogus", actual: refusal.Pointer);
        Assert.DoesNotContain(actualString: refusal.Authored, expectedSubstring: "missing required");
    }
    [InlineData("""{ "render": { "toon": { "band": "nonsense" } } }""", "'nonsense'")]
    [InlineData("""{ "render": { "toon": { "band": 3 } } }""", "a number")]
    [Theory]
    public void AnEnumMemberNamesTheSpellingsItReadsAgainstWhatTheDocumentHolds(string json, string found) {
        var refusal = Refuse(json: json);

        Assert.Equal(expected: "/render/toon/band", actual: refusal.Pointer);
        Assert.StartsWith(actualString: refusal.Authored, expectedStartString: "render.toon.band: expected one of '");
        Assert.EndsWith(actualString: refusal.Authored, expectedEndString: $", found {found}");
        Assert.DoesNotContain(actualString: refusal.Authored, expectedSubstring: "SdfToonBand");
    }
    [Fact]
    public void AnAbsentPayloadIsRefusedWholeWithNothingToPlace() {
        var refusal = Refuse(json: " ");

        Assert.Equal(expected: string.Empty, actual: refusal.Pointer);
        Assert.Equal(expected: WorldJsonPayload.EmptyPayload, actual: refusal.Authored);
    }
    [Fact]
    public void TheValidatorPlacesTheRefusalOnTheLineThatAuthoredTheMember() {
        const string Source = """
            schema: "puck.world.definition.v1"
            documentId: "wrong-kind"

            host {
              width: "wide"
              height: 720
            }
            """;
        var finding = Assert.Single(
            collection: WorldSourceDiagnostics.Diagnose(source: Source, sourcePath: null, cancellationToken: TestContext.Current.CancellationToken),
            predicate: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.SchemaRejected)
        );

        Assert.Equal(expected: "host.width: expected a whole number, found text", actual: finding.Message);
        Assert.Equal(expected: 5, actual: finding.Span.Line);
    }
    [Fact]
    public void ALoaderRefusalCarriesTheAuthoredWordsAndNoPayloadDump() {
        var document = JsonNode.Parse(json: """{ "schema": "puck.world.definition.v1", "host": { "width": "wide" } }""")!;

        Assert.False(condition: WorldDefinitionFileSource.TryParseDocument(
            definition: out _,
            json: document.ToJsonString(),
            reason: out var reason,
            sourceName: "world.json"
        ));
        Assert.Equal(expected: "world.json is not a valid puck.world.definition.v1 document: host.width: expected a whole number, found text", actual: reason);
    }
}
