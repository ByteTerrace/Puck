using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Maths;
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

    public SdfSkyEnvironmentRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups,
        SdfSkyEnvironmentPasses.Built built, SdfWorldPasses views) {
        m_context = context;
        m_built = built;
        m_views = views;
        m_sets = new RenderGraphPackageSets(context: context, groups: groups, groupLayoutHandles: built.Tables.Pipeline(kernel: Kernel).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context: context, sets: m_sets);
        m_publications = new RenderGraphSlotResults<long>(slots: context.InFlightFrames, abandon: Withdraw, complete: sequence => {
            if (Screens) { m_built.Tables.CompleteScreenEmission(sequence: sequence); } else { m_built.Tables.CompleteSkyEnvironment(sequence: sequence); }
        });
    }

    private SdfKernel Kernel => (Screens ? SdfKernel.ScreenEmission : (Reduces ? SdfKernel.SkyEnvironmentReduce : SdfKernel.SkyEnvironment));
    private bool Reduces => (m_context.Part == SdfSkyEnvironmentGraph.Coefficients);
    private bool Screens => (m_context.Part == SdfSkyEnvironmentGraph.Screens);

    public ulong ReadbackBytes => 0;

    public IReadOnlyList<string> WorkDetails(in FrameContext context) => (Screens ? ScreenWorkDetails : (Reduces ? [] : m_built.Residency.SkyDetails.Labels));
    // The residency is the state every recording of it shares (IRenderGraphPackageRecorder.RecordingDomain).
    public object? RecordingDomain => m_built.Residency;
    public void PrepareRecord(in FrameContext context) => m_views.PrepareResidency(residency: m_built.Residency, context: in context);
    public bool Skips(in FrameContext context) {
        return (!m_built.Residency.Prepare(context: context) ||
            !ReferenceEquals(objA: m_built.Tables, objB: m_built.Residency.Tables) || (!Screens && !m_built.Tables.SkyEnvironmentDemand));
    }
    public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) {
        var tables = m_built.Residency.Staged(context: context);

        return (Screens ? tables.ScreenEmissionSignature(m_built.Residency, m_built.View, reads, context.Latch)
            : tables.SkyEnvironmentSignature(latch: context.Latch, residency: m_built.Residency, view: m_built.View, reads: reads));
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        var tables = m_built.Residency.Staged(context: recording.Context);

        if (Screens) { _ = tables.ScreenEmissionSignature(m_built.Residency, m_built.View, recording.Reads, recording.Latch); } else { _ = tables.SkyEnvironmentSignature(latch: recording.Latch, residency: m_built.Residency, view: m_built.View, reads: recording.Reads); }
        if (!Screens && !tables.SkyEnvironmentCanRecord) {
            return RenderGraphPackageOutcome.DrewNothing;
        }
        var set = m_sets.PassSet(slot: recording.Slot);

        tables.BindSkyEnvironment(set: set, bindings: m_context.Services.Bindings);
        var emissionMask = m_built.Residency.BindScreenImages(view: m_built.View,
            shown: (Reduces ? UInt128.Zero : (Screens ? tables.ScreenEmissionScreens : tables.SkyEnvironmentScreens)), recording: in recording,
            bindings: m_context.Services.Bindings, binding: m_sets.BindingOf(member: SdfWorldPackage.ScreenSources), set: set,
            filler: tables.SampledFiller.ImageViewHandle, bound: default);
        SdfWorldInterfaces.EnvironmentParameters.WriteExtent(block: recording.PassBlock, width: SdfSkyEnvironment.Size, height: SdfSkyEnvironment.Size);
        var maskBlock = recording.PassBlock[((int)SdfWorldInterfaces.EnvironmentParameters.BlockOffsetOf(member: SdfKernelInterfaces.ScreenEmissionMask))..];

        for (var word = 0; (word < 4); word++) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination: maskBlock[(word * sizeof(uint))..], value: BitWords.Word32(mask: emissionMask, word: word));
        }
        m_work.Write(passSet: set, recording: recording);
        var pipeline = tables.Pipeline(kernel: Kernel);

        recording.Recorder.BindPipeline(commandBufferHandle: recording.CommandBuffer, bindPoint: GpuBindPoint.Compute, pipelineHandle: pipeline.Handle);
        m_sets.Bind(recorder: recording.Recorder, commandBuffer: recording.CommandBuffer, bindPoint: GpuBindPoint.Compute,
            pipelineLayout: pipeline.LayoutHandle, slot: recording.Slot);
        var groups = (Reduces ? 1u : (SdfSkyEnvironment.Size / 8u));

        // The screen reduction runs a group a slot up to the highest bound screen, the screens shading reads
        // (sdfScreenLightCount), so a bound slot with no acquired image this frame writes zero rather than keeping an old
        // record.
        var screenGroups = (uint)Math.Max(val1: 1, val2: Math.Max(val1: BitWords.Span(mask: emissionMask), val2: (int)tables.BoundScreenCount()));

        recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: (Screens ? screenGroups : groups),
            groupCountY: (Screens ? 1u : groups), groupCountZ: 1);
        // The views of this residency record after this pass on this thread (RecordingDomain), so they classify the buffer
        // they sample by what this recording writes, before the node commits it.
        if (Screens) {
            tables.RecordScreenEmission(frame: m_built.Residency.PackageFrame);
        } else if (Reduces) {
            tables.RecordSkyEnvironment(frame: m_built.Residency.PackageFrame);
        }
        return RenderGraphPackageOutcome.Drew;
    }
    public bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback) { readback = default; return false; }
    // Called only after this frame's recording drew (ShaderPipelineRenderNode.SubmitPackageReadbacks).
    public void Submitted(int slot) {
        if (!Reduces && !Screens) { return; }
        var sequence = (Screens ? m_built.Tables.SubmitScreenEmission() : m_built.Tables.SubmitSkyEnvironment());
        m_publications.Record(slot: slot, result: sequence);
        m_publications.Submitted(slot: slot);
    }
    public void Completed(int slot) => m_publications.Completed(slot: slot);
    public void Abandoned(int slot) => m_publications.Abandoned(slot: slot);
    private void Withdraw(long sequence) {
        if (Screens) { m_built.Tables.WithdrawScreenEmission(sequence: sequence); } else { m_built.Tables.WithdrawSkyEnvironment(sequence: sequence); }
    }
    public void Dispose() {
        m_publications.Release(unproven: Withdraw);
        m_built.Dispose();
    }
}
