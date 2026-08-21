using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Hosting;
using Puck.SignedDistance;

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
/// compute, fully backend-neutral (it depends only on the neutral <c>IGpuCompute*</c> seam, so the identical node runs
/// on whichever backend the host publishes). It resolves the shared device from <see cref="FrameContext.Host"/>,
/// pulls each frame's scene + cameras + regions from an <see cref="ISdfFrameSource"/>, and drives the shared
/// <see cref="SdfWorldEngine"/> core in its fire-and-forget mode (the host's frame pacing orders the frames).
/// <para>
/// Rendering is two-stage so the compositor is source-agnostic, ahead of which a sky pre-pass (<c>sdf-sky.comp</c>)
/// fills every source pixel with the authored sky, so a tile the beam later culls is never a stale, undispatched
/// pixel. <c>sdf-beam.comp</c> cone-marches the field per tile to a conservative march-start depth;
/// <c>sdf-world-views.comp</c> (Stage 1) renders each viewport's SDF camera into its own rect-sized
/// <em>source</em> texture; <c>sdf-world-composite.comp</c> (Stage 2) places each source — an SDF view, or a child
/// node's output bound into the same slot — into its screen region by a 1:1 copy.
/// The viewport count follows <see cref="SdfFrame.Views"/>; nothing about the scene, cameras, or layout is baked in.
/// </para>
/// <para>
/// Diegetic screens ride a separate, shading-only seam: a program may declare up to 8 static screen surfaces (see
/// <see cref="SdfProgramBuilder"/>'s screen-surface <c>ScreenSlab</c> overload), and this node polls the
/// <c>screenSources</c> constructor argument each frame to bind (or unbind) each one's sampled image — unlike a
/// child, this never adds or replaces a viewport; it only changes how one shape's lit face shades. A screen's
/// world-space sampling frame is normally set once at program build; a screen riding a dynamic transform instead
/// supplies a <c>screenSurfaceTransforms</c> provider, polled every frame right after <c>screenLights</c>, so its
/// sampling frame tracks the geometry the dynamic transform already moved (see <see cref="SdfWorldEngine.SetScreenSurface"/>).
/// </para>
/// </summary>
public sealed class SdfEngineNode : IRenderNode, IPassTimingSource, ICaptureRequestTarget {
    private const ulong TimingReportInterval = 60;

    private readonly int m_brickPoolVoxelCapacity;
    private readonly string? m_capturePath;
    private readonly Dictionary<int, IRenderNode> m_children;
    private readonly Func<IGpuDeviceContext, IGpuStorageImage>? m_createStorageImage;
    private readonly string? m_debugLabel;
    private readonly int m_dynamicTransformCapacity;
    private readonly ISdfFrameSource m_frameSource;
    private readonly uint m_height;
    private readonly int m_instanceCapacity;
    private readonly SdfWorldKernels m_kernels;
    private readonly int m_programWordCapacity;
    private readonly bool? m_rayQueryEnabled;
    private readonly Dictionary<int, Func<Vector3>> m_screenLights;
    private SdfScreenSourceFrame[] m_pendingScreenSourceFrames = [];
    private SdfScreenSourceFrame[][] m_retainedScreenSourceFrames = BuildScreenSourceFrameRing(capacity: 0);
    private readonly int[] m_retainedScreenSourceFrameCounts = new int[SdfWorldEngine.FrameRingSize];
    private Dictionary<int, Func<SdfScreenSourceFrame>> m_screenSourceFrames = EmptyScreenSourceFrames;
    private readonly Dictionary<int, Func<nint>> m_screenSources;
    private readonly Dictionary<int, Func<SdfScreenSurfaceTransform?>> m_screenSurfaceTransforms;
    private readonly SdfViewGpuServices m_services;
    private readonly bool? m_timingEnabled;
    private readonly int m_viewportCapacity;
    private readonly uint m_width;

    private bool m_captureUnavailable;
    private bool m_captured;
    private byte[]? m_capturedPixels;
    // [frame-timing] CPU-side sub-buckets: plain Stopwatch wall time (never a GPU query), so this digest still prints
    // even when the backend has no usable GPU timestamps. Armed live off GpuTimingControl.Shared, so bench.run / the
    // gpu.timing switch turn it on and off mid-session with no restart. Each digest reports the slowest node frame in
    // its block rather than an arbitrary modulo-boundary sample, exposing intermittent CPU hitches without per-frame IO.
    private ulong m_cpuTimingFrame;
    private CpuFrameTiming m_cpuTimingWorst;
    private string? m_debugCapturePath;
    private int m_debugMode;
    private IGpuDeviceContext? m_deviceContext;
    private bool m_disposed;
    private SdfWorldEngine? m_engine;
    private bool m_glyphAtlasInitialized;
    private IGpuComputeServices? m_gpu;
    private int m_produceFrameIndex;
    private ulong m_timingFrame;
    private SdfGlyphAtlas? m_uploadedGlyphAtlas;

    private readonly record struct CpuFrameTiming(
        long CaptureFrameTicks,
        long SetupTicks,
        long ScreenPublishTicks,
        long ViewRenderTicks,
        long BindingsTicks,
        long SubmitFrameTicks
    ) {
        public long TotalTicks => (((((CaptureFrameTicks + SetupTicks) + ScreenPublishTicks) + ViewRenderTicks) + BindingsTicks) + SubmitFrameTicks);
    }

    // Concrete Dictionary<,> (not the read-only interface) so the per-frame foreach binds the struct enumerator
    // instead of boxing IEnumerator on the render thread every ProduceFrame; the ctor copies caller maps to match.
    private static readonly Dictionary<int, IRenderNode> EmptyChildren = new();
    private static readonly Dictionary<int, Func<SdfScreenSourceFrame>> EmptyScreenSourceFrames = new();
    private static readonly Dictionary<int, Func<nint>> EmptyScreenSources = new();
    private static readonly Dictionary<int, Func<Vector3>> EmptyScreenLights = new();
    private static readonly Dictionary<int, Func<SdfScreenSurfaceTransform?>> EmptyScreenSurfaceTransforms = new();
    private readonly NodeDescriptor m_descriptor = new(
        Name: "compute-sdf-world",
        SurfaceId: SurfaceId.New()
    );
    private Surface[] m_childSurfaces = [];
    private int m_pendingScreenSourceFrameCount;
    private ISteppableRenderNode[] m_steppableChildren = [];

    private static int CaptureDelayFrames() {
        return ((int.TryParse(
            Environment.GetEnvironmentVariable(variable: "PUCK_CAPTURE_FRAME"),
            out var frame
        ) && (frame > 0))
            ? frame
            : 0
        );
    }
    private static SdfScreenSourceFrame[][] BuildScreenSourceFrameRing(int capacity) {
        var ring = new SdfScreenSourceFrame[SdfWorldEngine.FrameRingSize][];

        for (var slot = 0; (slot < ring.Length); slot++) {
            ring[slot] = new SdfScreenSourceFrame[capacity];
        }

        return ring;
    }
    private void EnsureEngine(IGpuDeviceContext gpuDevice, SdfFrame frame) {
        if (m_engine is not null) {
            return;
        }

        // One cohesive compute-services bundle instead of resolving each granular factory; the granular interfaces
        // are still registered for a node that needs only one of them. Forwarded unchanged from the composition
        // root's SdfViewGpuServices rather than re-resolved here.
        m_gpu ??= m_services.Gpu;
        m_deviceContext = gpuDevice;

        // The viewport CAPACITY: the first frame's count raised to the declared floor (the split-screen envelope —
        // the engine itself composites each frame's actual Views.Count, validated against this capacity).
        var viewportCount = ((uint)Math.Max(
            val1: frame.Views.Count,
            val2: m_viewportCapacity
        ));

        if (viewportCount > SdfWorldEngine.MaxViewports) {
            throw new ArgumentException(message: $"The world compositor supports at most {SdfWorldEngine.MaxViewports} viewports; the frame/floor asks for {viewportCount}.");
        }

        // Mark which live viewport slots a hosted child backs (the beam prepass and Stage 1 skip these); the source
        // for such a slot is the child's surface, not an SDF render.
        var childMask = 0u;

        foreach (var slot in m_children.Keys) {
            if (
                (slot >= 0) &&
                (slot < ((int)viewportCount))
            ) {
                childMask |= (1u << slot);
            }
        }

        // GPU performance counters, LIVE-ARMED: always USE the timing seam when the backend registered it (it is part
        // of the eagerly-resolved SdfViewGpuServices bundle now, not resolved granularly here). The engine creates its
        // rotating pools lazily on the first ARMED frame and consults GpuTimingControl.Shared per frame, so bench.run
        // / the gpu.timing switch turn it on mid-session with no rebuild — the resolved host.timing toggle only SEEDS
        // that control (see the constructor). Absent the seam the backend simply cannot time.
        var timingFactory = m_services.TimingFactory;
        var timingRecorder = m_services.TimingRecorder;

        m_engine = new SdfWorldEngine(
            device: gpuDevice,
            gpu: m_gpu,
            height: m_height,
            kernels: m_kernels,
            options: new SdfWorldEngineOptions(
                BrickPoolVoxelCapacity: m_brickPoolVoxelCapacity,
                ChildMask: childMask,
                CreateOutputImage: m_createStorageImage,
                DynamicTransformCapacity: Math.Max(
                    val1: Math.Max(
                        val1: 1,
                        val2: m_dynamicTransformCapacity
                    ),
                    val2: frame.DynamicTransforms.Count
                ),
                InstanceCapacity: m_instanceCapacity,
                LiveArmedTiming: true,
                Program: frame.Program,
                ProgramWordCapacity: m_programWordCapacity,
                TimingFactory: timingFactory,
                TimingRecorder: timingRecorder,
                ViewportCapacity: viewportCount
            ),
            width: m_width
        );

        if (
            (timingFactory is not null) &&
            (timingRecorder is not null)
        ) {
            if (m_engine.TimingEnabled) {
                var capabilities = m_engine.TimingCapabilities;

                Console.Error.WriteLine(value: $"[world-timing] available (armed on demand{(GpuTimingControl.Shared.Armed
                    ? "; ARMED"
                    : "")}) | period {capabilities.PeriodNanoseconds:0.###}ns | validBits {capabilities.ValidBits}");
            } else {
                Console.Error.WriteLine(value: "[world-timing] the device reports no usable GPU timestamps; running untimed.");
            }
        }
    }
    private static int Percent(double part, double whole) =>
        ((int)Math.Round(a: ((100.0 * part) / whole)));
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

        StepChildren(
            context: in context,
            frame: frame
        );

        foreach (var (slot, child) in m_children) {
            if (
                (slot < 0) ||
                (slot >= frame.Views.Count)
            ) {
                continue;
            }

            var region = frame.Views[slot].Region;

            m_childSurfaces[slot] = child.ProduceFrame(context: context with {
                TargetHeight = Math.Max(
                val1: 1u,
                val2: ((uint)(region.Height * m_height))
            ),
                TargetWidth = Math.Max(
                val1: 1u,
                val2: ((uint)(region.Width * m_width))
            ),
            });
        }
    }
    // PUCK_RAY_QUERY permits (default, unset, or any value other than "0") or denies ("0") the ray-query path; the
    // env read is the fallback when the constructor's rayQueryEnabled argument is null.
    private static bool RayQueryEnabledFromEnvironment() {
        return !string.Equals(
            a: Environment.GetEnvironmentVariable(variable: "PUCK_RAY_QUERY"),
            b: "0",
            comparisonType: StringComparison.Ordinal
        );
    }
    // A world load may replace (or remove) its immutable atlas without rebuilding this node. Polling the reference is
    // cheap; SetGlyphAtlas performs the expensive ring drain and upload only when the catalog actually changes.
    private void ReconcileGlyphAtlas() {
        var glyphAtlas = m_frameSource.GlyphAtlas;

        if (
            m_glyphAtlasInitialized &&
            ReferenceEquals(
            objA: glyphAtlas,
            objB: m_uploadedGlyphAtlas
        )
        ) {
            return;
        }

        if (glyphAtlas is null) {
            m_engine!.SetGlyphAtlas(
                rgbaPixels: ReadOnlyMemory<byte>.Empty,
                width: 0,
                height: 0
            );
        } else {
            m_engine!.SetGlyphAtlas(
                rgbaPixels: glyphAtlas.Rgba,
                width: glyphAtlas.Width,
                height: glyphAtlas.Height
            );
        }

        m_uploadedGlyphAtlas = glyphAtlas;
        m_glyphAtlasInitialized = true;
    }
    // Throttled [frame-timing] digest for this node's CPU phases (independent of the GPU pass-timing digest above).
    // The maximum total node time over each block is retained, so its buckets explain the same kind of intermittent
    // tail that the launcher's worst-of-N digest surfaces one level up.
    private void ReportCpuFrameTiming(CpuFrameTiming sample) {
        if (sample.TotalTicks >= m_cpuTimingWorst.TotalTicks) {
            m_cpuTimingWorst = sample;
        }

        if (0UL != (m_cpuTimingFrame % TimingReportInterval)) {
            return;
        }

        static double Milliseconds(long ticks) =>
            ((((double)ticks) * 1000.0) / Stopwatch.Frequency);

        var worst = m_cpuTimingWorst;

        m_cpuTimingWorst = default;

        Console.Error.WriteLine(value: $"[frame-timing] sdf-engine worst-of-{TimingReportInterval} total {Milliseconds(ticks: worst.TotalTicks):0.000}ms | capture {Milliseconds(ticks: worst.CaptureFrameTicks):0.000} | setup {Milliseconds(ticks: worst.SetupTicks):0.000} | screen-publish {Milliseconds(ticks: worst.ScreenPublishTicks):0.000} | view-render {Milliseconds(ticks: worst.ViewRenderTicks):0.000} | bindings {Milliseconds(ticks: worst.BindingsTicks):0.000} | submit {Milliseconds(ticks: worst.SubmitFrameTicks):0.000}");
    }
    // Reads the newest COMPLETE frame's marks (frame N − FrameRingSize — the engine's own ring fence proves it
    // retired, so no added stall) and prints a throttled per-pass digest: whole-frame GPU ms plus each pass's ms and
    // share-of-frame.
    private void ReportTiming() {
        if (
            (m_timingFrame == 0UL) ||
            (0UL != (m_timingFrame % TimingReportInterval))
        ) {
            return;
        }

        Span<double> passMilliseconds = stackalloc double[SdfWorldEngine.PassTimingCount];

        if (!m_engine!.TryReadPassTimings(
            frame: out var frame,
            passCount: out var passCount,
            passMilliseconds: passMilliseconds
        )) {
            return;
        }

        var builder = new StringBuilder(value: $"[world-timing] frame {frame:0.000}ms");
        var labels = SdfWorldEngine.PassTimingLabels;

        for (var index = 0; (index < passCount); index++) {
            _ = builder.Append(handler: $" | {labels[index]} {passMilliseconds[index]:0.000} ({Percent(
                part: passMilliseconds[index],
                whole: frame
            )}%)");
        }

        Console.Error.WriteLine(value: builder.ToString());
    }
    // Fleet stepping, task-per-node. The split enforces the timeline-access rule:
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

            if (
                (child is ISteppableRenderNode steppable) &&
                steppable.PrepareStep(context: in context)
            ) {
                if (m_steppableChildren.Length < m_children.Count) {
                    m_steppableChildren = new ISteppableRenderNode[m_children.Count];
                }

                m_steppableChildren[ready++] = steppable;
            }
        }

        if (ready == 1) {
            m_steppableChildren[0].ExecuteStep();
        } else if (ready > 1) {
            Parallel.For(
                fromInclusive: 0,
                toExclusive: ready,
                body: index => m_steppableChildren[index].ExecuteStep()
            );
        }
    }
    // Attempts one capture write, surviving (and loudly reporting) an environment that refuses to load Puck.Assets.
    // Returns false on any such failure so the caller can latch m_captureUnavailable and stop retrying a doomed load.
    private static bool TryWriteCapturePng(string path, byte[] rgba, int width, int height) =>
        CapturePngWriteGuard.TryWrite(
            state: (Path: path, Rgba: rgba, Width: width, Height: height),
            writeCore: static state => WriteCapturePngCore(
                height: state.Height,
                path: state.Path,
                rgba: state.Rgba,
                width: state.Width
            )
        );
    // Puck.Assets is an optional subsystem (screenshots/recording), not part of the render contract: an environment
    // that blocks or cannot load its assembly (an Application Control / code-integrity policy, a missing deployment
    // file) must not take the render loop down with it. WriteCapturePngCore is the ONLY member touching the
    // Puck.Assets-typed PngEncoder.Write call, kept non-inlined so the CLR only needs to resolve and load
    // Puck.Assets.dll when this exact method is JITted — i.e. lazily, on the first actual capture request, not on
    // every produced frame. CapturePngWriteGuard's try/catch wraps the call one frame up: a failure to load the
    // assembly surfaces as an exception thrown by that call (the callee never got to run), which is exactly where
    // the guard's surrounding try/catch can observe and report it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteCapturePngCore(string path, byte[] rgba, int width, int height) {
        PngEncoder.Write(
            height: height,
            path: path,
            rgba: rgba,
            width: width
        );
    }
    // A provider can acquire an externally-written image (the camera shared-target tier). Keep that acquisition with
    // the SDF frame-ring slot whose command buffer samples it, and retire the old contents only after that slot's fence
    // signals. The fixed arrays avoid allocating a closure/list every produced frame.
    private void RetireAndAdoptScreenSourceFrames(int frameSlot) {
        var retained = m_retainedScreenSourceFrames[frameSlot];
        var retainedCount = m_retainedScreenSourceFrameCounts[frameSlot];

        for (var index = 0; (index < retainedCount); index++) {
            retained[index].Retire();
            retained[index] = default;
        }

        m_pendingScreenSourceFrames.AsSpan(start: 0, length: m_pendingScreenSourceFrameCount).CopyTo(destination: retained);
        m_retainedScreenSourceFrameCounts[frameSlot] = m_pendingScreenSourceFrameCount;
        Array.Clear(array: m_pendingScreenSourceFrames, index: 0, length: m_pendingScreenSourceFrameCount);
        m_pendingScreenSourceFrameCount = 0;
    }
    private void RetirePendingScreenSourceFrames() {
        for (var index = 0; (index < m_pendingScreenSourceFrameCount); index++) {
            m_pendingScreenSourceFrames[index].Retire();
            m_pendingScreenSourceFrames[index] = default;
        }

        m_pendingScreenSourceFrameCount = 0;
    }
    private void RetireAllScreenSourceFrames() {
        RetirePendingScreenSourceFrames();

        for (var slot = 0; (slot < m_retainedScreenSourceFrames.Length); slot++) {
            var retained = m_retainedScreenSourceFrames[slot];
            var count = m_retainedScreenSourceFrameCounts[slot];

            for (var index = 0; (index < count); index++) {
                retained[index].Retire();
                retained[index] = default;
            }

            m_retainedScreenSourceFrameCounts[slot] = 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        // Drain before tearing down GPU resources: the per-frame submits are fire-and-forget, so a frame may still be
        // in flight. This also proves every retained external screen-source acquisition is safe to release below.
        m_deviceContext.TryWaitIdle();

        foreach (var child in m_children.Values) {
            child.Dispose();
        }

        m_engine?.Dispose();
        m_engine = null;
        RetireAllScreenSourceFrames();
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

        m_frameSource.NotifyDeviceLost();
        m_engine?.Dispose();
        m_engine = null;
        RetireAllScreenSourceFrames();
        m_glyphAtlasInitialized = false;
        m_uploadedGlyphAtlas = null;
        m_deviceContext = null;
        // Re-arm the one-shot capture so a --capture run writes a POST-recovery frame (lets device-loss recovery be
        // visually verified from the readback; harmless when no capture path is set).
        m_captured = false;
    }
    /// <summary>Looks up a named pass's milliseconds in a <see cref="TryReadPassTimings"/> result — a passthrough of
    /// <see cref="SdfWorldEngine.PassMilliseconds"/>.</summary>
    /// <param name="passMilliseconds">A filled <see cref="TryReadPassTimings"/> result span.</param>
    /// <param name="passCount">The entry count that read reported.</param>
    /// <param name="label">One of <see cref="PassTimingLabels"/>.</param>
    /// <returns>The pass's milliseconds, or 0 when the label is not present.</returns>
    public static double PassMilliseconds(ReadOnlySpan<double> passMilliseconds, int passCount, string label) =>
        SdfWorldEngine.PassMilliseconds(
            label: label,
            passCount: passCount,
            passMilliseconds: passMilliseconds
        );
    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // The shared device is an inherited host capability (every node in the tree composites on one device).
        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice)) {
            return default;
        }

        // Drive the carve-bake settle planner BEFORE this frame's capture: the frame source's
        // planner polls bake states + requests newly-settled bakes against the live engine, and a Ready→brick flip bumps
        // its content revision so the CaptureFrame just below rebuilds emitting the brick THIS frame. The engine is null
        // only on the very first frame (built by EnsureEngine after the first capture), where there is nothing to bake.
        if (m_engine is not null) {
            m_frameSource.AdvanceBricks(bakes: m_engine);
        }

        var cpuTimingEnabled = GpuTimingControl.Shared.Armed;
        var captureFrameStart = (cpuTimingEnabled
            ? Stopwatch.GetTimestamp()
            : 0L
        );
        var frame = m_frameSource.CaptureFrame(
            width: m_width,
            height: m_height,
            deltaSeconds: ((float)context.FrameDeltaSeconds),
            interpolationAlpha: ((float)context.InterpolationAlpha)
        );
        var captureFrameTicks = (cpuTimingEnabled
            ? (Stopwatch.GetTimestamp() - captureFrameStart)
            : 0L
        );
        var cpuPhaseStart = (cpuTimingEnabled
            ? Stopwatch.GetTimestamp()
            : 0L
        );

        // Produce each child viewport's surface first (so its image-view is known before the source array is bound),
        // then build/refresh the engine, then hand it the child views for this frame's source-array (re)bind.
        ProduceChildren(
            context: in context,
            frame: frame
        );
        EnsureEngine(
            frame: frame,
            gpuDevice: gpuDevice
        );
        ReconcileGlyphAtlas();
        m_engine!.DebugMode = m_debugMode;

        if (m_debugLabel is not null) {
            m_engine.DebugLabel = m_debugLabel;
        }

        var setupTicks = (cpuTimingEnabled
            ? (Stopwatch.GetTimestamp() - cpuPhaseStart)
            : 0L
        );

        foreach (var (slot, _) in m_children) {
            if (
                (slot < 0) ||
                (slot >= frame.Views.Count)
            ) {
                continue;
            }

            m_engine!.SetChildSource(
                slot: slot,
                imageViewHandle: m_childSurfaces[slot].ImageViewHandle
            );
        }

        // Screen-source PREPARE: hand the frame source the live device + compute services so a CPU-pixel source can
        // upload THIS frame's image to a stable handle before the providers below are polled (they return that
        // handle). Mirrors AdvanceBricks — an engine seam, default no-op. m_gpu is set by EnsureEngine just above.
        cpuPhaseStart = (cpuTimingEnabled
            ? Stopwatch.GetTimestamp()
            : 0L
        );
        m_frameSource.PrepareScreenSources(
            deviceContext: gpuDevice,
            gpu: m_gpu!
        );
        var screenPublishTicks = (cpuTimingEnabled
            ? (Stopwatch.GetTimestamp() - cpuPhaseStart)
            : 0L
        );

        // View RENDER: hand the frame source this frame's full context so a source hosting an offscreen ViewStack (a
        // diegetic camera / jumbotron) renders its views against the live device now — their handles fresh before the
        // screen-source poll below reads them. Mirrors PrepareScreenSources — an engine seam, default no-op.
        cpuPhaseStart = (cpuTimingEnabled
            ? Stopwatch.GetTimestamp()
            : 0L
        );
        m_frameSource.RenderViews(context: in context);
        var viewRenderTicks = (cpuTimingEnabled
            ? (Stopwatch.GetTimestamp() - cpuPhaseStart)
            : 0L
        );

        cpuPhaseStart = (cpuTimingEnabled
            ? Stopwatch.GetTimestamp()
            : 0L
        );

        // Screen sources: polled AFTER children have produced (a provider may read a just-produced child surface).
        // A provider returning 0 leaves the slot unbound this frame — the engine's material-shaded fallback applies.
        RetirePendingScreenSourceFrames();

        foreach (var (screenIndex, provider) in m_screenSources) {
            m_engine!.SetScreenSource(
                screenIndex: screenIndex,
                imageViewHandle: provider()
            );
        }

        foreach (var (screenIndex, provider) in m_screenSourceFrames) {
            var source = provider();

            m_engine!.SetScreenSource(
                screenIndex: screenIndex,
                imageViewHandle: source.ImageViewHandle
            );

            if (source.RequiresRetirement) {
                m_pendingScreenSourceFrames[m_pendingScreenSourceFrameCount++] = source;
            }
        }

        // Screen LIGHTS: the colored glow each screen emits into the room (parallel to the source poll above).
        foreach (var (screenIndex, provider) in m_screenLights) {
            m_engine!.SetScreenLight(
                screenIndex: screenIndex,
                color: provider()
            );
        }

        // Screen surface TRANSFORMS: a screen riding a dynamic entity re-poses its sampling frame every frame its
        // geometry moved (parallel to the polls above); a null result leaves the table untouched this frame.
        foreach (var (screenIndex, provider) in m_screenSurfaceTransforms) {
            if (provider() is { } transform) {
                m_engine!.SetScreenSurface(
                    screenIndex: screenIndex,
                    origin: transform.Origin,
                    right: transform.Right,
                    up: transform.Up,
                    halfWidth: transform.HalfWidth,
                    halfHeight: transform.HalfHeight
                );
            }
        }

        // Screen DECALS (the material-level text tier): a screen slot showing dense reading text this frame binds its
        // glyph-cell grid; a null result clears the slot back to the image/procedural path (the atlas-unavailable
        // degrade). Read straight off the frame source (the ISdfFrameSource.ScreenDecals seam, mirroring GlyphAtlas /
        // ScreenSurfaceTransforms) so this node's type coupling doesn't grow to thread it.
        if (m_frameSource.ScreenDecals is { } screenDecals) {
            foreach (var (screenIndex, provider) in screenDecals) {
                if (provider() is { } decal) {
                    m_engine!.SetScreenDecal(
                        screenIndex: screenIndex,
                        columns: decal.Columns,
                        rows: decal.Rows,
                        distanceRange: decal.DistanceRange,
                        cellWords: decal.Cells.Span
                    );
                } else {
                    m_engine!.ClearScreenDecal(screenIndex: screenIndex);
                }
            }
        }

        if (frame.ProgramChanged) {
            m_engine!.UploadProgram(program: frame.Program);
        }

        var bindingsTicks = (cpuTimingEnabled
            ? (Stopwatch.GetTimestamp() - cpuPhaseStart)
            : 0L
        );

        var submitFrameStart = (cpuTimingEnabled
            ? Stopwatch.GetTimestamp()
            : 0L
        );

        if (0 == m_screenSourceFrames.Count) {
            m_engine!.SubmitFrame(frame: frame);
        } else {
            m_engine!.SubmitFrameWithExternalResources(
                frame: frame,
                onFrameSlotAvailable: RetireAndAdoptScreenSourceFrames
            );
        }

        if (cpuTimingEnabled) {
            ++m_cpuTimingFrame;
            ReportCpuFrameTiming(sample: new CpuFrameTiming(
                CaptureFrameTicks: captureFrameTicks,
                SetupTicks: setupTicks,
                ScreenPublishTicks: screenPublishTicks,
                ViewRenderTicks: viewRenderTicks,
                BindingsTicks: bindingsTicks,
                SubmitFrameTicks: (Stopwatch.GetTimestamp() - submitFrameStart)
            ));
        }

        // PUCK_CAPTURE_FRAME=N delays the one-shot --capture to the Nth produced frame (default 0 = first), so a capture
        // can grab a post-transition frame (e.g. an animated split-screen settled) instead of frame 1. Diagnostic aid.
        ++m_produceFrameIndex;

        if (
            (m_capturePath is not null) &&
            !m_captured &&
            !m_captureUnavailable &&
            (m_produceFrameIndex > CaptureDelayFrames())
        ) {
            // Retain a copy of the readback (the readback buffer is reused across calls) so a parity gate can diff
            // two backends' output without a second GPU read.
            m_capturedPixels = m_engine.ReadPixels().ToArray();

            if (!TryWriteCapturePng(
                height: ((int)m_height),
                path: m_capturePath,
                rgba: m_capturedPixels,
                width: ((int)m_width)
            )) {
                m_captureUnavailable = true;
            }

            m_captured = true;
        }

        // The runtime sibling of --capture: a debug verb arms a one-shot capture of whatever frame is produced next.
        if (m_debugCapturePath is { } debugCapturePath) {
            m_debugCapturePath = null;

            if (m_captureUnavailable) {
                // The latch spares a doomed assembly load per frame, but a request dropped for it still has to be
                // said out loud: the requester was told a path and no file is coming.
                Console.Error.WriteLine(value: $"[debug] capture skipped, Puck.Assets is unavailable — no file written to {debugCapturePath}");
            } else if (TryWriteCapturePng(
                path: debugCapturePath,
                rgba: m_engine.ReadPixels().ToArray(),
                width: ((int)m_width),
                height: ((int)m_height)
            )) {
                Console.Error.WriteLine(value: $"[debug] captured frame {m_produceFrameIndex} -> {debugCapturePath}");
            } else {
                m_captureUnavailable = true;
            }
        }

        // Gate the [world-timing] digest on the live arming state (not just availability): a disarmed frame wrote no
        // marks, so TryReadPassTimings would refuse anyway — this skips even the attempt.
        if (
            m_engine.TimingEnabled &&
            GpuTimingControl.Shared.Armed
        ) {
            ReportTiming();
            m_timingFrame++;
        }

        // Export mode hands the host a shared NT handle (zero-copy cross-backend present); same-device mode hands it
        // an image view to sample directly.
        return (m_engine.ExportMode
            ? Surface.SharedTexture(
                sharedHandle: m_engine.ExportSharedHandle,
                width: m_width,
                height: m_height,
                format: SurfaceFormat.R8G8B8A8Unorm
            )
            : Surface.SameDeviceImage(
                imageHandle: m_engine.OutputImageHandle,
                imageViewHandle: m_engine.OutputImageViewHandle,
                width: m_width,
                height: m_height,
                format: SurfaceFormat.R8G8B8A8Unorm
            )
        );
    }
    /// <summary>Arms a one-shot debug capture: the next produced frame is read back and written to
    /// <paramref name="path"/> — the runtime sibling of the <c>--capture</c> startup flag (the debug-page verb).</summary>
    /// <param name="path">The PNG path to write (the caller creates the directory).</param>
    public void RequestCapture(string path) {
        m_debugCapturePath = path;
    }
    /// <summary>Reads the cadence gate's per-span diagnostics through the live engine (a passthrough of
    /// <see cref="SdfWorldEngine.CadenceDiagnostics"/>, mirroring the <see cref="TryReadPassTimings"/> forwarder) — the
    /// seam the <c>sdf.info</c> verb's cadence section reads without depending on the engine.</summary>
    /// <param name="diagnostics">Receives the latest diagnostics.</param>
    /// <returns>Whether the engine is built (false leaves <paramref name="diagnostics"/> at its default).</returns>
    public bool TryReadCadenceDiagnostics(out SdfCadenceDiagnostics diagnostics) {
        diagnostics = default;

        if (m_engine is null) {
            return false;
        }

        diagnostics = m_engine.CadenceDiagnostics;

        return true;
    }
    /// <summary>Reads the previous frame's per-pass GPU times through the live engine (a passthrough of
    /// <see cref="SdfWorldEngine.TryReadPassTimings"/>, mirroring the <see cref="DebugMode"/> forwarder) — the seam an
    /// <c>sdf.info</c>-style verb reads without depending on the engine. False when the engine is not yet built or
    /// timing is off (arm it live via the gpu.timing switch / the world.timing verb, or the run-doc <c>host.timing</c>
    /// field).</summary>
    /// <param name="passMilliseconds">Receives each render pass's milliseconds, in <see cref="SdfWorldEngine.PassTimingLabels"/>
    /// order; size it to <see cref="SdfWorldEngine.PassTimingCount"/>.</param>
    /// <param name="passCount">The number of pass entries written (0 when timing is off or the engine is not built).</param>
    /// <param name="frame">The whole-frame milliseconds.</param>
    /// <returns>Whether timing is live and the previous frame's marks were readable.</returns>
    public bool TryReadPassTimings(Span<double> passMilliseconds, out int passCount, out double frame) {
        passCount = 0;
        frame = 0.0;

        return (m_engine?.TryReadPassTimings(
            frame: out frame,
            passCount: out passCount,
            passMilliseconds: passMilliseconds
        ) ?? false);
    }

    /// <summary>Initializes a new instance of the <see cref="SdfEngineNode"/> class.</summary>
    /// <param name="services">The concrete GPU-services closure (<see cref="SdfViewGpuServices"/>) this node forwards
    /// to its offscreen engine — resolved once at the composition root and stashed unchanged (the device itself
    /// still comes from the host context each frame).</param>
    /// <param name="frameSource">The per-frame source of the scene, cameras, and viewport regions.</param>
    /// <param name="kernels">The compiled world kernel set (SPIR-V for Vulkan, DXIL for Direct3D 12).</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="capturePath">An optional PNG path; when set, the first rendered frame is read back from the GPU and written there.</param>
    /// <param name="createStorageImage">An optional factory for the output image. When it returns an <see cref="IGpuExportableStorageImage"/>, the node runs in <em>export</em> mode: it ends each frame in the cross-backend handoff layout, drains the producer queue, and emits a shared-handle <see cref="Surface"/> (for zero-copy cross-backend present) instead of a same-device image-view one. When <see langword="null"/>, a plain same-device storage image is created from the resolved <see cref="IGpuStorageImageFactory"/>.</param>
    /// <param name="children">An optional map from viewport slot to a child <see cref="IRenderNode"/> that supplies that slot's surface instead of an SDF camera. Each child is produced every frame at its slot's pixel rect, its same-device storage image is bound straight into the source-agnostic compositor's <c>sources[]</c> slot, and the SDF render skips that slot. The child must produce a <em>compute source</em> (a same-device storage image left in the general layout).</param>
    /// <param name="screenSources">An optional map from a program-declared <see cref="SdfScreenSurface.ScreenIndex"/>
    /// to a provider of that screen's current same-device storage-image view (General layout, shader-readable),
    /// called once per produced frame after children have produced — a provider may close over a hosted child (its
    /// slot's produced <see cref="Surface.ImageViewHandle"/>) or over any other GPU image a host owns directly, e.g.
    /// an emulator's native framebuffer image, unresampled (not one of this node's <paramref name="children"/>, whose
    /// surfaces are pane-extent-resampled — the screen seam samples the source itself, so no separate resample is
    /// needed or wanted). A provider returning 0 leaves the slot unbound this frame, which falls back to the
    /// flat/procedural screen material. See <see cref="SdfWorldEngine.SetScreenSource"/>.</param>
    /// <param name="screenLights">An optional map, parallel to <paramref name="screenSources"/>, from a screen index to
    /// a provider of the colored light that screen emits into the room this frame (typically its framebuffer's average
    /// color). Polled right after <paramref name="screenSources"/>; see <see cref="SdfWorldEngine.SetScreenLight"/>.</param>
    /// <param name="screenSurfaceTransforms">An optional map, parallel to <paramref name="screenSources"/>, from a
    /// screen index to a provider of that screen's world-space sampling frame this frame — for a screen slab riding a
    /// dynamic transform (e.g. a slab riding a moving rig), whose sampling frame must move with the geometry every
    /// frame or it goes stale. A provider returning <see langword="null"/> leaves the program-declared (or
    /// program-declared) frame untouched this frame — a screen on static geometry simply omits its entry, or a provider
    /// may return null on frames where nothing moved to skip the write. Polled right after <paramref name="screenLights"/>;
    /// see <see cref="SdfWorldEngine.SetScreenSurface"/>.</param>
    /// <param name="dynamicTransformCapacity">An optional floor on the engine's dynamic-transform slot capacity. The
    /// engine always provisions at least the first frame's transform count; a host whose moving-entity population
    /// grows over the run (hundreds of animated instances appearing later) passes its peak here so the buffer is
    /// sized once — the capacity is otherwise frozen at construction and later frames' excess transforms are
    /// dropped.</param>
    /// <param name="programWordCapacity">An optional floor on the program buffer's packed-word capacity (see
    /// <see cref="SdfWorldEngineOptions"/>): a frame source that hot-swaps programs (<see cref="SdfFrame.ProgramChanged"/>)
    /// declares its envelope here instead of relying on every future program staying within the first frame's size.</param>
    /// <param name="instanceCapacity">An optional floor on the instance count the per-tile mask buffer is sized for —
    /// the hot-swap counterpart of <paramref name="programWordCapacity"/> for instanced programs.</param>
    /// <param name="viewportCapacity">An optional floor on the compositor's viewport capacity — the envelope for a
    /// frame source whose per-frame view count grows past the first frame's (a split-screen host whose players join
    /// later). The engine composites each frame's actual view count up to the envelope; 0 keeps the pre-existing
    /// freeze-at-first-frame behavior.</param>
    /// <param name="timingEnabled">The resolved <c>host.timing</c> toggle (per-pass GPU-ms timestamps), or
    /// <see langword="null"/> for the disarmed default. The engine always receives the
    /// timing seam and arms live off <see cref="GpuTimingControl.Shared"/> (arm it live via the demo's gpu.timing
    /// switch / Puck.World's world.timing verb, or the run-doc <c>host.timing</c> field) — but seeds that shared
    /// control at construction (the lowest precedence tier: a programmatic arm or the run-doc composition seed outrank
    /// it).</param>
    /// <param name="rayQueryEnabled">The <c>PUCK_RAY_QUERY</c> toggle (permit/deny the ray-query path), or
    /// <see langword="null"/> to fall back to the environment/default. Exposed for parity with
    /// <paramref name="timingEnabled"/> and read back via <see cref="RayQueryEnabled"/>; no current render path
    /// consults it (the ray-query world's device-level feature probe is unconditional — see
    /// <c>VulkanLogicalDeviceFactory</c>; it does not own a per-viewport ray-query render node because
    /// rendering centralized here), but the toggle is threaded so a future ray-query consumer does not need another
    /// config-plumbing pass.</param>
    /// <param name="debugLabel">An optional GPU-capture debug-group name for this engine's whole recorded frame (see
    /// <see cref="SdfWorldEngine.DebugLabel"/>); a nested view engine passes <c>view:&lt;name&gt;</c> so a capture
    /// distinguishes it. Defaults to the engine's own default (<c>world</c>) when omitted. Presentation-only.</param>
    /// <param name="brickPoolVoxelCapacity">The carve-bake brick pool's voxel capacity (see
    /// <see cref="SdfWorldEngineOptions.BrickPoolVoxelCapacity"/>), frozen at construction. Defaults to
    /// <see cref="SdfWorldEngine.DefaultBrickPoolVoxelCapacity"/> (64 MB); pass 0 for a host whose scene never bakes
    /// carves (no pool is allocated).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public SdfEngineNode(SdfViewGpuServices services, ISdfFrameSource frameSource, SdfWorldKernels kernels, uint width, uint height, string? capturePath = null, Func<IGpuDeviceContext, IGpuStorageImage>? createStorageImage = null, IReadOnlyDictionary<int, IRenderNode>? children = null, IReadOnlyDictionary<int, Func<nint>>? screenSources = null, IReadOnlyDictionary<int, Func<Vector3>>? screenLights = null, IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? screenSurfaceTransforms = null, int dynamicTransformCapacity = 0, int programWordCapacity = 0, int instanceCapacity = 0, int viewportCapacity = 0, bool? timingEnabled = null, bool? rayQueryEnabled = null, string? debugLabel = null, int brickPoolVoxelCapacity = SdfWorldEngine.DefaultBrickPoolVoxelCapacity) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(frameSource);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "SDF engine node dimensions must be non-zero.");
        }

        m_capturePath = capturePath;
        m_debugLabel = debugLabel;
        // Copy each caller map into a concrete Dictionary<,> (its struct enumerator is what the per-frame foreach binds
        // — see the Empty* fields) rather than storing the read-only interface; the maps are built once and never
        // mutated after construction, and every per-frame loop over them writes independent per-slot state, so the copy
        // is observably identical. A null map shares the empty singleton.
        m_children = ((children is null)
            ? EmptyChildren
            : new Dictionary<int, IRenderNode>(collection: children)
        );
        m_createStorageImage = createStorageImage;
        m_dynamicTransformCapacity = dynamicTransformCapacity;
        m_instanceCapacity = instanceCapacity;
        m_viewportCapacity = viewportCapacity;
        m_brickPoolVoxelCapacity = brickPoolVoxelCapacity;
        m_programWordCapacity = programWordCapacity;
        m_frameSource = frameSource;
        m_height = height;
        m_kernels = kernels;
        m_rayQueryEnabled = rayQueryEnabled;
        m_screenSources = ((screenSources is null)
            ? EmptyScreenSources
            : new Dictionary<int, Func<nint>>(collection: screenSources)
        );
        m_screenLights = ((screenLights is null)
            ? EmptyScreenLights
            : new Dictionary<int, Func<Vector3>>(collection: screenLights)
        );
        m_screenSurfaceTransforms = ((screenSurfaceTransforms is null)
            ? EmptyScreenSurfaceTransforms
            : new Dictionary<int, Func<SdfScreenSurfaceTransform?>>(collection: screenSurfaceTransforms)
        );
        m_services = services;
        m_timingEnabled = timingEnabled;
        m_width = width;

        // Seed the shared arming control from the resolved host.timing toggle (the lowest-precedence seed — a
        // programmatic arm or the run-doc composition seed outrank it): it claims GpuTimingControl.Shared only when
        // nothing higher-precedence already has — see GpuTimingControl. Live arming (bench.run, the demo's
        // gpu.timing switch, Puck.World's world.timing verb) works regardless of this seed. Idempotent, so seeding
        // here and at composition with the same value is harmless.
        _ = GpuTimingControl.Shared.TrySeed(armed: (m_timingEnabled ?? false));
    }
    // Builder-only additive seam: keeps the longstanding public constructor's Func<nint> screenSources parameter
    // source-compatible while a render spec can opt particular indices into fence-retired frame acquisitions.
    internal void SetScreenSourceFrames(IReadOnlyDictionary<int, Func<SdfScreenSourceFrame>>? screenSourceFrames) {
        if (m_engine is not null) {
            throw new InvalidOperationException(message: "screen-source frame providers must be configured before the first produced frame");
        }

        m_screenSourceFrames = ((screenSourceFrames is null)
            ? EmptyScreenSourceFrames
            : new Dictionary<int, Func<SdfScreenSourceFrame>>(collection: screenSourceFrames)
        );
        m_pendingScreenSourceFrames = new SdfScreenSourceFrame[m_screenSourceFrames.Count];
        m_retainedScreenSourceFrames = BuildScreenSourceFrameRing(capacity: m_screenSourceFrames.Count);
    }

    /// <inheritdoc/>
    int IPassTimingSource.PassCount => PassTimingCount;
    /// <inheritdoc/>
    ReadOnlySpan<string> IPassTimingSource.PassLabels => PassTimingLabels;

    /// <summary>Gets the RGBA pixels read back the first time this node captured (its <c>capturePath</c> was set);
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
    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;
    /// <summary>Reads the most recent produced frame's per-frame instance-grid rebuild CPU cost through the live engine
    /// (a passthrough of <see cref="SdfWorldEngine.LastInstanceGridRebuildMilliseconds"/>) — the CPU-bound counterpart
    /// to <see cref="TryReadPassTimings"/>'s GPU pass timings. <see langword="null"/> before the engine is built or
    /// when the live program's instance grid is invariant (no per-frame rebuild).</summary>
    public double? LastInstanceGridRebuildMilliseconds => m_engine?.LastInstanceGridRebuildMilliseconds;
    /// <summary>Gets the pass count a <see cref="TryReadPassTimings"/> read reports — a passthrough of
    /// <see cref="SdfWorldEngine.PassTimingCount"/> (the width a caller sizes its span to).</summary>
    public static int PassTimingCount => SdfWorldEngine.PassTimingCount;
    /// <summary>Gets the render-pass labels a <see cref="TryReadPassTimings"/> read fills, in order — a passthrough of
    /// <see cref="SdfWorldEngine.PassTimingLabels"/> so a consumer holding only this node names no engine type.</summary>
    public static ReadOnlySpan<string> PassTimingLabels => SdfWorldEngine.PassTimingLabels;
    /// <inheritdoc/>
    public string? PendingCapturePath => m_debugCapturePath;
    /// <summary>Gets a value indicating whether the resolved <c>PUCK_RAY_QUERY</c> toggle is enabled: the constructor
    /// argument when given, else the environment/default. See the constructor's <c>rayQueryEnabled</c> parameter doc
    /// for why nothing consumes this yet.</summary>
    public bool RayQueryEnabled => (m_rayQueryEnabled ?? RayQueryEnabledFromEnvironment());
}
