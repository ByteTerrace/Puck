# Paired-forcing exclusion for integer polynomial tails

## Status

This note proves an infinite four-parameter family of proposed integer tail
values cannot equal the positive infinite tail.  It also proves that false
integer candidates can remain positive for arbitrarily long finite prefixes,
so no uniform fixed-depth orbit test can decide the general problem.

The finite parameter recognizer is
`PolynomialTailPairedForcingExclusionCertificate` in
`src/Puck.Maths/Research/PolynomialTailPairedForcing.cs`.  The argument below
is exact but has not yet been transcribed into Lean.

## Paired-forcing lemma

Let $a>1$, put $\delta=a-1$, and suppose $\varepsilon_n>0$ is summable and

\[
 a\varepsilon_{2m}>\varepsilon_{2m+1}\qquad(m\geq0).             \tag{1}
\]

Then no sequence satisfying

\[
 x_0=x_1>0,qquad
 x_{n+2}=(a+\varepsilon_n)x_n-\delta x_{n+1}                    \tag{2}
\]

can remain positive forever.

To prove this, set

\[
 E_m=x_{2m},\qquad O_m=x_{2m+1},\qquad D_m=E_m-O_m.
\]

Pairing two consecutive steps in (2) gives the exact identities

\[
\begin{aligned}
 E_{m+1}&=(a+\varepsilon_{2m})D_m+(1+\varepsilon_{2m})O_m,\\
 O_{m+1}&=-\delta(a+\varepsilon_{2m})D_m
 +(1+\varepsilon_{2m+1}-\delta\varepsilon_{2m})O_m,\\
 D_{m+1}&=(a^2+a\varepsilon_{2m})D_m
 +(a\varepsilon_{2m}-\varepsilon_{2m+1})O_m.                  \tag{3}
\end{aligned}
\]

Assume for contradiction that every term is positive.  We have $D_0=0$ and
$D_1=a\varepsilon_0-\varepsilon_1>0$.  Equations (1) and (3) then imply

\[
 D_m\geq D_1a^{2m-2}\qquad(m\geq1).                            \tag{4}
\]

The second line of (3) gives both

\[
 O_{m+1}\leq(1+\varepsilon_{2m+1})O_m
\]

and the sharper inequality

\[
 O_{m+1}\leq(1+\varepsilon_{2m+1})O_m-\delta aD_m.             \tag{5}
\]

Summability bounds the positive term uniformly:

\[
 O_m\leq O_0\exp\!\left(\sum_{n\geq0}\varepsilon_n\right).
\]

But (4) makes the negative term in (5) grow exponentially.  Eventually the
right side of (5) is negative, contradicting $O_{m+1}>0$.  This proves the
lemma using only finite inequalities once a sufficiently large $m$ is chosen.

## Four-parameter polynomial-tail family

Choose integers $P,c,k,h\geq1$, put $R=c(c+P)$, and take

\[
 (p,q,r,u,v)=
 \bigl(P,P(k-1),R,R(2k-1),Rk(k-1)+h\bigr).                    \tag{6}
\]

The quadratic numerator is

\[
 B_n=R(n+k-1)(n+k)+h.
\]

Test the integer value

\[
 s_1=k(P+c)=A_1+ck.
\]

Its cleared orbit has $Q_0=1$, $Q_1=ck$, and

\[
 Q_{n+2}=B_{n+1}Q_n-A_{n+2}Q_{n+1}.                           \tag{7}
\]

Normalize by the unperturbed factorial solution

\[
 H_n=c^n(k)_n,qquad Q_n=H_nx_n.
\]

Equation (7) becomes (2) with

\[
 a=1+\frac Pc,qquad
 \delta=\frac Pc,qquad
 \varepsilon_n=\frac{h}{c^2(n+k)(n+k+1)}.                    \tag{8}
\]

These terms are positive and

\[
 \sum_{n\geq0}\varepsilon_n=\frac{h}{c^2k},qquad
 a\varepsilon_{2m}>\varepsilon_{2m+1}.
\]

The lemma forces some $Q_n\leq0$.  By the exact integer-orbit criterion, the
proposed integer is therefore not the unique positive infinite tail.

The characteristic and numerator discriminants are

\[
 \Delta_c=(P+2c)^2,qquad \Delta_B=R(R-4h).
\]

For $h=1$ and $R>4$, $\Delta_B$ is nonsquare: if
$R(R-4)=y^2$, then $(R-2-y)(R-2+y)=4$, whose integer factor pairs force
$R=4$.  Thus the theorem supplies an infinite exact exclusion family in the
nonsquare-numerator branch.

## Arbitrarily delayed failure

For fixed $P,k,h,n$, the orbit value $Q_n$ is a polynomial in $c$ with leading
term $(k)_n c^n$.  Hence for every finite depth $N$, all
$Q_0,\ldots,Q_N$ are positive once $c$ is sufficiently large, even though the
paired-forcing theorem makes a later sign failure inevitable.  This rules out
any proposed uniform decision rule based on checking a fixed number of orbit
terms.

An explicit sufficient escape bound is obtained from (4)--(5).  Any $m$ with

\[
 \frac Pc\left(1+\frac Pc\right)D_1
 \left(1+\frac Pc\right)^{2m-2}
 >(1+\varepsilon_1)\exp\!\left(\frac h{c^2k}\right)
\]

forces $Q_{2m+3}<0$.

## What remains

This proof depends on an exact factorial comparison orbit.  A general positive
minimal orbit has no known normalization that produces a one-sign summable
forcing term.  Extending the argument to the full nonsquare-numerator branch,
or proving directly that positivity forces factorial reduction, remains open.
