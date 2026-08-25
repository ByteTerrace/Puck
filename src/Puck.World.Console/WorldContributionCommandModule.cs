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
        var echo = CommandEcho.Open(verb: "world.contributions");
        var matched = 0;

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

            var text = new StringBuilder(value: "slot '").Append(value: placement.Id).Append(value: '\'')
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

                _ = text.Append(value: " link=").Append(value: link)
                    .Append(value: " graceSeconds=").Append(value: contribution.GraceSeconds)
                    .Append(value: " graceTicks=").Append(value: contribution.CompiledGrace(simulationRateHz: definition.SimulationRateHz));

                if (contribution.Link is not null) {
                    _ = text.Append(value: " linkState=").Append(value: (server.TryLinkLiveness(
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

                _ = text.Append(value: " deadlineTick=").Append(value: (contribution.RetractDeadlineTick?.ToString() ?? "none"));
            }

            _ = echo.Text(text: text.ToString()).Segment();
        }

        if (matched == 0) {
            _ = echo.Text(text: ((filter is { } missing)
                ? $"no contribution slot '{missing}'"
                : "(no contribution slots)"
            ));
        }

        _ = echo.Field(key: "tick", value: (server.NextInputTick - 1UL));

        return echo.Close();
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
