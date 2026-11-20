using System.Runtime.CompilerServices;

namespace Puck.Abstractions;

/// <summary>
/// Gives each object a 64-bit serial for as long as it lives: its reference identity as a word that a digest can fold,
/// where <see cref="RuntimeHelpers.GetHashCode(object)"/> is 32 bits and shared by distinct objects. Two live objects
/// never share a serial, and an object's serial never changes; serials count up from one in the order objects are first
/// asked for, so they are unique within one process and mean nothing across processes or runs. A change key that
/// counts an object by reference folds its serial; nothing that feeds simulation state may. Safe for concurrent use.
/// </summary>
public static class ReferenceSerial {
    [SideTable(reason: "attaches each object's serial, its reference identity, which no code derives from the object")]
    private static readonly ConditionalWeakTable<object, StrongBox<ulong>> Serials = new();
    private static ulong Next;

    /// <summary>Returns the serial of <paramref name="value"/>, giving it the next one when it has none.</summary>
    /// <param name="value">The object.</param>
    /// <returns>The object's serial, never zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static ulong Of(object value) {
        ArgumentNullException.ThrowIfNull(argument: value);

        return Serials.GetValue(key: value, createValueCallback: static _ => new StrongBox<ulong>(value: Interlocked.Increment(location: ref Next))).Value;
    }
}
