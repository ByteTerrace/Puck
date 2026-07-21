# A proof of the Bosma–Dekking–Steiner metallic-mean conjecture

## Tweet

The BDS conjecture is now a theorem.

k,n≥1: K=k²+4, α=(k+√K)/2, xₙ=αn−(1+α)/√K,
C=(α−1)²/(Kα), P(z)=Kz²+(Kn−k−2)√K·z

sₙ=kn−1+n²/sₙ₊₁ ⟹ xₙ<sₙ<xₙ+C/n
m∈Z, m>xₙ ⟹ P(m−xₙ)=K(m²−kmn−n²+m+n)−k ≡ −k mod K, so ≥K−k>P(C/n) ⟹ m−xₙ>C/n

∴ ⌊sₙ⌋=⌊xₙ⌋

## Status and provenance

This note records a proof found with the `Puck.Maths` metallic-mean lens on
2026-07-20. The universal statement is formally verified by Lean 4.30.0 and
mathlib 4.30.0 as
[`PuckMathsFormal.BDS.bds_conjecture`](../formal/PuckMathsFormal/PuckMathsFormal/BDS/Theorem.lean).
The formalization contains no `sorry`, `admit`, `unsafe`, or custom axioms, and
the theorem passes Lean's trust-level-zero check. It has also been checked
against the exact verifier in
[`tools/bds-metallic-mean-verifier.cs`](../tools/bds-metallic-mean-verifier.cs).
It has not yet been externally peer reviewed; until that happens, cite it as a
formally verified proposed proof rather than as an accepted published theorem.

Bosma, Dekking, and Steiner introduced the conjecture in the last paragraph of
their 2018 paper [*A Remarkable Integer Sequence Related to pi and sqrt(2)*][bds].
Fokkink and Joshi restated it as Conjecture 20 in 2026 and wrote that a proof of
the BDS conjecture was still missing after proving a different, particular
golden-mean case; see Section 4 and Theorem 24 of [*On Cloitre's hiccup
sequences*][fj].

The Puck entry point is the exact incidence matrix

\[
M_k=\begin{pmatrix}k&1\\1&0\end{pmatrix}
\]

returned by `QuadraticInflation` for the period `[k]` beneath
`MetallicQuasicrystal`. Its Perron root and conjugate are exactly the two
quantities used below. The code was a lens for finding the invariant; the proof
itself is exact and does not depend on fixed-point evaluation.

## The conjecture

Fix an integer \(k\geq1\), and put

\[
 \alpha=\frac{k+\sqrt{k^2+4}}2,\qquad
 \lambda=\sqrt{k^2+4}=2\alpha-k.
\]

Let \((s_n)_{n\geq1}\) be the unique positive tail sequence satisfying

\[
 s_n=kn-1+\frac{n^2}{s_{n+1}}.
\]

The BDS conjecture is

\[
 \boxed{\left\lfloor s_n\right\rfloor=
 \left\lfloor\alpha n-\frac{1+\alpha}{2\alpha-k}\right\rfloor}
 \qquad(n\geq1).
\]

We prove it for every \(k\geq1\) and \(n\geq1\).

## 1. Trap the polynomial-continued-fraction tail

Write

\[
 \beta=-\frac{1+\alpha}{\lambda},\qquad
 x_n=\alpha n+\beta,\qquad
 c=\alpha+\beta,\qquad
 C=\frac{c^2}{\alpha^3}.
\]

Because \(\alpha^2=k\alpha+1\) and
\(\lambda=(\alpha^2+1)/\alpha\),

\[
 c=\frac{\alpha^2(\alpha-1)}{\alpha^2+1}>0.
\]

Define

\[
 T_n(y)=kn-1+n^2/y,\qquad J_n=[x_n,x_n+C/n].
\]

Since \(x_n=\alpha(n-1)+c>0\), every \(J_n\) is positive. We claim that

\[
 T_n(J_{n+1})\subset\operatorname{int}J_n.                    \tag{1}
\]

The map is decreasing. At the lower endpoint, direct simplification gives

\[
 T_n(x_{n+1})-x_n
 =\frac{c^2}{\alpha^2(\alpha n+c)}
 <\frac{c^2}{\alpha^3n}=\frac Cn.                            \tag{2}
\]

For \(h=C/(n+1)\), another direct simplification gives

\[
 T_n(x_{n+1}+h)>x_n
 \quad\Longleftrightarrow\quad
 h(\alpha n-c)<c^2.                                          \tag{3}
\]

If \(\alpha n-c\leq0\), this is immediate. Otherwise it follows from

\[
 h(\alpha n-c)<\frac{c^2\alpha n}{\alpha^3(n+1)}<c^2.
\]

Equations (2) and (3), together with monotonicity, prove (1).

For completeness, identify this trapping construction with the infinite
continued fraction as follows. At depth \(N\), start with
\(r^{(N)}_{N+1}=x_{N+1}\) and recurse backwards by
\(r^{(N)}_n=T_n(r^{(N)}_{n+1})\). Equation (1) puts every fixed coordinate
\(r^{(N)}_n\), once \(N\geq n\), in \(J_n\). A diagonal convergent
subsequence yields a positive infinite solution \(r\) of the recurrence.

Here uniqueness can be proved directly, without importing the corresponding
result from BDS. Any positive solution \(s\) satisfies

\[
 s_{j+1}>kj,
 \qquad
 s_j<(k+1)j.
\]

The trapped solution satisfies \(r_{j+1}>\alpha j\). Subtracting one recurrence
step exactly therefore gives

\[
 |s_j-r_j|
 =\frac{j^2}{s_{j+1}r_{j+1}}|s_{j+1}-r_{j+1}|
 <\rho |s_{j+1}-r_{j+1}|,
 \qquad \rho=\frac1{k\alpha}<1.
\]

At a terminal index \(M\), both values have a common linear bound, for example
\(|s_M-r_M|\leq2(k+1)M\). Iterating from fixed \(n\) to \(M\) and sending
\(M\to\infty\) gives \(s_n=r_n\), because \(M\rho^{M-n}\to0\). Thus every
positive solution is the trapped solution. Applying (1) once more to its
recurrence makes the inequalities strict:

\[
 \boxed{x_n<s_n<x_n+\frac Cn}.                               \tag{4}
\]

## 2. The next integer is outside the trap

Put

\[
 K=k^2+4=\lambda^2.
\]

The offset has the useful quadratic-ring form

\[
 \beta=\frac{k-2-(k+2)\alpha}{K},
\]

and hence

\[
 x_n=\frac{q\alpha+k-2}{K},\qquad q=Kn-k-2>0.                \tag{5}
\]

Let \(m\) be any integer strictly above \(x_n\), let \(g=m-x_n>0\), and set

\[
 t=Km-k+2.
\]

Since the conjugate of \(\alpha\) is
\(\alpha'=k-\alpha=-1/\alpha\), equation (5) gives

\[
 Kg=t-q\alpha,
 \qquad
 t-q\alpha'=Kg+q\lambda.
\]

Taking the quadratic norm produces

\[
 (Kg)(Kg+q\lambda)
 =t^2-ktq-q^2
 =K F,                                                       \tag{6}
\]

where

\[
 F=K(m^2-kmn-n^2+m+n)-k.
\]

Both factors on the left of (6) are positive, so \(F\) is a positive integer.
Moreover \(F\equiv-k\pmod K\), and \(0<k<K\), whence

\[
 F\geq K-k.                                                  \tag{7}
\]

After dividing (6) by \(K\), the gap \(g\) is the positive solution of

\[
 P(g)=F,\qquad P(z)=Kz^2+q\lambda z.                         \tag{8}
\]

The function \(P\) is strictly increasing for \(z>0\). We compare it at the
width of the trap. Since \(0<c<\alpha\), we have \(0<C<1\), while
\(\lambda<k+2\). Therefore

\[
\begin{aligned}
 P(C/n)
 &=K\lambda C-\frac{(k+2)\lambda C}{n}
   +\frac{KC^2}{n^2}\\
 &<K\lambda C.                                               \tag{9}
\end{aligned}
\]

Indeed, the last two terms have negative sum because
\(K=\lambda^2\) and \(\lambda C<k+2\leq(k+2)n\).

Finally, using
\(c=\alpha(\alpha-1)/\lambda\),

\[
 K\lambda C=\frac{\lambda(\alpha-1)^2}{\alpha}<K-k,          \tag{10}
\]

because the right side minus the left side simplifies exactly to

\[
 \alpha+\frac3\alpha>0.
\]

Equations (7)--(10) give

\[
 P(C/n)<K-k\leq F=P(g).
\]

Strict monotonicity of \(P\) yields

\[
 \boxed{m-x_n=g>C/n}.                                       \tag{11}
\]

In particular, take \(m=\lceil x_n\rceil\). The number \(x_n\) is irrational,
so this is the first integer above it. Equations (4) and (11) say that no integer
lies between \(x_n\) and \(s_n\). Their floors are equal, proving the conjecture.

## Exact computational cross-check

The verifier performs three independent checks:

1. `QuadraticInflation` recovers \(M_k\), determinant \(-1\), trace \(k\), and
   discriminant \(k^2+4\) from the same quadratic irrational.
2. The Beatty term is floored exactly in \(\mathbb Q(\sqrt{k^2+4})\), without
   `double` or Puck's fixed-point seam.
3. Consecutive exact rational truncations of the generalized continued fraction
   bracket its infinite tail. Both truncations have the conjectured floor for a
   configurable grid of \((k,n)\).

Run it with:

```text
dotnet run -c Release tools/bds-metallic-mean-verifier.cs
```

The finite cross-check is not the proof of the universal quantifiers; equations
(1)--(11) are. Its role is to catch transcription, indexing, and sign mistakes.

## Puck.Maths API

The theorem is available to consumers as
[`MetallicPolynomialContinuedFraction.TailFloor`](../src/Puck.Maths/MetallicPolynomialContinuedFraction.cs). The method
returns the exact floor of the infinite tail without constructing a truncation:

```csharp
var floor = MetallicPolynomialContinuedFraction.TailFloor(metallicIndex: 2, tailIndex: 1000);
var next = MetallicPolynomialContinuedFraction.TailFloor(metallicIndex: 2, tailIndex: 1001);
var hiccupStep = next - floor;
```

An overload accepting `BigInteger` inputs preserves the theorem's unbounded
integer domain. The standalone verifier compares the public evaluator both with
its independent exact quadratic-surd transcription and with consecutive exact
rational truncations.

The recurrence's analytic structure is also available through
`MetallicPolynomialContinuedFraction.Analyze(k)`. It is the
`(p,q,r,u,v)=(k,-1,1,0,0)` specialization of the general
[`PolynomialContinuedFractionTail`](polynomial-continued-fraction-tails.md)
existence/uniqueness theorem, certified `H/n` interval, and arbitrary-order
asymptotic expansion. The exact floor theorem on this page is the additional
arithmetic statement special to the metallic subfamily.

## Formal verification

The pinned Lean project is in [`formal/PuckMathsFormal`](../formal/PuckMathsFormal).
Its public theorem assumes only positivity and the displayed recurrence; it
derives the formula for every \(k,n\geq1\). The compactness construction,
division-continuity cutoff, uniqueness contraction, quadratic norm, congruence
gap, and final floor step are all formalized locally.

```text
cd formal/PuckMathsFormal
lake exe cache get
lake build
```

Lean reports that `bds_conjecture` uses only the standard mathlib logical
axioms `propext`, `Classical.choice`, and `Quot.sound`; in particular it does
not depend on `sorryAx` or any project-defined axiom.

[bds]: https://arxiv.org/abs/1710.01498
[fj]: https://doi.org/10.1007/s11139-025-01305-1
