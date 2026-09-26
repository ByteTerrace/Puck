using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

/// <summary>
/// Records compute and graphics work on Direct3D 12 direct command lists, each decoded from a
/// <see cref="DirectXCommandBufferState"/> token. Resource transitions use the legacy barrier model throughout, since
/// enhanced and legacy texture barriers cannot be mixed without an explicit COMMON handoff, and shared resource-state
/// tracking keeps compute, draws and reused frame slots consistent. UAV barriers order repeated writes, including zero
/// initialization. Every recording binds the device's two shader-visible heaps once, when it begins
/// (<see cref="DirectXShaderVisibleHeaps.Bind"/>), so a descriptor set binds only its table; a clear's descriptors are one
/// of the device's clear slots, which the fenced command buffer holds until it is reused or disposed.
/// <para>Handles: a pipeline or pipeline layout is a GCHandle token to a <see cref="DirectXPipelineLayout"/>, a
/// descriptor set one to a <see cref="DirectXDescriptorSet"/>, and a buffer or image the raw <c>ID3D12Resource*</c>. A
/// render pass begins from a <see cref="DirectXGpuFramebuffer"/>.</para>
/// </summary>
/// <param name="deviceContext">The device context whose <see cref="DirectXDeviceContext.DescriptorHeaps"/> every
/// recording binds and a clear's descriptors come from, and whose
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
        DirectXCommandCalls.Reset(
            allocator: allocator,
            calls: DirectXDeviceCommandCalls.Of(deviceContext: deviceContext),
            commandList: commandList
        );
        deviceContext.DescriptorHeaps.Bind(commandList: commandList);
    }
    /// <inheritdoc/>
    public void EndCommandBuffer(nint commandBufferHandle) =>
        DirectXCommandCalls.Close(
            calls: DirectXDeviceCommandCalls.Of(deviceContext: deviceContext),
            commandList: ((ID3D12GraphicsCommandList*)DecodeState(commandBufferHandle: commandBufferHandle).CommandList)
        );
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
    /// <remarks>A group's set sets the group's view table and then its sampler table, at the root parameter indices the
    /// bound pipeline's plan gave that group (<see cref="DirectXGroupLayout"/>, from its
    /// <see cref="DirectXPipelineLayout.GroupHandles"/>); a set of any other pipeline sets its one descriptor
    /// table.</remarks>
    public void BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, uint group, nint descriptorSetHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: pipelineLayoutHandle).Target!);
        var set = ((DirectXDescriptorSet)GCHandle.FromIntPtr(value: descriptorSetHandle).Target!);
        var own = (set.Group?.Ordinal ?? 0U);

        if (own != group) {
            throw new InvalidOperationException(message: $"A set of group {own} is bound at group {group}; a set binds only at its own group.");
        }

        if (set.Group is null) {
            if (0 > layout.DescriptorTableParamIndex) {
                return;
            }

            SetTable(
                bindPoint: bindPoint,
                commandList: commandList,
                gpuBase: set.GpuBase,
                rootParameterIndex: layout.DescriptorTableParamIndex
            );

            return;
        }

        if (
            (group >= ((uint)layout.GroupHandles.Length)) ||
            (0 == layout.GroupHandles[group])
        ) {
            throw new InvalidOperationException(message: $"A set of group {group} is bound to a pipeline that has no group {group}.");
        }

        var planned = ((DirectXGroupLayout)GCHandle.FromIntPtr(value: layout.GroupHandles[group]).Target!);

        if (0 <= planned.ViewTableIndex) {
            SetTable(
                bindPoint: bindPoint,
                commandList: commandList,
                gpuBase: set.GpuBase,
                rootParameterIndex: planned.ViewTableIndex
            );
        }

        if (0 <= planned.SamplerTableIndex) {
            SetTable(
                bindPoint: bindPoint,
                commandList: commandList,
                gpuBase: set.SamplerGpuBase,
                rootParameterIndex: planned.SamplerTableIndex
            );
        }
    }
    /// <inheritdoc/>
    public void PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        GpuPushConstantBinding.ValidateRange(
            dataLength: data.Length,
            offset: offset,
            stageFlags: stageFlags
        );

        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: pipelineLayoutHandle).Target!);

        if (0 > layout.RootConstantsParamIndex) {
            return;
        }

        fixed (byte* pData = data) {
            if (bindPoint == GpuBindPoint.Compute) {
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
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var descriptors = ClearDescriptor(
            state: state,
            view: new D3D12_UNORDERED_ACCESS_VIEW_DESC {
                Anonymous = new D3D12_UNORDERED_ACCESS_VIEW_DESC._Anonymous_e__Union {
                    Texture2D = new D3D12_TEX2D_UAV { MipSlice = 0, PlaneSlice = 0, },
                },
                Format = DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
                ViewDimension = D3D12_UAV_DIMENSION.D3D12_UAV_DIMENSION_TEXTURE2D,
            },
            resource: imageHandle
        );
        var clearColor = stackalloc float[4] { 0f, 0f, 0f, 0f };

        commandList->ClearUnorderedAccessViewFloat(
            descriptors.GpuHandle,
            descriptors.ClearCpuHandle,
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

        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var descriptors = ClearDescriptor(
            state: state,
            view: new D3D12_UNORDERED_ACCESS_VIEW_DESC {
                Anonymous = new D3D12_UNORDERED_ACCESS_VIEW_DESC._Anonymous_e__Union {
                    Buffer = new D3D12_BUFFER_UAV {
                        CounterOffsetInBytes = 0,
                        FirstElement = 0,
                        Flags = D3D12_BUFFER_UAV_FLAGS.D3D12_BUFFER_UAV_FLAG_RAW,
                        NumElements = checked((uint)(sizeBytes / 4)),
                        StructureByteStride = 0,
                    },
                },
                Format = DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS,
                ViewDimension = D3D12_UAV_DIMENSION.D3D12_UAV_DIMENSION_BUFFER,
            },
            resource: bufferHandle
        );
        var clearValues = stackalloc uint[4] { 0U, 0U, 0U, 0U };

        commandList->ClearUnorderedAccessViewUint(
            descriptors.GpuHandle,
            descriptors.ClearCpuHandle,
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
        GpuAccess sourceAccessMask,
        GpuAccess destinationAccessMask,
        GpuStage sourceStageMask,
        GpuStage destinationStageMask
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
        GpuAccess sourceAccessMask,
        GpuAccess destinationAccessMask,
        GpuStage sourceStageMask,
        GpuStage destinationStageMask
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
        GpuAccess sourceAccessMask,
        GpuAccess destinationAccessMask,
        GpuStage sourceStageMask,
        GpuStage destinationStageMask
    ) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        // A buffer has no subresources, so a transition covers the whole resource. Its first transition in a recording
        // starts from the state the declared prior access promoted it to (DirectXBufferStates); an upload-heap
        // buffer's registered GENERIC_READ replaces that for the buffer's whole life.
        var barrier = state.BufferStates.Plan(
            after: DirectXBufferStates.RequiredState(
                access: destinationAccessMask,
                stages: destinationStageMask
            ),
            bufferHandle: bufferHandle,
            firstState: DirectXResourceStates.Get(
                fallback: DirectXBufferStates.RequiredState(
                    access: sourceAccessMask,
                    stages: sourceStageMask
                ),
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
    /// viewport, the scissor and each clear cover the area, as Vulkan's render area bounds its clear loads.</remarks>
    public void BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area = null) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var target = ((DirectXGpuFramebuffer)framebuffer);
        var drawn = GpuFramebuffers.ResolveArea(
            area: area,
            framebuffer: framebuffer
        );
        var cleared = ToRect(rect: drawn);
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
                    NumRects: 1,
                    RenderTargetView: view,
                    pRects: &cleared
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
                    NumRects: 1,
                    Stencil: 0,
                    pRects: &cleared
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
        var scissor = ToRect(rect: rect);

        ((ID3D12GraphicsCommandList*)state.CommandList)->RSSetScissorRects(
            NumRects: 1,
            pRects: &scissor
        );
    }
    /// <summary>Returns the Direct3D 12 rectangle covering exactly a pixel rectangle's pixels: its left and top edges
    /// inclusive, its right and bottom edges exclusive, as a scissor and a clear rectangle both read it.</summary>
    /// <param name="rect">The pixel rectangle.</param>
    /// <returns>The rectangle.</returns>
    /// <exception cref="OverflowException">An edge of <paramref name="rect"/> lies past <see cref="int.MaxValue"/>.</exception>
    public static RECT ToRect(GpuPixelRect rect) => new() {
        bottom = checked((rect.Y + ((int)rect.Height))),
        left = rect.X,
        right = checked((rect.X + ((int)rect.Width))),
        top = rect.Y,
    };
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

    // Takes one of the device's clear slots for a recording, held until the command buffer is reused or disposed, and
    // writes the clear's view into both of its descriptors: the view-heap slot the GPU handle names, and the CPU-only
    // mirror the CPU handle names.
    private DirectXClearDescriptor ClearDescriptor(DirectXCommandBufferState state, D3D12_UNORDERED_ACCESS_VIEW_DESC view, nint resource) {
        var device = ((ID3D12Device*)deviceContext.Device.Handle);
        var descriptors = deviceContext.DescriptorHeaps.AllocateClear();

        state.RetainedResources.Add(item: descriptors);
        device->CreateUnorderedAccessView(
            DestDescriptor: descriptors.ViewCpuHandle,
            pCounterResource: null,
            pDesc: &view,
            pResource: ((ID3D12Resource*)resource)
        );
        device->CreateUnorderedAccessView(
            DestDescriptor: descriptors.ClearCpuHandle,
            pCounterResource: null,
            pDesc: &view,
            pResource: ((ID3D12Resource*)resource)
        );

        return descriptors;
    }
    private static void SetTable(ID3D12GraphicsCommandList* commandList, GpuBindPoint bindPoint, int rootParameterIndex, ulong gpuBase) {
        var handle = new D3D12_GPU_DESCRIPTOR_HANDLE { ptr = gpuBase };

        if (bindPoint == GpuBindPoint.Compute) {
            commandList->SetComputeRootDescriptorTable(
                BaseDescriptor: handle,
                RootParameterIndex: ((uint)rootParameterIndex)
            );
        } else {
            commandList->SetGraphicsRootDescriptorTable(
                BaseDescriptor: handle,
                RootParameterIndex: ((uint)rootParameterIndex)
            );
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
