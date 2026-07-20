using System.Globalization;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The console's SEQUENCING primitive — <c>world.wait</c>, the one verb that makes a piped "drive, then read back"
/// script honest. Every other verb returns the instant it is submitted (a movement verb only ENQUEUES a segment), so a
/// read-back on the next line observes a pose one tick into the motion; this verb suspends the drain of the queued lines
/// behind it until the simulation has advanced a stated number of ticks.
/// </summary>
/// <remarks>
/// It composes with (rather than replaces) <see cref="TextCommandSource"/>'s deferred-mutation barrier: that barrier
/// already stalls an Immediate line while a Simulation-routed mutation is pending, so a wait is never needed to see a
/// mutation APPLY — only to let the world RUN. This verb is Immediate, so the barrier holds it until the preceding
/// mutation has landed and the tick countdown starts from a tick that already contains it. A separate "settle" verb
/// would therefore duplicate the barrier and is deliberately absent.
/// </remarks>
internal sealed class WorldWaitCommandModule(WorldConsoleWaitGate gate) : ICommandModule {
    // Ten minutes at the host's 240 Hz fixed step — far past any scripted segment, and a loud ceiling on a fat-fingered
    // wait that would otherwise wedge the pipe for hours.
    private const ulong MaxWaitTicks = 144_000UL;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "world.wait",
            description: "Suspends the stdin stream until the simulation has advanced a number of fixed ticks: world.wait <ticks> — exactly one whole number, 1..144000 (the fixed step is 240 Hz, so 240 ticks is one second of world time). The lines queued behind it stay queued and run, in order, on the frame the tick count is reached; the sequencing primitive a scripted 'drive for a span, then read the pose back' needs, and tick-based rather than wall-clock so the same script reads the same pose on every run and machine. It waits for TIME only — a preceding mutation is already serialized by the wire's own deferred-mutation barrier. Echoes the release tick.",
            handler: (context, args) => {
                if (args.Count != 1) {
                    return Refuse(detail: "expected exactly one value — <ticks>");
                }

                if (!ulong.TryParse(s: args[0], style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var ticks)) {
                    return Refuse(detail: $"'{args[0]}' is not a whole number of ticks");
                }

                if ((ticks == 0UL) || (ticks > MaxWaitTicks)) {
                    return Refuse(detail: $"{ticks} ticks is outside 1..{MaxWaitTicks}");
                }

                var release = gate.Arm(ticks: ticks);

                return new CommandResult(Output: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[world.wait: {ticks} ticks from {gate.Tick} — releasing at tick {release}]"
                ));
            }
        );
    }

    // The one refusal shape: named, counted by wire.errors, and never a silent no-op that leaves a script reading green.
    private static CommandResult Refuse(string detail) {
        return new CommandResult(Output: $"[world.wait: {detail}]") {
            IsError = true,
        };
    }
}
