using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// An instance's packed bound composes through the field's set operations (<see cref="SdfBoundAlgebra"/>): an
/// intersection takes the smaller operand bound, so an unbounded lattice a finite shape clips is as finite as that
/// shape; a subtraction keeps its subject's; a union takes the larger, so any unbounded operand makes it unbounded.
/// Each law reads the bound the program packs, and the soundness laws sample the field outside it: past a bound the
/// field is at least the distance to the bound.
/// </summary>
public sealed class SdfBoundAlgebraLawTests {
    private static readonly Vector3 BoxHalfExtents = new(x: 3f, y: 1f, z: 2f);

    // The box's own bound: the sphere through its corners, rounded up.
    private const float BoxBound = 3.75f;
    private const float SphereRadius = 0.5f;

    private static readonly Vector3 Spacing = new(value: 4f);

    private delegate void Emit(SdfProgramBuilder builder, int material);

    private enum Lattice { P6m, Repeat, RepeatLimitedUnbounded }

    // The lattice's own prototype: a sphere repeated without end (or a P6M hex lattice of them).
    private static void Unbounded(SdfProgramBuilder builder, int material, Lattice lattice, SdfBlendOp blend) {
        switch (lattice) {
            case Lattice.P6m:
                _ = builder.WallpaperFold(cell: new Vector2(value: 4f), group: SdfWallpaperGroup.P6M, limit: new Vector2(value: SdfWallpaperFold.UnboundedLimit));
                break;
            case Lattice.Repeat:
                _ = builder.Repeat(spacing: Spacing);
                break;
            default:
                _ = builder.RepeatLimited(spacing: Spacing, limit: new Vector3(value: SdfDomainOps.UnboundedRepeatLimit));
                break;
        }

        _ = builder.Sphere(blend: blend, material: material, radius: SphereRadius);
    }
    private static void Box(SdfProgramBuilder builder, int material, SdfBlendOp blend) {
        _ = builder.Box(blend: blend, halfExtents: BoxHalfExtents, material: material, round: 0f);
    }
    // The scope holds the creation the way a scoped placement emits it; a flat instance holds the bare shapes.
    private static SdfProgram Build(bool instanced, bool scoped, Emit emit) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        if (instanced) {
            _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: BoxBound);
        }
        if (scoped) {
            _ = builder.PushField(compose: SdfBlendOp.Union);
        }

        emit(builder, material);

        if (scoped) {
            _ = builder.PopField();
        }
        if (instanced) {
            _ = builder.EndInstance();
        }

        return builder.Build();
    }
    private static SdfInstanceCost Cost(bool scoped, Emit emit) => Build(emit: emit, instanced: true, scoped: scoped).InspectInstance(index: 0);
    // (a) lattice, then the box intersected with it.
    private static void LatticeIntersectBox(SdfProgramBuilder builder, int material, Lattice lattice) {
        Unbounded(blend: SdfBlendOp.Union, builder: builder, lattice: lattice, material: material);
        _ = builder.ResetPoint();
        Box(blend: SdfBlendOp.Intersection, builder: builder, material: material);
    }
    // (b) lattice, then the box unioned with it.
    private static void LatticeUnionBox(SdfProgramBuilder builder, int material, Lattice lattice) {
        Unbounded(blend: SdfBlendOp.Union, builder: builder, lattice: lattice, material: material);
        _ = builder.ResetPoint();
        Box(blend: SdfBlendOp.Union, builder: builder, material: material);
    }
    // (c) the box, then the lattice subtracted from it.
    private static void BoxSubtractLattice(SdfProgramBuilder builder, int material, Lattice lattice) {
        Box(blend: SdfBlendOp.Union, builder: builder, material: material);
        _ = builder.ResetPoint();
        Unbounded(blend: SdfBlendOp.Subtraction, builder: builder, lattice: lattice, material: material);
    }

    [Fact]
    public void AnUnboundedLatticeIntersectedWithABoxPacksTheBoxsBound() {
        foreach (var lattice in Enum.GetValues<Lattice>()) {
            var cost = Cost(scoped: true, emit: (builder, material) => LatticeIntersectBox(builder: builder, lattice: lattice, material: material));

            Assert.False(condition: cost.Unmaskable, userMessage: $"{lattice} ∩ box");
            Assert.InRange(actual: cost.BoundRadius, low: BoxBound, high: (BoxBound * 1.05f));
        }
    }
    [Fact]
    public void ABoxIntersectedWithAnUnboundedLatticePacksTheBoxsBound() {
        foreach (var lattice in Enum.GetValues<Lattice>()) {
            var cost = Cost(scoped: true, emit: (builder, material) => {
                Box(blend: SdfBlendOp.Union, builder: builder, material: material);
                _ = builder.ResetPoint();
                Unbounded(blend: SdfBlendOp.Intersection, builder: builder, lattice: lattice, material: material);
            });

            Assert.False(condition: cost.Unmaskable, userMessage: $"box ∩ {lattice}");
            Assert.InRange(actual: cost.BoundRadius, low: BoxBound, high: (BoxBound * 1.05f));
        }
    }
    [Fact]
    public void AnUnboundedLatticeUnionedWithABoxIsUncullable() {
        foreach (var lattice in Enum.GetValues<Lattice>()) {
            foreach (var scoped in new[] { true, false }) {
                var cost = Cost(scoped: scoped, emit: (builder, material) => LatticeUnionBox(builder: builder, lattice: lattice, material: material));

                Assert.True(condition: cost.Unmaskable, userMessage: $"{lattice} ∪ box, scoped {scoped}");
                Assert.Equal(expected: SdfProgram.UnmaskableBoundRadius, actual: cost.BoundRadius);
            }
        }
    }
    [Fact]
    public void ABoxMinusAnUnboundedLatticePacksTheBoxsBound() {
        foreach (var lattice in Enum.GetValues<Lattice>()) {
            var cost = Cost(scoped: true, emit: (builder, material) => BoxSubtractLattice(builder: builder, lattice: lattice, material: material));

            Assert.False(condition: cost.Unmaskable, userMessage: $"box − {lattice}");
            Assert.InRange(actual: cost.BoundRadius, low: BoxBound, high: (BoxBound * 1.05f));
        }
    }
    [Fact]
    public void AnUnboundedLatticeMinusABoxKeepsTheLatticesNoBound() {
        foreach (var lattice in Enum.GetValues<Lattice>()) {
            var cost = Cost(scoped: true, emit: (builder, material) => {
                Unbounded(blend: SdfBlendOp.Union, builder: builder, lattice: lattice, material: material);
                _ = builder.ResetPoint();
                Box(blend: SdfBlendOp.Subtraction, builder: builder, material: material);
            });

            Assert.True(condition: cost.Unmaskable, userMessage: $"{lattice} − box");
        }
    }
    [Fact]
    public void AnUnboundedRepeatAloneIsUncullable() {
        foreach (var lattice in new[] { Lattice.Repeat, Lattice.RepeatLimitedUnbounded }) {
            foreach (var scoped in new[] { true, false }) {
                var cost = Cost(scoped: scoped, emit: (builder, material) => Unbounded(blend: SdfBlendOp.Union, builder: builder, lattice: lattice, material: material));

                Assert.True(condition: cost.Unmaskable, userMessage: $"{lattice}, scoped {scoped}");
                Assert.Equal(expected: SdfProgram.UnmaskableBoundRadius, actual: cost.BoundRadius);
            }
        }
    }
    [Fact]
    public void AFiniteRepeatKeepsItsInstanceBoundAndStaysCullable() {
        var cost = Cost(scoped: false, emit: (builder, material) => {
            _ = builder.RepeatLimited(spacing: Spacing, limit: new Vector3(value: 2f));
            _ = builder.Sphere(material: material, radius: SphereRadius);
        });

        Assert.False(condition: cost.Unmaskable);
        Assert.InRange(actual: cost.BoundRadius, low: BoxBound, high: (BoxBound * 1.05f));
    }
    // A flat intersection reads the one global accumulator, so what it intersects is every shape before it in the
    // instance, and the program cannot name a smaller bound for it: it stays unmaskable, scope-free.
    [Fact]
    public void AFlatIntersectionStaysUncullableWhateverItsOperandsBounds() {
        var cost = Cost(scoped: false, emit: (builder, material) => {
            Box(blend: SdfBlendOp.Union, builder: builder, material: material);
            _ = builder.ResetPoint();
            _ = builder.Sphere(blend: SdfBlendOp.Intersection, material: material, radius: SphereRadius);
        });

        Assert.True(condition: cost.Unmaskable);
    }
    [Fact]
    public void ABoundedScopeIntersectionComposesTheSmallerBound() {
        // Two finite shapes: the scope's bound is the smaller instance bound's own, never wider than the authored one.
        var cost = Cost(scoped: true, emit: (builder, material) => {
            Box(blend: SdfBlendOp.Union, builder: builder, material: material);
            _ = builder.ResetPoint();
            _ = builder.Sphere(blend: SdfBlendOp.Intersection, material: material, radius: SphereRadius);
        });

        Assert.False(condition: cost.Unmaskable);
        Assert.InRange(actual: cost.BoundRadius, low: BoxBound, high: (BoxBound * 1.05f));
    }
    // An instance begins under the point state the stream left it (BeginInstance emits no reset), so a fold with no edge
    // opened before the instance and not reset since leaves its shapes at every distance: the witness holds no wallpaper
    // instruction of its own and must still pack the unmaskable bound, where its authored radius would drop the surface
    // at (30, 1, 0) from a camera at (30, 2, 0).
    [Fact]
    public void AnInstanceBeginningUnderAnUnboundedFoldIsUncullable() {
        foreach (var lattice in Enum.GetValues<Lattice>()) {
            foreach (var scoped in new[] { true, false }) {
                var cost = Inherited(lattice: lattice, resetBetween: false, resetInside: false, scoped: scoped);

                Assert.True(condition: cost.Unmaskable, userMessage: $"{lattice} opened before the instance, scoped {scoped}");
                Assert.Equal(expected: SdfProgram.UnmaskableBoundRadius, actual: cost.BoundRadius);
            }
        }
    }
    [Fact]
    public void AResetBeforeOrInsideTheInstanceEndsTheInheritedFold() {
        foreach (var lattice in Enum.GetValues<Lattice>()) {
            foreach (var scoped in new[] { true, false }) {
                Assert.False(condition: Inherited(lattice: lattice, resetBetween: true, resetInside: false, scoped: scoped).Unmaskable, userMessage: $"{lattice} reset before the instance, scoped {scoped}");
                Assert.False(condition: Inherited(lattice: lattice, resetBetween: false, resetInside: true, scoped: scoped).Unmaskable, userMessage: $"{lattice} reset inside the instance, scoped {scoped}");
            }
        }
    }
    [Fact]
    public void AFoldOpenedInAnEarlierInstanceReachesTheNextOne() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 1f);
        _ = builder.ResetPoint();
        Fold(builder: builder, lattice: Lattice.P6m);
        _ = builder.EndInstance();
        _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 1f);
        _ = builder.Sphere(material: material, radius: 1f);
        _ = builder.EndInstance();

        var program = builder.Build();

        Assert.True(condition: program.InspectInstance(index: 1).Unmaskable);
    }
    // The authored radius SdfBoundAlgebra.Unbounded is the declaration that nothing bounds the instance, wherever the tree
    // it covers is bounded; no other non-finite or negative radius is admitted.
    [Fact]
    public void AnUnboundedAuthoredRadiusPacksTheUnmaskableBound() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        Assert.Null(@object: Record.Exception(testCode: () => builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: SdfBoundAlgebra.Unbounded)));

        _ = builder.Sphere(material: material, radius: 1f);
        _ = builder.EndInstance();

        var cost = builder.Build().InspectInstance(index: 0);

        Assert.True(condition: cost.Unmaskable);
        Assert.Equal(expected: SdfProgram.UnmaskableBoundRadius, actual: cost.BoundRadius);

        foreach (var refused in new[] { float.NaN, float.NegativeInfinity, -1f }) {
            _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgramBuilder().BeginInstance(boundCenter: Vector3.Zero, boundRadius: refused));
        }
    }

    private static void Fold(SdfProgramBuilder builder, Lattice lattice) {
        switch (lattice) {
            case Lattice.P6m:
                _ = builder.WallpaperFold(cell: new Vector2(value: 0.001f), group: SdfWallpaperGroup.P6M, limit: new Vector2(value: SdfWallpaperFold.UnboundedLimit));
                break;
            case Lattice.Repeat:
                _ = builder.Repeat(spacing: Spacing);
                break;
            default:
                _ = builder.RepeatLimited(spacing: Spacing, limit: new Vector3(value: SdfDomainOps.UnboundedRepeatLimit));
                break;
        }
    }
    // The fold opens in the world stream, the instance begins after it and holds only a unit sphere.
    private static SdfInstanceCost Inherited(Lattice lattice, bool scoped, bool resetBetween, bool resetInside) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint();
        Fold(builder: builder, lattice: lattice);

        if (resetBetween) {
            _ = builder.ResetPoint();
        }

        _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 1f);

        if (scoped) {
            _ = builder.PushField(compose: SdfBlendOp.Union);
        }
        if (resetInside) {
            _ = builder.ResetPoint();
        }

        _ = builder.Sphere(material: material, radius: 1f);

        if (scoped) {
            _ = builder.PopField();
        }

        _ = builder.EndInstance();

        return builder.Build().InspectInstance(index: 0);
    }

    [Fact]
    public void TheComposeIsTheSmallerForAnIntersectionTheSubjectForASubtractionAndTheLargerOtherwise() {
        Assert.Equal(expected: 2f, actual: SdfBoundAlgebra.Compose(accumulated: 5f, blend: SdfBlendOp.Intersection, operand: 2f));
        Assert.Equal(expected: 2f, actual: SdfBoundAlgebra.Compose(accumulated: 2f, blend: SdfBlendOp.SmoothIntersection, operand: 5f));
        Assert.Equal(expected: 5f, actual: SdfBoundAlgebra.Compose(accumulated: 5f, blend: SdfBlendOp.Subtraction, operand: SdfBoundAlgebra.Unbounded));
        Assert.Equal(expected: 5f, actual: SdfBoundAlgebra.Compose(accumulated: 5f, blend: SdfBlendOp.Union, operand: 2f));
        Assert.Equal(expected: SdfBoundAlgebra.Unbounded, actual: SdfBoundAlgebra.Compose(accumulated: 5f, blend: SdfBlendOp.Union, operand: SdfBoundAlgebra.Unbounded));
        Assert.Equal(expected: 5f, actual: SdfBoundAlgebra.Compose(accumulated: SdfBoundAlgebra.Unbounded, blend: SdfBlendOp.Intersection, operand: 5f));
    }

    // Soundness. The field outside a bound is at least the distance to it: every sampled point past the packed bound
    // reads a field no smaller than its distance to the bound's sphere, less rounding. The instance supplies the bound; a
    // bare twin of the same program supplies the field, since an instance's own evaluator culls by that bound.
    private const float Tolerance = 0.01f;

    [Fact]
    public void TheFieldPastAPackedBoundIsAtLeastTheDistanceToIt() {
        var cases = new (string Name, Emit Emit, bool Scoped)[] {
            ("repeat ∩ box", (builder, material) => LatticeIntersectBox(builder: builder, lattice: Lattice.Repeat, material: material), true),
            ("box ∩ repeat", (builder, material) => {
                Box(blend: SdfBlendOp.Union, builder: builder, material: material);
                _ = builder.ResetPoint();
                Unbounded(blend: SdfBlendOp.Intersection, builder: builder, lattice: Lattice.Repeat, material: material);
            }, true),
            ("box − repeat", (builder, material) => BoxSubtractLattice(builder: builder, lattice: Lattice.Repeat, material: material), true),
            ("box − limited repeat", (builder, material) => BoxSubtractLattice(builder: builder, lattice: Lattice.RepeatLimitedUnbounded, material: material), true),
            // Cases whose composition has no bound: nothing to sample, and a bound packed for one is the defect.
            ("repeat ∪ box", (builder, material) => LatticeUnionBox(builder: builder, lattice: Lattice.Repeat, material: material), true),
            ("repeat − box", (builder, material) => {
                Unbounded(blend: SdfBlendOp.Union, builder: builder, lattice: Lattice.Repeat, material: material);
                _ = builder.ResetPoint();
                Box(blend: SdfBlendOp.Subtraction, builder: builder, material: material);
            }, true),
        };
        var random = new Random(Seed: 2051);
        var bounded = 0;
        var sampled = 0;

        foreach (var (name, emit, scoped) in cases) {
            var cost = Cost(emit: emit, scoped: scoped);

            if (cost.Unmaskable) {
                continue;
            }

            bounded++;

            var evaluator = new SdfFieldEvaluator(program: Build(emit: emit, instanced: false, scoped: scoped));

            for (var sample = 0; (sample < 400); sample++) {
                var point = new Vector3(
                    x: Axis(random: random),
                    y: Axis(random: random),
                    z: Axis(random: random)
                );
                var past = (point.Length() - cost.BoundRadius);

                if (past <= 0f) {
                    continue;
                }

                Assert.True(condition: evaluator.TryDistance(position: Position(point: point), distance: out var field, material: out _), userMessage: $"{name} at {point}");
                Assert.True(
                    condition: (((float)field) >= (past - Tolerance)),
                    userMessage: $"{name} at {point}: field {((float)field)} is under its distance {past} to the bound {cost.BoundRadius}"
                );
                sampled++;
            }
        }

        Assert.Equal(actual: bounded, expected: 4);
        Assert.True(condition: (sampled > 400), userMessage: $"only {sampled} samples fell past a bound");
    }
    // The P6M cases cannot run the fixed-point evaluator, which has no fold: their field is the folded sphere's
    // distance against the box's, composed as the blend says, from SdfWallpaperFold itself.
    [Fact]
    public void TheWallpaperFieldPastAPackedBoundIsAtLeastTheDistanceToIt() {
        var cell = new Vector2(value: 4f);
        var limit = new Vector2(value: SdfWallpaperFold.UnboundedLimit);
        var random = new Random(Seed: 2052);
        var sampled = 0;

        float Wallpaper(Vector3 point) {
            var folded = SdfWallpaperFold.Fold(cell: cell, cellIndex: out _, group: SdfWallpaperGroup.P6M, limit: limit, point: new Vector2(x: point.X, y: point.Z));

            return (new Vector3(x: folded.X, y: point.Y, z: folded.Y).Length() - SphereRadius);
        }
        float BoxField(Vector3 point) {
            var q = (Vector3.Abs(value: point) - BoxHalfExtents);

            return (Vector3.Max(value1: q, value2: Vector3.Zero).Length() + MathF.Min(x: MathF.Max(x: q.X, y: MathF.Max(x: q.Y, y: q.Z)), y: 0f));
        }

        var cases = new (string Name, Emit Emit, Func<Vector3, float> Field)[] {
            ("p6m ∩ box", (builder, material) => LatticeIntersectBox(builder: builder, lattice: Lattice.P6m, material: material), (point) => MathF.Max(x: Wallpaper(point: point), y: BoxField(point: point))),
            ("box − p6m", (builder, material) => BoxSubtractLattice(builder: builder, lattice: Lattice.P6m, material: material), (point) => MathF.Max(x: BoxField(point: point), y: -Wallpaper(point: point))),
            ("p6m ∪ box", (builder, material) => LatticeUnionBox(builder: builder, lattice: Lattice.P6m, material: material), (point) => MathF.Min(x: Wallpaper(point: point), y: BoxField(point: point))),
        };
        var bounded = 0;

        foreach (var (name, emit, field) in cases) {
            var cost = Cost(emit: emit, scoped: true);

            if (cost.Unmaskable) {
                continue;
            }

            bounded++;

            for (var sample = 0; (sample < 400); sample++) {
                var point = new Vector3(
                    x: Axis(random: random),
                    y: Axis(random: random),
                    z: Axis(random: random)
                );
                var past = (point.Length() - cost.BoundRadius);

                if (past <= 0f) {
                    continue;
                }

                Assert.True(
                    condition: (field(point) >= (past - Tolerance)),
                    userMessage: $"{name} at {point}: field {field(point)} is under its distance {past} to the bound {cost.BoundRadius}"
                );
                sampled++;
            }
        }

        Assert.Equal(actual: bounded, expected: 2);
        Assert.True(condition: (sampled > 200), userMessage: $"only {sampled} samples fell past a bound");
    }

    private static float Axis(Random random) => ((((float)random.NextDouble()) * 24f) - 12f);
    private static FixedPosition Position(Vector3 point) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: point.X),
            Y: FixedQ4816.FromDouble(value: point.Y),
            Z: FixedQ4816.FromDouble(value: point.Z)
        ));
}
