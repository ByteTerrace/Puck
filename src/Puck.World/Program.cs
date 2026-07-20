using System.CommandLine;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Capture;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Pacing;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.AdvancedGamingBrick;
using Puck.Commands;
using Puck.Hosting;
using Puck.HumbleGamingBrick;
using Puck.Input;
using Puck.Launcher;
using Puck.Overlays;
using Puck.Platform;
using Puck.Platform.Audio;
using Puck.Platform.Windows;
using Puck.Platform.Windows.Gamepad;
using Puck.Platform.Windows.Hid;
using Puck.SdfVm;
using Puck.World;
using Puck.World.Audio;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

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
    Description = "The world definition file (puck.world.def.v1) to load; a missing or invalid file falls back loudly to the baked default. Default: Assets/worlds/default.world.json beside the executable.",
};
var recordingOption = new Option<string?>(name: "--recording") {
    DefaultValueFactory = static _ => null,
    Description = "The recording document (puck.recording.v1) the capture verbs use; a missing or invalid file falls back loudly to the baked default. Default: Assets/recordings/default.recording.json beside the executable.",
};
var storageUriOption = new Option<string?>(name: "--storage-uri") {
    DefaultValueFactory = static _ => null,
    Description = "Reserved: the per-user blob endpoint, overriding the world doc's storage.endpoint. Not yet wired to any Azure target; storage.status echoes it.",
};
var userIdOption = new Option<string?>(name: "--user-id") {
    DefaultValueFactory = static _ => null,
    Description = "The explicit storage user-id override (an Entra oid Guid), overriding the world doc's storage.userId. Feeds the identity resolver's explicit-override source; storage.status reports the resolution.",
};
var launchCommand = new RootCommand(description: "Puck World") {
    backendOption,
    exitAfterSecondsOption,
    heightOption,
    presentModeOption,
    recordingOption,
    storageUriOption,
    userIdOption,
    widthOption,
    worldOption,
};
var parseResult = launchCommand.Parse(args);

// Fail loudly on an unrecognized/invalid option (a typo, a bad value) rather than silently falling through to a live
// window with defaults.
if (parseResult.Errors.Count > 0) {
    foreach (var error in parseResult.Errors) {
        Console.Error.WriteLine(value: error.Message);
    }

    return 1;
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

// The world definition (see WorldDefinition) — a --world file, or Assets/worlds/default.world.json beside the
// executable, loaded / schema-checked / validated with a loud baked-default fallback on ANY failure (see
// WorldDefinitionLoader). LOADED BEFORE the window/launcher/presentation registrations because those now read their
// values from the resolved host section. Read by DI from the roster, population, frame source, render settings, and the
// world.quality verb; the resolved source is registered so world.save knows its default target (null when baked/fallback).
var worldSource = WorldDefinitionLoader.Load(explicitPath: parseResult.GetValue(option: worldOption));

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
    presentModeOverride: presentModeOverride
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
// The resolved host settings — read by the window/launcher/presentation registrations below and the world.host verb.
services.AddSingleton(implementationInstance: hostSettings);
services.Configure<NativeWindowOptions>(configureOptions: options => {
    options.Height = height;
    options.Mode = NativeWindowMode.PlatformWindow;
    options.StartFullscreen = hostSettings.Fullscreen;
    options.Title = WorldApplicationDefaults.WindowTitle;
    options.Width = width;
});
// Registered before the launcher terminal block (AddWorldGpuHost → AddLauncherTerminal) so the launcher's
// TryAddSingleton<LauncherOptions> defers to this one. A null target selects automatic display pacing from verified
// VRR capabilities or active signal timing; the world.fps verb observes the result.
services.AddSingleton(implementationInstance: new LauncherOptions {
    ExitAfter = ((hostSettings.ExitAfterSeconds > 0) ? TimeSpan.FromSeconds(value: hostSettings.ExitAfterSeconds) : null),
    TargetRenderRate = hostSettings.TargetRenderRate,
});
services.AddSingleton(implementationInstance: new PresentationOptions {
    PresentMode = hostSettings.PresentMode,
    SurfaceFormat = hostSettings.SurfaceFormat,
});
// The external-clock election policy from the host section's genlock field. Registered BEFORE AddWorldGpuHost →
// AddLauncherTerminal so the launcher's TryAddSingleton<ExternalClockRegistry> defers to this one. An explicitly-null
// genlock (every shipped world writes the host section) is the launcher's automatic election.
services.AddSingleton(implementationInstance: new ExternalClockRegistry(electionPolicy: hostSettings.Genlock));

// The storage host-section: the world doc's reserved endpoint + user-id, overlaid by the --storage-uri /
// --user-id CLI reflection. RESERVED — nothing constructs an Azure target from these yet. The identity resolver
// maps an explicit user-id to a per-user container Guid, or DECLINES (local-only); its result feeds storage.status.
var storageSettings = WorldStorageSettings.Resolve(
    defaults: worldSource.Definition.Storage,
    endpointOverride: parseResult.GetValue(option: storageUriOption),
    userIdOverride: parseResult.GetValue(option: userIdOption)
);
services.AddSingleton(implementationInstance: storageSettings);
services.AddSingleton(implementationInstance: IPlayerStorageIdentityResolver.Create(settings: storageSettings));

// The player's controls as DATA: the engine-default binding document (WASD/arrows movement, Space/South/East
// gestures, Enter/F1-F4 roster, sticks, Start),
// composed per seat with the world's binding overlays, the seat's profile bindings, and its live session rebinds. One
// WorldSeatBindings resolves every seat's input; both input consumers derive from the same composed documents — the
// per-seat sim-fold (the IInputBindings handed to AddFixedStepSimulation) and the slot-blind console dispatch
// (BindingCommandSource, built from the composed base; in World the router owns all physical input, so this consumer
// is dormant). Constructed here (before the container builds) with the engine default and boot overlays; the roster,
// the rebind verbs, and the post-step overlay sync push the per-seat and overlay layers in as they change.
var seatBindings = new WorldSeatBindings(engineDefault: WorldDefaultBindings.BuildDocument(), overlays: worldSource.Definition.BindingOverlays);
services.AddSingleton(implementationInstance: seatBindings);
services.AddSingleton(implementationFactory: _ => new BindingCommandSource(bindings: seatBindings.ConsoleBaseTable()));

// The player-profile catalog (persisted locally, cloud-ready behind the same storage seam): loaded once at startup,
// version-mismatch reseeded, malformed-doc fallback — the roster and the settings verbs read it live.
services.AddWorldProfiles();

// The participant roster (up to four players, one avatar + viewport each; player 1 always joined, seated on the boot
// profile) and its console/keyboard verb surface, plus the real-time profile/settings verbs (aggregated into the
// CommandRegistry with every other module).
services.AddSingleton<PlayerRoster>();
services.AddSingleton<IInputSlotResolver>(implementationFactory: static sp => sp.GetRequiredService<PlayerRoster>());
services.AddSingleton<ICommandModule, PlayerCommandModule>();
services.AddSingleton<ICommandModule, ProfileCommandModule>();
// The rebind surface — player.bind (live session remap + chord rows) / player.bindings (echo the composed active
// mapping) / player.signal (synthesized raw input over the pipe) / profile.save (fold session rebinds into the seat's
// profile through the server-owned player document). A SEPARATE module to keep each class under its analyzer ceilings.
// The router reaches the module LAZILY: the router's factory consumes the CommandRegistry, which aggregates every
// ICommandModule — a direct dependency would cycle the container.
services.AddSingleton<Func<InputRouter>>(implementationFactory: static sp => (() => sp.GetRequiredService<InputRouter>()));
services.AddSingleton<ICommandModule, WorldBindingCommandModule>();
// The per-seat editor mode: the mode owner (binding MODE layer + honest-idle diversion + camera rig swap),
// the drag preview channel (client-local pending rows, one mutation on release), the look-ray picker (a document-
// derived fixed-point program), and the selection/targeting state — plus the two editor.* verb modules (SEPARATE
// modules for the analyzer ceilings). The orbit pivot retargets at the selection via property injection (targeting
// composes after the session).
services.AddSingleton<WorldEditorDrag>();
// The sculpt workbench: the per-seat creation sub-editor's client context — its preview creation/placement
// compose over the delivered rows through the SAME stamp path a committed placement uses. The drag channel's ghost
// envelope pre-checks fold the workbench preview in (property-injected — the workbench composes after the drag).
services.AddSingleton(implementationFactory: static sp => {
    var workbench = new WorldWorkbench(
        client: sp.GetRequiredService<WorldClient>(),
        envelope: sp.GetRequiredService<WorldRenderEnvelope>(),
        drag: sp.GetRequiredService<WorldEditorDrag>()
    );

    sp.GetRequiredService<WorldEditorDrag>().CandidateComposer = workbench.ComposeCandidate;

    return workbench;
});
services.AddSingleton<WorldEditorSession>();
services.AddSingleton<WorldEditorPicker>();
services.AddSingleton(implementationFactory: static sp => {
    var targeting = new WorldEditorTargeting(
        client: sp.GetRequiredService<WorldClient>(),
        picker: sp.GetRequiredService<WorldEditorPicker>(),
        session: sp.GetRequiredService<WorldEditorSession>()
    );

    var session = sp.GetRequiredService<WorldEditorSession>();

    session.OrbitPivotSource = targeting.SelectionPosition;
    // Deactivation (exit / departed seat) clears the seat's selection with its drag (the teardown contract every
    // deactivation path must honor).
    session.SelectionReset = slot => targeting.Deselect(slot: slot);

    return targeting;
});
services.AddSingleton<ICommandModule, EditorCommandModule>();
services.AddSingleton<ICommandModule, EditorSelectionCommandModule>();
// The speaker authoring numeric twins — console-only by an honest chord audit (every place-page slot is
// spoken for); a SEPARATE module for the analyzer ceilings.
services.AddSingleton<ICommandModule, EditorSpeakerCommandModule>();
// The sculpt verb surface: lifecycle/commit/easel, shapes, style, and timeline/rig — SEPARATE modules per
// concern to keep every class under its analyzer ceilings.
services.AddSingleton<ICommandModule, EditorSculptCommandModule>();
services.AddSingleton<ICommandModule, EditorSculptShapeCommandModule>();
services.AddSingleton<ICommandModule, EditorSculptStyleCommandModule>();
services.AddSingleton<ICommandModule, EditorSculptRigCommandModule>();
// The creation-asset surface: editor.import/creations/creation.next|prev/spawn.creation — the place page's
// place-by-name twins. The animated-placement replay pool sits immediately after the avatar catalog's frozen
// dynamic-transform capacity (the slot-base contract the frame source's capacity arithmetic mirrors).
services.AddSingleton<ICommandModule, EditorCreationCommandModule>();
services.AddSingleton(implementationInstance: new WorldStampPool(slotBase: WorldAvatarCatalog.DynamicTransformCapacity));

// The audio director: derives the emitter table from the delivered definition, resolves poses per produced
// frame (the frame source calls it inside CaptureFrame), and publishes WorldAudioSnapshots for the device pump.
// Registered as its own singleton so the audio verb surface (audio.emitters) reads the same instance.
services.AddSingleton(implementationFactory: static sp => new WorldAudioDirector(
    client: sp.GetRequiredService<WorldClient>(),
    animator: sp.GetRequiredService<WorldStampPool>()
));
// The world speaker device: the hosted service owning the mixer + the WASAPI governor/pump threads.
// One dedicated bounded-join worker owns the device lifecycle, so a stalled device cannot wedge shutdown; a
// platform without a render backend gets a null factory and the service parks as 'unsupported'. Registered as its
// own singleton FIRST so the audio verb surface (audio.state) reads the same instance the host runs.
services.AddSingleton(implementationFactory: static sp => new WorldAudioRenderService(
    director: sp.GetRequiredService<WorldAudioDirector>(),
    factory: AudioRenderPlatform.CreateFactory()
));
services.AddHostedService(implementationFactory: static sp => sp.GetRequiredService<WorldAudioRenderService>());

// The server's entity table — the four local seats plus up to 124 network stand-ins the world.population verb
// activates — the one body system the snapshot reports (up to 128 avatars: the scale target).
services.AddSingleton<WorldPopulation>();

// The render-capacity oracle the server consults before applying a scene/screen mutation — configured by the frame
// source once it has probed the boot envelope, so an over-envelope edit is rejected loudly at apply time.
services.AddSingleton<WorldRenderEnvelope>();

// The authoritative world server and the in-process loopback fronting it: the client submits intents, commands,
// session requests, and buffered live edits (mutations, definition swaps, journal undo) over IServerLink; the server
// applies them at its step boundary, answers queries, and pushes each tick's snapshot (and, after an applied edit, the
// new definition) to the bound client sink.
services.AddSingleton<WorldServer>();
services.AddSingleton<LoopbackTransport>();
services.AddSingleton<IServerLink>(implementationFactory: static sp => sp.GetRequiredService<LoopbackTransport>());

// The addon principals: mounts the world document's enabled addon rows through a Puck.Scripting
// AddonHost (consumed, never modified) and drives each granted addon over the SAME IServerLink as a seat. Ticked by
// WorldSimulation between the seat-intent submit and the server step. DI disposes it (the owned Wasmtime engine/stores).
services.AddSingleton(implementationFactory: static sp => WorldAddonDriver.Create(
    definition: sp.GetRequiredService<WorldDefinition>(),
    link: sp.GetRequiredService<IServerLink>(),
    server: sp.GetRequiredService<WorldServer>()
));

// The shared live composition-override store — written by DeliverComposition (an accepted view.layout/view.camera), read
// by the frame source's view composer. One instance shared by the client and the frame source.
services.AddSingleton<WorldCompositionState>();
// The window composer — layout selection + eased transitions. One shared instance the frame source drives each produced
// frame and the world.view.state read observes.
services.AddSingleton<WorldViewComposer>();

// The client half: the snapshot-fed entity view + per-tick seat-intent submitter, bound to the loopback at
// construction (the bind delivers a primer snapshot so the render path sees the boot state before the first tick).
services.AddSingleton(implementationFactory: static sp => {
    var client = new WorldClient(
        roster: sp.GetRequiredService<PlayerRoster>(),
        link: sp.GetRequiredService<IServerLink>(),
        definition: sp.GetRequiredService<WorldDefinition>(),
        composition: sp.GetRequiredService<WorldCompositionState>()
    );

    sp.GetRequiredService<LoopbackTransport>().Bind(sink: client);

    return client;
});

// The engagement route — the seat/entry → screen table (where a player's intent GOES). Shared by the screen binder
// (pulls each engaged screen's OR-merged joypad buttons per frame), the player.engage/disengage verbs, and the
// world.screens / screen.state echoes.
services.AddSingleton<WorldEngagement>();

// The frame-rate witness, the live render settings (console-mutated in real time — the graphics-options verbs), the
// render probe (the world.gpu verb reads the live engine's per-pass GPU times through it), and the world's own verb
// surface (world.fps/.shadows/.ao/.render-scale/.target/.quality/.timing/.gpu — the graphics menu over the pipe).
services.AddSingleton<FrameRateMonitor>();
// The live render settings boot from the definition's render-lever defaults (then the console verbs move them live).
services.AddSingleton(implementationFactory: static sp => new WorldRenderSettings(defaults: sp.GetRequiredService<WorldDefinition>().Render));
services.AddSingleton<WorldRenderProbe>();
// The live-content platform seams the screen binder pulls CPU pixels through: the webcam (Media Foundation on Windows,
// the CPU tier — the dormant GPU zero-copy tier is untouched) and compositor-owned desktop-window capture. Registered here so a
// camera/capture screen resolves them from DI rather than self-constructing a backend.
services.AddCameraCapture();
services.AddSingleton<INativeImageCaptureService>(implementationFactory: static _ =>
    (OperatingSystem.IsWindows()
        ? new Win32NativeImageCaptureService()
        : new NullNativeImageCaptureService()));

// The screen-machine engines — the ONLY concrete-machine composition-root wiring in World. A declared or inserted
// machine screen resolves against this DI-collected set by engine id; the world speaks the neutral IScreenMachine
// contract everywhere else. The Advanced engine is the native ARM7TDMI tier, not the SM83 adapter's AGB costume.
services.AddSingleton<IScreenMachineEngine, GamingBrickEngine>();
services.AddSingleton<IScreenMachineEngine, AdvancedGamingBrickEngine>();

// The screen binder — owns the declared screens' CPU-fed GPU sources (test patterns, booted machines, the shared webcam,
// window captures); shared by the render factory (its provider maps feed the render spec, and the frame source publishes
// through it each frame) and the world.screens verb.
services.AddSingleton(implementationFactory: sp => new WorldScreenBinder(
    // Reserve the derived-face slot range up front (None-sourced placeholders) so a creation FACE appearing at a later
    // delivery re-points a slot that already exists — the render provider key set is frozen at boot (Arc 7).
    screens: [.. sp.GetRequiredService<WorldDefinition>().Screens, .. Puck.World.Client.WorldCreationFacets.ReservedFaceSlots(derivedFaceBase: Puck.World.Client.WorldCreationFacets.DerivedFaceBase, derivedFaceScreens: sp.GetRequiredService<WorldDefinition>().Authoring.DerivedFaceScreens)],
    engagement: sp.GetRequiredService<WorldEngagement>(),
    engines: sp.GetServices<IScreenMachineEngine>(),
    cameraCapture: sp.GetRequiredService<ICameraCaptureService>(),
    windowCapture: sp.GetRequiredService<INativeImageCaptureService>(),
    cameras: sp.GetRequiredService<WorldDefinition>().Cameras,
    anchors: sp.GetRequiredService<WorldClient>(),
    // On the D3D12 host the window/monitor capture feeds publish GPU-side into shared textures the screens sample
    // directly; the Vulkan host keeps the CPU-pixel transport. Camera stays CPU everywhere.
    hostsOnDirectX: hostsOnDirectX
));
services.AddSingleton<ICommandModule, WorldCommandModule>();
// The world-mutation verb surface — the dev reflection of the WorldMutation protocol (world.kit.*/screen.*/scene.*/…,
// world.load/undo/status/save). A SEPARATE module from WorldCommandModule to keep that class under its analyzer ceilings.
services.AddSingleton<ICommandModule, WorldMutationCommandModule>();
// The contact/solidity verb surface — world.collision(.on/.off/.skin/.slope/.gradient/.provider), world.kit.collider/
// .model/.response, world.scene.solid, and the world.contacts read. A SEPARATE module for the analyzer ceilings.
services.AddSingleton<ICommandModule, WorldCollisionCommandModule>();
// The LOOK verb surface — world.look.set/.remove/.assign/.tune, world.population.spawn (the spawn-policy RMW), and the
// world.looks census. A SEPARATE module for the analyzer ceilings.
services.AddSingleton<ICommandModule, WorldLookCommandModule>();
// The Arc 7 inhabitation + creation-facet verb surface (world.placement.inhabit/.face, world.kit.attend,
// world.inhabitants, world.faces). A SEPARATE module (WorldMutationCommandModule is at its analyzer ceiling).
services.AddSingleton<ICommandModule, WorldPlacementCommandModule>();
// The capability-grant verb surface — world.grant/world.revoke/world.grants (the principal/grant control plane).
// A SEPARATE module from WorldCommandModule/WorldMutationCommandModule to keep every class under its analyzer ceilings.
services.AddSingleton<ICommandModule, WorldGrantCommandModule>();
// The host-section verb surface — world.host (the DOCUMENT/RESOLVED/LIVE read-back) + world.host.set/world.host.tune.
// A SEPARATE module because the world.host read needs PresentPacingControl + GpuTimingControl, which would push
// WorldMutationCommandModule past its analyzer ceiling.
services.AddSingleton<ICommandModule, WorldHostCommandModule>();
// The window-composition verb surface — world.view.rig/.layout.set/.remove (durable), view.layout/view.camera (live
// overrides), and the world.view.state read. A SEPARATE module for the analyzer ceilings.
services.AddSingleton<ICommandModule, WorldViewCommandModule>();
// The audio verb surface — world.speaker.*/tune.*/patch.*/audio.set + world.speakers/audio.emitters (the
// mutation twins and the derived-emitter listing). A SEPARATE module for the analyzer ceilings.
services.AddSingleton<ICommandModule, WorldAudioCommandModule>();
// The diegetic screens' verb surface — screen.insert/eject boot cabinets over the wire, screen.state/peek make the
// emulated brick state pipe-assertable.
services.AddSingleton<ICommandModule, ScreenCommandModule>();
// The storage verb surface — storage.status, the honest echo of the player-catalog persistence state (tier, identity,
// reserved endpoint, per-catalog revision/sync/token). A SEPARATE module to keep every class under its analyzer ceilings.
services.AddSingleton<ICommandModule, WorldStorageCommandModule>();

// The recording graph (puck.recording.v1) — native capture for streaming/upload workflows, defined as data. The recording document is
// HOST-scope data (like the storage host-section): resolved once at boot from --recording (or the checked-in
// Assets/recordings/default.recording.json), it describes the encoder ladder, audio topology, and capture-only overlays.
// AddRecordingPlatform registers the Media Foundation encoder ladder + WASAPI loopback/microphone sources (declining
// factories off Windows) and the shared session clock. The RecordingTap is the swappable sink the capture render node is
// wired to for its whole lifetime; capture.start/stop arm and finalize a RecordingSession through it.
services.AddRecordingPlatform();
services.AddSingleton(implementationInstance: RecordingDocumentLoader.Load(explicitPath: parseResult.GetValue(option: recordingOption)));
services.AddSingleton<RecordingTap>();
services.AddSingleton<ICommandModule, WorldRecordingCommandModule>();

// The true-deterministic-replay tape (the seed of a future Puck.Replay) — captures the running session's per-tick
// server-input stream + starting state off the loopback, and rehydrates a fresh world to verify a recorded-vs-replayed
// hash match offline. WorldSimulation closes each captured tick inside Step; the replay.* verb surface arms and verifies
// it. Immediate verbs, a live non-document surface (the capture.* precedent). Constructed here so the sim and the verbs
// share it.
services.AddSingleton(implementationFactory: static sp => new WorldReplayTape(
    liveServer: sp.GetRequiredService<WorldServer>(),
    profiles: sp.GetRequiredService<WorldProfiles>(),
    transport: sp.GetRequiredService<LoopbackTransport>()
));
services.AddSingleton<ICommandModule, WorldReplayCommandModule>();

// Controllers, first-class beside the keyboard: the hardware manager (HID + the Xbox XInput/GameInput poll thread),
// the focus-gated snapshot capture binding the sticks to the player's Axis2D channels, and the hosted service that
// governs device lifetime (hotplug rescans every ~1.5 s). A new pad reserves a logical lane during capture without
// changing sim state; the first tick snapshot commits the local routing annotation and performs any join from that
// recorded lane. Each pad drives its own avatar; the first pad shares player one's avatar with the keyboard.
if (OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)) {
    services.AddSingleton(implementationFactory: static sp => new GamepadManager(
        acquisitionSource: new Win32XboxAcquisitionSource(diagnostics: static message => Console.Error.WriteLine(value: message)),
        clock: sp.GetRequiredService<IInputClock>(),
        diagnostics: static message => Console.Error.WriteLine(value: message),
        hidSource: new Win32HidDeviceSource()
    ));
    services.AddSingleton<IInputArbiter>(implementationFactory: static sp => new InputArbiter(manager: sp.GetRequiredService<GamepadManager>()));
    services.AddSingleton<ISnapshotInputCapture>(implementationFactory: static sp => new GamepadSnapshotInputCapture(
        arbiter: sp.GetRequiredService<IInputArbiter>(),
        router: sp.GetRequiredService<InputRouter>(),
        clock: sp.GetRequiredService<IInputClock>(),
        isActiveFor: sp.GetRequiredService<IInputFocus>().IsActiveFor
    ));
    services.AddHostedService<GamepadHostedService>();
}

// The screen-space overlay UI (Puck.Overlays): the console mirror, the per-seat binding bars, and the mutation
// toasts, all drawn by the ONE UnifiedOverlayNode wrapped around the world render below. The stores are the
// lock-free read seams; the mirror is stdin/stdout's visible twin (the unification contract's "on-screen panel AND
// stdin"). The TextCommandSource is registered HERE — before AddLauncherTerminal's TryAdd — so the pump's result
// callback both echoes to stdout (scripted runs stay assertable) and records into the mirror.
services.AddSingleton<ConsolePanelStore>();
services.AddSingleton<BindingBarStore>();
services.AddSingleton<EditorHudStore>();
services.AddSingleton<EditorGizmoStore>();
services.AddSingleton<OverlayToastStore>();
services.AddSingleton<WorldConsoleMirror>();
services.AddSingleton(implementationFactory: static sp => {
    var mirror = sp.GetRequiredService<WorldConsoleMirror>();
    var output = sp.GetRequiredService<BufferedConsoleOutput>();

    return new TextCommandSource(
        onResult: (line, result) => {
            if (!string.IsNullOrEmpty(value: result.Output)) {
                // A REFUSED line goes to stderr, an accepted one to the buffered stdout — the same split the launcher's
                // own sink makes (this one replaces it to also feed the on-screen console mirror). Without it a
                // rejection is byte-shaped like success on the same stream and a scripted run reads green.
                if (result.IsError) {
                    output.WriteErrorLine(value: result.Output);
                } else {
                    output.WriteLine(value: result.Output);
                }
            }

            mirror.Record(line: line, result: result);
        },
        registry: sp.GetRequiredService<CommandRegistry>()
    );
});
services.AddSingleton(implementationFactory: static sp => new WorldOverlayFeed(
    binder: sp.GetRequiredService<WorldScreenBinder>(),
    bindings: sp.GetRequiredService<WorldSeatBindings>(),
    client: sp.GetRequiredService<WorldClient>(),
    drag: sp.GetRequiredService<WorldEditorDrag>(),
    editor: sp.GetRequiredService<WorldEditorSession>(),
    editorHudStore: sp.GetRequiredService<EditorHudStore>(),
    gamepads: sp.GetService<GamepadManager>(),
    population: sp.GetRequiredService<WorldPopulation>(),
    roster: sp.GetRequiredService<PlayerRoster>(),
    router: sp.GetRequiredService<InputRouter>(),
    server: sp.GetRequiredService<WorldServer>(),
    settings: sp.GetRequiredService<WorldRenderSettings>(),
    store: sp.GetRequiredService<BindingBarStore>(),
    targeting: sp.GetRequiredService<WorldEditorTargeting>(),
    workbench: sp.GetRequiredService<WorldWorkbench>(),
    audio: sp.GetRequiredService<WorldAudioDirector>(),
    pacing: sp.GetRequiredService<PresentPacingControl>()
));
// The overlay verb surface — world.screenshot (the composed-frame capture) + world.console (the mirror toggle).
services.AddSingleton<ICommandModule, WorldUiCommandModule>();

// The shared easy path owns the one fixed-step accumulator, turns every physical/console input into a per-tick snapshot,
// applies it, and invokes WorldSimulation. Rendering below only consumes interpolation state.
services.AddFixedStepSimulation<WorldSimulation>(bindings: seatBindings);

// The trimmed GPU host (windowing, allocator, one complete launch-selected backend), minus the demo-only
// camera-capture concern. Registering only the selected backend ensures the
// neutral compute services and presenter name the same physical device and shader format.
WorldHost.AddWorldGpuHost(services: services, hostsOnDirectX: hostsOnDirectX);

// The render root: the shared SDF world assembly over the grass-and-boulders scene. The built Producer (the live
// SdfEngineNode) is stashed on the WorldRenderProbe so the world.gpu verb can read its per-pass GPU times. The frame
// source emits active avatars only (declared-but-parked instances widen the per-pixel shadow mask walk), so the
// 128-avatar worst case is held by the capacity floors a construction-time probe measured, plus the viewport floor for
// the join-later split screen.
services.AddSingleton<IRenderNode>(implementationFactory: sp => {
    var binder = sp.GetRequiredService<WorldScreenBinder>();
    var frameSource = new WorldFrameSource(
        frameRate: sp.GetRequiredService<FrameRateMonitor>(),
        client: sp.GetRequiredService<WorldClient>(),
        simulation: sp.GetRequiredService<WorldSimulation>(),
        settings: sp.GetRequiredService<WorldRenderSettings>(),
        binder: binder,
        envelope: sp.GetRequiredService<WorldRenderEnvelope>(),
        editor: sp.GetRequiredService<WorldEditorSession>(),
        targeting: sp.GetRequiredService<WorldEditorTargeting>(),
        drag: sp.GetRequiredService<WorldEditorDrag>(),
        animator: sp.GetRequiredService<WorldStampPool>(),
        workbench: sp.GetRequiredService<WorldWorkbench>(),
        audio: sp.GetRequiredService<WorldAudioDirector>(),
        gizmos: sp.GetRequiredService<EditorGizmoStore>(),
        composition: sp.GetRequiredService<WorldCompositionState>(),
        composer: sp.GetRequiredService<WorldViewComposer>()
    );

    // Stand up the jumbotron view pool now the frame source has probed the render envelope: each View screen registers a
    // persistent offscreen camera render sized to these worst-case capacities, using the selected host's bytecode.
    // A no-op when the world declares no View screen.
    binder.ConfigureViews(
        services: sp,
        hostsOnDirectX: hostsOnDirectX,
        programWordCapacity: frameSource.ProgramWordCapacity,
        instanceCapacity: frameSource.InstanceCapacity,
        dynamicTransformCapacity: frameSource.DynamicTransformCapacity
    );
    // Edit-boundary outcomes narrate into the overlay toast beside their loud stderr lines, grown to grant/revoke
    // outcomes, and applied mutations stamp the HUD's act-class tag.
    var toasts = sp.GetRequiredService<OverlayToastStore>();
    var overlayFeed = sp.GetRequiredService<WorldOverlayFeed>();
    var editorDrag = sp.GetRequiredService<WorldEditorDrag>();
    var editorWorkbench = sp.GetRequiredService<WorldWorkbench>();
    var audioDirector = sp.GetRequiredService<WorldAudioDirector>();

    sp.GetRequiredService<WorldServer>().EchoTap = echo => {
        toasts.Publish(message: echo.Message, isError: echo.Rejected);

        // Only applied DOCUMENT edits stamp the act-class tag — grant-table changes narrate as toasts alone.
        if (!echo.Rejected && (echo.Kind != WorldEditEchoKind.GrantTable)) {
            overlayFeed.NoteMutationApplied(documentOnly: (echo.Kind == WorldEditEchoKind.DocumentDefaults));
        }

        // A rejected mutation correlates back to the frozen released drag preview that submitted it: the
        // matched seat's overlay retires NOW and the row snaps honestly back, instead of waiting out the deadline.
        if (echo.Rejected && (echo.Mutation is { } rejectedMutation)) {
            editorDrag.NoteRejected(mutation: rejectedMutation);
            // A rejected sculpt commit clears its bench's pending flag WITHOUT flipping clean — the work stays
            // counted as uncommitted (the accept, in WorldWorkbench.Tick, is the only clean edge).
            editorWorkbench.NoteCommitRejected(mutation: rejectedMutation);
        }

        // THE EDIT-ECHO CUE LANE (the shimmer's audio twin): the same outcome fires its cue token —
        // capability denials as grant.denied, other rejections as mutation.rejected, applied edits as
        // mutation.applied AT the changed row's authored position where the mutation payload carries one (an upsert;
        // removals and section edits fall back to the listener placement). Cue coverage is world DATA — a world with
        // no cue rows hears nothing.
        if (echo.Denied) {
            audioDirector.SubmitCue(eventToken: WorldAudioCue.GrantDenied, site: null);
        } else if (echo.Kind != WorldEditEchoKind.GrantTable) {
            audioDirector.SubmitCue(
                eventToken: (echo.Rejected ? WorldAudioCue.MutationRejected : WorldAudioCue.MutationApplied),
                site: WorldAudioDirector.MutationSite(mutation: echo.Mutation)
            );
        }
    };

    // THE BINDER LIFECYCLE CUE LANE: machine boot/fault outcomes fire screen.boot / screen.fault at the
    // screen row's authored face origin (resolved from the LIVE definition at event time; an undeclared index falls
    // back to the listener placement). Pump-thread invocation; SubmitCue is gate-safe.
    var audioCueClient = sp.GetRequiredService<WorldClient>();

    binder.MachineLifecycleTap = (index, faulted) => {
        Vector3? site = null;

        foreach (var screen in audioCueClient.Definition.Screens) {
            if (screen.Index == index) {
                site = screen.Origin;

                break;
            }
        }

        audioDirector.SubmitCue(eventToken: (faulted ? WorldAudioCue.ScreenFault : WorldAudioCue.ScreenBoot), site: site);
    };

    // Captured out of the Decorate closure so the probe can expose the overlay's pass timing (world.gpu).
    UnifiedOverlayNode? overlayNode = null;
    var render = SdfWorldRenderBuilder.Build(
        serviceProvider: sp,
        spec: new SdfWorldRenderSpec(
            FrameSource: frameSource,
            Width: width,
            Height: height
        ) {
            // The unified overlay (console mirror + per-seat binding bars + toasts) wraps the producer on BOTH
            // backends: neutral services, bytecode selected by the resolved host. Degrades loudly to the bare world
            // when the pre-baked glyph atlas is missing.
            Decorate = producer => {
                var fontsDirectory = Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Fonts");
                // The prepacked-artifact path: a warm start reads the ~1.4 MiB pack beside the atlas; only a
                // cold/rebaked start decodes the combined PNG (and persists the pack for the next boot).
                var glyphs = new OverlayGlyphAtlasSet(fontsDirectory: fontsDirectory).LoadOverlayPack();

                if (glyphs is null) {
                    Console.Error.WriteLine(value: $"[unified-overlay] skipped: no usable glyph atlas under '{fontsDirectory}' (rebake via tools/font-atlas).");

                    return producer;
                }

                var bytecodeExtension = SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: hostsOnDirectX);

                return overlayNode = new UnifiedOverlayNode(
                    fragmentBytecode: File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: ("overlay-unified.frag" + bytecodeExtension))),
                    glyphs: glyphs,
                    height: height,
                    inner: producer,
                    services: OverlayServices.Build(hostsOnDirectX: hostsOnDirectX, serviceProvider: sp),
                    sources: new UnifiedOverlaySources(
                        BindingBar: sp.GetRequiredService<BindingBarStore>(),
                        Console: sp.GetRequiredService<ConsolePanelStore>(),
                        EditorHud: sp.GetRequiredService<EditorHudStore>(),
                        FeedTick: sp.GetRequiredService<WorldOverlayFeed>().Tick,
                        Gizmos: sp.GetRequiredService<EditorGizmoStore>(),
                        Toast: sp.GetRequiredService<OverlayToastStore>()
                    ),
                    vertexBytecode: File.ReadAllBytes(path: Path.Combine(path1: SdfWorldKernels.DefaultDirectory, path2: ("fullscreen.vert" + bytecodeExtension))),
                    width: width
                );
            },
            DynamicTransformCapacity = frameSource.DynamicTransformCapacity,
            HostsOnDirectX = hostsOnDirectX,
            InstanceCapacity = frameSource.InstanceCapacity,
            ProgramWordCapacity = frameSource.ProgramWordCapacity,
            // The ray-query hardware path from the host section — World previously left this unset, so SdfEngineNode fell
            // back to the PUCK_RAY_QUERY env read (an env var no world document could see); now the document decides.
            RayQuery = hostSettings.RayQuery,
            // The diegetic screens' source + light providers — the test-pattern screen's CPU feed and its room glow;
            // an unbound screen has no provider (the engine's procedural fallback lights it).
            ScreenLights = binder.ScreenLights,
            ScreenSources = binder.ScreenSources,
            ViewportCapacity = PlayerRoster.MaxSlots,
        }
    );

    var probe = sp.GetRequiredService<WorldRenderProbe>();

    probe.Node = render.Producer;
    // world.screenshot arms captures through the render host (routes to the outermost decorator).
    probe.Render = render;
    probe.Overlay = overlayNode;

    // The native-capture present tap: wrap the render root once, for the world's whole lifetime, in the backend-neutral
    // CapturingRenderNode. The live windowed present path hands GPU surfaces, so the tap reads each captured frame back
    // to CPU pixels through the SDF engine (probe.Node.ReadOutputPixels) — a synchronous GPU readback that runs ONLY
    // while a session is armed (the RecordingTap.WantsFrames gate), so the tap is free until capture.start. The capture
    // cadence keeps roughly the recording document's frame rate out of the desktop 120 Hz target.
    var recordingDocument = sp.GetRequiredService<RecordingDocumentSource>().Document;
    var tap = sp.GetRequiredService<RecordingTap>();

    // The teardown tie: the window loop disposes this root (device alive) before the presenter and long before the
    // container's reverse-creation-order sweep — ride that safe point for the binder's own GPU holdings (camera
    // feeds, jumbotron view engines), whose container-ordered disposal would otherwise land after device death.
    return new WorldRenderTeardown(
        inner: new CapturingRenderNode(
            inner: render.Root,
            sink: tap,
            options: new CaptureOptions {
                Enabled = true,
                FrameRate = (recordingDocument.Video?.FrameRate ?? 60),
                MaxFrames = 0,
                SourceFrameRate = 120,
            },
            captureGate: () => tap.WantsFrames,
            cpuReadback: () => (probe.Node?.ReadOutputPixels() ?? default)
        ),
        binder
    );
});
await builder.Build().RunAsync();
return 0;
