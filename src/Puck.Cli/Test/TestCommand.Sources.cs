using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World.Transpiler;

namespace Puck.Cli.Test;

internal static partial class TestCommand {
    private const string WorldSourceSuffix = ".puck";

    // A generated test world is written outside the tree its source sits in, so a path the document resolves
    // against its own directory has to be rooted before it moves. A `basis` and an `imports[].document` are the two
    // such paths a document carries at its root.
    private static void Reroot(JsonObject world, string sourceDirectory) {
        if (
            (world[propertyName: "basis"]?.GetValue<string>() is { } basis) &&
            !Path.IsPathRooted(path: basis)
        ) {
            world[propertyName: "basis"] = Rooted(
                relative: basis,
                sourceDirectory: sourceDirectory
            );
        }

        foreach (var entry in (world[propertyName: "imports"] as JsonArray ?? [])) {
            if (
                (entry is JsonObject import) &&
                (import[propertyName: "document"]?.GetValue<string>() is { } document) &&
                !Path.IsPathRooted(path: document)
            ) {
                import[propertyName: "document"] = Rooted(
                    relative: document,
                    sourceDirectory: sourceDirectory
                );
            }
        }
    }
    private static string Rooted(string sourceDirectory, string relative) => Path.GetFullPath(path: Path.Combine(
        path1: sourceDirectory,
        path2: relative
    )).Replace(
        newChar: '/',
        oldChar: '\\'
    );
    // Whether a document declares the section `puck test` boots it to. A directory sweep is over whatever the
    // author put there, so an ordinary world beside a test world is skipped rather than failing the sweep.
    private static bool DeclaresSchedule(string document) {
        try {
            return ((JsonNode.Parse(utf8Json: File.ReadAllBytes(path: document)) as JsonObject)?[propertyName: "schedule"] is not null);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException)) {
            return false;
        }
    }
    // Compiles one `.puck` source and writes each test world its `test` blocks generated. The bytes are canonical,
    // so two runs over an unchanged source write byte-identical documents.
    //
    // A generated world is named for its source's stem and its test's slug, so two sources of one sweep sharing a
    // stem can name the same world. `written` holds every world this run generated and the source it came from:
    // the second claim on a name is refused, since writing it would replace the first source's world and run the
    // second one's twice.
    private static bool TryGenerateWorlds(string source, string directory, bool sweeping, Dictionary<string, string> written, out IReadOnlyList<string> worlds, out string? skipped, out string? reason) {
        worlds = [];
        reason = null;
        skipped = null;

        var compilation = WorldCompiler.CompileFile(path: source);

        // A module — a source declaring no schema of its own — is not a world, and a test inside one needs the
        // `use` that stamps it into a world. Skipped by name before its diagnostics are read, since a fragment
        // cannot know what the root that uses it will supply.
        if (compilation.Document?.Schema is null) {
            if (!sweeping) {
                reason = $"{source} declares no schema, so it is a module rather than a world — a test inside a module needs the `use` that stamps it into one, which does not exist yet.";

                return false;
            }

            skipped = $"test: skipped {source} — it declares no schema, so it is a module rather than a world.";

            return true;
        }

        if (compilation.Diagnostics.HasErrors) {
            reason = $"{source} does not compile:{Environment.NewLine}{compilation.Diagnostics.FormatReport(File.ReadAllText(path: source))}";

            return false;
        }

        if (compilation.TestWorlds.Count == 0) {
            if (!sweeping) {
                reason = $"{source} authors no test block — puck test runs the worlds a source's `test` blocks generate, so a source without one has nothing for this verb to do.";

                return false;
            }

            skipped = $"test: skipped {source} — it authors no test block.";

            return true;
        }

        var sourceDirectory = (Path.GetDirectoryName(path: Path.GetFullPath(path: source)) ?? ".");
        var generated = new List<string>(capacity: compilation.TestWorlds.Count);

        _ = Directory.CreateDirectory(path: directory);

        foreach (var world in compilation.TestWorlds) {
            var path = Path.Combine(
                path1: directory,
                path2: (world.Name + WorldDocumentSuffix)
            );

            if (written.TryGetValue(
                key: world.Name,
                value: out var owner
            )) {
                reason = $"{source} and {owner} both generate the test world '{world.Name}' — a generated world is named for its source's file name and its test's name, so two sources run together need different file names or different test names.";

                return false;
            }

            written[world.Name] = source;
            Reroot(
                sourceDirectory: sourceDirectory,
                world: world.Json
            );
            File.WriteAllBytes(
                bytes: CanonicalJsonDocument.Serialize(node: world.Json),
                path: path
            );
            Console.WriteLine(value: $"test: {world.Name} <- {source} test \"{world.Test}\"");
            generated.Add(item: path);
        }

        worlds = generated;

        return true;
    }
    // Every world a path names: a document runs as it stands, a `.puck` source is compiled and its generated test
    // worlds run instead, and a directory contributes both, recursively, skipping what is not a test.
    private static bool TryCollectWorlds(string path, string generatedDirectory, out IReadOnlyList<string> worlds, out string? reason) {
        worlds = [];
        reason = null;

        var rooted = Path.GetFullPath(path: path);

        if (File.Exists(path: rooted)) {
            if (!rooted.EndsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: WorldSourceSuffix
            )) {
                worlds = [rooted];

                return true;
            }

            return TryGenerateWorlds(
                directory: generatedDirectory,
                reason: out reason,
                skipped: out _,
                source: rooted,
                sweeping: false,
                worlds: out worlds,
                written: new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase)
            );
        }

        if (!Directory.Exists(path: rooted)) {
            reason = $"'{path}' is neither a world document, a .puck source, nor a directory.";

            return false;
        }

        var documents = Directory.GetFiles(
            path: rooted,
            searchOption: SearchOption.AllDirectories,
            searchPattern: ("*" + WorldDocumentSuffix)
        );
        var sources = Directory.GetFiles(
            path: rooted,
            searchOption: SearchOption.AllDirectories,
            searchPattern: ("*" + WorldSourceSuffix)
        );

        Array.Sort(
            array: documents,
            comparer: StringComparer.Ordinal
        );
        Array.Sort(
            array: sources,
            comparer: StringComparer.Ordinal
        );

        var collected = new List<string>();
        var skips = new List<string>();
        var written = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var document in documents) {
            if (DeclaresSchedule(document: document)) {
                collected.Add(item: document);
            } else {
                skips.Add(item: $"test: skipped {document} — it declares no schedule section.");
            }
        }

        foreach (var source in sources) {
            if (!TryGenerateWorlds(
                directory: generatedDirectory,
                reason: out var sourceReason,
                skipped: out var skip,
                source: source,
                sweeping: true,
                worlds: out var generated,
                written: written
            )) {
                reason = sourceReason;

                return false;
            }

            if (skip is { }) {
                skips.Add(item: skip);
            }
            collected.AddRange(collection: generated);
        }

        foreach (var skip in skips) {
            Console.WriteLine(value: skip);
        }

        if (collected.Count == 0) {
            reason = $"'{path}' holds no test world: no *{WorldDocumentSuffix} document declaring a schedule, and no *{WorldSourceSuffix} source with a test block.";

            return false;
        }

        worlds = collected;

        return true;
    }
}
