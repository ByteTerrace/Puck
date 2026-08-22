using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Hosting;
using Puck.Input;
using Puck.Launcher;
using Puck.Launcher.Linux;
using Puck.Launcher.Windows;
using Puck.Networking;
using Puck.Overlays;
using Puck.Platform;
using Puck.Platform.Audio;
using Puck.Platform.Linux;
using Puck.Platform.Windows;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.World.Addons;
using Puck.World.Audio;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The boot-shape composition split: <see cref="AddWorldAuthoritativeCore"/> is the whole
/// server-safe world — everything that works with no window, no GPU device, no swapchain, no audio device.
/// <see cref="AddWorldPresentation"/> layers the GPU host, render root, overlays, audio, and screens/machines/gamepads
/// on top. <c>Program.cs</c> calls the core method always and the presentation method only when
/// <c>WorldHostSettings.Headless</c> is <see langword="false"/> — the boot-shape branch, decided before either method
/// runs. Both take only <c>this IServiceCollection services</c> (plus the one value <see cref="AddWorldPresentation"/>
/// needs eagerly, before any factory could resolve it): every other dependency is read from the already-registered
/// <see cref="WorldDefinitionSource"/>/<see cref="WorldHostSettings"/>/<see cref="WorldSeatBindings"/> singletons
/// <c>Program.cs</c> registers before calling either method.
/// <para><b>The command vocabulary must be identical in every boot shape.</b> The document validators (see
/// <c>WorldDefinitionValidator.ValidateBindingOverlays</c>, <c>BindingVocabularyHook</c>) check a world's
/// <c>bindingOverlays</c> — and the engine-default document's own wheels and editor pages, which every world
/// compiles in unconditionally — against whatever this composition registers, so a command a shipped world or the
/// engine default commits must be registered in every shape or a headless boot refuses a document a windowed one
/// admits. The editor verb family moved to <see cref="AddWorldAuthoritativeCore"/> wholesale because nothing
/// in its dependency chain is GPU-typed; <see cref="WorldUiCommandModule"/> and <see cref="WorldWheelCommandModule"/>
/// stay core-registered too but resolve their presentation dependency as optional and refuse by name at use — they
/// genuinely need a live render/pointer, which only <see cref="AddWorldPresentation"/> can supply.</para>
/// </summary>
internal static class WorldBootComposition {
    /// <summary>
    /// The authoritative core: profiles, roster, server, grants, population, addon runtime, replay tape, the
    /// submission/output hub (via <see cref="WorldServer"/>), the console's tick barrier, every server-safe
    /// console module, and the whole editor verb surface (command-vocabulary parity — see the class remarks).
    /// Registered in every boot shape.
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
        // The seat authority router — a small fixed CAS table, one writer (WorldInstanceHost's transfer commit), consumed by
        // WorldClient's seat-submission doors, WorldFramePresenter's per-seat loop, WorldHudBindingResolver, and
        // WorldAudioDirector's listener resolution.
        services.AddSingleton<WorldSeatAuthorityRouter>();
        services.AddSingleton<IInputSlotResolver>(implementationFactory: static sp => sp.GetRequiredService<PlayerRoster>());
        // The roster answers BOTH input seams: which slot a device belongs to, and who is acting through that slot.
        // The mixer stamps every captured entry from the second one, so a claimed slot's input carries the claimant's
        // identity.
        services.AddSingleton<ICommandPrincipalResolver>(implementationFactory: static sp => sp.GetRequiredService<PlayerRoster>());
        services.AddSingleton<ICommandModule, PlayerCommandModule>();
        // The seat-routed document-write twins (player.row.set / player.state.cell.set) — a crossed traveler's
        // console door onto the forwarded-submission path.
        services.AddSingleton<ICommandModule, WorldRoutedRowCommandModule>();
        services.AddSingleton<ICommandModule, WorldSeatCameraCommandModule>();
        services.AddSingleton<ICommandModule, IdentityCommandModule>();
        services.AddSingleton<ICommandModule, ChatCommandModule>();

        // The rebind surface — player.bind (live session remap + chord rows) / player.bindings (echo the composed
        // active mapping) / player.signal (synthesized raw input over the pipe) / identity.bindings.save (fold
        // session rebinds into the seat's owned identity world). A SEPARATE module to keep each class
        // under its probe ceilings. The router reaches the module LAZILY: the router's factory consumes the
        // CommandRegistry, which aggregates every ICommandModule — a direct dependency would cycle the container.
        services.AddSingleton<Func<InputRouter>>(implementationFactory: static sp => (() => sp.GetRequiredService<InputRouter>()));
        // The registry reaches this module the same lazy way, for the same cycle: world.affordances reads the built
        // registry at dispatch to emit the manifest the binding vocabulary checks validate against.
        services.AddSingleton<Func<CommandRegistry>>(implementationFactory: static sp => (() => sp.GetRequiredService<CommandRegistry>()));
        services.AddSingleton<ICommandModule, WorldBindingCommandModule>();

        // The stamp pool (dynamic-creation/placement animation accounting) — plain data, no render dependency.
        // Shared by the (core) audio director's panning source and the (presentation) frame source.
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
        services.AddSingleton<WorldTextCatalog>();

        // The authoritative world server and the in-process loopback fronting it: the client submits intents,
        // commands, session requests, and buffered live edits (mutations, definition swaps, journal undo) over
        // IServerLink; the server applies them at its step boundary, answers queries, and pushes each tick's snapshot
        // (and, after an applied edit, the new definition) to every bound client sink.
        services.AddSingleton<WorldServer>();
        // IWorldServerHost is the seam Puck.World.Protocol's LoopbackTransport holds instead of the concrete WorldServer
        // type (Puck.World.Protocol cannot reference Puck.World.Server) — WorldServer implements it directly, so this is
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
        // view.override layout/camera), read by the frame source's view composer. Plain state either way.
        services.AddSingleton<WorldCompositionState>();

        // The window composer — layout selection + eased transitions. Core, both boot shapes: WorldSimulation's
        // per-tick context publish and WorldPostBuildWiring's boot-census seed both read ActiveLayoutName, so it must
        // resolve without presentation. Headless simply has no reader for the frame-driven half.
        services.AddSingleton<WorldViewComposer>();

        // The local seats' live orbit state is harmless headless data and must exist before WorldClient: camera-
        // relative world-frame intent composition reads the same yaw the presentation camera renders. Pointer/stick
        // consumers remain presentation-only and are registered later.

        // The client half: the snapshot-fed entity view + per-tick seat-intent submitter, bound to the loopback at
        // construction (the bind delivers a primer snapshot). Headless-safe BY DESIGN — see WorldClient's own doc
        // comment: a client composed without the presentation services simply drops accepted levers rather than
        // failing to construct. Needed here (not just presentation) because WorldScreenBinder (an
        // ISdfAnchorSource consumer) and WorldAudioDirector both take it, and both are core.
        services.AddSingleton(implementationFactory: static sp => {
            var client = new WorldClient(
                roster: sp.GetRequiredService<PlayerRoster>(),
                definition: sp.GetRequiredService<WorldDefinition>(),
                composition: sp.GetRequiredService<WorldCompositionState>(),
                seatRouter: sp.GetRequiredService<WorldSeatAuthorityRouter>()
            );

            // The local client sink stays attached for the whole process lifetime, so its lease is deliberately never disposed.
            _ = sp.GetRequiredService<LoopbackTransport>().Bind(sink: client);

            return client;
        });

        // The live per-seat binding-bar visibility overrides the binding-bar lever writes and WorldBindingBarControl
        // reads. Core data: the read-back verb and the world.save fold both exist headless.
        services.AddSingleton<WorldBindingBarVisibility>();

        // The accepted-session-lever applier: the ONLY writer of the live presentation knobs the lever verbs move
        // (world.volume / world.shadows / world.target / world.binding-bar and their siblings), registered here by
        // name. Every dependency (render settings, present pacing, audio director, bar visibility) is core data, so
        // the sink itself constructs core; its ATTACHMENT to the client (WorldClient.AttachSessionLevers) happens in
        // the shared post-build wiring step (WorldPostBuildWiring) instead of the old render-root factory, so a lever
        // still reaches the client headless (dropped harmlessly, per WorldClient's own doc comment) instead of never
        // being wired at all.
        services.AddSingleton(implementationFactory: static sp => WorldSessionLevers.Compose(
            settings: sp.GetRequiredService<WorldRenderSettings>(),
            pacing: sp.GetRequiredService<PresentPacingControl>(),
            audio: sp.GetRequiredService<WorldAudioDirector>(),
            bindingBar: sp.GetRequiredService<WorldBindingBarVisibility>()
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
        // because WorldScreenBinder's constructor needs them regardless of boot shape — see below. Puck.World
        // references both Puck.Platform.Windows and Puck.Platform.Linux directly (it stays one universal build, per
        // CLAUDE.md rule 3), so this OperatingSystem.IsWindows() branch is the one composition-time choice between
        // them; it is not a falsifier target itself — that property belongs to Puck.Launcher.Linux/.Headless, which
        // touch neither package.
        if (OperatingSystem.IsWindows()) {
            services.AddWindowsCameraCapture();
        } else {
            services.AddLinuxCameraCapture();
        }

        // The camera-probes host: probe lifecycle, axis-to-command capture, and parameter/control writes — an
        // ISnapshotInputCapture contribution both host loops service once per host frame. Core, not presentation:
        // a track-input probe and its axis bindings need no window, and a camera-input probe headless simply
        // faults by name (no camera GPU tier) while a parameter binding finds no composed pass to write.
        services.AddWorldProbes();

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
            [.. definition.Screens, .. WorldCreationFacets.ReservedFaceSlots(
                    derivedFaceBase: WorldCreationFacets.DerivedFaceBase,
                    derivedFaceScreens: definition.Authoring.DerivedFaceScreens
                )];

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
                // The backend-neutral surface-transfer seam the Vulkan host's camera GPU tier imports its shared
                // targets through. Registered by whichever presenter composes; a headless boot has none (null) and
                // never publishes, so nothing reaches for it.
                surfaceTransfers: sp.GetService<IGpuSurfaceTransferFactory>(),
                cameras: definition.Cameras,
                anchors: sp.GetRequiredService<WorldClient>(),
                stamps: sp.GetRequiredService<WorldStampPool>(),
                // On the D3D12 host the window/monitor capture feeds publish GPU-side into shared textures the
                // screens sample directly; the Vulkan host keeps the CPU-pixel transport for THOSE. The shared
                // camera rides its GPU tier on both hosts (see CaptureCameraGpu). Headless never resolves either
                // backend, so this bool only matters once presentation composes.
                hostsOnDirectX: sp.GetRequiredService<WorldHostSettings>().HostsOnDirectX,
                // A session-sourced face's destination/reference lookup and resolver-owned instance — CORE, not
                // presentation-only, so an observation lease attaches (and a destination instance starts) in every
                // boot shape, exactly like WorldMachineHost's own boot-time machine start.
                instanceHost: sp.GetRequiredService<WorldInstanceHost>()
            );
        });

        // The participant/census verb surface — world.players/.devices/.population. Split out of WorldCommandModule
        // (which stays presentation-only) because these three read pure roster/population/document state.
        services.AddSingleton<ICommandModule, WorldPopulationCommandModule>();
        // The world-mutation verb surface — world.kit.default, world.population.defaults, world.placement.get,
        // world.grant.set/.remove, world.reset/.load/.reload/.undo/.save/.status/.references. A separate
        // module from WorldCommandModule to keep that class under its probe ceilings.
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
        // The addon cost-surface read-back — world.addons. Mounting/unmounting/reloading/enabling/disabling
        // an addon rides world.row.set addons/.remove instead (WorldRowCommandModule), never a verb here.
        services.AddSingleton<ICommandModule, WorldAddonCommandModule>();
        // The local auction house verb surface — market.list/.bid/.buyout/.cancel + world.market.
        services.AddSingleton<ICommandModule, WorldMarketCommandModule>();
        // The contribution-slot read-back verb — world.contributions. Slots themselves are authored and filled
        // through world.row.set placements.
        services.AddSingleton<ICommandModule, WorldContributionCommandModule>();
        // The refusal-catalog read-back verb — world.refusals. No constructor dependency: compiled-in data.
        services.AddSingleton<ICommandModule, WorldRefusalsCommandModule>();
        // The storage verb surface — storage.status/push/pull/credential over the owned-world catalog.
        services.AddSingleton<ICommandModule, WorldStorageCommandModule>();
        // The self-update document section's read-back verb — world.update. Live update.status/.check/.apply verbs
        // (when AddSelfUpdate is registered) are Puck.Launcher's own ICommandModule, not this one.
        services.AddSingleton<ICommandModule, WorldUpdateCommandModule>();
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
            engines: sp.GetServices<IScreenMachineEngine>(),
            addonHostFactory: static (definition, server) => WorldAddonRuntime.Create(
                definition: definition,
                server: server
            )
        ));
        services.AddSingleton<ICommandModule, WorldReplayCommandModule>();

        // The console's sequencing primitive: the tick barrier world.wait arms (published by the shared server-step
        // shell each fixed step) and the verb that arms it. CORE — world.wait is a server-safe verb by name (DELIVER
        // item 3), and a headless script needs the SAME read-after-write fence a windowed one does.
        services.AddSingleton<WorldConsoleWaitGate>();
        services.AddSingleton<ITextCommandHoldGate>(implementationFactory: static sp => sp.GetRequiredService<WorldConsoleWaitGate>());
        services.AddSingleton<IWorldWaitGateResolver, WorldSingleWaitGateResolver>();
        services.AddSingleton<ICommandModule, WorldWaitCommandModule>();
        // Launcher owns the one TextCommandSource and its stdout/stderr + operator-tape result fan-out. World
        // contributes only this wait gate; AddLauncherTerminalShared composes every contributed gate into that
        // source, so adding world.wait cannot sever the launcher's administrative mirror or deferred observers.

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
            continuum: sp.GetRequiredService<WorldContinuum>()
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
        services.AddSingleton<ICommandModule, WorldStateCommandModule>();

        // The `rules` section's READ-BACK — world.rules reads the live compiled set; the rows themselves are
        // authored through world.row.set/world.row.remove rules <json>. CORE for the same reason the state
        // module is: rules are document state.
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => new WorldRulesCommandModule(link: sp.GetRequiredService<IServerLink>()));

        // The generalized property-interaction READ-BACK — world.properties/world.interactions; both sections are
        // authored through world.row.set/world.row.remove over properties.names (a bare name token, the one
        // grammar exception) and interactions.interactions. CORE for the same reason the rules
        // module is: both sections are document state that compile to the SAME rule substrate.
        services.AddSingleton<ICommandModule>(implementationFactory: static sp => new WorldInteractionCommandModule(link: sp.GetRequiredService<IServerLink>()));

        // The transport-neutral local session resolver — WorldInstanceHost's
        // TriggerPortal and WorldPlacementCommandModule's world.destinations read-back both consume it, so it is
        // registered ahead of (and independent from) WorldInstanceHost itself.
        services.AddSingleton<WorldSessionResolver>();

        // The moved host engine's one seam into the desktop's client/roster/seat-router/input-router — see
        // IWorldEmbodiedSeats.
        services.AddSingleton<IWorldEmbodiedSeats>(implementationFactory: static sp => new WorldClientSeats(
            client: sp.GetRequiredService<WorldClient>(),
            roster: sp.GetRequiredService<PlayerRoster>(),
            seatRouter: sp.GetRequiredService<WorldSeatAuthorityRouter>(),
            router: sp.GetRequiredService<Func<InputRouter>>()
        ));

        // The process's running world instances (docs/vision.md's "Multi-world ticking in one process" row):
        // the boot world plus every instance started at runtime through the console, stepped by both boot shapes'
        // IFixedStepSimulation.Step. CORE (not presentation-only): an instance beside the boot world is render-less
        // by construction, so it works identically headless or windowed. The engine is boot-free at construction —
        // this factory admits the desktop's one boot row (AdmitBoot) immediately after building the host, so every
        // other resolver sees a fully admitted registry.
        services.AddSingleton(implementationFactory: static sp => {
            var host = new WorldInstanceHost(
                seats: sp.GetRequiredService<IWorldEmbodiedSeats>(),
                resolver: sp.GetRequiredService<WorldSessionResolver>(),
                machineId: sp.GetRequiredService<WorldOwnedWorlds>().MachineId,
                stateRoot: WorldStateRoot.Resolve(),
                applicationStopping: sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
                admitsSpawn: true
            );
            var bootOrigin = sp.GetRequiredService<WorldDefinitionSource>();
            var bootServer = sp.GetRequiredService<WorldServer>();
            var bootRow = new WorldInstance(
                name: WorldInstanceHost.BootInstanceName,
                origin: () => bootOrigin.SourcePath,
                server: bootServer,
                ownedMachines: null,
                link: sp.GetRequiredService<IServerLink>(),
                federation: new WorldFederationIdentity(
                    Authenticator: sp.GetRequiredService<IAuthenticator>(),
                    Subject: bootServer.AuthorityIdentity
                ),
                documentOrigin: new WorldFileOrigin(resolvedPath: bootOrigin.SourcePath)
            ) {
                Tape = sp.GetRequiredService<WorldReplayTape>(),
            };

            host.AdmitBoot(row: bootRow);

            return host;
        });
        services.AddSingleton<ICommandModule, WorldInstanceCommandModule>();

        // The desktop's IWorldConsoleAuthority — every moved console module resolves its target row through this
        // seam instead of an injected WorldServer singleton (see WorldBootConsoleAuthority's own remarks).
        services.AddSingleton<IWorldConsoleAuthority>(implementationFactory: static sp => new WorldBootConsoleAuthority(instances: sp.GetRequiredService<WorldInstanceHost>()));

        // The adjacency runtime source — CORE (not presentation-only): body contact consumes it regardless of boot
        // shape; the render half additionally consumes it only in the windowed shape.
        // Registered as its own concrete singleton so container disposal releases its held observation leases, and
        // resolved through IWorldAdjacencySource by both consumers so they share the same instance and handle cache.
        services.AddSingleton<WorldAdjacencyFields>(implementationFactory: static sp =>
            new WorldAdjacencyFields(
            instances: sp.GetRequiredService<WorldInstanceHost>(),
            sourceInstanceName: WorldInstanceHost.BootInstanceName
        ));
        services.AddSingleton<IWorldAdjacencySource>(implementationFactory: static sp => sp.GetRequiredService<WorldAdjacencyFields>());
        services.AddSingleton<WorldContinuum>();

        // Per-instance scheduling's own read-back + live pause/resume lever
        // — world.rate. Depends on WorldReplayTape (registered above) to tape a boot-instance pause/resume as an
        // ordered rate-lever event. CORE for the same reason WorldInstanceCommandModule is: an instance beside the
        // boot world is render-less by construction, so it works identically headless or windowed.
        services.AddSingleton<ICommandModule, WorldRateCommandModule>();

        // The binding bar's per-seat authored policy resolver. Core so its read-back remains available headless;
        // presentation only consumes the resolved layout and visibility when it builds a bar frame.
        // The overlay-visibility fact evaluator: every overlay element's authored `visible` predicate reads it. Core
        // (the binding bar's read-back is core); the presentation-only fact owners resolve to null headless.
        services.AddSingleton(implementationFactory: static sp => new WorldOverlayFacts(
            client: sp.GetRequiredService<WorldClient>(),
            roster: sp.GetRequiredService<PlayerRoster>(),
            server: sp.GetRequiredService<WorldServer>(),
            seatBindings: sp.GetRequiredService<WorldSeatBindings>(),
            router: () => sp.GetRequiredService<InputRouter>(),
            wheel: () => sp.GetService<WorldWheelFeed>(),
            pointer: sp.GetService<WorldPointer>(),
            consoles: sp.GetService<IConsoleSessions>()
        ));
        services.AddSingleton<WorldBindingBarControl>();

        // The overlay-UI verb surface — world.screenshot. CORE-registered with an optional renderer so headless and
        // windowed compositions retain one vocabulary; the handler refuses by name when no presentation exists.
        // The seat console is terminal-owned and registered beside quit, outside this world module.
        services.AddSingleton<ICommandModule, WorldUiCommandModule>();

        // The radial action menu's verb surface (player.wheel.ring/.select/.commit/.cancel + world.view.wheel) — see
        // AddWorldPresentation below for WorldWheelFeed/WheelStore, the genuinely presentation-only pointer/viewport
        // state this module reads. CORE-registered for the same command-vocabulary-parity reason as
        // WorldUiCommandModule above: the engine-default document's wheel-hold pages commit RingCommand/CommitCommand
        // on every group (play, editor), so a headless boot must carry the SAME verb NAMES; WorldWheelFeed is
        // OPTIONAL (default null) and every handler refuses BY NAME at use when it is absent.
        services.AddSingleton<ICommandModule, WorldWheelCommandModule>();

        // The window-composition verb surface — view.override camera|layout (the live overrides) and the
        // world.view.state/.pointer reads. The authored rows live under world.row.set/world.row.remove over
        // views.seatRig/views.layouts. CORE-registered for the same command-vocabulary-parity reason as the two
        // modules above: shipped worlds commit view.override on their wheel rings, so a headless boot must carry the
        // same verb name or the document refuses once its vocabulary composes. Composition submission and the
        // composer are both core, so view.override and world.view.state genuinely work headless; WorldCursorFeed is
        // OPTIONAL (default null) and world.view.pointer refuses by name at use when it is absent.
        services.AddSingleton<ICommandModule, WorldViewCommandModule>();

        return services;
    }
    /// <summary>
    /// Layers the GPU host, render root, overlays, audio device, and screens/machines/gamepads over the
    /// authoritative core (the editor verb surface lives in <see cref="AddWorldAuthoritativeCore"/> now — see
    /// its class remarks). Registered only when <c>WorldHostSettings.Headless</c> is
    /// <see langword="false"/> — every genuinely presentation-only console module (graphics options, host/audio
    /// levers, recording) refuses as unknown over stdin when this method never ran; <see cref="WorldUiCommandModule"/>,
    /// <see cref="WorldWheelCommandModule"/>, and <see cref="WorldViewCommandModule"/> stay registered either way and
    /// refuse by name at use instead.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="hostsOnDirectX">Whether the resolved backend is Direct3D 12 (else Vulkan) — needed eagerly (not
    /// deferred to a factory) because the Windows/Linux hosted-presentation registration below branches on it.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddWorldPresentation(this IServiceCollection services, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(argument: services);

        services.AddOptions<NativeWindowOptions>().Configure<WorldHostSettings>(configureOptions: static (options, hostSettings) => {
            options.Height = ((uint)hostSettings.Height);
            // The world draws its own pointer (CursorWriter); the OS cursor stays hidden over the client area.
            options.HideMouseCursor = true;
            options.Mode = NativeWindowMode.PlatformWindow;
            options.StartFullscreen = hostSettings.Fullscreen;
            options.Title = WorldApplicationDefaults.WindowTitle;
            options.Width = ((uint)hostSettings.Width);
        });
        services.AddSingleton(implementationFactory: static sp => new PresentationOptions {
            PresentMode = sp.GetRequiredService<WorldHostSettings>().PresentMode,
            SurfaceFormat = sp.GetRequiredService<WorldHostSettings>().SurfaceFormat,
        });
        // The external-clock election policy from the host section's genlock field. Registered BEFORE
        // AddLauncherTerminal (below) so the launcher's TryAddSingleton<ExternalClockRegistry> defers to this one.
        services.AddSingleton(implementationFactory: static sp => new ExternalClockRegistry(electionPolicy: sp.GetRequiredService<WorldHostSettings>().Genlock));

        // The world speaker device: the hosted service owning the mixer + the WASAPI governor/pump threads. One
        // dedicated bounded-join worker owns the device lifecycle, so a stalled device cannot wedge shutdown; a
        // platform without a render backend has no IAudioRenderDeviceFactory registered (Puck.Platform.Linux
        // registers none) and the service parks as 'unsupported'.
        if (OperatingSystem.IsWindows()) {
            services.AddWindowsAudioRender();
        }
        services.AddSingleton(implementationFactory: static sp => new WorldAudioRenderService(
            director: sp.GetRequiredService<WorldAudioDirector>(),
            factory: sp.GetService<IAudioRenderDeviceFactory>()
        ));
        services.AddHostedService(implementationFactory: static sp => sp.GetRequiredService<WorldAudioRenderService>());

        // The world's own presentation verb surface — world.fps/.gpu, world.screens/.cameras, and the graphics
        // options (shadows, ambient occlusion, render scale, an FPS target, a quality preset). Refuses as unknown
        // over headless stdin because this whole method never runs there.
        services.AddSingleton<ICommandModule, WorldCommandModule>();
        // The host-section READ-BACK — world.host, the DOCUMENT/RESOLVED/LIVE three-way read; the section is
        // written through world.row.set host <json>. Presentation-only: window/backend/present/pacing/GPU-timing
        // knobs.
        services.AddSingleton<ICommandModule, WorldHostCommandModule>();
        // The audio READ-BACK + lever surface — world.speakers/audio.state/speaker.state/world.volume/
        // audio.emitters. The rows are written through world.row.set/world.row.remove over
        // speakers/tunes/patches/audio. Presentation-only: injects the audio device render service directly.
        services.AddSingleton<ICommandModule, WorldAudioCommandModule>();
        // The recording graph (puck.recording.v1) — native capture for streaming/upload workflows. Presentation-only:
        // AddWindowsRecordingPlatform/AddLinuxRecordingPlatform register the encoder ladder, audio-source factory,
        // and shared session clock. The launcher drives the generic FrameCaptureController with the exact root
        // surface immediately before presentation; the world command module owns the concrete recording session it
        // arms here.
        if (OperatingSystem.IsWindows()) {
            services.AddWindowsRecordingPlatform();
        } else {
            services.AddLinuxRecordingPlatform();
        }
        services.AddSingleton<ICommandModule, WorldRecordingCommandModule>();

        // Controllers, first-class beside the keyboard: the hardware manager (HID + the Xbox XInput/GameInput poll
        // thread), the focus-gated snapshot capture binding the sticks to the player's Axis2D channels, and the
        // hosted service that governs device lifetime (hotplug rescans every ~1.5 s). Presentation-only — a headless
        // host has no window to give input focus to, so nothing here would ever fire.
        if (OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )) {
            services.AddSingleton(implementationFactory: static sp => WindowsInputTransports.CreateGamepadManager(
                clock: sp.GetRequiredService<IInputClock>(),
                diagnostics: static message => Console.Error.WriteLine(value: message)
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

        // The terminal's four seat-authenticated console sessions. The bank owns independent tape/editor/history/
        // text ingress per seat; the overlay still reads seat 1's store until presentation grows per-viewport panels.
        services.AddSingleton(implementationFactory: static sp => new ConsoleSessionBank(
            seatCount: PlayerRoster.MaxSlots,
            source: sp.GetRequiredService<TextCommandSource>(),
            router: sp.GetRequiredService<InputRouter>(),
            slotResolver: sp.GetRequiredService<IInputSlotResolver>(),
            clipboard: sp.GetRequiredService<IClipboardService>(),
            focus: sp.GetRequiredService<IInputFocus>(),
            terminalSessions: sp.GetRequiredService<TerminalConsoleSessions>()
        ));
        services.AddSingleton<ConsoleTapeStore>(implementationFactory: static sp => sp.GetRequiredService<ConsoleSessionBank>().StoreFor(slot: 0));
        services.AddSingleton<ICommandObserver>(implementationFactory: static sp => new ConsoleSessionCommandObserver(
            sessions: () => sp.GetRequiredService<ConsoleSessionBank>(),
            terminalSessions: sp.GetRequiredService<TerminalConsoleSessions>()
        ));
        services.AddSingleton<BindingBarStore>();
        services.AddSingleton<MarkerStore>();
        services.AddSingleton<WorldThemeResolve>();
        services.AddSingleton<OverlayToastStore>();
        services.AddSingleton(implementationFactory: static sp => new ConsoleInputSink(
            sessions: sp.GetRequiredService<ConsoleSessionBank>(),
            slotResolver: sp.GetRequiredService<IInputSlotResolver>()
        ));
        services.AddSingleton<IWindowInputObserver>(implementationFactory: static sp => sp.GetRequiredService<ConsoleInputSink>());

        // The pointer: ONE store of live browsing state (cursor position, drainable motion and wheel, held
        // buttons — per seat), ONE IWindowInputObserver that writes it, and any number of consumers that read it.
        // Presentation-only (nothing here rides a CommandSnapshot), so it lives in this method rather than the
        // authoritative-core one — a headless boot never sees a pointer to observe. A new pointer-driven feature
        // registers an IWorldPointerConsumer below; it does NOT add a second window-input observer.
        services.AddSingleton<WorldPointer>();

        // The local mouse seat's right-drag camera orbit (WoW-style): the shared yaw/pitch state WorldFramePresenter
        // composes onto the slot-0 chase camera anchor, and the pointer consumer that nudges it while the authored
        // arming button is held.
        services.AddSingleton<WorldSeatViewInput>();
        services.AddSingleton<IWorldPointerConsumer>(implementationFactory: static sp => sp.GetRequiredService<WorldSeatViewInput>());

        services.AddSingleton<WorldPointerSink>();
        services.AddSingleton<IWindowInputObserver>(implementationFactory: static sp => sp.GetRequiredService<WorldPointerSink>());

        // The drawn cursor: the per-seat viewport+camera publication the frame source fills each dressed frame, the
        // per-frame feed that reads the pointer store NON-destructively (position + held buttons; the drained
        // motion/wheel accumulators stay WorldSeatViewInput's), and the store the unified overlay's cursor writer
        // renders. All presentation/session state — nothing here reaches a CommandSnapshot or the simulation.
        services.AddSingleton<WorldSeatViewports>();
        services.AddSingleton<CursorStore>();
        services.AddSingleton(implementationFactory: static sp => new WorldCursorFeed(
            pointer: sp.GetRequiredService<WorldPointer>(),
            roster: sp.GetRequiredService<PlayerRoster>(),
            client: sp.GetRequiredService<WorldClient>(),
            viewInput: sp.GetRequiredService<WorldSeatViewInput>(),
            viewports: sp.GetRequiredService<WorldSeatViewports>(),
            hud: sp.GetRequiredService<HudStore>(),
            store: sp.GetRequiredService<CursorStore>(),
            facts: sp.GetRequiredService<WorldOverlayFacts>()
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
        // WorldWheelCommandModule (the verb surface reading this feed) is CORE-registered — see
        // AddWorldAuthoritativeCore's tail — because the engine-default document commits its verb NAMES in every
        // boot shape; it resolves this feed as an OPTIONAL dependency and refuses by name headless.

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
            store: sp.GetRequiredService<HudStore>(),
            facts: sp.GetRequiredService<WorldOverlayFacts>()
        ));

        // The boot document's resolved icon table: the ONE place an icon name, a font id, or a codepoint is known
        // (see WorldIconTable's own remarks) — shared by the binding-bar feed below and the glyph-atlas bake in the
        // render root further down, so both read the SAME codepoint ordering.
        services.AddSingleton(implementationFactory: static sp => new WorldIconTable(definition: sp.GetRequiredService<WorldDefinition>()));

        services.AddSingleton(implementationFactory: static sp => new WorldOverlayFeed(
            bindingBar: sp.GetRequiredService<WorldBindingBarControl>(),
            bindings: sp.GetRequiredService<WorldSeatBindings>(),
            client: sp.GetRequiredService<WorldClient>(),
            gamepads: sp.GetService<GamepadManager>(),
            icons: sp.GetRequiredService<WorldIconTable>(),
            roster: sp.GetRequiredService<PlayerRoster>(),
            router: sp.GetRequiredService<InputRouter>(),
            store: sp.GetRequiredService<BindingBarStore>()
        ));
        // WorldUiCommandModule (world.screenshot) is CORE-registered — see AddWorldAuthoritativeCore's tail — and
        // refuses by name headless. The console verb belongs to the terminal composition.

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
        //
        // hostsOnDirectX can only be true on Windows (WorldHostSettings.Resolve gates DirectX on
        // OperatingSystem.IsWindowsVersionAtLeast before this method ever runs), so the OS branch below and the
        // caller's backend choice never disagree.
        services.AddLauncherTerminal();
        if (OperatingSystem.IsWindows()) {
            services.AddWindowsHostedPresentation(hostsOnDirectX: hostsOnDirectX);
        } else {
            services.AddLinuxHostedPresentation();
        }
        services.AddBackendSwitcher(preferredBackend: (hostsOnDirectX
            ? "directx"
            : "vulkan"));

        // The composed frame source, registered on its own rather than built inside the render-root factory below:
        // it touches no GPU, and its constructor runs the ONE capacity probe, so the boot can resolve it before any
        // hosted service starts (WorldPostBuildWiring) and report an over-envelope world as an ordinary named boot
        // refusal instead of an unhandled service-factory exception mid-startup.
        services.AddSingleton(implementationFactory: sp => new WorldFramePresenter(
            frameRate: sp.GetRequiredService<FrameRateMonitor>(),
            client: sp.GetRequiredService<WorldClient>(),
            simulation: sp.GetRequiredService<WorldSimulation>(),
            settings: sp.GetRequiredService<WorldRenderSettings>(),
            binder: sp.GetRequiredService<WorldScreenBinder>(),
            envelope: sp.GetRequiredService<WorldRenderEnvelope>(),
            seatBindings: sp.GetRequiredService<WorldSeatBindings>(),
            animator: sp.GetRequiredService<WorldStampPool>(),
            audio: sp.GetRequiredService<WorldAudioDirector>(),
            anchor: sp.GetRequiredService<WorldPerceptionAnchor>(),
            composition: sp.GetRequiredService<WorldCompositionState>(),
            composer: sp.GetRequiredService<WorldViewComposer>(),
            sdfDocuments: sp.GetRequiredService<WorldSdfDocumentEmitter>(),
            viewports: sp.GetRequiredService<WorldSeatViewports>(),
            continuum: sp.GetRequiredService<WorldContinuum>(),
            text: sp.GetRequiredService<WorldTextCatalog>(),
            adjacencies: sp.GetRequiredService<IWorldAdjacencySource>(),
            markers: sp.GetRequiredService<MarkerStore>(),
            resolveIcon: sp.GetRequiredService<WorldIconTable>().ResolveIcon
        ));

        // The render root: the shared SDF world assembly over the grass-and-boulders scene. The built Producer (the
        // live SdfEngineNode) is stashed on the WorldRenderProbe so the world.gpu verb can read its per-pass GPU
        // times. The frame source emits active avatars only (declared-but-parked instances widen the per-pixel
        // shadow mask walk), so the local 128-avatar worst case is held by the capacity floors a construction-time
        // probe measured, plus the viewport floor for the join-later split screen. The affordance install, EchoTap,
        // MachineLifecycleTap, and lever-sink attachment live in the shared post-build wiring step
        // (WorldPostBuildWiring) — this factory only builds the render tree.
        services.AddSingleton<IRenderNode>(implementationFactory: sp => {
            var hostSettings = sp.GetRequiredService<WorldHostSettings>();
            var width = ((uint)hostSettings.Width);
            var height = ((uint)hostSettings.Height);
            var binder = sp.GetRequiredService<WorldScreenBinder>();

            // The view-composition GPU-services bundle: resolved once, eagerly, right here at the composition
            // root, then forwarded unchanged through ConfigureViews and Build to every late-construction site
            // (the binder's stashed camera-view factory, and SdfEngineNode itself) — never a retained
            // IServiceProvider re-resolved from later.
            var viewGpuServices = new SdfViewGpuServices(
                Gpu: sp.GetRequiredService<IGpuComputeServices>(),
                TimingFactory: (sp.GetService(serviceType: typeof(IGpuTimingPoolFactory)) as IGpuTimingPoolFactory),
                TimingRecorder: (sp.GetService(serviceType: typeof(IGpuTimingRecorder)) as IGpuTimingRecorder)
            );

            var frameSource = sp.GetRequiredService<WorldFramePresenter>();

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
                    Height: height,
                    Width: width
                ) {
                    // The post-render extension chain composes FIRST, over the bare SDF producer — before the
                    // unified overlay wraps it and before the glyph-atlas early return below, so a missing atlas
                    // never silently drops an authored extension (world content gets the extension's effect; HUD/
                    // console text drawn by the overlay stays on top of it, unaffected). An absent or empty
                    // render.extensions list composes zero extensions, so `composed` is `producer` unchanged — the
                    // byte-identical default path. WorldDefinitionValidator already refused an unshipped id at
                    // document load against the same catalog, so a lookup miss here means the deploy changed
                    // under the process, not that the document is bad.
                    //
                    // The unified overlay (console mirror + per-seat binding bars + toasts) wraps the (possibly
                    // extension-composed) producer on BOTH backends: neutral services, bytecode selected by the
                    // resolved host. Degrades loudly to the bare (extension-composed) world when the pre-baked
                    // glyph atlas is missing.
                    Decorate = producer => {
                        IRenderNode composed = producer;
                        var renderExtensions = sp.GetRequiredService<WorldDefinition>().Render.Extensions;

                        if (renderExtensions is { Count: > 0 }) {
                            var postRenderServices = WorldPostRenderExtensionServices.Build(serviceProvider: sp);

                            foreach (var entry in renderExtensions) {
                                var manifest = WorldPostRenderExtensions.Shipped.Load(id: entry.Id);

                                if (!manifest.TryBindConfig(
                                    config: entry.Config,
                                    values: out var config,
                                    reason: out var reason
                                )) {
                                    throw new InvalidOperationException(message: $"render.extensions '{entry.Id}' config is invalid: {reason}");
                                }

                                composed = new FullscreenPassNode(
                                    config: config,
                                    height: height,
                                    hostsOnDirectX: hostSettings.HostsOnDirectX,
                                    inner: composed,
                                    manifest: manifest,
                                    services: postRenderServices,
                                    width: width
                                );
                                sp.GetRequiredService<WorldPostRenderExtensionPasses>().Add(id: entry.Id, pass: (FullscreenPassNode)composed);
                            }
                        }

                        var fontsDirectory = Path.Combine(
                            path1: AppContext.BaseDirectory,
                            path2: "Assets",
                            path3: "Fonts"
                        );
                        var icons = sp.GetRequiredService<WorldIconTable>();
                        // The prepacked-artifact path: a warm start against the SAME icon repertoire reads the
                        // finished pack beside the atlas; only a cold/rebaked/repertoire-changed start decodes the
                        // combined PNG (and persists the pack for the next boot) — see WorldIconTable's remarks.
                        var glyphs = new OverlayGlyphAtlasSet(fontsDirectory: fontsDirectory).LoadOverlayPack(extraCodePoints: icons.ExtraCodePoints);

                        if (glyphs is null) {
                            Console.Error.WriteLine(value: $"[unified-overlay] skipped: no usable glyph atlas under '{fontsDirectory}' (restore the committed fixed-UI assets).");

                            return composed;
                        }

                        var bytecodeExtension = SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: hostSettings.HostsOnDirectX);

                        var themeResolve = sp.GetRequiredService<WorldThemeResolve>();
                        var bootDefinition = sp.GetRequiredService<WorldDefinition>();
                        var bootTheme = themeResolve.Resolve(
                            definition: bootDefinition,
                            revision: sp.GetRequiredService<WorldClient>().DefinitionRevision,
                            tick: sp.GetRequiredService<WorldClient>().Tick
                        );

                        return overlayNode = new UnifiedOverlayNode(
                            // The seat count and HUD/marker ceilings cross from Schema to Overlays here, as data.
                            capacity: WorldOverlayCapacity.FromSchema(),
                            fragmentBytecode: File.ReadAllBytes(path: Path.Combine(
                                path1: AppContext.BaseDirectory,
                                path2: "Assets",
                                path3: "Shaders",
                                path4: $"overlay-unified.frag{bytecodeExtension}"
                            )),
                            glyphs: glyphs,
                            height: height,
                            inner: composed,
                            services: OverlayServices.Build(
                                hostsOnDirectX: hostSettings.HostsOnDirectX,
                                serviceProvider: sp
                            ),
                            sources: new UnifiedOverlaySources(
                                BindingBar: sp.GetRequiredService<BindingBarStore>(),
                                Console: sp.GetRequiredService<ConsoleTapeStore>(),
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
                                    // The radial menu orders against the cursor feed the same way: its hub anchor
                                    // and hover derive from the status the feed just published.
                                    sp.GetRequiredService<WorldWheelFeed>().Tick();
                                    // Live retheme: the theme resolve is revision-gated (a no-op most frames), but
                                    // republishing the store + re-filling the GPU token slab happens every frame the
                                    // resolve produced a fresh value — cheap, and the only way a state.<row> bind
                                    // reaches pixels the next produced frame after the write lands.
                                    var client = sp.GetRequiredService<WorldClient>();

                                    overlayNode?.UpdateTheme(theme: sp.GetRequiredService<WorldThemeResolve>().Resolve(
                                        definition: client.Definition,
                                        revision: client.DefinitionRevision,
                                        tick: client.Tick
                                    ));
                                },
                                Markers: sp.GetRequiredService<MarkerStore>(),
                                Toast: sp.GetRequiredService<OverlayToastStore>(),
                                Hud: sp.GetRequiredService<HudStore>(),
                                HudBindings: sp.GetRequiredService<IHudBindingResolver>(),
                                Cursor: sp.GetRequiredService<CursorStore>(),
                                Wheel: sp.GetRequiredService<WheelStore>()
                            ),
                            theme: bootTheme,
                            vertexBytecode: File.ReadAllBytes(path: Path.Combine(
                                path1: SdfWorldKernels.DefaultDirectory,
                                path2: $"fullscreen.vert{bytecodeExtension}"
                            )),
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
                    ScreenSourceFrames = binder.ScreenSources,
                    ViewportCapacity = PlayerRoster.MaxSlots,
                }
            );

            var probe = sp.GetRequiredService<WorldRenderProbe>();

            probe.Node = render.Producer;
            // world.screenshot arms captures through the render host (routes to the outermost decorator).
            probe.Render = render;
            probe.Overlay = overlayNode;

            // The teardown tie: the window loop disposes this root (device alive) before the presenter and long
            // before the container's reverse-creation-order sweep — ride that safe point for the binder's own GPU
            // holdings (camera feeds, jumbotron view engines), whose container-ordered disposal would otherwise land
            // after device death.
            return new WorldRenderTeardown(
                inner: render.Root,
                binder
            );
        });

        return services;
    }
}
