namespace Puck.Overlays;

/// <summary>
/// The authored-HUD writer: renders <see cref="HudStore"/>'s structural snapshot in four separate calls —
/// <see cref="EmitUnder"/>, <see cref="EmitReplace"/>, <see cref="EmitOver"/> (the world-scope bands, one per band,
/// so <c>UnifiedOverlayNode</c>'s banded pipeline can sequence them around the four first-party writers' base slot)
/// and <see cref="EmitSeatPanels"/> (the player-scope per-seat panels, unbanded — see its own remarks). Each call
/// resolves every bound element's live value through <see cref="IHudBindingResolver"/> at emission time
/// (presentation float; resolved fresh every produced frame, never cached across frames) and draws rect/text/gauge
/// elements confined to their owning panel's rect via <see cref="OverlayFrameBuilder.BeginClip"/>, the same
/// clip-scope contract every per-seat writer uses.
/// </summary>
public sealed class HudWriter : IOverlaySeatEmitter<OverlayHudSeatPanel> {
    // The panel being emitted's presence — multiplied into its chrome and every element's alpha.
    private float m_panelAlpha = 1f;

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
    private readonly IHudSource m_source;
    private readonly OverlayThemeStore m_theme;

    private OverlayHudFrame m_frame;
    private bool m_hasFrame;

    // Reused across every ComposeTemplate call (single-threaded on the window-pump thread, like every writer here) so
    // only genuine capacity growth allocates — its high-water mark stabilizes after the first few frames' widest
    // template, rather than a fresh empty builder paying the grow-copy sequence back up on every call.
    private readonly System.Text.StringBuilder m_templateBuilder = new();

    /// <summary>Initializes a new instance of the <see cref="HudWriter"/> class.</summary>
    /// <param name="source">The HUD structure source.</param>
    /// <param name="bindings">The live binding resolver.</param>
    /// <param name="theme">The live resolved theme.</param>
    /// <param name="frameSlots">The node-owned frame-slot table a <see cref="OverlayHudElementKind.Frame"/> element
    /// binds against.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public HudWriter(IHudSource source, IHudBindingResolver bindings, OverlayThemeStore theme, OverlayFrameSlots frameSlots) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: theme);
        ArgumentNullException.ThrowIfNull(argument: frameSlots);

        m_source = source;
        m_bindings = bindings;
        m_theme = theme;
        m_frameSlots = frameSlots;
    }

    // Substitution only — the brace/escape grammar is parsed once by the host's document layer and arrives here as
    // runs, so this project restates none of it. A placeholder that fails to resolve appends nothing: the host's
    // validator already refused an unknown one before it could reach a live document, so an empty substitution keeps
    // the frame drawing rather than standing in for a refusal that belongs upstream.
    private string ComposeTemplate(ReadOnlySpan<OverlayHudTemplateSegment> segments) {
        var builder = m_templateBuilder.Clear();

        for (var index = 0; (index < segments.Length); index++) {
            var segment = segments[index];

            if (!segment.IsPlaceholder) {
                builder.Append(value: segment.Text);

                continue;
            }

            if (m_bindings.TryResolve(
                binding: segment.Text,
                fraction: out _,
                text: out var resolved
            )) {
                builder.Append(value: resolved);
            }
        }

        return builder.ToString();
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
        var label = string.Empty;

        if (
            (element.Binding is { Length: > 0 } binding) &&
            m_bindings.TryResolve(
            binding: binding,
            fraction: out var resolved,
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
            alpha: (m_theme.Current.Chrome.DimQuietAlpha * m_panelAlpha),
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

        EmitPanelInto(
            builder: builder,
            originX: vx,
            originY: vy,
            panel: in panel,
            spanH: vh,
            spanW: vw
        );

        builder.EndClip();
    }
    private void EmitText(OverlayFrameBuilder builder, in OverlayHudElement element, float x, float y, float h) {
        var text = (element.Text ?? string.Empty);

        if (!element.Template.IsEmpty) {
            text = ComposeTemplate(segments: element.Template.Span);
        } else if (
            (element.Binding is { Length: > 0 } binding) &&
            m_bindings.TryResolve(
            binding: binding,
            fraction: out _,
            text: out var resolved
        )
        ) {
            text = resolved;
        }

        if (text.Length == 0) {
            return;
        }

        var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: h);

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

    void IOverlaySeatEmitter<OverlayHudSeatPanel>.EmitSeat(OverlayFrameBuilder builder, in OverlayHudSeatPanel seat) => EmitSeatPanel(
        builder: builder,
        seat: in seat
    );

    /// <summary>Emits every under-band panel, in document order.</summary>
    /// <param name="builder">The frame builder.</param>
    public void EmitUnder(OverlayFrameBuilder builder) => EmitBand(
        band: OverlayHudBand.Under,
        builder: builder
    );
    /// <summary>Refreshes the cached structural snapshot for the frame about to produce. Call exactly once per
    /// produced frame, before any <c>Emit*</c> call.</summary>
    public void RefreshFrame() {
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
