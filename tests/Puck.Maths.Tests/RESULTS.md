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
- last run: 2026-08-25

## Default

- law cases executed: 5
- last run: 2026-08-25

## Deep

- law cases executed: 102
- last run: 2026-08-23

## Exhaustive

- law cases executed: 7
- last run: 2026-08-07

## Bench

| bench | median ratio | baseline | band | status |
| --- | --- | --- | --- | --- |
| bench.complex-mul-ratio | 0.9731 | 0.9663 | 0.0483 | within-band |

- last run: 2026-07-31

## Coverage

- covered: 1848
- waived: 36
- uncovered: 634
- total public members: 2518
- last run: 2026-08-25

## Legs

| leg kind | legs |
| --- | --- |
| classical | 760 |
| in-tree-independent | 28 |
| presented-twin | 9 |
| relative-canary | 17 |
| shared-substrate:delegation-twin | 42 |
| shared-substrate:fused-substrate | 35 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 17 |
| shared-substrate:shared-upstream | 18 |
| shared-substrate:transcription | 27 |
| structural | 1107 |
| **total** | **2142** |

- statements: 666
- statements with no independent leg: 172
- last run: 2026-08-25

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10504 |
| binary-field | 256 | 355 |
| binary-field-axioms | 256 | 355 |
| binary-field-group | 256 | 432 |
| binary-polynomial | 256 | 435 |
| binary-polynomial-division | 256 | 435 |
| binary-polynomial-gcd | 256 | 435 |
| clifford-motor | 512 | 272 |
| clifford-multivector | 512 | 272 |
| clifford-planar-complex | 512 | 272 |
| clifford-planar-dual | 512 | 272 |
| clifford-planar-split | 512 | 272 |
| clifford-quaternion-even | 512 | 272 |
| clifford-reverse | 512 | 272 |
| closed-unit | 512 | 506 |
| complex | 512 | 10505 |
| complex-direction | 512 | 400 |
| complex-divide | 512 | 495 |
| complex-rotate | 512 | 495 |
| contribution-fold-analog | 512 | 191 |
| contribution-fold-formula | 512 | 191 |
| contribution-fold-no-pool | 512 | 191 |
| contribution-fold-order | 512 | 191 |
| contribution-fold-quantization | 512 | 191 |
| directed-magnitude | 512 | 123 |
| directed-product | 512 | 123 |
| directed-product-sum | 512 | 123 |
| directed-quotient | 512 | 123 |
| directed-root | 512 | 123 |
| dual | 512 | 8290 |
| dual-divide | 512 | 400 |
| dual-generic | 512 | 400 |
| dual-quaternion | 512 | 495 |
| dynamics | 512 | 8 |
| extension-field | 256 | 421 |
| extension-field-inverse | 256 | 348 |
| extension-field-norm | 256 | 421 |
| extension-field-power | 256 | 348 |
| extension-field-product | 256 | 421 |
| mass-box | 256 | 123 |
| mass-capsule | 256 | 123 |
| mass-compound | 256 | 123 |
| mass-cylinder | 256 | 123 |
| mass-parallel-axis | 256 | 123 |
| mass-sphere | 256 | 123 |
| mass-volume | 256 | 123 |
| meet-associative | 512 | 182 |
| meet-bottom-absorption | 512 | 182 |
| meet-commutative | 512 | 182 |
| meet-idempotent | 512 | 182 |
| meet-monotonicity | 512 | 182 |
| meet-order-coherence | 512 | 182 |
| meet-product-composition | 512 | 182 |
| meet-top-identity | 512 | 182 |
| mixed-scale | 512 | 123 |
| mixed-scale-triple | 512 | 123 |
| mobius | 512 | 8290 |
| monogenic-exact | 512 | 272 |
| monogenic-fusion | 512 | 272 |
| position | 512 | 387 |
| position-delta | 512 | 478 |
| position-translate | 512 | 478 |
| presented | 512 | 776 |
| prime-field | 256 | 426 |
| prime-field-chain | 256 | 426 |
| prime-field-lucas | 256 | 426 |
| prime-field-primality | 256 | 426 |
| prime-field-root | 256 | 426 |
| q1648-scalar | 512 | 173 |
| q1648-scalar-division | 512 | 171 |
| q3232-scalar | 512 | 136 |
| q3232-scalar-division | 512 | 135 |
| quaternion | 512 | 495 |
| quaternion-direction | 512 | 400 |
| quaternion-rotate | 512 | 400 |
| quaternion-sublattice | 256 | 400 |
| rate | 512 | 479 |
| rigid | 512 | 518 |
| rigid-direction | 512 | 411 |
| rigid-point | 512 | 518 |
| scalar | 512 | 8294 |
| scalar-division | 512 | 513 |
| scalar-text | 512 | 513 |
| scalar-transcendental | 512 | 513 |
| smoke | 64 | 2020 |
| split | 512 | 10505 |
| split-divide | 512 | 495 |
| split-transform | 512 | 495 |
| sublattice | 256 | 4450 |
| symmetric-apply2 | 512 | 123 |
| symmetric-apply3 | 512 | 123 |
| symmetric-invert2 | 256 | 166 |
| symmetric-invert3 | 256 | 166 |
| symmetric-solve2 | 512 | 166 |
| symmetric-solve3 | 512 | 167 |
| unit-fraction16 | 512 | 420 |
| unit-fraction32 | 512 | 520 |
| unsigned-scalar | 512 | 506 |
| vector | 512 | 490 |
| vector-direction | 512 | 490 |
| vector-lattice | 512 | 396 |
| vector-narrow | 512 | 490 |
| vector-norm | 512 | 396 |
| vector-orthonormal-basis | 512 | 59 |

- last run: 2026-08-25
