# Arithmetic of the Lerch hard core

## Scope

This note sharpens the Lerch subfamily isolated in
[`polynomial-tail-connection-coordinate.md`](polynomial-tail-connection-coordinate.md).
It gives four exact results:

1. the algebraic target in equation (L5) has a much simpler closed form;
2. the corresponding incomplete-beta formulation identifies the precise
   transcendence statement that would be needed, and shows why mere
   transcendence of the incomplete-beta value cannot exclude equality;
3. the elementary two-embedding truncation obstruction has an exact norm
   limit, not just an order estimate;
4. ordinary diagonal Padé has a factorial coefficient-denominator
   obstruction, and exponential height after evaluation is equivalent to an
   explicit near-total ideal-gcd cancellation.

It also excludes a nontrivial infinite region of this subfamily by a finite
integer-orbit argument.  None of the results below claims to settle the full
Lerch-value problem.

Consider

\[
 s_n=pn+q+\frac{r(n+1)^2}{s_{n+1}},
 \qquad p,r\in\mathbf Z_{\geq1},\qquad q\in\mathbf Z,\qquad p+q\geq0.
 \tag{1}
\]

Put

\[
 D=\sqrt{p^2+4r},\qquad
 \lambda=\frac{p+D}{2},\qquad
 \mu=\frac{D-p}{2},\qquad
 t=\frac{\mu}{\lambda}=-x.
 \tag{2}
\]

Then

\[
 \lambda\mu=r,\qquad D=\lambda+\mu,
 \qquad 0<t<1.
 \tag{3}
\]

Assume throughout the residual branch

\[
 D\notin\mathbf Q,\qquad 2q\ne p.
 \tag{4}
\]

The Lerch parameter from (L2) is

\[
 a=\frac12-\frac{p-2q}{2D}
   =\frac{\mu+q}{D}.
 \tag{5}
\]

In particular \(a\) is a quadratic irrational, and under the nontrivial
quadratic automorphism

\[
 t^\tau=t^{-1},\qquad a^\tau=1-a,
 \qquad \lambda^\tau=-\mu.
 \tag{6}
\]

## The target collapses

Equation (L5) says that a proposed rational boundary (M\ne0) is the tail
value precisely when

\[
 \Phi(-t,1,a)=
 \frac{1}{a(1+t)-t(1-\lambda/M)}.
 \tag{7}
\]

The denominator in (7) is elementary.  From (3)--(5),

\[
 a(1+t)=\frac{\mu+q}{\lambda}
        =t+\frac q\lambda.
\]

Consequently

\[
 a(1+t)-t(1-\lambda/M)
 =\frac q\lambda+\frac\mu M,
\]

and hence

\[
 \boxed{
 s_1=M
 \quad\Longleftrightarrow\quad
 \Phi(-t,1,a)=K_M:=\frac{\lambda M}{qM+r}.
 }
 \tag{8}
\]

If \(qM+r=0\), equality is impossible: the Lerch series is finite at
\(-t\), because \(0<t<1\) and \(a\notin\mathbf Z\), whereas the denominator
in (7) vanishes.  Otherwise \(K_M\) is a nonzero member of
\(\mathbf Q(D)\), and

\[
 K_M^\tau=-\frac{\mu M}{qM+r}=-tK_M.
 \tag{9}
\]

Formula (8) is useful computationally as well as conceptually: no nested
expression involving \(a,x,\lambda\) is needed to construct the exact target.

## The exact incomplete-beta obstruction

Let

\[
 y=\frac{t}{1+t}=\frac{\mu}{D}=\frac{D-p}{2D}.
 \tag{10}
\]

For \(a>0\), substitution \(v=tu/(1+tu)\) in the Euler integral gives

\[
 \Phi(-t,1,a)
 =\int_0^1\frac{u^{a-1}}{1+tu}\,du
 =t^{-a}B_y(a,1-a).
 \tag{11}
\]

The incomplete integral in (11) is convergent whenever \(a>0\); its upper
endpoint is \(y<1\), so no condition on \(1-a\) is needed there.  Combining
(8) and (11) gives

\[
 \boxed{
 s_1=M
 \quad\Longleftrightarrow\quad
 B_y(a,1-a)=K_M t^a.
 }
 \tag{12}
\]

This changes the arithmetic interpretation of the problem.  Since \(t\) is
algebraic with \(t\notin\{0,1\}\) and \(a\) is algebraic irrational, the
Gelfond--Schneider theorem proves

\[
 t^a\ \text{is transcendental}.
 \tag{13}
\]

Therefore, if the integer equality in (12) holds, then

\[
 B_y(a,1-a)\ \text{is necessarily transcendental}.
 \tag{14}
\]

Thus a theorem proving only transcendence of this incomplete-beta value
cannot rule out the equality: transcendence is already forced by the
hypothetical equality.  What is actually needed is the stronger statement

\[
 \boxed{
 B_y(a,1-a)\notin \overline{\mathbf Q}\,t^a,
 }
 \tag{15}
\]

or, equivalently, nonalgebraicity of \(t^{-a}B_y(a,1-a)\).  The residual PCF
problem is a linear-independence problem between an incomplete-beta value and
an algebraic power, not a bare transcendence problem for either factor.

### The reciprocal value and its exact defect

Suppose additionally \(0<a<1\), equivalently

\[
 -\mu<q<\lambda.
 \tag{16}
\]

Splitting the complete beta integral at \(y\), or equivalently splitting
\(\int_0^\infty u^{a-1}/(1+tu)\,du\) at one, gives

\[
 \Phi(-t,1,a)+t^{-1}\Phi(-t^{-1},1,1-a)
 =\frac{\pi t^{-a}}{\sin(\pi a)}.
 \tag{17}
\]

Using (9), a hypothetical equality \(\Phi(-t,1,a)=K_M\) implies the exact
reciprocal formula

\[
 \boxed{
 \Phi(-t^{-1},1,1-a)-K_M^\tau
 =\frac{\pi t^{1-a}}{\sin(\pi a)}>0.
 }
 \tag{18}
\]

This is the Lerch specialization of the Wronskian defect in the connection
coordinate note.  It makes the failure of coefficientwise quadratic
conjugation completely explicit: the analytic reciprocal value is not the
algebraic conjugate target; the missing term is a nonzero complete-beta
connection constant.

Neither Gelfond--Schneider nor Baker's theorem decides whether the quotient
in (15) is algebraic.  Gelfond--Schneider treats the individual algebraic
power \(t^a\); it does not establish the required linear independence from
the incomplete-beta integral.  Likewise, proving that
\(\sin(\pi a)\) and \(t^a\) are individually transcendental does not control
the quotient in (18).

## Exact saturation of the two-embedding truncation

The preceding formulas also sharpen the truncation barrier in (L6)--(L7).
Assume the equality in (8), and put

\[
 S_N=\sum_{n=0}^N\frac{(-t)^n}{n+a},
 \qquad R_N=K_M-S_N\in\mathbf Q(D).
 \tag{19}
\]

At the physical embedding, the convergent Lerch tail gives

\[
 \lim_{N\to\infty}
 \frac{N R_N}{(-t)^{N+1}}=\frac1{1+t}.
 \tag{20}
\]

At the other embedding, use \(a^\tau=1-a\), \(t^\tau=t^{-1}\), and the fact
that the final terms dominate the now-divergent finite sum:

\[
 \begin{aligned}
 R_N^\tau
 &=K_M^\tau-\sum_{n=0}^N\frac{(-t^{-1})^n}{n+1-a},\\
 \lim_{N\to\infty}
 \frac{N R_N^\tau}{(-t^{-1})^N}
 &=-\frac1{1+t}.
 \end{aligned}
 \tag{21}
\]

Multiplying (20) and (21) proves the exact field-norm limit

\[
 \boxed{
 \lim_{N\to\infty}
 N^2\,N_{\mathbf Q(D)/\mathbf Q}(K_M-S_N)
 =\frac{t}{(1+t)^2}
 =\frac{r}{p^2+4r}>0.
 }
 \tag{22}
\]

Thus the exponential gain \(t^N\) at the convergent embedding is canceled
exactly by \(t^{-N}\) at the other embedding.  The product formula does not
merely fail because of a loose estimate: the naive truncations saturate at a
positive rational multiple of \(N^{-2}\).  Increasing the truncation depth
cannot turn this particular construction into an exponential norm
contradiction.  Any successful Padé or auxiliary-function argument must
improve both embeddings simultaneously, while also controlling the
denominators introduced by the distinct factors \(n+a\).

## What ordinary diagonal Padé repairs--and does not

Diagonal Padé approximation does repair the analytic half of the truncation
failure.  For \(a>0\),

\[
 \Phi(z,1,a)=\int_0^1\frac{u^{a-1}}{1-zu}\,du
 \tag{27}
\]

is a Markov function.  Its diagonal Padé approximants converge locally
uniformly on \(\mathbf C\setminus[1,\infty)\), in particular at every negative
real argument.  For

\[
 F(z)=a\Phi(z,1,a)={}_2F_1(a,1;a+1;z),
\]

the denominator of the normalized \([n/n]\) approximant is explicitly

\[
 Q_n(z)={}_2F_1(-n,-a-n;-a-2n;z).
 \tag{28}
\]

This is the specialization \(c=a+1,m=n\) of Padé's formula; see, for example,
[Driver--Jordaan](https://arxiv.org/abs/0901.0435).

Write \(\mathcal P_n(z,a)\) for the corresponding approximant to
\(\Phi(z,1,a)\).  In the central strip \(0<a<1\), both parameters \(a\) and
\(1-a\) define positive Markov measures.  Under the hypothetical equality
(8), put

\[
 \varepsilon_n=K_M-\mathcal P_n(-t,a)\in\mathbf Q(D).
 \tag{29}
\]

Padé convergence at the physical embedding gives

\[
 \varepsilon_n\longrightarrow0.
\]

Unlike the raw truncation, algebraic conjugation of the finite Padé
approximant is again a convergent Padé approximant:

\[
 \varepsilon_n^\tau
 =K_M^\tau-\mathcal P_n(-t^{-1},1-a)
 \longrightarrow
 K_M^\tau-\Phi(-t^{-1},1,1-a)
 =-\frac{\pi t^{1-a}}{\sin(\pi a)}\ne0.                 \tag{30}
\]

Consequently

\[
 N_{\mathbf Q(D)/\mathbf Q}(\varepsilon_n)\longrightarrow0.
 \tag{31}
\]

So Padé continuation genuinely removes the exponential blow-up at the second
embedding.  The remaining obstruction is arithmetic rather than analytic.
Indeed, the coefficient of \(z^k\) in (28) is

\[
 \frac{(-n)_k(-a-n)_k}{(-a-2n)_k\,k!}.
\]

The single factor

\[
 (-a-2n)_n=(-1)^n(a+n+1)_n                              \tag{32}
\]

clears all displayed denominator coefficients of \(Q_n\).  Its field norm
has

\[
 \log\left|N_{\mathbf Q(D)/\mathbf Q}(a+n+1)_n\right|
 =2n\log n+O(n).                                         \tag{33}
\]

The standard numerator construction introduces the same Pochhammer-scale
denominators through the moments \(a/(a+j)\).  Classical Jacobi asymptotics
make the Padé error geometric in \(n\), but a geometric upper bound cannot
beat the \(e^{\,\Theta(n\log n)}\) generic clearing factor in (33).

The following ideal calculation separates two questions that the crude
clearing factor leaves conflated.

### Coefficient height really is factorial

Let \(K=\mathbf Q(D)\), and choose a fixed positive integer \(L\) such that

\[
 \xi_j=L(a+j)\in\mathcal O_K\qquad(j\in\mathbf Z).
\]

The leading coefficient of (28) is

\[
 [z^n]Q_n=(-1)^n\frac{(a+1)_n}{(a+n+1)_n}
           =(-1)^n
             \frac{\prod_{j=1}^n\xi_j}
                  {\prod_{j=n+1}^{2n}\xi_j}.             \tag{33a}
\]

Write \(\mathfrak b_n\) for the reduced denominator ideal of (33a).  Then

\[
 \boxed{\log N\mathfrak b_n\ge n\log n-O(n).}            \tag{33b}
\]

Here is a proof.  Put

\[
 A_n=\prod_{j=1}^n(\xi_j),\qquad
 B_n=\prod_{j=n+1}^{2n}(\xi_j)
\]

as principal integral ideals.  Since

\[
 \mathfrak b_n=\frac{B_n}{A_n+B_n},
\]

it is enough to bound the norm of their ideal gcd.  Now

\[
 N_{K/\mathbf Q}(\xi_j)
 =L^2\left(j(j+1)+a(1-a)\right)
\]

is a quadratic polynomial in \(j\) with positive leading coefficient,

\[
 \log NB_n=2n\log n+O(n).                                \tag{33c}
\]

Outside the fixed set of prime ideals dividing \(L\), a prime ideal common
to \(A_n\) and \(B_n\) divides both \(\xi_i\) and \(\xi_j\) for some
\(1\le i\le n<j\le2n\), and hence divides the rational integer \(j-i\).
Its underlying rational prime is therefore at most \(2n\).  Apart from a
second fixed set, an inert prime cannot divide any \(\xi_j\): reduction of
\(\xi_j=La+Lj\) would otherwise put the nonrational residue of \(La\) in
the prime field.  At a split prime \(\ell\), each of the two degree-one prime
ideals has at most one admissible residue class modulo every \(\ell^e\).
Thus its valuation in \(B_n\) is at most

\[
 \frac n{\ell-1}+O\!\left(\frac{\log n}{\log\ell}\right).
\]

The prime number theorem for the fixed quadratic character gives

\[
 \sum_{\substack{\ell\le2n\\ \ell\ {\rm split}}}
       \frac{\log\ell}{\ell-1}
 =\frac12\log n+O(1).
\]

After summing over the two primes above every split \(\ell\), the common ideal
satisfies

\[
 \log N(A_n+B_n)\le n\log n+O(n).
\]

(For integral ideals, \(A_n+B_n\) is their ideal gcd.)  Subtracting this
from (33c) proves (33b).  Since every common coefficient denominator for
\(Q_n\) must clear its leading coefficient, no coefficientwise normalization
of the ordinary diagonal Padé denominator has exponential height.  The
factorial scale in (33) is therefore genuine, not merely an artifact of the
displayed Pochhammer clearing factor.

### Exact cancellation required after evaluation

Evaluation at the linked point \(z=-t\) could in principle cancel coefficient
denominators.  That possibility can be stated exactly.  Localize
\(\mathcal O_K\) at the fixed finite set \(S\) of prime ideals needed to make
\(a\) and \(t\) integral, and put

\[
 C_n=(-a-2n)_n=(-1)^n(a+n+1)_n,
 \qquad
 U_n=C_nQ_n(-t).
\]

The hypergeometric formula gives the explicit \(S\)-integer

\[
 U_n=
 \sum_{k=0}^n(-1)^k\binom nk
 (-a-n)_k(-a-2n+k)_{n-k}(-t)^k.             \tag{33d}
\]

Consequently the reduced denominator ideal outside \(S\) is not just bounded
by a Pochhammer ideal; it is exactly

\[
 \boxed{
 \operatorname{den}_S\!\left(Q_n(-t)\right)
 =\frac{(C_n)}{(C_n)+(U_n)}.
 }                                                        \tag{33e}
\]

The omitted \(S\)-part has norm \(e^{O(n)}\): at each fixed prime, the
valuation of a length-\(n\) arithmetic Pochhammer product is \(O(n)\), and
the powers of the fixed element \(t\) also contribute only \(O(n)\).  On the
other hand,

\[
 \log|N_{K/\mathbf Q}(C_n)|=2n\log n+O(n).
\]

It follows that \(Q_n(-t)\) has exponential denominator height if and only if

\[
 \boxed{
 \log N\bigl((C_n)+(U_n)\bigr)=2n\log n+O(n).
 }                                                        \tag{33f}
\]

In words: evaluation must cancel all but \(e^{O(n)}\) of a factorial-size
Pochhammer ideal.  The identities \(a^\tau=1-a\) and \(t^\tau=t^{-1}\)
control the two archimedean values, as (30) shows, but impose no such
nonarchimedean gcd identity.

For the full rational approximant, if \(P_n/Q_n\) denotes the normalized
Padé pair, then

\[
 \widehat P_n=C_n(a)_{n+1}P_n(-t),\qquad
 \widehat Q_n=C_nQ_n(-t)
\]

are \(S\)-integral.  The exact projective content ideal of these coordinates
is

\[
 \mathfrak g_n=(\widehat P_n)+((a)_{n+1}\widehat Q_n).   \tag{33g}
\]

Thus the reduced nonarchimedean projective height is obtained by removing
the common ideal \(\mathfrak g_n\) from the two coordinate ideals.  This
ideal formulation does not assume that \(\mathcal O_K\) is principal.

Thus the last step cannot be supplied by geometric Padé error estimates or
by the reciprocal connection formula.  It is an explicit factorial-reduction
problem for the hypergeometric values (33d) and (33g).  Proving the near-total
gcd in (33f)--(33g) would give the needed exponential height; proving that a
positive proportion of the Pochhammer ideal survives would rigorously rule
out ordinary diagonal Padé.  Neither conclusion follows from the present BDS
arithmetic.  This is the precise remaining lemma for this auxiliary-form
route, not an unresolved choice of clearing factor.  It is the Padé analogue
of the denominator-density wall isolated in
[the factorial-density note](polynomial-tail-factorial-density-obstruction.md):
in both formulations, an exponential-height conclusion requires a
factorial-size collection of prime-ideal valuations to disappear.

## An infinite excluded region

There is nevertheless a genuine infinite part of the Lerch hard core that
requires no transcendence theorem.

This region is also covered by the repository's stronger shifted norm-gap
path.  Reindexing with \(t_n=s_{n-1}\) sends (1) to the coefficient tuple
\((p,q-p,r,0,0)\).  The executable method
TryShiftedExactBeattyTrapCertificate in
[PolynomialExactBeattyTrap.cs](../src/Puck.Maths/Research/PolynomialExactBeattyTrap.cs)
recognizes every shifted strict norm-gap trap, and both total-DFAO
constructors consume the resulting certificate.  On the slice proved below,
the all-index nonintegrality conclusion is also machine-checked by
scaled_bds_ne_integer in
[ScaledBDS.lean](../formal/PuckMathsFormal/PuckMathsFormal/PolynomialTail/ScaledBDS.lean).
The elementary orbit proof here is an independent Lerch-specific derivation,
not a claim that this slice was previously uncovered.

### Theorem

For every pair of integers

\[
 p\ge1,\qquad 1\le r\le p,
 \tag{34}
\]

the positive tail

\[
 s_n=pn+\frac{r(n+1)^2}{s_{n+1}}
 \tag{35}
\]

does not take an integer value at \(n=1\).

This region automatically lies in the residual Lerch branch.  Indeed,
\(q=0\) gives \(2q\ne p\).  Also \(p^2+4r\) is not a square: the next square
of the same parity after \(p^2\) is \((p+2)^2\), which would require
\(r\ge p+1\).

### Proof

Suppose \(s_1=M\in\mathbf Z\), and write

\[
 d=M-p.
\]

Since \(s_1=p+4r/s_2\), positivity gives \(d\in\mathbf Z_{>0}\).  The cleared
forward orbit has

\[
 Q_0=1,\qquad Q_1=d,\qquad
 Q_n=rn^2Q_{n-2}-pnQ_{n-1}\quad(n\ge2).
 \tag{36}
\]

Exact equality with the positive tail would force every \(Q_n>0\).  In
particular,

\[
 Q_2=4r-2pd>0.
\]

Because \(r\le p\), this gives \(d<2\), hence \(d=1\), and also \(r>p/2\).
For \(p\ge2\),

\[
 Q_3=9r-3p(4r-2p)
    =3\bigl(2p^2-(4p-3)r\bigr)\le0.             \tag{37}
\]

To verify the final inequality, use the integer bound
\(r\ge\lfloor p/2\rfloor+1\).  If \(p=2k\), then

\[
 (4p-3)r-2p^2\ge(8k-3)(k+1)-8k^2=5k-3>0.
\]

If \(p=2k+1\ge3\), then

\[
 (4p-3)r-2p^2\ge(8k+1)(k+1)-2(2k+1)^2=k-1\ge0.
\]

This contradicts positivity.  The remaining case is \(p=r=1\).  Here the
same forced seed \(d=1\) gives

\[
 (Q_0,Q_1,Q_2,Q_3,Q_4,Q_5)=(1,1,2,3,20,-25),
\]

again a contradiction.  Therefore no integer equality exists in (34).  \(\square\)

## What remains in this subfamily

The companion exact-orbit argument in
[`polynomial-tail-lerch-q0-linear-wedge.md`](polynomial-tail-lerch-q0-linear-wedge.md)
first gives a symbolic extension from \(1\leq r\leq p\) to
\(1\leq r\leq2p\), then uses the resulting four-inequality finite reduction
and a standalone exact certificate to prove \(1\leq r\leq40p\). Every fixed
linear wedge \(r\leq Cp\) similarly reduces to finitely many integer seeds.

### Complete square-characteristic Lerch classification

The logarithmic argument used there extends from `q=0` to the full Lerch
family.  Suppose

\[
 D=\sqrt{p^2+4r}=p+2k\in\mathbf Z,
 \qquad k\in\mathbf Z_{>0}.
 \tag{37a}
\]

Then \(r=k(p+k)\), \(t=k/(p+k)\in\mathbf Q\), and

\[
 a=\frac{k+q}{D}\in\mathbf Q.                          \tag{37b}
\]

For rational \(a\ne0,-1,-2,\ldots\), the value
\(\Phi(-t,1,a)\) is transcendental.  To see this, use

\[
 \Phi(z,1,a)=\frac1a+z\Phi(z,1,a+1)                    \tag{37c}
\]

finitely many times to move the rational parameter to some
\(A/B\in(0,1]\).  The Euler integral and \(u=x^B\) give

\[
 \Phi(-t,1,A/B)
 =B\int_0^1\frac{x^{A-1}}{1+t x^B}\,dx.               \tag{37d}
\]

This is a nonzero integral of a proper rational function with distinct
algebraic poles.  Partial fractions express it as a nonzero algebraic linear
form in chosen logarithms of nonzero algebraic numbers, so Baker's theorem
makes it transcendental.  Equation (37c) preserves transcendence in both
directions.

Under the admissibility condition \(p+q\ge0\), (37b) cannot be a negative
integer: \(k+q=-mD\) with \(m\ge1\) would give \(q<-p\).  The only remaining
resonance is

\[
 a=0\quad\Longleftrightarrow\quad q=-k.                \tag{37e}
\]

That resonance is not an unresolved special value.  It is the exact affine
tail

\[
 \boxed{s_n=(p+k)n,}                                   \tag{37f}
\]

because

\[
 pn-k+\frac{k(p+k)(n+1)^2}{(p+k)(n+1)}=(p+k)n.
\]

For every \(q\ne-k\), an algebraic value of \(s_1\) would make the exact
Möbius coordinate

\[
 \Phi(-t,1,a)=\frac{\lambda s_1}{q s_1+r}
\]

algebraic; the zero-denominator case was excluded above.  This contradicts
(37d).  Hence

\[
 \boxed{
 D\in\mathbf Z:\quad
 q=-\frac{D-p}{2}\Longrightarrow s_n=\frac{p+D}{2}n,
 \quad
 q\ne-\frac{D-p}{2}\Longrightarrow s_1
 \text{ is transcendental}.
 }                                                       \tag{37g}
\]

Thus the square-characteristic Lerch slice is completely classified at the
first tail: its only first-tail integer hits are on the displayed exact-affine
resonance.  All residual first-tail special-value difficulty requires
\(D\notin\mathbf Q\).

The exact arithmetic target is now

\[
 \frac{B_y(a,1-a)}{t^a}=\frac{\lambda M}{qM+r},
 \tag{38}
\]

with \(t,y,a\) linked by (2), (5), and (10).  The open portion is to prove
that the left side cannot be algebraic at the admissible linked quadratic
arguments, outside finite-orbit exclusions such as (34).

The norm theorem (22) says precisely why truncation fails, while
(29)--(33g) show that ordinary diagonal Padé approximation repairs the
two-embedding analytic imbalance but turns the missing denominator estimate
into a near-total factorial ideal-gcd problem.
A plausible next attack is a genuinely simultaneous Padé construction for the
pair

\[
 \Phi(-t,1,a),\qquad \Phi(-t^{-1},1,1-a),
\]

designed so that (17) cancels the complete-beta connection term and the
remaining algebraic linear form has both real embeddings small.  Such a
construction would still need denominator growth substantially below the
product of all \(n+a\) factors.  No such estimate is proved here.
