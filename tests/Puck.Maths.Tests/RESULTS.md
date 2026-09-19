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
- last run: 2026-09-15

## Default

- law cases executed: 616
- last run: 2026-09-15

## Deep

- law cases executed: 112
- last run: 2026-09-14

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 2116
- waived: 43
- uncovered: 634
- total public members: 2793
- last run: 2026-09-15

## Legs

| leg kind | legs |
| --- | --- |
| classical | 812 |
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
| **total** | **2289** |

- statements: 757
- statements with no independent leg: 211
- last run: 2026-09-15

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10600 |
| binary-field | 256 | 438 |
| binary-field-axioms | 256 | 438 |
| binary-field-group | 256 | 528 |
| binary-polynomial | 256 | 531 |
| binary-polynomial-division | 256 | 531 |
| binary-polynomial-gcd | 256 | 531 |
| clifford-motor | 512 | 355 |
| clifford-multivector | 512 | 355 |
| clifford-planar-complex | 512 | 355 |
| clifford-planar-dual | 512 | 355 |
| clifford-planar-split | 512 | 355 |
| clifford-quaternion-even | 512 | 355 |
| clifford-reverse | 512 | 355 |
| closed-unit | 512 | 589 |
| complex | 512 | 10601 |
| complex-direction | 512 | 483 |
| complex-divide | 512 | 591 |
| complex-rotate | 512 | 591 |
| contribution-fold-analog | 512 | 274 |
| contribution-fold-formula | 512 | 274 |
| contribution-fold-no-pool | 512 | 274 |
| contribution-fold-order | 512 | 274 |
| contribution-fold-quantization | 512 | 274 |
| cost-bound-arithmetic | 512 | 21 |
| cost-model-budgets | 512 | 21 |
| cost-model-conversions | 512 | 21 |
| directed-magnitude | 512 | 206 |
| directed-product | 512 | 206 |
| directed-product-sum | 512 | 206 |
| directed-quotient | 512 | 206 |
| directed-root | 512 | 206 |
| dual | 512 | 8373 |
| dual-divide | 512 | 483 |
| dual-generic | 512 | 483 |
| dual-quaternion | 512 | 591 |
| dynamics | 512 | 91 |
| extension-field | 256 | 517 |
| extension-field-inverse | 256 | 431 |
| extension-field-norm | 256 | 517 |
| extension-field-power | 256 | 431 |
| extension-field-product | 256 | 517 |
| fixed-saturate | 512 | 21 |
| integer-hexagonal-index | 512 | 55 |
| integer-magic-constants | 512 | 52 |
| mass-box | 256 | 206 |
| mass-capsule | 256 | 206 |
| mass-compound | 256 | 206 |
| mass-cylinder | 256 | 206 |
| mass-parallel-axis | 256 | 206 |
| mass-sphere | 256 | 206 |
| mass-volume | 256 | 206 |
| meet-associative | 512 | 265 |
| meet-bottom-absorption | 512 | 265 |
| meet-commutative | 512 | 265 |
| meet-idempotent | 512 | 265 |
| meet-monotonicity | 512 | 265 |
| meet-order-coherence | 512 | 265 |
| meet-product-composition | 512 | 265 |
| meet-top-identity | 512 | 265 |
| mixed-scale | 512 | 206 |
| mixed-scale-triple | 512 | 206 |
| mobius | 512 | 8373 |
| monogenic-exact | 512 | 355 |
| monogenic-fusion | 512 | 355 |
| position | 512 | 470 |
| position-delta | 512 | 574 |
| position-translate | 512 | 574 |
| presented | 512 | 872 |
| prime-field | 256 | 522 |
| prime-field-chain | 256 | 522 |
| prime-field-lucas | 256 | 522 |
| prime-field-primality | 256 | 522 |
| prime-field-root | 256 | 522 |
| q1648-scalar | 512 | 256 |
| q1648-scalar-division | 512 | 254 |
| q3232-scalar | 512 | 219 |
| q3232-scalar-division | 512 | 218 |
| quaternion | 512 | 591 |
| quaternion-direction | 512 | 483 |
| quaternion-rotate | 512 | 483 |
| quaternion-sublattice | 256 | 483 |
| rate | 512 | 575 |
| rigid | 512 | 614 |
| rigid-direction | 512 | 494 |
| rigid-point | 512 | 614 |
| scalar | 512 | 8377 |
| scalar-division | 512 | 609 |
| scalar-text | 512 | 609 |
| scalar-transcendental | 512 | 611 |
| smoke | 64 | 2103 |
| split | 512 | 10601 |
| split-divide | 512 | 591 |
| split-transform | 512 | 591 |
| square-grid | 512 | 44 |
| sublattice | 256 | 4533 |
| symmetric-apply2 | 512 | 206 |
| symmetric-apply3 | 512 | 206 |
| symmetric-invert2 | 256 | 249 |
| symmetric-invert3 | 256 | 249 |
| symmetric-solve2 | 512 | 249 |
| symmetric-solve3 | 512 | 250 |
| unit-fraction16 | 512 | 503 |
| unit-fraction32 | 512 | 616 |
| unsigned-scalar | 512 | 602 |
| vector | 512 | 586 |
| vector-direction | 512 | 586 |
| vector-lattice | 512 | 479 |
| vector-narrow | 512 | 586 |
| vector-norm | 512 | 479 |
| vector-orthonormal-basis | 512 | 142 |

- last run: 2026-09-15
