namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain MagicConstants = new(
        Key: "integer-magic-constants",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] MagicConstantCases() => [
        Case(id: "integer.fermat-mask-bit-oracle", run: () => Laws.Claim(
            lawId: "integer.fermat-mask-bit-oracle", claim: Subjects.FermatMaskBitOracle)),
        Case(id: "integer.replication-mask-bit-oracle", run: () => Laws.Claim(
            lawId: "integer.replication-mask-bit-oracle", claim: Subjects.ReplicationMaskBitOracle)),
        Case(id: "integer.repeat-bits-bit-oracle", run: () => Laws.SweptClaim(
            lawId: "integer.repeat-bits-bit-oracle", domain: MagicConstants, tier: Tier.Default,
            width: 2, claim: Subjects.RepeatBitsBitOracle)),
        Case(id: "integer.periodic-mask-boundaries", run: () => Laws.Claim(
            lawId: "integer.periodic-mask-boundaries", claim: Subjects.PeriodicMaskBoundaries)),
    ];
}
