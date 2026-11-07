namespace Puck.State;

/// <summary>What an address named for the evaluation in flight: a value, nothing, or a fault.</summary>
/// <remarks><see cref="Case"/> spells a named address as <see cref="FaultCase.Finite"/> and an address naming nothing
/// as <see cref="FaultCase.Absent"/>; the value is reachable only through <see cref="TryNamed"/>.
/// <c>default(Resolved&lt;T&gt;)</c> is <see cref="FaultCase.Faulted"/>.</remarks>
/// <typeparam name="T">The resolved value: an interned key, an index, a row ordinal or a text.</typeparam>
public readonly struct Resolved<T> {
    private readonly T? m_value;
    private readonly FaultCase m_case;

    private Resolved(FaultCase faultCase, T? value) {
        m_case = faultCase;
        m_value = value;
    }

    /// <summary>Gets the address that names nothing (an empty zone's endpoint, a pointer spelling no cell name); a read
    /// through it is absent.</summary>
    public static Resolved<T> Nothing => new(
        faultCase: FaultCase.Absent,
        value: default
    );
    /// <summary>Gets the address whose computation faulted (overflow, a divide by zero, a value with no number); a read
    /// through it faults as arithmetic and a write through it refuses.</summary>
    public static Resolved<T> Faulted => default;
    /// <summary>Gets which case the address resolved to: <see cref="FaultCase.Finite"/> when it named a value,
    /// <see cref="FaultCase.Absent"/> when it named nothing, <see cref="FaultCase.Faulted"/> when computing it
    /// faulted.</summary>
    public FaultCase Case => m_case;

    /// <summary>Creates the resolution of an address that named a value. A key no row holds is still named: it reads
    /// zero.</summary>
    /// <param name="value">The value the address named.</param>
    public static Resolved<T> Named(T value) => new(
        faultCase: FaultCase.Finite,
        value: value
    );
    /// <summary>Reads the value when the address named one.</summary>
    /// <param name="value">The named value, or the default when the address named nothing or faulted.</param>
    /// <returns><see langword="true"/> when the address named a value.</returns>
    public bool TryNamed(out T value) {
        value = ((m_case == FaultCase.Finite)
            ? m_value!
            : default!);

        return (m_case == FaultCase.Finite);
    }
}
/// <summary>What the interpreter hands an operand for one declared address: the key it named, or nothing. There is no
/// faulted case to receive, because a faulted address ends the token before the operand runs.</summary>
/// <param name="Key">The interned key, or the invalid default when <paramref name="Named"/> is <see langword="false"/>
/// or the named cell is one no row holds.</param>
/// <param name="Named">Whether the address named a cell, an index or an age.</param>
/// <param name="Index">The integer the address named, for an address its operand reads as an index (a live row's
/// table index, a static table's key, a ring age); zero otherwise. An index no key table interns still arrives
/// here, which is why it is carried beside <paramref name="Key"/>.</param>
public readonly record struct ResolvedKey(CellKey Key, bool Named, long Index = 0L);
/// <summary>What a gate decided. The zero value is <see cref="Faulted"/>, so a verdict nothing set refuses.</summary>
public enum Verdict : byte {
    /// <summary>A conjunct faulted: the gate is neither open nor closed, and the latch holds.</summary>
    Faulted = 0,
    /// <summary>The gate evaluated and does not hold.</summary>
    Closed,
    /// <summary>The gate evaluated and holds.</summary>
    Open,
}
/// <summary>What a firing decided, read by the one door that commits or rewinds its scope. There is no faulted-and-fires
/// combination.</summary>
public enum FiringDecision : byte {
    /// <summary>A step the firing folds faulted: the scope rewinds and nothing the firing wrote stands.</summary>
    Faulted = 0,
    /// <summary>A step the firing folds refused: the scope rewinds exactly as a fault does.</summary>
    Refused,
    /// <summary>The gate is closed: nothing fires.</summary>
    Closed,
    /// <summary>The gate is open but the trigger holds the firing back (an Edge latch already crossed).</summary>
    Held,
    /// <summary>The effects fire.</summary>
    Fires,
}
