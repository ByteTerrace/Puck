using System.Numerics;

namespace Puck.Maths.Research;

/// <summary>
/// An exact binary incidence system with a free cyclic action of odd order. One column describes a complete orbit of
/// contexts: for each ray/object orbit, bit <c>p</c> records incidence at cyclic phase <c>p</c>. The class implements the
/// square-free Chinese-remainder decomposition of <c>GF(2)[t]/(t^n - 1)</c>, reducing a large expanded binary kernel to
/// small ranks over finite fields.
/// </summary>
/// <remarks>
/// This is geometry-neutral. Callers derive the orbit-polynomial table from a lattice, graph, schedule, puzzle, code, or
/// any other periodic incidence relation. <see cref="Analyze(ReadOnlySpan{int}, bool)"/> then decides parity validity,
/// computes exact nullity, and—when the selected number of complete orbits is odd—decides parity-proof irreducibility.
/// </remarks>
public sealed class OddCyclicIncidence {
    private readonly ulong[] m_columns;
    private readonly ulong[][] m_evaluatedColumns;
    private readonly BinaryPolynomial[] m_factorStorage;
    private readonly BinaryField<ulong>[] m_fields;
    private readonly BigInteger[] m_syndromes;

    /// <summary>
    /// Creates an incidence system and automatically factors <c>t^n + 1 = t^n - 1</c> over <c>GF(2)</c>.
    /// </summary>
    /// <param name="cycleOrder">The odd cyclic order; automatic factorization supports <c>[1, 31]</c>.</param>
    /// <param name="rayOrbitCount">The number of object/ray orbits.</param>
    /// <param name="letterCount">The number of context/basis orbits.</param>
    /// <param name="columns">
    /// The flattened letter-major table. Entry <c>letter * rayOrbitCount + rayOrbit</c> is a packed polynomial whose bit
    /// <c>p</c> records incidence at phase <c>p</c>.
    /// </param>
    public OddCyclicIncidence(int cycleOrder, int rayOrbitCount, int letterCount, ReadOnlySpan<ulong> columns)
        : this(
        cycleOrder: cycleOrder,
        rayOrbitCount: rayOrbitCount,
        letterCount: letterCount,
        columns: columns,
        factors: BinaryPolynomial.FactorOddCycle(cycleOrder: cycleOrder)
    ) { }
    /// <summary>
    /// Creates an incidence system from caller-supplied factors of <c>t^n + 1</c>. The factors are not trusted: the
    /// constructor checks irreducibility, pairwise coprimality, distinctness, and their exact product.
    /// </summary>
    /// <param name="cycleOrder">An odd cyclic order in <c>[1, 62]</c>.</param>
    /// <param name="rayOrbitCount">The number of object/ray orbits.</param>
    /// <param name="letterCount">The number of context/basis orbits.</param>
    /// <param name="columns">The flattened letter-major polynomial table.</param>
    /// <param name="factors">The proposed distinct monic irreducible factors of <c>t^n + 1</c>.</param>
    public OddCyclicIncidence(
        int cycleOrder,
        int rayOrbitCount,
        int letterCount,
        ReadOnlySpan<ulong> columns,
        ReadOnlySpan<BinaryPolynomial> factors
    ) {
        if (
            (cycleOrder <= 0) ||
            (cycleOrder >= 63) ||
            ((cycleOrder & 1) == 0)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(cycleOrder),
                actualValue: cycleOrder,
                message: "The cyclic order must be odd and in [1, 62]."
            );
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: rayOrbitCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: letterCount);
        if (columns.Length != checked((rayOrbitCount * letterCount))) {
            throw new ArgumentException(
                message: "The polynomial table length does not match rayOrbitCount * letterCount.",
                paramName: nameof(columns)
            );
        }
        if (factors.IsEmpty) {
            throw new ArgumentException(
                message: "At least one factor is required.",
                paramName: nameof(factors)
            );
        }

        CycleOrder = cycleOrder;
        RayOrbitCount = rayOrbitCount;
        LetterCount = letterCount;
        m_columns = columns.ToArray();
        m_factorStorage = factors.ToArray();

        var coefficientMask = ((1UL << cycleOrder) - 1UL);

        for (var index = 0; (index < m_columns.Length); ++index) {
            if ((m_columns[index] & ~coefficientMask) != 0UL) {
                throw new ArgumentException(
                    message: $"Polynomial column {index} has coefficients outside the cycle.",
                    paramName: nameof(columns)
                );
            }
        }

        ValidateFactors();
        m_fields = m_factorStorage.Select(selector: factor => BinaryField<ulong>.FromModulus(modulus: factor)).ToArray();
        m_evaluatedColumns = new ulong[m_fields.Length][];

        for (var component = 0; (component < m_fields.Length); ++component) {
            var evaluated = new ulong[m_columns.Length];

            for (var index = 0; (index < m_columns.Length); ++index) {
                evaluated[index] = m_fields[component].Reduce(value: m_columns[index]);
            }

            m_evaluatedColumns[component] = evaluated;
        }

        m_syndromes = new BigInteger[letterCount];
        for (var letter = 0; (letter < letterCount); ++letter) {
            var syndrome = BigInteger.Zero;

            for (var rayOrbit = 0; (rayOrbit < rayOrbitCount); ++rayOrbit) {
                if ((BitOperations.PopCount(value: PolynomialAt(
                    letter: letter,
                    rayOrbit: rayOrbit
                )) & 1) != 0) {
                    syndrome |= (BigInteger.One << rayOrbit);
                }
            }

            m_syndromes[letter] = syndrome;
        }

        SyndromeRank = BinaryRank(values: m_syndromes);
    }

    /// <summary>Gets the odd cyclic order.</summary>
    public int CycleOrder { get; }
    /// <summary>Gets a read-only view of the verified irreducible factors of <c>t^n + 1</c>.</summary>
    public ReadOnlyMemory<BinaryPolynomial> Factors => m_factorStorage;
    /// <summary>
    /// Gets the universal word-length ceiling from the syndrome layer: an irreducible word can contain at most
    /// <c>SyndromeRank + 1</c> letters. Odd lengths above this value are reducible before any extension-field work.
    /// </summary>
    public int IrreducibleWordLengthCeiling => Math.Min(
        val1: LetterCount,
        val2: checked((SyndromeRank + 1))
    );
    /// <summary>Gets the number of context/basis orbits.</summary>
    public int LetterCount { get; }
    /// <summary>Gets the number of ray/object orbits.</summary>
    public int RayOrbitCount { get; }
    /// <summary>Gets the binary rank of all <c>t=1</c> syndrome columns.</summary>
    public int SyndromeRank { get; }

    private static int BinaryRank(IEnumerable<BigInteger> values) {
        var pivots = new Dictionary<int, BigInteger>();
        var rank = 0;

        foreach (var initial in values) {
            var value = initial;

            while (!value.IsZero) {
                var pivot = checked((int)(value.GetBitLength() - 1));

                if (!pivots.TryGetValue(
                    key: pivot,
                    value: out var basis
                )) {
                    pivots[pivot] = value;
                    ++rank;
                    break;
                }

                value ^= basis;
            }
        }

        return rank;
    }
    private int ComponentRank(ReadOnlySpan<int> selectedLetters, int component) {
        var columnCount = selectedLetters.Length;
        var matrix = new ulong[checked((RayOrbitCount * columnCount))];

        for (var row = 0; (row < RayOrbitCount); ++row) {
            for (var column = 0; (column < columnCount); ++column) {
                matrix[((row * columnCount) + column)] = m_evaluatedColumns[component][
                    checked(((selectedLetters[column] * RayOrbitCount) + row))
                ];
            }
        }

        var field = m_fields[component];
        var rank = 0;

        for (var column = 0; ((column < columnCount) && (rank < RayOrbitCount)); ++column) {
            var pivot = rank;

            while (
                (pivot < RayOrbitCount) &&
                (matrix[((pivot * columnCount) + column)] == 0UL)
            ) { ++pivot; }
            if (pivot == RayOrbitCount) { continue; }

            if (pivot != rank) {
                for (var cursor = column; (cursor < columnCount); ++cursor) {
                    var first = ((rank * columnCount) + cursor);
                    var second = ((pivot * columnCount) + cursor);

                    (matrix[first], matrix[second]) = (matrix[second], matrix[first]);
                }
            }

            var inverse = field.Inverse(value: matrix[((rank * columnCount) + column)]);

            for (var cursor = column; (cursor < columnCount); ++cursor) {
                var index = ((rank * columnCount) + cursor);

                matrix[index] = field.Multiply(
                    left: matrix[index],
                    right: inverse
                );
            }

            for (var row = 0; (row < RayOrbitCount); ++row) {
                if (row == rank) { continue; }

                var scale = matrix[((row * columnCount) + column)];

                if (scale == 0UL) { continue; }

                for (var cursor = column; (cursor < columnCount); ++cursor) {
                    matrix[((row * columnCount) + cursor)] ^= field.Multiply(
                        left: scale,
                        right: matrix[((rank * columnCount) + cursor)]
                    );
                }
            }

            ++rank;
        }

        return rank;
    }
    private int ExpandedRank(ReadOnlySpan<int> selectedLetters) {
        var rowCount = checked((RayOrbitCount * CycleOrder));
        var pivots = new BigInteger[rowCount];
        var rank = 0;

        foreach (var letter in selectedLetters) {
            for (var shift = 0; (shift < CycleOrder); ++shift) {
                var vector = BigInteger.Zero;

                for (var rayOrbit = 0; (rayOrbit < RayOrbitCount); ++rayOrbit) {
                    var polynomial = PolynomialAt(
                        letter: letter,
                        rayOrbit: rayOrbit
                    );

                    for (var phase = 0; (phase < CycleOrder); ++phase) {
                        if (((polynomial >> phase) & 1UL) == 0UL) { continue; }

                        var ray = checked(((rayOrbit * CycleOrder) + ((phase + shift) % CycleOrder)));

                        vector |= (BigInteger.One << ray);
                    }
                }

                while (!vector.IsZero) {
                    var pivot = checked((int)(vector.GetBitLength() - 1));

                    if (pivots[pivot].IsZero) {
                        pivots[pivot] = vector;
                        ++rank;
                        break;
                    }

                    vector ^= pivots[pivot];
                }
            }
        }

        return rank;
    }
    private ulong PolynomialAt(int letter, int rayOrbit) =>
        m_columns[checked(((letter * RayOrbitCount) + rayOrbit))];
    private void ValidateFactors() {
        var product = new BinaryPolynomial(bits: 1UL);

        for (var first = 0; (first < m_factorStorage.Length); ++first) {
            var factor = m_factorStorage[first];

            if (!factor.IsIrreducible()) {
                throw new ArgumentException(
                    message: $"Factor {factor.Bits:x} is reducible.",
                    paramName: "factors"
                );
            }
            product = checked((product * factor));

            for (var second = (first + 1); (second < m_factorStorage.Length); ++second) {
                if (!factor.GreatestCommonDivisor(other: m_factorStorage[second]).IsOne) {
                    throw new ArgumentException(
                        message: "The proposed factors are not pairwise coprime.",
                        paramName: "factors"
                    );
                }
            }
        }

        var expected = new BinaryPolynomial(bits: (1UL << CycleOrder) | 1UL);

        if (product != expected) {
            throw new ArgumentException(
                message: "The proposed factors do not multiply to t^n + 1.",
                paramName: "factors"
            );
        }
        if (!m_factorStorage.Any(predicate: factor => (factor.Bits == 3UL))) {
            throw new ArgumentException(
                message: "The factorization omits t + 1.",
                paramName: "factors"
            );
        }
    }
    private void ValidateLetter(int letter) {
        if (((uint)letter) >= ((uint)LetterCount)) { throw new ArgumentOutOfRangeException(paramName: nameof(letter)); }
    }
    private void ValidateSelection(ReadOnlySpan<int> selectedLetters) {
        if (selectedLetters.IsEmpty) {
            throw new ArgumentException(
                message: "At least one letter must be selected.",
                paramName: nameof(selectedLetters)
            );
        }

        var seen = ((LetterCount <= 1024)
            ? stackalloc bool[LetterCount]
            : new bool[LetterCount]
        );

        seen.Clear();

        foreach (var letter in selectedLetters) {
            ValidateLetter(letter: letter);
            if (seen[letter]) {
                throw new ArgumentException(
                    message: $"Letter {letter} is selected more than once.",
                    paramName: nameof(selectedLetters)
                );
            }

            seen[letter] = true;
        }
    }

    /// <summary>
    /// Analyzes a union of complete context orbits. CRT mode is fast and exact. Setting
    /// <paramref name="verifyExpandedMatrix"/> additionally constructs the full binary incidence matrix and asserts that
    /// its directly measured nullity equals the cyclotomic sum—an executable demonstration of the theorem's central CRT
    /// identity for this word.
    /// </summary>
    /// <param name="selectedLetters">Distinct zero-based context-orbit indices.</param>
    /// <param name="verifyExpandedMatrix">Whether to perform the slower independent expanded binary-rank calculation.</param>
    /// <returns>Parity, component ranks, nullity, irreducibility, and optional direct-verification evidence.</returns>
    public OddCyclicWordAnalysis Analyze(ReadOnlySpan<int> selectedLetters, bool verifyExpandedMatrix = false) {
        ValidateSelection(selectedLetters: selectedLetters);

        var wordLength = selectedLetters.Length;
        var syndrome = BigInteger.Zero;
        var components = new OddCyclicComponentAnalysis[m_fields.Length];
        var nullity = 0;

        foreach (var letter in selectedLetters) { syndrome ^= m_syndromes[letter]; }

        for (var component = 0; (component < m_fields.Length); ++component) {
            var rank = ComponentRank(
                component: component,
                selectedLetters: selectedLetters
            );
            var componentNullity = checked((m_fields[component].Degree * (wordLength - rank)));

            nullity = checked((nullity + componentNullity));
            components[component] = new OddCyclicComponentAnalysis(
                Factor: m_factorStorage[component],
                Rank: rank,
                NullityContribution: componentNullity
            );
        }

        int? expandedRank = null;
        int? expandedNullity = null;

        if (verifyExpandedMatrix) {
            expandedRank = ExpandedRank(selectedLetters: selectedLetters);
            expandedNullity = checked(((CycleOrder * wordLength) - expandedRank.Value));

            if (expandedNullity.Value != nullity) {
                throw new InvalidOperationException(message: $"CRT nullity {nullity} disagrees with expanded binary nullity {expandedNullity.Value}.");
            }
        }

        var isOddWord = ((wordLength & 1) != 0);
        var hasEvenIncidence = syndrome.IsZero;
        var isParityProof = (isOddWord && hasEvenIncidence);

        return new OddCyclicWordAnalysis(
            wordLength: wordLength,
            isOddWord: isOddWord,
            hasEvenIncidence: hasEvenIncidence,
            isParityProof: isParityProof,
            isSyndromeCircuit: (hasEvenIncidence && (components[Array.FindIndex(
                array: m_factorStorage,
                match: factor => (factor.Bits == 3UL)
            )].Rank == (wordLength - 1))),
            nullity: nullity,
            components: components,
            expandedRank: expandedRank,
            expandedNullity: expandedNullity
        );
    }
    /// <summary>Returns one packed incidence polynomial from the immutable table.</summary>
    public BinaryPolynomial GetPolynomial(int letter, int rayOrbit) {
        ValidateLetter(letter: letter);
        if (((uint)rayOrbit) >= ((uint)RayOrbitCount)) { throw new ArgumentOutOfRangeException(paramName: nameof(rayOrbit)); }

        return new BinaryPolynomial(bits: PolynomialAt(
            letter: letter,
            rayOrbit: rayOrbit
        ));
    }
    /// <summary>Returns a letter's <c>t=1</c> syndrome as a bit vector over the ray orbits.</summary>
    public BigInteger GetSyndrome(int letter) {
        ValidateLetter(letter: letter);

        return m_syndromes[letter];
    }
    /// <summary>
    /// Tests the inexpensive binary-matroid condition: the selected syndrome columns sum to zero and have rank exactly
    /// one below their count. Every irreducible odd cyclic parity proof must pass this filter.
    /// </summary>
    public bool IsSyndromeCircuit(ReadOnlySpan<int> selectedLetters) {
        ValidateSelection(selectedLetters: selectedLetters);

        var values = new BigInteger[selectedLetters.Length];
        var sum = BigInteger.Zero;

        for (var index = 0; (index < selectedLetters.Length); ++index) {
            var value = m_syndromes[selectedLetters[index]];

            values[index] = value;
            sum ^= value;
        }

        return (
            sum.IsZero &&
            (BinaryRank(values: values) == (selectedLetters.Length - 1))
        );
    }
}
/// <summary>One Chinese-remainder component of an odd-cyclic word analysis.</summary>
/// <param name="Factor">The irreducible factor defining the finite field.</param>
/// <param name="Rank">The selected polynomial columns' rank over that field.</param>
/// <param name="NullityContribution"><c>degree(Factor) * (wordLength - Rank)</c>.</param>
public readonly record struct OddCyclicComponentAnalysis(BinaryPolynomial Factor, int Rank, int NullityContribution);
/// <summary>The exact result of analyzing a union of complete odd-cyclic context orbits.</summary>
public sealed class OddCyclicWordAnalysis {
    private readonly OddCyclicComponentAnalysis[] m_componentStorage;

    internal OddCyclicWordAnalysis(
        int wordLength,
        bool isOddWord,
        bool hasEvenIncidence,
        bool isParityProof,
        bool isSyndromeCircuit,
        int nullity,
        OddCyclicComponentAnalysis[] components,
        int? expandedRank,
        int? expandedNullity
    ) {
        WordLength = wordLength;
        IsOddWord = isOddWord;
        HasEvenIncidence = hasEvenIncidence;
        IsParityProof = isParityProof;
        IsSyndromeCircuit = isSyndromeCircuit;
        Nullity = nullity;
        m_componentStorage = components;
        ExpandedRank = expandedRank;
        ExpandedNullity = expandedNullity;
    }

    /// <summary>Gets the finite-field rank evidence in factor order.</summary>
    public ReadOnlyMemory<OddCyclicComponentAnalysis> Components => m_componentStorage;
    /// <summary>Gets whether direct expansion verified the CRT identity, or <see langword="null"/> when not requested.</summary>
    public bool? CrtMatchesExpanded => (ExpandedNullity.HasValue
        ? (ExpandedNullity.Value == Nullity)
        : null
    );
    /// <summary>Gets the independently computed expanded binary nullity, when requested.</summary>
    public int? ExpandedNullity { get; }
    /// <summary>Gets the independently computed expanded binary rank, when requested.</summary>
    public int? ExpandedRank { get; }
    /// <summary>Gets whether every expanded ray/object has even incidence.</summary>
    public bool HasEvenIncidence { get; }
    /// <summary>Gets whether the parity proof has no non-zero proper kernel selection.</summary>
    public bool IsIrreducible => (IsParityProof && (Nullity == 1));
    /// <summary>Gets whether the complete-orbit selection contains an odd number of contexts.</summary>
    public bool IsOddWord { get; }
    /// <summary>Gets whether this is an odd-cardinality parity proof.</summary>
    public bool IsParityProof { get; }
    /// <summary>Gets whether the <c>t=1</c> syndrome columns have exactly the compulsory all-ones relation.</summary>
    public bool IsSyndromeCircuit { get; }
    /// <summary>Gets the exact binary nullity, computed as the degree-weighted sum of component nullities.</summary>
    public int Nullity { get; }
    /// <summary>Gets the number of complete context orbits selected.</summary>
    public int WordLength { get; }
}
