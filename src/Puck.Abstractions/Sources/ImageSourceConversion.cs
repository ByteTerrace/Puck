using System.Buffers.Binary;
using Puck.Abstractions.Presentation;
using Puck.Maths;

namespace Puck.Abstractions.Sources;

/// <summary>
/// The conversion passes that turn an uploaded source's region (<see cref="ImageSourceUploadLayout"/>) into an image a
/// consumer samples, and the CPU reference each pass is held to. Each pass is a shipped HLSL compute kernel in
/// <c>Puck.Shaders</c> (<c>Assets/Shaders/Sources</c>) reading the region as a byte-address buffer named <c>region</c> and
/// writing a storage image named <c>image</c>, both in set 3 where a document pass's interface places its ports (the pass
/// block at binding 0, the region at binding 1, the image at binding 2), eight by eight threads a group, one thread a
/// pixel; the CPU reference here computes the same arithmetic in double precision, so a kernel's output agrees with it
/// to within one 8-bit code.
/// <para>
/// A planar source's chroma is sampled at the co-sited pixel of its half-resolution plane with no filtering, the Y'CbCr
/// matrix and code range are the ones its header names, and the resulting R'G'B' is clamped to 0–1 and not linearized.
/// </para>
/// <para>
/// Every pass writes the working space a frame is drawn in: display-referred values, one at SDR white, which the display
/// encode shows at the paper-white level, with headroom above one. An 8-bit sRGB pass copies its codes, which are already
/// working values. The transfer pass decodes the header's transfer function to linear light relative to the paper-white
/// level (<see cref="ToLinear"/>): an sRGB value is relative to SDR white already, a linear value is scRGB, one at
/// <see cref="DisplayOutput.SdrWhiteNits"/>, and a perceptual-quantizer value is its luminance; it then moves BT.2020
/// primaries to BT.709 and encodes the result with the sRGB curve extended past one and mirrored below zero
/// (<see cref="LinearToWorking"/>), the curve the display encode decodes. So a sample of N cd/m² displays at N cd/m² on an
/// HDR output, nothing above SDR white is clipped before the display encode, and nothing is encoded twice.
/// </para>
/// </summary>
public static class ImageSourceConversion {
    /// <summary>The pass that turns a palette-indexed image into RGBA8.</summary>
    public const string PalettePass = "source-palette";
    /// <summary>The pass that turns an NV12 image into RGBA8.</summary>
    public const string Nv12Pass = "source-nv12";
    /// <summary>The pass that copies an RGBA8 or BGRA8 image into RGBA8.</summary>
    public const string RgbaPass = "source-rgba";
    /// <summary>The pass that decodes an image's transfer function and primaries into working values in a half-float RGBA
    /// image.</summary>
    public const string TransferPass = "source-transfer";
    /// <summary>The pass that converts an imported half-float scRGB image, read as an image on the device rather than
    /// uploaded (a desktop capture of an HDR display copied into shared targets on the GPU), into working values in a
    /// half-float RGBA image: what <see cref="TransferPass"/> writes for the same pixels uploaded in
    /// <see cref="ImagePixelFormat.R16G16B16A16Float"/> under the scRGB encoding.</summary>
    public const string ScRgbImagePass = "source-scrgb";

    // SMPTE ST 2084 constants.
    private const double PqC1 = (3424.0 / 4096.0);
    private const double PqC2 = ((2413.0 / 4096.0) * 32.0);
    private const double PqC3 = ((2392.0 / 4096.0) * 32.0);
    private const double PqM1 = (2610.0 / 16384.0);
    private const double PqM2 = ((2523.0 / 4096.0) * 128.0);
    private const double PqPeakNits = 10_000.0;
    // The sRGB curve's linear segment ends at this linear value, where 12.92 x meets 1.055 x^(1 / 2.4) - 0.055.
    private const double SrgbLinearLimit = 0.0031308;

    // BT.2020 primaries to BT.709, for linear light (ITU-R BT.2407), row by row: the inverse of the display encode's BT.709
    // to BT.2020 (ITU-R BT.2087) to within 1e-6.
    private static ReadOnlySpan<double> Bt2020ToBt709 => [
        1.6604910, -0.5876411, -0.0728499,
        -0.1245505, 1.1328999, -0.0083494,
        -0.0181508, -0.1005789, 1.1187297,
    ];

    private static void RequireLength(int length, uint width, uint height, int channels, string paramName) {
        var required = ((((long)width) * height) * channels);

        if (length < required) {
            throw new ArgumentException(
                message: $"The destination holds {length} elements; a {width}x{height} image needs {required}.",
                paramName: paramName
            );
        }
    }

    /// <summary>Copies tightly packed B8G8R8A8 pixels as R8G8B8A8, swapping red and blue: what <see cref="RgbaPass"/>
    /// writes for a BGRA8 source.</summary>
    /// <param name="bgra">The source pixels, four bytes each.</param>
    /// <param name="rgba">The destination; at least as long as <paramref name="bgra"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="bgra"/> is not whole pixels, or <paramref name="rgba"/> is
    /// shorter than it.</exception>
    public static void BgraToRgba(ReadOnlySpan<byte> bgra, Span<byte> rgba) {
        if (
            ((bgra.Length % 4) != 0) ||
            (rgba.Length < bgra.Length)
        ) {
            throw new ArgumentException(
                message: $"{bgra.Length} source bytes into {rgba.Length} destination bytes is not a whole-pixel copy.",
                paramName: nameof(rgba)
            );
        }

        for (var offset = 0; (offset < bgra.Length); offset += 4) {
            rgba[offset] = bgra[(offset + 2)];
            rgba[(offset + 1)] = bgra[(offset + 1)];
            rgba[(offset + 2)] = bgra[offset];
            rgba[(offset + 3)] = bgra[(offset + 3)];
        }
    }
    /// <summary>Returns the conversion pass a source's format and encoding need.</summary>
    /// <param name="format">The source's pixel format.</param>
    /// <param name="color">The source's color encoding.</param>
    /// <returns><see cref="TransferPass"/> for linear or perceptual-quantizer content,
    /// <see cref="ImagePixelFormat.R10G10B10A2Unorm"/> pixels or <see cref="ImagePixelFormat.R16G16B16A16Float"/> pixels;
    /// otherwise the format's own pass.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined format, or planar or
    /// indexed content names a transfer function other than sRGB.</exception>
    public static string PassOf(ImagePixelFormat format, ImageColorEncoding color) {
        var sdr = (color.Transfer == ImageTransferFunction.Srgb);

        return format switch {
            ImagePixelFormat.R8G8B8A8Unorm => (sdr ? RgbaPass : TransferPass),
            ImagePixelFormat.B8G8R8A8Unorm when sdr => RgbaPass,
            ImagePixelFormat.R10G10B10A2Unorm or ImagePixelFormat.R16G16B16A16Float => TransferPass,
            ImagePixelFormat.Indexed8 when sdr => PalettePass,
            ImagePixelFormat.Nv12 when sdr => Nv12Pass,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: format,
                message: $"No conversion pass reads {format} content with the {color.Transfer} transfer function.",
                paramName: nameof(format)
            ),
        };
    }
    /// <summary>Returns the conversion pass an imported image of a format and encoding needs: one the device reads as an
    /// image, never through an upload's region.</summary>
    /// <param name="format">The image's pixel format.</param>
    /// <param name="color">The image's color encoding.</param>
    /// <returns><see cref="ScRgbImagePass"/> for <see cref="ImagePixelFormat.R16G16B16A16Float"/> scRGB content: linear,
    /// BT.709 primaries.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No conversion reads such an image.</exception>
    public static string ImagePassOf(ImagePixelFormat format, ImageColorEncoding color) => ((
        (format == ImagePixelFormat.R16G16B16A16Float) &&
        (color.Transfer == ImageTransferFunction.Linear) &&
        (color.Primaries == ImageColorPrimaries.Bt709)
    )
        ? ScRgbImagePass
        : throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: $"No conversion pass reads an imported {format} image with the {color.Transfer} transfer function and {color.Primaries} primaries.",
            paramName: nameof(format)
        ));
    /// <summary>Returns the perceptual quantizer's decoded luminance for an encoded value (SMPTE ST 2084).</summary>
    /// <param name="value">The encoded value, 0 to 1.</param>
    /// <returns>The luminance in cd/m², 0 to 10,000.</returns>
    public static double PqToNits(double value) {
        var power = Math.Pow(
            x: Math.Clamp(
                max: 1.0,
                min: 0.0,
                value: value
            ),
            y: (1.0 / PqM2)
        );
        var ratio = (Math.Max(
            val1: (power - PqC1),
            val2: 0.0
        ) / (PqC2 - (PqC3 * power)));

        return (PqPeakNits * Math.Pow(
            x: ratio,
            y: (1.0 / PqM1)
        ));
    }
    /// <summary>Returns the linear value of an sRGB-encoded value (IEC 61966-2-1).</summary>
    /// <param name="value">The encoded value, 0 to 1.</param>
    /// <returns>The linear value, 0 to 1.</returns>
    public static double SrgbToLinear(double value) {
        var clamped = Math.Clamp(
            max: 1.0,
            min: 0.0,
            value: value
        );

        return ((clamped <= 0.04045)
            ? (clamped / 12.92)
            : Math.Pow(
                x: ((clamped + 0.055) / 1.055),
                y: 2.4
            )
        );
    }
    /// <summary>Returns the sRGB-encoded value of a linear value (IEC 61966-2-1), the inverse of
    /// <see cref="SrgbToLinear"/>.</summary>
    /// <param name="value">The linear value, 0 to 1; a value outside it is clamped.</param>
    /// <returns>The encoded value, 0 to 1.</returns>
    public static double LinearToSrgb(double value) => LinearToWorking(linear: Math.Clamp(
        max: 1.0,
        min: 0.0,
        value: value
    ));
    /// <summary>Returns the luma weights of a Y'CbCr matrix: the red and blue shares of luma, green taking the rest.
    /// <c>imageSourceYuvToRgb</c> in <c>image-source.hlsli</c> holds the same literals.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns><c>Kr</c> and <c>Kb</c>: BT.601 0.299 and 0.114, BT.709 0.2126 and 0.0722, BT.2020 0.2627 and
    /// 0.0593.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="matrix"/> is not defined.</exception>
    public static (double Kr, double Kb) Coefficients(ImageYuvMatrix matrix) => matrix switch {
        ImageYuvMatrix.Bt601 => (0.299, 0.114),
        ImageYuvMatrix.Bt709 => (0.2126, 0.0722),
        ImageYuvMatrix.Bt2020 => (0.2627, 0.0593),
        _ => throw new ArgumentOutOfRangeException(
            actualValue: matrix,
            message: "The Y'CbCr matrix is not defined.",
            paramName: nameof(matrix)
        ),
    };
    /// <summary>Derives the four inverse coefficients of a Y'CbCr matrix from its <see cref="Coefficients"/>, for luma
    /// and chroma normalized to their code range's span: the code range sets the offset and span the caller
    /// normalizes by, so the coefficients are the same for limited and full range.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns><c>R = Y + RedFromV · Cr</c>, <c>G = Y - GreenFromU · Cb - GreenFromV · Cr</c> and
    /// <c>B = Y + BlueFromU · Cb</c>, with the weights <c>2 - 2Kr</c>, <c>2Kb(1 - Kb) / Kg</c>,
    /// <c>2Kr(1 - Kr) / Kg</c> and <c>2 - 2Kb</c> for <c>Kg = 1 - Kr - Kb</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="matrix"/> is not defined.</exception>
    public static (double RedFromV, double GreenFromU, double GreenFromV, double BlueFromU) YuvToRgbCoefficients(ImageYuvMatrix matrix) {
        var (kr, kb) = Coefficients(matrix: matrix);
        var kg = ((1.0 - kr) - kb);

        return (
            (2.0 - (2.0 * kr)),
            (((2.0 * kb) * (1.0 - kb)) / kg),
            (((2.0 * kr) * (1.0 - kr)) / kg),
            (2.0 - (2.0 * kb))
        );
    }
    /// <summary>Converts one Y'CbCr sample to R'G'B' under a matrix and code range, unclamped.</summary>
    /// <param name="y">The luma code.</param>
    /// <param name="cb">The blue-difference code.</param>
    /// <param name="cr">The red-difference code.</param>
    /// <param name="matrix">The matrix the sample was encoded with.</param>
    /// <param name="range">The code range the sample uses.</param>
    /// <returns>The normalized R'G'B', which may lie outside 0–1 for a sample outside the gamut.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="matrix"/> or <paramref name="range"/> is not
    /// defined.</exception>
    public static (double R, double G, double B) YuvToRgb(byte y, byte cb, byte cr, ImageYuvMatrix matrix, ImageYuvRange range) {
        var (kr, kb) = Coefficients(matrix: matrix);
        var (luma, blue, red) = range switch {
            ImageYuvRange.Limited => (((y - 16.0) / 219.0), ((cb - 128.0) / 224.0), ((cr - 128.0) / 224.0)),
            ImageYuvRange.Full => ((y / 255.0), ((cb - 128.0) / 255.0), ((cr - 128.0) / 255.0)),
            _ => throw new ArgumentOutOfRangeException(
                actualValue: range,
                message: "The code range is not defined.",
                paramName: nameof(range)
            ),
        };
        var r = (luma + ((2.0 - (2.0 * kr)) * red));
        var b = (luma + ((2.0 - (2.0 * kb)) * blue));
        var g = (((luma - (kr * r)) - (kb * b)) / ((1.0 - kr) - kb));

        return (r, g, b);
    }
    /// <summary>Computes what <see cref="RgbaPass"/>, <see cref="PalettePass"/> or <see cref="Nv12Pass"/> writes for a
    /// region: tightly packed RGBA8, red first, row by row.</summary>
    /// <param name="region">The source's region, header first.</param>
    /// <param name="rgba">The destination; at least four bytes per pixel.</param>
    /// <exception cref="ArgumentException"><paramref name="region"/>'s header is not valid, its format or transfer
    /// function needs <see cref="TransferPass"/>, or <paramref name="rgba"/> is too short.</exception>
    public static void ToRgba8(ReadOnlySpan<byte> region, Span<byte> rgba) {
        var header = ImageSourceUploadLayout.Read(region: region);
        var pass = PassOf(
            color: header.Color,
            format: header.Format
        );

        if (pass == TransferPass) {
            throw new ArgumentException(
                message: $"{header.Format} content with the {header.Color.Transfer} transfer function converts through {TransferPass}.",
                paramName: nameof(region)
            );
        }

        RequireLength(
            channels: 4,
            height: header.Height,
            length: rgba.Length,
            paramName: nameof(rgba),
            width: header.Width
        );

        for (var y = 0U; (y < header.Height); y++) {
            for (var x = 0U; (x < header.Width); x++) {
                var target = rgba.Slice(
                    length: 4,
                    start: checked((int)(((y * header.Width) + x) * 4U))
                );

                switch (header.Format) {
                    case ImagePixelFormat.Indexed8: {
                            var index = region[((int)((header.Plane1Offset + (y * header.Plane1Stride)) + x))];

                            region.Slice(
                                length: 4,
                                start: ((int)(header.Plane0Offset + (index * 4U)))
                            ).CopyTo(destination: target);

                            break;
                        }
                    case ImagePixelFormat.Nv12: {
                            var chroma = ((int)((header.Plane1Offset + ((y / 2U) * header.Plane1Stride)) + ((x / 2U) * 2U)));

                            var (r, g, b) = YuvToRgb(
                                cb: region[chroma],
                                cr: region[(chroma + 1)],
                                matrix: header.Color.Matrix,
                                range: header.Color.Range,
                                y: region[((int)((header.Plane0Offset + (y * header.Plane0Stride)) + x))]
                            );

                            target[0] = VectorFunctions.ToUnorm8(value: r);
                            target[1] = VectorFunctions.ToUnorm8(value: g);
                            target[2] = VectorFunctions.ToUnorm8(value: b);
                            target[3] = 0xFF;

                            break;
                        }
                    default: {
                            var pixel = region.Slice(
                                length: 4,
                                start: ((int)((header.Plane0Offset + (y * header.Plane0Stride)) + (x * 4U)))
                            );
                            var bgra = (header.Format == ImagePixelFormat.B8G8R8A8Unorm);

                            target[0] = pixel[(bgra ? 2 : 0)];
                            target[1] = pixel[1];
                            target[2] = pixel[(bgra ? 0 : 2)];
                            target[3] = pixel[3];

                            break;
                        }
                }
            }
        }
    }
    /// <summary>Computes what <see cref="TransferPass"/> writes for a region: four working values per pixel
    /// (<see cref="ToWorking(ImageColorEncoding, double, double, double, double)"/>), red first, row by row, alpha carried
    /// through unchanged.</summary>
    /// <param name="region">The source's region, header first; its format is
    /// <see cref="ImagePixelFormat.R8G8B8A8Unorm"/>, <see cref="ImagePixelFormat.R10G10B10A2Unorm"/> or
    /// <see cref="ImagePixelFormat.R16G16B16A16Float"/>.</param>
    /// <param name="paperWhiteNits">The paper-white level, in cd/m²: the luminance a working value of one shows at.</param>
    /// <param name="working">The destination; at least four floats per pixel.</param>
    /// <exception cref="ArgumentException"><paramref name="region"/>'s header is not valid or names another format, or
    /// <paramref name="working"/> is too short.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="paperWhiteNits"/> is outside the range
    /// <see cref="DisplayOutput.RequirePaperWhite"/> accepts.</exception>
    public static void ToWorking(ReadOnlySpan<byte> region, double paperWhiteNits, Span<float> working) {
        var header = ImageSourceUploadLayout.Read(region: region);

        if (header.Format is not (ImagePixelFormat.R8G8B8A8Unorm or ImagePixelFormat.R10G10B10A2Unorm or ImagePixelFormat.R16G16B16A16Float)) {
            throw new ArgumentException(
                message: $"{TransferPass} reads R8G8B8A8Unorm, R10G10B10A2Unorm or R16G16B16A16Float content, not {header.Format}.",
                paramName: nameof(region)
            );
        }

        _ = DisplayOutput.RequirePaperWhite(nits: paperWhiteNits);
        RequireLength(
            channels: 4,
            height: header.Height,
            length: working.Length,
            paramName: nameof(working),
            width: header.Width
        );

        for (var y = 0U; (y < header.Height); y++) {
            for (var x = 0U; (x < header.Width); x++) {
                var row = region[((int)(header.Plane0Offset + (y * header.Plane0Stride)))..];
                var target = working.Slice(
                    length: 4,
                    start: checked((int)(((y * header.Width) + x) * 4U))
                );

                var (r, g, b, a) = SampleOf(
                    format: header.Format,
                    row: row,
                    x: x
                );
                var (red, green, blue) = ToWorking(
                    b: b,
                    color: header.Color,
                    g: g,
                    paperWhiteNits: paperWhiteNits,
                    r: r
                );

                target[0] = ((float)red);
                target[1] = ((float)green);
                target[2] = ((float)blue);
                target[3] = ((float)a);
            }
        }
    }
    /// <summary>Converts one sample to the working space: its transfer function decoded to linear light relative to the
    /// paper-white level (<see cref="ToLinear"/>), BT.2020 primaries moved to BT.709, and each channel encoded with
    /// <see cref="LinearToWorking"/>.</summary>
    /// <param name="color">The sample's color encoding; its matrix and range are not read.</param>
    /// <param name="r">The red value as stored, normalized: a code over its maximum, or a float as it is.</param>
    /// <param name="g">The green value as stored.</param>
    /// <param name="b">The blue value as stored.</param>
    /// <param name="paperWhiteNits">The paper-white level, in cd/m².</param>
    /// <returns>The working values, unclamped.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A transfer function or primaries value is not defined, or
    /// <paramref name="paperWhiteNits"/> is outside the range <see cref="DisplayOutput.RequirePaperWhite"/>
    /// accepts.</exception>
    public static (double R, double G, double B) ToWorking(ImageColorEncoding color, double r, double g, double b, double paperWhiteNits) {
        var red = ToLinear(paperWhiteNits: paperWhiteNits, transfer: color.Transfer, value: r);
        var green = ToLinear(paperWhiteNits: paperWhiteNits, transfer: color.Transfer, value: g);
        var blue = ToLinear(paperWhiteNits: paperWhiteNits, transfer: color.Transfer, value: b);

        switch (color.Primaries) {
            case ImageColorPrimaries.Bt709:
                break;
            case ImageColorPrimaries.Bt2020: {
                    var m = Bt2020ToBt709;

                    (red, green, blue) = (
                        (((m[0] * red) + (m[1] * green)) + (m[2] * blue)),
                        (((m[3] * red) + (m[4] * green)) + (m[5] * blue)),
                        (((m[6] * red) + (m[7] * green)) + (m[8] * blue))
                    );

                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(
                    actualValue: color.Primaries,
                    message: "The color primaries are not defined.",
                    paramName: nameof(color)
                );
        }

        return (LinearToWorking(linear: red), LinearToWorking(linear: green), LinearToWorking(linear: blue));
    }
    /// <summary>Decodes one stored value to linear light relative to the paper-white level, so one is the luminance a
    /// working value of one shows at: an sRGB value (IEC 61966-2-1, clamped to 0–1) is relative to SDR white already
    /// and passes its own scale; a linear value is scRGB, one at <see cref="DisplayOutput.SdrWhiteNits"/>, and is
    /// scaled by that level over the paper white, unclamped; and a perceptual-quantizer value is its luminance
    /// (<see cref="PqToNits"/>) over the paper white.</summary>
    /// <param name="transfer">The transfer function the value is encoded with.</param>
    /// <param name="value">The value as stored, normalized.</param>
    /// <param name="paperWhiteNits">The paper-white level, in cd/m².</param>
    /// <returns>The linear value; one is the paper-white level.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="transfer"/> is not defined, or
    /// <paramref name="paperWhiteNits"/> is outside the range <see cref="DisplayOutput.RequirePaperWhite"/>
    /// accepts.</exception>
    public static double ToLinear(ImageTransferFunction transfer, double value, double paperWhiteNits) {
        var paperWhite = DisplayOutput.RequirePaperWhite(nits: paperWhiteNits);

        return transfer switch {
            ImageTransferFunction.Srgb => SrgbToLinear(value: value),
            ImageTransferFunction.Linear => ((value * DisplayOutput.SdrWhiteNits) / paperWhite),
            ImageTransferFunction.Pq => (PqToNits(value: value) / paperWhite),
            _ => throw new ArgumentOutOfRangeException(
                actualValue: transfer,
                message: "The transfer function is not defined.",
                paramName: nameof(transfer)
            ),
        };
    }
    /// <summary>Encodes linear light as a working value: the sRGB curve (IEC 61966-2-1), extended past one and mirrored
    /// below zero, which is the curve the display encode decodes, so one encodes to one and headroom stays on the
    /// curve.</summary>
    /// <param name="linear">The linear value; one is the paper-white level.</param>
    /// <returns>The working value, unclamped.</returns>
    public static double LinearToWorking(double linear) {
        var magnitude = Math.Abs(value: linear);
        var encoded = ((magnitude <= SrgbLinearLimit)
            ? (magnitude * 12.92)
            : ((1.055 * Math.Pow(
                x: magnitude,
                y: (1.0 / 2.4)
            )) - 0.055));

        return ((linear < 0.0) ? -encoded : encoded);
    }

    // One stored pixel of a transfer-pass format, each channel normalized: a 10-bit or 8-bit code over its maximum, or a
    // half float as it is.
    private static (double R, double G, double B, double A) SampleOf(ImagePixelFormat format, ReadOnlySpan<byte> row, uint x) {
        if (format == ImagePixelFormat.R16G16B16A16Float) {
            var pixel = row[((int)(x * 8U))..];

            return (
                ((double)BinaryPrimitives.ReadHalfLittleEndian(source: pixel)),
                ((double)BinaryPrimitives.ReadHalfLittleEndian(source: pixel[2..])),
                ((double)BinaryPrimitives.ReadHalfLittleEndian(source: pixel[4..])),
                ((double)BinaryPrimitives.ReadHalfLittleEndian(source: pixel[6..]))
            );
        }

        var word = BinaryPrimitives.ReadUInt32LittleEndian(source: row[((int)(x * 4U))..]);

        if (format == ImagePixelFormat.R10G10B10A2Unorm) {
            return (((word & 0x3FFU) / 1023.0), (((word >> 10) & 0x3FFU) / 1023.0), (((word >> 20) & 0x3FFU) / 1023.0), ((word >> 30) / 3.0));
        }

        var rgba = VectorFunctions.UnpackRgba8(packed: word);

        return (rgba.X, rgba.Y, rgba.Z, rgba.W);
    }
}
