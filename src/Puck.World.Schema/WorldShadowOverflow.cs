using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>How a shadow crossing proceeds when its handoff capacity is occupied.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldShadowOverflow>))]
public enum WorldShadowOverflow {
    /// <summary>Applies the crossing atomically without a fade.</summary>
    [JsonStringEnumMemberName("instant")]
    Instant = 0,

    /// <summary>Waits until a handoff is available, retaining only current desired assignments.</summary>
    [JsonStringEnumMemberName("queue")]
    Queue = 1,
}
