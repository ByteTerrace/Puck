using System.Text;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The contribution-slot read-back — <c>world.contributions</c> echoes every placement carrying a
/// <see cref="WorldPlacementContribution"/> facet, both halves of it, and the live link reachability the
/// presence sweep decides on.
/// </summary>
/// <remarks>Read-only. Slots are authored and filled through <c>world.row.set placements</c> like any other placement
/// row; the server stamps the contributor and owns the deadline, so there is no mutating verb here to stamp them
/// with.</remarks>
public sealed class WorldContributionCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    private static string Describe(WorldServer server, string? filter) {
        var definition = server.Definition;
        var builder = new StringBuilder();
        var matched = 0;

        _ = builder.Append(value: "[world.contributions:");

        foreach (var placement in definition.Placements) {
            if (placement.Contribution is not { } contribution) {
                continue;
            }

            if (
                (filter is { } only) &&
                !string.Equals(
                a: placement.Id,
                b: only,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                continue;
            }

            matched++;

            _ = builder.Append(value: " slot '").Append(value: placement.Id).Append(value: '\'')
                .Append(value: " tenure=").Append(value: contribution.Tenure)
                .Append(value: " slotCreation=").Append(value: contribution.SlotCreationId)
                .Append(value: " creation=").Append(value: placement.PrototypeId)
                .Append(value: (contribution.IsFilled
                ? " state=filled"
                : " state=empty"
            ))
                .Append(value: " contributor=").Append(value: (contribution.Contributor?.Describe() ?? "(none)"));

            if (contribution.Tenure == WorldContributionTenure.Presence) {
                var link = (contribution.Link?.Value ?? "(none)");

                _ = builder.Append(value: " link=").Append(value: link)
                    .Append(value: " graceSeconds=").Append(value: contribution.GraceSeconds)
                    .Append(value: " graceTicks=").Append(value: contribution.CompiledGrace(simulationRateHz: definition.SimulationRateHz));

                if (contribution.Link is not null) {
                    _ = builder.Append(value: " linkState=").Append(value: (server.TryLinkLiveness(
                        adjacencyName: link,
                        dropped: out var dropped,
                        staleTicks: out var staleTicks
                    )
                        ? $"{(dropped
                        ? "dropped"
                        : "live"
                    )}(stale {staleTicks} ticks)"
                        : "unauthored"
                    ));
                }

                _ = builder.Append(value: " deadlineTick=").Append(value: (contribution.RetractDeadlineTick?.ToString() ?? "none"));
            }

            _ = builder.Append(value: " |");
        }

        if (matched == 0) {
            _ = builder.Append(value: ((filter is { } missing)
                ? $" no contribution slot '{missing}'"
                : " (no contribution slots)"
            ));
        }

        _ = builder.Append(value: " tick=").Append(value: (server.NextInputTick - 1UL));

        return (builder.Append(value: ']').ToString());
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.contributions",
            description: "Echoes every contribution slot — the host-authored half (tenure, slotCreationId, link, graceSeconds and its compiled tick count) and the server-stamped half (contributor, retractDeadlineTick) — beside the live reachability of each presence slot's watched link: world.contributions [placementId]. With a placement id, echoes only that slot.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error(output: "[world.contributions: expected [placementId]]");
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.contributions"
                )) {
                    return error;
                }

                return new CommandResult(Output: Describe(
                    filter: ((args.Count == 1)
                    ? args[0].ToString()
                    : null
                ),
                    server: server
                ));
            }
        );
    }
}
