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
    private readonly bool m_temporal;
    private readonly SdfKernel m_kernel;

    private SdfWorldView m_view;
    private bool m_disposed;

    internal SdfResolveRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) {
        m_context = context;
        m_owner = owner;
        m_view = view;
        m_temporal = context.Parameters.Interface.Members.Any(static member => member.Name == SdfWorldPackage.HistoryColor);
        m_kernel = m_temporal ? SdfKernel.TemporalResolve : SdfKernel.Resolve;
        m_sets = new RenderGraphPackageSets(context: context, groups: groups, groupLayoutHandles: view.Residency.Tables!.Pipeline(kernel: m_kernel).GroupLayoutHandles);
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
        var render = m_owner.RenderExtentOf(instance: m_context.Instance)!.FrameAt(width: recording.FrameWidth, height: recording.FrameHeight);
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
        WriteBuffer(set: set, member: SdfWorldPackage.VisibilityRecords, buffer: recording.Inputs[1].Buffer!);
        WriteBuffer(set: set, member: SdfWorldPackage.CullBounds, buffer: recording.Inputs[2].Buffer!);
        WriteBuffer(set: set, member: SdfWorldPackage.ResolvedSurface, buffer: recording.Outputs[1].Buffer!);
        if (m_temporal) {
            bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(SdfWorldPackage.Reactivity),
                descriptorSetHandle: set, imageViewHandle: recording.Inputs[3].Image.ImageViewHandle);
            bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(SdfWorldPackage.HistoryColor),
                descriptorSetHandle: set, imageViewHandle: recording.Inputs[4].Image.ImageViewHandle);
            WriteBuffer(set: set, member: SdfWorldPackage.HistorySurface, buffer: recording.Inputs[5].Buffer!);
        }
        var pipeline = tables.Pipeline(kernel: m_kernel);

        recording.Recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: recording.CommandBuffer, pipelineHandle: pipeline.Handle);
        m_sets.Bind(bindPoint: GpuBindPoint.Compute, commandBuffer: recording.CommandBuffer, pipelineLayout: pipeline.LayoutHandle, recorder: recording.Recorder, slot: recording.Slot);
        if (m_temporal) {
            recording.Recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: recording.CommandBuffer,
                descriptorSetHandle: tables.WorldSet(slot: tables.CurrentSlot), group: ((uint)ShaderInterfaceGroup.World),
                pipelineLayoutHandle: pipeline.LayoutHandle);
        }
        recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: ((recording.Width + 7) / 8), groupCountY: ((recording.Height + 7) / 8), groupCountZ: 1);
        residency.MarkRendered(view: index);
        m_owner.MarkRendered(instance: m_context.Instance, view: in m_view);
        return RenderGraphPackageOutcome.Drew;
    }

    private void WriteBuffer(nint set, string member, IGpuBuffer buffer) {
        var resource = SdfWorldInterfaces.ResourceOf(layout: m_context.Parameters.Layout, member: member);

        m_context.Services.Bindings.WriteBuffer(binding: resource.Binding, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes, descriptorSetHandle: set, kind: resource.Kind, elementStride: resource.Member.Type!.Value.SizeBytes());
    }
}
