using System.Globalization;
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
/// (<see cref="WorldPopulation.SampleMediumSurfaces"/> → <c>WorldBody.SetMediumSurface</c> → the medium
/// hold's <c>Submerged</c> fact).
/// </summary>
public sealed class WorldMediumLawTests {
    // The medium law's own 240-tick answer, recorded in raw Q48.16 so the law keeps answering it whatever spelling
    // reaches it. Re-record only alongside a deliberate correction to the law itself.
    private const string MediumTrace240 = "0000000000000000000000000000028f00000000000000000000000000000000000000000000051e0000000000000000"
        + "000000000000000000000000000007ae000000000000000000000000000000000000000000000a3d0000000000000000"
        + "00000000000000000000000000000ccc000000000000000000000000000000000000000000000f5c0000000000000000"
        + "000000000000000000000000000011eb00000000000000000000000000000000000000000000147a0000000000000000"
        + "0000000000000000000000000000170a0000000000000000000000000000000000000000000019990000000000000000"
        + "00000000000000000000000000001c28000000000000000000000000000000000000000000001eb80000000000000000"
        + "000000000000000000000000000021470000000000000000000000000000000000000000000023d70000000000000000"
        + "000000000000000000000000000026660000000000000000000000000000000000000000000028f50000000000000000"
        + "00000000000000000000000000002b85000000000000000000000000000000000000000000002e140000000000000000"
        + "000000000000000000000000000030a30000000000000000000000000000000000000000000033330000000000000000"
        + "000000000000000000000000000035c20000000000000000000000000000000000000000000038510000000000000000"
        + "00000000000000000000000000003ae1000000000000000000000000000000000000000000003d700000000000000000"
        + "00000000000000000000000000003fff00000000000000000000000000000000000000000000428f0000000000000000"
        + "0000000000000000000000000000451e0000000000000000000000000000000000000000000047ae0000000000000000"
        + "00000000000000000000000000004a3d000000000000000000000000000000000000000000004ccc0000000000000000"
        + "00000000000000000000000000004f5c0000000000000000000000000000000000000000000051eb0000000000000000"
        + "0000000000000000000000000000547a00000000000000000000000000000000000000000000570a0000000000000000"
        + "00000000000000000000000000005999000000000000000000000000000000000000000000005c280000000000000000"
        + "00000000000000000000000000005eb80000000000000000000000000000000000000000000061470000000000000000"
        + "000000000000000000000000000063d60000000000000000000000000000000000000000000066660000000000000000"
        + "000000000000000000000000000068f5000000000000000000000000000000000000000000006b850000000000000000"
        + "00000000000000000000000000006e140000000000000000000000000000000000000000000070a30000000000000000"
        + "000000000000000000000000000073330000000000000000000000000000000000000000000075c20000000000000000"
        + "00000000000000000000000000007851000000000000000000000000000000000000000000007ae10000000000000000"
        + "00000000000000000000000000007d70000000000000000000000000000000000000000000007fff0000000000000000"
        + "0000000000000000000000000000828f00000000000000000000000000000000000000000000851e0000000000000000"
        + "000000000000000000000000000087ad000000000000000000000000000000000000000000008a3d0000000000000000"
        + "00000000000000000000000000008ccc000000000000000000000000000000000000000000008f5c0000000000000000"
        + "000000000000000000000000000091eb00000000000000000000000000000000000000000000947a0000000000000000"
        + "0000000000000000000000000000970a0000000000000000000000000000000000000000000099990000000000000000"
        + "00000000000000000000000000009c28000000000000000000000000000000000000000000009eb80000000000000000"
        + "0000000000000000000000000000a14700000000000000000000000000000000000000000000a3d60000000000000000"
        + "0000000000000000000000000000a66600000000000000000000000000000000000000000000a8f50000000000000000"
        + "0000000000000000000000000000ab8500000000000000000000000000000000000000000000ae140000000000000000"
        + "0000000000000000000000000000b0a300000000000000000000000000000000000000000000b3330000000000000000"
        + "0000000000000000000000000000b5c200000000000000000000000000000000000000000000b8510000000000000000"
        + "0000000000000000000000000000bae100000000000000000000000000000000000000000000bd700000000000000000"
        + "0000000000000000000000000000bfff00000000000000000000000000000000000000000000c28f0000000000000000"
        + "0000000000000000000000000000c51e00000000000000000000000000000000000000000000c7ad0000000000000000"
        + "0000000000000000000000000000ca3d00000000000000000000000000000000000000000000cccc0000000000000000"
        + "0000000000000000000000000000cf5c00000000000000000000000000000000000000000000d1eb0000000000000000"
        + "0000000000000000000000000000d47a00000000000000000000000000000000000000000000d70a0000000000000000"
        + "0000000000000000000000000000d99900000000000000000000000000000000000000000000dc280000000000000000"
        + "0000000000000000000000000000deb800000000000000000000000000000000000000000000e1470000000000000000"
        + "0000000000000000000000000000e3d600000000000000000000000000000000000000000000e6660000000000000000"
        + "0000000000000000000000000000e8f500000000000000000000000000000000000000000000eb840000000000000000"
        + "0000000000000000000000000000ee1400000000000000000000000000000000000000000000f0a30000000000000000"
        + "0000000000000000000000000000f33300000000000000000000000000000000000000000000f5c20000000000000000"
        + "0000000000000000000000000000f85100000000000000000000000000000000000000000000fae10000000000000000"
        + "0000000000000000000000000000fd7000000000000000000000000000000000000000000000ffff0000000000000000"
        + "0000000000000000000000000001028f00000000000000000000000000000000000000000001051e0000000000000000"
        + "000000000000000000000000000107ad000000000000000000000000000000000000000000010a3d0000000000000000"
        + "00000000000000000000000000010ccc000000000000000000000000000000000000000000010f5b0000000000000000"
        + "000000000000000000000000000111eb00000000000000000000000000000000000000000001147a0000000000000000"
        + "0000000000000000000000000001170a0000000000000000000000000000000000000000000119990000000000000000"
        + "00000000000000000000000000011c28000000000000000000000000000000000000000000011eb80000000000000000"
        + "000000000000000000000000000121470000000000000000000000000000000000000000000123d60000000000000000"
        + "000000000000000000000000000126660000000000000000000000000000000000000000000128f50000000000000000"
        + "00000000000000000000000000012b84000000000000000000000000000000000000000000012e140000000000000000"
        + "000000000000000000000000000130a30000000000000000000000000000000000000000000133330000000000000000"
        + "000000000000000000000000000135c20000000000000000000000000000000000000000000138510000000000000000"
        + "00000000000000000000000000013ae1000000000000000000000000000000000000000000013d700000000000000000"
        + "00000000000000000000000000013fff00000000000000000000000000000000000000000001428f0000000000000000"
        + "0000000000000000000000000001451e0000000000000000000000000000000000000000000147ad0000000000000000"
        + "00000000000000000000000000014a3d000000000000000000000000000000000000000000014ccc0000000000000000"
        + "00000000000000000000000000014f5b0000000000000000000000000000000000000000000151eb0000000000000000"
        + "0000000000000000000000000001547a00000000000000000000000000000000000000000001570a0000000000000000"
        + "00000000000000000000000000015999000000000000000000000000000000000000000000015c280000000000000000"
        + "00000000000000000000000000015eb80000000000000000000000000000000000000000000161470000000000000000"
        + "000000000000000000000000000163d60000000000000000000000000000000000000000000166660000000000000000"
        + "000000000000000000000000000168f5000000000000000000000000000000000000000000016b840000000000000000"
        + "00000000000000000000000000016e140000000000000000000000000000000000000000000170a30000000000000000"
        + "000000000000000000000000000173320000000000000000000000000000000000000000000175c20000000000000000"
        + "00000000000000000000000000017851000000000000000000000000000000000000000000017ae10000000000000000"
        + "00000000000000000000000000017d70000000000000000000000000000000000000000000017fff0000000000000000"
        + "0000000000000000000000000001828f00000000000000000000000000000000000000000001851e0000000000000000"
        + "000000000000000000000000000187ad000000000000000000000000000000000000000000018a3d0000000000000000"
        + "00000000000000000000000000018ccc000000000000000000000000000000000000000000018f5b0000000000000000"
        + "000000000000000000000000000191eb00000000000000000000000000000000000000000001947a0000000000000000"
        + "000000000000000000000000000197090000000000000000000000000000000000000000000199990000000000000000"
        + "00000000000000000000000000019c28000000000000000000000000000000000000000000019eb80000000000000000"
        + "0000000000000000000000000001a14700000000000000000000000000000000000000000001a3d60000000000000000"
        + "0000000000000000000000000001a66600000000000000000000000000000000000000000001a8f50000000000000000"
        + "0000000000000000000000000001ab8400000000000000000000000000000000000000000001ae140000000000000000"
        + "0000000000000000000000000001b0a300000000000000000000000000000000000000000001b3320000000000000000"
        + "0000000000000000000000000001b5c200000000000000000000000000000000000000000001b8510000000000000000"
        + "0000000000000000000000000001bae000000000000000000000000000000000000000000001bd700000000000000000"
        + "0000000000000000000000000001bfff00000000000000000000000000000000000000000001c28f0000000000000000"
        + "0000000000000000000000000001c51e00000000000000000000000000000000000000000001c7ad0000000000000000"
        + "0000000000000000000000000001ca3d00000000000000000000000000000000000000000001cccc0000000000000000"
        + "0000000000000000000000000001cf5b00000000000000000000000000000000000000000001d1eb0000000000000000"
        + "0000000000000000000000000001d47a00000000000000000000000000000000000000000001d7090000000000000000"
        + "0000000000000000000000000001d99900000000000000000000000000000000000000000001dc280000000000000000"
        + "0000000000000000000000000001deb800000000000000000000000000000000000000000001e1470000000000000000"
        + "0000000000000000000000000001e3d600000000000000000000000000000000000000000001e6660000000000000000"
        + "0000000000000000000000000001e8f500000000000000000000000000000000000000000001eb840000000000000000"
        + "0000000000000000000000000001ee1400000000000000000000000000000000000000000001f0a30000000000000000"
        + "0000000000000000000000000001f33200000000000000000000000000000000000000000001f5c20000000000000000"
        + "0000000000000000000000000001f85100000000000000000000000000000000000000000001fae00000000000000000"
        + "0000000000000000000000000001fd7000000000000000000000000000000000000000000001ffff0000000000000000"
        + "0000000000000000000000000002028f00000000000000000000000000000000000000000002051e0000000000000000"
        + "000000000000000000000000000207ad000000000000000000000000000000000000000000020a3d0000000000000000"
        + "00000000000000000000000000020ccc000000000000000000000000000000000000000000020f5b0000000000000000"
        + "000000000000000000000000000211eb00000000000000000000000000000000000000000002147a0000000000000000"
        + "000000000000000000000000000217090000000000000000000000000000000000000000000219990000000000000000"
        + "00000000000000000000000000021c28000000000000000000000000000000000000000000021eb70000000000000000"
        + "000000000000000000000000000221470000000000000000000000000000000000000000000223d60000000000000000"
        + "000000000000000000000000000226660000000000000000000000000000000000000000000228f50000000000000000"
        + "00000000000000000000000000022b84000000000000000000000000000000000000000000022e140000000000000000"
        + "000000000000000000000000000230a30000000000000000000000000000000000000000000233320000000000000000"
        + "000000000000000000000000000235c20000000000000000000000000000000000000000000238510000000000000000"
        + "00000000000000000000000000023ae0000000000000000000000000000000000000000000023d700000000000000000"
        + "00000000000000000000000000023fff00000000000000000000000000000000000000000002428e0000000000000000"
        + "0000000000000000000000000002451e0000000000000000000000000000000000000000000247ad0000000000000000"
        + "00000000000000000000000000024a3d000000000000000000000000000000000000000000024ccc0000000000000000"
        + "00000000000000000000000000024f5b0000000000000000000000000000000000000000000251eb0000000000000000"
        + "0000000000000000000000000002547a0000000000000000000000000000000000000000000257090000000000000000"
        + "00000000000000000000000000025999000000000000000000000000000000000000000000025c280000000000000000"
        + "00000000000000000000000000025eb70000000000000000000000000000000000000000000261470000000000000000"
        + "000000000000000000000000000263d60000000000000000000000000000000000000000000266660000000000000000";

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

    // The medium spelled as a hold row on an ordinary grounded kit — the one authoring spelling of the law, and the
    // document MediumTrace240 was recorded against.
    private static WorldDefinition BuildMediumHoldDocument(WorldStateLatticeTopology topology, float buoyancy = 0.5f) {
        var channels = new WorldChannel[] {
            new(Name: "forward", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveAdvance),
            new(Name: "strafe", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveStrafe),
            new(Name: "turn", Shape: ChannelShape.Bipolar, Role: ChannelRole.Turn),
            new(Name: "up", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveUp),
        };
        var hold = new BodyMotionProgram(
            Name: "medium",
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
        var kit = new WorldKit(
            Name: "diver-test",
            BodyMotionProgram: "medium",
            Motion: new WorldMotion(
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
            ProducersRaw: new Dictionary<string, BodyProgramParameters> {
                ["wander"] = Fixtures.TravelerWanderParameters,
            },
            Collider: null
        );

        return Fixtures.BuildDocument() with {
            ChannelsRaw = channels,
            BodyMotionProgramsRaw = [hold, wander],
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
    // The trace's raw Q48.16 components, X then Y then Z per tick, each 16 lower-case hex digits.
    private static string Encode(FixedVector3[] trace) {
        var builder = new StringBuilder(capacity: (trace.Length * 48));

        foreach (var sample in trace) {
            _ = builder.Append(value: sample.X.Value.ToString(format: "x16", provider: CultureInfo.InvariantCulture));
            _ = builder.Append(value: sample.Y.Value.ToString(format: "x16", provider: CultureInfo.InvariantCulture));
            _ = builder.Append(value: sample.Z.Value.ToString(format: "x16", provider: CultureInfo.InvariantCulture));
        }

        return builder.ToString();
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
    public void TheMediumHoldRowReproducesTheRecordedTrace_WhereChangingOneMediumFacetDiverges() {
        var topology = Topology(depth: 2, width: 2);

        // Not "similar": the recorded control is the law's own answer tick by tick, in raw fixed point.
        Assert.Equal(expected: MediumTrace240, actual: Encode(trace: MediumTrace(definition: BuildMediumHoldDocument(topology: topology))));
        // The discriminating control — one facet of the medium's own law, moved.
        Assert.NotEqual(expected: MediumTrace240, actual: Encode(trace: MediumTrace(definition: BuildMediumHoldDocument(buoyancy: -0.5f, topology: topology))));
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
        var motion = admitted.Kits[0].Motion!;

        WorldDefinition WithHolds(params WorldHold[] holds) => (admitted with {
            KitRowsRaw = [(admitted.Kits[0] with { Motion = (motion with { Holds = holds }) })],
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
    public void ABodyStandingInAMediumCellGetsSubmergedAfterAStep_AndABodyOnDryCellsDoesNot() {
        // A 2x2 lattice at the origin covers seat-1's spawn (0,0,0) but not seat-2's (2,0,0) — the "same lattice,
        // in vs out of coverage" contrast a single fixture proves both halves through, since every local seat runs
        // the SAME resolved seat kit (see the puck-world skill's own seat/kit remarks).
        using var fixture = Fixtures.FreshServer(definition: BuildMediumHoldDocument(topology: Topology(depth: 2, width: 2)));
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
