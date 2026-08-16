namespace Puck.Maths;

/// <summary>The certificate that one generator is a unit: the basis element that inverts it, and the coefficient that
/// inverse carries.</summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <param name="Symbol">The generator's symbol.</param>
/// <param name="InverseKey">The normal-form key of the basis element that inverts it.</param>
/// <param name="InverseCharge">The coefficient the inverse carries — the material's one, its negation where the
/// generator squares to minus the unit as a Clifford or Cayley-Dickson generator does, or, at a field material, the
/// coefficient derived from the cell the pair lands in, which is the only one that can work where the cell charge is
/// neither sign.</param>
/// <remarks>It is a computed witness, not a declaration: the pair is admitted only after
/// <c>generator · (charge · basis[key])</c> and its mirror image were both multiplied and both found equal to the
/// unit.</remarks>
public readonly record struct UnitWitness<TValue>(int Symbol, long InverseKey, TValue InverseCharge);
/// <summary>The refusal of a bounded group query.</summary>
/// <param name="Outcome">Why the query stopped. <see cref="ClosureOutcome.SearchLimitReached"/> is the budget: the
/// answer may well exist and was not reached. <see cref="ClosureOutcome.AmbiguityWitness"/> is a structural refusal
/// carried rather than thrown — the material does not license the exact semiring laws the finite-basis proof needs, a
/// generator has no inverse among the candidates searched, an element is not a single basis element at unit
/// coefficient, or a step's image is not one basis element, so there is no permutation to close.</param>
/// <param name="BlockedSymbol">The generator at fault, or <c>-1</c> where no single generator is.</param>
/// <param name="BlockedKey">The normal-form key at fault, or <c>-1</c> where no single key is.</param>
/// <param name="PointsReached">How far the query got: candidates searched, orbit members reached, or word letters
/// consumed.</param>
/// <remarks>When <paramref name="Outcome"/> is <see cref="ClosureOutcome.BasisNonAssociativityDetected"/>, the three
/// <c>Associator…Key</c> properties name the ordered basis triple whose two bracketings disagreed. When it is
/// <see cref="ClosureOutcome.SearchLimitReached"/> during <see cref="PresentedGroup{TValue, TOps}.TryCertify"/>, the
/// presentation has no finite basis on which associativity can be certified. The associator keys are <c>-1</c> for
/// every other obstruction.</remarks>
public readonly record struct GroupObstruction(ClosureOutcome Outcome, int BlockedSymbol, long BlockedKey, long PointsReached) {
    /// <summary>Gets the first key of a failed associativity triple, or <c>-1</c> when this is not a nonassociativity
    /// obstruction.</summary>
    public int AssociatorLeftKey { get; init; } = -1;
    /// <summary>Gets the middle key of a failed associativity triple, or <c>-1</c> when this is not a nonassociativity
    /// obstruction.</summary>
    public int AssociatorMiddleKey { get; init; } = -1;
    /// <summary>Gets the last key of a failed associativity triple, or <c>-1</c> when this is not a nonassociativity
    /// obstruction.</summary>
    public int AssociatorRightKey { get; init; } = -1;
}
/// <summary>
/// The group regime of a presented algebra: a finite-basis associativity proof, the certificate that every generator
/// is a unit, the inverses those certificates license, and bounded orbit enumeration under the generators.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// <b>It adds no product code.</b> Every statement here is made with the algebra's own <c>Multiply</c>: a witness is a
/// product compared to the unit, an inverse is a product of witnesses that is then multiplied out and checked, and an
/// orbit is a closure under multiplication by the generators. The compiled cells are read only as a filter, to skip the
/// candidates that cannot possibly work; every certificate this type issues rests on a product rather than on a table.
/// </para>
/// <para>
/// <b>Associativity is proved before inverse search.</b> A presentation with no finite basis is refused because this
/// type has no finite certificate for it. At a finite basis both bracketings of every ordered basis triple are
/// multiplied and compared; the first failure is returned as a typed
/// <see cref="ClosureOutcome.BasisNonAssociativityDetected"/> obstruction carrying all three keys. Two-sided
/// generator inverses alone never license a group. The material must also implement
/// <see cref="IExactSemiringMaterial{TValue, TSelf}"/>: extending the basis-triple check to the algebra's bilinear
/// product requires associativity, distributivity, and zero annihilation of its canonical coefficients.
/// </para>
/// <para>
/// <b>The candidate set is what the presentation has.</b> A witness is searched over the whole basis where the
/// presentation has one. A presentation without a finite basis is refused before this search because its product's
/// associativity has not been certified.
/// </para>
/// <para>
/// <b>The coefficient is derived, not searched, wherever it can be.</b> A key's candidate coefficients are the
/// material's one and its negation, which is every coefficient a Clifford, Cayley-Dickson or permutation basis needs;
/// at an <see cref="IFieldMaterial{TValue, TSelf}"/> the coefficient the cell actually requires is computed by
/// inverting the charge that cell carries, so a presentation whose cells are neither signs — a prime-field monogenic
/// basis, say — is answered rather than refused. Deriving proposes; the two-sided product still decides.
/// </para>
/// <para>
/// <b>This is a limit, in the boundary map's sense.</b> A finite basis is required because associativity must be proved,
/// not inferred from invertible generators. Infinite and merely uncompiled word presentations therefore refuse;
/// callers can present a finite multiplication table when they have an independently enumerated group.
/// </para>
/// </remarks>
public sealed class PresentedGroup<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    // The longest normal form a word-level inverse reads. It is the presentation's own word cap, so a key whose word
    // does not fit is a key this library could not have built.
    private const int MaximumWordLength = 256;

    private readonly PresentedAlgebra<TValue, TOps> m_algebra;
    private readonly UnitWitness<TValue>[] m_witnesses;

    private PresentedGroup(PresentedAlgebra<TValue, TOps> algebra, UnitWitness<TValue>[] witnesses) {
        m_algebra = algebra;
        m_witnesses = witnesses;
    }

    /// <summary>Gets the algebra whose group regime this is.</summary>
    public PresentedAlgebra<TValue, TOps> Algebra => m_algebra;
    /// <summary>Gets one unit witness per generator, in symbol order.</summary>
    public ReadOnlySpan<UnitWitness<TValue>> UnitWitnesses => m_witnesses;

    private static GroupObstruction NonAssociativeObstruction(int left, int middle, int right, long triplesChecked) =>
        new(
            BlockedKey: -1L,
            BlockedSymbol: -1,
            Outcome: ClosureOutcome.BasisNonAssociativityDetected,
            PointsReached: triplesChecked
        ) {
            AssociatorLeftKey = left,
            AssociatorMiddleKey = middle,
            AssociatorRightKey = right,
        };
    // Associativity is checked on the whole finite basis before any inverse is sought. Bilinearity makes the ordered
    // basis triples the exact finite certificate needed by the basis-element group regime. Pair products are memoized
    // because every one participates in two complete triple sweeps.
    private static bool TryCertifyAssociativity(PresentedAlgebra<TValue, TOps> algebra, int keyCount, out GroupObstruction obstruction) {
        var compiled = algebra.Compile();
        var cellCount = (keyCount * keyCount);
        var pairCharge = new TValue[cellCount];
        var pairTarget = new int[cellCount];
        var material = algebra.Presentation.Material;
        var one = material.One;
        var singleCharge = new TValue[1];
        var singleLeft = new TValue[1];
        var singleRight = new TValue[1];

        singleLeft[0] = one;
        singleRight[0] = one;

        // Every shipped group basis is monomial: one basis pair lands on one basis key, possibly with a sign. In that
        // regime the complete associativity certificate is a tight table chase with no Element allocation. Retain the
        // general product fallback below for a caller-authored table carrying sums or annihilation.
        var monomial = true;

        for (var left = 0; ((left < keyCount) && monomial); ++left) {
            for (var right = 0; (right < keyCount); ++right) {
                if (1 != compiled.TargetCount(
                    leftKey: left,
                    rightKey: right
                )) {
                    monomial = false;

                    break;
                }

                var slot = ((left * keyCount) + right);

                singleCharge[0] = compiled.Charge(
                    leftKey: left,
                    rightKey: right
                );
                pairCharge[slot] = material.FusedChargedSum(
                    charges: singleCharge,
                    left: singleLeft,
                    right: singleRight,
                    lane: algebra.Presentation.Lane
                );
                pairTarget[slot] = ((int)compiled.Target(
                    leftKey: left,
                    rightKey: right
                ));
            }
        }

        if (monomial) {
            var comparer = EqualityComparer<TValue>.Default;
            var triplesChecked = 0L;

            for (var left = 0; (left < keyCount); ++left) {
                for (var middle = 0; (middle < keyCount); ++middle) {
                    var leftPairSlot = ((left * keyCount) + middle);

                    for (var right = 0; (right < keyCount); ++right) {
                        ++triplesChecked;

                        var rightPairSlot = ((middle * keyCount) + right);
                        var leftCell = ((pairTarget[leftPairSlot] * keyCount) + right);
                        var rightCell = ((left * keyCount) + pairTarget[rightPairSlot]);

                        singleCharge[0] = compiled.Charge(
                            leftKey: pairTarget[leftPairSlot],
                            rightKey: right
                        );
                        singleLeft[0] = pairCharge[leftPairSlot];
                        singleRight[0] = one;

                        var before = material.FusedChargedSum(
                            charges: singleCharge,
                            left: singleLeft,
                            right: singleRight,
                            lane: algebra.Presentation.Lane
                        );

                        singleCharge[0] = compiled.Charge(
                            leftKey: left,
                            rightKey: pairTarget[rightPairSlot]
                        );
                        singleLeft[0] = one;
                        singleRight[0] = pairCharge[rightPairSlot];

                        var after = material.FusedChargedSum(
                            charges: singleCharge,
                            left: singleLeft,
                            right: singleRight,
                            lane: algebra.Presentation.Lane
                        );

                        var beforeZero = material.IsZero(value: before);
                        var afterZero = material.IsZero(value: after);

                        if (
                            (beforeZero && afterZero) ||
                            (!beforeZero && !afterZero && (pairTarget[leftCell] == pairTarget[rightCell]) && comparer.Equals(
                            x: before,
                            y: after
                        ))
                        ) {
                            continue;
                        }

                        obstruction = NonAssociativeObstruction(
                            left: left,
                            middle: middle,
                            right: right,
                            triplesChecked: triplesChecked
                        );

                        return false;
                    }
                }
            }

            obstruction = default;

            return true;
        }

        var basis = new PresentedAlgebra<TValue, TOps>.Element[keyCount];
        var products = new PresentedAlgebra<TValue, TOps>.Element[(keyCount * keyCount)];

        obstruction = default;

        for (var key = 0; (key < keyCount); ++key) {
            basis[key] = algebra.FromSupport(
                coefficients: [one],
                keys: [key]
            );
        }

        for (var left = 0; (left < keyCount); ++left) {
            for (var right = 0; (right < keyCount); ++right) {
                products[((left * keyCount) + right)] = algebra.Multiply(
                    left: basis[left],
                    right: basis[right]
                );
            }
        }

        var fallbackTriplesChecked = 0L;

        for (var left = 0; (left < keyCount); ++left) {
            for (var middle = 0; (middle < keyCount); ++middle) {
                var leftPair = products[((left * keyCount) + middle)];

                for (var right = 0; (right < keyCount); ++right) {
                    ++fallbackTriplesChecked;

                    var before = algebra.Multiply(
                        left: leftPair,
                        right: basis[right]
                    );
                    var after = algebra.Multiply(
                        left: basis[left],
                        right: products[((middle * keyCount) + right)]
                    );

                    if (algebra.AreEqual(
                        left: before,
                        right: after
                    )) { continue; }

                    obstruction = NonAssociativeObstruction(
                        left: left,
                        middle: middle,
                        right: right,
                        triplesChecked: fallbackTriplesChecked
                    );

                    return false;
                }
            }
        }

        return true;
    }
    // The coefficient a candidate would have to carry, read off the product it makes with the generator rather than
    // guessed: if g·b lands on the unit key at charge c, then the only coefficient that can invert g through b is the
    // material's inverse of c. At a field material every unit has one, so a generator whose inverse carries neither
    // sign — which is every generator of a prime-field presentation whose cells are not signs — is reached instead of
    // refused. The answer is a CANDIDATE and never a certificate; the caller multiplies it out on both sides.
    private static bool TryDeriveInverseCharge(
        PresentedAlgebra<TValue, TOps> algebra,
        IFieldMaterial<TValue, TOps> field,
        in PresentedAlgebra<TValue, TOps>.Element element,
        long candidate,
        long identityKey,
        out TValue charge
    ) {
        var product = algebra.Multiply(
            left: element,
            right: algebra.FromSupport(
                keys: [candidate],
                coefficients: [algebra.Presentation.Material.One]
            )
        );

        charge = algebra.Presentation.Material.Zero;

        return (
            (1 == product.SupportCount) &&
            (identityKey == product.Keys[0]) &&
            field.TryInvert(
            value: product.Coefficients[0],
            inverse: out charge
        )
        );
    }

    /// <summary>Certifies that the finite-basis product is associative and every generator is a unit.</summary>
    /// <param name="algebra">The algebra.</param>
    /// <param name="group">On success, the certified group regime.</param>
    /// <param name="obstruction">On failure, the missing exact-material or finite-basis certificate, a basis associator
    /// triple, or the generator that has no inverse and the number of candidates searched.</param>
    /// <returns><see langword="true"/> when associativity and every generator inverse were certified; otherwise
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="algebra"/> is <see langword="null"/>.</exception>
    /// <remarks>The unit must itself be one basis element, since a monomial product of basis elements cannot reach a sum
    /// of orthogonal idempotents — a quiver on more than one object is refused for exactly that reason, and the refusal
    /// names no generator because none of them is individually at fault.</remarks>
    public static bool TryCertify(PresentedAlgebra<TValue, TOps> algebra, out PresentedGroup<TValue, TOps> group, out GroupObstruction obstruction) {
        ArgumentNullException.ThrowIfNull(argument: algebra);

        var identity = algebra.Identity;
        var presentation = algebra.Presentation;
        var material = presentation.Material;

        group = null!;
        obstruction = default;

        if (material is not IExactSemiringMaterial<TValue, TOps>) {
            obstruction = new(
                BlockedKey: -1L,
                BlockedSymbol: -1,
                Outcome: ClosureOutcome.AmbiguityWitness,
                PointsReached: 0L
            );

            return false;
        }

        if (1 != identity.SupportCount) {
            obstruction = new(
                Outcome: ClosureOutcome.AmbiguityWitness,
                BlockedSymbol: -1,
                BlockedKey: -1L,
                PointsReached: identity.SupportCount
            );

            return false;
        }

        var compiled = algebra.Compile();
        var dense = (0 != compiled.KeyCount);

        if (!dense) {
            obstruction = new(
                BlockedKey: -1L,
                BlockedSymbol: -1,
                Outcome: ClosureOutcome.SearchLimitReached,
                PointsReached: 0L
            );

            return false;
        }

        if (!TryCertifyAssociativity(
            algebra: algebra,
            keyCount: compiled.KeyCount,
            obstruction: out obstruction
        )) {
            return false;
        }

        var field = (material as IFieldMaterial<TValue, TOps>);
        var generatorCount = presentation.GeneratorCount;
        var identityKey = identity.Keys[0];
        var negativeOne = material.Zero;
        var one = material.One;
        var signed = (material as ISignedMaterial<TValue, TOps>);
        var witnesses = new UnitWitness<TValue>[generatorCount];

        if (signed is not null) { negativeOne = signed.Negate(value: one); }

        // The coefficients a candidate is tried at: the material's one, its negation where the material is signed, and
        // — at a field material — the one DERIVED from the cell the candidate lands in. Nothing is certified from the
        // derivation; it only proposes the coefficient, and the candidate is still multiplied out on both sides.
        var attempts = new TValue[3];

        // The candidates are every element of the finite basis whose associativity was just proved. They are held as
        // keys so the compiled table can filter impossible inverse cells before the product checks.
        var candidates = new List<long>();

        for (var key = 0L; (key < compiled.KeyCount); ++key) { candidates.Add(item: key); }

        for (var symbol = 0; (symbol < generatorCount); ++symbol) {
            var element = algebra.Generator(symbol: symbol);
            var searched = 0L;
            var found = false;

            if (1 == element.SupportCount) {
                var key = element.Keys[0];

                for (var index = 0; ((index < candidates.Count) && !found); ++index) {
                    var candidate = candidates[index];

                    // The filter, and only a filter: a cell that does not carry the unit alone can hold no inverse, so
                    // the product below is never even formed for it.
                    if (
                        dense &&
                        ((1 != compiled.TargetCount(
                        leftKey: key,
                        rightKey: candidate
                    )) || (identityKey != compiled.Target(
                        leftKey: key,
                        rightKey: candidate
                    )))
                    ) { continue; }

                    ++searched;

                    var attemptCount = 1;

                    attempts[0] = one;

                    if (signed is not null) { attempts[attemptCount++] = negativeOne; }

                    if (
                        (field is not null) &&
                        TryDeriveInverseCharge(
                        algebra: algebra,
                        candidate: candidate,
                        charge: out var derived,
                        element: element,
                        field: field,
                        identityKey: identityKey
                    )
                    ) {
                        attempts[attemptCount++] = derived;
                    }

                    for (var attempt = 0; ((attempt < attemptCount) && !found); ++attempt) {
                        var charge = attempts[attempt];
                        var inverse = algebra.FromSupport(
                            coefficients: [charge],
                            keys: [candidate]
                        );

                        if (
                            !algebra.AreEqual(
                            left: algebra.Multiply(
                                left: element,
                                right: inverse
                            ),
                            right: identity
                        ) ||
                            !algebra.AreEqual(
                            left: algebra.Multiply(
                                left: inverse,
                                right: element
                            ),
                            right: identity
                        )
                        ) {
                            continue;
                        }

                        found = true;
                        witnesses[symbol] = new(
                            InverseCharge: charge,
                            InverseKey: candidate,
                            Symbol: symbol
                        );
                    }
                }
            }

            if (!found) {
                obstruction = new(
                    BlockedKey: -1L,
                    BlockedSymbol: symbol,
                    Outcome: ClosureOutcome.AmbiguityWitness,
                    PointsReached: searched
                );

                return false;
            }
        }

        group = new(
            algebra: algebra,
            witnesses: witnesses
        );

        return true;
    }
    /// <summary>Enumerates one basis element's orbit under the generators, bounded.</summary>
    /// <param name="seedKey">The normal-form key to close.</param>
    /// <param name="searchLimit">The largest orbit to admit; the enumeration stops there and refuses.</param>
    /// <param name="orbit">On success, the orbit's normal-form keys, ascending.</param>
    /// <param name="obstruction">On failure, <see cref="ClosureOutcome.SearchLimitReached"/> and the size reached, or
    /// <see cref="ClosureOutcome.AmbiguityWitness"/> and the step whose image was not a single basis element.</param>
    /// <returns><see langword="true"/> when the whole orbit fit the limit; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="searchLimit"/> is below one, or the seed names no
    /// normal form of a finite presentation.</exception>
    /// <exception cref="InvalidOperationException">The presentation has no finite basis and a step outgrew its
    /// normalization budget or its key scheme, which is <see cref="PresentedAlgebra{TValue, TOps}.Multiply"/>'s own
    /// contract; a limit the key scheme can hold is the caller's to choose.</exception>
    /// <remarks>The orbit is a set of keys. A step's charge is not part of it, so a Clifford basis whose products carry
    /// a sign has the same orbit as one whose products do not.</remarks>
    public bool TryEnumerateOrbit(long seedKey, long searchLimit, out ReadOnlyMemory<long> orbit, out GroupObstruction obstruction) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: searchLimit,
            other: 1L
        );
        ArgumentOutOfRangeException.ThrowIfNegative(value: seedKey);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: seedKey,
            other: m_algebra.Presentation.NormalFormCount
        );

        var algebra = m_algebra;
        var one = algebra.Presentation.Material.One;
        var frontier = new List<long> { seedKey };
        var sorted = new List<long> { seedKey };

        orbit = default;
        obstruction = default;

        for (var cursor = 0; (cursor < frontier.Count); ++cursor) {
            var point = algebra.FromSupport(
                keys: [frontier[cursor]],
                coefficients: [one]
            );

            for (var symbol = 0; (symbol < m_witnesses.Length); ++symbol) {
                var image = algebra.Multiply(
                    left: point,
                    right: algebra.Generator(symbol: symbol)
                );

                if (1 != image.SupportCount) {
                    obstruction = new(
                        Outcome: ClosureOutcome.AmbiguityWitness,
                        BlockedSymbol: symbol,
                        BlockedKey: frontier[cursor],
                        PointsReached: frontier.Count
                    );

                    return false;
                }

                var key = image.Keys[0];
                var slot = sorted.BinarySearch(item: key);

                if (slot >= 0) { continue; }

                if (frontier.Count >= searchLimit) {
                    obstruction = new(
                        Outcome: ClosureOutcome.SearchLimitReached,
                        BlockedSymbol: symbol,
                        BlockedKey: key,
                        PointsReached: frontier.Count
                    );

                    return false;
                }

                sorted.Insert(
                    index: ~slot,
                    item: key
                );
                frontier.Add(item: key);
            }
        }

        orbit = sorted.ToArray();

        return true;
    }
    /// <summary>Inverts one basis element, by inverting its word letter by letter and multiplying the result out.</summary>
    /// <param name="value">The element to invert; one basis element carrying the material's one.</param>
    /// <param name="inverse">On success, the two-sided inverse.</param>
    /// <param name="obstruction">On failure, the key that blocked and how much of its word was consumed.</param>
    /// <returns><see langword="true"/> when the inverse was multiplied out and found to be one; otherwise
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">The element belongs to another algebra.</exception>
    /// <remarks>
    /// <para>
    /// The inverse of a product is the reversed product of the inverses, so the word <c>g₁…gₙ</c> inverts to
    /// <c>gₙ⁻¹…g₁⁻¹</c> through the unit witnesses. Admission has already proved associativity, but the candidate is
    /// still multiplied out against the element on both sides before it is returned. A <see langword="true"/> is
    /// therefore a checked answer, and a presentation whose rewriting cannot see the cancellation is refused rather
    /// than answered wrongly.
    /// </para>
    /// <para>
    /// The algebra carries no <c>Inverse</c> of its own, and this does not add one: it inverts a basis element, which is
    /// a group element, never a general sum. Inverting a sum is a linear solve and is
    /// <see cref="PresentedAlgebra{TValue, TOps}.TrySolve"/>'s job, at a field material.
    /// </para>
    /// </remarks>
    public bool TryInvert(in PresentedAlgebra<TValue, TOps>.Element value, out PresentedAlgebra<TValue, TOps>.Element inverse, out GroupObstruction obstruction) {
        var algebra = m_algebra;
        var identity = algebra.Identity;
        var presentation = algebra.Presentation;

        algebra.RequireOwned(
            value: value,
            paramName: nameof(value)
        );

        inverse = algebra.Zero;
        obstruction = default;

        if (
            (1 != value.SupportCount) ||
            !EqualityComparer<TValue>.Default.Equals(
            x: value.Coefficients[0],
            y: presentation.Material.One
        )
        ) {
            obstruction = new(
                Outcome: ClosureOutcome.AmbiguityWitness,
                BlockedSymbol: -1,
                BlockedKey: ((0 == value.SupportCount)
                ? -1L
                : value.Keys[0]),
                PointsReached: value.SupportCount
            );

            return false;
        }

        var key = value.Keys[0];

        Span<int> word = stackalloc int[MaximumWordLength];

        if (!presentation.TryWordOf(
            key: key,
            length: out var length,
            word: word
        )) {
            obstruction = new(
                BlockedKey: key,
                BlockedSymbol: -1,
                Outcome: ClosureOutcome.AmbiguityWitness,
                PointsReached: 0L
            );

            return false;
        }

        var product = identity;

        for (var index = (length - 1); (index >= 0); --index) {
            var letter = word[index];

            if (
                (letter < 0) ||
                (letter >= m_witnesses.Length)
            ) {
                obstruction = new(
                    BlockedKey: key,
                    BlockedSymbol: letter,
                    Outcome: ClosureOutcome.AmbiguityWitness,
                    PointsReached: (length - index)
                );

                return false;
            }

            var witness = m_witnesses[letter];

            product = algebra.Multiply(
                left: product,
                right: algebra.FromSupport(
                    keys: [witness.InverseKey],
                    coefficients: [witness.InverseCharge]
                )
            );
        }

        if (
            !algebra.AreEqual(
            left: algebra.Multiply(
                left: value,
                right: product
            ),
            right: identity
        ) ||
            !algebra.AreEqual(
            left: algebra.Multiply(
                left: product,
                right: value
            ),
            right: identity
        )
        ) {
            obstruction = new(
                BlockedKey: key,
                BlockedSymbol: -1,
                Outcome: ClosureOutcome.AmbiguityWitness,
                PointsReached: length
            );

            return false;
        }

        inverse = product;

        return true;
    }
}
