namespace Puck.Abstractions.Gpu;

/// <summary>
/// Backend-neutral GPU device context. Resolved through <c>IHostContext.TryResolveCapability</c> by nodes that
/// render on the shared device chain without binding to a specific backend. Each backend implements this
/// interface alongside its own specific context (e.g. <c>IVulkanDeviceContext</c> or <c>IDirectXDeviceContext</c>).
/// </summary>
public interface IGpuDeviceContext {
    /// <summary>Gets the device's adapter LUID in the DXGI packing (<c>HighPart &lt;&lt; 32 | LowPart</c>) — the
    /// identity that lets another API's device be created on the SAME physical adapter so shared GPU resources are
    /// openable across them — or zero when the platform or driver reports none (non-Windows, or a driver without the
    /// device-ID query), in which case cross-API sharing is unavailable.</summary>
    long AdapterLuid { get; }
    /// <summary>Gets the opaque native device handle (e.g. <c>VkDevice</c>, <c>ID3D12Device*</c>).</summary>
    nint DeviceHandle { get; }

    /// <summary>Blocks until the device is idle — all queued work has completed.</summary>
    void WaitIdle();
}
