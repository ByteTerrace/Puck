namespace Puck.Maths;

/// <summary>
/// A carrier closed under one narrowing operation: <see cref="Meet"/> returns the greatest lower bound of its operands
/// in the carrier's own order (<see cref="IsAtMost"/>), and is idempotent, commutative and associative, with
/// <see cref="Top"/> (unrestricted) as its identity and <see cref="Bottom"/> (nothing) as its absorbing element. The
/// meet can only narrow, never widen — <c>Meet(a, b)</c> is at most <c>a</c> and at most <c>b</c> — and because the
/// operation is associative and commutative, folding a chain of envelopes yields one result independent of the order
/// the envelopes are folded in. That pair of facts is what makes an attenuation pipeline well defined: each link of a
/// delegation chain contributes an envelope, the chain's effective envelope is the meet of all of them, and no link
/// can grant more than any other link allows.
/// </summary>
/// <remarks>
/// <para><b>The attenuation pipeline only — an authority decision is deliberately NOT modelled here.</b> A decision
/// that resolves competing claims is not a semilattice: exclusivity conflicts are order-dependent, a verdict must
/// report which rule decided it, and state transitions between grants do not commute. This family codifies only the
/// lawful narrowing of envelopes along a delegation chain; the decision that consumes the narrowed envelope stays with
/// the consumer, in whatever order-sensitive form it needs.</para>
/// <para><b>Intended consumers.</b> The shipped carriers name the narrowing steps that recur through the engine's
/// authority plumbing: the channel fold's Reach ∧ Consent meet (<c>Puck.World</c>'s <c>ChannelReachMask.Meet</c>), the
/// addon dispatch door's manifest ∧ grant ∧ verb-mask triple gate (<c>AddonCapabilityMask</c>'s
/// <c>requested &amp; granted</c> arithmetic), grant metering's min of a dispatch budget and a structural cap (such as
/// an output-cell count), and per-tick fuel narrowing. Subject-set intersection is <see cref="MeetMask64"/> over
/// membership bits.</para>
/// </remarks>
/// <typeparam name="TSelf">The implementing carrier, carried as a curiously-recurring type parameter so the static
/// operations return the concrete carrier.</typeparam>
public interface IMeetSemilattice<TSelf>
    where TSelf : struct, IMeetSemilattice<TSelf> {
    /// <summary>Gets the least element — "nothing": the absorbing element of <see cref="Meet"/>, at most every other
    /// element.</summary>
    static abstract TSelf Bottom { get; }
    /// <summary>Gets the greatest element — "unrestricted": the identity of <see cref="Meet"/>, at least every other
    /// element.</summary>
    static abstract TSelf Top { get; }

    /// <summary>Returns the meet — the greatest lower bound of the two operands in the carrier's order.</summary>
    /// <param name="left">The first envelope.</param>
    /// <param name="right">The second envelope.</param>
    /// <returns>The widest envelope that is at most <paramref name="left"/> and at most <paramref name="right"/>.</returns>
    static abstract TSelf Meet(TSelf left, TSelf right);

    /// <summary>Decides the carrier's order: whether this envelope allows no more than <paramref name="other"/>.</summary>
    /// <param name="other">The envelope compared against.</param>
    /// <returns><see langword="true"/> when this envelope is at most <paramref name="other"/>; equivalently, when
    /// <c>Meet(this, other)</c> equals this envelope.</returns>
    bool IsAtMost(TSelf other);
}

/// <summary>A 64-bit bit-mask envelope: an element per bit, meet is bitwise AND, and the order is bit-subset. The
/// carrier for capability masks and small subject sets — every attenuation step clears bits or leaves them, and can
/// never set one.</summary>
/// <remarks>See <see cref="IMeetSemilattice{TSelf}"/> for the laws, the intended consumers, and the deliberate boundary
/// between this attenuation algebra and an authority decision.</remarks>
/// <param name="Bits">The mask's raw bits — bit <c>i</c> set means element <c>i</c> is allowed.</param>
public readonly record struct MeetMask64(ulong Bits) : IMeetSemilattice<MeetMask64> {
    /// <summary>Gets the empty mask — no bit set: the absorbing element, allowing nothing.</summary>
    public static MeetMask64 Bottom => new(Bits: 0UL);
    /// <summary>Gets the full mask — every bit set: the identity, restricting nothing.</summary>
    public static MeetMask64 Top => new(Bits: ulong.MaxValue);

    /// <summary>Returns the bitwise AND of the two masks — the bits both allow.</summary>
    /// <param name="left">The first mask.</param>
    /// <param name="right">The second mask.</param>
    /// <returns>The intersection mask.</returns>
    public static MeetMask64 Meet(MeetMask64 left, MeetMask64 right) =>
        new(Bits: left.Bits & right.Bits);

    /// <summary>Decides bit-subset order: whether every bit set here is also set in <paramref name="other"/>.</summary>
    /// <param name="other">The mask compared against.</param>
    /// <returns><see langword="true"/> when this mask allows no bit <paramref name="other"/> refuses.</returns>
    public bool IsAtMost(MeetMask64 other) =>
        ((Bits & ~other.Bits) == 0UL);
}

/// <summary>A non-negative 64-bit quantity envelope: meet is the minimum, and the order is numeric. The carrier for
/// quantitative bounds — budgets, ceilings, fuel shares, structural caps — where every attenuation step can lower a
/// limit and never raise it. Every integral bound in use embeds into the unsigned 64-bit carrier, so one width serves
/// them all; the carrier is total, with no invalid value to refuse.</summary>
/// <remarks>See <see cref="IMeetSemilattice{TSelf}"/> for the laws, the intended consumers, and the deliberate boundary
/// between this attenuation algebra and an authority decision.</remarks>
/// <param name="Amount">The quantity — how much the envelope allows.</param>
public readonly record struct MeetQuantity64(ulong Amount) : IMeetSemilattice<MeetQuantity64> {
    /// <summary>Gets the zero quantity — the absorbing element, allowing nothing.</summary>
    public static MeetQuantity64 Bottom => new(Amount: 0UL);
    /// <summary>Gets the maximum quantity — the identity, bounding nothing the carrier can express.</summary>
    public static MeetQuantity64 Top => new(Amount: ulong.MaxValue);

    /// <summary>Returns the smaller of the two quantities.</summary>
    /// <param name="left">The first quantity.</param>
    /// <param name="right">The second quantity.</param>
    /// <returns>The minimum.</returns>
    public static MeetQuantity64 Meet(MeetQuantity64 left, MeetQuantity64 right) =>
        new(Amount: Math.Min(val1: left.Amount, val2: right.Amount));

    /// <summary>Decides numeric order: whether this quantity is no larger than <paramref name="other"/>.</summary>
    /// <param name="other">The quantity compared against.</param>
    /// <returns><see langword="true"/> when this quantity does not exceed <paramref name="other"/>.</returns>
    public bool IsAtMost(MeetQuantity64 other) =>
        (Amount <= other.Amount);
}

/// <summary>The product of two meet-semilattices, which is itself a meet-semilattice: meet, top, bottom and order are
/// all componentwise, so a compound envelope — a capability mask beside a budget, say — attenuates as one value under
/// the same laws each component obeys alone. The construction stacks: either component may itself be a
/// <see cref="MeetProduct{TFirst, TSecond}"/>, so an envelope of any width is built by nesting pairs.</summary>
/// <remarks>See <see cref="IMeetSemilattice{TSelf}"/> for the laws, the intended consumers, and the deliberate boundary
/// between this attenuation algebra and an authority decision.</remarks>
/// <typeparam name="TFirst">The first component's carrier.</typeparam>
/// <typeparam name="TSecond">The second component's carrier.</typeparam>
/// <param name="First">The first component.</param>
/// <param name="Second">The second component.</param>
public readonly record struct MeetProduct<TFirst, TSecond>(TFirst First, TSecond Second)
    : IMeetSemilattice<MeetProduct<TFirst, TSecond>>
    where TFirst : struct, IMeetSemilattice<TFirst>
    where TSecond : struct, IMeetSemilattice<TSecond> {
    /// <summary>Gets the pair of component bottoms — the absorbing element, allowing nothing in either component.</summary>
    public static MeetProduct<TFirst, TSecond> Bottom => new(
        First: TFirst.Bottom,
        Second: TSecond.Bottom
    );
    /// <summary>Gets the pair of component tops — the identity, restricting nothing in either component.</summary>
    public static MeetProduct<TFirst, TSecond> Top => new(
        First: TFirst.Top,
        Second: TSecond.Top
    );

    /// <summary>Returns the componentwise meet.</summary>
    /// <param name="left">The first envelope.</param>
    /// <param name="right">The second envelope.</param>
    /// <returns>The pair of component meets.</returns>
    public static MeetProduct<TFirst, TSecond> Meet(MeetProduct<TFirst, TSecond> left, MeetProduct<TFirst, TSecond> right) =>
        new(
        First: TFirst.Meet(left: left.First, right: right.First),
        Second: TSecond.Meet(left: left.Second, right: right.Second)
    );

    /// <summary>Decides the componentwise order: at most in BOTH components.</summary>
    /// <param name="other">The envelope compared against.</param>
    /// <returns><see langword="true"/> when both components are at most <paramref name="other"/>'s.</returns>
    public bool IsAtMost(MeetProduct<TFirst, TSecond> other) =>
        (First.IsAtMost(other: other.First) && Second.IsAtMost(other: other.Second));
}
