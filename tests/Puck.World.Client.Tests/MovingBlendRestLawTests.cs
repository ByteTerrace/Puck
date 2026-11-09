using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class MovingBlendRestLawTests {
    [InlineData("swing")]
    [InlineData("slide")]
    [InlineData("frame")]
    [Theory]
    public void EveryOwnerPublishesTheIdentityDeltaAtAuthoredRest(string kind) {
        var shape = new ShapeDocument(0, "part", SdfSolidPrimitive.Sphere, new Vector3(x: 0.2f, y: 0.3f, z: 0.1f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f), new Vector3(value: 0.03f), 0, SdfBlendOp.Union, 0f, 0) {
            Swings = ((kind == "swing") ? [new("clock", Vector3.UnitY, Vector3.UnitZ, 0.2f)] : null),
            Slides = ((kind == "slide") ? [new("clock", Vector3.UnitX, 0.02f)] : null),
        };
        var document = CreationFixtures.Document("rest-delta", [shape]) with {
            Drivers = [new("clock", CreationDriverDocument.SignalTime, 1f)],
            Frames = ((kind == "frame") ? [new(Name: "away", Transforms: [new(Id: 0, Position: Vector3.UnitY, Rotation: Quaternion.Identity)])] : null),
        };
        var creation = CreationFixtures.Prototype(document: document, id: "rest-delta");
        var definition = Fixtures.BuildDocument() with { CreationsRaw = [creation], LookRowsRaw = [new("rest-delta", new WorldLookSource.Creation(PrototypeId: creation.Id), 1f, WorldLookMotion.Default)] };
        var client = ClientFixtures.Client(definition: definition);
        var rootPosition = new Vector3(x: 2f, y: 3f, z: 4f);
        var rootRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);

        client.DeliverSnapshot(snapshot: new(Authority: "a", Revision: 1, Tick: 1, StepTicks: 1, Entries: new EntitySnapshot[] { new(Active: true, Index: 0, Generation: 1, Continuity: EntityContinuity.Continuous, BodyColor: Vector3.One, CatalogRig: 0, Kit: 0, Look: 0, Orientation: rootRotation, Position: rootPosition) }));
        client.UpdateRenderPoses(alpha: 1f);
        var pool = new WorldCreationInstances();

        pool.Reconcile(WorldPlacementFrameCompilation.Compile(placements: [], worldSeed: 0), [], [creation], [], [new(0, creation, 1f, WorldLook.Implicit)]);
        var builder = new SdfProgramBuilder();

        pool.Emit(builder, WorldBakedColors.Of(definition: definition), false, 1f, slotBase: 0);
        var program = builder.Build();
        var transforms = new DynamicTransform[pool.DynamicSlotCount];
        var moved = new SdfMovedTransforms();

        moved.Begin(everything: true, tableRows: transforms.Length);
        pool.PackTransforms(transforms, client, moved, 0, new(x: 0f, y: -1000f, z: 0f));
        Assert.Equal(2, transforms.Length);
        Assert.InRange(Vector3.Distance(value1: transforms[0].Position, value2: transforms[1].Position), 0f, 0.000001f);
        Assert.InRange(MathF.Abs(x: Quaternion.Dot(quaternion1: transforms[0].Orientation, quaternion2: transforms[1].Orientation)), 0.999999f, 1.000001f);
        Assert.All(program.Instances, instance => Assert.True(condition: instance.RestPoseSkin));
    }
    [Fact]
    public void TheMotionPlanSplitsOnlyWhenTheDocumentExceedsItsEnvelope() {
        var first = new ShapeDocument(0, "anchor", SdfSolidPrimitive.Sphere, Vector3.Zero, Quaternion.Identity, new Vector3(value: 0.03f), 0, SdfBlendOp.Union, 0f, 1);
        var second = first with { Id = 1, Name = "moving", Blend = SdfBlendOp.SmoothUnion, Smooth = 0.04f, Slides = [new("clock", Vector3.UnitX, 0.002f)] };
        var document = CreationFixtures.Document("envelope", [first, second]) with { Drivers = [new("clock", CreationDriverDocument.SignalTime, 1f)] };
        var admitted = new CreationMotionPlan(document);

        Assert.Equal(admitted.RenderDocument.Shapes![0].Group, admitted.RenderDocument.Shapes[1].Group);
        var pair = Assert.Single(collection: CreationMotion.MovingBlends(document: document));

        Assert.True(condition: CreationMotion.Envelope(blend: pair, document: document).Admitted);
        var wide = document with { Shapes = [first, second with { Slides = [new("clock", Vector3.UnitX, 0.03f)] }] };
        var split = new CreationMotionPlan(wide);

        Assert.NotEqual(split.RenderDocument.Shapes![0].Group, split.RenderDocument.Shapes[1].Group);
        Assert.Contains("bakes as a crease", CreationMotion.Describe(blend: pair, document: wide));
    }
}
