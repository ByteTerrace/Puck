using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for a creation rig backed by the simulation's state: a driver's state signal reads the EASED sample
/// of a cell carrying a dynamics trait and resolves <c>$body</c> to the wearing body's index; a gate token may be a
/// state reference that holds on the cell's STORED truth; a look's <c>poses</c> select a timeline frame from a state
/// cell; <c>$fact:&lt;bodyRef&gt;:&lt;fact&gt;</c> reads a body's live fact inside a rule; and the validator refuses the
/// references none of those can resolve.</summary>
public sealed class CreationStateRigLawTests {
    private const string AirRow = "air";
    private const string EaseRow = "ease";

    private static CreationDocument Rig(IReadOnlyList<CreationDriverDocument> drivers, params ShapeDocument[] shapes) => new(
        Schema: CreationDocument.CurrentSchema,
        Name: "rig",
        Palette: null,
        Shapes: shapes,
        Frames: null,
        Drivers: drivers
    );
    private static ShapeDocument Limb(ShapeSwingDocument swing) => new(
        Id: 0,
        Name: "limb",
        Type: SdfSolidPrimitive.Capsule,
        Position: new Vector3(x: 0f, y: -1f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: new Vector3(x: 0.05f, y: 1f, z: 0.05f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Swings: [swing]
    );
    private static WorldDefinition World(CreationDocument creation, IReadOnlyList<WorldStateRow>? state = null) {
        var basis = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var section = (basis.StateRaw ?? new WorldStateSection());

        return basis with {
            CreationsRaw = [.. basis.Creations, new WorldPrototype(Id: new DocumentIdentifier(value: "rig"), Document: creation)],
            StateRaw = (section with { World = [.. (section.World ?? []), .. (state ?? [])] }),
            DynamicsRaw = [.. basis.Dynamics, new DynamicsRow(Name: EaseRow, Frequency: 1f, Damping: 1f, Response: 0f)],
        };
    }
    // A keyed Fixed row whose cell "0" stores 1 and eases toward it from 0 through the ease row, plus an ordinary
    // stored-1 cell "1" beside it.
    private static WorldStateRow EasedAirRow() => new(
        Name: CellName.Parse(candidate: AirRow),
        Kind: CellKind.Fixed,
        Cells: [
            new StateCell(Key: CellName.Parse(candidate: "0"), Value: FixedQ4816.One.Value, Dynamics: new StateDynamics(Row: EaseRow, Y0: 0L, V0: 0L)),
            new StateCell(Key: CellName.Parse(candidate: "1"), Value: FixedQ4816.One.Value),
        ]
    );
    private static string Refusal(WorldDefinition definition) => (WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason) ? string.Empty : reason);
    private static float Phase(WorldDefinition world, CreationDocument creation, int bodyIndex, ulong tick) {
        var phases = new float[CreationDocument.MaxDrivers];
        var weights = new float[CreationDocument.MaxDrivers];
        var address = new WorldEntityAddress(Authority: "a", Index: bodyIndex, Generation: 1);
        var last = Vector3.Zero;
        var lastRotation = Quaternion.Identity;
        var seeded = false;
        var lastAddress = default(WorldEntityAddress);
        var speed = 0f;

        // The first advance only seeds the address; the second samples.
        for (var frame = 0; (frame < 2); frame++) {
            WorldGaitDrivers.Advance(drivers: creation.Drivers, phases: phases, weights: weights, deltaSeconds: (1f / 60f), facts: Physics.Motion.BodyFacts.Grounded, position: Vector3.Zero, orientation: Quaternion.Identity, lastPosition: ref last, lastOrientation: ref lastRotation, seeded: ref seeded, lastAddress: ref lastAddress, easedSpeed: ref speed, address: address, definition: world, tick: tick);
        }

        return phases[0];
    }

    [Fact]
    public void ADriverStateSignalReadsTheEasedSampleThroughTheBodyKey() {
        var driver = new CreationDriverDocument(Name: "flight", Signal: $"state.{AirRow}.$body", Cadence: 1f, When: ["always"]);
        var creation = Rig(drivers: [driver], Limb(swing: new ShapeSwingDocument(Driver: "flight", Pivot: Vector3.Zero, Axis: Vector3.UnitZ, Amplitude: 1f, Wave: CreationWave.Linear)));
        var world = World(creation: creation, state: [EasedAirRow()]);

        Assert.Equal(expected: string.Empty, actual: Refusal(definition: world));

        // Body 0's cell eases: at the epoch it reads the follower's seed (0), not the stored 1; long after, the
        // follower has settled on the stored 1. Body 1's cell carries no trait and reads its stored 1 at once. Body 2
        // has no cell and reads nothing.
        Assert.Equal(expected: 0f, actual: Phase(world: world, creation: creation, bodyIndex: 0, tick: 0UL), precision: 3);
        Assert.Equal(expected: 1f, actual: Phase(world: world, creation: creation, bodyIndex: 0, tick: 3000UL), precision: 2);
        Assert.Equal(expected: 1f, actual: Phase(world: world, creation: creation, bodyIndex: 1, tick: 0UL), precision: 4);
        Assert.Equal(expected: 0f, actual: Phase(world: world, creation: creation, bodyIndex: 2, tick: 0UL), precision: 4);
    }

    [Fact]
    public void AStateGateTokenHoldsOnTheStoredTruthNotTheEasedSample() {
        var creation = Rig(drivers: [new CreationDriverDocument(Name: "stride", Signal: CreationDriverDocument.SignalPlanarTravel, Cadence: 1f, When: [$"state.{AirRow}.$body"])], Limb(swing: new ShapeSwingDocument(Driver: "stride", Pivot: Vector3.Zero, Axis: Vector3.UnitZ, Amplitude: 1f)));
        var world = World(creation: creation, state: [EasedAirRow()]);

        Assert.Equal(expected: string.Empty, actual: Refusal(definition: world));

        // Cell "0" stores 1 while its eased sample is still 0 at the epoch: the gate reads the truth and holds.
        Assert.True(condition: WorldGaitDrivers.GateHolds(gate: creation.Drivers![0].When, facts: Physics.Motion.BodyFacts.Grounded, moving: false, definition: world, tick: 0UL, bodyIndex: 0));
        // No cell for body 2, and no definition at all: the token fails the conjunction.
        Assert.False(condition: WorldGaitDrivers.GateHolds(gate: creation.Drivers![0].When, facts: Physics.Motion.BodyFacts.Grounded, moving: false, definition: world, tick: 0UL, bodyIndex: 2));
        Assert.False(condition: WorldGaitDrivers.GateHolds(gate: creation.Drivers![0].When, facts: Physics.Motion.BodyFacts.Grounded, moving: false));
    }

    [Fact]
    public void TheWorldValidatorRefusesAGateTokenOrPoseNamingNoNumericRow_ControlDeclaredClean() {
        static CreationDocument Gated(string token) => Rig(
            drivers: [new CreationDriverDocument(Name: "stride", Signal: CreationDriverDocument.SignalPlanarTravel, Cadence: 1f, When: [token])],
            Limb(swing: new ShapeSwingDocument(Driver: "stride", Pivot: Vector3.Zero, Axis: Vector3.UnitZ, Amplitude: 1f))
        );

        Assert.Contains(expectedSubstring: "drivers[0].when[0] 'state.missing.0' names no declared state row", actualString: Refusal(definition: World(creation: Gated(token: "state.missing.0"), state: [EasedAirRow()])));
        Assert.Equal(expected: string.Empty, actual: Refusal(definition: World(creation: Gated(token: $"state.{AirRow}.$body"), state: [EasedAirRow()])));

        var world = World(creation: Gated(token: "always"), state: [EasedAirRow()]);
        var look = new WorldLook(Name: "rig", Source: new WorldLookSource.Creation(PrototypeId: "rig"), Scale: 1f, Motion: WorldLookMotion.Default);

        static WorldDefinition WithPoses(WorldDefinition world, WorldLook look, IReadOnlyDictionary<string, string> poses) => (world with {
            LookRowsRaw = [look with { Motion = (look.Motion with { Poses = poses }) }],
        });

        Assert.Contains(expectedSubstring: "poses['wink'] names no frame", actualString: Refusal(definition: WithPoses(world: world, look: look, poses: new Dictionary<string, string> { ["wink"] = $"state.{AirRow}.$body" })));
        Assert.Contains(expectedSubstring: "must be a state.<row>[.<key>] reference", actualString: Refusal(definition: WithPoses(world: world, look: look, poses: new Dictionary<string, string> { ["wink"] = "always" })));
    }

    [Fact]
    public void ALookPoseSelectsItsFrameWhileTheStateCellIsNonzero() {
        var frame = new FrameDocument(Name: "wink", Transforms: [new FrameTransformDocument(Id: 0, Position: new Vector3(x: 0f, y: -1f, z: 0f), Rotation: Quaternion.Identity, Scale: new Vector3(x: 0.05f, y: 0.001f, z: 0.05f))]);
        var creation = (Rig(drivers: [], Limb(swing: new ShapeSwingDocument(Driver: "stride", Pivot: Vector3.Zero, Axis: Vector3.UnitZ, Amplitude: 1f))) with {
            Drivers = [new CreationDriverDocument(Name: "stride", Signal: CreationDriverDocument.SignalPlanarTravel, Cadence: 1f, When: ["always"])],
            Frames = [frame],
        });
        var look = new WorldLook(Name: "rig", Source: new WorldLookSource.Creation(PrototypeId: "rig"), Scale: 1f, Motion: (WorldLookMotion.Default with {
            Poses = new Dictionary<string, string> { ["wink"] = $"state.{AirRow}.$body" },
        }));
        var world = (World(creation: creation, state: [EasedAirRow()]) with { LookRowsRaw = [look] });

        Assert.Equal(expected: string.Empty, actual: Refusal(definition: world));
        // Body 0's cell stores 1 while its eased sample is still 0: the pose reads the truth and holds at once.
        Assert.Equal(expected: 1, actual: WorldStampPool.SelectPoseFrame(definition: world, poses: look.Motion.Poses!, frames: creation.Frames!, bodyIndex: 0, tick: 0UL));
        // Body 1's cell stores 1 (the pose holds, frame 1); body 2 has no cell (the live pose, frame 0).
        Assert.Equal(expected: 1, actual: WorldStampPool.SelectPoseFrame(definition: world, poses: look.Motion.Poses!, frames: creation.Frames!, bodyIndex: 1, tick: 0UL));
        Assert.Equal(expected: 0, actual: WorldStampPool.SelectPoseFrame(definition: world, poses: look.Motion.Poses!, frames: creation.Frames!, bodyIndex: 2, tick: 0UL));
    }
}
