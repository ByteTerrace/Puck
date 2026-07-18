namespace Puck.Overlays;

/// <summary>A panel's chrome recipe: which scrim + corner radius the token block resolves for it.</summary>
public enum OverlayPanelStyle : uint {
    /// <summary>The full panel scrim (0.90) with the r.3 radius.</summary>
    Panel = 0,
    /// <summary>The strip scrim (0.86) with the r.2 radius.</summary>
    Strip = 1,
    /// <summary>The chip scrim (0.94) with the r.2 radius.</summary>
    Chip = 2,
}

/// <summary>
/// The unified overlay's record packer: writers call the <c>Write*</c> methods in PIXEL coordinates (the design
/// tokens are px values) and the builder packs normalized screen-space records into the one storage-buffer scratch —
/// panels, then a flat element list (rects, fixed-cell text runs, icon chips), then the pre-resolved glyph-code
/// words the text runs index. Preallocated once; <see cref="BeginFrame"/> resets it with zero steady-state
/// allocation. Word layouts are documented at each writer — KEEP IN SYNC with <c>overlay-unified.frag.hlsl</c>.
/// </summary>
/// <remarks>
/// Buffer geography (32-bit words): <c>[0, TokenWords)</c> the <see cref="OverlayTokenBlock"/> slab and
/// <c>[TokenWords, PanelBaseWords)</c> the glyph SDF pack — both static, uploaded once by the node —
/// then the per-frame region this builder owns: panel records, element records, glyph-code words, and the clip
/// table. <para>CLIP CONTRACT (UIE-4): a writer scoping per-seat UI wraps its records in
/// <see cref="BeginClip"/>/<see cref="EndClip"/>; every record carries a clip index (word 9; 0 = unclipped) into
/// the clip table and the shader discards the record's contribution outside its rect — placement inside a seat
/// viewport is therefore also CLIPPING to it.</para><para>OVERFLOW CONTRACT (UIE-8): a record past a capacity is
/// dropped and COUNTED (<see cref="DroppedPanels"/>/<see cref="DroppedElements"/>/<see cref="DroppedTextWords"/>/
/// <see cref="DroppedClips"/>, reset each frame) so the node can narrate loudly; the tail reservation
/// (<see cref="ReserveTail"/>/<see cref="ReleaseTail"/>) holds capacity back from earlier writers so the LAST,
/// most urgent surface (the toast) can never be starved by writer order.</para>
/// </remarks>
public sealed class OverlayFrameBuilder {
    /// <summary>Words per panel record.</summary>
    public const int PanelWords = 12;
    /// <summary>Words per element record.</summary>
    public const int ElementWords = 12;
    /// <summary>Words per clip-table rect (normalized x, y, w, h).</summary>
    public const int ClipWords = 4;
    /// <summary>The panel-record capacity.</summary>
    public const int MaxPanels = 8;
    /// <summary>The element-record capacity (rects + text runs + icon chips, all surfaces together).</summary>
    public const int MaxElements = 192;
    /// <summary>The clip-rect capacity (index 0 is the unclipped sentinel; the table holds indices 1..MaxClips).</summary>
    public const int MaxClips = 8;
    /// <summary>The glyph-code word capacity shared by every text run in a frame.</summary>
    public const int TextWordCapacity = 4096;

    private readonly OverlayGlyphSdfPack m_glyphs;
    private readonly uint[] m_scratch;
    private readonly float m_inverseWidth;
    private readonly float m_inverseHeight;
    // The active clip index records are stamped with: 0 = unclipped, 1..MaxClips = a table rect, -1 = the clip
    // table overflowed — records inside the scope DROP (never bleed past a seat boundary) and count as overflow.
    private int m_activeClip;
    private int m_clipCount;
    private int m_elementCount;
    private int m_panelCount;
    private int m_textWordCount;
    private int m_droppedClips;
    private int m_droppedElements;
    private int m_droppedPanels;
    private int m_droppedTextWords;
    private int m_reservedElements;
    private int m_reservedPanels;
    private int m_reservedTextWords;

    /// <summary>Initializes a new instance of the <see cref="OverlayFrameBuilder"/> class.</summary>
    /// <param name="glyphs">The shared glyph SDF pack (cell metrics + the static prefix the node uploads).</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="glyphs"/> is <see langword="null"/>.</exception>
    public OverlayFrameBuilder(OverlayGlyphSdfPack glyphs, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(argument: glyphs);

        m_glyphs = glyphs;
        Width = width;
        Height = height;
        m_inverseWidth = (1f / width);
        m_inverseHeight = (1f / height);
        PanelBaseWords = (OverlayTokenBlock.WordCount + glyphs.PackedSdf.Count);
        ElementBaseWords = (PanelBaseWords + (MaxPanels * PanelWords));
        TextBaseWords = (ElementBaseWords + (MaxElements * ElementWords));
        ClipBaseWords = (TextBaseWords + TextWordCapacity);

        // Pad the total to a uint4 boundary — the storage buffer is bound as a StructuredBuffer<uint4> (the D3D12
        // allocator's stride-16 SRV), so its element count must divide exactly.
        var total = (ClipBaseWords + (MaxClips * ClipWords));

        WordCount = ((total + 3) & ~3);
        m_scratch = new uint[WordCount];

        OverlayTokenBlock.Write(destination: m_scratch);

        for (var index = 0; (index < glyphs.PackedSdf.Count); index++) {
            m_scratch[(OverlayTokenBlock.WordCount + index)] = glyphs.PackedSdf[index];
        }
    }

    /// <summary>Gets the render height in pixels.</summary>
    public uint Height { get; }
    /// <summary>Gets the render width in pixels.</summary>
    public uint Width { get; }
    /// <summary>Gets the glyph pack the text runs and icon badges sample.</summary>
    public OverlayGlyphSdfPack Glyphs => m_glyphs;
    /// <summary>Gets the first panel record's word index (also the length of the static token+glyph prefix).</summary>
    public int PanelBaseWords { get; }
    /// <summary>Gets the first element record's word index.</summary>
    public int ElementBaseWords { get; }
    /// <summary>Gets the first glyph-code word's index.</summary>
    public int TextBaseWords { get; }
    /// <summary>Gets the clip table's first word index.</summary>
    public int ClipBaseWords { get; }
    /// <summary>Gets the buffer's total word count (a multiple of 4).</summary>
    public int WordCount { get; }
    /// <summary>Gets the number of panels packed this frame.</summary>
    public int PanelCount => m_panelCount;
    /// <summary>Gets the number of elements packed this frame.</summary>
    public int ElementCount => m_elementCount;
    /// <summary>Gets the panel records dropped at capacity this frame.</summary>
    public int DroppedPanels => m_droppedPanels;
    /// <summary>Gets the element records (rects, text runs, icon chips) dropped at capacity this frame.</summary>
    public int DroppedElements => m_droppedElements;
    /// <summary>Gets the glyph-code words refused by the shared text capacity this frame.</summary>
    public int DroppedTextWords => m_droppedTextWords;
    /// <summary>Gets the clip rects dropped at table capacity this frame (records inside such a scope also drop).</summary>
    public int DroppedClips => m_droppedClips;
    /// <summary>Gets whether any record overflowed a capacity this frame — the node's loud-once narration gate.</summary>
    public bool HasOverflow => (((m_droppedPanels | m_droppedElements) | (m_droppedTextWords | m_droppedClips)) > 0);
    /// <summary>Gets whether this frame packed anything to draw.</summary>
    public bool HasContent => ((m_panelCount > 0) || (m_elementCount > 0));
    /// <summary>Gets the whole scratch buffer (the node's upload view).</summary>
    public ReadOnlySpan<uint> Scratch => m_scratch;

    /// <summary>Resets the per-frame region (records + glyph codes + clip table), the clip scope, the overflow
    /// counters, and the tail reservation. The static token/glyph prefix is untouched.</summary>
    public void BeginFrame() {
        m_panelCount = 0;
        m_elementCount = 0;
        m_textWordCount = 0;
        m_activeClip = 0;
        m_clipCount = 0;
        m_droppedPanels = 0;
        m_droppedElements = 0;
        m_droppedTextWords = 0;
        m_droppedClips = 0;
        m_reservedPanels = 0;
        m_reservedElements = 0;
        m_reservedTextWords = 0;
        Array.Clear(array: m_scratch, index: PanelBaseWords, length: (WordCount - PanelBaseWords));
    }

    /// <summary>Holds capacity back from the writers that follow, so the LAST writer in the node's order (the most
    /// urgent surface — the toast) can never be starved by an earlier one. Set once after <see cref="BeginFrame"/>;
    /// released with <see cref="ReleaseTail"/> immediately before the reserved writer emits.</summary>
    /// <param name="panels">Panel records to hold back.</param>
    /// <param name="elements">Element records to hold back.</param>
    /// <param name="textWords">Glyph-code words to hold back.</param>
    public void ReserveTail(int panels, int elements, int textWords) {
        m_reservedPanels = Math.Max(val1: 0, val2: panels);
        m_reservedElements = Math.Max(val1: 0, val2: elements);
        m_reservedTextWords = Math.Max(val1: 0, val2: textWords);
    }

    /// <summary>Releases the tail reservation (the reserved writer emits next).</summary>
    public void ReleaseTail() {
        m_reservedPanels = 0;
        m_reservedElements = 0;
        m_reservedTextWords = 0;
    }

    /// <summary>Opens a clip scope: records written before <see cref="EndClip"/> are discarded by the shader outside
    /// this rect — the per-seat viewport invariant every split-screen writer rides. Scopes do not nest (the last
    /// call wins). On clip-table overflow the scope's records DROP (counted) rather than bleed across a seat.</summary>
    /// <param name="x">Left, px.</param>
    /// <param name="y">Top, px.</param>
    /// <param name="w">Width, px.</param>
    /// <param name="h">Height, px.</param>
    public void BeginClip(float x, float y, float w, float h) {
        if (m_clipCount >= MaxClips) {
            m_droppedClips++;
            m_activeClip = -1;

            return;
        }

        var offset = (ClipBaseWords + (m_clipCount * ClipWords));

        m_scratch[offset] = Pack(value: (x * m_inverseWidth));
        m_scratch[(offset + 1)] = Pack(value: (y * m_inverseHeight));
        m_scratch[(offset + 2)] = Pack(value: (w * m_inverseWidth));
        m_scratch[(offset + 3)] = Pack(value: (h * m_inverseHeight));
        m_clipCount++;
        m_activeClip = m_clipCount;
    }

    /// <summary>Closes the clip scope (records return to unclipped).</summary>
    public void EndClip() => m_activeClip = 0;

    /// <summary>Packs one panel-chrome record (scrim fill + hairline + optional title band + optional Tier-1
    /// status ring/bloom). Word layout (12): 0..3 rect x,y,w,h (normalized floats) · 4 flags (bit0 = title band) ·
    /// 5 style kind · 6 ring role (0 = none) · 7 band height (normalized y float) · 8 alpha · 9 clip index ·
    /// 10..11 reserved.</summary>
    /// <param name="x">Left, px.</param>
    /// <param name="y">Top, px.</param>
    /// <param name="w">Width, px.</param>
    /// <param name="h">Height, px.</param>
    /// <param name="titleBand">Whether the panel carries a title band + divider.</param>
    /// <param name="bandHeight">The title band height, px.</param>
    /// <param name="style">The chrome recipe.</param>
    /// <param name="ringRole">The Tier-1 bloom ring hue, or <see langword="null"/> for a resting panel.</param>
    /// <param name="alpha">The whole panel's opacity.</param>
    public void WritePanel(float x, float y, float w, float h, bool titleBand, float bandHeight, OverlayPanelStyle style, OverlayColorRole? ringRole, float alpha) {
        if ((m_panelCount >= (MaxPanels - m_reservedPanels)) || (m_activeClip < 0)) {
            m_droppedPanels++;

            return;
        }

        var offset = (PanelBaseWords + (m_panelCount * PanelWords));

        m_scratch[offset] = Pack(value: (x * m_inverseWidth));
        m_scratch[(offset + 1)] = Pack(value: (y * m_inverseHeight));
        m_scratch[(offset + 2)] = Pack(value: (w * m_inverseWidth));
        m_scratch[(offset + 3)] = Pack(value: (h * m_inverseHeight));
        m_scratch[(offset + 4)] = (titleBand ? 1u : 0u);
        m_scratch[(offset + 5)] = (uint)style;
        m_scratch[(offset + 6)] = ((ringRole is { } ring) ? (uint)ring : 0u);
        m_scratch[(offset + 7)] = Pack(value: (bandHeight * m_inverseHeight));
        m_scratch[(offset + 8)] = Pack(value: alpha);
        m_scratch[(offset + 9)] = (uint)m_activeClip;
        m_panelCount++;
    }

    /// <summary>Packs one rounded-rect element (chip fill, selection fill, accent tick, state rail). Word layout
    /// (12): 0..3 rect (normalized) · 4 = 1 | (role &lt;&lt; 4) · 6 corner radius (px float) · 7 alpha ·
    /// 9 clip index.</summary>
    /// <param name="x">Left, px.</param>
    /// <param name="y">Top, px.</param>
    /// <param name="w">Width, px.</param>
    /// <param name="h">Height, px.</param>
    /// <param name="role">The fill's color role (the role's own alpha composes with <paramref name="alpha"/>).</param>
    /// <param name="radius">The corner radius, px.</param>
    /// <param name="alpha">The element opacity.</param>
    public void WriteRect(float x, float y, float w, float h, OverlayColorRole role, float radius, float alpha) {
        if ((m_elementCount >= (MaxElements - m_reservedElements)) || (m_activeClip < 0)) {
            m_droppedElements++;

            return;
        }

        var offset = (ElementBaseWords + (m_elementCount * ElementWords));

        m_scratch[offset] = Pack(value: (x * m_inverseWidth));
        m_scratch[(offset + 1)] = Pack(value: (y * m_inverseHeight));
        m_scratch[(offset + 2)] = Pack(value: (w * m_inverseWidth));
        m_scratch[(offset + 3)] = Pack(value: (h * m_inverseHeight));
        m_scratch[(offset + 4)] = (1u | ((uint)role << 4));
        m_scratch[(offset + 6)] = Pack(value: radius);
        m_scratch[(offset + 7)] = Pack(value: alpha);
        m_scratch[(offset + 9)] = (uint)m_activeClip;
        m_elementCount++;
    }

    /// <summary>Packs one fixed-cell text run (codes stored PRE-RESOLVED as atlas glyph indices; anything outside
    /// printable ASCII renders as the blank space cell). Word layout (12): 0..1 origin (normalized) · 2..3 one glyph
    /// cell's on-screen w/h (normalized) · 4 = 0 | (role &lt;&lt; 4) · 5 glyph start (word offset into the text
    /// region) · 6 glyph count · 7 alpha · 9 clip index.</summary>
    /// <param name="x">The run origin's left, px.</param>
    /// <param name="y">The run origin's top, px.</param>
    /// <param name="text">The characters to pack.</param>
    /// <param name="cellHeight">The on-screen glyph cell height, px (see <see cref="CellHeight"/>).</param>
    /// <param name="role">The text color role.</param>
    /// <param name="alpha">The run opacity.</param>
    /// <param name="maxChars">Clips the run without allocating; characters beyond this are dropped.</param>
    public void WriteText(float x, float y, ReadOnlySpan<char> text, int cellHeight, OverlayColorRole role, float alpha, int maxChars = int.MaxValue) {
        var count = Math.Min(val1: text.Length, val2: maxChars);

        if (count <= 0) {
            return;
        }

        if ((m_elementCount >= (MaxElements - m_reservedElements)) || (m_activeClip < 0)) {
            m_droppedElements++;

            return;
        }

        if ((m_textWordCount + count) > (TextWordCapacity - m_reservedTextWords)) {
            m_droppedElements++;
            m_droppedTextWords += count;

            return;
        }

        var start = m_textWordCount;

        for (var index = 0; (index < count); index++) {
            var glyph = OverlayGlyphSdfPack.GlyphIndex(codePoint: text[index]);

            m_scratch[(TextBaseWords + m_textWordCount++)] = (uint)Math.Max(val1: 0, val2: glyph);
        }

        var offset = (ElementBaseWords + (m_elementCount * ElementWords));

        m_scratch[offset] = Pack(value: (x * m_inverseWidth));
        m_scratch[(offset + 1)] = Pack(value: (y * m_inverseHeight));
        m_scratch[(offset + 2)] = Pack(value: (CellWidth(cellHeight: cellHeight) * m_inverseWidth));
        m_scratch[(offset + 3)] = Pack(value: (cellHeight * m_inverseHeight));
        m_scratch[(offset + 4)] = ((uint)role << 4);
        m_scratch[(offset + 5)] = (uint)start;
        m_scratch[(offset + 6)] = (uint)count;
        m_scratch[(offset + 7)] = Pack(value: alpha);
        m_scratch[(offset + 9)] = (uint)m_activeClip;
        m_elementCount++;
    }

    /// <summary>Packs one icon chip (the binding-bar repertoire folded in as an element kind: rounded plate with the
    /// four chip-state tiers, a procedural action icon, and a gamepad badge — atlas letters or procedural symbols).
    /// Word layout (12): 0..1 plate center (normalized) · 2 plate half-size (px) · 3 badge half-size (px) ·
    /// 4 = 2 | (role &lt;&lt; 4, unused) · 5 glyph &lt;&lt; 16 | icon · 6 state (alpha byte | pressed&lt;&lt;8 |
    /// (char0+1)&lt;&lt;9 | (char1+1)&lt;&lt;16 | accent&lt;&lt;23 | bound&lt;&lt;24) · 7..8 badge center offset from
    /// the plate center (px floats) · 9 clip index · 10..11 reserved.</summary>
    /// <param name="centerX">The plate center x, px.</param>
    /// <param name="centerY">The plate center y, px.</param>
    /// <param name="plateHalf">The plate half-extent, px.</param>
    /// <param name="glyphHalf">The badge half-extent, px (0 = no badge).</param>
    /// <param name="glyphOffsetX">The badge center's x offset from the plate center, px.</param>
    /// <param name="glyphOffsetY">The badge center's y offset from the plate center, px.</param>
    /// <param name="glyph">The physical-button badge glyph.</param>
    /// <param name="icon">The bound action's icon.</param>
    /// <param name="alpha">The chip opacity.</param>
    /// <param name="pressed">The HELD tier-1 state.</param>
    /// <param name="accent">The ACCENT tier-1 state (the context-primary action).</param>
    /// <param name="bound">Whether an action is bound (<see langword="false"/> = the DISABLED tier-0 look).</param>
    public void WriteIcon(float centerX, float centerY, float plateHalf, float glyphHalf, float glyphOffsetX, float glyphOffsetY, OverlayGlyphId glyph, OverlayIconId icon, float alpha, bool pressed, bool accent, bool bound) {
        if ((m_elementCount >= (MaxElements - m_reservedElements)) || (m_activeClip < 0)) {
            m_droppedElements++;

            return;
        }

        var offset = (ElementBaseWords + (m_elementCount * ElementWords));

        m_scratch[offset] = Pack(value: (centerX * m_inverseWidth));
        m_scratch[(offset + 1)] = Pack(value: (centerY * m_inverseHeight));
        m_scratch[(offset + 2)] = Pack(value: plateHalf);
        m_scratch[(offset + 3)] = Pack(value: glyphHalf);
        m_scratch[(offset + 4)] = 2u;
        m_scratch[(offset + 5)] = (((uint)glyph << 16) | (uint)icon);
        m_scratch[(offset + 6)] = ((uint)(Math.Clamp(value: alpha, max: 1f, min: 0f) * 255f)
            | (pressed ? (1u << 8) : 0u)
            | PackBadgeLabel(glyph: glyph)
            | (accent ? (1u << 23) : 0u)
            | (bound ? (1u << 24) : 0u));
        m_scratch[(offset + 7)] = Pack(value: glyphOffsetX);
        m_scratch[(offset + 8)] = Pack(value: glyphOffsetY);
        m_scratch[(offset + 9)] = (uint)m_activeClip;
        m_elementCount++;
    }

    /// <summary>The on-screen glyph cell height for a token type SIZE — the proven size-to-cell ratio
    /// (<c>TypeMonoLine / TypeMonoSize</c> = 1.5), so a 12px mono run gets an 18px cell.</summary>
    /// <param name="sizePx">The token type size, px.</param>
    /// <returns>The cell height, px.</returns>
    public static int CellHeight(float sizePx) =>
        Math.Max(val1: 1, val2: (int)MathF.Round(x: (sizePx * (DesignTokens.Type.TypeMonoLine / DesignTokens.Type.TypeMonoSize))));

    /// <summary>The on-screen glyph cell width for a cell height, preserving the atlas' cell aspect.</summary>
    /// <param name="cellHeight">The cell height, px.</param>
    /// <returns>The cell width, px.</returns>
    public float CellWidth(int cellHeight) =>
        MathF.Max(x: 1f, y: MathF.Round(x: ((cellHeight * (float)m_glyphs.AtlasCellWidth) / m_glyphs.AtlasCellHeight)));

    /// <summary>The on-screen width of a run of characters at a cell height.</summary>
    /// <param name="chars">The character count.</param>
    /// <param name="cellHeight">The cell height, px.</param>
    /// <returns>The run width, px.</returns>
    public float TextWidth(int chars, int cellHeight) => (chars * CellWidth(cellHeight: cellHeight));

    // A badge label's two 7-bit lanes at bits 9-15 (char0) and 16-22 (char1), each an (atlas glyph index + 1), or 0
    // for the iconographic glyphs that stay procedural. The shader uses a present char0 as the atlas-text flag.
    private static uint PackBadgeLabel(OverlayGlyphId glyph) {
        if (OverlayGamepadGlyphs.BadgeLabel(glyph: glyph) is not { Length: > 0 } label) {
            return 0u;
        }

        var first = OverlayGlyphSdfPack.GlyphIndex(codePoint: label[0]);

        if (first < 0) {
            return 0u;
        }

        var bits = ((uint)(first + 1) << 9);

        if (label.Length > 1) {
            var second = OverlayGlyphSdfPack.GlyphIndex(codePoint: label[1]);

            if (second >= 0) {
                bits |= ((uint)(second + 1) << 16);
            }
        }

        return bits;
    }
    private static uint Pack(float value) => BitConverter.SingleToUInt32Bits(value: value);
}
