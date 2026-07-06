using System.CommandLine;
using Microsoft.Extensions.Hosting;

namespace Puck.Demo.Configuration;

/// <summary>
/// The scenario CLI surface, housed OUTSIDE <c>Program</c> (whose <c>Main</c> sits at its maintainability and
/// complexity ceilings, exactly like the forge surface in <c>ForgeCliSeams</c>): the <c>--scenario</c> and repeatable
/// <c>--scenario-set</c> options declare here, and <c>Program</c> pays one property reference per option plus a single
/// resolution call. Resolving a bare scenario name to <c>scenarios/&lt;name&gt;.json</c> (falling back to a direct
/// path) and threading the file + the <c>--scenario-set</c> pairs into the configuration layering also lives here.
/// </summary>
internal static class ScenarioCliSeams {
    // The --scenario option: a bare scenario name (resolved to scenarios/<name>.json next to the executable) or a
    // direct path to a scenario JSON.
    private static readonly Option<string?> s_scenarioOption = new(name: "--scenario") {
        DefaultValueFactory = static _ => null,
        Description = "A creation-review SCENARIO — a repeatable creator-mode capture session (a bare name resolves to scenarios/<name>.json shipped next to the demo, or a direct path). It loads a creation into creator mode and writes the plan's camera shots as numbered PNGs. Demo tooling, separate from --run (the gated engine document). Combine with --scenario-set Key=Value to override any field per run.",
    };

    // The repeatable --scenario-set Key=Value option: per-run overrides fed into configuration ABOVE the scenario file
    // and the environment (e.g. --scenario-set Scenario:Creation=lantern-fish).
    private static readonly Option<string[]> s_scenarioSetOption = new(name: "--scenario-set") {
        AllowMultipleArgumentsPerToken = false,
        DefaultValueFactory = static _ => [],
        Description = "A Key=Value override for the active --scenario, applied above the scenario file and the environment (repeatable). E.g. --scenario-set Scenario:Creation=lantern-fish overrides which creation the review scenario loads without editing the file.",
    };

    /// <summary>Adds the scenario options to the demo's root command (kept off <c>Program</c> so its <c>Main</c> never
    /// names the option types).</summary>
    /// <param name="command">The root command.</param>
    public static void AddOptions(Command command) {
        ArgumentNullException.ThrowIfNull(command);

        command.Options.Add(item: s_scenarioOption);
        command.Options.Add(item: s_scenarioSetOption);
    }

    /// <summary>Resolves the scenario, layers the configuration, and binds the typed options — the demo's whole
    /// configuration composition behind one call so <c>Program.Main</c> names only this seam.</summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="parseResult">The parsed command line.</param>
    /// <returns>The scenario's <c>ExitAfterSeconds</c> (0 when none / unset), or -1 when a named scenario could not be
    /// resolved (a hard failure the caller should exit on).</returns>
    public static int Configure(IHostApplicationBuilder builder, ParseResult parseResult) {
        ArgumentNullException.ThrowIfNull(parseResult);

        return DemoConfiguration.Configure(
            builder: builder,
            scenario: parseResult.GetValue(option: s_scenarioOption),
            scenarioSets: (parseResult.GetValue(option: s_scenarioSetOption) ?? [])
        );
    }

    /// <summary>Resolves the <c>--scenario</c> token to a concrete file path (or null when the flag is absent),
    /// erroring to stderr and returning a not-found sentinel when a named/pathed scenario does not exist.</summary>
    /// <param name="scenario">The raw <c>--scenario</c> value.</param>
    /// <param name="resolvedPath">The resolved existing file path, or null when the flag was absent.</param>
    /// <returns><see langword="true"/> when resolution succeeded (including the no-flag case);
    /// <see langword="false"/> when a scenario was named but no file exists.</returns>
    public static bool TryResolveScenarioPath(string? scenario, out string? resolvedPath) {
        resolvedPath = null;

        if (string.IsNullOrWhiteSpace(value: scenario)) {
            return true;
        }

        if (File.Exists(path: scenario)) {
            resolvedPath = scenario;

            return true;
        }

        // A bare name resolves to scenarios/<name>.json alongside the executable (the committed, output-copied files),
        // then relative to the current directory as a convenience for ad-hoc scenarios.
        var candidates = new[] {
            Path.Combine(path1: AppContext.BaseDirectory, path2: "scenarios", path3: $"{scenario}.json"),
            Path.Combine(path1: "scenarios", path2: $"{scenario}.json"),
            $"{scenario}.json",
        };

        foreach (var candidate in candidates) {
            if (File.Exists(path: candidate)) {
                resolvedPath = candidate;

                return true;
            }
        }

        Console.Error.WriteLine(value: $"[scenario] no scenario named '{scenario}' — looked for scenarios/{scenario}.json (next to the demo and in the working directory) and a direct path.");

        return false;
    }
}
