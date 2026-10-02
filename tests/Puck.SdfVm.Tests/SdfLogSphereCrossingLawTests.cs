using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>A CPU reference march of the fold-wall crossing rule, <c>sdfMarchAdvance</c> in <c>field/sdf-map.hlsli</c>,
/// over a log-sphere fold: a different set of solids in every shell between concentric wall spheres about the fold's
/// center, crossed exactly when the fold's chain is a similarity and by the march's tolerance (ball walls) when it is
/// not. The log-sphere fold has no fixed evaluator, so the reference states the rule's claims against an exact oracle: a
/// sphere-traced ray never steps past a solid point of the shell it lies in (no skip), and it crosses each exactly
/// crossed wall sphere at most twice. The GPU rule itself is held on the device by
/// <c>SdfLogSphereMarchDeviceLawTests</c>.</summary>
public sealed class SdfLogSphereCrossingLawTests {
    private const float Limit = 240;
    private const float None = 1.0e30f;
    private const int Steps = 2048;
    private const float Tolerance = 0.001f;
    // The floor a step once took at a shell wall, as a fraction of the sample's radius about the fold's center.
    private const float FloorFraction = 1.0e-3f;
    private const float LayerThickness = 0.001f;

    private enum FoldWalls {
        Exact,
        Ball,
    }
    // A layer hugging a wall: the points between two radii about its center, on the side its normal faces.
    private sealed record Layer(Vector3 Center, float Low, float High, Vector3 Normal);
    private sealed record Cell((Vector3 Center, float Radius)[] Spheres, Layer[] Layers);
    // Shell radii about a fold center, how the march sees the walls, and the solids of each shell.
    private sealed record Scene(Vector3 FoldCenter, float[] FoldRadii, FoldWalls Fold, Func<int, Cell> CellOf);

    private static readonly Vector3 FoldCenter = new(x: 4, y: -3, z: 2);
    private static readonly Vector3[] Normals = [Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY];
    // A shell ratio of two about FoldCenter: shell j lies between radii 2^(j - 2.5) and 2^(j - 1.5). Each holds a copy
    // of one sphere scaled by its shell, and layers hugging both of its walls, over half-spaces that turn shell to shell.
    private static readonly float[] ShellRadii = [.. Enumerable.Range(count: 8, start: 0).Select(selector: static k => MathF.Pow(x: 2, y: (k - 2.5f)))];

    private static Scene Droste(FoldWalls walls) => new(
        CellOf: static shell => {
            var scale = MathF.Pow(x: 2, y: (shell - 3));
            var layers = new List<Layer>();

            if (shell < ShellRadii.Length) {
                layers.Add(item: new Layer(FoldCenter, (ShellRadii[shell] - LayerThickness), ShellRadii[shell], Normals[(shell % Normals.Length)]));
            }
            if (shell > 0) {
                layers.Add(item: new Layer(FoldCenter, ShellRadii[(shell - 1)], (ShellRadii[(shell - 1)] + LayerThickness), Normals[((shell + 3) % Normals.Length)]));
            }

            return new Cell(Layers: [.. layers], Spheres: [((FoldCenter + (new Vector3(x: 0.9f, y: 0.3f, z: 0) * scale)), (0.08f * scale))]);
        },
        Fold: walls,
        FoldCenter: FoldCenter,
        FoldRadii: ShellRadii
    );

    [Fact]
    public void ARayCrossesEachExactFoldShellAtMostTwiceAndSkipsNothing() {
        var failures = Sweep(floored: false, maximumCrossings: 2, origins: DrosteOrigins(), scene: Droste(walls: FoldWalls.Exact));

        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures.Take(count: 20)));
    }
    [Fact]
    public void ARaySkipsNothingAcrossBallFoldShells() {
        var failures = Sweep(floored: false, maximumCrossings: int.MaxValue, origins: DrosteOrigins(), scene: Droste(walls: FoldWalls.Ball));

        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: failures.Take(count: 20)));
    }
    [Fact]
    public void AStepFlooredAtAShellWallSkipsAThinLayerThatTheCrossingHits() {
        // From 0.0005 outside shell 5's outer wall (radius 2^2.5), below the center where the layer hugging that wall
        // from inside lies and on the side the outer shell's layer leaves open, heading in toward the center: the
        // floored step strides 0.0057, past the 0.001 layer, while the crossing lands in it.
        var scene = Droste(walls: FoldWalls.Exact);
        var outward = Vector3.Normalize(value: new Vector3(x: 0.3f, y: -1, z: 0));
        var origin = (FoldCenter + (outward * (ShellRadii[5] + 0.0005f)));
        var truth = FirstSolid(direction: -outward, origin: origin, scene: scene);
        var floored = March(direction: -outward, floored: true, origin: origin, scene: scene);
        var crossing = March(direction: -outward, floored: false, origin: origin, scene: scene);

        Assert.NotNull(value: truth);
        Assert.True(condition: (!floored.Found || (floored.Traveled > (truth.Value + Tolerance))), userMessage: $"the floored step found {floored.Traveled}");
        Assert.True(condition: (crossing.Found && (crossing.Traveled <= (truth.Value + Tolerance))), userMessage: $"the crossing found {crossing.Found} at {crossing.Traveled}, the first solid at {truth}");
    }
    [Fact]
    public void TheSweepFailsAStepFlooredAtAFoldShell() => Assert.NotEmpty(collection: Sweep(floored: true, maximumCrossings: int.MaxValue, origins: DrosteOrigins(), scene: Droste(walls: FoldWalls.Ball)));

    private static Vector3[] DrosteOrigins() => [Vector3.Zero, (FoldCenter + new Vector3(x: 0.2f, y: 0.1f, z: -0.05f)), new(x: -60, y: 10, z: 5), (FoldCenter + new Vector3(x: 0, y: 12, z: 0))];
    private static List<string> Sweep(Scene scene, Vector3[] origins, int maximumCrossings, bool floored) {
        var failures = new List<string>();

        foreach (var origin in origins) {
            foreach (var direction in Directions(count: 2048)) {
                Check(direction: direction, failures: failures, floored: floored, maximumCrossings: maximumCrossings, origin: origin, scene: scene);
            }
        }

        return failures;
    }
    private static void Check(Scene scene, Vector3 origin, Vector3 direction, int maximumCrossings, List<string> failures, bool floored = false) {
        var truth = FirstSolid(direction: direction, origin: origin, scene: scene);
        var march = March(direction: direction, floored: floored, origin: origin, scene: scene);

        foreach (var (radius, count) in march.Crossings) {
            if (count > maximumCrossings) {
                failures.Add(item: $"{origin} {direction}: {count} crossings of the wall at {radius}");
            }
        }
        // The march accepts within the tolerance of a solid; it may stop at a near miss, never past the first solid.
        if ((truth is { } first) && (!march.Found || (march.Traveled > ((first + Tolerance) + 1.0e-4f)))) {
            failures.Add(item: $"{origin} {direction}: first solid at {first}, the march found {march.Found} at {march.Traveled} after {march.Steps} steps");
        }
    }
    // A plain sphere trace with the crossing rule. Floored, the step instead takes every wall's gap as a bound floored as
    // the walls once were, and crosses nothing.
    private static (bool Found, float Traveled, int Steps, Dictionary<float, int> Crossings) March(Scene scene, Vector3 origin, Vector3 direction, bool floored) {
        var crossings = new Dictionary<float, int>();
        var traveled = 0f;

        for (var step = 0; (step < Steps); step++) {
            var sample = Sample(position: (origin + (direction * traveled)), scene: scene);

            if (sample.Distance < Tolerance) {
                return (true, traveled, step, crossings);
            }
            if (floored) {
                traveled += MathF.Min(x: sample.Distance, y: sample.FlooredBound);
            } else {
                traveled = Advance(advance: sample.Distance, clearance: sample.Distance, crossed: out var crossed, direction: direction, origin: origin, sample: sample,
                    tolerance: (0.5f * Tolerance), traveled: traveled);
                if (crossed is { } wall) {
                    crossings[wall] = (crossings.GetValueOrDefault(key: wall) + 1);
                }
            }
            if (traveled > Limit) {
                break;
            }
        }

        return (false, traveled, Steps, crossings);
    }

    // The walls mapCore publishes for a sample: a shell between two concentric walls about a center.
    private readonly record struct Shell(Vector3 Center, float Inner, float Outer, float Gap, float GapRadius, int Index);
    private readonly record struct Sampled(float Distance, Shell Fold, float BallWalls, float FlooredBound);

    // The shell of `radii` about `center` holding a position: a wall the position lies past (r > R) is an inner wall.
    private static Shell ShellOf(Vector3 center, float[] radii, Vector3 position) {
        var radius = Vector3.Distance(value1: position, value2: center);

        var (index, gap, gapRadius, inner, outer) = (0, None, 0f, 0f, None);

        foreach (var wall in radii) {
            var wallGap = ((radius > wall) ? (radius - wall) : (wall - radius));

            if (radius > wall) {
                index++;
                inner = MathF.Max(x: inner, y: wall);
            } else {
                outer = MathF.Min(x: outer, y: wall);
            }
            if (wallGap < gap) {
                (gap, gapRadius) = (wallGap, wall);
            }
        }

        return new Shell(Center: center, Gap: gap, GapRadius: gapRadius, Index: index, Inner: inner, Outer: outer);
    }
    private static Sampled Sample(Scene scene, Vector3 position) {
        var fold = ShellOf(center: scene.FoldCenter, position: position, radii: scene.FoldRadii);
        var cell = scene.CellOf(arg: fold.Index);
        var distance = 1.0e9f;

        foreach (var (center, solidRadius) in cell.Spheres) {
            distance = MathF.Min(x: distance, y: (Vector3.Distance(value1: position, value2: center) - solidRadius));
        }
        // A layer's field bounds its distance from below: the larger of the shell's and the half-space's.
        foreach (var layer in cell.Layers) {
            var offset = (position - layer.Center);
            var shell = (MathF.Abs(x: (offset.Length() - (0.5f * (layer.Low + layer.High)))) - (0.5f * (layer.High - layer.Low)));

            distance = MathF.Min(x: distance, y: MathF.Max(x: shell, y: -Vector3.Dot(vector1: layer.Normal, vector2: offset)));
        }
        var foldFloor = ((fold.Gap < None) ? MathF.Max(x: fold.Gap, y: (Vector3.Distance(value1: position, value2: scene.FoldCenter) * FloorFraction)) : None);
        var exact = (scene.Fold == FoldWalls.Exact);

        return new Sampled(
            BallWalls: (exact ? None : fold.Gap),
            Distance: distance,
            FlooredBound: foldFloor,
            Fold: (exact ? fold : (fold with { Gap = None }))
        );
    }
    // sdfMarchAdvance's steps, in float, crossing the fold shell; `crossed` names the radius of an exact wall a crossing
    // passes.
    private static float Advance(Sampled sample, Vector3 origin, Vector3 direction, float traveled, float clearance, float advance, float tolerance, out float? crossed) {
        crossed = null;
        var ballGap = MathF.Min(x: sample.BallWalls, y: sample.Fold.Gap);

        if (MathF.Max(x: clearance, y: advance) <= ballGap) {
            return (traveled + advance);
        }
        var (next, after, wall) = (None, None, 0f);

        if (sample.Fold.Gap < None) {
            ShellExit(after: ref after, direction: direction, next: ref next, origin: origin, shell: sample.Fold, traveled: traveled, wall: ref wall);
        }
        if (!(ballGap < clearance) && ((traveled + advance) < next)) {
            return (traveled + advance);
        }
        var landing = (traveled + clearance);

        if ((next < None) && ((next - traveled) <= clearance)) {
            var beyond = ((next < Limit) ? MathF.Min(x: tolerance, y: (0.5f * (MathF.Min(x: after, y: Limit) - next))) : tolerance);

            crossed = wall;
            landing = MathF.Max(x: (next + beyond), y: NextUp(value: next));
        }
        if ((sample.BallWalls < None) && (((traveled + sample.BallWalls) + tolerance) < landing)) {
            var reach = MathF.Max(x: ((traveled + sample.BallWalls) + tolerance), y: NextUp(value: traveled));

            if (reach < next) {
                crossed = null;
            }
            landing = ((reach >= next) ? MathF.Max(x: reach, y: NextUp(value: next)) : reach);
        }

        return landing;
    }
    private static void ShellExit(Shell shell, Vector3 origin, Vector3 direction, float traveled, ref float next, ref float after, ref float wall) {
        var offset = (origin - shell.Center);
        var along = Vector3.Dot(vector1: offset, vector2: direction);

        if (shell.Outer < None) {
            var exit = (Roots(along: along, far: out var far, near: out _, offset: offset, radius: shell.Outer) ? MathF.Max(x: far, y: traveled) : traveled);

            if (exit < next) {
                (next, after, wall) = (exit, None, shell.Outer);
            }
        }
        if ((shell.Inner > 0) && Roots(along: along, far: out var innerFar, near: out var innerNear, offset: offset, radius: shell.Inner)) {
            var entry = MathF.Max(x: innerNear, y: traveled);

            if ((innerNear < innerFar) && (entry < innerFar) && (entry < next)) {
                (next, after, wall) = (entry, innerFar, shell.Inner);
            }
        }
    }
    private static float NextUp(float value) => MathF.BitIncrement(x: MathF.Max(x: value, y: 1.0e-30f));
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
    // The first ray parameter in [0, Limit] at which the ray stands in a solid of the shells it is in, exactly.
    private static float? FirstSolid(Scene scene, Vector3 origin, Vector3 direction) {
        var o = new Vector3d(value: origin);
        var d = new Vector3d(value: direction);
        var breaks = new List<double> { 0, Limit };

        foreach (var wall in scene.FoldRadii) {
            if (Interval(center: new Vector3d(value: scene.FoldCenter), direction: d, origin: o, radius: wall) is { } span) {
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
            var middle = o.Plus(other: d.Times(scale: (0.5 * (start + end))));
            var foldRadius = middle.Minus(other: new Vector3d(value: scene.FoldCenter)).Length;
            var cell = scene.CellOf(arg: scene.FoldRadii.Count(predicate: wall => (foldRadius > wall)));
            var spans = new List<(double Near, double Far)>();
            double? first = null;

            foreach (var (center, radius) in cell.Spheres) {
                if (Interval(center: new Vector3d(value: center), direction: d, origin: o, radius: radius) is { } solid) {
                    spans.Add(item: solid);
                }
            }
            foreach (var layer in cell.Layers) {
                spans.AddRange(collection: LayerSpans(direction: d, layer: layer, origin: o));
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
    private static IEnumerable<(double Near, double Far)> LayerSpans(Vector3d origin, Vector3d direction, Layer layer) {
        var center = new Vector3d(value: layer.Center);

        if (Interval(center: center, direction: direction, origin: origin, radius: layer.High) is not { } shell) {
            yield break;
        }
        var normal = new Vector3d(value: layer.Normal);
        var facing = normal.Dot(other: direction);
        var height = normal.Dot(other: origin.Minus(other: center));

        var (low, high) = ((facing > 0) ? ((-height / facing), double.PositiveInfinity) : ((facing < 0) ? (double.NegativeInfinity, (-height / facing)) : ((height > 0) ? (double.NegativeInfinity, double.PositiveInfinity) : (1, 0))));
        (double Near, double Far)[] pieces = ((Interval(center: center, direction: direction, origin: origin, radius: layer.Low) is { } hole)
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
