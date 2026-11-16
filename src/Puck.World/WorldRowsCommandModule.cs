using System.Globalization;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The <c>world.rows</c> verb: the operator's levers that force the guarded paths of the world residency's draw rows, so
/// a check counts a path an ordinary run does not reach. <c>force hit-bound [&lt;frames&gt;]</c> gives the next frames'
/// pass blocks a mesh draw count of zero while the mesh and glass passes still draw into their targets
/// (<see cref="SdfVm.SdfWorldTables.ForceHitBound"/>), so every hit a pass reads is rejected by the mesh hit door, shades
/// as a miss and is counted in <c>world.counters gpu counter mesh-hits</c>; <c>status</c> prints what is armed. Each
/// form answers <c>[world.rows: hit-bound=&lt;frames&gt;]</c>, the forced frames still to prepare. The verb answers the
/// operator alone, and refuses until the renderer is built.
/// </summary>
/// <param name="probe">The live render objects, whose world residency the levers act on.</param>
public sealed class WorldRowsCommandModule(WorldRenderProbe probe) : ICommandModule {
    private const string Verb = "world.rows";
    private static readonly CommandForms WorldRowsForms = CommandForms.Of(verb: Verb, "force hit-bound [<frames>]", "status");

    private CommandResult Handle(WireArgs args) {
        if (probe.Residency?.Tables is not { } tables) {
            return CommandResult.Error(output: $"[{Verb}: the renderer has not been built]");
        }

        if ((args.Count == 1) && args.Is(index: 0, value: "status")) {
            return Describe(armed: tables.HitBoundArmed);
        }

        if ((args.Count is < 2 or > 3) || !args.Is(index: 0, value: "force") || !args.Is(index: 1, value: "hit-bound")) {
            return WorldRowsForms.Refuse(args: in args);
        }

        var frames = 1;

        if ((args.Count == 3) && (!args.TryInt(index: 2, value: out frames) || (frames < 1))) {
            return WorldRowsForms.Refuse(args: in args);
        }

        tables.ForceHitBound(frames: frames);

        return Describe(armed: tables.HitBoundArmed);
    }
    private static CommandResult Describe(int armed) =>
        new(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[{Verb}: hit-bound={armed}]"));

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            description: $"Forces a guarded path of the world residency's draw rows, so a check counts what an ordinary run does not reach (operator only): {WorldRowsForms.Spelling}. force hit-bound gives the next frames' pass blocks (default 1) a mesh draw count of zero while the mesh and glass passes still draw into their targets, so the mesh hit door rejects every hit a pass reads: the surface pass writes a miss, the glass pass shades no glass there, the reprojection answers no previous point, and each rejection counts in world.counters gpu counter mesh-hits (gpu.mesh-hits.rejected), which reads zero on every frame it does not force. status prints what is armed. Each form prints hit-bound=<frames>, the forced frames still to prepare.",
            forms: WorldRowsForms,
            handler: (_, args) => Handle(args: args),
            name: Verb
        );
    }
}
