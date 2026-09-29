using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Abstractions.Gpu;

/// <summary>What one configured pass did in a counted submission. On the wire a state is written by the name its
/// <see cref="JsonStringEnumMemberNameAttribute"/> gives it (<see cref="EnumWireName{TEnum}"/>).</summary>
[JsonConverter(typeof(StrictEnumConverter<GpuPassState>))]
public enum GpuPassState : byte {
    /// <summary>The submission never reached the pass, so it has no counts.</summary>
    [JsonStringEnumMemberName(name: "not-reached")]
    NotReached = 0,
    /// <summary>The submission entered the pass, and its counts are the work recorded while inside it.</summary>
    [JsonStringEnumMemberName(name: "executed")]
    Executed = 1,
    /// <summary>The node decided not to run the pass, so it has no counts; this is not a count of zero.</summary>
    [JsonStringEnumMemberName(name: "skipped")]
    Skipped = 2,
    /// <summary>The pass retained its earlier output because its signature and inputs were unchanged; it recorded no
    /// work and has no per-pass counts in this submission.</summary>
    [JsonStringEnumMemberName(name: "standing")]
    Standing = 3,
}
