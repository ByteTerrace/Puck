using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a <see cref="ShapeDomainOp.Repeat"/>/<see cref="ShapeDomainOp.Polar"/> domain op's <c>origin</c> centres
/// its fold on that point instead of the creation root, on both emission chains
/// (<see cref="CreationStampEmitter"/> and the client's animated stamper, which share
/// <see cref="ShapeDomainOps.Apply"/>) and the contact expansion (<see cref="ShapeDomainOps.TryExpand"/>), while a
/// null origin stays byte-identical to the pre-existing fold. The reproduced defect: a bounded repeat whose lattice
/// is authored off the creation root folds around the wrong point, so the clamp that should distribute copies across
/// the lattice instead saturates immediately and collapses every copy but the one nearest the root's own cell.
/// </summary>
public sealed class CreationDomainOriginLawTests {
    private const string PrototypeId = "domain-origin";
    // Mirrors the moth wing-band defect: a shape at y=0.96 with a Y repeat of spacing 0.21, limit 1 folds around the
    // creation root and collapses to one visible band at y=1.17 instead of three at 0.75/0.96/1.17. Scaled up here
    // (x10) so a small sphere radius still clears each band's neighbours with headroom.
    private static readonly Vector3 BandShapePosition = new(x: 0f, y: 9.6f, z: 0f);
    private static readonly Vector3 BandSpacing = new(x: 0f, y: 2.1f, z: 0f);
    private static readonly Vector3 BandLimit = new(x: 0f, y: 1f, z: 0f);
    private static readonly Vector3 NearBand = new(x: 0f, y: 7.5f, z: 0f);
    private static readonly Vector3 CentreBand = new(x: 0f, y: 9.6f, z: 0f);
    private static readonly Vector3 FarBand = new(x: 0f, y: 11.7f, z: 0f);

    private static ShapeDocument SphereAt(Vector3 position, IReadOnlyList<ShapeDomainOp> domain) =>
        new(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Sphere,
            Position: position,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(value: 0.5f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0,
            Domain: domain
        );
    private static CreationDocument Document(ShapeDocument shape) =>
        new(
            Schema: CreationDocument.CurrentSchema,
            Name: PrototypeId,
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
    private static SdfProgram Emit(ShapeDocument shape) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.Emit(
            builder: builder,
            document: Document(shape: shape),
            materialFor: _ => material,
            transform: new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: 1f, ReflectionNormal: null)
        );

        return builder.Build(buildInstanceGrid: false);
    }
    private static bool IsInside(SdfProgram program, Vector3 point) {
        var evaluator = new SdfFieldEvaluator(program: program);

        Assert.True(condition: evaluator.TryDistance(
            distance: out var distance,
            material: out _,
            position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))
        ));

        return (((double)distance) < 0d);
    }
    private static void AssertCanonicalizerRefusesNaming(ShapeDocument shape, string needle) {
        var violations = CreationCanonicalizer.Validate(document: Document(shape: shape));

        Assert.NotEmpty(collection: violations);
        Assert.Contains(
            collection: violations,
            filter: violation => violation.Message.Contains(comparisonType: StringComparison.Ordinal, value: needle)
        );
    }
    private static void AssertCanonicalizerAccepts(ShapeDocument shape) =>
        Assert.Empty(collection: CreationCanonicalizer.Validate(document: Document(shape: shape)));

    /// <summary>The control reproducing the defect: with no origin, the fold centres cell selection on the creation
    /// root, so the off-centre lattice saturates its clamp before it reaches its own three cells — only the far band
    /// (nearest the root's own cell) resolves.</summary>
    [Fact]
    public void ANullOriginFoldsAroundTheCreationRootAndCollapsesAnOffCentreLattice() {
        var program = Emit(shape: SphereAt(
            domain: [new ShapeDomainOp.Repeat(Spacing: BandSpacing, Limit: BandLimit)],
            position: BandShapePosition
        ));

        Assert.True(condition: IsInside(program: program, point: FarBand), userMessage: "the far band should still resolve without an origin.");
        Assert.False(condition: IsInside(program: program, point: CentreBand), userMessage: "the origin-free fold collapses the centre band.");
        Assert.False(condition: IsInside(program: program, point: NearBand), userMessage: "the origin-free fold collapses the near band.");
    }

    /// <summary>The subject: an origin at the shape's own rest position folds the lattice around it, so all three
    /// authored copies resolve.</summary>
    [Fact]
    public void AnOriginAtTheShapesOwnPositionFoldsTheLatticeAroundItInstead() {
        var program = Emit(shape: SphereAt(
            domain: [new ShapeDomainOp.Repeat(Spacing: BandSpacing, Limit: BandLimit, Origin: BandShapePosition)],
            position: BandShapePosition
        ));

        Assert.True(condition: IsInside(program: program, point: FarBand), userMessage: "the far band did not resolve with the fold centred on the shape's own position.");
        Assert.True(condition: IsInside(program: program, point: CentreBand), userMessage: "the centre band did not resolve with the fold centred on the shape's own position.");
        Assert.True(condition: IsInside(program: program, point: NearBand), userMessage: "the near band did not resolve with the fold centred on the shape's own position.");
    }

    /// <summary>A zero origin is byte-identical to an absent one, on the render chain: the sandwiching translates
    /// cancel exactly (x - 0 = x for every finite x), so the packed program does not merely evaluate the same, it
    /// packs the same words.</summary>
    [Fact]
    public void AZeroOriginRepeatPacksTheSameWordsAsNoOrigin() {
        var withoutOrigin = Emit(shape: SphereAt(
            domain: [new ShapeDomainOp.Repeat(Spacing: BandSpacing, Limit: BandLimit)],
            position: BandShapePosition
        ));
        var withZeroOrigin = Emit(shape: SphereAt(
            domain: [new ShapeDomainOp.Repeat(Spacing: BandSpacing, Limit: BandLimit, Origin: Vector3.Zero)],
            position: BandShapePosition
        ));

        Assert.Equal(
            actual: withZeroOrigin.Words.ToArray(),
            expected: withoutOrigin.Words.ToArray()
        );
    }
    [Fact]
    public void AZeroOriginPolarPacksTheSameWordsAsNoOrigin() {
        var withoutOrigin = Emit(shape: SphereAt(
            domain: [new ShapeDomainOp.Polar(Count: 5)],
            position: new Vector3(x: 4f, y: 0f, z: 0f)
        ));
        var withZeroOrigin = Emit(shape: SphereAt(
            domain: [new ShapeDomainOp.Polar(Count: 5, Origin: Vector3.Zero)],
            position: new Vector3(x: 4f, y: 0f, z: 0f)
        ));

        Assert.Equal(
            actual: withZeroOrigin.Words.ToArray(),
            expected: withoutOrigin.Words.ToArray()
        );
    }

    /// <summary>A Polar op's origin sandwiches its RepeatPolar instruction between a translate to and from that
    /// point, in creation units, matching the sign the render path composes onto the point (Translate subtracts its
    /// offset from the point on entry — see <see cref="SdfOp.Translate"/>'s decoder).</summary>
    [Fact]
    public void AnOriginBearingPolarSandwichesItsFoldBetweenMatchingTranslates() {
        var pivot = new Vector3(x: 1.5f, y: 0f, z: -2f);
        var program = Emit(shape: SphereAt(
            domain: [new ShapeDomainOp.Polar(Count: 5, Origin: pivot)],
            position: (pivot + new Vector3(x: 1f, y: 0f, z: 0f))
        ));
        var instructions = program.Instructions;
        var polarIndex = -1;

        for (var index = 0; (index < instructions.Count); index++) {
            if (instructions[index].Op == SdfOp.RepeatPolar) {
                polarIndex = index;

                break;
            }
        }

        Assert.True(condition: (polarIndex > 0), userMessage: "the program never emits its RepeatPolar instruction.");
        Assert.Equal(expected: SdfOp.Translate, actual: instructions[polarIndex - 1].Op);
        Assert.Equal(expected: SdfOp.Translate, actual: instructions[polarIndex + 1].Op);

        var toOrigin = instructions[polarIndex - 1].Data0;
        var fromOrigin = instructions[polarIndex + 1].Data0;

        Assert.Equal(expected: pivot.X, actual: toOrigin.X, precision: 5);
        Assert.Equal(expected: pivot.Y, actual: toOrigin.Y, precision: 5);
        Assert.Equal(expected: pivot.Z, actual: toOrigin.Z, precision: 5);
        Assert.Equal(expected: -pivot.X, actual: fromOrigin.X, precision: 5);
        Assert.Equal(expected: -pivot.Y, actual: fromOrigin.Y, precision: 5);
        Assert.Equal(expected: -pivot.Z, actual: fromOrigin.Z, precision: 5);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ANonFiniteRepeatOriginIsRefusedByName(float bad) =>
        AssertCanonicalizerRefusesNaming(
            needle: "origin is non-finite",
            shape: SphereAt(
                domain: [new ShapeDomainOp.Repeat(Spacing: Vector3.One, Origin: new Vector3(value: bad))],
                position: Vector3.Zero
            )
        );
    [Fact]
    public void AFiniteRepeatOriginIsAccepted() =>
        AssertCanonicalizerAccepts(shape: SphereAt(
            domain: [new ShapeDomainOp.Repeat(Spacing: Vector3.One, Origin: new Vector3(x: 1f, y: 2f, z: 3f))],
            position: Vector3.Zero
        ));
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ANonFinitePolarOriginIsRefusedByName(float bad) =>
        AssertCanonicalizerRefusesNaming(
            needle: "origin is non-finite",
            shape: SphereAt(
                domain: [new ShapeDomainOp.Polar(Count: 4, Origin: new Vector3(value: bad))],
                position: Vector3.Zero
            )
        );
    [Fact]
    public void AFinitePolarOriginIsAccepted() =>
        AssertCanonicalizerAccepts(shape: SphereAt(
            domain: [new ShapeDomainOp.Polar(Count: 4, Origin: new Vector3(x: 1f, y: 2f, z: 3f))],
            position: Vector3.Zero
        ));

    /// <summary>A polar op's origin term doubles into <see cref="ShapeDomainOps.Reach"/>: the render bound must widen
    /// enough to cover a pivot away from the creation root (see the type's remarks for the triangle-inequality
    /// argument), while a repeat's reach is unaffected — its physical copies sit at the same offsets from the shape's
    /// own position regardless of where cell selection centres.</summary>
    [Fact]
    public void APolarOriginWidensReachByTwiceItsDistanceWhileARepeatOriginDoesNotChangeIt() {
        var pivot = new Vector3(x: 3f, y: 0f, z: 4f);

        Assert.Equal(
            actual: ShapeDomainOps.Reach(domain: [new ShapeDomainOp.Polar(Count: 4, Origin: pivot)]),
            expected: (2f * pivot.Length()),
            tolerance: 1e-5f
        );
        Assert.Equal(
            actual: ShapeDomainOps.Reach(domain: [new ShapeDomainOp.Polar(Count: 4)]),
            expected: 0f,
            tolerance: 1e-5f
        );

        var repeatWithOrigin = ShapeDomainOps.Reach(domain: [new ShapeDomainOp.Repeat(Spacing: BandSpacing, Limit: BandLimit, Origin: pivot)]);
        var repeatWithoutOrigin = ShapeDomainOps.Reach(domain: [new ShapeDomainOp.Repeat(Spacing: BandSpacing, Limit: BandLimit)]);

        Assert.Equal(actual: repeatWithOrigin, expected: repeatWithoutOrigin, tolerance: 1e-5f);
    }
}
