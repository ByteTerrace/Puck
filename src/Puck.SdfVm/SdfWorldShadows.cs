using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// A world's one shadow solution, which every view of its residency reads and none draws: the cascade set's entries
/// (<see cref="SdfMeshShadowCascades"/>) kept as layers the residency's upload draws, ahead of every view's submission, so a
/// world shown on three screens and a painting pays for its shadows once.
/// <para>Each entry is anchored to the world (eye-centred on the residency's first view and snapped to a cell of an eighth
/// of its reach) and is cached: its layers are drawn whole only when the light turns past the entry's tolerance, the eye
/// crosses into another cell, the entry's texels change or a static draw changes
/// (<see cref="SdfWorldTables.StaticDrawRevision"/>), a redraw spread over several frames into a back layer where the
/// authored rate asks. Between redraws the near, far and secondary entries follow what moves by rectangles: every draw
/// record a staging changes reports the box it held and the box it holds (<see cref="Moved"/>), each entry projects those
/// boxes into its texels and merges them into at most <see cref="MaxRectangles"/> rectangles, and the upload clears and
/// draws every caster inside each rectangle again through a viewport of its own. A still world draws nothing; a walking
/// player costs the rectangles its body's shadow sweeps. The distant entry holds the static scene alone.</para>
/// <para>Invariant: a layer is read only through the camera it was drawn from. One door enforces it: <see cref="Decide"/>
/// chooses every entry's work for the upload about to be submitted and the cameras readers read after it,
/// <see cref="Record"/> draws exactly that work into the upload's command buffer, which the residency submits ahead of every
/// view, and <see cref="Submitted"/> commits it; a camera no submitted upload drew is never read.</para>
/// <para>Every image rests in the shader-readable layout between uploads; the upload moves each one it writes to the depth
/// attachment layout and back, so a view's submission samples them without a transition of its own.</para>
/// </summary>
internal sealed class SdfWorldShadows : IDisposable {
    /// <summary>The most rectangles one entry redraws in one upload; more moved boxes merge into these.</summary>
    public const int MaxRectangles = 4;
    // The texels a moved box's footprint is widened by on each side: rasterization covers a pixel whose centre a triangle
    // holds, and the bake's coarser levels stand a little proud of the box.
    private const int RectanglePad = 2;
    // The most moved boxes one upload keeps; past it every entry that follows motion redraws whole.
    private const int MaxMoved = 8192;
    // The drawings an entry may record: its whole layer (a redraw or a share of one), its glass extinction, and one per
    // rectangle.
    private const int WholeKind = 0;
    private const int OpticalKind = 1;
    private const int FirstRectangleKind = 2;
    private const int Kinds = (FirstRectangleKind + MaxRectangles);
    // The buffers a mesh set binds and those the selection kernel's sets add (SdfWorldPassRecorder lays them alike).
    private const int MeshSetBuffers = 7;
    private const int MeshSetCounters = 2;
    private const int MeshSetVisibility = 3;
    private const int MeshSetGlassSums = 4;
    private const int MeshSetOcclusionBuffers = 6;
    private const int OcclusionSets = 2;
    // The phase the selection emits glass extinction draws into (SdfKernelInterfaces.MeshShadowPhases).
    private const int GlassPhase = 4;

    private static readonly string[] EntryNames = ["near", "far", "second", "distant"];

    private readonly SdfWorldTables m_tables;
    private readonly GpuDeviceServices m_gpu;
    private readonly Entry[] m_entries;
    private readonly nint m_pool;
    private readonly IGpuBuffer m_counters;
    private readonly IGpuRenderPass m_loadPass;
    private readonly byte[] m_block = new byte[SdfFrameBlock.SizeBytes];
    // The boxes the stagings since the last decision moved, and whether a change no box bounds moved anything.
    private readonly List<SdfMeshBox> m_moved = [];
    private bool m_movedAll;
    private bool m_disposed;

    // The decision for the upload about to be submitted (Decide): the frame it was made for, whether it stands, and
    // whether the upload that records it was submitted this frame, after which no reader of the frame may see another.
    private SdfFrame? m_decidedFrame;
    private bool m_decided;
    private bool m_frozen;
    private ulong m_uploads;
    private SdfMeshShadowCascades m_readers;
    private long m_contentRevision;
    private long m_bindingRevision;

    /// <summary>Initializes a new instance of the <see cref="SdfWorldShadows"/> class: its descriptor pool, its sets and
    /// pass blocks for every entry, drawing and upload ring slot, and each entry's selections; its layers are allocated when
    /// an entry is first drawn.</summary>
    /// <param name="tables">The tables whose upload draws the layers, whose device creates them and whose pipelines draw.</param>
    /// <param name="gpu">The tables' counted device services.</param>
    internal SdfWorldShadows(SdfWorldTables tables, GpuDeviceServices gpu) {
        m_tables = tables;
        m_gpu = gpu;

        var ring = SdfWorldTables.FrameRingSize;
        var sets = (SdfMeshShadowCascades.MaxEntries * Kinds * ring);
        var sizes = default(GpuDescriptorPoolSizes);

        for (var set = 0; (set < sets); set++) {
            // A mesh set and its two selection sets, and a glass extinction set beside each.
            sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.Mesh.Groups);
            sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.Mesh.Groups);
            for (var phase = 0; (phase < OcclusionSets); phase++) {
                sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.MeshOcclusion.Groups);
            }
        }

        using var scope = new GpuCreationScope();

        m_pool = scope.Own(handle: gpu.Bindings.CreatePool(name: Name(part: "descriptors"), sizes: sizes), release: gpu.Bindings.DestroyPool);
        // The work counters the shadow pipelines bind; every block disables counting (GpuKernelCounters.DisabledRow), so
        // they are never written.
        m_counters = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(name: Name(part: "counters"), sizeBytes: 256, usage: GpuBufferUsage.Storage));
        m_loadPass = scope.Own(created: gpu.RenderPassFactory.Create(
            description: SdfMeshRasterPass.ShadowRenderPass with { Depth = SdfWorldPackage.MeshShadowDepthAttachment with { Load = GpuAttachmentLoad.Load } },
            name: Name(part: "retained depth")));
        m_entries = new Entry[SdfMeshShadowCascades.MaxEntries];
        for (var index = 0; (index < m_entries.Length); index++) {
            m_entries[index] = scope.Own(created: new Entry(index: index, owner: this, ring: ring));
        }
        scope.Complete();
    }

    /// <summary>Gets the revision of what the layers hold: one more for every upload that draws a layer whole, completes a
    /// redraw or redraws a rectangle, so a pass that samples them folds it into its signature and renders again exactly when
    /// what it would read changes.</summary>
    public long ContentRevision => m_contentRevision;
    /// <summary>Gets the revision of the images and buffers the layers are held in: one more for every allocation, so a
    /// view's pass set writes them again before it binds them.</summary>
    public long BindingRevision => m_bindingRevision;

    /// <summary>Returns the cascade set a view of the frame reads: the world's set, each entry through the camera its layers
    /// were drawn from, or none for a view that skips shadows.</summary>
    /// <param name="frame">The frame the tables hold.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <returns>The set.</returns>
    public SdfMeshShadowCascades CascadesOf(SdfFrame frame, int view) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        if ((frame.Views.Count == 0) || frame.Views[Math.Clamp(value: view, min: 0, max: (frame.Views.Count - 1))].Quality.DisableSoftShadows) {
            return default;
        }

        Decide(frame: frame);

        return m_readers;
    }
    /// <summary>Returns the image view a view's pass samples at one of the layers' bindings, or <paramref name="filler"/>
    /// for a layer that is not allocated.</summary>
    /// <param name="entry">The entry, from zero below <see cref="SdfMeshShadowCascades.MaxEntries"/>.</param>
    /// <param name="exit">Whether the binding is the entry's exit layer rather than its depth.</param>
    /// <param name="filler">The view a binding with no layer samples.</param>
    /// <returns>The image view.</returns>
    public nint ViewOf(int entry, bool exit, nint filler) => (m_entries[entry].Read(exit: exit)?.Image.ImageViewHandle ?? filler);
    /// <summary>Returns the glass extinction buffer an entry holds, or <see langword="null"/> for none.</summary>
    /// <param name="entry">The entry, from zero below <see cref="SdfMeshShadowCascades.MaxEntries"/>.</param>
    /// <returns>The buffer, or <see langword="null"/>.</returns>
    public IGpuBuffer? OpticalOf(int entry) => m_entries[entry].Optical;

    /// <summary>Notes a box a staging moved: a draw record as it stood before a change, or as it stands after, each a
    /// caster the entries that follow motion must draw again where it was and where it is.</summary>
    /// <param name="box">The box, or <see langword="null"/> for a change no box bounds, which redraws those entries whole.</param>
    internal void Moved(in SdfMeshBox? box) {
        if (box is not { } bounded) {
            m_movedAll = true;
        } else if (m_moved.Count < MaxMoved) {
            m_moved.Add(item: bounded);
        } else {
            m_movedAll = true;
        }
    }
    /// <summary>Decides, once for the upload about to be submitted, what every entry draws and through which camera
    /// receivers read it after that upload. Every reader of the frame and the upload's recording share the one decision.</summary>
    /// <param name="frame">The frame the tables hold.</param>
    internal void Decide(SdfFrame frame) {
        if (m_frozen || (m_decided && ReferenceEquals(objA: m_decidedFrame, objB: frame))) {
            return;
        }

        m_decided = true;
        m_decidedFrame = frame;

        var fit = SdfMeshShadowCascades.Fit(frame: frame, view: 0);
        var readers = fit;
        var changes = false;

        foreach (var entry in m_entries) {
            changes |= entry.Decide(fit: in fit, frame: frame, upload: m_uploads, moved: m_moved, movedAll: m_movedAll);
            readers = (fit.Holds(index: entry.Index) ? readers.WithCamera(index: entry.Index, camera: entry.ReaderCamera) : readers);
        }
        // Each entry holds what it took of the moved boxes until it draws it.
        m_moved.Clear();
        m_movedAll = false;
        m_readers = readers;
        if (changes) { m_contentRevision++; }
    }
    /// <summary>Records the decided work into the upload's command buffer, every image it writes left shader-readable for
    /// the views' submissions that follow.</summary>
    /// <param name="frame">The frame the tables hold.</param>
    /// <param name="commandBuffer">The upload's command buffer.</param>
    /// <param name="slot">The upload's ring slot.</param>
    internal void Record(SdfFrame frame, nint commandBuffer, int slot) {
        // One upload a frame: a freeze an earlier upload left without a frame start between ends here.
        if (m_frozen) {
            m_frozen = false;
            m_decided = false;
        }
        Decide(frame: frame);
        foreach (var entry in m_entries) {
            entry.Record(commandBuffer: commandBuffer, frame: frame, slot: slot);
        }
    }
    /// <summary>Commits the decided work once the upload that records it is submitted: the cameras and revisions each
    /// entry drew, the rectangles it drew, and the next share of a redraw in progress.</summary>
    /// <param name="slot">The upload's ring slot.</param>
    internal void Submitted(int slot) {
        foreach (var entry in m_entries) {
            entry.Submitted(slot: slot);
        }
        m_uploads++;
        m_frozen = true;
    }
    /// <summary>Starts a frame of the residency: the next reader decides the work of the next upload.</summary>
    internal void BeginFrame() {
        m_frozen = false;
        m_decided = false;
    }
    /// <summary>Lets the selections read what an earlier upload into a ring slot read back, once the upload before the
    /// one about to reuse the slot is known complete.</summary>
    /// <param name="slot">The ring slot about to be reused.</param>
    internal void Completed(int slot) {
        foreach (var entry in m_entries) {
            entry.Completed(slot: slot);
        }
    }
    /// <summary>Describes each entry for <c>world.residency</c>: its camera's half extent, texels, whether its layers stand,
    /// how many uploads it held, its redraw in progress and the rectangles it owes.</summary>
    /// <returns>The description.</returns>
    public string Describe() => string.Join(separator: ' ', values: m_entries.Select(selector: static entry => entry.Describe()));

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        foreach (var entry in m_entries) { entry?.Dispose(); }
        m_loadPass.Dispose();
        m_counters.Dispose();
        m_gpu.Bindings.DestroyPool(poolHandle: m_pool);
    }

    private GpuObjectName Name(string part, string? detail = null, int index = 0) => new(detail: detail, index: index, owner: $"{m_tables.DebugLabel} shadows", part: part);
    // The image, framebuffer or buffer a reallocation replaced, retired behind every submission that may still read it.
    private void Retire(IDisposable? replaced) {
        if (replaced is null) { return; }
        m_tables.RetireShadowObject(replaced: replaced);
    }

    // One image an entry holds, with the layout the upload last left it in.
    private sealed class Layer(IGpuImage image) : IDisposable {
        public IGpuImage Image { get; } = image;
        public GpuImageLayout Layout { get; set; } = GpuImageLayout.Undefined;
        public void Dispose() => Image.Dispose();
    }

    // A rectangle of an entry's texels, X0 and Y0 inclusive, X1 and Y1 exclusive, rows counted down from the top.
    private readonly record struct TexelRect(int X0, int Y0, int X1, int Y1) {
        public long Area => ((long)(X1 - X0) * (Y1 - Y0));
        public bool IsEmpty => ((X1 <= X0) || (Y1 <= Y0));
        public TexelRect Union(in TexelRect other) => new(X0: Math.Min(X0, other.X0), Y0: Math.Min(Y0, other.Y0), X1: Math.Max(X1, other.X1), Y1: Math.Max(Y1, other.Y1));
        public bool Touches(in TexelRect other) => ((X0 <= other.X1) && (other.X0 <= X1) && (Y0 <= other.Y1) && (other.Y0 <= Y1));
    }

    // What an entry draws for one upload.
    private struct Work {
        // The share of a redraw drawn, -1 for none; the shares the redraw takes; whether this share completes it.
        public int Share;
        public int Slices;
        public bool Completes;
        public SdfMeshShadowCamera Camera;
        public Vector3 Cell;
        public long Revision;
        // The rectangles drawn again this upload, from the entry's owed rectangles, and whether they are the whole layer.
        public int Rectangles;
        // Whether the glass extinction is drawn again over the whole layer.
        public bool Optical;
        // The rate counters the frame counts into, which the upload's submission counts the work into.
        public WorkCounterSet? Counts;
    }

    // A redraw of an entry's layers in progress: the camera, cell and static revision it started from, its shares and
    // the share it draws next.
    private readonly record struct Redraw(SdfMeshShadowCamera Camera, Vector3 Cell, long Revision, int Slices, int Next, int Texels);

    // One entry of the cascade set: its layers, its selections and their sets per ring slot, and what it holds.
    private sealed class Entry : IDisposable {
        private readonly SdfWorldShadows m_owner;
        private readonly SdfMeshOcclusion[] m_selections;
        private readonly IGpuStorageBuffer[][] m_blocks;
        private readonly nint[][] m_meshSets;
        private readonly IGpuBuffer?[][][] m_meshSetBuffers;
        private readonly IGpuBuffer?[][][] m_meshSetOcclusion;
        private readonly nint[][] m_meshSetTargets;
        private readonly nint[][] m_occlusionSets;
        private readonly IGpuBuffer?[][] m_occlusionSetBuffers;
        private readonly nint[] m_occlusionSetTargets;
        private readonly nint[] m_glassSets;
        private readonly IGpuBuffer?[][] m_glassSetBuffers;
        private readonly IGpuBuffer?[][] m_glassSetOcclusion;
        private readonly nint[] m_glassSetTargets;
        private readonly SdfShadowEntryCounts m_kinds;
        // The rectangles this entry owes, in the texels of the camera receivers read, and whether it owes the whole layer.
        private readonly TexelRect[] m_owed = new TexelRect[MaxRectangles];
        private int m_owedCount;
        private bool m_owedAll;

        private Layer? m_depth;
        private Layer? m_exit;
        private Layer? m_backDepth;
        private Layer? m_backExit;
        private IGpuFramebuffer? m_depthClear;
        private IGpuFramebuffer? m_exitClear;
        private IGpuFramebuffer? m_backClear;
        private IGpuFramebuffer? m_backExitClear;
        private IGpuFramebuffer? m_backLoad;
        private IGpuFramebuffer? m_backExitLoad;
        private bool m_opticalInitialized;
        private int m_texels;
        private bool m_sliced;

        // What the last submitted upload committed: the camera, cell, revision and texels the layers were drawn at and
        // whether they stand.
        private SdfMeshShadowCamera m_camera;
        private Vector3 m_cell;
        private long m_revision = -1;
        private bool m_stands;
        private Redraw? m_redraw;
        private int m_heldUploads;
        // This upload's work, decided and then recorded; whether the recording drew it, which Submitted commits.
        private Work m_work;
        private bool m_recorded;
        private bool m_holds;

        public Entry(int index, SdfWorldShadows owner, int ring) {
            Index = index;
            m_owner = owner;
            FollowsMotion = (index != SdfMeshShadowCascades.DistantEntry);
            m_kinds = SdfUpdateRateCounts.ShadowEntries[index];

            var gpu = owner.m_gpu;
            var tables = owner.m_tables;
            var part = $"shadow-{EntryNames[index]}";
            var named = $"{tables.DebugLabel} shadows";

            m_selections = new SdfMeshOcclusion[Kinds];
            for (var kind = 0; (kind < Kinds); kind++) {
                m_selections[kind] = new SdfMeshOcclusion(buffers: gpu.BufferFactory, owner: named, slots: ring, cascade: true, optical: (kind == OpticalKind),
                    part: kind switch { WholeKind => part, OpticalKind => $"{part} glass", _ => $"{part} rect{kind - FirstRectangleKind}" });
            }
            m_blocks = new IGpuStorageBuffer[Kinds][];
            m_meshSets = new nint[Kinds][];
            m_meshSetBuffers = new IGpuBuffer?[Kinds][][];
            m_meshSetOcclusion = new IGpuBuffer?[Kinds][][];
            m_meshSetTargets = new nint[Kinds][];
            m_occlusionSets = new nint[Kinds * ring * OcclusionSets][];
            m_occlusionSetBuffers = new IGpuBuffer?[Kinds * ring * OcclusionSets][];
            m_occlusionSetTargets = new nint[Kinds * ring * OcclusionSets];
            m_glassSets = new nint[ring];
            m_glassSetBuffers = new IGpuBuffer?[ring][];
            m_glassSetOcclusion = new IGpuBuffer?[ring][];
            m_glassSetTargets = new nint[ring];

            var meshLayout = tables.MeshPipeline.GroupLayoutHandles[((int)ShaderInterfaceGroup.Pass)];
            var occlusionLayout = tables.OcclusionPipeline.GroupLayoutHandles[((int)ShaderInterfaceGroup.Pass)];
            var blockBytes = (ulong)IGpuBindings.ConstantBufferBytes(blockBytes: (uint)SdfFrameBlock.SizeBytes);

            for (var kind = 0; (kind < Kinds); kind++) {
                m_blocks[kind] = new IGpuStorageBuffer[ring];
                m_meshSets[kind] = new nint[ring];
                m_meshSetBuffers[kind] = new IGpuBuffer?[ring][];
                m_meshSetOcclusion[kind] = new IGpuBuffer?[ring][];
                m_meshSetTargets[kind] = new nint[ring];
                for (var slot = 0; (slot < ring); slot++) {
                    var block = gpu.BufferFactory.CreateHostVisible(name: owner.Name(part: part, detail: $"pass block {kind}", index: slot), sizeBytes: blockBytes, usage: GpuBufferUsage.Uniform);

                    m_blocks[kind][slot] = block;
                    m_meshSets[kind][slot] = gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: meshLayout, name: owner.Name(part: part, detail: $"mesh group {kind}", index: slot), poolHandle: owner.m_pool);
                    gpu.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: block.BufferHandle, bufferSize: block.SizeBytes, descriptorSetHandle: m_meshSets[kind][slot]);
                    m_meshSetBuffers[kind][slot] = new IGpuBuffer?[MeshSetBuffers];
                    m_meshSetOcclusion[kind][slot] = new IGpuBuffer?[MeshSetOcclusionBuffers];
                    for (var phase = 0; (phase < OcclusionSets); phase++) {
                        var at = OcclusionSetOf(kind: kind, slot: slot, phase: phase);

                        m_occlusionSets[at] = [gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: occlusionLayout, name: owner.Name(part: part, detail: $"occlusion group {kind}.{phase}", index: slot), poolHandle: owner.m_pool)];
                        gpu.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: block.BufferHandle, bufferSize: block.SizeBytes, descriptorSetHandle: m_occlusionSets[at][0]);
                        m_occlusionSetBuffers[at] = new IGpuBuffer?[MeshSetBuffers + MeshSetOcclusionBuffers];
                    }
                    if (kind == OpticalKind) {
                        m_glassSets[slot] = gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: meshLayout, name: owner.Name(part: part, detail: "glass group", index: slot), poolHandle: owner.m_pool);
                        gpu.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: block.BufferHandle, bufferSize: block.SizeBytes, descriptorSetHandle: m_glassSets[slot]);
                        m_glassSetBuffers[slot] = new IGpuBuffer?[MeshSetBuffers];
                        m_glassSetOcclusion[slot] = new IGpuBuffer?[MeshSetOcclusionBuffers];
                    }
                }
            }
        }

        public int Index { get; }
        // Whether the entry draws every caster and follows what moves by rectangles (the near, far and secondary entries),
        // rather than holding the static scene alone (the distant map).
        public bool FollowsMotion { get; }
        public IGpuBuffer? Optical { get; private set; }
        // The camera receivers read through after this upload: the one this upload completes a redraw from, else the
        // committed one, else none.
        public SdfMeshShadowCamera ReaderCamera => (((m_work.Share >= 0) && m_work.Completes) ? m_work.Camera : (m_stands ? m_camera : default));

        // The layer receivers sample.
        public Layer? Read(bool exit) => (exit ? m_exit : m_depth);

        private static int OcclusionSetOf(int kind, int slot, int phase) => ((((kind * SdfWorldTables.FrameRingSize) + slot) * OcclusionSets) + phase);

        // Chooses this upload's work; returns whether it changes what receivers read.
        public bool Decide(in SdfMeshShadowCascades fit, SdfFrame frame, ulong upload, List<SdfMeshBox> moved, bool movedAll) {
            m_work = new Work { Share = -1, Counts = frame.RateCounts };
            m_recorded = false;
            m_holds = fit.Holds(index: Index);
            if (!m_holds) {
                return false;
            }

            var tables = m_owner.m_tables;
            var fresh = fit[Index];
            var texels = fit.TexelsOf(index: Index);
            var reach = SdfMeshShadowCascades.ReachOf(frame: frame, index: Index);
            var cell = SdfMeshShadowCascades.CellOf(eye: frame.Views[0].Camera.Position, reach: reach);
            var revision = tables.StaticDrawRevision;
            var rates = frame.UpdateRates;
            var holds = ((rates & SdfUpdateRates.ShadowDistant) != 0);
            var slices = (holds ? SlicesOf(frame: frame, rates: rates) : 1);
            var tolerance = TurnRadiansOf(frame: frame, texels: texels);
            var reallocated = Allocate(frame: frame, texels: texels, sliced: (slices > 1));

            if ((m_redraw is { } running) && ((running.Texels != texels) || (running.Slices != slices) || reallocated)) {
                m_redraw = null;
            }
            if (m_redraw is null) {
                var held = (
                    holds &&
                    m_stands &&
                    !reallocated &&
                    (m_camera.HalfHeight == fresh.HalfHeight) &&
                    (m_camera.Far == fresh.Far) &&
                    (m_cell == cell) &&
                    // The chord between unit directions is their angle to well under a part in a million at these turns.
                    (Vector3.Distance(value1: m_camera.Forward, value2: fresh.Forward) <= tolerance) &&
                    (m_revision == revision)
                );

                m_redraw = (held ? null : new Redraw(Camera: fresh, Cell: cell, Revision: revision, Slices: slices, Next: 0, Texels: texels));
            }
            if (m_redraw is { } drawing) {
                m_work.Share = drawing.Next;
                m_work.Slices = drawing.Slices;
                m_work.Completes = (drawing.Next == (drawing.Slices - 1));
                m_work.Camera = drawing.Camera;
                m_work.Cell = drawing.Cell;
                m_work.Revision = drawing.Revision;
            }

            var completes = ((m_work.Share >= 0) && m_work.Completes);

            if (FollowsMotion) {
                var camera = ReaderCamera;

                // A layer drawn anew answers what moved before it; what moved this upload is owed in its camera.
                if (completes) {
                    m_owedCount = 0;
                    m_owedAll = false;
                }
                if (camera.HalfHeight > 0f) {
                    if (movedAll) { m_owedAll = true; }
                    foreach (var box in moved) {
                        Owe(box: in box, camera: in camera);
                    }
                }
                // The far entry draws its moving casters at its authored rate, owing what moved between.
                var paced = (((rates & SdfUpdateRates.ShadowFar) == 0) || (Index != SdfMeshShadowCascades.FarEntry) || ((upload % (ulong)FarPeriodOf(frame: frame)) == 0UL));

                if ((camera.HalfHeight > 0f) && paced) {
                    if (m_owedAll) {
                        m_owed[0] = new TexelRect(X0: 0, Y0: 0, X1: m_texels, Y1: m_texels);
                        m_work.Rectangles = 1;
                    } else {
                        m_work.Rectangles = m_owedCount;
                    }
                }
                m_work.Optical = (completes && (Optical is not null));
            }

            return (completes || (m_work.Rectangles > 0) || reallocated);
        }
        // Projects a moved box into the camera's texels and merges it into the owed rectangles.
        private void Owe(in SdfMeshBox box, in SdfMeshShadowCamera camera) {
            if (m_owedAll || !box.MayCast(cascade: in camera)) { return; }

            var relative = (box.Center - camera.Position);
            var scale = ((0.5f * m_texels) / camera.HalfHeight);
            var u = (((Vector3.Dot(relative, camera.Right) / camera.HalfHeight) * 0.5f) + 0.5f) * m_texels;
            var v = (0.5f - ((Vector3.Dot(relative, camera.Up) / camera.HalfHeight) * 0.5f)) * m_texels;
            var halfU = (((MathF.Abs(Vector3.Dot(box.HalfX, camera.Right)) + MathF.Abs(Vector3.Dot(box.HalfY, camera.Right))) + MathF.Abs(Vector3.Dot(box.HalfZ, camera.Right))) * scale);
            var halfV = (((MathF.Abs(Vector3.Dot(box.HalfX, camera.Up)) + MathF.Abs(Vector3.Dot(box.HalfY, camera.Up))) + MathF.Abs(Vector3.Dot(box.HalfZ, camera.Up))) * scale);
            var rect = new TexelRect(
                X0: Math.Clamp(value: ((int)MathF.Floor(u - halfU) - RectanglePad), min: 0, max: m_texels),
                Y0: Math.Clamp(value: ((int)MathF.Floor(v - halfV) - RectanglePad), min: 0, max: m_texels),
                X1: Math.Clamp(value: ((int)MathF.Ceiling(u + halfU) + RectanglePad), min: 0, max: m_texels),
                Y1: Math.Clamp(value: ((int)MathF.Ceiling(v + halfV) + RectanglePad), min: 0, max: m_texels)
            );

            if (rect.IsEmpty) { return; }
            // Merges into a rectangle it touches; otherwise takes a new one, and past the most merges the pair whose union
            // grows least.
            for (var at = 0; (at < m_owedCount); at++) {
                if (m_owed[at].Touches(other: in rect)) {
                    m_owed[at] = m_owed[at].Union(other: in rect);
                    return;
                }
            }
            if (m_owedCount < MaxRectangles) {
                m_owed[m_owedCount++] = rect;
                return;
            }

            var best = 0;
            var growth = long.MaxValue;

            for (var at = 0; (at < m_owedCount); at++) {
                var union = m_owed[at].Union(other: in rect);
                var grown = (union.Area - m_owed[at].Area);

                if (grown < growth) { (best, growth) = (at, grown); }
            }
            m_owed[best] = m_owed[best].Union(other: in rect);
        }
        // The shares one redraw takes: the distant map's authored frames, and the far entry's refresh period (the authored
        // far rate, farShadows.hertz, spreads a redraw over the frames of one of its periods); one otherwise.
        private int SlicesOf(SdfFrame frame, SdfUpdateRates rates) => Index switch {
            SdfMeshShadowCascades.DistantEntry => Math.Min(val1: frame.DistantShadows.Slices, val2: SdfMeshOcclusion.MaxSlices),
            SdfMeshShadowCascades.FarEntry when ((rates & SdfUpdateRates.ShadowFar) != 0) => FarPeriodOf(frame: frame),
            _ => 1,
        };
        // The frames between the far entry's refreshes of its moving casters at the authored rate.
        private static int FarPeriodOf(SdfFrame frame) =>
            Math.Clamp(value: (int)MathF.Round(x: (SdfUpdateRateTiers.EveryFrameHertz / (float)Math.Max(val1: frame.FarShadows.Hertz, val2: 1))), min: 1, max: SdfMeshOcclusion.MaxSlices);
        // The light turn held layers tolerate: the distant map's and the far entry's authored tolerance, and otherwise the
        // turn that moves the shadow of a caster as tall as the entry's reach by one of its texels.
        private float TurnRadiansOf(SdfFrame frame, int texels) {
            var authored = Index switch {
                SdfMeshShadowCascades.DistantEntry => frame.DistantShadows.TurnRadians,
                SdfMeshShadowCascades.FarEntry => frame.FarShadows.TurnRadians,
                _ => 0f,
            };

            return ((authored > 0f) ? authored : (2f / Math.Max(val1: texels, val2: 1)));
        }
        // Allocates the layers an entry needs at its texels; returns whether anything was allocated anew, which no
        // standing layer survives.
        private bool Allocate(SdfFrame frame, int texels, bool sliced) {
            var optical = (FollowsMotion && frame.Program.HasGlassShadows);
            var changed = ((m_texels != texels) || (m_depth is null));

            if (changed) {
                ReleaseLayers();
                m_texels = texels;
                m_depth = Create(texels: texels, detail: "depth");
                m_exit = Create(texels: texels, detail: "exit");
                m_depthClear = Framebuffer(layer: m_depth, load: false);
                m_exitClear = Framebuffer(layer: m_exit, load: false);
                m_stands = false;
                m_sliced = false;
                m_opticalInitialized = false;
                m_owedCount = 0;
                m_owedAll = false;
            }
            if (sliced && !m_sliced) {
                m_backDepth = Create(texels: texels, detail: "back");
                m_backExit = Create(texels: texels, detail: "back exit");
                m_backClear = Framebuffer(layer: m_backDepth, load: false);
                m_backExitClear = Framebuffer(layer: m_backExit, load: false);
                m_backLoad = Framebuffer(layer: m_backDepth, load: true);
                m_backExitLoad = Framebuffer(layer: m_backExit, load: true);
                m_sliced = true;
                changed = true;
            }
            if (optical != (Optical is not null)) {
                m_owner.Retire(replaced: Optical);
                Optical = (optical
                    ? m_owner.m_gpu.BufferFactory.CreateDeviceLocal(
                        name: m_owner.Name(part: $"shadow-{EntryNames[Index]}", detail: "optical"),
                        sizeBytes: checked((((ulong)texels * (ulong)texels * SdfWorldPackage.ShadowOpticalTexelWords) + SdfWorldPackage.ShadowOpticalBitmapWords(side: (uint)texels)) * sizeof(uint)),
                        usage: GpuBufferUsage.Storage)
                    : null);
                m_opticalInitialized = false;
                // The extinction is drawn with a whole layer, so a buffer allocated now needs one.
                m_stands = false;
                changed = true;
            }
            if (changed) { m_owner.m_bindingRevision++; }

            return changed;
        }
        private Layer Create(int texels, string detail) => new(image: m_owner.m_gpu.ImageFactory.CreateDepth(
            attachment: SdfWorldPackage.MeshShadowDepthAttachment,
            height: (uint)texels,
            name: m_owner.Name(part: $"shadow-{EntryNames[Index]}", detail: detail),
            sampled: true,
            width: (uint)texels));
        private IGpuFramebuffer Framebuffer(Layer layer, bool load) => m_owner.m_gpu.RenderPassFactory.CreateFramebuffer(
            colors: [],
            depth: layer.Image,
            renderPass: (load ? m_owner.m_loadPass : m_owner.m_tables.ShadowRenderPass));
        private void ReleaseLayers() {
            foreach (var framebuffer in (ReadOnlySpan<IGpuFramebuffer?>)[m_depthClear, m_exitClear, m_exitLoad, m_backClear, m_backExitClear, m_backLoad, m_backExitLoad]) {
                m_owner.Retire(replaced: framebuffer);
            }
            foreach (var layer in (ReadOnlySpan<Layer?>)[m_depth, m_exit, m_backDepth, m_backExit]) {
                m_owner.Retire(replaced: layer);
            }
            (m_depthClear, m_exitClear, m_exitLoad, m_backClear, m_backExitClear, m_backLoad, m_backExitLoad) = (null, null, null, null, null, null, null);
            (m_depth, m_exit, m_backDepth, m_backExit) = (null, null, null, null);
        }

        public void Record(nint commandBuffer, SdfFrame frame, int slot) {
            if (!m_holds) { return; }
            m_recorded = true;
            // A layer allocated this upload holds nothing: it is cleared, so a view binding it before it is drawn samples
            // the far plane, and left shader-readable.
            foreach (var (layer, framebuffer) in (ReadOnlySpan<(Layer?, IGpuFramebuffer?)>)[(m_depth, m_depthClear), (m_exit, m_exitClear), (m_backDepth, m_backClear), (m_backExit, m_backExitClear)]) {
                if ((layer is { Layout: GpuImageLayout.Undefined }) && (framebuffer is not null)) {
                    Clear(commandBuffer: commandBuffer, layer: layer, framebuffer: framebuffer);
                }
            }

            var whole = new GpuPixelRect(Height: (uint)m_texels, Width: (uint)m_texels, X: 0, Y: 0);
            var motion = (FollowsMotion ? SdfMeshMotion.Any : SdfMeshMotion.Static);

            if (m_work.Share >= 0) {
                var sliced = (m_work.Slices > 1);
                var depth = (sliced ? m_backDepth! : m_depth!);
                var exit = (sliced ? m_backExit! : m_exit!);
                var load = (sliced && (m_work.Share > 0));

                Draw(commandBuffer: commandBuffer, frame: frame, slot: slot, kind: WholeKind, camera: in m_work.Camera, motion: motion,
                    share: m_work.Share, slices: m_work.Slices, depth: depth, exit: exit, area: whole,
                    depthFramebuffer: (sliced ? (load ? m_backLoad! : m_backClear!) : m_depthClear!),
                    exitFramebuffer: (sliced ? (load ? m_backExitLoad! : m_backExitClear!) : m_exitClear!));
                // A layer drawn now may miss a caster absent from the records, whose landing must then move the static
                // revision.
                m_owner.m_tables.CastersRead();
                if (sliced && m_work.Completes) {
                    Copy(commandBuffer: commandBuffer, source: m_backDepth!, destination: m_depth!);
                    Copy(commandBuffer: commandBuffer, source: m_backExit!, destination: m_exit!);
                }
                Rest(commandBuffer: commandBuffer, layer: depth);
                Rest(commandBuffer: commandBuffer, layer: exit);
            }

            var camera = ReaderCamera;

            for (var at = 0; (at < m_work.Rectangles); at++) {
                var rect = m_owed[at];
                var area = new GpuPixelRect(Height: (uint)(rect.Y1 - rect.Y0), Width: (uint)(rect.X1 - rect.X0), X: rect.X0, Y: rect.Y0);

                Draw(commandBuffer: commandBuffer, frame: frame, slot: slot, kind: (FirstRectangleKind + at), camera: in camera, motion: motion,
                    share: 0, slices: 1, depth: m_depth!, exit: m_exit!, area: area, depthFramebuffer: m_depthClear!, exitFramebuffer: m_exitClear!);
            }
            if (m_work.Optical) {
                Extinction(commandBuffer: commandBuffer, frame: frame, slot: slot, camera: in camera, area: whole, optical: Optical!);
            }
            Rest(commandBuffer: commandBuffer, layer: m_depth!);
            Rest(commandBuffer: commandBuffer, layer: m_exit!);
        }
        // Selects a drawing's draws on the GPU and draws them into an entry's two layers within an area of its texels: the
        // entry depth (back-face culled where the draw encloses a solid), then the exit layer. The light camera is the
        // entry's; an area less than the whole layer draws through a viewport of its own, from the camera of that
        // rectangle of the plane, so its texels land where the whole camera puts them, and keeps only the casters in that
        // rectangle's box. The render passes clear the area first.
        private void Draw(nint commandBuffer, SdfFrame frame, int slot, int kind, in SdfMeshShadowCamera camera, SdfMeshMotion motion, int share, int slices,
            Layer depth, Layer exit, GpuPixelRect area, IGpuFramebuffer depthFramebuffer, IGpuFramebuffer exitFramebuffer) {
            var recorder = m_owner.m_gpu.Recorder;
            var selection = m_selections[kind];
            var count = m_owner.m_tables.MeshDrawCount;
            var set = m_meshSets[kind][slot];
            var (drawn, aspect, cull) = Through(camera: in camera, area: area);

            Stage(frame: frame, slot: slot, kind: kind, blockCamera: in drawn, aspect: aspect, detailCamera: in camera, cull: in cull, motion: motion, share: share, slices: slices, optical: null);
            Move(commandBuffer: commandBuffer, layer: depth, to: GpuImageLayout.DepthAttachment);
            Move(commandBuffer: commandBuffer, layer: exit, to: GpuImageLayout.DepthAttachment);
            if (count > 0) {
                selection.RecordEarly(commandBuffer: commandBuffer, pipeline: m_owner.m_tables.OcclusionPipeline, recorder: recorder, set: m_occlusionSets[OcclusionSetOf(kind: kind, slot: slot, phase: 0)][0]);
            }
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: depthFramebuffer);
            if (count > 0) {
                Phase(commandBuffer: commandBuffer, phase: 0, pipeline: m_owner.m_tables.ShadowPipeline, selection: selection, set: set);
                Phase(commandBuffer: commandBuffer, phase: SdfKernelInterfaces.MeshShadowCulledEntryPhase, pipeline: m_owner.m_tables.ShadowEntryPipeline, selection: selection, set: set);
            }
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
            // The exit layer: front-face culled solids, then sheets and clipped draws unculled.
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: exitFramebuffer);
            if (count > 0) {
                Phase(commandBuffer: commandBuffer, phase: 2, pipeline: m_owner.m_tables.ShadowExitPipeline, selection: selection, set: set);
                Phase(commandBuffer: commandBuffer, phase: 3, pipeline: m_owner.m_tables.ShadowPipeline, selection: selection, set: set);
            }
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
        }
        // The camera an area of the entry's plane draws through, its aspect, and the cull that keeps the casters its box
        // may hold: the entry's own camera for the whole layer.
        private (SdfMeshShadowCamera Camera, float Aspect, SdfMeshCull Cull) Through(in SdfMeshShadowCamera camera, GpuPixelRect area) {
            var cullsNothing = (m_owner.m_decidedFrame?.MeshCull == SdfMeshCullLevel.Off);

            if ((area.Width == (uint)m_texels) && (area.Height == (uint)m_texels)) {
                return (camera, 1f, (cullsNothing ? SdfMeshCull.None : SdfMeshCull.OfLightBox(cascade: in camera)));
            }

            var texel = ((2f * camera.HalfHeight) / m_texels);
            var centreU = ((area.X + (0.5f * area.Width)) - (0.5f * m_texels));
            var centreV = ((0.5f * m_texels) - (area.Y + (0.5f * area.Height)));
            var drawn = camera with {
                HalfHeight = (0.5f * area.Height * texel),
                Position = ((camera.Position + (camera.Right * (centreU * texel))) + (camera.Up * (centreV * texel))),
            };
            // The cull's box is square: the drawn camera's, widened to the area's longer side.
            var square = drawn with { HalfHeight = (0.5f * Math.Max(area.Width, area.Height) * texel) };

            return (drawn, (((float)area.Width) / area.Height), (cullsNothing ? SdfMeshCull.None : SdfMeshCull.OfLightBox(cascade: in square)));
        }
        // Writes a drawing's pass block and its mesh and selection sets, and stages its selection.
        private void Stage(SdfFrame frame, int slot, int kind, in SdfMeshShadowCamera blockCamera, float aspect, in SdfMeshShadowCamera detailCamera, in SdfMeshCull cull,
            SdfMeshMotion motion, int share, int slices, IGpuBuffer? optical) {
            var tables = m_owner.m_tables;
            var selection = m_selections[kind];
            var count = tables.MeshDrawCount;
            var set = m_meshSets[kind][slot];
            var block = m_owner.m_block.AsSpan();
            var detail = ((frame.MeshDetail == SdfMeshDetailLevel.Auto) ? SdfMeshDetail.Of(cascade: in detailCamera, texels: m_texels) : SdfMeshDetail.Forced(level: (((int)frame.MeshDetail) - 1)));

            block.Clear();
            SdfFrameBlock.Write(block: block, frame: frame, height: (uint)m_texels, lightMask: tables.LightMaskOf(frame: frame, view: 0), tables: tables.PassValues, view: 0, width: (uint)m_texels);
            SdfFrameBlock.WriteShadowCamera(block: block, camera: in blockCamera, texels: m_texels, aspect: aspect);
            SdfFrameBlock.WriteWorkCounterRow(block: block, row: GpuKernelCounters.DisabledRow);
            SdfFrameBlock.WriteWorkCounterDetailRow(block: block, row: GpuKernelCounters.DisabledRow);
            m_blocks[kind][slot].Write<byte>(data: block);
            if (count == 0) { return; }

            var bound = m_meshSetBuffers[kind][slot];

            tables.WriteMeshTables(bound: bound, set: set, slot: tables.CurrentSlot);
            tables.WriteMeshWorkCounters(bound: ref bound[MeshSetCounters], counters: m_owner.m_counters, set: set);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetVisibility], buffer: tables.DummyBuffer, member: SdfWorldPackage.VisibilityRecords, set: set);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetGlassSums], buffer: tables.DummyBuffer, member: SdfWorldPackage.GlassSumsWritten, set: set);
            SdfMeshOcclusion.Unbind(bound: m_meshSetOcclusion[kind][slot], boundTarget: ref m_meshSetTargets[kind][slot], first: 0, set: set, tables: tables);
            selection.Stage(tables: tables, count: count, width: (uint)m_texels, height: (uint)m_texels, slot: slot, cull: in cull, detail: in detail, held: tables.HoldsFrame,
                glass: false, occlude: false, splitExit: true, motion: motion, slice: share, slices: slices);
            selection.BindDraws(tables: tables, set: set, bound: m_meshSetOcclusion[kind][slot]);
            for (var phase = 0; (phase < OcclusionSets); phase++) {
                var at = OcclusionSetOf(kind: kind, slot: slot, phase: phase);
                var occlusionSet = m_occlusionSets[at][0];
                var occlusionBound = m_occlusionSetBuffers[at];

                tables.WriteMeshTables(bound: occlusionBound, set: occlusionSet, slot: tables.CurrentSlot);
                tables.WriteMeshWorkCounters(bound: ref occlusionBound[MeshSetCounters], counters: m_owner.m_counters, set: occlusionSet);
                tables.WriteMeshBuffer(bound: ref occlusionBound[MeshSetVisibility], buffer: tables.DummyBuffer, member: SdfWorldPackage.VisibilityRecords, set: occlusionSet);
                tables.WriteMeshBuffer(bound: ref occlusionBound[MeshSetGlassSums], buffer: (((phase == 0) ? optical : null) ?? tables.DummyBuffer), member: SdfWorldPackage.GlassSumsWritten, set: occlusionSet);
                selection.Bind(bound: occlusionBound, boundTarget: ref m_occlusionSetTargets[at], first: MeshSetBuffers, set: occlusionSet, tables: tables, target: tables.SampledFiller.ImageViewHandle);
            }
        }
        // Clears an entry's glass extinction and draws every glass draw's into it against the final depth, which the glass
        // fragment samples (its own attachment is the exit layer, whose test the glass fragment's zero depth always fails,
        // so only its side effects land).
        private void Extinction(nint commandBuffer, SdfFrame frame, int slot, in SdfMeshShadowCamera camera, GpuPixelRect area, IGpuBuffer optical) {
            var tables = m_owner.m_tables;
            var recorder = m_owner.m_gpu.Recorder;
            var selection = m_selections[OpticalKind];
            var glassSet = m_glassSets[slot];
            var bound = m_glassSetBuffers[slot];
            var full = !m_opticalInitialized;

            if (tables.MeshDrawCount == 0) { return; }
            Stage(frame: frame, slot: slot, kind: OpticalKind, blockCamera: in camera, aspect: 1f, detailCamera: in camera,
                cull: ((frame.MeshCull == SdfMeshCullLevel.Off) ? SdfMeshCull.None : SdfMeshCull.OfLightBox(cascade: in camera)), motion: SdfMeshMotion.Any, share: 0, slices: 1, optical: optical);
            selection.RecordEarly(commandBuffer: commandBuffer, pipeline: tables.OcclusionPipeline, recorder: recorder, set: m_occlusionSets[OcclusionSetOf(kind: OpticalKind, slot: slot, phase: 0)][0]);
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: (GpuAccess.ShaderRead | GpuAccess.ShaderWrite), sourceStageMask: (GpuStage.ComputeShader | GpuStage.FragmentShader),
                destinationAccessMask: (full ? GpuAccess.TransferWrite : (GpuAccess.ShaderRead | GpuAccess.ShaderWrite)), destinationStageMask: (full ? GpuStage.Transfer : GpuStage.ComputeShader));
            if (full) {
                recorder.ClearStorageBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle, sizeBytes: optical.SizeBytes);
                m_opticalInitialized = true;
            } else {
                selection.RecordOpticalClear(recorder: recorder, commandBuffer: commandBuffer, pipeline: tables.OcclusionPipeline,
                    set: m_occlusionSets[OcclusionSetOf(kind: OpticalKind, slot: slot, phase: 0)][0], side: (uint)m_texels);
            }
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: (full ? GpuAccess.TransferWrite : GpuAccess.ShaderWrite), sourceStageMask: (full ? GpuStage.Transfer : GpuStage.ComputeShader),
                destinationAccessMask: (GpuAccess.ShaderRead | GpuAccess.ShaderWrite), destinationStageMask: (GpuStage.FragmentShader | GpuStage.ComputeShader));

            tables.WriteMeshTables(bound: bound, set: glassSet, slot: tables.CurrentSlot);
            tables.WriteMeshWorkCounters(bound: ref bound[MeshSetCounters], counters: m_owner.m_counters, set: glassSet);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetVisibility], buffer: tables.DummyBuffer, member: SdfWorldPackage.VisibilityRecords, set: glassSet);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetGlassSums], buffer: optical, member: SdfWorldPackage.GlassSumsWritten, set: glassSet);
            SdfMeshOcclusion.Unbind(bound: m_glassSetOcclusion[slot], boundTarget: ref m_glassSetTargets[slot], first: 0, set: glassSet, tables: tables);
            tables.WriteMeshTarget(bound: ref m_glassSetTargets[slot], set: glassSet, view: m_depth!.Image.ImageViewHandle);
            selection.BindDraws(tables: tables, set: glassSet, bound: m_glassSetOcclusion[slot]);
            Move(commandBuffer: commandBuffer, layer: m_depth!, to: GpuImageLayout.ShaderReadOnly);
            Move(commandBuffer: commandBuffer, layer: m_exit!, to: GpuImageLayout.DepthAttachment);
            // The exit layer's framebuffer that loads it: the pass clears nothing.
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: ExitLoad());
            Phase(commandBuffer: commandBuffer, phase: GlassPhase, pipeline: tables.ShadowGlassPipeline, selection: selection, set: glassSet);
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.FragmentShader,
                destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: (GpuStage.FragmentShader | GpuStage.ComputeShader));
        }
        // The framebuffer that loads the exit layer, made once and kept with the layers.
        private IGpuFramebuffer ExitLoad() => (m_exitLoad ??= Framebuffer(layer: m_exit!, load: true));
        private IGpuFramebuffer? m_exitLoad;
        private void Phase(nint commandBuffer, int phase, IGpuPipeline pipeline, SdfMeshOcclusion selection, nint set) {
            var recorder = m_owner.m_gpu.Recorder;

            recorder.BindPipeline(bindPoint: GpuBindPoint.Graphics, commandBufferHandle: commandBuffer, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Graphics, commandBufferHandle: commandBuffer, descriptorSetHandle: set,
                group: ((uint)ShaderInterfaceGroup.Pass), pipelineLayoutHandle: pipeline.LayoutHandle);
            selection.RecordDraws(commandBuffer: commandBuffer, phase: phase, pipeline: pipeline, recorder: recorder);
        }
        // Clears a layer through a framebuffer whose render pass clears its depth to the far plane, and leaves it
        // shader-readable.
        private void Clear(nint commandBuffer, Layer layer, IGpuFramebuffer framebuffer) {
            var recorder = m_owner.m_gpu.Recorder;

            Move(commandBuffer: commandBuffer, layer: layer, to: GpuImageLayout.DepthAttachment);
            recorder.BeginRenderPass(area: new GpuPixelRect(Height: layer.Image.Height, Width: layer.Image.Width, X: 0, Y: 0), commandBufferHandle: commandBuffer, framebuffer: framebuffer);
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
            Rest(commandBuffer: commandBuffer, layer: layer);
        }
        // Copies one whole layer into another of its extent.
        private void Copy(nint commandBuffer, Layer source, Layer destination) {
            Move(commandBuffer: commandBuffer, layer: source, to: GpuImageLayout.TransferSource);
            Move(commandBuffer: commandBuffer, layer: destination, to: GpuImageLayout.TransferDestination);
            m_owner.m_gpu.Recorder.CopyImage(commandBufferHandle: commandBuffer, sourceImageHandle: source.Image.ImageHandle, destinationImageHandle: destination.Image.ImageHandle,
                width: source.Image.Width, height: source.Image.Height);
        }
        // Leaves a layer shader-readable, the layout every view's submission samples it in.
        private void Rest(nint commandBuffer, Layer layer) => Move(commandBuffer: commandBuffer, layer: layer, to: GpuImageLayout.ShaderReadOnly);
        private void Move(nint commandBuffer, Layer layer, GpuImageLayout to) {
            if (layer.Layout == to) { return; }
            var (sourceAccess, sourceStage) = Use(layout: layer.Layout);
            var (destinationAccess, destinationStage) = Use(layout: to);

            m_owner.m_gpu.Recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, imageHandle: layer.Image.ImageHandle, oldLayout: layer.Layout, newLayout: to,
                sourceAccessMask: sourceAccess, sourceStageMask: sourceStage, destinationAccessMask: destinationAccess, destinationStageMask: destinationStage);
            layer.Layout = to;
        }
        // The accesses and stages a layout is used by here: a depth attachment by the raster's depth tests, a shader-readable
        // image by every shader stage that samples it (the glass fragment here, the views' compute passes after).
        private static (GpuAccess Access, GpuStage Stage) Use(GpuImageLayout layout) => layout switch {
            GpuImageLayout.DepthAttachment => ((GpuAccess.DepthAttachmentRead | GpuAccess.DepthAttachmentWrite), GpuStage.FragmentTests),
            GpuImageLayout.ShaderReadOnly => (GpuAccess.ShaderRead, (GpuStage.FragmentShader | GpuStage.ComputeShader)),
            GpuImageLayout.TransferSource => (GpuAccess.TransferRead, GpuStage.Transfer),
            GpuImageLayout.TransferDestination => (GpuAccess.TransferWrite, GpuStage.Transfer),
            _ => (GpuAccess.None, GpuStage.TopOfPipe),
        };

        public void Submitted(int slot) {
            foreach (var selection in m_selections) { selection.Submitted(slot: slot); }
            if (!m_holds || !m_recorded) { return; }

            var counts = m_work.Counts;

            if (m_work.Share >= 0) {
                counts?.Count(kind: (m_work.Completes ? m_kinds.Static : m_kinds.Share));
                if (m_redraw is { } redraw) {
                    m_redraw = (m_work.Completes ? null : (redraw with { Next = (redraw.Next + 1) }));
                }
                if (m_work.Completes) {
                    m_camera = m_work.Camera;
                    m_cell = m_work.Cell;
                    m_revision = m_work.Revision;
                    m_stands = true;
                    m_heldUploads = 0;
                }
            }
            if (m_work.Rectangles > 0) {
                counts?.Count(kind: m_kinds.Live);
                m_owedCount = 0;
                m_owedAll = false;
            }
            if ((m_work.Share < 0) && (m_work.Rectangles == 0)) {
                counts?.Count(kind: m_kinds.Held);
                m_heldUploads++;
            }
            m_recorded = false;
        }
        public void Completed(int slot) {
            foreach (var selection in m_selections) { selection.Completed(slot: slot); }
        }
        public string Describe() {
            var owed = 0L;

            for (var at = 0; (at < m_owedCount); at++) { owed += m_owed[at].Area; }
            return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"shadow.{EntryNames[Index]}=half{m_camera.HalfHeight:0.#}m/texels{m_texels}/stands{(m_stands ? 1 : 0)}/held{m_heldUploads}/redraw{(m_redraw is { } r ? $"{r.Next}of{r.Slices}" : "-")}/owed{(m_owedAll ? "all" : $"{m_owedCount}:{owed}texels")}{((Optical is not null) ? "/optical" : string.Empty)}");
        }

        public void Dispose() {
            ReleaseLayers();
            m_owner.Retire(replaced: Optical);
            Optical = null;
            foreach (var selection in m_selections) { selection.Dispose(); }
            foreach (var blocks in m_blocks) {
                foreach (var block in blocks) { block?.Dispose(); }
            }
        }
    }
}
