using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// Reconstruction uses the residency's optional reloadable pipeline: native views allocate none of its GPU resources.
internal sealed class SdfResolveRecorder : IRenderGraphPackageRecorder {
    // Resolve-only values may shift common members. Compile the name-based copies once; native pass blocks retain
    // their established layout, and the one frame writer remains authoritative for common values.
    private static readonly (int Source, int Destination, int Length)[] FrameCopies = [.. SdfWorldPackage.Values.Select(selector: static member => (
        Source: ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: member.Name)),
        Destination: ((int)SdfWorldInterfaces.ResolveParameters.BlockOffsetOf(member: member.Name)),
        Length: checked((int)(member.Type!.Value.SizeBytes() * (member.Length ?? 1)))))];

    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly SdfWorldPasses m_owner;
    private readonly RenderGraphPackageSets m_sets;
    private readonly RenderGraphPackageWorkCounters m_work;

    private SdfWorldView m_view;
    private bool m_disposed;

    internal SdfResolveRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) {
        m_context = context;
        m_owner = owner;
        m_view = view;
        m_sets = new RenderGraphPackageSets(context: context, groups: groups, groupLayoutHandles: view.Residency.Tables!.Pipeline(kernel: SdfKernel.Resolve).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context: context, sets: m_sets);

    }

    public void Dispose() {
        if (m_disposed) {
            return;
        }
        m_disposed = true;
        m_owner.Unhold(residency: m_view.Residency);
        m_view.Residency.Release();
    }
    public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
        if ((m_owner.ViewOf(instance: m_context.Instance) is { } current) && (current != m_view)) {
            if (!ReferenceEquals(objA: current.Residency, objB: m_view.Residency)) {
                current.Residency.Retain();
                m_owner.Hold(residency: current.Residency);
                m_owner.Unhold(residency: m_view.Residency);
                m_view.Residency.Release();
            }
            m_view = current;
        }
        var residency = m_view.Residency;

        m_owner.Begin(residency: residency);
        var tables = residency.Submit(context: recording.Context);
        var frame = residency.Frame!;
        var index = Math.Min(val1: m_view.View, val2: (frame.Views.Count - 1));
        var render = (Width: recording.RenderWidth, Height: recording.RenderHeight);
        Span<byte> frameBlock = stackalloc byte[SdfFrameBlock.SizeBytes];

        frameBlock.Clear();
        SdfFrameBlock.Write(block: frameBlock, tables: tables.PassValues, frame: frame, view: index, width: render.Width, height: render.Height);
        var temporal = m_owner.TemporalOf(instance: m_context.Instance, view: m_view, width: recording.FrameWidth, height: recording.FrameHeight, debug: tables.PassValues.DebugMode, renderWidth: render.Width, renderHeight: render.Height);

        SdfFrameBlock.WriteTemporal(block: frameBlock, jitter: temporal.Jitter, historyFrames: temporal.Frames);
        SdfFrameBlock.WritePreviousView(block: frameBlock, view: temporal.PreviousView, valid: temporal.HasPreviousView);
        foreach (var copy in FrameCopies) {
            frameBlock.Slice(length: copy.Length, start: copy.Source).CopyTo(destination: recording.PassBlock.Slice(length: copy.Length, start: copy.Destination));
        }
        BinaryPrimitives.WriteSingleLittleEndian(destination: recording.PassBlock[((int)SdfWorldInterfaces.ResolveParameters.BlockOffsetOf(member: SdfWorldPackage.UpscaleSharpness))..], value: frame.Views[index].UpscaleSharpness);
        var set = m_sets.PassSet(slot: recording.Slot);

        m_work.Write(passSet: set, recording: recording);
        var bindings = m_context.Services.Bindings;

        bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.CurrentColor), descriptorSetHandle: set, imageViewHandle: recording.Inputs[0].Image.ImageViewHandle);
        bindings.WriteStorageImage(arrayElement: 0, binding: m_sets.BindingOf(member: SdfWorldPackage.Output), descriptorSetHandle: set, imageViewHandle: recording.Outputs[0].Image.ImageViewHandle);
        var pipeline = tables.Pipeline(kernel: SdfKernel.Resolve);

        recording.Recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: recording.CommandBuffer, pipelineHandle: pipeline.Handle);
        m_sets.Bind(bindPoint: GpuBindPoint.Compute, commandBuffer: recording.CommandBuffer, pipelineLayout: pipeline.LayoutHandle, recorder: recording.Recorder, slot: recording.Slot);
        recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: ((recording.Width + 7) / 8), groupCountY: ((recording.Height + 7) / 8), groupCountZ: 1);
        residency.MarkRendered(view: index);
        m_owner.MarkRendered(instance: m_context.Instance, view: in m_view);
        return RenderGraphPackageOutcome.Drew;
    }
}
