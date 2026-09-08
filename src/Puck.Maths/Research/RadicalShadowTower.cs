using System.Numerics;

namespace Puck.Maths;

/// <summary>The integer parameters of the radical recurrence <c>t_n²=d n²+u n+v+w t_(n+1)</c>.</summary>
/// <param name="D">The positive nonsquare quadratic coefficient.</param>
/// <param name="U">The linear coefficient.</param>
/// <param name="V">The constant coefficient.</param>
/// <param name="W">The next-tail coefficient.</param>
public readonly record struct RadicalShadowTowerParameters(
    BigInteger D,
    BigInteger U,
    BigInteger V,
    BigInteger W
);
/// <summary>Describes why radical shadow-tower analysis stopped.</summary>
public enum RadicalShadowAnalysisStatus : byte {
    /// <summary>The parameters do not define an input accepted by the analyzer.</summary>
    Invalid = 0,
    /// <summary>A finite interval has been certified.</summary>
    FiniteDomain = 1,
    /// <summary>An infinite suffix has been certified, but a finite prefix remains outside the certificate.</summary>
    Eventual = 2,
    /// <summary>Every positive index has been certified.</summary>
    Total = 3,
    /// <summary>The deterministic analysis completed without deciding the requested property.</summary>
    Undecided = 4,
    /// <summary>A deterministic work or size limit was reached.</summary>
    ResourceLimit = 5,
}
/// <summary>Sets deterministic size ceilings for radical shadow-tower analysis.</summary>
public sealed class RadicalShadowAnalysisLimits {
    /// <summary>Initializes deterministic analysis ceilings.</summary>
    /// <param name="maximumParameterBitLength">The maximum magnitude bit length of any input coefficient.</param>
    /// <param name="maximumPellRepresentatives">The maximum generalized-Pell representatives a later channel pass may retain.</param>
    /// <param name="maximumAutomatonStates">The maximum states a later channel compiler may construct.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any limit is not positive.</exception>
    public RadicalShadowAnalysisLimits(
        int maximumParameterBitLength = 4096,
        int maximumPellRepresentatives = 65_536,
        int maximumAutomatonStates = 1_000_000
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumParameterBitLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPellRepresentatives);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAutomatonStates);

        MaximumParameterBitLength = maximumParameterBitLength;
        MaximumPellRepresentatives = maximumPellRepresentatives;
        MaximumAutomatonStates = maximumAutomatonStates;
    }

    /// <summary>Gets the default deterministic ceilings.</summary>
    public static RadicalShadowAnalysisLimits Default { get; } = new();
    /// <summary>Gets the maximum DFAO state count.</summary>
    public int MaximumAutomatonStates { get; }
    /// <summary>Gets the maximum magnitude bit length of an input coefficient.</summary>
    public int MaximumParameterBitLength { get; }
    /// <summary>Gets the maximum number of retained generalized-Pell representatives.</summary>
    public int MaximumPellRepresentatives { get; }
}
/// <summary>Exact affine and channel-envelope quantities derived from radical tower parameters.</summary>
public sealed class RadicalShadowTowerLaw {
    internal RadicalShadowTowerLaw(
        RealQuadratic slope,
        RealQuadratic center,
        RealQuadratic kappa,
        RealQuadratic activationThreshold,
        BigInteger channelNormResidue,
        BigInteger channelNormModulus
    ) {
        Slope = slope;
        Center = center;
        Kappa = kappa;
        ActivationThreshold = activationThreshold;
        ChannelNormResidue = channelNormResidue;
        ChannelNormModulus = channelNormModulus;
    }

    /// <summary>Gets <c>8 d^(3/2) |kappa|</c>, the strict first-order channel-activation threshold.</summary>
    public RealQuadratic ActivationThreshold { get; }
    /// <summary>Gets the affine offset <c>u/(2 sqrt(d))+w/2</c>.</summary>
    public RealQuadratic Center { get; }
    /// <summary>Gets the modulus <c>4d</c> for <see cref="ChannelNormResidue"/>.</summary>
    public BigInteger ChannelNormModulus { get; }
    /// <summary>Gets the required residue class of a channel norm.</summary>
    public BigInteger ChannelNormResidue { get; }
    /// <summary>Gets the exact first residual coefficient in <c>t_n=sqrt(d)n+c+kappa/n+O(n^-2)</c>.</summary>
    public RealQuadratic Kappa { get; }
    /// <summary>Gets the exact affine slope <c>sqrt(d)</c>.</summary>
    public RealQuadratic Slope { get; }

    /// <summary>Returns the exact affine center <c>sqrt(d)n+c</c>.</summary>
    /// <param name="index">The positive tower index.</param>
    /// <returns>The exact quadratic surd at the affine center.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not positive.</exception>
    public RealQuadratic AffineCenterAt(BigInteger index) {
        if (index <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(index),
                message: "a radical tower index must be positive"
            );
        }

        return ((Slope * RealQuadratic.Rational(value: index)) + Center);
    }
    /// <summary>Returns the floor of the exact affine center.</summary>
    /// <param name="index">The positive tower index.</param>
    /// <returns><c>floor(sqrt(d)n+c)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not positive.</exception>
    public BigInteger AffineFloorAt(BigInteger index) => AffineCenterAt(index: index).Floor();
}
/// <summary>The proof form carried by a persisted radical shadow-tower certificate.</summary>
public enum RadicalShadowProofKind : byte {
    /// <summary>The affine center satisfies the radical recurrence identically, so every correction is zero.</summary>
    ExactAffine = 1,
}
/// <summary>A serializable mathematical claim about a radical shadow tower.</summary>
public sealed class RadicalShadowTowerCertificate {
    /// <summary>Initializes a raw, unverified certificate.</summary>
    /// <param name="parameters">The radical recurrence parameters.</param>
    /// <param name="proofKind">The mathematical proof form.</param>
    public RadicalShadowTowerCertificate(
        RadicalShadowTowerParameters parameters,
        RadicalShadowProofKind proofKind
    ) {
        Parameters = parameters;
        ProofKind = proofKind;
    }

    /// <summary>Gets the recurrence parameters.</summary>
    public RadicalShadowTowerParameters Parameters { get; }
    /// <summary>Gets the mathematical proof form.</summary>
    public RadicalShadowProofKind ProofKind { get; }
}
/// <summary>A compiled correction program paired with the mathematical certificate from which it was built.</summary>
public sealed class RadicalShadowTowerProgram {
    /// <summary>Initializes a raw, unverified compiled program.</summary>
    /// <param name="certificate">The mathematical certificate.</param>
    /// <param name="corrections">The correction sequence addressed directly by positive tower index.</param>
    /// <exception cref="ArgumentNullException"><paramref name="certificate"/> or <paramref name="corrections"/> is <see langword="null"/>.</exception>
    public RadicalShadowTowerProgram(
        RadicalShadowTowerCertificate certificate,
        AutomaticIntegerSequence corrections
    ) {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(corrections);
        Certificate = certificate;
        Corrections = corrections;
    }

    /// <summary>Gets the raw mathematical certificate.</summary>
    public RadicalShadowTowerCertificate Certificate { get; }
    /// <summary>Gets the compiled correction sequence.</summary>
    public AutomaticIntegerSequence Corrections { get; }
}
/// <summary>Describes why a raw radical shadow-tower certificate failed exact verification.</summary>
public enum RadicalShadowVerificationFailure : byte {
    /// <summary>The certificate passed verification.</summary>
    None = 0,
    /// <summary>The quadratic coefficient is not positive and nonsquare.</summary>
    InvalidRadicand = 1,
    /// <summary>The proof form is not supported by this verifier version.</summary>
    UnsupportedProof = 2,
    /// <summary>The asserted exact affine identity does not hold.</summary>
    AffineIdentityDoesNotHold = 3,
    /// <summary>The asserted everywhere-positive affine solution is not positive at the first index.</summary>
    AffineSolutionIsNotPositive = 4,
    /// <summary>The compiled correction sequence is not identically zero.</summary>
    CorrectionDoesNotMatchProof = 5,
    /// <summary>A deterministic verification size ceiling was reached.</summary>
    ResourceLimit = 6,
}
/// <summary>An opaque radical shadow-tower certificate that has passed the exact verifier.</summary>
public sealed class VerifiedRadicalShadowTower {
    internal VerifiedRadicalShadowTower(RadicalShadowTowerProgram program, RadicalShadowTowerLaw law) {
        Program = program;
        Law = law;
    }

    internal RadicalShadowTowerProgram Program { get; }

    /// <summary>Gets the exact derived affine law.</summary>
    public RadicalShadowTowerLaw Law { get; }
    /// <summary>Gets the verified recurrence parameters.</summary>
    public RadicalShadowTowerParameters Parameters => Program.Certificate.Parameters;
}
/// <summary>Verifies untrusted radical shadow-tower certificates using exact arithmetic.</summary>
public static class RadicalShadowTowerVerifier {
    internal static bool TryVerifyMathematics(
        RadicalShadowTowerCertificate certificate,
        out RadicalShadowTowerLaw law,
        out RadicalShadowVerificationFailure failure,
        RadicalShadowAnalysisLimits? limits,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(certificate);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= RadicalShadowAnalysisLimits.Default;
        var parameters = certificate.Parameters;

        if (RadicalShadowTowerAnalyzer.MaximumBitLength(parameters: parameters) > limits.MaximumParameterBitLength) {
            law = null!;
            failure = RadicalShadowVerificationFailure.ResourceLimit;
            return false;
        }
        if (!RadicalShadowTowerAnalyzer.IsValidRadicand(value: parameters.D)) {
            law = null!;
            failure = RadicalShadowVerificationFailure.InvalidRadicand;
            return false;
        }

        law = RadicalShadowTowerAnalyzer.Derive(parameters: parameters);
        if (certificate.ProofKind != RadicalShadowProofKind.ExactAffine) {
            failure = RadicalShadowVerificationFailure.UnsupportedProof;
            return false;
        }
        if (law.Kappa.Sign != 0) {
            failure = RadicalShadowVerificationFailure.AffineIdentityDoesNotHold;
            return false;
        }
        if (law.AffineCenterAt(index: BigInteger.One).Sign <= 0) {
            failure = RadicalShadowVerificationFailure.AffineSolutionIsNotPositive;
            return false;
        }

        failure = RadicalShadowVerificationFailure.None;
        return true;
    }

    /// <summary>Attempts to turn a raw certificate into an opaque verified value.</summary>
    /// <param name="program">The raw compiled program to verify.</param>
    /// <param name="verified">Receives the opaque verified certificate on success; otherwise <see langword="null"/>.</param>
    /// <param name="failure">Receives the exact verification outcome.</param>
    /// <param name="limits">The deterministic limits, or <see langword="null"/> for <see cref="RadicalShadowAnalysisLimits.Default"/>.</param>
    /// <param name="cancellationToken">The cooperative cancellation token.</param>
    /// <returns><see langword="true"/> when every required proof check succeeds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static bool TryVerify(
        RadicalShadowTowerProgram program,
        out VerifiedRadicalShadowTower? verified,
        out RadicalShadowVerificationFailure failure,
        RadicalShadowAnalysisLimits? limits = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(program);
        if (!TryVerifyMathematics(
            cancellationToken: cancellationToken,
            certificate: program.Certificate,
            failure: out failure,
            law: out var law,
            limits: limits
        )) {
            verified = null;
            return false;
        }

        limits ??= RadicalShadowAnalysisLimits.Default;
        if (program.Corrections.Automaton.StateCount > limits.MaximumAutomatonStates) {
            verified = null;
            failure = RadicalShadowVerificationFailure.ResourceLimit;
            return false;
        }

        var corrections = program.Corrections;

        for (var state = 0; (state < corrections.Automaton.StateCount); ++state) {
            var symbol = corrections.Automaton.OutputSymbol(state: state);

            if (!corrections.OutputValue(symbol: symbol).IsZero) {
                verified = null;
                failure = RadicalShadowVerificationFailure.CorrectionDoesNotMatchProof;
                return false;
            }
        }

        verified = new VerifiedRadicalShadowTower(
            law: law,
            program: program
        );
        failure = RadicalShadowVerificationFailure.None;
        return true;
    }
}
/// <summary>The deterministic result of one radical shadow-tower analysis.</summary>
public sealed class RadicalShadowTowerAnalysis {
    internal RadicalShadowTowerAnalysis(
        RadicalShadowTowerParameters parameters,
        RadicalShadowAnalysisStatus status,
        RadicalShadowTowerLaw? law,
        RadicalShadowTowerCertificate? certificate,
        string detail
    ) {
        Parameters = parameters;
        Status = status;
        Law = law;
        Certificate = certificate;
        Detail = detail;
    }

    /// <summary>Gets the total certificate when <see cref="Status"/> is <see cref="RadicalShadowAnalysisStatus.Total"/>.</summary>
    public RadicalShadowTowerCertificate? Certificate { get; }
    /// <summary>Gets a stable explanation of the outcome.</summary>
    public string Detail { get; }
    /// <summary>Gets the exact derived law when the parameters are valid and within limits.</summary>
    public RadicalShadowTowerLaw? Law { get; }
    /// <summary>Gets the analyzed parameters.</summary>
    public RadicalShadowTowerParameters Parameters { get; }
    /// <summary>Gets the analysis outcome.</summary>
    public RadicalShadowAnalysisStatus Status { get; }
}
/// <summary>Performs exact, deterministic first-pass analysis of radical shadow towers.</summary>
public static class RadicalShadowTowerAnalyzer {
    internal static RadicalShadowTowerLaw Derive(RadicalShadowTowerParameters parameters) {
        var slope = RealQuadratic.Create(
            denominator: BigInteger.One,
            radicand: parameters.D,
            rationalNumerator: BigInteger.Zero,
            surdNumerator: BigInteger.One
        );
        var center = RealQuadratic.Create(
            denominator: (2 * parameters.D),
            radicand: parameters.D,
            rationalNumerator: (parameters.W * parameters.D),
            surdNumerator: parameters.U
        );
        var numerator = (
            ((RealQuadratic.Rational(value: parameters.V) +
            (RealQuadratic.Rational(value: parameters.W) * slope)) +
            (RealQuadratic.Rational(value: parameters.W) * center)) -
            (center * center)
        );
        var kappa = (numerator / (RealQuadratic.Rational(value: 2) * slope));
        var threshold = (
            (RealQuadratic.Rational(value: (8 * parameters.D)) *
            slope) *
            kappa.Abs()
        );
        var modulus = (4 * parameters.D);
        var residue = (((parameters.W & BigInteger.One).IsZero
            ? -(parameters.U * parameters.U)
            : (parameters.D - (parameters.U * parameters.U)))
        ).FloorModulo(modulus: modulus);

        return new RadicalShadowTowerLaw(
            activationThreshold: threshold,
            center: center,
            channelNormModulus: modulus,
            channelNormResidue: residue,
            kappa: kappa,
            slope: slope
        );
    }
    internal static bool IsValidRadicand(BigInteger value) {
        if (value <= BigInteger.Zero) { return false; }
        var root = BigIntegerFunctions.SquareRoot(value: value);

        return ((root * root) != value);
    }
    internal static int MaximumBitLength(RadicalShadowTowerParameters parameters) => checked((int)new[] {
        BigInteger.Abs(value: parameters.D).GetBitLength(),
        BigInteger.Abs(value: parameters.U).GetBitLength(),
        BigInteger.Abs(value: parameters.V).GetBitLength(),
        BigInteger.Abs(value: parameters.W).GetBitLength(),
    }.Max());

    /// <summary>Analyzes arbitrary-width recurrence parameters under deterministic limits.</summary>
    /// <param name="parameters">The radical recurrence parameters.</param>
    /// <param name="limits">The deterministic limits, or <see langword="null"/> for <see cref="RadicalShadowAnalysisLimits.Default"/>.</param>
    /// <param name="cancellationToken">The cooperative cancellation token.</param>
    /// <returns>A total exact-affine certificate, an honest undecided result, or a validation/resource outcome.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static RadicalShadowTowerAnalysis Analyze(
        RadicalShadowTowerParameters parameters,
        RadicalShadowAnalysisLimits? limits = null,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= RadicalShadowAnalysisLimits.Default;

        if (MaximumBitLength(parameters: parameters) > limits.MaximumParameterBitLength) {
            return new RadicalShadowTowerAnalysis(
                certificate: null,
                detail: "an input coefficient exceeds the configured bit-length ceiling",
                law: null,
                parameters: parameters,
                status: RadicalShadowAnalysisStatus.ResourceLimit
            );
        }

        if (!IsValidRadicand(value: parameters.D)) {
            return new RadicalShadowTowerAnalysis(
                certificate: null,
                detail: "d must be positive and nonsquare",
                law: null,
                parameters: parameters,
                status: RadicalShadowAnalysisStatus.Invalid
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        var law = Derive(parameters: parameters);

        if (law.Kappa.Sign == 0) {
            if (law.AffineCenterAt(index: BigInteger.One).Sign <= 0) {
                return new RadicalShadowTowerAnalysis(
                    certificate: null,
                    detail: "the exact affine solution is not positive at n=1",
                    law: law,
                    parameters: parameters,
                    status: RadicalShadowAnalysisStatus.Invalid
                );
            }

            var certificate = new RadicalShadowTowerCertificate(
                parameters: parameters,
                proofKind: RadicalShadowProofKind.ExactAffine
            );

            return new RadicalShadowTowerAnalysis(
                certificate: certificate,
                detail: "the affine center satisfies the recurrence identically",
                law: law,
                parameters: parameters,
                status: RadicalShadowAnalysisStatus.Total
            );
        }

        return new RadicalShadowTowerAnalysis(
            certificate: null,
            detail: "the nonzero residual requires the still-open global trap and channel-boundary proof",
            law: law,
            parameters: parameters,
            status: RadicalShadowAnalysisStatus.Undecided
        );
    }
}
/// <summary>Builds a total evaluator only after exact certificate verification succeeds.</summary>
public static class RadicalShadowTowerCompiler {
    /// <summary>Compiles a mathematical certificate into a raw correction program.</summary>
    /// <param name="certificate">The mathematical certificate.</param>
    /// <param name="limits">The deterministic limits, or <see langword="null"/> for <see cref="RadicalShadowAnalysisLimits.Default"/>.</param>
    /// <param name="cancellationToken">The cooperative cancellation token.</param>
    /// <returns>The raw compiled program, which must still pass <see cref="RadicalShadowTowerVerifier"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="certificate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The certificate does not prove a supported result.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static RadicalShadowTowerProgram Compile(
        RadicalShadowTowerCertificate certificate,
        RadicalShadowAnalysisLimits? limits = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!RadicalShadowTowerVerifier.TryVerifyMathematics(
            cancellationToken: cancellationToken,
            certificate: certificate,
            failure: out var failure,
            law: out _,
            limits: limits
        )) {
            throw new ArgumentException(
                message: $"the certificate did not verify: {failure}",
                paramName: nameof(certificate)
            );
        }

        var numeration = IntegerNumerationSystem.Positional();

        return new RadicalShadowTowerProgram(
            certificate: certificate,
            corrections: new AutomaticIntegerSequence(
                automaton: new DeterministicOutputAutomaton(
                    alphabetSize: numeration.AlphabetSize,
                    outputSymbols: [0],
                    transitions: [0, 0]
                ),
                numeration: numeration,
                outputAlphabet: [BigInteger.Zero]
            )
        );
    }
    /// <summary>Verifies a raw total program and creates its evaluator.</summary>
    /// <param name="program">The raw compiled program.</param>
    /// <param name="limits">The deterministic limits, or <see langword="null"/> for <see cref="RadicalShadowAnalysisLimits.Default"/>.</param>
    /// <param name="cancellationToken">The cooperative cancellation token.</param>
    /// <returns>The verified total evaluator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The program does not prove a supported all-index result.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static RadicalShadowTowerEvaluator CompileTotal(
        RadicalShadowTowerProgram program,
        RadicalShadowAnalysisLimits? limits = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(program);

        if (!RadicalShadowTowerVerifier.TryVerify(
            cancellationToken: cancellationToken,
            failure: out var failure,
            limits: limits,
            program: program,
            verified: out var verified
        )) {
            throw new ArgumentException(
                message: $"the program did not verify: {failure}",
                paramName: nameof(program)
            );
        }

        return new RadicalShadowTowerEvaluator(verified: verified!);
    }
}
/// <summary>Evaluates the certified floor and correction of a radical shadow tower at random-access indices.</summary>
public sealed class RadicalShadowTowerEvaluator {
    private readonly VerifiedRadicalShadowTower m_verified;

    internal RadicalShadowTowerEvaluator(VerifiedRadicalShadowTower verified) {
        m_verified = verified;
    }

    /// <summary>Gets the verified recurrence parameters.</summary>
    public RadicalShadowTowerParameters Parameters => m_verified.Parameters;

    /// <summary>Returns the certified correction to the affine floor at a positive arbitrary-width index.</summary>
    /// <param name="index">The positive tower index.</param>
    /// <returns>The arbitrary-width correction.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not positive.</exception>
    public BigInteger CorrectionAt(BigInteger index) {
        if (index <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(index),
                message: "a radical tower index must be positive"
            );
        }

        return m_verified.Program.Corrections.ValueAt(index: index);
    }
    /// <summary>Returns the certified correction at a positive unsigned 64-bit index.</summary>
    /// <param name="index">The positive tower index.</param>
    /// <returns>The arbitrary-width correction.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is zero.</exception>
    public BigInteger CorrectionAt(ulong index) {
        if (index == 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(index),
                message: "a radical tower index must be positive"
            );
        }

        return m_verified.Program.Corrections.ValueAt(index: index);
    }
    /// <summary>Returns the certified tower floor at a positive arbitrary-width index.</summary>
    /// <param name="index">The positive tower index.</param>
    /// <returns>The exact integer floor.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not positive.</exception>
    public BigInteger FloorAt(BigInteger index) => (
        m_verified.Law.AffineFloorAt(index: index) +
        CorrectionAt(index: index)
    );
    /// <summary>Returns the certified tower floor at a positive unsigned 64-bit index.</summary>
    /// <param name="index">The positive tower index.</param>
    /// <returns>The exact integer floor.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is zero.</exception>
    public BigInteger FloorAt(ulong index) {
        if (index == 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(index),
                message: "a radical tower index must be positive"
            );
        }

        return (
            m_verified.Law.AffineFloorAt(index: new BigInteger(value: index)) +
            CorrectionAt(index: index)
        );
    }
}
