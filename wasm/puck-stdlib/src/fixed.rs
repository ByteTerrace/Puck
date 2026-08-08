//! Bit-exact mirror of `Puck.Maths.FixedQ4816` (`src/Puck.Maths/FixedPoint/FixedQ4816.cs`) — the
//! signed Q48.16 fixed-point type that crosses the addon ABI. Every fixed-point ABI lane is this
//! type's raw `i64` storage (`FixedQ4816.Value`); the host never sends or accepts a float, ever.
//!
//! This module splits its surface by **what pins the correct answer**:
//!
//! - `add`, `sub`, `neg`, `cmp`, `clamp`, `mul`, `div`, and `sqrt` are **uniquely specified by a
//!   spec**: exact full-width arithmetic, rounded to nearest with ties to even (`sqrt` has no
//!   rounding at all — it is the exact integer floor of a widened square root). Any correct
//!   implementation of that spec, in any language, agrees with the host bit-for-bit, so these are
//!   plain guest code below.
//! - `atan2`, `sin`, `cos`, `exp2`, `log2`, and `pow` are **specified only by a particular
//!   algorithm** — a specific table-plus-polynomial recipe, accurate to roughly 0.5 ULP, not a
//!   closed-form answer with one correct bit pattern. Two independently *correct*
//!   implementations of, say, `atan2` will disagree by about 1 ULP, so hand-porting them can't be
//!   validated by reasoning about correctness the way `div`/`sqrt` can. `fixed_generated.rs`
//!   carries a **generated** Rust port of the host's own tables and polynomial coefficients
//!   instead — read from the live `FixedQ4816` type by the `wasm-stdlib` CLI verb
//!   (`dotnet run --project src/Puck.Cli -c Release -- wasm-stdlib`), never transcribed by hand — and
//!   `fixed_vectors.rs` carries the known-answer vectors (also generated, also read from the live
//!   host) that prove the port hasn't drifted. The six functions below are plain re-exports of
//!   that module: there is one public surface, and it never changes shape depending on which
//!   module actually does the arithmetic. This addon module is fully self-contained — there is no
//!   WASM import here or anywhere else in the crate.
//!
//! **Golden rule**: everything in the first group must match its `FixedQ4816` operator or method
//! bit-for-bit, tie for tie, sign for sign — `fixed_tests.rs`'s known-answer vectors are the
//! contract for that; everything in the second group is pinned the same way, by
//! `fixed_vectors.rs`'s known-answer vectors, which is why those two are GENERATED rather than
//! hand-edited — regenerate them with the tools verb above instead of touching them directly.
//! No gate checks this agreement today — the stage that once compared guest arithmetic against
//! the host left the build with `Puck.Post`'s quarantine and nothing replaced it. The stakes are
//! unchanged: if your addon's arithmetic ever disagrees with the host's, a replay diverges
//! silently, so the known-answer vectors above are the only machine check standing.

use core::cmp::Ordering;

/// Number of fractional bits in the Q48.16 layout.
pub const FRACTION_BITS: u32 = 16;
/// The raw representation of `1.0` (`AddonAbi.One` on the host).
pub const ONE: i64 = 1i64 << FRACTION_BITS;
/// The raw representation of `0.0`.
pub const ZERO: i64 = 0;
/// The raw representation of `-1.0`.
pub const NEGATIVE_ONE: i64 = -ONE;

const FRACTION_MASK: u64 = (1u64 << FRACTION_BITS) - 1;
// The half-ULP threshold, in the fraction domain — the tie point for round-half-to-even.
const HALF_ULP: u64 = 1u64 << (FRACTION_BITS - 1);

/// Converts a raw `i64` bit pattern (as carried on the ABI) to this module's raw storage — a no-op
/// wrapper kept for symmetry with `to_bits` and to mirror `FixedQ4816.FromRawBits`.
#[inline]
#[must_use]
pub const fn from_bits(value: i64) -> i64 {
    value
}

/// Returns the raw `i64` storage to write onto the ABI — a no-op wrapper mirroring
/// `FixedQ4816.Value`.
#[inline]
#[must_use]
pub const fn to_bits(value: i64) -> i64 {
    value
}

/// Adds two fixed-point values, wrapping on overflow (mirrors `FixedQ4816.operator +`).
#[inline]
#[must_use]
pub const fn add(x: i64, y: i64) -> i64 {
    x.wrapping_add(y)
}

/// Subtracts `y` from `x`, wrapping on underflow (mirrors `FixedQ4816.operator -`).
#[inline]
#[must_use]
pub const fn sub(x: i64, y: i64) -> i64 {
    x.wrapping_sub(y)
}

/// Negates a fixed-point value, wrapping only at `i64::MIN` (mirrors
/// `FixedQ4816.operator -(value)`).
#[inline]
#[must_use]
pub const fn neg(value: i64) -> i64 {
    value.wrapping_neg()
}

/// Compares two fixed-point values. The raw two's-complement storage orders identically to the
/// represented real number, so this is a plain `i64` comparison (mirrors the host's comparison
/// operators, which compare `.Value` directly).
#[inline]
#[must_use]
pub fn cmp(x: i64, y: i64) -> Ordering {
    x.cmp(&y)
}

/// Restricts `value` to the inclusive range `[minimum, maximum]` (mirrors `FixedQ4816.Clamp`).
#[inline]
#[must_use]
pub fn clamp(value: i64, minimum: i64, maximum: i64) -> i64 {
    value.clamp(minimum, maximum)
}

/// Multiplies two Q48.16 values, rounding the result to nearest with ties to even — bit-for-bit
/// identical to `FixedQ4816.operator *`.
///
/// The raw product is `x*y*2^32`; the wanted result is `x*y*2^16`, i.e. the product shifted right
/// by 16 bits and rounded. This rounds the non-negative magnitude — ties to even, inspecting the
/// **truncated result's low bit**, never `+ 0.5` — then re-applies the sign, exactly as the host
/// does (the integer neighbors share parity, so both signs round identically via the magnitude).
#[must_use]
pub fn mul(x: i64, y: i64) -> i64 {
    let product = i128::from(x) * i128::from(y);
    let negative = product < 0;
    let magnitude = (if negative { -product } else { product }) as u128;
    let mut truncated = (magnitude >> FRACTION_BITS) as u64;
    let remainder = (magnitude as u64) & FRACTION_MASK;

    if (remainder > HALF_ULP) || ((remainder == HALF_ULP) && ((truncated & 1) != 0)) {
        truncated = truncated.wrapping_add(1);
    }

    let result = truncated as i64;

    if negative {
        result.wrapping_neg()
    } else {
        result
    }
}

/// Divides `x` by `y` in fixed point, rounding to nearest with ties to even and wrapping on
/// overflow — bit-for-bit identical to `FixedQ4816.operator /`.
///
/// `result = round((x << 16) / y)`, ties to even. The host picks between a hardware 128-by-64
/// divide and a portable `UInt128` divide purely for speed; the two always agree, so this mirrors
/// only the portable one: widen `x`'s magnitude to 128 bits before shifting left by 16 (a plain
/// 64-bit shift would lose the top bits for large `x`), divide by `y`'s magnitude, then round the
/// quotient to nearest with ties to even. The tie compare is `remainder` against `divisor -
/// remainder` (equivalent to `2*remainder` against `divisor`, but it cannot overflow a 64-bit
/// register, which is exactly why the host writes it that way) — round up when the remainder is
/// past the halfway point, or exactly at it with an odd quotient. The combined sign of `x` and
/// `y` is re-applied at the end (parity-symmetric, so both signs round identically by magnitude).
///
/// # Panics
/// `y` is zero (mirrors the host's `DivideByZeroException`).
#[must_use]
pub fn div(x: i64, y: i64) -> i64 {
    assert!(y != 0, "FixedQ4816 division by zero");

    let sign_x = x >> 63;
    let sign_y = y >> 63;
    let x_magnitude = ((x ^ sign_x).wrapping_sub(sign_x)) as u64;
    let y_magnitude = ((y ^ sign_y).wrapping_sub(sign_y)) as u64;

    let dividend = (x_magnitude as u128) << FRACTION_BITS;
    let divisor = y_magnitude as u128;
    let quotient128 = dividend / divisor;
    let remainder = (dividend - (quotient128 * divisor)) as u64;

    let mut quotient = quotient128 as u64;

    if (remainder > (y_magnitude - remainder))
        || ((remainder == (y_magnitude - remainder)) && ((quotient & 1) != 0))
    {
        quotient = quotient.wrapping_add(1);
    }

    let result = quotient as i64;
    let result_sign = sign_x ^ sign_y;

    (result ^ result_sign).wrapping_sub(result_sign)
}

/// Non-negative floor square root in fixed point — bit-for-bit identical to `FixedQ4816.Sqrt`.
///
/// `value <= 0` returns zero, mirroring the host. Otherwise the result is exactly
/// `floor(sqrt((value as u64 as u128) << FRACTION_BITS))`: the host widens to 128 bits because a
/// plain 64-bit `<< 16` loses the top bits once `value` is at or above `2^48`. Because the answer
/// is defined as an *exact* integer floor rather than an approximation, any provably-exact
/// algorithm reaches the same bits as the host — unlike `atan2`/`sin`/`cos`/`exp2`/`log2`/`pow`,
/// whose answers are pinned only by a specific table-and-polynomial recipe. The host itself seeds
/// its answer with a hardware float square root and settles it with an integer correction, purely
/// for speed; this is a plain bit-by-bit (binary digit-by-digit) restoring integer square root
/// instead — slower, but exact by construction, so there is no seed to get right and no
/// correction step to verify.
#[must_use]
pub fn sqrt(value: i64) -> i64 {
    if value <= 0 {
        return 0;
    }

    let scaled = (value as u64 as u128) << FRACTION_BITS;

    isqrt_u128(scaled) as i64
}

/// Exact integer floor square root of a `u128` by binary digit-by-digit restoring division — the
/// textbook "one bit of the root per two bits of the radicand" algorithm. Each step either can or
/// cannot subtract off the next candidate digit, so the result is exactly `floor(sqrt(value))` for
/// every `value`, with no hardware float and no approximation to verify.
fn isqrt_u128(value: u128) -> u128 {
    if value == 0 {
        return 0;
    }

    // Start at the highest power of four that fits a u128 (2^126 — 126 is the largest even
    // exponent below 128), then shrink until it no longer overshoots `value`.
    let mut bit: u128 = 1u128 << 126;

    while bit > value {
        bit >>= 2;
    }

    let mut remainder = value;
    let mut result: u128 = 0;

    while bit != 0 {
        let candidate = result + bit;

        if remainder >= candidate {
            remainder -= candidate;
            result = (result >> 1) + bit;
        } else {
            result >>= 1;
        }

        bit >>= 2;
    }

    result
}

// --- Algorithm-pinned transcendentals: generated, never hand-written ------------------------
//
// `atan2`, `sin`, `cos`, `exp2`, `log2`, and `pow` are pinned only by a specific table-plus-
// polynomial recipe (see the module doc above). `fixed_generated.rs` carries the actual port —
// its tables and polynomial coefficients are read from the live `FixedQ4816` type by the
// `wasm-stdlib` CLI verb, never transcribed by hand — and `fixed_vectors.rs` carries the
// known-answer vectors (also generated) that prove it hasn't drifted. The six functions below are
// plain re-exports: they exist so an addon author's call sites never need to know which module
// actually does the arithmetic, and so this file stays the one place documenting the ABI-facing
// signatures (including `atan2`'s `(y, x)` argument order).
//
// There is deliberately no combined `sincos` export, even though the host itself exposes a
// single `SinCos` call that computes both at once: the addon ABI has no multi-value host call —
// a guest imports nothing at all — so `sin` and `cos` stay two separate calls here too, for
// parity with the rest of this file's surface — do not "fix" this by trying to fuse them.

/// Angle from the positive X axis to `(x, y)`, in fixed-point radians in `(-pi, pi]` — mirrors
/// `FixedQ4816.Atan2`; see `fixed_generated::atan2` for the port itself.
///
/// **Argument order matches the host method (and C's `atan2`): `(y, x)`, not `(x, y)`.**
#[inline]
#[must_use]
pub fn atan2(y: i64, x: i64) -> i64 {
    crate::fixed_generated::atan2(y, x)
}

/// Cosine of `angle` (fixed-point radians) — mirrors `FixedQ4816.Cos`; see `fixed_generated::cos`
/// for the port itself.
#[inline]
#[must_use]
pub fn cos(angle: i64) -> i64 {
    crate::fixed_generated::cos(angle)
}

/// `2^value` in fixed point — mirrors `FixedQ4816.Exp2`; see `fixed_generated::exp2` for the port
/// itself.
#[inline]
#[must_use]
pub fn exp2(value: i64) -> i64 {
    crate::fixed_generated::exp2(value)
}

/// `log2(value)` in fixed point — mirrors `FixedQ4816.Log2`; see `fixed_generated::log2` for the
/// port itself.
#[inline]
#[must_use]
pub fn log2(value: i64) -> i64 {
    crate::fixed_generated::log2(value)
}

/// `x` raised to the power `y`, in fixed point — mirrors `FixedQ4816.Pow` (via
/// `IPowerFunctions<FixedQ4816>`); see `fixed_generated::pow` for the port itself.
#[inline]
#[must_use]
pub fn pow(x: i64, y: i64) -> i64 {
    crate::fixed_generated::pow(x, y)
}

/// Sine of `angle` (fixed-point radians) — mirrors `FixedQ4816.Sin`; see `fixed_generated::sin`
/// for the port itself.
#[inline]
#[must_use]
pub fn sin(angle: i64) -> i64 {
    crate::fixed_generated::sin(angle)
}
