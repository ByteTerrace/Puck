using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Commands;
using Puck.Demo;
using Puck.DirectX.Presentation;
using Puck.Input;
using Puck.Launcher;
using Puck.Memory;
using Puck.Platform;
using Puck.Vulkan.Presentation;

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
var parseResult = launchCommand.Parse(args);
// Fail loudly on an unrecognized/invalid option rather than silently falling through to the default world render (a
// removed --validate-* flag, a typo, or a bad value). Otherwise a stale script or CI job that still passes a retired
// gate flag would get a 30-second live window and a misleading exit 0. Headless utilities are checked next.
if (!DemoRootNode.ReportParseErrors(parseResult: parseResult)) {
    return 1;
}
// Headless utilities short-circuit before any window/host is created.
var emitSchemaPath = parseResult.GetValue(emitSchemaOption);
if (emitSchemaPath is not null) {
    return DemoRootNode.EmitSchema(path: emitSchemaPath);
}
// Every forge tool mode (SDF art, camera, the framework games, tunes, bakes, avatars, flagships) dispatches
// through ForgeCliSeams — one nullable call, with the whole option surface and RomForge coupling housed there
// (Main is at its class-coupling and maintainability ceilings).
if (await Puck.Demo.Forge.ForgeCliSeams.TryRunAsync(args: args, parseResult: parseResult) is { } forgeSeamExit) {
    return forgeSeamExit;
}
var backend = parseResult.GetValue(backendOption) ?? "vulkan";
var capturePath = parseResult.GetValue(captureOption);
var exitAfterSeconds = parseResult.GetValue(exitAfterSecondsOption);
var presentMode = parseResult.GetValue(presentModeOption) ?? "vsync";
var runPath = parseResult.GetValue(runOption);
var surfaceFormat = parseResult.GetValue(surfaceFormatOption) ?? "r8g8b8a8";

// Every run flows through ONE data-driven path: a --run document, or the legacy flags synthesized into the SAME
// document model. The document then drives the host, the producer/gate, and the exit code — the flags are thin
// document-building aliases, so there is no second imperative path to keep in sync.
var runDocument = (runPath is not null)
    ? DemoRootNode.LoadRunDocument(runPath: runPath)
    : DemoRunDocuments.Synthesize(flags: new DemoFlags {
        Backend = backend,
        ExitAfterSeconds = exitAfterSeconds,
        PresentMode = presentMode,
        SurfaceFormat = surfaceFormat,
        ValidateOverworld = parseResult.GetValue(validateOverworldOption),
        Overworld = parseResult.GetValue(overworldOption),
        RomPath = parseResult.GetValue(romOption),
        RomExit = parseResult.GetValue(romExitOption),
    });

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
var startWithDirectX = (!isOffscreenRun && hostSettings.ResolveHostsOnDirectX(directXAvailable: DemoRootNode.HostDirectXAvailable(document: runDocument)));
// Pre-flight the graph against the RESOLVED host: a deferred/retired affordance (cross-backend produce, graph.child,
// an un-hosted viewport source) exits here with an attributed error, never a mid-host crash.
if (DemoRootNode.ReportGraphUnsupported(document: runDocument, hostsOnDirectX: startWithDirectX)) {
    return 2;
}
var builder = Host.CreateApplicationBuilder(args: args);
var services = builder.Services;
// Window size, launcher cadence, neutral presentation prefs, and the env-var feature toggles, all from the resolved
// host config. Presentation registers before the presenters so it wins their TryAdd defaults.
hostSettings.Apply(services: services);
services.AddLauncherTerminal();
// The launcher run loop is platform-agnostic; the composition root supplies the concrete native windowing (the window
// factory + clipboard + display probe) it resolves from the container at runtime.
services.AddPlatformWindowing();
services.AddCameraCapture();
// Bind the concrete unmanaged allocator here, at the composition root — the Vulkan backend depends only on the
// IAllocator abstraction and resolves it from the container.
services.AddPuckAllocator();
// ORDER MATTERS: AddVulkanPresenter MUST precede AddDirectXPresenter. Both chain in the neutral compute seam
// (AddVulkanComputeApis / AddDirectXComputeApis), which TryAdd the SAME IGpuCompute*/IGpuTiming*/IGpuComputeServices
// types into this one container — so whichever registers FIRST wins. The Vulkan-hosted producers resolve those from
// THIS provider and need the Vulkan device's adapters, so Vulkan must win. (The off-host Direct3D 12 nodes build their
// own isolated collection and are unaffected.)
services.AddVulkanPresenter();
if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
    services.AddDirectXPresenter();
}
// Contribute the concrete backends as NAMED presenters; the launcher's backend switch (AddBackendSwitcher) picks the
// preferred one and fronts the rest behind ISurfacePresenter. The launcher never names a backend — only Demo does, here.
services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(Name: "vulkan", Presenter: sp.GetRequiredService<VulkanSurfacePresenter>()));
if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
    services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(
        Name: "directx",
        Presenter: (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
            ? sp.GetRequiredService<DirectXSurfacePresenter>()
            : throw new PlatformNotSupportedException())));
}
services.AddBackendSwitcher(preferredBackend: (startWithDirectX ? "directx" : "vulkan"));
var parityResult = DemoRootNode.RegisterRunDocument(capturePath: capturePath, document: runDocument, height: hostSettings.Height, hostsOnDirectX: startWithDirectX, services: services, width: hostSettings.Width);
services.AddSingleton<ICommandModule, DemoCommandModule>();
services.AddSingleton<ICommandModule, Puck.Demo.Creator.CreatorCommandModule>();
services.AddSingleton<ICommandModule, Puck.Demo.Tracker.TrackerCommandModule>();
services.AddSingleton<ICommandModule, Puck.Demo.World.WorldCommandModule>();
services.AddSingleton<ICommandModule, Puck.Demo.Creator.CompanionCommandModule>();
services.AddSingleton<ICommandObserver, DemoCommandObserver>();
// The on-screen developer console's state store: DemoConsole publishes to it, the overworld's console overlay renders it.
services.AddSingleton<Puck.Demo.DevConsole.ConsoleTextStore>();
services.AddSingleton<DemoConsole>();
services.AddSingleton(implementationFactory: static _ => new BindingCommandSource(
    bindings: new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase) {
        [InputSources.Keyboard.Backspace] = [new(Command: "backspace")],
        [InputSources.Keyboard.Backtick] = [new(Command: "console")],
        [InputSources.Keyboard.Letter(letter: 'c')] = [new(Command: "copy", RequiredModifiers: InputModifiers.Control)],
        [InputSources.Keyboard.Text] = [new(Command: "echo")],
        [InputSources.Keyboard.Enter] = [new(Command: "enter")],
        [InputSources.Keyboard.Escape] = [new(Command: "escape")],
        [InputSources.Keyboard.Function(number: 4)] = [new(Command: "debug.view.cycle")],
        [InputSources.Keyboard.Letter(letter: 'a')] = [new(Command: "select", RequiredModifiers: InputModifiers.Control)],
    }
));
// Controller input: the manager owns device acquisition, the source feeds the command registry (focus-gated
// like keyboard input), and the hosted service governs device lifetime.
// Single-drainer discipline: the manager's per-frame drain is destructive per device, so exactly ONE consumer may
// drain. The live Overworld root drains per-device itself, and a document with gaming-brick / overworld viewport
// panes drains through the shared pad-routing service — suppress the global gamepad command source for both.
// Every other mode keeps the global source.
services.AddDemoGamepad(registerGlobalSource: ((runDocument.Graph is not Puck.Scene.OverworldNode) && !GamingBrickPadRegistration.UsesPadService(document: runDocument)));
services.AddBrickPadRouting(document: runDocument);
await builder.Build().RunAsync();

// A validation gate fills parityResult before requesting exit; propagate it as the process exit code. A live
// render installs no gate, so it always reports success.
return isOffscreenRun ? parityResult.ExitCode : 0;
