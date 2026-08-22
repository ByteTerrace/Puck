using System.Numerics;
using Puck.Commands;

namespace Puck.Overlays;

/// <summary>Up to two stacked atlas glyph indices resolved for one badge or one bound-action icon — 1-based,
/// 0 = absent in either slot; <see cref="IsLabel"/> distinguishes an authored text LABEL (suppressed by the bar's
/// own text-off policy) from an authored GLYPH (a pictogram, which stays regardless of that policy). The caller
/// resolving these (a world's icon table, never this project — see <c>Puck.World.WorldIconTable</c>) is the ONLY
/// place an icon name, a font id, or a codepoint is known; everything downstream of this struct is a plain index.</summary>
public readonly record struct OverlayResolvedGlyph(ushort Glyph0, ushort Glyph1, bool IsLabel = false) {
    /// <summary>Gets the value for an unresolved (absent) badge or icon.</summary>
    public static OverlayResolvedGlyph None => default;
}
/// <summary>
/// Joins one binding-bar BANK's page against its authored slot set and the CALLER'S already-resolved icon content —
/// the pure CPU half of the binding bar a host feed calls once per seat per bank per frame before publishing an
/// <see cref="OverlayBindingSeat"/>. This project names no icon, font, or codepoint: every badge/icon glyph arrives
/// pre-resolved (see <see cref="OverlayResolvedGlyph"/>) through the resolver delegates below, which the host binds
/// against its own authored icon table. An unmapped slot still renders as the chip's DISABLED tier-0 state (a dim
/// plate with its badge), unless the resolved policy hides unbound slots, so the player sees the physical socket
/// exists and is free.
/// </summary>
public static class BindingBarSeatComposer {
    // A resolved LABEL badge is TEXT, so a text-off bar drops it outright rather than leaving the shader's bare
    // backing disc behind it; a resolved GLYPH badge is a pictogram and stays.
    private static OverlayResolvedGlyph Badge(OverlayResolvedGlyph glyph, bool text) =>
        ((text || !glyph.IsLabel)
            ? glyph
            : OverlayResolvedGlyph.None
        );
    // Matched against the row's SOURCE IDS, never its Source display label: a row binding several controls
    // ("gamepad.buttonSouth,keyboard.space") joins them into one label, so comparing that label to a single
    // control's id answers false for every multi-source row and leaves its slot drawing as unbound.
    private static BindingPageButtonView? FindButton(BindingPageView view, string source) {
        foreach (var button in view.Buttons) {
            for (var index = 0; (index < button.Sources.Count); index++) {
                if (string.Equals(
                    a: button.Sources[index],
                    b: source,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    return button;
                }
            }
        }

        return null;
    }

    /// <summary>Answers whether a physical control is currently held, from the seat's ACTIVE page view — only the
    /// active page's routed command carries a live held sample, and only the active bank draws it (a stacked wing
    /// renders a page that is not live, so it shows no press).</summary>
    /// <param name="activeView">The seat's currently active page view.</param>
    /// <param name="source">The physical control's input source id.</param>
    /// <param name="isCommandHeld">Answers whether a named command is currently carried held for this seat.</param>
    /// <returns><see langword="true"/> when the control is bound on the active page AND that binding's command is
    /// held.</returns>
    public static bool IsPhysicallyPressed(BindingPageView activeView, string source, Func<string, bool> isCommandHeld) {
        ArgumentNullException.ThrowIfNull(argument: activeView);
        ArgumentNullException.ThrowIfNull(argument: isCommandHeld);

        var binding = FindButton(
            view: activeView,
            source: source
        );

        return ((binding is not null) && isCommandHeld(binding.Command));
    }
    /// <summary>Composes the bar's modifier indicators from a page view (the active page's chord IS the held modifier
    /// sequence, so <see cref="BindingModifierView.Required"/> doubles as "held right now").</summary>
    /// <param name="view">The seat's active page view.</param>
    /// <param name="text">Whether the bar draws its atlas text; <see langword="false"/> leaves each modifier a bare
    /// plate (every modifier badge resolves from a text LABEL).</param>
    /// <param name="resolveBadge">Resolves a modifier's input source id to its badge content — the caller's own icon
    /// table, the SAME door a slot's badge resolves through.</param>
    /// <param name="destination">The destination modifiers; at least <c>view.Modifiers.Count</c> entries.</param>
    /// <returns>The number of modifiers written.</returns>
    public static int ComposeModifiers(BindingPageView view, bool text, Func<string, OverlayResolvedGlyph> resolveBadge, Span<OverlayBindingModifier> destination) {
        ArgumentNullException.ThrowIfNull(argument: view);
        ArgumentNullException.ThrowIfNull(argument: resolveBadge);

        var count = Math.Min(
            val1: view.Modifiers.Count,
            val2: destination.Length
        );

        for (var index = 0; (index < count); index++) {
            var modifier = view.Modifiers[index];
            var badge = Badge(
                glyph: resolveBadge(modifier.Sources[0]),
                text: text
            );

            destination[index] = new OverlayBindingModifier(
                BadgeGlyph0: badge.Glyph0,
                BadgeGlyph1: badge.Glyph1,
                Held: modifier.Required
            );
        }

        return count;
    }
    /// <summary>Composes one BANK's slots from its own resolved page view and authored slot set.</summary>
    /// <param name="view">The bank's own resolved page view (never necessarily the seat's ACTIVE page).</param>
    /// <param name="slotSet">The authored physical controls this bar shows, by input source id, in authored order —
    /// an exotic slot's left-to-right position in its row follows this order.</param>
    /// <param name="resolveBadge">Resolves a physical control's input source id (already family-resolved by the
    /// caller) to its badge content — the caller's own icon table.</param>
    /// <param name="resolveIcon">Resolves a bound row's presentation KEY (its id, else its action name) to its
    /// plate-icon content; a <see langword="null"/>/unresolved string yields <see cref="OverlayResolvedGlyph.None"/>
    /// (a blank plate, never a placeholder mark).</param>
    /// <param name="isCommandHeld">Answers whether a named command is currently carried held for this seat — the
    /// latched-toggle door, consulted for every bank's toggle rows; <see langword="null"/> lights no toggles.</param>
    /// <param name="isPressed">Answers whether a physical control is currently held this frame — the SAME physical
    /// fact across every bank (a control's live press state does not depend on which page is being shown for it);
    /// <see langword="null"/> renders every chip unpressed (an input-stateless feed).</param>
    /// <param name="bankAlpha">This bank's resolved opacity multiplier (its authored alpha or active-alpha). An
    /// unbound slot's extra dim is the writer's, from the live theme — not folded in here.</param>
    /// <param name="text">Whether the bar draws its atlas text; <see langword="false"/> drops every badge whose
    /// content resolves from a text LABEL and keeps every badge that resolves from a GLYPH.</param>
    /// <param name="hideUnbound">Whether a slot with no bound act on this bank's page should not render at all,
    /// rather than drawing the DISABLED tier-0 plate.</param>
    /// <param name="bankOrder">This bank's declared stack order, carried on every slot so the writer can derive the
    /// bank's displacement without a second lookup.</param>
    /// <param name="bankOffsetOverride">This bank's authored displacement override (region-height units, y-down), or
    /// <see langword="null"/> to take the derived arrangement.</param>
    /// <param name="destination">The destination slots; exactly <paramref name="slotSet"/>.Length entries.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> does not match <paramref name="slotSet"/>
    /// in length.</exception>
    public static void ComposeBank(BindingPageView view, ReadOnlySpan<string> slotSet, Func<string, OverlayResolvedGlyph> resolveBadge, Func<string?, OverlayResolvedGlyph> resolveIcon, Func<string, bool>? isPressed, Func<string, bool>? isCommandHeld, float bankAlpha, bool text, bool hideUnbound, int bankOrder, Vector2? bankOffsetOverride, Span<OverlayBindingSlot> destination) {
        ArgumentNullException.ThrowIfNull(argument: view);
        ArgumentNullException.ThrowIfNull(argument: resolveBadge);
        ArgumentNullException.ThrowIfNull(argument: resolveIcon);

        if (destination.Length != slotSet.Length) {
            throw new ArgumentException(
                message: $"Expected {slotSet.Length} slots; got {destination.Length}.",
                paramName: nameof(destination)
            );
        }

        var exoticCount = 0;

        foreach (var slotSource in slotSet) {
            if (BindingBarLayout.Categorize(
                source: slotSource,
                classicIndex: out _
            ) == BindingSlotCategory.Exotic) {
                exoticCount++;
            }
        }

        var exoticSeen = 0;

        for (var index = 0; (index < slotSet.Length); index++) {
            var source = slotSet[index];
            var category = BindingBarLayout.Categorize(
                source: source,
                classicIndex: out var classicIndex
            );
            var categoryIndex = category switch {
                BindingSlotCategory.Classic => classicIndex,
                BindingSlotCategory.Center => Array.IndexOf(array: BindingBarLayout.CenterSources, value: source),
                _ => exoticSeen++,
            };
            var glyph = Badge(
                glyph: resolveBadge(source),
                text: text
            );
            var binding = FindButton(
                view: view,
                source: source
            );
            // A momentary press lights only on the live bank (isPressed is null for a wing). A LATCHED toggle is a
            // fact about the seat, not the live page: it lights on every bank that binds it, judged against THIS
            // bank's own row (the wing's L3 may latch a different channel than the base's), faded with the bank.
            var livePress = ((isPressed is not null) && isPressed(source));
            var latched = ((binding is { Toggle: true }) && (isCommandHeld is not null) && isCommandHeld(binding.Command));
            var pressed = (livePress || latched);
            var icon = ((binding is null)
                ? OverlayResolvedGlyph.None
                : resolveIcon(binding.Key)
            );

            destination[index] = new OverlayBindingSlot(
                Alpha: bankAlpha,
                BadgeGlyph0: glyph.Glyph0,
                BadgeGlyph1: glyph.Glyph1,
                BankOffsetOverride: bankOffsetOverride,
                BankOrder: bankOrder,
                Bound: (binding is not null),
                Category: category,
                Latched: (latched && (isPressed is null)),
                Toggled: latched,
                CategoryCount: exoticCount,
                CategoryIndex: categoryIndex,
                IconGlyph0: icon.Glyph0,
                IconGlyph1: icon.Glyph1,
                Pressed: pressed,
                Visible: ((binding is not null) || !hideUnbound)
            );
        }
    }
}
