# Positive EGF arithmetic: what positivity does and does not force

Let

\[
 F(z)=\sum_{n\geq0}\frac{Q_n}{n!}z^n
\]

be the exponential generating function of a cleared integral orbit.  In the
unresolved minimal case, \(Q_n>0\), \(F\) is holonomic over
\(\mathbf Q(z)\), and \(F\) is analytic through the nearer negative finite
singularity of its differential equation.  Its first actual singularity is
the positive conjugate and has an algebraic irrational Frobenius exponent.
This note records exactly what the standard arithmetic and positivity
theorems say about that situation.

The conclusion is negative but useful: **positivity by itself does not force
factorial reduction**, even for a strictly positive integral holonomic orbit.
Any successful theorem must use the special two-singularity connection
geometry of the polynomial-tail equation, not merely coefficient positivity,
integrality of \(Q_n\), or holonomy.

## 1. A strict positive counterexample

Put \(a=\sqrt2\) and define

\[
 \boxed{
 F_0(z)=\frac14\left((2+a)(1-z)^{-a}
                +(2-a)(1-z)^a\right)
       =\sum_{n\geq0}\frac{P_n}{n!}z^n .}             \tag{1}
\]

Then

\[
 P_n=\frac{(2+a)(a)_n+(2-a)(-a)_n}{4}.                \tag{2}
\]

These numbers have all the following properties.

### Integrality

Let

\[
 R_n=\frac{a\big((2+a)(a)_n-(2-a)(-a)_n\big)}4.
\]

The two Pochhammer recurrences give

\[
 P_{n+1}=nP_n+R_n,
 \qquad R_{n+1}=nR_n+2P_n.
\]

Eliminating \(R_n\) yields

\[
 \boxed{P_{n+2}=(2n+1)P_{n+1}+(2-n^2)P_n},           \tag{3}
\]

with \(P_0=P_1=1\).  Hence every \(P_n\) is an integer.

### Strict positivity

Both \(2+a\) and \(2-a\) are positive.  For \(n\geq2\), the first two
factors of

\[
 (-a)_n=(-a)(1-a)(2-a)\cdots(n-1-a)
\]

are negative and all remaining factors are positive.  Thus
\((a)_n>0\) and \((-a)_n>0\) for every \(n\geq2\).  Equation (2), together
with \(P_0=P_1=1\), proves

\[
 \boxed{P_n\in\mathbf Z_{>0}\quad(n\geq0).}           \tag{4}
\]

### Irrational local exponents

The function (1) satisfies

\[
 \boxed{(1-z)^2F_0''-(1-z)F_0'-2F_0=0.}              \tag{5}
\]

The indicial roots at \(z=1\) are \(a\) and \(-a\).  This operator is
minimal for \(F_0\).  Indeed, continuation once around \(z=1\) multiplies
the two nonzero summands in (1) by the distinct scalars
\(e^{-2\pi ia}\) and \(e^{2\pi ia}\).  Their sum therefore does not have
one-dimensional monodromy, whereas every nonzero solution of a first-order
equation over \(\overline{\mathbf Q}(z)\) does.

Consequently the minimal operator of this rational Taylor series has the
irrational exponents \(\pm\sqrt2\).

### Superexponential common denominators

Let

\[
 E_N(F_0)=\mathop{\rm lcm}_{0\leq n\leq N}
       \operatorname{denominator}(P_n/n!).            \tag{6}
\]

The coefficient growth is only polynomial:

\[
 \frac{P_n}{n!}
 =\frac{2+a}{4\Gamma(a)}n^{a-1}(1+O(n^{-1})).         \tag{7}
\]

There is a direct factorial lower bound.  Rewrite (3) as

\[
 P_{n+2}=B(n+1)P_n-A(n+2)P_{n+1},
\quad
A(k)=-2k+3,\quad B(k)=2-(k-1)^2.                     \tag{8}
\]

If \(2\) is a quadratic nonresidue modulo an odd prime \(\ell\), then
\(B(k)\not\equiv0\pmod\ell\) for every \(k\).  Every recurrence transfer is
therefore invertible modulo \(\ell\).  Since \(P_0=1\), two adjacent terms
can never both vanish modulo \(\ell\).  At least one of \(P_{N-1},P_N\) is
an \(\ell\)-adic unit, and hence

\[
 v_\ell(E_N(F_0))\geq v_\ell((N-1)!).                 \tag{9}
\]

Multiplying (9) over the primes inert in \(\mathbf Q(\sqrt2)\), and using
the prime number theorem for its nonprincipal quadratic character, gives

\[
 \boxed{\frac12\leq
 \liminf_{N\to\infty}\frac{\log E_N(F_0)}{N\log N}
 \leq
 \limsup_{N\to\infty}\frac{\log E_N(F_0)}{N\log N}
 \leq1.}                                             \tag{10}
\]

The upper bound uses only \(E_N(F_0)\mid N!\).  In particular,

\[
 \boxed{\limsup_{N\to\infty}
        \frac{\log E_N(F_0)}N=+\infty.}               \tag{11}
\]

The arithmetic local-exponent theorem gives an independent check.  If
\(E_N(F_0)\leq C^N\) for some fixed \(C\), then (5), (7), and rationality of
the coefficients would make \(F_0\) a G-function.  The
André--Chudnovsky--Katz theorem says that the minimal operator of a
G-function is Fuchsian with rational local exponents, contradicting the
minimality calculation above.

Thus (1) is an exact counterexample to the proposed general implication

\[
 \left.
 \begin{array}{c}
  Q_n\in\mathbf Z_{>0},\\
  \sum Q_nz^n/n!\text{ holonomic over }\mathbf Q(z),\\
  \sum Q_nz^n/n!\text{ has finite positive radius}
 \end{array}\right\}
 \quad\Longrightarrow\quad E_N\leq C^N.              \tag{12}
\]

The example deliberately does **not** have the two distinct quadratic-
conjugate finite singularities of the polynomial-tail equation.  It therefore
does not disprove a theorem exploiting that extra geometry.

## 2. Why the standard tools do not add the missing implication

### G-functions and André--Chudnovsky--Katz

They give exactly the already-used conditional implication

\[
 E_N\leq C^N
 \quad\Longrightarrow\quad
 \text{all local exponents of the minimal operator are rational}. \tag{13}
\]

They do not derive the premise from coefficient signs.  Example (1) proves
that no such derivation is available from positivity and integral \(Q_n\)
alone.

### Arithmetic Gevrey duality

For a formal series \(H(z)=\sum h_nz^n\), arithmetic Gevrey order \(s\)
requires the conjugates and the common denominators of
\(h_n/(n!)^s\) to have at most exponential growth.  Applied to

\[
 H(z)=\sum_{n\geq0}Q_nz^n,
\]

the order-one denominator condition is precisely exponential control of
\(Q_n/n!\).  Equivalently, its order-zero Borel transform is the EGF \(F\)
and requires the same condition.  Hence arithmetic Gevrey duality cannot be
used to prove factorial reduction: assuming that the orbit is an arithmetic
Gevrey series already assumes factorial reduction.

Integrality of \(Q_n\) controls the denominators of \(Q_n\), not those of
\(Q_n/n!\).  The distinction is substantive, as (1)--(11) show.

### Pólya--Carlson

The integer-coefficient series \(\sum Q_nz^n\) of a minimal factorial-growth
orbit has radius zero, so Pólya--Carlson does not apply to it.  The EGF has
positive radius, but its coefficients are the generally nonintegral rationals
\(Q_n/n!\).  In the counterexample, already \(P_2/2!=3/2\).

Nor can a fixed rescaling repair this.  If some integer \(m\) made
\(m^nQ_n/n!\) integral for every \(n\), then \(E_N\mid m^N\), which is
exactly exponential factorial reduction and is impossible in (1).

### Pringsheim's theorem

Pringsheim says that a power series with nonnegative real coefficients and
finite radius \(R\) is singular at \(z=R>0\).  In the polynomial-tail
problem this identifies the positive singularity after minimality has
cancelled the nearer negative channel.  It says nothing about denominators,
arithmetic heights, or whether the connection coefficient producing that
cancellation can be rational.

### Algebraic conjugacy

Rational Taylor coefficients are fixed coefficientwise by algebraic field
automorphisms.  Analytic continuation and convergence are archimedean
operations; a general automorphism of \(\mathbf C\) is not continuous and
cannot be interchanged with the limiting process.  Consequently one cannot
deduce that cancellation at one finite singularity is transported to its
quadratic conjugate.  In this project the explicit nonzero Wronskian defect in
`polynomial-tail-connection-coordinate.md` is the concrete form of this
failure.

## 3. Exact remaining EGF statement

For the polynomial-tail equation, positivity contributes the following
analytic facts:

1. the rational initial jet lies on the one-dimensional minimal line, so the
   nearer negative singular channel vanishes;
2. Pringsheim places an actual singularity at the positive conjugate;
3. Frobenius transfer gives
   \(Q_n=K n!c^n n^\theta(1+O(n^{-1}))\) with \(K>0\).

None controls the arithmetic height of \(Q_n/n!\).  The missing converse is
therefore not a standard positivity theorem but the genuinely special claim

\[
 \boxed{
 \begin{array}{c}
 \text{a rational initial jet of the degree-(2,1) equation}\cr
 \text{that cancels the negative quadratic singularity}
 \end{array}
 \Longrightarrow E_N\leq C^N,}                       \tag{14}
\]

or an equally strong direct contradiction on the irrational-exponent locus.
Example (1) proves that the words “positive”, “integral”, and “holonomic”
cannot be substituted for the degree-(2,1) connection hypothesis in (14).

## References

- Y. André, [*Séries Gevrey de type arithmétique, I. Théorèmes de
  pureté et de dualité*](https://doi.org/10.2307/121045), Annals of
  Mathematics 151 (2000), 705--740.
- Y. André, [*Arithmetic Gevrey series and transcendence: a
  survey*](https://www.numdam.org/item/JTNB_2003__15_1_1_0/), Journal de
  Théorie des Nombres de Bordeaux 15 (2003), 1--10.
- G. Lepetit, [*Le théorème
  d'André--Chudnovsky--Katz*](https://arxiv.org/abs/2109.10239), for a
  self-contained account of the rational-exponent theorem for minimal
  G-operators.
