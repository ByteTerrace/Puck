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
- last run: 2026-09-12

## Default

- law cases executed: 610
- last run: 2026-09-12

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
- last run: 2026-09-12

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
- last run: 2026-09-12

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10580 |
| binary-field | 256 | 419 |
| binary-field-axioms | 256 | 419 |
| binary-field-group | 256 | 508 |
| binary-polynomial | 256 | 511 |
| binary-polynomial-division | 256 | 511 |
| binary-polynomial-gcd | 256 | 511 |
| clifford-motor | 512 | 336 |
| clifford-multivector | 512 | 336 |
| clifford-planar-complex | 512 | 336 |
| clifford-planar-dual | 512 | 336 |
| clifford-planar-split | 512 | 336 |
| clifford-quaternion-even | 512 | 336 |
| clifford-reverse | 512 | 336 |
| closed-unit | 512 | 570 |
| complex | 512 | 10581 |
| complex-direction | 512 | 464 |
| complex-divide | 512 | 571 |
| complex-rotate | 512 | 571 |
| contribution-fold-analog | 512 | 255 |
| contribution-fold-formula | 512 | 255 |
| contribution-fold-no-pool | 512 | 255 |
| contribution-fold-order | 512 | 255 |
| contribution-fold-quantization | 512 | 255 |
| cost-bound-arithmetic | 512 | 2 |
| cost-model-budgets | 512 | 2 |
| cost-model-conversions | 512 | 2 |
| directed-magnitude | 512 | 187 |
| directed-product | 512 | 187 |
| directed-product-sum | 512 | 187 |
| directed-quotient | 512 | 187 |
| directed-root | 512 | 187 |
| dual | 512 | 8354 |
| dual-divide | 512 | 464 |
| dual-generic | 512 | 464 |
| dual-quaternion | 512 | 571 |
| dynamics | 512 | 72 |
| extension-field | 256 | 497 |
| extension-field-inverse | 256 | 412 |
| extension-field-norm | 256 | 497 |
| extension-field-power | 256 | 412 |
| extension-field-product | 256 | 497 |
| fixed-saturate | 512 | 2 |
| integer-hexagonal-index | 512 | 35 |
| integer-magic-constants | 512 | 33 |
| mass-box | 256 | 187 |
| mass-capsule | 256 | 187 |
| mass-compound | 256 | 187 |
| mass-cylinder | 256 | 187 |
| mass-parallel-axis | 256 | 187 |
| mass-sphere | 256 | 187 |
| mass-volume | 256 | 187 |
| meet-associative | 512 | 246 |
| meet-bottom-absorption | 512 | 246 |
| meet-commutative | 512 | 246 |
| meet-idempotent | 512 | 246 |
| meet-monotonicity | 512 | 246 |
| meet-order-coherence | 512 | 246 |
| meet-product-composition | 512 | 246 |
| meet-top-identity | 512 | 246 |
| mixed-scale | 512 | 187 |
| mixed-scale-triple | 512 | 187 |
| mobius | 512 | 8354 |
| monogenic-exact | 512 | 336 |
| monogenic-fusion | 512 | 336 |
| position | 512 | 451 |
| position-delta | 512 | 554 |
| position-translate | 512 | 554 |
| presented | 512 | 852 |
| prime-field | 256 | 502 |
| prime-field-chain | 256 | 502 |
| prime-field-lucas | 256 | 502 |
| prime-field-primality | 256 | 502 |
| prime-field-root | 256 | 502 |
| q1648-scalar | 512 | 237 |
| q1648-scalar-division | 512 | 235 |
| q3232-scalar | 512 | 200 |
| q3232-scalar-division | 512 | 199 |
| quaternion | 512 | 571 |
| quaternion-direction | 512 | 464 |
| quaternion-rotate | 512 | 464 |
| quaternion-sublattice | 256 | 464 |
| rate | 512 | 555 |
| rigid | 512 | 594 |
| rigid-direction | 512 | 475 |
| rigid-point | 512 | 594 |
| scalar | 512 | 8358 |
| scalar-division | 512 | 589 |
| scalar-text | 512 | 589 |
| scalar-transcendental | 512 | 591 |
| smoke | 64 | 2084 |
| split | 512 | 10581 |
| split-divide | 512 | 571 |
| split-transform | 512 | 571 |
| square-grid | 512 | 24 |
| sublattice | 256 | 4514 |
| symmetric-apply2 | 512 | 187 |
| symmetric-apply3 | 512 | 187 |
| symmetric-invert2 | 256 | 230 |
| symmetric-invert3 | 256 | 230 |
| symmetric-solve2 | 512 | 230 |
| symmetric-solve3 | 512 | 231 |
| unit-fraction16 | 512 | 484 |
| unit-fraction32 | 512 | 596 |
| unsigned-scalar | 512 | 582 |
| vector | 512 | 566 |
| vector-direction | 512 | 566 |
| vector-lattice | 512 | 460 |
| vector-narrow | 512 | 566 |
| vector-norm | 512 | 460 |
| vector-orthonormal-basis | 512 | 123 |

- last run: 2026-09-12
