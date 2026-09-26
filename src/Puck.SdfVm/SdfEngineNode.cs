using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
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
/// The SDF engine as a host-model <see cref="IRenderNode"/>: a generic multi-viewport SDF world renderer driven by
/// compute, fully backend-neutral (it depends only on the neutral <c>IGpuCompute*</c> seam, so the identical node runs
/// on whichever backend the host publishes). It resolves the shared device from <see cref="FrameContext.Host"/>,
/// pulls each frame's scene + cameras + regions from an <see cref="ISdfFrameSource"/>, and drives the shared
/// <see cref="SdfWorldEngine"/> core in its fire-and-forget mode (the host's frame pacing orders the frames).
/// <para>
/// Each view renders through its own dispatch set into its own output image: a sky pre-pass (<c>sdf-sky.comp</c>)
/// fills every output pixel with the authored sky, so a tile the beam later culls is never a stale, undispatched pixel;
/// <c>sdf-beam.comp</c> cone-marches the field per tile to a conservative march-start depth; <c>sdf-world-views.comp</c>
/// (Stage 1) shades the view's SDF camera into its output. A render graph places each output into its view's rect: the
/// node produces view 0 as <c>sdf.world</c>, and each later view through <see cref="ViewProducer"/>.
/// The viewport count follows <see cref="SdfFrame.Views"/>; nothing about the scene, cameras, or layout is baked in.
/// </para>
/// <para>
/// Diegetic screens ride a separate, shading-only seam: a program may declare up to
/// <see cref="SdfWorldEngine.MaxScreenSurfaces"/> static screen surfaces (see <see cref="SdfProgramBuilder"/>'s
/// screen-surface <c>ScreenSlab</c> overload), and each frame this node binds (or unbinds) each one's sampled image as its
/// <see cref="ISdfScreenSources"/> says: the image the render graph hands it for the source instance the screen reads, or
/// the image the host renders for it — this never adds or replaces a viewport; it only changes how one shape's lit face
/// shades. A screen's
/// world-space sampling frame is normally set once at program build; a screen riding a dynamic transform instead
/// supplies a <c>screenSurfaceTransforms</c> provider, polled every frame right after <c>screenLights</c>, so its
/// sampling frame tracks the geometry the dynamic transform already moved (see <see cref="SdfWorldEngine.SetScreenSurface"/>).
/// </para>
/// </summary>
public sealed partial class SdfEngineNode : IRenderNode, ICaptureRequestTarget {
    private readonly int m_brickPoolVoxelCapacity;
    private readonly string? m_debugLabel;
    private readonly int m_dynamicTransformCapacity;
    private readonly ISdfFrameSource m_frameSource;

    // The engine's extent, the largest any view renders at; Produce grows it, replacing the engine.
    private uint m_height;

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

    /// <summary>Gets the current program-word capacity, or the initial reserve before engine initialization.</summary>
    public int ProgramWordCapacity => (m_engine?.ProgramWordCapacity ?? m_programWordCapacity);
    /// <summary>Gets the bytes the engine allocates for its visibility records, or zero before engine initialization.</summary>
    public ulong VisibilityRecordBytes => (m_engine?.VisibilityRecordBytes ?? 0UL);
    /// <summary>Gets the bytes the engine's mesh region holds (<see cref="SdfWorldEngine.MeshRegionBytes"/>), or zero
    /// before engine initialization and before a frame draws a mesh.</summary>
    public ulong MeshRegionBytes => (m_engine?.MeshRegionBytes ?? 0UL);
    /// <summary>Gets the mesh draws of the last captured frame, the ones the mesh region holds once the frame
    /// renders.</summary>
    public int MeshDrawCount => (Volatile.Read(location: ref m_meshRegionDraws)?.Count ?? 0);

    private readonly int m_programWordCapacity;

    // The last captured frame's mesh draw list, which MeshDrawCount reads from another thread.
    private IReadOnlyList<SdfMeshDraw>? m_meshRegionDraws;

    // The image-view handle each screen index was bound to by the latest produced frame.
    private readonly nint[] m_boundScreenSources = new nint[SdfWorldEngine.MaxScreenSurfaces];

    // This frame's screen-source leases, moved into the frame-ring slot that samples them when the slot's fence has
    // retired the leases it held before.
    private readonly LeaseRetireList m_pendingScreenSourceFrames;

    // The frame-ring slot this frame's screen-source leases were adopted into, and the two callbacks the engine's
    // submission runs, converted once so a frame allocates no delegate.
    private int m_adoptedScreenSourceSlot;

    private readonly Action<IGpuQueueSubmitter> m_addScreenSourceWaits;
    private readonly Action<int> m_retireAndAdoptScreenSourceFrames;
    private readonly LeaseRetireList[] m_retainedScreenSourceFrames;
    private readonly ISdfScreenSources? m_screenSources;
    private readonly Dictionary<int, Func<SdfScreenSurfaceTransform?>> m_screenSurfaceTransforms;

    // The images the render graph handed the frame being produced, which the screens reading source instances bind;
    // null outside Produce.
    private RenderGraphExternalReads? m_reads;

    private readonly int m_viewportCapacity;

    private uint m_width;

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
    // instead of boxing IEnumerator on the render thread every ProduceFrame; the ctor copies the caller's map to match.
    private static readonly Dictionary<int, Func<SdfScreenSurfaceTransform?>> EmptyScreenSurfaceTransforms = new();

    private readonly NodeDescriptor m_descriptor = new(
        Name: "compute-sdf-world",
        SurfaceId: SurfaceId.New()
    );

    private static LeaseRetireList[] BuildScreenSourceFrameRing(int capacity) {
        var ring = new LeaseRetireList[SdfWorldEngine.FrameRingSize];

        for (var slot = 0; (slot < ring.Length); slot++) {
            ring[slot] = new LeaseRetireList(capacity: capacity);
        }

        return ring;
    }
    // Builds the engine once its pipelines are ready. The first call starts the pipeline build on the thread pool; until
    // it completes this returns false and the node presents nothing new, so a cold driver cache delays the first frame
    // rather than freezing the pump. The node builds an engine only when it has none (its first frame, the rebuild after
    // a device loss, which disposed the previous one, and the replacement at a new extent, which retired it), so a
    // refused build has no previous engine to fall back to: it presents nothing new and NotReadyReason names the
    // refusal, which includes an engine the device's descriptor heap cannot admit (GPU_DESCRIPTOR_HEAP, checked by the
    // engine's construction before it allocates). It is tried again only when its inputs change
    // (SdfWorldPipelineSource.TryBuild): the engine options this frame asks for (the program, the capacities), the
    // extent, a kernel reload request, the pipeline set, or the device, which a device loss replaces. The engine token
    // is raised exactly when a build creates an engine, never for a refused one.
    private bool EnsureEngine(IGpuDeviceContext gpuDevice, SdfFrame frame) {
        if (m_engine is not null) {
            return true;
        }

        m_deviceContext = gpuDevice;
        m_engine = m_pipelines.TryBuild(
            construct: static (pipelines, regionCopy, inputs) => {
                // The viewport CAPACITY: the first frame's count raised to the declared floor (the split-screen
                // envelope — the engine itself renders each frame's actual Views.Count, validated against it).
                if (inputs.Options.ViewportCapacity > SdfWorldEngine.MaxViewports) {
                    throw new ArgumentException(message: $"The world engine supports at most {SdfWorldEngine.MaxViewports} viewports; the frame/floor asks for {inputs.Options.ViewportCapacity}.");
                }

                return new SdfWorldEngine(
                    device: inputs.Device,
                    height: inputs.Height,
                    options: inputs.Options,
                    pipelines: pipelines,
                    regionCopy: regionCopy,
                    width: inputs.Width
                );
            },
            device: gpuDevice,
            hostsOnDirectX: false,
            includeBrickPipelines: (m_brickPoolVoxelCapacity > 0),
            inputsOf: static state => (
                state.Node,
                state.Device,
                Height: state.Node.m_height,
                Options: state.Node.EngineOptions(frame: state.Frame),
                ReloadRequest: state.Node.ShaderReloadStatus.RequestId,
                Width: state.Node.m_width
            ),
            kernels: m_kernels,
            label: "sdf-engine",
            state: (Node: this, Frame: frame, Device: gpuDevice)
        );

        if (m_engine is null) {
            return false;
        }

        return true;
    }
    private SdfWorldEngineOptions EngineOptions(SdfFrame frame) =>
        new(
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
            ViewportCapacity: ((uint)Math.Max(
                val1: frame.Views.Count,
                val2: m_viewportCapacity
            )),
            WorkLedger: m_work
        );
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
    // A provider can acquire an externally-written image (the camera shared-target tier). Keep that acquisition with
    // the SDF frame-ring slot whose command buffer samples it, and retire the old contents only after that slot's fence
    // signals. The preallocated lists avoid allocating a closure/list every produced frame.
    private void RetireAndAdoptScreenSourceFrames(int frameSlot) {
        var retained = m_retainedScreenSourceFrames[frameSlot];

        retained.RetireAll();
        m_pendingScreenSourceFrames.MoveTo(destination: retained);
        m_adoptedScreenSourceSlot = frameSlot;
    }
    // The leases the frame's submission samples are the slot's it adopted them into, so their waits ride that submission.
    private void AddScreenSourceWaits(IGpuQueueSubmitter submitter) => m_retainedScreenSourceFrames[m_adoptedScreenSourceSlot].AddWaits(submitter: submitter);
    // Binds either the screens that read a source instance or the ones the host renders, with each one's light. A
    // screen's read binds the image the graph handed this frame, whose lease is taken once however many screens show it;
    // a read the frame was not handed (a frame produced outside the graph, or a source the set does not run yet) binds
    // nothing, which the engine shades with its procedural screen material.
    private void BindScreenSources(SdfWorldEngine engine, bool rendered) {
        if (m_screenSources is not { } sources) {
            return;
        }

        var screens = sources.Screens;

        for (var position = 0; (position < screens.Count); position++) {
            var screen = screens[position];
            var read = sources.ReadOf(screen: screen);

            if ((read is null) != rendered) {
                continue;
            }

            nint handle = 0;

            if (read is null) {
                var lease = sources.Rendered(screen: screen);

                m_pendingScreenSourceFrames.Hold(lease: in lease);
                handle = lease.ImageViewHandle;
            } else if (
                (m_reads is { } reads) &&
                (reads.IndexOf(producer: read) is var index and >= 0)
            ) {
                if (!reads.IsTaken(index: index)) {
                    var lease = reads.Take(index: index);

                    m_pendingScreenSourceFrames.Hold(lease: in lease);
                }

                handle = reads[index].Lease.ImageViewHandle;
            }

            engine.SetScreenSource(
                imageViewHandle: handle,
                screenIndex: screen
            );
            engine.SetScreenLight(
                color: sources.Light(screen: screen),
                screenIndex: screen
            );
            m_boundScreenSources[screen] = handle;
        }
    }
    private void WriteDebugCapture(string path) {
        m_capturePng.ThrowIfUnavailable(path: path);

        if (!m_capturePng.TryWrite(
            height: ((int)m_engine!.OutputHeight),
            path: path,
            rgba: m_engine.ReadPixels().ToArray(),
            width: ((int)m_engine.OutputWidth)
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
        m_engine?.Dispose();
        m_engine = null;
        m_engineProduced = false;
        DisposeRetiringEngines();
        CancelShaderReload(reason: "the node was disposed");
        m_pipelines.Release();
        RetireAllScreenSourceFrames();
    }
    /// <inheritdoc/>
    public void OnDeviceLost() {
        // Device-loss recovery on the still-valid (lost) device. Unlike Dispose there is NO idle drain — the device is
        // lost, so nothing in flight will ever complete, and the host pump recreates the device immediately after. The
        // next ProduceFrame rebuilds the engine against the recreated device (construction re-uploads the program, so a
        // recovered device never renders an empty scene).
        // The lost submissions will never sample the leased screen sources, so the leases retire before the frame
        // source is told: a producer retiring its images then releases them at once, on the device that made them.
        RetireAllScreenSourceFrames();
        Array.Clear(array: m_boundScreenSources);
        m_frameSource.NotifyDeviceLost();
        m_engine?.Dispose();
        m_engine = null;
        m_engineProduced = false;
        // No lost submission will sample a replaced engine's output, so every held engine goes with the device.
        DisposeRetiringEngines();
        // A pipeline build or kernel reload still in flight is waited out and discarded before the host recreates the
        // device; the rebuilt engine builds its pipelines anew on the recreated one.
        CancelShaderReload(reason: "the device was lost");
        m_pipelines.Release();
        m_work.Invalidate();
        m_glyphAtlasInitialized = false;
        m_uploadedGlyphAtlas = null;
        m_deviceContext = null;
        // The frame an armed capture was owed is not produced on the lost device.
        m_debugCapture.RefuseForDeviceLoss();
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

        Volatile.Write(
            location: ref m_meshRegionDraws,
            value: frame.MeshDraws
        );

        // Until the engine's pipelines are built there is no engine and nothing new to present. The frame source still
        // captured this frame, so it keeps pace.
        if (!EnsureEngine(
            frame: frame,
            gpuDevice: gpuDevice
        )) {
            return default;
        }

        ApplyPendingShaderReload();
        ReconcileGlyphAtlas();
        m_engine!.DebugMode = m_debugMode;

        if (m_debugLabel is not null) {
            m_engine.DebugLabel = m_debugLabel;
        }

        // Screens reading source instances bind the images the render graph handed this frame before the offscreen views
        // render, so a view filming a screen samples the same image the room does, under the lease this node holds.
        m_pendingScreenSourceFrames.RetireAll();
        BindScreenSources(
            engine: m_engine!,
            rendered: false
        );

        // View RENDER: hand the frame source this frame's full context so a source hosting an offscreen ViewStack (a
        // diegetic camera / jumbotron) renders its views against the live device now — their images fresh before the
        // screens showing them bind below. An engine seam, default no-op.
        m_frameSource.RenderViews(context: in context);
        BindScreenSources(
            engine: m_engine!,
            rendered: true
        );

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

        ApplyScheduledViewExtents(
            engine: m_engine!,
            viewCount: frame.Views.Count
        );

        // The frame group every dispatch binds: the host's tick and its presentation clock. No pointer or paired camera
        // reaches the world engine; its cameras are the frame's views.
        m_engine!.FrameValues = new ShaderFrameValues(
            CameraFov: 0f,
            CameraPosition: default,
            CameraTarget: default,
            CameraUp: default,
            Pointer: default,
            PointerDown: false,
            PointerPresses: 0,
            Tick: context.ElapsedTicks,
            Time: context.ElapsedSeconds,
            TimeDelta: context.FrameDeltaSeconds
        );

        if (m_screenSources is null) {
            m_engine!.SubmitFrame(frame: frame);
        } else {
            m_engine!.SubmitFrameWithExternalResources(
                addWaits: m_addScreenSourceWaits,
                frame: frame,
                onFrameSlotAvailable: m_retireAndAdoptScreenSourceFrames
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
            width: m_engine.OutputWidth,
            height: m_engine.OutputHeight,
            format: GpuPixelFormat.R8G8B8A8Unorm
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
    /// <param name="width">The engine's extent width in pixels, the widest any view renders.</param>
    /// <param name="height">The engine's extent height in pixels, the tallest any view renders.</param>
    /// <param name="screenSources">What each program-declared <see cref="SdfScreenSurface.ScreenIndex"/> shows and the
    /// light it casts, read once per screen per produced frame, or <see langword="null"/> for a node that binds no
    /// screen. Every image is a same-device, shader-readable image view, sampled unresampled: the one the render graph
    /// hands <see cref="Produce"/> for the source instance a screen reads, or <see cref="ISdfScreenSources.Rendered"/>
    /// for a screen that reads none. The node holds each lease until the fence of the frame-ring slot whose submission
    /// sampled it, and adds the wait it carries (<see cref="GpuImageLease.Wait"/>) to that submission. A zero handle
    /// leaves the slot unbound this frame, which falls back to the procedural screen material. See
    /// <see cref="SdfWorldEngine.SetScreenSource"/> and <see cref="SdfWorldEngine.SetScreenLight"/>.</param>
    /// <param name="screenSurfaceTransforms">An optional map from a
    /// screen index to a provider of that screen's world-space sampling frame this frame — for a screen slab riding a
    /// dynamic transform (e.g. a slab riding a moving rig), whose sampling frame must move with the geometry every
    /// frame or it goes stale. A provider returning <see langword="null"/> leaves the program-declared (or
    /// program-declared) frame untouched this frame — a screen on static geometry simply omits its entry, or a provider
    /// may return null on frames where nothing moved to skip the write. Polled right after the screens bind; see
    /// <see cref="SdfWorldEngine.SetScreenSurface"/>.</param>
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
    /// <param name="viewportCapacity">An optional floor on the engine's viewport capacity — the envelope for a frame
    /// source whose per-frame view count grows past the first frame's (a split-screen host whose players join later).
    /// The engine renders each frame's actual view count up to the envelope; 0 sizes it by the first frame.</param>
    /// <param name="debugLabel">An optional GPU-capture debug-group name for this engine's whole recorded frame (see
    /// <see cref="SdfWorldEngine.DebugLabel"/>); a nested view engine passes <c>view:&lt;name&gt;</c> so a capture
    /// distinguishes it. Defaults to the engine's own default (<c>world</c>) when omitted. Presentation-only.</param>
    /// <param name="brickPoolVoxelCapacity">The carve-bake brick pool's voxel capacity (see
    /// <see cref="SdfWorldEngineOptions.BrickPoolVoxelCapacity"/>), frozen at construction. Defaults to
    /// <see cref="SdfWorldEngine.DefaultBrickPoolVoxelCapacity"/> (64 MB); pass 0 for a host whose scene never bakes
    /// carves (no pool is allocated).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public SdfEngineNode(SdfWorldPipelineCache pipelines, ISdfFrameSource frameSource, SdfWorldKernels kernels, uint width, uint height, ISdfScreenSources? screenSources = null, IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? screenSurfaceTransforms = null, int dynamicTransformCapacity = 0, int programWordCapacity = 0, int instanceCapacity = 0, int viewportCapacity = 0, string? debugLabel = null, int brickPoolVoxelCapacity = SdfWorldEngine.DefaultBrickPoolVoxelCapacity) {
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(frameSource);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "SDF engine node dimensions must be non-zero.");
        }

        m_debugLabel = debugLabel;
        // Copy the caller's transform map into a concrete Dictionary<,> (its struct enumerator is what the per-frame
        // foreach binds) rather than storing the read-only interface; the map is built once and never mutated after
        // construction, so the copy is observably identical. A null map shares the empty singleton.
        m_dynamicTransformCapacity = dynamicTransformCapacity;
        m_instanceCapacity = instanceCapacity;
        m_viewportCapacity = viewportCapacity;
        m_brickPoolVoxelCapacity = brickPoolVoxelCapacity;
        m_programWordCapacity = programWordCapacity;
        m_frameSource = frameSource;
        m_height = height;
        m_kernels = kernels;
        m_screenSources = screenSources;
        m_pendingScreenSourceFrames = new LeaseRetireList(capacity: (screenSources?.Screens.Count ?? 0));
        m_retainedScreenSourceFrames = BuildScreenSourceFrameRing(capacity: (screenSources?.Screens.Count ?? 0));
        m_screenSurfaceTransforms = ((screenSurfaceTransforms is null)
            ? EmptyScreenSurfaceTransforms
            : new Dictionary<int, Func<SdfScreenSurfaceTransform?>>(collection: screenSurfaceTransforms)
        );
        m_pipelines = new SdfWorldPipelineSource(cache: pipelines);
        m_width = width;
        m_writeDebugCapture = WriteDebugCapture;
        m_addScreenSourceWaits = AddScreenSourceWaits;
        m_retireAndAdoptScreenSourceFrames = RetireAndAdoptScreenSourceFrames;
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
    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <summary>Returns the image-view handle a screen was bound to by the latest produced frame, which the frame's
    /// offscreen views sample too: every screen reading a source instance is bound before they render.</summary>
    /// <param name="screen">The program-declared screen index, below <see cref="SdfWorldEngine.MaxScreenSurfaces"/>.</param>
    /// <returns>The handle, or zero for a screen bound to nothing, before the first frame and after a device loss.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="screen"/> is negative or not below
    /// <see cref="SdfWorldEngine.MaxScreenSurfaces"/>.</exception>
    public nint BoundScreenSource(int screen) => m_boundScreenSources[screen];

    /// <summary>Gets whether the node's engine is ready: its pipeline set is installed and the engine built from it has
    /// produced its first frame. It is false until the pipeline build that the first produced frame starts has completed
    /// and a frame has been submitted, and again after a device loss until the rebuilt engine has submitted one; a
    /// produced frame meanwhile returns an empty surface. It is the one readiness fact the console waits on and a
    /// capture's hold reads.</summary>
    public bool IsReady => m_engineProduced;
    /// <summary>Gets why the node is not <see cref="IsReady"/>, naming its pipeline build and how far it has come (for
    /// example <c>the engine's pipeline set is building (5 of 14 pipelines created)</c>) or the refusal of its engine's
    /// latest build, which is retried when its inputs change, or <see langword="null"/> once
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
