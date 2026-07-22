# The exceptional inert-prime Hasse kernel line

This note makes the state-specific condition in
[`polynomial-tail-factorial-density-obstruction.md`](polynomial-tail-factorial-density-obstruction.md)
explicit.  It also proves a useful uniqueness theorem for any seed that could
evade the factorial-density obstruction.  It does **not** prove the required
varying-prime nonconcentration theorem.

Consider

\[
 Q_0=1,\qquad Q_1=h,\qquad
 Q_{n+2}=B(n+1)Q_n-A(n+2)Q_{n+1},                     \tag{1}
\]

where

\[
 A(n)=pn+q,\qquad B(n)=rn^2+un+v.
\]

Put

\[
 \Delta_c=p^2+4r,\qquad \Delta_B=u^2-4rv,\qquad
 R=p(u-r)-2rq.                                         \tag{2}
\]

Throughout Sections 1--4, assume that \(\Delta_B\) is an integer square
(possibly zero), and let \(\ell\) be an odd prime avoiding the denominators
and the factors \(2r\Delta_cR\) (and \(\Delta_B\) when it is nonzero).
Suppose that
\((\Delta_c/\ell)=-1\).  These are exactly the good inert primes in the
square-numerator, nonsquare-characteristic, nonaligned branch.

## 1. Hypergeometric normal form over the quadratic residue field

Choose \(D\in\mathbf F_{\ell^2}\) with \(D^2=\Delta_c\).  Inertness gives
\(D^\ell=-D\).  Let \(\rho_1,\rho_2\in\mathbf F_\ell\) be the two roots of
\(B(n)\), counted with multiplicity, and define

\[
 A_0=1-\rho_1,\qquad B_0=1-\rho_2,
\]

\[
 C=\frac{A_0+B_0+1}{2}-\frac{R}{2rD},\qquad
 t=\frac{2rz-p+D}{2D}.                                 \tag{3}
\]

The point \(z=0\) is

\[
 t_0=\frac{D-p}{2D},\qquad t_0^\ell=1-t_0,             \tag{4}
\]

and direct substitution gives

\[
 C^\ell=A_0+B_0+1-C.                                   \tag{5}
\]

For the truncated EGF

\[
 F_\ell(z)=\sum_{n=0}^{\ell-1}\frac{Q_n}{n!}z^n,
\]

the differential equation

\[
 (1+pz-rz^2)F''=((3r+u)z-(2p+q))F'+(r+u+v)F           \tag{6}
\]

becomes the Gauss equation

\[
 t(1-t)H''+[C-(A_0+B_0+1)t]H'-A_0B_0H=0.              \tag{7}
\]

The identities behind (7) are

\[
 A_0+B_0=2+\frac ur,qquad
 A_0B_0=1+\frac{u+v}{r}.
\]

Thus the finite-field kernel condition is literally a Hasse-polynomial
condition for the same Gauss equation that occurs in the characteristic-zero
minimality reduction.

## 2. The explicit polynomial and Jacobi formula

Let \(m_A,m_B\in\{0,\ldots,\ell-1\}\) be defined by

\[
 m_A\equiv-A_0\pmod\ell,\qquad
 m_B\equiv-B_0\pmod\ell,
\]

and put \(m=\min(m_A,m_B)\).  Since \(R\ne0\) and \(D\notin\mathbf F_\ell\),
we have \(C\notin\mathbf F_\ell\).  In particular, none of
\(C,C+1,\ldots,C+\ell-1\) vanishes.  The unique nonzero polynomial solution
of (7), up to scale, is therefore

\[
 H_\ell(t)=
 \sum_{j=0}^{m}
   \frac{(A_0)_j(B_0)_j}{(C)_j\,j!}t^j.                \tag{8}
\]

Indeed, the coefficient recursion is nonsingular until one of the two
numerator Pochhammer symbols first vanishes.  Conversely, any nonzero
polynomial solution has nonzero constant term: if its first nonzero term had
degree \(j>0\), the lowest coefficient of (7) would give
\(j(j-1+C)=0\), impossible because \(C\notin\mathbf F_\ell\).

The same formula has a particularly symmetric Jacobi form.  Whichever of
\(A_0,B_0\) terminates first equals \(-m\) in \(\mathbf F_\ell\), so

\[
 \boxed{
 H_\ell(t)=\frac{m!}{(C)_m}
 P_m^{(C-1,C^\ell-1)}(1-2t).}                          \tag{9}
\]

The two Jacobi parameters are Galois conjugates.  Formula (9) follows from
the standard identity for \({}_2F_1(-m,b;C;t)\), because

\[
 m+C+C^\ell-1=m+A_0+B_0
\]

is the other numerator parameter.

## 3. Closed formula for the affine kernel line

Let \(M_\ell\) be the one-period transfer matrix, so that

\[
 (Q_\ell,Q_{\ell+1})^t=M_\ell(1,h)^t.
\]

The coefficient calculation in (6) gives

\[
 M_\ell(1,h)^t=0
 \quad\Longleftrightarrow\quad
 F_\ell\text{ is an exact polynomial solution of (6)}. \tag{10}
\]

Consequently, if \(H_\ell(t_0)=0\), the projective kernel of \(M_\ell\) is
the vertical line and no finite seed \((1,h)\) lies in it.  Otherwise there is
exactly one affine kernel residue, namely

\[
 \boxed{
 h_\ell=\frac rD\frac{H_\ell'(t_0)}{H_\ell(t_0)}.}     \tag{11}
\]

Although (11) is written in \(\mathbf F_{\ell^2}\), it belongs to
\(\mathbf F_\ell\).  One proof is conceptual: normalize
\(H_\ell(t(z))\) to have constant coefficient one.  Equation (6) and that
constant coefficient are defined over \(\mathbf F_\ell\), and uniqueness of
the polynomial solution makes the normalized polynomial Frobenius invariant.

Equivalently, put \(x_0=p/D\).  If \(K_0\) denotes the numerator parameter
which did not terminate first, the Jacobi derivative identity gives

\[
 \boxed{
 h_\ell=-\frac{rK_0}{D}
 \frac{P_{m-1}^{(C,C^\ell)}(x_0)}
      {P_m^{(C-1,C^\ell-1)}(x_0)}}                    \tag{12}
\]

when \(m>0\), with \(h_\ell=0\) when \(m=0\).  This is the requested
explicit Hasse/Jacobi description of the varying kernel line.  It replaces a
product of \(\ell\) transfer matrices by one finite-field special value, but
it does not make the value independent of \(\ell\).

## 4. A uniqueness theorem for factorially reduced seeds

The trace formula at the present inert primes is

\[
 \operatorname{tr}M_\ell=\frac Rr\ne0,
 \qquad \det M_\ell=0.                                 \tag{13}
\]

Thus \(M_\ell\) has one projective kernel line, and a state dies eventually
if and only if it lies in that line.  This elementary uniqueness has a global
consequence which was not used in the earlier factorial-density note.

Let \(S_h\) be the good inert primes at which the fixed integer seed \(h\)
does **not** lie in the kernel.  If \(h_1\ne h_2\), then, except at the
finitely many primes dividing \(h_1-h_2\), the two affine points
\((1,h_1)\) and \((1,h_2)\) cannot both lie in the same projective kernel.
Therefore

\[
 \boxed{
 \text{at most one integer }h\text{ can satisfy }
 \sum_{\ell\in S_h}\frac{\log\ell}{\ell-1}<\infty.}  \tag{14}
\]

To prove (14), suppose two seeds did.  The complement of
their two kernel-hit sets inside the inert primes would have convergent
Mertens weight.  Their intersection would consequently have the divergent
Mertens weight of the inert primes, whereas the preceding projective argument
makes that intersection finite.

There is a quantitative finite-family version.  For distinct integers
\(h_1,\ldots,h_k\), all sufficiently large good inert primes are kernel
primes for at most one of them.  Hence, with
\(w_\ell=\log\ell/(\ell-1)\),

\[
 \sum_{i=1}^k\ \sum_{\substack{\ell\le x\\\ell\in S_{h_i}}}w_\ell
 \ge (k-1)
 \sum_{\substack{\ell\le x\\\ell\text{ good inert}}}w_\ell-O(1). \tag{14a}
\]

Thus all but at most one member of every fixed finite family have divergent
nonkernel Mertens weight, and the aggregate obstruction has the sharp
\((k-1)/k\) average proportion of the inert-prime weight.

Combining (14) with the denominator theorem gives the concrete corollary

\[
 \boxed{
 \text{for a fixed exceptional recurrence, at most one integer seed can
 have }E_N\le C^N\text{ for some }C.}                  \tag{15}
\]

More usefully, if an already classified rational/factorial solution supplies
one seed \(h_0\) which lies in the kernel for all but finitely many good inert
primes, then every other integer seed has a nonkernel set of divergent
Mertens weight.  The exceptional factorial-density classification is then
complete for all \(h\ne h_0\).

There is also an exact finite-prime converse showing why computations alone
cannot prove the desired nonconcentration.  Given any finite set \(T\) of
good inert primes for which the kernel line is affine, the Chinese remainder
theorem gives an infinite arithmetic progression of integer seeds satisfying

\[
 h\equiv h_\ell\pmod\ell\qquad(\ell\in T).             \tag{16}
\]

Among intervals of length tending to infinity, its density is exactly
\(\prod_{\ell\in T}\ell^{-1}\).  Every finite collection of Hasse-kernel
tests therefore has genuine simultaneous integer survivors.

### 4.1 A prime-independent image line for every repeated-root numerator

There is one nontrivial fixed-seed class for which the required
nonconcentration is unconditional.  It comes from the **image** of the
monodromy, rather than from an evaluation of its varying kernel line.

Suppose

\[
 B(n)=r(n+a)^2,
 \qquad a\in\mathbf Z_{\geq0},                         \tag{17}
\]

so that \((u,v)=(2ra,ra^2)\) and \(\Delta_B=0\).  Define the finite
integer orbit

\[
 W_0=1,\qquad W_1=-\bigl(p(1-a)+q\bigr),              \tag{18}
\]

\[
 W_{j+2}=r(j+1)^2W_j-
          \bigl(p(j+2-a)+q\bigr)W_{j+1}
 \quad(0\leq j<a),                                    \tag{19}
\]

and put \(v_a=(W_a,W_{a+1})^t\).  This projective point is independent
of the prime.

For every sufficiently large good prime \(\ell\), the double root of
\(B\) is the residue \(-a\).  The corresponding singular transfer has
image generated by

\[
 (1,-A(1-a))^t=(W_0,W_1)^t.
\]

There are exactly \(a\) transfers after it before the end of the chosen
coefficient period.  Applying those transfers gives (19), and hence

\[
 \operatorname{im}M_\ell=\mathbf F_\ell v_a.          \tag{20}
\]

At a good \(\Delta_c\)-inert prime, the trace formula gives

\[
 \tau:=\operatorname{tr}M_\ell=\frac Rr
      =p(2a-1)-2q\ne0,                                \tag{21}
\]

while \(\det M_\ell=0\).  A rank-one matrix acts on its image by its
nonzero eigenvalue \(\tau\).  Consequently

\[
 \boxed{M_\ell v_a=\tau v_a\ne0.}                    \tag{22}
\]

Thus the fixed projective seed \(v_a\) is outside the Hasse kernel at
**every** good inert prime.  If \(W_a\ne0\), this is the fixed rational
affine seed

\[
 h_a=\frac{W_{a+1}}{W_a}.                             \tag{23}
\]

The image line has an exact characteristic-zero lift.  To avoid confusing
the repeated-root shift with a hypergeometric parameter, write the shift as
\(k=a\), and put

\[
 D=\sqrt{p^2+4r},\quad
 \lambda=\frac{p+D}{2},\quad
 \mu=\frac{D-p}{2},\quad
 y=\frac{\mu}{D},
\]

\[
 T=p(2k-1)-2q,\qquad
 C=k+\frac32-\frac{T}{2D},\qquad
 P_k(t)={}_2F_1(-k,-k;2-C;t).                        \tag{23a}
\]

Here \(P_k\) is a polynomial of degree \(k\).  The complementary solution of
the same Gauss equation as the Hasse polynomial is

\[
 G_k(t)=t^{1-C}(1-t)^{C-2k-2}P_k(t).                 \tag{23b}
\]

In the original EGF coordinate, an unnormalized form is

\[
 \overline F_k(z)=
 (1+\lambda z)^{1-C}(1-\mu z)^{C-2k-2}
 P_k\!\left(y(1+\lambda z)\right).                   \tag{23c}
\]

The identification with the finite recurrence in (18)--(19) is exact, not
only projective evidence.  With

\[
 S_k=D^k(2-C)_k,
\]

terminating-series algebra gives

\[
 S_kP_k(y)=W_k,                                      \tag{23d}
\]

\[
 S_k\left(
   \bigl((1-C)\lambda-(C-2k-2)\mu\bigr)P_k(y)
   +\frac rD P_k'(y)\right)=W_{k+1}.                 \tag{23e}
\]

Thus \(P_k(y)=0\) exactly when the image line is vertical.  Otherwise
\(F_{{\rm img},k}=\overline F_k/P_k(y)\) has initial jet
\((1,W_{k+1}/W_k)\), precisely the affine seed (23).

This closes the image line throughout the hard repeated-root branch.
Admissibility \(p+q\ge0\) implies \(C>1\), since

\[
 2D(C-1)\ge(2k+1)(D-p)>0.
\]

When \(D\notin\mathbf Q\) and \(T\ne0\), the exponent \(C\) is irrational.
Because \(P_k(0)=1\), the nearer singularity \(z=-1/\lambda\) in (23c)
cannot cancel.  Singularity transfer therefore gives

\[
 \frac{Q_n}{n!}\sim
 \frac{(D/\lambda)^{C-2k-2}}{P_k(y)}
 \frac{(-1)^n\lambda^n n^{C-2}}{\Gamma(C-1)}.
                                                               \tag{23f}
\]

Every finite affine image-line orbit is consequently eventually strictly
alternating in sign.  It cannot be the positive/minimal orbit.  The sole
remaining projective candidate is the regular Gauss line

\[
 H_{\rm reg}(t)={}_2F_1(k+1,k+1;C;t),                \tag{23g}
\]

which is exactly the characteristic-zero antecedent of the varying Hasse
kernel line.  The horizontal fixed-seed problem is therefore a rationality
question for this regular logarithmic derivative, not an ambiguity between
the regular and image solutions.

After discarding the finitely many primes dividing its denominator, its
nonkernel primes contain the entire inert half.  In particular their Mertens
weight diverges, and the factorial divisor argument sharpens to

\[
 \liminf_{N\to\infty}\frac{\log E_N}{N\log N}\geq
 \frac12.                                             \tag{24}
\]

There are positive integral image-line seeds.  Take \(a=1\), \(q=-p\), and
\(p\mid r\).  Then

\[
 v_1=(p,r)^t,\qquad h_1=r/p>0,
 \qquad \tau=3p.                                      \tag{25}
\]

Hence, whenever \(p^2+4r\) is nonsquare, the exceptional recurrence

\[
 (p,q,r,u,v)=(p,-p,r,2r,r)
\]

has the explicit positive integer seed \(h=r/p\) outside the kernel at
every good characteristic-inert prime.  For example, this applies to
\((1,-1,3,6,3)\) with \(h=3\), which lies beyond the previously trapped
small-\(r\) wedge.

This result does not identify the possible kernel-concentrated seed from
(14).  An image eigenline and a kernel line are complementary when the trace
is nonzero; proving that one explicit point is always on the former does not
control the latter.  It therefore supplies a genuine fixed-seed
nonconcentration theorem without changing the universal total-decision
boundary.

### 4.2 The positive orbit on this image line is completely classified

The word "positive" in (25) describes the integer **seed**.  It does not say
that its cleared orbit remains positive.  In fact that stronger question has
an exact answer:

\[
 \boxed{
 Q_0=1,\quad Q_1=\frac rp,\quad
 Q_n=rn^2Q_{n-2}-p(n-1)Q_{n-1}>0\ \ (n\ge2)
 \quad\Longleftrightarrow\quad r=2p^2.}
                                                               \tag{26}
\]

Here (p,r>0); the assumption (p\mid r) merely makes the displayed seed
integral.  To prove (26), use projective homogeneity to put

\[
 \rho=\frac r{p^2},\qquad q_n=\frac{Q_n}{p^n}.
\]

Then

\[
 q_0=1,\qquad q_1=\rho,\qquad
 q_n=\rho n^2q_{n-2}-(n-1)q_{n-1}.                    \tag{27}
\]

There is also an elementary order proof, independent of the Lerch formula.
Write \(y_n=s_n/p\) for the unique positive tail.  Then

\[
 y_n=n-1+\frac{\rho(n+1)^2}{y_{n+1}},
 \qquad
 \lambda=\frac{1+\sqrt{1+4\rho}}2,
 \qquad \rho=\lambda(\lambda-1).                    \tag{27a}
\]

If \(0<\rho<2\), then \(\rho<\lambda<2\), so eventually
\(\rho n<y_n<2n\).  For every \(n\ge2\), antitonicity of the Riccati
step propagates this interval backward:

\[
 \rho(n+1)<y_{n+1}<2(n+1)
 \Longrightarrow
 \rho n<y_n<2n.                                     \tag{27b}
\]

The endpoint images are \(n-1+\rho(n+1)/2\) and \(2n\); the first
exceeds \(\rho n\) by \((1-\rho/2)(n-1)>0\).  Hence \(y_2<4\).

If \(\rho>2\), then \(2<\lambda<\rho\), and the reversed interval obeys

\[
 2(n+1)<y_{n+1}<\rho(n+1)
 \Longrightarrow
 2n<y_n<\rho n.                                     \tag{27c}
\]

Now the upper endpoint is below \(\rho n\) by
\((\rho/2-1)(n-1)>0\), so \(y_2>4\).  Since

\[
 y_1=\frac{4\rho}{y_2},                             \tag{27d}
\]

one obtains \(y_1>\rho\) below the resonance and \(y_1<\rho\) above it.
Thus \(y_1=\rho\), equivalently \(s_1=r/p\), is possible only at
\(\rho=2\).  This proves the inequality as well as the equality
classification in (26).

This is the cleared orbit for the normalized Lerch tail

\[
 A_n=n-1,\qquad B_n=\rho(n+1)^2,qquad s_1=\rho.
\]

Put

\[
 \delta=\sqrt{1+4\rho},\quad
 \lambda=\frac{1+\delta}{2},\quad
 \mu=\frac{\delta-1}{2},\quad
 x=-\frac\mu\lambda,\quad
 a=\frac{\mu-1}{\delta}.                              \tag{28}
\]

For (a\ne0), the undivided Lerch contiguous identity from the
characteristic-zero reduction says that a rational boundary (M>0) can be
the positive tail only if

\[
 \left[a(1-x)+x\left(1-\frac\lambda M\right)\right]
 \Phi(x,1,a)=1.                                      \tag{29}
\]

For the image-line boundary (M=\rho), the coefficient on the left is

\[
 a(1-x)+x\left(1-\frac\lambda\rho\right)
 =\frac{-M+\rho}{\lambda M}=0.                       \tag{30}
\]

This is impossible: (-1<x<0), (-1<a<1/2), and (a\ne0), so the Lerch
series in (29) is finite.  The sole omitted value is

\[
 a=0\quad\Longleftrightarrow\quad\delta=3
 \quad\Longleftrightarrow\quad\rho=2.                \tag{31}
\]

At that value the orbit is explicit:

\[
 q_n=(n+1)!,\qquad Q_n=p^n(n+1)!,                    \tag{32}
\]

as direct substitution in (27) verifies.  Conversely, positivity of every
cleared-orbit term is equivalent to equality with the unique positive tail,
so (29)--(31) force some finite (Q_n\le0) whenever (r\ne2p^2).

The same Gauss reduction gives a closed form for the entire image-line
orbit, not merely its equality test.  Put

\[
 D=\sqrt{p^2+4r},\qquad
 \lambda=\frac{p+D}{2},\qquad
 \mu=\frac{D-p}{2}.
\]

The EGF with \(F_{\rm img}(0)=1\) and
\(F_{\rm img}'(0)=r/p\) is

\[
 \boxed{
 F_{\rm img}(z)=
 \left(1+\frac rp z\right)
 (1+\lambda z)^{-3\mu/D}
 (1-\mu z)^{-3\lambda/D}.}                           \tag{32a}
\]

Substitution verifies (32a) directly in the polynomial-tail EGF equation.
It is also the complementary Gauss solution from Section 4.1: the finite
field Hasse image line is the reduction of this singular,
characteristic-zero solution line.

When \(r=2p^2\), one has \(\mu=p\), the factor
\(1+(r/p)z=1+2pz\) cancels the negative singular channel, and (32a)
reduces to

\[
 F_{\rm img}(z)=(1-pz)^{-2}.
\]

Otherwise the nearer singularity \(z=-1/\lambda\) survives.  Singularity
transfer gives, with \(\kappa=3\mu/D\),

\[
 \frac{Q_n}{n!}\sim
 \left(1-\frac{\mu}{p}\right)
 \left(\frac D\lambda\right)^{-3\lambda/D}
 \frac{(-1)^n\lambda^n n^{\kappa-1}}{\Gamma(\kappa)}.
                                                               \tag{32b}
\]

Thus for \(r<2p^2\) every sufficiently large odd \(Q_n\) is negative,
whereas for \(r>2p^2\) every sufficiently large even \(Q_n\) is negative.
This strengthens finite failure to an eventual alternating-sign
classification and explains why the sole positive orbit is the cancellation
resonance.

In particular, every nonsquare-characteristic image-line example from
Section 4.1 fails at a finite index.  For the displayed example
\((p,r,h)=(1,3,3)\), the positive prefix ends with

\[
 Q_7=506817,\qquad Q_8=-2111751.                       \tag{33}
\]

This classification does **not** produce a uniform failure depth.  For each
fixed \(N\), every \(q_n(\rho)\) with \(n\le N\) is a polynomial in \(\rho\)
and \(q_n(2)=(n+1)!>0\).  Hence all of them remain positive in some
neighborhood of \(2\).  Choose a sufficiently large odd prime \(P\) and set

\[
 p=P,\qquad h=2P-1,\qquad r=P(2P-1).                  \tag{34}
\]

Then \(r/p^2=2-1/P\to2\), so the orbit is positive through \(N\), but (26)
says it fails later.  Its characteristic discriminant

\[
 p^2+4r=P(9P-4)                                      \tag{35}
\]

is nonsquare because \(P\) occurs to odd valuation.  Thus arbitrarily
delayed failure already occurs inside the nonsquare repeated-root Hasse
image-line family.

## 5. What the formula does and does not settle

For a fixed integer \(h\), exponential factorial reduction would force

\[
 h\equiv h_\ell\pmod\ell
\]

at all but a Mertens-negligible subset of the inert primes.  Equations
(11)--(12) identify this as nonconcentration of a sequence of
finite-field Jacobi logarithmic derivatives whose degree and parameters vary
with \(\ell\).  The determinant and trace formulas do not control this
projective coordinate.

The missing statement can now be phrased exactly:

> Unless the recurrence has a classified factorially reduced solution line,
> prove that, for every fixed integer \(h\), the primes for which (11) differs
> from \(h\) have divergent Mertens weight.

No theorem currently in the project proves that assertion, and (16) shows
that it cannot follow from any fixed finite list of primes.  This is a
horizontal, varying-characteristic Hasse-value problem, rather than the
operator-level trace problem already solved by the cyclic-continuant formula.

One nearby apparent escape is actually absent.  Suppose a nonzero rational
orbit were eventually hypergeometric-term, so that
\(Q_{n+1}/Q_n=L(n)\in\mathbf Q(D)(n)\) for all large \(n\).  Since every
ratio of two nonzero rational orbit values is rational, \(L(n)=L^\tau(n)\)
at infinitely many integers, hence \(L=L^\tau\in\mathbf Q(n)\).  But the
leading slope of a Riccati solution must solve

\[
 c^2+pc-r=0,
\]

and is irrational when \(\Delta_c\) is nonsquare.  Thus no positive rational
orbit in the exceptional branch can be a hypergeometric **term**.  Any
factorial reduction here would have to be the broader globally-bounded/G-function
phenomenon, not a hidden first-order term solution.

## 6. Why standard p-curvature theorems do not close the gap

The condition here is weaker, and more projective, than the hypothesis in the
Grothendieck--Katz paradigm.  At every good inert prime the determinant/trace
calculation already guarantees **one** polynomial solution and hence a
one-dimensional kernel of the period operator.  It does not give zero
p-curvature or a full basis of polynomial solutions.  Moreover, the line in
that one-dimensional kernel is allowed to vary with \(\ell\).  The open
condition is the horizontal compatibility

\[
 (1,h)\in\ker M_\ell
\]

for one fixed rational projective point at almost every inert prime, not the
existence of some kernel point in each reduction.

The usual Grothendieck--Katz implication starts from vanishing p-curvature at
almost every prime and predicts finite characteristic-zero monodromy.  It
does not supply a converse from a rank-one kernel on a density-one-half set,
and still less a theorem that its projective initial jet cannot repeatedly
equal a prescribed rational point.  An invariant-line version strong enough
for the present density-weighted statement would itself be a new arithmetic
input.

Classical hypergeometric p-curvature classifications do not bypass this
distinction.  In the nonaligned branch the characteristic-zero exponent
differences contain the nonzero quadratic-irrational term \(R/(rD)\), so the
equation is not on the rational-parameter, globally nilpotent, or finite
monodromy lists.  Reduction modulo an inert prime replaces that irrational
parameter by \(C\in\mathbf F_{\ell^2}\) and automatically creates the
degree-\(m(\ell)\) polynomial (8).  The existence of those separate Hasse
polynomials is therefore expected and carries no cross-prime identification
of their logarithmic derivatives (11).  That identification is precisely
the unproved part.

## 7. Verification

The standalone checker
[`polynomial-tail-hasse-kernel-verifier.cs`](../tools/polynomial-tail-hasse-kernel-verifier.cs)
computes the kernel in two independent ways:

1. by multiplying the original integer recurrence through one period; and
2. by evaluating (8) and (11) in \(\mathbf F_{\ell^2}\).

It checks three exceptional families at every good inert prime through 200,
including a repeated numerator root and distinct split roots.  The two answers
agree at every tested prime, and the quadratic-field result always descends
to \(\mathbf F_\ell\).  Three repeated-root image lines from Section 4.1,
including the \(a=2\) case, give 149 further inert-prime checks through 500;
none is ever the kernel line.  A separate coefficient-box sweep checks 35,573 good
inert parameter/prime instances through 43, including 2,220 vertical kernel
lines, with zero recurrence/Jacobi mismatches.  The command is

```powershell
dotnet run --property:NuGetAudit=false tools/polynomial-tail-hasse-kernel-verifier.cs
```

This is a standalone source-file check; it does not build the repository.

The companion checker
[`polynomial-tail-repeated-root-image-orbit-verifier.cs`](../tools/polynomial-tail-repeated-root-image-orbit-verifier.cs)
verifies the closed orbit (32), the concrete failure (33), nonsquareness of
the sampled delayed family (34), and an exact box of 20,000 image-line seeds.
In that box the only depth-500 survivors are the 100 resonances (r=2p^2);
the latest nonresonant first failure is (Q_{15}).  Run it with

```powershell
dotnet run --property:NuGetAudit=false tools/polynomial-tail-repeated-root-image-orbit-verifier.cs -- 100 200 500
```

It too is a standalone source-file check and does not build the repository.
