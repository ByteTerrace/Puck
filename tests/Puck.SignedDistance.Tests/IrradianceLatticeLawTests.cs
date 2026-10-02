using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

// The cache's layout: keys and bricks, the corner pairs, the direction strata and their exact orientations, the
// octahedral maps and their borders, the accept rule, and the classification's soundness against a crease.
public sealed class IrradianceLatticeLawTests {
    private static readonly IrradianceLevel Room = new(Name: "room", Radius: 0.0, Reach: 9.0, Spacing: 1.5, Strata: 2);

    [Fact]
    public void BricksHoldTheirProbesAndTileTheLevel() {
        foreach (var brick in new IrradianceBrickKey[] { new(Level: 1, X: 0, Y: 0, Z: 0), new(Level: 1, X: -1, Y: 2, Z: -3) }) {
            var probes = IrradianceLattice.ProbesOf(brick: brick).ToList();

            Assert.Equal(expected: 64, actual: probes.Count);
            Assert.Equal(expected: probes.Count, actual: probes.Distinct().Count());
            Assert.All(collection: probes, action: probe => Assert.Equal(expected: brick, actual: IrradianceLattice.BrickOf(key: probe)));
        }
    }
    [Fact]
    public void ACellHasEveryCornerPairOnce() {
        var pairs = Enumerable.Range(count: IrradianceLattice.CellSegments, start: 0)
            .Select(selector: static segment => IrradianceLattice.Segment(segment: segment))
            .ToList();

        Assert.Equal(expected: 28, actual: pairs.Distinct().Count());
        Assert.All(collection: pairs, action: static pair => Assert.True(condition: (pair.First < pair.Second)));
    }
    [Fact]
    public void EachStratumSpansTheSphereAndOrientationsAreExact() {
        var key = new IrradianceProbeKey(Level: 0, X: 3, Y: -2, Z: 7);
        var all = Enumerable.Range(start: 0, count: Room.Rays).Select(selector: ray => IrradianceLattice.Direction(key: key, level: Room, ray: ray)).ToList();

        // Pinned: the first base direction of 128 is fixed by the formula, whatever the machine.
        var first = IrradianceLattice.BaseDirection(count: 128, index: 0);

        Assert.Equal(expected: (1.0 - (1.0 / 128.0)), actual: first.Y, precision: 15);

        for (var stratum = 0; (stratum < Room.Strata); stratum++) {
            var rays = Enumerable.Range(start: 0, count: Room.Rays).Where(predicate: ray => (IrradianceLattice.StratumOf(level: Room, ray: ray) == stratum)).ToList();

            Assert.Equal(expected: IrradianceLattice.RaysPerStratum, actual: rays.Count);

            // Every direction on a coarse test set lies within 0.45 rad of one of the stratum's rays.
            for (var test = 0; (test < 500); test++) {
                var direction = IrradianceLattice.BaseDirection(count: 500, index: test);
                var best = rays.Max(selector: ray => Double3.Dot(a: direction, b: all[ray]));

                Assert.True(condition: (Math.Acos(d: Math.Min(val1: 1.0, val2: best)) < 0.45), userMessage: $"stratum {stratum} leaves a gap");
            }
        }

        for (var orientation = 0; (orientation < IrradianceLattice.Orientations); orientation++) {
            var oriented = IrradianceLattice.Orient(direction: first, orientation: orientation);
            var magnitudes = new[] { Math.Abs(value: oriented.X), Math.Abs(value: oriented.Y), Math.Abs(value: oriented.Z) }.Order();

            Assert.Equal(expected: new[] { Math.Abs(value: first.X), Math.Abs(value: first.Y), Math.Abs(value: first.Z) }.Order(), actual: magnitudes);
        }
    }
    [Fact]
    public void OctahedralMapsRoundTripAndBordersCopyAcrossTheFold() {
        for (var y = 0; (y < IrradianceLattice.InteriorTexels); y++) {
            for (var x = 0; (x < IrradianceLattice.InteriorTexels); x++) {
                var direction = IrradianceLattice.TexelDirection(texelX: x, texelY: y);

                var (u, v) = IrradianceLattice.Encode(direction: direction);

                Assert.Equal(actual: u, expected: ((x + 0.5) / IrradianceLattice.InteriorTexels), precision: 12);
                Assert.Equal(actual: v, expected: ((y + 0.5) / IrradianceLattice.InteriorTexels), precision: 12);
            }
        }

        // A border texel holds the interior texel whose direction neighbours its own position across the map's edge.
        var texel = (1.0 / IrradianceLattice.InteriorTexels);

        for (var y = 0; (y < IrradianceLattice.BorderedTexels); y++) {
            for (var x = 0; (x < IrradianceLattice.BorderedTexels); x++) {
                var (sourceX, sourceY) = IrradianceLattice.BorderSource(borderedX: x, borderedY: y);
                var u = (((x - 1) + 0.5) * texel);
                var v = (((y - 1) + 0.5) * texel);
                var folded = Fold(u: u, v: v);
                var expected = IrradianceLattice.Decode(u: folded.U, v: folded.V);
                var actual = IrradianceLattice.TexelDirection(texelX: sourceX, texelY: sourceY);

                Assert.True(condition: (Double3.Dot(a: expected, b: actual) > 0.95), userMessage: $"border ({x}, {y})");
            }
        }
    }
    [Fact]
    public void AHitIsAcceptedOnlyWithinTheSurfaceEpsilon() {
        // A sample 0.1 from a wall is never a hit, however far the ray has come: acceptance has no travel term.
        Assert.False(condition: IrradianceAcceptance.Accepts(clampedDistance: 0.1));
        Assert.True(condition: IrradianceAcceptance.Accepts(clampedDistance: 0.0009));

        // And a real ray grazing 0.1 above a 200-unit wall stores no hit: it marches on and, its step budget spent long
        // before the wall ends, is unresolved.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: System.Numerics.Vector3.One));

        _ = builder.Translate(offset: new System.Numerics.Vector3(x: 100f, y: -0.5f, z: 0f));
        _ = builder.Box(halfExtents: new System.Numerics.Vector3(x: 100f, y: 0.5f, z: 5f), material: material, round: 0f);

        var field = new IrradianceField(program: builder.Build());
        var ray = field.Cast(direction: new Double3(X: 1.0, Y: 0.0, Z: 0.0), maxDistance: 150.0, origin: new Double3(X: 0.0, Y: 0.1, Z: 0.0));

        Assert.Equal(expected: IrradianceRayKind.Unresolved, actual: ray.Kind);

        // Red leg: a travel-scaled rule, max(0.001, 0.004 t), stops the same ray on empty space from t = 25 on.
        static bool TravelScaled(double clampedDistance, double travelled) => (clampedDistance <= Math.Max(val1: 0.001, val2: (0.004 * travelled)));

        Assert.True(condition: TravelScaled(clampedDistance: 0.1, travelled: 25.0));
    }
    [Fact]
    public void ALaunchNeverStepsOverAThinSlab() {
        // A floor at y = 0 under a slab spanning 0.0012 to 0.0018, inside the interval an unchecked offset skips.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: System.Numerics.Vector3.One));

        _ = builder.Plane(material: material, normal: System.Numerics.Vector3.UnitY, offset: 0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new System.Numerics.Vector3(x: 0f, y: 0.0015f, z: 0f));
        _ = builder.Box(halfExtents: new System.Numerics.Vector3(x: 1f, y: 0.0003f, z: 1f), material: material, round: 0f);

        var field = new IrradianceField(program: builder.Build());
        var up = new Double3(X: 0.0, Y: 1.0, Z: 0.0);
        var surfaces = IrradianceScenes.Uniform(albedo: 0.0, emission: 0.0, sky: 1.0);

        Assert.Null(@object: IrradianceCells.Launch(field: field, height: 0.075, normal: up, surface: Double3.Zero));
        Assert.Equal(expected: 0.0, actual: new IrradianceReference(exitDistance: 50.0, field: field, surfaces: surfaces).Estimate(bounces: 0, normal: up, paths: 64, point: Double3.Zero).Irradiance.X);

        // Beside the slab the launch is certified to its full height, with its clearance bound.
        var open = IrradianceCells.Launch(field: field, height: 0.075, normal: up, surface: new Double3(X: 3.0, Y: 0.0, Z: 0.0));

        Assert.NotNull(@object: open);
        Assert.Equal(expected: 0.075, actual: open.Value.Point.Y, precision: 9);
        Assert.True(condition: (open.Value.Clearance > 0.0));

        // Red leg: an unchecked offset (the reference's own launch height of 0.004) starts above the slab and sees the sky.
        Assert.Equal(expected: IrradianceRayKind.Miss, actual: field.Cast(direction: up, maxDistance: 40.0, origin: new Double3(X: 0.0, Y: 0.004, Z: 0.0)).Kind);
    }
    [Fact]
    public void ClassificationNeverDropsAProbeASurfaceCellNeeds() {
        // A small ball at the far corner of the cell [0, 1.5] cubed: its surface lies in that cell, 2.4 units from the
        // cell's least corner, which no dormant test may then drop.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: System.Numerics.Vector3.One));

        _ = builder.Translate(offset: new System.Numerics.Vector3(x: 1.45f, y: 1.45f, z: 1.45f));
        _ = builder.Sphere(material: material, radius: 0.1f);

        var field = new IrradianceField(program: builder.Build());
        var cell = new IrradianceProbeKey(Level: 0, X: 0, Y: 0, Z: 0);

        for (var corner = 0; (corner < IrradianceLattice.CellCorners); corner++) {
            var lattice = IrradianceLattice.Position(key: IrradianceLattice.Corner(cell: cell, corner: corner), level: Room);

            Assert.NotEqual(expected: IrradianceProbeClass.Dormant, actual: IrradianceCells.Place(field: field, lattice: lattice, spacing: Room.Spacing).Class);
        }

        // Red leg: a dormant threshold of one spacing (plus the relocation allowance) drops the least corner.
        _ = field.TryClampedDistance(distance: out var distance, material: out _, point: Double3.Zero);

        Assert.True(condition: (distance >= (Room.Spacing * (1.0 + IrradianceCells.RelocationAllowance))));
    }

    // The octahedral map's edge identification: a point just past an edge is the point mirrored across that edge's
    // midpoint, so (u, v) past u = 0 is (-u, 1 - v), and likewise for the other edges.
    private static (double U, double V) Fold(double u, double v) {
        if (u < 0.0) {
            u = -u;
            v = (1.0 - v);
        } else if (u > 1.0) {
            u = (2.0 - u);
            v = (1.0 - v);
        }

        if (v < 0.0) {
            v = -v;
            u = (1.0 - u);
        } else if (v > 1.0) {
            v = (2.0 - v);
            u = (1.0 - u);
        }

        return (u, v);
    }
}
