using Puck.Commands;
using Puck.Launcher;

namespace Puck.World;

/// <summary>
/// <c>world.lockstep</c>: the operator's lever over lockstep presentation (<see cref="PresentPacingControl.TickLocked"/>),
/// which makes a windowed World present the way the offscreen host does, one step a frame at most and each frame's
/// presentation interval the simulation time it advanced, so stills of the same ticks render the same images on every
/// run.
/// </summary>
/// <param name="pacing">The window pump's pacing control.</param>
public sealed class WorldLockstepCommandModule(PresentPacingControl pacing) : ICommandModule {
    private CommandResult Describe() => new(Output: (pacing.TickLocked
        ? "[world.lockstep: on — at most one tick a frame, and presentation advances exactly the ticks stepped]"
        : "[world.lockstep: off — presentation runs on the display's clock and interpolates between ticks]"));

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            name: "world.lockstep",
            description: "Reads or sets lockstep presentation (Immediate, the operator's): world.lockstep [on|off]. On, the windowed host steps the simulation at most once a presented frame, and every frame presents the completed tick itself rather than interpolating toward it, as a frame held for a settled capture does, and advances presentation animation (gait and foot plants, effectors, followers) by exactly the ticks completed since the last frame it captured, so a frame the render graph skips drops no time. Captures of the same ticks of the same script in fresh Worlds then render the same images byte for byte (but for a rare 1-level residue on a few pixels), however fast the display runs or how loaded the machine is. A display slower than the simulation rate slows the simulation rather than skipping ticks; the simulation's state never depends on the lever. Off (the default), presentation runs on the display's wall clock and interpolates between ticks. Use it for repeatable debug stills (see world.at).",
            handler: (_, args) => {
                if (args.Count == 0) {
                    return Describe();
                }

                if ((args.Count != 1) || !(args.Is(index: 0, value: "on") || args.Is(index: 0, value: "off"))) {
                    return CommandResult.Error(output: "[world.lockstep: expected world.lockstep [on|off]]");
                }

                pacing.SetTickLocked(locked: args.Is(index: 0, value: "on"));

                return Describe();
            }
        );
    }
}
