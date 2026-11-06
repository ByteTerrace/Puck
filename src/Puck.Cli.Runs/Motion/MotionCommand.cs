using Puck.World;
using System.CommandLine;
using System.Globalization;

namespace Puck.Cli.Motion;

/// <summary>
/// <c>puck motion</c> — the one command that judges how a body moves. It boots the debug world's real <c>Puck.World</c>
/// (windowed, with no physical device), turns lockstep on before waiting for the bakes and runs scenarios from a scenario file
/// on exact ticks, writing each scenario's stills, contact sheets and a manifest. <c>--reproduce</c> runs everything twice
/// in fresh Worlds and requires matching pose and capture hashes and pixels within the parked per-render residue.
/// </summary>
public static partial class MotionCommand {
    /// <summary>The scenario file the verb runs when <c>--scenarios</c> names none, repository-relative.</summary>
    public const string DefaultScenarios = "worlds/debug/motion.scenarios.json";
    /// <summary>The world the verb boots when <c>--world</c> names none, repository-relative.</summary>
    public const string DefaultWorld = "worlds/debug";

    private const string Verb = "motion";

    private static int Run(string? scenariosPath, string[] names, bool list, bool reproduce, string? output, string? worldPath, string? worldArtifact, WorldBackendPreference? backend, string? cacheDirectory, int columns, int cellWidth, int timeoutSeconds) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        var scenarioFile = Path.GetFullPath(path: (scenariosPath ?? Path.Combine(path1: repositoryRoot, path2: DefaultScenarios)));

        if (!File.Exists(path: scenarioFile)) {
            return CliExit.Refuse(verb: Verb, what: "the scenario file", why: $"{CliPaths.ToDisplay(fullPath: scenarioFile)} does not exist");
        }
        if (!MotionScenarioFile.TryParse(error: out var parseError, json: File.ReadAllText(path: scenarioFile), set: out var set)) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: scenarioFile), why: parseError);
        }

        var selected = new List<MotionScenario>();

        if (names.Length == 0) {
            selected.AddRange(collection: set.Scenarios);
        } else {
            foreach (var name in names) {
                var found = set.Scenarios.FirstOrDefault(predicate: scenario => (scenario.Name == name));

                if (found is null) {
                    return CliExit.Refuse(verb: Verb, what: $"--scenario {name}", why: $"the file has no such scenario (it has {string.Join(separator: ", ", values: set.Scenarios.Select(selector: static scenario => scenario.Name))})");
                }
                if (!selected.Contains(item: found)) {
                    selected.Add(item: found);
                }
            }
        }

        if (selected.Count == 0) {
            return CliExit.Empty(verb: Verb, selection: CliPaths.ToDisplay(fullPath: scenarioFile), why: "declares no scenario");
        }

        if (list) {
            foreach (var scenario in selected) {
                Console.Out.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{scenario.Name}: {scenario.Form.ToString().ToLowerInvariant()}, {scenario.Capture.Count} still(s), ticks 0..{scenario.LastTick} — {scenario.Note}"));
            }

            return CliExit.Success;
        }

        var world = Path.GetFullPath(path: (worldPath ?? Path.Combine(path1: repositoryRoot, path2: DefaultWorld)));
        var directory = Path.GetFullPath(path: (output ?? RunDirectory.CreatePath(prefix: "puck-motion-")));

        if (directory.Contains(comparisonType: StringComparison.Ordinal, value: ' ')) {
            return CliExit.Refuse(verb: Verb, what: "--output", why: "a console line carries its still paths unquoted, so the output directory's path holds no space (pass --output to name one)");
        }

        return RunWorld(
            backend: backend,
            cacheDirectory: ((cacheDirectory is null) ? null : Path.GetFullPath(path: cacheDirectory)),
            cellWidth: cellWidth,
            columns: columns,
            directory: directory,
            repositoryRoot: repositoryRoot,
            reproduce: reproduce,
            scenarioFile: scenarioFile,
            selected: selected,
            set: set,
            timeoutSeconds: timeoutSeconds,
            world: world,
            worldArtifact: worldArtifact
        );
    }

    /// <summary>Creates the <c>motion</c> verb.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var routeOption = new Option<string>(name: "--route") { Description = "Run a generic authored no-capture route twice through its canonical authority tape, checking per-tick presentation equivalence before reporting controlled tick throughput. This is not the normal realtime frame bar." };
        var againstOption = new Option<string>(name: "--against-world-artifact") { Description = "The second route replay's World entry assembly; the first artifact again (A/A) when omitted. Both must implement the same route protocol." };
        var scenariosOption = new Option<string>(name: "--scenarios") { Description = $"The scenario file; {DefaultScenarios} when omitted." };
        var scenarioOption = new Option<string[]>(name: "--scenario") { AllowMultipleArgumentsPerToken = true, Description = "Run only the named scenario (repeat or list several); every scenario of the file when omitted." };
        var listOption = new Option<bool>(name: "--list") { Description = "Print each selected scenario's name, form, stills and note, and boot nothing." };
        var reproduceOption = new Option<bool>(name: "--reproduce") { Description = $"Run everything twice in fresh Worlds; exits 1 for unequal pose or capture hashes, missing camera images, or pixels beyond {MotionReport.MaximumResiduePixels} changed pixels per still and {MotionReport.MaximumResidueLevel} channel level." };
        var outputOption = CliOptions.Output(description: "The directory the stills, sheets, logs and manifest are written to; a kept run directory under the temporary root when omitted.");
        var worldOption = new Option<string>(name: "--world") { Description = $"The world to boot; {DefaultWorld} when omitted." };
        var artifactOption = new Option<string>(name: "--world-artifact") { Description = "Run this Puck.World entry assembly instead of the build of the checkout's sources. Never builds." };
        var backendOption = WorldLaunch.BackendOption(description: "The graphics backend the World boots on: auto, directx or vulkan; the document's when omitted.");
        var cacheOption = new Option<string>(name: "--cache-dir") { Description = "The World's device-cache root, shared by both fresh Worlds; its per-user default when omitted. Use a writable lane directory in a sandbox." };
        var columnsOption = CliOptions.Count(defaultValue: 6, description: "The most frames in one row of a contact sheet before it wraps to a new band.", minimum: 1, name: "--columns");
        var cellWidthOption = CliOptions.Count(defaultValue: 480, description: "The width each frame of a contact sheet is scaled to, in pixels.", maximum: 16384, minimum: 1, name: "--cell-width");
        var timeoutOption = CliOptions.Count(defaultValue: 1500, description: "The most seconds the whole run may take, a build excepted (default 1500).", minimum: 1, name: "--timeout-seconds");
        var command = new Command(
            description: "Judge how the Moth moves: run tick-pinned scenarios in the real debug World and write stills, contact sheets and a manifest.",
            name: Verb
        ) { routeOption, againstOption, scenariosOption, scenarioOption, listOption, reproduceOption, outputOption, worldOption, artifactOption, backendOption, cacheOption, columnsOption, cellWidthOption, timeoutOption };

        command.Detail(detail: $"""
            Boots the real Puck.World on {DefaultWorld} (windowed, always with no physical device, with its own
            state directory), turns lockstep on before waiting for the bakes, selects the scenario file's layout
            (the debug world's 'motion' layout: four cameras riding body 0), and runs each scenario from a reset body on
            exact ticks. The ticks come from the scenario file and one start tick, never from how long the World
            took to boot, so two Worlds pin every step and still to the same ticks. This is a GPU run: take the GPU
            lease first, and never run it beside another GPU job.

            A scenario is a start pose and form (suited or pilot), tick-pinned console lines (body.fly,
            body.press, player.signal, a suit press) and the ticks to capture. {DefaultScenarios} holds the
            default set (walk, run, turn, jump, suited and pilot, flight); add one by editing it. Its format
            is documented in docs/reference/cli.md.

            For each scenario the output directory holds <name>/raw (one PNG per camera per still), <name>/sheet.png
            (every camera, frames left to right, each cell labelled with its camera, tick offset and absolute tick),
            <name>/sheet-<camera>.png (one camera's frames), and manifest.json (each still's tick, capture hash, pose
            hash, body.where read and files). The World's own transcripts are stdout.log and stderr.log.

            --reproduce runs everything twice in fresh Worlds (run-a and run-b) and prints, per still, whether the
            pose and capture hashes are equal and how many pixels differ (count, largest channel difference, mean).
            Both hashes must match. Across all camera files of one still, at most {MotionReport.MaximumResiduePixels}
            pixels may differ, by at most {MotionReport.MaximumResidueLevel} channel level: the parked per-render residue.
            A missing or differently sized camera image fails comparison too. The table and reproduce.json carry
            each still's verdict; any failed comparison exits 1.

            Every slot camera is checked at every still: an eye in geometry, or a sight line to the aim point that meets
            geometry, fails its scenario by name (scenario, tick, camera, what blocked it) and writes no sheet for it.

            Exit codes: 0 every scenario ran clean, 1 a refused line, a missing still, a blocked camera, a body in the
            wrong form or a failed reproduction, 2 a refusal (a scenario file, a build or a World that did not complete).
            """);
        command.SetAction(action: parseResult => {
            if (parseResult.GetValue(routeOption) is { } route) {
                if (parseResult.GetValue(listOption) || parseResult.GetValue(reproduceOption) ||
                    parseResult.GetValue(scenariosOption) is not null || (parseResult.GetValue(scenarioOption)?.Length ?? 0) != 0) {
                    return CliExit.Refuse(Verb, "the route selection", "--route is separate from still scenarios, --list and --reproduce; it always checks two fresh replay legs.");
                }
                return RunRoute(route, parseResult.GetValue(outputOption), parseResult.GetValue(worldOption),
                    parseResult.GetValue(artifactOption), parseResult.GetValue(againstOption), parseResult.GetValue(backendOption),
                    parseResult.GetValue(cacheOption), parseResult.GetValue(timeoutOption));
            }
            if (parseResult.GetValue(againstOption) is not null) { return CliExit.Refuse(Verb, "--against-world-artifact", "requires --route."); }
            return Run(
            backend: parseResult.GetValue(option: backendOption),
            cacheDirectory: parseResult.GetValue(option: cacheOption),
            cellWidth: parseResult.GetValue(option: cellWidthOption),
            columns: parseResult.GetValue(option: columnsOption),
            list: parseResult.GetValue(option: listOption),
            names: (parseResult.GetValue(option: scenarioOption) ?? []),
            output: parseResult.GetValue(option: outputOption),
            reproduce: parseResult.GetValue(option: reproduceOption),
            scenariosPath: parseResult.GetValue(option: scenariosOption),
            timeoutSeconds: parseResult.GetValue(option: timeoutOption),
            worldArtifact: parseResult.GetValue(option: artifactOption),
            worldPath: parseResult.GetValue(option: worldOption)
            );
        });

        return command;
    }
}
