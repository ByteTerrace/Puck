using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Bench;
using Puck.Commands;
using Puck.Demo;
using Puck.Scene;

var backendOption = new Option<string>(name: "--backend") {
    DefaultValueFactory = static _ => "vulkan",
    Description = "The graphics backend to start with: vulkan (default) or directx.",
};
var exitAfterSecondsOption = new Option<int>(name: "--exit-after-seconds") {
    DefaultValueFactory = static _ => 30,
    Description = "Seconds before the demo auto-exits; 0 or less runs until the window is closed.",
};
var captureOption = new Option<string?>(name: "--capture") {
    DefaultValueFactory = static _ => null,
    Description = "Optional PNG path; captures the first rendered frame by reading the actual render target back from the GPU (no desktop scrape) and writing it there.",
};
var validateOverworldOption = new Option<bool>(name: "--validate-overworld") {
    Description = "Runs the overworld determinism + replay self-check (pure CPU: a scripted input run — roster churn + console boots — twice must produce identical per-tick state hashes, and a record->replay must reproduce them bit-for-bit) and exits (0 pass, 1 divergence, 2 infra-fail).",
};
var overworldOption = new Option<bool>(name: "--overworld") {
    Description = "Renders the OVERWORLD — the demo, and the default with no flags at all: a controller-driven player immersed inside four StandingMachines (the showcase cartridge on the DMG/CGB/AGB costumes of the one machine). Walk with the left stick, jump with South, and press North at a machine to boot it — each boot lights its pane and the screen walks its staged split. Vulkan host.",
};
var romOption = new Option<string?>(name: "--rom") {
    DefaultValueFactory = static _ => null,
    Description = "Path to a cartridge ROM (.gb/.gbc): boot straight INTO the game — the IMMERSED overworld. Each connecting controller (up to 4) seats its player at their own machine running this cartridge; with --rom-exit, any player reaching the condition breaks the fourth wall and reveals the room, everyone standing at their stands with the games continuing on the in-world screens.",
};
var romExitOption = new Option<string?>(name: "--rom-exit") {
    DefaultValueFactory = static _ => null,
    Description = "Fourth-wall condition for --rom, as <0xADDR><op><value> over work RAM (0xC000-0xDFFF), e.g. \"0xDA22>=1\" = a representative cartridge's save flag going nonzero.",
};
var runOption = new Option<string?>(name: "--run") {
    DefaultValueFactory = static _ => null,
    Description = "Path to a data-driven run document (run.json). Its graph selects the producer and its scene + viewports drive the render, replacing the world/showcase flags. Hosts on the backend the document's host section selects (Vulkan or DirectX).",
};
var emitSchemaOption = new Option<string?>(name: "--emit-schema") {
    DefaultValueFactory = static _ => null,
    Description = "Headless utility: write the run-document JSON Schema to the given path and exit (no window).",
};
var timingOption = new Option<bool>(name: "--timing") {
    Description = "Arms per-pass GPU timing for this run — sugar for the document's host.timing:true. Forces timing on even when the loaded/synthesized document leaves it unset.",
};
var benchOption = new Option<string?>(name: "--bench") {
    DefaultValueFactory = static _ => null,
    Description = "Headless CI/proof twin of the bench.run console verb: boots the default overworld document (or --run's, with the same overrides layered on) with host.presentMode immediate, host.presentRate display, host.timing true, and host.exitAfterSeconds 0; submits 'bench.run <suite>' before the host starts; exits when the suite completes (0 = clean scored run, 1 = abort/refusal/no-timestamps).",
};
var benchSamplesOption = new Option<bool>(name: "--bench-samples") {
    Description = "With --bench: retains each scene's raw per-frame sample arrays in the puck.bench.v1 report (the 'samples' bench.run modifier). No effect without --bench.",
};
var benchCompareOption = new Option<string[]?>(name: "--bench-compare") {
    AllowMultipleArgumentsPerToken = true,
    Arity = new ArgumentArity(minimumNumberOfValues: 2, maximumNumberOfValues: 2),
    Description = "Headless tool mode (like --emit-schema): compare two puck.bench.v1 reports and print the per-scene diff table, then exit — no window/host/GPU. Each argument is a report path or the alias 'latest'/'prev' (filename sort under bench-reports/). Exit 0 = clean compare, 2 = parse/compat refusal. A regression-gating exit code for CI is deliberately deferred.",
};
// The forge tool-mode options + dispatch live in Puck.Demo.Forge.ForgeCliSeams — Main is at its class-coupling
// and maintainability ceilings, so the forge surface pays one property reference per option here and one await
// below, nothing more.
var presentModeOption = new Option<string>(name: "--present-mode") {
    DefaultValueFactory = static _ => "vsync",
    Description = "The swapchain present mode (both backends honor it): vsync (default), mailbox, immediate, or adaptive (VRR).",
};
var surfaceFormatOption = new Option<string>(name: "--surface-format") {
    DefaultValueFactory = static _ => "r8g8b8a8",
    Description = "The back-buffer surface format (both backends honor it): r8g8b8a8 (default) or b8g8r8a8.",
};
var launchCommand = new RootCommand(description: "Puck Demo") {
    overworldOption,
    backendOption,
    benchOption,
    benchCompareOption,
    benchSamplesOption,
    captureOption,
    emitSchemaOption,
    exitAfterSecondsOption,
    Puck.Demo.Forge.ForgeCliSeams.ForgeOption,
    Puck.Demo.Forge.ForgeCliSeams.AvatarOption,
    Puck.Demo.Forge.ForgeCliSeams.AvatarFromOption,
    Puck.Demo.Forge.ForgeCliSeams.AvatarMovementModeOption,
    Puck.Demo.Forge.ForgeCliSeams.FlagshipsOption,
    Puck.Demo.Forge.ForgeCliSeams.TownOption,
    Puck.Demo.Forge.ForgeCliSeams.BakeOption,
    Puck.Demo.Forge.ForgeCliSeams.BakeCalibrationOption,
    Puck.Demo.Forge.ForgeCliSeams.BakeStressOption,
    Puck.Demo.Forge.ForgeCliSeams.CameraOption,
    Puck.Demo.Forge.ForgeCliSeams.VolleyOption,
    Puck.Demo.Forge.ForgeCliSeams.BrickfallOption,
    Puck.Demo.Forge.ForgeCliSeams.OracleOption,
    Puck.Demo.Forge.ForgeCliSeams.CritterSwapOption,
    Puck.Demo.Forge.ForgeCliSeams.ChromaOption,
    Puck.Demo.Forge.ForgeCliSeams.SolitaireOption,
    Puck.Demo.Forge.ForgeCliSeams.PokerOption,
    Puck.Demo.Forge.ForgeCliSeams.TuneOption,
    Puck.Demo.Forge.ForgeCliSeams.TuneFromOption,
    presentModeOption,
    romExitOption,
    romOption,
    runOption,
    surfaceFormatOption,
    timingOption,
    validateOverworldOption,
};
// The scenario options live in their seam (Program's Main is at its ceilings), added here rather than named inline.
Puck.Demo.Configuration.ScenarioCliSeams.AddOptions(command: launchCommand);
var parseResult = launchCommand.Parse(args);
// Fail loudly on an unrecognized/invalid option rather than silently falling through to the default world render (a
// removed --validate-* flag, a typo, or a bad value). Otherwise a stale script or CI job that still passes a retired
// gate flag would get a 30-second live window and a misleading exit 0. Headless utilities are checked next.
if (!DemoRunRegistrar.ReportParseErrors(parseResult: parseResult)) {
    return 1;
}
// Headless utilities short-circuit before any window/host is created.
var emitSchemaPath = parseResult.GetValue(option: emitSchemaOption);
if (emitSchemaPath is not null) {
    return DemoRunRegistrar.EmitSchema(path: emitSchemaPath);
}
// --bench-compare <a> <b>: the cross-run diff tool mode — a pure report reader, so (like --emit-schema) it exits
// before any host/GPU/window exists. Exit 0 clean, 2 on a parse/compat refusal.
var benchComparePaths = parseResult.GetValue(option: benchCompareOption);
if (benchComparePaths is { Length: 2 }) {
    return BenchReportComparer.Run(pathA: benchComparePaths[0], pathB: benchComparePaths[1], writer: Console.Out).ExitCode;
}
// Every forge tool mode (SDF art, camera, the framework games, tunes, bakes, avatars, flagships) dispatches
// through ForgeCliSeams — one nullable call, with the whole option surface and RomForge coupling housed there
// (Main is at its class-coupling and maintainability ceilings).
if (await Puck.Demo.Forge.ForgeCliSeams.TryRunAsync(args: args, parseResult: parseResult) is { } forgeSeamExit) {
    return forgeSeamExit;
}
var backend = (parseResult.GetValue(option: backendOption) ?? "vulkan");
var benchSamples = parseResult.GetValue(option: benchSamplesOption);
var benchSuite = parseResult.GetValue(option: benchOption);
var capturePath = parseResult.GetValue(option: captureOption);
var presentMode = (parseResult.GetValue(option: presentModeOption) ?? "vsync");
var runPath = parseResult.GetValue(option: runOption);
var surfaceFormat = (parseResult.GetValue(option: surfaceFormatOption) ?? "r8g8b8a8");
var timing = parseResult.GetValue(option: timingOption);
var builder = Host.CreateApplicationBuilder(args: args);
var services = builder.Services;

// Layer + bind the run's configuration in one call: it resolves the review SCENARIO (if any), layers the sources
// (scenario/appsettings JSON < supported PUCK_* environment variables < command-line --scenario-set overrides), binds the
// typed options, and reports the scenario's exit-after (0 when none / not set). A negative result means a named
// scenario could not be found — a hard, loud failure rather than a silent live window.
var scenarioExitAfterSeconds = Puck.Demo.Configuration.ScenarioCliSeams.Configure(builder: builder, parseResult: parseResult);
if (scenarioExitAfterSeconds < 0) {
    return 1;
}

// The scenario's exit-after-seconds (when set) wins over the CLI default so a run stays alive through its last shot;
// otherwise the CLI --exit-after-seconds stands.
var exitAfterSeconds = ((scenarioExitAfterSeconds > 0) ? scenarioExitAfterSeconds : parseResult.GetValue(option: exitAfterSecondsOption));

// Every run flows through one data-driven path: a --run document, or CLI options synthesized into the same
// document model. The document then drives the host, the producer/gate, and the exit code — the flags are thin
// document-building aliases, so there is no second imperative path to keep in sync.
var runDocument = ((runPath is not null)
    ? DemoRunRegistrar.LoadRunDocument(runPath: runPath)
    : DemoRunDocuments.Synthesize(
        backend: backend,
        exitAfterSeconds: exitAfterSeconds,
        presentMode: presentMode,
        surfaceFormat: surfaceFormat,
        validateOverworld: parseResult.GetValue(option: validateOverworldOption),
        overworld: parseResult.GetValue(option: overworldOption),
        romPath: parseResult.GetValue(option: romOption),
        romExit: parseResult.GetValue(option: romExitOption)
    ));

// A failed --run load is already reported by LoadRunDocument; a synthesized document is always valid.
if (runDocument is null) {
    return 2;
}

// --timing is sugar for host.timing:true, applied uniformly whether the document came from --run or was
// synthesized — the same "flags are thin document overrides" doctrine as every field HostSettings resolves.
if (timing) {
    runDocument = WithHostOverrides(document: runDocument, timing: true);
}

// --bench <suite>: the headless CI/proof twin of the bench.run console verb (plan §9). It never fights an explicit
// --run document — these host overrides (automatic display pacing, immediate present mode, armed timing, no
// auto-exit) layer onto whichever document is already resolved, so a --bench --run pairing benches the LOADED
// document's content instead of silently discarding it. BenchBootRequest is the cross-agent handshake the demo's
// BenchInstaller reads after composition to submit 'bench.run <suite>' and wire the process exit code.
if (benchSuite is not null) {
    runDocument = WithHostOverrides(document: runDocument, exitAfterSeconds: 0, presentMode: "immediate", presentRate: "display", timing: true);

    BenchBootRequest.ExitWhenComplete = true;
    BenchBootRequest.IncludeSamples = benchSamples;
    BenchBootRequest.Suite = benchSuite;
}

// A validation run installs a gate that renders OFFSCREEN on Vulkan, so it forces a Vulkan host regardless of the
// host section.
var isOffscreenRun = (runDocument.Validation is not null);
var hostSettings = HostSettings.FromDocument(flagBackend: backend, flagExitAfterSeconds: exitAfterSeconds, flagPresentMode: presentMode, flagSurfaceFormat: surfaceFormat, host: runDocument.Host);
// The window host follows the document's resolved backend, gated by the graph's Direct3D 12 requirement (DXR for rt)
// so it never diverges from the node's device; an offscreen gate forces Vulkan.
var startWithDirectX = (!isOffscreenRun && hostSettings.ResolveHostsOnDirectX(directXAvailable: DemoRunRegistrar.HostDirectXAvailable(document: runDocument)));
// Pre-flight the graph against the RESOLVED host: a deferred/retired affordance (cross-backend produce, graph.child,
// an un-hosted viewport source) exits here with an attributed error, never a mid-host crash.
if (DemoRunRegistrar.ReportGraphUnsupported(document: runDocument, hostsOnDirectX: startWithDirectX)) {
    return 2;
}
// Window size, launcher cadence, neutral presentation prefs, and the feature toggles, all from the resolved host
// config plus the launcher's PUCK_* runtime toggles (now bound from configuration). Presentation registers before the
// presenters so it wins their TryAdd defaults.
// Window size, launcher cadence, neutral presentation prefs, and the launcher's PUCK_* runtime toggles (bound from
// configuration) apply to the builder; the rest of the composition (windowing, both presenters, the run document, the
// command / input modules) is housed in the DemoHost seam so this entry point stays under its coupling ceiling.
hostSettings.Apply(builder: builder);
var parityResult = DemoHost.RegisterServices(services: services, document: runDocument, capturePath: capturePath, width: hostSettings.Width, height: hostSettings.Height, startWithDirectX: startWithDirectX);
// host.features flows into the FeatureSwitchRegistry through a lazy, post-composition hosted service (HostFeatureApplier,
// below): it resolves the registry from the fully-built container at StartAsync, so registration order against
// DemoHost's own AddSingleton<FeatureSwitchRegistry> never matters. Registered AFTER DemoHost's own hosted services
// (hosted services start in registration order) so BenchInstaller — which builds the §4 switch ROSTER at its StartAsync
// — has already registered every switch by the time this applier runs; registered before it, the applier would resolve
// an empty registry and every override would be an "unknown switch" no-op. Overrides that reach a frame-source-backed
// switch before the node's first frame LATCH (BenchInstaller flushes them onto the frame source when it materializes).
services.AddHostedService(implementationFactory: sp => new HostFeatureApplier(features: runDocument.Host?.Features, serviceProvider: sp));
await builder.Build().RunAsync();

// A --bench headless run's exit code is set by the demo's BenchInstaller via Environment.ExitCode (0 clean scored
// run, 1 abort/refusal/no-timestamps) once BenchRuntime.RunCompleted fires and the host stops — propagate THAT
// instead of the offscreen-gate ternary below, which never applies to a bench run (its document carries a Graph
// root intent, never a Validation gate).
if (BenchBootRequest.ExitWhenComplete && (BenchBootRequest.Suite is not null)) {
    return Environment.ExitCode;
}

// A validation gate fills parityResult before requesting exit; propagate it as the process exit code. A live
// render installs no gate, so it always reports success.
return (isOffscreenRun ? parityResult.ExitCode : 0);

// The run document and its host section are sealed classes, never records (the run-document serializer doctrine),
// so "flags are thin document overrides" is realized by explicit member-copy: clone the document with a host
// section layering the given overrides onto whatever the document already carried. Every member of both types is
// copied by name — a new document/host field must be added here too.
static PuckRunDocument WithHostOverrides(PuckRunDocument document, int? exitAfterSeconds = null, string? presentMode = null, string? presentRate = null, bool? timing = null) {
    var host = document.Host;

    return new PuckRunDocument {
        Addons = document.Addons,
        Extensions = document.Extensions,
        Fuzzing = document.Fuzzing,
        Graph = document.Graph,
        Host = new HostDocument {
            Backend = host?.Backend,
            ExitAfterSeconds = (exitAfterSeconds ?? host?.ExitAfterSeconds),
            Features = host?.Features,
            Fullscreen = host?.Fullscreen,
            Genlock = host?.Genlock,
            PresentMode = (presentMode ?? host?.PresentMode),
            PresentRate = (presentRate ?? host?.PresentRate),
            RayQuery = host?.RayQuery,
            Size = host?.Size,
            SurfaceFormat = host?.SurfaceFormat,
            Timing = (timing ?? host?.Timing),
        },
        Input = document.Input,
        Scene = document.Scene,
        ScreenSources = document.ScreenSources,
        Validation = document.Validation,
        Version = document.Version,
        Viewports = document.Viewports,
    };
}

namespace Puck.Demo {
    /// <summary>
    /// Applies a run document's <c>host.features</c> map to the <see cref="FeatureSwitchRegistry"/> once the registry
    /// is resolvable from the fully-built DI container — a lazy, post-composition consumer, so this file never needs
    /// to reference (or race) DemoHost's own registration of the registry. Attributed errors (an unknown switch name,
    /// a value the switch rejects) print to stderr with the same <c>[run] ...</c> prefix every other composition-time
    /// document diagnostic in this file uses; a document with no <c>host.features</c> (or an empty map) is a silent
    /// no-op.
    /// </summary>
    /// <param name="features">The document's <c>host.features</c> map, or <see langword="null"/>.</param>
    /// <param name="serviceProvider">The built container the registry is resolved from at <see cref="StartAsync"/>.</param>
    internal sealed class HostFeatureApplier(IReadOnlyDictionary<string, string>? features, IServiceProvider serviceProvider) : IHostedService {
        /// <inheritdoc/>
        public Task StartAsync(CancellationToken cancellationToken) {
            if ((features is null) || (features.Count == 0)) {
                return Task.CompletedTask;
            }

            var registry = serviceProvider.GetService<FeatureSwitchRegistry>();

            if (registry is null) {
                Console.Error.WriteLine(value: "[run] host.features was set but no feature-switch registry is composed into this run; every override is ignored.");

                return Task.CompletedTask;
            }

            foreach (var (name, value) in features) {
                if (!registry.TryGet(name: name, descriptor: out var descriptor)) {
                    Console.Error.WriteLine(value: $"[run] host.features.\"{name}\" is not a known feature switch — feature.list shows the registered roster.");

                    continue;
                }

                if (!descriptor.AllowedValues.Contains(value: value, comparer: StringComparer.Ordinal)) {
                    Console.Error.WriteLine(value: $"[run] host.features.\"{name}\" = \"{value}\" is not one of its allowed values ({string.Join(separator: '/', values: descriptor.AllowedValues)}); ignored.");

                    continue;
                }

                if (!descriptor.Set(arg: value)) {
                    Console.Error.WriteLine(value: $"[run] host.features.\"{name}\" = \"{value}\" was rejected (read-only / boot-only) — value unchanged at '{descriptor.Get()}'.");
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
