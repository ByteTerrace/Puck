namespace Puck.AdvancedGamingBrick;

/// <summary>
/// An owned <see cref="IBios"/> image supplied by the caller: a zeroed stub, replacement image, or retail dump.
/// This type stores bytes; it does not generate BIOS routines. The image is copied on construction so the
/// source buffer may be reused or discarded.
/// </summary>
public sealed class ReplacementBios : IBios {
    /// <summary>The exact size of the Advanced GamingBrick BIOS, in bytes.</summary>
    public const int ImageSize = (16 * 1024);

    private readonly ReadOnlyMemory<byte> m_image;

    /// <summary>Creates a replacement BIOS from a 16&#160;KiB image.</summary>
    /// <param name="image">The BIOS bytes; must be exactly <see cref="ImageSize"/> long.</param>
    /// <exception cref="ArgumentException"><paramref name="image"/> is not exactly <see cref="ImageSize"/> bytes.</exception>
    public ReplacementBios(ReadOnlySpan<byte> image) {
        if (image.Length != ImageSize) {
            throw new ArgumentException(
                message: $"The BIOS image must be exactly {ImageSize} bytes; got {image.Length}.",
                paramName: nameof(image)
            );
        }

        m_image = image.ToArray();
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Image => m_image;
}
