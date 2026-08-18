using System.Globalization;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The console's sequencing primitive — <c>world.wait</c>, the one verb that makes a piped "drive, then read back"
/// script honest. Every other verb returns the instant it is submitted (a movement verb only enqueues a segment), so a
/// read-back on the next line observes a pose one tick into the motion; this verb suspends the drain of the queued lines
/// behind it until the addressed row's simulation has advanced a stated number of ticks.
/// </summary>
/// <remarks>
/// It composes with (rather than replaces) <see cref="TextCommandSource"/>'s deferred-mutation barrier: that barrier
/// already stalls an Immediate line while a Simulation-routed mutation is pending, so a wait is never needed to see a
/// mutation apply — only to let the world run. This verb is Immediate, so the barrier holds it until the preceding
/// mutation has landed and the tick countdown starts from a tick that already contains it. A separate "settle" verb
/// would therefore duplicate the barrier and is deliberately absent.
/// </remarks>
public sealed class WorldWaitCommandModule(IWorldConsoleAuthority authority, IWorldWaitGateResolver gates) : ICommandModule {
    // Ten minutes at the addressed row's fixed step at its FASTEST authorable rate (50400 ticks/second) — far past
    // any scripted segment, and a loud ceiling on a fat-fingered wait that would otherwise wedge the pipe for hours.
    private const ulong MaxWaitTicks = 144_000UL;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.wait",
            description: "Suspends the stdin stream until the addressed world's simulation has advanced a number of fixed ticks: world.wait <ticks> — exactly one whole number, 1..144000 (see world.rate for the world's own current step width and completed-tick count). The lines queued behind it stay queued and run, in order, on the tick the count is reached; the sequencing primitive a scripted 'drive for a span, then read the pose back' needs, and tick-based rather than wall-clock so the same script reads the same pose on every run and machine. It waits for TIME only — a preceding mutation is already serialized by the wire's own deferred-mutation barrier. Refuses outright (naming which) while the world is paused or authors rateHz 0 — neither ever produces another completed tick to release on, so world.rate resume would be the very command trapped behind the wait it could never satisfy; arm it only once the world is actually running. A wait already armed when a pause LANDS mid-hold is force-released with a named note on stderr rather than left hanging. Echoes the release tick on success.",
            handler: (context, args) => {
                if (args.Count != 1) {
                    return CommandResult.Error(output: "[world.wait: expected exactly one value — <ticks>]");
                }

                if (!ulong.TryParse(
                    s: args[0],
                    style: NumberStyles.None,
                    provider: CultureInfo.InvariantCulture,
                    result: out var ticks
                )) {
                    return CommandResult.Error(output: $"[world.wait: '{args[0]}' is not a whole number of ticks]");
                }

                if (
                    (ticks == 0UL) ||
                    (ticks > MaxWaitTicks)
                ) {
                    return CommandResult.Error(output: $"[world.wait: {ticks} ticks is outside 1..{MaxWaitTicks}]");
                }

                if (!authority.TryResolve(
                    context: context,
                    instance: out var instance,
                    refusal: out var refusal
                )) {
                    return CommandResult.Error(output: $"[world.wait: refused ({refusal})]");
                }

                var gate = gates.GateFor(instance: instance);

                // REFUSE BY NAME rather than arm a hold that can never release on its own: a paused or rate-0 row
                // publishes no further completed ticks (WorldConsoleWaitGate.PublishTick only ever fires from a step
                // that actually ran), so this wait would trap every line behind it — INCLUDING the very
                // world.rate resume that would be the only thing able to lift it, since Immediate lines queue behind
                // an armed wait too. See WorldConsoleWaitGate.ReleaseStalled for the OTHER edge this module does not
                // own directly: a wait already armed when a pause lands mid-hold.
                if (instance.Server.Definition.SimulationRateHz <= 0) {
                    return CommandResult.Error(output: "[world.wait: refused (the world authors rateHz 0 — a durable stop that never produces another completed tick, so this wait could never release; see world.rate)]");
                }

                if (instance.IsPaused) {
                    return CommandResult.Error(output: "[world.wait: refused (the world is paused — no further tick will complete until world.rate resume, so this wait could never release; resume it first)]");
                }

                var release = gate.Arm(ticks: ticks);

                return new CommandResult(Output: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[world.wait: {ticks} ticks from {gate.Tick} — releasing at tick {release}]"
                ));
            }
        );
    }

}
