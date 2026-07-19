namespace Puck.Overlays;

/// <summary>
/// The editor-gizmo writer: draws each EDITING seat's projected gizmo chips from an <see cref="IEditorGizmoSource"/>
/// snapshot — an icon chip (the world icon grammar's speaker/bed symbols) at every projected pose, and a translucent
/// hairline radius RING for region rows — all inside a <see cref="OverlayFrameBuilder.BeginClip"/> scope on the
/// seat's viewport rect (a chip near a split boundary cuts, never bleeds). The chip-state tiers carry the
/// editor semantics for free: selection lights the ACCENT tier, a live change shimmer the HELD tier. Pure record
/// emission; no GPU types (a surface is a writer, never a new shader).
/// </summary>
public sealed class EditorGizmoWriter {
    // The gizmo plate half-extent, px — deliberately below the binding bar's reference chip so a gizmo reads as a
    // marker in the world, not a pressable control.
    private const float PlateHalf = 12f;
    private const float ChipAlpha = 0.9f;
    private const float RingAlpha = 0.35f;

    private readonly IEditorGizmoSource m_source;

    /// <summary>Initializes a new instance of the <see cref="EditorGizmoWriter"/> class.</summary>
    /// <param name="source">The gizmo snapshot source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public EditorGizmoWriter(IEditorGizmoSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_source = source;
    }

    /// <summary>Emits this frame's per-seat gizmo records, when a snapshot has been published.</summary>
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

    private static void EmitSeat(OverlayFrameBuilder builder, in OverlayGizmoSeat seat) {
        var chips = seat.Chips.Span;

        if (chips.Length == 0) {
            return;
        }

        var region = seat.Viewport;

        builder.BeginClip(
            h: (region.Height * builder.Height),
            w: (region.Width * builder.Width),
            x: (region.X * builder.Width),
            y: (region.Y * builder.Height)
        );

        foreach (ref readonly var chip in chips) {
            // The bed's presence ring first (under its own chip): the projected support radius as a translucent
            // hairline circle in the selection-aware hue.
            if (chip.RingRadiusPx > 0f) {
                builder.WriteRing(
                    alpha: RingAlpha,
                    centerX: chip.CenterX,
                    centerY: chip.CenterY,
                    radius: chip.RingRadiusPx,
                    role: (chip.Selected ? OverlayColorRole.Accent : OverlayColorRole.TextDim)
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
                icon: (chip.Bed ? OverlayIconId.AudioBed : OverlayIconId.AudioSpeaker),
                plateHalf: PlateHalf,
                pressed: chip.Pulse
            );
        }

        builder.EndClip();
    }
}
