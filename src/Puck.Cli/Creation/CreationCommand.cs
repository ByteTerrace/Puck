using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;

using Puck.World;
using Puck.World.Authoring;
using Puck.World.Authoring.Sculpting;

namespace Puck.Cli.Creation;

/// <summary>
/// <c>puck creation</c> — the offline twin of the in-engine <c>creation.sculpt</c>/<c>creation.sculpts</c> console
/// verbs: <c>sculpts</c> lists the registry, <c>sculpt &lt;name&gt; --world &lt;path&gt;</c> applies a sculpt's
/// <see cref="SculptPatch"/> to a world file on disk (refusing and leaving it untouched on a validation failure),
/// and <c>stats --world &lt;path&gt; [--prototype &lt;id&gt;]</c> reports a creation's shape budget and feature
/// usage. Exit codes: 0 succeeded, 1 the patched document was refused (sculpt) or the file failed to load (stats),
/// 2 a usage error (unknown sculpt/prototype name, missing file).
/// </summary>
internal static class CreationCommand {
    public static Command Create() => new(description: "Author creations through the sculpting library — list, apply, and inspect.", name: "creation") {
        SculptsCommand(),
        SculptCommand(),
        StatsCommand(),
    };

    private static Command SculptsCommand() {
        var command = new Command(description: "Lists the registered creation sculpts.", name: "sculpts");

        command.SetAction(action: _ => {
            if (CreationSculptRegistry.All.Count == 0) {
                Console.Out.WriteLine(value: "creation sculpts: none registered");

                return 0;
            }

            foreach (var sculpt in CreationSculptRegistry.All) {
                Console.Out.WriteLine(value: $"{sculpt.Name}: {sculpt.Description}");
            }

            return 0;
        });

        return command;
    }
    private static Command SculptCommand() {
        var nameArgument = new Argument<string>(name: "name") { Description = "The registered sculpt's name." };
        var worldOption = new Option<string>(name: "--world") { Description = "The world document to patch in place.", Required = true };
        var command = new Command(description: "Applies a sculpt's patch to a world file on disk: validates the result before writing, and refuses (leaving the file untouched) on a validation failure.", name: "sculpt") { nameArgument, worldOption };

        command.SetAction(action: parseResult => RunSculpt(
            name: parseResult.GetValue(argument: nameArgument)!,
            worldPath: parseResult.GetValue(option: worldOption)!
        ));

        return command;
    }
    private static Command StatsCommand() {
        var worldOption = new Option<string>(name: "--world") { Description = "The world document to inspect.", Required = true };
        var prototypeOption = new Option<string?>(name: "--prototype") { Description = "Limit to one prototype id (default: every prototype carrying a creation document)." };
        var command = new Command(description: "Reports a creation's per-stamp shape count against the budget, counts by primitive/blend, which shapes use domain/onion/twist/bend/rounding, and palette slot usage.", name: "stats") { worldOption, prototypeOption };

        command.SetAction(action: parseResult => RunStats(
            prototypeId: parseResult.GetValue(option: prototypeOption),
            worldPath: parseResult.GetValue(option: worldOption)!
        ));

        return command;
    }

    private static int RunSculpt(string name, string worldPath) {
        if (!CreationSculptRegistry.TryGet(
            name: name,
            sculpt: out var sculpt
        )) {
            Console.Error.WriteLine(value: $"creation sculpt: unknown sculpt '{name}' — {KnownSculpts()}");

            return 2;
        }

        var fullPath = Path.GetFullPath(path: worldPath);

        if (!File.Exists(path: fullPath)) {
            Console.Error.WriteLine(value: $"creation sculpt: '{fullPath}' does not exist.");

            return 2;
        }

        JsonObject document;

        try {
            document = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: fullPath))!.AsObject();
        } catch (Exception exception) when (exception is JsonException or InvalidOperationException) {
            Console.Error.WriteLine(value: $"creation sculpt: '{fullPath}' is not a valid JSON object: {exception.Message}");

            return 1;
        }

        var working = document.DeepClone().AsObject();
        var context = new SculptContext(Document: document);
        IReadOnlyList<SculptPatchResult> results;

        try {
            results = sculpt.Sculpt(context: context).Apply(document: working);
        } catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException or JsonException) {
            Console.Error.WriteLine(value: $"creation sculpt: refused — patch fault: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return 1;
        }

        foreach (var result in results) {
            Console.Out.WriteLine(value: $"{result.Kind} {result.Path}: {result.Verdict}");
        }

        WorldDefinition definition;

        try {
            definition = WorldDefinitionSerialization.Deserialize(utf8Json: JsonSerializer.SerializeToUtf8Bytes(value: working));
        } catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException) {
            Console.Error.WriteLine(value: $"creation sculpt: refused — {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return 1;
        }

        WorldDefinitionSerialization.Save(
            definition: definition,
            path: fullPath
        );
        Console.Out.WriteLine(value: $"creation sculpt: wrote {CliPaths.ToDisplay(fullPath: fullPath)}.");

        return 0;
    }
    private static string KnownSculpts() {
        var names = string.Join(separator: ", ", values: CreationSculptRegistry.All.Select(selector: static s => s.Name));

        return ((names.Length > 0) ? names : "none registered");
    }

    private static int RunStats(string worldPath, string? prototypeId) {
        var fullPath = Path.GetFullPath(path: worldPath);

        if (!WorldDefinitionFileSource.TryLoadLocally(
            contentHash: out _,
            definition: out var definition,
            path: fullPath,
            reason: out var reason
        )) {
            Console.Error.WriteLine(value: $"creation stats: '{fullPath}' failed to load — {reason}");

            return 1;
        }

        var prototypes = definition!.Creations
            .Where(predicate: p => (p.Document is not null))
            .Where(predicate: p => ((prototypeId is null) || string.Equals(a: p.Id, b: prototypeId, comparisonType: StringComparison.Ordinal)))
            .ToList();

        if (prototypes.Count == 0) {
            Console.Error.WriteLine(value: (prototypeId is null)
                ? $"creation stats: '{fullPath}' declares no prototype carrying a creation document."
                : $"creation stats: '{fullPath}' names no prototype '{prototypeId}' with a creation document.");

            return 2;
        }

        foreach (var prototype in prototypes) {
            ReportPrototype(
                document: prototype.Document!,
                id: prototype.Id
            );
        }

        return 0;
    }
    private static void ReportPrototype(string id, CreationDocument document) {
        var shapes = (document.Shapes ?? []);
        var stampShapes = document.StampShapeCount();

        Console.Out.WriteLine(value: $"[{id}] shapes: {shapes.Count}, stamp budget: {stampShapes}/{WorldPlacementPolicy.MaxShapesPerStamp}");

        WriteHistogram(
            label: "primitive",
            values: shapes.Select(selector: static s => s.Type.ToString())
        );
        WriteHistogram(
            label: "blend",
            values: shapes.Select(selector: static s => (s.Blend?.ToString() ?? "Union"))
        );
        Console.Out.WriteLine(value: $"  domain: {shapes.Count(predicate: static s => (s.Domain is { Count: > 0 }))}, onion: {shapes.Count(predicate: static s => (s.Onion is > 0))}, twist: {shapes.Count(predicate: static s => (s.Twist is not (null or 0f)))}, bend: {shapes.Count(predicate: static s => (s.Bend is not (null or 0f)))}, rounding: {shapes.Count(predicate: static s => (s.Rounding is > 0))}, dilate: {shapes.Count(predicate: static s => (s.Dilate is > 0))}, lift: {shapes.Count(predicate: static s => (s.Lift is not null))}, chamfer: {shapes.Count(predicate: static s => (s.Chamfer is > 0))}, panel: {shapes.Count(predicate: static s => (s.Panel is not null))}");

        if (document.Palette is { Count: > 0 } palette) {
            var usage = new int[palette.Count];

            foreach (var shape in shapes) {
                if ((shape.Material is { } slot) && (slot >= 0) && (slot < usage.Length)) {
                    usage[slot]++;
                }
            }

            Console.Out.WriteLine(value: $"  palette: {string.Join(separator: ", ", values: usage.Select(selector: (count, index) => $"{index}={count}"))}");
        }
    }
    private static void WriteHistogram(string label, IEnumerable<string> values) {
        var counts = values
            .GroupBy(keySelector: static v => v)
            .OrderByDescending(keySelector: static g => g.Count())
            .Select(selector: g => $"{g.Key}={g.Count()}");

        Console.Out.WriteLine(value: $"  {label}: {string.Join(separator: ", ", values: counts)}");
    }
}
