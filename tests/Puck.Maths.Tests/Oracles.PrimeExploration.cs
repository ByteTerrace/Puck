namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>The integer-index sieve used by every small exploration reference.</summary>
    public static readonly bool[] PrimeExplorationSieve = PrimeSieve(inclusiveMaximum: 100_000);
}
