namespace Puck.Maths.Tests;

/// <summary>Claim bodies for the <c>meet</c> family — the attenuation carriers <see cref="MeetMask64"/>,
/// <see cref="MeetQuantity64"/> and <see cref="MeetProduct{TFirst, TSecond}"/>. Every body sweeps all three shipped
/// carriers (the product closed at mask × quantity, the envelope shape the intended consumers pair), maps domain lanes
/// to carrier raws by plain bit reinterpretation so the committed edge battery lands on <c>0</c>,
/// <c>ulong.MaxValue</c> (from <c>−1</c>) and the off-by-ones around every power-of-two seam, and returns the
/// counterexample text or <see langword="null"/>. The independent order spellings used by the never-widens body live
/// here and never call a subject member.</summary>
internal static class MeetClaims {
    /// <summary>Meet is absorbed by <c>Bottom</c> on both sides, on every carrier: meeting with "nothing" yields
    /// nothing, whichever side the nothing arrives on.</summary>
    /// <param name="left">The first lane vector (width 2).</param>
    /// <param name="right">The second lane vector (width 2).</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BottomAbsorbs(long[] left, long[] right) {
        Span<long> raws = [left[0], left[1], right[0], right[1]];

        foreach (var raw in raws) {
            var mask = Mask(lane: raw);

            if ((MeetMask64.Meet(left: mask, right: MeetMask64.Bottom) != MeetMask64.Bottom) || (MeetMask64.Meet(left: MeetMask64.Bottom, right: mask) != MeetMask64.Bottom)) {
                return $"MeetMask64.Bottom failed to absorb at bits {mask.Bits:x}";
            }

            var quantity = Quantity(lane: raw);

            if ((MeetQuantity64.Meet(left: quantity, right: MeetQuantity64.Bottom) != MeetQuantity64.Bottom) || (MeetQuantity64.Meet(left: MeetQuantity64.Bottom, right: quantity) != MeetQuantity64.Bottom)) {
                return $"MeetQuantity64.Bottom failed to absorb at amount {quantity.Amount}";
            }
        }

        var envelope = Envelope(maskLane: left[0], amountLane: right[1]);
        var bottom = MeetProduct<MeetMask64, MeetQuantity64>.Bottom;

        if ((MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: envelope, right: bottom) != bottom) || (MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: bottom, right: envelope) != bottom)) {
            return $"MeetProduct.Bottom failed to absorb at ({envelope.First.Bits:x}, {envelope.Second.Amount})";
        }

        return null;
    }

    /// <summary>Meet is associative on every carrier: a delegation chain folds to one envelope however it is
    /// bracketed.</summary>
    /// <param name="left">The first lane vector (width 3).</param>
    /// <param name="right">The second lane vector (width 3).</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MeetIsAssociative(long[] left, long[] right) {
        Span<(long A, long B, long C)> triples = [(left[0], left[1], left[2]), (right[0], right[1], right[2]), (left[0], right[1], left[2])];

        foreach (var (a, b, c) in triples) {
            var (maskA, maskB, maskC) = (Mask(lane: a), Mask(lane: b), Mask(lane: c));

            if (MeetMask64.Meet(left: MeetMask64.Meet(left: maskA, right: maskB), right: maskC) != MeetMask64.Meet(left: maskA, right: MeetMask64.Meet(left: maskB, right: maskC))) {
                return $"MeetMask64.Meet is not associative at bits ({maskA.Bits:x}, {maskB.Bits:x}, {maskC.Bits:x})";
            }

            var (quantityA, quantityB, quantityC) = (Quantity(lane: a), Quantity(lane: b), Quantity(lane: c));

            if (MeetQuantity64.Meet(left: MeetQuantity64.Meet(left: quantityA, right: quantityB), right: quantityC) != MeetQuantity64.Meet(left: quantityA, right: MeetQuantity64.Meet(left: quantityB, right: quantityC))) {
                return $"MeetQuantity64.Meet is not associative at amounts ({quantityA.Amount}, {quantityB.Amount}, {quantityC.Amount})";
            }
        }

        var envelopeA = Envelope(maskLane: left[0], amountLane: right[0]);
        var envelopeB = Envelope(maskLane: left[1], amountLane: right[1]);
        var envelopeC = Envelope(maskLane: left[2], amountLane: right[2]);
        var leftFold = MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: envelopeA, right: envelopeB), right: envelopeC);
        var rightFold = MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: envelopeA, right: MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: envelopeB, right: envelopeC));

        return ((leftFold == rightFold)
            ? null
            : $"MeetProduct.Meet is not associative at masks ({envelopeA.First.Bits:x}, {envelopeB.First.Bits:x}, {envelopeC.First.Bits:x}), amounts ({envelopeA.Second.Amount}, {envelopeB.Second.Amount}, {envelopeC.Second.Amount})");
    }

    /// <summary>Meet is commutative on every carrier: the two links of a delegation step contribute symmetrically.</summary>
    /// <param name="left">The first lane vector (width 2).</param>
    /// <param name="right">The second lane vector (width 2).</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MeetIsCommutative(long[] left, long[] right) {
        Span<(long A, long B)> pairs = [(left[0], right[0]), (left[1], right[1]), (left[0], right[1])];

        foreach (var (a, b) in pairs) {
            var (maskA, maskB) = (Mask(lane: a), Mask(lane: b));

            if (MeetMask64.Meet(left: maskA, right: maskB) != MeetMask64.Meet(left: maskB, right: maskA)) {
                return $"MeetMask64.Meet is not commutative at bits ({maskA.Bits:x}, {maskB.Bits:x})";
            }

            var (quantityA, quantityB) = (Quantity(lane: a), Quantity(lane: b));

            if (MeetQuantity64.Meet(left: quantityA, right: quantityB) != MeetQuantity64.Meet(left: quantityB, right: quantityA)) {
                return $"MeetQuantity64.Meet is not commutative at amounts ({quantityA.Amount}, {quantityB.Amount})";
            }
        }

        var envelopeA = Envelope(maskLane: left[0], amountLane: left[1]);
        var envelopeB = Envelope(maskLane: right[0], amountLane: right[1]);

        return ((MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: envelopeA, right: envelopeB) == MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: envelopeB, right: envelopeA))
            ? null
            : $"MeetProduct.Meet is not commutative at masks ({envelopeA.First.Bits:x}, {envelopeB.First.Bits:x}), amounts ({envelopeA.Second.Amount}, {envelopeB.Second.Amount})");
    }

    /// <summary>Meet is idempotent on every carrier: an envelope folded with itself is unchanged, so repeating a link
    /// in a delegation chain attenuates nothing further.</summary>
    /// <param name="left">The first lane vector (width 2).</param>
    /// <param name="right">The second lane vector (width 2).</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MeetIsIdempotent(long[] left, long[] right) {
        Span<long> raws = [left[0], left[1], right[0], right[1]];

        foreach (var raw in raws) {
            var mask = Mask(lane: raw);

            if (MeetMask64.Meet(left: mask, right: mask) != mask) {
                return $"MeetMask64.Meet is not idempotent at bits {mask.Bits:x}";
            }

            var quantity = Quantity(lane: raw);

            if (MeetQuantity64.Meet(left: quantity, right: quantity) != quantity) {
                return $"MeetQuantity64.Meet is not idempotent at amount {quantity.Amount}";
            }
        }

        var envelope = Envelope(maskLane: left[0], amountLane: right[1]);

        return ((MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: envelope, right: envelope) == envelope)
            ? null
            : $"MeetProduct.Meet is not idempotent at ({envelope.First.Bits:x}, {envelope.Second.Amount})");
    }

    /// <summary>THE SECURITY PROPERTY, both halves of the greatest-lower-bound statement, against order spellings
    /// independent of the subject: the meet is at most each operand (no attenuation step can widen authority), and any
    /// common lower bound is at most the meet (attenuation discards nothing both operands allow).</summary>
    /// <param name="left">The first lane vector (width 3).</param>
    /// <param name="right">The second lane vector (width 3).</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MeetNeverWidens(long[] left, long[] right) {
        // The mask half, against the per-bit subset walk.
        var (maskA, maskB) = (Mask(lane: left[0]), Mask(lane: right[0]));
        var maskMeet = MeetMask64.Meet(left: maskA, right: maskB);

        if (!BitwiseSubset(narrow: maskMeet.Bits, wide: maskA.Bits) || !BitwiseSubset(narrow: maskMeet.Bits, wide: maskB.Bits)) {
            return $"MeetMask64.Meet widened: {maskMeet.Bits:x} is not a subset of both {maskA.Bits:x} and {maskB.Bits:x}";
        }

        // The greatest half: a common lower bound of both operands, built independently of the subject by clearing
        // bits from one operand through the OTHER operand's complement image, must sit at or below the meet.
        var maskLower = new MeetMask64(Bits: (maskA.Bits & ~unchecked((ulong)left[2])));

        if (BitwiseSubset(narrow: maskLower.Bits, wide: maskB.Bits) && !BitwiseSubset(narrow: maskLower.Bits, wide: maskMeet.Bits)) {
            return $"MeetMask64.Meet is not greatest: common lower bound {maskLower.Bits:x} exceeds meet {maskMeet.Bits:x}";
        }

        // The quantity half, against the carrier's primitive numeric order.
        var (quantityA, quantityB) = (Quantity(lane: left[1]), Quantity(lane: right[1]));
        var quantityMeet = MeetQuantity64.Meet(left: quantityA, right: quantityB);

        if ((quantityMeet.Amount > quantityA.Amount) || (quantityMeet.Amount > quantityB.Amount)) {
            return $"MeetQuantity64.Meet widened: {quantityMeet.Amount} exceeds an operand of ({quantityA.Amount}, {quantityB.Amount})";
        }

        var quantityLower = Quantity(lane: right[2]);

        if ((quantityLower.Amount <= quantityA.Amount) && (quantityLower.Amount <= quantityB.Amount) && (quantityLower.Amount > quantityMeet.Amount)) {
            return $"MeetQuantity64.Meet is not greatest: common lower bound {quantityLower.Amount} exceeds meet {quantityMeet.Amount}";
        }

        // The product, componentwise on the same independent spellings.
        var envelopeA = Envelope(maskLane: left[0], amountLane: left[1]);
        var envelopeB = Envelope(maskLane: right[0], amountLane: right[1]);
        var envelopeMeet = MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: envelopeA, right: envelopeB);

        return ((BitwiseSubset(narrow: envelopeMeet.First.Bits, wide: envelopeA.First.Bits) &&
                BitwiseSubset(narrow: envelopeMeet.First.Bits, wide: envelopeB.First.Bits) &&
                (envelopeMeet.Second.Amount <= envelopeA.Second.Amount) &&
                (envelopeMeet.Second.Amount <= envelopeB.Second.Amount))
            ? null
            : $"MeetProduct.Meet widened a component at masks ({envelopeA.First.Bits:x}, {envelopeB.First.Bits:x}), amounts ({envelopeA.Second.Amount}, {envelopeB.Second.Amount})");
    }

    /// <summary>The shipped order predicate decides exactly the meet's order, on every carrier:
    /// <c>a.IsAtMost(b)</c> if and only if <c>Meet(a, b) == a</c>. With the meet pinned as the greatest lower bound by
    /// the never-widens case, this ties <c>IsAtMost</c> to the same order, so neither member can drift from the
    /// other.</summary>
    /// <param name="left">The first lane vector (width 2).</param>
    /// <param name="right">The second lane vector (width 2).</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? OrderAgreesWithMeet(long[] left, long[] right) {
        // The sampled pair, plus a comparable pair manufactured from it (the meet against one operand IS comparable),
        // so both truth values of IsAtMost are exercised every sweep rather than only the generically-incomparable one.
        var (maskA, maskB) = (Mask(lane: left[0]), Mask(lane: right[0]));
        var maskMeet = MeetMask64.Meet(left: maskA, right: maskB);
        Span<(MeetMask64 A, MeetMask64 B)> maskPairs = [(maskA, maskB), (maskMeet, maskA), (maskA, maskMeet)];

        foreach (var (a, b) in maskPairs) {
            if (a.IsAtMost(other: b) != (MeetMask64.Meet(left: a, right: b) == a)) {
                return $"MeetMask64.IsAtMost disagrees with Meet at bits ({a.Bits:x}, {b.Bits:x})";
            }
        }

        var (quantityA, quantityB) = (Quantity(lane: left[1]), Quantity(lane: right[1]));
        var quantityMeet = MeetQuantity64.Meet(left: quantityA, right: quantityB);
        Span<(MeetQuantity64 A, MeetQuantity64 B)> quantityPairs = [(quantityA, quantityB), (quantityMeet, quantityA), (quantityA, quantityMeet)];

        foreach (var (a, b) in quantityPairs) {
            if (a.IsAtMost(other: b) != (MeetQuantity64.Meet(left: a, right: b) == a)) {
                return $"MeetQuantity64.IsAtMost disagrees with Meet at amounts ({a.Amount}, {b.Amount})";
            }
        }

        var envelopeA = Envelope(maskLane: left[0], amountLane: left[1]);
        var envelopeB = Envelope(maskLane: right[0], amountLane: right[1]);
        var envelopeMeet = MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: envelopeA, right: envelopeB);
        Span<(MeetProduct<MeetMask64, MeetQuantity64> A, MeetProduct<MeetMask64, MeetQuantity64> B)> envelopePairs = [(envelopeA, envelopeB), (envelopeMeet, envelopeA), (envelopeA, envelopeMeet)];

        foreach (var (a, b) in envelopePairs) {
            if (a.IsAtMost(other: b) != (MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: a, right: b) == a)) {
                return $"MeetProduct.IsAtMost disagrees with Meet at masks ({a.First.Bits:x}, {b.First.Bits:x}), amounts ({a.Second.Amount}, {b.Second.Amount})";
            }
        }

        return null;
    }

    /// <summary>The composition law: the product carrier's every operation projects componentwise onto the component
    /// carriers' own operations — meet, top, bottom, and the order as the conjunction of component orders — and the
    /// construction stacks, a nested product projecting the same way. This is what makes "a product of meets is a
    /// meet": the component laws pinned by the other meet cases transfer to any envelope built by pairing.</summary>
    /// <param name="left">The first lane vector (width 3).</param>
    /// <param name="right">The second lane vector (width 3).</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ProductComposesComponentwise(long[] left, long[] right) {
        var envelopeA = Envelope(maskLane: left[0], amountLane: left[1]);
        var envelopeB = Envelope(maskLane: right[0], amountLane: right[1]);
        var meet = MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: envelopeA, right: envelopeB);

        if ((meet.First != MeetMask64.Meet(left: envelopeA.First, right: envelopeB.First)) || (meet.Second != MeetQuantity64.Meet(left: envelopeA.Second, right: envelopeB.Second))) {
            return $"MeetProduct.Meet is not componentwise at masks ({envelopeA.First.Bits:x}, {envelopeB.First.Bits:x}), amounts ({envelopeA.Second.Amount}, {envelopeB.Second.Amount})";
        }

        var top = MeetProduct<MeetMask64, MeetQuantity64>.Top;
        var bottom = MeetProduct<MeetMask64, MeetQuantity64>.Bottom;

        if ((top.First != MeetMask64.Top) || (top.Second != MeetQuantity64.Top) || (bottom.First != MeetMask64.Bottom) || (bottom.Second != MeetQuantity64.Bottom)) {
            return "MeetProduct.Top or MeetProduct.Bottom does not project to the component identity or absorber";
        }

        if (envelopeA.IsAtMost(other: envelopeB) != (envelopeA.First.IsAtMost(other: envelopeB.First) && envelopeA.Second.IsAtMost(other: envelopeB.Second))) {
            return $"MeetProduct.IsAtMost is not the conjunction of component orders at masks ({envelopeA.First.Bits:x}, {envelopeB.First.Bits:x}), amounts ({envelopeA.Second.Amount}, {envelopeB.Second.Amount})";
        }

        // The stack: nest a product inside a product and check the same componentwise projection one level up.
        var nestedA = new MeetProduct<MeetProduct<MeetMask64, MeetQuantity64>, MeetMask64>(First: envelopeA, Second: Mask(lane: left[2]));
        var nestedB = new MeetProduct<MeetProduct<MeetMask64, MeetQuantity64>, MeetMask64>(First: envelopeB, Second: Mask(lane: right[2]));
        var nestedMeet = MeetProduct<MeetProduct<MeetMask64, MeetQuantity64>, MeetMask64>.Meet(left: nestedA, right: nestedB);

        return (((nestedMeet.First == meet) && (nestedMeet.Second == MeetMask64.Meet(left: nestedA.Second, right: nestedB.Second)))
            ? null
            : $"a nested MeetProduct did not project componentwise at outer masks ({nestedA.Second.Bits:x}, {nestedB.Second.Bits:x})");
    }

    /// <summary>Meet has <c>Top</c> as a two-sided identity on every carrier: an unrestricted link in a delegation
    /// chain attenuates nothing.</summary>
    /// <param name="left">The first lane vector (width 2).</param>
    /// <param name="right">The second lane vector (width 2).</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? TopIsIdentity(long[] left, long[] right) {
        Span<long> raws = [left[0], left[1], right[0], right[1]];

        foreach (var raw in raws) {
            var mask = Mask(lane: raw);

            if ((MeetMask64.Meet(left: mask, right: MeetMask64.Top) != mask) || (MeetMask64.Meet(left: MeetMask64.Top, right: mask) != mask)) {
                return $"MeetMask64.Top is not a two-sided identity at bits {mask.Bits:x}";
            }

            var quantity = Quantity(lane: raw);

            if ((MeetQuantity64.Meet(left: quantity, right: MeetQuantity64.Top) != quantity) || (MeetQuantity64.Meet(left: MeetQuantity64.Top, right: quantity) != quantity)) {
                return $"MeetQuantity64.Top is not a two-sided identity at amount {quantity.Amount}";
            }
        }

        var envelope = Envelope(maskLane: left[0], amountLane: right[1]);
        var top = MeetProduct<MeetMask64, MeetQuantity64>.Top;

        return (((MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: envelope, right: top) == envelope) && (MeetProduct<MeetMask64, MeetQuantity64>.Meet(left: top, right: envelope) == envelope))
            ? null
            : $"MeetProduct.Top is not a two-sided identity at ({envelope.First.Bits:x}, {envelope.Second.Amount})");
    }

    // The per-bit subset walk — the independent spelling of the mask order. It reads one bit at a time through shifts
    // and never forms the masked-complement expression MeetMask64.IsAtMost is written as, so the two cannot fail
    // together by sharing a formula.
    private static bool BitwiseSubset(ulong narrow, ulong wide) {
        for (var bit = 0; (bit < 64); ++bit) {
            if ((((narrow >> bit) & 1UL) == 1UL) && (((wide >> bit) & 1UL) == 0UL)) {
                return false;
            }
        }

        return true;
    }

    private static MeetProduct<MeetMask64, MeetQuantity64> Envelope(long maskLane, long amountLane) =>
        new(First: Mask(lane: maskLane), Second: Quantity(lane: amountLane));

    // Domain lanes are signed raws; the carriers are unsigned. A plain bit reinterpretation keeps the committed edge
    // battery meaningful: 0 stays Bottom, −1 becomes Top, and every power-of-two off-by-one lands beside its seam.
    private static MeetMask64 Mask(long lane) =>
        new(Bits: unchecked((ulong)lane));

    private static MeetQuantity64 Quantity(long lane) =>
        new(Amount: unchecked((ulong)lane));
}
