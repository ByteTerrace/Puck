# Uniform numerator-scale traps for the repeated-root regular line

## Result

Consider the repeated-root polynomial tail

\[
 s_n=pn+q+\frac{r(n+k)^2}{s_{n+1}},
 \qquad p,r\in\mathbf Z_{>0},\quad k\in\mathbf Z_{\geq0},
 \quad q\in\mathbf Z,\quad p+q\geq0.                 \tag{1}
\]

Let \(Q_0=1\), let \(Q_1=h\) be the regular/minimal EGF seed, and put

\[
 Q_{n+2}=r(n+k+1)^2Q_n-(p(n+2)+q)Q_{n+1}.            \tag{2}
\]

The first tail value and the seed are related by

\[
 s_1=p+q+h.                                          \tag{2a}
\]

## 1. A numerator-scale comparison lemma

The order argument is more general than repeated roots. Let
\(a_n,c_n>0\), and suppose a family of positive minimal tails satisfies

\[
 x_n(r)=\frac{r c_n}{a_n+x_{n+1}(r)}
 \qquad(r>0).                                        \tag{S1}
\]

Assume that \(x_n(r)/g_n\to\mu(r)>0\) for some common positive scale
\(g_n\), and that for \(0<r_1<r_2\)

\[
 1<\frac{\mu(r_2)}{\mu(r_1)}<\frac{r_2}{r_1}.
\]

Then, at every index,

\[
 \boxed{
 x_n(r_1)<x_n(r_2)<
 \frac{r_2}{r_1}x_n(r_1).
 }                                                    \tag{S2}
\]

Indeed, put \(R=r_2/r_1>1\). If at index \(n+1\)
\(u=x_{n+1}(r_1)<v=x_{n+1}(r_2)<Ru\), then

\[
 \frac{x_n(r_2)}{x_n(r_1)}
 =R\frac{a_n+u}{a_n+v}.                              \tag{S3}
\]

The right side is smaller than \(R\) because \(v>u\), and it is larger
than one because

\[
 R(a_n+u)-(a_n+v)>
 R(a_n+u)-(a_n+Ru)=(R-1)a_n>0.
\]

The assumed asymptotic places the ratio in \((1,R)\) at all sufficiently
large indices, and (S3) propagates it backwards.

For a positive degree-\((2,1)\) tail in which the whole quadratic
numerator is scaled by \(r\), write

\[
 a_n\sim pn,\qquad c_n\sim\gamma n^2
 \qquad(p,\gamma>0).
\]

The positive characteristic slope is determined by

\[
 \mu(p+\mu)=r\gamma.
\]

It is strictly increasing in \(r\), while
\(r/\mu=(p+\mu)/\gamma\) is strictly increasing. Hence the hypothesis of
the comparison lemma holds automatically. The result therefore applies
well beyond the repeated-root slice.

In particular, if one parameter \(r_*>0\) has a known positive regular
seed \(h_*=h(r_*)\), then every \(r\ne r_*\) obeys the multiplicative
anchor trap

\[
 \boxed{
 \min\!\left(h_*,\frac r{r_*}h_*\right)
 <h(r)<
 \max\!\left(h_*,\frac r{r_*}h_*\right).
 }                                                    \tag{S4}
\]

Suppose the anchor is rational, \(h_*=A_*/B_*>0\) in lowest terms. If a
different value \(h(r)=A/B\) were rational in lowest terms, rational
separation and (S4) would give

\[
 \frac1{BB_*}\leq |h(r)-h_*|
 <\frac{A_*}{B_*}\frac{|r-r_*|}{r_*}.
\]

Consequently

\[
 \boxed{
 h(r)\in\mathbf Q
 \quad\Longrightarrow\quad
 B>\frac{r_*}{A_*|r-r_*|}.
 }                                                    \tag{S5}
\]

Taking \(B=1\) gives an exact integer-exclusion band around every rational
anchor:

\[
 \boxed{
 0<|r-r_*|\leq\frac{r_*}{A_*}
 \quad\Longrightarrow\quad h(r)\notin\mathbf Z.
 }                                                    \tag{S6}
\]

For the repeated-root regular line, all square-characteristic
rational-function resonances from the terminating cases described in
[the regular-line reduction](polynomial-tail-repeated-root-regular-line.md),
\(a\in\{0,-1,\ldots,1-k\}\) provide such rational anchors. Thus (S4)--(S6)
turn every already classified positive resonance into a neighboring
nonsquare exclusion region, not only the affine resonance used below.

## 2. The affine anchor

The regular line is the unique line on which every \(Q_n\) is positive.
Define

\[
 c=p(k-1)-q.                                         \tag{3}
\]

This note treats the broad range \(c\geq0\). Admissibility then gives

\[
 0\leq c\leq pk.                                     \tag{4}
\]

In the reindexed Lerch notation of
[the regular-line reduction](polynomial-tail-repeated-root-regular-line.md),
\(q_0=q-p(k-1)=-c\). Thus this range is automatically nonaligned:
\(p-2q_0=p+2c>0\).

Put

\[
 r_0=c(p+c),\qquad d=\frac r{p+c}.                   \tag{5}
\]

The regular seed has the following uniform strict trap:

\[
 \boxed{
 (k+1)\min(c,d)<h<(k+1)\max(c,d)
 }
 \qquad(r\ne r_0).                                   \tag{6}
\]

At \(r=r_0\), the two endpoints coincide and the exact solution is

\[
 \boxed{
 h=c(k+1),\qquad Q_n=c^n(k+1)_n.
 }                                                    \tag{7}
\]

For \(c>0\), (7) is the affine square-characteristic resonance
\[
 p^2+4r_0=(p+2c)^2.
\]
For \(c=0\), \(r_0=0\) is outside the positive-\(r\)
family, and (6) simply reads

\[
 0<h<\frac{r(k+1)}p.                                 \tag{8}
\]

The old two-line trap for \(k=1,q=-p\) is the specialization \(c=p\):

\[
 \min\!\left(2p,\frac rp\right)
 <h<
 \max\!\left(2p,\frac rp\right).
\]

Thus the phenomenon is not confined to the canonical Hasse image family.
The second line in (6) is a simple affine comparison line; for \(k>1\) it
is generally different from the prime-independent Hasse image line.

## 3. Direct endpoint proof

Write

\[
 x_n=\frac{Q_{n+1}}{Q_n}>0,
 \qquad N=n+k+1.
\]

Equation (2), read backwards on the positive minimal line, is

\[
 x_n=\frac{rN^2}{p(n+2)+q+x_{n+1}}
     =:T_N(x_{n+1}).                                 \tag{9}
\]

Using \(q=p(k-1)-c\), this becomes

\[
 T_N(X)=\frac{rN^2}{pN-c+X}.                         \tag{10}
\]

The map \(T_N\) is strictly decreasing on the positive range. Its two
endpoint calculations are exact:

\[
 \boxed{T_N(c(N+1))=dN,}                             \tag{11}
\]

and

\[
 \boxed{
 T_N(d(N+1))-cN
 =\frac{N(d-c)(pN-c)}{pN-c+d(N+1)}.
 }                                                    \tag{12}
\]

All denominators in (11)--(12) are positive. Moreover, (4) and
\(N\geq k+1\) give the strict inequality

\[
 pN>c.                                                \tag{13}
\]

If \(r>r_0\), then \(d>c\). Equations (11)--(13) and antitonicity
show

\[
 c(N+1)<x_{n+1}<d(N+1)
 \quad\Longrightarrow\quad
 cN<x_n<dN.                                          \tag{14}
\]

If \(r<r_0\), the same calculation with the endpoints reversed gives

\[
 d(N+1)<x_{n+1}<c(N+1)
 \quad\Longrightarrow\quad
 dN<x_n<cN.                                          \tag{15}
\]

Let

\[
 \mu=\frac{\sqrt{p^2+4r}-p}{2},
 \qquad r=\mu(p+\mu).
\]

The standard Perron asymptotic of the positive/minimal line gives

\[
 \frac{x_n}{n+k+1}\longrightarrow\mu.                \tag{16}
\]

Because \(u\mapsto u(p+u)\) is strictly increasing on \(u\geq0\),

\[
 r>r_0\Longrightarrow c<\mu<d,
 \qquad
 r<r_0\Longrightarrow d<\mu<c.                      \tag{17}
\]

Indeed,

\[
 d-\mu=\frac{\mu(\mu-c)}{p+c}.
\]

Equations (16)--(17) place \(x_n\) strictly inside the appropriate strip
for all sufficiently large \(n\). Repeated application of (14) or (15)
propagates the strip back to \(n=0\), proving (6).

When \(r=r_0\), substitution gives

\[
 x_n=c(n+k+1)
\]

at every index. Multiplying these ratios proves (7). This also supplies
an elementary check of the limiting case of the trap. \(\square\)

## 4. Arithmetic consequences

One endpoint of (6), namely

\[
 H_0=c(k+1),
\]

is an integer. The width of the trapping interval is exactly

\[
 \delta=\frac{(k+1)|r-r_0|}{p+c}.                    \tag{18}
\]

Consequently the whole nonresonant unit band is excluded:

\[
 \boxed{
 0<|r-c(p+c)|\leq\frac{p+c}{k+1}
 \quad\Longrightarrow\quad h\notin\mathbf Z.
 }                                                    \tag{19}
\]

In particular, a proposed integer tail value \(s_1=M\) would make
\(h=M-(p+q)\) an integer by (2a), so (19) excludes the original
integer-hit question as well as an integral EGF seed.

This is an infinite exact exclusion family in the nonsquare,
nonaligned Lerch core whenever the characteristic discriminant is
nonsquare. It uses no fixed-depth orbit search and remains uniform in all
four parameters \(p,q,r,k\) subject to \(c\geq0\).

There is also a quantitative obstruction to rationality. If
\(h=A/B\ne H_0\) in lowest terms, then

\[
 |h-H_0|\geq\frac1B.
\]

Combining this with the strict trap gives

\[
 \boxed{
 h\in\mathbf Q
 \quad\Longrightarrow\quad
 B>\frac{p+c}{(k+1)|r-r_0|}
 }
 \qquad(r\ne r_0).                                   \tag{20}
\]

The denominator bound becomes strong close to the affine resonance, but it
does not prove irrationality away from it. The interval in (6) has positive
length whenever \(r\ne r_0\), and therefore contains rational numbers of
arbitrarily large denominator. No order argument based only on these two
lines can separate the regular seed from every rational number. A complete
rationality theorem still needs arithmetic information about the Lerch
connection coordinate, rather than a further real inequality of this form.

## 5. Verification

The standalone exact verifier
[polynomial-tail-repeated-root-affine-trap-verifier.cs](../tools/polynomial-tail-repeated-root-affine-trap-verifier.cs)
checks the general numerator-scale interval (S3), (11)--(13), the
orientation of both affine invariant strips, the resonance formula (7), and
the unit-band corollary over a configurable integer box. It uses only
BigInteger rational arithmetic.

~~~powershell
dotnet run --property:NuGetAudit=false tools/polynomial-tail-repeated-root-affine-trap-verifier.cs -- 8 4 40 6
~~~
