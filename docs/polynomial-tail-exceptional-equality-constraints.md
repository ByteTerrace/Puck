# Structural constraints on an exceptional polynomial-tail equality

This note records what a genuinely new exact equality would have to evade in
the square-numerator, nonsquare-characteristic, nonaligned branch.  It is a
constraint theorem, not a proof that the branch is empty.

The companion note
[`polynomial-tail-connection-coordinate.md`](polynomial-tail-connection-coordinate.md)
derives the exact logarithmic-derivative target, the conjugate Kummer solution,
its Wronskian defect, and the relevant Kimura classification.  The present
note concentrates on consequences of numerator positivity and on constructive
transformation routes.

Write

\[
 A_n=pn+q,\qquad B_n=rn^2+un+v,
\]

with `p,r>0`, `B_n>0` for every `n>=1`, and put

\[
 D=\sqrt{p^2+4r},\qquad d^2=u^2-4rv,\qquad
 R=p(u-r)-2rq.
\]

The branch considered here has `d` rational, `D` irrational, and `R!=0`.

## Shift-normal form

An equality question at an arbitrary index can be moved to index one without
changing any of the three discriminant/alignment invariants.  For
`j>=1`, set

\[
 \widetilde A_j=A_{N+j-1}=pj+q',\qquad
 \widetilde B_j=B_{N+j-1}=rj^2+u'j+v'.
\]

Then

\[
 q'=q+p(N-1),\quad u'=u+2r(N-1),\quad v'=B_{N-1},
\]

and direct expansion gives

\[
 u'^2-4rv'=u^2-4rv,\qquad
 p(u'-r)-2rq'=R.                                      \tag{1}
\]

Factor the shifted numerator as

\[
 \widetilde B_j=r(j+b)(j+g),\qquad
 b=N-1+\frac{u+\sigma d}{2r},\quad
 g=N-1+\frac{u-\sigma d}{2r},                          \tag{2}
\]

where `sigma=+1` or `-1`.  The two factor orientations simply interchange
`b` and `g`.

Positivity also synchronizes the two offsets.  The connected components of

\[
 \mathbb R\setminus\{-1,-2,-3,\ldots\}
\]

are `(-1,infinity)` and the unit strips `(-m-1,-m)`, `m>=1`.
The numbers `b` and `g` must lie in the same component.  Indeed, if a negative
integer `-j` separated them, then `(j+b)(j+g)<0`, contradicting (2); equality
would instead give the already-forbidden zero `Btilde_j=0`.  Conversely, two
offsets in the same component give factors with the same sign at every
positive integer.  Thus the numerator-positivity hypothesis is exactly a
same-strip condition on the two rational Gauss offsets.

The Gauss function in the minimality reduction consequently has the canonical
form

\[
 F(z)={}_2F_1(a,b;a+g;z),\qquad
 a=\frac{r+\sigma d}{2r}-\frac{R}{2rD},\qquad
 z=x=\frac{p-D}{p+D}\in(-1,0).                          \tag{3}
\]

In particular, the two rational numerator-root offsets are not incidental
parameters: they are exactly `b` and `c-a=g`.  Reversing the factorization
sends

\[
 (a,b,g)\longmapsto(a-b+g,g,b),                         \tag{4}
\]

and leaves `c=a+g` unchanged.

## No terminating or power-times-polynomial escape

In the nonaligned branch, `a`, `c=a+g`, and `c-b=a+g-b`
are irrational, because each has a nonzero rational multiple of `1/D` as
its irrational part.  Therefore none of these can be a nonpositive integer.

The remaining terminating possibilities are also excluded by numerator
positivity.  If `b=-m` for an integer `m>=1`, then (2) gives
`Btilde_m=0`; if `g=-m`, the same argument applies to the other factor.
Both contradict `B_n>0` at positive indices.  Thus none of

\[
 a,\quad b,\quad c-a,\quad c-b                         \tag{5}
\]

can be a negative integer.  These are precisely the elementary terminating
and Euler-transformed power-times-polynomial degeneracies of the Gauss
function.

The only zero-offset resonance is harmless.  If one of `b,g` is zero, use
the opposite factor orientation.  If both vanish, then the shifted numerator
is `rj^2`; the exact Riccati step

\[
 s_1=A_1+\delta\quad\Longleftrightarrow\quad s_2=r/\delta
\]

moves the question to index two, where both offsets equal one.  This is the
same regularization used by the exact Hausdorff certificate.

Consequently, an exceptional equality cannot arise from a hidden terminating
series, an Euler-transformed terminating series, or the apparent zero-factor
chart singularity.

## Transformation rigidity

The exponent differences of (3) are

\[
 \theta_0=1-a-g,\qquad \theta_1=g-b,\qquad
 \theta_\infty=a-b.                                      \tag{6}
\]

Here `theta_1` is rational, while `theta_0` and `theta_infinity` have
opposite nonzero irrational parts.  Kummer transformations only permute and
change the signs of these differences.  Contiguous transformations shift
them by integers.  A rational pullback multiplies a local exponent difference
by a positive ramification index.  None of these operations can turn the
nonzero irrational part into a rational number.

It follows that no chain of Kummer, contiguous, or classical rational
pullback transformations can move a nonaligned instance into the
rational-parameter one-period branch.  In particular, a Bauer--Muir/Darboux
transformation that is represented by a rational gauge and finitely many
contiguous shifts preserves the obstruction.  A proposed construction from
an aligned continued fraction must therefore use more than such an
equivalence transformation.

The same observation rules out finite hypergeometric monodromy: finite
projective monodromy would make every exponent difference rational modulo
integers, contrary to (6).  This is a global statement about the differential
equation.  It does **not** rule out an algebraic value of `F'(x)/F(x)` at the
single algebraic point `x`; that pointwise possibility is exactly the open
connection-coordinate problem.

## A residual quadratic-symmetry slice

There is one useful family on which classical quadratic transformations can
still simplify the *argument* without rationalizing the parameters.  Apply
Pfaff's transformation and put

\[
 y=\frac{x}{x-1}=\frac{D-p}{2D},\qquad y^\tau=1-y,
 \qquad 4y(1-y)=\frac{4r}{p^2+4r}\in\mathbb Q.            \tag{7}
\]

The two finite exponent differences of the Pfaff equation are opposites
exactly when

\[
 b+g=1\quad\Longleftrightarrow\quad u'=r.                 \tag{8}
\]

On this slice the equation has an explicit quadratic descent.  Pfaff's
transformation writes

\[
 F(a,b;a+g;x)=(1-x)^{-b}H(y),\qquad
 H(y)={}_2F_1(g,b;a+g;y).
\]

Put `t=1-2y`, `kappa=a-b`, and

\[
 H(y)=(1+t)^\kappa K(t).
\]

Substitution in the Gauss equation, using `b+g=1`, gives

\[
 (1-t^2)K''-2(\kappa+1)tK'
 -a(a-2b+1)K=0.                                           \tag{9}
\]

The coefficients are even.  With `z=t^2`, an even/odd basis is

\[
 \begin{aligned}
 U_0(t)&={}_2F_1\!\left(\frac a2,\frac{a-2b+1}{2};
                              \frac12;t^2\right),\\
 U_1(t)&=t\,{}_2F_1\!\left(\frac{a+1}{2},
                         \frac{a-2b+2}{2};\frac32;t^2\right).
 \end{aligned}                                             \tag{10}
\]

At the PCF evaluation point, `t=p/D`, so the new argument
`t^2=p^2/(p^2+4r)` is rational.  This derivation makes precise what the
quadratic symmetry buys: a rational argument and denominators `1/2` or
`3/2`, but numerator parameters that still contain the nonzero `R/D` part.
The descent therefore supplies neither a one-period nor an algebraic
connection coordinate.  The familiar elementary specializations of (10)
would require, for example, a terminating irrational numerator, `b=0`, or
`a=b` (up to the corresponding half-integer shifts); the first is impossible
and the latter two are respectively the handled resonance and the aligned
case.

This does, however, isolate the most plausible classical-transformation
search locus.  For integral coefficients at index one it is

\[
 B_n=rn^2+rn+v,\qquad r^2-4rv=d^2.                        \tag{11}
\]

Any claimed special evaluation on (11) still has to prove algebraicity of the
resulting logarithmic derivative; merely transforming its argument to the
rational numbers in (7) or (10) is insufficient.

## Consequence for constructive searches

A genuine non-rational equality must therefore satisfy all of the following:

1. its Gauss series and both Euler transforms are nonterminating;
2. its differential equation has infinite, non-rational-exponent monodromy;
3. it is not a rational-gauge/Bauer--Muir image of the aligned one-period
   branch;
4. nevertheless its single connection coordinate `F'(x)/F(x)` belongs to
   `Q(D)` and matches the integer boundary;
5. equivalently, its exact algebraic Hausdorff seed lies on the unique
   recessive line and survives every strict moment inequality.

This narrows the constructive problem to a genuinely pointwise special value.
It also explains why classical terminating identities and routine continued-
fraction transformations have not produced a counterexample.
