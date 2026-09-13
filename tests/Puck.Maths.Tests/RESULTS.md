# Puck.Maths.Tests — RESULTS

Machine-written by the assembly ledger at run end. Each block records the last run that exercised it — a tier
block only when that tier ran law cases, coverage only when the ratchet gate ran, the frontier only when a run
consumed domains AND every law it ran passed — so every other block keeps the text its own last run left. The
last-run dates are the only volatile content; they do not by themselves trigger a rewrite.

Every figure below is MACHINE-INDEPENDENT by construction: the same commit produces the same counts and the same
frontier indices on every machine, so a difference here is a real difference and never a difference of hardware.
No duration is recorded, deliberately. One here would carry no machine identity, would span the whole session
rather than the block it sits under, and would be taken without a busy-machine guard — so it could not answer
any question asked of it. Cost is the bench tier's business: a RATIO against a baseline held per machine, which
records nothing at all when the environment is suspect, and which names the machine it ran on.

## Invocations

| tier | command |
| --- | --- |
| Default (Smoke+Default) | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release` |
| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings` |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings` |
| Bench | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/bench.runsettings` |

## Smoke

- law cases executed: 22
- last run: 2026-09-13

## Default

- law cases executed: 610
- last run: 2026-09-13

## Deep

- law cases executed: 112
- last run: 2026-09-05

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 2109
- waived: 43
- uncovered: 634
- total public members: 2786
- last run: 2026-09-13

## Legs

| leg kind | legs |
| --- | --- |
| classical | 806 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1186 |
| **total** | **2283** |

- statements: 751
- statements with no independent leg: 211
- last run: 2026-09-13

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10582 |
| binary-field | 256 | 421 |
| binary-field-axioms | 256 | 421 |
| binary-field-group | 256 | 510 |
| binary-polynomial | 256 | 513 |
| binary-polynomial-division | 256 | 513 |
| binary-polynomial-gcd | 256 | 513 |
| clifford-motor | 512 | 338 |
| clifford-multivector | 512 | 338 |
| clifford-planar-complex | 512 | 338 |
| clifford-planar-dual | 512 | 338 |
| clifford-planar-split | 512 | 338 |
| clifford-quaternion-even | 512 | 338 |
| clifford-reverse | 512 | 338 |
| closed-unit | 512 | 572 |
| complex | 512 | 10583 |
| complex-direction | 512 | 466 |
| complex-divide | 512 | 573 |
| complex-rotate | 512 | 573 |
| contribution-fold-analog | 512 | 257 |
| contribution-fold-formula | 512 | 257 |
| contribution-fold-no-pool | 512 | 257 |
| contribution-fold-order | 512 | 257 |
| contribution-fold-quantization | 512 | 257 |
| cost-bound-arithmetic | 512 | 4 |
| cost-model-budgets | 512 | 4 |
| cost-model-conversions | 512 | 4 |
| directed-magnitude | 512 | 189 |
| directed-product | 512 | 189 |
| directed-product-sum | 512 | 189 |
| directed-quotient | 512 | 189 |
| directed-root | 512 | 189 |
| dual | 512 | 8356 |
| dual-divide | 512 | 466 |
| dual-generic | 512 | 466 |
| dual-quaternion | 512 | 573 |
| dynamics | 512 | 74 |
| extension-field | 256 | 499 |
| extension-field-inverse | 256 | 414 |
| extension-field-norm | 256 | 499 |
| extension-field-power | 256 | 414 |
| extension-field-product | 256 | 499 |
| fixed-saturate | 512 | 4 |
| integer-hexagonal-index | 512 | 37 |
| integer-magic-constants | 512 | 35 |
| mass-box | 256 | 189 |
| mass-capsule | 256 | 189 |
| mass-compound | 256 | 189 |
| mass-cylinder | 256 | 189 |
| mass-parallel-axis | 256 | 189 |
| mass-sphere | 256 | 189 |
| mass-volume | 256 | 189 |
| meet-associative | 512 | 248 |
| meet-bottom-absorption | 512 | 248 |
| meet-commutative | 512 | 248 |
| meet-idempotent | 512 | 248 |
| meet-monotonicity | 512 | 248 |
| meet-order-coherence | 512 | 248 |
| meet-product-composition | 512 | 248 |
| meet-top-identity | 512 | 248 |
| mixed-scale | 512 | 189 |
| mixed-scale-triple | 512 | 189 |
| mobius | 512 | 8356 |
| monogenic-exact | 512 | 338 |
| monogenic-fusion | 512 | 338 |
| position | 512 | 453 |
| position-delta | 512 | 556 |
| position-translate | 512 | 556 |
| presented | 512 | 854 |
| prime-field | 256 | 504 |
| prime-field-chain | 256 | 504 |
| prime-field-lucas | 256 | 504 |
| prime-field-primality | 256 | 504 |
| prime-field-root | 256 | 504 |
| q1648-scalar | 512 | 239 |
| q1648-scalar-division | 512 | 237 |
| q3232-scalar | 512 | 202 |
| q3232-scalar-division | 512 | 201 |
| quaternion | 512 | 573 |
| quaternion-direction | 512 | 466 |
| quaternion-rotate | 512 | 466 |
| quaternion-sublattice | 256 | 466 |
| rate | 512 | 557 |
| rigid | 512 | 596 |
| rigid-direction | 512 | 477 |
| rigid-point | 512 | 596 |
| scalar | 512 | 8360 |
| scalar-division | 512 | 591 |
| scalar-text | 512 | 591 |
| scalar-transcendental | 512 | 593 |
| smoke | 64 | 2086 |
| split | 512 | 10583 |
| split-divide | 512 | 573 |
| split-transform | 512 | 573 |
| square-grid | 512 | 26 |
| sublattice | 256 | 4516 |
| symmetric-apply2 | 512 | 189 |
| symmetric-apply3 | 512 | 189 |
| symmetric-invert2 | 256 | 232 |
| symmetric-invert3 | 256 | 232 |
| symmetric-solve2 | 512 | 232 |
| symmetric-solve3 | 512 | 233 |
| unit-fraction16 | 512 | 486 |
| unit-fraction32 | 512 | 598 |
| unsigned-scalar | 512 | 584 |
| vector | 512 | 568 |
| vector-direction | 512 | 568 |
| vector-lattice | 512 | 462 |
| vector-narrow | 512 | 568 |
| vector-norm | 512 | 462 |
| vector-orthonormal-basis | 512 | 125 |

- last run: 2026-09-13
