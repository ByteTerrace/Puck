using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>dynamics</c> section's read-back: <c>world.dynamics</c> reports the derived constants
/// <see cref="SecondOrderDynamics.Create"/> — the SAME fixed-point derivation the simulation reads — computes for
/// every declared row, plus how many document members currently reference it. The
/// <see cref="WorldSection.Dynamics"/> rows themselves are authored through
/// <c>world.row.set</c>/<c>world.row.remove dynamics</c>.
/// </summary>
public sealed class WorldDynamicsCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    // The five census buckets a WorldDefinitionRows.EnumerateDynamicsReferences entry's Section groups into — KEEP
    // IN SYNC with the Section values that helper yields.
    private readonly record struct ReferenceCounts(int Cameras, int Looks, int Parts, int Kits, int State) {
        public ReferenceCounts Increment(string section) => section switch {
            "cameras" => (this with { Cameras = (Cameras + 1) }),
            "looks" => (this with { Looks = (Looks + 1) }),
            "parts" => (this with { Parts = (Parts + 1) }),
            "kits" => (this with { Kits = (Kits + 1) }),
            "state" => (this with { State = (State + 1) }),
            _ => this,
        };
    }

    // One pass over the whole document, grouped by the referenced row name — every row's census then looks itself up
    // rather than each re-walking the document.
    private static Dictionary<string, ReferenceCounts> CountReferencesByRow(WorldDefinition definition) {
        var counts = new Dictionary<string, ReferenceCounts>(comparer: StringComparer.Ordinal);

        foreach (var reference in WorldDefinitionRows.EnumerateDynamicsReferences(definition: definition)) {
            counts[reference.RowName] = (counts.TryGetValue(
                key: reference.RowName,
                value: out var existing
            )
                ? existing
                : default
            ).Increment(section: reference.Section);
        }

        return counts;
    }
    private static string FormatRaw32(long raw) => (raw / 4294967296.0).ToString(
        format: "0.###",
        provider: CultureInfo.InvariantCulture
    );
    private static string DescribeRow(WorldDynamicsRow row, IReadOnlyDictionary<string, ReferenceCounts> referenceCounts) {
        // A validated row always compiles (ValidateDynamics runs this same derivation at the door), so no catch
        // masks a refusal here.
        var constants = SecondOrderDynamics.Create(
            dampingRatio: FixedQ4816.FromDouble(value: row.Damping),
            frequencyHz: FixedQ4816.FromDouble(value: row.Frequency),
            initialResponse: FixedQ4816.FromDouble(value: row.Response)
        );
        var refs = (referenceCounts.TryGetValue(
            key: row.Name,
            value: out var found
        )
            ? found
            : default
        );

        return ((((((((((((string)$" {row.Name} f={row.Frequency.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)}")
            + $" zeta={row.Damping.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)}")
            + $" r={row.Response.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)}")
            + $" decay={FormatRaw32(raw: constants.DecayRateRaw)}")
            + $" osc={FormatRaw32(raw: constants.OscillationRateRaw)}")
            + $" k3={FormatRaw32(raw: constants.TargetVelocityGainRaw)}")
            + $" refs=cameras:{refs.Cameras}")
            + $",looks:{refs.Looks}")
            + $",parts:{refs.Parts}")
            + $",kits:{refs.Kits}")
            + $",state:{refs.State}");
    }
    private static string DescribeDynamics(WorldDefinition definition) {
        var dynamics = definition.Dynamics;

        if (dynamics.Count == 0) {
            return "[world.dynamics: none declared]";
        }

        var referenceCounts = CountReferencesByRow(definition: definition);
        var builder = new StringBuilder(value: "[world.dynamics:");

        for (var index = 0; (index < dynamics.Count); index++) {
            if (dynamics[index] is { } row) {
                builder.Append(value: DescribeRow(
                    referenceCounts: referenceCounts,
                    row: row
                ));
            }

            if (index < (dynamics.Count - 1)) {
                builder.Append(value: " |");
            }
        }

        return builder.Append(value: ']').ToString();
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.dynamics",
            description: "Reports the dynamics census (Immediate; the stdin barrier makes it read the settled state after any pending mutation): one segment per row — the authored f/zeta/r triple, the derived decay/osc/k3 constants (the SAME fixed-point derivation the simulation reads), and how many document members reference it.",
            handler: (context, args) => {
                if (CommandResult.RequireNoArguments(args: args, verb: "world.dynamics") is { } refusal) {
                    return refusal;
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.dynamics"
                )) {
                    return error;
                }

                return new CommandResult(Output: DescribeDynamics(definition: server.Definition));
            }
        );
    }
}
