using Puck.Commands;
using Puck.Platform;

namespace Puck.Demo.Input;

/// <summary>
/// The demo's input policy: the provider-neutral input ids, the binding table mapping each id to the
/// command it drives, and the adapter from platform <see cref="InputPacket"/>s to
/// <see cref="InputSignal"/>s. This is the only place that knows the demo's physical-to-command layout —
/// swapping bindings here re-skins the controls without touching the renderer or the shell.
/// </summary>
internal static class DemoInputMap {
    private const string LayoutNext = "key.layoutNext";
    private const string LayoutPrev = "key.layoutPrev";
    private const string Pause = "key.pause";
    private const string Quit = "key.quit";

    /// <summary>Builds the binding table mapping each input id to the command it activates.</summary>
    /// <returns>The demo's input-to-command binding table.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> CreateBindings() {
        return new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase) {
            // The left/right arrows cycle through the split-screen layouts.
            [LayoutNext] = [new(Command: "layout.next")],
            [LayoutPrev] = [new(Command: "layout.prev")],
            [Pause] = [new(Command: "pause")],
            [Quit] = [new(Command: "quit")],
        };
    }

    /// <summary>Adapts a platform input packet to a provider-neutral <see cref="InputSignal"/>.</summary>
    /// <param name="packet">The platform packet to adapt.</param>
    /// <returns>The corresponding input signal, or <see langword="null"/> for an unbound packet kind.</returns>
    public static InputSignal? ToInputSignal(InputPacket packet) {
        return packet.Kind switch {
            InputKind.ArrowRight => Press(source: LayoutNext),
            InputKind.ArrowLeft => Press(source: LayoutPrev),
            InputKind.Function1 => Press(source: Pause),
            // Escape maps to the platform's "toggle main menu" intent; the demo has no menu, so it quits.
            InputKind.ToggleMainMenu => Press(source: Quit),
            _ => null,
        };
    }

    private static InputSignal Press(string source) {
        return new InputSignal(
            Source: source,
            Phase: CommandPhase.Started,
            Value: CommandValue.Digital(active: true)
        );
    }
}
