using System.Runtime.CompilerServices;

namespace Puck.Abstractions.Counting;

/// <summary>
/// One monotonic count of deterministic work, held as a mutable field of the instance that owns it. It starts at
/// zero, only goes up, and is never reset; a reader takes a window by reading it twice and subtracting.
/// <para>
/// One thread writes a count through <see cref="Increment"/> and <see cref="Add"/>. A count that several threads
/// genuinely write uses <see cref="IncrementShared"/> and <see cref="AddShared"/> for every write instead, which are
/// interlocked. <see cref="Value"/> may be read from any thread. Declare the count as a field that is not
/// <see langword="readonly"/>: a <see langword="readonly"/> field hands each write a copy, and the write is lost.
/// </para>
/// </summary>
public struct WorkCount {
    private long m_value;

    /// <summary>Gets the total counted so far.</summary>
    public readonly long Value =>
        Volatile.Read(location: ref Unsafe.AsRef(source: in m_value));

    /// <summary>Adds a non-negative amount from the count's single writer.</summary>
    /// <param name="amount">The amount of work, in the kind's unit; zero leaves the count unchanged.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative.</exception>
    /// <exception cref="OverflowException">The total would exceed <see cref="long.MaxValue"/>; the count is unchanged.</exception>
    public void Add(long amount) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: amount);

        // A plain store: an aligned 64-bit store is atomic on every 64-bit target, and a count orders nothing, so the
        // single writer pays no barrier on the hot paths that count.
        m_value = checked((m_value + amount));
    }
    /// <summary>Adds a non-negative amount from one of several writers.</summary>
    /// <param name="amount">The amount of work, in the kind's unit; zero leaves the count unchanged.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative.</exception>
    /// <exception cref="OverflowException">The total would exceed <see cref="long.MaxValue"/>; the count is unchanged.</exception>
    public void AddShared(long amount) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: amount);

        var observed = Volatile.Read(location: ref m_value);

        while (true) {
            var replaced = Interlocked.CompareExchange(
                comparand: observed,
                location1: ref m_value,
                value: checked((observed + amount))
            );

            if (replaced == observed) {
                return;
            }

            observed = replaced;
        }
    }
    /// <summary>Adds one from the count's single writer.</summary>
    /// <exception cref="OverflowException">The count is already <see cref="long.MaxValue"/>; it is unchanged.</exception>
    public void Increment() =>
        Add(amount: 1L);
    /// <summary>Adds one from one of several writers.</summary>
    /// <exception cref="OverflowException">The count is already <see cref="long.MaxValue"/>; it is unchanged.</exception>
    public void IncrementShared() =>
        AddShared(amount: 1L);
}
