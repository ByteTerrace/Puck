//! Puck's default addon — the dead-reckoning clamp-walk ghost that ships with the engine, and the
//! worked example for authoring your own addon against `puck-stdlib`.
//!
//! An addon crate carries only three things: the frozen `#[no_mangle]` ABI export shims below, each
//! a one-line delegate into `puck_stdlib::abi`/[`channels`]; the
//! [`channels!`](puck_stdlib::channels) table declaring the channel names this addon emits acts
//! against — which is also the `Input` channel's verb table — and the tick behaviour in [`on_tick`]
//! (plus, optionally, [`on_init`]), written against `puck_stdlib`'s typed [`puck_stdlib::Inputs`]
//! reader and [`puck_stdlib::Outputs`] writer, never a raw byte offset. Everything reusable — the
//! ABI machinery, the channel-name declaration API, the `FixedQ4816` mirror — lives in
//! `puck-stdlib`; this crate is the thin, shippable `cdylib` on top of it.
//!
//! **Why the exports live here and not in `puck-stdlib`:** `puck-stdlib` is an `rlib`, and a linker
//! is free to drop a symbol from a statically-linked `rlib` if nothing in the final crate graph
//! references it. Declaring `#[no_mangle] extern "C"` functions in the library would risk them
//! silently vanishing from this crate's compiled `.wasm` with no compile error — only a missing
//! export the host's load-time pre-flight then rejects. Keeping the frozen export names in the
//! `cdylib` that actually produces the shipped module sidesteps that risk entirely; verify with
//! `wasm-tools print target/wasm32-unknown-unknown/release/puck_addon_default.wasm | grep
//! '(export'` rather than assuming.
//!
//! **What the example below demonstrates, deliberately in this order:** an addon starts with no
//! authority at all. It cannot enumerate bodies — enumeration is itself a capability — so it waits
//! for the host to disclose what its principal was granted, learns a Drive handle and the body
//! index behind it, ASKS for Observe over that same body, queries that body's pose through the
//! granted Observe handle, and only then dead-reckons a walk toward a landmark, finishing with one
//! jump. Every one of those steps can be refused, and a refusal is a cell carrying a verdict — so
//! with no pose the ghost simply does not move, which is the honest behaviour and not a fallback
//! path.

use puck_stdlib::{
    abi, fixed, Handle, InCellKind, Inputs, Outputs, CAP_DRIVE, CAP_OBSERVE,
    OBSERVATION_VERB_GRANTED_BODY, REQUEST_VERB_BODY_POSE_ANSWER_PARTS,
};

puck_stdlib::channels! {
    static channels;
    /// Camera-relative facing-axis speed — the host applies this along the body's FACING vector.
    const FORWARD: Bipolar = "forward";
    /// Camera-relative right-axis speed — the host applies this along the body's +X right vector.
    const STRAFE: Bipolar = "strafe";
    /// The jump this ghost presses once it reaches its target — the primary action lane, which
    /// every grounded kit binds. PER-TICK: pressed only on the one tick this addon emits it.
    const JUMP: Binary = "jump";
}

/// Exact-match ABI version handshake — a stale-artifact detector: a `.wasm` built against an older
/// shape faults at load rather than being decoded by a host that no longer speaks it.
#[no_mangle]
pub extern "C" fn puck_abi_version() -> i32 {
    abi::ABI_VERSION
}

/// Byte offset of the guest→host output ring.
#[no_mangle]
pub extern "C" fn puck_out_ptr() -> i32 {
    abi::out_ptr()
}

/// Count of 32-byte output cells reserved at `puck_out_ptr()`.
#[no_mangle]
pub extern "C" fn puck_out_cap() -> i32 {
    abi::out_cap()
}

/// Byte offset of the host→guest input ring.
#[no_mangle]
pub extern "C" fn puck_in_ptr() -> i32 {
    abi::in_ptr()
}

/// Count of 32-byte input cells reserved at `puck_in_ptr()` — also the host's per-batch budget.
#[no_mangle]
pub extern "C" fn puck_in_cap() -> i32 {
    abi::in_cap()
}

/// Byte offset of this module's channel descriptor table. The stdlib lays the table out; this shim
/// supplies the one thing the stdlib cannot know — where this crate's declared channel-name table
/// lives, which is the `Input` channel's verb table.
#[no_mangle]
pub extern "C" fn puck_channels_ptr() -> i32 {
    abi::channels_ptr(channels::ptr(), channels::count())
}

/// Count of channel descriptors at `puck_channels_ptr()` — `Input`, `Request`, `Response`.
#[no_mangle]
pub extern "C" fn puck_channels_count() -> i32 {
    abi::channels_count()
}

/// Optional guest setup hook — called once at the end of mount, after the lane is bound, the
/// capability mask attenuated, and quota reserved, and before the first `puck_on_tick`.
#[no_mangle]
pub extern "C" fn puck_init() {
    on_init();
}

/// The ONE call the host makes every sim tick, carrying the number of input cells it wrote.
/// Delegates entirely to `puck_stdlib::abi::dispatch_tick`, which builds the typed views, calls
/// `on_tick` below, and returns the output-cell count.
#[no_mangle]
pub extern "C" fn puck_on_tick(input_count: i32) -> i32 {
    abi::dispatch_tick(input_count, on_tick)
}

// --- The default addon: the ghost's dead-reckoning clamp-walk -------------------------------

// "boulder-1" in the `default` world's scene sat at local (X=-1.2, Z=-0.3) — a visible landmark a
// short, clear walk from body:1's spawn (seat-2, (-3, 0, 2)) — before that world was retired under
// the 2026-08-06 four-world charter (no shipped world mounts this addon today; it ships as an
// example crate). The raw FixedQ4816 bits below are FixedQ4816::from_double(-1.2) /
// FixedQ4816::from_double(-0.3) computed by hand: -1.2 * 65536 = -78643.2, rounded to the nearest
// integer = -78643; -0.3 * 65536 = -19660.8, rounded to the nearest integer = -19661. A pose
// answer's position lanes speak the same frame the old snapshot did, so these are unchanged.
const TARGET_X_RAW: i64 = -78_643;
const TARGET_Z_RAW: i64 = -19_661;

// The proximity-to-jump range, per axis (~1.8 in Q16): 1.8 * 65536 = 117964.8, rounded = 117965.
const PROXIMITY_RAW: i64 = 117_965;

// A body pose answers in four parts — (posX, posY), (posZ, 0), (quatX, quatY), (quatZ, quatW) —
// and every part carries the same allowed verdict, a zero handle (a pose grants nothing) and its
// 0-based part index in the verb. Taken from the generated pin rather than written as 4, so a
// re-pin of the answer shape moves this with it.
const POSE_PART_COUNT: usize = REQUEST_VERB_BODY_POSE_ANSWER_PARTS as usize;
const POSE_PARTS_COMPLETE: u32 = (1u32 << POSE_PART_COUNT) - 1;

/// Everything this addon remembers between ticks, in one place so the single `static mut` below is
/// read and written by VALUE — a plain copy in at the top of the tick and a copy back at the
/// bottom, never a reference into the static.
#[derive(Clone, Copy)]
struct State {
    /// The Drive handle the host disclosed, and the body index behind it. Both are replaced
    /// wholesale by every disclosure push and are the addon's only route to acting at all.
    drive: Option<Handle>,
    body_index: Option<i64>,
    /// The Observe handle minted in answer to this addon's own `Ask`. Deliberately obtained by
    /// asking rather than by consuming an Observe disclosure — the shipped example exercises the
    /// ask path, attenuation, and both answer outcomes on purpose.
    observe: Option<Handle>,
    /// The last complete pose the host answered: (X, Y, Z), raw `FixedQ4816` bits. The walk needs
    /// position only; the orientation parts are read and dropped.
    pose: Option<(i64, i64, i64)>,
    /// Ordinals of the questions asked last tick, whose answers arrive this tick.
    pending_ask: Option<u16>,
    pending_pose: Option<u16>,
    /// The four (A, B) lane pairs of a pose answer, and a bitmask of the parts seen. Both are
    /// per-batch: parts arrive whole, contiguous and ascending inside one batch, so the mask is
    /// cleared at the start of every drain and never carried across ticks.
    pose_parts: [(i64, i64); POSE_PART_COUNT],
    pose_parts_seen: u32,
    /// Whether this ghost has already pressed jump once. Jump acts are PER-TICK and carry no phase
    /// — the host holds no lane state between ticks — so a "pressed once, never again" jump needs
    /// only this one latch: press it the single tick `close` first turns true, then never emit the
    /// act again (the host's own per-tick default is unpressed, so silence already means released).
    jumped: bool,
}

impl State {
    const EMPTY: Self = Self {
        drive: None,
        body_index: None,
        observe: None,
        pose: None,
        pending_ask: None,
        pending_pose: None,
        pose_parts: [(0, 0); POSE_PART_COUNT],
        pose_parts_seen: 0,
        jumped: false,
    };
}

// Like the ABI regions in puck-stdlib's abi.rs, this is only ever touched from the single sim-tick
// thread that calls puck_on_tick.
static mut STATE: State = State::EMPTY;

/// Guest setup hook. This addon needs no setup — and could not usefully emit here anyway: the host
/// zeroes the output ring before every tick, so anything written from init is erased unread.
fn on_init() {}

/// Called once per sim tick with the cells the host wrote and a writer for the cells this addon
/// returns. Replace this body with your own addon's behaviour if you're using this crate as a
/// worked example; the two-phase shape — drain everything the host said, THEN decide what to say —
/// is worth keeping, because deciding mid-drain means deciding on a partial batch.
fn on_tick(inputs: &Inputs, outputs: &mut Outputs) {
    // SAFETY: single sim-tick thread, no re-entrancy — see puck_stdlib::abi::dispatch_tick's safety
    // note. State is Copy, so this is a value read and the value write at the end of the function
    // is the only mutation; no reference to the static ever exists.
    let mut state = unsafe { STATE };

    // Part assembly is per-batch by contract, so last tick's mask can only mislead this one.
    state.pose_parts_seen = 0;

    let mut disclosed = false;

    for cell in inputs.iter() {
        match cell.kind {
            // Carries the engine tick and nothing this addon needs: it dead-reckons off pose
            // answers, not off a clock.
            Some(InCellKind::Tick) => {}
            Some(InCellKind::Observation) => {
                if cell.verb != OBSERVATION_VERB_GRANTED_BODY as u8 {
                    continue;
                }

                if !disclosed {
                    // First disclosure cell of this batch. A push is never split across batches, so
                    // this batch's cells are the COMPLETE new authoritative set — everything the
                    // last push established is dropped here rather than merged, and anything the
                    // new set omits is simply gone. The in-flight questions go with it: they were
                    // asked against a projection that no longer exists.
                    disclosed = true;
                    state.drive = None;
                    state.body_index = None;
                    state.pending_ask = None;
                    state.pending_pose = None;
                }

                // Only the Drive disclosure is folded in. An Observe disclosure may well arrive
                // beside it, and this addon deliberately ignores it and asks for Observe instead —
                // the shipped example is where the ask path has to be exercised.
                if cell.a == CAP_DRIVE as i64 {
                    state.drive = Some(cell.handle);
                    state.body_index = Some(cell.b);
                }
            }
            Some(InCellKind::Answer) => {
                let allowed = matches!(cell.verdict, Some(verdict) if verdict.is_allowed());

                if state.pending_ask == Some(cell.ordinal) {
                    // A refusal carries its verdict and no payload; leaving `observe` as None is
                    // the whole of the response, and the ghost then never moves.
                    if allowed {
                        state.observe = Some(cell.handle);
                    }

                    state.pending_ask = None;
                } else if (state.pending_pose == Some(cell.ordinal)) && allowed {
                    // Handle fields are zero on a pose answer — a pose grants nothing — so the
                    // correlation is ordinal plus part index, never a handle.
                    let part = cell.verb as usize;

                    if part < POSE_PART_COUNT {
                        state.pose_parts[part] = (cell.a, cell.b);
                        state.pose_parts_seen |= 1u32 << part;
                    }

                    if (part + 1) == POSE_PART_COUNT {
                        if state.pose_parts_seen == POSE_PARTS_COMPLETE {
                            state.pose = Some((
                                state.pose_parts[0].0,
                                state.pose_parts[0].1,
                                state.pose_parts[1].0,
                            ));
                        }

                        state.pending_pose = None;
                    }
                } else if state.pending_pose == Some(cell.ordinal) {
                    // Refused outright: one zero-payload cell, and no pose this tick.
                    state.pending_pose = None;
                }
            }
            // A cell kind this build has no name for. Skipping it is the only honest reading — a
            // guess would be inventing a fact the host did not send.
            None => {}
        }
    }

    if disclosed && state.drive.is_none() {
        // The new set no longer grants this addon a body to drive, so everything derived from the
        // old one goes with it: an Observe handle minted over a body this principal no longer holds
        // is authority it should not keep using, and a stale pose would keep the ghost walking.
        state.observe = None;
        state.pose = None;
    }

    // Re-ask on every disclosure push and never otherwise — the disclosure is the only trigger,
    // which keeps the ask rate a function of host events rather than of elapsed time.
    if disclosed {
        if let Some(body_index) = state.body_index {
            state.pending_ask = Some(outputs.ask_body(body_index, CAP_OBSERVE));
        }
    }

    // Asked every tick: a pose is a per-tick fact, and an answer that never comes back was starved
    // by the batch budget rather than denied, which makes re-asking the correct response either
    // way.
    if let Some(observe) = state.observe {
        state.pending_pose = Some(outputs.query_pose(observe));
    }

    if let (Some((pos_x, _, pos_z)), Some(drive)) = (state.pose, state.drive) {
        let dx = fixed::sub(TARGET_X_RAW, pos_x);
        let dz = fixed::sub(TARGET_Z_RAW, pos_z);

        // Per-axis clamp only — no mul/div/sqrt/normalize needed; a per-axis clamp already
        // guarantees each component's magnitude is <= 1 (which is also the host's pinned domain for
        // a Bipolar act, violated at the cost of a faulted instance), and the sim clamps overall
        // move magnitude anyway.
        //
        // STRAFE and FORWARD are NOT symmetric here, and that is deliberate, not an oversight:
        // strafe drives MoveStrafe, which the host applies along the body's +X right vector
        // directly, so the raw error dx is already the right sign. Forward drives MoveAdvance,
        // which the host applies along the body's FACING vector — and facing is -Z at this addon's
        // fixed yaw (0), per PlayerCommandModule's `player.face` doc ("0 = facing -Z") and
        // WorldBody's grounded integration (`facing = orientation.Rotate(-UnitZ)`,
        // `planarTarget = facing * MoveAdvance`). So a POSITIVE forward value moves the body toward
        // -Z, and closing a NEGATIVE dz (the target sits further toward -Z than the body does)
        // needs a POSITIVE forward value: it is the sign-flipped error, `-dz`, not `dz` itself.
        let strafe = fixed::clamp(dx, fixed::NEGATIVE_ONE, fixed::ONE);
        let forward = fixed::clamp(fixed::neg(dz), fixed::NEGATIVE_ONE, fixed::ONE);

        // Movement is TWO acts, per-tick declarative: an addon that stops emitting either one stops
        // contributing on that axis, matching a seat's own analog-clear behaviour.
        outputs.act_bipolar(drive, channels::FORWARD, forward);
        outputs.act_bipolar(drive, channels::STRAFE, strafe);

        let close = (dx.abs() < PROXIMITY_RAW) && (dz.abs() < PROXIMITY_RAW);

        // Jump is PER-TICK with no phase: pressing it once means emitting the act on exactly one
        // tick and then never again — the host's own per-tick default (no act this tick) already
        // reads as released, so there is no release to emit.
        if close && !state.jumped {
            outputs.act_binary(drive, channels::JUMP, true);
            state.jumped = true;
        }
    }

    // SAFETY: as above — single sim-tick thread, no re-entrancy, value write.
    unsafe {
        STATE = state;
    }
}
