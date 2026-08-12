namespace Puck.Overlays;

/// <summary>
/// The editor-HUD writer: renders each EDITING seat's selection readout from an <see cref="IEditorHudSource"/>
/// snapshot as a compact strip panel in the seat's top-left corner — a title band, the selection line, the context
/// hint, the session-honesty line (last act class / drift / exclusive holds), and the drag line (accent-ringed while
/// a drag is live) — CONFINED to that seat's normalized viewport rect: the panel anchors at the rect's top-left AND
/// every record rides a <see cref="OverlayFrameBuilder.BeginClip"/> scope on the same rect, so a narrow seat CUTS
/// the HUD at its boundary instead of bleeding into a neighbor (the clip-scope contract). Pure record emission;
/// no GPU types. A deliberate NON-consumer of <see cref="PadPictogramLayout"/>: the binding bar already renders the
/// active chord page's full chip cluster per seat, so a second pictogram here would duplicate that surface at lower
/// fidelity.
/// </summary>
public sealed class EditorHudWriter : IOverlaySeatEmitter<OverlayEditorSeat> {
    /// <summary>The readout lines one seat's strip draws: the selection, the context hint, the session-honesty line
    /// and the drag line.</summary>
    public const int MaxLines = 4;
    /// <summary>The character clamp on every readout line.</summary>
    public const int MaxLineChars = 46;
    /// <summary>The <see cref="Title"/> literal's character count — the ONE source <see cref="OverlayChannelLeases"/>
    /// reads for its text-word reservation. The <c>WriteText</c> call for <see cref="Title"/> clamps to this
    /// constant, so an edit to <see cref="Title"/> that forgets to update it truncates (reported, never silent)
    /// instead of quietly overrunning the reservation.</summary>
    public const int TitleChars = 6;

    private const float MinRegionExtent = 0.05f;
    private const string Title = "EDITOR";

    static EditorHudWriter() {
        System.Diagnostics.Debug.Assert(
            condition: (Title.Length == TitleChars),
            message: "EditorHudWriter.Title's length drifted from TitleChars — update TitleChars (and OverlayChannelLeases' EditorHudTextWordsPerSeat, which reads it) to match."
        );
    }

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
    /// <exception cref="InvalidOperationException">The published frame carries more seats than
    /// <see cref="OverlayChannelLeases.MaxSeats"/> provisions for.</exception>
    public void Emit(OverlayFrameBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (!m_source.TrySnapshot(frame: out var frame)) {
            return;
        }

        var seats = frame.Seats.Span;

        OverlaySeatLoop.Emit(
            builder: builder,
            seats: seats,
            writerName: nameof(EditorHudWriter),
            writer: this
        );
    }

    // One seat's panel: sized to its longest line, anchored at the seat region's top-left with the standard gutter,
    // and CLIPPED to the region (a 46-char line can outgrow a narrow split viewport; the boundary wins).
    private static void EmitSeat(OverlayFrameBuilder builder, in OverlayEditorSeat seat) {
        var region = seat.Viewport;

        if (
            (region.Width < MinRegionExtent) ||
            (region.Height < MinRegionExtent)
        ) {
            return;
        }

        builder.BeginClip(
            h: (region.Height * builder.Height),
            w: (region.Width * builder.Width),
            x: (region.X * builder.Width),
            y: (region.Y * builder.Height)
        );

        var monoCell = OverlayFrameBuilder.CellHeight(sizePx: DesignTokens.Type.TypeMonoSize);
        var microCell = OverlayFrameBuilder.CellHeight(sizePx: DesignTokens.Type.TypeMicroSize);
        var lineStep = (monoCell + DesignTokens.Space.Space1);
        var lineCount = Math.Max(
            val1: 1,
            val2: (((CountPresent(text: seat.SelectionLine) + CountPresent(text: seat.ContextLine)) + CountPresent(text: seat.SessionLine)) + CountPresent(text: seat.DragLine))
        );
        var widestChars = Math.Min(
            val1: MaxLineChars,
            val2: Math.Max(
                val1: Title.Length,
                val2: Math.Max(
                    val1: Math.Max(
                        val1: seat.SelectionLine.Length,
                        val2: seat.SessionLine.Length
                    ),
                    val2: Math.Max(
                        val1: seat.ContextLine.Length,
                        val2: seat.DragLine.Length
                    )
                )
            )
        );
        var panelWidth = ((DesignTokens.Space.Space3 * 2f) + builder.TextWidth(
            chars: widestChars,
            cellHeight: monoCell
        ));
        var bandHeight = (microCell + DesignTokens.Space.Space2);
        var panelHeight = ((bandHeight + DesignTokens.Space.Space2) + (lineCount * lineStep));
        var x = ((region.X * builder.Width) + DesignTokens.Space.Space4);
        var y = ((region.Y * builder.Height) + DesignTokens.Space.Space4);

        builder.WritePanel(
            alpha: 1f,
            bandHeight: bandHeight,
            h: panelHeight,
            ringRole: (seat.DragActive
            ? OverlayColorRole.Accent
            : (OverlayColorRole?)null),
            style: OverlayPanelStyle.Strip,
            titleBand: true,
            w: panelWidth,
            x: x,
            y: y
        );
        builder.WriteText(
            alpha: 1f,
            cellHeight: microCell,
            maxChars: TitleChars,
            role: OverlayColorRole.TextDim,
            text: Title,
            x: (x + DesignTokens.Space.Space3),
            y: (y + ((bandHeight - microCell) * 0.5f))
        );

        var lineY = ((y + bandHeight) + DesignTokens.Space.Space2);

        lineY = EmitLine(
            builder: builder,
            text: seat.SelectionLine,
            role: OverlayColorRole.TextPrimary,
            x: (x + DesignTokens.Space.Space3),
            y: lineY,
            cellHeight: monoCell,
            lineStep: lineStep
        );
        lineY = EmitLine(
            builder: builder,
            text: seat.ContextLine,
            role: OverlayColorRole.TextDim,
            x: (x + DesignTokens.Space.Space3),
            y: lineY,
            cellHeight: monoCell,
            lineStep: lineStep
        );
        lineY = EmitLine(
            builder: builder,
            text: seat.SessionLine,
            role: OverlayColorRole.TextDim,
            x: (x + DesignTokens.Space.Space3),
            y: lineY,
            cellHeight: monoCell,
            lineStep: lineStep
        );
        _ = EmitLine(
            builder: builder,
            text: seat.DragLine,
            role: OverlayColorRole.Accent,
            x: (x + DesignTokens.Space.Space3),
            y: lineY,
            cellHeight: monoCell,
            lineStep: lineStep
        );
        builder.EndClip();
    }

    void IOverlaySeatEmitter<OverlayEditorSeat>.EmitSeat(OverlayFrameBuilder builder, in OverlayEditorSeat seat) =>
        EmitSeat(
        builder: builder,
        seat: in seat
    );

    private static float EmitLine(OverlayFrameBuilder builder, string text, OverlayColorRole role, float x, float y, int cellHeight, float lineStep) {
        if (text.Length == 0) {
            return y;
        }

        builder.WriteText(
            alpha: 1f,
            cellHeight: cellHeight,
            maxChars: MaxLineChars,
            role: role,
            text: text,
            x: x,
            y: y
        );

        return (y + lineStep);
    }
    private static int CountPresent(string text) => ((text.Length > 0)
        ? 1
        : 0);
}
