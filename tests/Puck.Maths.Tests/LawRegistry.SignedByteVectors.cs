namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] SignedByteVectorCases() => [
        Case(
            id: "signed-byte-vectors.dot-vs-oracle",
            run: () => Laws.Claim(
                claim: SignedByteVectorClaims.DotVsOracle,
                lawId: "signed-byte-vectors.dot-vs-oracle"
            )
        ),
        Case(
            id: "signed-byte-vectors.dot-tiers-vs-scalar-rung",
            run: () => Laws.Claim(
                claim: SignedByteVectorClaims.DotTiersVsScalarRung,
                lawId: "signed-byte-vectors.dot-tiers-vs-scalar-rung"
            )
        ),
        Case(
            id: "signed-byte-vectors.cosine-vs-oracle",
            run: () => Laws.Claim(
                claim: SignedByteVectorClaims.CosineVsOracle,
                lawId: "signed-byte-vectors.cosine-vs-oracle"
            )
        ),
        Case(
            id: "signed-byte-vectors.normalize-admits",
            run: () => Laws.Claim(
                claim: SignedByteVectorClaims.NormalizeAdmits,
                lawId: "signed-byte-vectors.normalize-admits"
            )
        ),
        Case(
            id: "signed-byte-vectors.quantize-unit",
            run: () => Laws.Claim(
                claim: SignedByteVectorClaims.QuantizeUnit,
                lawId: "signed-byte-vectors.quantize-unit"
            )
        ),
        Case(
            id: "signed-byte-vectors.admission-tolerance",
            run: () => Laws.Claim(
                claim: SignedByteVectorClaims.AdmissionTolerance,
                lawId: "signed-byte-vectors.admission-tolerance"
            )
        ),
    ];
}
