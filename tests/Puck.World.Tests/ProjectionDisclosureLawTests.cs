using System.Text;
using System.Text.Json;

using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// Proves the egress door: a presentation-tier projection carries no member from the redacted section set, a
/// replica-tier egress is the definition verbatim, and a projection hydrates back into a document a client can
/// render from. The negative (redacted members absent) is paired with the positive control (the disclosed members
/// are present and survive the round trip), so a projection that silently disclosed nothing at all would fail too.
/// </summary>
public sealed class ProjectionDisclosureLawTests {
    /// <summary>Every top-level document member the presentation tier must never carry, plus every nested member a
    /// redacted row shape would have brought with it.</summary>
    private static readonly string[] RedactedMembers = [
        "rules",
        "grants",
        "state",
        "market",
        "admission",
        "generation",
        "generators",
        "groups",
        "properties",
        "addons",
        "storage",
        "host",
        "authoring",
        "identity",
        "inputHold",
        "targetRegisters",
        "bodyMotionPrograms",
        "portals",
        "actions",
        "producers",
        "custom",
    ];

    [Fact]
    public void PresentationProjection_CarriesNoRedactedMember() {
        var definition = (Fixtures.BuildDocument() with {
            Metadata = new WorldMetadataSection(
                Title: "Play",
                Description: "The overworld hub.",
                Authors: [new WorldMetadataAuthor(Name: "Jane")],
                Tags: ["overworld"],
                Custom: new Dictionary<string, JsonElement> { ["secret"] = JsonDocument.Parse(json: "true").RootElement.Clone() }
            ),
        });
        var projection = WorldProjection.Compose(definition: definition, tier: WorldDisclosureTier.Presentation, authority: "boot", revision: 7);

        Assert.NotNull(projection);

        var json = Encoding.UTF8.GetString(bytes: WorldProjection.Serialize(projection: projection!));
        var found = new List<string>();

        CollectMemberNames(element: JsonDocument.Parse(json: json).RootElement, names: found);

        foreach (var redacted in RedactedMembers) {
            Assert.DoesNotContain(collection: found, expected: redacted);
        }

        // The control: the projection is not empty, and the disclosed sections really are in it — including
        // metadata's title/description, which cross in reduced form even though authors/tags/custom do not.
        Assert.Contains(collection: found, expected: "placements");
        Assert.Contains(collection: found, expected: "creations");
        Assert.Contains(collection: found, expected: "kits");
        Assert.Contains(collection: found, expected: "views");
        Assert.Contains(collection: found, expected: "hud");
        Assert.Contains(collection: found, expected: "provenance");
        Assert.Contains(collection: found, expected: "metadata");
        Assert.Contains(collection: found, expected: "title");
        Assert.Contains(collection: found, expected: "description");
    }
    [Fact]
    public void PresentationProjection_RoundTripsAndHydrates() {
        var definition = (Fixtures.BuildDocument() with {
            Metadata = new WorldMetadataSection(Title: "Play", Description: "The overworld hub.", Authors: [new WorldMetadataAuthor(Name: "Jane")], Tags: ["overworld"]),
        });
        var projection = WorldProjection.Compose(definition: definition, tier: WorldDisclosureTier.Presentation, authority: "127.0.0.1:5000", revision: 3);

        Assert.NotNull(projection);
        Assert.True(WorldProjection.TryDeserialize(utf8Json: WorldProjection.Serialize(projection: projection!), projection: out var decoded, reason: out var reason), reason);
        Assert.NotNull(decoded);
        Assert.Equal(expected: WorldDisclosureTier.Presentation, actual: decoded!.Provenance.Tier);
        Assert.Equal(expected: "127.0.0.1:5000", actual: decoded.Provenance.Authority);
        Assert.Equal(expected: 3, actual: decoded.Provenance.Revision);

        Assert.True(WorldProjection.TryToDefinition(projection: decoded, definition: out var hydrated, reason: out var hydrationReason), hydrationReason);
        Assert.NotNull(hydrated);
        Assert.Equal(expected: definition.Kits.Count, actual: hydrated!.Kits.Count);
        Assert.Equal(expected: definition.Kits[0].Name, actual: hydrated.Kits[0].Name);
        Assert.Equal(expected: definition.DefaultSeatKit, actual: hydrated.DefaultSeatKit);
        Assert.Equal(expected: definition.Placements.Count, actual: hydrated.Placements.Count);
        Assert.Equal(expected: definition.SimulationRateHz, actual: hydrated.SimulationRateHz);
        // The redacted sections hydrate neutral, never as a guess at what the composing authority authored.
        Assert.Empty(hydrated.Grants);
        Assert.Empty(hydrated.State);
        Assert.Empty(hydrated.Kits[0].Actions);
        Assert.Empty(hydrated.BodyMotionPrograms);
        Assert.Null(hydrated.Admission);
        // Metadata's disclosed half survives the round trip; its withheld half hydrates neutral, not as a guess.
        Assert.Equal(expected: "Play", actual: hydrated.Metadata!.Title);
        Assert.Equal(expected: "The overworld hub.", actual: hydrated.Metadata.Description);
        Assert.Null(@object: hydrated.Metadata.Authors);
        Assert.Null(@object: hydrated.Metadata.Tags);
        Assert.Null(@object: hydrated.Metadata.Custom);
    }
    [Fact]
    public void ReplicaTier_SendsTheDefinitionVerbatim() {
        var definition = Fixtures.BuildDocument();

        Assert.Null(WorldProjection.Compose(definition: definition, tier: WorldDisclosureTier.Replica, authority: "boot", revision: 1));
        Assert.Null(WorldProjection.Compose(definition: definition, tier: WorldDisclosureTier.Frames, authority: "boot", revision: 1));

        var first = WorldDefinitionSerialization.Serialize(definition: definition);
        var reloaded = WorldDefinitionSerialization.Deserialize(utf8Json: first);

        Assert.Equal(expected: first, actual: WorldDefinitionSerialization.Serialize(definition: reloaded));
    }
    [Fact]
    public void UnauthoredAdmissionEntry_ResolvesToPresentation() {
        var entry = Fixtures.AnyAuthorityArrivals();

        Assert.Null(entry.Disclosure);
        Assert.Equal(expected: WorldDisclosureTier.Presentation, actual: entry.Tier);
        Assert.Equal(expected: WorldDisclosureTier.Replica, actual: (entry with { Disclosure = WorldDisclosureTier.Replica }).Tier);

        Assert.Null(WorldAdmissionDoor.TryAdmitArrival(entries: [entry], sourceAuthority: "127.0.0.1:9", verdict: out var verdict));
        Assert.NotNull(verdict);
        Assert.Equal(expected: WorldDisclosureTier.Presentation, actual: verdict!.Tier);

        Assert.Null(WorldAdmissionDoor.TryAdmitArrival(entries: [entry with { Disclosure = WorldDisclosureTier.Replica }], sourceAuthority: "127.0.0.1:9", verdict: out var replica));
        Assert.Equal(expected: WorldDisclosureTier.Replica, actual: replica!.Tier);
    }
    [Fact]
    public void ProjectionParse_RefusesForeignAndUnreservedMembers() {
        var projection = WorldProjection.Compose(definition: Fixtures.BuildDocument(), tier: WorldDisclosureTier.Presentation, authority: "boot", revision: 1)!;
        var json = Encoding.UTF8.GetString(bytes: WorldProjection.Serialize(projection: projection));

        Assert.False(WorldProjection.TryDeserialize(utf8Json: Encoding.UTF8.GetBytes(s: json.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"$schema\"", oldValue: "\"schema\"")), projection: out _, reason: out var schemaReason));
        Assert.Contains(expectedSubstring: "puck.world.projection.v1", actualString: schemaReason, comparisonType: StringComparison.Ordinal);

        var withUnknown = json.Insert(startIndex: (json.IndexOf(comparisonType: StringComparison.Ordinal, value: '{') + 1), value: "\"rules\": [],");

        Assert.False(WorldProjection.TryDeserialize(utf8Json: Encoding.UTF8.GetBytes(s: withUnknown), projection: out _, reason: out var unknownReason));
        Assert.Contains(expectedSubstring: "rules", actualString: unknownReason, comparisonType: StringComparison.Ordinal);

        var withReserved = json.Insert(startIndex: (json.IndexOf(comparisonType: StringComparison.Ordinal, value: '{') + 1), value: "\"_note\": \"kept\",");

        Assert.True(WorldProjection.TryDeserialize(utf8Json: Encoding.UTF8.GetBytes(s: withReserved), projection: out var reserved, reason: out var reservedReason), reservedReason);
        Assert.NotNull(reserved!.Extensions);
    }

    private static void CollectMemberNames(JsonElement element, List<string> names) {
        switch (element.ValueKind) {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject()) {
                    names.Add(item: member.Name);
                    CollectMemberNames(element: member.Value, names: names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) {
                    CollectMemberNames(element: item, names: names);
                }

                break;
            default:
                break;
        }
    }
}
