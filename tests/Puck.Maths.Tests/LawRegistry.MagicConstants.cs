namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain BitAlign = new(
        Key: "integer-bit-align",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain BitMorton = new(
        Key: "integer-bit-morton",
        Block: 128,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain BitScatter = new(
        Key: "integer-bit-scatter",
        Block: 128,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain BitSmear = new(
        Key: "integer-bit-smear",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MagicConstants = new(
        Key: "integer-magic-constants",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] MagicConstantCases() => [
        ClaimCase(
            claim: Subjects.FermatMaskBitOracle,
            id: "integer.fermat-mask-bit-oracle"
        ),
        ClaimCase(
            claim: Subjects.ReplicationMaskBitOracle,
            id: "integer.replication-mask-bit-oracle"
        ),
        SweptCase(
            claim: Subjects.RepeatBitsBitOracle,
            domain: MagicConstants,
            id: "integer.repeat-bits-bit-oracle",
            width: 2
        ),
        ClaimCase(
            claim: Subjects.PeriodicMaskBoundaries,
            id: "integer.periodic-mask-boundaries"
        ),
        ClaimCase(
            claim: Subjects.PowerOfTwoBitOracle,
            id: "integer.power-of-two-bit-oracle"
        ),
        ClaimCase(
            claim: Subjects.LowMaskBitOracle,
            id: "integer.low-mask-bit-oracle"
        ),
        SweptCase(
            claim: Subjects.SmearBelowHighestSetBitBitOracle,
            domain: BitSmear,
            id: "integer.smear-below-highest-set-bit-bit-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.AlignBitOracle,
            domain: BitAlign,
            id: "integer.align-bit-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.ParallelBitDepositExtractBitOracle,
            domain: BitScatter,
            id: "integer.parallel-bit-deposit-extract-bit-oracle",
            width: 2
        ),
        ClaimCase(
            claim: Subjects.FastParallelBitsDecisionTable,
            id: "integer.fast-parallel-bits-decision-table"
        ),
        SweptCase(
            claim: Subjects.MortonPathsBitOracle,
            domain: BitMorton,
            id: "integer.morton-paths-bit-oracle",
            width: 2
        ),
        ClaimCase(
            claim: Subjects.BitKernelRefusals,
            id: "integer.bit-kernel-refusals"
        ),
    ];
}
