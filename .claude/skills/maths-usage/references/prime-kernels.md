# Prime kernel rules and measured evidence

Read [prime exploration](../../../../docs/reference/prime-exploration.md)
before changing `IsPrime`, `NthPrime(uint)`, `PrimeKernels` or
`ScaledResidueRing64`. That reference owns the retained algorithms and useful
research conclusions. The owner's cleanup removes the wide counting and
interval exploration engine; it does not remove the separate maths research,
algebra or oracle wings.

- Keep the fastest correct single-value decision. It runs on one core;
  threads and GPU work do not belong on this path.
- Tune AVX2 and AVX-512 while preserving correct narrower and scalar
  fallbacks. Gate on instruction-set capability, never vendor.
- Keep shared primitives: the base-prime table also serves narrow nth-prime,
  primality, factorization and quadratic arithmetic. Witness bounds serve
  arbitrary-width primality, and modular reductions serve finite fields.
- Use counted work and generated instructions to judge a change. Warm tiered
  code unpinned before measuring: pinning a harness before warmup can starve
  the tiering thread and measure Tier0. Read timing on a quiet machine beside
  those counts; code layout alone has moved cycles by 1–3%.
- A structural table can accelerate arithmetic, but a table of answers over a
  query range is not a substitute for it. The small hashed-witness certificate
  remains an [owner decision](../../../../docs/plans/open-items.md), because
  production primality still reaches that range.
- Re-measure on a new .NET major before relying on old tuning. On .NET 11,
  `Math.BigMul` keeps both halves in registers; the retained reductions use
  three multiplies, and the signed base-two path avoids per-step folds.
- Read the reference's measured dead ends before proposing a branching trace
  ladder, an `Int128` signed reduction or a borrowed walk below 2^63 again.
