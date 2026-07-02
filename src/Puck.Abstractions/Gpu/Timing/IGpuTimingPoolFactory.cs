namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral GPU timestamp query pools and reports the device's timestamp capabilities. The created
/// <see cref="IGpuTimingPool"/> is usable only when <see cref="GetCapabilities"/> reports
/// <see cref="GpuTimestampCapabilities.IsSupported"/>; a caller gates on that and renders untimed otherwise.
/// </summary>
public interface IGpuTimingPoolFactory {
    /// <summary>Creates a timestamp query pool on the given device.</summary>
    /// <param name="deviceContext">The device to create the pool on.</param>
    /// <param name="queryCapacity">The number of timestamp queries the pool holds.</param>
    /// <returns>The created timestamp pool.</returns>
    IGpuTimingPool CreateTimestampPool(IGpuDeviceContext deviceContext, uint queryCapacity);
    /// <summary>Probes the device's GPU timestamp capabilities (tick period + valid bits).</summary>
    /// <param name="deviceContext">The device to probe.</param>
    /// <returns>The device's timestamp capabilities.</returns>
    GpuTimestampCapabilities GetCapabilities(IGpuDeviceContext deviceContext);
}
