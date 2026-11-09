using System.Numerics;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>
/// A probe's hit buffer and the angular first-hit surface support derived from it, with the receiver visibility rule
/// shared with the views shader.
/// <para>One closest-hit ray per octahedral texel, at the trace's full reach, is the probe's whole geometric
/// measurement (<see cref="TraceHits"/>): the radiance cubes are shaded from those hits under whatever lighting holds
/// (<see cref="SdfMeshIrradiance"/>'s relight), and the bounded surface map views test receivers against is the same
/// hits cut at the receiver support radius (<see cref="Surfaces"/>), so a lighting change re-shades and never re-traces,
/// and no second ray set measures visibility.</para>
/// </summary>
public static class SdfMeshProbeDepth {
    private const int Size = SdfMeshIrradianceProbe.DepthSize;

    /// <summary>The hits one probe holds: one a texel.</summary>
    public const int HitCount = SdfMeshProbeTrace.HitCount;
    /// <summary>The words one probe's hit buffer takes: per texel, the FP32 ray distance (negative for a back face,
    /// positive infinity for a miss), then the hit's material in the low sixteen bits and its two signed octahedral normal
    /// bytes, turned toward the probe, in the high sixteen, as a surface word packs them.</summary>
    public const int HitWords = SdfMeshProbeTrace.HitWords;
    /// <summary>The material a hit records when the scene's exceeds sixteen bits; it shades as the default material.</summary>
    public const int UnknownMaterial = SdfMeshProbeTrace.UnknownMaterial;

    // The low bit of a texel's curvature half: its hit is on a sheet (Surfaces). A reader masks it off before decoding the
    // curvature. KEEP IN SYNC with sdfMeshProbeDepthVisibility.
    private const uint SheetBit = 1u;
    // The sheet query's radius as a share of the depth it runs at: short of the hit's own surface, half a thickness away,
    // by a margin the packed normal's error stays inside.
    private const float SheetQueryShare = 0.9f;

    private static readonly Vector3[] HitDirections = BuildDirections();
    private static readonly float[] HitWeights = BuildWeights();

    // The surface map's directions are the hit buffer's.
    private static Vector3[] Directions => HitDirections;

    /// <summary>Gets each texel's unit ray direction, the octahedral texel's centre.</summary>
    public static ReadOnlySpan<Vector3> RayDirections => HitDirections;
    /// <summary>Gets each texel's solid angle in steradians, summing to four pi: the quadrature weight a cube integrates
    /// the texel's hit with, since octahedral texels are not of equal area.</summary>
    public static ReadOnlySpan<float> RayWeights => HitWeights;

    /// <summary>Traces one closest hit per texel out to a reach and writes the probe's hit buffer.</summary>
    /// <param name="scene">The existing static mesh hierarchy.</param>
    /// <param name="origin">The probe position.</param>
    /// <param name="maximum">The rays' reach, in metres.</param>
    /// <param name="hits">Receives <see cref="HitWords"/> words.</param>
    /// <param name="backFaces">Receives how many rays met a back face.</param>
    /// <returns>The number of closest-hit rays issued.</returns>
    public static int TraceHits(SdfMeshIrradianceScene scene, Vector3 origin, float maximum, Span<uint> hits, out int backFaces) {
        ArgumentNullException.ThrowIfNull(scene);
        backFaces = 0;
        for (var texel = 0; (texel < HitCount); texel++) {
            var direction = HitDirections[texel];

            if (!scene.Intersect(direction: direction, distance: out var distance, material: out var material, maximum: maximum, normal: out var normal, origin: origin)) {
                hits[(2 * texel)] = BitConverter.SingleToUInt32Bits(value: float.PositiveInfinity);
                hits[((2 * texel) + 1)] = 0u;
                continue;
            }

            var back = (Vector3.Dot(vector1: normal, vector2: direction) > 0f);

            if (back) {
                normal = -normal;
                backFaces++;
            }
            hits[(2 * texel)] = BitConverter.SingleToUInt32Bits(value: (back ? -distance : distance));
            hits[((2 * texel) + 1)] = PackNormal(normal: normal) | ((uint)(((material >= 0) && (material < UnknownMaterial)) ? material : UnknownMaterial));
        }
        return HitCount;
    }
    /// <summary>Reads one texel's hit.</summary>
    /// <param name="hits">A probe's hit buffer.</param>
    /// <param name="texel">The texel.</param>
    /// <param name="distance">Receives the ray distance to the hit.</param>
    /// <param name="normal">Receives the hit's unit normal, turned toward the probe (a front face's outward normal).</param>
    /// <param name="material">Receives the hit's material, <see cref="UnknownMaterial"/> when it was out of range.</param>
    /// <param name="backFace">Receives whether the ray met a back face.</param>
    /// <returns><see langword="false"/> for a miss.</returns>
    public static bool TryHit(ReadOnlySpan<uint> hits, int texel, out float distance, out Vector3 normal, out int material, out bool backFace) {
        var signed = BitConverter.UInt32BitsToSingle(value: hits[(2 * texel)]);
        var word = hits[((2 * texel) + 1)];

        backFace = (signed < 0f);
        distance = MathF.Abs(x: signed);
        material = ((int)(word & 0xFFFFu));
        if (float.IsPositiveInfinity(f: signed)) {
            normal = Vector3.Zero;
            return false;
        }
        normal = Normal(word: word);
        return true;
    }
    /// <summary>Traces a probe's hits out to the receiver support radius and derives its surface map, as
    /// <see cref="TraceHits"/> and <see cref="Surfaces"/> do together.</summary>
    /// <param name="scene">The existing static mesh hierarchy.</param>
    /// <param name="origin">The relocated probe position.</param>
    /// <param name="reach">The receiver support radius, in metres.</param>
    /// <param name="surfaces">Receives the packed surface map.</param>
    /// <param name="sheets">Whether the map marks the hits on sheets, as a coarse level's does.</param>
    /// <returns>The number of closest-hit rays issued.</returns>
    public static int Trace(SdfMeshIrradianceScene scene, Vector3 origin, float reach, Span<uint> surfaces, bool sheets) {
        Span<uint> hits = stackalloc uint[HitWords];
        var rays = TraceHits(backFaces: out _, hits: hits, maximum: reach, origin: origin, scene: scene);

        Surfaces(hits: hits, origin: origin, reach: reach, scene: scene, sheets: sheets, surfaces: surfaces);
        return rays;
    }
    // A unit normal's two signed octahedral bytes, in a surface word's high sixteen bits.
    private static uint PackNormal(Vector3 normal) {
        var uv = SdfOctahedral.Encode(direction: new Double3(X: normal.X, Y: normal.Y, Z: normal.Z));
        var scale = SdfMeshIrradianceProbe.DepthNormalScale;
        var x = ((int)Math.Round(a: (uv.X * scale)));
        var y = ((int)Math.Round(a: (uv.Y * scale)));

        return (((uint)((byte)((sbyte)x))) << 16) | (((uint)((byte)((sbyte)y))) << 24);
    }
    /// <summary>Derives the surface map views test receivers against from a probe's hit buffer: each texel's hit within
    /// the receiver support radius as an FP16 ray distance beside its two signed octahedral normal bytes, a negative
    /// distance marking a miss within that reach, then one FP16 normal-gradient curvature per texel, whose low bit marks a
    /// hit on a sheet: a surface the scene holds no solid behind thicker than
    /// <see cref="SdfMeshIrradianceProbe.DepthSheetThickness"/> (a slab, a wall), measured as any surface within 0.45 of
    /// that thickness of the point half of it behind the hit. The near side of a solid (a hill, a plinth) reads no surface
    /// there but its own, half the thickness away; a slab's top, its underside or its edge does.</summary>
    /// <param name="scene">The static mesh hierarchy the hits were traced against.</param>
    /// <param name="origin">The probe position the hits were traced from.</param>
    /// <param name="hits">The probe's hit buffer, traced at least as far as <paramref name="reach"/>.</param>
    /// <param name="reach">The receiver support radius, in metres.</param>
    /// <param name="surfaces">Receives the packed surface map.</param>
    /// <param name="sheets">Whether the map marks the hits on sheets (<see cref="SdfMeshIrradianceLevel.Sheets"/>); when
    /// false, every hit reads as a solid's near side.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scene"/> is <see langword="null"/>.</exception>
    public static void Surfaces(SdfMeshIrradianceScene scene, Vector3 origin, ReadOnlySpan<uint> hits, float reach, Span<uint> surfaces, bool sheets) {
        ArgumentNullException.ThrowIfNull(scene);
        Span<Vector3> points = stackalloc Vector3[(Size * Size)];
        Span<Vector3> normals = stackalloc Vector3[(Size * Size)];
        Span<bool> sheet = stackalloc bool[(Size * Size)];
        var depth = (0.5f * SdfMeshIrradianceProbe.DepthSheetThickness);

        sheet.Clear();
        surfaces.Slice(start: (Size * Size)).Clear();

        for (var texel = 0; (texel < Directions.Length); texel++) {
            var direction = Directions[texel];
            var distance = MathF.Abs(x: BitConverter.UInt32BitsToSingle(value: hits[(2 * texel)]));

            if (!(distance <= reach)) {
                surfaces[texel] = BitConverter.HalfToUInt16Bits(value: ((Half)(-reach)));
                points[texel] = normals[texel] = Vector3.Zero;
                continue;
            }
            var packed = (hits[((2 * texel) + 1)] & 0xFFFF0000u);

            surfaces[texel] = packed | BitConverter.HalfToUInt16Bits(value: ((Half)distance));
            points[texel] = (direction * ((float)((Half)distance)));
            normals[texel] = Normal(word: packed);
            // The exact hit, then the point half a sheet's thickness behind it along the normal turned toward the probe.
            sheet[texel] = (sheets && scene.TryNearestSurface(bakeHalfDiagonal: out _, normal: out _, point: ((origin + (direction * distance)) - (normals[texel] * depth)),
                radius: (SheetQueryShare * depth), surface: out _));
        }
        // Fit normal gradients once from the texel's local angular neighbourhood. Even a gather with only one
        // hit tap can then distinguish a plane from curvature; a lone hit in the whole neighbourhood is unsupported.
        for (var texel = 0; (texel < Directions.Length); texel++) {
            var variance = 0f;
            var covariance = 0f;

            if (normals[texel] != Vector3.Zero) {
                for (var dy = -1; (dy <= 1); dy++) {
                    for (var dx = -1; (dx <= 1); dx++) {
                        var source = SdfOctahedral.BorderSource(borderedX: (((texel % Size) + dx) + 1), borderedY: (((texel / Size) + dy) + 1), interiorTexels: Size);
                        var neighbour = (source.X + (Size * source.Y));

                        if ((neighbour == texel) || (normals[neighbour] == Vector3.Zero)) { continue; }
                        var delta = (points[neighbour] - points[texel]);

                        if ((Vector3.Dot(vector1: normals[texel], vector2: normals[neighbour]) <= 0f) ||
                            (MathF.Abs(x: Vector3.Dot(vector1: (normals[texel] + normals[neighbour]), vector2: delta)) > SdfMeshIrradianceProbe.DepthSurfaceJoinGap)) { continue; }
                        variance += delta.LengthSquared();
                        covariance += Vector3.Dot(vector1: delta, vector2: (normals[neighbour] - normals[texel]));
                    }
                }
            }
            var curvature = ((variance > 1e-8f) ? (covariance / variance) : float.NaN);
            var bits = ((((uint)BitConverter.HalfToUInt16Bits(value: ((Half)curvature))) & ~SheetBit) | (sheet[texel] ? SheetBit : 0u));

            surfaces[(Directions.Length + (texel >> 1))] |= (bits << (16 * (texel & 1)));
        }
    }

    private static Vector3 Normal(uint word) {
        var normal = SdfOctahedral.Decode(x: (((sbyte)((byte)(word >> 16))) / ((double)SdfMeshIrradianceProbe.DepthNormalScale)),
            y: (((sbyte)((byte)(word >> 24))) / ((double)SdfMeshIrradianceProbe.DepthNormalScale)));

        return new Vector3(x: ((float)normal.X), y: ((float)normal.Y), z: ((float)normal.Z));
    }

    /// <summary>Tests signed surface gaps only in the four reflected angular taps. Coplanar receivers stay visible
    /// at grazing angles. A missed ray supplies no fictitious far blocker. KEEP IN SYNC with
    /// <c>sdfMeshProbeDepthVisibility</c>; there are no cross-probe or whole-sphere plane vetoes.</summary>
    /// <param name="surfaces">The packed surface map of one probe.</param>
    /// <param name="toward">Receiver minus probe position, after normal bias.</param>
    /// <param name="bounded">Whether a receiver past a measured miss's reach is unsupported (zero); when false, only
    /// measured surfaces block, so a receiver past every probe's reach is extrapolated wherever no measured surface lies
    /// before it.</param>
    /// <returns>Visibility from zero to one, the taps' blocked shares closed by <see cref="SdfMeshProbeCoverage"/>.</returns>
    public static float Visibility(ReadOnlySpan<uint> surfaces, Vector3 toward, bool bounded = true) {
        var distance = toward.Length();

        if (!(distance > 0f)) { return 1f; }
        var uv = SdfOctahedral.Encode(direction: new Double3(X: toward.X, Y: toward.Y, Z: toward.Z));
        var texel = ((((new Vector2(x: ((float)uv.X), y: ((float)uv.Y)) * 0.5f) + new Vector2(value: 0.5f)) * Size) - new Vector2(value: 0.5f));
        var x = ((int)MathF.Floor(x: texel.X));
        var y = ((int)MathF.Floor(x: texel.Y));
        var fraction = (texel - new Vector2(x: x, y: y));
        Span<Vector3> points = stackalloc Vector3[4];
        Span<Vector3> normals = stackalloc Vector3[4];
        Span<float> weights = stackalloc float[4];
        Span<float> curvatures = stackalloc float[4];
        Span<bool> sheets = stackalloc bool[4];
        var firstCode = uint.MaxValue;
        var firstNormal = Vector3.Zero;
        var planar = true;
        var planeLow = float.PositiveInfinity;
        var planeHigh = float.NegativeInfinity;
        var planeSum = 0f;
        var planeWeight = 0f;
        var sheetWeight = 0f;

        weights.Clear();

        for (var corner = 0; (corner < 4); corner++) {
            var highX = corner & 1;
            var highY = (corner >> 1);
            var weight = (((highX != 0) ? fraction.X : (1f - fraction.X)) * ((highY != 0) ? fraction.Y : (1f - fraction.Y)));

            if (weight <= 0f) { continue; }
            var tap = SdfOctahedral.BorderSource(borderedX: ((x + highX) + 1), borderedY: ((y + highY) + 1), interiorTexels: Size);
            var index = (tap.X + (Size * tap.Y));
            var word = surfaces[index];
            var depth = ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)word)));

            if (depth < 0f) {
                if (bounded && (distance > -depth)) { return 0f; }
                continue;
            }
            points[corner] = (Directions[index] * depth);
            var packedCurvature = ((ushort)(surfaces[(Directions.Length + (index >> 1))] >> (16 * (index & 1))));
            var curvature = ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(packedCurvature & ~SheetBit))));

            if (!float.IsFinite(f: curvature)) { continue; }
            sheets[corner] = ((packedCurvature & SheetBit) != 0u);
            sheetWeight += (sheets[corner] ? weight : 0f);
            var code = (word >> 16);

            if (firstCode == uint.MaxValue) {
                firstCode = code;
                firstNormal = Normal(word: word);
            }
            normals[corner] = ((code == firstCode) ? firstNormal : Normal(word: word));
            curvatures[corner] = curvature;
            weights[corner] = weight;
            planar &= ((curvature == 0f) && (code == firstCode));
            var plane = Vector3.Dot(vector1: firstNormal, vector2: points[corner]);

            planeLow = MathF.Min(x: planeLow, y: plane);
            planeHigh = MathF.Max(x: planeHigh, y: plane);
            planeSum += (weight * plane);
            planeWeight += weight;
        }
        // This is the exact planar form of the quadratic. Flat walls dominate CPU bounce reads and pixels.
        if (planar && (planeWeight > 0f) && ((planeHigh - planeLow) <= (0.5f * SdfMeshIrradianceProbe.DepthSurfaceJoinGap))) {
            var gap = (Vector3.Dot(vector1: firstNormal, vector2: toward) - (planeSum / planeWeight));
            var closed = Math.Clamp(max: 1f, min: 0f, value: (-gap / SdfMeshIrradianceProbe.DepthSurfaceFade));

            return SdfMeshProbeCoverage.Visibility(blocked: (planeWeight * closed), sheets: (sheetWeight * closed));
        }
        var blocked = 0f;
        var blockedSheets = 0f;

        for (var corner = 0; (corner < 4); corner++) {
            if (weights[corner] <= 0f) { continue; }
            var quadratic = 0f;
            var linear = Vector3.Zero;
            var constant = 0f;
            var total = 0f;

            for (var neighbour = 0; (neighbour < 4); neighbour++) {
                if (weights[neighbour] <= 0f) { continue; }
                var sum = (normals[corner] + normals[neighbour]);

                if ((Vector3.Dot(vector1: normals[corner], vector2: normals[neighbour]) <= 0f) ||
                    (MathF.Abs(x: Vector3.Dot(vector1: sum, vector2: (points[neighbour] - points[corner]))) > SdfMeshIrradianceProbe.DepthSurfaceJoinGap)) { continue; }
                var curvature = curvatures[neighbour];
                var point = points[neighbour];
                var normal = normals[neighbour];

                quadratic += ((weights[neighbour] * 0.5f) * curvature);
                linear += (weights[neighbour] * (normal - (curvature * point)));
                constant += (weights[neighbour] * (-Vector3.Dot(vector1: normal, vector2: point) + ((0.5f * curvature) * point.LengthSquared())));
                total += weights[neighbour];
            }
            quadratic /= total;
            linear /= total;
            constant /= total;
            var a = (quadratic * toward.LengthSquared());
            var b = Vector3.Dot(vector1: linear, vector2: toward);
            var c = constant;
            var discriminant = ((b * b) - ((4f * a) * c));

            if ((c <= 0f) || (discriminant <= 0f)) { continue; }
            var divisor = (MathF.Sqrt(x: discriminant) - b);

            if (divisor <= 1e-8f) { continue; }
            var along = ((2f * c) / divisor);

            if ((along <= 0f) || (along >= 1f)) { continue; }
            var hit = (toward * along);
            var gradient = (linear + ((2f * quadratic) * hit));
            var gap = Vector3.Dot(vector1: Vector3.Normalize(value: gradient), vector2: (toward - hit));

            var share = (weights[corner] * Math.Clamp(max: 1f, min: 0f, value: (-gap / SdfMeshIrradianceProbe.DepthSurfaceFade)));

            blocked += share;
            blockedSheets += (sheets[corner] ? share : 0f);
        }
        return SdfMeshProbeCoverage.Visibility(blocked: blocked, sheets: blockedSheets);
    }

    /// <summary>Writes the sky's mean radiance over each hit texel's solid angle: what a ray of that texel that misses every
    /// surface carries, so a probe's open rays integrate the sky they see rather than a share of its whole-hemisphere
    /// irradiance. Each texel averages <see cref="SkySubsamples"/> squared directions of its octahedral cell, each weighted by
    /// its solid angle, from the sky's environment map.</summary>
    /// <param name="map">The sky environment map, <see cref="SdfSkyEnvironment.Texels"/> colours, row after row, in nits.</param>
    /// <param name="texels">Receives one colour per hit texel, in nits; at least <see cref="HitCount"/> long.</param>
    public static void SkyRadiance(ReadOnlySpan<Vector3> map, Span<Vector3> texels) {
        for (var y = 0; (y < Size); y++) {
            for (var x = 0; (x < Size); x++) {
                var sum = Vector3.Zero;
                var weights = 0.0;

                for (var sy = 0; (sy < SkySubsamples); sy++) {
                    for (var sx = 0; (sx < SkySubsamples); sx++) {
                        var direction = SdfOctahedral.Decode(x: ((((x + ((sx + 0.5) / SkySubsamples)) * 2.0) / Size) - 1.0), y: ((((y + ((sy + 0.5) / SkySubsamples)) * 2.0) / Size) - 1.0));
                        var norm = ((Math.Abs(value: direction.X) + Math.Abs(value: direction.Y)) + Math.Abs(value: direction.Z));
                        var weight = ((norm * norm) * norm);

                        sum += (SdfSkyEnvironment.Sample(direction: new Vector3(x: ((float)direction.X), y: ((float)direction.Y), z: ((float)direction.Z)), map: map) * ((float)weight));
                        weights += weight;
                    }
                }
                texels[(x + (Size * y))] = (sum / ((float)weights));
            }
        }
    }
    /// <summary>The sky samples along each axis of a hit texel's octahedral cell that <see cref="SkyRadiance"/> averages.</summary>
    public const int SkySubsamples = 4;

    private static Vector3[] BuildDirections() {
        var directions = new Vector3[(Size * Size)];

        for (var y = 0; (y < Size); y++) {
            for (var x = 0; (x < Size); x++) {
                var direction = SdfOctahedral.Decode(x: ((((x + 0.5) * 2.0) / Size) - 1.0), y: ((((y + 0.5) * 2.0) / Size) - 1.0));

                directions[(x + (Size * y))] = new Vector3(x: ((float)direction.X), y: ((float)direction.Y), z: ((float)direction.Z));
            }
        }
        return directions;
    }
    // Each texel's solid angle: the spherical area of its sub-cells, each split into two spherical triangles
    // (Van Oosterom and Strackee's formula). A sub-cell a fold line crosses is measured across the fold, a small error
    // in so fine a subdivision, and the whole is rescaled to the sphere.
    private static float[] BuildWeights() {
        const int Steps = 16;
        var weights = new float[(Size * Size)];
        var total = 0.0;

        for (var y = 0; (y < Size); y++) {
            for (var x = 0; (x < Size); x++) {
                var area = 0.0;

                for (var sy = 0; (sy < Steps); sy++) {
                    for (var sx = 0; (sx < Steps); sx++) {
                        var u0 = (((x + (sx / ((double)Steps))) * 2.0 / Size) - 1.0);
                        var u1 = (((x + ((sx + 1) / ((double)Steps))) * 2.0 / Size) - 1.0);
                        var v0 = (((y + (sy / ((double)Steps))) * 2.0 / Size) - 1.0);
                        var v1 = (((y + ((sy + 1) / ((double)Steps))) * 2.0 / Size) - 1.0);
                        var a = SdfOctahedral.Decode(x: u0, y: v0);
                        var b = SdfOctahedral.Decode(x: u1, y: v0);
                        var c = SdfOctahedral.Decode(x: u1, y: v1);
                        var d = SdfOctahedral.Decode(x: u0, y: v1);

                        area += (Triangle(a: a, b: b, c: c) + Triangle(a: a, b: c, c: d));
                    }
                }
                weights[(x + (Size * y))] = ((float)area);
                total += area;
            }
        }
        for (var texel = 0; (texel < weights.Length); texel++) { weights[texel] = ((float)((weights[texel] * (4.0 * Math.PI)) / total)); }
        return weights;
    }
    private static double Triangle(Double3 a, Double3 b, Double3 c) {
        static double Dot(Double3 p, Double3 q) => (((p.X * q.X) + (p.Y * q.Y)) + (p.Z * q.Z));
        var triple = ((a.X * ((b.Y * c.Z) - (b.Z * c.Y))) + (a.Y * ((b.Z * c.X) - (b.X * c.Z)))) + (a.Z * ((b.X * c.Y) - (b.Y * c.X)));
        var denominator = (((1.0 + Dot(p: a, q: b)) + Dot(p: b, q: c)) + Dot(p: c, q: a));

        return Math.Abs(value: (2.0 * Math.Atan2(y: triple, x: denominator)));
    }
}
