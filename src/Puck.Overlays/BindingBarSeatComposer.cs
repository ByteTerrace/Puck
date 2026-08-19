using System.Numerics;
using Puck.Commands;
using Puck.Input;
using Puck.Input.Devices;

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
    /// <summary>Resolves a physical button to its provider-neutral input source id — the whole
    /// <see cref="GamepadButtons"/> flag set (KEEP IN SYNC with it and with <see cref="InputSources.Gamepad"/>).</summary>
    /// <param name="button">The physical button (one flag).</param>
    /// <returns>The source id, or <see langword="null"/> for an undeclared flag.</returns>
    public static string? SourceOf(GamepadButtons button) => button switch {
        GamepadButtons.ButtonSouth => InputSources.Gamepad.ButtonSouth,
        GamepadButtons.ButtonEast => InputSources.Gamepad.ButtonEast,
        GamepadButtons.ButtonWest => InputSources.Gamepad.ButtonWest,
        GamepadButtons.ButtonNorth => InputSources.Gamepad.ButtonNorth,
        GamepadButtons.DpadUp => InputSources.Gamepad.DpadUp,
        GamepadButtons.DpadDown => InputSources.Gamepad.DpadDown,
        GamepadButtons.DpadLeft => InputSources.Gamepad.DpadLeft,
        GamepadButtons.DpadRight => InputSources.Gamepad.DpadRight,
        GamepadButtons.LeftShoulder => InputSources.Gamepad.LeftShoulder,
        GamepadButtons.RightShoulder => InputSources.Gamepad.RightShoulder,
        GamepadButtons.LeftStickPress => InputSources.Gamepad.LeftStickPress,
        GamepadButtons.RightStickPress => InputSources.Gamepad.RightStickPress,
        GamepadButtons.Back => InputSources.Gamepad.Back,
        GamepadButtons.Start => InputSources.Gamepad.Start,
        GamepadButtons.Guide => InputSources.Gamepad.Guide,
        GamepadButtons.Touchpad => InputSources.Gamepad.Touchpad,
        GamepadButtons.Mute => InputSources.Gamepad.Mute,
        GamepadButtons.LeftGrip => InputSources.Gamepad.LeftGrip,
        GamepadButtons.RightGrip => InputSources.Gamepad.RightGrip,
        GamepadButtons.LeftUpperGrip => InputSources.Gamepad.LeftUpperGrip,
        GamepadButtons.RightUpperGrip => InputSources.Gamepad.RightUpperGrip,
        GamepadButtons.QuickAccess => InputSources.Gamepad.QuickAccess,
        GamepadButtons.TouchpadLeft => InputSources.Gamepad.TouchpadLeft,
        _ => null,
    };

    // A resolved LABEL badge is TEXT, so a text-off bar drops it outright rather than leaving the shader's bare
    // backing disc behind it; a resolved GLYPH badge is a pictogram and stays.
    private static OverlayResolvedGlyph Badge(OverlayResolvedGlyph glyph, bool text) =>
        ((text || !glyph.IsLabel)
            ? glyph
            : OverlayResolvedGlyph.None
        );
    private static BindingPageButtonView? FindButton(BindingPageView view, string source) {
        foreach (var button in view.Buttons) {
            if (string.Equals(
                a: button.Source,
                b: source,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                return button;
            }
        }

        return null;
    }

    /// <summary>Answers whether a physical button is currently held, from the seat's ACTIVE page view — the ONE
    /// physical-press fact every bank's rendering of that button's slot reuses: a button's momentary press state
    /// does not depend on which bank/page is drawing it, and only the active page's routed command carries a live
    /// held sample.</summary>
    /// <param name="activeView">The seat's currently active page view.</param>
    /// <param name="button">The physical button to test.</param>
    /// <param name="isCommandHeld">Answers whether a named command is currently carried held for this seat.</param>
    /// <returns><see langword="true"/> when the button is bound on the active page AND that binding's command is
    /// held.</returns>
    public static bool IsPhysicallyPressed(BindingPageView activeView, GamepadButtons button, Func<string, bool> isCommandHeld) {
        ArgumentNullException.ThrowIfNull(argument: activeView);
        ArgumentNullException.ThrowIfNull(argument: isCommandHeld);

        if (SourceOf(button: button) is not { } source) {
            return false;
        }

        var binding = FindButton(
            view: activeView,
            source: source
        );

        return ((binding is not null) && isCommandHeld(binding.Command));
    }
    /// <summary>Composes the bar's modifier pips from a page view (the active page's chord IS the held modifier
    /// sequence, so <see cref="BindingModifierView.Required"/> doubles as "held right now").</summary>
    /// <param name="view">The seat's active page view.</param>
    /// <param name="text">Whether the bar draws its atlas text; <see langword="false"/> leaves each pip a bare plate
    /// (every modifier badge resolves from a text LABEL).</param>
    /// <param name="resolveModifierSource">Resolves a modifier's provider-neutral input source id to its badge
    /// content — the caller's own icon table.</param>
    /// <param name="destination">The destination pips; at least <c>view.Modifiers.Count</c> entries.</param>
    /// <returns>The number of pips written.</returns>
    public static int ComposeModifiers(BindingPageView view, bool text, Func<string, OverlayResolvedGlyph> resolveModifierSource, Span<OverlayBindingModifier> destination) {
        ArgumentNullException.ThrowIfNull(argument: view);
        ArgumentNullException.ThrowIfNull(argument: resolveModifierSource);

        var count = Math.Min(
            val1: view.Modifiers.Count,
            val2: destination.Length
        );

        for (var index = 0; (index < count); index++) {
            var modifier = view.Modifiers[index];
            var badge = Badge(
                glyph: resolveModifierSource(modifier.Sources[0]),
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
    /// <param name="slotSet">The authored physical buttons this bar shows, in authored order — an exotic button's
    /// left-to-right position in its row follows this order.</param>
    /// <param name="resolveBadge">Resolves a physical button (already family-resolved by the caller) to its badge
    /// content — the caller's own icon table.</param>
    /// <param name="resolveIcon">Resolves a bound action's opaque icon string (e.g. <c>action.jump</c>) to its
    /// plate-icon content; a <see langword="null"/>/unresolved string yields <see cref="OverlayResolvedGlyph.None"/>
    /// (a blank plate, never a placeholder mark).</param>
    /// <param name="isPressed">Answers whether a physical button is currently held this frame — the SAME physical
    /// fact across every bank (a button's live press state does not depend on which page is being shown for it);
    /// <see langword="null"/> renders every chip unpressed (an input-stateless feed).</param>
    /// <param name="bankAlpha">This bank's resolved opacity multiplier (its authored alpha or active-alpha).</param>
    /// <param name="text">Whether the bar draws its atlas text; <see langword="false"/> drops every badge whose
    /// content resolves from a text LABEL and keeps every badge that resolves from a GLYPH.</param>
    /// <param name="hideUnbound">Whether a slot with no bound act on this bank's page should not render at all,
    /// rather than drawing the DISABLED tier-0 plate.</param>
    /// <param name="bankOffset">This bank's authored 2D offset (region-height units, y-down), carried on every
    /// slot so the writer can stack it without a second lookup.</param>
    /// <param name="destination">The destination slots; exactly <paramref name="slotSet"/>.Length entries.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> does not match <paramref name="slotSet"/>
    /// in length.</exception>
    public static void ComposeBank(BindingPageView view, ReadOnlySpan<GamepadButtons> slotSet, Func<GamepadButtons, OverlayResolvedGlyph> resolveBadge, Func<string?, OverlayResolvedGlyph> resolveIcon, Func<GamepadButtons, bool>? isPressed, float bankAlpha, bool text, bool hideUnbound, Vector2 bankOffset, Span<OverlayBindingSlot> destination) {
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

        foreach (var button in slotSet) {
            if (BindingBarLayout.Categorize(
                button: button,
                classicIndex: out _
            ) == BindingSlotCategory.Exotic) {
                exoticCount++;
            }
        }

        var exoticSeen = 0;

        for (var index = 0; (index < slotSet.Length); index++) {
            var button = slotSet[index];
            var category = BindingBarLayout.Categorize(
                button: button,
                classicIndex: out var classicIndex
            );
            var categoryIndex = category switch {
                BindingSlotCategory.Classic => classicIndex,
                BindingSlotCategory.Center => Array.IndexOf(array: BindingBarLayout.CenterButtons, value: button),
                _ => exoticSeen++,
            };
            var glyph = Badge(
                glyph: resolveBadge(button),
                text: text
            );
            var source = SourceOf(button: button);
            var pressed = ((isPressed is not null) && isPressed(button));
            var binding = FindButton(
                view: view,
                source: (source ?? string.Empty)
            );
            var icon = ((binding is null)
                ? OverlayResolvedGlyph.None
                : resolveIcon(binding.Icon)
            );

            destination[index] = ((binding is null)
                ? new OverlayBindingSlot(
                    Alpha: (0.35f * bankAlpha),
                    BadgeGlyph0: glyph.Glyph0,
                    BadgeGlyph1: glyph.Glyph1,
                    BankOffset: bankOffset,
                    Bound: false,
                    Category: category,
                    CategoryCount: exoticCount,
                    CategoryIndex: categoryIndex,
                    IconGlyph0: 0,
                    IconGlyph1: 0,
                    Pressed: pressed,
                    Visible: !hideUnbound
                )
                : new OverlayBindingSlot(
                    Alpha: bankAlpha,
                    BadgeGlyph0: glyph.Glyph0,
                    BadgeGlyph1: glyph.Glyph1,
                    BankOffset: bankOffset,
                    Bound: true,
                    Category: category,
                    CategoryCount: exoticCount,
                    CategoryIndex: categoryIndex,
                    IconGlyph0: icon.Glyph0,
                    IconGlyph1: icon.Glyph1,
                    Pressed: pressed,
                    Visible: true
                ));
        }
    }
}
