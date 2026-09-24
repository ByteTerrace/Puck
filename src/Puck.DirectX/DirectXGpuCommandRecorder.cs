using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuCommandRecorder"/> for Direct3D 12 by recording into a
/// <c>ID3D12GraphicsCommandList</c> extracted from a <see cref="DirectXCommandBufferState"/> GCHandle token.
/// <para>
/// Handle semantics:
/// <list type="bullet">
/// <item><c>commandBufferHandle</c> → a GCHandle token to a <see cref="DirectXCommandBufferState"/></item>
/// <item><c>pipelineHandle</c> → a GCHandle token to a <see cref="DirectXPipelineLayout"/></item>
/// <item><c>pipelineLayoutHandle</c> → a GCHandle token to a <see cref="DirectXPipelineLayout"/></item>
/// <item><c>descriptorSetHandle</c> → a GCHandle token to a <see cref="DirectXDescriptorSet"/></item>
/// <item><c>bufferHandle</c> → the raw <c>ID3D12Resource*</c> of a geometry buffer</item>
/// </list>
/// A render pass begins from a <see cref="DirectXGpuFramebuffer"/>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuCommandRecorder : IGpuCommandRecorder {
    /// <inheritdoc/>
    public void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var allocator = ((ID3D12CommandAllocator*)state.Allocator);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);

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
    /// <remarks>Each attachment is transitioned from the state it is tracked in to its attachment state, since
    /// Direct3D 12 render passes do not transition resource state. An attachment that clears is cleared by
    /// <c>ClearRenderTargetView</c> or <c>ClearDepthStencilView</c> before the pass, which then preserves it: a clear
    /// inside a render pass is disallowed. Requires <c>ID3D12GraphicsCommandList4</c> (Windows 10 1809+) for a first-class
    /// render pass with explicit ending access; older runtimes bind the views with <c>OMSetRenderTargets</c>.</remarks>
    public void BeginRenderPass(nint deviceHandle, nint commandBufferHandle, IGpuFramebuffer framebuffer) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var target = ((DirectXGpuFramebuffer)framebuffer);
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
                    Depth: 1f,
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
            Height = target.Height,
            MaxDepth = 1f,
            MinDepth = 0f,
            TopLeftX = 0f,
            TopLeftY = 0f,
            Width = target.Width,
        };

        commandList->RSSetViewports(
            NumViewports: 1,
            pViewports: &viewport
        );
        state.CurrentFramebuffer = target;
    }
    /// <inheritdoc/>
    /// <remarks>Closes the render pass, so its ending accesses run, then transitions each color attachment the pass
    /// declares shader-readable into the shader-read state; every other attachment stays in its attachment state.</remarks>
    public void EndRenderPass(nint deviceHandle, nint commandBufferHandle) {
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
    public void BindGraphicsPipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: pipelineHandle).Target!);

        commandList->SetGraphicsRootSignature(pRootSignature: ((ID3D12RootSignature*)layout.RootSignatureHandle));
        commandList->SetPipelineState(pPipelineState: ((ID3D12PipelineState*)layout.PsoHandle));
        commandList->IASetPrimitiveTopology(PrimitiveTopology: D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    }
    /// <inheritdoc/>
    /// <remarks>The view is built from the buffer resource's GPU virtual address, the size and the stride.</remarks>
    public void BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) {
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
    public void BindIndexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) {
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
    public void DrawIndexed(nint deviceHandle, nint commandBufferHandle, uint indexCount) {
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
    public void BindDescriptorSet(
        nint deviceHandle,
        nint commandBufferHandle,
        nint pipelineLayoutHandle,
        nint descriptorSetHandle
    ) => DirectXRecorderOperations.BindDescriptorSet(
        commandBufferHandle: commandBufferHandle,
        descriptorSetHandle: descriptorSetHandle,
        isCompute: false,
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
        isCompute: false,
        offset: offset,
        pipelineLayoutHandle: pipelineLayoutHandle,
        stageFlags: stageFlags
    );
    /// <inheritdoc/>
    public void SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var scissor = new RECT {
            bottom = (y + ((int)height)),
            left = x,
            right = (x + ((int)width)),
            top = y,
        };

        ((ID3D12GraphicsCommandList*)state.CommandList)->RSSetScissorRects(
            NumRects: 1,
            pRects: &scissor
        );
    }
    /// <inheritdoc/>
    public void Draw(
        nint deviceHandle,
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

    private static DirectXCommandBufferState DecodeState(nint commandBufferHandle) =>
        DirectXCommandBufferState.Decode(commandBufferHandle: commandBufferHandle);
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
