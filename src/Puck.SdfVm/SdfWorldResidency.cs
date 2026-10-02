using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>One screen surface's world-space sampling frame for one frame — the polled counterpart of
/// <see cref="SdfWorldTables.SetScreenSurface"/>'s parameters, bundled so a transform provider returns one value.</summary>
/// <param name="Origin">The front face's world-space center this frame.</param>
/// <param name="Right">The world-space axis the UV's U increases along this frame (need not be pre-normalized).</param>
/// <param name="Up">The world-space axis the UV's V increases against this frame (need not be pre-normalized).</param>
/// <param name="HalfWidth">The half-extent along <paramref name="Right"/> this frame.</param>
/// <param name="HalfHeight">The half-extent along <paramref name="Up"/> this frame.</param>
public readonly record struct SdfScreenSurfaceTransform(Vector3 Origin, Vector3 Right, Vector3 Up, float HalfWidth, float HalfHeight);
/// <summary>
/// One SDF frame source made resident on the render graph's device: the world a host renders, a camera view filming
/// it, or a session. Its views are <c>sdf.world</c> instances of the render graph, each running the package's passes
/// (<see cref="SdfWorldPasses"/>) against the tables the residency holds (<see cref="SdfWorldTables"/>): once a frame it
/// captures the frame source's frame, binds its screens, uploads a changed program and packs the frame into the tables,
/// and the first pass of the frame to record submits the tables' upload. Fully backend-neutral: it resolves the shared
/// device from <see cref="FrameContext.Host"/> and builds its tables once its pipeline set, leased from the composition's
/// cache and built off the frame thread, is ready.
/// <para>
/// Diegetic screens ride a separate, shading-only seam: a program may declare up to
/// <see cref="SdfWorldTables.MaxScreenSurfaces"/> static screen surfaces (see <see cref="SdfProgramBuilder"/>'s
/// screen-surface <c>ScreenSlab</c> overload), and each frame the residency binds (or unbinds) each one as its
/// <see cref="ISdfScreenSources"/> says: the image the render graph hands a view's passes for the instance the screen
/// reads, the mapping its face is drawn from and the light it casts. A screen riding a dynamic transform supplies a
/// <c>ScreenSurfaceTransforms</c> provider on its frame source, polled every frame, so its sampling frame tracks the
/// geometry the dynamic transform already moved.
/// </para>
/// <para>
/// The residency is shared by its holders: the host that created it and every view's recorder
/// (<see cref="Retain"/>). Its tables are released once the last holder releases it, after the device has finished every
/// submission that reads them.
/// </para>
/// </summary>
public sealed partial class SdfWorldResidency : IDisposable {
    private readonly int m_brickPoolVoxelCapacity;
    private readonly int m_dynamicTransformCapacity;
    private readonly ISdfFrameSource m_frameSource;
    private readonly int m_instanceCapacity;
    private readonly int m_programWordCapacity;
    private readonly Func<FrameContext, bool>? m_film;
    private readonly ISdfScreenSources? m_screenSources;
    private readonly Dictionary<int, Func<SdfScreenSurfaceTransform?>> m_screenSurfaceTransforms;

    // Completed while the residency is ready, which a view's passes await before they install (SdfWorldPasses.BuildAsync). A
    // reset replaces only a completed source, so every wait begun before the next build sees it complete; its
    // continuations run on the thread pool, never on the frame thread that completes it.
    private TaskCompletionSource m_ready = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    // The image-view handle each screen index was bound to by the latest recorded frame.
    private readonly nint[] m_boundScreenSources = new nint[SdfWorldTables.MaxScreenSurfaces];
    // Owned here rather than by the tables, so submission identities keep increasing across a device-loss rebuild.
    private readonly GpuWorkLedger m_work = new(
        framesInFlight: SdfWorldTables.FrameRingSize,
        name: "gpu.sdf-tables"
    );

    // The lease on the pipeline set the views record with, shared through the composition's pipeline cache, built off
    // the frame thread and kept across rebuilds until a device loss or disposal releases it.
    private readonly SdfWorldPipelineSource m_pipelines;

    private SdfKernelSet m_kernels;
    private IGpuDeviceContext? m_deviceContext;
    private SdfWorldTables? m_tables;
    // The latest captured frame, which every view of the residency renders, and whether this frame captured or packed
    // it and submitted its upload yet.
    private SdfFrame? m_frame;
    private bool m_captured;
    private bool m_packed;
    private bool m_submitted;
    // The capture extent: the largest extent a view asked for, which a frame source composes its regions at.
    private uint m_width;
    private uint m_height;

    private int m_holds = 1;

    private int m_debugMode;
    private bool m_disposed;
    private bool m_glyphAtlasInitialized;
    private SdfGlyphAtlas? m_uploadedGlyphAtlas;
    // The last captured frame's mesh draw count, which MeshDrawCount reads from another thread: the count, never the list,
    // which its producer may rewrite in place for the next frame.
    private int m_meshDrawCount;

    // Each view's signature at its latest render, or null before one: the cadence compares this frame's against it.
    private ulong?[] m_renderedSignatures = [];

    // Concrete Dictionary<,> (not the read-only interface) so the per-frame foreach binds the struct enumerator instead
    // of boxing IEnumerator on the render thread every frame; the ctor copies the caller's map to match.
    private static readonly Dictionary<int, Func<SdfScreenSurfaceTransform?>> EmptyScreenSurfaceTransforms = new();

    /// <summary>Initializes a new instance of the <see cref="SdfWorldResidency"/> class, held by its creator.</summary>
    /// <param name="pipelines">The composition's pipeline catalog the residency leases its pipelines from. The device,
    /// and the services the tables record through, come from the host context each frame.</param>
    /// <param name="frameSource">The per-frame source of the scene, cameras, and viewport regions.</param>
    /// <param name="kernels">The compiled world kernel set (SPIR-V for Vulkan, DXIL for Direct3D 12).</param>
    /// <param name="name">The residency's name, which labels its uploads in a GPU capture and names its counted work.</param>
    /// <param name="width">The extent, in pixels, the frame source composes its first frame's regions at; the widest view
    /// asked for widens it (<see cref="RequestExtent"/>).</param>
    /// <param name="height">The extent's height, in pixels.</param>
    /// <param name="screenSources">What each program-declared <see cref="SdfScreenSurface.ScreenIndex"/> shows and the
    /// light it casts, read once per screen per frame, or <see langword="null"/> for a residency that binds no screen.
    /// Every image is a same-device, shader-readable image view, sampled unresampled: the one the render graph hands a
    /// view's passes for the instance a screen reads, whose lease the pass holds until its submission has finished.</param>
    /// <param name="film">Prepares the frame source for the frame's capture, returning <see langword="false"/> when it
    /// films nothing this frame, as a camera whose anchor did not resolve; the latest frame then stands. <see langword="null"/>
    /// captures every frame.</param>
    /// <param name="dynamicTransformCapacity">An optional floor on the tables' dynamic-transform slot capacity. The tables
    /// always provision at least the first frame's transform count; a host whose moving-entity population grows over the
    /// run passes its peak here so the table is sized once.</param>
    /// <param name="programWordCapacity">An optional floor on the program's packed-word capacity (see
    /// <see cref="SdfWorldTablesOptions"/>): a frame source that hot-swaps programs declares its envelope here.</param>
    /// <param name="instanceCapacity">An optional floor on the instance count the instance grid and a view's per-tile masks
    /// are sized for.</param>
    /// <param name="brickPoolVoxelCapacity">The carve-bake brick pool's voxel capacity (see
    /// <see cref="SdfWorldTablesOptions.BrickPoolVoxelCapacity"/>), frozen at construction; 0 allocates no pool.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public SdfWorldResidency(SdfWorldPipelineCatalog pipelines, ISdfFrameSource frameSource, SdfKernelSet kernels, string name, uint width, uint height, ISdfScreenSources? screenSources = null, Func<FrameContext, bool>? film = null, int dynamicTransformCapacity = 0, int programWordCapacity = 0, int instanceCapacity = 0, int brickPoolVoxelCapacity = SdfWorldTables.DefaultBrickPoolVoxelCapacity) {
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(frameSource);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "An SDF residency's extent must be non-zero.");
        }

        m_brickPoolVoxelCapacity = brickPoolVoxelCapacity;
        m_dynamicTransformCapacity = dynamicTransformCapacity;
        m_film = film;
        m_frameSource = frameSource;
        m_height = height;
        m_instanceCapacity = instanceCapacity;
        m_kernels = kernels;
        m_pipelines = new SdfWorldPipelineSource(catalog: pipelines);
        m_programWordCapacity = programWordCapacity;
        m_screenSources = screenSources;
        m_screenSurfaceTransforms = ((frameSource.ScreenSurfaceTransforms is { } transforms)
            ? new Dictionary<int, Func<SdfScreenSurfaceTransform?>>(collection: transforms)
            : EmptyScreenSurfaceTransforms);
        m_width = width;
        Name = name;
    }

    /// <summary>Gets the residency's name.</summary>
    public string Name { get; }

    // The package frame the residency's frame was last started for (SdfWorldPasses.Begin).
    internal long PackageFrame { get; set; }

    /// <summary>Gets the residency's tables, or <see langword="null"/> before its pipeline set is built and its first
    /// frame captured, and after a device loss until they are rebuilt.</summary>
    public SdfWorldTables? Tables => m_tables;
    /// <summary>Gets the latest captured frame, which every view of the residency renders, or <see langword="null"/>
    /// before the first.</summary>
    public SdfFrame? Frame => m_frame;
    /// <summary>Gets the screens the residency binds, or <see langword="null"/> for none.</summary>
    public ISdfScreenSources? ScreenSources => m_screenSources;
    /// <summary>Gets the last uploaded program's packed word count, or 0 before the first upload — the live half of the
    /// <c>world.budget</c> cost sheet against <see cref="ProgramWordCapacity"/>.</summary>
    public int LiveProgramWords { get; private set; }
    /// <summary>Gets the last uploaded program's instance count, or 0 before the first upload.</summary>
    public int LiveProgramInstances { get; private set; }
    /// <summary>Gets the bounded volume count in the latest captured frame, before per-ray rejection.</summary>
    public int LiveVolumes { get; private set; }
    /// <summary>Gets the last uploaded program's Lipschitz step scale (1 = no clamp), or 0 before the first upload.</summary>
    public float LiveProgramStepScale { get; private set; }
    /// <summary>Gets the last uploaded program's step-scale binder (see <see cref="SdfProgram.StepScaleBinder"/>), or
    /// <see langword="null"/> before the first upload and whenever nothing unscoped binds the step scale.</summary>
    public SdfStepScaleBinder? LiveProgramStepScaleBinder { get; private set; }

    /// <summary>Gets the last uploaded program's non-unit field-scope clamps, or an empty list before upload. These
    /// candidate-local bounds remain active when <see cref="LiveProgramStepScale"/> is one.</summary>
    public IReadOnlyList<SdfFieldScopeClamp> LiveProgramFieldScopeClamps { get; private set; } = [];

    /// <summary>Gets the current program-word capacity, or the initial reserve before the tables are built.</summary>
    public int ProgramWordCapacity => (m_tables?.ProgramWordCapacity ?? m_programWordCapacity);
    /// <summary>Gets the bytes the tables' mesh region holds (<see cref="SdfWorldTables.MeshRegionBytes"/>), or zero before
    /// the tables are built.</summary>
    public ulong MeshRegionBytes => (m_tables?.MeshRegionBytes ?? 0UL);
    /// <summary>Gets the bytes the tables' regions hold, by the memory they live in (<see cref="SdfWorldTables.TableBytes"/>),
    /// or none before the tables are built.</summary>
    public GpuMemoryBytes TableBytes => (m_tables?.TableBytes ?? default);
    /// <summary>Gets the mesh draws of the last captured frame, the ones the mesh region holds once the frame
    /// renders.</summary>
    public int MeshDrawCount => Volatile.Read(location: ref m_meshDrawCount);
    /// <summary>Gets whether the residency renders its current frame: its tables are built from its pipeline set, its
    /// first frame captured and packed, and the views kernel its program selects, or a fuller one, is built. A program that
    /// selects a stripped views variant never waits for the full ISA's. It is false again after a device loss until the
    /// rebuilt tables are, and while a captured program waits for a views kernel that is still building or was refused,
    /// during which the residency holds the frame it last packed. A refused views kernel is built again only on a kernel
    /// reload (<see cref="RequestShaderReload"/>) or a device loss (<see cref="OnDeviceLost"/>).</summary>
    public bool IsReady => ((m_tables is not null) && (m_viewsWaiting is null) && Volatile.Read(location: ref m_ready).Task.IsCompletedSuccessfully);
    /// <summary>Gets whether every hold on the residency has been released (<see cref="Release"/>), after which it renders
    /// nothing.</summary>
    public bool IsReleased => m_disposed;
    /// <summary>Gets why the residency is not <see cref="IsReady"/>, naming its pipeline build and how far it has come,
    /// the refusal of its tables' latest build, which is retried when its inputs change, or the views kernel its program
    /// waits on and, when that kernel was refused, its failure, or <see langword="null"/> once it
    /// is ready. It builds a new string on each read, so a caller polls <see cref="IsReady"/> and reads this only to
    /// report.</summary>
    public string? NotReadyReason => (IsReady
        ? null
        : ((m_frame is null)
            ? $"residency '{Name}' has captured no frame"
            : (((m_tables is { } tables) && (m_viewsWaiting is { } views))
                ? ((tables.ViewsRefusal(kernel: views) is { } refusal)
                    ? $"residency '{Name}' holds its frame: the views kernel its program selects, '{SdfKernelSet.StemOf(kernel: views)}', was refused and is built again on a kernel reload or a device loss: {refusal.Message}"
                    : $"residency '{Name}' holds its frame until the views kernel its program selects, '{SdfKernelSet.StemOf(kernel: views)}', is built: {m_pipelines.Describe()}")
                : m_pipelines.Describe())));
    /// <summary>Gets why the residency cannot become ready until something it is built from changes, or
    /// <see langword="null"/> while it is ready or what it waits on is still building: the refused build of its tables
    /// (the device, the kernels, its options or a reload retries it) or the refused views kernel its program selects (a
    /// kernel reload or a device loss builds it again), naming the failure. A refusal is never waited out:
    /// <see cref="SdfWorldPasses"/> reports it as its instances' refusal (<see cref="SdfWorldPasses.RefusalOf"/>), the one
    /// channel a host reads, while <see cref="NotReadyReason"/> describes the wait.</summary>
    public string? Refusal {
        get {
            if (IsReady) {
                return null;
            }
            if (m_pipelines.Refusal is not null) {
                return $"residency '{Name}': {m_pipelines.Describe()}";
            }

            return (((m_tables is { } tables) && (m_viewsWaiting is { } views) && (tables.ViewsRefusal(kernel: views) is { } refusal))
                ? $"residency '{Name}': the views kernel its program selects, '{SdfKernelSet.StemOf(kernel: views)}', was refused and is built again on a kernel reload or a device loss: {refusal.Message}"
                : null);
        }
    }
    /// <summary>Gets the GPU work the residency's uploads recorded (<see cref="SdfWorldTables.Work"/>), published by the
    /// first <see cref="Prepare"/> of a frame that finds the upload's fence signaled; submission identities keep
    /// increasing across a device-loss rebuild.</summary>
    public IGpuWorkSource Work => m_work;
    /// <summary>Gets the GPU objects the residency's tables have created, over its whole life.</summary>
    public IWorkCounterSource WorkLifetime => m_work;
    /// <summary>Gets or sets the SDF debug view mode every view's pass block carries from the next frame.</summary>
    public int DebugMode {
        get => m_debugMode;
        set {
            m_debugMode = value;

            if (m_tables is not null) {
                m_tables.DebugMode = value;
            }
        }
    }

    /// <summary>Copies the currently uploaded packed program for inspection, or returns an empty array before the tables
    /// are built. The caller owns the copy; editing it cannot change the renderer.</summary>
    /// <returns>The live program's 32-bit words, excluding reserved capacity and per-frame transform/grid buffers.</returns>
    /// <remarks>Call on the render pump thread, as with the live console diagnostics. This performs a CPU copy, not a GPU
    /// readback. The packed format follows <see cref="SdfProgram"/> and is not a durable asset format.</remarks>
    public uint[] CopyLiveProgramWords() => (m_tables?.CopyLiveProgramWords() ?? []);
    /// <summary>Returns the image-view handle a screen was bound to by the latest recorded frame.</summary>
    /// <param name="screen">The program-declared screen index, below <see cref="SdfWorldTables.MaxScreenSurfaces"/>.</param>
    /// <returns>The handle, or zero for a screen bound to nothing, before the first frame and after a device loss.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="screen"/> is negative or not below
    /// <see cref="SdfWorldTables.MaxScreenSurfaces"/>.</exception>
    public nint BoundScreenSource(int screen) => m_boundScreenSources[screen];
    /// <summary>Takes another hold on the residency, which <see cref="Release"/> gives back.</summary>
    /// <exception cref="ObjectDisposedException">Every hold has been released.</exception>
    public void Retain() {
        var holds = Volatile.Read(location: ref m_holds);

        while (true) {
            ObjectDisposedException.ThrowIf(condition: (holds <= 0), instance: this);

            var observed = Interlocked.CompareExchange(comparand: holds, location1: ref m_holds, value: (holds + 1));

            if (observed == holds) {
                return;
            }

            holds = observed;
        }
    }
    /// <summary>Gives back a hold; the last one releases the residency's tables and its pipeline set, after the device has
    /// finished every submission that reads them.</summary>
    public void Release() {
        if (Interlocked.Decrement(location: ref m_holds) != 0) {
            return;
        }

        m_disposed = true;
        m_deviceContext.TryWaitIdle();
        m_tables?.Dispose();
        m_tables = null;
        ResetReady();
        CancelShaderReload(reason: "the residency was released");
        m_pipelines.Release();
    }
    /// <summary>Gives back the creator's hold (<see cref="Release"/>).</summary>
    public void Dispose() => Release();
    /// <summary>Returns a task that completes once the residency is ready (<see cref="IsReady"/>), for a build on the thread
    /// pool, which awaits it and holds no thread meanwhile. It never completes on the frame thread's stack.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The task, canceled with <paramref name="cancellationToken"/>.</returns>
    public Task WaitReadyAsync(CancellationToken cancellationToken) =>
        Volatile.Read(location: ref m_ready).Task.WaitAsync(cancellationToken: cancellationToken);
    /// <summary>Blocks, on the frame thread between frames, until the pipeline builds the residency's frames started have
    /// finished, successfully or not: its pipeline set's, its region-copy and mesh pass pipelines', and a kernel reload's.
    /// It takes nothing and starts nothing, so the next frame (<see cref="Prepare"/>) builds the tables, refuses a failed
    /// build or installs the reload as it would had frames been produced meanwhile. It serves a host that produces frames
    /// on its own thread and has nothing to present until the builds finish, such as an offscreen capture, which would
    /// otherwise produce empty frames while it waits.</summary>
    /// <param name="cancellationToken">The token that ends the wait; the builds keep running.</param>
    /// <returns><see langword="true"/> when a build was in flight, which has finished since; <see langword="false"/> when
    /// none was: the tables are built, their build was refused, or no frame has started a build, so only another frame
    /// can change what the residency presents.</returns>
    /// <exception cref="ObjectDisposedException">The residency is released.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public bool WaitPipelineBuilds(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var waited = m_pipelines.WaitFinished(cancellationToken: cancellationToken);

        return m_reloadBuild.WaitFinished(cancellationToken: cancellationToken) | waited;
    }
    /// <summary>Starts a frame: forgets the frame captured, packed and uploaded in the previous one, so the frame's first
    /// read of the residency captures the current one. The render graph's package calls it once per produced frame, before
    /// the frame is scheduled.</summary>
    public void BeginFrame() {
        m_captured = false;
        m_packed = false;
        m_submitted = false;
    }
    /// <summary>Widens the extent the frame source composes its frames at to cover a view's.</summary>
    /// <param name="width">The view's width, in pixels.</param>
    /// <param name="height">The view's height, in pixels.</param>
    public void RequestExtent(uint width, uint height) {
        m_width = Math.Max(val1: m_width, val2: width);
        m_height = Math.Max(val1: m_height, val2: height);
    }
    /// <summary>Returns the frame the residency renders this frame, capturing it from the frame source now when it has not
    /// captured it yet: a routed residency takes its host's frame before capturing its own, so both render with the
    /// same presentation clock and latched routes.</summary>
    /// <param name="context">The host's frame context, whose presentation delta and interpolation fraction the capture
    /// reads.</param>
    /// <returns>The frame, or <see langword="null"/> when the residency has filmed none yet.</returns>
    /// <exception cref="ObjectDisposedException">The residency is released.</exception>
    public SdfFrame? HostFrame(in FrameContext context) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (!m_captured) {
            m_captured = true;
            Capture(context: in context);
        }

        return m_frame;
    }
    /// <summary>Prepares the frame: captures it, builds the tables once the pipeline set is ready, applies a pending kernel
    /// reload and the glyph atlas, binds the screens, uploads a changed program and packs the frame into the tables. It
    /// runs once a frame, whichever of the residency's views asks first.</summary>
    /// <param name="context">The host's frame context, which resolves the device.</param>
    /// <returns><see langword="true"/> when the tables hold a frame the views can render.</returns>
    /// <exception cref="ObjectDisposedException">The residency is released.</exception>
    public bool Prepare(in FrameContext context) {
        _ = HostFrame(context: in context);

        if (!m_packed) {
            m_packed = true;
            // The ledger publishes an upload once a frame finds its fence signaled, standing frames included, so the one
            // upload a still view renders from is read back even though no later upload follows it.
            m_work.Poll();
            m_renders = PrepareOnce(context: in context);
        }

        return m_renders;
    }

    // The frame's one preparation, whose answer Prepare repeats for the rest of the frame.
    private bool PrepareOnce(in FrameContext context) {
        if (
            ((m_pendingFrame ?? m_frame) is not { } frame) ||
            !context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device) ||
            !EnsureTables(
                device: device,
                frame: frame
            )
        ) {
            return false;
        }

        var tables = m_tables!;

        ApplyPendingShaderReload();
        ReconcileGlyphAtlas(tables: tables);
        tables.DebugMode = m_debugMode;
        tables.DebugLabel = Name;
        BindScreens(
            frame: frame,
            tables: tables
        );

        foreach (var (screenIndex, provider) in m_screenSurfaceTransforms) {
            if (provider() is { } transform) {
                tables.SetScreenSurface(
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
        // glyph-cell grid; a null result clears the slot back to the image or unbound-glass path.
        if (m_frameSource.ScreenDecals is { } screenDecals) {
            foreach (var (screenIndex, provider) in screenDecals) {
                if (provider() is { } decal) {
                    tables.SetScreenDecal(
                        screenIndex: screenIndex,
                        columns: decal.Columns,
                        rows: decal.Rows,
                        distanceRange: decal.DistanceRange,
                        cellWords: decal.Cells.Span
                    );
                } else {
                    tables.ClearScreenDecal(screenIndex: screenIndex);
                }
            }
        }

        // A frame the tables packed already, as when the frame source filmed nothing this frame, stands as it was.
        if (!ReferenceEquals(
            objA: frame,
            objB: m_packedFrame
        )) {
            if (m_programPending) {
                // A program whose views kernel is still building, or was refused, is not uploaded: the residency holds the
                // frame it last packed, which the live program's views render, until that kernel is built, as any rebuild
                // does.
                if (tables.ViewsWaiting(program: frame.Program) is { } waiting) {
                    m_viewsWaiting = waiting;
                    m_pendingFrame = frame;
                    m_frame = (m_packedFrame ?? frame);
                    ResetReady();

                    return (tables.ViewsWaiting(program: null) is null);
                }

                m_programPending = false;
                UploadProgram(
                    program: frame.Program,
                    tables: tables
                );
            }

            tables.Pack(frame: frame);
            m_packedFrame = frame;
            m_frame = frame;
            m_pendingFrame = null;
            LiveVolumes = frame.Volumes.Count;
        }

        tables.UpdateTablesSignature();
        // The views render once the live program's views kernel, or a fuller one, is built; the residency is ready then.
        m_viewsWaiting = tables.ViewsWaiting(program: null);

        if (m_viewsWaiting is not null) {
            ResetReady();
            return false;
        }

        _ = Volatile.Read(location: ref m_ready).TrySetResult();

        return true;
    }

    /// <summary>Returns whether a view's latest render stands for this frame: the frame forces no render
    /// (<see cref="SdfWorldTables.ForcesRender"/>) and the view's signature is the one it last rendered at. A residency
    /// that filmed nothing this frame keeps its latest frame, which every view rendered already.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns><see langword="true"/> when the view need not render.</returns>
    public bool IsUnchanged(in FrameContext context, int view) {
        if (
            !Prepare(context: in context) ||
            (m_frame is not { } frame) ||
            (view >= frame.Views.Count) ||
            (view >= m_renderedSignatures.Length) ||
            (m_renderedSignatures[view] is not { } rendered) ||
            m_tables!.ForcesRender(frame: frame)
        ) {
            return false;
        }

        return (rendered == m_tables.ViewSignature(
            frame: frame,
            view: view
        ));
    }
    /// <summary>Submits the frame's upload once, when the first pass of the frame records, ahead of every view's
    /// submission.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <returns>The tables, which hold the frame the pass renders.</returns>
    /// <exception cref="InvalidOperationException">The residency has no tables: a pass records only once its residency is
    /// ready.</exception>
    public SdfWorldTables Submit(in FrameContext context) {
        if (!Prepare(context: in context)) {
            _ = (m_tables ?? throw new InvalidOperationException(message: $"Residency '{Name}' has no tables to render: {NotReadyReason}"));
        }

        var tables = m_tables!;

        if (!m_submitted) {
            m_submitted = true;
            tables.SubmitUpload();
        }

        return tables;
    }
    /// <summary>Records that a view rendered the frame the residency holds, whose signature it stands for until one it
    /// renders from changes.</summary>
    /// <param name="view">The view's index in the frame's views.</param>
    public void MarkRendered(int view) {
        if (
            (m_tables is not { } tables) ||
            (m_frame is not { } frame) ||
            (view >= frame.Views.Count)
        ) {
            return;
        }

        if (view >= m_renderedSignatures.Length) {
            Array.Resize(
                array: ref m_renderedSignatures,
                newSize: (view + 1)
            );
        }

        m_renderedSignatures[view] = tables.ViewSignature(
            frame: frame,
            view: view
        );
    }
    /// <summary>Returns the image a screen samples this frame from the images a view's render graph hands its pass, taking
    /// the image's lease into the frame's lease list the first time a pass of the frame samples it.</summary>
    /// <param name="view">The view's index in the residency's frame, whose own read the screen samples
    /// (<see cref="ISdfScreenSources.ReadOf"/>).</param>
    /// <param name="screen">The program-declared screen index.</param>
    /// <param name="reads">The images the pass's instance reads that its graph binds to no version, or
    /// <see langword="null"/>.</param>
    /// <param name="leases">The frame's lease list, which retires the lease after the frame's submission.</param>
    /// <returns>The image view, or zero when the screen shows nothing this frame.</returns>
    public nint ScreenImage(int view, int screen, RenderGraphExternalReads? reads, LeaseRetireList leases) {
        ArgumentNullException.ThrowIfNull(argument: leases);

        nint handle = 0;

        if (
            (m_screenSources?.ReadOf(
                screen: screen,
                view: view
            ) is { } read) &&
            (reads is not null) &&
            (reads.IndexOf(producer: read) is var index and >= 0)
        ) {
            if (!reads.IsTaken(index: index)) {
                var lease = reads.Take(index: index);

                leases.Hold(lease: in lease);
            }

            handle = reads[index].Lease.ImageViewHandle;
        }

        m_boundScreenSources[screen] = handle;

        return handle;
    }
    /// <summary>Returns the counts a view of the residency allocates its counted scratch by at an extent: one viewport,
    /// its tiles, and the instances the tables are provisioned for (<see cref="CapacityRevision"/>) and their per-tile
    /// mask words.</summary>
    /// <param name="width">The view's width, in pixels.</param>
    /// <param name="height">The view's height, in pixels.</param>
    /// <returns>The counts.</returns>
    public ShaderPipelineStorageCounts CountsAt(uint width, uint height) {
        var instances = CountedInstances;

        return new ShaderPipelineStorageCounts(
            Height: height,
            Width: width
        ) {
            InstanceMaskWords = ((ulong)SdfProgram.InstanceMaskStorageWordCountFor(instanceCount: instances)),
            Instances = ((ulong)instances),
            Tiles = (((ulong)((width + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize)) * ((height + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize)),
            Viewports = 1,
        };
    }

    /// <summary>Gets a revision that moves whenever <see cref="CountsAt"/> would return another value at an unchanged
    /// extent: the instance count the counts are sized by, the tables' capacity once they are built and, before, the
    /// capacity they will be built with for the frame the residency captured, so building them moves it only when the
    /// capacity differs.</summary>
    public long CapacityRevision => CountedInstances;

    // The instances a view's counted scratch is sized for: the tables' capacity, or before they are built the one they are
    // built with, the larger of the captured program's instances and the residency's floor.
    private int CountedInstances => (m_tables?.InstanceCapacity ?? Math.Max(
        val1: (m_frame?.Program.Instances.Count ?? 1),
        val2: m_instanceCapacity
    ));

    /// <summary>Releases every device object after the device was lost, without waiting for any submission; the next
    /// frame rebuilds the tables on the recreated device, and every view renders again.</summary>
    public void OnDeviceLost() {
        Array.Clear(array: m_boundScreenSources);
        Array.Clear(array: m_renderedSignatures);
        m_frameSource.NotifyDeviceLost();
        m_tables?.Dispose();
        m_tables = null;
        ResetReady();
        // A pipeline build or kernel reload still in flight is waited out and discarded before the host recreates the
        // device; the rebuilt tables build their pipelines anew on the recreated one.
        CancelShaderReload(reason: "the device was lost");
        m_pipelines.Release();
        m_work.Invalidate();
        m_glyphAtlasInitialized = false;
        m_uploadedGlyphAtlas = null;
        m_deviceContext = null;
    }

    // The frame the tables last packed, which a frame that films nothing leaves standing, and whether a frame captured
    // since the tables last uploaded a program carries another one.
    private SdfFrame? m_packedFrame;
    // A captured frame waiting for its program's views kernel. It survives a frame whose film gate captures nothing,
    // while m_frame exposes the packed frame the views render; a newer capture replaces it.
    private SdfFrame? m_pendingFrame;
    private Puck.Abstractions.Presentation.FrameCaptureRequest? m_convergence;
    private SdfFrame? m_frozenFrame;

    /// <summary>Freezes the presentation source for a converging capture.</summary>
    /// <param name="request">The request whose completion releases the snapshot.</param>
    public void BeginConvergence(Puck.Abstractions.Presentation.FrameCaptureRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);
        if (!ReferenceEquals(objA: m_convergence, objB: request)) {
            m_convergence = request;
            m_frozenFrame = null;
            m_frameSource.BeginConvergence(request: request);
        }
    }

    private bool m_programPending;
    // The views kernel the residency waits on: the live program's while the tables are built and it is not yet, or a
    // captured program's while the residency holds its packed frame until it is (SdfWorldTables.ViewsWaiting).
    private SdfKernel? m_viewsWaiting;
    // Whether the frame's preparation left the tables holding a frame the views can render.
    private bool m_renders;

    // Captures the frame from the frame source when it films one this frame, first advancing its brick planner against
    // the live tables, whose Ready flip bumps the source's content revision so the capture emits the brick this frame.
    private void Capture(in FrameContext context) {
        if (!(m_film?.Invoke(arg: context) ?? true)) {
            return;
        }

        var converging = (m_convergence is { Completion.IsCompleted: false });

        if (converging && (m_frozenFrame is { } frozen)) {
            m_frame = frozen with { ProgramChanged = false };
            m_pendingFrame = null;
            return;
        }
        if (!converging) {
            m_frozenFrame = null;
        }

        if (m_tables is { } tables) {
            m_frameSource.AdvanceBricks(bakes: tables);
        }

        var frame = m_frameSource.CaptureFrame(
            width: m_width,
            height: m_height,
            deltaSeconds: (converging ? 0f : ((float)context.FrameDeltaSeconds)),
            interpolationAlpha: ((float)context.InterpolationAlpha)
        );

        m_frame = frame;
        m_pendingFrame = null;
        if (converging) {
            m_frozenFrame = frame;
        }
        m_programPending |= frame.ProgramChanged;
        Volatile.Write(
            location: ref m_meshDrawCount,
            value: frame.MeshDraws.Count
        );
    }
    // Builds the tables once the pipelines are ready. The first call starts the pipeline build on the thread pool; until
    // it completes this returns false and no view renders, so a cold driver cache delays the first frame rather than
    // freezing the pump. A refused build has no previous tables to fall back to: NotReadyReason names the refusal, which
    // includes tables the device's descriptor heap cannot admit (GPU_DESCRIPTOR_HEAP). It is tried again only when its
    // inputs change (SdfWorldPipelineSource.TryBuild).
    private bool EnsureTables(IGpuDeviceContext device, SdfFrame frame) {
        if (m_tables is not null) {
            return true;
        }

        m_deviceContext = device;
        m_tables = m_pipelines.TryBuild(
            construct: static (pipelines, passes, inputs) => new SdfWorldTables(
                device: inputs.Device,
                impostorRaster: passes.ImpostorRaster,
                meshRaster: passes.MeshRaster,
                options: inputs.Options,
                pipelines: pipelines,
                regionCopy: passes.RegionCopy
            ),
            device: device,
            hostsOnDirectX: false,
            includeBrickPipelines: (m_brickPoolVoxelCapacity > 0),
            inputsOf: static state => (
                state.Residency,
                state.Device,
                Options: state.Residency.TablesOptions(frame: state.Frame),
                ReloadRequest: state.Residency.ShaderReloadStatus.RequestId
            ),
            kernels: m_kernels,
            label: Name,
            state: (Residency: this, Frame: frame, Device: device)
        );

        if (m_tables is not { } tables) {
            return false;
        }

        // The construction uploaded the frame's program; the frame is packed afresh.
        NoteProgram(program: frame.Program);
        m_packedFrame = null;
        m_programPending = false;
        Array.Clear(array: m_renderedSignatures);

        return true;
    }
    private void ResetReady() {
        var ready = Volatile.Read(location: ref m_ready);

        if (ready.Task.IsCompleted) {
            _ = Interlocked.CompareExchange(
                comparand: ready,
                location1: ref m_ready,
                value: new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously)
            );
        }
    }
    private SdfWorldTablesOptions TablesOptions(SdfFrame frame) =>
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
            WorkLedger: m_work
        );
    // Binds every screen's mapping, light and bound flag: a screen shows a source while it names an instance some view of
    // the frame reads, whose image each view's pass binds from the images its render graph hands it (ScreenImage).
    private void BindScreens(SdfWorldTables tables, SdfFrame frame) {
        if (m_screenSources is not { } sources) {
            return;
        }

        var screens = sources.Screens;

        for (var position = 0; (position < screens.Count); position++) {
            var screen = screens[position];
            var bound = false;

            for (var view = 0; (!bound && (view < frame.Views.Count)); view++) {
                bound = (sources.ReadOf(
                    screen: screen,
                    view: view
                ) is not null);
            }

            tables.SetScreenBound(
                bound: bound,
                screenIndex: screen
            );
            tables.SetScreenMapping(
                mapping: sources.MappingOf(screen: screen),
                screenIndex: screen
            );
            tables.SetScreenLight(
                color: sources.Light(screen: screen),
                screenIndex: screen
            );
        }
    }
    // A world load may replace (or remove) its immutable atlas without rebuilding the residency. Polling the reference is
    // cheap; SetGlyphAtlas performs the device drain and upload only when the catalog actually changes.
    private void ReconcileGlyphAtlas(SdfWorldTables tables) {
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
            tables.SetGlyphAtlas(
                rgbaPixels: ReadOnlyMemory<byte>.Empty,
                width: 0,
                height: 0
            );
        } else {
            tables.SetGlyphAtlas(
                rgbaPixels: glyphAtlas.Rgba,
                width: glyphAtlas.Width,
                height: glyphAtlas.Height
            );
        }

        m_uploadedGlyphAtlas = glyphAtlas;
        m_glyphAtlasInitialized = true;
    }
    private void UploadProgram(SdfWorldTables tables, SdfProgram program) {
        tables.UploadProgram(program: program);
        NoteProgram(program: program);
    }
    private void NoteProgram(SdfProgram program) {
        LiveProgramWords = program.Words.Length;
        LiveProgramInstances = program.Instances.Count;
        LiveProgramStepScale = program.StepScale;
        LiveProgramStepScaleBinder = program.StepScaleBinder;
        LiveProgramFieldScopeClamps = program.FieldScopeClamps;
    }
}
