using System.CommandLine;
using System.Text.Json;
using Puck.World;

namespace Puck.Cli.Vocabulary;

// The `puck verbs` verb: boots this checkout's Puck.World over the debug world headless and windowed, reads each
// boot's `world.affordances` manifest, and writes the union of the engine verbs (never the world's authored commands)
// as the console verb catalog Puck.World.Vocabulary embeds, or under --check compares. Some modules register only in
// a rendered World, so neither boot alone holds every verb. --headless-only boots headless alone and keeps every
// catalogued verb it does not register, so a host without a free GPU can refresh the headless verbs.
// Exit 0 wrote or matched, 1 check found drift or a boot failed, 2 usage error or missing repository root.
public static class VerbsCommand {
    private const string RelativePath = "src/Puck.World.Vocabulary/Assets/puck.console.verbs.json";
    private const string Verb = "verbs";

    private static int Run(bool check, bool headlessOnly, string? worldArtifact, string? cacheDirectory) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        using var run = RunDirectory.Create(prefix: "puck-verbs-");
        var runDirectory = run.Path;
        if (!WorldConsoleVerbs.TryEnumerate(repositoryRoot: repositoryRoot, runDirectory: runDirectory,
            verb: Verb, verbs: out var rows, error: out var error, headlessOnly: headlessOnly,
            worldArtifact: worldArtifact, cacheDirectory: cacheDirectory)) {
            Console.Error.WriteLine(value: $"{Verb}: {error}");
            return 1;
        }
        var verbs = rows.Values.Select(selector: WorldConsoleVerbCatalog.ReadVerb)
            .ToDictionary(keySelector: static metadata => metadata.Name, comparer: StringComparer.Ordinal);

        // A headless refresh keeps the verbs only a rendered World registers, read from the catalog it replaces.
        if (headlessOnly) {
            var path = Path.Combine(path1: repositoryRoot, path2: RelativePath);

            if (File.Exists(path: path)) {
                using var existing = JsonDocument.Parse(json: File.ReadAllText(path: path));

                foreach (var (name, verb) in WorldConsoleVerbCatalog.Parse(json: existing)) {
                    // Compiled binding handles are internal destinations, never authored console vocabulary.
                    if (name.StartsWith(comparisonType: StringComparison.Ordinal, value: "channel.logical.")) { continue; }
                    _ = verbs.TryAdd(key: name, value: verb);
                }
            }
        }

        return run.Conclude(exitCode: (CliGeneratedFile.WriteOrCheck(
            check: check,
            detail: $" ({verbs.Count} verbs)",
            relativePath: RelativePath,
            repositoryRoot: repositoryRoot,
            source: (headlessOnly ? "the headless World's world.affordances and the catalog's other verbs" : "the headless and windowed Worlds' world.affordances"),
            text: WorldConsoleVerbCatalog.Render(verbs: verbs.Values),
            verb: Verb
        )
            ? 0
            : 1));
    }

    public static Command Create() {
        var worldArtifact = new Option<string?>(name: "--world-artifact") { Description = "Use this already-built Puck.World.dll instead of building the checkout." };
        var cacheDirectory = new Option<string?>(name: "--cache-dir") { Description = "The device-cache root for both World boots; absent uses the per-user store." };
        var check = CliOptions.Check(description: "Boot, regenerate in memory and compare against src/Puck.World.Vocabulary/Assets/puck.console.verbs.json; write nothing, and exit 1 naming the first differing line.");
        var headlessOnly = new Option<bool>(name: "--headless-only") {
            Description = "Boot headless alone (no GPU) and keep every catalogued verb the headless World does not register.",
        };
        var command = new Command(
            description: "Generate or check the console verb catalog authored commands are validated against.",
            name: Verb
        ) { check, headlessOnly, worldArtifact, cacheDirectory };

        command.SetAction(action: parseResult => Run(check: parseResult.GetValue(option: check), headlessOnly: parseResult.GetValue(option: headlessOnly), worldArtifact: parseResult.GetValue(option: worldArtifact), cacheDirectory: parseResult.GetValue(option: cacheDirectory)));

        return command.Detail(detail: """
            Builds this checkout's Puck.World (Release), or uses --world-artifact <Puck.World.dll>,
            boots it over worlds/debug headless and
            then windowed (a GPU leg), and reads each boot's world.affordances manifest: every
            registered verb with its bindability and audience. The union of the engine's verbs,
            never the world's authored commands, is written as the catalog Puck.World.Vocabulary
            embeds (WorldConsoleVerbCatalog). `puck compile --validate` checks a document's
            `commands` rows against it where no World runs, refusing an engine-verb name, an
            operator or unbindable line and a line calling a verb no boot shape registers; a live
            registry reads it so a line naming a verb only a rendered World registers passes a
            headless boot and is refused at dispatch there instead. Regenerate after adding,
            renaming or changing the bindability or audience of a console verb.

            Written to: src/Puck.World.Vocabulary/Assets/puck.console.verbs.json.
            --cache-dir names the device-cache root both boots use instead of the per-user store.
            """);
    }
}
