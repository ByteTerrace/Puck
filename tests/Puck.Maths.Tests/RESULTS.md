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
- last run: 2026-10-09

## Default

- law cases executed: 696
- last run: 2026-10-09

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2478
- waived: 103
- uncovered: 634
- total public members: 3215
- last run: 2026-10-09

## Legs

| leg kind | legs |
| --- | --- |
| classical | 911 |
| in-tree-independent | 34 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 21 |
| shared-substrate:shared-upstream | 23 |
| shared-substrate:transcription | 28 |
| structural | 1235 |
| **total** | **2443** |

- statements: 854
- statements with no independent leg: 222
- last run: 2026-10-09

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10670 |
| binary-field | 256 | 503 |
| binary-field-axioms | 256 | 503 |
| binary-field-group | 256 | 598 |
| binary-polynomial | 256 | 601 |
| binary-polynomial-division | 256 | 601 |
| binary-polynomial-gcd | 256 | 601 |
| clifford-motor | 512 | 420 |
| clifford-multivector | 512 | 420 |
| clifford-planar-complex | 512 | 420 |
| clifford-planar-dual | 512 | 420 |
| clifford-planar-split | 512 | 420 |
| clifford-quaternion-even | 512 | 420 |
| clifford-reverse | 512 | 420 |
| closed-unit | 512 | 654 |
| complex | 512 | 10671 |
| complex-direction | 512 | 548 |
| complex-divide | 512 | 661 |
| complex-rotate | 512 | 661 |
| contribution-fold-analog | 512 | 339 |
| contribution-fold-formula | 512 | 339 |
| contribution-fold-no-pool | 512 | 339 |
| contribution-fold-order | 512 | 339 |
| contribution-fold-quantization | 512 | 339 |
| core-word-modular | 512 | 45 |
| cost-bound-arithmetic | 512 | 86 |
| cost-model-budgets | 512 | 86 |
| cost-model-conversions | 512 | 86 |
| directed-magnitude | 512 | 271 |
| directed-product | 512 | 271 |
| directed-product-sum | 512 | 271 |
| directed-quotient | 512 | 271 |
| directed-root | 512 | 271 |
| dual | 512 | 8438 |
| dual-divide | 512 | 548 |
| dual-generic | 512 | 548 |
| dual-quaternion | 512 | 661 |
| dynamics | 512 | 156 |
| extension-field | 256 | 587 |
| extension-field-inverse | 256 | 496 |
| extension-field-norm | 256 | 587 |
| extension-field-power | 256 | 496 |
| extension-field-product | 256 | 587 |
| fixed-saturate | 512 | 86 |
| integer-bit-align | 256 | 60 |
| integer-bit-morton | 128 | 60 |
| integer-bit-scatter | 128 | 60 |
| integer-bit-smear | 256 | 60 |
| integer-hexagonal-index | 512 | 125 |
| integer-magic-constants | 512 | 117 |
| integer-try-arithmetic | 512 | 50 |
| integer-try-multiplication | 512 | 45 |
| interval-arc-functions | 512 | 30 |
| interval-arctangent | 512 | 30 |
| interval-arithmetic | 512 | 30 |
| interval-circular | 512 | 30 |
| interval-isotonic | 512 | 30 |
| interval-power | 512 | 30 |
| mass-box | 256 | 271 |
| mass-capsule | 256 | 271 |
| mass-compound | 256 | 271 |
| mass-cylinder | 256 | 271 |
| mass-parallel-axis | 256 | 271 |
| mass-sphere | 256 | 271 |
| mass-volume | 256 | 271 |
| meet-associative | 512 | 330 |
| meet-bottom-absorption | 512 | 330 |
| meet-commutative | 512 | 330 |
| meet-idempotent | 512 | 330 |
| meet-monotonicity | 512 | 330 |
| meet-order-coherence | 512 | 330 |
| meet-product-composition | 512 | 330 |
| meet-top-identity | 512 | 330 |
| mixed-scale | 512 | 271 |
| mixed-scale-triple | 512 | 271 |
| mobius | 512 | 8438 |
| monogenic-exact | 512 | 420 |
| monogenic-fusion | 512 | 420 |
| position | 512 | 535 |
| position-delta | 512 | 644 |
| position-translate | 512 | 644 |
| presented | 512 | 942 |
| prime-exploration-primality | 512 | 26 |
| prime-field | 256 | 592 |
| prime-field-chain | 256 | 592 |
| prime-field-lucas | 256 | 592 |
| prime-field-primality | 256 | 592 |
| prime-field-root | 256 | 592 |
| q1648-scalar | 512 | 321 |
| q1648-scalar-division | 512 | 319 |
| q3232-scalar | 512 | 284 |
| q3232-scalar-division | 512 | 283 |
| quaternion | 512 | 661 |
| quaternion-antiparallel | 512 | 35 |
| quaternion-arc | 512 | 35 |
| quaternion-direction | 512 | 548 |
| quaternion-rotate | 512 | 548 |
| quaternion-sublattice | 256 | 548 |
| rate | 512 | 645 |
| rigid | 512 | 684 |
| rigid-direction | 512 | 559 |
| rigid-exp-series | 512 | 33 |
| rigid-log-series | 512 | 33 |
| rigid-point | 512 | 684 |
| scalar | 512 | 8442 |
| scalar-division | 512 | 679 |
| scalar-smoothstep | 512 | 37 |
| scalar-text | 512 | 679 |
| scalar-transcendental | 512 | 681 |
| smoke | 64 | 2168 |
| split | 512 | 10671 |
| split-divide | 512 | 661 |
| split-transform | 512 | 661 |
| square-grid | 512 | 114 |
| sublattice | 256 | 4598 |
| symmetric-apply2 | 512 | 271 |
| symmetric-apply3 | 512 | 271 |
| symmetric-invert2 | 256 | 314 |
| symmetric-invert3 | 256 | 314 |
| symmetric-solve2 | 512 | 314 |
| symmetric-solve3 | 512 | 315 |
| unit-fraction16 | 512 | 568 |
| unit-fraction32 | 512 | 686 |
| unsigned-scalar | 512 | 672 |
| vector | 512 | 656 |
| vector-compare-length | 512 | 31 |
| vector-componentwise-helpers | 512 | 50 |
| vector-direction | 512 | 656 |
| vector-lattice | 512 | 544 |
| vector-narrow | 512 | 656 |
| vector-norm | 512 | 544 |
| vector-orthonormal-basis | 512 | 207 |
| vector-ray-plane | 512 | 48 |
| vector-within | 512 | 36 |

- last run: 2026-10-09
