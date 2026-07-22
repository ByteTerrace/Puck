# Positive polynomial continued-fraction tails

## Scope

Fix integers \(p,q,r,u,v\) satisfying

\[
p>0,\qquad r>0,\qquad pn+q\geq0,\qquad
rn^2+un+v>0\quad(n\geq1).
\tag{H}
\]

Because the two polynomials are respectively increasing and convex, the API
checks (H) exactly: the base needs only its value at \(1\), while the numerator
needs only \(1\) and the two integers surrounding its vertex.

Put

\[
A_n=pn+q,\qquad B_n=rn^2+un+v,\qquad
T_n(y)=A_n+\frac{B_n}{y}.
\]

This note proves that there is exactly one sequence of positive real numbers
\((s_n)_{n\geq1}\) satisfying

\[
s_n=T_n(s_{n+1}).                                      \tag{1}
\]

It then gives its exact affine asymptote, a constructive \(O(1/n)\) interval,
and its complete formal asymptotic expansion. The implementation is
[PolynomialContinuedFractionTail](../src/Puck.Maths/Research/PolynomialContinuedFractionTail.cs),
and the independent exact checker is
[tools/polynomial-continued-fraction-verifier.cs](../tools/polynomial-continued-fraction-verifier.cs).

## 1. Existence

For \(n\geq1\), \(A_{n+1}>0\). Define

\[
U_n=A_n+\frac{B_n}{A_{n+1}},\qquad J_n=[A_n,U_n].
\]

Every \(J_n\) is a nonempty compact interval. If \(y\in J_{n+1}\), then
\(y\geq A_{n+1}>0\), and monotonicity of \(T_n\) gives

\[
A_n<T_n(y)\leq A_n+\frac{B_n}{A_{n+1}}=U_n.             \tag{2}
\]

Thus \(T_n(J_{n+1})\subset J_n\). Start at any point of \(J_{N+1}\), recurse
backward to coordinate \(1\), and pad the unused coordinates by arbitrary
points of their intervals. Compactness of \(\prod J_n\), or equivalently the
finite-intersection argument formalized in
[BDS/Recurrence.lean](../formal/PuckMathsFormal/PuckMathsFormal/BDS/Recurrence.lean),
produces a sequence in every \(J_n\) satisfying (1). Equation (2) makes every
coordinate strictly positive, even when \(A_1=0\).

## 2. Uniqueness among all positive solutions

The elementary intervals above also bound every positive solution:

\[
A_n<s_n<U_n.                                           \tag{3}
\]

When \(r\) is large compared with \(p^2\), (3) is not yet a strong enough
lower bound for a one-step contraction. Refine it instead. Set

\[
L_{0,n}=A_n,\qquad U_{0,n}=U_n,
\]

and, successively,

\[
L_{j+1,n}=T_n(U_{j,n+1}),\qquad
U_{j+1,n}=T_n(L_{j,n+1}).                               \tag{4}
\]

Antitonicity of \(T_n\) proves by induction that

\[
L_{j,n}<s_n<U_{j,n}\qquad(j\geq0).                     \tag{5}
\]

For fixed \(j\), these are rational functions of \(n\). Their leading slopes
\(\ell_j,h_j\) obey

\[
\ell_0=p,\quad h_0=p+\frac rp,\qquad
\ell_{j+1}=p+\frac r{h_j},\quad
h_{j+1}=p+\frac r{\ell_j}.                              \tag{6}
\]

They are the alternating finite convergents of
\(p+r/(p+r/(\cdots))\), hence

\[
\ell_j\longrightarrow\lambda,\qquad
h_j\longrightarrow\lambda,\qquad
\lambda=\frac{p+\sqrt{p^2+4r}}2.                       \tag{7}
\]

Since \(\lambda^2=p\lambda+r>r\), choose a rational number \(a\) and a finite
\(j\) with

\[
\sqrt r<a<\ell_j.
\]

Equations (5)--(7) imply \(s_n>an\) for all sufficiently large \(n\), for
every positive solution. Equation (3) gives a common linear upper bound.

If \(s,t\) are two positive solutions, exact subtraction of (1) yields

\[
|s_n-t_n|=
\frac{B_n}{s_{n+1}t_{n+1}}|s_{n+1}-t_{n+1}|.           \tag{8}
\]

The multiplier in (8) is eventually at most a fixed \(\rho<1\), because it
tends from above to at most \(r/a^2<1\). Iterating to a terminal index \(N\),
the remaining difference is bounded linearly by (3), so it is at most
\(O(N)\rho^{N-n}\), which tends to zero. Thus \(s_n=t_n\) at every sufficiently
large index, and (1) propagates equality backward to every positive index.
The positive solution is unique.

## 3. Exact slope, offset, and affine residual

Let

\[
x_n=\lambda n+\beta,\qquad \lambda^2=p\lambda+r.
\]

Cancelling the coefficient of \(n\) in \(T_n(x_{n+1})-x_n\) gives the positive
root in (7). Cancelling the constant coefficient gives

\[
\boxed{
\beta=\frac{q\lambda^2+(u-r)\lambda}{\lambda^2+r}
}.                                                       \tag{9}
\]

After these two cancellations the remaining numerator is constant. In fact,
with

\[
\boxed{R=(q-\beta)(\lambda+\beta)+v},                   \tag{10}
\]

direct multiplication gives the exact identity

\[
\boxed{
T_n(x_{n+1})-x_n=\frac{R}{x_{n+1}}
}.                                                       \tag{11}
\]

All three quantities lie in the exact quadratic field
\(\mathbb Q(\sqrt{p^2+4r})\). The API returns them as <code>QuadraticSurd</code>,
without a floating-point seam. A square discriminant is reduced automatically
to a rational value.

## 4. A constructive certified \(O(1/n)\) interval

Choose rational numbers

\[
0<L_2<L_1<\lambda,\qquad L_1L_2>r.                     \tag{12}
\]

The implementation chooses adjacent dyadic lower approximations and raises
their precision until (12) holds. Choose \(N\) large enough that, for
\(n\geq N\),

\[
\frac{x_{n+1}}{n+1}\geq L_1                             \tag{13}
\]

and put

\[
W_N=r+\frac{\max(u,0)}{N+1}
       +\frac{\max(v,0)}{(N+1)^2},\qquad
\theta=\frac{W_N}{L_1L_2}<1.                            \tag{14}
\]

Let \(M\geq|R|\) be an integer and choose an integer \(H\geq0\) satisfying

\[
H\geq\frac{M}{L_1(1-\theta)},\qquad
\frac{H}{(N+1)^2}\leq L_1-L_2.                         \tag{15}
\]

Such an \(N,H\) always exist: \(W_N\to r<L_1L_2\), while the first lower bound
on \(H\) tends to a finite constant and the right side of the second condition
grows quadratically after denominators are cleared. The analyzer searches only
powers of two for \(N\), so termination follows directly from these limits.

Define

\[
I_n=\left[x_n-\frac Hn,x_n+\frac Hn\right].             \tag{16}
\]

For \(n\geq N\), (13) and the second half of (15) show that every
\(y\in I_{n+1}\) satisfies \(y\geq L_2(n+1)>0\). If
\(e=y-x_{n+1}\), exact subtraction using (11) gives

\[
T_n(y)-x_n=\frac R{x_{n+1}}
-\frac{B_ne}{x_{n+1}y}.
\]

Consequently,

\[
|T_n(y)-x_n|
\leq\frac{M/L_1+\theta H}{n+1}
\leq\frac H{n+1}<\frac Hn.                              \tag{17}
\]

Thus \(T_n(I_{n+1})\subset\operatorname{int}I_n\). The compactness construction
produces a positive solution trapped by (16); uniqueness identifies it with the
solution of Sections 1--2. Therefore

\[
\boxed{
\left|s_n-(\lambda n+\beta)\right|\leq\frac Hn
\quad(n\geq N)
}.                                                       \tag{18}
\]

<code>PolynomialTailIntervalCertificate</code> exposes \(N,H,L_1,L_2\), their
shared dyadic denominator, and \(M\). They are sufficient to recheck every
inequality in (12)--(15) using integers and one exact quadratic-surd comparison.
The public <code>VerifyIntervalCertificate</code> method performs that check.

## 5. Arbitrarily many asymptotic terms

Write \(t=1/n\) and seek

\[
s_n=\frac\lambda t+c_0+c_1t+c_2t^2+\cdots.             \tag{19}
\]

After shifting \(n\mapsto n+1\), factor

\[
s_{n+1}=\frac\lambda tG(t),
\]

where

\[
G(t)=1+t+
\sum_{j\geq0}\frac{c_j}{\lambda}
t^{j+1}(1+t)^{-j}.                                      \tag{20}
\]

Let \(G(t)^{-1}=\sum_{j\geq0}h_jt^j\). Substitution into (1) gives

\[
T_n(s_{n+1})=
\frac pt+q+
\frac1\lambda
\left(\frac rt+u+vt\right)
\left(\sum_{j\geq0}h_jt^j\right).                       \tag{21}
\]

At order \(t^m\), the unknown \(c_m\) occurs linearly with coefficient
\(-r/\lambda^2\). Everything else depends only on
\(c_0,\ldots,c_{m-1}\). Hence

\[
c_m=\frac{\text{known coefficient at order }m}
           {1+r/\lambda^2}.                             \tag{22}
\]

The divisor is positive, so (22) determines every coefficient uniquely in the
same quadratic field. For \(m=0\), it reduces exactly to (9).

This formal expansion is asymptotic to the actual tail, not merely symbolic.
Truncating (19) after \(c_mn^{-m}\) and using (20)--(22) leaves recurrence
residual \(O(n^{-m-1})\). Subtracting the truncated approximation from the
unique solution gives an inhomogeneous version of (8), whose multiplier tends
to

\[
\frac r{\lambda^2}<1.
\]

Iteration sums a convergent geometric convolution of the
\(O(n^{-m-1})\) residual and kills the linearly bounded terminal error.
Therefore, for every finite \(m\),

\[
\boxed{
s_n=\lambda n+\sum_{j=0}^{m}\frac{c_j}{n^j}
     +O(n^{-m-1})
}.                                                       \tag{23}
\]

<code>AsymptoticCoefficients(termCount)</code> implements (20)--(22) with
truncated formal power series and exact quadratic-field arithmetic. It imposes
no mathematical maximum order; time and memory grow with the requested finite
order.

## 6. Arbitrary-degree rational tails

Rational-function tails can be recognized without choosing a
denominator-degree bound. Their coefficients lie in the characteristic field
`Q(lambda)`, which is either rational or real quadratic. If `A/B` is reduced
and `B` is monic of degree `m`, coprimality in the cleared Riccati equation
forces

\[
 A(n+1)=B(n)C(n),\qquad
 A(n)-(pn+q)B(n)=B(n+1)K(n),
\]

where `C` and `K` are linear and their product is the quadratic recurrence
numerator. Matching the known asymptote fixes

\[
 C(n)=\lambda n+\beta+(m+1)\lambda,
\quad
 K(n)=(\lambda-p)n+\beta-q-m(\lambda-p).
\]

Thus the constant coefficient gives a quadratic equation in `m`:

\[
 (\beta+(m+1)\lambda)(\beta-q-m(\lambda-p))=v.
\]

There are at most two non-negative integer candidates. Over a genuine
quadratic field, the surd component is linear in `m` because
`-lambda(lambda-p)=-r`; it therefore selects at most one candidate before the
rational component is checked. For each candidate, the remaining denominator identity is a finite exact linear system for monic
`B`. `PolynomialRationalTailCertificate` stores the resulting coefficients;
its verifier rechecks the identities and proves that `B` has no
positive-integer zero. It also checks that the constant term of `C` is
positive. If it were non-positive, the base constraint and factor identity
would force `B(0),...,B(m+1)` to alternate signs, impossible for degree `m`.
Eventual positivity then propagates backward through the recurrence. This
subsumes affine and linear-fractional tails and includes families of every
denominator degree over both coefficient fields.

### Period-reducible non-rational tails

If `u^2-4rv` is a square, the quadratic numerator factors over `Q` and the
continued fraction is equivalent to a degree-one holonomic recurrence. The
former `TryDegreeOneMinimalityReduction` recognizes the case where
`p^2+4r` is also square, giving the rational characteristic roots covered by
the 2026 minimality theorem.

`TryOnePeriodEqualityReduction` is strictly broader. When
`p(u-r)=2rq`, its transformed Gauss parameters are

\[
a=\frac{d+r}{2r},\qquad
b=N-1+\frac{u+d}{2r},\qquad
c=N+\frac{u-r}{2r}.
\]

They remain rational even if the characteristic roots are irrational; only
the hypergeometric argument becomes quadratic algebraic. Euler's integral is
therefore still a 1-period, so the effective relation algorithm decides an
exact integer-tail equality. The API also returns the least even shift making
both Euler endpoint exponents positive. It therefore returns a complete exact
reduction but does not reimplement the external 1-period algorithm. Its
hypergeometric target uses the directly derived prefactor `mu/alpha`; an
independent convergent-versus-series regression test detects an erroneous extra
power of `mu` in that normalization.

The alignment condition is also necessary for rational Gauss parameters in the
nonsquare-characteristic case. Writing `ell` for the other characteristic root,

\[
a=\frac{d+r}{2r}+\frac{p(u-r)-2rq}{2r(\ell-\lambda)}.
\]

Thus the residual in the second numerator is exactly the irrational component,
not a heuristic discovered only from examples.

## 7. Rational and real coefficients

The integer restriction belongs to the exact API, not to the analytic theorem.
For rational \(p,q,r,u,v\), choose a common positive denominator \(d\) and put
\(t_n=d\,s_n\). Then

\[
t_n=(dp)n+dq+
\frac{(d^2r)n^2+(d^2u)n+d^2v}{t_{n+1}}.                \tag{24}
\]

All five transformed coefficients are integers. Applying the theorem to
\(t_n\) and dividing by \(d\) gives existence, uniqueness, the interval, and
every asymptotic coefficient for the original rational family. In particular,
the slope, offset, interval radius, and all \(c_j\) scale by \(1/d\).

The existence, uniqueness, and asymptotic arguments also remain valid for real
coefficients satisfying (H); only the arbitrary-width exact representation and
finite integer certificate are lost.

## 8. Exact Beatty norm-gap certificates

The zero-linear/zero-constant numerator slice admits a stronger, search-free
certificate.  For

\[
s_n=pn+q+\frac{rn^2}{s_{n+1}},\qquad p,r\geq1,\quad -p\leq q\leq0,
\]

put (K=p^2+4r), (x_n=\lambda n+\beta), (c=\lambda+\beta), and

\[
C=\frac{rc^2}{\lambda^3},\qquad
\rho=r\bigl(q(p+q)-r\bigr),\qquad
G=K+\rho.
\]

`PolynomialExactBeattyTrap.TryCreate` checks the complete finite sufficient
criterion: positivity of (G), strict contraction, strict endpoint trapping,
and

\[
K\sqrt K\,C<G.
\]

When it succeeds, `PolynomialExactBeattyTrapCertificate` proves

\[
x_n<s_n<x_n+\frac Cn
\quad\hbox{and}\quad
\lfloor s_n\rfloor=\lfloor x_n\rfloor
\qquad(n\geq1).
\]

For an integer boundary (m>x_n), `NormWitness` returns

\[
Q=Kn+pq-2r,\qquad T=Km-r(p+2q)
\]

and the integer (F) in

\[
T^2-pTQ-rQ^2=KF,qquad F\equiv\rho\pmod K.
\]

The verifier compares all six coefficients of this bivariate identity, so the
check is universal rather than a sample of indices.  The full unit-numerator
strip (r=1,-p\leq q\leq0) and the scaled wedge
(q=-p,1\leq r\leq p) are named certificate families; the general inequality
recognizes additional parameter triples as well.  Unsupported or insufficient
cases return `false`, including the genuine floor counterexample
((p,q,r)=(1,0,3)).

The standalone constructor avoids building the more general asymptotic
interval analysis.  The measured exact sweep classifies 34,400 triples in
1.21 seconds (about 28,000 per second), while 100,000 arbitrary-width certified
floor evaluations take 0.10 seconds on the verification machine.

## Usage

~~~csharp
var analysis = PolynomialContinuedFractionTail.Analyze(
    linear: 3,
    constant: -1,
    numeratorQuadratic: 2,
    numeratorLinear: 4,
    numeratorConstant: 7);

QuadraticSurd lambda = analysis.Slope;
QuadraticSurd beta = analysis.Offset;
PolynomialTailIntervalCertificate certificate = analysis.IntervalCertificate;

var n = certificate.Cutoff;
var interval = analysis.CertifiedInterval(n);
var coefficients = analysis.AsymptoticCoefficients(termCount: 20);

if (PolynomialExactBeattyTrap.TryCreate(
    linear: 7,
    constant: -3,
    numeratorQuadratic: 1,
    numeratorLinear: 0,
    numeratorConstant: 0,
    certificate: out var beatty)) {
    BigInteger exactFloor = beatty.TailFloor(tailIndex: 1_000_000);
    PolynomialExactBeattyNormWitness witness = beatty.NormWitness(
        tailIndex: 1_000_000,
        boundary: exactFloor + 1);
}
~~~

The former metallic family is the specialization
\((p,q,r,u,v)=(k,-1,1,0,0)\). Its exact floor theorem remains a stronger
arithmetic result layered on top of the general analytic theory here.

## Verification status

The general theorem above is an exact written proof but has not yet been
transcribed into Lean. Its compactness lemma is the already formalized
trust-level-zero result linked in Section 1. The executable parts are checked
independently by:

~~~text
dotnet run -c Release tools/polynomial-continued-fraction-verifier.cs
dotnet run -c Release tools/maths-battery.cs
dotnet run tools/polynomial-tail-rational-verifier.cs -c Release
dotnet run tools/polynomial-tail-one-period-reduction-verifier.cs -c Release
dotnet run tools/polynomial-exact-beatty-trap-verifier.cs -c Release --no-restore
cd formal/PuckMathsFormal && lake build PuckMathsFormal.PolynomialTail
~~~

The dedicated verifier covers 29,040 accepted/rejected small-coefficient
families, checks every returned affine identity and finite certificate witness,
substitutes 10 exact formal terms in 1,191 distinct families, checks 32 terms in
the golden case, exercises a 256-bit \(r\gg p^2\) regime, and confirms exact
rational truncations enter the certified interval in four qualitatively
different regimes.
