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
/// hold's <c>InMedium</c> fact).
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

    private static WorldStateLatticeTopology.Field Topology(int width = 4, int depth = 4) => new(
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
    private static WorldDefinition BuildMediumHoldDocument(WorldStateLatticeTopology topology, float idleDrift = 0.5f, float settleRate = 6f) {
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
                BodyMotionOp.ShapeVelocity,
                BodyMotionOp.ApplyHold,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        );
        var roam = new BodyMotionProgram(Name: "roam", Version: "puck.body-motion.v1", Kind: BodyProgramKind.Producer, Operations: [BodyMotionOp.ProduceSteeringIntent]);
        var kit = new WorldKit(
            Name: "diver-test",
            BodyMotionProgram: "medium",
            Motion: new WorldMotion(
                Speed: new WorldSpeed(Value: 3.2f),
                Turn: new WorldTurn(Rate: 2.2f),
                // One unconditional row using the exact authored spelling: absent engage/release rates snap both
                // the planar velocity and (through ApplyMedium's own vertical lane) the medium's commanded vertical
                // target instead of approximating "instant" with a large finite rate.
                Shaping: [
                    new WorldShaping(Along: new WorldShapingAlong()),
                ],
                Holds: [
                    new WorldHold(
                        Bond: BodyHoldBond.Medium,
                        Envelope: new WorldHoldEnvelope(RiseSpeed: 2.4f, SinkSpeed: 3f),
                        Hold: BodyHoldKind.None,
                        Medium: new WorldHoldMedium(
                            EquilibriumOffset: 1f,
                            IdleDrift: idleDrift,
                            SettleRate: settleRate
                        ),
                        Name: "water",
                        Thrust: 0.75f
                    ),
                    // The list's unconditional row — a Medium row is conditional on the lattice column, so
                    // ResolveHold needs a Free fallback behind it for the case the body ever leaves the medium.
                    // Trailing it keeps the Medium row preferred for every position this suite's traces drive
                    // through.
                    new WorldHold(
                        Bond: BodyHoldBond.Free,
                        Envelope: new WorldHoldEnvelope(SinkSpeed: 1f),
                        Gravity: new WorldHoldGravity(Fall: 1f, Rise: 1f),
                        Hold: BodyHoldKind.Gravity,
                        Name: "air"
                    ),
                ]
            ),
            ProducersRaw: new Dictionary<string, BodyProgramParameters> {
                ["roam"] = Fixtures.TravelerRoamParameters,
            },
            Collider: null
        );

        return Fixtures.BuildDocument() with {
            ChannelsRaw = channels,
            BodyMotionProgramsRaw = [hold, roam],
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
        Assert.NotEqual(expected: MediumTrace240, actual: Encode(trace: MediumTrace(definition: BuildMediumHoldDocument(idleDrift: -0.5f, topology: topology))));
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
            Envelope: new WorldHoldEnvelope(SinkSpeed: 1f),
            Gravity: new WorldHoldGravity(Fall: 1f, Rise: 1f),
            Hold: BodyHoldKind.Gravity,
            Medium: new WorldHoldMedium(
                EquilibriumOffset: 1f,
                IdleDrift: 0.5f,
                SettleRate: 6f
            ),
            Name: "floor",
            Reach: 1f,
            Thrust: 0.75f
        ));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: misplaced, reason: out var misplacedReason));
        Assert.Contains(actualString: misplacedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "medium is refused");
    }
    [Fact]
    public void ABodyStandingInAMediumCellReadsInMediumAfterAStep_AndABodyOnDryCellsDoesNot() {
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
        // seat reads in-medium from the very first step with no drive needed.
        Assert.True(condition: wetBody.InMedium);
        Assert.False(condition: dryBody.InMedium);
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
    /// <summary>A medium displaces a body by its own law, so a Medium row applies no arc: authoring a Gravity kind
    /// on one refuses by name, where the same row holding None is admitted.</summary>
    [Fact]
    public void AMediumRowAuthoringAGravityKind_RefusesByName_WhereTheSameRowHoldingNoneIsAdmitted() {
        var topology = Topology();
        var admitted = BuildMediumHoldDocument(topology: topology);
        var motion = admitted.Kits[0].Motion!;
        var water = motion.Holds![0];

        Assert.Equal(expected: BodyHoldKind.None, actual: water.Hold);
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);

        var arced = (admitted with {
            KitRowsRaw = [(admitted.Kits[0] with {
                Motion = (motion with {
                    Holds = [
                        (water with {
                            Gravity = new WorldHoldGravity(Fall: 1f, Rise: 1f),
                            Hold = BodyHoldKind.Gravity,
                        }),
                        .. motion.Holds.Skip(count: 1),
                    ],
                }),
            })],
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: arced, reason: out var arcedReason));
        Assert.Contains(actualString: arcedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "is refused on a Medium bond");
    }
    /// <summary>The medium law measures displacement along the body's own resolved gravity-up (a ray/plane
    /// intersection against the field's own point-and-normal), not a raw world-Y difference — proved by tilting the
    /// world's own gravity area 30 degrees off vertical and letting a body with no commanded input settle under the
    /// medium's own law alone. The control is the OLD Y-scalar law's prediction (surface minus equilibriumOffset,
    /// ignoring the tilt entirely): the settled height must land near the tilted-up prediction and clearly away from
    /// the untilted one.</summary>
    [Fact]
    public void AMediumRowUnderATiltedGravityArea_SettlesAtEquilibriumOffsetAlongItsOwnUp_WhereTheOldYOnlyLawWouldMiss() {
        const double TiltDegrees = 30.0;
        var tiltRadians = (TiltDegrees * (Math.PI / 180.0));
        var sinTilt = (float)Math.Sin(tiltRadians);
        var cosTilt = (float)Math.Cos(tiltRadians);
        // Wide and centered: reaching equilibrium mixes a horizontal excursion into the settle (the vertical
        // channel integrates along the body's own tilted up, not literally world Y — see IntegratePlanarAndVerticalVelocity),
        // so the spawn needs room on every side, not just headroom above the pool.
        var topology = new WorldStateLatticeTopology.Field(
            Name: "world",
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Width: 12,
            Depth: 12,
            Layers: 1
        );
        // A uniform field tilted 30 degrees off vertical toward +X: acceleration points "down" along
        // (sin, -cos, 0), so gravity-up resolves to (-sin, cos, 0) rather than world +Y.
        var document = BuildMediumHoldDocument(topology: topology) with {
            GravityRaw = new WorldGravity(
                Attractors: [],
                GravitationalConstant: 0f,
                SofteningLength: 1f,
                Solver: WorldGravitySolver.Pairwise,
                Uniform: new DocumentVector3(x: (10f * sinTilt), y: (-10f * cosTilt), z: 0f)
            ),
            SpawnPointsRaw = [
                new WorldSpawnPoint(Id: "seat-1", Position: new DocumentVector3(x: 6f, y: 0f, z: 6f)),
                new WorldSpawnPoint(Id: "seat-2", Position: new DocumentVector3(x: 2f, y: 0f, z: 0f)),
                new WorldSpawnPoint(Id: "seat-3", Position: new DocumentVector3(x: 0f, y: 0f, z: 2f)),
                new WorldSpawnPoint(Id: "seat-4", Position: new DocumentVector3(x: 2f, y: 0f, z: 2f)),
            ],
        };

        using var fixture = Fixtures.FreshServer(definition: document);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        // No commanded input at all — the medium's own settle law is the only thing moving the body, so the
        // settled height is a pure read of the law rather than of any thrust interaction. 5000 ticks is well past
        // convergence (the body is bit-stable by ~4000 in the recorded trace this law was checked against).
        for (var tick = 0; (tick < 5000); tick++) {
            body.SubmitIntent(intent: default);
            fixture.Step();
        }

        var settledY = (double)body.FixedPosition.Y;
        // The medium surface sits at world Y 5 (lattice origin 0 + value 1 * heightScale 5); the row's own
        // equilibriumOffset is 1 — see BuildMediumHoldDocument.
        const double SurfaceY = 5.0;
        const double EquilibriumOffset = 1.0;
        var expectedTiltedY = (SurfaceY - (EquilibriumOffset * cosTilt));
        var untiltedLawPrediction = (SurfaceY - EquilibriumOffset);

        Assert.True(
            condition: (Math.Abs(value: (settledY - expectedTiltedY)) < 0.01),
            userMessage: $"expected the body to settle at y={expectedTiltedY:0.####} (equilibriumOffset projected along the tilted up), but it settled at y={settledY:0.####}"
        );
        // The control: the untilted Y-scalar law's own equilibrium — surface minus equilibriumOffset, ignoring the
        // tilt entirely — is a materially different height, proving the settled position actually reads the tilt
        // rather than landing near it by coincidence.
        Assert.True(
            condition: (Math.Abs(value: (settledY - untiltedLawPrediction)) > 0.1),
            userMessage: $"the settled y={settledY:0.####} must diverge from the untilted Y-scalar law's prediction y={untiltedLawPrediction:0.####}, or the tilt is not actually being read"
        );
    }
    // A variant of BuildMediumHoldDocument with seat-1 dropped to an authored spawn Y (the other three seats keep
    // their own BuildSpawnPoints() corners) and the medium row's own settle rate and envelope overridden, so the
    // envelope/settle-rate law tests below can place the body on either side of the equilibrium band without
    // touching BuildMediumHoldDocument's own recorded-trace shape.
    private static WorldDefinition BuildMediumEnvelopeDocument(WorldStateLatticeTopology topology, float spawnY, float idleDrift, float settleRate, float riseSpeed, float sinkSpeed) {
        var document = BuildMediumHoldDocument(topology: topology, idleDrift: idleDrift, settleRate: settleRate);
        var motion = document.Kits[0].Motion!;
        var water = motion.Holds![0] with {
            Envelope = new WorldHoldEnvelope(RiseSpeed: riseSpeed, SinkSpeed: sinkSpeed),
        };

        return document with {
            KitRowsRaw = [(document.Kits[0] with {
                Motion = (motion with {
                    Holds = [water, .. motion.Holds.Skip(count: 1)],
                }),
            })],
            SpawnPointsRaw = [
                new WorldSpawnPoint(Id: "seat-1", Position: new DocumentVector3(x: 0f, y: spawnY, z: 0f)),
                new WorldSpawnPoint(Id: "seat-2", Position: new DocumentVector3(x: 2f, y: 0f, z: 0f)),
                new WorldSpawnPoint(Id: "seat-3", Position: new DocumentVector3(x: 0f, y: 0f, z: 2f)),
                new WorldSpawnPoint(Id: "seat-4", Position: new DocumentVector3(x: 2f, y: 0f, z: 2f)),
            ],
        };
    }

    [Fact]
    public void MediumPositiveFixedFields_RefuseValuesThatQuantizeToZero() {
        var document = BuildMediumEnvelopeDocument(
            topology: Topology(),
            spawnY: 4.5f,
            idleDrift: 0.5f,
            settleRate: 6f,
            riseSpeed: 2.4f,
            sinkSpeed: 3f
        );

        foreach (var (mutate, token) in new (Func<WorldHold, WorldHold> Mutate, string Token)[] {
            (hold => hold with { Medium = hold.Medium! with { EquilibriumOffset = float.Epsilon } }, "medium.equilibriumOffset"),
            (hold => hold with { Medium = hold.Medium! with { SettleRate = float.Epsilon } }, "medium.settleRate"),
            (hold => hold with { Envelope = hold.Envelope! with { RiseSpeed = float.Epsilon } }, "envelope.riseSpeed"),
            (hold => hold with { Envelope = hold.Envelope! with { SinkSpeed = float.Epsilon } }, "envelope.sinkSpeed"),
        }) {
            var motion = document.Kits[0].Motion!;
            var kits = document.Kits.ToList();

            kits[0] = kits[0] with { Motion = motion with { Holds = [mutate(motion.Holds![0])] } };

            Assert.False(
                condition: WorldDefinitionValidator.TryValidateLocally(definition: document with { KitRowsRaw = kits }, reason: out var reason),
                userMessage: $"a medium field failing {token} was expected to refuse"
            );
            Assert.Contains(actualString: reason, expectedSubstring: token);
        }
    }
    // The vertical speed the FIRST tick's own position delta implies (u/s), for a document whose shaping row is
    // instant (BuildMediumHoldDocument's), so m_verticalVelocity snaps to ApplyMedium's own target with no ramp and
    // that tick's position delta is exactly target * (1 / SimulationRateHz).
    private const double SimulationRateHz = 240.0;
    private static double FirstTickImpliedVerticalSpeed(WorldDefinition definition) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var before = (double)body.FixedPosition.Y;

        body.SubmitIntent(intent: default);
        fixture.Step();

        var after = (double)body.FixedPosition.Y;

        return ((after - before) * SimulationRateHz);
    }
    /// <summary>The medium surface sits at world Y 5 (see BuildMediumHoldDocument); the row's own equilibriumOffset
    /// is 1, so the free-fall branch (<c>error &gt; equilibriumOffset</c>) governs a body deep enough that its own
    /// displacement exceeds 2, and clamps its authored idle drift to the hold's own RiseSpeed. The control is a
    /// document whose RiseSpeed is wide enough that the same idle drift never reaches it — the two must diverge, or
    /// the narrow envelope is not actually the thing bounding the clamped answer.</summary>
    [Fact]
    public void TheFreeFallBranchsIdleDriftIsClampedToTheHoldsRiseSpeed_WhereAWideEnvelopeLetsTheUnclampedDriftThrough() {
        var topology = Topology();
        const float IdleDrift = 5f;
        const float NarrowRiseSpeed = 1f;
        const float WideRiseSpeed = 100f;

        var clamped = FirstTickImpliedVerticalSpeed(definition: BuildMediumEnvelopeDocument(topology: topology, spawnY: 0f, idleDrift: IdleDrift, settleRate: 6f, riseSpeed: NarrowRiseSpeed, sinkSpeed: 3f));
        var control = FirstTickImpliedVerticalSpeed(definition: BuildMediumEnvelopeDocument(topology: topology, spawnY: 0f, idleDrift: IdleDrift, settleRate: 6f, riseSpeed: WideRiseSpeed, sinkSpeed: 3f));

        Assert.True(condition: (Math.Abs(value: (clamped - NarrowRiseSpeed)) < 0.05), userMessage: $"expected the narrow envelope to clamp the rise to {NarrowRiseSpeed}, read {clamped:0.####}");
        Assert.True(condition: (Math.Abs(value: (control - IdleDrift)) < 0.05), userMessage: $"expected the wide envelope to let the authored idle drift {IdleDrift} through unclamped, read {control:0.####}");
        Assert.True(condition: (control > (clamped + 1f)), userMessage: $"the wide-envelope control {control:0.####} must exceed the narrow-envelope clamp {clamped:0.####}, or the RiseSpeed bound is not actually being read");
    }
    /// <summary>The in-band branch (<c>error &lt;= equilibriumOffset</c>) turns the equilibrium error into a target
    /// through the medium's own SettleRate, clamped to the hold's own SinkSpeed for a body recovering a breach above
    /// the surface (a large negative error). The control is a document whose SinkSpeed is wide enough that the same
    /// error*SettleRate answer never reaches it — the two must diverge, or the narrow envelope is not actually the
    /// thing bounding the clamped answer.</summary>
    [Fact]
    public void TheInBandBranchsSettleDriftIsClampedToTheHoldsSinkSpeed_WhereAWideEnvelopeLetsTheUnclampedDriftThrough() {
        var topology = Topology();
        const float SpawnY = 4.5f; // displacement 0.5, error = 0.5 - 1 = -0.5.
        const float SettleRate = 6f; // raw drift = -0.5 * 6 = -3.
        const float NarrowSinkSpeed = 1f;
        const float WideSinkSpeed = 100f;

        var clamped = FirstTickImpliedVerticalSpeed(definition: BuildMediumEnvelopeDocument(topology: topology, spawnY: SpawnY, idleDrift: 0.5f, settleRate: SettleRate, riseSpeed: 2.4f, sinkSpeed: NarrowSinkSpeed));
        var control = FirstTickImpliedVerticalSpeed(definition: BuildMediumEnvelopeDocument(topology: topology, spawnY: SpawnY, idleDrift: 0.5f, settleRate: SettleRate, riseSpeed: 2.4f, sinkSpeed: WideSinkSpeed));

        Assert.True(condition: (Math.Abs(value: (clamped - -NarrowSinkSpeed)) < 0.05), userMessage: $"expected the narrow envelope to clamp the sink to {-NarrowSinkSpeed}, read {clamped:0.####}");
        Assert.True(condition: (Math.Abs(value: (control - -3.0)) < 0.05), userMessage: $"expected the wide envelope to let the raw error*SettleRate answer -3 through unclamped, read {control:0.####}");
        Assert.True(condition: (clamped > (control + 1f)), userMessage: $"the narrow-envelope clamp {clamped:0.####} must be less negative than the wide-envelope control {control:0.####}, or the SinkSpeed bound is not actually being read");
    }
    /// <summary>The in-band branch's own target is the equilibrium error scaled by the medium's own SettleRate, not
    /// an implicit unit gain: two documents differing only in SettleRate must answer proportionally different first-
    /// tick drifts. Both spawns and envelopes are wide enough that neither answer clamps, so the divergence reads the
    /// SettleRate scaling alone.</summary>
    [Fact]
    public void TheInBandBranchsDriftScalesWithTheMediumsOwnSettleRate_WhereADifferentRateAnswersDifferently() {
        var topology = Topology();
        const float SpawnY = 4.5f; // displacement 0.5, error = -0.5.

        var fast = FirstTickImpliedVerticalSpeed(definition: BuildMediumEnvelopeDocument(topology: topology, spawnY: SpawnY, idleDrift: 0.5f, settleRate: 6f, riseSpeed: 2.4f, sinkSpeed: 100f));
        var slow = FirstTickImpliedVerticalSpeed(definition: BuildMediumEnvelopeDocument(topology: topology, spawnY: SpawnY, idleDrift: 0.5f, settleRate: 2f, riseSpeed: 2.4f, sinkSpeed: 100f));

        Assert.True(condition: (Math.Abs(value: (fast - -3.0)) < 0.05), userMessage: $"expected error(-0.5) * SettleRate(6) = -3, read {fast:0.####}");
        Assert.True(condition: (Math.Abs(value: (slow - -1.0)) < 0.05), userMessage: $"expected error(-0.5) * SettleRate(2) = -1, read {slow:0.####}");
        Assert.True(condition: (Math.Abs(value: (fast - slow)) > 1f), userMessage: $"a SettleRate of 6 ({fast:0.####}) must answer differently from a SettleRate of 2 ({slow:0.####}), or the in-band branch is not actually reading it");
    }
    // Declares bodies.scaleRow over the fixture's own seat-1 cell (index "0"), matching BodyScaleLawTests' own
    // pattern, so seat-1's live Scale reads the cell's value while every other seat stays at the unauthored default.
    private static WorldDefinition WithScaleRow(WorldDefinition document, FixedQ4816 cellValue) {
        var scaleRow = new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "scale"),
            Kind: CellKind.Fixed,
            Min: FixedQ4816.FromDouble(value: 0.05).Value,
            Max: FixedQ4816.One.Value,
            Capacity: 8,
            Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: cellValue.Value)]
        );

        return document with {
            PopulationRaw = (document.Population with { ScaleRow = "scale" }),
            StateRaw = ((document.StateRaw ?? new WorldStateSection()) with {
                World = [.. (document.StateRaw?.World ?? []), scaleRow],
            }),
        };
    }
    /// <summary>The medium's whole vertical-channel envelope scales with the body's own live <c>Scale</c>, the same
    /// way <c>ApplyHoldGravity</c>'s acceleration and terminal both do: a scaled body's idle drift and its in-band
    /// settle target (<c>error * SettleRate</c>) answer proportionally, and a scale of exactly 1 reproduces the
    /// unscaled document's own first-tick answer bit for bit through the scaleRow path.</summary>
    [Fact]
    public void TheMediumsIdleDriftAndSettleTargetScaleWithTheBodysOwnScale_WhereScaleOneReproducesTheUnscaledAnswer() {
        var topology = Topology();
        var scale = FixedQ4816.FromDouble(value: 0.4);
        // Wide enough at both scale 1 and scale 0.4 (40) that neither answer clamps to the envelope — isolates each
        // branch's target scaling, not the envelope bounds the two clamp laws above already cover.
        var settle = BuildMediumEnvelopeDocument(topology: topology, spawnY: 4.5f, idleDrift: 0.5f, settleRate: 6f, riseSpeed: 100f, sinkSpeed: 100f);
        var idle = BuildMediumEnvelopeDocument(topology: topology, spawnY: 0f, idleDrift: 5f, settleRate: 6f, riseSpeed: 100f, sinkSpeed: 100f);

        AssertScales(document: settle, branch: "in-band settle target");
        AssertScales(document: idle, branch: "free-fall idle drift");

        void AssertScales(WorldDefinition document, string branch) {
            var unscaledSpeed = FirstTickImpliedVerticalSpeed(definition: document);
            var scaleOneSpeed = FirstTickImpliedVerticalSpeed(definition: WithScaleRow(document: document, cellValue: FixedQ4816.One));
            var scaledSpeed = FirstTickImpliedVerticalSpeed(definition: WithScaleRow(document: document, cellValue: scale));

            Assert.True(
                condition: (Math.Abs(value: (scaleOneSpeed - unscaledSpeed)) < 0.01),
                userMessage: $"a scaleRow cell of exactly 1 must reproduce the {branch}'s unscaled answer ({unscaledSpeed:0.####}), read {scaleOneSpeed:0.####}"
            );

            var expectedScaledSpeed = (unscaledSpeed * (double)scale);

            Assert.True(
                condition: (Math.Abs(value: (scaledSpeed - expectedScaledSpeed)) < 0.05),
                userMessage: $"expected the {branch} to scale by {(double)scale} to {expectedScaledSpeed:0.####}, read {scaledSpeed:0.####}"
            );
        }
    }
}
