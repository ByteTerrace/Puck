using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 <see cref="IGpuCommandPool"/>: a DIRECT command allocator and command list (initially
/// closed), packed into a <see cref="DirectXCommandBufferState"/> GCHandle token the recorder records into.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuCommandPool : IGpuCommandPool {
    private readonly GCHandle m_token;

    private bool m_disposed;

    /// <summary>Initializes a new instance, creating the command allocator and list.</summary>
    public DirectXGpuCommandPool(IDirectXDeviceContext deviceContext) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        var calls = DirectXDeviceCommandCalls.Of(deviceContext: deviceContext);
        var commandList = DirectXCommandCalls.CreateCommandList(
            allocator: out var commandAllocator,
            calls: calls,
            type: D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT
        );

        DirectXCommandCalls.Close(
            calls: calls,
            commandList: commandList
        );

        m_token = GCHandle.Alloc(value: new DirectXCommandBufferState {
            Allocator = ((nint)commandAllocator),
            CommandList = ((nint)commandList),
        });
    }

    /// <inheritdoc/>
    public nint CommandBufferHandle => GCHandle.ToIntPtr(value: m_token);

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (m_token.IsAllocated) {
            var state = ((DirectXCommandBufferState)m_token.Target!);

            state.ReleaseRetainedResources();

            if (0 != state.CommandList) {
                _ = ((IUnknown*)state.CommandList)->Release();
                state.CommandList = 0;
            }

            if (0 != state.Allocator) {
                _ = ((IUnknown*)state.Allocator)->Release();
                state.Allocator = 0;
            }

            m_token.Free();
        }
    }
}
