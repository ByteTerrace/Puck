using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.World;

/// <summary>
/// A <c>puck.counters.ceilings.v1</c> document: the counted-cost ceilings <c>puck counters --check</c> holds the counters
/// workload to, pass by pass, and <c>puck counters --record</c> writes. Each backend states, for every pass of every
/// render node and for the work outside every pass, what each GPU submission kind may read there: at most its ceiling,
/// where a ceiling of zero is a required zero. The ceilings every device shares (deterministic counts, and the zeros that
/// are structural contracts, <see cref="WorldCountCeiling.RequiredZero"/>) are the backend's own; the magnitudes of
/// per-backend-deterministic counts (<see cref="WorkClass.PerBackendDeterministic"/>), which follow the device, are one
/// record per device, and a run is judged against its own device's record.
/// </summary>
/// <param name="Workload">The workload's world document, repository-relative with forward slashes.</param>
/// <param name="Script">The workload's console script, repository-relative with forward slashes.</param>
/// <param name="Width">The offscreen presentation's width in pixels.</param>
/// <param name="Height">The offscreen presentation's height in pixels.</param>
/// <param name="Backends">One entry per backend, in the order they ran.</param>
public sealed record WorldCountersCeilings(
    string Workload,
    string Script,
    int Width,
    int Height,
    IReadOnlyList<WorldCountersBackendCeilings> Backends
) {
    /// <summary>The document schema tag every well-formed <c>puck.counters.ceilings.v1</c> document carries.</summary>
    public const string SchemaVersion = "puck.counters.ceilings.v1";

    /// <summary>Gets the document schema tag — <see cref="SchemaVersion"/> for a well-formed document.</summary>
    public string Schema { get; init; } = SchemaVersion;
}
/// <summary>One backend's ceilings: one record per device it was recorded on, and the ceilings every device shares.</summary>
/// <param name="Backend">The backend: <c>vulkan</c> or <c>directx</c>.</param>
/// <param name="Devices">One record per device, keyed by its adapter (the PCI vendor and device) and its driver
/// implementation (<see cref="GpuDeviceIdentity.DriverId"/>), in the order they were first recorded; a record of one device
/// never moves another's.</param>
/// <param name="Ceilings">The ceilings every device is judged against: each deterministic count's, and each required
/// zero, in the order the run reported the counts.</param>
public sealed record WorldCountersBackendCeilings(
    string Backend,
    IReadOnlyList<WorldCountersDeviceCeilings> Devices,
    IReadOnlyList<WorldCountCeiling> Ceilings
);
/// <summary>One device's ceilings on one backend: the per-backend-deterministic magnitudes its run read.</summary>
/// <param name="Device">The device as its run reported it, its driver version among it as recorded evidence: a driver
/// update keeps the record, which the device is still judged against.</param>
/// <param name="Ceilings">Every per-backend-deterministic count that is not a required zero, in the order the run reported
/// the counts.</param>
public sealed record WorldCountersDeviceCeilings(
    GpuDeviceIdentity Device,
    IReadOnlyList<WorldCountCeiling> Ceilings
);
/// <summary>What one kind may read in one pass of one render node, or in the work outside its every pass.</summary>
/// <param name="Node">The render node.</param>
/// <param name="Pass">The pass, or <see langword="null"/> for the work outside every pass.</param>
/// <param name="Kind">The GPU submission kind's dotted name.</param>
/// <param name="Class">What the count was recorded as: the kind's class, loosened to its pass's.</param>
/// <param name="Ceiling">The most the count may read; zero requires it to read zero.</param>
/// <param name="RequiredZero">Whether the ceiling is a zero that holds on every device: the kind is a magnitude that varies
/// with the device (a kernel kind such as the march steps), and the pass never does that kind of work, so zero is a
/// structural contract and not one device's reading, and it is a ceiling every device shares. Only a zero ceiling of a
/// per-backend-deterministic class carries it; a deterministic ceiling is shared by its class, and a count loosened to its
/// pass's class is one device's reading.</param>
/// <param name="Detail">The label within the pass, or null for its total or work outside every pass.</param>
public sealed record WorldCountCeiling(
    string Node,
    string? Pass,
    string Kind,
    WorkClass Class,
    long Ceiling,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequiredZero = false,
    [property: System.Text.Json.Serialization.JsonRequired] string? Detail = null
);
