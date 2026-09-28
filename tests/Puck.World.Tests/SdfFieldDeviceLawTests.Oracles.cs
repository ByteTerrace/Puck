using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Tests;

public sealed partial class SdfFieldDeviceLawTests {
    // The ops the evaluator refuses that have an equivalent at one point, each with what it does to that point or to
    // the field there, written from the op's definition in field/sdf-map.hlsli: every plane rotation (each plane driven
    // by each axis), a polar repeat about each axis, plain and mirrored, a shear of each target by each other driver, a
    // Gaussian push, a domain warp, a sinusoidal displacement and a value-noise displacement.
    private static IEnumerable<SdfFieldOracle> Oracles() {
        const float Rate = 0.6f;
        const float Origin = 0.125f;

        foreach (var plane in Enum.GetValues<SdfPlane>()) {
            foreach (var driver in Enum.GetValues<SdfAxis>()) {
                yield return new SdfFieldOracle(
                    Axis: Normal(plane: plane),
                    Name: $"rotate-plane {plane} by {driver}",
                    Op: b => b.RotatePlane(driver: driver, origin: Origin, plane: plane, rate: Rate),
                    Tail: WarpedBox,
                    Warp: point => PlaneRotated(driver: driver, origin: Origin, plane: plane, point: point, rate: Rate)
                );
            }
        }

        foreach (var axis in Enum.GetValues<SdfAxis>()) {
            foreach (var mirror in ((bool[])[false, true])) {
                var offset = Across(axis: axis);

                yield return new SdfFieldOracle(
                    Axis: Unit(axis: axis),
                    Name: $"repeat-polar about {axis}{(mirror ? ", mirrored" : string.Empty)}",
                    Op: b => b.RepeatPolar(axis: axis, count: 5, mirror: mirror),
                    Tail: (b, m) => b.Translate(offset: offset).Box(halfExtents: new Vector3(x: 0.2f, y: 0.15f, z: 0.1f), material: m, round: 0.02f),
                    Warp: point => PolarRepeated(axis: axis, count: 5, mirror: mirror, point: point)
                );
            }
        }

        foreach (var target in Enum.GetValues<SdfAxis>()) {
            foreach (var driver in Enum.GetValues<SdfAxis>().Where(predicate: driver => (driver != target))) {
                yield return new SdfFieldOracle(
                    Name: $"shear {target} by {driver}",
                    Op: b => b.Shear(cubic: 0.1f, driver: driver, linear: 0.3f, quadratic: 0.2f, target: target),
                    Tail: WarpedBox,
                    Warp: point => {
                        var t = point[((int)driver)];
                        var sheared = point;

                        sheared[((int)target)] += (((((0.1f * t) + 0.2f) * t) + 0.3f) * t);

                        return sheared;
                    }
                );
            }
        }

        var center = new Vector3(x: 0.2f, y: 0.1f, z: -0.1f);
        var radii = new Vector3(x: 0.6f, y: 0.5f, z: 0.7f);
        var push = new Vector3(x: 0.15f, y: -0.1f, z: 0.2f);

        yield return new SdfFieldOracle(
            Name: "gaussian-push",
            Op: b => b.GaussianPush(center: center, push: push, radii: radii),
            Tail: WarpedBox,
            Warp: point => {
                var offset = ((point - center) / radii);

                return (point - (push * MathF.Exp(x: -Vector3.Dot(vector1: offset, vector2: offset))));
            }
        );

        var frequency = new Vector3(x: 1.3f, y: 0.9f, z: 1.7f);

        yield return new SdfFieldOracle(
            Name: "domain-warp",
            Op: b => b.DomainWarp(amplitude: 0.12f, frequency: frequency),
            Tail: WarpedBox,
            Warp: point => (point + (0.12f * new Vector3(
                x: MathF.Sin(x: (frequency.X * point.Y)),
                y: MathF.Sin(x: (frequency.Y * point.Z)),
                z: MathF.Sin(x: (frequency.Z * point.X))
            )))
        );

        var relief = new Vector3(x: 2.1f, y: 1.7f, z: 2.9f);

        yield return new SdfFieldOracle(
            Name: "displace",
            Op: b => b.Displace(amplitude: 0.06f, frequency: relief),
            Relief: point => (0.06f * ((MathF.Sin(x: (relief.X * point.X)) * MathF.Sin(x: (relief.Y * point.Y))) * MathF.Sin(x: (relief.Z * point.Z)))),
            Tail: WarpedBox
        );
        yield return new SdfFieldOracle(
            Name: "noise-displace",
            Op: b => b.NoiseDisplace(amplitude: 0.08f, frequency: 1.7f, gain: 0.5f, lacunarity: 2f, octaves: 3, seed: 7u),
            Relief: point => NoiseRelief(amplitude: 0.08f, frequency: 1.7f, gain: 0.5f, lacunarity: 2f, octaves: 3, point: point, seed: 7u),
            Tail: WarpedBox
        );
    }
    private static Vector3 Unit(SdfAxis axis) =>
        axis switch {
            SdfAxis.X => Vector3.UnitX,
            SdfAxis.Y => Vector3.UnitY,
            _ => Vector3.UnitZ,
        };
    private static Vector3 Normal(SdfPlane plane) =>
        plane switch {
            SdfPlane.XY => Vector3.UnitZ,
            SdfPlane.YZ => Vector3.UnitX,
            _ => Vector3.UnitY,
        };
    // A point off an axis, in its repeat's first sector: along the fold plane's first coordinate.
    private static Vector3 Across(SdfAxis axis) =>
        ((axis == SdfAxis.X) ? new Vector3(x: 0f, y: 0.7f, z: 0f) : new Vector3(x: 0.7f, y: 0f, z: 0f));
    // The fold plane's two coordinates (SDF_OP_REPEAT_POLAR): Y and Z about X, X and Y about Z, X and Z about Y.
    private static (int U, int V) FoldPlane(SdfAxis axis) =>
        axis switch {
            SdfAxis.X => (1, 2),
            SdfAxis.Z => (0, 1),
            _ => (0, 2),
        };
    // SDF_OP_ROTATE_PLANE: the plane's two coordinates (Y and Z of YZ, X and Y of XY, X and Z of XZ) rotate by the rate
    // times the driver's distance from the origin, u' = c·u + s·v and v' = -s·u + c·v.
    private static Vector3 PlaneRotated(SdfPlane plane, SdfAxis driver, float rate, float origin, Vector3 point) {
        var u = ((plane == SdfPlane.YZ) ? 1 : 0);
        var v = ((plane == SdfPlane.XY) ? 1 : 2);
        var angle = (rate * (point[((int)driver)] - origin));

        var (sine, cosine) = MathF.SinCos(x: angle);
        var rotated = point;

        rotated[u] = ((cosine * point[u]) + (sine * point[v]));
        rotated[v] = ((-sine * point[u]) + (cosine * point[v]));

        return rotated;
    }
    // SDF_OP_REPEAT_POLAR: the point's angle about the axis folds into the sector centred on the fold plane's first
    // coordinate, and mirrored across that sector's bisector when the repeat mirrors.
    private static Vector3 PolarRepeated(SdfAxis axis, int count, bool mirror, Vector3 point) {
        var (u, v) = FoldPlane(axis: axis);
        var sectorAngle = ((2f * MathF.PI) / count);
        var inverseAngle = (1f / sectorAngle);
        var angle = (MathF.Atan2(x: point[u], y: point[v]) + (0.5f * sectorAngle));
        var radius = MathF.Sqrt(x: ((point[u] * point[u]) + (point[v] * point[v])));
        var sector = MathF.Floor(x: (angle * inverseAngle));

        angle = ((angle - (sectorAngle * sector)) - (0.5f * sectorAngle));

        if (mirror) {
            angle = MathF.Abs(x: angle);
        }

        var folded = point;

        folded[u] = (MathF.Cos(x: angle) * radius);
        folded[v] = (MathF.Sin(x: angle) * radius);

        return folded;
    }
    // SDF_OP_NOISE_DISPLACE: the amplitude times the normalized sum of the octaves' value noise at the point.
    private static float NoiseRelief(float frequency, float amplitude, int octaves, float gain, float lacunarity, uint seed, Vector3 point) {
        var gainSum = 0f;
        var gainPower = 1f;

        for (var octave = 0; (octave < octaves); octave++) {
            gainSum += gainPower;
            gainPower *= gain;
        }

        var q = (point * frequency);
        var octaveAmplitude = 1f;
        var sum = 0f;

        for (var octave = 0U; (octave < ((uint)octaves)); octave++) {
            var octaveSeed = (seed + octave);

            sum += (octaveAmplitude * ValueNoise(
                q: q,
                seed: (octaveSeed, (octaveSeed * 0x9E3779B9u), (octaveSeed * 0x85EBCA6Bu))
            ));
            q *= lacunarity;
            octaveAmplitude *= gain;
        }

        return ((amplitude * (1f / gainSum)) * sum);
    }
    // sdfValueNoise3: the quintic-smoothed trilinear blend of the PCG3D-hashed lattice corners, in [-1, 1].
    private static float ValueNoise(Vector3 q, (uint X, uint Y, uint Z) seed) {
        var floor = new Vector3(x: MathF.Floor(x: q.X), y: MathF.Floor(x: q.Y), z: MathF.Floor(x: q.Z));

        var (x, y, z) = (((int)floor.X), ((int)floor.Y), ((int)floor.Z));
        var f = (q - floor);
        var u = (((f * f) * f) * ((f * ((f * 6f) - new Vector3(value: 15f))) + new Vector3(value: 10f)));

        float Corner(int dx, int dy, int dz) =>
            (Pcg3d(
                x: ((uint)(x + dx)) ^ seed.X,
                y: ((uint)(y + dy)) ^ seed.Y,
                z: ((uint)(z + dz)) ^ seed.Z
            ) * (1f / 4294967296f));
        static float Lerp(float a, float b, float t) =>
            (a + ((b - a) * t));

        var x00 = Lerp(a: Corner(dx: 0, dy: 0, dz: 0), b: Corner(dx: 1, dy: 0, dz: 0), t: u.X);
        var x10 = Lerp(a: Corner(dx: 0, dy: 1, dz: 0), b: Corner(dx: 1, dy: 1, dz: 0), t: u.X);
        var x01 = Lerp(a: Corner(dx: 0, dy: 0, dz: 1), b: Corner(dx: 1, dy: 0, dz: 1), t: u.X);
        var x11 = Lerp(a: Corner(dx: 0, dy: 1, dz: 1), b: Corner(dx: 1, dy: 1, dz: 1), t: u.X);

        return ((Lerp(a: Lerp(a: x00, b: x10, t: u.Y), b: Lerp(a: x01, b: x11, t: u.Y), t: u.Z) * 2f) - 1f);
    }
    // sdfPcg3d's first output.
    private static uint Pcg3d(uint x, uint y, uint z) {
        x = ((x * 1664525u) + 1013904223u);
        y = ((y * 1664525u) + 1013904223u);
        z = ((z * 1664525u) + 1013904223u);
        x += (y * z);
        y += (z * x);
        z += (x * y);
        x ^= (x >> 16);
        y ^= (y >> 16);
        z ^= (z >> 16);
        x += (y * z);

        return x;
    }
}
