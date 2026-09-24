namespace Puck.Abstractions.Gpu;

/// <summary>
/// The registry of the render nodes a host composed, so one readout can report every node's GPU work without knowing
/// how the host built them. A host without a renderer registers none.
/// </summary>
public interface IGpuWorkRegistry {
    /// <summary>Gets the identity of the device the nodes render on, or <see langword="null"/> before the device is
    /// brought up.</summary>
    GpuDeviceIdentity? DeviceIdentity { get; }

    /// <summary>Appends every registered node, in the order a readout lists them. A node registered while this runs
    /// may or may not be included; one never appears twice.</summary>
    /// <param name="nodes">The list to append to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="nodes"/> is <see langword="null"/>.</exception>
    void CopyNodes(List<GpuWorkNode> nodes);
}
