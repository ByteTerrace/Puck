using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Two claims over the unit interval as a material family.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="UnitIntervalPowerOfTwoEnvelopeBoundary"/> — the power-of-two twin's ENVELOPE far side (a
/// four-arc chain at exponent nine), which <c>presented.unit-interval-power-of-two-twin</c>'s own leg names as
/// unmeasured in the suite (worklist O6).</item>
/// <item><see cref="UnitIntervalFusedCompetingTermsVsOracle"/> — the absolute classical statement that
/// <see cref="MostLikelyPathMaterial"/>'s <c>FusedChargedSum</c>, folded over THREE COMPETING terms, equals one
/// independently-derived ties-to-even rounding of the winning exact term — a multi-term absolute leg the suite does
/// not otherwise carry (the existing absolute leg on <c>presented.unit-interval-semirings-vs-oracle</c> is
/// single-term, and the existing divergence canary on <c>presented.unit-interval-fused-vs-per-term-diverges</c>
/// proves disagreement with an alternative discipline rather than agreement with an independent oracle).</item>
/// </list>
/// </remarks>
internal static class PresentedZetaClaims {
    private const ulong UnitOneRaw = (1UL << 32);

    /// <summary>Builds the codiscrete quiver on a given number of objects at any material: every ordered pair is an
    /// arrow, so the algebra IS the matrix algebra of that order. A private copy of the same small helper every
    /// unit-interval subject already carries, kept local so this file calls into no other law file.</summary>
    private static ChargedPresentation<TValue, TOps> CodiscreteQuiver<TValue, TOps>(int order, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var arrows = new (int Source, int Target, TValue Weight)[(order * order)];

        for (var source = 0; (source < order); ++source) {
            for (var target = 0; (target < order); ++target) { arrows[((source * order) + target)] = (source, target, material.One); }
        }

        return Presentations.Quiver<TValue, TOps>(objectCount: order, arrows: arrows, material: material);
    }

    private static long Key(int source, int target, int order) =>
        ((source * order) + target);

    /// <summary>Proves the power-of-two twin's ENVELOPE far side, which no case in the suite measures: past the
    /// carrier's 2⁻³² grid the log-domain isomorphism between <see cref="MostLikelyPathMaterial"/>'s likelihood and
    /// <see cref="TropicalMaterial"/>'s cost stops holding, because the likelihood underflows to the impossible
    /// outcome while the cost stays an ordinary finite integer.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// <para>
    /// A five-object simple path 0→1→2→3→4, every arc at exponent nine. Three hops cost 27, inside the carrier's
    /// 2⁻³² grid; four hops cost 36, past it. <see cref="MostLikelyPathMaterial"/> is not a certified exact
    /// semiring, so its guarded sum is read through the explicit finite <c>TruncatedSum</c> schedule (bound =
    /// order − 1, the most hops a simple path over five objects can take) rather than
    /// <c>TrySumOverAllLengths</c>; <see cref="TropicalMaterial"/> IS exact and idempotent, so its guarded sum is
    /// read through <c>TrySumOverAllLengths</c> directly. One fixed instance rather than a swept domain, because the
    /// claim names one specific boundary rather than a family.
    /// </para>
    /// <para>
    /// This closes the gap <c>presented.unit-interval-power-of-two-twin</c> names in its own ENVELOPE leg: that case
    /// is silent about its own far boundary, where the isomorphism becomes false past 32 fraction bits, and defers
    /// here for it.
    /// </para>
    /// </remarks>
    public static string? UnitIntervalPowerOfTwoEnvelopeBoundary() {
        const int Order = 5;
        const int Exponent = 9;

        var likelyAlgebra = PresentedAlgebra<UnitInterval32, MostLikelyPathMaterial>.Create(
            presentation: CodiscreteQuiver<UnitInterval32, MostLikelyPathMaterial>(order: Order, material: default)
        );
        var tropicalAlgebra = PresentedAlgebra<FixedQ4816, TropicalMaterial>.Create(
            presentation: CodiscreteQuiver<FixedQ4816, TropicalMaterial>(order: Order, material: default)
        );
        var keys = (long[])[Key(source: 0, target: 1, order: Order), Key(source: 1, target: 2, order: Order), Key(source: 2, target: 3, order: Order), Key(source: 3, target: 4, order: Order)];
        var likelyCoefficients = (UnitInterval32[])[
            UnitInterval32.Create(value: (UnitOneRaw >> Exponent)), UnitInterval32.Create(value: (UnitOneRaw >> Exponent)),
            UnitInterval32.Create(value: (UnitOneRaw >> Exponent)), UnitInterval32.Create(value: (UnitOneRaw >> Exponent)),
        ];
        var tropicalCoefficients = (FixedQ4816[])[
            FixedQ4816.FromInteger(value: Exponent), FixedQ4816.FromInteger(value: Exponent),
            FixedQ4816.FromInteger(value: Exponent), FixedQ4816.FromInteger(value: Exponent),
        ];
        var likelyElement = likelyAlgebra.FromSupport(keys: keys, coefficients: likelyCoefficients);
        var tropicalElement = tropicalAlgebra.FromSupport(keys: keys, coefficients: tropicalCoefficients);
        var likelyTotal = likelyAlgebra.TruncatedSum(value: likelyElement, bound: (Order - 1));

        if (!tropicalAlgebra.TrySumOverAllLengths(value: tropicalElement, total: out var tropicalTotal, obstruction: out var tropicalObstruction)) {
            return $"the tropical star was refused on the five-object chain, attempting {tropicalObstruction.Attempted}, where the exact idempotent material carries no such obstruction";
        }

        var threeHopKey = Key(source: 0, target: 3, order: Order);
        var fourHopKey = Key(source: 0, target: 4, order: Order);
        var threeHopCost = tropicalTotal[key: threeHopKey];
        var fourHopCost = tropicalTotal[key: fourHopKey];
        var threeHopLikelihood = likelyTotal[key: threeHopKey];
        var fourHopLikelihood = likelyTotal[key: fourHopKey];

        if (threeHopCost != FixedQ4816.FromInteger(value: (3 * Exponent))) {
            return $"the three-hop tropical cost is {threeHopCost.Value}, expected exactly {3 * Exponent} at Q16";
        }

        if (fourHopCost != FixedQ4816.FromInteger(value: (4 * Exponent))) {
            return $"the four-hop tropical cost is {fourHopCost.Value}, expected exactly {4 * Exponent} at Q16, where the tropical material names it exactly and never underflows";
        }

        if (UnitInterval32.Zero == threeHopLikelihood) {
            return "the three-hop likelihood underflowed to the impossible outcome, where a cost of 27 sits inside the 2⁻³² grid and should read 2⁻²⁷ exactly";
        }

        if (UnitInterval32.Zero != fourHopLikelihood) {
            return $"the four-hop likelihood read {fourHopLikelihood.Value} rather than underflowing, where a cost of 36 sits past the 2⁻³² grid the carrier holds";
        }

        return null;
    }

    // ---- SplitMix64 index mixer: a pure function of a running counter, never System.Random and never wall-clock ----

    private static ulong MixIndex(ulong index) {
        var mixed = (index + 0x9E3779B97F4A7C15UL);

        mixed = ((mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL);
        mixed = ((mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL);

        return (mixed ^ (mixed >> 31));
    }

    private static ulong DrawRaw(ref ulong counter) {
        counter += 1UL;

        return (MixIndex(index: counter) % (UnitOneRaw + 1UL));
    }

    /// <summary>Rounds an exact non-negative dyadic value to the closed-unit grid, ties to even — re-derived here
    /// rather than borrowed from <see cref="UnitInterval32"/> or from <c>Oracles.cs</c>, which is the whole point of
    /// an oracle.</summary>
    private static ulong RoundTiesToEvenLocal(BigInteger exact, int shift) {
        var truncated = BigInteger.DivRem(dividend: exact, divisor: (BigInteger.One << shift), remainder: out var remainder);
        var half = (BigInteger.One << (shift - 1));

        if ((remainder > half) || ((remainder == half) && !(truncated & BigInteger.One).IsZero)) { truncated += BigInteger.One; }

        return ((ulong)truncated);
    }

    /// <summary>Proves the ABSOLUTE multi-term statement <c>presented.unit-interval-semirings-vs-oracle</c> does not
    /// carry: <see cref="MostLikelyPathMaterial"/>'s <c>FusedChargedSum</c>, folded over THREE COMPETING terms under
    /// <see cref="ChargeLane.General"/>, equals one independently-derived ties-to-even rounding of the single
    /// winning exact <see cref="BigInteger"/> triple product — not a per-term rounding restated, but the max-then-
    /// round subject checked against a round-then-max oracle that shares no rounding code with it.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// <para>
    /// The subject's own kernel (<c>MaterialOps.cs</c>, <see cref="MostLikelyPathMaterial.FusedChargedSum"/>) rounds
    /// EACH term once via <c>UnitInterval32.Multiply(charge, left, right)</c> and then takes the max of the rounded
    /// terms. The oracle here takes the max of the three EXACT <see cref="BigInteger"/> triple products first and
    /// rounds that winning term exactly once. The two formulations agree only because ties-to-even rounding is a
    /// non-decreasing function of its input, so <c>max(round(a), round(b)) == round(max(a, b))</c> — the identity
    /// "the maximum commutes with a monotone rounding", checked directly here over operands drawn from a pure
    /// SplitMix64 mix of a running counter rather than <see cref="Random"/>.
    /// </para>
    /// <para>
    /// This is the ABSOLUTE classical half only. The DIVERGENCE half — the same fused fold compared against the
    /// two-rounding nesting <c>UnitInterval32.Multiply(charge, Multiply(left, right))</c> — is already pinned by
    /// <c>presented.unit-interval-fused-vs-per-term-diverges</c> in <c>LawRegistry.cs</c> and is not restated here.
    /// </para>
    /// </remarks>
    public static string? UnitIntervalFusedCompetingTermsVsOracle() {
        const int Draws = 4096;
        const int Terms = 3;

        var material = default(MostLikelyPathMaterial);
        var counter = 0UL;
        var charges = new UnitInterval32[Terms];
        var left = new UnitInterval32[Terms];
        var right = new UnitInterval32[Terms];

        for (var draw = 0; (draw < Draws); ++draw) {
            var winningExact = BigInteger.Zero;

            for (var term = 0; (term < Terms); ++term) {
                var chargeRaw = DrawRaw(counter: ref counter);
                var leftRaw = DrawRaw(counter: ref counter);
                var rightRaw = DrawRaw(counter: ref counter);

                charges[term] = UnitInterval32.Create(value: chargeRaw);
                left[term] = UnitInterval32.Create(value: leftRaw);
                right[term] = UnitInterval32.Create(value: rightRaw);

                var exact = (((BigInteger)chargeRaw * (BigInteger)leftRaw) * (BigInteger)rightRaw);

                if (exact > winningExact) { winningExact = exact; }
            }

            var fused = material.FusedChargedSum(charges: charges, left: left, right: right, lane: ChargeLane.General);
            var expected = RoundTiesToEvenLocal(exact: winningExact, shift: 64);

            if (fused.Value != expected) {
                return $"draw {draw}: the three-term fused competing fold reads {fused.Value}, where rounding the winning exact term {winningExact} once at shift 64 gives {expected}";
            }
        }

        return null;
    }
}
