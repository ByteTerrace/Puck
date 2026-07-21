# Irreducible five-letter E8 parity proofs

## Result

Among the 21,653,868 five-letter, fifteen-fold parity proofs in the 120-ray
E8/Gosset configuration, exactly

\[
\boxed{3,569,146}
\]

are irreducible. The remaining 18,084,722 are reducible.

This answers the finite question left open in Section 6 of Aravind, Burton,
Núñez Ponasso, and Richter, [*Triacontagonal proofs of the Bell–Kochen–Specker
theorem*](https://doi.org/10.1088/1751-8121/ae55e4), *Journal of Physics A* 59
(2026) 175301. The authors enumerate the five-letter proofs and state that they
know no way, short of examining every case, to determine how many are
irreducible. This result is an exact exhaustive examination, accelerated by
the fifteen-fold symmetry and repeated through independent constructions.

This is a computationally proved proposed result, not yet an externally peer
reviewed or published theorem.

## Reconstruction from `SymmetryLattice`

No basis table or generator profiles from the paper are embedded in the
programs. They reconstruct the configuration through the public
`Puck.Maths.SymmetryLattice` API:

1. `Reflect(i, i)` pairs the 240 roots into 120 antipodal rays.
2. `Reflect(i, j) == i` detects orthogonality.
3. The 8-cliques of the resulting graph are the 2,025 orthogonal bases.
4. `Cycle²` is the paper's 24-degree ray rotation. It partitions the bases into
   135 disjoint orbits of fifteen; these are the paper's letters.

The reconstruction reproduces all relevant published invariants:

| Invariant | Reconstructed value |
|---|---:|
| Rays | 120 |
| Orthogonal neighbors per ray | 63 |
| Bases | 2,025 |
| Rays per basis | 8 |
| Fifteen-base letter orbits | 135 |
| Mod-2 letter syndromes | 16 |
| One-letter proofs | 16 |
| Three-letter proofs | 25,812 |
| Five-letter proofs | 21,653,868 |
| Seven-letter proofs | 8,652,003,024 |

The source-to-object crosswalk is checked against the publisher's official
supplement, not inferred only from these aggregates. The pinned
[`e8-published-generators.json`](certificates/e8-published-generators.json)
contains all 135 Supplementary Table 2 generators and the SHA-256 of the
official TeX source. The standard-coordinate verifier maps the paper's 120 ray
labels to its independently generated E8 rays, checks `Cycle²` is the paper's
`+1` rotation with wraparound, and verifies that all 135 published generators
are distinct reconstructed basis orbits. The live-source profile verifier also
compares all 33 profile multiplicities.

The earlier 2,025-basis construction is also described independently by
Waegell and Aravind in [*Parity proofs of the Kochen–Specker theorem based on
the Lie algebra E8*](https://arxiv.org/abs/1502.04350).

## Why rank 74 is exactly irreducibility

For a valid five-letter word \(W\), expand its five letters into their 75
individual bases and let

\[
A_W\in\mathbf F_2^{120\times75}
\]

be the ray–basis incidence matrix. A binary vector in \(\ker A_W\) selects a
set of bases in which every ray occurs an even number of times. The all-ones
vector is in the kernel and has odd weight 75.

If the kernel has dimension one, its only nonzero vector is the full proof, so
there is no proper parity subproof. Conversely, if the kernel contains an
independent vector \(v\), then either \(v\) has odd weight and is a proper
parity subproof, or \(v+\mathbf1\) has odd weight and is one. Therefore

\[
W\text{ is irreducible}
\iff \dim\ker A_W=1
\iff \operatorname{rank}A_W=74.
\]

This detects subproofs of lower symmetry or no symmetry; merely checking
whether whole letters can be removed would not suffice.

The reduction itself is kernel-checked in Lean at
[`PuckMathsFormal/E8/Irreducibility.lean`](../formal/PuckMathsFormal/PuckMathsFormal/E8/Irreducibility.lean).
It defines irreducibility using the actual odd-cardinality parity-subproof
condition. For an odd number of selected bases with the all-ones vector in the
kernel, Lean proves the complement argument and the equivalences with kernel
carrier `{0, allOnes}` and kernel finrank one.

## Why 3,569,146

The number is not a simple percentage of the 21,653,868 five-letter proofs.
It has a projective-geometric outer structure and an E8-specific inner rank
calculation. Keeping those two layers separate explains both the substantial
compression and why some exact finite computation remains necessary.

### The syndrome geometry is `PG(3,2)`

Evaluating each of the 135 orbit generators at \(t=1\) gives an eight-bit
syndrome. The 16 distinct syndromes are closed under XOR and span a
four-dimensional binary space. In the displayed eight coordinates they form
the first-order Reed--Muller code \(RM(1,3)\): zero, the all-ones word, and 14
words of weight four.

Because \(\mathbf F_2\) has only one nonzero scalar, the 15 nonzero syndrome
vectors are exactly the 15 points of \(PG(3,2)\). A valid five-letter proof has
syndrome sum zero. If its \(t=1\) rank is four, that sum is its unique linear
relation; hence none of its five syndromes is zero or repeated, and every four
are independent. Its syndromes are therefore a five-circuit, equivalently an
unordered projective frame in \(PG(3,2)\). Conversely, every such circuit has
automatic \(t=1\) rank four.

There are exactly 168 circuits. Indeed,

\[
\frac{|GL(4,2)|}{4!}=\frac{20,160}{24}=840
\]

unordered bases can be extended by their vector sum. Each resulting
five-circuit is obtained from each of its five four-element bases, so the
number of distinct circuits is \(840/5=168\). Any parity-valid five-set whose
syndromes do not form one of these circuits already has nullity at least two
in the \(t=1\) component and cannot be irreducible.

### A lifted four-fold symmetry

The 168 circuits form one orbit under \(GL(4,2)\). The stabilizer of a
particular circuit has order \(20,160/168=120\), and its action on the five
points realizes every permutation, so it is \(S_5\). This is the full abstract
projective symmetry; the orbit multiplicities break it in two stages. The
subgroup preserving just the syndrome-bucket sizes has order eight and is
\(D_8\), while the subgroup preserving the complete circuit transversal and
rank statistics has order four and is \(C_4\).

The syndrome buckets have sizes

\[
16;\quad
6,2,12,14,6,12,12,2,2,6,14,2,12,6,11,
\]

where 16 is the zero bucket. The nonzero buckets are not an unstructured list.
They split into the following color classes:

\[
\begin{array}{c|c|c}
\text{class}&\text{syndromes}&\text{bucket size}\\ \hline
A&\{1d,5a,e2,a5\}&6\\
B&\{2e,8b,96,cc\}&2\\
C&\{33,d1,74,69\}&12\\
D&\{47,b8\}&14\\
E&\{ff\}&11.
\end{array}
\]

The final \(C_4\) is not inferred merely from the matching statistics. Every
unit \(k\in U(15)=\{1,2,4,7,8,11,13,14\}\) has an explicitly checked affine
lift on the ray coordinates,

\[
(r,p)\longmapsto(\pi_k(r),kp+\delta_{k,r})\pmod {15},
\]

and each lift permutes all 135 rows of the complete orbit-polynomial table.
The two units \(k\) and \(-k\) induce the same syndrome transformation, giving
\(U(15)/\{\pm1\}\cong C_4\). The original \(C_{15}\) phase translations are
already quotiented out when a letter is represented by its 15-element orbit.
Thus it is the unit part of the \(C_{15}\) normalizer that supplies the
surviving four-fold action.

For one concrete generator, write an orbit row as
\(p=(p_0,\ldots,p_7)\), with rows identified up to multiplication by a common
monomial \(t^k\), since that only changes the chosen generator phase. Directly
on the pinned 135-row table, the transformation

\[
(Tp)_r=t^{d_r}p_{\rho(r)}(t^7),
\qquad
\rho=(5,3,7,2,0,6,4,1),
\qquad
d=(0,10,6,8,9,14,0,5)pmod {15}
\]

permutes all 135 rows and has order four. On syndromes it induces

\[
(1d\ 5a\ e2\ a5)
(2e\ 8b\ 96\ cc)
(33\ d1\ 74\ 69)
(47\ b8),
\]

while fixing \(ff\). The substitution \(t\mapsto t^7\) fixes the factors
\(t^2+t+1\) and \(t^4+t^3+t^2+t+1\), and exchanges the reciprocal factors
\(t^4+t+1\) and \(t^4+t^3+1\). It therefore preserves the condition that all
four nontrivial CRT ranks are five.

On the 15 projective points, the identity, involution, and two order-four
elements have cycle types \(1^{15}\), \(1^3 2^6\), and
\(1^1 2^1 4^3\), respectively. The induced \(C_4\) action on the 168 syndrome
circuits has two orbits of size one, five of size two, and 39 of size four.
Equivalently, the identity fixes
168 circuits, the two order-four elements fix two each, and the order-two
element fixes 12, so Burnside's lemma gives

\[
\frac{168+2+12+2}{4}=46
\]

structural circuit types. The irreducible contribution consequently reduces
to one representative per type:

\[
3,569,146
=186,606+2(133,506)+4(778,882).
\]

Here the three terms are the sums over representatives of the size-one,
size-two, and size-four orbits. The corresponding candidate identity is

\[
4,194,800
=228,272+2(155,520)+4(913,872).
\]

The certificate's 45 compressed rows are a projection of these 46 genuine
types. Grouping the orbit representatives only by their transversal and
irreducible totals merges two distinct size-four types, represented by

```text
1d-2e-69-96-cc
1d-2e-74-8b-cc
```

Both happen to have 576 transversals and 510 irreducible transversals. They
therefore merge into the single histogram row of multiplicity eight. Thus 45
is one numerical collision among 46 symmetry types, not a separate group
orbit count.

### The 4,194,800 candidates from a color enumerator

Let lowercase \(a,b,c,d,e\) mark how many circuit points lie in the five color
classes above. Direct enumeration of the 168 frames in \(PG(3,2)\), before any
E8 rank calculation, gives the circuit color enumerator

\[
\begin{aligned}
\mathcal C={}&c^4e+4bc^3d+6b^2c^2e+8b^2c^2d+4b^3cd+b^4e\\
&+8ac^2de+4ac^2d^2+16abcde+8abcd^2+8abc^3\\
&+8ab^2de+4ab^2d^2+16ab^2c^2+8ab^3c\\
&+4a^2c^2e+4a^2c^2d+8a^2bce+24a^2bcd\\
&+4a^2b^2e+4a^2b^2d+4a^3c^2+8a^3bc+4a^3b^2.
\end{aligned}
\]

The coefficient sum \(\mathcal C(1,1,1,1,1)=168\) recovers the frame count.
Choosing one orbit letter from every syndrome bucket weights the five colors
by their bucket sizes, and therefore

\[
\mathcal C(6,2,12,14,11)=4,194,800.
\]

The 24 monomials give exactly the 24 different transversal totals occurring
in the certificate. This part of the count follows entirely from
\(PG(3,2)\), the color partition, and the bucket sizes.

There is also a coordinate-free way to obtain the same number without listing
the 168 frames. Identify the syndrome space with \(V=\mathbf F_2^4\), and let
\(n_v\) be the size of the letter bucket over the nonzero point \(v\). In the
group algebra of \(V\), the required weighted zero-sum five-subsets are

\[
[y^5X^0]\prod_{v\ne0}(1+n_vyX^v).
\]

Fourier projection onto the zero element turns this into the 16-character
formula

\[
\frac1{16}\sum_{a\in V}[y^5]
  \prod_{v\ne0}\left(1+(-1)^{a\cdot v}n_vy\right)
=\frac{67,116,800}{16}
=4,194,800.
\]

The circuit verifier derives and checks all 16 integer coefficients in this
formula.  Thus the candidate total is a character-theoretic consequence of
the syndrome fibers, rather than a disguised pass through 4,194,800 words.

### Cyclotomic inclusion--exclusion

What the color enumerator does not decide is whether the five evaluated E8
columns are independent in every nontrivial CRT component. This can be
stated as one determinantal condition. For a word \(W\), let \(P_W(t)\) be
its \(8\times5\) polynomial incidence matrix and define

\[
\Delta_W(t)=\gcd\!\left(t^{15}+1,
  \{\text{all }5\times5\text{ minors of }P_W(t)\}\right).
\]

The projective-frame condition says that \(t+1\) divides every maximal minor
and accounts for the one unavoidable dependency. Square-free CRT then gives
the compact criterion

\[
W\text{ is irreducible}\quad\Longleftrightarrow\quad
\Delta_W(t)=t+1.
\]

Every additional factor of \(\Delta_W\) is therefore a genuine periodic
resonance producing another dependency.  Let

\[
\begin{array}{c|c}
E_3&t^2+t+1\text{ component has rank below five},\\
E_5&t^4+t^3+t^2+t+1\text{ component has rank below five},\\
E_+&t^4+t+1\text{ component has rank below five},\\
E_-&t^4+t^3+1\text{ component has rank below five}.
\end{array}
\]

The exact intersections over the 4,194,800 circuit transversals are:

| Failure intersection | Count |
|---|---:|
| \(E_3\) | 312,772 |
| \(E_5\) | 295,170 |
| \(E_+\), respectively \(E_-\) | 31,783 each |
| \(E_3\cap E_5\) | 34,556 |
| \(E_3\cap E_+\), respectively \(E_3\cap E_-\) | 3,491 each |
| \(E_5\cap E_+\), respectively \(E_5\cap E_-\) | 3,163 each |
| \(E_+\cap E_-\) | 468 |
| \(E_3\cap E_5\cap E_+\), respectively \(E_3\cap E_5\cap E_-\) | 1,127 each |
| \(E_3\cap E_+\cap E_-\) | 194 |
| \(E_5\cap E_+\cap E_-\) | 194 |
| \(E_3\cap E_5\cap E_+\cap E_-\) | 164 |

The equalities obtained by exchanging \(E_+\) and \(E_-\) have a direct
explanation. Up to common generator phase, the involution

\[
(Jp)_r=t^{q_r}p_r(t^{-1}),
\qquad q=(0,9,8,2,8,10,1,3)pmod {15}
\]

permutes all 135 orbit rows while preserving every syndrome bucket. It swaps
the two reciprocal degree-four factors. On the letters it has 25 fixed points
and 55 transposed pairs, so the reciprocal symmetry holds separately within
every one of the 168 circuits.

For readability, the exact failure-mask table can be summarized by its
inclusion--exclusion layers:

\[
\begin{aligned}
N_{\rm irr}
&=4,194,800
-671,508
+48,332
-2,642
+164\\
&=\boxed{3,569,146}.
\end{aligned}
\]

The four correction terms are respectively the sums of the one-factor,
two-factor, three-factor, and four-factor intersections in the table. This is
an accounting restatement of that table, not independent corroboration: the
alternating sum telescopes to its zero-failure bucket for any failure-mask
distribution. Bucket sizes alone determine 4,194,800, but not the intersection table: for
example, circuits with 12,096 transversals realize six different irreducible
counts. Those differences come from the relative positions of the evaluated
orbit columns over \(\mathbf F_4\) and the three copies of \(\mathbf F_{16}\).
They are exact finite-field statistics of the pinned
[`OrbitData.lean`](../formal/PuckMathsFormal/PuckMathsFormal/E8/OrbitData.lean),
The zero-failure count is recomputed from the orbit table by
[`e8-crt-circuit-count.json`](certificates/e8-crt-circuit-count.json), the
standalone [`e8-crt-circuit-verifier.cs`](../tools/e8-crt-circuit-verifier.cs),
and the reflected Lean count. The JSON's own ordered counts are certificate
data, not an independent recount. These values are not inferred from bucket
multiplicities, a random-rank heuristic, or an independence assumption.

## Full E8 normalizer

The full E8 Weyl group does not act on the fixed 135-letter system: a general
Weyl element sends the chosen Coxeter subgroup, and hence its letter
partition, to a conjugate one. The intrinsic symmetry group of these letters
is the normalizer of that subgroup. Let \(c\) be the order-30 Coxeter element
and \(g=c^2\), the paper's order-15 rotation. The same regular eigenvector that
makes \(c\) regular makes \(g\) regular. Springer's regular-centralizer theorem
then says that the degrees of \(C_{W(E_8)}(g)\) are the E8 invariant degrees
divisible by 15. Among

\[
2,8,12,14,18,20,24,30
\]

only 30 qualifies, so the centralizer has order 30. Conjugation injects the
normalizer quotient into \(\operatorname{Aut}(C_{15})\), of order eight, giving
the upper bound \(30\varphi(15)=240\).

The independent standard-coordinate
[`e8-normalizer-analysis.cs`](../tools/e8-normalizer-analysis.cs) attains this
bound. It constructs a quarter-integral E8 root automorphism for every unit
\(k=1,7,11,13,17,19,23,29\), and accepts it only after exact orthogonality,
240-root permutation, and \(wgw^{-1}=g^k\) checks. Thus the root normalizer has
order 240, its action on antipodal rays has order 120, and its effective action
on letters is

\[
N_{W(E_8)}(\langle g\rangle)/\langle c\rangle
\cong (\mathbf Z/30\mathbf Z)^\times
\cong C_4\times C_2.
\]

This order-eight group has 30 orbits on the 135 letters, with orbit-size
histogram \(1^3\,2^8\,4^9\,8^{10}\). Burnside's lemma gives 2,738,185 orbits
on all parity-valid five-letter supports and 446,502 orbits on the irreducible
ones. The latter split as

\[
3\text{ orbits of size }2,\qquad
713\text{ of size }4,\qquad
445,786\text{ of size }8,
\]

whose weighted sum is \(3,569,146\). There are no size-one irreducible orbits,
so no support is fixed by the entire group; almost every irreducible has
trivial stabilizer. E8 symmetry is a
useful 46-type compression at the syndrome-circuit layer, but it does not turn
the final answer into a small list of proof isomorphism types.

The exact fixed-point table, cycle types, orbit histograms, implementation
hash, and trust boundary are pinned in
[`e8-normalizer-action.json`](certificates/e8-normalizer-action.json). Reproduce
the certificate with the single targeted command

```text
dotnet run -c Release tools/e8-normalizer-analysis.cs
```

The finite computation uses exact integers after candidate discovery. Its two
external group-theory inputs are that the E8 root-system automorphism group is
its Weyl group and T. A. Springer's regular-centralizer theorem; the primary
source is [*Regular Elements of Finite Reflection Groups*, Inventiones
Mathematicae 25 (1974), 159--198](https://doi.org/10.1007/BF01390173).

## Five exact verification paths

### Symmetry-reduced search

The 120 rows and 75 columns split into eight and five orbits of length fifteen.
The incidence map is consequently an \(8\times5\) polynomial matrix over

\[
R=\mathbf F_2[t]/(t^{15}-1).
\]

The square-free factorization

\[
t^{15}-1=(t+1)(t^2+t+1)
(t^4+t^3+t^2+t+1)(t^4+t+1)(t^4+t^3+1)
\]

and the Chinese remainder theorem reduce the binary nullity to a
degree-weighted sum of five small extension-field nullities. Irreducibility is
equivalent to rank four at \(t=1\) and rank five at each other factor.

The fast search enumerates every zero-syndrome five-subset and also performs a
direct 120-by-75 binary rank calculation for every word. All 21,653,868 direct
ranks equal their extension-field predictions.

### Independently authored direct verifier

The verifier was written independently and does not use the polynomial/CRT
reduction. It separately reconstructs the rays, bases, and letters, enumerates
the five-subsets, and performs ordinary GF(2) elimination on every expanded
120-by-75 matrix. It reproduces the same count, full nullity histogram, and
irreducible-support digest.

### Standard-coordinate verifier without Puck

The third verifier has no Puck or project reference. It generates the standard
112 integral and 128 half-integral E8 roots, uses exact coordinate dot
products, constructs its own Coxeter action, and reconstructs the 120 rays,
2,025 bases, and 135 letters. It reproduces every published weight count, the
complete nullity histogram, and the irreducible count. It additionally checks
the full 135-generator publisher crosswalk described above.

### Circuit/CRT certificate

The 16 `t=1` syndromes form a four-dimensional binary code. Nullity one forces
the five syndromes to be one of its 168 rank-four five-circuits. Thus only
4,194,800 bucket transversals can be irreducible. A standalone BCL-only checker
tests those transversals in the four nontrivial CRT factors and obtains

```text
3,569,146 irreducible; 625,654 reducible within the circuit universe
```

Its machine-readable certificate records all 168 ordered circuit counts and a
45-row compressed histogram:
[`e8-crt-circuit-count.json`](certificates/e8-crt-circuit-count.json).

The geometry-neutral checker from the general odd-cyclic theorem independently
reads the same orbit-polynomial boundary, re-verifies the five factors of
\(t^{15}+1\), reconstructs the 168 syndrome circuits and 4,194,800
transversals, and recovers the same 3,569,146 count. Its small adapter is
[`e8-odd-cyclic-incidence.json`](certificates/e8-odd-cyclic-incidence.json).

### Reflected Lean count

[`ConcreteTheorem.lean`](../formal/PuckMathsFormal/PuckMathsFormal/E8/ConcreteTheorem.lean)
derives the syndrome circuits and finite-field columns from the explicit
135-by-8 orbit-polynomial table. Eight independently compiled reflected range
theorems combine to prove
`e8_fiveLetter_reflectedFullRank_count : fiveCircuits.size = 168 ∧
irreducibleCircuitCount = 3_569_146`.
The companion theorem checks the 4,194,800-transversal universe.

## Certificate

The machine-readable summary is
[`e8-five-letter-irreducibility.json`](certificates/e8-five-letter-irreducibility.json).
The canonical word convention is:

- rays are ordered by their least antipodal root index;
- bases are lexicographically ordered ray sets;
- letter orbits are ordered by their least basis index;
- a word is five strictly increasing, zero-based letter indices;
- each word contributes exactly five raw bytes to the digest stream.

The SHA-256 of the lexicographically ordered 3,569,146 irreducible words is

```text
c6111295d59daace3422ec73b30716c2e0c11951ec8146d49086f87d1c710afe
```

Run the exact paths with:

```text
dotnet run -c Release tools/e8-parity-proof-search.cs -- --classify --direct-all
dotnet run -c Release tools/e8-parity-proof-verifier.cs
dotnet run -c Release tools/e8-standard-coordinate-verifier.cs
dotnet run -c Release -p:NuGetAudit=false tools/e8-crt-circuit-verifier.cs
dotnet run -c Release -p:NuGetAudit=false tools/odd-cyclic-incidence-verifier.cs -- --certificate docs/certificates/e8-odd-cyclic-incidence.json
dotnet run -c Release -p:NuGetAudit=false tools/e8-circuit-symmetry.cs
dotnet run -c Release tools/e8-normalizer-analysis.cs
powershell -File tools/e8-supplement-profile-verifier.ps1
```

All classification programs use exact integer and finite-field operations
only. The live supplement check requires network access; every other command
is offline once dependencies are present.

Build the formal reduction and concrete count with the pinned Lean 4.30/mathlib
project:

```text
lake build PuckMathsFormal.E8.ConcreteTheorem
```

The formalization contains no `sorry`, `admit`, `unsafe`, or user-declared
axioms. The abstract irreducibility theorem has only the standard mathlib
dependencies `propext`, `Classical.choice`, and `Quot.sound`. The concrete
count's eight reflected decisions use `native_decide`; Lean checks their types,
but this adds eight generated native-reduction axioms and the Lean compiler to
the trusted base. This boundary is explicit rather than being described as a
pure kernel-only enumeration.

## Full nullity distribution

```text
 1:3569146  2:9083304  3:2977458  4:1069656  5:835033
 6:1734784  7:1217527  8:383488   9:337509  10:147432
11:118873  12:51962   13:40710   14:11595   15:5706
16:13789   17:17464   18:7319    19:2174    20:1153
21:13345   22:10809   23:1142    24:1491    25:278
26:386     27:17      28:28      29:2       31:4
32:224     33:55      36:4       47:1
```

The nullity-one entry is the irreducible count. The histogram sums to the
published total of 21,653,868 five-letter proofs.

## Scope

The count concerns labeled five-letter supports in the paper's 135-letter
system, exactly as in its weight distribution. It does not quotient proofs by
the full E8 automorphism group. Determining the isomorphism classes among these
irreducible proofs would be a separate problem.
