# Puck.Maths

Low-level numeric primitives for the Puck engine: unsigned binary **fixed-point**
types and a kit of **branchless, width-agnostic integer routines** (bit twiddling,
prime work, pairing functions, square roots, and cryptographically secure random
draws).

Everything here is allocation-free, deterministic, and written against
`System.Numerics.IBinaryInteger<T>` so a single implementation serves every integer
width from `byte` to `Int128`. There are no external dependencies — `Puck.Maths` is a
leaf library.

```text
namespace Puck.Maths
target     net10.0
deps       none
```

---

## At a glance

| Type | Kind | What it's for |
|------|------|---------------|
| `UFixedQ4816` | `readonly record struct` | UQ48.16 fixed-point — 48 integer bits, 16 fraction bits (range `[0, 2⁴⁸)`). |
| `UFixedQ0016` | `readonly record struct` | UQ0.16 fraction — a real number in `[0, 1)` stored in 16 bits. |
| `UFixedQ0032` | `readonly record struct` | UQ0.32 fraction — a real number in `[0, 1)` stored in 32 bits. |
| `BinaryIntegerFunctions` | `static` ext. methods | Bit manipulation & base-10 digit ops over `IBinaryInteger<T>`. |
| `UnsignedNumberFunctions` | `static` ext. methods | Pairing functions, prime factorization, modular inverse, integer roots. |
| `PrimeExtensions` | `static` ext. methods | Deterministic primality, n-th prime, prime counting (`uint`). |
| `SecureRandom` | `static` | Uniform, unbiased, cryptographically secure unsigned draws. |

> `BinaryIntegerConstants<T>` is an internal helper (width, log2-width, the constants
> 9 and 10 for an arbitrary `T`) and is not part of the public surface.

---

## Fixed-point types

All three fixed-point structs wrap a single unsigned integer (`ulong`/`ushort`/`uint`)
whose value is the represented real number **scaled by `2^FractionBitCount`**. They
implement the full generic-math operator interface set (`IAdditionOperators`,
`IComparisonOperators`, `IShiftOperators`, `IMinMaxValue`, `ISpanFormattable`,
`ISpanParsable<T>`, …), so they drop into generic numeric code.

**Key idea:** the most-significant bit is always an ordinary magnitude bit — these
types are *unsigned*. `Abs` is the identity, and negation/`--`/subtraction *wrap*
through the unsigned range unless you use the saturating helpers.

### Choosing a type

- **`UFixedQ4816`** — when you need an integer part: positions, sizes, accumulated
  advances. Range `[0, ~2.8×10¹⁴)`, resolution `2⁻¹⁶ ≈ 1.5×10⁻⁵`.
- **`UFixedQ0016` / `UFixedQ0032`** — pure fractions in `[0, 1)`: normalized
  coordinates, blend factors, sub-pixel offsets. There is **no representable `1.0`**
  (hence no `One`, no `MultiplicativeIdentity`, no `++`). Multiplication is closed over
  `[0, 1)` and cannot overflow; addition/division/left-shift can leave the range.

### Wrap vs. saturate

Operators wrap (modular arithmetic, matching `unchecked` integer semantics). When you
want clamping instead, call the explicit helpers:

```csharp
using Puck.Maths;

var a = UFixedQ4816.FromInteger(value: 3);     // 3.0
var b = UFixedQ4816.FromDouble(value: 0.25);   // 0.25

var sum     = a + b;                                  // 3.25  (wraps on overflow)
var clamped = UFixedQ4816.AddSaturating(x: a, y: b);  // 3.25, but pins to MaxValue
var product = a * b;                                  // 0.75  (round-half-to-even)

double d = (double)product;        // 0.75
string s = product.ToString();     // "0.75"  — exact decimal expansion, invariant culture
```

### Rounding & conversion notes

- `*` and `/` round **to nearest, ties to even**. The `…Unchecked` variants truncate
  and hand back the remainder so you can build your own rounding.
- `FromDouble` rounds to nearest (ties to even) and **clamps** out-of-range / negative /
  NaN inputs into range.
- `ToString` / `TryFormat` emit the **exact** decimal expansion (a `/2ⁿ` fraction always
  terminates) using the invariant culture; the `format`/`provider` arguments are ignored.
- `Parse` / `TryParse` use `decimal` internally and reject out-of-range text, so
  `Parse(x.ToString())` round-trips.
- `FromRawBits` / the `Value` member let you reinterpret the raw storage directly.

`UFixedQ4816.DivideUnchecked` uses a hardware 128÷64 divide on x64 and falls back to
`UInt128` arithmetic elsewhere.

---

## Integer routines

### `BinaryIntegerFunctions` (generic over `IBinaryInteger<T>`)

Branchless, width-agnostic bit and digit operations. A closed generic compiles to a
compact, value-independent instruction sequence, and hardware instructions (`PDEP`/`PEXT`
via BMI2) are used when available.

| Method | Result |
|--------|--------|
| `BitwisePair` / `BitwiseUnpair` | Morton (Z-order) interleave and its inverse. |
| `ReverseBits` | Reverse all bits (SWAR butterfly). |
| `ReflectedBinaryEncode` / `…Decode` | Gray code ↔ binary. |
| `PermuteBitsLexicographically` | Next integer with the same popcount (Gosper's hack). |
| `PopulationParity` | Parity of the popcount. |
| `ExtractLowestSetBit` / `ClearLowestSetBit` | Isolate / clear the lowest set bit. |
| `FillFromLowestSetBit` / `FillFromLowestClearBit` | Fill trailing zeros / clear trailing ones. |
| `LeastSignificantBit` / `MostSignificantBit` | 1-based bit position (0 if none). |
| `GreatestCommonDivisor` / `LeastCommonMultiple` | Binary GCD (Stein's) and LCM. |
| `Exponentiate` | Integer power by squaring. |
| `DigitalRoot`, `EnumerateDigits`, `LogarithmBase10` | Base-10 digit work. |
| `LeastSignificantDigit` / `MostSignificantDigit` | First / last decimal digit. |
| `ReverseDigits`, `RotateDigitsLeft` / `…Right` | Decimal digit reversal / rotation (sign-preserving). |

```csharp
using Puck.Maths;

ulong morton = 5u.BitwisePair<uint, ulong>(other: 3u);  // interleave x=5, y=3
(uint x, uint y) = morton.BitwiseUnpair<ulong, uint>();  // -> (5, 3)

uint g = 48u.GreatestCommonDivisor(other: 36u);          // 12
int  reversed = 1230.ReverseDigits();                    // 321
```

### `UnsignedNumberFunctions` (generic, `IBinaryInteger<T>` + `IUnsignedNumber<T>`)

| Method | Result |
|--------|--------|
| `ElegantPair` / `ElegantUnpair` | Szudzik pairing of two non-negatives ↔ one value. |
| `EnumeratePrimeFactors` | Lazy prime factors with multiplicity (mod-30 wheel). |
| `ModularInverse` | Multiplicative inverse of an odd value mod `2^width` (Newton–Hensel). |
| `SquareRoot` | Floor integer square root (hardware-seeded for ≤64-bit). |
| `NextPowerOfTwo` / `NextSquare` | Round up to the next power of two / perfect square. |

### `PrimeExtensions` (on `uint`)

Exact over the **entire** 32-bit range — never probabilistic. Trial division by small
primes, then a deterministic Miller–Rabin with bases `{2, 7, 61}`; the hot loop reduces
through a precomputed reciprocal and does no hardware division.

```csharp
using Puck.Maths;

bool isPrime = 1_000_003u.IsPrime();              // true
uint p       = 100u.NthPrime();                   // 547   (0-based index)
uint pi      = 1_000_000u.PrimeCountingFunction();// 78498 (primes ≤ 1,000,000)
```

`PrimeCountingFunction` uses a sublinear combinatorial method and rents its working
buffers from `ArrayPool<T>` (peak ≈ 512 KiB).

### `SecureRandom`

Uniform, bias-free unsigned draws backed by `RandomNumberGenerator`. Bounded draws use
rejection sampling (not modulo), so the output is exactly uniform.

```csharp
using Puck.Maths;

uint full   = SecureRandom.NextUInt<uint>();                      // whole range
uint bounded = SecureRandom.NextUInt<uint>(maximum: 99, minimum: 1); // inclusive [1, 99]
```

---

## Notes for agents

- **Unsigned only.** None of these types or routines model negative numbers; `Abs` is the
  identity on the fixed-point types and the unsigned helpers assume non-negative inputs.
- **Wrap is the default.** Bare operators wrap on overflow/underflow. Reach for
  `AddSaturating` / `SubtractSaturating` (and the `[0,1)` types' built-in saturation on `*`
  and `/`) when you need clamping.
- **No `1.0` in the Q0.x types.** Don't look for `One` / `MultiplicativeIdentity` / `++`
  on `UFixedQ0016` or `UFixedQ0032` — they don't exist by design.
- **Determinism.** Results do not depend on culture, locale, or CPU feature availability
  (hardware paths are bit-identical to the fallbacks). Formatting/parsing is invariant.
- **Generic-math friendly.** Pass these as `T` into code constrained on the
  `System.Numerics` operator interfaces.
- See the [generated API reference](../../docs/api) for the full member-by-member docs.
