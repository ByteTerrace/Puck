using System.Text;
using Puck.Commands;
using Puck.Scripting;
using Puck.World.Addons;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The joined configuration/runtime read-back — <c>world.addons</c>, one segment per DOCUMENT addon row, in
/// document order — never a mounted-guest-only enumeration. A disabled row reads <c>DISABLED</c> with no cost
/// figures (nothing is compiled for it); an enabled row always has a committed runtime entry to join against,
/// because an enabled row that cannot prepare refuses the whole mutation or boot that would have installed it — the
/// document and the runtime can never disagree about what is actually mounted. An addon row's mount/unmount/
/// reload/enable/disable is expressed entirely by <c>world.row.set addons</c>/<c>world.row.remove addons</c> —
/// <see cref="WorldAddonRow.Enabled"/> and <see cref="WorldAddonRow.Revision"/> — through the ordinary mutation
/// pipeline (<c>WorldMutation.UpsertAddon</c>/<c>RemoveAddon</c>), which runs the addon runtime's prepare/admit
/// sequence as its own last fallible gate before installing.
/// </summary>
internal sealed class WorldAddonCommandModule(WorldAddonRuntime runtime, WorldServer server) : ICommandModule {
    private CommandResult Describe(WireArgs args) {
        if (args.Count > 0) {
            return Usage(
                form: "",
                verb: "world.addons"
            );
        }

        var rows = server.Definition.Addons;

        if (rows.Count == 0) {
            return new CommandResult(Output: "[world.addons: no addons]");
        }

        var report = runtime.DescribeCost();
        var byName = new Dictionary<string, AddonCostReport>(capacity: report.Count, comparer: StringComparer.Ordinal);

        for (var index = 0; (index < report.Count); index++) {
            byName[report[index].Name] = report[index];
        }

        var builder = new StringBuilder(value: "[world.addons:");

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];

            _ = builder.Append(value: ((index == 0)
                ? " "
                : " | "))
                .Append(value: row.Name).Append(value: ' ');

            if (!row.Enabled) {
                _ = builder.Append(value: "DISABLED");

                continue;
            }

            // Guaranteed present: an enabled row that could not prepare refuses the whole install/mutation before
            // the document ever reaches this state, so a missing entry here would be a defect elsewhere, never an
            // honest outcome to format.
            var entry = byName[row.Name];

            _ = builder.Append(value: StateLabel(entry: entry))
                .Append(value: " fuel-budget:").Append(value: entry.FuelPerTick)
                .Append(value: " fuel-last-tick:").Append(value: entry.LastTickFuelConsumed)
                .Append(value: " fuel-total:").Append(value: entry.TotalFuelConsumed)
                .Append(value: " answers-dropped-total:").Append(value: entry.TotalAnswersDropped)
                .Append(value: " event-gaps-total:").Append(value: entry.EventGaps)
                .Append(value: " event-cells-total:").Append(value: entry.EventCellsDelivered)
                .Append(value: " route-events-total:").Append(value: entry.RouteEventsDelivered)
                .Append(value: " collision-events-total:").Append(value: entry.CollisionEventsDelivered);
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }
    private static string StateLabel(AddonCostReport entry) => entry.State switch {
        AddonState.Enabled => "ENABLED",
        AddonState.Disabled => "DISABLED",
        _ => $"FAULTED({(entry.FaultDetail ?? entry.State.ToString())})",
    };
    private static CommandResult Usage(string verb, string form) {
        return CommandResult.Error(output: (string.IsNullOrEmpty(value: form)
            ? $"[{verb}: expected no arguments]"
            : $"[{verb}: expected {form}]"));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.addons",
            description: "Reports the joined configuration/runtime read-back (Immediate; the stdin barrier makes it read the settled state after a pending world.row.set addons/world.row.remove addons applies): world.addons. One segment per DOCUMENT addon row, in document order — a disabled row reads DISABLED with no cost figures; an enabled row reads its lifecycle state (with the fault detail, if faulted), the per-tick fuel budget, fuel consumed by the most recent tick it actually ran (zero on a tick it was skipped), the running fuel total consumed since it was FIRST mounted, answer groups dropped with no verdict cell, event cells dropped to a per-row event budget or the input-ring ceiling, and collision events delivered. Lifetime counters survive an unrelated reprepare pass reusing this guest untouched. Diagnostic only — never simulation state, never on a hashed path.",
            handler: (_, args) => Describe(args: args)
        );
    }
}
