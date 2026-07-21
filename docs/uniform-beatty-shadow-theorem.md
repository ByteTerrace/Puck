# The uniform Beatty-shadow theorem

## Status

**IN PROGRESS — the eventual finite-channel theorem is proved and implemented; the advertised uniform total decider
is not. A previously proposed integer-avoidance lemma is false, and its first non-affine rational family is now
classified and recognized exactly.**

The implementation now proves arbitrary-order tail remainders, finite norm reduction, finite generalized-Pell orbit
reduction, and effective sign stabilization. Thus every sufficiently large nonzero discrepancy has a finite exact
channel presentation, and the toolkit decides whether the discrepancy is eventually identically zero.

One obligation remains before the proposed theorem may be cited as stated: totalize the finite-prefix comparison when
a tail may equal an integer exactly. The generalized-Pell presentation now compiles into an explicit Ostrowski DFAO,
the rational-slope branch compiles into a radix-2 DFAO, and both branches splice in every finitely certified prefix.

The second item is not routine numerical cleanup. Degree-(2,1) polynomial continued fractions include rational-limit
families and special-function values, so a blanket equality oracle risks subsuming unresolved irrationality questions.
The API deliberately does not pretend that shrinking rational enclosures decide equality.

## Proposed theorem

Let

\[
  s_n=pn+q+\frac{rn^2+un+v}{s_{n+1}}
\]

be the unique everywhere-positive tail for integer parameters satisfying the positivity hypotheses checked by
`PolynomialContinuedFractionTail.Analyze`. Put

\[
  \lambda=\frac{p+\sqrt{p^2+4r}}2,
  \qquad
  \beta=\frac{q\lambda^2+(u-r)\lambda}{\lambda^2+r},
  \qquad
  x_n=\lambda n+\beta,
\]

and define

\[
  d_n=\lfloor s_n\rfloor-\lfloor x_n\rfloor.
\]

The target theorem has three parts.

1. If \(\lambda\) is irrational, \((d_n)\) is effectively automatic in the Ostrowski numeration system of
   \(\lambda\). If \(\lambda\) is rational, it is effectively automatic in an ordinary positional numeration system.
2. There is a terminating exact algorithm which constructs that automaton and decides whether \(d_n=0\) for every
   \(n\geq1\), returning a finite counterexample or a finite equality certificate.
3. The construction is uniform in the five integer parameters; it does not assume a metallic one-term continued
   fraction.

This would turn the isolated exact-floor phenomena behind metallic hiccup sequences into a decision theorem for the
whole positive degree-\((2,1)\) polynomial-tail family.

## 1. Exact affine defect

Write

\[
  a_n=pn+q,
  \qquad
  b_n=rn^2+un+v,
  \qquad
  T_n(y)=a_n+\frac{b_n}{y}.
\]

The definitions of \(\lambda\) and \(\beta\) cancel the quadratic and linear powers of \(n\) in
\((a_n-x_n)x_{n+1}+b_n\). Consequently there is a constant

\[
  R=(q-\beta)(\lambda+\beta)+v
\]

such that the exact identity

\[
  T_n(x_{n+1})-x_n=\frac{R}{x_{n+1}}
\]

holds for every positive integer \(n\). For \(e_n=s_n-x_n\), subtraction gives the exact error recurrence

\[
  e_n=
  \frac{R}{x_{n+1}}
  -\frac{b_ne_{n+1}}{x_{n+1}(x_{n+1}+e_{n+1})}.
  \tag{1}
\]

`PolynomialContinuedFractionTail` already constructs a positive invariant interval proving

\[
  |e_n|\leq\frac Hn \qquad(n\geq N)
  \tag{2}
\]

for explicit integers \(H\geq0\) and \(N\geq1\).

The toolkit now constructs the same kind of certificate to every finite order. Given exact formal coefficients
\(c_0,\ldots,c_{k-1}\), put

\[
 z_n=\lambda n+c_0+\frac{c_1}{n}+\cdots+\frac{c_{k-1}}{n^{k-1}}.
\]

Writing \(z_n=P(n)/n^{k-1}\), the recurrence residual has the exact form

\[
 T_n(z_{n+1})-z_n=
 \frac{A(n)}{n^{k-1}P(n+1)},
\]

where formal cancellation gives \(\deg A<k\). Exact coefficient bounds therefore give
\(|T_n(z_{n+1})-z_n|\le M/n^k\). A rational pair
\(0<L_2<L_1<\lambda\) with \(L_1L_2>r\) supplies a uniform contraction, producing explicit integers
\(N_k,H_k\) with

\[
 |s_n-z_n|\le\frac{H_k}{n^k}\qquad(n\ge N_k).
 \tag{2a}
\]

`PolynomialTailAsymptoticCertificate` stores the cleared finite inequalities. The independent verifier exercises
4,032 such certificates through order six and compares selected intervals with exact backward tail enclosures.

## 2. Finite-norm reduction

Choose a common positive denominator \(Z\) and integers \(P,Q,A,B,D\) such that

\[
  \lambda=\frac{P+Q\sqrt D}{Z},
  \qquad
  \beta=\frac{A+B\sqrt D}{Z}.
\]

Thus

\[
  x_n=\frac{X_n+Y_n\sqrt D}{Z},
  \qquad
  X_n=Pn+A,
  \qquad
  Y_n=Qn+B.
\]

Suppose \(\lfloor s_n\rfloor\ne\lfloor x_n\rfloor\) for some \(n\geq N\). An integer \(m\) then lies between
\(s_n\) and \(x_n\), so (2) gives

\[
  |m-x_n|\leq\frac Hn.
\]

The cleared field norm of this boundary difference is the integer

\[
  J_{n,m}
  =Z^2\operatorname{Norm}(m-x_n)
  =(Zm-X_n)^2-DY_n^2.
  \tag{3}
\]

Conjugation and the triangle inequality give

\[
\begin{aligned}
 |m-x_n'|
 &\leq |m-x_n|+|x_n-x_n'|\\
 &\leq \frac Hn+
   \frac{2|Q|\sqrt D}{Z}n+
   \frac{2|B|\sqrt D}{Z}.
\end{aligned}
\]

Let

\[
  L=\left\lceil\frac{2|Q|\sqrt D}{Z}\right\rceil,
  \qquad
  C=\left\lceil\frac{2|B|\sqrt D}{Z}\right\rceil.
\]

For \(n\geq1\), equations (2) and (3) therefore imply

\[
  |J_{n,m}|\leq Z^2H(H+L+C)=:J.
  \tag{4}
\]

This is the first finiteness theorem: every discrepancy beyond \(N\) belongs to one of the explicitly finite equations

\[
  U^2-DY^2=h,
  \qquad -J\leq h\leq J,
  \tag{5}
\]

with the additional linear congruences

\[
  Y=Qn+B,
  \qquad
  U+Pn+A\equiv0\pmod Z.
  \tag{6}
\]

The public `PolynomialBeattyShadowNormCertificate` is exactly (3)--(4). The independent verifier checks the cleared
representation and envelope on a broad finite parameter box.

## 3. Finite generalized-Pell orbit reduction

Assume \(D\) is not a square and let

\[
  \varepsilon=c+d\sqrt D>1,
  \qquad c^2-Dd^2=1,
\]

be the fundamental positive norm-one unit. Multiplication by \(\varepsilon\) preserves every equation (5):

\[
  (U,Y)\longmapsto(cU+DdY,\ dU+cY).
  \tag{7}
\]

Every orbit contains a bounded representative. Indeed, multiply a nonzero solution
\(\alpha=U+Y\sqrt D\) by a power of \(\varepsilon\) until

\[
  \sqrt{|h|/\varepsilon}\leq|\alpha|<\sqrt{|h|\varepsilon}.
\]

Since \(|\alpha'|=|h|/|\alpha|\), both real embeddings then have magnitude at most
\(\sqrt{|h|\varepsilon}\). Hence

\[
  U^2<2|h|c,
  \qquad
  DY^2<2|h|c,
  \tag{8}
\]

where \(\varepsilon=c+d\sqrt D<2c\). Exhausting the finite box (8) meets every orbit. `PellEquation` computes
\(\varepsilon\) by the continued fraction of \(\sqrt D\) and returns this finite, independently checkable orbit cover.

Modulo the fixed proof modulus \(|Q|Z\), transformation (7) is an invertible map of a finite set.
Consequently the congruences (6) select a periodic set of exponents on each Pell orbit. This proves that every possible
discrepancy index lies in an effectively constructible finite union of quadratic recurrence channels.

## 4. Effective sign stabilization

The norm and orbit reductions locate every index where a discrepancy *can* occur. It remains to decide whether the
tail crosses the near-center root belonging to each norm \(h\).

Let \(x_n'\) be the conjugate affine center and put

\[
 \Delta_n=x_n-x_n'=d_1n+d_0,
 \qquad c=\frac{h}{Z^2}.
\]

The near-center boundary gap \(g_n=m_h(n)-x_n\) is the small root of

\[
 g_n(\Delta_n+g_n)=c.
 \tag{9}
\]

Its coefficients \(g_n=a_1/n+a_2/n^2+\cdots\) obey the effective recursion

\[
 a_1=\frac c{d_1},
 \qquad
 a_{j+1}=-\frac{d_0a_j+\sum_{i=1}^{j-1}a_i a_{j-i}}{d_1}.
 \tag{10}
\]

Substitution of a truncation into (9), followed by exact root separation, constructs an explicit
\(K_j/n^{j+1}\) remainder. This is `PolynomialBeattyBoundaryAsymptoticCertificate`.

At first order, the tail and boundary either differ or collide. The apparent infinite-order exceptional case collapses:
direct expansion of (1), use of the affine coefficient identity, and
\(\operatorname{Tr}(\beta)=q\) give

\[
 c_2-a_2=-\frac{p\lambda}{\lambda^2+r}\,c_1.
 \tag{11}
\]

Hence a nonzero first-order collision always separates at second order. If \(c_1=0\), then the affine residual
\(R=0\); norm \(h=0\) is the exact affine solution and uniqueness gives \(s_n=x_n\). There is no unbounded coefficient
dichotomy left.

Combining (2a) with the boundary remainder gives a computable cutoff after which the sign of \(m_h(n)-s_n\) is fixed.
`PolynomialBeattyShadowNormDecisionCertificate` records that sign and the corresponding floor discrepancy. The exact
decision verifier currently checks 34 norms, including three genuine second-order collisions, across 4,376 stabilized
Pell-channel points.

## 5. Eventual finite-channel theorem

Enumerate the finite norm interval (4), attach the sign decision from Section 4, and retain precisely the positive-real
Pell channels whose adjacent boundary is crossed. The resulting finite object has a global cutoff \(N_*\) and satisfies

\[
 \{n\ge N_*:d_n\ne0\}
 =\bigcup_{C\in\mathcal C}\{n(C,k):k\ge k_C\}.
 \tag{12}
\]

Every \(n(C,k)\) is decoded from one fixed orbit under a norm-one quadratic unit, with its exponent restricted by a
finite residue cycle. `PolynomialBeattyShadow.EventualCertificate` constructs exactly this presentation and decides
whether \(d_n=0\) for all \(n\ge N_*\).

When the discriminant is square, \(\lambda\) is integral and \(\beta\) is rational. Its fractional part is fixed, so
the order-one interval proves eventual zero unless \(\beta\) is integral. In the integral-offset case, the sign of
\(c_1/n+O(n^{-2})\) gives the eventual value (zero or minus one), with \(c_1=0\) again the exact affine case.
`RationalSlopeDecisionCertificate` implements this positional-numeration branch.

For a positive channel, exact greedy representations eventually have the form

\[
 P\,B^k\,S. \tag{13}
\]

The extractor certifies (13), rather than trusting sampled strings: it checks canonical digit constraints across every
join, verifies that shifting by \(|B|\) digits has determinant one and trace \(2U\), and matches the affine recurrence
and initial channel values. The regular expression is then compiled through an epsilon-NFA determinization, and all
active channel machines are product-composed into a deterministic output automaton. The resulting
`PolynomialBeattyShadowOstrowskiCertificate` evaluates \(d_n\) directly from the canonical Ostrowski digits for every
\(n\) beyond its explicit cutoff. Independent checks currently cover five quadratic systems, 20 regular languages,
and 500 exact channel words, including nontrivial alternating blocks and a two-term continued-fraction period.

Termination of pattern extraction follows from finite-state normalization in quadratic Ostrowski numeration: the
channel unit and the continued-fraction period unit are commensurable in the rank-one positive unit group, while fixed
linear offsets are normalized by the finite addition transducer. Soundness does not rely on that termination argument;
every returned pattern carries the direct recurrence certificate above.

## 6. Why the total identity decider remains open

For each of the finitely many \(n<N_*\), positive finite truncations give nested exact rational enclosures for \(s_n\).
They decide the floor unless the limit is exactly an integer boundary. Merely iterating until the interval is narrow is
not a terminating equality algorithm.

This obstruction is genuine in the surrounding class. Bowman and McLaughlin document both rational and irrational
limits for polynomial continued fractions and note that the classical degree-(2,1) family is evaluated through
hypergeometric functions. Modern work on polynomial-continued-fraction Diophantine approximations likewise emphasizes
that systematic irrationality detection is not known in general. Therefore a uniform integer-equality procedure needs
a separate theorem; it cannot be silently assumed from convergence.

Kenison, Klurman, Lefaucheux, Luca, Moree, Ouaknine, Whiteland, and Worrell formalize precisely this operation as the
**PCF Equality Problem**: decide whether a convergent polynomial continued fraction equals a specified algebraic
number. They prove that the general PCF Equality Problem and minimality for second-order holonomic recurrences are
interreducible. In the linear-coefficient setting, their remaining equality tests are zero tests for periods,
exponential periods, and related integrals, with decidability obtained only conditionally from major period
conjectures. The integer-avoidance conjecture below is a sharply restricted instance, so this does not prove it hard;
it does prove that no general-purpose exact-real or continued-fraction routine supplies the missing step.

The implemented finite splice is exact and deliberately partial. `TryCertifiedFloor` propagates a certified far-tail
interval backward and succeeds as soon as both endpoints have the same floor. `TryTotalOstrowskiAutomaton` and
`TryTotalPositionalAutomaton` use those floors to add singleton word automata below the eventual cutoff. On success the
returned certificate is a genuine all-index DFAO; `IdenticallyZero` decides the universal identity and
`FirstCounterexample` returns its least failure. On an unresolved integer-straddling interval they return that exact
index and no certificate. Thus there is now no engineering gap concealed inside the mathematical one.

The first bounded enclosure search missed a rational non-affine family because its original coefficient box was too
small. Exact forward Riccati-orbit search subsequently found the infinite counterexample family

\[
 (p,q,r,u,v)=(p,0,2p+4,4p+12,0),
 \qquad
 s_n=(p+2)n+2-\frac2{n+1}
 \qquad(p\ge1).
\]

Here the discriminant is `(p+4)^2`, `s_1=p+3` is integral, and the tail is not affine. The recurrence identity factors
as

\[
 (s_n-pn)s_{n+1}
 =\frac{2n(n+2)}{n+1}
  \frac{(n+1)((p+2)n+2p+6)}{n+2}
 =2(p+2)n^2+4(p+3)n.
\]

Thus the proposed integer-avoidance lemma is false. `TryCertifiedRationalTail` now recognizes this entire family and
lets the finite-prefix constructor decide its exact floors without refinement. The independent counterexample verifier
checks 10,000 family members and 60,000 exact recurrence instances; the symbolic factorization above is the general
proof. See `polynomial-tail-integer-counterexamples.md`.

The broader equality obstruction remains: this family does not prove that every integer hit is a rational Riccati
solution of classifiable form. Positive degree-(2,1) continued fractions are hypergeometric ratios, and recent
Bauer--Muir work transforms this structural family into evaluations involving gamma quotients, `pi`, and `log 2`.

The strongest realized statement is consequently:

> For every admissible parameter tuple, eventual identity is decidable. In the irrational-slope case the discrepancy
> has an effectively constructible Ostrowski DFAO beyond an explicit cutoff and its sign on every channel is decidable
> at order at most two; in the rational-slope case it has an eventual radix-2 DFAO. Whenever the finite prefix contains
> no unresolved exact integer hit, these machines extend effectively to a total DFAO and decide all-index identity.

There is also an unconditional, but nonuniform, corollary: every discrepancy sequence in the family *is* Ostrowski
automatic (or positional automatic in the square case), because a finite modification of an automatic sequence is
automatic. What remains open is the word "effectively": uniformly extracting the unknown singleton outputs at an exact
integer hit is equivalent to the integer-avoidance problem above.

This is substantial, but it is not yet the three-part proposed theorem above.

## Verification commands

```text
dotnet build src/Puck.Maths/Puck.Maths.csproj -c Release --no-restore
dotnet run tools/quadratic-beatty-shadow-norm-verifier.cs -c Release
dotnet run tools/pell-equation-verifier.cs -c Release
dotnet run tools/polynomial-tail-asymptotic-certificate-verifier.cs
dotnet run tools/quadratic-beatty-shadow-channel-verifier.cs -c Release
dotnet run tools/quadratic-beatty-shadow-decision-verifier.cs
dotnet run tools/ostrowski-pell-channel-verifier.cs
dotnet run tools/polynomial-tail-integer-counterexample-verifier.cs
```

## References

- D. Bowman and J. McLaughlin, [Polynomial Continued Fractions](https://arxiv.org/abs/1812.08251).
- N. Ben David et al., [On the Connection Between Irrationality Measures and Polynomial Continued Fractions](https://arxiv.org/abs/2111.04468).
- P. Hieronymi and A. Terry Jr., [Ostrowski Numeration Systems, Addition and Finite Automata](https://arxiv.org/abs/1407.7000).
- L. Schaeffer, J. Shallit, and S. Zorcic, [Beatty Sequences for a Quadratic Irrational: Decidability and Applications](https://arxiv.org/abs/2402.08331).
- K.-W. Chen and C.-H. Liu, [Continued Fractions with Quadratic Numerators via the Bauer--Muir Transform](https://doi.org/10.3390/math13152332).
- G. Kenison et al., [On Positivity and Minimality for Second-Order Holonomic Sequences](https://doi.org/10.4230/LIPIcs.MFCS.2021.67).
