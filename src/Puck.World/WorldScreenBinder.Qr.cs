using System.Numerics;
using Puck.SdfVm.Views;
using Puck.Assets.Qr;

namespace Puck.World;

/// <summary>One screen's QR authoring, as <c>screen.source &lt;index&gt; qr</c> reads it back — the authored inputs plus everything the
/// encoder derived from them, so a piped session can assert the decision the setter made rather than infer it from
/// pixels.</summary>
/// <param name="Payload">The encoded payload string.</param>
/// <param name="Level">The resolved error-correction level.</param>
/// <param name="Version">The encoder-chosen QR version (1..10) — the smallest that held the payload at
/// <paramref name="Level"/>.</param>
/// <param name="Mask">The encoder-chosen mask pattern (0..7) — the lowest-penalty of the eight.</param>
/// <param name="QuietZoneModules">The rendered quiet-zone width in modules on every side.</param>
/// <param name="Width">The rasterized buffer's width in pixels.</param>
/// <param name="Height">The rasterized buffer's height in pixels.</param>
internal readonly record struct WorldScreenQrAuthoring(string Payload, QrErrorCorrectionLevel Level, int Version,
    int Mask, int QuietZoneModules, uint Width, uint Height);
internal sealed partial class WorldScreenBinder {
    // A one-line preview for a QR echo — a link payload runs to hundreds of characters, which would otherwise flood the
    // console mirror's 64-line ring.
    private static string ElideForEcho(string payload) {
        const int MaxLength = 48;

        return ((payload.Length <= MaxLength)
            ? payload
            : $"{payload[..MaxLength]}…"
        );
    }
    // Parses the level letter, encodes the payload, and rasterizes the matrix into a fresh CPU upload surface — the
    // ONE construction path both the declared row and the live verb take, so their refusals read identically. The
    // module pixel size targets a comfortable on-screen resolution whatever version the payload chose: big enough for
    // a scanner to resolve the modules, small enough to stay well under the validator's surface-dimension ceiling.
    private static bool TryBuildQrFeed(string payload, string? ecLevel, int quietZoneModules, out QrFeed? feed, out string? fault) {
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
            max: QrMaxModulePixels,
            min: QrMinModulePixels,
            value: (QrTargetPixelExtent / totalModules)
        );
        var pixels = matrix.RenderPixels(
            height: out var height,
            modulePixels: modulePixels,
            quietZoneModules: quietZoneModules,
            width: out var width
        );

        feed = new QrFeed(
            pixels: pixels,
            width: ((uint)width),
            height: ((uint)height),
            surface: new CpuSurfaceSource(),
            payload: payload,
            level: level,
            version: matrix.Version,
            mask: matrix.MaskPattern,
            quietZoneModules: quietZoneModules,
            light: AverageColor(pixels: pixels)
        );
        fault = null;

        return true;
    }

    /// <summary>Authors (or re-authors) a declared screen's QR code — the runtime <c>screen.source &lt;index&gt; qr</c> path, the live twin
    /// of a declared <see cref="WorldScreenSource.Qr"/> row. The payload is encoded and rasterized once, right here, so
    /// the per-frame cost of the resulting screen is a single unchanged-buffer upload and then nothing at all. Any live
    /// producer on the slot (the webcam, a window capture) and any jumbotron view are cleared first, exactly as
    /// <c>screen.source &lt;index&gt; camera</c>/<c>view</c> clear each other, so the freshly authored code is what the screen shows
    /// next publish. Fails loudly — never throws — for an undeclared screen, an unrecognized EC-level letter, a
    /// negative quiet zone, or a payload too large for the encoder's supported version range (refused by name, never
    /// truncated).</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="payload">The payload string to encode, UTF-8 byte mode.</param>
    /// <param name="ecLevel">The error-correction level letter (<c>L</c>/<c>M</c>/<c>Q</c>/<c>H</c>, case-insensitive),
    /// or <see langword="null"/> for the document default (<c>M</c>).</param>
    /// <param name="quietZoneModules">The quiet-zone width in modules, or <see langword="null"/> for the document
    /// default (4).</param>
    /// <returns>Whether the author succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryQr(int index, string payload, string? ecLevel, int? quietZoneModules) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (!TryBuildQrFeed(
            payload: payload,
            ecLevel: (ecLevel ?? QrErrorCorrection.Letter(level: QrErrorCorrection.Default)),
            quietZoneModules: (quietZoneModules ?? QrDefaultQuietZoneModules),
            feed: out var feed,
            fault: out var fault
        )) {
            return (Ok: false, Message: fault!);
        }

        slot.ClearLive();
        ReleaseSlotView(slot: slot);
        slot.ReleaseQr();
        slot.Qr = feed;
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} showing QR v{feed!.Version} {QrErrorCorrection.Letter(level: feed.Level)} mask{feed.Mask} {feed.Width}x{feed.Height} '{ElideForEcho(payload: feed.Payload)}'");
    }
    /// <summary>Reads back a screen's QR authoring — the <c>screen.source &lt;index&gt; qr</c> query (no payload argument) that makes the
    /// decision its setter made pipe-assertable: the payload, level, encoder-chosen version and mask, quiet zone, and
    /// rendered pixel extent. Fails when the screen carries no QR (nothing authored, or the declared source is
    /// something else).</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="authoring">The screen's QR authoring, on success; <see langword="default"/> otherwise.</param>
    /// <returns>Whether the screen carries a QR.</returns>
    public bool TryReadQr(int index, out WorldScreenQrAuthoring authoring) {
        if (
            m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) &&
            (slot.Qr is { } qr)
        ) {
            authoring = new WorldScreenQrAuthoring(
                Payload: qr.Payload,
                Level: qr.Level,
                Version: qr.Version,
                Mask: qr.Mask,
                QuietZoneModules: qr.QuietZoneModules,
                Width: qr.Width,
                Height: qr.Height
            );

            return true;
        }

        authoring = default;

        return false;
    }

    // One QR screen's owned state: the rasterized B8G8R8A8 buffer (built ONCE — a pure function of Payload/Level/
    // QuietZoneModules, never the tick), its GPU upload adapter, the encoder's resolved version/mask (what screen.source <index> qr
    // reads back), and the room glow, which is a constant for a static image. Published is the "already uploaded to
    // this device" latch Publish checks so an unchanged buffer is not re-copied every produced frame.
    private sealed class QrFeed(byte[] pixels, uint width, uint height, CpuSurfaceSource surface, string payload, QrErrorCorrectionLevel level, int version, int mask, int quietZoneModules, Vector3 light) {
        public uint Height { get; } = height;
        public QrErrorCorrectionLevel Level { get; } = level;
        public Vector3 Light { get; } = light;
        public int Mask { get; } = mask;
        public string Payload { get; } = payload;
        public byte[] Pixels { get; } = pixels;
        public int QuietZoneModules { get; } = quietZoneModules;
        public CpuSurfaceSource Surface { get; } = surface;
        public int Version { get; } = version;
        public uint Width { get; } = width;

        public bool Published { get; set; }
    }
}
