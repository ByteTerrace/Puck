using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.HumbleGamingBrick.Timing;

/// <summary>
/// A position on, or a span of, the machine's master timeline, measured in T-cycles with sub-cycle precision. The
/// fundamental tick is the <c>ulong</c> raw storage of the wrapped <see cref="UFixedQ4816"/>: its high 48 bits count
/// whole T-cycles (LCD dots) and its low 16 bits carry the sub-cycle phase. Because every arithmetic operation routes
/// through <see cref="UFixedQ4816"/>'s integer-only, deterministic math, two runs with the same inputs occupy exactly
/// the same ticks on every machine.
/// <para>
/// The hot leaf members are marked for aggressive inlining: this value type sits in the innermost advance loop and is
/// consumed across the assembly boundary, where cross-assembly inlining is otherwise conservative.
/// </para>
/// </summary>
/// <param name="Value">The instant or duration as a fixed-point count of T-cycles.</param>
public readonly record struct Tick(UFixedQ4816 Value)
    : IComparable,
      IComparable<Tick>,
      IComparisonOperators<Tick, Tick, bool>,
      IAdditionOperators<Tick, Tick, Tick>,
      ISubtractionOperators<Tick, Tick, Tick>,
      IAdditiveIdentity<Tick, Tick>,
      IMinMaxValue<Tick> {
    /// <summary>The zero instant — the start of the timeline, and the additive identity for durations.</summary>
    public static Tick Zero => default;
    /// <summary>The additive identity, <see cref="Zero"/>.</summary>
    public static Tick AdditiveIdentity => default;
    /// <summary>The earliest representable instant, <see cref="Zero"/>.</summary>
    public static Tick MinValue => default;
    /// <summary>The latest representable instant.</summary>
    public static Tick MaxValue => new(Value: UFixedQ4816.MaxValue);

    /// <summary>Gets the fundamental tick: the raw <c>ulong</c> storage, the represented T-cycle count scaled by
    /// <c>2¹⁶</c>.</summary>
    public ulong RawBits {
        [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
        get => Value.Value;
    }
    /// <summary>Gets the number of whole T-cycles (LCD dots) at or before this instant, discarding the sub-cycle
    /// phase.</summary>
    public ulong WholeCycles {
        [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
        get => (Value.Value >> UFixedQ4816.FractionBitCount);
    }
    /// <summary>Gets the sub-cycle phase within the current T-cycle, a fraction in <c>[0, 1)</c> of a T-cycle.</summary>
    public UFixedQ4816 SubCyclePhase =>
        UFixedQ4816.Fractional(value: Value);

    /// <summary>Adds two ticks (instant plus duration, or duration plus duration).</summary>
    /// <param name="left">The first operand.</param>
    /// <param name="right">The second operand.</param>
    /// <returns>The sum, wrapping on overflow as <see cref="UFixedQ4816"/> does.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static Tick operator +(Tick left, Tick right) =>
        new(Value: (left.Value + right.Value));
    /// <summary>Subtracts one tick from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference, wrapping on underflow as <see cref="UFixedQ4816"/> does.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static Tick operator -(Tick left, Tick right) =>
        new(Value: (left.Value - right.Value));
    /// <summary>Scales a tick duration by a whole count — for example, advancing by <paramref name="count"/> fundamental
    /// ticks of a fixed quantum.</summary>
    /// <param name="duration">The duration to repeat.</param>
    /// <param name="count">The number of repetitions.</param>
    /// <returns>The scaled duration, wrapping on overflow.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static Tick operator *(Tick duration, ulong count) =>
        new(Value: UFixedQ4816.FromRawBits(value: unchecked((duration.Value.Value * count))));
    /// <summary>Indicates whether one instant precedes another.</summary>
    /// <param name="left">The first instant.</param>
    /// <param name="right">The second instant.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is earlier than <paramref name="right"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static bool operator <(Tick left, Tick right) =>
        (left.Value < right.Value);
    /// <summary>Indicates whether one instant precedes or equals another.</summary>
    /// <param name="left">The first instant.</param>
    /// <param name="right">The second instant.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is no later than <paramref name="right"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(Tick left, Tick right) =>
        (left.Value <= right.Value);
    /// <summary>Indicates whether one instant follows another.</summary>
    /// <param name="left">The first instant.</param>
    /// <param name="right">The second instant.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is later than <paramref name="right"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static bool operator >(Tick left, Tick right) =>
        (left.Value > right.Value);
    /// <summary>Indicates whether one instant follows or equals another.</summary>
    /// <param name="left">The first instant.</param>
    /// <param name="right">The second instant.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is no earlier than <paramref name="right"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(Tick left, Tick right) =>
        (left.Value >= right.Value);

    /// <summary>Creates a tick from a whole number of T-cycles (LCD dots).</summary>
    /// <param name="cycles">The T-cycle count.</param>
    /// <returns>The instant or duration of exactly <paramref name="cycles"/> T-cycles.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static Tick FromCycles(ulong cycles) =>
        new(Value: UFixedQ4816.FromInteger(value: cycles));
    /// <summary>Creates a tick directly from the fundamental tick: the raw fixed-point storage bits.</summary>
    /// <param name="rawBits">The raw <see cref="UFixedQ4816"/> storage, a T-cycle count scaled by <c>2¹⁶</c>.</param>
    /// <returns>The instant or duration whose <see cref="RawBits"/> equal <paramref name="rawBits"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static Tick FromRawBits(ulong rawBits) =>
        new(Value: UFixedQ4816.FromRawBits(value: rawBits));
    /// <summary>Creates a tick from a count of fundamental ticks at a given resolution.</summary>
    /// <param name="quanta">The number of fundamental ticks.</param>
    /// <param name="resolution">The resolution whose <see cref="TickResolution.Quantum"/> each tick represents.</param>
    /// <returns>The duration of <paramref name="quanta"/> fundamental ticks.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static Tick FromQuanta(ulong quanta, TickResolution resolution) =>
        new(Value: UFixedQ4816.FromRawBits(value: unchecked((quanta * resolution.QuantumRawBits))));
    /// <summary>Returns the earlier of two instants.</summary>
    /// <param name="left">The first instant.</param>
    /// <param name="right">The second instant.</param>
    /// <returns>Whichever instant is earlier.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static Tick Min(Tick left, Tick right) =>
        new(Value: UFixedQ4816.Min(x: left.Value, y: right.Value));
    /// <summary>Returns the later of two instants.</summary>
    /// <param name="left">The first instant.</param>
    /// <param name="right">The second instant.</param>
    /// <returns>Whichever instant is later.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static Tick Max(Tick left, Tick right) =>
        new(Value: UFixedQ4816.Max(x: left.Value, y: right.Value));

    /// <summary>Counts how many whole fundamental ticks of <paramref name="resolution"/> have elapsed at this instant,
    /// flooring any finer sub-cycle remainder.</summary>
    /// <param name="resolution">The resolution whose <see cref="TickResolution.Quantum"/> to count in.</param>
    /// <returns>The number of fundamental ticks at or before this instant.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public ulong ToQuanta(TickResolution resolution) =>
        (Value.Value >> (UFixedQ4816.FractionBitCount - resolution.SubdivisionLog2));
    /// <summary>Compares this instant with a boxed <see cref="Tick"/>.</summary>
    /// <param name="obj">The object to compare with, or <see langword="null"/>.</param>
    /// <returns>A negative value, zero, or a positive value as this instant precedes, equals, or follows
    /// <paramref name="obj"/>; a <see langword="null"/> <paramref name="obj"/> sorts first.</returns>
    /// <exception cref="ArgumentException"><paramref name="obj"/> is neither <see langword="null"/> nor a
    /// <see cref="Tick"/>.</exception>
    public int CompareTo(object? obj) {
        if (obj is null) { return 1; }
        if (obj is Tick other) { return CompareTo(other: other); }

        throw new ArgumentException(message: $"Object must be of type {nameof(Tick)}.", paramName: nameof(obj));
    }
    /// <summary>Compares this instant with another and indicates their relative order.</summary>
    /// <param name="other">The instant to compare with.</param>
    /// <returns>A negative value, zero, or a positive value as this instant precedes, equals, or follows
    /// <paramref name="other"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Tick other) =>
        Value.CompareTo(other: other.Value);
}
