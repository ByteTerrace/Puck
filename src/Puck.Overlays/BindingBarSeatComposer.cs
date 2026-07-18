using Puck.Commands;
using Puck.Input;
using Puck.Input.Devices;

namespace Puck.Overlays;

/// <summary>
/// Joins a seat's active <see cref="BindingPageView"/> against the twelve physical layout slots and resolves the
/// family glyphs — the pure CPU half of the binding bar a host feed calls once per seat per frame before publishing
/// an <see cref="OverlayBindingSeat"/>. An unmapped slot still renders as the chip's DISABLED tier-0 state (a dim
/// plate with its badge) so the player sees the physical socket exists and is free.
/// </summary>
public static class BindingBarSeatComposer {
    /// <summary>The twelve physical button SOURCE ids a page binds, parallel to
    /// <see cref="BindingBarLayout.SlotButtons"/> (index i's source drives layout slot i).</summary>
    public static readonly string[] SlotSources = [
        InputSources.Gamepad.DpadUp,
        InputSources.Gamepad.DpadRight,
        InputSources.Gamepad.DpadDown,
        InputSources.Gamepad.DpadLeft,
        InputSources.Gamepad.LeftShoulder,
        InputSources.Gamepad.LeftStickPress,
        InputSources.Gamepad.ButtonNorth,
        InputSources.Gamepad.ButtonWest,
        InputSources.Gamepad.ButtonSouth,
        InputSources.Gamepad.ButtonEast,
        InputSources.Gamepad.RightShoulder,
        InputSources.Gamepad.RightStickPress,
    ];

    /// <summary>Composes one bar's twelve slots from a page view.</summary>
    /// <param name="view">The seat's active page view.</param>
    /// <param name="family">The connected controller family (badge glyph theming).</param>
    /// <param name="isPressed">Answers whether a bound command is held this frame; <see langword="null"/> renders
    /// every chip unpressed (an input-stateless feed).</param>
    /// <param name="barAlpha">The whole bar's opacity multiplier.</param>
    /// <param name="destination">The destination slots; exactly <see cref="SlotSources"/>.Length entries.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not exactly twelve entries.</exception>
    public static void ComposeSlots(BindingPageView view, GamepadType family, Func<string, bool>? isPressed, float barAlpha, Span<OverlayBindingSlot> destination) {
        ArgumentNullException.ThrowIfNull(argument: view);

        if (destination.Length != SlotSources.Length) {
            throw new ArgumentException(message: $"Expected {SlotSources.Length} slots; got {destination.Length}.", paramName: nameof(destination));
        }

        for (var index = 0; (index < destination.Length); index++) {
            var button = FindButton(view: view, source: SlotSources[index]);

            if (button is null) {
                destination[index] = new OverlayBindingSlot(
                    Alpha: (0.35f * barAlpha),
                    Bound: false,
                    Glyph: OverlayGamepadGlyphs.Resolve(button: BindingBarLayout.SlotButtons[index], family: family),
                    Icon: OverlayIconId.None,
                    Pressed: false,
                    Visible: true
                );

                continue;
            }

            destination[index] = new OverlayBindingSlot(
                Alpha: barAlpha,
                Bound: true,
                Glyph: OverlayGamepadGlyphs.Resolve(button: BindingBarLayout.SlotButtons[index], family: family),
                Icon: OverlayGamepadGlyphs.ResolveIcon(icon: button.Icon),
                Pressed: (isPressed?.Invoke(arg: button.Command) ?? false),
                Visible: true
            );
        }
    }

    /// <summary>Composes the bar's modifier pips from a page view (the active page's chord IS the held modifier
    /// sequence, so <see cref="BindingModifierView.Required"/> doubles as "held right now").</summary>
    /// <param name="view">The seat's active page view.</param>
    /// <param name="destination">The destination pips; at least <c>view.Modifiers.Count</c> entries.</param>
    /// <returns>The number of pips written.</returns>
    public static int ComposeModifiers(BindingPageView view, Span<OverlayBindingModifier> destination) {
        ArgumentNullException.ThrowIfNull(argument: view);

        var count = Math.Min(val1: view.Modifiers.Count, val2: destination.Length);

        for (var index = 0; (index < count); index++) {
            var modifier = view.Modifiers[index];

            destination[index] = new OverlayBindingModifier(
                Glyph: OverlayGamepadGlyphs.ResolveModifierSource(source: modifier.Source),
                Held: modifier.Required
            );
        }

        return count;
    }

    private static BindingPageButtonView? FindButton(BindingPageView view, string source) {
        foreach (var button in view.Buttons) {
            if (string.Equals(a: button.Source, b: source, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return button;
            }
        }

        return null;
    }
}
