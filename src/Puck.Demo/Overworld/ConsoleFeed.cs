using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Gpu;
using Puck.Demo.DevConsole;
using Puck.Text;

namespace Puck.Demo.Overworld;

/// <summary>
/// The diegetic console terminal's screen feed: a CPU-composed CRT framebuffer that live-MIRRORS the developer
/// console (the same trailing lines the on-screen overlay shows and stdout echoes), published through
/// <see cref="IGpuSurfaceUpload"/> exactly like <see cref="ProceduralFeed"/> (map, blocking copy, one reusable
/// image-view handle valid until the next upload). It is Puck.Text's FIRST consumer: a fixed-cell monospace
/// <see cref="FontAtlas"/> (a <see cref="FontAtlasKind.SoftMask"/> coverage atlas, rasterized once from the same GDI+
/// path the overlay's <see cref="ConsoleGlyphFont"/> uses) drives <see cref="TextLayout"/> to position each glyph, and
/// the atlas coverage is sampled CPU-side into the phosphor framebuffer — the simplest correct path at a ~256px CRT
/// scale, where MTSDF's scale-independence buys nothing (a distance field shines under GPU magnification, not a 1:1
/// CPU blit at the atlas's native size).
/// <para>
/// The console TAP is <see cref="IConsoleTextSource"/> (the overlay's own read seam): each <see cref="Tick"/> snapshots
/// the latest published frame and re-rasterizes ONLY when the shown lines / input / visibility changed (a content
/// signature, not a per-frame raster) — a steady console never re-uploads. Presentation-only: nothing here touches
/// simulation state, reads the wall clock, or is gated by Post (the demo is greenfield). Both verb echoes and narration
/// lines appear, because the overlay's line history already carries both.
/// </para>
/// <para>
/// The bounded ring is implicit: the console store holds the full history, and this feed selects only the trailing
/// lines that fit the CRT (never the whole log). When the glyph atlas is unavailable (a non-Windows host — GDI+ is
/// Windows-only, exactly as the overlay documents), the terminal shows a dark, textless CRT rather than nothing.
/// </para>
/// </summary>
internal sealed class ConsoleFeed : IDisposable {
    /// <summary>The named-feed handle the terminal's screen surface samples (the wiring name a screen resolves through
    /// the diegetic-feed director's registry — see <see cref="CameraFeedPool.ResolveNamedFeedHandle"/>).</summary>
    public const string FeedName = "console";

    /// <summary>The CRT framebuffer width — a classic 4:3 terminal resolution, small enough that a 1:1 CPU glyph blit
    /// stays cheap and the phosphor grid reads crisply on the slab.</summary>
    public const int CrtWidth = 256;
    /// <summary>The CRT framebuffer height.</summary>
    public const int CrtHeight = 192;

    private const int BytesPerPixel = 4;
    private const int MarginX = 8;
    private const int MarginY = 6;
    // The trailing-history cap: even a huge log only ever rasterizes the last handful of lines that fit the CRT, so the
    // feed is a bounded window over the store's history, never the whole thing.
    private const int MaxHistoryLines = 24;
    private const string PromptPrefix = "> ";

    // The CRT palette: a dark phosphor-glass background, a soft green text line, and a fainter green for the scanline
    // dimming — deliberately generic (no third-party terminal's scheme), matching the overlay's phosphor read.
    private static readonly byte[] BackgroundColor = [0x06, 0x0b, 0x08, 0xff];
    private static readonly byte[] TextColor = [0x7a, 0xf0, 0x9c, 0xff];

    private readonly byte[] m_pixels = new byte[(CrtWidth * CrtHeight * BytesPerPixel)];
    private readonly IConsoleTextSource? m_source;
    private readonly TextLayout m_layout = new();
    private readonly FontAtlas? m_atlas;
    // The atlas coverage image (tightly packed RGBA, top-down) sampled by each placement's atlas rectangle; null when
    // the atlas is unavailable (a non-Windows host), which leaves the CRT a textless dark screen.
    private readonly byte[]? m_atlasRgba;
    private readonly int m_atlasWidth;
    private readonly int m_cellWidth;
    private readonly int m_cellHeight;
    private readonly int m_columns;
    private readonly int m_historyRows;
    private IGpuSurfaceUpload? m_upload;
    // The content signature of the LAST rasterized frame — a re-raster/re-upload happens only when it changes (the
    // dirty gate). -1 before the first successful raster (nothing published yet).
    private long m_lastSignature = -1L;
    private bool m_published;

    /// <summary>Initializes the feed over the console's read seam, building its glyph atlas once (or running textless
    /// when GDI+ is unavailable).</summary>
    /// <param name="source">The console's published-frame read seam (the overlay's own tap), or null when no console
    /// store is registered — the terminal then shows an empty CRT.</param>
    public ConsoleFeed(IConsoleTextSource? source) {
        m_source = source;

        if (TryBuildAtlas(atlas: out var atlas, rgba: out var rgba, cellWidth: out var cellWidth, cellHeight: out var cellHeight)) {
            m_atlas = atlas;
            m_atlasRgba = rgba;
            m_atlasWidth = atlas.Width;
            m_cellWidth = cellWidth;
            m_cellHeight = cellHeight;
            m_columns = Math.Max(val1: 1, val2: ((CrtWidth - (2 * MarginX)) / cellWidth));
            // One row is reserved for the live prompt; the rest scroll the trailing history.
            var rows = Math.Max(val1: 2, val2: ((CrtHeight - (2 * MarginY)) / cellHeight));
            m_historyRows = (rows - 1);
        }
    }

    /// <summary>The current image-view handle (valid until the next <see cref="Tick"/> that republishes); 0 before the
    /// first successful publish.</summary>
    public nint CurrentImageViewHandle { get; private set; }

    /// <summary>Snapshots the console and republishes the CRT when the shown content changed, blocking only long enough
    /// to map/copy/unmap the small framebuffer (matching <see cref="ProceduralFeed.Tick"/>'s upload cost). Never throws
    /// — a resolution failure (no GPU device/services yet) simply skips this tick; the caller retries next frame.</summary>
    /// <param name="device">The GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    public void Tick(IGpuDeviceContext device, IGpuComputeServices gpu) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(gpu);

        var frame = default(ConsoleTextFrame);
        var hasFrame = ((m_source is { } source) && source.TrySnapshot(frame: out frame));
        var signature = (hasFrame ? ComputeSignature(frame: in frame) : 0L);

        if (m_published && (signature == m_lastSignature)) {
            return; // steady console — nothing changed, no re-raster, no re-upload.
        }

        Rasterize(frame: (hasFrame ? frame : default));

        m_upload ??= gpu.SurfaceTransferFactory.CreateUpload(deviceContext: device);
        // The returned handle is only valid until the NEXT Upload on this object — re-stored on every publish, exactly
        // the contract IGpuSurfaceUpload documents (mirrors ProceduralFeed.Tick).
        CurrentImageViewHandle = m_upload.Upload(
            deviceContext: device,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: CrtHeight,
            pixels: m_pixels,
            width: CrtWidth
        );
        m_lastSignature = signature;
        m_published = true;
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_upload?.Dispose();
        m_upload = null;
    }

    // A cheap order-sensitive signature over exactly the pixels that WOULD be drawn (the trailing shown lines + the
    // prompt/input + visibility) — a change flips the dirty gate. Not a security hash; an FNV-1a walk of the shown
    // strings is plenty to spot an edit.
    private long ComputeSignature(in ConsoleTextFrame frame) {
        var hash = 1469598103934665603L; // FNV-1a offset basis
        var lines = frame.Lines;
        var firstShown = Math.Max(val1: 0, val2: (lines.Count - Math.Min(val1: m_historyRows, val2: MaxHistoryLines)));

        hash = Mix(hash: hash, value: (frame.Visible ? 1 : 0));
        hash = Mix(hash: hash, value: (lines.Count - firstShown));

        for (var index = firstShown; (index < lines.Count); index++) {
            hash = MixString(hash: hash, text: lines[index]);
        }

        return MixString(hash: hash, text: frame.Input);
    }

    private static long Mix(long hash, int value) =>
        ((hash ^ value) * 1099511628211L);

    private static long MixString(long hash, string text) {
        var running = Mix(hash: hash, value: text.Length);

        for (var index = 0; (index < text.Length); index++) {
            running = Mix(hash: running, value: text[index]);
        }

        return running;
    }

    // Composes the CRT framebuffer: a dark phosphor field with faint scanlines, the trailing history lines, and the
    // live prompt on the bottom row. Each line is laid out through Puck.Text (TextLayout advances the pen + steps the
    // baseline) and its glyphs are sampled from the SoftMask atlas coverage — nothing is drawn when the atlas is
    // unavailable (a textless dark CRT).
    private void Rasterize(ConsoleTextFrame frame) {
        FillBackground();

        if ((m_atlas is not { } atlas) || (m_atlasRgba is null)) {
            return;
        }

        var lines = frame.Lines;
        var shown = ((lines is null) ? 0 : Math.Min(val1: m_historyRows, val2: Math.Min(val1: lines.Count, val2: MaxHistoryLines)));
        var firstShown = ((lines is null) ? 0 : (lines.Count - shown));
        // The whole visible block as ONE laid-out string (history rows, then the prompt row) so TextLayout owns the
        // per-line baseline stepping; each line is clipped to the column count so a long line never runs off the CRT.
        var builder = new System.Text.StringBuilder();

        for (var row = 0; (row < shown); row++) {
            _ = builder.Append(value: Clip(text: lines![firstShown + row])).Append(value: '\n');
        }

        // Pad the history block to a fixed height so the prompt always sits on the bottom row (empty rows advance the
        // baseline without drawing).
        for (var row = shown; (row < m_historyRows); row++) {
            _ = builder.Append(value: '\n');
        }

        _ = builder.Append(value: Clip(text: (PromptPrefix + (frame.Input ?? string.Empty))));

        var layout = m_layout.Layout(atlas: atlas, text: builder.ToString(), scale: m_cellHeight);
        // The block's top edge in layout space: line 0's baseline is at y = 0, so its plane top is the ascender (one
        // em) up; every placement maps down from there into the y-down framebuffer.
        var blockTopY = (atlas.Metrics.Ascender * m_cellHeight);

        foreach (var placement in layout.Placements) {
            BlitGlyph(atlasRgba: m_atlasRgba, blockTopY: blockTopY, placement: in placement);
        }
    }

    // Samples one glyph's SoftMask coverage rectangle into the framebuffer at its laid-out position — a 1:1 copy (the
    // atlas cell and the plane quad are both cellWidth x cellHeight px at the native atlas size), blending the phosphor
    // text color over the CRT background by coverage.
    private void BlitGlyph(byte[] atlasRgba, float blockTopY, in TextGlyphPlacement placement) {
        var plane = placement.PlaneBounds;
        var source = placement.AtlasBounds;
        var destLeft = (MarginX + (int)MathF.Round(x: plane.Left));
        var destTop = (MarginY + (int)MathF.Round(x: (blockTopY - plane.Top)));
        var width = (int)MathF.Round(x: (plane.Right - plane.Left));
        var height = (int)MathF.Round(x: (plane.Top - plane.Bottom));
        var sourceLeft = (int)MathF.Round(x: source.Left);
        var sourceTop = (int)MathF.Round(x: source.Top); // atlas texels are top-down (this generator's orientation)

        for (var py = 0; (py < height); py++) {
            var destY = (destTop + py);

            if ((destY < 0) || (destY >= CrtHeight)) {
                continue;
            }

            var sourceRow = ((sourceTop + py) * m_atlasWidth);
            var destRow = (destY * CrtWidth);

            for (var px = 0; (px < width); px++) {
                var destX = (destLeft + px);

                if ((destX < 0) || (destX >= CrtWidth)) {
                    continue;
                }

                var coverage = atlasRgba[(((sourceRow + sourceLeft + px) * BytesPerPixel) + 3)];

                if (coverage == 0) {
                    continue;
                }

                BlendPixel(offset: ((destRow + destX) * BytesPerPixel), coverage: coverage);
            }
        }
    }

    // Blends the phosphor text color over the current pixel by 8-bit coverage (out = bg + (text - bg) * coverage/255).
    private void BlendPixel(int offset, byte coverage) {
        m_pixels[offset + 0] = (byte)(m_pixels[offset + 0] + (((TextColor[0] - m_pixels[offset + 0]) * coverage) / 255));
        m_pixels[offset + 1] = (byte)(m_pixels[offset + 1] + (((TextColor[1] - m_pixels[offset + 1]) * coverage) / 255));
        m_pixels[offset + 2] = (byte)(m_pixels[offset + 2] + (((TextColor[2] - m_pixels[offset + 2]) * coverage) / 255));
        m_pixels[offset + 3] = 0xff;
    }

    // Fills the framebuffer with the CRT background, darkening every other scanline a touch for a subtle phosphor-line
    // read (deterministic, purely cosmetic).
    private void FillBackground() {
        for (var y = 0; (y < CrtHeight); y++) {
            var scanline = ((y & 1) == 0);
            var r = (scanline ? BackgroundColor[0] : (byte)(BackgroundColor[0] * 3 / 4));
            var g = (scanline ? BackgroundColor[1] : (byte)(BackgroundColor[1] * 3 / 4));
            var b = (scanline ? BackgroundColor[2] : (byte)(BackgroundColor[2] * 3 / 4));
            var row = (y * CrtWidth * BytesPerPixel);

            for (var x = 0; (x < CrtWidth); x++) {
                var offset = (row + (x * BytesPerPixel));

                m_pixels[offset + 0] = r;
                m_pixels[offset + 1] = g;
                m_pixels[offset + 2] = b;
                m_pixels[offset + 3] = 0xff;
            }
        }
    }

    // Clips a line to the CRT's column count (a long verb echo never runs off the phosphor grid).
    private string Clip(string text) =>
        ((text.Length <= m_columns) ? text : text[..m_columns]);

    // Builds the Puck.Text SoftMask atlas from the overlay's proven GDI+ glyph raster: ConsoleGlyphFont rasterizes
    // printable ASCII into fixed cells, which are unpacked into one contiguous top-down RGBA coverage image (alpha =
    // coverage) and wrapped as a fixed-cell monospace FontAtlas. Returns false when GDI+ is unavailable (non-Windows),
    // leaving the terminal textless. This is the atlas GENERATOR the render-agnostic library expects a consumer to
    // supply; a dedicated font FIXTURE / IFontAtlasGenerator can replace it in a later tier.
    private static bool TryBuildAtlas([NotNullWhen(true)] out FontAtlas? atlas, [NotNullWhen(true)] out byte[]? rgba, out int cellWidth, out int cellHeight) {
        atlas = null;
        rgba = null;
        cellWidth = 0;
        cellHeight = 0;

        // A compact cell keeps the CRT dense (a terminal wants rows, not billboard letters); GDI+ renders the monospace
        // face at this size, and the SoftMask coverage carries its anti-aliasing.
        if (ConsoleGlyphFont.TryCreate(cellWidth: 6, cellHeight: 11) is not { } font) {
            return false;
        }

        var glyphCount = ConsoleGlyphFont.GlyphCount;

        cellWidth = font.CellWidth;
        cellHeight = font.CellHeight;

        var atlasWidth = (glyphCount * cellWidth);
        var image = new byte[(atlasWidth * cellHeight * BytesPerPixel)];
        var coverage = font.PackedCoverage;
        var glyphStride = (cellWidth * cellHeight);
        var glyphs = new List<FontAtlasGlyph>(capacity: glyphCount);
        var advanceEm = (cellWidth / (float)cellHeight);

        for (var index = 0; (index < glyphCount); index++) {
            var glyphBase = (index * glyphStride);
            var atlasLeft = (index * cellWidth);

            for (var y = 0; (y < cellHeight); y++) {
                for (var x = 0; (x < cellWidth); x++) {
                    var packedIndex = (glyphBase + (y * cellWidth) + x);
                    // PackedCoverage stores four coverage bytes per uint, little-endian (ConsoleGlyphFont's packing).
                    var value = (byte)((coverage[packedIndex >> 2] >> ((packedIndex & 3) * 8)) & 0xff);
                    var offset = ((((y * atlasWidth) + atlasLeft + x)) * BytesPerPixel);

                    image[offset + 0] = value;
                    image[offset + 1] = value;
                    image[offset + 2] = value;
                    image[offset + 3] = value;
                }
            }

            glyphs.Add(item: new FontAtlasGlyph(
                advance: advanceEm,
                atlasBounds: new FontAtlasBounds(Left: atlasLeft, Bottom: cellHeight, Right: (atlasLeft + cellWidth), Top: 0),
                planeBounds: new FontAtlasBounds(Left: 0f, Bottom: 0f, Right: advanceEm, Top: 1f),
                unicode: (ConsoleGlyphFont.FirstChar + index)
            ));
        }

        atlas = new FontAtlas(
            distanceRange: 0f,
            glyphs: glyphs,
            height: cellHeight,
            imageData: new FontAtlasImageData(height: cellHeight, rgbaPixels: image, width: atlasWidth),
            imagePath: "console-glyph-atlas",
            kind: FontAtlasKind.SoftMask,
            kerningPairs: [],
            metrics: new FontAtlasMetrics(Ascender: 1f, Descender: 0f, LineHeight: 1f, UnderlineThickness: 0.06f, UnderlineY: -0.1f),
            size: cellHeight,
            width: atlasWidth
        );
        rgba = image;

        return true;
    }
}
