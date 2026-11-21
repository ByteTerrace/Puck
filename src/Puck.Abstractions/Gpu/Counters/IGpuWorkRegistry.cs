namespace Puck.Abstractions.Gpu;

/// <summary>
/// The registry of the render nodes a host composed, so one readout can report every node's GPU work without knowing
/// how the host built them. A host without a renderer registers none.
/// </summary>
public interface IGpuWorkRegistry {
    /// <summary>Gets the identity of the device the nodes render on, or <see langword="null"/> before the device is
    /// brought up.</summary>
    GpuDeviceIdentity? DeviceIdentity { get; }
    /// <summary>Gets what the device the nodes render on can bind, or <see langword="null"/> before the device is
    /// brought up.</summary>
    GpuDeviceCapabilities? DeviceCapabilities { get; }

    /// <summary>Appends every registered node, in the order a readout lists them. A node registered while this runs
    /// may or may not be included; one never appears twice.</summary>
    /// <param name="nodes">The list to append to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="nodes"/> is <see langword="null"/>.</exception>
    void CopyNodes(List<GpuWorkNode> nodes);
    /// <summary>Requests that registered render nodes capture GPU kernel work counters for a readout. Returns
    /// <see langword="false"/> when every counting node already holds a counted sample (or no node counts kernel work),
    /// so the caller may read immediately; returns <see langword="true"/> when counting was armed and
    /// <paramref name="onReady"/> will be invoked once the next counted frame completes or the presentation closes.</summary>
    /// <param name="onReady">Invoked when the counted sample is ready to read.</param>
    /// <returns><see langword="true"/> when the caller should wait for <paramref name="onReady"/>; otherwise
    /// <see langword="false"/>.</returns>
    bool TryDemandKernelCounters(Action onReady) => false;
}
