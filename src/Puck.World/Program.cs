using Puck.Commands;
using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Launcher;
using Puck.World;
using Puck.World.Machines;

// Narration, boot lines and every other write through Console.Out and Console.Error carry engine text with non-ASCII
// characters (the em dash before a transfer's arrival pose, the degree sign in body.where). Without this the host
// encodes those streams through the machine's console code page, which turns an em dash into '-' and a degree sign into
// a byte that is not UTF-8, while the command echoes beside them are UTF-8, so the run's output differs by machine.
// The setter suppresses the byte-order mark.
Console.OutputEncoding = System.Text.Encoding.UTF8;
// Every line written through the console from here on is also held in a bounded ring that host.log reads, so an
// Operator attachment reads what only this process's launcher otherwise sees.
var hostLog = HostLog.CaptureConsole();
// The run's state root, resolved from --state-dir once the command line parses; the method recorder reads it at exit.
Puck.World.Server.WorldStateRoot? stateRoot = null;
WorldMethodRecorder.StartIfBuiltIn(stateRoot: () => stateRoot);
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
    Description = "The world to boot (required): an asset package folder (or its manifest.json), whose puck.asset-package.v1 manifest names the entry document beside it; a .puck source compiled in memory; or a .world.json document (puck.world.definition.v1). A .puck source that declares several worlds boots its entry world (see --entry). A missing or invalid world FAILS the boot with a named reason and exit 1, and so does a boot without --world: the engine has no default world.",
};
var entryOption = new Option<string?>(name: "--entry") {
    DefaultValueFactory = static _ => null,
    Description = "The declared world a --world composition source boots, in place of the world it declares `entry world`. The whole composition is staged either way, so its borders stay real. A name the source does not declare, or a --world that is not a composition, refuses the boot by name.",
};
var recordingOption = new Option<string?>(name: "--recording") {
    DefaultValueFactory = static _ => null,
    Description = "The recording document (puck.recording.configuration.v1) the capture verbs use; a missing or invalid file falls back loudly to the baked default. Default: Assets/recordings/default.recording.json beside the executable.",
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
    Description = "Override the on-disk state root (profile catalog, replays). Compiled worlds and compiles are per-user caches every boot shares. Absent uses the world subdirectory of the per-user Puck directory (%LOCALAPPDATA%/Puck/world on Windows). A developer/deployment override: parallel verification runs and multiple hosts on one machine each need their own root.",
};
var cacheDirOption = new Option<string?>(name: "--cache-dir") {
    DefaultValueFactory = static _ => null,
    Description = "Override the root of the device caches (held compiles, compiled worlds, baked meshes), each a subdirectory of it. Absent uses the per-user Puck directory (%LOCALAPPDATA%/Puck on Windows). A developer/deployment override, the --state-dir pattern: an empty root boots as a machine's first boot does, baking every mesh.",
};
var captureDirOption = new Option<string?>(name: "--capture-dir") {
    DefaultValueFactory = static _ => null,
    Description = "Override the world document's captures.directory. A developer/deployment override, the --state-dir pattern: a cross-backend parity run needs the two legs' captures kept apart.",
};
var scheduleDirOption = new Option<string?>(name: "--schedule-dir") {
    DefaultValueFactory = static _ => null,
    Description = "Arms the world document's schedule section and names where the run writes its state export and submission manifest. Absent, a document carrying a schedule submits no row and writes no export, and the boot says so once: a published world travels, and a section that submits commands runs only where the operator asked for it.",
};
// A developer and qualification diagnostic, never world-authored: the validation of whichever backend the run hosts
// on. A bare flag is the basic level, which `puck canary --debug-layers` and `puck qualify` (its profile's debugLayers)
// pass to the Worlds they start; `full` adds the GPU's own checks.
var debugLayersOption = new Option<GpuValidation>(name: "--debug-layers") {
    Arity = ArgumentArity.ZeroOrOne,
    CustomParser = static result => WorldDiagnosticFlags.ParseValidation(
        error: result.AddError,
        token: ((result.Tokens.Count == 0) ? null : result.Tokens[0].Value)
    ),
    DefaultValueFactory = static _ => GpuValidation.Off,
    Description = "Create the GPU device with its backend's validation. A bare flag (or 'basic') is the API layer: [vulkan-debug] lines with synchronization validation on Vulkan, [d3d12-debug] lines and a teardown live-object report on Direct3D 12. 'full' adds GPU-based validation with state tracking and synchronized queue validation on Direct3D 12, GPU-assisted validation and best-practices checks (NVIDIA's included) on Vulkan, and makes pipeline creation and every frame much slower. Every level names the device's objects. On some Direct3D 12 configurations the layer makes device creation fail.",
};
// A device choice, never world-authored: WARP runs the Direct3D 12 host on the CPU, so a machine with no usable GPU
// (a CI agent baking the mesh store) or one whose GPU another job holds boots the same frames, slowly.
var softwareGpuOption = new Option<bool>(name: "--software-gpu") {
    Description = "Create the Direct3D 12 device on WARP, the software rasterizer, instead of a hardware adapter. Needs --backend directx.",
};
// A developer diagnostic, never world-authored: a capture tool hooks the graphics API from inside the process, so it
// loads before anything can create a device, and gpu.capture then captures the frames the operator asks for.
var captureToolOption = new Option<string?>(name: "--capture-tool") {
    DefaultValueFactory = static _ => null,
    Description = "Load a GPU capture tool into the process before the device exists, so gpu.capture <path> [<frames>] captures the next frames: pix (PIX on Windows' WinPixGpuCapturer.dll, Direct3D 12 only) or renderdoc (RenderDoc's in-application API, either backend). Also names the device's objects. Refused when the tool is not installed.",
};
captureToolOption.AcceptOnlyFromAmong(values: [.. GpuCaptureTools.Names]);
var unpacedOption = new Option<bool>(name: "--unpaced") {
    DefaultValueFactory = static _ => false,
    Description = "Advance a headless world on its fixed simulation tick grid without wall-clock pacing. Intended for an armed authored schedule or another offline run; refused for presented hosts.",
};
// A DEVELOPER REFLECTION of the document's host.presentation field, not a separate product (the unification
// contract): absent lets the document decide; a bare --headless (or --headless true) forces host.presentation=none
// for this run only (no window, no GPU device, no swapchain, no audio device); --headless false forces windowed.
var headlessOption = new Option<bool?>(name: "--headless") {
    Arity = ArgumentArity.ZeroOrOne,
    DefaultValueFactory = static _ => null,
    Description = "Override the world's boot shape: a bare flag (or 'true') boots headless (no window/GPU/swapchain/audio device — the authoritative server, console, and tape only); 'false' forces windowed. Absent uses the world document's host.presentation.",
};
// A DEVELOPER REFLECTION of the document's host.listen field (the QUIC socket door), not a separate product: absent
// lets the document decide (null = loopback-only, never opens a socket); an explicit value binds a QUIC listener for
// this run only.
var listenOption = new Option<string?>(name: "--listen") {
    DefaultValueFactory = static _ => null,
    Description = "Override the QUIC listen endpoint (an \"ip:port\" pair, e.g. 127.0.0.1:7777). Absent uses the world document's host.listen (null = loopback-only, never opens a socket).",
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
var authenticationConfigFileOption = new Option<string?>(name: "--authentication-config-file") {
    Description = "Deployment-owned connection authentication extension (type and settings). Requires --connect; credentials are acquired by the installed provider and never read from the world document.",
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
// Deployment authority, like the federation key and update trust configuration; never world-authored input.
var extensionsConfigFileOption = new Option<string?>(name: "--extensions-config-file") {
    Description = "Host-approved service extension composition (puck.world.extensions.v1). Absent disables external services. The file selects installed provider types, bindings, grants, and state connections; it never loads executable code.",
};
// An operator's per-run opt-in, never world-authored: a document cannot open a control endpoint on whoever boots it.
var controlOption = new Option<bool>(name: "--control") {
    DefaultValueFactory = static _ => false,
    Description = "Start the local trusted Operator control endpoint at boot, as a first console line `world.control start` would, and print its attachment file on standard error. `puck mcp --profile operator` then attaches to this World. Absent, the endpoint stays closed until a console line starts it.",
};
// An OPERATOR's per-run choice, never world-authored (the --control pattern): a document cannot decide whose hardware a
// run may read. Agents and automation boot with `none`, so the owner's pads, keyboard and mouse never drive their Worlds.
var devicesOption = new Option<string>(name: "--devices") {
    DefaultValueFactory = static _ => "all",
    Description = "Which physical input devices this run opens: all (the default: every gamepad, and the keyboard and mouse the window receives) or none (no pad is opened or polled, and no OS key, text or pointer event reaches a seat; stdin, the console, player.signal, body.*, schedule rows and MCP drive the World exactly as before). Every World an agent boots passes none.",
};
devicesOption.AcceptOnlyFromAmong(values: ["all", "none"]);
var launchCommand = new RootCommand(description: "Puck World") {
    backendOption,
    connectOption,
    controlOption,
    devicesOption,
    federationKeyFileOption,
    authenticationConfigFileOption,
    captureDirOption,
    captureToolOption,
    debugLayersOption,
    softwareGpuOption,
    scheduleDirOption,
    unpacedOption,
    exitAfterSecondsOption,
    headlessOption,
    stateDirOption,
    cacheDirOption,
    heightOption,
    listenOption,
    presentModeOption,
    recordingOption,
    storageDiscoveryUriOption,
    storageUriOption,
    updateConfigFileOption,
    extensionsConfigFileOption,
    userIdOption,
    widthOption,
    worldOption,
    entryOption,
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
// Help, version, and parser directives are complete invocations. Normal boot has no parser action;
// execute a selected action before touching configuration, persistence, networking, or presentation.
if (parseResult.Action is not null) {
    return await parseResult.InvokeAsync();
}
// The engine names no world of its own: a boot runs the world it is handed, so one without --world ends here.
if (parseResult.GetValue(option: worldOption) is not { Length: > 0 } worldPath) {
    Console.Error.WriteLine(value: "[world] definition refused: no --world. Puck.World boots the world it is handed: pass --world with an asset package folder (a folder whose manifest.json names its entry document), a .puck source, or a .world.json document.");

    return 1;
}
var connectTarget = parseResult.GetValue(option: connectOption);
Puck.World.Server.WorldExtensionConfiguration? extensionsConfiguration = null;
if (parseResult.GetValue(option: extensionsConfigFileOption) is { } extensionsPath) {
    try {
        if (connectTarget is not null) { throw new InvalidOperationException(message: "Service extensions require a local authority, not a remote client boot."); }
        extensionsConfiguration = Puck.World.Server.WorldExtensionConfiguration.Load(path: extensionsPath);
    } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)) {
        Console.Error.WriteLine(value: $"[world.extensions: configuration refused: {exception.Message}]");
        return 1;
    }
}
// The per-user default is resolved here and nowhere else: every consumer below takes this root from the service
// collection, so a host or fixture that composes World services carries its own.
stateRoot = new Puck.World.Server.WorldStateRoot(path: (parseResult.GetValue(option: stateDirOption) ?? PuckUserDirectory.Resolve(name: "world")));
// The device caches are per user rather than under the state root, since each holds a pure function of its inputs and
// the build, never of a run's state. Their per-user defaults are named here and nowhere else, like the state root's.
var cacheRoot = parseResult.GetValue(option: cacheDirOption);
var caches = new WorldCacheRoots(
    compilations: ((cacheRoot is null) ? PuckUserDirectory.Resolve(name: "compilations") : Path.Join(path1: cacheRoot, path2: "compilations")),
    compiledWorlds: ((cacheRoot is null) ? PuckUserDirectory.Resolve(name: "compiled-worlds") : Path.Join(path1: cacheRoot, path2: "compiled-worlds")),
    meshes: ((cacheRoot is null) ? PuckUserDirectory.Resolve(name: "meshes") : Path.Join(path1: cacheRoot, path2: "meshes"))
);
// A world source compiles once across boots.
Puck.World.Transpiler.Composition.WorldCompileCache.Shared.Persist(directory: caches.Compilations);
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
if (parseResult.GetValue(option: softwareGpuOption) && (backendOverride != WorldBackendPreference.DirectX)) {
    Console.Error.WriteLine(value: "--software-gpu creates a WARP device, which only the Direct3D 12 host has; pass --backend directx with it.");

    return 1;
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
// Compose the host's extensions before loading the world: composition and semantic validation must use the same
// immutable machine catalog that runtime machine construction receives, including optional installed extensions.
PuckExtensionSet extensions;
WorldMachineCatalog machineCatalog;
try {
    extensions = WorldBootComposition.ComposeExtensions(directories: PuckExtensionDiscovery.DefaultDirectories());
    machineCatalog = WorldMachineCatalog.From(extensions: extensions);
} catch (Exception error) when ((error is PuckExtensionException or ArgumentException)) {
    Console.Error.WriteLine(value: $"[world.extensions: refused: {error.Message}]");

    return 1;
}
var machineCatalogFingerprint = WorldBootComposition.MachineCatalogFingerprint(machineCatalog: machineCatalog);
// The federation identity door — deny-by-default, mirroring --listen's own absent-means-closed posture: an
// unconfigured authenticator refuses the federation dialect outright at WorldPeerHost's IsConfigured gate, leaving
// ordinary admitted-peer listening (the interactive attestation door) untouched. A configured one signs
// SignsDirectly claims under this run's own pinned key, naming this document's host.authority as the subject a
// peer's own admission entries pin against; verification reads the CURRENT document's admission rows fresh on
// every attempt, so a live world.reload/edit is honored the same way the interactive door already is.
Puck.Networking.IAuthenticator authenticator = new Puck.World.Protocol.WorldAttestedAuthenticator();
Puck.World.Server.WorldServer? admissionServer = null;
string? connectionSubject = null;
// Read before the world so an override it carries reaches the document before its one admission, never after.
Func<WorldDefinition, WorldDefinition>? bootOverrides = null;
if (parseResult.GetValue(option: authenticationConfigFileOption) is { } authenticationPath) {
    try {
        if (
            (connectTarget is null) ||
            (parseResult.GetValue(option: federationKeyFileOption) is not null)
        ) {
            throw new ArgumentException(message: "Connection authentication requires --connect and cannot be combined with --federation-key-file.");
        }
        // The desktop host runs on system time; the provider's token and metadata deadlines read it.
        var connection = WorldConnectionAuthentication.Load(
            clock: TimeProvider.System,
            extensions: extensions,
            path: authenticationPath
        );

        authenticator = connection.Authenticator;
        connectionSubject = connection.Subject;
        // A user's local authority is an instance namespace, not a published listening endpoint.
        bootOverrides = static definition => (definition with { HostRaw = definition.Host with { Authority = null, Listen = null } });
    } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or Azure.Identity.AuthenticationFailedException)) {
        Console.Error.WriteLine(value: $"[world.authentication: configuration refused: {error.Message}]");
        return 1;
    }
}
// The world definition (see WorldDefinition) — the --world package, source or document, loaded / schema-checked /
// validated (see WorldDefinitionLoader). LOADED BEFORE the window/launcher/presentation registrations because those
// read their values from the resolved host section. Read by DI from the roster, population, frame source, render
// settings, and the world.quality verb; the resolved source is registered so world.save knows its default target. Any
// path that will not load ends the boot here — a typo must never quietly run a different world.
if (!PuckWorldLoader.TryResolveWorld(
    caches: caches,
    entry: parseResult.GetValue(option: entryOption),
    worldPath: worldPath,
    stateRoot: stateRoot,
    failure: out var worldFailure,
    source: out var worldSource,
    catalogFingerprint: machineCatalogFingerprint,
    catalog: machineCatalog,
    overrides: bootOverrides
)) {
    Console.Error.WriteLine(value: worldFailure);

    return 1;
}
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
        // The one key-import path in the tree: refuses trailing bytes and any curve other than the one the signing
        // algorithm names, so a wrong key file fails here by name rather than at the first signed claim.
        var key = Puck.Attestation.AttestationKeys.ImportPkcs8PrivateKey(
            algorithm: Puck.Attestation.AttestationAlgorithms.EcdsaP256Sha256,
            pkcs8: pkcs8
        );

        authenticator = new Puck.World.Protocol.WorldAttestedAuthenticator(
            oracle: new Puck.World.Protocol.LocalKeySigningOracle(
                key: key,
                subject: federationSubject,
                validity: Puck.World.Protocol.WorldAttestedAuthenticator.MaximumClaimAge
            ),
            trustEntries: () => (admissionServer?.Definition ?? worldSource.Definition).Admission
        );
    } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)) {
        Console.Error.WriteLine(value: $"--federation-key-file could not be read: {exception.Message}");

        return 1;
    }
}
// Resolve the effective host settings: the world doc's host defaults (absence coalesced to WorldHostDefaults.Absent —
// no presentation; a world authors its windowed boot or inherits its basis's) overlaid by the nullable CLI flags.
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
var unpaced = parseResult.GetValue(option: unpacedOption);
if (unpaced && !hostSettings.Headless) {
    Console.Error.WriteLine(value: "--unpaced requires a headless host (--headless true or host.presentation none).");

    return 1;
}
// The document validates host.listen's shape only, and --listen not at all, so the endpoint is parsed here: an
// endpoint the peer host cannot parse refuses the boot as configuration, before any host exists to bind it.
if (
    (hostSettings.Listen is { } listenEndpoint) &&
    !System.Net.IPEndPoint.TryParse(
        result: out _,
        s: listenEndpoint
    )
) {
    Console.Error.WriteLine(value: $"[world.host: refused: listen endpoint '{listenEndpoint}' is not a parseable \"ip:port\" endpoint (a hostname is not accepted)]");

    return 1;
}
if (hostSettings.BackendUnsatisfiable) {
    Console.Error.WriteLine(value: LauncherHostRun.FormatUnsupported(
        label: "world",
        unavailable: new Puck.Abstractions.Gpu.GpuDeviceUnavailableException(
            backend: "directx",
            reason: "Direct3D 12 requires Windows 10 or newer; use --backend vulkan on this platform"
        )
    ));

    return LauncherHostRun.UnsupportedExitCode;
}
// A Windows build of one project ships Direct3D 12 bytecode alone (build/Shaders.targets), so a Vulkan host whose build
// carries no SPIR-V refuses before it composes anything that reads a stage.
if (
    !hostSettings.Headless &&
    !hostSettings.HostsOnDirectX &&
    !File.Exists(path: Path.Combine(path1: Puck.SdfVm.SdfKernelSet.DefaultDirectory, path2: $"{Puck.SdfVm.SdfMeshRasterPass.ShaderStem}.frag.spv"))
) {
    Console.Error.WriteLine(value: "[world.host: refused: this build carries no Vulkan SPIR-V; a Windows build of one project ships Direct3D 12 bytecode alone. Build with -p:PuckShaderSpirvEnabled=true (or build the solution) to run on Vulkan.]");

    return 1;
}
if (hostSettings.BackendDowngraded) {
    Console.Error.WriteLine(value: $"[world.host] backend \"{WorldHostTokens.BackendToken(backend: hostSettings.RequestedBackend)}\" is unavailable on this OS; hosting on Vulkan instead.");
}
// The capture tool loads here, after the backend is settled and before anything below can create a device.
IGpuCaptureTool? captureTool = null;
if (parseResult.GetValue(option: captureToolOption) is { } captureToolName) {
    var captureFailure = "a headless boot creates no device to capture";

    if (hostSettings.Headless || !GpuCaptureTools.TryLoad(
        directX: hostSettings.HostsOnDirectX,
        failure: out captureFailure,
        name: captureToolName,
        tool: out captureTool
    )) {
        Console.Error.WriteLine(value: $"[world.host: refused: --capture-tool {captureToolName}: {captureFailure}]");

        return 1;
    }

    Console.Error.WriteLine(value: $"{GpuFrameCapture.LinePrefix} {captureTool!.Name} loaded; gpu.capture <path> [<frames>] captures the next frames");
}
var physicalDevices = (parseResult.GetValue(option: devicesOption) != "none");
if (!physicalDevices) {
    Console.Error.WriteLine(value: ((hostSettings.Presentation == WorldHostPresentation.Windowed)
        ? "[world.host] physical input devices are off (--devices none): no gamepad is opened, and no key, text or pointer event from the OS reaches a seat; scripted input still drives the World."
        : "[world.host] physical input devices are off (--devices none); this boot shape opens none either way."));
}
var builder = Host.CreateApplicationBuilder(args: args);
// Standard output carries the console's read-back answers, which a script parses; every log line, whatever its level
// and whichever thread writes it, goes to standard error beside the narration.
builder.Logging.AddConsole(configure: static options => options.LogToStandardErrorThreshold = LogLevel.Trace);
var services = builder.Services;
services.AddSingleton(implementationInstance: hostLog);
if (hostSettings.Presentation == WorldHostPresentation.Windowed) {
    // The recording graph (puck.recording.configuration.v1) — native capture for streaming/upload workflows, defined as data.
    // PRESENTATION-ONLY (AddWorldPresentation registers the encoder ladder/capture controller/verb module), but the
    // document resolution itself can fail-and-exit, so it stays here beside World's other --world/--recording
    // loaders. Skipped headless and offscreen: neither composes the capture verbs, so neither needs a valid
    // recordings asset.
    if (!RecordingDocumentLoader.TryResolve(
        explicitPath: parseResult.GetValue(option: recordingOption),
        source: out var recordingSource,
        failure: out var recordingFailure
    )) {
        Console.Error.WriteLine(value: recordingFailure);

        return 1;
    }

    services.AddSingleton(implementationInstance: recordingSource);
}
// Everything the boot resolved above becomes the one service collection its shape needs; the composition laws build
// the same collection through the same method.
services.AddWorldBoot(inputs: new WorldBootInputs(
    Authenticator: authenticator,
    Caches: caches,
    Extensions: extensions,
    HostSettings: hostSettings,
    MachineCatalog: machineCatalog,
    Source: worldSource,
    StateRoot: stateRoot
) {
    CaptureDirectory = parseResult.GetValue(option: captureDirOption),
    CaptureTool = captureTool,
    ConnectionSubject = connectionSubject,
    Control = parseResult.GetValue(option: controlOption),
    PhysicalDevices = physicalDevices,
    // The parser hands a bare flag its default rather than its custom parser, so a present flag with no value is read
    // here as the basic level.
    Validation = ((parseResult.GetResult(option: debugLayersOption) is { Implicit: false, Tokens.Count: 0 })
        ? GpuValidation.Basic
        : parseResult.GetValue(option: debugLayersOption)),
    Software = parseResult.GetValue(option: softwareGpuOption),
    ExtensionsConfiguration = extensionsConfiguration,
    FederationKeyFile = parseResult.GetValue(option: federationKeyFileOption),
    ScheduleDirectory = parseResult.GetValue(option: scheduleDirOption),
    StorageDiscoveryEndpoint = parseResult.GetValue(option: storageDiscoveryUriOption),
    StorageEndpoint = parseResult.GetValue(option: storageUriOption),
    StorageUserId = parseResult.GetValue(option: userIdOption),
    Unpaced = unpaced,
});
// Self-update: channel/cacheRoot/checkInterval/keepVersions come from the world document's own update section
// (WorldUpdateDefaults) — a deployment-facet field carrying no simulation-state weight, matching WorldHostDefaults'
// own posture. The trust anchor is a build-pinned composition-root constant, never a document field: a synced
// puck.world.definition.v1 a player's own storage container could rewrite is not a trust anchor. It stays the refusing
// ReleaseTrustAnchor.Placeholder until a real release-signing chain is minted for this build.
var updateSection = worldSource.Definition.Update;
var updateCacheRoot = Path.Combine(path1: stateRoot.PathOf(name: "updates"), path2: (updateSection?.CacheRoot ?? string.Empty));
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
// The signing oracle is configured before DI exists, but every verification after construction must consult
// the installed server document, including reloads, rather than retaining the source loaded at boot.
admissionServer = host.Services.GetRequiredService<Puck.World.Server.WorldServer>();
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
        actingPrincipal: Principal.Console
    );
}
// A rendering World compiles its own code in the background, so a first crossing or a first addon write runs compiled
// code rather than stalling a frame on the JIT; a headless World has no frame to stall.
if (hostSettings.Presentation != WorldHostPresentation.None) {
    _ = WorldCodeWarmer.Start(entry: typeof(WorldCodeWarmer).Assembly, error: Console.Error);
}
return await LauncherHostRun.RunAsync(
    error: Console.Error,
    host: host,
    label: "world"
);
