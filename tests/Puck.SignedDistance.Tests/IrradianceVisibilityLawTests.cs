using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

// The cell partition's wall guarantee: a sealed room beside a bright exterior stays exactly dark inside, through planar
// and curved walls 0.05 m thick, wherever the walls fall in the 1.5 m lattice, at the wall's foot, on its face and in
// its corners, while its outside stays lit. Under a table the floor reads only the probes beneath it, and a doorway lets
// light in. Each red leg turns the partition off and shows the same receivers lit through the wall.
public sealed class IrradianceVisibilityLawTests {
    private const double Dark = 1.0e-12;
    private const double Thickness = 0.05;

    private static readonly IrradianceLevel Room = new(Name: "room", Radius: 0.0, Reach: 0.0, Spacing: 1.5, Strata: 2);
    private static readonly Double3 Up = new(X: 0.0, Y: 1.0, Z: 0.0);
    private static readonly Double3 Half = new(X: 2.0, Y: 1.5, Z: 2.0);

    public static TheoryData<double> Offsets => [0.0, 0.3, 0.75, 1.1];

    [MemberData(nameof(Offsets))]
    [Theory]
    public void APlanarSealedRoomStaysDarkInside(double offset) {
        var center = new Double3(X: offset, Y: (1.5 + (offset * 0.5)), Z: (offset * 0.7));
        var field = IrradianceScenes.Room(center: center, interiorHalf: Half, thickness: Thickness);
        var inside = new (Double3 Point, Double3 Normal)[] {
            ((center + new Double3(X: -1.99, Y: -1.5, Z: 0.3)), Up),
            ((center + new Double3(X: 0.2, Y: -1.5, Z: -0.4)), Up),
            ((center + new Double3(X: -2.0, Y: 0.1, Z: 0.7)), new Double3(X: 1.0, Y: 0.0, Z: 0.0)),
            ((center + new Double3(X: 0.5, Y: 1.5, Z: 0.5)), new Double3(X: 0.0, Y: -1.0, Z: 0.0)),
            ((center + new Double3(X: 2.0, Y: -1.4, Z: 1.9)), new Double3(X: -1.0, Y: 0.0, Z: 0.0)),
        };
        var outside = ((center + new Double3(X: -2.05, Y: 0.0, Z: 0.0)), new Double3(X: -1.0, Y: 0.0, Z: 0.0));

        HoldsDarkInside(center: center, field: field, inside: inside, outside: outside);
    }
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(0.9)]
    [Theory]
    public void ACurvedSealedRoomStaysDarkInside(double offset) {
        var center = new Double3(X: offset, Y: (offset * 0.3), Z: -offset);
        var field = IrradianceScenes.SphereRoom(center: center, radius: 2.0, thickness: Thickness);
        Double3[] directions = [new(X: 0.0, Y: -1.0, Z: 0.0), new(X: 1.0, Y: 0.2, Z: 0.0), new(X: -0.6, Y: 0.6, Z: 0.5)];
        var inside = directions.Select(selector: direction => (Point: (center + (direction.Normalize() * 2.0)), Normal: -direction.Normalize())).ToArray();
        var outside = ((center + new Double3(X: 2.05, Y: 0.0, Z: 0.0)), new Double3(X: 1.0, Y: 0.0, Z: 0.0));

        HoldsDarkInside(center: center, field: field, inside: inside, outside: outside);
    }
    [Fact]
    public void ASealedPocketBetweenSubSamplesReadsNoExteriorCorner() {
        // The shell fits inside one cell. Its void misses all former 27 pocket samples: the closest sample,
        // (0.25, 0.25, 0.25), lies in the shell, not the void. All eight exterior corners still form one component.
        var center = new Double3(X: 0.3, Y: 0.3, Z: 0.3);
        var field = IrradianceScenes.SphereRoom(center: center, radius: 0.08, thickness: 0.02);
        var cell = new IrradianceProbeKey(Level: 0, X: 0, Y: 0, Z: 0);
        var corners = Enumerable.Range(count: IrradianceLattice.CellCorners, start: 0)
            .Select(selector: corner => IrradianceCells.Place(
                field: field,
                lattice: IrradianceLattice.Position(key: IrradianceLattice.Corner(cell: cell, corner: corner), level: Room),
                spacing: Room.Spacing
            ))
            .ToArray();
        var partition = IrradianceCells.Partition(corners: corners, field: field, spacing: Room.Spacing);

        Assert.Equal(expected: 1, actual: partition.ComponentCount);
        Assert.All(collection: corners, action: static corner => Assert.True(condition: corner.IsLit));
        Assert.Equal(expected: 0, actual: IrradianceCells.ReadableCorners(corners: corners, field: field, partition: partition, point: center));
        // A point outside the same enclosure reaches the same component, so rejecting every receiver cannot pass.
        Assert.Equal(expected: 255, actual: IrradianceCells.ReadableCorners(corners: corners, field: field, partition: partition, point: new Double3(X: 0.1, Y: 0.1, Z: 0.1)));
        // Red leg: the old one-component shortcut returns 255 for the enclosed receiver too.
    }
    [Fact]
    public void TheFloorUnderATableReadsOnlyTheProbesBeneathIt() {
        var field = IrradianceScenes.TableHall(halfWidth: 1.6, height: 0.8, thickness: 0.05);
        var surfaces = new IrradianceSurfaces(
            albedo: static material => ((material == 0) ? Double3.Zero : new Double3(X: 0.5, Y: 0.5, Z: 0.5)),
            emission: static material => ((material == 0) ? new Double3(X: 1.0, Y: 1.0, Z: 1.0) : Double3.Zero)
        );
        var under = new Double3(X: 0.3, Y: 0.0, Z: 0.2);
        var open = new Double3(X: 3.5, Y: 0.0, Z: 0.1);

        TableEdgeLaunchStaysOutside(field, surfaces, under, path: 1149);
        TableEdgeLaunchStaysOutside(field, surfaces, under, path: 1443);
        var model = Solved(field: field, max: new Double3(X: 6.5, Y: 6.5, Z: 6.5), min: new Double3(X: -6.5, Y: -6.5, Z: -6.5), partition: true, surfaces: surfaces);
        var leaky = Solved(field: field, max: new Double3(X: 6.5, Y: 6.5, Z: 6.5), min: new Double3(X: -6.5, Y: -6.5, Z: -6.5), partition: false, surfaces: surfaces);
        var reference = new IrradianceReference(exitDistance: 60.0, field: field, surfaces: surfaces).Estimate(bounces: 2, normal: Up, paths: 2048, point: under);

        var mask = model.ReadableCorners(level: 0, normal: Up, surface: under);
        var cell = IrradianceLattice.CellOf(level: Room, levelIndex: 0, point: IrradianceCells.Launch(field: field, height: (IrradianceCells.ReceiverBias * Room.Spacing), normal: Up, surface: under)!.Value.Point);

        Assert.True(condition: (mask > 0));

        for (var corner = 0; (corner < IrradianceLattice.CellCorners); corner++) {
            Assert.True(condition: model.TryGetProbe(key: IrradianceLattice.Corner(cell: cell, corner: corner), placement: out var placement));

            if ((mask & (1 << corner)) != 0) {
                Assert.True(condition: (placement.Position.Y < 0.8), userMessage: $"corner {corner} above the table is read");
            }
        }

        var shaded = model.Irradiance(normal: Up, surface: under)!.Value.X;
        var lit = model.Irradiance(normal: Up, surface: open)!.Value.X;

        Assert.Equal(expected: 0, actual: reference.Unresolved);
        Assert.True(condition: (shaded < (0.5 * lit)));
        Assert.InRange(actual: shaded, high: (reference.Irradiance.X + 0.05), low: (reference.Irradiance.X - 0.05));

        // Red leg: with no partition the corners above the table are read and the floor beneath it brightens.
        Assert.True(condition: ((leaky.Irradiance(normal: Up, surface: under)!.Value.X - shaded) > 0.05));
    }
    [Fact]
    public void ADoorwayLetsLightIn() {
        var center = new Double3(X: 0.4, Y: 1.5, Z: 0.3);
        var field = IrradianceScenes.Room(center: center, doorHeight: 2.0, doorWidth: 1.0, interiorHalf: Half, thickness: Thickness);
        var surfaces = IrradianceScenes.Uniform(albedo: 0.5, emission: 0.0, sky: 1.0);
        var reference = new IrradianceReference(exitDistance: 60.0, field: field, surfaces: surfaces);
        var model = Solved(field: field, max: (center + new Double3(X: 5.0, Y: 5.0, Z: 5.0)), min: (center - new Double3(X: 5.0, Y: 5.0, Z: 5.0)), partition: true, surfaces: surfaces);
        var leaky = Solved(field: field, max: (center + new Double3(X: 5.0, Y: 5.0, Z: 5.0)), min: (center - new Double3(X: 5.0, Y: 5.0, Z: 5.0)), partition: false, surfaces: surfaces);
        Double3[] receivers = [
            (center + new Double3(X: -1.95, Y: -1.5, Z: 0.9)),
            (center + new Double3(X: -1.95, Y: -1.5, Z: -1.6)),
            (center + new Double3(X: 0.5, Y: -1.5, Z: 0.0)),
            (center + new Double3(X: 1.5, Y: -1.5, Z: 1.5)),
        ];
        var worstLeak = 0.0;

        foreach (var receiver in receivers) {
            var expected = reference.Estimate(bounces: 2, normal: Up, paths: 2048, point: receiver).Irradiance.X;
            var actual = model.Irradiance(normal: Up, surface: receiver)!.Value.X;

            // The opening is not sealed: light enters, and every receiver stays within 0.06 of the reference, against an
            // exterior of 1. The largest error is just behind the door's frame, where probes in the opening's view are
            // interpolated to a floor point that sees the opening only at grazing angles.
            Assert.True(condition: (actual > 0.0));
            Assert.InRange(actual: actual, high: (expected + 0.06), low: (expected - 0.06));
            worstLeak = Math.Max(val1: worstLeak, val2: (leaky.Irradiance(normal: Up, surface: receiver)!.Value.X - expected));
        }

        // Red leg: with no partition the far corner reads probes outside the wall.
        Assert.True(condition: (worstLeak > 0.06));
    }
    [Fact]
    public void EachHitSeesTheLightForItself() {
        // A table top shades the floor beneath it from an overhead sun, whose direct light this fixture marches 2 units
        // up from each lit point, inside the hall's 6-unit shell. The probe under the table stands in its shadow,
        // yet its rays reach both the shaded floor beneath and the sunlit floor beyond the table's edge; each hit is lit
        // by its own visibility toward the sun.
        var field = IrradianceScenes.TableHall(halfWidth: 1.6, height: 0.8, thickness: 0.05);
        var sun = new Double3(X: 0.0, Y: 1.0, Z: 0.0);
        var surfaces = new IrradianceSurfaces(
            albedo: static material => ((material == 0) ? Double3.Zero : new Double3(X: 0.5, Y: 0.5, Z: 0.5)),
            direct: (point, normal, material) => (((material == 1) && (Double3.Dot(a: normal, b: sun) > 0.0) && field.SegmentClear(from: (point + (normal * 0.002)), to: (point + (sun * 2.0))))
                ? new Double3(X: 1.0, Y: 1.0, Z: 1.0)
                : Double3.Zero),
            emission: static _ => Double3.Zero
        );
        var model = Solved(field: field, max: new Double3(X: 6.5, Y: 6.5, Z: 6.5), min: new Double3(X: -6.5, Y: -6.5, Z: -6.5), partition: true, surfaces: surfaces, bounces: 0);
        var probe = new IrradianceProbeKey(Level: 0, X: 0, Y: 0, Z: 0);

        Assert.True(condition: model.TryGetProbe(key: probe, placement: out var placement));
        Assert.True(condition: placement.IsLit);
        Assert.True(condition: (placement.Position.Y < 0.8));

        var hits = model.HitsOf(key: probe);
        var sunlit = 0;
        var shaded = 0;

        for (var ray = 0; (ray < hits.Count); ray++) {
            if ((hits[ray].Kind != IrradianceHitKind.Hit) || (hits[ray].Material != 1) || (hits[ray].Normal.Y < 0.9)) {
                continue;
            }

            var underTable = ((Math.Abs(value: hits[ray].Point.X) < 1.5) && (Math.Abs(value: hits[ray].Point.Z) < 1.5));
            var radiance = model.RadianceOf(key: probe, ray: ray)!.Value.X;

            if (underTable) {
                Assert.Equal(actual: radiance, expected: 0.0, precision: 12);
                shaded++;
            } else if (((Math.Abs(value: hits[ray].Point.X) > 1.7) || (Math.Abs(value: hits[ray].Point.Z) > 1.7)) && (hits[ray].Point.Length < 5.5)) {
                Assert.Equal(actual: radiance, expected: 0.5, precision: 12);
                sunlit++;
            }
        }

        // Red leg: the probe's own visibility toward the sun is blocked by the table, so lighting its hits by it would
        // leave every sunlit floor hit dark.
        Assert.False(condition: field.SegmentClear(from: placement.Position, to: (placement.Position + (sun * 2.0))));
        Assert.True(condition: (sunlit > 0));
        Assert.True(condition: (shaded > 0));
    }

    // These two ordinary Halton paths graze the same table's lower +X edge. The accepted hit's finite-stencil
    // gradient follows the incoming direction, but still points toward positive field values, outside the solid.
    private static void TableEdgeLaunchStaysOutside(IrradianceField field, IrradianceSurfaces surfaces, Double3 point, int path) {
        var first = IrradianceCells.Launch(field: field, height: 0.004, normal: Up, surface: point);

        Assert.NotNull(value: first);
        var direction = IrradianceReference.CosineDirection(normal: Up,
            u: IrradianceReference.RadicalInverse(index: path, primeBase: 2),
            v: IrradianceReference.RadicalInverse(index: path, primeBase: 3));
        var hit = field.Cast(origin: first.Value.Point, direction: direction, maxDistance: 60.0);

        Assert.Equal(IrradianceRayKind.Hit, hit.Kind);
        Assert.True(condition: field.TryGradient(hit.Point, out var gradient));
        Assert.True(condition: (Double3.Dot(a: gradient, b: direction) > 0.0));
        var outward = IrradianceCells.Launch(field: field, height: 0.004, normal: gradient, surface: hit.Point);

        Assert.NotNull(value: outward);
        Assert.True(condition: field.TryClampedDistance(outward.Value.Point, out var clearance, out _));
        Assert.True(condition: (clearance > 0.0));

        // Facing the incoming ray would enter the solid. That failed launch remains unknown, never resolved black.
        Assert.Null(value: IrradianceCells.Launch(field: field, height: 0.004, normal: -gradient, surface: hit.Point));
        var refused = new IrradianceReference(field, surfaces, exitDistance: 60.0)
            .Estimate(point: hit.Point, normal: -gradient, bounces: 1, paths: 1);

        Assert.Equal(1, refused.Unresolved);
    }
    private static void HoldsDarkInside(IrradianceField field, Double3 center, (Double3 Point, Double3 Normal)[] inside, (Double3 Point, Double3 Normal) outside) {
        var surfaces = IrradianceScenes.Uniform(albedo: 0.5, emission: 0.0, sky: 1.0);
        var min = (center - new Double3(X: 5.0, Y: 5.0, Z: 5.0));
        var max = (center + new Double3(X: 5.0, Y: 5.0, Z: 5.0));
        var model = Solved(field: field, max: max, min: min, partition: true, surfaces: surfaces);
        var leaky = Solved(field: field, max: max, min: min, partition: false, surfaces: surfaces);
        var worstLeak = 0.0;

        foreach (var (point, normal) in inside) {
            var irradiance = model.Irradiance(normal: normal, surface: point);

            Assert.NotNull(@object: irradiance);
            Assert.InRange(actual: irradiance.Value.X, high: Dark, low: 0.0);
            worstLeak = Math.Max(val1: worstLeak, val2: leaky.Irradiance(normal: normal, surface: point)!.Value.X);
        }

        Assert.True(condition: (model.Irradiance(normal: outside.Normal, surface: outside.Point)!.Value.X > 0.5));
        Assert.Equal(expected: 0, actual: model.UnresolvedRays);

        // Red leg: the same receivers read probes outside the wall once the partition is off.
        Assert.True(condition: (worstLeak > 0.01));
    }
    private static IrradianceCacheModel Solved(IrradianceField field, Double3 min, Double3 max, bool partition, IrradianceSurfaces surfaces, int bounces = 2) {
        var model = new IrradianceCacheModel(
            field: field,
            levels: [Room],
            options: new IrradianceModelOptions(ExitDistance: 60.0, Partition: partition),
            surfaces: surfaces
        );

        model.Allocate(level: 0, max: max, min: min);
        model.Classify();
        model.Trace();
        model.Solve(bounces: bounces);

        return model;
    }
}
