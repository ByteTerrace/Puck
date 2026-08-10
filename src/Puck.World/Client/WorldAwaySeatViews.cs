using Puck.Hosting;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// Traveler-follow stage 1's away-render pool: one offscreen render per followed instance (never per seat),
/// refcounted like <see cref="WorldInstanceHost.ReapIfEmpty"/>'s own occupancy rule — a second seat crossing into an
/// already-tracked instance shares its render rather than double-registering, and tracking releases only once the
/// last seat following that instance leaves it. Mirrors <see cref="WorldScreenBinder.ResolveSession"/>/
/// <see cref="WorldScreenBinder.RegisterSessionView"/>'s construction exactly: a <see cref="WorldSessionMirror"/>
/// attached via <see cref="Server.WorldServer.AttachSink"/>, composed through an <see cref="AwaySeatSceneEmitter"/>
/// into an <see cref="SdfCompositionFrameSource"/>, wrapped in a <see cref="WorldSessionView"/> registered into this
/// pool's own <see cref="ViewStack"/> (a second stack, not the binder's — the two content kinds never share a
/// registration namespace).
/// </summary>
/// <remarks>
/// <para><b>Reconciliation, once per produced frame.</b> <see cref="Reconcile"/> (called from
/// <see cref="WorldFrameSource.CaptureFrame"/>, before anything resolves a handle this frame) diffs the router's
/// current away locations against the tracked set: a newly-away instance starts tracking, an instance no seat
/// follows any more stops. The tracked slot (whose mirrored entity the chase camera follows) is recomputed the same
/// pass — the lowest local seat slot currently following that instance — so two seats sharing one destination
/// degrade gracefully (the render follows one of them, never neither) rather than needing a widened multi-camera
/// contract this stage does not build. The same definition revision reconciles the followed world's authored and
/// derived session screens: each global source resolves through <see cref="WorldInstanceHost.TryResolveObservedDestination"/>
/// and registers a screen-free child projection before the parent view, so portal openings remain live after a
/// crossing while recursion stops structurally at depth one.</para>
/// <para><b>The screen-source seam.</b> <see cref="ScreenSources"/> is a fixed dictionary, built once at
/// construction, one entry per <see cref="WorldAwaySeatQuad"/> reserved index — merged into
/// <c>SdfWorldRenderSpec.ScreenSources</c> alongside <see cref="WorldScreenBinder.ScreenSources"/> at the
/// composition root (disjoint index ranges by construction, so a plain dictionary concat is exact). Each entry's
/// delegate reads whatever this call resolves to, exactly like <see cref="WorldScreenBinder.CurrentHandle"/>'s own
/// live-read contract — never rebuilt after boot, per <c>SdfEngineNode</c>'s "the screen-source dictionary is
/// copied once, at construction" invariant.</para>
/// </remarks>
internal sealed class WorldAwaySeatViews : IDisposable {
    private sealed class Entry {
        public required string InstanceName;
        public required WorldSessionMirror Mirror;
        public required IDisposable Lease;
        public required AwaySeatSceneEmitter Emitter;
        public required SdfCompositionFrameSource FrameSource;
        public required IDisposable EnvelopeRegistration;
        public Dictionary<int, NestedFeed> SessionFeeds { get; } = new();
        public int SessionSourceRevision = -1;
        public WorldSessionView? View;
        // The lowest local seat slot currently following this instance — recomputed every Reconcile call, read live
        // by the emitter's own tracked-index delegate (never cached past one frame).
        public int TrackedSlot;
        // The offscreen render's currently constructed size — the largest-area following seat's own exact composed
        // viewport as of the last commit in ReconcileViewportSizes. An offscreen render target cannot resize in
        // place, so a change here drives a release-and-recreate, never an in-place mutation of the live View.
        public uint DesiredWidth = WorldSessionView.DefaultWidth;
        public uint DesiredHeight = WorldSessionView.DefaultHeight;
        // The size ReconcileViewportSizes most recently OBSERVED (as opposed to DesiredWidth/Height, which only
        // moves once that observation has held for ViewportSettleFrameThreshold consecutive produced frames) and how
        // many consecutive frames it has held — a layout transition eases its region continuously, so the reported
        // pixel size changes on nearly every produced frame during a reflow; without this debounce every one of
        // those changes would release and rebuild the WorldSessionView + engine.
        public uint PendingWidth;
        public uint PendingHeight;
        public int StableFrameCount;
        public string RegistrationName => $"away-seat:{InstanceName}";
    }

    private sealed class NestedFeed {
        public required int ScreenIndex;
        public required string Destination;
        public required string? RequestedCamera;
        public required string InstanceName;
        public required WorldSessionMirror Mirror;
        public required IDisposable Lease;
        public required WorldSessionSceneEmitter Emitter;
        public required SdfCompositionFrameSource FrameSource;
        public required IDisposable EnvelopeRegistration;
        public WorldSessionView? View;
        public string RegistrationName(string parentInstanceName) => $"away-seat:{parentInstanceName}:session:{ScreenIndex}";
    }

    private readonly WorldInstanceHost m_instances;
    private readonly WorldSeatInstanceRouter m_seatRouter;
    private readonly WorldCameraOrbit m_cameraOrbit;
    private readonly WorldCompositionState m_composition;
    private readonly Dictionary<string, Entry> m_entries = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_releaseScratch = new();
    // Away-seat render resolution: each local seat's own composed viewport pixel size, reported once per produced
    // frame by WorldFrameSource.Dress (the one place that resolves it) via ReportSeatViewportSize, before this
    // pool's own RenderViews runs later the same frame. Seeded at the default size so a tracked-but-never-reported
    // slot still renders something rather than a degenerate 0x0 offscreen target.
    private readonly uint[] m_seatViewportWidth = CreateSeatDimensionArray(value: WorldSessionView.DefaultWidth);
    private readonly uint[] m_seatViewportHeight = CreateSeatDimensionArray(value: WorldSessionView.DefaultHeight);
    private ViewStack? m_viewStack;
    private SdfViewGpuServices? m_services;
    private bool m_hostsOnDirectX;
    private bool m_disposed;

    /// <summary>Initializes the pool over the running-instance registry and the traveler-follow router it
    /// reconciles against.</summary>
    /// <param name="instances">The running-instance registry — resolves a followed instance's own server to attach
    /// a mirror to.</param>
    /// <param name="seatRouter">The traveler-follow router — the source of truth <see cref="Reconcile"/> diffs
    /// against and <see cref="ResolveHandle"/> reads per seat.</param>
    /// <param name="cameraOrbit">Every local seat's live camera-orbit accumulator — threaded into each tracked
    /// entry's <see cref="AwaySeatSceneEmitter"/> so the tracked follower's own live drag keeps steering the away
    /// view's native camera.</param>
    /// <param name="composition">The live global layout/camera override store. Its camera selection is consumed by
    /// each followed instance's own emitter so the override survives a crossing.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldAwaySeatViews(WorldInstanceHost instances, WorldSeatInstanceRouter seatRouter, WorldCameraOrbit cameraOrbit, WorldCompositionState composition) {
        ArgumentNullException.ThrowIfNull(argument: instances);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);
        ArgumentNullException.ThrowIfNull(argument: cameraOrbit);
        ArgumentNullException.ThrowIfNull(argument: composition);

        m_instances = instances;
        m_seatRouter = seatRouter;
        m_cameraOrbit = cameraOrbit;
        m_composition = composition;

        var sources = new Dictionary<int, Func<nint>>();

        for (var slot = 0; (slot < WorldAwaySeatQuad.Count); slot++) {
            var captured = slot;

            sources[WorldAwaySeatQuad.IndexForSeat(slot: captured)] = () => ResolveHandle(slot: captured);
        }

        ScreenSources = sources;
    }

    /// <summary>The reserved away-seat quad indices' screen-source providers — merged into
    /// <c>SdfWorldRenderSpec.ScreenSources</c> at the composition root.</summary>
    public IReadOnlyDictionary<int, Func<nint>> ScreenSources { get; }

    /// <summary>Completes GPU registration for every currently-tracked instance (and any tracked later) —
    /// deferred from construction because the render envelope/GPU services are not known until the frame source has
    /// probed them, mirroring <c>WorldScreenBinder.ConfigureViews</c>'s own deferral.</summary>
    /// <param name="services">The shared view GPU-services bundle.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12.</param>
    public void ConfigureViews(SdfViewGpuServices services, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(argument: services);

        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        m_services = services;
        m_hostsOnDirectX = hostsOnDirectX;
        m_viewStack ??= new ViewStack();

        foreach (var entry in m_entries.Values) {
            ReconcileSessionSources(entry: entry);
            EnsureNestedRegistered(entry: entry);
            EnsureRegistered(entry: entry);
        }
    }

    /// <summary>Diffs the tracked set against the router's CURRENT away locations — starts tracking a
    /// newly-away instance, stops tracking one no seat follows any more, and re-points each tracked entry's own
    /// chase target to its lowest current follower. Called once per produced frame, before any handle is
    /// resolved.</summary>
    public void Reconcile() {
        if (m_disposed) {
            return;
        }

        var wanted = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
            var location = m_seatRouter.Location(slot: slot);

            if (string.Equals(a: location.InstanceName, b: WorldInstanceHost.BootInstanceName, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            if (!wanted.ContainsKey(key: location.InstanceName)) {
                wanted[location.InstanceName] = slot;
            }
        }

        foreach (var (name, slot) in wanted) {
            if (m_entries.TryGetValue(key: name, value: out var existing)) {
                existing.TrackedSlot = slot;
                ReconcileSessionSources(entry: existing);
            } else {
                TryTrack(instanceName: name, initialSlot: slot);
            }
        }

        m_releaseScratch.Clear();

        foreach (var name in m_entries.Keys) {
            if (!wanted.ContainsKey(key: name)) {
                m_releaseScratch.Add(item: name);
            }
        }

        foreach (var name in m_releaseScratch) {
            Untrack(instanceName: name);
        }
    }

    private void TryTrack(string instanceName, int initialSlot) {
        if (!m_instances.TryGet(name: instanceName, instance: out var instance) || (instance is null)) {
            return;
        }

        var mirror = new WorldSessionMirror(placeholder: instance.Server.Definition);
        var lease = instance.Server.AttachSink(sink: mirror);
        var entry = new Entry {
            InstanceName = instanceName,
            Mirror = mirror,
            Lease = lease,
            TrackedSlot = initialSlot,
            Emitter = null!,
            FrameSource = null!,
            EnvelopeRegistration = null!,
            // Seeded from the initiating seat's OWN last-reported viewport size (this method runs from Reconcile,
            // BEFORE WorldFrameSource.Dress reports THIS frame's size — the previous frame's report, still the best
            // available estimate) rather than the fixed legacy default, so the FIRST offscreen render this entry
            // ever builds is already close to right instead of guaranteed one ReconcileViewportSizes call stale.
            DesiredWidth = m_seatViewportWidth[initialSlot],
            DesiredHeight = m_seatViewportHeight[initialSlot],
        };

        var emitter = new AwaySeatSceneEmitter(
            mirror: mirror,
            cameraOrbit: m_cameraOrbit,
            trackedTarget: () => new AwaySeatSceneEmitter.TrackedTarget(InstanceSlot: m_seatRouter.Location(slot: entry.TrackedSlot).InstanceSlot, LocalSlot: entry.TrackedSlot),
            cameraOverride: () => m_composition.SelectedCamera
        );
        var borderMargin = new WorldBorderMarginSceneEmitter(mirror: mirror, source: instance.Server.BorderMargin!);
        var frameSource = new SdfCompositionFrameSource(emitters: [emitter, borderMargin], dresser: emitter);

        entry.Emitter = emitter;
        entry.FrameSource = frameSource;

        // The followed instance's own render envelope is configured from this view's construction, mirroring
        // WorldFrameSource's boot-constructor call — an unconfigured envelope admits any document mutation
        // regardless of capacity, which a followed instance someone is actively rendering cannot afford.
        // Candidate-aware: the emitter measures the candidate's own placement/creation rows plus the identical
        // all-avatar probe used to size this frame source; using the frozen capacity instead would compare the
        // ceiling to itself and admit every mutation, including one that overflows the next offscreen rebuild.
        entry.EnvelopeRegistration = instance.Server.Envelope.Configure(
            programWordCapacity: frameSource.WorstCaseProgramWordCapacity,
            instanceCapacity: frameSource.WorstCaseInstanceCapacity,
            measure: emitter.MeasureCandidate
        );

        m_entries[instanceName] = entry;
        ReconcileSessionSources(entry: entry);
        EnsureNestedRegistered(entry: entry);
        EnsureRegistered(entry: entry);

        // Capacity honesty: names the ACTUAL offscreen render size this registration just claimed, not merely the
        // fixed legacy default — the same information a program-word/instance ceiling breach already narrates by
        // name, now extended to the size dimension WorldRenderEnvelope's own measure stays blind to (program
        // words/instances are geometry-shaped and provably independent of viewport pixels — see
        // ReportSeatViewportSize's own remarks on the clamp that keeps this dimension bounded).
        Console.Error.WriteLine(value: $"[world.view: tracking '{instanceName}' for the away-seat render (seat {(initialSlot + 1)}, {entry.DesiredWidth}x{entry.DesiredHeight})]");
    }

    private void Untrack(string instanceName) {
        if (!m_entries.Remove(key: instanceName, value: out var entry)) {
            return;
        }

        m_viewStack?.Release(name: entry.RegistrationName);

        foreach (var feed in entry.SessionFeeds.Values) {
            ReleaseNested(entry: entry, feed: feed);
        }

        entry.SessionFeeds.Clear();
        entry.EnvelopeRegistration.Dispose();
        entry.Lease.Dispose();

        Console.Error.WriteLine(value: $"[world.view: released the away-seat render tracking '{instanceName}']");
    }

    private void EnsureRegistered(Entry entry) {
        if ((m_services is not { } services) || (m_viewStack is not { } stack) || (entry.View is not null)) {
            return;
        }

        // entry.DesiredWidth/DesiredHeight (the largest currently-following seat's own composed viewport, or the
        // fixed default before any seat has ever reported) — never WorldSessionView's own DefaultWidth/DefaultHeight
        // directly, so an away-seat's followed-instance render matches the seat's ACTUAL on-screen size instead of
        // the native 160x144 brick-panel size stretched over it.
        var view = new WorldSessionView(
            services: services,
            hostsOnDirectX: m_hostsOnDirectX,
            frameSource: entry.FrameSource,
            width: entry.DesiredWidth,
            height: entry.DesiredHeight,
            resolveScreenSource: index => ResolveNestedHandle(entry: entry, screenIndex: index)
        );

        _ = stack.Register(name: entry.RegistrationName, content: view, band: ScreenSlotPriority.Ambient);
        entry.View = view;
    }

    /// <summary>Reports local seat <paramref name="slot"/>'s own composed viewport pixel size this produced frame —
    /// called from <see cref="WorldFrameSource.Dress"/> at the SAME point it resolves the seat's periscope camera
    /// (<c>WorldAwaySeatQuad.PeriscopeCamera</c>'s own <c>width</c>/<c>height</c> arguments), the one place this size
    /// is known. Clamped to <c>[1, WorldDefinitionValidator.MaxSurfaceDimension]</c> — the same structural GPU-safety
    /// ceiling every AUTHORED offscreen render dimension in this document model already respects (camera
    /// <c>RenderWidth</c>/<c>RenderHeight</c>, test-pattern/profile dimensions); this size is DERIVED from the live
    /// window rather than authored, but an unbounded derived allocation is exactly as unsafe as an unbounded authored
    /// one, so it earns the identical ceiling rather than a fresh, uncoordinated one. A slot outside the clamp
    /// narrates once via <see cref="ReconcileViewportSizes"/> only when it actually changes a tracked entry's own
    /// target — a seat with no away-tracked instance yet still records its size here so the entry that starts
    /// tracking it a moment later (this same frame, via <see cref="Reconcile"/> having already run before
    /// <see cref="WorldFrameSource.Dress"/>) is built at the right size the first time.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <param name="width">The seat's composed viewport width in pixels.</param>
    /// <param name="height">The seat's composed viewport height in pixels.</param>
    public void ReportSeatViewportSize(int slot, uint width, uint height) {
        if ((uint)slot >= (uint)WorldSeatBindings.SeatCount) {
            return;
        }

        m_seatViewportWidth[slot] = Math.Clamp(value: width, min: 1u, max: (uint)WorldDefinitionValidator.MaxSurfaceDimension);
        m_seatViewportHeight[slot] = Math.Clamp(value: height, min: 1u, max: (uint)WorldDefinitionValidator.MaxSurfaceDimension);
    }

    // How many consecutive ReconcileViewportSizes calls (produced frames) a candidate size must hold before it is
    // committed to DesiredWidth/Height and rebuilt. Large enough that a layout transition's continuously-eased
    // region does not each register as a new target; small enough that a genuine settle is not visibly delayed.
    // Frame-count, never wall-clock, though this is a presentation-side debounce.
    private const int ViewportSettleFrameThreshold = 3;

    /// <summary>Re-targets every tracked entry's own offscreen render to the LARGEST-AREA currently-following seat's
    /// OWN reported viewport size — a real follower's exact request, never a component-wise max across followers
    /// (which can synthesize a size no follower asked for: e.g. 1920x540 and 960x1080 maxing to 1920x1080, an
    /// aspect neither requested). Ties break toward the lowest seat slot, the same tie-break
    /// <see cref="Reconcile"/> already uses to pick a tracked entry's chase target. The chosen follower's shared
    /// render is exact for it; every OTHER follower samples it up or down to their own region, never the reverse.
    /// <para>
    /// A candidate size only commits once it has been reported unchanged for
    /// <see cref="ViewportSettleFrameThreshold"/> consecutive produced frames (see <see cref="Entry.PendingWidth"/>)
    /// — a layout transition eases its region continuously, so the reported pixel size changes on nearly every
    /// produced frame during a reflow (a seat joining or leaving reflows every composed region). Rebuilding on each
    /// of those intermediate frames would release and reconstruct the WorldSessionView + engine dozens of times
    /// during one reflow; a briefly stretched image at the previous extent beats that rebuild storm. Once a
    /// candidate settles, this method releases the registration (disposing the old <see cref="WorldSessionView"/>
    /// and its engine) and rebuilds fresh via <see cref="EnsureRegistered"/> — the same release-and-recreate seam
    /// <see cref="WorldScreenBinder.ReconcileCameras"/> uses for a camera-view dimension change and
    /// <see cref="WorldSessionView.NotifyDeviceLost"/>'s device-loss reset establishes (drop the device-owned state,
    /// rebuild lazily).
    /// </para>
    /// Called once per produced frame from <see cref="RenderViews"/>, AFTER <see cref="WorldFrameSource.Dress"/> has
    /// reported every away seat's size for this frame and BEFORE this pool's own <see cref="ViewStack.RenderFrame"/>
    /// resolves anything, so a committed size change lands before the next image is produced rather than one frame
    /// late.</summary>
    private void ReconcileViewportSizes() {
        if (m_viewStack is not { } stack) {
            return;
        }

        foreach (var entry in m_entries.Values) {
            var bestArea = 0UL;
            var width = 0u;
            var height = 0u;

            for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
                if (!string.Equals(a: m_seatRouter.Location(slot: slot).InstanceName, b: entry.InstanceName, comparisonType: StringComparison.Ordinal)) {
                    continue;
                }

                // Strict '>' (not '>=') keeps the FIRST (lowest-slot) follower on an area tie, mirroring
                // Reconcile's own lowest-slot tie-break for the tracked chase target.
                var area = ((ulong)m_seatViewportWidth[slot] * m_seatViewportHeight[slot]);

                if (area > bestArea) {
                    bestArea = area;
                    width = m_seatViewportWidth[slot];
                    height = m_seatViewportHeight[slot];
                }
            }

            // No seat currently resolves to this instance (a same-frame edge between Reconcile tracking it and
            // Dress reporting a size for it) — keep whatever size is already desired rather than collapsing to 0.
            if ((width == 0u) || (height == 0u)) {
                continue;
            }

            if ((width == entry.DesiredWidth) && (height == entry.DesiredHeight)) {
                // Already at the constructed size — nothing pending (this also clears a stale in-flight count from
                // an overshoot that eased back to rest without ever reaching the settle threshold).
                entry.PendingWidth = width;
                entry.PendingHeight = height;
                entry.StableFrameCount = 0;

                continue;
            }

            if ((width == entry.PendingWidth) && (height == entry.PendingHeight)) {
                entry.StableFrameCount++;
            } else {
                entry.PendingWidth = width;
                entry.PendingHeight = height;
                entry.StableFrameCount = 1;
            }

            if (entry.StableFrameCount < ViewportSettleFrameThreshold) {
                // Still moving (or too freshly arrived to trust) — keep rendering at the previous extent rather than
                // chasing every intermediate frame of the ease.
                continue;
            }

            var previousWidth = entry.DesiredWidth;
            var previousHeight = entry.DesiredHeight;

            entry.DesiredWidth = width;
            entry.DesiredHeight = height;

            if (entry.View is not null) {
                stack.Release(name: entry.RegistrationName);
                entry.View = null;
                EnsureRegistered(entry: entry);

                Console.Error.WriteLine(value: $"[world.view: '{entry.InstanceName}' away-seat render re-targeted {previousWidth}x{previousHeight} -> {width}x{height}]");
            }
        }
    }

    private void ReconcileSessionSources(Entry entry) {
        if (entry.SessionSourceRevision == entry.Mirror.DefinitionRevision) {
            return;
        }

        entry.SessionSourceRevision = entry.Mirror.DefinitionRevision;
        var definition = entry.Mirror.Definition;
        var facets = WorldCreationFacets.Derive(
            definition: definition,
            derivedFaceBase: WorldCreationFacets.DerivedFaceBase,
            derivedFaceScreens: definition.Authoring.DerivedFaceScreens
        );
        var desired = new Dictionary<int, WorldScreenSource.Session>();

        foreach (var screen in definition.Screens.Concat(second: facets.Faces)) {
            if (screen.Source is WorldScreenSource.Session session) {
                desired[screen.Index] = session;
            }
        }

        foreach (var (index, feed) in entry.SessionFeeds.ToArray()) {
            if (desired.TryGetValue(key: index, value: out var source) &&
                string.Equals(a: feed.Destination, b: source.Destination, comparisonType: StringComparison.Ordinal) &&
                string.Equals(a: feed.RequestedCamera, b: source.CameraName, comparisonType: StringComparison.Ordinal)) {
                _ = desired.Remove(key: index);

                continue;
            }

            _ = entry.SessionFeeds.Remove(key: index);
            ReleaseNested(entry: entry, feed: feed);
        }

        if (!m_instances.TryGet(name: entry.InstanceName, instance: out var sourceInstance) || (sourceInstance is null)) {
            return;
        }

        foreach (var (index, session) in desired) {
            if (!m_instances.TryResolveObservedDestination(source: sourceInstance, destinationName: session.Destination, target: out var target, resolved: out var resolved, reason: out var reason) || (target is null)) {
                Console.Error.WriteLine(value: $"[world.view: '{entry.InstanceName}' session screen {index} refused ({reason})]");

                continue;
            }

            var effectiveCamera = ResolveEffectiveCameraName(destinationDefinition: target.Server.Definition, requested: session.CameraName, parentInstanceName: entry.InstanceName, index: index, destinationName: session.Destination);
            var mirror = new WorldSessionMirror(placeholder: target.Server.Definition);
            var lease = target.Server.AttachSink(sink: mirror);
            var emitter = new WorldSessionSceneEmitter(mirror: mirror, effectiveCameraName: effectiveCamera);
            var frameSource = new SdfCompositionFrameSource(emitters: [emitter], dresser: emitter);
            var envelopeRegistration = target.Server.Envelope.Configure(
                programWordCapacity: frameSource.WorstCaseProgramWordCapacity,
                instanceCapacity: frameSource.WorstCaseInstanceCapacity,
                measure: emitter.MeasureCandidate
            );
            var feed = new NestedFeed {
                ScreenIndex = index,
                Destination = session.Destination,
                RequestedCamera = session.CameraName,
                InstanceName = resolved.InstanceName,
                Mirror = mirror,
                Lease = lease,
                Emitter = emitter,
                FrameSource = frameSource,
                EnvelopeRegistration = envelopeRegistration,
            };

            entry.SessionFeeds[index] = feed;
            EnsureNestedRegistered(entry: entry, feed: feed);
            Console.Error.WriteLine(value: $"[world.view: '{entry.InstanceName}' session screen {index} -> destination '{session.Destination}' resolved to instance '{resolved.InstanceName}' generation {resolved.GenerationId}; recursion stops at this child projection]");
        }
    }

    private void EnsureNestedRegistered(Entry entry) {
        foreach (var feed in entry.SessionFeeds.Values) {
            EnsureNestedRegistered(entry: entry, feed: feed);
        }
    }

    private void EnsureNestedRegistered(Entry entry, NestedFeed feed) {
        if ((m_services is not { } services) || (m_viewStack is not { } stack) || (feed.View is not null)) {
            return;
        }

        var view = new WorldSessionView(services: services, hostsOnDirectX: m_hostsOnDirectX, frameSource: feed.FrameSource);

        _ = stack.Register(name: feed.RegistrationName(parentInstanceName: entry.InstanceName), content: view, band: ScreenSlotPriority.Ambient);
        feed.View = view;
    }

    private nint ResolveNestedHandle(Entry entry, int screenIndex) =>
        ((entry.SessionFeeds.TryGetValue(key: screenIndex, value: out var feed) && (m_viewStack is { } stack))
            ? stack.Resolve(name: feed.RegistrationName(parentInstanceName: entry.InstanceName))
            : 0);

    private void ReleaseNested(Entry entry, NestedFeed feed) {
        m_viewStack?.Release(name: feed.RegistrationName(parentInstanceName: entry.InstanceName));
        feed.EnvelopeRegistration.Dispose();
        feed.Lease.Dispose();
    }

    private static string? ResolveEffectiveCameraName(WorldDefinition destinationDefinition, string? requested, string parentInstanceName, int index, string destinationName) {
        if (requested is not { } name) {
            return null;
        }

        foreach (var camera in destinationDefinition.Cameras) {
            if (string.Equals(a: camera.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return name;
            }
        }

        Console.Error.WriteLine(value: $"[world.view: '{parentInstanceName}' session screen {index} -> destination '{destinationName}' names unknown camera '{name}' — falling back to the default projection]");

        return null;
    }

    /// <summary>The image handle currently bound for local seat <paramref name="slot"/>'s own reserved quad — 0
    /// (no signal, the engine's flat/procedural fallback) when the seat is boot-bound, or for the one produced
    /// frame between a transfer landing and the next <see cref="Reconcile"/> call tracking it (self-heals the
    /// following frame).</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    public nint ResolveHandle(int slot) {
        if (m_disposed) {
            return 0;
        }

        var location = m_seatRouter.Location(slot: slot);

        if (string.Equals(a: location.InstanceName, b: WorldInstanceHost.BootInstanceName, comparisonType: StringComparison.Ordinal)) {
            return 0;
        }

        return ((m_entries.TryGetValue(key: location.InstanceName, value: out var entry) && (m_viewStack is { } stack))
            ? stack.Resolve(name: entry.RegistrationName)
            : 0);
    }

    /// <summary>Renders every tracked away view's own offscreen frame — called from
    /// <see cref="WorldFrameSource.RenderViews"/> alongside the jumbotron pool, mirroring
    /// <see cref="WorldScreenBinder.RenderViews"/>'s own shape exactly. The host-context screen resolver remains
    /// no-signal because away content must never sample the boot room's surfaces; its own first-level session
    /// sources are resolved explicitly by the parent <see cref="WorldSessionView"/> from this pool's child views.
    /// Reconciles every tracked entry's own target size (see <see cref="ReconcileViewportSizes"/>) FIRST, so a size
    /// change this produced frame's <see cref="WorldFrameSource.Dress"/> reported lands before anything resolves.</summary>
    public void RenderViews(in FrameContext context, SdfProgram program, int revision, IReadOnlyList<DynamicTransform> transforms, float time, ulong authoritativeTick, SdfFrame hostFrame) {
        if (m_disposed || (m_viewStack is not { } stack)) {
            return;
        }

        ReconcileViewportSizes();

        stack.RenderFrame(context: new ViewRenderContext(
            Host: context,
            HostFrame: hostFrame,
            Program: program,
            ProgramRevision: revision,
            Time: time,
            AuthoritativeTick: authoritativeTick,
            ResolveScreenSource: static _ => 0
        ));
    }

    /// <summary>Drops every away view's device-owned engine and cached handle while preserving registrations and
    /// CPU-side mirrors. The next render recreates them through <see cref="WorldSessionView"/>.</summary>
    public void NotifyDeviceLost() {
        if (!m_disposed) {
            m_viewStack?.NotifyDeviceLost();
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var entry in m_entries.Values) {
            foreach (var feed in entry.SessionFeeds.Values) {
                feed.EnvelopeRegistration.Dispose();
                feed.Lease.Dispose();
            }

            entry.EnvelopeRegistration.Dispose();
            entry.Lease.Dispose();
        }

        m_entries.Clear();
        m_releaseScratch.Clear();
        m_viewStack?.Dispose();
        m_viewStack = null;
        m_services = null;
    }

    // A fixed-value seed for the per-seat reported-size arrays — WorldSeatBindings.SeatCount entries, all starting at
    // the legacy fixed default so a slot never reports a degenerate 0x0 before its first ReportSeatViewportSize call.
    private static uint[] CreateSeatDimensionArray(uint value) {
        var values = new uint[WorldSeatBindings.SeatCount];

        Array.Fill(array: values, value: value);

        return values;
    }
}
