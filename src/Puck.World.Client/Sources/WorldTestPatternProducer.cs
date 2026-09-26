using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// The <c>testPattern</c> producer: a deterministic animated test pattern, SMPTE-style vertical color bars over a
/// horizontal luma ramp, with a white vertical bar that sweeps across the frame so motion is visible. The pixels are a
/// pure function of the simulation tick, the width and the height — no wall clock, no random state — so the same tick
/// always draws the same frame, and the feed states that frame exactly (<see cref="IImageSourceReference"/>).
/// </summary>
public sealed class WorldTestPatternProducer : IWorldImageProducer {
    // The bars fill the top two thirds of the frame; the bottom band shows a moving luma ramp.
    private const int BarRegionDenominator = 3;
    private const int BarRegionNumerator = 2;
    private const int BytesPerPixel = 4;
    // The sweep advances one column every this-many ticks, so the motion stays gentle whatever the tick rate.
    private const ulong SweepTicksPerColumn = 64UL;

    // The seven SMPTE-style bars, brightest to primary, as packed 0xRRGGBB.
    private static readonly uint[] Bars = [
        0xFFFFFFu,
        0xFFFF00u,
        0x00FFFFu,
        0x00FF00u,
        0xFF00FFu,
        0xFF0000u,
        0x0000FFu,
    ];

    /// <inheritdoc/>
    public ImageContentClass Content => ImageContentClass.Deterministic;
    /// <inheritdoc/>
    public string Id => WorldImageProducerSettings.TestPatternId;
    /// <inheritdoc/>
    public ImageSourceTransport Transport => ImageSourceTransport.Uploaded;

    /// <summary>Renders the pattern for a tick into a caller-owned buffer: exactly <paramref name="width"/> ×
    /// <paramref name="height"/> × 4 bytes of B8G8R8A8.</summary>
    /// <param name="bgra">The buffer; at least four bytes per pixel.</param>
    /// <param name="tick">The simulation tick driving the sweep.</param>
    /// <param name="width">The frame width in pixels; positive.</param>
    /// <param name="height">The frame height in pixels; positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">An extent is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="bgra"/> is too small.</exception>
    public static void Render(Span<byte> bgra, ulong tick, int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);

        var required = ((width * height) * BytesPerPixel);

        if (bgra.Length < required) {
            throw new ArgumentException(
                message: $"The destination holds {bgra.Length} bytes; the {width}x{height} pattern needs {required}.",
                paramName: nameof(bgra)
            );
        }

        var barCount = Bars.Length;
        var barRegionHeight = ((height * BarRegionNumerator) / BarRegionDenominator);
        var sweepColumn = ((int)((tick / SweepTicksPerColumn) % ((ulong)width)));
        var sweepHalfWidth = Math.Max(
            val1: 1,
            val2: (width / 64)
        );

        for (var y = 0; (y < height); y++) {
            var inBars = (y < barRegionHeight);
            var row = ((y * width) * BytesPerPixel);

            for (var x = 0; (x < width); x++) {
                uint color;

                if (inBars) {
                    color = Bars[((x * barCount) / width)];
                } else {
                    // A horizontal luma ramp that scrolls with the sweep phase, so the bottom band also moves.
                    var ramp = ((byte)(((x + sweepColumn) * 255) / width));

                    color = (((uint)ramp) << 16) | (((uint)ramp) << 8) | ramp;
                }

                if (Math.Abs(value: (x - sweepColumn)) <= sweepHalfWidth) {
                    color = 0xFFFFFFu;
                }

                var offset = (row + (x * BytesPerPixel));

                bgra[(offset + 0)] = ((byte)color);
                bgra[(offset + 1)] = ((byte)(color >> 8));
                bgra[(offset + 2)] = ((byte)(color >> 16));
                bgra[(offset + 3)] = 0xFF;
            }
        }
    }
    /// <inheritdoc/>
    public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var (settings, refusal) = WorldImageProducerSettings.Bind<WorldTestPatternSettings>(producer: source);

        if (
            (settings is null) ||
            (settings.Width <= 0) ||
            (settings.Height <= 0)
        ) {
            feed = null;
            fault = (refusal ?? $"test pattern {settings?.Width}x{settings?.Height} has no pixels");

            return false;
        }

        feed = new Feed(
            height: settings.Height,
            width: settings.Width
        );
        fault = null;

        return true;
    }

    // One test-pattern screen: the pattern's pixels, re-rendered and uploaded every published tick.
    private sealed class Feed : IWorldImageFeed, IImageSourceReference {
        private readonly byte[] m_pixels;
        private readonly CpuSurfaceSource m_surface = new();

        private ImageSourceStamp m_stamp;

        public Feed(int width, int height) {
            m_pixels = new byte[((width * height) * BytesPerPixel)];
            Descriptor = new ImageSourceDescriptor(
                Cadence: ImageSourceCadence.Tick,
                Color: ImageColorEncoding.Srgb,
                Content: ImageContentClass.Deterministic,
                Format: ImagePixelFormat.B8G8R8A8Unorm,
                Height: ((uint)height),
                Producer: WorldImageProducerSettings.TestPatternId,
                Transport: ImageSourceTransport.Uploaded,
                Width: ((uint)width)
            );
        }

        public ImageSourceDescriptor Descriptor { get; }
        public string? Fault => null;
        public Vector3 Light { get; private set; }

        public GpuImageLease AcquireFrame() => m_surface.CurrentHandle;
        public void Dispose() => m_surface.Dispose();
        public nint Handle() => m_surface.CurrentHandle;
        public void NotifyDeviceLost() => m_surface.NotifyDeviceLost();
        public void Publish(ulong tick, IGpuDeviceContext deviceContext) {
            Render(
                bgra: m_pixels,
                height: ((int)Descriptor.Height),
                tick: tick,
                width: ((int)Descriptor.Width)
            );
            _ = m_surface.Publish(
                deviceContext: deviceContext,
                format: SurfaceFormat.B8G8R8A8Unorm,
                height: Descriptor.Height,
                pixels: m_pixels,
                width: Descriptor.Width
            );
            Light = WorldImageLight.Average(bgra: m_pixels);
            m_stamp = new ImageSourceStamp(
                Sequence: (m_stamp.Sequence + 1UL),
                Tick: tick
            );
        }
        public bool TryWriteReference(Span<byte> rgba, out ImageSourceStamp stamp) {
            stamp = m_stamp;

            if (m_stamp.Sequence == 0UL) {
                return false;
            }

            ImageSourceConversion.BgraToRgba(
                bgra: m_pixels,
                rgba: rgba
            );

            return true;
        }
    }
}
