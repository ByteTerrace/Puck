using System.Text;
using Puck.Commands;

namespace Puck.World;

/// <summary>The refusal-catalog read-back verb — the read-back docs/capability-channels-STATE.md's "How we work"
/// section requires beside any new decision surface, instantiated here for the refusal vocabulary itself: the STATE
/// doc's own "THE GAP" is that nothing enumerates what the engine refuses, so a coverage battery can never assert
/// "every refusal this door can produce has been exercised". <c>world.refusals</c> is that enumeration, sourced
/// entirely from <see cref="RefusalCatalog"/> (reflection over <see cref="RefusalAttribute"/>-tagged enum members) —
/// never a hand-kept list. A SEPARATE module (no constructor dependency at all): the catalog is compiled-in data, not
/// live server state, so this module needs nothing <see cref="Program"/>'s composition root would have to wire.</summary>
internal sealed class WorldRefusalsCommandModule : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.refusals",
            description: "Echoes the engine's declared refusal catalog (Immediate; reads compiled-in data, never simulation state — zero per-tick cost, and the doors themselves pay nothing for it): world.refusals [door]. Each row reads `<door>/<id> [protocol-fault|verdict] <condition>` — door is the refusing surface's stable name, id is the enum member a door's refusal path is required to name (never free text), kind separates 'the input is not even legible' (protocol-fault) from 'the input is legible and a rule refused what it means' (verdict), and condition is the one-line trigger. Sourced by reflecting over every RefusalAttribute-tagged enum member in this build (RefusalCatalog) — never a hand-kept second list, so an unlisted reason cannot be constructed at a covered door, and every listed reason is exactly what that door's own throw sites can select from (see RefusalAttribute's remarks for what this does and does not guarantee). With a door token, lists only that door's rows; an unknown door is refused by name.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error(output: "[world.refusals: expected at most 1 value — an optional door filter]");
                }

                var filter = ((args.Count == 1) ? args[0].ToString() : null);
                var rows = new List<string>();
                var doors = new HashSet<string>(comparer: StringComparer.Ordinal);

                foreach (var entry in RefusalCatalog.All()) {
                    if ((filter is not null) && !string.Equals(a: entry.Door, b: filter, comparisonType: StringComparison.Ordinal)) {
                        continue;
                    }

                    _ = doors.Add(item: entry.Door);
                    rows.Add(item: $"{entry.Door}/{entry.Id} [{((entry.Kind == RefusalKind.ProtocolFault) ? "protocol-fault" : "verdict")}] {entry.Condition}");
                }

                if (rows.Count == 0) {
                    return CommandResult.Error(output: $"[world.refusals: {((filter is null) ? "no refusals are declared in this build" : $"door '{filter}' names no declared refusal")}]");
                }

                var builder = new StringBuilder(value: $"[world.refusals: {rows.Count} across {doors.Count} door(s)");

                foreach (var row in rows) {
                    _ = builder.Append(value: " | ").Append(value: row);
                }

                _ = builder.Append(value: ']');

                return new CommandResult(Output: builder.ToString());
            }
        );
    }
}
