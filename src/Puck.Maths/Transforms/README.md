# Transforms

This folder holds frequency-domain transforms: the exact number-theoretic
transform over a finite field, and the fixed-point fast Fourier transform over
complex fixed-point numbers. Each folder under `Puck.Maths` is called a
**wing**; this is the wing for turning a sequence into (and back out of) its
frequency-domain representation.

Both transforms follow the same shape. A **plan** — `NttPlan` or
`FixedFourierPlan` — is built once for a power-of-two length and caches the
root-of-unity or twiddle table that length needs; `Forward` and `Inverse` then
take a plan and a span, run the in-place radix-2 butterfly network, and touch
nothing else. Building a plan is the only place either transform allocates;
a cached plan reused across many calls costs nothing per call beyond the
butterfly arithmetic itself.

## At a glance

| Type | Kind | What it's for |
|---|---|---|
| `NumberTheoreticTransform` | `static` | The exact NTT over `PrimeField64` at a fixed NTT-friendly modulus: `Forward`, `Inverse`, `PointwiseMultiply`, and the exact cyclic `Convolve` built from them. |
| `NttPlan` | `sealed class` | The cached root-of-unity table for one power-of-two length. |
| `FixedFourierTransform` | `static` | The fixed-point FFT over `FixedComplex`: `Forward`, `Inverse`, and the real-sequence convenience wrappers `ForwardReal` / `InverseReal`. |
| `FixedFourierPlan` | `sealed class` | The cached twiddle table for one power-of-two length, built from `FixedQ4816.SinCos`. |

## `NumberTheoreticTransform`

Exact arithmetic: every element is a `PrimeField64` residue, so a produced
value is the unique correct answer modulo the modulus, and the convolution
theorem holds bit-for-bit rather than within a bound.

**The modulus.** `NumberTheoreticTransform.Modulus` is
`262111 * 2^44 + 1 = 4611105476287922177`, chosen for high two-adicity below
`PrimeField64.MaximumModulus` (`2^62`): `Modulus - 1` factors as
`262111 * 2^44`, with `262111` itself prime, so every power-of-two length up
to `2^44` divides the multiplicative group's order and has a primitive
`N`-th root of unity. `PrimitiveRoot` (`3`) generates the whole group; the
`ntt.prime-and-primitive-root` law proves the primality of both factors and
the root's order with a Pocklington-style certificate (not one at the group
order divided by either prime factor). `NttPlan.Create`'s `length` parameter
is an `int`, whose largest power of two is `2^30` — far below `2^44` — so the
prime's own two-adicity ceiling is never the refusal a caller hits; every
representable length is legal.

**Convolution.** `Convolve` forwards both operands, multiplies pointwise,
and inverts — the convolution theorem. It OVERWRITES `left` and `right`
with their forward transforms; only `destination` and a fresh pair of
scratch spans are needed for repeated calls, so nothing is allocated beyond
the plan itself.

| Operation | Semantics |
|---|---|
| `Forward` | `X[k] = sum over n of x[n] * root^(nk)`, in place. |
| `Inverse` | The exact inverse of `Forward`, in place. |
| `PointwiseMultiply` | The elementwise field product of two spans. |
| `Convolve` | Forward, forward, pointwise multiply, inverse — the exact cyclic convolution. |

## `FixedFourierTransform`

Fixed-point arithmetic over `FixedComplex`: the twiddle multiplies round
(one rounding per component, `FixedComplex`'s own one-rounding kernel), so
round trip, linearity and Parseval's identity hold within a measured bound
rather than exactly. `ImpulseDcNyquistExact`'s three inputs are the
exception — every twiddle they touch is exactly `±1` or `±i`, so the
one-rounding multiply never actually rounds, and those three bins are exact.

**Scaling convention.** `Forward` is UNSCALED — the textbook DFT sum — so an
impulse's spectrum is flat at `One`, a DC input's spectrum is `N * value` at
bin zero, and Parseval's identity reads in its familiar textbook form with no
extra scale factor. `Inverse` instead halves every component at EACH of the
`log2(N)` butterfly stages, reaching the `1/N` normalization by exact bit
shifts rather than by one late multiply by `1/N` — which underflows to zero
once `N > 2^16`, past `FixedQ4816`'s sixteen fraction bits. `Inverse` never
overflows past its own input's scale (repeated halving only shrinks);
`Forward`, being unscaled, can grow up to a factor of `N` across its stages,
so a caller working at a large length or a large amplitude should pre-scale
to stay inside `FixedQ4816`'s raw range.

**Twiddles.** `FixedFourierPlan.Create` builds each forward twiddle
independently via `FixedComplex.FromAngle(FixedQ4816.FromDouble(angle))` —
one `FixedQ4816.SinCos` call per table entry rather than an incrementally
multiplied ladder, so each twiddle's error stays at `SinCos`'s own bound
instead of compounding across the table. Inverse twiddles are the EXACT
conjugates of the forward ones.

| Operation | Semantics |
|---|---|
| `Forward` | The unscaled forward transform, in place. |
| `Inverse` | The per-stage-halved inverse transform, in place. |
| `ForwardReal` | Embeds a real sequence (zero imaginary parts) and forwards it. |
| `InverseReal` | Inverts a spectrum and discards the imaginary part of each restored sample. |

**Accuracy is measured, not assumed.** Round-trip, linearity and Parseval
error all scale with operand amplitude — each twiddle's own quantization
error (from `FixedQ4816.SinCos`) multiplies through the signal at every
stage. The `fft.*` laws pin their bounds at a specific, documented amplitude
envelope (raw `[-2^20, 2^20]`, about `±16.0`) and freeze them at a measured
maximum with margin; a caller working at a different amplitude should expect
error to scale proportionally, not to stay under the same ceiling.

## Verifying changes

The `ntt.*` and `fft.*` law families are the gate of record, in
[tests/Puck.Maths.Tests](../../../tests/Puck.Maths.Tests/README.md).

```text
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings
```

`ntt.*` statements are exact identities or exact agreement with
`Oracles.CyclicConvolutionModulus`'s O(N^2) BigInteger reference — nothing in
`PrimeField64` rounds, so nothing here is a bound. `fft.*` statements split
between the three exact bins (`fft.impulse-dc-nyquist-exact`), measured
bounds (`fft.round-trip-bound`, `fft.linearity-bound`, `fft.parseval-bound`,
each with a Deep-tier mirror at longer lengths), same-process determinism
(`fft.self-referential-bit-identity`), the radix-2 network against an
independently scheduled direct O(N^2) sum built from the same
`FixedComplex` kernel (`fft.radix2-vs-direct-sum`), the real-sequence
wrappers' faithfulness (`fft.real-wrappers-are-faithful-embeddings`), and
refusals (`ntt.length-refusals`, `fft.length-refusals`).

`puck bench --filter '*Ntt*'` and `puck bench --filter '*Fft*'` measure
`Forward`/`Inverse` latency and the transform against its O(N^2) baseline;
neither gates a value.
