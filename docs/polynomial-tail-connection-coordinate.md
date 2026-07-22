# The residual hypergeometric connection coordinate

## Scope

This note concerns the residual square-numerator branch of a degree-`(2,1)`
polynomial continued fraction.  Write

\[
 D=\sqrt{p^2+4r},\qquad
 x=\frac{p-D}{p+D}\in(-1,0),\qquad
 R=p(u-r)-2rq,
\]

and assume

\[
 p,q,r,u,v,N\in\mathbf Z,\quad p,r,N\ge1,\qquad
 u^2-4rv=d^2,\quad d\in\mathbf Z_{\ge0},\qquad
 D\notin\mathbf Q,\quad R\ne0.
\tag{P1}
\]

As in the polynomial-tail APIs, the numerator polynomial is also assumed
strictly positive at every positive integer.  These hypotheses imply
\(\lambda=(p+D)/2>0\), \(\ell=(p-D)/2<0\), and hence \(-1<x<0\).

For a factor orientation \(\sigma\in\{+1,-1\}\) and a tail beginning at
\(N\), the Gauss parameters are

\[
 \begin{aligned}
 a&=\frac{r+\sigma d}{2r}-\rho,&
 b&=N-1+\frac{u+\sigma d}{2r},\\
 c&=N+\frac{u-r}{2r}-\rho,&
 \rho&=\frac{R}{2rD}.
 \end{aligned}
\tag{P2}
\]

The aim here is twofold.  First, we make the Galois--Kummer symmetry into an
exact connection and Wronskian formula.  Second, we apply Kimura's theorem to
show that this branch has no hidden irreducible Liouvillian, dihedral, or
finite-monodromy family.  Neither statement proves the desired special-value
nonalgebraicity; the Wronskian calculation identifies exactly why the tempting
quadratic-conjugation proof does not do so.

The algebraic PCF-to-Gauss reduction is implemented in
[`PolynomialTailMinimalityReduction.cs`](../src/Puck.Maths/Research/PolynomialTailMinimalityReduction.cs),
and its parameter decomposition and equivalence prefactor are independently
checked in
[`PolynomialTail/MinimalityReduction.lean`](../formal/PuckMathsFormal/PuckMathsFormal/PolynomialTail/MinimalityReduction.lean).
The exact finite exclusion hierarchy built from the same Gauss quotient is
described in
[polynomial-tail-euler-moment-exclusion.md](polynomial-tail-euler-moment-exclusion.md).
Complementary arithmetic constraints on any surviving equality, including the
quadratic descent on the symmetric slice, are collected in
[polynomial-tail-exceptional-equality-constraints.md](polynomial-tail-exceptional-equality-constraints.md).

## Exact PCF reduction and orientation equivalence

At tail index \(N\), the two orientations of the rational factorization are

\[
 B_{N+j-1}=(rj+\gamma_\sigma)(j+\alpha_\sigma-1),
\]

where

\[
 \alpha_\sigma=N+\frac{u+\sigma d}{2r},\qquad
 \gamma_\sigma=r(N-1)+\frac{u-\sigma d}{2}.
\tag{P3}
\]

Let \(\lambda=(p+D)/2\) and \(\ell=(p-D)/2\).  The degree-one recurrence
transformation gives

\[
 x=\frac\ell\lambda,qquad b_\sigma=\alpha_\sigma-1,
\]

and

\[
 a_\sigma=
 \frac{\ell^2\alpha_\sigma-\ell(pN+q)-\gamma_\sigma}
      {\ell(\ell-\lambda)},qquad
 c_\sigma=a_\sigma+\frac{\gamma_\sigma}{r}.                \tag{P4}
\]

Substitution of \(\lambda+\ell=p\), \(\lambda\ell=-r\), and
\(\ell-\lambda=-D\) reduces (P4) exactly to (P2); in particular \(c_\sigma=c\)
is independent of the orientation.  For a rational boundary \(M\), equality
of the original tail with \(M\) is equivalent to

\[
 \boxed{
 c\,{}_2F_1(a_\sigma,b_\sigma;c;x)
 =\frac M\lambda\,{}_2F_1(a_\sigma,b_\sigma+1;c+1;x).
 }
\tag{P5}
\]

The factor \(M/\lambda\), rather than \(M/\lambda^2\), is the one-power
equivalence prefactor formalized in the Lean file cited above.

The cross-orientation relations are

\[
 c-a_\sigma=b_{-\sigma},\qquad
 c-b_\sigma=a_{-\sigma},\qquad
 c-a_\sigma-b_\sigma=-\frac{\sigma d}{r}.                  \tag{P6}
\]

Euler's transformation gives

\[
 {}_2F_1(a_\sigma,b_\sigma;c;x)
 =(1-x)^{-\sigma d/r}
 {}_2F_1(a_{-\sigma},b_{-\sigma};c;x).                    \tag{P7}
\]

Applying the same transformation to the contiguous function in (P5) produces
the same factor \((1-x)^{-\sigma d/r}\).  Therefore the quotient in (P5), and
hence the equality question, is exactly independent of the chosen numerator
orientation.  Trying both orientations in the moment code changes the
available positive Euler chart, not the analytic connection coordinate.

## The conjugate principal solution is an infinity solution

Let \(\tau\) be the nontrivial automorphism of \(\mathbf Q(D)\).  Directly
from (P2),

\[
x^\tau=\frac1x,\qquad
a^\tau=1-c+b,\qquad
c^\tau=1-a+b.
\tag{K1}
\]

Put

\[
 F(z)={}_2F_1(a,b;c;z),\qquad
 F^\dagger(w)={}_2F_1(a^\tau,b;c^\tau;w).
\]

Here the dagger is notation for conjugating the **parameters**, not a claim
that an analytic value has been conjugated.  On the negative real axis choose
\(\log(-z)\in\mathbf R\), and define

\[
 K(z)=(-z)^{-b}F^\dagger(1/z),\qquad z<0.                 \tag{K2}
\]

The inversion \(w=1/z\), followed by the gauge \((-z)^{-b}\), transforms the
Gauss equation with parameters \((a^\tau,b,c^\tau)\) into the equation with
parameters \((a,b,c)\).  Thus \(K\) is a genuine second solution of the same
differential equation as \(F\).  There is no branch-cut ambiguity in (K2): both
\(z\) and \(1/z\) stay on the negative axis, away from the standard Gauss cut
\([1,\infty)\), and \(-z>0\).

For \(|\arg(-z)|<\pi\), the standard connection formula is

\[
 \begin{aligned}
 F(z)={}&A(-z)^{-a}
 {}_2F_1\!\left(a,1-c+a;1-b+a;\frac1z\right)+B K(z),\\
 A={}&\frac{\Gamma(c)\Gamma(b-a)}
             {\Gamma(b)\Gamma(c-a)},\qquad
 B=\frac{\Gamma(c)\Gamma(a-b)}
             {\Gamma(a)\Gamma(c-b)}.
 \end{aligned}                                             \tag{K3}
\]

Formula (K3) is initially stated away from integral resonances and elsewhere
by the usual limiting continuation.  In the nonresonant positive chart
\(b>0\) and \(c-a\notin\{0,-1,-2,\ldots\}\), the two displayed infinity
solutions are independent.  Comparing their leading powers at negative
infinity gives the exact Wronskian:

\[
 \boxed{
 W(F,K)(z)=
 \frac{\Gamma(c)\Gamma(b-a+1)}
      {\Gamma(b)\Gamma(c-a)}
 (-z)^{-c}(1-z)^{c-a-b-1}.
 }
\tag{K4}
\]

We use \(W(F,K)=FK'-F'K\).  All powers in (K4) are positive-real powers for
\(z<0\).  In particular, the Wronskian is nonzero under the stated
nonresonance hypotheses.

## The exact defect in the tempting conjugation argument

Let \(\lambda=(p+D)/2\), so that \(\lambda^\tau=(p-D)/2=x\lambda\).  For a
rational proposed boundary \(M\), put

\[
 T=\frac M\lambda,\qquad
 k=\frac{b((c-a)/T-1)}{x-1}.
\tag{D1}
\]

The PCF equality is equivalent to

\[
 \frac{F'(x)}{F(x)}=k.                                     \tag{D2}
\]

Because \(c-a\in\mathbf Q\), formal quadratic conjugation of the algebraic
right-hand side gives

\[
 k^\tau=
 \frac{b((c-a)/(M/\lambda^\tau)-1)}{1/x-1}.                \tag{D3}
\]

A direct simplification, using \(\lambda^\tau=x\lambda\), yields

\[
 -\frac bx-\frac{k^\tau}{x^2}=k.                           \tag{D4}
\]

On the other hand, differentiating (K2) gives

\[
 \frac{K'(x)}{K(x)}=-\frac bx-\frac1{x^2}
 \frac{(F^\dagger)'(1/x)}{F^\dagger(1/x)}.                 \tag{D5}
\]

Consequently, if one were allowed to assert

\[
 \frac{(F^\dagger)'(1/x)}{F^\dagger(1/x)}=k^\tau,          \tag{D6}
\]

then (D4)--(D5) would give \(K'(x)/K(x)=F'(x)/F(x)\),
contradicting (K4).  More precisely, whenever the quotients exist, (D2)
implies
the exact connection defect

\[
 \boxed{
 \frac{(F^\dagger)'(1/x)}{F^\dagger(1/x)}-k^\tau
 =-x^2\frac{W(F,K)(x)}{F(x)K(x)}\ne0.
 }
\tag{D7}
\]

Thus Kummer inversion does not merely fail to prove (D6): it proves that
(D6) is false whenever the original equality (D2) holds in a nonresonant
chart.  Quadratic conjugation acts on each finite algebraic coefficient, but
it cannot be commuted with the infinite sum or analytic continuation defining
the special value.  The nonzero Wronskian in (D7) is the exact analytic term
lost by that forbidden interchange.

## Kimura's theorem leaves no irreducible solvable family

The exponent differences of the Gauss equation may be chosen as

\[
 \delta_0=1-c,\qquad
 \delta_1=c-a-b=-\frac{\sigma d}{r},\qquad
 \delta_\infty=a-b.                                       \tag{G1}
\]

Under (P1), \(\delta_0\) and \(\delta_\infty\) are irrational quadratic
numbers, whereas \(\delta_1\) is rational.
[Kimura's solvability theorem](https://doi.org/10.24546/0100498821) has two
alternatives.

In its first alternative, one of

\[
 \delta_0+\delta_1+\delta_\infty,\quad
 -\delta_0+\delta_1+\delta_\infty,\quad
 \delta_0-\delta_1+\delta_\infty,\quad
 \delta_0+\delta_1-\delta_\infty
\]

is an odd integer.  These four numbers are, respectively,

\[
 1-2b,\qquad 2(c-b)-1,\qquad 1-2(c-a),\qquad 1-2a.        \tag{G2}
\]

Therefore this alternative is exactly

\[
 a\in\mathbf Z\quad\text{or}\quad b\in\mathbf Z
 \quad\text{or}\quad c-a\in\mathbf Z
 \quad\text{or}\quad c-b\in\mathbf Z,                     \tag{G3}
\]

the usual reducibility criterion.  Here \(a\) and \(c-b\) are irrational,
while the cross-orientation identity gives

\[
 c-a_\sigma=b_{-\sigma}.
\]

Hence (G3) reduces to integrality of one of the two rational numerator-factor
offsets \(b_+\) or \(b_-\).

Kimura's second alternative is impossible.  Families 2--15 in Kimura's
table require all three exponent differences to be rational modulo signs and
integers.  Family 1 permits one arbitrary difference but requires the other
two to be half-integral.  Our triple (G1) has exactly one rational member.
It therefore belongs to none of those families, in any order or choice of
signs.

It follows that

\[
 \boxed{
 \text{the nonaligned equation is Liouvillian-solvable}
 \iff b_+\in\mathbf Z\text{ or }b_-\in\mathbf Z,
 }
\tag{G4}
\]

and every solvable case is reducible.  In particular there is no hidden
irreducible dihedral family and no finite-monodromy family in the residual
branch.  When neither offset is integral, differential-Galois factorization
cannot turn the isolated algebraic logarithmic-derivative question (D2) into
an elementary identity.

## Reducible does not mean rational tail

It is important not to strengthen (G4) incorrectly.  A positive integral
factor offset makes the differential equation reducible, but the principal
solution can still be a quadrature of the hyperexponential solution rather
than that solution itself.

For example, take

\[
 (p,q,r,u,v)=(1,0,3,4,1),\qquad N=1.
\tag{E1}
\]

Then

\[
 D=\sqrt{13},\qquad d=2,qquad R=1,qquad
 B_n=3(n+1)(n+1/3).
\]

In the plus orientation,

\[
 b=1,qquad
 a=\frac56-\frac1{6\sqrt{13}},qquad
 c=\frac76-\frac1{6\sqrt{13}},qquad c-a=\frac13.
\tag{E2}
\]

The equation is reducible.  Indeed Euler's transformation of the second
local solution gives the hyperexponential solution

\[
 h(z)=z^{1-c}(1-z)^{c-a-1},                                \tag{E3}
\]

and reduction of order writes the principal solution locally as

\[
 F(z)=h(z)\left(C+
   \int^z t^{c-2}(1-t)^{a-c}\,dt\right).                   \tag{E4}
\]

The remaining value is an incomplete-beta-type quadrature with a quadratic
irrational exponent, not a rational function.

This example has no rational-function Riccati tail.  To see this directly,
the dominant slope and affine offset are

\[
 \lambda=\frac{1+\sqrt{13}}2,qquad
 \beta=\frac{\lambda q+u-r}{D}=\frac1{\sqrt{13}}.
\]

The rational-tail structure theorem would give, for some denominator degree
\(m\ge0\), a linear factor

\[
 C(n)=\lambda n+c_0,qquad c_0=\beta+(m+1)\lambda.
\]

Since \(C(n)K(n)=B_n=3(n+1)(n+1/3)\), the offset
\(c_0/\lambda\) would have to be either \(1\) or \(1/3\).  But

\[
 \frac{c_0}{\lambda}=m+1+\frac1{\sqrt{13}\lambda}
\]

is irrational, a contradiction.  Thus the integral cases in (G4) must not
be silently identified with the already-certified rational-tail locus.

## Consequence for the equality problem

The residual equality problem now splits more sharply:

1. If neither rational factor offset is integral, the Gauss equation is
   irreducible and non-Liouvillian.  Any algebraic value in (D2) would be a
   genuinely isolated special value, not an elementary, finite-monodromy, or
   dihedral identity.
2. If a factor offset is integral, the operator reduces, but the principal
   solution generally becomes an incomplete-beta-type quadrature with an
   algebraic irrational exponent.  Algebraicity of its projective value at
   \(x\) is still not supplied by differential-Galois solvability.
3. Kummer inversion supplies the explicit nonzero defect (D7).  A future
   arithmetic theorem strong enough to control this defect--rather than
   formally conjugating the analytic value--would rule out the remaining
   equality.

## A Lerch hard core already inside the family

The obstruction persists in a particularly small reducible subfamily. Take

\[
 B_n=r(n+1)^2,\qquad (u,v)=(2r,r),\qquad N=1.             \tag{L1}
\]

Then both numerator offsets are one and

\[
 b=c-a=1,\qquad
 a=\frac12-\frac{p-2q}{2D},\qquad
 c=a+1.                                                   \tag{L2}
\]

The nonaligned condition is `2q!=p`, so $a$ is a quadratic irrational.
The principal Gauss function is the Lerch/incomplete-beta value

\[
 F(x)={}_2F_1(a,1;a+1;x)=a\,\Phi(x,1,a),\qquad
 \Phi(x,1,a)=\sum_{n\ge0}\frac{x^n}{n+a}.                \tag{L3}
\]

It satisfies the elementary differential identity

\[
 xF'(x)+aF(x)=\frac{a}{1-x}.                              \tag{L4}
\]

For a proposed rational boundary $M\ne0$, (D1)--(D2) and (L4) therefore
give the exact equivalence

\[
 \boxed{
 s_1=M\quad\Longleftrightarrow\quad
 \Phi(x,1,a)=
 \frac{1}{a(1-x)+x(1-\lambda/M)}.
 }                                                        \tag{L5}
\]

The apparent four-parameter denominator in (L5) collapses. If
\(\mu=(D-p)/2\), then

\[
 x=-\frac\mu\lambda,\qquad
 a=\frac{q+\mu}{D},\qquad
 1-x=\frac D\lambda,\qquad \lambda\mu=r.
\]

Therefore

\[
 a(1-x)+x(1-\lambda/M)=\frac{qM+r}{\lambda M},
\]

and the residual target is simply

\[
 \boxed{
 s_1=M\quad\Longleftrightarrow\quad
 \Phi(x,1,a)=\frac{M\lambda}{qM+r}.
 }                                                        \tag{L5'}
\]

When \(qM+r=0\), the undivided contiguous identity is inconsistent, so the
candidate is excluded directly. Arbitrarily delayed exact exclusions inside
this same Lerch slice are constructed in
[polynomial-tail-lerch-delayed-failure.md](polynomial-tail-lerch-delayed-failure.md).
The homogeneous scaling law and the specialization \(q=0\) are developed in
[polynomial-tail-projective-rationality.md](polynomial-tail-projective-rationality.md):
after division by \(p\), that two-coefficient family becomes the rationality
problem for one function \(\mathcal F(r/p^2)\), and an integral scaling ray
contains a true integer hit exactly when this normalized value is rational.

Thus a general exclusion theorem would already have to rule out one explicit
algebraic value of $\Phi(x,1,a)$, where $x$ and $a$ are linked quadratic
irrationals. The usual rational-parameter G-function theory does not apply:
the parameter $a$ is algebraic irrational.

Two large slices of (L1) are nevertheless already decided by reindexing the
strict BDS traps. Put $t_n=s_{n-1}$. Then

\[
 t_n=p(n-1)+q+\frac{rn^2}{t_{n+1}}.                      \tag{L5a}
\]

If $q=0$ and $1\le r\le p$, this is exactly the scaled-BDS wedge. If
$r=1$ and $0\le q\le p$, it is the generalized-BDS strip with offset
$q-p\in[-p,0]$. The existing traps are strict: the positive tail lies
strictly above its affine center, while `integer_gap` makes the trap narrower
than the distance to every integer above that center. Consequently no
$t_n$, hence no $s_n$, is an integer on either slice. The formal
corollaries are `scaled_bds_ne_integer` and
`generalized_bds_ne_integer` in the corresponding Lean files.

The elementary cleared-orbit theorem in
[polynomial-tail-lerch-q0-linear-wedge.md](polynomial-tail-lerch-q0-linear-wedge.md)
extends the first slice uniformly to (q=0, 1\le r\le40p). The symbolic
subwedge \(r\le2p\) fails by the seventh orbit term; an exact finite reduction
and standalone integer certificate extend this to \(40p\), with failure by
the forty-sixth term. It also reduces every fixed wedge \(r\le Cp\) to a
finite list after four inequalities.

Therefore the genuinely unresolved Lerch core begins only outside

\[
 \{q=0,\ 1\le r\le40p\}\ \cup\
\{r=1,\ 0\le q\le p\}.                                  \tag{L5b}
\]

This union is only the simplest closed-form coverage. More generally,
`TryShiftedExactBeattyTrapCertificate` applies the native recognizer to the
shifted coefficients

\[
 (p,q-p,r,0,0).                                          \tag{L5c}
\]

and verifies the positive backward extension $t_1=q+r/s_1$. Whenever it
returns a `NormGap` certificate, its strict trap gives the same all-index
nonintegrality conclusion. Both total DFAO constructors consume this shifted
certificate directly. Thus (L5) is an arithmetic hard core only after the
shifted norm-gap recognizer has also failed; treating every parameter outside
(L5b) as open would discard a substantial part of the exact toolkit.

There is also an exact reason that direct truncation plus the product formula
does not settle (L5). Let

\[
 S_N=\sum_{n=0}^N\frac{x^n}{n+a}.
\]

If the right side of (L5) were the true value, its remainder would satisfy

\[
 \Phi(x,1,a)-S_N
 \sim\frac{x^{N+1}}{(N+1+a)(1-x)}.                       \tag{L6}
\]

But $a^\tau=1-a$ and $x^\tau=1/x$. The conjugate finite sum is therefore

\[
 S_N^\tau=\sum_{n=0}^N\frac{x^{-n}}{n+1-a},
\]

whose last terms have size $\asymp |x|^{-N}/N$. The exponentially small
factor in (L6) is cancelled at the other real embedding; the norm of the
putative algebraic remainder is only of order $N^{-2}$, not exponentially
small. Moreover a direct algebraic denominator clears through a product of
the $N+1$ distinct factors $n+a$, whose norm has

\[
 \log\left|N_{\mathbf Q(D)/\mathbf Q}
       \prod_{n=0}^N(n+a)\right|=2N\log N+O(N).           \tag{L7}
\]

Consequently the elementary truncation/product-formula bound is far too weak
to force the remainder to vanish. A successful arithmetic proof needs a
simultaneous two-embedding approximation with substantially better
denominator control, or a different theorem for algebraic-irrational Lerch
parameters. Merely increasing the one-embedding truncation depth cannot close
the gap.
