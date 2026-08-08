namespace Puck.Overlays;

/// <summary>
/// The binding-bar writer: renders each seat's active-page slot cluster from an <see cref="IBindingBarSource"/>
/// snapshot as icon elements — the twelve slot chips (the mirrored-diamond layout), the modifier pips, and the
/// active page's name —
/// CONFINED to that seat's own normalized viewport rect, so 4-player split screen gets four correctly scaled bars
/// with the render node staying dumb. Pure record emission; no GPU types.
/// </summary>
public sealed class BindingBarWriter : IOverlaySeatEmitter<OverlayBindingSeat> {
    /// <summary>The chord-hint lines one seat's bar draws. A page with more command-chord rows than this shows the
    /// first <see cref="MaxHintLines"/> and the rest are refused at the bar's own channel boundary, attributed.</summary>
    public const int MaxHintLines = 8;
    /// <summary>The character clamp on every text run the bar writes (the page label and the hint lines alike) —
    /// the editor HUD's line clamp, shared so the two text surfaces read at one width.</summary>
    public const int MaxLineChars = 46;
    /// <summary>The modifier pips one seat's bar draws.</summary>
    public const int MaxModifierPips = 8;

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
    /// <exception cref="InvalidOperationException">The published frame carries more seats than
    /// <see cref="OverlayChannelLeases.MaxSeats"/> provisions for.</exception>
    public void Emit(OverlayFrameBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (!m_source.TrySnapshot(frame: out var frame)) {
            return;
        }

        var seats = frame.Seats.Span;

        OverlaySeatLoop.Emit(builder: builder, seats: seats, writerName: nameof(BindingBarWriter), writer: this);
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
        var anchor = BindingBarLayout.BarAnchor(anchorOffsetY: m_layoutOptions.AnchorOffsetY, aspect: regionAspect);
        var anchorX = (regionOriginX + (anchor.X * regionHeightPx));
        var anchorY = (regionOriginY + (anchor.Y * regionHeightPx));
        var pipHalf = ((m_layoutOptions.ButtonSize * 0.35f) * regionHeightPx);
        var pipSpacing = ((m_layoutOptions.ButtonSize * 1.1f) * regionHeightPx);
        // The page NAME rides directly under the pips — the visible half of the page model: squeeze a trigger chord
        // and the bar both re-renders AND says which page it turned to, so a sparse page still reads.
        var labelCell = Math.Max(val1: 12, val2: (int)(pipHalf * 1.9f));

        if (!string.IsNullOrEmpty(value: seat.Label)) {
            var labelChars = Math.Min(val1: seat.Label.Length, val2: MaxLineChars);

            builder.WriteText(
                alpha: 0.9f,
                cellHeight: labelCell,
                maxChars: MaxLineChars,
                role: OverlayColorRole.TextPrimary,
                text: seat.Label,
                x: (anchorX - (builder.TextWidth(chars: labelChars, cellHeight: labelCell) * 0.5f)),
                y: (anchorY + (pipHalf * 1.4f))
            );
        }

        if (modifiers.Length == 0) {
            return;
        }

        var pipCount = Math.Min(val1: modifiers.Length, val2: MaxModifierPips);

        // The pip cap is the SAME kind of self-declared truncation as the hint-line cap below it: attribute it the
        // same way (NoteRefused) rather than letting it clip silently at a smaller grain than the row cap does.
        if (pipCount < modifiers.Length) {
            builder.NoteRefused(elements: (modifiers.Length - pipCount), textWords: 0);
        }

        for (var index = 0; (index < pipCount); index++) {
            var modifier = modifiers[index];

            builder.WriteIcon(
                accent: false,
                alpha: (modifier.Held ? 1f : 0.35f),
                bound: true,
                centerX: (anchorX + ((index - ((pipCount - 1) * 0.5f)) * pipSpacing)),
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

        // The chord hints stack above the pips: one small centered line per command-chord row of the active group
        // (ASCII only — the glyph pack is ASCII-95), quiet alpha so the bar's chips stay dominant.
        var hints = seat.Hints.Span;

        if (hints.Length == 0) {
            return;
        }

        var hintCell = Math.Max(val1: 10, val2: (int)(pipHalf * 1.6f));
        var hintLineStep = (hintCell * 1.3f);
        var hintBaseY = (anchorY - (pipHalf * 2.2f));
        // A DELIBERATE BEHAVIOR CHANGE, and the reason it is one: the hint tail used to be unbounded here, so a page
        // with many command-chord rows lost its overflow SILENTLY at the shared record pool's boundary, by draw-order
        // accident — whichever writer happened to run last paid for it. The cap replaces that accident with a pinned
        // truncation the bar's own channel reports: the first MaxHintLines rows draw, the rest are refused at the
        // bar's reservation and attributed to the bar. Boundedness is what makes the reservation meaningful; an
        // unbounded writer cannot be carved by any scheme.
        var hintCount = Math.Min(val1: hints.Length, val2: MaxHintLines);

        if (hintCount < hints.Length) {
            var refusedWords = 0;

            for (var index = hintCount; (index < hints.Length); index++) {
                refusedWords += Math.Min(val1: hints[index].Length, val2: MaxLineChars);
            }

            builder.NoteRefused(elements: (hints.Length - hintCount), textWords: refusedWords);
        }

        for (var index = 0; (index < hintCount); index++) {
            var hint = hints[index];

            if (string.IsNullOrEmpty(value: hint)) {
                continue;
            }

            var hintChars = Math.Min(val1: hint.Length, val2: MaxLineChars);

            builder.WriteText(
                alpha: 0.6f,
                cellHeight: hintCell,
                maxChars: MaxLineChars,
                role: OverlayColorRole.TextDim,
                text: hint,
                x: (anchorX - (builder.TextWidth(chars: hintChars, cellHeight: hintCell) * 0.5f)),
                y: (hintBaseY - ((hintCount - 1 - index) * hintLineStep) - hintCell)
            );
        }
    }

    void IOverlaySeatEmitter<OverlayBindingSeat>.EmitSeat(OverlayFrameBuilder builder, in OverlayBindingSeat seat) =>
        EmitSeat(builder: builder, seat: in seat);
}
