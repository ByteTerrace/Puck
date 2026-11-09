using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class MovingBlendRestLawTests {
    [Theory]
    [InlineData("swing")]
    [InlineData("slide")]
    [InlineData("frame")]
    public void EveryOwnerPublishesTheIdentityDeltaAtAuthoredRest(string kind) {
        var shape = new ShapeDocument(0, "part", SdfSolidPrimitive.Sphere, new Vector3(0.2f, 0.3f, 0.1f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f), new Vector3(0.03f), 0, SdfBlendOp.Union, 0f, 0) {
            Swings = kind == "swing" ? [new("clock", Vector3.UnitY, Vector3.UnitZ, 0.2f)] : null,
            Slides = kind == "slide" ? [new("clock", Vector3.UnitX, 0.02f)] : null,
        };
        var document = CreationFixtures.Document("rest-delta", [shape]) with {
            Drivers = [new("clock", CreationDriverDocument.SignalTime, 1f)],
            Frames = kind == "frame" ? [new("away", [new(0, Vector3.UnitY, Quaternion.Identity)])] : null,
        };
        var creation = CreationFixtures.Prototype("rest-delta", document);
        var definition = Fixtures.BuildDocument() with { CreationsRaw = [creation], LookRowsRaw = [new("rest-delta", new WorldLookSource.Creation(creation.Id), 1f, WorldLookMotion.Default)] };
        var client = ClientFixtures.Client(definition);
        var rootPosition = new Vector3(2f, 3f, 4f);
        var rootRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        client.DeliverSnapshot(new("a", 1, 1, 1, [new(Active: true, Index: 0, Generation: 1, Continuity: EntityContinuity.Continuous, BodyColor: Vector3.One, CatalogRig: 0, Kit: 0, Look: 0, Orientation: rootRotation, Position: rootPosition)]));
        client.UpdateRenderPoses(1f);
        var pool = new WorldCreationInstances();
        pool.Reconcile(WorldPlacementFrameCompilation.Compile([], 0), [], [creation], [], [new(0, creation, 1f, WorldLook.Implicit)]);
        var builder = new SdfProgramBuilder();
        pool.Emit(builder, WorldBakedColors.Of(definition), false, 1f, slotBase: 0);
        var program = builder.Build();
        var transforms = new DynamicTransform[pool.DynamicSlotCount];
        var moved = new SdfMovedTransforms();
        moved.Begin(true, transforms.Length);
        pool.PackTransforms(transforms, client, moved, 0, new(0f, -1000f, 0f));
        Assert.Equal(2, transforms.Length);
        Assert.InRange(Vector3.Distance(transforms[0].Position, transforms[1].Position), 0f, 0.000001f);
        Assert.InRange(MathF.Abs(Quaternion.Dot(transforms[0].Orientation, transforms[1].Orientation)), 0.999999f, 1.000001f);
        Assert.All(program.Instances, instance => Assert.True(instance.RestPoseSkin));
    }

    [Fact]
    public void TheMotionPlanSplitsOnlyWhenTheDocumentExceedsItsEnvelope() {
        var first = new ShapeDocument(0, "anchor", SdfSolidPrimitive.Sphere, Vector3.Zero, Quaternion.Identity, new Vector3(0.03f), 0, SdfBlendOp.Union, 0f, 1);
        var second = first with { Id = 1, Name = "moving", Blend = SdfBlendOp.SmoothUnion, Smooth = 0.04f, Slides = [new("clock", Vector3.UnitX, 0.002f)] };
        var document = CreationFixtures.Document("envelope", [first, second]) with { Drivers = [new("clock", CreationDriverDocument.SignalTime, 1f)] };
        var admitted = new CreationMotionPlan(document);
        Assert.Equal(admitted.RenderDocument.Shapes![0].Group, admitted.RenderDocument.Shapes[1].Group);
        var pair = Assert.Single(CreationMotion.MovingBlends(document));
        Assert.True(CreationMotion.Envelope(document, pair).Admitted);
        var wide = document with { Shapes = [first, second with { Slides = [new("clock", Vector3.UnitX, 0.03f)] }] };
        var split = new CreationMotionPlan(wide);
        Assert.NotEqual(split.RenderDocument.Shapes![0].Group, split.RenderDocument.Shapes[1].Group);
        Assert.Contains("bakes as a crease", CreationMotion.Describe(wide, pair));
    }
}
