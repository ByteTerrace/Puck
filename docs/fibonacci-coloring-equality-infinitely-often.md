# Fibonacci-coloring equality infinitely often — conditional reduction

## Status

This note records a conditional route showing that the Fibonacci-coloring
upper bound is attained for infinitely many even alphabet sizes. The exact
golden arithmetic, return-coordinate reductions, concrete lower witnesses,
maximal-right-return mechanical estimate, and
`FibonacciRichPeriodClassification` are now kernel-checked. In particular,
the Lean development proves the exact asymptotic critical exponent of the
Fibonacci ruler construction without a combinatorial assumption.

Theorem 5.1 as a statement about the infimum over *all* balanced words is not
yet unconditional in Lean: the development does not formalize the full
minimal-subshift reduction and Hubert classification/converse needed to pass
from an arbitrary balanced word to this Fibonacci return analysis.

The three-cell return-lattice calculations below describe the remaining
general-balanced-word converse. Prose lemmas depending on the unformalized
Hubert reduction must not yet be read as kernel-closed results.

The notation follows Dvořáková and Pelantová,
[*An upper bound on asymptotic repetition threshold of balanced sequences via
colouring of the Fibonacci sequence*](https://doi.org/10.1016/j.tcs.2024.114490),
and Dvořáková, Opočenská, and Pelantová,
[*Asymptotic repetitive threshold of balanced
sequences*](https://doi.org/10.1090/mcom/3816).

## 1. The candidate values

Put

\[
 \tau=\frac{1+\sqrt5}{2},\qquad d=2\delta,\qquad
 H=2^{\delta-1}.
\]

Let \(N=N(H)\) be the unique integer such that

\[
 \tau^{N+1}\le H<\tau^{N+2}.
\]

The Fibonacci coloring \(\mathbf v_\delta\) has

\[
 E^*(\mathbf v_\delta)
 =1+\frac1{H\tau^{N-1}}.
\]

It is useful to remove the universal period scale \(H^{-2}\).  Define

\[
 C(H):=H^2\bigl(E^*(\mathbf v_\delta)-1\bigr)
      =\frac{H}{\tau^{N-1}}.
\]

Writing \(H=\tau^{N+1}x\), where \(1\le x<\tau\), gives

\[
 C(H)=\tau^2x\in[\tau^2,\tau^3).
\]

The first five values explain the pattern that motivated the reduction:

| \(d\) | \(H\) | \(C(H)\) | Fibonacci bound optimal? |
|---:|---:|---:|:---:|
| 2 | 1 | \(\tau^2\approx2.618034\) | yes |
| 4 | 2 | \(2\tau\approx3.236068\) | yes |
| 6 | 4 | \(4\) | no |
| 8 | 8 | \(8/\tau^2\approx3.055728\) | yes |
| 10 | 16 | \(16/\tau^3\approx3.777088\) | no |

They are separated exactly by

\[
 C_2:=2+\sqrt2\approx3.414214.
\]

## 2. Infinitely many powers lie below the gap

### Lemma 2.1

There are infinitely many integers \(m\ge0\) for which, with \(H=2^m\),

\[
 C(H)<2+\sqrt2.
\]

### Proof

The number \(\alpha=\log_\tau2\) is irrational.  Indeed, if
\(\alpha=p/q\in\mathbb Q\), then \(2^q=\tau^p\).  Taking the conjugation
\(\tau\mapsto-1/\tau\) in \(\mathbb Q(\sqrt5)\) would give
\(2^q=(-1/\tau)^p\), impossible in absolute value for \(p,q>0\).

Consequently the fractional parts \(\{m\alpha\}\) are dense in \([0,1]\).
The same is true after deleting any finite prefix of the orbit, so every
nonempty open interval is visited infinitely often.
For \(H=2^m\), the quotient

\[
 x(H)=\frac{H}{\tau^{N(H)+1}}
      =\tau^{\{m\alpha\}}
\]

(with the harmless endpoint convention at \(m=0\)) is therefore dense in
\([1,\tau]\).  Since

\[
 \tau^2<2+\sqrt2,
\]

the open interval

\[
 1<x<\frac{2+\sqrt2}{\tau^2}
\]

is nonempty and contains \(x(H)\) for infinitely many \(m\).  For all of
those \(m\), \(C(H)=\tau^2x(H)<2+\sqrt2\). \(\square\)

The same proof gives the stronger numerical form needed below.

### Corollary 2.2

There are infinitely many powers \(H=2^m\) for which

\[
 C(H)<\frac83.
 \tag{F}
\]

Indeed, \(\tau^2<8/3\), so replace \(2+\sqrt2\) by \(8/3\) in the
last open interval of the proof of Lemma 2.1. \(\square\)

## 3. The first Sturmian asymptotic-index gap

Cassaigne's Theorem 2.3 in
[*On extremal properties of the Fibonacci
word*](https://doi.org/10.1051/ita:2008003) says that a Sturmian word of slope
\([0;a_1,a_2,\ldots]\) satisfies

\[
 E^*(\mathbf u)
 =2+\limsup_{n\to\infty}[a_n;a_{n-1},\ldots,a_1].
\]

Set

\[
 r_n=[a_n;a_{n-1},\ldots,a_1].
\]

Then \(r_n=a_n+1/r_{n-1}\).

### Lemma 3.1 (first gap)

For every Sturmian word \(\mathbf u\), exactly one of the following holds:

1. its continued-fraction coefficients are eventually all \(1\), and
   \(E^*(\mathbf u)=2+\tau\);
2. \(E^*(\mathbf u)\ge3+\sqrt2\).

Equivalently, for the excess above one,

\[
 E^*(\mathbf u)-1=\tau^2
 \quad\hbox{or}\quad
 E^*(\mathbf u)-1\ge2+\sqrt2.
\]

### Proof

Let \(T=1+\sqrt2\), the positive fixed point of \(x\mapsto2+1/x\).
Suppose \(\limsup r_n<T\).  Then \(r_n<T\) for every sufficiently large
\(n\).

No such large \(n\) can have \(a_n\ge3\), since then \(r_n>3>T\).  If
\(a_n=2\), the inequality

\[
 2+\frac1{r_{n-1}}=r_n<T
\]

implies

\[
 r_{n-1}>\frac1{T-2}=1+\sqrt2=T,
\]

again a contradiction.  Hence \(a_n=1\) eventually.  The recurrence then
converges to \(\tau\), so \(E^*(\mathbf u)=2+\tau\).

Conversely, if the coefficients are not eventually all \(1\), the preceding
contrapositive gives \(\limsup r_n\ge T\), hence
\(E^*(\mathbf u)\ge2+T=3+\sqrt2\). \(\square\)

## 4. Return-lattice bounds

We first record the reduction that allows Hubert's recurrent classification to
be used for the infimum defining \(RT_B^*\).  This point matters because a
balanced word in that definition is not assumed recurrent.

### Lemma 4.0 (uniformly recurrent reduction)

For every infinite balanced word \(\mathbf z\), there is a uniformly recurrent
balanced word \(\mathbf v\) such that

\[
 \mathcal L(\mathbf v)\subseteq\mathcal L(\mathbf z),
 \qquad E^*(\mathbf v)\le E^*(\mathbf z).                 \tag{UR}
\]

If \(E^*(\mathbf z)<\infty\), then \(\mathbf v\) is aperiodic.  Its alphabet
may be a proper subset of the alphabet of \(\mathbf z\).

### Proof

Let \(X\) be the closure of the shift orbit of \(\mathbf z\) in the compact
one-sided full shift.  A standard minimal-subsystem argument gives a nonempty
closed shift-invariant subset \(M\subseteq X\) that contains no proper
nonempty closed shift-invariant subset.  Every point \(\mathbf v\in M\) is
uniformly recurrent: for a prefix \(w\) of \(\mathbf v\), the cylinder
\([w]\) meets every orbit in \(M\), and compactness supplies finitely many
inverse shifts of \([w]\) that cover \(M\).  Thus occurrences of \(w\), and
then of every factor of \(\mathbf v\), have bounded gaps.

Every factor of a point in \(X\) is a factor of \(\mathbf z\), so
\(\mathcal L(\mathbf v)\subseteq\mathcal L(\mathbf z)\).  Balance is inherited
by sublanguages.  The definition of \(E^*\) in terms of repetitions of
arbitrarily long factors is monotone under language inclusion, which proves
(UR).  Finally, if \(\mathbf v\) were periodic, its language would contain
arbitrarily high powers of its period.  Those powers would also occur in
\(\mathbf z\), forcing \(E^*(\mathbf z)=\infty\). \(\square\)

We may therefore apply Hubert's representation to \(\mathbf v\): every
uniformly recurrent aperiodic balanced word can be written

\[
 \mathbf v=\operatorname{colour}(\mathbf u,\mathbf y,\mathbf y'),
\]

where \(\mathbf u\) is Sturmian and \(\mathbf y,\mathbf y'\) are constant-gap
sequences of periods \(P,P'\).  Put \(D=PP'\).

Write

\[
 A_N=\begin{pmatrix}p_{N-1}&p_N\\q_{N-1}&q_N\end{pmatrix},\quad
 x_N=\frac{p_{N-1}+q_{N-1}}{p_N+q_N},\quad
 \delta_N=[a_{N+1};a_{N+2},\ldots].
\]

For \(0\le m<a_{N+1}\), the relevant return vectors are

\[
 \begin{split}
 \mathcal S(N,m)=\biggl\{(\ell,k)\in\mathbb N^2\setminus\{(0,0)\}:{}&
 A_N\begin{pmatrix}1&0\\m&1\end{pmatrix}
 \binom\ell k\equiv\binom00\pmod{\binom P{P'}},\\
 &|\ell(\delta_N-m)-k|<\delta_N-m+1\biggr\}.
 \end{split}
\]

Proposition 20 of Dvořáková--Opočenská--Pelantová gives

\[
 E^*(\mathbf v)-1=\limsup_{N\to\infty}\Phi_N,
 \qquad
 \Phi_N=\max_{\substack{0\le m<a_{N+1}\\(\ell,k)\in\mathcal S(N,m)}}
 \frac{1+m+x_N}{k+\ell m+\ell x_N}.
 \tag{RF}
\]

Thus the main issue is a uniform theorem about an evolving sequence of
index-\(D\) congruence lattices, not merely an estimate for one lattice.

### A stronger colored lifting conjecture (false)

For every such coloring,

\[
 D\bigl(E^*(\mathbf v)-1\bigr)
 \;\ge\;
 \min\bigl\{E^*(\mathbf u)-1,\,4\bigr\}.
 \tag{CL}
\]

In the range below \(4\), (CL) says that coloring cannot cross the first
Sturmian spectral gap after the natural determinant normalization.  It was a
natural strengthening, but Theorem 5.1 does not require it: Lemma 4.2 proves
the uniform constant needed here directly.

The conjecture is false.  Take the Sturmian slope

\[
 [0;10,\overline{1,3,3}]
\]

and coloring periods \(P=5\), \(P'=10\), so \(D=50\).  Concrete
constant-gap colorings are supplied by the five residue classes modulo \(5\)
on the first letter and by

\[
 0\bmod2,\quad1,3,5,7,9\bmod10
\]

on the second letter.  Thus this is an actual Hubert coloring on \(5+6=11\)
letters, not merely a congruence lattice.  Exact full-orbit evaluation gives

\[
 E^*(\mathbf v)-1=\frac{10-\sqrt{65}}{25},\qquad
 D(E^*(\mathbf v)-1)=20-2\sqrt{65}<4,       \tag{CLX1}
\]

whereas the uncolored excess is

\[
 E^*(\mathbf u)-1=\frac{11+\sqrt{65}}4>4.  \tag{CLX2}
\]

Therefore the right side of (CL) is \(4\), and (CLX1) disproves (CL).  The
same value remains above the theorem's required non-Fibonacci constant:

\[
 20-2\sqrt{65}>\frac83.
\]

The all-component search finds 36 congruence cycles; the minimizing component
has representative \((0,1;1,0)\) at directive phase zero and is reached by the
one-digit prefix \([10]\).  Its extremal phase is \((1,4;1,3)\) at phase two,
with return vector \((m,\ell,k)=(0,5,15)\).  The optimized evaluator and an
independent exhaustive phase loop reproduce (CLX1)--(CLX2), while exact-cover
search independently reconstructs both colorings.  These checks are permanent
regressions in [the Hubert-converse verifier](../tools/hubert-converse-verifier.cs);
the bounded discovery tool is
[the colored-lifting search](../tools/colored-lifting-conjecture-search.cs).

### Why a one-index proof did not work

The tempting strengthening

\[
 D\Phi_N\ge\min\{\Phi_N^{(P=P'=1)},4\}
 \tag{false}
\]

is false.  For example, evaluating (RF) for

\[
 [0;2,3,\overline{3,1,3}],\qquad P=7,\qquad P'=6
\]

gives a phase for which the ratio of the left side of (false) to its right side
is exactly

\[
 \frac{441-21\sqrt{65}}{376}<1.
\]

For the same example the ratio of the two limsups is

\[
 \frac{21+7\sqrt{65}}8>1.
\]

Thus this local example does not itself contradict (CL), but it proves that
no pointwise argument could have established it.  The full-orbit example
(CLX1)--(CLX2) above now disproves (CL).  These identities, and the
published extremal values for \(d=3,6,8,10\), are checked in exact quadratic
arithmetic by
[`tools/fibonacci-coloring-return-spectrum.cs`](../tools/fibonacci-coloring-return-spectrum.cs).
Run it with

```powershell
dotnet run -c Release -p:NuGetAudit=false --file tools\fibonacci-coloring-return-spectrum.cs
```

Even the immediate two-phase repair fails for this example.  If the colored
and uncolored maxima are each taken over two consecutive cyclic phases, the
least ratio is

\[
 \frac{273-21\sqrt{65}}{104}<1.
\]

Run the checker with `--counterexample-windows` for this additional exact
certificate.  Thus a proof has to use the whole recurrent congruence orbit,
not a pointwise or adjacent-phase comparison.

This remains true even if one asks only for the non-Fibonacci constant
\(2+\sqrt2\), rather than the full comparison (CL).  For the tail
\([0;\overline{1,2}]\), the digit-\(2\) phase with \(P=P'=1\) has value

\[
 1+\sqrt3<2+\sqrt2.
\]

With \((P,P')=(1,2)\), the maximum over that phase and its immediate
successor, after determinant normalization, is still only

\[
 \frac{6+2\sqrt3}{3}<2+\sqrt2,
\]

although the full normalized limsup is \(4+2\sqrt3\).  The flag
`--non-fibonacci-local-failures` checks all three identities exactly.  The
silver equality itself occurs in the intermediate \(m=1\) cell omitted by a
digit-only model; `--silver-intermediate-cell` certifies this coordinate
point.

### Exact divided-cell reformulation of the former (CL)

There is a useful coordinate-free form of this stronger conjecture.  Fix a
phase \((N,m)\), and put

\[
 R=Q_N,\qquad y=m+\frac{Q_{N-1}}{Q_N},\qquad
 t=\delta_N-m.
\]

Let \(c_1,c_2\) be the two columns of

\[
 A_N\begin{pmatrix}1&0\\m&1\end{pmatrix}.
\]

Thus \(|c_1|=Ry\), \(|c_2|=R\), where \(|(a,b)|=a+b\).
If \(\theta\) is the slope of the original Sturmian word, apply the fixed
linear map

\[
 (a,b)\longmapsto (X,Y)
 =\left(a+b,\frac{a-\theta b}{1+\theta}\right).       \tag{DC1}
\]

Its determinant is \(-1\).  The continued-fraction identity

\[
 \theta=\frac{p_N\delta_N+p_{N-1}}
               {q_N\delta_N+q_{N-1}}
\]

and unimodularity of \(A_N\) give, up to reversing the sign of the second
coordinate,

\[
 c_1\longmapsto
 \left(Ry,\frac{t}{R(t+y)}\right),\qquad
 c_2\longmapsto
 \left(R,-\frac1{R(t+y)}\right).             \tag{DC2}
\]

In particular, the determinant of the two displayed vectors is \(-1\), as
it must be.  For \(z=(\ell,k)^T\), equations (DC1)--(DC2) yield

\[
 X(z)=R(k+\ell y),\qquad
 Y(z)=\frac{\ell t-k}{R(t+y)}.                \tag{DC3}
\]

Consequently the Sturmian factor condition is exactly

\[
 |Y(z)|<h_1+h_2,\qquad
 h_1=\frac{t}{R(t+y)},\quad
 h_2=\frac1{R(t+y)},                           \tag{DC4}
\]

and the return quotient is

\[
 \frac{R(1+y)}{X(z)}.
\]

The image of \(\mathbb Z^2\) under (DC2) is therefore a unimodular lattice
with a divided-cell basis: both horizontal coordinates are positive and the
vertical coordinates have opposite signs.  The coloring congruences select
an index-\(D\) sublattice of it.  As \((N,m)\) ranges through the
continued-fraction phases, (DC2) ranges through the successive divided-cell
bases of one fixed irrational lattice, while the colored sublattice itself
remains fixed in the original Parikh coordinates.

Hence the former (CL) is equivalent to the following purely two-dimensional assertion.
For every index-\(D\) sublattice of a unimodular irrational lattice, the
limsup, over its successive divided cells, of

\[
 \frac{x_1+x_2}
 {\min\{X(z):z\text{ lies in the positive cell cone and }
                   |Y(z)|<h_1+h_2\}}
                                                               \tag{DCL}
\]

is at least \(D^{-1}\) times the corresponding ambient-lattice limsup,
with the latter truncated at \(4\).  Formula (DCL) also pinpoints why a
static homogeneous-minimum estimate is insufficient: the admissible region
is a one-sided cone and a moving sum-of-heights strip.  The required bound
must use the complete divided-cell orbit.

There is nevertheless a universal estimate stronger than the elementary
\(1/D\) bound whenever a genuine intermediate cell is present.  It is most
transparent in the divided-cell coordinates.

### Lemma 4.1 (finite-group mean-return bound)

Let \(\Phi_{N,m}\) denote the maximum in (RF) with \(m\) fixed.  With

\[
 y=m+x_N,\qquad t=\delta_N-m,
\]

one has

\[
 D\Phi_{N,m}
 \ge \frac{(1+y)(1+t)}{y+t}.                 \tag{MR}
\]

Consequently, if the continued-fraction coefficients are not eventually all
\(1\), then every coloring satisfies

\[
 D\bigl(E^*(\mathbf v)-1\bigr)\ge2.          \tag{NF2}
\]

### Proof

Fix \((N,m)\).  In the notation of (DC2), write

\[
 c_1=(x_1,h_1),\qquad c_2=(x_2,-h_2),
\]

where

\[
 x_1=Ry,\quad x_2=R,\quad
 h_1=\frac{t}{R(t+y)},\quad
 h_2=\frac1{R(t+y)}.                         \tag{28}
\]

The factor criterion (DC4) is the Parikh-vector criterion for a Sturmian
word on two letters whose frequencies are

\[
 f_1=\frac{h_2}{h_1+h_2}=\frac1{t+1},
 \qquad
 f_2=\frac{h_1}{h_1+h_2}=\frac{t}{t+1}.      \tag{29}
\]

Give these two letters weights \(x_1,x_2\).  Their mean weight is

\[
 \bar x=f_1x_1+f_2x_2
       =\frac{R(y+t)}{t+1}.                  \tag{30}
\]

The coloring congruence is the kernel of the surjective homomorphism

\[
 \mathbb Z^2\longrightarrow
 \mathbb Z/P\mathbb Z\times\mathbb Z/P'\mathbb Z,
 \qquad
 z\longmapsto
 A_N\begin{pmatrix}1&0\\m&1\end{pmatrix}z,
                                                               \tag{31}
\]

whose kernel has index \(D\).  The following elementary mean-return argument
avoids any assumption about the finite extension.  Take a prefix of length
\(n\) of a Sturmian word with the factor language in (29), and label its
\(n+1\) prefix boundaries by their images under (31).  For each of the at
most \(D\) labels, join consecutive occurrences.  Every resulting interval
is a nonempty factor with zero group increment.  There are at least
\(n+1-D\) such intervals.

For a fixed label these intervals are disjoint, so each letter of the prefix
is counted at most once for that label and at most \(D\) times over all
labels.  Their total weighted length is therefore at most \(D\) times the
weighted length of the prefix.  Since Sturmian letter frequencies exist,
some zero-increment factor has, as \(n\to\infty\), weight arbitrarily close
from above to

\[
 D\bar x.                                    \tag{32}
\]

Each such Parikh vector belongs to \(\mathcal S(N,m)\).  Taking the infimum
of their weights (equivalently, the maximum in (RF)) and using (DC3), (30),
and (32) gives

\[
 D\Phi_{N,m}
 \ge \frac{x_1+x_2}{\bar x}
 =\frac{(1+y)(1+t)}{y+t},
\]

which proves (MR).

If \(a_{N+1}\ge2\), choose \(m=1\).  Then \(y=1+x_N>1\) and
\(t=\delta_N-1>1\), so

\[
 \frac{(1+y)(1+t)}{y+t}-2
 =\frac{(y-1)(t-1)}{y+t}>0.                  \tag{33}
\]

For a non-eventually-Fibonacci directive there are infinitely many such
indices \(N\).  Taking the limsup in (RF) proves (NF2). \(\square\)

The constant \(2\) in (NF2) is already enough to remove every non-maximal
period pair on the favorable subsequence (F).  It does not by itself settle
maximal periods, because \(C(H)>\tau^2>2\).

The missing improvement comes from using the last two intermediate cells of
a digit together with the first cell of its successor.

### Lemma 4.2 (three-cell non-Fibonacci gap)

If the continued-fraction coefficients are not eventually all \(1\), then
every coloring of determinant \(D\) satisfies

\[
 D\bigl(E^*(\mathbf v)-1\bigr)\ge\frac83.    \tag{NF8}
\]

### Proof

Fix an index \(N\) with \(a=a_{N+1}\ge2\), and put

\[
 x=x_N,\qquad
 u=\delta_{N+1},\qquad
 s=a-1+x>1,\qquad
 r=1+\frac1u>1.                              \tag{34}
\]

Use coefficient coordinates for the last intermediate cell \(m=a-1\), and
let \(K\subset\mathbb Z^2\) be its congruence lattice.  It has index \(D\).
For \(z=(\ell,k)\), set

\[
 X=s\ell+k,\qquad e=r\ell-k.                 \tag{35}
\]

The linear map \((\ell,k)\mapsto(X,e)\) has absolute determinant \(r+s\),
so the image \(\Lambda\) of \(K\) has covolume

\[
 \det\Lambda=D(r+s).                         \tag{36}
\]

The same physical return vector has coefficient pairs

\[
 (\ell,k+\ell),\qquad(\ell,k),\qquad(k-\ell,\ell)                 \tag{37}
\]

in, respectively, the cells \((N,a-2)\), \((N,a-1)\), and
\((N+1,0)\).  Direct substitution in (RF) gives the three quotients

\[
 \frac{s}{X},\qquad \frac{s+1}{X},\qquad \frac{s+2}{X},           \tag{38}
\]

and the three factor strips

\[
 |e|<r+2,\qquad |e|<r+1,\qquad |e|<r.        \tag{39}
\]

Put \(q=3/8\), and in the \((X,e)\)-plane define the open rectangles

\[
 \mathcal R_j=
 \{(X,e): |X|<qD(s+j),\ |e|<r+2-j\},
 \qquad j=0,1,2.                             \tag{40}
\]

We first find a nonzero point of \(\Lambda\) in their union.  Let

\[
 \mathcal B_j=
 \{(X,e): |X|<\tfrac12qD(s+j),
                  |e|<\tfrac12(r+2-j)\}
\]

and take \(\mathcal B=\mathcal B_0\cup\mathcal B_2\).  Since the first
outer rectangle is narrower and taller than the second,

\[
 \begin{aligned}
 \operatorname{area}(\mathcal B)
 &=qDs(r+2)+qD(s+2)r-qDsr\\
 &=qD\bigl(rs+2r+2s\bigr).                   \tag{41}
 \end{aligned}
\]

Suppose

\[
 3rs>2(r+s).                                 \tag{42}
\]

Then (41) is strictly larger than \(D(r+s)\).  Blichfeldt's lemma supplies
two distinct points of \(\mathcal B\) whose difference belongs to
\(\Lambda\).  A difference of two points in \(\mathcal B_0\) lies in
\(\mathcal R_0\), a difference of two points in \(\mathcal B_2\) lies in
\(\mathcal R_2\), and a cross-difference lies in \(\mathcal R_1\), because

\[
 \frac{qDs+qD(s+2)}2=qD(s+1),\qquad
 \frac{(r+2)+r}{2}=r+1.                      \tag{43}
\]

Thus \(\Lambda\cap(\mathcal R_0\cup\mathcal R_1\cup\mathcal R_2)\)
contains a nonzero point.

It remains to check the positive factor cones, which is where integrality is
essential.  Such a point cannot have \(X=0\).  Indeed, then
\(k=-s\ell\) and \(|e|=(r+s)|\ell|\).  If \(a\ge3\), this exceeds
\(r+2\); if \(a=2\), the same holds for \(|\ell|\ge2\), while
\(|\ell|=1\) would force the noninteger \(s=1+x\) to be an integer.
After changing the overall sign, take \(X>0\).

For \(\mathcal R_0\), the inequalities \(X>0\), \(|e|<r+2\) force
\(\ell\ge0\) and \(k+\ell\ge0\).  Indeed, if
\(\ell=-n<0\), then \(X>0\) gives \(k>sn\), so
\(|e|=rn+k>n(r+s)\).  This is greater than \(r+2\) when
\(a\ge3\) or \(n\ge2\); in the only remaining case \(a=2,n=1\),
integrality gives \(k\ge2\), hence \(|e|\ge r+2\).  Once
\(\ell\ge0\), the alternative \(k+\ell<0\) forces
\(\ell\ge1\) and \(k\le-\ell-1\), so
\(e\ge(r+1)\ell+1\ge r+2\).

For \(\mathcal R_1\), if \(\ell=-n<0\), then \(k>sn>n\), hence
\(|e|=rn+k\ge r+1\).  If \(\ell\ge0\) but \(k<0\), then
\(\ell\ge1\) and \(e\ge r+1\).  Thus \(|e|<r+1\) forces
\(\ell,k\ge0\).  Finally, in \(\mathcal R_2\), a negative \(\ell\)
similarly gives \(|e|>r\), while \(k-\ell<0\) with \(\ell\ge0\)
forces \(\ell\ge1\) and
\(e\ge(r-1)\ell+1\ge r\).  Hence
\(\ell\ge0\) and \(k-\ell\ge0\).

Hence the three pairs in (37) are nonnegative in the rectangle assigned to
them.  Equations (38)--(40) therefore give, at one of these three cells,

\[
 \Phi>\frac{1}{qD}=\frac{8}{3D}.             \tag{44}
\]

Finally, (42) occurs infinitely often for every non-eventually-Fibonacci
directive.  If \(a_{N+1}\ge3\), then \(s>2\), and

\[
 3rs-2r-2s=r(3s-2)-2s>s-2>0.                \tag{45}
\]

If coefficients at least \(3\) occur only finitely often, the directive is
eventually over \(\{1,2\}\).  Since it is not eventually all \(1\), there
are infinitely many later indices with \(a_{N+1}=2\).  At each such index,

\[
 x=\frac1{a_N+x_{N-1}}>\frac13,qquad
 u=a_{N+2}+\frac1{\delta_{N+2}}<3.           \tag{46}
\]

For \(s=1+x\) and \(r=1+1/u\), condition (42) is equivalent to

\[
 u<\frac{1+3x}{1-x},                         \tag{47}
\]

which follows strictly from (46), since the right side is greater than
\(3\).  Thus (44) holds along infinitely many indices.  Taking the limsup
in (RF) proves (NF8). \(\square\)

The older floating-point search remains useful for exploration, but is not
part of the certificate.

### Exact reduction for a Fibonacci tail

Suppose that \(a_n=1\) from some point onward.  Then \(m=0\),
\(\delta_N=\tau\), and \(x_N\to1/\tau\).  On every recurrent congruence
phase, (RF) therefore has the limiting form

\[
 \Phi_N=max_{(\ell,k)\in\mathcal S_N}
          \frac{\tau^2}{\ell+k\tau},
 \qquad
 |\ell\tau-k|<\tau^2.                         \tag{Fib-RF}
\]

Moreover

\[
 A_{N+1}=A_NT,
 \qquad
 T=\begin{pmatrix}0&1\\1&1\end{pmatrix}.
\]

Consequently, for fixed \(P,P'\), an arbitrary finite preperiod affects only
the initial residue class of \(A_N\bmod(P,P')\); the tail visits a finite
cycle under right multiplication by \(T\).  Formula (Fib-RF) is thus an exact
finite congruence-orbit problem in \(\mathbb Z[\tau]\).  Indeed, for
\(\eta=\ell+k\tau\) and its conjugate \(\eta'=\ell+k(1-\tau)\), the strip
condition is \(|\tau\eta'|<\tau^2\).

The following argument handles every period and every residue component, not
only powers of two.

### Lemma 4.3 (Fibonacci lifting at arbitrary index)

If the slope has an eventual Fibonacci tail, then every coloring of determinant
\(D=PP'\) satisfies

\[
 D\bigl(E^*(\mathbf v)-1\bigr)\ge\tau^2.       \tag{FL}
\]

### Proof

Fix one recurrent Fibonacci phase, and let \(K\subset\mathbb Z^2\) be its
congruence lattice.  Since \(A_N\) is unimodular, \(K\) has index \(D\).
Identify \(z=(\ell,k)^T\) with

\[
 \eta=\ell+k\tau,
 \qquad N(\eta)=\eta\eta'=\ell^2+\ell k-k^2.
\]

First suppose \(D\ge7\).  Choose an integral basis matrix \(B\) for \(K\),
so \(|\det B|=D\).  The integral indefinite binary quadratic form

\[
 F(x,y)=N\bigl(B(x,y)^T\bigr)
\]

has discriminant \(5D^2\).  [Hurwitz's approximation
theorem](https://eudml.org/doc/157573) says that every
irrational number \(\xi\) has infinitely many rationals \(p/q\) with

\[
 \left|\xi-\frac pq\right|<\frac1{\sqrt5q^2}.
\]

Applied to either irrational zero of \(F(X,1)\), this gives

\[
 \inf_{(x,y)\in\mathbb Z^2\setminus\{0\}}|F(x,y)|
 \le\sqrt{\frac{5D^2}{5}}=D.                \tag{22}
\]

For completeness, if
\(F(x,y)=a(x-\xi y)(x-\xi' y)\), substitution of those approximants gives

\[
 \liminf |F(p,q)|
 \le\frac{|a|\,|\xi-\xi'|}{\sqrt5}
 =\sqrt{\frac{\operatorname{disc}F}{5}}.
\]

The form is integral and anisotropic over \(\mathbb Q\), so its nonzero
absolute values are positive integers; hence the displayed infimum is
attained.  Thus there is a nonzero \(\eta_0\in K\) with

\[
 q_0:=|N(\eta_0)|\le D.                     \tag{23}
\]

We next put a unit multiple into the positive coefficient cone.  Multiplying
by \(\tau\) multiplies the ratio

\[
 \rho(\eta):=\frac{\eta}{|\eta'|}
\]

by \(\tau^2\) and reverses the sign of \(\eta'\).  After an overall sign
change, a number \(\eta>0\) has nonnegative coefficients in the basis
\(1,\tau\) precisely when

\[
 \rho(\eta)\ge1\quad(\eta'>0),
 \qquad
 \rho(\eta)\ge\tau^2\quad(\eta'<0).         \tag{24}
\]

Choose the first unit multiple \(\eta_1=\pm\tau^j\eta_0\) that enters one
of these two cones.  Its predecessor fails the corresponding alternative in
(24), so in either parity

\[
 \rho(\eta_1)<\tau^4,
 \qquad \eta_1=\sqrt{q_0\rho(\eta_1)}<\tau^2\sqrt{q_0}.       \tag{25}
\]

Now multiply forward by \(\tau\), preserving nonnegative coefficients, until

\[
 |\eta'|<\tau.                              \tag{26}
\]

If no multiplication was necessary, (25) still holds.  Otherwise minimality
gives \(1\le|\eta'|<\tau\), and hence

\[
 \eta=\frac{q_0}{|\eta'|}\le q_0.
\]

Consequently the resulting nonnegative vector satisfies

\[
 \eta\le\max\{q_0,\tau^2\sqrt{q_0}\}\le D,            \tag{27}
\]

where the last inequality uses \(q_0\le D\) and
\(D\ge7>\tau^4\).

It remains only to relate the unit shifts to the correct congruence phase.
The Fibonacci relation \(A_{N+1}=A_NT\) gives
\(K_{N+1}=T^{-1}K_N\), while multiplication by \(\tau\) is \(T\) on
coefficient columns.  The phase orbit is finite and invertible, so every
positive or negative unit shift used above occurs again at a forward
recurrent phase.  At that phase, (26) is exactly

\[
 |\ell\tau-k|=|\tau\eta'|<\tau^2.
\]

The vector is therefore admissible in (Fib-RF), and (27) gives
\(\Phi_N\ge\tau^2/D\) along an infinite phase subsequence.

For \(1\le D\le6\), the remaining finite assertion can be checked without
an approximation bound.  Every index-\(D\) sublattice has the unique column
Hermite form

\[
 K=\langle(a,0)^T,(b,d)^T\rangle,
 \qquad ad=D,\quad0\le b<a.
\]

There are \(\sigma(D)\) such forms.  The matrix \(T\) permutes them, so the
first \(\sigma(D)\) shifts contain a complete phase orbit.  Exhausting the
nonnegative pairs with \(\ell+k\tau\le D\) gives the following exact
certificate summary; every listed pair also satisfies the strict Fibonacci
strip.

| \(D\) | HNF lattices | largest required shift | witness pairs occurring |
|---:|---:|---:|:---|
| 1 | 1 | 0 | \((1,0)\) |
| 2 | 3 | 1 | \((1,0),(0,1)\) |
| 3 | 4 | 1 | \((1,0),(0,1),(1,1)\) |
| 4 | 7 | 1 | \((1,0),(0,1),(1,1),(0,2)\) |
| 5 | 6 | 1 | \((1,0),(0,1),(1,1),(2,1),(1,2)\) |
| 6 | 12 | 1 | \((1,0),(0,1),(1,1),(2,1),(1,2),(1,3),(0,2),(2,2)\) |

The [full 33-row certificate](certificates/fibonacci-small-index-lattices.md)
lists the shift and witness for every HNF.  The program
`tools/fibonacci-coloring-return-spectrum.cs` regenerates that table and
verifies both surd inequalities exactly using `QuadraticSurd`; no
floating-point comparison enters the certificate.  Thus (27), and hence
(FL), also holds for these six indices. \(\square\)

The stronger separation needed for the equality classification can now be
proved for maximal power-of-two periods.

### Lemma 4.4 (asymmetric maximal-period separation)

Let the slope have an eventual Fibonacci tail, and let

\[
 P=2^{r-1},\qquad P'=2^{s-1},\qquad r+s=2\delta.
\]

If \(r\ne s\), then every congruence component satisfies

\[
 PP'\bigl(E^*(\mathbf v)-1\bigr)>8.       \tag{MP}
\]

This includes every finite continued-fraction preperiod.

### Proof

Interchange the two colors if necessary and write

\[
 P=h,\qquad P'=hR,\qquad R=2^t.
\]

Because \(r+s\) is even and \(r\ne s\), the integer \(t=s-r\) is even and
at least \(2\).

For a recurrent phase let the rows of \(A_N\) be \(u_N\) and \(v_N\).
The matrix \(A_N\) is unimodular.  Hence the congruences for a return vector
\(z\) are equivalent to

\[
 z=h w,\qquad v_Nw\equiv0\pmod R.          \tag{12}
\]

Indeed, the original two congruences imply \(A_Nz\equiv0\pmod h\), hence
\(z\equiv0\pmod h\); after writing \(z=hw\), only the second congruence
modulo \(R\) remains.

Identify a column \((a,b)^T\) with \(\alpha=a+b\tau\in\mathbb Z[\tau]\).
Right multiplication by

\[
 T=\begin{pmatrix}0&1\\1&1\end{pmatrix}
\]

is multiplication of \(\alpha\) by \(\tau\).  Its absolute algebraic norm

\[
 |N(\alpha)|=|a^2+ab-b^2|
\]

is therefore invariant under the phase action.

We need the following elementary description of that action.  On
\(\mathbb P^1(\mathbb Z/2^t\mathbb Z)\), the action of \(T\) has one orbit
when \(t=2\), represented by \((1,0)^T\).  When \(t\ge3\), it has exactly
two orbits, represented by

\[
 e_1=(1,0)^T,\qquad e_5=(2,1)^T,           \tag{13}
\]

whose absolute norms are \(1\) and \(5\).

Here are the details.  The projective line has \(3\cdot2^{t-1}\) points.
Using

\[
 T^j=\begin{pmatrix}F_{j-1}&F_j\\F_j&F_{j+1}\end{pmatrix},qquad
 \det(w,T^jw)=F_jN(w),                     \tag{14}
\]

and the fact that \(N(w)\) is odd for every primitive \(w\), a projective
point is fixed by \(T^j\) exactly when \(2^t\mid F_j\).  The elementary
2-adic Fibonacci valuation

\[
 \min\{j>0:2^t\mid F_j\}=
 \begin{cases}
  6,&t=2,\\
  3\cdot2^{t-2},&t\ge3
 \end{cases}                                \tag{15}
\]

follows from the elementary valuation formula

\[
 v_2(F_j)=
 \begin{cases}
 0,&3\nmid j,\\
 1,&j\equiv3\pmod6,\\
 v_2(j)+2,&6\mid j.
 \end{cases}
\]

Indeed, the Fibonacci recurrence modulo \(4\) gives the first two cases, and
the recurrence modulo \(16\) gives \(v_2(F_j)=3\) when
\(j\equiv6\pmod{12}\).  Write an index divisible by \(6\) as
\(j=6q2^e\) with \(q\) odd.  Starting at \(6q\), the identity
\(F_{2j}=F_jL_j\) and \(L_j\equiv2\pmod4\) for \(6\mid j\) increase the
valuation by exactly one at each of the \(e\) doublings.  This gives
\(v_2(F_j)=3+e=v_2(j)+2\), and hence (15).
Thus all projective orbits have size \(6\) for \(t=2\), and size
\(3\cdot2^{t-2}\) for \(t\ge3\).  There is consequently one orbit in the
first case and two in the second.  The two vectors in (13) lie in distinct
orbits: multiplication by \(T\) negates the norm, while projective scaling
multiplies it by an odd square, which is \(1\pmod8\); the norm classes
\(\{1,7\}\) and \(\{3,5\}\) are disjoint.

The kernel in (12) is a projective point.  Equations (13)--(15) show that at
some phase it contains a vector \(w_0\) with positive embedding
\(\alpha_0\), where

\[
 q:=|N(\alpha_0)|=
 \begin{cases}
  1,&t=2,\\
  1\text{ or }5,&t\ge3.
 \end{cases}                                \tag{16}
\]

If it contains \(w_0\) at phase \(i\), it contains \(T^nw_0\) at phase
\(i-n\).  Choose the least \(n\ge0\) for which, with
\(\alpha_n=\tau^n\alpha_0\),

\[
 h|\alpha_n'|<\tau.                         \tag{17}
\]

The tail phase orbit modulo \((h,hR)\) is finite and invertible.  Hence the
possibly backward phase \(i-n\) is congruent to a forward phase and recurs
infinitely often; this is also why an arbitrary finite preperiod has no
effect on the argument.

The coordinates of \(T^nw_0\) are nonnegative.  Condition (17) is exactly
the Fibonacci strip condition for \(z=hT^nw_0\), because
\(\tau(a+b(1-\tau))=a\tau-b\).  Minimality of \(n\) gives

\[
 \alpha_n\le h|\alpha_0\alpha_0'|=hq;       \tag{18}
\]

for \(n=0\) the same inequality is immediate for the two representatives in
(13).  Substitution of this admissible vector in (Fib-RF) yields

\[
 PP'\Phi_N
 \ge \frac{R\tau^2}{q}.
\]

If \(t=2\), this is \(4\tau^2>8\).  If \(t\ge3\), then evenness of \(t\)
gives \(R\ge16\), and the right side is at least
\(16\tau^2/5>8\).  Taking the limsup proves (MP). \(\square\)

For completeness, we now prove rather than quote the exact value in the
symmetric case.  This is the point at which Fibonacci--Ostrowski (equivalently,
Zeckendorf) numeration enters: consecutive Fibonacci denominators are the
successive best nonnegative approximants to \(\tau\).

### Lemma 4.5 (symmetric Fibonacci value)

Suppose the slope has an eventual Fibonacci tail and \(P=P'=H=2^m\).  Let
\(N=N(H)\) be defined by

\[
 \tau^{N+1}\le H<\tau^{N+2}.
\]

Then, independently of the finite preperiod,

\[
 E^*(\mathbf v)-1=\frac1{H\tau^{N-1}}.      \tag{19}
\]

### Proof

Unimodularity of \(A_j\) makes the two congruences modulo \(H\) equivalent to
\(z\in H\mathbb Z^2\) at every phase.  Once the Fibonacci tail begins, write

\[
 (\ell,k)=H(\lambda,\kappa),\qquad
 c=\frac{\tau^2}{H}.
\]

The strip condition in (Fib-RF) is

\[
 |\lambda\tau-\kappa|<c.                    \tag{56}
\]

Although the backward quotient \(x_j\) retains the finite preperiod, it tends
to \(1/\tau\).  Hence (RF) gives

\[
 E^*(\mathbf v)-1=
 \frac{\tau^2}{H M_H},\qquad
 M_H=\min\left\{\lambda+\kappa\tau:
 \begin{array}{l}
 \lambda,\kappa\in\mathbb N,\quad \lambda+\kappa>0,\\[-2mm]
 |\lambda\tau-\kappa|<c
 \end{array}\right\}.                       \tag{57}
\]

To justify passing the maximum through the limit, use any fixed admissible
pair in (56).  For all sufficiently large \(j\), a maximizing pair has
\(\kappa+\lambda x_j\) no larger than a fixed constant determined by that
pair.  Since \(x_j\) stays bounded away from zero, only finitely many
nonnegative pairs can maximize.  The maximum therefore converges to the
finite maximum displayed in (57).

Assume first that \(N\ge1\).  Since \(H\) is an integer while
\(\tau^{N+1}\) is irrational,

\[
 \tau^{-N}<c<\tau^{1-N}.                    \tag{58}
\]

We use the following elementary best-approximation fact.  If nonnegative
integers \(\kappa,\lambda\), not both zero, satisfy

\[
 |\kappa-\tau\lambda|<c
 \quad\hbox{and}\quad
 \tau^{-N}<c<\tau^{1-N},
\]

then

\[
 \kappa\ge F_{N+1},\qquad \lambda\ge F_N.  \tag{59}
\]

Here is a short proof.  Put

\[
 x=F_N\kappa-F_{N+1}\lambda,qquad
 y=F_{N-1}\kappa-F_N\lambda.
\]

Cassini's identity gives

\[
 (-1)^N\kappa=yF_{N+1}-xF_N,qquad
 (-1)^N\lambda=yF_N-xF_{N-1}.              \tag{60}
\]

If \(x=0\), coprimality makes \((\kappa,\lambda)\) a positive multiple of
\((F_{N+1},F_N)\).  If \(y=0\), its error is a positive multiple of
\(|F_N-\tau F_{N-1}|=\tau^{1-N}>c\), so it is not admissible.  If
\(xy<0\), the two terms in each identity (60) have the same sign, immediately
giving (59).  Finally, if \(xy>0\), the consecutive errors
\(F_{N+1}-\tau F_N\) and \(F_N-\tau F_{N-1}\) have opposite signs, and (60)
gives

\[
 |\kappa-\tau\lambda|
 =|y|\tau^{-N}+|x|\tau^{1-N}>c,
\]

a contradiction.  This proves (59).

The pair

\[
 (\lambda,\kappa)=(F_N,F_{N+1})
\]

is admissible by the left inequality in (58), and

\[
 |F_N\tau-F_{N+1}|=\tau^{-N},qquad
 F_N+F_{N+1}\tau=\tau^{N+1}.               \tag{61}
\]

Equation (59) shows that no admissible pair has smaller weight.  Thus
\(M_H=\tau^{N+1}\).

The two initial cases are direct.  If \(N=-1\), then \(H=1\) and the least
pair is \((\lambda,\kappa)=(1,0)\), so \(M_H=1=\tau^{N+1}\).  If \(N=0\),
then \(H=2\); the only positive pair of weight below \(\tau\) is \((1,0)\),
which fails (56), while \((0,1)\) is admissible.  Hence again
\(M_H=\tau=\tau^{N+1}\).  Substitution in (57) proves (19). \(\square\)

As an independent bounded regression certificate, the return-spectrum checker
constructs `QuadraticOstrowskiSystem` at \(\tau=[\overline1]\), verifies (57)
for \(H=1,2,\ldots,256\) through the powers of two, and uses
`PellEquation.ResidueCycle` with \(\tau^6=9+4\sqrt5\) to check the exact
\(\mathbb Z[\tau]\)-to-Pell phase adapter modulo \(4,8,\ldots,256\).  The
unbounded proof is (58)--(61); these computations guard its arithmetic and
coordinate conventions.

Thus the Fibonacci equality analysis is complete for colorings in which both
constant-gap periods attain their individual bounds.  It remains to quantify
the loss from a non-maximal period.

### Lemma 4.6 (constant-gap period stability)

If a constant-gap sequence on \(r\ge2\) letters has least period \(P\), then

\[
 P=2^{r-1}
 \quad\hbox{or}\quad
 P\le3\cdot2^{r-3}.                         \tag{20}
\]

### Proof

The occurrences of the letters form a disjoint covering of \(\mathbb Z\) by
\(r\) arithmetic progressions, and the least common multiple of their moduli
is \(P\).  Corollary 2 of Simpson,
[*Regular coverings of the integers by arithmetic
progressions*](https://eudml.org/doc/205961), gives

\[
 r\ge f(P)+1,
 \qquad
 f(P)=\sum_{p^a\parallel P}a(p-1).          \tag{21}
\]

For every prime \(p\), \(p\le2^{p-1}\).  Multiplying this inequality over
the prime factors of \(P\), with multiplicity, gives

\[
 P\le2^{f(P)}\le2^{r-1}.
\]

Equality throughout is possible only when every prime factor is \(2\) and
\(f(P)=r-1\), which gives \(P=2^{r-1}\).  Otherwise, if
\(f(P)\le r-2\), then \(P\le2^{r-2}<3\cdot2^{r-3}\).  If
\(f(P)=r-1\) but equality fails, some odd prime occurs; for every odd prime
\(p\),

\[
 p\le3\cdot2^{p-3}=\frac34\,2^{p-1}.
\]

Using this sharpened inequality for one odd prime factor and
\(q\le2^{q-1}\) for all remaining factors gives
\(P\le\frac34\,2^{f(P)}=3\cdot2^{r-3}\). \(\square\)

Applying Lemma 4.6 to the two coloring periods (with the one-letter case
\(P=1\) understood trivially) proves the promised product gap:

\[
 (P,P')\ne(2^{r-1},2^{s-1})
 \quad\Longrightarrow\quad
 PP'\le\frac34H^2.                          \tag{PD}
\]

## 5. Fibonacci-coloring equality infinitely often

### Theorem 5.1

Let \(\delta\ge1\), put \(H=2^{\delta-1}\), and let \(N=N(H)\) be defined
by

\[
 \tau^{N+1}\le H<\tau^{N+2}.
\]

If

\[
 C(H)=\frac{H}{\tau^{N-1}}<\frac83,
\]

then

\[
 RT_B^*(2\delta)
 =1+\frac1{H\tau^{N-1}}.                    \tag{48}
\]

Consequently the Fibonacci-coloring upper bound is attained for infinitely
many even alphabet sizes.

### Proof

The Fibonacci coloring \(\mathbf v_\delta\) supplies the upper bound in
(48).  It remains to prove the matching lower bound for an arbitrary balanced
word \(\mathbf z\) on the prescribed \(d=2\delta\) letters.  We may suppose
\(E^*(\mathbf z)<\infty\).  Lemma 4.0 supplies a uniformly recurrent
aperiodic balanced word \(\mathbf v\) with

\[
 \mathcal L(\mathbf v)\subseteq\mathcal L(\mathbf z),
 \qquad E^*(\mathbf z)\ge E^*(\mathbf v).
\]

Let \(d_0\le d\) be the number of letters that actually occur in
\(\mathbf v\).  Hubert's theorem writes

\[
 \mathbf v=\operatorname{colour}(\mathbf u,\mathbf y,\mathbf y')
\]

on \(d_0\) letters.  Write \(r,s\) for the alphabet sizes of the two
constant-gap colorings, \(P,P'\) for their least periods, and \(D=PP'\).
Then \(r+s=d_0\), and the constant-gap period bound gives

\[
 P\le2^{r-1},\qquad P'\le2^{s-1},\qquad
 D\le2^{d_0-2}\le H^2.                      \tag{49}
\]

If \(d_0<d\), then \(D\le H^2/2\).  Lemma 4.2 in the non-Fibonacci case
and Lemma 4.3 in the eventual-Fibonacci case respectively give

\[
 H^2(E^*(\mathbf v)-1)\ge\frac{16}{3}
 \quad\hbox{or}\quad
 H^2(E^*(\mathbf v)-1)\ge2\tau^2.
\]

Both bounds are strictly larger than \(C(H)<8/3\).  Hence every word whose
uniformly recurrent reduction loses a letter already lies strictly above the
candidate.  We may therefore assume from now on that \(d_0=d=2\delta\), so
that the last inequality in (49) has the scale required below.

If the Sturmian directive is not eventually Fibonacci, Lemma 4.2 gives

\[
 H^2\bigl(E^*(\mathbf v)-1\bigr)
 \ge \frac{8H^2}{3D}
 \ge\frac83>C(H).                           \tag{50}
\]

Thus a word meeting or improving the candidate value must have an eventual
Fibonacci directive.  Lemma 4.3 then gives

\[
 H^2\bigl(E^*(\mathbf v)-1\bigr)
 \ge \frac{\tau^2H^2}{D}.                   \tag{51}
\]

If either coloring period is non-maximal, (PD) says
\(D\le3H^2/4\); hence

\[
 H^2\bigl(E^*(\mathbf v)-1\bigr)
 \ge\frac{4\tau^2}{3}>\frac83>C(H).         \tag{52}
\]

Both coloring periods must therefore be maximal:

\[
 P=2^{r-1},\qquad P'=2^{s-1},\qquad PP'=H^2. \tag{53}
\]

If \(r\ne s\), Lemma 4.4 strengthens (51) to

\[
 H^2\bigl(E^*(\mathbf v)-1\bigr)>8>C(H).    \tag{54}
\]

The only remaining case is \(r=s=\delta\) and \(P=P'=H\).  In this case
Lemma 4.5, including its finite-preperiod argument, gives

\[
 E^*(\mathbf v)-1=\frac1{H\tau^{N-1}}.     \tag{55}
\]

Together with the matching Fibonacci construction, this proves (48):

\[
 RT_B^*(2\delta)
 =1+\frac1{H\tau^{N-1}}.
\]

Finally, Corollary 2.2 supplies infinitely many powers \(H=2^{\delta-1}\)
with \(C(H)<8/3\), and hence infinitely many even dimensions
\(d=2\delta\) satisfying (48). \(\square\)
