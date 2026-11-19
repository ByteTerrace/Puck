using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// THE LAW: <see cref="CreationMotion.MovingBlends"/> names each blend of an animated creation's pooled group that joins,
/// by a blend other than union, shapes that move independently (a swing, a slide or a frame one has and the other does
/// not share through a parent), and nothing else: a union, an ungrouped shape, a shape sharing its partner's motion
/// through a parent or a frame moving both alike, and a creation that does not animate draw no warning.
/// </summary>
public sealed class CreationMotionLawTests {
    private static readonly CreationDriverDocument Clock = new(Cadence: 1f, Name: "clock", Signal: CreationDriverDocument.SignalTime);
    private static readonly ShapeSlideDocument Slide = new(Amplitude: 0.1f, Axis: Vector3.UnitX, Driver: "clock");
    private static readonly ShapeSwingDocument Swing = new(Amplitude: 0.5f, Axis: Vector3.UnitZ, Driver: "clock", Pivot: Vector3.Zero);

    [Fact]
    public void ASmoothBlendOntoAShapeThatSlidesAloneIsNamed() {
        var document = Document(drivers: [Clock], shapes: [
            Shape(id: 0, name: "anchor", group: 1),
            Shape(blend: SdfBlendOp.SmoothUnion, group: 1, id: 1, name: "visitor", slides: [Slide]),
        ]);
        var blend = Assert.Single(collection: CreationMotion.MovingBlends(rig: CreationRig.Of(document: document)));

        Assert.Equal(expected: new CreationMovingBlend(Blend: SdfBlendOp.SmoothUnion, Group: 1, Other: 0, Shape: 1), actual: blend);
        Assert.Contains(expectedSubstring: "shape 'visitor' to group 1's shape 'anchor' by SmoothUnion", actualString: CreationMotion.Describe(blend: blend, rig: CreationRig.Of(document: document)));
        // The control: as a union, or ungrouped, or in a creation nothing moves, it draws nothing.
        Assert.Empty(collection: CreationMotion.MovingBlends(rig: CreationRig.Of(document: document with { Shapes = [document.Shapes![0], document.Shapes[1] with { Blend = SdfBlendOp.Union }] })));
        Assert.Empty(collection: CreationMotion.MovingBlends(rig: CreationRig.Of(document: document with { Shapes = [document.Shapes![0], document.Shapes[1] with { Group = null }] })));
        Assert.Empty(collection: CreationMotion.MovingBlends(rig: CreationRig.Of(document: document with { Drivers = null, Shapes = [document.Shapes![0], document.Shapes[1] with { Slides = null }] })));
    }
    [Fact]
    public void AShapeInheritsItsParentsSwingAndSharesItWithItsSibling() {
        var document = Document(drivers: [Clock], shapes: [
            Shape(id: 0, name: "torso", group: 1),
            Shape(id: 1, name: "arm", swings: [Swing]),
            Shape(blend: SdfBlendOp.SmoothUnion, group: 1, id: 2, name: "hand", parent: "arm"),
            Shape(group: 2, id: 3, name: "sleeve", parent: "arm"),
            Shape(blend: SdfBlendOp.Subtraction, group: 2, id: 4, name: "cuff", parent: "arm"),
        ]);
        var blend = Assert.Single(collection: CreationMotion.MovingBlends(rig: CreationRig.Of(document: document)));

        Assert.Equal(expected: 2, actual: blend.Shape);
        Assert.Contains(expectedSubstring: "shape 'hand'", actualString: CreationMotion.Describe(blend: blend, rig: CreationRig.Of(document: document)));
    }
    [Fact]
    public void AFrameMovingOneShapeOfABlendIsNamedAndOneMovingBothAlikeIsNot() {
        var lowered = new Vector3(x: 0f, y: -0.1f, z: 0f);
        var document = Document(shapes: [
            Shape(group: 1, id: 0, name: "eye"),
            Shape(blend: SdfBlendOp.Intersection, group: 1, id: 1, name: "lid"),
        ], frames: [new FrameDocument(Name: "blink", Transforms: [new FrameTransformDocument(Id: 0, Position: lowered, Rotation: Quaternion.Identity), new FrameTransformDocument(Id: 1, Position: lowered, Rotation: Quaternion.Identity)])]);

        Assert.True(condition: CreationRig.Of(document: document).Animates);
        Assert.Empty(collection: CreationMotion.MovingBlends(rig: CreationRig.Of(document: document)));

        var apart = document with { Frames = [new FrameDocument(Name: "blink", Transforms: [new FrameTransformDocument(Id: 1, Position: lowered, Rotation: Quaternion.Identity)])] };
        var blend = Assert.Single(collection: CreationMotion.MovingBlends(rig: CreationRig.Of(document: apart)));

        Assert.Contains(expectedSubstring: "shape 'lid'", actualString: CreationMotion.Describe(blend: blend, rig: CreationRig.Of(document: apart)));

        // A frame holding every shape at its authored pose moves nothing.
        var still = document with { Frames = [new FrameDocument(Name: "rest", Transforms: [new FrameTransformDocument(Id: 1, Position: Vector3.Zero, Rotation: Quaternion.Identity)])] };

        Assert.False(condition: CreationRig.Of(document: still).Animates);
        Assert.Empty(collection: CreationMotion.MovingBlends(rig: CreationRig.Of(document: still)));
    }

    [Fact]
    public void AnAlignedTipThatIsNoBoneOwnsItsSlotAndItsSmoothSeamCreases() {
        var document = Document(drivers: [Clock], shapes: [
            Shape(id: 0, name: "thigh", swings: [Swing]),
            Shape(id: 1, name: "shin", parent: "thigh", group: 1),
            Shape(blend: SdfBlendOp.SmoothUnion, group: 1, id: 2, name: "boot", parent: "shin"),
        ]) with {
            Effectors = [new CreationEffectorDocument(Align: 1f, Chain: ["thigh", "shin"], Name: "foot", Target: new CreationEffectorTargetDocument(Kind: CreationEffectorTargetDocument.KindSurface), Tip: "boot")],
        };
        var rig = CreationRig.Of(document: document);

        // The boot is no bone and swings nothing, but the align turns it every frame: it is a writer and owns a slot.
        Assert.Equal(expected: CreationRigWriters.Align, actual: rig.Writers(shape: 2));
        Assert.Equal(expected: 2, actual: rig.Owner(shape: 2));
        Assert.Equal(expected: [0, 1, 2], actual: new CreationMotionPlan(rig: rig).Owners);

        var blend = Assert.Single(collection: CreationMotion.MovingBlends(rig: rig));

        Assert.Equal(expected: "effector motion is unbounded", actual: CreationMotion.Envelope(blend: blend, rig: rig).Refusal);
        Assert.True(condition: CreationMotion.Creases(blend: blend, rig: rig));
        // The control: without the align the boot rides the shin's slot and joins it by no moving seam.
        var unaligned = CreationRig.Of(document: document with { Effectors = [document.Effectors![0] with { Align = null }] });

        Assert.Equal(expected: 1, actual: unaligned.Owner(shape: 2));
        Assert.Empty(collection: CreationMotion.MovingBlends(rig: unaligned));
    }
    [Fact]
    public void AShapeTurnsAboutItsJointBeforeItsFirstSwingsPivot() {
        var joint = new Vector3(x: 0f, y: 0.5f, z: 0f);
        var document = Document(drivers: [Clock], shapes: [
            Shape(id: 0, name: "upper", swings: [Swing with { Pivot = new Vector3(x: 0f, y: 1f, z: 0f) }]) with { Joint = joint },
            Shape(id: 1, name: "lower", parent: "upper", swings: [Swing with { Pivot = new Vector3(x: 0f, y: 2f, z: 0f) }]),
            Shape(id: 2, name: "tip", parent: "lower"),
        ]);
        var rig = CreationRig.Of(document: document);

        Assert.Equal(expected: joint, actual: rig.Joint(shape: 0));
        Assert.Equal(expected: new Vector3(x: 0f, y: 2f, z: 0f), actual: rig.Joint(shape: 1));
        Assert.Equal(expected: Vector3.Zero, actual: rig.Joint(shape: 2));
        // The rig plays the swings, so the document it hands every reader carries none, and it refuses to be compiled.
        Assert.All(collection: rig.Document.Shapes!, action: static shape => Assert.Null(@object: shape.Swings));
        Assert.Throws<ArgumentException>(testCode: () => CreationRig.Compile(document: rig.Document));
    }

    private static CreationDocument Document(IReadOnlyList<ShapeDocument> shapes, IReadOnlyList<CreationDriverDocument>? drivers = null, IReadOnlyList<FrameDocument>? frames = null) =>
        new(Drivers: drivers, Frames: frames, Name: "probe", Palette: null, Schema: CreationDocument.CurrentSchema, Shapes: shapes);
    private static ShapeDocument Shape(int id, string name, int? group = null, SdfBlendOp blend = SdfBlendOp.Union, string? parent = null, IReadOnlyList<ShapeSlideDocument>? slides = null, IReadOnlyList<ShapeSwingDocument>? swings = null) =>
        new(
            Blend: blend,
            Group: group,
            Id: id,
            Material: 0,
            Name: name,
            Parent: parent,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(value: 0.1f),
            Slides: slides,
            Smooth: 0.05f,
            Swings: swings,
            Type: SdfSolidPrimitive.Sphere
        );
}
