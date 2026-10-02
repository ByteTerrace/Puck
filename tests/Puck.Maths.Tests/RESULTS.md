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

- law cases executed: 645
- last run: 2026-10-02

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2158
- waived: 43
- uncovered: 634
- total public members: 2835
- last run: 2026-10-02

## Legs

| leg kind | legs |
| --- | --- |
| classical | 848 |
| in-tree-independent | 33 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1201 |
| **total** | **2342** |

- statements: 786
- statements with no independent leg: 213
- last run: 2026-10-02

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10640 |
| binary-field | 256 | 473 |
| binary-field-axioms | 256 | 473 |
| binary-field-group | 256 | 568 |
| binary-polynomial | 256 | 571 |
| binary-polynomial-division | 256 | 571 |
| binary-polynomial-gcd | 256 | 571 |
| clifford-motor | 512 | 390 |
| clifford-multivector | 512 | 390 |
| clifford-planar-complex | 512 | 390 |
| clifford-planar-dual | 512 | 390 |
| clifford-planar-split | 512 | 390 |
| clifford-quaternion-even | 512 | 390 |
| clifford-reverse | 512 | 390 |
| closed-unit | 512 | 624 |
| complex | 512 | 10641 |
| complex-direction | 512 | 518 |
| complex-divide | 512 | 631 |
| complex-rotate | 512 | 631 |
| contribution-fold-analog | 512 | 309 |
| contribution-fold-formula | 512 | 309 |
| contribution-fold-no-pool | 512 | 309 |
| contribution-fold-order | 512 | 309 |
| contribution-fold-quantization | 512 | 309 |
| core-word-modular | 512 | 15 |
| cost-bound-arithmetic | 512 | 56 |
| cost-model-budgets | 512 | 56 |
| cost-model-conversions | 512 | 56 |
| directed-magnitude | 512 | 241 |
| directed-product | 512 | 241 |
| directed-product-sum | 512 | 241 |
| directed-quotient | 512 | 241 |
| directed-root | 512 | 241 |
| dual | 512 | 8408 |
| dual-divide | 512 | 518 |
| dual-generic | 512 | 518 |
| dual-quaternion | 512 | 631 |
| dynamics | 512 | 126 |
| extension-field | 256 | 557 |
| extension-field-inverse | 256 | 466 |
| extension-field-norm | 256 | 557 |
| extension-field-power | 256 | 466 |
| extension-field-product | 256 | 557 |
| fixed-saturate | 512 | 56 |
| integer-bit-align | 256 | 30 |
| integer-bit-morton | 128 | 30 |
| integer-bit-scatter | 128 | 30 |
| integer-bit-smear | 256 | 30 |
| integer-hexagonal-index | 512 | 95 |
| integer-magic-constants | 512 | 87 |
| integer-try-arithmetic | 512 | 20 |
| integer-try-multiplication | 512 | 15 |
| mass-box | 256 | 241 |
| mass-capsule | 256 | 241 |
| mass-compound | 256 | 241 |
| mass-cylinder | 256 | 241 |
| mass-parallel-axis | 256 | 241 |
| mass-sphere | 256 | 241 |
| mass-volume | 256 | 241 |
| meet-associative | 512 | 300 |
| meet-bottom-absorption | 512 | 300 |
| meet-commutative | 512 | 300 |
| meet-idempotent | 512 | 300 |
| meet-monotonicity | 512 | 300 |
| meet-order-coherence | 512 | 300 |
| meet-product-composition | 512 | 300 |
| meet-top-identity | 512 | 300 |
| mixed-scale | 512 | 241 |
| mixed-scale-triple | 512 | 241 |
| mobius | 512 | 8408 |
| monogenic-exact | 512 | 390 |
| monogenic-fusion | 512 | 390 |
| position | 512 | 505 |
| position-delta | 512 | 614 |
| position-translate | 512 | 614 |
| presented | 512 | 912 |
| prime-field | 256 | 562 |
| prime-field-chain | 256 | 562 |
| prime-field-lucas | 256 | 562 |
| prime-field-primality | 256 | 562 |
| prime-field-root | 256 | 562 |
| q1648-scalar | 512 | 291 |
| q1648-scalar-division | 512 | 289 |
| q3232-scalar | 512 | 254 |
| q3232-scalar-division | 512 | 253 |
| quaternion | 512 | 631 |
| quaternion-antiparallel | 512 | 5 |
| quaternion-arc | 512 | 5 |
| quaternion-direction | 512 | 518 |
| quaternion-rotate | 512 | 518 |
| quaternion-sublattice | 256 | 518 |
| rate | 512 | 615 |
| rigid | 512 | 654 |
| rigid-direction | 512 | 529 |
| rigid-exp-series | 512 | 3 |
| rigid-log-series | 512 | 3 |
| rigid-point | 512 | 654 |
| scalar | 512 | 8412 |
| scalar-division | 512 | 649 |
| scalar-smoothstep | 512 | 7 |
| scalar-text | 512 | 649 |
| scalar-transcendental | 512 | 651 |
| smoke | 64 | 2138 |
| split | 512 | 10641 |
| split-divide | 512 | 631 |
| split-transform | 512 | 631 |
| square-grid | 512 | 84 |
| sublattice | 256 | 4568 |
| symmetric-apply2 | 512 | 241 |
| symmetric-apply3 | 512 | 241 |
| symmetric-invert2 | 256 | 284 |
| symmetric-invert3 | 256 | 284 |
| symmetric-solve2 | 512 | 284 |
| symmetric-solve3 | 512 | 285 |
| unit-fraction16 | 512 | 538 |
| unit-fraction32 | 512 | 656 |
| unsigned-scalar | 512 | 642 |
| vector | 512 | 626 |
| vector-compare-length | 512 | 1 |
| vector-componentwise-helpers | 512 | 20 |
| vector-direction | 512 | 626 |
| vector-lattice | 512 | 514 |
| vector-narrow | 512 | 626 |
| vector-norm | 512 | 514 |
| vector-orthonormal-basis | 512 | 177 |
| vector-ray-plane | 512 | 18 |
| vector-within | 512 | 6 |

- last run: 2026-10-02
