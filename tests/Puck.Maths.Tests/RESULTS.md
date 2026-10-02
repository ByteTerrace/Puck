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

- law cases executed: 644
- last run: 2026-10-02

## Deep

- law cases executed: 112
- last run: 2026-10-02

## Exhaustive

- law cases executed: 7
- last run: 2026-10-02

## Coverage

- covered: 2186
- waived: 55
- uncovered: 634
- total public members: 2875
- last run: 2026-10-02

## Legs

| leg kind | legs |
| --- | --- |
| classical | 846 |
| in-tree-independent | 31 |
| presented-twin | 9 |
| relative-canary | 18 |
| shared-substrate:delegation-twin | 46 |
| shared-substrate:fused-substrate | 36 |
| shared-substrate:intra-presented | 82 |
| shared-substrate:shared-exact-kernel | 19 |
| shared-substrate:shared-upstream | 22 |
| shared-substrate:transcription | 28 |
| structural | 1203 |
| **total** | **2340** |

- statements: 785
- statements with no independent leg: 215
- last run: 2026-10-02

## Frontier

| domain | block | index |
| --- | --- | --- |
| algebra-fractional | 512 | 10638 |
| binary-field | 256 | 471 |
| binary-field-axioms | 256 | 471 |
| binary-field-group | 256 | 566 |
| binary-polynomial | 256 | 569 |
| binary-polynomial-division | 256 | 569 |
| binary-polynomial-gcd | 256 | 569 |
| clifford-motor | 512 | 388 |
| clifford-multivector | 512 | 388 |
| clifford-planar-complex | 512 | 388 |
| clifford-planar-dual | 512 | 388 |
| clifford-planar-split | 512 | 388 |
| clifford-quaternion-even | 512 | 388 |
| clifford-reverse | 512 | 388 |
| closed-unit | 512 | 622 |
| complex | 512 | 10639 |
| complex-direction | 512 | 516 |
| complex-divide | 512 | 629 |
| complex-rotate | 512 | 629 |
| contribution-fold-analog | 512 | 307 |
| contribution-fold-formula | 512 | 307 |
| contribution-fold-no-pool | 512 | 307 |
| contribution-fold-order | 512 | 307 |
| contribution-fold-quantization | 512 | 307 |
| core-word-modular | 512 | 13 |
| cost-bound-arithmetic | 512 | 54 |
| cost-model-budgets | 512 | 54 |
| cost-model-conversions | 512 | 54 |
| directed-magnitude | 512 | 239 |
| directed-product | 512 | 239 |
| directed-product-sum | 512 | 239 |
| directed-quotient | 512 | 239 |
| directed-root | 512 | 239 |
| dual | 512 | 8406 |
| dual-divide | 512 | 516 |
| dual-generic | 512 | 516 |
| dual-quaternion | 512 | 629 |
| dynamics | 512 | 124 |
| extension-field | 256 | 555 |
| extension-field-inverse | 256 | 464 |
| extension-field-norm | 256 | 555 |
| extension-field-power | 256 | 464 |
| extension-field-product | 256 | 555 |
| fixed-saturate | 512 | 54 |
| integer-bit-align | 256 | 28 |
| integer-bit-morton | 128 | 28 |
| integer-bit-scatter | 128 | 28 |
| integer-bit-smear | 256 | 28 |
| integer-hexagonal-index | 512 | 93 |
| integer-magic-constants | 512 | 85 |
| integer-try-arithmetic | 512 | 18 |
| integer-try-multiplication | 512 | 13 |
| interval-arc-functions | 512 | 5 |
| interval-arctangent | 512 | 5 |
| interval-arithmetic | 512 | 5 |
| interval-circular | 512 | 5 |
| interval-isotonic | 512 | 5 |
| interval-power | 512 | 2 |
| mass-box | 256 | 239 |
| mass-capsule | 256 | 239 |
| mass-compound | 256 | 239 |
| mass-cylinder | 256 | 239 |
| mass-parallel-axis | 256 | 239 |
| mass-sphere | 256 | 239 |
| mass-volume | 256 | 239 |
| meet-associative | 512 | 298 |
| meet-bottom-absorption | 512 | 298 |
| meet-commutative | 512 | 298 |
| meet-idempotent | 512 | 298 |
| meet-monotonicity | 512 | 298 |
| meet-order-coherence | 512 | 298 |
| meet-product-composition | 512 | 298 |
| meet-top-identity | 512 | 298 |
| mixed-scale | 512 | 239 |
| mixed-scale-triple | 512 | 239 |
| mobius | 512 | 8406 |
| monogenic-exact | 512 | 388 |
| monogenic-fusion | 512 | 388 |
| position | 512 | 503 |
| position-delta | 512 | 612 |
| position-translate | 512 | 612 |
| presented | 512 | 910 |
| prime-field | 256 | 560 |
| prime-field-chain | 256 | 560 |
| prime-field-lucas | 256 | 560 |
| prime-field-primality | 256 | 560 |
| prime-field-root | 256 | 560 |
| q1648-scalar | 512 | 289 |
| q1648-scalar-division | 512 | 287 |
| q3232-scalar | 512 | 252 |
| q3232-scalar-division | 512 | 251 |
| quaternion | 512 | 629 |
| quaternion-direction | 512 | 516 |
| quaternion-rotate | 512 | 516 |
| quaternion-sublattice | 256 | 516 |
| rate | 512 | 613 |
| rigid | 512 | 652 |
| rigid-direction | 512 | 527 |
| rigid-point | 512 | 652 |
| scalar | 512 | 8410 |
| scalar-division | 512 | 647 |
| scalar-text | 512 | 647 |
| scalar-transcendental | 512 | 649 |
| smoke | 64 | 2136 |
| split | 512 | 10639 |
| split-divide | 512 | 629 |
| split-transform | 512 | 629 |
| square-grid | 512 | 82 |
| sublattice | 256 | 4566 |
| symmetric-apply2 | 512 | 239 |
| symmetric-apply3 | 512 | 239 |
| symmetric-invert2 | 256 | 282 |
| symmetric-invert3 | 256 | 282 |
| symmetric-solve2 | 512 | 282 |
| symmetric-solve3 | 512 | 283 |
| unit-fraction16 | 512 | 536 |
| unit-fraction32 | 512 | 654 |
| unsigned-scalar | 512 | 640 |
| vector | 512 | 624 |
| vector-componentwise-helpers | 512 | 18 |
| vector-direction | 512 | 624 |
| vector-lattice | 512 | 512 |
| vector-narrow | 512 | 624 |
| vector-norm | 512 | 512 |
| vector-orthonormal-basis | 512 | 175 |
| vector-ray-plane | 512 | 16 |

- last run: 2026-10-02
