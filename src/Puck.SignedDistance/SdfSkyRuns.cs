namespace Puck.SignedDistance;

/// <summary>Where an already resolved sky layer is evaluated.</summary>
public enum SdfSkyLayerClass : byte {
    /// <summary>A band-limited layer summarized at the sky field extent.</summary>
    Field,
    /// <summary>An analytic layer evaluated at each composite pixel.</summary>
    Point,
    /// <summary>An instance image sampled at each composite pixel.</summary>
    Screen,
}

/// <summary>A maximal consecutive run in the authored sky stack. Runtime gates never change its storage.</summary>
/// <param name="Class">The evaluation class shared by the run's layers.</param>
/// <param name="FirstLayer">The first layer's structural index in the authored stack.</param>
/// <param name="LayerCount">The number of consecutive layers.</param>
public readonly record struct SdfSkyRun(SdfSkyLayerClass Class, int FirstLayer, int LayerCount) {
    /// <summary>The number of field summary images: offset alone for a field run at the bottom of the stack,
    /// scale and offset for a field run above another layer, and none for point or screen runs.</summary>
    public int SummaryImageCount => Class == SdfSkyLayerClass.Field ? FirstLayer == 0 ? 1 : 2 : 0;

    /// <summary>The field texels written by one refresh at the supplied extent. This is a structural gauge,
    /// not a cumulative work counter. An unrefreshed run writes no texels.</summary>
    /// <param name="width">The field extent's width.</param>
    /// <param name="height">The field extent's height.</param>
    /// <returns>The number of texels across the run's summary images.</returns>
    public ulong SummaryTexelWrites(uint width, uint height) => checked((ulong)SummaryImageCount * width * height);
}

/// <summary>Partitions authored sky layers once when the stack's structure changes, without reordering them.</summary>
public static class SdfSkyRuns {
    /// <summary>Writes maximal runs of equal evaluation class. All authored layers participate, including currently
    /// inactive layers. Opacity, visibility and quality gate evaluations within these fixed runs; changing a gate
    /// never reallocates a run image or recompiles a pipeline.</summary>
    /// <param name="layers">The structural layer classes in authored order.</param>
    /// <param name="runs">Caller-owned storage for the worst case of one run per layer.</param>
    /// <returns>The number of initialized entries in <paramref name="runs"/>.</returns>
    /// <exception cref="ArgumentException">The destination cannot hold one run per layer.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A layer names an undeclared evaluation class.</exception>
    public static int Write(ReadOnlySpan<SdfSkyLayerClass> layers, Span<SdfSkyRun> runs) {
        if (runs.Length < layers.Length) {
            throw new ArgumentException("Sky run storage must hold one run per authored layer.", nameof(runs));
        }
        var count = 0;
        for (var first = 0; first < layers.Length;) {
            var kind = layers[first];
            if (kind is not (SdfSkyLayerClass.Field or SdfSkyLayerClass.Point or SdfSkyLayerClass.Screen)) {
                throw new ArgumentOutOfRangeException(nameof(layers), $"Sky layer {first} has undeclared evaluation class {(byte)kind}.");
            }
            var end = first + 1;
            while (end < layers.Length && layers[end] == kind) { end++; }
            runs[count++] = new(kind, first, end - first);
            first = end;
        }
        return count;
    }
}
