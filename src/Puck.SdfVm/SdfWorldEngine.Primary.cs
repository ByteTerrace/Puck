using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    // Primary, surface, ambient and views share live rectangles, masks and the indirect bbox. Every active
    // shaded pixel reads a record written this frame. All four kernels skip hosted child views. The pass's buffer
    // transitions order the previous hit pass's record writes before this one.
    private void RecordHitPass(nint commandBuffer, IGpuComputePipeline pipeline, SdfFramePass pass, string label, int workPass) {
        var recorder = m_gpu.ComputeRecorder;

        m_work.EnterPass(pass: workPass);
        RecordBufferBarriers(
            commandBuffer: commandBuffer,
            pass: pass
        );
        recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            label: label
        );
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
        recorder.EndDebugGroup(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );
        m_work.LeavePass();
    }
}
