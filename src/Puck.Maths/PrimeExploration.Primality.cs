namespace Puck.Maths;

public static partial class PrimeExploration {
    /// <summary>Decides primality exactly throughout the unsigned 64-bit domain.</summary>
    /// <param name="value">The integer to test.</param>
    /// <returns>Whether <paramref name="value"/> is prime; zero and one are not prime.</returns>
    /// <remarks>The existing exact uint decision handles small words. Larger words pass wheel rejection and
    /// Sinclair's seven-witness Miller–Rabin test, using exact Montgomery products with UInt128 intermediates.</remarks>
    public static bool IsPrime(ulong value) => PrimeKernels.IsPrimeWord(value: value);
    /// <summary>Decides a validated wheel candidate without repeating divisibility tests for two, three and five.</summary>
    /// <param name="candidate">The validated address; its default value encodes the nonprime integer one.</param>
    /// <returns>Whether the addressed integer is prime.</returns>
    public static bool IsPrimeCandidate(CandidateAddress candidate) => PrimeKernels.IsPrimeCandidateWord(value: candidate.Value);
}
