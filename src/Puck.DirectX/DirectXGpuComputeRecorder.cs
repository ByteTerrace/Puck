using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuComputeRecorder : IGpuComputeRecorder, IGpuImageInitializationRecorder, IGpuBufferInitializationRecorder, IDisposable {
    // The DISPATCH command signature for ExecuteIndirect, cached per ID3D12Device* (one signature serves every
    // indirect dispatch on a device). Released on Dispose; the service provider disposes this recorder singleton.
    private readonly ConcurrentDictionary<nint, nint> m_dispatchSignatures = new();

    /// <inheritdoc/>
    public void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var allocator = ((ID3D12CommandAllocator*)state.Allocator);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);

        state.ReleaseRetainedResources();
        allocator->Reset();
        commandList->Reset(pAllocator: allocator, pInitialState: null);
    }
    /// <inheritdoc/>
    public void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        ((ID3D12GraphicsCommandList*)state.CommandList)->Close();
    }
    /// <inheritdoc/>
    public void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) =>
        DirectXDebugLabel.Begin(commandList: ((ID3D12GraphicsCommandList*)DecodeState(commandBufferHandle: commandBufferHandle).CommandList), label: label);
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
    ) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: pipelineLayoutHandle).Target!);
        var set = ((DirectXDescriptorSet)GCHandle.FromIntPtr(value: descriptorSetHandle).Target!);
        var heap = ((ID3D12DescriptorHeap*)set.HeapHandle);

        commandList->SetDescriptorHeaps(NumDescriptorHeaps: 1, ppDescriptorHeaps: &heap);

        if (0 <= layout.DescriptorTableParamIndex) {
            commandList->SetComputeRootDescriptorTable(
                ((uint)layout.DescriptorTableParamIndex),
                new D3D12_GPU_DESCRIPTOR_HANDLE { ptr = set.GpuBase }
            );
        }
    }
    /// <inheritdoc/>
    public void PushConstants(
        nint deviceHandle,
        nint commandBufferHandle,
        nint pipelineLayoutHandle,
        GpuShaderStage stageFlags,
        uint offset,
        ReadOnlySpan<byte> data
    ) {
        GpuPushConstantBinding.ValidateRange(stageFlags: stageFlags, offset: offset, dataLength: data.Length);
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: pipelineLayoutHandle).Target!);

        if (0 > layout.RootConstantsParamIndex) {
            return;
        }

        fixed (byte* pData = data) {
            commandList->SetComputeRoot32BitConstants(
                RootParameterIndex: ((uint)layout.RootConstantsParamIndex),
                Num32BitValuesToSet: ((uint)(data.Length / 4)),
                pSrcData: pData,
                DestOffsetIn32BitValues: (offset / 4)
            );
        }
    }
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
        var signature = ((ID3D12CommandSignature*)GetOrCreateDispatchSignature(deviceHandle: deviceHandle));

        // The argument buffer is an upload-heap resource permanently in GENERIC_READ (which already permits
        // INDIRECT_ARGUMENT reads), so no resource-state transition is recorded before the indirect dispatch.
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
        var descriptors = DirectXClearImageDescriptors.Create(deviceHandle: deviceHandle, imageHandle: imageHandle, format: DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format));
        DecodeState(commandBufferHandle).RetainedResources.Add(descriptors);
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var descriptorHeap = ((ID3D12DescriptorHeap*)descriptors.GpuHeapHandle);
        commandList->SetDescriptorHeaps(NumDescriptorHeaps: 1, ppDescriptorHeaps: &descriptorHeap);
        var clearColor = stackalloc float[4] { 0f, 0f, 0f, 0f };
        commandList->ClearUnorderedAccessViewFloat(
            descriptors.GpuHandle,
            descriptors.CpuHandle,
            ((ID3D12Resource*)imageHandle),
            clearColor,
            0,
            null);

    }
    /// <inheritdoc/>
    public void ClearStorageBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) {
        ArgumentOutOfRangeException.ThrowIfZero(deviceHandle);
        ArgumentOutOfRangeException.ThrowIfZero(commandBufferHandle);
        ArgumentOutOfRangeException.ThrowIfZero(bufferHandle);
        if (sizeBytes == 0 || (sizeBytes & 3) != 0 || sizeBytes / 4 > uint.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "Direct3D 12 buffer clears require a positive size divisible by four and representable by a raw UAV.");
        }

        var descriptors = DirectXClearBufferDescriptors.Create(deviceHandle: deviceHandle, bufferHandle: bufferHandle, sizeBytes: sizeBytes);
        DecodeState(commandBufferHandle).RetainedResources.Add(descriptors);
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var descriptorHeap = ((ID3D12DescriptorHeap*)descriptors.GpuHeapHandle);
        commandList->SetDescriptorHeaps(NumDescriptorHeaps: 1, ppDescriptorHeaps: &descriptorHeap);
        var clearValues = stackalloc uint[4] { 0U, 0U, 0U, 0U };
        commandList->ClearUnorderedAccessViewUint(
            descriptors.GpuHandle,
            descriptors.CpuHandle,
            ((ID3D12Resource*)bufferHandle),
            clearValues,
            0,
            null);
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
        var before = DirectXResourceStates.Get(imageHandle, ToResourceState(oldLayout));
        var after = ToResourceState(layout: newLayout);

        if (before == after) {
            return;
        }

        var barrier = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        barrier.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            StateAfter = after,
            StateBefore = before,
            Subresource = DirectXConstants.AllSubresources,
            pResource = ((ID3D12Resource*)imageHandle),
        };

        commandList->ResourceBarrier(NumBarriers: 1, pBarriers: &barrier);
        DirectXResourceStates.Set(imageHandle, after);
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

        commandList->ResourceBarrier(NumBarriers: 1, pBarriers: &barrier);
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

        // Shared legacy state: a per-resource TRANSITION barrier between the access states (e.g. UNORDERED_ACCESS ->
        // INDIRECT_ARGUMENT). Buffers have no subresources, so transition the whole resource.
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var before = DirectXResourceStates.Get(bufferHandle, ToBufferResourceState(accessMask: sourceAccessMask));
        var after = ToBufferResourceState(accessMask: destinationAccessMask);

        if (before == after || (after != D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS && (before & after) == after)) {
            if ((after & D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS) != 0) {
                MemoryBarrier(deviceHandle, commandBufferHandle, sourceAccessMask, destinationAccessMask, sourceStageMask, destinationStageMask);
            }
            return;
        }
        var transition = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        transition.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            StateAfter = after,
            StateBefore = before,
            Subresource = DirectXConstants.AllSubresources,
            pResource = ((ID3D12Resource*)bufferHandle),
        };

        commandList->ResourceBarrier(NumBarriers: 1, pBarriers: &transition);
        DirectXResourceStates.Set(bufferHandle, after);
    }

    // Access mask -> the resource state required by the next buffer use.
    private static D3D12_RESOURCE_STATES ToBufferResourceState(GpuComputeAccess accessMask) {
        if (0 != (accessMask & GpuComputeAccess.TransferWrite)) {
            return D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        }
        if (0 != (accessMask & GpuComputeAccess.IndirectCommandRead)) {
            return D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_INDIRECT_ARGUMENT;
        }

        if (0 != (accessMask & GpuComputeAccess.ShaderWrite)) {
            return D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        }

        if (0 != (accessMask & GpuComputeAccess.ShaderRead)) {
            return D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        }

        return D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
    }
    // The cached one-argument DISPATCH command signature for a device (ByteStride = sizeof(D3D12_DISPATCH_ARGUMENTS),
    // a single DISPATCH argument, no root signature — a pure-dispatch signature binds nothing). Same per-device cache
    // a concurrent loser releases its duplicate so exactly one signature is kept.
    private nint GetOrCreateDispatchSignature(nint deviceHandle) {
        if (m_dispatchSignatures.TryGetValue(key: deviceHandle, value: out var existing)) {
            return existing;
        }

        var device = ((ID3D12Device*)deviceHandle);
        var argumentDesc = new D3D12_INDIRECT_ARGUMENT_DESC {
            Type = D3D12_INDIRECT_ARGUMENT_TYPE.D3D12_INDIRECT_ARGUMENT_TYPE_DISPATCH,
        };
        var signatureDesc = new D3D12_COMMAND_SIGNATURE_DESC {
            ByteStride = ((uint)sizeof(D3D12_DISPATCH_ARGUMENTS)),
            NumArgumentDescs = 1,
            pArgumentDescs = &argumentDesc,
        };

        void* signature;
        var signatureIid = ID3D12CommandSignature.IID_Guid;

        device->CreateCommandSignature(
            pDesc: in signatureDesc,
            pRootSignature: null,
            ppvCommandSignature: &signature,
            riid: in signatureIid
        );

        var handle = ((nint)signature);

        if (!m_dispatchSignatures.TryAdd(key: deviceHandle, value: handle)) {
            _ = ((IUnknown*)handle)->Release();

            return m_dispatchSignatures[deviceHandle];
        }

        return handle;
    }

    /// <summary>Releases the cached command signatures. The service provider disposes this recorder singleton.</summary>
    public void Dispose() {
        foreach (var signature in m_dispatchSignatures.Values) {
            _ = ((IUnknown*)signature)->Release();
        }

        m_dispatchSignatures.Clear();
    }

    private sealed unsafe class DirectXClearImageDescriptors : IDisposable {
        private nint m_cpuHeap;
        private nint m_gpuHeap;
        public D3D12_CPU_DESCRIPTOR_HANDLE CpuHandle { get; private set; }
        public D3D12_GPU_DESCRIPTOR_HANDLE GpuHandle { get; private set; }
        public nint GpuHeapHandle => m_gpuHeap;

        public static DirectXClearImageDescriptors Create(nint deviceHandle, nint imageHandle, DXGI_FORMAT format) {
            var device = ((ID3D12Device*)deviceHandle);
            var cpuHeap = CreateHeap(device, D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_NONE);
            ID3D12DescriptorHeap* gpuHeap;
            try { gpuHeap = CreateHeap(device, D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE); }
            catch { _ = ((IUnknown*)cpuHeap)->Release(); throw; }
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
                pResource: ((ID3D12Resource*)imageHandle));
            device->CreateUnorderedAccessView(
                DestDescriptor: DirectXConstants.GetCpuHeapStart(heap: gpuHeap),
                pCounterResource: null,
                pDesc: &uav,
                pResource: ((ID3D12Resource*)imageHandle));
            return result;
        }

        public void Dispose() {
            DirectXConstants.Release(ref m_cpuHeap);
            DirectXConstants.Release(ref m_gpuHeap);
        }

        private static ID3D12DescriptorHeap* CreateHeap(ID3D12Device* device, D3D12_DESCRIPTOR_HEAP_FLAGS flags) {
            var description = new D3D12_DESCRIPTOR_HEAP_DESC {
                Flags = flags,
                NodeMask = 0,
                NumDescriptors = 1,
                Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
            };
            device->CreateDescriptorHeap(
                pDescriptorHeapDesc: in description,
                ppvHeap: out var heap,
                riid: ID3D12DescriptorHeap.IID_Guid);
            return (ID3D12DescriptorHeap*)heap;
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
            var cpuHeap = CreateHeap(device, D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_NONE);
            ID3D12DescriptorHeap* gpuHeap;
            try { gpuHeap = CreateHeap(device, D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE); }
            catch { _ = ((IUnknown*)cpuHeap)->Release(); throw; }
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
                pResource: ((ID3D12Resource*)bufferHandle));
            device->CreateUnorderedAccessView(
                DestDescriptor: DirectXConstants.GetCpuHeapStart(heap: gpuHeap),
                pCounterResource: null,
                pDesc: &uav,
                pResource: ((ID3D12Resource*)bufferHandle));
            return result;
        }

        public void Dispose() {
            DirectXConstants.Release(ref m_cpuHeap);
            DirectXConstants.Release(ref m_gpuHeap);
        }

        private static ID3D12DescriptorHeap* CreateHeap(ID3D12Device* device, D3D12_DESCRIPTOR_HEAP_FLAGS flags) {
            var description = new D3D12_DESCRIPTOR_HEAP_DESC {
                Flags = flags,
                NodeMask = 0,
                NumDescriptors = 1,
                Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
            };
            device->CreateDescriptorHeap(
                pDescriptorHeapDesc: in description,
                ppvHeap: out var heap,
                riid: ID3D12DescriptorHeap.IID_Guid);
            return (ID3D12DescriptorHeap*)heap;
        }
    }
    private static DirectXCommandBufferState DecodeState(nint commandBufferHandle) =>
        ((DirectXCommandBufferState)GCHandle.FromIntPtr(value: commandBufferHandle).Target!);
    private static D3D12_RESOURCE_STATES ToResourceState(GpuImageLayout layout) =>
        // General and Undefined both resolve to the compute read/write state (the kernel's working layout).
        (DirectXGpuFormats.TryToResourceState(layout: layout, resourceState: out var resourceState)
            ? resourceState
            : D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
}
