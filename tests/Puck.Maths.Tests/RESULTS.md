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
- last run: 2026-08-16

## Default

- law cases executed: 511
- last run: 2026-08-16

## Deep

- law cases executed: 99
- last run: 2026-08-15

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 1687
- waived: 10
- uncovered: 634
- total public members: 2331
- last run: 2026-08-16

## Legs

| leg kind | legs |
| --- | --- |
| classical | 751 |
| in-tree-independent | 27 |
| presented-twin | 9 |
| relative-canary | 17 |
| shared-substrate:delegation-twin | 42 |
| shared-substrate:fused-substrate | 34 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 17 |
| shared-substrate:shared-upstream | 16 |
| shared-substrate:transcription | 26 |
| structural | 1076 |
| **total** | **2097** |

- statements: 639
- statements with no independent leg: 153
- last run: 2026-08-16

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10452 |
| binary-field | 256 | 313 |
| binary-field-axioms | 256 | 313 |
| binary-field-group | 256 | 380 |
| binary-polynomial | 256 | 383 |
| binary-polynomial-division | 256 | 383 |
| binary-polynomial-gcd | 256 | 383 |
| clifford-motor | 512 | 230 |
| clifford-multivector | 512 | 230 |
| clifford-planar-complex | 512 | 230 |
| clifford-planar-dual | 512 | 230 |
| clifford-planar-split | 512 | 230 |
| clifford-quaternion-even | 512 | 230 |
| clifford-reverse | 512 | 230 |
| closed-unit | 512 | 464 |
| complex | 512 | 10453 |
| complex-direction | 512 | 358 |
| complex-divide | 512 | 443 |
| complex-rotate | 512 | 443 |
| contribution-fold-analog | 512 | 149 |
| contribution-fold-formula | 512 | 149 |
| contribution-fold-no-pool | 512 | 149 |
| contribution-fold-order | 512 | 149 |
| contribution-fold-quantization | 512 | 149 |
| directed-magnitude | 512 | 81 |
| directed-product | 512 | 81 |
| directed-product-sum | 512 | 81 |
| directed-quotient | 512 | 81 |
| directed-root | 512 | 81 |
| dual | 512 | 8248 |
| dual-divide | 512 | 358 |
| dual-generic | 512 | 358 |
| dual-quaternion | 512 | 443 |
| extension-field | 256 | 369 |
| extension-field-inverse | 256 | 306 |
| extension-field-norm | 256 | 369 |
| extension-field-power | 256 | 306 |
| extension-field-product | 256 | 369 |
| mass-box | 256 | 81 |
| mass-capsule | 256 | 81 |
| mass-compound | 256 | 81 |
| mass-cylinder | 256 | 81 |
| mass-parallel-axis | 256 | 81 |
| mass-sphere | 256 | 81 |
| mass-volume | 256 | 81 |
| meet-associative | 512 | 140 |
| meet-bottom-absorption | 512 | 140 |
| meet-commutative | 512 | 140 |
| meet-idempotent | 512 | 140 |
| meet-monotonicity | 512 | 140 |
| meet-order-coherence | 512 | 140 |
| meet-product-composition | 512 | 140 |
| meet-top-identity | 512 | 140 |
| mixed-scale | 512 | 81 |
| mixed-scale-triple | 512 | 81 |
| mobius | 512 | 8248 |
| monogenic-exact | 512 | 230 |
| monogenic-fusion | 512 | 230 |
| position | 512 | 345 |
| position-delta | 512 | 426 |
| position-translate | 512 | 426 |
| presented | 512 | 724 |
| prime-field | 256 | 374 |
| prime-field-chain | 256 | 374 |
| prime-field-lucas | 256 | 374 |
| prime-field-primality | 256 | 374 |
| prime-field-root | 256 | 374 |
| q1648-scalar | 512 | 131 |
| q1648-scalar-division | 512 | 129 |
| q3232-scalar | 512 | 94 |
| q3232-scalar-division | 512 | 93 |
| quaternion | 512 | 443 |
| quaternion-direction | 512 | 358 |
| quaternion-rotate | 512 | 358 |
| quaternion-sublattice | 256 | 358 |
| rate | 512 | 427 |
| rigid | 512 | 466 |
| rigid-direction | 512 | 369 |
| rigid-point | 512 | 466 |
| scalar | 512 | 8252 |
| scalar-division | 512 | 461 |
| scalar-text | 512 | 461 |
| scalar-transcendental | 512 | 461 |
| smoke | 64 | 1978 |
| split | 512 | 10453 |
| split-divide | 512 | 443 |
| split-transform | 512 | 443 |
| sublattice | 256 | 4408 |
| symmetric-apply2 | 512 | 81 |
| symmetric-apply3 | 512 | 81 |
| symmetric-invert2 | 256 | 124 |
| symmetric-invert3 | 256 | 124 |
| symmetric-solve2 | 512 | 124 |
| symmetric-solve3 | 512 | 125 |
| unit-fraction16 | 512 | 378 |
| unit-fraction32 | 512 | 468 |
| unsigned-scalar | 512 | 454 |
| vector | 512 | 438 |
| vector-direction | 512 | 438 |
| vector-lattice | 512 | 354 |
| vector-narrow | 512 | 438 |
| vector-norm | 512 | 354 |
| vector-orthonormal-basis | 512 | 17 |

- last run: 2026-08-16
