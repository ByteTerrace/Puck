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

- law cases executed: 603
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

- covered: 2075
- waived: 43
- uncovered: 634
- total public members: 2752
- last run: 2026-09-12

## Legs

| leg kind | legs |
| --- | --- |
| classical | 802 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1180 |
| **total** | **2273** |

- statements: 744
- statements with no independent leg: 208
- last run: 2026-09-12

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10579 |
| binary-field | 256 | 418 |
| binary-field-axioms | 256 | 418 |
| binary-field-group | 256 | 507 |
| binary-polynomial | 256 | 510 |
| binary-polynomial-division | 256 | 510 |
| binary-polynomial-gcd | 256 | 510 |
| clifford-motor | 512 | 335 |
| clifford-multivector | 512 | 335 |
| clifford-planar-complex | 512 | 335 |
| clifford-planar-dual | 512 | 335 |
| clifford-planar-split | 512 | 335 |
| clifford-quaternion-even | 512 | 335 |
| clifford-reverse | 512 | 335 |
| closed-unit | 512 | 569 |
| complex | 512 | 10580 |
| complex-direction | 512 | 463 |
| complex-divide | 512 | 570 |
| complex-rotate | 512 | 570 |
| contribution-fold-analog | 512 | 254 |
| contribution-fold-formula | 512 | 254 |
| contribution-fold-no-pool | 512 | 254 |
| contribution-fold-order | 512 | 254 |
| contribution-fold-quantization | 512 | 254 |
| directed-magnitude | 512 | 186 |
| directed-product | 512 | 186 |
| directed-product-sum | 512 | 186 |
| directed-quotient | 512 | 186 |
| directed-root | 512 | 186 |
| dual | 512 | 8353 |
| dual-divide | 512 | 463 |
| dual-generic | 512 | 463 |
| dual-quaternion | 512 | 570 |
| dynamics | 512 | 71 |
| extension-field | 256 | 496 |
| extension-field-inverse | 256 | 411 |
| extension-field-norm | 256 | 496 |
| extension-field-power | 256 | 411 |
| extension-field-product | 256 | 496 |
| fixed-saturate | 512 | 1 |
| integer-hexagonal-index | 512 | 34 |
| integer-magic-constants | 512 | 32 |
| mass-box | 256 | 186 |
| mass-capsule | 256 | 186 |
| mass-compound | 256 | 186 |
| mass-cylinder | 256 | 186 |
| mass-parallel-axis | 256 | 186 |
| mass-sphere | 256 | 186 |
| mass-volume | 256 | 186 |
| meet-associative | 512 | 245 |
| meet-bottom-absorption | 512 | 245 |
| meet-commutative | 512 | 245 |
| meet-idempotent | 512 | 245 |
| meet-monotonicity | 512 | 245 |
| meet-order-coherence | 512 | 245 |
| meet-product-composition | 512 | 245 |
| meet-top-identity | 512 | 245 |
| mixed-scale | 512 | 186 |
| mixed-scale-triple | 512 | 186 |
| mobius | 512 | 8353 |
| monogenic-exact | 512 | 335 |
| monogenic-fusion | 512 | 335 |
| position | 512 | 450 |
| position-delta | 512 | 553 |
| position-translate | 512 | 553 |
| presented | 512 | 851 |
| prime-field | 256 | 501 |
| prime-field-chain | 256 | 501 |
| prime-field-lucas | 256 | 501 |
| prime-field-primality | 256 | 501 |
| prime-field-root | 256 | 501 |
| q1648-scalar | 512 | 236 |
| q1648-scalar-division | 512 | 234 |
| q3232-scalar | 512 | 199 |
| q3232-scalar-division | 512 | 198 |
| quaternion | 512 | 570 |
| quaternion-direction | 512 | 463 |
| quaternion-rotate | 512 | 463 |
| quaternion-sublattice | 256 | 463 |
| rate | 512 | 554 |
| rigid | 512 | 593 |
| rigid-direction | 512 | 474 |
| rigid-point | 512 | 593 |
| scalar | 512 | 8357 |
| scalar-division | 512 | 588 |
| scalar-text | 512 | 588 |
| scalar-transcendental | 512 | 590 |
| smoke | 64 | 2083 |
| split | 512 | 10580 |
| split-divide | 512 | 570 |
| split-transform | 512 | 570 |
| square-grid | 512 | 23 |
| sublattice | 256 | 4513 |
| symmetric-apply2 | 512 | 186 |
| symmetric-apply3 | 512 | 186 |
| symmetric-invert2 | 256 | 229 |
| symmetric-invert3 | 256 | 229 |
| symmetric-solve2 | 512 | 229 |
| symmetric-solve3 | 512 | 230 |
| unit-fraction16 | 512 | 483 |
| unit-fraction32 | 512 | 595 |
| unsigned-scalar | 512 | 581 |
| vector | 512 | 565 |
| vector-direction | 512 | 565 |
| vector-lattice | 512 | 459 |
| vector-narrow | 512 | 565 |
| vector-norm | 512 | 459 |
| vector-orthonormal-basis | 512 | 122 |

- last run: 2026-09-12
