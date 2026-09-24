using System.Buffers.Binary;
using System.Numerics;

namespace Puck.Abstractions.Sources;

/// <summary>
/// The conversion passes that turn an uploaded source's region (<see cref="ImageSourceUploadLayout"/>) into an image a
/// consumer samples, and the CPU reference each pass is held to. Each pass is a shipped HLSL compute kernel in
/// <c>Puck.Shaders</c> (<c>Assets/Shaders/Sources</c>) reading the region as a byte-address buffer at binding 0 and writing its
/// image at binding 1, eight by eight threads a group, one thread a pixel; the CPU reference here computes the same
/// arithmetic in double precision, so a kernel's output agrees with it to within one 8-bit code.
/// <para>
/// A planar source's chroma is sampled at the co-sited pixel of its half-resolution plane with no filtering, the Y'CbCr
/// matrix and code range are the ones its header names, and the resulting R'G'B' is clamped to 0–1 and not linearized.
/// The transfer pass decodes the header's transfer function to linear light scaled so 1 is the SDR reference white:
/// sRGB and linear values pass their own scale, and a perceptual-quantizer value is divided by
/// <see cref="ReferenceWhiteNits"/>.
/// </para>
/// </summary>
public static class ImageSourceConversion {
    /// <summary>The pass that turns a palette-indexed image into RGBA8.</summary>
    public const string PalettePass = "source-palette";
    /// <summary>The pass that turns an NV12 image into RGBA8.</summary>
    public const string Nv12Pass = "source-nv12";
    /// <summary>The luminance, in cd/m², the transfer pass maps to 1: the HDR reference white of ITU-R BT.2408.</summary>
    public const double ReferenceWhiteNits = 203.0;
    /// <summary>The pass that copies an RGBA8 or BGRA8 image into RGBA8.</summary>
    public const string RgbaPass = "source-rgba";
    /// <summary>The pass that decodes an image's transfer function to linear light in a half-float RGBA image.</summary>
    public const string TransferPass = "source-transfer";

    // SMPTE ST 2084 constants.
    private const double PqC1 = (3424.0 / 4096.0);
    private const double PqC2 = ((2413.0 / 4096.0) * 32.0);
    private const double PqC3 = ((2392.0 / 4096.0) * 32.0);
    private const double PqM1 = (2610.0 / 16384.0);
    private const double PqM2 = ((2523.0 / 4096.0) * 128.0);
    private const double PqPeakNits = 10_000.0;

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
    private static double Decode(ImageTransferFunction transfer, double value) => transfer switch {
        ImageTransferFunction.Srgb => SrgbToLinear(value: value),
        ImageTransferFunction.Linear => value,
        ImageTransferFunction.Pq => (PqToNits(value: value) / ReferenceWhiteNits),
        _ => throw new ArgumentOutOfRangeException(
            actualValue: transfer,
            message: "The transfer function is not defined.",
            paramName: nameof(transfer)
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
    /// <returns><see cref="TransferPass"/> for linear or perceptual-quantizer content or
    /// <see cref="ImagePixelFormat.R10G10B10A2Unorm"/> pixels; otherwise the format's own pass.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined format, or planar or
    /// indexed content names a transfer function other than sRGB.</exception>
    public static string PassOf(ImagePixelFormat format, ImageColorEncoding color) {
        var sdr = (color.Transfer == ImageTransferFunction.Srgb);

        return format switch {
            ImagePixelFormat.R8G8B8A8Unorm => (sdr ? RgbaPass : TransferPass),
            ImagePixelFormat.B8G8R8A8Unorm when sdr => RgbaPass,
            ImagePixelFormat.R10G10B10A2Unorm => TransferPass,
            ImagePixelFormat.Indexed8 when sdr => PalettePass,
            ImagePixelFormat.Nv12 when sdr => Nv12Pass,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: format,
                message: $"No conversion pass reads {format} content with the {color.Transfer} transfer function.",
                paramName: nameof(format)
            ),
        };
    }
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
    /// <summary>Computes what <see cref="TransferPass"/> writes for a region: four linear floats per pixel, red first,
    /// row by row, alpha carried through unchanged.</summary>
    /// <param name="region">The source's region, header first; its format is
    /// <see cref="ImagePixelFormat.R8G8B8A8Unorm"/> or <see cref="ImagePixelFormat.R10G10B10A2Unorm"/>.</param>
    /// <param name="linear">The destination; at least four floats per pixel.</param>
    /// <exception cref="ArgumentException"><paramref name="region"/>'s header is not valid or names another format, or
    /// <paramref name="linear"/> is too short.</exception>
    public static void ToLinear(ReadOnlySpan<byte> region, Span<float> linear) {
        var header = ImageSourceUploadLayout.Read(region: region);

        if (header.Format is not (ImagePixelFormat.R8G8B8A8Unorm or ImagePixelFormat.R10G10B10A2Unorm)) {
            throw new ArgumentException(
                message: $"{TransferPass} reads R8G8B8A8Unorm or R10G10B10A2Unorm content, not {header.Format}.",
                paramName: nameof(region)
            );
        }

        RequireLength(
            channels: 4,
            height: header.Height,
            length: linear.Length,
            paramName: nameof(linear),
            width: header.Width
        );

        var transfer = header.Color.Transfer;

        for (var y = 0U; (y < header.Height); y++) {
            for (var x = 0U; (x < header.Width); x++) {
                var word = BinaryPrimitives.ReadUInt32LittleEndian(source: region[((int)((header.Plane0Offset + (y * header.Plane0Stride)) + (x * 4U)))..]);
                var target = linear.Slice(
                    length: 4,
                    start: checked((int)(((y * header.Width) + x) * 4U))
                );

                var (r, g, b, a) = ((header.Format == ImagePixelFormat.R10G10B10A2Unorm)
                    ? (((word & 0x3FFU) / 1023.0), (((word >> 10) & 0x3FFU) / 1023.0), (((word >> 20) & 0x3FFU) / 1023.0), ((word >> 30) / 3.0))
                    : (((word & 0xFFU) / 255.0), (((word >> 8) & 0xFFU) / 255.0), (((word >> 16) & 0xFFU) / 255.0), ((word >> 24) / 255.0))
                );

                target[0] = ((float)Decode(transfer: transfer, value: r));
                target[1] = ((float)Decode(transfer: transfer, value: g));
                target[2] = ((float)Decode(transfer: transfer, value: b));
                target[3] = ((float)a);
            }
        }
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
