namespace Puck.Platform;

/// <summary>Normalizes camera rows from a driver-reported stride and orientation into tightly packed, top-down pixels.
/// Platform capture backends use this before publishing the <see cref="ICameraPixelStream"/> contract.</summary>
public static class CameraFramePacking {
    /// <summary>Copies BGRA8 rows into a tightly packed, top-down destination.</summary>
    /// <param name="source">The driver buffer.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="sourceStride">The signed driver stride; zero means tightly packed and top-down, while a negative
    /// value identifies bottom-up row order.</param>
    /// <param name="destination">The tightly packed destination.</param>
    /// <returns>Whether both buffers and the stride describe a complete frame.</returns>
    public static bool TryPackBgra(ReadOnlySpan<byte> source, int width, int height, int sourceStride, Span<byte> destination) =>
        TryCopyRows(
            bytesPerPixel: 4,
            destination: destination,
            height: height,
            source: source,
            sourceStride: sourceStride,
            width: width
        );
    /// <summary>Expands L8 rows into tightly packed, top-down opaque BGRA8 while summing luminance.</summary>
    /// <param name="source">The driver buffer.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="sourceStride">The signed driver stride; zero means tightly packed and top-down, while a negative
    /// value identifies bottom-up row order.</param>
    /// <param name="destination">The tightly packed BGRA8 destination.</param>
    /// <param name="luminanceSum">When this returns <see langword="true"/>, the sum of all L8 samples.</param>
    /// <returns>Whether both buffers and the stride describe a complete frame.</returns>
    public static bool TryExpandLuminance(ReadOnlySpan<byte> source, int width, int height, int sourceStride, Span<byte> destination, out long luminanceSum) {
        luminanceSum = 0L;

        if (!TryGetLayout(
            bytesPerPixel: 1,
            destinationBytesPerPixel: 4,
            destinationLength: destination.Length,
            height: height,
            sourceLength: source.Length,
            sourceStride: sourceStride,
            absoluteStride: out var absoluteStride,
            tightSourceRowBytes: out var tightSourceRowBytes,
            width: width
        )) {
            return false;
        }

        for (var row = 0; (row < height); row++) {
            var sourceRow = ((sourceStride < 0) ? ((height - 1) - row) : row);
            var sourcePixels = source.Slice(length: tightSourceRowBytes, start: (sourceRow * absoluteStride));
            var destinationOffset = ((row * width) * 4);

            for (var column = 0; (column < width); column++) {
                var luminance = sourcePixels[column];
                var offset = (destinationOffset + (column * 4));

                destination[offset] = luminance;
                destination[(offset + 1)] = luminance;
                destination[(offset + 2)] = luminance;
                destination[(offset + 3)] = 0xFF;
                luminanceSum += luminance;
            }
        }

        return true;
    }

    private static bool TryCopyRows(ReadOnlySpan<byte> source, int width, int height, int sourceStride, int bytesPerPixel, Span<byte> destination) {
        if (!TryGetLayout(
            bytesPerPixel: bytesPerPixel,
            destinationBytesPerPixel: bytesPerPixel,
            destinationLength: destination.Length,
            height: height,
            sourceLength: source.Length,
            sourceStride: sourceStride,
            absoluteStride: out var absoluteStride,
            tightSourceRowBytes: out var tightSourceRowBytes,
            width: width
        )) {
            return false;
        }

        for (var row = 0; (row < height); row++) {
            var sourceRow = ((sourceStride < 0) ? ((height - 1) - row) : row);

            source.Slice(length: tightSourceRowBytes, start: (sourceRow * absoluteStride)).CopyTo(
                destination: destination.Slice(length: tightSourceRowBytes, start: (row * tightSourceRowBytes))
            );
        }

        return true;
    }
    private static bool TryGetLayout(int width, int height, int sourceStride, int sourceLength, int destinationLength, int bytesPerPixel, int destinationBytesPerPixel, out int absoluteStride, out int tightSourceRowBytes) {
        absoluteStride = 0;
        tightSourceRowBytes = 0;

        if ((width <= 0) || (height <= 0) || (sourceStride == int.MinValue)) {
            return false;
        }

        var tightSourceRowBytes64 = (((long)width) * bytesPerPixel);
        var destinationLength64 = ((((long)width) * height) * destinationBytesPerPixel);

        if ((tightSourceRowBytes64 > int.MaxValue) || (destinationLength64 > destinationLength)) {
            return false;
        }

        tightSourceRowBytes = ((int)tightSourceRowBytes64);
        absoluteStride = ((0 == sourceStride) ? tightSourceRowBytes : Math.Abs(value: sourceStride));

        if (absoluteStride < tightSourceRowBytes) {
            return false;
        }

        var requiredSourceLength = ((((long)(height - 1)) * absoluteStride) + tightSourceRowBytes);

        return (requiredSourceLength <= sourceLength);
    }
}
