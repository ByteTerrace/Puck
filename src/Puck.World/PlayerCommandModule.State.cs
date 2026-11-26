using System.Globalization;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    private static readonly CommandForms BodyStateForms = CommandForms.Of(verb: "body.state", OptionalBodyForm);
    private static readonly CommandForms BodyStateLoadForms = CommandForms.Of(verb: "body.state-load", "<name> <counter-value|timer-seconds> [body]");
    private static readonly CommandForms BodyRegisterForms = CommandForms.Of(verb: "body.register", "<name> <counter-value|timer-ticks> [body]");
    private CommandResult StateHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return BodyStateForms.Refuse();
        }

        if (TryRoutedSeatQuery(
            args: in args,
            caller: context.Principal,
            query: static index => new WorldQuery.PlayerState(Index: index),
            result: out var routed
        )) {
            return routed;
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "body.state"
        );
        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        var result = default(CommandResult);

        m_link.Query(
            principal: context.Principal,
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
            return BodyStateLoadForms.Refuse();
        }
        if (
            !args.TryFloat(
            index: 1,
            value: out var authored
        ) ||
            !float.IsFinite(f: authored)
        ) {
            return CommandResult.Error(output: "[body.state-load: value must be finite]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 2,
            verb: "body.state-load"
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
            return CommandResult.Error(output: $"[body.state-load: state '{name}' names no declared slot]");
        }
        if (lifetime != ActionStateLifetime.Durable) {
            return CommandResult.Error(output: $"[body.state-load: state '{name}' is ephemeral]");
        }
        if (!playerWritable) {
            return CommandResult.Error(output: $"[body.state-load: state '{name}' is not player-writable]");
        }
        // A timer's typed seconds are read as the decimal they spell and round up to engine ticks, the rule every
        // compiled duration takes; the bound keeps the tick count inside its 64-bit carrier.
        var timerSeconds = 0m;

        if (
            (kind == ActionStateKind.Timer) &&
            (
                !decimal.TryParse(
                    provider: CultureInfo.InvariantCulture,
                    result: out timerSeconds,
                    s: args[1].ToString(),
                    style: NumberStyles.Float
                ) ||
                (timerSeconds < 0m) ||
                (timerSeconds > (ulong.MaxValue / FixedTickConversion.TicksPerSecond))
            )
        ) {
            return CommandResult.Error(output: $"[body.state-load: timer seconds must be non-negative and at most {(ulong.MaxValue / FixedTickConversion.TicksPerSecond)}]");
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
                TimerTicks: FixedTickConversion.DurationEngineTicks(seconds: timerSeconds)
            )
        );
        return StageRegister(context: context, args: in args, index: index, value: value, verb: "body.state-load", operatorWrite: false);
    }
    // body.register: an operator's write of any declared register, ephemeral ones included, in the form body.state reads
    // it back. It enters the simulation as the same tick-stamped command body.state-load stages, so the replay tape holds
    // it; the authority refuses it from anyone but an operator.
    private CommandResult RegisterHandler(CommandContext context, WireArgs args) {
        if (args.Count is < 2 or > 3) {
            return BodyRegisterForms.Refuse();
        }

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: 2, verb: "body.register");

        if (player is null) {
            return CommandResult.Error(output: error!);
        }
        if (!player.TryParseRegister(name: args[0].ToString(), refusal: out var refusal, text: args[1].ToString(), value: out var value)) {
            return CommandResult.Error(output: $"[body.register: {refusal}]");
        }

        return StageRegister(context: context, args: in args, index: index, value: value, verb: "body.register", operatorWrite: true);
    }
    // Submits one register value for the next simulation tick: the one door body.state-load and body.register share.
    private CommandResult StageRegister(CommandContext context, in WireArgs args, int index, DurableStateValue value, string verb, bool operatorWrite) {
        var tick = m_server.NextInputTick;

        m_link.SubmitCommand(command: new WorldCommand.StageRegisters(
            Principal: context.Principal,
            EntityIndex: index,
            Tick: tick,
            Values: [value],
            Operator: operatorWrite
        ));

        return Echoed(
            args: in args,
            handler: $"[{verb}: body:{index} {value.Name} staged for tick {tick}]"
        );
    }
}
