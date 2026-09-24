namespace Puck.Abstractions.Sources;

/// <summary>A deterministic source that can state the exact image it shows: the pixels its conversion pass must produce
/// for its current stamp, which a verdict holds a read-back of the converted image to before composition.</summary>
public interface IImageSourceReference {
    /// <summary>Gets the source's descriptor; its content class is <see cref="ImageContentClass.Deterministic"/>.</summary>
    ImageSourceDescriptor Descriptor { get; }

    /// <summary>Writes the image the source currently shows as tightly packed RGBA8, red first, row by row.</summary>
    /// <param name="rgba">The destination; at least four bytes per pixel of the descriptor's extent.</param>
    /// <param name="stamp">The stamp of the image written, or <see langword="default"/> when there is none.</param>
    /// <returns><see langword="true"/> when an image was written; <see langword="false"/> before the first image.</returns>
    bool TryWriteReference(Span<byte> rgba, out ImageSourceStamp stamp);
}
/// <summary>The outcome of an exact pixel verdict (<see cref="ImageSourceVerdict.Compare"/>).</summary>
/// <param name="Producer">The producer whose image was judged.</param>
/// <param name="Width">The image's width in pixels.</param>
/// <param name="Height">The image's height in pixels.</param>
/// <param name="Mismatches">How many pixels differ in any channel.</param>
/// <param name="FirstX">The column of the first differing pixel in row order, or -1 when none differs.</param>
/// <param name="FirstY">The row of the first differing pixel, or -1 when none differs.</param>
/// <param name="Expected">The first differing pixel's expected RGBA8, packed red in the low byte; zero when none differs.</param>
/// <param name="Actual">The first differing pixel's read-back RGBA8, packed the same way; zero when none differs.</param>
public readonly record struct ImageSourceVerdictResult(string Producer, uint Width, uint Height, long Mismatches, int FirstX, int FirstY, uint Expected, uint Actual) {
    /// <summary>Gets whether every pixel matched exactly.</summary>
    public bool Holds => (Mismatches == 0L);

    /// <inheritdoc/>
    public override string ToString() => (Holds
        ? $"{Producer} {Width}x{Height} exact"
        : $"{Producer} {Width}x{Height} {Mismatches} pixel(s) differ; first at ({FirstX}, {FirstY}) expected #{Expected:X8} read #{Actual:X8}"
    );
}
/// <summary>
/// The exact pixel verdict a deterministic source's image gets before composition: the read-back of its converted image
/// against the pixels the source states it shows (<see cref="IImageSourceReference"/>). The verdict tolerates no
/// difference, which is stricter than the per-tile verdict the composed frame gets; an external or presentation source
/// has no reference and is refused.
/// </summary>
public static class ImageSourceVerdict {
    /// <summary>Compares a read-back of a deterministic source's converted image to the pixels it states it shows.</summary>
    /// <param name="descriptor">The source's descriptor.</param>
    /// <param name="expected">The source's reference image, RGBA8, as <see cref="IImageSourceReference.TryWriteReference"/>
    /// writes it.</param>
    /// <param name="actual">The read-back of the converted image, RGBA8, tightly packed at the descriptor's extent.</param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="descriptor"/> is not
    /// <see cref="ImageContentClass.Deterministic"/>, or either image is not four bytes per pixel of its extent.</exception>
    public static ImageSourceVerdictResult Compare(ImageSourceDescriptor descriptor, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) {
        ArgumentNullException.ThrowIfNull(argument: descriptor);

        if (descriptor.Content != ImageContentClass.Deterministic) {
            throw new ArgumentException(
                message: $"Producer '{descriptor.Producer}' is {descriptor.Content} content; only a deterministic source has an exact verdict.",
                paramName: nameof(descriptor)
            );
        }

        var length = checked(((((long)descriptor.Width) * descriptor.Height) * 4L));

        if (
            (expected.Length != length) ||
            (actual.Length != length)
        ) {
            throw new ArgumentException(
                message: $"Producer '{descriptor.Producer}' is {descriptor.Width}x{descriptor.Height}, {length} bytes; the reference holds {expected.Length} and the read-back {actual.Length}.",
                paramName: nameof(actual)
            );
        }

        var mismatches = 0L;
        var first = -1;

        for (var offset = 0; (offset < expected.Length); offset += 4) {
            if (!expected.Slice(
                length: 4,
                start: offset
            ).SequenceEqual(other: actual.Slice(
                length: 4,
                start: offset
            ))) {
                mismatches++;

                if (first < 0) {
                    first = (offset / 4);
                }
            }
        }

        if (first < 0) {
            return new ImageSourceVerdictResult(
                Actual: 0U,
                Expected: 0U,
                FirstX: -1,
                FirstY: -1,
                Height: descriptor.Height,
                Mismatches: 0L,
                Producer: descriptor.Producer,
                Width: descriptor.Width
            );
        }

        return new ImageSourceVerdictResult(
            Actual: System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source: actual[(first * 4)..]),
            Expected: System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source: expected[(first * 4)..]),
            FirstX: ((int)(first % descriptor.Width)),
            FirstY: ((int)(first / descriptor.Width)),
            Height: descriptor.Height,
            Mismatches: mismatches,
            Producer: descriptor.Producer,
            Width: descriptor.Width
        );
    }
}
