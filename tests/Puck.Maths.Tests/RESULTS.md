# Puck.Maths.Tests — RESULTS

Machine-written by the assembly ledger at run end. Each block records the last run that exercised it — a tier
block only when that tier ran law cases, coverage only when the ratchet gate ran, the frontier only when a run
consumed domains AND every law it ran passed — so every other block keeps the text its own last run left. The
last-run dates are the only volatile content; they do not by themselves trigger a rewrite.

Every figure below is MACHINE-INDEPENDENT by construction: the same commit produces the same counts and the same
frontier indices on every machine, so a difference here is a real difference and never a difference of hardware.
No duration is recorded, deliberately. One here would carry no machine identity, would span the whole session
rather than the block it sits under, and would be taken without a busy-machine guard — so it could not answer
any question asked of it. Cost is measured outside the suite, by `puck bench`.

## Invocations

| tier | command |
| --- | --- |
| Default (Smoke+Default) | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release` |
| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --filter-trait tier=Smoke` |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --explicit on --filter-trait tier=Deep` |
| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --explicit on --filter-trait tier=Exhaustive` |

## Smoke

- law cases executed: 22
- last run: 2026-10-12

## Default

- law cases executed: 701
- last run: 2026-10-12

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2499
- waived: 106
- uncovered: 634
- total public members: 3239
- last run: 2026-10-12

## Legs

| leg kind | legs |
| --- | --- |
| classical | 914 |
| in-tree-independent | 34 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 21 |
| shared-substrate:shared-upstream | 23 |
| shared-substrate:transcription | 28 |
| structural | 1238 |
| **total** | **2449** |

- statements: 859
- statements with no independent leg: 224
- last run: 2026-10-12

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10673 |
| binary-field | 256 | 506 |
| binary-field-axioms | 256 | 506 |
| binary-field-group | 256 | 601 |
| binary-polynomial | 256 | 604 |
| binary-polynomial-division | 256 | 604 |
| binary-polynomial-gcd | 256 | 604 |
| clifford-motor | 512 | 423 |
| clifford-multivector | 512 | 423 |
| clifford-planar-complex | 512 | 423 |
| clifford-planar-dual | 512 | 423 |
| clifford-planar-split | 512 | 423 |
| clifford-quaternion-even | 512 | 423 |
| clifford-reverse | 512 | 423 |
| closed-unit | 512 | 657 |
| complex | 512 | 10674 |
| complex-direction | 512 | 551 |
| complex-divide | 512 | 664 |
| complex-rotate | 512 | 664 |
| contribution-fold-analog | 512 | 342 |
| contribution-fold-formula | 512 | 342 |
| contribution-fold-no-pool | 512 | 342 |
| contribution-fold-order | 512 | 342 |
| contribution-fold-quantization | 512 | 342 |
| core-word-modular | 512 | 48 |
| cost-bound-arithmetic | 512 | 89 |
| cost-model-budgets | 512 | 89 |
| cost-model-conversions | 512 | 89 |
| directed-magnitude | 512 | 274 |
| directed-product | 512 | 274 |
| directed-product-sum | 512 | 274 |
| directed-quotient | 512 | 274 |
| directed-root | 512 | 274 |
| dual | 512 | 8441 |
| dual-divide | 512 | 551 |
| dual-generic | 512 | 551 |
| dual-quaternion | 512 | 664 |
| dynamics | 512 | 159 |
| extension-field | 256 | 590 |
| extension-field-inverse | 256 | 499 |
| extension-field-norm | 256 | 590 |
| extension-field-power | 256 | 499 |
| extension-field-product | 256 | 590 |
| fixed-saturate | 512 | 89 |
| integer-bit-align | 256 | 63 |
| integer-bit-morton | 128 | 63 |
| integer-bit-scatter | 128 | 63 |
| integer-bit-smear | 256 | 63 |
| integer-hexagonal-index | 512 | 128 |
| integer-magic-constants | 512 | 120 |
| integer-try-arithmetic | 512 | 53 |
| integer-try-multiplication | 512 | 48 |
| interval-arc-functions | 512 | 33 |
| interval-arctangent | 512 | 33 |
| interval-arithmetic | 512 | 33 |
| interval-circular | 512 | 33 |
| interval-isotonic | 512 | 33 |
| interval-power | 512 | 33 |
| mass-box | 256 | 274 |
| mass-capsule | 256 | 274 |
| mass-compound | 256 | 274 |
| mass-cylinder | 256 | 274 |
| mass-parallel-axis | 256 | 274 |
| mass-sphere | 256 | 274 |
| mass-volume | 256 | 274 |
| meet-associative | 512 | 333 |
| meet-bottom-absorption | 512 | 333 |
| meet-commutative | 512 | 333 |
| meet-idempotent | 512 | 333 |
| meet-monotonicity | 512 | 333 |
| meet-order-coherence | 512 | 333 |
| meet-product-composition | 512 | 333 |
| meet-top-identity | 512 | 333 |
| mixed-scale | 512 | 274 |
| mixed-scale-triple | 512 | 274 |
| mobius | 512 | 8441 |
| monogenic-exact | 512 | 423 |
| monogenic-fusion | 512 | 423 |
| position | 512 | 538 |
| position-delta | 512 | 647 |
| position-translate | 512 | 647 |
| presented | 512 | 945 |
| prime-exploration-primality | 512 | 29 |
| prime-field | 256 | 595 |
| prime-field-chain | 256 | 595 |
| prime-field-lucas | 256 | 595 |
| prime-field-primality | 256 | 595 |
| prime-field-root | 256 | 595 |
| q1648-scalar | 512 | 324 |
| q1648-scalar-division | 512 | 322 |
| q3232-scalar | 512 | 287 |
| q3232-scalar-division | 512 | 286 |
| quaternion | 512 | 664 |
| quaternion-antiparallel | 512 | 38 |
| quaternion-arc | 512 | 38 |
| quaternion-direction | 512 | 551 |
| quaternion-rotate | 512 | 551 |
| quaternion-sublattice | 256 | 551 |
| rate | 512 | 648 |
| rigid | 512 | 687 |
| rigid-direction | 512 | 562 |
| rigid-exp-series | 512 | 36 |
| rigid-log-series | 512 | 36 |
| rigid-point | 512 | 687 |
| scalar | 512 | 8445 |
| scalar-division | 512 | 682 |
| scalar-smoothstep | 512 | 40 |
| scalar-text | 512 | 682 |
| scalar-transcendental | 512 | 684 |
| smoke | 64 | 2171 |
| split | 512 | 10674 |
| split-divide | 512 | 664 |
| split-transform | 512 | 664 |
| square-grid | 512 | 117 |
| sublattice | 256 | 4601 |
| symmetric-apply2 | 512 | 274 |
| symmetric-apply3 | 512 | 274 |
| symmetric-invert2 | 256 | 317 |
| symmetric-invert3 | 256 | 317 |
| symmetric-solve2 | 512 | 317 |
| symmetric-solve3 | 512 | 318 |
| unit-fraction16 | 512 | 571 |
| unit-fraction32 | 512 | 689 |
| unsigned-scalar | 512 | 675 |
| vector | 512 | 659 |
| vector-compare-length | 512 | 34 |
| vector-componentwise-helpers | 512 | 53 |
| vector-direction | 512 | 659 |
| vector-lattice | 512 | 547 |
| vector-narrow | 512 | 659 |
| vector-norm | 512 | 547 |
| vector-orthonormal-basis | 512 | 210 |
| vector-ray-plane | 512 | 51 |
| vector-within | 512 | 39 |

- last run: 2026-10-12
