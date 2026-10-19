using System.Buffers.Binary;
using Puck.Hosting;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// One pass of an sdf.world instance (SdfWorldPasses): a part of the package's fragment, recorded into the instance's
// command buffer for the pass. Every part writes its pass block (SdfFrameBlock): the view's camera, the frame's levers,
// light count and curvature shading, the shadow cascades, the irradiance volume and the world values. Every compute part
// binds the residency's World set of the ring slot the frame's upload wrote, which holds its tables (the lights and the
// sky among them), and the world interface's pass group: the fragment storages its ports bind and, at every member its
// ports do not, a dummy of the residency's; the node's work counters for the frame slot, whose row it writes into its pass
// block; and the screens, whose host images are rewritten every frame. The mesh part draws the frame's mesh draws into
// its target through the mesh pipeline and each shadow cascade into its depth alone through the shadow pipeline, each
// with a set of its own per frame slot binding its pass block. A recorder records no barrier: the planner's are the instance's, and the node's orders the work counters.
internal sealed class SdfWorldPassRecorder : IRenderGraphPackageRecorder, IRenderGraphPackageReadback {
    // Views attribute analytic sky lighting by layer.
    public IReadOnlyList<string> WorkDetails(in FrameContext context) => ((m_part == SdfWorldPackage.Parts.Views) ? m_view.Residency.SkyDetails.Labels : []);

    // The world interface's scratch buffer members, each bound to the dummy unless a port binds it.
    private static readonly string[] ScratchMembers = [
        SdfWorldPackage.InstanceMasks,
        SdfWorldPackage.InstanceMasksWritten,
        SdfWorldPackage.VisibilityRecords,
        SdfWorldPackage.VisibilityRecordsWritten,
        SdfWorldPackage.ReactivityWritten,
        SdfWorldPackage.MeshBakeSamplesWritten,
    ];
    private static readonly uint MeshShadowNearBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowNear);
    private static readonly uint MeshShadowFarBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowFar);
    private static readonly uint MeshShadowSecondBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowSecond);
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
    // The bake pass's readback: per frame slot a buffer, the lattice its latest recording dispatched and the fence that
    // completes it.
    private readonly BakeSlot[] m_bakeSlots = [];
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
    // The buffers each mesh set binds now: its mesh region, its dynamic transforms and its work counters.
    private readonly IGpuBuffer?[][] m_meshSetBuffers = [];
    private readonly IReadOnlyList<IGpuImage> m_meshTargets = [];
    private readonly IGpuFramebuffer[] m_framebuffers = [];
    // Whether each instance of the mesh part's or a cascade's target has been drawn or cleared once.
    private readonly bool[] m_targetsWritten = [];
    private readonly byte[] m_meshPushedIndex = new byte[GpuPipelineLayoutDescription.PushIndexBytes];

    private bool m_disposed;
    private bool m_sourceTainted;
    private bool m_readsHistory;

    private readonly SdfWorldPickReadback? m_pick;

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

        if (m_part == SdfWorldPackage.Parts.Bake) {
            m_bakeSlots = new BakeSlot[slots];
            for (var slot = 0; (slot < slots); slot++) { m_bakeSlots[slot] = new BakeSlot(); }
            owner.ReadBakes(reader: this, reads: true);
        }
        if (!IsMesh) {
            m_sets = new RenderGraphPackageSets(
                context: context,
                groupLayoutHandles: tables.Pipeline(kernel: SdfKernel.Surface).GroupLayoutHandles,
                groups: groups
            );

            if (string.Equals(a: m_part, b: SdfWorldPackage.Parts.Views, comparisonType: StringComparison.Ordinal)) {
                m_pick = new SdfWorldPickReadback(picker: owner.PickerOf(instance: context.Instance), context: context);
            }

            return;
        }

        // The mesh part draws through the mesh interface's layout, whose one pass group holds the pass block the world
        // interface lays out and the mesh region: a set per frame slot and tables ring slot from a pool of its own, its block
        // bound to the frame slot's pass block buffer, and a framebuffer over each instance of its target and depth, or of a
        // cascade's depth alone.
        var bindings = context.Services.Bindings;
        var sizes = default(GpuDescriptorPoolSizes);
        var sets = (slots * SdfWorldTables.FrameRingSize);

        for (var set = 0; (set < sets); set++) {
            sizes += GpuDescriptorPoolSizes.ForGroups(groups: SdfWorldTables.PipelineLayouts.Mesh.Groups);
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

            for (var set = 0; (set < sets); set++) {
                var slot = (set / SdfWorldTables.FrameRingSize);

                m_meshSetBuffers[set] = new IGpuBuffer?[3];
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
        } catch {
            foreach (var framebuffer in m_framebuffers) {
                framebuffer?.Dispose();
            }

            bindings.DestroyPool(poolHandle: m_meshPool);

            throw;
        }
    }

    // Whether the part rasterizes the frame's mesh draws through the mesh pipeline: the mesh pass, or one of the shadow
    // cascades, which draw from the cascade's light camera.
    private bool IsMesh => (m_part is SdfWorldPackage.Parts.Mesh || IsShadowCascade);
    private bool TargetsWritten => Array.TrueForAll(array: m_targetsWritten, match: static written => written);
    private bool IsShadowCascade => (m_part is SdfWorldPackage.Parts.ShadowNear or SdfWorldPackage.Parts.ShadowFar or SdfWorldPackage.Parts.ShadowSecond);

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_pick?.Dispose();
        if (m_bakeSlots.Length > 0) { m_owner.ReadBakes(reader: this, reads: false); }
        foreach (var slot in m_bakeSlots) {
            slot.Batch?.Bake?.Abandon(batch: slot.Batch);
            slot.Buffer?.Dispose();
        }

        foreach (var framebuffer in m_framebuffers) {
            framebuffer.Dispose();
        }

        if (m_meshPool != 0) {
            m_context.Services.Bindings.DestroyPool(poolHandle: m_meshPool);
        }

        m_owner.Unhold(residency: m_view.Residency);
        m_view.Residency.Release();
    }
    // A part skips a frame whose work it would not do, recording neither its work nor its planned barriers: the mask and
    // bake passes run only while the residency's bake dispatches a lattice this frame, in whichever of its views the
    // render graph records first (the mask then selects the lattice's instances, as the bake's walk reads them), so a
    // residency whose first view is out of view still bakes, the mesh part a frame that draws no mesh, and a shadow cascade
    // a frame with no draw or no cascade of its own. A frame that draws no mesh still records while an instance of the
    // part's target has never been written, as before the first bake lands: its render pass clears the target the surface
    // pass samples, which would otherwise hold no contents at all. No other part walks the field, and none skips.
    public bool Skips(in FrameContext context) {
        switch (m_part) {
            case SdfWorldPackage.Parts.Mask:
            case SdfWorldPackage.Parts.Bake: {
                Follow();
                m_owner.Begin(residency: m_view.Residency);
                PollBake();
                _ = m_view.Residency.Submit(context: in context);
                return !ClaimsBake(batch: out _);
            }
            case SdfWorldPackage.Parts.Mesh: {
                Follow();
                m_owner.Begin(residency: m_view.Residency);
                return ((m_view.Residency.Submit(context: in context).MeshDrawCount == 0) && TargetsWritten);
            }
            case SdfWorldPackage.Parts.ShadowNear:
            case SdfWorldPackage.Parts.ShadowFar:
            case SdfWorldPackage.Parts.ShadowSecond: {
                Follow();
                m_owner.Begin(residency: m_view.Residency);
                var tables = m_view.Residency.Submit(context: in context);
                var cascades = SdfMeshShadowCascades.Fit(frame: m_view.Residency.Frame!, view: m_view.View);

                return ((cascades.Count == 0) || ((tables.MeshDrawCount == 0) && TargetsWritten) ||
                    ((m_part == SdfWorldPackage.Parts.ShadowSecond) && (cascades.SecondPenumbraSlope <= 0f)));
            }
            default:
                return false;
        }
    }
    // The bake's passes record whenever they do not skip: a pending bake dispatches a lattice each frame.
    public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) => ((m_part is SdfWorldPackage.Parts.Bake or SdfWorldPackage.Parts.Mask)
        ? null : m_owner.SignatureOf(instance: m_context.Instance, part: m_part, temporal: m_temporal, context: in context));

    public void Submitted() {
        if (m_part == SdfWorldPackage.Parts.Views) {
            m_owner.SubmittedTaint(m_context.Instance, m_sourceTainted, m_readsHistory);
        }
        if ((m_part == SdfWorldPackage.Parts.Views) && !m_resolved) { m_owner.MarkSampleRendered(instance: m_context.Instance); }
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        Follow();

        var residency = m_view.Residency;

        m_owner.Begin(residency: residency);

        var tables = residency.Submit(context: recording.Context);
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
        var temporal = m_owner.TemporalOf(
            instance: m_context.Instance, view: m_view, width: recording.FrameWidth, height: recording.FrameHeight, debug: tables.PassValues.DebugMode, temporal: m_temporal, unread: recording.UnreadFrames, renderWidth: width, renderHeight: height
        );

        if (m_part == SdfWorldPackage.Parts.Views) {
            m_sourceTainted = (SdfWorldPasses.SourceTainted(frame: frame, tables: tables) || (recording.Reads is { Tainted: true }));
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
        }

        SdfFrameBlock.WriteMeshIrradiance(block: recording.PassBlock, volume: residency.MeshIrradianceParameters);

        var cascades = SdfMeshShadowCascades.Fit(frame: frame, texels: BoundCascadeTexels(recording: in recording), view: view);

        SdfFrameBlock.WriteMeshShadows(block: recording.PassBlock, cascades: in cascades);
        if (IsShadowCascade) {
            SdfFrameBlock.WriteShadowCamera(block: recording.PassBlock, camera: cascades[m_part switch {
                SdfWorldPackage.Parts.ShadowNear => 0,
                SdfWorldPackage.Parts.ShadowFar => 1,
                _ => 2,
            }], texels: cascades.Texels);
        }
        if (m_part is SdfWorldPackage.Parts.Mask or SdfWorldPackage.Parts.Bake) {
            if (!ClaimsBake(batch: out var batch)) {
                return RenderGraphPackageOutcome.Drew;
            }
            SdfFrameBlock.WriteMeshBake(batch: batch, block: recording.PassBlock);
            if (m_part == SdfWorldPackage.Parts.Bake) {
                PollBake();

                var target = m_bakeSlots[recording.Slot];

                // A batch recorded but never copied answers nothing; its groups dispatch their lattices again.
                if (target.Record || (target.Fence is not null)) {
                    target.Batch?.Bake?.Abandon(batch: target.Batch);
                    target.Fence = null;
                }
                target.Record = true;
                target.Batch = batch;
                residency.MeshPathBake.Record(batch: batch);
                target.Version = recording.Outputs[0].Version;
            }
        }

        if (IsMesh) {
            RecordMesh(
                cascadeTexels: cascades.Texels,
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
            return m_pick.Take(index: index, readback: out readback, slot: slot);
        }
        if ((m_bakeSlots.Length > 0) && (index == 0) && m_bakeSlots[slot].Record) {
            var target = m_bakeSlots[slot];
            var bytes = (ulong)(target.Batch!.Samples * SdfWorldPackage.MeshBakeSampleWords * sizeof(uint));

            target.Buffer ??= m_context.Services.BufferFactory.CreateReadback(
                name: new GpuObjectName(detail: "bake", index: slot, owner: m_context.Instance, part: m_context.Pass),
                sizeBytes: (((ulong)SdfWorldPackage.MeshBakeMaxSamples) * SdfWorldPackage.MeshBakeSampleWords * sizeof(uint))
            );
            target.Record = false;
            target.Submit = true;
            readback = new RenderGraphBufferReadback(Destination: target.Buffer, SizeBytes: bytes, SourceOffsetBytes: 0, Version: target.Version!);
            return true;
        }
        readback = default;
        return false;
    }

    public ulong ReadbackBytes => ((m_pick?.ReadbackBytes ?? 0UL) + (ulong)m_bakeSlots.Sum(selector: static slot => (long)(slot.Buffer?.SizeBytes ?? 0UL)));

    public void Submitted(int slot, IGpuSubmissionFence fence) {
        if ((m_bakeSlots.Length > 0) && m_bakeSlots[slot].Submit) {
            m_bakeSlots[slot].Submit = false;
            m_bakeSlots[slot].Fence = fence;
        }
        m_pick?.Submitted(fence: fence, slot: slot);
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
    private static bool IsCascadeDepth(string version) =>
        (version is SdfWorldPackage.Parts.ShadowNearDepth or SdfWorldPackage.Parts.ShadowFarDepth or SdfWorldPackage.Parts.ShadowSecondDepth);
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
    // Binds the pass set and dispatches the part's kernel: the bake mask one group a lattice, the bake one invocation a
    // sample, and the surface and views passes one invocation a pixel of the render extent.
    private void RecordCompute(in RenderGraphPackageRecording recording, SdfWorldTables tables) {
        var slot = recording.Slot;
        var set = m_sets!.PassSet(slot: slot);
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var pipeline = tables.Pipeline(kernel: m_part switch {
            SdfWorldPackage.Parts.Mask => SdfKernel.MeshBakeMask,
            SdfWorldPackage.Parts.Bake => SdfKernel.MeshBake,
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
            SdfWorldPackage.Parts.Mask => ((uint)SdfWorldPackage.MeshBakeBatch, 1u),
            SdfWorldPackage.Parts.Bake => (((uint)(m_bakeSlots[recording.Slot].Batch!.Samples + 63) / 64u), 1u),
            _ => (((recording.Width + (SdfVisibility.GroupEdgePixels - 1)) / SdfVisibility.GroupEdgePixels),
                ((recording.Height + (SdfVisibility.GroupEdgePixels - 1)) / SdfVisibility.GroupEdgePixels)),
        };

        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            groupCountX: x,
            groupCountY: y,
            groupCountZ: 1
        );
    }
    // Draws the frame's mesh draws into the instance's target, one draw call a draw, pulling their triangles from the mesh
    // region, through the mesh pipeline; a cascade draws every mesh through the shadow pipeline into its depth,
    // cascadeTexels a side. A frame with no draws records the render pass alone, which clears the target.
    // The mesh set a frame slot draws through while the tables upload into their current ring slot. The tables' ring and
    // the frame slots' turn at different lengths, so a set per pair keeps each set's buffers those of one ring slot, and
    // a set never rewrites what it binds once written.
    private static int MeshSetOf(int slot, SdfWorldTables tables) =>
        ((slot * SdfWorldTables.FrameRingSize) + tables.CurrentSlot);
    private void RecordMesh(in RenderGraphPackageRecording recording, SdfWorldTables tables, int cascadeTexels) {
        var draws = tables.MeshDraws;
        var count = ((draws is null) ? 0u : tables.MeshDrawCount);
        var slot = recording.Slot;
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var pipeline = (IsShadowCascade ? tables.ShadowPipeline : tables.MeshPipeline);
        var meshSet = MeshSetOf(slot: slot, tables: tables);
        var set = m_meshSets[meshSet];
        var target = recording.Outputs[0].Owned!;
        var framebuffer = m_framebuffers[0];

        for (var index = 0; (index < m_meshTargets.Count); index++) {
            if (ReferenceEquals(
                objA: m_meshTargets[index],
                objB: target
            )) {
                framebuffer = m_framebuffers[index];
                m_targetsWritten[index] = true;
            }
        }

        if (count > 0) {
            tables.WriteMeshTables(
                bound: m_meshSetBuffers[meshSet],
                set: set,
                slot: tables.CurrentSlot
            );
            tables.WriteMeshWorkCounters(
                bound: ref m_meshSetBuffers[meshSet][2],
                counters: WorkCountersOf(recording: in recording).Buffer,
                set: set
            );
        }
        // A cascade covers its whole depth, whatever the view's render extent.
        recorder.BeginRenderPass(
            area: new GpuPixelRect(
                Height: (IsShadowCascade ? ((uint)cascadeTexels) : recording.Height),
                Width: (IsShadowCascade ? ((uint)cascadeTexels) : recording.Width),
                X: 0,
                Y: 0
            ),
            commandBufferHandle: commandBuffer,
            framebuffer: framebuffer
        );
        if (count > 0) {
            RecordMeshDraws(
                commandBuffer: commandBuffer,
                count: count,
                draws: draws!,
                pipeline: pipeline,
                recorder: recorder,
                set: set
            );
        }
        recorder.EndRenderPass(commandBufferHandle: commandBuffer);
    }
    // Binds a pipeline and the pass set, then draws every draw.
    private void RecordMeshDraws(uint count, IReadOnlyList<SdfMeshDraw> draws, IGpuPipeline pipeline, IGpuRecorder recorder, nint commandBuffer, nint set) {
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

        for (var draw = 0u; (draw < count); draw++) {
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: m_meshPushedIndex,
                value: SdfKernelInterfaces.MeshPushedIndex(
                    draw: draw,
                    view: 0u
                )
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: commandBuffer,
                data: m_meshPushedIndex,
                offset: 0,
                pipelineLayoutHandle: pipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Vertex | GpuShaderStage.Fragment
            );
            recorder.Draw(
                commandBufferHandle: commandBuffer,
                parameters: new GpuDrawParameters(
                    instanceCount: 1,
                    vertexCount: ((uint)draws[((int)draw)].Mesh.Indices.Length)
                )
            );
        }
    }
    // Writes the slot's counter buffer into its pass set when it is not the one the set binds, compared by the buffer
    // object, since a handle value may name a new object once the old one is released.
    private void BindCounters(in RenderGraphPackageRecording recording, nint set, SdfWorldTables tables) {
        var counters = WorkCountersOf(recording: in recording).Buffer;

        if (ReferenceEquals(objA: m_boundCounters[recording.Slot], objB: counters)) {
            return;
        }

        tables.WriteWorldBuffer(buffer: counters, member: ShaderWorkCounters.Buffer, set: set);
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
        var output = tables.StorageFiller.ImageViewHandle;
        var meshVisibility = tables.SampledFiller.ImageViewHandle;
        var shadowNear = tables.SampledFiller.ImageViewHandle;
        var shadowFar = tables.SampledFiller.ImageViewHandle;
        var shadowSecond = tables.SampledFiller.ImageViewHandle;

        foreach (var member in ScratchMembers) {
            tables.WriteWorldBuffer(buffer: tables.DummyBuffer, member: member, set: set);
        }

        for (var port = 0; (port < m_inputs.Length); port++) {
            var name = m_inputs[port];
            var bound = recording.Inputs[port];

            if (bound.Buffer is { } buffer) {
                if (ReadMemberOf(version: name) is { } member) {
                    tables.WriteWorldBuffer(buffer: buffer, member: member, set: set);
                }
            } else if (string.Equals(
                a: name,
                b: SdfWorldPackage.Parts.MeshTarget,
                comparisonType: StringComparison.Ordinal
            )) {
                meshVisibility = bound.Image.ImageViewHandle;
            } else if (name == SdfWorldPackage.Parts.ShadowNearDepth) {
                shadowNear = bound.Image.ImageViewHandle;
            } else if (name == SdfWorldPackage.Parts.ShadowFarDepth) {
                shadowFar = bound.Image.ImageViewHandle;
            } else if (name == SdfWorldPackage.Parts.ShadowSecondDepth) {
                shadowSecond = bound.Image.ImageViewHandle;
            }
        }
        for (var port = 0; (port < m_outputs.Length); port++) {
            var name = m_outputs[port];
            var bound = recording.Outputs[port];

            if (bound.Buffer is { } buffer) {
                if (WrittenMemberOf(version: name) is { } member) {
                    tables.WriteWorldBuffer(buffer: buffer, member: member, set: set);
                }
            } else if (bound.Kind == ShaderPipelineResourceKind.Image) {
                output = bound.Image.ImageViewHandle;
            }
        }

        m_boundCounters[slot] = null;
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
        bindings.WriteSampledImage(arrayElement: 0, binding: MeshShadowNearBinding, descriptorSetHandle: set, imageViewHandle: shadowNear);
        bindings.WriteSampledImage(arrayElement: 0, binding: MeshShadowFarBinding, descriptorSetHandle: set, imageViewHandle: shadowFar);
        bindings.WriteSampledImage(arrayElement: 0, binding: MeshShadowSecondBinding, descriptorSetHandle: set, imageViewHandle: shadowSecond);
        Array.Clear(array: m_screens[slot]);
        m_portTables[slot] = tables;
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
        SdfWorldPackage.Parts.InstanceMasks => SdfWorldPackage.InstanceMasks,
        SdfWorldPackage.Parts.Visibility => SdfWorldPackage.VisibilityRecords,
        _ => null,
    };
    // The member a pass writes a fragment buffer through, or null for one it writes through no member.
    private static string? WrittenMemberOf(string version) => version switch {
        SdfWorldPackage.Parts.InstanceMasks => SdfWorldPackage.InstanceMasksWritten,
        SdfWorldPackage.Parts.Visibility => SdfWorldPackage.VisibilityRecordsWritten,
        SdfWorldPackage.Parts.Reactivity => SdfWorldPackage.ReactivityWritten,
        SdfWorldPackage.Parts.BakeSamples => SdfWorldPackage.MeshBakeSamplesWritten,
        _ => null,
    };

    // Takes the frame's bake batch for this view's instance: the first instance whose mask or bake pass asks claims it,
    // and every other view of the residency skips its own.
    private bool ClaimsBake([System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMeshBakeBatch? batch) {
        var bake = m_view.Residency.MeshPathBake;

        if (!bake.TryTakeForFrame(batch: out batch, frame: m_view.Residency.PackageFrame)) {
            return false;
        }

        return batch.Claim(instance: m_context.Instance);
    }
    // Reads every bake slot whose submission completed and answers the bake that dispatched its batch with its samples.
    internal void PollBake() {
        foreach (var slot in m_bakeSlots) {
            if ((slot.Fence is not { } fence) || !fence.IsSignaled) { continue; }
            slot.Fence = null;

            if (slot.Batch is not { } batch) { continue; }

            var bytes = (batch.Samples * SdfWorldPackage.MeshBakeSampleWords * sizeof(uint));

            if (slot.Bytes.Length < bytes) { slot.Bytes = new byte[bytes]; }
            slot.Buffer!.Read(destination: slot.Bytes.AsSpan(length: bytes, start: 0));
            slot.Batch = null;
            batch.Bake?.Complete(batch: batch, samples: slot.Bytes.AsSpan(length: bytes, start: 0));
        }
    }

    // One frame slot's bake readback: the buffer the samples are copied into, the batch its latest recording dispatched,
    // and the fence of the submission that copies them.
    private sealed class BakeSlot {
        public IGpuReadbackBuffer? Buffer { get; set; }
        public byte[] Bytes { get; set; } = [];
        public bool Record { get; set; }
        public bool Submit { get; set; }
        public IGpuSubmissionFence? Fence { get; set; }
        public SdfMeshBakeBatch? Batch { get; set; }
        public string? Version { get; set; }
    }
}
