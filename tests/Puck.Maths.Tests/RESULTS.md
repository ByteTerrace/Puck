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
- last run: 2026-09-06

## Default

- law cases executed: 594
- last run: 2026-09-06

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

- covered: 2054
- waived: 40
- uncovered: 634
- total public members: 2728
- last run: 2026-09-06

## Legs

| leg kind | legs |
| --- | --- |
| classical | 800 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 44 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 27 |
| structural | 1164 |
| **total** | **2252** |

- statements: 735
- statements with no independent leg: 201
- last run: 2026-09-06

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10571 |
| binary-field | 256 | 410 |
| binary-field-axioms | 256 | 410 |
| binary-field-group | 256 | 499 |
| binary-polynomial | 256 | 502 |
| binary-polynomial-division | 256 | 502 |
| binary-polynomial-gcd | 256 | 502 |
| clifford-motor | 512 | 327 |
| clifford-multivector | 512 | 327 |
| clifford-planar-complex | 512 | 327 |
| clifford-planar-dual | 512 | 327 |
| clifford-planar-split | 512 | 327 |
| clifford-quaternion-even | 512 | 327 |
| clifford-reverse | 512 | 327 |
| closed-unit | 512 | 561 |
| complex | 512 | 10572 |
| complex-direction | 512 | 455 |
| complex-divide | 512 | 562 |
| complex-rotate | 512 | 562 |
| contribution-fold-analog | 512 | 246 |
| contribution-fold-formula | 512 | 246 |
| contribution-fold-no-pool | 512 | 246 |
| contribution-fold-order | 512 | 246 |
| contribution-fold-quantization | 512 | 246 |
| directed-magnitude | 512 | 178 |
| directed-product | 512 | 178 |
| directed-product-sum | 512 | 178 |
| directed-quotient | 512 | 178 |
| directed-root | 512 | 178 |
| dual | 512 | 8345 |
| dual-divide | 512 | 455 |
| dual-generic | 512 | 455 |
| dual-quaternion | 512 | 562 |
| dynamics | 512 | 63 |
| extension-field | 256 | 488 |
| extension-field-inverse | 256 | 403 |
| extension-field-norm | 256 | 488 |
| extension-field-power | 256 | 403 |
| extension-field-product | 256 | 488 |
| integer-hexagonal-index | 512 | 26 |
| integer-magic-constants | 512 | 24 |
| mass-box | 256 | 178 |
| mass-capsule | 256 | 178 |
| mass-compound | 256 | 178 |
| mass-cylinder | 256 | 178 |
| mass-parallel-axis | 256 | 178 |
| mass-sphere | 256 | 178 |
| mass-volume | 256 | 178 |
| meet-associative | 512 | 237 |
| meet-bottom-absorption | 512 | 237 |
| meet-commutative | 512 | 237 |
| meet-idempotent | 512 | 237 |
| meet-monotonicity | 512 | 237 |
| meet-order-coherence | 512 | 237 |
| meet-product-composition | 512 | 237 |
| meet-top-identity | 512 | 237 |
| mixed-scale | 512 | 178 |
| mixed-scale-triple | 512 | 178 |
| mobius | 512 | 8345 |
| monogenic-exact | 512 | 327 |
| monogenic-fusion | 512 | 327 |
| position | 512 | 442 |
| position-delta | 512 | 545 |
| position-translate | 512 | 545 |
| presented | 512 | 843 |
| prime-field | 256 | 493 |
| prime-field-chain | 256 | 493 |
| prime-field-lucas | 256 | 493 |
| prime-field-primality | 256 | 493 |
| prime-field-root | 256 | 493 |
| q1648-scalar | 512 | 228 |
| q1648-scalar-division | 512 | 226 |
| q3232-scalar | 512 | 191 |
| q3232-scalar-division | 512 | 190 |
| quaternion | 512 | 562 |
| quaternion-direction | 512 | 455 |
| quaternion-rotate | 512 | 455 |
| quaternion-sublattice | 256 | 455 |
| rate | 512 | 546 |
| rigid | 512 | 585 |
| rigid-direction | 512 | 466 |
| rigid-point | 512 | 585 |
| scalar | 512 | 8349 |
| scalar-division | 512 | 580 |
| scalar-text | 512 | 580 |
| scalar-transcendental | 512 | 582 |
| smoke | 64 | 2075 |
| split | 512 | 10572 |
| split-divide | 512 | 562 |
| split-transform | 512 | 562 |
| square-grid | 512 | 15 |
| sublattice | 256 | 4505 |
| symmetric-apply2 | 512 | 178 |
| symmetric-apply3 | 512 | 178 |
| symmetric-invert2 | 256 | 221 |
| symmetric-invert3 | 256 | 221 |
| symmetric-solve2 | 512 | 221 |
| symmetric-solve3 | 512 | 222 |
| unit-fraction16 | 512 | 475 |
| unit-fraction32 | 512 | 587 |
| unsigned-scalar | 512 | 573 |
| vector | 512 | 557 |
| vector-direction | 512 | 557 |
| vector-lattice | 512 | 451 |
| vector-narrow | 512 | 557 |
| vector-norm | 512 | 451 |
| vector-orthonormal-basis | 512 | 114 |

- last run: 2026-09-06
