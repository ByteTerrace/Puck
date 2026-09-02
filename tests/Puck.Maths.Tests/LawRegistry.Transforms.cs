namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] NttCases() => [
        // ---- the exact number-theoretic transform over PrimeField64 ----
        Case(
            id: "ntt.prime-and-primitive-root",
            run: () => Laws.Claim(
                claim: NttClaims.PrimeAndPrimitiveRoot,
                lawId: "ntt.prime-and-primitive-root"
            )
        ),
        Case(
            id: "ntt.round-trip-exact",
            run: () => Laws.Claim(
                claim: NttClaims.RoundTripExact,
                lawId: "ntt.round-trip-exact"
            )
        ),
        Case(
            id: "ntt.linearity-exact",
            run: () => Laws.Claim(
                claim: NttClaims.LinearityExact,
                lawId: "ntt.linearity-exact"
            )
        ),
        Case(
            id: "ntt.convolution-vs-oracle",
            run: () => Laws.Claim(
                claim: NttClaims.ConvolutionVsOracle,
                lawId: "ntt.convolution-vs-oracle"
            )
        ),
        Case(
            id: "ntt.pointwise-multiply-is-elementwise-product",
            run: () => Laws.Claim(
                claim: NttClaims.PointwiseMultiplyIsElementwiseProduct,
                lawId: "ntt.pointwise-multiply-is-elementwise-product"
            )
        ),
        Case(
            id: "ntt.convolution-aliasing-contract",
            run: () => Laws.Claim(
                claim: NttClaims.ConvolutionAliasingContract,
                lawId: "ntt.convolution-aliasing-contract"
            )
        ),
        Case(
            id: "ntt.length-refusals",
            run: () => Laws.Claim(
                claim: NttClaims.LengthRefusals,
                lawId: "ntt.length-refusals"
            )
        ),

    ];
    private static LawCase[] FftCases() => [
        // ---- the fixed-point FFT over FixedComplex ----
        Case(
            id: "fft.impulse-dc-nyquist-exact",
            run: () => Laws.Claim(
                claim: FftClaims.ImpulseDcNyquistExact,
                lawId: "fft.impulse-dc-nyquist-exact"
            )
        ),
        Case(
            id: "fft.round-trip-bound",
            run: () => Laws.Claim(
                claim: FftClaims.RoundTripBound,
                lawId: "fft.round-trip-bound"
            )
        ),
        Case(
            id: "fft.round-trip-bound-deep",
            run: () => Laws.Claim(
                claim: FftClaims.RoundTripBoundDeepMirror,
                lawId: "fft.round-trip-bound-deep"
            )
        ),
        Case(
            id: "fft.linearity-bound",
            run: () => Laws.Claim(
                claim: FftClaims.LinearityBound,
                lawId: "fft.linearity-bound"
            )
        ),
        Case(
            id: "fft.linearity-bound-deep",
            run: () => Laws.Claim(
                claim: FftClaims.LinearityBoundDeepMirror,
                lawId: "fft.linearity-bound-deep"
            )
        ),
        Case(
            id: "fft.parseval-bound",
            run: () => Laws.Claim(
                claim: FftClaims.ParsevalBound,
                lawId: "fft.parseval-bound"
            )
        ),
        Case(
            id: "fft.parseval-bound-deep",
            run: () => Laws.Claim(
                claim: FftClaims.ParsevalBoundDeepMirror,
                lawId: "fft.parseval-bound-deep"
            )
        ),
        Case(
            id: "fft.self-referential-bit-identity",
            run: () => Laws.Claim(
                claim: FftClaims.SelfReferentialBitIdentity,
                lawId: "fft.self-referential-bit-identity"
            )
        ),
        Case(
            id: "fft.radix2-vs-direct-sum",
            run: () => Laws.Claim(
                claim: FftClaims.Radix2VsDirectSum,
                lawId: "fft.radix2-vs-direct-sum"
            )
        ),
        Case(
            id: "fft.real-wrappers-are-faithful-embeddings",
            run: () => Laws.Claim(
                claim: FftClaims.RealWrappersAreFaithfulEmbeddings,
                lawId: "fft.real-wrappers-are-faithful-embeddings"
            )
        ),
        Case(
            id: "fft.length-refusals",
            run: () => Laws.Claim(
                claim: FftClaims.LengthRefusals,
                lawId: "fft.length-refusals"
            )
        ),
        Case(
            id: "fft.convolution-vs-oracle-bound",
            run: () => Laws.Claim(
                claim: FftClaims.ConvolutionVsOracleBound,
                lawId: "fft.convolution-vs-oracle-bound"
            )
        ),
        Case(
            id: "fft.convolution-vs-oracle-bound-deep",
            run: () => Laws.Claim(
                claim: FftClaims.ConvolutionVsOracleBoundDeepMirror,
                lawId: "fft.convolution-vs-oracle-bound-deep"
            )
        ),
        Case(
            id: "fft.pointwise-multiply-is-elementwise-product",
            run: () => Laws.Claim(
                claim: FftClaims.PointwiseMultiplyIsElementwiseProduct,
                lawId: "fft.pointwise-multiply-is-elementwise-product"
            )
        ),
        Case(
            id: "fft.convolution-aliasing-contract",
            run: () => Laws.Claim(
                claim: FftClaims.ConvolutionAliasingContract,
                lawId: "fft.convolution-aliasing-contract"
            )
        ),
    ];
    private static LawCase[] WhtCases() => [
        // ---- the exact Walsh–Hadamard transform over any binary integer ----
        Case(
            id: "wht.round-trip-exact",
            run: () => Laws.Claim(
                claim: WhtClaims.RoundTripExact,
                lawId: "wht.round-trip-exact"
            )
        ),
        Case(
            id: "wht.linearity-exact",
            run: () => Laws.Claim(
                claim: WhtClaims.LinearityExact,
                lawId: "wht.linearity-exact"
            )
        ),
        Case(
            id: "wht.forward-vs-oracle",
            run: () => Laws.Claim(
                claim: WhtClaims.ForwardVsOracleExact,
                lawId: "wht.forward-vs-oracle"
            )
        ),
        Case(
            id: "wht.parseval-exact",
            run: () => Laws.Claim(
                claim: WhtClaims.ParsevalExact,
                lawId: "wht.parseval-exact"
            )
        ),
        Case(
            id: "wht.length-refusals",
            run: () => Laws.Claim(
                claim: WhtClaims.LengthRefusals,
                lawId: "wht.length-refusals"
            )
        ),
    ];
    private static LawCase[] DctCases() => [
        // ---- the fixed-point cosine transform over FixedQ4816 ----
        Case(
            id: "dct.constant-and-impulse-exact",
            run: () => Laws.Claim(
                claim: DctClaims.ConstantAndImpulseExact,
                lawId: "dct.constant-and-impulse-exact"
            )
        ),
        Case(
            id: "dct.round-trip-bound",
            run: () => Laws.Claim(
                claim: DctClaims.RoundTripBound,
                lawId: "dct.round-trip-bound"
            )
        ),
        Case(
            id: "dct.round-trip-bound-deep",
            run: () => Laws.Claim(
                claim: DctClaims.RoundTripBoundDeepMirror,
                lawId: "dct.round-trip-bound-deep"
            )
        ),
        Case(
            id: "dct.linearity-bound",
            run: () => Laws.Claim(
                claim: DctClaims.LinearityBound,
                lawId: "dct.linearity-bound"
            )
        ),
        Case(
            id: "dct.linearity-bound-deep",
            run: () => Laws.Claim(
                claim: DctClaims.LinearityBoundDeepMirror,
                lawId: "dct.linearity-bound-deep"
            )
        ),
        Case(
            id: "dct.parseval-bound",
            run: () => Laws.Claim(
                claim: DctClaims.ParsevalBound,
                lawId: "dct.parseval-bound"
            )
        ),
        Case(
            id: "dct.parseval-bound-deep",
            run: () => Laws.Claim(
                claim: DctClaims.ParsevalBoundDeepMirror,
                lawId: "dct.parseval-bound-deep"
            )
        ),
        Case(
            id: "dct.forward-vs-direct-sum",
            run: () => Laws.Claim(
                claim: DctClaims.ForwardVsDirectSum,
                lawId: "dct.forward-vs-direct-sum"
            )
        ),
        Case(
            id: "dct.length-refusals",
            run: () => Laws.Claim(
                claim: DctClaims.LengthRefusals,
                lawId: "dct.length-refusals"
            )
        ),
    ];
}
