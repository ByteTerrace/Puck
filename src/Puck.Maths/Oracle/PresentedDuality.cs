using System.Runtime.InteropServices;

namespace Puck.Maths;

/// <summary>The word at which two machines' behaviors first disagree, together with the two values it produced.</summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <remarks>A default value carries an empty word and is what an equivalence decision returns when the two machines
/// agree everywhere; the word is otherwise the SHORTEST distinguishing one, in the well-founded order the closure
/// numbers states by.</remarks>
public readonly struct EquivalenceWitness<TValue> {
    private readonly int[]? m_word;

    internal EquivalenceWitness(int[] word, TValue leftValue, TValue rightValue) {
        LeftValue = leftValue;
        RightValue = rightValue;
        m_word = word;
    }

    /// <summary>Gets the value the first machine produced on the distinguishing word.</summary>
    public TValue LeftValue { get; }
    /// <summary>Gets the value the second machine produced on the distinguishing word.</summary>
    public TValue RightValue { get; }
    /// <summary>Gets the shortest distinguishing word as generator symbols, or an empty span when the machines agree.</summary>
    public ReadOnlySpan<int> Word => m_word;
}

/// <summary>
/// A module over a presented algebra, stated as its duality data: an initial vector, one step element per input symbol,
/// and a readout covector. Its behavior on a word is the pairing of the readout with the initial vector stepped along
/// that word — a state, a step and a readout, which is the whole of the stepper framing and is not a second kernel.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>Immutable once created. The steps are ordinary algebra elements, so stepping is
/// <see cref="PresentedAlgebra{TValue, TOps}.Multiply"/> and nothing else; a machine adds no arithmetic of its own.</remarks>
public sealed class PresentedMachine<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private readonly PresentedAlgebra<TValue, TOps>.Element[] m_steps;

    private PresentedMachine(
        PresentedAlgebra<TValue, TOps> algebra,
        in PresentedAlgebra<TValue, TOps>.Element initial,
        PresentedAlgebra<TValue, TOps>.Element[] steps,
        in PresentedAlgebra<TValue, TOps>.Element readout
    ) {
        Algebra = algebra;
        Initial = initial;
        Readout = readout;
        m_steps = steps;
    }

    /// <summary>Gets the algebra whose elements the steps are.</summary>
    public PresentedAlgebra<TValue, TOps> Algebra { get; }
    /// <summary>Gets the initial vector.</summary>
    public PresentedAlgebra<TValue, TOps>.Element Initial { get; }
    /// <summary>Gets the readout covector.</summary>
    public PresentedAlgebra<TValue, TOps>.Element Readout { get; }
    /// <summary>Gets the number of input symbols, which is the number of step elements.</summary>
    public int StepCount => m_steps.Length;

    /// <summary>Creates a machine.</summary>
    /// <param name="algebra">The algebra the steps live in.</param>
    /// <param name="initial">The initial vector.</param>
    /// <param name="steps">One step element per input symbol.</param>
    /// <param name="readout">The readout covector.</param>
    /// <returns>The described machine.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="algebra"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The initial vector, a step or the readout belongs to another algebra.</exception>
    /// <remarks>Every part is checked for membership here rather than at the first <see cref="Run"/>: a module is a
    /// vector, an action and a functional over ONE algebra, and a machine assembled from another's elements would
    /// answer with values read off the wrong normal forms instead of failing.</remarks>
    public static PresentedMachine<TValue, TOps> Create(
        PresentedAlgebra<TValue, TOps> algebra,
        in PresentedAlgebra<TValue, TOps>.Element initial,
        ReadOnlySpan<PresentedAlgebra<TValue, TOps>.Element> steps,
        in PresentedAlgebra<TValue, TOps>.Element readout
    ) {
        ArgumentNullException.ThrowIfNull(argument: algebra);
        algebra.RequireOwned(value: initial, paramName: nameof(initial));
        algebra.RequireOwned(value: readout, paramName: nameof(readout));

        foreach (var step in steps) { algebra.RequireOwned(value: step, paramName: nameof(steps)); }

        return new(algebra: algebra, initial: initial, steps: steps.ToArray(), readout: readout);
    }

    /// <summary>Decides whether two machines have the same behavior on every word, and returns the shortest word that
    /// separates them when they do not.</summary>
    /// <param name="left">The first machine.</param>
    /// <param name="right">The second machine.</param>
    /// <param name="witness">On inequivalence, the shortest distinguishing word and the two values it produced;
    /// otherwise a witness carrying an empty word.</param>
    /// <returns><see langword="true"/> when the behaviors agree on every word; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">A machine is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The machines take a different number of input symbols, or their algebras
    /// carry different materials.</exception>
    /// <exception cref="InvalidOperationException">The material is not a certified field, or an algebra has no finite basis.</exception>
    /// <remarks>
    /// <para>
    /// Exact, and exact-only: the decision quotients by a pairing radical, which needs the material's inverses and is
    /// meaningless over a rounding carrier where two behaviors may differ in the last bit for no structural reason.
    /// </para>
    /// <para>
    /// It walks the joint state space breadth-first, keeping only the pairs that raise the rank of the combined vector.
    /// A pair that does not raise the rank is a linear combination of pairs already kept, so by linearity of both the
    /// step and the readout its whole subtree is decided too — that is what makes the walk polynomial rather than a
    /// word enumeration, and what makes the first disagreement found the shortest one.
    /// </para>
    /// <para>
    /// <b>The two presentations may differ; the two MATERIALS may not.</b> The rank walk reduces the joint vector in a
    /// single field, so it is a decision procedure only when both halves are arithmetic in that same field: a machine
    /// over one modulus compared against a machine over another would have its subtree pruned by a rank statement that
    /// is false on the right-hand side, and the call would answer equivalent for machines that disagree at every short
    /// word. The materials are therefore compared by value and an unequal pair is refused rather than decided.
    /// </para>
    /// </remarks>
    public static bool AreEquivalent(PresentedMachine<TValue, TOps> left, PresentedMachine<TValue, TOps> right, out EquivalenceWitness<TValue> witness) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        if (left.StepCount != right.StepCount) {
            throw new ArgumentException(message: "Two machines are comparable only when they take the same number of input symbols.", paramName: nameof(right));
        }

        if (!EqualityComparer<TOps>.Default.Equals(x: left.Algebra.Presentation.Material, y: right.Algebra.Presentation.Material)) {
            throw new ArgumentException(message: "Two machines are comparable only over one material: the joint rank walk reduces both halves in a single field.", paramName: nameof(right));
        }

        var comparer = EqualityComparer<TValue>.Default;
        var leftAlgebra = left.Algebra;
        var leftWidth = RequireFiniteBasis(algebra: leftAlgebra);
        var rightAlgebra = right.Algebra;
        var rightWidth = RequireFiniteBasis(algebra: rightAlgebra);
        var field = RequireField(algebra: leftAlgebra);
        var basis = new FieldEchelon<TValue, TOps>(field: field, width: (leftWidth + rightWidth));
        var joint = new TValue[(leftWidth + rightWidth)];
        var reached = new List<(PresentedAlgebra<TValue, TOps>.Element Left, PresentedAlgebra<TValue, TOps>.Element Right, int[] Word)>();

        witness = default;

        bool Disagrees(in PresentedAlgebra<TValue, TOps>.Element leftState, in PresentedAlgebra<TValue, TOps>.Element rightState, int[] word, out EquivalenceWitness<TValue> found) {
            var leftValue = leftAlgebra.Pair(covector: left.Readout, value: leftState);
            var rightValue = rightAlgebra.Pair(covector: right.Readout, value: rightState);

            found = new EquivalenceWitness<TValue>(word: word, leftValue: leftValue, rightValue: rightValue);

            return !comparer.Equals(x: leftValue, y: rightValue);
        }

        void Load(in PresentedAlgebra<TValue, TOps>.Element leftState, in PresentedAlgebra<TValue, TOps>.Element rightState) {
            Array.Fill(array: joint, value: field.Zero);

            for (var index = 0; (index < leftState.SupportCount); ++index) { joint[((int)leftState.Keys[index])] = leftState.Coefficients[index]; }
            for (var index = 0; (index < rightState.SupportCount); ++index) { joint[(leftWidth + (int)rightState.Keys[index])] = rightState.Coefficients[index]; }
        }

        if (Disagrees(leftState: left.Initial, rightState: right.Initial, word: [], found: out witness)) { return false; }

        Load(leftState: left.Initial, rightState: right.Initial);

        if (basis.TryAdmit(vector: joint)) { reached.Add(item: (left.Initial, right.Initial, [])); }

        for (var cursor = 0; (cursor < reached.Count); ++cursor) {
            var (leftState, rightState, word) = reached[cursor];

            for (var symbol = 0; (symbol < left.StepCount); ++symbol) {
                var leftNext = leftAlgebra.Multiply(left: leftState, right: left.Step(index: symbol));
                var rightNext = rightAlgebra.Multiply(left: rightState, right: right.Step(index: symbol));
                var nextWord = new int[(word.Length + 1)];

                word.CopyTo(array: nextWord, index: 0);
                nextWord[word.Length] = symbol;

                if (Disagrees(leftState: leftNext, rightState: rightNext, word: nextWord, found: out witness)) { return false; }

                Load(leftState: leftNext, rightState: rightNext);

                if (basis.TryAdmit(vector: joint)) { reached.Add(item: (leftNext, rightNext, nextWord)); }
            }
        }

        witness = default;

        return true;
    }

    /// <summary>Quotients this machine by its pairing radical, returning a minimal machine of the same behavior.</summary>
    /// <returns>A machine at a quiver presentation on the minimal number of states, with the same behavior on every word.</returns>
    /// <exception cref="InvalidOperationException">The material is not a certified field, the algebra has no finite
    /// basis, or the minimal state count exceeds
    /// <see cref="PresentedAlgebra{TValue, TOps}.MaximumClosureStates"/>.</exception>
    /// <remarks>
    /// <para>
    /// The construction is the duality one and it is exact-only. The reachable subspace is the span of the initial
    /// vector under every step; the pairing radical is the part of it that every readout-after-a-word annihilates; and
    /// the quotient by that radical is the minimal realization. Its dimension is the rank of the observation span, and
    /// both spans are computed by reduced row echelon over the material's own inverses — which is why the operation
    /// lives at a field material and is refused elsewhere rather than approximated.
    /// </para>
    /// <para>
    /// The result is canonical in behavior and dimension, and NOT in coordinates. Its basis is the reduced echelon
    /// basis of the observation span, which depends on the order the reachable vectors arrived in, so two machines of
    /// the same behavior minimize to machines of the same dimension whose behaviors agree word for word while their
    /// initial vectors, steps and readouts may sit at different states. Behavioral sameness is what
    /// <see cref="AreEquivalent"/> decides; a coordinatewise comparison of two minimal forms is not that decision.
    /// </para>
    /// </remarks>
    public PresentedMachine<TValue, TOps> MinimizeByPairingRadical() {
        var algebra = Algebra;
        var field = RequireField(algebra: algebra);
        var material = algebra.Presentation.Material;
        var width = RequireFiniteBasis(algebra: algebra);
        var fullKeys = new long[width];

        for (var key = 0; (key < width); ++key) { fullKeys[key] = key; }

        // The reachable subspace: close the initial vector under every step, keeping only the vectors that raise the
        // rank. Its reduced echelon rows are the basis every later coordinate is read against.
        var reachable = new FieldEchelon<TValue, TOps>(field: field, width: width);
        var frontier = new List<PresentedAlgebra<TValue, TOps>.Element>();
        var dense = new TValue[width];

        Scatter(value: Initial, dense: dense, zero: field.Zero);

        if (reachable.TryAdmit(vector: dense)) { frontier.Add(item: Initial); }

        for (var cursor = 0; (cursor < frontier.Count); ++cursor) {
            for (var symbol = 0; (symbol < StepCount); ++symbol) {
                var next = algebra.Multiply(left: frontier[cursor], right: m_steps[symbol]);

                Scatter(value: next, dense: dense, zero: field.Zero);

                if (reachable.TryAdmit(vector: dense)) { frontier.Add(item: next); }
            }
        }

        var reachableCount = reachable.Count;
        var basis = new PresentedAlgebra<TValue, TOps>.Element[reachableCount];

        for (var index = 0; (index < reachableCount); ++index) {
            basis[index] = algebra.FromSupport(keys: fullKeys, coefficients: reachable.Row(index: index));
        }

        // The step matrices in that basis, and the readout as a covector on it.
        var stepMatrix = new TValue[StepCount][][];
        var observation = new TValue[reachableCount];

        for (var index = 0; (index < reachableCount); ++index) {
            observation[index] = algebra.Pair(covector: Readout, value: basis[index]);
        }

        for (var symbol = 0; (symbol < StepCount); ++symbol) {
            var rows = new TValue[reachableCount][];

            for (var index = 0; (index < reachableCount); ++index) {
                var moved = algebra.Multiply(left: basis[index], right: m_steps[symbol]);

                Scatter(value: moved, dense: dense, zero: field.Zero);

                rows[index] = new TValue[reachableCount];
                reachable.Coordinates(vector: dense, coordinates: rows[index]);
            }

            stepMatrix[symbol] = rows;
        }

        // The observation span: the readout covector closed under the transposed steps. Its rank is the minimal
        // dimension, and its radical — the part of the reachable space it annihilates — is what the quotient removes.
        var observed = new FieldEchelon<TValue, TOps>(field: field, width: reachableCount);
        var covectors = new List<TValue[]>();
        var carried = new TValue[reachableCount];

        observation.CopyTo(array: carried, index: 0);

        if (observed.TryAdmit(vector: carried)) { covectors.Add(item: observation); }

        for (var cursor = 0; (cursor < covectors.Count); ++cursor) {
            for (var symbol = 0; (symbol < StepCount); ++symbol) {
                var moved = ApplyMatrix(matrix: stepMatrix[symbol], vector: covectors[cursor], field: field);

                moved.CopyTo(array: carried, index: 0);

                if (observed.TryAdmit(vector: carried)) { covectors.Add(item: moved); }
            }
        }

        var dimension = observed.Count;

        if (dimension > PresentedAlgebra<TValue, TOps>.MaximumClosureStates) {
            throw new InvalidOperationException(message: "The minimal realization needs more states than a quiver presentation of this library carries.");
        }

        var quiver = PresentedAlgebra<TValue, TOps>.Create(presentation: Presentations.Quiver<TValue, TOps>(objectCount: Math.Max(val1: dimension, val2: 1), arrows: [], material: material));

        if (0 == dimension) {
            var silent = new PresentedAlgebra<TValue, TOps>.Element[StepCount];

            Array.Fill(array: silent, value: quiver.Zero);

            return new PresentedMachine<TValue, TOps>(
                algebra: quiver,
                initial: quiver.Zero,
                steps: silent,
                readout: quiver.Zero
            );
        }

        // The quotient coordinates: state k of the minimal machine is the observation functional o_k evaluated at the
        // reachable state, so the initial vector, the steps and the readout are all read off in that basis.
        Scatter(value: Initial, dense: dense, zero: field.Zero);

        var initialCoordinates = new TValue[reachableCount];

        reachable.Coordinates(vector: dense, coordinates: initialCoordinates);

        var minimalInitialKeys = new long[dimension];
        var minimalInitialValues = new TValue[dimension];

        for (var state = 0; (state < dimension); ++state) {
            minimalInitialKeys[state] = state;
            minimalInitialValues[state] = Contract(left: observed.Row(index: state), right: initialCoordinates, field: field);
        }

        var minimalReadoutKeys = new long[dimension];
        var minimalReadoutValues = new TValue[dimension];

        for (var state = 0; (state < dimension); ++state) {
            minimalReadoutKeys[state] = state;
            minimalReadoutValues[state] = observation[observed.Pivot(index: state)];
        }

        var minimalSteps = new PresentedAlgebra<TValue, TOps>.Element[StepCount];
        var cellKeys = new long[(dimension * dimension)];
        var cellValues = new TValue[(dimension * dimension)];

        // Every step of a codiscrete quiver fills the same full support, so the keys are the identity map once.
        for (var cell = 0; (cell < cellKeys.Length); ++cell) { cellKeys[cell] = cell; }

        for (var symbol = 0; (symbol < StepCount); ++symbol) {
            for (var state = 0; (state < dimension); ++state) {
                var moved = ApplyMatrix(matrix: stepMatrix[symbol], vector: observed.Row(index: state), field: field);

                for (var target = 0; (target < dimension); ++target) {
                    cellValues[((target * dimension) + state)] = moved[observed.Pivot(index: target)];
                }
            }

            minimalSteps[symbol] = quiver.FromSupport(keys: cellKeys, coefficients: cellValues);
        }

        return new PresentedMachine<TValue, TOps>(
            algebra: quiver,
            initial: quiver.FromSupport(keys: minimalInitialKeys, coefficients: minimalInitialValues),
            steps: minimalSteps,
            readout: quiver.FromSupport(keys: minimalReadoutKeys, coefficients: minimalReadoutValues)
        );
    }

    /// <summary>Returns the behavior of one word: the readout paired with the initial vector stepped along it.</summary>
    /// <param name="word">The input symbols, in the order they are applied.</param>
    /// <returns>The behavior value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A symbol names no step of this machine.</exception>
    public TValue Run(ReadOnlySpan<int> word) {
        var state = Initial;

        foreach (var symbol in word) {
            state = Algebra.Multiply(left: state, right: Step(index: symbol));
        }

        return Algebra.Pair(covector: Readout, value: state);
    }

    /// <summary>Returns one step element.</summary>
    /// <param name="index">The input symbol.</param>
    /// <returns>The step element.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The symbol names no step of this machine.</exception>
    public PresentedAlgebra<TValue, TOps>.Element Step(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: m_steps.Length);

        return m_steps[index];
    }

    private static TValue[] ApplyMatrix(TValue[][] matrix, ReadOnlySpan<TValue> vector, IFieldMaterial<TValue, TOps> field) {
        var result = new TValue[matrix.Length];

        for (var row = 0; (row < matrix.Length); ++row) {
            result[row] = Contract(left: matrix[row], right: vector, field: field);
        }

        return result;
    }
    private static TValue Contract(ReadOnlySpan<TValue> left, ReadOnlySpan<TValue> right, IFieldMaterial<TValue, TOps> field) {
        var total = field.Zero;

        for (var index = 0; (index < left.Length); ++index) {
            total = field.Add(left: total, right: field.Multiply(left: left[index], right: right[index]));
        }

        return total;
    }
    private static IFieldMaterial<TValue, TOps> RequireField(PresentedAlgebra<TValue, TOps> algebra) {
        if (algebra.Presentation.Material is not IFieldMaterial<TValue, TOps> field) {
            throw new InvalidOperationException(message: "The duality quotient inverts coefficients, which a material that is not a certified field cannot do.");
        }

        return field;
    }
    private static int RequireFiniteBasis(PresentedAlgebra<TValue, TOps> algebra) {
        if (!algebra.Presentation.HasCompiledNormalFormBasis) {
            throw new InvalidOperationException(message: "The duality quotient needs a finite basis to coordinatize, which this presentation does not have.");
        }

        return algebra.Presentation.NormalFormCount;
    }
    private static void Scatter(in PresentedAlgebra<TValue, TOps>.Element value, TValue[] dense, TValue zero) {
        Array.Fill(array: dense, value: zero);

        for (var index = 0; (index < value.SupportCount); ++index) { dense[((int)value.Keys[index])] = value.Coefficients[index]; }
    }
}
public sealed partial class PresentedAlgebra<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private TValue[] m_pairCharges = [];
    private TValue[] m_pairValues = [];

    /// <summary>Returns the behavior of one element between an initial vector and a readout covector.</summary>
    /// <param name="initial">The initial vector.</param>
    /// <param name="value">The element that acts — the image of a word under the module action.</param>
    /// <param name="readout">The readout covector.</param>
    /// <returns>The behavior value <c>⟨readout, initial·value⟩</c>.</returns>
    /// <exception cref="ArgumentException">An operand belongs to another algebra.</exception>
    /// <remarks>This is the duality in one call: a state, a step, and a readout. The product rounds once per coefficient
    /// and the pairing rounds once for the returned scalar, which is the same discipline every other operation here
    /// keeps.</remarks>
    public TValue Behavior(in Element initial, in Element value, in Element readout) {
        RequireOwned(value: initial, paramName: nameof(initial));
        RequireOwned(value: value, paramName: nameof(value));
        RequireOwned(value: readout, paramName: nameof(readout));

        return Pair(covector: readout, value: Multiply(left: initial, right: value));
    }

    /// <summary>Pairs a covector with an element.</summary>
    /// <param name="covector">The covector, carried as an element whose coefficients are read as functionals on the keys
    /// they sit at.</param>
    /// <param name="value">The element.</param>
    /// <returns><c>Σ covector[k]·value[k]</c>, folded with exactly one rounding.</returns>
    /// <exception cref="ArgumentException">An operand belongs to another algebra.</exception>
    /// <remarks>The evaluation half of the duality. Both operands are canonically ordered supports, so the matched keys
    /// are found by one merge walk and folded through a single
    /// <see cref="IMaterialOps{TValue, TOps}.FusedChargedLinear"/>.</remarks>
    public TValue Pair(in Element covector, in Element value) {
        RequireOwned(value: covector, paramName: nameof(covector));
        RequireOwned(value: value, paramName: nameof(value));

        var covectorKeys = covector.Keys;
        var covectorValues = covector.Coefficients;
        var material = m_material;
        var valueKeys = value.Keys;
        var valueValues = value.Coefficients;
        var needed = Math.Min(val1: covectorKeys.Length, val2: valueKeys.Length);

        if (m_pairCharges.Length < needed) {
            m_pairCharges = new TValue[needed];
            m_pairValues = new TValue[needed];
        }

        var covectorIndex = 0;
        var terms = 0;
        var valueIndex = 0;

        while ((covectorIndex < covectorKeys.Length) && (valueIndex < valueKeys.Length)) {
            var covectorKey = covectorKeys[covectorIndex];
            var valueKey = valueKeys[valueIndex];

            if (covectorKey < valueKey) {
                ++covectorIndex;
            } else if (valueKey < covectorKey) {
                ++valueIndex;
            } else {
                m_pairCharges[terms] = covectorValues[covectorIndex++];
                m_pairValues[terms] = valueValues[valueIndex++];
                ++terms;
            }
        }

        return material.FusedChargedLinear(
            charges: m_pairCharges.AsSpan(start: 0, length: terms),
            values: m_pairValues.AsSpan(start: 0, length: terms),
            lane: m_lane
        );
    }

    /// <summary>Pairs one element of each factor into this tensor algebra — the Kronecker pair-up.</summary>
    /// <param name="left">The left factor's element.</param>
    /// <param name="right">The right factor's element.</param>
    /// <param name="rightKeyCount">The right factor's normal-form count, which is the stride of the pair key.</param>
    /// <returns>The element of this algebra whose coefficient at <c>i·rightKeyCount + j</c> is the product of the two
    /// factors' coefficients.</returns>
    /// <exception cref="ArgumentException">A nondefault factor has no finite basis, has the wrong basis width for its
    /// side of the pairing, or carries a material different from this algebra's.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rightKeyCount"/> is not positive, or a paired key
    /// names no normal form of this algebra, including when it does not divide this algebra's basis width.</exception>
    /// <remarks>
    /// <para>
    /// <b>This is the intentional cross-algebra operation.</b> Unlike ordinary element consumers, the two operands do
    /// not belong to this tensor algebra: each belongs to its factor. Their owners are therefore validated by shape and
    /// material rather than by reference identity. The right owner's finite basis must have
    /// <paramref name="rightKeyCount"/> keys, the left owner's must have the complementary quotient of this algebra's
    /// basis width, and both materials must equal this algebra's material by value. Factor presentations may otherwise
    /// differ, because pair-up reads their canonical coordinates and not their products. A default-initialized operand
    /// remains the universal zero and needs no owner.
    /// </para>
    /// <para>
    /// <b>The construction survives every material; the THEOREM does not.</b> The pair-up itself is defined over any
    /// semiring and each of its cells carries exactly one rounding. But the cells of a tensor are not products of the
    /// factors' already-rounded cells, so over the house scalar the behavior of a pair-up is NOT the product of the two
    /// behaviors. That identity holds on every exact material and fails on <see cref="FixedQ4816"/>; it is a documented
    /// boundary, not a defect.
    /// </para>
    /// </remarks>
    public Element PairUp(in Element left, in Element right, int rightKeyCount) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: rightKeyCount, other: 1);

        if (!m_isDense || (0 != (m_keyCount % rightKeyCount))) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(rightKeyCount),
                message: "The right-factor stride must divide this algebra's finite basis width."
            );
        }

        RequireTensorFactor(value: left, keyCount: (m_keyCount / rightKeyCount), paramName: nameof(left));
        RequireTensorFactor(value: right, keyCount: rightKeyCount, paramName: nameof(right));

        var material = m_material;
        var coefficients = new List<TValue>();
        var keys = new List<long>();

        for (var leftIndex = 0; (leftIndex < left.SupportCount); ++leftIndex) {
            var leftKey = left.Keys[leftIndex];
            var leftValue = left.Coefficients[leftIndex];

            for (var rightIndex = 0; (rightIndex < right.SupportCount); ++rightIndex) {
                var rightKey = right.Keys[rightIndex];

                if (rightKey >= rightKeyCount) {
                    throw new ArgumentOutOfRangeException(paramName: nameof(rightKeyCount), message: "A key of the right factor lies outside the declared stride.");
                }

                var value = material.Multiply(left: leftValue, right: right.Coefficients[rightIndex]);

                if (material.IsZero(value: value)) { continue; }

                coefficients.Add(item: value);
                keys.Add(item: ((leftKey * rightKeyCount) + rightKey));
            }
        }

        return FromSupport(keys: CollectionsMarshal.AsSpan(list: keys), coefficients: CollectionsMarshal.AsSpan(list: coefficients));
    }

    // PairUp alone consumes elements from factor algebras rather than from this one. Its ownership rule is coordinate
    // ownership: an actual owner supplies the finite basis width and material that make the coordinate embedding
    // meaningful; the owner-less default remains the zero of every coordinate space.
    private void RequireTensorFactor(in Element value, int keyCount, string paramName) {
        var owner = value.Owner;

        if (owner is null) { return; }

        if (!owner.Presentation.HasCompiledNormalFormBasis || (owner.Presentation.NormalFormCount != keyCount)) {
            throw new ArgumentException(
                message: "A tensor factor belongs to a finite-basis algebra with the basis width assigned to that factor.",
                paramName: paramName
            );
        }

        if (!EqualityComparer<TOps>.Default.Equals(x: owner.Presentation.Material, y: m_material)) {
            throw new ArgumentException(
                message: "A tensor factor carries the same material value as the tensor algebra.",
                paramName: paramName
            );
        }
    }

    /// <summary>Returns the trace of an element: its pairing with the unit.</summary>
    /// <param name="value">The element.</param>
    /// <returns>The trace, folded with exactly one rounding.</returns>
    /// <exception cref="ArgumentException">The element belongs to another algebra.</exception>
    /// <remarks>It is the counit of the duality, and the presentation decides what that means without a special case:
    /// at a monoid presentation the unit is one basis element, so the trace is the scalar part; at a quiver the unit is
    /// the diagonal sum, so the trace is the matrix trace.</remarks>
    public TValue Trace(in Element value) {
        RequireOwned(value: value, paramName: nameof(value));

        return Pair(covector: Identity, value: value);
    }
}

/// <summary>A reduced row echelon basis over a field material — the one piece of linear algebra the duality layer needs,
/// and the reason its operations are refused at a material without inverses.</summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
internal sealed class FieldEchelon<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    private readonly IFieldMaterial<TValue, TOps> m_field;
    private readonly List<int> m_pivots = [];
    private readonly List<TValue[]> m_rows = [];
    private readonly int m_width;

    internal FieldEchelon(IFieldMaterial<TValue, TOps> field, int width) {
        m_field = field;
        m_width = width;
    }

    /// <summary>Gets the number of independent rows admitted.</summary>
    internal int Count => m_rows.Count;

    /// <summary>Reads the coordinates of a vector of the span against the admitted rows.</summary>
    internal void Coordinates(ReadOnlySpan<TValue> vector, Span<TValue> coordinates) {
        for (var index = 0; (index < m_rows.Count); ++index) { coordinates[index] = vector[m_pivots[index]]; }
    }

    /// <summary>Gets the pivot column of one admitted row.</summary>
    internal int Pivot(int index) =>
        m_pivots[index];

    /// <summary>Gets one admitted row.</summary>
    internal ReadOnlySpan<TValue> Row(int index) =>
        m_rows[index];

    /// <summary>Admits a vector when it is independent of the rows already held, reducing it in place either way.</summary>
    internal bool TryAdmit(Span<TValue> vector) {
        for (var index = 0; (index < m_rows.Count); ++index) {
            var factor = vector[m_pivots[index]];

            if (m_field.IsZero(value: factor)) { continue; }

            var row = m_rows[index];

            for (var column = 0; (column < m_width); ++column) {
                vector[column] = m_field.Subtract(left: vector[column], right: m_field.Multiply(left: factor, right: row[column]));
            }
        }

        var pivot = -1;

        for (var column = 0; (column < m_width); ++column) {
            if (!m_field.IsZero(value: vector[column])) {
                pivot = column;

                break;
            }
        }

        if ((pivot < 0) || !m_field.TryInvert(value: vector[pivot], out var inverse)) { return false; }

        var admitted = new TValue[m_width];

        for (var column = 0; (column < m_width); ++column) { admitted[column] = m_field.Multiply(left: inverse, right: vector[column]); }

        // Back-substitute so the basis stays reduced: with every other row carrying a zero in this pivot, a coordinate
        // read is a single lookup rather than a solve.
        for (var index = 0; (index < m_rows.Count); ++index) {
            var existing = m_rows[index];
            var factor = existing[pivot];

            if (m_field.IsZero(value: factor)) { continue; }

            for (var column = 0; (column < m_width); ++column) {
                existing[column] = m_field.Subtract(left: existing[column], right: m_field.Multiply(left: factor, right: admitted[column]));
            }
        }

        var slot = 0;

        while ((slot < m_pivots.Count) && (m_pivots[slot] < pivot)) { ++slot; }

        m_pivots.Insert(index: slot, item: pivot);
        m_rows.Insert(index: slot, item: admitted);

        return true;
    }
}
