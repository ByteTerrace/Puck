using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Unused render capacity must not widen a live program's masks or move surviving transform addresses.</summary>
public sealed class WorldStampPoolCompactionLawTests {
    private const int SlotBase = 23;

    private static WorldPrototype Creation(params ShapeDocument[] shapes) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: "compact",
                Palette: [new("#AA7755", null, null, null)],
                Shapes: shapes,
                Frames: null
            ),
            source: "compact"
        );
        return new WorldPrototype(Id: "compact", Document: canonical.Document, HashRaw: canonical.Hash);
    }

    private static ShapeDocument Shape(int id, int? group = null, SdfBlendOp blend = SdfBlendOp.Union) => new(
        Id: id,
        Name: $"part-{id}",
        Type: SdfSolidPrimitive.Box,
        Position: new Vector3(id * 0.1f, 0f, 0f),
        Rotation: Quaternion.Identity,
        Scale: new Vector3(0.1f),
        Material: 0,
        Blend: blend,
        Smooth: 0f,
        Group: group
    );

    private static void Reconcile(WorldStampPool pool, WorldPrototype creation, params int[] bodies) => pool.Reconcile(
        placements: [],
        creations: [creation],
        dynamics: [],
        bodyStamps: bodies.Select(body => new WorldStampPool.BodyStamp(
            BodyIndex: body,
            Creation: creation,
            Scale: 1f,
            Motion: WorldLookMotion.Default
        )).ToArray()
    );

    private static SdfProgram Emit(WorldStampPool pool) {
        var builder = new SdfProgramBuilder();
        pool.Emit(
            builder: builder,
            definition: Fixtures.BuildGradientUpDocument(gradientUp: false),
            probeWorstCase: false,
            maxPlacementScale: 1f,
            slotBase: SlotBase
        );
        return builder.Build(buildInstanceGrid: false);
    }

    [Fact]
    public void EmptyPoolEmitsNoGeometry() {
        var program = Emit(pool: new WorldStampPool());
        Assert.Empty(program.Instances);
        Assert.DoesNotContain(program.Instructions, instruction => instruction.Op == SdfOp.ShapeBlend);
    }

    [Fact]
    public void RemovingAnEarlierBodyKeepsTheSurvivorsTransformAddress() {
        var pool = new WorldStampPool();
        var creation = Creation(Shape(id: 0));
        Reconcile(pool, creation, 0, 1);
        var before = Emit(pool);
        Assert.Equal(2, before.Instances.Count);
        var survivorSlot = before.Instances[1].Slot;
        Assert.Equal(SlotBase + WorldStampPool.SlotsPerPlacement + 1, survivorSlot);

        Reconcile(pool, creation, 1);
        var after = Emit(pool);
        var instance = Assert.Single(after.Instances);
        Assert.True(instance.Active);
        Assert.Equal(survivorSlot, instance.Slot);
        Assert.Equal(1, after.InstanceMaskWordCount);

        Reconcile(pool, creation, 1, 2);
        var reused = Emit(pool);
        Assert.Equal(2, reused.Instances.Count);
        Assert.Contains(reused.Instances, item => item.Slot == survivorSlot);
        Assert.Contains(reused.Instances, item => item.Slot == SlotBase + 1);
    }

    [Fact]
    public void GroupsAndNewShapesUseAuthorSlotsWithoutPlaceholderInstances() {
        var pool = new WorldStampPool();
        var shapes = new[] { Shape(id: 0, group: 7), Shape(id: 1), Shape(id: 2, group: 7, blend: SdfBlendOp.Subtraction) };
        Reconcile(pool, Creation(shapes), 0);
        var initial = Emit(pool);
        Assert.Equal(2, initial.Instances.Count);
        Assert.Contains(initial.Instances, item => item.Slot == SlotBase + 1);
        Assert.Contains(initial.Instances, item => item.Slot == SlotBase + 2);

        Reconcile(pool, Creation([.. shapes, Shape(id: 3)]), 0);
        var grown = Emit(pool);
        Assert.Equal(3, grown.Instances.Count);
        Assert.All(grown.Instances, item => Assert.True(item.Active));
        Assert.Contains(grown.Instances, item => item.Slot == SlotBase + 4);
        Assert.Equal(1, grown.InstanceMaskWordCount);
    }

    [Fact]
    public void ARemoteCutterDoesNotWidenTheGroupsSolidBound() {
        var pool = new WorldStampPool();
        var solid = Shape(id: 0, group: 7) with { Position = new Vector3(0f, 8f, 0f) };
        var cutter = Shape(id: 1, group: 7, blend: SdfBlendOp.Subtraction) with {
            Position = new Vector3(10f, 0f, 0f), Scale = new Vector3(2f),
        };
        Reconcile(pool, Creation(solid, cutter), 0);
        var group = Assert.Single(Emit(pool).Instances);
        Assert.Equal(SlotBase + 1, group.Slot);
        Assert.InRange(group.Radius, 0.1f, 0.3f);
    }

    [Fact]
    public void AFrameMovingAUnionMemberIsIncludedInTheBound() {
        var pool = new WorldStampPool();
        var first = Shape(id: 0, group: 7);
        var second = Shape(id: 1, group: 7);
        var creation = Creation(first, second);
        var canonical = CreationCanonicalizer.Canonicalize(
            document: creation.Document with {
                Frames = [new FrameDocument("move", [new FrameTransformDocument(
                    Id: second.Id, Position: new Vector3(2f, 0f, 0f), Rotation: Quaternion.Identity, Scale: second.Scale)])],
            },
            source: "compact"
        );
        creation = new WorldPrototype(Id: creation.Id, Document: canonical.Document, HashRaw: canonical.Hash);
        Reconcile(pool, creation, 0);
        var group = Assert.Single(Emit(pool).Instances);
        Assert.Equal(SlotBase + 1, group.Slot);
        Assert.True(group.Radius >= 2.1f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyMembersSharingTheSameRigidMotionUseATightBound(bool inheritMotion) {
        var pool = new WorldStampPool();
        var first = Shape(id: 0, group: 7) with {
            Swings = [new ShapeSwingDocument(Amplitude: 0.7f, Axis: Vector3.UnitZ, Driver: "sway", Pivot: Vector3.Zero)],
        };
        var second = Shape(id: 1, group: 7) with { Parent = inheritMotion ? first.Name!.Value : null };
        var template = Creation(Shape(id: 0));
        var canonical = CreationCanonicalizer.Canonicalize(
            document: template.Document with {
                Shapes = [first, second],
                Drivers = [new CreationDriverDocument(Name: "sway", Signal: CreationDriverDocument.SignalTime, Cadence: 1f)],
            },
            source: "compact"
        );
        Reconcile(pool, new WorldPrototype(Id: template.Id, Document: canonical.Document, HashRaw: canonical.Hash), 0);
        var group = Assert.Single(Emit(pool).Instances);
        Assert.Equal(inheritMotion ? SlotBase + 1 : SlotBase, group.Slot);
    }
}
