# Euler-moment exclusion for integer polynomial tails

## Status

This note proves a new exact, finite exclusion test for integer hits in the
square-numerator branch of the degree-`(2,1)` polynomial-tail problem.  It does
not decide every remaining hypergeometric equality.  It does decide cases with
genuinely quadratic characteristic roots and a genuinely quadratic Gauss
parameter, so it is strictly outside the rational-function and 1-period
recognizers.

The implementation is
`PolynomialTailEulerMomentExclusionCertificate` in
`src/Puck.Maths/Research/PolynomialTailEulerMoment.cs`.  It tries both rational
factor orientations of the quadratic numerator and uses only exact
`QuadraticSurd` comparisons.

## Reduction

Suppose that, after shifting to the requested tail index $N$, the quadratic
numerator has the factorization

\[
 B_{N+j-1}=(rj+\gamma)(j+\alpha-1).
\]

If the numerator discriminant is a rational square, both orientations of its
two roots give rational $\alpha,\gamma$.  Let $\lambda>0$ be the dominant
characteristic root, let $\ell=p-\lambda<0$, and put

\[
 x=\frac{\ell}{\lambda}\in(-1,0),
 \qquad
 a=\frac{\ell^2\alpha-\ell(pN+q)-\gamma}
          {\ell(\ell-\lambda)},
 \qquad
 b=\alpha-1,
 \qquad
 c=a+\frac\gamma r.
\]

The polynomial-continued-fraction reduction used by
`PolynomialTailOnePeriodReduction` is algebraic and remains valid when $a$
is quadratic rather than rational.  A putative integer equality $s_N=M$
therefore forces

\[
 c\,{}_2F_1(a,b;c;x)
   =T\,{}_2F_1(a,b+1;c+1;x),
 \qquad T=\frac M\lambda .                                      \tag{1}
\]

The simplification $T=M/\lambda$ includes the continued-fraction
equivalence prefactor; it is the same one-power normalization already checked
in `PolynomialTail/MinimalityReduction.lean`.

## The two orientations and quadratic conjugation

The two rational factorizations are not two independent special-value
problems.  Put

\[
 D=\sqrt{p^2+4r},\qquad
 R=p(u-r)-2rq,\qquad
 \rho=\frac{R}{2rD},
\]

and let $d=\sqrt{u^2-4rv}$.  For the orientation
$\sigma\in\{+1,-1\}$, the parameters above simplify to

\[
 \begin{aligned}
 a_\sigma&=\frac{r+\sigma d}{2r}-\rho,\\
 b_\sigma&=N-1+\frac{u+\sigma d}{2r},\\
 c&=N+\frac{u-r}{2r}-\rho,\\
 x&=\frac{p-D}{p+D}.
 \end{aligned}                                                \tag{O1}
\]

They obey the exact cross-orientation identities

\[
 c-a_\sigma=b_{-\sigma},\qquad
 c-b_\sigma=a_{-\sigma},\qquad
 c-a_\sigma-b_\sigma=-\frac{\sigma d}{r}.                    \tag{O2}
\]

Euler's transformation therefore gives

\[
 {}_2F_1(a_\sigma,b_\sigma;c;x)
 =(1-x)^{-\sigma d/r}
 {}_2F_1(a_{-\sigma},b_{-\sigma};c;x).                        \tag{O3}
\]

Thus trying both orientations is useful for finding a positive Euler chart,
but it does not create two arithmetically independent tests.

There is also an exact Galois--Kummer symmetry.  Let $\tau$ be the nontrivial
automorphism of $\mathbf Q(D)$.  Then

\[
 x^\tau=\frac1x,\qquad
 a_\sigma^\tau=1-c+b_\sigma,\qquad
 c^\tau=1-a_\sigma+b_\sigma.                                \tag{O4}
\]

These are precisely the parameter changes induced on Gauss's differential
equation by inversion $z\mapsto1/z$ and the gauge
$y(1/z)\mapsto z^{-b_\sigma}y(1/z)$.  In other words, quadratic conjugation
does not produce an unrelated hypergeometric equation: it exchanges the
$0$ and $\infty$ singularities through a Kummer transformation.

This symmetry is structural, but it is not by itself a proof of
non-equality.  A field automorphism of $\overline{\mathbf Q}$ cannot be passed
through an infinite complex hypergeometric sum or its analytic continuation.
Consequently (O4) does **not** imply that the conjugate of a special value is
the corresponding analytically continued value at $1/x$.  The elementary
sequence $\alpha^n$ with $\alpha=\sqrt2-1$ illustrates the topological gap:
$\alpha^n\to0$ in the chosen real embedding, while
$(\alpha^\tau)^n=(-\sqrt2-1)^n$ diverges.

## Equality as an algebraic connection coordinate

There is a sharper equivalent form of (1).  Write

\[
 F(z)={}_2F_1(a,b;c;z),\qquad
 G(z)={}_2F_1(a,b+1;c+1;z).
\]

Euler integration by parts, followed by analytic continuation, gives the
contiguous identity

\[
 \boxed{
 \frac bcG(z)=
 \frac{bF(z)+(z-1)F'(z)}{c-a}
 }                                                            \tag{C1}
\]

whenever the displayed expressions are nonresonant.  Hence (1) is equivalent
to

\[
 \boxed{
 \frac{F'(x)}{F(x)}=
 \frac{b\bigl((c-a)/T-1\bigr)}{x-1}
 \in\mathbf Q(D),
 \qquad T=\frac M\lambda.
 }                                                            \tag{C2}
\]

Thus the residual integer-tail question is exactly an algebraicity question
for the projective connection coordinate, or logarithmic derivative, of the
principal Gauss solution at the algebraic point $x$.

This is a pointwise condition, not a claim that $F'/F$ is algebraic as a
function of $z$.  Differential-Galois and hypergeometric-factorization
algorithms classify the latter, global Riccati phenomenon; in the PCF model it
is the rational-function-tail branch already handled by the exact
recognizers.  They do not imply that an algebraic value of $F'/F$ at one
ordinary algebraic point extends to a global algebraic solution.

### The special-value obstruction

Outside the aligned locus $R=0$, the local exponent contains the nonzero
quadratic irrational $\rho$.  Gelfond--Schneider shows that the associated
local-monodromy multiplier $\exp(2\pi i\rho)$ is transcendental.  That fact
does not currently imply that the single connection coordinate (C2) is
nonalgebraic: monodromy matrices and connection constants may themselves have
transcendental entries.

The available linear-independence theorems for contiguous hypergeometric
$G$-function values require rational hypergeometric parameters; see
[Lai, *Generalized hypergeometric G-functions take linear independent
values*](https://arxiv.org/abs/2203.00207).  The July 2026 revision of
[David--Hirata-Kohno--Kawashima, *Linear independence of values of
hypergeometric functions and arithmetic Gevrey
series*](https://arxiv.org/abs/2511.06534) greatly extends the Padé machinery
across the regimes $p\lesseqgtr q+1$, but its archimedean $p=q+1$ estimates
still assume rational hypergeometric parameters.  Likewise the July 2026
exceptional-set result for Gauss values explicitly begins with
$a,b,c\in\mathbf Q$; see
[Bhattacharjee, *Exceptional Sets for Certain ${}_2F_1$ Hypergeometric
Functions*](https://arxiv.org/abs/2607.16331).  Here $a,c\notin\mathbf Q$
precisely when $R\ne0$ and $D\notin\mathbf Q$.  Therefore neither result
supplies the missing implication in (C2).

The tempting conjugation proof would replace the right side of (C2) by its
quadratic conjugate and identify it with the analytic quotient at $1/x$.
Equation (O4) explains why that idea looks natural, but the forbidden passage
of $\tau$ through analytic continuation is exactly the gap.  Closing it would
amount to a new special-value theorem for Gauss functions with algebraic
irrational parameters, not a further algebraic manipulation of the PCF
reduction.

## Positive-moment interval

Assume

\[
 b>0,\qquad c-b>0.                                               \tag{2}
\]

Because $x\in(-1,0)$, the Euler weight

\[
 w(t)=t^{b-1}(1-t)^{c-b-1}(1-xt)^{-a},\qquad 0<t<1,
\]

is positive and integrable.  Normalize it to a probability measure and write
$\mathbb E$ for expectation.  Euler's integral gives

\[
 \frac{{}_2F_1(a,b+1;c+1;x)}{{}_2F_1(a,b;c;x)}
   =\frac cb\,\mathbb E[t].
\]

Thus (1) is equivalent to

\[
 T=\frac b{\mathbb E[t]}.                                       \tag{3}
\]

Now integrate the derivative of

\[
 t^b(1-t)^{c-b}(1-xt)^{-a}.
\]

The endpoint terms vanish by (2), and division by the Euler normalizing
integral gives

\[
 0=b-c\mathbb E[t]
   +ax\,\mathbb E\!\left[\frac{t(1-t)}{1-xt}\right].
\]

After dividing by $\mathbb E[t]$, equations (3) and the last identity yield

\[
 T=c-ax\,
 \mathbb E_t\!\left[\frac{1-t}{1-xt}\right],                    \tag{4}
\]

where $\mathbb E_t$ denotes expectation under the size-biased probability
measure with density proportional to $t w(t)$.  On $0<t<1$ and
$-1<x<0$,

\[
 0<\frac{1-t}{1-xt}<1.
\]

Consequently, if $a\ne0$, every integer equality must satisfy the strict
exact interval

\[
 \boxed{
   \min(c,c-ax)<\frac M\lambda<\max(c,c-ax)
 }.                                                              \tag{5}
\]

Equation (3) also gives $T>b$, since $0<t<1$.  Thus when $a<0$ the
lower endpoint in (5) can be strengthened to $\max(b,c-ax)$.  If $a=0$,
the target must instead satisfy $M/\lambda=c$ (and still $T>b$).  Failure of
any of these strict inequalities, including equality with an endpoint, is
therefore a finite certificate that $s_N\ne M$.

## Exact Hausdorff hierarchy

The interval is only the first member of a systematic exact hierarchy.  Let

\[
 m_k=\mathbb E[t^k],\qquad m_0=1,\qquad m_1=\frac bT.
\]

Integrating the derivative of
$t^{b+k}(1-t)^{c-b}(1-xt)^{-a}$ gives the recurrence

\[
 (b+k)m_k-
 \bigl(c+k+x(b+k+1-a)\bigr)m_{k+1}
 +x(c+k+1-a)m_{k+2}=0.                         \tag{6}
\]

Consequently a proposed integer equality forces every $m_k$ as an exact
element of $\mathbf Q(\sqrt{p^2+4r})$.  Because the Euler measure has positive
density on $(0,1)$, a true equality must satisfy every strict Hausdorff
inequality

\[
 \boxed{
   \sum_{i=0}^j(-1)^i\binom ji m_{k+i}
   =\mathbb E[t^k(1-t)^j]>0
 }
 \qquad(k,j\geq0).                              \tag{7}
\]

`TryEulerHausdorffIntegerExclusionCertificate` generates (6) through a
requested total order and returns the first nonpositive value in (7).
`VerifyEulerHausdorffIntegerExclusionCertificate` reconstructs both the
moments and the failed inequality.  This is a nested, tolerance-free
exclusion hierarchy.

## Exact contiguous regularization

The native assumptions $b>0$ and $c-b>0$ are not essential.  For integers
$k,j\geq0$, put

\[
 W_{k,j}=\sum_{i=0}^j(-1)^i\binom ji m_{k+i}.
\]

The elementary contiguous identity

\[
 \boxed{
 W_{k,j}=
 \frac{(b)_k(c-b)_j}{(c)_{k+j}}
 \frac{{}_2F_1(a,b+k;c+k+j;x)}
      {{}_2F_1(a,b;c;x)}
 }                                                        \tag{8}
\]

moves the two endpoint exponents independently.  One proof starts with

\[
 m_k=\frac{(b)_k}{(c)_k}
 \frac{{}_2F_1(a,b+k;c+k;x)}{{}_2F_1(a,b;c;x)}
\]

and applies the forward-difference identity once; iteration gives (8).
Equivalently, expand each Gauss series and use

\[
 \sum_{i=0}^j(-1)^i\binom ji
 \frac{(b)_{k+i}(b+k+i)_n}
      {(c)_{k+i}(c+k+i)_n}
 =\frac{(b)_k(c-b)_j}{(c)_{k+j}}
  \frac{(b+k)_n}{(c+k+j)_n}.
\]

Choose the least nonnegative shifts

\[
 K=\max(0,\lfloor-b\rfloor+1),\qquad
 J=\max(0,\lfloor b-c\rfloor+1).                            \tag{9}
\]

Then $b+K>0$ and $c-b+J>0$.  Provided

\[
 (b)_K(c-b)_J(c)_{K+J}\ne0,                                 \tag{10}
\]

$W_{K,J}$ is a nonzero common factor times a strictly positive Euler
integral.  Dividing every later contiguous value by this anchor gives

\[
 \frac{W_{K+i,J+l}}{W_{K,J}}
 =\mathbb E_{K,J}[t^i(1-t)^l]>0
 \qquad(i,l\geq0),                                          \tag{11}
\]

where the probability density is proportional to

\[
 t^{b+K-1}(1-t)^{c-b+J-1}(1-xt)^{-a}.
\]

This proves the sign-normalized test

\[
 \boxed{W_{K,J}W_{K+i,J+l}>0}.                              \tag{12}
\]

It is important that (12), rather than $W_{K+i,J+l}>0$, is tested: analytic
continuation from a non-native chart can give the common prefactor either
sign.  A zero anchor also excludes equality when (10) holds.

`TryEulerMomentRegularization` constructs and verifies the canonical shifts
(9) with unbounded `BigInteger` indices.  It rejects precisely the zero
Pochhammer resonance rather than dividing by it.
`TryEulerRegularizedHausdorffIntegerExclusionCertificate` implements (12),
subject only to the implementation's dense-moment cap of total order 256.

### Singular cases and coverage of the nonaligned branch

In the square-numerator, nonsquare-characteristic, nonaligned branch, write
$D=\sqrt{p^2+4r}$ and $R=p(u-r)-2rq\ne0$.  The two factor orientations have

\[
 b_\sigma=N-1+\frac{u+\sigma d}{2r},\qquad
 c-b_\sigma=\frac12-\frac{\sigma d}{2r}-\frac{R}{2rD}.
\]

Thus $c-b_\sigma$ and $c$ are irrational, so neither corresponding
Pochhammer factor in (10) can vanish.  Moreover

\[
 B_{N+j-1}=r(j+b_+)(j+b_-).
\]

Positivity of $B_n$ for $n\geq1$ rules out $b_\sigma=-m$ for every integer
$m\geq1$: taking $j=m$ would give a positive-index zero of $B$.  Hence an
orientation is nonregularizable only when $b_\sigma=0$.  If just one
orientation has $b=0$, the other orientation is nonresonant.  If both do,
necessarily

\[
 N=1,\qquad B_n=rn^2.                                       \tag{13}
\]

The Riccati equation removes this last resonance.  Write a proposed boundary
as

\[
 M=A_1+d.
\]

Since $s_1=A_1+B_1/s_2$ and $B_1=r$, equality is impossible immediately when
$d\leq0$.  When $d>0$ it is exactly equivalent to the rational boundary

\[
 \boxed{s_2=\frac rd}.                                       \tag{14}
\]

At $N=2$ both repeated factors have $b=1$, so (8) is a nonresonant positive
quotient chart.  The moment construction only needs the target
$T=(r/d)/\lambda$; integrality of the shifted boundary was never used.
`TryEulerMomentRegularization` therefore records both the original integer
question and this equivalent rational Gauss boundary, and the regularized
Hausdorff certificate verifies the Riccati shift before using it.

Consequently every viable nonaligned square-numerator integer candidate has a
nonresonant positive quotient chart.  There is no remaining direct-value
exception.

There is no hidden singularity in recurrence (6) in the nonresonant case.
Its division coefficient is

\[
 x(c+k+1-a)=x\left(\frac\gamma r+k+1\right).
\]

Here $x\ne0$, while a zero of the parenthesis would make the factor
$r(k+1)+\gamma$ vanish and hence give $B_{N+k}=0$.  Positivity of the original
numerator excludes that at every $k\geq0$.

### Termination on every false seed

The unbounded regularized hierarchy is not merely heuristic: it terminates on
every false target in the nonaligned square-numerator branch.

Indeed, after division by the coefficient of $m_{k+2}$, the limiting
characteristic equation of (6) is

\[
 1-(1+x)\rho+x\rho^2=0,
\]

with roots $1$ and $1/x$.  Since $-1<x<0$, these roots have distinct moduli
and $|1/x|>1$.  Poincare--Perron theory therefore gives a unique recessive
solution line with successive ratio tending to $1$; every solution off that
line has successive ratio tending to $1/x< -1$.

The genuine Gauss moments lie on the recessive line.  This follows directly
from the positive shifted Euler chart: for fixed regularizing $J$ its moments
are beta-type integrals concentrated near $t=1$, hence have only polynomial
growth or decay and successive ratio tending to $1$.  Normalize this genuine
solution by $m_0^*=1$.

For a proposed target $T$, recurrence (6) instead starts from

\[
 m_0=1,\qquad m_1=b/T.
\]

In a nonresonant chart $b\ne0$, so a false target gives a nonzero difference
$y=m-m^*$.  It has $y_0=0$.  It cannot be a nonzero member of the one-dimensional
recessive line, because every such member is a scalar multiple of $m^*$ and
$m_0^*=1$.  Hence

\[
 \frac{y_{k+1}}{y_k}\longrightarrow\frac1x<-1.              \tag{15}
\]

Taking any fixed finite difference preserves this dominant component:
$\Delta^J y_k$ acquires the nonzero multiplier $(1-1/x)^J$.  Consequently
$W_{k,J}$ eventually alternates sign and grows exponentially, while the
genuine positive-chart contribution is recessive.  Relative to the fixed
nonzero anchor $W_{K,J}$, one of the two parities must eventually violate
(12).  If the anchor itself is zero, it is already an exclusion.

The Riccati shift (14) makes $b=1$ in the former double-zero case, so the same
argument covers it as well.  Thus:

\[
 \boxed{T\ne T^*\quad\Longleftrightarrow\quad
 \text{some finite regularized Hausdorff witness excludes }T.} \tag{16}
\]

This is a mathematical semidecision theorem.  The current API searches only
through its requested order and enforces a dense-array safety cap of 256; it
does not assert that 256 is a uniform bound.

The exact regression `(p,q,r,u,v,M)=(2,-1,6,23,7,5)` survives through total
order 10 but fails first at
$\mathbb E[t^5(1-t)^7]$ in the reversed factor orientation, at total order
12.  This is useful evidence that a fixed collection of the lowest moment
inequalities is not the whole theorem.

## Scope and verification

The certificate is useful precisely where the former 1-period recognizer can
stop: $a$ may be a non-rational element of the real characteristic field.
Trying both numerator-factor orientations is material because (2) may hold, or
the target may be excluded, for only one orientation.

The standalone verifier
`tools/polynomial-tail-euler-moment-verifier.cs` performs a coefficient-box
sweep, independently follows every excluded cleared orbit through 2,000
indices, requires coverage from the reversed factor orientation, exercises
the native and regularized Hausdorff hierarchies through order 20, includes a
non-native one-shift regression and the double-zero resonance, and rejects
corrupted certificates.  Its command is:

```text
dotnet run tools/polynomial-tail-euler-moment-verifier.cs -c Release
```

That command has deliberately not been run while the unrelated repository
build break is owned by another task.  A separate read-only arithmetic
reproduction over `p,r<=8` and coefficient radius `12` examined 24,023
nonaligned square-numerator integer candidates.  The interval excluded 6,440,
including 3,819 that required the reversed factor orientation; every excluded
orbit became nonpositive before depth 2,000.  Among the 12,495 candidates
admitting a native positive Euler chart, the exact order-20 hierarchy excluded
all 12,495, and every corresponding orbit became nonpositive before depth
2,000.  This finite-box result is evidence, not a uniform order bound.

## What remains

After the immediate check $M>A_N$, every candidate in this branch enters the
regularized Hausdorff hierarchy, which is a complete exact semidecision
procedure for non-equality.  A true equality survives every finite level.
What is not known is how to recognize in finite time that the algebraic seed
$b/T$ lies on the unique completely monotone, minimal moment line, or how to
prove that this cannot occur outside the rational-function and 1-period loci.
That stable-line membership theorem is the central minimality problem in
Hausdorff-moment form.
