# The Hasse kernel in the self-parameterized Lerch core

## Result

Consider the repeated-root Lerch tail

\[
 s_n=pn+\frac{r(n+1)^2}{s_{n+1}},
 \qquad p,r\in\mathbf Z_{>0},                         \tag{1}
\]

and its cleared orbit

\[
 Q_0=1,\qquad Q_1=h=s_1-p,
 \qquad Q_{n+2}=r(n+2)^2Q_n-p(n+2)Q_{n+1}.           \tag{2}
\]

Put

\[
 D^2=p^2+4r,\qquad
 \lambda=\frac{p+D}{2},\qquad
 \mu=\frac{D-p}{2},\qquad
 a=\frac\mu D,\qquad x=-\frac\mu\lambda.            \tag{3}
\]

Let \(\ell\) be an odd good prime for which \(D^2\) is a quadratic
nonresidue, and work in \(\mathbf F_{\ell^2}=\mathbf F_\ell(D)\).  Define
the finite Lerch sum and its contiguous quotient by

\[
 S_\ell=\sum_{j=0}^{\ell-2}\frac{x^j}{a+j},
 \qquad
 \mathcal L_\ell=aS_\ell+\frac1x.                    \tag{4}
\]

Then the unique affine Hasse-kernel tail is

\[
 \boxed{M_\ell=\mu\mathcal L_\ell\in\mathbf F_\ell,
 \qquad h_\ell=M_\ell-p.}                            \tag{5}
\]

Equivalently, a fixed rational tail \(M\), with its denominator excluded
from the good primes, lies on the Hasse kernel exactly when

\[
 \boxed{\mathcal L_\ell=\frac M\mu.}                 \tag{6}
\]

Thus the horizontal nonconcentration problem in this infinite family is
literally a varying-prime special-value problem for one finite-field Lerch
quotient.  Formula (5) replaces both the length-\(\ell\) transfer product
and the degree-\(\ell-2\) Jacobi logarithmic derivative.

## Frobenius law

Inertness and (3) give

\[
 D^\ell=-D,\qquad a^\ell=1-a,\qquad x^\ell=x^{-1},
 \qquad x^{\ell+1}=1.                                 \tag{7}
\]

Reverse the summation index in \(S_\ell^\ell\).  If
\(i=\ell-2-j\), then

\[
\begin{aligned}
 S_\ell^\ell
 &=-x^3\sum_{i=0}^{\ell-2}\frac{x^i}{a+i+1}\\
 &=-x^2S_\ell+\frac{x^2}{a}-\frac1{a-1}\\
 &=-x^2S_\ell+(1-x)^2.                               \tag{8}
\end{aligned}
\]

The last equality uses \(a=x/(x-1)\), the self-parameter relation peculiar
to \(q=0\).  Consequently

\[
 \boxed{\mathcal L_\ell^\ell=x\mathcal L_\ell.}     \tag{9}
\]

Since \(\mu^\ell=-\lambda\) and \(x=-\mu/\lambda\), equation (9) proves

\[
 (\mu\mathcal L_\ell)^\ell=\mu\mathcal L_\ell,
\]

which explains the descent in (5) without appealing to the transfer
matrix.

## Derivation from the Hasse polynomial

For this repeated root, the polynomial from the Hasse/Jacobi construction is

\[
 H_\ell(t)={}_2F_1(2,2;a+2;t)_{\leq\ell-2}.          \tag{10}
\]

Write \(m=\ell-2\) and \(X=t/(t-1)\).  In characteristic \(\ell\), the
first numerator parameter is also \(-m\), so Pfaff's terminating identity
gives

\[
 H_\ell(t)=(1-t)^m
 {}_2F_1(-m,a;a+2;X).                                 \tag{11}
\]

The coefficient of \(X^j\) in the last polynomial is

\[
 a(a+1)\left(\frac{1-a}{j+a}+\frac a{j+a+1}\right).
                                                               \tag{12}
\]

At \(t=a\), one has \(X=x=a/(a-1)\).  The coefficient of
\(S_\ell\) in (12) cancels, leaving the prime-independent value

\[
 \boxed{H_\ell(a)=\frac{1+a}{1-a}.}                  \tag{13}
\]

Differentiating (11)--(12) before making the substitution gives

\[
 \frac{H_\ell'(a)}{H_\ell(a)}
 =\frac{1+aS_\ell}{1-a}-\frac2a.                    \tag{14}
\]

The affine kernel formula from the Hasse note is
\(h_\ell=(r/D)H_\ell'(a)/H_\ell(a)\).  Using
\(1-a=\lambda/D\), \(r=\lambda\mu\), and
\(1/x=-\lambda/\mu\), equation (14) becomes

\[
 h_\ell=\mu(1+aS_\ell)-2\lambda,
 \qquad
 p+h_\ell=\mu\left(aS_\ell+\frac1x\right),
\]

which is (5).

## What this changes

The characteristic-zero value on the same projective line is

\[
 s_1=\mu\Phi(x,1,a).
\]

Equations (4)--(6) are its exact characteristic-\(\ell\) Hasse analogue.
They show that the remaining modular question is not hidden in a matrix
product: it is concentration of the explicitly displayed diagonal finite
Lerch values.

They do **not** prove the required nonconcentration.  The summation length,
the quadratic parameter, and the Frobenius relation all change with
\(\ell\).  Hence these values are not Frobenius traces of a fixed finite
extension to which ordinary Chebotarev applies.  Standard \(p\)-curvature
theorems likewise ask for a full horizontal basis (or, in rank one, zero
curvature at almost every prime); here only one projective line is selected
on the inert half of the rational primes.  Proving that (6) fails on a set
of divergent Mertens weight remains a new finite-field Lerch
nonconcentration theorem.

This sharpens, rather than closes, the total Beatty-shadow boundary.  It
also explains why the prime-independent image-line theorem and the varying
regular line behave differently: the Hasse value (13) is fixed, while its
parameter derivative (14) retains all of the cross-prime arithmetic.

## Verification

The standalone exact checker
[`polynomial-tail-hasse-lerch-quotient-verifier.cs`](../tools/polynomial-tail-hasse-lerch-quotient-verifier.cs)
verifies (5), (8), (9), (13), and (14) independently against both the
original recurrence kernel and the direct hypergeometric derivative.  Its
default box covers 3,525 good inert instances across 128 parameter pairs:

```powershell
dotnet run --property:NuGetAudit=false tools/polynomial-tail-hasse-lerch-quotient-verifier.cs
```

This source-file check does not build the repository.
