using Puck.Abstractions.Presentation;

namespace Puck.SdfVm.Views;

/// <summary>
/// A deterministic animated test pattern: SMPTE-style vertical color bars over a horizontal luma ramp, with a white
/// vertical bar that sweeps across the frame so motion is visible. The rendered pixels are a pure function of
/// <c>(tick, width, height)</c> — no wall clock, no random state — so the same tick always draws the same frame. The
/// output is B8G8R8A8 (the capture-path pixel order), ready to hand to <see cref="CpuSurfaceSource"/>.
/// </summary>
public sealed class TestPatternSource {
    /// <summary>The pixel format the pattern writes (B8G8R8A8).</summary>
    public const SurfaceFormat PixelFormat = SurfaceFormat.B8G8R8A8Unorm;

    private const int BytesPerPixel = 4;
    // The bars fill the top of the frame; the bottom band shows a moving luma ramp.
    private const int BarRegionNumerator = 2;
    private const int BarRegionDenominator = 3;
    // The sweep advances one column every this-many ticks, so the motion stays gentle regardless of the caller's tick
    // rate.
    private const ulong SweepTicksPerColumn = 64UL;

    // The seven SMPTE-style bars, brightest to primary, as packed 0xRRGGBB.
    private static readonly uint[] Bars = [
        0xFFFFFFu, // white
        0xFFFF00u, // yellow
        0x00FFFFu, // cyan
        0x00FF00u, // green
        0xFF00FFu, // magenta
        0xFF0000u, // red
        0x0000FFu, // blue
    ];
    private readonly byte[] m_pixels;

    /// <summary>Initializes a pattern producer sized to <paramref name="width"/> × <paramref name="height"/>.</summary>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public TestPatternSource(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);

        Width = width;
        Height = height;
        m_pixels = new byte[((width * height) * BytesPerPixel)];
    }

    /// <summary>Gets the frame width in pixels.</summary>
    public int Width { get; }
    /// <summary>Gets the frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Renders the pattern for <paramref name="tick"/> into the internal buffer and returns it — the frame to
    /// hand to <see cref="CpuSurfaceSource.Publish(Puck.Abstractions.Gpu.IGpuDeviceContext, Puck.Abstractions.Gpu.IGpuComputeServices, ReadOnlyMemory{byte}, uint, uint, SurfaceFormat)"/>.</summary>
    /// <param name="tick">The engine tick driving the sweep phase.</param>
    /// <returns>The rendered B8G8R8A8 pixels (owned by this producer, valid until the next render).</returns>
    public ReadOnlyMemory<byte> Render(ulong tick) {
        Render(destination: m_pixels, tick: tick, width: Width, height: Height);

        return m_pixels;
    }

    /// <summary>Renders the pattern into a caller-owned buffer — the pure "fill this buffer" producer. Writes exactly
    /// <paramref name="width"/> × <paramref name="height"/> × 4 bytes in B8G8R8A8 order.</summary>
    /// <param name="destination">The buffer to fill; must hold at least <paramref name="width"/> × <paramref name="height"/> × 4 bytes.</param>
    /// <param name="tick">The engine tick driving the sweep phase.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public static void Render(Span<byte> destination, ulong tick, int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);

        var required = ((width * height) * BytesPerPixel);

        if (destination.Length < required) {
            throw new ArgumentException(
                message: $"The destination holds {destination.Length} bytes; the {width}x{height} pattern needs {required}.",
                paramName: nameof(destination)
            );
        }

        var barCount = Bars.Length;
        var barRegionHeight = ((height * BarRegionNumerator) / BarRegionDenominator);
        var sweepColumn = (int)((tick / SweepTicksPerColumn) % (ulong)width);
        var sweepHalfWidth = Math.Max(val1: 1, val2: (width / 64));

        for (var y = 0; (y < height); y++) {
            var inBars = (y < barRegionHeight);
            var row = ((y * width) * BytesPerPixel);

            for (var x = 0; (x < width); x++) {
                uint color;

                if (inBars) {
                    color = Bars[((x * barCount) / width)];
                } else {
                    // A horizontal luma ramp that scrolls with the sweep phase, so the bottom band also moves.
                    var ramp = (byte)(((x + sweepColumn) * 255) / width);

                    color = ((uint)ramp << 16) | ((uint)ramp << 8) | ramp;
                }

                // The sweep column brightens to solid white over the whole height.
                if (Math.Abs(value: (x - sweepColumn)) <= sweepHalfWidth) {
                    color = 0xFFFFFFu;
                }

                var offset = (row + (x * BytesPerPixel));

                destination[(offset + 0)] = (byte)color;         // B
                destination[(offset + 1)] = (byte)(color >> 8);  // G
                destination[(offset + 2)] = (byte)(color >> 16); // R
                destination[(offset + 3)] = 0xFF;                // A
            }
        }
    }
}
