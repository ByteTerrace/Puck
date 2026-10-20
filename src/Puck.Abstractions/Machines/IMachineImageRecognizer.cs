namespace Puck.Abstractions.Machines;

/// <summary>An engine capability: judging a raw content image by the image's own header, never by its file name. A
/// host lists a folder's files for a machine through it, offering a file because its bytes are an image the engine's
/// machines run, whatever its extension, and naming why any other file is refused.</summary>
public interface IMachineImageRecognizer {
    /// <summary>Gets how many leading bytes of an image <see cref="RefuseImage"/> reads.</summary>
    int ImageHeaderLength { get; }

    /// <summary>Returns why a raw image is not one this engine's machines run: not the engine's format at all, or a
    /// format the engine does not implement (a mapper, a board).</summary>
    /// <param name="header">The image's leading bytes: <see cref="ImageHeaderLength"/> of them, or the whole image when
    /// it is shorter.</param>
    /// <param name="imageLength">The whole image's length in bytes.</param>
    /// <returns>The reason, or <see langword="null"/> when the engine's machines run the image.</returns>
    string? RefuseImage(ReadOnlySpan<byte> header, long imageLength);
}
