# Integer hits in positive degree-(2,1) tails

## Resolution

The proposed integer-avoidance statement is **false**. There is an infinite family of admissible, positive,
non-affine tails that hit an integer.

For every integer `p >= 1`, define

\[
 a_n=pn,
 \qquad
 b_n=2(p+2)n^2+4(p+3)n,
\]

and

\[
 t_n=(p+2)n+2-\frac{2}{n+1}
     =\frac{n((p+2)n+p+4)}{n+1}.
 \tag{1}
\]

Both `a_n` and `b_n` are positive for every positive integer `n`, and `t_n>0`. Moreover,

\[
 t_n-a_n=\frac{2n(n+2)}{n+1},
 \qquad
 t_{n+1}=\frac{(n+1)((p+2)n+2p+6)}{n+2}.
\]

Consequently

\[
 (t_n-a_n)t_{n+1}
 =2n((p+2)n+2p+6)
 =b_n,
\]

so

\[
 t_n=a_n+\frac{b_n}{t_{n+1}}
 \tag{2}
\]

identically. The positive-tail uniqueness theorem therefore identifies `t_n` with the infinite continued-fraction
tail `s_n`.

The characteristic data are

\[
 r=2p+4,
 \qquad
 p^2+4r=(p+4)^2,
 \qquad
 \lambda=p+2,
 \qquad
 \beta=2.
\]

Thus the affine center is `(p+2)n+2`, but (1) differs from it by `-2/(n+1)`, so the tail is not exact affine. At the
first index,

\[
s_1=t_1=p+3\in\mathbb Z.
\]

Equivalently, its exact affine residual is

\[
 R=(q-\beta)(\lambda+\beta)+v=-2(p+4)\ne0.
\]

The smallest member is

\[
 (p,q,r,u,v)=(1,0,6,16,0),
 \qquad
 s_n=3n+2-\frac2{n+1},
 \qquad
 s_1=4.
\]

This is an exact counterexample to the claim that an integer hit forces an exact-affine tail.

## How the family was found

Starting from a proposed integer value for `s_1`, the Riccati recurrence can be iterated forward exactly:

\[
 y_{n+1}=\frac{b_n}{y_n-a_n}.
\]

If `y_1=s_1`, every iterate must remain strictly above `a_n`. Searching this necessary condition over 11,159,802
admissible tuples and 9,191,436 integer starts left five non-affine depth-2,000 survivors. Four were the first members
of the family above. Rational interpolation of their exact iterates produced (1), after which (2) supplied the finite
symbolic proof.

The search is evidence only; the displayed polynomial identity is the proof.

## Linear-fractional classification

The family above is one point in a class that can be recognized completely. Suppose the characteristic slope and
offset are rational and seek a non-affine solution

\[
 s_n=\lambda n+\beta+\frac{c}{n+d}.
 \tag{3}
\]

Put \(\mu=\lambda-p\) and \(\alpha=\beta-q\). Substitution into
\((s_n-pn-q)s_{n+1}=rn^2+un+v\) initially has possible poles at \(n=-d\) and \(n=-d-1\). Because \(c\ne0\), their
residues vanish if and only if

\[
 d(2\lambda-p)=2\beta+p-q,
 \qquad
 c=\lambda d-\lambda-\beta.
 \tag{4}
\]

After those cancellations, the product is exactly

\[
 \bigl(\mu n+\alpha-\mu\bigr)
 \bigl(\lambda n+2\lambda+\beta\bigr).
 \tag{5}
\]

Consequently (3) is a solution if and only if (4) holds and the three coefficients in (5) equal \((r,u,v)\). These
are finite rational identities. Strict positivity for every positive integer is also decidable without an unbounded
scan: after clearing denominators, the sign test compares a linear denominator with a convex quadratic numerator, so
only the finite sign-change endpoints and the integer points surrounding the quadratic vertex need inspection.
Positive-tail uniqueness then proves that the certified rational function is the infinite tail.

`TryLinearFractionalTailCertificate` constructs this certificate;
`VerifyLinearFractionalTailCertificate` checks it independently; and `TryCertifiedRationalTail` evaluates it. The
original counterexample has \((\lambda,\beta,c,d)=(p+2,2,-2,1)\).

The classification is machine-checked in Lean by
`PuckMathsFormal.PolynomialTail.LinearFractional.nonaffine_positive_integer_classification`. It proves both directions
directly for recurrence equality at every positive natural index: five samples force the degree-four cleared identity,
the two formal poles force (4), and three consecutive samples force the numerator coefficients. Its supporting
theorems certify the displayed counterexample family’s positivity, non-affineness, and integer hit at the first
positive index.

## Complete arbitrary-degree rational recognition

The linear-fractional ansatz is not the end of the rational branch. Let
`s=A/B` be any reduced rational-function solution, normalize `B` to be monic,
and put `m=deg B`. Coprimality in

\[
 (A-(pn+q)B)A(n+1)=(rn^2+un+v)B(n)B(n+1)
\]

forces two polynomial divisibilities. Consequently there are linear
polynomials

\[
 C(n)=\lambda n+c_0,
 \qquad
 K(n)=(\lambda-p)n+k_0
\]

such that

\[
 A(n+1)=B(n)C(n),
 \qquad
 A(n)-(pn+q)B(n)=B(n+1)K(n),
 \qquad
 rn^2+un+v=C(n)K(n).                                  \tag{6}
\]

Matching the affine asymptote gives

\[
 c_0=\beta+(m+1)\lambda,
 \qquad
 k_0=\beta-q-m(\lambda-p).                             \tag{7}
\]

The constant coefficient in (6) therefore forces

\[
 \boxed{(\beta+(m+1)\lambda)
 (\beta-q-m(\lambda-p))=v}.                            \tag{8}
\]

Equation (8) is quadratic in the non-negative integer `m`, so there are at
most two candidate denominator degrees. For each candidate, the remaining
identity

\[
 B(n-1)C(n-1)-(pn+q)B(n)=B(n+1)K(n)                  \tag{9}
\]

is a finite linear system for the lower coefficients of monic `B`. Strict
positivity needs no unbounded sign oracle: once the monic denominator is
eventually positive and has no positive-integer zero, the rational tail is
eventually positive; the positive Riccati recurrence transports positivity
backward through the finite prefix.

`TryRationalTailCertificate` implements (8)--(9) using exact Gaussian
elimination in `Q(lambda)`. When `lambda` is irrational, the surd component of
(8) is linear in `m` and selects the possible integer degree exactly.
`VerifyRationalTailCertificate` independently rechecks (6), (9), the necessary
`c0>0` condition, and the finite no-pole certificate. The exact checker exercises the infinite
family

\[
 (p,q,r,u,v)=(1,0,2,3m+2,0),\qquad m\ge0,
\]

whose rational tail has denominator degree exactly `m`; its acceptance run
checks every degree through 32. A separately discovered degree-two example is

\[
 (p,q,r,u,v)=(1,-1,2,7,0),\qquad
 s_n=\frac{6n^3+21n^2+13n-5}{3n^2+9n+5}.
\]

The certificate-to-recurrence theorem, degree equation, backward positivity,
and eventual-contraction uniqueness theorem are machine-checked in
`PuckMathsFormal.PolynomialTail.Rational`; its certificate layer is generic
over a field and therefore covers the quadratic coefficient case. A systematic
box sweep over 4,019,652 admissible tuples recognizes 1,777 rational-function
tails through denominator degree 11, including 532 with genuinely quadratic
coefficients and 362 beyond the linear-fractional API.

## Consequence for the uniform Beatty-shadow theorem

The finite-prefix strategy now handles every positive rational-function tail,
not only the displayed counterexample or the linear-fractional class. A
complete total decider would still need to decide integer equality for the
remaining non-rational hypergeometric tails. The general PCF equality problem
is not supplied by rational-solution recognition, so no such claim is made
here.
