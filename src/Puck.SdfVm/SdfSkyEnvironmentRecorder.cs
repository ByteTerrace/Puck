using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// Both passes share the residency's buffers, acquired reads and projection signature. Only the reduction's successful
// submission publishes a new sequence; a standing or skipped pass never publishes. The graph owns every barrier.
internal sealed class SdfSkyEnvironmentRecorder : IRenderGraphPackageRecorder, IRenderGraphPackageReadback {
    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly SdfSkyEnvironmentPasses.Built m_built;
    private readonly SdfWorldPasses m_views;
    private readonly RenderGraphPackageSets m_sets;
    private readonly RenderGraphPackageWorkCounters m_work;
    private readonly bool[] m_recorded;
    private long m_submission;

    public SdfSkyEnvironmentRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups,
        SdfSkyEnvironmentPasses.Built built, SdfWorldPasses views) {
        m_context = context;
        m_built = built;
        m_views = views;
        m_sets = new RenderGraphPackageSets(context: context, groups: groups, groupLayoutHandles: built.Tables.Pipeline(kernel: Kernel).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context: context, sets: m_sets);
        m_recorded = new bool[context.InFlightFrames];
    }

    private bool Reduces => m_context.Part == SdfSkyEnvironmentGraph.Coefficients;
    private SdfKernel Kernel => Reduces ? SdfKernel.SkyEnvironmentReduce : SdfKernel.SkyEnvironment;
    public ulong ReadbackBytes => 0;
    public IReadOnlyList<string> WorkDetails(in FrameContext context) => Reduces ? [] : m_built.Residency.SkyDetails.Labels;
    public bool Skips(in FrameContext context) {
        Array.Clear(array: m_recorded);
        m_views.Begin(residency: m_built.Residency);
        m_built.Tables.PollSkyEnvironment();
        return !m_built.Residency.Prepare(context: context) ||
            !ReferenceEquals(objA: m_built.Tables, objB: m_built.Residency.Tables) || !m_built.Tables.SkyEnvironmentDemand;
    }
    public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) {
        var tables = m_built.Residency.Submit(context: context);
        return tables.SkyEnvironmentSignature(residency: m_built.Residency, view: m_built.View, reads: reads);
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        var tables = m_built.Residency.Submit(context: recording.Context);
        _ = tables.SkyEnvironmentSignature(residency: m_built.Residency, view: m_built.View, reads: recording.Reads);
        var set = m_sets.PassSet(slot: recording.Slot);
        tables.BindSkyEnvironment(set: set, bindings: m_context.Services.Bindings);
        for (var screen = 0; screen < SdfWorldTables.MaxScreenSurfaces; screen++) {
            var image = !Reduces && tables.SkyEnvironmentScreens[screen]
                ? m_built.Residency.ScreenImage(view: m_built.View, screen: screen, reads: recording.Reads, leases: recording.Leases) : 0;
            m_context.Services.Bindings.WriteSampledImage(arrayElement: (uint)screen,
                binding: m_sets.BindingOf(member: SdfWorldPackage.ScreenSources), descriptorSetHandle: set,
                imageViewHandle: image != 0 ? image : tables.SampledFiller.ImageViewHandle);
        }
        SdfWorldInterfaces.EnvironmentParameters.WriteExtent(block: recording.PassBlock, width: SdfSkyEnvironment.Size, height: SdfSkyEnvironment.Size);
        m_work.Write(passSet: set, recording: recording);
        var pipeline = tables.Pipeline(kernel: Kernel);
        recording.Recorder.BindPipeline(commandBufferHandle: recording.CommandBuffer, bindPoint: GpuBindPoint.Compute, pipelineHandle: pipeline.Handle);
        m_sets.Bind(recorder: recording.Recorder, commandBuffer: recording.CommandBuffer, bindPoint: GpuBindPoint.Compute,
            pipelineLayout: pipeline.LayoutHandle, slot: recording.Slot);
        var groups = Reduces ? 1u : SdfSkyEnvironment.Size / 8u;
        recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: groups, groupCountY: groups, groupCountZ: 1);
        m_recorded[recording.Slot] = true;
        return RenderGraphPackageOutcome.Drew;
    }
    public bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback) { readback = default; return false; }
    public void Submitted(int slot, IGpuSubmissionFence fence) {
        if (!m_recorded[slot]) { return; }
        m_recorded[slot] = false;
        if (Reduces) { m_submission = m_built.Tables.SubmitSkyEnvironment(fence: fence); }
    }
    public void Dispose() {
        if (m_submission != 0) { m_built.Tables.WithdrawSkyEnvironment(sequence: m_submission); }
        m_built.Dispose();
    }
}
