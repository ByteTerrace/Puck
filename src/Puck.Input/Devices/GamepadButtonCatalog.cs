using System.Numerics;
using System.Reflection;

namespace Puck.Input.Devices;

/// <summary>The declared correspondence between a <see cref="GamepadButtons"/> flag and the provider-neutral
/// <see cref="InputSources.Gamepad"/> id that names the same physical control — derived from the enum itself, so no
/// consumer restates a member list. This is the ONE place the two vocabularies are joined: the capture path turns a
/// pressed flag into an <see cref="Puck.Commands.InputSignal"/> source through it, and everything downstream of
/// capture (bindings, the binding bar, badges) speaks source ids alone.</summary>
public static class GamepadButtonCatalog {
    // Every named flag (other than None) must have a same-named constant in InputSources.Gamepad
    // (ButtonSouth -> InputSources.Gamepad.ButtonSouth). Built once at type init and throws immediately if a flag has
    // no matching source, rather than the bit silently never reaching a binding.
    private static (GamepadButtons Flag, string Source)[] BuildSources() {
        var gamepadSources = typeof(InputSources.Gamepad);
        var flags = Enum.GetValues<GamepadButtons>();
        var map = new List<(GamepadButtons Flag, string Source)>(capacity: flags.Length);
        var highestBit = -1;

        foreach (var flag in flags) {
            if (
                (flag == GamepadButtons.None) ||
                (BitOperations.PopCount(value: ((uint)flag)) != 1)
            ) {
                continue;
            }

            var name = flag.ToString();
            var field = (gamepadSources.GetField(
                bindingAttr: BindingFlags.Public | BindingFlags.Static,
                name: name
            )
                ?? throw new InvalidOperationException(message: ((string)$"GamepadButtons.{name} has no matching InputSources.Gamepad.{name} source constant. Every digital button must be reachable as an InputSignal source or it can never be bound (nor synthesized by an addon). Add 'public const string {name} = \"gamepad.{char.ToLowerInvariant(c: name[0])}{name[1..]}\";' to InputSources.Gamepad.")));

            map.Add(item: (flag, ((string)field.GetValue(obj: null)!)));

            var bit = BitOperations.TrailingZeroCount(value: ((uint)flag));

            if (bit > highestBit) {
                highestBit = bit;
            }
        }

        // KEEP IN SYNC: GamepadButtonEdges reserves one press-stamp slot per bit, sized by a compile-time constant
        // (InlineArray requires one) that cannot itself be derived from the enum at compile time. This is the runtime
        // backstop, thrown once at type init instead of the coalescer indexing out of range on a fresh pad's first
        // press of the forgotten button.
        if ((highestBit + 1) > GamepadButtonEdges.Count) {
            throw new InvalidOperationException(message: ((string)$"GamepadButtons defines a flag at bit {highestBit} but GamepadButtonEdges.Count is only {GamepadButtonEdges.Count}. Bump GamepadButtonEdges.Count to at least {(highestBit + 1)}."));
        }

        return [.. map];
    }

    /// <summary>Gets every declared single-flag button paired with its provider-neutral source id, in enum
    /// declaration order.</summary>
    public static IReadOnlyList<(GamepadButtons Flag, string Source)> Sources { get; } = BuildSources();

    /// <summary>Returns the provider-neutral source id naming the same physical control as a button flag.</summary>
    /// <param name="button">The physical button (one flag).</param>
    /// <returns>The source id, or <see langword="null"/> for an undeclared or composite flag.</returns>
    public static string? SourceOf(GamepadButtons button) {
        for (var index = 0; (index < Sources.Count); index++) {
            if (Sources[index].Flag == button) {
                return Sources[index].Source;
            }
        }

        return null;
    }
}
