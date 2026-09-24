namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] NttCases() => [
        // ---- the exact number-theoretic transform over PrimeField64 ----
        ClaimCase(
            claim: NttClaims.PrimeAndPrimitiveRoot,
            id: "ntt.prime-and-primitive-root"
        ),
        ClaimCase(
            claim: NttClaims.RoundTripExact,
            id: "ntt.round-trip-exact"
        ),
        ClaimCase(
            claim: NttClaims.LinearityExact,
            id: "ntt.linearity-exact"
        ),
        ClaimCase(
            claim: NttClaims.ConvolutionVsOracle,
            id: "ntt.convolution-vs-oracle"
        ),
        ClaimCase(
            claim: NttClaims.PointwiseMultiplyIsElementwiseProduct,
            id: "ntt.pointwise-multiply-is-elementwise-product"
        ),
        ClaimCase(
            claim: NttClaims.ConvolutionAliasingContract,
            id: "ntt.convolution-aliasing-contract"
        ),
        ClaimCase(
            claim: NttClaims.LengthRefusals,
            id: "ntt.length-refusals"
        ),

    ];
    private static LawCase[] FftCases() => [
        // ---- the fixed-point FFT over FixedComplex ----
        ClaimCase(
            claim: FftClaims.ImpulseDcNyquistExact,
            id: "fft.impulse-dc-nyquist-exact"
        ),
        ClaimCase(
            claim: FftClaims.RoundTripBound,
            id: "fft.round-trip-bound"
        ),
        ClaimCase(
            claim: FftClaims.RoundTripBoundDeepMirror,
            id: "fft.round-trip-bound-deep"
        ),
        ClaimCase(
            claim: FftClaims.LinearityBound,
            id: "fft.linearity-bound"
        ),
        ClaimCase(
            claim: FftClaims.LinearityBoundDeepMirror,
            id: "fft.linearity-bound-deep"
        ),
        ClaimCase(
            claim: FftClaims.ParsevalBound,
            id: "fft.parseval-bound"
        ),
        ClaimCase(
            claim: FftClaims.ParsevalBoundDeepMirror,
            id: "fft.parseval-bound-deep"
        ),
        ClaimCase(
            claim: FftClaims.SelfReferentialBitIdentity,
            id: "fft.self-referential-bit-identity"
        ),
        ClaimCase(
            claim: FftClaims.Radix2VsDirectSum,
            id: "fft.radix2-vs-direct-sum"
        ),
        ClaimCase(
            claim: FftClaims.RealWrappersAreFaithfulEmbeddings,
            id: "fft.real-wrappers-are-faithful-embeddings"
        ),
        ClaimCase(
            claim: FftClaims.LengthRefusals,
            id: "fft.length-refusals"
        ),
        ClaimCase(
            claim: FftClaims.ConvolutionVsOracleBound,
            id: "fft.convolution-vs-oracle-bound"
        ),
        ClaimCase(
            claim: FftClaims.ConvolutionVsOracleBoundDeepMirror,
            id: "fft.convolution-vs-oracle-bound-deep"
        ),
        ClaimCase(
            claim: FftClaims.PointwiseMultiplyIsElementwiseProduct,
            id: "fft.pointwise-multiply-is-elementwise-product"
        ),
        ClaimCase(
            claim: FftClaims.ConvolutionAliasingContract,
            id: "fft.convolution-aliasing-contract"
        ),
    ];
    private static LawCase[] WhtCases() => [
        // ---- the exact Walsh–Hadamard transform over any binary integer ----
        ClaimCase(
            claim: WhtClaims.RoundTripExact,
            id: "wht.round-trip-exact"
        ),
        ClaimCase(
            claim: WhtClaims.LinearityExact,
            id: "wht.linearity-exact"
        ),
        ClaimCase(
            claim: WhtClaims.ForwardVsOracleExact,
            id: "wht.forward-vs-oracle"
        ),
        ClaimCase(
            claim: WhtClaims.ParsevalExact,
            id: "wht.parseval-exact"
        ),
        ClaimCase(
            claim: WhtClaims.LengthRefusals,
            id: "wht.length-refusals"
        ),
    ];
    private static LawCase[] DctCases() => [
        // ---- the fixed-point cosine transform over FixedQ4816 ----
        ClaimCase(
            claim: DctClaims.ConstantAndImpulseExact,
            id: "dct.constant-and-impulse-exact"
        ),
        ClaimCase(
            claim: DctClaims.RoundTripBound,
            id: "dct.round-trip-bound"
        ),
        ClaimCase(
            claim: DctClaims.RoundTripBoundDeepMirror,
            id: "dct.round-trip-bound-deep"
        ),
        ClaimCase(
            claim: DctClaims.LinearityBound,
            id: "dct.linearity-bound"
        ),
        ClaimCase(
            claim: DctClaims.LinearityBoundDeepMirror,
            id: "dct.linearity-bound-deep"
        ),
        ClaimCase(
            claim: DctClaims.ParsevalBound,
            id: "dct.parseval-bound"
        ),
        ClaimCase(
            claim: DctClaims.ParsevalBoundDeepMirror,
            id: "dct.parseval-bound-deep"
        ),
        ClaimCase(
            claim: DctClaims.ForwardVsDirectSum,
            id: "dct.forward-vs-direct-sum"
        ),
        ClaimCase(
            claim: DctClaims.LengthRefusals,
            id: "dct.length-refusals"
        ),
    ];
}
