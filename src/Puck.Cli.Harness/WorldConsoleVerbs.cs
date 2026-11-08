using System.ComponentModel;
using System.Text.Json;

namespace Puck.Cli;

/// <summary>Reads the live console vocabulary from the structured affordance manifest of each boot shape.</summary>
public static class WorldConsoleVerbs {
    private const string Prefix = "[world.affordances: ";

    /// <summary>Resolves a World build and unions its headless and, unless omitted, windowed vocabulary.
    /// Authored commands are included only when requested; the engine catalog excludes them.</summary>
    public static bool TryEnumerate(string repositoryRoot, string runDirectory, string verb,
        out Dictionary<string, JsonElement> verbs, out string error, bool headlessOnly = false,
        bool includeAuthored = false, string? worldArtifact = null, string? cacheDirectory = null) {
        verbs = new(comparer: StringComparer.Ordinal);
        if (!WorldLaunch.TryResolve(named: worldArtifact, build: out var world, error: out error,
            logDirectory: runDirectory, repositoryRoot: repositoryRoot,
            spirv: (!headlessOnly && WorldLaunch.NeedsSpirv(backend: null, shape: WorldShape.Windowed)), verb: verb)) {
            return false;
        }

        using (world) {
            foreach (var headless in (headlessOnly ? new[] { true } : new[] { true, false })) {
                var shape = (headless ? "headless" : "windowed");
                Console.Error.WriteLine(value: $"{verb}: booting Puck.World {shape} to read world.affordances.");
                try {
                    using var launch = WorldLaunch.Admit(request: new WorldLaunchRequest(Artifact: world.Path, Step: verb) {
                        Arguments = ((cacheDirectory is null) ? [] : ["--cache-dir", cacheDirectory]),
                        ExitAfterSeconds = 60,
                        Shape = (headless ? WorldShape.Headless : WorldShape.Windowed),
                        StateDirectory = Path.Combine(path1: runDirectory, path2: $"state-{shape}"),
                        WorkingDirectory = runDirectory,
                        World = Path.Combine(paths: [repositoryRoot, "worlds", "debug"]),
                    });
                    var process = launch.Run(input: "world.affordances\nwire.errors\nquit\n", timeout: TimeSpan.FromSeconds(value: 120));
                    File.WriteAllText(path: Path.Combine(path1: runDirectory, path2: $"{shape}-stdout.log"), contents: process.Stdout);
                    File.WriteAllText(path: Path.Combine(path1: runDirectory, path2: $"{shape}-stderr.log"), contents: process.Stderr);
                    var found = Read(stdout: process.Stdout, includeAuthored: includeAuthored);
                    if (process.TimedOut || (process.ExitCode != 0) || (found.Count == 0) ||
                        !process.OutputLines.Any(predicate: static line => (line.Stream == CliProcessOutputStream.Stdout) && (line.Line == "[wire.errors: 0 rejected]"))) {
                        error = $"the {shape} Puck.World boot {(process.TimedOut ? "timed out" : $"exited {process.ExitCode}")} without an accepted world.affordances manifest; transcripts are at {CliPaths.ToDisplay(fullPath: runDirectory)}.";
                        return false;
                    }
                    foreach (var row in found) { verbs[row.GetProperty(propertyName: "name").GetString()!] = row; }
                } catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or TimeoutException or JsonException or KeyNotFoundException or ArgumentException) {
                    error = $"could not enumerate the {shape} Puck.World boot: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
                    return false;
                }
            }
        }
        error = string.Empty;
        return true;
    }

    private static List<JsonElement> Read(string stdout, bool includeAuthored) {
        var line = CliText.Lines(text: stdout).FirstOrDefault(predicate: static candidate => candidate.StartsWith(comparisonType: StringComparison.Ordinal, value: Prefix));
        if ((line is null) || !line.EndsWith(value: ']')) { return []; }
        using var json = JsonDocument.Parse(json: line[Prefix.Length..^1]);
        return [.. json.RootElement.GetProperty(propertyName: "commands").EnumerateArray()
            .Where(predicate: row => includeAuthored || !(row.TryGetProperty(propertyName: "authored", value: out var authored) && authored.GetBoolean()))
            .Select(selector: static row => row.Clone())];
    }
}
