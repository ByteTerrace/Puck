using System.Diagnostics;
using System.Globalization;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The console's sequencing primitive — <c>world.wait</c>, the one verb that makes a piped "drive, then read back"
/// script honest. Every other verb returns the instant it is submitted (a movement verb only enqueues a segment), so a
/// read-back on the next line observes a pose one tick into the motion; this verb suspends the drain of the queued lines
/// behind it until the addressed row's simulation has advanced a stated number of ticks. Its <c>ready</c> form holds
/// the session until the rendering engine is ready instead (<see cref="IWorldEngineReadiness"/>), its <c>bakes</c> form
/// until the presentation's creation bakes are settled (<see cref="IWorldBakeReadiness"/>), and its <c>captures</c> form
/// until every capture armed on the render graph's root has landed (<see cref="IWorldEngineReadiness.CapturesSettled"/>),
/// and its <c>indirect</c> form until a newer produced frame and every active shared cache's current-source fence hold,
/// so a script that reads rendered work, a drawn bake or a written capture waits on that fact rather than guessing it
/// from a tick count.
/// </summary>
/// <remarks>
/// It composes with (rather than replaces) <see cref="TextCommandSource"/>'s deferred-mutation barrier: that barrier
/// already stalls an Immediate line while a Simulation-routed mutation is pending, so a wait is never needed to see a
/// mutation apply — only to let the world run. This verb is Immediate, so the barrier holds it until the preceding
/// mutation has landed and the tick countdown starts from a tick that already contains it. A separate "settle" verb
/// would therefore duplicate the barrier and is deliberately absent.
/// </remarks>
/// <param name="authority">Resolves the world instance a wait addresses.</param>
/// <param name="gates">Resolves the addressed instance's tick barrier.</param>
/// <param name="readiness">The rendering engine's readiness the <c>ready</c> form waits on, or <see langword="null"/> on
/// a host that composes no renderer, where that form refuses.</param>
/// <param name="bakes">The creation bakes' readiness the <c>bakes</c> form waits on, or <see langword="null"/> on a host
/// that bakes nothing, where that form refuses.</param>
/// <param name="indirect">The active shared caches' current-source fence readiness, or null without a rendered host.</param>
public sealed class WorldWaitCommandModule(IWorldConsoleAuthority authority, IWorldWaitGateResolver gates, IWorldEngineReadiness? readiness = null, IWorldBakeReadiness? bakes = null, IWorldIndirectReadiness? indirect = null) : ICommandModule {
    /// <summary>The longest deadline, in seconds, a readiness, bake, capture or indirect wait may hold a
    /// session for.</summary>
    public const int MaxReadySeconds = 600;

    // Ten minutes at the addressed row's fixed step at its FASTEST authorable rate (50400 ticks/second) — far past
    // any scripted segment, and a loud ceiling on a fat-fingered wait that would otherwise wedge the pipe for hours.
    private const ulong MaxWaitTicks = 144_000UL;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.wait",
            description: "Suspends only the issuing text session until the addressed world's simulation has advanced a number of fixed ticks, or until its rendering engine is ready, or until its creation bakes are settled. world.wait ready <seconds> holds the session until the engine has installed its pipeline set, produced its first frame, seen the GPU complete a frame of every instance that has rendered and produced one frame more, so a windowed host has caught up the ticks its slowest frame cost before the next line runs, or the deadline (1..600 seconds) passes, whichever comes first, and reports which on stderr as a line of its own, not a second world.wait answer: '[engine: ready at tick T]', or '[engine: not ready after N seconds, so world.wait released: <reason naming the pipeline build and its progress>]'. The command's one answer is its arming line on stdout, as world.wait <ticks> answers with its release tick. It refuses on a host that composes no renderer. world.wait bakes <seconds> holds the same way until every prototype the presentation last saw is baked, held or refused and none is queued or baking, and reports '[bakes: settled at tick T]' or '[bakes: not settled after N seconds, so world.wait released]'; it refuses on a host that bakes nothing. world.wait captures <seconds> holds the same way until every capture world.screenshot armed on the render graph's root has written its file or been refused, and reports '[captures: settled at tick T]' or '[captures: not settled after N seconds, so world.wait released]'; a script that arms one capture after another waits on it between them, since a still-pending capture refuses the next; it refuses on a host that composes no renderer. world.wait indirect <seconds> requires a produced frame after arming and every active shared cache's current geometry, lighting source and completed publication fence. It reports '[indirect: settled at tick T: residency=... allocation=... epoch=... generation=... stamp=... source=... | ...]' on stderr, retaining the exact identities once, or a named not-settled deadline verdict. It proves shared-cache convergence, not every view's receiver admission, and refuses when no active indirect cache exists. A script may pause simulation through this warm-up, then resume for its fixed input ticks. A script that reads rendered work (world.counters gpu) waits on it rather than on a tick count, since a cold driver cache can hold the first frame back for many ticks. world.wait <ticks> — exactly one whole number, 1..144000 (see world.rate for the world's own current step width and completed-tick count). Later work in that session resumes in order right after the release tick completes and before the next tick steps, since the console drains before every step. On the offscreen host every one of those ticks composed and rendered its own frame, so a wait of N ticks has N frames behind it; a windowed host may compose one frame for several ticks it catches up. It waits for TIME only — a preceding mutation is already serialized by the wire's own deferred-mutation barrier. Refuses outright (naming which) while the world is paused or authors rateHz 0 — neither ever produces another completed tick to release on, so world.rate resume would be the very command trapped behind the wait it could never satisfy; arm it only once the world is actually running. A wait already armed when a pause LANDS mid-hold is force-released with a named note on stderr rather than left hanging. Echoes the release tick on success.",
            handler: (context, args) => {
                if ((args.Count == 2) && args.Is(index: 0, value: "indirect")) {
                    return ArmIndirect(args: args, context: context);
                }
                if (
                    (args.Count == 2) &&
                    args[0].Equals(
                        comparisonType: StringComparison.Ordinal,
                        other: "ready"
                    )
                ) {
                    return ArmReady(
                        args: args,
                        context: context
                    );
                }
                if (
                    (args.Count == 2) &&
                    args[0].Equals(
                        comparisonType: StringComparison.Ordinal,
                        other: "bakes"
                    )
                ) {
                    return ArmBakes(
                        args: args,
                        context: context
                    );
                }

                if (
                    (args.Count == 2) &&
                    args[0].Equals(
                        comparisonType: StringComparison.Ordinal,
                        other: "captures"
                    )
                ) {
                    return ArmCaptures(
                        args: args,
                        context: context
                    );
                }

                if (args.Count != 1) {
                    return CommandResult.Error(output: "[world.wait: expected <ticks>, ready <seconds>, bakes <seconds>, captures <seconds>, or indirect <seconds>]");
                }

                if (!args.TryUnsignedDigits(
                    index: 0,
                    value: out var ticks
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

                if (context.TextSession is not { } session) {
                    return CommandResult.Error(output: "[world.wait: requires an originating text session]");
                }

                if (ticks > (ulong.MaxValue - gate.Tick)) {
                    return CommandResult.Error(output: "[world.wait: release tick would overflow the host clock]");
                }

                var release = gate.Arm(
                    session: session,
                    ticks: ticks
                );

                return new CommandResult(Output: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[world.wait: {ticks} ticks from {gate.Tick} — releasing at tick {release}]"
                ));
            }
        );
    }

    // Holds the issuing session until the engine is ready or the deadline passes, and reports which, once, from the
    // hold itself as the pump polls it. Polling reads only IsReady, so a pending hold allocates nothing; the reason is
    // read only for the report.
    private CommandResult ArmReady(CommandContext context, WireArgs args) {
        if (readiness is not { } engine) {
            return CommandResult.Error(output: "[world.wait: refused (this host composes no renderer, so no engine will ever be ready)]");
        }

        return ArmUntil(
            args: args,
            context: context,
            done: () => engine.IsReady,
            releasedLine: static seconds => string.Create(provider: CultureInfo.InvariantCulture, handler: $"[engine: not ready after {seconds} seconds, so world.wait released: "),
            what: "the engine is ready",
            readyLine: static tick => string.Create(provider: CultureInfo.InvariantCulture, handler: $"[engine: ready at tick {tick}]"),
            reason: () => (engine.NotReadyReason ?? "no reason given")
        );
    }
    private CommandResult ArmIndirect(CommandContext context, WireArgs args) {
        if (indirect is null) { return CommandResult.Error(output: "[world.wait: indirect readiness is unavailable without a rendered host]"); }
        if (!indirect.TryBegin(reason: out var reason, wait: out var wait)) {
            return CommandResult.Error(output: $"[world.wait: indirect refused ({reason})]");
        }
        return ArmUntil(context: context, args: args, done: () => wait.IsSettled,
            readyLine: tick => string.Create(CultureInfo.InvariantCulture, $"[indirect: settled at tick {tick}: {string.Join(separator: " | ", values: wait.Identities.Select(selector: identity =>
                string.Create(CultureInfo.InvariantCulture, $"residency={identity.Residency} allocation={identity.Allocation} epoch={identity.Epoch} generation={identity.Generation} stamp={identity.Stamp} source={identity.Source}")))}]"),
            releasedLine: seconds => $"[indirect: not settled after {seconds} seconds, so world.wait released",
            what: "the current shared indirect caches have completed their source fences", reason: null);
    }
    // Holds the issuing session until the bakes are settled or the deadline passes, and reports which, once.
    private CommandResult ArmBakes(CommandContext context, WireArgs args) {
        if (bakes is not { } schedule) {
            return CommandResult.Error(output: "[world.wait: refused (this host bakes nothing, so no bake will ever settle)]");
        }

        return ArmUntil(
            args: args,
            context: context,
            done: () => schedule.IsSettled,
            releasedLine: static seconds => string.Create(provider: CultureInfo.InvariantCulture, handler: $"[bakes: not settled after {seconds} seconds, so world.wait released"),
            what: "the bakes are settled",
            readyLine: static tick => string.Create(provider: CultureInfo.InvariantCulture, handler: $"[bakes: settled at tick {tick}]"),
            reason: null
        );
    }
    // Holds the issuing session until every capture armed on the root has landed or been refused, or the deadline passes,
    // and reports which, once.
    private CommandResult ArmCaptures(CommandContext context, WireArgs args) {
        if (readiness is not { } engine) {
            return CommandResult.Error(output: "[world.wait: refused (this host composes no renderer, so no capture will ever land)]");
        }

        return ArmUntil(
            args: args,
            context: context,
            done: () => engine.CapturesSettled,
            releasedLine: static seconds => string.Create(provider: CultureInfo.InvariantCulture, handler: $"[captures: not settled after {seconds} seconds, so world.wait released"),
            what: "the captures have landed",
            readyLine: static tick => string.Create(provider: CultureInfo.InvariantCulture, handler: $"[captures: settled at tick {tick}]"),
            reason: null
        );
    }
    // The hold every form but the tick count arms: polls `done` each drain (a delegate read that allocates nothing), and on release reports
    // once on stderr whether it came true or the deadline passed, naming `reason` when one is given.
    private CommandResult ArmUntil(CommandContext context, WireArgs args, Func<bool> done, string what, Func<ulong, string> readyLine, Func<ulong, string> releasedLine, Func<string>? reason) {
        if (
            !args.TryUnsignedDigits(
                index: 1,
                value: out var seconds
            ) ||
            (seconds < 1UL) ||
            (seconds > MaxReadySeconds)
        ) {
            return CommandResult.Error(output: $"[world.wait: '{args[1]}' is not a whole number of seconds in 1..{MaxReadySeconds}]");
        }

        if (!authority.TryResolve(
            context: context,
            instance: out var instance,
            refusal: out var refusal
        )) {
            return CommandResult.Error(output: $"[world.wait: refused ({refusal})]");
        }

        if (context.TextSession is not { } session) {
            return CommandResult.Error(output: "[world.wait: requires an originating text session]");
        }

        var gate = gates.GateFor(instance: instance);
        var deadline = checked((Stopwatch.GetTimestamp() + (Stopwatch.Frequency * ((long)seconds))));
        var held = true;

        session.HoldWhile(hold: () => {
            if (!held) {
                return false;
            }

            if (done()) {
                Console.Error.WriteLine(value: readyLine(arg: gate.Tick));
            } else if (Stopwatch.GetTimestamp() >= deadline) {
                Console.Error.WriteLine(value: ((reason is null)
                    ? (releasedLine(arg: seconds) + "]")
                    : ((releasedLine(arg: seconds) + reason()) + "]")));
            } else {
                return true;
            }

            held = false;

            return false;
        });

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.wait: holding until {what}, at most {seconds} seconds, from tick {gate.Tick}]"
        ));
    }

}
