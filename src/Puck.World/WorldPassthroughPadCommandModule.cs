using Puck.Commands;
using Puck.Input;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The pad's desktop verbs: what a local pad's bound controls do over the window a seat engaged on a window capture's
/// screen frames, Steam desktop-mode style. <c>source.passthrough.pointer</c> (a stick) moves the pointer,
/// <c>source.passthrough.scroll</c> (a stick) turns the wheel, <c>source.passthrough.button.&lt;left|right|middle|back|forward&gt;</c>
/// press and release a pointer button (a press on the framed picture opens the window as a passthrough source, as a
/// physical click does), and <c>source.passthrough.arrow.&lt;up|down|left|right&gt;</c> press an arrow key on a window the
/// local user has opened. A world's binding page names them; only a local pad's own control acts through them: a typed
/// line, an authored command, a wheel sector, a scripted <c>player.signal</c> and a probe are refused by name
/// (<see cref="WorldPassthroughPad.IsLocalPad"/>), so a document chooses which control means which act but can never
/// make one happen.
/// </summary>
/// <remarks>Core-registered so a headless boot carries the same verb names the binding pages commit (the
/// <see cref="WorldWheelCommandModule"/> precedent); the passthrough door is presentation-only, so it is optional here
/// and a headless dispatch from a local pad is refused by name.</remarks>
/// <param name="passthrough">The windowed host's passthrough door, or <see langword="null"/> on a boot with no window.</param>
public sealed class WorldPassthroughPadCommandModule(WorldSourcePassthrough? passthrough = null) : ICommandModule {
    /// <summary>The pointer stick's verb.</summary>
    public const string PointerCommand = "source.passthrough.pointer";
    /// <summary>The scrolling stick's verb.</summary>
    public const string ScrollCommand = "source.passthrough.scroll";

    private static readonly (string Name, int Button)[] Buttons = [("left", 0), ("right", 1), ("middle", 2), ("back", 3), ("forward", 4)];
    private static readonly (string Name, KeyCode Key)[] Arrows = [("up", KeyCode.ArrowUp), ("down", KeyCode.ArrowDown), ("left", KeyCode.ArrowLeft), ("right", KeyCode.ArrowRight)];

    private static bool Pressed(CommandPhase phase) => (phase is CommandPhase.Started or CommandPhase.Active);

    // The one door every pad verb passes: only a local pad's bound control reaches the passthrough door, and only on a
    // windowed boot.
    private CommandResult Act(CommandContext context, string verb, Func<WorldSourcePassthrough, CommandContext, CommandResult> act) {
        if (!WorldPassthroughPad.IsLocalPad(
            deviceId: context.DeviceId,
            origin: context.Origin,
            source: context.Source
        )) {
            return CommandResult.Error(output: $"[{verb}: only a local pad's own control may drive a passthrough window; a typed line, an authored command, a wheel sector, player.signal and a probe cannot]");
        }
        if (passthrough is not { } door) {
            return CommandResult.Error(output: $"[{verb}: requires a windowed boot, which hosts passthrough]");
        }

        return act(
            arg1: door,
            arg2: context
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            description: "A local pad's stick as the pointer over the window its seat frames on an engaged window capture's screen (Axis2D, +X right / +Y up): the pointer moves across the framed picture at the screen route's padPointer feel, clamped to the picture, and the system cursor follows it. Only a local pad's own control drives it; a typed line, an authored command, a wheel sector, player.signal and a probe are refused. source.passthrough echoes the pad.",
            held: true,
            name: PointerCommand,
            valueKind: CommandValueKind.Axis2D,
            handler: context => Act(
                act: static (door, bound) => {
                    _ = door.PadPointer(
                        slot: bound.Slot,
                        value: (Pressed(phase: bound.Phase) ? bound.Value.AsAxis2D : default)
                    );

                    return CommandResult.None;
                },
                context: context,
                verb: PointerCommand
            )
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            description: "A local pad's stick as the wheel at the pointer over the window its seat frames (Axis2D, +X scrolls right / +Y scrolls up), at the screen route's padPointer scrollSpeed, on a window the local user has opened. Only a local pad's own control drives it.",
            held: true,
            name: ScrollCommand,
            valueKind: CommandValueKind.Axis2D,
            handler: context => Act(
                act: static (door, bound) => {
                    _ = door.PadScroll(
                        slot: bound.Slot,
                        value: (Pressed(phase: bound.Phase) ? bound.Value.AsAxis2D : default)
                    );

                    return CommandResult.None;
                },
                context: context,
                verb: ScrollCommand
            )
        );

        foreach (var (name, button) in Buttons) {
            var verb = $"source.passthrough.button.{name}";

            yield return CommandDefinition.Verb(
                bindability: CommandBindability.Bindable,
                description: $"Held: a local pad's control as the {name} pointer button at the pointer over the window its seat frames. A press on the framed picture opens the window as a passthrough source when it is not open yet, as a physical click does, and reaches it; the release goes where the press went, so holding it drags. Only a local pad's own control drives it.",
                held: true,
                name: verb,
                valueKind: CommandValueKind.Digital,
                handler: context => Act(
                    act: (door, bound) => {
                        _ = door.PadButton(
                            button: button,
                            pressed: Pressed(phase: bound.Phase),
                            slot: bound.Slot
                        );

                        return CommandResult.None;
                    },
                    context: context,
                    verb: verb
                )
            );
        }
        foreach (var (name, key) in Arrows) {
            var verb = $"source.passthrough.arrow.{name}";

            yield return CommandDefinition.Verb(
                bindability: CommandBindability.Bindable,
                description: $"Held: a local pad's control as the {name} arrow key on the window its seat frames, once the local user has opened it, wherever the keyboard is; the release goes to the window its press reached. Only a local pad's own control drives it.",
                held: true,
                name: verb,
                valueKind: CommandValueKind.Digital,
                handler: context => Act(
                    act: (door, bound) => {
                        _ = door.PadKey(
                            key: key,
                            pressed: Pressed(phase: bound.Phase),
                            slot: bound.Slot
                        );

                        return CommandResult.None;
                    },
                    context: context,
                    verb: verb
                )
            );
        }
    }
}
