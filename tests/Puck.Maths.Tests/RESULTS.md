# Puck.Maths.Tests — RESULTS

Machine-written by the assembly ledger at run end. Each block records the last run that exercised it — a tier
block only when that tier ran law cases, coverage only when the ratchet gate ran, the frontier only when a run
consumed domains AND every law it ran passed — so every other block keeps the text its own last run left. The
last-run dates are the only volatile content; they do not by themselves trigger a rewrite.

Every figure below is MACHINE-INDEPENDENT by construction: the same commit produces the same counts and the same
frontier indices on every machine, so a difference here is a real difference and never a difference of hardware.
No duration is recorded, deliberately. One here would carry no machine identity, would span the whole session
rather than the block it sits under, and would be taken without a busy-machine guard — so it could not answer
any question asked of it. Cost is measured outside the suite, by `puck bench`.

## Invocations

| tier | command |
| --- | --- |
| Default (Smoke+Default) | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release` |
| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings` |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings` |
| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/exhaustive.runsettings` |

## Smoke

- law cases executed: 22
- last run: 2026-10-01

## Default

- law cases executed: 636
- last run: 2026-10-01

## Deep

- law cases executed: 112
- last run: 2026-10-01

## Exhaustive

- law cases executed: 7
- last run: 2026-10-01

## Coverage

- covered: 2155
- waived: 43
- uncovered: 634
- total public members: 2832
- last run: 2026-10-01

## Legs

| leg kind | legs |
| --- | --- |
| classical | 838 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1200 |
| **total** | **2329** |

- statements: 777
- statements with no independent leg: 213
- last run: 2026-10-01

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10631 |
| binary-field | 256 | 464 |
| binary-field-axioms | 256 | 464 |
| binary-field-group | 256 | 559 |
| binary-polynomial | 256 | 562 |
| binary-polynomial-division | 256 | 562 |
| binary-polynomial-gcd | 256 | 562 |
| clifford-motor | 512 | 381 |
| clifford-multivector | 512 | 381 |
| clifford-planar-complex | 512 | 381 |
| clifford-planar-dual | 512 | 381 |
| clifford-planar-split | 512 | 381 |
| clifford-quaternion-even | 512 | 381 |
| clifford-reverse | 512 | 381 |
| closed-unit | 512 | 615 |
| complex | 512 | 10632 |
| complex-direction | 512 | 509 |
| complex-divide | 512 | 622 |
| complex-rotate | 512 | 622 |
| contribution-fold-analog | 512 | 300 |
| contribution-fold-formula | 512 | 300 |
| contribution-fold-no-pool | 512 | 300 |
| contribution-fold-order | 512 | 300 |
| contribution-fold-quantization | 512 | 300 |
| core-word-modular | 512 | 6 |
| cost-bound-arithmetic | 512 | 47 |
| cost-model-budgets | 512 | 47 |
| cost-model-conversions | 512 | 47 |
| directed-magnitude | 512 | 232 |
| directed-product | 512 | 232 |
| directed-product-sum | 512 | 232 |
| directed-quotient | 512 | 232 |
| directed-root | 512 | 232 |
| dual | 512 | 8399 |
| dual-divide | 512 | 509 |
| dual-generic | 512 | 509 |
| dual-quaternion | 512 | 622 |
| dynamics | 512 | 117 |
| extension-field | 256 | 548 |
| extension-field-inverse | 256 | 457 |
| extension-field-norm | 256 | 548 |
| extension-field-power | 256 | 457 |
| extension-field-product | 256 | 548 |
| fixed-saturate | 512 | 47 |
| integer-bit-align | 256 | 21 |
| integer-bit-morton | 128 | 21 |
| integer-bit-scatter | 128 | 21 |
| integer-bit-smear | 256 | 21 |
| integer-hexagonal-index | 512 | 86 |
| integer-magic-constants | 512 | 78 |
| integer-try-arithmetic | 512 | 11 |
| integer-try-multiplication | 512 | 6 |
| mass-box | 256 | 232 |
| mass-capsule | 256 | 232 |
| mass-compound | 256 | 232 |
| mass-cylinder | 256 | 232 |
| mass-parallel-axis | 256 | 232 |
| mass-sphere | 256 | 232 |
| mass-volume | 256 | 232 |
| meet-associative | 512 | 291 |
| meet-bottom-absorption | 512 | 291 |
| meet-commutative | 512 | 291 |
| meet-idempotent | 512 | 291 |
| meet-monotonicity | 512 | 291 |
| meet-order-coherence | 512 | 291 |
| meet-product-composition | 512 | 291 |
| meet-top-identity | 512 | 291 |
| mixed-scale | 512 | 232 |
| mixed-scale-triple | 512 | 232 |
| mobius | 512 | 8399 |
| monogenic-exact | 512 | 381 |
| monogenic-fusion | 512 | 381 |
| position | 512 | 496 |
| position-delta | 512 | 605 |
| position-translate | 512 | 605 |
| presented | 512 | 903 |
| prime-field | 256 | 553 |
| prime-field-chain | 256 | 553 |
| prime-field-lucas | 256 | 553 |
| prime-field-primality | 256 | 553 |
| prime-field-root | 256 | 553 |
| q1648-scalar | 512 | 282 |
| q1648-scalar-division | 512 | 280 |
| q3232-scalar | 512 | 245 |
| q3232-scalar-division | 512 | 244 |
| quaternion | 512 | 622 |
| quaternion-direction | 512 | 509 |
| quaternion-rotate | 512 | 509 |
| quaternion-sublattice | 256 | 509 |
| rate | 512 | 606 |
| rigid | 512 | 645 |
| rigid-direction | 512 | 520 |
| rigid-point | 512 | 645 |
| scalar | 512 | 8403 |
| scalar-division | 512 | 640 |
| scalar-text | 512 | 640 |
| scalar-transcendental | 512 | 642 |
| smoke | 64 | 2129 |
| split | 512 | 10632 |
| split-divide | 512 | 622 |
| split-transform | 512 | 622 |
| square-grid | 512 | 75 |
| sublattice | 256 | 4559 |
| symmetric-apply2 | 512 | 232 |
| symmetric-apply3 | 512 | 232 |
| symmetric-invert2 | 256 | 275 |
| symmetric-invert3 | 256 | 275 |
| symmetric-solve2 | 512 | 275 |
| symmetric-solve3 | 512 | 276 |
| unit-fraction16 | 512 | 529 |
| unit-fraction32 | 512 | 647 |
| unsigned-scalar | 512 | 633 |
| vector | 512 | 617 |
| vector-componentwise-helpers | 512 | 11 |
| vector-direction | 512 | 617 |
| vector-lattice | 512 | 505 |
| vector-narrow | 512 | 617 |
| vector-norm | 512 | 505 |
| vector-orthonormal-basis | 512 | 168 |
| vector-ray-plane | 512 | 9 |

- last run: 2026-10-01
