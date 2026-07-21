# Automatic cyclic incidence

## Result

Let a base-`k` DFAO emit vectors `a(n)` in the finite binary vector space
`GF(2)^L`, and let

\[
P(N)=\bigoplus_{0\le n<N}a(n).
\]

Then `P` is itself `k`-automatic. More concretely, if the original selector
has `s` states, a prefix DFAO can be constructed with at most

\[
(s+1)2^{s+1}
\]

states. The extra state normalizes leading zeroes. The bound is deliberately
worst-case: only reachable states are materialized.

Now fix any finite odd-cyclic incidence system on the same `L` letters. Each
of the following index sets is a regular base-`k` language:

- prefixes with even incidence;
- prefixes giving odd parity proofs;
- prefixes passing the syndrome-circuit filter;
- prefixes giving irreducible parity proofs;
- prefixes having any prescribed CRT component-rank or nullity profile.

This is the **automatic cyclic-incidence theorem**. The automatic-sequence
closure mechanism is classical; the new content here is its constructive
composition with exact cyclic group-algebra incidence, including executable
CRT rank/nullity evidence for the resulting contextuality predicates.

## Constructive proof

Add a state `z` to the selector so that leading zeroes are ignored. At the end
of an all-zero word, `z` emits `a(0)`. Let `Q'` be this normalized state set.

While reading the base-`k` representation `d₁...dₘ` of `N`, maintain:

- `cᵢ`, the selector state reached by the prefix `d₁...dᵢ`;
- `Vᵢ` in `GF(2)^Q'`, whose `q` coordinate is the parity of the number of
  `i`-digit words lexicographically smaller than `d₁...dᵢ` that reach `q`.

Initially `c₀=z` and `V₀=0`. Define the linear map

\[
T(e_q)=\sum_{0\le e<k}e_{\delta(q,e)}.
\]

On the next input digit `d`, update

\[
V_{i+1}=T(V_i)+\sum_{0\le e<d}e_{\delta(c_i,e)},
\qquad
c_{i+1}=\delta(c_i,d).
\]

The first term extends every already-smaller prefix by every possible digit.
The second keeps the previous prefix equal and makes the new digit smaller.
These two cases are disjoint and exhaust the smaller words, proving the
invariant by induction.

At the end, fixed-width words below the representation of `N` are exactly the
integers `0,...,N-1`. XORing the selector output of every state whose
coordinate in `Vₘ` is one therefore gives `P(N)`.

There are only `(s+1)2^(s+1)` pairs `(c,V)`, proving automaticity. Every
incidence property above is a function of the finite mask `P(N)`, so marking
the corresponding final states gives a regular language.

`AutomaticCyclicIncidence.CompilePositionalPrefixSelector` implements this
construction directly. `PrefixSelection` evaluates the same transition
without materializing all reachable states, which avoids exponential output
state spaces.

## Gray-orbit remark

There is a canonical selector that toggles letter

\[
v_2(n+1)\pmod L
\]

at step `n`. A binary DFAO needs only `L` states: its state is the current run
of trailing one digits modulo `L`. Its prefix is the binary-reflected Gray
code

\[
G(N)=N\mathbin{\mathtt{xor}}\lfloor N/2\rfloor,
\]

with bit positions folded modulo `L`. Before folding begins, the prefixes

\[
P(0),P(1),\ldots,P(2^L-1)
\]

are exactly the `2^L` subsets of the letter set, each once. Consequently:

> For every finite odd-cyclic incidence system, one binary automatic prefix
> orbit contains every selection mask exactly once in its first `2^L` terms,
> and hence contains every kernel relation. The number of irreducible prefixes
> in that interval is exactly the total number of irreducible relations in the
> system.

This is the defining subset-enumeration property of reflected Gray code, not a
new counting method. For the 120-cell complete-C15-orbit system, `L=45`. Its first
`2^45 = 35,184,372,088,832` Gray prefixes therefore contain exactly the
independently certified **308,440** irreducible complete-orbit proofs. This is
not a classification of arbitrary subsets of the 675 individual bases.

This corollary does not make the exhaustive 120-cell count cheaper by itself.
It gives that finite classification a canonical automatic ordering and lets
other automatic selectors be compared against a universal orbit.

## C# surface

```csharp
var automatic = AutomaticCyclicIncidence.CreateBinaryGrayCodeEnumerator(incidence);

BigInteger relation = automatic.PrefixSelection(prefixLength);
AutomaticCyclicIncidenceAnalysis result = automatic.AnalyzePrefix(prefixLength);

if (result.IsIrreducible) {
    // result.WordAnalysis contains the exact CRT ranks and nullity.
}
```

For ordinary positional selectors, prefix and range queries are logarithmic
in the numeric index. Quadratic-Ostrowski selectors use cached exact suffix
dynamic programming. A full positional prefix DFAO may be compiled when its
reachable state space is reasonably small; a caller-supplied safety ceiling
prevents accidental exponential materialization.

## Reproduction

Run the implementation verifier:

```text
dotnet run -c Release --no-restore tools/automatic-cyclic-incidence-verifier.cs
```

It currently performs 608,576 exact comparisons against naïve accumulation
and direct digit sums,
including random positional and quadratic-Ostrowski automata, ranges, CRT
analyses, a compiled Thue--Morse prefix DFAO, and exhaustive Gray bijections
through 16 bits.

Run the 120-cell application:

```text
dotnet run -c Release --no-restore tools/automatic-120-cell-explorer.cs
```

## Formalization status

`AutomaticCyclicIncidence.lean` currently formalizes the compiler step's two
projection equations and the exact unnormalized and normalized state-count
bounds. It does **not** yet formalize the semantic prefix invariant, the five
regular-language consequences, this Gray-code remark, or either polytope
classification count. Those results presently rely on the written proof and
the independently checked C# constructions.

## Novelty boundary

Partial sums of automatic sequences naturally pass through the classical
theory of `k`-regular sequences; see Allouche and Shallit,
[*The ring of k-regular sequences*](https://doi.org/10.1016/0304-3975(92)90001-V).
Recognizability of addition for quadratic Ostrowski systems is also known;
see Hieronymi and Terry,
[*Ostrowski numeration systems, addition and finite automata*](https://arxiv.org/abs/1407.7000).

The first nontrivial Ostrowski application is now the
[Fibonacci--600-cell theorem](fibonacci-600-cell-automatic-parity.md): its
Zeckendorf digit-sum selector obeys an exact five-letter recurrence of minimal
period 255 and yields irreducible complete-C15-orbit proofs in exactly 32
residue classes. This supplies the selector-specific geometry and density
theorem that the general construction was intended to uncover.

The E8, 600-cell, and 120-cell parity-proof literature uses symmetry and
finite search, but the literature search performed for this work found no
prior automatic-prefix formulation of cyclic contextuality. The positional
theorem and elementary Gray corollary are currently a new constructive
synthesis, not strong publication-level novelty or a priority claim. The
Fibonacci application is a stronger proposed cross-domain theorem, but
external literature review and peer review would still be required before
claiming novelty or priority in publication.
