using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.Launcher;
using Puck.Overlays;
using Puck.World.Addons;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Machines;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The post-build wiring step every boot shape runs: the affordance vocabulary install, the boot document's genuine
/// binding-vocabulary re-validation (see the remarks on <see cref="Install"/>), the accepted-session-lever
/// attachment, the outstanding-capture drain (see the end of <see cref="Install"/>), and the server's
/// <see cref="WorldServer.EchoTap"/>/<see cref="WorldServer.SaveEffectTap"/>/<see cref="WorldServer.MusicTransitionTap"/>/
/// <see cref="WorldServer.MusicLayerTap"/>/<see cref="WorldServer.MusicEmbellishmentTap"/>/
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
    /// The loader validates before the DI container exists. At that point the command half of
    /// <c>BindingVocabularyHook.VocabularyCheck</c> is deferred because <see cref="WorldAffordances.Installed"/>
    /// is false. After <see cref="WorldAffordances.Install"/>, <see cref="WorldDefinitionValidator.TryCompleteAdmission"/>
    /// rechecks environment-dependent sections and neighbour claims against the completed host. The loader's
    /// exact document and catalog retain their local proof and compiled programs. A boot override that changed
    /// the document clears its receipt and takes full validation. Both headless and presented boots refuse an
    /// unregistered wheel or page commit here, before the host starts.
    /// </remarks>
    /// <param name="services">The built root service provider.</param>
    /// <returns><see langword="true"/> when the boot may proceed; <see langword="false"/> when the re-validated boot
    /// document refused (a reason is already printed to stderr) and the caller must fail the boot instead of calling
    /// <c>IHost.RunAsync</c>.</returns>
    public static bool Install(IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        var machineCatalog = services.GetRequiredService<WorldMachineCatalog>();
        var machineCatalogFingerprint = WorldBootComposition.MachineCatalogFingerprint(machineCatalog: machineCatalog);

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

        try {
            services.GetRequiredService<WorldServiceExtensions>().Initialize();
        } catch (Exception exception) when ((exception is ArgumentException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or IOException)) {
            Console.Error.WriteLine(value: $"[world.extensions: configuration refused: {exception.Message}]");
            return false;
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

        // The crossing's binding swap, wired here rather than in a presentation-only composition method because
        // bindings and channels resolve on the console and peer input path in every boot shape, headless included.
        // A changed claim recomposes that one seat from its new route's document; WorldSimulation's per-tick
        // PublishSeats (windowed only) reaches the same state one step later. The edge is delivered by WorldHostStep
        // on the pump thread, whichever thread published the claim.
        var seatRouter = services.GetRequiredService<WorldSeatAuthorityRouter>();

        seatBindings.FollowRoutes(
            client: services.GetRequiredService<WorldClient>(),
            routes: seatRouter
        );

        // THE CAMERA-APPLICATION TEARDOWN SEAM: a world load/reload/reset (or a crossing) reseeds a seat's authored
        // mode families to their defaults, dropping the camera-targeting state a live camera application was composed
        // from — but WorldSeatBindings owns the published state, not the possession route, so the two halves meet
        // here, where both resolve in EVERY boot shape. Disengaging through the SAME exit player.mode takes releases
        // the seat's possession route, so a reseed can never leave a body idled under a route no mode state is asking
        // for. Stamped with the seat's own acting principal: the restore targets that seat's own body, which is
        // exactly the authority PrincipalOf reports. Unconditional — Disengage on an already-clear route is the
        // ordinary NotEngaged no-op.
        var cameraRoster = services.GetRequiredService<PlayerRoster>();
        var cameraLink = services.GetRequiredService<LoopbackTransport>();

        services.GetRequiredService<WorldReplayTape>().TimelineRestored += () => {
            for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
                if (
                    (seatRouter.TryRoute(slot: slot) is { } route) &&
                    route.Endpoint.ClockOwnedHere &&
                    (route.Endpoint.Identity == WorldInstanceHost.BootInstanceName)
                ) {
                    _ = seatRouter.CompareExchangeEntity(
                        slot,
                        route,
                        route.Entity,
                        out _
                    );
                }
            }
        };

        seatBindings.CameraApplicationDropped += slot => WorldCameraApplication.Deactivate(
            actingPrincipal: cameraRoster.PrincipalOf(slot: slot),
            link: cameraLink,
            slot: slot
        );

        // The loader's rule/state proof remains valid for its exact unchanged document. Host vocabulary does not:
        // complete that phase now that command services and every neighbour transport are available.
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
        var fileNeighbours = new WorldFileNeighbourResolver(
            baseDirectory: () => WorldDocumentPaths.DirectoryOf(documentPath: worldSource.SourcePath),
            catalogFingerprint: machineCatalogFingerprint,
            catalog: machineCatalog
        );
        var storageNeighbours = services.GetRequiredService<WorldStorageSyncHandle>().Neighbours;
        var neighbours = WorldCompositeNeighbourResolver.Compose(
            fileNeighbours,
            storageNeighbours
        );

        var admission = worldSource.Admission;
        bool admitted;
        string vocabularyReason;

        if ((admission is not null) && admission.AppliesTo(definition: worldSource.Definition, machines: machineCatalog)) {
            admitted = WorldDefinitionValidator.TryCompleteAdmission(admission: admission, machines: machineCatalog, neighbours: neighbours, reason: out vocabularyReason);
        } else {
            admitted = WorldDefinitionValidator.TryValidate(worldSource.Definition, out vocabularyReason, neighbours, machineCatalog);
        }
        if (!admitted) {
            Console.Error.WriteLine(value: $"[world] definition refused once its command vocabulary composed: {vocabularyReason}");

            return false;
        }

        // The running server also carries the resolver, for the ONE live document-swap moment (world.load/reload/
        // reset) that gets it — see WorldServer.Neighbours' own remarks on why nothing else does.
        var server = services.GetRequiredService<WorldServer>();

        // The override gate reads a row's source from the same directory the rendering host compiles it against, in
        // every boot shape, so a headless and a rendered host accept the same pipeline commits.
        server.PipelineSources = new WorldPipelineSources(documentDirectory: WorldDocumentPaths.DirectoryOf(documentPath: services.GetRequiredService<WorldDefinitionSource>().SourcePath));
        if (!server.TryBindPipelineRows(reason: out var pipelineReason)) {
            Console.Error.WriteLine(value: $"[world] definition refused: {pipelineReason}");

            return false;
        }
        server.Neighbours = neighbours;
        server.RebuildNeighbours = candidatePath => WorldCompositeNeighbourResolver.Compose(
            new WorldFileNeighbourResolver(
                baseDirectory: () => WorldDocumentPaths.DirectoryOf(documentPath: candidatePath),
                catalogFingerprint: machineCatalogFingerprint,
                catalog: machineCatalog
            ),
            storageNeighbours
        );
        server.RebuildDocuments = services.GetRequiredService<IWorldDocumentSource>();

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
        var graphHost = services.GetService<WorldViewGraphHost>();

        // A GPU shape resolves its device context here, before any presenter that creates objects on the device, so
        // the container, which disposes singletons in reverse creation order, releases every presenter before it.
        if (graphHost is not null) {
            _ = services.GetRequiredService<IGpuDeviceContext>();
        }

        var consoleSessions = services.GetRequiredService<TerminalConsoleSessions>();
        var audioDirector = services.GetRequiredService<WorldAudioDirector>();
        var definitionSource = services.GetRequiredService<WorldDefinitionSource>();
        var deferredVerbAnswers = WorldDeferredVerbAnswers.Attach(
            echoes: services.GetRequiredService<WorldDeferredVerbEchoes>(),
            registry: consoleRegistry
        );
        var scheduleRunner = services.GetRequiredService<WorldScheduleRunner>();

        // The boot row is admitted by the time this runs, so a sibling world the armed document declares can start
        // beside it before the first step.
        scheduleRunner.ArmInstances();
        services.GetRequiredService<WorldServer>().EchoTap = echo => {
            // A scheduled command's own mutation verdict arrives here and nowhere else, so a test world that
            // schedules a command the world must refuse has that refusal recorded in the schedule manifest.
            if (echo.ConnectionId == SubmissionEnvelope.LocalConnectionId) {
                scheduleRunner.NoteEcho(
                    message: echo.Message,
                    rejected: echo.Rejected
                );
            }

            // The per-verb half of a rebuild verdict: a rebuild verb registered its minted correlation at
            // submit, so a LOCAL submission's verdict prints an accountable "[<verb>: …]" line beside the
            // verb-agnostic "[world.mutation …]" narration — stderr on rejection (alongside "[world.mutation
            // rejected: …]"), stdout on acceptance (the verb's own confirmation, distinct from the narration's
            // "[world.mutation: …]" stderr line), so a script can account either verdict under the verb it submitted
            // rather than only the reason it was refused.
            // The same verdict settles the submitting line, for a session that reports settled results, and a
            // refusal is counted so `wire.errors` reports it like a synchronous one, in every boot shape.
            deferredVerbAnswers.Answer(
                echo: in echo,
                row: WorldInstanceHost.BootInstanceName
            );

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
                // The rendering host resolves views.graphs sources against this same moved directory from here
                // on — presentation-only, so a headless boot has no runtime to rebase.
                graphHost?.Rebase(documentDirectory: WorldDocumentPaths.DirectoryOf(documentPath: origin));
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
        // WorldMutation (see WorldEffect.Save's remarks for why), so WorldServer cannot run it through the ordinary
        // mutation pipeline — and cannot compose the snapshot itself either: Puck.World.Server references no rendering
        // or input, and the lever half of the world.save fold needs the live render levers, audio director, and pacing
        // control, all composition-root state. This closure composes the IDENTICAL snapshot (WorldSaveSnapshot)
        // WorldMutationCommandModule's own 'world.save' verb writes, to the world's own loaded file (never an authored
        // path — see the effect's remarks on why), and compacts the journal on success exactly like a manual save.
        // A write failure (disk full, the target's directory gone, a read-only file) is caught and narrated on
        // stderr by name; the firing tick is not rolled back, because nothing durable in it depended on the save
        // succeeding.
        var worldServer = services.GetRequiredService<WorldServer>();
        var renderSettings = services.GetRequiredService<WorldRenderSettings>();
        var pacing = services.GetRequiredService<PresentPacingControl>();
        var bindingBarVisibility = services.GetRequiredService<WorldBindingBarVisibility>();

        // The authored gameplay-cue lane: emitCue publishes a deterministic token from simulation. Audio consumes
        // that token through the same document-authored cue table as built-in events; an optional body association
        // supplies the body's authoritative position at delivery time, otherwise listener placement applies.
        worldServer.GameplayCueTap = cue => {
            var site = (((cue.Body is { } index) && (worldServer.Body(index: index) is { } body))
                ? body.FixedPosition.ToVector3()
                : (Vector3?)null
            );

            audioDirector.SubmitCue(
                eventToken: cue.Name,
                site: site
            );
        };

        worldServer.SaveEffectTap = tick => {
            var target = definitionSource.SourcePath;

            try {
                var snapshot = WorldSaveSnapshot.Compose(
                    audio: audioDirector,
                    bindingBar: bindingBarVisibility,
                    pacing: pacing,
                    render: renderSettings,
                    server: worldServer,
                    tick: tick
                );
                var bytes = WorldDefinitionSerialization.SavePreservingBasis(
                    basisPath: out var basisPath,
                    catalog: machineCatalog,
                    catalogFingerprint: machineCatalogFingerprint,
                    definition: snapshot,
                    imports: out var preservedImports,
                    note: out var note,
                    path: target
                );

                worldServer.Compact();

                var derivation = (((basisPath is { }) || (preservedImports.Count > 0))
                    ? $", basis: {((basisPath is { })
                        ? basisPath
                        : "none")}, imports: {preservedImports.Count.ToString(provider: CultureInfo.InvariantCulture)}"
                    : ((note.Length > 0)
                        ? $", {note}"
                        : ""
                ));

                Console.Error.WriteLine(value: $"[world.rule: save effect -> {target} ({bytes} bytes{derivation})]");
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)) {
                Console.Error.WriteLine(value: $"[world.rule: save effect refused — could not write {target} ({exception.Message.ReplaceLineEndings(replacementText: " ")})]");
            }
        };

        // Binds the instance host's own cross-instance narration to stderr — the boot server's own hub and
        // WorldMachineHost's already bound a sink at DI construction time (WorldBootComposition), before either one
        // could narrate anything of its own; WorldInstanceHost narrates nothing during construction, so attaching
        // here, after the container finishes building, loses nothing. Every headless script and canary reads the
        // identical lines a direct Console.Error write would have produced.
        services.GetRequiredService<WorldInstanceHost>().AttachNarrationSink(sink: new WorldConsoleNarrationSink());

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

        // THE MUSIC-TRANSITION CUE LANE: a committed segment transition fires music.transition, listener-placed (a
        // transition carries no world site) — the SAME tap-and-wiring shape the machine lifecycle lane above uses.
        // CORE: WorldServer and the audio director are both core-registered, so this runs unconditionally in EVERY
        // boot shape; a headless boot's cue accumulates harmlessly like every other cue here.
        worldServer.MusicTransitionTap = _ => {
            audioDirector.SubmitCue(
                eventToken: WorldAudioCue.MusicTransition,
                site: null
            );
        };

        // THE MUSIC-LAYER LANE: the active conditional-layer tune id set is level-triggered (never queued), so the
        // audio director re-derives its bed plan against it EVERY tick the set changes — see
        // WorldAudioDirector.SetActiveMusicLayers. CORE, same posture as the transition lane above.
        worldServer.MusicLayerTap = tuneIds => {
            audioDirector.SetActiveMusicLayers(tuneIds: tuneIds);
        };

        // THE MUSIC-EMBELLISHMENT CUE LANE: a fired director embellishment voices its OWN authored patch directly —
        // see WorldAudioDirector.SubmitEmbellishment's remarks for why this cannot ride the ordinary SubmitCue
        // token→row lookup the transition lane above uses. CORE, same posture as the transition lane above.
        worldServer.MusicEmbellishmentTap = patchId => {
            audioDirector.SubmitEmbellishment(patchId: patchId);
        };

        // THE CAPTURE-REQUEST DRAIN: world.screenshot arms a readback of the NEXT composed frame, so a run that ends
        // before that frame writes nothing at all. Left alone, the caller's only evidence is the arming echo, which
        // is indistinguishable from a capture that succeeded — the silent-success shape this repository has already
        // been bitten by. Say it out loud instead, at ApplicationStopped (every hosted service has stopped, so the
        // render loop is provably finished and an outstanding request provably never will be served). The scheduled
        // `captures` rows are not drained here: the host loop settles them (IFixedStepSimulation.SettleOwedFrames,
        // WorldCaptureScheduler.Drain) before it disposes the render root, while the chain that would have served
        // them is still alive. Presentation-only: a headless boot has no render probe and world.screenshot refuses
        // there anyway.
        // THE SCHEDULE DRAIN, every boot shape: a run that ended before its export tick must leave a manifest
        // saying where it got to rather than an empty directory a reader cannot tell from a crash.
        services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(callback: scheduleRunner.Drain);

        if (services.GetService<WorldRenderProbe>() is { } renderProbe) {
            services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(callback: () => {
                if (renderProbe.Root?.PendingCapturePath is { } pending) {
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

            // The default render graph plans here too, off the GPU, so a render.extensions config its set's schema does
            // not bind is refused by name before the renderer is built.
            _ = services.GetService<WorldRootGraph>();
        } catch (WorldRenderCapacityRefusedException refusal) {
            Console.Error.WriteLine(value: $"[world] definition refused: {refusal.Message}");

            return false;
        } catch (WorldRootGraphRefusedException refusal) {
            Console.Error.WriteLine(value: $"[world] definition refused: {refusal.Message}");

            return false;
        }

        // The document-composition read-back, stated once beside the other origin lines: how many basis-and-imports
        // merges this boot performed, how many reaches it answered from a document it had already composed, and what
        // the images it is holding cost. A shard boot is the shape this counts for — its own basis, its four
        // adjacency neighbours and every derived corner all name the same island document, and the shared figure is
        // what says so out loud instead of leaving it to a wall-clock reading of the boot. world.status answers the
        // same four numbers on demand.
        Console.Error.WriteLine(value: $"[world.documents] {WorldBootWork.Current.Read(kind: WorldBootWork.Compositions)} composed, {WorldBootWork.Current.Read(kind: WorldBootWork.CompositionsShared)} shared, {WorldDefinitionFileSource.ComposedDocumentsHeld} held ({WorldDefinitionFileSource.ComposedDocumentBytes} bytes)");

        return true;
    }
}
