using System.CommandLine;
using System.Globalization;

namespace Puck.Cli.Motion;

/// <summary>
/// <c>puck motion</c> — the one command that judges how a body moves. It boots the debug world's real <c>Puck.World</c>
/// (windowed, with no physical device), waits for the bakes, turns lockstep on and runs scenarios from a scenario file
/// on exact ticks, writing each scenario's stills, contact sheets and a manifest. <c>--reproduce</c> runs everything twice
/// in fresh Worlds and reports, per still, whether the poses agree and how many pixels differ.
/// </summary>
public static partial class MotionCommand {
    /// <summary>The scenario file the verb runs when <c>--scenarios</c> names none, repository-relative.</summary>
    public const string DefaultScenarios = "worlds/debug/motion.scenarios.json";
    /// <summary>The world the verb boots when <c>--world</c> names none, repository-relative.</summary>
    public const string DefaultWorld = "worlds/debug";

    private const string Verb = "motion";

    private static int Run(string? scenariosPath, string[] names, bool list, bool reproduce, string? output, string? worldPath, string? worldArtifact, string? backend, int columns, int cellWidth, int timeoutMinutes) {
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

        if (list) {
            foreach (var scenario in set.Scenarios) {
                Console.Out.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{scenario.Name}: {scenario.Form.ToString().ToLowerInvariant()}, {scenario.Capture.Count} still(s), ticks 0..{scenario.LastTick} — {scenario.Note}"));
            }

            return CliExit.Success;
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

        var world = Path.GetFullPath(path: (worldPath ?? Path.Combine(path1: repositoryRoot, path2: DefaultWorld)));
        var directory = Path.GetFullPath(path: (output ?? RunDirectory.CreatePath(prefix: "puck-motion-")));

        if (directory.Contains(comparisonType: StringComparison.Ordinal, value: ' ')) {
            return CliExit.Refuse(verb: Verb, what: "--output", why: "a console line carries its still paths unquoted, so the output directory's path holds no space (pass --output to name one)");
        }

        return RunWorld(
            backend: backend,
            cellWidth: cellWidth,
            columns: columns,
            directory: directory,
            repositoryRoot: repositoryRoot,
            reproduce: reproduce,
            scenarioFile: scenarioFile,
            selected: selected,
            set: set,
            timeoutMinutes: timeoutMinutes,
            world: world,
            worldArtifact: worldArtifact
        );
    }

    /// <summary>Creates the <c>motion</c> verb.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var scenariosOption = new Option<string>(name: "--scenarios") { Description = $"The scenario file; {DefaultScenarios} when omitted." };
        var scenarioOption = new Option<string[]>(name: "--scenario") { AllowMultipleArgumentsPerToken = true, Description = "Run only the named scenario (repeat or list several); every scenario of the file when omitted." };
        var listOption = new Option<bool>(name: "--list") { Description = "Print each scenario's name, form, stills and note, and boot nothing." };
        var reproduceOption = new Option<bool>(name: "--reproduce") { Description = "Run everything twice in fresh Worlds and report, per still, whether the pose hashes are equal and how many pixels differ; exits 1 when a pose hash differs." };
        var outputOption = CliOptions.Output(description: "The directory the stills, sheets, logs and manifest are written to; a kept run directory under the temporary root when omitted.");
        var worldOption = new Option<string>(name: "--world") { Description = $"The world to boot; {DefaultWorld} when omitted." };
        var artifactOption = new Option<string>(name: "--world-artifact") { Description = "Run this Puck.World entry assembly instead of the build of the checkout's sources. Never builds." };
        var backendOption = new Option<string>(name: "--backend") { Description = "The graphics backend the World boots on; its default when omitted." };
        var columnsOption = new Option<int>(name: "--columns") { DefaultValueFactory = static _ => 6, Description = "The most frames in one row of a contact sheet before it wraps to a new band." };
        var cellWidthOption = new Option<int>(name: "--cell-width") { DefaultValueFactory = static _ => 480, Description = "The width each frame of a contact sheet is scaled to, in pixels." };
        var timeoutOption = new Option<int>(name: "--timeout") { DefaultValueFactory = static _ => 25, Description = "The most minutes the whole run may take, a build excepted." };
        var command = new Command(
            description: "Judge how the Moth moves: run tick-pinned scenarios in the real debug World and write stills, contact sheets and a manifest.",
            name: Verb
        ) { scenariosOption, scenarioOption, listOption, reproduceOption, outputOption, worldOption, artifactOption, backendOption, columnsOption, cellWidthOption, timeoutOption };

        command.Detail(detail: $"""
            Boots the real Puck.World on {DefaultWorld} (windowed, always with no physical device, with its own
            state directory), waits for the bakes, selects the scenario file's layout (the debug world's 'motion'
            layout: four cameras riding body 0), turns lockstep on, and runs each scenario from a reset body on
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
            pose hashes are equal and how many pixels differ (count, largest channel difference, mean). A pose hash
            that differs fails the run: the motion itself is not deterministic. Pixel differences are reported and
            never fail it.

            Every slot camera is checked at every still: an eye in geometry, or a sight line to the aim point that meets
            geometry, fails its scenario by name (scenario, tick, camera, what blocked it) and writes no sheet for it.

            Exit codes: 0 every scenario ran clean, 1 a refused line, a missing still, a blocked camera, a body in the
            wrong form or a pose hash that differs, 2 a refusal (a scenario file, a build or a World that did not complete).
            """);
        command.SetAction(action: parseResult => Run(
            backend: parseResult.GetValue(option: backendOption),
            cellWidth: parseResult.GetValue(option: cellWidthOption),
            columns: parseResult.GetValue(option: columnsOption),
            list: parseResult.GetValue(option: listOption),
            names: (parseResult.GetValue(option: scenarioOption) ?? []),
            output: parseResult.GetValue(option: outputOption),
            reproduce: parseResult.GetValue(option: reproduceOption),
            scenariosPath: parseResult.GetValue(option: scenariosOption),
            timeoutMinutes: parseResult.GetValue(option: timeoutOption),
            worldArtifact: parseResult.GetValue(option: artifactOption),
            worldPath: parseResult.GetValue(option: worldOption)
        ));

        return command;
    }
}
