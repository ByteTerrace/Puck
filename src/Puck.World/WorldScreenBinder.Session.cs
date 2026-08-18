using System.Numerics;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // The document-driven bind: ApplySource's Session arm calls this with the AUTHORED session record VERBATIM —
    // carrying Projection/Resolution, which TrySession's narrower (destination, camera)-only verb surface cannot
    // express. Reusing this ONE core keeps a live re-point (TrySession) and a document delivery (ApplySource) from
    // ever disagreeing about what "session {index}" is.
    private (bool Ok, string Message) ApplySessionSource(int index, WorldScreenSource.Session session) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        var previous = slot.Session;
        var feed = ResolveSession(
            session: session,
            slot: slot
        );

        if (feed is null) {
            return (Ok: false, Message: (slot.DeclaredFault ?? $"screen {index} session bind failed"));
        }

        if (previous is { } oldFeed) {
            ReleaseSession(
                feed: oldFeed,
                index: index,
                reason: "source re-pointed"
            );
        }

        if (m_viewServices is not null) {
            RegisterSessionView(
                feed: feed,
                index: index
            );
        }

        slot.Session = feed;

        return (Ok: true, Message: $"screen {index} showing session '{session.Destination}' -> instance '{feed.InstanceName}'");
    }
    // Best-effort lifecycle observation, called once per produced frame: a destination instance retiring drops the
    // projection to a held last image with a stderr note. The held-image half is free — the mirror simply stops
    // receiving deliveries and Resolve keeps re-rendering its last mirrored definition — this only detects the
    // transition once and narrates it. Re-resolving onto the destination's next generation is not implemented; this
    // holds the frozen image and says so rather than silently going stale.
    private void ReconcileSessionLifecycles() {
        foreach (var slot in m_slots.Values) {
            if (
                (slot.Session is not { } feed) ||
                feed.InstanceGone
            ) {
                continue;
            }

            if (!m_instanceHost.TryGet(
                name: feed.InstanceName,
                instance: out _
            )) {
                feed.InstanceGone = true;

                Console.Error.WriteLine(value: $"[world.screen: session {slot.Index} -> destination '{feed.Destination}' instance '{feed.InstanceName}' retired — holding last image]");
            }
        }
    }
    // Completes a resolved session's offscreen GPU registration — deferred from ResolveSession because the render
    // envelope (m_viewServices) is not known until the render factory calls ConfigureViews (or a live reconcile runs,
    // by which point it always is). Mirrors RegisterCameraView's shape: one WorldSessionSceneEmitter composed through
    // its own SdfCompositionFrameSource, wrapped in a WorldSessionView and registered under the slot's own name — NOT
    // shared across screens even when two name the same destination+camera (unlike camera views), since the shipped
    // content never needs that and a shared registration would complicate the per-slot teardown this wave relies on.
    private void RegisterSessionView(int index, SessionFeed feed) {
        m_viewStack ??= new ViewStack();

        var emitter = new WorldSessionSceneEmitter(
            mirror: feed.Mirror,
            effectiveCameraName: feed.EffectiveCamera
        );
        var frameSource = new SdfCompositionFrameSource(
            dresser: emitter,
            emitters: [emitter]
        );
        var isWindow = (feed.Projection == WorldScreenProjection.Window);
        // A window renders every produced frame (isBudgeted: false — see WorldSessionView's own remarks): a stale
        // image between ViewStack's round-robin turns would show the destination lagging the viewer's own eye
        // movement, breaking the parallax the projection exists for. The resolution defaults to the panel size every
        // OTHER session already renders at, so an unauthored facet is unaffected.
        var width = ((uint)(feed.Resolution?.Width ?? ((int)WorldSessionView.DefaultWidth)));
        var height = ((uint)(feed.Resolution?.Height ?? ((int)WorldSessionView.DefaultHeight)));
        var view = new WorldSessionView(
            services: m_viewServices!,
            hostsOnDirectX: m_viewHostsOnDirectX,
            frameSource: frameSource,
            width: width,
            height: height,
            isBudgeted: !isWindow
        );

        _ = m_viewStack.Register(
            name: feed.RegistrationName,
            content: view,
            band: ScreenSlotPriority.Ambient
        );
        feed.Stack = m_viewStack;
        feed.View = view;
        feed.Emitter = emitter;

        if (isWindow) {
            feed.SetWindowLease(lease: WorldSessionWindowLeases.Acquire(
                height: ((int)height),
                width: ((int)width)
            ));
        }

        // The destination instance's own render envelope is not configured for a jumbotron session by default — an
        // unconfigured envelope admits any document mutation regardless of capacity. Configuring it here closes
        // that gap for ordinary authored session screens. The candidate-aware emitter
        // measurement is load-bearing: returning the construction capacity for every candidate would make
        // WorldRenderEnvelope compare the ceiling to itself and admit every mutation.
        if (
            m_instanceHost.TryGet(
            name: feed.InstanceName,
            instance: out var destination
        ) &&
            (destination is not null)
        ) {
            feed.EnvelopeRegistration?.Dispose();
            feed.EnvelopeRegistration = destination.Server.Envelope.Configure(
                programWordCapacity: frameSource.WorstCaseProgramWordCapacity,
                instanceCapacity: frameSource.WorstCaseInstanceCapacity,
                measure: emitter.MeasureCandidate
            );
        }
    }
    // Releases one session's GPU registration (ViewStack.Release disposes the WorldSessionView and its offscreen
    // engine) and its observation lease (WorldServer.AttachSink's disposable — the destination instance itself is
    // NEVER touched here, per docs/vision.md: "releasing an observation lease alone never advances the
    // generation — the resolver owns lifecycle").
    private void ReleaseSession(SessionFeed feed, int index, string reason) {
        m_viewStack?.Release(name: feed.RegistrationName);
        feed.Dispose();

        Console.Error.WriteLine(value: $"[world.screen: session {index} -> destination '{feed.Destination}' released ({reason})]");
    }
    // Drops a slot's session reference and releases its registration/lease — the symmetric half of TrySession's
    // acquire, run whenever the slot stops observing that destination (a source change away from Session, or a
    // screen removal).
    private void ReleaseSlotSession(ScreenSlot slot) {
        if (slot.Session is not { } feed) {
            return;
        }

        slot.Session = null;
        ReleaseSession(
            feed: feed,
            index: slot.Index,
            reason: "source changed"
        );
    }
    // Refuses an unknown camera at bind time with a loud stderr note and falls back to the default projection,
    // never a boot refusal. An absent request resolves to null (the default projection) with no narration — that is
    // ordinary, not a fault.
    private static string? ResolveEffectiveCameraName(WorldDefinition destinationDefinition, string? requested, int index, string destinationName) {
        if (requested is not { } name) {
            return null;
        }

        foreach (var camera in destinationDefinition.Cameras) {
            if (string.Equals(
                a: camera.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return name;
            }
        }

        Console.Error.WriteLine(value: $"[world.screen: session {index} -> destination '{destinationName}' names unknown camera '{name}' — falling back to the default projection]");

        return null;
    }
    // Resolves (and headless-safely attaches) a session-sourced face's destination — the boot-loop and the runtime
    // TrySession path both call this, so a resolve at boot and a resolve triggered by a live document mutation take
    // the identical route. Returns the newly attached feed on success (slot.DeclaredFault cleared); returns null on
    // failure (slot.DeclaredFault set to the refusal reason). Deliberately never touches slot.Session itself either
    // way — the boot-loop caller assigns it directly (a fresh slot has nothing to preserve), while TrySession's
    // re-point caller must be able to inspect a failed resolve without losing the slot's previous feed reference.
    // GPU view registration is a separate step (RegisterSessionView), since GPU services may not exist yet
    // (headless, or boot before the render factory runs).
    private SessionFeed? ResolveSession(ScreenSlot slot, WorldScreenSource.Session session) {
        if (!TryResolveDestinationInstance(
            destinationName: session.Destination,
            instance: out var instance,
            resolved: out var resolvedSession,
            reason: out var reason
        )) {
            slot.DeclaredFault = reason;

            Console.Error.WriteLine(value: $"[world.screen: session {slot.Index} refused ({reason})]");

            return null;
        }

        WarnIfDestinationRecurses(
            index: slot.Index,
            destinationName: session.Destination,
            destinationDefinition: instance!.Server.Definition
        );

        var effectiveCamera = ResolveEffectiveCameraName(
            destinationDefinition: instance.Server.Definition,
            requested: session.CameraName,
            index: slot.Index,
            destinationName: session.Destination
        );
        var mirror = new WorldSessionMirror(placeholder: instance.Server.Definition);
        var lease = instance.Server.AttachSink(sink: mirror);

        var feed = new SessionFeed(
            destination: session.Destination,
            requestedCamera: session.CameraName,
            effectiveCamera: effectiveCamera,
            instanceName: resolvedSession.InstanceName,
            generationId: resolvedSession.GenerationId,
            mirror: mirror,
            lease: lease,
            registrationName: $"{SessionRegistrationPrefix}{slot.Index}",
            projection: session.Projection,
            resolution: session.Resolution
        );

        slot.DeclaredFault = null;

        Console.Error.WriteLine(value: $"[world.screen: session {slot.Index} -> destination '{session.Destination}' resolved to instance '{resolvedSession.InstanceName}' generation {resolvedSession.GenerationId}{(resolvedSession.IsNewGeneration
            ? " (new)"
            : "")}]");

        return feed;
    }
    // Resolves through the instance host's ONE observation door. Besides sharing WorldSessionResolver identity with
    // portal entry, that door also owns persisted-origin adoption ("return means home"), collision fencing and
    // failed-generation abort; duplicating only TryResolve+TryStart here previously let a screen mint a second copy
    // of an already-running persisted world while a crossing correctly adopted it.
    private bool TryResolveDestinationInstance(string destinationName, out WorldInstance? instance, out WorldSessionResolver.Resolved resolved, out string reason) {
        if (
            !m_instanceHost.TryGet(
            instance: out var source,
            name: WorldInstanceHost.BootInstanceName
        ) ||
            (source is null)
        ) {
            instance = null;
            resolved = default;
            reason = "the boot source instance is not running";

            return false;
        }

        return m_instanceHost.TryResolveObservedDestination(
            destinationName: destinationName,
            reason: out reason,
            resolved: out resolved,
            source: source,
            target: out instance
        );
    }
    // Resolves the source (local, this document) face claiming this slot's screen index, plus its portal facet's
    // mapped counterpart and that counterpart's own derived face in the destination's mirrored document.
    // WorldDefinitionValidator already refuses a 'window' projection whose face lacks a mapped counterpart at
    // document-validation time, so a false return here means the destination mirror has not delivered a definition
    // naming that face yet.
    private static bool TryResolveWindowGeometry(WorldDefinition bootDefinition, WorldFaceCatalog localCatalog, ScreenSlot slot, SessionFeed feed, out WorldFaceGeometry source, out WorldFaceGeometry destination) {
        source = default;
        destination = default;

        var found = false;
        var localRow = default(WorldFaceRow);

        foreach (var row in localCatalog.Rows) {
            if (row.ScreenIndex == slot.Index) {
                localRow = row;
                found = true;

                break;
            }
        }

        if (!found) {
            return false;
        }

        var placement = WorldDefinitionRows.FindPlacement(
            placements: bootDefinition.Placements,
            id: localRow.PlacementId
        );
        var face = ((placement is null)
            ? null
            : WorldDefinitionRows.FindPlacementFace(
                placement: placement,
                face: localRow.FaceName
            )
        );

        if (
            (face?.Portal is not { Arrival: WorldPortalArrival.Mapped, Counterpart: { } counterpart }) ||
            !WorldPortalCounterpart.TryParse(
            counterpart: counterpart,
            face: out var destinationFaceName,
            placementId: out var destinationPlacementId
        )
        ) {
            return false;
        }

        var destinationCatalog = WorldFaceCatalog.For(definition: feed.Mirror.Definition);

        if (!destinationCatalog.TryFind(
            faceName: destinationFaceName,
            placementId: destinationPlacementId,
            row: out var destinationRow
        )) {
            return false;
        }

        source = WorldFaceGeometry.FromFrame(frame: localRow.Frame);
        destination = WorldFaceGeometry.FromFrame(frame: destinationRow.Frame);

        return true;
    }
    // The runtime session bind — the ApplySource switch's Session arm, reached by a live document mutation
    // (world.row.set screens/placements) replacing a face's declared source. Resolves/attaches (headless-safe) into
    // a local candidate first — slot.Session is never touched until the new feed is proven live: a re-point that
    // fails to resolve leaves the slot's previous feed completely untouched — still registered, still rendering,
    // still holding its lease — and reports failure by name rather than silently landing on a torn-down slot while
    // claiming success. Only once the new feed is confirmed does this retire the old registration and hand the name
    // to the new one; single-threaded confinement means no frame is ever produced between the release and the
    // register below, so a successful re-point still shows no gap.
    //
    // Releasing BEFORE registering is what keeps this the compliant caller of ViewStack.Register's documented
    // contract: Register on an already-held name treats the incoming content as an update to the same logical
    // registration (see RegisterCameraView, the only other caller, which reuses one persistent instance across
    // every re-register). RegisterSessionView instead constructs a brand-new WorldSessionView every call, so
    // registering it under an already-occupied name would silently orphan whatever content currently answers to
    // that name — releasing the old feed's registration first avoids that.
    // The runtime screen.session verb's own narrow surface (destination + optional camera only — it re-points
    // an ordinary camera-projection session live; a WINDOW facet is authored-only, per this lane's own brief, so
    // this verb has no way to spell one). Shares ApplySessionSource's bind/release/register core with the
    // document-reconcile path below rather than duplicating it.
    private (bool Ok, string Message) TrySession(int index, string destinationName, string? cameraName) =>
        ApplySessionSource(
            index: index,
            session: new WorldScreenSource.Session(
                Destination: destinationName,
                CameraName: cameraName
            )
        );
    // Recomputes every live WINDOW session's off-axis camera from this frame's local eye and the border pair's two
    // face rows — fresh every call, never cached across frames, so a placement mutation reaches the render the very
    // next produced frame.
    //
    // The eye is read from the authoritative simulation body (WorldPopulation.EntryBody), never from
    // hostFrame.Views: the overworld's SdfViewSnapshot camera rides a render-relative space, while
    // WorldFaceCatalog's derived frames are in the document's absolute authored space, and mixing the two silently
    // would fit a frustum against the wrong point. A no-op with no live window session or no resolvable local body.
    private void UpdateWindowCameras() {
        // The LOCAL (boot) document — the same "one observation door" WorldInstanceHost.BootInstanceName resolves
        // everywhere else in this type (TryResolveDestinationInstance). Absent only in a boot-sequencing gap this
        // binder itself is constructed inside; a window degrades to its ordinary fallback for that one frame.
        if (
            !m_instanceHost.TryGet(
            instance: out var boot,
            name: WorldInstanceHost.BootInstanceName
        ) ||
            (boot is null)
        ) {
            return;
        }

        // The reference viewer: a screen surface renders ONE shared image per slot today, so a window necessarily
        // fits against ONE eye — the primary local seat's (body index 0 — player.* is 1-based, body:<n> is 0-based),
        // the same single-perspective simplification an ordinary camera-projection session already makes (it has no
        // per-viewer image either).
        if (boot.Server.Population.EntryBody(index: 0) is not { } localBody) {
            return;
        }

        var localEye = (localBody.Position + new Vector3(
            x: 0f,
            y: LocalEyeHeight,
            z: 0f
        ));
        var bootDefinition = boot.Server.Definition;
        var localCatalog = WorldFaceCatalog.For(definition: bootDefinition);

        foreach (var slot in m_slots.Values) {
            if (
                (slot.Session is not { } feed) ||
                (feed.Projection != WorldScreenProjection.Window) ||
                (feed.Emitter is not { } emitter)
            ) {
                continue;
            }

            var geometryOk = TryResolveWindowGeometry(
                bootDefinition: bootDefinition,
                destination: out var destination,
                feed: feed,
                localCatalog: localCatalog,
                slot: slot,
                source: out var source
            );
            var camera = default(Puck.Abstractions.Cameras.CameraSnapshot);
            var offset = default(Vector2);
            var fitOk = (geometryOk && WorldWindowFrustumFit.TryFitWindow(
                camera: out camera,
                destination: destination,
                localEye: localEye,
                offset: out offset,
                source: source
            ));

            if (fitOk) {
                emitter.SetWindowCamera(
                    camera: camera,
                    offset: offset
                );
            } else {
                // A transient gap (the destination hasn't delivered its first definition yet, the eye stands behind
                // the glass this frame) degrades to the emitter's own ordinary default projection for one frame
                // rather than freezing or throwing — the SAME fallback WorldSessionSceneEmitter.ResolveCamera already
                // takes for an unknown/absent camera name.
                emitter.SetWindowCamera(
                    camera: null,
                    offset: default
                );
            }
        }
    }
    // A session mirror never processes a destination's own screens/faces at all (WorldSessionSceneEmitter renders
    // static placement geometry only), so recursion is impossible by construction regardless of this check — this
    // narrates the policy loudly when it would otherwise have mattered, so the refusal is observable rather than
    // merely true.
    private static void WarnIfDestinationRecurses(int index, string destinationName, WorldDefinition destinationDefinition) {
        var recurses = false;

        foreach (var screen in destinationDefinition.Screens) {
            if (screen.Source is WorldScreenSource.Session) {
                recurses = true;

                break;
            }
        }

        if (!recurses) {
            foreach (var placement in destinationDefinition.Placements) {
                foreach (var face in (placement.FaceSources ?? [])) {
                    if (face.Source is WorldScreenSource.Session) {
                        recurses = true;

                        break;
                    }
                }

                if (recurses) {
                    break;
                }
            }
        }

        if (recurses) {
            Console.Error.WriteLine(value: $"[world.screen: session {index} -> destination '{destinationName}' authors its own session screen(s) — recursion refused at depth 1 (a session mirror renders static geometry only and never processes a destination's own screens)]");
        }
    }

    /// <summary>Reads back a screen index's session projection state, when it carries one.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="description">The session's live state, on success.</param>
    /// <returns>Whether the index carries a resolved session projection.</returns>
    public bool TryDescribeSession(int index, out WorldSessionDescription description) {
        if (
            m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) &&
            (slot.Session is { } feed)
        ) {
            var isWindow = (feed.Projection == WorldScreenProjection.Window);

            description = new WorldSessionDescription(
                Destination: feed.Destination,
                RequestedCamera: feed.RequestedCamera,
                EffectiveCamera: feed.EffectiveCamera,
                InstanceName: feed.InstanceName,
                GenerationId: feed.GenerationId,
                LeaseHeld: !feed.InstanceGone,
                InstanceGone: feed.InstanceGone,
                Projection: feed.Projection,
                RenderWidth: (feed.Resolution?.Width ?? ((int)WorldSessionView.DefaultWidth)),
                RenderHeight: (feed.Resolution?.Height ?? ((int)WorldSessionView.DefaultHeight)),
                RendersEveryFrame: isWindow
            );

            return true;
        }

        description = default;

        return false;
    }

    /// <summary>One session-sourced screen's live projection state — the <c>world.faces</c> read-back's session
    /// extension.</summary>
    /// <param name="Destination">The observed destination row's name.</param>
    /// <param name="RequestedCamera">The authored camera name, or <see langword="null"/> for the default projection.</param>
    /// <param name="EffectiveCamera">The camera actually rendered through — <see langword="null"/> when the
    /// requested camera was absent/unknown at bind time (or none was authored) and the default projection applies.</param>
    /// <param name="InstanceName">The resolved destination instance's process-local name.</param>
    /// <param name="GenerationId">The resolved generation id — the same id a crossing at the same door would land in.</param>
    /// <param name="LeaseHeld">Whether the observation lease's destination instance is still running.</param>
    /// <param name="InstanceGone">Whether the destination instance retired — the projection holds its last image.</param>
    /// <param name="Projection">How the destination render projects onto this face — see
    /// <see cref="WorldScreenProjection"/>.</param>
    /// <param name="RenderWidth">The resolved offscreen render width, pixels — the true cost this session pays every
    /// time it produces a frame (see <see cref="WorldSessionWindowLeases"/> for a window's own accounting).</param>
    /// <param name="RenderHeight">The resolved offscreen render height, pixels.</param>
    /// <param name="RendersEveryFrame">Whether this session is unbudgeted — a window always is (see
    /// <c>Puck.SdfVm.Views.WorldSessionView.IsBudgeted</c>): it pays its full render cost on every produced frame,
    /// never sharing the <c>OffscreenRenderBudget.PerProducedFrame</c> round-robin the way an ordinary camera projection does.</param>
    internal readonly record struct WorldSessionDescription(string Destination, string? RequestedCamera, string? EffectiveCamera, string InstanceName, ulong GenerationId, bool LeaseHeld, bool InstanceGone, WorldScreenProjection Projection, int RenderWidth, int RenderHeight, bool RendersEveryFrame);

    // One session-sourced screen's live state: which destination it observes, its resolved instance/generation, the
    // attached observation lease + client-side mirror, and (once GPU services are configured) its registered
    // offscreen view. A mutable class so a lifecycle transition (re-point, teardown, instance-retired) updates it in
    // place; the constructor parameters are immutable facts about ONE resolution (a re-point builds a fresh instance
    // rather than mutating this one — see TrySession).
    private sealed class SessionFeed(string destination, string? requestedCamera, string? effectiveCamera, string instanceName, ulong generationId, WorldSessionMirror mirror, IDisposable lease, string registrationName, WorldScreenProjection projection, WorldScreenResolution? resolution) : IDisposable {
        public string Destination { get; } = destination;
        public string? RequestedCamera { get; } = requestedCamera;
        public string? EffectiveCamera { get; } = effectiveCamera;
        public string InstanceName { get; } = instanceName;
        public ulong GenerationId { get; } = generationId;
        public WorldSessionMirror Mirror { get; } = mirror;
        public string RegistrationName { get; } = registrationName;
        public WorldScreenProjection Projection { get; } = projection;
        public WorldScreenResolution? Resolution { get; } = resolution;

        private IDisposable? Lease { get; set; } = lease;

        // Acquired only for a WINDOW projection (WorldSessionWindowLeases) — the runtime accounting world.faces'
        // true-cost echo reads; the DOCUMENT-level refusal is WorldDefinitionValidator's, at boot/mutation time, not
        // this lease.
        private IDisposable? WindowLease { get; set; }

        // Set by RegisterSessionView (its own constructed instance) — the render-envelope's per-frame WINDOW update
        // (WorldScreenBinder.UpdateWindowCameras) pushes the fitted camera into it before Resolve; a non-window feed
        // never needs it.
        public WorldSessionSceneEmitter? Emitter { get; set; }
        public IDisposable? EnvelopeRegistration { get; set; }
        // Set by ReconcileSessionLifecycles the moment the resolved instance stops running — the projection then
        // holds its last mirrored image (Resolve keeps re-rendering the mirror's frozen definition; nothing here
        // needs to force that, since the mirror simply stops receiving deliveries).
        public bool InstanceGone { get; set; }
        public ViewStack? Stack { get; set; }
        public WorldSessionView? View { get; set; }

        // Releases the observation lease ONLY — the GPU registration (Stack.Release) is the caller's job (see
        // ReleaseSession), because releasing it needs the SHARED m_viewStack this feed does not itself hold a
        // disposal-owning reference to (Stack here is a read reference for Handle/Light, not an owner).
        public void Dispose() {
            EnvelopeRegistration?.Dispose();
            EnvelopeRegistration = null;
            WindowLease?.Dispose();
            WindowLease = null;
            Lease?.Dispose();
            Lease = null;
        }
        public nint Handle() => (Stack?.Resolve(name: RegistrationName) ?? 0);
        public Vector3 Light() => (Stack?.ResolveGlow(name: RegistrationName) ?? Vector3.Zero);
        /// <summary>Acquires (replacing any prior) this feed's window-cost lease.</summary>
        public void SetWindowLease(IDisposable lease) {
            WindowLease?.Dispose();
            WindowLease = lease;
        }
    }
}
