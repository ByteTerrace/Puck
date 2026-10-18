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
/// the session until the rendering engine is ready instead (<see cref="IWorldEngineReadiness"/>), and its <c>captures</c>
/// form until every capture armed on the render graph's root has landed (<see cref="IWorldEngineReadiness.CapturesSettled"/>),
/// so a script that reads rendered work or a written capture waits on that fact rather than guessing it
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
public sealed class WorldWaitCommandModule(IWorldConsoleAuthority authority, IWorldWaitGateResolver gates, IWorldEngineReadiness? readiness = null) : ICommandModule {
    /// <summary>The longest deadline, in seconds, a readiness or capture wait may hold a
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
            description: "Suspends only the issuing text session until the addressed world's simulation has advanced a number of fixed ticks, or until its rendering engine is ready. world.wait ready <seconds> holds the session until the engine has installed its pipeline set, produced its first frame, seen the GPU complete a frame of every instance that has rendered and produced one frame more, so a windowed host has caught up the ticks its slowest frame cost before the next line runs, or the deadline (1..600 seconds) passes, whichever comes first, and reports which on stderr as a line of its own, not a second world.wait answer: '[engine: ready at tick T]', or '[engine: not ready after N seconds, so world.wait released: <reason naming the pipeline build and its progress>]'. The command's one answer is its arming line on stdout, as world.wait <ticks> answers with its release tick. It refuses on a host that composes no renderer. world.wait captures <seconds> holds the same way until every capture world.screenshot armed on the render graph's root has written its file or been refused, and reports '[captures: settled at tick T]' or '[captures: not settled after N seconds, so world.wait released]'; a script that arms one capture after another waits on it between them, since a still-pending capture refuses the next; it refuses on a host that composes no renderer. A script that reads rendered work (world.counters gpu) waits on it rather than on a tick count, since a cold driver cache can hold the first frame back for many ticks. world.wait <ticks> — exactly one whole number, 1..144000 (see world.rate for the world's own current step width and completed-tick count). Later work in that session resumes in order right after the release tick completes and before the next tick steps, since the console drains before every step. On the offscreen host every one of those ticks composed and rendered its own frame, so a wait of N ticks has N frames behind it; a windowed host may compose one frame for several ticks it catches up. It waits for TIME only — a preceding mutation is already serialized by the wire's own deferred-mutation barrier. Refuses outright (naming which) while the world is paused or authors rateHz 0 — neither ever produces another completed tick to release on, so world.rate resume would be the very command trapped behind the wait it could never satisfy; arm it only once the world is actually running. A wait already armed when a pause LANDS mid-hold is force-released with a named note on stderr rather than left hanging. Echoes the release tick on success. In a session that settles its results, such as an operator attachment's call, the line answers when the hold ends rather than as it arms: world.wait <ticks> with '[world.wait: N ticks from T — released at tick R]', and the ready and captures forms with their release report, a refusal unless the condition came true. Such a call refuses a tick wait the world's rate cannot complete before the call's answer is due, and a hold still standing when it is due ends with a refusal naming that, so the attachment survives and its next call runs at once.",
            handler: (context, args) => {
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
                        other: "captures"
                    )
                ) {
                    return ArmCaptures(
                        args: args,
                        context: context
                    );
                }

                if (args.Count != 1) {
                    return CommandResult.Error(output: "[world.wait: expected <ticks>, ready <seconds> or captures <seconds>]");
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

                // A caller with an answer deadline (a control attachment's call) is refused a wait its rate cannot
                // complete in time, rather than left to lose its attachment to the deadline mid-wait.
                var rateHz = ((ulong)instance.Server.Definition.SimulationRateHz);
                var span = TimeSpan.FromTicks(value: ((long)((ticks * ((ulong)TimeSpan.TicksPerSecond)) / rateHz)));

                if (
                    (session.AnswerDeadline is { } due) &&
                    (span > due.Remaining)
                ) {
                    return CommandResult.Error(output: string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"[world.wait: refused ({ticks} ticks at {rateHz} Hz take {span.TotalSeconds:0.###} s, longer than the {Math.Max(val1: due.Remaining.TotalSeconds, val2: 0d):0.###} s this call can wait for its answer; wait in shorter steps or give the call a longer deadline)]"
                    ));
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

    // Holds the issuing session until the engine is ready or a deadline passes, and reports which, once, as the hold
    // ends. Polling reads only IsReady, so a pending hold allocates nothing; the reason is read only for the report.
    private CommandResult ArmReady(CommandContext context, WireArgs args) {
        if (readiness is not { } engine) {
            return CommandResult.Error(output: "[world.wait: refused (this host composes no renderer, so no engine will ever be ready)]");
        }

        return ArmUntil(
            args: args,
            context: context,
            done: () => engine.IsReady,
            releasedLine: static when => $"[engine: not ready {when}, so world.wait released: ",
            what: "the engine is ready",
            readyLine: static tick => string.Create(provider: CultureInfo.InvariantCulture, handler: $"[engine: ready at tick {tick}]"),
            reason: () => (engine.NotReadyReason ?? "no reason given")
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
            releasedLine: static when => $"[captures: not settled {when}, so world.wait released",
            what: "the captures have landed",
            readyLine: static tick => string.Create(provider: CultureInfo.InvariantCulture, handler: $"[captures: settled at tick {tick}]"),
            reason: null
        );
    }
    // The hold every form but the tick count arms: polls `done` each drain (a delegate read that allocates nothing)
    // until it comes true or the seconds pass. As the hold ends it reports once on stderr whether `done` came true, the
    // seconds passed or the session's answer deadline passed first, naming `reason` when one is given; a session that
    // settles results answers the arming line with that report, a refusal unless `done` came true.
    private CommandResult ArmUntil(CommandContext context, WireArgs args, Func<bool> done, string what, Func<ulong, string> readyLine, Func<string, string> releasedLine, Func<string>? reason) {
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

        session.HoldWhile(
            hold: () => (!done() && (Stopwatch.GetTimestamp() < deadline)),
            released: deadlinePassed => {
                CommandResult report;

                if (done()) {
                    report = new CommandResult(Output: readyLine(arg: gate.Tick));
                } else {
                    var released = releasedLine(arg: (deadlinePassed
                        ? "when this call's answer was due"
                        : string.Create(provider: CultureInfo.InvariantCulture, handler: $"after {seconds} seconds")));

                    report = CommandResult.Error(output: ((reason is null)
                        ? (released + "]")
                        : ((released + reason()) + "]")));
                }

                Console.Error.WriteLine(value: report.Output);

                return report;
            }
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.wait: holding until {what}, at most {seconds} seconds, from tick {gate.Tick}]"
        ));
    }

}
