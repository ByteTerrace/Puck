using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>How a sky layer composes over the colour beneath it: <c>over</c> is a·c + (1 − a)·d, <c>add</c> d + a·c,
/// <c>multiply</c> the colour beneath scaled toward c by a, and <c>screen</c> the colour beneath lifted toward one by
/// a·c. Each is affine in the colour beneath, per channel.</summary>
public enum SdfSkyBlend {
    /// <summary>The layer covers the colour beneath by its alpha.</summary>
    Over,
    /// <summary>The layer adds its colour, weighted by its alpha.</summary>
    Add,
    /// <summary>The layer scales the colour beneath by its colour, weighted by its alpha.</summary>
    Multiply,
    /// <summary>The layer screens the colour beneath with its colour, weighted by its alpha.</summary>
    Screen,
}
/// <summary>The class a sky layer is evaluated in: a field layer is band-limited, so the sky pass evaluates it at the
/// sky's field extent; a point layer has features smaller than a field texel, so the composite evaluates it at each
/// pixel.</summary>
public enum SdfSkyLayerClass {
    /// <summary>A band-limited layer, evaluated at the field extent and summarized with its run.</summary>
    Field,
    /// <summary>A layer of features smaller than a field texel, evaluated at each pixel.</summary>
    Point,
}
/// <summary>One sky layer's value at one direction: its class, its blend, its colour and its alpha.</summary>
/// <param name="Class">Whether the layer is a field or a point layer.</param>
/// <param name="Blend">How it composes over the colour beneath.</param>
/// <param name="Color">Its colour at the direction.</param>
/// <param name="Alpha">Its weight at the direction, in [0, 1].</param>
public readonly record struct SdfSkyLayerSample(SdfSkyLayerClass Class, SdfSkyBlend Blend, Vector3 Color, float Alpha);
/// <summary>One run of a sky's layer stack: a maximal sequence of consecutive field layers, summarized as the one affine
/// map d' = Scale·d + Offset it applies to the colour beneath, or the point layers between two field runs, which the
/// composite evaluates one by one.</summary>
/// <param name="Field">Whether the run is a field run.</param>
/// <param name="Scale">A field run's per-channel scale; one for a point run.</param>
/// <param name="Offset">A field run's per-channel offset; zero for a point run.</param>
/// <param name="Points">A point run's layers in their authored order; none for a field run.</param>
public sealed record SdfSkyRun(bool Field, Vector3 Scale, Vector3 Offset, IReadOnlyList<SdfSkyLayerSample> Points);
/// <summary>
/// The CPU reference for how the sky and composite passes compose a sky's layer stack (rendering plan P18-5). The stack
/// is cut into runs without reordering it; each field run is summarized exactly as the affine map it applies to the
/// colour beneath, since every blend is affine in that colour and a composition of affine maps is affine; and the
/// composite walks the runs in the authored order, applying each field run's map and each point run's layers. The result
/// is the stack's one ordered evaluation, so stars beneath clouds are dimmed by them as authored. The kernels evaluate
/// today's stack, the gradient, the disc and the stars, then the clouds, through the same three runs
/// (<c>shade/sdf-sky.hlsli</c>).
/// </summary>
public static class SdfSkyRuns {
    /// <summary>Returns the affine map one layer applies to the colour beneath it.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>Its per-channel scale and offset.</returns>
    public static (Vector3 Scale, Vector3 Offset) Affine(SdfSkyLayerSample layer) {
        var a = layer.Alpha;
        var c = layer.Color;

        return layer.Blend switch {
            SdfSkyBlend.Over => (new Vector3(value: (1f - a)), (a * c)),
            SdfSkyBlend.Add => (Vector3.One, (a * c)),
            SdfSkyBlend.Multiply => (((Vector3.One - (a * Vector3.One)) + (a * c)), Vector3.Zero),
            SdfSkyBlend.Screen => ((Vector3.One - (a * c)), (a * c)),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(layer), actualValue: layer.Blend, message: "Not a sky blend."),
        };
    }
    /// <summary>Evaluates a stack in its authored order, one layer at a time, over the colour beneath it.</summary>
    /// <param name="stack">The layers, lowest first.</param>
    /// <param name="beneath">The colour beneath the lowest layer.</param>
    /// <returns>The colour the stack shows.</returns>
    public static Vector3 Evaluate(IReadOnlyList<SdfSkyLayerSample> stack, Vector3 beneath) {
        ArgumentNullException.ThrowIfNull(argument: stack);

        var color = beneath;

        foreach (var layer in stack) {
            var (scale, offset) = Affine(layer: layer);

            color = ((scale * color) + offset);
        }

        return color;
    }
    /// <summary>Cuts a stack into its runs, in the authored order, summarizing each field run as one affine map.</summary>
    /// <param name="stack">The layers, lowest first.</param>
    /// <returns>The runs, lowest first.</returns>
    public static IReadOnlyList<SdfSkyRun> Runs(IReadOnlyList<SdfSkyLayerSample> stack) {
        ArgumentNullException.ThrowIfNull(argument: stack);

        var runs = new List<SdfSkyRun>();
        var index = 0;

        while (index < stack.Count) {
            var field = (stack[index].Class == SdfSkyLayerClass.Field);
            var start = index;

            while ((index < stack.Count) && ((stack[index].Class == SdfSkyLayerClass.Field) == field)) {
                index++;
            }

            if (!field) {
                runs.Add(item: new SdfSkyRun(Field: false, Offset: Vector3.Zero, Points: [.. stack.Skip(count: start).Take(count: (index - start))], Scale: Vector3.One));
                continue;
            }

            // Applying (M1, B1) and then (M2, B2) is d -> M2·(M1·d + B1) + B2: the scale M2·M1 and the offset M2·B1 + B2.
            var scale = Vector3.One;
            var offset = Vector3.Zero;

            for (var layer = start; (layer < index); layer++) {
                var (layerScale, layerOffset) = Affine(layer: stack[layer]);

                scale = (layerScale * scale);
                offset = ((layerScale * offset) + layerOffset);
            }

            runs.Add(item: new SdfSkyRun(Field: true, Offset: offset, Points: [], Scale: scale));
        }

        return runs;
    }
    /// <summary>Composites runs over the colour beneath, walking them in their authored order: a field run applies its
    /// map, a point run its layers one by one.</summary>
    /// <param name="runs">The runs, lowest first.</param>
    /// <param name="beneath">The colour beneath the lowest run.</param>
    /// <returns>The colour the runs show.</returns>
    public static Vector3 Composite(IReadOnlyList<SdfSkyRun> runs, Vector3 beneath) {
        ArgumentNullException.ThrowIfNull(argument: runs);

        var color = beneath;

        foreach (var run in runs) {
            color = (run.Field ? ((run.Scale * color) + run.Offset) : Evaluate(beneath: color, stack: run.Points));
        }

        return color;
    }
}
