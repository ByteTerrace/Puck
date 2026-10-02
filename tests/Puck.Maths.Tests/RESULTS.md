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

- law cases executed: 639
- last run: 2026-10-02

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2156
- waived: 43
- uncovered: 634
- total public members: 2833
- last run: 2026-10-02

## Legs

| leg kind | legs |
| --- | --- |
| classical | 842 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1201 |
| **total** | **2334** |

- statements: 780
- statements with no independent leg: 213
- last run: 2026-10-02

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10634 |
| binary-field | 256 | 467 |
| binary-field-axioms | 256 | 467 |
| binary-field-group | 256 | 562 |
| binary-polynomial | 256 | 565 |
| binary-polynomial-division | 256 | 565 |
| binary-polynomial-gcd | 256 | 565 |
| clifford-motor | 512 | 384 |
| clifford-multivector | 512 | 384 |
| clifford-planar-complex | 512 | 384 |
| clifford-planar-dual | 512 | 384 |
| clifford-planar-split | 512 | 384 |
| clifford-quaternion-even | 512 | 384 |
| clifford-reverse | 512 | 384 |
| closed-unit | 512 | 618 |
| complex | 512 | 10635 |
| complex-direction | 512 | 512 |
| complex-divide | 512 | 625 |
| complex-rotate | 512 | 625 |
| contribution-fold-analog | 512 | 303 |
| contribution-fold-formula | 512 | 303 |
| contribution-fold-no-pool | 512 | 303 |
| contribution-fold-order | 512 | 303 |
| contribution-fold-quantization | 512 | 303 |
| core-word-modular | 512 | 9 |
| cost-bound-arithmetic | 512 | 50 |
| cost-model-budgets | 512 | 50 |
| cost-model-conversions | 512 | 50 |
| directed-magnitude | 512 | 235 |
| directed-product | 512 | 235 |
| directed-product-sum | 512 | 235 |
| directed-quotient | 512 | 235 |
| directed-root | 512 | 235 |
| dual | 512 | 8402 |
| dual-divide | 512 | 512 |
| dual-generic | 512 | 512 |
| dual-quaternion | 512 | 625 |
| dynamics | 512 | 120 |
| extension-field | 256 | 551 |
| extension-field-inverse | 256 | 460 |
| extension-field-norm | 256 | 551 |
| extension-field-power | 256 | 460 |
| extension-field-product | 256 | 551 |
| fixed-saturate | 512 | 50 |
| integer-bit-align | 256 | 24 |
| integer-bit-morton | 128 | 24 |
| integer-bit-scatter | 128 | 24 |
| integer-bit-smear | 256 | 24 |
| integer-hexagonal-index | 512 | 89 |
| integer-magic-constants | 512 | 81 |
| integer-try-arithmetic | 512 | 14 |
| integer-try-multiplication | 512 | 9 |
| mass-box | 256 | 235 |
| mass-capsule | 256 | 235 |
| mass-compound | 256 | 235 |
| mass-cylinder | 256 | 235 |
| mass-parallel-axis | 256 | 235 |
| mass-sphere | 256 | 235 |
| mass-volume | 256 | 235 |
| meet-associative | 512 | 294 |
| meet-bottom-absorption | 512 | 294 |
| meet-commutative | 512 | 294 |
| meet-idempotent | 512 | 294 |
| meet-monotonicity | 512 | 294 |
| meet-order-coherence | 512 | 294 |
| meet-product-composition | 512 | 294 |
| meet-top-identity | 512 | 294 |
| mixed-scale | 512 | 235 |
| mixed-scale-triple | 512 | 235 |
| mobius | 512 | 8402 |
| monogenic-exact | 512 | 384 |
| monogenic-fusion | 512 | 384 |
| position | 512 | 499 |
| position-delta | 512 | 608 |
| position-translate | 512 | 608 |
| presented | 512 | 906 |
| prime-field | 256 | 556 |
| prime-field-chain | 256 | 556 |
| prime-field-lucas | 256 | 556 |
| prime-field-primality | 256 | 556 |
| prime-field-root | 256 | 556 |
| q1648-scalar | 512 | 285 |
| q1648-scalar-division | 512 | 283 |
| q3232-scalar | 512 | 248 |
| q3232-scalar-division | 512 | 247 |
| quaternion | 512 | 625 |
| quaternion-direction | 512 | 512 |
| quaternion-rotate | 512 | 512 |
| quaternion-sublattice | 256 | 512 |
| rate | 512 | 609 |
| rigid | 512 | 648 |
| rigid-direction | 512 | 523 |
| rigid-point | 512 | 648 |
| scalar | 512 | 8406 |
| scalar-division | 512 | 643 |
| scalar-smoothstep | 512 | 1 |
| scalar-text | 512 | 643 |
| scalar-transcendental | 512 | 645 |
| smoke | 64 | 2132 |
| split | 512 | 10635 |
| split-divide | 512 | 625 |
| split-transform | 512 | 625 |
| square-grid | 512 | 78 |
| sublattice | 256 | 4562 |
| symmetric-apply2 | 512 | 235 |
| symmetric-apply3 | 512 | 235 |
| symmetric-invert2 | 256 | 278 |
| symmetric-invert3 | 256 | 278 |
| symmetric-solve2 | 512 | 278 |
| symmetric-solve3 | 512 | 279 |
| unit-fraction16 | 512 | 532 |
| unit-fraction32 | 512 | 650 |
| unsigned-scalar | 512 | 636 |
| vector | 512 | 620 |
| vector-componentwise-helpers | 512 | 14 |
| vector-direction | 512 | 620 |
| vector-lattice | 512 | 508 |
| vector-narrow | 512 | 620 |
| vector-norm | 512 | 508 |
| vector-orthonormal-basis | 512 | 171 |
| vector-ray-plane | 512 | 12 |

- last run: 2026-10-02
