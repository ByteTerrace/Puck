namespace Puck.Maths;

public static partial class PrimeExtensions {
    /// <summary>Decides primality exactly throughout the unsigned 64-bit domain.</summary>
    /// <param name="value">The integer to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is prime; zero and one are not prime.</returns>
    /// <remarks>
    /// Words within <see cref="uint"/> take <see cref="IsPrime(uint)"/>. Larger words reject even values and the multiples
    /// of the odd primes through <see cref="PrimeKernels.TrialDivisionCeiling"/> and then run Baillie–PSW with Selfridge
    /// Method A, whose exactness on this finite domain rests on the exhaustive
    /// computation cited by <see cref="PrimeField64.IsBaillieProbablePrime(ulong)"/>.
    /// </remarks>
    public static bool IsPrime(this ulong value) => PrimeKernels.IsPrimeWord(value: value);
}
