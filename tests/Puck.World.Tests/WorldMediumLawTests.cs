using System.Numerics;
using Puck.Maths;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.Physics.Motion;

namespace Puck.World.Tests;

/// <summary>
/// The lattice-row MEDIUM primitive that replaced the global <c>water</c> section: a lattice field carrying
/// <c>lattice.medium</c> is a fluid free surface (value times heightScale, over the lattice origin) every active
/// body samples at its coupled cell each tick — the same coupling <see cref="WorldFieldLattice.TryBodyCellOf"/>
/// resolves for <c>emit</c>/<c>expose</c>. See <see cref="WorldFieldLatticeLawTests"/> for the coupling/surface-math
/// laws at the lattice level; this suite proves the validator's refusals and the full server-tier wiring
/// (<see cref="WorldPopulation.SampleMediumSurfaces"/> → <c>WorldBody.SetMediumSurface</c> →
/// <c>ApplyBuoyancyAndSurface</c>'s <c>Submerged</c> fact).
/// </summary>
public sealed class WorldMediumLawTests {
    private static WorldStateLatticeTopology Topology(int width = 4, int depth = 4) => new(
        Name: "world",
        Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
        CellSize: 1f,
        Width: width,
        Depth: depth,
        Layers: 1
    );

    [Fact]
    public void MediumWithHeightScaleZeroIsRefusedByName() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
                World: [
                    new WorldStateRow(
                        Name: WorldCellName.Parse(candidate: "medium"),
                        Kind: CellKind.Fixed,
                        Lattice: new WorldStateLatticeTrait(Topology: "world", Medium: new WorldLatticeMedium())
                    ),
                ],
                Lattices: [Topology()]
            ),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "medium requires a heightScale greater than 0");
    }
    [Fact]
    public void MediumOutsideTheLatticeTraitRefusesByName() {
        // The document below is a VALID medium row — the sabotage moves "medium" from inside "lattice" to a
        // sibling of it, proving medium can only ever be authored through the lattice trait's own JSON shape.
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
                World: [
                    new WorldStateRow(
                        Name: WorldCellName.Parse(candidate: "medium"),
                        Kind: CellKind.Fixed,
                        Lattice: new WorldStateLatticeTrait(Topology: "world", HeightScale: 5f, Color: "#3B7BD6")
                    ),
                ],
                Lattices: [Topology()]
            ),
        };
        var node = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition)))!.AsObject();
        var row = node["state"]!.AsObject()["world"]!.AsArray()[0]!.AsObject();

        row["medium"] = new JsonObject();

        var sabotaged = Encoding.UTF8.GetBytes(s: node.ToJsonString());
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: sabotaged));

        Assert.IsType<JsonException>(@object: exception.InnerException);
        Assert.Contains(expectedSubstring: "medium", actualString: exception.InnerException!.Message, comparisonType: StringComparison.Ordinal);
    }

    private static WorldDefinition BuildSwimKitDocument(WorldStateLatticeTopology topology) {
        var channels = new WorldChannel[] {
            new(Name: "forward", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveAdvance),
            new(Name: "strafe", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveStrafe),
            new(Name: "turn", Shape: ChannelShape.Bipolar, Role: ChannelRole.Turn),
            new(Name: "up", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveUp),
        };
        var swim = new BodyMotionProgram(
            Name: "swim",
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
                BodyMotionOp.ComputeSwimTargetVelocity,
                BodyMotionOp.ShapePlanarVelocity,
                BodyMotionOp.ApplyBuoyancyAndSurface,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        );
        var wander = new BodyMotionProgram(Name: "wander", Version: "puck.body-motion.v1", Kind: BodyProgramKind.Producer, Operations: [BodyMotionOp.ProduceWanderIntent]);
        var kit = new WorldKit(
            Name: "diver-test",
            BodyMotionProgram: "swim",
            Motion: new WorldMotionModel.Swim(
                ThrustSpeed: 3.2f,
                TurnSpeed: 2.2f,
                VerticalThrustFraction: 0.75f,
                Response: [],
                Buoyancy: 0.5f,
                MaxRiseSpeed: 2.4f,
                MaxSinkSpeed: 3f,
                SurfaceSettleRate: 6f,
                FloatDepth: 1f,
                SprintMultiplier: 1f
            ),
            ProducersRaw: new Dictionary<string, BodyProgramParameters> {
                ["wander"] = Fixtures.TravelerWanderParameters,
            },
            Collider: null
        );

        return Fixtures.BuildDocument() with {
            ChannelsRaw = channels,
            BodyMotionProgramsRaw = [swim, wander],
            KitRowsRaw = [kit],
            DefaultSeatKitRaw = "diver-test",
            StateRaw = new WorldStateSection(
                World: [
                    Fixtures.MediumRow(),
                ],
                Lattices: [topology]
            ),
        };
    }

    // The same medium, spelled as a hold row on an ordinary grounded kit instead of as a swim arm. Every number is
    // the swim kit's own, so the two documents differ in nothing but which spelling names the law.
    private static WorldDefinition BuildMediumHoldDocument(WorldStateLatticeTopology topology, float buoyancy = 0.5f) {
        var swimDocument = BuildSwimKitDocument(topology: topology);
        var hold = new BodyMotionProgram(
            Name: "swim",
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
                BodyMotionOp.ResolveHold,
                BodyMotionOp.ComputePlanarTargetVelocity,
                BodyMotionOp.ShapePlanarVelocity,
                BodyMotionOp.ApplyHold,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        );
        var wander = new BodyMotionProgram(Name: "wander", Version: "puck.body-motion.v1", Kind: BodyProgramKind.Producer, Operations: [BodyMotionOp.ProduceWanderIntent]);
        var kit = (swimDocument.Kits[0] with {
            Motion = new WorldMotionModel.Grounded(
                MoveSpeed: 3.2f,
                TurnSpeed: 2.2f,
                RiseGravity: 1f,
                FallGravity: 1f,
                MaxFallSpeed: 1f,
                SprintMultiplier: 1f,
                Response: [],
                Holds: [
                    new WorldHold(
                        Bond: BodyHoldBond.Medium,
                        Hold: BodyHoldKind.None,
                        Medium: new WorldHoldMedium(
                            Buoyancy: buoyancy,
                            FloatDepth: 1f,
                            MaxRiseSpeed: 2.4f,
                            MaxSinkSpeed: 3f,
                            SurfaceSettleRate: 6f,
                            ThrustFraction: 0.75f
                        ),
                        Name: "water"
                    ),
                ]
            ),
        });

        return (swimDocument with {
            BodyMotionProgramsRaw = [hold, wander],
            KitRowsRaw = [kit],
        });
    }
    private static FixedVector3[] MediumTrace(WorldDefinition definition, int ticks = 240) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var trace = new FixedVector3[ticks];

        for (var tick = 0; (tick < ticks); tick++) {
            // A steady rise toward the float line: the vertical thrust the medium's own fraction scales, carrying
            // the body up through the bob band into the settle half, so one trace exercises both halves of the law.
            // Driving DOWN instead would leave the lattice column within a tick and measure nothing about the medium.
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: 3, value: FixedQ4816.One));
            fixture.Step();

            trace[tick] = body.FixedPosition;
        }

        return trace;
    }

    [Fact]
    public void TheSwimArmAndAMediumHoldRowAreTheSameLaw_WhereChangingOneMediumFacetDiverges() {
        var topology = Topology(depth: 2, width: 2);
        var arm = MediumTrace(definition: BuildSwimKitDocument(topology: topology));
        var row = MediumTrace(definition: BuildMediumHoldDocument(topology: topology));

        // Not "similar": the arm compiles into the row, so the same tick produces the same fixed-point pose.
        Assert.Equal(expected: arm, actual: row);
        // The discriminating control — one facet of the medium's own law, moved.
        Assert.NotEqual(expected: arm, actual: MediumTrace(definition: BuildMediumHoldDocument(buoyancy: -0.5f, topology: topology)));
    }
    [Fact]
    public void AMediumHoldInADryWorld_RefusesByName_WhereTheSameHoldWithAMediumRowIsAdmitted() {
        var topology = Topology();
        var admitted = BuildMediumHoldDocument(topology: topology);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);

        var dry = (admitted with { StateRaw = new WorldStateSection() });

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: dry, reason: out var dryReason));
        Assert.Contains(actualString: dryReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "requires a medium lattice row");
    }
    [Fact]
    public void AMediumHoldWithNoLaw_AndASurfaceHoldCarryingOne_BothRefuseByName() {
        var topology = Topology();
        var admitted = BuildMediumHoldDocument(topology: topology);
        var grounded = ((WorldMotionModel.Grounded)admitted.Kits[0].Motion!);

        WorldDefinition WithHolds(params WorldHold[] holds) => (admitted with {
            KitRowsRaw = [(admitted.Kits[0] with { Motion = (grounded with { Holds = holds }) })],
        });

        var lawless = WithHolds(new WorldHold(
            Bond: BodyHoldBond.Medium,
            Hold: BodyHoldKind.None,
            Name: "water"
        ));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: lawless, reason: out var lawlessReason));
        Assert.Contains(actualString: lawlessReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "medium is required");

        var misplaced = WithHolds(new WorldHold(
            Bond: BodyHoldBond.Surface,
            Cone: new Vector2(x: 0f, y: 60f),
            Hold: BodyHoldKind.Gravity,
            Medium: new WorldHoldMedium(
                Buoyancy: 0.5f,
                FloatDepth: 1f,
                MaxRiseSpeed: 2.4f,
                MaxSinkSpeed: 3f,
                SurfaceSettleRate: 6f,
                ThrustFraction: 0.75f
            ),
            Name: "floor",
            Reach: 1f
        ));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: misplaced, reason: out var misplacedReason));
        Assert.Contains(actualString: misplacedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "medium is refused");
    }
    [Fact]
    public void SwimKitInAWorldWithNoMediumRowRefusesByName() {
        var dry = BuildSwimKitDocument(topology: Topology()) with {
            StateRaw = new WorldStateSection(),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: dry, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "declares a swim model but the world authors no medium lattice row");
    }
    [Fact]
    public void ABodyStandingInAMediumCellGetsSubmergedAfterAStep_AndABodyOnDryCellsDoesNot() {
        // A 2x2 lattice at the origin covers seat-1's spawn (0,0,0) but not seat-2's (2,0,0) — the "same lattice,
        // in vs out of coverage" contrast a single fixture proves both halves through, since every local seat runs
        // the SAME resolved seat kit (see the puck-world skill's own seat/kit remarks).
        using var fixture = Fixtures.FreshServer(definition: BuildSwimKitDocument(topology: Topology(depth: 2, width: 2)));
        var wet = WorldPrincipal.Seat(slot: 0);
        var dry = WorldPrincipal.Seat(slot: 1);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: wet, Slot: wet.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: dry, Slot: dry.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        fixture.Step();

        var wetBody = fixture.Server.Body(index: wet.Index)!;
        var dryBody = fixture.Server.Body(index: dry.Index)!;

        // Spawn Y (0) sits below the medium's surface (origin.Y 0 + value 1 * heightScale 5 = 5), so the coupled
        // seat is submerged from the very first step with no drive needed.
        Assert.True(condition: wetBody.Submerged);
        Assert.False(condition: dryBody.Submerged);
    }
    [Fact]
    public void AMediumRowSurvivesCompileDecompileRoundTripExactly() {
        var trait = new WorldStateLatticeTrait(
            Topology: "world",
            Initial: 0.5f,
            Min: 0f,
            Max: 1f,
            HeightScale: 3f,
            Color: "#112233",
            Medium: new WorldLatticeMedium()
        );
        var state = new WorldStateSection(
            World: [new WorldStateRow(Name: WorldCellName.Parse(candidate: "medium"), Kind: CellKind.Fixed, Lattice: trait)],
            Lattices: [Topology()]
        );
        var compiled = WorldFieldsSection.Compile(state: state)!;

        Assert.True(condition: compiled.Fields[0].Medium);

        var decompiled = WorldFieldsSection.ToStateSection(composite: compiled);

        Assert.Equal(expected: trait, actual: decompiled.World![0].Lattice);
    }
    [Fact]
    public void AMediumRowSurvivesWorldSaveRoundTripExactly() {
        var trait = new WorldStateLatticeTrait(
            Topology: "world",
            Initial: 0.5f,
            Min: 0f,
            Max: 1f,
            HeightScale: 3f,
            Color: "#112233",
            Medium: new WorldLatticeMedium()
        );
        var document = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
                World: [new WorldStateRow(Name: WorldCellName.Parse(candidate: "medium"), Kind: CellKind.Fixed, Lattice: trait)],
                Lattices: [Topology()]
            ),
        };
        var roundTripped = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: document));
        var row = Assert.Single(collection: roundTripped.State);

        Assert.Equal(expected: trait, actual: row.Lattice);
    }
}
