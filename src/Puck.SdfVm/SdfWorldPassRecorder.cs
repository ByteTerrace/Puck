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
    // Views attribute analytic sky lighting by layer; the bins pass counts each bin's tiles (sdf-world-views-bins.comp.hlsl),
    // a row for every bin a plan gives a width (SdfShadeDispatch.PlanBins).
    public IReadOnlyList<string> WorkDetails(in FrameContext context) => m_part switch {
        SdfWorldPackage.Parts.Views => m_view.Residency.SkyDetails.Labels,
        SdfWorldPackage.Parts.ViewsProbes => RateDetails,
        SdfWorldPackage.Parts.ShadowFar => CasterDetails,
        SdfWorldPackage.Parts.ViewsBins => BinDetails,
        _ => [],
    };

    private static readonly string[] CasterDetails = ["receiver-rejected", "entry-rejected"];
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
        SdfWorldPackage.ViewsFilterRecords,
        SdfWorldPackage.ViewsFilterRecordsWritten,
        SdfWorldPackage.ViewsProbeRecords,
        SdfWorldPackage.ViewsProbeRecordsWritten,
        SdfWorldPackage.ShadeTiles,
        SdfWorldPackage.ShadeTilesWritten,
        SdfWorldPackage.ShadeArgumentsWritten,
        SdfWorldPackage.HistorySurface,
    ];
    // Each cascade layer's storage and the binding the surface pass samples it at.
    private static readonly (string Version, uint Binding)[] CascadeBindings = [
        (SdfWorldPackage.Parts.ShadowNearDepth, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowNear)),
        (SdfWorldPackage.Parts.ShadowFarDepth, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowFar)),
        (SdfWorldPackage.Parts.ShadowSecondDepth, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowSecond)),
        (SdfWorldPackage.Parts.ShadowNearExit, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowNearExit)),
        (SdfWorldPackage.Parts.ShadowFarExit, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowFarExit)),
        (SdfWorldPackage.Parts.ShadowSecondExit, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowSecondExit)),
        (SdfWorldPackage.Parts.ShadowDistantDepth, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowDistant)),
        (SdfWorldPackage.Parts.ShadowDistantExit, SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowDistantExit)),
    ];
    private static readonly uint OutputBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.Output);
    private static readonly uint MeshVisibilityBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshVisibility);
    private static readonly uint ScreenSourcesBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.ScreenSources);

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly string[] m_inputs;
    private readonly string[] m_outputs;
    private readonly int m_rateHistoryPort;
    private readonly IGpuBuffer?[] m_boundRateHistory;
    private readonly SdfWorldPasses m_owner;
    private readonly string m_part;
    // Whether the pass belongs to the temporal fragment (SdfWorldPackage.TemporalFragment).
    private readonly bool m_temporal;
    private readonly bool m_resolved;
    // Whether the views pass's graph shades in bins (SdfWorldPasses.ShadesInBins); it records unsplit on a frame whose
    // plan does not bin.
    private readonly bool m_binned;

    private bool HasShadowOptical => (IsShadowCascade && m_outputs.Any(predicate: static output => output is SdfWorldPackage.Parts.ShadowNearOptical or SdfWorldPackage.Parts.ShadowFarOptical or SdfWorldPackage.Parts.ShadowSecondOptical));
    // Whether the distant pass keeps a back map, which a redraw spread over several frames draws its shares into
    // (SdfWorldPackage.WithShadows): its third and fourth outputs.
    private bool HasDistantBack => ((m_part == SdfWorldPackage.Parts.ShadowDistant) && m_outputs.Contains(value: SdfWorldPackage.Parts.ShadowDistantBackDepth));
    // Whether the far pass keeps a static layer (SdfWorldPackage.WithShadows' farStatic).
    private bool HasFarStatic => ((m_part == SdfWorldPackage.Parts.ShadowFar) && m_outputs.Contains(value: SdfWorldPackage.Parts.ShadowFarStaticDepth));

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
    private readonly IGpuFramebuffer[] m_entryLateFramebuffers = [];
    // The distant pass's back map (HasDistantBack), per instance of its target: its depth and exit layer, a framebuffer
    // over each that clears it (a redraw's first share) and one that loads it (each later share).
    private readonly IReadOnlyList<IGpuImage> m_backDepths = [];
    private readonly IReadOnlyList<IGpuImage> m_backExits = [];
    private readonly IGpuFramebuffer[] m_backFramebuffers = [];
    private readonly IGpuFramebuffer[] m_backExitFramebuffers = [];
    private readonly IGpuFramebuffer[] m_backLoadFramebuffers = [];
    private readonly IGpuFramebuffer[] m_backExitLoadFramebuffers = [];
    // The far pass's static layer (HasFarStatic), per instance of its target: its depth and exit layer, framebuffers that
    // clear each for the static draws and one that loads the depth for their second phase, a framebuffer that loads the
    // cascade's own exit layer for the moving draws over the layer's copy (the entry's is m_entryLateFramebuffers), whether
    // a submitted recording of this graph drew each instance's layer, and the static draws' own selection, so neither
    // staging's visibility history is the other's.
    private readonly IReadOnlyList<IGpuImage> m_staticDepths = [];
    private readonly IReadOnlyList<IGpuImage> m_staticExits = [];
    private readonly IGpuFramebuffer[] m_staticFramebuffers = [];
    private readonly IGpuFramebuffer[] m_staticLateFramebuffers = [];
    private readonly IGpuFramebuffer[] m_staticExitFramebuffers = [];
    private readonly IGpuFramebuffer[] m_exitLoadFramebuffers = [];
    private readonly bool[] m_staticWritten = [];
    private readonly SdfMeshOcclusion? m_staticOcclusion;
    // The target instance whose static layer the latest recording drew, which Submitted marks written.
    private int? m_recordedStatic;
    // The mesh sets of one group, one per frame slot and tables ring slot: the sets are laid out by group, the cascade's own
    // selection first, the static layer's next when the pass keeps one, then the glass extinction's (OcclusionGroups).
    private readonly int m_rasterSets;
    private readonly IGpuBuffer?[][] m_meshSetOcclusion = [];
    private readonly nint[] m_meshSetTargets = [];
    // Early and late compute sets beside each mesh set. Early binds a sampled filler while the target is an attachment;
    // late binds the target that RecordLate transitions for sampling. A descriptor remains valid even on a branch that
    // does not read it, and rewriting one shared set would change both recorded dispatches.
    private readonly nint[] m_occlusionSets = [];
    private readonly IGpuBuffer?[][] m_occlusionSetBuffers = [];
    private readonly nint[] m_occlusionSetTargets = [];
    // Whether each instance of the mesh part's or a cascade's target has been drawn or cleared once, and for a cascade
    // whether that instance's exit layer has too: a layer drawn in no submitted frame of this graph has no contents.
    private readonly bool[] m_targetsWritten = [];
    private readonly bool[] m_exitsWritten = [];
    // Whether each instance's target, and a cascade's exit layer, last recorded a frame holding none of the part's draws,
    // so it holds that recording's clear and no hit a later frame's draw records could misname (PartDrawCount).
    private readonly bool[] m_targetsClear = [];
    private readonly bool[] m_exitsClear = [];

    // The layers the latest recording writes into its target instance (null for a layer it leaves), each with whether
    // it writes the clear of a frame holding none of the part's draws. Only Submitted applies it to the four arrays
    // above, and the node calls Submitted only once the recording's submission reached the queue, so a recording whose
    // submission never ran marks no layer written or clear.
    private (int Index, bool? Depth, bool? Exit)? m_recordedLayers;
    private bool m_disposed;
    // What made the latest views render read external content, or null when nothing did.
    private string? m_sourceTaint;
    private bool m_readsHistory;

    private readonly SdfWorldPickReadback? m_pick;
    private readonly SdfWorldPickReadback? m_diagnosticPick;

    private SdfShadeDispatch? m_shadePlan;

    private readonly SdfShadeSlots m_shade;

    public SdfWorldPassRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) {
        m_context = context;
        m_shade = new SdfShadeSlots(slots: context.InFlightFrames);
        m_owner = owner;
        m_view = view;
        m_part = (context.Part ?? throw new ArgumentException(message: $"Pass '{context.Pass}' runs no part of '{RenderGraphPackageCatalog.SdfWorld}'.", paramName: nameof(context)));
        // Port declarations are captured by graph planning, before the asynchronous build. The live frame may already
        // request another fade capacity when this recorder installs or follows a view.
        var prefix = context.Pass[..^m_part.Length];

        string LocalName(ShaderPipelineResource resource) => (resource.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix)
            ? resource.Name[prefix.Length..] : resource.Name);

        m_inputs = [.. context.Inputs.Select(selector: LocalName)];
        m_rateHistoryPort = Array.IndexOf(m_inputs, SdfWorldPackage.Parts.HistorySurface);
        m_outputs = [.. context.Outputs.Select(selector: LocalName)];
        var fragment = owner.FragmentOf(instance: context.Instance)!;

        m_temporal = fragment.Resources.Any(predicate: static resource => resource.History);
        m_resolved = m_outputs.Contains(value: SdfWorldPackage.CurrentColor);
        m_binned = SdfWorldPasses.ShadesInBins(context: context);

        DeclareScreens(residency: view.Residency);

        var slots = context.InFlightFrames;
        var tables = (view.Residency.Tables ?? throw new InvalidOperationException(message: $"Residency '{view.Residency.Name}' has no tables for pass '{context.Pass}'."));

        m_portTables = new SdfWorldTables?[slots];
        m_boundCounters = new IGpuBuffer?[slots];
        m_boundRateHistory = m_rateHistoryPort >= 0 ? new IGpuBuffer?[slots] : [];
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
        var rasterSets = (slots * SdfWorldTables.FrameRingSize);

        m_rasterSets = rasterSets;

        var selectingSets = (rasterSets * OcclusionGroups);
        var sets = (selectingSets + (HasShadowOptical ? rasterSets : 0));

        var occludes = IsMesh;

        for (var set = 0; (set < sets); set++) {
            sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.Mesh.Groups);

            if (occludes && (set < selectingSets)) {
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
            var occlusionSets = (occludes ? (selectingSets * OcclusionSetsPerMesh) : 0);

            m_occlusionSets = new nint[occlusionSets];
            m_occlusionSetBuffers = new IGpuBuffer?[occlusionSets][];
            m_occlusionSetTargets = new nint[occlusionSets];

            for (var set = 0; (set < sets); set++) {
                var slot = ((set % (slots * SdfWorldTables.FrameRingSize)) / SdfWorldTables.FrameRingSize);

                m_meshSetBuffers[set] = new IGpuBuffer?[MeshSetBuffers];
                m_meshSetOcclusion[set] = new IGpuBuffer?[MeshSetOcclusionBuffers];

                if (occludes && (set < selectingSets)) {
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
            m_exitsWritten = new bool[m_meshTargets.Count];
            m_targetsClear = new bool[m_meshTargets.Count];
            m_exitsClear = new bool[m_meshTargets.Count];

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
                m_shadowGlassFramebuffers = new IGpuFramebuffer[(HasShadowOptical ? m_meshTargets.Count : 0)];
                if (HasShadowOptical || (m_part == SdfWorldPackage.Parts.ShadowFar) || HasDistantBack) {
                    m_lateRenderPass = context.Services.RenderPassFactory.Create(
                    description: SdfMeshRasterPass.ShadowRenderPass with { Depth = SdfWorldPackage.MeshShadowDepthAttachment with { Load = GpuAttachmentLoad.Load } },
                    name: new GpuObjectName(owner: context.Instance, part: context.Pass, detail: "retained depth"));
                }
                m_entryLateFramebuffers = new IGpuFramebuffer[((m_part == SdfWorldPackage.Parts.ShadowFar) ? m_meshTargets.Count : 0)];

                for (var index = 0; (index < m_lateFramebuffers.Length); index++) {
                    m_lateFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(
                        colors: [],
                        depth: groups.OutputImages[1][index],
                        renderPass: tables.ShadowRenderPass
                    );
                    if (HasShadowOptical) {
                        m_shadowGlassFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(
                        colors: [], depth: groups.OutputImages[1][index], renderPass: m_lateRenderPass!);
                    }
                    if (m_entryLateFramebuffers.Length != 0) {
                        m_entryLateFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(
                        colors: [], depth: m_meshTargets[index], renderPass: m_lateRenderPass!);
                    }
                }
                if (HasFarStatic) {
                    m_staticDepths = groups.OutputImages[OutputNamed(output: SdfWorldPackage.Parts.ShadowFarStaticDepth)];
                    m_staticExits = groups.OutputImages[OutputNamed(output: SdfWorldPackage.Parts.ShadowFarStaticExit)];
                    m_staticFramebuffers = new IGpuFramebuffer[m_meshTargets.Count];
                    m_staticLateFramebuffers = new IGpuFramebuffer[m_meshTargets.Count];
                    m_staticExitFramebuffers = new IGpuFramebuffer[m_meshTargets.Count];
                    m_exitLoadFramebuffers = new IGpuFramebuffer[m_meshTargets.Count];
                    m_staticWritten = new bool[m_meshTargets.Count];
                    for (var index = 0; (index < m_meshTargets.Count); index++) {
                        m_staticFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(colors: [], depth: m_staticDepths[index], renderPass: tables.ShadowRenderPass);
                        m_staticLateFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(colors: [], depth: m_staticDepths[index], renderPass: m_lateRenderPass!);
                        m_staticExitFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(colors: [], depth: m_staticExits[index], renderPass: tables.ShadowRenderPass);
                        m_exitLoadFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(colors: [], depth: groups.OutputImages[1][index], renderPass: m_lateRenderPass!);
                    }
                }
                if (HasDistantBack) {
                    m_backDepths = groups.OutputImages[OutputNamed(output: SdfWorldPackage.Parts.ShadowDistantBackDepth)];
                    m_backExits = groups.OutputImages[OutputNamed(output: SdfWorldPackage.Parts.ShadowDistantBackExit)];
                    m_backFramebuffers = new IGpuFramebuffer[m_meshTargets.Count];
                    m_backExitFramebuffers = new IGpuFramebuffer[m_meshTargets.Count];
                    m_backLoadFramebuffers = new IGpuFramebuffer[m_meshTargets.Count];
                    m_backExitLoadFramebuffers = new IGpuFramebuffer[m_meshTargets.Count];
                    for (var index = 0; (index < m_meshTargets.Count); index++) {
                        m_backFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(colors: [], depth: m_backDepths[index], renderPass: tables.ShadowRenderPass);
                        m_backExitFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(colors: [], depth: m_backExits[index], renderPass: tables.ShadowRenderPass);
                        m_backLoadFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(colors: [], depth: m_backDepths[index], renderPass: m_lateRenderPass!);
                        m_backExitLoadFramebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(colors: [], depth: m_backExits[index], renderPass: m_lateRenderPass!);
                    }
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
            m_occlusion = new SdfMeshOcclusion(buffers: context.Services.BufferFactory, owner: context.Instance, part: context.Pass, slots: slots, argumentPhases: (IsShadowCascade ? (HasShadowOptical ? 5u : 4u) : 2u),
                sight: ((m_part == SdfWorldPackage.Parts.Mesh) ? owner.PortalSightOf(instance: context.Instance) : null));
            m_staticOcclusion = (HasFarStatic
                ? new SdfMeshOcclusion(buffers: context.Services.BufferFactory, owner: context.Instance, part: $"{context.Pass} static", slots: slots, argumentPhases: (HasShadowOptical ? 5u : 4u))
                : null);
        } catch {
            foreach (var framebuffer in m_framebuffers) {
                framebuffer?.Dispose();
            }
            foreach (var framebuffer in m_lateFramebuffers) {
                framebuffer?.Dispose();
            }

            foreach (var framebuffer in m_shadowGlassFramebuffers) { framebuffer?.Dispose(); }
            foreach (var framebuffer in m_entryLateFramebuffers) { framebuffer?.Dispose(); }
            DisposeBackFramebuffers();
            m_occlusion?.Dispose();
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
    private bool IsShadowCascade => (m_part is SdfWorldPackage.Parts.ShadowNear or SdfWorldPackage.Parts.ShadowFar or SdfWorldPackage.Parts.ShadowSecond or SdfWorldPackage.Parts.ShadowDistant);
    // The cascade entry a shadow part draws (SdfMeshShadowCascades).
    private int CascadeEntry => m_part switch {
        SdfWorldPackage.Parts.ShadowNear => SdfMeshShadowCascades.NearEntry,
        SdfWorldPackage.Parts.ShadowFar => SdfMeshShadowCascades.FarEntry,
        SdfWorldPackage.Parts.ShadowDistant => SdfMeshShadowCascades.DistantEntry,
        _ => SdfMeshShadowCascades.SecondEntry,
    };

    // The draws the part rasterizes: the glass draws for the glass pass, the opaque ones for the mesh pass and the cascades.
    private uint PartDrawCount(SdfWorldTables tables) => (IsGlass ? tables.GlassDrawCount : tables.OpaqueDrawCount);

    // Whether a mesh or cascade frame holding none of the part's draws may skip: every target instance (and a cascade's
    // every exit layer) already holds the clear such a frame records. A target that last drew hits keeps them when its part
    // skips, and the surface pass reads a hit's draw and triangle against draw records that may no longer hold their
    // meshes. The glass passes skip by their own answer instead (SdfWorldPasses.GlassSeen), which skips the shading pass
    // that reads their target with them.
    private bool TargetsHoldClear => (Array.TrueForAll(array: m_targetsClear, match: static clear => clear) &&
        (!IsShadowCascade || Array.TrueForAll(array: m_exitsClear, match: static clear => clear)));

    // The mesh set groups that run a selection: the cascade's own, and the static layer's when the pass keeps one.
    private int OcclusionGroups => (HasFarStatic ? 2 : 1);

    // The index of the target instance a recording binds among the part's instances, or -1 for none of them.
    private int TargetIndexOf(IGpuImage? target) {
        for (var index = 0; (index < m_meshTargets.Count); index++) {
            if (ReferenceEquals(objA: m_meshTargets[index], objB: target)) {
                return index;
            }
        }

        return -1;
    }
    // Whether a submitted recording of this graph drew the static layer of the target instance a recording binds.
    private bool StaticWritten(IGpuImage? target) => ((TargetIndexOf(target: target) is var index and >= 0) && (index < m_staticWritten.Length) && m_staticWritten[index]);
    // Whether every layer of the target instance a recording binds has been drawn in this graph.
    private bool LayersWritten(IGpuImage? target) => ((TargetIndexOf(target: target) is var index and >= 0) &&
        m_targetsWritten[index] && (!IsShadowCascade || m_exitsWritten[index]));

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
        foreach (var framebuffer in m_entryLateFramebuffers) { framebuffer?.Dispose(); }
        DisposeBackFramebuffers();
        m_lateRenderPass?.Dispose();
        m_occlusion?.Dispose();
        m_staticOcclusion?.Dispose();

        if (m_meshPool != 0) {
            m_context.Services.Bindings.DestroyPool(poolHandle: m_meshPool);
        }

        m_shade.Release(releases: m_owner.ShadeReleases);
        m_owner.Leave(view: m_view);
    }
    private void DisposeBackFramebuffers() {
        foreach (var framebuffer in m_staticFramebuffers) { framebuffer?.Dispose(); }
        foreach (var framebuffer in m_staticLateFramebuffers) { framebuffer?.Dispose(); }
        foreach (var framebuffer in m_staticExitFramebuffers) { framebuffer?.Dispose(); }
        foreach (var framebuffer in m_exitLoadFramebuffers) { framebuffer?.Dispose(); }
        foreach (var framebuffer in m_backFramebuffers) { framebuffer?.Dispose(); }
        foreach (var framebuffer in m_backExitFramebuffers) { framebuffer?.Dispose(); }
        foreach (var framebuffer in m_backLoadFramebuffers) { framebuffer?.Dispose(); }
        foreach (var framebuffer in m_backExitLoadFramebuffers) { framebuffer?.Dispose(); }
    }
    // The index of one of the part's outputs by its name in the fragment.
    private int OutputNamed(string output) => Array.IndexOf(array: m_outputs, value: output) is var index and >= 0
        ? index
        : throw new InvalidOperationException(message: $"Pass '{m_context.Pass}' has no output '{output}'.");
    // A part skips a frame whose work it would not do, recording neither its work nor its planned barriers: the mesh part
    // a frame that draws no opaque mesh, the glass passes one whose view sees no glass draw, and a shadow cascade a frame
    // with no draw or no cascade of its own. The mesh part and the cascades with no draws still record until every
    // instance of their target holds the clear such a frame records (TargetsHoldClear): before the first bake lands, and
    // after a frame that drew, whose hits a skipped frame would leave for the surface pass to read. No part walks the
    // field: the bake samples it as the residency's background GPU work (SdfMeshBakeGpuWork).
    public bool Skips(in FrameContext context) {
        switch (m_part) {
            case SdfWorldPackage.Parts.Mesh: {
                    Follow();
                    m_owner.Begin(residency: m_view.Residency);
                    var staged = m_view.Residency.Staged(context: in context);
                    var skips = ((PartDrawCount(tables: staged) == 0) && TargetsHoldClear);

                    if (skips) { m_owner.SkippedClearMeshTarget(instance: m_context.Instance, tables: staged); }
                    return skips;
                }
            // The glass clear and the glass raster skip a frame in which no glass draw meets the view's frustum
            // (SdfWorldPasses.GlassSeen); the shading pass, which reads their sums and target, then draws nothing.
            case SdfWorldPackage.Parts.GlassClear:
            case SdfWorldPackage.Parts.Glass: {
                    Follow();
                    m_owner.Begin(residency: m_view.Residency);
                    return !m_owner.GlassSeen(instance: m_context.Instance, view: m_view, context: in context);
                }
            // The surface pass reads the pyramid only for a pixel the mesh covers, which a frame with no draws has none of.
            case SdfWorldPackage.Parts.Pyramid: {
                    Follow();
                    m_owner.Begin(residency: m_view.Residency);
                    return (m_view.Residency.Staged(context: in context).MeshDrawCount == 0);
                }
            // The filter and bins passes skip a frame whose views do not shade in bins (SdfWorldResidency.ShadesInBins).
            case SdfWorldPackage.Parts.ViewsFilter:
            case SdfWorldPackage.Parts.ViewsBins: {
                    Follow();
                    m_owner.Begin(residency: m_view.Residency);
                    return !m_view.Residency.ShadesInBins;
                }
            case SdfWorldPackage.Parts.ShadowNear:
            case SdfWorldPackage.Parts.ShadowFar:
            case SdfWorldPackage.Parts.ShadowSecond:
            case SdfWorldPackage.Parts.ShadowDistant: {
                    Follow();
                    m_owner.Begin(residency: m_view.Residency);
                    var tables = m_view.Residency.Staged(context: in context);
                    var cascades = tables.CascadesOf(frame: m_view.Residency.Frame!, view: m_view.View);

                    return (!cascades.Holds(index: CascadeEntry) || ((PartDrawCount(tables: tables) == 0) && TargetsHoldClear));
                }
            default:
                return false;
        }
    }
    public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) =>
        m_owner.SignatureOf(instance: m_context.Instance, part: m_part, temporal: m_temporal, context: in context, reads: reads);
    public ulong? InputSignature(string input, long generation) => (((m_part == SdfWorldPackage.Parts.ShadowFar) && (m_context.Inputs.Count > 0) &&
        (input == m_context.Inputs[0].Name))
            ? m_owner.ReceiverSignatureOf(instance: m_context.Instance)
            : unchecked((ulong)generation));
    public void Stood() {
        if (m_part == SdfWorldPackage.Parts.ShadowFar) {
            m_view.Residency.Frame?.RateCounts?.Count(kind: SdfUpdateRateCounts.ShadowFarHeld);
        }
        if (m_part == SdfWorldPackage.Parts.ShadowDistant) {
            m_view.Residency.Frame?.RateCounts?.Count(kind: SdfUpdateRateCounts.ShadowDistantHeld);
        }
    }

    private SdfMeshRowStamp m_previousMeshRows;
    private bool m_notedMeshRows;

    public void Submitted() {
        m_notedMeshRows = false;
        if (m_recordedLayers is { } recorded) {
            if (recorded.Depth is { } depth) { m_targetsWritten[recorded.Index] = true; m_targetsClear[recorded.Index] = depth; }
            if (recorded.Exit is { } exit) { m_exitsWritten[recorded.Index] = true; m_exitsClear[recorded.Index] = exit; }
            m_recordedLayers = null;
        }
        if (m_recordedStatic is { } drawn) {
            m_staticWritten[drawn] = true;
            m_recordedStatic = null;
        }
        if (m_part == SdfWorldPackage.Parts.ShadowFar) {
            m_owner.SubmittedFar(instance: m_context.Instance);
        }
        if (m_part == SdfWorldPackage.Parts.ShadowDistant) {
            m_owner.SubmittedDistant(instance: m_context.Instance);
        }
        if (m_part == SdfWorldPackage.Parts.Views) {
            m_owner.SubmittedTaint(m_context.Instance, m_sourceTaint, m_readsHistory);
        }
        if ((m_part == SdfWorldPackage.Parts.Views) && !m_resolved) { m_owner.MarkSampleRendered(instance: m_context.Instance); }
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        m_recordedLayers = null;
        Follow();

        var residency = m_view.Residency;

        m_owner.Begin(residency: residency);

        var tables = residency.Staged(context: recording.Context);

        if (m_part == SdfWorldPackage.Parts.Mesh) {
            m_previousMeshRows = m_owner.NoteMeshRows(instance: m_context.Instance, stamp: tables.RowStamp);
            m_notedMeshRows = true;
        }

        m_owner.RecordHits(instance: m_context.Instance, part: m_part, tables: tables);
        // The glass clear zeroes the glass sums, its one output, ahead of the glass pass's accumulation.
        if (m_part == SdfWorldPackage.Parts.GlassClear) {
            var sums = recording.Outputs[0].Buffer!;

            m_owner.NoteGlassExtent(instance: m_context.Instance, width: recording.RenderWidth, height: recording.RenderHeight);
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

        if (IsGlass) {
            m_owner.NoteGlassExtent(instance: m_context.Instance, width: width, height: height);
        }
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

        if (m_rateHistoryPort >= 0) {
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

        var boundTexels = BoundCascadeTexels(recording: in recording, distant: false);
        var boundDistantTexels = BoundCascadeTexels(recording: in recording, distant: true);
        // The far cascade may draw one layer of a split refresh; asking stages the camera each drawn layer is drawn from,
        // which the cascades below and every later reader this frame read.
        var drawsStatic = false;
        var layers = ((m_part == SdfWorldPackage.Parts.ShadowFar)
            ? m_owner.RecordFar(
                counts: frame.RateCounts,
                drawsStatic: out drawsStatic,
                fresh: tables.CascadesOf(frame: frame, texels: boundTexels, view: view)[SdfMeshShadowCascades.FarEntry],
                instance: m_context.Instance,
                layer: new SdfFarStaticLayer(Held: HasFarStatic, Revision: tables.StaticDrawRevision, Written: StaticWritten(target: recording.Outputs[0].Owned)),
                written: LayersWritten(target: recording.Outputs[0].Owned)
            )
            : SdfFarLayers.Both);

        // A static layer drawn now may miss a caster absent from the records, whose landing must then move the revision.
        if (drawsStatic) {
            tables.CastersRead();
        }

        // The distant map is drawn from the camera its signature chose this frame (the fit below decides it), whole or as
        // one share of a redraw into the back map; asking stages that camera for this frame's readers when the drawing
        // completes a map. A share is drawn from its redraw's camera whatever camera receivers read this frame.
        SdfDistantDraw? distantDraw = null;

        if (m_part == SdfWorldPackage.Parts.ShadowDistant) {
            _ = m_owner.CascadesOf(distantTexels: boundDistantTexels, frame: frame, instance: m_context.Instance, tables: tables, texels: boundTexels, view: view);
            distantDraw = m_owner.RecordDistant(counts: frame.RateCounts, hasBack: HasDistantBack, instance: m_context.Instance, revision: tables.StaticDrawRevision);
            tables.CastersRead();
        }

        var cascades = m_owner.CascadesOf(distantTexels: boundDistantTexels, frame: frame, instance: m_context.Instance, tables: tables, texels: boundTexels, view: view);
        var drawCamera = ((distantDraw is { } drawn) ? drawn.Camera : (IsShadowCascade ? cascades[CascadeEntry] : default));

        SdfFrameBlock.WriteMeshShadows(block: recording.PassBlock, cascades: in cascades);
        if (IsShadowCascade) {
            SdfFrameBlock.WriteShadowCamera(block: recording.PassBlock, camera: drawCamera, texels: cascades.TexelsOf(index: CascadeEntry));
        }
        if (IsMesh) {
            var cascade = CascadeEntry;
            var cascadeTexels = cascades.TexelsOf(index: cascade);
            var distant = (m_part == SdfWorldPackage.Parts.ShadowDistant);

            RecordMesh(
                cascadeTexels: cascadeTexels,
                jitter: temporal.Jitter,
                occlude: ((frame.MeshCull == SdfMeshCullLevel.Occlusion) || (!IsShadowCascade && (frame.MeshCull == SdfMeshCullLevel.Camera))),
                // The distant map is held across camera turns and moves, so it keeps every caster in its light's box
                // rather than those that may shadow what the view shows now.
                cull: ((frame.MeshCull == SdfMeshCullLevel.Off)
                    ? SdfMeshCull.None
                    : (distant
                        ? SdfMeshCull.OfLightBox(cascade: drawCamera)
                        : (IsShadowCascade
                            ? SdfMeshCull.Of(camera: frame.Views[view].Camera, cascade: cascades[cascade], far: frame.FarDistance, height: recording.RenderHeight, texels: cascadeTexels, width: recording.RenderWidth)
                            : SdfMeshCull.Of(camera: frame.Views[view].Camera, far: frame.FarDistance, height: height, width: width)))),
                // The glass pass's thickness is the sums of its own triangles' ray distances, so glass draws its own.
                detail: (IsGlass
                    ? SdfMeshDetail.Finest
                    : (frame.MeshDetail switch {
                        SdfMeshDetailLevel.Auto => (IsShadowCascade
                            ? SdfMeshDetail.Of(cascade: drawCamera, texels: cascadeTexels)
                            : SdfMeshDetail.Of(camera: frame.Views[view].Camera, displayHeight: recording.FrameHeight)),
                        _ => SdfMeshDetail.Forced(level: (((int)frame.MeshDetail) - 1)),
                    })),
                drawsStatic: drawsStatic,
                layers: layers,
                motion: (distant ? SdfMeshMotion.Static : (HasFarStatic ? SdfMeshMotion.Dynamic : SdfMeshMotion.Any)),
                recording: in recording,
                share: ((distantDraw is { IsShare: true } share) ? share : null),
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
        m_staticOcclusion?.Submitted(slot: slot);
    }
    public void Completed(int slot) {
        m_shade.Completed(slot: slot);
        m_pick?.Completed(slot: slot);
        m_diagnosticPick?.Completed(slot: slot);
        m_occlusion?.Completed(slot: slot);
        m_staticOcclusion?.Completed(slot: slot);
    }
    public void Abandoned(int slot) {
        if (m_notedMeshRows) { _ = m_owner.NoteMeshRows(instance: m_context.Instance, stamp: m_previousMeshRows); m_notedMeshRows = false; }
        m_shade.Abandoned(slot: slot);
        m_pick?.Abandoned(slot: slot);
        m_diagnosticPick?.Abandoned(slot: slot);
        m_occlusion?.Abandoned(slot: slot);
        m_staticOcclusion?.Abandoned(slot: slot);
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
    // The texels a side of the cascade depth the pass binds, a cascade's output or the surface pass's input, which its pass
    // block and its fit follow, so a pass draws and reads the depth its graph allocated while a graph of another shadow
    // resolution builds; null for a pass that binds none. `distant` asks for the distant map's, whose size is its own.
    private int? BoundCascadeTexels(in RenderGraphPackageRecording recording, bool distant) {
        for (var port = 0; (port < m_outputs.Length); port++) {
            if (IsCascadeDepth(version: m_outputs[port], distant: distant)) {
                return ((int)recording.Outputs[port].Image.Width);
            }
        }
        for (var port = 0; (port < m_inputs.Length); port++) {
            if (IsCascadeDepth(version: m_inputs[port], distant: distant)) {
                return ((int)recording.Inputs[port].Image.Width);
            }
        }

        return null;
    }
    private static bool IsCascadeDepth(string version, bool distant) => ((CascadeIndexOf(version: version) >= 0) &&
        (distant == (version is SdfWorldPackage.Parts.ShadowDistantDepth or SdfWorldPackage.Parts.ShadowDistantExit)));
    // The cascade layer a storage is (its index in CascadeBindings), or -1 for any other storage.
    private static int CascadeIndexOf(string version) {
        for (var cascade = 0; (cascade < CascadeBindings.Length); cascade++) {
            if (string.Equals(a: CascadeBindings[cascade].Version, b: version, comparisonType: StringComparison.Ordinal)) {
                return cascade;
            }
        }

        return -1;
    }
    // Takes the view the instance resolved this frame (SdfWorldPasses.Follow). A counter change holds recording until
    // matching passes install. The ports and screens rebind for another residency's tables on this recording.
    private void Follow() {
        if (!m_owner.Follow(instance: m_context.Instance, view: ref m_view)) { return; }
        DeclareScreens(residency: m_view.Residency);
        foreach (var screens in m_screens) {
            Array.Clear(array: screens);
        }
    }
    private void DeclareScreens(SdfWorldResidency residency) => residency.DeclareScreens(declared: m_declaredScreens);
    // Binds the pass set and dispatches the part's kernel: the pyramid one invocation a texel of its first level, and the
    // surface and views passes one invocation a pixel of the render extent.
    private void RecordCompute(in RenderGraphPackageRecording recording, SdfWorldTables tables) {
        var slot = recording.Slot;
        var set = m_sets!.PassSet(slot: slot);
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var residency = m_view.Residency;

        BindPorts(
            recording: in recording,
            set: set,
            tables: tables
        );
        if (m_rateHistoryPort >= 0) {
            // History follows the last writer across submissions, independently of this descriptor set's slot.
            var history = recording.Inputs[m_rateHistoryPort].Buffer!;
            if (!ReferenceEquals(history, m_boundRateHistory[slot])) {
                tables.WriteInterfaceBuffer(buffer: history, layout: PassLayout, member: SdfWorldPackage.HistorySurface, set: set);
                m_boundRateHistory[slot] = history;
            }
        }
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
                    // The views kernels are the residency's shading selection, pinned for this recording's submission. A
                    // binned graph's plan is the one whose arguments its bins pass last wrote (ShadePlanOf).
                    m_shadePlan = (m_binned
                        ? m_owner.ShadePlanOf(instance: m_context.Instance, frame: residency.Frame!, width: recording.Width, height: recording.Height, screens: tables.PassValues.ScreenCount)
                        : SdfShadeDispatch.For(previous: m_shadePlan, frame: residency.Frame!, view: m_view.View, width: recording.Width, height: recording.Height,
                            screens: tables.PassValues.ScreenCount, glass: false, tier: residency.ShadeTier));
                    var selection = m_shade.RecordSelection(plan: m_shadePlan, residency: residency, slot: slot);

                    if (!m_shadePlan.Binned) {
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
                    // The filter runs no program: one job of views' groups, whose pushed ordinal its entry remaps as views'
                    // does. The bins' counts start from zero before any group adds to them.
                    m_shadePlan = SdfShadeDispatch.ForEngine(previous: m_shadePlan, width: recording.Width, height: recording.Height);
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
                    m_shadePlan.Record(pipeline: pipeline, recording: in recording);
                    return;
                }
            case SdfWorldPackage.Parts.ViewsBins: {
                    // Step zero (one group) turns the counts into offsets and writes every job's arguments at the plan's
                    // widths; step one (an invocation a tile) scatters the tiles once every offset is written.
                    var plan = m_owner.CommitShadePlan(instance: m_context.Instance, frame: residency.Frame!, width: recording.Width, height: recording.Height, screens: tables.PassValues.ScreenCount);
                    var bins = recording.Outputs[OutputIndexOf(member: SdfWorldPackage.ShadeTilesWritten)].Buffer!;
                    var pipeline = tables.Pipeline(kernel: SdfKernel.ViewsBins);
                    var tiles = (recording.Width.CeilingDivide(divisor: SdfVisibility.GroupEdgePixels) * recording.Height.CeilingDivide(divisor: SdfVisibility.GroupEdgePixels));
                    Span<byte> step = stackalloc byte[sizeof(uint)];

                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: step, value: 0u);
                    SdfFrameBlock.WriteShadeBins(block: recording.PassBlock, plan: plan);
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
    // Selects draws and levels on the GPU, then draws one indirect phase (two for opaque occlusion), pulling triangles
    // from the shared mesh region; a cascade uses the shadow pipeline into its depth,
    // cascadeTexels a side. A frame with no draws records the render pass alone, which clears the target.
    // The mesh set a frame slot draws through while the tables upload into their current ring slot. The tables' ring and
    // the frame slots' turn at different lengths, so a set per pair keeps each set's buffers those of one ring slot, and
    // a set never rewrites what it binds once written.
    private static int MeshSetOf(int slot, SdfWorldTables tables) =>
        ((slot * SdfWorldTables.FrameRingSize) + tables.CurrentSlot);
    // A cascade draws its depth (with the glass extinction that reads it) and its exit layer as `layers` says; a layer it
    // skips keeps what an earlier frame drew into it.
    // A share of a distant map redraw (`share`) draws into the back map instead, clearing it on the first share and loading
    // it on each later one, and the last share copies the back map into the map receivers read.
    // A far cascade with a static layer draws both layers each recording: the static draws into the static layer first when
    // `drawsStatic`, then a copy of the layer into the cascade, then the moving draws (`motion`) loading that copy, and the
    // glass extinction of every glass draw over the result.
    private void RecordMesh(in RenderGraphPackageRecording recording, SdfWorldTables tables, int cascadeTexels, in SdfMeshCull cull, in SdfMeshDetail detail, bool occlude, SdfFarLayers layers, System.Numerics.Vector2 jitter, SdfMeshMotion motion, bool drawsStatic = false, SdfDistantDraw? share = null) {
        var drawsDepth = (!IsShadowCascade || ((layers & SdfFarLayers.Depth) != 0));
        var drawsExit = ((layers & SdfFarLayers.Exit) != 0);
        var count = tables.MeshDrawCount;
        var slot = recording.Slot;
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var pipeline = (IsShadowCascade ? tables.ShadowPipeline : (IsGlass ? tables.GlassPipeline : tables.MeshPipeline));
        var meshSet = MeshSetOf(slot: slot, tables: tables);
        var target = recording.Outputs[0].Owned!;
        var framebuffer = m_framebuffers[0];
        var lateFramebuffer = ((m_lateFramebuffers.Length > 0) ? m_lateFramebuffers[0] : null);
        var occlusion = ((count > 0) ? m_occlusion : null);
        var twoPhase = (occlude && !IsGlass && (!IsShadowCascade || ((m_part == SdfWorldPackage.Parts.ShadowFar) && (m_inputs.Length > 0))));
        var entryLateFramebuffer = ((m_entryLateFramebuffers.Length > 0) ? m_entryLateFramebuffers[0] : lateFramebuffer);
        var shadowGlassFramebuffer = (HasShadowOptical ? m_shadowGlassFramebuffers[0] : null);
        var targetIndex = TargetIndexOf(target: target);

        m_recordedStatic = null;
        if (targetIndex >= 0) {
            var index = targetIndex;

            framebuffer = m_framebuffers[index];
            lateFramebuffer = ((m_lateFramebuffers.Length > 0) ? m_lateFramebuffers[index] : null);
            entryLateFramebuffer = ((m_entryLateFramebuffers.Length > 0) ? m_entryLateFramebuffers[index] : lateFramebuffer);
            m_recordedLayers = (index, (drawsDepth ? (PartDrawCount(tables: tables) == 0) : null), ((IsShadowCascade && drawsExit) ? (PartDrawCount(tables: tables) == 0) : null));
            if (HasShadowOptical) { shadowGlassFramebuffer = m_shadowGlassFramebuffers[index]; }
            if (share is { } drawing) {
                framebuffer = ((drawing.Slice == 0) ? m_backFramebuffers[index] : m_backLoadFramebuffers[index]);
                lateFramebuffer = ((drawing.Slice == 0) ? m_backExitFramebuffers[index] : m_backExitLoadFramebuffers[index]);
                // A share leaves the map receivers read as it was; the last copies the back map into it, which may
                // hold casters of earlier shares a later share's draw count no longer shows, so it is never clear.
                m_recordedLayers = (drawing.Completes ? (index, false, false) : null);
            }
        }

        // A frame holding none of the part's draws certifies its target clear (m_recordedLayers), so later empty frames
        // skip: it clears the whole allocation, since the render grid can later grow within it and a render pass clears
        // only its area. A cascade covers its whole depth, whatever the view's render extent.
        var area = ((PartDrawCount(tables: tables) == 0)
            ? new GpuPixelRect(Height: target.Height, Width: target.Width, X: 0, Y: 0)
            : new GpuPixelRect(
                Height: (IsShadowCascade ? ((uint)cascadeTexels) : recording.Height),
                Width: (IsShadowCascade ? ((uint)cascadeTexels) : recording.Width),
                X: 0,
                Y: 0
            ));

        // The static layer: drawn again when RecordFar says so, into its own depths through its own selection, then copied
        // into the cascade, which the moving draws below load rather than clear.
        if (HasFarStatic && (targetIndex >= 0)) {
            var staticDepth = m_staticDepths[targetIndex];
            var staticExit = m_staticExits[targetIndex];

            if (drawsStatic) {
                var staticSet = (meshSet + m_rasterSets);
                var staticSelection = ((count > 0) ? m_staticOcclusion : null);

                SelectDraws(cascadeTexels: cascadeTexels, cull: in cull, detail: in detail, drawsDepth: true, drawsExit: true, jitter: jitter, meshSet: staticSet, motion: SdfMeshMotion.Static,
                    occlusion: staticSelection, recording: in recording, share: null, tables: tables, target: staticDepth, twoPhase: twoPhase);
                DrawLayers(area: area, drawsDepth: true, drawsExit: true, entryFramebuffer: m_staticFramebuffers[targetIndex], entryLateFramebuffer: m_staticLateFramebuffers[targetIndex],
                    exitFramebuffer: m_staticExitFramebuffers[targetIndex], meshSet: staticSet, occlusion: staticSelection, pipeline: pipeline, recording: in recording, tables: tables, target: staticDepth, twoPhase: twoPhase);
                m_recordedStatic = targetIndex;
            }

            CopyDepth(recorder: recorder, commandBuffer: commandBuffer, source: staticDepth, destination: target);
            CopyDepth(recorder: recorder, commandBuffer: commandBuffer, source: staticExit, destination: recording.Outputs[1].Owned!);
            framebuffer = entryLateFramebuffer!;
            lateFramebuffer = m_exitLoadFramebuffers[targetIndex];
        }

        if (HasShadowOptical && drawsDepth) {
            var optical = recording.Outputs[2].Buffer!;

            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.FragmentShader,
                destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
            recorder.ClearStorageBuffer(commandBuffer, optical.BufferHandle, optical.SizeBytes);
            recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: optical.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.FragmentShader);
        }
        SelectDraws(cascadeTexels: cascadeTexels, cull: in cull, detail: in detail, drawsDepth: drawsDepth, drawsExit: drawsExit, jitter: jitter, meshSet: meshSet, motion: motion,
            occlusion: occlusion, recording: in recording, share: share, tables: tables, target: target, twoPhase: twoPhase);
        DrawLayers(area: area, drawsDepth: drawsDepth, drawsExit: drawsExit, entryFramebuffer: framebuffer, entryLateFramebuffer: entryLateFramebuffer, exitFramebuffer: lateFramebuffer,
            meshSet: meshSet, occlusion: occlusion, pipeline: pipeline, recording: in recording, tables: tables, target: target, twoPhase: twoPhase);
        if (HasShadowOptical && drawsDepth && (occlusion is not null)) {
            // Separate descriptors: early raster/selection must never bind the entry depth while it is an attachment.
            var glassSetIndex = (meshSet + (m_rasterSets * OcclusionGroups));
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
            RecordMeshPhase(commandBuffer: commandBuffer, pipeline: tables.ShadowGlassPipeline, recorder: recorder, set: glassSet, selection: occlusion, phase: 4);
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, imageHandle: target.ImageHandle,
                oldLayout: GpuImageLayout.ShaderReadOnly, newLayout: GpuImageLayout.DepthAttachment,
                sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: GpuStage.FragmentShader,
                destinationAccessMask: GpuAccess.DepthAttachmentWrite, destinationStageMask: GpuStage.FragmentTests);
        }
        if ((share is { Completes: true }) && (targetIndex >= 0)) {
            CopyDepth(recorder: recorder, commandBuffer: commandBuffer, source: m_backDepths[targetIndex], destination: target);
            CopyDepth(recorder: recorder, commandBuffer: commandBuffer, source: m_backExits[targetIndex], destination: recording.Outputs[1].Owned!);
        }
    }
    // Writes a mesh set's tables and counters, stages the selection's draws into it and records the selection's first
    // step: the set and its two selection sets are one group's (meshSet), and `target` is the depth the selection's second
    // phase samples.
    private void SelectDraws(in RenderGraphPackageRecording recording, SdfWorldTables tables, SdfMeshOcclusion? occlusion, int meshSet, IGpuImage target, int cascadeTexels, in SdfMeshCull cull,
        in SdfMeshDetail detail, bool twoPhase, bool drawsDepth, bool drawsExit, System.Numerics.Vector2 jitter, SdfMeshMotion motion, SdfDistantDraw? share) {
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

        if (occlusion is null) {
            return;
        }

        var frame = m_view.Residency.Frame!;
        var slot = recording.Slot;

        occlusion.Stage(tables: tables, count: tables.MeshDrawCount, height: (IsShadowCascade ? (uint)cascadeTexels : recording.RenderHeight), width: (IsShadowCascade ? (uint)cascadeTexels : recording.RenderWidth), slot: slot, cull: in cull, detail: in detail, held: tables.HoldsFrame, glass: IsGlass, occlude: twoPhase, splitExit: IsShadowCascade, skipsDepth: !drawsDepth, skipsExit: (IsShadowCascade && !drawsExit),
            camera: frame.Views[Math.Min(val1: m_view.View, val2: (frame.Views.Count - 1))].Camera,
            receiverWidth: ((IsShadowCascade && twoPhase) ? recording.Inputs[0].Image.Width : 0),
            receiverHeight: ((IsShadowCascade && twoPhase) ? recording.Inputs[0].Image.Height : 0), jitter: jitter, motion: motion,
            slice: Math.Max(val1: (share?.Slice ?? 0), val2: 0), slices: (share?.Slices ?? 1));
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
                target: (!twoPhase ? tables.SampledFiller.ImageViewHandle : ((phase != 0) ? target.ImageViewHandle :
                    (IsShadowCascade ? recording.Inputs[0].Image.ImageViewHandle : tables.SampledFiller.ImageViewHandle))));
        }
        occlusion.RecordEarly(commandBuffer: recording.CommandBuffer, pipeline: tables.OcclusionPipeline, recorder: recording.Recorder, set: m_occlusionSets[(meshSet * OcclusionSetsPerMesh)]);
    }
    // Draws a selection's entry depth into `target` in its two phases, the second after the selection's late step, and a
    // cascade's exit layer after the entry is final, each in the framebuffer `layers` names; a layer it skips keeps what an
    // earlier frame drew into it.
    private void DrawLayers(in RenderGraphPackageRecording recording, SdfWorldTables tables, SdfMeshOcclusion? occlusion, int meshSet, IGpuPipeline pipeline, IGpuImage target, GpuPixelRect area,
        IGpuFramebuffer entryFramebuffer, IGpuFramebuffer? entryLateFramebuffer, IGpuFramebuffer? exitFramebuffer, bool twoPhase, bool drawsDepth, bool drawsExit) {
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var set = m_meshSets[meshSet];

        if (drawsDepth) {
            recorder.BeginRenderPass(
                area: area,
                commandBufferHandle: commandBuffer,
                framebuffer: entryFramebuffer
            );
            if (occlusion is not null) {
                // The first phase: what the previous frame found visible, and every draw no host box bounds.
                RecordMeshPhase(commandBuffer: commandBuffer, phase: 0, pipeline: pipeline, recorder: recorder, selection: occlusion, set: set);
            }
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
        }

        if (twoPhase && drawsDepth && (occlusion is not null) && (occlusion.CandidateCount > 0)) {
            // Finish entry depth before any exit/peel or glass consumer. Both camera and light use this one path.
            occlusion.RecordLate(commandBuffer: commandBuffer, pipeline: tables.OcclusionPipeline, recorder: recorder, set: m_occlusionSets[((meshSet * OcclusionSetsPerMesh) + 1)], target: target.ImageHandle);
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: entryLateFramebuffer!);
            RecordMeshPhase(commandBuffer: commandBuffer, phase: 1, pipeline: pipeline, recorder: recorder, selection: occlusion, set: set);
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
        }
        if (IsShadowCascade && drawsExit) {
            // Exit solids use front-face culling; sheets and clipped draws use the unculled pipeline.
            recorder.BeginRenderPass(area: area, commandBufferHandle: commandBuffer, framebuffer: exitFramebuffer!);
            if (occlusion is not null) {
                RecordMeshPhase(commandBuffer: commandBuffer, pipeline: tables.ShadowExitPipeline, recorder: recorder, set: set, selection: occlusion, phase: 2);
                RecordMeshPhase(commandBuffer: commandBuffer, pipeline: tables.ShadowPipeline, recorder: recorder, set: set, selection: occlusion, phase: 3);
            }
            recorder.EndRenderPass(commandBufferHandle: commandBuffer);
        }
    }
    // Copies one whole depth attachment into another of its format and extent, each left in its attachment layout as the
    // planner expects it after the pass.
    private static void CopyDepth(IGpuRecorder recorder, nint commandBuffer, IGpuImage source, IGpuImage destination) {
        recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, imageHandle: source.ImageHandle,
            oldLayout: GpuImageLayout.DepthAttachment, newLayout: GpuImageLayout.TransferSource,
            sourceAccessMask: GpuAccess.DepthAttachmentWrite, sourceStageMask: GpuStage.FragmentTests,
            destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
        recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, imageHandle: destination.ImageHandle,
            oldLayout: GpuImageLayout.DepthAttachment, newLayout: GpuImageLayout.TransferDestination,
            sourceAccessMask: GpuAccess.DepthAttachmentWrite, sourceStageMask: GpuStage.FragmentTests,
            destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
        recorder.CopyImage(commandBufferHandle: commandBuffer, sourceImageHandle: source.ImageHandle, destinationImageHandle: destination.ImageHandle, width: source.Width, height: source.Height);
        recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, imageHandle: source.ImageHandle,
            oldLayout: GpuImageLayout.TransferSource, newLayout: GpuImageLayout.DepthAttachment,
            sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.DepthAttachmentWrite, destinationStageMask: GpuStage.FragmentTests);
        recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, imageHandle: destination.ImageHandle,
            oldLayout: GpuImageLayout.TransferDestination, newLayout: GpuImageLayout.DepthAttachment,
            sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.DepthAttachmentWrite, destinationStageMask: GpuStage.FragmentTests);
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
        if (m_rateHistoryPort >= 0) { m_boundRateHistory[slot] = null; }
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
