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

- law cases executed: 642
- last run: 2026-10-02

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2157
- waived: 43
- uncovered: 634
- total public members: 2834
- last run: 2026-10-02

## Legs

| leg kind | legs |
| --- | --- |
| classical | 845 |
| in-tree-independent | 32 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1201 |
| **total** | **2338** |

- statements: 783
- statements with no independent leg: 213
- last run: 2026-10-02

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10636 |
| binary-field | 256 | 469 |
| binary-field-axioms | 256 | 469 |
| binary-field-group | 256 | 564 |
| binary-polynomial | 256 | 567 |
| binary-polynomial-division | 256 | 567 |
| binary-polynomial-gcd | 256 | 567 |
| clifford-motor | 512 | 386 |
| clifford-multivector | 512 | 386 |
| clifford-planar-complex | 512 | 386 |
| clifford-planar-dual | 512 | 386 |
| clifford-planar-split | 512 | 386 |
| clifford-quaternion-even | 512 | 386 |
| clifford-reverse | 512 | 386 |
| closed-unit | 512 | 620 |
| complex | 512 | 10637 |
| complex-direction | 512 | 514 |
| complex-divide | 512 | 627 |
| complex-rotate | 512 | 627 |
| contribution-fold-analog | 512 | 305 |
| contribution-fold-formula | 512 | 305 |
| contribution-fold-no-pool | 512 | 305 |
| contribution-fold-order | 512 | 305 |
| contribution-fold-quantization | 512 | 305 |
| core-word-modular | 512 | 11 |
| cost-bound-arithmetic | 512 | 52 |
| cost-model-budgets | 512 | 52 |
| cost-model-conversions | 512 | 52 |
| directed-magnitude | 512 | 237 |
| directed-product | 512 | 237 |
| directed-product-sum | 512 | 237 |
| directed-quotient | 512 | 237 |
| directed-root | 512 | 237 |
| dual | 512 | 8404 |
| dual-divide | 512 | 514 |
| dual-generic | 512 | 514 |
| dual-quaternion | 512 | 627 |
| dynamics | 512 | 122 |
| extension-field | 256 | 553 |
| extension-field-inverse | 256 | 462 |
| extension-field-norm | 256 | 553 |
| extension-field-power | 256 | 462 |
| extension-field-product | 256 | 553 |
| fixed-saturate | 512 | 52 |
| integer-bit-align | 256 | 26 |
| integer-bit-morton | 128 | 26 |
| integer-bit-scatter | 128 | 26 |
| integer-bit-smear | 256 | 26 |
| integer-hexagonal-index | 512 | 91 |
| integer-magic-constants | 512 | 83 |
| integer-try-arithmetic | 512 | 16 |
| integer-try-multiplication | 512 | 11 |
| mass-box | 256 | 237 |
| mass-capsule | 256 | 237 |
| mass-compound | 256 | 237 |
| mass-cylinder | 256 | 237 |
| mass-parallel-axis | 256 | 237 |
| mass-sphere | 256 | 237 |
| mass-volume | 256 | 237 |
| meet-associative | 512 | 296 |
| meet-bottom-absorption | 512 | 296 |
| meet-commutative | 512 | 296 |
| meet-idempotent | 512 | 296 |
| meet-monotonicity | 512 | 296 |
| meet-order-coherence | 512 | 296 |
| meet-product-composition | 512 | 296 |
| meet-top-identity | 512 | 296 |
| mixed-scale | 512 | 237 |
| mixed-scale-triple | 512 | 237 |
| mobius | 512 | 8404 |
| monogenic-exact | 512 | 386 |
| monogenic-fusion | 512 | 386 |
| position | 512 | 501 |
| position-delta | 512 | 610 |
| position-translate | 512 | 610 |
| presented | 512 | 908 |
| prime-field | 256 | 558 |
| prime-field-chain | 256 | 558 |
| prime-field-lucas | 256 | 558 |
| prime-field-primality | 256 | 558 |
| prime-field-root | 256 | 558 |
| q1648-scalar | 512 | 287 |
| q1648-scalar-division | 512 | 285 |
| q3232-scalar | 512 | 250 |
| q3232-scalar-division | 512 | 249 |
| quaternion | 512 | 627 |
| quaternion-antiparallel | 512 | 1 |
| quaternion-arc | 512 | 1 |
| quaternion-direction | 512 | 514 |
| quaternion-rotate | 512 | 514 |
| quaternion-sublattice | 256 | 514 |
| rate | 512 | 611 |
| rigid | 512 | 650 |
| rigid-direction | 512 | 525 |
| rigid-point | 512 | 650 |
| scalar | 512 | 8408 |
| scalar-division | 512 | 645 |
| scalar-smoothstep | 512 | 1 |
| scalar-text | 512 | 645 |
| scalar-transcendental | 512 | 647 |
| smoke | 64 | 2134 |
| split | 512 | 10637 |
| split-divide | 512 | 627 |
| split-transform | 512 | 627 |
| square-grid | 512 | 80 |
| sublattice | 256 | 4564 |
| symmetric-apply2 | 512 | 237 |
| symmetric-apply3 | 512 | 237 |
| symmetric-invert2 | 256 | 280 |
| symmetric-invert3 | 256 | 280 |
| symmetric-solve2 | 512 | 280 |
| symmetric-solve3 | 512 | 281 |
| unit-fraction16 | 512 | 534 |
| unit-fraction32 | 512 | 652 |
| unsigned-scalar | 512 | 638 |
| vector | 512 | 622 |
| vector-componentwise-helpers | 512 | 16 |
| vector-direction | 512 | 622 |
| vector-lattice | 512 | 510 |
| vector-narrow | 512 | 622 |
| vector-norm | 512 | 510 |
| vector-orthonormal-basis | 512 | 173 |
| vector-ray-plane | 512 | 14 |
| vector-within | 512 | 1 |

- last run: 2026-10-02
