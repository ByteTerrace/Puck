# The positive EGF counterexample is not a PCF pullback

The strictly positive integral EGF

\[
 F_0(z)=\frac14\bigl((2+\sqrt2)(1-z)^{-\sqrt2}
                    +(2-\sqrt2)(1-z)^{\sqrt2}\bigr)
\]

from [`polynomial-tail-positive-egf-arithmetic.md`](polynomial-tail-positive-egf-arithmetic.md)
shows that coefficient positivity and holonomy alone do not force factorial
reduction.  This note asks a sharper question: can that example be put back
into the degree-\((2,1)\) polynomial-continued-fraction equation by a rational
pullback and a scalar gauge?

The answer is **no**, independently of the initial seed.  The obstruction is
sharp: the only locally compatible boundary has \(B_1=0\), precisely where
the strict PCF numerator-positivity hypothesis fails.

## 1. The pullback spectrum of the Euler equation

With \(x=1-z\), the minimal equation of \(F_0\) is

\[
 x^2Y''+xY'-2Y=0,                                      \tag{1}
\]

with solution space spanned by \(x^{\sqrt2}\) and
\(x^{-\sqrt2}\).  Let \(x=\phi(w)\) be a nonconstant rational
pullback and multiply both pulled-back solutions by any common meromorphic
or algebraic scalar gauge.  A scalar gauge shifts the two local exponents by
the same amount, so it does not change their difference.

At any point \(w_0\), there are only two possibilities.

* If \(\phi\) has a zero or pole of order \(m\ne0\) at \(w_0\), the local
  exponent difference is

  \[
  2|m|\sqrt2.                                          \tag{2}
  \]

* If \(\phi(w_0)\in\mathbf C^*\) and \(\phi-\phi(w_0)\) has order
  \(e\ge1\), the source equation is ordinary there.  Its two pulled-back
  solutions have orders differing by

  \[
  e.                                                    \tag{3}
  \]

  When \(e>1\), this is the familiar apparent singularity created by using a
  ramified scalar cyclic vector over an ordinary point.  The unramified case
  has ordinary difference one.

Thus every local exponent difference of a scalar-gauged pullback belongs,
up to sign, to

\[
 \boxed{\{1,2,3,\ldots\}\ \cup\
        \{2\sqrt2,4\sqrt2,6\sqrt2,\ldots\}.}           \tag{4}
\]

Moreover, a nonconstant rational function has at least one zero and one
pole, at distinct points.  Hence at least two distinct points have a
\(2m\sqrt2\)-type difference.

## 2. The three PCF exponent differences

Consider the PCF EGF equation

\[
 (1+pz-rz^2)F''=
 ((3r+u)z-(2p+q))F'+(r+u+v)F,                         \tag{5}
\]

where \(p,q,r,u,v\in\mathbf Z\), \(p,r>0\), and put

\[
 D=\sqrt{p^2+4r},\qquad
 d^2=u^2-4rv,\qquad
 R=p(u-r)-2rq.                                        \tag{6}
\]

The two roots of \(1+pz-rz^2\) are distinct.  At the negative root and the
positive root, respectively, the exponent differences are

\[
 \boxed{
 \delta_-=-\frac{r+u}{2r}+\frac{R}{2rD},\qquad
 \delta_+=-\frac{r+u}{2r}-\frac{R}{2rD}.}             \tag{7}
\]

At infinity the exponent difference is

\[
 \boxed{\delta_\infty=\frac d r}                       \tag{8}
\]

up to sign.  In the local coordinate \(x=1/z\), the two infinity exponents
are

\[
 \rho_\pm=\frac{2r+u\pm d}{2r}.                        \tag{9}
\]

Formula (7) follows immediately from the indicial equation

\[
 \rho\bigl(A'(z_0)(\rho-1)-L(z_0)\bigr)=0,
 \quad A=1+pz-rz^2,\quad L=(3r+u)z-(2p+q),           \tag{10}
\]

at a simple root \(z_0\) of \(A\).  Substitution of the two roots gives
(7).  Formula (8) follows from the indicial polynomial

\[
 -r\rho(\rho-1)+(r+u)\rho-(r+u+v)                     \tag{11}
\]

in the coordinate \(x=1/z\).

## 3. Impossibility under strict numerator positivity

Assume

\[
 B_n=rn^2+un+v>0\qquad(n\ge1).                         \tag{12}
\]

Suppose for contradiction that (5) is a scalar-gauged rational pullback of
(1).

If \(D\in\mathbf Q\), or if \(R=0\), both finite differences in (7) are
rational.  Then only infinity can possibly have a \(2m\sqrt2\)-type
difference.  This contradicts the fact that a nonconstant rational pullback
has distinct zero and pole points.

It remains to consider \(D\notin\mathbf Q\) and \(R\ne0\).  Now both finite
differences in (7) are irrational, so (4) forces both to be of
\(2m\sqrt2\)-type.  For signs \(\epsilon_\pm\in\{\pm1\}\) and positive
integers \(m_\pm\), write

\[
 \delta_-=2\epsilon_-m_-\sqrt2,
 \qquad
 \delta_+=2\epsilon_+m_+\sqrt2.                       \tag{13}
\]

Their sum is rational by (7).  Therefore the irrational parts cancel:

\[
 \epsilon_-m_-+\epsilon_+m_+=0.
\]

The signs are opposite, the orders are equal, and (13) has sum zero.  But
(7) also gives

\[
 \delta_-+\delta_+=-\frac{r+u}{r}.
\]

Consequently

\[
 \boxed{u=-r.}                                        \tag{14}
\]

Strict positivity at \(n=1\) now says

\[
 B_1=r+u+v=v>0.                                       \tag{15}
\]

On the other hand, (8) and (14) give

\[
 \delta_\infty^2
 =\frac{u^2-4rv}{r^2}
 =1-\frac{4v}{r}<1.                                   \tag{16}
\]

This is impossible under (4): an ordinary/ramified difference has square at
least \(1\), while a zero/pole difference has square at least \(8\).

We have proved the following parameter-uniform statement.

> **Pullback obstruction.** If \(p,r>0\) and
> \(rn^2+un+v>0\) for every \(n\ge1\), then the degree-\((2,1)\) PCF EGF
> operator (5) is not obtained from the minimal operator of \(F_0\) by a
> nonconstant rational pullback followed by any common scalar meromorphic or
> algebraic gauge.

In particular, no choice of rational positive minimal seed can turn the
positive-EGF counterexample into an admissible PCF counterexample by this
route: the operator obstruction occurs before the initial condition is
considered.

## 4. Sharpness: the excluded \(B_1=0\) boundary really is Euler

The strict inequality in (15) is essential.  Put

\[
 u=-r,\qquad v=0.                                    \tag{18}
\]

Then \(B_n=rn(n-1)\), so \(B_1=0\), and the infinity exponents are \(0,1\).
The infinity point is apparent/ordinary in the projective equation.  The
constant term in (5) vanishes, so \(F=1\) is a solution.

For the finite differences to equal \(\mp2m\sqrt2\), write

\[
 D=h\sqrt2,qquad p^2+4r=2h^2,qquad
 p+q=4mh,                                              \tag{19}
\]

where integrality forces rational \(h\) to be an integer.  If
\(\alpha<0<\beta\) are the roots of \(1+pz-rz^2\), put

\[
 H(z)=\frac{z-\alpha}{z-\beta}.                        \tag{20}
\]

The second solution is a constant multiple of

\[
 H(z)^{-2m\sqrt2}.                                    \tag{21}
\]

Equivalently, the solution space is a scalar gauge of the pullback of
\(x^{\pm\sqrt2}\) under \(x=H(z)^m\): multiply the pulled-back basis by
\(H(z)^{-m\sqrt2}\).  This realizes exactly the exponent pattern used in
the proof.  It also explains why the only compatible PCF boundary has a
constant solution and a vanishing first numerator.

Thus the obstruction is not a coarse mismatch of singularity counts.  The
Euler geometry reaches the closure of the PCF parameter space, but strict
positivity removes precisely that closure stratum.

## 5. Apparency at infinity and integer-shift near misses

Matching exponent differences only modulo integers is insufficient for a
Darboux or change-of-cyclic-vector proposal.  The missing local datum is the
resonance obstruction.

In the coordinate \(x=1/z\), (5) becomes

\[
 x^2(x^2+px-r)Y''
 +x(2x^2-qx+r+u)Y'-(r+u+v)Y=0.                        \tag{22}
\]

Suppose \(k=d/r\) is a positive integer, and start the Frobenius solution at
the smaller exponent \(\rho_-\) from (9):

\[
 Y=x^{\rho_-}\sum_{n\ge0}c_nx^n,qquad c_0=1.
\]

For \(n\ge0\), its coefficients obey

\[
\begin{aligned}
 I(\rho_-+n)c_n
 &+(\rho_-+n-1)
       \bigl(p(\rho_-+n-2)-q\bigr)c_{n-1}\\
 &+(\rho_-+n-2)(\rho_-+n-1)c_{n-2}=0,                \tag{23}\\
 I(s)&=-rs(s-1)+(r+u)s-(r+u+v),
\end{aligned}
\]

with \(c_{-1}=c_{-2}=0\).  At \(n=k\), the first coefficient vanishes.
Infinity is log-free only if the remaining resonance numerator in (23)
vanishes.  This is an exact finite apparency certificate.

For the common near-miss \(k=1\), the condition reduces to

\[
 \boxed{\rho_-\bigl(p(\rho_--1)-q\bigr)=0.}           \tag{24}
\]

For example,

\[
 (p,q,r,u,v)=(2,10,1,3,2)                             \tag{25}
\]

has exponent differences

\[
 -2-2\sqrt2,qquad -2+2\sqrt2,qquad1.                \tag{26}
\]

They agree modulo integers with the tempting Euler/Mobius pattern.  But
\(\rho_-=2\), and (24) equals \(-16\ne0\).  Infinity is logarithmic, not
apparent, so the operator is not an Euler pullback.  This example isolates
the extra condition that any proposed Darboux embedding must check.

## 6. Scope

The pullback obstruction covers rational pullback followed by scalar gauge,
including an algebraic scalar gauge: exponent differences are unchanged by
any common scalar factor.  An untwisted rational \(2\times2\)
differential-module gauge
is ruled out even earlier by determinant monodromy.  Equation (1) has trivial
determinant character.  A finite PCF singularity has determinant multiplier
\(e^{2\pi i\delta_\pm}\), and a rational gauge changes local exponents only
by integers.  At least one of the distinct zero and pole of a rational
pullback is finite.  Trivial determinant would force the corresponding
finite \(\delta_\pm\) to be integral, while its projective eigenvalue ratio
would simultaneously have to be \(e^{4\pi i m\sqrt2}\ne1\), a contradiction.

A Darboux transformation combined with a non-rational rank-one twist is a
strictly broader operation.  It preserves exponent differences only modulo
integers and must additionally satisfy every resonance condition such as
(23).  The theorem does not silently identify that broader category with a
scalar gauge.  What it does prove is enough for the arithmetic question that
motivated it: the explicit positive EGF counterexample itself is not hiding
inside the admissible PCF operator family through the usual pullback-and-gauge
route.
