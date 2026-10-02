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
| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings` |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings` |
| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/exhaustive.runsettings` |

## Smoke

- law cases executed: 22
- last run: 2026-10-02

## Default

- law cases executed: 644
- last run: 2026-10-02

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2157
- waived: 43
- uncovered: 634
- total public members: 2834
- last run: 2026-10-02

## Legs

| leg kind | legs |
| --- | --- |
| classical | 847 |
| in-tree-independent | 32 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1201 |
| **total** | **2340** |

- statements: 785
- statements with no independent leg: 213
- last run: 2026-10-02

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10639 |
| binary-field | 256 | 472 |
| binary-field-axioms | 256 | 472 |
| binary-field-group | 256 | 567 |
| binary-polynomial | 256 | 570 |
| binary-polynomial-division | 256 | 570 |
| binary-polynomial-gcd | 256 | 570 |
| clifford-motor | 512 | 389 |
| clifford-multivector | 512 | 389 |
| clifford-planar-complex | 512 | 389 |
| clifford-planar-dual | 512 | 389 |
| clifford-planar-split | 512 | 389 |
| clifford-quaternion-even | 512 | 389 |
| clifford-reverse | 512 | 389 |
| closed-unit | 512 | 623 |
| complex | 512 | 10640 |
| complex-direction | 512 | 517 |
| complex-divide | 512 | 630 |
| complex-rotate | 512 | 630 |
| contribution-fold-analog | 512 | 308 |
| contribution-fold-formula | 512 | 308 |
| contribution-fold-no-pool | 512 | 308 |
| contribution-fold-order | 512 | 308 |
| contribution-fold-quantization | 512 | 308 |
| core-word-modular | 512 | 14 |
| cost-bound-arithmetic | 512 | 55 |
| cost-model-budgets | 512 | 55 |
| cost-model-conversions | 512 | 55 |
| directed-magnitude | 512 | 240 |
| directed-product | 512 | 240 |
| directed-product-sum | 512 | 240 |
| directed-quotient | 512 | 240 |
| directed-root | 512 | 240 |
| dual | 512 | 8407 |
| dual-divide | 512 | 517 |
| dual-generic | 512 | 517 |
| dual-quaternion | 512 | 630 |
| dynamics | 512 | 125 |
| extension-field | 256 | 556 |
| extension-field-inverse | 256 | 465 |
| extension-field-norm | 256 | 556 |
| extension-field-power | 256 | 465 |
| extension-field-product | 256 | 556 |
| fixed-saturate | 512 | 55 |
| integer-bit-align | 256 | 29 |
| integer-bit-morton | 128 | 29 |
| integer-bit-scatter | 128 | 29 |
| integer-bit-smear | 256 | 29 |
| integer-hexagonal-index | 512 | 94 |
| integer-magic-constants | 512 | 86 |
| integer-try-arithmetic | 512 | 19 |
| integer-try-multiplication | 512 | 14 |
| mass-box | 256 | 240 |
| mass-capsule | 256 | 240 |
| mass-compound | 256 | 240 |
| mass-cylinder | 256 | 240 |
| mass-parallel-axis | 256 | 240 |
| mass-sphere | 256 | 240 |
| mass-volume | 256 | 240 |
| meet-associative | 512 | 299 |
| meet-bottom-absorption | 512 | 299 |
| meet-commutative | 512 | 299 |
| meet-idempotent | 512 | 299 |
| meet-monotonicity | 512 | 299 |
| meet-order-coherence | 512 | 299 |
| meet-product-composition | 512 | 299 |
| meet-top-identity | 512 | 299 |
| mixed-scale | 512 | 240 |
| mixed-scale-triple | 512 | 240 |
| mobius | 512 | 8407 |
| monogenic-exact | 512 | 389 |
| monogenic-fusion | 512 | 389 |
| position | 512 | 504 |
| position-delta | 512 | 613 |
| position-translate | 512 | 613 |
| presented | 512 | 911 |
| prime-field | 256 | 561 |
| prime-field-chain | 256 | 561 |
| prime-field-lucas | 256 | 561 |
| prime-field-primality | 256 | 561 |
| prime-field-root | 256 | 561 |
| q1648-scalar | 512 | 290 |
| q1648-scalar-division | 512 | 288 |
| q3232-scalar | 512 | 253 |
| q3232-scalar-division | 512 | 252 |
| quaternion | 512 | 630 |
| quaternion-antiparallel | 512 | 4 |
| quaternion-arc | 512 | 4 |
| quaternion-direction | 512 | 517 |
| quaternion-rotate | 512 | 517 |
| quaternion-sublattice | 256 | 517 |
| rate | 512 | 614 |
| rigid | 512 | 653 |
| rigid-direction | 512 | 528 |
| rigid-exp-series | 512 | 2 |
| rigid-log-series | 512 | 2 |
| rigid-point | 512 | 653 |
| scalar | 512 | 8411 |
| scalar-division | 512 | 648 |
| scalar-smoothstep | 512 | 6 |
| scalar-text | 512 | 648 |
| scalar-transcendental | 512 | 650 |
| smoke | 64 | 2137 |
| split | 512 | 10640 |
| split-divide | 512 | 630 |
| split-transform | 512 | 630 |
| square-grid | 512 | 83 |
| sublattice | 256 | 4567 |
| symmetric-apply2 | 512 | 240 |
| symmetric-apply3 | 512 | 240 |
| symmetric-invert2 | 256 | 283 |
| symmetric-invert3 | 256 | 283 |
| symmetric-solve2 | 512 | 283 |
| symmetric-solve3 | 512 | 284 |
| unit-fraction16 | 512 | 537 |
| unit-fraction32 | 512 | 655 |
| unsigned-scalar | 512 | 641 |
| vector | 512 | 625 |
| vector-componentwise-helpers | 512 | 19 |
| vector-direction | 512 | 625 |
| vector-lattice | 512 | 513 |
| vector-narrow | 512 | 625 |
| vector-norm | 512 | 513 |
| vector-orthonormal-basis | 512 | 176 |
| vector-ray-plane | 512 | 17 |
| vector-within | 512 | 5 |

- last run: 2026-10-02
