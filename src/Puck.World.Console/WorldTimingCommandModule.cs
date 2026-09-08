using Puck.Commands;
using Puck.Hosting;

namespace Puck.World;

/// <summary>
/// The one arming verb for the engine's live performance metrics — <c>world.timing [on|off]</c>, server-side and
/// registered in every boot shape (including <c>--headless</c>), so it lights the world-simulation worst-of-N
/// digest (<c>WorldHostStep</c>, reported through <see cref="SimulationTimingReporter"/>) whether or not a window is
/// present. Where presentation is also composed, the same arming additionally lights the GPU per-pass digest
/// (<c>world.gpu</c>, in <c>Puck.World</c>) and the launcher's own CPU frame-timing hub.
/// </summary>
public sealed class WorldTimingCommandModule : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.timing",
            description: "Arms per-pass timing engine-wide, live (no restart, no magic env var): world.timing [on|off] — no argument echoes the armed state. On lights the world-simulation worst-of-60 CPU digest in every boot shape, plus, where a window is present, the GPU per-pass digest (world.gpu) and the launcher's CPU frame-timing hub; performance metrics are a first-class citizen here.",
            handler: (_, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: $"[world.timing: {(GpuTimingControl.Shared.Armed
                        ? "on"
                        : "off")}]");
                }

                bool resolved;

                if (args.Is(
                    index: 0,
                    value: "on"
                )) {
                    resolved = true;
                } else if (args.Is(
                    index: 0,
                    value: "off"
                )) {
                    resolved = false;
                } else {
                    return CommandResult.Error(output: $"[world.timing: unknown state '{args[0]}' — on|off]");
                }

                GpuTimingControl.Shared.SetArmed(armed: resolved);

                return new CommandResult(Output: $"[world.timing: {(resolved
                    ? "on"
                    : "off")}]");
            }
        );
    }
}
