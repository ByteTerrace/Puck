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

/// <summary>A maximal consecutive run in the active, authored sky stack.</summary>
/// <param name="Class">The evaluation class shared by the run's layers.</param>
/// <param name="FirstLayer">The first layer's index in the active stack.</param>
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

/// <summary>Partitions resolved, active sky layers without changing their authored order.</summary>
public static class SdfSkyRuns {
    /// <summary>Writes maximal runs of equal evaluation class. The caller filters inactive layers before this
    /// operation; this helper neither resolves fields nor decides opacity, visibility or quality.</summary>
    /// <param name="layers">The active layer classes in authored order.</param>
    /// <param name="runs">Caller-owned storage for the worst case of one run per layer.</param>
    /// <returns>The number of initialized entries in <paramref name="runs"/>.</returns>
    /// <exception cref="ArgumentException">The destination cannot hold one run per layer.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A layer names an undeclared evaluation class.</exception>
    public static int Write(ReadOnlySpan<SdfSkyLayerClass> layers, Span<SdfSkyRun> runs) {
        if (runs.Length < layers.Length) {
            throw new ArgumentException("Sky run storage must hold one run per active layer.", nameof(runs));
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
