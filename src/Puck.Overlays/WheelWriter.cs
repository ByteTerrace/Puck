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
    // The same emptied-viewport guard the cursor writer applies before opening a clip scope on the region.
    private const float MinRegionExtent = 0.05f;

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
    /// <summary>The half-width, px, trimmed off each angular edge of a piece so neighbours read as separate pieces.</summary>
    public const float SectorGapPx = 2f;
    /// <summary>A sector's icon chip half-extent as a fraction of the ring width.</summary>
    public const float ChipHalfRatio = 0.3f;

    private readonly OverlayThemeStore m_theme;
    private readonly IWheelSource m_source;

    /// <summary>Initializes a new instance of the <see cref="WheelWriter"/> class.</summary>
    /// <param name="source">The wheel snapshot source.</param>
    /// <param name="theme">The live resolved theme.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="theme"/> is
    /// <see langword="null"/>.</exception>
    public WheelWriter(IWheelSource source, OverlayThemeStore theme) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: theme);

        m_source = source;
        m_theme = theme;
    }

    void IOverlaySeatEmitter<OverlayWheelSeat>.EmitSeat(OverlayFrameBuilder builder, in OverlayWheelSeat seat) =>
        EmitSeat(
            builder: builder,
            seat: in seat
        );

    private void EmitSeat(OverlayFrameBuilder builder, in OverlayWheelSeat seat) {
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

        var theme = m_theme.Current;
        var chrome = theme.Chrome;
        var cellHeight = OverlayFrameBuilder.CellHeight(sizePx: theme.Type.MicroSize);

        builder.BeginClip(
            h: (region.Height * builder.Height),
            w: (region.Width * builder.Width),
            x: (region.X * builder.Width),
            y: (region.Y * builder.Height)
        );

        // The pie: a quiet hub disc, then every ring's sectors as filled pieces separated by a constant pixel gap,
        // each carrying its action's icon chip at the piece's centroid. The hovered piece and its chip lift to the
        // accent tier; an inactive ring's pieces sit at the quiet ring alpha. The hub carries the hovered sector's
        // text (what a commit will do) over the active ring's label (which shell is live) — the two facts a player
        // reads without hunting, and the only text the wheel draws.
        // The hub overlaps the first ring's inner edge by a pixel so two anti-aliased fills meeting there show no
        // seam: the pieces' angular gaps are the only seams the pie shows.
        var hubRadius = (seat.InnerRadius + 1f);

        // After a commit or cancel the whole wheel stays, with the verdict glowing on it — the accepted piece
        // positive, the refused piece or the hub danger — and fades out on the authored curve (seat.Fade). While
        // open, Fade is 1 and the glow is the hover accent.
        var fade = seat.Fade;
        var verdictOnHub = ((seat.Outcome is OverlayWheelOutcome.Cancelled or OverlayWheelOutcome.Errored) && (seat.OutcomeSector < 0));

        var hoveredLabel = seat.HubLabel.AsSpan();

        for (var ringIndex = 0; (ringIndex < ringCount); ringIndex++) {
            var isActive = (ringIndex == seat.ActiveRing);
            var innerRadius = (seat.InnerRadius + (ringIndex * seat.RingWidth));
            var outerRadius = (innerRadius + seat.RingWidth);
            var centroid = (innerRadius + (seat.RingWidth * 0.5f));
            var ringAlpha = ((isActive
                ? chrome.WheelActiveRingAlpha
                : chrome.WheelRingAlpha) * fade);
            var sectors = rings[ringIndex].Sectors.Span;
            var sectorCount = Math.Min(
                val1: sectors.Length,
                val2: MaxSectorsPerRing
            );

            if (sectorCount == 0) {
                continue;
            }

            var span = (MathF.Tau / sectorCount);
            var direction = (seat.Clockwise
                ? 1f
                : -1f
            );

            for (var sectorIndex = 0; (sectorIndex < sectorCount); sectorIndex++) {
                // Layout policy is authored with the radial. The host uses the identical transform for selection:
                // sector N is CENTERED on its angle, so the piece starts half a span before it.
                var angle = (seat.RotationRadians + ((direction * sectorIndex) * span));
                var sector = sectors[sectorIndex];
                var isHovered = (isActive && (sectorIndex == seat.HoveredSector));
                var isOutcome = (isActive && (sectorIndex == seat.OutcomeSector));
                // Selection and verdict are a GLOW on the piece's edge, never a different fill: the piece itself,
                // and whatever the world shows beneath it, stay exactly as they were.
                var glow = ((isOutcome && (seat.Outcome == OverlayWheelOutcome.Accepted))
                    ? OverlayColorRole.Positive
                    : ((isOutcome && (seat.Outcome == OverlayWheelOutcome.Errored))
                        ? OverlayColorRole.Danger
                        : (isHovered
                            ? OverlayColorRole.Accent
                            : (OverlayColorRole?)null))
                );

                builder.WriteWedge(
                    alpha: ringAlpha,
                    centerX: seat.CenterX,
                    centerY: seat.CenterY,
                    gap: SectorGapPx,
                    glow: glow,
                    innerRadius: innerRadius,
                    outerRadius: outerRadius,
                    role: OverlayColorRole.ScrimPanel,
                    startAngle: (angle - (span * 0.5f)),
                    sweep: span
                );

                var chipX = (seat.CenterX + (MathF.Sin(x: angle) * centroid));
                var chipY = (seat.CenterY - (MathF.Cos(x: angle) * centroid));

                if (sector.Icon.Glyph0 != 0) {
                    // The action's icon chip: the same plate the binding bar draws, with no physical-button badge
                    // (the piece's position IS its selector), lifted to the accent tier while hovered — and, once
                    // a verdict lands on this piece, blooming in the verdict's hue instead.
                    builder.WriteIcon(
                        accent: (isHovered || (glow is { } chipGlow && chipGlow != OverlayColorRole.Accent)),
                        accentRole: ((glow is { } chipRole && chipRole != OverlayColorRole.Accent)
                            ? chipRole
                            : null),
                        alpha: ringAlpha,
                        badgeGlyph0: 0,
                        badgeGlyph1: 0,
                        bound: true,
                        centerX: chipX,
                        centerY: chipY,
                        glyphHalf: 0f,
                        glyphOffsetX: 0f,
                        glyphOffsetY: 0f,
                        iconGlyph0: sector.Icon.Glyph0,
                        iconGlyph1: sector.Icon.Glyph1,
                        plateHalf: (seat.RingWidth * ChipHalfRatio),
                        pressed: false
                    );
                } else {
                    // No icon resolved: the piece carries its text instead, so an un-iconed sector still reads.
                    var label = sector.Label;
                    var width = builder.TextWidth(
                        chars: Math.Min(
                            val1: label.Length,
                            val2: MaxSectorLabelChars
                        ),
                        cellHeight: cellHeight
                    );

                    builder.WriteText(
                        alpha: ringAlpha,
                        cellHeight: cellHeight,
                        maxChars: MaxSectorLabelChars,
                        role: (isHovered
                        ? OverlayColorRole.Accent
                        : OverlayColorRole.TextPrimary),
                        text: label,
                        x: (chipX - (width * 0.5f)),
                        y: (chipY - (cellHeight * 0.5f))
                    );
                }

                if (isHovered) {
                    hoveredLabel = sector.Label;
                }
            }
        }

        // The hub is the cancel target: it glows accent while nothing is hovered (releasing now cancels) and goes
        // quiet the moment a piece takes the selection. Drawn AFTER the pieces: its halo falls outward onto the
        // first ring, and a halo painted before the pieces' fills would be buried under them.
        builder.WriteWedge(
            alpha: (chrome.WheelActiveRingAlpha * fade),
            centerX: seat.CenterX,
            centerY: seat.CenterY,
            gap: 0f,
            glow: (verdictOnHub
                ? OverlayColorRole.Danger
                : ((seat.Outcome == OverlayWheelOutcome.None) && (seat.HoveredSector < 0))
                    ? OverlayColorRole.Accent
                    : null),
            innerRadius: 0f,
            outerRadius: hubRadius,
            role: OverlayColorRole.ScrimPanel,
            startAngle: 0f,
            sweep: MathF.Tau
        );

        // The hub text: the hovered sector's name (what a commit does), then the active ring's label beneath it.
        var hubTextY = (seat.CenterY - (cellHeight * 0.5f));

        if (hoveredLabel.Length > 0) {
            var hoveredWidth = builder.TextWidth(
                chars: Math.Min(
                    val1: hoveredLabel.Length,
                    val2: MaxSectorLabelChars
                ),
                cellHeight: cellHeight
            );

            builder.WriteText(
                alpha: (chrome.WheelLabelAlpha * fade),
                cellHeight: cellHeight,
                maxChars: MaxSectorLabelChars,
                role: OverlayColorRole.TextPrimary,
                text: hoveredLabel,
                x: (seat.CenterX - (hoveredWidth * 0.5f)),
                y: hubTextY
            );
            hubTextY += (cellHeight + chrome.WheelHubLabelGap);
        }

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
                alpha: (chrome.WheelRingAlpha * fade),
                cellHeight: cellHeight,
                maxChars: MaxRingLabelChars,
                role: OverlayColorRole.TextDim,
                text: activeLabel,
                x: (seat.CenterX - (activeWidth * 0.5f)),
                y: hubTextY
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
