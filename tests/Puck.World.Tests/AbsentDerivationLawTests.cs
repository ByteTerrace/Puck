using System.Text;

using Xunit;

using Puck.Forge.Authoring;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the absent-is-valid contract: a document declaring only what it wants to state deserializes,
/// derives every unauthored section from what it did declare (or the smallest inert value), and validates.
/// </summary>
public sealed class AbsentDerivationLawTests {
    private static WorldDefinition Parse(string json) => WorldDefinitionSerialization.Deserialize(utf8Json: Encoding.UTF8.GetBytes(s: json));

    [Fact]
    public void NullWorld_DeserializesAndValidates() {
        // The exact contents of src/Puck.World/Assets/worlds/null.world.json.
        var definition = Parse(json: """
            {
              "schema": "puck.world.def.v1",
              "documentId": "null"
            }
            """);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: 0, actual: definition.Population.LocalSeats);
        Assert.Equal(expected: 0, actual: definition.Population.Capacity);
        Assert.Empty(collection: definition.Kits);
        Assert.Equal(expected: string.Empty, actual: definition.DefaultSeatKit);
    }
    [Fact]
    public void NullSeatWorld_DeserializesAndValidates() {
        // The exact contents of src/Puck.World/Assets/worlds/null-seat.world.json.
        var definition = Parse(json: """
            {
              "schema": "puck.world.def.v1",
              "documentId": "null-seat",
              "population": { "localSeats": 1 },
              "bodyMotionPrograms": [
                {
                  "name": "traverse",
                  "version": "puck.body-motion.v1",
                  "kind": "Motion",
                  "operations": [
                    "ResolveYawAttitudeAndPlanarFrame",
                    "ComputePlanarTargetVelocity",
                    "ShapePlanarVelocity",
                    "SnapYawToPlanarIntent",
                    "ApplyVerticalGravity",
                    "IntegratePlanarAndVerticalVelocity",
                    "CommitPose"
                  ]
                }
              ],
              "kits": [
                {
                  "name": "stander",
                  "bodyMotionProgram": "traverse",
                  "motion": {
                    "$type": "grounded",
                    "moveSpeed": 4,
                    "turnSpeed": 2.5,
                    "riseGravity": 28,
                    "fallGravity": 46,
                    "maxFallSpeed": 40,
                    "response": [],
                    "sprintMultiplier": 1
                  },
                  "collider": { "$type": "sphere", "radius": 0.5 },
                  "bodyContact": "Solid"
                }
              ],
              "defaultSeatKit": "stander"
            }
            """);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: 1, actual: definition.Population.LocalSeats);
        Assert.Equal(expected: 1, actual: definition.Population.Capacity);
        Assert.Equal(expected: SeatActivationPolicy.Eager, actual: Assert.Single(collection: definition.Population.SeatActivation));
        Assert.Equal(expected: WorldSpawnPointDefaults.ImplicitOriginId, actual: Assert.Single(collection: definition.Population.SeatSpawns));
    }
    // A nonzero-capacity document must carry a kit (see Kits_Empty_RequiredOnlyWhenPopulationImpliesABody), so the
    // two derivation laws below carry the same minimal kit null-seat.world.json does.
    private const string MinimalKitSection = """
        "bodyMotionPrograms": [
          { "name": "p", "version": "puck.body-motion.v1", "kind": "Motion", "operations": ["ResolveYawAttitudeAndPlanarFrame", "ComputePlanarTargetVelocity", "ShapePlanarVelocity", "SnapYawToPlanarIntent", "ApplyVerticalGravity", "IntegratePlanarAndVerticalVelocity", "CommitPose"] }
        ],
        "kits": [
          { "name": "k", "bodyMotionProgram": "p", "motion": { "$type": "grounded", "moveSpeed": 4, "turnSpeed": 2.5, "riseGravity": 28, "fallGravity": 46, "maxFallSpeed": 40, "response": [], "sprintMultiplier": 1 } }
        ]
        """;

    [Fact]
    public void LocalSeats_Absent_DerivesFromDeclaredSeatSpawnsRowCount() {
        var definition = Parse(json: $$"""
            {
              "schema": "puck.world.def.v1",
              "documentId": "seat-spawns-derive",
              "spawnPoints": [
                { "id": "a", "position": [0, 0, 0] },
                { "id": "b", "position": [1, 0, 0] }
              ],
              "population": { "seatSpawns": ["a", "b"] },
              {{MinimalKitSection}}
            }
            """);

        Assert.Equal(expected: 2, actual: definition.Population.LocalSeats);
        Assert.Equal(expected: 2, actual: definition.Population.Capacity);
    }
    [Fact]
    public void Capacity_Absent_DerivesFromLocalSeatsPlusNetworkPlayers() {
        var definition = Parse(json: $$"""
            {
              "schema": "puck.world.def.v1",
              "documentId": "capacity-derive",
              "population": { "localSeats": 2, "networkPlayers": 3 },
              {{MinimalKitSection}}
            }
            """);

        Assert.Equal(expected: 5, actual: definition.Population.Capacity);
    }
    [Fact]
    public void DefaultSeatKit_Absent_DerivesFromTheSoleKit() {
        var definition = Parse(json: """
            {
              "schema": "puck.world.def.v1",
              "documentId": "sole-kit-derive",
              "bodyMotionPrograms": [
                { "name": "p", "version": "puck.body-motion.v1", "kind": "Motion", "operations": ["ResolveYawAttitudeAndPlanarFrame", "ComputePlanarTargetVelocity", "ShapePlanarVelocity", "SnapYawToPlanarIntent", "ApplyVerticalGravity", "IntegratePlanarAndVerticalVelocity", "CommitPose"] }
              ],
              "kits": [
                {
                  "name": "solo",
                  "bodyMotionProgram": "p",
                  "motion": { "$type": "grounded", "moveSpeed": 4, "turnSpeed": 2.5, "riseGravity": 28, "fallGravity": 46, "maxFallSpeed": 40, "response": [], "sprintMultiplier": 1 }
                }
              ]
            }
            """);

        Assert.Equal(expected: "solo", actual: definition.DefaultSeatKit);
    }
    [Fact]
    public void Channels_Absent_ResolvesToTheThreeStandardMovementChannels() {
        var definition = Parse(json: """{"schema": "puck.world.def.v1", "documentId": "channels-derive"}""");

        Assert.Equal(expected: 3, actual: definition.Channels.Count);
        Assert.Contains(collection: definition.Channels, filter: c => (c.Name == "forward"));
        Assert.Contains(collection: definition.Channels, filter: c => (c.Name == "strafe"));
        Assert.Contains(collection: definition.Channels, filter: c => (c.Name == "turn"));
    }
    [Fact]
    public void CreationHash_Absent_ComputesFromTheEmbeddedDocument() {
        // The same "pip" creation null.world.json authored by hand, minus the hash field.
        var definition = Parse(json: """
            {
              "schema": "puck.world.def.v1",
              "documentId": "creation-hash-derive",
              "creations": [
                {
                  "id": "pip",
                  "document": {
                    "schema": "puck.creation.v1",
                    "name": "pip",
                    "palette": [{ "color": "#CCCCCC", "emissive": 0, "specular": 0, "shininess": 0 }],
                    "shapes": [
                      {
                        "id": 0, "name": "pip", "type": "Sphere",
                        "position": [0, 0.5, 0],
                        "rotation": [0, 0, 0, 1],
                        "scale": [0.5, 0.5, 0.5],
                        "material": 0, "blend": "Union"
                      }
                    ]
                  }
                }
              ]
            }
            """);
        var creation = Assert.Single(collection: definition.Creations);
        var canonical = CreationCanonicalizer.Canonicalize(document: creation.Document, source: creation.Id);

        Assert.Equal(expected: canonical.Hash, actual: creation.Hash);
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
    }
    [Fact]
    public void CreationWithANonIdentityQuaternion_RoundTripsThroughCanonicalizeWithUnchangedBytes() {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "quat-round-trip",
            Palette: [new PaletteEntryDocument(Color: "#CCCCCC", Emissive: 0, Specular: 0, Shininess: 0)],
            Shapes: [
                new ShapeDocument(
                    Id: 0,
                    Name: "leaning",
                    Type: AvatarPrimitive.Box,
                    Position: new(x: 0, y: 1, z: 0),
                    Rotation: new(x: 0.34202015f, y: 0, z: 0, w: 0.9396926f),
                    Scale: new(x: 1, y: 1, z: 1),
                    Material: 0,
                    Blend: null,
                    Smooth: null,
                    Group: null
                ),
            ],
            Frames: null
        );

        var first = CreationCanonicalizer.Canonicalize(document: document);
        var second = CreationCanonicalizer.Canonicalize(document: first.Document);

        Assert.Equal(expected: first.Hash, actual: second.Hash);
        Assert.Equal(expected: first.Bytes, actual: second.Bytes);
    }
    [Fact]
    public void Kits_Empty_RequiredOnlyWhenPopulationImpliesABody() {
        var zeroCapacity = Parse(json: """{"schema": "puck.world.def.v1", "documentId": "kits-empty-ok"}""");

        Assert.Empty(collection: zeroCapacity.Kits);

        var exception = Assert.Throws<InvalidDataException>(testCode: () => Parse(json: """{"schema": "puck.world.def.v1", "documentId": "kits-required", "population": {"localSeats": 1}}"""));

        Assert.Contains(expectedSubstring: "kits", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
}
