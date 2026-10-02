using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>A CPU reference march of the symmetry-LOD crossing rule, <c>sdfMarchAdvance</c> in
/// <c>field/sdf-map.hlsli</c>, over a field with a different set of solids in every shell between concentric switch
/// spheres, as a wallpaper fold's lattice differs on each side of its switch. Wallpaper has no fixed evaluator, so the
/// reference states the rule's two claims against an exact oracle: a sphere-traced ray never steps past a solid point
/// of the side it lies on (no skip), and it crosses each switch sphere at most twice, once from the camera. The GPU
/// rule itself is held on the device by <c>SdfMarchLodDeviceLawTests</c>.</summary>
public sealed class SdfMarchLodCrossingLawTests {
    private const float Limit = 240;
    private const float None = 1.0e30f;
    private const int Steps = 1024;
    private const float Tolerance = 0.001f;
    // The floor a step once took at a switch, as a fraction of its radius.
    private const float FloorFraction = 1.0e-4f;

    // Switch spheres about the origin at radii 30 and 50, so three shells, each holding solids of its own; only the part
    // of a solid in its own shell exists. A step floored at FloorFraction of the radius strides 0.003 at the inner switch
    // and 0.005 at the outer, past the layers.
    private static readonly float[] Radii = [30, 50];
    private static readonly (Vector3 Center, float Radius)[][] Spheres = [
        [(new Vector3(x: 15, y: 5, z: 0), 4), (new Vector3(x: 0, y: 0, z: 29.997f), 0.004f), (new Vector3(x: -22, y: -18, z: 3), 2.5f)],
        [(new Vector3(x: -35, y: 20, z: 10), 3), (new Vector3(x: 0, y: 0, z: 30.001f), 0.002f), (new Vector3(x: 0, y: 0, z: 40), 0.5f)],
        [(new Vector3(x: -40, y: -40, z: 0), 5), (new Vector3(x: 0, y: 0, z: -50.001f), 0.002f)],
    ];
    // Layers 0.001 thick hugging a switch, each over the half-space its normal faces: just inside the inner switch,
    // just past it and just inside the outer, and just past the outer.
    private static readonly (float Inner, float Outer, Vector3 Normal)[][] Layers = [
        [(29.999f, 30, -Vector3.UnitX)],
        [(30, 30.001f, Vector3.UnitY), (49.999f, 50, Vector3.UnitY)],
        [(50, 50.001f, -Vector3.UnitX)],
    ];

    [Fact]
    public void ACameraRayCrossesEachSwitchOnceAndSkipsNothing() {
        var failures = new List<string>();

        foreach (var direction in Directions(count: 4096)) {
            Check(direction: direction, failures: failures, maximumCrossings: 1, origin: Vector3.Zero);
        }
        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures.Take(count: 20)));
    }
    [Fact]
    public void AnyRayCrossesEachSwitchAtMostTwiceAndSkipsNothing() {
        var failures = new List<string>();
        Vector3[] origins = [new(x: -90, y: 3, z: 1), new(x: 0, y: -80, z: 20), new(x: 60, y: 60, z: -10), new(x: 10, y: 10, z: 10), new(x: 39, y: 0, z: 0)];

        foreach (var origin in origins) {
            foreach (var direction in Directions(count: 2048)) {
                Check(direction: direction, failures: failures, maximumCrossings: 2, origin: origin);
            }
        }
        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures.Take(count: 20)));
    }
    [Fact]
    public void ARayAimedThroughBothWallsCrossesEachTwice() {
        // From outside both spheres, past the center on the side no layer covers: in and out of each, clear of every solid.
        var origin = new Vector3(x: 6, y: -6, z: -90);
        var march = March(direction: Vector3.UnitZ, floorFraction: null, origin: origin);

        Assert.Null(value: FirstSolid(direction: Vector3.UnitZ, origin: origin));
        Assert.Equal(expected: 2, actual: march.Crossings[0]);
        Assert.Equal(expected: 2, actual: march.Crossings[1]);
    }
    [Fact]
    public void AStepFlooredAtTheSwitchSkipsAThinLayerThatTheCrossingHits() {
        // From 0.0005 inside the inner switch along +y, where the inner shell holds nothing ahead: the floored step lands
        // about 0.0025 past the switch, beyond the layer there, while the crossing lands in it.
        var origin = new Vector3(x: 1, y: MathF.Sqrt(x: ((29.9995f * 29.9995f) - 1)), z: 0);
        var truth = FirstSolid(direction: Vector3.UnitY, origin: origin);
        var floored = March(direction: Vector3.UnitY, floorFraction: FloorFraction, origin: origin);
        var crossing = March(direction: Vector3.UnitY, floorFraction: null, origin: origin);

        Assert.NotNull(value: truth);
        Assert.True(condition: (!floored.Found || (floored.Traveled > (truth.Value + Tolerance))), userMessage: $"the floored step found {floored.Traveled}");
        Assert.True(condition: (crossing.Found && (crossing.Traveled <= (truth.Value + Tolerance))), userMessage: $"the crossing found {crossing.Found} at {crossing.Traveled}, the first solid at {truth}");
    }
    [Fact]
    public void TheSweepFailsAStepFlooredAtTheSwitch() {
        var failures = new List<string>();

        foreach (var direction in Directions(count: 4096)) {
            Check(direction: direction, failures: failures, floorFraction: FloorFraction, maximumCrossings: 1, origin: Vector3.Zero);
        }
        Assert.NotEmpty(collection: failures);
    }

    private static void Check(Vector3 origin, Vector3 direction, int maximumCrossings, List<string> failures, float? floorFraction = null) {
        var truth = FirstSolid(direction: direction, origin: origin);
        var march = March(direction: direction, floorFraction: floorFraction, origin: origin);

        for (var index = 0; (index < Radii.Length); index++) {
            if (march.Crossings[index] > maximumCrossings) {
                failures.Add(item: $"{origin} {direction}: {march.Crossings[index]} crossings of the switch at {Radii[index]}");
            }
        }
        // The march accepts within the tolerance of a solid; it may stop at a near miss, never past the first solid.
        if ((truth is { } first) && (!march.Found || (march.Traveled > ((first + Tolerance) + 1.0e-4f)))) {
            failures.Add(item: $"{origin} {direction}: first solid at {first}, the march found {march.Found} at {march.Traveled} after {march.Steps} steps");
        }
    }
    // A plain sphere trace with the crossing rule; with floorFraction set, the step instead takes the switch gap as a
    // bound floored at that fraction of the switch radius and crosses nothing.
    private static (bool Found, float Traveled, int Steps, int[] Crossings) March(Vector3 origin, Vector3 direction, float? floorFraction) {
        var crossings = new int[Radii.Length];
        var traveled = 0f;

        for (var step = 0; (step < Steps); step++) {
            var sample = Sample(position: (origin + (direction * traveled)));

            if (sample.Distance < Tolerance) {
                return (true, traveled, step, crossings);
            }
            if (floorFraction is { } fraction) {
                traveled += MathF.Min(x: sample.Distance, y: MathF.Max(x: sample.Gap, y: (sample.GapRadius * fraction)));
            } else {
                traveled = Advance(advance: sample.Distance, clearance: sample.Distance, crossed: out var crossed, direction: direction, origin: origin, sample: sample,
                    tolerance: (0.5f * Tolerance), traveled: traveled);
                if (crossed >= 0) {
                    crossings[crossed]++;
                }
            }
            if (traveled > Limit) {
                break;
            }
        }

        return (false, traveled, Steps, crossings);
    }
    // The field at a position: the nearest solid of the shell the position lies in, and the shell's walls as mapCore
    // publishes them (a switch the position lies past, r > R, is an inner wall).
    private static (float Distance, float Gap, float GapRadius, float Inner, float Outer) Sample(Vector3 position) {
        var radius = position.Length();
        var shell = 0;
        var gap = None;
        var gapRadius = 0f;
        var inner = 0f;
        var outer = None;

        foreach (var wall in Radii) {
            var wallGap = ((radius > wall) ? (radius - wall) : (wall - radius));

            if (radius > wall) {
                shell++;
                inner = MathF.Max(x: inner, y: wall);
            } else {
                outer = MathF.Min(x: outer, y: wall);
            }
            if (wallGap < gap) {
                gap = wallGap;
                gapRadius = wall;
            }
        }
        var distance = 1.0e9f;

        foreach (var (center, solidRadius) in Spheres[shell]) {
            distance = MathF.Min(x: distance, y: (Vector3.Distance(value1: position, value2: center) - solidRadius));
        }
        // A layer's field bounds its distance from below: the larger of the shell's and the half-space's.
        foreach (var (low, high, normal) in Layers[shell]) {
            var layer = (MathF.Abs(x: (radius - (0.5f * (low + high)))) - (0.5f * (high - low)));

            distance = MathF.Min(x: distance, y: MathF.Max(x: layer, y: -Vector3.Dot(vector1: normal, vector2: position)));
        }

        return (distance, gap, gapRadius, inner, outer);
    }
    // sdfMarchAdvance's steps, in float; `crossed` names the switch a crossing passes, or -1.
    private static float Advance((float Distance, float Gap, float GapRadius, float Inner, float Outer) sample, Vector3 origin, Vector3 direction, float traveled,
        float clearance, float advance, float tolerance, out int crossed) {
        crossed = -1;
        if (MathF.Max(x: clearance, y: advance) <= sample.Gap) {
            return (traveled + advance);
        }
        var along = Vector3.Dot(vector1: origin, vector2: direction);
        var next = None;
        var after = None;
        var wall = 0f;

        if (sample.Outer < None) {
            next = (Roots(along: along, far: out var far, near: out _, offset: origin, radius: sample.Outer) ? MathF.Max(x: far, y: traveled) : traveled);
            wall = sample.Outer;
        }
        if ((sample.Inner > 0) && Roots(along: along, far: out var innerFar, near: out var innerNear, offset: origin, radius: sample.Inner)) {
            var entry = MathF.Max(x: innerNear, y: traveled);

            if ((innerNear < innerFar) && (entry < innerFar) && (entry < next)) {
                next = entry;
                after = innerFar;
                wall = sample.Inner;
            }
        }
        if (!(sample.Gap < clearance) && ((traveled + advance) < next)) {
            return (traveled + advance);
        }
        if ((next < None) && ((next - traveled) <= clearance)) {
            crossed = Array.IndexOf(array: Radii, value: wall);
            var beyond = ((next < Limit) ? MathF.Min(x: tolerance, y: (0.5f * (MathF.Min(x: after, y: Limit) - next))) : tolerance);

            return MathF.Max(x: (next + beyond), y: MathF.BitIncrement(x: MathF.Max(x: next, y: 1.0e-30f)));
        }

        return (traveled + clearance);
    }
    private static bool Roots(Vector3 offset, float along, float radius, out float near, out float far) {
        var c = (Vector3.Dot(vector1: offset, vector2: offset) - (radius * radius));
        var discriminant = ((along * along) - c);

        near = None;
        far = None;
        if (discriminant < 0) {
            return false;
        }
        var root = MathF.Sqrt(x: discriminant);
        var q = -(along + ((along >= 0) ? root : -root));
        var other = ((q != 0) ? (c / q) : 0);

        near = MathF.Min(x: q, y: other);
        far = MathF.Max(x: q, y: other);

        return true;
    }
    // The first ray parameter in [0, Limit] at which the ray stands in a solid of the shell it is in, exactly.
    private static float? FirstSolid(Vector3 origin, Vector3 direction) {
        var o = new Vector3d(value: origin);
        var d = new Vector3d(value: direction);
        var breaks = new List<double> { 0, Limit };

        foreach (var wall in Radii) {
            if (Interval(center: default, direction: d, origin: o, radius: wall) is { } span) {
                breaks.Add(item: span.Near);
                breaks.Add(item: span.Far);
            }
        }
        breaks = [.. breaks.Where(predicate: static value => ((value >= 0) && (value <= Limit))).Order()];
        for (var index = 0; ((index + 1) < breaks.Count); index++) {
            var (start, end) = (breaks[index], breaks[(index + 1)]);

            if (end <= start) {
                continue;
            }
            var middle = o.Plus(other: d.Times(scale: (0.5 * (start + end)))).Length;
            var shell = Radii.Count(predicate: wall => (middle > wall));
            double? first = null;

            var spans = new List<(double Near, double Far)>();

            foreach (var (center, radius) in Spheres[shell]) {
                if (Interval(center: new Vector3d(value: center), direction: d, origin: o, radius: radius) is { } solid) {
                    spans.Add(item: solid);
                }
            }
            foreach (var (inner, outer, normal) in Layers[shell]) {
                spans.AddRange(collection: LayerSpans(direction: d, inner: inner, normal: new Vector3d(value: normal), origin: o, outer: outer));
            }
            foreach (var (near, far) in spans) {
                var entry = Math.Max(val1: near, val2: start);

                if ((entry <= Math.Min(val1: far, val2: end)) && ((first is null) || (entry < first))) {
                    first = entry;
                }
            }
            if (first is { } found) {
                return ((float)found);
            }
        }

        return null;
    }
    private static (double Near, double Far)? Interval(Vector3d origin, Vector3d direction, Vector3d center, double radius) {
        var offset = origin.Minus(other: center);
        var along = offset.Dot(other: direction);
        var discriminant = ((along * along) - (offset.Dot(other: offset) - (radius * radius)));

        return ((discriminant < 0) ? null : ((-along - Math.Sqrt(d: discriminant)), (-along + Math.Sqrt(d: discriminant))));
    }
    // Where the ray runs between a layer's two spheres and on its normal's side.
    private static IEnumerable<(double Near, double Far)> LayerSpans(Vector3d origin, Vector3d direction, double inner, double outer, Vector3d normal) {
        if (Interval(center: default, direction: direction, origin: origin, radius: outer) is not { } shell) {
            yield break;
        }
        var facing = normal.Dot(other: direction);
        var height = normal.Dot(other: origin);

        var (low, high) = ((facing > 0) ? ((-height / facing), double.PositiveInfinity) : ((facing < 0) ? (double.NegativeInfinity, (-height / facing)) : ((height > 0) ? (double.NegativeInfinity, double.PositiveInfinity) : (1, 0))));
        (double Near, double Far)[] pieces = ((Interval(center: default, direction: direction, origin: origin, radius: inner) is { } hole)
            ? [(shell.Near, hole.Near), (hole.Far, shell.Far)]
            : [shell]);

        foreach (var (near, far) in pieces) {
            var (from, to) = (Math.Max(val1: near, val2: low), Math.Min(val1: far, val2: high));

            if (from <= to) {
                yield return (from, to);
            }
        }
    }
    // Unit directions spread evenly over the sphere by the golden-angle spiral.
    private static IEnumerable<Vector3> Directions(int count) {
        var golden = (Math.PI * (3 - Math.Sqrt(d: 5)));

        for (var index = 0; (index < count); index++) {
            var y = (1 - ((2 * (index + 0.5)) / count));
            var ring = Math.Sqrt(d: (1 - (y * y)));
            var angle = (golden * index);

            yield return Vector3.Normalize(value: new Vector3(x: ((float)(ring * Math.Cos(d: angle))), y: ((float)y), z: ((float)(ring * Math.Sin(a: angle)))));
        }
    }

    private readonly record struct Vector3d(double X, double Y, double Z) {
        public Vector3d(Vector3 value) : this(X: value.X, Y: value.Y, Z: value.Z) { }

        public double Length => Math.Sqrt(d: Dot(other: this));

        public double Dot(Vector3d other) => (((X * other.X) + (Y * other.Y)) + (Z * other.Z));
        public Vector3d Minus(Vector3d other) => new(X: (X - other.X), Y: (Y - other.Y), Z: (Z - other.Z));
        public Vector3d Plus(Vector3d other) => new(X: (X + other.X), Y: (Y + other.Y), Z: (Z + other.Z));
        public Vector3d Times(double scale) => new(X: (X * scale), Y: (Y * scale), Z: (Z * scale));
    }
}
