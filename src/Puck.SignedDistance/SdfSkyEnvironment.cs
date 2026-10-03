using System.Numerics;
using Puck.SignedDistance.Illumination;

namespace Puck.SignedDistance;

/// <summary>
/// The CPU reference for the sky's environment: the map and its coefficients a residency renders once per change of its
/// sky's gradient, which the composite reads the fog's in-scattered colour from. The map is <see cref="Size"/> by
/// <see cref="Size"/> texels over the octahedral projection the radiance cache's maps use, the pole at +y
/// (<see cref="IrradianceLattice.Encode"/>), so the upper hemisphere is the inner diamond and the lower one folds into
/// the corners; each texel holds the gradient at its centre's direction (<see cref="Gradient"/>), the layer the fog
/// in-scatters, as four half floats (<see cref="Quantize"/>). No body, star or cloud enters it, so a bright disc never
/// smears into the fog around it. A lookup (<see cref="Sample"/>) filters the four texels about a
/// direction bilinearly, a tap one texel past an edge read from the texel the octahedral fold puts there
/// (<see cref="IrradianceLattice.BorderSource(int, int, int)"/>), so the filter is continuous across every edge. The
/// coefficients are the nine real second-order spherical harmonics of the map per colour channel (<see cref="Basis"/>),
/// each texel weighted by its solid angle (<see cref="SolidAngle"/>) and the sum scaled so the weights total 4π
/// (<see cref="Project"/>), so a constant sky projects to its colour times √(4π) in the first coefficient and zero in
/// the rest. The kernels are <c>shade/sdf-sky-environment.hlsli</c>, <c>passes/sdf-sky-environment.comp.hlsl</c>,
/// <c>passes/sdf-sky-environment-reduce.comp.hlsl</c> and the composite's lookup in <c>passes/sdf-sky-pass.hlsli</c>.
/// </summary>
public static class SdfSkyEnvironment {
    /// <summary>The map's texels along each axis. KEEP IN SYNC with <c>SdfSkyEnvironmentSize</c> in
    /// <c>shade/sdf-sky-environment.hlsli</c>.</summary>
    public const int Size = 64;
    /// <summary>The map's texels, each one sky evaluation when the map renders.</summary>
    public const int Texels = (Size * Size);
    /// <summary>The bytes one texel takes: four half floats, the colour and a zero.</summary>
    public const int TexelBytes = 8;
    /// <summary>The bytes the map takes.</summary>
    public const int MapBytes = (Texels * TexelBytes);
    /// <summary>The coefficients per colour channel: the real spherical harmonics of bands zero to two.</summary>
    public const int CoefficientCount = 9;
    /// <summary>The bytes the coefficients take: one four-float record each, the three channels and a zero.</summary>
    public const int CoefficientBytes = (CoefficientCount * 16);
    /// <summary>The bytes a residency keeps for its sky's environment: the map and its coefficients, one pair.</summary>
    public const int PayloadBytes = (MapBytes + CoefficientBytes);
    /// <summary>The largest value a texel channel holds, the largest finite half float.</summary>
    public const float MaxRadiance = 65504f;

    // The real spherical harmonics' normalizations, bands zero to two.
    private const double Band0 = 0.28209479177387814;
    private const double Band1 = 0.48860251190291992;
    private const double Band2Cross = 1.0925484305920792;
    private const double Band2Square = 0.54627421529603959;
    private const double Band2Zonal = 0.31539156525252005;

    /// <summary>Returns the unit direction at a texel's centre.</summary>
    /// <param name="x">The texel's column, in [0, <see cref="Size"/>).</param>
    /// <param name="y">The texel's row, in [0, <see cref="Size"/>).</param>
    /// <returns>The direction.</returns>
    public static Vector3 Direction(int x, int y) {
        var direction = IrradianceLattice.Decode(
            u: ((x + 0.5) / Size),
            v: ((y + 0.5) / Size)
        );

        return new Vector3(x: ((float)direction.X), y: ((float)direction.Y), z: ((float)direction.Z));
    }
    /// <summary>Returns a texel's solid angle in steradians: its area in the projection, (2 / <see cref="Size"/>)², times
    /// the cube of the L1 norm of its centre's unit direction, the projection's exact density there, since the
    /// octahedron's point in a unit direction d lies at distance 1 / |d|₁.</summary>
    /// <param name="x">The texel's column.</param>
    /// <param name="y">The texel's row.</param>
    /// <returns>The solid angle.</returns>
    public static double SolidAngle(int x, int y) {
        var direction = IrradianceLattice.Decode(
            u: ((x + 0.5) / Size),
            v: ((y + 0.5) / Size)
        );
        var norm = ((Math.Abs(value: direction.X) + Math.Abs(value: direction.Y)) + Math.Abs(value: direction.Z));
        var side = (2.0 / Size);

        return ((side * side) * ((norm * norm) * norm));
    }
    /// <summary>Writes the nine real spherical harmonics of bands zero to two at a unit direction, in the world's own axes:
    /// Y₀₀; Y₁₋₁ (y), Y₁₀ (z), Y₁₁ (x); Y₂₋₂ (xy), Y₂₋₁ (yz), Y₂₀ (3z² − 1), Y₂₁ (xz), Y₂₂ (x² − y²).</summary>
    /// <param name="direction">The unit direction.</param>
    /// <param name="basis">Receives the nine values, at least <see cref="CoefficientCount"/> long.</param>
    public static void Basis(Vector3 direction, Span<double> basis) {
        double x = direction.X, y = direction.Y, z = direction.Z;

        basis[0] = Band0;
        basis[1] = (Band1 * y);
        basis[2] = (Band1 * z);
        basis[3] = (Band1 * x);
        basis[4] = ((Band2Cross * x) * y);
        basis[5] = ((Band2Cross * y) * z);
        basis[6] = (Band2Zonal * (((3.0 * z) * z) - 1.0));
        basis[7] = ((Band2Cross * x) * z);
        basis[8] = (Band2Square * ((x * x) - (y * y)));
    }
    /// <summary>Projects a map onto the nine harmonics per channel: the solid-angle-weighted sum over every texel of its
    /// colour times each harmonic at its centre, scaled by 4π over the weights' sum, so the weights total the sphere
    /// exactly.</summary>
    /// <param name="map">The map, <see cref="Texels"/> colours, row after row.</param>
    /// <param name="coefficients">Receives the coefficients, at least <see cref="CoefficientCount"/> long.</param>
    /// <exception cref="ArgumentException"><paramref name="map"/> holds other than <see cref="Texels"/> colours.</exception>
    public static void Project(ReadOnlySpan<Vector3> map, Span<Vector3> coefficients) {
        if (map.Length != Texels) {
            throw new ArgumentException(message: $"A map holds {Texels} texels; {map.Length} were given.", paramName: nameof(map));
        }

        Span<double> basis = stackalloc double[CoefficientCount];
        Span<double> sums = stackalloc double[(CoefficientCount * 3)];
        var total = 0.0;

        sums.Clear();
        for (var y = 0; (y < Size); y++) {
            for (var x = 0; (x < Size); x++) {
                var weight = SolidAngle(x: x, y: y);
                var color = map[((y * Size) + x)];

                Basis(basis: basis, direction: Direction(x: x, y: y));
                total += weight;
                for (var index = 0; (index < CoefficientCount); index++) {
                    var scale = (weight * basis[index]);

                    sums[(index * 3)] += (scale * color.X);
                    sums[((index * 3) + 1)] += (scale * color.Y);
                    sums[((index * 3) + 2)] += (scale * color.Z);
                }
            }
        }

        var normalization = ((4.0 * Math.PI) / total);

        for (var index = 0; (index < CoefficientCount); index++) {
            coefficients[index] = new Vector3(
                x: ((float)(sums[(index * 3)] * normalization)),
                y: ((float)(sums[((index * 3) + 1)] * normalization)),
                z: ((float)(sums[((index * 3) + 2)] * normalization))
            );
        }
    }
    /// <summary>Returns the map's colour in a direction: the bilinear filter of the four texels about its point, a tap one
    /// texel past an edge read where the octahedral fold puts it.</summary>
    /// <param name="map">The map, <see cref="Texels"/> colours, row after row.</param>
    /// <param name="direction">The unit direction.</param>
    /// <returns>The colour.</returns>
    public static Vector3 Sample(ReadOnlySpan<Vector3> map, Vector3 direction) {
        var (u, v) = IrradianceLattice.Encode(direction: new Double3(X: direction.X, Y: direction.Y, Z: direction.Z));
        var positionX = ((u * Size) - 0.5);
        var positionY = ((v * Size) - 0.5);
        var originX = ((int)Math.Floor(d: positionX));
        var originY = ((int)Math.Floor(d: positionY));
        var fractionX = ((float)(positionX - originX));
        var fractionY = ((float)(positionY - originY));
        var color = Vector3.Zero;

        for (var corner = 0; (corner < 4); corner++) {
            var dx = corner & 1;
            var dy = (corner >> 1);
            var weight = (((dx == 0) ? (1f - fractionX) : fractionX) * ((dy == 0) ? (1f - fractionY) : fractionY));

            var (x, y) = IrradianceLattice.BorderSource(
                borderedX: ((originX + dx) + 1),
                borderedY: ((originY + dy) + 1),
                interiorTexels: Size
            );

            color += (weight * map[((y * Size) + x)]);
        }

        return color;
    }
    /// <summary>Returns the colour a texel holds for a field-run colour: each channel clamped to [0,
    /// <see cref="MaxRadiance"/>] and rounded to a half float.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The colour the texel holds.</returns>
    public static Vector3 Quantize(Vector3 color) => new(
        x: ((float)((Half)Math.Clamp(max: MaxRadiance, min: 0f, value: color.X))),
        y: ((float)((Half)Math.Clamp(max: MaxRadiance, min: 0f, value: color.Y))),
        z: ((float)((Half)Math.Clamp(max: MaxRadiance, min: 0f, value: color.Z)))
    );
    /// <summary>Renders a sky's map: each texel the gradient at its centre's direction, as the texel holds it
    /// (<see cref="Quantize"/>). The disc, the stars and the clouds never enter it.</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="stops">Its stops table.</param>
    /// <param name="map">Receives the map, <see cref="Texels"/> colours, row after row.</param>
    /// <exception cref="ArgumentException"><paramref name="map"/> holds other than <see cref="Texels"/> colours.</exception>
    public static void Render(in SdfSkyBlock block, ReadOnlySpan<SdfSkyStop> stops, Span<Vector3> map) {
        if (map.Length != Texels) {
            throw new ArgumentException(message: $"A map holds {Texels} texels; {map.Length} were given.", paramName: nameof(map));
        }

        for (var y = 0; (y < Size); y++) {
            for (var x = 0; (x < Size); x++) {
                map[((y * Size) + x)] = Quantize(color: Gradient(direction: Direction(x: x, y: y), stopCount: block.StopCount, stops: stops));
            }
        }
    }
    /// <summary>Returns the sky's gradient in a direction, the lowest field run: the stops piecewise-linear in its
    /// elevation, clamped to the end stops (<c>sdfSkyGradient</c>).</summary>
    /// <param name="stops">The stops table.</param>
    /// <param name="stopCount">The stops in use; zero while the gradient is muted.</param>
    /// <param name="direction">The unit direction.</param>
    /// <returns>The colour.</returns>
    public static Vector3 Gradient(ReadOnlySpan<SdfSkyStop> stops, uint stopCount, Vector3 direction) {
        if (stopCount == 0u) { return Vector3.Zero; }
        var previous = stops[0];

        if (direction.Y <= previous.Elevation) {
            return previous.Color;
        }

        for (var index = 1; (index < stopCount); index++) {
            var next = stops[index];

            if (direction.Y <= next.Elevation) {
                var t = Math.Clamp(value: ((direction.Y - previous.Elevation) / MathF.Max(x: (next.Elevation - previous.Elevation), y: 1e-5f)), max: 1f, min: 0f);

                return Vector3.Lerp(amount: t, value1: previous.Color, value2: next.Color);
            }

            previous = next;
        }

        return previous.Color;
    }
    /// <summary>Returns whether two packed skies draw the same map: the same stops in use. Everything else the block
    /// carries (the fog's density, the disc, the stars and their twinkle, the clouds, the studio horizon) leaves the map
    /// as it is.</summary>
    /// <param name="block">One sky block.</param>
    /// <param name="stops">Its stops table.</param>
    /// <param name="otherBlock">The other sky block.</param>
    /// <param name="otherStops">Its stops table.</param>
    /// <returns><see langword="true"/> when a map rendered from one stands for the other.</returns>
    public static bool SameMap(in SdfSkyBlock block, ReadOnlySpan<SdfSkyStop> stops, in SdfSkyBlock otherBlock, ReadOnlySpan<SdfSkyStop> otherStops) {
        if (block.StopCount != otherBlock.StopCount) {
            return false;
        }

        var used = Math.Min(val1: ((int)Math.Min(val1: block.StopCount, val2: SdfSky.MaxStops)), val2: Math.Min(val1: stops.Length, val2: otherStops.Length));

        return stops[..used].SequenceEqual(other: otherStops[..used]);
    }
}
