using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldInspectionCommandModule {
    private CommandResult Cost(CommandContext context, WireArgs args) {
        var residency = ((inspector is null) ? probe?.Residency : inspector.ResidencyOf(slot: context.Slot));

        if (residency?.Frame is not { } frame) {
            return CommandResult.Error(output: "[world.cost: requires a live rendered program]");
        }
        var top = ((args.Count > 0) && args.Is(index: 0, value: "top"));
        var count = 10;

        if ((args.Count > (top ? 2 : 1)) || (top && (args.Count == 2) &&
            (!int.TryParse(s: args[1].ToString(), provider: CultureInfo.InvariantCulture, result: out count) || (count < 1)))) {
            return CommandResult.Usage(form: "[<placement>|top [<n>]]", verb: "world.cost");
        }
        var pointer = ((cursor?.Status.Slot == context.Slot) ? cursor.Pick : null);
        var placement = (((args.Count == 1) && !top) ? args[0].ToString() : (pointer?.Target as WorldPickTarget)?.Placement);

        if (!top && (placement is null)) {
            return CommandResult.Error(output: "[world.cost: no completed placement hit under this seat's pointer; world.inspect on demands a live pixel]");
        }
        var report = WorldPlacementCostReport.Read(frame: frame);
        var rows = (top ? report.Placements.Take(count: count)
            : report.Placements.Where(predicate: row => (row.Placement == placement)));
        var text = new StringBuilder();

        text.Append(handler: $"[world.cost: program-words={report.ProgramWords} budget-words={residency.ProgramWordCapacity} shared-words={report.SharedWords} other-instance-words={report.OtherInstanceWords}");
        var found = false;

        foreach (var row in rows) {
            found = true;
            text.Append(provider: CultureInfo.InvariantCulture,
                handler: $"\n{row.Placement}: prototype={(row.Prototype ?? "none")} shapes={row.Shapes} owned-words={row.OwnedWords} share={((100.0 * row.OwnedWords) / Math.Max(val1: 1, val2: report.ProgramWords)):0.###}% instances={row.Instances} scope-clamps={row.ScopeClamps} bound-radius={row.BoundRadius:0.#####} halo={row.Halo:0.#####} unmaskable={row.Unmaskable} stamp-pool-slots={row.StampPoolSlots} mesh-draws={row.MeshDraws} draws-bake={row.DrawsBake}");
        }
        if (!found && !top) { return CommandResult.Error(output: $"[world.cost: no live placement '{placement}']"); }
        if (!top && (pointer is { } pixel) && ((pixel.Target as WorldPickTarget)?.Placement == placement)) { text.Append(handler: $"\npixel={pixel.X},{pixel.Y} steps={pixel.Steps} queries={pixel.Queries}"); }
        text.Append(value: ']');
        return new CommandResult(Output: text.ToString());
    }
}
