namespace Puck.Abstractions.Sources;

/// <summary>The pixel format a source writes. Each value's code is what an uploaded region's header carries
/// (<see cref="ImageSourceUploadLayout"/>), so the values are fixed and zero is never a format.</summary>
public enum ImagePixelFormat : byte {
    /// <summary>Four 8-bit channels, red in the lowest byte of each little-endian word.</summary>
    R8G8B8A8Unorm = 1,
    /// <summary>Four 8-bit channels, blue in the lowest byte of each little-endian word.</summary>
    B8G8R8A8Unorm = 2,
    /// <summary>One 8-bit index per pixel into a 256-entry <see cref="R8G8B8A8Unorm"/> palette.</summary>
    Indexed8 = 3,
    /// <summary>Planar 4:2:0 luma and chroma: a full-resolution 8-bit Y plane, then a half-resolution plane of
    /// interleaved 8-bit Cb and Cr pairs.</summary>
    Nv12 = 4,
    /// <summary>Three 10-bit color channels and a 2-bit alpha, red in the lowest bits of each little-endian word.</summary>
    R10G10B10A2Unorm = 5,
}
/// <summary>The color primaries a source's pixels are expressed in.</summary>
public enum ImageColorPrimaries : byte {
    /// <summary>ITU-R BT.709 primaries, which sRGB shares.</summary>
    Bt709 = 0,
    /// <summary>ITU-R BT.2020 primaries.</summary>
    Bt2020 = 1,
}
/// <summary>The transfer function a source's pixel values are encoded with.</summary>
public enum ImageTransferFunction : byte {
    /// <summary>The sRGB transfer function (IEC 61966-2-1), which display-referred 8-bit content uses.</summary>
    Srgb = 0,
    /// <summary>Linear light: a value is proportional to luminance.</summary>
    Linear = 1,
    /// <summary>The SMPTE ST 2084 perceptual quantizer, which HDR10 content uses; 1 encodes 10,000 cd/m².</summary>
    Pq = 2,
}
/// <summary>The matrix that turns a planar source's Y, Cb and Cr into R'G'B'.</summary>
public enum ImageYuvMatrix : byte {
    /// <summary>ITU-R BT.601: Kr = 0.299, Kb = 0.114.</summary>
    Bt601 = 0,
    /// <summary>ITU-R BT.709: Kr = 0.2126, Kb = 0.0722.</summary>
    Bt709 = 1,
    /// <summary>ITU-R BT.2020 non-constant luminance: Kr = 0.2627, Kb = 0.0593.</summary>
    Bt2020 = 2,
}
/// <summary>The code range a planar source's 8-bit samples use.</summary>
public enum ImageYuvRange : byte {
    /// <summary>Limited (studio) range: Y spans 16 to 235 and Cb, Cr span 16 to 240 about 128.</summary>
    Limited = 0,
    /// <summary>Full range: Y spans 0 to 255 and Cb, Cr span 0 to 255 about 128.</summary>
    Full = 1,
}
/// <summary>How a source's pixel values map to color. The matrix and range apply only to a planar source.</summary>
/// <param name="Primaries">The color primaries.</param>
/// <param name="Transfer">The transfer function.</param>
/// <param name="Matrix">The Y'CbCr matrix a planar source was encoded with.</param>
/// <param name="Range">The code range a planar source's samples use.</param>
public readonly record struct ImageColorEncoding(ImageColorPrimaries Primaries, ImageTransferFunction Transfer, ImageYuvMatrix Matrix = ImageYuvMatrix.Bt709, ImageYuvRange Range = ImageYuvRange.Limited) {
    /// <summary>Gets the encoding of display-referred sRGB content, which every 8-bit RGB source uses.</summary>
    public static ImageColorEncoding Srgb { get; } = new(
        Primaries: ImageColorPrimaries.Bt709,
        Transfer: ImageTransferFunction.Srgb
    );

    /// <summary>Returns the encoding of a planar BT.709-primaries source with the given matrix and range.</summary>
    /// <param name="matrix">The Y'CbCr matrix.</param>
    /// <param name="range">The code range.</param>
    /// <returns>The encoding.</returns>
    public static ImageColorEncoding Yuv(ImageYuvMatrix matrix, ImageYuvRange range) => new(
        Matrix: matrix,
        Primaries: ImageColorPrimaries.Bt709,
        Range: range,
        Transfer: ImageTransferFunction.Srgb
    );
}
