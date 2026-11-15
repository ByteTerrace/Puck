namespace Puck.Overlays;

public sealed partial class AnnotationWriter : IOverlaySeatEmitter<OverlayMarkerSeat> {
    // A viewport eased/shrunk to nothing has nowhere to place a chip — the guard every per-seat writer applies
    // before opening a clip scope on the region (BindingBarWriter/CursorWriter/WheelWriter each carry their own
    // copy; not promoted to a shared theme field because it is a rendering-geometry guard, not a look choice).
    private const float MinRegionExtent = 0.05f;

    private readonly int m_maxChipsPerSeat = ((maxChipsPerSeat >= 0)
        ? maxChipsPerSeat
        : throw new ArgumentOutOfRangeException(
            actualValue: maxChipsPerSeat,
            message: "maxChipsPerSeat must not be negative.",
            paramName: nameof(maxChipsPerSeat)
        ));

    void IOverlaySeatEmitter<OverlayMarkerSeat>.EmitSeat(OverlayFrameBuilder builder, in OverlayMarkerSeat seat) {
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
            val2: m_maxChipsPerSeat
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
            if (chip.RingRadiusPx > 0f) {
                builder.WriteRing(
                    alpha: chip.RingAlpha,
                    centerX: chip.CenterX,
                    centerY: chip.CenterY,
                    color: chip.RingColor,
                    radius: chip.RingRadiusPx
                );
            }

            builder.WriteIcon(
                accent: chip.Selected,
                alpha: chip.ChipAlpha,
                badgeGlyph0: 0,
                badgeGlyph1: 0,
                bound: true,
                centerX: chip.CenterX,
                centerY: chip.CenterY,
                glyphHalf: 0f,
                glyphOffsetX: 0f,
                glyphOffsetY: 0f,
                iconGlyph0: chip.IconGlyph0,
                iconGlyph1: chip.IconGlyph1,
                plateHalf: chip.PlateHalf,
                pressed: chip.Pulse
            );
        }

        builder.EndClip();
    }

    /// <summary>Emits this frame's per-seat marker records: an icon chip at every projected pose, and a translucent
    /// hairline radius ring when the source instance resolves one, all inside a clip scope on the seat's viewport rect
    /// (a chip near a split boundary cuts, never bleeds). Every look value arrives already resolved on the chip.</summary>
    /// <param name="builder">The frame builder, inside the <see cref="OverlayChannel.Markers"/> scope.</param>
    /// <exception cref="InvalidOperationException">The published frame carries more seats than
    /// <see cref="OverlayChannelLeases.MaxSeats"/> provisions for.</exception>
    public void EmitMarkers(OverlayFrameBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if ((markers is null) || !markers.TrySnapshot(frame: out var frame)) {
            return;
        }

        OverlaySeatLoop.Emit(
            builder: builder,
            seats: frame.Seats.Span,
            writerName: nameof(AnnotationWriter),
            writer: this
        );
    }
}
