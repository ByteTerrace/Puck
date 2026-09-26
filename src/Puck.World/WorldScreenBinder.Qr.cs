using Puck.Assets.Qr;
using Puck.World.Client;

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

    /// <summary>Authors (or re-authors) a declared screen's QR code — the runtime <c>screen.source &lt;index&gt; qr</c> path, the live twin
    /// of a declared <c>qr</c> producer source. The payload is encoded and rasterized once, right here
    /// (<see cref="WorldQrFeed.TryBuild"/>), so the per-frame cost of the resulting screen is a single upload and then
    /// nothing at all. Any live producer on the slot (the webcam, a window capture) and any jumbotron view are cleared
    /// first, exactly as <c>screen.source &lt;index&gt; camera</c>/<c>view</c> clear each other, so the freshly authored
    /// code is what the screen shows next publish. Fails loudly — never throws — for an undeclared screen, an
    /// unrecognized EC-level letter, a negative quiet zone, or a payload too large for the encoder's supported version
    /// range (refused by name, never truncated).</summary>
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

        if (!WorldQrFeed.TryBuild(
            ecLevel: (ecLevel ?? QrErrorCorrection.Letter(level: QrErrorCorrection.Default)),
            fault: out var fault,
            feed: out var feed,
            payload: payload,
            quietZoneModules: (quietZoneModules ?? QrDefaultQuietZoneModules)
        )) {
            return (Ok: false, Message: fault!);
        }

        slot.ClearLive();
        ReleaseSlotView(slot: slot);
        slot.ReleaseDeclared();
        slot.DeclaredFeed = feed;
        slot.DeclaredFault = null;
        ShowLive(index: index);

        return (Ok: true, Message: $"screen {index} showing QR v{feed!.Version} {QrErrorCorrection.Letter(level: feed.Level)} mask{feed.Mask} {feed.Descriptor.Width}x{feed.Descriptor.Height} '{ElideForEcho(payload: feed.Payload)}'");
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
            (slot.DeclaredFeed is WorldQrFeed qr)
        ) {
            authoring = new WorldScreenQrAuthoring(
                Payload: qr.Payload,
                Level: qr.Level,
                Version: qr.Version,
                Mask: qr.Mask,
                QuietZoneModules: qr.QuietZoneModules,
                Width: qr.Descriptor.Width,
                Height: qr.Descriptor.Height
            );

            return true;
        }

        authoring = default;

        return false;
    }
}
