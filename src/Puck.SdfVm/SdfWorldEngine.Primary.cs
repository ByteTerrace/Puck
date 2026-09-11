using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    // The primary and shade dispatches use the same live view rectangles, tile masks, and indirect bbox. Every
    // active shaded pixel therefore reads a record written this frame. Child views are skipped by both kernels.
    private void RecordPrimary(nint commandBuffer, nint timingPool) {
        var recorder = m_gpu.ComputeRecorder;

        recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, label: "primary");
        recorder.BindComputePipeline(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            pipelineHandle: m_primaryPipeline.Handle
        );
        recorder.BindComputeDescriptorSet(
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_viewsSets[m_currentSlot],
            deviceHandle: m_deviceHandle,
            pipelineLayoutHandle: m_primaryPipeline.LayoutHandle
        );
        recorder.PushConstants(
            commandBufferHandle: commandBuffer,
            data: m_pushConstant,
            deviceHandle: m_deviceHandle,
            offset: 0,
            pipelineLayoutHandle: m_primaryPipeline.LayoutHandle,
            stageFlags: GpuShaderStage.Compute
        );
        recorder.DispatchIndirect(
            argumentBufferHandle: m_viewsArgsBuffer.BufferHandle,
            argumentBufferOffset: 0,
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );
        recorder.EndDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);
        WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 6, timingPool: timingPool);
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );
    }
}
