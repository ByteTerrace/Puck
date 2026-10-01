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

- law cases executed: 634
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
- last run: 2026-10-01

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10627 |
| binary-field | 256 | 460 |
| binary-field-axioms | 256 | 460 |
| binary-field-group | 256 | 555 |
| binary-polynomial | 256 | 558 |
| binary-polynomial-division | 256 | 558 |
| binary-polynomial-gcd | 256 | 558 |
| clifford-motor | 512 | 377 |
| clifford-multivector | 512 | 377 |
| clifford-planar-complex | 512 | 377 |
| clifford-planar-dual | 512 | 377 |
| clifford-planar-split | 512 | 377 |
| clifford-quaternion-even | 512 | 377 |
| clifford-reverse | 512 | 377 |
| closed-unit | 512 | 611 |
| complex | 512 | 10628 |
| complex-direction | 512 | 505 |
| complex-divide | 512 | 618 |
| complex-rotate | 512 | 618 |
| contribution-fold-analog | 512 | 296 |
| contribution-fold-formula | 512 | 296 |
| contribution-fold-no-pool | 512 | 296 |
| contribution-fold-order | 512 | 296 |
| contribution-fold-quantization | 512 | 296 |
| core-word-modular | 512 | 2 |
| cost-bound-arithmetic | 512 | 43 |
| cost-model-budgets | 512 | 43 |
| cost-model-conversions | 512 | 43 |
| directed-magnitude | 512 | 228 |
| directed-product | 512 | 228 |
| directed-product-sum | 512 | 228 |
| directed-quotient | 512 | 228 |
| directed-root | 512 | 228 |
| dual | 512 | 8395 |
| dual-divide | 512 | 505 |
| dual-generic | 512 | 505 |
| dual-quaternion | 512 | 618 |
| dynamics | 512 | 113 |
| extension-field | 256 | 544 |
| extension-field-inverse | 256 | 453 |
| extension-field-norm | 256 | 544 |
| extension-field-power | 256 | 453 |
| extension-field-product | 256 | 544 |
| fixed-saturate | 512 | 43 |
| integer-bit-align | 256 | 17 |
| integer-bit-morton | 128 | 17 |
| integer-bit-scatter | 128 | 17 |
| integer-bit-smear | 256 | 17 |
| integer-hexagonal-index | 512 | 82 |
| integer-magic-constants | 512 | 74 |
| integer-try-arithmetic | 512 | 7 |
| integer-try-multiplication | 512 | 2 |
| mass-box | 256 | 228 |
| mass-capsule | 256 | 228 |
| mass-compound | 256 | 228 |
| mass-cylinder | 256 | 228 |
| mass-parallel-axis | 256 | 228 |
| mass-sphere | 256 | 228 |
| mass-volume | 256 | 228 |
| meet-associative | 512 | 287 |
| meet-bottom-absorption | 512 | 287 |
| meet-commutative | 512 | 287 |
| meet-idempotent | 512 | 287 |
| meet-monotonicity | 512 | 287 |
| meet-order-coherence | 512 | 287 |
| meet-product-composition | 512 | 287 |
| meet-top-identity | 512 | 287 |
| mixed-scale | 512 | 228 |
| mixed-scale-triple | 512 | 228 |
| mobius | 512 | 8395 |
| monogenic-exact | 512 | 377 |
| monogenic-fusion | 512 | 377 |
| position | 512 | 492 |
| position-delta | 512 | 601 |
| position-translate | 512 | 601 |
| presented | 512 | 899 |
| prime-field | 256 | 549 |
| prime-field-chain | 256 | 549 |
| prime-field-lucas | 256 | 549 |
| prime-field-primality | 256 | 549 |
| prime-field-root | 256 | 549 |
| q1648-scalar | 512 | 278 |
| q1648-scalar-division | 512 | 276 |
| q3232-scalar | 512 | 241 |
| q3232-scalar-division | 512 | 240 |
| quaternion | 512 | 618 |
| quaternion-direction | 512 | 505 |
| quaternion-rotate | 512 | 505 |
| quaternion-sublattice | 256 | 505 |
| rate | 512 | 602 |
| rigid | 512 | 641 |
| rigid-direction | 512 | 516 |
| rigid-point | 512 | 641 |
| scalar | 512 | 8399 |
| scalar-division | 512 | 636 |
| scalar-text | 512 | 636 |
| scalar-transcendental | 512 | 638 |
| smoke | 64 | 2125 |
| split | 512 | 10628 |
| split-divide | 512 | 618 |
| split-transform | 512 | 618 |
| square-grid | 512 | 71 |
| sublattice | 256 | 4555 |
| symmetric-apply2 | 512 | 228 |
| symmetric-apply3 | 512 | 228 |
| symmetric-invert2 | 256 | 271 |
| symmetric-invert3 | 256 | 271 |
| symmetric-solve2 | 512 | 271 |
| symmetric-solve3 | 512 | 272 |
| unit-fraction16 | 512 | 525 |
| unit-fraction32 | 512 | 643 |
| unsigned-scalar | 512 | 629 |
| vector | 512 | 613 |
| vector-componentwise-helpers | 512 | 7 |
| vector-direction | 512 | 613 |
| vector-lattice | 512 | 501 |
| vector-narrow | 512 | 613 |
| vector-norm | 512 | 501 |
| vector-orthonormal-basis | 512 | 164 |
| vector-ray-plane | 512 | 5 |

- last run: 2026-10-01
