namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] SignedByteVectorCases() => [
        ClaimCase(
            claim: SignedByteVectorClaims.DotVsOracle,
            id: "signed-byte-vectors.dot-vs-oracle"
        ),
        ClaimCase(
            claim: SignedByteVectorClaims.DotTiersVsScalarRung,
            id: "signed-byte-vectors.dot-tiers-vs-scalar-rung"
        ),
        ClaimCase(
            claim: SignedByteVectorClaims.CosineVsOracle,
            id: "signed-byte-vectors.cosine-vs-oracle"
        ),
        ClaimCase(
            claim: SignedByteVectorClaims.NormalizeAdmits,
            id: "signed-byte-vectors.normalize-admits"
        ),
        ClaimCase(
            claim: SignedByteVectorClaims.QuantizeUnit,
            id: "signed-byte-vectors.quantize-unit"
        ),
        ClaimCase(
            claim: SignedByteVectorClaims.AdmissionTolerance,
            id: "signed-byte-vectors.admission-tolerance"
        ),
    ];
}
