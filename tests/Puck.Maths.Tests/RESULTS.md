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
| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --filter-trait tier=Smoke` |
| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --explicit on --filter-trait tier=Deep` |
| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --explicit on --filter-trait tier=Exhaustive` |

## Smoke

- law cases executed: 22
- last run: 2026-10-03

## Default

- law cases executed: 653
- last run: 2026-10-03

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2191
- waived: 55
- uncovered: 634
- total public members: 2880
- last run: 2026-10-03

## Legs

| leg kind | legs |
| --- | --- |
| classical | 854 |
| in-tree-independent | 33 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1205 |
| **total** | **2352** |

- statements: 794
- statements with no independent leg: 216
- last run: 2026-10-03

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10644 |
| binary-field | 256 | 477 |
| binary-field-axioms | 256 | 477 |
| binary-field-group | 256 | 572 |
| binary-polynomial | 256 | 575 |
| binary-polynomial-division | 256 | 575 |
| binary-polynomial-gcd | 256 | 575 |
| clifford-motor | 512 | 394 |
| clifford-multivector | 512 | 394 |
| clifford-planar-complex | 512 | 394 |
| clifford-planar-dual | 512 | 394 |
| clifford-planar-split | 512 | 394 |
| clifford-quaternion-even | 512 | 394 |
| clifford-reverse | 512 | 394 |
| closed-unit | 512 | 628 |
| complex | 512 | 10645 |
| complex-direction | 512 | 522 |
| complex-divide | 512 | 635 |
| complex-rotate | 512 | 635 |
| contribution-fold-analog | 512 | 313 |
| contribution-fold-formula | 512 | 313 |
| contribution-fold-no-pool | 512 | 313 |
| contribution-fold-order | 512 | 313 |
| contribution-fold-quantization | 512 | 313 |
| core-word-modular | 512 | 19 |
| cost-bound-arithmetic | 512 | 60 |
| cost-model-budgets | 512 | 60 |
| cost-model-conversions | 512 | 60 |
| directed-magnitude | 512 | 245 |
| directed-product | 512 | 245 |
| directed-product-sum | 512 | 245 |
| directed-quotient | 512 | 245 |
| directed-root | 512 | 245 |
| dual | 512 | 8412 |
| dual-divide | 512 | 522 |
| dual-generic | 512 | 522 |
| dual-quaternion | 512 | 635 |
| dynamics | 512 | 130 |
| extension-field | 256 | 561 |
| extension-field-inverse | 256 | 470 |
| extension-field-norm | 256 | 561 |
| extension-field-power | 256 | 470 |
| extension-field-product | 256 | 561 |
| fixed-saturate | 512 | 60 |
| integer-bit-align | 256 | 34 |
| integer-bit-morton | 128 | 34 |
| integer-bit-scatter | 128 | 34 |
| integer-bit-smear | 256 | 34 |
| integer-hexagonal-index | 512 | 99 |
| integer-magic-constants | 512 | 91 |
| integer-try-arithmetic | 512 | 24 |
| integer-try-multiplication | 512 | 19 |
| interval-arc-functions | 512 | 4 |
| interval-arctangent | 512 | 4 |
| interval-arithmetic | 512 | 4 |
| interval-circular | 512 | 4 |
| interval-isotonic | 512 | 4 |
| interval-power | 512 | 4 |
| mass-box | 256 | 245 |
| mass-capsule | 256 | 245 |
| mass-compound | 256 | 245 |
| mass-cylinder | 256 | 245 |
| mass-parallel-axis | 256 | 245 |
| mass-sphere | 256 | 245 |
| mass-volume | 256 | 245 |
| meet-associative | 512 | 304 |
| meet-bottom-absorption | 512 | 304 |
| meet-commutative | 512 | 304 |
| meet-idempotent | 512 | 304 |
| meet-monotonicity | 512 | 304 |
| meet-order-coherence | 512 | 304 |
| meet-product-composition | 512 | 304 |
| meet-top-identity | 512 | 304 |
| mixed-scale | 512 | 245 |
| mixed-scale-triple | 512 | 245 |
| mobius | 512 | 8412 |
| monogenic-exact | 512 | 394 |
| monogenic-fusion | 512 | 394 |
| position | 512 | 509 |
| position-delta | 512 | 618 |
| position-translate | 512 | 618 |
| presented | 512 | 916 |
| prime-field | 256 | 566 |
| prime-field-chain | 256 | 566 |
| prime-field-lucas | 256 | 566 |
| prime-field-primality | 256 | 566 |
| prime-field-root | 256 | 566 |
| q1648-scalar | 512 | 295 |
| q1648-scalar-division | 512 | 293 |
| q3232-scalar | 512 | 258 |
| q3232-scalar-division | 512 | 257 |
| quaternion | 512 | 635 |
| quaternion-antiparallel | 512 | 9 |
| quaternion-arc | 512 | 9 |
| quaternion-direction | 512 | 522 |
| quaternion-rotate | 512 | 522 |
| quaternion-sublattice | 256 | 522 |
| rate | 512 | 619 |
| rigid | 512 | 658 |
| rigid-direction | 512 | 533 |
| rigid-exp-series | 512 | 7 |
| rigid-log-series | 512 | 7 |
| rigid-point | 512 | 658 |
| scalar | 512 | 8416 |
| scalar-division | 512 | 653 |
| scalar-smoothstep | 512 | 11 |
| scalar-text | 512 | 653 |
| scalar-transcendental | 512 | 655 |
| smoke | 64 | 2142 |
| split | 512 | 10645 |
| split-divide | 512 | 635 |
| split-transform | 512 | 635 |
| square-grid | 512 | 88 |
| sublattice | 256 | 4572 |
| symmetric-apply2 | 512 | 245 |
| symmetric-apply3 | 512 | 245 |
| symmetric-invert2 | 256 | 288 |
| symmetric-invert3 | 256 | 288 |
| symmetric-solve2 | 512 | 288 |
| symmetric-solve3 | 512 | 289 |
| unit-fraction16 | 512 | 542 |
| unit-fraction32 | 512 | 660 |
| unsigned-scalar | 512 | 646 |
| vector | 512 | 630 |
| vector-compare-length | 512 | 5 |
| vector-componentwise-helpers | 512 | 24 |
| vector-direction | 512 | 630 |
| vector-lattice | 512 | 518 |
| vector-narrow | 512 | 630 |
| vector-norm | 512 | 518 |
| vector-orthonormal-basis | 512 | 181 |
| vector-ray-plane | 512 | 22 |
| vector-within | 512 | 10 |

- last run: 2026-10-03
