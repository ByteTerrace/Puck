using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A creation's render bound composes through its shapes' blends the way the program's instance bound does
/// (<see cref="SdfBoundAlgebra"/>): one scoped instance holds the whole creation, so an unbounded lattice a box clips
/// is as bounded as the box, a box minus a lattice keeps the box's, and a lattice with a box unioned in has none. An
/// infinite repeat has no reach, as an unbounded wallpaper has none, and no radius of 1e6 cells stands in for it.</summary>
public sealed class CreationBoundComposeLawTests {
    private static readonly Vector3 BoxScale = new(x: 3f, y: 1f, z: 2f);

    // A repeat with an absent limit, or a limit at the sentinel on any axis, has copies at every distance and answers
    // the program's own mark for an influence nothing contains; a limit short of it reaches its cells.
    [Fact]
    public void AnUnboundedRepeatReachesAsFarAsNothingContains() {
        ShapeDomainOp[] unbounded = [
            new ShapeDomainOp.Repeat(Spacing: Vector3.One),
            new ShapeDomainOp.Repeat(Spacing: Vector3.One, Limit: new Vector3(x: SdfDomainOps.UnboundedRepeatLimit, y: 2f, z: 2f)),
            new ShapeDomainOp.Repeat(Spacing: Vector3.One, Limit: new Vector3(x: 2f, y: 2f, z: SdfDomainOps.UnboundedRepeatLimit)),
        ];

        foreach (var op in unbounded) {
            Assert.True(condition: Assert.IsType<ShapeDomainOp.Repeat>(@object: op).IsUnbounded);
            Assert.True(condition: SdfBoundAlgebra.IsUnbounded(bound: ShapeDomainOps.Reach(domain: [op])));
        }

        var finite = new ShapeDomainOp.Repeat(Spacing: new Vector3(x: 2f, y: 1f, z: 1f), Limit: new Vector3(x: 3f, y: 0f, z: 0f));

        Assert.False(condition: finite.IsUnbounded);
        Assert.Equal(expected: 6f, actual: ShapeDomainOps.Reach(domain: [finite]));
    }
    [Fact]
    public void AnIntersectionTakesTheSmallerReachWhateverTheOrder() {
        var box = Box(blend: SdfBlendOp.Union);
        var boxReach = Reach(composed: true, shapes: [box]);

        foreach (var lattice in Lattices()) {
            Assert.Equal(expected: boxReach, actual: Reach(composed: true, shapes: [lattice, (box with { Blend = SdfBlendOp.Intersection })]));
            Assert.Equal(expected: boxReach, actual: Reach(composed: true, shapes: [box, (lattice with { Blend = SdfBlendOp.Intersection })]));
            Assert.Equal(expected: boxReach, actual: Reach(composed: true, shapes: [lattice, (box with { Blend = SdfBlendOp.SmoothIntersection })]));
        }
    }
    [Fact]
    public void ASubtractionKeepsItsSubjectsReach() {
        var box = Box(blend: SdfBlendOp.Union);

        foreach (var lattice in Lattices()) {
            Assert.Equal(expected: Reach(composed: true, shapes: [box]), actual: Reach(composed: true, shapes: [box, (lattice with { Blend = SdfBlendOp.Subtraction })]));
            Assert.True(condition: SdfBoundAlgebra.IsUnbounded(bound: Reach(composed: true, shapes: [lattice, (box with { Blend = SdfBlendOp.Subtraction })])));
        }
    }
    [Fact]
    public void AUnionTakesTheLargerReachSoAnUnboundedOperandLeavesNone() {
        var box = Box(blend: SdfBlendOp.Union);

        foreach (var lattice in Lattices()) {
            foreach (var blend in new[] { SdfBlendOp.Union, SdfBlendOp.SmoothUnion }) {
                Assert.True(condition: SdfBoundAlgebra.IsUnbounded(bound: Reach(composed: true, shapes: [lattice, (box with { Blend = blend })])));
                Assert.True(condition: SdfBoundAlgebra.IsUnbounded(bound: Reach(composed: true, shapes: [box, (lattice with { Blend = blend })])));
            }
        }
    }
    // A subset of a creation's shapes has not seen the blends that bound them, so the reach the dynamic pool's per-shape
    // and per-group instances read stays the largest of its shapes'.
    [Fact]
    public void ABoundWithoutComposedBlendsIsTheLargestShapeReach() {
        var box = Box(blend: SdfBlendOp.Intersection);

        foreach (var lattice in Lattices()) {
            Assert.True(condition: SdfBoundAlgebra.IsUnbounded(bound: Reach(composed: false, shapes: [lattice, box])));
        }
    }
    // End to end through the static stamper: the one scoped instance of a lattice clipped by a box packs the box's bound
    // and stays cullable; the same lattice with the box unioned in is uncullable.
    [Fact]
    public void AStampedLatticeClippedByABoxIsCullableAndOneUnionedWithItIsNot() {
        var box = Box(blend: SdfBlendOp.Union);
        var boxReach = Reach(composed: true, shapes: [box]);

        foreach (var lattice in Lattices()) {
            var clipped = Stamp(shapes: [lattice, (box with { Blend = SdfBlendOp.Intersection })]);

            Assert.False(condition: clipped.Unmaskable, userMessage: $"{lattice.Domain![0]} ∩ box");
            Assert.InRange(actual: clipped.BoundRadius, low: boxReach, high: (boxReach + 1f));
            Assert.True(condition: Stamp(shapes: [lattice, box]).Unmaskable, userMessage: $"{lattice.Domain![0]} ∪ box");
            Assert.False(condition: Stamp(shapes: [box, (lattice with { Blend = SdfBlendOp.Subtraction })]).Unmaskable, userMessage: $"box − {lattice.Domain![0]}");
        }
    }
    // Unbounded is a state, never a number a scale can overflow: a P6M lattice of 0.001 cells at a placement scale of 1e9
    // reaches as far as nothing contains, where the sentinel times the scale ran to infinity and BeginInstance refused it;
    // the placement is admitted and packs the unmaskable bound, and the same lattice clipped by a box is as bounded as the
    // box at that scale.
    [Fact]
    public void AnUnboundedLatticeAtAHugeScaleIsAdmittedAndPacksTheUnmaskableBound() {
        var lattice = (CreationFixtures.UnitSphereShape with { Domain = [new ShapeDomainOp.Wallpaper(Cell: new Vector2(value: 0.001f), Group: SdfWallpaperGroup.P6M)] });
        var box = Box(blend: SdfBlendOp.Intersection);

        // A scale below one shrinks a sentinel number into a radius too, so the states are checked on both sides of it.
        foreach (var scale in new[] { 1e-3f, 1e9f, 1e30f }) {
            Assert.True(condition: SdfBoundAlgebra.IsUnbounded(bound: Reach(composed: false, scale: scale, shapes: [lattice])), userMessage: $"scale {scale}");

            SdfInstanceCost cost = default;

            Assert.Null(@object: Record.Exception(testCode: () => cost = Stamp(scale: scale, shapes: [lattice])));

            Assert.True(condition: cost.Unmaskable, userMessage: $"scale {scale}");
            Assert.Equal(expected: SdfProgram.UnmaskableBoundRadius, actual: cost.BoundRadius);

            var clipped = Stamp(scale: scale, shapes: [lattice, box]);
            var boxReach = Reach(composed: true, scale: scale, shapes: [box with { Blend = SdfBlendOp.Union }]);

            Assert.False(condition: clipped.Unmaskable, userMessage: $"clipped, scale {scale}");
            Assert.InRange(actual: clipped.BoundRadius, low: boxReach, high: ((boxReach * 1.05f) + 1f));
        }
    }

    private static ShapeDocument Box(SdfBlendOp blend) => (CreationFixtures.Shape(scale: BoxScale, type: SdfSolidPrimitive.Box) with { Blend = blend });
    // The unbounded prototypes: a sphere folded through a hex lattice with no limit, and one through an infinite repeat.
    private static ShapeDocument[] Lattices() => [
        (CreationFixtures.UnitSphereShape with { Domain = [new ShapeDomainOp.Wallpaper(Cell: new Vector2(value: 4f), Group: SdfWallpaperGroup.P6M)] }),
        (CreationFixtures.UnitSphereShape with { Domain = [new ShapeDomainOp.Repeat(Spacing: new Vector3(value: 4f))] }),
    ];
    private static CreationDocument Document(IReadOnlyList<ShapeDocument> shapes) => CreationFixtures.Document(name: "compose", shapes: [.. shapes.Select(selector: (shape, index) => (shape with { Id = index }))]);
    private static float Reach(bool composed, IReadOnlyList<ShapeDocument> shapes, float scale = 1f) => CreationStampEmitter.RenderReach(
        composeBlends: composed,
        document: Document(shapes: shapes),
        fontFor: null,
        scale: scale
    );
    private static SdfInstanceCost Stamp(IReadOnlyList<ShapeDocument> shapes, float scale = 1f) {
        var definition = CreationFixtures.RigWorld(creation: Document(shapes: shapes));
        var builder = new SdfProgramBuilder();

        WorldPlacementStamper.EmitStatic(
            builder: builder,
            creations: definition.Creations,
            definition: definition,
            placements: [new WorldPlacement(
                Id: "compose-placed",
                Position: new DocumentVector3(value: Vector3.Zero),
                PrototypeId: "rig",
                Scale: scale,
                YawDegrees: 0f
            )]
        );

        return builder.Build().InspectInstance(index: 0);
    }
}
