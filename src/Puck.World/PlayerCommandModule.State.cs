using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    private CommandResult StateHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[player.state: expected at most 1 value — an optional player index]");
        }

        if (TryRoutedSeatQuery(
            args: in args,
            query: static index => new WorldQuery.PlayerState(Index: index),
            result: out var routed
        )) {
            return routed;
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "player.state"
        );
        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        var result = default(CommandResult);

        m_link.Query(
            query: new WorldQuery.PlayerState(Index: index),
            completion: answer => {
                result = new CommandResult(Output: answer.Text) { IsError = answer.Refused };
            }
        );
        return result;
    }
    private CommandResult StateLoadHandler(CommandContext context, WireArgs args) {
        if (
            (args.Count < 2) ||
            (args.Count > 3)
        ) {
            return CommandResult.Error(output: "[player.state-load: expected <name> <counter-value|timer-seconds> [player]]");
        }
        if (
            !args.TryFloat(
            index: 1,
            value: out var authored
        ) ||
            !float.IsFinite(f: authored)
        ) {
            return CommandResult.Error(output: "[player.state-load: value must be finite]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 2,
            verb: "player.state-load"
        );
        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        var name = args[0].ToString();

        if (!player.TryDescribeActionState(
            kind: out var kind,
            lifetime: out var lifetime,
            name: name,
            playerWritable: out var playerWritable,
            timerTicks: out _,
            value: out _
        )) {
            return CommandResult.Error(output: $"[player.state-load: state '{name}' names no declared slot]");
        }
        if (lifetime != ActionStateLifetime.Durable) {
            return CommandResult.Error(output: $"[player.state-load: state '{name}' is ephemeral]");
        }
        if (!playerWritable) {
            return CommandResult.Error(output: $"[player.state-load: state '{name}' is not player-writable]");
        }
        if (
            (kind == ActionStateKind.Timer) &&
            (authored < 0f)
        ) {
            return CommandResult.Error(output: "[player.state-load: timer seconds must be non-negative]");
        }

        var value = ((kind == ActionStateKind.Counter)
            ? new DurableStateValue(
                Name: name,
                Value: FixedQ4816.FromDouble(value: authored),
                TimerTicks: 0UL
            )
            : new DurableStateValue(
                Name: name,
                Value: FixedQ4816.Zero,
                TimerTicks: FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: authored))
            )
        );
        var tick = m_server.NextInputTick;

        m_link.SubmitCommand(command: new WorldCommand.LoadDurableState(
            Principal: context.ActingPrincipal(),
            EntityIndex: (index - 1),
            Tick: tick,
            Values: [value]
        ));

        return Echoed(
            args: in args,
            handler: $"[player.state-load: p{index} {name} staged for tick {tick}]"
        );
    }
}
