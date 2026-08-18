namespace Puck.Overlays;

/// <summary>
/// The radial-action-menu writer: renders each open seat's wheel from an <see cref="IWheelSource"/> snapshot — up to
/// <see cref="MaxRings"/> concentric ring outlines around a hub dot, every ring's sector labels arranged clockwise
/// from twelve o'clock, the ACTIVE ring stroked twice (a visibly heavier shell) with its label beside the hub, and
/// the hovered sector's label lit in the accent hue with a marker dot on its angle — all inside a
/// <see cref="OverlayFrameBuilder.BeginClip"/> scope on the seat's viewport rect, the <see cref="CursorWriter"/>
/// discipline. Pure record emission; the geometry (center, radii, active/hovered indices) is the HOST's decision,
/// published in the snapshot, so what this draws and what a release commits can never disagree.
/// </summary>
public sealed class WheelWriter : IOverlaySeatEmitter<OverlayWheelSeat> {
    private const float ActiveRingAlpha = 0.95f;
    private const float HubDotHalf = 3f;
    private const float LabelAlpha = 1f;
    private const float MarkerHalf = 3.5f;
    // The same emptied-viewport guard the cursor writer applies before opening a clip scope on the region.
    private const float MinRegionExtent = 0.05f;
    private const float RingAlpha = 0.55f;

    /// <summary>The active-ring label's character clamp — <see cref="MaxSectorLabelChars"/>' hub-label twin.</summary>
    public const int MaxRingLabelChars = 16;
    /// <summary>The most rings a published seat may carry — mirrors the binding substrate's wheel bound (the
    /// authored document is validated against its own <c>BindingWheelDefinition.MaxRings</c>; this is the
    /// render-side backstop the reservation is sized from).</summary>
    public const int MaxRings = 3;
    /// <summary>The sector-label character clamp — the ONE source <see cref="OverlayChannelLeases"/> reads for this
    /// channel's per-sector text reservation; every sector <c>WriteText</c> call clamps to it.</summary>
    public const int MaxSectorLabelChars = 12;
    /// <summary>The most sectors a published ring may carry — <see cref="MaxRings"/>' per-ring twin.</summary>
    public const int MaxSectorsPerRing = 8;

    private readonly IWheelSource m_source;

    /// <summary>Initializes a new instance of the <see cref="WheelWriter"/> class.</summary>
    /// <param name="source">The wheel snapshot source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public WheelWriter(IWheelSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_source = source;
    }

    void IOverlaySeatEmitter<OverlayWheelSeat>.EmitSeat(OverlayFrameBuilder builder, in OverlayWheelSeat seat) =>
        EmitSeat(
            builder: builder,
            seat: in seat
        );

    private static void EmitSeat(OverlayFrameBuilder builder, in OverlayWheelSeat seat) {
        var region = seat.Viewport;

        if (
            (region.Width < MinRegionExtent) ||
            (region.Height < MinRegionExtent)
        ) {
            return;
        }

        var rings = seat.Rings.Span;
        var ringCount = Math.Min(
            val1: rings.Length,
            val2: MaxRings
        );

        if (ringCount == 0) {
            return;
        }

        var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: DesignTokens.Type.TypeMicroSize);

        builder.BeginClip(
            h: (region.Height * builder.Height),
            w: (region.Width * builder.Width),
            x: (region.X * builder.Width),
            y: (region.Y * builder.Height)
        );

        // The hub dot — quiet, in the dim hue: a release here cancels, so nothing about it should read committal.
        builder.WriteRect(
            alpha: LabelAlpha,
            h: (HubDotHalf * 2f),
            radius: HubDotHalf,
            role: OverlayColorRole.TextDim,
            w: (HubDotHalf * 2f),
            x: (seat.CenterX - HubDotHalf),
            y: (seat.CenterY - HubDotHalf)
        );

        for (var ringIndex = 0; (ringIndex < ringCount); ringIndex++) {
            var isActive = (ringIndex == seat.ActiveRing);
            var centerline = (seat.InnerRadius + ((ringIndex + 0.5f) * seat.RingWidth));

            builder.WriteRing(
                alpha: (isActive
                ? ActiveRingAlpha
                : RingAlpha),
                centerX: seat.CenterX,
                centerY: seat.CenterY,
                radius: centerline,
                role: (isActive
                ? OverlayColorRole.TextPrimary
                : OverlayColorRole.TextDim)
            );

            if (isActive) {
                // A second stroke one pixel out reads as a heavier shell — the active-ring highlight.
                builder.WriteRing(
                    alpha: ActiveRingAlpha,
                    centerX: seat.CenterX,
                    centerY: seat.CenterY,
                    radius: (centerline + 1.5f),
                    role: OverlayColorRole.TextPrimary
                );
            }

            var sectors = rings[ringIndex].Sectors.Span;
            var sectorCount = Math.Min(
                val1: sectors.Length,
                val2: MaxSectorsPerRing
            );

            if (sectorCount == 0) {
                continue;
            }

            var span = (MathF.Tau / sectorCount);

            for (var sectorIndex = 0; (sectorIndex < sectorCount); sectorIndex++) {
                // Layout policy is authored with the radial. The host uses the identical transform for selection.
                var angle = (seat.RotationRadians + (((seat.Clockwise
                    ? 1f
                    : -1f) * sectorIndex) * span));
                var labelX = (seat.CenterX + (MathF.Sin(x: angle) * centerline));
                var labelY = (seat.CenterY - (MathF.Cos(x: angle) * centerline));
                var label = sectors[sectorIndex];
                var isHovered = (isActive && (sectorIndex == seat.HoveredSector));
                var width = builder.TextWidth(
                    chars: Math.Min(
                        val1: label.Length,
                        val2: MaxSectorLabelChars
                    ),
                    cellHeight: cellHeight
                );

                if (isHovered) {
                    // The marker sits on the sector's own angle, just outside the ring.
                    var markerRadius = (centerline + (cellHeight * 1.6f));

                    builder.WriteRect(
                        alpha: LabelAlpha,
                        h: (MarkerHalf * 2f),
                        radius: MarkerHalf,
                        role: OverlayColorRole.Accent,
                        w: (MarkerHalf * 2f),
                        x: ((seat.CenterX + (MathF.Sin(x: angle) * markerRadius)) - MarkerHalf),
                        y: ((seat.CenterY - (MathF.Cos(x: angle) * markerRadius)) - MarkerHalf)
                    );
                }

                builder.WriteText(
                    alpha: (isActive
                    ? LabelAlpha
                    : RingAlpha),
                    cellHeight: cellHeight,
                    maxChars: MaxSectorLabelChars,
                    role: (isHovered
                    ? OverlayColorRole.Accent
                    : (isActive
                        ? OverlayColorRole.TextPrimary
                        : OverlayColorRole.TextDim)),
                    text: label,
                    x: (labelX - (width * 0.5f)),
                    y: (labelY - (cellHeight * 0.5f))
                );
            }
        }

        // The active ring's label under the hub — which shell the wheel is cycling within, without hunting.
        var activeLabel = rings[Math.Clamp(
            value: seat.ActiveRing,
            min: 0,
            max: (ringCount - 1)
        )].Label;

        if (activeLabel.Length > 0) {
            var activeWidth = builder.TextWidth(
                chars: Math.Min(
                    val1: activeLabel.Length,
                    val2: MaxRingLabelChars
                ),
                cellHeight: cellHeight
            );

            builder.WriteText(
                alpha: LabelAlpha,
                cellHeight: cellHeight,
                maxChars: MaxRingLabelChars,
                role: OverlayColorRole.TextPrimary,
                text: activeLabel,
                x: (seat.CenterX - (activeWidth * 0.5f)),
                y: ((seat.CenterY + (HubDotHalf * 2f)) + 2f)
            );
        }

        builder.EndClip();
    }

    /// <summary>Emits this frame's per-seat wheel records, when a snapshot has been published.</summary>
    /// <param name="builder">The frame builder.</param>
    /// <exception cref="InvalidOperationException">The published frame carries more seats than
    /// <see cref="OverlayChannelLeases.MaxSeats"/> provisions for.</exception>
    public void Emit(OverlayFrameBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (!m_source.TrySnapshot(frame: out var frame)) {
            return;
        }

        OverlaySeatLoop.Emit(
            builder: builder,
            seats: frame.Seats.Span,
            writerName: nameof(WheelWriter),
            writer: this
        );
    }
}
