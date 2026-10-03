using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.World;

/// <summary>
/// A <c>puck.counters.ceilings.v1</c> document: the counted-cost ceilings <c>puck counters --check</c> holds the counters
/// workload to, pass by pass, and <c>puck counters --record</c> writes. Each backend's run states, for every pass of every
/// render node and for the work outside every pass, what each GPU submission kind may read there: at most its ceiling,
/// where a ceiling of zero is a required zero. A deterministic kind is judged on any device; a per-backend-deterministic
/// kind (<see cref="WorkClass.PerBackendDeterministic"/>) only on the device its run was recorded on, and is reported as
/// not judged on any other, except a zero that is a structural contract (<see cref="WorldCountCeiling.RequiredZero"/>),
/// which holds on every device and is judged on every one.
/// </summary>
/// <param name="Workload">The workload's world document, repository-relative with forward slashes.</param>
/// <param name="Script">The workload's console script, repository-relative with forward slashes.</param>
/// <param name="Runs">One run per backend, in the order they ran.</param>
public sealed record WorldCountersCeilings(
    string Workload,
    string Script,
    IReadOnlyList<WorldCountersCeilingRun> Runs
) {
    /// <summary>The document schema tag every well-formed <c>puck.counters.ceilings.v1</c> document carries.</summary>
    public const string SchemaVersion = "puck.counters.ceilings.v1";

    /// <summary>Gets the document schema tag — <see cref="SchemaVersion"/> for a well-formed document.</summary>
    public string Schema { get; init; } = SchemaVersion;
}
/// <summary>One backend's ceilings, as recorded on one device.</summary>
/// <param name="Backend">The backend the ceilings were recorded on: <c>vulkan</c> or <c>directx</c>.</param>
/// <param name="Device">The device they were recorded on, which a per-backend-deterministic ceiling is judged on
/// alone.</param>
/// <param name="Width">The offscreen presentation's width in pixels.</param>
/// <param name="Height">The offscreen presentation's height in pixels.</param>
/// <param name="Ceilings">Every pass's ceiling for every kind, in the order the run reported the counts.</param>
public sealed record WorldCountersCeilingRun(
    string Backend,
    GpuDeviceIdentity Device,
    int Width,
    int Height,
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
/// structural contract and not one device's reading. A per-backend-deterministic ceiling is judged on a foreign device
/// only when it is a required zero. Only a zero ceiling of a per-backend-deterministic class carries it; a deterministic
/// ceiling is judged everywhere by its class, and a count loosened to its pass's class is a reading of the recording
/// device alone.</param>
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
