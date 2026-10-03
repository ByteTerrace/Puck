using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

internal sealed class SdfIndirectRecorder : IRenderGraphPackageRecorder {
    private static readonly (int Source, int Destination, int Length)[] FrameCopies = [.. SdfWorldPackage.Values.Select(member => (
        ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member.Name)), ((int)SdfWorldInterfaces.IndirectParameters.BlockOffsetOf(member.Name)),
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
        m_sets = new RenderGraphPackageSets(context, groups, built.Residency.Tables!.Pipeline(Kernel).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context, m_sets);
    }

    private int Count => m_context.Part switch {
        SdfWorldPackage.IndirectPlace => m_built.Cache.PlaceCount,
        SdfWorldPackage.IndirectClassify => m_built.Cache.ClassifyCount,
        _ => m_built.Cache.TraceCount,
    };
    private SdfKernel Kernel => ((m_context.Part == SdfWorldPackage.IndirectTrace) ? SdfKernel.IndirectTrace : SdfKernel.IndirectClassify);

    public IReadOnlyList<string> WorkDetails(in FrameContext context) => SdfWorldWorkDetails.Indirect;
    public bool Skips(in FrameContext context) {
        m_views.Begin(m_built.Residency);
        if (!m_built.Residency.Prepare(context) || !ReferenceEquals(m_built.Cache, m_built.Residency.Tables!.Indirect)) { return true; }
        m_built.Residency.Tables.PlanIndirect(m_built.Residency.Frame!);
        return ((m_context.Part != SdfWorldPackage.IndirectTrace) && (Count == 0));
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        var tables = m_built.Residency.Submit(recording.Context);
        var cache = m_built.Cache;

        if (Count == 0) { return RenderGraphPackageOutcome.Drew; }
        Span<byte> common = stackalloc byte[SdfFrameBlock.SizeBytes];

        common.Clear();
        SdfFrameBlock.Write(block: common, tables: tables.PassValues, frame: m_built.Residency.Frame!, view: 0, width: 1, height: 1);
        foreach (var (source, destination, length) in FrameCopies) { common.Slice(source, length).CopyTo(recording.PassBlock.Slice(destination, length)); }
        Write(recording.PassBlock, SdfWorldPackage.IndirectEpoch, cache.Epoch);
        Write(recording.PassBlock, SdfWorldPackage.IndirectFrame, cache.Frame);
        Write(recording.PassBlock, SdfWorldPackage.IndirectPlaceCount, ((uint)cache.PlaceCount));
        Write(recording.PassBlock, SdfWorldPackage.IndirectClassifyCount, ((uint)cache.ClassifyCount));
        Write(recording.PassBlock, SdfWorldPackage.IndirectTraceCount, ((uint)cache.TraceCount));
        Write(recording.PassBlock, SdfWorldPackage.IndirectPhase, ((m_context.Part == SdfWorldPackage.IndirectPlace) ? 0u : 1u));
        var set = m_sets.PassSet(recording.Slot);
        var layout = SdfWorldInterfaces.IndirectParameters.Layout;

        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectCacheWritten, recording.Outputs[0].Buffer!);
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectBricks, cache.Regions[0].Buffer(tables.CurrentSlot));
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectUpdates, cache.Regions[1].Buffer(tables.CurrentSlot));
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectDirections, cache.Regions[2].Buffer(tables.CurrentSlot));
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectTraceStates, cache.Regions[3].Buffer(tables.CurrentSlot));
        m_work.Write(recording, set);
        var pipeline = tables.Pipeline(Kernel);

        recording.Recorder.BindPipeline(recording.CommandBuffer, GpuBindPoint.Compute, pipeline.Handle);
        m_sets.Bind(recording.Recorder, recording.CommandBuffer, GpuBindPoint.Compute, pipeline.LayoutHandle, recording.Slot);
        recording.Recorder.BindDescriptorSet(recording.CommandBuffer, GpuBindPoint.Compute, pipeline.LayoutHandle, ((uint)ShaderInterfaceGroup.World), tables.WorldSet(tables.CurrentSlot));
        recording.Recorder.Dispatch(recording.CommandBuffer, ((uint)Count), 1, 1);
        return RenderGraphPackageOutcome.Drew;
    }
    public void Submitted() { if (m_context.Part == SdfWorldPackage.IndirectTrace) { m_built.Cache.Submitted(); } }
    public void Dispose() => m_built.Dispose();

    private static void Write(Span<byte> block, string member, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(block[((int)SdfWorldInterfaces.IndirectParameters.BlockOffsetOf(member))..], value);
}
