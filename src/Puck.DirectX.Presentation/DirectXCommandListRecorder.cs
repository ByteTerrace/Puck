using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX.Presentation;

/// <summary>
/// Default <see cref="IDirectXCommandListRecorder"/> for Direct3D 12: transitions the back buffer to
/// render-target state, sets up the RTV, viewport, scissor, and primitive topology, replays each
/// <see cref="DirectXDrawCommand"/> (binding pipeline, descriptor heap, root table, vertex buffer, and root
/// constants as indicated by non-zero fields), then transitions back to present state.
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
        var commandList = (ID3D12GraphicsCommandList*)commandListHandle;
        var backBuffer = (ID3D12Resource*)backBufferHandle;

        var toRenderTarget = CreateTransition(
            resource: backBuffer,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT,
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET
        );

        commandList->ResourceBarrier(1, &toRenderTarget);

        var rtv = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = (nuint)rtvCpuHandle, };

        commandList->OMSetRenderTargets(1, &rtv, false, null);

        var viewport = new D3D12_VIEWPORT {
            Height = viewportHeight,
            MaxDepth = 1f,
            MinDepth = 0f,
            TopLeftX = 0f,
            TopLeftY = 0f,
            Width = viewportWidth,
        };
        var scissor = new Windows.Win32.Foundation.RECT {
            bottom = (int)viewportHeight,
            left = 0,
            right = (int)viewportWidth,
            top = 0,
        };

        commandList->RSSetViewports(1, &viewport);
        commandList->RSSetScissorRects(1, &scissor);
        commandList->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

        nint currentPso = 0;
        nint currentRootSig = 0;

        foreach (var command in drawCommands) {
            // A zero handle on a field means "leave the currently bound state unchanged" (see the doc). The
            // pipeline layout supplies the descriptor-table and root-constant parameter indices, so those two
            // bindings require a layout; the descriptor heap and vertex buffer bind independently of it.
            DirectXPipelineLayout? layout = null;

            if (command.PipelineLayoutHandle != 0) {
                layout = (DirectXPipelineLayout)GCHandle.FromIntPtr(command.PipelineLayoutHandle).Target!;

                if (
                    (layout.PsoHandle != currentPso) ||
                    (layout.RootSignatureHandle != currentRootSig)
                ) {
                    commandList->SetGraphicsRootSignature((ID3D12RootSignature*)layout.RootSignatureHandle);
                    commandList->SetPipelineState((ID3D12PipelineState*)layout.PsoHandle);
                    currentPso = layout.PsoHandle;
                    currentRootSig = layout.RootSignatureHandle;
                }
            }

            if (command.DescriptorHeapHandle != 0) {
                var heap = (ID3D12DescriptorHeap*)command.DescriptorHeapHandle;

                commandList->SetDescriptorHeaps(1, &heap);
            }

            if (
                (command.DescriptorTableGpuHandle != 0) &&
                (layout is not null) &&
                (layout.DescriptorTableParamIndex >= 0)
            ) {
                var gpuHandle = new D3D12_GPU_DESCRIPTOR_HANDLE { ptr = command.DescriptorTableGpuHandle, };

                commandList->SetGraphicsRootDescriptorTable(
                    (uint)layout.DescriptorTableParamIndex,
                    gpuHandle
                );
            }

            if (command.VertexBufferHandle != 0) {
                var view = (DirectXVertexBufferView)GCHandle.FromIntPtr(command.VertexBufferHandle).Target!;
                var vbView = new D3D12_VERTEX_BUFFER_VIEW {
                    BufferLocation = view.BufferLocation,
                    SizeInBytes = view.SizeBytes,
                    StrideInBytes = view.StrideBytes,
                };

                commandList->IASetVertexBuffers(0, 1, &vbView);
            }

            var rootConstants = command.RootConstants;

            if (
                (rootConstants is not null) &&
                (layout is not null) &&
                (layout.RootConstantsParamIndex >= 0) &&
                (0 < rootConstants.Data.Length)
            ) {
                fixed (byte* pData = rootConstants.Data.Span) {
                    commandList->SetGraphicsRoot32BitConstants(
                        RootParameterIndex: (uint)layout.RootConstantsParamIndex,
                        Num32BitValuesToSet: (uint)(rootConstants.Data.Length / sizeof(uint)),
                        pSrcData: pData,
                        DestOffsetIn32BitValues: (rootConstants.Offset / sizeof(uint))
                    );
                }
            }

            var p = command.DrawParameters;

            commandList->DrawInstanced(
                VertexCountPerInstance: p.VertexCount,
                InstanceCount: p.InstanceCount,
                StartVertexLocation: p.StartVertexLocation,
                StartInstanceLocation: p.StartInstanceLocation
            );
        }

        var toPresent = CreateTransition(
            resource: backBuffer,
            before: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
            after: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT
        );

        commandList->ResourceBarrier(1, &toPresent);
    }

    private static D3D12_RESOURCE_BARRIER CreateTransition(
        ID3D12Resource* resource,
        D3D12_RESOURCE_STATES before,
        D3D12_RESOURCE_STATES after
    ) {
        var barrier = new D3D12_RESOURCE_BARRIER {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        barrier.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            pResource = resource,
            StateAfter = after,
            StateBefore = before,
            Subresource = AllSubresources,
        };

        return barrier;
    }
}
