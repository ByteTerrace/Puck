using System.CommandLine;
using System.Text.Json;
using Puck.Commands;
using Puck.World;

namespace Puck.Cli.Vocabulary;

// The `puck verbs` verb: boots this checkout's Puck.World headless over the debug world, reads its `world.affordances`
// manifest, and writes the engine verbs (never the world's authored commands) as the console verb catalog
// Puck.World.Vocabulary embeds, or under --check compares. The catalog is what `puck compile --validate` checks a
// document's `commands` rows against when no World is running.
// Exit 0 wrote or matched, 1 check found drift or the boot failed, 2 usage error or missing repository root.
public static class VerbsCommand {
    private const string AffordancesPrefix = "[world.affordances: ";
    private const string RelativePath = "src/Puck.World.Vocabulary/Assets/puck.console.verbs.json";
    private const string Verb = "verbs";

    // The affordance echo's command array, the engine's own verbs alone.
    private static List<CommandMetadata>? ReadAffordances(string stdout) {
        var line = stdout.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n').FirstOrDefault(predicate: static candidate => candidate.StartsWith(value: AffordancesPrefix, comparisonType: StringComparison.Ordinal));
        var end = (line?.IndexOf(value: ",\"channels\":", comparisonType: StringComparison.Ordinal) ?? -1);

        if (end < 0) {
            return null;
        }

        using var json = JsonDocument.Parse(json: line![AffordancesPrefix.Length..end]);

        return [.. json.RootElement.EnumerateArray()
            .Where(predicate: static verb => !(verb.TryGetProperty(propertyName: "authored", value: out var authored) && authored.GetBoolean()))
            .Select(selector: static verb => new CommandMetadata(
                Audience: ((verb.GetProperty(propertyName: "audience").GetString() == "operator") ? CommandAudience.Operator : CommandAudience.Anyone),
                Bindability: (verb.GetProperty(propertyName: "bindable").GetBoolean() ? CommandBindability.Bindable : CommandBindability.Unbindable),
                Name: verb.GetProperty(propertyName: "name").GetString()!,
                Routing: CommandRouting.Immediate,
                ValueKind: CommandValueKind.Digital
            ))];
    }
    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var runDirectory = Directory.CreateTempSubdirectory(prefix: "puck-verbs-").FullName;

        try {
            if (!WorldArtifactBuild.TryResolve(
                artifact: out var world,
                error: out var error,
                logDirectory: runDirectory,
                repositoryRoot: repositoryRoot,
                timeout: TimeSpan.FromSeconds(value: 300),
                verb: Verb
            )) {
                Console.Error.WriteLine(value: $"{Verb}: {error}");

                return 1;
            }

            CliProcessResult process;

            using (world) {
                process = CliProcess.RunCaptured(
                    arguments: [
                        world.Path,
                        "--world", Path.Combine(paths: [repositoryRoot, "worlds", "debug"]),
                        "--headless", "true",
                        "--exit-after-seconds", "60",
                        "--state-dir", Path.Combine(path1: runDirectory, path2: "state"),
                    ],
                    fileName: "dotnet",
                    input: $"world.affordances{Environment.NewLine}quit{Environment.NewLine}",
                    timeout: TimeSpan.FromSeconds(value: 120),
                    workingDirectory: runDirectory
                );
            }

            if (process.TimedOut || (process.ExitCode != 0) || (ReadAffordances(stdout: process.Stdout) is not { Count: > 0 } verbs)) {
                Console.Error.WriteLine(value: $"{Verb}: the headless Puck.World boot {(process.TimedOut ? "timed out" : $"exited {process.ExitCode}")} without a world.affordances manifest; its stderr ends: {string.Join(separator: " | ", values: process.Stderr.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n').Where(predicate: static line => (line.Trim().Length != 0)).TakeLast(count: 4))}");

                return 1;
            }

            return (CliGeneratedFile.WriteOrCheck(
                check: check,
                detail: $" ({verbs.Count} verbs)",
                relativePath: RelativePath,
                repositoryRoot: repositoryRoot,
                source: "the headless World's world.affordances",
                text: WorldConsoleVerbCatalog.Render(verbs: verbs),
                verb: Verb
            )
                ? 0
                : 1);
        } finally {
            Directory.Delete(path: runDirectory, recursive: true);
        }
    }

    public static Command Create() => CliOptions.CheckVerb(
        checkDescription: "Boot, regenerate in memory and compare against src/Puck.World.Vocabulary/Assets/puck.console.verbs.json; write nothing, and exit 1 naming the first differing line.",
        description: "Generate or check the console verb catalog authored commands are validated against offline.",
        name: Verb,
        run: Run
    ).Detail(detail: """
        Builds this checkout's Puck.World (Release), boots it headless over worlds/debug, and
        reads its world.affordances manifest: every registered verb with its bindability and
        audience. The engine's verbs, never the world's authored commands, are written as the
        catalog Puck.World.Vocabulary embeds (WorldConsoleVerbCatalog), which `puck compile
        --validate` checks a document's `commands` rows against when no World is running: a
        command taking an engine verb's name, or a line calling an operator or unbindable verb,
        is refused offline. A verb only a windowed World registers is absent, and the live
        registry still checks it at boot and reload. Regenerate after adding, renaming or
        changing the bindability or audience of a console verb.

        Written to: src/Puck.World.Vocabulary/Assets/puck.console.verbs.json.
        """);
}
