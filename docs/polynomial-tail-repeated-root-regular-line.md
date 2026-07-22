# The repeated-root regular line is exactly the Lerch core

## Scope

This note closes a bookkeeping gap left by
[`polynomial-tail-hasse-kernel-line.md`](polynomial-tail-hasse-kernel-line.md).
For every repeated-root numerator, that note identifies the sole possible
positive/minimal line as

\[
 H_k(z)={}_2F_1(k+1,k+1;C;z).
\]

At first sight, allowing an arbitrary repeated-root shift \(k\) appears to
produce a new family of hypergeometric logarithmic derivatives.  It does not.
Integer contiguity and an exact Riccati reindexing reduce every one of them to
the same Lerch value already isolated in
[`polynomial-tail-lerch-arithmetic.md`](polynomial-tail-lerch-arithmetic.md).
The reduction is projectively invertible, so it preserves rationality in both
directions.  As consequences, this note also gives:

1. an elementary transcendence proof for the complete aligned repeated-root
   regular line, including nonsquare characteristic discriminant; and
2. a complete rational-versus-transcendental classification when the
   characteristic discriminant is a square, including the negative-integer
   resonances introduced by a nonzero repeated-root shift.

The remaining nonsquare, nonaligned question is thereby reduced to one exact
projective Lerch-value statement.  It is not solved here.

## 1. The regular Gauss seed

Let

\[
 s_n=pn+q+\frac{r(n+k)^2}{s_{n+1}},
 \qquad p,r\in\mathbf Z_{>0},\quad k\in\mathbf Z_{\geq0},
 \quad q\in\mathbf Z,\quad p+q\geq0.                 \tag{1}
\]

Put

\[
 D=\sqrt{p^2+4r},\qquad
 \lambda=\frac{p+D}{2},\qquad
 \mu=\frac{D-p}{2},\qquad
 t=\frac\mu\lambda,\qquad y=\frac\mu D.             \tag{2}
\]

Thus \(\lambda\mu=r\), \(D=\lambda+\mu\),
\(0<t<1\), and \(y=t/(1+t)\).  The parameters used in the Hasse note are

\[
 T=p(2k-1)-2q,\qquad
 C=k+\frac32-\frac{T}{2D}.                            \tag{3}
\]

The regular solution and its affine EGF seed are

\[
 H_{\rm reg}(z)={}_2F_1(k+1,k+1;C;z),                \tag{4}
\]

\[
 h_{\rm reg}=\frac rD\frac{(k+1)^2}{C}
 \frac{{}_2F_1(k+2,k+2;C+1;y)}
      {{}_2F_1(k+1,k+1;C;y)}.                         \tag{5}
\]

All series in (4)--(5) are positive at \(y\): the admissibility condition in
(1) implies \(C>1\).  The positive tail at the first original index is

\[
 s_1=p+q+h_{\rm reg}.                                \tag{6}
\]

## 2. Exact reindexing

Set

\[
 q_0=q-p(k-1),\qquad S_j=s_{j-k+1}.                  \tag{7}
\]

Wherever both sides are in the original range, direct substitution in (1)
gives

\[
 \boxed{
 S_j=pj+q_0+\frac{r(j+1)^2}{S_{j+1}}.
 }                                                     \tag{8}
\]

In particular, \(S_k=s_1\).  For \(k=0\), this reads \(S_0=s_1\).
Equation (8) also defines the unique projective continuation to the finitely
many preceding indices.  No positivity assumption at those artificial
indices is needed.

The two quantities in (3) simplify under (7):

\[
 T=p-2q_0,\qquad
 a:=C-k-1=\frac12-\frac{p-2q_0}{2D}
              =\frac{\mu+q_0}{D}.                    \tag{9}
\]

Thus the nonintegral part of the Gauss parameter is completely independent
of \(k\).  Pfaff's identity makes the same fact visible analytically:

\[
 H_{\rm reg}(y)
 =(1+t)^{k+1}
 {}_2F_1(k+1,a;k+1+a;-t).                            \tag{10}
\]

The right side differs from the \(k=0\) Lerch solution only by integer
contiguity.

## 3. The projectively invertible Lerch reduction

Assume first that

\[
 a\notin\{0,-1,-2,\ldots\},
\]

and put

\[
 L=\Phi(-t,1,a)=\sum_{n\geq0}\frac{(-t)^n}{n+a}.
                                                               \tag{11}
\]

The standard Lerch calculation for (8) at index one gives the exact
projective identity

\[
 \boxed{
 S_1=\frac{rL}{\lambda-q_0L},
 \qquad
 \frac L\lambda=\frac{S_1}{q_0S_1+r}.
 }                                                     \tag{12}
\]

The formulas include the value at infinity in \(\mathbf P^1\).  Every
subsequent Riccati step is another rational projective automorphism:

\[
 S_{j+1}=\frac{r(j+1)^2}{S_j-(pj+q_0)},qquad
 \mathcal R_j=
 \begin{pmatrix}
 0&r(j+1)^2\\
 1&-(pj+q_0)
 \end{pmatrix},                                      \tag{13}
\]

with \(\det\mathcal R_j=-r(j+1)^2\ne0\).  Hence, for
\(k\geq1\),

\[
 S_k=(\mathcal R_{k-1}\cdots\mathcal R_1)\cdot S_1. \tag{14}
\]

For \(k=0\), the inverse first step is

\[
 S_0=q_0+\frac r{S_1}.                                \tag{15}
\]

Writing \(U=L/\lambda\) makes the rational projective structure completely
explicit.  The first map has matrix

\[
 \mathcal B=\begin{pmatrix}r&0\\-q_0&1\end{pmatrix},
 \qquad S_1=\mathcal B\cdot U.
\]

For \(k\geq1\), subtraction of the original first base term gives

\[
 h_{\rm reg}=\mathcal M_k\cdot U,qquad
 \mathcal M_k=
 \begin{pmatrix}1&-(pk+q_0)\\0&1\end{pmatrix}
 \mathcal R_{k-1}\cdots\mathcal R_1\mathcal B,       \tag{15a}
\]

and

\[
 \det\mathcal M_k=(-1)^{k-1}r^k(k!)^2\ne0.           \tag{15b}
\]

For \(k=0\), the same statement is

\[
 h_{\rm reg}=\frac1U-q_0,qquad
 \mathcal M_0=\begin{pmatrix}-q_0&1\\1&0\end{pmatrix},
 \qquad\det\mathcal M_0=-1.                          \tag{15c}
\]

Equations (12)--(15c) are all projective maps over \(\mathbf Q\) with
nonzero determinant.  Since the actual regular value \(S_k=s_1\) is finite,
(6) now gives the central equivalence

\[
 \boxed{
 h_{\rm reg}\in\mathbf Q
 \quad\Longleftrightarrow\quad
 S_k\in\mathbf Q
 \quad\Longleftrightarrow\quad
 \frac1\lambda\Phi\!\left(
   -\frac\mu\lambda,1,\frac{\mu+q_0}{D}
 \right)\in\mathbf Q.
 }                                                     \tag{16}
\]

This is an equivalence, not only a necessary condition.  Apparent zeros or
poles at an artificial preceding index cause no exception: (13) acts on
\(\mathbf P^1(\mathbf Q)\), and every matrix has nonzero determinant.

For an individual proposed rational value \(S_1=M\), (12) is the target

\[
 L=\frac{\lambda M}{q_0M+r},                          \tag{17}
\]

with the zero denominator interpreted projectively.  Backward application
of (8) converts any proposed rational \(S_k\) into exactly one such
projective rational \(M\).  Thus the general repeated-root regular line has
not created a new special-value problem: it is the Lerch hard core, viewed
at a later Riccati index.

There is also a pole-free positive-parameter formulation.  Because
\(C>1\), the shifted parameter \(A=a+k=C-1\) is positive, and

\[
 \Phi(-t,1,a)
 =\sum_{j=0}^{k-1}\frac{(-t)^j}{a+j}
   +(-t)^k\Phi(-t,1,a+k).                             \tag{18}
\]

Away from the displayed nonpositive-integer resonances, composing (18) with
(12)--(15) is again a nonconstant projective map.  Its determinant is, up to
the harmless final translation by \(p+q\),

\[
 -r^k(k!)^2\lambda t^k\ne0\qquad(k\geq1).             \tag{19}
\]

Thus a rational regular seed would make the convergent Euler integral
\(\Phi(-t,1,C-1)\) algebraic as well.

## 4. The aligned line is transcendental

The aligned condition is

\[
 T=0
 \quad\Longleftrightarrow\quad
 q_0=\frac p2
 \quad\Longleftrightarrow\quad
 a=\frac12.                                           \tag{20}
\]

Here

\[
 L=\Phi(-t,1,1/2)
   =\frac{2\arctan\sqrt t}{\sqrt t}.                 \tag{21}
\]

The number \(\arctan\sqrt t\) is transcendental.  Indeed, if it were a
nonzero algebraic number \(\theta\), then Lindemann--Weierstrass would make
\(e^{2i\theta}\) transcendental, whereas

\[
 e^{2i\theta}=\frac{1+i\sqrt t}{1-i\sqrt t}
\]

is algebraic.  Consequently \(L\), and hence \(L/\lambda\), is
transcendental.  Equation (16) proves

\[
 \boxed{
 D\notin\mathbf Q,\ T=0
 \quad\Longrightarrow\quad
 h_{\rm reg}\text{ and }s_1\text{ are transcendental}.
 }                                                     \tag{22}
\]

This gives a direct theorem, rather than only an appeal to the general
effective 1-period decision procedure, for the whole aligned repeated-root
line.

## 5. Complete square-characteristic audit

Suppose \(D\in\mathbf Q\).  Since \(p,r\) are integers, \(D\) is an
integer, so \(t,\lambda\), and \(a\) are rational.

If \(a\) is not a nonpositive integer, the rational-parameter Lerch value in
(11) is transcendental.  One shifts \(a\) into a positive rational interval
using (18), writes its Euler integral as an integral of a proper rational
function after a finite root substitution, and applies Baker's theorem to
the resulting nonzero algebraic linear form in logarithms.  The nonconstant
projective reduction above then makes \(h_{\rm reg}\) transcendental.

It remains to inspect \(a=-j\), \(j\geq0\).  Since \(C=k+1+a>1\), only

\[
 0\leq j\leq k-1                                      \tag{23}
\]

can occur.  Euler's transformation terminates exactly:

\[
 {}_2F_1(k+1,k+1;k+1-j;y)
 =(1-y)^{-k-1-j}
 {}_2F_1(-j,-j;k+1-j;y).                              \tag{24}
\]

The last factor is a degree-\(j\) polynomial over \(\mathbf Q\), so (5) is
rational.  These are precisely rational-function resonances, not new
minimal special values.  We obtain the complete classification

\[
 \boxed{
 D\in\mathbf Z:\quad
 h_{\rm reg}\in\mathbf Q
 \Longleftrightarrow
 a\in\{0,-1,\ldots,1-k\};
 \quad
 \text{otherwise }h_{\rm reg}\text{ is transcendental}.
 }                                                     \tag{25}
\]

The set on the right is empty when \(k=0\).  For \(k=1\), its sole member
\(a=0\) is the familiar exact-affine resonance.  The additional members for
larger \(k\) are its finite contiguous rational-function descendants.

## 6. The exact remaining theorem

When \(D\notin\mathbf Q\), (9) shows that \(a\) is rational only on the
aligned line already disposed of by (22).  Every genuinely open
repeated-root instance therefore has

\[
 D\notin\mathbf Q,\qquad p-2q_0\ne0,\qquad
 a=\frac{\mu+q_0}{D}\in\mathbf Q(D)\setminus\mathbf Q. \tag{26}
\]

The missing assertion is exactly

\[
 \boxed{
 \frac1\lambda
 \Phi\!\left(-\frac\mu\lambda,1,\frac{\mu+q_0}{D}\right)
 \notin\mathbf Q
 }
                                                               \tag{27}
\]

for integers \(p,r>0,q_0\), with \(D=\sqrt{p^2+4r}\notin\mathbf Q\),
apart from the aligned value \(2q_0=p\).  Conversely, any counterexample to
(27) gives a rational regular seed after every sufficiently large shift
\(k\), and homogeneity then gives an integral representative with an integer
tail hit.  Thus (27) is equivalent to the full repeated-root projective
rationality problem, not merely implied by it.

When \(a>0\), (27) can also be written

\[
 \frac{t^{-a}B_y(a,1-a)}{\lambda}\notin\mathbf Q,
 \qquad y=\frac{t}{1+t}.                              \tag{28}
\]

Gelfond--Schneider proves that \(t^a\) is transcendental but does not prove
(28); a hypothetical rational value in (28) would make the incomplete beta
value a nonzero algebraic multiple of that same transcendental power.  The
reciprocal connection formula and Pad\'e denominator obstruction in the
Lerch arithmetic note therefore apply unchanged.  The real gain here is a
classification of the apparent extra \(k\)-family: all of it is one and the
same unresolved value problem.

## Verification

The standalone exact verifier checks the matrices in (13)--(15c), their
closed determinant, and the terminating formula (24) against the original
cleared recurrence:

    dotnet run --property:NuGetAudit=false tools/polynomial-tail-repeated-root-regular-verifier.cs

The default box verifies 138,600 projective matrices, 4,851,000 exact
projective evaluations, 8,813 admissible square-characteristic resonances,
and 132,195 exact recurrence coefficients.  It is independent of the main
project build.
