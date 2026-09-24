using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>Shared command recording operations for Direct3D 12 graphics and compute pipelines.</summary>
[SupportedOSPlatform("windows10.0.10240")]
internal static unsafe class DirectXRecorderOperations {
    public static void BindDescriptorSet(
        nint commandBufferHandle,
        nint descriptorSetHandle,
        bool isCompute,
        nint pipelineLayoutHandle
    ) {
        var state = DirectXCommandBufferState.Decode(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: pipelineLayoutHandle).Target!);
        var set = ((DirectXDescriptorSet)GCHandle.FromIntPtr(value: descriptorSetHandle).Target!);
        var heap = ((ID3D12DescriptorHeap*)set.HeapHandle);

        commandList->SetDescriptorHeaps(
            NumDescriptorHeaps: 1,
            ppDescriptorHeaps: &heap
        );

        if (0 <= layout.DescriptorTableParamIndex) {
            var handle = new D3D12_GPU_DESCRIPTOR_HANDLE { ptr = set.GpuBase };

            if (isCompute) {
                commandList->SetComputeRootDescriptorTable(
                    BaseDescriptor: handle,
                    RootParameterIndex: ((uint)layout.DescriptorTableParamIndex)
                );
            } else {
                commandList->SetGraphicsRootDescriptorTable(
                    BaseDescriptor: handle,
                    RootParameterIndex: ((uint)layout.DescriptorTableParamIndex)
                );
            }
        }
    }
    public static void EndCommandBuffer(nint commandBufferHandle) {
        var state = DirectXCommandBufferState.Decode(commandBufferHandle: commandBufferHandle);

        ((ID3D12GraphicsCommandList*)state.CommandList)->Close();
    }
    public static void PushConstants(
        nint commandBufferHandle,
        ReadOnlySpan<byte> data,
        bool isCompute,
        uint offset,
        nint pipelineLayoutHandle,
        GpuShaderStage stageFlags
    ) {
        GpuPushConstantBinding.ValidateRange(
            dataLength: data.Length,
            offset: offset,
            stageFlags: stageFlags
        );
        var state = DirectXCommandBufferState.Decode(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: pipelineLayoutHandle).Target!);

        if (0 > layout.RootConstantsParamIndex) {
            return;
        }

        fixed (byte* pData = data) {
            if (isCompute) {
                commandList->SetComputeRoot32BitConstants(
                    DestOffsetIn32BitValues: (offset / 4),
                    Num32BitValuesToSet: ((uint)(data.Length / 4)),
                    pSrcData: pData,
                    RootParameterIndex: ((uint)layout.RootConstantsParamIndex)
                );
            } else {
                commandList->SetGraphicsRoot32BitConstants(
                    DestOffsetIn32BitValues: (offset / 4),
                    Num32BitValuesToSet: ((uint)(data.Length / 4)),
                    pSrcData: pData,
                    RootParameterIndex: ((uint)layout.RootConstantsParamIndex)
                );
            }
        }
    }
}
