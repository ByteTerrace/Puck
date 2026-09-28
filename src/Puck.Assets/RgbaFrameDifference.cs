namespace Puck.Assets;

/// <summary>The visible difference between two equally sized, tightly packed RGBA8 frames. Alpha is ignored.</summary>
/// <param name="ChangedPixels">The number of pixels whose largest RGB channel delta reaches <see cref="MinChangedDelta"/>.</param>
/// <param name="MaxDelta">The largest RGB channel delta, in 8-bit channel units, including changes below the noise floor.</param>
public readonly record struct RgbaFrameDifference(long ChangedPixels, int MaxDelta) {
    /// <summary>Gets the mean absolute RGB channel delta in 8-bit channel units; alpha is ignored.</summary>
    public double MeanAbsoluteDelta { get; init; }

    /// <summary>The smallest RGB channel delta counted as a changed pixel. One channel unit is capture noise.</summary>
    public const int MinChangedDelta = 2;

    /// <summary>Counts visible pixel changes without allocating. Callers must establish matching image dimensions;
    /// this method checks the packed buffers' shape.</summary>
    /// <param name="before">The earlier frame, four bytes per pixel in RGBA order.</param>
    /// <param name="after">The later frame, in the same layout and pixel order.</param>
    /// <returns>The changed-pixel count, greatest RGB delta and mean absolute RGB delta; all zero for empty buffers.</returns>
    /// <exception cref="ArgumentException">The lengths differ or do not contain whole RGBA pixels.</exception>
    public static RgbaFrameDifference Measure(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after) {
        if ((before.Length != after.Length) || ((before.Length % 4) != 0)) {
            throw new ArgumentException(message: "Compared frames must contain the same number of whole RGBA pixels.", paramName: nameof(after));
        }
        var changedPixels = 0L;
        var maxDelta = 0;
        var absoluteSum = 0L;

        for (var index = 0; (index < before.Length); index += 4) {
            var delta = Math.Max(val1: Math.Abs(value: (before[index] - after[index])),
                val2: Math.Max(val1: Math.Abs(value: (before[(index + 1)] - after[(index + 1)])),
                    val2: Math.Abs(value: (before[(index + 2)] - after[(index + 2)]))));

            for (var channel = 0; channel < 3; channel++) {
                absoluteSum += Math.Abs(value: before[index + channel] - after[index + channel]);
            }
            if (delta >= MinChangedDelta) {
                changedPixels++;
            }
            maxDelta = Math.Max(val1: maxDelta, val2: delta);
        }
        return new RgbaFrameDifference(ChangedPixels: changedPixels, MaxDelta: maxDelta) {
            MeanAbsoluteDelta = before.IsEmpty ? 0.0 : (double)absoluteSum / ((before.Length / 4L) * 3L),
        };
    }
}
