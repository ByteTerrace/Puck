namespace Puck.Abstractions.Gpu;

/// <summary>
/// Backend-neutral GPU device context. Resolved through <c>IHostContext.TryResolveCapability</c> by nodes that
/// render on the shared device chain without binding to a specific backend. Each backend implements this
/// interface alongside its own specific context (e.g. <c>IVulkanDeviceContext</c> or <c>IDirectXDeviceContext</c>).
/// </summary>
public interface IGpuDeviceContext {
    /// <summary>Gets the opaque native device handle (e.g. <c>VkDevice</c>, <c>ID3D12Device*</c>).</summary>
    nint DeviceHandle { get; }

    /// <summary>Blocks until the device is idle — all queued work has completed.</summary>
    void WaitIdle();
}
