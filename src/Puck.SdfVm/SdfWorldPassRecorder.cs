using System.Buffers.Binary;
using Puck.Hosting;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// One pass of an sdf.world instance (SdfWorldPasses): a part of the package's fragment, recorded into the instance's
// command buffer for the pass. Every part writes its pass block (SdfFrameBlock): the view's camera, the frame's levers,
// light count and curvature shading, and the world values. Every compute part binds the residency's World set of the ring
// slot the frame's upload wrote, which holds its tables (the lights and the sky among them), and the world interface's pass group: the fragment storages its ports bind and, at every
// member its ports do not, a dummy of the residency's; the node's work counters for the frame slot, whose row it writes
// into its pass block; and the screens, whose host images are rewritten every frame. The mesh part draws the frame's
// mesh draws into its target through the mesh pipeline, with a set of its own per frame slot binding its pass block. A
// recorder records no barrier: the planner's are the instance's, and the node's orders the work counters.
internal sealed class SdfWorldPassRecorder : IRenderGraphPackageRecorder, IRenderGraphPackageReadback {
    private const uint WorkgroupEdge = 8;

    // Views attribute analytic sky lighting by layer; the shadow pass attributes secondary pixels by decision.
    public IReadOnlyList<string> WorkDetails(in FrameContext context) => m_part switch {
        SdfWorldPackage.Parts.Views or SdfWorldPackage.Parts.Receiver => m_view.Residency.SkyDetails.Labels,
        SdfWorldPackage.Parts.Shadow => SdfShadowDecisions.Labels,
        _ => [],
    };

    // The world interface's scratch buffer members, each bound to the dummy unless a port binds it.
    private static readonly string[] ScratchMembers = [
        SdfWorldPackage.InstanceMasks,
        SdfWorldPackage.InstanceMasksWritten,
        SdfWorldPackage.SegmentTapes,
        SdfWorldPackage.SegmentTapesWritten,
        SdfWorldPackage.Tiles,
        SdfWorldPackage.TilesWritten,
        SdfWorldPackage.CullBounds,
        SdfWorldPackage.CullBoundsWritten,
        SdfWorldPackage.ViewsArgsWritten,
        SdfWorldPackage.VisibilityRecords,
        SdfWorldPackage.VisibilityRecordsWritten,
        SdfWorldPackage.ReactivityWritten,
        SdfWorldPackage.ShadowHistory,
        SdfWorldPackage.ShadowHistoryWritten,
        SdfWorldPackage.IndirectCache,
        SdfWorldPackage.IndirectCacheWritten,
        SdfWorldPackage.IndirectPickWritten,
        SdfWorldPackage.IndirectDeferredWritten,
        SdfWorldPackage.IndirectAnswer,
        SdfWorldPackage.IndirectAnswerWritten,
        SdfWorldPackage.IndirectLightDepth,
        SdfWorldPackage.IndirectLightDepthWritten,
        SdfWorldPackage.MeshBakeSamplesWritten,
    ];
    private static readonly uint MeshShadowNearBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowNear);
    private static readonly uint MeshShadowFarBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshShadowFar);
    private static readonly uint OutputBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.Output);
    private static readonly uint MeshVisibilityBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.MeshVisibility);
    private static readonly uint IncomingVisibilityBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.IncomingVisibility);
    private static readonly uint IncomingVisibilityWrittenBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.IncomingVisibilityWritten);
    private static readonly uint ScreenSourcesBinding = SdfWorldTables.WorldBinding(member: SdfWorldPackage.ScreenSources);

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly string[] m_inputs;
    private readonly string[] m_outputs;
    private readonly SdfWorldPasses m_owner;
    private readonly string m_part;
    // Whether the pass belongs to the temporal fragment (SdfWorldPackage.TemporalFragment).
    private readonly bool m_temporal;
    // Whether the pass belongs to the baked-mesh path's fragment (SdfWorldPackage.MeshPathFragment): primary records the
    // mesh surface kernel, the passes that march the field skip, and the mask and bake passes run the residency's bake.
    private readonly bool m_meshPath;
    // The bake pass's readback: per frame slot a buffer, the lattice its latest recording dispatched and the fence that
    // completes it.
    private readonly BakeSlot[] m_bakeSlots = [];
    private readonly bool m_resolved;
    // Whether the planned graph allocates the incoming handoff image (a policy allowing fades). Without it the passes
    // bind the tables' fillers and read a zero fade count, whatever the live frame's handoffs.
    private readonly bool m_incoming;

    // Whether the receiver records the comparison receiver this frame: its view selects a comparison method.
    private bool m_comparison;
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
    private readonly (nint Read, nint Write)[] m_shadowPorts;

    private readonly SdfShadowHistory m_shadowHistory = new();

    private readonly SdfLights[] m_shadowFrames;
    private readonly uint[] m_shadowRebuilt;

    private int m_shadowRecordingSlot;

    // The mesh part's pool, its set per frame slot, and a framebuffer over each instance of its target and depth.
    private readonly nint m_meshPool;

    private readonly nint[] m_meshSets = [];
    private readonly IReadOnlyList<IGpuImage> m_meshTargets = [];
    private readonly IGpuFramebuffer[] m_framebuffers = [];
    private readonly byte[] m_meshPushedIndex = new byte[GpuPipelineLayoutDescription.PushIndexBytes];
    // The mesh part's choice among each baked placement's mesh and impostor draws, and per frame slot which tables' impostor
    // depth atlas, at which revision, its set last bound.
    private SdfMeshLodSelector m_lod = new();
    private readonly SdfWorldTables?[] m_impostorDepthTables = [];
    private readonly long[] m_impostorDepthRevisions = [];
    private bool[] m_recorded = [];

    private bool m_disposed;
    private bool m_sourceTainted;
    private bool m_readsHistory;
    private SdfViewLightingSource m_recordedLighting;
    private bool m_lightingSubmitted;

    private readonly SdfWorldPickReadback? m_pick;
    private readonly SdfIndirectCache.LightViewBank? m_lightBank;

    public SdfWorldPassRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view, SdfIndirectCache.LightViewBank? lightBank) {
        m_context = context;
        m_owner = owner;
        m_view = view;
        m_lightBank = lightBank;
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
        m_meshPath = fragment.Passes.Any(predicate: static pass => (pass.Name == SdfWorldPackage.MeshParts.Bake));
        m_resolved = m_outputs.Contains(value: SdfWorldPackage.CurrentColor);
        var incoming = context.Inputs.Concat(second: context.Outputs).SingleOrDefault(predicate: resource => (LocalName(resource: resource) == SdfWorldPackage.IncomingVisibility));

        if ((incoming is not null) && (ShaderPipelineRenderNode.ParseFormat(format: incoming.Format) != SdfWorldPackage.IncomingVisibilityFormat)) {
            throw new InvalidOperationException(message: $"Pass '{context.Pass}' has an unsupported incoming visibility format '{incoming.Format}'.");
        }
        m_incoming = (incoming is not null);

        DeclareScreens(residency: view.Residency);

        var slots = context.InFlightFrames;
        var tables = (view.Residency.Tables ?? throw new InvalidOperationException(message: $"Residency '{view.Residency.Name}' has no tables for pass '{context.Pass}'."));

        m_portTables = new SdfWorldTables?[slots];
        m_boundCounters = new IGpuBuffer?[slots];
        m_screens = new nint[slots][];
        m_shadowPorts = new (nint, nint)[slots];
        m_shadowFrames = new SdfLights[slots];
        m_shadowRebuilt = new uint[slots];

        for (var slot = 0; (slot < slots); slot++) {
            m_screens[slot] = new nint[SdfWorldTables.MaxScreenSurfaces];
            m_shadowFrames[slot] = new SdfLights();
        }

        if (m_part == SdfWorldPackage.IndirectReceiverReset) { return; }
        if (m_part == SdfWorldPackage.MeshParts.Bake) {
            m_bakeSlots = new BakeSlot[slots];
            for (var slot = 0; (slot < slots); slot++) { m_bakeSlots[slot] = new BakeSlot(); }
        }
        if (!IsMesh) {
            m_sets = new RenderGraphPackageSets(
                context: context,
                groupLayoutHandles: tables.Pipeline(kernel: SdfKernel.Shadow).GroupLayoutHandles,
                groups: groups
            );

            if (string.Equals(a: m_part, b: SdfWorldPackage.Parts.Views, comparisonType: StringComparison.Ordinal)) {
                m_pick = new SdfWorldPickReadback(picker: owner.PickerOf(instance: context.Instance), context: context);
                m_pick.ReceiversCompleted = (scope, lighting, deferred, readers) => m_owner.CompletedReceivers(m_context.Instance, scope, lighting, deferred, readers);
            }
            // A replacement bank contains no maps, even while a retiring graph still publishes its old bank.
            // Installation precedes every pass's Skip/Record, so the first traversal also sees this invalidation.
            if (m_lightBank is not null) { view.Residency.IndirectLightViews.InvalidateStorage(); }

            return;
        }

        // The mesh part draws through the mesh interface's layout, whose one pass group holds the pass block the world
        // interface lays out and the mesh region: a set per frame slot from a pool of its own, its block bound to the slot's
        // pass block buffer, and a framebuffer over each instance of its target and depth.
        var bindings = context.Services.Bindings;
        var sizes = default(GpuDescriptorPoolSizes);

        for (var slot = 0; (slot < slots); slot++) {
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
            m_meshSets = new nint[slots];
            m_impostorDepthTables = new SdfWorldTables?[slots];
            m_impostorDepthRevisions = new long[slots];

            for (var slot = 0; (slot < slots); slot++) {
                m_meshSets[slot] = bindings.AllocateSet(
                    descriptorSetLayoutHandle: tables.MeshPipeline.GroupLayoutHandles[((int)ShaderInterfaceGroup.Pass)],
                    name: new GpuObjectName(
                        detail: "mesh group",
                        index: slot,
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
                    descriptorSetHandle: m_meshSets[slot]
                );
            }

            m_meshTargets = groups.OutputImages[0];
            m_framebuffers = new IGpuFramebuffer[m_meshTargets.Count];

            for (var index = 0; (index < m_framebuffers.Length); index++) {
                m_framebuffers[index] = context.Services.RenderPassFactory.CreateFramebuffer(
                    colors: [m_meshTargets[index]],
                    depth: groups.OutputImages[1][index],
                    renderPass: tables.MeshRenderPass
                );
            }
        } catch {
            foreach (var framebuffer in m_framebuffers) {
                framebuffer?.Dispose();
            }

            bindings.DestroyPool(poolHandle: m_meshPool);

            throw;
        }
    }

    // Whether the part rasterizes the frame's mesh draws through the mesh pipeline: the mesh pass, or one of the mesh
    // path's shadow cascades, which draw from the cascade's light camera.
    private bool IsMesh => (m_part is SdfWorldPackage.Parts.Mesh or SdfWorldPackage.MeshParts.ShadowNear or SdfWorldPackage.MeshParts.ShadowFar);
    private bool IsShadowCascade => (m_part is SdfWorldPackage.MeshParts.ShadowNear or SdfWorldPackage.MeshParts.ShadowFar);

    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_pick?.Dispose();
        m_lightBank?.Dispose();
        foreach (var slot in m_bakeSlots) {
            m_view.Residency.MeshPathBake.Abandon(batch: slot.Batch);
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
    // A part skips a frame whose work it would not do, recording neither its work nor its planned barriers:
    // - the mesh part a frame that draws no mesh, when the hit passes, whose pass block's mesh draws are then zero, read
    //   nothing of the target;
    // - the ambient part a view whose ambient occlusion is off, whose neutral occlusion the surface pass already wrote;
    // - the shadow part a view whose soft shadows are off or a frame that has no shadow slots, when views reads nothing
    //   of the record's shadow row.
    public bool Skips(in FrameContext context) {
        if (m_owner.HoldsScreenClosureImage(instance: m_context.Instance)) { return true; }
        if (m_view.LightView) {
            Follow();
            m_owner.Begin(residency: m_view.Residency);
            if (!m_owner.HasIndirectReaders(residency: m_view.Residency)) { return true; }
            m_view.Residency.PlanLightView(context: in context);
            if (m_view.Residency.IndirectLightViews.Pending < 0) { return true; }
            if (!m_view.Residency.AdmitLightView()) { return true; }
        }
        if (m_meshPath && SkipsOnMeshPath(context: in context) is { } meshPathSkip) {
            return meshPathSkip;
        }

        var mesh = IsMesh;
        var ambient = string.Equals(a: m_part, b: SdfWorldPackage.Parts.Ambient, comparisonType: StringComparison.Ordinal);
        var shadow = string.Equals(a: m_part, b: SdfWorldPackage.Parts.Shadow, comparisonType: StringComparison.Ordinal);

        if (!(mesh || ambient || shadow)) {
            return false;
        }

        Follow();

        var residency = m_view.Residency;

        m_owner.Begin(residency: residency);

        var tables = residency.Submit(context: in context);
        var frame = residency.Frame!;

        if (mesh) {
            return (tables.MeshDrawCount == 0);
        }

        var quality = frame.Views[Math.Min(
            val1: m_view.View,
            val2: (frame.Views.Count - 1)
        )].Quality;

        if (ambient) {
            return quality.DisableAmbientOcclusion;
        }

        return (quality.DisableSoftShadows || (frame.Lights.ShadowSlots.SlotCount == 0));
    }
    // On the mesh path no pass walks the field in the frame: the beam, the tape, surface, ambient, shadow and the receiver
    // skip, the mask and bake passes run only while the residency's bake dispatches a lattice this frame (the mask then
    // selects the lattice's instances, as the bake's walk reads them), and the shadow cascades skip a frame with no
    // cascade or no draw. Null leaves the part to the interpreter path's rule.
    private bool? SkipsOnMeshPath(in FrameContext context) {
        switch (m_part) {
            case SdfWorldPackage.Parts.Beam:
            case SdfWorldPackage.Parts.Tape:
            case SdfWorldPackage.Parts.Surface:
            case SdfWorldPackage.Parts.Ambient:
            case SdfWorldPackage.Parts.Shadow:
            case SdfWorldPackage.Parts.Receiver:
                return true;
            case SdfWorldPackage.Parts.Mask:
            case SdfWorldPackage.MeshParts.Bake: {
                Follow();
                m_owner.Begin(residency: m_view.Residency);
                PollBake();
                _ = m_view.Residency.Submit(context: in context);
                return ((m_view.View != 0) || !m_view.Residency.MeshPathBake.TryTakeForFrame(batch: out _, frame: m_view.Residency.PackageFrame));
            }
            case SdfWorldPackage.MeshParts.ShadowNear:
            case SdfWorldPackage.MeshParts.ShadowFar: {
                Follow();
                m_owner.Begin(residency: m_view.Residency);
                var tables = m_view.Residency.Submit(context: in context);

                return ((tables.MeshDrawCount == 0) || (SdfMeshShadowCascades.Fit(frame: m_view.Residency.Frame!, view: m_view.View).Count == 0));
            }
            default:
                return null;
        }
    }
    public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) => ((m_part is SdfWorldPackage.IndirectReceiverReset or SdfWorldPackage.MeshParts.Bake || (m_meshPath && (m_part == SdfWorldPackage.Parts.Mask)))
        ? null : ((m_view.LightView && !m_owner.HasIndirectReaders(residency: m_view.Residency))
            ? m_view.Residency.IndirectLightViews.Revision
            : m_owner.SignatureOf(instance: m_context.Instance, part: SignaturePart, temporal: m_temporal, context: in context)));

    // The receiver executes exactly when views does: it reads what views reads, and views reads its answers.
    // On the mesh path, primary writes the key light's visibility and the cascades draw along the light, so each follows the
    // shadow's inputs as the shadow pass does.
    private string SignaturePart => m_part switch {
        SdfWorldPackage.Parts.Receiver => SdfWorldPackage.Parts.Views,
        SdfWorldPackage.Parts.Primary when m_meshPath => SdfWorldPackage.Parts.Shadow,
        SdfWorldPackage.MeshParts.ShadowNear or SdfWorldPackage.MeshParts.ShadowFar => SdfWorldPackage.Parts.Shadow,
        _ => m_part,
    };

    public void Submitted() {
        if (!m_view.LightView && (m_part == SdfWorldPackage.Parts.Primary)) { m_owner.SubmittedReceiverSurface(instance: m_context.Instance); }
        if (!m_view.LightView && (m_part == SdfWorldPackage.Parts.Receiver)) { m_owner.SubmittedReceiverValidation(instance: m_context.Instance); }
        if ((m_part == SdfWorldPackage.Parts.Views) && !m_view.LightView) {
            m_owner.SubmittedTaint(m_context.Instance, m_sourceTainted, m_readsHistory);
            m_lightingSubmitted = true;
        }
        if (m_part == SdfWorldPackage.LightDepth) { m_view.Residency.SubmitLightView(); }
        if ((m_part == SdfWorldPackage.Parts.Views) && !m_resolved) { m_owner.MarkSampleRendered(instance: m_context.Instance); }
        if (m_part == SdfWorldPackage.Parts.Shadow) {
            m_shadowHistory.Submitted(lights: m_shadowFrames[m_shadowRecordingSlot], rebuilt: m_shadowRebuilt[m_shadowRecordingSlot]);
        }
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        Follow();

        var residency = m_view.Residency;

        m_owner.Begin(residency: residency);

        var tables = residency.Submit(context: recording.Context);

        if (m_part == SdfWorldPackage.IndirectReceiverReset) {
            var diagnostic = recording.Outputs[0].Buffer!;

            recording.Recorder.ClearStorageBuffer(recording.CommandBuffer, diagnostic.BufferHandle, (2 * sizeof(uint)));
            return RenderGraphPackageOutcome.Drew;
        }
        var frame = (m_view.LightView ? residency.LightFrame() : residency.Frame!);
        var view = Math.Min(
            val1: m_view.View,
            val2: (frame.Views.Count - 1)
        );
        var width = recording.Width;
        var height = recording.Height;

        if (!m_view.LightView) { residency.RequestExtent(height: recording.FrameHeight, width: recording.FrameWidth); }
        SdfFrameBlock.Write(
            block: recording.PassBlock,
            frame: frame,
            height: height,
            tables: (m_view.LightView ? tables.PassValues with { DebugMode = 0 } : tables.PassValues),
            view: view,
            width: width
        );
        // A graph planned without the incoming image marches and reads no incoming slot, as its policy requires, until a
        // replanned graph allocates the image a live handoff needs.
        if (!m_incoming) { SdfFrameBlock.WriteWithoutFades(block: recording.PassBlock); }

        var temporal = m_owner.TemporalOf(
            instance: m_context.Instance, view: m_view, width: recording.FrameWidth, height: recording.FrameHeight, debug: tables.PassValues.DebugMode, temporal: m_temporal, unread: recording.UnreadFrames, renderWidth: width, renderHeight: height
        );

        if (!m_view.LightView && (m_part == SdfWorldPackage.Parts.Views)) {
            m_sourceTainted = (SdfWorldPasses.SourceTainted(frame: frame, tables: tables,
                readsIndirect: m_owner.HasIndirectReaders(instance: m_context.Instance)) || (recording.Reads is { Tainted: true }));
            m_readsHistory = (m_temporal && temporal.HasPreviousView);
        }

        SdfFrameBlock.WriteTemporal(block: recording.PassBlock, jitter: temporal.Jitter, historyFrames: temporal.Frames, temporal: m_temporal);
        SdfFrameBlock.WritePreviousView(block: recording.PassBlock, view: temporal.PreviousView, valid: temporal.HasPreviousView);
        SdfFrameBlock.WriteLightViews(block: recording.PassBlock,
            views: ((residency.IndirectTier == SdfIndirectTier.Off) ? null : residency.IndirectLightViews), depthCamera: m_view.LightView);
        SdfFrameBlock.WriteIndirect(block: recording.PassBlock, cache: ((!m_view.LightView && (m_part == SdfWorldPackage.Parts.Primary))
            ? tables.Indirect : BoundIndirect(recording: recording, tables: tables)));
        if (!m_view.LightView && (m_part == SdfWorldPackage.Parts.Primary)) {
            var preserve = m_owner.PrepareReceiverSurface(m_context.Instance, this, recording.Outputs[0].Buffer!,
                tables.PassSignature(frame: frame, part: SdfWorldPackage.Parts.Primary, view: view),
                new SdfReprojectionView(frame.Views[view].Camera, temporal.Jitter, width, height),
                cut: frame.Views[view].CutRevision, stableGeometry: !tables.LightGeometryMutable);

            SdfFrameBlock.WriteIndirectReceiverPreservation(block: recording.PassBlock, preserve: preserve);
        }
        if (!m_view.LightView && (m_part == SdfWorldPackage.Parts.Receiver)) {
            var boundIndirect = BoundIndirect(recording: recording, tables: tables);

            m_comparison = (frame.Views[view].Quality.IndirectMethod != SdfIndirectMethod.Cache);
            SdfFrameBlock.WriteIndirectReceiverPreservation(block: recording.PassBlock, preserve: m_owner.PrepareReceiverValidation(instance: m_context.Instance));
            // A converging capture fills each sample's receiver proofs without an allowance: a sample whose receivers
            // admission order deferred would render again, and which receivers a contended frame admits is not
            // reproducible.
            if ((boundIndirect is { Frozen: false }) && m_owner.Converging(instance: m_context.Instance)) {
                SdfFrameBlock.WriteUnboundedIndirectReceiverBudget(block: recording.PassBlock);
            } else {
                SdfFrameBlock.WriteIndirectReceiverBudget(block: recording.PassBlock, count: ((boundIndirect is null) ? 0 : residency.AdmitReceiverProofs(cache: boundIndirect)));
            }
            SdfFrameBlock.WriteIndirectNear(block: recording.PassBlock, cache: boundIndirect, frame: frame, ready: residency.IsIndirectReady);
            // The selected pixel's near ray and replacing sources are the receiver's part of its record; views takes
            // the same armed request when it records next.
            if ((boundIndirect is not null) && m_owner.PickerOf(instance: m_context.Instance).Arm(height: height, width: width, x: out var pickX, y: out var pickY)) {
                SdfFrameBlock.WriteIndirectPick(block: recording.PassBlock, enabled: true, x: pickX, y: pickY);
            }
        }
        if (!m_view.LightView && (m_part == SdfWorldPackage.Parts.Views)) {
            var boundIndirect = BoundIndirect(recording: recording, tables: tables);

            m_recordedLighting = new(boundIndirect?.PublishedLightingSource, (boundIndirect?.PublishedStamp ?? 0u),
                m_owner.ClosureEpochOf(residency: residency), (tables.SubmittedSkyEnvironment?.Publication ?? default),
                (tables.SubmittedScreenEmission?.Publication ?? default));
        }
        if (m_view.LightView) {
            SdfFrameBlock.WriteTemporal(block: recording.PassBlock, jitter: default, historyFrames: 0, temporal: false);
            SdfFrameBlock.WritePreviousView(block: recording.PassBlock, view: default, valid: false);
        }
        if (m_part == SdfWorldPackage.Parts.Shadow) {
            var enabled = (m_temporal && frame.Views[view].Quality.ShadowAmortize && (tables.PassValues.DebugMode == 0));
            var ownership = m_shadowHistory.Ownership(lights: frame.Lights);
            var lightMotion = m_shadowHistory.LightMotion(lights: frame.Lights);

            SdfFrameBlock.WriteShadowHistory(recording.PassBlock, enabled, ownership, lightMotion);
            m_shadowFrames[recording.Slot].CopyFrom(source: frame.Lights);
            m_shadowRebuilt[recording.Slot] = ((enabled && temporal.HasPreviousView) ? ownership | lightMotion : 15u);
            m_shadowRecordingSlot = recording.Slot;
        }

        SdfFrameBlock.WriteWorkCounterRow(
            block: recording.PassBlock,
            row: WorkCountersOf(recording: in recording).Row
        );
        SdfFrameBlock.WriteWorkCounterDetailRow(block: recording.PassBlock, row: recording.WorkDetailRow);

        if (m_pick is not null) {
            // Build ports carry the storage declaration; the recording carries the version this pass reads or writes.
            var deferredPort = Array.IndexOf(array: m_inputs, value: SdfWorldPackage.IndirectDeferredClear);

            m_pick.Prepare(slot: recording.Slot, width: width, height: height, frame: frame,
                visibility: recording.Inputs[InputIndexOf(member: SdfWorldPackage.VisibilityRecords)].Version,
                box: recording.Inputs[InputIndexOf(member: SdfWorldPackage.CullBounds)].Version,
                sample: new SdfReprojectionView(Camera: frame.Views[view].Camera, Jitter: temporal.Jitter, Width: width, Height: height),
                cut: frame.Views[view].CutRevision, armedOnly: (deferredPort >= 0));
            var cachePort = Array.IndexOf(array: m_inputs, value: SdfWorldPackage.IndirectCache);
            var pickPort = Array.IndexOf(array: m_outputs, value: SdfWorldPackage.IndirectPick);
            var indirect = BoundIndirect(recording: recording, tables: tables);
            var receiverScope = ((indirect is null) ? default : m_owner.PrepareReceivers(m_context.Instance, indirect));

            m_pick.PrepareReceivers(recording.Slot, indirect,
                ((deferredPort < 0) ? null : recording.Inputs[deferredPort].Version), receiverScope);
            m_pick.PrepareIndirect(recording.Slot, indirect,
                ((cachePort < 0) ? null : recording.Inputs[cachePort].Version),
                ((pickPort < 0) ? null : recording.Outputs[pickPort].Version), recording.PassBlock);
        }

        SdfFrameBlock.WriteRenderPath(block: recording.PassBlock, mesh: m_meshPath);

        if (m_meshPath) {
            var cascades = SdfMeshShadowCascades.Fit(frame: frame, view: view);

            SdfFrameBlock.WriteMeshShadows(block: recording.PassBlock, cascades: in cascades);
            if (IsShadowCascade) {
                SdfFrameBlock.WriteShadowCamera(block: recording.PassBlock, camera: cascades[((m_part == SdfWorldPackage.MeshParts.ShadowNear) ? 0 : 1)]);
            }
            if (m_part is SdfWorldPackage.Parts.Mask or SdfWorldPackage.MeshParts.Bake) {
                if (!residency.MeshPathBake.TryTakeForFrame(batch: out var batch, frame: residency.PackageFrame)) {
                    return RenderGraphPackageOutcome.Drew;
                }
                SdfFrameBlock.WriteMeshBake(batch: batch, block: recording.PassBlock);
                if (m_part == SdfWorldPackage.MeshParts.Bake) {
                    PollBake();

                    var target = m_bakeSlots[recording.Slot];

                    // A batch recorded but never copied answers nothing; its groups dispatch their lattices again.
                    if (target.Record || (target.Fence is not null)) {
                        residency.MeshPathBake.Abandon(batch: target.Batch);
                        target.Fence = null;
                    }
                    target.Record = true;
                    target.Batch = batch;
                    target.Version = recording.Outputs[0].Version;
                }
            }
        }

        if (IsMesh) {
            RecordMesh(
                camera: frame.Views[view].Camera,
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
        if (m_lightingSubmitted) {
            m_owner.SubmittedLighting(m_context.Instance, m_recordedLighting, fence);
            m_lightingSubmitted = false;
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

        m_lod = new SdfMeshLodSelector();
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
    // Binds the pass set and dispatches the part's kernel: the masks over the tile grid in groups, the beam one group a tile, the cull arguments once, and the hit passes indirectly over the surviving tiles.
    private void RecordCompute(in RenderGraphPackageRecording recording, SdfWorldTables tables) {
        var slot = recording.Slot;
        var set = m_sets!.PassSet(slot: slot);
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var pipeline = m_part switch {
            SdfWorldPackage.Parts.Mask when m_meshPath => tables.Pipeline(kernel: SdfKernel.MeshBakeMask),
            SdfWorldPackage.MeshParts.Bake => tables.Pipeline(kernel: SdfKernel.MeshBake),
            SdfWorldPackage.Parts.Primary when m_meshPath => tables.Pipeline(kernel: SdfKernel.MeshSurface),
            SdfWorldPackage.Parts.Mask => tables.Pipeline(kernel: SdfKernel.InstanceCull),
            SdfWorldPackage.Parts.Beam => tables.Pipeline(kernel: SdfKernel.Beam),
            SdfWorldPackage.Parts.Tape => tables.Pipeline(kernel: SdfKernel.Tape),
            SdfWorldPackage.Parts.CullArgs => tables.Pipeline(kernel: SdfKernel.CullArgs),
            SdfWorldPackage.Parts.Primary => tables.Pipeline(kernel: (m_view.LightView ? SdfKernel.LightPrimary : SdfKernel.Primary)),
            SdfWorldPackage.LightDepth => tables.Pipeline(kernel: SdfKernel.LightDepth),
            SdfWorldPackage.Parts.Surface => tables.Pipeline(kernel: SdfKernel.Surface),
            SdfWorldPackage.Parts.Ambient => tables.Pipeline(kernel: SdfKernel.Ambient),
            SdfWorldPackage.Parts.Shadow => tables.Pipeline(kernel: SdfKernel.Shadow),
            SdfWorldPackage.Parts.Receiver => tables.ReceiverPipeline(comparison: m_comparison),
            _ => tables.ViewsPipeline,
        };

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

        var residency = m_view.Residency;

        if (m_view.LightView) {
            var light = residency.IndirectLightViews;
            var queries = m_part switch {
                SdfWorldPackage.Parts.Primary => ((((long)light.RowCount) * light.ColumnCount) * SdfIndirectLightLayout.MarchSteps),
                SdfWorldPackage.Parts.Beam => (light.EstimatedQueries - ((((long)light.RowCount) * light.ColumnCount) * SdfIndirectLightLayout.MarchSteps)),
                _ => 0,
            };

            residency.LogIndirectSubmission(("light-" + m_part), queries, residency.Frame!.Program.InstructionCount, (tables.Indirect?.Frame ?? 0));
        } else if ((m_part == SdfWorldPackage.Parts.Receiver) && (tables.Indirect is { } cache)) {
            residency.LogReceiverProofs(cache: cache);
        }

        if (recording.Arguments is { } arguments) {
            recorder.DispatchIndirect(
                argumentBufferHandle: arguments.BufferHandle,
                argumentBufferOffset: 0,
                commandBufferHandle: commandBuffer
            );

            return;
        }

        var tileGridX = ((recording.Width + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize);
        var tileGridY = ((recording.Height + (SdfWorldPackage.TileSize - 1)) / SdfWorldPackage.TileSize);

        var (x, y) = m_part switch {
            SdfWorldPackage.Parts.Mask when m_meshPath => ((uint)SdfWorldPackage.MeshBakeBatch, 1u),
            SdfWorldPackage.MeshParts.Bake => (((uint)(m_bakeSlots[recording.Slot].Batch!.Samples + 63) / 64u), 1u),
            SdfWorldPackage.Parts.Mask => (((tileGridX + (WorkgroupEdge - 1)) / WorkgroupEdge), ((tileGridY + (WorkgroupEdge - 1)) / WorkgroupEdge)),
            SdfWorldPackage.Parts.Beam or SdfWorldPackage.Parts.Tape => (tileGridX, tileGridY),
            SdfWorldPackage.LightDepth => ((((uint)m_view.Residency.IndirectLightViews.ColumnCount) / 8u), (((uint)m_view.Residency.IndirectLightViews.RowCount) / SdfIndirectLightLayout.SliceRowEdge)),
            _ => (1u, 1u),
        };

        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            groupCountX: x,
            groupCountY: y,
            groupCountZ: 1
        );
    }
    // Draws the frame's mesh draws into the instance's target, one draw call a draw, pulling their triangles from the mesh
    // region: every draw the view records (SdfMeshLodSelector) through the mesh pipeline, then the impostor cards among
    // them through the card pipeline. A frame with no draws never records (Skips).
    private void RecordMesh(in RenderGraphPackageRecording recording, SdfWorldTables tables, CameraSnapshot camera) {
        var draws = tables.MeshDraws;
        var count = tables.MeshDrawCount;

        if (
            (count == 0) ||
            (draws is null)
        ) {
            return;
        }

        var slot = recording.Slot;
        var recorder = recording.Recorder;
        var commandBuffer = recording.CommandBuffer;
        var pipeline = tables.MeshPipeline;
        var set = m_meshSets[slot];
        var target = recording.Outputs[0].Owned!;
        var framebuffer = m_framebuffers[0];

        for (var index = 0; (index < m_meshTargets.Count); index++) {
            if (ReferenceEquals(
                objA: m_meshTargets[index],
                objB: target
            )) {
                framebuffer = m_framebuffers[index];
            }
        }

        if (m_recorded.Length < ((int)count)) {
            m_recorded = new bool[((int)count)];
        }

        if (IsShadowCascade) {
            // A cascade draws every mesh, whatever level of detail the camera chose: an impostor card faces the camera,
            // not the light.
            for (var index = 0; (index < count); index++) {
                m_recorded[index] = (draws[index].Impostor is null);
            }
        } else if (m_view.LightView) {
            // A point-sampled impostor cannot certify a swept shadow column. The retained field supplies its geometry;
            // full baked meshes may shorten that field search without becoming a visibility certificate themselves.
            for (var index = 0; (index < count); index++) {
                m_recorded[index] = ((draws[index].Impostor is null) &&
                    (SdfIndirectPolicy.Resolve(draws[index].EffectiveIndirectParticipation(instancesComposable: tables.MeshIndirectInstancesComposable),
                        draws[index].IsDynamic, SdfIndirectTier.Off, tables.LightGeometry.Bodies) == SdfIndirectParticipation.Cast));
            }
        } else {
            m_lod.Select(
            cameraForward: camera.Forward,
            cameraPosition: camera.Position,
            draws: draws,
            impostorsAvailable: (tables.ImpostorAtlas is not null),
            pixelsPerUnitDepth: SdfMeshLod.PixelsPerUnitDepth(
                renderHeight: recording.Height,
                tanHalfFieldOfView: camera.TanHalfFieldOfView
            ),
            recorded: m_recorded
        );
        }
        tables.WriteMeshTables(
            set: set,
            slot: tables.CurrentSlot
        );

        if (
            !ReferenceEquals(objA: m_impostorDepthTables[slot], objB: tables) ||
            (m_impostorDepthRevisions[slot] != tables.ImpostorAtlasRevision)
        ) {
            tables.WriteMeshImpostorDepth(set: set);
            m_impostorDepthTables[slot] = tables;
            m_impostorDepthRevisions[slot] = tables.ImpostorAtlasRevision;
        }

        tables.WriteMeshWorkCounters(
            counters: WorkCountersOf(recording: in recording).Buffer,
            set: set
        );
        // A cascade covers its whole target, whatever the view's render extent.
        recorder.BeginRenderPass(
            area: new GpuPixelRect(
                Height: (IsShadowCascade ? SdfWorldPackage.MeshShadowResolution : recording.Height),
                Width: (IsShadowCascade ? SdfWorldPackage.MeshShadowResolution : recording.Width),
                X: 0,
                Y: 0
            ),
            commandBufferHandle: commandBuffer,
            framebuffer: framebuffer
        );
        RecordMeshDraws(
            cards: false,
            commandBuffer: commandBuffer,
            count: count,
            draws: draws,
            pipeline: pipeline,
            recorder: recorder,
            set: set
        );

        if (HasCard(count: count, draws: draws)) {
            RecordMeshDraws(
                cards: true,
                commandBuffer: commandBuffer,
                count: count,
                draws: draws,
                pipeline: tables.ImpostorPipeline,
                recorder: recorder,
                set: set
            );
        }

        recorder.EndRenderPass(commandBufferHandle: commandBuffer);
    }
    // Whether the view records an impostor card this frame.
    private bool HasCard(uint count, IReadOnlyList<SdfMeshDraw> draws) {
        for (var draw = 0; (draw < count); draw++) {
            if (m_recorded[draw] && (draws[draw].Impostor is not null)) {
                return true;
            }
        }

        return false;
    }
    // Binds a pipeline and the pass set, then draws the recorded draws of one kind: the cards, or everything else.
    private void RecordMeshDraws(bool cards, uint count, IReadOnlyList<SdfMeshDraw> draws, IGpuPipeline pipeline, IGpuRecorder recorder, nint commandBuffer, nint set) {
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
            if (
                !m_recorded[((int)draw)] ||
                ((draws[((int)draw)].Impostor is not null) != cards)
            ) {
                continue;
            }

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
        var shadowPorts = (Read: ((nint)0), Write: ((nint)0));

        for (var port = 0; (port < m_inputs.Length); port++) {
            if (m_inputs[port] == SdfWorldPackage.ShadowHistory) { shadowPorts.Read = recording.Inputs[port].Buffer!.BufferHandle; }
        }
        for (var port = 0; (port < m_outputs.Length); port++) {
            if (m_outputs[port] == SdfWorldPackage.ShadowHistory) { shadowPorts.Write = recording.Outputs[port].Buffer!.BufferHandle; }
        }

        tables.WriteWorldBuffer(buffer: (tables.Indirect?.Regions[0].Buffer(slot: tables.CurrentSlot) ?? tables.DummyBuffer),
            member: SdfWorldPackage.IndirectBricks, set: set);
        tables.WriteWorldBuffer(buffer: (tables.Indirect?.Regions[2].Buffer(slot: tables.CurrentSlot) ?? tables.DummyBuffer),
            member: SdfWorldPackage.IndirectDirections, set: set);
        tables.WriteWorldBuffer(buffer: (tables.Indirect?.Regions[3].Buffer(slot: tables.CurrentSlot) ?? tables.DummyBuffer),
            member: SdfWorldPackage.IndirectTraceStates, set: set);

        if (ReferenceEquals(
            objA: m_portTables[slot],
            objB: tables
        ) && (m_shadowPorts[slot] == shadowPorts)) {
            return;
        }

        var bindings = tables.Bindings;
        var output = tables.StorageFiller.ImageViewHandle;
        var meshVisibility = tables.SampledFiller.ImageViewHandle;
        // Every per-view interface declares the incoming handoff image; a graph without one binds the fillers.
        var incomingVisibility = tables.SampledFiller.ImageViewHandle;
        var incomingWritten = tables.IncomingStorageFiller.ImageViewHandle;
        var shadowNear = tables.SampledFiller.ImageViewHandle;
        var shadowFar = tables.SampledFiller.ImageViewHandle;

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
            } else if (name == SdfWorldPackage.IncomingVisibility) {
                incomingVisibility = bound.Image.ImageViewHandle;
            } else if (name == SdfWorldPackage.MeshParts.ShadowNearTarget) {
                shadowNear = bound.Image.ImageViewHandle;
            } else if (name == SdfWorldPackage.MeshParts.ShadowFarTarget) {
                shadowFar = bound.Image.ImageViewHandle;
            }
        }
        for (var port = 0; (port < m_outputs.Length); port++) {
            var name = m_outputs[port];
            var bound = recording.Outputs[port];

            if (bound.Buffer is { } buffer) {
                if (WrittenMemberOf(version: name) is { } member) {
                    tables.WriteWorldBuffer(buffer: buffer, member: member, set: set);
                }
            } else if (name == SdfWorldPackage.IncomingVisibility) {
                incomingWritten = bound.Image.ImageViewHandle;
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
        bindings.WriteSampledImage(arrayElement: 0, binding: IncomingVisibilityBinding,
            descriptorSetHandle: set, imageViewHandle: incomingVisibility);
        bindings.WriteStorageImage(arrayElement: 0, binding: IncomingVisibilityWrittenBinding,
            descriptorSetHandle: set, imageViewHandle: incomingWritten);
        bindings.WriteSampledImage(arrayElement: 0, binding: MeshShadowNearBinding, descriptorSetHandle: set, imageViewHandle: shadowNear);
        bindings.WriteSampledImage(arrayElement: 0, binding: MeshShadowFarBinding, descriptorSetHandle: set, imageViewHandle: shadowFar);
        Array.Clear(array: m_screens[slot]);
        m_portTables[slot] = tables;
        m_shadowPorts[slot] = shadowPorts;
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
    private string? ReadMemberOf(string version) => version switch {
        SdfWorldPackage.ShadowHistory => SdfWorldPackage.ShadowHistory,
        SdfWorldPackage.IndirectCache => ((m_part == SdfWorldPackage.Parts.Receiver) ? SdfWorldPackage.IndirectCacheWritten : SdfWorldPackage.IndirectCache),
        SdfWorldPackage.IndirectAnswer => SdfWorldPackage.IndirectAnswer,
        SdfWorldPackage.IndirectLightDepth => SdfWorldPackage.IndirectLightDepth,
        SdfWorldPackage.Parts.InstanceMasks => SdfWorldPackage.InstanceMasks,
        SdfWorldPackage.Parts.SegmentTapes => SdfWorldPackage.SegmentTapes,
        SdfWorldPackage.Parts.Tiles => SdfWorldPackage.Tiles,
        SdfWorldPackage.Parts.CullBounds => SdfWorldPackage.CullBounds,
        SdfWorldPackage.Parts.Visibility or SdfWorldPackage.Parts.SurfaceVisibility or SdfWorldPackage.Parts.AmbientVisibility or SdfWorldPackage.Parts.ShadowVisibility =>
            ((m_part == SdfWorldPackage.Parts.Receiver) ? SdfWorldPackage.VisibilityRecordsWritten : SdfWorldPackage.VisibilityRecords),
        _ => null,
    };
    // A graph may still retain an old allocation while a tier or far-distance replacement installs. Never combine
    // that buffer with the new allocation's brick directory or publication stamps.
    private SdfIndirectCache? BoundIndirect(in RenderGraphPackageRecording recording, SdfWorldTables tables) {
        if (m_view.LightView || (tables.Indirect is not { } cache)) { return null; }
        for (var port = 0; (port < m_inputs.Length); port++) {
            if ((m_inputs[port] == SdfWorldPackage.IndirectCache) && ReferenceEquals(objA: recording.Inputs[port].Buffer, objB: cache.Buffer)) { return cache; }
        }
        return null;
    }
    // The member a pass writes a fragment buffer through, or null for one it writes through no member.
    private static string? WrittenMemberOf(string version) => version switch {
        SdfWorldPackage.IndirectPick => SdfWorldPackage.IndirectPickWritten,
        SdfWorldPackage.IndirectAnswer => SdfWorldPackage.IndirectAnswerWritten,
        SdfWorldPackage.IndirectDeferredClear => SdfWorldPackage.IndirectDeferredWritten,
        SdfWorldPackage.IndirectVisibility => SdfWorldPackage.VisibilityRecordsWritten,
        SdfWorldPackage.IndirectLightDepth => SdfWorldPackage.IndirectLightDepthWritten,
        SdfWorldPackage.ShadowHistory => SdfWorldPackage.ShadowHistoryWritten,
        SdfWorldPackage.Parts.InstanceMasks => SdfWorldPackage.InstanceMasksWritten,
        SdfWorldPackage.Parts.SegmentTapes => SdfWorldPackage.SegmentTapesWritten,
        SdfWorldPackage.Parts.Tiles => SdfWorldPackage.TilesWritten,
        SdfWorldPackage.Parts.Arguments => SdfWorldPackage.ViewsArgsWritten,
        SdfWorldPackage.Parts.CullBounds => SdfWorldPackage.CullBoundsWritten,
        SdfWorldPackage.Parts.Visibility or SdfWorldPackage.Parts.SurfaceVisibility or SdfWorldPackage.Parts.AmbientVisibility or SdfWorldPackage.Parts.ShadowVisibility => SdfWorldPackage.VisibilityRecordsWritten,
        SdfWorldPackage.Parts.Reactivity => SdfWorldPackage.ReactivityWritten,
        SdfWorldPackage.MeshParts.BakeSamples => SdfWorldPackage.MeshBakeSamplesWritten,
        _ => null,
    };

    // Reads every bake slot whose submission completed and answers the bake with its samples.
    private void PollBake() {
        foreach (var slot in m_bakeSlots) {
            if ((slot.Fence is not { } fence) || !fence.IsSignaled) { continue; }
            slot.Fence = null;

            if (slot.Batch is not { } batch) { continue; }

            var bytes = (batch.Samples * SdfWorldPackage.MeshBakeSampleWords * sizeof(uint));

            if (slot.Bytes.Length < bytes) { slot.Bytes = new byte[bytes]; }
            slot.Buffer!.Read(destination: slot.Bytes.AsSpan(length: bytes, start: 0));
            slot.Batch = null;
            m_view.Residency.MeshPathBake.Complete(batch: batch, samples: slot.Bytes.AsSpan(length: bytes, start: 0));
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
