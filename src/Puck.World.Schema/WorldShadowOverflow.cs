using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>How a new shadow-slot crossing behaves while every allowed handoff is occupied.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldShadowOverflow>))]
public enum WorldShadowOverflow {
    /// <summary>Waits until a handoff is available, retaining only current desired assignments.</summary>
    Queue,
    /// <summary>Applies the overflowing crossing without a fade.</summary>
    Instant,
}
