using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX.Presentation;

/// <summary>
/// Default <see cref="IDirectXCommandListRecorder"/> for Direct3D 12: transitions the back buffer to
/// render-target state, sets up the RTV, viewport, scissor, and primitive topology, replays each
/// <see cref="DirectXDrawCommand"/> (binding the pipeline, the view and sampler heaps, and the group's view and sampler
/// tables as indicated by non-zero fields), then transitions back to present state.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXCommandListRecorder : IDirectXCommandListRecorder {
    /// <inheritdoc/>
    public void RecordBackBuffer(
        nint commandListHandle,
        nint backBufferHandle,
        nint rtvCpuHandle,
        uint viewportWidth,
        uint viewportHeight,
        IReadOnlyList<DirectXDrawCommand> drawCommands
    ) {
        var commandList = ((ID3D12GraphicsCommandList*)commandListHandle);
        var backBuffer = ((ID3D12Resource*)backBufferHandle);

        var toRenderTarget = DirectXBarriers.Transition(
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT,
            resource: backBuffer
        );

        commandList->ResourceBarrier(
            NumBarriers: 1,
            pBarriers: &toRenderTarget
        );

        var rtv = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = ((nuint)rtvCpuHandle), };

        commandList->OMSetRenderTargets(
            NumRenderTargetDescriptors: 1,
            RTsSingleHandleToDescriptorRange: false,
            pDepthStencilDescriptor: null,
            pRenderTargetDescriptors: &rtv
        );

        var viewport = new D3D12_VIEWPORT {
            Height = viewportHeight,
            MaxDepth = 1f,
            MinDepth = 0f,
            TopLeftX = 0f,
            TopLeftY = 0f,
            Width = viewportWidth,
        };
        var scissor = new Windows.Win32.Foundation.RECT {
            bottom = ((int)viewportHeight),
            left = 0,
            right = ((int)viewportWidth),
            top = 0,
        };

        commandList->RSSetViewports(
            NumViewports: 1,
            pViewports: &viewport
        );
        commandList->RSSetScissorRects(
            NumRects: 1,
            pRects: &scissor
        );
        commandList->IASetPrimitiveTopology(PrimitiveTopology: D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

        // A command binds at most one view heap and one sampler heap.
        var heaps = stackalloc ID3D12DescriptorHeap*[2];
        nint currentPso = 0;
        nint currentRootSig = 0;

        foreach (var command in drawCommands) {
            // A zero handle on a field means "leave the currently bound state unchanged" (see the doc). The pipeline
            // layout's group supplies the view and sampler tables' root parameter indices, so the tables require a
            // layout; the heaps bind independently of it, together in one call as Direct3D 12 requires.
            DirectXGroupLayout? group = null;

            if (command.PipelineLayoutHandle != 0) {
                var layout = ((DirectXPipelineLayout)GCHandle.FromIntPtr(value: command.PipelineLayoutHandle).Target!);

                if (
                    (layout.PsoHandle != currentPso) ||
                    (layout.RootSignatureHandle != currentRootSig)
                ) {
                    commandList->SetGraphicsRootSignature(pRootSignature: ((ID3D12RootSignature*)layout.RootSignatureHandle));
                    commandList->SetPipelineState(pPipelineState: ((ID3D12PipelineState*)layout.PsoHandle));
                    currentPso = layout.PsoHandle;
                    currentRootSig = layout.RootSignatureHandle;
                }
                if (
                    (command.Group < layout.GroupHandles.Length) &&
                    (layout.GroupHandles[command.Group] != 0)
                ) {
                    group = ((DirectXGroupLayout)GCHandle.FromIntPtr(value: layout.GroupHandles[command.Group]).Target!);
                }
            }

            var heapCount = 0U;

            if (command.ViewHeapHandle != 0) {
                heaps[heapCount++] = ((ID3D12DescriptorHeap*)command.ViewHeapHandle);
            }
            if (command.SamplerHeapHandle != 0) {
                heaps[heapCount++] = ((ID3D12DescriptorHeap*)command.SamplerHeapHandle);
            }
            if (heapCount != 0) {
                commandList->SetDescriptorHeaps(
                    NumDescriptorHeaps: heapCount,
                    ppDescriptorHeaps: heaps
                );
            }
            if (
                (command.ViewTableGpuHandle != 0) &&
                (group is { ViewTableIndex: >= 0 })
            ) {
                commandList->SetGraphicsRootDescriptorTable(
                    BaseDescriptor: new D3D12_GPU_DESCRIPTOR_HANDLE { ptr = command.ViewTableGpuHandle, },
                    RootParameterIndex: ((uint)group.ViewTableIndex)
                );
            }
            if (
                (command.SamplerTableGpuHandle != 0) &&
                (group is { SamplerTableIndex: >= 0 })
            ) {
                commandList->SetGraphicsRootDescriptorTable(
                    BaseDescriptor: new D3D12_GPU_DESCRIPTOR_HANDLE { ptr = command.SamplerTableGpuHandle, },
                    RootParameterIndex: ((uint)group.SamplerTableIndex)
                );
            }

            var p = command.DrawParameters;

            commandList->DrawInstanced(
                VertexCountPerInstance: p.VertexCount,
                InstanceCount: p.InstanceCount,
                StartVertexLocation: p.StartVertexLocation,
                StartInstanceLocation: p.StartInstanceLocation
            );
        }

        var toPresent = DirectXBarriers.Transition(
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            resource: backBuffer
        );

        commandList->ResourceBarrier(
            NumBarriers: 1,
            pBarriers: &toPresent
        );
    }
}
