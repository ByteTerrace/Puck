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
- last run: 2026-09-27

## Default

- law cases executed: 634
- last run: 2026-09-27

## Deep

- law cases executed: 112
- last run: 2026-09-27

## Exhaustive

- law cases executed: 7
- last run: 2026-09-27

## Coverage

- covered: 2155
- waived: 43
- uncovered: 634
- total public members: 2832
- last run: 2026-09-27

## Legs

| leg kind | legs |
| --- | --- |
| classical | 836 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1199 |
| **total** | **2326** |

- statements: 775
- statements with no independent leg: 213
- last run: 2026-09-27

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10626 |
| binary-field | 256 | 459 |
| binary-field-axioms | 256 | 459 |
| binary-field-group | 256 | 554 |
| binary-polynomial | 256 | 557 |
| binary-polynomial-division | 256 | 557 |
| binary-polynomial-gcd | 256 | 557 |
| clifford-motor | 512 | 376 |
| clifford-multivector | 512 | 376 |
| clifford-planar-complex | 512 | 376 |
| clifford-planar-dual | 512 | 376 |
| clifford-planar-split | 512 | 376 |
| clifford-quaternion-even | 512 | 376 |
| clifford-reverse | 512 | 376 |
| closed-unit | 512 | 610 |
| complex | 512 | 10627 |
| complex-direction | 512 | 504 |
| complex-divide | 512 | 617 |
| complex-rotate | 512 | 617 |
| contribution-fold-analog | 512 | 295 |
| contribution-fold-formula | 512 | 295 |
| contribution-fold-no-pool | 512 | 295 |
| contribution-fold-order | 512 | 295 |
| contribution-fold-quantization | 512 | 295 |
| core-word-modular | 512 | 1 |
| cost-bound-arithmetic | 512 | 42 |
| cost-model-budgets | 512 | 42 |
| cost-model-conversions | 512 | 42 |
| directed-magnitude | 512 | 227 |
| directed-product | 512 | 227 |
| directed-product-sum | 512 | 227 |
| directed-quotient | 512 | 227 |
| directed-root | 512 | 227 |
| dual | 512 | 8394 |
| dual-divide | 512 | 504 |
| dual-generic | 512 | 504 |
| dual-quaternion | 512 | 617 |
| dynamics | 512 | 112 |
| extension-field | 256 | 543 |
| extension-field-inverse | 256 | 452 |
| extension-field-norm | 256 | 543 |
| extension-field-power | 256 | 452 |
| extension-field-product | 256 | 543 |
| fixed-saturate | 512 | 42 |
| integer-bit-align | 256 | 16 |
| integer-bit-morton | 128 | 16 |
| integer-bit-scatter | 128 | 16 |
| integer-bit-smear | 256 | 16 |
| integer-hexagonal-index | 512 | 81 |
| integer-magic-constants | 512 | 73 |
| integer-try-arithmetic | 512 | 6 |
| integer-try-multiplication | 512 | 1 |
| mass-box | 256 | 227 |
| mass-capsule | 256 | 227 |
| mass-compound | 256 | 227 |
| mass-cylinder | 256 | 227 |
| mass-parallel-axis | 256 | 227 |
| mass-sphere | 256 | 227 |
| mass-volume | 256 | 227 |
| meet-associative | 512 | 286 |
| meet-bottom-absorption | 512 | 286 |
| meet-commutative | 512 | 286 |
| meet-idempotent | 512 | 286 |
| meet-monotonicity | 512 | 286 |
| meet-order-coherence | 512 | 286 |
| meet-product-composition | 512 | 286 |
| meet-top-identity | 512 | 286 |
| mixed-scale | 512 | 227 |
| mixed-scale-triple | 512 | 227 |
| mobius | 512 | 8394 |
| monogenic-exact | 512 | 376 |
| monogenic-fusion | 512 | 376 |
| position | 512 | 491 |
| position-delta | 512 | 600 |
| position-translate | 512 | 600 |
| presented | 512 | 898 |
| prime-field | 256 | 548 |
| prime-field-chain | 256 | 548 |
| prime-field-lucas | 256 | 548 |
| prime-field-primality | 256 | 548 |
| prime-field-root | 256 | 548 |
| q1648-scalar | 512 | 277 |
| q1648-scalar-division | 512 | 275 |
| q3232-scalar | 512 | 240 |
| q3232-scalar-division | 512 | 239 |
| quaternion | 512 | 617 |
| quaternion-direction | 512 | 504 |
| quaternion-rotate | 512 | 504 |
| quaternion-sublattice | 256 | 504 |
| rate | 512 | 601 |
| rigid | 512 | 640 |
| rigid-direction | 512 | 515 |
| rigid-point | 512 | 640 |
| scalar | 512 | 8398 |
| scalar-division | 512 | 635 |
| scalar-text | 512 | 635 |
| scalar-transcendental | 512 | 637 |
| smoke | 64 | 2124 |
| split | 512 | 10627 |
| split-divide | 512 | 617 |
| split-transform | 512 | 617 |
| square-grid | 512 | 70 |
| sublattice | 256 | 4554 |
| symmetric-apply2 | 512 | 227 |
| symmetric-apply3 | 512 | 227 |
| symmetric-invert2 | 256 | 270 |
| symmetric-invert3 | 256 | 270 |
| symmetric-solve2 | 512 | 270 |
| symmetric-solve3 | 512 | 271 |
| unit-fraction16 | 512 | 524 |
| unit-fraction32 | 512 | 642 |
| unsigned-scalar | 512 | 628 |
| vector | 512 | 612 |
| vector-componentwise-helpers | 512 | 6 |
| vector-direction | 512 | 612 |
| vector-lattice | 512 | 500 |
| vector-narrow | 512 | 612 |
| vector-norm | 512 | 500 |
| vector-orthonormal-basis | 512 | 163 |
| vector-ray-plane | 512 | 4 |

- last run: 2026-09-27
