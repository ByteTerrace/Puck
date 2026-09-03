using System.Text;

using Xunit;

using Puck.World.Authoring;
using Puck.SignedDistance;

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
        // src/Puck.World/Assets/worlds/null-seat.world.json's census, motion program and kit, plus the movement
        // channels and seat rig a nonzero census owes now that the engine declares neither (both are authored, in
        // standard.world.json) — the smallest document that puts one body in a world and lets a seat see it.
        var definition = Parse(json: $$"""
            {
              "schema": "puck.world.def.v1",
              "documentId": "null-seat",
              "bodies": { "localSeats": 1 },
              {{MinimalChannelSection}},
              {{MinimalCollisionSection}},
              {{MinimalDynamicsSection}},
              {{MinimalViewsSection}},
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
              "kits": { "rows": [
                {
                  "name": "stander",
                  "bodyMotionProgram": "traverse",
                  "motion": {
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
              ] },
              "defaultSeatKit": "stander"
            }
            """);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: 1, actual: definition.Population.LocalSeats);
        Assert.Equal(expected: 1, actual: definition.Population.Capacity);
        Assert.Equal(expected: SeatActivationPolicy.Eager, actual: Assert.Single(collection: definition.Population.SeatActivation));
        Assert.Equal(expected: WorldSpawnPointDefaults.ImplicitOriginId, actual: Assert.Single(collection: definition.Population.SeatSpawns));
    }

    // A nonzero-capacity document must carry a kit (see Kits_Empty_RequiredOnlyWhenPopulationImpliesABody), the
    // movement channels its motion program claims roles from (see Channels_Absent_ResolvesToNone), and a seat rig
    // (see Views_Absent_RequiredOnlyWhenPopulationImpliesABody) — none of which the engine supplies. The derivation
    // laws below carry the smallest set that satisfies all three.
    private const string MinimalChannelSection = """
        "channels": [
          { "name": "forward", "shape": "Bipolar", "role": "MoveAdvance" },
          { "name": "strafe", "shape": "Bipolar", "role": "MoveStrafe" },
          { "name": "turn", "shape": "Bipolar", "role": "Turn" }
        ]
        """;
    private const string MinimalCollisionSection = """
        "collision": { "requirements": [], "contactSkin": 0.02, "maxIterations": 4, "maxSlopeDegrees": 60, "gradientProbe": 0 }
        """;
    private const string MinimalDynamicsSection = """
        "dynamics": [ { "name": "chase", "f": 0.9549, "zeta": 1, "r": 1 } ]
        """;
    private const string MinimalKitSection = """
        "bodyMotionPrograms": [
          { "name": "p", "version": "puck.body-motion.v1", "kind": "Motion", "operations": ["ResolveYawAttitudeAndPlanarFrame", "ComputePlanarTargetVelocity", "ShapePlanarVelocity", "SnapYawToPlanarIntent", "ApplyVerticalGravity", "IntegratePlanarAndVerticalVelocity", "CommitPose"] }
        ],
        "kits": { "rows": [
          { "name": "k", "bodyMotionProgram": "p", "motion": { "moveSpeed": 4, "turnSpeed": 2.5, "riseGravity": 28, "fallGravity": 46, "maxFallSpeed": 40, "response": [], "sprintMultiplier": 1 } }
        ] }
        """;
    private const string MinimalViewsSection = """
        "views": {
          "layouts": [],
          "seatControl": { "yawReference": "World", "minPitch": -0.35, "maxPitch": 1.2 },
          "seatRig": {
            "name": "seatChase",
            "version": "puck.camera.v1",
            "operations": [
              { "$type": "orbit", "distance": 5.4626001, "yaw": 0, "pitch": 0.4145069, "pivotOffset": [0, 0, 0] },
              { "$type": "lookAt", "subject": { "$type": "reference" }, "targetOffset": [0, 1, 0], "worldAxes": false },
              { "$type": "fov", "fieldOfViewRadians": 0.9599311 },
              { "$type": "dynamics", "row": "chase" }
            ]
          }
        }
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
              "bodies": { "seatSpawns": ["a", "b"] },
              {{MinimalChannelSection}},
              {{MinimalCollisionSection}},
              {{MinimalKitSection}},
              {{MinimalDynamicsSection}},
              {{MinimalViewsSection}}
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
              "bodies": { "localSeats": 2, "networkPlayers": 3 },
              {{MinimalChannelSection}},
              {{MinimalCollisionSection}},
              {{MinimalKitSection}},
              {{MinimalDynamicsSection}},
              {{MinimalViewsSection}}
            }
            """);

        Assert.Equal(expected: 5, actual: definition.Population.Capacity);
    }
    [Fact]
    public void DefaultSeatKit_Absent_DerivesFromTheSoleKit() {
        var definition = Parse(json: $$"""
            {
              "schema": "puck.world.def.v1",
              "documentId": "sole-kit-derive",
              {{MinimalChannelSection}},
              "bodyMotionPrograms": [
                { "name": "p", "version": "puck.body-motion.v1", "kind": "Motion", "operations": ["ResolveYawAttitudeAndPlanarFrame", "ComputePlanarTargetVelocity", "ShapePlanarVelocity", "SnapYawToPlanarIntent", "ApplyVerticalGravity", "IntegratePlanarAndVerticalVelocity", "CommitPose"] }
              ],
              "kits": { "rows": [
                {
                  "name": "solo",
                  "bodyMotionProgram": "p",
                  "motion": { "moveSpeed": 4, "turnSpeed": 2.5, "riseGravity": 28, "fallGravity": 46, "maxFallSpeed": 40, "response": [], "sprintMultiplier": 1 }
                }
              ] }
            }
            """);

        Assert.Equal(expected: "solo", actual: definition.DefaultSeatKit);
    }
    [Fact]
    public void Channels_Absent_ResolvesToNone() {
        // The engine declares no channel of its own: the standard movement set is authored, in
        // src/Puck.World/Assets/worlds/standard.world.json, and a world inherits it by naming that basis.
        var definition = Parse(json: """{"schema": "puck.world.def.v1", "documentId": "channels-derive"}""");

        Assert.Empty(collection: definition.Channels);

        // The control: absent is inert, not permissive — a kit whose motion program needs the movement roles refuses
        // by name rather than resolving against a built-in table.
        var exception = Assert.Throws<InvalidDataException>(testCode: () => Parse(json: $$"""
            {
              "schema": "puck.world.def.v1",
              "documentId": "channels-required",
              "bodies": { "localSeats": 1 },
              {{MinimalKitSection}},
              {{MinimalDynamicsSection}},
              {{MinimalViewsSection}}
            }
            """));

        Assert.Contains(expectedSubstring: "requires channel role 'MoveAdvance'", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void Views_Absent_RequiredOnlyWhenPopulationImpliesABody() {
        // The engine ships no seat rig either (standard.world.json authors it), so a seatless document may author no
        // views and a census implying a body may not.
        var seatless = Parse(json: """{"schema": "puck.world.def.v1", "documentId": "views-absent-ok"}""");

        Assert.Empty(collection: seatless.Views.Layouts);

        var exception = Assert.Throws<InvalidDataException>(testCode: () => Parse(json: $$"""
            {
              "schema": "puck.world.def.v1",
              "documentId": "views-required",
              "bodies": { "localSeats": 1 },
              {{MinimalChannelSection}},
              {{MinimalKitSection}}
            }
            """));

        Assert.Contains(expectedSubstring: "views is required", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void CreationHash_Absent_ComputesFromTheEmbeddedDocument() {
        // The same "pip" creation null.world.json authored by hand, minus the hash field.
        var definition = Parse(json: """
            {
              "schema": "puck.world.def.v1",
              "documentId": "creation-hash-derive",
              "prototypes": [
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
            Palette: [new PaletteEntryDocument(Color: "#CCCCCC", Emissive: 0, Shininess: 0, Specular: 0)],
            Shapes: [
                new ShapeDocument(
                    Id: 0,
                    Name: "leaning",
                    Type: SdfSolidPrimitive.Box,
                    Position: new(x: 0, y: 1, z: 0),
                    Rotation: new(w: 0.9396926f, x: 0.34202015f, y: 0, z: 0),
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

        var exception = Assert.Throws<InvalidDataException>(testCode: () => Parse(json: """{"schema": "puck.world.def.v1", "documentId": "kits-required", "bodies": {"localSeats": 1}}"""));

        Assert.Contains(expectedSubstring: "kits", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
}
