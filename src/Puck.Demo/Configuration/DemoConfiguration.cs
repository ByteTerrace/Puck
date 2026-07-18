using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Puck.Demo.Configuration;

/// <summary>
/// The demo's configuration composition root: it layers the run's configuration sources in the required precedence
/// (scenario / <c>appsettings.json</c> &lt; environment variables &lt; command line) and binds the typed options
/// (<see cref="ScenarioOptions"/> and the launcher's supported <c>PUCK_*</c> runtime toggles).
/// <para>The environment map carries the launcher and engine diagnostic toggles
/// (<c>PUCK_PRESENT_TIMING</c>/<c>PUCK_GENLOCK</c>/<c>PUCK_TEST_DEVICE_LOSS</c>): a small explicit map turns each into
/// its configuration key, added as an in-memory source ABOVE the JSON files (so environment overrides file config) and
/// BELOW the command-line <c>--scenario-set</c> overrides (so the command line wins).</para>
/// <para>
/// THE CONFIGURATION VOCABULARY RULE, so a new type doesn't have to re-derive it: a config-bound POCO — one that
/// <c>services.Configure&lt;T&gt;</c> binds straight from a configuration section — is named <c>*Options</c>
/// (<see cref="ScenarioOptions"/>). <see cref="Puck.Demo.HostSettings"/> is the one
/// deliberate exception: it is DERIVED (resolved from the run document's host section plus CLI-flag fallback, then
/// applied to the DI container and registered as its own singleton), not bound from a configuration section, so it
/// keeps the <c>*Settings</c> name to signal that difference rather than pretending to be a POCO a binder produced.
/// There is exactly one direction between the environment and typed config: <c>PUCK_*</c> environment variables flow
/// INTO configuration here (env→config); nothing flows back out (no config→env re-push) — a deep reader that still
/// wants an env var (e.g. <see cref="Puck.SdfVm.SdfEngineNode"/>'s <c>PUCK_RAY_QUERY</c> read)
/// gets an optional constructor argument that falls back to the environment read when the caller has nothing
/// resolved to pass, rather than the reverse plumbing. A static accessor over an options object for a
/// coupling-ceiling escape (<see cref="ScenarioAccessor"/>) is neither of the above — it owns no state — and is
/// named for what it is (an accessor), not <c>*Settings</c>.
/// </para>
/// <para>
/// THE <c>*CliSeams</c> CONVENTION (also blessed here): a CLI surface that would otherwise push an entry point's
/// <c>Main</c> over its class-coupling ceiling declares its <c>System.CommandLine</c> options and dispatch off a
/// static type named <c>*CliSeams</c> (<see cref="ScenarioCliSeams"/>, <see cref="Puck.Demo.Forge.ForgeCliSeams"/>) —
/// the entry point pays one property reference per option and one resolve/dispatch call, nothing more. The next CLI
/// surface that would grow <c>Main</c>'s coupling should follow this shape rather than re-deriving the pattern.
/// </para>
/// </summary>
internal static class DemoConfiguration {
    // Each supported PUCK_* environment variable maps to the configuration key it feeds. Boolean values are
    // normalized to "true" or "false" as they are copied in (see ReadEnvironmentOverrides).
    private static readonly (string Env, string Key, bool Boolean)[] s_environmentMap = [
        ("PUCK_PRESENT_TIMING", "Launcher:LogPresentTiming", true),
        ("PUCK_GENLOCK", "Launcher:Genlock", false),
        ("PUCK_TEST_DEVICE_LOSS", "Launcher:SyntheticDeviceLossSeconds", false),
    ];

    /// <summary>Resolves the scenario, layers the configuration sources, and binds the typed options — the demo's whole
    /// configuration composition in one call so the entry point (whose <c>Main</c> is at its coupling ceiling) names
    /// only this type.</summary>
    /// <param name="builder">The application builder (its <see cref="IConfigurationManager"/> + services).</param>
    /// <param name="scenario">The raw <c>--scenario</c> value (a bare name or a path), or null.</param>
    /// <param name="scenarioSets">The <c>--scenario-set Key=Value</c> command-line overrides.</param>
    /// <returns>The scenario's <c>ExitAfterSeconds</c> (0 when none / unset), or -1 when a named scenario could not
    /// be resolved (a hard failure the caller should exit on).</returns>
    public static int Configure(IHostApplicationBuilder builder, string? scenario, IReadOnlyList<string> scenarioSets) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(scenarioSets);

        if (!ScenarioCliSeams.TryResolveScenarioPath(scenario: scenario, resolvedPath: out var scenarioPath)) {
            return -1;
        }

        AddDemoSources(configuration: builder.Configuration, scenarioPath: scenarioPath, scenarioSets: scenarioSets);
        AddScenarioOptions(services: builder.Services, configuration: builder.Configuration);

        return builder.Configuration.GetSection(key: ScenarioOptions.Section).GetValue<int>(key: nameof(ScenarioOptions.ExitAfterSeconds));
    }

    // Layers the configuration sources for the run. The scenario JSON (when resolved) sits just above the (currently
    // absent) appsettings.json, the mapped environment overrides sit above that, and the command-line --scenario-set
    // pairs sit on top.
    private static void AddDemoSources(IConfigurationManager configuration, string? scenarioPath, IReadOnlyList<string> scenarioSets) {

        // appsettings.json is optional and sits at the bottom of the demo layers (below the scenario file), so a
        // committed default can exist without one being required.
        _ = configuration.AddJsonFile(path: "appsettings.json", optional: true, reloadOnChange: false);

        if (scenarioPath is not null) {
            _ = configuration.AddJsonFile(path: scenarioPath, optional: false, reloadOnChange: false);
            // The flag path is authoritative: a resolved scenario is active regardless of what the file says.
            _ = configuration.AddInMemoryCollection(initialData: [new(key: "Scenario:Active", value: "true")]);
        }

        // Environment overrides the file config (added above the JSON so its values win).
        _ = configuration.AddInMemoryCollection(initialData: ReadEnvironmentOverrides());

        // Command line wins over everything (the repeatable --scenario-set Key=Value pairs).
        _ = configuration.AddInMemoryCollection(initialData: ParseScenarioSets(scenarioSets: scenarioSets));
    }

    // Binds the scenario review harness options from configuration into the container.
    private static void AddScenarioOptions(IServiceCollection services, IConfiguration configuration) {
        _ = services.Configure<ScenarioOptions>(config: configuration.GetSection(key: ScenarioOptions.Section));
    }

    /// <summary>Resolves the launcher's runtime toggles from configuration into the launcher's own POCO (which the
    /// launcher reads instead of the <c>PUCK_*</c> environment). Called from <see cref="HostSettings"/> where the
    /// <c>LauncherOptions</c> is assembled.</summary>
    /// <param name="configuration">The composed configuration.</param>
    /// <returns>The launcher runtime toggles.</returns>
    public static (bool GenlockEnabled, bool LogPresentTiming, double? SyntheticDeviceLossSeconds) ResolveLauncherRuntime(IConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(key: "Launcher");
        // PUCK_GENLOCK=0 disabled genlock; any other value (or unset) leaves it on — preserve that exactly.
        var genlockRaw = section["Genlock"];
        var genlockEnabled = !string.Equals(a: genlockRaw, b: "0", comparisonType: StringComparison.Ordinal);
        var logPresentTiming = section.GetValue<bool>(key: "LogPresentTiming");
        var deviceLossRaw = section["SyntheticDeviceLossSeconds"];
        var deviceLossSeconds = ((double.TryParse(s: deviceLossRaw, result: out var seconds) && (seconds > 0.0)) ? (double?)seconds : null);

        return (genlockEnabled, logPresentTiming, deviceLossSeconds);
    }

    private static IEnumerable<KeyValuePair<string, string?>> ReadEnvironmentOverrides() {
        foreach (var (env, key, boolean) in s_environmentMap) {
            if (Environment.GetEnvironmentVariable(variable: env) is not { } value) {
                continue;
            }

            // A "=1 turns it on" toggle becomes a proper boolean; PUCK_GENLOCK is passed through raw (its "0"
            // sentinel is interpreted in ResolveLauncherRuntime). Everything else is copied verbatim.
            yield return new KeyValuePair<string, string?>(
                key: key,
                value: (boolean ? (string.Equals(a: value, b: "1", comparisonType: StringComparison.Ordinal) ? "true" : "false") : value)
            );
        }
    }
    private static IEnumerable<KeyValuePair<string, string?>> ParseScenarioSets(IReadOnlyList<string> scenarioSets) {
        foreach (var pair in scenarioSets) {
            if (string.IsNullOrWhiteSpace(value: pair)) {
                continue;
            }

            var split = pair.IndexOf(value: '=');

            if (split <= 0) {
                Console.Error.WriteLine(value: $"[scenario-set] ignoring '{pair}' — expected Key=Value (e.g. Scenario:Creation=lantern-fish).");

                continue;
            }

            yield return new KeyValuePair<string, string?>(
                key: pair[..split].Trim(),
                value: pair[(split + 1)..]
            );
        }
    }
}
