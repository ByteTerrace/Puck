//! **BATTERY-ONLY GUEST.** This module is never shipped and no world document pins it — it exists
//! solely so a battery can make the query-dispatch-budget refusal
//! ([`puck_stdlib::abi_generated::AddonVerdict::QuotaExhausted`], re-exported here as
//! [`puck_stdlib::Verdict::QuotaExhausted`]) demonstrable. The shipped default addon
//! (`puck-addon-default`) emits exactly ONE `query_pose` per tick and, under the stdlib's default
//! 16-cell input ring, can never fill the per-tick answer budget — so the refusal path it is capable
//! of exercising has no guest that actually walks it. This crate is that guest: it floods THREE
//! `query_pose` calls every tick against a small, deliberately DECLARED (not stdlib-default) input
//! capacity, so `Puck.World.Server.WorldAddonRuntime.MergeAnswers`'s per-tick budget — computed
//! host-side as `puck_in_cap() - 1` — is provably too small to answer all three in full.
//!
//! An addon crate carries only three things, same as the default addon: the frozen `#[no_mangle]`
//! ABI export shims below, each a one-line delegate into `puck_stdlib::abi`/[`sources`] (with ONE
//! deliberate exception — see [`puck_in_cap`] below); the [`sources!`](puck_stdlib::sources) table
//! declaring the engine source ids this addon emits against; and the tick behaviour in [`on_tick`].
//!
//! **Why the exports live here and not in `puck-stdlib`:** `puck-stdlib` is an `rlib`, and a linker
//! is free to drop a symbol from a statically-linked `rlib` if nothing in the final crate graph
//! references it. Declaring `#[no_mangle] extern "C"` functions in the library would risk them
//! silently vanishing from this crate's compiled `.wasm` with no compile error — only a missing
//! export the host's load-time pre-flight then rejects. Keeping the frozen export names in the
//! `cdylib` that actually produces the shipped module sidesteps that risk entirely; verify with
//! `wasm-tools print target/wasm32-unknown-unknown/release/puck_addon_queryspam.wasm | grep
//! '(export'` rather than assuming.
//!
//! **Why `puck_in_cap` is NOT a delegate to `puck_stdlib::abi::in_cap()` here, unlike every other
//! export shim:** that function is documented as "also the host's per-batch budget" — the host reads
//! this crate's own declared value and writes at most that many input cells per tick, and
//! `WorldAddonRuntime` derives its whole per-tick answer budget from it (`InputCellCapacity - 1`).
//! Declaring a SMALLER capacity than the stdlib's physically-reserved 16-cell ring is safe (the host
//! never writes past what this export claims, so the extra reserved bytes simply go unused) and is
//! the intended, guest-side lever for shrinking that budget — there is no host-side or `world.grant`
//! knob for it. [`IN_CAP_OVERRIDE`] = 10 gives a 9-cell answer budget: two full four-part pose
//! answers (8 cells) plus one `QuotaExhausted` refusal cell (1 cell) fill it exactly, so of the three
//! `query_pose` calls this addon issues every tick, the first two are answered in full and the THIRD
//! is refused with `QuotaExhausted` — deterministically, every steady-state tick, never a silent
//! drop (a refusal cell always fits: `remaining >= 1` holds right up to the budget's last cell).
//!
//! **What the example below demonstrates, deliberately in this order — mirroring the default
//! addon's manifest disclosure/ask flow but never its walk:** this addon requests Drive AND Observe
//! over the same body (so both may be disclosed), folds in the Drive disclosure exactly as the
//! default addon does, and separately remembers a body index from an Observe disclosure ONLY as a
//! fallback — the ask always prefers the Drive-disclosed body index, falling back to the
//! Observe-disclosed one solely when no Drive disclosure has arrived. It ASKS for Observe over that
//! body (never consuming a disclosed Observe handle directly, exactly as the default addon
//! deliberately exercises the ask path), and once granted, spams `query_pose` three times a tick.
//! It never emits a movement or digital act — this guest exists to make refusal traffic, not to
//! move anything, and a refusal is a cell carrying a verdict, never a fault: the addon keeps running
//! every tick regardless of how the host answers.

use puck_stdlib::{
    abi, Handle, InCellKind, Inputs, Outputs, CAP_DRIVE, CAP_OBSERVE,
    OBSERVATION_VERB_GRANTED_BODY,
};

puck_stdlib::channels! {
    static channels;
    /// Declared only because the `Input` channel's descriptor requires a nonempty verb table
    /// (`AddonChannelTableReader`'s `VerbCount` must lie in `[1, MaxChannelNames]`) — this addon
    /// never emits an act against it. Movement is deliberately out of scope for a query-flood guest.
    const FORWARD: Bipolar = "forward";
}

/// This crate's own declared input-ring capacity — see the module doc's third paragraph for why
/// this is a literal rather than a delegate to [`puck_stdlib::abi::in_cap`]. Chosen so the per-tick
/// answer budget (`IN_CAP_OVERRIDE - 1` = 9) exactly holds two four-part pose answers (8 cells) plus
/// one `QuotaExhausted` refusal cell (1 cell) — the third of this addon's three per-tick
/// `query_pose` calls is the one that overflows.
const IN_CAP_OVERRIDE: i32 = 10;

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
/// **Deliberately NOT `abi::in_cap()`** — see the module doc for why this crate declares a smaller
/// capacity than the stdlib's physically-reserved ring, and [`IN_CAP_OVERRIDE`] for the chosen
/// value. The physical ring backing `puck_in_ptr()` is still the stdlib's full 16 cells, so the host
/// writing up to this smaller declared count never reads or writes past the reserved region.
#[no_mangle]
pub extern "C" fn puck_in_cap() -> i32 {
    IN_CAP_OVERRIDE
}

/// Byte offset of this module's channel descriptor table.
#[no_mangle]
pub extern "C" fn puck_channels_ptr() -> i32 {
    abi::channels_ptr(channels::ptr(), channels::count())
}

/// Count of channel descriptors at `puck_channels_ptr()` — `Input`, `Request`, `Response`.
#[no_mangle]
pub extern "C" fn puck_channels_count() -> i32 {
    abi::channels_count()
}

/// Optional guest setup hook — this addon needs no setup, and could not usefully emit here anyway:
/// the host zeroes the output ring before every tick, so anything written from init is erased
/// unread.
#[no_mangle]
pub extern "C" fn puck_init() {}

/// The ONE call the host makes every sim tick, carrying the number of input cells it wrote.
/// Delegates entirely to `puck_stdlib::abi::dispatch_tick`, which builds the typed views, calls
/// `on_tick` below, and returns the output-cell count.
#[no_mangle]
pub extern "C" fn puck_on_tick(input_count: i32) -> i32 {
    abi::dispatch_tick(input_count, on_tick)
}

// --- The query-spam battery guest --------------------------------------------------------------

/// Everything this addon remembers between ticks, in one place so the single `static mut` below is
/// read and written by VALUE — a plain copy in at the top of the tick and a copy back at the
/// bottom, never a reference into the static. Mirrors the default addon's `State` shape, minus the
/// walk/jump fields this guest has no use for and minus pose-answer bookkeeping — this addon never
/// reads a pose value, only counts on the REFUSAL its flood provokes, so answer cells other than the
/// one pending `Ask` are simply drained and dropped.
#[derive(Clone, Copy)]
struct State {
    /// The Drive handle the host disclosed, and the body index behind it — folded in exactly as the
    /// default addon folds in its own Drive disclosure.
    drive: Option<Handle>,
    drive_body_index: Option<i64>,
    /// The body index an Observe disclosure names, kept ONLY as a fallback `Ask` target for when no
    /// Drive disclosure has arrived. The Observe HANDLE that same disclosure cell carries is
    /// deliberately dropped, never folded in — exactly as the default addon drops its own Observe
    /// disclosure, because this crate exercises the ask path on purpose.
    observe_disclosed_body_index: Option<i64>,
    /// The Observe handle minted in answer to this addon's own `Ask`. This is the ONLY route to an
    /// Observe handle this addon ever takes.
    observe: Option<Handle>,
    /// Ordinal of the in-flight `Ask`, whose answer arrives next tick.
    pending_ask: Option<u16>,
}

impl State {
    const EMPTY: Self = Self {
        drive: None,
        drive_body_index: None,
        observe_disclosed_body_index: None,
        observe: None,
        pending_ask: None,
    };
}

// Like the ABI regions in puck-stdlib's abi.rs, this is only ever touched from the single sim-tick
// thread that calls puck_on_tick.
static mut STATE: State = State::EMPTY;

/// Called once per sim tick with the cells the host wrote and a writer for the cells this addon
/// returns. Two-phase, as the default addon's own `on_tick`: drain everything the host said, THEN
/// decide what to say — deciding mid-drain means deciding on a partial batch.
fn on_tick(inputs: &Inputs, outputs: &mut Outputs) {
    // SAFETY: single sim-tick thread, no re-entrancy — see puck_stdlib::abi::dispatch_tick's safety
    // note. State is Copy, so this is a value read and the value write at the end of the function
    // is the only mutation; no reference to the static ever exists.
    let mut state = unsafe { STATE };

    let mut disclosed = false;

    for cell in inputs.iter() {
        match cell.kind {
            // Carries the engine tick and nothing this addon needs — the flood is driven by holding
            // an Observe handle, not by elapsed time.
            Some(InCellKind::Tick) => {}
            Some(InCellKind::Observation) => {
                if cell.verb != OBSERVATION_VERB_GRANTED_BODY as u8 {
                    continue;
                }

                if !disclosed {
                    // First disclosure cell of this batch: the COMPLETE new authoritative set, so
                    // everything the last push established is dropped here rather than merged (same
                    // rule the default addon's on_tick documents at its own disclosure branch).
                    disclosed = true;
                    state.drive = None;
                    state.drive_body_index = None;
                    state.observe_disclosed_body_index = None;
                    state.pending_ask = None;
                }

                if cell.a == CAP_DRIVE as i64 {
                    state.drive = Some(cell.handle);
                    state.drive_body_index = Some(cell.b);
                } else if cell.a == CAP_OBSERVE as i64 {
                    // Handle deliberately dropped — see the State doc on
                    // `observe_disclosed_body_index`.
                    state.observe_disclosed_body_index = Some(cell.b);
                }
            }
            Some(InCellKind::Answer) => {
                if state.pending_ask == Some(cell.ordinal) {
                    let allowed = matches!(cell.verdict, Some(verdict) if verdict.is_allowed());

                    // A refusal carries its verdict and no payload; leaving `observe` as None is the
                    // whole of the response, and the flood then simply does not start.
                    if allowed {
                        state.observe = Some(cell.handle);
                    }

                    state.pending_ask = None;
                }
                // Every other Answer this tick is either a pose-query part or the QuotaExhausted
                // refusal this addon exists to provoke — this guest reads neither payload nor
                // verdict from them, only their EXISTENCE matters, and that is proven at the engine
                // console / battery layer, not inside the guest.
            }
            // A cell kind this build has no name for. Skipping it is the only honest reading — a
            // guess would be inventing a fact the host did not send.
            None => {}
        }
    }

    // The ask target: prefer the Drive-disclosed body index, falling back to the Observe-disclosed
    // one only when no Drive disclosure has named a body at all.
    let target_body_index = state.drive_body_index.or(state.observe_disclosed_body_index);

    if disclosed && target_body_index.is_none() {
        // Nothing to ask over any more — the previously granted Observe handle (if any) is no
        // longer usable authority this addon can keep re-deriving, so it goes with the projection
        // that no longer names a body.
        state.observe = None;
    }

    // Re-ask on every disclosure push and never otherwise — the disclosure is the only trigger,
    // which keeps the ask rate a function of host events rather than of elapsed time. Matches the
    // default addon's own re-ask rule.
    if disclosed {
        if let Some(body_index) = target_body_index {
            state.pending_ask = Some(outputs.ask_body(body_index, CAP_OBSERVE));
        }
    }

    // The flood: once an Observe handle is held, spam THREE query_pose calls every tick — three
    // ordinals, deliberately more than the declared 9-cell answer budget (two full four-part pose
    // answers) can hold whole, so the third is refused with QuotaExhausted. Never a movement or
    // digital act: this guest exists to make refusal traffic, not to move anything.
    if let Some(observe) = state.observe {
        outputs.query_pose(observe);
        outputs.query_pose(observe);
        outputs.query_pose(observe);
    }

    // SAFETY: as above — single sim-tick thread, no re-entrancy, value write.
    unsafe {
        STATE = state;
    }
}
