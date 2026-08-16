using System.Text;

using Xunit;

namespace Puck.Maths.Tests;

/// <summary>A binary operation on algebra elements, raw in and raw out.</summary>
/// <param name="u1">The first element's scalar part.</param>
/// <param name="v1">The first element's root coefficient.</param>
/// <param name="u2">The second element's scalar part.</param>
/// <param name="v2">The second element's root coefficient.</param>
/// <returns>The result components.</returns>
internal delegate (long U, long V) BinaryElemOp(long u1, long v1, long u2, long v2);
/// <summary>A unary operation on one algebra element, raw in and raw out.</summary>
/// <param name="u">The scalar part.</param>
/// <param name="v">The root coefficient.</param>
/// <returns>The result components.</returns>
internal delegate (long U, long V) UnaryElemOp(long u, long v);
/// <summary>A scalar-valued operation on one algebra element (a norm), raw in and raw out.</summary>
/// <param name="u">The scalar part.</param>
/// <param name="v">The root coefficient.</param>
/// <returns>The scalar result.</returns>
internal delegate long ScalarElemOp(long u, long v);
/// <summary>A binary operation on two carrier scalars, raw in and raw out.</summary>
/// <param name="a">The first operand.</param>
/// <param name="b">The second operand.</param>
/// <returns>The result.</returns>
internal delegate long ScalarBinaryOp(long a, long b);
/// <summary>A binary operation on lane vectors of raws — the shape every multi-lane algebra takes once the element stops
/// being a pair. The result span is written in full, one lane per basis position.</summary>
/// <param name="left">The multiplicand's lanes.</param>
/// <param name="right">The multiplier's lanes.</param>
/// <param name="result">The destination lanes, the same width as the operands.</param>
internal delegate void VectorBinaryOp(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result);
/// <summary>A ternary operation on lane vectors of raws — the shape an associator takes.</summary>
/// <param name="a">The first operand's lanes.</param>
/// <param name="b">The second operand's lanes.</param>
/// <param name="c">The third operand's lanes.</param>
/// <param name="result">The destination lanes, the same width as the operands.</param>
internal delegate void VectorTernaryOp(ReadOnlySpan<long> a, ReadOnlySpan<long> b, ReadOnlySpan<long> c, Span<long> result);
/// <summary>A power of the adjoined root of the relation <c>x² = P·x + Q</c>, raw in and raw out.</summary>
/// <param name="p">The linear coefficient, raw Q16.</param>
/// <param name="q">The constant coefficient, raw Q16.</param>
/// <param name="exponent">The power.</param>
/// <returns>The result components.</returns>
internal delegate (long U, long V) PowerOp(long p, long q, ulong exponent);

/// <summary>
/// Module 3 — generic law combinators, each written ONCE and instantiated per subject in <see cref="LawRegistry"/>.
/// Laws take a <see cref="Domain"/> and pull operands from <see cref="Domains"/>; none hardcodes operand generation.
/// On a mismatch a law fails with the domain key, the derived seed, the frontier index, and the raw operands, so the
/// failure reproduces without re-running the sweep. The API is deliberately framework-thin: each law is a plain static
/// method a future runner change can re-drive cheaply.
/// </summary>
internal static class Laws {
    /// <summary>BitIdenticalToOracle for a binary element operation: the subject equals the shared-nothing oracle on
    /// every operand quad of the domain.</summary>
    public static void BinaryMatchesOracle(string lawId, Domain domain, Tier tier, BinaryElemOp subject, BinaryElemOp oracle) {
        LawShapes.Report(shape: LawShape.OracleAgreement);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (u1, v1, u2, v2) in Domains.Quads(domain: domain, index: index, tier: tier)) {
            var actual = subject(u1, v1, u2, v2);
            var expected = oracle(u1, v1, u2, v2);

            if (actual != expected) {
                Fail(detail: $"operands=({u1},{v1},{u2},{v2}) subject=({actual.U},{actual.V}) oracle=({expected.U},{expected.V})", domain: domain, index: index, lawId: lawId);
            }
        }
    }
    /// <summary>TwinIdentity for two subject binary operations that must agree bit-for-bit (for example a hand-written
    /// planar type versus the corresponding <see cref="QuadraticAlgebra{TScalar}"/> lane), with the THIRD LEG beside
    /// them: an independently authored witness both sides must also equal, on the same operand stream.</summary>
    /// <param name="lawId">The case id, quoted on failure.</param>
    /// <param name="domain">The operand domain.</param>
    /// <param name="tier">The tier, which sets the sweep length.</param>
    /// <param name="first">The first subject.</param>
    /// <param name="second">The second subject.</param>
    /// <param name="witness">The independent third leg, or <see langword="null"/> when none stands beside this twin.
    /// The parameter is REQUIRED so every twin in the registry says at its call site whether it has one: where subject
    /// and twin share a rounding kernel, agreement proves everything except the shared part, and a null here is that
    /// gap admitted in code rather than in prose.</param>
    public static void TwinBinary(string lawId, Domain domain, Tier tier, BinaryElemOp first, BinaryElemOp second, BinaryElemOp? witness) {
        LawShapes.Report(shape: LawShape.Twin);

        if (witness is not null) { LawShapes.Report(shape: LawShape.Witnessed); }

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (u1, v1, u2, v2) in Domains.Quads(domain: domain, index: index, tier: tier)) {
            var a = first(u1, v1, u2, v2);
            var b = second(u1, v1, u2, v2);

            if (a != b) {
                Fail(detail: $"operands=({u1},{v1},{u2},{v2}) first=({a.U},{a.V}) second=({b.U},{b.V})", domain: domain, index: index, lawId: lawId);
            }

            if (witness is not null) {
                var c = witness(u1, v1, u2, v2);

                if (a != c) {
                    Fail(detail: $"operands=({u1},{v1},{u2},{v2}) twin=({a.U},{a.V}) witness=({c.U},{c.V})", domain: domain, index: index, lawId: lawId);
                }
            }
        }
    }
    /// <summary>TwinIdentity for two scalar-valued element operations that must agree bit-for-bit, with the third leg
    /// beside them. The scalar sibling of <see cref="TwinBinary"/>, for a norm twin whose second side is a SUBJECT and
    /// so must never sit in an oracle-shaped slot.</summary>
    /// <param name="lawId">The case id, quoted on failure.</param>
    /// <param name="domain">The operand domain.</param>
    /// <param name="tier">The tier, which sets the sweep length.</param>
    /// <param name="first">The first subject.</param>
    /// <param name="second">The second subject.</param>
    /// <param name="witness">The independent third leg, or <see langword="null"/> when none stands beside this twin.</param>
    public static void ScalarTwin(string lawId, Domain domain, Tier tier, ScalarElemOp first, ScalarElemOp second, ScalarElemOp? witness) {
        LawShapes.Report(shape: LawShape.Twin);

        if (witness is not null) { LawShapes.Report(shape: LawShape.Witnessed); }

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (u, v) in Domains.Pairs(domain: domain, index: index, tier: tier)) {
            var a = first(u, v);
            var b = second(u, v);

            if (a != b) {
                Fail(detail: $"operands=({u},{v}) first={a} second={b}", domain: domain, index: index, lawId: lawId);
            }

            if (witness is not null) {
                var c = witness(u, v);

                if (a != c) {
                    Fail(detail: $"operands=({u},{v}) twin={a} witness={c}", domain: domain, index: index, lawId: lawId);
                }
            }
        }
    }
    /// <summary>Purity for a binary element operation: two back-to-back calls on the same operands, in this process,
    /// return identical bits. This is a same-process purity check on one compiled body — it observes hidden mutable
    /// state or an operand-independent result, and nothing else. It is NOT the cross-run/cross-machine determinism
    /// contract: a codegen or environment difference lands between runs, never inside one expression.</summary>
    public static void PureBinary(string lawId, Domain domain, Tier tier, BinaryElemOp op) {
        LawShapes.Report(shape: LawShape.SelfContained);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (u1, v1, u2, v2) in Domains.Quads(domain: domain, index: index, tier: tier)) {
            if (op(u1, v1, u2, v2) != op(u1, v1, u2, v2)) {
                Fail(detail: $"operands=({u1},{v1},{u2},{v2}) repeated call differed", domain: domain, index: index, lawId: lawId);
            }
        }
    }
    /// <summary>BitIdenticalToOracle for a scalar-valued element operation (a norm).</summary>
    public static void ScalarMatchesOracle(string lawId, Domain domain, Tier tier, ScalarElemOp subject, ScalarElemOp oracle) {
        LawShapes.Report(shape: LawShape.OracleAgreement);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (u, v) in Domains.Pairs(domain: domain, index: index, tier: tier)) {
            var actual = subject(u, v);
            var expected = oracle(u, v);

            if (actual != expected) {
                Fail(detail: $"operands=({u},{v}) subject={actual} oracle={expected}", domain: domain, index: index, lawId: lawId);
            }
        }
    }
    /// <summary>BitIdenticalToOracle for a carrier-scalar binary operation (for example <see cref="FixedQ4816"/>
    /// multiply or add versus the dyadic oracle).</summary>
    public static void ScalarBinaryMatchesOracle(string lawId, Domain domain, Tier tier, ScalarBinaryOp subject, ScalarBinaryOp oracle) {
        LawShapes.Report(shape: LawShape.OracleAgreement);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (a, b) in Domains.Pairs(domain: domain, index: index, tier: tier)) {
            var actual = subject(a, b);
            var expected = oracle(a, b);

            if (actual != expected) {
                Fail(detail: $"operands=({a},{b}) subject={actual} oracle={expected}", domain: domain, index: index, lawId: lawId);
            }
        }
    }
    /// <summary>Purity for a carrier-scalar binary operation; the scalar twin of <see cref="PureBinary"/>, with the
    /// same same-process scope.</summary>
    public static void PureScalarBinary(string lawId, Domain domain, Tier tier, ScalarBinaryOp op) {
        LawShapes.Report(shape: LawShape.SelfContained);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (a, b) in Domains.Pairs(domain: domain, index: index, tier: tier)) {
            if (op(a, b) != op(a, b)) {
                Fail(detail: $"operands=({a},{b}) repeated call differed", domain: domain, index: index, lawId: lawId);
            }
        }
    }
    /// <summary>RoundTrip: <paramref name="inverse"/> undoes <paramref name="forward"/> — applying both returns the
    /// original element bit-for-bit (for example a conjugation or negation involution).</summary>
    public static void RoundTrip(string lawId, Domain domain, Tier tier, UnaryElemOp forward, UnaryElemOp inverse) {
        LawShapes.Report(shape: LawShape.SelfContained);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (u, v) in Domains.Pairs(domain: domain, index: index, tier: tier)) {
            var (fu, fv) = forward(u, v);
            var restored = inverse(fu, fv);

            if (restored != (u, v)) {
                Fail(detail: $"operands=({u},{v}) round-trip=({restored.U},{restored.V})", domain: domain, index: index, lawId: lawId);
            }
        }
    }
    /// <summary>IdentityElement: the given element is a two-sided identity for the operation.</summary>
    public static void IdentityElement(string lawId, Domain domain, Tier tier, BinaryElemOp op, long identityU, long identityV) {
        LawShapes.Report(shape: LawShape.SelfContained);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (u, v) in Domains.Pairs(domain: domain, index: index, tier: tier)) {
            var right = op(u, v, identityU, identityV);
            var left = op(identityU, identityV, u, v);

            if ((right != (u, v)) || (left != (u, v))) {
                Fail(detail: $"operands=({u},{v}) left=({left.U},{left.V}) right=({right.U},{right.V})", domain: domain, index: index, lawId: lawId);
            }
        }
    }
    /// <summary>ConjugateSymmetry: conjugation is an involution that distributes over multiplication —
    /// <c>conj(a·b) == conj(a)·conj(b)</c> for the commutative planar algebras.</summary>
    public static void ConjugateSymmetry(string lawId, Domain domain, Tier tier, BinaryElemOp mul, UnaryElemOp conj) {
        LawShapes.Report(shape: LawShape.SelfContained);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (u1, v1, u2, v2) in Domains.Quads(domain: domain, index: index, tier: tier)) {
            var product = mul(u1, v1, u2, v2);
            var conjugateOfProduct = conj(product.U, product.V);

            var (ca, cb) = conj(u1, v1);
            var (cc, cd) = conj(u2, v2);
            var productOfConjugates = mul(ca, cb, cc, cd);

            if (conjugateOfProduct != productOfConjugates) {
                Fail(detail: $"operands=({u1},{v1},{u2},{v2}) conj(ab)=({conjugateOfProduct.U},{conjugateOfProduct.V}) conj(a)conj(b)=({productOfConjugates.U},{productOfConjugates.V})", domain: domain, index: index, lawId: lawId);
            }
        }
    }
    /// <summary>NormMultiplicativity: <c>N(a·b) == N(a) ⊗ N(b)</c>, where <c>⊗</c> is <paramref name="combineNorms"/>.
    /// The caller supplies a domain constrained to an integer sublattice, so every product is exact and the identity
    /// holds bit-for-bit; the subject multiply and subject norm are both exercised.</summary>
    public static void NormMultiplicativity(string lawId, Domain domain, Tier tier, BinaryElemOp mul, ScalarElemOp norm, ScalarBinaryOp combineNorms) {
        LawShapes.Report(shape: LawShape.SelfContained);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (u1, v1, u2, v2) in Domains.Quads(domain: domain, index: index, tier: tier)) {
            var product = mul(u1, v1, u2, v2);
            var normOfProduct = norm(product.U, product.V);
            var productOfNorms = combineNorms(norm(u1, v1), norm(u2, v2));

            if (normOfProduct != productOfNorms) {
                Fail(detail: $"operands=({u1},{v1},{u2},{v2}) N(ab)={normOfProduct} N(a)*N(b)={productOfNorms}", domain: domain, index: index, lawId: lawId);
            }
        }
    }
    /// <summary>Möbius exactness/oracle law: the projective step's denominator is the input numerator and its numerator
    /// matches the oracle recomputation (exact for integer relations, one rounding otherwise).</summary>
    public static void MobiusMatchesOracle(string lawId, Domain domain, Tier tier, UnaryElemOp subject, ScalarBinaryOp oracleNumerator) {
        LawShapes.Report(shape: LawShape.OracleAgreement);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (n, d) in Domains.Pairs(domain: domain, index: index, tier: tier)) {
            var (numerator, denominator) = subject(n, d);
            var expected = oracleNumerator(n, d);

            if ((denominator != n) || (numerator != expected)) {
                Fail(detail: $"operands=({n},{d}) step=({numerator}:{denominator}) expectedNumerator={expected}", domain: domain, index: index, lawId: lawId);
            }
        }
    }
    /// <summary>TwinIdentity for two lane-vector operations that must agree bit-for-bit — the multi-lane sibling of
    /// <see cref="TwinBinary"/> (a derived multi-generator product against the hand-written kernel it reproduces), with
    /// the third leg beside them.</summary>
    /// <param name="lawId">The case id, quoted on failure.</param>
    /// <param name="domain">The operand domain.</param>
    /// <param name="tier">The tier, which sets the sweep length.</param>
    /// <param name="width">The lane count.</param>
    /// <param name="first">The first subject.</param>
    /// <param name="second">The second subject.</param>
    /// <param name="witness">The independent third leg, or <see langword="null"/> when none stands beside this twin.</param>
    public static void VectorTwin(string lawId, Domain domain, Tier tier, int width, VectorBinaryOp first, VectorBinaryOp second, VectorBinaryOp? witness) {
        LawShapes.Report(shape: LawShape.Twin);

        if (witness is not null) { LawShapes.Report(shape: LawShape.Witnessed); }

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);
        var a = new long[width];
        var b = new long[width];
        var c = ((witness is null) ? [] : new long[width]);

        foreach (var (left, right) in Domains.Vectors(domain: domain, index: index, tier: tier, width: width)) {
            first(left, right, a);
            second(left, right, b);

            var lane = FirstDifference(left: a, right: b);

            if (lane >= 0) {
                Fail(lawId: lawId, domain: domain, index: index, detail: $"left=[{Render(values: left)}] right=[{Render(values: right)}] lane={lane} first={a[lane]} second={b[lane]}");
            }

            if (witness is not null) {
                witness(left, right, c);

                var witnessLane = FirstDifference(left: a, right: c);

                if (witnessLane >= 0) {
                    Fail(lawId: lawId, domain: domain, index: index, detail: $"left=[{Render(values: left)}] right=[{Render(values: right)}] lane={witnessLane} twin={a[witnessLane]} witness={c[witnessLane]}");
                }
            }
        }
    }
    /// <summary>BitIdenticalToOracle for a lane-vector operation: the subject equals the shared-nothing oracle on every
    /// operand pair of the domain.</summary>
    public static void VectorMatchesOracle(string lawId, Domain domain, Tier tier, int width, VectorBinaryOp subject, VectorBinaryOp oracle) {
        LawShapes.Report(shape: LawShape.OracleAgreement);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);
        var actual = new long[width];
        var expected = new long[width];

        foreach (var (left, right) in Domains.Vectors(domain: domain, index: index, tier: tier, width: width)) {
            subject(left, right, actual);
            oracle(left, right, expected);

            var lane = FirstDifference(left: actual, right: expected);

            if (lane >= 0) {
                Fail(lawId: lawId, domain: domain, index: index, detail: $"left=[{Render(values: left)}] right=[{Render(values: right)}] lane={lane} subject={actual[lane]} oracle={expected[lane]}");
            }
        }
    }
    /// <summary>TwinIdentity for two ternary lane-vector operations — the shape an associator twin takes, where the
    /// statement needs three independent operands rather than two — with the third leg beside them.</summary>
    /// <param name="lawId">The case id, quoted on failure.</param>
    /// <param name="domain">The operand domain.</param>
    /// <param name="tier">The tier, which sets the sweep length.</param>
    /// <param name="width">The lane count.</param>
    /// <param name="first">The first subject.</param>
    /// <param name="second">The second subject.</param>
    /// <param name="witness">The independent third leg, or <see langword="null"/> when none stands beside this twin.</param>
    public static void VectorTernaryTwin(string lawId, Domain domain, Tier tier, int width, VectorTernaryOp first, VectorTernaryOp second, VectorTernaryOp? witness) {
        LawShapes.Report(shape: LawShape.Twin);

        if (witness is not null) { LawShapes.Report(shape: LawShape.Witnessed); }

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);
        var x = new long[width];
        var y = new long[width];
        var z = ((witness is null) ? [] : new long[width]);

        foreach (var (a, b, c) in Domains.VectorTriples(domain: domain, index: index, tier: tier, width: width)) {
            first(a, b, c, x);
            second(a, b, c, y);

            var lane = FirstDifference(left: x, right: y);

            if (lane >= 0) {
                Fail(lawId: lawId, domain: domain, index: index, detail: $"a=[{Render(values: a)}] b=[{Render(values: b)}] c=[{Render(values: c)}] lane={lane} first={x[lane]} second={y[lane]}");
            }

            if (witness is not null) {
                witness(a, b, c, z);

                var witnessLane = FirstDifference(left: x, right: z);

                if (witnessLane >= 0) {
                    Fail(lawId: lawId, domain: domain, index: index, detail: $"a=[{Render(values: a)}] b=[{Render(values: b)}] c=[{Render(values: c)}] lane={witnessLane} twin={x[witnessLane]} witness={z[witnessLane]}");
                }
            }
        }
    }
    /// <summary>TwinIdentity for two power schedules that must agree bit-for-bit, over the domain's relation
    /// coefficients and a fixed exponent ladder, with the third leg beside them. The ladder is part of the law: it spans
    /// zero, the small exponents whose square-and-multiply schedules differ most from a sequential fold, and a
    /// four-digit exponent.</summary>
    /// <param name="lawId">The case id, quoted on failure.</param>
    /// <param name="domain">The relation-coefficient domain.</param>
    /// <param name="tier">The tier, which sets the sweep length.</param>
    /// <param name="first">The first subject.</param>
    /// <param name="second">The second subject.</param>
    /// <param name="witness">The independent third leg, or <see langword="null"/> when none stands beside this twin.</param>
    public static void TwinPower(string lawId, Domain domain, Tier tier, PowerOp first, PowerOp second, PowerOp? witness) {
        LawShapes.Report(shape: LawShape.Twin);

        if (witness is not null) { LawShapes.Report(shape: LawShape.Witnessed); }

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (p, q) in Domains.Pairs(domain: domain, index: index, tier: tier)) {
            foreach (var exponent in ExponentLadder) {
                var a = first(p, q, exponent);
                var b = second(p, q, exponent);

                if (a != b) {
                    Fail(detail: $"relation=({p},{q}) exponent={exponent} first=({a.U},{a.V}) second=({b.U},{b.V})", domain: domain, index: index, lawId: lawId);
                }

                if (witness is not null) {
                    var c = witness(p, q, exponent);

                    if (a != c) {
                        Fail(detail: $"relation=({p},{q}) exponent={exponent} twin=({a.U},{a.V}) witness=({c.U},{c.V})", domain: domain, index: index, lawId: lawId);
                    }
                }
            }
        }
    }
    /// <summary>DivergenceCanary: two disciplines that are ALLOWED to differ must actually differ, on at least
    /// <paramref name="minimumDivergences"/> of the domain's operand pairs.</summary>
    /// <remarks>This is the inverse of every other law here, and it is what keeps the fused one-rounding contract from
    /// being decorative: if folding every term before rounding once agreed with rounding each term first, the fused
    /// kernels would be buying nothing. A count floor rather than an exact count, because the domain sweeps fresh
    /// operands every run.</remarks>
    public static void DivergenceCanary(string lawId, Domain domain, Tier tier, int width, VectorBinaryOp fused, VectorBinaryOp perProduct, int minimumDivergences) {
        LawShapes.Report(shape: LawShape.Divergence);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);
        var a = new long[width];
        var b = new long[width];
        var cases = 0L;
        var divergences = 0L;

        foreach (var (left, right) in Domains.Vectors(domain: domain, index: index, tier: tier, width: width)) {
            fused(left, right, a);
            perProduct(left, right, b);

            ++cases;

            if (FirstDifference(left: a, right: b) >= 0) { ++divergences; }
        }

        if (divergences < minimumDivergences) {
            Fail(detail: $"fused and per-product agreed too often: {divergences} divergence(s) over {cases} case(s), floor {minimumDivergences}", domain: domain, index: index, lawId: lawId);
        }
    }
    /// <summary>Records one concrete counterexample to an equality that is known to be false. The supplied witness
    /// returns <see langword="null"/> only when the two sides differ as documented.</summary>
    /// <param name="lawId">The case id, quoted on failure.</param>
    /// <param name="counterexample">The fixed witness for the false statement.</param>
    public static void KnownFalse(string lawId, Func<string?> counterexample) {
        LawShapes.Report(shape: LawShape.Divergence);

        string? detail;

        try {
            detail = counterexample();
        } catch (Exception exception) {
            Assert.Fail(message: $"{lawId} threw {exception.GetType().Name}: {exception.Message}");

            return;
        }

        if (detail is not null) {
            Assert.Fail(message: $"{lawId} {detail}");
        }
    }
    /// <summary>An exhaustive structural claim over a presentation's OWN basis rather than a sampled operand stream —
    /// the shape of a statement quantified over every ordered key pair, or over a single computed certificate. The claim
    /// returns <see langword="null"/> when it holds and the counterexample text when it does not, so the assertion stays
    /// in this module and the registry keeps its declaration-only shape.</summary>
    public static void Claim(string lawId, Func<string?> claim) {
        LawShapes.Report(shape: LawShape.Claim);

        string? detail;

        try {
            detail = claim();
        } catch (Exception exception) {
            Assert.Fail(message: $"{lawId} threw {exception.GetType().Name}: {exception.Message}");

            return;
        }

        if (detail is not null) {
            Assert.Fail(message: $"{lawId} {detail}");
        }
    }
    /// <summary>A claim evaluated on every operand pair of the domain, rather than on one presentation's basis: the
    /// swept sibling of <see cref="Claim"/>, for statements whose subject is a whole family of carriers and so cannot be
    /// expressed as one lane vector.</summary>
    public static void SweptClaim(string lawId, Domain domain, Tier tier, int width, Func<long[], long[], string?> claim) {
        LawShapes.Report(shape: LawShape.Claim);

        var (index, _) = Frontier.Consume(key: domain.Key, block: domain.Block);

        foreach (var (left, right) in Domains.Vectors(domain: domain, index: index, tier: tier, width: width)) {
            if (claim(left, right) is { } detail) {
                Fail(lawId: lawId, domain: domain, index: index, detail: $"left=[{Render(values: left)}] right=[{Render(values: right)}] {detail}");
            }
        }
    }

    // The exponent ladder every power twin runs: zero, the low exponents, a Mersenne boundary, a byte boundary, and a
    // four-digit exponent whose square-and-multiply chain is ten squarings deep.
    private static readonly ulong[] ExponentLadder = [0UL, 1UL, 2UL, 3UL, 4UL, 5UL, 7UL, 8UL, 13UL, 31UL, 64UL, 255UL, 1000UL];

    private static int FirstDifference(ReadOnlySpan<long> left, ReadOnlySpan<long> right) {
        for (var lane = 0; (lane < left.Length); ++lane) {
            if (left[lane] != right[lane]) { return lane; }
        }

        return -1;
    }
    private static string Render(ReadOnlySpan<long> values) {
        var builder = new StringBuilder();

        for (var lane = 0; (lane < values.Length); ++lane) {
            if (lane > 0) { _ = builder.Append(value: ','); }

            _ = builder.Append(value: values[lane]);
        }

        return builder.ToString();
    }
    private static void Fail(string lawId, Domain domain, long index, string detail) =>
        Assert.Fail(message: $"{lawId} [{domain.Key}] seed={domain.Seed(index: index)} k={index} {detail}");
}
