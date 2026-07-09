using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Capture;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>One screen surface's world-space sampling frame for one frame — the polled counterpart of
/// <see cref="SdfWorldEngine.SetScreenSurface"/>'s parameters, bundled so a transform provider returns one value.</summary>
/// <param name="Origin">The front face's world-space center this frame.</param>
/// <param name="Right">The world-space axis the UV's U increases along this frame (need not be pre-normalized).</param>
/// <param name="Up">The world-space axis the UV's V increases against this frame (need not be pre-normalized).</param>
/// <param name="HalfWidth">The half-extent along <paramref name="Right"/> this frame.</param>
/// <param name="HalfHeight">The half-extent along <paramref name="Up"/> this frame.</param>
public readonly record struct SdfScreenSurfaceTransform(Vector3 Origin, Vector3 Right, Vector3 Up, float HalfWidth, float HalfHeight);

/// <summary>
/// The SDF engine as a host-model <see cref="IRenderNode"/>: a generic multi-viewport SDF world compositor driven by
/// COMPUTE, fully BACKEND-NEUTRAL (it depends only on the neutral <c>IGpuCompute*</c> seam, so the identical node runs
/// on whichever backend the host publishes). It resolves the shared device from <see cref="FrameContext.Host"/>,
/// pulls each frame's scene + cameras + regions from an <see cref="ISdfFrameSource"/>, and drives the shared
/// <see cref="SdfWorldEngine"/> core in its fire-and-forget mode (the host's frame pacing orders the frames).
/// <para>
/// Rendering is TWO-STAGE so the compositor is SOURCE-AGNOSTIC. <c>sdf-beam.comp</c> cone-marches the field per tile
/// to a conservative march-start depth; <c>sdf-world-views.comp</c> (Stage 1) renders each viewport's SDF camera
/// into its own rect-sized <em>source</em> texture; <c>sdf-world-composite.comp</c> (Stage 2) places each source —
/// an SDF view, or a child node's output bound into the same slot — into its screen region by a 1:1 copy.
/// The viewport count follows <see cref="SdfFrame.Views"/>; nothing about the scene, cameras, or layout is baked in.
/// </para>
/// <para>
/// DIEGETIC SCREENS ride a separate, SHADING-ONLY seam: a program may declare up to 8 static screen surfaces (see
/// <see cref="SdfProgramBuilder"/>'s screen-surface <c>ScreenSlab</c> overload), and this node polls the
/// <c>screenSources</c> constructor argument each frame to bind (or unbind) each one's sampled image — unlike a
/// child, this never adds or replaces a viewport; it only changes how one shape's LIT face shades. A screen's
/// world-space sampling FRAME is normally set once at program build; a screen riding a dynamic transform instead
/// supplies a <c>screenSurfaceTransforms</c> provider, polled every frame right after <c>screenLights</c>, so its
/// sampling frame tracks the geometry the dynamic transform already moved (see <see cref="SdfWorldEngine.SetScreenSurface"/>).
/// </para>
/// </summary>
public sealed class SdfEngineNode : IRenderNode {
    private const ulong TimingReportInterval = 60; // print the digest roughly once per second at 60 fps

    // Concrete Dictionary<,> (not the read-only interface) so the per-frame foreach binds the struct enumerator
    // instead of boxing IEnumerator on the render thread every ProduceFrame; the ctor copies caller maps to match.
    private static readonly Dictionary<int, IRenderNode> EmptyChildren = new();
    private static readonly Dictionary<int, Func<nint>> EmptyScreenSources = new();
    private static readonly Dictionary<int, Func<Vector3>> EmptyScreenLights = new();
    private static readonly Dictionary<int, Func<SdfScreenSurfaceTransform?>> EmptyScreenSurfaceTransforms = new();

    private readonly string? m_capturePath;
    private readonly Dictionary<int, IRenderNode> m_children;
    private readonly Func<IGpuDeviceContext, IGpuStorageImage>? m_createStorageImage;
    private readonly int m_dynamicTransformCapacity;
    private readonly NodeDescriptor m_descriptor = new(
        Name: "compute-sdf-world",
        SurfaceId: SurfaceId.New()
    );
    private readonly ISdfFrameSource m_frameSource;
    private readonly uint m_height;
    private readonly int m_instanceCapacity;
    private readonly SdfWorldKernels m_kernels;
    private readonly int m_programWordCapacity;
    private readonly Dictionary<int, Func<nint>> m_screenSources;
    private readonly Dictionary<int, Func<Vector3>> m_screenLights;
    private readonly Dictionary<int, Func<SdfScreenSurfaceTransform?>> m_screenSurfaceTransforms;
    private readonly IServiceProvider m_serviceProvider;
    private readonly uint m_width;
    private bool m_captured;
    private byte[]? m_capturedPixels;
    private Surface[] m_childSurfaces = [];
    private string? m_debugCapturePath;
    private IGpuDeviceContext? m_deviceContext;
    private bool m_disposed;
    private int m_debugMode;
    private SdfWorldEngine? m_engine;
    private IGpuComputeServices? m_gpu;
    private int m_produceFrameIndex;
    private readonly bool? m_rayQueryEnabled;
    private ISteppableRenderNode[] m_steppableChildren = [];
    private readonly bool? m_timingEnabled;
    private ulong m_timingFrame;

    private static int CaptureDelayFrames() {
        return (int.TryParse(Environment.GetEnvironmentVariable(variable: "PUCK_CAPTURE_FRAME"), out var frame) && (frame > 0)) ? frame : 0;
    }

    // PUCK_RAY_QUERY permits (default, unset, or any value other than "0") or denies ("0") the ray-query path; the
    // env read is the fallback when the constructor's rayQueryEnabled argument is null.
    private static bool RayQueryEnabledFromEnvironment() {
        return !string.Equals(Environment.GetEnvironmentVariable(variable: "PUCK_RAY_QUERY"), "0", StringComparison.Ordinal);
    }
    // PUCK_TIMING=1 opts in to GPU performance counters; the env read is the fallback when the constructor's
    // timingEnabled argument is null.
    private static bool TimingEnabledFromEnvironment() {
        return string.Equals(Environment.GetEnvironmentVariable(variable: "PUCK_TIMING"), "1", StringComparison.Ordinal);
    }

    /// <summary>Initializes a new instance of the <see cref="SdfEngineNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (resolves the neutral GPU compute factories; the device comes from the host context).</param>
    /// <param name="frameSource">The per-frame source of the scene, cameras, and viewport regions.</param>
    /// <param name="kernels">The compiled world kernel set (SPIR-V for Vulkan, DXIL for Direct3D 12).</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="capturePath">An optional PNG path; when set, the first rendered frame is read back from the GPU and written there.</param>
    /// <param name="createStorageImage">An optional factory for the output image. When it returns an <see cref="IGpuExportableStorageImage"/>, the node runs in <em>export</em> mode: it ends each frame in the cross-backend handoff layout, drains the producer queue, and emits a shared-handle <see cref="Surface"/> (for zero-copy cross-backend present) instead of a same-device image-view one. When <see langword="null"/>, a plain same-device storage image is created from the resolved <see cref="IGpuStorageImageFactory"/>.</param>
    /// <param name="children">An optional map from viewport slot to a child <see cref="IRenderNode"/> that supplies that slot's surface instead of an SDF camera. Each child is produced every frame at its slot's pixel rect, its same-device storage image is bound straight into the source-agnostic compositor's <c>sources[]</c> slot, and the SDF render skips that slot. The child must produce a <em>compute source</em> (a same-device storage image left in the general layout).</param>
    /// <param name="screenSources">An optional map from a program-declared <see cref="SdfScreenSurface.ScreenIndex"/>
    /// to a provider of that screen's current same-device storage-image view (General layout, shader-readable),
    /// called once per produced frame AFTER children have produced — a provider may close over a hosted child (its
    /// slot's produced <see cref="Surface.ImageViewHandle"/>) or over ANY other GPU image a host owns directly, e.g.
    /// an emulator's NATIVE framebuffer image, unresampled (not one of this node's <paramref name="children"/>, whose
    /// surfaces are pane-extent-resampled — the screen seam samples the source itself, so no separate resample is
    /// needed or wanted). A provider returning 0 leaves the slot unbound this frame, which falls back to the
    /// flat/procedural screen material. See <see cref="SdfWorldEngine.SetScreenSource"/>.</param>
    /// <param name="screenLights">An optional map, parallel to <paramref name="screenSources"/>, from a screen index to
    /// a provider of the colored light that screen emits into the room this frame (typically its framebuffer's average
    /// color). Polled right after <paramref name="screenSources"/>; see <see cref="SdfWorldEngine.SetScreenLight"/>.</param>
    /// <param name="screenSurfaceTransforms">An optional map, parallel to <paramref name="screenSources"/>, from a
    /// screen index to a provider of that screen's world-space sampling frame THIS FRAME — for a screen slab riding a
    /// dynamic transform (e.g. a slab riding a moving rig), whose sampling frame must move with the geometry every
    /// frame or it goes stale. A provider returning <see langword="null"/> leaves the program-declared (or
    /// previously set) frame untouched this frame — a screen on static geometry simply omits its entry, or a provider
    /// may return null on frames where nothing moved to skip the write. Polled right after <paramref name="screenLights"/>;
    /// see <see cref="SdfWorldEngine.SetScreenSurface"/>.</param>
    /// <param name="dynamicTransformCapacity">An optional FLOOR on the engine's dynamic-transform slot capacity. The
    /// engine always provisions at least the first frame's transform count; a host whose moving-entity population
    /// grows over the run (hundreds of animated instances appearing later) passes its peak here so the buffer is
    /// sized once — the capacity is otherwise frozen at construction and later frames' excess transforms are
    /// dropped.</param>
    /// <param name="programWordCapacity">An optional FLOOR on the program buffer's packed-word capacity (see
    /// <see cref="SdfWorldEngineOptions"/>): a frame source that hot-swaps programs (<see cref="SdfFrame.ProgramChanged"/>)
    /// declares its envelope here instead of relying on every future program staying within the first frame's size.</param>
    /// <param name="instanceCapacity">An optional FLOOR on the instance count the per-tile mask buffer is sized for —
    /// the hot-swap counterpart of <paramref name="programWordCapacity"/> for instanced programs.</param>
    /// <param name="timingEnabled">The <c>PUCK_TIMING</c> toggle (per-pass GPU-ms timestamps), or <see langword="null"/>
    /// to fall back to the <c>PUCK_TIMING=1</c> environment read — so a host that resolves the toggle from its own
    /// config (e.g. a run document's <c>host.timing</c>) can pass it straight through, while the environment variable
    /// keeps working verbatim for anything that sets it externally instead.</param>
    /// <param name="rayQueryEnabled">The <c>PUCK_RAY_QUERY</c> toggle (permit/deny the ray-query path), or
    /// <see langword="null"/> to fall back to the environment/default. Exposed for parity with
    /// <paramref name="timingEnabled"/> and read back via <see cref="RayQueryEnabled"/>; no current render path
    /// consults it (the ray-query world's device-level feature probe is unconditional — see
    /// <c>VulkanLogicalDeviceFactory</c> — since its own per-viewport ray-query render node was retired when
    /// rendering centralized here), but the toggle is threaded so a future ray-query consumer does not need another
    /// config-plumbing pass.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public SdfEngineNode(IServiceProvider serviceProvider, ISdfFrameSource frameSource, SdfWorldKernels kernels, uint width, uint height, string? capturePath = null, Func<IGpuDeviceContext, IGpuStorageImage>? createStorageImage = null, IReadOnlyDictionary<int, IRenderNode>? children = null, IReadOnlyDictionary<int, Func<nint>>? screenSources = null, IReadOnlyDictionary<int, Func<Vector3>>? screenLights = null, IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? screenSurfaceTransforms = null, int dynamicTransformCapacity = 0, int programWordCapacity = 0, int instanceCapacity = 0, bool? timingEnabled = null, bool? rayQueryEnabled = null) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(frameSource);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "SDF engine node dimensions must be non-zero.");
        }

        m_capturePath = capturePath;
        // Copy each caller map into a concrete Dictionary<,> (its struct enumerator is what the per-frame foreach binds
        // — see the Empty* fields) rather than storing the read-only interface; the maps are built once and never
        // mutated after construction, and every per-frame loop over them writes independent per-slot state, so the copy
        // is observably identical. A null map shares the empty singleton.
        m_children = (children is null ? EmptyChildren : new Dictionary<int, IRenderNode>(collection: children));
        m_createStorageImage = createStorageImage;
        m_dynamicTransformCapacity = dynamicTransformCapacity;
        m_instanceCapacity = instanceCapacity;
        m_programWordCapacity = programWordCapacity;
        m_frameSource = frameSource;
        m_height = height;
        m_kernels = kernels;
        m_rayQueryEnabled = rayQueryEnabled;
        m_screenSources = (screenSources is null ? EmptyScreenSources : new Dictionary<int, Func<nint>>(collection: screenSources));
        m_screenLights = (screenLights is null ? EmptyScreenLights : new Dictionary<int, Func<Vector3>>(collection: screenLights));
        m_screenSurfaceTransforms = (screenSurfaceTransforms is null ? EmptyScreenSurfaceTransforms : new Dictionary<int, Func<SdfScreenSurfaceTransform?>>(collection: screenSurfaceTransforms));
        m_serviceProvider = serviceProvider;
        m_timingEnabled = timingEnabled;
        m_width = width;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <summary>The resolved <c>PUCK_RAY_QUERY</c> toggle: the constructor argument when given, else the
    /// environment/default. See the constructor's <c>rayQueryEnabled</c> parameter doc for why nothing consumes this
    /// yet.</summary>
    public bool RayQueryEnabled => (m_rayQueryEnabled ?? RayQueryEnabledFromEnvironment());

    /// <summary>The RGBA pixels read back the first time this node captured (its <c>capturePath</c> was set);
    /// empty until then. Lets a parity gate diff two backends' renders without re-reading the GPU.</summary>
    public ReadOnlyMemory<byte> CapturedPixels => m_capturedPixels;

    /// <summary>Gets or sets the SDF debug view mode applied to the next submitted frame.</summary>
    public int DebugMode {
        get => m_debugMode;
        set {
            m_debugMode = value;

            if (m_engine is not null) {
                m_engine.DebugMode = value;
            }
        }
    }

    /// <summary>Arms a one-shot debug capture: the NEXT produced frame is read back and written to
    /// <paramref name="path"/> — the runtime sibling of the <c>--capture</c> startup flag (the debug-page verb).</summary>
    /// <param name="path">The PNG path to write (the caller creates the directory).</param>
    public void RequestCapture(string path) {
        m_debugCapturePath = path;
    }

    /// <summary>Reads the previous frame's per-pass GPU times through the live engine (a passthrough of
    /// <see cref="SdfWorldEngine.TryReadPassTimings"/>, mirroring the <see cref="DebugMode"/> forwarder) — the seam an
    /// <c>sdf.info</c>-style verb reads without depending on the engine. False when the engine is not yet built or
    /// timing is off (<c>PUCK_TIMING=1</c> / the spec Timing flag).</summary>
    /// <param name="beam">The beam prepass milliseconds.</param>
    /// <param name="views">The cull-args + Stage 1 views milliseconds.</param>
    /// <param name="composite">The Stage 2 composite milliseconds.</param>
    /// <param name="frame">The whole-frame milliseconds.</param>
    /// <returns>Whether timing is live and the previous frame's marks were readable.</returns>
    public bool TryReadPassTimings(out double beam, out double views, out double composite, out double frame) {
        beam = 0.0;
        views = 0.0;
        composite = 0.0;
        frame = 0.0;

        return (m_engine?.TryReadPassTimings(beam: out beam, composite: out composite, frame: out frame, views: out views) ?? false);
    }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // The shared device is an inherited host capability (every node in the tree composites on one device).
        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice)) {
            return default;
        }

        var frame = m_frameSource.CaptureFrame(width: m_width, height: m_height, deltaSeconds: (float)context.DeltaSeconds, interpolationAlpha: (float)context.InterpolationAlpha);

        // Produce each child viewport's surface first (so its image-view is known before the source array is bound),
        // then build/refresh the engine, then hand it the child views for this frame's source-array (re)bind.
        ProduceChildren(context: in context, frame: frame);
        EnsureEngine(gpuDevice: gpuDevice, frame: frame);
        m_engine!.DebugMode = m_debugMode;

        foreach (var (slot, _) in m_children) {
            if (
                (slot < 0) ||
                (slot >= frame.Views.Count)
            ) {
                continue;
            }

            m_engine!.SetChildSource(slot: slot, imageViewHandle: m_childSurfaces[slot].ImageViewHandle);
        }

        // Screen sources: polled AFTER children have produced (a provider may read a just-produced child surface).
        // A provider returning 0 leaves the slot unbound this frame — the engine's material-shaded fallback applies.
        foreach (var (screenIndex, provider) in m_screenSources) {
            m_engine!.SetScreenSource(screenIndex: screenIndex, imageViewHandle: provider());
        }

        // Screen LIGHTS: the colored glow each screen emits into the room (parallel to the source poll above).
        foreach (var (screenIndex, provider) in m_screenLights) {
            m_engine!.SetScreenLight(screenIndex: screenIndex, color: provider());
        }

        // Screen surface TRANSFORMS: a screen riding a dynamic entity re-poses its sampling frame every frame its
        // geometry moved (parallel to the polls above); a null result leaves the table untouched this frame.
        foreach (var (screenIndex, provider) in m_screenSurfaceTransforms) {
            if (provider() is { } transform) {
                m_engine!.SetScreenSurface(screenIndex: screenIndex, origin: transform.Origin, right: transform.Right, up: transform.Up, halfWidth: transform.HalfWidth, halfHeight: transform.HalfHeight);
            }
        }

        if (frame.ProgramChanged) {
            m_engine!.UploadProgram(program: frame.Program);
        }

        m_engine!.SubmitFrame(frame: frame);

        // PUCK_CAPTURE_FRAME=N delays the one-shot --capture to the Nth produced frame (default 0 = first), so a capture
        // can grab a post-transition frame (e.g. an animated split-screen settled) instead of frame 1. Diagnostic aid.
        ++m_produceFrameIndex;

        if (
            (m_capturePath is not null) &&
            !m_captured &&
            (m_produceFrameIndex > CaptureDelayFrames())
        ) {
            // Retain a copy of the readback (the readback buffer is reused across calls) so a parity gate can diff
            // two backends' output without a second GPU read.
            m_capturedPixels = m_engine.ReadPixels().ToArray();
            PngEncoder.Write(
                height: (int)m_height,
                path: m_capturePath,
                rgba: m_capturedPixels,
                width: (int)m_width
            );
            m_captured = true;
        }

        // The runtime sibling of --capture: a debug verb arms a one-shot capture of whatever frame is produced next.
        if (m_debugCapturePath is { } debugCapturePath) {
            m_debugCapturePath = null;

            PngEncoder.Write(
                height: (int)m_height,
                path: debugCapturePath,
                rgba: m_engine.ReadPixels().ToArray(),
                width: (int)m_width
            );
            Console.Error.WriteLine(value: $"[debug] captured frame {m_produceFrameIndex} -> {debugCapturePath}");
        }

        if (m_engine.TimingEnabled) {
            ReportTiming();
            m_timingFrame++;
        }

        // Export mode hands the host a shared NT handle (zero-copy cross-backend present); same-device mode hands it
        // an image view to sample directly.
        return m_engine.ExportMode
            ? new Surface(
                ImageViewHandle: 0,
                Width: m_width,
                Height: m_height,
                Format: SurfaceFormat.R8G8B8A8Unorm,
                SharedHandle: m_engine.ExportSharedHandle
            )
            : new Surface(
                ImageViewHandle: m_engine.OutputImageViewHandle,
                Width: m_width,
                Height: m_height,
                Format: SurfaceFormat.R8G8B8A8Unorm
            );
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        // Drain before tearing down GPU resources: the per-frame submits are fire-and-forget (nothing fences them),
        // so a frame could still be in flight at teardown. The engine's Dispose is wait-free by contract.
        m_deviceContext.TryWaitIdle();

        foreach (var child in m_children.Values) {
            child.Dispose();
        }

        m_engine?.Dispose();
        m_engine = null;
    }

    /// <inheritdoc/>
    public void OnDeviceLost() {
        // Device-loss recovery: reset the subtree on the still-valid (lost) device, child-first (children are device
        // children too, and must be torn down before the device is). Unlike Dispose there is NO idle drain — the device
        // is lost, so nothing in flight will ever complete, and the host pump recreates the device immediately after.
        // The next ProduceFrame rebuilds the engine against the recreated device (construction re-uploads the program,
        // so a recovered device never renders an empty scene).
        foreach (var child in m_children.Values) {
            child.OnDeviceLost();
        }

        m_engine?.Dispose();
        m_engine = null;
        m_deviceContext = null;
        // Re-arm the one-shot capture so a --capture run writes a POST-recovery frame (lets device-loss recovery be
        // visually verified from the readback; harmless when no capture path is set).
        m_captured = false;
    }

    private void EnsureEngine(IGpuDeviceContext gpuDevice, SdfFrame frame) {
        if (m_engine is not null) {
            return;
        }

        // One cohesive compute-services bundle instead of resolving each granular factory; the granular interfaces
        // are still registered for a node that needs only one of them.
        m_gpu ??= (IGpuComputeServices)m_serviceProvider.GetService(serviceType: typeof(IGpuComputeServices))!;
        m_deviceContext = gpuDevice;

        var viewportCount = (uint)frame.Views.Count;

        if (viewportCount > SdfWorldEngine.MaxViewports) {
            throw new ArgumentException(message: $"The world compositor supports at most {SdfWorldEngine.MaxViewports} viewports; the frame has {viewportCount}.");
        }

        // Mark which live viewport slots a hosted child backs (the beam prepass and Stage 1 skip these); the source
        // for such a slot is the child's surface, not an SDF render.
        var childMask = 0u;

        foreach (var slot in m_children.Keys) {
            if (
                (slot >= 0) &&
                (slot < (int)viewportCount)
            ) {
                childMask |= (1u << slot);
            }
        }

        // GPU performance counters: opt-in via the resolved timing toggle (the constructor argument, falling back to
        // PUCK_TIMING=1 when not supplied — see TimingEnabledFromEnvironment), gated on the backend having
        // registered the timing seam (resolved granularly — timing is not part of the always-on bundle) and on the
        // device reporting usable timestamps (the engine checks that half).
        IGpuTimingPoolFactory? timingFactory = null;
        IGpuTimingRecorder? timingRecorder = null;

        if (m_timingEnabled ?? TimingEnabledFromEnvironment()) {
            timingFactory = m_serviceProvider.GetService(serviceType: typeof(IGpuTimingPoolFactory)) as IGpuTimingPoolFactory;
            timingRecorder = m_serviceProvider.GetService(serviceType: typeof(IGpuTimingRecorder)) as IGpuTimingRecorder;

            if (
                (timingFactory is null) ||
                (timingRecorder is null)
            ) {
                Console.Error.WriteLine(value: "[world-timing] the GPU timing seam is not registered on this backend; running untimed.");
            }
        }

        m_engine = new SdfWorldEngine(
            device: gpuDevice,
            gpu: m_gpu,
            height: m_height,
            kernels: m_kernels,
            options: new SdfWorldEngineOptions(
                ChildMask: childMask,
                CreateOutputImage: m_createStorageImage,
                DynamicTransformCapacity: Math.Max(Math.Max(1, m_dynamicTransformCapacity), frame.DynamicTransforms.Count),
                InstanceCapacity: m_instanceCapacity,
                Program: frame.Program,
                ProgramWordCapacity: m_programWordCapacity,
                TimingFactory: timingFactory,
                TimingRecorder: timingRecorder,
                ViewportCapacity: viewportCount
            ),
            width: m_width
        );

        if ((timingFactory is not null) && (timingRecorder is not null)) {
            if (m_engine.TimingEnabled) {
                var capabilities = m_engine.TimingCapabilities;

                Console.Error.WriteLine(value: $"[world-timing] enabled | period {capabilities.PeriodNanoseconds:0.###}ns | validBits {capabilities.ValidBits}");
            } else {
                Console.Error.WriteLine(value: "[world-timing] the device reports no usable GPU timestamps; running untimed.");
            }
        }
    }

    // Render each hosted child viewport's surface at its slot's pixel rect. Children resolve the same shared device
    // from the forwarded host context; the parent passes each the slot's pixel extent (matching the SDF source
    // sizing) so Stage 2's 1:1 copy lands in bounds. Their submits are enqueued ahead of the compositor's.
    private void ProduceChildren(in FrameContext context, SdfFrame frame) {
        if (m_children.Count == 0) {
            return;
        }

        // Sized once to the first frame's view count (the layout is stable for the run); never resized, so a frozen
        // child slot index can never fall outside it.
        if (m_childSurfaces.Length == 0) {
            m_childSurfaces = new Surface[frame.Views.Count];
        }

        StepChildren(context: in context, frame: frame);

        foreach (var (slot, child) in m_children) {
            if (
                (slot < 0) ||
                (slot >= frame.Views.Count)
            ) {
                continue;
            }

            var region = frame.Views[slot].Region;

            m_childSurfaces[slot] = child.ProduceFrame(context: context with {
                TargetHeight = Math.Max(1u, (uint)(region.Height * m_height)),
                TargetWidth = Math.Max(1u, (uint)(region.Width * m_width)),
            });
        }
    }

    // Fleet stepping, task-per-node (machine-fleet-plan.md lever 1). The split enforces the timeline-access rule:
    // PrepareStep runs SERIALLY here on the render thread (shared-timeline cursors and shared input drainers), then
    // ExecuteStep — the simulation itself, the expensive half — fans out one task per node. Steppable children share
    // nothing, ExecuteStep touches only each node's private state, and Parallel.For is a barrier, so every child's
    // output is staged before the serial GPU pass reads it; GPU submit order is unchanged. A single prepared child
    // just runs inline — no point paying the fork.
    private void StepChildren(in FrameContext context, SdfFrame frame) {
        var ready = 0;

        foreach (var (slot, child) in m_children) {
            // The SAME eligibility as the produce loop: a child whose slot is not (yet) in the frame's view list is
            // not produced, so it must not step either — a just-booted pane's machine starts consuming the timeline
            // on exactly the frame its view exists.
            if (
                (slot < 0) ||
                (slot >= frame.Views.Count)
            ) {
                continue;
            }

            if ((child is ISteppableRenderNode steppable) && steppable.PrepareStep(context: in context)) {
                if (m_steppableChildren.Length < m_children.Count) {
                    m_steppableChildren = new ISteppableRenderNode[m_children.Count];
                }

                m_steppableChildren[ready++] = steppable;
            }
        }

        if (ready == 1) {
            m_steppableChildren[0].ExecuteStep();
        }
        else if (ready > 1) {
            Parallel.For(fromInclusive: 0, toExclusive: ready, body: index => m_steppableChildren[index].ExecuteStep());
        }
    }

    // Reads the PREVIOUS frame's marks (the launcher drains the device between frames, so they are complete with no
    // added stall) and prints a throttled per-pass digest: whole-frame GPU ms plus each pass's ms and share-of-frame.
    private void ReportTiming() {
        if (
            (m_timingFrame == 0UL) ||
            (0UL != (m_timingFrame % TimingReportInterval))
        ) {
            return;
        }

        if (!m_engine!.TryReadPassTimings(beam: out var beam, composite: out var composite, frame: out var frame, views: out var views)) {
            return;
        }

        Console.Error.WriteLine(value: $"[world-timing] frame {frame:0.000}ms | beam {beam:0.000} ({Percent(part: beam, whole: frame)}%) views {views:0.000} ({Percent(part: views, whole: frame)}%) composite {composite:0.000} ({Percent(part: composite, whole: frame)}%)");
    }
    private static int Percent(double part, double whole) =>
        (int)Math.Round(a: ((100.0 * part) / whole));
}
