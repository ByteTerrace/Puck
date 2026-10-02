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
- last run: 2026-10-02

## Default

- law cases executed: 643
- last run: 2026-10-02

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2186
- waived: 43
- uncovered: 634
- total public members: 2863
- last run: 2026-10-02

## Legs

| leg kind | legs |
| --- | --- |
| classical | 845 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1202 |
| **total** | **2338** |

- statements: 784
- statements with no independent leg: 215
- last run: 2026-10-02

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10635 |
| binary-field | 256 | 468 |
| binary-field-axioms | 256 | 468 |
| binary-field-group | 256 | 563 |
| binary-polynomial | 256 | 566 |
| binary-polynomial-division | 256 | 566 |
| binary-polynomial-gcd | 256 | 566 |
| clifford-motor | 512 | 385 |
| clifford-multivector | 512 | 385 |
| clifford-planar-complex | 512 | 385 |
| clifford-planar-dual | 512 | 385 |
| clifford-planar-split | 512 | 385 |
| clifford-quaternion-even | 512 | 385 |
| clifford-reverse | 512 | 385 |
| closed-unit | 512 | 619 |
| complex | 512 | 10636 |
| complex-direction | 512 | 513 |
| complex-divide | 512 | 626 |
| complex-rotate | 512 | 626 |
| contribution-fold-analog | 512 | 304 |
| contribution-fold-formula | 512 | 304 |
| contribution-fold-no-pool | 512 | 304 |
| contribution-fold-order | 512 | 304 |
| contribution-fold-quantization | 512 | 304 |
| core-word-modular | 512 | 10 |
| cost-bound-arithmetic | 512 | 51 |
| cost-model-budgets | 512 | 51 |
| cost-model-conversions | 512 | 51 |
| directed-magnitude | 512 | 236 |
| directed-product | 512 | 236 |
| directed-product-sum | 512 | 236 |
| directed-quotient | 512 | 236 |
| directed-root | 512 | 236 |
| dual | 512 | 8403 |
| dual-divide | 512 | 513 |
| dual-generic | 512 | 513 |
| dual-quaternion | 512 | 626 |
| dynamics | 512 | 121 |
| extension-field | 256 | 552 |
| extension-field-inverse | 256 | 461 |
| extension-field-norm | 256 | 552 |
| extension-field-power | 256 | 461 |
| extension-field-product | 256 | 552 |
| fixed-saturate | 512 | 51 |
| integer-bit-align | 256 | 25 |
| integer-bit-morton | 128 | 25 |
| integer-bit-scatter | 128 | 25 |
| integer-bit-smear | 256 | 25 |
| integer-hexagonal-index | 512 | 90 |
| integer-magic-constants | 512 | 82 |
| integer-try-arithmetic | 512 | 15 |
| integer-try-multiplication | 512 | 10 |
| interval-arc-functions | 512 | 2 |
| interval-arctangent | 512 | 2 |
| interval-arithmetic | 512 | 2 |
| interval-circular | 512 | 2 |
| interval-isotonic | 512 | 2 |
| mass-box | 256 | 236 |
| mass-capsule | 256 | 236 |
| mass-compound | 256 | 236 |
| mass-cylinder | 256 | 236 |
| mass-parallel-axis | 256 | 236 |
| mass-sphere | 256 | 236 |
| mass-volume | 256 | 236 |
| meet-associative | 512 | 295 |
| meet-bottom-absorption | 512 | 295 |
| meet-commutative | 512 | 295 |
| meet-idempotent | 512 | 295 |
| meet-monotonicity | 512 | 295 |
| meet-order-coherence | 512 | 295 |
| meet-product-composition | 512 | 295 |
| meet-top-identity | 512 | 295 |
| mixed-scale | 512 | 236 |
| mixed-scale-triple | 512 | 236 |
| mobius | 512 | 8403 |
| monogenic-exact | 512 | 385 |
| monogenic-fusion | 512 | 385 |
| position | 512 | 500 |
| position-delta | 512 | 609 |
| position-translate | 512 | 609 |
| presented | 512 | 907 |
| prime-field | 256 | 557 |
| prime-field-chain | 256 | 557 |
| prime-field-lucas | 256 | 557 |
| prime-field-primality | 256 | 557 |
| prime-field-root | 256 | 557 |
| q1648-scalar | 512 | 286 |
| q1648-scalar-division | 512 | 284 |
| q3232-scalar | 512 | 249 |
| q3232-scalar-division | 512 | 248 |
| quaternion | 512 | 626 |
| quaternion-direction | 512 | 513 |
| quaternion-rotate | 512 | 513 |
| quaternion-sublattice | 256 | 513 |
| rate | 512 | 610 |
| rigid | 512 | 649 |
| rigid-direction | 512 | 524 |
| rigid-point | 512 | 649 |
| scalar | 512 | 8407 |
| scalar-division | 512 | 644 |
| scalar-text | 512 | 644 |
| scalar-transcendental | 512 | 646 |
| smoke | 64 | 2133 |
| split | 512 | 10636 |
| split-divide | 512 | 626 |
| split-transform | 512 | 626 |
| square-grid | 512 | 79 |
| sublattice | 256 | 4563 |
| symmetric-apply2 | 512 | 236 |
| symmetric-apply3 | 512 | 236 |
| symmetric-invert2 | 256 | 279 |
| symmetric-invert3 | 256 | 279 |
| symmetric-solve2 | 512 | 279 |
| symmetric-solve3 | 512 | 280 |
| unit-fraction16 | 512 | 533 |
| unit-fraction32 | 512 | 651 |
| unsigned-scalar | 512 | 637 |
| vector | 512 | 621 |
| vector-componentwise-helpers | 512 | 15 |
| vector-direction | 512 | 621 |
| vector-lattice | 512 | 509 |
| vector-narrow | 512 | 621 |
| vector-norm | 512 | 509 |
| vector-orthonormal-basis | 512 | 172 |
| vector-ray-plane | 512 | 13 |

- last run: 2026-10-02
