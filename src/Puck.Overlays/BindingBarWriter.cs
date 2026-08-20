namespace Puck.Overlays;

/// <summary>
/// The binding-bar writer: renders each seat's authored banks from an <see cref="IBindingBarSource"/> snapshot as
/// icon elements — every bank's slot cluster (the mirrored-diamond layout plus the menu-trio and exotics rows, each
/// bank displaced by its own authored offset), the modifier indicators, and the active page's name — CONFINED to that
/// seat's own normalized viewport rect, so 4-player split screen gets four correctly scaled bars with the render
/// node staying dumb. Pure record emission; no GPU types.
/// </summary>
public sealed class BindingBarWriter : IOverlaySeatEmitter<OverlayBindingSeat> {
    // A viewport eased/shrunk to nothing has nowhere to draw a bar.
    private const float MinRegionExtent = 0.05f;

    /// <summary>The chord-hint lines one seat's bar draws. A page with more command-chord rows than this shows the
    /// first <see cref="MaxHintLines"/> and the rest are refused at the bar's own channel boundary, attributed.</summary>
    public const int MaxHintLines = 8;
    /// <summary>The character clamp on every text run the bar writes (the page label and the hint lines alike).</summary>
    public const int MaxLineChars = 46;

    private readonly IBindingBarSource m_source;
    private readonly OverlayThemeStore m_theme;

    /// <summary>Initializes a new instance of the <see cref="BindingBarWriter"/> class.</summary>
    /// <param name="source">The binding-bar snapshot source.</param>
    /// <param name="theme">The live resolved theme.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="theme"/> is
    /// <see langword="null"/>.</exception>
    public BindingBarWriter(IBindingBarSource source, OverlayThemeStore theme) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: theme);

        m_source = source;
        m_theme = theme;
    }

    void IOverlaySeatEmitter<OverlayBindingSeat>.EmitSeat(OverlayFrameBuilder builder, in OverlayBindingSeat seat) =>
        EmitSeat(
            builder: builder,
            seat: in seat
        );

    // One seat's cluster: the layout runs in the seat REGION's own space (its aspect, its bottom-center anchor,
    // every length a fraction of the region height), then maps to pixels — so a bar shrinks with its pane through
    // the split-screen ladder and a fullscreen seat draws the classic full-size cluster.
    private void EmitSeat(OverlayFrameBuilder builder, in OverlayBindingSeat seat) {
        var region = seat.Viewport;

        if (
            !seat.Visible ||
            (region.Width < MinRegionExtent) ||
            (region.Height < MinRegionExtent)
        ) {
            return;
        }

        var layout = seat.Layout;
        var chrome = m_theme.Current.Chrome;
        var space = m_theme.Current.Space;
        var scaledButtonSize = (layout.ButtonSize * layout.Scale);
        var regionWidthPx = (region.Width * builder.Width);
        var regionHeightPx = (region.Height * builder.Height);
        var regionOriginX = (region.X * builder.Width);
        var regionOriginY = (region.Y * builder.Height);
        var regionAspect = (regionWidthPx / regionHeightPx);
        var slots = seat.Slots.Span;

        for (var index = 0; (index < slots.Length); index++) {
            var slot = slots[index];

            if (!slot.Visible) {
                continue;
            }

            var placement = BindingBarLayout.Place(
                aspect: regionAspect,
                category: slot.Category,
                categoryCount: slot.CategoryCount,
                categoryIndex: slot.CategoryIndex,
                options: in layout
            );
            var derived = BindingBarLayout.BankOffset(
                buttonSize: scaledButtonSize,
                order: slot.BankOrder,
                space: in space
            );
            var bankOffset = (slot.BankOffsetOverride ?? derived);
            var centerX = ((regionOriginX + (placement.Center.X * regionHeightPx)) + (bankOffset.X * regionHeightPx));
            var centerY = ((regionOriginY + (placement.Center.Y * regionHeightPx)) + (bankOffset.Y * regionHeightPx));

            builder.WriteIcon(
                accent: slot.Accent,
                alpha: (slot.Bound
                ? slot.Alpha
                : (slot.Alpha * chrome.DimQuietAlpha)),
                badgeGlyph0: slot.BadgeGlyph0,
                badgeGlyph1: slot.BadgeGlyph1,
                bound: slot.Bound,
                centerX: centerX,
                centerY: centerY,
                glyphHalf: (placement.GlyphHalfSize * regionHeightPx),
                glyphOffsetX: ((placement.GlyphCenter.X - placement.Center.X) * regionHeightPx),
                glyphOffsetY: ((placement.GlyphCenter.Y - placement.Center.Y) * regionHeightPx),
                iconGlyph0: slot.IconGlyph0,
                iconGlyph1: slot.IconGlyph1,
                plateHalf: (placement.HalfSize * regionHeightPx),
                pressed: slot.Pressed
            );
        }

        // The modifier indicators sit between the clusters on the bar's anchor line, lit while held.
        var modifiers = seat.Modifiers.Span;
        var anchor = BindingBarLayout.BarAnchor(
            anchorOffsetY: layout.AnchorOffsetY,
            aspect: regionAspect
        );
        var anchorX = (regionOriginX + (anchor.X * regionHeightPx));
        var anchorY = (regionOriginY + (anchor.Y * regionHeightPx));
        var modifierHalf = ((scaledButtonSize * layout.ModifierHalfRatio) * regionHeightPx);
        var modifierSpacing = ((scaledButtonSize * layout.ModifierSpacingRatio) * regionHeightPx);
        // The page NAME rides directly under the modifiers — the visible half of the page model: squeeze a trigger chord
        // and the bar both re-renders AND says which page it turned to, so a sparse page still reads.
        var labelCell = Math.Max(
            val1: ((int)layout.LabelCellMinPx),
            val2: ((int)(modifierHalf * layout.LabelCellRatio))
        );

        if (!string.IsNullOrEmpty(value: seat.Label)) {
            var labelChars = Math.Min(
                val1: seat.Label.Length,
                val2: MaxLineChars
            );

            builder.WriteText(
                alpha: chrome.BarLabelAlpha,
                cellHeight: labelCell,
                maxChars: MaxLineChars,
                role: OverlayColorRole.TextPrimary,
                text: seat.Label,
                x: (anchorX - (builder.TextWidth(
                    cellHeight: labelCell,
                    chars: labelChars
                ) * 0.5f)),
                y: (anchorY + (modifierHalf * layout.LabelGapRatio))
            );
        }

        if (modifiers.Length == 0) {
            return;
        }

        // The producer bounds the count: the feed's per-seat modifier array and this channel's lease reservation are
        // both sized from the document contract's modifier ceiling (WorldBindingBarCapacity.MaxModifiers, crossed as
        // OverlayCapacity.BindingBarMaxModifiers), which the document validator also refuses a composed profile past —
        // so every published modifier has a reserved record and the bar carries no private cap of its own.
        var modifierCount = modifiers.Length;

        for (var index = 0; (index < modifierCount); index++) {
            var modifier = modifiers[index];

            builder.WriteIcon(
                accent: false,
                alpha: (modifier.Held
                ? 1f
                : chrome.DimQuietAlpha),
                badgeGlyph0: modifier.BadgeGlyph0,
                badgeGlyph1: modifier.BadgeGlyph1,
                bound: true,
                centerX: (anchorX + ((index - ((modifierCount - 1) * 0.5f)) * modifierSpacing)),
                centerY: anchorY,
                glyphHalf: (modifierHalf * layout.ModifierGlyphRatio),
                glyphOffsetX: 0f,
                glyphOffsetY: 0f,
                iconGlyph0: 0,
                iconGlyph1: 0,
                plateHalf: modifierHalf,
                pressed: modifier.Held
            );
        }

        // The chord hints stack above the modifiers: one small centered line per command-chord row of the active group
        // (ASCII only — the glyph pack is ASCII-95), quiet alpha so the bar's chips stay dominant.
        var hints = seat.Hints.Span;

        if (hints.Length == 0) {
            return;
        }

        var hintCell = Math.Max(
            val1: ((int)layout.HintCellMinPx),
            val2: ((int)(modifierHalf * layout.HintCellRatio))
        );
        var hintLineStep = (hintCell * layout.HintLineStepRatio);
        var hintBaseY = (anchorY - (modifierHalf * layout.HintBaseGapRatio));
        // Bounded and pinned: a page with many command-chord rows would otherwise lose its overflow silently at the
        // shared record pool's boundary, by draw-order accident. The first MaxHintLines rows draw; the rest are
        // refused at the bar's reservation and attributed to the bar — boundedness is what makes the reservation
        // meaningful, since an unbounded writer cannot be carved by any scheme.
        var hintCount = Math.Min(
            val1: hints.Length,
            val2: MaxHintLines
        );

        if (hintCount < hints.Length) {
            var refusedWords = 0;

            for (var index = hintCount; (index < hints.Length); index++) {
                refusedWords += Math.Min(
                    val1: hints[index].Length,
                    val2: MaxLineChars
                );
            }

            builder.NoteRefused(
                elements: (hints.Length - hintCount),
                textWords: refusedWords
            );
        }

        for (var index = 0; (index < hintCount); index++) {
            var hint = hints[index];

            if (string.IsNullOrEmpty(value: hint)) {
                continue;
            }

            var hintChars = Math.Min(
                val1: hint.Length,
                val2: MaxLineChars
            );

            builder.WriteText(
                alpha: chrome.BarHintAlpha,
                cellHeight: hintCell,
                maxChars: MaxLineChars,
                role: OverlayColorRole.TextDim,
                text: hint,
                x: (anchorX - (builder.TextWidth(
                    cellHeight: hintCell,
                    chars: hintChars
                ) * 0.5f)),
                y: ((hintBaseY - (((hintCount - 1) - index) * hintLineStep)) - hintCell)
            );
        }
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

        OverlaySeatLoop.Emit(
            builder: builder,
            seats: seats,
            writerName: nameof(BindingBarWriter),
            writer: this
        );
    }
}
