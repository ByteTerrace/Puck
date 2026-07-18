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
    /// <summary>The named-view handle the terminal's screen surface samples (the wiring name a screen resolves through
    /// the diegetic view stack's registry — see <see cref="Puck.SdfVm.Views.ViewStack.Resolve"/>).</summary>
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
    // The enrichment animation clock: m_contentTick counts produced frames (deterministic, no wall clock), and this is
    // how many of those frames make one content second for the effects' Hz-based frequencies. The overworld paces at
    // roughly this rate; the exact value only sets the visible speed of a wave/shake, never correctness.
    private const float TicksPerSecond = 60.0f;

    // The CRT palette: a dark phosphor-glass background, a soft green text line, and a fainter green for the scanline
    // dimming — deliberately generic (no third-party terminal's scheme), matching the overlay's phosphor read.
    private static readonly byte[] BackgroundColor = [0x06, 0x0b, 0x08, 0xff];
    private static readonly byte[] TextColor = [0x7a, 0xf0, 0x9c, 0xff];
    private readonly byte[] m_pixels = new byte[((CrtWidth * CrtHeight) * BytesPerPixel)];
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
    // The produced-frame counter that drives enrichment animation — advanced once per Tick, folded into the dirty
    // signature only while animated enriched content is actually shown (so a steady or motion-off terminal never
    // re-rasterizes). Deterministic: a frame count, never the wall clock.
    private long m_contentTick;
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

        m_contentTick++;

        var frame = default(ConsoleTextFrame);
        var hasFrame = ((m_source is { } source) && source.TrySnapshot(frame: out frame));
        var signature = (hasFrame ? ComputeSignature(frame: in frame) : 0L);

        // While motion-enabled animated enrichment is on screen, fold the content tick into the signature so the CRT
        // re-rasterizes every frame (a wave/shake needs a fresh phase). A steady console, a purely static-enriched line
        // (colour/weight), or motion-off content keeps a content-only signature and never re-uploads.
        if (hasFrame && TextMotionState.MotionEnabled && ShownContentAnimates(frame: in frame)) {
            signature = Mix(hash: signature, value: unchecked((int)m_contentTick));
        }

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

    // === The GLYPH DECAL cell grid (the resolution-independent text mode) ============================================
    // The terminal's screen surface can carry a fixed monospace grid of glyph cells the engine's decal sampling
    // reconstructs from the SHARED SDF atlas (DiegeticUiDirector owns it) — so the CRT text is crisp at walk-up distance
    // instead of a scaled bitmap. These statics compose the SAME trailing history + prompt the CPU bitmap shows, mapped
    // to a cell grid; the director caches the baked cells on ComputeDecalSignature and clears the decal (falling back to
    // this feed's CPU bitmap, or a dark CRT) when no shared atlas exists. Kept within SdfWorldEngine.MaxScreenDecalCells
    // (DecalColumns × DecalRows = 800).

    /// <summary>The decal grid column count.</summary>
    internal const int DecalColumns = 40;
    /// <summary>The decal grid row count (the last row is the live prompt; the rest scroll the trailing history).</summary>
    internal const int DecalRows = 20;

    private static readonly uint DecalForegroundRgba = PackRgba(color: TextColor);
    private static readonly uint DecalBackgroundRgba = PackRgba(color: BackgroundColor);

    /// <summary>A content signature over exactly the decal's shown lines + prompt — the dirty gate the director caches
    /// on (the cell-grid counterpart of <see cref="ComputeSignature"/>, sized to the decal's own history-row count).</summary>
    /// <param name="source">The console read seam, or null (an empty terminal).</param>
    internal static long ComputeDecalSignature(IConsoleTextSource? source) {
        var hash = 1469598103934665603L; // FNV-1a offset basis

        if ((source is null) || !source.TrySnapshot(frame: out var frame)) {
            return 0L;
        }

        var lines = frame.Lines;
        var firstShown = Math.Max(val1: 0, val2: (lines.Count - Math.Min(val1: (DecalRows - 1), val2: MaxHistoryLines)));

        hash = Mix(hash: hash, value: (frame.Visible ? 1 : 0));
        hash = Mix(hash: hash, value: (lines.Count - firstShown));

        for (var index = firstShown; (index < lines.Count); index++) {
            hash = MixString(hash: hash, text: lines[index]);
        }

        return MixString(hash: hash, text: (frame.Input ?? string.Empty));
    }

    /// <summary>Bakes the console's trailing history + live prompt into <paramref name="cells"/> (row-major
    /// <see cref="DecalRows"/> × <see cref="DecalColumns"/>, four uints per cell): each printable cell packs its glyph's
    /// atlas UV rect (from the shared SDF atlas) + the phosphor fg/bg; a space or unknown glyph stays a blank cell
    /// (equal UV corners => background only).</summary>
    /// <param name="source">The console read seam, or null (an empty terminal).</param>
    /// <param name="atlas">The shared SDF glyph atlas the decal samples.</param>
    /// <param name="cells">The destination cell buffer (<see cref="DecalRows"/> × <see cref="DecalColumns"/> × 4 uints).</param>
    internal static void BakeDecalCells(IConsoleTextSource? source, FontAtlas atlas, uint[] cells) {
        var atlasWidth = (float)atlas.Width;
        var atlasHeight = (float)atlas.Height;

        // Every cell starts blank (equal UV corners) on the phosphor background.
        for (var cell = 0; (cell < (DecalColumns * DecalRows)); cell++) {
            var b = (cell * 4);

            cells[(b + 0)] = 0u;
            cells[(b + 1)] = 0u;
            cells[(b + 2)] = DecalForegroundRgba;
            cells[(b + 3)] = DecalBackgroundRgba;
        }

        var frame = default(ConsoleTextFrame);

        _ = ((source is { } tap) && tap.TrySnapshot(frame: out frame));

        var lines = frame.Lines;
        var shown = ((lines is null) ? 0 : Math.Min(val1: (DecalRows - 1), val2: Math.Min(val1: lines.Count, val2: MaxHistoryLines)));
        var firstShown = ((lines is null) ? 0 : (lines.Count - shown));

        for (var row = 0; (row < shown); row++) {
            WriteDecalRow(cells: cells, atlas: atlas, atlasWidth: atlasWidth, atlasHeight: atlasHeight, row: row, text: lines![(firstShown + row)]);
        }

        WriteDecalRow(cells: cells, atlas: atlas, atlasWidth: atlasWidth, atlasHeight: atlasHeight, row: (DecalRows - 1), text: (PromptPrefix + (frame.Input ?? string.Empty)));
    }

    private static void WriteDecalRow(uint[] cells, FontAtlas atlas, float atlasWidth, float atlasHeight, int row, string text) {
        var count = Math.Min(val1: text.Length, val2: DecalColumns);

        for (var column = 0; (column < count); column++) {
            var character = text[column];

            if ((character == ' ') || !atlas.TryGetGlyph(unicode: character, glyph: out var glyph) || (glyph.AtlasBounds is not { } bounds)) {
                continue; // blank cell (already the background)
            }

            var index = (((row * DecalColumns) + column) * 4);

            cells[(index + 0)] = PackUv(u: (bounds.Left / atlasWidth), v: (bounds.Top / atlasHeight));
            cells[(index + 1)] = PackUv(u: (bounds.Right / atlasWidth), v: (bounds.Bottom / atlasHeight));
        }
    }
    private static uint PackUv(float u, float v) {
        var packedU = (uint)Math.Clamp(value: (int)MathF.Round(x: (u * 65535f)), max: 65535, min: 0);
        var packedV = (uint)Math.Clamp(value: (int)MathF.Round(x: (v * 65535f)), max: 65535, min: 0);

        return packedU | (packedV << 16);
    }
    private static uint PackRgba(byte[] color) =>
        (uint)color[0] | ((uint)color[1] << 8) | ((uint)color[2] << 16) | ((uint)color[3] << 24);

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
        // The whole visible block as ONE enriched rune stream (history rows, then the prompt row) so TextLayout owns the
        // per-line baseline stepping AND carries each glyph's effect onto its placement. Each line is compiled from its
        // BBCode markup independently (so an unclosed tag never bleeds across lines) and clipped to the column count.
        var runes = new List<TextEffectRune>();

        for (var row = 0; (row < shown); row++) {
            AppendLine(runes: runes, rawLine: lines![(firstShown + row)], terminate: true);
        }

        // Pad the history block to a fixed height so the prompt always sits on the bottom row (empty rows advance the
        // baseline without drawing).
        for (var row = shown; (row < m_historyRows); row++) {
            runes.Add(item: new TextEffectRune(Rune: NewLineRune, Effect: TextEffect.None));
        }

        AppendLine(runes: runes, rawLine: (PromptPrefix + (frame.Input ?? string.Empty)), terminate: false);

        var layout = m_layout.Layout(atlas: atlas, runes: runes, scale: m_cellHeight);
        // The block's top edge in layout space: line 0's baseline is at y = 0, so its plane top is the ascender (one
        // em) up; every placement maps down from there into the y-down framebuffer.
        var blockTopY = (atlas.Metrics.Ascender * m_cellHeight);
        var motionEnabled = TextMotionState.MotionEnabled;
        var glyphIndex = 0;

        foreach (var placement in layout.Placements) {
            // Each placement carries its effect; resolving it against the deterministic content tick yields the
            // per-glyph channel (offset/coverage/tint/weight) the 1:1 CPU blit consumes. Unenriched glyphs resolve to
            // the identity channel, so plain console text renders exactly as before.
            var channel = TextGlyphChannel.Resolve(
                effect: placement.Effect,
                contentTick: m_contentTick,
                ticksPerSecond: TicksPerSecond,
                glyphPhase: placement.BaselineOrigin.X,
                glyphIndex: glyphIndex,
                motionEnabled: motionEnabled
            );

            BlitGlyph(atlasRgba: m_atlasRgba, blockTopY: blockTopY, channel: in channel, placement: in placement);
            glyphIndex++;
        }
    }

    // The line-feed rune used to separate laid-out lines in the enriched block.
    private static readonly System.Text.Rune NewLineRune = new(value: '\n');

    // Parses one raw console line as an enrichment control-char stream (clipped to the CRT columns) and appends its
    // runes, optionally followed by a line-feed rune. Enrichment is OPT-IN and line-scoped: only the compiled stream a
    // producer (text.say / text.motion) intentionally wrote carries tags; an ordinary console line — including one that
    // merely contains '[' and ']' — has no control chars, so every rune is unenriched and renders exactly as before.
    private void AppendLine(List<TextEffectRune> runes, string rawLine, bool terminate) {
        foreach (var rune in TextEnrichmentTags.EnumerateRichTextRunes(text: Clip(text: rawLine))) {
            runes.Add(item: rune);
        }

        if (terminate) {
            runes.Add(item: new TextEffectRune(Rune: NewLineRune, Effect: TextEffect.None));
        }
    }

    // Whether any shown line carries an animated enrichment tag (a motion effect or a reveal) — the cheap check that
    // decides whether the content tick folds into the dirty signature this frame. Only compiled-stream lines match: the
    // markers are the tag-start control char followed by a canonical animated tag name, so ordinary text never trips it.
    private bool ShownContentAnimates(in ConsoleTextFrame frame) {
        var lines = frame.Lines;

        if (lines is null) {
            return false;
        }

        var shown = Math.Min(val1: m_historyRows, val2: Math.Min(val1: lines.Count, val2: MaxHistoryLines));
        var firstShown = (lines.Count - shown);

        for (var index = firstShown; (index < lines.Count); index++) {
            if (LineHasAnimatedTag(line: lines[index])) {
                return true;
            }
        }

        return false;
    }

    // The tag-start control char + each canonical animated tag name (motion kinds + reveal); BBCode names all canonicalize
    // through one table on compile, so these six cover every animated stream.
    private static readonly string[] AnimatedTagMarkers = [
        $"{TextEnrichmentTags.TagStart}wave",
        $"{TextEnrichmentTags.TagStart}shake",
        $"{TextEnrichmentTags.TagStart}pulse",
        $"{TextEnrichmentTags.TagStart}jitter",
        $"{TextEnrichmentTags.TagStart}dissolve",
        $"{TextEnrichmentTags.TagStart}reveal",
    ];

    private static bool LineHasAnimatedTag(string line) {
        foreach (var marker in AnimatedTagMarkers) {
            if (line.Contains(value: marker, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    // Samples one glyph's SoftMask coverage rectangle into the framebuffer at its laid-out position — a 1:1 copy (the
    // atlas cell and the plane quad are both cellWidth x cellHeight px at the native atlas size), blending a phosphor
    // colour over the CRT background by coverage. The enrichment channel nudges the glyph (offset), scales its coverage
    // (reveal/dissolve + weight), and overrides the colour (tint); an identity channel reproduces the plain blit.
    private void BlitGlyph(byte[] atlasRgba, float blockTopY, in TextGlyphChannel channel, in TextGlyphPlacement placement) {
        // A fully eroded glyph (reveal not yet reached / dissolve gap) draws nothing.
        if (channel.Coverage <= 0.0f) {
            return;
        }

        var plane = placement.PlaneBounds;
        var source = placement.AtlasBounds;
        // The per-glyph offset is in the same y-up scaled units as the plane; +Y lifts the glyph, so it subtracts from
        // the y-down destination row.
        var destLeft = (MarginX + (int)MathF.Round(x: (plane.Left + channel.Offset.X)));
        var destTop = (MarginY + (int)MathF.Round(x: ((blockTopY - plane.Top) - channel.Offset.Y)));
        var width = (int)MathF.Round(x: (plane.Right - plane.Left));
        var height = (int)MathF.Round(x: (plane.Top - plane.Bottom));
        var sourceLeft = (int)MathF.Round(x: source.Left);
        var sourceTop = (int)MathF.Round(x: source.Top); // atlas texels are top-down (this generator's orientation)
        // The phosphor colour: the enrichment tint when set, otherwise the CRT's default green.
        var color = (channel.HasTint ? ToColorBytes(tint: channel.Tint) : TextColor);
        // Coverage scale folds the reveal/dissolve coverage with a fake-bold weight boost, clamped to unit.
        var coverageScale = Math.Clamp(value: (channel.Coverage * (1.0f + MathF.Max(x: 0.0f, y: channel.WeightBias))), max: 1.0f, min: 0.0f);

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

                var sampled = atlasRgba[((((sourceRow + sourceLeft) + px) * BytesPerPixel) + 3)];

                if (sampled == 0) {
                    continue;
                }

                var coverage = (byte)(sampled * coverageScale);

                if (coverage == 0) {
                    continue;
                }

                BlendPixel(offset: ((destRow + destX) * BytesPerPixel), color: color, coverage: coverage);
            }
        }
    }

    // Blends a phosphor colour over the current pixel by 8-bit coverage (out = bg + (colour - bg) * coverage/255).
    private void BlendPixel(int offset, byte[] color, byte coverage) {
        m_pixels[(offset + 0)] = (byte)(m_pixels[(offset + 0)] + (((color[0] - m_pixels[(offset + 0)]) * coverage) / 255));
        m_pixels[(offset + 1)] = (byte)(m_pixels[(offset + 1)] + (((color[1] - m_pixels[(offset + 1)]) * coverage) / 255));
        m_pixels[(offset + 2)] = (byte)(m_pixels[(offset + 2)] + (((color[2] - m_pixels[(offset + 2)]) * coverage) / 255));
        m_pixels[(offset + 3)] = 0xff;
    }

    // Converts an enrichment tint (0..1 RGBA) into the CRT's 8-bit RGB triple (alpha rides the coverage, not the tint).
    private static byte[] ToColorBytes(System.Numerics.Vector4 tint) => [
        (byte)Math.Clamp(value: (int)MathF.Round(x: (tint.X * 255.0f)), max: 255, min: 0),
        (byte)Math.Clamp(value: (int)MathF.Round(x: (tint.Y * 255.0f)), max: 255, min: 0),
        (byte)Math.Clamp(value: (int)MathF.Round(x: (tint.Z * 255.0f)), max: 255, min: 0),
        0xff,
    ];

    // Fills the framebuffer with the CRT background, darkening every other scanline a touch for a subtle phosphor-line
    // read (deterministic, purely cosmetic).
    private void FillBackground() {
        for (var y = 0; (y < CrtHeight); y++) {
            var scanline = ((y & 1) == 0);
            var r = (scanline ? BackgroundColor[0] : (byte)((BackgroundColor[0] * 3) / 4));
            var g = (scanline ? BackgroundColor[1] : (byte)((BackgroundColor[1] * 3) / 4));
            var b = (scanline ? BackgroundColor[2] : (byte)((BackgroundColor[2] * 3) / 4));
            var row = ((y * CrtWidth) * BytesPerPixel);

            for (var x = 0; (x < CrtWidth); x++) {
                var offset = (row + (x * BytesPerPixel));

                m_pixels[(offset + 0)] = r;
                m_pixels[(offset + 1)] = g;
                m_pixels[(offset + 2)] = b;
                m_pixels[(offset + 3)] = 0xff;
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
        var image = new byte[((atlasWidth * cellHeight) * BytesPerPixel)];
        var coverage = font.PackedCoverage;
        var glyphStride = (cellWidth * cellHeight);
        var glyphs = new List<FontAtlasGlyph>(capacity: glyphCount);
        var advanceEm = (cellWidth / (float)cellHeight);

        for (var index = 0; (index < glyphCount); index++) {
            var glyphBase = (index * glyphStride);
            var atlasLeft = (index * cellWidth);

            for (var y = 0; (y < cellHeight); y++) {
                for (var x = 0; (x < cellWidth); x++) {
                    var packedIndex = ((glyphBase + (y * cellWidth)) + x);
                    // PackedCoverage stores four coverage bytes per uint, little-endian (ConsoleGlyphFont's packing).
                    var value = (byte)((coverage[(packedIndex >> 2)] >> ((packedIndex & 3) * 8)) & 0xff);
                    var offset = (((((y * atlasWidth) + atlasLeft) + x)) * BytesPerPixel);

                    image[(offset + 0)] = value;
                    image[(offset + 1)] = value;
                    image[(offset + 2)] = value;
                    image[(offset + 3)] = value;
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
