using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// A world's one shadow solution, which every view of its residency reads and none draws: the cascade set's entries
/// (<see cref="SdfMeshShadowCascades"/>) kept as layers the residency's upload draws once a frame, ahead of every view's
/// submission, so a world shown on three screens and a painting pays for its shadows once.
/// <para>Each entry is anchored to the world (eye-centred on the residency's first view and snapped to a cell of an eighth
/// of its reach), and holds a static layer: the static scene's casters alone (<see cref="SdfMeshRecord.DynamicFlag"/>
/// clear), drawn again only when the light turns past the entry's tolerance, the eye crosses into another cell, the entry's
/// texels change or a static draw changes (<see cref="SdfWorldTables.StaticDrawRevision"/>). A redraw may be spread over
/// several frames into a back layer, one share of the casters a frame, which the last share copies into the static layer.
/// The near, far and secondary entries also hold a live layer, which receivers read: a copy of the static layer with the
/// moving casters (and the glass extinction) drawn over it, drawn again only when a moving caster inside the entry's light
/// box moved (its motion scope, <see cref="SdfMotionScope"/>), the static layer was drawn again, or the frame's levers ask
/// for it. A still world so draws nothing at all, and a walking player costs the near entry's moving casters a frame.</para>
/// <para>Invariant: a layer is read only through the camera it was drawn from, and a live layer copies only the static
/// layer drawn from that same camera. One door enforces it: <see cref="Decide"/> chooses every entry's work for the
/// upload about to be submitted and the cameras readers read after it, and <see cref="Record"/> draws exactly that work
/// into the upload's command buffer, which the residency submits ahead of every view. The decision commits only once the
/// upload is submitted (<see cref="Submitted"/>), so a camera no submitted upload drew is never read.</para>
/// <para>Every image rests in the shader-readable layout between uploads; the upload moves each one it writes to the
/// depth attachment layout and back, so a view's submission samples them without a transition of its own.</para>
/// </summary>
internal sealed class SdfWorldShadows : IDisposable {
    // The kinds of drawing an entry does: its static layer (a whole redraw or a share of one) and its live layer.
    private const int StaticKind = 0;
    private const int LiveKind = 1;
    private const int Kinds = 2;
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
    /// pass blocks for every entry, kind and upload ring slot, and each entry's two selections; its layers are allocated
    /// when an entry is first drawn.</summary>
    /// <param name="tables">The tables whose upload draws the layers, whose device creates them and whose pipelines draw.</param>
    /// <param name="gpu">The tables' counted device services.</param>
    internal SdfWorldShadows(SdfWorldTables tables, GpuDeviceServices gpu) {
        m_tables = tables;
        m_gpu = gpu;

        var ring = SdfWorldTables.FrameRingSize;
        var sets = (SdfMeshShadowCascades.MaxEntries * Kinds * ring);
        var sizes = default(GpuDescriptorPoolSizes);

        for (var set = 0; (set < sets); set++) {
            // A mesh set and its two selection sets, and the glass extinction's set beside each live one.
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

    /// <summary>Gets the revision of what the layers hold: one more for every upload that draws a static layer to
    /// completion or a live layer, so a pass that samples them folds it into its signature and renders again exactly when
    /// what it would read changes.</summary>
    public long ContentRevision => m_contentRevision;
    /// <summary>Gets the revision of the images and buffers the layers are held in: one more for every allocation, so a
    /// view's pass set writes them again before it binds them.</summary>
    public long BindingRevision => m_bindingRevision;

    /// <summary>Returns the cascade set a view of the frame reads: the world's set, each entry through the camera its held
    /// layers were drawn from, or none for a view that skips shadows.</summary>
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

    /// <summary>Decides, once for the upload about to be submitted, what every entry draws and through which camera
    /// receivers read it after that upload. Every reader of the frame and the upload's recording share the one decision.</summary>
    /// <param name="frame">The frame the tables hold.</param>
    internal void Decide(SdfFrame frame) {
        if (m_frozen || (m_decided && ReferenceEquals(objA: m_decidedFrame, objB: frame))) {
            return;
        }

        var upload = m_uploads;

        m_decided = true;
        m_decidedFrame = frame;

        var fit = SdfMeshShadowCascades.Fit(frame: frame, view: 0);
        var readers = fit;
        var changes = false;

        foreach (var entry in m_entries) {
            changes |= entry.Decide(fit: in fit, frame: frame, upload: upload);
            readers = (fit.Holds(index: entry.Index) ? readers.WithCamera(index: entry.Index, camera: entry.ReaderCamera) : readers);
        }
        m_readers = readers;
        if (changes) { m_contentRevision++; }
    }
    /// <summary>Records the decided work into the upload's command buffer: each entry's static share or redraw and its
    /// live layer, every image it writes left shader-readable for the views' submissions that follow.</summary>
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
    /// entry drew, and the next share of a redraw in progress.</summary>
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
    /// <summary>Describes each entry for <c>world.residency</c>: its camera's half extent, texels, how many uploads ago its
    /// static layer was drawn, whether a redraw is in progress and its live layer's state.</summary>
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
    // The image or buffer a reallocation replaced, retired behind every submission that may still read it.
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

    // What an entry draws for one upload.
    private struct Work {
        // The share of a static redraw drawn, -1 for none; the shares the redraw takes; whether this share completes it.
        public int Share;
        public int Slices;
        public bool Completes;
        public SdfMeshShadowCamera StaticCamera;
        public Vector3 Cell;
        public long Revision;
        // Whether the live layer is drawn, and the motion revision it is drawn at.
        public bool Live;
        public long Motion;
        public bool Optical;
        // The rate counters the frame counts into, which the upload's submission counts the work into.
        public WorkCounterSet? Counts;
    }

    // A redraw of the static layer in progress: the camera, cell and static revision it started from, its shares and the
    // share it draws next.
    private readonly record struct Redraw(SdfMeshShadowCamera Camera, Vector3 Cell, long Revision, int Slices, int Next, int Texels);

    // One entry of the cascade set: its layers, its two selections and their sets per ring slot, and what it holds.
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
        private readonly SdfMotionScope m_scope = new(reader: SdfMotionReader.Caster);
        private readonly SdfShadowEntryCounts m_kinds;

        private Layer? m_staticDepth;
        private Layer? m_staticExit;
        private Layer? m_backDepth;
        private Layer? m_backExit;
        private Layer? m_liveDepth;
        private Layer? m_liveExit;
        private IGpuFramebuffer? m_staticClear;
        private IGpuFramebuffer? m_staticExitClear;
        private IGpuFramebuffer? m_backClear;
        private IGpuFramebuffer? m_backExitClear;
        private IGpuFramebuffer? m_backLoad;
        private IGpuFramebuffer? m_backExitLoad;
        private IGpuFramebuffer? m_liveClear;
        private IGpuFramebuffer? m_liveExitClear;
        private IGpuFramebuffer? m_liveLoad;
        private IGpuFramebuffer? m_liveExitLoad;
        private bool m_opticalInitialized;
        private int m_texels;
        private bool m_sliced;

        // What the last submitted upload committed: the camera, cell, revision and texels the static layer was drawn at
        // and whether it stands; the motion revision the live layer was drawn at and whether it stands.
        private SdfMeshShadowCamera m_camera;
        private Vector3 m_cell;
        private long m_revision = -1;
        private bool m_stands;
        private long m_liveMotion = -1;
        private bool m_liveStands;
        private Redraw? m_redraw;
        private int m_heldUploads;
        // This upload's work, decided and then recorded; whether the recording drew it, which Submitted commits.
        private Work m_work;
        private bool m_recorded;
        private bool m_holds;

        public Entry(int index, SdfWorldShadows owner, int ring) {
            Index = index;
            m_owner = owner;
            Live = (index != SdfMeshShadowCascades.DistantEntry);
            m_kinds = SdfUpdateRateCounts.ShadowEntries[index];

            var gpu = owner.m_gpu;
            var tables = owner.m_tables;
            var part = $"shadow-{EntryNames[index]}";

            m_selections = [
                new SdfMeshOcclusion(buffers: gpu.BufferFactory, owner: $"{tables.DebugLabel} shadows", part: $"{part} static", slots: ring, cascade: true),
                new SdfMeshOcclusion(buffers: gpu.BufferFactory, owner: $"{tables.DebugLabel} shadows", part: part, slots: ring, cascade: true, optical: Live),
            ];
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
                    if (kind == LiveKind) {
                        m_glassSets[slot] = gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: meshLayout, name: owner.Name(part: part, detail: "glass group", index: slot), poolHandle: owner.m_pool);
                        gpu.Bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: block.BufferHandle, bufferSize: block.SizeBytes, descriptorSetHandle: m_glassSets[slot]);
                        m_glassSetBuffers[slot] = new IGpuBuffer?[MeshSetBuffers];
                        m_glassSetOcclusion[slot] = new IGpuBuffer?[MeshSetOcclusionBuffers];
                    }
                }
            }
        }

        public int Index { get; }
        // Whether the entry draws the moving casters over its static layer into a live layer receivers read.
        public bool Live { get; }
        public IGpuBuffer? Optical { get; private set; }
        // The camera receivers read through after this upload: the one this upload completes a static layer from, else the
        // committed one, else none.
        public SdfMeshShadowCamera ReaderCamera => (((m_work.Share >= 0) && m_work.Completes) ? m_work.StaticCamera : (m_stands ? m_camera : default));

        // The layer receivers sample: the live layer for an entry that keeps one, the static layer otherwise.
        public Layer? Read(bool exit) => (Live ? (exit ? m_liveExit : m_liveDepth) : (exit ? m_staticExit : m_staticDepth));

        private static int OcclusionSetOf(int kind, int slot, int phase) => ((((kind * SdfWorldTables.FrameRingSize) + slot) * OcclusionSets) + phase);

        // Chooses this upload's work; returns whether it changes what receivers read.
        public bool Decide(in SdfMeshShadowCascades fit, SdfFrame frame, ulong upload) {
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
            var holdsLayers = ((rates & SdfUpdateRates.ShadowDistant) != 0);
            var slices = (holdsLayers ? SlicesOf(frame: frame, rates: rates) : 1);
            var tolerance = TurnRadiansOf(frame: frame, texels: texels);
            var reallocated = Allocate(frame: frame, texels: texels, sliced: (slices > 1));

            if ((m_redraw is { } running) && ((running.Texels != texels) || (running.Slices != slices) || reallocated)) {
                m_redraw = null;
            }

            if (m_redraw is not { } redraw) {
                var held = (
                    holdsLayers &&
                    m_stands &&
                    !reallocated &&
                    (m_camera.HalfHeight == fresh.HalfHeight) &&
                    (m_camera.Far == fresh.Far) &&
                    (m_cell == cell) &&
                    // The chord between unit directions is their angle to well under a part in a million at these turns.
                    (Vector3.Distance(value1: m_camera.Forward, value2: fresh.Forward) <= tolerance) &&
                    (m_revision == revision)
                );

                redraw = (held ? default : new Redraw(Camera: fresh, Cell: cell, Revision: revision, Slices: slices, Next: 0, Texels: texels));
                m_redraw = (held ? null : redraw);
            }
            if (m_redraw is { } drawing) {
                m_work.Share = drawing.Next;
                m_work.Slices = drawing.Slices;
                m_work.Completes = (drawing.Next == (drawing.Slices - 1));
                m_work.StaticCamera = drawing.Camera;
                m_work.Cell = drawing.Cell;
                m_work.Revision = drawing.Revision;
            }

            if (Live) {
                // The live layer follows the camera receivers read; a moving caster inside that camera's box moves the
                // scope, as does a static caster the static layer then redraws.
                var camera = ReaderCamera;
                var cull = ((frame.MeshCull == SdfMeshCullLevel.Off) ? SdfMeshCull.None : SdfMeshCull.OfLightBox(cascade: camera));
                var motion = tables.ObserveMotion(cull: in cull, scope: m_scope, view: 0);
                var paced = (((rates & SdfUpdateRates.ShadowFar) == 0) || (Index != SdfMeshShadowCascades.FarEntry) || ((upload % (ulong)FarPeriodOf(frame: frame)) == 0UL));

                m_work.Motion = motion;
                m_work.Optical = frame.Program.HasGlassShadows;
                m_work.Live = ((camera.HalfHeight > 0f) && (
                    (m_work.Share >= 0 && m_work.Completes) ||
                    !m_liveStands ||
                    reallocated ||
                    ((motion != m_liveMotion) && paced) ||
                    (m_work.Optical != (Optical is not null))));
            }

            return (((m_work.Share >= 0) && m_work.Completes) || m_work.Live || reallocated);
        }
        // The shares one static redraw takes: the distant map's authored frames, and the far entry's refresh period (the
        // authored far rate, farShadows.hertz, spreads a redraw over the frames of one of its periods); one otherwise.
        private int SlicesOf(SdfFrame frame, SdfUpdateRates rates) => Index switch {
            SdfMeshShadowCascades.DistantEntry => Math.Min(val1: frame.DistantShadows.Slices, val2: SdfMeshOcclusion.MaxSlices),
            SdfMeshShadowCascades.FarEntry when ((rates & SdfUpdateRates.ShadowFar) != 0) => FarPeriodOf(frame: frame),
            _ => 1,
        };
        // The frames between the far entry's refreshes of its moving casters at the authored rate.
        private static int FarPeriodOf(SdfFrame frame) =>
            Math.Clamp(value: (int)MathF.Round(x: (SdfUpdateRateTiers.EveryFrameHertz / (float)Math.Max(val1: frame.FarShadows.Hertz, val2: 1))), min: 1, max: SdfMeshOcclusion.MaxSlices);
        // The light turn a held static layer tolerates: the distant map's and the far entry's authored tolerance, and
        // otherwise the turn that moves the shadow of a caster as tall as the entry's reach by one of its texels.
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
            var optical = (Live && frame.Program.HasGlassShadows);
            var changed = ((m_texels != texels) || (m_staticDepth is null));

            if (changed) {
                ReleaseLayers();
                m_texels = texels;
                m_staticDepth = Create(texels: texels, detail: "static");
                m_staticExit = Create(texels: texels, detail: "static exit");
                m_staticClear = Framebuffer(layer: m_staticDepth, load: false);
                m_staticExitClear = Framebuffer(layer: m_staticExit, load: false);
                if (Live) {
                    m_liveDepth = Create(texels: texels, detail: "live");
                    m_liveExit = Create(texels: texels, detail: "live exit");
                    m_liveClear = Framebuffer(layer: m_liveDepth, load: false);
                    m_liveExitClear = Framebuffer(layer: m_liveExit, load: false);
                    m_liveLoad = Framebuffer(layer: m_liveDepth, load: true);
                    m_liveExitLoad = Framebuffer(layer: m_liveExit, load: true);
                }
                m_stands = false;
                m_liveStands = false;
                m_sliced = false;
                m_opticalInitialized = false;
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
                changed = true;
            }
            if (changed) { m_owner.m_bindingRevision++; }

            return changed;
        }
        private Layer Create(int texels, string detail) => new(image: m_owner.m_gpu.ImageFactory.Create(
            format: SdfWorldPackage.MeshDepthFormat,
            height: (uint)texels,
            name: m_owner.Name(part: $"shadow-{EntryNames[Index]}", detail: detail),
            usage: (GpuImageUsage.DepthAttachment | GpuImageUsage.Sampled),
            width: (uint)texels));
        private IGpuFramebuffer Framebuffer(Layer layer, bool load) => m_owner.m_gpu.RenderPassFactory.CreateFramebuffer(
            colors: [],
            depth: layer.Image,
            renderPass: (load ? m_owner.m_loadPass : m_owner.m_tables.ShadowRenderPass));
        private void ReleaseLayers() {
            foreach (var framebuffer in (ReadOnlySpan<IGpuFramebuffer?>)[m_staticClear, m_staticExitClear, m_backClear, m_backExitClear, m_backLoad, m_backExitLoad, m_liveClear, m_liveExitClear, m_liveLoad, m_liveExitLoad]) {
                m_owner.Retire(replaced: framebuffer);
            }
            foreach (var layer in (ReadOnlySpan<Layer?>)[m_staticDepth, m_staticExit, m_backDepth, m_backExit, m_liveDepth, m_liveExit]) {
                m_owner.Retire(replaced: layer);
            }
            (m_staticClear, m_staticExitClear, m_backClear, m_backExitClear, m_backLoad, m_backExitLoad) = (null, null, null, null, null, null);
            (m_liveClear, m_liveExitClear, m_liveLoad, m_liveExitLoad) = (null, null, null, null);
            (m_staticDepth, m_staticExit, m_backDepth, m_backExit, m_liveDepth, m_liveExit) = (null, null, null, null, null, null);
        }

        public void Record(nint commandBuffer, SdfFrame frame, int slot) {
            var tables = m_owner.m_tables;

            if (!m_holds) { return; }
            m_recorded = true;
            // A layer allocated this upload holds nothing: it is cleared, so a view binding it before it is drawn samples
            // the far plane, and left shader-readable.
            foreach (var (layer, framebuffer) in (ReadOnlySpan<(Layer?, IGpuFramebuffer?)>)[(m_staticDepth, m_staticClear), (m_staticExit, m_staticExitClear), (m_liveDepth, m_liveClear), (m_liveExit, m_liveExitClear), (m_backDepth, m_backClear), (m_backExit, m_backExitClear)]) {
                if ((layer is { Layout: GpuImageLayout.Undefined }) && (framebuffer is not null)) {
                    Clear(commandBuffer: commandBuffer, layer: layer, framebuffer: framebuffer);
                }
            }

            if (m_work.Share >= 0) {
                var sliced = (m_work.Slices > 1);
                var depth = (sliced ? m_backDepth! : m_staticDepth!);
                var exit = (sliced ? m_backExit! : m_staticExit!);
                var load = (sliced && (m_work.Share > 0));

                Draw(commandBuffer: commandBuffer, frame: frame, slot: slot, kind: StaticKind, camera: in m_work.StaticCamera,
                    motion: SdfMeshMotion.Static, share: m_work.Share, slices: m_work.Slices, depth: depth, exit: exit,
                    depthFramebuffer: (sliced ? (load ? m_backLoad! : m_backClear!) : m_staticClear!),
                    exitFramebuffer: (sliced ? (load ? m_backExitLoad! : m_backExitClear!) : m_staticExitClear!));
                // A static layer drawn now may miss a caster absent from the records, whose landing must then move the
                // static revision.
                tables.CastersRead();
                if (sliced && m_work.Completes) {
                    Copy(commandBuffer: commandBuffer, source: m_backDepth!, destination: m_staticDepth!);
                    Copy(commandBuffer: commandBuffer, source: m_backExit!, destination: m_staticExit!);
                }
                Rest(commandBuffer: commandBuffer, layer: depth);
                Rest(commandBuffer: commandBuffer, layer: exit);
            }

            if (m_work.Live) {
                var camera = ReaderCamera;

                Copy(commandBuffer: commandBuffer, source: m_staticDepth!, destination: m_liveDepth!);
                Copy(commandBuffer: commandBuffer, source: m_staticExit!, destination: m_liveExit!);
                Draw(commandBuffer: commandBuffer, frame: frame, slot: slot, kind: LiveKind, camera: in camera,
                    motion: SdfMeshMotion.Dynamic, share: 0, slices: 1, depth: m_liveDepth!, exit: m_liveExit!,
                    depthFramebuffer: m_liveLoad!, exitFramebuffer: m_liveExitLoad!);
                Rest(commandBuffer: commandBuffer, layer: m_liveDepth!);
                Rest(commandBuffer: commandBuffer, layer: m_liveExit!);
            }

            Rest(commandBuffer: commandBuffer, layer: m_staticDepth!);
            Rest(commandBuffer: commandBuffer, layer: m_staticExit!);
        }
        // Selects a kind's draws on the GPU and draws them into an entry's two layers from a light camera: the entry
        // depth (back-face culled where the draw encloses a solid), then the exit layer, and for a live layer the glass
        // extinction against the final depth.
        private void Draw(nint commandBuffer, SdfFrame frame, int slot, int kind, in SdfMeshShadowCamera camera, SdfMeshMotion motion, int share, int slices,
            Layer depth, Layer exit, IGpuFramebuffer depthFramebuffer, IGpuFramebuffer exitFramebuffer) {
            var tables = m_owner.m_tables;
            var recorder = m_owner.m_gpu.Recorder;
            var selection = m_selections[kind];
            var count = tables.MeshDrawCount;
            var set = m_meshSets[kind][slot];
            var block = m_owner.m_block.AsSpan();
            var texels = m_texels;
            var area = new GpuPixelRect(Height: (uint)texels, Width: (uint)texels, X: 0, Y: 0);
            var detail = ((frame.MeshDetail == SdfMeshDetailLevel.Auto) ? SdfMeshDetail.Of(cascade: in camera, texels: texels) : SdfMeshDetail.Forced(level: (((int)frame.MeshDetail) - 1)));
            var cull = ((frame.MeshCull == SdfMeshCullLevel.Off) ? SdfMeshCull.None : SdfMeshCull.OfLightBox(cascade: in camera));
            var optical = ((kind == LiveKind) ? Optical : null);

            block.Clear();
            SdfFrameBlock.Write(block: block, frame: frame, height: (uint)texels, lightMask: tables.LightMaskOf(frame: frame, view: 0), tables: tables.PassValues, view: 0, width: (uint)texels);
            SdfFrameBlock.WriteShadowCamera(block: block, camera: in camera, texels: texels);
            SdfFrameBlock.WriteWorkCounterRow(block: block, row: GpuKernelCounters.DisabledRow);
            SdfFrameBlock.WriteWorkCounterDetailRow(block: block, row: GpuKernelCounters.DisabledRow);
            m_blocks[kind][slot].Write<byte>(data: block);

            Move(commandBuffer: commandBuffer, layer: depth, to: GpuImageLayout.DepthAttachment);
            Move(commandBuffer: commandBuffer, layer: exit, to: GpuImageLayout.DepthAttachment);

            if (count > 0) {
                var bound = m_meshSetBuffers[kind][slot];

                tables.WriteMeshTables(bound: bound, set: set, slot: tables.CurrentSlot);
                tables.WriteMeshWorkCounters(bound: ref bound[MeshSetCounters], counters: m_owner.m_counters, set: set);
                tables.WriteMeshBuffer(bound: ref bound[MeshSetVisibility], buffer: tables.DummyBuffer, member: SdfWorldPackage.VisibilityRecords, set: set);
                tables.WriteMeshBuffer(bound: ref bound[MeshSetGlassSums], buffer: tables.DummyBuffer, member: SdfWorldPackage.GlassSumsWritten, set: set);
                SdfMeshOcclusion.Unbind(bound: m_meshSetOcclusion[kind][slot], boundTarget: ref m_meshSetTargets[kind][slot], first: 0, set: set, tables: tables);
                selection.Stage(tables: tables, count: count, width: (uint)texels, height: (uint)texels, slot: slot, cull: in cull, detail: in detail, held: tables.HoldsFrame,
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
                selection.RecordEarly(commandBuffer: commandBuffer, pipeline: tables.OcclusionPipeline, recorder: recorder, set: m_occlusionSets[OcclusionSetOf(kind: kind, slot: slot, phase: 0)][0]);
            }

            // The entry depth: back-face culled for draws that enclose a solid, unculled for the rest.
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: depthFramebuffer);
            if (count > 0) {
                Phase(commandBuffer: commandBuffer, phase: 0, pipeline: tables.ShadowPipeline, selection: selection, set: set);
                Phase(commandBuffer: commandBuffer, phase: SdfKernelInterfaces.MeshShadowCulledEntryPhase, pipeline: tables.ShadowEntryPipeline, selection: selection, set: set);
            }
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
            // The exit layer: front-face culled solids, then sheets and clipped draws unculled.
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: exitFramebuffer);
            if (count > 0) {
                Phase(commandBuffer: commandBuffer, phase: 2, pipeline: tables.ShadowExitPipeline, selection: selection, set: set);
                Phase(commandBuffer: commandBuffer, phase: 3, pipeline: tables.ShadowPipeline, selection: selection, set: set);
            }
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);

            if ((optical is not null) && (count > 0)) {
                Extinction(commandBuffer: commandBuffer, slot: slot, depth: depth, exitFramebuffer: exitFramebuffer, area: area, optical: optical);
            }
        }
        // Clears an entry's glass extinction and draws every glass draw's into it against the final depth, which the glass
        // fragment samples (its own attachment is the exit layer, whose test the glass fragment's zero depth always fails,
        // so only its side effects land).
        private void Extinction(nint commandBuffer, int slot, Layer depth, IGpuFramebuffer exitFramebuffer, GpuPixelRect area, IGpuBuffer optical) {
            var tables = m_owner.m_tables;
            var recorder = m_owner.m_gpu.Recorder;
            var selection = m_selections[LiveKind];
            var glassSet = m_glassSets[slot];
            var bound = m_glassSetBuffers[slot];
            var full = !m_opticalInitialized;

            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: (GpuAccess.ShaderRead | GpuAccess.ShaderWrite), sourceStageMask: (GpuStage.ComputeShader | GpuStage.FragmentShader),
                destinationAccessMask: (full ? GpuAccess.TransferWrite : (GpuAccess.ShaderRead | GpuAccess.ShaderWrite)), destinationStageMask: (full ? GpuStage.Transfer : GpuStage.ComputeShader));
            if (full) {
                recorder.ClearStorageBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle, sizeBytes: optical.SizeBytes);
                m_opticalInitialized = true;
            } else {
                selection.RecordOpticalClear(recorder: recorder, commandBuffer: commandBuffer, pipeline: tables.OcclusionPipeline,
                    set: m_occlusionSets[OcclusionSetOf(kind: LiveKind, slot: slot, phase: 0)][0], side: (uint)m_texels);
            }
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: (full ? GpuAccess.TransferWrite : GpuAccess.ShaderWrite), sourceStageMask: (full ? GpuStage.Transfer : GpuStage.ComputeShader),
                destinationAccessMask: (GpuAccess.ShaderRead | GpuAccess.ShaderWrite), destinationStageMask: (GpuStage.FragmentShader | GpuStage.ComputeShader));

            tables.WriteMeshTables(bound: bound, set: glassSet, slot: tables.CurrentSlot);
            tables.WriteMeshWorkCounters(bound: ref bound[MeshSetCounters], counters: m_owner.m_counters, set: glassSet);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetVisibility], buffer: tables.DummyBuffer, member: SdfWorldPackage.VisibilityRecords, set: glassSet);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetGlassSums], buffer: optical, member: SdfWorldPackage.GlassSumsWritten, set: glassSet);
            SdfMeshOcclusion.Unbind(bound: m_glassSetOcclusion[slot], boundTarget: ref m_glassSetTargets[slot], first: 0, set: glassSet, tables: tables);
            tables.WriteMeshTarget(bound: ref m_glassSetTargets[slot], set: glassSet, view: depth.Image.ImageViewHandle);
            selection.BindDraws(tables: tables, set: glassSet, bound: m_glassSetOcclusion[slot]);
            Move(commandBuffer: commandBuffer, layer: depth, to: GpuImageLayout.ShaderReadOnly);
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: exitFramebuffer);
            Phase(commandBuffer: commandBuffer, phase: GlassPhase, pipeline: tables.ShadowGlassPipeline, selection: selection, set: glassSet);
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.FragmentShader,
                destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: (GpuStage.FragmentShader | GpuStage.ComputeShader));
        }
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
            if (!m_holds) { return; }

            var counts = m_work.Counts;

            if (!m_recorded) { return; }
            if (m_work.Share >= 0) {
                counts?.Count(kind: (m_work.Completes ? m_kinds.Static : m_kinds.Share));
                if (m_redraw is { } redraw) {
                    m_redraw = (m_work.Completes ? null : (redraw with { Next = (redraw.Next + 1) }));
                }
                if (m_work.Completes) {
                    m_camera = m_work.StaticCamera;
                    m_cell = m_work.Cell;
                    m_revision = m_work.Revision;
                    m_stands = true;
                    m_heldUploads = 0;
                }
            }
            if (m_work.Live) {
                counts?.Count(kind: m_kinds.Live);
                m_liveMotion = m_work.Motion;
                m_liveStands = true;
            }
            if ((m_work.Share < 0) && !m_work.Live) {
                counts?.Count(kind: m_kinds.Held);
                m_heldUploads++;
            }
            m_recorded = false;
        }
        public void Completed(int slot) {
            foreach (var selection in m_selections) { selection.Completed(slot: slot); }
        }
        public string Describe() => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"shadow.{EntryNames[Index]}=half{m_camera.HalfHeight:0.#}m/texels{m_texels}/stands{(m_stands ? 1 : 0)}/held{m_heldUploads}/redraw{(m_redraw is { } r ? $"{r.Next}of{r.Slices}" : "-")}/live{(Live ? (m_liveStands ? 1 : 0) : -1)}{((Optical is not null) ? "/optical" : string.Empty)}");

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
