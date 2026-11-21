using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Puck.Confinement;
using Puck.World.Transpiler.Composition;
using Puck.World.Transpiler.Lowering;
using Puck.World;
using Puck.World.Transpiler;

namespace Puck.Cli.Test;

public static partial class TestCommand {
    // Whether a document declares the section `puck test` boots it to. A directory sweep is over whatever the
    // author put there, so an ordinary world beside a test world is skipped rather than failing the sweep.
    private static bool DeclaresSchedule(string document) {
        try {
            return ((JsonNode.Parse(utf8Json: File.ReadAllBytes(path: document)) as JsonObject)?[propertyName: "schedule"] is not null);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException)) {
            return false;
        }
    }
    // Compiles one `.puck` source and writes its generated test worlds, or its authored scheduled document carrying
    // verdict rows. Both use the same staging path, preserving basis resolution outside the temporary directory.
    //
    // A generated world is named for its source's stem and its test's slug, so two sources of one sweep sharing a
    // stem can name the same world. `written` holds every world this run generated and the source it came from:
    // the second claim on a name is refused, since writing it would replace the first source's world and run the
    // second one's twice.
    private static bool TryGenerateWorlds(string source, string directory, bool sweeping, Dictionary<string, string> written, CliGlob[] filters, out int filtered, out IReadOnlyList<TestWorld> worlds, out string? skipped, out string? reason) {
        filtered = 0;
        worlds = [];
        reason = null;
        skipped = null;

        // Generated test bytes live only while this source is staged: held for the session, every generated document
        // would stay alive for the entire sweep.
        _ = WorldCompileSession.Shared.TryCompile(
            compiled: out var compiled,
            failure: out var failure,
            hold: false,
            includeTests: true,
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
            if (compiled.EmitsDocument && (compiled.Document is { } bytes) &&
                (JsonNode.Parse(utf8Json: bytes) is JsonObject authored) &&
                (authored["schedule"] is JsonObject) &&
                (authored["state"]?["world"] is JsonArray rows) &&
                rows.OfType<JsonObject>().Any(predicate: static row => (row["verdict"] is JsonObject))) {
                var name = Path.GetFileNameWithoutExtension(path: source);
                if (!Selected(filters, name)) { filtered++; return true; }

                if (written.TryGetValue(key: name, value: out var owner)) {
                    reason = $"{source} and {owner} both generate the test world '{name}' — sources run together need different document names.";
                    return false;
                }
                written[name] = source;
                if (!TryStageTestWorld(
                    foreign: [],
                    generatedDirectory: directory,
                    name: name,
                    path: out var staged,
                    reason: out var composeReason,
                    siblings: [],
                    sourceDirectory: (Path.GetDirectoryName(path: Path.GetFullPath(path: source)) ?? "."),
                    world: authored
                )) {
                    reason = $"{source} scheduled verdict world does not compose: {composeReason}";
                    return false;
                }
                Console.WriteLine(value: $"test: {name} <- {source} (authored schedule and verdicts)");
                worlds = [new TestWorld(Name: name, Path: staged)];
                return true;
            }
            if (!sweeping) {
                reason = $"{source} authors no test block or scheduled verdict world — puck test runs generated tests and authored documents carrying both a schedule and verdict rows.";

                return false;
            }

            skipped = $"test: skipped {source} — it authors no test block or scheduled verdict world.";

            return true;
        }

        var sourceDirectory = (Path.GetDirectoryName(path: Path.GetFullPath(path: source)) ?? ".");
        var generated = new List<TestWorld>(capacity: compiled.Tests.Count);

        foreach (var world in compiled.Tests) {
            if (!Selected(filters, world.Name, world.Test)) { filtered++; continue; }
            // A test over a composition is several documents: the one the run boots with, plus one per world its
            // schedule arms. Every one is written; only the boot document is a world this verb runs.
            foreach (var name in ((IEnumerable<string>)[world.Name, .. world.Siblings.Select(selector: static sibling => sibling.Name)])) {
                if (written.TryGetValue(
                    key: name,
                    value: out var owner
                )) {
                    reason = $"{source} and {owner} both generate the test world '{name}' — a generated world is named for its source's file name and its test's name, so two sources run together need different file names or different test names.";

                    return false;
                }

                written[name] = source;
            }

            if (!TryStageTestWorld(
                foreign: world.Foreign,
                generatedDirectory: directory,
                name: world.Name,
                path: out var path,
                reason: out var composeReason,
                siblings: [.. world.Siblings.Select(selector: static sibling => (sibling.Name, sibling.Json))],
                sourceDirectory: sourceDirectory,
                world: ((JsonObject)JsonNode.Parse(utf8Json: world.Json)!)
            )) {
                reason = $"{source} test \"{world.Test}\" does not compose: {composeReason}";

                return false;
            }

            Console.WriteLine(value: $"test: {world.Name} <- {source} test \"{world.Test}\"{((world.Subject is { } subject)
                ? $" with {subject}"
                : string.Empty)}");

            foreach (var sibling in world.Siblings) {
                Console.WriteLine(value: $"test: {sibling.Name} <- {source} test \"{world.Test}\" (armed beside {world.Name})");
            }

            generated.Add(item: new TestWorld(
                Foreign: world.Foreign,
                Name: world.Name,
                Path: path
            ));
        }

        worlds = generated;

        return true;
    }
    // Every world a path names: a document runs as it stands, a `.puck` source contributes its generated tests or
    // authored scheduled verdict document, and a directory recursively skips worlds carrying neither.
    private static bool Selected(CliGlob[] filters, string name, string? test = null) => filters.Length == 0 || filters.Any(pattern => pattern.IsMatch(name) || (test is not null && pattern.IsMatch(test)));

    private static bool TryCollectWorlds(string path, string generatedDirectory, CliGlob[] filters, out int filtered, out IReadOnlyList<TestWorld> worlds, out string? reason) {
        filtered = 0;
        worlds = [];
        reason = null;

        var rooted = Path.GetFullPath(path: path);

        if (File.Exists(path: rooted)) {
            if (!WorldDocumentName.IsSourceFile(path: rooted)) {
                var world = TestWorld.ForDocument(path: rooted);
                if (Selected(filters, world.Name)) { worlds = [world]; } else { filtered++; }

                return true;
            }

            return TryGenerateWorlds(
                filters: filters,
                filtered: out filtered,
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
        if (!WorldCompileSession.TryCarriers(
            carriers: out var carriers,
            directory: rooted,
            libraries: out var libraries,
            option: SearchOption.AllDirectories,
            reason: out var carrierReason
        )) {
            reason = carrierReason;

            return false;
        }

        var collected = new List<TestWorld>();
        var skips = new List<string>();
        var written = new Dictionary<string, string>(comparer: DocumentName.Comparer);

        foreach (var document in carriers.Where(predicate: static carrier => !carrier.IsSource).Select(selector: static carrier => carrier.Path)) {
            if (DeclaresSchedule(document: document)) {
                var world = TestWorld.ForDocument(path: document);
                if (Selected(filters, world.Name)) { collected.Add(item: world); } else { filtered++; }
            } else {
                skips.Add(item: $"test: skipped {document} — it declares no schedule section.");
            }
        }

        // A composition carries each world it declares, so its file may carry several names and runs once.
        foreach (var source in carriers.Where(predicate: static carrier => carrier.IsSource).Select(selector: static carrier => carrier.Path).Distinct(comparer: StringComparer.Ordinal).Concat(second: libraries).Order(comparer: StringComparer.Ordinal)) {
            if (!TryGenerateWorlds(
                filters: filters,
                filtered: out var sourceFiltered,
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
            filtered += sourceFiltered;
            collected.AddRange(collection: generated);
        }

        foreach (var skip in skips) {
            Console.WriteLine(value: skip);
        }

        if (collected.Count == 0 && filtered == 0) {
            reason = $"'{path}' holds no test world: no *{WorldDocumentName.DocumentSuffix} document declaring a schedule, and no *{WorldDocumentName.SourceSuffix} source with a test block or scheduled verdict world.";

            return false;
        }

        worlds = collected;

        return true;
    }
    // Stages one test world into a directory of its own, as the documents it tests stand beside each other: the world
    // as the file its documentId names, each world its schedule arms, and every document a staged world's references
    // reach from the directory that world was authored in, composed beside them. A crossing onto a sibling lands in a
    // world staged here, and a sibling's way back naming the test world's id resolves to the test world's own file, so
    // the crossing returns to the instance the leg booted rather than starting a second one.
    //
    // `foreign` are the test's expectations about sibling documents: each is lowered into the document of its name as
    // that document is reached, so the verdict is judged in the world a crossing starts.
    private static bool TryStageTestWorld(string name, JsonObject world, IReadOnlyList<(string Name, byte[] Json)> siblings, IReadOnlyList<WorldTestForeign> foreign, string sourceDirectory, string generatedDirectory, out string path, out string? reason) {
        var catalog = CliWorldVocabulary.EnsureInstalled();
        var directory = Path.Combine(
            path1: generatedDirectory,
            path2: name
        );

        path = string.Empty;

        // A kept run directory staged before holds this world's last staging, which this one replaces whole.
        ConfinedDirectory.RemoveTree(path: directory);

        // Test worlds are temporary JSON documents, staged flattened so the executable boots the exact composed source
        // without depending on the temporary file's location.
        if (!WorldStaging.TryWrite(
            catalog: catalog,
            directory: directory,
            name: name,
            path: out var staged,
            reason: out reason,
            sourceDirectory: sourceDirectory,
            world: world
        )) {
            return false;
        }

        // A documentId that cannot name a file stays under the generated name, and the boot refuses it by name.
        var ownId = (((Staged(path: staged)["documentId"]?.GetValue<string>() is { } documentId) && WorldDocumentName.TryValidate(name: documentId, reason: out _))
            ? documentId
            : name
        );

        path = Path.Combine(
            path1: directory,
            path2: WorldDocumentName.DocumentFile(name: ownId)
        );
        File.Move(
            destFileName: path,
            overwrite: true,
            sourceFileName: staged
        );

        // The staged worlds' references resolve beside the directory they were authored in.
        string Beside(string document) => Path.Combine(path1: sourceDirectory, path2: WorldDocumentName.DocumentFile(name: document));
        var roots = new List<(JsonObject World, string Path)> { (Staged(path: path), Beside(document: ownId)) };

        foreach (var (siblingName, siblingJson) in siblings) {
            if (!Stage(document: siblingName, world: ((JsonObject)JsonNode.Parse(utf8Json: siblingJson)!), from: sourceDirectory, staged: out var siblingPath, reason: out reason)) {
                return false;
            }
            roots.Add(item: (Staged(path: siblingPath), Beside(document: siblingName)));
        }
        // A document the test expects verdicts from carries them as it is staged; the source's compile already refused
        // an expectation about a document this walk does not reach.
        foreach (var (document, resolved, found) in WorldCompileSession.Shared.ReachDocuments(known: [ownId, .. siblings.Select(selector: static sibling => sibling.Name)], roots: roots)) {
            var matched = foreign.Where(predicate: expectation => string.Equals(a: expectation.Document, b: document, comparisonType: StringComparison.Ordinal)).ToArray();

            if (matched.Length > 0) {
                var inheritedState = ((((found["basis"] is not null) || (found["imports"] is not null)) &&
                    WorldCompileSession.Shared.TryCompose(composed: out var composedFound, reason: out _, rootBytes: Encoding.UTF8.GetBytes(s: found.ToJsonString()), rootResolvedPath: resolved))
                        ? (composedFound?["state"] as JsonObject)
                        : null
                );

                foreach (var expectation in matched) {
                    if (!expectation.TryApply(inheritedState: inheritedState, reason: out reason, world: found)) {
                        return false;
                    }
                }
            }
            if (!Stage(document: document, world: found, from: (Path.GetDirectoryName(path: resolved) ?? sourceDirectory), staged: out _, reason: out reason)) {
                return false;
            }
        }

        reason = null;

        return true;

        bool Stage(string document, JsonObject world, string from, out string staged, out string? reason) {
            if (WorldStaging.TryWrite(catalog: catalog, directory: directory, name: document, path: out staged, reason: out var stageReason, sourceDirectory: from, world: world)) {
                reason = null;

                return true;
            }
            reason = $"the document '{document}' beside it does not compose: {stageReason}";

            return false;
        }
    }
    // A document WorldStaging wrote, which is a JSON object.
    private static JsonObject Staged(string path) => JsonNode.Parse(utf8Json: File.ReadAllBytes(path: path))!.AsObject();
}
