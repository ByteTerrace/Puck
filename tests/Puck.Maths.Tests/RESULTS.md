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
| algebra-fractional | 512 | 10586 |
| binary-field | 256 | 425 |
| binary-field-axioms | 256 | 425 |
| binary-field-group | 256 | 514 |
| binary-polynomial | 256 | 517 |
| binary-polynomial-division | 256 | 517 |
| binary-polynomial-gcd | 256 | 517 |
| clifford-motor | 512 | 342 |
| clifford-multivector | 512 | 342 |
| clifford-planar-complex | 512 | 342 |
| clifford-planar-dual | 512 | 342 |
| clifford-planar-split | 512 | 342 |
| clifford-quaternion-even | 512 | 342 |
| clifford-reverse | 512 | 342 |
| closed-unit | 512 | 576 |
| complex | 512 | 10587 |
| complex-direction | 512 | 470 |
| complex-divide | 512 | 577 |
| complex-rotate | 512 | 577 |
| contribution-fold-analog | 512 | 261 |
| contribution-fold-formula | 512 | 261 |
| contribution-fold-no-pool | 512 | 261 |
| contribution-fold-order | 512 | 261 |
| contribution-fold-quantization | 512 | 261 |
| cost-bound-arithmetic | 512 | 8 |
| cost-model-budgets | 512 | 8 |
| cost-model-conversions | 512 | 8 |
| directed-magnitude | 512 | 193 |
| directed-product | 512 | 193 |
| directed-product-sum | 512 | 193 |
| directed-quotient | 512 | 193 |
| directed-root | 512 | 193 |
| dual | 512 | 8360 |
| dual-divide | 512 | 470 |
| dual-generic | 512 | 470 |
| dual-quaternion | 512 | 577 |
| dynamics | 512 | 78 |
| extension-field | 256 | 503 |
| extension-field-inverse | 256 | 418 |
| extension-field-norm | 256 | 503 |
| extension-field-power | 256 | 418 |
| extension-field-product | 256 | 503 |
| fixed-saturate | 512 | 8 |
| integer-hexagonal-index | 512 | 41 |
| integer-magic-constants | 512 | 39 |
| mass-box | 256 | 193 |
| mass-capsule | 256 | 193 |
| mass-compound | 256 | 193 |
| mass-cylinder | 256 | 193 |
| mass-parallel-axis | 256 | 193 |
| mass-sphere | 256 | 193 |
| mass-volume | 256 | 193 |
| meet-associative | 512 | 252 |
| meet-bottom-absorption | 512 | 252 |
| meet-commutative | 512 | 252 |
| meet-idempotent | 512 | 252 |
| meet-monotonicity | 512 | 252 |
| meet-order-coherence | 512 | 252 |
| meet-product-composition | 512 | 252 |
| meet-top-identity | 512 | 252 |
| mixed-scale | 512 | 193 |
| mixed-scale-triple | 512 | 193 |
| mobius | 512 | 8360 |
| monogenic-exact | 512 | 342 |
| monogenic-fusion | 512 | 342 |
| position | 512 | 457 |
| position-delta | 512 | 560 |
| position-translate | 512 | 560 |
| presented | 512 | 858 |
| prime-field | 256 | 508 |
| prime-field-chain | 256 | 508 |
| prime-field-lucas | 256 | 508 |
| prime-field-primality | 256 | 508 |
| prime-field-root | 256 | 508 |
| q1648-scalar | 512 | 243 |
| q1648-scalar-division | 512 | 241 |
| q3232-scalar | 512 | 206 |
| q3232-scalar-division | 512 | 205 |
| quaternion | 512 | 577 |
| quaternion-direction | 512 | 470 |
| quaternion-rotate | 512 | 470 |
| quaternion-sublattice | 256 | 470 |
| rate | 512 | 561 |
| rigid | 512 | 600 |
| rigid-direction | 512 | 481 |
| rigid-point | 512 | 600 |
| scalar | 512 | 8364 |
| scalar-division | 512 | 595 |
| scalar-text | 512 | 595 |
| scalar-transcendental | 512 | 597 |
| smoke | 64 | 2090 |
| split | 512 | 10587 |
| split-divide | 512 | 577 |
| split-transform | 512 | 577 |
| square-grid | 512 | 30 |
| sublattice | 256 | 4520 |
| symmetric-apply2 | 512 | 193 |
| symmetric-apply3 | 512 | 193 |
| symmetric-invert2 | 256 | 236 |
| symmetric-invert3 | 256 | 236 |
| symmetric-solve2 | 512 | 236 |
| symmetric-solve3 | 512 | 237 |
| unit-fraction16 | 512 | 490 |
| unit-fraction32 | 512 | 602 |
| unsigned-scalar | 512 | 588 |
| vector | 512 | 572 |
| vector-direction | 512 | 572 |
| vector-lattice | 512 | 466 |
| vector-narrow | 512 | 572 |
| vector-norm | 512 | 466 |
| vector-orthonormal-basis | 512 | 129 |

- last run: 2026-09-13
