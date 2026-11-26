using Puck.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Maths;

namespace Puck.SdfVm;

// One pass of an sdf.world instance (SdfWorldPasses): a part of the package's fragment, recorded into the instance's
// command buffer for the pass. Every part writes its pass block (SdfFrameBlock): the view's camera, the frame's levers,
// light count and curvature shading, the world's shared shadow cascades, the irradiance volume and the world values. Every
// compute part binds the residency's World set of the ring slot the frame's upload wrote, which holds its tables (the
// lights and the sky among them), and the world interface's pass group: the fragment storages its ports bind, the world's
// shadow layers (SdfWorldShadows, drawn by the residency's upload, never by a view) and, at every member nothing binds, a
// dummy of the residency's; the node's work counters for the frame slot, whose row it writes into its pass block; and the
// screens, whose host images are rewritten every frame. The mesh part draws the frame's mesh draws into its target through
// the mesh pipeline, with a set of its own per frame slot binding its pass block. A recorder records no barrier on the graph's resources:
// the planner's are the instance's, and the node's orders the work counters. The one exception is the mesh part's
// occlusion (SdfMeshOcclusion), which orders its own buffers and moves the mesh target between its two phases, leaving it
// in its attachment layout as the planner expects.
//
// The recording splits in two (IRenderGraphPackageEmitter): Decide changes, in pass order, whatever another pass of the
// instance, its commit or its residency reads, and keeps the values the draw needs in m_decided; Emit records them and may
// run beside the instance's other passes' emissions, so it reads only m_decided, the recorder's own state and the
// residency's staged tables and frame, and writes only the recorder's own state, its own sets and the command buffer.
internal sealed class SdfWorldPassRecorder : SdfViewRecorder, IRenderGraphPackageEmitter, IRenderGraphPackageReadback {
    // Views attribute analytic sky lighting by layer; the bins pass counts each bin's tiles (sdf-world-views-bins.comp.hlsl),
    // a row for every bin a plan gives a width (SdfShadeDispatch.PlanBins).
    public IReadOnlyList<string> WorkDetails(in FrameContext context) => m_part switch {
        SdfWorldPackage.Parts.Views => m_view.Residency.SkyDetails.Labels,
        SdfWorldPackage.Parts.ViewsProbes => RateDetails,
        SdfWorldPackage.Parts.ViewsBins => BinDetails,
        _ => [],
    };

    private static readonly string[] RateDetails = ["tiles-1x1", "tiles-2x1", "tiles-1x2", "tiles-2x2"];
    private static readonly string[] BinDetails = ["no-program", "program"];
    // The world interface's scratch buffer members, each bound to the dummy unless a port binds it.
    private static readonly string[] ScratchMembers = [
        SdfWorldPackage.VisibilityRecords,
        SdfWorldPackage.VisibilityRecordsWritten,
        SdfWorldPackage.ReactivityWritten,
        SdfWorldPackage.MeshPyramid,
        SdfWorldPackage.MeshPyramidWritten,
        SdfWorldPackage.MeshShadowNearOptical,
        SdfWorldPackage.MeshShadowFarOptical,
        SdfWorldPackage.MeshShadowSecondOptical,
        SdfWorldPackage.MeshShadowNearBlocks,
        SdfWorldPackage.MeshShadowFarBlocks,
        SdfWorldPackage.MeshShadowSecondBlocks,
        SdfWorldPackage.MeshShadowDistantBlocks,
        SdfWorldPackage.ViewsFilterRecords,
        SdfWorldPackage.ViewsFilterRecordsWritten,
        SdfWorldPackage.ViewsProbeRecords,
        SdfWorldPackage.ViewsProbeRecordsWritten,
        SdfWorldPackage.ShadeTiles,
        SdfWorldPackage.ShadeTilesWritten,
        SdfWorldPackage.ShadeArgumentsWritten,
        SdfWorldPackage.HistorySurface,
        SdfWorldPackage.ViewsProbeHistory,
        SdfWorldPackage.ViewsProbeHistoryWritten,
    ];
    // Each layer of the world's shared shadows (SdfWorldShadows) a compute pass samples: its entry, whether it is the exit
    // layer, and the binding.
    private static readonly (int Entry, bool Exit, uint Binding)[] CascadeBindings = [
        (SdfMeshShadowCascades.NearEntry, false, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowNear)),
        (SdfMeshShadowCascades.FarEntry, false, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowFar)),
        (SdfMeshShadowCascades.SecondEntry, false, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowSecond)),
        (SdfMeshShadowCascades.NearEntry, true, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowNearExit)),
        (SdfMeshShadowCascades.FarEntry, true, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowFarExit)),
        (SdfMeshShadowCascades.SecondEntry, true, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowSecondExit)),
        (SdfMeshShadowCascades.DistantEntry, false, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowDistant)),
        (SdfMeshShadowCascades.DistantEntry, true, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowDistantExit)),
    ];
    // Each entry's glass extinction and the member a compute pass reads it through.
    private static readonly (int Entry, string Member)[] OpticalBindings = [
        (SdfMeshShadowCascades.NearEntry, SdfWorldPackage.MeshShadowNearOptical),
        (SdfMeshShadowCascades.FarEntry, SdfWorldPackage.MeshShadowFarOptical),
        (SdfMeshShadowCascades.SecondEntry, SdfWorldPackage.MeshShadowSecondOptical),
    ];
    // Each entry's block bounds, of the layers CascadeBindings binds, and the member a compute pass reads them through.
    private static readonly (int Entry, string Member)[] BlocksBindings = [
        (SdfMeshShadowCascades.NearEntry, SdfWorldPackage.MeshShadowNearBlocks),
        (SdfMeshShadowCascades.FarEntry, SdfWorldPackage.MeshShadowFarBlocks),
        (SdfMeshShadowCascades.SecondEntry, SdfWorldPackage.MeshShadowSecondBlocks),
        (SdfMeshShadowCascades.DistantEntry, SdfWorldPackage.MeshShadowDistantBlocks),
    ];
    private static readonly uint OutputBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.Output);
    private static readonly uint MeshVisibilityBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshVisibility);
    private static readonly uint ScreenSourcesBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.ScreenSources);

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly string[] m_inputs;
    private readonly string[] m_outputs;
    // Whether the part reads the preceding resolve's history surface (SdfWorldPackage.RateFragment's probe lookup).
    private readonly bool m_readsRateHistory;
    // The history buffers the part reads or writes through members (SdfHistoryPorts).
    private readonly SdfHistoryPorts m_history;
    private readonly SdfWorldPasses m_owner;
    private readonly string m_part;
    private readonly bool m_resolved;
    // Whether the views pass's graph shades in bins (SdfWorldPasses.ShadesInBins); it records unsplit on a frame whose
    // plan does not bin.
    private readonly bool m_binned;

    // Which screen indices the residency binds.

    // A compute part's frame and pass sets, one of each per frame slot.
    private readonly RenderGraphPackageSets? m_sets;
    // Per frame slot: the tables the pass set's ports and dummies were written for, and each screen element's last written
    // view.
    private readonly SdfWorldTables?[] m_portTables;
    // Per frame slot, the shared shadows' binding revision the slot's pass set was written at.
    private readonly long[] m_portShadows;
    // Per frame slot, the counter buffer the slot's pass set binds: the node replaces a slot's buffer when its detail rows
    // grow, so the binding follows the buffer the recording names rather than the one the ports were first written with.
    private readonly IGpuBuffer?[] m_boundCounters;
    private readonly nint[][] m_screens;
    // The mesh part's pool, its set per frame slot and tables ring slot (MeshSetOf), and a framebuffer over each instance
    // of its target and depth.
    private readonly nint m_meshPool;

    private readonly nint[] m_meshSets = [];
    // The buffers each mesh set binds now: its mesh region, its dynamic transforms, its work counters, and the
    // visibility records and glass sums the glass pass reads and accumulates (the dummy in every other raster part).
    private readonly IGpuBuffer?[][] m_meshSetBuffers = [];

    private const int MeshSetBuffers = 8;
    private const int MeshSetCounters = 2;
    private const int MeshSetGlassSums = 4;
    private const int MeshSetOcclusionBuffers = 6;
    private const int MeshSetVisibility = 3;
    private const int OcclusionSetsPerMesh = 2;

    private readonly IReadOnlyList<IGpuImage> m_meshTargets = [];
    private readonly IGpuFramebuffer[] m_framebuffers = [];

    // The mesh part's occlusion (SdfMeshOcclusion): its state, the render pass its second phase draws in, loading what
    // the first drew, and per mesh set the occlusion's buffers and the target view the set binds.
    private readonly SdfMeshOcclusion? m_occlusion;
    private readonly IGpuRenderPass? m_lateRenderPass;

    // The framebuffers the mesh part's occlusion phase draws in, one per instance of its target, over the target and depth.
    private readonly IGpuFramebuffer[] m_lateFramebuffers = [];
    // The glass clear's record of each glass sums buffer it has cleared (GlassClear): the bytes the glass raster may have
    // left nonzero there, from the rows its glass draws spanned (SdfWorldTables.GlassRows). The invariant is that every
    // word outside that range is zero, so a clear of the range alone leaves the whole buffer zero for the raster. A buffer
    // this recorder has not cleared yet holds anything and is cleared whole. The raster stands and records with the clear
    // (one signature), so the range a submitted clear staged is the one its frame's raster wrote within.
    private readonly List<(IGpuBuffer Buffer, ulong Start, ulong End)> m_glassDirty = [];
    // The rows a glass draw's span is widened by on each side: the jitter moves the grid under half a pixel, and a
    // rasterized triangle covers the pixels whose centres it holds.
    private const uint GlassRowMargin = 2;
    private (IGpuBuffer Buffer, ulong Start, ulong End)? m_glassPending;
    private readonly IGpuBuffer?[][] m_meshSetOcclusion = [];
    private readonly nint[] m_meshSetTargets = [];
    // Early and late compute sets beside each mesh set. Early binds a sampled filler while the target is an attachment;
    // late binds the target that RecordLate transitions for sampling. A descriptor remains valid even on a branch that
    // does not read it, and rewriting one shared set would change both recorded dispatches.
    private readonly nint[] m_occlusionSets = [];
    private readonly IGpuBuffer?[][] m_occlusionSetBuffers = [];
    private readonly nint[] m_occlusionSetTargets = [];
    // Whether each instance of the mesh part's target has been drawn or cleared once: a target drawn in no submitted frame
    // of this graph has no contents.
    private readonly bool[] m_targetsWritten = [];
    // Whether each instance's target last recorded a frame holding none of the part's draws, so it holds that recording's
    // clear and no hit a later frame's draw records could misname (PartDrawCount).
    private readonly bool[] m_targetsClear = [];

    // The target instance the latest recording writes, with whether it writes the clear of a frame holding none of the
    // part's draws. Only Submitted applies it to the two arrays above, and the node calls Submitted only once the
    // recording's submission reached the queue, so a recording whose submission never ran marks no target written or clear.
    private (int Index, bool Clear)? m_recordedTarget;
    private bool m_disposed;
    // What made the latest views render read external content, or null when nothing did.
    private string? m_sourceTaint;
    private bool m_readsHistory;

    private readonly SdfWorldPickReadback? m_pick;
    private readonly SdfWorldPickReadback? m_diagnosticPick;

    private SdfShadeDispatch? m_shadePlan;

    private readonly SdfShadeSlots m_shade;

    // What the frame's decision chose for its emission.
    private struct Decided {
        public SdfWorldTables? Tables;
        // The glass clear's range of its sums.
        public IGpuBuffer? Sums;
        public ulong ClearStart;
        public ulong ClearEnd;
        // A raster part's draw.
        public bool Occlude;
        public SdfMeshCull Cull;
        public SdfMeshDetail Detail;
        // The views pass's kernels, pinned for the recording's submission.
        public SdfShadeSelection Selection;
    }
    private Decided m_decided;
    // The image each screen index samples this frame, which a compute part's decision took, or zero.
    private readonly nint[] m_screenImages = new nint[SdfWorldTables.MaxScreenSurfaces];

    public SdfWorldPassRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) : base(owner: owner, instance: context.Instance, view: view) {
        m_context = context;
        m_shade = new SdfShadeSlots(slots: context.InFlightFrames);
        m_owner = owner;
        m_part = (context.Part ?? throw new ArgumentException(message: $"Pass '{context.Pass}' runs no part of '{RenderGraphPackageCatalog.SdfWorld}'.", paramName: nameof(context)));
        // Port declarations are captured by graph planning, before the asynchronous build. The live frame may already
        // request another fade capacity when this recorder installs or follows a view.
        m_inputs = LocalNames(pass: context.Pass, part: m_part, ports: context.Inputs);
        m_outputs = LocalNames(pass: context.Pass, part: m_part, ports: context.Outputs);
        m_readsRateHistory = m_inputs.Contains(value: SdfWorldPackage.Parts.HistorySurface);
        m_history = new SdfHistoryPorts(inputs: m_inputs, outputs: m_outputs, slots: context.InFlightFrames);

        m_resolved = m_outputs.Contains(value: SdfWorldPackage.CurrentColor);
        m_binned = SdfWorldPasses.ShadesInBins(context: context);


        var slots = context.InFlightFrames;
        var tables = (view.Residency.Tables ?? throw new InvalidOperationException(message: $"Residency '{view.Residency.Name}' has no tables for pass '{context.Pass}'."));

        m_portTables = new SdfWorldTables?[slots];
        m_portShadows = new long[slots];
        m_boundCounters = new IGpuBuffer?[slots];
        m_screens = new nint[slots][];

        for (var slot = 0; (slot < slots); slot++) {
            m_screens[slot] = new nint[SdfWorldTables.MaxScreenSurfaces];
        }

        // The glass clear records one buffer clear and binds nothing.
        if (m_part == SdfWorldPackage.Parts.GlassClear) {
            return;
        }
        if (!IsMesh) {
            m_sets = new RenderGraphPackageSets(
                context: context,
                groupLayoutHandles: tables.Pipeline(kernel: SdfKernel.Surface).GroupLayoutHandles,
                groups: groups
            );

            if (string.Equals(a: m_part, b: SdfWorldPackage.Parts.Views, comparisonType: StringComparison.Ordinal)) {
                m_pick = new SdfWorldPickReadback(picker: owner.PickerOf(instance: context.Instance), context: context);
                m_diagnosticPick = new SdfWorldPickReadback(picker: owner.DiagnosticPickerOf(instance: context.Instance), context: context);
            }

            return;
        }

        // The mesh and glass parts draw through the mesh interface's layout, whose one pass group holds the pass block the
        // world interface lays out and the mesh region: a set per frame slot and tables ring slot from a pool of its own,
        // its block bound to the frame slot's pass block buffer, two selection sets beside each, and a framebuffer over each
        // instance of its target and depth.
        var bindings = context.Services.Bindings;
        var sizes = default(GpuDescriptorPoolSizes);
        var sets = (slots * SdfWorldTables.FrameRingSize);

        for (var set = 0; (set < sets); set++) {
            sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.Mesh.Groups);
            for (var phase = 0; (phase < OcclusionSetsPerMesh); phase++) {
                sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.MeshOcclusion.Groups);
            }
        }

        m_meshPool = bindings.CreatePool(
            name: new GpuObjectName(
                owner: context.Instance,
                part: context.Pass
            ),
            sizes: sizes
        );

        try {
            m_meshSets = new nint[sets];
            m_meshSetBuffers = new IGpuBuffer?[sets][];
            m_meshSetOcclusion = new IGpuBuffer?[sets][];
            m_meshSetTargets = new nint[sets];
            m_occlusionSets = new nint[sets * OcclusionSetsPerMesh];
            m_occlusionSetBuffers = new IGpuBuffer?[sets * OcclusionSetsPerMesh][];
            m_occlusionSetTargets = new nint[sets * OcclusionSetsPerMesh];

            for (var set = 0; (set < sets); set++) {
                var slot = (set / SdfWorldTables.FrameRingSize);

                m_meshSetBuffers[set] = new IGpuBuffer?[MeshSetBuffers];
                m_meshSetOcclusion[set] = new IGpuBuffer?[MeshSetOcclusionBuffers];
                for (var phase = 0; (phase < OcclusionSetsPerMesh); phase++) {
                    var occlusionSet = ((set * OcclusionSetsPerMesh) + phase);

                    m_occlusionSetBuffers[occlusionSet] = new IGpuBuffer?[(MeshSetBuffers + MeshSetOcclusionBuffers)];
                    m_occlusionSets[occlusionSet] = bindings.AllocateSet(
                        descriptorSetLayoutHandle: tables.OcclusionPipeline.GroupLayoutHandles[((int)ShaderInterfaceGroup.Pass)],
                        name: new GpuObjectName(
                            detail: "occlusion group",
                            index: occlusionSet,
                            owner: context.Instance,
                            part: context.Pass
                        ),
                        poolHandle: m_meshPool
                    );
                    bindings.WriteConstantBuffer(
                        arrayElement: 0,
                        binding: 0,
                        bufferHandle: groups.PassBlocks[slot].BufferHandle,
                        bufferSize: groups.PassBlocks[slot].SizeBytes,
                        descriptorSetHandle: m_occlusionSets[occlusionSet]
                    );
                }

                m_meshSets[set] = bindings.AllocateSet(
                    descriptorSetLayoutHandle: tables.MeshPipeline.GroupLayoutHandles[((int)ShaderInterfaceGroup.Pass)],
                    name: new GpuObjectName(
                        detail: "mesh group",
                        index: set,
                        owner: context.Instance,
                        part: context.Pass
                    ),
                    poolHandle: m_meshPool
                );
                bindings.WriteConstantBuffer(
                    arrayElement: 0,
                    binding: 0,
                    bufferHandle: groups.PassBlocks[slot].BufferHandle,
                    bufferSize: groups.PassBlocks[slot].SizeBytes,
                    descriptorSetHandle: m_meshSets[set]
                );
            }

            m_meshTargets = groups.OutputImages[0];
            m_framebuffers = new IGpuFramebuffer[m_meshTargets.Count];
            m_targetsWritten = new bool[m_meshTargets.Count];
            m_targetsClear = new bool[m_meshTargets.Count];

            for (var index = 0; (index < m_framebuffers.Length); index++) {
                m_framebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(
                    colors: [m_meshTargets[index]],
                    depth: groups.OutputImages[1][index],
                    renderPass: tables.MeshRenderPass
                );
            }
            if (m_part == SdfWorldPackage.Parts.Mesh) {
                m_lateRenderPass = context.Services.RenderPassFactory.Create(
                    description: SdfMeshRasterPass.LateRenderPass,
                    name: new GpuObjectName(detail: "occlusion", owner: context.Instance, part: context.Pass)
                );
                m_lateFramebuffers = new IGpuFramebuffer[m_meshTargets.Count];

                for (var index = 0; (index < m_lateFramebuffers.Length); index++) {
                    m_lateFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(
                        colors: [m_meshTargets[index]],
                        depth: groups.OutputImages[1][index],
                        renderPass: m_lateRenderPass
                    );
                }
            }
            m_occlusion = new SdfMeshOcclusion(buffers: context.Services.BufferFactory, owner: context.Instance, part: context.Pass, slots: slots, cascade: false,
                sight: ((m_part == SdfWorldPackage.Parts.Mesh) ? owner.PortalSightOf(instance: context.Instance) : null));
        } catch {
            foreach (var framebuffer in m_framebuffers) {
                framebuffer?.Dispose();
            }
            foreach (var framebuffer in m_lateFramebuffers) {
                framebuffer?.Dispose();
            }

            m_occlusion?.Dispose();
            m_lateRenderPass?.Dispose();

            bindings.DestroyPool(poolHandle: m_meshPool);

            throw;
        }
    }

    // Whether the part rasterizes the frame's mesh draws through the mesh pipeline: the mesh pass, or the glass pass, which
    // draws the glass draws alone. The shadows are the residency's, drawn by its upload (SdfWorldShadows).
    private bool IsMesh => (m_part is SdfWorldPackage.Parts.Mesh or SdfWorldPackage.Parts.Glass);
    private bool IsGlass => (m_part is SdfWorldPackage.Parts.Glass);
    // The interface every compute part's pass set is laid out by.
    private static ShaderInterfaceLayout PassLayout => SdfWorldInterfaces.WorldLayout;

    // The draws the part rasterizes: the glass draws for the glass pass, the opaque ones for the mesh pass.
    private uint PartDrawCount(SdfWorldTables tables) => (IsGlass ? tables.GlassDrawCount : tables.OpaqueDrawCount);

    // Whether a mesh frame holding none of the part's draws may skip: every target instance already holds the clear such a
    // frame records. A target that last drew hits keeps them when its part skips, and the surface pass reads a hit's draw
    // and triangle against draw records that may no longer hold their meshes. The glass passes skip by their own answer
    // instead (SdfWorldPasses.GlassSeen), which skips the shading pass that reads their target with them.
    private bool TargetsHoldClear => Array.TrueForAll(array: m_targetsClear, match: static clear => clear);

    // The index of the target instance a recording binds among the part's instances, or -1 for none of them.
    private int TargetIndexOf(IGpuImage? target) {
        for (var index = 0; (index < m_meshTargets.Count); index++) {
            if (ReferenceEquals(objA: m_meshTargets[index], objB: target)) {
                return index;
            }
        }

        return -1;
    }

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_pick?.Dispose();
        m_diagnosticPick?.Dispose();
        foreach (var framebuffer in m_framebuffers) {
            framebuffer.Dispose();
        }
        foreach (var framebuffer in m_lateFramebuffers) {
            framebuffer.Dispose();
        }

        m_lateRenderPass?.Dispose();
        m_occlusion?.Dispose();

        if (m_meshPool != 0) {
            m_context.Services.Bindings.DestroyPool(poolHandle: m_meshPool);
        }

        m_shade.Release(releases: m_owner.ShadeReleases);
        LeaveView();
    }
    // A part skips a frame whose work it would not do, recording neither its work nor its planned barriers: the mesh part
    // a frame that draws no opaque mesh, and the glass passes one whose view sees no glass draw. The mesh part with no draws
    // still records until every instance of its target holds the clear such a frame records (TargetsHoldClear): before the
    // first bake lands, and after a frame that drew, whose hits a skipped frame would leave for the surface pass to read. No
    // part walks the field: the bake samples it as the residency's background GPU work (SdfMeshBakeGpuWork).
    public bool Skips(in FrameContext context) {
        switch (m_part) {
            case SdfWorldPackage.Parts.Mesh: {
                    var staged = m_view.Residency.Staged(context: in context);
                    var skips = ((PartDrawCount(tables: staged) == 0) && TargetsHoldClear);

                    if (skips) { m_owner.SkippedClearMeshTarget(instance: m_context.Instance, tables: staged); }
                    return skips;
                }
            // The glass clear and the glass raster skip a frame in which no glass draw meets the view's frustum
            // (SdfWorldPasses.GlassSeen); the shading pass, which reads their sums and target, then draws nothing.
            case SdfWorldPackage.Parts.GlassClear:
            case SdfWorldPackage.Parts.Glass: {
                    return !m_owner.GlassSeen(instance: m_context.Instance, view: m_view, context: in context);
                }
            // The surface pass reads the pyramid only for a pixel the mesh covers, which a frame with no draws has none of.
            case SdfWorldPackage.Parts.Pyramid: {
                    return (m_view.Residency.Staged(context: in context).MeshDrawCount == 0);
                }
            // The filter and bins passes skip a frame whose views do not shade in bins (SdfWorldResidency.ShadesInBins).
            case SdfWorldPackage.Parts.ViewsFilter:
            case SdfWorldPackage.Parts.ViewsBins: {
                    return !m_view.Residency.ShadesInBins;
                }
            default:
                return false;
        }
    }
    public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) =>
        m_owner.SignatureOf(instance: m_context.Instance, part: m_part, temporal: m_temporal, context: in context, reads: reads);
    public ulong? InputSignature(string input, long generation) => unchecked((ulong)generation);
    public void Stood() { }

    private SdfMeshRowStamp m_previousMeshRows;
    private bool m_notedMeshRows;

    public void Submitted() {
        m_notedMeshRows = false;
        if (m_recordedTarget is { } recorded) {
            m_targetsWritten[recorded.Index] = true;
            m_targetsClear[recorded.Index] = recorded.Clear;
            m_recordedTarget = null;
        }
        if (m_glassPending is { } cleared) {
            var known = FindGlassDirtyIndex(buffer: cleared.Buffer);

            if (known >= 0) { m_glassDirty[known] = cleared; } else { m_glassDirty.Add(item: cleared); }
            m_glassPending = null;
        }
        if (m_part == SdfWorldPackage.Parts.Views) {
            m_owner.SubmittedTaint(m_context.Instance, m_sourceTaint, m_readsHistory);
        }
        if ((m_part == SdfWorldPackage.Parts.Views) && !m_resolved) { m_owner.MarkSampleRendered(instance: m_context.Instance); }
    }
    private int FindGlassDirtyIndex(IGpuBuffer buffer) {
        for (var index = 0; (index < m_glassDirty.Count); index++) {
            if (ReferenceEquals(objA: m_glassDirty[index].Buffer, objB: buffer)) {
                return index;
            }
        }
        return -1;
    }
    // Decides the pass's frame: everything the instance's later passes, its commit or its residency read (the rows and hits
    // it notes, the view's history, the shading plan it commits, the screens' leases) changes here, in pass order, and the
    // values the emission draws by are kept in m_decided.
    public RenderGraphPackageOutcome Decide(in RenderGraphPackageRecording recording) {
        m_recordedTarget = null;

        var residency = m_view.Residency;
        var tables = residency.Staged(context: recording.Context);

        m_decided = new Decided { Tables = tables };

        if (m_part == SdfWorldPackage.Parts.Mesh) {
            m_previousMeshRows = m_owner.NoteMeshRows(instance: m_context.Instance, stamp: tables.RowStamp);
            m_notedMeshRows = true;
        }

        m_owner.RecordHits(instance: m_context.Instance, part: m_part, tables: tables);
        // The glass clear zeroes the glass sums, its one output, ahead of the glass pass's accumulation: the bytes the last
        // submitted raster may have written (m_glassDirty), and it stages the rows this frame's raster may write, at the
        // render grid's width that the raster addresses the sums by (sdfGlassSumWord).
        if (m_part == SdfWorldPackage.Parts.GlassClear) {
            var sums = recording.Outputs[0].Buffer!;
            var known = FindGlassDirtyIndex(buffer: sums);
            var (start, end) = ((known >= 0) ? (m_glassDirty[known].Start, Math.Min(val1: m_glassDirty[known].End, val2: sums.SizeBytes)) : (0UL, sums.SizeBytes));
            var renderFrame = residency.Frame!;
            var rows = tables.GlassRows(
                camera: renderFrame.Views[Math.Min(val1: m_view.View, val2: (renderFrame.Views.Count - 1))].Camera,
                height: recording.RenderHeight,
                margin: GlassRowMargin
            );
            var rowBytes = ((ulong)recording.RenderWidth * SdfWorldPackage.GlassSumWords * sizeof(uint));

            m_owner.NoteGlassExtent(instance: m_context.Instance, width: recording.RenderWidth, height: recording.RenderHeight);
            m_decided.Sums = sums;
            m_decided.ClearStart = start;
            m_decided.ClearEnd = end;
            m_glassPending = ((rows is { } span) ? (sums, (span.First * rowBytes), ((span.Last + 1UL) * rowBytes)) : (sums, 0UL, 0UL));

            return RenderGraphPackageOutcome.Drew;
        }
        var frame = residency.Frame!;
        var view = Math.Min(
            val1: m_view.View,
            val2: (frame.Views.Count - 1)
        );
        var width = recording.Width;
        var height = recording.Height;

        if (IsGlass) {
            m_owner.NoteGlassExtent(instance: m_context.Instance, width: width, height: height);
        }
        residency.RequestExtent(height: recording.FrameHeight, width: recording.FrameWidth);
        SdfFrameBlock.Write(
            block: recording.PassBlock,
            frame: frame,
            height: height,
            lightMask: tables.LightMaskOf(frame: frame, view: view),
            tables: tables.PassValues,
            view: view,
            width: width
        );
        var temporal = m_owner.TemporalOf(
            instance: m_context.Instance, view: m_view, width: recording.FrameWidth, height: recording.FrameHeight, debug: tables.PassValues.DebugMode, temporal: m_temporal, unread: recording.UnreadFrames, renderWidth: recording.RenderWidth, renderHeight: recording.RenderHeight
        );

        if (m_part == SdfWorldPackage.Parts.Views) {
            m_sourceTaint = (SdfWorldPasses.SourceTaintOf(frame: frame, packageFrame: residency.PackageFrame, tables: tables) ??SdfWorldPasses.ReadTaintOf(latch: recording.Latch, reads: recording.Reads));
            m_readsHistory = (m_temporal && temporal.HasPreviousView);
        }

        Span<System.Numerics.Vector4> windows = stackalloc System.Numerics.Vector4[SdfWorldPackage.ScreenSourceCount];

        m_owner.ScreenWindowsOf(residency: residency, tables: tables, view: view, windows: windows);
        SdfFrameBlock.WriteScreenWindows(block: recording.PassBlock, windows: windows);
        SdfFrameBlock.WriteTemporal(block: recording.PassBlock, jitter: temporal.Jitter, historyFrames: temporal.Frames, temporal: m_temporal);
        SdfFrameBlock.WritePreviousView(block: recording.PassBlock, view: temporal.PreviousView, valid: temporal.HasPreviousView);

        if (m_readsRateHistory) {
            // The history planes follow the output extent. The visibility and probe cells follow the render grid.
            ShaderParameterWriter.WriteUInt32(recording.PassBlock, SdfFrameBlock.ViewsRate + 2 * sizeof(uint), recording.FrameWidth);
            ShaderParameterWriter.WriteUInt32(recording.PassBlock, SdfFrameBlock.ViewsRate + 3 * sizeof(uint), recording.FrameHeight);
        }
        SdfFrameBlock.WriteWorkCounterRow(
            block: recording.PassBlock,
            row: WorkCountersOf(recording: in recording).Row
        );
        SdfFrameBlock.WriteWorkCounterDetailRow(block: recording.PassBlock, row: recording.WorkDetailRow);

        if (m_pick is not null) {
            // Build ports carry the storage declaration; the recording carries the version this pass reads.
            m_pick.Prepare(slot: recording.Slot, width: width, height: height, frame: frame,
                visibility: recording.Inputs[InputIndexOf(member: SdfWorldPackage.VisibilityRecords)].Version,
                sample: new SdfReprojectionView(Camera: frame.Views[view].Camera, Jitter: temporal.Jitter, Width: width, Height: height),
                cut: frame.Views[view].CutRevision, rowStamp: m_owner.MeshRowsOf(instance: m_context.Instance));
            m_diagnosticPick?.Prepare(slot: recording.Slot, width: width, height: height, frame: frame,
                visibility: recording.Inputs[InputIndexOf(member: SdfWorldPackage.VisibilityRecords)].Version,
                sample: new SdfReprojectionView(Camera: frame.Views[view].Camera, Jitter: temporal.Jitter, Width: width, Height: height),
                cut: frame.Views[view].CutRevision, rowStamp: m_owner.MeshRowsOf(instance: m_context.Instance));
        }

        SdfFrameBlock.WriteMeshIrradiance(block: recording.PassBlock, volume: residency.MeshIrradianceParameters);
        // The world's shared cascades, each through the camera its layers were drawn from (SdfWorldShadows).
        SdfFrameBlock.WriteMeshShadows(block: recording.PassBlock, cascades: tables.CascadesOf(frame: frame, view: view));
        if (IsMesh) {
            m_owner.NoteDrawn(height: height, instance: m_context.Instance, part: m_part, width: width);
            m_decided.Occlude = (frame.MeshCull is SdfMeshCullLevel.Occlusion or SdfMeshCullLevel.Camera);
            m_decided.Cull = SdfWorldPasses.CullOf(frame: frame, height: height, view: view, width: width);
            // The glass pass's thickness is the sums of its own triangles' ray distances, so glass draws its own.
            m_decided.Detail = (IsGlass
                ? SdfMeshDetail.Finest
                : (frame.MeshDetail switch {
                    SdfMeshDetailLevel.Auto => SdfMeshDetail.Of(camera: frame.Views[view].Camera, displayHeight: recording.FrameHeight),
                    _ => SdfMeshDetail.Forced(level: (((int)frame.MeshDetail) - 1)),
                }));
        } else {
            DecideCompute(recording: in recording, tables: tables);
        }

        return RenderGraphPackageOutcome.Drew;
    }
    // Records what the frame's decision chose (Decide): only the recorder's own state, its own sets and the command buffer
    // change here, and the residency's staged tables and frame are only read, so the instance's passes emit side by side.
    public void Emit(in RenderGraphPackageRecording recording) {
        var tables = m_decided.Tables!;

        if (m_part == SdfWorldPackage.Parts.GlassClear) {
            if (m_decided.ClearEnd > m_decided.ClearStart) {
                recording.Recorder.ClearStorageBuffer(recording.CommandBuffer, m_decided.Sums!.BufferHandle, (m_decided.ClearEnd - m_decided.ClearStart), m_decided.ClearStart);
            }
            return;
        }
        if (IsMesh) {
            RecordMesh(
                cull: in m_decided.Cull,
                detail: in m_decided.Detail,
                occlude: m_decided.Occlude,
                recording: in recording,
                tables: tables
            );
        } else {
            RecordCompute(
                recording: in recording,
                tables: tables
            );
        }
    }
    public bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback) {
        // Each index takes the next prepared copy: the pick's, then the diagnostic pick's.
        readback = default;
        return ((m_pick is not null) && (m_pick.Take(readback: out readback, slot: slot) ||
            m_diagnosticPick!.Take(readback: out readback, slot: slot)));
    }

    public ulong ReadbackBytes => ((m_pick?.ReadbackBytes ?? 0UL) + (m_diagnosticPick?.ReadbackBytes ?? 0UL));

    public void Submitted(int slot) {
        m_shade.Submitted(slot: slot);
        m_pick?.Submitted(slot: slot);
        m_diagnosticPick?.Submitted(slot: slot);
        m_occlusion?.Submitted(slot: slot);
    }
    public void Completed(int slot) {
        m_shade.Completed(slot: slot);
        m_pick?.Completed(slot: slot);
        m_diagnosticPick?.Completed(slot: slot);
        m_occlusion?.Completed(slot: slot);
    }
    public void Abandoned(int slot) {
        if (m_notedMeshRows) { _ = m_owner.NoteMeshRows(instance: m_context.Instance, stamp: m_previousMeshRows); m_notedMeshRows = false; }
        m_shade.Abandoned(slot: slot);
        m_pick?.Abandoned(slot: slot);
        m_diagnosticPick?.Abandoned(slot: slot);
        m_occlusion?.Abandoned(slot: slot);
    }

    private int OutputIndexOf(string member) {
        for (var index = 0; (index < m_outputs.Length); index++) {
            if (WrittenMemberOf(version: m_outputs[index]) == member) {
                return index;
            }
        }
        throw new InvalidOperationException(message: $"Pass '{m_context.Pass}' has no output written through '{member}'.");
    }
    private int InputIndexOf(string member) {
        for (var index = 0; (index < m_inputs.Length); index++) {
            if (ReadMemberOf(version: m_inputs[index]) == member) {
                return index;
            }
        }
        throw new InvalidOperationException(message: $"Pass '{m_context.Pass}' has no input read through '{member}'.");
    }
    // The view follows the one the instance resolved this frame in place when its passes can record it
    // (SdfWorldPasses.CanFollow); one they cannot record rebuilds them instead. A counter change holds recording until
    // matching passes install. The ports and screens rebind for another residency's tables on this recording.
    private protected override void FollowedResidency() {
        foreach (var screens in m_screens) {
            Array.Clear(array: screens);
        }
    }
    // Decides a compute part's frame: the screens' images it samples (taking their leases), the views' and filter's
    // shading plans and the kernels they record with, the bins' committed plan in the pass block, and the tables' World
    // set, written now so the emission only binds it.
    private void DecideCompute(in RenderGraphPackageRecording recording, SdfWorldTables tables) {
        var residency = m_view.Residency;

        _ = residency.TakeScreenImages(images: m_screenImages, recording: in recording, shown: tables.ResidentScreens, tables: tables, view: m_view.View);
        switch (m_part) {
            case SdfWorldPackage.Parts.Views: {
                    // The views kernels are the residency's shading selection, pinned for this recording's submission. A
                    // binned graph's plan is the one whose arguments its bins pass last wrote (ShadePlanOf).
                    m_shadePlan = (m_binned
                        ? m_owner.ShadePlanOf(instance: m_context.Instance, frame: residency.Frame!, width: recording.Width, height: recording.Height, screens: tables.PassValues.ScreenCount)
                        : SdfShadeDispatch.For(previous: m_shadePlan, frame: residency.Frame!, width: recording.Width, height: recording.Height,
                            screens: tables.PassValues.ScreenCount, glass: false, tier: residency.ShadeTier));
                    m_decided.Selection = m_shade.RecordSelection(plan: m_shadePlan, residency: residency, slot: recording.Slot);
                    break;
                }
            case SdfWorldPackage.Parts.ViewsFilter: {
                    // The filter runs no program: one job of views' groups, whose pushed ordinal its entry remaps as views'
                    // does.
                    m_shadePlan = SdfShadeDispatch.ForEngine(previous: m_shadePlan, width: recording.Width, height: recording.Height);
                    break;
                }
            case SdfWorldPackage.Parts.ViewsBins: {
                    var plan = m_owner.CommitShadePlan(instance: m_context.Instance, frame: residency.Frame!, width: recording.Width, height: recording.Height, screens: tables.PassValues.ScreenCount);

                    SdfFrameBlock.WriteShadeBins(block: recording.PassBlock, plan: plan);
                    break;
                }
        }
        _ = tables.WorldSet(slot: tables.CurrentSlot);
    }
    // Binds the pass set and dispatches the part's kernel: the pyramid one invocation a texel of its first level, and the
    // surface and views passes one invocation a pixel of the render extent.
    private void RecordCompute(in RenderGraphPackageRecording recording, SdfWorldTables tables) {
        var slot = recording.Slot;
        var set = m_sets!.PassSet(slot: slot);
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;

        BindPorts(
            recording: in recording,
            set: set,
            tables: tables
        );
        m_history.Bind(
            layout: PassLayout,
            pass: m_context.Pass,
            recording: in recording,
            set: set,
            tables: tables
        );
        BindCounters(
            recording: in recording,
            set: set,
            tables: tables
        );
        BindScreens(
            recording: in recording,
            set: set,
            tables: tables
        );

        switch (m_part) {
            case SdfWorldPackage.Parts.Views: {
                    var selection = m_decided.Selection;

                    if (!m_shadePlan!.Binned) {
                        var pipeline = selection.Pipeline(kernel: SdfKernel.Views);

                        BindCompute(pipeline: pipeline, recording: in recording, tables: tables);
                        m_shadePlan.Record(pipeline: pipeline, recording: in recording);
                        return;
                    }
                    // Bin zero's tiles hold no program, so the plain kernel shades them; every other bin's take the
                    // selection's kernel, which shades every program the frame runs.
                    for (var bin = 0; (bin < m_shadePlan.Bins.Count); bin++) {
                        if (m_shadePlan.Bins[bin].Jobs == 0) { continue; }
                        var pipeline = ((bin == 0) ? selection.Plain(kernel: SdfKernel.ViewsShade) : selection.Pipeline(kernel: SdfKernel.ViewsShade));

                        BindCompute(pipeline: pipeline, recording: in recording, tables: tables);
                        m_shadePlan.RecordBin(bin: bin, pipeline: pipeline, recording: in recording);
                    }
                    return;
                }
            case SdfWorldPackage.Parts.ViewsFilter: {
                    // The bins' counts start from zero before any group adds to them.
                    var bins = recording.Outputs[OutputIndexOf(member: SdfWorldPackage.ShadeTilesWritten)].Buffer!;
                    var pipeline = tables.Pipeline(kernel: SdfKernel.ViewsFilter);

                    recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: bins.BufferHandle,
                        sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                        destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
                    recorder.ClearStorageBuffer(commandBuffer, bins.BufferHandle, bins.SizeBytes);
                    recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: bins.BufferHandle,
                        sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                        destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
                    BindCompute(pipeline: pipeline, recording: in recording, tables: tables);
                    m_shadePlan!.Record(pipeline: pipeline, recording: in recording);
                    return;
                }
            case SdfWorldPackage.Parts.ViewsBins: {
                    // Step zero (one group) turns the counts into offsets and writes every job's arguments at the plan's
                    // widths (the pass block the decision wrote); step one (an invocation a tile) scatters the tiles once
                    // every offset is written.
                    var bins = recording.Outputs[OutputIndexOf(member: SdfWorldPackage.ShadeTilesWritten)].Buffer!;
                    var pipeline = tables.Pipeline(kernel: SdfKernel.ViewsBins);
                    var tiles = (recording.Width.CeilingDivide(divisor: SdfVisibility.GroupEdgePixels) * recording.Height.CeilingDivide(divisor: SdfVisibility.GroupEdgePixels));
                    Span<byte> step = stackalloc byte[sizeof(uint)];

                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: step, value: 0u);
                    BindCompute(pipeline: pipeline, recording: in recording, tables: tables);
                    recorder.PushConstants(bindPoint: GpuBindPoint.Compute, commandBufferHandle: commandBuffer, data: step, offset: 0,
                        pipelineLayoutHandle: pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
                    recorder.Dispatch(commandBufferHandle: commandBuffer, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
                    recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: bins.BufferHandle,
                        sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                        destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: step, value: 1u);
                    recorder.PushConstants(bindPoint: GpuBindPoint.Compute, commandBufferHandle: commandBuffer, data: step, offset: 0,
                        pipelineLayoutHandle: pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
                    recorder.Dispatch(commandBufferHandle: commandBuffer, groupCountX: Math.Clamp(tiles.CeilingDivide(divisor: 64u), 1u, 65535u), groupCountY: 1, groupCountZ: 1);
                    return;
                }
            default: {
                    var pipeline = tables.Pipeline(kernel: m_part switch {
                        SdfWorldPackage.Parts.Pyramid => SdfKernel.MeshPyramid,
                        SdfWorldPackage.Parts.ViewsProbes => SdfKernel.ViewsProbes,
                        _ => SdfKernel.Surface,
                    });

                    var (x, y) = m_part switch {
                        // One invocation a texel of the pyramid's first level, half the extent rounded up.
                        SdfWorldPackage.Parts.Pyramid => (recording.Width.CeilingDivide(divisor: 2u).CeilingDivide(divisor: 8u), recording.Height.CeilingDivide(divisor: 2u).CeilingDivide(divisor: 8u)),
                        // One invocation a cell of the probe lookup, the extent over its rate rounded up.
                        SdfWorldPackage.Parts.ViewsProbes => (recording.Width.CeilingDivide(divisor: SdfWorldPackage.ViewsProbeRate).CeilingDivide(divisor: SdfVisibility.GroupEdgePixels), recording.Height.CeilingDivide(divisor: SdfWorldPackage.ViewsProbeRate).CeilingDivide(divisor: SdfVisibility.GroupEdgePixels)),
                        _ => (recording.Width.CeilingDivide(divisor: SdfVisibility.GroupEdgePixels), recording.Height.CeilingDivide(divisor: SdfVisibility.GroupEdgePixels)),
                    };

                    if (m_part == SdfWorldPackage.Parts.ViewsProbes) {
                        // The pool's count of extra slots starts from zero before any group takes one; it follows the
                        // records' planes (sdfViewsProbePoolWord in sdf-views-probes.hlsli).
                        var records = recording.Outputs[OutputIndexOf(member: SdfWorldPackage.ViewsProbeRecordsWritten)].Buffer!;
                        var count = (((records.SizeBytes / sizeof(uint)) / SdfWorldPackage.ViewsProbeWords) * SdfWorldPackage.ViewsProbeRecordWords);

                        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: records.BufferHandle,
                            sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                            destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
                        recorder.ClearStorageBuffer(commandBuffer, records.BufferHandle, sizeof(uint), (count * sizeof(uint)));
                        recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: records.BufferHandle,
                            sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
                    }
                    BindCompute(pipeline: pipeline, recording: in recording, tables: tables);
                    recorder.Dispatch(
                        commandBufferHandle: commandBuffer,
                        groupCountX: x,
                        groupCountY: y,
                        groupCountZ: 1
                    );
                    return;
                }
        }
    }
    // Binds a compute kernel with the slot's pass set and the residency's World set. A pipeline bind can drop the bound
    // sets (Direct3D 12 sets the kernel's root signature again), so the sets follow every bind.
    private void BindCompute(IGpuComputePipeline pipeline, in RenderGraphPackageRecording recording, SdfWorldTables tables) {
        recording.Recorder.BindPipeline(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: recording.CommandBuffer,
            pipelineHandle: pipeline.Handle
        );
        m_sets!.Bind(
            bindPoint: GpuBindPoint.Compute,
            commandBuffer: recording.CommandBuffer,
            pipelineLayout: pipeline.LayoutHandle,
            recorder: recording.Recorder,
            slot: recording.Slot
        );
        recording.Recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: recording.CommandBuffer,
            descriptorSetHandle: tables.WorldSet(slot: tables.CurrentSlot),
            group: ((uint)ShaderInterfaceGroup.World),
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
    }
    // The mesh set a frame slot draws through while the tables upload into their current ring slot. The tables' ring and
    // the frame slots' turn at different lengths, so a set per pair keeps each set's buffers those of one ring slot, and
    // a set never rewrites what it binds once written.
    private static int MeshSetOf(int slot, SdfWorldTables tables) =>
        ((slot * SdfWorldTables.FrameRingSize) + tables.CurrentSlot);
    // Selects draws and levels on the GPU, then draws one indirect phase (two for opaque occlusion), pulling triangles
    // from the shared mesh region. A frame with no draws records the render pass alone, which clears the target.
    private void RecordMesh(in RenderGraphPackageRecording recording, SdfWorldTables tables, in SdfMeshCull cull, in SdfMeshDetail detail, bool occlude) {
        var count = tables.MeshDrawCount;
        var slot = recording.Slot;
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var pipeline = (IsGlass ? tables.GlassPipeline : tables.MeshPipeline);
        var meshSet = MeshSetOf(slot: slot, tables: tables);
        var target = recording.Outputs[0].Owned!;
        var framebuffer = m_framebuffers[0];
        var lateFramebuffer = ((m_lateFramebuffers.Length > 0) ? m_lateFramebuffers[0] : null);
        var occlusion = ((count > 0) ? m_occlusion : null);
        var twoPhase = (occlude && !IsGlass);
        var targetIndex = TargetIndexOf(target: target);

        if (targetIndex >= 0) {
            framebuffer = m_framebuffers[targetIndex];
            lateFramebuffer = ((m_lateFramebuffers.Length > 0) ? m_lateFramebuffers[targetIndex] : null);
            m_recordedTarget = (targetIndex, (PartDrawCount(tables: tables) == 0));
        }

        // A frame holding none of the part's draws certifies its target clear (m_recordedTarget), so later empty frames
        // skip: it clears the whole allocation, since the render grid can later grow within it and a render pass clears
        // only its area.
        var area = ((PartDrawCount(tables: tables) == 0)
            ? new GpuPixelRect(Height: target.Height, Width: target.Width, X: 0, Y: 0)
            : new GpuPixelRect(Height: recording.Height, Width: recording.Width, X: 0, Y: 0));

        SelectDraws(cull: in cull, detail: in detail, meshSet: meshSet, occlusion: occlusion, recording: in recording, tables: tables, target: target, twoPhase: twoPhase);

        var set = m_meshSets[meshSet];

        recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: framebuffer);
        if (occlusion is not null) {
            // The first phase: what the previous frame found visible, and every draw no host box bounds.
            RecordMeshPhase(commandBuffer: commandBuffer, phase: 0, pipeline: pipeline, recorder: recorder, selection: occlusion, set: set);
        }
        recorder.EndRenderPass(commandBufferHandle: commandBuffer);

        if (twoPhase && (occlusion is not null) && (occlusion.CandidateCount > 0)) {
            occlusion.RecordLate(commandBuffer: commandBuffer, pipeline: tables.OcclusionPipeline, recorder: recorder, set: m_occlusionSets[((meshSet * OcclusionSetsPerMesh) + 1)], target: target.ImageHandle);
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: lateFramebuffer!);
            RecordMeshPhase(commandBuffer: commandBuffer, phase: 1, pipeline: pipeline, recorder: recorder, selection: occlusion, set: set);
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
        }
    }
    // Writes a mesh set's tables and counters, stages the selection's draws into it and records the selection's first
    // step: the set and its two selection sets are one group's (meshSet), and `target` is the image the selection's second
    // phase samples.
    private void SelectDraws(in RenderGraphPackageRecording recording, SdfWorldTables tables, SdfMeshOcclusion? occlusion, int meshSet, IGpuImage target, in SdfMeshCull cull,
        in SdfMeshDetail detail, bool twoPhase) {
        if (tables.MeshDrawCount == 0) {
            return;
        }

        var set = m_meshSets[meshSet];

        tables.WriteMeshTables(
            bound: m_meshSetBuffers[meshSet],
            set: set,
            slot: tables.CurrentSlot
        );
        tables.WriteMeshWorkCounters(
            bound: ref m_meshSetBuffers[meshSet][MeshSetCounters],
            counters: WorkCountersOf(recording: in recording).Buffer,
            set: set
        );
        // The glass pass reads each pixel's opaque hit, its one input, and accumulates into the glass sums, its
        // third output; the mesh pass binds the dummy at both.
        tables.WriteMeshBuffer(
            bound: ref m_meshSetBuffers[meshSet][MeshSetVisibility],
            buffer: (IsGlass ? recording.Inputs[0].Buffer! : tables.DummyBuffer),
            member: SdfWorldPackage.VisibilityRecords,
            set: set
        );
        tables.WriteMeshBuffer(
            bound: ref m_meshSetBuffers[meshSet][MeshSetGlassSums],
            buffer: (IsGlass ? recording.Outputs[2].Buffer! : tables.DummyBuffer),
            member: SdfWorldPackage.GlassSumsWritten,
            set: set
        );

        // The raster set binds dummies at the compute-only members and reads the command identity alias. The
        // selection kernel's own sets bind its writable state and each phase's sampled target.
        SdfMeshOcclusion.Unbind(bound: m_meshSetOcclusion[meshSet], boundTarget: ref m_meshSetTargets[meshSet], first: 0, set: set, tables: tables);

        if (occlusion is null) {
            return;
        }

        var frame = m_view.Residency.Frame!;
        var view = frame.Views[Math.Min(val1: m_view.View, val2: (frame.Views.Count - 1))];

        occlusion.Stage(tables: tables, count: tables.MeshDrawCount, height: recording.RenderHeight, width: recording.RenderWidth, slot: recording.Slot, cull: in cull, detail: in detail, held: tables.HoldsFrame, glass: IsGlass, occlude: twoPhase,
            audience: view.Audience, camera: view.Camera);
        occlusion.BindDraws(tables: tables, set: set, bound: m_meshSetOcclusion[meshSet]);
        for (var phase = 0; (phase < OcclusionSetsPerMesh); phase++) {
            var index = ((meshSet * OcclusionSetsPerMesh) + phase);
            var occlusionSet = m_occlusionSets[index];
            var bound = m_occlusionSetBuffers[index];

            tables.WriteMeshTables(bound: bound, set: occlusionSet, slot: tables.CurrentSlot);
            tables.WriteMeshWorkCounters(bound: ref bound[MeshSetCounters], counters: WorkCountersOf(recording: in recording).Buffer, set: occlusionSet);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetVisibility], buffer: tables.DummyBuffer, member: SdfWorldPackage.VisibilityRecords, set: occlusionSet);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetGlassSums], buffer: tables.DummyBuffer, member: SdfWorldPackage.GlassSumsWritten, set: occlusionSet);
            occlusion.Bind(bound: bound, boundTarget: ref m_occlusionSetTargets[index], first: MeshSetBuffers, set: occlusionSet, tables: tables,
                target: ((twoPhase && (phase != 0)) ? target.ImageViewHandle : tables.SampledFiller.ImageViewHandle));
        }
        occlusion.RecordEarly(commandBuffer: recording.CommandBuffer, pipeline: tables.OcclusionPipeline, recorder: recording.Recorder, set: m_occlusionSets[(meshSet * OcclusionSetsPerMesh)]);
    }
    // Binds one raster phase and consumes its existing counted arguments.
    private static void RecordMeshPhase(IGpuPipeline pipeline, IGpuRecorder recorder, nint commandBuffer, nint set, SdfMeshOcclusion selection, int phase) {
        recorder.BindPipeline(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: commandBuffer,
            pipelineHandle: pipeline.Handle
        );
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: set,
            group: ((uint)ShaderInterfaceGroup.Pass),
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
        selection.RecordDraws(commandBuffer: commandBuffer, phase: phase, pipeline: pipeline, recorder: recorder);
    }
    private void BindCounters(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var counters = WorkCountersOf(recording: in recording).Buffer;

        if (ReferenceEquals(objA: m_boundCounters[recording.Slot], objB: counters)) {
            return;
        }

        tables.WriteInterfaceBuffer(buffer: counters, layout: PassLayout, member: ShaderWorkCounters.Buffer, set: set);
        m_boundCounters[recording.Slot] = counters;
    }
    // Writes, once per slot, every storage the pass's ports bind at the member its access reads or writes it through, the
    // world's shared shadow layers (SdfWorldShadows) at theirs, and the tables' dummy and fillers at every member nothing
    // binds. The storages a slot resolves stay the instance's for the recorder's life; the shadow layers are written again
    // whenever the shadows allocate anew or the view reads another anchor (SdfWorldShadows.BindingRevisionOf).
    private void BindPorts(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var slot = recording.Slot;
        var shadows = tables.Shadows;
        var view = m_view.View;
        var shadowRevision = (shadows?.BindingRevisionOf(view: view) ?? -1L);

        if (ReferenceEquals(objA: m_portTables[slot], objB: tables) && (m_portShadows[slot] == shadowRevision)) {
            return;
        }

        var bindings = tables.Bindings;
        var layout = PassLayout;
        var output = tables.StorageFiller.ImageViewHandle;
        var meshVisibility = tables.SampledFiller.ImageViewHandle;

        foreach (var member in ScratchMembers) {
            tables.WriteInterfaceBuffer(buffer: tables.DummyBuffer, layout: layout, member: member, set: set);
        }

        for (var port = 0; (port < m_inputs.Length); port++) {
            var name = m_inputs[port];
            var bound = recording.Inputs[port];

            if (bound.Buffer is { } buffer) {
                if (!SdfHistoryPorts.IsHistory(version: name) && (ReadMemberOf(version: name) is { } member)) {
                    tables.WriteInterfaceBuffer(buffer: buffer, layout: layout, member: member, set: set);
                }
            } else if (string.Equals(
                a: name,
                b: SdfWorldPackage.Parts.MeshTarget,
                comparisonType: StringComparison.Ordinal
            )) {
                meshVisibility = bound.ReadImage(latch: recording.Latch).ImageViewHandle;
            }
        }
        for (var port = 0; (port < m_outputs.Length); port++) {
            var name = m_outputs[port];
            var bound = recording.Outputs[port];

            if (bound.Buffer is { } buffer) {
                if (!SdfHistoryPorts.IsHistory(version: name) && (WrittenMemberOf(version: name) is { } member)) {
                    tables.WriteInterfaceBuffer(buffer: buffer, layout: layout, member: member, set: set);
                }
            } else if (bound.Kind == ShaderPipelineResourceKind.Image) {
                output = bound.ReadImage(latch: recording.Latch).ImageViewHandle;
            }
        }
        foreach (var (entry, member) in OpticalBindings) {
            if (shadows?.OpticalOf(view: view, entry: entry) is { } optical) {
                tables.WriteInterfaceBuffer(buffer: optical, layout: layout, member: member, set: set);
            }
        }
        foreach (var (entry, member) in BlocksBindings) {
            if (shadows?.BlocksOf(view: view, entry: entry) is { } blocks) {
                tables.WriteInterfaceBuffer(buffer: blocks, layout: layout, member: member, set: set);
            }
        }

        m_boundCounters[slot] = null;
        m_history.Forget(slot: slot);
        m_portTables[slot] = tables;
        m_portShadows[slot] = shadowRevision;

        bindings.WriteStorageImage(
            arrayElement: 0,
            binding: OutputBinding,
            descriptorSetHandle: set,
            imageViewHandle: output
        );
        bindings.WriteSampledImage(
            arrayElement: 0,
            binding: MeshVisibilityBinding,
            descriptorSetHandle: set,
            imageViewHandle: meshVisibility
        );
        foreach (var (entry, exit, binding) in CascadeBindings) {
            bindings.WriteSampledImage(arrayElement: 0, binding: binding, descriptorSetHandle: set,
                imageViewHandle: (shadows?.ViewOf(view: view, entry: entry, exit: exit, filler: tables.SampledFiller.ImageViewHandle) ?? tables.SampledFiller.ImageViewHandle));
        }
        Array.Clear(array: m_screens[slot]);
    }
    // Writes each declared screen's image, which the decision took (DecideCompute), into the slot's pass set.
    private void BindScreens(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) =>
        SdfWorldResidency.WriteScreenImages(images: m_screenImages, bindings: tables.Bindings,
            binding: ScreenSourcesBinding, set: set, filler: tables.SampledFiller.ImageViewHandle, bound: m_screens[recording.Slot]);
    // Where a part counts its march steps and texels written: every one counts
    // (RenderGraphFragmentPass.CountsKernelWork), so its node always hands it a row.
    private GpuKernelCounterRow WorkCountersOf(in RenderGraphPackageRecording recording) =>
        (recording.WorkCounters ?? throw new InvalidOperationException(message: $"Pass '{m_context.Pass}' counts its kernels' work, but its recording carries no work counters."));
    // The member a pass reads a fragment buffer through, or null for one it reads through no member.
    private static string? ReadMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.Visibility => SdfWorldPackage.VisibilityRecords,
        SdfWorldPackage.Parts.MeshPyramid => SdfWorldPackage.MeshPyramid,
        SdfWorldPackage.Parts.Filter => SdfWorldPackage.ViewsFilterRecords,
        SdfWorldPackage.Parts.Probes => SdfWorldPackage.ViewsProbeRecords,
        SdfWorldPackage.Parts.ShadeTiles or SdfWorldPackage.Parts.ShadeBinned => SdfWorldPackage.ShadeTiles,
        _ => null,
    };
    // The member a pass writes a fragment buffer through, or null for one it writes through no member.
    private static string? WrittenMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.Visibility => SdfWorldPackage.VisibilityRecordsWritten,
        SdfWorldPackage.Parts.Reactivity => SdfWorldPackage.ReactivityWritten,
        SdfWorldPackage.Parts.MeshPyramid => SdfWorldPackage.MeshPyramidWritten,
        SdfWorldPackage.Parts.Filter => SdfWorldPackage.ViewsFilterRecordsWritten,
        SdfWorldPackage.Parts.Probes => SdfWorldPackage.ViewsProbeRecordsWritten,
        SdfWorldPackage.Parts.ShadeTiles or SdfWorldPackage.Parts.ShadeBinned => SdfWorldPackage.ShadeTilesWritten,
        SdfWorldPackage.Parts.ShadeArguments => SdfWorldPackage.ShadeArgumentsWritten,
        _ => null,
    };
}
