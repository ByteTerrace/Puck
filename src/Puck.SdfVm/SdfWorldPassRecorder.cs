using Puck.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Maths;

namespace Puck.SdfVm;

// One pass of an sdf.world instance (SdfWorldPasses): a part of the package's fragment, recorded into the instance's
// command buffer for the pass. Every part writes its pass block (SdfFrameBlock): the view's camera, the frame's levers,
// light count and curvature shading, the shadow cascades, the irradiance volume and the world values. Every compute part
// binds the residency's World set of the ring slot the frame's upload wrote, which holds its tables (the lights and the
// sky among them), and the world interface's pass group: the fragment storages its ports bind and, at every member its
// ports do not, a dummy of the residency's; the node's work counters for the frame slot, whose row it writes into its
// pass block; and the screens, whose host images are rewritten every frame. The mesh part draws the frame's mesh draws into
// its target through the mesh pipeline and each shadow cascade into its depth alone through the shadow pipeline, each
// with a set of its own per frame slot binding its pass block. A recorder records no barrier on the graph's resources:
// the planner's are the instance's, and the node's orders the work counters. The one exception is the mesh part's
// occlusion (SdfMeshOcclusion), which orders its own buffers and moves the mesh target between its two phases, leaving it
// in its attachment layout as the planner expects.
internal sealed class SdfWorldPassRecorder : IRenderGraphPackageRecorder, IRenderGraphPackageReadback {
    // Views attribute analytic sky lighting by layer.
    public IReadOnlyList<string> WorkDetails(in FrameContext context) => ((m_part == SdfWorldPackage.Parts.Views) ? m_view.Residency.SkyDetails.Labels : []);

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
    ];
    // Each cascade layer's storage and the binding the surface pass samples it at.
    private static readonly (string Version, uint Binding)[] CascadeBindings = [
        (SdfWorldPackage.Parts.ShadowNearDepth, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowNear)),
        (SdfWorldPackage.Parts.ShadowFarDepth, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowFar)),
        (SdfWorldPackage.Parts.ShadowSecondDepth, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowSecond)),
        (SdfWorldPackage.Parts.ShadowNearExit, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowNearExit)),
        (SdfWorldPackage.Parts.ShadowFarExit, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowFarExit)),
        (SdfWorldPackage.Parts.ShadowSecondExit, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowSecondExit)),
    ];
    private static readonly uint OutputBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.Output);
    private static readonly uint MeshVisibilityBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshVisibility);
    private static readonly uint ScreenSourcesBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.ScreenSources);

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly string[] m_inputs;
    private readonly string[] m_outputs;
    private readonly SdfWorldPasses m_owner;
    private readonly string m_part;
    // Whether the pass belongs to the temporal fragment (SdfWorldPackage.TemporalFragment).
    private readonly bool m_temporal;

    private readonly bool m_resolved;

    // The view the pass records, followed in place when the instance resolves another its passes can record
    // (SdfWorldPasses.CanFollow); one they cannot record rebuilds them instead.
    private SdfWorldView m_view;

    // Which screen indices the residency binds.
    private readonly bool[] m_declaredScreens = new bool[SdfWorldTables.MaxScreenSurfaces];

    // A compute part's frame and pass sets, one of each per frame slot.
    private readonly RenderGraphPackageSets? m_sets;
    // Per frame slot: the tables the pass set's ports and dummies were written for, and each screen element's last written
    // view.
    private readonly SdfWorldTables?[] m_portTables;
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

    private const int MeshSetBuffers = 7;
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

    // The framebuffers a raster part's second render pass draws in, one per instance of its target: the mesh part's
    // occlusion phase over the target and depth, a cascade's exit layer over its second output.
    private readonly IGpuFramebuffer[] m_lateFramebuffers = [];
    private readonly IGpuFramebuffer[] m_shadowGlassFramebuffers = [];
    private readonly IGpuBuffer?[][] m_meshSetOcclusion = [];
    private readonly nint[] m_meshSetTargets = [];
    // Early and late compute sets beside each mesh set. Early binds a sampled filler while the target is an attachment;
    // late binds the target that RecordLate transitions for sampling. A descriptor remains valid even on a branch that
    // does not read it, and rewriting one shared set would change both recorded dispatches.
    private readonly nint[] m_occlusionSets = [];
    private readonly IGpuBuffer?[][] m_occlusionSetBuffers = [];
    private readonly nint[] m_occlusionSetTargets = [];
    // Whether each instance of the mesh part's or a cascade's target has been drawn or cleared once.
    private readonly bool[] m_targetsWritten = [];

    private bool m_disposed;
    // What made the latest views render read external content, or null when nothing did.
    private string? m_sourceTaint;
    private bool m_readsHistory;

    private readonly SdfWorldPickReadback? m_pick;
    private readonly SdfWorldPickReadback? m_diagnosticPick;

    public SdfWorldPassRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) {
        m_context = context;
        m_owner = owner;
        m_view = view;
        m_part = (context.Part ?? throw new ArgumentException(message: $"Pass '{context.Pass}' runs no part of '{RenderGraphPackageCatalog.SdfWorld}'.", paramName: nameof(context)));
        // Port declarations are captured by graph planning, before the asynchronous build. The live frame may already
        // request another fade capacity when this recorder installs or follows a view.
        var prefix = context.Pass[..^m_part.Length];

        string LocalName(ShaderPipelineResource resource) => (resource.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix)
            ? resource.Name[prefix.Length..] : resource.Name);

        m_inputs = [.. context.Inputs.Select(selector: LocalName)];
        m_outputs = [.. context.Outputs.Select(selector: LocalName)];
        var fragment = owner.FragmentOf(instance: context.Instance)!;

        m_temporal = fragment.Resources.Any(predicate: static resource => resource.History);
        m_resolved = m_outputs.Contains(value: SdfWorldPackage.CurrentColor);

        DeclareScreens(residency: view.Residency);

        var slots = context.InFlightFrames;
        var tables = (view.Residency.Tables ?? throw new InvalidOperationException(message: $"Residency '{view.Residency.Name}' has no tables for pass '{context.Pass}'."));

        m_portTables = new SdfWorldTables?[slots];
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

        // The mesh part draws through the mesh interface's layout, whose one pass group holds the pass block the world
        // interface lays out and the mesh region: a set per frame slot and tables ring slot from a pool of its own, its block
        // bound to the frame slot's pass block buffer, and a framebuffer over each instance of its target and depth, or of a
        // cascade's depth alone.
        var bindings = context.Services.Bindings;
        var sizes = default(GpuDescriptorPoolSizes);
        var sets = (slots * SdfWorldTables.FrameRingSize * (IsShadowCascade ? 2 : 1));

        var occludes = IsMesh;

        for (var set = 0; (set < sets); set++) {
            sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.Mesh.Groups);

            if (occludes) {
                for (var phase = 0; (phase < OcclusionSetsPerMesh); phase++) {
                    sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.MeshOcclusion.Groups);
                }
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
            var occlusionSets = (occludes ? (sets * OcclusionSetsPerMesh) : 0);

            m_occlusionSets = new nint[occlusionSets];
            m_occlusionSetBuffers = new IGpuBuffer?[occlusionSets][];
            m_occlusionSetTargets = new nint[occlusionSets];

            for (var set = 0; (set < sets); set++) {
                var slot = ((set % (slots * SdfWorldTables.FrameRingSize)) / SdfWorldTables.FrameRingSize);

                m_meshSetBuffers[set] = new IGpuBuffer?[MeshSetBuffers];
                m_meshSetOcclusion[set] = new IGpuBuffer?[MeshSetOcclusionBuffers];

                if (occludes) {
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

            for (var index = 0; (index < m_framebuffers.Length); index++) {
                m_framebuffers[index] = (IsShadowCascade
                    ? context.Services.RenderPassFactory.CreateFramebuffer(
                        colors: [],
                        depth: m_meshTargets[index],
                        renderPass: tables.ShadowRenderPass
                    )
                    : context.Services.RenderPassFactory.CreateFramebuffer(
                        colors: [m_meshTargets[index]],
                        depth: groups.OutputImages[1][index],
                        renderPass: tables.MeshRenderPass
                    ));
            }

            if (IsShadowCascade) {
                m_lateFramebuffers = new IGpuFramebuffer[m_meshTargets.Count];
                m_shadowGlassFramebuffers = new IGpuFramebuffer[m_meshTargets.Count];
                m_lateRenderPass = context.Services.RenderPassFactory.Create(
                    description: SdfMeshRasterPass.ShadowRenderPass with { Depth = SdfWorldPackage.MeshShadowDepthAttachment with { Load = GpuAttachmentLoad.Load } },
                    name: new GpuObjectName(owner: context.Instance, part: context.Pass, detail: "glass extinction"));

                for (var index = 0; (index < m_lateFramebuffers.Length); index++) {
                    m_lateFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(
                        colors: [],
                        depth: groups.OutputImages[1][index],
                        renderPass: tables.ShadowRenderPass
                    );
                    m_shadowGlassFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(
                        colors: [], depth: groups.OutputImages[1][index], renderPass: m_lateRenderPass);
                }
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
            m_occlusion = new SdfMeshOcclusion(buffers: context.Services.BufferFactory, owner: context.Instance, part: context.Pass, slots: slots,
                sight: m_part == SdfWorldPackage.Parts.Mesh ? owner.PortalSightOf(context.Instance) : null);
        } catch {
            foreach (var framebuffer in m_framebuffers) {
                framebuffer?.Dispose();
            }
            foreach (var framebuffer in m_lateFramebuffers) {
                framebuffer?.Dispose();
            }

            foreach (var framebuffer in m_shadowGlassFramebuffers) { framebuffer?.Dispose(); }
            m_lateRenderPass?.Dispose();

            bindings.DestroyPool(poolHandle: m_meshPool);

            throw;
        }
    }

    // Whether the part rasterizes the frame's mesh draws through the mesh pipeline: the mesh pass, one of the shadow
    // cascades, which draw from the cascade's light camera, or the glass pass, which draws the glass draws alone.
    private bool IsMesh => ((m_part is SdfWorldPackage.Parts.Mesh or SdfWorldPackage.Parts.Glass) || IsShadowCascade);
    private bool IsGlass => (m_part is SdfWorldPackage.Parts.Glass);
    // The interface every compute part's pass set is laid out by.
    private static ShaderInterfaceLayout PassLayout => SdfWorldInterfaces.WorldLayout;
    private bool IsShadowCascade => (m_part is SdfWorldPackage.Parts.ShadowNear or SdfWorldPackage.Parts.ShadowFar or SdfWorldPackage.Parts.ShadowSecond);
    private bool TargetsWritten => Array.TrueForAll(array: m_targetsWritten, match: static written => written);

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

        foreach (var framebuffer in m_shadowGlassFramebuffers) { framebuffer?.Dispose(); }
        m_lateRenderPass?.Dispose();
        m_occlusion?.Dispose();

        if (m_meshPool != 0) {
            m_context.Services.Bindings.DestroyPool(poolHandle: m_meshPool);
        }

        m_owner.Unhold(residency: m_view.Residency);
        m_view.Residency.Release();
    }
    // A part skips a frame whose work it would not do, recording neither its work nor its planned barriers: the mesh part
    // a frame that draws no mesh, and a shadow cascade a frame with no draw or no cascade of its own. A frame that draws no
    // mesh still records while an instance of the part's target has never been written, as before the first bake lands:
    // its render pass clears the target the surface pass samples, which would otherwise hold no contents at all. No part
    // walks the field: the bake samples it as the residency's background GPU work (SdfMeshBakeGpuWork).
    public bool Skips(in FrameContext context) {
        switch (m_part) {
            case SdfWorldPackage.Parts.Mesh: {
                    Follow();
                    m_owner.Begin(residency: m_view.Residency);
                    return ((m_view.Residency.Staged(context: in context).OpaqueDrawCount == 0) && TargetsWritten);
                }
            // The glass pass skips a frame with no glass draw once its target holds a clear; the clear and the shading
            // pass never skip, since the shading pass writes the lit image every pass after it reads.
            case SdfWorldPackage.Parts.Glass: {
                    Follow();
                    m_owner.Begin(residency: m_view.Residency);
                    return ((m_view.Residency.Staged(context: in context).GlassDrawCount == 0) && TargetsWritten);
                }
            // The surface pass reads the pyramid only for a pixel the mesh covers, which a frame with no draws has none of.
            case SdfWorldPackage.Parts.Pyramid: {
                    Follow();
                    m_owner.Begin(residency: m_view.Residency);
                    return (m_view.Residency.Staged(context: in context).MeshDrawCount == 0);
                }
            case SdfWorldPackage.Parts.ShadowNear:
            case SdfWorldPackage.Parts.ShadowFar:
            case SdfWorldPackage.Parts.ShadowSecond: {
                    Follow();
                    m_owner.Begin(residency: m_view.Residency);
                    var tables = m_view.Residency.Staged(context: in context);
                    var cascades = SdfMeshShadowCascades.Fit(frame: m_view.Residency.Frame!, view: m_view.View);

                    return ((cascades.Count == 0) || ((tables.OpaqueDrawCount == 0) && TargetsWritten) ||
                        ((m_part == SdfWorldPackage.Parts.ShadowSecond) && (cascades.SecondPenumbraSlope <= 0f)));
                }
            default:
                return false;
        }
    }
    public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) =>
        m_owner.SignatureOf(instance: m_context.Instance, part: m_part, temporal: m_temporal, context: in context);
    public void Submitted() {
        if (m_part == SdfWorldPackage.Parts.Views) {
            m_owner.SubmittedTaint(m_context.Instance, m_sourceTaint, m_readsHistory);
        }
        if ((m_part == SdfWorldPackage.Parts.Views) && !m_resolved) { m_owner.MarkSampleRendered(instance: m_context.Instance); }
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        Follow();

        var residency = m_view.Residency;

        m_owner.Begin(residency: residency);

        var tables = residency.Staged(context: recording.Context);

        // The glass clear zeroes the glass sums, its one output, ahead of the glass pass's accumulation.
        if (m_part == SdfWorldPackage.Parts.GlassClear) {
            var sums = recording.Outputs[0].Buffer!;

            recording.Recorder.ClearStorageBuffer(recording.CommandBuffer, sums.BufferHandle, sums.SizeBytes);

            return RenderGraphPackageOutcome.Drew;
        }
        var frame = residency.Frame!;
        var view = Math.Min(
            val1: m_view.View,
            val2: (frame.Views.Count - 1)
        );
        var width = recording.Width;
        var height = recording.Height;

        residency.RequestExtent(height: recording.FrameHeight, width: recording.FrameWidth);
        SdfFrameBlock.Write(
            block: recording.PassBlock,
            frame: frame,
            height: height,
            tables: tables.PassValues,
            view: view,
            width: width
        );
        // A shadow cascade can record first; history always describes the camera's sample grid, not that pass's target.
        var temporal = m_owner.TemporalOf(
            instance: m_context.Instance, view: m_view, width: recording.FrameWidth, height: recording.FrameHeight, debug: tables.PassValues.DebugMode, temporal: m_temporal, unread: recording.UnreadFrames, renderWidth: recording.RenderWidth, renderHeight: recording.RenderHeight
        );

        if (m_part == SdfWorldPackage.Parts.Views) {
            m_sourceTaint = (SdfWorldPasses.SourceTaintOf(frame: frame, tables: tables) ?? SdfWorldPasses.ReadTaintOf(reads: recording.Reads));
            m_readsHistory = (m_temporal && temporal.HasPreviousView);
        }

        SdfFrameBlock.WriteTemporal(block: recording.PassBlock, jitter: temporal.Jitter, historyFrames: temporal.Frames, temporal: m_temporal);
        SdfFrameBlock.WritePreviousView(block: recording.PassBlock, view: temporal.PreviousView, valid: temporal.HasPreviousView);

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
                cut: frame.Views[view].CutRevision);
            m_diagnosticPick?.Prepare(slot: recording.Slot, width: width, height: height, frame: frame,
                visibility: recording.Inputs[InputIndexOf(member: SdfWorldPackage.VisibilityRecords)].Version,
                sample: new SdfReprojectionView(Camera: frame.Views[view].Camera, Jitter: temporal.Jitter, Width: width, Height: height),
                cut: frame.Views[view].CutRevision);
        }

        SdfFrameBlock.WriteMeshIrradiance(block: recording.PassBlock, volume: residency.MeshIrradianceParameters, measuring: (residency.IndirectTier != Puck.SignedDistance.SdfIndirectTier.Off));

        var cascades = m_owner.CascadesOf(frame: frame, instance: m_context.Instance, texels: BoundCascadeTexels(recording: in recording), view: view);

        SdfFrameBlock.WriteMeshShadows(block: recording.PassBlock, cascades: in cascades);
        if (m_part == SdfWorldPackage.Parts.ShadowFar) {
            frame.RateCounts?.Count(kind: SdfUpdateRateCounts.ShadowFarRendered);
        }
        if (IsShadowCascade) {
            SdfFrameBlock.WriteShadowCamera(block: recording.PassBlock, camera: cascades[m_part switch {
                SdfWorldPackage.Parts.ShadowNear => 0,
                SdfWorldPackage.Parts.ShadowFar => 1,
                _ => 2,
            }], texels: cascades.Texels);
        }
        if (IsMesh) {
            var cascade = m_part switch {
                SdfWorldPackage.Parts.ShadowNear => 0,
                SdfWorldPackage.Parts.ShadowFar => 1,
                _ => 2,
            };

            RecordMesh(
                cascadeTexels: cascades.Texels,
                occlude: (frame.MeshCull == SdfMeshCullLevel.Occlusion),
                cull: ((frame.MeshCull == SdfMeshCullLevel.Off)
                    ? SdfMeshCull.None
                    : (IsShadowCascade
                        ? SdfMeshCull.Of(camera: frame.Views[view].Camera, cascade: cascades[cascade], far: frame.FarDistance, height: recording.RenderHeight, texels: cascades.Texels, width: recording.RenderWidth)
                        : SdfMeshCull.Of(camera: frame.Views[view].Camera, far: frame.FarDistance, height: height, width: width))),
                // The glass pass's thickness is the sums of its own triangles' ray distances, so glass draws its own.
                detail: (IsGlass
                    ? SdfMeshDetail.Finest
                    : (frame.MeshDetail switch {
                        SdfMeshDetailLevel.Auto => (IsShadowCascade
                            ? SdfMeshDetail.Of(cascade: cascades[cascade], texels: cascades.Texels)
                            : SdfMeshDetail.Of(camera: frame.Views[view].Camera, displayHeight: recording.FrameHeight)),
                        _ => SdfMeshDetail.Forced(level: (((int)frame.MeshDetail) - 1)),
                    })),
                recording: in recording,
                tables: tables
            );
        } else {
            RecordCompute(
                recording: in recording,
                tables: tables
            );
        }

        return RenderGraphPackageOutcome.Drew;
    }
    public bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback) {
        if (m_pick is not null) {
            return (m_pick.Take(index: 0, readback: out readback, slot: slot) ||
                m_diagnosticPick!.Take(index: 0, readback: out readback, slot: slot));
        }
        readback = default;
        return false;
    }

    public ulong ReadbackBytes => ((m_pick?.ReadbackBytes ?? 0UL) + (m_diagnosticPick?.ReadbackBytes ?? 0UL));

    public void Submitted(int slot, IGpuSubmissionFence fence) {
        m_pick?.Submitted(fence: fence, slot: slot);
        m_diagnosticPick?.Submitted(fence: fence, slot: slot);
        m_occlusion?.Submitted(slot, fence);
    }

    private int InputIndexOf(string member) {
        for (var index = 0; (index < m_inputs.Length); index++) {
            if (ReadMemberOf(version: m_inputs[index]) == member) {
                return index;
            }
        }
        throw new InvalidOperationException(message: $"Pass '{m_context.Pass}' has no input read through '{member}'.");
    }
    // The texels a side of the cascade depth the pass binds, a cascade's output or the surface pass's input, which its pass
    // block and its fit follow, so a pass draws and reads the depth its graph allocated while a graph of another shadow
    // resolution builds; null for a pass that binds none.
    private int? BoundCascadeTexels(in RenderGraphPackageRecording recording) {
        for (var port = 0; (port < m_outputs.Length); port++) {
            if (IsCascadeDepth(version: m_outputs[port])) {
                return ((int)recording.Outputs[port].Image.Width);
            }
        }
        for (var port = 0; (port < m_inputs.Length); port++) {
            if (IsCascadeDepth(version: m_inputs[port])) {
                return ((int)recording.Inputs[port].Image.Width);
            }
        }

        return null;
    }
    private static bool IsCascadeDepth(string version) => (CascadeIndexOf(version: version) >= 0);
    // The cascade layer a storage is (its index in CascadeBindings), or -1 for any other storage.
    private static int CascadeIndexOf(string version) {
        for (var cascade = 0; (cascade < CascadeBindings.Length); cascade++) {
            if (string.Equals(a: CascadeBindings[cascade].Version, b: version, comparisonType: StringComparison.Ordinal)) {
                return cascade;
            }
        }

        return -1;
    }
    // Takes the view the instance resolved this frame when it is another than the one the pass records: the package
    // decided the pass can record it as built (SdfWorldPasses.CanFollow). A counter change holds recording until matching
    // passes install. The hold and the retain move with the view; the ports and screens rebind for the
    // other residency's tables on this recording.
    private void Follow() {
        if (
            (m_owner.ViewOf(instance: m_context.Instance) is not { } current) ||
            (current == m_view)
        ) {
            return;
        }

        var previous = m_view.Residency;

        if (!ReferenceEquals(
            objA: previous,
            objB: current.Residency
        )) {
            current.Residency.Retain();
            m_owner.Hold(residency: current.Residency);
            m_owner.Unhold(residency: previous);
            previous.Release();
            DeclareScreens(residency: current.Residency);

            foreach (var screens in m_screens) {
                Array.Clear(array: screens);
            }
        }

        m_view = current;
    }
    // Which screen indices a residency binds.
    private void DeclareScreens(SdfWorldResidency residency) {
        Array.Clear(array: m_declaredScreens);

        if (residency.ScreenSources is { } sources) {
            foreach (var screen in sources.Screens) {
                m_declaredScreens[screen] = true;
            }
        }
    }
    // Binds the pass set and dispatches the part's kernel: the pyramid one invocation a texel of its first level, and the
    // surface and views passes one invocation a pixel of the render extent.
    private void RecordCompute(in RenderGraphPackageRecording recording, SdfWorldTables tables) {
        var slot = recording.Slot;
        var set = m_sets!.PassSet(slot: slot);
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var pipeline = tables.Pipeline(kernel: m_part switch {
            SdfWorldPackage.Parts.Pyramid => SdfKernel.MeshPyramid,
            SdfWorldPackage.Parts.Surface => SdfKernel.Surface,
            _ => SdfKernel.Views,
        });

        BindPorts(
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
        recorder.BindPipeline(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            pipelineHandle: pipeline.Handle
        );
        m_sets.Bind(
            bindPoint: GpuBindPoint.Compute,
            commandBuffer: commandBuffer,
            pipelineLayout: pipeline.LayoutHandle,
            recorder: recorder,
            slot: slot
        );
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Compute,
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: tables.WorldSet(slot: tables.CurrentSlot),
            group: ((uint)ShaderInterfaceGroup.World),
            pipelineLayoutHandle: pipeline.LayoutHandle
        );

        var (x, y) = m_part switch {
            // One invocation a texel of the pyramid's first level, half the extent rounded up.
            SdfWorldPackage.Parts.Pyramid => (recording.Width.CeilingDivide(divisor: 2u).CeilingDivide(divisor: 8u), recording.Height.CeilingDivide(divisor: 2u).CeilingDivide(divisor: 8u)),
            _ => (recording.Width.CeilingDivide(divisor: SdfVisibility.GroupEdgePixels), recording.Height.CeilingDivide(divisor: SdfVisibility.GroupEdgePixels)),
        };

        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            groupCountX: x,
            groupCountY: y,
            groupCountZ: 1
        );
    }
    // Selects draws and levels on the GPU, then draws one indirect phase (two for opaque occlusion), pulling triangles
    // from the shared mesh region; a cascade uses the shadow pipeline into its depth,
    // cascadeTexels a side. A frame with no draws records the render pass alone, which clears the target.
    // The mesh set a frame slot draws through while the tables upload into their current ring slot. The tables' ring and
    // the frame slots' turn at different lengths, so a set per pair keeps each set's buffers those of one ring slot, and
    // a set never rewrites what it binds once written.
    private static int MeshSetOf(int slot, SdfWorldTables tables) =>
        ((slot * SdfWorldTables.FrameRingSize) + tables.CurrentSlot);
    private void RecordMesh(in RenderGraphPackageRecording recording, SdfWorldTables tables, int cascadeTexels, in SdfMeshCull cull, in SdfMeshDetail detail, bool occlude) {
        var count = tables.MeshDrawCount;
        var slot = recording.Slot;
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var pipeline = (IsShadowCascade ? tables.ShadowPipeline : (IsGlass ? tables.GlassPipeline : tables.MeshPipeline));
        var meshSet = MeshSetOf(slot: slot, tables: tables);
        var set = m_meshSets[meshSet];
        var target = recording.Outputs[0].Owned!;
        var framebuffer = m_framebuffers[0];
        var lateFramebuffer = ((m_lateFramebuffers.Length > 0) ? m_lateFramebuffers[0] : null);
        var occlusion = ((count > 0) ? m_occlusion : null);
        var twoPhase = (occlude && !IsShadowCascade && !IsGlass);
        IGpuFramebuffer? shadowGlassFramebuffer = null;

        for (var index = 0; (index < m_meshTargets.Count); index++) {
            if (ReferenceEquals(
                objA: m_meshTargets[index],
                objB: target
            )) {
                framebuffer = m_framebuffers[index];
                lateFramebuffer = ((m_lateFramebuffers.Length > 0) ? m_lateFramebuffers[index] : null);
                m_targetsWritten[index] = true;
                if (IsShadowCascade) { shadowGlassFramebuffer = m_shadowGlassFramebuffers[index]; }
            }
        }

        if (IsShadowCascade) {
            var optical = recording.Outputs[2].Buffer!;
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.FragmentShader,
                destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
            recorder.ClearStorageBuffer(commandBuffer, optical.BufferHandle, optical.SizeBytes);
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.FragmentShader);
        }
        if (count > 0) {
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
            // third output; every other raster part binds the dummy at both.
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

            if (occlusion is not null) {
                var frame = m_view.Residency.Frame!;
                occlusion.Stage(tables: tables, count: count, height: recording.RenderHeight, width: recording.RenderWidth, slot: slot, cull: in cull, detail: in detail, held: tables.HoldsFrame, glass: IsGlass, occlude: twoPhase, splitExit: IsShadowCascade,
                    camera: frame.Views[Math.Min(m_view.View, frame.Views.Count - 1)].Camera);
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
                        target: (((phase == 0) || !twoPhase) ? tables.SampledFiller.ImageViewHandle : target.ImageViewHandle));
                }
                occlusion.RecordEarly(commandBuffer: commandBuffer, pipeline: tables.OcclusionPipeline, recorder: recorder, set: m_occlusionSets[(meshSet * OcclusionSetsPerMesh)]);
            }
        }

        var area = new GpuPixelRect(
            // A cascade covers its whole depth, whatever the view's render extent.
            Height: (IsShadowCascade ? ((uint)cascadeTexels) : recording.Height),
            Width: (IsShadowCascade ? ((uint)cascadeTexels) : recording.Width),
            X: 0,
            Y: 0
        );

        recorder.BeginRenderPass(
            area: area,
            commandBufferHandle: commandBuffer,
            framebuffer: framebuffer
        );
        if (occlusion is not null) {
            // The first phase: what the previous frame found visible, and every draw no host box bounds.
            RecordMeshPhase(commandBuffer: commandBuffer, phase: 0, pipeline: pipeline, recorder: recorder, selection: occlusion, set: set);
        }
        recorder.EndRenderPass(commandBufferHandle: commandBuffer);

        if (IsShadowCascade) {
            // Exit solids use front-face culling; sheets and clipped draws use the unculled pipeline.
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: lateFramebuffer!);
            if (occlusion is not null) {
                RecordMeshPhase(commandBuffer: commandBuffer, pipeline: tables.ShadowExitPipeline, recorder: recorder, set: set, selection: occlusion, phase: 1);
                RecordMeshPhase(commandBuffer: commandBuffer, pipeline: tables.ShadowPipeline, recorder: recorder, set: set, selection: occlusion, phase: 2);
            }
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
        }
        if (IsShadowCascade && occlusion is not null) {
            // Separate descriptors: early raster/selection must never bind the entry depth while it is an attachment.
            var glassSetIndex = meshSet + m_meshSets.Length / 2;
            var glassSet = m_meshSets[glassSetIndex];
            var bound = m_meshSetBuffers[glassSetIndex];
            tables.WriteMeshTables(bound: bound, set: glassSet, slot: tables.CurrentSlot);
            tables.WriteMeshWorkCounters(bound: ref bound[MeshSetCounters], counters: WorkCountersOf(recording: in recording).Buffer, set: glassSet);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetVisibility], buffer: tables.DummyBuffer, member: SdfWorldPackage.VisibilityRecords, set: glassSet);
            tables.WriteMeshBuffer(bound: ref bound[MeshSetGlassSums], buffer: recording.Outputs[2].Buffer!, member: SdfWorldPackage.GlassSumsWritten, set: glassSet);
            SdfMeshOcclusion.Unbind(bound: m_meshSetOcclusion[glassSetIndex], boundTarget: ref m_meshSetTargets[glassSetIndex], first: 0, set: glassSet, tables: tables);
            tables.WriteMeshTarget(bound: ref m_meshSetTargets[glassSetIndex], set: glassSet, view: target.ImageViewHandle);
            occlusion.BindDraws(tables: tables, set: glassSet, bound: m_meshSetOcclusion[glassSetIndex]);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, imageHandle: target.ImageHandle,
                oldLayout: GpuImageLayout.DepthAttachment, newLayout: GpuImageLayout.ShaderReadOnly,
                sourceAccessMask: GpuAccess.DepthAttachmentWrite, sourceStageMask: GpuStage.FragmentTests,
                destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.FragmentShader);
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: shadowGlassFramebuffer!);
            RecordMeshPhase(commandBuffer: commandBuffer, pipeline: tables.ShadowGlassPipeline, recorder: recorder, set: glassSet, selection: occlusion, phase: 3);
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, imageHandle: target.ImageHandle,
                oldLayout: GpuImageLayout.ShaderReadOnly, newLayout: GpuImageLayout.DepthAttachment,
                sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: GpuStage.FragmentShader,
                destinationAccessMask: GpuAccess.DepthAttachmentWrite, destinationStageMask: GpuStage.FragmentTests);
        }
        if (twoPhase && (occlusion is not null) && (occlusion.CandidateCount > 0)) {
            // The second phase: what the test found visible that the first phase did not draw, over the first's target
            // and depth.
            occlusion.RecordLate(commandBuffer: commandBuffer, pipeline: tables.OcclusionPipeline, recorder: recorder, set: m_occlusionSets[((meshSet * OcclusionSetsPerMesh) + 1)], target: target.ImageHandle);
            recorder.BeginRenderPass(
                area: area,
                commandBufferHandle: commandBuffer,
                framebuffer: lateFramebuffer!
            );
            RecordMeshPhase(commandBuffer: commandBuffer, phase: 1, pipeline: pipeline, recorder: recorder, selection: occlusion, set: set);
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
        }
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
    // Writes, once per slot, every storage the pass's ports bind at the member its access reads or writes it through, and
    // the tables' dummy and fillers at every member no port binds. The storages a slot resolves stay
    // the instance's for the recorder's life.
    private void BindPorts(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var slot = recording.Slot;

        if (ReferenceEquals(
            objA: m_portTables[slot],
            objB: tables
        )) {
            return;
        }

        var bindings = tables.Bindings;
        var layout = PassLayout;
        var output = tables.StorageFiller.ImageViewHandle;
        var meshVisibility = tables.SampledFiller.ImageViewHandle;
        Span<nint> cascades = stackalloc nint[CascadeBindings.Length];

        cascades.Fill(value: tables.SampledFiller.ImageViewHandle);

        foreach (var member in ScratchMembers) {
            tables.WriteInterfaceBuffer(buffer: tables.DummyBuffer, layout: layout, member: member, set: set);
        }

        for (var port = 0; (port < m_inputs.Length); port++) {
            var name = m_inputs[port];
            var bound = recording.Inputs[port];

            if (bound.Buffer is { } buffer) {
                if (ReadMemberOf(version: name) is { } member) {
                    tables.WriteInterfaceBuffer(buffer: buffer, layout: layout, member: member, set: set);
                }
            } else if (string.Equals(
                a: name,
                b: SdfWorldPackage.Parts.MeshTarget,
                comparisonType: StringComparison.Ordinal
            )) {
                meshVisibility = bound.Image.ImageViewHandle;
            } else if (CascadeIndexOf(version: name) is var cascade and >= 0) {
                cascades[cascade] = bound.Image.ImageViewHandle;
            }
        }
        for (var port = 0; (port < m_outputs.Length); port++) {
            var name = m_outputs[port];
            var bound = recording.Outputs[port];

            if (bound.Buffer is { } buffer) {
                if (WrittenMemberOf(version: name) is { } member) {
                    tables.WriteInterfaceBuffer(buffer: buffer, layout: layout, member: member, set: set);
                }
            } else if (bound.Kind == ShaderPipelineResourceKind.Image) {
                output = bound.Image.ImageViewHandle;
            }
        }

        m_boundCounters[slot] = null;
        m_portTables[slot] = tables;

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
        for (var cascade = 0; (cascade < CascadeBindings.Length); cascade++) {
            bindings.WriteSampledImage(arrayElement: 0, binding: CascadeBindings[cascade].Binding, descriptorSetHandle: set, imageViewHandle: cascades[cascade]);
        }
        Array.Clear(array: m_screens[slot]);
    }
    // Writes each screen's image into the slot's pass set: a host's image every frame, since its handle is unique only among
    // live objects, and the filler once while the screen shows nothing.
    private void BindScreens(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var bound = m_screens[recording.Slot];
        var filler = tables.SampledFiller.ImageViewHandle;
        var residency = m_view.Residency;

        for (var screen = 0; (screen < SdfWorldTables.MaxScreenSurfaces); screen++) {
            var image = (m_declaredScreens[screen]
                ? residency.ScreenImage(
                    leases: recording.Leases,
                    reads: recording.Reads,
                    screen: screen,
                    view: m_view.View
                )
                : 0);

            if (image == 0) {
                if (bound[screen] == filler) {
                    continue;
                }

                image = filler;
            }

            tables.Bindings.WriteSampledImage(
                arrayElement: ((uint)screen),
                binding: ScreenSourcesBinding,
                descriptorSetHandle: set,
                imageViewHandle: image
            );
            bound[screen] = image;
        }
    }
    // Where a part counts its march steps and texels written: every one counts
    // (RenderGraphFragmentPass.CountsKernelWork), so its node always hands it a row.
    private GpuKernelCounterRow WorkCountersOf(in RenderGraphPackageRecording recording) =>
        (recording.WorkCounters ?? throw new InvalidOperationException(message: $"Pass '{m_context.Pass}' counts its kernels' work, but its recording carries no work counters."));
    // The member a pass reads a fragment buffer through, or null for one it reads through no member.
    private static string? ReadMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.Visibility => SdfWorldPackage.VisibilityRecords,
        SdfWorldPackage.Parts.MeshPyramid => SdfWorldPackage.MeshPyramid,
        SdfWorldPackage.Parts.ShadowNearOptical => SdfWorldPackage.MeshShadowNearOptical,
        SdfWorldPackage.Parts.ShadowFarOptical => SdfWorldPackage.MeshShadowFarOptical,
        SdfWorldPackage.Parts.ShadowSecondOptical => SdfWorldPackage.MeshShadowSecondOptical,
        _ => null,
    };
    // The member a pass writes a fragment buffer through, or null for one it writes through no member.
    private static string? WrittenMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.Visibility => SdfWorldPackage.VisibilityRecordsWritten,
        SdfWorldPackage.Parts.Reactivity => SdfWorldPackage.ReactivityWritten,
        SdfWorldPackage.Parts.MeshPyramid => SdfWorldPackage.MeshPyramidWritten,
        _ => null,
    };
}
