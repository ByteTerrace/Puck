using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    // A package asks for a range, never a barrier. The node tracks the transfer like every other unplanned access.
    private void RecordPackageReadback(RuntimePass pass, int slot, nint command, IGpuRecorder recorder) {
        if ((pass.Package is not IRenderGraphPackageReadback source) || !source.TryReadback(readback: out var readback, slot: slot)) {
            return;
        }

        foreach (var access in pass.Accesses) {
            if (!string.Equals(a: access.Version, b: readback.Version, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            var resource = m_resources[access.Storage];

            if (resource.Spec.Kind != ShaderPipelineResourceKind.Buffer) {
                throw new InvalidOperationException(message: $"Package readback '{readback.Version}' is not a buffer.");
            }

            var instance = InstanceIndex(resource: resource, slot: slot, previous: access.PreviousFrame);
            var buffer = ResolveBuffer(resource: resource, name: readback.Version, index: instance);

            if ((readback.SizeBytes == 0) || (readback.SizeBytes > readback.Destination.SizeBytes) ||
                (readback.SourceOffsetBytes > buffer.SizeBytes) || (readback.SizeBytes > (buffer.SizeBytes - readback.SourceOffsetBytes))) {
                throw new InvalidOperationException(message: $"Package readback '{readback.Version}' exceeds its buffer range.");
            }

            var transfer = new ShaderPipelineAccessState(Access: GpuAccess.TransferRead, Layout: GpuImageLayout.Undefined, Stage: GpuStage.Transfer);

            RecordBarrier(
                barrier: ShaderPipelineBarrier.Always(kind: resource.Spec.Kind, prior: access.Use, use: transfer),
                command: command, instance: instance, recorder: recorder, resource: resource
            );
            recorder.CopyBuffer(commandBufferHandle: command, destinationBufferHandle: readback.Destination.BufferHandle,
                sourceBufferHandle: buffer.BufferHandle, sizeBytes: readback.SizeBytes, sourceOffsetBytes: readback.SourceOffsetBytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: readback.Destination.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
            resource.SetOverride(instance: instance, state: transfer);
            return;
        }

        throw new InvalidOperationException(message: $"Package readback '{readback.Version}' is not bound to the recording pass.");
    }
    private void SubmitPackageReadbacks(int slot, IGpuSubmissionFence fence) {
        foreach (var pass in m_passes) {
            if (pass.Package is IRenderGraphPackageReadback readback) {
                readback.Submitted(fence: fence, slot: slot);
            }
        }
    }
}
