# Transforms

This folder holds the transforms that carry a sequence into another basis and
back: two exact ones and two fixed-point ones. Each folder under `Puck.Maths`
is called a **wing**; this is the wing for frequency-domain and
sequency-domain representations and the convolutions built on them.

Every transform here has one shape:

- A **plan** — `NumberTheoreticTransformPlan`, `FixedFourierTransformPlan`,
  `FixedCosineTransformPlan` — is built once by `Create(length)` for a
  power-of-two length and caches every table that length needs. Building a
  plan is the only place a transform allocates. The Walsh–Hadamard transform
  needs no table, so it alone takes no plan.
- `Forward` and `Inverse` take the plan and a span, run **in place**, and touch
  nothing else. `Forward` is always the unscaled textbook sum; `Inverse` always
  carries the `1/N` normalization, so `Inverse(Forward(x))` is `x` (exactly
  for the exact transforms, within a measured bound for the fixed-point ones).
- The two spectral transforms — number-theoretic and Fourier — also offer
  `PointwiseMultiply` and `Convolve`, the cyclic convolution as
  forward–forward–pointwise–inverse.
- A length that is not a positive power of two is refused with
  `ArgumentOutOfRangeException` at `Create` (or `ArgumentException` on the span
  for the plan-free transform); a span whose length is not the plan's is
  refused with `ArgumentException` naming that parameter.

The shared bit-reversal permutation and the refusal messages live in one
internal `TransformKernels`; no transform carries its own copy.

## At a glance

| Type | Carrier | Exactness | What it's for |
|---|---|---|---|
| `NumberTheoreticTransform` + `NumberTheoreticTransformPlan` | `ulong` residues of `PrimeField64` at a fixed modulus | Exact | Exact cyclic convolution; polynomial and big-integer products. |
| `WalshHadamardTransform` | Any `IBinaryInteger<T>`; no plan | Exact inside the carrier | Sequency analysis, spreading and hashing, exact ±1 correlation. |
| `FixedFourierTransform` + `FixedFourierTransformPlan` | `FixedComplex` | Measured bound | Spectra, filtering and convolution of fixed-point signals. |
| `FixedCosineTransform` + `FixedCosineTransformPlan` | `FixedQ4816` | Measured bound | Real-signal spectra with energy compaction — audio and image blocks. |

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
the root's order with a Pocklington-style certificate. `Create`'s `length` is
an `int`, whose largest power of two is `2^30` — far below `2^44` — so every
representable length is legal.

**Montgomery form inside.** The butterfly network runs in
`ScaledResidueRing64`'s representation: `Forward` and `Inverse` encode their
operand once on entry, pay one REDC per butterfly product instead of a
hardware 128-by-64 division, and decode once on exit (`Inverse` folds its
`1/N` scale into that decoding REDC). The plan's tables are held in the same
form. `Convolve` never leaves the ring between its three transforms and its
pointwise product; its `left` and `right` are decoded back to ordinary
residues before it returns.

**Convolution.** `Convolve` overwrites `left` and `right` with their forward
transforms; only `destination` and a fresh pair of scratch spans are needed
for repeated calls.

| Operation | Semantics |
|---|---|
| `Forward` | `X[k] = sum over n of x[n] * root^(nk)`, in place. |
| `Inverse` | The exact inverse of `Forward`, in place: the conjugate network, then a multiply by the field inverse of `N`. |
| `PointwiseMultiply` | The elementwise field product of two spans; the destination may alias either. |
| `Convolve` | Forward, forward, pointwise multiply, inverse — the exact cyclic convolution. |

## `WalshHadamardTransform`

Exact integer arithmetic over any `IBinaryInteger<T>`: every butterfly is one
addition and one subtraction, there is no twiddle table, and so there is no
plan.

**Ordering.** `Forward` produces the Sylvester (natural) ordering,
`X[k] = sum over n of x[n] * (-1)^popcount(n AND k)` — the ordering the
recursive doubling `H(2N) = [[H, H], [H, -H]]` produces, not the sequency
(Walsh) ordering. Bin zero is the plain sum.

**Inverse.** Because `H * H = N * I`, `Inverse` is a second forward pass
followed by an arithmetic shift right by `log2(N)`: exact on any spectrum
`Forward` produced (every element of `H * H * x` is a multiple of `N`), and a
floor division by `N` on any other.

**Lanes.** The `long` and `int` carriers run every stage whose half-length
covers a whole `Vector<T>` lane-parallel; the first stages and every other
carrier take the scalar loop. Wrapping addition is the same operation in every
lane width, so the two paths return identical bits.

**Envelope.** Arithmetic is unchecked, the posture `FixedQ4816`'s `+` takes:
the transform is exact whenever `N * max|x|` fits the carrier and wraps
silently otherwise. A `FixedQ4816` sequence is transformed by passing its raw
`Value`s — the transform is linear over the integers, so the grid rides along.

| Operation | Semantics |
|---|---|
| `Forward<T>` | The unscaled Sylvester-order transform, in place. |
| `Inverse<T>` | `Forward`, then `>> log2(N)`, in place. |

## `FixedFourierTransform`

Fixed-point arithmetic over `FixedComplex`: the twiddle multiplies round
(one rounding per component, `FixedComplex`'s own one-rounding kernel), so
round trip, linearity and Parseval's identity hold within a measured bound
rather than exactly. The three inputs `fft.impulse-dc-nyquist-exact` covers
are the exception — every twiddle they touch is exactly `±1` or `±i`, so the
one-rounding multiply never rounds, and those bins are exact.

**Scaling convention.** `Forward` is unscaled — the textbook DFT sum — so an
impulse's spectrum is flat at `One`, a DC input's spectrum is `N * value` at
bin zero, and Parseval's identity reads in its textbook form. `Inverse`
halves every component at each of the `log2(N)` butterfly stages, reaching
the `1/N` normalization by exact bit shifts rather than by one late multiply
by `1/N` — which underflows to zero once `N > 2^16`, past `FixedQ4816`'s
sixteen fraction bits. Each inverse butterfly rounds once: the twiddle product
stays at Q32, the other operand is lifted to Q32, and their sum or difference
rounds to Q16 at a seventeen-bit shift, so the halving costs no rounding of
its own. `Inverse` never overflows past its own input's scale;
`Forward` can grow up to a factor of `N` across its stages, and `Convolve`'s
output grows as `N` times the product of the operands' amplitudes, so a
caller at a large length or amplitude pre-scales to stay inside `FixedQ4816`'s
raw range.

**Twiddles.** `Create` builds each forward twiddle independently via
`FixedComplex.FromAngle(FixedQ4816.FromDouble(angle))` — one
`FixedQ4816.SinCos` call per table entry rather than an incrementally
multiplied ladder, so each twiddle's error stays at `SinCos`'s own bound
instead of compounding across the table. Inverse twiddles are the exact
conjugates of the forward ones.

| Operation | Semantics |
|---|---|
| `Forward` | The unscaled forward transform, in place. |
| `Inverse` | The per-stage-halved inverse transform, in place; one rounding per butterfly component. |
| `ForwardReal` | Embeds a real sequence (zero imaginary parts) and forwards it. |
| `InverseReal` | Inverts a spectrum and discards the imaginary part of each restored sample. |
| `PointwiseMultiply` | The elementwise `FixedComplex` product; the destination may alias either operand. |
| `Convolve` | Forward, forward, pointwise multiply, inverse — the cyclic convolution within a measured bound. |

## `FixedCosineTransform`

Fixed-point arithmetic over `FixedQ4816`: the DCT-II and its DCT-III inverse,
computed through one `FixedFourierTransform` of the same length rather than a
double-length embedding.

**Scaling convention.** `Forward` is the unscaled DCT-II,
`X[k] = sum over n of x[n] * cos(pi * (2n + 1) * k / (2N))`, so a constant
input's spectrum is exactly `N * value` at bin zero and exactly zero
elsewhere; `Inverse` is the matching DCT-III with `1/N` folded in,
`x[n] = X[0]/N + (2/N) * sum over k >= 1 of X[k] * cos(pi * (2n + 1) * k / (2N))`,
reached through the Fourier inverse's per-stage halving. Parseval's identity
for this convention reads `N * sum x[n]^2 = X[0]^2 + 2 * sum over k >= 1 of X[k]^2`.

**The route.** The even samples ascend into the front half of a complex
scratch sequence and the odd samples descend into the back half
(`v[n] = x[2n]`, `v[N-1-n] = x[2n+1]`); one forward FFT, then one
post-twiddle by `exp(-i*pi*k/(2N))` per bin, yields the DCT-II as the real
part. The inverse folds the spectrum into
`V[k] = exp(+i*pi*k/(2N)) * (X[k] - i*X[N-k])` (with `X[N] = 0`), inverts,
and un-permutes. Both directions take a caller-supplied `FixedComplex`
scratch span of the same length, so nothing allocates beyond the plan and
two threads sharing one plan never share a buffer.

| Operation | Semantics |
|---|---|
| `Forward` | The unscaled DCT-II, in place over the real span, through the scratch span. |
| `Inverse` | The normalized DCT-III, in place over the real span, through the scratch span. |

**Accuracy is measured, not assumed.** For both fixed-point transforms,
round-trip, linearity and Parseval error scale with operand amplitude — each
twiddle's own quantization error (from `FixedQ4816.SinCos`) multiplies through
the signal at every stage. The `fft.*` and `dct.*` laws pin their bounds at a
documented amplitude envelope (raw `[-2^20, 2^20]`, about `±16.0`; the
convolution law at raw `[-2^16, 2^16]`) and freeze them at a measured maximum
with margin; a caller at a different amplitude should expect error to scale
proportionally, not to stay under the same ceiling.

## Verifying changes

The `ntt.*`, `wht.*`, `fft.*` and `dct.*` law families are the gate of
record, in [tests/Puck.Maths.Tests](../../../tests/Puck.Maths.Tests/README.md).

```text
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings
```

`ntt.*` and `wht.*` statements are exact identities or exact agreement with an
O(N^2) definition-form `BigInteger` reference in `Oracles` — nothing in either
subject rounds, so nothing there is a bound. `fft.*` and `dct.*` statements
split between exact bins (an impulse, a constant, the Nyquist alternation),
measured round-trip, linearity, Parseval and convolution bounds (each with a
Deep-tier mirror at longer lengths), the fast route against a direct O(N^2)
sum built from the same `SinCos` kernel with a different schedule, the wiring
of the real wrappers and the pointwise product, and refusals.

`puck bench --filter '*Ntt*'`, `'*Wht*'`, `'*Fft*'` and `'*Dct*'` measure
`Forward`/`Inverse` latency and each transform against its O(N^2) baseline;
none gates a value.
