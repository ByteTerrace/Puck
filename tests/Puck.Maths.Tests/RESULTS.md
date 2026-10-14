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
- last run: 2026-10-14

## Default

- law cases executed: 707
- last run: 2026-10-14

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2522
- waived: 106
- uncovered: 634
- total public members: 3262
- last run: 2026-10-14

## Legs

| leg kind | legs |
| --- | --- |
| classical | 925 |
| in-tree-independent | 34 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 21 |
| shared-substrate:shared-upstream | 23 |
| shared-substrate:transcription | 28 |
| structural | 1250 |
| **total** | **2472** |

- statements: 868
- statements with no independent leg: 224
- last run: 2026-10-14

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10677 |
| binary-field | 256 | 510 |
| binary-field-axioms | 256 | 510 |
| binary-field-group | 256 | 605 |
| binary-polynomial | 256 | 608 |
| binary-polynomial-division | 256 | 608 |
| binary-polynomial-gcd | 256 | 608 |
| clifford-motor | 512 | 427 |
| clifford-multivector | 512 | 427 |
| clifford-planar-complex | 512 | 427 |
| clifford-planar-dual | 512 | 427 |
| clifford-planar-split | 512 | 427 |
| clifford-quaternion-even | 512 | 427 |
| clifford-reverse | 512 | 427 |
| closed-unit | 512 | 661 |
| complex | 512 | 10678 |
| complex-direction | 512 | 555 |
| complex-divide | 512 | 668 |
| complex-rotate | 512 | 668 |
| contribution-fold-analog | 512 | 346 |
| contribution-fold-formula | 512 | 346 |
| contribution-fold-no-pool | 512 | 346 |
| contribution-fold-order | 512 | 346 |
| contribution-fold-quantization | 512 | 346 |
| core-word-modular | 512 | 52 |
| cost-bound-arithmetic | 512 | 93 |
| cost-model-budgets | 512 | 93 |
| cost-model-conversions | 512 | 93 |
| directed-magnitude | 512 | 278 |
| directed-product | 512 | 278 |
| directed-product-sum | 512 | 278 |
| directed-quotient | 512 | 278 |
| directed-root | 512 | 278 |
| dual | 512 | 8445 |
| dual-divide | 512 | 555 |
| dual-generic | 512 | 555 |
| dual-quaternion | 512 | 668 |
| dynamics | 512 | 163 |
| extension-field | 256 | 594 |
| extension-field-inverse | 256 | 503 |
| extension-field-norm | 256 | 594 |
| extension-field-power | 256 | 503 |
| extension-field-product | 256 | 594 |
| fixed-saturate | 512 | 93 |
| integer-bit-align | 256 | 67 |
| integer-bit-morton | 128 | 67 |
| integer-bit-scatter | 128 | 67 |
| integer-bit-smear | 256 | 67 |
| integer-hexagonal-index | 512 | 132 |
| integer-magic-constants | 512 | 124 |
| integer-try-arithmetic | 512 | 57 |
| integer-try-multiplication | 512 | 52 |
| interval-arc-functions | 512 | 37 |
| interval-arctangent | 512 | 37 |
| interval-arithmetic | 512 | 37 |
| interval-circular | 512 | 37 |
| interval-isotonic | 512 | 37 |
| interval-power | 512 | 37 |
| mass-box | 256 | 278 |
| mass-capsule | 256 | 278 |
| mass-compound | 256 | 278 |
| mass-cylinder | 256 | 278 |
| mass-parallel-axis | 256 | 278 |
| mass-sphere | 256 | 278 |
| mass-volume | 256 | 278 |
| meet-associative | 512 | 337 |
| meet-bottom-absorption | 512 | 337 |
| meet-commutative | 512 | 337 |
| meet-idempotent | 512 | 337 |
| meet-monotonicity | 512 | 337 |
| meet-order-coherence | 512 | 337 |
| meet-product-composition | 512 | 337 |
| meet-top-identity | 512 | 337 |
| mixed-scale | 512 | 278 |
| mixed-scale-triple | 512 | 278 |
| mobius | 512 | 8445 |
| monogenic-exact | 512 | 427 |
| monogenic-fusion | 512 | 427 |
| position | 512 | 542 |
| position-delta | 512 | 651 |
| position-translate | 512 | 651 |
| presented | 512 | 949 |
| prime-exploration-primality | 512 | 33 |
| prime-field | 256 | 599 |
| prime-field-chain | 256 | 599 |
| prime-field-lucas | 256 | 599 |
| prime-field-primality | 256 | 599 |
| prime-field-root | 256 | 599 |
| q1648-scalar | 512 | 328 |
| q1648-scalar-division | 512 | 326 |
| q3232-scalar | 512 | 291 |
| q3232-scalar-division | 512 | 290 |
| quaternion | 512 | 668 |
| quaternion-antiparallel | 512 | 42 |
| quaternion-arc | 512 | 42 |
| quaternion-direction | 512 | 555 |
| quaternion-rotate | 512 | 555 |
| quaternion-sublattice | 256 | 555 |
| rate | 512 | 652 |
| rigid | 512 | 691 |
| rigid-direction | 512 | 566 |
| rigid-exp-series | 512 | 40 |
| rigid-log-series | 512 | 40 |
| rigid-point | 512 | 691 |
| scalar | 512 | 8449 |
| scalar-division | 512 | 686 |
| scalar-smoothstep | 512 | 44 |
| scalar-text | 512 | 686 |
| scalar-transcendental | 512 | 688 |
| smoke | 64 | 2175 |
| split | 512 | 10678 |
| split-divide | 512 | 668 |
| split-transform | 512 | 668 |
| square-grid | 512 | 121 |
| sublattice | 256 | 4605 |
| symmetric-apply2 | 512 | 278 |
| symmetric-apply3 | 512 | 278 |
| symmetric-invert2 | 256 | 321 |
| symmetric-invert3 | 256 | 321 |
| symmetric-solve2 | 512 | 321 |
| symmetric-solve3 | 512 | 322 |
| unit-fraction16 | 512 | 575 |
| unit-fraction32 | 512 | 693 |
| unsigned-scalar | 512 | 679 |
| vector | 512 | 663 |
| vector-compare-length | 512 | 38 |
| vector-componentwise-helpers | 512 | 57 |
| vector-direction | 512 | 663 |
| vector-lattice | 512 | 551 |
| vector-narrow | 512 | 663 |
| vector-norm | 512 | 551 |
| vector-orthonormal-basis | 512 | 214 |
| vector-ray-plane | 512 | 55 |
| vector-within | 512 | 43 |

- last run: 2026-10-14
