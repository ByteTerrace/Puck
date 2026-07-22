# The uniform Beatty-shadow theorem

## Status

**IN PROGRESS — the eventual finite-channel theorem is proved and implemented; the advertised uniform total decider
is not. A previously proposed integer-avoidance lemma is false. Positive rational-function Riccati tails over
the characteristic real quadratic field now have a structurally complete arbitrary-degree certificate, and the dense
executable recognizer accepts denominator degrees through its explicit resource ceiling of 128. A new
degree-one reduction and its aligned 1-period extension place further non-rational subfamilies in proven decidable
classes, but equality for the remaining hypergeometric tails is still not uniform.**

The implementation now proves arbitrary-order tail remainders, finite norm reduction, finite generalized-Pell orbit
reduction, and effective sign stabilization. Thus every sufficiently large nonzero discrepancy has a finite exact
channel presentation, and the toolkit decides whether the discrepancy is eventually identically zero.  The same
presentation now gives the effective bound `#{n <= N : d_n != 0}=O(log N)` for every irrational-slope instance.
On the unresolved integer-orbit side, exact prime-power profiling has isolated a finite-field monodromy criterion:
both its determinant and trace halves now have direct finite-field proofs.  They imply unconditional
superexponential EGF-denominator growth whenever the numerator discriminant is not a rational square, leaving only
the square-numerator, nonsquare-characteristic, nonaligned hypergeometric branch as the possible general obstruction.
Two new machine-checked trapping theorems nevertheless settle complementary
infinite two-parameter regions inside that branch.

One obligation remains before the proposed theorem may be cited as stated: totalize the finite-prefix comparison when
a tail may equal an integer exactly. The generalized-Pell presentation now compiles into an explicit Ostrowski DFAO,
the rational-slope branch compiles into a radix-2 DFAO, and both branches splice in every finitely certified prefix.

This obligation is not routine numerical cleanup. Degree-(2,1) polynomial continued fractions include rational-limit
families and special-function values, so a blanket equality oracle risks subsuming unresolved irrationality questions.
The API deliberately does not pretend that shrinking rational enclosures decide equality.

## Proposed theorem

Let

\[
  s_n=pn+q+\frac{rn^2+un+v}{s_{n+1}}
\]

be the unique everywhere-positive tail for integer parameters with \(p\geq1\) satisfying the positivity hypotheses checked by
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

The motivating metallic slice `(p,q,r,u,v)=(k,-1,1,0,0)` is already fully
machine-checked: `PuckMathsFormal.BDS.bds_conjecture` in
`formal/PuckMathsFormal/PuckMathsFormal/BDS/Theorem.lean` proves its discrepancy
is identically zero for every `k,n>=1`.

The metallic argument is no longer isolated.  The new theorem
`PuckMathsFormal.PolynomialTail.GeneralizedBDS.generalized_bds_floor` in
`formal/PuckMathsFormal/PuckMathsFormal/PolynomialTail/GeneralizedBDS.lean`
proves the following two-parameter extension.  For integers \(k\geq1\) and
\(-k\leq q\leq0\), every positive solution of

\[
  s_n=kn+q+\frac{n^2}{s_{n+1}}
\]

satisfies

\[
  \left\lfloor s_n\right\rfloor
  =\left\lfloor \alpha n+\beta\right\rfloor,
  \qquad
  \alpha=\frac{k+\sqrt{k^2+4}}2,
  \qquad
  \beta=\frac{q\alpha^2-\alpha}{\alpha^2+1}
\]

for every \(n\geq1\).  Thus the exact Beatty shadow persists while the constant
term ranges through an interval of \(k+1\) integer values; the published
metallic case is the single slice \(q=-1\).  The Lean proof includes strict
trap invariance, compactness-based existence, contraction uniqueness, an
integral quadratic-norm separation lemma, and the final floor equality.  This
family has square numerator discriminant and nonsquare characteristic
discriminant; except on the aligned slice \(2q=-k\), it lies precisely in the
formerly untouched stratum (E).  It therefore shows that (E) is an obstruction to the *uniform method*,
not a region in which exact Beatty equality is necessarily absent.

The numerator coefficient can also vary.  The theorem
`PuckMathsFormal.PolynomialTail.ScaledBDS.scaled_bds_floor` in
`formal/PuckMathsFormal/PuckMathsFormal/PolynomialTail/ScaledBDS.lean` proves
that for integers \(p,r\) with \(1\leq r\leq p\), every positive solution of

\[
  s_n=p(n-1)+\frac{r n^2}{s_{n+1}}
\]

satisfies

\[
  \lfloor s_n\rfloor=\lfloor\alpha n+\beta\rfloor,
  \qquad
  \alpha=\frac{p+\sqrt{p^2+4r}}2,
  \qquad
  \beta=\frac{r}{\sqrt{p^2+4r}}-\alpha
\]

for every \(n\geq1\).  Its integral norm is congruent to
\(-r^2\pmod {p^2+4r}\); the hypothesis \(r\leq p\) makes the least positive
norm at least \(p^2+4r-r^2\), strictly wider than the complete analytic trap
image.  This entire family has \(\Delta_B=0\), nonsquare
\(\Delta_c=p^2+4r\), and nonzero alignment residual \(R=pr\).  It is therefore
an unconditional two-dimensional equality region lying wholly inside (E),
not merely touching its aligned or rational-characteristic boundary.

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

For \(n\geq N\), equations (2) and (3) therefore imply

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

### Effective sparsity of the irrational discrepancy support

The finite-channel statement has a quantitative consequence that does not
need the unresolved finite-prefix equality oracle.  On one active channel let

\[
 \varepsilon=U+V\sqrt D>1
\]

be its positive period unit.  If its Pell point has second coordinate `Y_k`,
then `Y_(k+2)=2U Y_(k+1)-Y_k`, while decoding gives

\[
 n_k=\frac{Y_k-S}{Q}
\]

for fixed integers `S,Q`.  On the positive branch this yields
`n_k=Theta(epsilon^k)`.  Each channel therefore contributes only `O(log N)`
indices through `N`, with constants effectively recoverable from its exact
Pell data.  There are finitely many active channels, so in the irrational-slope
case

\[
 \#\{n\le N:d_n\ne0\}=O(\log N). \tag{S}
\]

In particular the discrepancy support has natural density zero.  Every
individual channel has exponentially spaced indices.  The finitely many
unknown prefix outputs cannot change (S), so this conclusion is unconditional
even before the total automaton has been effectively spliced.

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

Thus the proposed integer-avoidance lemma is false. More generally, substitution of

\[
 s_n=\lambda n+\beta+\frac{c}{n+d}
\]

shows that every non-affine solution of this form must satisfy

\[
 d(2\lambda-p)=2\beta+p-q,
 \qquad c=\lambda d-\lambda-\beta,
\]

after which the recurrence numerator must factor as

\[
 \bigl((\lambda-p)n+(\beta-q)-(\lambda-p)\bigr)
 \bigl(\lambda n+2\lambda+\beta\bigr).
\]

These identities are also sufficient when the resulting tail is positive. Positivity reduces exactly to finitely
many sign checks for a linear denominator and convex quadratic numerator. `TryLinearFractionalTailCertificate`
recognizes this class, and `VerifyLinearFractionalTailCertificate` checks its coefficient identities and global
positivity. The independent linear-fractional verifier checks
33,956 generated families and 203,736 exact recurrence instances, including 1,805 integer first tails. The original
counterexample verifier separately checks 10,000 family members and 60,000 recurrence instances. See
`polynomial-tail-integer-counterexamples.md`.

Hypergeometric recurrence solving exposed positive tails with polynomial
denominators of degrees two, three, and arbitrarily higher. For a reduced
rational tail with monic denominator `B` of degree `m`, pole cancellation
forces

\[
 s_n=\frac{B(n-1)C(n-1)}{B(n)},\qquad
 rn^2+un+v=C(n)K(n),
\]

where `C` and `K` are linear. Matching the affine slope and offset makes the
constant coefficient equation quadratic in `m`, leaving at most two degrees;
the coefficients of `B` then satisfy one exact linear system.
`TryRationalTailCertificate` implements this recognizer over the full
characteristic field `Q(lambda)` through denominator degree 128, while
`VerifyRationalTailCertificate` independently
checks the factorization, recurrence identity, and absence of positive-integer
poles. The positivity verifier also checks `c0>0`; otherwise the two linear
factors and the denominator-sign recurrence would force a degree-`m`
polynomial to alternate sign more than `m` times. The one-family checker
verifies degrees 0 through 32 in `(1,0,2,3m+2,0)`. A separate coefficient-box
sweep covers 4,019,652 admissible tuples, recognizing 1,777 rational functions
through degree 11; 532 genuinely use quadratic-field coefficients and 362 lie
beyond the linear-fractional class.

The exact Lean statements are
`PuckMathsFormal.PolynomialTail.Rational.riccati_of_certificate`,
`degree_equation_of_numeratorIdentity`,
`cConstant_ne_zero`,
`positive_everywhere_of_eventually_positive`, and
`eventually_contracting_unique` in
`formal/PuckMathsFormal/PuckMathsFormal/PolynomialTail/Rational.lean`.  The
certificate algebra is polymorphic over a field, so the formal statement
includes the quadratic coefficient field rather than only `Q`.

## 7. The degree-one and 1-period islands

There is a second, strictly larger decidable branch.  If

\[
 p^2+4r=D^2,\qquad u^2-4rv=d^2,
\]

then at any starting tail index `N` the continued fraction after subtracting
its linear base has coefficients

\[
 \frac{(rj+\gamma_0)(j+\alpha-1)}{pj+\beta_0},
\]

with rational `alpha,beta0,gamma0`.  This is precisely the continued fraction
associated by Pincherle's theorem to a degree-one second-order holonomic
recurrence whose characteristic roots are the two distinct rational roots of
`X^2-pX-r`.

Kenison, Klurman, Lefaucheux, Luca, Moree, Ouaknine, Sertöz, Whiteland, and Worrell
proved in 2026 that minimality is decidable for this class, using effective
relation algorithms for E-functions and 1-periods.  Consequently
`s_N=M` is decidable here even when the tail is not a rational function.
`TryDegreeOneMinimalityReduction` constructs the exact recurrence and the
equivalence-scaled Pincherle data `u_-1=alpha`, `u_0=A_N-M`, so that the
normalized target is `(M-A_N)/alpha`; `VerifyDegreeOneMinimalityReduction` rechecks the shifted
factorization.  The independent verifier covers 21,315 accepted reductions
and 85,260 shifted identities.  The algebraic reduction is formalized by
`shifted_numerator_factorization`, `shifted_base`, and
`rational_characteristic_roots`, `equivalence_scale_step`, and
`pincherle_scaled_initial_ratio` in
`PolynomialTail/MinimalityReduction.lean`.  The local toolkit does not yet
implement the paper's E-function/1-period engines, so it constructs a complete
input to the published decision procedure rather than pretending numerical
quadrature is that procedure.

The rational-characteristic hypothesis is not the end of this reduction. If
the numerator discriminant is square and

\[
 p(u-r)=2rq,
\]

then the transformed Gauss hypergeometric parameters simplify, independently
of the starting index, to

\[
 a=\frac{d+r}{2r},\qquad
 b=N-1+\frac{u+d}{2r},\qquad
 c=N+\frac{u-r}{2r}.
\]

They are rational even when `p^2+4r` is not square. The argument `x` is then
quadratic algebraic rather than rational, but Euler's integrand is still an
algebraic function over algebraic data and hence a 1-period. The effective
1-period relation algorithm therefore still decides the equality.
This branch is exact for the direct Gauss representation, not merely a
convenient sufficient family: if `ell` is the non-dominant characteristic root,

\[
 a=\frac{d+r}{2r}+\frac{p(u-r)-2rq}{2r(\ell-\lambda)}.
\]

For nonsquare `p^2+4r`, the alignment residual is therefore the sole irrational
component of `a`; it vanishes if and only if the Gauss parameters are rational.
`TryOnePeriodEqualityReduction` recognizes this enlarged branch, computes the
least even shift that makes both Euler endpoint exponents positive, and
verifies the transformation exactly. A direct equivalence calculation gives
the prefactor `mu/alpha`: multiplying all partial denominators by `delta` and
all partial numerators by `delta^2` scales the fraction by `delta`. The current
2026 draft prints `mu^2/alpha` in equations (10)--(11); the verifier guards
against that extra power by comparing the corrected hypergeometric quotient to
100,000-level continued-fraction convergents. Its independent box verifier accepts 22,649
instances: 21,315 with rational characteristic roots and 1,334 new aligned
irrational-characteristic cases; 21,640 are not rational-function tails. The
parameter collapse is formalized by `aligned_hypergeometric_parameter` and
`aligned_remaining_parameters`; the exact decomposition is formalized by
`hypergeometric_parameter_decomposition`, while the corrected prefactor is formalized by
`equivalence_prefactor_one_power`, in `PolynomialTail/MinimalityReduction.lean`.

## 8. Integer equality as integer-orbit positivity

There is a useful exact reformulation of the remaining obstruction. Write
`A_n=pn+q`, `B_n=rn^2+un+v`, and suppose `s_1=A_1+d` is an integer candidate.
Set

\[
 P_2=1,\qquad P_3=d,\qquad
 P_{n+2}=B_{n-1}P_n-A_nP_{n+1}.
\]

Then

\[
 s_n=\frac{B_{n-1}P_n}{P_{n+1}}
\]

satisfies the Riccati recurrence wherever the denominators are nonzero. Since
`B_n>0`, the candidate is the unique positive tail exactly when this integer
orbit remains positive forever. The bridge is machine-checked by
`initial_riccati_step`, `riccati_of_cleared_orbit`,
`positive_ratio_of_positive_orbit`, and
`positive_orbit_successor_of_positive_ratio` in
`PolynomialTail/IntegerOrbit.lean`.

This reformulation makes searches much cheaper and identifies the precise new
theorem one would need: positivity is decidable for this restricted integral
degree-(2,1) recurrence, or every positive instance has a hypergeometric
solution. A targeted aligned-irrational search checked 2,261,907 tuples and
2,575,816 integer starts through depth 10,000 and found no survivor at all.
The longest false start did not become non-positive until `P_146`, so a fixed
handful of inequalities would not explain the data. This is evidence for
rigidity, not a replacement for the missing proof.

There is an equivalent differential-equation view. Put `Q_j=P_(j+2)` and let

\[
 F(z)=\sum_{j\geq0}Q_j\frac{z^j}{j!}.
\]

Coefficient extraction from the integer recurrence gives the exact Fuchsian
equation

\[
 (1+pz-rz^2)F''=
 ((3r+u)z-(2p+q))F'+(r+u+v)F.                 \tag{12}
\]

The nonzero local exponent at the nearer finite singularity has the
decomposition

\[
 -\frac{r+u}{2r}+
 \frac{p(u-r)-2rq}{2r\sqrt{p^2+4r}},           \tag{13}
\]

and the discriminant of the indicial polynomial at infinity is

\[
 u^2-4rv.                                      \tag{14}
\]

Consequently the one-period reduction found above is maximal in a structural
sense: its double-square or square-numerator-plus-alignment hypotheses are
exactly the conditions under which all local exponent differences of (12) are
rational. Outside this locus the obstruction is no longer merely a missing
continued-fraction normalization; the associated hypergeometric equation has
an algebraic irrational local exponent, so its Euler solutions are not
ordinary 1-periods. These identities are machine-checked in
`PolynomialTail/GeneratingFunction.lean`.

This also gives a new arithmetic obstruction. For an exact integer equality,
the positive orbit is the minimal factorial-growth solution, so the
coefficients `Q_n/n!` of `F` are exponentially bounded. Let

\[
 E_N=\operatorname{lcm}_{0\leq n\leq N}
      \operatorname{denominator}\!\left(\frac{Q_n}{n!}\right).
\]

If `E_N <= C^N` for some constant `C`, then `F` meets the arithmetic,
growth, and holonomy conditions for a G-function. The
André--Chudnovsky--Katz quasi-unipotent-monodromy theorem then forces every
local exponent occurring in `F` to be rational. Equations (13)--(14) imply the
following dichotomy:

> Any integer equality outside
> `u^2-4rv=square` together with
> (`p^2+4r=square` or `p(u-r)=2rq`) must have superexponential EGF common
> denominators: for every `C>1`, `E_N>C^N` for arbitrarily large `N`.

This does not yet exclude such an orbit, but it replaces an unrestricted
special-function equality by a concrete p-adic denominator-growth obligation.
The self-contained exact search
`tools/polynomial-tail-egf-arithmetic-search.py` tested 1,457,835 integer starts
through depth 500. All 358 survivors had square characteristic discriminant
and rational hypergeometric successive-term ratios; 356 ratios were affine and
the remaining two were degree-(2,1). None lay in the new superexponential
branch. Their largest measured common-denominator size was 493 bits at depth
500, consistent with exponential rather than factorial growth. This is
evidence for the dichotomy's useful side, not a proof that the other side is
empty.

The denominator condition is not automatic for an integral recurrence. For
example, continuing the integral orbit for `(p,q,r,u,v,d)=(1,0,1,0,1,1)` even
after its first sign failure gives a common-denominator size of 3,397 bits by
index 499, with the ratio `log2(E_N)/N` still increasing. In contrast, the
aligned long false start `(1,0,176,176,44,14)` has only 710 bits at the same
index. Thus the arithmetic experiment distinguishes the rational-exponent
locus, but a new theorem would still be needed to show that *positivity* rules
out the superexponential behavior.

The new exact prime-power profiler
`tools/polynomial-tail-padic-sieve.py` computes the exponent

\[
 e_\ell(N)=\max_{0\le n\le N}
   \max(0,v_\ell(n!)-v_\ell(Q_n))
\]

from the recurrence modulo `ell^v_ell(N!)`, without factoring the
factorial-sized orbit values.  It also closes the finite state orbit
`(n mod ell,Q_n mod ell,Q_(n+1) mod ell)`.  If its eventual cycle contains a
nonzero `Q_n mod ell`, such a nonzero residue recurs with bounded gaps.
Legendre's formula then gives the rigorous finite-prime certificate

\[
 \liminf_{N\to\infty}\frac{\log E_N}{N}
 \geq \sum_{\ell\in S}\frac{\log\ell}{\ell-1}. \tag{P}
\]

for every finite set `S` of those primes.  Thus increasing finite residue
searches produce certified lower bounds rather than a fitted growth curve.
The same tool multiplies one complete coefficient period into a two-by-two
matrix over `F_ell`.  That monodromy kills every initial orbit precisely when
its trace and determinant vanish.

The observed criterion in fact admits a direct proof.  If

\[
 T_n=\begin{pmatrix}0&1\\B(n+1)&-A(n+2)\end{pmatrix},\qquad
 M_\ell=T_{\ell-1}\cdots T_0,
\]

then

\[
 \det M_\ell=(-1)^\ell\prod_{x\in\mathbf F_\ell}B(x).
\]

At an odd prime not dividing `2r`, this determinant vanishes exactly when the
numerator discriminant `u^2-4rv` is a square modulo `ell`.  Exclude the primes
dividing the characteristic discriminant as well.  There is a stronger closed
trace formula.  Put

\[
 R=p(u-r)-2rq,qquad \Delta_c=p^2+4r,qquad h=(\ell-1)/2.
\]

Then, for every odd prime `ell` not dividing `r Delta_c`,

\[
 \boxed{\operatorname{tr}M_\ell=
 \frac{R}{2r}\left(1-\Delta_c^h\right)}. \tag{T}
\]

Here is a direct proof on the determinant-zero locus.  When `B` has distinct
roots `a,b`, put
`d=b-a` in `{1,...,ell-1}`.  The singular transfer at `a` has rank one:

\[
 T_{a-1}=c_a e_2^t,
 \qquad c_a=(1,-A(a+1))^t.
\]

Cyclically cutting the product at the two singular transfers therefore gives

\[
 \operatorname{tr}M_\ell=H(a,b)H(b,a), \tag{T0}
\]

where `H(a,b)` is the second coordinate obtained by propagating `c_a` from
`a` to `b`.  This connection coefficient also has an elementary determinant
form.  Write `q_a=pa+q`, let

\[
 C=\begin{pmatrix}0&r\\1&p\end{pmatrix},
\]

and let `rho_(d-1)` be its induced action on `Sym^(d-1)`.  The continuant
recurrence for the connection is exactly the leading-principal-minor
recurrence of

\[
 L_d=(p+q_a)I+\rho_{d-1}(C).
\]

Hence `H(a,b)=(-1)^d det(L_d)`.  If `lambda_-,lambda_+` are the roots of
`x^2-px-r`, then

\[
 H(a,b)=(-1)^d\prod_{j=0}^{d-1}
 \left(p+q_a+(d-1-j)\lambda_-+j\lambda_+\right). \tag{T1}
\]

The reverse connection has length `ell-d` and `q_b=q_a+pd`.  If
`delta=lambda_+-lambda_-` and
`c=q_a+d lambda_-+lambda_+`, the two products in (T1) combine to give

\[
 \operatorname{tr}M_\ell
 =-\prod_{j\in\mathbf F_\ell}(c+j\delta)
 =c\delta^{\ell-1}-c^\ell. \tag{T2}
\]

If `Delta_c` splits, `c,delta` lie in `F_ell`, so (T2) is zero.  If it is
nonsplit, Frobenius interchanges `lambda_-` and `lambda_+`; hence
`delta^(ell-1)=-1`, and (T2) reduces to

\[
 -(c+c^\ell)
 =-\bigl(2q+p(a+b+1)\bigr)
 =R/r.
\]

Euler's criterion now gives (T) whenever `B` splits.  To remove that apparently
extraneous assumption, hold `p,q,r,u` fixed and view the cyclic continuant
`tr M_ell` as a polynomial in `v`.  A matching on the odd `ell`-cycle uses at
most `h` quadratic entries.  The coefficient of `v^h` is the sum of the one
remaining linear entry over all cyclic positions, hence is zero in `F_ell`.
The degree is therefore less than `h`.  As `v` ranges, the condition that
`u^2-4rv` be a nonzero square supplies exactly `h` distinct values, on all of
which (T) was just proved.  Polynomial interpolation proves (T) for every
`v`.

In particular, after also excluding the numerator discriminant, determinant
and trace together give the exact operator-level criterion

\[
 \operatorname{tr}M_\ell=0
 \quad\Longleftrightarrow\quad
 p(u-r)-2rq=0\pmod\ell
 \quad\text{or}\quad
 p^2+4r\text{ is a square modulo }\ell. \tag{T3}
\]

At depth 500, primes through 31 already certify base-two rates `3.144300` for
the long outside-locus escape `(1,0,8,11,3,3)` and `3.776906` for the generic
outside-locus orbit `(1,0,1,0,1,1)`.  The factorial, rational-ratio, and aligned
controls have no bad prime through 31.  An independent local-exponent sweep of
45,762 unramified parameter/prime instances found exact agreement between
nilpotence of this monodromy and the finite-field analogue of the EGF locus:
the numerator discriminant splits, and either the characteristic discriminant
splits or the alignment residual vanishes.  The sweep now independently
checks the determinant formula, connection factorization, and closed trace
formula used in the proof.  A second exhaustive sweep checks all 17,068
coefficient tuples modulo 3, 5, and 7, including nonsplit and ramified
numerators, with zero trace-formula mismatches.  There were 737 instances in the reverse
initial-value test where a nonsplit, nonnilpotent full operator nevertheless
had one special initial orbit die modulo that particular prime.  Consequently
the operator-level modular pattern agrees perfectly with the EGF geometry,
while particular-solution accidents still prevent it from replacing the
minimal-solution analysis.

Those accidents nevertheless have an exact description.  Cayley--Hamilton
gives `M_ell^2=(tr M_ell)M_ell` whenever its determinant is zero.  If the trace
also vanishes, every state dies within two coefficient periods.  If the trace
is nonzero, a particular state dies eventually if and only if it already lies
in `ker M_ell`, in which case it dies after one period.  For the cleared tail
the test is simply

\[
 M_\ell(1,d)^t=0\pmod\ell. \tag{K}
\]

The sweep checks (K) against every one of the 737 exceptional cycles with zero
mismatches.  Thus the remaining arithmetic question is whether the positive
minimal initial state can satisfy (K) for enough primes without coming from a
certified rational or hypergeometric tail.

There is no such particular-state exception when the numerator discriminant
is nonsquare over `Q`.  Let `Delta_B=u^2-4rv` be a nonzero nonsquare integer.
For every unramified prime inert in `Q(sqrt(Delta_B))`, the determinant product
above is nonzero, so `M_ell` is invertible.  Since the initial state `(1,d)` is
nonzero modulo every prime, its residue orbit cannot become zero.  Every such
prime is therefore a bad prime in (P), independently of `d`.  Inert primes
have positive density, and the corresponding Mertens sum diverges:

\[
 \sum_{\substack{\ell\le x\\(\Delta_B/\ell)=-1}}
   \frac{\log\ell}{\ell-1}\longrightarrow\infty.
\]

Applying (P) to successively larger finite subsets proves the unconditional
superexponential-denominator theorem

\[
 \boxed{\Delta_B\notin\mathbf Q^{\,2}
 \quad\Longrightarrow\quad
 \lim_{N\to\infty}\frac{\log E_N}{N}=+\infty} . \tag{SE}
\]

This holds for every initial increment, without positivity.  It completely
settles the denominator-growth question off the square-numerator locus.  The
only outside-rational-exponent branch on which kernel accidents can matter is
therefore

\[
 \Delta_B\in\mathbf Q^{\,2},\qquad
 \Delta_c\notin\mathbf Q^{\,2},\qquad R\ne0. \tag{E}
\]

The determinant and state-dynamics parts of this argument are now
machine-checked in
`formal/PuckMathsFormal/PuckMathsFormal/PolynomialTail/FiniteFieldMonodromy.lean`:
`det_monodromy_eq_zero_iff_discriminant_square` proves the discriminant locus,
and `two_periods_kill_of_trace_det_zero` proves the two-period nilpotence
claim.  The file contains no `sorry`; it also checks the trace formula
exhaustively over `F_3`.  The uniform cyclic-continuant evaluation (T), though
proved above and independently verified by the exact sweeps, remains the sole
part of this modular theorem not yet internalized in Lean.

The 2025 global-boundedness algorithm for degree-one second-order recurrences
does apply near the gauged recurrence used in the one-period reduction, but it
does not decide (E).  It classifies algebraic or globally bounded solution
lines under additional hypotheses.  Exact tail equality instead selects the
minimal analytic solution, whose gauged coefficients need not be globally
bounded; the paper explicitly leaves the general nonzero exponential-factor
case open.  Thus this recent result validates the reduction but does not turn
minimality into an equality oracle.

This is the recurrence-side form of *factorial reduction*.  Recent
continued-fraction searches report it as a rare lower-dimensional phenomenon
and explicitly use it as a conjectural discovery heuristic.  Here the EGF
calculation explains why its occurrence is controlled by rational local
exponents.  What is still missing is the converse implication specific to the
positive minimal orbit: positivity would have to force factorial reduction,
or otherwise rule out the infinitely accumulating bad-prime certificates.

This is exactly the exceptional case left by the latest general sign theory.
Hagihara and Kawamura classify every possible ultimate sign of a second-order
holonomic sequence and give a partial algorithm that halts on almost every
initial value, but explicitly reduce the remaining unstable line to the
Minimality Problem, whose decidability they state is unknown. An exact
integer-tail equality puts the cleared orbit on that minimal line. Therefore
neither their sign classification nor the new EGF reformulation supplies the
missing equality oracle; they identify the same obstruction from complementary
directions.

The broader equality obstruction remains: this family does not prove that every integer hit is a rational Riccati
solution of classifiable form. Positive degree-(2,1) continued fractions are hypergeometric ratios, and recent
Bauer--Muir work transforms this structural family into evaluations involving gamma quotients, `pi`, and `log 2`.

The strongest realized statement is consequently:

> For every admissible parameter tuple, eventual identity is decidable. In the irrational-slope case the discrepancy
> has an effectively constructible Ostrowski DFAO beyond an explicit cutoff and its sign on every channel is decidable
> at order at most two; in the rational-slope case it has an eventual radix-2 DFAO. Exact-affine and
> rational-function tails have a complete finite certificate scheme (with the dense executable recognizer bounded at
> denominator degree 128), double-square instances reduce to an
> unconditional published minimality decision procedure, and the aligned square-numerator branch reduces to
> effective 1-period equality.  In addition, the full integer family
> `(p,q,r,u,v)=(k,q,1,0,0)` with `k>=1` and `-k<=q<=0` has unconditional
> all-index Beatty equality by a machine-checked generalized trapping theorem.
> The complementary scaled family `(p,q,r,u,v)=(p,-p,r,0,0)` with
> `1<=r<=p` likewise has unconditional all-index equality, despite lying in
> the nonsquare-characteristic, nonaligned exceptional stratum.
> Whenever every other finite-prefix comparison
> avoids an unresolved exact integer hit, these machines extend effectively to a total DFAO and decide all-index
> identity.

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
dotnet run tools/polynomial-tail-linear-fractional-verifier.cs
dotnet run tools/polynomial-tail-rational-verifier.cs
dotnet run tools/polynomial-tail-rational-box-verifier.cs -- 12 24
dotnet run tools/polynomial-tail-minimality-reduction-verifier.cs
dotnet run tools/polynomial-tail-one-period-reduction-verifier.cs
dotnet run tools/polynomial-exact-beatty-trap-verifier.cs -c Release --no-restore
dotnet run tools/polynomial-tail-aligned-period-orbit-search.cs -- 200 300 600 10000
python tools/polynomial-tail-egf-arithmetic-search.py 8 20 500
python tools/polynomial-tail-padic-sieve.py 500 --cycle-prime-bound 31 --verify --sweep
dotnet run tools/polynomial-tail-integer-counterexample-verifier.cs
cd formal/PuckMathsFormal && lake build PuckMathsFormal.PolynomialTail
```

## References

- D. Bowman and J. McLaughlin, [Polynomial Continued Fractions](https://arxiv.org/abs/1812.08251).
- N. Ben David et al., [On the Connection Between Irrationality Measures and Polynomial Continued Fractions](https://arxiv.org/abs/2111.04468).
- P. Hieronymi and A. Terry Jr., [Ostrowski Numeration Systems, Addition and Finite Automata](https://arxiv.org/abs/1407.7000).
- L. Schaeffer, J. Shallit, and S. Zorcic, [Beatty Sequences for a Quadratic Irrational: Decidability and Applications](https://arxiv.org/abs/2402.08331).
- K.-W. Chen and C.-H. Liu, [Continued Fractions with Quadratic Numerators via the Bauer--Muir Transform](https://doi.org/10.3390/math13152332).
- G. Kenison et al., [On Positivity and Minimality for Second-Order Holonomic Sequences](https://doi.org/10.4230/LIPIcs.MFCS.2021.67).
- G. Kenison et al., [On the Positivity Problem for Second-Order Holonomic Sequences](https://georgekenison.github.io/uploads/papers/holonomic_positivity26.pdf), 2026.
- E. C. Sertöz, J. Ouaknine, and J. Worrell, [Computing transcendence and linear relations of 1-periods](https://arxiv.org/abs/2505.20397), 2025.
- S. Garoufalidis, [G-functions and multisum versus holonomic sequences](https://arxiv.org/abs/0708.4354), especially the G-function denominator criterion and rational local-exponent theorem.
- Y. André, [Arithmetic Gevrey series and transcendence: a survey](https://www.numdam.org/item/JTNB_2003__15_1_1_0/), for the arithmetic-Gevrey denominator condition and Fourier--Laplace duality.
- R. Elimelech et al., [Algorithm-assisted discovery of an intrinsic order among mathematical constants](https://arxiv.org/abs/2308.11829), for factorial reduction in polynomial continued fractions and its status as a conjectural search principle.
- A. Matveeva, [On the integrality of some P-recursive sequences](https://arxiv.org/abs/2511.02121), for the recent global-boundedness algorithm for restricted degree-one second-order recurrences and its remaining general case.
- F. Hagihara and A. Kawamura, [The Ultimate Signs of Second-Order Holonomic Sequences](https://doi.org/10.4230/LIPIcs.ICALP.2025.159), 2025; the exceptional unstable line reduces to the open Minimality Problem.
