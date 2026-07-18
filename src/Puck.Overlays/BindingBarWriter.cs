namespace Puck.Overlays;

/// <summary>
/// The binding-bar writer: renders each seat's active-page slot cluster from an <see cref="IBindingBarSource"/>
/// snapshot as icon elements — the twelve slot chips (the proven mirrored-diamond layout) plus the modifier pips —
/// CONFINED to that seat's own normalized viewport rect, so 4-player split screen gets four correctly scaled bars
/// with the render node staying dumb. Pure record emission; no GPU types.
/// </summary>
public sealed class BindingBarWriter {
    // A viewport eased/shrunk to nothing has nowhere to draw a bar.
    private const float MinRegionExtent = 0.05f;

    private readonly IBindingBarSource m_source;
    private readonly BindingBarLayoutOptions m_layoutOptions;

    /// <summary>Initializes a new instance of the <see cref="BindingBarWriter"/> class.</summary>
    /// <param name="source">The binding-bar snapshot source.</param>
    /// <param name="layoutOptions">The layout tuning; <see langword="null"/> uses <see cref="BindingBarLayoutOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public BindingBarWriter(IBindingBarSource source, BindingBarLayoutOptions? layoutOptions = null) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_source = source;
        m_layoutOptions = (layoutOptions ?? BindingBarLayoutOptions.Default);
    }

    /// <summary>Emits this frame's per-seat bars, when a snapshot has been published.</summary>
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

    // One seat's cluster: the layout runs in the seat REGION's own space (its aspect, its bottom-center anchor,
    // every length a fraction of the region height), then maps to pixels — so a bar shrinks with its pane through
    // the split-screen ladder and a fullscreen seat draws the classic full-size cluster.
    private void EmitSeat(OverlayFrameBuilder builder, in OverlayBindingSeat seat) {
        var region = seat.Viewport;

        if ((region.Width < MinRegionExtent) || (region.Height < MinRegionExtent)) {
            return;
        }

        var regionWidthPx = (region.Width * builder.Width);
        var regionHeightPx = (region.Height * builder.Height);
        var regionOriginX = (region.X * builder.Width);
        var regionOriginY = (region.Y * builder.Height);
        var regionAspect = (regionWidthPx / regionHeightPx);
        var slots = seat.Slots.Span;

        for (var index = 0; ((index < slots.Length) && (index < BindingBarLayout.SlotButtons.Length)); index++) {
            var slot = slots[index];

            if (!slot.Visible) {
                continue;
            }

            var placement = BindingBarLayout.Place(aspect: regionAspect, index: index, options: in m_layoutOptions);

            builder.WriteIcon(
                accent: slot.Accent,
                alpha: slot.Alpha,
                bound: slot.Bound,
                centerX: (regionOriginX + (placement.Center.X * regionHeightPx)),
                centerY: (regionOriginY + (placement.Center.Y * regionHeightPx)),
                glyph: slot.Glyph,
                glyphHalf: (placement.GlyphHalfSize * regionHeightPx),
                glyphOffsetX: ((placement.GlyphCenter.X - placement.Center.X) * regionHeightPx),
                glyphOffsetY: ((placement.GlyphCenter.Y - placement.Center.Y) * regionHeightPx),
                icon: slot.Icon,
                plateHalf: (placement.HalfSize * regionHeightPx),
                pressed: slot.Pressed
            );
        }

        // The modifier pips sit between the clusters on the bar's anchor line, lit while held.
        var modifiers = seat.Modifiers.Span;

        if (modifiers.Length == 0) {
            return;
        }

        var anchor = BindingBarLayout.BarAnchor(anchorOffsetY: m_layoutOptions.AnchorOffsetY, aspect: regionAspect);
        var anchorX = (regionOriginX + (anchor.X * regionHeightPx));
        var anchorY = (regionOriginY + (anchor.Y * regionHeightPx));
        var pipHalf = ((m_layoutOptions.ButtonSize * 0.35f) * regionHeightPx);
        var pipSpacing = ((m_layoutOptions.ButtonSize * 1.1f) * regionHeightPx);

        for (var index = 0; (index < modifiers.Length); index++) {
            var modifier = modifiers[index];

            builder.WriteIcon(
                accent: false,
                alpha: (modifier.Held ? 1f : 0.35f),
                bound: true,
                centerX: (anchorX + ((index - ((modifiers.Length - 1) * 0.5f)) * pipSpacing)),
                centerY: anchorY,
                glyph: modifier.Glyph,
                glyphHalf: (pipHalf * 0.8f),
                glyphOffsetX: 0f,
                glyphOffsetY: 0f,
                icon: OverlayIconId.None,
                plateHalf: pipHalf,
                pressed: modifier.Held
            );
        }
    }
}
