using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Security;
using Windows.Win32.System.Com;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 fence another device signals, opened on this device from its shared NT handle
/// (<c>ID3D12Device::OpenSharedHandle</c>). A submission waits on it through
/// <see cref="IGpuQueueSubmitter.AddExternalWait"/>, which issues <c>ID3D12CommandQueue::Wait</c>. Owns one reference
/// to the fence, released on disposal; never the handle it was opened from.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXSharedFence : IGpuSharedFence {
    private nint m_fence;

    internal DirectXSharedFence(ID3D12Fence* fence) {
        m_fence = ((nint)fence);
    }

    /// <summary>Opens a shared fence on a device.</summary>
    /// <param name="device">The device the fence is opened on; stays owned by the caller.</param>
    /// <param name="sharedHandle">The fence's shared NT handle; stays owned by the caller.</param>
    /// <returns>The opened fence, owned by the caller.</returns>
    /// <exception cref="System.Runtime.InteropServices.COMException">The handle names no fence this device can open.</exception>
    internal static DirectXSharedFence Open(ID3D12Device* device, nint sharedHandle) {
        void* fence;
        var fenceIid = ID3D12Fence.IID_Guid;

        device->OpenSharedHandle(
            NTHandle: new HANDLE(value: ((void*)sharedHandle)),
            riid: &fenceIid,
            ppvObj: &fence
        );

        return new DirectXSharedFence(fence: ((ID3D12Fence*)fence));
    }

    /// <summary>Gets the native <c>ID3D12Fence</c> pointer, or zero once disposed.</summary>
    public nint FenceHandle => m_fence;
    /// <inheritdoc/>
    /// <remarks>Reads <c>ID3D12Fence::GetCompletedValue</c>. A removed device reports <c>UINT64_MAX</c>, which surfaces
    /// as <see cref="DeviceLostException"/>.</remarks>
    public ulong CompletedValue => ReadCompletedValue(fence: ((ID3D12Fence*)m_fence));

    internal static ulong ReadCompletedValue(ID3D12Fence* fence) {
        ObjectDisposedException.ThrowIf(
            condition: (fence is null),
            type: typeof(DirectXSharedFence)
        );

        var completed = fence->GetCompletedValue();

        return ((completed == ulong.MaxValue)
            ? throw new DeviceLostException(message: "ID3D12Fence::GetCompletedValue reported a removed device.")
            : completed
        );
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (0 != m_fence) {
            _ = ((IUnknown*)m_fence)->Release();
            m_fence = 0;
        }
    }
}
/// <summary>
/// A Direct3D 12 fence created on this device with <c>D3D12_FENCE_FLAG_SHARED</c> and an NT handle to it
/// (<c>ID3D12Device::CreateSharedHandle</c>), for a producer on another device to signal: a Direct3D 11 device opens
/// <see cref="SharedHandle"/> through <c>ID3D11Device5::OpenSharedFence</c>, and a Vulkan device imports it as a timeline
/// semaphore. A submission on this device waits on it through <see cref="IGpuQueueSubmitter.AddExternalWait"/>. Starts
/// at zero. Owns the fence and the handle, released and closed on disposal.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXExportableFence : IGpuExportableFence {
    private nint m_fence;
    private HANDLE m_sharedHandle;

    internal DirectXExportableFence(ID3D12Device* device) {
        device->CreateFence(
            Flags: D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_SHARED,
            InitialValue: 0,
            ppFence: out var fence,
            riid: ID3D12Fence.IID_Guid
        );
        m_fence = ((nint)fence);

        try {
            var sharedHandle = default(HANDLE);

            device->CreateSharedHandle(
                Access: GenericAll,
                Name: default(PCWSTR),
                pAttributes: ((SECURITY_ATTRIBUTES*)null),
                pHandle: &sharedHandle,
                pObject: ((ID3D12DeviceChild*)fence)
            );
            m_sharedHandle = sharedHandle;
        } catch {
            Dispose();

            throw;
        }
    }

    /// <summary>Gets the native <c>ID3D12Fence</c> pointer, or zero once disposed.</summary>
    public nint FenceHandle => m_fence;
    /// <inheritdoc/>
    public nint SharedHandle => ((nint)m_sharedHandle.Value);
    /// <inheritdoc/>
    /// <remarks>Reads <c>ID3D12Fence::GetCompletedValue</c>. A removed device reports <c>UINT64_MAX</c>, which surfaces
    /// as <see cref="DeviceLostException"/>.</remarks>
    public ulong CompletedValue => DirectXSharedFence.ReadCompletedValue(fence: ((ID3D12Fence*)m_fence));

    /// <inheritdoc/>
    public void Dispose() {
        if (!m_sharedHandle.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_sharedHandle);
            m_sharedHandle = HANDLE.Null;
        }

        if (0 != m_fence) {
            _ = ((IUnknown*)m_fence)->Release();
            m_fence = 0;
        }
    }
}
