namespace Puck.Text;

/// <summary>
/// Decodes the PNG image referenced or carried by a <see cref="FontAtlas"/> into in-memory RGBA pixels.
/// </summary>
/// <remarks>
/// This is the counterpart to a pre-baked <see cref="FontAtlasLoader"/>: a loaded atlas may only carry an
/// <see cref="FontAtlas.ImagePath"/>, deferring the (comparatively expensive) pixel decode until a caller
/// actually needs to upload the image — for example to a GPU texture.
/// </remarks>
public interface IFontAtlasImageDataLoader {
    /// <summary>Returns the decoded image for <paramref name="atlas"/>, reading and decoding <see cref="FontAtlas.ImagePath"/> when <see cref="FontAtlas.ImageData"/> is not already in memory.</summary>
    /// <param name="atlas">The atlas whose image is decoded.</param>
    /// <returns>The atlas's decoded image, validated against the atlas's declared dimensions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The decoded image's dimensions disagree with the atlas metadata.</exception>
    FontAtlasImageData Load(FontAtlas atlas);
    /// <summary>Decodes a PNG byte buffer directly, without reference to a <see cref="FontAtlas"/>.</summary>
    /// <param name="imageIdentifier">A stable identifier for the image, used only in error messages.</param>
    /// <param name="pngBytes">The raw PNG file bytes.</param>
    /// <returns>The decoded image.</returns>
    /// <exception cref="ArgumentException"><paramref name="imageIdentifier"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="InvalidDataException"><paramref name="pngBytes"/> is not a supported PNG.</exception>
    FontAtlasImageData Load(string imageIdentifier, ReadOnlyMemory<byte> pngBytes);
}
