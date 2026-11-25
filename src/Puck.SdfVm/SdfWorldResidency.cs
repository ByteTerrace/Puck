using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Waiting;
using Puck.Hosting;
using Puck.Hosting.Jobs;
using Puck.Maths;
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
/// and the render graph's preparation stages it (<see cref="Stage"/>), submitting the tables' upload before any pass
/// records. Fully backend-neutral: it resolves the shared
/// device from <see cref="FrameContext.Host"/> and builds its tables once its pipeline set, leased from the composition's
/// cache and built off the frame thread, is ready.
/// <para>
/// Diegetic screens ride a separate, shading-only seam: a program declares any number of screen surfaces (see
/// <see cref="SdfProgramBuilder"/>'s screen-surface <c>ScreenSlab</c> overload), each frame the residency holds a slot
/// (<see cref="SdfWorldTables.SettleScreenSlots"/>, <see cref="SdfWorldTables.MaxScreenSurfaces"/> at once, the nearest
/// first) for each that shows a source or a glyph grid, and it binds (or unbinds) each one as its
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
    private readonly int m_dynamicTransformCapacity;
    private readonly ISdfFrameSource m_frameSource;
    /// <summary>Gets the frame source the residency films: the composition of the world or scene it renders.</summary>
    public ISdfFrameSource FrameSource => m_frameSource;
    private readonly int m_instanceCapacity;
    private readonly int m_programWordCapacity;
    private readonly Func<FrameContext, bool>? m_film;
    private readonly ISdfScreenSources? m_screenSources;
    private readonly Dictionary<int, Func<SdfScreenSurfaceTransform?>> m_screenSurfaceTransforms;
    // The screens wanted resident this frame (BindScreens), the set that keeps each once, the nearness order past the
    // slots, and each glyph-grid provider's answer this frame; all reused, so a steady frame allocates nothing.
    private readonly List<int> m_wantedScreens = [];
    private readonly HashSet<int> m_wantedSeen = [];
    private (float Distance, int Screen)[] m_wantedOrder = [];
    private readonly List<(int Screen, SdfScreenDecalFrame? Decal)> m_screenDecalFrames = [];

    // Completed while the residency is ready, which a view's passes await before they install (SdfWorldPasses.BuildAsync). A
    // reset replaces only a completed source, so every wait begun before the next build sees it complete; its
    // continuations run on the thread pool, never on the frame thread that completes it.
    private TaskCompletionSource m_ready = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    // The image-view handle each screen index was bound to by the latest recorded frame.
    // The image each screen identity was last bound to, grown on the pack thread (BindScreens) to every identity it
    // wanted, before any recording of the frame writes it.
    private nint[] m_boundScreenSources = new nint[SdfWorldTables.MaxScreenSurfaces];
    // Owned here rather than by the tables, so submission identities keep increasing across a device-loss rebuild.
    private readonly GpuWorkLedger m_work = new(
        framesInFlight: SdfWorldTables.FrameRingSize,
        name: "gpu.sdf-tables"
    );

    // The lease on the pipeline set the views record with, shared through the composition's pipeline cache, built off
    // the frame thread and kept across rebuilds until a device loss or disposal releases it.
    private readonly SdfWorldPipelineSource m_pipelines;
    private readonly SdfSkyDetails m_skyDetails;

    private SdfKernelSet m_kernels;
    /// <summary>Gets the kernels the residency records with: those it was created with, or the last set a kernel reload
    /// installed. Read on the frame thread.</summary>
    public SdfKernelSet Kernels => m_kernels;
    private IGpuDeviceContext? m_deviceContext;
    private SdfWorldTables? m_tables;
    // The latest captured frame, which every view of the residency renders, and whether this frame captured or packed
    // it and submitted its upload yet.
    private SdfFrame? m_frame;
    private bool m_captured;
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
    // which its producer may rewrite in place for the next frame; and how many of those draws are glass.
    private int m_meshDrawCount;
    private int m_glassDrawCount;

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
    /// <param name="instanceCapacity">An optional floor on the instance count the bake's instance masks are sized
    /// for.</param>
    /// <param name="meshLibrary">The library the residency's bake shares meshes through with every other residency
    /// holding it (<see cref="SdfMeshPathBake.Library"/>), or <see langword="null"/> for a library of its own.</param>
    /// <param name="capturePackingDomain">Enables worker packing for a composition whose transform and draw packers
    /// share presentation state only within this identity. Only one worker may pack a domain at a time.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public SdfWorldResidency(SdfWorldPipelineCatalog pipelines, ISdfFrameSource frameSource, SdfKernelSet kernels, string name, uint width, uint height, ISdfScreenSources? screenSources = null, Func<FrameContext, bool>? film = null, int dynamicTransformCapacity = 0, int programWordCapacity = 0, int instanceCapacity = 0, SdfMeshLibrary? meshLibrary = null, object? capturePackingDomain = null) {
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(frameSource);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if ((capturePackingDomain is not null) && (frameSource is not SdfCompositionFrameSource)) {
            throw new ArgumentException(message: "Worker packing requires a composition frame source.", paramName: nameof(frameSource));
        }
        CapturePackingDomain = capturePackingDomain;

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "An SDF residency's extent must be non-zero.");
        }

        m_dynamicTransformCapacity = dynamicTransformCapacity;
        m_film = film;
        m_frameSource = frameSource;
        m_height = height;
        m_instanceCapacity = instanceCapacity;
        m_kernels = kernels;
        m_pipelines = new SdfWorldPipelineSource(catalog: pipelines);
        m_skyDetails = pipelines.SkyDetails;
        m_programWordCapacity = programWordCapacity;
        m_screenSources = screenSources;
        m_screenSurfaceTransforms = ((frameSource.ScreenSurfaceTransforms is { } transforms)
            ? new Dictionary<int, Func<SdfScreenSurfaceTransform?>>(collection: transforms)
            : EmptyScreenSurfaceTransforms);
        m_width = width;
        Name = name;
        m_ownedLibrary = ((meshLibrary is null) ? new SdfMeshLibrary() : null);
        MeshPathBake = new SdfMeshPathBake { Demand = MeshDemand, Identity = SdfMeshStore.IdentityOf(kernels: kernels), Library = (meshLibrary ?? m_ownedLibrary!), Residency = name };
        m_meshDresser = new SdfMeshDresser(bake: MeshPathBake);
        // A program built in the background plans its bake before the frame that adopts it, at the far distance of the
        // frames before it, which a restart at another plans again.
        frameSource.ObserveBuilds(built: (program, transforms) => {
            if (Volatile.Read(location: ref m_frame) is { } latest) {
                FollowRunningCode();
                MeshPathBake.PlanAhead(farDistance: latest.FarDistance, program: program, transforms: transforms);
            }
        });
    }

    // A hot-reload update applied to code the bake runs keys the meshes it bakes and stores from then on apart from the
    // build's (SdfMeshBakeIdentity.UnderRunningCode); an update elsewhere leaves the identity, and the store, as they are.
    private void FollowRunningCode() {
        var identity = MeshPathBake.Identity;
        var running = identity.UnderRunningCode(code: SdfMeshBakeClosure.Current);

        if (!ReferenceEquals(objA: identity, objB: running)) {
            MeshPathBake.Identity = running;
        }
    }

    /// <summary>Gets the residency's name.</summary>
    public string Name { get; }

    /// <summary>Gets the demand points the residency's mesh residency follows: every view of its latest captured frame,
    /// observed before the frame is dressed.</summary>
    public SdfMeshDemand MeshDemand { get; } = new();

    /// <summary>Describes the residency's mesh residency for <c>world.residency</c>: the latest bake's landing from its
    /// start, near the primary seat's eye and everywhere (<see cref="SdfMeshPathBake.DescribeResidency"/>), its floors
    /// (<see cref="SdfMeshPathBake.DescribeFloors"/>), the store's reads, the mesh region's landing
    /// (<see cref="SdfWorldTables.DescribeLanding"/>), what the raster passes selected of it
    /// (<see cref="SdfWorldTables.DescribeSelection"/>), the motion scopes its passes sign with and why each last moved
    /// (<see cref="SdfWorldTables.DescribeMotion"/>), its presentation's selector
    /// (<see cref="SdfResidencySelector.Describe"/>) with the background work's one budget, the tables' fixed cost (<see cref="SdfWorldTables.DescribeFixedCost"/>), the background GPU work's buffers
    /// (<see cref="SdfGpuWork.DescribeBytes"/>), the frame's sampled table (<see cref="SdfWorldTables.DescribeFrameSampled"/>),
    /// the latest program's emission, build and size, and the irradiance
    /// volume's probe cache.</summary>
    /// <param name="radius">The distance, in metres, from the primary seat's eye within which a group counts as near.</param>
    /// <param name="composition">The composed frame source whose programs the residency draws, when its frame source
    /// wraps one; <see langword="null"/> reads the frame source itself.</param>
    /// <returns>The description, one part a line.</returns>
    public IReadOnlyList<string> DescribeResidency(float radius, SdfCompositionFrameSource? composition = null) {
        composition ??= (m_frameSource as SdfCompositionFrameSource);
        var program = m_frame?.Program;

        return [
            MeshPathBake.DescribeResidency(radius: radius),
            MeshPathBake.DescribeFloors(),
            $"{(MeshPathBake.Library.Disk?.DescribeReads() ?? "store=none")} {SdfMesh.DescribeRestores()}",
            (m_tables?.DescribeLanding() ?? "region=none"),
            (m_tables?.DescribeSelection() ?? "selection=none"),
            (m_tables?.DescribeMotion() ?? "motion-scopes=none"),
            $"shadows {(m_tables?.DescribeShadows() is { Length: > 0 } shadows ? shadows : "none")}",
            ((m_tables is { } tables) ? $"{tables.Grant.Selector.Describe()}{tables.Grant.Selector.Work.Describe()}" : "selector=none"),
            (m_tables?.DescribeFixedCost() ?? "fixed-cost=none"),
            DescribeParking(),
            $"{(m_tables?.GpuWork.DescribeBytes() ?? "gpu-work=none")} late-stagings={LateStagings}{(m_tables?.GpuWork.Describe() ?? string.Empty)}",
            (m_tables?.DescribeFrameSampled() ?? "frame-sampled=none"),
            $"change-history-logical-bytes transforms={(m_frame?.MovedTransforms?.StorageBytes ?? 0)} draws={(m_frame?.DrawRows.Changes?.StorageBytes ?? 0)} dresser-scratch={m_meshDresser.ScratchBytes} staging-runs={(m_tables?.DrawChangeScratchBytes ?? 0)} (array payload; headers excluded)",
            string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"program-instances={(program?.Instances.Count ?? 0)} program-words={(program?.WordCount ?? 0)} program-builds={(composition?.ProgramBuilds ?? 0)} emit-ms={(composition?.EmitMilliseconds ?? 0d):0.0} build-ms={(composition?.BuildMilliseconds ?? 0d):0.0}"),
            MeshIrradiance.DescribeCache(),
            .. MeshPathBake.DescribeDemand(),
        ];
    }

    // The package frame the residency's frame was last started for (SdfWorldPasses.Begin).
    internal long PackageFrame { get; set; }

    /// <summary>Gets the residency's tables, or <see langword="null"/> before its pipeline set is built and its first
    /// frame captured, and after a device loss until they are rebuilt.</summary>
    public SdfWorldTables? Tables => m_tables;
    /// <summary>Names what keeps the meshes from being resident (<see cref="SdfWorldTables.MeshResidencyPending"/>),
    /// or <see langword="null"/> once resident. Read on the frame thread; never waits.</summary>
    public string? MeshResidencyPending => ((m_tables is { } tables) ? tables.MeshResidencyPending() : "its tables are not built");
    /// <summary>Gets whether the residency's meshes are still arriving, and only frames that render one of its views
    /// bring them: its bake owes the frame a mesh (<see cref="SdfMeshPathBake.Pending"/>), whose GPU sampling such a frame
    /// dispatches, or its tables still owe the staged meshes' words or their upload
    /// (<see cref="SdfWorldTables.MeshesArriving"/>), which land only after such a frame's upload. A frame that renders
    /// none of its views still prepares the residency and may pack a frame, which moves what the background work binds,
    /// so a residency nobody renders stops landing until one of its views renders again. A parked residency
    /// (<see cref="IsParked"/>) has nothing arriving until a reader wakes it. Read on the frame thread; never
    /// waits.</summary>
    public bool MeshesArriving => (!m_parked && (MeshPathBake.Pending || (m_tables?.MeshesArriving ?? false)));
    /// <summary>Gets whether the residency's meshes are still making their arrival, which a window rendered out of sight
    /// exists to bring: a group owes the frame its first mesh or an arrival's first detail over its floor
    /// (<see cref="SdfMeshPathBake.OwedGroups"/>), or the latest staging held a draw out because its mesh had not landed
    /// (<see cref="DrawsAwaitMeshes"/>). The bake's ordinary work in play (a tier the demand moved to, a resample) and
    /// another residency's landing in the shared mesh region owe no arrival, so a world whose view renders only to bring its
    /// meshes stops rendering once they have come, and can go unobserved and park. A parked residency has nothing arriving.
    /// Read on the frame thread; never waits.</summary>
    public bool MeshesFirstArriving => (!m_parked && ((MeshPathBake.OwedGroups > 0) || DrawsAwaitMeshes));
    /// <summary>Gets whether the residency owes work its preparation serves whether or not anything reads it this frame
    /// (its minimum): its meshes are arriving (<see cref="MeshesArriving"/>), whose bake plans and lands through a prepared
    /// frame, or its lighting is settling (<see cref="SdfMeshIrradiance.Settling"/>), whose trace a prepared frame stages,
    /// so a world becomes resident and lit before its glass shows. False once both are done, when only a reader prepares
    /// it, and while it is parked (<see cref="IsParked"/>), when only a reader wakes it. Read on the frame thread; never
    /// waits.</summary>
    public bool OwesPreparation => (!m_parked && (MeshesArriving || MeshIrradiance.Settling));
    /// <summary>Gets whether the latest staging held a draw out of the frame because its mesh had not landed
    /// (<see cref="SdfWorldTables.DrawsAwaitMeshes"/>); <see langword="false"/> before the tables are built.</summary>
    public bool DrawsAwaitMeshes => (m_tables?.DrawsAwaitMeshes ?? false);
    /// <summary>Gets the residency's copy of its latest captured frame, which every view of the residency renders, or
    /// <see langword="null"/> before the first. The residency writes its frames again at later captures, so a reader keeps
    /// no reference past the frame it read it in.</summary>
    public SdfFrame? Frame => m_frame;
    /// <summary>Gets the screens the residency binds, or <see langword="null"/> for none.</summary>
    public ISdfScreenSources? ScreenSources => m_screenSources;



    /// <summary>Gets the last uploaded program's packed word count, or 0 before the first upload — the live half of the
    /// <c>world.budget</c> cost sheet against <see cref="ProgramWordCapacity"/>.</summary>
    public int LiveProgramWords { get; private set; }
    /// <summary>Gets the last uploaded program's instance count, or 0 before the first upload.</summary>
    public int LiveProgramInstances { get; private set; }
    public int LiveProgramMaterials => (m_frame?.Program.MaterialCount ?? 0);
    public int LiveDynamicTransforms => (m_frame?.DynamicTransforms.Count ?? 0);
    /// <summary>Gets the bounded volume count in the latest captured frame, before per-ray rejection.</summary>
    public int LiveVolumes { get; private set; }
    /// <summary>Gets the last uploaded program's residual CPU query multiplier, or 0 before the first upload.</summary>
    public float LiveProgramStepScale { get; private set; }
    /// <summary>Gets the last uploaded program's step-scale binder (see <see cref="SdfProgram.StepScaleBinder"/>), or
    /// <see langword="null"/> before the first upload and whenever nothing unscoped binds the step scale.</summary>
    public SdfStepScaleBinder? LiveProgramStepScaleBinder { get; private set; }

    /// <summary>Gets the last uploaded program's non-unit field-scope clamps, or an empty list before upload. These
    /// candidate-local bounds remain active when <see cref="LiveProgramStepScale"/> is one.</summary>
    public IReadOnlyList<SdfFieldScopeClamp> LiveProgramFieldScopeClamps { get; private set; } = [];

    /// <summary>Gets the last uploaded program's directory segment count, or 0 before the first upload.</summary>
    public int LiveProgramSegments { get; private set; }
    /// <summary>Gets how many of the last uploaded program's directory segments carry a rigid-leaf plan, or 0 before the
    /// first upload.</summary>
    public int LiveProgramRigidSegments { get; private set; }
    /// <summary>Gets the current program-word capacity, or the initial reserve before the tables are built.</summary>
    public int ProgramWordCapacity => (m_tables?.ProgramWordCapacity ?? m_programWordCapacity);
    /// <summary>Gets the bytes the tables' mesh region holds (<see cref="SdfWorldTables.MeshRegionBytes"/>), or zero before
    /// the tables are built.</summary>
    public ulong MeshRegionBytes => (m_tables?.MeshRegionBytes ?? 0UL);
    /// <summary>Gets the bytes the latest staged frame's draw records and meshes take of the mesh region
    /// (<see cref="SdfWorldTables.MeshRegionUsedBytes"/>), or zero before the tables are built.</summary>
    public ulong MeshRegionUsedBytes => (m_tables?.MeshRegionUsedBytes ?? 0UL);
    /// <summary>Gets the most bytes the mesh region places (<see cref="SdfWorldTables.MeshRegionLimitBytes"/>), or zero
    /// before the tables are built.</summary>
    public ulong MeshRegionLimitBytes => (m_tables?.MeshRegionLimitBytes ?? 0UL);
    /// <summary>Gets the bytes the tables' regions hold, by the memory they live in (<see cref="SdfWorldTables.TableBytes"/>),
    /// or none before the tables are built.</summary>
    public GpuMemoryBytes TableBytes => (m_tables?.TableBytes ?? default);
    /// <summary>Gets the mesh draws of the last captured frame, the ones the mesh region holds once the frame
    /// renders.</summary>
    public int MeshDrawCount => Volatile.Read(location: ref m_meshDrawCount);
    /// <summary>Gets how many of <see cref="MeshDrawCount"/> are glass draws (<see cref="SdfMeshDraw.Glass"/>), which
    /// the glass pass draws over the opaque image.</summary>
    public int GlassDrawCount => Volatile.Read(location: ref m_glassDrawCount);
    /// <summary>Gets whether the residency renders its current frame: its tables are built from its pipeline set, its
    /// first frame captured and packed, and every pipeline its views record with built. It is false again after a device
    /// loss until the rebuilt tables are, and while a captured frame waits for a pipeline that is still building or was
    /// refused, during which the residency holds the frame it last packed and its bake samples meanwhile. A refused
    /// kernel is built again only on a kernel reload (<see cref="RequestShaderReload"/>) or a device loss
    /// (<see cref="OnDeviceLost"/>), a refused pass pipeline on a device loss.</summary>
    public bool IsReady => ((m_tables is not null) && (m_pipelineWaiting is null) && Volatile.Read(location: ref m_ready).Task.IsCompletedSuccessfully);
    /// <summary>Gets whether every hold on the residency has been released (<see cref="Release"/>), after which it renders
    /// nothing.</summary>
    public bool IsReleased => m_disposed;
    /// <summary>Gets why the residency is not <see cref="IsReady"/>, naming its pipeline build and how far it has come,
    /// the refusal of its tables' latest build, which is retried when its inputs change, or the pipeline its frame
    /// waits on and, when that pipeline was refused, its failure, or <see langword="null"/> once it
    /// is ready. It builds a new string on each read, so a caller polls <see cref="IsReady"/> and reads this only to
    /// report.</summary>
    public string? NotReadyReason => (IsReady
        ? null
        : ((m_frame is null)
            ? ((SourceWaiting is { } waiting) ? $"residency '{Name}' has captured no frame: {waiting}" : $"residency '{Name}' has captured no frame")
            : (((m_tables is not null) && (m_pipelineWaiting is { } pipeline))
                ? ((pipeline.Refusal is { } refusal)
                    ? $"residency '{Name}' holds its frame: the '{pipeline.Name}' pipeline was refused and is built again on a kernel reload or a device loss: {refusal.Message}"
                    : $"residency '{Name}' holds its frame until the '{pipeline.Name}' pipeline is built: {m_pipelines.Describe()}")
                : (((m_tables is null) && StandsParked)
                    ? $"residency '{Name}' is parked: it gave its tables back and builds them again when a render or the sight wants it"
                    : m_pipelines.Describe()))));
    /// <summary>Gets why the residency cannot become ready until something it is built from changes, or
    /// <see langword="null"/> while it is ready or what it waits on is still building: the refused build of its tables
    /// (the device, the kernels, its options or a reload retries it) or a refused pipeline its frame waits on (a kernel reload or a device loss builds it again), naming the failure. A refusal is never waited out:
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

            return (((m_tables is not null) && (m_pipelineWaiting is { Refusal: { } refusal } waiting))
                ? $"residency '{Name}': the '{waiting.Name}' pipeline was refused and is built again on a kernel reload or a device loss: {refusal.Message}"
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
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The handle, or zero for a screen bound to nothing (one holding no slot included), before the first frame
    /// and after a device loss.</returns>
    public nint BoundScreenSource(int screen) {
        var bound = m_boundScreenSources;

        return ((((uint)screen) < ((uint)bound.Length)) ? bound[screen] : 0);
    }
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
    /// <summary>Gives back a hold; the last one hands the residency's tables, irradiance, meshes and pipeline set to the
    /// device's retirement (<see cref="GpuDeviceRetirement"/>), which releases them once the device has finished every
    /// submission that reads them, so releasing a residency with views still in flight never waits for the queue. A
    /// residency with no device, before its first frame or after a loss, releases them at once.</summary>
    public void Release() {
        if (Interlocked.Decrement(location: ref m_holds) != 0) {
            return;
        }

        m_disposed = true;
        if (m_deviceContext is { } device) {
            device.Services.Retirement.Retire(item: new Puck.Abstractions.DisposeOnce(action: () => ReleaseDeviceObjects(deviceLost: false)));
        } else {
            ReleaseDeviceObjects(deviceLost: false);
        }
    }

    // The one release of the residency's device objects: its tables, its pipeline set and specializations, and a kernel
    // reload in flight. A final release runs once, after the last hold: at once without a device, otherwise from the
    // device's retirement once no submission reads the objects (GpuDeviceRetirement.Poll, on the frame thread), or at its
    // drain after a loss or at teardown; it also lets go of the residency's bake, lighting and meshes. A loss keeps those,
    // reports every shading submission as unproven and readies the residency to rebuild on the recreated device.
    //
    // Nothing here waits for a build: a pipeline lease still being taken, a last lease whose creation is still in the
    // driver, a specialization's compile and a reload's builds are each retired into the catalog's table, which outlives
    // the residency and discards each on the pool as it finishes, and a reload's kernel compiles are canceled without
    // waiting. A loss alone then drains that table, since no lease of the lost device may outlive its recovery; the host's
    // teardown drains it before the device goes.
    private void ReleaseDeviceObjects(bool deviceLost) {
        JoinPack();
        if (deviceLost) {
            LoseShade();
            Array.Clear(array: m_boundScreenSources);
            Array.Clear(array: m_renderedSignatures);
            m_frameSource.NotifyDeviceLost();
        } else {
            MeshIrradiance.Dispose();
        }
        m_tables?.Dispose();
        m_tables = null;
        m_probeTables = null;
        ResetReady();
        CancelShaderReload(reason: (deviceLost ? "the device was lost" : "the residency was released"));
        ReleaseShadePipelines();
        m_pipelines.Release(retired: m_pipelines.Catalog.RetiredBuilds);
        if (deviceLost) {
            m_pipelines.Catalog.RetiredBuilds.Drain();
            m_work.Invalidate();
            m_glyphAtlasInitialized = false;
            m_uploadedGlyphAtlas = null;
            m_deviceContext = null;
        } else {
            m_meshDresser.Dispose();
            m_packLane?.Dispose();
            MeshPathBake.Retire();
            m_ownedLibrary?.Dispose();
        }
    }

    // The library the residency made for itself when its host shared none, which it disposes on its last release.
    private readonly SdfMeshLibrary? m_ownedLibrary;

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

        if (!m_reloadBuild.IsPending && (m_reloadLoad is { IsCompleted: false } load)) {
            // A failed load is reported by the next frame, which finishes the request.
            load.WaitFinished(cancellationToken: cancellationToken);

            return true;
        }

        return m_reloadBuild.WaitFinished(cancellationToken: cancellationToken) | waited;
    }
    /// <summary>Starts a frame: forgets the frame captured, packed and uploaded in the previous one, so the frame's first
    /// read of the residency captures the current one. The render graph's package calls it once per produced frame, before
    /// the frame is scheduled, for every residency it holds whether or not the frame reads it.</summary>
    /// <param name="frameSeconds">The presentation interval the frame spans, in seconds, or zero when the caller keeps no
    /// presentation clock. A frame that captured nothing hands its interval to the next capture, so a residency nothing
    /// read for several frames advances its source by all of them when it is read again.</param>
    public void BeginFrame(double frameSeconds = 0) {
        JoinPack();
        if (m_begun && !m_captured) {
            m_skippedSeconds += m_frameSeconds;
            // A frame nothing read shows the player nothing of the residency: its background work takes the least weight.
            m_tables?.GpuWork.Weigh(share: 0d);
        }
        m_begun = true;
        m_tables?.ForgetViewInputs();
        m_tables?.BeginShadowFrame();
        m_frameSeconds = Math.Max(val1: frameSeconds, val2: 0);
        m_captured = false;
        m_capturePacking = default;
        m_pack = PackStage.None;
        m_packFrame = null;
        m_packDevice = null;
        m_submitted = false;
    }

    /// <summary>Gets the presentation time, in seconds, from the frame the residency last captured to the frame it has
    /// started: the started frame's span and the spans of the frames before it that captured nothing. The minimum of a
    /// residency nothing reads is rated by it (<see cref="SdfWorldPasses.BeginFrame"/>).</summary>
    internal double SecondsSinceCapture => (m_skippedSeconds + m_frameSeconds);

    // Whether a frame has started, the interval the current frame spans, and the intervals of the frames since the last
    // capture that captured nothing (BeginFrame), which the next capture's source advances by beside its own.
    private bool m_begun;
    private double m_frameSeconds;
    private double m_skippedSeconds;
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

        if (!m_captured) { Capture(context: in context); }

        return m_frame;
    }
    /// <summary>Prepares the frame: captures it, builds the tables once the pipeline set is ready, applies a pending kernel
    /// reload and the glyph atlas, binds the screens, uploads a changed program and packs the frame into the tables. It
    /// runs once a frame, whichever of the residency's views asks first.</summary>
    /// <param name="context">The host's frame context, which resolves the device.</param>
    /// <returns><see langword="true"/> when the tables hold a frame the views can render.</returns>
    /// <exception cref="ObjectDisposedException">The residency is released.</exception>
    public bool Prepare(in FrameContext context) {
        JoinPack();
        _ = HostFrame(context: in context);
        _ = BeginPack(context: in context);
        RunPack();
        EndPack();

        return m_renders;
    }

    // The frame's one preparation in three steps, whose answer Prepare repeats for the rest of the frame. BeginPack and
    // EndPack run on the frame thread; RunPack runs on whichever thread holds the residency between them, the frame
    // thread or one partition of a fork the frame thread joins before it ends the pack (SdfWorldPasses.ReadsPrepared).
    //
    // Invariant: RunPack reads and writes only this residency, its tables, bake, lighting and exposures, the frame it
    // packs and the device, never another residency or the host that filmed the frame. It holds because everything of
    // the preparation that asks the host (the screens' sources, surfaces, decals and texts) or changes the device's
    // programs is done in BeginPack, and the upload is submitted in EndPack; a step added to the preparation that reads
    // outside the residency belongs in BeginPack. Nothing checks it but SubmitOnce's refusal inside a partition.
    private enum PackStage : byte {
        None,
        Begun,
        Ran,
        Ended,
    }

    private PackStage m_pack;
    // What BeginPack resolved for RunPack: the frame the tables take and the device they are on.
    private SdfFrame? m_packFrame;
    private IGpuDeviceContext? m_packDevice;
    private FrameContext m_packContext;

    /// <summary>Gets whether the frame's packing may run beside other residencies' as a partition of a fork: it has
    /// begun, and the frame neither locks presentation to a tick nor converges its lighting, both of which settle
    /// their work inside the preparation and wait for it there.</summary>
    internal bool PacksBeside => ((m_pack == PackStage.Begun) && (m_packFrame is { TickLocked: false, ConvergesLighting: false }) && !HoldsFrame);

    /// <summary>Begins the frame's preparation on the frame thread, once a frame: publishes the uploads whose fences
    /// signaled, builds the tables once their pipelines are ready, applies a pending reload and the glyph atlas, binds
    /// the screens, and uploads a changed program.</summary>
    /// <param name="context">The host's frame context, which resolves the device.</param>
    /// <param name="jobs">The pool the pack forks its moved slots' judgements across, or <see langword="null"/> to
    /// judge them on the packing thread.</param>
    /// <returns><see langword="true"/> when the frame is left to <see cref="RunPack"/>; <see langword="false"/> when the
    /// preparation has already begun this frame or settled its answer here (no frame, no tables, or a frame held while
    /// a pipeline builds), or the residency stands parked (<see cref="StandsParked"/>), which settles nothing for the
    /// frame.</returns>
    internal bool BeginPack(in FrameContext context, Puck.Hosting.Jobs.JobPool? jobs = null) {
        if (m_pack != PackStage.None) { return false; }
        // The ledger publishes an upload once a frame finds its fence signaled, standing frames included, so the one
        // upload a still view renders from is read back even though no later upload follows it.
        m_work.Poll();
        if (!PrepareTables(context: in context, device: out var device, frame: out var frame)) {
            m_renders = false;
            // A preparation that finds the residency standing parked builds nothing and settles nothing, so a render the
            // frame asks for later (Stage, BeginStage) wakes it and prepares it whole in this same frame.
            if (!StandsParked) { m_pack = PackStage.Ran; }
            return false;
        }
        m_packDevice = device;
        m_packFrame = frame;
        m_packContext = context;
        m_tables!.Jobs = jobs;
        m_pack = PackStage.Begun;
        return true;
    }
    /// <summary>Packs the frame <see cref="BeginPack"/> left into the tables and stages its meshes, shading, lighting
    /// and exposure. It runs on one thread, which need not be the frame thread, and touches only the residency's own
    /// state. A pack already running beside the frame thread (<see cref="PackAhead"/>) is joined instead.</summary>
    internal void RunPack() {
        JoinPack();
        RunPackHeld();
    }

    // The pack run beside the frame thread (PackAhead), on the residency's own lane, until a door joins it (JoinPack).
    //
    // Invariant: while the pack runs on the lane, the frame thread changes nothing the pack reads or writes of this
    // residency. The pack keeps to the residency (the RunPack invariant above), so the frame thread can go on with other
    // residencies' captures; every member of this residency that steps its frame or reads what the pack writes joins
    // the lane first: BeginFrame, Upkeep, Prepare (and through it Stage, BeginStage and IsUnchanged), RunPack, EndPack,
    // Park and the release of the device objects. Frame and Tables stand while the pack runs: BeginPack set both, and
    // the pack replaces neither. Nothing checks a new member that reads pack state without joining; review holds that.
    private JobLane? m_packLane;
    private PackAheadUnit? m_packAheadUnit;
    private bool m_packPosted;
    // Whether the calling thread runs a residency's pack on its lane, where the frame's submission is refused as it is in
    // a partition (SubmitOnce).
    [ThreadStatic] private static bool PackingAhead;

    private static readonly WaitReason PackWait = new(Subsystem: "sdf.residency", WaitsFor: "the pack run beside the frame");

    private sealed class PackAheadUnit(SdfWorldResidency owner) : IJobLaneUnit {
        public void Run() {
            PackingAhead = true;
            try { owner.RunPackHeld(); } finally { PackingAhead = false; }
        }
    }

    /// <summary>Hands the frame's pack, once <see cref="BeginPack"/> left it, to the residency's own lane, where it runs
    /// beside the frame thread until a door of the residency joins it; the frame thread goes on meanwhile with work that
    /// touches no state of this residency (other residencies' captures). A frame whose pack keeps to the frame thread
    /// (<see cref="PacksBeside"/>) is left to <see cref="RunPack"/>.</summary>
    /// <returns><see langword="true"/> when the pack was handed to the lane.</returns>
    internal bool PackAhead() {
        if (m_packPosted || !PacksBeside) { return false; }
        m_packLane ??= new JobLane(name: ("Puck pack: " + Name), priority: JobPool.WorkerPriority);
        m_packLane.Post(unit: (m_packAheadUnit ??= new PackAheadUnit(owner: this)));
        m_packPosted = true;
        SdfCaptureWork.ProcessWork.Count(kind: SdfCaptureWork.PackedAhead);
        return true;
    }
    // Waits for the pack PackAhead posted, if any, and takes it: what it wrote may be read, and what it read may change,
    // once this returns. Rethrows what the pack threw. The frame thread's door; the lane's own thread never joins itself.
    private void JoinPack() {
        if (!m_packPosted || PackingAhead) { return; }

        var lane = m_packLane!;
        var outcome = WaitOutcome.DeadlinePassed;

        if (!lane.Ready) { SdfCaptureWork.ProcessWork.Count(kind: SdfCaptureWork.PackAheadWaits); }
        try {
            outcome = lane.Collect(deadline: WaitDeadline.Synchronous(), reason: in PackWait);
        } finally {
            // A pack that threw is collected too: the lane is free, and the throw reaches the caller.
            if ((outcome == WaitOutcome.Completed) || !lane.InFlight) { m_packPosted = false; }
        }
        if (outcome == WaitOutcome.DeadlinePassed) { HostWaits.ThrowIfPending(pending: PackWait); }
    }
    private void RunPackHeld() {
        if (m_pack != PackStage.Begun) { return; }
        try {
            m_renders = PackPrepared(context: in m_packContext, device: m_packDevice!, frame: m_packFrame!);
        } finally {
            m_packFrame = null;
            m_packDevice = null;
            m_pack = PackStage.Ran;
        }
    }
    /// <summary>Ends the frame's preparation on the frame thread: weighs the background work by the share of the display
    /// the frame is shown over, and submits the upload of tables whose frame waits on a pipeline.</summary>
    internal void EndPack() {
        JoinPack();
        if (m_pack != PackStage.Ran) { return; }
        m_pack = PackStage.Ended;
        // The background work draws on the presentation's budget by the share of the display the frame is shown over.
        if ((m_tables is { } weighed) && (m_frame is { } shown)) { weighed.GpuWork.Weigh(share: shown.DisplayShare); }
        // Tables whose frame waits on a pipeline still serve their background work: the upload is what lets the bake
        // bind the program the construction uploaded, so it samples while the driver compiles the rest.
        if (!m_renders && (m_tables is { } tables) && (m_pipelineWaiting is not null)) { SubmitOnce(tables: tables); }
    }

    // Binds one screen slot's glyph-cell grid, or clears the slot when its provider shows no dense text this frame.
    private static void BindScreenDecal(SdfWorldTables tables, int screenIndex, SdfScreenDecalFrame? frame) {
        if (frame is { } decal) {
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
    // The preparation's first half (BeginPack): everything that asks the host or changes what the device holds beside the
    // tables' own regions. Returns false when the residency has no frame, device or tables, or holds its packed frame
    // while a pipeline builds.
    private bool PrepareTables(in FrameContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out IGpuDeviceContext? device, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfFrame? frame) {
        frame = (m_pendingFrame ?? m_frame);
        device = null;
        if (
            (frame is null) ||
            !context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var resolved) ||
            !EnsureTables(
                device: resolved,
                frame: frame
            )
        ) {
            return false;
        }
        device = resolved;

        var tables = m_tables!;

        ApplyPendingShaderReload();
        ReconcileGlyphAtlas(tables: tables);
        tables.DebugMode = m_debugMode;
        tables.DebugLabel = Name;
        tables.MeshDemand = MeshDemand;
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

        // Screen DECALS (the material-level text tier): a resident screen showing dense reading text this frame binds its
        // glyph-cell grid, asked of its provider once in BindScreens; a null result clears the slot back to the image or
        // unbound-glass path.
        foreach (var (screenIndex, decal) in m_screenDecalFrames) {
            BindScreenDecal(frame: decal, screenIndex: screenIndex, tables: tables);
        }

        // Surface texts (text paints): every slot is asked each frame, so a slot whose text went away clears.
        if (m_frameSource.SurfaceTexts is { } surfaceTexts) {
            for (var slot = 0; (slot < SdfSurfaceTextLayout.MaxTexts); slot++) {
                if (surfaceTexts(arg: slot) is { } text) {
                    tables.SetSurfaceText(distanceRange: text.DistanceRange, glyphWords: text.Glyphs.Span, slot: slot);
                } else {
                    tables.ClearSurfaceText(slot: slot);
                }
            }
        }

        if (!ReferenceEquals(
            objA: frame,
            objB: m_packedFrame
        )) {
            // A frame waits while a pipeline its views record with still builds.
            if (tables.FrameWaiting() is { } waiting) {
                m_pipelineWaiting = waiting;
                m_pendingFrame = frame;
                m_frame = (m_packedFrame ?? frame);
                ResetReady();
                return false;
            }
            if (m_programPending) {
                // A program whose pipelines are still building, or were refused, is not uploaded: the residency holds the
                // frame it last packed, which the live program's views render, until they are built, as any rebuild does.
                m_programPending = false;
                UploadProgram(
                    delta: frame.ProgramDelta,
                    program: frame.Program,
                    tables: tables
                );
            }
            // The frame the pack takes is the residency's frame from here, on the frame thread, so a pack running beside
            // it (PackAhead) leaves Frame standing.
            m_frame = frame;
            m_pendingFrame = null;
        }

        return true;
    }
    // The preparation's second half (RunPack), which keeps to the residency's own state: packs the frame, or stages the
    // meshes that landed for a frame that stands, then stages shading, the bake's view of the tables, lighting and
    // exposure. Returns whether the tables hold a frame the views can render.
    private bool PackPrepared(in FrameContext context, IGpuDeviceContext device, SdfFrame frame) {
        var tables = m_tables!;

        // A frame the tables packed already, as when the frame source filmed nothing this frame, stands as it was.
        if (!ReferenceEquals(
            objA: frame,
            objB: m_packedFrame
        )) {
            using (HostFrameWork.Measure(HostFrameWorkPart.Pack)) { tables.Pack(frame: frame); }
            m_packedFrame = frame;
            LiveVolumes = frame.Volumes.Count;
        } else {
            // The frame stands, but meshes its draws name can have landed since it was packed.
            tables.StageLandedMeshes(frame: frame);
        }
        tables.AdvanceHitBound();

        // The shading selection follows the frame the passes record (the one the tables hold), never a pending one.
        PrepareShade(device: device, frame: m_frame!);
        FollowRunningCode();
        MeshPathBake.ObserveResidency(tables: tables);
        EnsureProbeWork(tables: tables);
        using (HostFrameWork.Measure(HostFrameWorkPart.Lighting)) {
            MeshIrradianceParameters = MeshIrradiance.Stage(frame: m_frame!, tables: tables, tier: IndirectTier, bakePending: MeshPathBake.IrradiancePending);
        }
        // Latch readiness for this whole frame. A worker finishing after preparation cannot make an image whose
        // exposure was held stand for converged lighting. The next preparation publishes and meters that result.
        LightingPending = (m_frame!.ConvergesLighting && MeshIrradiance.Settling);
        // A locked tick adapts once its complete geometry supplies the meter, not over partial mesh arrivals.
        if ((!m_frame.TickLocked || !MeshPathBake.Pending) && !LightingPending) {
            using (HostFrameWork.Measure(HostFrameWorkPart.Exposure)) {
                Exposures.Stage(frame: m_frame, irradiance: MeshIrradiance, seconds: ExposureSeconds(context: in context, frame: m_frame));
            }
        } else {
            Exposures.Hold();
        }
        tables.ViewExposures = Exposures;

        tables.UpdateTablesSignature();
        // The views render once every pipeline they record with is built; the residency is ready then.
        m_pipelineWaiting = tables.FrameWaiting();

        if (m_pipelineWaiting is not null) {
            ResetReady();
            return false;
        }

        _ = Volatile.Read(location: ref m_ready).TrySetResult();

        return true;
    }

    /// <summary>Returns whether a view's latest render stands for this frame as far as the residency's own inputs go: the
    /// cadence gate is on and the view's signature is the one it last rendered at. The images of other instances the
    /// view samples (a screen's source, a sky layer's infinity image) change behind their handles where no signature sees
    /// them; <see cref="SdfWorldPasses.IsUnchanged"/> holds those to their publications. A residency that filmed nothing
    /// this frame keeps its latest frame, which every view rendered already.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <returns><see langword="true"/> when the view need not render.</returns>
    public bool IsUnchanged(in FrameContext context, int view) {
        if (
            !Prepare(context: in context) ||
            MeshPathBake.DrawsPending || (SourceWaiting is not null) ||
            (m_frame is not { } frame) ||
            (view >= frame.Views.Count) ||
            (view >= m_renderedSignatures.Length) ||
            (m_renderedSignatures[view] is not { } rendered) ||
            !frame.EnableCadenceGate
        ) {
            return false;
        }

        return (rendered == m_tables!.ViewSignature(
            frame: frame,
            view: view
        ));
    }
    /// <summary>Stages the frame: prepares it (<see cref="Prepare"/>), then submits its upload and pumps the tables'
    /// background GPU work against it (<see cref="SdfGpuWork.Pump"/>), or drives it while the graph withholds the frame
    /// (<see cref="FrameWithheld"/>, <see cref="SdfGpuWork.Drive"/>), once a frame, whoever asks first. The render graph's
    /// preparation asks for every scheduled render that reads the residency, inside the frame's open batch and before any
    /// node records (<see cref="SdfWorldPasses.PrepareRender"/>), so no node's recording pays for the frame's shared work.
    /// A frame that waits on a pipeline is staged by its preparation instead, which no render may ask for.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <returns><see langword="true"/> when the residency has tables, whose frame is staged; <see langword="false"/> before
    /// they are built.</returns>
    /// <exception cref="ObjectDisposedException">The residency is released.</exception>
    public bool Stage(in FrameContext context) {
        ClaimWake();
        _ = Prepare(context: in context);

        if (m_tables is not { } tables) { return false; }
        SubmitOnce(tables: tables);

        return true;
    }
    // Submits the frame's upload and pumps the tables' background work against it, once a frame: the one place the
    // frame's upload is submitted, which only the frame thread may reach. A recording forked onto the job pool finds it
    // done (SdfWorldPasses.PrepareResidency) and refuses rather than submit from another thread.
    private void SubmitOnce(SdfWorldTables tables) {
        if (!OpenSubmit(tables: tables)) { return; }
        tables.RecordUpload();
        CloseSubmit(tables: tables);
    }
    // Opens the frame's one submission on the frame thread (SdfWorldTables.OpenUpload); false when the frame's upload is
    // already submitted. The upload's recording then runs on whichever thread holds the tables
    // (SdfWorldTables.RecordUpload), and CloseSubmit ends it on the frame thread.
    private bool OpenSubmit(SdfWorldTables tables) {
        if (m_submitted) { return false; }
        if (JobPool.InPartition || PackingAhead) {
            throw new InvalidOperationException(message: $"Residency '{Name}' was asked to stage its frame from a forked recording; only the frame thread's preparation may.");
        }
        m_submitted = true;
        tables.HoldsFrame = HoldsFrame;
        tables.OpenUpload();
        return true;
    }
    private void CloseSubmit(SdfWorldTables tables) {
        tables.CloseUpload();
        ServeSubmitted(tables: tables);
    }
    // Pumps the tables' background work against the upload just published.
    private void ServeSubmitted(SdfWorldTables tables) {
        // A withheld frame presents nothing, so the work it waits on, its meshes or its lighting, runs at the GPU's pace.
        if (FrameWithheld) { tables.GpuWork.Drive(tables: tables); } else { tables.GpuWork.Pump(tables: tables); }
    }

    /// <summary>Opens the frame's staging for a caller that records several residencies' uploads side by side
    /// (<see cref="SdfWorldPasses.RendersScheduled"/>): prepares the frame, then opens its upload on the frame thread.
    /// The caller then runs <see cref="RecordStage"/> on any one thread and <see cref="EndStage"/> on the frame
    /// thread.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <returns><see langword="true"/> when the upload is open and waits to be recorded; <see langword="false"/> when the
    /// residency has no tables, its frame is already staged, or the frame is one whose staging keeps to the frame thread
    /// (a locked tick, a converging capture or a withheld frame), which <see cref="Stage"/> stages.</returns>
    /// <exception cref="ObjectDisposedException">The residency is released.</exception>
    internal bool BeginStage(in FrameContext context) {
        ClaimWake();
        _ = Prepare(context: in context);
        if ((m_tables is not { } tables) || m_submitted || FrameWithheld || HoldsFrame || (m_frame is not { TickLocked: false, ConvergesLighting: false })) { return false; }
        return OpenSubmit(tables: tables);
    }
    /// <summary>Records the upload <see cref="BeginStage"/> opened (<see cref="SdfWorldTables.RecordUpload"/>), touching
    /// only the residency's tables.</summary>
    internal void RecordStage() => m_tables!.RecordUpload();
    /// <summary>Submits the upload <see cref="RecordStage"/> recorded, deferred in the frame's open batch, on the frame
    /// thread; <see cref="EndStage"/> then hands it to the queue and publishes it.</summary>
    internal void QueueStage() => m_tables!.QueueUpload();
    /// <summary>Hands the upload <see cref="QueueStage"/> submitted to the queue, with every upload queued before it,
    /// publishes it and pumps the tables' background work, on the frame thread.</summary>
    internal void EndStage() {
        var tables = m_tables!;

        tables.HandOffUpload();
        tables.PublishUpload();
        ServeSubmitted(tables: tables);
    }
    /// <summary>Returns the tables the frame is staged in (<see cref="Stage"/>), for a pass that records against them,
    /// staging it late (<see cref="StageLate"/>) when nothing has yet.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <returns>The tables, which hold the frame the pass renders.</returns>
    /// <exception cref="InvalidOperationException">The residency has no tables, since a pass records only once its
    /// residency is ready, or it is asked to stage from a forked recording.</exception>
    /// <exception cref="ObjectDisposedException">The residency is released.</exception>
    public SdfWorldTables Staged(in FrameContext context) => (StageLate(context: in context)
        ? m_tables!
        : throw new InvalidOperationException(message: $"Residency '{Name}' has no tables to render: {NotReadyReason}"));
    /// <summary>Stages a frame the render graph's preparation did not reach (a graph that installed during the frame with
    /// a package the schedule did not name), counting it (<see cref="LateStagings"/>); does nothing to a staged frame. A
    /// recorder asks it as it prepares its recording on the frame thread, before the recording forks.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <returns><see langword="true"/> when the frame is staged; <see langword="false"/> before the tables are
    /// built.</returns>
    /// <exception cref="InvalidOperationException">The frame is unstaged and this is a forked recording.</exception>
    /// <exception cref="ObjectDisposedException">The residency is released.</exception>
    public bool StageLate(in FrameContext context) {
        if (m_submitted) { return true; }
        if (!Stage(context: in context)) { return false; }
        LateStagings++;
        return true;
    }

    /// <summary>Gets the frames a recording staged because the frame's preparation had not (<see cref="StageLate"/>); zero
    /// while every recording finds the frame's shared work done before it begins.</summary>
    public long LateStagings { get; private set; }

    /// <summary>Serves the residency's upkeep, whether or not anything reads it this frame: pumps the tables' background GPU
    /// work (<see cref="SdfGpuWork.Pump"/>), which answers the jobs that completed and runs their callbacks, then submits
    /// the next while the budget allows, among them the bake's sampling (the work of a frame the residency withholds,
    /// <see cref="FrameWithheld"/>, is that frame's own, which the budget does not meter); publishes the uploads whose
    /// fences have signaled; and polls retired device objects and answers a memory trim the process account asked for.
    /// Called on the frame thread at the top of each frame, before any residency prepares; a residency with no tables
    /// keeps nothing to serve, beyond giving back the lighting of one parked for <see cref="ForgetLightingSeconds"/>.</summary>
    public void Upkeep() {
        JoinPack();
        if (m_disposed) { return; }
        ForgetParkedLighting();
        if (m_tables is not { } tables) { return; }
        tables.GpuWork.Pump(tables: tables, withheld: FrameWithheld);
        m_work.Poll();
        tables.ServiceMemoryTrim();
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
    /// <param name="latch">The frame's delivery latch.</param>
    /// <param name="leases">The frame's lease list, which retires the lease after the frame's submission.</param>
    /// <returns>The image view, or zero when the screen shows nothing this frame.</returns>
    public nint ScreenImage(int view, int screen, RenderGraphExternalReads? reads, in DeliveryLatch latch, LeaseRetireList leases) {
        ArgumentNullException.ThrowIfNull(argument: leases);

        var handle = (((ScreenRead(reads: reads, screen: screen, view: view) is var index and >= 0) &&
            (reads is not null) &&
            reads.TrySample(imageViewHandle: out var sampled, index: index, latch: in latch, leases: leases))
            ? sampled
            : 0);

        if (((uint)screen) < ((uint)m_boundScreenSources.Length)) { m_boundScreenSources[screen] = handle; }

        return handle;
    }
    /// <summary>Writes into a pass set's screen array the image (<see cref="ScreenImage"/>) of each screen a filter marks,
    /// and the filler at every other screen: a host's image on every call, since its handle is unique only among live
    /// objects, and the filler only where the set does not already hold it.</summary>
    /// <param name="view">The view's index in the residency's frame.</param>
    /// <param name="shown">The slots whose screens' images the pass samples, a bit a slot (<see cref="SdfWorldTables.ResidentScreens"/>).</param>
    /// <param name="tables">The tables whose slots <paramref name="shown"/> names.</param>
    /// <param name="recording">The recording whose reads and lease list the images come from.</param>
    /// <param name="bindings">The writer of the set's descriptors.</param>
    /// <param name="binding">The screen array's binding in <paramref name="set"/>.</param>
    /// <param name="set">The pass set.</param>
    /// <param name="filler">The image view a screen that shows nothing samples.</param>
    /// <param name="bound">The image view the set holds at each screen, which this updates, or empty for a set whose
    /// holdings are not tracked, which is written at every screen.</param>
    /// <returns>The screens bound an image, one bit per screen index.</returns>
    internal UInt128 BindScreenImages(int view, UInt128 shown, SdfWorldTables tables, in RenderGraphPackageRecording recording, IGpuBindings bindings,
        uint binding, nint set, nint filler, Span<nint> bound) {
        Span<nint> images = stackalloc nint[SdfWorldTables.MaxScreenSurfaces];

        var imaged = TakeScreenImages(images: images, recording: in recording, shown: shown, tables: tables, view: view);

        WriteScreenImages(images: images, binding: binding, bindings: bindings, bound: bound, filler: filler, set: set);
        return imaged;
    }
    /// <summary>Takes the image (<see cref="ScreenImage"/>) of each screen a filter marks, holding its lease in the
    /// recording's lease list, and zero at every other screen: the half of <see cref="BindScreenImages"/> that changes the
    /// recording's leases and the residency, which a pass's decision takes in pass order.</summary>
    /// <param name="view">The view's index in the residency's frame.</param>
    /// <param name="shown">The slots whose screens' images the pass samples, a bit a slot.</param>
    /// <param name="tables">The tables whose slots <paramref name="shown"/> names (<see cref="SdfWorldTables.ScreenOfSlot"/>).</param>
    /// <param name="recording">The recording whose reads and lease list the images come from.</param>
    /// <param name="images">The image view each slot samples, or zero; <see cref="SdfWorldTables.MaxScreenSurfaces"/>
    /// long.</param>
    /// <returns>The slots bound an image, a bit a slot.</returns>
    internal UInt128 TakeScreenImages(int view, UInt128 shown, SdfWorldTables tables, in RenderGraphPackageRecording recording, Span<nint> images) {
        var imaged = UInt128.Zero;

        images.Clear();
        foreach (var slot in BitWords.Members(mask: shown)) {
            images[slot] = ScreenImage(latch: recording.Latch, leases: recording.Leases, reads: recording.Reads, screen: tables.ScreenOfSlot(slot: slot), view: view);
            if (images[slot] != 0) { imaged |= BitWords.Bit(index: slot); }
        }
        return imaged;
    }
    /// <summary>Writes into a pass set's screen array the images <see cref="TakeScreenImages"/> took, and the filler at
    /// every screen it took none for: a host's image on every call, since its handle is unique only among live objects, and
    /// the filler only where the set does not already hold it. It changes nothing but the set and
    /// <paramref name="bound"/>, so a pass's emission writes it beside other passes'.</summary>
    /// <param name="images">The image view each screen index samples, or zero.</param>
    /// <param name="bindings">The writer of the set's descriptors.</param>
    /// <param name="binding">The screen array's binding in <paramref name="set"/>.</param>
    /// <param name="set">The pass set.</param>
    /// <param name="filler">The image view a screen that shows nothing samples.</param>
    /// <param name="bound">The image view the set holds at each screen, which this updates, or empty for a set whose
    /// holdings are not tracked, which is written at every screen.</param>
    internal static void WriteScreenImages(ReadOnlySpan<nint> images, IGpuBindings bindings, uint binding, nint set, nint filler, Span<nint> bound) {
        for (var screen = 0; (screen < SdfWorldTables.MaxScreenSurfaces); screen++) {
            var image = images[screen];

            if (image == 0) {
                if (!bound.IsEmpty && (bound[screen] == filler)) { continue; }
                image = filler;
            }
            bindings.WriteSampledImage(arrayElement: ((uint)screen), binding: binding, descriptorSetHandle: set, imageViewHandle: image);
            if (!bound.IsEmpty) { bound[screen] = image; }
        }
    }
    /// <summary>Returns the instance a screen shows in one view (<see cref="ISdfScreenSources.ReadOf"/>), whose image
    /// the screen samples.</summary>
    /// <param name="view">The view's index in the residency's frame.</param>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The instance's name, or <see langword="null"/> for a screen that shows none.</returns>
    public string? ScreenProducerOf(int view, int screen) => m_screenSources?.ReadOf(screen: screen, view: view);
    /// <summary>Returns where the read a screen samples in one view lies among the images a view's render graph hands
    /// its pass: the instance the screen shows in that view (<see cref="ScreenProducerOf"/>). The image, its lease and
    /// its publication are that read's (<see cref="RenderGraphExternalReads"/>).</summary>
    /// <param name="view">The view's index in the residency's frame.</param>
    /// <param name="screen">The program-declared screen index.</param>
    /// <param name="reads">The images the pass's instance reads that its graph binds to no version, or
    /// <see langword="null"/>.</param>
    /// <returns>The read's position in <paramref name="reads"/>, or -1 when the screen samples none of them.</returns>
    public int ScreenRead(int view, int screen, RenderGraphExternalReads? reads) => (
        ((reads is not null) && (m_screenSources?.ReadOf(screen: screen, view: view) is { } producer))
            ? reads.IndexOf(producer: producer)
            : -1
    );

    /// <summary>Gets the detail rows the residency's sky counts its runs and layers in, the composition's one set
    /// (<see cref="SdfWorldPipelineCatalog.SkyDetails"/>).</summary>
    public SdfSkyDetails SkyDetails => m_skyDetails;

    /// <summary>Returns the counts a view of the residency allocates its counted scratch by at an extent: one viewport,
    /// and the provisioned instances and their mask words (<see cref="CapacityRevision"/>).</summary>
    /// <param name="width">The view's width, in pixels.</param>
    /// <param name="height">The view's height, in pixels.</param>
    /// <returns>The counts.</returns>
    public ShaderPipelineStorageCounts CountsAt(uint width, uint height) => CountsFor(height: height, instances: CountedInstances, width: width);
    /// <summary>Returns the counts a view of a residency allocates its counted scratch by: one viewport, and the
    /// instances it is provisioned for and their mask words.</summary>
    /// <param name="width">The view's width, in pixels.</param>
    /// <param name="height">The view's height, in pixels.</param>
    /// <param name="instances">The provisioned instances.</param>
    /// <returns>The counts.</returns>
    public static ShaderPipelineStorageCounts CountsFor(uint width, uint height, int instances) => new(
        Height: height,
        Width: width
    ) {
        InstanceMaskWords = ((ulong)SdfProgram.InstanceMaskStorageWordCountFor(instanceCount: instances)),
        Instances = ((ulong)instances),
        Viewports = 1,
    };

    /// <summary>Gets a revision that moves whenever <see cref="CountsAt"/> would return another value at an unchanged
    /// extent: the provisioned instance count. Before the tables exist, it is the capacity they will receive for that
    /// captured frame.</summary>
    public long CapacityRevision => CountedInstances;

    // The instances a view's counted scratch is sized for: the tables' capacity, or before they are built the one they are
    // built with, the larger of the captured program's instances and the residency's floor.
    private int CountedInstances => (m_tables?.InstanceCapacity ?? Math.Max(
        val1: (m_frame?.Program.Instances.Count ?? 1),
        val2: m_instanceCapacity
    ));

    /// <summary>Releases every device object after the device was lost, without waiting for any submission; the next
    /// frame rebuilds the tables on the recreated device, and every view renders again.</summary>
    public void OnDeviceLost() => ReleaseDeviceObjects(deviceLost: true);

    /// <summary>Gets the bake of the residency's program into the meshes its views draw.</summary>
    public SdfMeshPathBake MeshPathBake { get; }

    private readonly SdfMeshDresser m_meshDresser;

    /// <summary>Gets the residency's indirect lighting, the irradiance volume its views read while its frame asks for
    /// indirect light.</summary>
    public SdfMeshIrradiance MeshIrradiance { get; private set; } = new();
    /// <summary>Gets the exposure each view of the residency's latest frame maps the luminance it shades by.</summary>
    public SdfViewExposures Exposures { get; } = new();

    /// <summary>Gets the irradiance volume the latest prepared frame's views read; disabled with indirect light
    /// off.</summary>
    public SdfMeshIrradianceParameters MeshIrradianceParameters { get; private set; }
    /// <summary>Gets whether this prepared frame still awaits its requested lighting and exposure convergence.
    /// Background GPU work continues while the graph marks this frame's image as waiting.</summary>
    public bool LightingPending { get; private set; }
    /// <summary>Gets whether the prepared frame is a locked tick that waits for its meshes: its bake still owes a mesh, or
    /// a draw waits for one to land. The graph withholds the frame's image until they arrive
    /// (<see cref="SdfWorldPasses.WaitingOf"/>), as it does for <see cref="LightingPending"/>.</summary>
    public bool MeshesPending => ((m_frame is { TickLocked: true }) && (MeshPathBake.Pending || DrawsAwaitMeshes));
    /// <summary>Gets whether the graph withholds the prepared frame's image (<see cref="MeshesPending"/> or
    /// <see cref="LightingPending"/>). Such a frame presents nothing for background work to delay, so its staging drives
    /// that work at the GPU's pace (<see cref="SdfGpuWork.Drive"/>) instead of pumping it under the budgets that protect a
    /// presented frame.</summary>
    public bool FrameWithheld => (MeshesPending || LightingPending);
    /// <summary>Gets why the frame source's latest capture filmed nothing, or a frame that does not yet show what its
    /// content calls for (<see cref="ISdfFrameSource.Waiting"/>), or <see langword="null"/>. While it stands the graph
    /// withholds the residency's views and answers waiting with it (<see cref="SdfWorldPasses.WithheldOf"/>), so a
    /// lockstep host holds its tick; a frame the source films nothing for leaves the frame captured before
    /// standing.</summary>
    public string? SourceWaiting { get; private set; }

    // The frame the tables last packed, which a frame that films nothing leaves standing, and whether a frame captured
    // since the tables last uploaded a program carries another one.
    private SdfFrame? m_packedFrame;
    // A captured frame waiting for its shadow or views kernel. It survives a frame whose film gate captures nothing,
    // while m_frame exposes the packed frame the views render; a newer capture replaces it.
    private SdfFrame? m_pendingFrame;
    private Puck.Abstractions.Presentation.FrameCaptureRequest? m_convergence;
    private SdfFrame? m_frozenFrame;
    // The residency's own frames: a capture is copied into the one the tables do not hold (Capture), and a converging
    // capture's first frame into the frozen one, so the frame the views render is never the source's, which its next
    // capture may write again, and capturing allocates nothing.
    private readonly SdfFrame.Writer[] m_captures = [new(), new()];
    private readonly SdfFrame.Writer m_frozen = new();

    /// <summary>Gets whether the residency holds its frame for a capture (<see cref="BeginConvergence"/>): until the
    /// capture completes, every frame renders the frame the capture's first frame took.</summary>
    public bool HoldsFrame => (m_convergence is { Completion.IsCompleted: false });

    // The presentation time of the frame the exposure last adapted to, for a lockstep frame's own interval.
    private float m_exposureTime = float.NaN;

    // The seconds the views' exposure adapts over this staging (SdfViewExposures.SecondsOf).
    private double ExposureSeconds(in FrameContext context, SdfFrame frame) {
        var previous = m_exposureTime;

        m_exposureTime = frame.Time;

        return SdfViewExposures.SecondsOf(frame: frame, frameSeconds: context.FrameDeltaSeconds, previousTime: previous);
    }

    /// <summary>Freezes the presentation source for a capture.</summary>
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
    // The pipeline the residency waits on while it holds its last packed frame (SdfWorldTables.FrameWaiting).
    private SdfPipelineWait? m_pipelineWaiting;
    // Whether the frame's preparation left the tables holding a frame the views can render.
    private bool m_renders;

    internal object? CapturePackingDomain { get; }
    private readonly record struct CapturePacking(float Delta, float Alpha, bool Rebuilt);
    private CapturePacking m_capturePacking;

    // Opens a capture on the frame thread, including the host's camera fitting callback. Holding a frame runs
    // the ordinary capture path so convergence does not borrow composition state across a fork.
    internal bool BeginCapturePacking(in FrameContext context) {
        if (HoldsFrame) { _ = HostFrame(context: in context); return false; }
        using var work = HostFrameWork.Measure(HostFrameWorkPart.Capture);
        if (!OpenCapture(context: in context)) { return false; }

        m_frozenFrame = null;
        var source = (SdfCompositionFrameSource)m_frameSource;
        var delta = CaptureInterval(context: in context, converging: false);
        var alpha = (float)context.InterpolationAlpha;
        var ready = source.PrepareCapture(deltaSeconds: delta, interpolationAlpha: alpha, rebuilt: out var rebuilt);

        SourceWaiting = source.Waiting;
        m_capturePacking = new CapturePacking(Delta: delta, Alpha: alpha, Rebuilt: rebuilt);
        return ready;
    }

    internal void RunCapturePacking() {
        using var work = HostFrameWork.Measure(HostFrameWorkPart.Capture);
        ((SdfCompositionFrameSource)m_frameSource).PackCapture(rebuilt: m_capturePacking.Rebuilt);
    }

    internal void EndCapturePacking() {
        using var work = HostFrameWork.Measure(HostFrameWorkPart.Capture);
        var job = m_capturePacking;
        var frame = ((SdfCompositionFrameSource)m_frameSource).FinishCapture(width: m_width, height: m_height,
            deltaSeconds: job.Delta, interpolationAlpha: job.Alpha);

        PublishCaptured(frame: CaptureWriter().CopyFrom(source: frame));
    }

    private bool OpenCapture(in FrameContext context) {
        if (m_captured) { return false; }
        m_captured = true;
        if (!(m_film?.Invoke(arg: context) ?? true)) {
            return false;
        }
        // The source's capture may write what the frames these tables hold borrow.
        m_tables?.ForgetViewInputs();
        return true;
    }

    private float CaptureInterval(in FrameContext context, bool converging) {
        // A residency read again after frames nothing read advances by those frames too, so a time-keyed timeline
        // shows the presented time rather than the phase it was last seen at.
        var seconds = (converging ? 0f : ((float)(context.FrameDeltaSeconds + m_skippedSeconds)));

        m_skippedSeconds = 0;
        SdfCaptureWork.ProcessWork.Count(kind: SdfCaptureWork.Run);
        return seconds;
    }

    // Write into the residency's own frame that the tables do not hold.
    private SdfFrame.Writer CaptureWriter() => ReferenceEquals(objA: m_captures[0].Frame, objB: m_packedFrame) ? m_captures[1] : m_captures[0];

    private void Capture(in FrameContext context) {
        using var work = HostFrameWork.Measure(HostFrameWorkPart.Capture);
        if (!OpenCapture(context: in context)) { return; }

        var converging = HoldsFrame;
        var capture = CaptureWriter();
        SdfFrame frame;

        if (converging && (m_frozenFrame is { } frozen)) {
            frame = capture.CopyFrom(content: (frozen.Content with { ProgramChanged = false }), source: frozen);
            SourceWaiting = null;
            m_frameSource.PresentHeldFrame();
        } else {
            if (!converging) {
                m_frozenFrame = null;
            }

            var captured = m_frameSource.CaptureFrame(
                width: m_width,
                height: m_height,
                deltaSeconds: CaptureInterval(context: in context, converging: converging),
                interpolationAlpha: ((float)context.InterpolationAlpha)
            );

            SourceWaiting = m_frameSource.Waiting;
            // A source that films nothing leaves the frame captured last standing, packed or pending as it was.
            if (captured is null) {
                return;
            }
            // The source's frame is borrowed until its next capture, which may write it again: the residency renders its
            // own copy.
            frame = capture.CopyFrom(source: captured);
            if (converging && (SourceWaiting is null)) {
                // Freeze the authored frame, while derived meshes can still finish under the capture's hold. A frame the
                // source says is incomplete is never frozen; a later one is.
                m_frozenFrame = m_frozen.CopyFrom(source: frame);
            }
        }
        PublishCaptured(frame: frame);
    }

    private void PublishCaptured(SdfFrame frame) {
        // Every view draws the program's baked meshes, landed first where its views stand.
        MeshDemand.Observe(frame: frame, height: m_height);
        try { frame = m_meshDresser.Dress(frame: frame); } catch (SdfDressFaultException) {
            // The diagnostic deliberately leaves a partial publication. Retry through the ordinary pending door
            // before any consumer sees it; that door starts over, including an ownerless allocation.
            frame = m_meshDresser.Dress(frame: frame);
        }

        m_frame = frame;
        m_pendingFrame = null;
        m_programPending |= frame.ProgramChanged;

        var glass = (frame.MeshRows?.GlassCount ?? 0);

        if (frame.MeshRows is null) { foreach (var draw in frame.MeshDraws) { if (draw.Glass) { glass++; } } }
        Volatile.Write(location: ref m_meshDrawCount, value: (frame.MeshRows?.LiveCount ?? frame.MeshDraws.Count));
        Volatile.Write(location: ref m_glassDrawCount, value: glass);
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
        // A parked residency builds its tables only for a render or for the sight's want (StandsParked).
        if (StandsParked) {
            return false;
        }

        m_deviceContext = device;
        m_tables = m_pipelines.TryBuild(
            construct: static (pipelines, passes, inputs) => new SdfWorldTables(
                device: inputs.Device,
                options: inputs.Options,
                passes: passes,
                pipelines: pipelines
            ),
            device: device,
            hostsOnDirectX: false,
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

        // The bake samples through the tables' background GPU work, which owns its jobs' slots and the tables' lifetime;
        // the tables exist once its kernels are built, so it starts while the rest compile. The probes join once every
        // pipeline is (EnsureProbeWork).
        tables.Grant.Name = MeshPathBake.Residency;
        MeshIrradiance.Time = tables.Grant.Selector.Time;
        // A residency built for a reader counts as observed from now; a park's wake withholds the views until what the
        // park took is back (Waking).
        m_clock = tables.Grant.Selector.Time;
        m_observedAt = m_clock.GetTimestamp();
        Wake();
        tables.GpuWork.Add(client: new SdfMeshBakeGpuWork(bake: MeshPathBake, tables: tables));
        MeshPathBake.DispatchReport = tables.GpuWork.Describe;
        // A lattice a worker readies is taken when it is ready, not at the next frame.
        MeshPathBake.Offered = tables.GpuWork.Wake;
        // The construction uploaded the frame's program; the frame is packed afresh.
        NoteProgram(program: frame.Program);
        m_packedFrame = null;
        m_programPending = false;
        Array.Clear(array: m_renderedSignatures);

        return true;
    }
    // Adds the irradiance probes' background work to the tables once every kernel of their set is built, the probe
    // kernels among them, whose layouts its construction reads: the tables are built before them (SdfWorldPipelines
    // .PollRequired), and lighting has nothing to light before a frame renders. Tables built again (a device loss) are
    // given work of their own.
    private void EnsureProbeWork(SdfWorldTables tables) {
        if (ReferenceEquals(objA: m_probeTables, objB: tables) || (tables.Pipelines.FirstUnbuilt() is not null)) { return; }

        var probeWork = new SdfMeshProbeGpuWork(tables: tables, volume: MeshIrradiance);

        tables.GpuWork.Add(client: probeWork);
        MeshIrradiance.GpuTrace = probeWork;
        m_probeTables = tables;
    }

    // The tables the probes' background work was last added to (EnsureProbeWork).
    private SdfWorldTables? m_probeTables;

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
            ShadeDefault: frame.ShadeDefault,
            SkyDetails: m_skyDetails,
            WorkLedger: m_work
        );
    // Settles which screens hold a slot this frame, then binds each resident screen's mapping, emission policy and bound
    // flag. A screen is resident while it shows a source (it names an instance some view of the frame reads, whose image
    // each view's pass binds from the images its render graph hands it, ScreenImage) or a glyph grid this frame; past the
    // slots, the nearest to the frame's first camera hold them (SdfWorldTables.SettleScreenSlots), and the rest shade as
    // screens with no source until a nearer one leaves. A screen that is neither shows nothing and holds no slot.
    private void BindScreens(SdfWorldTables tables, SdfFrame frame) {
        m_wantedScreens.Clear();
        m_wantedSeen.Clear();
        m_screenDecalFrames.Clear();
        if (m_screenSources is { } sources) {
            foreach (var screen in sources.Screens) {
                if (ShowsSource(sources: sources, screen: screen, frame: frame) && m_wantedSeen.Add(item: screen)) { m_wantedScreens.Add(item: screen); }
            }
        }
        // A provider is asked once a frame; a dictionary is walked through its own enumerator, which the interface's would
        // box every frame.
        if (m_frameSource.ScreenDecals is Dictionary<int, Func<SdfScreenDecalFrame?>> decals) {
            foreach (var (screen, provider) in decals) { NoteDecal(screen: screen, decal: provider()); }
        } else if (m_frameSource.ScreenDecals is { } screenDecals) {
            foreach (var (screen, provider) in screenDecals) { NoteDecal(screen: screen, decal: provider()); }
        }
        if (m_wantedScreens.Count > SdfWorldTables.MaxScreenSurfaces) { OrderByNearness(frame: frame); }
        foreach (var screen in m_wantedScreens) {
            if (screen >= m_boundScreenSources.Length) {
                Array.Resize(array: ref m_boundScreenSources, newSize: (int)Math.Min(val1: SdfProgramBuilder.MaxScreenIdentities, val2: (2L * (screen + 1))));
            }
        }
        tables.SettleScreenSlots(wanted: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_wantedScreens));
        if (m_screenSources is not { } bound) {
            return;
        }
        foreach (var slot in BitWords.Members(mask: tables.ResidentScreens)) {
            var screen = tables.ScreenOfSlot(slot: slot);

            tables.SetScreenBound(
                bound: ShowsSource(sources: bound, screen: screen, frame: frame),
                screenIndex: screen
            );
            tables.SetScreenMapping(
                face: bound.FaceOf(screen: screen),
                mapping: bound.MappingOf(screen: screen),
                screenIndex: screen,
                treatment: bound.TreatmentOf(screen: screen)
            );
            tables.SetScreenLuminance(
                luminance: bound.LuminanceOf(screen: screen),
                screenIndex: screen
            );
            tables.SetScreenRate(
                code: ((DebugViewModes.ShowsRates(mode: m_debugMode) && (screen < frame.ScreenRateCodes.Length)) ? ((SdfUpdateRateCode)frame.ScreenRateCodes.Span[screen]) : SdfUpdateRateCode.None),
                screenIndex: screen
            );
            tables.SetScreenRipple(
                ripple: (bound.TryRippleOf(
                    ripple: out var ripple,
                    screen: screen
                )
                    ? ripple
                    : null
                ),
                screenIndex: screen
            );
        }
    }
    // Whether a screen shows a source this frame: some view of the frame reads the instance it names.
    private static bool ShowsSource(ISdfScreenSources sources, int screen, SdfFrame frame) {
        for (var view = 0; (view < frame.Views.Count); view++) {
            if (sources.ReadOf(screen: screen, view: view) is not null) { return true; }
        }
        return false;
    }
    // Holds a screen's glyph grid for this frame, which makes the screen resident while it has one.
    private void NoteDecal(int screen, SdfScreenDecalFrame? decal) {
        m_screenDecalFrames.Add(item: (screen, decal));
        if ((decal is not null) && m_wantedSeen.Add(item: screen)) { m_wantedScreens.Add(item: screen); }
    }
    // Orders the wanted screens nearest first from the frame's first camera, the identity breaking ties, so the slots go
    // to the screens a viewer is most likely to see. A screen whose frame neither a surface transform nor the program
    // declares orders last.
    private void OrderByNearness(SdfFrame frame) {
        var eye = ((frame.Views.Count > 0) ? frame.Views[0].Camera.Position : Vector3.Zero);
        var count = m_wantedScreens.Count;

        if (m_wantedOrder.Length < count) { Array.Resize(array: ref m_wantedOrder, newSize: count); }
        for (var index = 0; (index < count); index++) {
            var screen = m_wantedScreens[index];
            var origin = ((m_screenSurfaceTransforms.TryGetValue(key: screen, value: out var provider) && (provider() is { } transform))
                ? transform.Origin
                : (frame.Program.Appearance.TryGetScreenSurface(screenIndex: screen, surface: out var surface) ? surface.Origin : new Vector3(value: float.PositiveInfinity)));

            m_wantedOrder[index] = (Vector3.DistanceSquared(value1: eye, value2: origin), screen);
        }
        m_wantedOrder.AsSpan(start: 0, length: count).Sort();
        for (var index = 0; (index < count); index++) {
            m_wantedScreens[index] = m_wantedOrder[index].Screen;
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
    private void UploadProgram(SdfWorldTables tables, SdfProgram program, SdfProgramDelta? delta) {
        tables.UploadProgram(delta: delta, program: program);
        NoteProgram(program: program);
    }
    private void NoteProgram(SdfProgram program) {
        LiveProgramWords = program.WordCount;
        LiveProgramInstances = program.Instances.Count;
        LiveProgramStepScale = program.StepScale;
        LiveProgramStepScaleBinder = program.StepScaleBinder;
        LiveProgramFieldScopeClamps = program.FieldScopeClamps;
        LiveProgramSegments = program.SkipSegmentCount;
        LiveProgramRigidSegments = program.RigidSegmentCount;
    }
}
