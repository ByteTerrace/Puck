//! Known-answer vectors for `fixed.rs`'s hand-written functions, run on the host target via
//! `cargo test --target <host-triple>` (see the crate README — `.cargo/config.toml` pins the
//! default *build* target to `wasm32-unknown-unknown`, so tests need an explicit override to
//! execute; `rustc -vV` prints the host triple under `host:`).
//!
//! The `mul`, `div`, and `sqrt` vectors below are independently derived from `FixedQ4816.cs`'s own
//! algorithms — full-width product/quotient, round-half-to-even inspecting the *truncated*
//! result's low bit for the tie (never `+ 0.5`), and the exact integer floor for `sqrt` — and
//! double-checked by hand; they are the bit-for-bit contract this file exists to pin down. If a
//! future edit to `mul`/`div`/`sqrt` changes any of these, the host and the guest have diverged —
//! fix the guest function, do not fix the test. `atan2`/`sin`/`cos`/`exp2`/`log2`/`pow` have no
//! vectors here: they are re-exports of `fixed_generated.rs` (see `fixed.rs`'s module doc), whose
//! own known-answer vectors live in `fixed_vectors.rs`.

use crate::fixed;

#[test]
fn mul_identity_is_one() {
    assert_eq!(fixed::mul(fixed::ONE, fixed::ONE), fixed::ONE);
}

#[test]
fn mul_negative_identity() {
    assert_eq!(fixed::mul(fixed::ONE, fixed::NEGATIVE_ONE), fixed::NEGATIVE_ONE);
    assert_eq!(fixed::mul(fixed::NEGATIVE_ONE, fixed::NEGATIVE_ONE), fixed::ONE);
}

#[test]
fn mul_half_times_half_is_exact_quarter() {
    // 0.5 * 0.5 = 0.25, with zero remainder — no rounding branch exercised, a sanity baseline
    // before the tie cases below.
    let half = fixed::ONE / 2; // 32768, exact — Q16 halves are exact in raw i64 division
    let quarter = fixed::ONE / 4; // 16384

    assert_eq!(fixed::mul(half, half), quarter);
}

#[test]
fn mul_tie_rounds_up_when_truncated_is_odd() {
    // x=3, y=32768 (0.5 in real terms): product = 98304 = 1.5 * 65536 exactly — an exact tie.
    // The truncated integer part (1) is odd, so ties-to-even rounds AWAY from it, up to 2.
    assert_eq!(fixed::mul(3, 32_768), 2);
}

#[test]
fn mul_tie_rounds_down_when_truncated_is_even() {
    // x=1, y=32768: product = 32768 = 0.5 * 65536 exactly — an exact tie. The truncated integer
    // part (0) is even, so ties-to-even rounds TOWARD it, staying at 0. A naive "+0.5" rounder
    // (add half a ULP, then truncate) would instead compute (32768+32768)>>16 = 1 here — this is
    // the case that actually distinguishes ties-to-even from that naive rounding.
    assert_eq!(fixed::mul(1, 32_768), 0);
}

#[test]
fn mul_tie_negative_operand_rounds_up_by_magnitude() {
    // Mirrors mul_tie_rounds_up_when_truncated_is_odd with a negated operand: the tie-break
    // operates on the non-negative magnitude, then the sign is re-applied — so this must equal
    // -2, not some other rounding of the negative product directly.
    assert_eq!(fixed::mul(-3, 32_768), -2);
}

#[test]
fn mul_tie_negative_operand_rounds_down_by_magnitude() {
    // Mirrors mul_tie_rounds_down_when_truncated_is_even with a negated operand: magnitude ties
    // to 0, and -0 collapses to plain 0 in two's-complement i64.
    assert_eq!(fixed::mul(-1, 32_768), 0);
}

#[test]
fn mul_below_half_ulp_truncates_without_rounding() {
    // remainder (1) < HALF_ULP (32768): no rounding at all, truncated stays 0.
    assert_eq!(fixed::mul(1, 1), 0);
}

#[test]
fn mul_above_half_ulp_always_rounds_up_regardless_of_parity() {
    // x=1, y=49152 (0.75 real): product = 49152, remainder 49152 > HALF_ULP (32768) — rounds up
    // unconditionally, independent of the truncated bit's parity (unlike the exact-tie cases
    // above).
    assert_eq!(fixed::mul(1, 49_152), 1);
}

#[test]
fn clamp_leaves_in_range_value_untouched() {
    assert_eq!(fixed::clamp(1_000, fixed::NEGATIVE_ONE, fixed::ONE), 1_000);
}

#[test]
fn clamp_saturates_to_maximum() {
    assert_eq!(fixed::clamp(100_000, -65_536, 65_536), 65_536);
}

#[test]
fn clamp_saturates_to_minimum() {
    assert_eq!(fixed::clamp(-100_000, -65_536, 65_536), -65_536);
}

#[test]
fn clamp_at_the_exact_boundaries_is_inclusive() {
    assert_eq!(fixed::clamp(65_536, -65_536, 65_536), 65_536);
    assert_eq!(fixed::clamp(-65_536, -65_536, 65_536), -65_536);
}

#[test]
fn add_sub_neg_round_trip() {
    assert_eq!(fixed::add(fixed::ONE, fixed::ONE), (2 * fixed::ONE));
    assert_eq!(fixed::sub(fixed::ONE, fixed::ONE), fixed::ZERO);
    assert_eq!(fixed::neg(fixed::ONE), fixed::NEGATIVE_ONE);
}

#[test]
fn neg_wraps_at_min_value_like_the_host() {
    // FixedQ4816's unary negation is `unchecked(-value.Value)`; two's-complement negation of
    // i64::MIN overflows back to itself. `wrapping_neg` must match that exactly.
    assert_eq!(fixed::neg(i64::MIN), i64::MIN);
}

#[test]
fn cmp_orders_like_the_real_number_line() {
    use core::cmp::Ordering;

    assert_eq!(fixed::cmp(fixed::NEGATIVE_ONE, fixed::ONE), Ordering::Less);
    assert_eq!(fixed::cmp(fixed::ONE, fixed::NEGATIVE_ONE), Ordering::Greater);
    assert_eq!(fixed::cmp(fixed::ZERO, fixed::ZERO), Ordering::Equal);
}

// --- div ---------------------------------------------------------------------------------------
//
// `div`'s algorithm: widen `x`'s magnitude to 128 bits, shift left 16 (`dividend = |x| << 16`),
// divide by `|y|` to get `quotient`/`remainder`, round `quotient` to nearest with ties to even
// (comparing `remainder` against `|y| - remainder`, rounding up on `>` or on `==` with an odd
// `quotient`), then reapply the combined sign of `x` and `y`.

#[test]
fn div_identity_is_one() {
    // |x|=65536, |y|=65536: dividend = 65536<<16 = 4294967296, quotient = 65536, remainder = 0 —
    // exact, no rounding.
    assert_eq!(fixed::div(fixed::ONE, fixed::ONE), fixed::ONE);
}

#[test]
fn div_negative_identity() {
    // Same magnitudes as above (both exact); only the combined sign differs.
    assert_eq!(fixed::div(fixed::ONE, fixed::NEGATIVE_ONE), fixed::NEGATIVE_ONE);
    assert_eq!(fixed::div(fixed::NEGATIVE_ONE, fixed::NEGATIVE_ONE), fixed::ONE);
}

#[test]
fn div_tie_rounds_up_when_quotient_is_odd() {
    // x=3, y=131072 (2.0 in real terms): dividend = 3<<16 = 196608. 196608 / 131072 = 1 remainder
    // 65536. |y| - remainder = 131072 - 65536 = 65536 = remainder — an exact tie. The quotient (1)
    // is odd, so ties-to-even rounds away from it, up to 2.
    assert_eq!(fixed::div(3, 131_072), 2);
}

#[test]
fn div_tie_rounds_down_when_quotient_is_even() {
    // x=1, y=131072: dividend = 65536. 65536 / 131072 = 0 remainder 65536. |y| - remainder =
    // 131072 - 65536 = 65536 = remainder — an exact tie. The quotient (0) is even, so ties-to-even
    // rounds toward it, staying at 0.
    assert_eq!(fixed::div(1, 131_072), 0);
}

#[test]
fn div_tie_negative_numerator_rounds_up_by_magnitude() {
    // Mirrors div_tie_rounds_up_when_quotient_is_odd with x negated: the tie-break operates on the
    // non-negative magnitude (quotient 1, odd, rounds up to 2), then the combined sign (x negative,
    // y positive) is reapplied, giving -2.
    assert_eq!(fixed::div(-3, 131_072), -2);
}

#[test]
fn div_tie_negative_numerator_rounds_down_by_magnitude() {
    // Mirrors div_tie_rounds_down_when_quotient_is_even with x negated: magnitude ties to 0, and
    // -0 collapses to plain 0 in two's-complement i64.
    assert_eq!(fixed::div(-1, 131_072), 0);
}

#[test]
fn div_tie_negative_divisor_rounds_up_by_magnitude() {
    // Same magnitudes as div_tie_rounds_up_when_quotient_is_odd (quotient 1, odd, rounds up to 2),
    // but the divisor is negated instead of the numerator — same combined sign (exactly one of
    // x/y negative), so the result is again -2.
    assert_eq!(fixed::div(3, -131_072), -2);
}

#[test]
fn div_below_half_ulp_rounds_down_regardless_of_parity() {
    // x=1, y=196608 (3.0 in real terms): dividend = 65536. 65536 / 196608 = 0 remainder 65536.
    // |y| - remainder = 196608 - 65536 = 131072. remainder (65536) < 131072 — below the halfway
    // point, so the quotient (0) is not rounded up.
    assert_eq!(fixed::div(1, 196_608), 0);
}

#[test]
fn div_above_half_ulp_rounds_up_regardless_of_parity() {
    // x=2, y=196608: dividend = 131072. 131072 / 196608 = 0 remainder 131072. |y| - remainder =
    // 196608 - 131072 = 65536. remainder (131072) > 65536 — past the halfway point, so the
    // quotient (0) rounds up to 1 unconditionally, independent of parity.
    assert_eq!(fixed::div(2, 196_608), 1);
}

#[test]
fn div_by_one_is_identity_at_extreme_magnitudes() {
    // Dividing by ONE (65536) always divides dividend = |x|<<16 by exactly 65536, undoing the
    // shift exactly: quotient = |x|, remainder = 0, no rounding, for any x — including the two
    // representable extremes.
    assert_eq!(fixed::div(i64::MAX, fixed::ONE), i64::MAX);
    assert_eq!(fixed::div(i64::MIN, fixed::ONE), i64::MIN);
}

#[test]
fn div_min_value_by_negative_one_wraps_like_the_host() {
    // |i64::MIN| as u64 is 2^63; dividend = 2^63 << 16 = 2^79, |y| = 65536 = 2^16, quotient =
    // 2^79 / 2^16 = 2^63 exactly (remainder 0). As an i64 bit pattern, 2^63 is i64::MIN. The
    // combined sign of MIN (negative) and -1 (negative) is positive (XOR of two negatives), so no
    // sign flip is applied — but "no sign flip" on a bit pattern that already reads as i64::MIN
    // leaves it at i64::MIN. This mirrors the host's wrapping division operator: MIN / -1
    // mathematically overflows i64, and the host wraps rather than throwing OverflowException
    // (that's reserved for `operator checked /`).
    assert_eq!(fixed::div(i64::MIN, fixed::NEGATIVE_ONE), i64::MIN);
}

#[test]
#[should_panic(expected = "division by zero")]
fn div_by_zero_panics() {
    let _ = fixed::div(fixed::ONE, 0);
}

// --- sqrt ----------------------------------------------------------------------------------
//
// `sqrt`'s algorithm: `value <= 0` is zero; otherwise the result is the exact integer floor of
// `sqrt((value as u64 as u128) << 16)`. Every expected value below is derived from that widened
// integer, either because it widens to a perfect square or by bounding it between two consecutive
// squares.

#[test]
fn sqrt_of_zero_and_negatives_is_zero() {
    assert_eq!(fixed::sqrt(0), 0);
    assert_eq!(fixed::sqrt(-1), 0);
    assert_eq!(fixed::sqrt(fixed::NEGATIVE_ONE), 0);
    assert_eq!(fixed::sqrt(i64::MIN), 0);
}

#[test]
fn sqrt_of_one_is_one() {
    // value = ONE = 65536 = 2^16. Widened: 2^16 << 16 = 2^32, a perfect square: sqrt(2^32) =
    // 2^16 = 65536 = ONE.
    assert_eq!(fixed::sqrt(fixed::ONE), fixed::ONE);
}

#[test]
fn sqrt_of_four_is_two() {
    // value = 4*ONE = 262144 = 2^18. Widened: 2^18 << 16 = 2^34, a perfect square: sqrt(2^34) =
    // 2^17 = 131072 = 2*ONE.
    assert_eq!(fixed::sqrt(4 * fixed::ONE), 2 * fixed::ONE);
}

#[test]
fn sqrt_of_nine_is_three() {
    // value = 9*ONE = 589824 = 9 * 2^16. Widened: 9 * 2^16 << 16 = 9 * 2^32 = (3 * 2^16)^2, a
    // perfect square: sqrt(9 * 2^32) = 3 * 2^16 = 196608 = 3*ONE. Unlike the power-of-two cases
    // above, 9 is not itself a power of two, so this exercises the restoring-division loop's
    // odd-factor branches rather than only shift-aligned bits.
    assert_eq!(fixed::sqrt(9 * fixed::ONE), 3 * fixed::ONE);
}

#[test]
fn sqrt_of_small_non_perfect_square_floors_correctly() {
    // value = 3. Widened: 3 << 16 = 196608. 443^2 = 196249 <= 196608 < 197136 = 444^2 (443*443:
    // 443*400=177200, 443*43=19049, sum=196249; 444*444 = 443*444+444 = 196692+444 = 197136), so
    // floor(sqrt(196608)) = 443.
    assert_eq!(fixed::sqrt(3), 443);
}

#[test]
fn sqrt_at_the_hosts_128_bit_widening_boundary() {
    // value = 1<<48 — exactly the boundary where the host's 64-bit shift-then-sqrt path would
    // lose bits and it switches to the 128-bit path. Widened: 2^48 << 16 = 2^64, a perfect
    // square: sqrt(2^64) = 2^32.
    assert_eq!(fixed::sqrt(1i64 << 48), 1i64 << 32);
}

#[test]
fn sqrt_above_the_128_bit_widening_boundary_floors_correctly() {
    // value = (1<<50) + (1<<17). Widened: ((2^50 + 2^17) << 16) = 2^66 + 2^33 = k^2 + k for
    // k = 2^33 (k^2 = 2^66). floor(sqrt(k^2 + k)) = k for every k >= 0, because k^2 <= k^2 + k
    // (k >= 0) and k^2 + k < (k+1)^2 = k^2 + 2k + 1 (k < 2k+1 for every k >= 0). So the exact
    // floor here is k = 2^33, well above the 2^48 boundary and not a perfect square itself.
    assert_eq!(fixed::sqrt((1i64 << 50) + (1i64 << 17)), 1i64 << 33);
}
