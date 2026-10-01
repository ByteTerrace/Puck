using Puck.SignedDistance;

namespace Puck.World.Tests.FidgetStudy;

/// <summary>Double-precision transcriptions of the kernels' primitive and blend formulas
/// (<c>field/sdf-shapes.hlsli</c>, <c>field/sdf-blend.hlsli</c>, <c>field/sdf-noise.hlsli</c>,
/// <c>field/sdf-hash.hlsli</c>), written in the same operation order. Study code: the kernels are the contract.</summary>
public static class SdfStudyShapes {
    /// <summary>The accumulator's seed (<c>SDF_FAR_DISTANCE</c>).</summary>
    public const double FarDistance = 1.0e9;

    private const uint HashStreamA = 0x9E3779B9u;
    private const uint HashStreamB = 0x85EBCA6Bu;
    private const double SmoothRadiusMin = 0.0001;
    private const double SqrtHalf = 0.7071067811865476;

    /// <summary>Returns whether <see cref="Evaluate"/> transcribes <paramref name="shape"/>.</summary>
    /// <param name="shape">The shape type.</param>
    /// <returns>Whether the shape is supported.</returns>
    public static bool Supports(SdfShapeType shape) => (shape is SdfShapeType.Sphere or SdfShapeType.Box or SdfShapeType.ScreenSlab
        or SdfShapeType.Torus or SdfShapeType.Plane or SdfShapeType.RoundCone or SdfShapeType.Superellipsoid
        or SdfShapeType.Capsule or SdfShapeType.Cylinder or SdfShapeType.RoundedRectangle
        or SdfShapeType.ChamferedRectangle or SdfShapeType.Trapezoid or SdfShapeType.Glyph);
    /// <summary>Returns whether the kernels' gradient walk has an analytic gradient for <paramref name="shape"/>
    /// (<c>evaluateShapeGradient</c>); every other shape takes a shape-local four-tap difference.</summary>
    /// <param name="shape">The shape type.</param>
    /// <returns>Whether the gradient is analytic.</returns>
    public static bool HasAnalyticGradient(SdfShapeType shape) => (shape is SdfShapeType.Sphere or SdfShapeType.Plane or SdfShapeType.Box
        or SdfShapeType.ScreenSlab or SdfShapeType.Torus or SdfShapeType.Capsule or SdfShapeType.Cylinder or SdfShapeType.Superellipsoid);
    /// <summary>Evaluates one primitive at a local point. A glyph evaluates as its conservative extruded quad, the
    /// form the beam kernel uses.</summary>
    /// <param name="shape">The shape type.</param>
    /// <param name="p">The local point.</param>
    /// <param name="d0">The first payload vector.</param>
    /// <param name="d1">The second payload vector.</param>
    /// <returns>The primitive's field value.</returns>
    /// <exception cref="NotSupportedException"><paramref name="shape"/> has no transcription.</exception>
    public static double Evaluate(SdfShapeType shape, V3 p, (double X, double Y, double Z, double W) d0, (double X, double Y, double Z, double W) d1) => shape switch {
        SdfShapeType.Sphere => (p.Length - d0.X),
        SdfShapeType.Box or SdfShapeType.ScreenSlab => Box(p: p, half: new V3(X: d0.X, Y: d0.Y, Z: d0.Z), round: d0.W),
        SdfShapeType.Torus => (Length2(x: (Length2(x: p.X, y: p.Z) - d0.X), y: p.Y) - d0.Y),
        SdfShapeType.Plane => (V3.Dot(a: p, b: new V3(X: d0.X, Y: d0.Y, Z: d0.Z)) + d0.W),
        SdfShapeType.RoundCone => RoundCone(a: d1.Y, b: d0.W, height: d0.Z, lower: d0.X, p: p, upper: d0.Y),
        SdfShapeType.Superellipsoid => Superellipsoid(p: p, radii: new V3(X: d0.X, Y: d0.Y, Z: d0.Z), inverse: new V3(X: d1.Y, Y: d1.Z, Z: d1.W), exponent: d0.W),
        SdfShapeType.Capsule => Capsule(p: p, end: new V3(X: d0.X, Y: d0.Y, Z: d0.Z), radius: d0.W, inverseLengthSquared: d1.Y),
        SdfShapeType.Cylinder => (Cylinder(halfHeight: d0.Y, p: p, radius: d0.X) - d1.W),
        SdfShapeType.RoundedRectangle => (((d1.Y > 0.5)
            ? ExtrudeChamfer(d: RoundBox2D(x: p.X, y: p.Y, bx: d0.X, by: d0.Y, r: d0.Z), pz: p.Z, h: d0.W, c: d1.Z)
            : RoundBox2D(x: (Length2(x: p.X, y: p.Z) - d0.W), y: p.Y, bx: d0.X, by: d0.Y, r: d0.Z)) - d1.W),
        SdfShapeType.ChamferedRectangle => (((d1.Y > 0.5)
            ? ExtrudeChamfer(d: ChamferBox2D(x: p.X, y: p.Y, bx: d0.X, by: d0.Y, c: d0.Z), pz: p.Z, h: d0.W, c: d0.Z)
            : ChamferBox2D(x: (Length2(x: p.X, y: p.Z) - d0.W), y: p.Y, bx: d0.X, by: d0.Y, c: d0.Z)) - d1.W),
        SdfShapeType.Trapezoid => (((d1.Y > 0.5)
            ? ExtrudeChamfer(d: Trapezoid2D(x: p.X, y: p.Y, r1: d0.X, r2: d0.Y, he: d0.Z), pz: p.Z, h: d0.W, c: d1.Z)
            : Trapezoid2D(x: (Length2(x: p.X, y: p.Z) - d0.W), y: p.Y, r1: d0.X, r2: d0.Y, he: d0.Z)) - d1.W),
        SdfShapeType.Glyph => GlyphQuad(halfDepth: d0.W, halfX: d1.Y, halfY: d1.Z, p: p),
        _ => throw new NotSupportedException(message: $"shape {shape}"),
    };
    /// <summary>Composes a candidate into the accumulator (<c>blendShape</c>).</summary>
    /// <param name="current">The accumulator.</param>
    /// <param name="candidate">The candidate.</param>
    /// <param name="blend">The blend.</param>
    /// <param name="smooth">The blend radius lane.</param>
    /// <returns>The composed value.</returns>
    public static double Blend(double current, double candidate, SdfBlendOp blend, double smooth) {
        var smoothK = Math.Max(val1: smooth, val2: SmoothRadiusMin);
        var chamfer = Math.Max(val1: smooth, val2: 0.0);

        return blend switch {
            SdfBlendOp.SmoothUnion => SmoothUnion(a: current, b: candidate, k: smoothK),
            SdfBlendOp.Subtraction or SdfBlendOp.StairsSubtraction => Math.Max(val1: current, val2: -candidate),
            SdfBlendOp.Intersection => Math.Max(val1: current, val2: candidate),
            SdfBlendOp.Xor => Math.Max(val1: Math.Min(val1: current, val2: candidate), val2: -Math.Max(val1: current, val2: candidate)),
            SdfBlendOp.SmoothIntersection => -SmoothUnion(a: -current, b: -candidate, k: smoothK),
            SdfBlendOp.SmoothSubtraction => -SmoothUnion(a: candidate, b: -current, k: smoothK),
            SdfBlendOp.GrooveUnion => Math.Max(val1: Math.Min(val1: current, val2: candidate), val2: (chamfer - Length2(x: current, y: candidate))),
            SdfBlendOp.PipeUnion => Math.Min(val1: Math.Min(val1: current, val2: candidate), val2: (Length2(x: current, y: candidate) - chamfer)),
            SdfBlendOp.GrooveSubtraction => Math.Max(val1: Math.Max(val1: current, val2: -candidate), val2: (chamfer - Length2(x: current, y: candidate))),
            SdfBlendOp.PipeSubtraction => Math.Min(val1: Math.Max(val1: current, val2: -candidate), val2: (Length2(x: current, y: candidate) - chamfer)),
            SdfBlendOp.ChamferUnion => Math.Min(val1: Math.Min(val1: current, val2: candidate), val2: (((current + candidate) - chamfer) * SqrtHalf)),
            SdfBlendOp.ChamferIntersection => Math.Max(val1: Math.Max(val1: current, val2: candidate), val2: (((current + candidate) + chamfer) * SqrtHalf)),
            SdfBlendOp.ChamferSubtraction => Math.Max(val1: Math.Max(val1: current, val2: -candidate), val2: (((current - candidate) + chamfer) * SqrtHalf)),
            _ => Math.Min(val1: current, val2: candidate),
        };
    }
    /// <summary>Returns the polynomial smooth minimum (<c>blendSmoothUnion</c>).</summary>
    /// <param name="a">The accumulator.</param>
    /// <param name="b">The candidate.</param>
    /// <param name="k">The floored radius.</param>
    /// <returns>The smooth minimum.</returns>
    public static double SmoothUnion(double a, double b, double k) {
        var h = Math.Clamp(max: 1.0, min: 0.0, value: (0.5 + ((0.5 * (b - a)) / k)));
        var blended = ((h <= 0.0) ? b : (a + ((1.0 - h) * (b - a))));

        return (blended - ((k * h) * (1.0 - h)));
    }
    /// <summary>Returns the smooth blend radius floor the kernels apply.</summary>
    /// <param name="smooth">The authored radius lane.</param>
    /// <returns>The floored radius.</returns>
    public static double SmoothRadius(double smooth) => Math.Max(val1: smooth, val2: SmoothRadiusMin);
    /// <summary>Rotates a point by the inverse of a quaternion (<c>rotatePointByInverseQuaternion</c>).</summary>
    /// <param name="p">The point.</param>
    /// <param name="q">The quaternion (x, y, z, w).</param>
    /// <returns>The rotated point.</returns>
    public static V3 RotateInverse(V3 p, (double X, double Y, double Z, double W) q) {
        var u = new V3(X: -q.X, Y: -q.Y, Z: -q.Z);

        return (p + (V3.Cross(a: u, b: ((p * q.W) + V3.Cross(a: u, b: p))) * 2.0));
    }
    /// <summary>Returns an upper bound on the operator norm of <see cref="RotateInverse"/>'s linear map, which
    /// departs from one only as far as the stored quaternion departs from unit length.</summary>
    /// <param name="q">The quaternion.</param>
    /// <returns>The bound, at least one.</returns>
    public static double RotationNormBound(in (double X, double Y, double Z, double W) q) {
        var c0 = RotateInverse(p: new V3(X: 1, Y: 0, Z: 0), q: q);
        var c1 = RotateInverse(p: new V3(X: 0, Y: 1, Z: 0), q: q);
        var c2 = RotateInverse(p: new V3(X: 0, Y: 0, Z: 1), q: q);
        // The Gram matrix's largest row sum bounds its largest eigenvalue (Gershgorin).
        var g00 = V3.Dot(a: c0, b: c0);
        var g11 = V3.Dot(a: c1, b: c1);
        var g22 = V3.Dot(a: c2, b: c2);
        var g01 = Math.Abs(value: V3.Dot(a: c0, b: c1));
        var g02 = Math.Abs(value: V3.Dot(a: c0, b: c2));
        var g12 = Math.Abs(value: V3.Dot(a: c1, b: c2));
        var row = Math.Max(val1: ((g00 + g01) + g02), val2: Math.Max(val1: ((g01 + g11) + g12), val2: ((g02 + g12) + g22)));

        return Math.Max(val1: 1.0, val2: Math.Sqrt(d: row));
    }
    /// <summary>Returns the fBm value-noise relief sum <c>SDF_OP_NOISE_DISPLACE</c> adds, before its amplitude.</summary>
    /// <param name="q">The noise-space point (local point times frequency).</param>
    /// <param name="seed">The seed lane.</param>
    /// <param name="octaves">The octave count.</param>
    /// <param name="gain">The per-octave gain.</param>
    /// <param name="lacunarity">The per-octave frequency factor.</param>
    /// <returns>The unnormalized sum.</returns>
    public static double NoiseSum(V3 q, uint seed, uint octaves, double gain, double lacunarity) {
        var amplitude = 1.0;
        var sum = 0.0;

        for (var octave = 0u; (octave < octaves); octave++) {
            var octaveSeed = (seed + octave);

            sum += (amplitude * ValueNoise(q: q, sx: octaveSeed, sy: unchecked((octaveSeed * HashStreamA)), sz: unchecked((octaveSeed * HashStreamB))));
            q *= lacunarity;
            amplitude *= gain;
        }

        return sum;
    }
    /// <summary>Returns the cellular distance <c>SDF_OP_CELL_DISPLACE</c> reads (<c>sdfCellDistanceGrad</c>):
    /// F1 for mode zero, F2 minus F1 otherwise.</summary>
    /// <param name="q">The cell-space point.</param>
    /// <param name="seed">The seed lane.</param>
    /// <param name="mode">The mode lane.</param>
    /// <param name="randomness">The feature jitter.</param>
    /// <returns>The distance, in [0, 2 sqrt 3].</returns>
    public static double CellDistance(V3 q, uint seed, uint mode, double randomness) {
        var fx = Math.Floor(d: q.X);
        var fy = Math.Floor(d: q.Y);
        var fz = Math.Floor(d: q.Z);
        var f = new V3(X: (q.X - fx), Y: (q.Y - fy), Z: (q.Z - fz));
        var first = 1.0e20;
        var second = 1.0e20;

        for (var z = -1; (z <= 1); z++) {
            for (var y = -1; (y <= 1); y++) {
                for (var x = -1; (x <= 1); x++) {
                    var (hx, hy, hz) = Pcg3d(
                        x: unchecked((uint)(((int)fx) + x)) ^ seed,
                        y: unchecked((uint)(((int)fy) + y)) ^ (seed ^ 0x9E3779B9u),
                        z: unchecked((uint)(((int)fz) + z)) ^ (seed ^ 0x85EBCA77u)
                    );
                    var feature = new V3(
                        X: (0.5 + (randomness * (((hx >> 16) * (1.0 / 65536.0)) - 0.5))),
                        Y: (0.5 + (randomness * (((hy >> 16) * (1.0 / 65536.0)) - 0.5))),
                        Z: (0.5 + (randomness * (((hz >> 16) * (1.0 / 65536.0)) - 0.5)))
                    );
                    var delta = (f - (new V3(X: x, Y: y, Z: z) + feature));
                    var squared = V3.Dot(a: delta, b: delta);

                    if (squared < first) {
                        second = first;
                        first = squared;
                    } else if (squared < second) {
                        second = squared;
                    }
                }
            }
        }
        first = Math.Sqrt(d: first);
        second = Math.Sqrt(d: second);

        return ((mode == 0u) ? first : (second - first));
    }

    private static double ValueNoise(V3 q, uint sx, uint sy, uint sz) {
        var fx = Math.Floor(d: q.X);
        var fy = Math.Floor(d: q.Y);
        var fz = Math.Floor(d: q.Z);
        var cx = ((int)fx);
        var cy = ((int)fy);
        var cz = ((int)fz);
        var u = new V3(X: Quintic(f: (q.X - fx)), Y: Quintic(f: (q.Y - fy)), Z: Quintic(f: (q.Z - fz)));
        var x00 = Lerp(a: Corner(x: cx, y: cy, z: cz), b: Corner(x: (cx + 1), y: cy, z: cz), t: u.X);
        var x10 = Lerp(a: Corner(x: cx, y: (cy + 1), z: cz), b: Corner(x: (cx + 1), y: (cy + 1), z: cz), t: u.X);
        var x01 = Lerp(a: Corner(x: cx, y: cy, z: (cz + 1)), b: Corner(x: (cx + 1), y: cy, z: (cz + 1)), t: u.X);
        var x11 = Lerp(a: Corner(x: cx, y: (cy + 1), z: (cz + 1)), b: Corner(x: (cx + 1), y: (cy + 1), z: (cz + 1)), t: u.X);

        return ((Lerp(a: Lerp(a: x00, b: x10, t: u.Y), b: Lerp(a: x01, b: x11, t: u.Y), t: u.Z) * 2.0) - 1.0);

        double Corner(int x, int y, int z) => (Pcg3d(x: unchecked((uint)x) ^ sx, y: unchecked((uint)y) ^ sy, z: unchecked((uint)z) ^ sz).X * (1.0 / 4294967296.0));
        static double Quintic(double f) => (((f * f) * f) * ((f * ((f * 6.0) - 15.0)) + 10.0));
    }
    private static (uint X, uint Y, uint Z) Pcg3d(uint x, uint y, uint z) {
        unchecked {
            x = ((x * 1664525u) + 1013904223u);
            y = ((y * 1664525u) + 1013904223u);
            z = ((z * 1664525u) + 1013904223u);
            x += (y * z); y += (z * x); z += (x * y);
            x ^= (x >> 16); y ^= (y >> 16); z ^= (z >> 16);
            x += (y * z); y += (z * x); z += (x * y);
        }

        return (x, y, z);
    }
    private static double Lerp(double a, double b, double t) => (a + (t * (b - a)));
    private static double Length2(double x, double y) => Math.Sqrt(d: ((x * x) + (y * y)));
    private static double Box(V3 p, V3 half, double round) {
        var qx = (Math.Abs(value: p.X) - (half.X - round));
        var qy = (Math.Abs(value: p.Y) - (half.Y - round));
        var qz = (Math.Abs(value: p.Z) - (half.Z - round));
        var outside = new V3(X: Math.Max(val1: qx, val2: 0.0), Y: Math.Max(val1: qy, val2: 0.0), Z: Math.Max(val1: qz, val2: 0.0)).Length;

        return ((outside + Math.Min(val1: Math.Max(val1: qx, val2: Math.Max(val1: qy, val2: qz)), val2: 0.0)) - round);
    }
    private static double Capsule(V3 p, V3 end, double radius, double inverseLengthSquared) {
        var h = Math.Clamp(value: (V3.Dot(a: p, b: end) * inverseLengthSquared), min: 0.0, max: 1.0);

        return ((p - (end * h)).Length - radius);
    }
    private static double Cylinder(V3 p, double radius, double halfHeight) {
        var dx = (Length2(x: p.X, y: p.Z) - radius);
        var dy = (Math.Abs(value: p.Y) - halfHeight);

        return (Math.Min(val1: Math.Max(val1: dx, val2: dy), val2: 0.0) + Length2(x: Math.Max(val1: dx, val2: 0.0), y: Math.Max(val1: dy, val2: 0.0)));
    }
    private static double RoundCone(V3 p, double lower, double upper, double height, double b, double a) {
        var qx = Length2(x: p.X, y: p.Z);
        var qy = p.Y;
        var k = ((qx * -b) + (qy * a));

        if (k < 0.0) {
            return (Length2(x: qx, y: qy) - lower);
        }
        if (k > (a * height)) {
            return (Length2(x: qx, y: (qy - height)) - upper);
        }

        return (((qx * a) + (qy * b)) - lower);
    }
    private static double Superellipsoid(V3 p, V3 radii, V3 inverse, double exponent) {
        var minRadius = Math.Min(val1: radii.X, val2: Math.Min(val1: radii.Y, val2: radii.Z));

        if (exponent == 2.0) {
            return ((new V3(X: (p.X * inverse.X), Y: (p.Y * inverse.Y), Z: (p.Z * inverse.Z)).Length - 1.0) * minRadius);
        }

        var q = new V3(X: (Math.Abs(value: p.X) * inverse.X), Y: (Math.Abs(value: p.Y) * inverse.Y), Z: (Math.Abs(value: p.Z) * inverse.Z));
        var m = Math.Max(val1: q.X, val2: Math.Max(val1: q.Y, val2: q.Z));

        if (m <= 0.0) {
            return -minRadius;
        }

        var sum = ((Math.Pow(x: (q.X / m), y: exponent) + Math.Pow(x: (q.Y / m), y: exponent)) + Math.Pow(x: (q.Z / m), y: exponent));

        return (((m * Math.Pow(x: sum, y: (1.0 / exponent))) - 1.0) * minRadius);
    }
    private static double ExtrudeChamfer(double d, double pz, double h, double c) {
        var wy = (Math.Abs(value: pz) - h);
        var plain = (Math.Min(val1: Math.Max(val1: d, val2: wy), val2: 0.0) + Length2(x: Math.Max(val1: d, val2: 0.0), y: Math.Max(val1: wy, val2: 0.0)));
        var bevel = (((d + wy) + c) * SqrtHalf);

        return Math.Max(val1: plain, val2: bevel);
    }
    private static double RoundBox2D(double x, double y, double bx, double by, double r) {
        var qx = ((Math.Abs(value: x) - bx) + r);
        var qy = ((Math.Abs(value: y) - by) + r);

        return ((Math.Min(val1: Math.Max(val1: qx, val2: qy), val2: 0.0) + Length2(x: Math.Max(val1: qx, val2: 0.0), y: Math.Max(val1: qy, val2: 0.0))) - r);
    }
    private static double ChamferBox2D(double x, double y, double bx, double by, double c) {
        var qx = (Math.Abs(value: x) - bx);
        var qy = (Math.Abs(value: y) - by);
        var box = (Math.Min(val1: Math.Max(val1: qx, val2: qy), val2: 0.0) + Length2(x: Math.Max(val1: qx, val2: 0.0), y: Math.Max(val1: qy, val2: 0.0)));

        return Math.Max(val1: box, val2: (((qx + qy) + c) * SqrtHalf));
    }
    private static double Trapezoid2D(double x, double y, double r1, double r2, double he) {
        var k1x = r2;
        var k1y = he;
        var k2x = (r2 - r1);
        var k2y = (2.0 * he);

        x = Math.Abs(value: x);

        var cax = (x - Math.Min(val1: x, val2: ((y < 0.0) ? r1 : r2)));
        var cay = (Math.Abs(value: y) - he);
        var t = Math.Clamp(max: 1.0, min: 0.0, value: ((((k1x - x) * k2x) + ((k1y - y) * k2y)) / ((k2x * k2x) + (k2y * k2y))));
        var cbx = ((x - k1x) + (k2x * t));
        var cby = ((y - k1y) + (k2y * t));
        var s = (((cbx < 0.0) && (cay < 0.0)) ? -1.0 : 1.0);

        return (s * Math.Sqrt(d: Math.Min(val1: ((cax * cax) + (cay * cay)), val2: ((cbx * cbx) + (cby * cby)))));
    }
    private static double GlyphQuad(V3 p, double halfX, double halfY, double halfDepth) {
        var bx = (Math.Abs(value: p.X) - halfX);
        var by = (Math.Abs(value: p.Y) - halfY);
        var quad = (Length2(x: Math.Max(val1: bx, val2: 0.0), y: Math.Max(val1: by, val2: 0.0)) + Math.Min(val1: Math.Max(val1: bx, val2: by), val2: 0.0));
        var wy = (Math.Abs(value: p.Z) - halfDepth);

        return (Math.Min(val1: Math.Max(val1: quad, val2: wy), val2: 0.0) + Length2(x: Math.Max(val1: quad, val2: 0.0), y: Math.Max(val1: wy, val2: 0.0)));
    }
}
