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
        var blend = Assert.Single(collection: CreationMotion.MovingBlends(document: document));

        Assert.Equal(expected: new CreationMovingBlend(Blend: SdfBlendOp.SmoothUnion, Group: 1, Other: 0, Shape: 1), actual: blend);
        Assert.Contains(expectedSubstring: "shape 'visitor', which moves by its own swings, slides or effector, to group 1's shape 'anchor', which rides the creation root, by SmoothUnion", actualString: CreationMotion.Describe(blend: blend, document: document));
        // The control: as a union, or ungrouped, or in a creation nothing moves, it draws nothing.
        Assert.Empty(collection: CreationMotion.MovingBlends(document: document with { Shapes = [document.Shapes![0], document.Shapes[1] with { Blend = SdfBlendOp.Union }] }));
        Assert.Empty(collection: CreationMotion.MovingBlends(document: document with { Shapes = [document.Shapes![0], document.Shapes[1] with { Group = null }] }));
        Assert.Empty(collection: CreationMotion.MovingBlends(document: document with { Drivers = null, Shapes = [document.Shapes![0], document.Shapes[1] with { Slides = null }] }));
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
        var blend = Assert.Single(collection: CreationMotion.MovingBlends(document: document));

        Assert.Equal(expected: 2, actual: blend.Shape);
        Assert.Contains(expectedSubstring: "'hand', which moves with 'arm'", actualString: CreationMotion.Describe(blend: blend, document: document));
    }
    [Fact]
    public void AFrameMovingOneShapeOfABlendIsNamedAndOneMovingBothAlikeIsNot() {
        var lowered = new Vector3(x: 0f, y: -0.1f, z: 0f);
        var document = Document(shapes: [
            Shape(group: 1, id: 0, name: "eye"),
            Shape(blend: SdfBlendOp.Intersection, group: 1, id: 1, name: "lid"),
        ], frames: [new FrameDocument(Name: "blink", Transforms: [new FrameTransformDocument(Id: 0, Position: lowered, Rotation: Quaternion.Identity), new FrameTransformDocument(Id: 1, Position: lowered, Rotation: Quaternion.Identity)])]);

        Assert.True(condition: CreationMotion.Animates(document: document));
        Assert.Empty(collection: CreationMotion.MovingBlends(document: document));

        var apart = document with { Frames = [new FrameDocument(Name: "blink", Transforms: [new FrameTransformDocument(Id: 1, Position: lowered, Rotation: Quaternion.Identity)])] };
        var blend = Assert.Single(collection: CreationMotion.MovingBlends(document: apart));

        Assert.Contains(expectedSubstring: "'lid', which is posed by a timeline frame", actualString: CreationMotion.Describe(blend: blend, document: apart));

        // A frame holding every shape at its authored pose moves nothing.
        var still = document with { Frames = [new FrameDocument(Name: "rest", Transforms: [new FrameTransformDocument(Id: 1, Position: Vector3.Zero, Rotation: Quaternion.Identity)])] };

        Assert.False(condition: CreationMotion.Animates(document: still));
        Assert.Empty(collection: CreationMotion.MovingBlends(document: still));
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
