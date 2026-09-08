using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>Which of a tick's active bodies one observer is delivered.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldObserverDisclosureMode>))]
public enum WorldObserverDisclosureMode : byte {
    /// <summary>Every active body. The default, and what every world authoring no policy delivers.</summary>
    All,

    /// <summary>Only bodies within <see cref="WorldObserverDisclosure.Radius"/> metres of the observer's own body,
    /// plus that body itself. An observer with no body of its own is delivered nothing.</summary>
    Radius,

    /// <summary>Only the observer's own body. An observer with no body of its own is delivered nothing.</summary>
    SelfOnly,
}
/// <summary>
/// A world's per-observer snapshot disclosure policy — the <c>population.disclosure</c> row. Applied at the output
/// hub's sink boundary, never inside the tick: every observer's simulation is the same simulation, and two worlds
/// differing only in this row produce bit-identical state hashes.
/// </summary>
/// <param name="Mode">Which bodies an observer is delivered.</param>
/// <param name="Radius">The disclosure radius in metres for <see cref="WorldObserverDisclosureMode.Radius"/>;
/// authored only for that mode.</param>
/// <param name="UpdateSeconds">The minimum interval between remote projection snapshots. Zero requests every
/// authority tick; the default is 0.03 seconds (30 Hz on the standard 240 Hz authority after whole-step
/// quantization). Local in-process sinks are unaffected.</param>
public sealed record WorldObserverDisclosure(
    WorldObserverDisclosureMode Mode = WorldObserverDisclosureMode.All,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Radius = null,
    float UpdateSeconds = 0.03f
) {
    /// <summary>The greatest supported remote snapshot interval.</summary>
    public const float MaximumUpdateSeconds = 1f;
    /// <summary>Gets the disclose-all policy an unauthored world resolves to.</summary>
    public static WorldObserverDisclosure Default { get; } = new();
}
