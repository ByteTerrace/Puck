using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Capture;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Pacing;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Hosting;
using Puck.Input;
using Puck.Launcher;
using Puck.Overlays;
using Puck.Platform;
using Puck.Platform.Audio;
using Puck.Platform.Windows;
using Puck.Platform.Windows.Gamepad;
using Puck.Platform.Windows.Hid;
using Puck.SdfVm;
using Puck.World.Audio;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The boot-shape composition split: <see cref="AddWorldAuthoritativeCore"/> is the WHOLE
/// server-safe world — everything that works with no window, no GPU device, no swapchain, no audio device.
/// <see cref="AddWorldPresentation"/> layers the GPU host, render root, overlays, audio, screens/machines, gamepads,
/// and editor on top. <c>Program.cs</c> calls the core method ALWAYS and the presentation method only when
/// <c>WorldHostSettings.Headless</c> is <see langword="false"/> — the boot-shape branch, decided before either method
/// runs. Both take only <c>this IServiceCollection services</c> (plus the one value <see cref="AddWorldPresentation"/>
/// needs eagerly, before any factory could resolve it): every other dependency is read from the already-registered
/// <see cref="WorldDefinitionSource"/>/<see cref="WorldHostSettings"/>/<see cref="WorldSeatBindings"/> singletons
/// <c>Program.cs</c> registers before calling either method.
/// </summary>
internal static class WorldBootComposition {
    /// <summary>
    /// The authoritative core: profiles, roster, server, grants, population, addon runtime, replay tape, the
    /// submission/output hub (via <see cref="WorldServer"/>), the console's tick barrier, and every server-safe
    /// console module. Registered in EVERY boot shape.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddWorldAuthoritativeCore(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        // The owned-world catalog (files under the state root; the storage.* verbs sync it to the per-user cloud
        // container): loaded once at startup, malformed documents refused by name — the roster and the settings verbs
        // read it live.
        services.AddWorldOwnedWorlds();

        // The participant roster (up to four players, one avatar + viewport each; player 1 always joined, seated on
        // the boot profile) and its console/keyboard verb surface, plus the real-time profile/settings verbs
        // (aggregated into the CommandRegistry with every other module).
        services.AddSingleton<PlayerRoster>();
        // The per-seat PERCEPTION ANCHOR — the one body index all seat-relative presentation (camera eye, audio
        // listener, seat.<n>.position.* HUD bindings, crowd soft-shadow centers) derives from; today always the
        // seat's bound body (pure indirection — a future route-target swap moves the anchor here, in one place).
        // CORE: the HUD binding resolver and player.where's anchor echo consume it in every boot shape;
        // presentation's frame source and scene emitter reach the same singleton.
        services.AddSingleton<WorldPerceptionAnchor>();
        services.AddSingleton<IInputSlotResolver>(implementationFactory: static sp => sp.GetRequiredService<PlayerRoster>());
        // The roster answers BOTH input seams: which slot a device belongs to, and who is acting through that slot.
        // The mixer stamps every captured entry from the second one, so a claimed slot's input carries the claimant's
        // identity.
        services.AddSingleton<ICommandPrincipalResolver>(implementationFactory: static sp => sp.GetRequiredService<PlayerRoster>());
        services.AddSingleton<ICommandModule, PlayerCommandModule>();
        services.AddSingleton<ICommandModule, IdentityCommandModule>();
        services.AddSingleton<ICommandModule, ChatCommandModule>();

        // The rebind surface — player.bind (live session remap + chord rows) / player.bindings (echo the composed
        // active mapping) / player.signal (synthesized raw input over the pipe) / identity.bindings.save (fold
        // session rebinds into the seat's owned identity world). A SEPARATE module to keep each class
        // under its analyzer ceilings. The router reaches the module LAZILY: the router's factory consumes the
        // CommandRegistry, which aggregates every ICommandModule — a direct dependency would cycle the container.
        services.AddSingleton<Func<InputRouter>>(implementationFactory: static sp => (() => sp.GetRequiredService<InputRouter>()));
        // The registry reaches this module the same lazy way, for the same cycle: world.affordances reads the built
        // registry at dispatch to emit the manifest the binding vocabulary checks validate against.
        services.AddSingleton<Func<CommandRegistry>>(implementationFactory: static sp => (() => sp.GetRequiredService<CommandRegistry>()));
        services.AddSingleton<ICommandModule, WorldBindingCommandModule>();

        // The stamp pool (dynamic-creation/placement-preview accounting) — plain data, no render dependency. Shared
        // by the (core) audio director's panning source and the (presentation) frame source/creation editor.
        services.AddSingleton<WorldStampPool>();

        // The audio director: derives the emitter table from the delivered definition and resolves poses (the actual
        // WASAPI device pump — WorldAudioRenderService — is presentation-only). Registered here because the core
        // mutation module's world.save/world.status round-trip live render/audio levers into the document, and the
        // shared post-build echo/cue wiring submits cues unconditionally (a cue submitted with no device pump
        // attached is simply never drained — harmless headless).
        services.AddSingleton(implementationFactory: static sp => new WorldAudioDirector(
            client: sp.GetRequiredService<WorldClient>(),
            animator: sp.GetRequiredService<WorldStampPool>()
        ));

        // The server's entity table — the four local seats plus up to 124 network stand-ins the world.population verb
        // activates — the one body system the snapshot reports (up to 128 avatars: the scale target).
        services.AddSingleton<WorldPopulation>();

        // The render-capacity oracle the server consults before applying a scene/screen mutation — configured by the
        // frame source once it has probed the boot envelope (presentation-only), so an over-envelope edit is rejected
        // loudly at apply time. Unconfigured (headless, or before presentation's frame source has run) reads as
        // "fits" (WorldRenderEnvelope's own documented default) — the boot definition is what a probe would measure,
        // so it always fits until something narrower configures it.
        services.AddSingleton<WorldRenderEnvelope>();

        // The authoritative world server and the in-process loopback fronting it: the client submits intents,
        // commands, session requests, and buffered live edits (mutations, definition swaps, journal undo) over
        // IServerLink; the server applies them at its step boundary, answers queries, and pushes each tick's snapshot
        // (and, after an applied edit, the new definition) to every bound client sink.
        services.AddSingleton<WorldServer>();
        // IWorldServerHost is the seam Puck.World.Data's LoopbackTransport holds instead of the concrete WorldServer
        // type (Puck.World.Data cannot reference Puck.World.Server) — WorldServer implements it directly, so this is
        // a type mapping onto the same singleton, never a second instance.
        services.AddSingleton<IWorldServerHost>(implementationFactory: static sp => sp.GetRequiredService<WorldServer>());
        services.AddSingleton<LoopbackTransport>();
        services.AddSingleton<IServerLink>(implementationFactory: static sp => sp.GetRequiredService<LoopbackTransport>());

        // The TCP socket door — registered in EVERY boot shape (headless or windowed, play-and-host is first-class)
        // but only ever bound when host.listen/--listen names an endpoint (WorldPostBuildWiring.Install starts it).
        // Disposed by the container at shutdown (IDisposable), which stops the listener and drops every connection.
        services.AddSingleton<WorldTcpHost>();

        // The addon principals: mounts the world document's enabled Simulation-lane rows through a Puck.Scripting
        // AddonHost (consumed, never modified) and attaches itself to the server, which then pumps it at three pinned
        // points inside its own Step — guests run at the top, their contributions apply after the intent drain, and
        // their reads resolve after the step. Constructed here rather than by the server so mounting happens AFTER
        // the server's constructor settled the document's grants (the mount-time disclosure must report a settled
        // table). DI disposes it (the owned Wasmtime engine/stores).
        services.AddSingleton(implementationFactory: static sp => WorldAddonRuntime.Create(
            definition: sp.GetRequiredService<WorldDefinition>(),
            server: sp.GetRequiredService<WorldServer>()
        ));

        // The shared live composition-override store — written by DeliverComposition (an accepted
        // view.override layout/camera), read by the (presentation-only) frame source's view composer. Plain
        // state either way.
        services.AddSingleton<WorldCompositionState>();

        // The client half: the snapshot-fed entity view + per-tick seat-intent submitter, bound to the loopback at
        // construction (the bind delivers a primer snapshot). Headless-safe BY DESIGN — see WorldClient's own doc
        // comment: a client composed without the presentation services simply drops accepted levers rather than
        // failing to construct. Needed here (not just presentation) because WorldScreenBinder (an
        // ISdfAnchorSource consumer) and WorldAudioDirector both take it, and both are core.
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

        // The accepted-session-lever applier: the ONLY writer of the live presentation knobs the lever verbs move
        // (world.volume / world.shadows / world.target and their siblings). Every dependency (render settings,
        // present pacing, audio director) is core data, so the sink itself constructs core; its ATTACHMENT to the
        // client (WorldClient.AttachSessionLevers) happens in the shared post-build wiring step (WorldPostBuildWiring)
        // instead of the old render-root factory, so a lever still reaches the client headless (dropped harmlessly,
        // per WorldClient's own doc comment) instead of never being wired at all.
        services.AddSingleton(implementationFactory: static sp => new WorldSessionLeverSink(
            settings: sp.GetRequiredService<WorldRenderSettings>(),
            pacing: sp.GetRequiredService<PresentPacingControl>(),
            audio: sp.GetRequiredService<WorldAudioDirector>()
        ));

        // The frame-rate witness (a plain 2-second rolling window over presentation-fed deltas — no device
        // dependency to construct) and the live render settings (console-mutated in real time). Both core because
        // world.hud's live binding resolver (world.fps/world.tick) and world.shadow-mask/.ao-quality/.shadow-march's
        // auto-tier thresholds read them regardless of boot shape; presentation is the only thing that ever FEEDS
        // FrameRateMonitor real samples, so world.fps reads "no frames sampled yet" headless (there is no world.fps
        // verb headless anyway — it lives on the presentation-only WorldCommandModule).
        services.AddSingleton<FrameRateMonitor>();
        // The live render settings boot from the definition's render-lever defaults (then the console verbs move
        // them live).
        services.AddSingleton(implementationFactory: static sp => new WorldRenderSettings(defaults: sp.GetRequiredService<WorldDefinition>().Render));

        // The live-content platform seams the screen binder pulls CPU pixels through: the webcam (Media Foundation
        // on Windows, the CPU tier) and compositor-owned desktop-window capture. Registered here (not presentation)
        // because WorldScreenBinder's constructor needs them regardless of boot shape — see below.
        services.AddCameraCapture();
        services.AddSingleton<INativeImageCaptureService>(implementationFactory: static _ =>
            (OperatingSystem.IsWindows()
                ? new Win32NativeImageCaptureService()
                : new NullNativeImageCaptureService()));

        // The screen-machine engines — read from WorldScreenMachineEngines.All, the ONLY place a concrete engine type
        // is named in World (WorldDataHookInstaller reads the SAME list to install the load-time registered-key
        // check, so the two can never drift). A declared or inserted machine screen resolves against this
        // DI-registered set by engine id. Core because Server.WorldMachineHost (below) needs them at construction
        // regardless of boot shape; nothing here opens a GPU device.
        foreach (var engine in WorldScreenMachineEngines.All) {
            services.AddSingleton<IScreenMachineEngine>(implementationInstance: engine);
        }

        // The reserved derived-face slot range (None-sourced placeholders, so a creation FACE appearing at a later
        // delivery re-points a slot that already exists — the render provider key set is frozen at boot) —
        // shared by WorldMachineHost and WorldScreenBinder below so BOTH see the identical index set.
        static IReadOnlyList<WorldScreen> ExpandedScreens(WorldDefinition definition) =>
            [.. definition.Screens, .. WorldCreationFacets.ReservedFaceSlots(derivedFaceBase: WorldCreationFacets.DerivedFaceBase, derivedFaceScreens: definition.Authoring.DerivedFaceScreens)];

        // The authoritative screen-machine host — owns every booted IScreenMachine, in EVERY boot shape: registered
        // here (not under AddWorldPresentation) so a headless boot's cabinets run exactly like a windowed one's. A
        // PEER singleton to WorldServer (which takes it as a constructor parameter), never a private field WorldServer
        // builds, so the container disposes the machines it holds.
        services.AddSingleton(implementationFactory: static sp => new WorldMachineHost(
            screens: ExpandedScreens(definition: sp.GetRequiredService<WorldDefinition>()),
            engines: sp.GetServices<IScreenMachineEngine>(),
            documentPath: sp.GetRequiredService<WorldDefinitionSource>().SourcePath
        ));

        // The screen binder — owns the declared screens' CPU-fed GPU sources (test patterns, the shared webcam,
        // window captures) and READS Server.WorldMachineHost's outputs for a machine-owning index (it no longer
        // boots, steps, or owns a machine itself — see WorldMachineHost's own remarks). CORE (not presentation-only)
        // because WorldPlacementCommandModule's world.faces and PlayerCommandModule's player.engage both read its
        // bound/no-signal state. ConfigureViews (the offscreen jumbotron pool) is ONLY ever called from
        // presentation-only code (the render-root factory) — a headless boot constructs the binder as pure state and
        // never GPU-wires it, so no capture device or GPU-side texture is ever touched.
        services.AddSingleton(implementationFactory: static sp => {
            var definition = sp.GetRequiredService<WorldDefinition>();

            return new WorldScreenBinder(
                screens: ExpandedScreens(definition: definition),
                machines: sp.GetRequiredService<WorldMachineHost>(),
                cameraCapture: sp.GetRequiredService<ICameraCaptureService>(),
                windowCapture: sp.GetRequiredService<INativeImageCaptureService>(),
                cameras: definition.Cameras,
                anchors: sp.GetRequiredService<WorldClient>(),
                stamps: sp.GetRequiredService<WorldStampPool>(),
                // On the D3D12 host the window/monitor capture feeds publish GPU-side into shared textures the
                // screens sample directly; the Vulkan host keeps the CPU-pixel transport. Camera stays CPU
                // everywhere. Headless never resolves either backend, so this bool only matters once presentation
                // composes.
                hostsOnDirectX: sp.GetRequiredService<WorldHostSettings>().HostsOnDirectX
            );
        });

        // The participant/census verb surface — world.players/.devices/.population. Split out of WorldCommandModule
        // (which stays presentation-only) because these three read pure roster/population/document state.
        services.AddSingleton<ICommandModule, WorldPopulationCommandModule>();
        // The world-mutation verb surface — world.kit.default, world.population.defaults, world.placement.get,
        // world.grant.set/.remove, world.reset/.load/.reload/.undo/.save/.status/.references (the whole-row
        // kit/screen/camera/spawns/motion/etc. verbs this module used to carry folded into world.row.set/.remove).
        // A SEPARATE module from WorldCommandModule to keep that class under its analyzer ceilings.
        services.AddSingleton<ICommandModule, WorldMutationCommandModule>();
        // The general document-row verb pair — world.row.set/.remove — that replaced the one-verb-per-section RMW
        // sugar the console had accumulated (a dotted document member path selects the section), plus world.kits
        // (the kit section's own census, which had none). CORE, like the mutation module above: no presentation
        // dependency, and a headless script wants the same general row door a windowed one has.
        services.AddSingleton<ICommandModule, WorldRowCommandModule>();
        // The contact/solidity verb surface — world.collision.probe/.status and the world.contacts read. Authoring
        // the field or a kit's collider goes through world.row.set collision/world.row.set kits.
        services.AddSingleton<ICommandModule, WorldCollisionCommandModule>();
        // The LOOK verb surface — world.population.spawn (the spawn-policy RMW) and the world.looks census.
        // Authoring a look row goes through world.row.set looks; assignment through world.assign looks.
        services.AddSingleton<ICommandModule, WorldLookCommandModule>();
        // The inhabitation + creation-facet READ-BACK surface — world.inhabitants, world.faces,
        // world.attachments, world.portals. The facets themselves are authored through
        // world.row.set placements <json>.
        services.AddSingleton<ICommandModule, WorldPlacementCommandModule>();
        // The capability-grant verb surface — world.grant/world.revoke/world.grants/world.why.
        services.AddSingleton<ICommandModule, WorldGrantCommandModule>();
        // The group+membership binding substrate verb surface — world.group.form/.join/.leave/.kick,
        // world.ownership.offer/.accept/.reclaim, world.groups. Kind rows are authored through
        // world.row.set groups.kinds/world.row.remove groups.kinds.
        services.AddSingleton<ICommandModule, WorldGroupCommandModule>();
        // The live addon-runtime verb surface — world.addon.reload/.enable/.disable + world.addons.
        services.AddSingleton<ICommandModule, WorldAddonCommandModule>();
        // The refusal-catalog read-back verb — world.refusals. No constructor dependency: compiled-in data.
        services.AddSingleton<ICommandModule, WorldRefusalsCommandModule>();
        // The storage verb surface — storage.status/push/pull/credential over the owned-world catalog.
        services.AddSingleton<ICommandModule, WorldStorageCommandModule>();
        // The TCP socket's read-back verb — world.peers (the connection table WorldTcpHost owns).
        services.AddSingleton<ICommandModule, WorldNetworkCommandModule>();
        // The diegetic screens' verb surface — screen.insert/.eject/.select/.options/.link/.unlink submit a
        // WorldScreenOp through the ordered domain to the CORE Server.WorldMachineHost (machines boot and step in
        // every shape, so scripting a cabinet — and reading screen.state/.peek back — must work headless too);
        // screen.source (camera|capture|desktop|qr|view — the five former per-kind verbs folded into one) and
        // screen.links stay
        // genuinely presentation calls straight into WorldScreenBinder (harmless headless: they attempt a real
        // device open/no-op exactly as they always have, never gated on a boot shape).
        services.AddSingleton<ICommandModule, ScreenCommandModule>();
        // world.identify — the world's own identity (documentId + the live definition's content-address pin) drawn onto
        // a declared screen through the SAME live-QR path screen.source <index> qr drives. A composition of two
        // existing capabilities, registered beside the screen surface it borrows because it needs the same binder.
        services.AddSingleton<ICommandModule, WorldIdentifyCommandModule>();

        // The true-deterministic-replay tape — captures the running session's per-tick server-input stream +
        // starting state off the loopback, and rehydrates a fresh world to verify a recorded-vs-replayed hash match
        // offline. WorldServerStepShell closes each captured tick inside Step (shared by both boot shapes); the
        // replay.* verb surface arms and verifies it.
        services.AddSingleton(implementationFactory: static sp => new WorldReplayTape(
            liveServer: sp.GetRequiredService<WorldServer>(),
            profiles: sp.GetRequiredService<WorldOwnedWorlds>(),
            transport: sp.GetRequiredService<LoopbackTransport>(),
            engines: sp.GetServices<IScreenMachineEngine>()
        ));
        services.AddSingleton<ICommandModule, WorldReplayCommandModule>();

        // The console's sequencing primitive: the tick barrier world.wait arms (published by the shared server-step
        // shell each fixed step) and the verb that arms it. CORE — world.wait is a server-safe verb by name (DELIVER
        // item 3), and a headless script needs the SAME read-after-write fence a windowed one does.
        services.AddSingleton<WorldConsoleWaitGate>();
        services.AddSingleton<ICommandModule, WorldWaitCommandModule>();
        // The command source: results echo to stdout/stderr (split by verdict) and the tick barrier holds queued
        // lines. Registered here (NOT TryAdd) so it wins over AddLauncherTerminalShared's own TryAddSingleton — the
        // ordering AddWorldAuthoritativeCore always running before AddLauncherTerminal/AddLauncherHeadlessTerminal
        // guarantees this. The on-screen console mirror is OPTIONAL (sp.GetService, not GetRequiredService): present
        // when AddWorldPresentation also ran, null headless — the mirror recording is simply skipped, never a
        // missing-service crash.
        services.AddSingleton(implementationFactory: static sp => {
            var output = sp.GetRequiredService<BufferedConsoleOutput>();
            var waitGate = sp.GetRequiredService<WorldConsoleWaitGate>();

            return new TextCommandSource(
                onResult: (line, result) => {
                    if (!string.IsNullOrEmpty(value: result.Output)) {
                        // A REFUSED line goes to stderr, an accepted one to the buffered stdout — the same split the
                        // launcher's own sink makes (this one replaces it to also feed the on-screen console mirror
                        // when one is composed). Without it a rejection is byte-shaped like success on the same
                        // stream and a scripted run reads green.
                        if (result.IsError) {
                            output.WriteErrorLine(value: result.Output);
                        } else {
                            output.WriteLine(value: result.Output);
                        }
                    }

                    sp.GetService<WorldConsoleMirror>()?.Record(line: line, result: result);
                },
                registry: sp.GetRequiredService<CommandRegistry>()
            ) {
                HoldGate = waitGate.IsHolding,
            };
        });

        // The world-scope HUD's live binding resolver (world.tick/world.fps/seat.<n>.position.*/population.active)
        // and its console read-back — world.hud + world.hud.template. The panels and defaults themselves are
        // authored through world.row.set/world.row.remove over hud.panels/hud.defaults, and an element rides its
        // panel's row rather than having a door of its own. CORE: every dependency (WorldClient, FrameRateMonitor, WorldPopulation) is
        // core data, and the HUD document itself is server state — only the STRUCTURE store that feeds the
        // on-screen overlay (HudStore/WorldHudFeed) is presentation-only.
        services.AddSingleton<IHudBindingResolver>(implementationFactory: static sp => new WorldHudBindingResolver(
            client: sp.GetRequiredService<WorldClient>(),
            frameRate: sp.GetRequiredService<FrameRateMonitor>(),
            population: sp.GetRequiredService<WorldPopulation>(),
            anchor: sp.GetRequiredService<WorldPerceptionAnchor>()
        ));
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => new WorldHudCommandModule(
            server: sp.GetRequiredService<WorldServer>(),
            bindings: sp.GetRequiredService<IHudBindingResolver>(),
            roster: sp.GetRequiredService<PlayerRoster>()
        ));

        // The genre-neutral state section's FINE-GRAIN verb surface — world.state.cell.set (one cell, dispatching
        // on the row's own declared kind, text included) and .remove, world.generate (redraws a draw site), and
        // world.state (read-back at all three grains). A whole ROW is authored through
        // world.row.set/world.row.remove state <row-json>. ONE module for one substrate: a slot IS a row with one
        // cell, so there is no second family over the same rows. CORE, like the HUD module above: the document
        // itself is server state, and no presentation dependency is needed to read or write it.
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => new WorldStateCommandModule(
            server: sp.GetRequiredService<WorldServer>(),
            link: sp.GetRequiredService<IServerLink>()
        ));

        // The `rules` section's READ-BACK — world.rules reads the live compiled set; the rows themselves are
        // authored through world.row.set/world.row.remove rules <json>. CORE for the same reason the state
        // module is: rules are document state.
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => new WorldRulesCommandModule(
            link: sp.GetRequiredService<IServerLink>()
        ));

        // The generalized property-interaction READ-BACK — world.properties/world.interactions; both sections are
        // authored through world.row.set/world.row.remove over properties.names (a bare name token, the one
        // grammar exception) and interactions.interactions. CORE for the same reason the rules
        // module is: both sections are document state that compile to the SAME rule substrate.
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => new WorldInteractionCommandModule(
            link: sp.GetRequiredService<IServerLink>()
        ));

        // The process's running world instances (docs/world-model.md's "Multi-world ticking in one process" row):
        // the boot world plus every instance started at runtime through the console, stepped by both boot shapes'
        // IFixedStepSimulation.Step. CORE (not presentation-only): an instance beside the boot world is render-less
        // by construction, so it works identically headless or windowed.
        services.AddSingleton<WorldInstanceHost>();
        services.AddSingleton<ICommandModule, WorldInstanceCommandModule>();

        return services;
    }

    /// <summary>
    /// Layers the GPU host, render root, overlays, audio device, screens/machines, gamepads, and editor over the
    /// authoritative core. Registered ONLY when <c>WorldHostSettings.Headless</c> is
    /// <see langword="false"/> — every presentation-only console module refuses as UNKNOWN over stdin when this
    /// method never ran.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="hostsOnDirectX">Whether the resolved backend is Direct3D 12 (else Vulkan) — needed eagerly (not
    /// deferred to a factory) because <c>WorldHost.AddWorldGpuHost</c> branches its OWN registrations on it.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddWorldPresentation(this IServiceCollection services, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(argument: services);

        services.AddOptions<NativeWindowOptions>().Configure<WorldHostSettings>(configureOptions: static (options, hostSettings) => {
            options.Height = (uint)hostSettings.Height;
            options.Mode = NativeWindowMode.PlatformWindow;
            options.StartFullscreen = hostSettings.Fullscreen;
            options.Title = WorldApplicationDefaults.WindowTitle;
            options.Width = (uint)hostSettings.Width;
        });
        services.AddSingleton(implementationFactory: static sp => new PresentationOptions {
            PresentMode = sp.GetRequiredService<WorldHostSettings>().PresentMode,
            SurfaceFormat = sp.GetRequiredService<WorldHostSettings>().SurfaceFormat,
        });
        // The external-clock election policy from the host section's genlock field. Registered BEFORE AddWorldGpuHost
        // → AddLauncherTerminal (below) so the launcher's TryAddSingleton<ExternalClockRegistry> defers to this one.
        services.AddSingleton(implementationFactory: static sp => new ExternalClockRegistry(electionPolicy: sp.GetRequiredService<WorldHostSettings>().Genlock));

        // The per-seat editor mode: the mode owner (binding MODE layer + honest-idle diversion + camera rig swap),
        // the drag preview channel (client-local pending rows, one mutation on release), the look-ray picker (a
        // document-derived fixed-point program), and the selection/targeting state — plus the editor.* verb modules
        // (SEPARATE modules for the analyzer ceilings). The orbit pivot retargets at the selection via property
        // injection (targeting composes after the session).
        services.AddSingleton<WorldEditorDrag>();
        // The sculpt workbench: the per-seat creation sub-editor's client context — its preview creation/placement
        // compose over the delivered rows through the SAME stamp path a committed placement uses. The drag channel's
        // ghost envelope pre-checks fold the workbench preview in (property-injected — the workbench composes after
        // the drag).
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
                session: sp.GetRequiredService<WorldEditorSession>(),
                stamps: sp.GetRequiredService<WorldStampPool>()
            );

            var session = sp.GetRequiredService<WorldEditorSession>();

            session.OrbitPivotSource = targeting.SelectionPosition;
            // Deactivation (exit / departed seat) clears the seat's selection with its drag (the teardown contract
            // every deactivation path must honor).
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
        // place-by-name twins.
        services.AddSingleton<ICommandModule, EditorCreationCommandModule>();

        // The world speaker device: the hosted service owning the mixer + the WASAPI governor/pump threads. One
        // dedicated bounded-join worker owns the device lifecycle, so a stalled device cannot wedge shutdown; a
        // platform without a render backend gets a null factory and the service parks as 'unsupported'.
        services.AddSingleton(implementationFactory: static sp => new WorldAudioRenderService(
            director: sp.GetRequiredService<WorldAudioDirector>(),
            factory: AudioRenderPlatform.CreateFactory()
        ));
        services.AddHostedService(implementationFactory: static sp => sp.GetRequiredService<WorldAudioRenderService>());

        // The window composer — layout selection + eased transitions. One shared instance the frame source drives
        // each produced frame and the world.view.state read observes.
        services.AddSingleton<WorldViewComposer>();

        // The world's own presentation verb surface — world.fps/.gpu, world.screens/.cameras, and the graphics
        // options (shadows, ambient occlusion, render scale, an FPS target, a quality preset). Refuses as unknown
        // over headless stdin because this whole method never runs there.
        services.AddSingleton<ICommandModule, WorldCommandModule>();
        // The host-section READ-BACK — world.host, the DOCUMENT/RESOLVED/LIVE three-way read; the section is
        // written through world.row.set host <json>. Presentation-only: window/backend/present/pacing/GPU-timing
        // knobs.
        services.AddSingleton<ICommandModule, WorldHostCommandModule>();
        // The window-composition verb surface — view.override camera|layout (the live overrides) and the
        // world.view.state/.orbit/.pointer reads. The authored rows live under world.row.set/world.row.remove over
        // views.seatRig/views.layouts.
        services.AddSingleton<ICommandModule, WorldViewCommandModule>();
        // The audio READ-BACK + lever surface — world.speakers/audio.state/speaker.state/world.volume/
        // audio.emitters. The rows are written through world.row.set/world.row.remove over
        // speakers/tunes/patches/audio. Presentation-only: injects the audio device render service directly.
        services.AddSingleton<ICommandModule, WorldAudioCommandModule>();
        // The recording graph (puck.recording.v1) — native capture for streaming/upload workflows. Presentation-only:
        // AddRecordingPlatform registers the Media Foundation encoder ladder + WASAPI loopback/microphone sources and
        // the shared session clock; the render-root factory wires the RecordingTap into the capture render node.
        services.AddRecordingPlatform();
        services.AddSingleton<RecordingTap>();
        services.AddSingleton<ICommandModule, WorldRecordingCommandModule>();

        // Controllers, first-class beside the keyboard: the hardware manager (HID + the Xbox XInput/GameInput poll
        // thread), the focus-gated snapshot capture binding the sticks to the player's Axis2D channels, and the
        // hosted service that governs device lifetime (hotplug rescans every ~1.5 s). Presentation-only — a headless
        // host has no window to give input focus to, so nothing here would ever fire.
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

        // The screen-space overlay UI (Puck.Overlays): the console mirror, the per-seat binding bars, and the
        // mutation toasts, all drawn by the ONE UnifiedOverlayNode wrapped around the world render below. The stores
        // are the lock-free read seams; the mirror is stdin/stdout's visible twin. Presentation-only — nothing draws
        // headless.
        services.AddSingleton<ConsolePanelStore>();
        services.AddSingleton<BindingBarStore>();
        services.AddSingleton<EditorHudStore>();
        services.AddSingleton<EditorGizmoStore>();
        services.AddSingleton<OverlayToastStore>();
        services.AddSingleton<WorldConsoleMirror>();
        // The mirror also observes the DISPATCH path, so a Simulation-routed verb's tick-deferred verdict — which
        // the core TextCommandSource's onResult callback never sees (Submit returned None) — paints on the panel
        // too, a refusal in the danger role.
        services.AddSingleton<ICommandObserver>(implementationFactory: static sp => sp.GetRequiredService<WorldConsoleMirror>());

        // The console's line editor and its IWindowInputObserver bridge — presentation-only twins of the mirror
        // above, so they live in this method exactly as it does: a headless boot never runs AddWorldPresentation,
        // so it never sees a WorldConsoleMirror to build one against, and no capability gets contributed.
        services.AddSingleton(implementationFactory: static sp => {
            var mirror = sp.GetRequiredService<WorldConsoleMirror>();
            var input = new WorldConsoleInput(
                source: sp.GetRequiredService<TextCommandSource>(),
                mirror: mirror,
                clipboard: sp.GetRequiredService<IClipboardService>()
            );
            var focus = sp.GetRequiredService<IInputFocus>();

            // Suppression lives in IInputFocus, not in the sink below: showing the console releases the
            // KEYBOARD device's focus — PlayerRoster.KeyboardDevice IS InputDeviceId's default, the SAME id the
            // window pump's unfocus check reads (LauncherWindowHostedService's IsActiveFor(deviceId: default)),
            // so releasing it also trips the pump's ReleaseHeld() on the transition and any already-held
            // movement key drops for free — a held W never keeps driving the avatar into typed text. Hiding
            // claims the device back and resets the line editor through its own home, so a reopened console
            // never resurrects a half-typed line.
            mirror.VisibilityChanged = visible => {
                if (visible) {
                    focus.Release(deviceId: PlayerRoster.KeyboardDevice);
                } else {
                    input.Clear();
                    focus.Claim(deviceId: PlayerRoster.KeyboardDevice);
                }
            };

            // The mirror starts visible by construction (the console is the control plane) — apply the show-edge
            // suppression above once here too, so the keyboard starts released rather than driving gameplay
            // through an already-open panel; VisibilityChanged only fires on a later TRANSITION.
            if (mirror.Visible) {
                focus.Release(deviceId: PlayerRoster.KeyboardDevice);
            }

            return input;
        });
        services.AddSingleton<WorldConsoleTextSink>();
        services.AddSingleton<IWindowInputObserver>(implementationFactory: static sp => sp.GetRequiredService<WorldConsoleTextSink>());

        // The pointer: ONE store of live browsing state (cursor position, drainable motion and wheel, held
        // buttons — per seat), ONE IWindowInputObserver that writes it, and any number of consumers that read it.
        // Presentation-only (nothing here rides a CommandSnapshot), so it lives in this method rather than the
        // authoritative-core one — a headless boot never sees a pointer to observe. A new pointer-driven feature
        // registers an IWorldPointerConsumer below; it does NOT add a second window-input observer.
        services.AddSingleton<WorldPointer>();

        // The local mouse seat's right-drag camera orbit (WoW-style): the shared yaw/pitch state WorldFrameSource
        // composes onto the slot-0 chase camera anchor, and the pointer consumer that nudges it while the authored
        // arming button is held.
        services.AddSingleton<WorldCameraOrbit>();
        services.AddSingleton<WorldCameraOrbitDrag>();
        services.AddSingleton<IWorldPointerConsumer>(implementationFactory: static sp => sp.GetRequiredService<WorldCameraOrbitDrag>());

        services.AddSingleton<WorldPointerSink>();
        services.AddSingleton<IWindowInputObserver>(implementationFactory: static sp => sp.GetRequiredService<WorldPointerSink>());

        // The drawn cursor: the per-seat viewport+camera publication the frame source fills each dressed frame, the
        // per-frame feed that reads the pointer store NON-destructively (position + held buttons; the drained
        // motion/wheel accumulators stay WorldCameraOrbitDrag's), and the store the unified overlay's cursor writer
        // renders. All presentation/session state — nothing here reaches a CommandSnapshot or the simulation.
        services.AddSingleton<WorldSeatViewports>();
        services.AddSingleton<CursorStore>();
        services.AddSingleton(implementationFactory: static sp => new WorldCursorFeed(
            pointer: sp.GetRequiredService<WorldPointer>(),
            roster: sp.GetRequiredService<PlayerRoster>(),
            client: sp.GetRequiredService<WorldClient>(),
            seatFeel: sp.GetRequiredService<WorldSeatFeel>(),
            viewports: sp.GetRequiredService<WorldSeatViewports>(),
            picker: sp.GetRequiredService<WorldEditorPicker>(),
            hud: sp.GetRequiredService<HudStore>(),
            store: sp.GetRequiredService<CursorStore>()
        ));

        // The editor's mouse manipulation policy: click-select and cursor drag-and-drop over the feed's published
        // decision — per-frame polled edges on the pointer store (no observer, no consumer, no draining), acts
        // dispatched through the existing editor.select verb and the existing drag channel. Presentation/session
        // policy, inert while the seat is not editing.
        services.AddSingleton(implementationFactory: static sp => new WorldEditorMouse(
            pointer: sp.GetRequiredService<WorldPointer>(),
            roster: sp.GetRequiredService<PlayerRoster>(),
            session: sp.GetRequiredService<WorldEditorSession>(),
            drag: sp.GetRequiredService<WorldEditorDrag>(),
            feed: sp.GetRequiredService<WorldCursorFeed>(),
            viewports: sp.GetRequiredService<WorldSeatViewports>(),
            console: sp.GetRequiredService<TextCommandSource>()
        ));

        // The radial action menu — held binding pages presenting themselves: the store the overlay's wheel writer
        // reads, the feed that keeps the radial's presentation state (hub anchor, active ring, hovered sector) and
        // returns a sector activation through the seat's input-router lane, and the verb surface
        // world.view.wheel). The feed is the process's ONE IWorldWheelConsumer — registered as a consumer BELOW so
        // WorldPointerSink's construction-time registration sees it and stops drain-discarding the wheel
        // accumulator. Its router is lazy because CommandRegistry aggregates WorldWheelCommandModule, which consumes
        // the feed.
        services.AddSingleton<WheelStore>();
        services.AddSingleton(implementationFactory: static sp => new WorldWheelFeed(
            pointer: sp.GetRequiredService<WorldPointer>(),
            roster: sp.GetRequiredService<PlayerRoster>(),
            bindings: sp.GetRequiredService<WorldSeatBindings>(),
            cursor: sp.GetRequiredService<WorldCursorFeed>(),
            viewports: sp.GetRequiredService<WorldSeatViewports>(),
            store: sp.GetRequiredService<WheelStore>(),
            router: () => sp.GetRequiredService<InputRouter>()
        ));
        services.AddSingleton<IWorldPointerConsumer>(implementationFactory: static sp => sp.GetRequiredService<WorldWheelFeed>());
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => new WorldWheelCommandModule(
            feed: sp.GetRequiredService<WorldWheelFeed>(),
            roster: sp.GetRequiredService<PlayerRoster>()
        ));

        // Both sinks above want every raw window input event, but IHostContext.HoldsCapability resolves exactly
        // ONE instance per capability type (see IWindowInputObserver's doc comment) — WorldWindowInputObservers
        // fans out to every IWindowInputObserver singleton registered above, and it ALONE is contributed as the
        // HELD root capability the window pump resolves, ahead of mapping/routing (mirroring IInputFocus above).
        services.AddSingleton(implementationFactory: static sp => new WorldWindowInputObservers(observers: sp.GetServices<IWindowInputObserver>()));
        services.AddSingleton(implementationFactory: static sp => new HostCapabilityContribution(
            CapabilityType: typeof(IWindowInputObserver),
            Instance: sp.GetRequiredService<WorldWindowInputObservers>(),
            IsHeld: true
        ));

        // The authored world-scope AND player-scope HUD's STRUCTURE store (world panels reconciled from the
        // delivered definition on revision move; seat panels recomposed every tick from the roster + each joined
        // seat's profile — see WorldHudFeed's own remarks) and its feed (its Tick joins WorldOverlayFeed's in the
        // render root's FeedTick chain below; roster/editor are read-only here, the SAME LayoutRegion call
        // WorldOverlayFeed makes for its own per-seat rects). The live binding resolver and the world.hud verb
        // surface are core (WorldBootComposition.AddWorldAuthoritativeCore) — only the on-screen render cache is
        // presentation-only.
        services.AddSingleton<HudStore>();
        services.AddSingleton(implementationFactory: static sp => new WorldHudFeed(
            client: sp.GetRequiredService<WorldClient>(),
            roster: sp.GetRequiredService<PlayerRoster>(),
            editor: sp.GetRequiredService<WorldEditorSession>(),
            store: sp.GetRequiredService<HudStore>()
        ));

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
        // The overlay verb surface — world.screenshot (the composed-frame capture) + world.console (the mirror
        // toggle). Presentation-only: both need a live render/overlay.
        services.AddSingleton<ICommandModule, WorldUiCommandModule>();

        // The render probe the world.gpu/world.debug-view verbs read the live engine's per-pass GPU times through —
        // a mutable holder the render-root factory below fills in once the engine node exists.
        services.AddSingleton<WorldRenderProbe>();

        // The first-party puck.sdf.v1 geometry-document emitter (world.sdf.load) — a singleton so the command
        // module and the render-root factory below share the SAME instance regardless of resolution order.
        services.AddSingleton<WorldSdfDocumentEmitter>();
        services.AddSingleton<ICommandModule, WorldSdfCommandModule>();

        // The trimmed GPU host (windowing, allocator, one complete launch-selected backend), minus the demo-only
        // camera-capture concern. Registering only the selected backend ensures the neutral compute services and
        // presenter name the same physical device and shader format. This is the ONLY call in the whole composition
        // that opens a window, a GPU device, or a swapchain — never reached on the headless boot shape.
        WorldHost.AddWorldGpuHost(services: services, hostsOnDirectX: hostsOnDirectX);

        // The render root: the shared SDF world assembly over the grass-and-boulders scene. The built Producer (the
        // live SdfEngineNode) is stashed on the WorldRenderProbe so the world.gpu verb can read its per-pass GPU
        // times. The frame source emits active avatars only (declared-but-parked instances widen the per-pixel
        // shadow mask walk), so the 128-avatar worst case is held by the capacity floors a construction-time probe
        // measured, plus the viewport floor for the join-later split screen. The affordance install, EchoTap,
        // MachineLifecycleTap, and lever-sink attachment that used to live INSIDE this factory now live in the
        // shared post-build wiring step (WorldPostBuildWiring) — this factory only builds the render tree.
        services.AddSingleton<IRenderNode>(implementationFactory: sp => {
            var hostSettings = sp.GetRequiredService<WorldHostSettings>();
            var width = (uint)hostSettings.Width;
            var height = (uint)hostSettings.Height;
            var binder = sp.GetRequiredService<WorldScreenBinder>();

            // The view-composition GPU-services bundle: resolved ONCE, eagerly,
            // right here at the composition root — the OverlayServices.Build precedent (resolve inside the factory,
            // hand out concrete members, the provider never escapes) — then forwarded UNCHANGED through ConfigureViews
            // and Build to every late-construction site (the binder's stashed camera-view factory, and SdfEngineNode
            // itself). Retires the retained IServiceProvider those sites used to stash and re-resolve from on their
            // own late-construction paths.
            var viewGpuServices = new SdfViewGpuServices(
                Gpu: sp.GetRequiredService<IGpuComputeServices>(),
                TimingFactory: (sp.GetService(serviceType: typeof(IGpuTimingPoolFactory)) as IGpuTimingPoolFactory),
                TimingRecorder: (sp.GetService(serviceType: typeof(IGpuTimingRecorder)) as IGpuTimingRecorder)
            );

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
                anchor: sp.GetRequiredService<WorldPerceptionAnchor>(),
                cameraOrbit: sp.GetRequiredService<WorldCameraOrbit>(),
                seatFeel: sp.GetRequiredService<WorldSeatFeel>(),
                composition: sp.GetRequiredService<WorldCompositionState>(),
                composer: sp.GetRequiredService<WorldViewComposer>(),
                sdfDocuments: sp.GetRequiredService<WorldSdfDocumentEmitter>(),
                viewports: sp.GetRequiredService<WorldSeatViewports>()
            );

            // Stand up the jumbotron view pool now the frame source has probed the render envelope: each View screen
            // registers a persistent offscreen camera render sized to these worst-case capacities, using the
            // selected host's bytecode. A no-op when the world declares no View screen.
            binder.ConfigureViews(
                services: viewGpuServices,
                hostsOnDirectX: hostSettings.HostsOnDirectX,
                programWordCapacity: frameSource.ProgramWordCapacity,
                instanceCapacity: frameSource.InstanceCapacity,
                dynamicTransformCapacity: frameSource.DynamicTransformCapacity
            );

            // Captured out of the Decorate closure so the probe can expose the overlay's pass timing (world.gpu).
            UnifiedOverlayNode? overlayNode = null;
            var render = SdfWorldRenderBuilder.Build(
                services: viewGpuServices,
                spec: new SdfWorldRenderSpec(
                    FrameSource: frameSource,
                    Width: width,
                    Height: height
                ) {
                    // The unified overlay (console mirror + per-seat binding bars + toasts) wraps the producer on
                    // BOTH backends: neutral services, bytecode selected by the resolved host. Degrades loudly to
                    // the bare world when the pre-baked glyph atlas is missing.
                    Decorate = producer => {
                        var fontsDirectory = Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Fonts");
                        // The prepacked-artifact path: a warm start reads the ~1.4 MiB pack beside the atlas; only a
                        // cold/rebaked start decodes the combined PNG (and persists the pack for the next boot).
                        var glyphs = new OverlayGlyphAtlasSet(fontsDirectory: fontsDirectory).LoadOverlayPack();

                        if (glyphs is null) {
                            Console.Error.WriteLine(value: $"[unified-overlay] skipped: no usable glyph atlas under '{fontsDirectory}' (rebake via experimental/tools/font-atlas).");

                            return producer;
                        }

                        var bytecodeExtension = SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: hostSettings.HostsOnDirectX);

                        return overlayNode = new UnifiedOverlayNode(
                            fragmentBytecode: File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: $"overlay-unified.frag{bytecodeExtension}")),
                            glyphs: glyphs,
                            height: height,
                            inner: producer,
                            services: OverlayServices.Build(hostsOnDirectX: hostSettings.HostsOnDirectX, serviceProvider: sp),
                            sources: new UnifiedOverlaySources(
                                BindingBar: sp.GetRequiredService<BindingBarStore>(),
                                Console: sp.GetRequiredService<ConsolePanelStore>(),
                                EditorHud: sp.GetRequiredService<EditorHudStore>(),
                                // WorldHudFeed's Tick joins WorldOverlayFeed's in the same per-produced-frame hook —
                                // it only reconciles HudStore's STRUCTURE on a definition-revision move (cheap on
                                // every other frame); live binding VALUES are resolved separately, every frame, by
                                // HudWriter through HudBindings.
                                FeedTick: () => {
                                    sp.GetRequiredService<WorldOverlayFeed>().Tick();
                                    sp.GetRequiredService<WorldHudFeed>().Tick();
                                    // The cursor feed reads the viewports the frame source published THIS frame
                                    // (the node runs FeedTick after the inner producer's frame), so it runs after
                                    // the two feeds above only by convention — its only ordering need is being
                                    // after the dress, which the node's call order already guarantees.
                                    sp.GetRequiredService<WorldCursorFeed>().Tick();
                                    // The mouse policy DOES order against the cursor feed: it acts on the very
                                    // decision (visibility, hover, local position) the feed just published, so a
                                    // press acts on exactly what the player saw this frame.
                                    sp.GetRequiredService<WorldEditorMouse>().Tick();
                                    // The radial menu orders against the cursor feed the same way: its hub anchor
                                    // and hover derive from the status the feed just published.
                                    sp.GetRequiredService<WorldWheelFeed>().Tick();
                                },
                                Gizmos: sp.GetRequiredService<EditorGizmoStore>(),
                                Toast: sp.GetRequiredService<OverlayToastStore>(),
                                Hud: sp.GetRequiredService<HudStore>(),
                                HudBindings: sp.GetRequiredService<IHudBindingResolver>(),
                                Cursor: sp.GetRequiredService<CursorStore>(),
                                Wheel: sp.GetRequiredService<WheelStore>()
                            ),
                            vertexBytecode: File.ReadAllBytes(path: Path.Combine(path1: SdfWorldKernels.DefaultDirectory, path2: $"fullscreen.vert{bytecodeExtension}")),
                            width: width
                        );
                    },
                    DynamicTransformCapacity = frameSource.DynamicTransformCapacity,
                    HostsOnDirectX = hostSettings.HostsOnDirectX,
                    InstanceCapacity = frameSource.InstanceCapacity,
                    ProgramWordCapacity = frameSource.ProgramWordCapacity,
                    // The ray-query hardware path from the host section — the document decides.
                    RayQuery = hostSettings.RayQuery,
                    // The diegetic screens' source + light providers — the test-pattern screen's CPU feed and its
                    // room glow; an unbound screen has no provider (the engine's procedural fallback lights it).
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

            // The native-capture present tap: wrap the render root once, for the world's whole lifetime, in the
            // backend-neutral CapturingRenderNode. The live windowed present path hands GPU surfaces, so the tap
            // reads each captured frame back to CPU pixels through the SDF engine (probe.Node.ReadOutputPixels) — a
            // synchronous GPU readback that runs ONLY while a session is armed (the RecordingTap.WantsFrames gate),
            // so the tap is free until capture.start. The capture cadence keeps roughly the recording document's
            // frame rate out of the desktop 120 Hz target.
            var recordingDocument = sp.GetRequiredService<RecordingDocumentSource>().Document;
            var tap = sp.GetRequiredService<RecordingTap>();

            // The teardown tie: the window loop disposes this root (device alive) before the presenter and long
            // before the container's reverse-creation-order sweep — ride that safe point for the binder's own GPU
            // holdings (camera feeds, jumbotron view engines), whose container-ordered disposal would otherwise land
            // after device death.
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

        return services;
    }
}
