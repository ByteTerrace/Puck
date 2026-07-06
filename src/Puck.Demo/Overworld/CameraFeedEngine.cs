using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>
/// The diegetic camera FEED pool — the generalized successor to the single hardcoded lure lens. A camera feed is a live
/// offscreen render of the SAME world program the main engine renders, from a posed eye the caller supplies per frame;
/// its output is a diegetic screen source (bind a feed's <see cref="FeedOutputHandle"/> into a room screen slot via the
/// wiring model). This engine owns a small POOL of up to <see cref="MaxCameraFeeds"/> such feeds, each a tiny
/// <see cref="SdfWorldEngine"/> at the native brick panel size (160x144). The developer places camera EYES anywhere and
/// wires their feeds onto any screen exactly like screens themselves — the hundredth camera costs a verb, not a
/// redesign — up to the pool budget, beyond which the request narrates a degrade rather than growing the render cost
/// silently.
/// <para>
/// COST MODEL: each active feed is one additional 160x144 world render pass per produced frame (a full VM
/// interpretation per pixel of the same program the room renders). The budget is deliberately small — a security wall
/// of four angles, a mirror, a couple of creature lenses. When more eyes are wired than the pool can serve, the extras
/// are DROPPED with a narration (the surface wired to a dropped feed falls back to its flat material), never a silent
/// cost blow-up.
/// </para>
/// <para>
/// Constructed with the SAME worst-case capacity envelope as the main engine (the frame source's probe — see the
/// sdf-world skill's "capacity probe" contract) so each feed's own <see cref="SdfWorldEngine.UploadProgram"/> never
/// throws when the shared program grows within that envelope. <see cref="Rebuild"/> re-uploads every active feed
/// whenever the main frame source's revision counter advances (the SAME program object, so a feed always shows the
/// CURRENT world) — cheap to call every frame; it no-ops when the revision is unchanged.
/// </para>
/// <para>
/// THE SELF-REFERENCE RULE (generalized): inside feed F's OWN program, any screen surface currently wired to feed F
/// binds 0 (the flat/procedural fallback) — a feed never samples the image it is itself writing, which would compound
/// every frame. Every OTHER feed and surface binds normally, so one-frame-lag TV-in-TV chains (a feed showing a screen
/// that shows a DIFFERENT feed) are legal and desirable. The caller declares, per feed, which screen indices are wired
/// to THAT feed (<see cref="RenderFeed"/>'s <c>selfWiredScreens</c>) plus the resolver for every OTHER source, and the
/// engine muxes accordingly.
/// </para>
/// </summary>
public sealed class CameraFeedEngine : IDisposable {
    /// <summary>The most simultaneous camera feeds the pool renders. A deliberately small budget: each feed is a full
    /// 160x144 world render pass per frame (see the cost model), so this bounds the extra GPU cost the diegetic camera
    /// primitive can ever add. More wired eyes than this narrate a degrade (see <see cref="SetActiveFeedCount"/>).</summary>
    public const int MaxCameraFeeds = 4;

    /// <summary>A feed's fixed render width — the native brick panel size, matching every other diegetic screen source
    /// in the overworld (KEEP IN SYNC with the overworld's 160:144 screen authoring).</summary>
    public const uint FeedWidth = 160;
    /// <summary>A feed's fixed render height.</summary>
    public const uint FeedHeight = 144;

    private readonly IGpuComputeServices m_gpu;
    private readonly IGpuDeviceContext m_device;
    private readonly SdfWorldKernels m_kernels;
    private readonly int m_dynamicTransformCapacity;
    private readonly int m_programWordCapacity;
    private readonly int m_instanceCapacity;
    private readonly SdfWorldEngine?[] m_feeds = new SdfWorldEngine?[MaxCameraFeeds];
    private SdfProgram m_currentProgram;
    private int m_lastUploadedRevision = -1;
    private int m_activeFeedCount;
    private bool m_disposed;

    /// <summary>Initializes the pool against the SAME worst-case capacity envelope the main engine was built from —
    /// pass the identical probe-derived numbers (see <see cref="SdfWorldEngineOptions.ProgramWordCapacity"/>/
    /// <see cref="SdfWorldEngineOptions.InstanceCapacity"/>/<see cref="SdfWorldEngineOptions.DynamicTransformCapacity"/>)
    /// so a future world rebuild that still fits the main engine's buffers also fits every feed's. No feed engine is
    /// created up front; <see cref="SetActiveFeedCount"/> lazily builds exactly as many as the current wiring needs, so
    /// a world with zero wired cameras pays nothing.</summary>
    /// <param name="gpu">The neutral GPU compute services (the same bundle the host resolves for the main engine).</param>
    /// <param name="device">The shared GPU device context.</param>
    /// <param name="kernels">The compiled world kernel set (SPIR-V or DXIL, matching the host backend).</param>
    /// <param name="initialProgram">The world program every feed uploads at construction (the main frame source's current program).</param>
    /// <param name="programWordCapacity">The FLOOR on each feed's program-buffer packed-word capacity — the main engine's probe envelope.</param>
    /// <param name="instanceCapacity">The FLOOR on each feed's per-tile mask instance capacity — the main engine's probe envelope.</param>
    /// <param name="dynamicTransformCapacity">The FLOOR on each feed's dynamic-transform slot capacity — the main engine's probe envelope.</param>
    public CameraFeedEngine(IGpuComputeServices gpu, IGpuDeviceContext device, SdfWorldKernels kernels, SdfProgram initialProgram, int programWordCapacity, int instanceCapacity, int dynamicTransformCapacity) {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(initialProgram);

        m_gpu = gpu;
        m_device = device;
        m_kernels = kernels;
        m_programWordCapacity = programWordCapacity;
        m_instanceCapacity = instanceCapacity;
        m_dynamicTransformCapacity = dynamicTransformCapacity;
        m_currentProgram = initialProgram;
    }

    /// <summary>How many feeds are currently live (built and rendering) — the pool's occupancy. Grows as cameras are
    /// wired (up to <see cref="MaxCameraFeeds"/>); a request beyond it is the degrade case (see
    /// <see cref="SetActiveFeedCount"/>).</summary>
    public int ActiveFeedCount => m_activeFeedCount;

    /// <summary>The output image view for feed <paramref name="feedIndex"/> — bind this into a ROOM screen slot through
    /// the wiring model. Zero for a feed index that is not currently active (unbuilt or beyond the active count); the
    /// caller's wiring resolver should treat a zero handle as "no signal" (the flat/procedural fallback), exactly like
    /// an unbound screen source.</summary>
    /// <param name="feedIndex">The pool feed index (0..<see cref="ActiveFeedCount"/> - 1 for a live handle).</param>
    /// <returns>The feed's output image view handle, or 0 when the feed is not active.</returns>
    public nint FeedOutputHandle(int feedIndex) =>
        (((feedIndex >= 0) && (feedIndex < m_feeds.Length) && (m_feeds[feedIndex] is { } engine)) ? engine.OutputImageViewHandle : 0);

    /// <summary>Re-uploads the shared world program into every ACTIVE feed when the main frame source's revision
    /// counter has advanced since the last call — a no-op otherwise. Call this once per produced frame, before any
    /// <see cref="RenderFeed"/>. A feed built later (as the active count grows) uploads the current program at build
    /// time in <see cref="SetActiveFeedCount"/>, so a freshly-built feed is never stale.</summary>
    /// <param name="program">The main frame source's CURRENT program (must fit the envelope this pool was constructed with).</param>
    /// <param name="revision">The main frame source's revision counter (any monotonically-changing value; only
    /// (in)equality with the last-seen value matters).</param>
    public void Rebuild(SdfProgram program, int revision) {
        ArgumentNullException.ThrowIfNull(program);

        m_currentProgram = program;

        if (revision == m_lastUploadedRevision) {
            return;
        }

        for (var index = 0; (index < m_activeFeedCount); index++) {
            m_feeds[index]?.UploadProgram(program: program);
        }

        m_lastUploadedRevision = revision;
    }

    /// <summary>Sets how many feeds the pool should serve this frame, building any newly-needed feed engines lazily
    /// (each uploads the current program at build time) and DEGRADING a request beyond <see cref="MaxCameraFeeds"/> to
    /// the budget — the narration says so. Feeds are never torn down mid-run (a feed that stops being wired simply is
    /// not rendered); the pool holds its built engines so a re-wire costs nothing.</summary>
    /// <param name="requested">How many feeds the current wiring wants active.</param>
    /// <param name="narration">A one-line degrade narration when <paramref name="requested"/> exceeds the budget, else
    /// null.</param>
    /// <returns>The granted active feed count (min of the request and the budget).</returns>
    public int SetActiveFeedCount(int requested, out string? narration) {
        narration = null;

        var granted = Math.Clamp(value: requested, max: MaxCameraFeeds, min: 0);

        if (requested > MaxCameraFeeds) {
            narration = $"[camera-feeds] {requested} feed(s) wired but the pool budget is {MaxCameraFeeds} — {requested - MaxCameraFeeds} dropped (their screens fall back to the flat material)";
        }

        EnsureFeeds(count: granted);
        m_activeFeedCount = granted;

        return granted;
    }

    /// <summary>Renders feed <paramref name="feedIndex"/> once from a posed eye, FIRE-AND-FORGET (the host's frame
    /// pacing orders it against the previous frame's reads, exactly like <see cref="SdfEngineNode"/>'s own submit use).
    /// Enforces the generalized self-reference rule per feed: every screen index the shared program declares is muxed
    /// through <paramref name="resolveScreenSource"/> EXCEPT the ones in <paramref name="selfWiredScreens"/> (wired to
    /// THIS feed), which bind 0 so the feed never samples the image it is writing. One-frame lag between this call and
    /// the pixels <see cref="FeedOutputHandle"/> shows is expected and desirable (diegetic CRTs read slightly behind).</summary>
    /// <param name="feedIndex">The pool feed index to render (a no-op for an inactive index).</param>
    /// <param name="eye">The world-space eye position.</param>
    /// <param name="target">The world-space look-at target.</param>
    /// <param name="fieldOfViewRadians">The eye's vertical field of view.</param>
    /// <param name="time">The frame's time value (seconds) — forwarded into the rendered view exactly like the main engine's frame.</param>
    /// <param name="dynamicTransforms">The per-frame dynamic-entity transform list, IDENTICAL to what the main engine
    /// renders this frame (a feed sees the same moving world).</param>
    /// <param name="resolveScreenSource">Resolves a screen index the shared program declares to its bound image-view
    /// handle for THIS feed's render (a brick framebuffer, ANOTHER feed's output, a named host feed, or 0 for the flat
    /// fallback) — the wiring model, minus this feed's own self-wired screens.</param>
    /// <param name="selfWiredScreens">The screen indices currently wired to THIS feed: they bind 0 in this feed's own
    /// program (the self-reference rule). An empty set means no screen shows this feed.</param>
    public void RenderFeed(int feedIndex, Vector3 eye, Vector3 target, float fieldOfViewRadians, float time, IReadOnlyList<DynamicTransform> dynamicTransforms, Func<int, nint> resolveScreenSource, IReadOnlySet<int> selfWiredScreens) {
        ArgumentNullException.ThrowIfNull(resolveScreenSource);
        ArgumentNullException.ThrowIfNull(selfWiredScreens);

        if ((feedIndex < 0) || (feedIndex >= m_activeFeedCount) || (m_feeds[feedIndex] is not { } engine)) {
            return;
        }

        // Mux every screen surface the shared program COULD declare: a self-wired screen binds 0 (no feedback), every
        // other resolves through the wiring model. Re-applied each frame — cheap host-side state, matching the engine's
        // per-frame screen-source polling seam.
        for (var screenIndex = 0; (screenIndex < SdfProgramBuilder.MaxScreenSurfaces); screenIndex++) {
            var handle = (selfWiredScreens.Contains(item: screenIndex) ? 0 : resolveScreenSource(arg: screenIndex));

            engine.SetScreenSource(screenIndex: screenIndex, imageViewHandle: handle);
        }

        var camera = CameraSnapshot.LookAt(
            fieldOfViewRadians: fieldOfViewRadians,
            position: eye,
            target: target,
            viewportHeight: FeedHeight,
            viewportWidth: FeedWidth
        );
        var frame = new SdfFrame(
            // Rebuild (via UploadProgram) is the single owner of the live program; SubmitFrame never re-uploads, so this
            // is carried only to satisfy SdfFrame's shape — always the last program Rebuild uploaded.
            Program: m_currentProgram,
            ProgramChanged: false,
            Time: time,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            WarpAmount: 0f
        ) {
            DynamicTransforms = dynamicTransforms,
        };

        engine.SubmitFrame(frame: frame);
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        for (var index = 0; (index < m_feeds.Length); index++) {
            m_feeds[index]?.Dispose();
            m_feeds[index] = null;
        }
    }

    // Builds feed engines up to `count`, each uploading the current program at construction so it is never stale. Feeds
    // once built are retained (never torn down mid-run) so a re-wire is free; this only ever grows the pool.
    private void EnsureFeeds(int count) {
        for (var index = 0; (index < count); index++) {
            if (m_feeds[index] is not null) {
                continue;
            }

            m_feeds[index] = new SdfWorldEngine(
                device: m_device,
                gpu: m_gpu,
                height: FeedHeight,
                kernels: m_kernels,
                options: new SdfWorldEngineOptions(
                    DynamicTransformCapacity: m_dynamicTransformCapacity,
                    InstanceCapacity: m_instanceCapacity,
                    Program: m_currentProgram,
                    ProgramWordCapacity: m_programWordCapacity,
                    ViewportCapacity: 1
                ),
                width: FeedWidth
            );
        }
    }
}
