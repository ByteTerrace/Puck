using Puck.Commands;

namespace Puck.Launcher.Commands;

/// <summary>
/// The host's frame-timing surface: <c>host.frames</c> reads the <see cref="HostFrameTimeline"/>, the
/// interval of each recent host iteration split into the loop's phases. Registered by
/// every terminal registration, with an independent per-step timing lane and host-lifetime shed count.
/// </summary>
internal sealed class HostFrameCommandModule(HostFrameTimeline timeline) : ICommandModule {
    private const double DefaultBudgetMilliseconds = (1000.0 / 60.0);
    private const int DefaultFrames = 600;
    private const int DefaultWorst = 8;
    private static readonly CommandForms HostFramesForms = CommandForms.Of("host.frames", "[frames] [budget-ms] [worst]", "reset");

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            description: $"Reads the host's per-iteration timeline: {HostFramesForms.Spelling}. With frames, budget-ms and worst it echoes the last frames iterations' interval (default {DefaultFrames}, at most {HostFrameTimeline.Capacity}) as avg, p50, p99 and max, the count whose work (the interval less the pacer's wait) is over budget-ms (default one 60 Hz period), the most work an iteration did outside the pacer's wait, how late the pacer woke at most, and the collections and their pause time; then produce, produce-work and produce-fence (produce split into the frame thread's own work and its time blocked on submission fences, the device still finishing earlier work), simulate and produce+simulate per iteration as avg, p50, p99 and max, and produce and produce-work over the iterations that ran a step and over those that ran none, then the frame thread's allocated bytes per iteration; then the worst slowest iterations (default {DefaultWorst}), and the busiest one when they do not hold it, each split into input, commands, simulate (with its fixed steps), begin (the presenter's wait for the previous frame), produce (with its fence waits), present and the pacer's wait with its lateness, with the garbage collections by generation, the GC pause time, the bytes every thread allocated inside it and the bytes the frame thread allocated. The independent ticks lane reports step count and avg, p50, p99 and max step cost; steps.shed counts whole owed steps dropped when catch-up spent its wall-time budget. reset forgets frame and tick samples, preserving the host-lifetime steps.shed counter.",
            forms: HostFramesForms,
            handler: (_, args) => {
                if ((args.Count == 1) && args.Is(index: 0, value: "reset")) {
                    timeline.Reset();

                    return new CommandResult(Output: "[host.frames: reset]");
                }

                var frames = DefaultFrames;
                var budget = ((float)DefaultBudgetMilliseconds);
                var worst = DefaultWorst;

                if (
                    (args.Count > 3) ||
                    ((args.Count > 0) && (!args.TryInt(index: 0, value: out frames) || (frames < 1))) ||
                    ((args.Count > 1) && (!args.TryFloat(index: 1, value: out budget) || !(budget > 0f))) ||
                    ((args.Count > 2) && (!args.TryInt(index: 2, value: out worst) || (worst < 0)))
                ) {
                    return HostFramesForms.Refuse(args: in args);
                }

                return new CommandResult(Output: timeline.Describe(
                    budgetMilliseconds: budget,
                    frames: frames,
                    worst: worst
                ));
            },
            name: "host.frames"
        );
    }
}
