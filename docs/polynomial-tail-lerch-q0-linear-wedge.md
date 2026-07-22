# Fixed linear wedges in the `q=0` Lerch slice

## Result

Consider the positive Lerch tail

\[
 s_n=pn+\frac{r(n+1)^2}{s_{n+1}},
 \qquad p,r\in\mathbf Z_{\geq1}.
 \tag{1}
\]

The shifted exact norm-gap argument already excludes the scaled-BDS region
`1 <= r <= p`.  The following elementary result doubles that linear wedge;
the finite reduction it exposes gives a checked fortyfold extension later in
this note.

### Theorem

If

\[
 p<r\leq2p,
 \tag{2}
\]

then \(s_1\notin\mathbf Z\).

The statement does not require the characteristic discriminant
\(p^2+4r\) to be nonsquare. This is the analytic first step; the finite
certificate below ultimately moves the residual boundary to \(r>40p\).

The square-characteristic part of the slice can in fact be excluded for all
\(r\), independently of (2), by Baker's theorem; see the final section.

The proof is a uniform exact orbit certificate of depth at most seven.  It
does not approximate the continued fraction or its Lerch value.

## Cleared orbit

Suppose \(s_1=M\in\mathbf Z\), and put \(d=M-p\).  Positivity of (1) gives
\(d\in\mathbf Z_{>0}\).  The usual cleared forward orbit is

\[
 Q_0=1,\qquad Q_1=d,\qquad
 Q_n=rn^2Q_{n-2}-pnQ_{n-1}\quad(n\geq2).
 \tag{4}
\]

Exact equality with the positive infinite tail would force

\[
 Q_n>0\qquad\text{for every }n\geq0.
 \tag{5}
\]

The first term is

\[
 Q_2=4r-2pd.
 \tag{6}
\]

Under \(r\leq2p\), (5)--(6) imply \(d<4\).  Hence only

\[
 d\in\{1,2,3\}
 \tag{7}
\]

can occur.

## The seeds `d=1` and `d=2`

For \(d=1\),

\[
 Q_3=3\bigl(2p^2-(4p-3)r\bigr).
 \tag{8}
\]

If \(p\geq2\), then \(r>p\) makes the expression in parentheses strictly
smaller than \(p(3-2p)\), hence negative.  If \(p=1\), condition (2) forces
\(r=2\), and (8) is zero.  Thus `d=1` is impossible.

For \(d=2\),

\[
 Q_3=6\bigl(2p^2-(2p-3)r\bigr).
 \tag{9}
\]

If \(p\geq7\) and \(Q_3>0\), then

\[
 r<\frac{2p^2}{2p-3}
   =p+\frac{3p}{2p-3}<p+2.
\]

Thus integrality forces \(r=p+1\).  Direct substitution gives

\[
 Q_4=8(-3p^2-p+8)<0.
 \tag{10}
\]

It remains only to inspect \(1\leq p\leq6\).  Equation (9), together with
\(p<r\leq2p\), leaves

\[
\begin{split}
 &(1,2),\\
 &(2,3),(2,4),\\
 &(3,4),(3,5),\\
 &(4,5),(4,6),\\
 &(5,6),(5,7),\\
 &(6,7).
\end{split}
\]

All but

\[
 (p,r)=(1,2),(2,4),(3,5),(4,6),(5,7)
 \tag{11}
\]

already have \(Q_4\leq0\).  For the last five seeds the orbit prefixes are

\[
\begin{array}{c|rrrrrrr}
(p,r)&Q_0&Q_1&Q_2&Q_3&Q_4&Q_5&Q_6\\ \hline
(1,2)&1&2&4&24&32&1040&-3936\\
(2,4)&1&2&8&24&320&-800&55680\\
(3,5)&1&2&8&18&424&-4110&150300\\
(4,6)&1&2&8&12&576&-9720&357696\\
(5,7)&1&2&8&6&776&-18350&746052
\end{array}
\]

so `d=2` is impossible as well.

## The seed `d=3`

Put

\[
 k=2r-3p.
 \tag{12}
\]

Now \(Q_2=2k>0\), so \(k\) is a positive integer.  The upper bound
\(r\leq2p\) gives \(k\leq p\), and integrality of \(r=(3p+k)/2\) gives
\(k\equiv p\pmod2\).  The next two terms are

\[
 Q_3=\frac32\bigl(27p-k(4p-9)\bigr),
 \tag{13}
\]

and

\[
 Q_4=2\bigl((12k-81)p^2-3pk+8k^2\bigr).
 \tag{14}
\]

If \(k\leq6\), then the expression in (14) is at most

\[
 -9p^2-3pk+8k^2\leq-p^2-3pk<0,
\]

because \(k\leq p\).  Hence positivity requires \(k\geq7\).  Equation
(13) then gives

\[
 (4k-27)p<9k.
 \tag{15}
\]

Since \(p\geq k\), (15) implies \(4k-27<9\), and therefore

\[
 k\in\{7,8\}.
 \tag{16}
\]

For \(k=7\), parity makes \(p\) odd, while (13) says \(7\leq p<63\).
An expansion of the next term gives

\[
 4Q_5=-120p^3+615p^2-2030p+33075.
 \tag{17}
\]

The polynomial in (17) is strictly decreasing: its derivative has negative
leading coefficient and discriminant

\[
 1230^2-4\cdot360\cdot2030<0.
\]

At \(p=9\) it is already negative.  Thus the only possible value is
\(p=7\), which gives \(r=14\).  Its orbit is

\[
 (Q_0,\ldots,Q_7)
 =(1,3,14,84,784,1960,312816,-13983424).
 \tag{18}
\]

For \(k=8\), parity makes \(p\) even and \(p\geq8\); equation (13) also
gives \(p<72/5\).  Here

\[
 4Q_5=-600p^3-165p^2-7280p+43200,
 \tag{19}
\]

which is strictly decreasing for positive \(p\) and is already negative at
\(p=8\).  This eliminates the last branch.  Equations (8)--(19) contradict
(5) in every case, proving the theorem. \(\square\)

## Verification and limitation

The standalone file app
[`polynomial-tail-lerch-q0-linear-wedge-verifier.cs`](../tools/polynomial-tail-lerch-q0-linear-wedge-verifier.cs)
recomputes the integer orbit for every \(p\) through a supplied bound and
checks that all candidates in \(p<r\leq2p\) fail by index seven.  For example:

```text
dotnet run --property:NuGetAudit=false tools/polynomial-tail-lerch-q0-linear-wedge-verifier.cs -- 2000
```

The proof above, not the finite scan, supplies uniformity in \(p\).

The depth-seven result does not decide the full `q=0` slice. Delayed-failure examples
from [`polynomial-tail-lerch-delayed-failure.md`](polynomial-tail-lerch-delayed-failure.md)
have \(r/p\) growing with the requested delay, so they do not contradict the
uniform depth-seven theorem on the bounded linear wedge. The finite reduction
below pushes the checked boundary farther without changing that limitation.

## A finite reduction on every fixed linear wedge

The substitution used for `d=3` has a useful general consequence.  For an
arbitrary positive integer candidate \(d\), put

\[
 k=2r-pd,
 \qquad
 \delta=4k-3d^2.
 \tag{20}
\]

Then \(Q_2=2k\), and direct expansion gives

\[
 Q_3=\frac32\bigl(-\delta p+3dk\bigr),
 \qquad
 Q_4=2\bigl(3\delta p^2-dkp+8k^2\bigr).
 \tag{21}
\]

These two inequalities already bound \(p\) in terms of \(d\), independently
of \(r\).  Define

\[
 k_0=\left\lfloor\frac{3d^2}{4}\right\rfloor+1.
 \tag{22}
\]

If \(\delta>0\), positivity of \(Q_3\) gives

\[
 p<\frac{3dk}{4k-3d^2}\leq3dk_0.
 \tag{23}
\]

The last inequality follows because \(k/(4k-3d^2)\) decreases for
\(k>3d^2/4\), and its first integral argument is \(k_0\).  If \(\delta=0\),
then \(Q_4>0\) gives \(p<8k/d=6d<3dk_0\).  Finally, if \(\delta<0\),
positivity of \(Q_4\) gives

\[
 p^2<\frac{8k^2}{-3\delta},
 \]

which is again strictly smaller than the bound in (23), using
\(k<3d^2/4\).  Consequently

\[
 \boxed{
 Q_0,\ldots,Q_4>0
 \quad\Longrightarrow\quad
 p<3d\left(\left\lfloor\frac{3d^2}{4}\right\rfloor+1\right).
 }
 \tag{24}
\]

For any fixed real \(C>0\), the additional constraint \(r\leq Cp\) makes
\(d<2C\) by \(Q_2>0\).  Equation (24) then bounds \(p\), and
\(k=2r-pd\) is bounded as well.  Thus every fixed linear wedge

\[
 r\leq Cp
 \tag{25}
\]

reduces after only four orbit inequalities to an explicit finite set of
integer seeds.  This is not by itself a proof that every such finite set is
empty: a surviving seed still poses the original positivity question.
It does show structurally that any sequence of increasingly large candidate
seeds surviving four steps must have \(r/p\to\infty\).  The unbounded ratio
in the delayed-failure construction is therefore forced, rather than an
artifact of that construction.

## A certified fortyfold wedge

Specializing the preceding reduction to \(C=40\) gives an exact finite
certificate for an infinite coefficient region.

### Finite-certificate theorem

If

\[
 p,r\in\mathbf Z_{\geq1},\qquad r\leq40p,
\]

then the positive tail (1) satisfies \(s_1\notin\mathbf Z\).

Indeed, an integer proposal has \(1\leq d\leq79\) by \(d<80\). For each such
\(d\), (24) leaves only

\[
 1\leq p<3d\left(\left\lfloor\frac{3d^2}{4}\right\rfloor+1\right).
 \tag{25a}
\]

Writing \(k=2r-pd\), the remaining exact conditions are

\[
 \begin{gathered}
 k>0,\qquad k\equiv pd\pmod2,\qquad k\leq p(80-d),\\
 k(4p-3d)<3d^2p\quad\text{when }4p>3d,                 \tag{25b}\\
 8k^2+(12p^2-dp)k-9d^2p^2>0.                           \tag{25c}
 \end{gathered}
\]

Equation (25b) is \(Q_3>0\), and (25c) is \(Q_4/2>0\). The quadratic in
(25c) has positive leading coefficient and negative constant term, hence
exactly one positive root. Its positive integral interval can therefore be
found by exact binary search without skipping a possible seed.

The standalone exact verifier
[`polynomial-tail-lerch-q0-fixed-wedge-verifier.cs`](../tools/polynomial-tail-lerch-q0-fixed-wedge-verifier.cs)
implements (25a)--(25c). Exactly \(35,501,112,405\) parity-compatible seeds
survive \(Q_3\), but only \(21,159,528\) survive \(Q_4\). Direct integer
recurrence proves that all of the latter fail by \(Q_{46}\). The unique seed
attaining that last index is

\[
 (p,r,d)=(1,37,9),\qquad Q_0,\ldots,Q_{45}>0,\quad Q_{46}<0. \tag{25d}
\]

The verifier embeds the counts, unique extremal seed, and failure index as a
regression fingerprint. The reproducing command is

```powershell
dotnet run --property:NuGetAudit=false tools/polynomial-tail-lerch-q0-fixed-wedge-verifier.cs -- 40 1 64
```

No floating-point calculation, tail approximation, coefficient cutoff, or
main-project build enters this certificate. The constants \(40\) and \(46\)
are the proved endpoint and maximum failure depth of this particular finite
certificate; they are not asserted to be optimal.

## The entire square-characteristic branch is transcendental

There is also a complete classification of the rational-slope part of this
`q=0` slice.  Suppose

\[
 D=\sqrt{p^2+4r}\in\mathbf Z.
 \tag{26}
\]

Since \(D\) and \(p\) have the same parity, write

\[
 k=\frac{D-p}{2}\in\mathbf Z_{>0}.
\]

Then

\[
 r=k(p+k),\qquad
 t=\frac{k}{p+k}\in\mathbf Q,
 \qquad
 a=\frac{k}{p+2k}\in\mathbf Q,
 \tag{27}
\]

and the exact Lerch formula from
[`polynomial-tail-lerch-arithmetic.md`](polynomial-tail-lerch-arithmetic.md)
becomes

\[
 s_1=k\Phi(-t,1,a).
 \tag{28}
\]

Write \(a=A/B\) in lowest terms.  Here \(0<A<B\).  The Euler integral and
the substitution \(u=x^B\) give

\[
 \Phi(-t,1,A/B)
 =B\int_0^1\frac{x^{A-1}}{1+t x^B}\,dx.
 \tag{29}
\]

The integrand in (29) is a proper rational function over the algebraic
numbers, with distinct algebraic poles and no pole on \([0,1]\).  Partial
fractions therefore express (29) as an algebraic linear combination of
chosen logarithms of nonzero algebraic numbers.  The value is nonzero--in
fact positive--because the integrand is positive on \((0,1)\).  Baker's
theorem on linear forms in logarithms consequently makes (29)
transcendental.  Multiplication by the nonzero integer \(k\) preserves
transcendence, so

\[
 \boxed{
 p^2+4r\text{ square}
 \quad\Longrightarrow\quad
 s_1\text{ is transcendental}.
 }
 \tag{30}
\]

In particular \(s_1\) is never an integer on the square-characteristic
branch. Combining this with the fortyfold certificate, every still-open
`q=0` equality has both

\[
 p^2+4r\text{ nonsquare}
 \qquad\text{and}\qquad
 r>40p.
 \tag{31}
\]
