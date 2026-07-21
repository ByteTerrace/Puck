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

## Consequence for the uniform Beatty-shadow theorem

The previous finite-prefix strategy recognized only exact-affine equality. It must additionally recognize rational
Riccati solutions such as (1), and a complete total decider still needs an exhaustive classification or a separate
decision procedure for all remaining exact integer hits.
