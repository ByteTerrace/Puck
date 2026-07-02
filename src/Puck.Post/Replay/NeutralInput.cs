using System.CommandLine;
using System.Numerics;
using Puck.Commands;
using Puck.Maths;

namespace Puck.Post;

/// <summary>
/// The neutral command vocabulary the Tier-A determinism stages drive — a 2D move axis and a jump impulse — plus the
/// projection from a per-tick <see cref="CommandSnapshot"/> into the <see cref="NeutralSim"/>'s intent. It stands in for
/// the (deferred) game's input mapping, exercising the same engine command/snapshot seam with no game dependency.
/// </summary>
internal static class NeutralInput {
    /// <summary>The 2D move command name (an <see cref="CommandValueKind.Axis2D"/>).</summary>
    public const string MoveCommand = "post.move";
    /// <summary>The jump command name (a <see cref="CommandValueKind.Digital"/>).</summary>
    public const string JumpCommand = "post.jump";

    /// <summary>An empty shared binding table for the console-driven path: the CLI determinism stage drives the sim by
    /// INJECTING parsed commands (which bypass the binding table), so no physical-source mapping is needed.</summary>
    public static IInputBindings ConsoleBindings { get; } = new SharedInputBindings(bindings: InputBindingTable.Build(definitions: []));

    /// <summary>Projects slot 0's lane in a snapshot into the neutral sim's intent for the tick.</summary>
    /// <param name="snapshot">The tick's command snapshot.</param>
    /// <param name="moveId">The interned id of the move command.</param>
    /// <param name="jumpId">The interned id of the jump command.</param>
    /// <returns>The tick's intent.</returns>
    public static NeutralIntent Project(in CommandSnapshot snapshot, ushort moveId, ushort jumpId) {
        var jumpPressed = false;
        var moveX = FixedQ4816.Zero;
        var moveY = FixedQ4816.Zero;

        if (snapshot.TryGetLane(slot: 0, out var lane)) {
            if (lane.TryGetEntry(commandId: moveId, entry: out var moveEntry)) {
                var stick = moveEntry.Value.AsAxis2D;

                moveX = FixedQ4816.FromDouble(value: stick.X);
                moveY = FixedQ4816.FromDouble(value: stick.Y);
            }

            if (lane.TryGetEntry(commandId: jumpId, entry: out var jumpEntry)) {
                jumpPressed = (jumpEntry.Phase == CommandPhase.Started);
            }
        }

        return new NeutralIntent(JumpPressed: jumpPressed, MoveX: moveX, MoveY: moveY);
    }
}

/// <summary>Interns the neutral move/jump commands so a <see cref="CommandRegistry"/> assigns them stable ids; the
/// handlers are no-ops, since the sim reads the values from the per-tick snapshot rather than via dispatch. Used by the
/// scripted-snapshot determinism stage.</summary>
internal sealed class NeutralCommandModule : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            description: "Neutral 2D move axis for the determinism vehicle.",
            handler: static _ => CommandResult.None,
            name: NeutralInput.MoveCommand,
            valueKind: CommandValueKind.Axis2D
        );
        yield return CommandDefinition.Verb(
            description: "Neutral jump impulse for the determinism vehicle.",
            handler: static _ => CommandResult.None,
            name: NeutralInput.JumpCommand,
            valueKind: CommandValueKind.Digital
        );
    }
}

/// <summary>The console-facing variant of the neutral vocabulary used by the CLI-determinism stage: <c>move</c> (an
/// Axis2D with <c>--x</c>/<c>--y</c>) and <c>jump</c> (a digital impulse), both <see cref="CommandRouting.Simulation"/>
/// so a submitted text line is injected into the per-tick snapshot rather than run inline. The handlers are no-ops.</summary>
internal sealed class NeutralConsoleModule : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        var xOption = new Option<float>(name: "--x") { Description = "The horizontal move axis, -1 to 1.", };
        var yOption = new Option<float>(name: "--y") { Description = "The vertical move axis, -1 to 1.", };

        yield return new CommandDefinition(
            Description: "Neutral move for the determinism vehicle (console).",
            Handler: static _ => CommandResult.None,
            Name: NeutralInput.MoveCommand,
            TextCommand: new Command(name: NeutralInput.MoveCommand, description: "Move the neutral vehicle.") {
                xOption,
                yOption,
            },
            ValueKind: CommandValueKind.Axis2D,
            ValueSelector: parse => CommandValue.Axis(value: new Vector2(x: parse.GetValue(option: xOption), y: parse.GetValue(option: yOption)))
        ) {
            Aliases = ["move"],
            Routing = CommandRouting.Simulation,
        };
        yield return CommandDefinition.Verb(
            aliases: ["jump"],
            description: "Neutral jump for the determinism vehicle (console).",
            handler: static _ => CommandResult.None,
            name: NeutralInput.JumpCommand,
            routing: CommandRouting.Simulation,
            valueKind: CommandValueKind.Digital
        );
    }
}
