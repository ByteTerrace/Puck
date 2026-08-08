# Acknowledgments and citations

Puck's emulator cores are validated against public hardware-test corpora and
independently-written reference emulators used strictly as *evidence* (never as
gates — see the `gaming-bricks` skill's oracle discipline). This file carries the
citations for publicly-documented hardware facts that informed the implementation,
so that identifiers, comments, and XML docs in the code stay free of external
company / product / emulator proper nouns.

The same rule governs the mathematics. `Puck.Maths` names its primitives for what
they do, so the people and the theorems behind a construction are credited here
rather than in identifiers. Nothing in this repository claims novelty for the
results cited below.

## Advanced GamingBrick (ARM7TDMI / GBA-class) — evidence-first accuracy wave

- **Direct Sound FIFO ring + playing-buffer model** (the 7-word ring + separate
  32-bit playing buffer, the "two DMA requests need an intervening timer overflow"
  invariant, and overrun auto-reset). Hardware measurement:
  <https://github.com/mgba-emu/mgba/issues/1847> ·
  <http://problemkaputt.de/gbatek-gba-sound-channel-a-and-b-dma-sound.htm>

- **Per-channel DMA read latch** and the DMA start-delay / force-non-sequential /
  latch / IRQ-dispatch-latency / HALTCNT / timer reload-race cycle oracles that
  the `--oracle` probe battery reproduces. Documented hardware-test corpus:
  <https://github.com/nba-emu/hw-test> ·
  <https://problemkaputt.de/gbatek-gba-dma-transfers.htm> ·
  <https://problemkaputt.de/gbatek-gba-timers.htm>

- **Per-game save-type / RTC override database.** The behaviours are public
  documentation; no external emulator's data file was copied. Top Gun: Combat
  Zones triple-decoy anti-piracy lock and the Classic NES / Famicom Mini
  SRAM-bait-but-EEPROM family:
  <https://mgba.io/2014/12/28/classic-nes/> ·
  <https://zork.net/~st/jottings/GBA_saves.html> ·
  <https://tcrf.net/Top_Gun:_Combat_Zones_(Game_Boy_Advance)>

- **BIOS pre-flight hash identification** (refusing cycle-parity work on a
  non-retail BIOS — the documented "phantom cycle drift" trap; mismatched
  ROM/BIOS hash is a top cause of divergence):
  <https://mgba.io/2020/01/25/infinite-loop-holy-grail/> ·
  <https://www.smashladder.com/guides/view/26pv/desync-troubleshooting-guide>

## Reference emulators and test suites (co-simulation oracles / evidence)

Independently-written emulators and community test suites, used as differential
oracles and conformance evidence only:

- mGBA — <https://github.com/mgba-emu/mgba> (and its test suite,
  <https://github.com/mgba-emu/suite>)
- ares — <https://ares-emu.net/>
- jsmolka `gba-tests` — <https://github.com/jsmolka/gba-tests>
- FuzzARM — <https://github.com/DenSinH/FuzzARM>
- AGS aging cartridge test spec (AGSTests) — <https://github.com/DenSinH/AGSTests>
- GBATEK hardware reference — <https://problemkaputt.de/gbatek.htm>

## Puck.Maths: the presented charged algebra

`src/Puck.Maths/Oracle/` carries a presented charged algebra: one
graft-and-normalize product whose instances differ from one another only by a
presentation value and a material type argument. Its public surface is named for
behaviour (`TrySumOverAllLengths`, not "Kleene star"; "charge", not "cocycle"),
so the traditions it draws on are credited here. See
[the `Oracle/` reference](../src/Puck.Maths/Oracle/README.md) for what it does
and refuses.

- **Rational series over a semiring, and weighted automata.** The theorem that a
  finitely describable series is exactly one recognised by a finite weighted
  machine, which specialises to both the classical regular-language result and
  the classical theory of linear recurrences. It is why `Power` at a monogenic
  presentation and `Power` at a quiver presentation are one operation.
  S. C. Kleene, "Representation of events in nerve nets and finite automata",
  *Automata Studies*, Princeton University Press (1956) ·
  M. P. Schützenberger, "On the definition of a family of automata",
  *Information and Control* 4 (1961) 245–270 ·
  S. Eilenberg, *Automata, Languages, and Machines*, Vol. A, Academic Press (1974) ·
  J. Berstel and C. Reutenauer, *Noncommutative Rational Series with
  Applications*, Cambridge University Press (2011) ·
  M. Droste, W. Kuich and H. Vogler (eds.), *Handbook of Weighted Automata*,
  Springer (2009)

- **The algebraic path problem.** One presentation at three materials answering
  reachability, shortest distance and walk counts, with the sum over all lengths
  gated by a computed certificate rather than by an assumption of convergence.
  S. Warshall, "A theorem on Boolean matrices", *Journal of the ACM* 9 (1962)
  11–12 ·
  R. W. Floyd, "Algorithm 97: Shortest path", *Communications of the ACM* 5 (1962)
  345 ·
  R. C. Backhouse and B. A. Carré, "Regular algebra applied to path-finding
  problems", *Journal of the Institute of Mathematics and its Applications* 15
  (1975) 161–186 ·
  D. J. Lehmann, "Algebraic structures for transitive closure", *Theoretical
  Computer Science* 4 (1977) 59–76 ·
  M. Gondran and M. Minoux, *Graphs, Dioids and Semirings*, Springer (2008)

- **Most-likely-path decoding, and the max-times semiring it runs in.** Reading
  the most PROBABLE route through a weighted graph rather than the shortest one,
  which is the algebraic path problem again at another semiring: the maximum
  chooses and the product composes. It is why `MostLikelyPathMaterial` carries no
  kernel of its own and why its answers ride the same guarded sum the tropical
  material does — the two are carried onto each other by the logarithm, which the
  power-of-two law states as a theorem on the subfamily where both sides are
  exact rather than as an analogy.
  A. J. Viterbi, "Error bounds for convolutional codes and an asymptotically
  optimum decoding algorithm", *IEEE Transactions on Information Theory* 13
  (1967) 260–269 ·
  G. D. Forney, "The Viterbi algorithm", *Proceedings of the IEEE* 61 (1973)
  268–278 ·
  M. Mohri, "Semiring frameworks and algorithms for shortest-distance problems",
  *Journal of Automata, Languages and Combinatorics* 7 (2002) 321–350

- **Fuzzy sets, the minimum as conjunction, and the De Morgan complement.** A
  coefficient as a DEGREE of membership rather than a bit, with the maximum for
  union, the minimum for intersection, and the involution `1 − x` exchanging
  them. It is what makes `FuzzyMaterial` the second material here to carry a
  complement at all, and the pattern lens's complement graded rather than
  two-valued; the admission condition — a semiring with a De Morgan complement
  and a top element satisfies `1 + 1 = 1` — is met because the maximum is
  idempotent. That the minimum is the only idempotent conjunction of this kind is
  owed to the triangular-norm literature rather than to the set theory.
  L. A. Zadeh, "Fuzzy sets", *Information and Control* 8 (1965) 338–353 ·
  K. Gödel, "Zum intuitionistischen Aussagenkalkül", *Anzeiger der Akademie der
  Wissenschaften in Wien* 69 (1932) 65–66 ·
  E. P. Klement, R. Mesiar and E. Pap, *Triangular Norms*, Kluwer (2000)

- **The bounded sum, and the many-valued logic it comes from.** The conjunction
  `max(0, a + b − 1)`, the strictest of the continuous triangular norms: a route
  survives only while its steps' shortfalls from certainty still sum to under
  one, so a long chain is cut off outright where the minimum would merely narrow
  it. It is `BoundedSumMaterial`'s product, exact in raw arithmetic, and its
  associativity — every nesting of three factors is `max(0, a + b + c − 2)` — is
  why the three-factor fused term there needs no wide accumulator.
  J. Łukasiewicz, "O logice trójwartościowej", *Ruch Filozoficzny* 5 (1920)
  170–171 ·
  J. Łukasiewicz and A. Tarski, "Untersuchungen über den Aussagenkalkül",
  *Comptes Rendus des Séances de la Société des Sciences et des Lettres de
  Varsovie* 23 (1930) 30–50 ·
  P. Hájek, *Metamathematics of Fuzzy Logic*, Kluwer (1998)

- **Incidence algebras and Möbius inversion.** Convolution over the
  factorizations of an arrow, which is what makes the matrix product, the
  Dirichlet convolution and the geometric product one loop; and the inverse of
  the zeta element, which the phase-2 divisibility window rests on.
  G.-C. Rota, "On the foundations of combinatorial theory I: Theory of Möbius
  functions", *Zeitschrift für Wahrscheinlichkeitstheorie und verwandte Gebiete*
  2 (1964) 340–368 ·
  P. Doubilet, G.-C. Rota and R. Stanley, "On the foundations of combinatorial
  theory VI: The idea of generating function", *Proceedings of the Sixth Berkeley
  Symposium on Mathematical Statistics and Probability* (1972) ·
  R. P. Stanley, *Enumerative Combinatorics*, Vol. 1, ch. 3, Cambridge University
  Press (1997)

- **The Euler characteristic as Möbius mass, and the chain-counting identity
  behind it.** Why `ExteriorCalculus.TryEulerCharacteristic` counts nothing by
  dimension: the Möbius value of the single interval spanning a bounded face
  order is the alternating count of the chains through it, so it already IS the
  reduced Euler characteristic, and the alternating cell count is an independent
  statement rather than the definition. The same reading is why the Dirichlet
  window is this order's *reduced* incidence algebra — the quotient by the
  interval type — and not a specialization of `Presentations.IntervalPoset`.
  L. Euler, "Elementa doctrinae solidorum", *Novi Commentarii Academiae
  Scientiarum Petropolitanae* 4 (1758) 109–140 ·
  P. Hall, "A contribution to the theory of groups of prime-power order",
  *Proceedings of the London Mathematical Society* 36 (1934) 29–95 ·
  G.-C. Rota, "On the foundations of combinatorial theory I", as above, §§3–5

- **Simplicial chains, cochains, and the discrete Stokes identity.** The oriented
  incidence numbers whose alternating-sign rule makes the boundary of a boundary
  vanish, and the adjunction `⟨dω, c⟩ = ⟨ω, ∂c⟩` that defines the coboundary from
  the boundary — which in an incidence algebra is not a theorem about two
  operators but the associativity of one product, since a cochain multiplies the
  incidence element on the right and a chain multiplies it on the left.
  H. Poincaré, "Analysis situs", *Journal de l'École Polytechnique* 1 (1895)
  1–121 ·
  J. W. Alexander, "A proof of the invariance of certain constants of analysis
  situs", *Transactions of the American Mathematical Society* 16 (1915) 148–154 ·
  G. G. Stokes, Smith's Prize examination paper, Cambridge (1854) ·
  A. Bossavit, *Computational Electromagnetism*, Academic Press (1998), ch. 5

- **The elementary-divisor theorem, and the coefficient growth that makes a
  bounded attempt necessary.** Every integer matrix admits unimodular `U` and `V`
  with `U·A·V` diagonal and each diagonal entry dividing the next, which is what
  `SmithNormalForm` computes and — because the triple is its own certificate —
  re-multiplies before returning. It is declared a *second kernel* precisely
  because it is not a convolution: it searches for a pivot and divides with
  remainder, so no presentation computes it. That the product of the first `k`
  divisors is the greatest common divisor of all `k`-by-`k` minors is what lets
  the law suite check the answer against an oracle that runs none of the
  reduction's steps. The pivot rule and the magnitude ceiling are owed to the
  algorithmic literature rather than to the theorem: intermediate entries genuinely
  explode, so taking the smallest remaining entry as the pivot is a containment
  measure and not an optimization, and a bound is the only honest way to promise a
  finite attempt.
  H. J. S. Smith, "On systems of linear indeterminate equations and congruences",
  *Philosophical Transactions of the Royal Society of London* 151 (1861) 293–326 ·
  R. Kannan and A. Bachem, "Polynomial algorithms for computing the Smith and
  Hermite normal forms of an integer matrix", *SIAM Journal on Computing* 8 (1979)
  499–507 ·
  G. Havas, B. S. Majewski and K. R. Matthews, "Extended GCD and Hermite normal
  form algorithms via lattice basis reduction", *Experimental Mathematics* 7 (1998)
  125–136

- **Torsion coefficients, Betti numbers, and universal coefficients.** Why
  `IntegerHomology` needs no homology-specific arithmetic at all: a unimodular
  change of basis moves neither the kernel nor the image of a boundary operator, so
  the elementary divisors above one ARE the orders of the cyclic torsion summands
  and the free rank is a difference of three counts. The same reading explains why
  `FieldHomology` — which reads its ranks from the echelon the duality layer
  already carries — sees strictly less: over a field the homology is a vector space
  and torsion has nowhere to live, so the two answers separate exactly at the
  characteristics dividing a torsion coefficient. The minimal six-vertex
  triangulation of the real projective plane is the smallest complex where that
  separation can be measured at all.
  E. Betti, "Sopra gli spazi di un numero qualunque di dimensioni", *Annali di
  Matematica Pura ed Applicata* 4 (1871) 140–158 ·
  H. Poincaré, "Complément à l'analysis situs", *Rendiconti del Circolo Matematico
  di Palermo* 13 (1899) 285–343 ·
  S. Eilenberg and S. Mac Lane, "Group extensions and homology", *Annals of
  Mathematics* 43 (1942) 757–831 ·
  J. R. Munkres, *Elements of Algebraic Topology*, Addison-Wesley (1984), §§11 and 51

- **Rewriting, normal forms, and bounded law verification.** Normal-form discovery,
  deterministic declaration-priority reduction, and the bounded finite-basis product
  checks behind `Certify`. The certificate deliberately does not claim confluence of
  the declared rewrite relation: `BasisAssociativityVerified` means the induced
  compiled product associated on every basis triple. The word problem for a general
  presentation remains undecidable, which is why normalization is bounded;
  certification has its own explicit computational budget, keeping "the budget ran
  out" distinct from either proof or counterexample.
  M. H. A. Newman, "On theories with a combinatorial definition of
  'equivalence'", *Annals of Mathematics* 43 (1942) 223–243 ·
  A. A. Markov, "On the impossibility of certain algorithms in the theory of
  associative systems", *Doklady Akademii Nauk SSSR* 55 (1947) 583–586 ·
  E. L. Post, "Recursive unsolvability of a problem of Thue", *Journal of Symbolic
  Logic* 12 (1947) 1–11 ·
  A. I. Shirshov, "Some algorithmic problems for Lie algebras", *Sibirskii
  Matematicheskii Zhurnal* 3 (1962) 292–296 ·
  D. E. Knuth and P. B. Bendix, "Simple word problems in universal algebras",
  *Computational Problems in Abstract Algebra*, Pergamon (1970) 263–297 ·
  G. M. Bergman, "The diamond lemma for ring theory", *Advances in Mathematics*
  29 (1978) 178–218

- **Reflection groups, their bond matrices, and the automatic structure the
  naive rules lack.** A group presented by involutions and one braid relation per
  pair of them, which is what `Presentations.Coxeter` builds, and the finite
  reflection group of a root system, which is what `ReflectionSystem` reads off
  the lattice's own action. The classification is why a chain of bonds of three
  has the symmetric group on one more letter than its rank, and the automatic
  structure is why those two rules alone do *not* decide the word problem past
  rank two: the completion this library does not do is what a shortlex-automatic
  structure supplies.
  H. S. M. Coxeter, "Discrete groups generated by reflections", *Annals of
  Mathematics* 35 (1934) 588–621 ·
  J. Tits, "Le problème des mots dans les groupes de Coxeter", *Symposia
  Mathematica* 1 (1969) 175–185 ·
  B. Brink and R. B. Howlett, "A finiteness property and an automatic structure
  for Coxeter groups", *Mathematische Annalen* 296 (1993) 179–190 ·
  N. Bourbaki, *Groupes et algèbres de Lie*, chapters IV–VI, Hermann (1968)

- **Monoidal coherence: the pentagon, and 3-cocycles as coherence data.** Why
  `PresentationCertificate.IsCoherent` is a five-vertex identity on the declared
  charges rather than a convention: an associator is coherent exactly when the two
  routes that rebalance a product of four factors charge the same thing, and the
  coherence theorem is what makes "the charge a bracketing collects" a property of
  the bracketing instead of a property of the walk that removed its brackets. The
  cochain itself is the group-cohomology object, `H³` with values in the units,
  which is why a Cayley-Dickson floor's associator, being the coboundary of its
  own 2-cochain, is coherent at every floor including the sedenions, and why a
  nontrivial cocycle can be declared over a group algebra whose product already
  associates.
  S. Mac Lane, "Natural associativity and commutativity", *Rice University
  Studies* 49 (1963) 28–46 ·
  S. Eilenberg and S. Mac Lane, "Cohomology theory of abelian groups and
  homotopy theory", *Proceedings of the National Academy of Sciences* 36 (1950)
  443–447 ·
  A. Joyal and R. Street, "Braided tensor categories", *Advances in Mathematics*
  102 (1993) 20–78

- **Twisted group algebras, and the octonions as a quasialgebra.** The reason the
  octonion floor is an ordinary instance of the kernel rather than a special
  case: a nonassociative algebra can be a monoid object in a monoidal category
  whose associator is a nontrivial 3-cochain, so law failure relocates into
  coherence data the object computes and returns. The same reading makes a
  Clifford algebra a twisted group algebra of the group `(Z/2)^n`, which is
  exactly what `Presentations.Clifford` builds.
  L. E. Dickson, "On quaternions and their generalization and the history of the
  eight square theorem", *Annals of Mathematics* 20 (1919) 155–171 ·
  H. Albuquerque and S. Majid, "Quasialgebra structure of the octonions",
  *Journal of Algebra* 220 (1999) 188–224 ·
  H. Albuquerque and S. Majid, "Clifford algebras obtained by twisting of group
  algebras", *Journal of Pure and Applied Algebra* 171 (2002) 133–148 ·
  J. C. Baez, "The octonions", *Bulletin of the American Mathematical Society* 39
  (2002) 145–205

- **Derivative-based matching, and twisted derivations.** The residual operator
  with a twisted Leibniz rule, which is what makes a machine *derived* from the
  algebra rather than parallel to it: the counit twist is derivative-based
  pattern matching, the identity twist is an ordinary derivation (the
  forward-mode sensitivity `FixedDual` already carries), and the shift twist is
  the skew step behind holonomic recurrences. Phase-2 work, credited here because
  the design rests on it.
  O. Ore, "Theory of non-commutative polynomials", *Annals of Mathematics* 34
  (1933) 480–508 ·
  J. A. Brzozowski, "Derivatives of regular expressions", *Journal of the ACM* 11
  (1964) 481–494 ·
  V. M. Antimirov, "Partial derivatives of regular expressions and finite
  automaton constructions", *Theoretical Computer Science* 155 (1996) 291–319 ·
  S. Owens, J. Reppy and A. Turon, "Regular-expression derivatives re-examined",
  *Journal of Functional Programming* 19 (2009) 173–190

- **Minimization and equivalence by the pairing radical.** The duality reading of
  a weighted machine — reachable subspace, observation span, and the quotient by
  the part every readout-after-a-word annihilates — which is what makes
  `MinimizeByPairingRadical` and `AreEquivalent` linear algebra over the material
  rather than an enumeration of words, and what makes the bound the enumeration
  oracle checks against a Myhill bound.
  J. Myhill, "Finite automata and the representation of events", *WADD Technical
  Report* 57-624 (1957) ·
  M. P. Schützenberger, "On the definition of a family of automata",
  *Information and Control* 4 (1961) 245–270 ·
  J. W. Carlyle and A. Paz, "Realizations by stochastic finite automata",
  *Journal of Computer and System Sciences* 5 (1971) 26–40 ·
  M. Fliess, "Matrices de Hankel", *Journal de Mathématiques Pures et Appliquées*
  53 (1974) 197–222

- **The non-metric complement and the regressive product.** The meet as a
  De Morgan dual of the join through a complement that reads only the ambient
  grading — no metric, no signature, no hand-authored sign table — which is why
  the top-grade coefficient of a triple join is exactly a determinant and why the
  orientation predicate needs no geometry.
  H. Grassmann, *Die lineale Ausdehnungslehre*, Otto Wigand (1844) ·
  G. Peano, *Calcolo geometrico secondo l'Ausdehnungslehre di H. Grassmann*,
  Bocca (1888) ·
  M. Barnabei, A. Brini and G.-C. Rota, "On the exterior calculus of invariant
  theory", *Journal of Algebra* 96 (1985) 120–160

- **Möbius inversion in the classical arithmetic setting.** μ as the convolution
  inverse of ζ, the Mertens partial sum, and inclusion-and-exclusion prime
  counting stated as one product and one pairing rather than as a sieve loop.
  A. F. Möbius, "Über eine besondere Art von Umkehrung der Reihen", *Journal für
  die reine und angewandte Mathematik* 9 (1832) 105–123 ·
  A.-M. Legendre, *Théorie des nombres*, 3rd ed., Firmin Didot (1830) ·
  F. Mertens, "Über einige asymptotische Gesetze der Zahlentheorie", *Journal für
  die reine und angewandte Mathematik* 77 (1874) 289–338 ·
  G. H. Hardy and E. M. Wright, *An Introduction to the Theory of Numbers*,
  6th ed., Oxford University Press (2008)

- **The finite calculus.** Summation as the inverse of differencing on
  bounded-degree sequences, which is what makes the antidifference the guarded
  star of the shift and the closed forms for `Σ k^m` fall out of one star rather
  than out of a table.
  I. Newton, *Methodus differentialis* (1711) ·
  G. Boole, *A Treatise on the Calculus of Finite Differences*, Macmillan (1860) ·
  C. Jordan, *Calculus of Finite Differences*, Chelsea (1950) ·
  R. L. Graham, D. E. Knuth and O. Patashnik, *Concrete Mathematics*, 2nd ed.,
  Addison-Wesley (1994), ch. 2

- **The fundamental matrix of an absorbing chain.** `(I − Q)⁻¹` as the exact
  expected visit counts, which is the reading that turns a refused iterative star
  into one solve under the `FieldResolvent` certificate and makes the proof a
  re-multiplication rather than a truncation.
  J. G. Kemeny and J. L. Snell, *Finite Markov Chains*, Van Nostrand (1960)

- **Alphabet refinement and symbolic automata.** The minterm partition of a
  predicate algebra as the generator set the kernel consumes, which is what lets
  an infinite label space be served without the kernel ever learning what a
  predicate is — the declared second axis, **O2**.
  G. van Noord and D. Gerdemann, "Finite state transducers with predicates and
  identities", *Grammars* 4 (2001) 263–286 ·
  M. Veanes, P. de Halleux and N. Tillmann, "Rex: Symbolic regular expression
  explorer", *ICST* (2010) 498–507 ·
  L. D'Antoni and M. Veanes, "The power of symbolic automata and transducers",
  *CAV* (2017) 47–67

- **Diagram algebras: planar matchings as a basis, and composition as arc
  tracing.** Why `Presentations.PlanarTangle` needs no canonicalization step and no
  confluence argument: a planar diagram on a bounded boundary IS a non-crossing
  perfect matching, planarity makes that matching unique rather than one
  representative of an isotopy class, and gluing two of them and following the arcs
  associates by construction, so the composition table proves itself. The loop that
  falls out of a gluing carries a charge exactly as a generator square does, and the
  three relations that tradition states as axioms (a cup-cap idempotent up to the
  loop charge, an adjacent triple collapsing, distant pairs commuting) are here
  computed on the derived product and asserted rather than declared.
  H. N. V. Temperley and E. H. Lieb, "Relations between the 'percolation' and
  'colouring' problem and other graph-theoretical problems associated with regular
  planar lattices", *Proceedings of the Royal Society A* 322 (1971) 251–280 ·
  V. F. R. Jones, "Index for subfactors", *Inventiones Mathematicae* 72 (1983)
  1–25 ·
  L. H. Kauffman, "State models and the Jones polynomial", *Topology* 26 (1987)
  395–407 ·
  F. M. Goodman, P. de la Harpe and V. F. R. Jones, *Coxeter Graphs and Towers of
  Algebras*, Springer (1989)

- **Braided monoidal categories, and the two hexagons.** Why
  `PresentationCertificate.IsBraided` is two identities on charges the certificate
  DERIVED rather than a flag a caller sets: a braiding is a 2-cochain on ordered
  pairs, the hexagons are its compatibility with the product in each argument
  separately, and symmetry is the extra condition that a charge is its own mirror.
  Reading the charge off the compiled cells rather than taking a declaration is why
  the reported braiding is the product's own, and why an instance that is braided
  and NOT symmetric has to be built deliberately: every shipped braiding here is a
  sign, and a sign is its own mirror.
  A. Joyal and R. Street, "Braided tensor categories", *Advances in Mathematics*
  102 (1993) 20–78 ·
  V. G. Drinfeld, "Quantum groups", *Proceedings of the International Congress of
  Mathematicians*, Berkeley (1986) 798–820 ·
  C. Kassel, *Quantum Groups*, Springer (1995), chapters XIII–XIV

- **Braid groups, plat closures, and the state-sum invariant of a diagram.** The
  lineage behind the phase's last clause, which ships as an instance of shipped
  members rather than as code of its own. A braid word's crossings smooth two ways;
  the sum over all smoothings, weighted by a crossing charge and by the loop charge
  each state's closed curves collect, is an invariant of the DIAGRAM under the
  second and third moves and scales by a fixed factor under the first, which is
  exactly why this library evaluates a given diagram and refuses to decide
  equivalence. The braid group's own place in that story is the reason its finite
  basis is a refusal here: it is infinite, so no finite normal-form set exists.
  E. Artin, "Theorie der Zöpfe", *Abhandlungen aus dem Mathematischen Seminar der
  Universität Hamburg* 4 (1925) 47–72 ·
  K. Reidemeister, "Elementare Begründung der Knotentheorie", *Abhandlungen aus
  dem Mathematischen Seminar der Universität Hamburg* 5 (1927) 24–32 ·
  A. A. Markov, "Über die freie Äquivalenz der geschlossenen Zöpfe", *Recueil
  Mathématique de Moscou* 1 (1936) 73–78 ·
  V. F. R. Jones, "A polynomial invariant for knots via von Neumann algebras",
  *Bulletin of the American Mathematical Society* 12 (1985) 103–111 ·
  L. H. Kauffman, "An invariant of regular isotopy", *Transactions of the American
  Mathematical Society* 318 (1990) 417–471 ·
  J. S. Birman, *Braids, Links, and Mapping Class Groups*, Princeton University
  Press (1974)

- **Shuffle and quasi-shuffle products.** The second product of the mandate, and
  the reason `Presentations.Shuffle` is one entry with two behaviours rather than
  two features: interleaving two words is the shuffle, and letting two heads merge
  through a letter product adds the collision term that makes it the quasi-shuffle,
  with an empty letter product recovering the first exactly. The identity the
  collision term satisfies is the one that makes a product of two iterated sums a
  merged sum over the union of their index sets, which is why this library checks
  it against the antidifference of a completely different presentation.
  R. Ree, "Lie elements and an algebra associated with shuffles", *Annals of
  Mathematics* 68 (1958) 210–220 ·
  K. T. Chen, R. H. Fox and R. C. Lyndon, "Free differential calculus, IV: the
  quotient groups of the lower central series", *Annals of Mathematics* 68 (1958)
  81–95 ·
  M. E. Hoffman, "Quasi-shuffle products", *Journal of Algebraic Combinatorics* 11
  (2000) 49–68 ·
  C. Reutenauer, *Free Lie Algebras*, Oxford University Press (1993)

- **The characteristic polynomial from power sums, and the zeta of a graph.** Why
  `GraphZeta` adds no arithmetic: the coefficients of `det(I − tA)` are recovered
  from the traces of the element's powers by a recursion that divides only by the
  loop index, which is why the licence here is a field material and why a
  characteristic at or below the order blocks at exactly that index. Reading the
  reciprocal of that polynomial as a generating function over closed walks is the
  graph-zeta tradition, and it is what makes the dynamical zeta the guarded star of
  one subtraction from the unit rather than a second construction.
  A. Girard, *Invention nouvelle en l'algèbre*, Amsterdam (1629) ·
  U. J. J. Le Verrier, "Sur les variations séculaires des éléments des orbites",
  *Journal de Mathématiques Pures et Appliquées* 5 (1840) 220–254 ·
  D. K. Faddeev and I. S. Sominskii, *Sbornik zadach po vysshei algebre*,
  Gostekhizdat (1949) ·
  Y. Ihara, "On discrete subgroups of the two by two projective linear group over
  p-adic fields", *Journal of the Mathematical Society of Japan* 18 (1966)
  219–235 ·
  H. Bass, "The Ihara-Selberg zeta function of a tree lattice", *International
  Journal of Mathematics* 3 (1992) 717–797
