//! Known-answer vectors for `fixed.rs`, run on the host target via `cargo test --target
//! <host-triple>` (see the crate README — `.cargo/config.toml` pins the default *build* target to
//! `wasm32-unknown-unknown`, so tests need an explicit override to execute; `rustc -vV` prints the
//! host triple under `host:`).
//!
//! The `mul` vectors below are independently derived from `FixedQ4816.cs`'s algorithm (full-width
//! `i128` product, round-half-to-even on the low 16 bits, inspecting the *truncated* result's low
//! bit for the tie — never `+ 0.5`) and double-checked by hand; they are the bit-for-bit contract
//! this file exists to pin down. If a future edit to `mul` changes any of these, the host and the
//! guest have diverged — fix `mul`, do not fix the test.

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
