namespace Puck.Overlays;

/// <summary>
/// The editor-HUD writer: renders each EDITING seat's selection readout from an <see cref="IEditorHudSource"/>
/// snapshot as a compact strip panel in the seat's top-left corner — a title band, the selection line, the context
/// hint, and the drag line (accent-ringed while a drag is live) — CONFINED to that seat's normalized viewport rect
/// like the binding bar, so split-screen gets per-seat HUDs with the render node staying dumb. Pure record emission;
/// no GPU types. A deliberate NON-consumer of <see cref="PadPictogramLayout"/>: the binding bar already renders the
/// active chord page's full chip cluster per seat, so a second pictogram here would duplicate that surface at lower
/// fidelity.
/// </summary>
public sealed class EditorHudWriter {
    private const float MinRegionExtent = 0.05f;
    private const int MaxLineChars = 46;
    private const string Title = "EDITOR";

    private readonly IEditorHudSource m_source;

    /// <summary>Initializes a new instance of the <see cref="EditorHudWriter"/> class.</summary>
    /// <param name="source">The editor-HUD snapshot source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public EditorHudWriter(IEditorHudSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_source = source;
    }

    /// <summary>Emits this frame's per-seat HUD panels, when a snapshot has been published.</summary>
    /// <param name="builder">The frame builder.</param>
    public void Emit(OverlayFrameBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (!m_source.TrySnapshot(frame: out var frame)) {
            return;
        }

        var seats = frame.Seats.Span;

        for (var index = 0; (index < seats.Length); index++) {
            EmitSeat(builder: builder, seat: in seats[index]);
        }
    }

    // One seat's panel: sized to its longest line, anchored at the seat region's top-left with the standard gutter.
    private static void EmitSeat(OverlayFrameBuilder builder, in OverlayEditorSeat seat) {
        var region = seat.Viewport;

        if ((region.Width < MinRegionExtent) || (region.Height < MinRegionExtent)) {
            return;
        }

        var monoCell = OverlayFrameBuilder.CellHeight(sizePx: DesignTokens.Type.TypeMonoSize);
        var microCell = OverlayFrameBuilder.CellHeight(sizePx: DesignTokens.Type.TypeMicroSize);
        var lineStep = (monoCell + DesignTokens.Space.Space1);
        var lineCount = Math.Max(val1: 1, val2: ((CountPresent(text: seat.SelectionLine) + CountPresent(text: seat.ContextLine)) + CountPresent(text: seat.DragLine)));
        var widestChars = Math.Min(val1: MaxLineChars, val2: Math.Max(
            val1: Title.Length,
            val2: Math.Max(val1: seat.SelectionLine.Length, val2: Math.Max(val1: seat.ContextLine.Length, val2: seat.DragLine.Length))
        ));
        var panelWidth = ((DesignTokens.Space.Space3 * 2f) + builder.TextWidth(chars: widestChars, cellHeight: monoCell));
        var bandHeight = (microCell + DesignTokens.Space.Space2);
        var panelHeight = ((bandHeight + DesignTokens.Space.Space2) + (lineCount * lineStep));
        var x = ((region.X * builder.Width) + DesignTokens.Space.Space4);
        var y = ((region.Y * builder.Height) + DesignTokens.Space.Space4);

        builder.WritePanel(
            alpha: 1f,
            bandHeight: bandHeight,
            h: panelHeight,
            ringRole: (seat.DragActive ? OverlayColorRole.Accent : (OverlayColorRole?)null),
            style: OverlayPanelStyle.Strip,
            titleBand: true,
            w: panelWidth,
            x: x,
            y: y
        );
        builder.WriteText(
            alpha: 1f,
            cellHeight: microCell,
            role: OverlayColorRole.TextDim,
            text: Title,
            x: (x + DesignTokens.Space.Space3),
            y: (y + ((bandHeight - microCell) * 0.5f))
        );

        var lineY = ((y + bandHeight) + DesignTokens.Space.Space2);

        lineY = EmitLine(builder: builder, text: seat.SelectionLine, role: OverlayColorRole.TextPrimary, x: (x + DesignTokens.Space.Space3), y: lineY, cellHeight: monoCell, lineStep: lineStep);
        lineY = EmitLine(builder: builder, text: seat.ContextLine, role: OverlayColorRole.TextDim, x: (x + DesignTokens.Space.Space3), y: lineY, cellHeight: monoCell, lineStep: lineStep);
        _ = EmitLine(builder: builder, text: seat.DragLine, role: OverlayColorRole.Accent, x: (x + DesignTokens.Space.Space3), y: lineY, cellHeight: monoCell, lineStep: lineStep);
    }

    private static float EmitLine(OverlayFrameBuilder builder, string text, OverlayColorRole role, float x, float y, int cellHeight, float lineStep) {
        if (text.Length == 0) {
            return y;
        }

        builder.WriteText(alpha: 1f, cellHeight: cellHeight, maxChars: MaxLineChars, role: role, text: text, x: x, y: y);

        return (y + lineStep);
    }

    private static int CountPresent(string text) => ((text.Length > 0) ? 1 : 0);
}
