//! Bit-exact mirror of `Puck.Maths.FixedQ4816` (`src/Puck.Maths/FixedQ4816.cs`) — the signed Q48.16
//! fixed-point type that crosses the `puck.addon.v1` ABI. Every ABI field is this type's raw `i64`
//! storage (`FixedQ4816.Value`); the host never sends or accepts a float, ever.
//!
//! **Golden rule**: `mul` here must match `FixedQ4816.operator *` in the host bit-for-bit, tie for
//! tie, sign for sign. Puck.Post's `scripting-determinism` stage (the `echo`/`fuel-boundary`
//! fixtures) is the reference oracle for this contract — if your addon's arithmetic ever disagrees
//! with the host's, a replay or a fuel-boundary comparison diverges silently.

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

/// CORDIC vectoring constant for `atan2` — copied verbatim from `FixedQ4816.cs` for an author who
/// opts into implementing `atan2` below. Unused by `mul`/`add`/`sub`/`clamp`.
pub const HALF_PI_RAW: i64 = 102_944;
/// `atan(2^-i)` for `i = 0..15`, pre-scaled to Q16 radians, in the exact iteration order the host
/// consumes them.
pub const CORDIC_ATAN_TABLE: [i64; 16] = [
    51472, 30386, 16055, 8150, 4091, 2048, 1024, 512, 256, 128, 64, 32, 16, 8, 4, 2,
];

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
/// identical to `FixedQ4816.operator *` (`src/Puck.Maths/FixedQ4816.cs:93-110`).
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

/// Divides `x` by `y` in fixed point, rounding to nearest with ties to even — the host's exact
/// algorithm (`FixedQ4816.cs:116-134`), shipped as a **documented opt-in stub**: v1's clamp-walk
/// ghost (see `lib.rs`) needs no division, so this panics until an author wires it up. To
/// implement bit-for-bit: widen `x << 16` and `y` to `i128`; take absolute magnitudes; divide
/// (`abs_dividend / abs_divisor`); round the quotient to nearest with ties to even using
/// `2 * remainder` compared against the divisor (round up when `2*remainder > divisor`, or when
/// equal and the quotient's low bit is set); then re-apply the combined sign of `x` and `y`.
///
/// # Panics
/// Always — this is an unimplemented opt-in stub. See the doc comment above for the algorithm.
#[must_use]
pub fn div(_x: i64, _y: i64) -> i64 {
    unimplemented!(
        "FixedQ4816 division is an opt-in stub — mirror the host algorithm at FixedQ4816.cs:116-134 \
         bit-for-bit (see this function's doc comment) before using it in a shipped addon"
    );
}

/// Floor square root in fixed point — the host's exact algorithm (`FixedQ4816.cs:290-299`),
/// shipped as a **documented opt-in stub**. To implement bit-for-bit: for `value <= 0` return
/// zero; otherwise widen `(value as u64) << 16` to `u128` (80 bits of headroom is required — a
/// 64-bit shift-then-widen loses the top bits) and take the exact integer (floor) square root —
/// stable Rust has no `u128::isqrt`, so this needs a branchless bit-by-bit restoring square root,
/// not a hardware float `sqrt` (floor-sqrt has exactly one correct integer answer; any
/// provably-exact algorithm is acceptable, but an approximate one is not).
///
/// # Panics
/// Always — this is an unimplemented opt-in stub. See the doc comment above for the algorithm.
#[must_use]
pub fn sqrt(_value: i64) -> i64 {
    unimplemented!(
        "FixedQ4816 sqrt is an opt-in stub — mirror the host algorithm at FixedQ4816.cs:290-299 \
         bit-for-bit (see this function's doc comment) before using it in a shipped addon"
    );
}

/// Angle from the positive X axis to `(x, y)`, in fixed-point radians in `(-pi, pi]` — the host's
/// exact 16-iteration CORDIC vectoring loop (`FixedQ4816.cs:305-338`), shipped as a **documented
/// opt-in stub**. `HALF_PI_RAW` and `CORDIC_ATAN_TABLE` above are the exact constants to use. To
/// implement bit-for-bit: if both are zero, return zero; fold left-half-plane inputs into the
/// right half-plane by swapping and negating the coordinates and adding/subtracting
/// `HALF_PI_RAW` (tracking which quarter-turn was folded in); then for `i` in `0..16`, in order:
/// `direction = if y >= 0 { 1 } else { -1 }`, `x' = x + direction*(y >> i)`,
/// `y' = y - direction*(x >> i)`, `z += direction * CORDIC_ATAN_TABLE[i]`.
///
/// # Panics
/// Always — this is an unimplemented opt-in stub. See the doc comment above for the algorithm.
#[must_use]
pub fn atan2(_y: i64, _x: i64) -> i64 {
    unimplemented!(
        "FixedQ4816 atan2 is an opt-in stub — mirror the host algorithm and constants at \
         FixedQ4816.cs:305-338 bit-for-bit (see this function's doc comment) before using it in a \
         shipped addon"
    );
}
