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
    /// <summary>Gets the neutral services bound to this device context, created with it by its backend. It is the one
    /// way a consumer reaches the recorder, the bindings, the queue submitter and the factories, and none of them takes
    /// a device argument. Reading it never brings the device up; a service does so on its first call that needs the
    /// device.</summary>
    GpuDeviceServices Services { get; }
    /// <summary>Gets what the device is — backend, adapter, driver, API version — as its backend reported it when the
    /// device was created, or <see langword="null"/> before the device is brought up or when the host has no real
    /// device. Reading it never brings the device up. It is recorded beside counted work and never branched on.</summary>
    GpuDeviceIdentity? Identity { get; }
    /// <summary>Gets what the device can bind — descriptor sets or root-signature words, push-constant bytes, per-stage
    /// descriptor limits, and Direct3D 12's binding tier, root signature version, shader model and heap sizes — as its
    /// backend reported it when the device was created, or <see langword="null"/> before the device is brought up or
    /// when the host has no real device. Reading it never brings the device up. Like <see cref="Identity"/> it is
    /// recorded and never branched on.</summary>
    GpuDeviceCapabilities? Capabilities { get; }
    /// <summary>Gets what the device's memory is — whether it is coherent unified memory, how much is device-local, its
    /// largest device-local heap, and how much of it the host can write directly — as its backend reported it when the
    /// device was created, or the default profile, which reports nothing, before the device is brought up or when the
    /// host has no real device. Reading it never brings the device up. Unlike <see cref="Identity"/> it is branched on:
    /// <see cref="GpuResidency.Select"/> chooses a region's residency from it.</summary>
    GpuMemoryProfile MemoryProfile { get; }

    /// <summary>Blocks until the device is idle — all queued work has completed. A device that was never brought up
    /// has run no work, so waiting on it returns at once and never attempts the bring-up; teardown relies on this.</summary>
    void WaitIdle();
}
