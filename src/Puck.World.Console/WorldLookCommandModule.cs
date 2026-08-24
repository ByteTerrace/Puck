using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The LOOK section's remaining verb surface. <c>world.population.spawn</c> is the read-modify-write sugar over the
/// population defaults' <see cref="WorldDistribution"/> (it stays: the spawn policy sits under
/// <c>world.population.*</c>, not a separate spawns-named family, so the verb name matches the
/// <see cref="WorldSection.Population"/> grant section it mutates); <c>world.looks</c> is the Immediate census (one
/// line per look row — name, resolved source, active count — mirroring <c>world.population</c>). The
/// <see cref="WorldSection.Looks"/> rows themselves are authored through
/// <c>world.row.set</c>/<c>world.row.remove looks</c>, and their row-to-entity sequence through
/// <c>world.assign looks ...</c>. A SEPARATE module from the mutation surface so neither class crosses its
/// analyzer ceilings.
/// </summary>
public sealed class WorldLookCommandModule(IWorldConsoleAuthority authority, IServerLink link) : ICommandModule {
    // The world.looks census: one row per look, mirroring world.population's per-kit echo. dyn= names the root
    // dynamics row when the look authors one; partDyn= is the authored part-follower count.
    private static string DescribeLooks(WorldPopulation population) {
        var rows = population.LookRows;
        var counts = population.ActiveLookCounts();
        var builder = new StringBuilder(value: "[world.looks:");

        for (var index = 0; (index < rows.Count); index++) {
            var motion = rows[index].Motion;

            _ = builder.Append(value: $" {rows[index].Name}={DescribeSource(source: rows[index].Source)}:{counts[index]}");

            if (motion.Dynamics is { } dynamicsRow) {
                _ = builder.Append(value: $" dyn={dynamicsRow}");
            }

            if (motion.PartDynamics is { Count: > 0 } partDynamics) {
                _ = builder.Append(value: $" partDyn={partDynamics.Count}");
            }
        }

        return builder.Append(value: ']').ToString();
    }
    private static string DescribeSource(WorldLookSource source) => source switch {
        WorldLookSource.Catalog { Index: { } catalogIndex } => $"catalog(index {catalogIndex})",
        WorldLookSource.Catalog => "catalog(index-derived)",
        WorldLookSource.Creation creation => $"creation({creation.CreationId})",
        _ => "unknown",
    };
    private static WorldDistribution? ParseDistribution(in WireArgs args, out string error) {
        error = string.Empty;

        if (args.Is(
            index: 0,
            value: "disc"
        )) {
            if (
                (args.Count != 3) ||
                !float.TryParse(
                s: args[1],
                style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture,
                result: out var radius
            ) ||
                !int.TryParse(
                s: args[2],
                style: NumberStyles.Integer,
                provider: CultureInfo.InvariantCulture,
                result: out var sampleCount
            )
            ) {
                error = "disc needs a <radius> number and <sampleCount> integer";

                return null;
            }

            return new WorldDistribution(
                Region: new WorldDistributionRegion.Disc(
                    Radius: radius,
                    SampleCount: sampleCount
                ),
                Fill: new WorldSequence(
                    Name: WorldSequence.Additive,
                    Offset: 0,
                    Step: 0.3819660112501051f
                )
            );
        }

        if (args.Is(
            index: 0,
            value: "points"
        )) {
            if (
                (args.Count < 3) ||
                !float.TryParse(
                s: args[1],
                style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture,
                result: out var halfExtent
            )
            ) {
                error = "points needs a <halfExtent> number then at least one spawn-point id";

                return null;
            }

            var points = new string[(args.Count - 2)];

            for (var index = 2; (index < args.Count); index++) {
                points[(index - 2)] = args[index].ToString();
            }

            return new WorldDistribution(
                Region: new WorldDistributionRegion.Points(
                    HalfExtent: halfExtent,
                    Names: points
                ),
                Fill: new WorldSequence(
                    Name: WorldSequence.R2,
                    Offset: 133,
                    Step: 0f
                )
            );
        }

        error = $"unknown region '{args[0].ToString()}' — disc | points";

        return null;
    }
    private CommandResult Submit(WorldMutation mutation) {
        link.SubmitWorldMutation(mutation: mutation);

        return CommandResult.None;
    }
    private static CommandResult Usage(string verb, string form) => CommandResult.Error(output: $"[{verb}: expected {form}]");

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.population.spawn",
            description: "Sets the simulated-peer spawn distribution (LIVE for future activations, standing bodies unmoved): world.population.spawn disc <radius> <sampleCount> | points <halfExtent> <id> [<id>…].",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return Usage(
                        form: "disc <radius> <sampleCount> | points <halfExtent> <id> [<id>…]",
                        verb: "world.population.spawn"
                    );
                }

                if (ParseDistribution(
                    args: in args,
                    error: out var distributionError
                ) is not { } distribution) {
                    return CommandResult.Error(output: $"[world.population.spawn: {distributionError}]");
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.population.spawn"
                )) {
                    return error;
                }

                var current = server.Definition.Population;

                return Submit(mutation: new WorldMutation.SetPopulationDefaults(
                    Principal: context.ActingPrincipal(),
                    Population: (current with { DistributionRaw = distribution })
                ));
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.looks",
            description: "Reports the LOOK census (Immediate; the stdin barrier makes it read the settled state after any pending mutation): one line per look row — name, resolved source, active entity count. A world with no looks section prints the single implicit 'catalog (index-derived)' row over the whole population.",
            handler: (context, args) => {
                if (args.Count != 0) {
                    return CommandResult.Error(output: $"[world.looks: unrecognized '{args[0]}' — expected no arguments]");
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.looks"
                )) {
                    return error;
                }

                return new CommandResult(Output: DescribeLooks(population: server.Population));
            }
        );
    }
}
