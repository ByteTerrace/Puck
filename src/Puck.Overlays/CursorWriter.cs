namespace Puck.Overlays;

/// <summary>
/// The drawn-cursor writer: renders each visible seat's pointer from an <see cref="ICursorSource"/> snapshot — a
/// hairline ring around a small center dot at the hotspot, plus the hover label (the tooltip text) beside it — all
/// inside a <see cref="OverlayFrameBuilder.BeginClip"/> scope on the seat's viewport rect, so the cursor is confined
/// to its own split-screen view. Hover lights the ACCENT hue; the bare cursor stays in the primary text hue. Pure
/// record emission; no GPU types and no OS cursor — the pointer's on-screen echo is a composed overlay layer like
/// every other surface.
/// </summary>
public sealed class CursorWriter : IOverlaySeatEmitter<OverlayCursorSeat> {
    private const float CursorAlpha = 0.9f;
    // The same emptied-viewport guard the marker writer applies before opening a clip scope on the region.
    private const float MinRegionExtent = 0.05f;

    /// <summary>The hover-label character clamp — the ONE source <see cref="OverlayChannelLeases"/> reads for the
    /// cursor channel's text-word reservation; every label <c>WriteText</c> call clamps to it.</summary>
    public const int MaxLabelChars = 48;
    /// <summary>The largest ring radius a seat may publish, px — the writer's own declared cap (the world-authored
    /// size is validated against its document band; this is the render-side backstop the host clamps to).</summary>
    public const float MaxSizePx = 64f;

    private readonly OverlayThemeStore m_theme;
    private readonly ICursorSource m_source;

    /// <summary>Initializes a new instance of the <see cref="CursorWriter"/> class.</summary>
    /// <param name="source">The cursor snapshot source.</param>
    /// <param name="theme">The live resolved theme.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="theme"/> is
    /// <see langword="null"/>.</exception>
    public CursorWriter(ICursorSource source, OverlayThemeStore theme) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: theme);

        m_source = source;
        m_theme = theme;
    }

    void IOverlaySeatEmitter<OverlayCursorSeat>.EmitSeat(OverlayFrameBuilder builder, in OverlayCursorSeat seat) =>
        EmitSeat(
            builder: builder,
            seat: in seat
        );

    private void EmitSeat(OverlayFrameBuilder builder, in OverlayCursorSeat seat) {
        var region = seat.Viewport;

        if (
            (region.Width < MinRegionExtent) ||
            (region.Height < MinRegionExtent)
        ) {
            return;
        }

        // Hover lights the accent tier; the bare cursor keeps the seat's world-authored hue. Geometry scales off
        // the authored ring radius: the center dot rides at roughly a fifth of it, the label clear of the ring.
        var role = (seat.Hover
            ? OverlayColorRole.Accent
            : seat.Role
        );
        var ringRadius = Math.Clamp(
            value: seat.SizePx,
            min: 1f,
            max: MaxSizePx
        );
        var dotHalf = Math.Clamp(
            max: 4f,
            min: 1f,
            value: (ringRadius * 0.22f)
        );
        var labelOffset = (ringRadius + 5f);

        builder.BeginClip(
            h: (region.Height * builder.Height),
            w: (region.Width * builder.Width),
            x: (region.X * builder.Width),
            y: (region.Y * builder.Height)
        );
        builder.WriteRing(
            alpha: CursorAlpha,
            centerX: seat.X,
            centerY: seat.Y,
            radius: ringRadius,
            role: role
        );
        builder.WriteRect(
            alpha: 1f,
            h: (dotHalf * 2f),
            radius: dotHalf,
            role: role,
            w: (dotHalf * 2f),
            x: (seat.X - dotHalf),
            y: (seat.Y - dotHalf)
        );

        if (seat.HoverLabel.Length > 0) {
            var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: m_theme.Current.Type.MicroSize);

            builder.WriteText(
                alpha: 1f,
                cellHeight: cellHeight,
                maxChars: MaxLabelChars,
                role: OverlayColorRole.Accent,
                text: seat.HoverLabel,
                x: (seat.X + labelOffset),
                y: (seat.Y + labelOffset)
            );
        }

        builder.EndClip();
    }

    /// <summary>Emits this frame's per-seat cursor records, when a snapshot has been published.</summary>
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
            writerName: nameof(CursorWriter),
            writer: this
        );
    }
}
