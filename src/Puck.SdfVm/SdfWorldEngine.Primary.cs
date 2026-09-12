using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    // Primary, surface, ambient and views share live rectangles, masks and the indirect bbox. Every active
    // shaded pixel reads a record written this frame. All four kernels skip hosted child views.
    private void RecordHitPass(nint commandBuffer, nint timingPool, IGpuComputePipeline pipeline, string label, uint timingMark) {
        var recorder = m_gpu.ComputeRecorder;

        recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, label: label);
        recorder.BindComputePipeline(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            pipelineHandle: pipeline.Handle
        );
        recorder.BindComputeDescriptorSet(
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_viewsSets[m_currentSlot],
            deviceHandle: m_deviceHandle,
            pipelineLayoutHandle: pipeline.LayoutHandle
        );
        recorder.PushConstants(
            commandBufferHandle: commandBuffer,
            data: m_pushConstant,
            deviceHandle: m_deviceHandle,
            offset: 0,
            pipelineLayoutHandle: pipeline.LayoutHandle,
            stageFlags: GpuShaderStage.Compute
        );
        recorder.DispatchIndirect(
            argumentBufferHandle: m_viewsArgsBuffer.BufferHandle,
            argumentBufferOffset: 0,
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );
        recorder.EndDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);
        WriteTimingMark(commandBuffer: commandBuffer, queryIndex: timingMark, timingPool: timingPool);
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );
    }
}
