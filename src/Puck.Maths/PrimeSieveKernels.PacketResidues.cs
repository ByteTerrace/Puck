using System.Runtime.CompilerServices;

namespace Puck.Maths;

public static partial class PrimeSieveKernels {
    /// <summary>Names one prime residue channel of the thirty-wheel as a type, so the marking loop specializes per channel.</summary>
    /// <remarks>Public so a law can compare each channel's packed masks with the wheel derivation; only the sieve's own loops use them.</remarks>
    public interface IPacketResidue {
        /// <summary>Gets the eight clear masks of the channel, one per ascending multiplier phase, packed from the lowest byte.</summary>
        static abstract ulong Masks { get; }
        /// <summary>Gets the channel's residue modulo thirty.</summary>
        static abstract uint Residue { get; }
    }
    // Byte i clears the bit PrimeWheel30.TargetBit(r, i) of (r * s_i) mod 30, for ascending multiplier residues s_i. The
    // words are written out so a constant-residue instantiation folds them while importing;
    // prime-exploration.wheel-tables-match-their-derivation proves they equal the derivation.
    /// <summary>The packet channel for residue one modulo thirty.</summary>
    public readonly struct PacketResidue1 : IPacketResidue {
        /// <inheritdoc/>
        public static uint Residue => 1;
        /// <inheritdoc/>
        public static ulong Masks => 0x7FBFDFEFF7FBFDFEUL;
    }
    /// <summary>The packet channel for residue seven modulo thirty.</summary>
    public readonly struct PacketResidue7 : IPacketResidue {
        /// <inheritdoc/>
        public static uint Residue => 7;
        /// <inheritdoc/>
        public static ulong Masks => 0xBFFBF77FFEEFDFFDUL;
    }
    /// <summary>The packet channel for residue nineteen modulo thirty.</summary>
    public readonly struct PacketResidue19 : IPacketResidue {
        /// <inheritdoc/>
        public static uint Residue => 19;
        /// <inheritdoc/>
        public static ulong Masks => 0xFBEFFEBFFD7FF7DFUL;
    }
    /// <summary>The packet channel for residue thirteen modulo thirty.</summary>
    public readonly struct PacketResidue13 : IPacketResidue {
        /// <inheritdoc/>
        public static uint Residue => 13;
        /// <inheritdoc/>
        public static ulong Masks => 0xEF7FFDFBDFBFFEF7UL;
    }
    /// <summary>The packet channel for residue eleven modulo thirty.</summary>
    public readonly struct PacketResidue11 : IPacketResidue {
        /// <inheritdoc/>
        public static uint Residue => 11;
        /// <inheritdoc/>
        public static ulong Masks => 0xDFF77FFDBFFEEFFBUL;
    }
    /// <summary>The packet channel for residue seventeen modulo thirty.</summary>
    public readonly struct PacketResidue17 : IPacketResidue {
        /// <inheritdoc/>
        public static uint Residue => 17;
        /// <inheritdoc/>
        public static ulong Masks => 0xF7FEBFDFFBFD7FEFUL;
    }
    /// <summary>The packet channel for residue twenty-nine modulo thirty.</summary>
    public readonly struct PacketResidue29 : IPacketResidue {
        /// <inheritdoc/>
        public static uint Residue => 29;
        /// <inheritdoc/>
        public static ulong Masks => 0xFEFDFBF7EFDFBF7FUL;
    }
    /// <summary>The packet channel for residue twenty-three modulo thirty.</summary>
    public readonly struct PacketResidue23 : IPacketResidue {
        /// <inheritdoc/>
        public static uint Residue => 23;
        /// <inheritdoc/>
        public static ulong Masks => 0xFDDFEFFE7FF7FBBFUL;
    }

    /// <summary>Gets the byte offset of a packet's mark at one phase past its phase-zero mark, for a prime
    /// <c>30q + r</c> of the residue <typeparamref name="TResidue"/> names.</summary>
    /// <typeparam name="TResidue">The prime's residue modulo thirty.</typeparam>
    /// <param name="phase">The phase, from one through seven.</param>
    /// <param name="quotient">The prime's quotient <c>q</c> by thirty, with <c>30q + r</c> fitting in <see cref="uint"/>.</param>
    /// <returns><c>(PrimeWheel30.Multiplier(phase) − 1) · q + PrimeWheel30.Lift(r, PrimeWheel30.Multiplier(phase))</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="phase"/> is outside one through seven, or
    /// <paramref name="quotient"/> makes the prime exceed <see cref="uint.MaxValue"/>.</exception>
    /// <exception cref="ArgumentException"><typeparamref name="TResidue"/> does not name a unit modulo thirty.</exception>
    /// <remarks>Each phase is written out so that a constant phase folds to one multiply and add.</remarks>
    public static nint PacketOffset<TResidue>(int phase, uint quotient)
        where TResidue : struct, IPacketResidue {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: phase, other: 1, paramName: nameof(phase));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: phase, other: 7, paramName: nameof(phase));
        var residue = TResidue.Residue;

        if ((residue >= PrimeWheel30.Modulus) || !PrimeWheel30.TryChannel(channel: out _, residue: ((byte)residue))) {
            throw new ArgumentException(message: "The packet residue must be a unit modulo thirty.", paramName: nameof(TResidue));
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: quotient, other: ((uint.MaxValue - residue) / PrimeWheel30.Modulus), paramName: nameof(quotient));
        return PacketOffsetCore<TResidue>(phase: phase, quotient: quotient);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static nint PacketOffsetCore<TResidue>(int phase, uint quotient)
        where TResidue : struct, IPacketResidue =>
        phase switch {
            1 => ((nint)((6 * quotient) + ((TResidue.Residue * 7) / PrimeWheel30.Modulus))),
            2 => ((nint)((10 * quotient) + ((TResidue.Residue * 11) / PrimeWheel30.Modulus))),
            3 => ((nint)((12 * quotient) + ((TResidue.Residue * 13) / PrimeWheel30.Modulus))),
            4 => ((nint)((16 * quotient) + ((TResidue.Residue * 17) / PrimeWheel30.Modulus))),
            5 => ((nint)((18 * quotient) + ((TResidue.Residue * 19) / PrimeWheel30.Modulus))),
            6 => ((nint)((22 * quotient) + ((TResidue.Residue * 23) / PrimeWheel30.Modulus))),
            _ => ((nint)((28 * quotient) + ((TResidue.Residue * 29) / PrimeWheel30.Modulus))),
        };
}
