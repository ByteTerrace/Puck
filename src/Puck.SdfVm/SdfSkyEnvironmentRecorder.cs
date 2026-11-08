using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The sky passes share one projection signature; the screen reduction has its own acquired-source signature.
// Each reduction publishes only after successful submission. Standing and skipped passes never publish; the graph owns barriers.
internal sealed class SdfSkyEnvironmentRecorder : IRenderGraphPackageRecorder, IRenderGraphPackageReadback {
    private static readonly string[] ScreenWorkDetails = ["screen-images"];

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly SdfSkyEnvironmentPasses.Built m_built;
    private readonly SdfWorldPasses m_views;
    private readonly RenderGraphPackageSets m_sets;
    private readonly RenderGraphPackageWorkCounters m_work;
    // Each slot's submitted publication sequence, latched complete only by the node's proof.
    private readonly RenderGraphSlotResults<long> m_publications;

    private long m_submission;

    public SdfSkyEnvironmentRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups,
        SdfSkyEnvironmentPasses.Built built, SdfWorldPasses views) {
        m_context = context;
        m_built = built;
        m_views = views;
        m_sets = new RenderGraphPackageSets(context: context, groups: groups, groupLayoutHandles: built.Tables.Pipeline(kernel: Kernel).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context: context, sets: m_sets);
        m_publications = new RenderGraphSlotResults<long>(slots: context.InFlightFrames, complete: sequence => {
            if (Screens) { m_built.Tables.CompleteScreenEmission(sequence: sequence); } else { m_built.Tables.CompleteSkyEnvironment(sequence: sequence); }
        });
    }

    private SdfKernel Kernel => (Screens ? SdfKernel.ScreenEmission : (Reduces ? SdfKernel.SkyEnvironmentReduce : SdfKernel.SkyEnvironment));
    private bool Reduces => (m_context.Part == SdfSkyEnvironmentGraph.Coefficients);
    private bool Screens => (m_context.Part == SdfSkyEnvironmentGraph.Screens);

    public ulong ReadbackBytes => 0;

    public IReadOnlyList<string> WorkDetails(in FrameContext context) => (Screens ? ScreenWorkDetails : (Reduces ? [] : m_built.Residency.SkyDetails.Labels));
    public bool Skips(in FrameContext context) {
        m_views.Begin(residency: m_built.Residency);
        return (!m_built.Residency.Prepare(context: context) ||
            !ReferenceEquals(objA: m_built.Tables, objB: m_built.Residency.Tables) || (!Screens && !m_built.Tables.SkyEnvironmentDemand));
    }
    public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) {
        var tables = m_built.Residency.Staged(context: context);

        return (Screens ? tables.ScreenEmissionSignature(m_built.Residency, m_built.View, reads)
            : tables.SkyEnvironmentSignature(residency: m_built.Residency, view: m_built.View, reads: reads));
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        var tables = m_built.Residency.Staged(context: recording.Context);

        if (Screens) { _ = tables.ScreenEmissionSignature(m_built.Residency, m_built.View, recording.Reads); } else { _ = tables.SkyEnvironmentSignature(residency: m_built.Residency, view: m_built.View, reads: recording.Reads); }
        if (!Screens && !tables.SkyEnvironmentCanRecord) {
            return RenderGraphPackageOutcome.DrewNothing;
        }
        var set = m_sets.PassSet(slot: recording.Slot);

        tables.BindSkyEnvironment(set: set, bindings: m_context.Services.Bindings);
        var emissionMask = 0u;

        for (var screen = 0; (screen < SdfWorldTables.MaxScreenSurfaces); screen++) {
            var image = ((!Reduces && (Screens ? tables.ScreenEmissionScreens[screen] : tables.SkyEnvironmentScreens[screen]))
                ? m_built.Residency.ScreenImage(view: m_built.View, screen: screen, reads: recording.Reads, leases: recording.Leases) : 0);

            if (image != 0) { emissionMask |= (1u << screen); }
            m_context.Services.Bindings.WriteSampledImage(arrayElement: ((uint)screen),
                binding: m_sets.BindingOf(member: SdfWorldPackage.ScreenSources), descriptorSetHandle: set,
                imageViewHandle: ((image != 0) ? image : tables.SampledFiller.ImageViewHandle));
        }
        SdfWorldInterfaces.EnvironmentParameters.WriteExtent(block: recording.PassBlock, width: SdfSkyEnvironment.Size, height: SdfSkyEnvironment.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: recording.PassBlock[((int)SdfWorldInterfaces.EnvironmentParameters.BlockOffsetOf(member: SdfKernelInterfaces.ScreenEmissionMask))..], value: emissionMask);
        m_work.Write(passSet: set, recording: recording);
        var pipeline = tables.Pipeline(kernel: Kernel);

        recording.Recorder.BindPipeline(commandBufferHandle: recording.CommandBuffer, bindPoint: GpuBindPoint.Compute, pipelineHandle: pipeline.Handle);
        m_sets.Bind(recorder: recording.Recorder, commandBuffer: recording.CommandBuffer, bindPoint: GpuBindPoint.Compute,
            pipelineLayout: pipeline.LayoutHandle, slot: recording.Slot);
        var groups = (Reduces ? 1u : (SdfSkyEnvironment.Size / 8u));

        recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: (Screens ? SdfWorldTables.MaxScreenSurfaces : groups),
            groupCountY: (Screens ? 1u : groups), groupCountZ: 1);
        return RenderGraphPackageOutcome.Drew;
    }
    public bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback) { readback = default; return false; }
    // Called only after this frame's recording drew (ShaderPipelineRenderNode.SubmitPackageReadbacks).
    public void Submitted(int slot) {
        if (!Reduces && !Screens) { return; }
        m_submission = (Screens ? m_built.Tables.SubmitScreenEmission() : m_built.Tables.SubmitSkyEnvironment());
        m_publications.Record(slot: slot, result: m_submission);
        m_publications.Submitted(slot: slot);
    }
    public void Completed(int slot) => m_publications.Completed(slot: slot);
    public void Dispose() {
        if (m_submission != 0) {
            if (Screens) { m_built.Tables.WithdrawScreenEmission(sequence: m_submission); } else { m_built.Tables.WithdrawSkyEnvironment(sequence: m_submission); }
        }
        m_built.Dispose();
    }
}
