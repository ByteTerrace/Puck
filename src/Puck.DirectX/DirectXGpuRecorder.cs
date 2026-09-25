using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Records compute and graphics work on Direct3D 12 direct command lists, each decoded from a
/// <see cref="DirectXCommandBufferState"/> token. Resource transitions use the legacy barrier model throughout, since
/// enhanced and legacy texture barriers cannot be mixed without an explicit COMMON handoff, and shared resource-state
/// tracking keeps compute, draws and reused frame slots consistent. UAV barriers order repeated writes, including zero
/// initialization, and a clear's temporary descriptors belong to the fenced command buffer, retiring when it is reused
/// or disposed.
/// <para>Handles: a pipeline or pipeline layout is a GCHandle token to a <see cref="DirectXPipelineLayout"/>, a
/// descriptor set one to a <see cref="DirectXDescriptorSet"/>, and a buffer or image the raw <c>ID3D12Resource*</c>. A
/// render pass begins from a <see cref="DirectXGpuFramebuffer"/>.</para>
/// </summary>
/// <param name="deviceContext">The device context whose device creates a clear's descriptors and whose
/// <see cref="DirectXDeviceContext.DispatchSignature"/> every indirect dispatch uses.</param>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuRecorder(DirectXDeviceContext deviceContext) : IGpuRecorder {
    /// <inheritdoc/>
    public void BeginCommandBuffer(nint commandBufferHandle) {
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
    public void EndCommandBuffer(nint commandBufferHandle) =>
        DirectXRecorderOperations.EndCommandBuffer(commandBufferHandle: commandBufferHandle);
    /// <inheritdoc/>
    public void BeginDebugGroup(nint commandBufferHandle, string label) =>
        DirectXDebugLabel.Begin(
            commandList: ((ID3D12GraphicsCommandList*)DecodeState(commandBufferHandle: commandBufferHandle).CommandList),
            label: label
        );
    /// <inheritdoc/>
    public void EndDebugGroup(nint commandBufferHandle) =>
        DirectXDebugLabel.End(commandList: ((ID3D12GraphicsCommandList*)DecodeState(commandBufferHandle: commandBufferHandle).CommandList));
    /// <inheritdoc/>
    public void BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: pipelineHandle).Target!);

        if (bindPoint == GpuBindPoint.Compute) {
            commandList->SetComputeRootSignature(pRootSignature: ((ID3D12RootSignature*)layout.RootSignatureHandle));
            commandList->SetPipelineState(pPipelineState: ((ID3D12PipelineState*)layout.PsoHandle));

            return;
        }

        commandList->SetGraphicsRootSignature(pRootSignature: ((ID3D12RootSignature*)layout.RootSignatureHandle));
        commandList->SetPipelineState(pPipelineState: ((ID3D12PipelineState*)layout.PsoHandle));
        commandList->IASetPrimitiveTopology(PrimitiveTopology: D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    }
    /// <inheritdoc/>
    public void BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, nint descriptorSetHandle) =>
        DirectXRecorderOperations.BindDescriptorSet(
            commandBufferHandle: commandBufferHandle,
            descriptorSetHandle: descriptorSetHandle,
            isCompute: (bindPoint == GpuBindPoint.Compute),
            pipelineLayoutHandle: pipelineLayoutHandle
        );
    /// <inheritdoc/>
    public void PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) =>
        DirectXRecorderOperations.PushConstants(
            commandBufferHandle: commandBufferHandle,
            data: data,
            isCompute: (bindPoint == GpuBindPoint.Compute),
            offset: offset,
            pipelineLayoutHandle: pipelineLayoutHandle,
            stageFlags: stageFlags
        );
    /// <inheritdoc/>
    public void Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        ((ID3D12GraphicsCommandList*)state.CommandList)->Dispatch(
            ThreadGroupCountX: groupCountX,
            ThreadGroupCountY: groupCountY,
            ThreadGroupCountZ: groupCountZ
        );
    }
    /// <inheritdoc/>
    public void DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) {
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
    public void ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) {
        ArgumentOutOfRangeException.ThrowIfZero(commandBufferHandle);
        ArgumentOutOfRangeException.ThrowIfZero(imageHandle);
        var descriptors = DirectXClearImageDescriptors.Create(
            deviceHandle: deviceContext.DeviceHandle,
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
    public void ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) {
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
            deviceHandle: deviceContext.DeviceHandle,
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
    /// <inheritdoc/>
    /// <remarks>Each attachment is transitioned from the state it is tracked in to its attachment state, since
    /// Direct3D 12 render passes do not transition resource state. An attachment that clears is cleared by
    /// <c>ClearRenderTargetView</c> or <c>ClearDepthStencilView</c> before the pass, which then preserves it: a clear
    /// inside a render pass is disallowed. Requires <c>ID3D12GraphicsCommandList4</c> (Windows 10 1809+) for a first-class
    /// render pass with explicit ending access; older runtimes bind the views with <c>OMSetRenderTargets</c>. The
    /// viewport and scissor cover the area; a clear covers the whole attachment.</remarks>
    public void BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area = null) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var target = ((DirectXGpuFramebuffer)framebuffer);
        var drawn = GpuFramebuffers.ResolveArea(
            area: area,
            framebuffer: framebuffer
        );
        var description = target.Pass.Description;
        var colorCount = description.Colors.Count;
        var renderTargets = stackalloc D3D12_RENDER_PASS_RENDER_TARGET_DESC[Math.Max(
            val1: 1,
            val2: colorCount
        )];
        var views = stackalloc D3D12_CPU_DESCRIPTOR_HANDLE[Math.Max(
            val1: 1,
            val2: colorCount
        )];
        var clearColor = stackalloc float[4] { 0f, 0f, 0f, 1f };

        for (var index = 0; (index < colorCount); index++) {
            var color = description.Colors[index];
            var view = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = target.ColorViews[index] };

            TransitionTo(
                after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
                commandList: commandList,
                resource: target.ColorResources[index]
            );

            if (color.Load == GpuAttachmentLoad.Clear) {
                commandList->ClearRenderTargetView(
                    ColorRGBA: clearColor,
                    NumRects: 0,
                    RenderTargetView: view,
                    pRects: null
                );
            }

            views[index] = view;
            renderTargets[index] = new D3D12_RENDER_PASS_RENDER_TARGET_DESC {
                BeginningAccess = new D3D12_RENDER_PASS_BEGINNING_ACCESS { Type = BeginningOf(load: color.Load), },
                EndingAccess = new D3D12_RENDER_PASS_ENDING_ACCESS { Type = EndingOf(store: color.Store), },
                cpuDescriptor = view,
            };
        }

        var depthView = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = target.DepthView };
        var depthStencil = default(D3D12_RENDER_PASS_DEPTH_STENCIL_DESC);

        if (description.Depth is { } depth) {
            TransitionTo(
                after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE,
                commandList: commandList,
                resource: target.DepthResource
            );

            if (depth.Load == GpuAttachmentLoad.Clear) {
                commandList->ClearDepthStencilView(
                    ClearFlags: D3D12_CLEAR_FLAGS.D3D12_CLEAR_FLAG_DEPTH,
                    Depth: depth.ClearDepth,
                    DepthStencilView: depthView,
                    NumRects: 0,
                    Stencil: 0,
                    pRects: null
                );
            }

            depthStencil = new D3D12_RENDER_PASS_DEPTH_STENCIL_DESC {
                DepthBeginningAccess = new D3D12_RENDER_PASS_BEGINNING_ACCESS { Type = BeginningOf(load: depth.Load), },
                DepthEndingAccess = new D3D12_RENDER_PASS_ENDING_ACCESS { Type = EndingOf(store: depth.Store), },
                StencilBeginningAccess = new D3D12_RENDER_PASS_BEGINNING_ACCESS { Type = D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE.D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE_NO_ACCESS, },
                StencilEndingAccess = new D3D12_RENDER_PASS_ENDING_ACCESS { Type = D3D12_RENDER_PASS_ENDING_ACCESS_TYPE.D3D12_RENDER_PASS_ENDING_ACCESS_TYPE_NO_ACCESS, },
                cpuDescriptor = depthView,
            };
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(
            10,
            0,
            17763
        )) {
            ((ID3D12GraphicsCommandList4*)state.CommandList)->BeginRenderPass(
                Flags: D3D12_RENDER_PASS_FLAGS.D3D12_RENDER_PASS_FLAG_NONE,
                NumRenderTargets: ((uint)colorCount),
                pDepthStencil: ((description.Depth is null)
                    ? null
                    : &depthStencil),
                pRenderTargets: ((colorCount == 0)
                    ? null
                    : renderTargets)
            );
        } else {
            commandList->OMSetRenderTargets(
                NumRenderTargetDescriptors: ((uint)colorCount),
                RTsSingleHandleToDescriptorRange: false,
                pDepthStencilDescriptor: ((description.Depth is null)
                    ? null
                    : &depthView),
                pRenderTargetDescriptors: ((colorCount == 0)
                    ? null
                    : views)
            );
        }

        var viewport = new D3D12_VIEWPORT {
            Height = drawn.Height,
            MaxDepth = 1f,
            MinDepth = 0f,
            TopLeftX = drawn.X,
            TopLeftY = drawn.Y,
            Width = drawn.Width,
        };

        commandList->RSSetViewports(
            NumViewports: 1,
            pViewports: &viewport
        );
        SetScissor(
            commandBufferHandle: commandBufferHandle,
            rect: drawn
        );
        state.CurrentFramebuffer = target;
    }
    /// <inheritdoc/>
    /// <remarks>Closes the render pass, so its ending accesses run, then transitions each color attachment the pass
    /// declares shader-readable into the shader-read state; every other attachment stays in its attachment state.</remarks>
    public void EndRenderPass(nint commandBufferHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var target = (state.CurrentFramebuffer ?? throw new InvalidOperationException(message: "No render pass is being recorded."));

        if (OperatingSystem.IsWindowsVersionAtLeast(
            10,
            0,
            17763
        )) {
            ((ID3D12GraphicsCommandList4*)state.CommandList)->EndRenderPass();
        }

        for (var index = 0; (index < target.ColorResources.Count); index++) {
            if (target.Pass.Description.Colors[index].FinalLayout == GpuImageLayout.ShaderReadOnly) {
                TransitionTo(
                    after: DirectXResourceStates.ShaderRead,
                    commandList: commandList,
                    resource: target.ColorResources[index]
                );
            }
        }

        state.CurrentFramebuffer = null;
    }
    /// <inheritdoc/>
    /// <remarks>The view is built from the buffer resource's GPU virtual address, the size and the stride.</remarks>
    public void BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var vbv = new D3D12_VERTEX_BUFFER_VIEW {
            BufferLocation = ((ID3D12Resource*)bufferHandle)->GetGPUVirtualAddress(),
            SizeInBytes = checked((uint)sizeBytes),
            StrideInBytes = strideBytes,
        };

        commandList->IASetVertexBuffers(
            NumViews: 1,
            StartSlot: 0,
            pViews: &vbv
        );
    }
    /// <inheritdoc/>
    /// <remarks>The view is built from the buffer resource's GPU virtual address plus the offset, the size and the
    /// index format.</remarks>
    public void BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var ibv = new D3D12_INDEX_BUFFER_VIEW {
            BufferLocation = (((ID3D12Resource*)bufferHandle)->GetGPUVirtualAddress() + offsetBytes),
            Format = format switch {
                GpuIndexFormat.UInt16 => DXGI_FORMAT.DXGI_FORMAT_R16_UINT,
                GpuIndexFormat.UInt32 => DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
                _ => throw new ArgumentOutOfRangeException(
                    actualValue: format,
                    message: "The index format is not defined.",
                    paramName: nameof(format)
                ),
            },
            SizeInBytes = checked((uint)sizeBytes),
        };

        commandList->IASetIndexBuffer(pView: &ibv);
    }
    /// <inheritdoc/>
    public void DrawIndexed(nint commandBufferHandle, uint indexCount) {
        ArgumentOutOfRangeException.ThrowIfZero(value: indexCount);

        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        ((ID3D12GraphicsCommandList*)state.CommandList)->DrawIndexedInstanced(
            BaseVertexLocation: 0,
            IndexCountPerInstance: indexCount,
            InstanceCount: 1,
            StartIndexLocation: 0,
            StartInstanceLocation: 0
        );
    }
    /// <inheritdoc/>
    public void SetScissor(nint commandBufferHandle, GpuPixelRect rect) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var scissor = new RECT {
            bottom = (rect.Y + ((int)rect.Height)),
            left = rect.X,
            right = (rect.X + ((int)rect.Width)),
            top = rect.Y,
        };

        ((ID3D12GraphicsCommandList*)state.CommandList)->RSSetScissorRects(
            NumRects: 1,
            pRects: &scissor
        );
    }
    /// <inheritdoc/>
    public void Draw(
        nint commandBufferHandle,
        in GpuDrawParameters parameters
    ) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        ((ID3D12GraphicsCommandList*)state.CommandList)->DrawInstanced(
            VertexCountPerInstance: parameters.VertexCount,
            InstanceCount: parameters.InstanceCount,
            StartVertexLocation: parameters.FirstVertex,
            StartInstanceLocation: parameters.FirstInstance
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
    private static D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE BeginningOf(GpuAttachmentLoad load) => (load switch {
        GpuAttachmentLoad.Discard => D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE.D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE_DISCARD,
        _ => D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE.D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE_PRESERVE,
    });
    private static D3D12_RENDER_PASS_ENDING_ACCESS_TYPE EndingOf(GpuAttachmentStore store) => ((store == GpuAttachmentStore.Store)
        ? D3D12_RENDER_PASS_ENDING_ACCESS_TYPE.D3D12_RENDER_PASS_ENDING_ACCESS_TYPE_PRESERVE
        : D3D12_RENDER_PASS_ENDING_ACCESS_TYPE.D3D12_RENDER_PASS_ENDING_ACCESS_TYPE_DISCARD
    );
    // Records the transition of an attachment from the state it is tracked in to the one its use needs, and tracks the
    // new state; nothing is recorded when it is already there.
    private static void TransitionTo(ID3D12GraphicsCommandList* commandList, nint resource, D3D12_RESOURCE_STATES after) {
        var before = DirectXResourceStates.Get(
            fallback: after,
            resource: resource
        );

        if (before == after) {
            return;
        }

        var barrier = DirectXBarriers.Transition(
            after: after,
            before: before,
            resource: ((ID3D12Resource*)resource)
        );

        commandList->ResourceBarrier(
            NumBarriers: 1,
            pBarriers: &barrier
        );
        DirectXResourceStates.Set(
            resource: resource,
            state: after
        );
    }
}
