using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>The one storage-shape axis the library answers "how is this row addressed" with, derived from the
/// row's authored <see cref="StateDomain"/> by <see cref="RowShapes.FromDomain"/> and never declared
/// separately.</summary>
[JsonConverter(typeof(StrictEnumConverter<RowShape>))]
public enum RowShape : byte {
    /// <summary>One cell, keyed <see cref="StateRow.SlotKey"/>; an omitted key addresses it.</summary>
    Slot,

    /// <summary>Author-chosen keys whose order carries no gameplay meaning.</summary>
    Keyed,

    /// <summary>Author-chosen keys whose order is pile order — first and last selection mean something.</summary>
    Ordered,

    /// <summary>One cell per cell of a named topology.</summary>
    Lattice,

    /// <summary>A fixed-capacity ring of pushed values, oldest overwritten first.</summary>
    Ring,
}
/// <summary>Derives a row's <see cref="RowShape"/> from its authored domain.</summary>
public static class RowShapes {
    /// <summary>Returns the shape a row of <paramref name="domain"/> is stored and addressed in.</summary>
    /// <param name="domain">The row's effective domain.</param>
    /// <returns>The derived shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="domain"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="domain"/> is a case this derivation does not
    /// answer for.</exception>
    public static RowShape FromDomain(StateDomain domain) {
        ArgumentNullException.ThrowIfNull(argument: domain);

        return domain switch {
            StateDomain.Slot => RowShape.Slot,
            StateDomain.Keys => RowShape.Keyed,
            StateDomain.KeysOf keysOf => (keysOf.Ordered
                ? RowShape.Ordered
                : RowShape.Keyed
            ),
            StateDomain.CellsOf => RowShape.Lattice,
            StateDomain.Ring => RowShape.Ring,
            _ => throw new InvalidOperationException(message: $"State domain '{domain.GetType().Name}' has no row shape."),
        };
    }
}
