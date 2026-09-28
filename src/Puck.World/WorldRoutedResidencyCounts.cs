using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.World;

/// <summary>
/// The lifetime counters of the GPU node a routed scene's residency reports under (<c>sdf:routed$&lt;endpoint&gt;</c>): the
/// residency's own object-lifetime counts, then the residencies this presentation has made for the scene's endpoint and
/// the bytes the residency's tables hold, by the memory they live in. Created less released reads the residencies an
/// endpoint holds now, which the one-residency-per-endpoint rule requires be one; allocated less released reads the table
/// bytes held now, the memory every seat and full-disclosure window presenting the endpoint shares.
/// </summary>
/// <param name="lifetime">The residency's own object-lifetime counters.</param>
/// <param name="residencies">Reads the residencies made and released for the scene's endpoint.</param>
/// <param name="tables">Reads the bytes the residency's tables hold now.</param>
public sealed class WorldRoutedResidencyCounts(IWorkCounterSource lifetime, Func<(long Created, long Released)> residencies, Func<GpuMemoryBytes> tables) : IWorkCounterSource {
    private readonly Lock m_gate = new();
    private readonly WorkKind[] m_kinds = [.. lifetime.WorkKinds, .. Kinds];

    private long m_deviceLocalAllocated;
    private long m_deviceLocalReleased;
    private long m_hostVisibleAllocated;
    private long m_hostVisibleReleased;
    private GpuMemoryBytes m_last;

    /// <summary>Gets the kind counting the residencies made for an endpoint's scene.</summary>
    public static WorkKind ResidenciesCreated { get; } = new(name: "world.routed.residencies.created", unit: "residencies", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the residencies released for an endpoint's scene.</summary>
    public static WorkKind ResidenciesReleased { get; } = new(name: "world.routed.residencies.released", unit: "residencies", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting table bytes the residency came to hold in device-local memory.</summary>
    public static WorkKind DeviceLocalAllocated { get; } = new(name: "world.routed.tables.device-local.allocated", unit: "bytes", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting table bytes the residency gave back from device-local memory.</summary>
    public static WorkKind DeviceLocalReleased { get; } = new(name: "world.routed.tables.device-local.released", unit: "bytes", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting table bytes the residency came to hold in host-visible memory.</summary>
    public static WorkKind HostVisibleAllocated { get; } = new(name: "world.routed.tables.host-visible.allocated", unit: "bytes", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting table bytes the residency gave back from host-visible memory.</summary>
    public static WorkKind HostVisibleReleased { get; } = new(name: "world.routed.tables.host-visible.released", unit: "bytes", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kinds this type adds to a residency's own, in report order.</summary>
    public static IReadOnlyList<WorkKind> Kinds { get; } = [ResidenciesCreated, ResidenciesReleased, DeviceLocalAllocated, DeviceLocalReleased, HostVisibleAllocated, HostVisibleReleased];

    /// <inheritdoc/>
    public string Name => lifetime.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => m_kinds;

    /// <inheritdoc/>
    /// <remarks>The table bytes are read now and folded into the allocated and released totals by the change since the
    /// last read, so both only grow and their difference is what the tables hold.</remarks>
    public bool TryRead(WorkKind kind, out long value) {
        if (ReferenceEquals(objA: kind, objB: ResidenciesCreated)) {
            value = residencies().Created;

            return true;
        }
        if (ReferenceEquals(objA: kind, objB: ResidenciesReleased)) {
            value = residencies().Released;

            return true;
        }

        var index = Array.IndexOf(array: [DeviceLocalAllocated, DeviceLocalReleased, HostVisibleAllocated, HostVisibleReleased], value: kind);

        if (index < 0) {
            return lifetime.TryRead(
                kind: kind,
                value: out value
            );
        }

        lock (m_gate) {
            var now = tables();

            Fold(
                allocated: ref m_deviceLocalAllocated,
                last: m_last.DeviceLocal,
                now: now.DeviceLocal,
                released: ref m_deviceLocalReleased
            );
            Fold(
                allocated: ref m_hostVisibleAllocated,
                last: m_last.HostVisible,
                now: now.HostVisible,
                released: ref m_hostVisibleReleased
            );
            m_last = now;
            value = index switch {
                0 => m_deviceLocalAllocated,
                1 => m_deviceLocalReleased,
                2 => m_hostVisibleAllocated,
                _ => m_hostVisibleReleased,
            };
        }

        return true;
    }

    // Folds one memory's change since the last read into its totals.
    private static void Fold(ref long allocated, ref long released, ulong last, ulong now) {
        if (now > last) {
            allocated = checked((allocated + ((long)(now - last))));
        } else {
            released = checked((released + ((long)(last - now))));
        }
    }
}
