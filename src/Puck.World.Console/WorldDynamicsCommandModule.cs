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
    private static int CountKitReferences(WorldDefinition definition, string name) {
        var count = 0;

        foreach (var kit in definition.Kits) {
            var declared = (kit?.Motion switch {
                WorldMotionModel.Grounded grounded => grounded.Dynamics,
                WorldMotionModel.Swim swim => swim.Dynamics,
                _ => null,
            });

            if (string.Equals(
                a: declared,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                count++;
            }
        }

        return count;
    }
    private static int CountLookReferences(WorldDefinition definition, string name) {
        var count = 0;

        foreach (var look in definition.Looks) {
            if (string.Equals(
                a: look?.Motion.Dynamics,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                count++;
            }
        }

        return count;
    }
    private static int CountPartReferences(WorldDefinition definition, string name) {
        var count = 0;

        foreach (var look in definition.Looks) {
            foreach (var (_, partRow) in (look?.Motion.PartDynamics ?? new Dictionary<string, string>())) {
                if (string.Equals(
                    a: partRow,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    count++;
                }
            }
        }

        return count;
    }
    private static bool ReferencesRow(WorldCameraProgram? program, string name) =>
        ((program?.DynamicsOp is { } op) && string.Equals(
            a: op.Row,
            b: name,
            comparisonType: StringComparison.Ordinal
        ));
    private static int CountCameraReferences(WorldDefinition definition, string name) {
        var count = 0;

        foreach (var camera in definition.Cameras) {
            if (ReferencesRow(
                name: name,
                program: camera?.Rig
            )) {
                count++;
            }
        }

        if (definition.ViewsRaw is { } views) {
            if (ReferencesRow(
                name: name,
                program: views.SeatRig
            )) {
                count++;
            }

            if (ReferencesRow(
                name: name,
                program: views.CameraRig
            )) {
                count++;
            }
        }

        return count;
    }
    private static int CountStateReferences(WorldDefinition definition, string name) {
        var count = 0;

        foreach (var row in definition.State) {
            if (row is null) {
                continue;
            }

            if (string.Equals(
                a: row.Dynamics?.Row,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                count++;
            }

            foreach (var cell in (row.Cells ?? [])) {
                if (string.Equals(
                    a: cell?.Dynamics?.Row,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    count++;
                }
            }
        }

        return count;
    }
    private static string FormatRaw32(long raw) => (raw / 4294967296.0).ToString(
        format: "0.###",
        provider: CultureInfo.InvariantCulture
    );
    private static string DescribeRow(WorldDefinition definition, WorldDynamicsRow row) {
        // A validated row always compiles (ValidateDynamics runs this same derivation at the door), so no catch
        // masks a refusal here.
        var constants = SecondOrderDynamics.Create(
            dampingRatio: FixedQ4816.FromDouble(value: row.Damping),
            frequencyHz: FixedQ4816.FromDouble(value: row.Frequency),
            initialResponse: FixedQ4816.FromDouble(value: row.Response)
        );

        return $" {row.Name}"
            + $" f={row.Frequency.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)}"
            + $" zeta={row.Damping.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)}"
            + $" r={row.Response.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)}"
            + $" decay={FormatRaw32(raw: constants.DecayRateRaw)}"
            + $" osc={FormatRaw32(raw: constants.OscillationRateRaw)}"
            + $" k3={FormatRaw32(raw: constants.TargetVelocityGainRaw)}"
            + $" refs=cameras:{CountCameraReferences(definition: definition, name: row.Name)}"
            + $",looks:{CountLookReferences(definition: definition, name: row.Name)}"
            + $",parts:{CountPartReferences(definition: definition, name: row.Name)}"
            + $",kits:{CountKitReferences(definition: definition, name: row.Name)}"
            + $",state:{CountStateReferences(definition: definition, name: row.Name)}";
    }
    private static string DescribeDynamics(WorldDefinition definition) {
        var dynamics = definition.Dynamics;

        if (dynamics.Count == 0) {
            return "[world.dynamics: none declared]";
        }

        var builder = new StringBuilder(value: "[world.dynamics:");

        for (var index = 0; (index < dynamics.Count); index++) {
            if (dynamics[index] is { } row) {
                builder.Append(value: DescribeRow(
                    definition: definition,
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
                if (args.Count != 0) {
                    return CommandResult.Error(output: $"[world.dynamics: unrecognized '{args[0]}' — expected no arguments]");
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
