namespace Puck.Overlays;

/// <summary>
/// The authored-HUD writer: renders <see cref="HudStore"/>'s structural snapshot in four separate calls —
/// <see cref="EmitUnder"/>, <see cref="EmitReplace"/>, <see cref="EmitOver"/> (the WORLD-scope bands, one per band,
/// so <c>UnifiedOverlayNode</c>'s banded pipeline can sequence them around the five first-party writers' base slot)
/// and <see cref="EmitSeatPanels"/> (the PLAYER-scope per-seat panels, unbanded — see its own remarks). Each call
/// resolves every bound element's LIVE value through <see cref="IHudBindingResolver"/> at emission time
/// (presentation float; resolved fresh every produced frame, never cached across frames) and draws rect/text/gauge
/// elements CONFINED to their owning panel's rect via <see cref="OverlayFrameBuilder.BeginClip"/>, the same
/// clip-scope contract <see cref="EditorHudWriter"/> uses.
/// </summary>
public sealed class HudWriter {
    /// <summary>The glyph-word ceiling ONE text element's run is clipped to — <c>WorldHudCapacity.TextWordCost</c>'s
    /// render-side twin, and the per-element term <see cref="OverlayChannelLeases"/> multiplies into the Hud
    /// reservation. Enforced at <see cref="OverlayFrameBuilder.WriteText"/>'s own <c>maxChars</c> clamp, so the
    /// reservation arithmetic describes what the writer can actually emit: a template resolving many long
    /// <c>state</c> cells (each up to <c>WorldStateCapacity.MaxTextValueLength</c>) clips as this writer's OWN
    /// attributed refusal instead of eating the shared channel budget and dropping other elements' records.</summary>
    public const int TextRunChars = 64;

    // A gauge's fixed layout budget: a track rect + a fill rect + one label run — WorldHudCapacity.GaugeElementCost /
    // GaugeWordCost's render-side twin (Puck.World.Data cannot be referenced here; the two are kept in step by the
    // combined reservation's static assertion in OverlayChannelLeases).
    private const int GaugeLabelChars = 16;
    private const float GaugeTrackAlpha = 0.35f;

    private readonly IHudBindingResolver m_bindings;
    private readonly IHudSource m_source;
    private OverlayHudFrame m_frame;
    private bool m_hasFrame;

    /// <summary>Initializes a new instance of the <see cref="HudWriter"/> class.</summary>
    /// <param name="source">The HUD structure source.</param>
    /// <param name="bindings">The live binding resolver.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public HudWriter(IHudSource source, IHudBindingResolver bindings) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: bindings);

        m_source = source;
        m_bindings = bindings;
    }

    /// <summary>Refreshes the cached structural snapshot for the frame about to produce. Call exactly once per
    /// produced frame, before any <c>Emit*</c> call.</summary>
    public void RefreshFrame() {
        m_hasFrame = m_source.TrySnapshot(frame: out m_frame);
    }

    /// <summary>Whether at least one live panel declares <see cref="OverlayHudBand.Replace"/> this frame — the
    /// banded pipeline's base-slot decision (the caller checks this BEFORE running the five first-party writers).</summary>
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

    /// <summary>Emits every UNDER-band panel, in document order.</summary>
    /// <param name="builder">The frame builder.</param>
    public void EmitUnder(OverlayFrameBuilder builder) => EmitBand(builder: builder, band: OverlayHudBand.Under);

    /// <summary>Emits every REPLACE-band panel, in document order — the base slot when <see cref="HasReplace"/>.</summary>
    /// <param name="builder">The frame builder.</param>
    public void EmitReplace(OverlayFrameBuilder builder) => EmitBand(builder: builder, band: OverlayHudBand.Replace);

    /// <summary>Emits every OVER-band panel, in document order.</summary>
    /// <param name="builder">The frame builder.</param>
    public void EmitOver(OverlayFrameBuilder builder) => EmitBand(builder: builder, band: OverlayHudBand.Over);

    /// <summary>Emits every PLAYER-scope (per-seat) panel — the EditorHud per-seat precedent: each panel is CONFINED
    /// to its owning seat's viewport via one <see cref="OverlayFrameBuilder.BeginClip"/> scope (clip scopes do not
    /// nest, so this does NOT also open the world-scope panel's own per-panel clip), with the panel positioned
    /// LOCAL to that viewport rather than the whole screen. Bands are not meaningful for a seat panel (it has no
    /// base slot to take over), so this runs once, outside the under/base/over sequence.</summary>
    /// <param name="builder">The frame builder.</param>
    /// <exception cref="InvalidOperationException">The published frame carries more seat panels than
    /// <see cref="OverlayChannelLeases.MaxSeats"/> provisions for.</exception>
    public void EmitSeatPanels(OverlayFrameBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (!m_hasFrame) {
            return;
        }

        var seats = m_frame.SeatPanels.Span;

        OverlayChannelLeases.EnsureSeatCapacity(seatCount: seats.Length, writerName: nameof(HudWriter));

        for (var index = 0; (index < seats.Length); index++) {
            EmitSeatPanel(builder: builder, seat: in seats[index]);
        }
    }

    private void EmitSeatPanel(OverlayFrameBuilder builder, in OverlayHudSeatPanel seat) {
        var viewport = seat.Viewport;

        if ((viewport.Width <= 0f) || (viewport.Height <= 0f)) {
            return;
        }

        var vx = (viewport.X * builder.Width);
        var vy = (viewport.Y * builder.Height);
        var vw = (viewport.Width * builder.Width);
        var vh = (viewport.Height * builder.Height);

        builder.BeginClip(x: vx, y: vy, w: vw, h: vh);

        var panel = seat.Panel;
        var rect = panel.Rect;

        if ((rect.Width > 0f) && (rect.Height > 0f)) {
            var x = (vx + (rect.X * vw));
            var y = (vy + (rect.Y * vh));
            var w = (rect.Width * vw);
            var h = (rect.Height * vh);

            builder.WritePanel(x: x, y: y, w: w, h: h, titleBand: false, bandHeight: 0f, style: panel.Style, ringRole: null, alpha: 1f);

            var elements = panel.Elements.Span;

            for (var index = 0; (index < elements.Length); index++) {
                EmitElement(builder: builder, element: in elements[index], panelX: x, panelY: y, panelW: w, panelH: h);
            }
        }

        builder.EndClip();
    }

    private void EmitBand(OverlayFrameBuilder builder, OverlayHudBand band) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (!m_hasFrame) {
            return;
        }

        var panels = m_frame.Panels.Span;

        for (var index = 0; (index < panels.Length); index++) {
            if (panels[index].Band == band) {
                EmitPanel(builder: builder, panel: in panels[index]);
            }
        }
    }

    private void EmitPanel(OverlayFrameBuilder builder, in OverlayHudPanel panel) {
        var rect = panel.Rect;

        if ((rect.Width <= 0f) || (rect.Height <= 0f)) {
            return;
        }

        var x = (rect.X * builder.Width);
        var y = (rect.Y * builder.Height);
        var w = (rect.Width * builder.Width);
        var h = (rect.Height * builder.Height);

        builder.BeginClip(x: x, y: y, w: w, h: h);
        builder.WritePanel(x: x, y: y, w: w, h: h, titleBand: false, bandHeight: 0f, style: panel.Style, ringRole: null, alpha: 1f);

        var elements = panel.Elements.Span;

        for (var index = 0; (index < elements.Length); index++) {
            EmitElement(builder: builder, element: in elements[index], panelX: x, panelY: y, panelW: w, panelH: h);
        }

        builder.EndClip();
    }

    private void EmitElement(OverlayFrameBuilder builder, in OverlayHudElement element, float panelX, float panelY, float panelW, float panelH) {
        var rect = element.Rect;

        if ((rect.Width <= 0f) || (rect.Height <= 0f)) {
            return;
        }

        var x = (panelX + (rect.X * panelW));
        var y = (panelY + (rect.Y * panelH));
        var w = (rect.Width * panelW);
        var h = (rect.Height * panelH);

        switch (element.Kind) {
            case OverlayHudElementKind.Rect:
                builder.WriteRect(x: x, y: y, w: w, h: h, role: element.Role, radius: 0f, alpha: 1f);

                break;
            case OverlayHudElementKind.Text:
                EmitText(builder: builder, element: in element, x: x, y: y, h: h);

                break;
            case OverlayHudElementKind.Gauge:
                EmitGauge(builder: builder, element: in element, x: x, y: y, w: w, h: h);

                break;
        }
    }

    private void EmitText(OverlayFrameBuilder builder, in OverlayHudElement element, float x, float y, float h) {
        var text = element.Text ?? string.Empty;

        if (!element.Template.IsEmpty) {
            text = ComposeTemplate(segments: element.Template.Span);
        } else if ((element.Binding is { Length: > 0 } binding) && m_bindings.TryResolve(binding: binding, fraction: out _, text: out var resolved)) {
            text = resolved;
        }

        if (text.Length == 0) {
            return;
        }

        var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: h);

        builder.WriteText(x: x, y: y, text: text, cellHeight: cellHeight, role: element.Role, alpha: 1f, maxChars: TextRunChars);
    }

    // Substitution only — the brace/escape GRAMMAR is parsed once by Puck.World.Data's HudTemplate and arrives here
    // as runs, so this project restates none of it (a mirrored constant can be eyeballed for drift; a mirrored
    // grammar cannot). A placeholder that fails to resolve appends nothing: the document validator already refused
    // an unknown one before it could reach a live document, so an empty substitution keeps the frame drawing rather
    // than standing in for a refusal that belongs upstream.
    private string ComposeTemplate(ReadOnlySpan<OverlayHudTemplateSegment> segments) {
        var builder = new System.Text.StringBuilder();

        for (var index = 0; (index < segments.Length); index++) {
            var segment = segments[index];

            if (!segment.IsPlaceholder) {
                builder.Append(value: segment.Text);

                continue;
            }

            if (m_bindings.TryResolve(binding: segment.Text, fraction: out _, text: out var resolved)) {
                builder.Append(value: resolved);
            }
        }

        return builder.ToString();
    }

    private void EmitGauge(OverlayFrameBuilder builder, in OverlayHudElement element, float x, float y, float w, float h) {
        var fraction = 0f;
        var label = string.Empty;

        if ((element.Binding is { Length: > 0 } binding) && m_bindings.TryResolve(binding: binding, fraction: out var resolved, text: out var text)) {
            fraction = Math.Clamp(value: resolved, min: 0f, max: 1f);
            label = text;
        }

        // Track (always the full extent) + fill (scaled by the resolved fraction) + a short value label — the 3
        // render elements WorldHudCapacity.GaugeElementCost reserves.
        builder.WriteRect(x: x, y: y, w: w, h: h, role: OverlayColorRole.SurfaceInset, radius: 0f, alpha: GaugeTrackAlpha);
        builder.WriteRect(x: x, y: y, w: (w * fraction), h: h, role: element.Role, radius: 0f, alpha: 1f);

        if (label.Length > 0) {
            var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: h);

            builder.WriteText(x: x, y: y, text: label, cellHeight: cellHeight, role: OverlayColorRole.TextPrimary, alpha: 1f, maxChars: GaugeLabelChars);
        }
    }
}
