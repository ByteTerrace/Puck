# Projective rationality behind integer tail hits

## 1. Homogeneity

Consider the positive polynomial tail

\[
 s_n=pn+q+\frac{rn^2+un+v}{s_{n+1}}.                 \tag{1}
\]

For every positive rational number \(h\), put

\[
 (p',q',r',u',v')=(hp,hq,h^2r,h^2u,h^2v),
 \qquad s'_n=hs_n.                                    \tag{2}
\]

Then (1) gives, identically,

\[
 s'_n=p'n+q'+\frac{r'n^2+u'n+v'}{s'_{n+1}}.          \tag{3}
\]

Thus the Riccati tail is homogeneous of weight one in \((p,q,s)\) and
weight two in \((r,u,v)\).  Positivity, square or nonsquare status of both
discriminants, alignment, and rational-function solvability are preserved:

\[
 \Delta'_c=h^2\Delta_c,\qquad
 \Delta'_B=h^4\Delta_B,\qquad
 R'=h^3R.                                              \tag{4}
\]

The same calculation applies to every finite truncation and every cleared
orbit.  This is the structural reason that the denominator-clearing
construction in
[`polynomial-tail-lerch-delayed-failure.md`](polynomial-tail-lerch-delayed-failure.md)
can turn a rational finite tail into an integral false boundary without
leaving its residual stratum.

There is also a converse interpretation.  Fix a rational projective shape
and an index \(N\).  Some integral representative of its positive scaling ray
has an integer value at \(N\) if and only if the normalized value \(s_N\) is
rational.  Indeed, an integer hit \(hs_N\in\mathbf Z\) implies
\(s_N\in\mathbf Q\).  Conversely, if \(s_N\in\mathbf Q\), choose \(h\) to
clear simultaneously the coefficient denominators and the denominator of
\(s_N\).

Consequently a classification of integer hits on all integral scaling rays
contains a special-value rationality classification.  This is a statement
about the family of all representatives on a ray; it is not by itself a
one-call reduction from rationality to a proposed integer-hit algorithm,
because the required clearing scale is not known before the value is known.

## 2. The one-variable Lerch core

For the repeated-root numerator slice

\[
 s_n=pn+\frac{r(n+1)^2}{s_{n+1}},qquad p,r>0,        \tag{5}
\]

divide by \(p\) and put

\[
 \rho=\frac r{p^2},\qquad y_n=\frac{s_n}{p}.
\]

Then

\[
 y_n=n+\frac{\rho(n+1)^2}{y_{n+1}}.                  \tag{6}
\]

In particular the normalized first tail depends on the two coefficients only
through the single positive rational parameter \(\rho\).  Write it as

\[
 \mathcal F(\rho)=y_1.
\]

Let

\[
 D_0=\sqrt{1+4\rho},\qquad
 c=\frac{D_0-1}{2},\qquad
 t=\frac{c}{1+c},\qquad
 a=\frac{c}{D_0}.                                    \tag{7}
\]

The Lerch reduction in
[`polynomial-tail-lerch-arithmetic.md`](polynomial-tail-lerch-arithmetic.md)
simplifies here to the exact one-variable formula

\[
 \boxed{
 \mathcal F(\rho)
   =c\,\Phi(-t,1,a)
   =c\,t^{-a}B_a(a,1-a).
 }                                                     \tag{8}
\]

The second equality uses

\[
 \frac{t}{1+t}=\frac{c}{1+2c}=\frac c{D_0}=a.        \tag{9}
\]

Thus this residual two-coefficient family is already the rationality problem
for the self-parameterized incomplete-beta value in (8).

For integral \(p,r\), an integer first tail is equivalent to

\[
 p\mathcal F(r/p^2)\in\mathbf Z,                      \tag{10}
\]

and hence forces \(\mathcal F(r/p^2)\in\mathbf Q\).  Conversely, fix any
positive rational \(\rho\).  The integral representatives

\[
 p=kL,qquad r=\rho p^2
\]

exist after choosing \(L\) to clear the denominator of \(\rho\).  If
\(\mathcal F(\rho)\) is rational, a further multiple of \(p\) makes
\(p\mathcal F(\rho)\) integral.  Therefore

\[
 \boxed{
 \text{the integral ray of (6) contains a true integer hit}
 \iff \mathcal F(\rho)\in\mathbf Q.
 }                                                     \tag{11}
\]

The delayed-zero family with \((p,r)=(b,b^2)\) lies on the single ray
\(\rho=1\).  Its proposed integers are denominator-cleared finite convergents
to \(\mathcal F(1)\).  Their arbitrary survival depth is therefore a sequence
of increasingly accurate rational approximations to one fixed special value,
not evidence for a sequence of unrelated exceptional tails.

The current unconditional classification of this ray problem is recorded in
[polynomial-tail-lerch-q0-linear-wedge.md](polynomial-tail-lerch-q0-linear-wedge.md).
It proves nonintegrality for every integral representative with
\(1\le r\le40p\), and proves that \(\mathcal F(\rho)\) is transcendental when
\(1+4\rho\) is a rational square.  Hence a still-open integral representative
must satisfy \(r>40p\) and have nonsquare characteristic discriminant.

## 3. What this changes

Equation (11) sharpens the remaining theorem in two ways.

1. A universal claim that the non-rational Lerch slice has no integer hits
   would already prove
   \(\mathcal F(\rho)\notin\mathbf Q\) for every positive rational \(\rho\)
   with \(1+4\rho\) nonsquare.
2. A genuine exceptional equality would produce a rational value of the
   self-parameterized incomplete beta function (8), and homogeneous scaling
   would then produce infinitely many integral representatives with integer
   hits.

Bare transcendence of \(t^a\) does not decide this.  By (8), rationality of
\(\mathcal F(\rho)\) is precisely the possibility that the incomplete-beta
value is an algebraic multiple of that transcendental power.  This is the
same linear-independence obstruction isolated in the Lerch arithmetic note,
now expressed as a one-variable projective rationality problem.
