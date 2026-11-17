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
    /// descriptor limits, constant-buffer view bytes, and Direct3D 12's binding tier, root signature version, shader model and heap sizes — as its
    /// backend reported it when the device was created, or <see langword="null"/> before the device is brought up or
    /// when the host has no real device. Reading it never brings the device up. It is recorded beside
    /// <see cref="Identity"/>; view and heap limits also bound resource admission.</summary>
    GpuDeviceCapabilities? Capabilities { get; }
    /// <summary>Gets what the device's memory is — whether it is coherent unified memory, how much is device-local, its
    /// largest device-local heap, and how much of it the host can write directly — as its backend reported it when the
    /// device was created, or the default profile, which reports nothing, before the device is brought up or when the
    /// host has no real device. Reading it never brings the device up. Unlike <see cref="Identity"/> it is branched on:
    /// <see cref="GpuResidency.Select"/> chooses a region's residency from it.</summary>
    GpuMemoryProfile MemoryProfile { get; }
    /// <summary>Gets the device's latest memory budget reading: for each segment, what its API says the process may use
    /// and is using. Reading it makes no driver call and never brings the device up; the reading changes only through
    /// <see cref="RefreshMemoryReading"/>. A device not brought up reads <see cref="GpuMemoryReading.Absent"/>, and one
    /// lost or being recreated reads <see cref="GpuMemoryReadingState.Lost"/>. Safe to read from any thread.</summary>
    GpuMemoryReading MemoryReading { get; }

    /// <summary>Refreshes <see cref="MemoryReading"/> when its device marked it stale: on Direct3D 12 when the budget-change
    /// event is set, tested without waiting; on Vulkan, whose budget has no signal, at a low fixed rate; and on both at
    /// once after the device refused an allocation (<see cref="GpuDeviceMemoryWork.DeviceRefusals"/>). A refresh that
    /// finds nothing stale makes no driver call, and one never brings the device up. The host calls it once a frame on
    /// its frame thread, at the top of the frame.</summary>
    void RefreshMemoryReading();
    /// <summary>Tells the device's memory manager how many bytes of device memory the process cannot run without, where
    /// the API has such a reservation: Direct3D 12's <c>IDXGIAdapter3::SetVideoMemoryReservation</c> on the local
    /// segment group, clamped to what the last reading says may be reserved. It is a hint the memory manager weighs,
    /// never capacity an account counts on; the next reading's reservation figure says what stands. The host passes the
    /// figure once a frame on its frame thread, and a device makes the native call only when the clamped figure
    /// changes. A device not brought up, and an API with no reservation (Vulkan), do nothing.</summary>
    /// <param name="bytes">The bytes the process cannot run without.</param>
    /// <returns>The bytes passed to the API, after clamping, or <see langword="null"/> where there is no reservation
    /// or the API refused the call.</returns>
    ulong? ReserveMemory(ulong bytes) => null;
    /// <summary>Blocks until the device is idle — all queued work has completed. A device that was never brought up
    /// has run no work, so waiting on it returns at once and never attempts the bring-up; teardown relies on this.</summary>
    void WaitIdle();
}
