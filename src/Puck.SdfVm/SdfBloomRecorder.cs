using System.Buffers.Binary;
using System.Globalization;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Hosting;

namespace Puck.SdfVm;

// The planner owns all images, barriers and timings; each recorder is one counted pyramid dispatch.
internal sealed class SdfBloomRecorder : IRenderGraphPackageRecorder {
    private readonly RenderGraphPackageRecorderContext m_context;
    private readonly SdfWorldPasses m_owner;
    private readonly RenderGraphPackageSets m_sets;
    private readonly RenderGraphPackageWorkCounters m_work;
    private readonly SdfKernel m_kernel;
    private readonly int m_level;
    private readonly nint m_sampler;

    private SdfWorldView m_view;
    private bool m_disposed;

    public SdfBloomRecorder(RenderGraphPackageRecorderContext context, RenderGraphPackageGroups groups, SdfWorldPasses owner, SdfWorldView view) {
        m_context = context;
        m_owner = owner;
        m_view = view;
        m_kernel = ((context.Part == "bloom-apply") ? SdfKernel.BloomApply : (context.Part!.StartsWith(comparisonType: StringComparison.Ordinal, value: "bloom-down-") ? SdfKernel.BloomDown : SdfKernel.BloomUp));
        m_level = ((m_kernel == SdfKernel.BloomApply) ? 0 : int.Parse(context.Part.AsSpan(start: (context.Part.LastIndexOf(value: '-') + 1)), CultureInfo.InvariantCulture));
        m_sets = new RenderGraphPackageSets(context, groups, view.Residency.Tables!.Pipeline(kernel: m_kernel).GroupLayoutHandles);
        m_work = new RenderGraphPackageWorkCounters(context: context, sets: m_sets);
        m_sampler = context.Services.Bindings.CreateSampler(filter: GpuSamplerFilter.Linear);
        for (var slot = 0; (slot < context.InFlightFrames); slot++) {
            context.Services.Bindings.WriteSampler(arrayElement: 0, binding: m_sets.BindingOf(member: "bloomSampler"),
                descriptorSetHandle: m_sets.PassSet(slot: slot), samplerHandle: m_sampler);
        }
    }

    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        m_context.Services.Bindings.DestroySampler(samplerHandle: m_sampler);
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
        m_owner.Begin(residency: m_view.Residency);
        var tables = m_view.Residency.Submit(context: recording.Context);
        var frame = m_view.Residency.Frame!;
        var snapshot = frame.Views[Math.Min(val1: m_view.View, val2: (frame.Views.Count - 1))];
        var bloom = (snapshot.Bloom ?? new CameraBloom());
        var set = m_sets.PassSet(slot: recording.Slot);
        var output = recording.Outputs[0].Image;
        // The coarse mix tapers smoothly when the requested radius crosses an octave. The output grid,
        // rather than the internal scene grid, determines support under dynamic resolution.
        var support = (bloom.Radius * Math.Min(val1: output.Width, val2: output.Height));

        // Six half-resolution levels end at one sixty-fourth of the output. Scale tent spacing as well
        // as octave weights, so support keeps growing with output size instead of saturating at six levels.
        Write(block: recording.PassBlock, name: "bloomRadius", value: Math.Max(val1: 0.5f,
            val2: ((bloom.Radius * Math.Min(val1: recording.FrameWidth, val2: recording.FrameHeight)) / 64f)));

        Write(block: recording.PassBlock, name: "bloomMix", value: (((m_kernel == SdfKernel.BloomApply) && (m_view.Residency.DebugMode == DebugViewModes.Bloom))
            ? -1f : (0.65f * Math.Clamp(max: 1f, min: 0f, value: (support / 2f)))));
        Write(block: recording.PassBlock, name: "bloomCutoff", value: ((((m_kernel == SdfKernel.BloomDown) && (m_level == 0)) || (m_kernel == SdfKernel.BloomApply)) ? bloom.Cutoff : 0f));
        Write(block: recording.PassBlock, name: "bloomScatter", value: (bloom.Scatter * (1f - Math.Clamp(snapshot.ScreenFidelity, 0f, 1f))));
        m_work.Write(passSet: set, recording: recording);
        var bindings = m_context.Services.Bindings;

        bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: "bloomSource"), descriptorSetHandle: set,
            imageViewHandle: recording.Inputs[0].Image.ImageViewHandle);
        bindings.WriteSampledImage(arrayElement: 0, binding: m_sets.BindingOf(member: "bloomFine"), descriptorSetHandle: set,
            imageViewHandle: recording.Inputs[((m_kernel == SdfKernel.BloomDown) ? 0 : 1)].Image.ImageViewHandle);
        bindings.WriteStorageImage(arrayElement: 0, binding: m_sets.BindingOf(member: "bloomOutput"), descriptorSetHandle: set,
            imageViewHandle: output.ImageViewHandle);
        var pipeline = tables.Pipeline(kernel: m_kernel);

        recording.Recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: recording.CommandBuffer, pipelineHandle: pipeline.Handle);
        m_sets.Bind(bindPoint: GpuBindPoint.Compute, commandBuffer: recording.CommandBuffer, pipelineLayout: pipeline.LayoutHandle, recorder: recording.Recorder, slot: recording.Slot);
        recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: ((output.Width + 7) / 8), groupCountY: ((output.Height + 7) / 8), groupCountZ: 1);
        return RenderGraphPackageOutcome.Drew;
    }

    private void Write(Span<byte> block, string name, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(destination: block[((int)m_context.Parameters.BlockOffsetOf(member: name))..], value: value);
}
