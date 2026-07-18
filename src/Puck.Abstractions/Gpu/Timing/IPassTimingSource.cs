namespace Puck.Abstractions.Gpu;

/// <summary>
/// The neutral per-pass GPU timing READ contract — a frame-driven observer (a benchmark harness, an <c>sdf.info</c>
/// verb) reads the previous readable frame's per-pass GPU milliseconds without naming the engine that produced them.
/// It summarizes the pool/recorder machinery in this namespace: a render engine fills a rotating set of timestamp
/// pools, and this seam surfaces the resolved per-pass span plus the whole-frame span, labeled and sized so a
/// FIXED-COLUMN consumer knows the width up front and an ITERATING consumer walks the labels in order.
/// </summary>
public interface IPassTimingSource {
    /// <summary>The render-pass labels a <see cref="TryReadPassTimings"/> read fills, in order.</summary>
    ReadOnlySpan<string> PassLabels { get; }
    /// <summary>The pass count a read reports — the width a caller sizes its span to.</summary>
    int PassCount { get; }
    /// <summary>Reads the previous readable frame's per-pass GPU milliseconds (pipelined, non-stalling — frame N-2 by
    /// contract). <see langword="false"/> when timing is disarmed, unsupported, or no timestamps landed.</summary>
    /// <param name="passMilliseconds">Receives each pass's milliseconds, in <see cref="PassLabels"/> order; size it to
    /// <see cref="PassCount"/>.</param>
    /// <param name="passCount">The number of pass entries written (0 when the read did not land).</param>
    /// <param name="frameMilliseconds">The whole-frame milliseconds.</param>
    /// <returns>Whether the previous readable frame's marks were readable.</returns>
    bool TryReadPassTimings(Span<double> passMilliseconds, out int passCount, out double frameMilliseconds);
}
