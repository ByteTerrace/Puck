using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuCommandRecorder"/> for Direct3D 12 by recording into a
/// <c>ID3D12GraphicsCommandList</c> extracted from a <see cref="DirectXCommandBufferState"/> GCHandle token.
/// <para>
/// Handle semantics (all are GCHandle tokens):
/// <list type="bullet">
/// <item><c>commandBufferHandle</c> → <see cref="DirectXCommandBufferState"/></item>
/// <item><c>pipelineHandle</c> → <see cref="DirectXPipelineLayout"/></item>
/// <item><c>pipelineLayoutHandle</c> → <see cref="DirectXPipelineLayout"/></item>
/// <item><c>vertexBufferHandle</c> → <see cref="DirectXVertexBufferView"/></item>
/// <item><c>descriptorSetHandle</c> → <see cref="DirectXDescriptorSet"/></item>
/// <item><c>renderPassHandle</c> → raw <c>ID3D12Resource*</c> (the render-target texture)</item>
/// <item><c>framebufferHandle</c> → <c>D3D12_CPU_DESCRIPTOR_HANDLE.ptr</c> cast to nint</item>
/// </list>
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuCommandRecorder : IGpuCommandRecorder {
    /// <inheritdoc/>
    public void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var allocator = ((ID3D12CommandAllocator*)state.Allocator);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);

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
    public void BeginRenderPass(
        nint deviceHandle,
        nint commandBufferHandle,
        nint renderPassHandle,
        nint framebufferHandle,
        uint width,
        uint height
    ) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var renderTarget = ((ID3D12Resource*)renderPassHandle);

        if (D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET != state.RenderTargetState) {
            var barrier = CreateTransition(
                after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
                before: state.RenderTargetState,
                resource: renderTarget
            );

            commandList->ResourceBarrier(NumBarriers: 1, pBarriers: &barrier);
            state.RenderTargetState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET;
        }

        var rtvHandle = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = ((nuint)framebufferHandle) };
        var clearColor = stackalloc float[4] { 0f, 0f, 0f, 1f };

        // First-class render pass (the Vulkan render-pass peer): scope the draws in a real BeginRenderPass /
        // EndRenderPass with an explicit store op, instead of the legacy OMSetRenderTargets emulation. The clear runs
        // through ClearRenderTargetView BEFORE the pass and the pass PRESERVEs it (a render-pass CLEAR load op would
        // need the render-target format, and ID3D12Resource::GetDesc has a struct-return ABI hazard) — and a clear
        // inside a render pass is disallowed anyway. Requires ID3D12GraphicsCommandList4 (Windows 10 1809+); older
        // runtimes use the OMSetRenderTargets emulation. The RENDER_TARGET state transition stays a barrier either way
        // (Direct3D 12 render passes do not transition resource state).
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) {
            commandList->ClearRenderTargetView(ColorRGBA: clearColor, NumRects: 0, RenderTargetView: rtvHandle, pRects: null);

            var renderTargetDesc = new D3D12_RENDER_PASS_RENDER_TARGET_DESC {
                BeginningAccess = new D3D12_RENDER_PASS_BEGINNING_ACCESS {
                    Type = D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE.D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE_PRESERVE,
                },
                EndingAccess = new D3D12_RENDER_PASS_ENDING_ACCESS {
                    Type = D3D12_RENDER_PASS_ENDING_ACCESS_TYPE.D3D12_RENDER_PASS_ENDING_ACCESS_TYPE_PRESERVE,
                },
                cpuDescriptor = rtvHandle,
            };

            ((ID3D12GraphicsCommandList4*)state.CommandList)->BeginRenderPass(Flags: D3D12_RENDER_PASS_FLAGS.D3D12_RENDER_PASS_FLAG_NONE, NumRenderTargets: 1, pDepthStencil: null, pRenderTargets: &renderTargetDesc);
        } else {
            commandList->OMSetRenderTargets(NumRenderTargetDescriptors: 1, RTsSingleHandleToDescriptorRange: false, pDepthStencilDescriptor: null, pRenderTargetDescriptors: &rtvHandle);
            commandList->ClearRenderTargetView(ColorRGBA: clearColor, NumRects: 0, RenderTargetView: rtvHandle, pRects: null);
        }

        var viewport = new D3D12_VIEWPORT {
            Height = height,
            MaxDepth = 1f,
            MinDepth = 0f,
            TopLeftX = 0f,
            TopLeftY = 0f,
            Width = width,
        };

        commandList->RSSetViewports(NumViewports: 1, pViewports: &viewport);

        state.CurrentRenderTargetHandle = renderPassHandle;
    }
    /// <inheritdoc/>
    public void EndRenderPass(nint deviceHandle, nint commandBufferHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);

        // Close the render pass (the store op runs) before the read-state barrier — same OS gate as BeginRenderPass.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) {
            ((ID3D12GraphicsCommandList4*)state.CommandList)->EndRenderPass();
        }

        var barrier = CreateTransition(
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            resource: ((ID3D12Resource*)state.CurrentRenderTargetHandle)
        );

        commandList->ResourceBarrier(NumBarriers: 1, pBarriers: &barrier);
        state.RenderTargetState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
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
    public void BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, nint vertexBufferHandle) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var commandList = ((ID3D12GraphicsCommandList*)state.CommandList);
        var view = ((DirectXVertexBufferView)GCHandle.FromIntPtr(value: vertexBufferHandle).Target!);
        var vbv = new D3D12_VERTEX_BUFFER_VIEW {
            BufferLocation = view.BufferLocation,
            SizeInBytes = view.SizeBytes,
            StrideInBytes = view.StrideBytes,
        };

        commandList->IASetVertexBuffers(NumViews: 1, StartSlot: 0, pViews: &vbv);
    }
    /// <inheritdoc/>
    public void BindDescriptorSet(
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
            commandList->SetGraphicsRootDescriptorTable(
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
            commandList->SetGraphicsRoot32BitConstants(
                RootParameterIndex: ((uint)layout.RootConstantsParamIndex),
                Num32BitValuesToSet: ((uint)(data.Length / 4)),
                pSrcData: pData,
                DestOffsetIn32BitValues: (offset / 4)
            );
        }
    }
    /// <inheritdoc/>
    public void SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);
        var scissor = new RECT {
            bottom = (y + ((int)height)),
            left = x,
            right = (x + ((int)width)),
            top = y,
        };

        ((ID3D12GraphicsCommandList*)state.CommandList)->RSSetScissorRects(NumRects: 1, pRects: &scissor);
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
        ((DirectXCommandBufferState)GCHandle.FromIntPtr(value: commandBufferHandle).Target!);
    private static D3D12_RESOURCE_BARRIER CreateTransition(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
        var barrier = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        barrier.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            StateAfter = after,
            StateBefore = before,
            Subresource = 0xFFFFFFFF,
            pResource = resource,
        };

        return barrier;
    }
}
