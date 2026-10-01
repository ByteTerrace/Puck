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
- last run: 2026-10-01

## Default

- law cases executed: 637
- last run: 2026-10-01

## Deep

- law cases executed: 112
- last run: 2026-10-01

## Exhaustive

- law cases executed: 7
- last run: 2026-10-01

## Coverage

- covered: 2155
- waived: 43
- uncovered: 634
- total public members: 2832
- last run: 2026-10-01

## Legs

| leg kind | legs |
| --- | --- |
| classical | 840 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1200 |
| **total** | **2331** |

- statements: 778
- statements with no independent leg: 213
- last run: 2026-10-01

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10633 |
| binary-field | 256 | 466 |
| binary-field-axioms | 256 | 466 |
| binary-field-group | 256 | 561 |
| binary-polynomial | 256 | 564 |
| binary-polynomial-division | 256 | 564 |
| binary-polynomial-gcd | 256 | 564 |
| clifford-motor | 512 | 383 |
| clifford-multivector | 512 | 383 |
| clifford-planar-complex | 512 | 383 |
| clifford-planar-dual | 512 | 383 |
| clifford-planar-split | 512 | 383 |
| clifford-quaternion-even | 512 | 383 |
| clifford-reverse | 512 | 383 |
| closed-unit | 512 | 617 |
| complex | 512 | 10634 |
| complex-direction | 512 | 511 |
| complex-divide | 512 | 624 |
| complex-rotate | 512 | 624 |
| contribution-fold-analog | 512 | 302 |
| contribution-fold-formula | 512 | 302 |
| contribution-fold-no-pool | 512 | 302 |
| contribution-fold-order | 512 | 302 |
| contribution-fold-quantization | 512 | 302 |
| core-word-modular | 512 | 8 |
| cost-bound-arithmetic | 512 | 49 |
| cost-model-budgets | 512 | 49 |
| cost-model-conversions | 512 | 49 |
| directed-magnitude | 512 | 234 |
| directed-product | 512 | 234 |
| directed-product-sum | 512 | 234 |
| directed-quotient | 512 | 234 |
| directed-root | 512 | 234 |
| dual | 512 | 8401 |
| dual-divide | 512 | 511 |
| dual-generic | 512 | 511 |
| dual-quaternion | 512 | 624 |
| dynamics | 512 | 119 |
| extension-field | 256 | 550 |
| extension-field-inverse | 256 | 459 |
| extension-field-norm | 256 | 550 |
| extension-field-power | 256 | 459 |
| extension-field-product | 256 | 550 |
| fixed-saturate | 512 | 49 |
| integer-bit-align | 256 | 23 |
| integer-bit-morton | 128 | 23 |
| integer-bit-scatter | 128 | 23 |
| integer-bit-smear | 256 | 23 |
| integer-hexagonal-index | 512 | 88 |
| integer-magic-constants | 512 | 80 |
| integer-try-arithmetic | 512 | 13 |
| integer-try-multiplication | 512 | 8 |
| mass-box | 256 | 234 |
| mass-capsule | 256 | 234 |
| mass-compound | 256 | 234 |
| mass-cylinder | 256 | 234 |
| mass-parallel-axis | 256 | 234 |
| mass-sphere | 256 | 234 |
| mass-volume | 256 | 234 |
| meet-associative | 512 | 293 |
| meet-bottom-absorption | 512 | 293 |
| meet-commutative | 512 | 293 |
| meet-idempotent | 512 | 293 |
| meet-monotonicity | 512 | 293 |
| meet-order-coherence | 512 | 293 |
| meet-product-composition | 512 | 293 |
| meet-top-identity | 512 | 293 |
| mixed-scale | 512 | 234 |
| mixed-scale-triple | 512 | 234 |
| mobius | 512 | 8401 |
| monogenic-exact | 512 | 383 |
| monogenic-fusion | 512 | 383 |
| position | 512 | 498 |
| position-delta | 512 | 607 |
| position-translate | 512 | 607 |
| presented | 512 | 905 |
| prime-field | 256 | 555 |
| prime-field-chain | 256 | 555 |
| prime-field-lucas | 256 | 555 |
| prime-field-primality | 256 | 555 |
| prime-field-root | 256 | 555 |
| q1648-scalar | 512 | 284 |
| q1648-scalar-division | 512 | 282 |
| q3232-scalar | 512 | 247 |
| q3232-scalar-division | 512 | 246 |
| quaternion | 512 | 624 |
| quaternion-direction | 512 | 511 |
| quaternion-rotate | 512 | 511 |
| quaternion-sublattice | 256 | 511 |
| rate | 512 | 608 |
| rigid | 512 | 647 |
| rigid-direction | 512 | 522 |
| rigid-point | 512 | 647 |
| scalar | 512 | 8405 |
| scalar-division | 512 | 642 |
| scalar-text | 512 | 642 |
| scalar-transcendental | 512 | 644 |
| smoke | 64 | 2131 |
| split | 512 | 10634 |
| split-divide | 512 | 624 |
| split-transform | 512 | 624 |
| square-grid | 512 | 77 |
| sublattice | 256 | 4561 |
| symmetric-apply2 | 512 | 234 |
| symmetric-apply3 | 512 | 234 |
| symmetric-invert2 | 256 | 277 |
| symmetric-invert3 | 256 | 277 |
| symmetric-solve2 | 512 | 277 |
| symmetric-solve3 | 512 | 278 |
| unit-fraction16 | 512 | 531 |
| unit-fraction32 | 512 | 649 |
| unsigned-scalar | 512 | 635 |
| vector | 512 | 619 |
| vector-componentwise-helpers | 512 | 13 |
| vector-direction | 512 | 619 |
| vector-lattice | 512 | 507 |
| vector-narrow | 512 | 619 |
| vector-norm | 512 | 507 |
| vector-orthonormal-basis | 512 | 170 |
| vector-ray-plane | 512 | 11 |

- last run: 2026-10-01
