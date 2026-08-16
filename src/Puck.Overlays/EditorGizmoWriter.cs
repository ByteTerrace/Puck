namespace Puck.Overlays;

/// <summary>
/// The editor-gizmo writer: draws each EDITING seat's projected gizmo chips from an <see cref="IEditorGizmoSource"/>
/// snapshot — an icon chip (the world icon grammar's speaker/bed symbols) at every projected pose, and a translucent
/// hairline radius RING for region rows — all inside a <see cref="OverlayFrameBuilder.BeginClip"/> scope on the
/// seat's viewport rect (a chip near a split boundary cuts, never bleeds). The chip-state tiers carry the
/// editor semantics for free: selection lights the ACCENT tier, a live change shimmer the HELD tier. Pure record
/// emission; no GPU types (a surface is a writer, never a new shader).
/// </summary>
public sealed class EditorGizmoWriter : IOverlaySeatEmitter<OverlayGizmoSeat> {
    private const float ChipAlpha = 0.9f;
    // A viewport eased/shrunk to nothing has nowhere to place a chip — the guard the bar and the HUD both apply
    // before opening a clip scope on the region.
    private const float MinRegionExtent = 0.05f;
    // The gizmo plate half-extent, px — deliberately below the binding bar's reference chip so a gizmo reads as a
    // marker in the world, not a pressable control.
    private const float PlateHalf = 12f;
    private const float RingAlpha = 0.35f;

    /// <summary>The projected chips one seat draws. The host admits the nearest rows to the camera up to this
    /// count; anything past it is refused at the gizmo channel's own boundary, attributed.</summary>
    public const int MaxChipsPerSeat = 16;

    private readonly IEditorGizmoSource m_source;

    /// <summary>Initializes a new instance of the <see cref="EditorGizmoWriter"/> class.</summary>
    /// <param name="source">The gizmo snapshot source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public EditorGizmoWriter(IEditorGizmoSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_source = source;
    }

    void IOverlaySeatEmitter<OverlayGizmoSeat>.EmitSeat(OverlayFrameBuilder builder, in OverlayGizmoSeat seat) =>
        EmitSeat(
            builder: builder,
            seat: in seat
        );

    private static void EmitSeat(OverlayFrameBuilder builder, in OverlayGizmoSeat seat) {
        var chips = seat.Chips.Span;
        var region = seat.Viewport;

        if (
            (chips.Length == 0) ||
            ((region.Width < MinRegionExtent) || (region.Height < MinRegionExtent))
        ) {
            return;
        }

        var chipCount = Math.Min(
            val1: chips.Length,
            val2: MaxChipsPerSeat
        );

        if (chipCount < chips.Length) {
            // Each chip writes its ring and its plate; a refused chip loses both.
            builder.NoteRefused(
                elements: ((chips.Length - chipCount) * 2),
                textWords: 0
            );
        }

        builder.BeginClip(
            h: (region.Height * builder.Height),
            w: (region.Width * builder.Width),
            x: (region.X * builder.Width),
            y: (region.Y * builder.Height)
        );

        foreach (ref readonly var chip in chips[..chipCount]) {
            // The bed's presence ring first (under its own chip): the projected support radius as a translucent
            // hairline circle in the selection-aware hue.
            if (chip.RingRadiusPx > 0f) {
                builder.WriteRing(
                    alpha: RingAlpha,
                    centerX: chip.CenterX,
                    centerY: chip.CenterY,
                    radius: chip.RingRadiusPx,
                    role: (chip.Selected
                    ? OverlayColorRole.Accent
                    : OverlayColorRole.TextDim)
                );
            }

            builder.WriteIcon(
                accent: chip.Selected,
                alpha: ChipAlpha,
                bound: true,
                centerX: chip.CenterX,
                centerY: chip.CenterY,
                glyph: OverlayGlyphId.None,
                glyphHalf: 0f,
                glyphOffsetX: 0f,
                glyphOffsetY: 0f,
                icon: (chip.Bed
                ? OverlayIconId.AudioBed
                : OverlayIconId.AudioSpeaker),
                plateHalf: PlateHalf,
                pressed: chip.Pulse
            );
        }

        builder.EndClip();
    }

    /// <summary>Emits this frame's per-seat gizmo records, when a snapshot has been published.</summary>
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
            writerName: nameof(EditorGizmoWriter),
            writer: this
        );
    }
}
