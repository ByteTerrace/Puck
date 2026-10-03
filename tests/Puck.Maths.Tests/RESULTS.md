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
- last run: 2026-10-03

## Default

- law cases executed: 652
- last run: 2026-10-03

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2189
- waived: 55
- uncovered: 634
- total public members: 2878
- last run: 2026-10-03

## Legs

| leg kind | legs |
| --- | --- |
| classical | 854 |
| in-tree-independent | 33 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1204 |
| **total** | **2351** |

- statements: 793
- statements with no independent leg: 215
- last run: 2026-10-03

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10642 |
| binary-field | 256 | 475 |
| binary-field-axioms | 256 | 475 |
| binary-field-group | 256 | 570 |
| binary-polynomial | 256 | 573 |
| binary-polynomial-division | 256 | 573 |
| binary-polynomial-gcd | 256 | 573 |
| clifford-motor | 512 | 392 |
| clifford-multivector | 512 | 392 |
| clifford-planar-complex | 512 | 392 |
| clifford-planar-dual | 512 | 392 |
| clifford-planar-split | 512 | 392 |
| clifford-quaternion-even | 512 | 392 |
| clifford-reverse | 512 | 392 |
| closed-unit | 512 | 626 |
| complex | 512 | 10643 |
| complex-direction | 512 | 520 |
| complex-divide | 512 | 633 |
| complex-rotate | 512 | 633 |
| contribution-fold-analog | 512 | 311 |
| contribution-fold-formula | 512 | 311 |
| contribution-fold-no-pool | 512 | 311 |
| contribution-fold-order | 512 | 311 |
| contribution-fold-quantization | 512 | 311 |
| core-word-modular | 512 | 17 |
| cost-bound-arithmetic | 512 | 58 |
| cost-model-budgets | 512 | 58 |
| cost-model-conversions | 512 | 58 |
| directed-magnitude | 512 | 243 |
| directed-product | 512 | 243 |
| directed-product-sum | 512 | 243 |
| directed-quotient | 512 | 243 |
| directed-root | 512 | 243 |
| dual | 512 | 8410 |
| dual-divide | 512 | 520 |
| dual-generic | 512 | 520 |
| dual-quaternion | 512 | 633 |
| dynamics | 512 | 128 |
| extension-field | 256 | 559 |
| extension-field-inverse | 256 | 468 |
| extension-field-norm | 256 | 559 |
| extension-field-power | 256 | 468 |
| extension-field-product | 256 | 559 |
| fixed-saturate | 512 | 58 |
| integer-bit-align | 256 | 32 |
| integer-bit-morton | 128 | 32 |
| integer-bit-scatter | 128 | 32 |
| integer-bit-smear | 256 | 32 |
| integer-hexagonal-index | 512 | 97 |
| integer-magic-constants | 512 | 89 |
| integer-try-arithmetic | 512 | 22 |
| integer-try-multiplication | 512 | 17 |
| interval-arc-functions | 512 | 2 |
| interval-arctangent | 512 | 2 |
| interval-arithmetic | 512 | 2 |
| interval-circular | 512 | 2 |
| interval-isotonic | 512 | 2 |
| interval-power | 512 | 2 |
| mass-box | 256 | 243 |
| mass-capsule | 256 | 243 |
| mass-compound | 256 | 243 |
| mass-cylinder | 256 | 243 |
| mass-parallel-axis | 256 | 243 |
| mass-sphere | 256 | 243 |
| mass-volume | 256 | 243 |
| meet-associative | 512 | 302 |
| meet-bottom-absorption | 512 | 302 |
| meet-commutative | 512 | 302 |
| meet-idempotent | 512 | 302 |
| meet-monotonicity | 512 | 302 |
| meet-order-coherence | 512 | 302 |
| meet-product-composition | 512 | 302 |
| meet-top-identity | 512 | 302 |
| mixed-scale | 512 | 243 |
| mixed-scale-triple | 512 | 243 |
| mobius | 512 | 8410 |
| monogenic-exact | 512 | 392 |
| monogenic-fusion | 512 | 392 |
| position | 512 | 507 |
| position-delta | 512 | 616 |
| position-translate | 512 | 616 |
| presented | 512 | 914 |
| prime-field | 256 | 564 |
| prime-field-chain | 256 | 564 |
| prime-field-lucas | 256 | 564 |
| prime-field-primality | 256 | 564 |
| prime-field-root | 256 | 564 |
| q1648-scalar | 512 | 293 |
| q1648-scalar-division | 512 | 291 |
| q3232-scalar | 512 | 256 |
| q3232-scalar-division | 512 | 255 |
| quaternion | 512 | 633 |
| quaternion-antiparallel | 512 | 7 |
| quaternion-arc | 512 | 7 |
| quaternion-direction | 512 | 520 |
| quaternion-rotate | 512 | 520 |
| quaternion-sublattice | 256 | 520 |
| rate | 512 | 617 |
| rigid | 512 | 656 |
| rigid-direction | 512 | 531 |
| rigid-exp-series | 512 | 5 |
| rigid-log-series | 512 | 5 |
| rigid-point | 512 | 656 |
| scalar | 512 | 8414 |
| scalar-division | 512 | 651 |
| scalar-smoothstep | 512 | 9 |
| scalar-text | 512 | 651 |
| scalar-transcendental | 512 | 653 |
| smoke | 64 | 2140 |
| split | 512 | 10643 |
| split-divide | 512 | 633 |
| split-transform | 512 | 633 |
| square-grid | 512 | 86 |
| sublattice | 256 | 4570 |
| symmetric-apply2 | 512 | 243 |
| symmetric-apply3 | 512 | 243 |
| symmetric-invert2 | 256 | 286 |
| symmetric-invert3 | 256 | 286 |
| symmetric-solve2 | 512 | 286 |
| symmetric-solve3 | 512 | 287 |
| unit-fraction16 | 512 | 540 |
| unit-fraction32 | 512 | 658 |
| unsigned-scalar | 512 | 644 |
| vector | 512 | 628 |
| vector-compare-length | 512 | 3 |
| vector-componentwise-helpers | 512 | 22 |
| vector-direction | 512 | 628 |
| vector-lattice | 512 | 516 |
| vector-narrow | 512 | 628 |
| vector-norm | 512 | 516 |
| vector-orthonormal-basis | 512 | 179 |
| vector-ray-plane | 512 | 20 |
| vector-within | 512 | 8 |

- last run: 2026-10-03
