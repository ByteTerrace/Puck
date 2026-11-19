using System.Numerics;

using Puck.Assets.Documents;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>Laws for an effector's reach and influence that running cannot show: a target on the creation's own shape is
/// read on the pose of the same frame, so the tip stays on it while it moves, and a lost target is never read again, so
/// the fade toward the last goal drawn moves the hand by nothing whatever the former target does.</summary>
public sealed class CreationReachLawTests {
    private const string AimRow = "aim";
    private const float DeltaSeconds = (1f / 60f);

    // A two-bone arm, 0.5 then 0.4 long from a shoulder at (0, 1, 0), with its tip at the end; author X and Z are zero on
    // the chain so the frame flip leaves its numbers alone.
    private static IEnumerable<ShapeDocument> Arm() => [
        Part(id: 0, joint: new Vector3(x: 0f, y: 1f, z: 0f), name: "upper", parent: null, position: new Vector3(x: 0f, y: 0.75f, z: 0f)),
        Part(id: 1, joint: new Vector3(x: 0f, y: 0.5f, z: 0f), name: "lower", parent: "upper", position: new Vector3(x: 0f, y: 0.3f, z: 0f)),
        Part(id: 2, joint: null, name: "tip", parent: "lower", position: new Vector3(x: 0f, y: 0.1f, z: 0f)),
    ];
    private static ShapeDocument Part(int id, string name, string? parent, Vector3 position, Vector3? joint, IReadOnlyList<ShapeSwingDocument>? swings = null) => new(
        Id: id,
        Name: name,
        Type: SdfSolidPrimitive.Capsule,
        Position: position,
        Rotation: Quaternion.Identity,
        Scale: new Vector3(x: 0.05f, y: 0.2f, z: 0.05f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Joint: ((joint is { } authored) ? new DocumentVector3(value: authored) : null),
        Parent: parent,
        Swings: swings
    );
    private static WorldDefinition World(CreationDocument document, long aim) {
        var basis = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var creation = CreationFixtures.Prototype(document: document, id: "rig");
        var section = (basis.StateRaw ?? new WorldStateSection());

        return basis with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook(Name: "rig", Source: new WorldLookSource.Creation(PrototypeId: creation.Id), Scale: 1f, Motion: WorldLookMotion.Default)],
            StateRaw = (section with { World = [.. (section.World ?? []), StateFixtures.IntSlot(min: -1, name: AimRow, value: aim)] }),
        };
    }

    // One body wearing the creation and, beside it, a second body it may reach for, packed frame by frame through the real
    // pack path with the aim cell delivered as state.
    private sealed class Stage {
        private readonly CreationDocument m_document;
        private readonly WorldClient m_client;
        private readonly WorldCreationInstances m_pool = new();
        private readonly DynamicTransform[] m_transforms;

        public Stage(CreationDocument document) {
            m_document = document;
            var definition = World(aim: -1L, document: document);
            var creation = definition.Creations[0];

            m_client = ClientFixtures.Client(definition: definition);
            m_client.DeliverDefinition(definition: definition, version: default, stream: default);
            m_pool.Reconcile(placementFrames: WorldPlacementFrameCompilation.Compile(placements: [], worldSeed: 0UL),
                bodyStamps: [new WorldCreationInstances.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Look: WorldLook.Implicit)],
                creations: [creation],
                dynamics: [],
                placements: []
            );
            m_transforms = new DynamicTransform[m_pool.DynamicSlotCount];
        }

        public WorldCreationInstances Pool => m_pool;

        public WorldRigEffector Frame(ulong tick, long aim, Vector3 other) {
            m_client.DeliverState(definition: World(aim: aim, document: m_document), version: default, stamp: new WorldStateStamp(EngineTick: tick, Everything: true, MovedRows: default, Tick: tick), stream: default);
            m_client.DeliverSnapshot(snapshot: new WorldSnapshot(
                Authority: "a",
                Entries: new[] {
                    new EntitySnapshot(Active: true, BodyColor: Vector3.One, CatalogRig: 0, Continuity: EntityContinuity.Continuous, Generation: 1, Index: 0, Kit: 0, Look: 0, Orientation: Quaternion.Identity, Position: Vector3.Zero),
                    new EntitySnapshot(Active: true, BodyColor: Vector3.One, CatalogRig: 0, Continuity: EntityContinuity.Continuous, Generation: 1, Index: 1, Kit: 0, Look: 0, Orientation: Quaternion.Identity, Position: other),
                },
                Revision: 0,
                StepTicks: 1UL,
                Tick: tick
            ), stream: default);
            m_client.UpdateRenderPoses(alpha: 1f);
            m_pool.Tick(deltaSeconds: DeltaSeconds);
            var moved = new SdfMovedTransforms();

            moved.Begin(everything: false, tableRows: m_transforms.Length);
            m_pool.PackTransforms(moved: moved, client: m_client, parkPosition: new Vector3(x: 0f, y: -1000f, z: 0f), slotBase: 0, transforms: m_transforms);
            Assert.True(condition: m_pool.TryBodyRig(bodyIndex: 0, state: out var rig));

            return rig!.Effectors[0];
        }
    }

    /// <summary>A fingertip aimed at a point riding the other arm, which swings on <c>time</c>, holds its error under a
    /// tenth of a millimetre on the final pose for 200 ticks: the target is read on the pose the frame draws, after every
    /// driver, never a frame late.</summary>
    [Fact]
    public void AFingertipOnTheOtherSwingingArmHoldsItsErrorForTwoHundredTicks() {
        var document = new CreationDocument(
            Drivers: [new CreationDriverDocument(Cadence: 3f, Name: "sway", Signal: CreationDriverDocument.SignalTime, When: [CreationDriverDocument.WhenAlways])],
            Effectors: [new CreationEffectorDocument(
                BlendInSeconds: 0f,
                Chain: ["upper", "lower"],
                Name: "fingertip",
                Target: new CreationEffectorTargetDocument(Kind: CreationEffectorTargetDocument.KindShape, Offset: new DocumentVector3(value: new Vector3(x: 0.3f, y: 0.4f, z: 0.2f)), Shape: "rod"),
                Tip: "tip",
                When: [CreationDriverDocument.WhenAlways]
            )],
            Frames: null,
            Name: "reacher",
            Palette: null,
            Schema: CreationDocument.CurrentSchema,
            Shapes: [
                .. Arm(),
                Part(id: 3, joint: null, name: "rod", parent: null, position: new Vector3(x: 0.3f, y: 0.5f, z: 0.2f), swings: [new ShapeSwingDocument(
                    Amplitude: 0.3f,
                    Axis: Vector3.UnitX,
                    Driver: "sway",
                    Pivot: new Vector3(x: 0.3f, y: 0.6f, z: 0.2f)
                )]),
            ]
        );

        CreationFixtures.AssertAccepts(document: document);
        var stage = new Stage(document: document);
        var worst = 0f;
        var moved = 0f;
        Vector3? first = null;

        for (var tick = 0UL; (tick < 200UL); tick++) {
            var effector = stage.Frame(aim: -1L, other: new Vector3(x: 2f, y: 0f, z: 0f), tick: tick);

            Assert.True(condition: (effector.Error is not null), userMessage: $"tick {tick}: no target resolved");
            worst = MathF.Max(x: worst, y: effector.Error!.Value);
            first ??= effector.Target;
            moved = MathF.Max(x: moved, y: Vector3.Distance(value1: first!.Value, value2: effector.Target!.Value));
        }

        Assert.True(condition: (worst < 1e-4f), userMessage: $"the worst error over 200 ticks is {worst} m");
        Assert.True(condition: (moved > 0.05f), userMessage: $"the target moved only {moved} m, so the law tested a still target");
    }
    /// <summary>Clearing the cell that names a reached body releases the effector in that frame: it reads no target, its
    /// weight eases out, and the hand fades toward the last goal it drew in its own creation's frame, so moving the former
    /// target during the fade moves the hand by nothing, bit for bit, against a run where the target stays put.</summary>
    [Fact]
    public void ALostBodyTargetFadesAndMovingItMovesTheHandByNothing() {
        var document = new CreationDocument(
            Drivers: null,
            Effectors: [new CreationEffectorDocument(
                BlendOutSeconds: 0.5f,
                Chain: ["upper", "lower"],
                Name: "hand",
                Target: new CreationEffectorTargetDocument(Kind: CreationEffectorTargetDocument.KindBody, Reference: $"state.{AimRow}"),
                Tip: "tip",
                When: [CreationDriverDocument.WhenAlways]
            )],
            Frames: null,
            Name: "reacher",
            Palette: null,
            Schema: CreationDocument.CurrentSchema,
            Shapes: [.. Arm()]
        );

        CreationFixtures.AssertAccepts(document: document);
        var still = new Stage(document: document);
        var moving = new Stage(document: document);
        var target = new Vector3(x: 0.3f, y: 0.4f, z: 0.2f);

        for (var tick = 0UL; (tick < 120UL); tick++) {
            var held = still.Frame(aim: 1L, other: target, tick: tick);

            _ = moving.Frame(aim: 1L, other: target, tick: tick);
            if (tick == 119UL) {
                Assert.Equal(expected: target, actual: held.Target!.Value);
                Assert.Equal(expected: 1f, actual: held.Applied);
            }
        }

        var previous = 1f;

        for (var tick = 120UL; (tick < 150UL); tick++) {
            var away = (target + new Vector3(x: (0.01f * (tick - 119UL)), y: 0f, z: 0f));
            var a = still.Frame(aim: -1L, other: target, tick: tick);
            var b = moving.Frame(aim: -1L, other: away, tick: tick);

            Assert.Null(a.Target);
            Assert.Null(b.Target);
            Assert.True(condition: ((a.Applied < previous) && (a.Applied > 0f)), userMessage: $"tick {tick}: applied {a.Applied} after {previous}");
            previous = a.Applied;
            Assert.Equal(expected: a.Applied, actual: b.Applied);
            Assert.Equal(expected: still.Pool.BodyShapePose(bodyIndex: 0, shapeIndex: 2).Position, actual: moving.Pool.BodyShapePose(bodyIndex: 0, shapeIndex: 2).Position);
        }
    }
    /// <summary>A cell holding zero selects body zero, and one holding a number that names no active body selects
    /// none.</summary>
    [Fact]
    public void ACellOfZeroSelectsBodyZero() {
        var document = new CreationDocument(
            Drivers: null,
            Effectors: [new CreationEffectorDocument(
                Chain: ["upper", "lower"],
                Name: "hand",
                Target: new CreationEffectorTargetDocument(Kind: CreationEffectorTargetDocument.KindBody, Offset: new DocumentVector3(value: new Vector3(x: 0f, y: 0.4f, z: 0.5f)), Reference: $"state.{AimRow}"),
                Tip: "tip",
                When: [CreationDriverDocument.WhenAlways]
            )],
            Frames: null,
            Name: "reacher",
            Palette: null,
            Schema: CreationDocument.CurrentSchema,
            Shapes: [.. Arm()]
        );
        var stage = new Stage(document: document);

        Assert.Equal(expected: new Vector3(x: 0f, y: 0.4f, z: -0.5f), actual: stage.Frame(aim: 0L, other: Vector3.One, tick: 0UL).Target);
        Assert.Null(stage.Frame(aim: 7L, other: Vector3.One, tick: 1UL).Target);
    }
}
