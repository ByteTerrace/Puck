using System.Numerics;
using Puck.SignedDistance.Illumination;

namespace Puck.SignedDistance;

/// <summary>
/// The CPU reference for the sky's environment: the map and its coefficients a residency renders once per change of the
/// layers its lighting sees, which the composite reads the fog's in-scattered colour from. The map is <see cref="Size"/>
/// by <see cref="Size"/> texels over the octahedral projection the radiance cache's maps use, the pole at world +y
/// (<see cref="IrradianceLattice.Encode"/>), so the upper hemisphere is the inner diamond and the lower one folds into
/// the corners; each texel holds the layers the lighting sees at its centre's direction (<see cref="Evaluate"/>), as four
/// half floats (<see cref="Quantize"/>). No disc enters it, so a bright body never smears into the fog around it, and a
/// layer only the camera sees (stars and clouds by default) is left out. A lookup (<see cref="Sample"/>) filters the
/// four texels about a
/// direction bilinearly, a tap one texel past an edge read from the texel the octahedral fold puts there
/// (<see cref="IrradianceLattice.BorderSource(int, int, int)"/>), so the filter is continuous across every edge. The
/// coefficients are the nine real second-order spherical harmonics of the map per colour channel (<see cref="Basis"/>),
/// each texel weighted by its solid angle (<see cref="SolidAngle"/>) and the sum scaled so the weights total 4π
/// (<see cref="Project"/>), so a constant sky projects to its colour times √(4π) in the first coefficient and zero in
/// the rest. The kernels are <c>shade/sdf-sky-environment.hlsli</c>, <c>passes/sdf-sky-environment.comp.hlsl</c>,
/// <c>passes/sdf-sky-environment-reduce.comp.hlsl</c> and the composite's lookup in <c>passes/sdf-sky-pass.hlsli</c>.
/// </summary>
public static partial class SdfSkyEnvironment {
    /// <summary>The map's texels along each axis. KEEP IN SYNC with <c>SdfSkyEnvironmentSize</c> in
    /// <c>shade/sdf-sky-environment.hlsli</c>.</summary>
    public const int Size = 64;
    /// <summary>The map's texels, each one sky evaluation when the map renders.</summary>
    public const int Texels = (Size * Size);
    /// <summary>The bytes one texel takes: four half floats, the colour and a zero.</summary>
    public const int TexelBytes = 8;
    /// <summary>The bytes of the full and panel-free map planes.</summary>
    public const int MapBytes = ((2 * Texels) * TexelBytes);
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
                var color = (map[((y * Size) + x)] - map[0]);

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
        coefficients[0] += (map[0] * ((float)Math.Sqrt((4d * Math.PI))));
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
    /// <summary>Renders a sky's map: each texel the layers the lighting sees, composed over black in their authored order
    /// at its centre's direction, as the texel holds it (<see cref="Quantize"/>). A disc never enters it. The reference
    /// evaluates every lighting-capable kind and the stack's composition: the sky frame, each layer's
    /// rotation, mask, opacity and blend.</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="layers">Its layer table.</param>
    /// <param name="map">Receives the map, <see cref="Texels"/> colours, row after row.</param>
    /// <exception cref="ArgumentException"><paramref name="map"/> holds other than <see cref="Texels"/> colours.</exception>
    /// <exception cref="NotSupportedException">A layer the lighting sees is of a kind the reference does not evaluate.</exception>
    public static void Render(in SdfSkyBlock block, ReadOnlySpan<SdfSkyLayer> layers, Span<Vector3> map) {
        if (map.Length != Texels) {
            throw new ArgumentException(message: $"A map holds {Texels} texels; {map.Length} were given.", paramName: nameof(map));
        }

        for (var y = 0; (y < Size); y++) {
            for (var x = 0; (x < Size); x++) {
                map[((y * Size) + x)] = Quantize(color: Evaluate(block: in block, direction: Direction(x: x, y: y), layers: layers));
            }
        }
    }
    /// <summary>Returns the colour the lighting sees in a world direction: the layers the lighting sees, but a disc,
    /// composed over black in their authored order (<c>sdfSkyEnvironmentColor</c>).</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="layers">Its layer table.</param>
    /// <param name="direction">The unit world direction.</param>
    /// <returns>The colour.</returns>
    /// <exception cref="NotSupportedException">A layer the lighting sees is of a kind the reference does not evaluate.</exception>
    public static Vector3 Evaluate(in SdfSkyBlock block, ReadOnlySpan<SdfSkyLayer> layers, Vector3 direction) {
        var sky = FrameDirection(block: in block, direction: direction);
        var color = Vector3.Zero;
        var count = Math.Min(val1: ((int)block.LayerCount), val2: layers.Length);

        for (var index = 0; (index < count); index++) {
            var layer = layers[index];

            if (!IsLit(layer: in layer)) {
                continue;
            }

            var weight = (layer.Opacity * MaskWeight(direction: sky, layer: in layer));

            if (weight <= 0f) {
                continue;
            }

            var local = Rotate(direction: sky, rotation: layer.Rotation);
            Vector3 radiance;

            if (layer.Kind == SdfSkyLayerKind.Panel) {
                var value = Panel(panel: SdfSky.PayloadOf<SdfSkyPanel>(layer: ref layer), direction: local);

                radiance = new Vector3(value.X, value.Y, value.Z);
                weight *= value.W;
            } else if (layer.Kind == SdfSkyLayerKind.Gradient) {
                radiance = Gradient(direction: local, gradient: SdfSky.PayloadOf<SdfSkyGradient>(layer: ref layer));
            } else {
                var value = Procedural(ref layer, block, local);

                radiance = new Vector3(value.X, value.Y, value.Z);
                weight *= value.W;
            }

            var (scale, offset) = SdfSkyRuns.Affine(layer: new SdfSkyLayerSample(
                Alpha: weight,
                Blend: layer.Blend,
                Class: SdfSkyLayerClass.Field,
                Color: radiance
            ));

            color = ((scale * color) + offset);
        }

        return color;
    }
    /// <summary>Returns a world direction in the sky frame: its components along the block's frame axes.</summary>
    /// <param name="block">The packed sky block.</param>
    /// <param name="direction">The world direction.</param>
    /// <returns>The sky-frame direction.</returns>
    public static Vector3 FrameDirection(in SdfSkyBlock block, Vector3 direction) => new(
        x: Vector3.Dot(vector1: direction, vector2: block.FrameRight),
        y: Vector3.Dot(vector1: direction, vector2: block.FrameUp),
        z: Vector3.Dot(vector1: direction, vector2: block.FrameForward)
    );
    /// <summary>Returns a direction rotated by a layer's unit quaternion, v + 2·q × (q × v + w·v).</summary>
    /// <param name="direction">The direction.</param>
    /// <param name="rotation">The quaternion (x, y, z, w).</param>
    /// <returns>The rotated direction.</returns>
    public static Vector3 Rotate(Vector3 direction, Vector4 rotation) {
        var axis = new Vector3(x: rotation.X, y: rotation.Y, z: rotation.Z);

        return (direction + (2f * Vector3.Cross(vector1: axis, vector2: (Vector3.Cross(vector1: axis, vector2: direction) + (rotation.W * direction)))));
    }
    /// <summary>Returns a layer's mask weight at a sky-frame direction, in <c>[0, 1]</c>.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="direction">The sky-frame direction.</param>
    /// <returns>The weight.</returns>
    public static float MaskWeight(in SdfSkyLayer layer, Vector3 direction) {
        var soft = layer.MaskSoftness;

        return layer.Mask switch {
            SdfSkyMask.Elevation => (Rise(edge: layer.MaskBand.X, soft: soft, value: direction.Y) * (1f - Rise(edge: (layer.MaskBand.Y + soft), soft: soft, value: direction.Y))),
            SdfSkyMask.Cone => Rise(edge: layer.MaskBand.W, soft: soft, value: Vector3.Dot(vector1: direction, vector2: new Vector3(x: layer.MaskBand.X, y: layer.MaskBand.Y, z: layer.MaskBand.Z))),
            _ => 1f,
        };
    }
    /// <summary>Returns a gradient's colour in a layer-frame direction: the stops piecewise-linear in its height, clamped
    /// to the end stops (the <c>gradient</c> kind's module).</summary>
    /// <param name="gradient">The gradient.</param>
    /// <param name="direction">The unit layer-frame direction.</param>
    /// <returns>The colour.</returns>
    public static Vector3 Gradient(in SdfSkyGradient gradient, Vector3 direction) {
        var previous = gradient.Stop(index: 0);

        if (direction.Y <= previous.Elevation) {
            return previous.Color;
        }

        var count = Math.Min(val1: ((int)gradient.Count), val2: SdfSky.MaxStops);

        for (var index = 1; (index < count); index++) {
            var next = gradient.Stop(index: index);

            if (direction.Y <= next.Elevation) {
                var t = Math.Clamp(value: ((direction.Y - previous.Elevation) / MathF.Max(x: (next.Elevation - previous.Elevation), y: 1e-5f)), max: 1f, min: 0f);

                return Vector3.Lerp(amount: t, value1: previous.Color, value2: next.Color);
            }

            previous = next;
        }

        return previous.Color;
    }
    /// <summary>Returns whether two packed skies draw the same map: the same frame, the same quality tier, and the same
    /// layers the lighting sees, but discs, in the same order. Everything else (the fog's density, every layer only the
    /// camera sees, a disc, the environment gains) leaves the map as it is.</summary>
    /// <param name="block">One sky block.</param>
    /// <param name="layers">Its layer table.</param>
    /// <param name="otherBlock">The other sky block.</param>
    /// <param name="otherLayers">Its layer table.</param>
    /// <returns><see langword="true"/> when a map rendered from one stands for the other.</returns>
    public static bool SameMap(in SdfSkyBlock block, ReadOnlySpan<SdfSkyLayer> layers, in SdfSkyBlock otherBlock, ReadOnlySpan<SdfSkyLayer> otherLayers) {
        if (
            (block.FrameRight != otherBlock.FrameRight) ||
            (block.FrameUp != otherBlock.FrameUp) ||
            (block.FrameForward != otherBlock.FrameForward) ||
            (block.Quality != otherBlock.Quality)
        ) {
            return false;
        }

        var count = Math.Min(val1: ((int)block.LayerCount), val2: layers.Length);
        var otherCount = Math.Min(val1: ((int)otherBlock.LayerCount), val2: otherLayers.Length);
        var other = 0;

        for (var index = 0; (index <= count); index++) {
            while ((index < count) && !IsLit(layer: in layers[index])) {
                index++;
            }
            while ((other < otherCount) && !IsLit(layer: in otherLayers[other])) {
                other++;
            }

            var done = (index >= count);
            var otherDone = (other >= otherCount);

            if (done || otherDone) {
                return (done && otherDone);
            }
            if ((layers[index] with { Detail = 0u, Visibility = SdfSkyVisibility.Lighting }) != (otherLayers[other] with { Detail = 0u, Visibility = SdfSkyVisibility.Lighting })) {
                return false;
            }

            other++;
        }

        return true;
    }
    /// <summary>Returns whether the environment map draws a layer: the lighting sees it and it is not a disc.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns><see langword="true"/> when the map draws it.</returns>
    public static bool IsLit(in SdfSkyLayer layer) =>
        (((layer.Visibility & SdfSkyVisibility.Lighting) != 0) && (layer.Kind != SdfSkyLayerKind.Disc));

    // A step from zero below an edge to one at it, widened downward over a soft width (smoothstep's cubic).
    private static float Rise(float edge, float soft, float value) {
        if (!(soft > 0f)) {
            return ((value >= edge) ? 1f : 0f);
        }

        var t = Math.Clamp(max: 1f, min: 0f, value: ((value - (edge - soft)) / soft));

        return ((t * t) * (3f - (2f * t)));
    }
}
