using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    private static ulong PackageReadbackBytes(RuntimePass[] passes) => passes.Aggregate(0UL,
        static (bytes, pass) => checked((bytes + ((pass?.Package as IRenderGraphPackageReadback)?.ReadbackBytes ?? 0UL))));
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
            if (!access.ReferencesVersion(version: readback.Version)) {
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
                sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
            resource.SetOverride(instance: instance, state: transfer);
            return;
        }

        throw new InvalidOperationException(message: $"Package readback '{readback.Version}' is not bound to the recording pass.");
    }
    // The one door a readback is armed through: only a pass whose recording drew this frame is armed, so a pass that
    // skipped, stood or drew nothing never arms an earlier recording's result under this submission. The slot then owes
    // its readback passes a completion, which PollReadbacks proves early once its fence reads signaled.
    private void SubmitPackageReadbacks(int slot) {
        foreach (var pass in m_passes) {
            if (pass.Drew && (pass.Package is IRenderGraphPackageReadback readback)) {
                readback.Submitted(slot: slot);
                m_slots[slot].ReadbacksOwed = true;
            }
        }
    }
    // Waits, through WaitSlot, every slot whose readback passes are owed a completion and whose fence already reads
    // signaled, so a result lands while the node stands instead of at the slot's reuse. The wait does not block and is
    // the proof WaitSlot gives at reuse: a lost device throws from it and nothing is completed.
    private void RetireSignaledReadbacks() {
        for (var slot = 0; (slot < m_slots.Length); slot++) {
            if (m_slots[slot] is { ReadbacksOwed: true, Fence: { IsSignaled: true } }) {
                WaitSlot(slot: slot);
            }
        }
    }
    private bool ReadbacksOwed {
        get {
            foreach (var slot in m_slots) {
                if (slot.ReadbacksOwed) { return true; }
            }
            return false;
        }
    }
    // Called only after the slot's fence was waited (WaitSlot), never on a device loss's release.
    private void CompletePackageReadbacks(int slot) {
        m_slots[slot].ReadbacksOwed = false;
        CompleteReadbacks(passes: m_passes, slot: slot);
    }
    // Tells every readback pass of a graph that the slot's previous submission completed.
    private static void CompleteReadbacks(RuntimePass[] passes, int slot) {
        foreach (var pass in passes) {
            if (pass?.Package is IRenderGraphPackageReadback readback) {
                readback.Completed(slot: slot);
            }
        }
    }
}
