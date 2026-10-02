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

- law cases executed: 642
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
| classical | 845 |
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
| **total** | **2338** |

- statements: 783
- statements with no independent leg: 213
- last run: 2026-10-02

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10637 |
| binary-field | 256 | 470 |
| binary-field-axioms | 256 | 470 |
| binary-field-group | 256 | 565 |
| binary-polynomial | 256 | 568 |
| binary-polynomial-division | 256 | 568 |
| binary-polynomial-gcd | 256 | 568 |
| clifford-motor | 512 | 387 |
| clifford-multivector | 512 | 387 |
| clifford-planar-complex | 512 | 387 |
| clifford-planar-dual | 512 | 387 |
| clifford-planar-split | 512 | 387 |
| clifford-quaternion-even | 512 | 387 |
| clifford-reverse | 512 | 387 |
| closed-unit | 512 | 621 |
| complex | 512 | 10638 |
| complex-direction | 512 | 515 |
| complex-divide | 512 | 628 |
| complex-rotate | 512 | 628 |
| contribution-fold-analog | 512 | 306 |
| contribution-fold-formula | 512 | 306 |
| contribution-fold-no-pool | 512 | 306 |
| contribution-fold-order | 512 | 306 |
| contribution-fold-quantization | 512 | 306 |
| core-word-modular | 512 | 12 |
| cost-bound-arithmetic | 512 | 53 |
| cost-model-budgets | 512 | 53 |
| cost-model-conversions | 512 | 53 |
| directed-magnitude | 512 | 238 |
| directed-product | 512 | 238 |
| directed-product-sum | 512 | 238 |
| directed-quotient | 512 | 238 |
| directed-root | 512 | 238 |
| dual | 512 | 8405 |
| dual-divide | 512 | 515 |
| dual-generic | 512 | 515 |
| dual-quaternion | 512 | 628 |
| dynamics | 512 | 123 |
| extension-field | 256 | 554 |
| extension-field-inverse | 256 | 463 |
| extension-field-norm | 256 | 554 |
| extension-field-power | 256 | 463 |
| extension-field-product | 256 | 554 |
| fixed-saturate | 512 | 53 |
| integer-bit-align | 256 | 27 |
| integer-bit-morton | 128 | 27 |
| integer-bit-scatter | 128 | 27 |
| integer-bit-smear | 256 | 27 |
| integer-hexagonal-index | 512 | 92 |
| integer-magic-constants | 512 | 84 |
| integer-try-arithmetic | 512 | 17 |
| integer-try-multiplication | 512 | 12 |
| mass-box | 256 | 238 |
| mass-capsule | 256 | 238 |
| mass-compound | 256 | 238 |
| mass-cylinder | 256 | 238 |
| mass-parallel-axis | 256 | 238 |
| mass-sphere | 256 | 238 |
| mass-volume | 256 | 238 |
| meet-associative | 512 | 297 |
| meet-bottom-absorption | 512 | 297 |
| meet-commutative | 512 | 297 |
| meet-idempotent | 512 | 297 |
| meet-monotonicity | 512 | 297 |
| meet-order-coherence | 512 | 297 |
| meet-product-composition | 512 | 297 |
| meet-top-identity | 512 | 297 |
| mixed-scale | 512 | 238 |
| mixed-scale-triple | 512 | 238 |
| mobius | 512 | 8405 |
| monogenic-exact | 512 | 387 |
| monogenic-fusion | 512 | 387 |
| position | 512 | 502 |
| position-delta | 512 | 611 |
| position-translate | 512 | 611 |
| presented | 512 | 909 |
| prime-field | 256 | 559 |
| prime-field-chain | 256 | 559 |
| prime-field-lucas | 256 | 559 |
| prime-field-primality | 256 | 559 |
| prime-field-root | 256 | 559 |
| q1648-scalar | 512 | 288 |
| q1648-scalar-division | 512 | 286 |
| q3232-scalar | 512 | 251 |
| q3232-scalar-division | 512 | 250 |
| quaternion | 512 | 628 |
| quaternion-antiparallel | 512 | 2 |
| quaternion-arc | 512 | 2 |
| quaternion-direction | 512 | 515 |
| quaternion-rotate | 512 | 515 |
| quaternion-sublattice | 256 | 515 |
| rate | 512 | 612 |
| rigid | 512 | 651 |
| rigid-direction | 512 | 526 |
| rigid-point | 512 | 651 |
| scalar | 512 | 8409 |
| scalar-division | 512 | 646 |
| scalar-smoothstep | 512 | 4 |
| scalar-text | 512 | 646 |
| scalar-transcendental | 512 | 648 |
| smoke | 64 | 2135 |
| split | 512 | 10638 |
| split-divide | 512 | 628 |
| split-transform | 512 | 628 |
| square-grid | 512 | 81 |
| sublattice | 256 | 4565 |
| symmetric-apply2 | 512 | 238 |
| symmetric-apply3 | 512 | 238 |
| symmetric-invert2 | 256 | 281 |
| symmetric-invert3 | 256 | 281 |
| symmetric-solve2 | 512 | 281 |
| symmetric-solve3 | 512 | 282 |
| unit-fraction16 | 512 | 535 |
| unit-fraction32 | 512 | 653 |
| unsigned-scalar | 512 | 639 |
| vector | 512 | 623 |
| vector-componentwise-helpers | 512 | 17 |
| vector-direction | 512 | 623 |
| vector-lattice | 512 | 511 |
| vector-narrow | 512 | 623 |
| vector-norm | 512 | 511 |
| vector-orthonormal-basis | 512 | 174 |
| vector-ray-plane | 512 | 15 |
| vector-within | 512 | 3 |

- last run: 2026-10-02
