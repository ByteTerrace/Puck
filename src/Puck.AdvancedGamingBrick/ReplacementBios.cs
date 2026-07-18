namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The default <see cref="IBios"/>: an open-source replacement BIOS image, supplied as bytes. It keeps the
/// emulator self-contained and legally clean; for full hardware accuracy a real dumped BIOS can be registered
/// in its place. The image is copied on construction so the source buffer may be reused or discarded.
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
            throw new ArgumentException(message: $"The BIOS image must be exactly {ImageSize} bytes; got {image.Length}.", paramName: nameof(image));
        }

        m_image = image.ToArray();
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Image => m_image;
}
