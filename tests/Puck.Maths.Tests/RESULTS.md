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
| algebra-fractional | 512 | 10581 |
| binary-field | 256 | 420 |
| binary-field-axioms | 256 | 420 |
| binary-field-group | 256 | 509 |
| binary-polynomial | 256 | 512 |
| binary-polynomial-division | 256 | 512 |
| binary-polynomial-gcd | 256 | 512 |
| clifford-motor | 512 | 337 |
| clifford-multivector | 512 | 337 |
| clifford-planar-complex | 512 | 337 |
| clifford-planar-dual | 512 | 337 |
| clifford-planar-split | 512 | 337 |
| clifford-quaternion-even | 512 | 337 |
| clifford-reverse | 512 | 337 |
| closed-unit | 512 | 571 |
| complex | 512 | 10582 |
| complex-direction | 512 | 465 |
| complex-divide | 512 | 572 |
| complex-rotate | 512 | 572 |
| contribution-fold-analog | 512 | 256 |
| contribution-fold-formula | 512 | 256 |
| contribution-fold-no-pool | 512 | 256 |
| contribution-fold-order | 512 | 256 |
| contribution-fold-quantization | 512 | 256 |
| cost-bound-arithmetic | 512 | 3 |
| cost-model-budgets | 512 | 3 |
| cost-model-conversions | 512 | 3 |
| directed-magnitude | 512 | 188 |
| directed-product | 512 | 188 |
| directed-product-sum | 512 | 188 |
| directed-quotient | 512 | 188 |
| directed-root | 512 | 188 |
| dual | 512 | 8355 |
| dual-divide | 512 | 465 |
| dual-generic | 512 | 465 |
| dual-quaternion | 512 | 572 |
| dynamics | 512 | 73 |
| extension-field | 256 | 498 |
| extension-field-inverse | 256 | 413 |
| extension-field-norm | 256 | 498 |
| extension-field-power | 256 | 413 |
| extension-field-product | 256 | 498 |
| fixed-saturate | 512 | 3 |
| integer-hexagonal-index | 512 | 36 |
| integer-magic-constants | 512 | 34 |
| mass-box | 256 | 188 |
| mass-capsule | 256 | 188 |
| mass-compound | 256 | 188 |
| mass-cylinder | 256 | 188 |
| mass-parallel-axis | 256 | 188 |
| mass-sphere | 256 | 188 |
| mass-volume | 256 | 188 |
| meet-associative | 512 | 247 |
| meet-bottom-absorption | 512 | 247 |
| meet-commutative | 512 | 247 |
| meet-idempotent | 512 | 247 |
| meet-monotonicity | 512 | 247 |
| meet-order-coherence | 512 | 247 |
| meet-product-composition | 512 | 247 |
| meet-top-identity | 512 | 247 |
| mixed-scale | 512 | 188 |
| mixed-scale-triple | 512 | 188 |
| mobius | 512 | 8355 |
| monogenic-exact | 512 | 337 |
| monogenic-fusion | 512 | 337 |
| position | 512 | 452 |
| position-delta | 512 | 555 |
| position-translate | 512 | 555 |
| presented | 512 | 853 |
| prime-field | 256 | 503 |
| prime-field-chain | 256 | 503 |
| prime-field-lucas | 256 | 503 |
| prime-field-primality | 256 | 503 |
| prime-field-root | 256 | 503 |
| q1648-scalar | 512 | 238 |
| q1648-scalar-division | 512 | 236 |
| q3232-scalar | 512 | 201 |
| q3232-scalar-division | 512 | 200 |
| quaternion | 512 | 572 |
| quaternion-direction | 512 | 465 |
| quaternion-rotate | 512 | 465 |
| quaternion-sublattice | 256 | 465 |
| rate | 512 | 556 |
| rigid | 512 | 595 |
| rigid-direction | 512 | 476 |
| rigid-point | 512 | 595 |
| scalar | 512 | 8359 |
| scalar-division | 512 | 590 |
| scalar-text | 512 | 590 |
| scalar-transcendental | 512 | 592 |
| smoke | 64 | 2085 |
| split | 512 | 10582 |
| split-divide | 512 | 572 |
| split-transform | 512 | 572 |
| square-grid | 512 | 25 |
| sublattice | 256 | 4515 |
| symmetric-apply2 | 512 | 188 |
| symmetric-apply3 | 512 | 188 |
| symmetric-invert2 | 256 | 231 |
| symmetric-invert3 | 256 | 231 |
| symmetric-solve2 | 512 | 231 |
| symmetric-solve3 | 512 | 232 |
| unit-fraction16 | 512 | 485 |
| unit-fraction32 | 512 | 597 |
| unsigned-scalar | 512 | 583 |
| vector | 512 | 567 |
| vector-direction | 512 | 567 |
| vector-lattice | 512 | 461 |
| vector-narrow | 512 | 567 |
| vector-norm | 512 | 461 |
| vector-orthonormal-basis | 512 | 124 |

- last run: 2026-09-13
