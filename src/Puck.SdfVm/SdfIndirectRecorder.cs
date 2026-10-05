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
    private readonly string m_prefix;
    private readonly string m_lightDepthVersion;

    private bool m_environmentRecorded;

    public SdfIndirectRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfIndirectPasses.Built built, SdfWorldPasses views) {
        m_context = context;
        m_built = built;
        m_views = views;
        m_sets = new RenderGraphPackageSets(context, groups, built.Tables.Pipeline(kernel: Kernel).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context: context, sets: m_sets);
        m_prefix = context.Pass[..^context.Part!.Length];
        m_lightDepthVersion = (m_prefix + SdfWorldPackage.IndirectLightDepth);
    }

    private int Count => m_context.Part switch {
        SdfWorldPackage.IndirectPlace => m_built.Cache.PlaceCount,
        SdfWorldPackage.IndirectClassify => m_built.Cache.ClassifyCount,
        SdfWorldPackage.IndirectShade => m_built.Cache.ShadeCount,
        _ => m_built.Cache.TraceCount,
    };
    private bool IsEnvironmentPin => (m_context.Part == SdfSkyEnvironmentGraph.Pin);
    private bool IsShade => (m_context.Part == SdfWorldPackage.IndirectShade);
    private SdfKernel Kernel => m_context.Part switch {
        SdfWorldPackage.IndirectTrace => SdfKernel.IndirectTrace,
        SdfWorldPackage.IndirectShade => SdfKernel.IndirectShade,
        _ => SdfKernel.IndirectClassify,
    };

    public IReadOnlyList<string> WorkDetails(in FrameContext context) => (IsEnvironmentPin ? [] : SdfWorldWorkDetails.Indirect);
    public bool Skips(in FrameContext context) {
        m_environmentRecorded = false;
        m_views.Begin(residency: m_built.Residency);
        if (!m_built.Residency.Prepare(context: context) || !ReferenceEquals(objA: m_built.Cache, objB: m_built.Residency.Tables!.Indirect)) { return true; }
        m_built.Residency.Tables.PlanIndirect(frame: m_built.Residency.Frame!);
        if (IsEnvironmentPin) { return (m_built.Cache.Frozen || (m_built.Cache.Lighting?.CanRecordEnvironment != true)); }
        return ((m_context.Part != SdfWorldPackage.IndirectTrace) && (Count == 0) && (!IsShade || (m_built.Cache.ShadeBatch is null)));
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        var tables = m_built.Residency.Submit(context: recording.Context);
        var cache = m_built.Cache;

        if (IsEnvironmentPin) {
            cache.Lighting!.RecordEnvironment(recording: recording, frame: m_built.Residency.Frame!, prefix: m_prefix);
            m_environmentRecorded = true;
            return RenderGraphPackageOutcome.Drew;
        }

        if ((Count == 0) && (m_context.Part != SdfWorldPackage.IndirectTrace)) { return RenderGraphPackageOutcome.Drew; }
        Span<byte> common = stackalloc byte[SdfFrameBlock.SizeBytes];

        common.Clear();
        var pinned = (IsShade ? cache.Lighting : null);

        SdfFrameBlock.Write(block: common, tables: (pinned?.Values ?? tables.PassValues), frame: (pinned?.Frame ?? m_built.Residency.Frame!), view: 0, width: 1, height: 1);
        SdfFrameBlock.WriteLightViews(common, m_built.Residency.IndirectLightViews, depthCamera: false,
            geometryOwner: ((pinned is null) ? null : tables), geometry: (pinned?.Snapshot?.Geometry ?? default));
        foreach (var (source, destination, length) in FrameCopies) { common.Slice(length: length, start: source).CopyTo(destination: recording.PassBlock.Slice(length: length, start: destination)); }
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectEpoch, value: cache.Epoch);
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectFrame, value: cache.Frame);
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectPlaceCount, value: ((uint)cache.PlaceCount));
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectClassifyCount, value: ((uint)cache.ClassifyCount));
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectTraceCount, value: ((uint)cache.TraceCount));
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectPhase, value: ((m_context.Part == SdfWorldPackage.IndirectPlace) ? 0u : 1u));
        var batch = cache.ShadeBatch;

        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectShadeCount, value: ((uint)cache.ShadeCount));
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectReadGeneration, value: ((uint)(batch?.ReadGeneration ?? 0)));
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectWriteGeneration, value: ((uint)(batch?.WriteGeneration ?? 0)));
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectReadPublication, value: ((batch is { Sweep: > 0 }) ? cache.PublishedStamp : 0u));
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectWritePublication, value: cache.WriteLightingStamp);
        Write(block: recording.PassBlock, member: SdfWorldPackage.IndirectFeedback, value: BitConverter.SingleToUInt32Bits(value: ((batch is { Sweep: > 0 }) ? 1f : 0f)));
        var set = m_sets.PassSet(slot: recording.Slot);
        var layout = SdfWorldInterfaces.IndirectParameters.Layout;

        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectCacheWritten, recording.Outputs[0].Buffer!);
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectBricks, cache.Regions[0].Buffer(slot: tables.CurrentSlot));
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectUpdates, cache.Regions[(IsShade ? 4 : 1)].Buffer(slot: tables.CurrentSlot));
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectDirections, cache.Regions[2].Buffer(slot: tables.CurrentSlot));
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectTraceStates, cache.Regions[3].Buffer(slot: tables.CurrentSlot));
        var lightDepth = tables.DummyBuffer;

        if (IsShade) {
            foreach (var input in recording.Inputs) {
                if (((input.Version == SdfWorldPackage.IndirectLightDepth) || (input.Version == m_lightDepthVersion)) &&
                    (input.Buffer is { } depth)) { lightDepth = depth; }
            }
        }
        tables.WriteInterfaceBuffer(set, layout, SdfWorldPackage.IndirectLightDepth, lightDepth);
        m_work.Write(passSet: set, recording: recording);
        var pipeline = tables.Pipeline(kernel: Kernel);

        recording.Recorder.BindPipeline(recording.CommandBuffer, GpuBindPoint.Compute, pipeline.Handle);
        m_sets.Bind(recording.Recorder, recording.CommandBuffer, GpuBindPoint.Compute, pipeline.LayoutHandle, recording.Slot);
        recording.Recorder.BindDescriptorSet(recording.CommandBuffer, GpuBindPoint.Compute, pipeline.LayoutHandle, ((uint)ShaderInterfaceGroup.World), (pinned?.WorldSet(slot: tables.CurrentSlot) ?? tables.WorldSet(slot: tables.CurrentSlot)));
        recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: ((uint)Math.Max(val1: 1, val2: Count)), groupCountY: 1, groupCountZ: 1);
        return RenderGraphPackageOutcome.Drew;
    }
    public void Submitted() {
        if (IsEnvironmentPin && m_environmentRecorded) {
            m_environmentRecorded = false;
            m_built.Cache.Lighting!.EnvironmentSubmitted(cache: m_built.Cache);
        }
        if (m_context.Part == SdfWorldPackage.IndirectTrace) { m_built.Cache.Submitted(); }
        if (IsShade) { m_built.Cache.SubmittedLighting(); }
    }
    public void Dispose() => m_built.Dispose();

    private static void Write(Span<byte> block, string member, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(destination: block[((int)SdfWorldInterfaces.IndirectParameters.BlockOffsetOf(member: member))..], value: value);
}
