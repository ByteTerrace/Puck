using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Platform.Windows;
using Puck.World;
using Puck.World.Protocol;

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
    Description = "The world definition file (puck.world.def.v1) to load. A missing or invalid file FAILS the boot with a named reason and exit 1. Absent, the shipped Assets/worlds/nexus.world.json beside the executable loads; failure to load that document also fails the boot.",
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
// NOT a host.* reflection — connecting is inherently this one run's initial transport target. It selects the
// remote authority beneath the normal boot composition; rendering, commands, input, and routing remain unchanged.
var connectOption = new Option<string?>(name: "--connect") {
    DefaultValueFactory = static _ => null,
    Description = "Boot into a remote authority (an \"ip:port\" pair) through the normal window/headless composition. --world supplies the initial definition until the authority's definition revision arrives.",
};
var federationKeyFileOption = new Option<string?>(name: "--federation-key-file") {
    DefaultValueFactory = static _ => null,
    Description = "A deployment-secret file holding this authority's own PKCS8 ECDSA P-256 private key (raw DER bytes) — the SignsDirectly signing identity peers pin against this world's host.authority (or its \"boot\" instance identity when host.authority is absent). Absent disables federation while leaving ordinary admitted-peer listening available.",
};
// AddSelfUpdate is always registered (channel/cacheRoot/checkInterval/keepVersions come from the world document's
// update section — see WorldUpdateDefaults — and the trust anchor is the build-pinned constant below). This
// narrows to the two facets a document must never author — the release-source directory and the trust anchor —
// the same test/ops control-plane category as --federation-key-file. Absent, self-update stays wired but harmless:
// the release source resolves to an empty directory and the trust anchor stays the refusing placeholder.
var updateConfigFileOption = new Option<string?>(name: "--update-config-file") {
    DefaultValueFactory = static _ => null,
    Description = "A test/ops-only override for the release-source directory and the trust anchor (ReleaseSourceDirectory/TrustAnchor*) — see Puck.Launcher.Release.SelfUpdateConfigFile. Absent, self-update runs against an empty release source and the refusing build-time placeholder trust anchor.",
};
var launchCommand = new RootCommand(description: "Puck World") {
    backendOption,
    connectOption,
    federationKeyFileOption,
    exitAfterSecondsOption,
    headlessOption,
    stateDirOption,
    heightOption,
    listenOption,
    presentModeOption,
    recordingOption,
    storageDiscoveryUriOption,
    storageUriOption,
    updateConfigFileOption,
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
var connectTarget = parseResult.GetValue(option: connectOption);
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
// The world definition (see WorldDefinition) — a --world file or the shipped Assets/worlds/nexus.world.json beside
// the executable, loaded / schema-checked / validated (see WorldDefinitionLoader). LOADED BEFORE the
// window/launcher/presentation registrations because those now read their values from the resolved host section. Read
// by DI from the roster, population, frame source, render settings, and the world.quality verb; the resolved source is
// registered so world.save knows its default target. Any path that will not load ends the boot here — a typo or missing
// shipped document must never quietly run a different world.
if (!WorldDefinitionLoader.TryResolve(
    explicitPath: parseResult.GetValue(option: worldOption),
    source: out var worldSource,
    failure: out var worldFailure
)) {
    Console.Error.WriteLine(value: worldFailure);

    return 1;
}
// The federation identity door — deny-by-default, mirroring --listen's own absent-means-closed posture: an
// unconfigured authenticator refuses the federation dialect outright at WorldTcpHost's IsConfigured gate, leaving
// ordinary admitted-peer listening (the interactive attestation door) untouched. A configured one signs
// SignsDirectly claims under this run's own pinned key, naming this document's host.authority as the subject a
// peer's own admission entries pin against; verification reads the CURRENT document's admission rows fresh on
// every attempt, so a live world.reload/edit is honored the same way the interactive door already is.
Puck.Networking.IAuthenticator authenticator = new Puck.World.Server.WorldAttestedAuthenticator();
if (parseResult.GetValue(option: federationKeyFileOption) is { } federationKeyFile) {
    // The exact fallback WorldServer.AuthorityIdentity itself applies for the boot instance (host.authority absent
    // means "colocated with the resolver", per its own doc comment) — reusing it here means authoring a signing key
    // never has to also change what every LOCAL entity in this world is addressed under.
    var federationSubject = ((worldSource.Definition.Host.Authority is { Length: > 0 } authored)
        ? authored
        : Puck.World.WorldDefinitionLoader.BootInstanceName
    );

    try {
        var pkcs8 = File.ReadAllBytes(path: Path.GetFullPath(path: federationKeyFile));
        var key = System.Security.Cryptography.ECDsa.Create();

        key.ImportPkcs8PrivateKey(
            bytesRead: out _,
            source: pkcs8
        );

        authenticator = new Puck.World.Server.WorldAttestedAuthenticator(
            oracle: new Puck.World.Server.LocalKeySigningOracle(
                key: key,
                subject: federationSubject,
                validity: Puck.World.Server.WorldAttestedAuthenticator.MaximumClaimAge
            ),
            trustEntries: () => worldSource.Definition.Admission
        );
    } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)) {
        Console.Error.WriteLine(value: $"--federation-key-file could not be read: {exception.Message}");

        return 1;
    }
}
// Resolve the effective host settings: the world doc's host defaults (absence coalesced to WorldHostDefaults.Absent —
// no presentation; the standard windowed boot is authored in standard.world.json) overlaid by the nullable CLI flags.
// Backend authority differs by source — a CLI
// assertion the OS cannot satisfy hard-exits (World's current behavior), a document preference degrades to Vulkan loudly.
var directXAvailable = OperatingSystem.IsWindowsVersionAtLeast(
    major: 10,
    minor: 0,
    build: 10240
);
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
var width = ((uint)hostSettings.Width);
var height = ((uint)hostSettings.Height);
// The GPU-timing arm boots from the host section's timing field — the lowest-precedence seed (a live world.timing
// SetArmed always overrides). TrySeed is idempotent and claims the control only if nothing above has.
GpuTimingControl.Shared.TrySeed(armed: hostSettings.Timing);
var builder = Host.CreateApplicationBuilder(args: args);
var services = builder.Services;
services.AddSingleton(implementationInstance: worldSource);
services.AddSingleton(implementationInstance: worldSource.Definition);
services.AddSingleton<Puck.Networking.IAuthenticator>(implementationInstance: authenticator);
// The resolved host settings — read by the composition modules below and the world.host verb.
services.AddSingleton(implementationInstance: hostSettings);
// Registered before the launcher terminal block (AddLauncherTerminal/AddLauncherHeadlessTerminal, reached through
// AddWorldPresentation/AddHeadlessHost) so the launcher's TryAddSingleton<LauncherOptions> defers to this one, IN
// EITHER BOOT SHAPE — --exit-after-seconds applies to the headless tick host exactly like the windowed one. A null target
// selects automatic display pacing from verified VRR capabilities or active signal timing (windowed only).
services.AddSingleton(implementationInstance: new LauncherOptions {
    ExitAfter = ((hostSettings.ExitAfterSeconds > 0)
    ? TimeSpan.FromSeconds(value: hostSettings.ExitAfterSeconds)
    : null),
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
// The player's controls as DATA: the world's binding overlays (the engine ships none — a world names
// Assets/worlds/standard.world.json as its basis for the standard movement rows, or authors its
// own, or has none), composed per seat with the seat's profile bindings and its live session rebinds. One
// WorldSeatBindings resolves every seat's input, feeding the ONE input consumer there is: the per-seat sim-fold (the
// IInputBindings handed to AddFixedStepSimulation), whose router stamps each lane's acting principal. Constructed here
// (before the container builds) with the boot overlays; the roster, the rebind verbs, and the post-step overlay sync
// push the per-seat and overlay layers in as they change.
// The per-seat control-feel store is built here too, seeded from the boot document's own authored feel — the
// resolution every seat sits at until a profile is delivered for it, from wherever that profile arrives.
var seatBindings = new WorldSeatBindings(definition: worldSource.Definition);
services.AddSingleton(implementationInstance: seatBindings);
// Boot shape resolves before registration: the authoritative core registers in every shape; the GPU host, render
// root, overlays, audio device, screens/machines, gamepads, and editor register only when presentation is
// composed. See WorldBootComposition for the full split and WorldPostBuildWiring for the shared every-shape wiring.
services.AddWorldAuthoritativeCore();
if (hostSettings.Headless) {
    // No window, GPU device, swapchain, allocator, backend presenter, or audio device — the headless twin of the
    // block below (command pump + tick host). Nothing under AddWorldPresentation is ever called on this path.
    services.AddLauncherHeadlessTerminal();
    // The standalone high-resolution precision waiter for the headless tick host's pacing loop — Windows only,
    // registered by the composition root so Puck.Launcher stays platform-neutral. A no-op on an unsupported OS
    // version (the tick host falls back to a coarse sleep).
    if (OperatingSystem.IsWindows()) {
        services.AddWindowsPrecisionWaiter();
    }
    services.AddFixedStepSimulation<HeadlessWorldSimulation>(bindings: seatBindings);
} else {
    // The recording graph (puck.recording.v1) — native capture for streaming/upload workflows, defined as data.
    // PRESENTATION-ONLY (AddWorldPresentation registers the encoder ladder/capture controller/verb module), but the
    // document resolution itself can fail-and-exit, so it stays here beside World's other --world/--recording
    // loaders. Skipped headless: a GPU-less CI box has no reason to carry a valid recordings asset.
    if (!RecordingDocumentLoader.TryResolve(
        explicitPath: parseResult.GetValue(option: recordingOption),
        source: out var recordingSource,
        failure: out var recordingFailure
    )) {
        Console.Error.WriteLine(value: recordingFailure);

        return 1;
    }

    services.AddSingleton(implementationInstance: recordingSource);

    // The trimmed GPU host (windowing, allocator, one complete launch-selected backend), the render root, overlays,
    // the audio device, screens/machines verbs, and gamepads. Only the selected backend enters this
    // service provider so its neutral compute services and presenter name the same physical device and shader
    // format.
    services.AddWorldPresentation(hostsOnDirectX: hostsOnDirectX);

    // The shared easy path owns the one fixed-step accumulator, turns every physical/console input into a per-tick
    // snapshot, applies it, and invokes WorldSimulation (client + screens + editor, over the shared server-step
    // shell). Rendering consumes interpolation state only.
    services.AddFixedStepSimulation<WorldSimulation>(bindings: seatBindings);
}
// Self-update: channel/cacheRoot/checkInterval/keepVersions come from the world document's own update section
// (WorldUpdateDefaults) — a deployment-facet field carrying no simulation-state weight, matching WorldHostDefaults'
// own posture. The trust anchor is a build-pinned composition-root constant, never a document field: a synced
// puck.world.def.v1 a player's own storage container could rewrite is not a trust anchor. It stays the refusing
// ReleaseTrustAnchor.Placeholder until a real release-signing chain is minted for this build.
var updateSection = worldSource.Definition.Update;
var updateCacheRoot = (updateSection?.CacheRoot ?? Path.Combine(
    path1: Puck.World.Server.WorldStateRoot.Resolve(),
    path2: "updates"
));
var updateCheckInterval = ((updateSection?.CheckIntervalSeconds is { } updateCheckSeconds)
    ? ((updateCheckSeconds > 0)
        ? TimeSpan.FromSeconds(value: updateCheckSeconds)
        : (TimeSpan?)null)
    : null
);
var releaseTrustAnchor = Puck.Launcher.Release.ReleaseTrustAnchor.Placeholder;
Puck.Launcher.Release.IReleaseSource releaseSource = new Puck.Launcher.Release.DirectoryReleaseSource(root: Path.Combine(
    path1: updateCacheRoot,
    path2: "release-source"
));
if (parseResult.GetValue(option: updateConfigFileOption) is { } updateConfigFile) {
    if (!Puck.Launcher.Release.SelfUpdateConfigFile.TryLoad(
        config: out var updateConfig,
        error: out var updateError,
        path: updateConfigFile
    )) {
        Console.Error.WriteLine(value: $"--update-config-file could not be read: {updateError}");

        return 1;
    }

    releaseTrustAnchor = updateConfig!.ToTrustAnchor();
    releaseSource = updateConfig.ToReleaseSource();
    updateCacheRoot = (updateConfig.CacheRoot ?? updateCacheRoot);
}
// The stub reads its own selected version from `<cacheRoot>/current` (FileUpdateApplier's own contract: cacheRoot
// IS the stub's install root). Reporting that same value back as InstalledVersion is what makes version-monotonicity
// verification mean something beyond "this build's fixed assembly version" once staging is real; a cacheRoot with no
// stub-managed install (no `current` file yet) falls back to the compiled assembly version.
var updateCurrentPointerPath = Path.Combine(
    path1: updateCacheRoot,
    path2: "current"
);
var updateInstalledVersion = (File.Exists(path: updateCurrentPointerPath)
    ? File.ReadAllText(path: updateCurrentPointerPath).Trim()
    : string.Empty
);
if (updateInstalledVersion.Length == 0) {
    updateInstalledVersion = (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");
}
services.AddSelfUpdate(
    options: new Puck.Launcher.Release.UpdateOptions(
        App: "puck.world",
        CacheRoot: updateCacheRoot,
        Channel: (updateSection?.Channel ?? "stable"),
        CheckInterval: updateCheckInterval,
        InstalledVersion: updateInstalledVersion,
        KeepVersions: (updateSection?.KeepVersions ?? 2),
        TrustAnchor: releaseTrustAnchor
    ),
    releaseSource: releaseSource
);
var host = builder.Build();
// The every-shape post-build wiring step: affordance install, the boot document's genuine binding-vocabulary
// re-validation (WorldAffordances.Installed is only true from here on — see WorldPostBuildWiring.Install's remarks
// for why the first validation at WorldDefinitionLoader.TryResolve above could not have caught this), the
// lever-sink attachment, and the server's echo/cue taps, so wire.errors stays honest headless. Runs before the
// host starts, so it observes the fully built container in either boot shape. A refused re-validation prints its
// reason on stderr and fails the boot here, identically in both shapes, rather than starting a host whose own boot
// document the real vocabulary would have refused.
if (!WorldPostBuildWiring.Install(services: host.Services)) {
    return 1;
}
if (connectTarget is { } remoteEndpoint) {
    var instances = host.Services.GetRequiredService<WorldInstanceHost>();

    _ = instances.EnqueueTransfer(
        sourceInstance: WorldInstanceHost.BootInstanceName,
        scope: WorldInstanceHost.TransferScope.Body,
        sourceSlot: 0,
        destination: WorldInstanceHost.TransferDestination.Remote(
            name: "remote-boot",
            documentPath: worldSource.SourcePath,
            authority: remoteEndpoint
        ),
        actingPrincipal: WorldPrincipal.Console
    );
}
await host.RunAsync();
return 0;
