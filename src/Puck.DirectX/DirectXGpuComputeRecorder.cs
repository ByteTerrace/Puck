using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Records compute work on Direct3D 12 direct command lists. Resource transitions use the same legacy
/// barrier model as graphics and readback; enhanced and legacy texture barriers cannot be mixed without
/// an explicit COMMON handoff. Shared resource-state tracking keeps compute, fullscreen draws, and
/// reused frame slots consistent. UAV barriers order repeated writes, including zero initialization.
/// Temporary clear descriptors belong to the fenced command buffer and retire when it is reused or disposed.
/// </summary>
/// <param name="deviceContext">The device context whose <see cref="DirectXDeviceContext.DispatchSignature"/> every
/// indirect dispatch uses.</param>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuComputeRecorder(DirectXDeviceContext deviceContext) : IGpuComputeRecorder, IGpuImageInitializationRecorder, IGpuBufferInitializationRecorder {
    /// <inheritdoc/>
    public void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var allocator = ((ID3D12CommandAllocator*)state.Allocator);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);

        state.ReleaseRetainedResources();
        state.BufferStates.Reset();
        allocator->Reset();
        commandList->Reset(
            pAllocator: allocator,
            pInitialState: null
        );
    }
    /// <inheritdoc/>
    public void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) =>
        DirectXRecorderOperations.EndCommandBuffer(commandBufferHandle: commandBufferHandle);
    /// <inheritdoc/>
    public void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) =>
        DirectXDebugLabel.Begin(
            commandList: ((ID3D12GraphicsCommandList*)DecodeState(commandBufferHandle: commandBufferHandle).CommandList),
            label: label
        );
    /// <inheritdoc/>
    public void EndDebugGroup(nint deviceHandle, nint commandBufferHandle) =>
        DirectXDebugLabel.End(commandList: ((ID3D12GraphicsCommandList*)DecodeState(commandBufferHandle: commandBufferHandle).CommandList));
    /// <inheritdoc/>
    public void BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: pipelineHandle).Target!);

        commandList->SetComputeRootSignature(pRootSignature: ((ID3D12RootSignature*)layout.RootSignatureHandle));
        commandList->SetPipelineState(pPipelineState: ((ID3D12PipelineState*)layout.PsoHandle));
    }
    /// <inheritdoc/>
    public void BindComputeDescriptorSet(
        nint deviceHandle,
        nint commandBufferHandle,
        nint pipelineLayoutHandle,
        nint descriptorSetHandle
    ) => DirectXRecorderOperations.BindDescriptorSet(
        commandBufferHandle: commandBufferHandle,
        descriptorSetHandle: descriptorSetHandle,
        isCompute: true,
        pipelineLayoutHandle: pipelineLayoutHandle
    );
    /// <inheritdoc/>
    public void PushConstants(
        nint deviceHandle,
        nint commandBufferHandle,
        nint pipelineLayoutHandle,
        GpuShaderStage stageFlags,
        uint offset,
        ReadOnlySpan<byte> data
    ) => DirectXRecorderOperations.PushConstants(
        commandBufferHandle: commandBufferHandle,
        data: data,
        isCompute: true,
        offset: offset,
        pipelineLayoutHandle: pipelineLayoutHandle,
        stageFlags: stageFlags
    );
    /// <inheritdoc/>
    public void Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        ((ID3D12GraphicsCommandList*)state.CommandList)->Dispatch(
            ThreadGroupCountX: groupCountX,
            ThreadGroupCountY: groupCountY,
            ThreadGroupCountZ: groupCountZ
        );
    }
    /// <inheritdoc/>
    public void DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var signature = ((ID3D12CommandSignature*)deviceContext.DispatchSignature);

        // No transition is recorded here. An upload-heap argument buffer stays in GENERIC_READ, which permits
        // INDIRECT_ARGUMENT reads; a GPU-written one reaches INDIRECT_ARGUMENT through the caller's TransitionBuffer.
        ((ID3D12GraphicsCommandList*)state.CommandList)->ExecuteIndirect(
            ArgumentBufferOffset: argumentBufferOffset,
            CountBufferOffset: 0,
            MaxCommandCount: 1,
            pArgumentBuffer: ((ID3D12Resource*)argumentBufferHandle),
            pCommandSignature: signature,
            pCountBuffer: null
        );
    }
    /// <inheritdoc/>
    public void ClearStorageImage(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) {
        ArgumentOutOfRangeException.ThrowIfZero(deviceHandle);
        ArgumentOutOfRangeException.ThrowIfZero(commandBufferHandle);
        ArgumentOutOfRangeException.ThrowIfZero(imageHandle);
        var descriptors = DirectXClearImageDescriptors.Create(
            deviceHandle: deviceHandle,
            imageHandle: imageHandle,
            format: DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format)
        );

        DecodeState(commandBufferHandle: commandBufferHandle).RetainedResources.Add(item: descriptors);
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var descriptorHeap = ((ID3D12DescriptorHeap*)descriptors.GpuHeapHandle);

        commandList->SetDescriptorHeaps(
            NumDescriptorHeaps: 1,
            ppDescriptorHeaps: &descriptorHeap
        );
        var clearColor = stackalloc float[4] { 0f, 0f, 0f, 0f };

        commandList->ClearUnorderedAccessViewFloat(
            descriptors.GpuHandle,
            descriptors.CpuHandle,
            ((ID3D12Resource*)imageHandle),
            clearColor,
            0,
            null
        );

    }
    /// <inheritdoc/>
    public void ClearStorageBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) {
        ArgumentOutOfRangeException.ThrowIfZero(deviceHandle);
        ArgumentOutOfRangeException.ThrowIfZero(commandBufferHandle);
        ArgumentOutOfRangeException.ThrowIfZero(bufferHandle);
        if (
            (sizeBytes == 0) ||
            ((sizeBytes & 3) != 0) ||
            ((sizeBytes / 4) > uint.MaxValue)
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(sizeBytes),
                sizeBytes,
                "Direct3D 12 buffer clears require a positive size divisible by four and representable by a raw UAV."
            );
        }

        var descriptors = DirectXClearBufferDescriptors.Create(
            bufferHandle: bufferHandle,
            deviceHandle: deviceHandle,
            sizeBytes: sizeBytes
        );

        DecodeState(commandBufferHandle: commandBufferHandle).RetainedResources.Add(item: descriptors);
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var descriptorHeap = ((ID3D12DescriptorHeap*)descriptors.GpuHeapHandle);

        commandList->SetDescriptorHeaps(
            NumDescriptorHeaps: 1,
            ppDescriptorHeaps: &descriptorHeap
        );
        var clearValues = stackalloc uint[4] { 0U, 0U, 0U, 0U };

        commandList->ClearUnorderedAccessViewUint(
            descriptors.GpuHandle,
            descriptors.CpuHandle,
            ((ID3D12Resource*)bufferHandle),
            clearValues,
            0,
            null
        );
        // ClearUnorderedAccessViewUint is a UAV write. Order it before the first shader access even when the
        // neutral transition remains UAV -> UAV (which correctly elides a state transition).
        // Buffer clear ordering is supplied by the following runtime buffer transition.
    }
    public void TransitionImageLayout(
        nint deviceHandle,
        nint commandBufferHandle,
        nint imageHandle,
        GpuImageLayout oldLayout,
        GpuImageLayout newLayout,
        GpuComputeAccess sourceAccessMask,
        GpuComputeAccess destinationAccessMask,
        GpuComputeStage sourceStageMask,
        GpuComputeStage destinationStageMask
    ) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        // Shared legacy state: the neutral oldLayout is Undefined on the first frame, so the prior state comes from
        // tracking. UNORDERED_ACCESS is both the private texture's creation state and a legal promotable BeforeState
        // while a simultaneous-access texture rests in COMMON.
        var before = DirectXResourceStates.Get(
            imageHandle,
            ToResourceState(layout: oldLayout)
        );
        var after = ToResourceState(layout: newLayout);

        if (before == after) {
            return;
        }

        var barrier = DirectXBarriers.Transition(
            after: after,
            before: before,
            resource: ((ID3D12Resource*)imageHandle)
        );

        commandList->ResourceBarrier(
            NumBarriers: 1,
            pBarriers: &barrier
        );
        DirectXResourceStates.Set(
            resource: imageHandle,
            state: after
        );
    }
    /// <inheritdoc/>
    public void MemoryBarrier(
        nint deviceHandle,
        nint commandBufferHandle,
        GpuComputeAccess sourceAccessMask,
        GpuComputeAccess destinationAccessMask,
        GpuComputeStage sourceStageMask,
        GpuComputeStage destinationStageMask
    ) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        // Shared legacy state: a global UAV barrier (null resource) ordering the prior UAV writes before the next reads.
        // Legacy D3D12 UAV barriers carry no access/stage scope, so the neutral masks are unused here.
        var barrier = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_UAV,
        };

        barrier.Anonymous.UAV = new D3D12_RESOURCE_UAV_BARRIER {
            pResource = ((ID3D12Resource*)null),
        };

        commandList->ResourceBarrier(
            NumBarriers: 1,
            pBarriers: &barrier
        );
    }
    /// <inheritdoc/>
    public void TransitionBuffer(
        nint deviceHandle,
        nint commandBufferHandle,
        nint bufferHandle,
        GpuComputeAccess sourceAccessMask,
        GpuComputeAccess destinationAccessMask,
        GpuComputeStage sourceStageMask,
        GpuComputeStage destinationStageMask
    ) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        // A buffer has no subresources, so a transition covers the whole resource. Its first transition in a recording
        // starts from the state the declared prior access promoted it to (DirectXBufferStates); an upload-heap
        // buffer's registered GENERIC_READ replaces that for the buffer's whole life.
        var barrier = state.BufferStates.Plan(
            after: DirectXBufferStates.RequiredState(access: destinationAccessMask),
            bufferHandle: bufferHandle,
            firstState: DirectXResourceStates.Get(
                fallback: DirectXBufferStates.RequiredState(access: sourceAccessMask),
                resource: bufferHandle
            )
        );
        var resourceBarrier = new D3D12_RESOURCE_BARRIER();

        switch (barrier.Kind) {
            case DirectXBufferBarrierKind.UnorderedAccess:
                // The buffer stays in UNORDERED_ACCESS: a UAV barrier on this buffer alone orders its earlier writes.
                resourceBarrier.Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_UAV;
                resourceBarrier.Anonymous.UAV = new D3D12_RESOURCE_UAV_BARRIER {
                    pResource = ((ID3D12Resource*)bufferHandle),
                };
                break;
            case DirectXBufferBarrierKind.Transition:
                resourceBarrier = DirectXBarriers.Transition(
                    after: barrier.After,
                    before: barrier.Before,
                    resource: ((ID3D12Resource*)bufferHandle)
                );
                break;
            default:
                return;
        }

        ((ID3D12GraphicsCommandList*)state.CommandList)->ResourceBarrier(
            NumBarriers: 1,
            pBarriers: &resourceBarrier
        );
    }

    private sealed unsafe class DirectXClearImageDescriptors : IDisposable {
        private nint m_cpuHeap;
        private nint m_gpuHeap;

        public D3D12_CPU_DESCRIPTOR_HANDLE CpuHandle { get; private set; }
        public D3D12_GPU_DESCRIPTOR_HANDLE GpuHandle { get; private set; }
        public nint GpuHeapHandle => m_gpuHeap;

        public static DirectXClearImageDescriptors Create(nint deviceHandle, nint imageHandle, DXGI_FORMAT format) {
            var device = ((ID3D12Device*)deviceHandle);
            // One UAV descriptor: a CPU-only heap for the clear's CPU handle, a shader-visible one for its GPU handle.
            var cpuHeap = DirectXDescriptorHeaps.Create(
                count: 1,
                device: device,
                shaderVisible: false,
                type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV
            );
            ID3D12DescriptorHeap* gpuHeap;

            try {
                gpuHeap = DirectXDescriptorHeaps.Create(
                    count: 1,
                    device: device,
                    shaderVisible: true,
                    type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV
                );
            } catch { _ = ((IUnknown*)cpuHeap)->Release(); throw; }
            var result = new DirectXClearImageDescriptors {
                m_cpuHeap = ((nint)cpuHeap),
                m_gpuHeap = ((nint)gpuHeap),
                CpuHandle = DirectXConstants.GetCpuHeapStart(heap: cpuHeap),
                GpuHandle = DirectXConstants.GetGpuHeapStart(heap: gpuHeap),
            };
            var uav = new D3D12_UNORDERED_ACCESS_VIEW_DESC {
                Format = format,
                ViewDimension = D3D12_UAV_DIMENSION.D3D12_UAV_DIMENSION_TEXTURE2D,
            };

            uav.Anonymous.Texture2D = new D3D12_TEX2D_UAV { MipSlice = 0, PlaneSlice = 0, };
            device->CreateUnorderedAccessView(
                DestDescriptor: result.CpuHandle,
                pCounterResource: null,
                pDesc: &uav,
                pResource: ((ID3D12Resource*)imageHandle)
            );
            device->CreateUnorderedAccessView(
                DestDescriptor: DirectXConstants.GetCpuHeapStart(heap: gpuHeap),
                pCounterResource: null,
                pDesc: &uav,
                pResource: ((ID3D12Resource*)imageHandle)
            );
            return result;
        }
        public void Dispose() {
            DirectXConstants.Release(pointer: ref m_cpuHeap);
            DirectXConstants.Release(pointer: ref m_gpuHeap);
        }
    }
    private sealed unsafe class DirectXClearBufferDescriptors : IDisposable {
        private nint m_cpuHeap;
        private nint m_gpuHeap;

        public D3D12_CPU_DESCRIPTOR_HANDLE CpuHandle { get; private set; }
        public D3D12_GPU_DESCRIPTOR_HANDLE GpuHandle { get; private set; }
        public nint GpuHeapHandle => m_gpuHeap;

        public static DirectXClearBufferDescriptors Create(nint deviceHandle, nint bufferHandle, ulong sizeBytes) {
            var device = ((ID3D12Device*)deviceHandle);
            // One UAV descriptor: a CPU-only heap for the clear's CPU handle, a shader-visible one for its GPU handle.
            var cpuHeap = DirectXDescriptorHeaps.Create(
                count: 1,
                device: device,
                shaderVisible: false,
                type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV
            );
            ID3D12DescriptorHeap* gpuHeap;

            try {
                gpuHeap = DirectXDescriptorHeaps.Create(
                    count: 1,
                    device: device,
                    shaderVisible: true,
                    type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV
                );
            } catch { _ = ((IUnknown*)cpuHeap)->Release(); throw; }
            var result = new DirectXClearBufferDescriptors {
                m_cpuHeap = ((nint)cpuHeap),
                m_gpuHeap = ((nint)gpuHeap),
                CpuHandle = DirectXConstants.GetCpuHeapStart(heap: cpuHeap),
                GpuHandle = DirectXConstants.GetGpuHeapStart(heap: gpuHeap),
            };
            var uav = new D3D12_UNORDERED_ACCESS_VIEW_DESC {
                Format = DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS,
                ViewDimension = D3D12_UAV_DIMENSION.D3D12_UAV_DIMENSION_BUFFER,
            };

            uav.Anonymous.Buffer = new D3D12_BUFFER_UAV {
                CounterOffsetInBytes = 0,
                FirstElement = 0,
                Flags = D3D12_BUFFER_UAV_FLAGS.D3D12_BUFFER_UAV_FLAG_RAW,
                NumElements = checked((uint)(sizeBytes / 4)),
                StructureByteStride = 0,
            };
            device->CreateUnorderedAccessView(
                DestDescriptor: result.CpuHandle,
                pCounterResource: null,
                pDesc: &uav,
                pResource: ((ID3D12Resource*)bufferHandle)
            );
            device->CreateUnorderedAccessView(
                DestDescriptor: DirectXConstants.GetCpuHeapStart(heap: gpuHeap),
                pCounterResource: null,
                pDesc: &uav,
                pResource: ((ID3D12Resource*)bufferHandle)
            );
            return result;
        }
        public void Dispose() {
            DirectXConstants.Release(pointer: ref m_cpuHeap);
            DirectXConstants.Release(pointer: ref m_gpuHeap);
        }
    }

    private static DirectXCommandBufferState DecodeState(nint commandBufferHandle) =>
        DirectXCommandBufferState.Decode(commandBufferHandle: commandBufferHandle);
    private static D3D12_RESOURCE_STATES ToResourceState(GpuImageLayout layout) =>
        // General and Undefined both resolve to the compute read/write state (the kernel's working layout).
        (DirectXGpuFormats.TryToResourceState(
            layout: layout,
            resourceState: out var resourceState
        )
            ? resourceState
            : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS
        );
}
