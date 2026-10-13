using Puck.Commands;

namespace Puck.Launcher.Commands;

/// <summary>
/// The windowed host's frame-timing surface: <c>host.frames</c> reads the <see cref="HostFrameTimeline"/>, the
/// interval of each recent host iteration split into the loop's phases. Registered by
/// <see cref="LauncherServiceRegistration.AddLauncherTerminal"/>, so the verb exists only on the windowed host.
/// </summary>
internal sealed class HostFrameCommandModule(HostFrameTimeline timeline) : ICommandModule {
    private const int DefaultFrames = 600;
    private const int DefaultWorst = 8;
    private const double DefaultBudgetMilliseconds = (1000.0 / 60.0);

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            description: $"Reads the windowed host's per-iteration timeline: host.frames [frames] [budget-ms] [worst] echoes the last frames iterations' interval (default {DefaultFrames}, at most {HostFrameTimeline.Capacity}) as avg, p50, p99 and max, the count over budget-ms (default one 60 Hz period), the most work an iteration did outside the pacer's wait, how late the pacer woke at most, and the collections and their pause time; then the worst slowest iterations (default {DefaultWorst}), and the busiest one when they do not hold it, each split into input, commands, simulate (with its fixed steps), begin (the presenter's wait for the previous frame), produce, present and the pacer's wait with its lateness, with the garbage collections by generation and the GC pause time inside it. host.frames reset forgets every recorded iteration.",
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
                    return CommandResult.Error(output: "[host.frames: expects [frames] [budget-ms] [worst], or reset]");
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
