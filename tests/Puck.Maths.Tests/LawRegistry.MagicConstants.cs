namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain MagicConstants = new(
        Key: "integer-magic-constants",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] MagicConstantCases() => [
        Case(
            id: "integer.fermat-mask-bit-oracle",
            run: () => Laws.Claim(
                claim: Subjects.FermatMaskBitOracle,
                lawId: "integer.fermat-mask-bit-oracle"
            )
        ),
        Case(
            id: "integer.replication-mask-bit-oracle",
            run: () => Laws.Claim(
                claim: Subjects.ReplicationMaskBitOracle,
                lawId: "integer.replication-mask-bit-oracle"
            )
        ),
        Case(
            id: "integer.repeat-bits-bit-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.RepeatBitsBitOracle,
                domain: MagicConstants,
                lawId: "integer.repeat-bits-bit-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "integer.periodic-mask-boundaries",
            run: () => Laws.Claim(
                claim: Subjects.PeriodicMaskBoundaries,
                lawId: "integer.periodic-mask-boundaries"
            )
        ),
    ];
}
