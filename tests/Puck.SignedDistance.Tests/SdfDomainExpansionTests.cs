using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfDomainExpansionTests {
    // The fixed-point rounding floor the two emission paths differ by: Q48.16 resolves 1/65536.
    private const double Tolerance = 0.001d;

    private static readonly Vector3 BoxHalfExtents = new(
        x: 0.7f,
        y: 0.3f,
        z: 0.5f
    );
    private static readonly Vector3 ShapePosition = new(
        x: 6f,
        y: 3f,
        z: 6f
    );

    private static Quaternion ShapeRotation => Quaternion.Normalize(value: Quaternion.CreateFromAxisAngle(
        angle: 0.7f,
        axis: Vector3.Normalize(value: new Vector3(
            x: 0.3f,
            y: 1f,
            z: 0.2f
        ))
    ));

    // The folded program: the domain ops as point ops, then the shape's own local pose.
    private static SdfProgram Folded(IReadOnlyList<SdfDomainOp> domain, Vector3 position) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfDomainOps.Apply(
            chain: builder.ResetPoint(),
            domain: domain
        )
            .Translate(offset: position)
            .Rotate(rotation: ShapeRotation)
            .Box(
                halfExtents: BoxHalfExtents,
                material: material,
                round: 0f
            );

        return builder.Build();
    }
    // The same content as one segment per expanded copy, with no domain op anywhere.
    private static SdfProgram Expanded(IReadOnlyList<SdfDomainOp> domain, Vector3 position) {
        Assert.True(
            condition: SdfDomainExpansion.TryExpand(
                domain: domain,
                frames: out var frames,
                refusal: out var refusal
            ),
            userMessage: refusal
        );

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var local = new SdfRigidFrame(
            Mirrored: false,
            Position: FixedVector3.FromVector3(value: position),
            Rotation: FixedQuaternion.FromQuaternion(value: ShapeRotation).Normalize()
        );

        foreach (var frame in frames) {
            var placed = frame.Compose(inner: local);

            _ = builder.ResetPoint()
                .Translate(offset: placed.Position.ToVector3())
                .Rotate(rotation: placed.Rotation.ToQuaternion())
                .Box(
                    halfExtents: BoxHalfExtents,
                    material: material,
                    round: 0f
                );
        }

        return builder.Build();
    }
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));
    // Every copy the expansion reports, as the world point the authored shape origin lands on.
    private static List<Vector3> CopyOrigins(IReadOnlyList<SdfDomainOp> domain, Vector3 position) {
        Assert.True(
            condition: SdfDomainExpansion.TryExpand(
                domain: domain,
                frames: out var frames,
                refusal: out var refusal
            ),
            userMessage: refusal
        );

        return [.. frames.Select(selector: frame => frame.TransformPoint(point: FixedVector3.FromVector3(value: position)).ToVector3())];
    }
    private static void AssertContains(List<Vector3> origins, Vector3 expected) {
        Assert.True(
            condition: origins.Any(predicate: origin => ((origin - expected).Length() < 0.001f)),
            userMessage: $"expected a copy near {expected}; got [{string.Join(separator: ", ", values: origins)}]"
        );
    }
    // The expansion is the true union of the copies, so it can only refine the fold: a fold evaluates the copy whose
    // cell/sector/half-space the query rounds into, which for a non-spherical prototype is not always the nearest copy,
    // and that overestimates. Both carry the same zero set, so the sign — the half contact reads — must agree exactly.
    private static void AssertExpansionRefinesTheFold(IReadOnlyList<SdfDomainOp> domain, Vector3 position) {
        var foldedField = new SdfFieldEvaluator(program: Folded(
            domain: domain,
            position: position
        ));
        var expandedField = new SdfFieldEvaluator(program: Expanded(
            domain: domain,
            position: position
        ));
        var worst = 0d;

        for (var x = -9; (x <= 9); x++) {
            for (var y = 0; (y <= 6); y++) {
                for (var z = -9; (z <= 9); z++) {
                    var sample = Position(
                        x: (x * 1.1),
                        y: (y * 1.1),
                        z: (z * 1.1)
                    );

                    Assert.True(condition: foldedField.TryDistance(
                        distance: out var foldedDistance,
                        material: out _,
                        position: sample
                    ));
                    Assert.True(condition: expandedField.TryDistance(
                        distance: out var expandedDistance,
                        material: out _,
                        position: sample
                    ));

                    var folded = ((double)foldedDistance);
                    var expandedValue = ((double)expandedDistance);

                    Assert.True(
                        condition: ((expandedValue - folded) < Tolerance),
                        userMessage: $"the expansion reported {expandedValue} past the fold's {folded} at {sample}"
                    );
                    Assert.True(
                        condition: ((folded < 0d) == (expandedValue < 0d)),
                        userMessage: $"the fold reads {folded} and the expansion {expandedValue} at {sample}"
                    );

                    worst = Math.Max(
                        val1: worst,
                        val2: (folded - expandedValue)
                    );
                }
            }
        }

        Assert.True(
            condition: (worst < 1d),
            userMessage: $"the expansion refined the fold by {worst}, further than a wrong-neighbour gap explains"
        );
    }

    [Fact]
    public void EmptyDomainExpandsToTheIdentityCopy() {
        Assert.True(condition: SdfDomainExpansion.TryExpand(
            domain: null,
            frames: out var frames,
            refusal: out _
        ));
        Assert.Single(collection: frames);
        Assert.Equal(
            actual: frames[0],
            expected: SdfRigidFrame.Identity
        );
    }
    [Fact]
    public void ComposingWithIdentityIsExact() {
        // A document authoring no domain op composes against the identity frame on every contact path, so the identity
        // has to be free: a fixed-point multiply and renormalize would round, and the colliders would shift.
        var frame = new SdfRigidFrame(
            Mirrored: true,
            Position: FixedVector3.FromVector3(value: new Vector3(
                x: 1.25f,
                y: -3.5f,
                z: 7.75f
            )),
            Rotation: FixedQuaternion.FromQuaternion(value: ShapeRotation).Normalize()
        );
        var point = FixedVector3.FromVector3(value: ShapePosition);

        Assert.Equal(
            actual: SdfRigidFrame.Identity.Compose(inner: frame),
            expected: frame
        );
        Assert.Equal(
            actual: frame.Compose(inner: SdfRigidFrame.Identity),
            expected: frame
        );
        Assert.Equal(
            actual: SdfRigidFrame.Identity.TransformPoint(point: point),
            expected: point
        );
    }
    [Fact]
    public void ExpansionIsBitIdenticalAcrossCalls() {
        List<SdfDomainOp> domain = [
            new SdfDomainOp.Symmetry(Normal: Vector3.UnitX),
            new SdfDomainOp.Polar(
                Count: 5,
                Mirror: true
            ),
        ];

        Assert.True(condition: SdfDomainExpansion.TryExpand(
            domain: domain,
            frames: out var first,
            refusal: out _
        ));
        Assert.True(condition: SdfDomainExpansion.TryExpand(
            domain: domain,
            frames: out var second,
            refusal: out _
        ));
        Assert.Equal(
            actual: second,
            expected: first
        );
    }
    [Fact]
    public void PolarSectorsRingTheAxis() {
        var origins = CopyOrigins(
            domain: [new SdfDomainOp.Polar(Count: 4)],
            position: new Vector3(
                x: 6f,
                y: 0f,
                z: 0f
            )
        );

        Assert.Equal(
            actual: origins.Count,
            expected: 4
        );
        AssertContains(
            expected: new Vector3(
                x: 6f,
                y: 0f,
                z: 0f
            ),
            origins: origins
        );
        AssertContains(
            expected: new Vector3(
                x: 0f,
                y: 0f,
                z: 6f
            ),
            origins: origins
        );
        AssertContains(
            expected: new Vector3(
                x: -6f,
                y: 0f,
                z: 0f
            ),
            origins: origins
        );
        AssertContains(
            expected: new Vector3(
                x: 0f,
                y: 0f,
                z: -6f
            ),
            origins: origins
        );
    }
    [Fact]
    public void RepeatExpansionMatchesTheFoldedField() {
        // The prototype sits at the centre cell's middle: a repeat fold is exact only for an on-centre prototype
        // within half a spacing per axis, and one parked on a cell wall is clipped by the fold but whole here.
        AssertExpansionRefinesTheFold(
            domain: [
                new SdfDomainOp.Repeat(
                    Limit: new Vector3(
                        x: 1f,
                        y: 0f,
                        z: 1f
                    ),
                    Spacing: new Vector3(
                        x: 6f,
                        y: 12f,
                        z: 6f
                    )
                ),
            ],
            position: new Vector3(
                x: 0f,
                y: 3f,
                z: 0f
            )
        );
    }
    [Fact]
    public void SymmetryPairPlacesOneCopyPerQuadrant() {
        var origins = CopyOrigins(
            domain: [
                new SdfDomainOp.Symmetry(Normal: Vector3.UnitX),
                new SdfDomainOp.Symmetry(Normal: Vector3.UnitZ),
            ],
            position: ShapePosition
        );

        Assert.Equal(
            actual: origins.Count,
            expected: 4
        );

        foreach (var signX in new[] { 1f, -1f }) {
            foreach (var signZ in new[] { 1f, -1f }) {
                AssertContains(
                    expected: new Vector3(
                        x: (6f * signX),
                        y: 3f,
                        z: (6f * signZ)
                    ),
                    origins: origins
                );
            }
        }
    }
    [Fact]
    public void SymmetryExpansionMatchesTheFoldedField() {
        AssertExpansionRefinesTheFold(
            domain: [
                new SdfDomainOp.Symmetry(Normal: Vector3.UnitX),
                new SdfDomainOp.Symmetry(Normal: Vector3.UnitZ),
            ],
            position: ShapePosition
        );
    }
    [Fact]
    public void UnboundedRepeatLimitRefusesByName() {
        Assert.False(condition: SdfDomainExpansion.TryExpand(
            domain: [
                new SdfDomainOp.Repeat(
                    Limit: new Vector3(value: 1000000f),
                    Spacing: Vector3.One
                ),
            ],
            frames: out var frames,
            refusal: out var refusal
        ));
        Assert.Empty(collection: frames);
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "repeat"
        );
    }
    [Fact]
    public void WallpaperRefusesByName() {
        Assert.False(condition: SdfDomainExpansion.TryExpand(
            domain: [
                new SdfDomainOp.Wallpaper(
                    Cell: Vector2.One,
                    Group: SdfWallpaperGroup.P1,
                    Limit: Vector2.One
                ),
            ],
            frames: out var frames,
            refusal: out var refusal
        ));
        Assert.Empty(collection: frames);
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "wallpaper"
        );
    }
}
