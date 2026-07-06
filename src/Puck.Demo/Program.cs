using System.CommandLine;
using Microsoft.Extensions.Hosting;
using Puck.Demo;

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
    Description = "Renders the OVERWORLD — the demo, and the default with no flags at all: a controller-driven player in a room with three console stands (the showcase cartridge on the DMG/CGB/AGB costumes of the one machine). Walk with the left stick, jump with South, and press North at a stand to boot it — each boot lights its pane and the screen walks its staged split. Vulkan host.",
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
    Description = "Path to a data-driven run document (run.json). Its graph selects the producer and its scene + viewports drive the render, replacing the world/showcase flags. Hosts on Vulkan.",
};
var emitSchemaOption = new Option<string?>(name: "--emit-schema") {
    DefaultValueFactory = static _ => null,
    Description = "Headless utility: write the run-document JSON Schema to the given path and exit (no window).",
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
    captureOption,
    emitSchemaOption,
    exitAfterSecondsOption,
    Puck.Demo.Forge.ForgeCliSeams.ForgeOption,
    Puck.Demo.Forge.ForgeCliSeams.AvatarOption,
    Puck.Demo.Forge.ForgeCliSeams.AvatarFromOption,
    Puck.Demo.Forge.ForgeCliSeams.AvatarMovementModeOption,
    Puck.Demo.Forge.ForgeCliSeams.FlagshipsOption,
    Puck.Demo.Forge.ForgeCliSeams.BakeOption,
    Puck.Demo.Forge.ForgeCliSeams.BakeCalibrationOption,
    Puck.Demo.Forge.ForgeCliSeams.BakeStressOption,
    Puck.Demo.Forge.ForgeCliSeams.CameraOption,
    Puck.Demo.Forge.ForgeCliSeams.VolleyOption,
    Puck.Demo.Forge.ForgeCliSeams.BrickfallOption,
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
var emitSchemaPath = parseResult.GetValue(emitSchemaOption);
if (emitSchemaPath is not null) {
    return DemoRunRegistrar.EmitSchema(path: emitSchemaPath);
}
// Every forge tool mode (SDF art, camera, the framework games, tunes, bakes, avatars, flagships) dispatches
// through ForgeCliSeams — one nullable call, with the whole option surface and RomForge coupling housed there
// (Main is at its class-coupling and maintainability ceilings).
if (await Puck.Demo.Forge.ForgeCliSeams.TryRunAsync(args: args, parseResult: parseResult) is { } forgeSeamExit) {
    return forgeSeamExit;
}
var backend = parseResult.GetValue(backendOption) ?? "vulkan";
var capturePath = parseResult.GetValue(captureOption);
var presentMode = parseResult.GetValue(presentModeOption) ?? "vsync";
var runPath = parseResult.GetValue(runOption);
var surfaceFormat = parseResult.GetValue(surfaceFormatOption) ?? "r8g8b8a8";

var builder = Host.CreateApplicationBuilder(args: args);
var services = builder.Services;

// Layer + bind the run's configuration in one call: it resolves the review SCENARIO (if any), layers the sources
// (scenario/appsettings JSON < the legacy PUCK_* environment < the command-line --scenario-set overrides), binds the
// typed options, and reports the scenario's exit-after (0 when none / not set). A negative result means a named
// scenario could not be found — a hard, loud failure rather than a silent live window.
var scenarioExitAfterSeconds = Puck.Demo.Configuration.ScenarioCliSeams.Configure(builder: builder, parseResult: parseResult);

if (scenarioExitAfterSeconds < 0) {
    return 1;
}

// The scenario's exit-after-seconds (when set) wins over the CLI default so a run stays alive through its last shot;
// otherwise the CLI --exit-after-seconds stands.
var exitAfterSeconds = ((scenarioExitAfterSeconds > 0) ? scenarioExitAfterSeconds : parseResult.GetValue(exitAfterSecondsOption));

// Every run flows through ONE data-driven path: a --run document, or the legacy flags synthesized into the SAME
// document model. The document then drives the host, the producer/gate, and the exit code — the flags are thin
// document-building aliases, so there is no second imperative path to keep in sync.
var runDocument = (runPath is not null)
    ? DemoRunRegistrar.LoadRunDocument(runPath: runPath)
    : DemoRunDocuments.Synthesize(
        backend: backend,
        exitAfterSeconds: exitAfterSeconds,
        presentMode: presentMode,
        surfaceFormat: surfaceFormat,
        validateOverworld: parseResult.GetValue(validateOverworldOption),
        overworld: parseResult.GetValue(overworldOption),
        romPath: parseResult.GetValue(romOption),
        romExit: parseResult.GetValue(romExitOption)
    );

// A failed --run load is already reported by LoadRunDocument; a synthesized document is always valid.
if (runDocument is null) {
    return 2;
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
await builder.Build().RunAsync();

// A validation gate fills parityResult before requesting exit; propagate it as the process exit code. A live
// render installs no gate, so it always reports success.
return isOffscreenRun ? parityResult.ExitCode : 0;
