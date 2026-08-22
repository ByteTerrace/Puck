using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Commands;
using Puck.Launcher;
using Puck.Overlays;
using Puck.World.Addons;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The post-build wiring step every boot shape runs: the affordance vocabulary install, the boot document's genuine
/// binding-vocabulary re-validation (see the remarks on <see cref="Install"/>), the accepted-session-lever
/// attachment, the outstanding-capture drain (see the end of <see cref="Install"/>), and the server's
/// <see cref="WorldServer.EchoTap"/>/<see cref="WorldServer.SaveEffectTap"/>/
/// <see cref="WorldMachineHost.MachineLifecycleTap"/> closures — moved out of the old presentation-only render-root
/// factory so <c>wire.errors</c> stays honest headless (a deferred Simulation-routed refusal is counted regardless of
/// boot shape). Called once from <c>Program.cs</c> right after <c>IHost.Build()</c>, for both boot shapes. The
/// toast/HUD-structure/audio-cue-listener-placement-lookup halves that only make sense with a renderer resolve their
/// presentation services optionally (<see cref="IServiceProvider.GetService"/>, never <c>GetRequiredService</c>) and
/// no-op when absent.
/// </summary>
internal static class WorldPostBuildWiring {
    /// <summary>Installs the affordance vocabulary, re-validates the boot document's binding vocabulary now that the
    /// vocabulary is real (see the remarks below), attaches the session-lever sink, wires the server's echo/cue taps,
    /// and registers the shutdown drain that reports an armed capture no frame ever served. Safe to call exactly
    /// once, after the container has built but before the host starts.</summary>
    /// <remarks>
    /// Boot validation stops being vacuous here. <c>WorldDefinitionLoader.TryResolve</c> (in <c>Program.cs</c>) loads
    /// and validates the boot document before the DI container exists — <c>WorldDefinitionValidator.Validate</c> runs
    /// <c>BindingVocabularyHook.VocabularyCheck</c> over every binding overlay and the compiled-in engine-default
    /// wheels/pages at that instant, but <see cref="WorldAffordances.Installed"/> is still <see langword="false"/>
    /// then (it flips a few lines below, in this method — the first point on the boot path where a built
    /// <see cref="CommandRegistry"/> exists), so the command half of that check is a documented no-op
    /// (<see cref="WorldAffordances.Validate"/>'s own remarks: "Absent means the command half of validation is
    /// skipped — structural validation still runs — never that it passed"). Nothing before that instant ever branches
    /// on <c>WorldHostSettings.Headless</c>, so the gap is not a headless-only bug: every boot shape validates its own
    /// wheel/page commits vacuously at load, and only happens to get away with it windowed because
    /// <see cref="WorldBootComposition.AddWorldPresentation"/> composes a superset registry before anything asks again. Re-running
    /// <see cref="WorldDefinitionValidator.TryValidate"/> on the SAME (already-loaded, already-resolved) boot
    /// definition here — immediately after <see cref="WorldAffordances.Install"/> — makes the check genuine: an
    /// unregistered wheel or page commit now refuses BOOT by name, identically in both shapes, instead of surfacing
    /// only later at a <c>world.instance.start</c> crossing or a live <c>player.bind</c> recompose.
    /// </remarks>
    /// <param name="services">The built root service provider.</param>
    /// <returns><see langword="true"/> when the boot may proceed; <see langword="false"/> when the re-validated boot
    /// document refused (a reason is already printed to stderr) and the caller must fail the boot instead of calling
    /// <c>IHost.RunAsync</c>.</returns>
    public static bool Install(IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        // The addon runtime resolves lazily as a DI singleton (WorldBootComposition), and WorldAddonCommandModule —
        // one of the modules CommandRegistry aggregates below — takes it as a constructor dependency, so resolving
        // CommandRegistry first would transitively construct it INSIDE that call, with no narrow catch around it.
        // Resolving it explicitly here first gives it its own catch, matching every sibling boot gate's
        // false + printed-reason shape; the transitive resolution CommandRegistry triggers moments later just
        // returns this same cached singleton.
        try {
            _ = services.GetRequiredService<WorldAddonRuntime>();
        } catch (WorldAddonInstallRefusedException refusal) {
            Console.Error.WriteLine(value: $"[world] definition refused: {refusal.Message}");

            return false;
        }

        var consoleRegistry = services.GetRequiredService<CommandRegistry>();

        // The TCP socket door: bound ONLY when host.listen/--listen names an endpoint — a world with no Listen field
        // never opens a socket, exactly like a headless flag never opening a window. Started here (not a factory)
        // so it observes the fully-built container's WorldHostSettings singleton.
        if (services.GetRequiredService<WorldHostSettings>().Listen is { } listen) {
            services.GetRequiredService<WorldTcpHost>().Start(listen: listen);
        }

        // The affordance vocabulary goes live here — the first post-container point on the boot path where the built
        // registry exists (whichever verbs THIS boot shape actually composed). From now on every binding door
        // (player.bind, recomposes, the document validators) refuses a command name the registry does not carry; the
        // sweep re-covers the layers that composed BEFORE this instant (the engine default and the world's boot
        // overlays), so a dead reference in them prints loudly at boot instead of resolving to a silent dead key.
        // Commands only: a channel table belongs to the document that declares it, so every caller supplies its own
        // (WorldSeatBindings holds the boot instance's; the document validator compiles the candidate's).
        WorldAffordances.Install(registry: consoleRegistry);

        var seatBindings = services.GetRequiredService<WorldSeatBindings>();

        seatBindings.ValidateAffordancesLoudly();

        // THE CROSSING-PARITY BINDING SWAP: wired here rather than a presentation-only composition method because
        // bindings/channels resolve on the console/peer input path in EVERY boot shape, headless included, and
        // headless and presented shapes share the input lifecycle, while this event still applies the route edge
        // immediately. The instant WorldSeatAuthorityRouter publishes a changed claim,
        // changed (a crossing in or out — see that event's own remarks), recompose that ONE seat's binding pages,
        // wheels, and channel vocabulary from its NEW route's own document (WorldInstanceHost.ResolveRoutedDefinition
        // — the identical routed-definition lookup WorldSeatViewInput already subscribes to this same event for,
        // to reclamp the pitch instead). WorldSimulation's own per-tick SyncSeat loop (windowed only) would reach the
        // SAME state one poll later in the ordinary case — this is the explicit, shape-independent edge, not a
        // parallel mechanism.
        var seatRouter = services.GetRequiredService<WorldSeatAuthorityRouter>();

        seatRouter.RouteChanged += slot => {
            if (seatRouter.TryRoute(slot: slot) is { } route) {
                seatBindings.SyncSeat(
                    slot: slot,
                    definition: route.Endpoint.Definition,
                    entityIndex: route.EntityIndex,
                    nextInputTick: route.Endpoint.NextInputTick
                );
            }
        };

        // THE CAMERA-APPLICATION TEARDOWN SEAM: a world load/reload/reset (or a crossing) reseeds a seat's authored
        // mode families to their defaults, dropping the camera-targeting state a live camera application was composed
        // from — but WorldSeatBindings owns the published state, not the possession route, so the two halves meet
        // here, where both resolve in EVERY boot shape. Disengaging through the SAME exit player.mode takes releases
        // the seat's possession route, so a reseed can never leave a body idled under a route no mode state is asking
        // for. Stamped with the seat's own acting principal: the restore targets that seat's own body, which is
        // exactly the authority PrincipalOf reports. Unconditional — Disengage on an already-clear route is the
        // ordinary NotEngaged no-op.
        var cameraRoster = services.GetRequiredService<PlayerRoster>();
        var cameraLink = services.GetRequiredService<IServerLink>();

        seatBindings.CameraApplicationDropped += slot => WorldCameraApplication.Deactivate(
            actingPrincipal: cameraRoster.PrincipalOf(slot: slot),
            link: cameraLink,
            slot: slot
        );

        // The genuine boot-document re-validation (see this method's remarks): the FIRST validation, at
        // WorldDefinitionLoader.TryResolve, ran before WorldAffordances.Installed — its command half was a no-op in
        // EVERY boot shape. This is the first point where re-running the SAME validator asks a real question, so an
        // unregistered wheel/page commit fails the BOOT here (both shapes alike) rather than only a later crossing.
        var worldSource = services.GetRequiredService<WorldDefinitionSource>();

        // The adjacency proof's neighbour resolver, composed from every transport this boot can reach: a
        // file-backed read beside the currently-loaded document (WorldFileNeighbourResolver — the ONLY resolver a
        // local-only boot, no --storage-uri, ever has, which is exactly the quilt worlds' shape) tried first, then
        // the cloud-backed WorldStorageNeighbourResolver when cloud storage is wired. The file resolver reads
        // worldSource.SourcePath fresh on every call rather than capturing the boot directory once, so it keeps
        // resolving correctly across a live world.load/reload that moves the tracked origin (see
        // WorldDefinitionSource.SourcePath's own remarks). WorldCompositeNeighbourResolver.Compose returns null only
        // when NEITHER transport is present, in which case an authored adjacency refuses by
        // name rather than passing unproven — unreachable, not this method's own choice.
        var fileNeighbours = new WorldFileNeighbourResolver(baseDirectory: () => ((Path.GetDirectoryName(path: worldSource.SourcePath) is { Length: > 0 } directory)
            ? directory
            : AppContext.BaseDirectory));
        var storageNeighbours = services.GetRequiredService<WorldStorageSyncHandle>().Neighbours;
        var neighbours = WorldCompositeNeighbourResolver.Compose(
            fileNeighbours,
            storageNeighbours
        );

        if (!WorldDefinitionValidator.TryValidate(
            definition: worldSource.Definition,
            reason: out var vocabularyReason,
            neighbours: neighbours
        )) {
            Console.Error.WriteLine(value: $"[world] definition refused once its command vocabulary composed: {vocabularyReason}");

            return false;
        }

        // The running server also carries the resolver, for the ONE live document-swap moment (world.load/reload/
        // reset) that gets it — see WorldServer.Neighbours' own remarks on why nothing else does.
        var server = services.GetRequiredService<WorldServer>();

        server.Neighbours = neighbours;
        server.RebuildNeighbours = candidatePath => WorldCompositeNeighbourResolver.Compose(
            new WorldFileNeighbourResolver(baseDirectory: () => ((Path.GetDirectoryName(path: candidatePath) is { Length: > 0 } directory)
            ? directory
            : AppContext.BaseDirectory)),
            storageNeighbours
        );

        // The boot authority's runtime adjacency source — unlike Neighbours (a load-time proof), this is
        // consulted every tick a body stands inside a derived overlap. Spawned authorities get
        // their own instance-bound sibling in WorldInstanceHost.TryStart. CORE (both boot shapes): contact resolution
        // needs it regardless of whether a window exists.
        server.Adjacencies = services.GetRequiredService<IWorldAdjacencySource>();

        // Seed the seats' context-family states off the boot census once, so a read-back that runs before the first
        // simulation tick reports the joined boot seats truthfully rather than the resolver's cold defaults (the
        // per-tick publish in WorldSimulation takes over from the first step).
        WorldSeatContextSync.Publish(
            seatBindings: services.GetRequiredService<WorldSeatBindings>(),
            roster: services.GetRequiredService<PlayerRoster>(),
            grants: services.GetRequiredService<WorldServer>().Grants,
            anchor: services.GetRequiredService<WorldPerceptionAnchor>(),
            activeLayout: services.GetRequiredService<Puck.World.Client.WorldViewComposer>().ActiveLayoutName
        );

        // Close the lever path here, where both halves are resolvable in EVERY shape: an accepted lever reaches the
        // client, which either applies it (presentation composed) or drops it per WorldClient's own documented
        // headless contract.
        services.GetRequiredService<WorldClient>().AttachSessionLevers(levers: services.GetRequiredService<WorldSessionLeverSink>());


        // The echo fan-out's halves — resolved ONCE so the tap closure below never queries the container per-echo.
        // toasts are presentation-only (AddWorldPresentation registers it); the stable
        // terminal-session proxy exists in both shapes and mirrors edit outcomes when a windowed bank is attached.
        var toasts = services.GetService<OverlayToastStore>();
        var consoleSessions = services.GetRequiredService<TerminalConsoleSessions>();
        var audioDirector = services.GetRequiredService<WorldAudioDirector>();
        var definitionSource = services.GetRequiredService<WorldDefinitionSource>();

        services.GetRequiredService<WorldServer>().EchoTap = echo => {
            // world.load/world.reload move what the console considers "the current origin" — but only once the
            // SERVER's own echo confirms the rebuild actually applied (this tap fires from the tick boundary, after
            // every gate — authority, dirty-guard, validation, capacity, solids — has already passed), never eagerly
            // at submit time, when the rebuild might still be refused. world.reset never reaches here with a
            // RebuildOrigin (it targets the base without moving it), so SourcePath is correctly left untouched.
            if (
                !echo.Rejected &&
                (echo.Kind == WorldEditEchoKind.Rebuild) &&
                (echo.RebuildOrigin is { } origin)
            ) {
                definitionSource.SourcePath = origin;
            }

            // toast/HUD narration is presentation-only; a headless boot simply has nowhere to paint it.
            toasts?.Publish(
                message: echo.Message,
                isError: echo.Rejected
            );
            // The chip wraps but is still bounded; the panel row is the FULL text (up to its 120-column width), so a
            // capacity reason too long for the toast stays readable where the operator is already looking.
            consoleSessions.RecordAdministrativeEcho(
                message: echo.Message,
                refused: echo.Rejected
            );

            // A world edit is Simulation-routed: the SUBMIT succeeded (the line entered the tick queue) and the
            // server refuses it a tick later, so the registry's own dispatch accounting cannot see it. This tap is
            // the one place both halves meet — count the deferred refusal here so `wire.errors` reports it exactly
            // like a synchronous one, IN EVERY BOOT SHAPE. No double count: a line refused synchronously never
            // reaches the server and so never echoes.
            if (echo.Rejected) {
                consoleRegistry.NoteDeferredRejection();
            }

            // THE EDIT-ECHO CUE LANE: the same outcome fires its cue token — capability
            // denials as grant.denied, other rejections as mutation.rejected, applied edits as mutation.applied AT
            // the changed row's authored position where the mutation payload carries one. The audio director is
            // CORE, so this runs unconditionally; a headless boot's cues simply accumulate in a queue no device pump
            // ever drains (WorldAudioRenderService is presentation-only).
            if (echo.Denied) {
                audioDirector.SubmitCue(
                    eventToken: WorldAudioCue.GrantDenied,
                    site: null
                );
            } else if (echo.Kind != WorldEditEchoKind.GrantTable) {
                audioDirector.SubmitCue(
                    eventToken: (echo.Rejected
                    ? WorldAudioCue.MutationRejected
                    : WorldAudioCue.MutationApplied),
                    site: WorldAudioDirector.MutationSite(mutation: echo.Mutation)
                );
            }
        };

        // THE SAVE-EFFECT TAP: a world rule's 'save' effect performs engine I/O directly rather than composing a
        // WorldMutation (see ActionEffect.Save's remarks for why), so WorldServer cannot run it through the ordinary
        // mutation pipeline — and cannot run the CAPTURE itself either: Puck.World.Server references no rendering or
        // input, and WorldSessionCapture.Capture (the world.save fold) needs the live render levers, screen binder,
        // audio director, and pacing control, all composition-root state. This closure runs the IDENTICAL fold
        // WorldMutationCommandModule's own 'world.save' verb runs, to the world's own loaded file (never an authored
        // path — see the effect's remarks on why), and compacts the journal on success exactly like a manual save.
        // A write failure (disk full, the target's directory gone, a read-only file) is caught and narrated on
        // stderr by name; the firing tick is not rolled back, because nothing durable in it depended on the save
        // succeeding.
        var worldServer = services.GetRequiredService<WorldServer>();
        var renderSettings = services.GetRequiredService<WorldRenderSettings>();
        var screenBinder = services.GetRequiredService<WorldScreenBinder>();
        var pacing = services.GetRequiredService<PresentPacingControl>();
        var bindingBarVisibility = services.GetRequiredService<WorldBindingBarVisibility>();

        worldServer.SaveEffectTap = tick => {
            var target = definitionSource.SourcePath;

            try {
                var snapshot = WorldSessionCapture.Capture(
                    definition: worldServer.Definition,
                    render: renderSettings,
                    population: worldServer.Population,
                    binder: screenBinder,
                    audio: audioDirector,
                    bindingBar: bindingBarVisibility,
                    pacing: pacing,
                    tick: tick
                );
                var bytes = WorldDefinitionSerialization.SavePreservingBasis(
                    basisPath: out var basisPath,
                    definition: snapshot,
                    note: out var note,
                    path: target
                );

                worldServer.Compact();

                var derivation = ((basisPath is { })
                    ? $", basis: {basisPath}"
                    : ((note.Length > 0)
                        ? $", {note}"
                        : ""
                ));

                Console.Error.WriteLine(value: $"[world.rule: save effect -> {target} ({bytes} bytes{derivation})]");
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)) {
                Console.Error.WriteLine(value: $"[world.rule: save effect refused — could not write {target} ({exception.Message.ReplaceLineEndings(replacementText: " ")})]");
            }
        };

        // THE MACHINE LIFECYCLE CUE LANE: machine boot/fault outcomes fire screen.boot / screen.fault at the screen
        // row's authored face origin. CORE: Server.WorldMachineHost and WorldClient are both core-registered, so
        // this runs unconditionally in EVERY boot shape — a headless boot's screens boot (and step) real machines,
        // so this cue lane is presentation-only only in WHO fires it (the audio director's cue queue accumulates
        // harmlessly with no device pump draining it headless, same as every other cue here).
        var audioCueClient = services.GetRequiredService<WorldClient>();

        services.GetRequiredService<WorldMachineHost>().MachineLifecycleTap = (index, faulted) => {
            Vector3? site = null;

            foreach (var screen in audioCueClient.Definition.Screens) {
                if (screen.Index == index) {
                    site = screen.Origin;

                    break;
                }
            }

            audioDirector.SubmitCue(
                eventToken: (faulted
                ? WorldAudioCue.ScreenFault
                : WorldAudioCue.ScreenBoot),
                site: site
            );
        };

        // THE CAPTURE-REQUEST DRAIN: world.screenshot arms a readback of the NEXT composed frame, so a run that ends
        // before that frame writes nothing at all. Left alone, the caller's only evidence is the arming echo, which
        // is indistinguishable from a capture that succeeded — the silent-success shape this repository has already
        // been bitten by. Say it out loud instead, at ApplicationStopped (every hosted service has stopped, so the
        // render loop is provably finished and an outstanding request provably never will be served). Presentation-
        // only: a headless boot has no render probe and world.screenshot refuses there anyway.
        if (services.GetService<WorldRenderProbe>() is { } renderProbe) {
            services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(callback: () => {
                if (renderProbe.Render?.PendingCapturePath is { } pending) {
                    Console.Error.WriteLine(value: $"[world.screenshot] WARNING: a capture of {pending} was still pending when the run ended — no frame composed after it was armed, so NO FILE WAS WRITTEN.");
                }
            });
        }

        // THE RENDER-CAPACITY PRE-FLIGHT. The composed scene's construction-time probe is the first and only point
        // where the WHOLE worst case exists — the boot document's own rows, the avatar catalog, and one reservation
        // per adjacency band — and it is pure CPU, so it runs here, before any hosted service starts. A world whose
        // composed scene cannot fit an engine ceiling refuses BY NAME with the same shape every other refused boot
        // document takes, instead of tearing the host down from inside a service factory mid-startup. Presentation-
        // only: a headless boot composes no frame source and this resolves to null.
        try {
            if (services.GetService<WorldFramePresenter>() is { } composed) {
                // The probed envelope's own read-back: the frozen ceilings every live rebuild fits inside, stated
                // once at boot beside the other origin lines, so the headroom a world is running on is observable
                // rather than inferred from whether it crashed.
                Console.Error.WriteLine(value: $"[world.render] envelope: {composed.InstanceCapacity} instances, {composed.ProgramWordCapacity} program words, {composed.DynamicTransformCapacity} dynamic slots");
            }
        } catch (WorldRenderCapacityRefusedException refusal) {
            Console.Error.WriteLine(value: $"[world] definition refused: {refusal.Message}");

            return false;
        }

        return true;
    }
}
