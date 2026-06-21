using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuComputeRecorder"/> for Direct3D 12 by recording into the
/// <c>ID3D12GraphicsCommandList</c> extracted from a <see cref="DirectXCommandBufferState"/> GCHandle token (a
/// DIRECT list carries both the graphics and compute verbs, so no compute-specific interface is needed).
/// <para>
/// Bind/dispatch map straight across: <c>BindComputePipeline</c> = <c>SetComputeRootSignature</c> +
/// <c>SetPipelineState</c>; <c>BindComputeDescriptorSet</c> = <c>SetDescriptorHeaps</c> +
/// <c>SetComputeRootDescriptorTable</c>; <c>PushConstants</c> = <c>SetComputeRoot32BitConstants</c>;
/// <c>Dispatch</c> = <c>Dispatch</c>.
/// </para>
/// <para>
/// The neutral synchronization primitives map to D3D12 as follows. <c>TransitionImageLayout</c> emits a TRANSITION
/// barrier on the storage-image resource: <see cref="GpuImageLayout.General"/> → <c>UNORDERED_ACCESS</c>,
/// <see cref="GpuImageLayout.ShaderReadOnly"/> → <c>PIXEL_SHADER_RESOURCE</c> (the layout the readback and the
/// compositor sample read require). Because the neutral <c>oldLayout</c> is <see cref="GpuImageLayout.Undefined"/>
/// on the first frame, the actual <c>StateBefore</c> is taken from a per-resource tracked state seeded at the
/// storage image's initial <c>UNORDERED_ACCESS</c>, not from the neutral old layout. <c>MemoryBarrier</c> emits a
/// UAV barrier (a global one with a null resource) — the access/stage scopes are advisory only on D3D12.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuComputeRecorder : IGpuComputeRecorder, IDisposable {
    // The current D3D12 resource state of each storage image, keyed by its ID3D12Resource* — used ONLY by the classic
    // (legacy) barrier fallback. Seeded lazily at the texture's initial UNORDERED_ACCESS state (DirectXGpuStorageImage
    // creates it there) so the first classic transition's StateBefore is correct even though the neutral oldLayout is
    // Undefined. The enhanced-barrier path needs no such tracking — it honors the neutral oldLayout directly (mapping
    // Undefined to D3D12_BARRIER_LAYOUT_UNDEFINED + a discard), exactly like a Vulkan image-layout transition.
    private readonly ConcurrentDictionary<nint, D3D12_RESOURCE_STATES> m_imageStates = new();
    // Whether each device supports Enhanced Barriers (D3D12_FEATURE_D3D12_OPTIONS12), cached per ID3D12Device*. When
    // supported, transitions/memory barriers carry real sync + access scopes and first-class layouts (the Vulkan-barrier
    // peer); otherwise the legacy resource-state barriers are used.
    private readonly ConcurrentDictionary<nint, bool> m_enhancedBarriers = new();
    // The DISPATCH command signature for ExecuteIndirect, cached per ID3D12Device* (one signature serves every
    // indirect dispatch on a device). Released on Dispose; the service provider disposes this recorder singleton.
    private readonly ConcurrentDictionary<nint, nint> m_dispatchSignatures = new();

    /// <inheritdoc/>
    public void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        var state = DecodeState(commandBufferHandle);
        var allocator = (ID3D12CommandAllocator*)state.Allocator;
        var commandList = (ID3D12GraphicsCommandList*)state.CommandList;

        allocator->Reset();
        commandList->Reset(pAllocator: allocator, pInitialState: null);
    }

    /// <inheritdoc/>
    public void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        var state = DecodeState(commandBufferHandle);

        ((ID3D12GraphicsCommandList*)state.CommandList)->Close();
    }

    /// <inheritdoc/>
    public void BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) {
        var state = DecodeState(commandBufferHandle);
        var commandList = (ID3D12GraphicsCommandList*)state.CommandList;
        var layout = (DirectXPipelineLayout)GCHandle.FromIntPtr(pipelineHandle).Target!;

        commandList->SetComputeRootSignature((ID3D12RootSignature*)layout.RootSignatureHandle);
        commandList->SetPipelineState((ID3D12PipelineState*)layout.PsoHandle);
    }

    /// <inheritdoc/>
    public void BindComputeDescriptorSet(
        nint deviceHandle,
        nint commandBufferHandle,
        nint pipelineLayoutHandle,
        nint descriptorSetHandle
    ) {
        var state = DecodeState(commandBufferHandle);
        var commandList = (ID3D12GraphicsCommandList*)state.CommandList;
        var layout = (DirectXPipelineLayout)GCHandle.FromIntPtr(pipelineLayoutHandle).Target!;
        var set = (DirectXDescriptorSet)GCHandle.FromIntPtr(descriptorSetHandle).Target!;
        var heap = (ID3D12DescriptorHeap*)set.HeapHandle;

        commandList->SetDescriptorHeaps(1, &heap);

        if (0 <= layout.DescriptorTableParamIndex) {
            commandList->SetComputeRootDescriptorTable(
                (uint)layout.DescriptorTableParamIndex,
                new D3D12_GPU_DESCRIPTOR_HANDLE { ptr = set.GpuBase }
            );
        }
    }

    /// <inheritdoc/>
    public void PushConstants(
        nint deviceHandle,
        nint commandBufferHandle,
        nint pipelineLayoutHandle,
        uint stageFlags,
        uint offset,
        ReadOnlySpan<byte> data
    ) {
        var state = DecodeState(commandBufferHandle);
        var commandList = (ID3D12GraphicsCommandList*)state.CommandList;
        var layout = (DirectXPipelineLayout)GCHandle.FromIntPtr(pipelineLayoutHandle).Target!;

        if (0 > layout.RootConstantsParamIndex) {
            return;
        }

        fixed (byte* pData = data) {
            commandList->SetComputeRoot32BitConstants(
                RootParameterIndex: (uint)layout.RootConstantsParamIndex,
                Num32BitValuesToSet: (uint)(data.Length / 4),
                pSrcData: pData,
                DestOffsetIn32BitValues: offset / 4
            );
        }
    }

    /// <inheritdoc/>
    public void Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        var state = DecodeState(commandBufferHandle);

        ((ID3D12GraphicsCommandList*)state.CommandList)->Dispatch(
            ThreadGroupCountX: groupCountX,
            ThreadGroupCountY: groupCountY,
            ThreadGroupCountZ: groupCountZ
        );
    }

    /// <inheritdoc/>
    public void DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) {
        var state = DecodeState(commandBufferHandle);
        var signature = (ID3D12CommandSignature*)GetOrCreateDispatchSignature(deviceHandle: deviceHandle);

        // The argument buffer is an upload-heap resource permanently in GENERIC_READ (which already permits
        // INDIRECT_ARGUMENT reads), so no resource-state transition is recorded before the indirect dispatch.
        ((ID3D12GraphicsCommandList*)state.CommandList)->ExecuteIndirect(
            pCommandSignature: signature,
            MaxCommandCount: 1,
            pArgumentBuffer: (ID3D12Resource*)argumentBufferHandle,
            ArgumentBufferOffset: argumentBufferOffset,
            pCountBuffer: null,
            CountBufferOffset: 0
        );
    }

    /// <inheritdoc/>
    public void TransitionImageLayout(
        nint deviceHandle,
        nint commandBufferHandle,
        nint imageHandle,
        uint oldLayout,
        uint newLayout,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    ) {
        var state = DecodeState(commandBufferHandle);

        // Enhanced Barriers (the Vulkan-barrier peer): a texture barrier carrying real sync + access scopes (from the
        // neutral stage/access masks) and first-class layouts. The neutral oldLayout is honored directly — Undefined
        // maps to LAYOUT_UNDEFINED with a discard, so no per-resource state tracking is needed.
        if (UseEnhancedBarriers(deviceHandle: deviceHandle)) {
            var textureBarrier = new D3D12_TEXTURE_BARRIER {
                AccessAfter = ToTextureAccess(layout: newLayout),
                AccessBefore = ToTextureAccess(layout: oldLayout),
                Flags = ((oldLayout == GpuImageLayout.Undefined) ? D3D12_TEXTURE_BARRIER_FLAGS.D3D12_TEXTURE_BARRIER_FLAG_DISCARD : D3D12_TEXTURE_BARRIER_FLAGS.D3D12_TEXTURE_BARRIER_FLAG_NONE),
                LayoutAfter = ToBarrierLayout(layout: newLayout),
                LayoutBefore = ToBarrierLayout(layout: oldLayout),
                pResource = (ID3D12Resource*)imageHandle,
                Subresources = new D3D12_BARRIER_SUBRESOURCE_RANGE { IndexOrFirstMipLevel = DirectXConstants.AllSubresources, },
                SyncAfter = ToBarrierSync(stageMask: destinationStageMask),
                SyncBefore = ToBarrierSync(stageMask: sourceStageMask),
            };
            var textureGroup = new D3D12_BARRIER_GROUP {
                NumBarriers = 1,
                Type = D3D12_BARRIER_TYPE.D3D12_BARRIER_TYPE_TEXTURE,
            };

            textureGroup.Anonymous.pTextureBarriers = &textureBarrier;
            ((ID3D12GraphicsCommandList7*)state.CommandList)->Barrier(1, &textureGroup);

            return;
        }

        var commandList = (ID3D12GraphicsCommandList*)state.CommandList;
        // Legacy fallback: the neutral oldLayout is Undefined on the first frame, so the true prior state comes from
        // tracking — the texture was created in UNORDERED_ACCESS, which is the seed for an untracked resource.
        var before = m_imageStates.GetOrAdd(imageHandle, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        var after = ToResourceState(layout: newLayout);

        if (before == after) {
            return;
        }

        var barrier = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        barrier.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            pResource = (ID3D12Resource*)imageHandle,
            Subresource = DirectXConstants.AllSubresources,
            StateBefore = before,
            StateAfter = after,
        };

        commandList->ResourceBarrier(1, &barrier);
        m_imageStates[imageHandle] = after;
    }

    /// <inheritdoc/>
    public void MemoryBarrier(
        nint deviceHandle,
        nint commandBufferHandle,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    ) {
        var state = DecodeState(commandBufferHandle);

        // Enhanced Barriers: a global memory barrier carrying real sync + access scopes from the neutral masks (the
        // Vulkan VkMemoryBarrier peer), instead of the scopeless legacy UAV barrier.
        if (UseEnhancedBarriers(deviceHandle: deviceHandle)) {
            var globalBarrier = new D3D12_GLOBAL_BARRIER {
                AccessAfter = ToGlobalAccess(accessMask: destinationAccessMask),
                AccessBefore = ToGlobalAccess(accessMask: sourceAccessMask),
                SyncAfter = ToBarrierSync(stageMask: destinationStageMask),
                SyncBefore = ToBarrierSync(stageMask: sourceStageMask),
            };
            var globalGroup = new D3D12_BARRIER_GROUP {
                NumBarriers = 1,
                Type = D3D12_BARRIER_TYPE.D3D12_BARRIER_TYPE_GLOBAL,
            };

            globalGroup.Anonymous.pGlobalBarriers = &globalBarrier;
            ((ID3D12GraphicsCommandList7*)state.CommandList)->Barrier(1, &globalGroup);

            return;
        }

        var commandList = (ID3D12GraphicsCommandList*)state.CommandList;
        // Legacy fallback: a global UAV barrier (null resource) ordering the prior UAV writes before the next reads.
        // Legacy D3D12 UAV barriers carry no access/stage scope, so the neutral masks are unused here.
        var barrier = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_UAV,
        };

        barrier.Anonymous.UAV = new D3D12_RESOURCE_UAV_BARRIER {
            pResource = (ID3D12Resource*)null,
        };

        commandList->ResourceBarrier(1, &barrier);
    }

    /// <inheritdoc/>
    public void TransitionBuffer(
        nint deviceHandle,
        nint commandBufferHandle,
        nint bufferHandle,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    ) {
        var state = DecodeState(commandBufferHandle);

        // Enhanced Barriers: a per-RESOURCE buffer barrier. ExecuteIndirect requires the argument buffer in the
        // INDIRECT_ARGUMENT access state; a global barrier does not prepare a specific buffer for it, so the GPU-written
        // args (a UAV write) are transitioned to INDIRECT_ARGUMENT here. The barrier covers the whole resource
        // (Offset 0, Size = max): the conservative, always-correct scope. D3D12 does permit a sub-range, but the neutral
        // TransitionBuffer verb deliberately syncs the whole buffer — sub-range scoping would only be an optimization
        // for a large, partially-written buffer, which no current consumer needs.
        if (UseEnhancedBarriers(deviceHandle: deviceHandle)) {
            var bufferBarrier = new D3D12_BUFFER_BARRIER {
                AccessAfter = ToGlobalAccess(accessMask: destinationAccessMask),
                AccessBefore = ToGlobalAccess(accessMask: sourceAccessMask),
                Offset = 0,
                Size = ulong.MaxValue,
                SyncAfter = ToBarrierSync(stageMask: destinationStageMask),
                SyncBefore = ToBarrierSync(stageMask: sourceStageMask),
                pResource = (ID3D12Resource*)bufferHandle,
            };
            var bufferGroup = new D3D12_BARRIER_GROUP {
                NumBarriers = 1,
                Type = D3D12_BARRIER_TYPE.D3D12_BARRIER_TYPE_BUFFER,
            };

            bufferGroup.Anonymous.pBufferBarriers = &bufferBarrier;
            ((ID3D12GraphicsCommandList7*)state.CommandList)->Barrier(1, &bufferGroup);

            return;
        }

        // Legacy fallback: a per-resource TRANSITION barrier between the access states (e.g. UNORDERED_ACCESS ->
        // INDIRECT_ARGUMENT). Buffers have no subresources, so transition the whole resource.
        var commandList = (ID3D12GraphicsCommandList*)state.CommandList;
        var before = ToBufferResourceState(accessMask: sourceAccessMask);
        var after = ToBufferResourceState(accessMask: destinationAccessMask);

        if (before == after) {
            return;
        }

        var transition = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        transition.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            pResource = (ID3D12Resource*)bufferHandle,
            Subresource = DirectXConstants.AllSubresources,
            StateAfter = after,
            StateBefore = before,
        };

        commandList->ResourceBarrier(1, &transition);
    }
    // Access mask -> the legacy buffer resource state (used only when Enhanced Barriers are unavailable).
    private static D3D12_RESOURCE_STATES ToBufferResourceState(uint accessMask) {
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

    // Whether the device supports Enhanced Barriers, cached per device. CsWin32's CheckFeatureSupport is the throwing
    // void overload, so an unsupported query (older device/OS) is treated as "not supported" and falls back to legacy.
    private bool UseEnhancedBarriers(nint deviceHandle) {
        if (m_enhancedBarriers.TryGetValue(key: deviceHandle, value: out var supported)) {
            return supported;
        }

        var device = (ID3D12Device*)deviceHandle;
        D3D12_FEATURE_DATA_D3D12_OPTIONS12 options = default;

        try {
            device->CheckFeatureSupport(
                Feature: D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS12,
                pFeatureSupportData: &options,
                FeatureSupportDataSize: (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS12)
            );
        } catch {
            options.EnhancedBarriersSupported = false;
        }

        var result = (bool)options.EnhancedBarriersSupported;

        m_enhancedBarriers[deviceHandle] = result;

        return result;
    }
    // The cached one-argument DISPATCH command signature for a device (ByteStride = sizeof(D3D12_DISPATCH_ARGUMENTS),
    // a single DISPATCH argument, no root signature — a pure-dispatch signature binds nothing). Same per-device cache
    // shape as UseEnhancedBarriers; a concurrent loser releases its duplicate so exactly one signature is kept.
    private nint GetOrCreateDispatchSignature(nint deviceHandle) {
        if (m_dispatchSignatures.TryGetValue(key: deviceHandle, value: out var existing)) {
            return existing;
        }

        var device = (ID3D12Device*)deviceHandle;
        var argumentDesc = new D3D12_INDIRECT_ARGUMENT_DESC {
            Type = D3D12_INDIRECT_ARGUMENT_TYPE.D3D12_INDIRECT_ARGUMENT_TYPE_DISPATCH,
        };
        var signatureDesc = new D3D12_COMMAND_SIGNATURE_DESC {
            ByteStride = (uint)sizeof(D3D12_DISPATCH_ARGUMENTS),
            NumArgumentDescs = 1,
            pArgumentDescs = &argumentDesc,
        };

        void* signature;
        var signatureIid = ID3D12CommandSignature.IID_Guid;

        device->CreateCommandSignature(
            pDesc: in signatureDesc,
            pRootSignature: null,
            riid: in signatureIid,
            ppvCommandSignature: &signature
        );

        var handle = (nint)signature;

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
    // Stage mask → barrier sync scope. TopOfPipe (no prior work) contributes nothing, so a source-only TopOfPipe maps
    // to SYNC_NONE; the compute and pixel stages map to their shading sync scopes.
    private static D3D12_BARRIER_SYNC ToBarrierSync(uint stageMask) {
        var sync = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE;

        if (0 != (stageMask & GpuComputeStage.ComputeShader)) {
            sync |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING;
        }

        if (0 != (stageMask & GpuComputeStage.FragmentShader)) {
            sync |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_PIXEL_SHADING;
        }

        if (0 != (stageMask & GpuComputeStage.DrawIndirect)) {
            sync |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EXECUTE_INDIRECT;
        }

        return sync;
    }
    // Image layout → the access compatible with it (the access a texture barrier carries must match its layout).
    private static D3D12_BARRIER_ACCESS ToTextureAccess(uint layout) {
        return layout switch {
            GpuImageLayout.General => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS,
            GpuImageLayout.ShaderReadOnly => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE,
            // Undefined has no access; External rests in COMMON (any access compatible with the shared handoff).
            GpuImageLayout.Undefined => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS,
            _ => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON,
        };
    }
    // Image layout → first-class barrier layout (the Vulkan image-layout peer).
    private static D3D12_BARRIER_LAYOUT ToBarrierLayout(uint layout) {
        return layout switch {
            GpuImageLayout.General => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_UNORDERED_ACCESS,
            GpuImageLayout.ShaderReadOnly => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_SHADER_RESOURCE,
            GpuImageLayout.External => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COMMON,
            _ => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_UNDEFINED,
        };
    }
    // Access mask → global-barrier access scope. A shader read may be a UAV read or a sampled read, so it spans both.
    private static D3D12_BARRIER_ACCESS ToGlobalAccess(uint accessMask) {
        if (accessMask == GpuComputeAccess.None) {
            return D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS;
        }

        var access = D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON;

        if (0 != (accessMask & GpuComputeAccess.ShaderWrite)) {
            access |= D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS;
        }

        if (0 != (accessMask & GpuComputeAccess.ShaderRead)) {
            access |= (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE);
        }

        if (0 != (accessMask & GpuComputeAccess.IndirectCommandRead)) {
            access |= D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_INDIRECT_ARGUMENT;
        }

        return access;
    }

    private static DirectXCommandBufferState DecodeState(nint commandBufferHandle) =>
        (DirectXCommandBufferState)GCHandle.FromIntPtr(commandBufferHandle).Target!;
    private static D3D12_RESOURCE_STATES ToResourceState(uint layout) {
        return layout switch {
            GpuImageLayout.ShaderReadOnly => D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
            // The cross-backend handoff state: a shared resource must rest in COMMON for a foreign device to open it.
            GpuImageLayout.External => D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            // General and Undefined both resolve to the compute read/write state (the kernel's working layout).
            _ => D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
        };
    }
}
