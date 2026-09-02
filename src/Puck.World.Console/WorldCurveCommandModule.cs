using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>curves</c> section's read-back: <c>world.curves</c> reports every declared row's authored shape, its
/// compiled segment count and total arc length (the SAME <c>Puck.Maths.CurvatureSpline.Compile</c> derivation the
/// camera path op and the sim curve-follow target both read), and how many document members currently reference it.
/// The <see cref="WorldSection.Curves"/> rows themselves are authored through
/// <c>world.row.set</c>/<c>world.row.remove curves</c>.
/// </summary>
public sealed class WorldCurveCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    // The two census buckets a WorldDefinitionRows.EnumerateCurveReferences entry's Section groups into — KEEP IN
    // SYNC with the Section values that helper yields.
    private readonly record struct ReferenceCounts(int Cameras, int Follows) {
        public ReferenceCounts Increment(string section) => section switch {
            "cameras" => (this with { Cameras = (Cameras + 1) }),
            "follows" => (this with { Follows = (Follows + 1) }),
            _ => this,
        };
    }

    // One pass over the whole document, grouped by the referenced row name — every row's census then looks itself up
    // rather than each re-walking the document.
    private static Dictionary<string, ReferenceCounts> CountReferencesByRow(WorldDefinition definition) {
        var counts = new Dictionary<string, ReferenceCounts>(comparer: StringComparer.Ordinal);

        foreach (var reference in WorldDefinitionRows.EnumerateCurveReferences(definition: definition)) {
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
    private static string DescribeCurves(WorldDefinition definition) {
        var curves = definition.Curves;

        if (curves.Count == 0) {
            return "[world.curves: none declared]";
        }

        var referenceCounts = CountReferencesByRow(definition: definition);
        var echo = CommandEcho.Open(verb: "world.curves");

        for (var index = 0; (index < curves.Count); index++) {
            if (index > 0) {
                echo = echo.Segment();
            }

            if (curves[index] is not { } row) {
                continue;
            }

            // A validated row always compiles (ValidateCurves runs this same derivation at the door), so no catch
            // masks a refusal here.
            var compiled = row.Compiled;
            var refs = (referenceCounts.TryGetValue(
                key: row.Name,
                value: out var found
            )
                ? found
                : default
            );

            echo = echo
                .Head(head: row.Name)
                .Field(key: "closed", value: row.Closed)
                .Field(key: "knots", value: row.Knots.Count)
                .Field(key: "segments", value: compiled.SegmentCount)
                .Field(key: "length", value: compiled.TotalLength.ToString())
                .Field(key: "refs", value: $"cameras:{refs.Cameras},follows:{refs.Follows}");
        }

        return echo.Close();
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.curves",
            description: "Reports the curves census (Immediate; the stdin barrier makes it read the settled state after any pending mutation): one segment per row — the authored closed flag and knot count, the compiled segment count and total arc length (the SAME derivation the camera path op and the sim curve-follow target read), and how many document members reference it.",
            handler: (context, args) => {
                if (CommandResult.RequireNoArguments(args: args, verb: "world.curves") is { } refusal) {
                    return refusal;
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.curves"
                )) {
                    return error;
                }

                return new CommandResult(Output: DescribeCurves(definition: server.Definition));
            }
        );
    }
}
