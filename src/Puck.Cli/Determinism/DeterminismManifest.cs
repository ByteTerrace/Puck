using System.Globalization;
using System.Text.Json;
using Puck.Assets;
using Puck.Maths;

namespace Puck.Cli.Determinism;

/// <summary>One held intent of a scenario: the named channel values a body is driven with on every tick from
/// <paramref name="From"/> through <paramref name="Through"/>, both counted from one.</summary>
/// <param name="Body">The body's 0-based population index.</param>
/// <param name="From">The first tick the intent is submitted before.</param>
/// <param name="Through">The last tick the intent is submitted before.</param>
/// <param name="Channels">Each channel the intent drives, by declared name, with its exact fixed-point value.</param>
internal sealed record DeterminismIntent(int Body, int From, int Through, IReadOnlyList<(string Channel, FixedQ4816 Value)> Channels);
/// <summary>One state cell a scenario writes before a tick, through the same value mutation the console's
/// <c>world.state.cell.set</c> submits.</summary>
/// <param name="Tick">The tick the write is applied before, counted from one.</param>
/// <param name="Row">The state row.</param>
/// <param name="Key">The cell's key.</param>
/// <param name="Value">The value as authored: an integer for an <c>Int</c> cell, a decimal for a <c>Fixed</c> one,
/// <c>true</c> or <c>false</c> for a <c>Bool</c> one.</param>
internal sealed record DeterminismCellWrite(int Tick, string Row, string Key, string Value);
/// <summary>One scenario of a determinism manifest: a world booted in-process, the seats joined before the first tick,
/// the intents held over tick ranges, the state cells written before given ticks, and the number of ticks
/// recorded.</summary>
/// <param name="Name">The scenario's name, unique in its manifest.</param>
/// <param name="World">The full path of the world document or <c>.puck</c> source.</param>
/// <param name="Ticks">The number of simulation ticks recorded.</param>
/// <param name="Seats">The 0-based seat slots joined before the first tick, in order.</param>
/// <param name="Intents">The held intents, in authored order.</param>
/// <param name="Cells">The state cell writes, in authored order.</param>
internal sealed record DeterminismScenario(string Name, string World, int Ticks, IReadOnlyList<int> Seats, IReadOnlyList<DeterminismIntent> Intents, IReadOnlyList<DeterminismCellWrite> Cells);
/// <summary>
/// A determinism manifest (<c>puck.determinism.manifest.v1</c>): the scenarios <c>puck determinism record</c> boots and
/// records. Paths resolve against the manifest's own directory. The manifest's pin, a hash of its bytes, is written into
/// every stream recorded from it, so <c>puck determinism compare</c> refuses two streams of different manifests rather
/// than reading their difference as a divergence.
/// </summary>
/// <param name="Pin">The manifest file's content pin.</param>
/// <param name="Scenarios">The scenarios, in authored order.</param>
internal sealed record DeterminismManifest(AssetContentHash Pin, IReadOnlyList<DeterminismScenario> Scenarios) {
    /// <summary>The manifest's schema token.</summary>
    public const string Schema = "puck.determinism.manifest.v1";

    private const int MaxDepth = 16;
    private const int MaxTicks = 100_000;

    private sealed class Refusal(string message) : Exception(message: message);

    private static Exception Refuse(string message) => new Refusal(message: message);
    private static DeterminismIntent ReadIntent(JsonElement element, string context) {
        CliStrictJson.RequireObject(context: context, element: element, refusal: Refuse);
        CliStrictJson.RequireOnlyMembers(allowed: ["body", "channels", "from", "through"], context: context, element: element, refusal: Refuse, unknownMemberDetail: "an intent holds body, from, through and channels");

        var body = CliStrictJson.ReadRequiredInt32(context: context, element: element, member: "body", refusal: Refuse);
        var from = CliStrictJson.ReadRequiredInt32(context: context, element: element, member: "from", refusal: Refuse);
        var through = CliStrictJson.ReadRequiredInt32(context: context, element: element, member: "through", refusal: Refuse);
        var channels = CliStrictJson.ReadRequiredObject(context: context, element: element, member: "channels", refusal: Refuse);
        var values = new List<(string Channel, FixedQ4816 Value)>();

        if (
            (body < 0) ||
            (from < 1) ||
            (through < from)
        ) {
            throw Refuse(message: $"{context} needs a non-negative body and 1 <= from <= through");
        }

        foreach (var channel in channels.EnumerateObject()) {
            if (
                (channel.Value.ValueKind != JsonValueKind.String) ||
                !FixedQ4816.TryParse(provider: CultureInfo.InvariantCulture, result: out var value, s: channel.Value.GetString())
            ) {
                throw Refuse(message: $"{context}.channels.{channel.Name} must be a decimal string such as \"1\" or \"-0.5\", parsed exactly");
            }

            values.Add(item: (channel.Name, value));
        }

        if (values.Count == 0) {
            throw Refuse(message: $"{context}.channels names no channel");
        }

        return new DeterminismIntent(
            Body: body,
            Channels: values,
            From: from,
            Through: through
        );
    }
    private static DeterminismCellWrite ReadCell(JsonElement element, string context, int ticks) {
        CliStrictJson.RequireObject(context: context, element: element, refusal: Refuse);
        CliStrictJson.RequireOnlyMembers(allowed: ["key", "row", "tick", "value"], context: context, element: element, refusal: Refuse, unknownMemberDetail: "a cell write holds tick, row, key and value");

        var cell = new DeterminismCellWrite(
            Key: CliStrictJson.ReadRequiredString(context: context, element: element, member: "key", refusal: Refuse),
            Row: CliStrictJson.ReadRequiredString(context: context, element: element, member: "row", refusal: Refuse),
            Tick: CliStrictJson.ReadRequiredInt32(context: context, element: element, member: "tick", refusal: Refuse),
            Value: CliStrictJson.ReadRequiredString(context: context, element: element, member: "value", refusal: Refuse)
        );

        if (
            (cell.Tick < 1) ||
            (cell.Tick > ticks)
        ) {
            throw Refuse(message: $"{context}.tick must lie in 1..{ticks}");
        }

        return cell;
    }
    private static DeterminismScenario ReadScenario(JsonElement element, string context, string directory) {
        CliStrictJson.RequireObject(context: context, element: element, refusal: Refuse);
        CliStrictJson.RequireOnlyMembers(allowed: ["cells", "intents", "name", "seats", "ticks", "world"], context: context, element: element, refusal: Refuse, unknownMemberDetail: "a scenario holds name, world, ticks, seats, intents and cells");

        var name = CliStrictJson.ReadRequiredString(context: context, element: element, member: "name", refusal: Refuse);
        var world = CliStrictJson.ReadRequiredString(context: context, element: element, member: "world", refusal: Refuse);
        var ticks = CliStrictJson.ReadRequiredInt32(context: context, element: element, member: "ticks", refusal: Refuse);
        var seats = new List<int>();
        var intents = new List<DeterminismIntent>();

        if (
            (name.Length == 0) ||
            name.Any(predicate: static character => !(char.IsAsciiLetterOrDigit(c: character) || (character == '-')))
        ) {
            throw Refuse(message: $"{context}.name must be ASCII letters, digits and '-'");
        }
        if (
            Path.IsPathRooted(path: world) ||
            world.Contains(value: '\\')
        ) {
            throw Refuse(message: $"{context}.world must be a forward-slashed path relative to the manifest");
        }
        if (
            (ticks < 1) ||
            (ticks > MaxTicks)
        ) {
            throw Refuse(message: $"{context}.ticks must lie in 1..{MaxTicks}");
        }

        foreach (var seat in CliStrictJson.ReadRequiredArray(context: context, element: element, member: "seats", refusal: Refuse).EnumerateArray()) {
            if (
                !seat.TryGetInt32(value: out var slot) ||
                (slot < 0)
            ) {
                throw Refuse(message: $"{context}.seats holds only non-negative seat slots");
            }

            seats.Add(item: slot);
        }

        var index = 0;

        foreach (var intent in CliStrictJson.ReadRequiredArray(context: context, element: element, member: "intents", refusal: Refuse).EnumerateArray()) {
            var read = ReadIntent(context: $"{context}.intents[{index++}]", element: intent);

            if (read.Through > ticks) {
                throw Refuse(message: $"{context}.intents[{(index - 1)}] runs past the scenario's {ticks} ticks");
            }

            intents.Add(item: read);
        }

        var cells = new List<DeterminismCellWrite>();

        index = 0;

        foreach (var cell in CliStrictJson.ReadRequiredArray(context: context, element: element, member: "cells", refusal: Refuse).EnumerateArray()) {
            cells.Add(item: ReadCell(context: $"{context}.cells[{index++}]", element: cell, ticks: ticks));
        }

        return new DeterminismScenario(
            Cells: cells,
            Intents: intents,
            Name: name,
            Seats: seats,
            Ticks: ticks,
            World: Path.GetFullPath(path: Path.Combine(
                path1: directory,
                path2: world
            ))
        );
    }

    /// <summary>Loads a manifest strictly: exactly the members this version defines, every scenario name unique.</summary>
    /// <param name="path">The manifest file.</param>
    /// <param name="manifest">The loaded manifest, or <see langword="null"/>.</param>
    /// <param name="error">Why the manifest is unusable, or empty.</param>
    /// <returns><see langword="true"/> when the manifest loaded.</returns>
    public static bool TryLoad(string path, out DeterminismManifest? manifest, out string error) {
        manifest = null;
        error = string.Empty;

        try {
            var full = Path.GetFullPath(path: path);
            using var document = CliStrictJson.ParseStrict(duplicateDetail: "a manifest names each member once", maxDepth: MaxDepth, path: full, refusal: Refuse);
            var root = CliStrictJson.RequireObject(context: "manifest", element: document.RootElement, refusal: Refuse);

            CliStrictJson.RequireOnlyMembers(allowed: ["scenarios", "schema"], context: "manifest", element: root, refusal: Refuse, unknownMemberDetail: "a manifest holds schema and scenarios");

            var schema = CliStrictJson.ReadRequiredString(context: "manifest", element: root, member: "schema", refusal: Refuse);

            if (!string.Equals(a: schema, b: Schema, comparisonType: StringComparison.Ordinal)) {
                throw Refuse(message: $"manifest.schema is '{schema}', not {Schema}");
            }

            var directory = Path.GetDirectoryName(path: full)!;
            var scenarios = new List<DeterminismScenario>();
            var names = new HashSet<string>(comparer: StringComparer.Ordinal);
            var index = 0;

            foreach (var element in CliStrictJson.ReadRequiredArray(context: "manifest", element: root, member: "scenarios", refusal: Refuse).EnumerateArray()) {
                var scenario = ReadScenario(context: $"scenarios[{index++}]", directory: directory, element: element);

                if (!names.Add(item: scenario.Name)) {
                    throw Refuse(message: $"scenario '{scenario.Name}' is named twice");
                }

                scenarios.Add(item: scenario);
            }

            if (scenarios.Count == 0) {
                throw Refuse(message: "manifest.scenarios is empty");
            }

            manifest = new DeterminismManifest(
                Pin: AssetContentHash.Compute(content: File.ReadAllBytes(path: full)),
                Scenarios: scenarios
            );

            return true;
        } catch (Exception exception) when ((exception is Refusal or JsonException or IOException or UnauthorizedAccessException)) {
            error = exception.Message;

            return false;
        }
    }
}
