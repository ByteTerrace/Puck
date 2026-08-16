using System.Numerics;

using Floor3 = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>>;
using Floor4 = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>>>;

namespace Puck.Maths.Tests;

/// <summary>
/// Two claims over the LIVE-associator normalizer of a Cayley-Dickson presentation: its <c>TryNormalize</c> output
/// against <see cref="DoublingAlgebra{TInner}"/>'s own hand-written nested products at every ordered basis triple of
/// the octonion and sedenion floors, and every bracketing of every ordered sedenion quadruple against its own nested
/// <c>Multiply</c> chain.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LiveAssociatorMatchesDoublingTower"/> is the fused-substrate cross-check neither nearby sibling makes.
/// <c>presented.octonion-twin-doubling</c>/<c>presented.associator-twin-doubling</c> check <c>Multiply</c> — not the
/// live-associator <c>TryNormalize</c> path — against <c>DoublingAlgebra</c> at full raw range but without live
/// re-association; <c>presented.reassociation-route-coherent</c> checks the live <c>TryNormalize</c> path but only
/// against the algebra's OWN nested <c>Multiply</c> chain (intra-presented), never against the external doubling
/// kernel. It also pins the EXACT bracket-sensitive triple counts (168 of 512 octonion triples, 1848 of 4096 sedenion
/// triples) as a regression pin, where the existing <c>presented.coherence-route-independence</c> canary asserts only
/// a LOOSE floor (&gt;150/&gt;1600), a strictly weaker statement.
/// </para>
/// <para>
/// <see cref="SedenionQuadrupleBracketingsExhaustive"/> is a MIRROR of
/// <c>presented.reassociation-route-coherent</c>'s own quadruple-bracketing statement (all five bracketings of a
/// quadruple normalize to their own nested product) at strictly stronger operands: that statement stops at
/// <c>floors &gt; 3</c> (Subjects.cs's own loop guard), so it never reaches the sedenion floor's full 16⁴ = 65,536
/// ordered quadruple cross product. Floor 4 instead of floor ≤ 3 is why it is tiered Deep rather than Default.
/// </para>
/// <para>
/// No code here is shared with <see cref="LawRegistry"/> or <see cref="Subjects"/>: the doubling-tower unit-basis
/// construction and the octonion/sedenion lane readout are written out in this file and called from nowhere else.
/// </para>
/// </remarks>
internal static class PresentedModuleClaims {
    private const long NormalizationSteps = (1L << 20);

    // ---- doubling-tower oracle construction ----

    private static DoublingAlgebra<FixedScalarRing> UnitComplex(int index, int offset) =>
        new(
            Left: new FixedScalarRing(Value: ((offset == index) ? FixedQ4816.One : FixedQ4816.Zero)),
            Right: new FixedScalarRing(Value: (((offset + 1) == index) ? FixedQ4816.One : FixedQ4816.Zero))
        );
    private static DoublingAlgebra<DoublingAlgebra<FixedScalarRing>> UnitQuaternion(int index, int offset) =>
        new(Left: UnitComplex(index: index, offset: offset), Right: UnitComplex(index: index, offset: (offset + 2)));
    private static Floor3 UnitOctonionAt(int index, int offset) =>
        new(Left: UnitQuaternion(index: index, offset: offset), Right: UnitQuaternion(index: index, offset: (offset + 4)));
    private static Floor3 UnitOctonion(int index) =>
        UnitOctonionAt(index: index, offset: 0);
    private static Floor4 UnitSedenion(int index) =>
        new(Left: UnitOctonionAt(index: index, offset: 0), Right: UnitOctonionAt(index: index, offset: 8));
    private static void WriteOctonionLanes(Floor3 value, Span<long> lanes) {
        lanes[0] = value.Left.Left.Left.Value.Value;
        lanes[1] = value.Left.Left.Right.Value.Value;
        lanes[2] = value.Left.Right.Left.Value.Value;
        lanes[3] = value.Left.Right.Right.Value.Value;
        lanes[4] = value.Right.Left.Left.Value.Value;
        lanes[5] = value.Right.Left.Right.Value.Value;
        lanes[6] = value.Right.Right.Left.Value.Value;
        lanes[7] = value.Right.Right.Right.Value.Value;
    }
    private static void WriteSedenionLanes(Floor4 value, Span<long> lanes) {
        WriteOctonionLanes(value: value.Left, lanes: lanes[..8]);
        WriteOctonionLanes(value: value.Right, lanes: lanes.Slice(length: 8, start: 8));
    }
    private static Term Bracket(Term left, Term right) =>
        Term.Node(children: [left, right], symbol: Term.Product);

    /// <summary>Proves that the LIVE-associator normalizer's <c>TryNormalize</c> output, at every ordered basis
    /// triple of both bracketing shapes, equals <see cref="DoublingAlgebra{TInner}"/>'s own hand-written nested
    /// products — at the octonion floor (8³ = 512 triples) and the sedenion floor (16³ = 4096 triples) — and that the
    /// associator's support is EXACTLY the pinned 168 and 1848 triples respectively.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LiveAssociatorMatchesDoublingTower() {
        // ---- octonion floor: 8^3 = 512 ordered triples, both bracketing shapes, against Floor3 (DoublingAlgebra over
        // the quaternion floor's own eight-product fused kernel — DoublingAlgebra.cs:265-337). ----
        var octonionAlgebra = PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(
            presentation: Presentations.CayleyDickson<FixedQ4816, FixedMaterial>(basisRelabelling: [], floors: 3, liveAssociator: true, material: default));
        var octonionWritten = new long[8];
        var octonionMoved = 0;

        for (var first = 0; (first < 8); ++first) {
            for (var second = 0; (second < 8); ++second) {
                for (var third = 0; (third < 8); ++third) {
                    var nestedTerm = Bracket(left: Term.Leaf(symbol: first), right: Bracket(left: Term.Leaf(symbol: second), right: Term.Leaf(symbol: third)));
                    var flatTerm = Bracket(left: Bracket(left: Term.Leaf(symbol: first), right: Term.Leaf(symbol: second)), right: Term.Leaf(symbol: third));
                    var nestedValue = Floor3.Multiply(left: UnitOctonion(index: first), right: Floor3.Multiply(left: UnitOctonion(index: second), right: UnitOctonion(index: third)));
                    var flatValue = Floor3.Multiply(left: Floor3.Multiply(left: UnitOctonion(index: first), right: UnitOctonion(index: second)), right: UnitOctonion(index: third));

                    if (!octonionAlgebra.TryNormalize(normalForm: out var nestedForm, obstruction: out var nestedObstruction, stepLimit: NormalizationSteps, term: nestedTerm)) {
                        return $"cayley-dickson(3, live): the right-nested triple ({first},{second},{third}) did not normalize (steps={nestedObstruction.StepsTaken} blocked={nestedObstruction.BlockedKey})";
                    }

                    WriteOctonionLanes(lanes: octonionWritten, value: nestedValue);

                    for (var lane = 0; (lane < 8); ++lane) {
                        if (nestedForm[lane].Value != octonionWritten[lane]) {
                            return $"cayley-dickson(3, live): the right-nested triple ({first},{second},{third}) disagrees with DoublingAlgebra<LeafQuaternion>.Multiply at lane {lane}";
                        }
                    }

                    if (!octonionAlgebra.TryNormalize(normalForm: out var flatForm, obstruction: out var flatObstruction, stepLimit: NormalizationSteps, term: flatTerm)) {
                        return $"cayley-dickson(3, live): the left-normed triple ({first},{second},{third}) did not normalize (steps={flatObstruction.StepsTaken} blocked={flatObstruction.BlockedKey})";
                    }

                    WriteOctonionLanes(lanes: octonionWritten, value: flatValue);

                    for (var lane = 0; (lane < 8); ++lane) {
                        if (flatForm[lane].Value != octonionWritten[lane]) {
                            return $"cayley-dickson(3, live): the left-normed triple ({first},{second},{third}) disagrees with DoublingAlgebra<LeafQuaternion>.Multiply at lane {lane}";
                        }
                    }

                    if (nestedValue != flatValue) { ++octonionMoved; }
                }
            }
        }

        if (168 != octonionMoved) {
            return $"cayley-dickson(3, live): the associator's support moved {octonionMoved} of 512 ordered triples, not the pinned 168 (regression pin, set by observing the subject)";
        }

        // ---- sedenion floor: 16^3 = 4096 ordered triples, both bracketing shapes, against Floor4. ----
        var sedenionAlgebra = PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(
            presentation: Presentations.CayleyDickson<FixedQ4816, FixedMaterial>(basisRelabelling: [], floors: 4, liveAssociator: true, material: default));
        var sedenionWritten = new long[16];
        var sedenionMoved = 0;

        for (var first = 0; (first < 16); ++first) {
            for (var second = 0; (second < 16); ++second) {
                for (var third = 0; (third < 16); ++third) {
                    var nestedTerm = Bracket(left: Term.Leaf(symbol: first), right: Bracket(left: Term.Leaf(symbol: second), right: Term.Leaf(symbol: third)));
                    var flatTerm = Bracket(left: Bracket(left: Term.Leaf(symbol: first), right: Term.Leaf(symbol: second)), right: Term.Leaf(symbol: third));
                    var nestedValue = Floor4.Multiply(left: UnitSedenion(index: first), right: Floor4.Multiply(left: UnitSedenion(index: second), right: UnitSedenion(index: third)));
                    var flatValue = Floor4.Multiply(left: Floor4.Multiply(left: UnitSedenion(index: first), right: UnitSedenion(index: second)), right: UnitSedenion(index: third));

                    if (!sedenionAlgebra.TryNormalize(normalForm: out var nestedForm, obstruction: out var nestedObstruction, stepLimit: NormalizationSteps, term: nestedTerm)) {
                        return $"cayley-dickson(4, live): the right-nested triple ({first},{second},{third}) did not normalize (steps={nestedObstruction.StepsTaken} blocked={nestedObstruction.BlockedKey})";
                    }

                    WriteSedenionLanes(lanes: sedenionWritten, value: nestedValue);

                    for (var lane = 0; (lane < 16); ++lane) {
                        if (nestedForm[lane].Value != sedenionWritten[lane]) {
                            return $"cayley-dickson(4, live): the right-nested triple ({first},{second},{third}) disagrees with DoublingAlgebra<LeafOctonion>.Multiply at lane {lane}";
                        }
                    }

                    if (!sedenionAlgebra.TryNormalize(normalForm: out var flatForm, obstruction: out var flatObstruction, stepLimit: NormalizationSteps, term: flatTerm)) {
                        return $"cayley-dickson(4, live): the left-normed triple ({first},{second},{third}) did not normalize (steps={flatObstruction.StepsTaken} blocked={flatObstruction.BlockedKey})";
                    }

                    WriteSedenionLanes(lanes: sedenionWritten, value: flatValue);

                    for (var lane = 0; (lane < 16); ++lane) {
                        if (flatForm[lane].Value != sedenionWritten[lane]) {
                            return $"cayley-dickson(4, live): the left-normed triple ({first},{second},{third}) disagrees with DoublingAlgebra<LeafOctonion>.Multiply at lane {lane}";
                        }
                    }

                    if (nestedValue != flatValue) { ++sedenionMoved; }
                }
            }
        }

        if (1_848 != sedenionMoved) {
            return $"cayley-dickson(4, live): the associator's support moved {sedenionMoved} of 4096 ordered triples, not the pinned 1848 (regression pin, set by observing the subject)";
        }

        return null;
    }
    /// <summary>MIRROR of <c>presented.reassociation-route-coherent</c>'s quadruple-bracketing statement at strictly
    /// stronger operands. Proves that all five bracketings of EVERY ordered quadruple of the live sedenion
    /// floor (16⁴ = 65,536 quadruples) normalize to their own nested <c>Multiply</c> chain — the full cross product the
    /// existing sibling's own loop guard (<c>floors &gt; 3</c>, Subjects.cs) stops short of.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SedenionQuadrupleBracketingsExhaustive() {
        var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(
            presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(basisRelabelling: [], floors: 4, liveAssociator: true, material: default));
        var basis = new PresentedAlgebra<BigInteger, IntegerMaterial>.Element[16];

        for (var key = 0; (key < 16); ++key) {
            basis[key] = algebra.FromSupport(keys: [key], coefficients: [algebra.Presentation.Material.One]);
        }

        for (var first = 0; (first < 16); ++first) {
            for (var second = 0; (second < 16); ++second) {
                for (var third = 0; (third < 16); ++third) {
                    for (var fourth = 0; (fourth < 16); ++fourth) {
                        var w = Term.Leaf(symbol: first);
                        var x = Term.Leaf(symbol: second);
                        var y = Term.Leaf(symbol: third);
                        var z = Term.Leaf(symbol: fourth);
                        var p = basis[first];
                        var q = basis[second];
                        var r = basis[third];
                        var s = basis[fourth];
                        var trees = new[] {
                            Bracket(left: Bracket(left: Bracket(left: w, right: x), right: y), right: z),
                            Bracket(left: Bracket(left: w, right: Bracket(left: x, right: y)), right: z),
                            Bracket(left: Bracket(left: w, right: x), right: Bracket(left: y, right: z)),
                            Bracket(left: w, right: Bracket(left: Bracket(left: x, right: y), right: z)),
                            Bracket(left: w, right: Bracket(left: x, right: Bracket(left: y, right: z))),
                        };
                        var values = new[] {
                            algebra.Multiply(left: algebra.Multiply(left: algebra.Multiply(left: p, right: q), right: r), right: s),
                            algebra.Multiply(left: algebra.Multiply(left: p, right: algebra.Multiply(left: q, right: r)), right: s),
                            algebra.Multiply(left: algebra.Multiply(left: p, right: q), right: algebra.Multiply(left: r, right: s)),
                            algebra.Multiply(left: p, right: algebra.Multiply(left: algebra.Multiply(left: q, right: r), right: s)),
                            algebra.Multiply(left: p, right: algebra.Multiply(left: q, right: algebra.Multiply(left: r, right: s))),
                        };

                        for (var shape = 0; (shape < 5); ++shape) {
                            if (!algebra.TryNormalize(term: trees[shape], stepLimit: NormalizationSteps, normalForm: out var form, obstruction: out var obstruction)) {
                                return $"cayley-dickson(4, live): quadruple ({first},{second},{third},{fourth}) bracketing {shape} did not normalize (steps={obstruction.StepsTaken} blocked={obstruction.BlockedKey})";
                            }

                            if (!algebra.AreEqual(left: form, right: values[shape])) {
                                return $"cayley-dickson(4, live): quadruple ({first},{second},{third},{fourth}) bracketing {shape} normalized to a value disagreeing with its own nested Multiply chain";
                            }
                        }
                    }
                }
            }
        }

        return null;
    }
}
