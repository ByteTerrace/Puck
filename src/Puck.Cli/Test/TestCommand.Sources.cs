using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Puck.World.Transpiler.Composition;
using Puck.World;

namespace Puck.Cli.Test;

internal static partial class TestCommand {
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

        // The source compiles through the compile cache the composer and the game share, so an unchanged source
        // run again is not compiled again; a source that does not compile is compiled for its diagnostics.
        _ = WorldCompileCache.Shared.TryCompile(
            compiled: out var compiled,
            failure: out var failure,
            path: source
        );

        // A module — a source declaring no schema of its own — is not a world, and is skipped by name before its
        // diagnostics are read, since a fragment cannot know what the root that uses it will supply. A module that
        // carries a test naming the module and the arguments to stand it up with does have something to run.
        if (((compiled?.Schema ?? failure?.Document?.Schema) is null) && ((compiled?.Tests.Count ?? failure!.TestWorlds.Count) == 0)) {
            if (!sweeping) {
                reason = $"{source} declares no schema, so it is a module rather than a world, and authors no test block of its own — a module's tests are written `test \"name\" with {Path.GetFileNameWithoutExtension(path: source)}(arguments)`, and its own run wherever it is used.";

                return false;
            }

            skipped = $"test: skipped {source} — it declares no schema, so it is a module rather than a world, and authors no test block.";

            return true;
        }

        if (failure is not null) {
            reason = $"{source} does not compile:{Environment.NewLine}{failure.Diagnostics.FormatReport(File.ReadAllText(path: source))}";

            return false;
        }

        if (compiled!.Tests.Count == 0) {
            if (!sweeping) {
                reason = $"{source} authors no test block — puck test runs the worlds a source's `test` blocks generate, so a source without one has nothing for this verb to do.";

                return false;
            }

            skipped = $"test: skipped {source} — it authors no test block.";

            return true;
        }

        var sourceDirectory = (Path.GetDirectoryName(path: Path.GetFullPath(path: source)) ?? ".");
        var generated = new List<string>(capacity: compiled.Tests.Count);

        foreach (var world in compiled.Tests) {
            // A test over a composition is several documents: the one the run boots with, plus one per world its
            // schedule arms. Every one is written; only the boot document is a world this verb runs.
            foreach (var document in ((IReadOnlyList<(string Name, byte[] Json)>)[
                (world.Name, world.Json),
                .. world.Siblings.Select(selector: static sibling => (sibling.Name, sibling.Json)),
            ])) {
                if (written.TryGetValue(
                    key: document.Name,
                    value: out var owner
                )) {
                    reason = $"{source} and {owner} both generate the test world '{document.Name}' — a generated world is named for its source's file name and its test's name, so two sources run together need different file names or different test names.";

                    return false;
                }

                written[document.Name] = source;

                // Test worlds are temporary JSON documents, staged flattened so the executable boots the exact
                // composed source without depending on the temporary file's location.
                if (!WorldStaging.TryWrite(
                    directory: directory,
                    name: document.Name,
                    path: out var path,
                    reason: out var composeReason,
                    sourceDirectory: sourceDirectory,
                    world: ((JsonObject)JsonNode.Parse(utf8Json: document.Json)!)
                )) {
                    reason = $"{source} test \"{world.Test}\" does not compose: {composeReason}";

                    return false;
                }

                if (document.Name == world.Name) {
                    Console.WriteLine(value: $"test: {world.Name} <- {source} test \"{world.Test}\"{((world.Subject is { } subject)
                        ? $" with {subject}"
                        : string.Empty)}");
                    generated.Add(item: path);
                } else {
                    Console.WriteLine(value: $"test: {document.Name} <- {source} test \"{world.Test}\" (armed beside {world.Name})");
                }
            }
        }

        worlds = generated;

        return true;
    }
    // Every world a path names: a document runs as it stands, a `.puck` source is compiled and its generated test
    // worlds run instead, and a directory contributes the file carrying each document, recursively, skipping what is
    // not a test.
    private static bool TryCollectWorlds(string path, string generatedDirectory, out IReadOnlyList<string> worlds, out string? reason) {
        worlds = [];
        reason = null;

        var rooted = Path.GetFullPath(path: path);

        if (File.Exists(path: rooted)) {
            if (!WorldDocumentName.IsSourceFile(path: rooted)) {
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
                written: new Dictionary<string, string>(comparer: DocumentName.Comparer)
            );
        }

        if (!Directory.Exists(path: rooted)) {
            reason = $"'{path}' is neither a world document, a .puck source, nor a directory.";

            return false;
        }

        // One file per document, as the composer resolves a name: a document file beside the source of its name
        // emitting that name is never read. A module library carries no document name, and a composition only the
        // worlds it declares, so a document named like either runs, and the library is swept for the tests its modules
        // carry.
        if (!PuckDocumentComposer.TryCarriers(
            carriers: out var carriers,
            directory: rooted,
            libraries: out var libraries,
            option: SearchOption.AllDirectories,
            reason: out var carrierReason
        )) {
            reason = carrierReason;

            return false;
        }

        var collected = new List<string>();
        var skips = new List<string>();
        var written = new Dictionary<string, string>(comparer: DocumentName.Comparer);

        foreach (var document in carriers.Where(predicate: static carrier => !carrier.IsSource).Select(selector: static carrier => carrier.Path)) {
            if (DeclaresSchedule(document: document)) {
                collected.Add(item: document);
            } else {
                skips.Add(item: $"test: skipped {document} — it declares no schedule section.");
            }
        }

        // A composition carries each world it declares, so its file may carry several names and runs once.
        foreach (var source in carriers.Where(predicate: static carrier => carrier.IsSource).Select(selector: static carrier => carrier.Path).Distinct(comparer: StringComparer.Ordinal).Concat(second: libraries).Order(comparer: StringComparer.Ordinal)) {
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
            reason = $"'{path}' holds no test world: no *{WorldDocumentName.DocumentSuffix} document declaring a schedule, and no *{WorldDocumentName.SourceSuffix} source with a test block.";

            return false;
        }

        worlds = collected;

        return true;
    }
}
