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
/// The unified overlay's record packer: writers call the <c>Write*</c> methods in pixel coordinates (the design
/// tokens are px values) and the builder packs normalized screen-space records into the one storage-buffer scratch —
/// panels, then a flat element list (rects, fixed-cell text runs, icon chips), then the pre-resolved glyph-code
/// words the text runs index. Preallocated once; <see cref="BeginFrame"/> resets it with zero steady-state
/// allocation. Word layouts are documented at each writer — KEEP IN SYNC with <c>overlay-unified.frag.hlsl</c>.
/// </summary>
/// <remarks>
/// Buffer geography (32-bit words): <c>[0, TokenWords)</c> the <see cref="OverlayTokenBlock"/> slab and
/// <c>[TokenWords, PanelBaseWords)</c> the glyph SDF pack — both static, uploaded once by the node —
/// then the per-frame region this builder owns: panel records, element records, glyph-code words, and the clip
/// table. <para><b>Channel contract.</b> Every write belongs to a declared <see cref="OverlayChannel"/>, opened with
/// <see cref="BeginChannel"/> and closed with <see cref="EndChannel"/>; a write outside a channel scope is a
/// programming error and throws. A channel may write up to its <see cref="OverlayChannelLeases">lease</see> and no
/// further: it clips at its own boundary, the drop is attributed to it (<see cref="Dropped"/>), and it can never
/// consume another channel's capacity.</para><para><b>Clip contract.</b> A writer scoping per-seat UI wraps its records in
/// <see cref="BeginClip"/>/<see cref="EndClip"/>; every record carries a clip index (word 9; 0 = unclipped) into
/// the clip table and the shader discards the record's contribution outside its rect — placement inside a seat
/// viewport is therefore also clipping to it. A scope whose rect could not be recorded still scopes (its records
/// drop rather than bleed past a seat boundary), and the scope's end unwinds that state, so an overflowed scope can
/// never take the records that follow it down with it.</para>
/// </remarks>
public sealed class OverlayFrameBuilder {
    /// <summary>Words per panel record.</summary>
    public const int PanelWords = 12;
    /// <summary>Words per element record.</summary>
    public const int ElementWords = 12;
    /// <summary>Words per clip-table rect (normalized x, y, w, h).</summary>
    public const int ClipWords = 4;
    /// <summary>The panel-record ceiling — a cannot-overflow backstop, never a budget.</summary>
    /// <remarks>What a capacity here is: the point past which a record cannot be addressed at all. The budget is
    /// <see cref="OverlayChannelLeases"/>' per-channel reservations (the five first-party writers plus the authored
    /// world-scope-and-reserved-seat-scope HUD reservation — see <c>OverlayChannelLeases</c>'
    /// <c>HudPanels</c>/<c>HudElements</c>/<c>HudTextWords</c>/<c>HudClips</c>), which sum strictly below every
    /// capacity; the remainder below is simply unclaimed — no addon/lease admission model reads it (that
    /// contributor-lease design was never built and is not being built here). Capacity costs memory only: the
    /// fragment shader loops to the written counts it receives in push constants, never to a capacity, so raising a
    /// ceiling never enters per-pixel cost.</remarks>
    public const int MaxPanels = 16;
    /// <summary>The element-record ceiling (rects + rings + text runs + icon chips together) — a cannot-overflow
    /// backstop, never a budget; see <see cref="MaxPanels"/> for what that means.</summary>
    public const int MaxElements = 1024;
    /// <summary>The clip-rect ceiling (index 0 is the unclipped sentinel; the table holds indices 1..MaxClips) — a
    /// cannot-overflow backstop, never a budget; see <see cref="MaxPanels"/> for what that means.</summary>
    public const int MaxClips = 32;
    /// <summary>The glyph-code word ceiling every text run in a frame draws from — a cannot-overflow backstop, never
    /// a budget; see <see cref="MaxPanels"/> for what that means.</summary>
    public const int TextWordCapacity = 16384;

    // THE STATIC ASSERTION. Each of these is the capacity remaining UNCLAIMED after every channel's reservation —
    // the five first-party writers plus the authored-HUD reservation (OverlayChannelLeases.TotalElements /
    // TotalTextWords / TotalPanels / TotalClips, which folds HudElements/HudTextWords/HudPanels/HudClips into the
    // sum). An over-subscribed table makes one of these NEGATIVE, and a negative constant cannot convert to uint —
    // the BUILD fails here, at the resource whose reservations over-ran, before any frame is composed.
    /// <summary>The element records no channel has reserved.</summary>
    public const uint ElementHeadroom = (uint)(MaxElements - OverlayChannelLeases.TotalElements);
    /// <summary>The glyph-code words no channel has reserved.</summary>
    public const uint TextWordHeadroom = (uint)(TextWordCapacity - OverlayChannelLeases.TotalTextWords);
    /// <summary>The panel records no channel has reserved.</summary>
    public const uint PanelHeadroom = (uint)(MaxPanels - OverlayChannelLeases.TotalPanels);
    /// <summary>The clip-table rects no channel has reserved.</summary>
    public const uint ClipHeadroom = (uint)(MaxClips - OverlayChannelLeases.TotalClips);

    private readonly OverlayGlyphSdfPack m_glyphs;
    private readonly uint[] m_scratch;
    private readonly OverlayChannelReservation[] m_reservations = new OverlayChannelReservation[OverlayChannelLeases.Count];
    private readonly Counters[] m_written = new Counters[OverlayChannelLeases.Count];
    // RESERVATION overflow: a channel exceeded the hard maximum OverlayChannelLeases declares for it — content it
    // legally tried to offer the builder was clipped at the channel's OWN boundary. See m_refused for the other,
    // unrelated loss cause these must never be conflated with (that conflation was M2: a narration that named the
    // wrong cause).
    private readonly Counters[] m_dropped = new Counters[OverlayChannelLeases.Count];
    // WRITER'S OWN DECLARED CAP: content the writer itself chose never to offer (NoteRefused) or a WriteText
    // maxChars clamp that truncated a run — a deliberate, pinned limit the writer authored, never a reservation
    // failure. A channel can be well within its reservation and still show up here.
    private readonly Counters[] m_refused = new Counters[OverlayChannelLeases.Count];
    private readonly float m_inverseWidth;
    private readonly float m_inverseHeight;
    // The active clip index records are stamped with: 0 = unclipped, 1..MaxClips = a table rect, -1 = the scope's
    // rect could not be recorded — records inside it DROP (never bleed past a seat boundary) and count as overflow.
    private int m_activeClip;
    // Records written since the active scope opened: an EMPTY scope hands its table slot back at EndClip.
    private int m_activeClipRecords;
    // The open channel's index, or -1 when no channel scope is open (any write then throws).
    private int m_channel = -1;
    private int m_clipCount;
    private int m_elementCount;
    private int m_panelCount;
    private int m_textWordCount;

    // One channel's per-frame counts. A mutable struct held in an array and reached by ref — the per-record
    // accounting must not allocate or copy.
    private struct Counters {
        public int Clips;
        public int Elements;
        public int Panels;
        public int TextWords;
    }

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

        for (var index = 0; (index < OverlayChannelLeases.Count); index++) {
            m_reservations[index] = OverlayChannelLeases.ReservationOf(channel: (OverlayChannel)index);
        }

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
    /// <summary>Gets the number of glyph-code words packed this frame — the node's upload bound for the text
    /// region (never the whole <see cref="TextWordCapacity"/>).</summary>
    public int TextWordCount => m_textWordCount;
    /// <summary>Gets the number of clip-table rects packed this frame — the node's upload bound for the clip
    /// region (never the whole <see cref="MaxClips"/>).</summary>
    public int ClipCount => m_clipCount;
    /// <summary>Gets whether any channel lost content this frame — either by exceeding its reservation
    /// (<see cref="Dropped"/>) or by refusing its own excess at a self-declared cap (<see cref="Refused"/>) — the
    /// node's narration gate for both causes.</summary>
    public bool HasOverflow { get; private set; }
    /// <summary>Gets whether this frame packed anything to draw.</summary>
    public bool HasContent => ((m_panelCount > 0) || (m_elementCount > 0));
    /// <summary>Gets the whole scratch buffer (the node's upload view).</summary>
    public ReadOnlySpan<uint> Scratch => m_scratch;

    /// <summary>The records one channel wrote this frame.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The channel's written counts.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not a declared channel.</exception>
    public OverlayChannelUsage Written(OverlayChannel channel) => Usage(counters: in m_written[IndexOf(channel: channel)]);

    /// <summary>The records one channel lost at its own reservation this frame. Non-zero means that channel
    /// exceeded the hard maximum it declares — no other channel's content was touched. Distinct from
    /// <see cref="Refused"/>: this is a capacity failure, never a deliberate authored limit.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The channel's dropped counts.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not a declared channel.</exception>
    public OverlayChannelUsage Dropped(OverlayChannel channel) => Usage(counters: in m_dropped[IndexOf(channel: channel)]);

    /// <summary>The records one channel refused at its own declared cap this frame — content it chose never to
    /// offer the builder (<see cref="NoteRefused"/>) or a <see cref="WriteText"/> run truncated by its caller's own
    /// <c>maxChars</c>. Non-zero here does not mean the channel is anywhere near its reservation; it means the
    /// writer authored a smaller, deliberate, pinned limit of its own. Distinct from <see cref="Dropped"/>.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The channel's refused counts.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not a declared channel.</exception>
    public OverlayChannelUsage Refused(OverlayChannel channel) => Usage(counters: in m_refused[IndexOf(channel: channel)]);

    /// <summary>The reservation one channel writes against.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The channel's hard reservation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not a declared channel.</exception>
    public OverlayChannelReservation ReservationOf(OverlayChannel channel) => m_reservations[IndexOf(channel: channel)];

    /// <summary>Resets the per-frame region (records + glyph codes + clip table), the clip and channel scopes, and
    /// every channel's written/dropped/refused counts. The static token/glyph prefix is untouched.</summary>
    /// <remarks>The clear is bounded to the previous frame's own high-water mark in each region (its
    /// <see cref="PanelCount"/>/<see cref="ElementCount"/>/<see cref="TextWordCount"/>/<see cref="ClipCount"/> —
    /// read here before this call resets them), never the capacity-sized region behind it: only a word a prior
    /// frame actually wrote can hold stale record data, since everything past that mark was already zeroed the same
    /// way when IT was the smaller frame (the invariant holds by induction from the all-zero freshly-allocated
    /// buffer). A capacity-sized clear here was a live, unconditional-every-frame cost unrelated to what the frame
    /// actually draws — see <see cref="OverlayChannelLeases"/>' reservation totals for what a realistic frame
    /// spends against <see cref="MaxElements"/>/<see cref="TextWordCapacity"/>/<see cref="MaxPanels"/>/
    /// <see cref="MaxClips"/>.</remarks>
    public void BeginFrame() {
        Array.Clear(array: m_scratch, index: PanelBaseWords, length: (m_panelCount * PanelWords));
        Array.Clear(array: m_scratch, index: ElementBaseWords, length: (m_elementCount * ElementWords));
        Array.Clear(array: m_scratch, index: TextBaseWords, length: m_textWordCount);
        Array.Clear(array: m_scratch, index: ClipBaseWords, length: (m_clipCount * ClipWords));

        m_panelCount = 0;
        m_elementCount = 0;
        m_textWordCount = 0;
        m_activeClip = 0;
        m_activeClipRecords = 0;
        m_channel = -1;
        m_clipCount = 0;
        HasOverflow = false;
        Array.Clear(array: m_written);
        Array.Clear(array: m_dropped);
        Array.Clear(array: m_refused);
    }

    /// <summary>Opens a channel scope: every record written until <see cref="EndChannel"/> is charged to
    /// <paramref name="channel"/>'s reservation and, if it exceeds it, dropped and attributed to it.</summary>
    /// <param name="channel">The writing channel.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not a declared channel.</exception>
    /// <exception cref="InvalidOperationException">A channel scope is already open.</exception>
    public void BeginChannel(OverlayChannel channel) {
        var index = IndexOf(channel: channel);

        if (m_channel >= 0) {
            throw new InvalidOperationException(message: $"The overlay channel \"{OverlayChannelLeases.NameOf(channel: (OverlayChannel)m_channel)}\" is still open.");
        }

        m_channel = index;
    }

    /// <summary>Closes the channel scope. A clip scope the channel left open is unwound here, so one writer's
    /// bookkeeping mistake — or an overflowed clip scope — can never reach the channel that draws next.</summary>
    /// <exception cref="InvalidOperationException">No channel scope is open.</exception>
    public void EndChannel() {
        if (m_channel < 0) {
            throw new InvalidOperationException(message: "No overlay channel scope is open.");
        }

        if (m_activeClip != 0) {
            EndClip();
        }

        m_channel = -1;
    }

    /// <summary>Records content the channel itself refused at its own declared cap, before it was ever offered to
    /// the builder — a writer that clamps a repeating row to a pinned maximum reports the remainder here so the
    /// truncation lands on an attributed, narrated path instead of vanishing. Reported as <see cref="Refused"/>,
    /// never as <see cref="Dropped"/>: this is a deliberate, pinned limit the writer authored, not a reservation
    /// failure, and the two must narrate as the distinct causes they are.</summary>
    /// <param name="elements">The element records refused.</param>
    /// <param name="textWords">The glyph-code words refused.</param>
    /// <exception cref="InvalidOperationException">No channel scope is open.</exception>
    public void NoteRefused(int elements, int textWords) => RefuseOwnCap(index: ActiveChannel(), elements: elements, textWords: textWords);

    /// <summary>Opens a clip scope: records written before <see cref="EndClip"/> are discarded by the shader outside
    /// this rect — the per-seat viewport invariant every split-screen writer rides. Scopes do not nest: a
    /// <see cref="BeginClip"/> called while one is already open throws rather than silently clobbering the outer
    /// scope's bookkeeping (an outer scope that opened before an inner one could never hand its own slot back on an
    /// empty scope — a real, if latent, clip-table leak with only one active-scope slot tracked). A scope the
    /// channel has no clip reservation left for still scopes: its records drop (counted) rather than bleed across a
    /// seat, and <see cref="EndClip"/> unwinds that state.</summary>
    /// <param name="x">Left, px.</param>
    /// <param name="y">Top, px.</param>
    /// <param name="w">Width, px.</param>
    /// <param name="h">Height, px.</param>
    /// <exception cref="InvalidOperationException">No channel scope is open, or a clip scope is already open.</exception>
    public void BeginClip(float x, float y, float w, float h) {
        var index = ActiveChannel();

        if (m_activeClip != 0) {
            throw new InvalidOperationException(message: "A clip scope is already open; BeginClip does not nest — call EndClip before opening another.");
        }

        m_activeClipRecords = 0;

        ref var written = ref m_written[index];

        if ((written.Clips >= m_reservations[index].Clips) || (m_clipCount >= MaxClips)) {
            m_dropped[index].Clips++;
            m_activeClip = -1;
            HasOverflow = true;

            return;
        }

        var offset = (ClipBaseWords + (m_clipCount * ClipWords));

        m_scratch[offset] = Pack(value: (x * m_inverseWidth));
        m_scratch[(offset + 1)] = Pack(value: (y * m_inverseHeight));
        m_scratch[(offset + 2)] = Pack(value: (w * m_inverseWidth));
        m_scratch[(offset + 3)] = Pack(value: (h * m_inverseHeight));
        written.Clips++;
        m_clipCount++;
        m_activeClip = m_clipCount;
    }

    /// <summary>Closes the clip scope (records return to unclipped). A scope that wrote nothing hands its table slot
    /// back — the count decrements — so a writer with an empty seat never burns its clip reservation, and an
    /// overflowed scope's drop-everything state ends here with the scope that caused it.</summary>
    /// <exception cref="InvalidOperationException">No channel scope is open.</exception>
    public void EndClip() {
        var index = ActiveChannel();

        if ((m_activeClipRecords == 0) && (m_activeClip == m_clipCount) && (m_activeClip > 0)) {
            m_written[index].Clips--;
            m_clipCount--;
        }

        m_activeClip = 0;
        m_activeClipRecords = 0;
    }

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
    /// <exception cref="InvalidOperationException">No channel scope is open.</exception>
    public void WritePanel(float x, float y, float w, float h, bool titleBand, float bandHeight, OverlayPanelStyle style, OverlayColorRole? ringRole, float alpha) {
        if (!TryTakePanel()) {
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
    /// <exception cref="InvalidOperationException">No channel scope is open.</exception>
    public void WriteRect(float x, float y, float w, float h, OverlayColorRole role, float radius, float alpha) {
        if (!TryTakeElement()) {
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

    /// <summary>Packs one stroked hairline ring (the gizmo radius indicator). Word layout (12): 0..1 center
    /// (normalized) · 2 radius (px float) · 4 = 3 | (role &lt;&lt; 4) · 7 alpha · 9 clip index.</summary>
    /// <param name="centerX">The ring center x, px.</param>
    /// <param name="centerY">The ring center y, px.</param>
    /// <param name="radius">The ring radius, px.</param>
    /// <param name="role">The stroke's color role.</param>
    /// <param name="alpha">The element opacity (composes with the role's own alpha).</param>
    /// <exception cref="InvalidOperationException">No channel scope is open.</exception>
    public void WriteRing(float centerX, float centerY, float radius, OverlayColorRole role, float alpha) {
        if (!TryTakeElement()) {
            return;
        }

        var offset = (ElementBaseWords + (m_elementCount * ElementWords));

        m_scratch[offset] = Pack(value: (centerX * m_inverseWidth));
        m_scratch[(offset + 1)] = Pack(value: (centerY * m_inverseHeight));
        m_scratch[(offset + 2)] = Pack(value: radius);
        m_scratch[(offset + 4)] = (3u | ((uint)role << 4));
        m_scratch[(offset + 7)] = Pack(value: alpha);
        m_scratch[(offset + 9)] = (uint)m_activeClip;
        m_elementCount++;
    }

    /// <summary>Packs one fixed-cell text run (codes stored pre-resolved as atlas glyph indices; anything outside
    /// printable ASCII renders as the blank space cell). Word layout (12): 0..1 origin (normalized) · 2..3 one glyph
    /// cell's on-screen w/h (normalized) · 4 = 0 | (role &lt;&lt; 4) · 5 glyph start (word offset into the text
    /// region) · 6 glyph count · 7 alpha · 9 clip index.</summary>
    /// <param name="x">The run origin's left, px.</param>
    /// <param name="y">The run origin's top, px.</param>
    /// <param name="text">The characters to pack.</param>
    /// <param name="cellHeight">The on-screen glyph cell height, px (see <see cref="CellHeight"/>).</param>
    /// <param name="role">The text color role.</param>
    /// <param name="alpha">The run opacity.</param>
    /// <param name="maxChars">Clips the run without allocating; characters beyond this are refused (reported via
    /// <see cref="Refused"/>, never as a reservation <see cref="Dropped"/>).</param>
    /// <exception cref="InvalidOperationException">No channel scope is open.</exception>
    public void WriteText(float x, float y, ReadOnlySpan<char> text, int cellHeight, OverlayColorRole role, float alpha, int maxChars = int.MaxValue) {
        var channelIndex = ActiveChannel();
        var count = Math.Clamp(value: maxChars, min: 0, max: text.Length);

        // The CALLER'S OWN maxChars clamp, not a reservation limit — report the truncated tail on the same
        // writer's-own-cap path as NoteRefused so a silent per-character drop cannot hide behind a record-level
        // narration. Covers the whole-run-refused edge case too (maxChars <= 0) that used to vanish silently.
        if (count < text.Length) {
            RefuseOwnCap(index: channelIndex, elements: 0, textWords: (text.Length - count));
        }

        if (count <= 0) {
            return;
        }

        if (!CanTakeElement(index: channelIndex)) {
            m_dropped[channelIndex].Elements++;
            HasOverflow = true;

            return;
        }

        // The run's glyph words and its element record are ONE indivisible take: a run whose words do not fit is
        // dropped whole (both resources attributed) rather than rendered truncated behind the writer's back.
        if (((m_written[channelIndex].TextWords + count) > m_reservations[channelIndex].TextWords) || ((m_textWordCount + count) > TextWordCapacity)) {
            m_dropped[channelIndex].Elements++;
            m_dropped[channelIndex].TextWords += count;
            HasOverflow = true;

            return;
        }

        TakeElement(index: channelIndex);
        m_written[channelIndex].TextWords += count;

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
    /// <param name="pressed">The held tier-1 state.</param>
    /// <param name="accent">The accent tier-1 state (the context-primary action).</param>
    /// <param name="bound">Whether an action is bound (<see langword="false"/> = the disabled tier-0 look).</param>
    /// <exception cref="InvalidOperationException">No channel scope is open.</exception>
    public void WriteIcon(float centerX, float centerY, float plateHalf, float glyphHalf, float glyphOffsetX, float glyphOffsetY, OverlayGlyphId glyph, OverlayIconId icon, float alpha, bool pressed, bool accent, bool bound) {
        if (!TryTakeElement()) {
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

    /// <summary>The on-screen glyph cell height for a token type size — the size-to-cell ratio
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

    // The open channel's index. A write with no channel scope open is a programming error, never a data condition:
    // an unattributed record is exactly what the lease table exists to make impossible.
    private int ActiveChannel() {
        if (m_channel < 0) {
            throw new InvalidOperationException(message: "No overlay channel scope is open; open one with BeginChannel before writing records.");
        }

        return m_channel;
    }
    private static int IndexOf(OverlayChannel channel) {
        if (((uint)channel) >= OverlayChannelLeases.Count) {
            throw new ArgumentOutOfRangeException(paramName: nameof(channel), actualValue: channel, message: "Not a declared overlay channel.");
        }

        return (int)channel;
    }
    private static OverlayChannelUsage Usage(in Counters counters) =>
        new(Clips: counters.Clips, Elements: counters.Elements, Panels: counters.Panels, TextWords: counters.TextWords);
    // The one place both NoteRefused and WriteText's own maxChars truncation land: content the WRITER declared it
    // would never offer, kept in its own bucket (m_refused) so it can never be reported as a reservation overflow.
    private void RefuseOwnCap(int index, int elements, int textWords) {
        if ((elements <= 0) && (textWords <= 0)) {
            return;
        }

        m_refused[index].Elements += Math.Max(val1: 0, val2: elements);
        m_refused[index].TextWords += Math.Max(val1: 0, val2: textWords);
        HasOverflow = true;
    }
    // Whether one more element record fits: the channel's own reservation and the ceiling above it, plus the
    // drop-everything state a scope whose clip rect could not be recorded leaves behind.
    private bool CanTakeElement(int index) =>
        ((m_activeClip >= 0) && ((m_written[index].Elements < m_reservations[index].Elements) && (m_elementCount < MaxElements)));
    private void TakeElement(int index) {
        m_written[index].Elements++;
        m_activeClipRecords++;
    }
    private bool TryTakeElement() {
        var index = ActiveChannel();

        if (!CanTakeElement(index: index)) {
            m_dropped[index].Elements++;
            HasOverflow = true;

            return false;
        }

        TakeElement(index: index);

        return true;
    }
    private bool TryTakePanel() {
        var index = ActiveChannel();

        if ((m_activeClip < 0) || ((m_written[index].Panels >= m_reservations[index].Panels) || (m_panelCount >= MaxPanels))) {
            m_dropped[index].Panels++;
            HasOverflow = true;

            return false;
        }

        m_written[index].Panels++;
        m_activeClipRecords++;

        return true;
    }

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
