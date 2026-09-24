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
/// The doubling-tower unit basis and lane readout come from <see cref="DoublingTower"/>, which builds operands and reads
/// lanes and multiplies nothing; every product on the reference side is <see cref="DoublingAlgebra{TInner}"/>'s own.
/// </para>
/// </remarks>
internal static class PresentedModuleClaims {
    private const long NormalizationSteps = (1L << 20);

    private static Term Bracket(Term left, Term right) =>
        Term.Node(
            children: [left, right],
            symbol: Term.Product
        );

    /// <summary>Proves that the LIVE-associator normalizer's <c>TryNormalize</c> output, at every ordered basis
    /// triple of both bracketing shapes, equals <see cref="DoublingAlgebra{TInner}"/>'s own hand-written nested
    /// products — at the octonion floor (8³ = 512 triples) and the sedenion floor (16³ = 4096 triples) — and that the
    /// associator's support is EXACTLY the pinned 168 and 1848 triples respectively.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LiveAssociatorMatchesDoublingTower() =>
        (LiveAssociatorAtFloor<Floor3>(
            floors: 3,
            kernel: "DoublingAlgebra<LeafQuaternion>",
            pinnedMoved: 168,
            unit: static index => DoublingTower.UnitOctonionAt(index: index),
            write: DoublingTower.WriteOctonionLanes
        ) ??
            LiveAssociatorAtFloor<Floor4>(
            floors: 4,
            kernel: "DoublingAlgebra<LeafOctonion>",
            pinnedMoved: 1_848,
            unit: static index => DoublingTower.UnitSedenionAt(index: index),
            write: DoublingTower.WriteSedenionLanes
        ));

    private delegate void LaneWriter<TFloor>(TFloor value, Span<long> lanes, int offset);

    // One floor of LiveAssociatorMatchesDoublingTower: every ordered basis triple of the live presentation, in both
    // bracketing shapes, normalized and compared lane for lane against the doubling tower's own nested product at the
    // same floor, with the associator's support counted against its pinned size.
    private static string? LiveAssociatorAtFloor<TFloor>(int floors, string kernel, int pinnedMoved, Func<int, TFloor> unit, LaneWriter<TFloor> write)
        where TFloor : IConjugationRing<TFloor>, IEquatable<TFloor> {
        var width = (1 << floors);
        var algebra = PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(presentation: Presentations.CayleyDickson<FixedQ4816, FixedMaterial>(
            basisRelabelling: [],
            floors: floors,
            liveAssociator: true,
            material: default
        ));
        var written = new long[width];
        var moved = 0;

        for (var first = 0; (first < width); ++first) {
            for (var second = 0; (second < width); ++second) {
                for (var third = 0; (third < width); ++third) {
                    var nestedTerm = Bracket(
                        left: Term.Leaf(symbol: first),
                        right: Bracket(
                            left: Term.Leaf(symbol: second),
                            right: Term.Leaf(symbol: third)
                        )
                    );
                    var flatTerm = Bracket(
                        left: Bracket(
                            left: Term.Leaf(symbol: first),
                            right: Term.Leaf(symbol: second)
                        ),
                        right: Term.Leaf(symbol: third)
                    );
                    var nestedValue = TFloor.Multiply(
                        left: unit(arg: first),
                        right: TFloor.Multiply(
                            left: unit(arg: second),
                            right: unit(arg: third)
                        )
                    );
                    var flatValue = TFloor.Multiply(
                        left: TFloor.Multiply(
                            left: unit(arg: first),
                            right: unit(arg: second)
                        ),
                        right: unit(arg: third)
                    );

                    foreach (var (shape, term, value) in ((ReadOnlySpan<(string, Term, TFloor)>)[("right-nested", nestedTerm, nestedValue), ("left-normed", flatTerm, flatValue)])) {
                        if (!algebra.TryNormalize(
                            normalForm: out var form,
                            obstruction: out var obstruction,
                            stepLimit: NormalizationSteps,
                            term: term
                        )) {
                            return $"cayley-dickson({floors}, live): the {shape} triple ({first},{second},{third}) did not normalize (steps={obstruction.StepsTaken} blocked={obstruction.BlockedKey})";
                        }

                        write(
                            lanes: written,
                            offset: 0,
                            value: value
                        );

                        for (var lane = 0; (lane < width); ++lane) {
                            if (form[lane].Value != written[lane]) {
                                return $"cayley-dickson({floors}, live): the {shape} triple ({first},{second},{third}) disagrees with {kernel}.Multiply at lane {lane}";
                            }
                        }
                    }

                    if (!nestedValue.Equals(other: flatValue)) { ++moved; }
                }
            }
        }

        return ((pinnedMoved == moved)
            ? null
            : $"cayley-dickson({floors}, live): the associator's support moved {moved} of {((width * width) * width)} ordered triples, not the pinned {pinnedMoved} (regression pin, set by observing the subject)"
        );
    }

    /// <summary>MIRROR of <c>presented.reassociation-route-coherent</c>'s quadruple-bracketing statement at strictly
    /// stronger operands. Proves that all five bracketings of EVERY ordered quadruple of the live sedenion
    /// floor (16⁴ = 65,536 quadruples) normalize to their own nested <c>Multiply</c> chain — the full cross product the
    /// existing sibling's own loop guard (<c>floors &gt; 3</c>, Subjects.cs) stops short of.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SedenionQuadrupleBracketingsExhaustive() {
        var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
            basisRelabelling: [],
            floors: 4,
            liveAssociator: true,
            material: default
        ));
        var basis = new PresentedAlgebra<BigInteger, IntegerMaterial>.Element[16];

        for (var key = 0; (key < 16); ++key) {
            basis[key] = algebra.FromSupport(
                keys: [key],
                coefficients: [algebra.Presentation.Material.One]
            );
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
                            Bracket(
                            left: Bracket(
                                left: Bracket(
                                    left: w,
                                    right: x
                                ),
                                right: y
                            ),
                            right: z
                        ),
                            Bracket(
                            left: Bracket(
                                left: w,
                                right: Bracket(
                                    left: x,
                                    right: y
                                )
                            ),
                            right: z
                        ),
                            Bracket(
                            left: Bracket(
                                left: w,
                                right: x
                            ),
                            right: Bracket(
                                left: y,
                                right: z
                            )
                        ),
                            Bracket(
                            left: w,
                            right: Bracket(
                                left: Bracket(
                                    left: x,
                                    right: y
                                ),
                                right: z
                            )
                        ),
                            Bracket(
                            left: w,
                            right: Bracket(
                                left: x,
                                right: Bracket(
                                    left: y,
                                    right: z
                                )
                            )
                        ),
                        };
                        var values = new[] {
                            algebra.Multiply(
                            left: algebra.Multiply(
                                left: algebra.Multiply(
                                    left: p,
                                    right: q
                                ),
                                right: r
                            ),
                            right: s
                        ),
                            algebra.Multiply(
                            left: algebra.Multiply(
                                left: p,
                                right: algebra.Multiply(
                                    left: q,
                                    right: r
                                )
                            ),
                            right: s
                        ),
                            algebra.Multiply(
                            left: algebra.Multiply(
                                left: p,
                                right: q
                            ),
                            right: algebra.Multiply(
                                left: r,
                                right: s
                            )
                        ),
                            algebra.Multiply(
                            left: p,
                            right: algebra.Multiply(
                                left: algebra.Multiply(
                                    left: q,
                                    right: r
                                ),
                                right: s
                            )
                        ),
                            algebra.Multiply(
                            left: p,
                            right: algebra.Multiply(
                                left: q,
                                right: algebra.Multiply(
                                    left: r,
                                    right: s
                                )
                            )
                        ),
                        };

                        for (var shape = 0; (shape < 5); ++shape) {
                            if (!algebra.TryNormalize(
                                term: trees[shape],
                                stepLimit: NormalizationSteps,
                                normalForm: out var form,
                                obstruction: out var obstruction
                            )) {
                                return $"cayley-dickson(4, live): quadruple ({first},{second},{third},{fourth}) bracketing {shape} did not normalize (steps={obstruction.StepsTaken} blocked={obstruction.BlockedKey})";
                            }

                            if (!algebra.AreEqual(
                                left: form,
                                right: values[shape]
                            )) {
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
