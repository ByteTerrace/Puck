using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Presentation;

/// <summary>
/// What a swapchain presents to the display: the pixel format of its images and the color space they are encoded in.
/// It is the one backend-neutral description of a display output. A backend reports the pairs its surface or output
/// offers as <see cref="DisplayOutput"/> values, chooses one through <see cref="TrySelect"/>, and exposes the chosen one
/// once its swapchain exists.
/// <para>
/// Standard dynamic range in an 8-bit unsigned normalized format is the default and the fallback. An HDR output,
/// <see cref="DisplayColorSpace.Hdr10"/> in <see cref="GpuPixelFormat.R10G10B10A2Unorm"/> or
/// <see cref="DisplayColorSpace.ScRgb"/> in <see cref="GpuPixelFormat.R16G16B16A16Float"/>, is chosen only when it is
/// requested and reported.
/// </para>
/// </summary>
/// <param name="Format">The format of the swapchain's images.</param>
/// <param name="ColorSpace">The color space the images are encoded in.</param>
public readonly record struct DisplayOutput(GpuPixelFormat Format, DisplayColorSpace ColorSpace) {
    /// <summary>The SDR white level, in nits: the luminance of an SDR pixel value of one, and the default paper
    /// white.</summary>
    public const double SdrWhiteNits = 80.0;
    /// <summary>The highest paper-white level, in nits: the peak the ST 2084 perceptual quantizer encodes.</summary>
    public const double MaxPaperWhiteNits = 10_000.0;

    /// <summary>Gets the formats an SDR swapchain may be created in, in the order <see cref="TrySelect"/> prefers them
    /// when the preferred format is not reported: 8-bit unsigned normalized, then 8-bit sRGB, then 10-bit, then half
    /// float.</summary>
    public static IReadOnlyList<GpuPixelFormat> SdrFormats { get; } = [
        GpuPixelFormat.B8G8R8A8Unorm,
        GpuPixelFormat.R8G8B8A8Unorm,
        GpuPixelFormat.B8G8R8A8Srgb,
        GpuPixelFormat.R8G8B8A8Srgb,
        GpuPixelFormat.R10G10B10A2Unorm,
        GpuPixelFormat.R16G16B16A16Float,
    ];

    /// <summary>Gets whether the output is HDR: any color space but <see cref="DisplayColorSpace.Srgb"/>.</summary>
    public bool IsHdr => (ColorSpace != DisplayColorSpace.Srgb);

    /// <summary>Returns the format an HDR color space's swapchain is created in.</summary>
    /// <param name="colorSpace">The HDR color space.</param>
    /// <returns><see cref="GpuPixelFormat.R10G10B10A2Unorm"/> for <see cref="DisplayColorSpace.Hdr10"/> and
    /// <see cref="GpuPixelFormat.R16G16B16A16Float"/> for <see cref="DisplayColorSpace.ScRgb"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="colorSpace"/> is <see cref="DisplayColorSpace.Srgb"/>
    /// or not a defined value.</exception>
    public static GpuPixelFormat HdrFormatOf(DisplayColorSpace colorSpace) => colorSpace switch {
        DisplayColorSpace.Hdr10 => GpuPixelFormat.R10G10B10A2Unorm,
        DisplayColorSpace.ScRgb => GpuPixelFormat.R16G16B16A16Float,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: colorSpace,
            message: "The color space is not an HDR color space.",
            paramName: nameof(colorSpace)
        ),
    };
    /// <summary>Chooses the output a swapchain is created with from the outputs the display reports. A requested HDR
    /// color space is chosen in its format (<see cref="HdrFormatOf"/>) when that pair is reported. Otherwise the output
    /// is SDR: the preferred format when it is one of <see cref="SdrFormats"/> reported in
    /// <see cref="DisplayColorSpace.Srgb"/>, else the first of <see cref="SdrFormats"/> reported in it. The order the
    /// display reports its outputs in never decides.</summary>
    /// <param name="reported">The outputs the display reports; a pair no <see cref="GpuPixelFormat"/> names is left
    /// out by the backend that reads them.</param>
    /// <param name="requested">The color space asked for; <see cref="DisplayColorSpace.Srgb"/> asks for SDR.</param>
    /// <param name="preferredSdrFormat">The format an SDR output prefers, or <see langword="null"/> for the order of
    /// <see cref="SdrFormats"/> alone.</param>
    /// <param name="chosen">The chosen output, or the default when none is reported.</param>
    /// <returns><see langword="false"/> when the display reports no SDR output in any of <see cref="SdrFormats"/> and no
    /// requested HDR output.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reported"/> is <see langword="null"/>.</exception>
    public static bool TrySelect(IReadOnlyCollection<DisplayOutput> reported, DisplayColorSpace requested, GpuPixelFormat? preferredSdrFormat, out DisplayOutput chosen) {
        ArgumentNullException.ThrowIfNull(argument: reported);

        if (requested != DisplayColorSpace.Srgb) {
            var hdr = new DisplayOutput(
                ColorSpace: requested,
                Format: HdrFormatOf(colorSpace: requested)
            );

            if (reported.Contains(value: hdr)) {
                chosen = hdr;

                return true;
            }
        }

        if (
            (preferredSdrFormat is { } preferred) &&
            SdrFormats.Contains(value: preferred) &&
            reported.Contains(value: Sdr(format: preferred))
        ) {
            chosen = Sdr(format: preferred);

            return true;
        }

        foreach (var format in SdrFormats) {
            if (reported.Contains(value: Sdr(format: format))) {
                chosen = Sdr(format: format);

                return true;
            }
        }

        chosen = default;

        return false;
    }
    /// <summary>Returns the SDR output in a format.</summary>
    /// <param name="format">The swapchain's format.</param>
    /// <returns>The output in <see cref="DisplayColorSpace.Srgb"/>.</returns>
    public static DisplayOutput Sdr(GpuPixelFormat format) => new(
        ColorSpace: DisplayColorSpace.Srgb,
        Format: format
    );
    /// <summary>Returns a paper-white level when it lies within the SDR white level and the perceptual quantizer's
    /// peak.</summary>
    /// <param name="nits">The paper-white level, in nits.</param>
    /// <returns><paramref name="nits"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nits"/> is not a number, below
    /// <see cref="SdrWhiteNits"/> or above <see cref="MaxPaperWhiteNits"/>.</exception>
    public static double RequirePaperWhite(double nits) {
        if (
            double.IsNaN(d: nits) ||
            (nits < SdrWhiteNits) ||
            (nits > MaxPaperWhiteNits)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: nits,
                message: $"The paper-white level is {SdrWhiteNits} to {MaxPaperWhiteNits} nits.",
                paramName: nameof(nits)
            );
        }

        return nits;
    }

    /// <summary>Returns the linear pixel value that UI white takes in this output at a paper-white level: one in SDR,
    /// whatever the level, since an SDR display shows its own white; the level over <see cref="SdrWhiteNits"/> in
    /// scRGB; and the level over <see cref="MaxPaperWhiteNits"/> in HDR10, before the perceptual quantizer encodes
    /// it.</summary>
    /// <param name="paperWhiteNits">The paper-white level, in nits.</param>
    /// <returns>The linear value of UI white.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="paperWhiteNits"/> is outside the range
    /// <see cref="RequirePaperWhite"/> accepts, or <see cref="ColorSpace"/> is not a defined value.</exception>
    public double WhiteScale(double paperWhiteNits) {
        var nits = RequirePaperWhite(nits: paperWhiteNits);

        return ColorSpace switch {
            DisplayColorSpace.Srgb => 1.0,
            DisplayColorSpace.ScRgb => (nits / SdrWhiteNits),
            DisplayColorSpace.Hdr10 => (nits / MaxPaperWhiteNits),
            _ => throw new ArgumentOutOfRangeException(
                actualValue: ColorSpace,
                message: "The color space is not a defined value.",
                paramName: nameof(ColorSpace)
            ),
        };
    }
}
