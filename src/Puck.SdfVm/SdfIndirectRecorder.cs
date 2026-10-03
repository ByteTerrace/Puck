using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

internal sealed class SdfIndirectRecorder : IRenderGraphPackageRecorder {
    private static readonly (int Source, int Destination, int Length)[] FrameCopies = [.. SdfWorldPackage.Values.Select(selector: member => (
        ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: member.Name)), ((int)SdfWorldInterfaces.IndirectParameters.BlockOffsetOf(member: member.Name)),
        checked((int)(member.Type!.Value.SizeBytes() * (member.Length ?? 1)))))];

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly SdfIndirectPasses.Built m_built;
    private readonly SdfWorldPasses m_views;
    private readonly RenderGraphPackageSets m_sets;
    private readonly RenderGraphPackageWorkCounters m_work;

    public SdfIndirectRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfIndirectPasses.Built built, SdfWorldPasses views) {
        m_context = context;
        m_built = built;
        m_views = views;
        m_sets = new RenderGraphPackageSets(context, groups, built.Residency.Tables!.Pipeline(kernel: Kernel).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context: context, sets: m_sets);
    }

    private int Count => m_context.Part switch {
        SdfWorldPackage.IndirectPlace => m_built.Cache.PlaceCount,
        SdfWorldPackage.IndirectClassify => m_built.Cache.ClassifyCount,
        _ => m_built.Cache.TraceCount,
    };
    private SdfKernel Kernel => ((m_context.Part == SdfWorldPackage.IndirectTrace) ? SdfKernel.IndirectTrace : SdfKernel.IndirectClassify);

    public IReadOnlyList<string> WorkDetails(in FrameContext context) => SdfWorldWorkDetails.Indirect;
    public bool Skips(in FrameContext context) {
        m_views.Begin(residency: m_built.Residency);
        if (!m_built.Residency.Prepare(context: context) || !ReferenceEquals(objA: m_built.Cache, objB: m_built.Residency.Tables!.Indirect)) { return true; }
        m_built.Residency.Tables.PlanIndirect(frame: m_built.Residency.Frame!);
        return ((m_context.Part != SdfWorldPackage.IndirectTrace) && (Count == 0));
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        var tables = m_built.Residency.Submit(context: recording.Context);
        var cache = m_built.Cache;

        if (Count == 0) { return RenderGraphPackageOutcome.Drew; }
        Span<byte> common = stackalloc byte[SdfFrameBlock.SizeBytes];

        common.Clear();
        SdfFrameBlock.Write(block: common, tables: tables.PassValues, frame: m_built.Residency.Frame!, view: 0, width: 1, height: 1);
        foreach (var (source, destination, length) in FrameCopies) { common.Slice(length: length, start: source).CopyTo(destination: recording.PassBlock.Slice(length: length, start: destination)); }
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectEpoch, value: cache.Epoch);
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectFrame, value: cache.Frame);
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectPlaceCount, value: ((uint)cache.PlaceCount));
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectClassifyCount, value: ((uint)cache.ClassifyCount));
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectTraceCount, value: ((uint)cache.TraceCount));
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectPhase, value: ((m_context.Part == SdfWorldPackage.IndirectPlace) ? 0u : 1u));
        var set = m_sets.PassSet(slot: recording.Slot);
        var layout = SdfWorldInterfaces.IndirectParameters.Layout;

        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectCacheWritten, recording.Outputs[0].Buffer!);
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectBricks, cache.Regions[0].Buffer(slot: tables.CurrentSlot));
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectUpdates, cache.Regions[1].Buffer(slot: tables.CurrentSlot));
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectDirections, cache.Regions[2].Buffer(slot: tables.CurrentSlot));
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectTraceStates, cache.Regions[3].Buffer(slot: tables.CurrentSlot));
        m_work.Write(passSet: set, recording: recording);
        var pipeline = tables.Pipeline(kernel: Kernel);

        recording.Recorder.BindPipeline(recording.CommandBuffer, GpuBindPoint.Compute, pipeline.Handle);
        m_sets.Bind(recording.Recorder, recording.CommandBuffer, GpuBindPoint.Compute, pipeline.LayoutHandle, recording.Slot);
        recording.Recorder.BindDescriptorSet(recording.CommandBuffer, GpuBindPoint.Compute, pipeline.LayoutHandle, ((uint)ShaderInterfaceGroup.World), tables.WorldSet(slot: tables.CurrentSlot));
        recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: ((uint)Count), groupCountY: 1, groupCountZ: 1);
        return RenderGraphPackageOutcome.Drew;
    }
    public void Submitted() { if (m_context.Part == SdfWorldPackage.IndirectTrace) { m_built.Cache.Submitted(); } }
    public void Dispose() => m_built.Dispose();

    private static void Write(Span<byte> block, string member, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(destination: block[((int)SdfWorldInterfaces.IndirectParameters.BlockOffsetOf(member: member))..], value: value);
}
