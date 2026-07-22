# Polynomial continued-fraction tails: state of the art

**Purpose.** Single entry point for the positive degree-(2,1)
polynomial-continued-fraction program: the objects, what is settled and in what
sense, what is refuted, what is open, and where the code, proofs, and literature
live.

**How to read.** §0–§2 are prerequisites. §7 is the decision procedure — run it
on your tuple. §4 is the highest value-per-line section: it is what stops you
wasting weeks. §12 is the cold-start order.

**Provenance and its limits.** Synthesized from the nineteen documents in §10.
Claims are attributed inline; hedges are carried verbatim. Sections marked
**[synthesis]** are this page's own organization, not a source claim — §7, §8,
and the framing of §5 are the main ones, and they are the parts most likely to be
wrong. This page proves nothing; it is a map.

**Status of this page.** The apex source
[uniform-beatty-shadow-theorem.md](uniform-beatty-shadow-theorem.md) declares
itself **IN PROGRESS**: *"the eventual finite-channel theorem is proved and
implemented; the advertised uniform total decider is not. A previously proposed
integer-avoidance lemma is false."* Read every "closed" below against that.

---

## §0 Glossary

Terms used in load-bearing claims. Working definitions only; the source column
is where the term does real work.

| Term | Working definition | Where it matters |
|---|---|---|
| **holonomic** | satisfies a linear ODE/recurrence with polynomial coefficients | §2.1, §4.1 |
| **minimal / recessive solution** | the solution of a second-order recurrence decaying fastest relative to the others; the CF tail is minimal | §1, §2.1, §3.10 |
| **Pincherle** | the classical correspondence between CF convergence and minimality | §3.2a |
| **1-period** | a period of a rational differential form over a rational domain; the class for which effective relation algorithms exist | §3.2a, §5.2 |
| **E-function / G-function** | Siegel classes with arithmetic growth on Taylor coefficients | §4.2, §5.2 |
| **globally bounded** | a power series with a common denominator growing at most exponentially | §4.2 |
| **Fuchsian** | ODE all of whose singularities are regular | §5.2 |
| **local exponent** | root of the indicial equation at a singular point | §5.2 |
| **Liouvillian** | solvable in closed form by algebraic ops, exponentials, integrals | §3.9 |
| **Kummer / Pfaff / Euler transformation** | classical `₂F₁` identities permuting parameters or the argument | §1.3, §3.9 |
| **contiguous relation** | identity linking `₂F₁` with parameters differing by integers | §3.6, §3.10 |
| **Riccati** | the first-order quadratic recurrence `sₙ = A(n) + B(n)/sₙ₊₁` | §1, §3.2 |
| **Ostrowski numeration** | positional representation with respect to CF convergents of an irrational; the natural base for Beatty phenomena | §3.4 |
| **DFAO** | deterministic finite automaton with output | §3.4 |
| **inert / split prime** | in `Q(√Δ)`: `(Δ/ℓ) = −1` / `= +1`; **good** = unramified and dividing none of the excluded quantities | §3.7, §4.2 |
| **Mertens weight** | `Σ_{ℓ∈S} log ℓ/(ℓ−1)`; divergence is the relevant notion of "most primes" here | §4.2, §5.3 |
| **Poincaré–Perron** | asymptotics of linear recurrences by characteristic-root moduli | §3.10 |
| **completely monotone / Hausdorff** | moment sequences of measures on `[0,1]`; characterized by `Σ(−1)ⁱC(j,i)m_{k+i} ≥ 0` | §3.10, §5.3 |
| **Padé / Hermite–Padé** | rational (or simultaneous-rational) approximation to a function or tuple | §4.2, §5.3, §8 |
| **Pochhammer** `(a)ₙ` | `a(a+1)⋯(a+n−1)` | §5.3 |
| **Markov function** | a Cauchy transform of a positive measure; guarantees Padé convergence | §5.3 |

---

## §1 The object

### 1.1 Setup

```
A(n) = p·n + q                (linear partial quotient)
B(n) = r·n² + u·n + v         (quadratic numerator)
```

**Standing hypothesis (H):** `p > 0`, `r > 0`, `A(n) ≥ 0` and `B(n) > 0` for all
`n ≥ 1`. Exactly decidable: `A` increasing (check `n=1`), `B` convex (check `n=1`
and the two integers bracketing the vertex).

**The tail.** Under (H) there is a **unique** positive sequence `(sₙ)_{n≥1}` with

```
sₙ = A(n) + B(n)/sₙ₊₁                                               (T-rec)
```

Uniqueness is load-bearing: exhibiting any positive closed form satisfying
(T-rec) identically **is** an identification of the tail, and it makes the tail
the **minimal** solution of the associated second-order recurrence.
[trunk §§1–2]

Note the one-step contraction is insufficient when `r` is large relative to `p²`;
the proof uses an alternating antitone iteration whose slopes are convergents of
`p + r/(p + r/⋯)`.

**Elementary facts worth having immediately.** `sₙ > A(n)` for all `n` (from
(T-rec) and positivity) — this alone settles many naive candidates.
`Δ_B = 0` counts as square. `Δ_B < 0` means `B` has no real root and never
factors over `Q`.

### 1.2 Derived constants

```
D  = √(p²+4r)      λ = (p+D)/2      ℓ = (p−D)/2 < 0      μ = (D−p)/2 = −ℓ
β  = (q·λ² + (u−r)·λ)/(λ²+r)  ≡  (q·λ + u − r)/D
xₙ = λ·n + β                                          (the affine centre)
```

`λ` is the **dominant** root of `X² = pX + r`; `λμ = r`, `λ+μ = D`.

`λ` is obtained by cancelling the **coefficient of `n`**, and `β` by cancelling
the **constant coefficient**, in `T_n(x_{n+1}) − xₙ` where `T_n(y) = A(n)+B(n)/y`.
Both characteristic roots admit such a `β`; the tail-relevant pair is the one
with the dominant slope. The cancellation leaves an exactly constant remainder:

```
T_n(x_{n+1}) − xₙ = R_aff/x_{n+1},   R_aff = (q−β)(λ+β) + v      (EXACT)
```
[trunk §§1,3]

### 1.3 The three stratifying invariants

```
Δ_c = p² + 4r          characteristic discriminant   (square ⟺ λ ∈ Q)
Δ_B = u² − 4rv         numerator discriminant        (square ⟺ B factors over Q)
R   = p(u − r) − 2rq   alignment residual            (R = 0 ⟺ "aligned")
d   = √Δ_B
```

**What `R` is, precisely.** In the Gauss reduction,
`a = (d+r)/(2r) + R/(2r(ℓ−λ)) = (d+r)/(2r) − R/(2rD)`. So `R` is the
**`√Δ_c`-component** of `a`. Hence:

> **When `Δ_c` is nonsquare AND `Δ_B` is square: `a ∈ Q ⟺ R = 0`.**

Both hypotheses are required. Counterexample to dropping them — the refuting
family of §4.1 at any `p`: `Δ_c = (p+4)²` so `D = p+4 ∈ Z`, `Δ_B = (4p+12)²`
square, `R = 2p(p+4) ≠ 0`, yet `a = (p+4)/(p+2) ∈ Q`. Conversely `R = 0` with
`Δ_B` nonsquare leaves `a = (d+r)/(2r) ∉ Q`. [trunk §6]

**Scaling law.** The family is closed under `(p,q,r,u,v) ↦ (hp,hq,h²r,h²u,h²v)`,
`sₙ ↦ h·sₙ`, and the invariants transform as
`Δ'_c = h²Δ_c`, `Δ'_B = h⁴Δ_B`, `R' = h³R` — so square/nonsquare status and
alignment are **ray invariants**. [projective-rationality (4);
lerch-delayed-failure (S7)]

### 1.4 The cleared integer orbit

For a proposed value `s₁ = M`:

```
Q₀ = 1,   Q₁ = h := M − A(1),   Q_{n+2} = B(n+1)·Qₙ − A(n+2)·Q_{n+1}
s₁ = p + q + h
```

`h` is the **seed**. `M` is the tail value **iff every `Qₙ > 0`**.
`E_N := lcm_{0≤n≤N} denominator(Qₙ/n!)` measures **factorial reduction**;
"exponential factorial reduction" means `E_N ≤ Cᴺ`. Always `E_N | N!`.
[beatty-shadow §8; factorial-density (1)–(2)]

### 1.5 Repeated root, and the Gauss/Lerch parameters

**Repeated root** ⟺ `Δ_B = 0` ⟺ `B(n) = r(n+k)²` with `k = u/(2r)`,
`v = rk²`, `k ≥ 0` integer. This is where the whole Lerch cluster lives.

**Gauss parameters** at tail index `N`, with `ρ = R/(2rD)` and orientation
`σ ∈ {±1}` (which root of `B` is taken first):

```
a_σ = (r+σd)/(2r) − ρ        b_σ = N−1 + (u+σd)/(2r)        c = N + (u−r)/(2r) − ρ
x   = (p−D)/(p+D) = ℓ/λ ∈ (−1,0)                          [the connection coordinate]
```

`c` is orientation-independent. Cross-orientation identities:
`c − a_σ = b_{−σ}`, `c − b_σ = a_{−σ}`, `c − a_σ − b_σ = −σd/r`. Euler's
transformation multiplies both `₂F₁` factors by `(1−x)^{−σd/r}`, so **the two
orientations are not two independent tests**.
[connection-coordinate (P2)–(P7); euler-moment (O1)–(O3)]

`F(z) = ₂F₁(a,b;c;z)` denotes the principal Gauss solution; it solves the
Fuchsian equation of §5.2.

**Analytic objects.**

```
Φ(z,1,a) = Σ_{n≥0} zⁿ/(n+a)                         [Lerch transcendent]
t = μ/λ = −x ∈ (0,1)        y = t/(1+t) = μ/D
B_y(a,1−a) = ∫₀^y w^{a−1}(1−w)^{−a} dw               [incomplete beta]
Φ(−t,1,a) = ∫₀¹ w^{a−1}/(1+tw) dw = t^{−a}·B_y(a,1−a)     (for a > 0)
```
[lerch-arithmetic (2),(10),(11)]

**Why `x` is the right coordinate.** It is the Gauss argument the exact PCF
reduction produces; it is orientation-independent; the equality question is a
single log-derivative condition there; and it is Galois-natural —
`x^τ = 1/x`, `λ^τ = xλ`, `a^τ = 1−c+b`. [connection-coordinate]

**Repeated-root reindexing.** With `q₀ = q − p(k−1)`, the Lerch parameter is
`a = (μ+q₀)/D`, **independent of `k`**. At `k = 1`, `q₀ = q`. Also
`T := p(2k−1) − 2q = p − 2q₀ = R/r`, and `C := k + 3/2 − T/(2D)`, so
`a = C − k − 1`. [repeated-root-regular-line (3),(9)]

---

## §2 The questions

**Q1 (integer hit).** Can `s_N = M` for an integer `M`? The rational case reduces
to the integer case only up to scaling — by §1.3's scaling law, `s_N ∈ Q` iff
*some* integral representative of the ray has an integer hit, but the clearing
scale is not known in advance (§4.3).

**Q2 (Beatty shadow).** Is `dₙ := ⌊sₙ⌋ − ⌊xₙ⌋ ≡ 0`? If not, what is its
structure?

**Q3 (decision).** Is there a terminating algorithm for Q2, uniform in the five
parameters?

Q1 is the engine room: Q2 fails at `n` exactly when an integer separates `sₙ`
from `xₙ`, and Q3 requires deciding finitely many Q1 instances exactly.

### 2.1 Reformulations of Q1

| Form | Statement | Kind | Source |
|---|---|---|---|
| **Orbit positivity** | `M` is the tail value ⟺ every `Qₙ > 0` (§1.4) | **equivalence** | beatty-shadow §8 |
| **Log-derivative** | `F'(x)/F(x) = k ∈ Q(D)`, `k = b((c−a)/T − 1)/(x−1)`, `T = M/λ` | **equivalence** (nonresonant chart) | connection-coordinate (D1)–(D2) |
| **Lerch value** (repeated root, `k=1`) | `Φ(−t,1,a) = λM/(qM+r)`; if `qM+r = 0` the candidate is excluded outright | **equivalence** | lerch-arithmetic (8) |
| **Moment** | a proposed equality forces `m₁ = b/T` and then every `m_j` exactly | **necessary condition** (the equivalence is §3.10's termination theorem) | euler-moment (3),(6) |

The orbit form makes non-equality **uniformly semidecidable** and equality
**co-r.e. (Π⁰₁)**. Everything in this program is a race between finite exclusion
certificates and a missing semidecision on the other side.

---

## §3 What is settled, and in what sense

**Labels used below, strictly:**
**PROVEN** = written proof in a source. **LEAN** = machine-checked.
**REDUCED** = converted to an external procedure not implemented here.
**SEMIDECIDED** = one direction terminates.
**OBSTRUCTION** = a necessary condition, excluding no tuple by itself.

### 3.1 The analytic trunk — PROVEN

Existence and uniqueness under (H), including `A(1) = 0`; the exact affine
identity; a certified trap `|sₙ − xₙ| ≤ H/n` (`n ≥ N`) with integer-recheckable
`N, H`; arbitrary-order asymptotics `sₙ = λn + Σ_{j≤m}c_j n^{−j} + O(n^{−m−1})`,
genuinely asymptotic and with **no maximum order**, certified as
`|sₙ − zₙ| ≤ H_k/n^k`. Rational coefficients clear by `tₙ = d·sₙ`; real
coefficients keep everything but the finite certificate.
Note `c₁ = R_aff/D`, so `c₁ = 0 ⟺ R_aff = 0` (the exact-affine case).
**Not in Lean** — "an exact written proof but has not yet been transcribed."
[trunk]

### 3.2 Rational-function tails — PROVEN, complete, no degree bound

Let `s = 𝒜/ℬ` be a reduced rational solution, `ℬ` monic of degree `m`
(so `deg 𝒜 = m+1`). Coprimality in the cleared Riccati equation forces linear
`C, K` with **all three** identities:

```
𝒜(n+1) = ℬ(n)·C(n)
𝒜(n) − (pn+q)·ℬ(n) = ℬ(n+1)·K(n)
r n² + u n + v = C(n)·K(n)
C(n) = λn + β + (m+1)λ        K(n) = (λ−p)n + β − q − m(λ−p)
```

Matching the asymptote and reading the constant coefficient gives a **quadratic
in `m`** — at most two candidate degrees; over a genuine quadratic field the surd
component is linear in `m`, pinning at most one. Per candidate, `ℬ` solves one
exact linear system. Positivity is decidable without an unbounded scan (eventual
positivity plus backward transport). Requires `C`'s constant term positive.

⚠ `𝒜, ℬ` here are **not** the `A, B` of §1.1. The recognizer's degree-128 cap is
a storage/elimination cost decision: "the classification and finite certificate
scheme themselves have no degree bound."
[integer-counterexamples; trunk §6]

### 3.2a The degree-one / 1-period island — REDUCED, not implemented

Under `Δ_c` square **and** `Δ_B` square, the equality question at a finite prefix
becomes Pincherle minimality for a degree-one second-order holonomic recurrence,
decidable by Kenison et al. 2026 (§13). Coefficient shape
`(rj+γ₀)(j+α−1)/(pj+β₀)`; Pincherle data `u₋₁ = α`, `u₀ = A_N − M`, normalized
target `(M−A_N)/α`. The aligned extension (`Δ_B` square, `R = 0`) gives rational
`a = (d+r)/2r`, `b = N−1+(u+d)/2r`, `c = N+(u−r)/2r`.

> **Carry this verbatim:** *"The local toolkit does not yet implement the paper's
> E-function/1-period engines, so it constructs a complete input to the published
> decision procedure rather than pretending numerical quadrature is that
> procedure."* [beatty-shadow §7]

Verifier counts: 21,315 rational-characteristic + 1,334 aligned
irrational-characteristic = 22,649 accepted.

### 3.3 BDS metallic-mean conjecture — LEAN

For integer `k ≥ 1`, `α = (k+√(k²+4))/2`, tail `sₙ = kn − 1 + n²/sₙ₊₁`
(the `(k,−1,1,0,0)` slice):

```
⌊sₙ⌋ = ⌊ α·n − (1+α)/(2α−k) ⌋      for all n ≥ 1
```

Trap `xₙ < sₙ < xₙ + C/n`, uniqueness by contraction `ρ = 1/(kα)` (proved
directly, not imported); then the quadratic norm gives `Kg(Kg+qλ) = KF` with `F`
a positive integer `≡ −k (mod K)`, so `F ≥ K−k`, while `P(C/n) < KλC < K−k` —
the next integer is outside the trap.

`PuckMathsFormal.BDS.bds_conjecture`, `formal/.../BDS/Theorem.lean`. Lean 4.30.0
/ mathlib; no `sorry`/`admit`/custom axioms; depends only on `propext`,
`Classical.choice`, `Quot.sound`.
API: `MetallicPolynomialContinuedFraction.TailFloor(metallicIndex, tailIndex)`
(with `BigInteger` overload), `.Analyze(k)`.

Provenance: Bosma–Dekking–Steiner 2018 posed it; Fokkink–Joshi 2026 restated it
as Conjecture 20, having proved only a particular golden-mean case (§13).

> *"It has not yet been externally peer reviewed; until that happens, cite it as
> a formally verified proposed proof rather than as an accepted published
> theorem."*

### 3.4 Beatty shadow: EVENTUAL behaviour — PROVEN and implemented; NOT in Lean

The source is **IN PROGRESS**; what follows is the tail half only. **Nothing in
this subsection is formalized** — §6.1's Lean table has no row for any of it.

- **Finite-norm reduction.** A discrepancy at `n ≥ N` forces an integer within
  `H/n` of `xₙ`; the cleared field norm obeys `|J_{n,m}| ≤ Z²H(H+L+C) =: J`,
  where `Z` clears denominators of `λ, β`, `H` is §3.1's trap constant, and
  `L = ⌈2|Q|√D/Z⌉`, `C = ⌈2|B|√D/Z⌉` bound the conjugate.
- **Finite Pell-orbit reduction.** Each `U²−DY² = h`, `|h| ≤ J`, is stable under
  the fundamental unit `ε`; every orbit has a representative with
  `U² < 2|h|c`, `DY² < 2|h|c`; modulo `|Q|Z` the unit action permutes a finite
  set, cutting out periodic exponent sets.
- **Sign stabilization, decidable at order ≤ 2.** The feared infinite-order
  collision collapses: a nonzero first-order collision always separates at second
  order, and `c₁ = 0 ⟹ R_aff = 0`, where uniqueness gives `sₙ = xₙ`. *"There is
  no unbounded coefficient dichotomy left."*
- **Eventual finite-channel theorem.** `{n ≥ N* : dₙ ≠ 0}` is a finite union of
  Pell channels `n(C,k) = Θ(ε^k)`, compiled to a DFAO. Soundness does not rest on
  termination: *"every returned pattern carries the direct recurrence
  certificate."*
- **Sparsity — unconditional.** `#{n ≤ N : dₙ ≠ 0} = O(log N)` for irrational
  slope; density zero. Independent of the missing equality oracle.
- **Rational-slope branch: EVENTUAL only.** `Δ_c` square ⟹ eventual zero unless
  `β` is integral; in the integral-offset case the eventual value is `0` or `−1`
  by `sign(c₁)`. The **finite prefix is still ungated** (§5.3 item 1 applies to
  both branches).
- **Unconditional automaticity.** Every discrepancy sequence is
  Ostrowski-automatic (positional if `Δ_c` square), since a finite modification
  of an automatic sequence is automatic. *"What remains open is the word
  effectively."*

### 3.4a The exact Beatty norm-gap certificate — PROVEN

The search-free recognizer that does most practical work. For `u = v = 0`:

```
K = p²+4r    c = λ+β    C = r·c²/λ³    ρ = r(q(p+q) − r)    G = K + ρ
Criterion:  G > 0,  strict contraction,  strict endpoint trapping,  K√K·C < G
Conclusion: xₙ < sₙ < xₙ + C/n   and   ⌊sₙ⌋ = ⌊xₙ⌋   for all n ≥ 1
Witness:    Q = Kn + pq − 2r,  T = Km − r(p+2q),  T² − pTQ − rQ² = KF,  F ≡ ρ (mod K)
```

Named families: the unit-numerator strip `r = 1, −p ≤ q ≤ 0`, and the scaled
wedge `q = −p, 1 ≤ r ≤ p`. The criterion also accepts further triples.
**Genuinely uncovered:** `(p,q,r) = (1,0,3)`, where the floor conclusion really
fails and `TryCreate` correctly returns false.

**Shifted form.** `TryShiftedExactBeattyTrapCertificate` applies the native
recognizer to `(p, q−p, r, 0, 0)` via `tₙ = sₙ₋₁`, checking `t₁ = q + r/s₁`. This
covers `B(n) = r(n+1)²` cases well beyond the two named slices — which is why the
open region is smaller than the simple closed forms suggest (§4.3).
[trunk §8; connection-coordinate (L5c)]

### 3.5 Exact exclusion families — each rules out an infinite region

Every row here **excludes tuples**. Structural obstructions that exclude nothing
are in §3.5a; do not confuse them.

| Region | Result | Search? | Source |
|---|---|---|---|
| `(p,0,r,2r,r)` with `p < r ≤ 2p` | `s₁ ∉ Z`. Uniform depth ≤ 7. **No nonsquare hypothesis.** | search-free | lerch-q0-linear-wedge (2) |
| `(p,0,r,2r,r)` with `1 ≤ r ≤ p` | `s₁ ∉ Z`, by the shifted norm-gap / scaled-BDS argument | search-free | lerch-arithmetic (34); §3.4a |
| `(p,0,r,2r,r)` with `r ≤ 40p` | `s₁ ∉ Z`. 35,501,112,405 seeds survive `Q₃`; 21,159,528 survive `Q₄`; all dead by `Q₄₆`. Extremal seed `(p,r,d) = (1,37,9)`. *"40 and 46 … are not asserted to be optimal."* | machine-discharged finite case split | lerch-q0-linear-wedge |
| `(p,0,r,2r,r)`, any fixed `r ≤ Cp` | `Q₀..Q₄ > 0 ⟹ p < 3d(⌊3d²/4⌋+1)`, so each wedge reduces to finitely many seeds. Forces `r/p → ∞` for any delayed family. | search-free reduction | lerch-q0-linear-wedge (24) |
| Repeated root, `c := p(k−1) − q ≥ 0` | `0 < |r − c(p+c)| ≤ (p+c)/(k+1) ⟹ h ∉ Z` (hence `s₁ ∉ Z` via `s₁ = p+q+h`). Uniform in all four parameters; **valid in the nonsquare nonaligned region**. | search-free | repeated-root-affine-trap (19) |
| Any family with a known rational anchor | (S4) multiplicative anchor trap; (S5) `h ∈ Q ⟹ B > r_*/(A_*|r−r_*|)`; (S6) integer-exclusion band around **every** classified resonance | search-free | repeated-root-affine-trap §1 |
| `Δ_c` square: `(P, P(k−1), R, R(2k−1), Rk(k−1)+h)` with `R = c(c+P)` | `s₁ ≠ k(P+c)`, by paired forcing. `Δ_B = R(R−4h)`, **nonsquare when `h=1, R>4`** | search-free | paired-forcing |
| Square-numerator, nonsquare-`Δ_c`, nonaligned; native chart `b>0, c−b>0` | `min(c, c−ax) < M/λ < max(c, c−ax)`, strictly (endpoint equality also excludes). Refinements: `T > b` always; `a<0` ⟹ lower endpoint `max(b, c−ax)`; `a=0` ⟹ `M/λ = c` exactly | search-free | euler-moment (5) |

### 3.5a Structural obstructions — these exclude NOTHING

| Region | Statement | What it does not do |
|---|---|---|
| `Δ_B` nonsquare, any seed | `½ ≤ liminf log E_N/(N log N) ≤ limsup ≤ 1`. No positivity or minimality assumed. | Excludes *exponential factorial reduction*, not any tuple |
| `Δ_B` nonsquare, any seed | `log E_N/N → +∞` **(SE)** | *"presently supplies no contradiction with positivity"* |

Both are necessary conditions on a hypothetical counterexample. See §4.3.
[factorial-density (9); beatty-shadow (SE)]

### 3.6 Transcendence — the square branches, at the first tail

- **`Δ_c` square, Lerch slice, all `q`:** `s₁` transcendental unless
  `q = −μ`, which is the exact affine tail `sₙ = λn`. By Baker, after contiguity
  moves `a` into `(0,1]` and the Euler integral becomes a nonzero algebraic
  linear form in logarithms. [lerch-arithmetic (37g); lerch-q0-linear-wedge (30)]
- **Aligned repeated-root line, `Δ_c` nonsquare:** `T = 0 ⟺ a = ½`, so
  `Φ = 2·arctan√t/√t`, transcendental by Lindemann–Weierstrass.
  [repeated-root-regular-line (22)]
- **`Δ_c` square, repeated-root regular line — complete:** `h_reg ∈ Q ⟺
  a ∈ {0,−1,…,1−k}`, exactly the rational-function resonances (**empty for
  `k=0`; `{0}` for `k=1`**); otherwise transcendental (Baker).
  [repeated-root-regular-line (25)]

> **Scope, verbatim:** *"All residual **first-tail** special-value difficulty
> [in the Lerch slice] requires `D ∉ Q`."* Nothing is claimed for `s_N`, `N > 1`,
> nor outside the Lerch slice.

### 3.7 Repeated root: the image line — PROVEN closed and eliminated

For `B(n) = r(n+k)²`, the one-period transfer matrix
`M_ℓ = T_{ℓ−1}⋯T_0`, `T_n = [[0,1],[B(n+1), −A(n+2)]]`, satisfies
`det M_ℓ = (−1)^ℓ ∏_{x∈F_ℓ}B(x) = 0` and — **in the nonaligned case** —
`tr M_ℓ = R/r ≠ 0`. (When `R = 0` the matrix is nilpotent and every state dies
within two periods.) So `M_ℓ` is rank one, with an **image** line and a **kernel**
line.

1. **Image line.** Prime-*independent*, computed by a finite integer orbit, and
   lifting **exactly** to the singular Gauss solution: `S_k·P_k(y) = W_k`. Proven
   never to be the kernel line at any good inert prime. **When `D ∉ Q` and
   `T ≠ 0`** it is eventually sign-alternating, hence never the positive orbit.
   At the resonance it *is* positive: in the canonical slice
   `(p,−p,r,2r,r)` with seed `h = r/p`, positivity holds **iff `r = 2p²`**, where
   `Qₙ = pⁿ(n+1)!` and `F_img(z) = (1−pz)^{−2}` — this is why the resonance must
   be excluded separately. General-`k` elimination is by the alternation
   argument, not by an iff.
2. **Regular line** `H_reg = ₂F₁(k+1,k+1;C;t)` with `C = k + 3/2 − T/(2D)` — the
   characteristic-zero antecedent of the (varying) kernel line, and **the entire
   remaining problem**. Seed
   `h_reg = (r/D)·((k+1)²/C)·₂F₁(k+2,k+2;C+1;y)/₂F₁(k+1,k+1;C;y)`, with
   `s₁ = p + q + h_reg`.

Density payoff: the image line's nonkernel primes contain the whole inert half,
giving `liminf log E_N/(N log N) ≥ 1/2`. [hasse-kernel-line §4]

**The explicit kernel line.** `H_ℓ(t) = (m!/(C)_m)·P_m^{(C−1, C^ℓ−1)}(1−2t)`
(Galois-conjugate Jacobi parameters), and `h_ℓ = (r/D)·H'_ℓ(t₀)/H_ℓ(t₀)`.
**At most one integer seed** per exceptional recurrence can have exponentially
bounded factorial denominators, with a quantitative `(k−1)/k` version for finite
seed sets. [hasse-kernel-line (8)–(14a)]

**The Hasse–Lerch quotient (`q = 0`).** With `S_ℓ = Σ_{j=0}^{ℓ−2} x^j/(a+j)` and
`L_ℓ = a·S_ℓ + 1/x`: `M_ℓ = μL_ℓ ∈ F_ℓ`, `h_ℓ = M_ℓ − p`, and a fixed rational
`M` is on the kernel exactly when `L_ℓ = M/μ`. Frobenius law `L_ℓ^ℓ = x·L_ℓ`
(clean — no defect term), and the prime-independent `H_ℓ(a) = (1+a)/(1−a)`. This
replaces a length-`ℓ` transfer product by one finite-field special value and is
the sharpest handle on §5.3 item 4. [hasse-lerch-quotient (5),(6),(9),(13),(14)]

**Certified finite-prime lower bounds (P).** For any finite set `S` of surviving
primes, `liminf log E_N/N ≥ Σ_{ℓ∈S} log ℓ/(ℓ−1)`. Finite prime data therefore
*does* yield rigorous lower bounds, even though it cannot prove nonconcentration
(§4.2). Measured rates at depth 500 with primes ≤ 31: `3.144300` for
`(1,0,8,11,3,3)`; `3.776906` for `(1,0,1,0,1,1)`. [beatty-shadow §8]

### 3.8 The `k`-family of regular lines is an illusion — PROVEN

Reindexing `q₀ = q − p(k−1)` makes `a = (μ+q₀)/D` **independent of `k`**, and
each Riccati step is a projective automorphism (`det 𝓡_j = −r(j+1)² ≠ 0`) with
composite `det 𝓜_k = (−1)^{k−1}r^k(k!)² ≠ 0`. Hence the **equivalence**:

```
h_reg ∈ Q   ⟺   (1/λ)·Φ(−μ/λ, 1, (μ+q₀)/D) ∈ Q
```

*"The general repeated-root regular line has not created a new special-value
problem: it is the Lerch hard core, viewed at a later Riccati index."* The
reduction is closed; **the value problem is not — "It is not solved here."**
[repeated-root-regular-line]

### 3.9 Structural constraints that close escape routes — PROVEN

- **Kimura dichotomy** *(in the square-numerator, nonsquare-`Δ_c`, nonaligned
  branch (P1))*: Liouvillian-solvable ⟺ `b₊ ∈ Z` or `b₋ ∈ Z`, and every solvable
  case is reducible. **No irreducible dihedral family, no finite-monodromy
  family.** `b±` are the rational numerator-factor offsets and are undefined when
  `Δ_B` is nonsquare. [connection-coordinate (G4)]
- **No negative-integer Gauss parameters.** `a`, `c`, `c−b` are excluded by
  *irrationality* (nonalignment); `b` and `g` by *numerator positivity*. Kills
  every terminating and Euler-transformed-terminating degeneracy.
  [exceptional-equality-constraints]
- **Transformation rigidity.** `θ₀, θ∞` have opposite nonzero irrational parts;
  no Kummer, contiguous, rational-pullback, or rational-gauge Bauer–Muir chain
  can rationalize them. **Global, not pointwise** — it "does not rule out an
  algebraic value of `F'(x)/F(x)` at the single algebraic point `x`."
- **The quadratic-symmetry slice** `b+g = 1` (i.e. `u' = r`, locus
  `B(n) = rn²+rn+v` with `r²−4rv = d²`) is where classical quadratic
  transformations *do* buy something: a rational argument
  `t² = p²/(p²+4r)` and denominators `1/2, 3/2`. But numerator parameters still
  carry `R/D`, so it "supplies neither a one-period nor an algebraic connection
  coordinate." Called the most plausible classical-transformation search locus.
- **No hypergeometric-term escape.** Rationality of ratios would force
  `L ∈ Q(n)`, but the Riccati slope solves `c²+pc−r = 0`, irrational when `Δ_c`
  is nonsquare. [hasse-kernel-line §5]
- **Pullback obstruction.** No admissible PCF operator is a nonconstant rational
  pullback plus common scalar gauge of `F₀`'s Euler operator — parameter-uniform,
  seed-independent. Untwisted rational 2×2 module gauges are ruled out separately
  by determinant monodromy. **Scope, verbatim:** *"A Darboux transformation
  combined with a non-rational rank-one twist is a strictly broader operation …
  The theorem does not silently identify that broader category with a scalar
  gauge."* The obstruction is exactly strict positivity: at `B(1) = 0` the
  operator genuinely *is* such a pullback. [positive-egf-pullback]
- **Homogeneity.** §1.3's scaling law makes integer hits a property of the ray,
  so a counterexample can never be isolated — it would scale into infinitely
  many. [projective-rationality]

### 3.10 Euler-moment hierarchy — SEMIDECIDED (nonaligned square-numerator)

A proposed equality determines **every** moment exactly in `Q(√Δ_c)` via
`(b+k)m_k − (c+k+x(b+k+1−a))m_{k+1} + x(c+k+1−a)m_{k+2} = 0`, so every Hausdorff
inequality `E[t^k(1−t)^j] > 0` must hold.

**Termination theorem.** By Poincaré–Perron the genuine moments occupy the unique
recessive line; a false target's error grows like `(1/x)^k`, `|1/x| > 1`, with
alternating sign. Hence in the **nonaligned square-numerator branch**:

```
T ≠ T*   ⟺   some finite regularized Hausdorff witness excludes T
```

**One-sided.** A true equality survives every finite level; the source's own
Status says *"It does not decide every remaining hypergeometric equality."*

Operational subtleties:
- Correct test is the **sign-normalized** `W_{K,J}·W_{K+i,J+l} > 0`, not
  `W_{K+i,J+l} > 0` — continuation from a non-native chart can flip the common
  prefactor. Canonical shifts `K = max(0,⌊−b⌋+1)`, `J = max(0,⌊b−c⌋+1)`.
- Double-zero resonance (`N=1`, `B(n) = rn²`) is removed by the Riccati step:
  `s₁ = A₁+d ⟺ s₂ = r/d` (impossible for `d ≤ 0`). At `N=2` both offsets are 1.
  With this, **every** viable nonaligned square-numerator candidate has a
  nonresonant positive chart.
- No fixed low order suffices: `(2,−1,6,23,7,5)` survives through total order 10
  and fails first at `E[t⁵(1−t)⁷]`, order 12, in the **reversed** orientation.

The 256 cap is an implementation guard, not a claimed bound. [euler-moment]

---

## §4 What is REFUTED — do not re-attempt

### 4.1 False statements

**"An integer hit forces an exact-affine tail."** **FALSE.** For every `p ≥ 1`:

```
(p, q, r, u, v) = (p, 0, 2p+4, 4p+12, 0)
sₙ = (p+2)n + 2 − 2/(n+1)     satisfies (T-rec) identically
s₁ = p+3 ∈ Z,   R_aff = −2(p+4) ≠ 0   (non-affine)
```

Smallest member `(1,0,6,16,0)`. Refuted by symbolic identity, not search. The
reason it was missed: *"the original coefficient box was too small."*
[integer-counterexamples; beatty-shadow §6]

**"Linear-fractional exhausts the rational branch."** **FALSE.**
`(1,0,2,3m+2,0)` realizes denominator degree exactly `m` for every `m ≥ 0`;
`(1,−1,2,7,0)` is a degree-2 example beyond the linear-fractional API.

**"Stratum (E) is equality-free."** **FALSE.** `scaled_bds_floor` gives an
equality family **wholly inside** (E); `generalized_bds_floor` lies inside except
on the aligned slice `2q = −k`. Correct statement:

> **(E) is an obstruction to the uniform method, not a region in which exact
> Beatty equality is necessarily absent.**

**"Positivity + integrality + holonomy + finite positive radius forces factorial
reduction."** **FALSE.**
`F₀(z) = ¼((2+√2)(1−z)^{−√2} + (2−√2)(1−z)^{√2}) = Σ Pₙzⁿ/n!` with
`Pₙ₊₂ = (2n+1)Pₙ₊₁ + (2−n²)Pₙ`, `P₀ = P₁ = 1` — integral, strictly positive,
minimal operator `(1−z)²F₀'' − (1−z)F₀' − 2F₀ = 0` with indicial roots `±√2`,
and `½ ≤ liminf log E_N/(N log N) ≤ 1`, so `limsup log E_N/N = +∞`.

> **Scope, verbatim:** *"The example deliberately does not have the two distinct
> quadratic-conjugate finite singularities of the polynomial-tail equation. It
> therefore does not disprove a theorem exploiting that extra geometry."*
[positive-egf-arithmetic]

**Published-draft correction.** Kenison et al. 2026 (§13) prints `μ²/α` in its
equations (10)–(11); the correct prefactor is `μ/α` — multiplying all partial
denominators by `δ` and all partial numerators by `δ²` scales the fraction by
`δ`. Formalized as `equivalence_prefactor_one_power`; guarded by comparison to
100,000-level convergents.

### 4.2 Dead methods, with the invariant that kills each

Test your own variant against the **invariant** column, not the name.

| Method | Killing invariant | Source |
|---|---|---|
| **Galois conjugation of the special value** | The Wronskian `W(F,K) ≠ 0`. Not a gap — a disproof: `(F†)'(1/x)/F†(1/x) − k^τ = −x²W(F,K)(x)/(F(x)K(x)) ≠ 0`. *Proved* in connection-coordinate (D7); **independently identified as a gap, not disproved,** in euler-moment (O4) and positive-egf-arithmetic §2 | connection-coordinate (D7) |
| **Fixed-depth orbit-sign testing** | Any depth `N` is beaten by construction. Proved **three** times in disjoint branches: integral rescaling (positive through `N−1`, exactly zero at `N`); `Qₙ` polynomial in `c` with leading `(k)ₙcⁿ`; the image-line family `p=P, h=2P−1, r=P(2P−1)` with `Δ_c = P(9P−4)` nonsquare | lerch-delayed-failure; paired-forcing; hasse-kernel-line (34) |
| **One-embedding approximation of any depth** | **Norm saturation lemma:** for the Lerch target with `S_N` the `N`-th partial sum, `N²·Norm_{Q(D)/Q}(K_M − S_N) → r/(p²+4r) > 0`. An exact floor, not a lossy bound; and denominator clearing costs `2N log N + O(N)`. **This is a property of the value, so it applies to any single-embedding scheme** — diagonal, non-diagonal, or Hermite–Padé with more forms | lerch-arithmetic (20)–(22) |
| **Ordinary diagonal Padé (arithmetic side)** | Denominator height `log N𝔟ₙ ≥ n log n − O(n)`, genuine (split-prime valuations + quadratic-character PNT), not an artifact of the Pochhammer factor. Conditional both ways — see §5.3 item 5 | lerch-arithmetic (33a)–(33g) |
| **Finite prime computation for nonconcentration** | CRT: an infinite AP of seeds of density `∏ℓ^{−1}` agrees with the kernel on any finite prime set. (Finite primes *do* give certified lower bounds — §3.7 (P)) | hasse-kernel-line (16) |
| **Grothendieck–Katz / p-curvature** | Wrong direction. Rank-one kernel at every good inert prime is neither zero p-curvature nor a full horizontal basis; GK gives no converse from rank one on a density-½ set | hasse-kernel-line §6 |
| **Chebotarev on the Hasse–Lerch quotient** | Summation length, parameter, **and** Frobenius relation all vary with `ℓ` — not Frobenius traces of a fixed extension | hasse-lerch-quotient |
| **Gelfond–Schneider** | Proves `t^a` transcendental, which the hypothetical equality already forces. Needed instead: `B_y(a,1−a) ∉ Q̄·t^a`, a **linear-independence** statement | lerch-arithmetic (13)–(15) |
| **Baker in the nonsquare-`Δ_c` case** | ⚠ **The obvious generalization of §3.6.** Baker closes the square branch because `a, t` are then *rational*, making the Euler integral a linear form in logs of algebraic numbers with rational exponents. When `Δ_c` is nonsquare, `a` is a quadratic irrational **linked to the argument**, and the integral is no longer such a form. No source claims this is refuted — it is **untried and believed out of reach**; §5.1's "all cited machinery needs rational parameters" is the relevant statement | §5.1; lerch-arithmetic §"What remains" |
| **Numerical detection (PSLQ/LLL) then certification** | Not refuted, but **no height bound is known** for the putative rational value, so a negative search proves nothing and there is no stopping rule. §4.3's non-effectivity applies | [synthesis] |
| **Arithmetic Gevrey duality** | Circular: arithmetic Gevrey order 1 *is* exponential control of `Qₙ/n!` denominators | positive-egf-arithmetic §2 |
| **Pólya–Carlson** | `ΣQₙzⁿ` has radius zero; EGF coefficients non-integral; rescaling by `mⁿ` would give `E_N | mᴺ`, the impossible reduction | positive-egf-arithmetic §2 |
| **Two-line order arguments for irrationality** | The trap has positive length for `r ≠ r₀`, so it contains rationals of arbitrarily large denominator. *"A complete rationality theorem still needs arithmetic information about the Lerch connection coordinate, rather than a further real inequality of this form."* | repeated-root-affine-trap §4 |
| **Density-one nonkilling as a target** | Provably unattainable. Chebotarev ceilings: distinct quadratic fields → nonkilling lower density ≥ ½, upper ≤ ¾; same field → exactly ½, seed-independent; `R=0` with `Δ_B` nonsquare → exactly numerator inertness, density ½; branch (E) → **every seed ≤ ½**. Correct target is divergent **Mertens weight** on the nonkernel subset of the inert half | factorial-density §5 |
| **Matching exponent differences mod Z** | Missing datum is the resonance obstruction. `(2,10,1,3,2)` matches the pattern but has a *logarithmic* point at infinity (`−16 ≠ 0`) | positive-egf-pullback §5 |
| **Small-root archimedean contradiction** | `Qₙ/(n−1)! → 0` for `r ≤ p` does not contradict the modular divisor theorem: that mass is in the **denominator** of `Qₙ/n!`. Quantified: forced `½N log N` denominator mass is matched by `≥ ¼N log N` numerator height | factorial-density §4 |
| **Global-boundedness algorithms (Matveeva 2025)** | Classifies algebraic/globally-bounded solution *lines* under extra hypotheses; exact tail equality selects the *minimal analytic* solution, whose gauged coefficients need not be globally bounded. *"the paper explicitly leaves the general nonzero exponential-factor case open"* | beatty-shadow §8 |
| **Ultimate-sign classification (Hagihara–Kawamura 2025)** | Halts on almost every initial value, but reduces the remaining **unstable line** to the open Minimality Problem — and an exact integer tail equality sits exactly on that line | beatty-shadow §8 |

### 4.3 Traps in reading the corpus

- **Reducible ≠ rational tail.** `(1,0,3,4,1)` is reducible (a Kimura integral
  case) yet has **no** rational-function tail — an incomplete-beta quadrature
  with quadratic-irrational exponent.
- **Transformation rigidity is global, not pointwise.**
- **Projective rationality is not effective:** "the required clearing scale is not
  known before the value is known."
- **The open region is smaller than the simple statements suggest** — §3.4a's
  shifted `NormGap` recognizer covers much beyond the named slices.
- **(SE) decides nothing.** Outside (E) it forces superexponential growth with no
  contradiction against positivity; inside (E) it is evaded by the kernel
  condition. *"These are two differently constrained parts of the same open
  minimality problem, not a decided region and one residual region."*
- **A full Hasse-kernel theorem would still not finish integer equality** —
  factorial-density growth is compatible with positivity.

---

## §5 What is OPEN

### 5.1 The core

> Decide whether one Lerch / incomplete-beta value at **linked
> quadratic-irrational argument and parameter** is rational.

Faces (a), (b), (d) are the general statement; **(c) is the `q=0` repeated-root
normalization only**, not a fourth face of equal generality.

```
(a)  F'(x)/F(x) ∈ Q(D)                                   general nonaligned
(b)  (1/λ)·Φ(−μ/λ, 1, (μ+q₀)/D) ∈ Q                      general repeated root
(c)  𝓕(ρ) = c·t^{−a}·B_y(a,1−a) ∈ Q,  ρ = r/p²           q=0 repeated root only
     where D₀ = √(1+4ρ), c = (D₀−1)/2, t = c/(1+c), a = c/D₀ = y
(d)  B_y(a,1−a) ∉ Q̄·t^a                                  the linear-independence form
```

(b) is **equivalent** to the full repeated-root projective rationality problem:
a counterexample gives a rational regular seed after every large shift `k`, and
homogeneity then gives an integral representative with an integer hit.

In (c), `y = a` because `t/(1+t) = c/(1+2c) = c/D₀`; that is why `B_y` and `B_a`
are the same object there.

**Why no existing machinery applies.** Lai (arXiv:2203.00207),
David–Hirata-Kohno–Kawashima (arXiv:2511.06534), and Bhattacharjee
(arXiv:2607.16331) all require **rational** hypergeometric parameters. Here
`a, c ∉ Q` precisely when `R ≠ 0` and `Δ_c` is nonsquare.

### 5.2 Where the difficulty lives **[partly synthesis]**

The Fuchsian equation of the cleared orbit's EGF `F(z) = ΣQⱼzʲ/j!` is

```
(1 + pz − rz²)F'' = ((3r+u)z − (2p+q))F' + (r+u+v)F
```
[beatty-shadow §8, GeneratingFunction.lean]

Its exponent differences: at the finite singularities
`−(r+u)/(2r) ± R/(2r√Δ_c)`, rational ⟺ `R = 0` or `Δ_c` square; at infinity,
discriminant `Δ_B/r²`, rational ⟺ `Δ_B` square. Hence

```
all exponent differences rational  ⟺  Δ_B square ∧ (Δ_c square ∨ R = 0)
```

which is exactly the one-period locus. Therefore

```
{irrational local exponent} = Δ_B nonsquare ∨ (Δ_c nonsquare ∧ R ≠ 0)  ⊋  (E)
```

**Stratum (E)** is the square-numerator part of that: `Δ_B` square, `Δ_c`
nonsquare, `R ≠ 0`. It is where the exponent at infinity is rational but the
finite ones are not. The rest of the irrational-exponent locus is `Δ_B`
nonsquare, where the one-period reduction also fails but the Euler-moment
machinery does not apply either.

Within (E), the still-open `(p,0,r,2r,r)` locus additionally needs `r > 40p`.

### 5.3 Named open problems

1. **Totalize the finite Beatty prefix.** For `n < N*`, nested rational
   enclosures decide the floor *unless the limit is exactly an integer*. Applies
   to **both** slope branches. *"This obligation is not routine numerical
   cleanup."*
2. **Decide positivity** for the restricted integral degree-(2,1) recurrence — or
   prove every positive instance has a hypergeometric solution.
3. **Positivity ⟹ factorial reduction?** Must use the PCF equation's specific
   two-singularity connection geometry (§4.1's counterexample lives outside it).
   Two documents independently isolate this as the same missing implication.
4. **Finite-field Lerch nonconcentration.** For every fixed integer seed `h`, do
   the primes where the kernel residue differs from `h` carry divergent Mertens
   weight? *"No theorem currently in the project proves that assertion."*
   **Conditional shortcut:** if a classified rational/factorial solution supplies
   one seed `h₀` in the kernel at all but finitely many good inert primes, then
   every other seed has divergent nonkernel weight and the classification is
   complete for all `h ≠ h₀`.
5. **Padé denominator gcd.** Exponential height after evaluation is *equivalent*
   to `log N((Cₙ)+(Uₙ)) = 2n log n + O(n)`. Proving the near-total gcd gives the
   theorem; proving survival retires ordinary diagonal Padé. **Both open.**
6. **Stable-line membership.** Recognize in finite time that the algebraic seed
   `b/T` lies on the unique completely monotone minimal moment line — *"the
   central minimality problem in Hausdorff-moment form."*
7. **Near-full-factorial numerator divisibility.** If positive `L_N | Q_N` along
   an infinite subsequence with `log(N!/L_N) ≤ κN + o(N)` and `κ < −log c`, a
   small-`c` stratum closes. Present results give `½N log N` and sit on the
   **denominator** side — far from this threshold in both location and size.
8. **Internalize the trace formula (T)** in Lean, plus the truncated-EGF
   equivalence.
9. **Why paired forcing does not generalize:** *"A general positive minimal orbit
   has no known normalization that produces a one-sign summable forcing term."*

### 5.4 Uncovered regions — difficulty not assessed by any source

- **Repeated root, `Δ_c` nonsquare, nonaligned, `c < 0`** (i.e. `q > p(k−1)`):
  neither an order bound nor an arithmetic theorem. The affine trap assumes
  `c ≥ 0`; the regular-line classification is complete only for `Δ_c` square and
  on the aligned line. Note the crux: `c < 0` puts `c` inside the denominator
  `(p+c)` of the exclusion band, so the sign of `p+c` is what any extension turns
  on. **Do not assume this is clerical** — §4.2 records that two-line order
  arguments provably cannot finish the job.
- **The fifth survivor.** The 11,159,802 × 9,191,436 search to depth 2,000 left
  **five** non-affine survivors; four were the refuting family. The fifth is
  never identified.
- **At most one exceptional seed** may have exponentially bounded factorial
  denominators; whether it exists outside known loci is open.

---

## §6 Verification

### 6.0 Implementation surface

All in `src/Puck.Maths/Research/` unless noted.

| File | Entry points |
|---|---|
| `PolynomialContinuedFractionTail.cs` | `Analyze`, `AsymptoticCoefficients(termCount)`, `CertifiedInterval`, `VerifyIntervalCertificate`, `Cutoff` |
| `PolynomialExactBeattyTrap.cs` | `TryCreate`, `TailFloor`, `NormWitness`, `TryShiftedExactBeattyTrapCertificate` |
| `PolynomialRationalTail.cs` | `TryLinearFractionalTailCertificate`, `VerifyLinearFractionalTailCertificate`, `TryCertifiedRationalTail`, `TryRationalTailCertificate`, `VerifyRationalTailCertificate` |
| `PolynomialTailMinimalityReduction.cs` | `TryDegreeOneMinimalityReduction` (retired), `TryOnePeriodEqualityReduction` |
| `PolynomialTailEulerMoment.cs` | `TryEulerHausdorffIntegerExclusionCertificate`, `TryEulerMomentRegularization`, `TryEulerRegularizedHausdorffIntegerExclusionCertificate` |
| `PolynomialTailPairedForcing.cs` | `PolynomialTailPairedForcingExclusionCertificate` |
| `PolynomialBeattyShadow.cs` | `EventualCertificate`, `TryCertifiedFloor`, `TryTotalOstrowskiAutomaton`, `TryTotalPositionalAutomaton`, `IdenticallyZero`, `FirstCounterexample`, `RationalSlopeDecisionCertificate` |
| `PellEquation.cs`, `QuadraticSurd.cs` | fundamental unit; exact quadratic arithmetic |
| `MetallicPolynomialContinuedFraction.cs` | `TailFloor(metallicIndex, tailIndex)`, `Analyze(k)` |

Certificate types: `PolynomialTailIntervalCertificate`,
`PolynomialTailAsymptoticCertificate`, `PolynomialBeattyShadowNormCertificate`,
`PolynomialBeattyBoundaryAsymptoticCertificate`,
`PolynomialBeattyShadowNormDecisionCertificate`,
`PolynomialBeattyShadowOstrowskiCertificate`,
`PolynomialExactBeattyTrapCertificate`, `PolynomialRationalTailCertificate`.

### 6.1 Lean

Project `formal/PuckMathsFormal`. Full build:
`cd formal/PuckMathsFormal && lake exe cache get && lake build`.
Narrow: `lake build PuckMathsFormal.PolynomialTail`.

| Name | File | Content |
|---|---|---|
| `BDS.bds_conjecture` | `BDS/Theorem.lean` | metallic slice, all `k,n ≥ 1`; trust-level zero |
| — | `BDS/Recurrence.lean` | compactness lemma |
| `PolynomialTail.GeneralizedBDS.generalized_bds_floor` | `PolynomialTail/GeneralizedBDS.lean` | `−k ≤ q ≤ 0`; inside (E) except `2q = −k` |
| `…GeneralizedBDS.generalized_bds_ne_integer` | same | decides `r = 1, 0 ≤ q ≤ p` |
| `PolynomialTail.ScaledBDS.scaled_bds_floor` | `PolynomialTail/ScaledBDS.lean` | `1 ≤ r ≤ p`; **wholly** inside (E) |
| `…ScaledBDS.scaled_bds_ne_integer` | same | decides `q = 0, 1 ≤ r ≤ p` |
| `…LinearFractional.nonaffine_positive_integer_classification` | `PolynomialTail/LinearFractional.lean` | linear-fractional classification, both directions |
| (namespace) `PolynomialTail.Rational` | `PolynomialTail/Rational.lean` | `riccati_of_certificate`, `degree_equation_of_numeratorIdentity`, `cConstant_ne_zero`, `positive_everywhere_of_eventually_positive`, `eventually_contracting_unique`; generic over a field |
| (namespace) `PolynomialTail.MinimalityReduction` | `PolynomialTail/MinimalityReduction.lean` | `shifted_numerator_factorization`, `aligned_hypergeometric_parameter`, `equivalence_prefactor_one_power`, … |
| (namespace) `PolynomialTail.IntegerOrbit` | `PolynomialTail/IntegerOrbit.lean` | `cleared_orbit_pair_ne_zero`, Riccati/orbit correspondence |
| (namespace) `PolynomialTail.GeneratingFunction` | `PolynomialTail/GeneratingFunction.lean` | the Fuchsian equation and exponents |
| (namespace) `PolynomialTail.FiniteFieldMonodromy` | `PolynomialTail/FiniteFieldMonodromy.lean` | `det_monodromy_eq_zero_iff_discriminant_square`, `two_periods_kill_of_trace_det_zero`; no `sorry` |

> **Not formalized:** the general trunk theorem; the trace formula (T); the
> paired-forcing lemma; the valuation/divisor bookkeeping and the
> quadratic-character prime-distribution estimate; the truncated-EGF equivalence.
> **And nothing in §3.4 is in Lean** — the finite-norm reduction, Pell-orbit
> reduction, sign stabilization, finite-channel/DFAO theorem, sparsity, and
> rational-slope classification are written-proof-only.

### 6.2 Verifiers

Commands as given by the sources. Some need `-c Release`, `--no-restore`,
`--property:NuGetAudit=false`, or positional arguments; defaults will not
reproduce the cited counts. Two entries are Python, not `dotnet`.

```bash
dotnet build src/Puck.Maths/Puck.Maths.csproj -c Release --no-restore
dotnet run -c Release tools/maths-battery.cs
dotnet run -c Release tools/polynomial-continued-fraction-verifier.cs        # trunk; 29,040 families
dotnet run -c Release tools/bds-metallic-mean-verifier.cs                    # BDS cross-check
dotnet run tools/polynomial-tail-rational-verifier.cs
dotnet run tools/polynomial-tail-rational-box-verifier.cs -- 12 24           # 4,019,652 → 1,777
dotnet run tools/polynomial-tail-linear-fractional-verifier.cs               # 33,956 families
dotnet run tools/polynomial-tail-integer-counterexample-verifier.cs
dotnet run tools/polynomial-tail-minimality-reduction-verifier.cs            # 21,315
dotnet run tools/polynomial-tail-one-period-reduction-verifier.cs            # 22,649
dotnet run --no-restore tools/polynomial-exact-beatty-trap-verifier.cs
dotnet run tools/polynomial-tail-asymptotic-certificate-verifier.cs          # 4,032, order ≤ 6
dotnet run -c Release tools/polynomial-tail-euler-moment-verifier.cs         # ⚠ see §6.3
dotnet run --property:NuGetAudit=false tools/polynomial-tail-hasse-kernel-verifier.cs
dotnet run --property:NuGetAudit=false tools/polynomial-tail-hasse-lerch-quotient-verifier.cs
dotnet run --property:NuGetAudit=false tools/polynomial-tail-hasse-image-egf-verifier.cs
dotnet run --property:NuGetAudit=false tools/polynomial-tail-repeated-root-image-orbit-verifier.cs -- 100 200 500
dotnet run --property:NuGetAudit=false tools/polynomial-tail-repeated-root-regular-verifier.cs
dotnet run --property:NuGetAudit=false tools/polynomial-tail-repeated-root-affine-trap-verifier.cs -- 8 4 40 6
dotnet run --property:NuGetAudit=false tools/polynomial-tail-lerch-delayed-verifier.cs -- 100
dotnet run tools/polynomial-tail-lerch-q0-linear-wedge-verifier.cs -- 2000
dotnet run tools/polynomial-tail-lerch-q0-fixed-wedge-verifier.cs -- 40 1 64
dotnet run tools/polynomial-tail-paired-forcing-verifier.cs
dotnet run tools/quadratic-beatty-shadow-norm-verifier.cs
dotnet run tools/quadratic-beatty-shadow-channel-verifier.cs
dotnet run tools/quadratic-beatty-shadow-decision-verifier.cs
dotnet run tools/ostrowski-pell-channel-verifier.cs
dotnet run tools/pell-equation-verifier.cs
dotnet run tools/quadratic-surd-verifier.cs

# searches (evidence, NOT proof — see §6.4)
dotnet run tools/polynomial-tail-aligned-period-orbit-search.cs -- 200 300 600 10000
dotnet run tools/polynomial-tail-integer-orbit-search.cs
dotnet run tools/polynomial-tail-exceptional-slice-search.cs
dotnet run tools/ostrowski-pell-channel-search.cs
python tools/polynomial-tail-egf-arithmetic-search.py 8 20 500
python tools/polynomial-tail-padic-sieve.py 500 --cycle-prime-bound 31 --verify --sweep
```

### 6.3 ⚠ Verification hygiene

Two sources state their primary machine check **was deliberately not run**,
pending an unrelated repository build break:

- `tools/polynomial-tail-euler-moment-verifier.cs` — *"That command has
  deliberately not been run while the unrelated repository build break is owned
  by another task."*
- the Lean build of `PolynomialTail/IntegerOrbit.lean` — *"It has deliberately
  not been built while the unrelated repository build break is being handled
  elsewhere."*

The Euler-moment figures (24,023 candidates; 6,440 excluded by the interval test;
3,819 needing the reversed orientation; 12,495 excluded at order 20; orbits
nonpositive before depth 2,000) come from a separate read-only reproduction, not
that verifier. Its own hedge: *"This finite-box result is evidence, not a uniform
order bound."* **Re-run both before citing.**

### 6.4 The evidence/proof line

- *"The search is evidence only; the displayed polynomial identity is the
  proof."* [integer-counterexamples]
- *"The finite cross-check is not the proof of the universal quantifiers;
  equations (1)--(11) are. Its role is to catch transcription, indexing, and sign
  mistakes."* [bds-metallic-mean]
- *"This is evidence for rigidity, not a replacement for the missing proof."*
  [beatty-shadow, on 2,261,907 aligned tuples with zero survivors]
- *"100 is only the executable regression depth."* [lerch-delayed-failure]

---

## §7 Decision procedure **[synthesis — the most error-prone part of this page]**

Run in order. **Each outcome is labelled with which question it settles.** Q1 =
integer hit; Q2 = Beatty shadow. Closure for one does not imply the other.

**Step 0 — rational-function tail?** Run the degree quadratic of §3.2 (at most
two candidate degrees; over a quadratic field at most one). If a positive
rational tail exists, **Q1 and Q2 are both decided outright** — evaluate it.
This is orthogonal to everything below; do it first.
*Example:* `(1,−1,2,7,0)` has `sₙ = (6n³+21n²+13n−5)/(3n²+9n+5)`. Decided.

**Step 1 — norm-gap certificate?** Try `PolynomialExactBeattyTrap.TryCreate`, and
`TryShiftedExactBeattyTrapCertificate` on `(p, q−p, r, 0, 0)`. Success gives
**Q1 and Q2 for all indices**. Covers much more than the named slices (§3.4a).

**Step 2 — repeated root?** If `Δ_B = 0`, set `k = u/(2r)`, `q₀ = q − p(k−1)`,
`c = −q₀`. Then:
- Image line (seed `h = r/p` in the canonical slice): eliminated — §3.7.
- `q = 0` and `r ≤ 40p`: **Q1 closed** (`s₁ ∉ Z`) — §3.5.
- `c ≥ 0` and `0 < |r − c(p+c)| ≤ (p+c)/(k+1)`: **Q1 closed** — §3.5.
- `Δ_c` square: **Q1 fully classified** — §3.6.
- Aligned (`T = 0`): **Q1 closed**, transcendental — §3.6.
- Otherwise: the regular line — **Q1 OPEN**, the core (§5.1(b)). If `c < 0`,
  §5.4 applies: no result at all.

**Step 3 — alignment and discriminants.** Compute `R`, `Δ_c`, `Δ_B`.

| `Δ_B` | `Δ_c` / `R` | Q1 | Q2 |
|---|---|---|---|
| square | `Δ_c` square **and** `Δ_B` square | **REDUCED** to Kenison et al. 2026 — external, **not implemented here** (§3.2a) | eventual: closed; finite prefix open |
| square | `R = 0` (aligned), `Δ_B` square | **REDUCED**, same caveat | same |
| square | `Δ_c` nonsquare **and** `R ≠ 0` | **STRATUM (E)** — the open core. Euler-moment semidecides *false* targets (§3.10); interval test may exclude (§3.5) | prefix open |
| nonsquare | any | **OPEN.** (SE) forces superexponential denominators but *decides nothing* (§3.5a, §4.3). Paired forcing closes a specific family when `Δ_c` is square | eventual: closed if `Δ_c` square; else finite-channel DFAO |

**Step 4 — regardless of branch:** the finite Beatty prefix (§5.3 item 1) is
ungated everywhere. **No branch closes Q3.**

*Worked routings.* `(1,0,1,0,1)`: `Δ_B = −4` nonsquare → Step 3 row 4, Q1 open —
and indeed the sources use `(1,0,1,0,1,1)` as a live open example with
`log₂E_N/N` still increasing at index 499. `(4,0,8,16,8)`: `Δ_B = 0` → Step 2,
`k = 1`, `q = 0`, `r = 8 ≤ 2p` → **Q1 closed**. `(2,−1,6,23,7)`: `Δ_B = 361`,
`Δ_c = 28`, `R = 46` → (E); Euler-moment applies (`M = 5` dies at `Q₂₄`).

---

## §8 Attack surface **[synthesis]**

1. **Simultaneous two-embedding Padé.** The archimedean obstruction is exactly
   the connection constant `π t^{1−a}/sin(πa)` in the reflection formula
   `Φ(−t,1,a) + t^{−1}Φ(−t^{−1},1,1−a) = π t^{−a}/sin(πa)`. Approximate the pair
   `Φ(−t,1,a), Φ(−t^{−1},1,1−a)` so the reflection cancels the connection term.
   Named in lerch-arithmetic as the next attack and explicitly not attempted:
   *"No such estimate is proved here."* **Before starting:** you must beat both
   §4.2's norm-saturation floor `r/(p²+4r)` and the `2N log N + O(N)` denominator
   cost; and check `a ∈ (0,1)` in your instance (§3.9 rules out only negative
   integers). Existing apparatus is in lerch-arithmetic (19)–(33g).
2. **The Padé gcd, either direction** (§5.3 item 5). A negative answer is as
   valuable as a positive one.
3. **The conditional shortcut to nonconcentration** (§5.3 item 4) — cheaper than
   proving item 4 outright.
4. **Exploit unused rational anchors.** Every resonance classified in
   repeated-root-regular-line (25) is an anchor for (S4)–(S6), each yielding a
   fresh exclusion band. Mechanical, and currently unexploited.
5. **Push the `r ≤ Cp` wedge past 40p.** §3.5 row 4 shows every fixed wedge
   reduces to finitely many seeds, so this is CPU, not research — but it never
   reaches `r/p → ∞`, so it cannot close the branch.
6. **The `b+g = 1` quadratic-symmetry slice** (§3.9) — the most plausible
   classical-transformation locus, search locus `B(n) = rn²+rn+v`, `r²−4rv = d²`.
7. **Close the `c < 0` gap** (§5.4) — but read §4.2's two-line-order refutation
   first; this is research, not writing.
8. **Identify the fifth survivor** (§5.4). Cheap; a loose end in a
   publication-quality claim.

**Before any of these, re-read §4.** Several plausible routes into each are
already refuted, and §4.2's invariant column is what to test your variant
against.

---

## §9 Working conventions

- **Positivity is load-bearing.** `B(n) > 0` creates the pullback obstruction,
  excludes negative-index resonances, rules out zeros of the moment-recurrence
  divisor, and forces the same-strip condition on the Gauss offsets. Any argument
  that quietly relaxes it is probably wrong.
- **Exact arithmetic only** — `BigInteger` rationals and `QuadraticSurd`. No
  `double`, no fixed-point seam. Even in BDS the fixed-point lens "was a lens for
  finding the invariant; the proof itself is exact."
- **Never conceal a mathematical gap in an API.** *"The API deliberately does not
  pretend that shrinking rational enclosures decide equality"* — on an unresolved
  index it returns that index and no certificate, so *"there is now no
  engineering gap concealed inside the mathematical one."*
- **Lean-verified ≠ peer reviewed.** Cite with the caveat.
- **Record status with search bounds attached** to every "none found."

---

## §10 Document map

**Trunk and apex**

| Document | Owns |
|---|---|
| [polynomial-continued-fraction-tails.md](polynomial-continued-fraction-tails.md) | Existence/uniqueness, `λ/β/R_aff`, certified trap, asymptotics, rational-tail structure, norm-gap certificates |
| [uniform-beatty-shadow-theorem.md](uniform-beatty-shadow-theorem.md) | The three-part proposed theorem; eventual finite-channel/DFAO; sparsity; (SE); trace formula; stratum (E); the prefix gap |
| [bds-metallic-mean-conjecture.md](bds-metallic-mean-conjecture.md) | The solved metallic case, Lean-verified |

**Counterexamples and classification**

| Document | Owns |
|---|---|
| [polynomial-tail-integer-counterexamples.md](polynomial-tail-integer-counterexamples.md) | The refuting family; arbitrary-degree rational classification; the 11.16M search |
| [polynomial-tail-projective-rationality.md](polynomial-tail-projective-rationality.md) | Homogeneity; ray ⟺ rationality; `𝓕(ρ)`; deflation of delayed failure |

**Connection coordinate**

| Document | Owns |
|---|---|
| [polynomial-tail-connection-coordinate.md](polynomial-tail-connection-coordinate.md) | The coordinate; the Wronskian defect (D7); Kimura; the Lerch collapse |
| [polynomial-tail-exceptional-equality-constraints.md](polynomial-tail-exceptional-equality-constraints.md) | Same-strip condition; no negative-integer parameters; transformation rigidity; the `b+g=1` slice; the five-condition profile |
| [polynomial-tail-paired-forcing.md](polynomial-tail-paired-forcing.md) | The forcing lemma; an infinite exclusion family at square `Δ_c` |

**Lerch cluster** (repeated root)

| Document | Owns |
|---|---|
| [polynomial-tail-lerch-arithmetic.md](polynomial-tail-lerch-arithmetic.md) | Target collapse; norm saturation; the Padé wall; square-`Δ_c` transcendence |
| [polynomial-tail-lerch-delayed-failure.md](polynomial-tail-lerch-delayed-failure.md) | Delayed orbit failure; the scaling lemma; anti-silent-coverage |
| [polynomial-tail-lerch-q0-linear-wedge.md](polynomial-tail-lerch-q0-linear-wedge.md) | The `2p` and `40p` exclusions; the four-inequality reduction |
| [polynomial-tail-hasse-lerch-quotient.md](polynomial-tail-hasse-lerch-quotient.md) | The mod-`ℓ` mirror; Frobenius law; nonconcentration as a special value |

**Repeated-root / Hasse**

| Document | Owns |
|---|---|
| [polynomial-tail-hasse-kernel-line.md](polynomial-tail-hasse-kernel-line.md) | Hasse polynomial and kernel line; image/regular dichotomy; `liminf ≥ ½`; the `r = 2p²` resonance; why GK fails |
| [polynomial-tail-repeated-root-regular-line.md](polynomial-tail-repeated-root-regular-line.md) | `k`-independence; the projective equivalence; aligned transcendence; square-`Δ_c` classification |
| [polynomial-tail-repeated-root-affine-trap.md](polynomial-tail-repeated-root-affine-trap.md) | ⚠ **Despite the filename, this is about the regular line.** The comparison lemma (S1)–(S6); the two-sided trap; the exclusion band |

**Exclusion mechanisms**

| Document | Owns |
|---|---|
| [polynomial-tail-euler-moment-exclusion.md](polynomial-tail-euler-moment-exclusion.md) | Interval test; Hausdorff hierarchy; regularization; termination |
| [polynomial-tail-factorial-density-obstruction.md](polynomial-tail-factorial-density-obstruction.md) | `½ ≤ liminf`; Mertens criterion; density ceilings; the archimedean non-contradiction |
| [polynomial-tail-positive-egf-arithmetic.md](polynomial-tail-positive-egf-arithmetic.md) | The `√2` counterexample; why each arithmetic tool fails |
| [polynomial-tail-positive-egf-pullback.md](polynomial-tail-positive-egf-pullback.md) | Exponent-difference obstruction; `B(1)=0` sharpness; resonance certificate |

---

## §11 Notation hazards

| Symbol | Meanings in the corpus |
|---|---|
| `R` | (i) affine defect `(q−β)(λ+β)+v` [trunk §1; beatty-shadow §1]; (ii) alignment residual `p(u−r)−2rq` [everywhere else]; (iii) in paired-forcing, `R = c(c+P)` **is the numerator leading coefficient `r`** |
| `a` | (i) repeated-root shift in `B(n) = r(n+a)²` [hasse-kernel §4.1, renamed to `k` mid-document]; (ii) the Gauss/Lerch parameter [everywhere else] |
| `c` | (i) trap anchor `p(k−1)−q`; (ii) Gauss third parameter `a+g`; (iii) in projective-rationality, `c = (D₀−1)/2`; (iv) in factorial-density §4, the positive characteristic root `μ` |
| `A, B` | (i) `A(n) = pn+q`, `B(n) = rn²+un+v` [§1.1]; (ii) numerator/denominator of a rational solution `s = 𝒜/ℬ` [§3.2] |
| `T` | (i) the tail recurrence / `T_n(y) = A(n)+B(n)/y`; (ii) the Euler-moment target `M/λ`; (iii) `T = p(2k−1)−2q = R/r`; (iv) **the trace formula (T)** |
| `F` | (i) principal Gauss solution `₂F₁(a,b;c;z)`; (ii) the EGF `ΣQⱼzʲ/j!`; (iii) `𝓕(ρ)` in projective-rationality; (iv) the integer `F` in the BDS norm witness |
| `M` | (i) a proposed integer/rational tail value; (ii) `M_ℓ`, the one-period transfer matrix |
| `t` | `μ/λ ∈ (0,1)` with argument `−t` [lerch-arithmetic]; written directly as `x = −t` elsewhere; a *different* `t = c/(1+c)` in projective-rationality |
| `E` | `E_N` (denominator lcm) vs `E[·]` (Euler-moment expectation) vs `eₙ` (finite backward tail) |
| `D` vs `d` | `D = √Δ_c` vs `d = √Δ_B` |

Other traps:

- **`Δ_c` square vs "`D` square".** `D` is a square root; squareness is a property
  of `Δ_c`. Sources say `D ∈ Z` / `D ∉ Q`. Prefer "`Δ_c` square".
- **`a = (μ+q)/D` vs `a = (μ+q₀)/D`** agree only at `k = 1`.
- **The hasse-lerch Frobenius law** rests on `a = x/(x−1)`, valid **only at
  `q = 0`**.
- **hasse-kernel §4.2** re-derives, unattributed, the `k=1, q=−p` case of the
  regular-line machinery.
- **One resonance, three names — on a specific locus.** At `c = p` (i.e. `k = 1`,
  `q = −p`): the Lerch `a = 0`, the affine-trap `r₀ = c(p+c) = 2p²`, and the EGF
  cancellation `F_img = (1−pz)^{−2}` are the same point (`D = 3p`, `μ = p`,
  `Δ_c = (p+2c)²`). Away from `c = p` they are *not* the same: `c = 0` gives
  `r₀ = 0`, outside the positive range, while `2p² > 0`.
- Widespread unescaped `qquad` in at least six documents; equation-tag gaps —
  `(17)` skipped in positive-egf-pullback, `(22)→(27)` in lerch-arithmetic,
  `(33)` overloaded, affine-trap §5 cites `(S3)` for `(S2)`.
- `docs/README.md:33` indexes `reviews/2026-07-21-maths-research-audit.md`, which
  **does not exist**.

---

## §12 Cold start

1. §0 glossary, §1 objects, §2 questions.
2. §4 — the refutations. Highest value per line; read before designing anything.
3. §7 — run the decision procedure on your tuple, in order, Step 0 first.
4. If Step 0 or 1 succeeds, you are done. If Step 2/3 gives a **closed** row, take
   the certificate from §3 and verify it with §6.2. Remember Q1 ≠ Q2, and that
   **Q3 is open on every branch**.
5. If you land in (E) or the `Δ_B`-nonsquare branch, read
   [connection-coordinate](polynomial-tail-connection-coordinate.md) and
   [lerch-arithmetic](polynomial-tail-lerch-arithmetic.md) in full — they define
   the open core — then §8.
6. Re-run §6.2 before trusting any numeric claim; note §6.3.
7. Record what you prove as PROVEN / LEAN / REDUCED / SEMIDECIDED / OBSERVED /
   REFUTED, kept separate, with search bounds on every "none found."

---

## §13 References

**Cited to close or bound a branch**

- Kenison, Klurman, Lefaucheux, Luca, Moree, Ouaknine, Sertöz, Whiteland,
  Worrell, *On the Positivity Problem for Second-Order Holonomic Sequences*
  (2026) — minimality decidability for the degree-one class; source of the
  PCF Equality Problem framing and of the `μ²/α` misprint (§4.1).
  `georgekenison.github.io/uploads/papers/holonomic_positivity26.pdf`
- Kenison et al., MFCS 2021, doi:10.4230/LIPIcs.MFCS.2021.67 — PCF Equality
  Problem interreducible with minimality.
- Sertöz, Ouaknine, Worrell, arXiv:2505.20397 — transcendence and linear
  relations of 1-periods.

**Transcendence / linear independence (all require rational parameters — §5.1)**

- Lai, arXiv:2203.00207
- David, Hirata-Kohno, Kawashima, arXiv:2511.06534
- Bhattacharjee, arXiv:2607.16331

**Structure theory**

- Kimura, solvability of the hypergeometric equation, doi:10.24546/0100498821
- Driver, Jordaan, arXiv:0901.0435 — Padé denominators for `Φ`
- Garoufalidis, arXiv:0708.4354 — G-function denominators
- André, *Annals* 151 (2000), doi:10.2307/121045; survey JTNB 15 (2003),
  numdam `JTNB_2003__15_1_1_0`
- Lepetit, arXiv:2109.10239

**Near misses (see §4.2)**

- Matveeva, arXiv:2511.02121 — global boundedness
- Hagihara, Kawamura, ICALP 2025, doi:10.4230/LIPIcs.ICALP.2025.159 — ultimate
  sign classification
- Elimelech et al., arXiv:2308.11829 — factorial reduction
- Chen, Liu, doi:10.3390/math13152332 — Bauer–Muir transform
- Bowman, McLaughlin, arXiv:1812.08251; Ben David et al., arXiv:2111.04468

**Ostrowski / automata**

- Hieronymi, Terry, arXiv:1407.7000
- Schaeffer, Shallit, Zorcic, arXiv:2402.08331

**The conjecture**

- Bosma, Dekking, Steiner, arXiv:1710.01498 (2018) — posed in the final paragraph
- Fokkink, Joshi, doi:10.1007/s11139-025-01305-1 (2026) — Conjecture 20;
  Theorem 24 proves only a particular golden-mean case
