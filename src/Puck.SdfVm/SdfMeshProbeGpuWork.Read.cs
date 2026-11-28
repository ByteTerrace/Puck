using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

internal sealed partial class SdfMeshProbeGpuWork {
    // An exclusive query copies at most ReadRecordLimit records under the same scene/window admission as a relight.
    // CopyToReadback orders both device sources and the slot scratch; completion owns and releases the readback.
    private void RecordRead(Job query, in SdfGpuWorkRecording recording) {
        var slots = query.RecordSlots!;
        var bytes = (ulong)(slots.Length * ReadRecordWords + 1) * sizeof(uint);
        var result = recording.DeviceBuffer("records", bytes, GpuBufferUsage.Storage, GpuBufferSizing.Exact);
        recording.ReserveReadback(bytes);
        var recorder = recording.Recorder;
        var command = recording.CommandBuffer;
        var feedback = m_feedback!.Buffer(0);
        var cache = m_hitCache.Buffer!;
        recorder.TransitionBuffer(command, result.BufferHandle, GpuAccess.None, GpuAccess.CopyWrite, GpuStage.TopOfPipe, GpuStage.Transfer);
        recorder.ClearStorageBuffer(command, result.BufferHandle, bytes);
        recorder.TransitionBuffer(command, feedback.BufferHandle, GpuAccess.ShaderRead, GpuAccess.TransferRead, SdfWorldTables.TableReaders, GpuStage.Transfer);
        recorder.TransitionBuffer(command, cache.BufferHandle, GpuAccess.ShaderRead, GpuAccess.TransferRead, GpuStage.ComputeShader, GpuStage.Transfer);
        for (var i = 0; i < slots.Length; i++) {
            if (slots[i] < 0) { continue; }
            var destination = (ulong)i * ReadRecordWords * sizeof(uint);
            recorder.CopyBuffer(command, feedback.BufferHandle, result.BufferHandle, (ulong)SdfMeshIrradianceProbe.ProbeWords * sizeof(uint),
                (ulong)slots[i] * SdfMeshIrradianceProbe.ProbeWords * sizeof(uint), destination + SdfMeshProbeRelight.OutputWords * sizeof(uint));
            if (query.CacheSlots.Span[i] == uint.MaxValue) { continue; }
            recorder.CopyBuffer(command, cache.BufferHandle, result.BufferHandle, (ulong)SdfMeshProbeRelight.OutputWords * sizeof(uint),
                ((ulong)query.CacheSlots.Span[i] * SdfMeshProbeRelight.CacheWords + SdfMeshProbeRelight.CacheResultWord) * sizeof(uint), destination);
        }
        recorder.TransitionBuffer(command, feedback.BufferHandle, GpuAccess.TransferRead, GpuAccess.ShaderRead, GpuStage.Transfer, SdfWorldTables.TableReaders);
        recorder.TransitionBuffer(command, cache.BufferHandle, GpuAccess.TransferRead, GpuAccess.ShaderRead, GpuStage.Transfer, GpuStage.ComputeShader);
        recording.CopyToReadback(result, bytes);
    }
}
