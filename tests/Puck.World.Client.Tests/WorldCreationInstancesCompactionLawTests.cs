using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>Unused render capacity must not widen a live program's masks or move surviving transform addresses.</summary>
public sealed class WorldCreationInstancesCompactionLawTests {
    private const int SlotBase = 23;

    private static WorldPrototype Creation(params ShapeDocument[] shapes) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: "compact",
                Palette: [new(
                        "#AA7755",
                        null,
                        null,
                        null
                    )],
                Shapes: shapes,
                Frames: null
            ),
            source: "compact"
        );

        return new WorldPrototype(
            Id: "compact",
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    private static SdfProgram Emit(WorldCreationInstances pool) {
        var builder = new SdfProgramBuilder();

        pool.Emit(
            builder: builder,
            colors: WorldBakedColors.Of(definition: Fixtures.BuildGradientUpDocument(gradientUp: false)),
            probeWorstCase: false,
            maxPlacementScale: 1f,
            slotBase: SlotBase
        );
        return builder.Build();
    }
    private static void Reconcile(WorldCreationInstances pool, WorldPrototype creation, params int[] bodies) => pool.Reconcile(
        placements: [],
        creations: [creation],
        dynamics: [],
        bodyStamps: bodies.Select(selector: body => new WorldCreationInstances.BodyStamp(
            BodyIndex: body,
            Creation: creation,
            Scale: 1f,
            Look: WorldLook.Implicit
        )).ToArray()
    );
    private static ShapeDocument Shape(int id, int? group = null, SdfBlendOp blend = SdfBlendOp.Union) => new(
        Id: id,
        Name: $"part-{id}",
        Type: SdfSolidPrimitive.Box,
        Position: new Vector3(
            x: (id * 0.1f),
            y: 0f,
            z: 0f
        ),
        Rotation: Quaternion.Identity,
        Scale: new Vector3(value: 0.1f),
        Material: 0,
        Blend: blend,
        Smooth: 0f,
        Group: group
    );

    [Fact]
    public void AFrameMovingAUnionMemberIsIncludedInTheBound() {
        var pool = new WorldCreationInstances();
        var first = Shape(
            id: 0,
            group: 7
        );
        var second = Shape(
            id: 1,
            group: 7
        );
        var creation = Creation(
            first,
            second
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: creation.Document with {
                Frames = [new FrameDocument(
                    Name: "move",
                    Transforms: [new FrameTransformDocument(
                            Id: second.Id,
                            Position: new Vector3(
                                x: 2f,
                                y: 0f,
                                z: 0f
                            ),
                            Rotation: Quaternion.Identity
                        )]
                )],
            },
            source: "compact"
        );

        creation = new WorldPrototype(
            Id: creation.Id,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
        Reconcile(
            pool,
            creation,
            0
        );
        var group = Assert.Single(collection: Emit(pool: pool).Instances);

        Assert.Equal(
            SlotBase,
            group.Slot
        );
        Assert.True(condition: (group.Radius >= 2.1f));
    }
    [Fact]
    public void ARemoteCutterDoesNotWidenTheGroupsSolidBound() {
        var pool = new WorldCreationInstances();
        var solid = Shape(
            id: 0,
            group: 7
        ) with {
            Position = new Vector3(
            x: 0f,
            y: 8f,
            z: 0f
        ),
        };
        var cutter = Shape(
            blend: SdfBlendOp.Subtraction,
            group: 7,
            id: 1
        ) with {
            Position = new Vector3(
            x: 10f,
            y: 0f,
            z: 0f
        ),
            Scale = new Vector3(value: 2f),
        };

        Reconcile(
            pool,
            Creation(solid, cutter) with { Document = Creation(solid, cutter).Document with { Frames = [new FrameDocument("rest", [])] } },
            0
        );
        var group = Assert.Single(collection: Emit(pool: pool).Instances);

        Assert.Equal(
            SlotBase,
            group.Slot
        );
        Assert.InRange(
            group.Radius,
            0.1f,
            0.3f
        );
    }
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [Theory]
    public void AnInhabitedAnimatedPlacementRendersOnlyThroughItsBody(bool inhabited, int expectedInstances) {
        var initial = Creation(Shape(0));
        var creation = new WorldPrototype(
            initial.Id,
            initial.Document with {
                Frames = [new FrameDocument(
                    Name: "pose",
                    // The frame lifts the shape off its authored pose, so the creation animates.
                    Transforms: [new FrameTransformDocument(
                            Id: 0,
                            Position: Vector3.UnitY,
                            Rotation: Quaternion.Identity
                        )]
                )],
            }
        );
        var row = new WorldPlacement(
            Id: "station",
            PrototypeId: creation.Id,
            Position: Vector3.Zero,
            YawDegrees: 0f,
            Scale: 1f,
            Inhabit: (inhabited
            ? new WorldPlacementInhabit(
                    Kit: "display",
                    Look: null,
                    Source: Puck.World.Protocol.IntentSource.Idle
                )
            : null)
        );
        var pool = new WorldCreationInstances();
        var bodies = new[] { new WorldCreationInstances.BodyStamp(
            0,
            creation,
            1f,
            WorldLook.Implicit
        ) };

        pool.Reconcile(
            bodyStamps: bodies,
            creations: [creation],
            dynamics: [],
            placements: [row]
        );
        Assert.Equal(
            expectedInstances,
            Emit(pool: pool).Instances.Count
        );

        // Changing the row's ownership also retires an already-registered placement copy.
        pool.Reconcile(
            [row with { Inhabit = new WorldPlacementInhabit(
                    "display",
                    null,
                    Puck.World.Protocol.IntentSource.Idle
                ) }],
            [creation],
            [],
            bodies
        );
        Assert.Single(collection: Emit(pool: pool).Instances);
        pool.Reconcile(
            [row with { Inhabit = new WorldPlacementInhabit(
                    "display",
                    null,
                    Puck.World.Protocol.IntentSource.Idle
                ) }],
            [creation],
            [],
            []
        );
        Assert.Empty(collection: Emit(pool: pool).Instances);
    }
    [Fact]
    public void EmptyPoolEmitsNoGeometry() {
        var program = Emit(pool: new WorldCreationInstances());

        Assert.Empty(collection: program.Instances);
        Assert.DoesNotContain(
            collection: program.Instructions,
            filter: instruction => (instruction.Op == SdfOp.ShapeBlend)
        );
    }
    [Fact]
    public void GroupsAndNewShapesUseAuthorSlotsWithoutPlaceholderInstances() {
        var pool = new WorldCreationInstances();
        var shapes = new[] { Shape(
            id: 0,
            group: 7
        ), Shape(id: 1), Shape(
            blend: SdfBlendOp.Subtraction,
            group: 7,
            id: 2
        ) };

        Reconcile(
            pool,
            Creation(shapes),
            0
        );
        var initial = Emit(pool: pool);

        Assert.Single(initial.Instances);
        Assert.Equal(SlotBase, initial.Instances[0].Slot);
        Reconcile(
            pool,
            Creation([.. shapes, Shape(id: 3)]),
            0
        );
        var grown = Emit(pool: pool);

        Assert.Single(grown.Instances);
        Assert.Equal(SlotBase, grown.Instances[0].Slot);
        Assert.Equal(
            1,
            grown.InstanceMaskWordCount
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void OnlyMembersSharingTheSameRigidMotionUseATightBound(bool inheritMotion) {
        var pool = new WorldCreationInstances();
        var first = Shape(
            id: 0,
            group: 7
        ) with {
            Swings = [new ShapeSwingDocument(
                Amplitude: 0.7f,
                Axis: Vector3.UnitZ,
                Driver: "sway",
                Pivot: Vector3.Zero
            )],
        };
        var second = Shape(
            id: 1,
            group: 7
        ) with {
            Parent = (inheritMotion
            ? first.Name!.Value
            : null),
        };
        var template = Creation(Shape(id: 0));
        var canonical = CreationCanonicalizer.Canonicalize(
            document: template.Document with {
                Shapes = [first, second],
                Drivers = [new CreationDriverDocument(
                    Name: "sway",
                    Signal: CreationDriverDocument.SignalTime,
                    Cadence: 1f
                )],
            },
            source: "compact"
        );

        Reconcile(
            pool,
            new WorldPrototype(
                Id: template.Id,
                Document: canonical.Document,
                HashRaw: canonical.Hash
            ),
            0
        );
        var group = Assert.Single(collection: Emit(pool: pool).Instances);

        Assert.Equal(
            (inheritMotion
            ? (SlotBase + 1)
            : SlotBase),
            group.Slot
        );
    }
    // A small creation takes a compact registration and a creation past the compact shape count a full one, after every
    // compact range; once every compact registration is taken a small creation takes a full one too; and a body whose
    // creation grows past the compact shape count leaves its compact registration for a full one.


}
