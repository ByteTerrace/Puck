using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.Assets.Qr;

namespace Puck.World.Client;

/// <summary>The <c>qr</c> producer: an authored QR code whose module grid is a pure function of its payload, level and
/// quiet zone, rasterized once and converted once by the source instance that shows it.</summary>
public sealed class WorldQrProducer : IWorldImageProducer {
    /// <inheritdoc/>
    public ImageContentClass Content => ImageContentClass.Deterministic;
    /// <inheritdoc/>
    public string Id => WorldImageProducerSettings.QrId;
    /// <inheritdoc/>
    public ImageSourceTransport Transport => ImageSourceTransport.Uploaded;

    /// <inheritdoc/>
    public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var (settings, refusal) = WorldImageProducerSettings.Bind<WorldQrSettings>(producer: source);

        if (settings is null) {
            feed = null;
            fault = refusal;

            return false;
        }

        var built = WorldQrFeed.TryBuild(
            ecLevel: settings.EcLevel,
            fault: out fault,
            feed: out var qr,
            payload: settings.Payload,
            quietZoneModules: settings.QuietZoneModules
        );

        feed = qr;

        return built;
    }
}
/// <summary>One QR source: the rasterized B8G8R8A8 code its source instance's region holds, and the encoder's decisions
/// (version and mask) <c>screen.source &lt;index&gt; qr</c> reads back. The code never changes, so its static source
/// converts it once.</summary>
public sealed class WorldQrFeed : IWorldUploadFeed, IImageSourceReference {
    // The module pixel size lands the rendered image near this many pixels square whatever version the payload chose,
    // clamped so each module stays legibly crisp and even a version-10 grid with a generous quiet zone stays far under
    // the validator's surface-dimension ceiling.
    private const int MaxModulePixels = 12;
    private const int MinModulePixels = 4;
    private const int TargetPixelExtent = 640;

    private readonly byte[] m_pixels;

    private WorldQrFeed(byte[] pixels, uint width, uint height, string payload, QrErrorCorrectionLevel level, int version, int mask, int quietZoneModules) {
        m_pixels = pixels;
        Descriptor = new ImageSourceDescriptor(
            Cadence: ImageSourceCadence.Static,
            Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.Deterministic,
            Format: ImagePixelFormat.B8G8R8A8Unorm,
            Height: height,
            Producer: WorldImageProducerSettings.QrId,
            Transport: ImageSourceTransport.Uploaded,
            Width: width
        );
        Level = level;
        Light = WorldImageLight.Average(bgra: pixels);
        Mask = mask;
        Payload = payload;
        QuietZoneModules = quietZoneModules;
        Version = version;
    }

    /// <inheritdoc/>
    public ImageSourceDescriptor Descriptor { get; }
    /// <inheritdoc/>
    public string? Fault => null;
    /// <summary>Gets the resolved error-correction level.</summary>
    public QrErrorCorrectionLevel Level { get; }
    /// <inheritdoc/>
    public Vector3 Light { get; }
    /// <summary>Gets the encoder-chosen mask pattern, 0 to 7: the lowest-penalty of the eight.</summary>
    public int Mask { get; }
    /// <summary>Gets the encoded payload.</summary>
    public string Payload { get; }
    /// <summary>Gets the quiet-zone width in modules on every side.</summary>
    public int QuietZoneModules { get; }
    /// <summary>Gets the encoder-chosen QR version, 1 to 10: the smallest that holds the payload at <see cref="Level"/>.</summary>
    public int Version { get; }

    /// <summary>Parses the level, encodes the payload and rasterizes the code: the one construction the declared source
    /// and the live verb share, so their refusals read identically. The module pixel size targets a comfortable
    /// on-screen resolution whatever version the payload chose.</summary>
    /// <param name="payload">The payload, UTF-8 byte mode.</param>
    /// <param name="ecLevel">The error-correction level letter.</param>
    /// <param name="quietZoneModules">The quiet-zone width in modules; non-negative.</param>
    /// <param name="feed">The feed, or <see langword="null"/> when the code cannot be built.</param>
    /// <param name="fault">Why it cannot be built, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the code was built.</returns>
    public static bool TryBuild(string payload, string? ecLevel, int quietZoneModules, out WorldQrFeed? feed, out string? fault) {
        feed = null;

        if (!QrErrorCorrection.TryParse(
            level: out var level,
            text: ecLevel
        )) {
            fault = $"ecLevel '{ecLevel}' must be one of {QrErrorCorrection.Vocabulary}";

            return false;
        }

        if (quietZoneModules < 0) {
            fault = $"quietZoneModules {quietZoneModules} must be non-negative";

            return false;
        }

        if (
            !QrEncoder.TryEncode(
                error: out fault,
                level: level,
                matrix: out var matrix,
                payload: payload
            ) ||
            (matrix is null)
        ) {
            return false;
        }

        var totalModules = (matrix.Size + (2 * quietZoneModules));
        var modulePixels = Math.Clamp(
            max: MaxModulePixels,
            min: MinModulePixels,
            value: (TargetPixelExtent / totalModules)
        );
        var pixels = matrix.RenderPixels(
            height: out var height,
            modulePixels: modulePixels,
            quietZoneModules: quietZoneModules,
            width: out var width
        );

        feed = new WorldQrFeed(
            height: ((uint)height),
            level: level,
            mask: matrix.MaskPattern,
            payload: payload,
            pixels: pixels,
            quietZoneModules: quietZoneModules,
            version: matrix.Version,
            width: ((uint)width)
        );
        fault = null;

        return true;
    }
    /// <inheritdoc/>
    /// <remarks>The code owns no resource.</remarks>
    public void Dispose() { }
    /// <inheritdoc/>
    /// <remarks>The code never changes, so a region that already holds it owes nothing.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="region"/> is <see langword="null"/>.</exception>
    public bool TryWrite(long tick, GpuRegion region) {
        ArgumentNullException.ThrowIfNull(argument: region);

        _ = region.Write(
            bytes: m_pixels,
            offset: ImageSourceUploadLayout.HeaderBytes
        );

        return true;
    }
    /// <inheritdoc/>
    public bool TryWriteReference(Span<byte> rgba, out ImageSourceStamp stamp) {
        ImageSourceConversion.BgraToRgba(
            bgra: m_pixels,
            rgba: rgba
        );

        stamp = new ImageSourceStamp(
            Sequence: 1UL,
            Tick: 0UL
        );

        return true;
    }
}
