using System.Diagnostics;
using System.Numerics;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // The session bind: ApplySource's Session arm calls this with the authored session record verbatim, projection and
    // resolution included, at boot and whenever a live document mutation (world.row.set screens/placements) replaces a
    // face's declared source. It resolves and attaches into a local candidate first, so a re-point that fails to
    // resolve leaves the slot's previous feed registered, rendering and holding its lease, and reports the failure by
    // name. Only a live candidate retires the old registration and takes the name; no frame is produced between the
    // release and the register, so a re-point shows no gap.
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

        slot.Session = feed;

        if (m_viewPipelines is not null) {
            RegisterSessionView(feed: feed);
        }

        ReconcileViews();

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
    // Completes a resolved session's view registration — deferred from ResolveSession because the render envelope
    // (m_viewPipelines) is not known until the render factory calls ConfigureViews (or a live reconcile runs, by which
    // point it always is): one WorldSessionSceneEmitter composed through its own SdfCompositionFrameSource, which the
    // session's instance renders under the slot's own name — NOT shared across screens even when two name the same
    // destination and camera, since the shipped content never needs that and a shared view would complicate the
    // per-slot teardown.
    private void RegisterSessionView(SessionFeed feed) {
        var emitter = new WorldSessionSceneEmitter(
            mirror: feed.Mirror,
            effectiveCameraName: feed.EffectiveCamera
        );
        var frameSource = new SdfCompositionFrameSource(
            dresser: emitter,
            emitters: [emitter]
        );
        var isWindow = (feed.Projection == WorldScreenProjection.Window);
        var width = (feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth);
        var height = (feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight);

        feed.FrameSource = frameSource;
        feed.Emitter = emitter;

        if (isWindow) {
            feed.SetWindowLease(lease: WorldSessionWindowLeases.Acquire(
                height: height,
                width: width
            ));
        }

        // The destination instance's own render envelope is not configured for a session screen by default — an
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
                allowGrowth: true,
                programWordCapacity: frameSource.WorstCaseProgramWordCapacity,
                instanceCapacity: frameSource.WorstCaseInstanceCapacity,
                measure: emitter.MeasureCandidate
            );
        }
    }
    // Releases one session's observation lease (WorldServer.AttachSink's disposable — the destination instance itself is
    // NEVER touched here, per docs/architecture/worlds.md: "releasing an observation lease alone never advances the
    // generation — the resolver owns lifecycle"); its instance leaves the render graph once no slot holds the feed.
    private void ReleaseSession(SessionFeed feed, int index, string reason) {
        feed.Dispose();

        Console.Error.WriteLine(value: $"[world.screen: session {index} -> destination '{feed.Destination}' released ({reason})]");
    }
    // The registered session feed a view instance name names, or null.
    private SessionFeed? SessionFeedOf(string name) {
        foreach (var slot in m_slots.Values) {
            if (
                (slot.Session is { FrameSource: not null } feed) &&
                string.Equals(
                    a: feed.RegistrationName,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )
            ) {
                return feed;
            }
        }

        return null;
    }
    // Drops a slot's session reference and releases its registration/lease — the symmetric half of
    // ApplySessionSource's acquire, run whenever the slot stops observing that destination (a source change away from
    // Session, or a screen removal).
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
    // Resolves (and headless-safely attaches) a session-sourced face's destination — the boot loop and
    // ApplySessionSource both call this, so a resolve at boot and a resolve triggered by a live document mutation take
    // the identical route. Returns the newly attached feed on success (slot.DeclaredFault cleared); returns null on
    // failure (slot.DeclaredFault set to the refusal reason). Deliberately never touches slot.Session itself either
    // way — the boot-loop caller assigns it directly (a fresh slot has nothing to preserve), while a re-point must be
    // able to inspect a failed resolve without losing the slot's previous feed reference.
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
            registrationName: WorldViewNames.Session(screen: slot.Index),
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
    // Recomputes every live WINDOW session's off-axis camera from this frame's local eye and the border pair's two
    // face rows — fresh every call, never cached across frames, so a placement mutation reaches the render the very
    // next produced frame.
    //
    // The eye is the primary local seat's body (WorldPopulation.EntryBody) at LocalEyeHeight, in the document's
    // authored space, the space WorldFaceCatalog derives both apertures in. With no resolvable local body, each window
    // falls back to the ordinary session camera.
    private void UpdateWindowCameras() {
        foreach (var slot in m_slots.Values) {
            if (slot.Session is { Projection: WorldScreenProjection.Window, Emitter: { } emitter }) {
                emitter.SetWindowCamera(camera: null);
            }
        }

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
        // per-viewer image either). A world with no population has no body 0 to read.
        if (
            (boot.Server.Population.Capacity == 0) ||
            (boot.Server.Population.EntryBody(index: 0) is not { } localBody)
        ) {
            return;
        }

        var localEye = (localBody.Position + new Vector3(
            x: 0f,
            y: LocalEyeHeight,
            z: 0f
        ));
        var bootDefinition = boot.Server.Definition;

        foreach (var slot in m_slots.Values) {
            if (
                (slot.Session is not { } feed) ||
                (feed.Projection != WorldScreenProjection.Window) ||
                (feed.Emitter is not { } emitter)
            ) {
                continue;
            }

            // A transient gap (the destination has not delivered a definition naming the counterpart yet, or the eye
            // stands behind the glass this frame) degrades to the emitter's ordinary default projection for the frame
            // rather than freezing or throwing — the fallback WorldSessionSceneEmitter.ResolveCamera takes for an
            // unknown or absent camera name.
            emitter.SetWindowCamera(camera: ((
                (RowOf(screen: slot.Index) is { } row) &&
                WorldWindowFrustumFit.TryResolveApertures(
                    counterpart: out var destination,
                    destination: feed.Mirror.Definition,
                    local: bootDefinition,
                    screenIndex: slot.Index,
                    source: out var source
                ) &&
                WorldWindowFrustumFit.TryFitWindow(
                    camera: out var camera,
                    destination: destination,
                    glass: WorldWindowFrustumFit.Glass(screen: row),
                    localEye: localEye,
                    source: source
                ))
                ? camera
                : null));
        }
    }
    // The screen row ReconcileScreens last applied for an index, or null.
    private WorldScreen? RowOf(int screen) {
        for (var index = 0; (index < m_rows.Count); index++) {
            if (m_rows[index].Index == screen) {
                return m_rows[index];
            }
        }

        return null;
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
                RenderWidth: (feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth),
                RenderHeight: (feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight),
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
    /// <param name="RendersEveryFrame">Whether this session renders on every produced frame — a window always does,
    /// paying its full render cost each frame, where an ordinary camera projection refreshes at the views' divisor
    /// (<c>world.view-refresh</c>).</param>
    internal readonly record struct WorldSessionDescription(string Destination, string? RequestedCamera, string? EffectiveCamera, string InstanceName, ulong GenerationId, bool LeaseHeld, bool InstanceGone, WorldScreenProjection Projection, int RenderWidth, int RenderHeight, bool RendersEveryFrame);

    // One session-sourced screen's live state: which destination it observes, its resolved instance/generation, the
    // attached observation lease + client-side mirror, and (once GPU services are configured) its registered
    // offscreen view. A mutable class so a lifecycle transition (re-point, teardown, instance-retired) updates it in
    // place; the constructor parameters are immutable facts about ONE resolution (a re-point builds a fresh instance
    // rather than mutating this one — see ApplySessionSource).
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
        // The frame source the session's instance renders, set once the views are configured (RegisterSessionView).
        public SdfCompositionFrameSource? FrameSource { get; set; }
        // Set by ReconcileSessionLifecycles the moment the resolved instance stops running — the projection then
        // holds its last mirrored image (the instance keeps rendering the mirror's frozen definition; nothing here
        // needs to force that, since the mirror simply stops receiving deliveries).
        public bool InstanceGone { get; set; }

        // Releases the observation lease and the envelope and window registrations; the session's instance leaves the
        // render graph once no slot holds the feed.
        public void Dispose() {
            EnvelopeRegistration?.Dispose();
            EnvelopeRegistration = null;
            WindowLease?.Dispose();
            WindowLease = null;
            Lease?.Dispose();
            Lease = null;
        }        /// <summary>Acquires (replacing any prior) this feed's window-cost lease.</summary>
        public void SetWindowLease(IDisposable lease) {
            WindowLease?.Dispose();
            WindowLease = lease;
        }
    }
    // A session's frame source on its own clock: the destination is independently scheduled, so the view hands its
    // composition the interval between its own frames rather than the host's frame delta, and no interpolation fraction.
    // Wall-clock and presentation-only: the away-seat framing it paces is not reproducible run to run.
    private sealed class SessionFrameSource(SdfCompositionFrameSource inner) : ISdfFrameSource {
        private bool m_hasProduced;
        private long m_lastProduceTimestamp;

        public SdfGlyphAtlas? GlyphAtlas => ((ISdfFrameSource)inner).GlyphAtlas;
        public IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => ((ISdfFrameSource)inner).ScreenDecals;
        public IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? ScreenSurfaceTransforms => ((ISdfFrameSource)inner).ScreenSurfaceTransforms;

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            var timestamp = Stopwatch.GetTimestamp();
            var ownDelta = (m_hasProduced
                ? ((float)Stopwatch.GetElapsedTime(
                    endingTimestamp: timestamp,
                    startingTimestamp: m_lastProduceTimestamp
                ).TotalSeconds)
                : 0f);

            m_lastProduceTimestamp = timestamp;
            m_hasProduced = true;

            return inner.CaptureFrame(
                deltaSeconds: ownDelta,
                height: height,
                interpolationAlpha: 0f,
                width: width
            );
        }
        // The time a device loss takes to recover must not land as one giant smoothing delta on the next frame.
        public void NotifyDeviceLost() {
            ((ISdfFrameSource)inner).NotifyDeviceLost();
            m_hasProduced = false;
        }
    }
}
