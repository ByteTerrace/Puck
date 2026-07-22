# Arbitrarily delayed failure in the residual Lerch slice

## Result

The square-numerator, nonsquare-characteristic, nonaligned branch already
contains false integer candidates whose exact positive orbit can be made as
long as desired.  This strengthens the earlier paired-forcing obstruction in
one important respect: the examples below lie in the residual Lerch hard core

\[
 A_n=pn+q,\qquad B_n=r(n+1)^2,
\]

rather than in the square-characteristic or nonsquare-numerator strata.

For every integer \(N\geq2\), define rational numbers backwards by

\[
 e_N=0,\qquad
 e_n=\frac{(n+1)^2}{n+1+e_{n+1}}
 \quad(1\leq n<N).                                      \tag{1}
\]

Write the positive reduced fraction \(e_1=a_N/b_N\).  Set

\[
 \begin{aligned}
 (p,q,r,u,v)&=(b_N,0,b_N^2,2b_N^2,b_N^2),\\
 M_N&=a_N+b_N.
 \end{aligned}                                           \tag{2}
\]

Then \(M_N\) is an integer strictly above \(A_1=b_N\), but it is not
the positive infinite tail at index one.  More precisely, its cleared orbit

\[
 Q_0=1,\quad Q_1=M_N-A_1=a_N,\quad
 Q_{n+2}=b_N^2(n+2)^2Q_n-b_N(n+2)Q_{n+1}                 \tag{3}
\]

satisfies

\[
 \boxed{Q_0,Q_1,\ldots,Q_{N-1}>0,\qquad Q_N=0.}          \tag{4}
\]

Consequently, for every fixed inspection depth \(H\), taking \(N>H\)
gives a false integer proposal that passes every cleared-orbit positivity test
through depth \(H\).  This is an exact theorem, not a numerical limiting
claim.

The standalone checker
[`polynomial-tail-lerch-delayed-verifier.cs`](../tools/polynomial-tail-lerch-delayed-verifier.cs)
constructs the reduced fractions, verifies every invariant below, and checks
(4) with integer arithmetic.  It deliberately does not reference the main C#
project.

## The homogeneous finite-tail scaling lemma

The Lerch construction is a specialization of a general exact mechanism.
Let integer polynomials \(A_n=pn+q\) and
\(B_n=rn^2+un+v\) satisfy

\[
 A_{n+1}>0,\qquad B_n>0\qquad(n\geq1).                  \tag{S1}
\]

For a prescribed \(N\geq2\), form the finite backward tail

\[
 z_N=0,\qquad z_n=\frac{B_n}{A_{n+1}+z_{n+1}}
 \quad(1\leq n<N),                                     \tag{S2}
\]

and reduce \(z_1=a/b\) with \(a,b>0\).  Scale the recurrence by

\[
 A'_n=bA_n,qquad B'_n=b^2B_n                           \tag{S3}
\]

and propose the integer

\[
 M=bA_1+a.                                               \tag{S4}
\]

Writing \(Z_n=bz_n\), equation (S2) becomes

\[
 Z_{n+1}=\frac{B'_n}{Z_n}-A'_{n+1}.                     \tag{S5}
\]

Thus the cleared orbit started by \(M\) has consecutive ratios \(Z_n\),
is positive through index \(N-1\), and is exactly zero at index \(N\).
This proves:

\[
 \boxed{
 \text{Every admissible integral coefficient shape has integrally scaled
 false candidates with arbitrarily delayed orbit failure.}
 }                                                       \tag{S6}
\]

The scaling preserves all three invariants that define the residual strata:

\[
 \Delta'_c=b^2\Delta_c,\qquad
 \Delta'_B=b^4\Delta_B,\qquad
 R'=b^3R.                                                \tag{S7}
\]

It also preserves rational-function solvability, since \(s'_n=bs_n\) is a
bijective correspondence between solutions of the two Riccati recurrences.
Therefore a base shape with nonsquare \(\Delta_c\), square \(\Delta_B\),
nonzero \(R\), and no rational tail yields delayed candidates that retain all
four properties.  This is stronger than merely finding a long prefix in a
coefficient box; it explains why such prefixes are unavoidable under
unbounded integral scaling.

The Lerch family (1)--(4) is (S2)--(S4) for the fixed base shape

\[
 (p,q,r,u,v)=(1,0,1,2,1).                               \tag{S8}
\]

There is a complementary specialization on the quadratic-symmetry slice
\(b+g=1\).  Starting from

\[
 (p,q,r,u,v)=(1,1,1,1,0),qquad
 A_n=n+1,quad B_n=n(n+1),                              \tag{S9}
\]

define

\[
 z_N=0,qquad z_n=\frac{n(n+1)}{n+2+z_{n+1}},qquad
 z_1=\frac{a_N}{b_N}.                                  \tag{S10}
\]

Then

\[
 (p,q,r,u,v)=(b_N,b_N,b_N^2,b_N^2,0),qquad
 M_N=2b_N+a_N                                           \tag{S11}
\]

has the same delayed-zero property.  Here
\(\Delta_B=b_N^4\), \(\Delta_c=5b_N^2\), and
\(R=-2b_N^3\), so it lies in the square-numerator,
nonsquare-characteristic, nonaligned \(b+g=1\) slice.  It has no rational
tail: the factor \(C(n)=\lambda n+c_0\) would have to take its root from
\(n(n+1)\), forcing \(c_0\in\{0,\lambda\}\), while its positive offset
gives \(c_0=\beta+(m+1)\lambda>\lambda\).

## Proof of the delayed zero

Put \(E_n=b_Ne_n\).  Equation (1) rearranges to

\[
 E_{n+1}
 =\frac{b_N^2(n+1)^2}{E_n}-b_N(n+1).                    \tag{5}
\]

Because \(E_1=b_N(a_N/b_N)=a_N=Q_1/Q_0\), induction in
(3)--(5) gives

\[
 \frac{Q_n}{Q_{n-1}}=E_n\qquad(1\leq n<N).              \tag{6}
\]

All the backward values in (1) are strictly positive before the final zero,
so (6) proves \(Q_0,\ldots,Q_{N-1}>0\).  At the last step,
(5) gives

\[
 \frac{Q_N}{Q_{N-1}}=E_N=0,
\]

and hence \(Q_N=0\).  The exact integer-orbit criterion now excludes
\(M_N\): a true positive tail would require every \(Q_n\) to be strictly
positive.

The first values are

| \(N\) | \(b_N=p\) | \(a_N=M_N-p\) | \(M_N\) |
|---:|---:|---:|---:|
| 2 | 1 | 2 | 3 |
| 3 | 5 | 4 | 9 |
| 4 | 23 | 28 | 51 |
| 5 | 167 | 172 | 339 |
| 6 | 1387 | 1532 | 2919 |
| 7 | 14167 | 15212 | 29379 |
| 8 | 33149 | 36004 | 69153 |

These numbers are generated by (1); the table is illustrative and is not
used as proof.

## The examples really are in the residual hard stratum

All recurrence hypotheses hold: \(A_n=b_Nn>0\) and
\(B_n=b_N^2(n+1)^2>0\) at every positive index.  Their two discriminants are

\[
 \Delta_B=(2b_N^2)^2-4b_N^2b_N^2=0,
 \qquad
 \Delta_c=b_N^2+4b_N^2=5b_N^2.                          \tag{7}
\]

The first is a square and the second is not: a square equal to
\(5b_N^2\) would make \(5\) a rational square.  The alignment residual is

\[
 R=p(u-r)-2rq=b_N^3\ne0.                                \tag{8}
\]

Thus neither the rational-characteristic minimality theorem nor the aligned
one-period reduction applies.  For \(N\geq3\), the shifted recurrence has
the coefficient shape of the scaled-BDS wedge but parameter
\(r=p^2>p\), outside that theorem's hypothesis \(r\leq p\).

The newer general shifted norm-gap recognizer also fails here for every
\(N\geq3\), for a transparent exact reason.  Its native shifted parameters
are

\[
 (p,q,r)=(b_N,-b_N,b_N^2),
\]

so its characteristic modulus, norm residue, and required minimum positive
norm are

\[
 K=5b_N^2,qquad
 \eta=-b_N^4,qquad
 K+\eta=b_N^2(5-b_N^2).                                 \tag{8a}
\]

The verifier requires \(K+\eta>0\).  In fact \(b_N\geq5\) for every
\(N\geq3\).  The first three backward steps give

\[
 \frac45\leq e_1\leq\frac{28}{23}.
\]

Indeed \(e_2\leq3\) gives the lower bound, while
\(e_3\leq4\) and, after one more step, \(e_3<4\), give the upper bound
(the endpoint cases are \(N=3,4\)).  The value \(e_1=1\) would successively
force \(e_2=2\), \(e_3=3/2\), and \(e_4=20/3\), whereas (1) gives
\(e_4\leq5\); the shorter endpoint cases are checked directly.  The only
reduced rational in the displayed interval with denominator at most four is
1, so \(b_N\geq5\).  Hence (8a) is negative and the native `NormGap`
certificate does not silently cover any delayed member from \(N=3\) onward.

There is also no rational-function Riccati tail.  The dominant slope and
offset are

\[
 \lambda=b_N\frac{1+\sqrt5}{2},\qquad
 \beta=\frac{b_N}{\sqrt5}>0.                            \tag{9}
\]

If a reduced rational-function tail had denominator degree \(m\geq0\), the
rational-tail structure theorem would give

\[
 C(n)=\lambda n+c_0,\qquad
 c_0=\beta+(m+1)\lambda.                                \tag{10}
\]

But \(C\) must divide \(B_n=b_N^2(n+1)^2\).  Its only possible root is
therefore \(-1\), forcing \(c_0=\lambda\), whereas (9)--(10) give
\(c_0>\lambda\).  This contradiction places the whole family outside the
complete rational-tail recognizer.

## A simplification of the Lerch target

For the general Lerch slice

\[
 B_n=r(n+1)^2,qquad
 D=\sqrt{p^2+4r},\quad
 \lambda=\frac{p+D}{2},\quad
 \mu=\frac{D-p}{2},\quad x=-\frac\mu\lambda,
\]

the parameters in the connection-coordinate note reduce to

\[
 a=\frac{q+\mu}{D},\qquad 1-x=\frac D\lambda,qquad
 \lambda\mu=r.                                         \tag{11}
\]

Consequently the denominator in equation (L5) of that note is

\[
 \begin{aligned}
 a(1-x)+x(1-\lambda/M)
 &=\frac{q+\mu}{\lambda}
   -\frac\mu\lambda+\frac\mu M\\
 &=\frac{qM+r}{\lambda M}.
 \end{aligned}                                          \tag{12}
\]

Whenever \(M\ne0\), the residual equality target therefore has the simpler
exact form

\[
 \boxed{
 s_1=M\quad\Longleftrightarrow\quad
 \Phi(x,1,a)=\frac{M\lambda}{qM+r}.
 }                                                       \tag{13}
\]

If \(qM+r=0\), the finite Lerch value cannot meet the corresponding infinite
right-hand side, so the proposal is excluded directly.  Formula (13) removes
an accidental-looking combination of \(a,x,\lambda\) from the remaining
special-value problem.

## What this changes, and what it does not

The construction proves that arbitrary finite delay occurs even after all of
the following restrictions are imposed simultaneously:

1. the numerator discriminant is a square;
2. the characteristic discriminant is nonsquare;
3. the Gauss parameter is nonaligned;
4. the positive tail is not a rational function;
5. the numerator is the smallest reducible Lerch shape \(r(n+1)^2\).

It follows that the earlier fixed-depth orbit obstruction was not an artifact
of the nonsquare-numerator paired-forcing family.  No uniform fixed number of
cleared-orbit sign checks can decide even this single residual shape after
integral rescaling.  This statement is deliberately limited to orbit signs;
the construction alone does not establish how early a different exact
certificate might reject the same candidates.

This does **not** produce a positive integer equality and does not obstruct
the adaptive regularized Hausdorff semidecision: every member above is false,
and (4) is already its finite certificate.  The surviving positive-certifying
direction remains the same pointwise irrationality/minimality problem.  On
this scaled subfamily it is equivalent to asking whether the one fixed
normalized BDS/Lerch tail can be rational; the construction supplies
arbitrarily close rational finite tails, not a proof of irrationality.

## Verification

The checked command is

```powershell
dotnet run --property:NuGetAudit=false tools/polynomial-tail-lerch-delayed-verifier.cs -- 100
```

It verifies every failure index from 2 through 100.  The proof above is
uniform and has no upper bound; 100 is only the executable regression depth.
No repository build is required or used.
