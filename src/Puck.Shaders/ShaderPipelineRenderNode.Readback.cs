using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    // A package asks for ranges, never a barrier. The node tracks each transfer like every other unplanned access.
    private void RecordPackageReadback(RuntimePass pass, int slot, nint command, IGpuRecorder recorder) {
        if (pass.Package is not IRenderGraphPackageReadback source) {
            return;
        }

        for (var index = 0; source.TryReadback(index: index, readback: out var readback, slot: slot); index++) {
            RecordPackageCopy(command: command, pass: pass, readback: in readback, recorder: recorder, slot: slot);
        }
    }
    private void RecordPackageCopy(RuntimePass pass, in RenderGraphBufferReadback readback, int slot, nint command, IGpuRecorder recorder) {
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

            if ((readback.SizeBytes == 0) || (readback.DestinationOffsetBytes > readback.Destination.SizeBytes) ||
                (readback.SizeBytes > (readback.Destination.SizeBytes - readback.DestinationOffsetBytes)) ||
                (readback.SourceOffsetBytes > buffer.SizeBytes) || (readback.SizeBytes > (buffer.SizeBytes - readback.SourceOffsetBytes))) {
                throw new InvalidOperationException(message: $"Package readback '{readback.Version}' exceeds its buffer range.");
            }

            var transfer = new ShaderPipelineAccessState(Access: GpuAccess.TransferRead, Layout: GpuImageLayout.Undefined, Stage: GpuStage.Transfer);

            RecordBarrier(
                barrier: ShaderPipelineBarrier.Always(kind: resource.Spec.Kind, prior: access.Use, use: transfer),
                command: command, instance: instance, recorder: recorder, resource: resource
            );
            recorder.CopyBuffer(commandBufferHandle: command, destinationBufferHandle: readback.Destination.BufferHandle,
                sourceBufferHandle: buffer.BufferHandle, sizeBytes: readback.SizeBytes, sourceOffsetBytes: readback.SourceOffsetBytes,
                destinationOffsetBytes: readback.DestinationOffsetBytes);
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
