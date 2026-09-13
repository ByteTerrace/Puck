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
| algebra-fractional | 512 | 10585 |
| binary-field | 256 | 424 |
| binary-field-axioms | 256 | 424 |
| binary-field-group | 256 | 513 |
| binary-polynomial | 256 | 516 |
| binary-polynomial-division | 256 | 516 |
| binary-polynomial-gcd | 256 | 516 |
| clifford-motor | 512 | 341 |
| clifford-multivector | 512 | 341 |
| clifford-planar-complex | 512 | 341 |
| clifford-planar-dual | 512 | 341 |
| clifford-planar-split | 512 | 341 |
| clifford-quaternion-even | 512 | 341 |
| clifford-reverse | 512 | 341 |
| closed-unit | 512 | 575 |
| complex | 512 | 10586 |
| complex-direction | 512 | 469 |
| complex-divide | 512 | 576 |
| complex-rotate | 512 | 576 |
| contribution-fold-analog | 512 | 260 |
| contribution-fold-formula | 512 | 260 |
| contribution-fold-no-pool | 512 | 260 |
| contribution-fold-order | 512 | 260 |
| contribution-fold-quantization | 512 | 260 |
| cost-bound-arithmetic | 512 | 7 |
| cost-model-budgets | 512 | 7 |
| cost-model-conversions | 512 | 7 |
| directed-magnitude | 512 | 192 |
| directed-product | 512 | 192 |
| directed-product-sum | 512 | 192 |
| directed-quotient | 512 | 192 |
| directed-root | 512 | 192 |
| dual | 512 | 8359 |
| dual-divide | 512 | 469 |
| dual-generic | 512 | 469 |
| dual-quaternion | 512 | 576 |
| dynamics | 512 | 77 |
| extension-field | 256 | 502 |
| extension-field-inverse | 256 | 417 |
| extension-field-norm | 256 | 502 |
| extension-field-power | 256 | 417 |
| extension-field-product | 256 | 502 |
| fixed-saturate | 512 | 7 |
| integer-hexagonal-index | 512 | 40 |
| integer-magic-constants | 512 | 38 |
| mass-box | 256 | 192 |
| mass-capsule | 256 | 192 |
| mass-compound | 256 | 192 |
| mass-cylinder | 256 | 192 |
| mass-parallel-axis | 256 | 192 |
| mass-sphere | 256 | 192 |
| mass-volume | 256 | 192 |
| meet-associative | 512 | 251 |
| meet-bottom-absorption | 512 | 251 |
| meet-commutative | 512 | 251 |
| meet-idempotent | 512 | 251 |
| meet-monotonicity | 512 | 251 |
| meet-order-coherence | 512 | 251 |
| meet-product-composition | 512 | 251 |
| meet-top-identity | 512 | 251 |
| mixed-scale | 512 | 192 |
| mixed-scale-triple | 512 | 192 |
| mobius | 512 | 8359 |
| monogenic-exact | 512 | 341 |
| monogenic-fusion | 512 | 341 |
| position | 512 | 456 |
| position-delta | 512 | 559 |
| position-translate | 512 | 559 |
| presented | 512 | 857 |
| prime-field | 256 | 507 |
| prime-field-chain | 256 | 507 |
| prime-field-lucas | 256 | 507 |
| prime-field-primality | 256 | 507 |
| prime-field-root | 256 | 507 |
| q1648-scalar | 512 | 242 |
| q1648-scalar-division | 512 | 240 |
| q3232-scalar | 512 | 205 |
| q3232-scalar-division | 512 | 204 |
| quaternion | 512 | 576 |
| quaternion-direction | 512 | 469 |
| quaternion-rotate | 512 | 469 |
| quaternion-sublattice | 256 | 469 |
| rate | 512 | 560 |
| rigid | 512 | 599 |
| rigid-direction | 512 | 480 |
| rigid-point | 512 | 599 |
| scalar | 512 | 8363 |
| scalar-division | 512 | 594 |
| scalar-text | 512 | 594 |
| scalar-transcendental | 512 | 596 |
| smoke | 64 | 2089 |
| split | 512 | 10586 |
| split-divide | 512 | 576 |
| split-transform | 512 | 576 |
| square-grid | 512 | 29 |
| sublattice | 256 | 4519 |
| symmetric-apply2 | 512 | 192 |
| symmetric-apply3 | 512 | 192 |
| symmetric-invert2 | 256 | 235 |
| symmetric-invert3 | 256 | 235 |
| symmetric-solve2 | 512 | 235 |
| symmetric-solve3 | 512 | 236 |
| unit-fraction16 | 512 | 489 |
| unit-fraction32 | 512 | 601 |
| unsigned-scalar | 512 | 587 |
| vector | 512 | 571 |
| vector-direction | 512 | 571 |
| vector-lattice | 512 | 465 |
| vector-narrow | 512 | 571 |
| vector-norm | 512 | 465 |
| vector-orthonormal-basis | 512 | 128 |

- last run: 2026-09-13
