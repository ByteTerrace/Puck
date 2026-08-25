using System.Globalization;
using System.Text;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The camera-probes console surface — <c>probe.status</c>, <c>probe.record</c>, and <c>probe.set</c> — for the
/// <c>probes</c> document section's live host (<see cref="WorldProbes"/>). All three verbs are Immediate.
/// <c>probe.status</c> is a query (always echoes, even under <c>wire.ack quiet</c>) reporting every declared
/// probe's run state and every binding's conditioned value and write count; <c>probe.record</c> arms a live
/// recording of one probe's fresh readings to a <c>puck.probe-track.v1</c> document — the recorded track a
/// track-input probe plugs into in place of a live device, so a binding's downstream behavior is testable without
/// hardware; <c>probe.set</c> patches one probe kind config field live (<see cref="WorldProbes.TrySetField"/>). A
/// refusal marks <see cref="CommandResult.IsError"/> (stderr, counted by <c>wire.errors</c>);
/// <c>probe.record</c>'s own completion narrates separately, on stderr, once its window elapses (see
/// <see cref="WorldProbes.TryBeginRecording"/>).
/// </summary>
internal sealed class ProbeCommandModule(Func<WorldProbes> probes) : ICommandModule {
    private readonly Func<WorldProbes> m_probes = probes;

    private CommandResult RecordHandler(CommandContext context, WireArgs args) {
        if (args.Count != 3) {
            return CommandResult.Error(output: "[probe.record: expected <probe>[@<seat>] <path> <seconds>]");
        }

        if (!args.TryFloat(
            index: 2,
            value: out var seconds
        )) {
            return CommandResult.Error(output: $"[probe.record: seconds '{args[2].ToString()}' must be a number]");
        }

        var probeRef = args[0].ToString();
        var path = args[1].ToString();

        if (!m_probes().TryBeginRecording(
            probeRef: probeRef,
            path: path,
            reason: out var reason,
            seconds: seconds
        )) {
            return CommandResult.Error(output: $"[probe.record: {reason}]");
        }

        return new CommandResult(Output: $"[probe.record: {probeRef} arming -> {path} ({seconds.ToString(provider: CultureInfo.InvariantCulture)}s)]");
    }
    private CommandResult SetHandler(CommandContext context, WireArgs args) {
        if (args.Count != 3) {
            return CommandResult.Error(output: "[probe.set: expected <probe>[@<seat>] <field> <value>]");
        }

        if (!args.TryFloat(
            index: 2,
            value: out var value
        )) {
            return CommandResult.Error(output: $"[probe.set: value '{args[2].ToString()}' must be a number]");
        }

        var probeRef = args[0].ToString();
        var field = args[1].ToString();

        if (!m_probes().TrySetField(
            field: field,
            probeRef: probeRef,
            reason: out var reason,
            value: value
        )) {
            return CommandResult.Error(output: $"[probe.set: {reason}]");
        }

        return new CommandResult(Output: $"[probe.set: {probeRef}.{field}={value.ToString(format: "0.0000", provider: CultureInfo.InvariantCulture)}]");
    }
    private CommandResult StatusHandler(CommandContext context, WireArgs args) {
        if (CommandResult.RequireNoArguments(args: args, verb: "probe.status") is { } refusal) {
            return refusal;
        }

        var builder = new StringBuilder();

        return (m_probes().Describe(builder: builder)
            ? new CommandResult(Output: $"[probe.status: {builder}]")
            : new CommandResult(Output: "[probe.status: no probes rows declared]")
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "probe.status",
            description: "Echoes every live probe instance and its bindings, live: probe.status — a probe whose row is seat-relative (at least one camera socket carries no seat of its own) is instanced once per occupied local seat and listed as <id>@<seat>; a single-instance row is listed by its bare <id>. For each instance, its kind, either every socket's bound source (socket=camera:<sensor>@<resolvedToken>|view:<camera>|probe:<id>|unbound, space-separated — a camera socket names the seat it targets and the roster token (camera<N>) actually resolved, or 'seat<N>-unassigned' when no camera is seated there) or track=<path> for a track-input probe, run state (running | idle, plus a fault when one is recorded) and, while running, tier, rate, cycles/drops, the latest capture age, channel values, and confidence; for each axis binding, its conditioned value, confidence, captured value, and expiry; for each parameter binding, its extension target, value, and write count; for each control binding, its device control, value, and write count. A query (always echoes, even under wire.ack quiet). A world with no probes rows answers 'no probes rows declared'.",
            handler: StatusHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "probe.record",
            description: "Arms a live recording of one declared probe instance to a puck.probe-track.v1 document: probe.record <probe>[@<seat>] <path> <seconds> — every fresh reading the instance publishes over the window is captured (serviced once per host frame, so this only progresses while the world is running windowed); the document writes once the window elapses and completion narrates on stderr with the sample count. The recorded document plugs into a track-input probe (probes[].track) in place of a live device — the hardware-free proof leg. The @<seat> suffix names one instance of a seat-relative probe (required — omitting it is ambiguous and refused, naming the live instances) or, for a single-instance probe, is optional. Errors on an unknown probe, an unresolved or ambiguous instance, a non-positive or non-numeric seconds, an already-armed recording, or an unwritable path.",
            handler: RecordHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "probe.set",
            description: "Patches one float config field of a declared probe's kind live: probe.set <probe>[@<seat>] <field> <value> — the same constants write a probe-target parameter binding performs, applied immediately to the running kernel(s). Bound only by the field's own declared range, not any binding's authored one; a parameter binding targeting the same field overwrites this write on its own next changed reading. With no @<seat> suffix, a single-instance probe's one instance is written; a seat-relative probe's every live instance is written. With a suffix, only that one instance. Errors on an unknown probe, an unresolved instance, a field that is not a declared float config field of the probe's kind, or a value outside the field's declared range.",
            handler: SetHandler
        );
    }
}
