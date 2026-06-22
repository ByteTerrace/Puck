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
var produceOption = new Option<string>(name: "--produce") {
    DefaultValueFactory = static _ => "directx",
    Description = "Which backend renders the SDF showcase (Vulkan hosts either way): directx (default, zero-copy cross-backend) or vulkan (same-device).",
};
var validateOption = new Option<bool>(name: "--validate") {
    Description = "Runs the cross-backend parity gate: renders both backends offscreen, diffs them tolerance-aware, writes artifacts/parity/, and exits with 0 (pass), 1 (gate-fail), or 2 (infra-fail). Forces a Vulkan host.",
};
var validateExportOption = new Option<bool>(name: "--validate-export") {
    Description = "Runs the same-device export/import smoke test on both backends (Vulkan OPAQUE_WIN32 and Direct3D 12 shared handles) and exits (0 pass, 2 infra-fail). Forces a Vulkan host.",
};
var validateComputeOption = new Option<bool>(name: "--validate-compute") {
    Description = "Runs the Vulkan compute smoke test (dispatch gradient.comp into a storage image, read it back) and exits (0 pass, 2 infra-fail). Forces a Vulkan host.",
};
var validateMiniActionOption = new Option<bool>(name: "--validate-mini-action") {
    Description = "Runs the MiniAction determinism + replay self-check (pure CPU: a scripted input run twice must produce identical per-tick state hashes, and a record->replay must reproduce them bit-for-bit) and exits (0 pass, 1 divergence, 2 infra-fail).",
};
var validateDeterminismOption = new Option<bool>(name: "--validate-determinism") {
    Description = "Runs the engine determinism + replay self-check (pure CPU): verifies the fixed-point sim is correct, then records a per-tick CommandSnapshot stream, round-trips it through the neutral binary format, and replays it — asserting identical per-tick state hashes and that every command value kind survives the round-trip. Exits (0 pass, 1 divergence, 2 infra-fail).",
};
var validateCliDeterminismOption = new Option<bool>(name: "--validate-cli-determinism") {
    Description = "Runs the deterministic command-line/STDIN sim-control self-check (pure CPU): drives the fixed-point sim from a scripted console session of real text commands (Submit->inject->snapshot), asserting two identical sessions produce identical per-tick state hashes, a record->binary round-trip->replay reproduces them bit-for-bit, and the console input measurably drove the sim. Exits (0 pass, 1 divergence, 2 infra-fail).",
};
var miniActionOption = new Option<bool>(name: "--mini-action") {
    Description = "Renders the live MiniAction prototype: a controller-driven player box running around a room on a Vulkan host. Move with the left stick, jump with the South (A / Cross / B) button.",
};
var worldOption = new Option<bool>(name: "--world") {
    Description = "Renders the generic SDF compute compositor (the SDF VM run in a compute kernel over a data scene + data camera) instead of the SDF showcase. Forces a Vulkan host.",
};
var worldSplitOption = new Option<bool>(name: "--world-split") {
    Description = "Like --world, but a 2x2 split-screen of four independent data-driven cameras on the same scene, filled in a single compute dispatch. Forces a Vulkan host.",
};
var worldChildOption = new Option<bool>(name: "--world-child") {
    Description = "Like --world-split, but the bottom-right viewport shows a hosted child node's animated surface instead of an SDF camera (the per-viewport child-surface seam). Forces a Vulkan host.",
};
var worldRtOption = new Option<bool>(name: "--world-rt") {
    Description = "Renders the Vulkan-only ray-query world: a per-frame TLAS over a unit-AABB instance, ray-traced by an inline RayQuery compute kernel. Always Vulkan-hosted (ray-query has no Direct3D 12 equivalent); falls back to a blank surface on an adapter without ray-query support.",
};
var validateWorldOption = new Option<bool>(name: "--validate-world") {
    Description = "Cross-backend parity gate: renders the compute SDF world on both Vulkan (host device) and Direct3D 12 (bespoke LUID-matched device) at a fixed frame, captures each, and exits (0 pass, 2 infra-fail). Forces a Vulkan host.",
};
var validateWorldChildOption = new Option<bool>(name: "--validate-world-child") {
    Description = "Like --validate-world, but the bottom-right viewport is a hosted child node; captures both backends to artifacts/parity-world-child-*.png for the per-viewport child-surface seam. Forces a Vulkan host.",
};
var validateReverseShareOption = new Option<bool>(name: "--validate-reverse-share") {
    Description = "Reverse cross-API share gate: Direct3D 12 owns a shared storage image, Vulkan imports it and computes a gradient INTO it, then Direct3D 12 reads it back. Exits (0 pass, 2 infra-fail). Forces a Vulkan host.",
};
var validateIndirectOption = new Option<bool>(name: "--validate-indirect") {
    Description = "Indirect-compute-dispatch gate: dispatches sdf-child via Dispatch and DispatchIndirect (GPU-read group counts) on BOTH Vulkan (vkCmdDispatchIndirect) and Direct3D 12 (ExecuteIndirect), asserting the two are bit-identical. Exits (0 pass, 2 infra-fail). Forces a Vulkan host.",
};
var validateResampleOption = new Option<bool>(name: "--validate-resample") {
    Description = "Sampled-image-in-compute gate: renders sdf-child then SAMPLES it in resample.comp (a combined-image-sampler on Vulkan, an SRV + static sampler on Direct3D 12) on BOTH backends — asserting a nearest identity resample equals the source bit-for-bit and a 2x linear upscale matches cross-backend. Exits (0 pass, 2 infra-fail). Forces a Vulkan host.",
};
var validateViewportsOption = new Option<bool>(name: "--validate-viewports") {
    Description = "Generic-compositor gate: composites a heterogeneous layout (a raw integer-copy pane beside a NEAREST-resampled pane) through the source-agnostic ViewportCompositorNode on BOTH backends, capturing each to a PNG and asserting they agree within 1 LSB. Exits (0 pass, 1 cross-backend diff, 2 infra-fail). Forces a Vulkan host.",
};
var validatePixelateOption = new Option<bool>(name: "--validate-pixelate") {
    Description = "Retro-pixelation gate: composites a raw pane beside the same pattern wrapped in a PixelateNode (cell-blocked + posterized) through ViewportCompositorNode on BOTH backends, capturing each to a PNG and asserting they agree within 1 LSB. Exits (0 pass, 1 cross-backend diff, 2 infra-fail). Forces a Vulkan host.",
};
var validateCaptureOption = new Option<bool>(name: "--validate-capture") {
    Description = "Native image-capture gate: drives the backend-neutral capture pipeline end to end (a GDI screen grab through IFrameCaptureSource into a CaptureSink + frame hash), writing artifacts/capture-desktop.png. Lenient about signal (a headless/secure desktop yields nothing); exits 0 (pass/skip) or 2 (infra-fail). Forces a Vulkan host.",
};
var fuzzSeedOption = new Option<int>(name: "--fuzz-seed") {
    DefaultValueFactory = static _ => -1,
    Description = "With --validate-world, renders a fuzz-generated SDF scene program (deterministic from this seed, identical on both backends) instead of the showcase — one cross-backend differential-fuzzing iteration. A negative value (default) disables fuzzing.",
};
var runOption = new Option<string?>(name: "--run") {
    DefaultValueFactory = static _ => null,
    Description = "Path to a data-driven run document (run.json). Its graph selects the producer and its scene + viewports drive the render, replacing the world/showcase flags. Hosts on Vulkan.",
};
var emitSchemaOption = new Option<string?>(name: "--emit-schema") {
    DefaultValueFactory = static _ => null,
    Description = "Headless utility: write the run-document JSON Schema to the given path and exit (no window).",
};
var checkRunOption = new Option<string?>(name: "--check-run") {
    DefaultValueFactory = static _ => null,
    Description = "Headless utility: assert a run document's scene program is bit-identical to the built-in showcase scene and exit (0 match, 1 mismatch, 2 load error; no window).",
};
var presentModeOption = new Option<string>(name: "--present-mode") {
    DefaultValueFactory = static _ => "vsync",
    Description = "The swapchain present mode (both backends honor it): vsync (default), mailbox, or immediate.",
};
var surfaceFormatOption = new Option<string>(name: "--surface-format") {
    DefaultValueFactory = static _ => "r8g8b8a8",
    Description = "The back-buffer surface format (both backends honor it): r8g8b8a8 (default) or b8g8r8a8.",
};
var launchCommand = new RootCommand(description: "Puck Demo") {
    backendOption,
    captureOption,
    checkRunOption,
    emitSchemaOption,
    exitAfterSecondsOption,
    fuzzSeedOption,
    presentModeOption,
    produceOption,
    runOption,
    surfaceFormatOption,
    validateOption,
    validateExportOption,
    validateComputeOption,
    validateMiniActionOption,
    validateDeterminismOption,
    validateCliDeterminismOption,
    miniActionOption,
    validateReverseShareOption,
    validateIndirectOption,
    validateResampleOption,
    validateViewportsOption,
    validatePixelateOption,
    validateCaptureOption,
    validateWorldOption,
    validateWorldChildOption,
    worldOption,
    worldSplitOption,
    worldChildOption,
    worldRtOption,
};
var parseResult = launchCommand.Parse(args);
// Headless utilities short-circuit before any window/host is created.
var emitSchemaPath = parseResult.GetValue(emitSchemaOption);
if (emitSchemaPath is not null) {
    return DemoRootNode.EmitSchema(path: emitSchemaPath);
}
var checkRunPath = parseResult.GetValue(checkRunOption);
if (checkRunPath is not null) {
    return DemoRootNode.CheckRunDocument(runPath: checkRunPath);
}
var backend = parseResult.GetValue(backendOption) ?? "vulkan";
var capturePath = parseResult.GetValue(captureOption);
var exitAfterSeconds = parseResult.GetValue(exitAfterSecondsOption);
var fuzzSeed = parseResult.GetValue(fuzzSeedOption);
var presentMode = parseResult.GetValue(presentModeOption) ?? "vsync";
var produceBackend = parseResult.GetValue(produceOption) ?? "directx";
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
        FuzzSeed = fuzzSeed,
        PresentMode = presentMode,
        Produce = produceBackend,
        SurfaceFormat = surfaceFormat,
        Validate = parseResult.GetValue(validateOption),
        ValidateCompute = parseResult.GetValue(validateComputeOption),
        ValidateMiniAction = parseResult.GetValue(validateMiniActionOption),
        ValidateDeterminism = parseResult.GetValue(validateDeterminismOption),
        ValidateCliDeterminism = parseResult.GetValue(validateCliDeterminismOption),
        MiniAction = parseResult.GetValue(miniActionOption),
        ValidateExport = parseResult.GetValue(validateExportOption),
        ValidateReverseShare = parseResult.GetValue(validateReverseShareOption),
        ValidateIndirect = parseResult.GetValue(validateIndirectOption),
        ValidateResample = parseResult.GetValue(validateResampleOption),
        ValidateViewports = parseResult.GetValue(validateViewportsOption),
        ValidatePixelate = parseResult.GetValue(validatePixelateOption),
        ValidateCapture = parseResult.GetValue(validateCaptureOption),
        ValidateWorld = parseResult.GetValue(validateWorldOption),
        ValidateWorldChild = parseResult.GetValue(validateWorldChildOption),
        World = parseResult.GetValue(worldOption),
        WorldChild = parseResult.GetValue(worldChildOption),
        WorldRt = parseResult.GetValue(worldRtOption),
        WorldSplit = parseResult.GetValue(worldSplitOption),
    });

// A failed --run load is already reported by LoadRunDocument; a synthesized document is always valid.
if (runDocument is null) {
    return 2;
}

// A fuzzing seedRange must go through the process-isolated `tools fuzz` loop; --run runs ONE seed in-process.
if (DemoRootNode.RequiresExternalFuzzLoop(document: runDocument, fuzzSeed: fuzzSeed)) {
    return 2;
}

// A validation OR fuzzing run installs a cross-backend gate that renders OFFSCREEN on Vulkan (it LUID-matches a
// Direct3D 12 device from the Vulkan host), so it forces a Vulkan host regardless of the host section.
var isOffscreenRun = (runDocument.Validation is not null) || (runDocument.Fuzzing is not null);
var hostSettings = HostSettings.FromDocument(flagBackend: backend, flagExitAfterSeconds: exitAfterSeconds, flagPresentMode: presentMode, flagSurfaceFormat: surfaceFormat, host: runDocument.Host);
// The window host follows the document's resolved backend, gated by the graph's Direct3D 12 requirement (DXR for rt)
// so it never diverges from the node's device; an offscreen gate forces Vulkan.
var startWithDirectX = (!isOffscreenRun && hostSettings.ResolveHostsOnDirectX(directXAvailable: DemoRootNode.HostDirectXAvailable(document: runDocument)));
var builder = Host.CreateApplicationBuilder(args: args);
var services = builder.Services;
// Window size, launcher cadence, neutral presentation prefs, and the env-var feature toggles, all from the resolved
// host config. Presentation registers before the presenters so it wins their TryAdd defaults.
hostSettings.Apply(services: services);
services.AddLauncherTerminal();
// The launcher run loop is platform-agnostic; the composition root supplies the concrete native windowing (the window
// factory + clipboard + display probe) it resolves from the container at runtime.
services.AddPlatformWindowing();
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
var parityResult = DemoRootNode.RegisterRunDocument(capturePath: capturePath, document: runDocument, fuzzSeed: fuzzSeed, height: hostSettings.Height, hostsOnDirectX: startWithDirectX, services: services, width: hostSettings.Width);
services.AddSingleton<ICommandModule, DemoCommandModule>();
services.AddSingleton<ICommandObserver, DemoCommandObserver>();
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
// The live MiniAction node is the SOLE per-frame gamepad drainer (it routes each controller to its own player), so
// suppress the global gamepad command source for it — otherwise that source's destructive drain consumes the
// per-device edges first. Every other mode keeps the global source.
services.AddDemoGamepad(registerGlobalSource: runDocument.Graph is not Puck.Scene.MiniActionNode);
await builder.Build().RunAsync();

// A validation/fuzzing gate fills parityResult before requesting exit; propagate it as the process exit code. A live
// render installs no gate, so it always reports success.
return isOffscreenRun ? parityResult.ExitCode : 0;
