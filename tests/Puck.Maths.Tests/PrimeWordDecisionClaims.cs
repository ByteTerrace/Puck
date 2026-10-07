namespace Puck.Maths.Tests;

internal static partial class Subjects {
    /// <summary>Checks the exact word dispatch at its uint seam and across dense wide-word intervals.</summary>
    public static string? PrimeWordDecisionDispatch() {
        foreach (var (value, _) in PrimeFieldWitnessPseudoprimes) {
            var failure = PrimeWordDecisionValue(value: value);

            if (failure is not null) { return failure; }
        }
        foreach (var value in PrimeFieldPrimalityLadder) {
            var failure = PrimeWordDecisionValue(value: value);

            if (failure is not null) { return failure; }
        }
        ReadOnlySpan<ulong> starts = [
            (uint.MaxValue - 512UL), (1_000_000_000_000UL - 512UL),
            (1_000_000_000_000_000_000UL - 512UL), (ulong.MaxValue - 1024UL),
        ];

        foreach (var low in starts) {
            for (var offset = 0UL; (offset <= 1024UL); ++offset) {
                var failure = PrimeWordDecisionValue(value: (low + offset));

                if (failure is not null) { return failure; }
            }
        }
        return null;
    }

    private static string? PrimeWordDecisionValue(ulong value) {
        var expected = Oracles.ExactPrimality(value: value);

        if (PrimeField64.IsBaillieProbablePrime(value: value) != expected) { return $"Baillie-PSW differs from the independent oracle at {value}: expected {expected}"; }
        if (PrimeField64.IsPrime(value: value) != expected) { return $"Field word dispatch differs from the independent oracle at {value}: expected {expected}"; }
        if (PrimeExploration.IsPrime(value: value) != expected) { return $"Exploration word dispatch differs from the independent oracle at {value}: expected {expected}"; }
        if (CandidateAddress.TryFromValue(address: out var candidate, value: value) &&
            (PrimeExploration.IsPrimeCandidate(candidate: candidate) != expected)) {
            return $"Candidate word dispatch differs from the independent oracle at {value}: expected {expected}";
        }
        return null;
    }
}
