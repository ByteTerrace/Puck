using System.Globalization;
using System.Text;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The camera-probes console surface — <c>probe.status</c> and <c>probe.record</c>, the pipe-assertable read-back
/// and hardware-free proof leg for the <c>probes</c> document section's live host (<see cref="WorldProbes"/>).
/// Both verbs are Immediate: <c>probe.status</c> is a query (always echoes, even under <c>wire.ack quiet</c>)
/// reporting every declared probe's run state and every binding's conditioned value and write count;
/// <c>probe.record</c> arms a live recording of one probe's fresh readings to a <c>puck.probe-track.v1</c>
/// document — the recorded track a track-input probe plugs into in place of a live device, so a binding's
/// downstream behavior is testable without hardware. A refusal marks <see cref="CommandResult.IsError"/> (stderr,
/// counted by <c>wire.errors</c>); <c>probe.record</c>'s own completion narrates separately, on stderr, once its
/// window elapses (see <see cref="WorldProbes.TryBeginRecording"/>).
/// </summary>
internal sealed class ProbeCommandModule(Func<WorldProbes> probes) : ICommandModule {
    private readonly Func<WorldProbes> m_probes = probes;

    private CommandResult RecordHandler(CommandContext context, WireArgs args) {
        if (args.Count != 3) {
            return CommandResult.Error(output: "[probe.record: expected <probe> <path> <seconds>]");
        }

        if (!args.TryFloat(
            index: 2,
            value: out var seconds
        )) {
            return CommandResult.Error(output: $"[probe.record: seconds '{args[2].ToString()}' must be a number]");
        }

        var probeId = args[0].ToString();
        var path = args[1].ToString();

        if (!m_probes().TryBeginRecording(
            probeId: probeId,
            path: path,
            reason: out var reason,
            seconds: seconds
        )) {
            return CommandResult.Error(output: $"[probe.record: {reason}]");
        }

        return new CommandResult(Output: $"[probe.record: {probeId} arming -> {path} ({seconds.ToString(provider: CultureInfo.InvariantCulture)}s)]");
    }
    private CommandResult StatusHandler(CommandContext context, WireArgs args) {
        if (args.Count != 0) {
            return CommandResult.Error(output: "[probe.status: expected no arguments]");
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
            description: "Echoes every declared probe row and its bindings, live: probe.status — for each probe, its kind, input (camera:<sensor> or track), run state (running | idle, plus a fault when one is recorded) and, while running, tier, rate, cycles/drops, the latest capture age, channel values, and confidence; for each axis binding, its conditioned value, confidence, captured value, and expiry; for each parameter binding, its extension target, value, and write count; for each control binding, its device control, value, and write count. A query (always echoes, even under wire.ack quiet). A world with no probes rows answers 'no probes rows declared'.",
            handler: StatusHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "probe.record",
            description: "Arms a live recording of one declared probe to a puck.probe-track.v1 document: probe.record <probe> <path> <seconds> — every fresh reading the probe publishes over the window is captured (serviced once per host frame, so this only progresses while the world is running windowed); the document writes once the window elapses and completion narrates on stderr with the sample count. The recorded document plugs into a track-input probe (probes[].input.$type = track) in place of a live device — the hardware-free proof leg. Errors on an unknown probe, a non-positive or non-numeric seconds, an already-armed recording, or an unwritable path.",
            handler: RecordHandler
        );
    }
}
