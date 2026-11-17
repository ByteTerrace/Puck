using System.Globalization;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The <c>world.rows</c> verb: the operator's levers that force the guarded paths of the world residency's draw rows, so
/// a check counts a path an ordinary run does not reach. <c>force hit-bound [&lt;frames&gt;]</c> gives the next frames'
/// pass blocks a mesh draw count of zero while the mesh and glass passes still draw into their targets
/// (<see cref="SdfVm.SdfWorldTables.ForceHitBound"/>), so every hit a pass reads is rejected by the mesh hit door, shades
/// as a miss and is counted in <c>world.counters gpu counter mesh-hits</c>; <c>status</c> prints what is armed. Each
/// hit-bound form answers the forced frames still to prepare. <c>check on|off</c> enables the independent pack,
/// generation ledger, seed and quiescent device comparisons in <c>sdf.rows.check</c>, including after device loss.
/// <c>force limit bytes|auto</c> caps admission through the arena's placing function; <c>force relocate</c> packs its
/// meshes; <c>force skip tables</c> leaves that consumer's cursor uncommitted beyond its retained history. The existing
/// <c>host.memory collect</c> and <c>gpu.faults lose</c> force trim and replacement tables. The verb answers the
/// operator alone; forcing forms refuse until the renderer is built.
/// </summary>
/// <param name="probe">The live render objects, whose world residency the levers act on.</param>
public sealed class WorldRowsCommandModule(WorldRenderProbe probe) : ICommandModule {
    private const string Verb = "world.rows";

    private static readonly CommandForms WorldRowsForms = CommandForms.Of(verb: Verb, "check <on|off>",
        "force hit-bound [<frames>]", "force limit <bytes|auto>", "force relocate", "force skip <tables|host|scene>", "force pack", "force dress-fault [<writes>]", "status");

    private CommandResult Handle(WireArgs args) {
        if ((args.Count == 2) && args.Is(index: 0, value: "check") && (args.Is(index: 1, value: "on") || args.Is(index: 1, value: "off"))) {
            SdfVm.SdfRowChecks.Enabled = args.Is(index: 1, value: "on");
            return new(Output: $"[{Verb}: check={(SdfVm.SdfRowChecks.Enabled ? "on" : "off")}]");
        }
        if (args.Count == 2 && args.Is(0, "force") && args.Is(1, "pack")) {
            SdfVm.SdfRowChecks.PackRequested = true;
            return new(Output: $"[{Verb}: row pack armed]");
        }
        if (args.Count is 2 or 3 && args.Is(0, "force") && args.Is(1, "dress-fault")) {
            var writes = 1;
            if (args.Count == 3 && (!args.TryInt(2, out writes) || writes < 1)) { return WorldRowsForms.Refuse(in args); }
            SdfVm.SdfRowChecks.DressFaultAfter = writes;
            return new(Output: $"[{Verb}: dress failure armed after {writes} writes]");
        }
        if (args.Count == 3 && args.Is(0, "force") && args.Is(1, "skip") && (args.Is(2, "host") || args.Is(2, "scene"))) {
            SdfVm.SdfRowChecks.SkipHostRequested = args.Is(2, "host");
            SdfVm.SdfRowChecks.SkipSceneRequested = args.Is(2, "scene");
            return new(Output: $"[{Verb}: {args[2]} cursor skip armed]");
        }
        if (probe.Residency?.Tables is not { } tables) {
            return CommandResult.Error(output: $"[{Verb}: the renderer has not been built]");
        }

        if ((args.Count == 1) && args.Is(index: 0, value: "status")) {
            return new(Output: $"[{Verb}: check={(SdfVm.SdfRowChecks.Enabled ? "on" : "off")} hit-bound={tables.HitBoundArmed} failure={(tables.RowCheckFailure ?? "none")}]");
        }
        if ((args.Count == 2) && args.Is(index: 0, value: "force") && args.Is(index: 1, value: "relocate")) {
            tables.ForceMeshRelocation();
            return new(Output: $"[{Verb}: relocation armed]");
        }
        if ((args.Count == 3) && args.Is(index: 0, value: "force") && args.Is(index: 1, value: "skip") && args.Is(index: 2, value: "tables")) {
            tables.ForceSkipTables();
            return new(Output: $"[{Verb}: tables cursor skip armed]");
        }
        if ((args.Count == 3) && args.Is(index: 0, value: "force") && args.Is(index: 1, value: "limit")) {
            if (args.Is(index: 2, value: "auto")) { tables.ForceMeshLimit(bytes: null); } else if (ulong.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)) { tables.ForceMeshLimit(bytes: bytes); } else { return WorldRowsForms.Refuse(args: in args); }
            return new(Output: $"[{Verb}: admission limit armed]");
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
            description: $"Operator row checks and forcing levers: {WorldRowsForms.Spelling}. check compares packing, an independent generation ledger, previous-matrix seeds and quiescent device bytes; world.counters sdf.rows.check reads comparisons, mismatches and forcing counts. force limit caps admission in bytes (auto restores the device limit). force relocate packs the mesh arena. force skip tables leaves the table cursor uncommitted beyond journal history. host.memory collect forces trim; gpu.faults lose creates new tables. force hit-bound zeroes the next frames' reader bound (default 1), so hits become counted misses in gpu.mesh-hits.rejected. status prints the check state, hit-bound frames and first comparison failure.",
            forms: WorldRowsForms,
            handler: (_, args) => Handle(args: args),
            name: Verb
        );
    }
}
