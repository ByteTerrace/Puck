using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    private ReadOnlyMemory<byte> DispatchIsaReport(IGpuComputePipeline viewsPipeline, IGpuStorageImage reportImage, IGpuStorageImage sampledImage, IGpuSurfaceReadback readback, bool initializeImages) {
        var commandBuffer = m_commandPools[0].CommandBufferHandle;
        var recorder = m_gpu.ComputeRecorder;

        recorder.BeginCommandBuffer(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );

        if (initializeImages) {
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                imageHandle: sampledImage.ImageHandle,
                newLayout: GpuImageLayout.ShaderReadOnly,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuComputeAccess.None,
                sourceStageMask: GpuComputeStage.TopOfPipe
            );
        }

        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderWrite,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            imageHandle: reportImage.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: (initializeImages
            ? GpuImageLayout.Undefined
            : GpuImageLayout.ShaderReadOnly),
            sourceAccessMask: (initializeImages
            ? GpuComputeAccess.None
            : GpuComputeAccess.ShaderRead),
            sourceStageMask: (initializeImages
            ? GpuComputeStage.TopOfPipe
            : GpuComputeStage.ComputeShader)
        );

        if (initializeImages) {
            recorder.BindComputePipeline(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                pipelineHandle: m_beamPipeline.Handle
            );
            recorder.BindComputeDescriptorSet(
                commandBufferHandle: commandBuffer,
                descriptorSetHandle: m_beamSets[0],
                deviceHandle: m_deviceHandle,
                pipelineLayoutHandle: m_beamPipeline.LayoutHandle
            );
            recorder.PushConstants(
                commandBufferHandle: commandBuffer,
                data: m_pushConstant,
                deviceHandle: m_deviceHandle,
                offset: 0,
                pipelineLayoutHandle: m_beamPipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Compute
            );
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                groupCountX: 1,
                groupCountY: 1,
                groupCountZ: 1
            );
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );
        }

        recorder.BindComputePipeline(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            pipelineHandle: viewsPipeline.Handle
        );
        recorder.BindComputeDescriptorSet(
            commandBufferHandle: commandBuffer,
            descriptorSetHandle: m_viewsSets[0],
            deviceHandle: m_deviceHandle,
            pipelineLayoutHandle: viewsPipeline.LayoutHandle
        );
        recorder.PushConstants(
            commandBufferHandle: commandBuffer,
            data: m_pushConstant,
            deviceHandle: m_deviceHandle,
            offset: 0,
            pipelineLayoutHandle: viewsPipeline.LayoutHandle,
            stageFlags: GpuShaderStage.Compute
        );
        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            groupCountX: 1,
            groupCountY: 1,
            groupCountZ: 1
        );
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            imageHandle: reportImage.ImageHandle,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );
        recorder.EndCommandBuffer(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );
        m_gpu.QueueSubmitter.SubmitAndWait(
            commandBufferHandles: [commandBuffer],
            deviceContext: m_deviceContext
        );

        return readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: Format,
            height: 1,
            sourceImageHandle: reportImage.ImageHandle,
            sourceLayout: GpuImageLayout.ShaderReadOnly,
            width: 1
        );
    }
    private void VerifyIsaVersion() {
        using var reportImage = m_gpu.StorageImageFactory.Create(
            deviceContext: m_deviceContext,
            format: Format,
            height: 1,
            width: 1
        );
        using var sampledImage = m_gpu.StorageImageFactory.Create(
            deviceContext: m_deviceContext,
            format: Format,
            height: 1,
            width: 1
        );
        using var readback = m_gpu.SurfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);
        var viewsSet = m_viewsSets[0];

        for (var element = 0u; (element < MaxViewports); element++) {
            m_descriptorAllocator.WriteStorageImage(
                arrayElement: element,
                binding: ViewSourceBindingIndex,
                descriptorSetHandle: viewsSet,
                deviceHandle: m_deviceHandle,
                imageViewHandle: reportImage.ImageViewHandle
            );
        }

        for (var element = 0; (element < ScreenSourceBindingIndices.Length); element++) {
            m_descriptorAllocator.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: ScreenSourceBindingIndices[element],
                descriptorSetHandle: viewsSet,
                deviceHandle: m_deviceHandle,
                imageViewHandle: sampledImage.ImageViewHandle,
                samplerHandle: m_screenSampler
            );
        }

        m_descriptorAllocator.WriteCombinedImageSampler(
            arrayElement: 0,
            binding: GlyphAtlasBindingIndex,
            descriptorSetHandle: viewsSet,
            deviceHandle: m_deviceHandle,
            imageViewHandle: sampledImage.ImageViewHandle,
            samplerHandle: m_screenSampler
        );
        _ = BitConverter.TryWriteBytes(
            destination: m_pushConstant.AsSpan(
                length: sizeof(uint),
                start: (8 * sizeof(uint))
            ),
            value: SdfShaderSetVerification.ReportRequest
        );

        try {
            var report = DispatchIsaReport(
                initializeImages: true,
                readback: readback,
                reportImage: reportImage,
                sampledImage: sampledImage,
                viewsPipeline: m_viewsPipeline
            );

            SdfShaderSetVerification.ValidateReport(
                report: report.Span,
                viewsVariant: "full views"
            );

            report = DispatchIsaReport(
                initializeImages: false,
                readback: readback,
                reportImage: reportImage,
                sampledImage: sampledImage,
                viewsPipeline: m_viewsCorePipeline
            );
            SdfShaderSetVerification.ValidateReport(
                report: report.Span,
                viewsVariant: "core views"
            );
        } finally {
            Array.Clear(array: m_pushConstant);
        }
    }
}
