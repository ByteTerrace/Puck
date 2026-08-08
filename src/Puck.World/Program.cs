using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Launcher;
using Puck.World;

// The host CLI flags are a DEPLOYMENT OVERRIDE laid over the world document's presentation intent, so each is NULLABLE
// with no DefaultValueFactory: absent means "the document decides" (WorldHostSettings.Resolve coalesces to the authored
// host defaults). A DefaultValueFactory here would silently defeat the document on every unflagged run.
var backendOption = new Option<string?>(name: "--backend") {
    DefaultValueFactory = static _ => null,
    Description = "Override the world's graphics backend: auto, directx, or vulkan. Absent uses the world document's host.backend. A --backend directx on a non-Direct3D-12 OS is an operator assertion and hard-exits (a document preference degrades to Vulkan loudly instead).",
};
var widthOption = new Option<int?>(name: "--width") {
    DefaultValueFactory = static _ => null,
    Description = "Override the window client width in pixels. Absent uses the world document's host.width.",
};
var heightOption = new Option<int?>(name: "--height") {
    DefaultValueFactory = static _ => null,
    Description = "Override the window client height in pixels. Absent uses the world document's host.height.",
};
var exitAfterSecondsOption = new Option<int?>(name: "--exit-after-seconds") {
    DefaultValueFactory = static _ => null,
    Description = "Override the auto-exit seconds; 0 or less runs until the window is closed. Absent uses the world document's host.exitAfterSeconds.",
};
var presentModeOption = new Option<string?>(name: "--present-mode") {
    DefaultValueFactory = static _ => null,
    Description = "Override the swapchain presentation algorithm: vsync, mailbox, immediate, or adaptive. Absent uses the world document's host.presentMode.",
};
var worldOption = new Option<string?>(name: "--world") {
    DefaultValueFactory = static _ => null,
    Description = "The world definition file (puck.world.def.v1) to load. A missing or invalid file FAILS the boot with a named reason and exit 1. Absent, the shipped Assets/worlds/play.world.json beside the executable loads; failure to load that document also fails the boot.",
};
var recordingOption = new Option<string?>(name: "--recording") {
    DefaultValueFactory = static _ => null,
    Description = "The recording document (puck.recording.v1) the capture verbs use; a missing or invalid file falls back loudly to the baked default. Default: Assets/recordings/default.recording.json beside the executable.",
};
var storageUriOption = new Option<string?>(name: "--storage-uri") {
    DefaultValueFactory = static _ => null,
    Description = "The per-user blob endpoint (a service URI for the platform edge, or a dev/emulator connection string), overriding the world doc's storage.endpoint. With a resolved user identity it wires storage.push/storage.pull; storage.status echoes it.",
};
var userIdOption = new Option<string?>(name: "--user-id") {
    DefaultValueFactory = static _ => null,
    Description = "The explicit storage user-id override (an Entra oid Guid), overriding the world doc's storage.userId. Feeds the identity resolver's explicit-override source; storage.status reports the resolution.",
};
var storageDiscoveryUriOption = new Option<string?>(name: "--storage-discovery-uri") {
    DefaultValueFactory = static _ => null,
    Description = "The direct-to-account endpoint (a service URI or a dev/emulator connection string) container LIST uses when --storage-uri resolves to the platform edge, overriding the world doc's storage.discoveryEndpoint. The edge cannot serve a container list at all, so an edge-shaped endpoint with no discovery endpoint refuses cloud-world discovery by name; storage.status echoes the resolution.",
};
var stateDirOption = new Option<string?>(name: "--state-dir") {
    DefaultValueFactory = static _ => null,
    Description = "Override the on-disk state root (profile catalog, replays). Absent uses %LOCALAPPDATA%\\Puck\\World. A developer/deployment override: parallel verification runs and multiple hosts on one machine each need their own root.",
};
// A DEVELOPER REFLECTION of the document's host.presentation field, not a separate product (the unification
// contract): absent lets the document decide; a bare --headless (or --headless true) forces host.presentation=none
// for this run only (no window, no GPU device, no swapchain, no audio device); --headless false forces windowed.
var headlessOption = new Option<bool?>(name: "--headless") {
    Arity = ArgumentArity.ZeroOrOne,
    DefaultValueFactory = static _ => null,
    Description = "Override the world's boot shape: a bare flag (or 'true') boots headless (no window/GPU/swapchain/audio device — the authoritative server, console, and tape only); 'false' forces windowed. Absent uses the world document's host.presentation.",
};
// A DEVELOPER REFLECTION of the document's host.listen field (the TCP socket door), not a separate product: absent
// lets the document decide (null = loopback-only, never opens a socket); an explicit value binds a TCP listener for
// this run only.
var listenOption = new Option<string?>(name: "--listen") {
    DefaultValueFactory = static _ => null,
    Description = "Override the TCP listen endpoint (an \"ip:port\" pair, e.g. 127.0.0.1:7777). Absent uses the world document's host.listen (null = loopback-only, never opens a socket).",
};
// NOT a host.* reflection — connecting is inherently this ONE run's transport target, never a property of the
// world being authored. Present, the whole normal composition (GPU, local authoritative server, CommandRegistry) is
// skipped in favor of Puck.World.WorldRemoteClient's minimal socket harness; every other option is ignored.
var connectOption = new Option<string?>(name: "--connect") {
    DefaultValueFactory = static _ => null,
    Description = "Connect to a remote TCP host instead of booting locally (an \"ip:port\" pair). Skips the whole normal composition in favor of a minimal stdin/socket client (Puck.World.WorldRemoteClient); every other option is ignored.",
};
var launchCommand = new RootCommand(description: "Puck World") {
    backendOption,
    connectOption,
    exitAfterSecondsOption,
    headlessOption,
    stateDirOption,
    heightOption,
    listenOption,
    presentModeOption,
    recordingOption,
    storageDiscoveryUriOption,
    storageUriOption,
    userIdOption,
    widthOption,
    worldOption,
};
var parseResult = launchCommand.Parse(args);

// Fail loudly on an unrecognized/invalid option (a typo, a bad value) rather than silently falling through to a live
// window with defaults — checked BEFORE the --connect branch so a malformed flag refuses even a client-mode run.
if (parseResult.Errors.Count > 0) {
    foreach (var error in parseResult.Errors) {
        Console.Error.WriteLine(value: error.Message);
    }

    return 1;
}

// --connect bypasses the whole normal composition — a minimal socket client, not a second world boot.
if (parseResult.GetValue(option: connectOption) is { } connectTarget) {
    return await WorldRemoteClient.RunAsync(connect: connectTarget);
}
if (parseResult.GetValue(option: stateDirOption) is { } stateDirOverride) {
    Puck.World.Server.WorldStateRoot.Override(path: stateDirOverride);
}
// Parse the nullable host CLI overrides at the boundary, keeping World's loud typo hard-exits for --backend / --present-
// mode. A null override means "the document decides" (WorldHostSettings.Resolve coalesces to the authored defaults).
WorldBackendPreference? backendOverride = null;
if (parseResult.GetValue(option: backendOption) is { } backendName) {
    backendOverride = WorldHostTokens.ParseBackend(token: backendName);

    if (backendOverride is null) {
        Console.Error.WriteLine(value: $"Unknown --backend '{backendName}'; expected auto, directx, or vulkan.");

        return 1;
    }
}
PresentMode? presentModeOverride = null;
if (parseResult.GetValue(option: presentModeOption) is { } presentModeName) {
    presentModeOverride = presentModeName.ToUpperInvariant() switch {
        "VSYNC" => PresentMode.Vsync,
        "MAILBOX" => PresentMode.Mailbox,
        "IMMEDIATE" => PresentMode.Immediate,
        "ADAPTIVE" => PresentMode.Adaptive,
        _ => null,
    };

    if (presentModeOverride is null) {
        Console.Error.WriteLine(value: $"Unknown --present-mode '{presentModeName}'; expected vsync, mailbox, immediate, or adaptive.");

        return 1;
    }
}

// The --headless reflection, parsed at the boundary like --backend/--present-mode: null lets the document's
// host.presentation decide; an explicit true/false overrides it for this run only.
WorldHostPresentation? presentationOverride = (parseResult.GetValue(option: headlessOption) switch {
    true => WorldHostPresentation.None,
    false => WorldHostPresentation.Windowed,
    null => null,
});

// The world definition (see WorldDefinition) — a --world file or the shipped Assets/worlds/play.world.json beside
// the executable, loaded / schema-checked / validated (see WorldDefinitionLoader). LOADED BEFORE the
// window/launcher/presentation registrations because those now read their values from the resolved host section. Read
// by DI from the roster, population, frame source, render settings, and the world.quality verb; the resolved source is
// registered so world.save knows its default target. Any path that will not load ends the boot here — a typo or missing
// shipped document must never quietly run a different world.
if (!WorldDefinitionLoader.TryResolve(explicitPath: parseResult.GetValue(option: worldOption), source: out var worldSource, failure: out var worldFailure)) {
    Console.Error.WriteLine(value: worldFailure);

    return 1;
}

// Resolve the effective host settings: the world doc's host defaults (absence coalesced to WorldHostDefaults.Default,
// which reproduces World's current boot) overlaid by the nullable CLI flags. Backend authority differs by source — a CLI
// assertion the OS cannot satisfy hard-exits (World's current behavior), a document preference degrades to Vulkan loudly.
var directXAvailable = OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240);
var hostSettings = WorldHostSettings.Resolve(
    defaults: worldSource.Definition.Host,
    directXAvailable: directXAvailable,
    backendOverride: backendOverride,
    widthOverride: parseResult.GetValue(option: widthOption),
    heightOverride: parseResult.GetValue(option: heightOption),
    exitAfterSecondsOverride: parseResult.GetValue(option: exitAfterSecondsOption),
    presentModeOverride: presentModeOverride,
    presentationOverride: presentationOverride,
    listenOverride: parseResult.GetValue(option: listenOption)
);
if (hostSettings.BackendUnsatisfiable) {
    Console.Error.WriteLine(value: "The Direct3D 12 backend requires Windows 10 or newer; use --backend vulkan on this platform.");

    return 1;
}
if (hostSettings.BackendDowngraded) {
    Console.Error.WriteLine(value: $"[world.host] backend \"{WorldHostTokens.BackendToken(backend: hostSettings.RequestedBackend)}\" is unavailable on this OS; hosting on Vulkan instead.");
}
var hostsOnDirectX = hostSettings.HostsOnDirectX;
var width = (uint)hostSettings.Width;
var height = (uint)hostSettings.Height;
// The GPU-timing arm boots from the host section's timing field — the lowest-precedence seed (a live world.timing
// SetArmed always overrides). TrySeed is idempotent and claims the control only if nothing above has.
GpuTimingControl.Shared.TrySeed(armed: hostSettings.Timing);
var builder = Host.CreateApplicationBuilder(args: args);
var services = builder.Services;
services.AddSingleton(implementationInstance: worldSource);
services.AddSingleton(implementationInstance: worldSource.Definition);
// The resolved host settings — read by the composition modules below and the world.host verb.
services.AddSingleton(implementationInstance: hostSettings);
// Registered before the launcher terminal block (AddWorldGpuHost/AddWorldHeadlessHost → AddLauncherTerminal/
// AddLauncherHeadlessTerminal) so the launcher's TryAddSingleton<LauncherOptions> defers to this one, IN EITHER BOOT
// SHAPE — --exit-after-seconds applies to the headless tick host exactly like the windowed one. A null target
// selects automatic display pacing from verified VRR capabilities or active signal timing (windowed only).
services.AddSingleton(implementationInstance: new LauncherOptions {
    ExitAfter = ((hostSettings.ExitAfterSeconds > 0) ? TimeSpan.FromSeconds(value: hostSettings.ExitAfterSeconds) : null),
    TargetRenderRate = hostSettings.TargetRenderRate,
});

// The storage host-section: the world doc's endpoint + user-id + discovery endpoint, overlaid by the
// --storage-uri / --user-id / --storage-discovery-uri CLI reflection. The identity resolver maps an explicit
// user-id to a per-user container Guid, or DECLINES (local-only). Endpoint plus resolved identity wires the
// owned-world sync engine (storage.push / storage.pull); anything less leaves the catalog local-only, and
// storage.status names which half declined. The discovery endpoint only matters when the resolved endpoint is
// edge-shaped — the platform edge cannot serve container LIST at all, so cloud-world discovery refuses by name
// without one.
var storageSettings = WorldStorageSettings.Resolve(
    defaults: worldSource.Definition.Storage,
    endpointOverride: parseResult.GetValue(option: storageUriOption),
    userIdOverride: parseResult.GetValue(option: userIdOption),
    discoveryEndpointOverride: parseResult.GetValue(option: storageDiscoveryUriOption)
);
services.AddSingleton(implementationInstance: storageSettings);
services.AddSingleton(implementationInstance: IPlayerStorageIdentityResolver.Create(settings: storageSettings));
Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
services.AddSingleton(implementationFactory: static provider => WorldStorageSyncHandle.Create(
    identity: provider.GetRequiredService<IPlayerStorageIdentityResolver>(),
    settings: provider.GetRequiredService<WorldStorageSettings>(),
    store: provider.GetRequiredService<Puck.Storage.IObjectBlobStore>(),
    worlds: provider.GetRequiredService<Puck.World.Server.WorldOwnedWorlds>()
));

// The player's controls as DATA: the engine-default binding document (WASD/arrows movement, Space/South/East
// gestures, Enter/F1-F4 roster, sticks, Start),
// composed per seat with the world's binding overlays, the seat's profile bindings, and its live session rebinds. One
// WorldSeatBindings resolves every seat's input, feeding the ONE input consumer there is: the per-seat sim-fold (the
// IInputBindings handed to AddFixedStepSimulation), whose router stamps each lane's acting principal. Constructed here
// (before the container builds) with the engine default and boot overlays; the roster, the rebind verbs, and the
// post-step overlay sync push the per-seat and overlay layers in as they change.
// The per-seat control-feel store is built here too, seeded from the boot document's own authored feel — the
// resolution every seat sits at until a profile is delivered for it, from wherever that profile arrives.
var seatFeel = new WorldSeatFeel(worldLook: worldSource.Definition.PlayerDefaults.SeatLook);
var seatBindings = new WorldSeatBindings(engineDefault: WorldDefaultBindings.BuildDocument(), definition: worldSource.Definition, seatFeel: seatFeel);
services.AddSingleton(implementationInstance: seatFeel);
services.AddSingleton(implementationInstance: seatBindings);

// BOOT SHAPE RESOLVES BEFORE REGISTRATION: the authoritative core registers in EVERY shape;
// the GPU host, render root, overlays, audio device, screens/machines, gamepads, and editor register ONLY when
// presentation is composed. See WorldBootComposition for the full split and WorldPostBuildWiring for the shared
// every-shape wiring that used to live inside the (presentation-only) render-root factory.
services.AddWorldAuthoritativeCore();
if (hostSettings.Headless) {
    // NO window, NO GPU device, NO swapchain, NO allocator, NO backend presenter, NO audio device — the headless
    // twin of the block below. Nothing under AddWorldPresentation is ever called on this path.
    WorldHost.AddWorldHeadlessHost(services: services);
    services.AddFixedStepSimulation<HeadlessWorldSimulation>(bindings: seatBindings);
} else {
    // The recording graph (puck.recording.v1) — native capture for streaming/upload workflows, defined as data.
    // PRESENTATION-ONLY (AddWorldPresentation registers the encoder ladder/RecordingTap/verb module), but the
    // document resolution itself can fail-and-exit, so it stays here beside World's other --world/--recording
    // loaders. Skipped headless: a GPU-less CI box has no reason to carry a valid recordings asset.
    if (!RecordingDocumentLoader.TryResolve(explicitPath: parseResult.GetValue(option: recordingOption), source: out var recordingSource, failure: out var recordingFailure)) {
        Console.Error.WriteLine(value: recordingFailure);

        return 1;
    }

    services.AddSingleton(implementationInstance: recordingSource);

    // The trimmed GPU host (windowing, allocator, one complete launch-selected backend), the render root, overlays,
    // the audio device, screens/machines verbs, gamepads, and the editor. Only the selected backend enters this
    // service provider so its neutral compute services and presenter name the same physical device and shader
    // format.
    services.AddWorldPresentation(hostsOnDirectX: hostsOnDirectX);

    // The shared easy path owns the one fixed-step accumulator, turns every physical/console input into a per-tick
    // snapshot, applies it, and invokes WorldSimulation (client + screens + editor, over the shared server-step
    // shell). Rendering consumes interpolation state only.
    services.AddFixedStepSimulation<WorldSimulation>(bindings: seatBindings);
}
var host = builder.Build();

// The EVERY-SHAPE post-build wiring step: affordance install, the lever-sink attachment, and the
// server's echo/cue taps — moved out of the old presentation-only render-root factory so wire.errors stays honest
// headless. Runs before the host starts, so it observes the FULLY built container in either boot shape.
WorldPostBuildWiring.Install(services: host.Services);
await host.RunAsync();
return 0;
