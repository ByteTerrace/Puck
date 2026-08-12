using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.World.Protocol;

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
public sealed record WorldObserverDisclosure(
    WorldObserverDisclosureMode Mode = WorldObserverDisclosureMode.All,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Radius = null
) {
    /// <summary>Gets the disclose-all policy an unauthored world resolves to.</summary>
    public static WorldObserverDisclosure Default { get; } = new();

    /// <summary>Returns whether an observer at <paramref name="observerIndex"/> is delivered
    /// <paramref name="entry"/>.</summary>
    /// <param name="entry">The candidate entity.</param>
    /// <param name="observerIndex">The observer's own 0-based body index, or a negative value when the observer has
    /// no body in this world.</param>
    /// <param name="observerPosition">The observer's own position; ignored unless
    /// <see cref="Mode"/> is <see cref="WorldObserverDisclosureMode.Radius"/>.</param>
    /// <returns><see langword="true"/> when the entity is delivered.</returns>
    public bool Discloses(in EntitySnapshot entry, int observerIndex, Vector3 observerPosition) {
        if (Mode == WorldObserverDisclosureMode.All) {
            return true;
        }

        if (observerIndex < 0) {
            return false;
        }

        if (entry.Index == observerIndex) {
            return true;
        }

        return ((Mode == WorldObserverDisclosureMode.Radius)
            && (Radius is { } radius)
            && (Vector3.DistanceSquared(value1: entry.Position, value2: observerPosition) <= (radius * radius)));
    }
}
