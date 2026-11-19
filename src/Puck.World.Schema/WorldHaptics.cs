using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>How a <see cref="WorldHapticRow"/> turns its authored state bindings into a pad pulse.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldHapticMode>))]
public enum WorldHapticMode : byte {
    /// <summary>Continuous motor drive: while <see cref="WorldHapticRow.Low"/> or <see cref="WorldHapticRow.High"/>
    /// is positive, the pad motors run at those intensities, re-armed within <see cref="WorldHapticRow.DurationSeconds"/>
    /// and stopped when both return to zero.</summary>
    Level,

    /// <summary>Rising-edge pulse: fires one finite pulse of <see cref="WorldHapticRow.DurationSeconds"/> at
    /// (<see cref="WorldHapticRow.Low"/>, <see cref="WorldHapticRow.High"/>) when <see cref="WorldHapticRow.Signal"/>
    /// transitions from non-positive to positive.</summary>
    Rise,

    /// <summary>Change-edge pulse: fires one finite pulse of <see cref="WorldHapticRow.DurationSeconds"/> at
    /// (<see cref="WorldHapticRow.Low"/>, <see cref="WorldHapticRow.High"/>) whenever <see cref="WorldHapticRow.Signal"/>
    /// changes to a new finite value after initial observation.</summary>
    Change,
}
/// <summary>
/// One row of the <c>haptics</c> document section: a world-authored pad pulse driven by presentation state.
/// </summary>
/// <param name="Id">The row's stable id, unique within <c>haptics</c>.</param>
/// <param name="Low">The low-frequency motor intensity, in <c>[0, 1]</c>.</param>
/// <param name="High">The high-frequency motor intensity, in <c>[0, 1]</c>.</param>
/// <param name="DurationSeconds">The finite pulse duration, in seconds (<c>(0, MaxDurationSeconds]</c>).</param>
/// <param name="Signal">The state-driven trigger scalar for <see cref="WorldHapticMode.Rise"/> and
/// <see cref="WorldHapticMode.Change"/> modes; omitted in <see cref="WorldHapticMode.Level"/> mode.</param>
/// <param name="Mode">How the row fires (<see cref="WorldHapticMode"/>).</param>
/// <param name="Seat">Optional target seat (<c>seat1</c>..<c>seat4</c>), or <see langword="null"/> for every local
/// joined seat.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldHapticRow(
    string Id,
    BindableScalar Low,
    BindableScalar High,
    float DurationSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Signal = null,
    WorldHapticMode Mode = WorldHapticMode.Level,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Seat = null
);
/// <summary>The <see cref="WorldHapticRow"/> bounds <see cref="WorldDefinitionValidator"/> enforces.</summary>
public static class WorldHapticCapacity {
    /// <summary>The section's declared row-count ceiling.</summary>
    public const int MaxRows = 32;
    /// <summary>The longest finite pulse duration one row may author, in seconds.</summary>
    public const float MaxDurationSeconds = 10f;
}
