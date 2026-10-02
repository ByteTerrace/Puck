using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Puck.Cli.Canary;

/// <summary>What a gate selection of <c>puck canary</c> costs: World boots and the summed leg budget in seconds,
/// both as <see cref="CanaryCommand.Plan"/> counts them from the selected manifests.</summary>
/// <param name="WorldBoots">The World processes the selection's legs boot.</param>
/// <param name="LegBudgetSeconds">The sum of the selection's per-leg timeouts.</param>
internal sealed record CanaryCeiling(int WorldBoots, int LegBudgetSeconds);
/// <summary>
/// <c>CanaryCeilings.json</c>, the recorded cost of the two gate selections of <c>puck canary</c>: the automatic set
/// and the merge gate. A run or <c>--plan</c> of a gate selection whose plan exceeds its recorded cost is refused
/// before anything builds. <c>puck canary-ceilings</c> writes the ledger from the plans of the checked-in manifests
/// and <c>--check</c> fails when a plan differs from it in either direction, so a lane that adds cost records the
/// rise in the same change, and two lanes that each record one touch the same lines and conflict textually. A
/// clean merge of two identical records still fails the check, because the merged manifests plan more than either
/// lane recorded.
/// </summary>
/// <param name="Automatic">The automatic set: a bare <c>puck canary</c>, <c>puck landing</c>, and
/// <c>--capability automatic</c>.</param>
/// <param name="Merge">The merge gate: <c>puck canary --merge</c>.</param>
internal sealed record CanaryCeilingsLedger(CanaryCeiling Automatic, CanaryCeiling Merge) {
    /// <summary>The ledger's file name at the repository root.</summary>
    public const string FileName = "CanaryCeilings.json";
    /// <summary>The ledger's own shape version.</summary>
    public const int Format = 1;

    private static void AppendCeiling(StringBuilder builder, string name, CanaryCeiling ceiling, bool last) {
        builder.Append(value: $"        \"{name}\": {{\n");
        builder.Append(value: $"            \"legBudgetSeconds\": {ceiling.LegBudgetSeconds.ToString(provider: CultureInfo.InvariantCulture)},\n");
        builder.Append(value: $"            \"worldBoots\": {ceiling.WorldBoots.ToString(provider: CultureInfo.InvariantCulture)}\n");
        builder.Append(value: (last
            ? "        }\n"
            : "        },\n"));
    }
    private static bool TryReadCeiling(JsonElement ceilings, string name, out CanaryCeiling ceiling, out string error) {
        ceiling = new CanaryCeiling(
            LegBudgetSeconds: 0,
            WorldBoots: 0
        );
        error = string.Empty;

        if (
            !ceilings.TryGetProperty(
                propertyName: name,
                value: out var element
            ) ||
            (element.ValueKind != JsonValueKind.Object)
        ) {
            error = $"ceilings.{name} is missing or is not an object";

            return false;
        }

        var members = element.EnumerateObject().Select(selector: static member => member.Name).Order(comparer: StringComparer.Ordinal).ToArray();

        if (!members.SequenceEqual(second: ["legBudgetSeconds", "worldBoots"])) {
            error = $"ceilings.{name} must hold exactly legBudgetSeconds and worldBoots";

            return false;
        }
        if (
            !element.GetProperty(propertyName: "legBudgetSeconds").TryGetInt32(value: out var legBudgetSeconds) ||
            !element.GetProperty(propertyName: "worldBoots").TryGetInt32(value: out var worldBoots) ||
            (legBudgetSeconds < 0) ||
            (worldBoots < 0)
        ) {
            error = $"ceilings.{name} counts must be non-negative integers";

            return false;
        }

        ceiling = new CanaryCeiling(
            LegBudgetSeconds: legBudgetSeconds,
            WorldBoots: worldBoots
        );

        return true;
    }
    private static IEnumerable<string> Differences(string name, CanaryCeiling recorded, CanaryCeiling planned) {
        if (planned.WorldBoots != recorded.WorldBoots) {
            yield return $"{name}: plans {planned.WorldBoots} World boot(s) against {recorded.WorldBoots} recorded ({Direction(planned: planned.WorldBoots, recorded: recorded.WorldBoots)})";
        }
        if (planned.LegBudgetSeconds != recorded.LegBudgetSeconds) {
            yield return $"{name}: plans a {planned.LegBudgetSeconds}-second leg budget against {recorded.LegBudgetSeconds} recorded ({Direction(planned: planned.LegBudgetSeconds, recorded: recorded.LegBudgetSeconds)})";
        }
    }
    private static string Direction(int planned, int recorded) => ((planned > recorded)
        ? "a rise"
        : "a fall");

    /// <summary>Makes the ledger of two gate selections' plans.</summary>
    /// <param name="automatic">The automatic set's plan.</param>
    /// <param name="merge">The merge gate's plan.</param>
    /// <returns>The ledger recording exactly those counts.</returns>
    public static CanaryCeilingsLedger Of(CanaryCommand.CanaryPlan automatic, CanaryCommand.CanaryPlan merge) => new(
        Automatic: new CanaryCeiling(
            LegBudgetSeconds: automatic.BudgetSeconds,
            WorldBoots: automatic.WorldBoots
        ),
        Merge: new CanaryCeiling(
            LegBudgetSeconds: merge.BudgetSeconds,
            WorldBoots: merge.WorldBoots
        )
    );
    /// <summary>Reads the ledger from the repository root.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="ledger">The parsed ledger, or <see langword="null"/>.</param>
    /// <param name="text">The file's text, or empty.</param>
    /// <param name="error">Why the ledger is unusable, or empty.</param>
    /// <returns><see langword="true"/> when the file exists and parses.</returns>
    public static bool TryRead(string repositoryRoot, out CanaryCeilingsLedger? ledger, out string text, out string error) {
        var path = Path.Combine(
            path1: repositoryRoot,
            path2: FileName
        );

        ledger = null;
        text = string.Empty;

        if (!File.Exists(path: path)) {
            error = $"'{path}' does not exist; run 'puck canary-ceilings' to record it.";

            return false;
        }

        text = File.ReadAllText(path: path);

        return TryParse(
            error: out error,
            json: text,
            ledger: out ledger
        );
    }
    /// <summary>Parses a ledger strictly: exactly the members <see cref="Render"/> writes, no others.</summary>
    /// <param name="json">The ledger text.</param>
    /// <param name="ledger">The parsed ledger, or <see langword="null"/>.</param>
    /// <param name="error">Why the text is unusable, or empty.</param>
    /// <returns><see langword="true"/> when the text is a ledger.</returns>
    public static bool TryParse(string json, out CanaryCeilingsLedger? ledger, out string error) {
        ledger = null;

        try {
            using var document = JsonDocument.Parse(json: json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) {
                error = "the ledger is not a JSON object";

                return false;
            }
            if (!root.EnumerateObject().Select(selector: static member => member.Name).Order(comparer: StringComparer.Ordinal).SequenceEqual(second: ["ceilings", "format"])) {
                error = "the ledger must hold exactly format and ceilings";

                return false;
            }
            if (
                !root.GetProperty(propertyName: "format").TryGetInt32(value: out var format) ||
                (format != Format)
            ) {
                error = $"format must be {Format}";

                return false;
            }

            var ceilings = root.GetProperty(propertyName: "ceilings");

            if (
                (ceilings.ValueKind != JsonValueKind.Object) ||
                !ceilings.EnumerateObject().Select(selector: static member => member.Name).Order(comparer: StringComparer.Ordinal).SequenceEqual(second: ["automatic", "merge"])
            ) {
                error = "ceilings must hold exactly automatic and merge";

                return false;
            }
            if (
                !TryReadCeiling(
                    ceiling: out var automatic,
                    ceilings: ceilings,
                    error: out error,
                    name: "automatic"
                ) ||
                !TryReadCeiling(
                    ceiling: out var merge,
                    ceilings: ceilings,
                    error: out error,
                    name: "merge"
                )
            ) {
                return false;
            }

            ledger = new CanaryCeilingsLedger(
                Automatic: automatic,
                Merge: merge
            );
            error = string.Empty;

            return true;
        } catch (JsonException exception) {
            error = exception.Message;

            return false;
        }
    }
    /// <summary>Renders the ledger in its one spelling: members in ordinal order, four-space indentation, one value
    /// per line, one final line feed.</summary>
    /// <returns>The ledger text.</returns>
    public string Render() {
        var builder = new StringBuilder();

        builder.Append(value: $"{{\n    \"format\": {Format.ToString(provider: CultureInfo.InvariantCulture)},\n    \"ceilings\": {{\n");
        AppendCeiling(
            builder: builder,
            ceiling: Automatic,
            last: false,
            name: "automatic"
        );
        AppendCeiling(
            builder: builder,
            ceiling: Merge,
            last: true,
            name: "merge"
        );
        builder.Append(value: "    }\n}\n");

        return builder.ToString();
    }
    /// <summary>The <c>--check</c> verdict on the recorded ledger against what the manifests plan now.</summary>
    /// <param name="ledgerText">The recorded ledger's text.</param>
    /// <param name="planned">The ledger the manifests' plans would record.</param>
    /// <returns>Every problem, each naming its fix; empty when the ledger holds. A recorded count must equal the plan:
    /// a rise is a deliberate recorded change, and a fall is recorded too, so freed headroom is never spendable by
    /// another lane without a reviewed change and a clean merge of two identical records cannot hide a combined
    /// rise.</returns>
    public IReadOnlyList<string> Check(string ledgerText, CanaryCeilingsLedger planned) {
        var problems = new List<string>();

        if (!string.Equals(
            a: ledgerText,
            b: Render(),
            comparisonType: StringComparison.Ordinal
        )) {
            problems.Add(item: $"not canonical: '{FileName}' differs from the form the writer produces; run 'puck canary-ceilings' to rewrite it");
        }

        problems.AddRange(collection: Differences(
            name: "automatic",
            planned: planned.Automatic,
            recorded: Automatic
        ));
        problems.AddRange(collection: Differences(
            name: "merge",
            planned: planned.Merge,
            recorded: Merge
        ));

        return problems;
    }
}
