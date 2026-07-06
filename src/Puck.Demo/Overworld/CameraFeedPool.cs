using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.Demo.Creator;
using Puck.Demo.World;
using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>
/// The overworld's diegetic-feed pool — the successor that WIRES the landed camera primitive into the live render
/// path. It owns the presentation-only feed sources every screen surface can display beyond a booted cabinet: the
/// procedural face feed (the default screen-faced-creation face) and a small POOL of up to
/// <see cref="MaxCameraFeeds"/> live offscreen world renders (posed camera EYES), each a tiny
/// <see cref="SdfWorldEngine"/> at the native brick panel size (160x144). It resolves the wiring model (a screen
/// names a source; a source resolves to an image-view handle) through a small NAMED-FEED registry, so a screen wired
/// to <c>named:emotes</c>, a creation camera's <c>named:lure</c>, or any host feed all resolve the same way — no
/// consumer-specific channel exists (see [[abstractions-not-specifics]]).
/// <para>
/// COST MODEL: each active feed is one additional 160x144 world render pass per produced frame (a full VM
/// interpretation per pixel of the same program the room renders). The budget is deliberately small — a security
/// wall of four angles, a mirror, a couple of creature lenses. When more eyes are wired than the pool can serve, the
/// extras are DROPPED with a narration (the surface wired to a dropped feed falls back to its flat material), never a
/// silent cost blow-up.
/// </para>
/// <para>
/// COMPOSITION: <see cref="OverworldFrameSource"/> owns one of these and drives it in two halves, matching the
/// engine's own frame pacing. Each produced frame, on the RENDER thread (a live GPU device), <see cref="TickFeeds"/>
/// lazily builds the pool's feed engines, ticks the procedural face feed, and renders every active feed from the eye
/// poses the previous <see cref="PlanFeeds"/> resolved — a deliberate one-frame lag that is exactly the diegetic-CRT
/// read the primitive's self-reference rule expects. Inside <c>CaptureFrame</c>, <see cref="PlanFeeds"/> counts the
/// distinct camera feeds the wiring wants, assigns each an eye pose + pool slot, registers the screen claimant, and
/// records which screens are self-wired (bind 0 in their own feed). The frame source's generic screen-claimant seam
/// (<see cref="OverworldFrameSource.RegisterScreenClaimant"/>) carries the whole thing into the render node without
/// the node ever naming a feed type.
/// </para>
/// <para>
/// THE SELF-REFERENCE RULE (generalized): inside feed F's OWN program, any screen surface currently wired to feed F
/// binds 0 (the flat/procedural fallback) — a feed never samples the image it is itself writing, which would compound
/// every frame. Every OTHER feed and surface binds normally, so one-frame-lag TV-in-TV chains (a feed showing a screen
/// that shows a DIFFERENT feed) are legal and desirable. <see cref="PlanFeeds"/> records, per feed, which screen
/// indices are wired to THAT feed (<c>selfWiredScreens</c>) plus the resolver for every OTHER source, and
/// <see cref="TickFeeds"/> muxes accordingly.
/// </para>
/// </summary>
internal sealed class CameraFeedPool : IDisposable {
    /// <summary>The most simultaneous camera feeds the pool renders. A deliberately small budget: each feed is a full
    /// 160x144 world render pass per frame (see the cost model), so this bounds the extra GPU cost the diegetic camera
    /// primitive can ever add. More wired eyes than this narrate a degrade (see <see cref="PlanFeeds"/>).</summary>
    public const int MaxCameraFeeds = 4;

    /// <summary>A feed's fixed render width — the native brick panel size, matching every other diegetic screen source
    /// in the overworld (KEEP IN SYNC with the overworld's 160:144 screen authoring).</summary>
    public const uint FeedWidth = 160;
    /// <summary>A feed's fixed render height.</summary>
    public const uint FeedHeight = 144;

    // One resolved feed this frame: the pool slot it renders into, the eye that poses it, the feed's name (for the
    // registry), and the screen indices wired to it (self-reference set + the room-glow read).
    private readonly record struct PlannedFeed(int PoolSlot, CameraEye Eye, float AnchorYaw, Vector3 AnchorPosition, string Name, IReadOnlySet<int> WiredScreens);

    private readonly ProceduralFeed m_faceFeed = new();
    private readonly IServiceProvider m_services;
    private readonly bool m_hostsOnDirectX;
    private readonly int m_programWordCapacity;
    private readonly int m_instanceCapacity;
    private readonly int m_dynamicTransformCapacity;
    private readonly SdfWorldEngine?[] m_feeds = new SdfWorldEngine?[MaxCameraFeeds];
    private SdfWorldKernels? m_kernels;
    private SdfProgram? m_currentProgram;
    private int m_lastUploadedRevision = -1;
    private int m_activeFeedCount;
    // This frame's plan (produced in PlanFeeds, consumed in TickFeeds one frame later). Empty when nothing is wired.
    private IReadOnlyList<PlannedFeed> m_plan = [];
    // The named-feed handle map for THIS frame's screen-source resolution: feed name -> its live image-view handle.
    // Rebuilt every PlanFeeds so a screen wired by name resolves to whatever feed currently carries that name.
    private readonly Dictionary<string, nint> m_namedHandles = new(comparer: StringComparer.Ordinal);
    // The named-feed glow map (parallel to the handles) — a feed's average color for the room-light seam. Camera feeds
    // report zero (their light is the world they film, already lit); the face feed reports its own average.
    private readonly Dictionary<string, Vector3> m_namedLights = new(comparer: StringComparer.Ordinal);
    private bool m_disposed;

    /// <summary>Initializes the pool against the main engine's worst-case capacity envelope so a feed's own program
    /// upload never throws when the shared program grows within that ceiling.</summary>
    /// <param name="services">The application services (resolves the neutral GPU compute factories).</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (selects the kernel bytecode).</param>
    /// <param name="programWordCapacity">The main engine's probed program-word floor.</param>
    /// <param name="instanceCapacity">The main engine's probed instance floor.</param>
    /// <param name="dynamicTransformCapacity">The main engine's dynamic-transform slot count.</param>
    public CameraFeedPool(IServiceProvider services, bool hostsOnDirectX, int programWordCapacity, int instanceCapacity, int dynamicTransformCapacity) {
        ArgumentNullException.ThrowIfNull(services);

        m_services = services;
        m_hostsOnDirectX = hostsOnDirectX;
        m_programWordCapacity = programWordCapacity;
        m_instanceCapacity = instanceCapacity;
        m_dynamicTransformCapacity = dynamicTransformCapacity;
    }

    /// <summary>How many camera feeds are live this frame (the pool's occupancy) — 0 until the first wired feed is
    /// planned and its engine built.</summary>
    public int ActiveFeedCount => m_activeFeedCount;

    /// <summary>The image-view handle for the named feed <paramref name="name"/> this frame, or 0 when no feed
    /// currently carries that name (or its feed is inactive) — the flat/procedural fallback. The frame source's
    /// generic screen-source seam calls this for a <c>named:</c> wire.</summary>
    /// <param name="name">The feed name.</param>
    public nint ResolveNamedFeedHandle(string name) =>
        (m_namedHandles.TryGetValue(key: name, value: out var handle) ? handle : 0);

    /// <summary>The room-glow color for the named feed <paramref name="name"/> this frame, or zero when none carries
    /// it (or it reports no light).</summary>
    /// <param name="name">The feed name.</param>
    public Vector3 ResolveNamedFeedLight(string name) =>
        (m_namedLights.TryGetValue(key: name, value: out var light) ? light : Vector3.Zero);

    /// <summary>Whether the named feed <paramref name="name"/> is producing pixels this frame (a non-zero handle) —
    /// the companion face auto-tune's "preferred remote feed is live" probe.</summary>
    /// <param name="name">The feed name.</param>
    public bool IsNamedFeedLive(string name) =>
        (m_namedHandles.TryGetValue(key: name, value: out var handle) && (handle != 0));

    /// <summary>The procedural face feed's current expression setter (the default face feed's content — proximity /
    /// boot events pick the expression through this).</summary>
    /// <param name="expression">The expression to show on the next tick.</param>
    public void SetFaceExpression(FaceExpression expression) =>
        m_faceFeed.SetExpression(expression: expression);

    /// <summary>Plans this frame's camera feeds from the eyes the wiring wants live and records the named-feed handle
    /// map (from the PREVIOUS tick's rendered handles, so a name resolves to a real image this frame). Called inside
    /// <c>CaptureFrame</c>, after the wiring is known. Returns nothing — the plan is stashed for <see cref="TickFeeds"/>
    /// and the named-feed maps are refreshed for the frame source's screen-source resolution.</summary>
    /// <param name="requestedFeeds">The distinct camera feeds the wiring wants active this frame, each an (eye pose,
    /// feed name, wired-screen set). More than <see cref="MaxCameraFeeds"/> narrates a degrade.</param>
    public void PlanFeeds(IReadOnlyList<CameraFeedRequest> requestedFeeds) {
        ArgumentNullException.ThrowIfNull(requestedFeeds);

        // The named-feed maps are rebuilt from the CURRENT live handles: the procedural face feed's last-published
        // image (the default face feed), plus each planned camera feed's output handle from the pool. A name resolves
        // to a real handle THIS frame; the pixels are last frame's (the diegetic one-frame lag).
        m_namedHandles.Clear();
        m_namedLights.Clear();
        m_namedHandles[CompanionState.DefaultFaceFeed] = m_faceFeed.CurrentImageViewHandle;
        m_namedLights[CompanionState.DefaultFaceFeed] = m_faceFeed.CurrentImageViewHandle != 0 ? FaceFeedGlow : Vector3.Zero;

        var granted = Math.Min(val1: requestedFeeds.Count, val2: MaxCameraFeeds);
        var plan = new List<PlannedFeed>(capacity: granted);

        for (var index = 0; (index < granted); index++) {
            var request = requestedFeeds[index];

            plan.Add(item: new PlannedFeed(
                AnchorPosition: request.AnchorPosition,
                AnchorYaw: request.AnchorYaw,
                Eye: request.Eye,
                Name: request.Name,
                PoolSlot: index,
                WiredScreens: request.WiredScreens
            ));

            // The named handle for a camera feed is the pool slot's OUTPUT from the last tick (0 until first rendered).
            m_namedHandles[request.Name] = FeedOutputHandle(feedIndex: index);
        }

        m_plan = plan;
    }

    /// <summary>Renders this frame's planned camera feeds and ticks the procedural face feed — the render-thread half,
    /// called from the frame source's <c>TickBakePreview</c>-adjacent seam (a live GPU device). Lazily builds the pool's
    /// feed engines on the first frame a feed is wanted (nothing is paid while no camera is wired). A no-op when the
    /// device does not resolve this frame (retried next frame).</summary>
    /// <param name="context">The frame context (its host resolves the live GPU device).</param>
    /// <param name="program">The main frame source's CURRENT world program.</param>
    /// <param name="revision">The main frame source's program revision counter.</param>
    /// <param name="time">The frame's content time (seconds) — the feeds see the same animated world.</param>
    /// <param name="dynamicTransforms">The per-frame dynamic-transform list, identical to the main engine's.</param>
    /// <param name="resolveScreenSource">Resolves a screen index to its bound handle for a feed's own render (the
    /// wiring model minus the feed's own self-wired screens).</param>
    /// <param name="faceFeedNeeded">Whether any consumer wants the default (procedural) face feed this frame (a
    /// screen-faced creation, or a screen wired to it) — the face feed only ticks/uploads when needed, so a plain room
    /// with no companions pays nothing (and no GPU upload leaks into a feed-free run).</param>
    public void TickFeeds(in Puck.Hosting.FrameContext context, SdfProgram program, int revision, float time, IReadOnlyList<DynamicTransform> dynamicTransforms, Func<int, nint> resolveScreenSource, bool faceFeedNeeded) {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(dynamicTransforms);
        ArgumentNullException.ThrowIfNull(resolveScreenSource);

        if ((!faceFeedNeeded && (m_plan.Count == 0)) ||
            m_services.GetService(serviceType: typeof(IGpuComputeServices)) is not IGpuComputeServices gpu ||
            !context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device)) {
            return; // Nothing to do this frame (no face feed needed, no camera feed wired), or the device isn't ready.
        }

        // The procedural face feed publishes its default-face image (blink cadence + expression changes) only while a
        // consumer wants it — a room with no screen-faced creation never uploads it.
        if (faceFeedNeeded) {
            m_faceFeed.Tick(deltaSeconds: (float)context.DeltaSeconds, device: device, gpu: gpu);
        }

        if (m_plan.Count == 0) {
            return; // No camera feed wired — the pool stays unbuilt (pays nothing).
        }

        EnsureFeeds(device: device, gpu: gpu, program: program, count: m_plan.Count);
        Rebuild(program: program, revision: revision);

        foreach (var feed in m_plan) {
            var (eye, target) = feed.Eye.Resolve(anchorPosition: feed.AnchorPosition, anchorYaw: feed.AnchorYaw);

            RenderFeed(
                dynamicTransforms: dynamicTransforms,
                eye: eye,
                feedIndex: feed.PoolSlot,
                fieldOfViewRadians: feed.Eye.EffectiveFieldOfViewRadians,
                resolveScreenSource: resolveScreenSource,
                selfWiredScreens: feed.WiredScreens,
                target: target,
                time: time
            );
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_faceFeed.Dispose();

        for (var index = 0; (index < m_feeds.Length); index++) {
            m_feeds[index]?.Dispose();
            m_feeds[index] = null;
        }
    }

    // A soft neutral glow for the default face feed's room-light contribution (the CRT face casts a faint light) —
    // the camera feeds report no light of their own (they film an already-lit world).
    private static readonly Vector3 FaceFeedGlow = new(0.10f, 0.16f, 0.12f);

    // The output image view for pool slot `feedIndex` — 0 for a feed index that is not currently active (unbuilt or
    // beyond the active count); the caller's wiring resolver treats a zero handle as "no signal" (the flat/procedural
    // fallback), exactly like an unbound screen source.
    private nint FeedOutputHandle(int feedIndex) =>
        (((feedIndex >= 0) && (feedIndex < m_feeds.Length) && (m_feeds[feedIndex] is { } engine)) ? engine.OutputImageViewHandle : 0);

    // Re-uploads the shared world program into every ACTIVE feed when the main frame source's revision counter has
    // advanced since the last call — a no-op otherwise. A feed built later (as the active count grows) uploads the
    // current program at build time in EnsureFeeds, so a freshly-built feed is never stale.
    private void Rebuild(SdfProgram program, int revision) {
        m_currentProgram = program;

        if (revision == m_lastUploadedRevision) {
            return;
        }

        for (var index = 0; (index < m_activeFeedCount); index++) {
            m_feeds[index]?.UploadProgram(program: program);
        }

        m_lastUploadedRevision = revision;
    }

    // Renders pool slot `feedIndex` once from a posed eye, FIRE-AND-FORGET (the host's frame pacing orders it against
    // the previous frame's reads, exactly like SdfEngineNode's own submit use). Enforces the generalized self-reference
    // rule per feed: every screen index the shared program declares is muxed through `resolveScreenSource` EXCEPT the
    // ones in `selfWiredScreens` (wired to THIS feed), which bind 0 so the feed never samples the image it is writing.
    // One-frame lag between this call and the pixels FeedOutputHandle shows is expected and desirable (diegetic CRTs
    // read slightly behind).
    private void RenderFeed(int feedIndex, Vector3 eye, Vector3 target, float fieldOfViewRadians, float time, IReadOnlyList<DynamicTransform> dynamicTransforms, Func<int, nint> resolveScreenSource, IReadOnlySet<int> selfWiredScreens) {
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
            Program: m_currentProgram!,
            ProgramChanged: false,
            Time: time,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            WarpAmount: 0f
        ) {
            DynamicTransforms = dynamicTransforms,
        };

        engine.SubmitFrame(frame: frame);
    }

    // Sets how many feeds the pool should serve this frame, building any newly-needed feed engines lazily (each
    // uploads the current program at build time) and DEGRADING a request beyond MaxCameraFeeds to the budget — the
    // narration says so. Feeds are never torn down mid-run (a feed that stops being wired simply is not rendered); the
    // pool holds its built engines so a re-wire costs nothing.
    private void EnsureFeeds(IGpuDeviceContext device, IGpuComputeServices gpu, SdfProgram program, int count) {
        var granted = Math.Clamp(value: count, max: MaxCameraFeeds, min: 0);

        if (count > MaxCameraFeeds) {
            Console.Error.WriteLine(value: $"[camera-feeds] {count} feed(s) wired but the pool budget is {MaxCameraFeeds} — {count - MaxCameraFeeds} dropped (their screens fall back to the flat material)");
        }

        m_kernels ??= SdfWorldKernels.Load(bytecodeExtension: SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: m_hostsOnDirectX), directory: DemoShaders.SdfDirectory);
        m_currentProgram ??= program;

        for (var index = 0; (index < granted); index++) {
            if (m_feeds[index] is not null) {
                continue;
            }

            m_feeds[index] = new SdfWorldEngine(
                device: device,
                gpu: gpu,
                height: FeedHeight,
                kernels: m_kernels.Value,
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

        m_activeFeedCount = granted;
    }
}

/// <summary>One camera feed the wiring wants live this frame — its posed eye, its live anchor frame (resolved by the
/// frame source against the world/companion/placement it rides), the feed's name (the wiring handle), and the screen
/// indices wired to it (the self-reference set + the room-glow read). A value type: the frame source builds a fresh
/// list each <c>CaptureFrame</c>.</summary>
/// <param name="Eye">The camera eye (its stored offset pose + anchor kind + fov).</param>
/// <param name="AnchorPosition">The eye's live anchor world-space origin (the shape/placement it rides, or zero for a
/// world-anchored eye).</param>
/// <param name="AnchorYaw">The eye's live anchor heading, radians (zero for a world-anchored eye).</param>
/// <param name="Name">The feed's name (the wiring handle a screen names to show it).</param>
/// <param name="WiredScreens">The screen indices wired to THIS feed — they bind 0 in the feed's own program (the
/// self-reference rule) so a feed never samples the image it is writing.</param>
internal readonly record struct CameraFeedRequest(
    CameraEye Eye,
    Vector3 AnchorPosition,
    float AnchorYaw,
    string Name,
    IReadOnlySet<int> WiredScreens
);
