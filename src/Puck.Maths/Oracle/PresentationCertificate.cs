namespace Puck.Maths;

/// <summary>The outcome of a bounded structural computation. Presentation certification uses the basis-associativity
/// outcomes; other bounded Oracle operations use the generic obstruction and limit outcomes.</summary>
public enum ClosureOutcome {
    /// <summary>The compiled product associated on every ordered basis triple, and every other certificate pass
    /// completed inside the verification budget. This says nothing about confluence of the declared rewrite
    /// relation.</summary>
    BasisAssociativityVerified = 0,
    /// <summary>The compiled product failed associativity on at least one ordered basis triple.</summary>
    BasisNonAssociativityDetected = 3,
    /// <summary>A structure-specific obstruction was found and returned as data rather than as a fault.</summary>
    AmbiguityWitness = 1,
    /// <summary>The budget ran out before verification finished, which is distinct from proving a law or finding a
    /// counterexample.</summary>
    SearchLimitReached = 2,
}
/// <summary>The certificate a guarded sum over all lengths runs under. The operation degrades by certificate; it is
/// never offered on an assumption.</summary>
public enum ClosureCertificate {
    /// <summary>No certificate: the infinite sum is refused and only the exact finite truncation is available.</summary>
    None,
    /// <summary>Some power of the element is zero, so the sum is a finite exact total.</summary>
    Nilpotent,
    /// <summary>The grading and the presentation window leave only finitely many nonzero lengths.</summary>
    LocallyFinite,
    /// <summary>The material's addition is idempotent, so the partial sums stabilize.</summary>
    Idempotent,
    /// <summary>The material is a certified field and the resolvent is obtained by an exact solve.</summary>
    FieldResolvent,
}
/// <summary>The unresolved associator of one ordered basis triple, carried as the charge it puts on the leading term of
/// <c>(a·b)·c − a·(b·c)</c>.</summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <param name="Left">The first basis key.</param>
/// <param name="Middle">The second basis key.</param>
/// <param name="Right">The third basis key.</param>
/// <param name="Charge">The associator's coefficient on its lowest-key support entry.</param>
public readonly record struct AssociatorCharge<TValue>(int Left, int Middle, int Right, TValue Charge);
/// <summary>One ordered basis triple at which a hexagon identity fails, carried as the two charges that disagree.</summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <param name="Left">The first basis key.</param>
/// <param name="Middle">The second basis key.</param>
/// <param name="Right">The third basis key.</param>
/// <param name="Nested">The commutation charge the folded pair carries.</param>
/// <param name="Flat">The product of the commutation charges the two factors carry.</param>
/// <remarks>Both hexagons are indexed by the same ordered triple — the first folds the leading pair against
/// <paramref name="Right"/>, the second folds the trailing pair against <paramref name="Left"/> — so one record shape
/// serves both, exactly as one <see cref="CoherenceWitness{TValue}"/> serves both rebalancing routes.</remarks>
public readonly record struct BraidingWitness<TValue>(int Left, int Middle, int Right, TValue Nested, TValue Flat);
/// <summary>An ordered pair of basis keys whose product is zero.</summary>
/// <param name="LeftKey">The left basis key.</param>
/// <param name="RightKey">The right basis key.</param>
public readonly record struct ZeroDivisorWitness(long LeftKey, long RightKey);
/// <summary>One ordered basis quadruple whose two rebalancing routes charge differently, so the charge a bracketing
/// picks up would depend on the order its brackets were spliced away.</summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <param name="First">The first basis key.</param>
/// <param name="Second">The second basis key.</param>
/// <param name="Third">The third basis key.</param>
/// <param name="Fourth">The fourth basis key.</param>
/// <param name="Nested">The charge of the route that splices the innermost bracket first.</param>
/// <param name="Flat">The charge of the route that splices the outermost bracket first.</param>
public readonly record struct CoherenceWitness<TValue>(int First, int Second, int Third, int Fourth, TValue Nested, TValue Flat);
/// <summary>The refusal a guarded sum over all lengths returns when no certificate could be issued.</summary>
/// <param name="Attempted">The certificate the material licensed an attempt at.</param>
/// <param name="SupportKey">A key still moving when the attempt was cut off, or zero when the sum had emptied.</param>
/// <param name="StepsTaken">The number of lengths accumulated before the attempt was cut off.</param>
public readonly record struct SumClosureObstruction(ClosureCertificate Attempted, long SupportKey, long StepsTaken);
/// <summary>The refusal a bounded normalization returns.</summary>
/// <param name="StepsTaken">The number of rewrite steps taken before the bound was reached.</param>
/// <param name="BlockedKey">The packed key of the term that could not be reduced, or <c>-1</c> when the term outgrew
/// the key scheme entirely.</param>
public readonly record struct NormalizationObstruction(long StepsTaken, long BlockedKey);
/// <summary>
/// The computed law certificates of one presentation. Every flag is proved over the presentation's own basis; none is
/// assumed, and the operation surface degrades by certificate rather than by a hardcoded assumption.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
public readonly struct PresentationCertificate<TValue> {
    private readonly AssociatorCharge<TValue>[] m_associatorWitness;
    private readonly TValue[] m_braidingCharge;
    private readonly int m_braidingKeys;
    private readonly BraidingWitness<TValue>[] m_braidingWitness;
    private readonly CoherenceWitness<TValue>[] m_coherenceWitness;
    private readonly ZeroDivisorWitness[] m_zeroDivisorWitness;

    internal PresentationCertificate(
        ClosureOutcome outcome,
        bool isAlternative,
        bool isAssociative,
        bool isBraided,
        bool isCoherent,
        bool isCommutative,
        bool isSymmetric,
        bool hasIdentity,
        long nonAssociativeTripleCount,
        int braidingKeys,
        AssociatorCharge<TValue>[] associatorWitness,
        TValue[] braidingCharge,
        BraidingWitness<TValue>[] braidingWitness,
        CoherenceWitness<TValue>[] coherenceWitness,
        ZeroDivisorWitness[] zeroDivisorWitness
    ) {
        NonAssociativeTripleCount = nonAssociativeTripleCount;
        HasIdentity = hasIdentity;
        IsAlternative = isAlternative;
        IsAssociative = isAssociative;
        IsBraided = isBraided;
        IsCoherent = isCoherent;
        IsCommutative = isCommutative;
        IsSymmetric = isSymmetric;
        Outcome = outcome;
        m_associatorWitness = associatorWitness;
        m_braidingCharge = braidingCharge;
        m_braidingKeys = braidingKeys;
        m_braidingWitness = braidingWitness;
        m_coherenceWitness = coherenceWitness;
        m_zeroDivisorWitness = zeroDivisorWitness;
    }

    /// <summary>
    /// Gets the nonzero associators as charges on basis triples — the associator 3-cochain. Nonempty exactly at the
    /// quasialgebra floors over a signed exact material.
    /// </summary>
    /// <remarks>
    /// Over an exact material this measures coherence. Over the rounding carrier it does not: every algebra in this
    /// library already fails associativity bitwise because each returned component carries its own rounding, so on
    /// <see cref="FixedQ4816"/> a nonzero entry may be rounding noise rather than a 3-cochain. Read the quasialgebra
    /// regime on an exact material.
    /// <para>An unsigned material has no subtraction, so no charge can be formed; the witness is then empty and
    /// <see cref="NonAssociativeTripleCount"/> alone reports the failures, counted by inequality.</para>
    /// </remarks>
    public ReadOnlySpan<AssociatorCharge<TValue>> AssociatorWitness => m_associatorWitness;
    /// <summary>
    /// Gets the ordered basis triples at which a hexagon identity fails — the braiding's incoherence, carried as
    /// charges rather than thrown. Nonempty exactly where a derived commutation charge is not a bicharacter.
    /// </summary>
    /// <remarks>Empty is not by itself a proof: a pair whose commutation charge could not be derived states no identity
    /// to fail, so read <see cref="IsBraided"/> for the proved flag and <see cref="BraidingCharge"/> for where the
    /// derivation stopped. A degenerate Clifford signature is the shipped instance of exactly that — its annihilating
    /// pairs constrain no charge, so it reports no braiding and witnesses nothing, where the incoherent braiding this
    /// span carries is the octonion floor's.</remarks>
    public ReadOnlySpan<BraidingWitness<TValue>> BraidingWitness => m_braidingWitness;
    /// <summary>
    /// Gets the ordered basis quadruples whose two rebalancing routes charge differently. Empty exactly when
    /// <see cref="IsCoherent"/> holds.
    /// </summary>
    public ReadOnlySpan<CoherenceWitness<TValue>> CoherenceWitness => m_coherenceWitness;
    /// <summary>Gets a value indicating whether the unit acts as an identity on every basis element.</summary>
    public bool HasIdentity { get; }
    /// <summary>Gets a value indicating whether the associator vanishes whenever two of its three arguments coincide.</summary>
    public bool IsAlternative { get; }
    /// <summary>Gets a value indicating whether the product associates on every ordered basis triple examined.</summary>
    public bool IsAssociative { get; }
    /// <summary>
    /// Gets a value indicating whether the product carries a braiding: every ordered basis pair has a derived
    /// commutation charge, and those charges satisfy both hexagon identities on every ordered basis triple examined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived, never declared. The charge of an ordered pair is the coefficient <c>c</c> with
    /// <c>a·b = c·(b·a)</c>, searched over the material's one, its negation, and — at an
    /// <see cref="IFieldMaterial{TValue, TSelf}"/> — the coefficient the two cells' own leading charges name. The
    /// search proposes and the scaled comparison decides, so the reported braiding is the product's own rather than a
    /// datum about it, which is strictly more than <see cref="IsCoherent"/> reports about the declared associator.
    /// </para>
    /// <para>
    /// It is a limit: where none of the three candidates works the pair issues no charge, no flag is issued, and the
    /// missing coefficient is readable as a zero from <see cref="BraidingCharge"/>. That is a shrunk guarantee and not
    /// a wrong answer — a noncommutative group algebra, whose two orderings land on different basis keys, has no
    /// commutation charge at all and says so.
    /// </para>
    /// <para>
    /// A pair that annihilates both ways is the second such case, and it is a limit for the opposite reason: every
    /// coefficient relates the two zeros, so the pair determines none. No charge is issued there either, which is why a
    /// degenerate Clifford signature reports no braiding — its annihilating pairs leave the derivation incomplete —
    /// rather than reporting the hexagon failures an arbitrarily chosen charge would manufacture.
    /// </para>
    /// </remarks>
    public bool IsBraided { get; }
    /// <summary>
    /// Gets a value indicating whether the presentation's re-association charges are coherent: the charge a
    /// bracketing picks up depends only on the bracketing, never on the order its brackets were spliced away.
    /// </summary>
    /// <remarks>
    /// Proved on every ordered basis quadruple examined, by charging its two rebalancing routes and comparing them. It
    /// is a statement about the declared charges rather than about the product, so a presentation whose uniform charge
    /// is one is coherent outright and a declared 3-cochain is coherent exactly when it satisfies that quadruple
    /// identity. A presentation with no finite basis proves nothing and reports <see langword="false"/>, as every other
    /// flag here does.
    /// <para>The quadruple identity is the whole remaining condition because the other coherence axiom — that the
    /// charges are normalized at the unit — is enforced at construction, where a charge sitting at the unit is refused
    /// with an impossibility argument rather than certified afterwards. That is why a uniform charge that is not one
    /// never reaches this flag: it names no presentation. Certifying it here instead would be unsound over a rounding
    /// carrier, where a small charge cubed and squared truncate to the same value and the identity holds vacuously.</para>
    /// </remarks>
    public bool IsCoherent { get; }
    /// <summary>Gets a value indicating whether the product commutes on every ordered basis pair examined.</summary>
    public bool IsCommutative { get; }
    /// <summary>Gets a value indicating whether the braiding is symmetric: <see cref="IsBraided"/> holds and every
    /// ordered basis pair's commutation charge equals its mirror's.</summary>
    /// <remarks>A graded-commutative regime — every nondegenerate Clifford signature, every Cayley-Dickson floor
    /// through the quaternions — is symmetric, because its charges are signs and a sign is its own mirror. A charge
    /// that is a root of unity of higher order is not, which is what separates a braiding from a symmetry and is why
    /// the two flags are reported apart rather than as one. A degenerate signature is symmetric everywhere it is
    /// constrained and reports <see langword="false"/> anyway, because the flag is a proof and its annihilating pairs
    /// leave one unfinished.</remarks>
    public bool IsSymmetric { get; }
    /// <summary>Gets the number of ordered basis triples whose associator is nonzero.</summary>
    public long NonAssociativeTripleCount { get; }
    /// <summary>Gets the outcome of bounded law verification over the compiled finite-basis product.</summary>
    public ClosureOutcome Outcome { get; }
    /// <summary>Gets the ordered basis pairs whose product is zero.</summary>
    public ReadOnlySpan<ZeroDivisorWitness> ZeroDivisorWitness => m_zeroDivisorWitness;

    /// <summary>Returns the commutation charge derived for one ordered basis pair — the coefficient <c>c</c> with
    /// <c>a·b = c·(b·a)</c>.</summary>
    /// <param name="leftKey">The left basis key.</param>
    /// <param name="rightKey">The right basis key.</param>
    /// <returns>The charge, or the material's zero where no coefficient could be derived.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A key names no normal form of the certified presentation, which
    /// includes every key of a presentation with no finite basis.</exception>
    /// <remarks>
    /// The material's zero reads unambiguously as "not derived", because no derived charge can be zero: the candidates
    /// are the material's one, its negation, and a quotient of two nonzero field coefficients, and none of the three is
    /// the zero of any material this library carries.
    /// <para>"Not derived" covers three cases and does not separate them, so read it as the absence of a charge and
    /// never as a fact about the pair: the pair annihilates both ways and so constrains no coefficient; none of the
    /// three candidates related the two cells; or the basis-law walk ran out of <c>overlapLimit</c> before reaching the pair at
    /// all, which <see cref="Outcome"/> reports as <see cref="ClosureOutcome.SearchLimitReached"/>. A truncated
    /// certificate therefore reads zero at pairs a complete one charges, which is the budget showing through.</para>
    /// </remarks>
    public TValue BraidingCharge(long leftKey, long rightKey) {
        if (
            (leftKey < 0L) ||
            (leftKey >= m_braidingKeys)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(leftKey),
                message: "The key names no normal form of the certified presentation."
            );
        }

        if (
            (rightKey < 0L) ||
            (rightKey >= m_braidingKeys)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(rightKey),
                message: "The key names no normal form of the certified presentation."
            );
        }

        return m_braidingCharge[((leftKey * m_braidingKeys) + rightKey)];
    }
}
