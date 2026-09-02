using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

internal static class AutomaticSequenceClaims {
    public static string? NumerationAutomatonAndRadicalTowerAreExact() {
        var positional = IntegerNumerationSystem.Positional(radix: 2);
        var automaton = new DeterministicOutputAutomaton(
            alphabetSize: 2,
            outputSymbols: [0, 1, 1],
            startState: 0,
            transitions: [0, 1, 1, 0, 2, 2]
        );

        if (
            (positional.Kind != IntegerNumerationKind.Positional) ||
            (positional.Radix != 2) ||
            (positional.AlphabetSize != 2) ||
            (positional.Basis is not null)
        ) {
            return "the positional numeration descriptor did not retain its public contract";
        }
        if (
            (automaton.StateCount != 2) ||
            (automaton.StartState != 0) ||
            (automaton.AlphabetSize != 2) ||
            (automaton.Transition(digit: 1, state: 0) != 1) ||
            (automaton.OutputSymbol(state: 1) != 1) ||
            (automaton.Run(digits: [1, 0, 1]) != 0)
        ) {
            return "the DFAO did not normalize and execute the reachable state graph";
        }

        var parity = new AutomaticIntegerSequence(
            automaton: automaton,
            numeration: positional,
            outputAlphabet: [-BigInteger.One, BigInteger.One]
        );

        if (
            !parity.HasSignedUnitOutput ||
            (parity.Automaton != automaton) ||
            (parity.Numeration != positional) ||
            (parity.OutputAlphabetSize != 2) ||
            (parity.OutputValue(symbol: 0) != -BigInteger.One)
        ) {
            return "the automatic sequence did not retain its finite alphabets";
        }

        for (ulong index = 0; (index < 4096); ++index) {
            var digits = positional.Represent(value: index);
            var rebuilt = positional.Evaluate(digits: digits);
            var expectedSymbol = BitOperations.PopCount(value: index) & 1;

            if (rebuilt != index) {
                return $"positional representation failed to round-trip {index}";
            }
            if (
                (parity.OutputSymbolAt(index: index) != expectedSymbol) ||
                (parity.OutputSymbolAt(index: new BigInteger(value: index)) != expectedSymbol)
            ) {
                return $"the BigInteger and ulong DFAO paths disagreed with parity at {index}";
            }

            var expectedValue = ((expectedSymbol == 0) ? -BigInteger.One : BigInteger.One);

            if (
                (parity.ValueAt(index: index) != expectedValue) ||
                (parity.ValueAt(index: new BigInteger(value: index)) != expectedValue)
            ) {
                return $"the output-alphabet map disagreed with parity at {index}";
            }
        }

        var squareRootTwo = RealQuadratic.Create(
            denominator: 1,
            radicand: 2,
            rationalNumerator: 0,
            surdNumerator: 1
        );
        var ostrowski = IntegerNumerationSystem.QuadraticOstrowski(basis: squareRootTwo);

        if (
            (ostrowski.Kind != IntegerNumerationKind.QuadraticOstrowski) ||
            (ostrowski.Radix != 0) ||
            (ostrowski.AlphabetSize != 3) ||
            (ostrowski.Basis != squareRootTwo)
        ) {
            return "the quadratic Ostrowski descriptor did not retain sqrt(2)'s digit system";
        }

        var ostrowskiAutomaton = new DeterministicOutputAutomaton(
            alphabetSize: 3,
            outputSymbols: [0, 1],
            transitions: [0, 1, 0, 1, 0, 1]
        );
        var ostrowskiParity = new AutomaticIntegerSequence(
            automaton: ostrowskiAutomaton,
            numeration: ostrowski,
            outputAlphabet: [BigInteger.Zero, BigInteger.One]
        );
        var arbitraryWidth = BigInteger.Parse(value: "100000000000000000000000000000000000000000000000001");

        foreach (var index in new BigInteger[] { 0, 1, 2, 3, 55, 65_535, arbitraryWidth }) {
            var digits = ostrowski.Represent(value: index);

            if (ostrowski.Evaluate(digits: digits) != index) {
                return $"Ostrowski representation failed to round-trip {index}";
            }

            var expected = digits.Aggregate(
                func: (sum, digit) => ((sum + digit) & 1),
                seed: 0
            );

            if (ostrowskiParity.ValueAt(index: index) != expected) {
                return $"Ostrowski DFAO evaluation disagreed with its digit sum at {index}";
            }
            if (
                (index <= ulong.MaxValue) &&
                (ostrowskiParity.ValueAt(index: ((ulong)index)) != expected)
            ) {
                return $"the Ostrowski ulong path disagreed with its arbitrary-width path at {index}";
            }
        }

        var parameters = new RadicalShadowTowerParameters(
            D: 2,
            U: 4,
            V: 2,
            W: 0
        );
        var analysis = RadicalShadowTowerAnalyzer.Analyze(
            cancellationToken: TestContext.Current.CancellationToken,
            parameters: parameters
        );

        if (
            (analysis.Status != RadicalShadowAnalysisStatus.Total) ||
            (analysis.Parameters != parameters) ||
            string.IsNullOrWhiteSpace(value: analysis.Detail) ||
            (analysis.Certificate is null) ||
            (analysis.Law is null)
        ) {
            return "the exact-affine family did not produce a total certificate";
        }
        if (
            (analysis.Law.Slope != squareRootTwo) ||
            (analysis.Law.Center != squareRootTwo) ||
            (analysis.Law.Kappa.Sign != 0) ||
            (analysis.Law.ActivationThreshold.Sign != 0) ||
            (analysis.Law.ChannelNormModulus != 8) ||
            (analysis.Law.ChannelNormResidue != 0) ||
            (analysis.Law.AffineCenterAt(index: 3) != (RealQuadratic.Rational(value: 4) * squareRootTwo)) ||
            (analysis.Law.AffineFloorAt(index: 3) != 5)
        ) {
            return "the derived exact affine law is wrong for d=2,u=4,v=2,w=0";
        }

        var program = RadicalShadowTowerCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            certificate: analysis.Certificate,
            limits: null
        );

        if (
            (program.Certificate != analysis.Certificate) ||
            !program.Corrections.ValueAt(index: BigInteger.One).IsZero
        ) {
            return "the radical compiler did not preserve the mathematical certificate and zero correction";
        }
        if (!RadicalShadowTowerVerifier.TryVerify(
            cancellationToken: TestContext.Current.CancellationToken,
            failure: out var verificationFailure,
            program: program,
            verified: out var verified
        ) ||
            (verificationFailure != RadicalShadowVerificationFailure.None) ||
            (verified is null) ||
            (verified.Parameters != parameters) ||
            (verified.Law.Kappa.Sign != 0)
        ) {
            return $"the analyzer's certificate did not verify: {verificationFailure}";
        }

        var evaluator = RadicalShadowTowerCompiler.CompileTotal(
            cancellationToken: TestContext.Current.CancellationToken,
            program: program
        );

        if (evaluator.Parameters != parameters) {
            return "the evaluator lost its verified recurrence parameters";
        }
        for (ulong index = 1; (index <= 256); ++index) {
            var expected = IndependentSquareRootFloor(value: (2 * BigInteger.Pow(
                exponent: 2,
                value: (index + 1)
            )));

            if (
                !evaluator.CorrectionAt(index: index).IsZero ||
                !evaluator.CorrectionAt(index: new BigInteger(value: index)).IsZero ||
                (evaluator.FloorAt(index: index) != expected) ||
                (evaluator.FloorAt(index: new BigInteger(value: index)) != expected)
            ) {
                return $"the certified radical evaluator failed at n={index}";
            }
        }

        var undecided = RadicalShadowTowerAnalyzer.Analyze(
            cancellationToken: TestContext.Current.CancellationToken,
            parameters: new RadicalShadowTowerParameters(D: 2, U: 0, V: 0, W: 1)
        );

        if (
            (undecided.Status != RadicalShadowAnalysisStatus.Undecided) ||
            (undecided.Certificate is not null) ||
            (undecided.Law is null) ||
            (undecided.Law.Kappa.Sign == 0)
        ) {
            return "the open nonzero-residual family was not kept explicitly undecided";
        }

        var invalid = RadicalShadowTowerAnalyzer.Analyze(
            cancellationToken: TestContext.Current.CancellationToken,
            parameters: new RadicalShadowTowerParameters(D: 4, U: 0, V: 0, W: 0)
        );

        if (invalid.Status != RadicalShadowAnalysisStatus.Invalid) {
            return "a square radicand did not produce Invalid";
        }

        var limited = RadicalShadowTowerAnalyzer.Analyze(
            cancellationToken: TestContext.Current.CancellationToken,
            limits: new RadicalShadowAnalysisLimits(
                maximumAutomatonStates: 1,
                maximumParameterBitLength: 1,
                maximumPellRepresentatives: 1
            ),
            parameters: parameters
        );

        if (
            (limited.Status != RadicalShadowAnalysisStatus.ResourceLimit) ||
            (RadicalShadowAnalysisLimits.Default.MaximumAutomatonStates <= 0) ||
            (RadicalShadowAnalysisLimits.Default.MaximumParameterBitLength <= 0) ||
            (RadicalShadowAnalysisLimits.Default.MaximumPellRepresentatives <= 0)
        ) {
            return "deterministic analysis limits did not remain distinct from undecided";
        }
        if (RadicalShadowTowerVerifier.TryVerify(
            cancellationToken: TestContext.Current.CancellationToken,
            failure: out verificationFailure,
            limits: new RadicalShadowAnalysisLimits(
                maximumAutomatonStates: 1,
                maximumParameterBitLength: 1,
                maximumPellRepresentatives: 1
            ),
            program: program,
            verified: out _
        ) || (verificationFailure != RadicalShadowVerificationFailure.ResourceLimit)) {
            return "certificate verification did not enforce the same deterministic bit ceiling";
        }

        var wrongCorrections = new AutomaticIntegerSequence(
            automaton: new DeterministicOutputAutomaton(
                alphabetSize: 2,
                outputSymbols: [0],
                transitions: [0, 0]
            ),
            numeration: positional,
            outputAlphabet: [BigInteger.One]
        );
        var tampered = new RadicalShadowTowerCertificate(
            parameters: parameters,
            proofKind: RadicalShadowProofKind.ExactAffine
        );

        if (RadicalShadowTowerVerifier.TryVerify(
            cancellationToken: TestContext.Current.CancellationToken,
            failure: out verificationFailure,
            program: new RadicalShadowTowerProgram(
                certificate: tampered,
                corrections: wrongCorrections
            ),
            verified: out _
        ) || (verificationFailure != RadicalShadowVerificationFailure.CorrectionDoesNotMatchProof)) {
            return "the verifier accepted a nonzero correction under the exact-affine proof";
        }

        if (
            (analysis.Certificate.Parameters != parameters) ||
            (analysis.Certificate.ProofKind != RadicalShadowProofKind.ExactAffine)
        ) {
            return "the raw certificate did not retain its public fields";
        }

        return null;
    }

    private static BigInteger IndependentSquareRootFloor(BigInteger value) {
        var low = BigInteger.Zero;
        var high = BigInteger.One;

        while ((high * high) <= value) { high <<= 1; }
        while ((low + 1) < high) {
            var middle = ((low + high) >> 1);

            if ((middle * middle) <= value) {
                low = middle;
            } else {
                high = middle;
            }
        }

        return low;
    }
}
