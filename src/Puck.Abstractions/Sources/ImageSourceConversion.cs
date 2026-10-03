using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Presentation;

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

    private static (double Kr, double Kb) Coefficients(ImageYuvMatrix matrix) => matrix switch {
        ImageYuvMatrix.Bt601 => (0.299, 0.114),
        ImageYuvMatrix.Bt709 => (0.2126, 0.0722),
        ImageYuvMatrix.Bt2020 => (0.2627, 0.0593),
        _ => throw new ArgumentOutOfRangeException(
            actualValue: matrix,
            message: "The Y'CbCr matrix is not defined.",
            paramName: nameof(matrix)
        ),
    };
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
    public static string ImagePassOf(ImagePixelFormat format, ImageColorEncoding color) => (
        (format == ImagePixelFormat.R16G16B16A16Float) &&
        (color.Transfer == ImageTransferFunction.Linear) &&
        (color.Primaries == ImageColorPrimaries.Bt709)
    )
        ? ScRgbImagePass
        : throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: $"No conversion pass reads an imported {format} image with the {color.Transfer} transfer function and {color.Primaries} primaries.",
            paramName: nameof(format)
        );
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
    public static double LinearToSrgb(double value) {
        var clamped = Math.Clamp(
            max: 1.0,
            min: 0.0,
            value: value
        );

        return ((clamped <= 0.0031308)
            ? (clamped * 12.92)
            : ((1.055 * Math.Pow(
                x: clamped,
                y: (1.0 / 2.4)
            )) - 0.055)
        );
    }
    /// <summary>Returns the 8-bit sRGB code of a linear value (IEC 61966-2-1): the code whose interval under
    /// <see cref="SrgbToLinear"/> holds it, so a value lying between two codes' decoded midpoints encodes to the code
    /// between them, and every code's own decoded value encodes back to it. A negative value or NaN encodes to 0 and a
    /// value past the last midpoint to 255.
    /// <para>The encode compares <paramref name="value"/> with the 255 decoded midpoints and nothing else, and each
    /// midpoint is the smallest <see cref="double"/> at or above the exact real one, found in integer arithmetic, so the
    /// code is the same on every machine; an encode through <see cref="Math.Pow(double, double)"/> is not, because the
    /// last bit of a power is the platform library's.</para></summary>
    /// <param name="value">The linear value.</param>
    /// <returns>The code.</returns>
    public static byte LinearToSrgb8(double value) {
        var midpoints = SrgbMidpoints.Values;
        var low = 0;
        var high = midpoints.Length;

        while (low < high) {
            var middle = ((low + high) >>> 1);

            if (midpoints[middle] <= value) {
                low = (middle + 1);
            } else {
                high = middle;
            }
        }

        return ((byte)low);
    }
    /// <summary>Returns the 8-bit code a normalized value stores as: clamped to 0–1 and rounded to the nearest code.</summary>
    /// <param name="value">The normalized value.</param>
    /// <returns>The code.</returns>
    public static byte ToUnorm8(double value) => ((byte)Math.Round(
        value: (Math.Clamp(
            max: 1.0,
            min: 0.0,
            value: value
        ) * 255.0),
        mode: MidpointRounding.ToEven
    ));
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

                            target[0] = ToUnorm8(value: r);
                            target[1] = ToUnorm8(value: g);
                            target[2] = ToUnorm8(value: b);
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

        return ((format == ImagePixelFormat.R10G10B10A2Unorm)
            ? (((word & 0x3FFU) / 1023.0), (((word >> 10) & 0x3FFU) / 1023.0), (((word >> 20) & 0x3FFU) / 1023.0), ((word >> 30) / 3.0))
            : (((word & 0xFFU) / 255.0), (((word >> 8) & 0xFFU) / 255.0), (((word >> 16) & 0xFFU) / 255.0), ((word >> 24) / 255.0)));
    }

    /// <summary>Returns the linear value of an 8-bit sRGB code (IEC 61966-2-1): the smallest <see cref="double"/> at or
    /// above the exact decoded value, found in integer arithmetic, so it is the same on every machine. It is the decode
    /// <see cref="LinearToSrgb8"/> inverts: every code's value encodes back to that code. A filter that averages sRGB
    /// texels in linear light reads them through this rather than <see cref="SrgbToLinear"/>, whose last bit is the
    /// platform library's.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The linear value, 0 to 1.</returns>
    public static double Srgb8ToLinear(byte code) =>
        SrgbMidpoints.Codes[code];

    // The exact sRGB tables, each entry the smallest double at or above an exact decoded value. An encoded value is
    // k / 510 for an integer k: Midpoints[c - 1] decodes k = 2c - 1 (the midpoint below code c, for c from 1 to 255), and
    // Codes[c] decodes k = 2c (code c itself). An estimate is stepped one double at a time until the exact comparison
    // holds for it and fails for the double below it, so the tables rest on no floating-point result, only on the
    // ordering of doubles.
    private static class SrgbMidpoints {
        public static readonly double[] Values = Compute(count: 255, first: 1, offset: -1);
        public static readonly double[] Codes = Compute(count: 256, first: 0, offset: 0);

        // Entry i decodes k = 2 (first + i) + offset.
        private static double[] Compute(int first, int count, int offset) {
            var values = new double[count];

            for (var index = 0; (index < count); index++) {
                var k = ((2 * (first + index)) + offset);

                if (k == 0) {
                    continue;
                }

                var decoded = SrgbToLinear(value: (k / 510.0));

                while (!AtLeast(k: k, value: decoded)) {
                    decoded = Math.BitIncrement(x: decoded);
                }

                while (AtLeast(k: k, value: Math.BitDecrement(x: decoded))) {
                    decoded = Math.BitDecrement(x: decoded);
                }

                values[index] = decoded;
            }

            return values;
        }
        // Whether a value is at least the exact decoded value of the encoded value k / 510. Encoded values at or under
        // 0.04045 (k up to 20) decode linearly, to 5 k / 32946; the rest decode to r^(12/5) with
        // r = (20 k + 561) / 10761. A positive value is m 2^e exactly, so it is compared over integers: m 2^e 32946
        // against 5 k, or m^5 2^(5e) 10761^12 against (20 k + 561)^12.
        private static bool AtLeast(int k, double value) {
            if (!(value > 0.0)) {
                return false;
            }

            var bits = BitConverter.DoubleToInt64Bits(value: value);
            var exponent = ((int)((bits >> 52) & 0x7FF));
            var mantissa = new BigInteger(value: bits & 0xF_FFFF_FFFF_FFFFL);

            if (exponent == 0) {
                exponent = -1074;
            } else {
                mantissa += (BigInteger.One << 52);
                exponent -= 1075;
            }

            return ((k <= 20)
                ? (Compare(left: (mantissa * 32946), leftShift: exponent, right: new BigInteger(value: (k * 5))) >= 0)
                : (Compare(
                    left: (BigInteger.Pow(exponent: 5, value: mantissa) * BigInteger.Pow(exponent: 12, value: 10761)),
                    leftShift: (5 * exponent),
                    right: BigInteger.Pow(exponent: 12, value: ((20 * k) + 561))
                ) >= 0));
        }
        // Compares left 2^leftShift with right.
        private static int Compare(BigInteger left, int leftShift, BigInteger right) => ((leftShift >= 0)
            ? (left << leftShift).CompareTo(other: right)
            : left.CompareTo(other: (right << -leftShift)));
    }
}
