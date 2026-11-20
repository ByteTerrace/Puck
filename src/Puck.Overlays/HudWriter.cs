using Puck.Abstractions.Presentation;
using Puck.Text;

namespace Puck.Overlays;

/// <summary>
/// The authored-HUD writer: renders <see cref="HudStore"/>'s structural snapshot in four separate calls —
/// <see cref="EmitUnder"/>, <see cref="EmitReplace"/>, <see cref="EmitOver"/> (the world-scope bands, one per band,
/// so <see cref="OverlayFrameComposer"/>'s banded pipeline can sequence them around the four first-party writers' base slot)
/// and <see cref="EmitSeatPanels"/> (the player-scope per-seat panels, unbanded — see its own remarks). Each call
/// resolves every bound element's live value through <see cref="IHudBindingResolver"/> at emission time
/// (presentation float; resolved fresh every produced frame, never cached across frames) and draws rect/text/gauge
/// elements confined to their owning panel's rect via <see cref="OverlayFrameBuilder.BeginClip"/>, the same
/// clip-scope contract every per-seat writer uses.
/// </summary>
public sealed class HudWriter : IOverlaySeatEmitter<OverlayHudSeatPanel> {
    /// <summary>The render records each plot sample expands into. Track, reference and status use the existing
    /// per-element reservation; bars reserve this cost separately per panel.</summary>
    public const int PlotSampleElementCost = 1;
    private PresentationReading[] m_plotSamples = [];
    private int m_plotRemaining;
    // The engine ticks a presented second holds (Puck.Hosting.FixedTickConversion.TicksPerSecond), which the content
    // clock of a marked-up text counts in.
    private const float RenderTicksPerSecond = 50400f;
    // The marked-up texts a panel may hold parsed at once; past it the cache starts again.
    private const int StyledTextCacheSize = 64;
    private readonly Dictionary<string, StyledGlyph[]> m_styledTexts = new(comparer: StringComparer.Ordinal);
    private readonly char[] m_styledRun = new char[TextRunChars];
    private int m_styledRemaining;
    private ulong m_renderTicks;
    // The panel being emitted's presence — multiplied into its chrome and every element's alpha.
    private float m_panelAlpha = 1f;
    // Reused across every ComposeTemplate call (single-threaded on the window-pump thread, like every writer here) so
    // only genuine capacity growth allocates — its high-water mark stabilizes after the first few frames' widest
    // template, rather than a fresh empty builder paying the grow-copy sequence back up on every call.
    private readonly System.Text.StringBuilder m_templateBuilder = new();
    private char[] m_templateText = new char[TextRunChars];

    // A gauge's label run is clipped to this many characters; TextRunChars is the wider bound the reservation takes.
    private const int GaugeLabelChars = 16;

    /// <summary>The render elements one authored gauge expands into — a track rect, a fill rect, and one label run —
    /// the per-element cost <see cref="OverlayChannelLeases"/> multiplies into the Hud element reservation (the
    /// authored kind that expands widest; a rect or text element is one record).</summary>
    public const int GaugeElementCost = 3;
    /// <summary>The glyph-word ceiling one text element's run is clipped to — the per-element term
    /// <see cref="OverlayChannelLeases"/> multiplies into the Hud text-word reservation. Enforced at
    /// <see cref="OverlayFrameBuilder.WriteText"/>'s own <c>maxChars</c> clamp, so the reservation arithmetic
    /// describes what the writer can actually emit: a template resolving many long host-supplied text cells clips as
    /// this writer's own attributed refusal instead of eating the shared channel budget and dropping other elements'
    /// records.</summary>
    public const int TextRunChars = 64;

    private readonly IHudBindingResolver m_bindings;
    private readonly OverlayFrameSlots m_frameSlots;
    private readonly IOverlaySource<OverlayHudFrame> m_source;
    private readonly OverlayThemeStore m_theme;

    private OverlayHudFrame m_frame;
    private bool m_hasFrame;

    // The seat whose player-scope panel is being emitted, which its bindings resolve for; -1 while a world-scope panel
    // is.
    private int m_seat = -1;

    /// <summary>Initializes a new instance of the <see cref="HudWriter"/> class.</summary>
    /// <param name="source">The HUD structure source.</param>
    /// <param name="bindings">The live binding resolver.</param>
    /// <param name="theme">The live resolved theme.</param>
    /// <param name="frameSlots">The node-owned frame-slot table a <see cref="OverlayHudElementKind.Frame"/> element
    /// binds against.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public HudWriter(IOverlaySource<OverlayHudFrame> source, IHudBindingResolver bindings, OverlayThemeStore theme, OverlayFrameSlots frameSlots) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: theme);
        ArgumentNullException.ThrowIfNull(argument: frameSlots);

        m_source = source;
        m_bindings = bindings;
        m_theme = theme;
        m_frameSlots = frameSlots;
    }

    void IOverlaySeatEmitter<OverlayHudSeatPanel>.EmitSeat(OverlayFrameBuilder builder, in OverlayHudSeatPanel seat) => EmitSeatPanel(
        builder: builder,
        seat: in seat
    );

    // Substitution only — the brace/escape grammar is parsed once by the host's document layer and arrives here as
    // runs, so this project restates none of it. A placeholder that fails to resolve appends nothing: the host's
    // validator already refused an unknown one before it could reach a live document, so an empty substitution keeps
    // the frame drawing rather than standing in for a refusal that belongs upstream.
    private ReadOnlySpan<char> ComposeTemplate(ReadOnlySpan<OverlayHudTemplateSegment> segments) {
        var builder = m_templateBuilder.Clear();

        for (var index = 0; (index < segments.Length); index++) {
            var segment = segments[index];

            if (!segment.IsPlaceholder) {
                builder.Append(value: segment.Text);

                continue;
            }

            if (m_bindings.TryResolve(
                binding: segment.Text,
                seat: m_seat,
                fraction: out _,
                text: out var resolved
            )) {
                builder.Append(value: resolved);
            }
        }

        if (m_templateText.Length < builder.Length) {
            m_templateText = new char[builder.Capacity];
        }
        builder.CopyTo(sourceIndex: 0, destination: m_templateText, destinationIndex: 0, count: builder.Length);
        return m_templateText.AsSpan(start: 0, length: builder.Length);
    }
    private void EmitBand(OverlayFrameBuilder builder, OverlayHudBand band) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (!m_hasFrame) {
            return;
        }

        var panels = m_frame.Panels.Span;

        for (var index = 0; (index < panels.Length); index++) {
            if (panels[index].Band == band) {
                EmitPanel(
                    builder: builder,
                    panel: in panels[index]
                );
            }
        }
    }
    private void EmitElement(OverlayFrameBuilder builder, in OverlayHudElement element, float panelX, float panelY, float panelW, float panelH) {
        var rect = element.Rect;

        if (
            (rect.Width <= 0f) ||
            (rect.Height <= 0f)
        ) {
            return;
        }

        var x = (panelX + (rect.X * panelW));
        var y = (panelY + (rect.Y * panelH));
        var w = (rect.Width * panelW);
        var h = (rect.Height * panelH);

        switch (element.Kind) {
            case OverlayHudElementKind.Plot:
                EmitPlot(builder, in element, x, y, w, h);
                break;
            case OverlayHudElementKind.Rect:
                builder.WriteRect(
                    x: x,
                    y: y,
                    w: w,
                    h: h,
                    role: element.Role,
                    radius: 0f,
                    alpha: m_panelAlpha
                );

                break;
            case OverlayHudElementKind.Text:
                EmitText(
                    builder: builder,
                    element: in element,
                    h: h,
                    x: x,
                    y: y
                );

                break;
            case OverlayHudElementKind.Gauge:
                EmitGauge(
                    builder: builder,
                    element: in element,
                    h: h,
                    w: w,
                    x: x,
                    y: y
                );

                break;
            case OverlayHudElementKind.Frame:
                EmitFrame(
                    builder: builder,
                    element: in element,
                    h: h,
                    w: w,
                    x: x,
                    y: y
                );

                break;
        }
    }
    // A slot with no live lease this frame (an unassigned source, an unopened camera, every slot already taken)
    // draws nothing — never a placeholder — so a face cam with no camera attached is simply an empty rect, not a
    // stand-in graphic the writer would need to author. The outgoing side of a cross-fade degrades the same way:
    // when its bind fails the winner draws alone at full mix, since the fade exists to soften a switch that has
    // already happened, never to hold the picture hostage to a source that cannot show.
    private void EmitFrame(OverlayFrameBuilder builder, in OverlayHudElement element, float x, float y, float w, float h) {
        if (element.FrameSource < 0) {
            return;
        }

        var slot = m_frameSlots.Bind(key: element.FrameSource);

        if (slot < 0) {
            return;
        }

        var slotB = -1;
        var mix = 1f;

        if (element.FrameSourceB >= 0) {
            slotB = m_frameSlots.Bind(key: element.FrameSourceB);

            if (slotB >= 0) {
                mix = element.FrameMix;
            }
        }

        builder.WriteFrame(
            alpha: (element.Opacity * m_panelAlpha),
            fit: element.Fit,
            h: h,
            mirror: element.Mirror,
            mix: mix,
            radius: element.Radius,
            slot: slot,
            slotB: slotB,
            w: w,
            x: x,
            y: y
        );
    }
    private void EmitGauge(OverlayFrameBuilder builder, in OverlayHudElement element, float x, float y, float w, float h) {
        var fraction = 0f;
        ReadOnlySpan<char> label = [];

        if (
            (element.Binding is { Length: > 0 } binding) &&
            m_bindings.TryResolve(
            binding: binding,
            fraction: out var resolved,
            seat: m_seat,
            text: out var text
        )
        ) {
            fraction = Math.Clamp(
                max: 1f,
                min: 0f,
                value: resolved
            );
            label = text;
        }

        // Track (always the full extent) + fill (scaled by the resolved fraction) + a short value label — the
        // GaugeElementCost records the reservation counts per gauge.
        builder.WriteRect(
            alpha: (m_theme.Current.Styles.Hud.TrackAlpha * m_panelAlpha),
            h: h,
            radius: 0f,
            role: OverlayColorRole.SurfaceInset,
            w: w,
            x: x,
            y: y
        );
        builder.WriteRect(
            x: x,
            y: y,
            w: (w * fraction),
            h: h,
            role: element.Role,
            radius: 0f,
            alpha: m_panelAlpha
        );

        if (label.Length > 0) {
            var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: h);

            builder.WriteText(
                alpha: m_panelAlpha,
                cellHeight: cellHeight,
                maxChars: GaugeLabelChars,
                role: OverlayColorRole.TextPrimary,
                text: label,
                x: x,
                y: y
            );
        }
    }
    private void EmitPlot(OverlayFrameBuilder builder, in OverlayHudElement element, float x, float y, float w, float h) {
        var requested = Math.Clamp(element.PlotSamples, 0, m_plotRemaining);
        m_plotRemaining -= requested;
        if (requested == 0 || element.Binding is not { } binding || element.PlotMax <= element.PlotMin) { return; }
        if (m_plotSamples.Length < requested) { m_plotSamples = new PresentationReading[requested]; }
        var count = Math.Clamp(m_bindings.CopyRecent(binding, m_seat, m_plotSamples.AsSpan(0, requested)), 0, requested);
        var current = m_bindings.ReadReading(binding, m_seat);

        builder.WriteRect(x, y, w, h, OverlayColorRole.SurfaceInset, radius: 0, alpha: m_theme.Current.Styles.Hud.TrackAlpha * m_panelAlpha);
        var step = w / requested;
        for (var index = 0; index < count; index++) {
            var sample = m_plotSamples[index];
            if (sample.State != PresentationReadingState.Present || !double.IsFinite(sample.Value)) { continue; }
            var height = h * (float)Math.Clamp((sample.Value - element.PlotMin) / ((double)element.PlotMax - element.PlotMin), 0, 1);
            builder.WriteRect(x + (requested - count + index) * step, y + h - height, step, height, element.Role, radius: 0, alpha: m_panelAlpha);
        }

        double? reference = element.PlotReference;
        if (element.PlotReferenceBinding is { } token) {
            var reading = m_bindings.ReadReading(token, m_seat);
            reference = reading.State == PresentationReadingState.Present ? reading.Value : null;
        }
        if (reference is { } line && double.IsFinite(line) && line >= element.PlotMin && line <= element.PlotMax) {
            var offset = h * (float)((line - element.PlotMin) / ((double)element.PlotMax - element.PlotMin));
            builder.WriteRect(x, y + h - offset, w, 1, OverlayColorRole.TextPrimary, radius: 0, alpha: m_panelAlpha);
        }
        if (current.State != PresentationReadingState.Present || count == 0) {
            builder.WriteText(x, y, current.State == PresentationReadingState.Absent ? "absent" : "unmeasured", OverlayFrameBuilder.CellHeight(sizePx: Math.Min(h, 16)), OverlayColorRole.TextPrimary, maxChars: TextRunChars, alpha: m_panelAlpha);
        }
    }
    private void EmitPanel(OverlayFrameBuilder builder, in OverlayHudPanel panel) {
        var rect = panel.Rect;

        if (
            (rect.Width <= 0f) ||
            (rect.Height <= 0f)
        ) {
            return;
        }

        var x = (rect.X * builder.Width);
        var y = (rect.Y * builder.Height);
        var w = (rect.Width * builder.Width);
        var h = (rect.Height * builder.Height);

        builder.BeginClip(
            h: h,
            w: w,
            x: x,
            y: y
        );

        EmitPanelInto(
            builder: builder,
            originX: 0f,
            originY: 0f,
            panel: in panel,
            spanH: builder.Height,
            spanW: builder.Width
        );

        builder.EndClip();
    }
    // The shared panel body: chrome + elements, placed by panel.Rect's fractions within the (originX, originY,
    // spanW, spanH) rect — the whole frame for a world-scope panel, the seat viewport for a seat-scope one. The
    // caller owns its own clip scope (a world-scope panel clips to ITSELF; a seat-scope one clips to the whole
    // viewport, wider than its panel, so future seat-scope content can draw past the panel's own rect).
    private void EmitPanelInto(OverlayFrameBuilder builder, in OverlayHudPanel panel, float originX, float originY, float spanW, float spanH) {
        var rect = panel.Rect;

        if (
            (rect.Width <= 0f) ||
            (rect.Height <= 0f)
        ) {
            return;
        }

        m_panelAlpha = panel.Alpha;
        m_plotRemaining = builder.Leases.Capacity.HudPlotSamplesPerPanel;
        m_styledRemaining = builder.Leases.Capacity.HudStyledRunsPerPanel;

        var x = (originX + (rect.X * spanW));
        var y = (originY + (rect.Y * spanH));
        var w = (rect.Width * spanW);
        var h = (rect.Height * spanH);

        builder.WritePanel(
            x: x,
            y: y,
            w: w,
            h: h,
            titleBand: false,
            bandHeight: 0f,
            style: panel.Style,
            ringRole: null,
            alpha: panel.Alpha
        );

        var elements = panel.Elements.Span;

        for (var index = 0; (index < elements.Length); index++) {
            EmitElement(
                builder: builder,
                element: in elements[index],
                panelX: x,
                panelY: y,
                panelW: w,
                panelH: h
            );
        }
    }
    private void EmitSeatPanel(OverlayFrameBuilder builder, in OverlayHudSeatPanel seat) {
        var viewport = seat.Viewport;

        if (
            (viewport.Width <= 0f) ||
            (viewport.Height <= 0f)
        ) {
            return;
        }

        var vx = (viewport.X * builder.Width);
        var vy = (viewport.Y * builder.Height);
        var vw = (viewport.Width * builder.Width);
        var vh = (viewport.Height * builder.Height);

        builder.BeginClip(
            h: vh,
            w: vw,
            x: vx,
            y: vy
        );

        var panel = seat.Panel;

        m_seat = seat.Seat;
        EmitPanelInto(
            builder: builder,
            originX: vx,
            originY: vy,
            panel: in panel,
            spanH: vh,
            spanW: vw
        );
        m_seat = -1;

        builder.EndClip();
    }
    private void EmitText(OverlayFrameBuilder builder, in OverlayHudElement element, float x, float y, float h) {
        ReadOnlySpan<char> text = element.Text;

        if (!element.Template.IsEmpty) {
            text = ComposeTemplate(segments: element.Template.Span);
        } else if (
            (element.Binding is { Length: > 0 } binding) &&
            m_bindings.TryResolve(
            binding: binding,
            fraction: out _,
            seat: m_seat,
            text: out var resolved
        )
        ) {
            text = resolved;
        }

        if (text.Length == 0) {
            return;
        }

        var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: h);

        if (element.Markup) {
            EmitStyledText(builder: builder, cellHeight: cellHeight, role: element.Role, text: text, x: x, y: y);

            return;
        }

        builder.WriteText(
            x: x,
            y: y,
            text: text,
            cellHeight: cellHeight,
            role: element.Role,
            alpha: 1f,
            maxChars: TextRunChars
        );
    }
    // A marked-up text draws as one run for each stretch of glyphs that share a channel this frame: a glyph's offset
    // moves its run's pen, its scale sizes the run's cell about the glyph's centre, and its coverage is the run's alpha.
    // The fixed-cell tier has no per-glyph tint or weight, so those two fields are not drawn. The panel's styled-run
    // budget bounds the split: once it is spent, the rest of the text draws as one unstyled run, so nothing is dropped.
    private void EmitStyledText(OverlayFrameBuilder builder, ReadOnlySpan<char> text, float x, float y, int cellHeight, OverlayColorRole role) {
        var glyphs = StyledGlyphs(text: text);
        var count = Math.Min(val1: glyphs.Length, val2: TextRunChars);
        var advance = builder.GlyphAdvance(cellHeight: cellHeight);
        var start = 0;

        while (start < count) {
            var channel = ChannelOf(glyph: in glyphs[start], index: start, advance: advance);
            var end = (start + 1);

            if (m_styledRemaining <= 0) {
                channel = TextGlyphChannel.Identity;
                end = count;
            } else {
                while ((end < count) && (ChannelOf(glyph: in glyphs[end], index: end, advance: advance) == channel)) {
                    end++;
                }
                if (start > 0) {
                    m_styledRemaining--;
                }
            }

            for (var index = start; (index < end); index++) {
                m_styledRun[(index - start)] = glyphs[index].Character;
            }

            var runCell = Math.Max(val1: 1, val2: ((int)MathF.Round(x: (cellHeight * channel.Scale))));

            builder.WriteText(
                x: ((x + (start * advance)) + channel.Offset.X) - ((builder.GlyphAdvance(cellHeight: runCell) - advance) * 0.5f),
                y: (y - channel.Offset.Y) - ((runCell - cellHeight) * 0.5f),
                text: m_styledRun.AsSpan(start: 0, length: (end - start)),
                cellHeight: runCell,
                role: role,
                alpha: Math.Clamp(max: 1f, min: 0f, value: channel.Coverage),
                maxChars: TextRunChars
            );
            start = end;
        }
    }
    private TextGlyphChannel ChannelOf(in StyledGlyph glyph, int index, float advance) => TextGlyphChannel.Resolve(
        contentTick: ((long)m_renderTicks),
        effect: glyph.Effect,
        glyphIndex: glyph.SpanIndex,
        glyphPhase: (index * advance),
        ticksPerSecond: RenderTicksPerSecond
    );
    // The markup is compiled when a text first resolves to it, so a frame that only advances the content clock parses
    // nothing; a text that changes every frame costs one compile a frame and is bounded by the cache's size.
    private StyledGlyph[] StyledGlyphs(ReadOnlySpan<char> text) {
        var lookup = m_styledTexts.GetAlternateLookup<ReadOnlySpan<char>>();

        if (lookup.TryGetValue(key: text, value: out var cached)) {
            return cached;
        }
        if (m_styledTexts.Count >= StyledTextCacheSize) {
            m_styledTexts.Clear();
        }

        var markup = text.ToString();
        var glyphs = new List<StyledGlyph>(capacity: markup.Length);
        var span = 0;
        var previous = TextEffect.None;

        foreach (var rune in BbCodeTextMarkup.EnrichRunes(markup: markup)) {
            span = ((rune.Effect == previous) ? (span + 1) : 0);
            previous = rune.Effect;
            glyphs.Add(item: new StyledGlyph(
                Character: (rune.Rune.IsBmp ? ((char)rune.Rune.Value) : '?'),
                Effect: rune.Effect,
                SpanIndex: span
            ));
        }

        cached = [.. glyphs];
        m_styledTexts[markup] = cached;

        return cached;
    }

    private readonly record struct StyledGlyph(char Character, TextEffect Effect, int SpanIndex);

    /// <summary>Emits every over-band panel, in document order.</summary>
    /// <param name="builder">The frame builder.</param>
    public void EmitOver(OverlayFrameBuilder builder) => EmitBand(
        band: OverlayHudBand.Over,
        builder: builder
    );
    /// <summary>Emits every replace-band panel, in document order — the base slot when <see cref="HasReplace"/>.</summary>
    /// <param name="builder">The frame builder.</param>
    public void EmitReplace(OverlayFrameBuilder builder) => EmitBand(
        band: OverlayHudBand.Replace,
        builder: builder
    );
    /// <summary>Emits every player-scope (per-seat) panel: each panel is confined
    /// to its owning seat's viewport via one <see cref="OverlayFrameBuilder.BeginClip"/> scope (clip scopes do not
    /// nest, so this does not also open the world-scope panel's own per-panel clip), with the panel positioned
    /// local to that viewport rather than the whole screen. Bands are not meaningful for a seat panel (it has no
    /// base slot to take over), so this runs once, outside the under/base/over sequence.</summary>
    /// <param name="builder">The frame builder.</param>
    /// <exception cref="InvalidOperationException">The published frame carries more seat panels than
    /// <see cref="OverlayChannelLeases.MaxSeats"/> provisions for.</exception>
    public void EmitSeatPanels(OverlayFrameBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (!m_hasFrame) {
            return;
        }

        OverlaySeatLoop.Emit(
            builder: builder,
            seats: m_frame.SeatPanels.Span,
            writer: this,
            writerName: nameof(HudWriter)
        );
    }
    /// <summary>Emits every under-band panel, in document order.</summary>
    /// <param name="builder">The frame builder.</param>
    public void EmitUnder(OverlayFrameBuilder builder) => EmitBand(
        band: OverlayHudBand.Under,
        builder: builder
    );
    /// <summary>Refreshes the cached structural snapshot for the frame about to produce. Call exactly once per
    /// produced frame, before any <c>Emit*</c> call.</summary>
    /// <param name="renderTicks">The frame's presented time in engine ticks, the content clock a marked-up text's moving
    /// effects read.</param>
    public void RefreshFrame(ulong renderTicks = 0UL) {
        m_renderTicks = renderTicks;
        m_hasFrame = m_source.TrySnapshot(frame: out m_frame);
    }

    /// <summary>Whether at least one live panel declares <see cref="OverlayHudBand.Replace"/> this frame — the
    /// banded pipeline's base-slot decision (the caller checks this before running the five first-party writers).</summary>
    public bool HasReplace {
        get {
            if (!m_hasFrame) {
                return false;
            }

            var panels = m_frame.Panels.Span;

            for (var index = 0; (index < panels.Length); index++) {
                if (panels[index].Band == OverlayHudBand.Replace) {
                    return true;
                }
            }

            return false;
        }
    }
}
