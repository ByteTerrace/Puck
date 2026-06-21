namespace Puck.Abstractions;

/// <summary>
/// Creates backend-neutral ray-tracing acceleration structures bound to a device. The created
/// <see cref="IGpuAccelerationStructure"/> reports <see cref="IGpuAccelerationStructure.IsSupported"/> false when the
/// device lacks inline ray tracing, so a caller can gate gracefully rather than the factory throwing.
/// </summary>
public interface IGpuAccelerationStructureFactory {
    /// <summary>Creates an acceleration structure on the given device.</summary>
    /// <param name="deviceContext">The device to create the acceleration structure on.</param>
    /// <returns>The created acceleration structure (not yet built — call <see cref="IGpuAccelerationStructure.EnsureCreated"/>).</returns>
    IGpuAccelerationStructure Create(IGpuDeviceContext deviceContext);
}
