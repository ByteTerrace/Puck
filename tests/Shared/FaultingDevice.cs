using Puck.Abstractions.Gpu;

namespace Puck.Testing;

/// <summary>A GPU device context that hands out its inner device's services wrapped in <paramref name="faults"/>,
/// so a law injects creation failures at the points the faults name.</summary>
/// <param name="gpu">The device whose identity, capabilities and queue the context presents.</param>
/// <param name="faults">The creation faults the context's services apply.</param>
internal sealed class FaultingDevice(IGpuDeviceContext gpu, GpuCreationFaults faults) : IGpuDeviceContext {
    public long AdapterLuid => gpu.AdapterLuid;
    public GpuDeviceCapabilities? Capabilities => gpu.Capabilities;
    public GpuDeviceIdentity? Identity => gpu.Identity;
    public GpuMemoryProfile MemoryProfile => gpu.MemoryProfile;
    public GpuDeviceServices Services { get; } = GpuCreationFaults.Wrap(
        faults: faults,
        services: gpu.Services
    );

    public void WaitIdle() => gpu.WaitIdle();
}
