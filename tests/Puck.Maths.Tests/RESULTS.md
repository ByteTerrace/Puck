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

- covered: 2055
- waived: 40
- uncovered: 634
- total public members: 2729
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
| algebra-fractional | 512 | 10578 |
| binary-field | 256 | 417 |
| binary-field-axioms | 256 | 417 |
| binary-field-group | 256 | 506 |
| binary-polynomial | 256 | 509 |
| binary-polynomial-division | 256 | 509 |
| binary-polynomial-gcd | 256 | 509 |
| clifford-motor | 512 | 334 |
| clifford-multivector | 512 | 334 |
| clifford-planar-complex | 512 | 334 |
| clifford-planar-dual | 512 | 334 |
| clifford-planar-split | 512 | 334 |
| clifford-quaternion-even | 512 | 334 |
| clifford-reverse | 512 | 334 |
| closed-unit | 512 | 568 |
| complex | 512 | 10579 |
| complex-direction | 512 | 462 |
| complex-divide | 512 | 569 |
| complex-rotate | 512 | 569 |
| contribution-fold-analog | 512 | 253 |
| contribution-fold-formula | 512 | 253 |
| contribution-fold-no-pool | 512 | 253 |
| contribution-fold-order | 512 | 253 |
| contribution-fold-quantization | 512 | 253 |
| directed-magnitude | 512 | 185 |
| directed-product | 512 | 185 |
| directed-product-sum | 512 | 185 |
| directed-quotient | 512 | 185 |
| directed-root | 512 | 185 |
| dual | 512 | 8352 |
| dual-divide | 512 | 462 |
| dual-generic | 512 | 462 |
| dual-quaternion | 512 | 569 |
| dynamics | 512 | 70 |
| extension-field | 256 | 495 |
| extension-field-inverse | 256 | 410 |
| extension-field-norm | 256 | 495 |
| extension-field-power | 256 | 410 |
| extension-field-product | 256 | 495 |
| integer-hexagonal-index | 512 | 33 |
| integer-magic-constants | 512 | 31 |
| mass-box | 256 | 185 |
| mass-capsule | 256 | 185 |
| mass-compound | 256 | 185 |
| mass-cylinder | 256 | 185 |
| mass-parallel-axis | 256 | 185 |
| mass-sphere | 256 | 185 |
| mass-volume | 256 | 185 |
| meet-associative | 512 | 244 |
| meet-bottom-absorption | 512 | 244 |
| meet-commutative | 512 | 244 |
| meet-idempotent | 512 | 244 |
| meet-monotonicity | 512 | 244 |
| meet-order-coherence | 512 | 244 |
| meet-product-composition | 512 | 244 |
| meet-top-identity | 512 | 244 |
| mixed-scale | 512 | 185 |
| mixed-scale-triple | 512 | 185 |
| mobius | 512 | 8352 |
| monogenic-exact | 512 | 334 |
| monogenic-fusion | 512 | 334 |
| position | 512 | 449 |
| position-delta | 512 | 552 |
| position-translate | 512 | 552 |
| presented | 512 | 850 |
| prime-field | 256 | 500 |
| prime-field-chain | 256 | 500 |
| prime-field-lucas | 256 | 500 |
| prime-field-primality | 256 | 500 |
| prime-field-root | 256 | 500 |
| q1648-scalar | 512 | 235 |
| q1648-scalar-division | 512 | 233 |
| q3232-scalar | 512 | 198 |
| q3232-scalar-division | 512 | 197 |
| quaternion | 512 | 569 |
| quaternion-direction | 512 | 462 |
| quaternion-rotate | 512 | 462 |
| quaternion-sublattice | 256 | 462 |
| rate | 512 | 553 |
| rigid | 512 | 592 |
| rigid-direction | 512 | 473 |
| rigid-point | 512 | 592 |
| scalar | 512 | 8356 |
| scalar-division | 512 | 587 |
| scalar-text | 512 | 587 |
| scalar-transcendental | 512 | 589 |
| smoke | 64 | 2082 |
| split | 512 | 10579 |
| split-divide | 512 | 569 |
| split-transform | 512 | 569 |
| square-grid | 512 | 22 |
| sublattice | 256 | 4512 |
| symmetric-apply2 | 512 | 185 |
| symmetric-apply3 | 512 | 185 |
| symmetric-invert2 | 256 | 228 |
| symmetric-invert3 | 256 | 228 |
| symmetric-solve2 | 512 | 228 |
| symmetric-solve3 | 512 | 229 |
| unit-fraction16 | 512 | 482 |
| unit-fraction32 | 512 | 594 |
| unsigned-scalar | 512 | 580 |
| vector | 512 | 564 |
| vector-direction | 512 | 564 |
| vector-lattice | 512 | 458 |
| vector-narrow | 512 | 564 |
| vector-norm | 512 | 458 |
| vector-orthonormal-basis | 512 | 121 |

- last run: 2026-09-06
