using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Abstractions.Counting;

/// <summary>
/// What two readings of one count, taken at the same code over the same workload, may be expected to agree on. The
/// owner of a <see cref="WorkKind"/> declares its class once; a collector compares only what the class says is
/// comparable. On the wire a class is written by the name its <see cref="JsonStringEnumMemberNameAttribute"/> gives it
/// (<see cref="EnumWireName{TEnum}"/>).
/// </summary>
[JsonConverter(typeof(StrictEnumConverter<WorkClass>))]
public enum WorkClass : byte {
    /// <summary>The same inputs give the same count on every run, machine, and backend: a simulation count read at a
    /// pinned tick, or a GPU count of one submission.</summary>
    [JsonStringEnumMemberName(name: "deterministic")]
    Deterministic = 0,
    /// <summary>The same inputs give the same count on every run of one backend, and backends may differ: the GPU
    /// objects a node creates at a fixed extent.</summary>
    [JsonStringEnumMemberName(name: "per-backend-deterministic")]
    PerBackendDeterministic = 1,
    /// <summary>The count depends on when the work ran or on state that outlives the run — wall-clock pacing (how many
    /// submissions completed, how many presents were skipped) or a cache kept across processes — so two runs may
    /// differ and are never compared.</summary>
    [JsonStringEnumMemberName(name: "pacing")]
    Pacing = 2,
    /// <summary>A managed-allocation reading (<see cref="AllocationWindow.Measure"/>): only whether it is zero is
    /// compared, never its size. No <see cref="WorkKind"/> is of this class.</summary>
    [JsonStringEnumMemberName(name: "allocation-zero-nonzero")]
    AllocationZeroNonzero = 3,
}
