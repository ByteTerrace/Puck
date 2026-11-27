using System.Globalization;
using Puck.Hosting.Jobs;

namespace Puck.SdfVm;

/// <summary>
/// One residency's reservation of the lighting's device storage (<see cref="SdfResidencySelector.ReserveLighting"/>),
/// under <see cref="SdfResidencySelector.LightingOwner"/>: its irradiance table, its hit, camera-sample and batch caches,
/// and the trace words its standing static meshes need in the probe scenes.
/// <para>
/// Invariant: the pool counts the lighting's storage from the frame its volume first stages, not from the frames that
/// allocate it, so the selector's elastic budget (what the pool's limit leaves the mesh region after everything held and
/// promised) does not fall when the lighting first lights. Enforced by the reservation alone: the account counts the
/// owner's peak less what its resources hold as promised, and every resource the lighting allocates is named under the
/// owner, so an allocation moves bytes from promised to held and leaves their sum standing. The guarantee stops at the
/// estimate: the trace words are sized from each traced mesh's triangles before its hierarchy is built
/// (<see cref="SdfMeshIrradianceScene.EstimateGpuWords"/>), so the budget moves by what the built scene holds past or
/// short of it, and the device's ray-query structures are not the lighting owner's.
/// </para>
/// The whole reservation is the residency's floor (fixed storage, never refused): a pool with no room for it reads over
/// its limit rather than ending the lighting. <see cref="Reserve"/> runs on the thread that prepares the residency;
/// <see cref="Describe"/> reads from any thread, a diagnostic that may see one part's update before another's.
/// </summary>
public sealed class SdfLightingHold : IDisposable {
    private readonly MemoryAccount m_account;
    private readonly MemoryAccount.DeviceReservation m_device;
    private bool m_disposed;

    internal SdfLightingHold(MemoryAccount account, MemoryAccount.DeviceReservation device) {
        m_account = account;
        m_device = device;
    }

    /// <summary>Gets the bytes reserved for the irradiance table.</summary>
    public long TableBytes { get; private set; }
    /// <summary>Gets the bytes reserved for the hit, camera-sample and batch caches.</summary>
    public long CacheBytes { get; private set; }
    /// <summary>Gets the bytes reserved for the trace words the standing static meshes need.</summary>
    public long TraceBytes { get; private set; }
    /// <summary>Gets the bytes the residency reserves in all.</summary>
    public long Bytes => checked((TableBytes + CacheBytes) + TraceBytes);

    /// <summary>Reserves the lighting's storage, raising or lowering the reservation to the parts' sum, all of it the
    /// floor. Reserving the sum already held does nothing.</summary>
    /// <param name="tableBytes">The bytes of the irradiance table.</param>
    /// <param name="cacheBytes">The bytes of the hit, camera-sample and batch caches.</param>
    /// <param name="traceBytes">The bytes of the trace words the standing static meshes need.</param>
    /// <exception cref="ArgumentOutOfRangeException">A part is negative.</exception>
    public void Reserve(long tableBytes, long cacheBytes, long traceBytes) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: tableBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(value: cacheBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(value: traceBytes);
        if (m_disposed) { return; }

        var peak = checked((tableBytes + cacheBytes) + traceBytes);

        TableBytes = tableBytes;
        CacheBytes = cacheBytes;
        TraceBytes = traceBytes;
        if (peak == m_device.Peak) { return; }
        // Within its floor a reservation is never refused.
        _ = m_device.TryReserve(floor: peak, peak: peak);
    }
    /// <summary>Describes the reservation for <c>world.residency</c>: the residency's parts, and the owner's reservation
    /// over every residency (its peak, what its resources hold and the part still promised).</summary>
    /// <returns>One line of <c>key=value</c> fields.</returns>
    public string Describe() {
        var owner = default(MemoryReservationReading);

        foreach (var reading in m_account.ReadReservations()) {
            if ((reading.Pool == m_device.Pool) && string.Equals(a: reading.Owner, b: m_device.Owner, comparisonType: StringComparison.Ordinal)) { owner = reading; break; }
        }
        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"lighting reserved={Bytes}B table={TableBytes}B caches={CacheBytes}B trace={TraceBytes}B | owner {m_device.Owner} peak={owner.Peak}B held={owner.Held}B promised={owner.Promised}B");
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        m_device.Dispose();
    }
}
