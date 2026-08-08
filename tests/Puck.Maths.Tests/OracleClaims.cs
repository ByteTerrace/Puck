using System.Globalization;
using System.Numerics;

namespace Puck.Maths.Tests;

internal static class OracleClaims {
    internal static string? ComplementAdmissionRequiresMutualInverses() {
        var field = PrimeFieldMaterial.Create(modulus: 5UL);
        Generator[] generators = [
            new(symbol: 0, inputs: ReadOnlyMemory<int>.Empty, outputs: ReadOnlyMemory<int>.Empty, degree: 1),
            new(symbol: 1, inputs: ReadOnlyMemory<int>.Empty, outputs: ReadOnlyMemory<int>.Empty, degree: 1),
        ];
        RewriteRule<ulong>[] rules = [
            new(
                kind: RuleKind.Reassociate,
                pattern: ReadOnlyMemory<int>.Empty,
                replacement: ReadOnlyMemory<int>.Empty,
                charges: new ulong[] { 1UL }
            ),
            new(
                kind: RuleKind.Annihilate,
                pattern: new int[] { 0, 0 },
                replacement: ReadOnlyMemory<int>.Empty,
                charges: ReadOnlyMemory<ulong>.Empty
            ),
            new(
                kind: RuleKind.Annihilate,
                pattern: new int[] { 1, 1 },
                replacement: ReadOnlyMemory<int>.Empty,
                charges: ReadOnlyMemory<ulong>.Empty
            ),
            new(
                kind: RuleKind.Swap,
                pattern: new int[] { 1, 0 },
                replacement: RewriteRule<ulong>.PackReplacement([0, 1]),
                charges: new ulong[] { 2UL }
            ),
        ];
        var charged = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(
            presentation: ChargedPresentation<ulong, PrimeFieldMaterial>.Create(
                generators: generators,
                rules: rules,
                material: field
            )
        );

        try {
            _ = GradedComplement<ulong, PrimeFieldMaterial>.Create(algebra: charged);

            return "the GF(5) charge e1 e0 -> 2 e0 e1 admitted complements whose composition scales e1 by four";
        } catch (ArgumentException exception) when ("algebra" == exception.ParamName) {
            if (!exception.Message.Contains(value: "basis key", comparisonType: StringComparison.Ordinal)
                || !exception.Message.Contains(value: "rather than", comparisonType: StringComparison.Ordinal)) {
                return $"the GF(5) refusal did not publish its basis witness: {exception.Message}";
            }
        }

        var rounded = PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(
            presentation: Presentations.Clifford<FixedQ4816, FixedMaterial>(
                positiveCount: 2,
                negativeCount: 0,
                degenerateCount: 0,
                material: default
            )
        );

        try {
            _ = GradedComplement<FixedQ4816, FixedMaterial>.Create(algebra: rounded);

            return "the rounded FixedMaterial admitted a basis-key proof that cannot extend to arbitrary coefficients";
        } catch (ArgumentException exception) when ("algebra" == exception.ParamName) {
            if (!exception.Message.Contains(value: "exact semiring", comparisonType: StringComparison.Ordinal)) {
                return $"the rounded-material refusal did not name its missing exact-semiring licence: {exception.Message}";
            }
        }

        return ComplementCliffordSignatures(maximumGeneratorCount: 4);
    }

    internal static string? ComplementCliffordSignaturesDeep() =>
        ComplementCliffordSignatures(maximumGeneratorCount: 8);

    internal static string? MatcherRejectsDifferentAlphabetIdentity() {
        var finite = FiniteTokenAlphabet.Create(tokens: [10UL, 20UL]);
        var ten = finite.Predicate(tokens: [10UL]);
        var twenty = finite.Predicate(tokens: [20UL]);
        var first = MintermAlphabet<ulong, FiniteTokenAlphabet>.Create(refinement: finite, predicates: [ten]);
        var swapped = MintermAlphabet<ulong, FiniteTokenAlphabet>.Create(refinement: finite, predicates: [twenty]);
        var pattern = TokenPattern<bool, BooleanMaterial>.Create(letterCount: first.LetterCount, window: 1, material: default);
        var value = pattern.Predicate(letters: 1UL);

        if (!PatternMatcher<bool, BooleanMaterial>.TryCompile(
            pattern: pattern,
            value: value,
            alphabet: first,
            stateLimit: PresentedAlgebra<bool, BooleanMaterial>.MaximumClosureStates,
            matcher: out var matcher,
            obstruction: out _
        )) {
            return "the finite-alphabet matcher did not compile";
        }

        if (!TokenMatching.TryMatch(matcher: matcher, alphabet: first, tokens: [10UL], weight: out var accepted, obstruction: out _)
            || !accepted) {
            return "the matcher rejected token 10 through the alphabet that assigned letter zero to it";
        }

        try {
            _ = TokenMatching.TryMatch(matcher: matcher, alphabet: swapped, tokens: [20UL], weight: out _, obstruction: out _);

            return "a same-sized swapped two-token partition silently changed letter zero from token 10 to token 20";
        } catch (ArgumentException exception) when ("alphabet" == exception.ParamName) {
        }

        var ranges = default(TokenRangeAlphabet);
        var low = TokenRangeSet.Create(ranges: [new TokenRange(First: 0UL, Last: 99UL)]);
        var high = TokenRangeSet.Create(ranges: [new TokenRange(First: 100UL, Last: 199UL)]);
        var lowFirst = MintermAlphabet<TokenRangeSet, TokenRangeAlphabet>.Create(refinement: ranges, predicates: [low, high]);
        var highFirst = MintermAlphabet<TokenRangeSet, TokenRangeAlphabet>.Create(refinement: ranges, predicates: [high, low]);

        if (lowFirst.LetterCount != highFirst.LetterCount) {
            return $"the range controls do not have equal partition sizes ({lowFirst.LetterCount} and {highFirst.LetterCount})";
        }

        var rangePattern = TokenPattern<bool, BooleanMaterial>.Create(letterCount: lowFirst.LetterCount, window: 1, material: default);
        var lowValue = rangePattern.Predicate(letters: 1UL);

        if (!PatternMatcher<bool, BooleanMaterial>.TryCompile(
            pattern: rangePattern,
            value: lowValue,
            alphabet: lowFirst,
            stateLimit: PresentedAlgebra<bool, BooleanMaterial>.MaximumClosureStates,
            matcher: out var rangeMatcher,
            obstruction: out _
        )) {
            return "the range-alphabet matcher did not compile";
        }

        if (!TokenMatching.TryMatch(matcher: rangeMatcher, alphabet: lowFirst, tokens: [50UL], weight: out var lowAccepted, obstruction: out _)
            || !lowAccepted) {
            return "the range matcher rejected the low block through its bound alphabet";
        }

        try {
            _ = TokenMatching.TryMatch(matcher: rangeMatcher, alphabet: highFirst, tokens: [150UL], weight: out _, obstruction: out _);

            return "an equal-sized range partition with different block ordering silently changed the matcher's letters";
        } catch (ArgumentException exception) when ("alphabet" == exception.ParamName) {
        }

        return null;
    }

    internal static string? FiniteBasisCapacityIsTyped() {
        var presentation = Presentations.FreeMonoid<BigInteger, IntegerMaterial>(
            letterCount: 2,
            windowDegree: 9,
            material: default
        );
        var status = presentation.BasisStatus;

        if (!presentation.HasFiniteNormalForms || !status.IsKnownFinite || (NormalFormBoundedness.DeclaredFinite != status.Boundedness)) {
            return $"the 1,023-word window was not recognized as mathematically finite ({status})";
        }

        if (presentation.HasCompiledNormalFormBasis || (0 != presentation.NormalFormCount)) {
            return $"the 1,023-word window claims a compiled basis of {presentation.NormalFormCount} form(s)";
        }

        if ((NormalFormBasisOutcome.CapacityObstructed != status.Outcome)
            || (NormalFormBasisStage.Discovery != status.Stage)
            || (512L != status.ConfiguredBound)
            || (512L != status.AmountReached)) {
            return $"the 1,023-word window reported {status}";
        }

        var compiled = Presentations.FreeMonoid<BigInteger, IntegerMaterial>(
            letterCount: 2,
            windowDegree: 4,
            material: default
        );

        if (!compiled.HasFiniteNormalForms
            || !compiled.HasCompiledNormalFormBasis
            || (31 != compiled.NormalFormCount)
            || (NormalFormBasisOutcome.Compiled != compiled.BasisStatus.Outcome)
            || (NormalFormBasisStage.Complete != compiled.BasisStatus.Stage)) {
            return $"the 31-word positive control reported {compiled.BasisStatus} with {compiled.NormalFormCount} compiled form(s)";
        }

        var free = Presentations.FreeMonoid<BigInteger, IntegerMaterial>(
            letterCount: 2,
            windowDegree: 0,
            material: default
        );

        if (free.HasFiniteNormalForms
            || free.HasCompiledNormalFormBasis
            || free.BasisStatus.IsKnownFinite
            || (NormalFormBasisOutcome.CapacityObstructed != free.BasisStatus.Outcome)) {
            return $"the unwindowed free monoid inferred finiteness from a stopped search ({free.BasisStatus})";
        }

        // Discovery fits (201 words), but the longest product is 400 symbols and exceeds the product-table word
        // capacity. The late obstruction must discard dense keys without discarding the packed-key unit.
        var lateObstruction = Presentations.FreeMonoid<BigInteger, IntegerMaterial>(
            letterCount: 1,
            windowDegree: 200,
            material: default
        );
        var lateAlgebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: lateObstruction);
        var lateIdentity = lateAlgebra.Identity;

        if (!lateObstruction.HasFiniteNormalForms
            || lateObstruction.HasCompiledNormalFormBasis
            || (NormalFormBasisOutcome.CapacityObstructed != lateObstruction.BasisStatus.Outcome)
            || (NormalFormBasisStage.ProductTable != lateObstruction.BasisStatus.Stage)
            || (256L != lateObstruction.BasisStatus.ConfiguredBound)
            || (257L != lateObstruction.BasisStatus.AmountReached)
            || (1 != lateIdentity.SupportCount)
            || (0L != lateIdentity.Keys[0])
            || (BigInteger.One != lateIdentity.Coefficients[0])) {
            return $"the late product-table obstruction lost its packed-key identity ({lateObstruction.BasisStatus}, support {lateIdentity.SupportCount})";
        }

        var loopingRule = new RewriteRule<BigInteger>(
            kind: RuleKind.Reduce,
            pattern: new int[] { 0 },
            replacement: RewriteRule<BigInteger>.PackReplacement([0]),
            charges: new BigInteger[] { BigInteger.One }
        );
        var exhausted = ChargedPresentation<BigInteger, IntegerMaterial>.Create(
            generators: [new Generator(symbol: 0, inputs: ReadOnlyMemory<int>.Empty, outputs: ReadOnlyMemory<int>.Empty, degree: 1)],
            rules: [loopingRule],
            material: default
        );

        if ((NormalFormBasisOutcome.NormalizationExhausted != exhausted.BasisStatus.Outcome)
            || (NormalFormBasisStage.Discovery != exhausted.BasisStatus.Stage)
            || ((1L << 20) != exhausted.BasisStatus.ConfiguredBound)
            || ((1L << 20) != exhausted.BasisStatus.AmountReached)
            || exhausted.HasFiniteNormalForms
            || exhausted.HasCompiledNormalFormBasis) {
            return $"the looping normalizer reported {exhausted.BasisStatus}";
        }

        return null;
    }

    internal static string? NonChainHomologyRefuses() {
        int[] dimensions = [0, 1, 2];
        (int Face, int Coface, int Sign)[] incidences = [(0, 1, +1), (1, 2, +1)];

        try {
            _ = FieldHomology<QuadraticSurd, RationalMaterial>.Create(
                calculus: ExteriorCalculus<QuadraticSurd, RationalMaterial>.Create(
                    dimensions: dimensions,
                    incidences: incidences,
                    material: default
                )
            );

            return "field homology admitted incidence data with boundary_1 boundary_2 nonzero";
        } catch (ChainComplexException<QuadraticSurd> exception) {
            var witness = exception.Obstruction;

            if ((1 != witness.Degree) || (0 != witness.RowCell) || (2 != witness.ColumnCell) || (QuadraticSurd.One != witness.CompositeCoefficient)) {
                return $"field refusal witnessed degree={witness.Degree}, row={witness.RowCell}, column={witness.ColumnCell}, coefficient={witness.CompositeCoefficient}";
            }
        }

        try {
            _ = IntegerHomology.TryCompute(
                calculus: ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                    dimensions: dimensions,
                    incidences: incidences,
                    material: default
                ),
                magnitudeBits: 64,
                homology: out _,
                obstruction: out _
            );

            return "integer homology admitted incidence data with boundary_1 boundary_2 nonzero";
        } catch (ChainComplexException<BigInteger> exception) {
            var witness = exception.Obstruction;

            if ((1 != witness.Degree) || (0 != witness.RowCell) || (2 != witness.ColumnCell) || (BigInteger.One != witness.CompositeCoefficient)) {
                return $"integer refusal witnessed degree={witness.Degree}, row={witness.RowCell}, column={witness.ColumnCell}, coefficient={witness.CompositeCoefficient}";
            }
        }

        // A filled oriented triangle is the adjacent-boundary positive control: every edge boundary cancels in the
        // boundary of the face, and both coefficient paths must publish nonnegative [1,0,0].
        int[] validDimensions = [0, 0, 0, 1, 1, 1, 2];
        (int Face, int Coface, int Sign)[] validIncidences = [
            (0, 3, -1), (1, 3, +1),
            (1, 4, -1), (2, 4, +1),
            (0, 5, -1), (2, 5, +1),
            (3, 6, +1), (4, 6, +1), (5, 6, -1),
        ];

        var field = FieldHomology<QuadraticSurd, RationalMaterial>.Create(
            calculus: ExteriorCalculus<QuadraticSurd, RationalMaterial>.Create(
                dimensions: validDimensions,
                incidences: validIncidences,
                material: default
            )
        );

        if ((1 != field.BettiNumber(degree: 0)) || (0 != field.BettiNumber(degree: 1)) || (0 != field.BettiNumber(degree: 2))) {
            return $"the valid field control returned [{field.BettiNumber(degree: 0)},{field.BettiNumber(degree: 1)},{field.BettiNumber(degree: 2)}]";
        }

        if (!IntegerHomology.TryCompute(
            calculus: ExteriorCalculus<BigInteger, IntegerMaterial>.Create(
                dimensions: validDimensions,
                incidences: validIncidences,
                material: default
            ),
            magnitudeBits: 64,
            homology: out var integer,
            obstruction: out var reduction
        )) {
            return $"the valid integer control refused its Smith reduction at stage {reduction.Stage}";
        }

        if ((1 != integer.BettiNumber(degree: 0)) || (0 != integer.BettiNumber(degree: 1)) || (0 != integer.BettiNumber(degree: 2))) {
            return $"the valid integer control returned [{integer.BettiNumber(degree: 0)},{integer.BettiNumber(degree: 1)},{integer.BettiNumber(degree: 2)}]";
        }

        return null;
    }

    internal static string? PresentedGroupRequiresAssociativity() {
        var octonions = PresentedAlgebra<QuadraticSurd, RationalMaterial>.Create(
            presentation: Presentations.CayleyDickson<QuadraticSurd, RationalMaterial>(
                floors: 3,
                basisRelabelling: [],
                material: default
            )
        );

        if (PresentedGroup<QuadraticSurd, RationalMaterial>.TryCertify(
            algebra: octonions,
            group: out _,
            obstruction: out var obstruction
        )) {
            return "the octonion basis certified as a group from generator inverses alone";
        }

        if ((ClosureOutcome.BasisNonAssociativityDetected != obstruction.Outcome)
            || (obstruction.AssociatorLeftKey < 0)
            || (obstruction.AssociatorMiddleKey < 0)
            || (obstruction.AssociatorRightKey < 0)) {
            return $"the octonion refusal reported {obstruction.Outcome} and triple ({obstruction.AssociatorLeftKey},{obstruction.AssociatorMiddleKey},{obstruction.AssociatorRightKey})";
        }

        var one = octonions.Presentation.Material.One;
        var left = octonions.FromSupport(keys: [obstruction.AssociatorLeftKey], coefficients: [one]);
        var middle = octonions.FromSupport(keys: [obstruction.AssociatorMiddleKey], coefficients: [one]);
        var right = octonions.FromSupport(keys: [obstruction.AssociatorRightKey], coefficients: [one]);
        var before = octonions.Multiply(left: octonions.Multiply(left: left, right: middle), right: right);
        var after = octonions.Multiply(left: left, right: octonions.Multiply(left: middle, right: right));

        if (octonions.AreEqual(left: before, right: after)) {
            return "the octonion obstruction's recorded basis triple actually associates";
        }

        var infinite = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(
            presentation: Presentations.Coxeter<BigInteger, IntegerMaterial>(
                rank: 2,
                bonds: [1, 0, 0, 1],
                material: default
            )
        );

        if (PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(algebra: infinite, group: out _, obstruction: out var unverified)
            || (ClosureOutcome.SearchLimitReached != unverified.Outcome)) {
            return $"an infinite-basis Coxeter product was admitted without an associativity certificate, or refused as {unverified.Outcome}";
        }

        // A finite associative basis does not by itself extend to an associative bilinear algebra when its material
        // explicitly declines the exact-semiring laws. The group regime therefore refuses before treating its basis
        // triple check as a proof about products carrying rounded coefficients.
        var rounded = PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(
            presentation: Presentations.Clifford<FixedQ4816, FixedMaterial>(
                positiveCount: 1,
                negativeCount: 0,
                degenerateCount: 0,
                material: default
            )
        );

        if (PresentedGroup<FixedQ4816, FixedMaterial>.TryCertify(
            algebra: rounded,
            group: out _,
            obstruction: out var roundedObstruction
        ) || (ClosureOutcome.AmbiguityWitness != roundedObstruction.Outcome)
            || (-1 != roundedObstruction.BlockedSymbol)
            || (-1L != roundedObstruction.BlockedKey)
            || (0L != roundedObstruction.PointsReached)) {
            return $"a rounded material certified a group regime, or refused as {roundedObstruction}";
        }

        // Two independent associative controls: the finite Coxeter word presentation of D3 and the explicit
        // two-permutation table. Both must still certify after associativity became an admission condition.
        var coxeter = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(
            presentation: Presentations.Coxeter<BigInteger, IntegerMaterial>(
                rank: 2,
                bonds: [1, 3, 3, 1],
                material: default
            )
        );

        if (!PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(algebra: coxeter, group: out var coxeterGroup, obstruction: out var coxeterRefusal)) {
            return $"the associative D3 Coxeter control refused as {coxeterRefusal.Outcome}";
        }

        foreach (var badSeed in new[] { -1L, (long)coxeter.Presentation.NormalFormCount }) {
            try {
                _ = coxeterGroup.TryEnumerateOrbit(
                    seedKey: badSeed,
                    searchLimit: 1L,
                    orbit: out _,
                    obstruction: out _
                );

                return $"the invalid group-orbit seed {badSeed} was admitted";
            } catch (ArgumentOutOfRangeException exception) when ("seedKey" == exception.ParamName) {
                // The public API names its own seed instead of leaking PresentedAlgebra.FromSupport's `keys` detail.
            }
        }

        var permutation = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(
            presentation: Presentations.PermutationGroup<BigInteger, IntegerMaterial>(
                pointCount: 2,
                permutations: [0, 1, 1, 0],
                material: default
            )
        );

        if (!PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(algebra: permutation, group: out _, obstruction: out var permutationRefusal)) {
            return $"the associative permutation control refused as {permutationRefusal.Outcome}";
        }

        return null;
    }

    // The counting semiring's carrier is the naturals, so a negative coefficient is not a count at all. It used to be
    // admitted and to square to one, which reads as a walk count and is not one.
    internal static string? CountingMaterialAdmitsOnlyNaturals() {
        IMaterialOps<BigInteger, CountingMaterial> material = default(CountingMaterial);
        var algebra = PresentedAlgebra<BigInteger, CountingMaterial>.Create(
            presentation: Presentations.FreeMonoid<BigInteger, CountingMaterial>(letterCount: 2, material: default, windowDegree: 2)
        );

        foreach (var negative in new BigInteger[] { BigInteger.MinusOne, -2, BigInteger.Pow(value: 10, exponent: 40) * BigInteger.MinusOne }) {
            var direct = Refusal<ArgumentOutOfRangeException>(action: () => material.Canonicalize(value: negative));

            if (direct is null) { return $"the counting material canonicalized the negative count {negative}"; }

            if ("value" != direct.ParamName) { return $"the counting refusal of {negative} named '{direct.ParamName}' rather than 'value'"; }

            if (!direct.Message.Contains(value: negative.ToString(provider: CultureInfo.InvariantCulture), comparisonType: StringComparison.Ordinal)
                || !direct.Message.Contains(value: "natural number", comparisonType: StringComparison.Ordinal)) {
                return $"the counting refusal of {negative} named neither the value nor the rule: {direct.Message}";
            }

            var admission = Refusal<ArgumentOutOfRangeException>(action: () => _ = algebra.FromSupport(keys: [1L], coefficients: [negative]));

            if (admission is null) { return $"element admission accepted the negative count {negative}"; }
        }

        // The naturals themselves are untouched, identities included, and a walk count still multiplies as a count.
        var counting = default(CountingMaterial);
        var two = algebra.FromSupport(keys: [1L], coefficients: [(BigInteger)2]);
        var three = algebra.FromSupport(keys: [2L], coefficients: [(BigInteger)3]);
        var product = algebra.Multiply(left: two, right: three);

        if ((1 != product.SupportCount) || (6 != product.Coefficients[0])) {
            return $"the counting product of two ways by three ways carries {product.SupportCount} term(s) reading {(0 == product.SupportCount ? "nothing" : product.Coefficients[0].ToString(provider: CultureInfo.InvariantCulture))} rather than one term reading six";
        }

        if ((!counting.Zero.IsZero) || (!counting.One.IsOne)
            || (BigInteger.Zero != material.Canonicalize(value: counting.Zero))
            || (BigInteger.One != material.Canonicalize(value: counting.One))) {
            return "the counting material's own identities did not survive admission";
        }

        return null;
    }

    // A colour index numbers a boundary wire and ColourCount is one past the largest one mentioned, so an index outside
    // the range that arithmetic is honest over must not reach it. Both -1 and int.MaxValue used to be admitted, each
    // leaving ColourCount at zero for a presentation whose generator still mentioned the colour.
    internal static string? GeneratorColoursAreBoundedIndices() {
        const int ColourCap = 4096;

        foreach (var colour in new[] { -1, int.MinValue, ColourCap, int.MaxValue }) {
            foreach (var onInput in new[] { true, false }) {
                var boundary = (onInput ? "input" : "output");
                var wires = new int[] { colour };
                var refusal = Refusal<ArgumentOutOfRangeException>(action: () => _ = ChargedPresentation<BigInteger, IntegerMaterial>.Create(
                    generators: [new(
                        symbol: 0,
                        inputs: (onInput ? wires : ReadOnlyMemory<int>.Empty),
                        outputs: (onInput ? ReadOnlyMemory<int>.Empty : wires),
                        degree: 1
                    )],
                    rules: [],
                    material: default,
                    windowDegree: 1
                ));

                if (refusal is null) { return $"presentation admission accepted the {boundary} colour {colour}"; }

                if ("generators" != refusal.ParamName) { return $"the colour refusal of {colour} named '{refusal.ParamName}' rather than 'generators'"; }

                if (!refusal.Message.Contains(value: colour.ToString(provider: CultureInfo.InvariantCulture), comparisonType: StringComparison.Ordinal)
                    || !refusal.Message.Contains(value: boundary, comparisonType: StringComparison.Ordinal)) {
                    return $"the colour refusal of {colour} named neither the value nor the {boundary} boundary it sat on: {refusal.Message}";
                }
            }
        }

        // The legal range is admitted whole, and the count is one past the largest index mentioned rather than a count
        // of the wires: two generators mentioning colours 0 and 2 carry three colours.
        var wide = ChargedPresentation<BigInteger, IntegerMaterial>.Create(
            generators: [
                new(symbol: 0, inputs: new int[] { 0 }, outputs: new int[] { 2 }, degree: 1),
                new(symbol: 1, inputs: new int[] { (ColourCap - 1) }, outputs: ReadOnlyMemory<int>.Empty, degree: 1),
            ],
            rules: [],
            material: default,
            windowDegree: 1
        );

        if (ColourCap != wide.ColourCount) { return $"the largest admitted colour {(ColourCap - 1)} produced a colour count of {wide.ColourCount} rather than {ColourCap}"; }

        var narrow = ChargedPresentation<BigInteger, IntegerMaterial>.Create(
            generators: [new(symbol: 0, inputs: new int[] { 0 }, outputs: new int[] { 2 }, degree: 1)],
            rules: [],
            material: default,
            windowDegree: 1
        );

        if (3 != narrow.ColourCount) { return $"colours 0 and 2 produced a colour count of {narrow.ColourCount} rather than three"; }

        var colourless = ChargedPresentation<BigInteger, IntegerMaterial>.Create(
            generators: [new(symbol: 0, inputs: ReadOnlyMemory<int>.Empty, outputs: ReadOnlyMemory<int>.Empty, degree: 1)],
            rules: [],
            material: default,
            windowDegree: 1
        );

        if (0 != colourless.ColourCount) { return $"a generator with no boundary wires produced a colour count of {colourless.ColourCount} rather than zero"; }

        return null;
    }

    // A letter is the smallest thing a mask can name, so a predicate that cuts one in half is not nameable. The mask
    // used to be built from intersection alone, which handed back a letter accepting tokens the predicate rejects.
    internal static string? LetterMaskRefusesASplitBlock() {
        var refinement = FiniteTokenAlphabet.Create(tokens: [1UL, 2UL, 3UL]);
        var tokenOne = refinement.Predicate(tokens: [1UL]);
        var oneOrTwo = refinement.Predicate(tokens: [1UL, 2UL]);
        var coarse = MintermAlphabet<ulong, FiniteTokenAlphabet>.Create(refinement: refinement, predicates: [refinement.Full]);

        if (1 != coarse.LetterCount) { return $"refining three tokens against the full predicate produced {coarse.LetterCount} letters rather than one"; }

        foreach (var splitting in new[] { tokenOne, oneOrTwo }) {
            var refusal = Refusal<ArgumentException>(action: () => _ = coarse.LettersOf(predicate: splitting));

            if (refusal is null) {
                return $"the single letter {coarse.Minterm(letter: 0)} was returned for the predicate {splitting}, which splits it";
            }

            if ("predicate" != refusal.ParamName) { return $"the split refusal named '{refusal.ParamName}' rather than 'predicate'"; }

            if (!refusal.Message.Contains(value: "Letter 0", comparisonType: StringComparison.Ordinal)
                || !refusal.Message.Contains(value: "split", comparisonType: StringComparison.Ordinal)) {
                return $"the split refusal named neither the letter nor the rule: {refusal.Message}";
            }
        }

        // A predicate that is a union of whole letters is still answered exactly, and the block of tokens satisfying no
        // listed predicate is one of those letters.
        var refined = MintermAlphabet<ulong, FiniteTokenAlphabet>.Create(refinement: refinement, predicates: [tokenOne, oneOrTwo]);

        if (3 != refined.LetterCount) { return $"refining against the token-1 and token-{{1,2}} predicates produced {refined.LetterCount} letters rather than three"; }

        var outside = refinement.Complement(predicate: oneOrTwo);

        foreach (var (predicate, name) in new[] { (tokenOne, "token 1"), (oneOrTwo, "tokens 1 and 2"), (outside, "token 3"), (refinement.Full, "every token") }) {
            var mask = refined.LettersOf(predicate: predicate);

            if (0UL == mask) { return $"the predicate accepting {name} named no letter at all"; }

            for (var letter = 0; (letter < refined.LetterCount); ++letter) {
                var named = (0UL != (mask & (1UL << letter)));

                foreach (var token in new[] { 1UL, 2UL, 3UL }) {
                    if (!refinement.Contains(predicate: refined.Minterm(letter: letter), token: token)) { continue; }

                    if (named != refinement.Contains(predicate: predicate, token: token)) {
                        return $"the mask for {name} {(named ? "named" : "omitted")} letter {letter}, which carries token {token} the predicate {(named ? "rejects" : "accepts")}";
                    }
                }
            }
        }

        return null;
    }

    // The material advertises the exact rational FIELD. The surd carrier also represents a + b·√d, and those values are
    // not one field between them: √2 and √3 were each admitted and their sum then had nowhere to live.
    internal static string? RationalMaterialAdmitsOnlyRationals() {
        IMaterialOps<QuadraticSurd, RationalMaterial> material = default(RationalMaterial);
        var algebra = PresentedAlgebra<QuadraticSurd, RationalMaterial>.Create(
            presentation: Presentations.FreeMonoid<QuadraticSurd, RationalMaterial>(letterCount: 2, material: default, windowDegree: 2)
        );
        var root2 = QuadraticSurd.Create(rationalNumerator: 0, surdNumerator: 1, radicand: 2, denominator: 1);
        var root3 = QuadraticSurd.Create(rationalNumerator: 0, surdNumerator: 1, radicand: 3, denominator: 1);
        var goldenRatio = QuadraticSurd.Create(rationalNumerator: 1, surdNumerator: 1, radicand: 5, denominator: 2);

        foreach (var irrational in new[] { root2, root3, goldenRatio }) {
            var direct = Refusal<ArgumentOutOfRangeException>(action: () => material.Canonicalize(value: irrational));

            if (direct is null) { return $"the rational material canonicalized the irrational {irrational}"; }

            if ("value" != direct.ParamName) { return $"the rational refusal of {irrational} named '{direct.ParamName}' rather than 'value'"; }

            if (!direct.Message.Contains(value: irrational.ToString(), comparisonType: StringComparison.Ordinal)
                || !direct.Message.Contains(value: $"√{irrational.Radicand}", comparisonType: StringComparison.Ordinal)) {
                return $"the rational refusal of {irrational} named neither the value nor the root that leaves the field: {direct.Message}";
            }

            var admission = Refusal<ArgumentOutOfRangeException>(action: () => _ = algebra.FromSupport(keys: [1L], coefficients: [irrational]));

            if (admission is null) { return $"element admission accepted the irrational coefficient {irrational}"; }
        }

        // The rationals stay a field: a product and a sum of admitted coefficients land back in the carrier, and the
        // reciprocal of a nonzero one exists.
        var third = QuadraticSurd.Rational(numerator: 1, denominator: 3);
        var half = QuadraticSurd.Rational(numerator: -1, denominator: 2);
        var left = algebra.FromSupport(keys: [1L], coefficients: [third]);
        var right = algebra.FromSupport(keys: [1L], coefficients: [half]);
        var sum = algebra.Add(left: left, right: right);
        var expected = QuadraticSurd.Rational(numerator: -1, denominator: 6);

        if ((1 != sum.SupportCount) || (expected != sum.Coefficients[0])) {
            return $"one third plus minus one half admitted as {(0 == sum.SupportCount ? "nothing" : sum.Coefficients[0].ToString())} rather than {expected}";
        }

        var rational = default(RationalMaterial);

        if (!rational.TryInvert(value: third, out var reciprocal) || (QuadraticSurd.Rational(value: 3) != reciprocal)) {
            return $"the reciprocal of one third came back as {reciprocal}";
        }

        if (rational.TryInvert(value: rational.Zero, out var zeroInverse) || (0 != zeroInverse.Sign)) {
            return "zero reported a reciprocal";
        }

        return null;
    }

    // Returns the refusal an action raised, or null when it raised none — so a claim can read the exception's parameter
    // name and message rather than merely observing that something was thrown.
    private static TException? Refusal<TException>(Action action)
        where TException : ArgumentException {
        try {
            action();
        } catch (TException exception) {
            return exception;
        }

        return null;
    }

    private static string? ComplementCliffordSignatures(int maximumGeneratorCount) {
        for (var generatorCount = 1; (generatorCount <= maximumGeneratorCount); ++generatorCount) {
            for (var positive = 0; (positive <= generatorCount); ++positive) {
                for (var negative = 0; (negative <= (generatorCount - positive)); ++negative) {
                    var degenerate = (generatorCount - positive - negative);
                    var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(
                        presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
                            positiveCount: positive,
                            negativeCount: negative,
                            degenerateCount: degenerate,
                            material: default
                        )
                    );
                    var complement = GradedComplement<BigInteger, IntegerMaterial>.Create(algebra: algebra);

                    for (var key = 0; (key < algebra.Presentation.NormalFormCount); ++key) {
                        var basis = algebra.FromSupport(keys: [key], coefficients: [BigInteger.One]);
                        var leftAfterRight = complement.LeftComplement(value: complement.RightComplement(value: basis));
                        var rightAfterLeft = complement.RightComplement(value: complement.LeftComplement(value: basis));

                        if (!algebra.AreEqual(left: basis, right: leftAfterRight)
                            || !algebra.AreEqual(left: basis, right: rightAfterLeft)) {
                            return $"Clifford({positive},{negative},{degenerate}) failed a complement composition at basis key {key}";
                        }
                    }
                }
            }
        }

        return null;
    }
}
