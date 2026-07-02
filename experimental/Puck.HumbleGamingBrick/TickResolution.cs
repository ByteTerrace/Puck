using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.HumbleGamingBrick.Timing;

/// <summary>
/// The sub-cycle granularity of the machine's timeline: how finely a single master-clock T-cycle (one LCD dot) is
/// subdivided into the fundamental <c>ulong</c> ticks the whole system advances in. The subdivision is always a power
/// of two so the quantum lands exactly on the binary fixed-point grid of <see cref="UFixedQ4816"/> — every instant
/// the machine can occupy is then an exact multiple of <see cref="Quantum"/>, which is what keeps the simulation
/// bit-identical across machines.
/// <para>
/// The default is <see cref="Quarter"/> (four fundamental ticks per T-cycle); raise <see cref="SubdivisionLog2"/> to
/// resolve finer sub-cycle phases for the PPU and APU without changing any component's logic.
/// </para>
/// </summary>
public readonly record struct TickResolution {
    /// <summary>The largest permitted <see cref="SubdivisionLog2"/> (<c>16</c>): the quantum is one unit in the last
    /// place of <see cref="UFixedQ4816"/>, the finest the fixed-point grid can express.</summary>
    public const int MaximumSubdivisionLog2 = UFixedQ4816.FractionBitCount;
    /// <summary>The smallest permitted <see cref="SubdivisionLog2"/> (<c>0</c>): the quantum is a whole T-cycle.</summary>
    public const int MinimumSubdivisionLog2 = 0;

    /// <summary>A whole-T-cycle resolution: one fundamental tick per T-cycle, no sub-cycle movement.</summary>
    public static TickResolution Whole => new(subdivisionLog2: 0);
    /// <summary>A half-T-cycle resolution: two fundamental ticks per T-cycle.</summary>
    public static TickResolution Half => new(subdivisionLog2: 1);
    /// <summary>The default quarter-T-cycle resolution: four fundamental ticks per T-cycle.</summary>
    public static TickResolution Quarter => new(subdivisionLog2: 2);
    /// <summary>An eighth-T-cycle resolution: eight fundamental ticks per T-cycle.</summary>
    public static TickResolution Eighth => new(subdivisionLog2: 3);
    /// <summary>The resolution used when none is specified — <see cref="Quarter"/>.</summary>
    public static TickResolution Default => Quarter;

    /// <summary>Creates a resolution that subdivides each T-cycle into <c>2^<paramref name="subdivisionLog2"/></c>
    /// fundamental ticks.</summary>
    /// <param name="subdivisionLog2">The base-two logarithm of the number of fundamental ticks per T-cycle.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="subdivisionLog2"/> is outside
    /// <c>[<see cref="MinimumSubdivisionLog2"/>, <see cref="MaximumSubdivisionLog2"/>]</c>.</exception>
    public TickResolution(int subdivisionLog2) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: subdivisionLog2, other: MinimumSubdivisionLog2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: subdivisionLog2, other: MaximumSubdivisionLog2);

        SubdivisionLog2 = subdivisionLog2;
    }

    /// <summary>Gets the base-two logarithm of the number of fundamental ticks per T-cycle.</summary>
    public int SubdivisionLog2 { get; }

    /// <summary>Gets the number of fundamental ticks that make up one T-cycle (<c>2^<see cref="SubdivisionLog2"/></c>).</summary>
    public ulong TicksPerCycle {
        [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
        get => (1UL << SubdivisionLog2);
    }
    /// <summary>Gets the raw <see cref="UFixedQ4816"/> storage bits of one fundamental tick: one T-cycle scaled by
    /// <c>2¹⁶</c> then divided by <see cref="TicksPerCycle"/>.</summary>
    public ulong QuantumRawBits {
        [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
        get => (1UL << (UFixedQ4816.FractionBitCount - SubdivisionLog2));
    }
    /// <summary>Gets the duration of one fundamental tick as a fixed-point fraction of a T-cycle.</summary>
    public UFixedQ4816 Quantum =>
        UFixedQ4816.FromRawBits(value: QuantumRawBits);
}
