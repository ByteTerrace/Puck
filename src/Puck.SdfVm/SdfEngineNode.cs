using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
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
/// A child occupies a slot by NAME (<see cref="SdfViewSnapshot.Child"/> against the constructor's <c>children</c> map),
/// never a fixed slot index — the SAME name may sit at a different viewport slot on a later frame, since the slot
/// order follows <see cref="SdfFrame.Views"/>. Which slots skip the SDF camera march for a child is decided EVERY
/// frame from that frame's bindings resolved against the registered names (a layout switch can turn any slot into a
/// child or back); a slot naming a child the map lacks renders through the ordinary SDF camera path instead — see
/// <see cref="HasChild"/> for the read-back a caller uses to tell the two apart.
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
public sealed partial class SdfEngineNode : IRenderNode, ICaptureRequestTarget {
    private readonly int m_brickPoolVoxelCapacity;

    // Not readonly: RegisterChild swaps the shared empty singleton for a private map on the first post-construction
    // registration (see the constructor's copy remark).
    private Dictionary<string, IRenderNode> m_children;
    // THIS frame's child-slot bitmask, derived by DeriveChildMask from the frame's own SdfViewSnapshot.Child bindings
    // resolved against m_children, and handed to the engine (SetChildMask) before its SetChildSource calls — the one
    // answer ProduceChildren/StepChildren/the SetChildSource loop all share for "is this slot a child this frame".
    private uint m_childSlotMask;

    private readonly string? m_debugLabel;
    private readonly int m_dynamicTransformCapacity;
    private readonly ISdfFrameSource m_frameSource;
    private readonly uint m_height;
    private readonly int m_instanceCapacity;

    private SdfWorldKernels m_kernels;

    /// <summary>Gets the last uploaded program's packed word count, or 0 before the first upload — the live half of
    /// the <c>world.budget</c> cost sheet against <see cref="ProgramWordCapacity"/>.</summary>
    public int LiveProgramWords { get; private set; }

    /// <summary>Copies the currently uploaded packed program for inspection, or returns an empty array before
    /// the engine is initialized. The caller owns the copy; editing it cannot change the renderer.</summary>
    /// <returns>The live program's 32-bit words, excluding reserved capacity and per-frame transform/grid buffers.</returns>
    /// <remarks>Call on the render pump thread, as with the live console diagnostics. This performs a CPU copy,
    /// not a GPU readback. The packed format follows <see cref="SdfProgram"/> and is not a durable asset format.</remarks>
    public uint[] CopyLiveProgramWords() => (m_engine?.CopyLiveProgramWords() ?? []);

    /// <summary>Gets the last uploaded program's instance count, or 0 before the first upload.</summary>
    public int LiveProgramInstances { get; private set; }
    /// <summary>Gets the bounded volume count in the most recently submitted frame, before per-ray rejection.</summary>
    public int LiveVolumes { get; private set; }
    /// <summary>Gets the last uploaded program's Lipschitz step scale (1 = no clamp), or 0 before the first
    /// upload.</summary>
    public float LiveProgramStepScale { get; private set; }
    /// <summary>Gets the last uploaded program's step-scale binder (see <see cref="SdfProgram.StepScaleBinder"/>), or
    /// <see langword="null"/> before the first upload and whenever nothing unscoped binds the step scale.</summary>
    public SdfStepScaleBinder? LiveProgramStepScaleBinder { get; private set; }

    /// <summary>Gets the last uploaded program's non-unit field-scope clamps, or an empty list before upload.
    /// These candidate-local bounds remain active when <see cref="LiveProgramStepScale"/> is one.</summary>
    public IReadOnlyList<SdfFieldScopeClamp> LiveProgramFieldScopeClamps { get; private set; } = [];

    /// <summary>Gets whether <paramref name="name"/> is registered in this node's <c>children</c> map (see the
    /// constructor) — the read-back a caller (e.g. a <c>world.view.state</c> echo) uses to tell an unresolved child
    /// binding apart from a live one, since a slot naming an unregistered child falls back to the ordinary SDF
    /// camera path rather than throwing.</summary>
    /// <param name="name">The child name a view binding's <see cref="SdfViewSnapshot.Child"/> may carry.</param>
    public bool HasChild(string name) =>
        m_children.ContainsKey(key: name);
    /// <summary>Registers <paramref name="node"/> under <paramref name="name"/> after construction — a pipeline a console
    /// verb loads mid-session — so a later frame's <see cref="SdfViewSnapshot.Child"/> naming it resolves like a
    /// constructor-supplied child; this node then owns the child's lifetime (<see cref="Dispose"/>,
    /// <see cref="OnDeviceLost"/>) exactly the same way. Pump-thread only: the same thread <see cref="ProduceFrame"/>
    /// runs on, since the map is iterated there unguarded.</summary>
    /// <param name="name">The child's name — refused when already registered.</param>
    /// <param name="node">The child render node; must produce a same-device storage-image surface.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or already registered.</exception>
    public void RegisterChild(string name, IRenderNode node) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);
        ArgumentNullException.ThrowIfNull(argument: node);

        if (ReferenceEquals(
            objA: m_children,
            objB: EmptyChildren
        )) {
            m_children = new Dictionary<string, IRenderNode>(comparer: StringComparer.Ordinal);
        }

        if (!m_children.TryAdd(
            key: name,
            value: node
        )) {
            throw new ArgumentException(
                message: $"A child named '{name}' is already registered.",
                paramName: nameof(name)
            );
        }
    }
    /// <summary>Removes a named child after retiring submissions that may sample its output. Pump-thread only.</summary>
    /// <param name="name">The child to remove; an absent name is a no-op.</param>
    public void RemoveChild(string name) {
        if (!m_children.TryGetValue(
            key: name,
            value: out var child
        )) { return; }
        m_deviceContext.TryWaitIdle();
        m_children.Remove(key: name);
        child.Dispose();
    }

    /// <summary>Gets the current program-word capacity, or the initial reserve before engine initialization.</summary>
    public int ProgramWordCapacity => (m_engine?.ProgramWordCapacity ?? m_programWordCapacity);
    /// <summary>Gets the bytes the engine allocates for its visibility records, or zero before engine initialization.</summary>
    public ulong VisibilityRecordBytes => (m_engine?.VisibilityRecordBytes ?? 0UL);

    private readonly int m_programWordCapacity;
    private readonly Dictionary<int, Func<Vector3>> m_screenLights;

    // This frame's screen-source leases, moved into the frame-ring slot that samples them when the slot's fence has
    // retired the leases it held before.
    private LeaseRetireList m_pendingScreenSourceFrames = new();
    private LeaseRetireList[] m_retainedScreenSourceFrames = BuildScreenSourceFrameRing(capacity: 0);
    private Dictionary<int, Func<GpuImageLease>> m_screenSourceFrames = EmptyScreenSourceFrames;

    private readonly Dictionary<int, Func<nint>> m_screenSources;
    private readonly Dictionary<int, Func<SdfScreenSurfaceTransform?>> m_screenSurfaceTransforms;
    private readonly int m_viewportCapacity;
    private readonly uint m_width;

    private readonly CapturePngWriter m_capturePng = new();

    // Converted once: a lambda passed per frame would allocate a delegate on every produced frame.
    private readonly Action<string> m_writeDebugCapture;

    // Owned here rather than by the engine, so submission identities keep increasing across a device-loss rebuild.
    private readonly GpuWorkLedger m_work = new(
        framesInFlight: SdfWorldEngine.FrameRingSize,
        name: "gpu.sdf-engine"
    );
    private readonly CaptureRequestSlot m_debugCapture = new();

    private int m_debugMode;
    private IGpuDeviceContext? m_deviceContext;
    private bool m_disposed;
    private SdfWorldEngine? m_engine;
    // Whether the current engine has submitted a frame: IsReady. Cleared wherever the engine is.
    private bool m_engineProduced;
    private bool m_glyphAtlasInitialized;

    // The lease on the pipeline set the engine records with, shared through the composition's pipeline cache, built off
    // the frame thread and kept across engine rebuilds until a device loss or disposal releases it.
    private readonly SdfWorldPipelineSource m_pipelines;

    private int m_produceFrameIndex;
    private SdfGlyphAtlas? m_uploadedGlyphAtlas;

    // Concrete Dictionary<,> (not the read-only interface) so the per-frame foreach binds the struct enumerator
    // instead of boxing IEnumerator on the render thread every ProduceFrame; the ctor copies caller maps to match.
    private static readonly Dictionary<string, IRenderNode> EmptyChildren = new(comparer: StringComparer.Ordinal);
    private static readonly Dictionary<int, Func<GpuImageLease>> EmptyScreenSourceFrames = new();
    private static readonly Dictionary<int, Func<nint>> EmptyScreenSources = new();
    private static readonly Dictionary<int, Func<Vector3>> EmptyScreenLights = new();
    private static readonly Dictionary<int, Func<SdfScreenSurfaceTransform?>> EmptyScreenSurfaceTransforms = new();
    private readonly NodeDescriptor m_descriptor = new(
        Name: "compute-sdf-world",
        SurfaceId: SurfaceId.New()
    );
    private Surface[] m_childSurfaces = [];
    private readonly Dictionary<IRenderNode, Surface> m_producedChildren = new(comparer: ReferenceEqualityComparer.Instance);
    private ISteppableRenderNode[] m_steppableChildren = [];
    private readonly HashSet<IRenderNode> m_preparedChildren = new(comparer: ReferenceEqualityComparer.Instance);

    private static LeaseRetireList[] BuildScreenSourceFrameRing(int capacity) {
        var ring = new LeaseRetireList[SdfWorldEngine.FrameRingSize];

        for (var slot = 0; (slot < ring.Length); slot++) {
            ring[slot] = new LeaseRetireList(capacity: capacity);
        }

        return ring;
    }
    // Builds the engine once its pipelines are ready. The first call starts the pipeline build on the thread pool; until
    // it completes this returns false and the node presents nothing new, so a cold driver cache delays the first frame
    // rather than freezing the pump.
    private bool EnsureEngine(IGpuDeviceContext gpuDevice, SdfFrame frame) {
        if (m_engine is not null) {
            return true;
        }

        m_deviceContext = gpuDevice;

        if (m_pipelines.Poll(
            device: gpuDevice,
            hostsOnDirectX: false,
            includeBrickPipelines: (m_brickPoolVoxelCapacity > 0),
            kernels: m_kernels
        ) is not { } pipelines) {
            return false;
        }

        // The viewport CAPACITY: the first frame's count raised to the declared floor (the split-screen envelope —
        // the engine itself composites each frame's actual Views.Count, validated against this capacity).
        var viewportCount = ((uint)Math.Max(
            val1: frame.Views.Count,
            val2: m_viewportCapacity
        ));

        if (viewportCount > SdfWorldEngine.MaxViewports) {
            throw new ArgumentException(message: $"The world compositor supports at most {SdfWorldEngine.MaxViewports} viewports; the frame/floor asks for {viewportCount}.");
        }

        m_engine = new SdfWorldEngine(
            device: gpuDevice,
            height: m_height,
            options: new SdfWorldEngineOptions(
                BrickPoolVoxelCapacity: m_brickPoolVoxelCapacity,
                DynamicTransformCapacity: Math.Max(
                    val1: Math.Max(
                        val1: 1,
                        val2: m_dynamicTransformCapacity
                    ),
                    val2: frame.DynamicTransforms.Count
                ),
                InstanceCapacity: m_instanceCapacity,
                Program: frame.Program,
                ProgramWordCapacity: m_programWordCapacity,
                ViewportCapacity: viewportCount,
                WorkLedger: m_work
            ),
            pipelines: pipelines,
            width: m_width
        );

        return true;
    }
    // Which live viewport slots a hosted child backs THIS frame (the beam prepass and Stage 1 skip these; the source
    // for such a slot is the child's surface, not an SDF render): the frame's own SdfViewSnapshot.Child bindings
    // resolved by NAME against m_children. Re-derived every produced frame — a layout switch (view.override) can
    // turn any slot into a child or back — and handed to the engine as its live mask (SetChildMask). A name absent
    // from m_children leaves its slot on the ordinary SDF camera path (see this type's remarks).
    private uint DeriveChildMask(SdfFrame frame) {
        var childMask = 0u;
        var slotCount = Math.Min(
            val1: frame.Views.Count,
            val2: ((int)SdfWorldEngine.MaxViewports)
        );

        for (var slot = 0; (slot < slotCount); slot++) {
            if (
                (frame.Views[slot].Child is { } childName) &&
                m_children.ContainsKey(key: childName)
            ) {
                childMask |= (1u << slot);
            }
        }

        return childMask;
    }
    // The current frame's child for viewport slot `slot`, resolved by name against m_children and gated by this
    // frame's m_childSlotMask (DeriveChildMask) — the one lookup ProduceChildren/StepChildren/the SetChildSource loop
    // in ProduceFrame all share, so their "is this slot a child this frame" question always agrees.
    private bool TryChildForSlot(SdfFrame frame, int slot, out IRenderNode child) {
        child = null!;

        return (
            (slot >= 0) &&
            (slot < frame.Views.Count) &&
            (0 != (m_childSlotMask & (1u << slot))) &&
            (frame.Views[slot].Child is { } name) &&
            m_children.TryGetValue(
            key: name,
            value: out child!
        )
        );
    }
    // Render each hosted child viewport's surface at its slot's pixel rect. Children resolve the same shared device
    // from the forwarded host context; the parent passes each the slot's pixel extent (matching the SDF source
    // sizing); Stage 2 reconstructs the actual image extent into each region. Their submits precede the compositor.
    private void ProduceChildren(in FrameContext context, SdfFrame frame) {
        if (m_children.Count == 0) {
            return;
        }

        // Grown to the widest view count seen (a layout switch can add slots mid-run); never shrunk, so a slot index
        // this frame's mask names can never fall outside it.
        if (m_childSurfaces.Length < frame.Views.Count) {
            Array.Resize(
                array: ref m_childSurfaces,
                newSize: frame.Views.Count
            );
        }

        StepChildren(
            context: in context,
            frame: frame
        );

        m_producedChildren.Clear();

        for (var slot = 0; (slot < frame.Views.Count); slot++) {
            if (!TryChildForSlot(
                child: out var child,
                frame: frame,
                slot: slot
            )) {
                continue;
            }

            if (m_producedChildren.TryGetValue(
                key: child,
                value: out var produced
            )) {
                m_childSurfaces[slot] = produced;
                continue;
            }
            // One instance advances once. Its first slot sets the requested extent; later slots reuse the image.
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
            m_producedChildren.Add(
                key: child,
                value: m_childSurfaces[slot]
            );
        }
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
    // Fleet stepping, task-per-node. The split enforces the timeline-access rule:
    // PrepareStep runs SERIALLY here on the render thread (shared-timeline cursors and shared input drainers), then
    // ExecuteStep — the simulation itself, the expensive half — fans out one task per node. Steppable children share
    // nothing, ExecuteStep touches only each node's private state, and Parallel.For is a barrier, so every child's
    // output is staged before the serial GPU pass reads it; GPU submit order is unchanged. A single prepared child
    // just runs inline — no point paying the fork.
    private void StepChildren(in FrameContext context, SdfFrame frame) {
        var ready = 0;

        m_preparedChildren.Clear();

        // The SAME eligibility as the produce loop (TryChildForSlot): a child whose slot is not this frame's child
        // slot is not produced, so it must not step either — a just-booted pane's machine starts consuming the
        // timeline on exactly the frame its view exists.
        for (var slot = 0; (slot < frame.Views.Count); slot++) {
            if (!TryChildForSlot(
                child: out var child,
                frame: frame,
                slot: slot
            )) {
                continue;
            }

            if (
                (child is ISteppableRenderNode steppable) &&
                m_preparedChildren.Add(item: child) &&
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
    // A provider can acquire an externally-written image (the camera shared-target tier). Keep that acquisition with
    // the SDF frame-ring slot whose command buffer samples it, and retire the old contents only after that slot's fence
    // signals. The preallocated lists avoid allocating a closure/list every produced frame.
    private void RetireAndAdoptScreenSourceFrames(int frameSlot) {
        var retained = m_retainedScreenSourceFrames[frameSlot];

        retained.RetireAll();
        m_pendingScreenSourceFrames.MoveTo(destination: retained);
    }
    private void WriteDebugCapture(string path) {
        m_capturePng.ThrowIfUnavailable(path: path);

        if (!m_capturePng.TryWrite(
            height: ((int)m_height),
            path: path,
            rgba: m_engine!.ReadPixels().ToArray(),
            width: ((int)m_width)
        )) {
            throw new NotSupportedException(message: "PNG capture is unavailable.");
        }

        Console.Error.WriteLine(value: $"[debug] captured frame {m_produceFrameIndex} -> {path}");
    }
    private void RetireAllScreenSourceFrames() {
        m_pendingScreenSourceFrames.RetireAll();

        foreach (var retained in m_retainedScreenSourceFrames) {
            retained.RetireAll();
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_debugCapture.Refuse(error: new ObjectDisposedException(objectName: nameof(SdfEngineNode)));

        // Drain before tearing down GPU resources: the per-frame submits are fire-and-forget, so a frame may still be
        // in flight. This also proves every retained external screen-source acquisition is safe to release below.
        m_deviceContext.TryWaitIdle();

        foreach (var child in m_children.Values) {
            child.Dispose();
        }

        m_engine?.Dispose();
        m_engine = null;
        m_engineProduced = false;
        CancelShaderReload(reason: "the node was disposed");
        m_pipelines.Release();
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

        // The lost submissions will never sample the leased screen sources, so the leases retire before the frame
        // source is told: a producer retiring its images then releases them at once, on the device that made them.
        RetireAllScreenSourceFrames();
        m_frameSource.NotifyDeviceLost();
        m_engine?.Dispose();
        m_engine = null;
        m_engineProduced = false;
        // A pipeline build or kernel reload still in flight is waited out and discarded before the host recreates the
        // device; the rebuilt engine builds its pipelines anew on the recreated one.
        CancelShaderReload(reason: "the device was lost");
        m_pipelines.Release();
        m_work.Invalidate();
        m_glyphAtlasInitialized = false;
        m_uploadedGlyphAtlas = null;
        m_deviceContext = null;
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

        // Drive the carve-bake settle planner BEFORE this frame's capture: the frame source's
        // planner polls bake states + requests newly-settled bakes against the live engine, and a Ready→brick flip bumps
        // its content revision so the CaptureFrame just below rebuilds emitting the brick THIS frame. The engine is null
        // only on the very first frame (built by EnsureEngine after the first capture), where there is nothing to bake.
        if (m_engine is not null) {
            m_frameSource.AdvanceBricks(bakes: m_engine);
        }

        var frame = m_frameSource.CaptureFrame(
            width: m_width,
            height: m_height,
            deltaSeconds: ((float)context.FrameDeltaSeconds),
            interpolationAlpha: ((float)context.InterpolationAlpha)
        );

        // Decide this frame's child slots and produce each child viewport's surface (so its image-view is known before
        // the source array is bound). Children need nothing from the engine, so they step and produce whether or not
        // its pipelines are built yet: a hosted pane compiles and installs its own pipelines while the engine's build
        // is still pending, instead of waiting behind it.
        m_childSlotMask = DeriveChildMask(frame: frame);
        ProduceChildren(
            context: in context,
            frame: frame
        );

        // Until the engine's pipelines are built there is no engine and nothing new to present. The frame source still
        // captured this frame and the children still produced theirs, so both keep pace.
        if (!EnsureEngine(
            frame: frame,
            gpuDevice: gpuDevice
        )) {
            return default;
        }

        // Hand the engine the mask + child views for this frame's source-array (re)bind.
        // Empty is a valid result while an asynchronous child is waiting for its first successful compile.
        // Keep that slot on the initialized SDF path until it publishes an image; never bind a null GPU view.
        for (var slot = 0; (slot < frame.Views.Count); slot++) {
            if ((m_childSlotMask & (1u << slot)) == 0) { continue; }
            if (m_childSurfaces[slot].IsEmpty) {
                m_childSlotMask &= ~(1u << slot);
            } else if (!m_childSurfaces[slot].IsSameDeviceImage) {
                throw new InvalidOperationException(message: $"Child viewport {slot} must publish a same-device image surface.");
            }
        }
        m_engine!.SetChildMask(mask: m_childSlotMask);
        ApplyPendingShaderReload();
        ReconcileGlyphAtlas();
        m_engine!.DebugMode = m_debugMode;

        if (m_debugLabel is not null) {
            m_engine.DebugLabel = m_debugLabel;
        }

        for (var slot = 0; (slot < frame.Views.Count); slot++) {
            if (!TryChildForSlot(
                child: out _,
                frame: frame,
                slot: slot
            )) {
                continue;
            }

            m_engine!.SetChildSource(
                slot: slot,
                imageViewHandle: m_childSurfaces[slot].ImageViewHandle
            );
        }

        // Screen-source PREPARE: hand the frame source the live device so a CPU-pixel source can upload THIS frame's
        // image through its services to a stable handle before the providers below are polled (they return that
        // handle). Mirrors AdvanceBricks — an engine seam, default no-op.
        m_frameSource.PrepareScreenSources(deviceContext: gpuDevice);

        // View RENDER: hand the frame source this frame's full context so a source hosting an offscreen ViewStack (a
        // diegetic camera / jumbotron) renders its views against the live device now — their handles fresh before the
        // screen-source poll below reads them. Mirrors PrepareScreenSources — an engine seam, default no-op.
        m_frameSource.RenderViews(context: in context);

        // Screen sources: polled AFTER children have produced (a provider may read a just-produced child surface).
        // A provider returning 0 leaves the slot unbound this frame — the engine's material-shaded fallback applies.
        m_pendingScreenSourceFrames.RetireAll();

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

            m_pendingScreenSourceFrames.Hold(lease: in source);
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
            LiveProgramWords = frame.Program.Words.Length;
            LiveProgramInstances = frame.Program.Instances.Count;
            LiveProgramStepScale = frame.Program.StepScale;
            LiveProgramStepScaleBinder = frame.Program.StepScaleBinder;
            LiveProgramFieldScopeClamps = frame.Program.FieldScopeClamps;
        }

        if (0 == m_screenSourceFrames.Count) {
            m_engine!.SubmitFrame(frame: frame);
        } else {
            m_engine!.SubmitFrameWithExternalResources(
                frame: frame,
                onFrameSlotAvailable: RetireAndAdoptScreenSourceFrames
            );
        }

        LiveVolumes = frame.Volumes.Count;

        ++m_produceFrameIndex;
        m_engineProduced = true;

        // A debug verb (world.screenshot) arms a one-shot capture of whatever frame is produced next.
        m_debugCapture.Serve(
            failureLabel: "[debug] capture failed",
            writer: m_writeDebugCapture
        );

        return Surface.SameDeviceImage(
            imageHandle: m_engine.OutputImageHandle,
            imageViewHandle: m_engine.OutputImageViewHandle,
            width: m_width,
            height: m_height,
            format: SurfaceFormat.R8G8B8A8Unorm
        );
    }
    /// <inheritdoc/>
    public void RequestCapture(FrameCaptureRequest request) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        m_debugCapture.Arm(
            pendingPath: PendingCapturePath,
            request: request
        );
    }

    /// <summary>Initializes a new instance of the <see cref="SdfEngineNode"/> class.</summary>
    /// <param name="pipelines">The composition's pipeline cache the node leases its engine's pipeline set from. The
    /// device, and the services the engine records through, come from the host context each frame.</param>
    /// <param name="frameSource">The per-frame source of the scene, cameras, and viewport regions.</param>
    /// <param name="kernels">The compiled world kernel set (SPIR-V for Vulkan, DXIL for Direct3D 12).</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="children">An optional map from a stable name to a child <see cref="IRenderNode"/> that supplies a
    /// viewport slot's surface instead of an SDF camera whenever the frame's own <see cref="SdfFrame.Views"/> binds that
    /// slot's <see cref="SdfViewSnapshot.Child"/> to the same name (see this class's remarks for the per-frame
    /// derivation). Each bound child is produced every frame at its slot's pixel rect, its same-device storage image is
    /// bound straight into the source-agnostic compositor's <c>sources[]</c> slot, and the SDF render skips that slot.
    /// The child must produce a <em>compute source</em> (a same-device storage image left in the general layout).</param>
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
    /// <param name="debugLabel">An optional GPU-capture debug-group name for this engine's whole recorded frame (see
    /// <see cref="SdfWorldEngine.DebugLabel"/>); a nested view engine passes <c>view:&lt;name&gt;</c> so a capture
    /// distinguishes it. Defaults to the engine's own default (<c>world</c>) when omitted. Presentation-only.</param>
    /// <param name="brickPoolVoxelCapacity">The carve-bake brick pool's voxel capacity (see
    /// <see cref="SdfWorldEngineOptions.BrickPoolVoxelCapacity"/>), frozen at construction. Defaults to
    /// <see cref="SdfWorldEngine.DefaultBrickPoolVoxelCapacity"/> (64 MB); pass 0 for a host whose scene never bakes
    /// carves (no pool is allocated).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public SdfEngineNode(SdfWorldPipelineCache pipelines, ISdfFrameSource frameSource, SdfWorldKernels kernels, uint width, uint height, IReadOnlyDictionary<string, IRenderNode>? children = null, IReadOnlyDictionary<int, Func<nint>>? screenSources = null, IReadOnlyDictionary<int, Func<Vector3>>? screenLights = null, IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? screenSurfaceTransforms = null, int dynamicTransformCapacity = 0, int programWordCapacity = 0, int instanceCapacity = 0, int viewportCapacity = 0, string? debugLabel = null, int brickPoolVoxelCapacity = SdfWorldEngine.DefaultBrickPoolVoxelCapacity) {
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(frameSource);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "SDF engine node dimensions must be non-zero.");
        }

        m_debugLabel = debugLabel;
        // Copy each caller map into a concrete Dictionary<,> (its struct enumerator is what the per-frame foreach binds
        // — see the Empty* fields) rather than storing the read-only interface; the maps are built once and never
        // mutated after construction, and every per-frame loop over them writes independent per-slot state, so the copy
        // is observably identical. A null map shares the empty singleton.
        m_children = ((children is null)
            ? EmptyChildren
            : new Dictionary<string, IRenderNode>(
                collection: children,
                comparer: StringComparer.Ordinal
            )
        );
        m_dynamicTransformCapacity = dynamicTransformCapacity;
        m_instanceCapacity = instanceCapacity;
        m_viewportCapacity = viewportCapacity;
        m_brickPoolVoxelCapacity = brickPoolVoxelCapacity;
        m_programWordCapacity = programWordCapacity;
        m_frameSource = frameSource;
        m_height = height;
        m_kernels = kernels;
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
        m_pipelines = new SdfWorldPipelineSource(cache: pipelines);
        m_width = width;
        m_writeDebugCapture = WriteDebugCapture;
    }

    // Builder-only additive seam: keeps the longstanding public constructor's Func<nint> screenSources parameter
    // source-compatible while a render spec can opt particular indices into fence-retired frame acquisitions.
    internal void SetScreenSourceFrames(IReadOnlyDictionary<int, Func<GpuImageLease>>? screenSourceFrames) {
        if (m_engine is not null) {
            throw new InvalidOperationException(message: "screen-source frame providers must be configured before the first produced frame");
        }

        m_screenSourceFrames = ((screenSourceFrames is null)
            ? EmptyScreenSourceFrames
            : new Dictionary<int, Func<GpuImageLease>>(collection: screenSourceFrames)
        );
        m_pendingScreenSourceFrames = new LeaseRetireList(capacity: m_screenSourceFrames.Count);
        m_retainedScreenSourceFrames = BuildScreenSourceFrameRing(capacity: m_screenSourceFrames.Count);
    }

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
    /// <summary>Gets the hosted children, keyed by the name each was constructed or registered under. Read it on the
    /// thread that produces frames, as <see cref="RegisterChild"/> and <see cref="RemoveChild"/> change it there.</summary>
    public IReadOnlyDictionary<string, IRenderNode> Children => m_children;
    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;
    /// <summary>Gets whether the node's engine is ready: its pipeline set is installed and the engine built from it has
    /// produced its first frame. It is false until the pipeline build that the first produced frame starts has completed
    /// and a frame has been submitted, and again after a device loss until the rebuilt engine has submitted one; a
    /// produced frame meanwhile returns an empty surface. It is the one readiness fact the console waits on and a
    /// capture's hold reads.</summary>
    public bool IsReady => m_engineProduced;
    /// <summary>Gets why the node is not <see cref="IsReady"/>, naming its pipeline build and how far it has come (for
    /// example <c>the engine's pipeline set is building (5 of 14 pipelines created)</c>), or <see langword="null"/> once
    /// it is ready. It builds a new string on each read, so a caller polls <see cref="IsReady"/> and reads this only to
    /// report.</summary>
    public string? NotReadyReason => (m_engineProduced
        ? null
        : ((m_engine is null)
            ? m_pipelines.Describe()
            : "the engine has not produced its first frame"
        )
    );
    /// <summary>Gets the GPU work this node's engine recorded, per pass, for its newest completed submission (see
    /// <see cref="SdfWorldEngine.Work"/>). Unavailable before the first frame completes and again after a device loss
    /// until a frame of the rebuilt engine completes; submission identities keep increasing across the rebuild.</summary>
    public IGpuWorkSource Work => m_work;
    /// <summary>Gets the GPU objects this node's engines have created, over the node's whole life (see
    /// <see cref="SdfWorldEngine.WorkLifetime"/>).</summary>
    public IWorkCounterSource WorkLifetime => m_work;
    /// <summary>Gets the render-pass labels, in submission order — a passthrough of <see cref="SdfWorldEngine.PassLabels"/>
    /// so a consumer holding only this node names no engine type.</summary>
    public static ReadOnlySpan<string> PassLabels => SdfWorldEngine.PassLabels;
    /// <inheritdoc/>
    public string? PendingCapturePath => m_debugCapture.PendingPath;
}
