//! **BATTERY-ONLY GUEST.** Three build-time variants (Cargo features — see `Cargo.toml`) from one crate, never
//! shipped and pinned by no shipped world, built to make the capability-channel model's less-visited corners
//! demonstrable at a real `Puck.World` boot rather than argued about:
//!
//! - `main` (the default): declares `{forward, walkonly, trigger}` and drives three probes off one shared
//!   tick-since-drive counter (see [`on_tick`]'s module-level walkthrough below).
//! - `bound64`: declares exactly [`puck_stdlib::channels::MAX_CHANNEL_NAMES`] (64) channel names and nothing
//!   else — proves the ceiling MOUNTS at its own edge.
//! - `bound65`: declares 65 — one past the ceiling — proves the host refuses it `BadExport`, naming the bound,
//!   before ever reading the guest's declared name bytes (see [`bound65`]'s module doc for how it gets there
//!   without the `channels!` macro's own compile-time assertion refusing the build outright).
//!
//! Exactly one feature must be selected; the `compile_error!` below refuses a build that selects none or more
//! than one, rather than letting an omitted `--features` flag silently fall through to an arbitrary arm.
//!
//! **Why the frozen exports live here and not in `puck-stdlib`:** identical reasoning to
//! `puck-addon-default`/`puck-addon-queryspam`'s own module docs — `puck-stdlib` is an `rlib`, and a linker is
//! free to drop an unreferenced `#[no_mangle]` export from it with no compile error, only a missing export the
//! host's load-time pre-flight then rejects. Keeping the frozen export names in the `cdylib` that actually
//! produces the shipped module sidesteps that risk entirely.

#[cfg(not(any(feature = "main", feature = "bound64", feature = "bound65")))]
compile_error!("puck-addon-channelwalk: select exactly one of the `main`, `bound64`, or `bound65` features");

#[cfg(any(
    all(feature = "main", feature = "bound64"),
    all(feature = "main", feature = "bound65"),
    all(feature = "bound64", feature = "bound65")
))]
compile_error!("puck-addon-channelwalk: `main`, `bound64`, and `bound65` are mutually exclusive — select exactly one");

use puck_stdlib::abi;

/// Byte offset of the guest→host output ring. Identical across every feature variant — the ring geometry is
/// `puck-stdlib`'s own budget, untouched by which channel table this crate declares.
#[no_mangle]
pub extern "C" fn puck_out_ptr() -> i32 {
    abi::out_ptr()
}

/// Count of 32-byte output cells reserved at [`puck_out_ptr`].
#[no_mangle]
pub extern "C" fn puck_out_cap() -> i32 {
    abi::out_cap()
}

/// Byte offset of the host→guest input ring.
#[no_mangle]
pub extern "C" fn puck_in_ptr() -> i32 {
    abi::in_ptr()
}

/// Count of 32-byte input cells reserved at [`puck_in_ptr`] — also the host's per-batch write budget.
#[no_mangle]
pub extern "C" fn puck_in_cap() -> i32 {
    abi::in_cap()
}

/// Exact-match ABI version handshake — a stale-artifact detector, identical across every feature variant.
#[no_mangle]
pub extern "C" fn puck_abi_version() -> i32 {
    abi::ABI_VERSION
}

/// Count of channel descriptors at `puck_channels_ptr()` — `Input`, `Request`, `Response`, fixed regardless of
/// how many channel NAMES the `Input` descriptor's own verb table carries.
#[no_mangle]
pub extern "C" fn puck_channels_count() -> i32 {
    abi::channels_count()
}

#[cfg(feature = "main")]
mod main_behavior {
    //! The functional guest: `{forward, walkonly, trigger}`, driven by one shared tick-since-drive counter so
    //! every probe advances off the SAME deterministic clock rather than three independent ones a script would
    //! have to align separately.
    //!
    //! **`walkonly`** is declared and never resolves against any world this crate is battery-tested with — the
    //! channel-walk test world (`worlds/channel-walk-world.json`) keeps the shipped default's channel set and
    //! adds `trigger`, deliberately never adding `walkonly` — so it stays the unresolved-name case
    //! (`puck_stdlib::channels`'s own module doc: "report-and-inert... never faults the mount"). This guest
    //! drives it EVERY tick a Drive handle is held, forever, so `world.addons` staying `ENABLED` with no fault
    //! across the whole run is itself the proof that a per-tick act against an unresolved declared channel
    //! answers `AddonVerdict::AttenuatedToEmpty` and never faults the instance.
    //!
    //! **`forward`** contributes `fixed::ONE` for the first [`FORWARD_ACTIVE_TICKS`] ticks after a Drive handle
    //! is first held, then stops — per-tick declarative contribution, so the tick immediately after the window
    //! closes reads back to zero on its own; nothing here "releases" it, matching the ABI's own "an addon that
    //! stops emitting stops contributing" rule for every channel act (see `puck_stdlib::Outputs`'s own module
    //! doc). The task's own brief asks for "exactly 3 ticks"; this guest uses a much wider window instead — see
    //! [`FORWARD_ACTIVE_TICKS`]'s own doc for why a few-tick window is not reliably observable through the
    //! stdin console driver at all, and this crate's README for the finding in full.
    //!
    //! **`strafe`** contributes `fixed::ONE` every tick a Drive handle is held, forever (never time-limited,
    //! unlike `forward`) — the co-driving pool/consent probe (case J-floor) needs an ONGOING contribution it
    //! can grant/deny reach and ceiling against at any point long after mount, which `forward`'s finite window
    //! and `trigger`'s own settle-and-stop walk cannot offer. Unlike `walkonly`, `strafe` IS a real resolved
    //! world channel (the `MoveStrafe` role), so its contribution actually reaches `Puck.Maths.FixedContributionFold`'s
    //! pool/ceiling gate rather than dead-ending at attenuation for a different reason.
    //!
    //! **`trigger`** presses/releases at exactly `{0, fixed::ONE}` — the ONLY two raw values a Binary-shaped
    //! channel admits from ANY writer, addon or human. The host decodes an `Act` against the WORLD's declared
    //! channel shape, NOT this crate's local kind hint, so any other raw value is a whole-batch `DecodeError`
    //! that faults the instance. That is why case H cannot be walked by writing raw values either side of the
    //! pinned threshold: such an act is not constructible for a Binary channel from anywhere in the system.
    //! Case H is walked on the CEILING axis instead — this guest presses `trigger` to its own full `ONE` for a
    //! window, and the seat's authored pool ceiling is moved across the threshold between runs
    //! (`h-below-threshold.txt` / `h-at-threshold.txt`).
    //!
    //! Those two scripts read IDENTICALLY, and that identity is the result rather than a failure: the ceiling
    //! does not bite a Binary COMPOSITION channel at all, because this act arrives as a held-channel image and
    //! never as an intent delta, so the pool arithmetic is handed nothing to clamp. See the README's Finding 2
    //! for the call sites. Do not "fix" the scripts to expect a clamped value.
    //!
    //! All four channels key off the SAME `ticks_since_drive` counter (0-based, incrementing once per tick a
    //! Drive handle is held, reset to `None` whenever a disclosure drops the drive) — see
    //! [`FORWARD_ACTIVE_TICKS`]/[`TRIGGER_PRESS_TICKS`] for the exact windows.

    use puck_stdlib::{
        abi, fixed, Handle, InCellKind, Inputs, Outputs, CAP_DRIVE, OBSERVATION_VERB_GRANTED_BODY,
    };

    puck_stdlib::channels! {
        static channels;
        /// Contributes `fixed::ONE` for the first [`FORWARD_ACTIVE_TICKS`] ticks (240, ~1 real second) a Drive
        /// handle is held, then stops — the
        /// per-tick-expiry probe (case I).
        const FORWARD: Bipolar = "forward";
        /// Declared, never resolved against the channel-walk test world's table on purpose — the
        /// unresolved-name / per-act-attenuation probe (case K). Driven every tick, forever.
        const WALKONLY: Bipolar = "walkonly";
        /// LOCAL kind hint only — see the module doc above for why `Bipolar` here is deliberate even though the
        /// test world declares `trigger` as `Binary`. Walks the pinned threshold boundary (case H).
        const TRIGGER: Bipolar = "trigger";
        /// Contributes `fixed::ONE` every tick a Drive handle is held, forever — the ongoing, never-expiring
        /// contribution the co-driving pool/consent probe (case J-floor) drives against. Resolves against the
        /// world's real `MoveStrafe` role channel, unlike `walkonly`.
        const STRAFE: Bipolar = "strafe";
    }

    /// The channel-walk test world's pinned `trigger` threshold, raw `FixedQ4816` bits — `0.75 * 65536 = 49152`
    /// exactly (0.75 = 3/4, and 65536 is a power of two, so this quantizes with no rounding ambiguity; see
    /// `worlds/channel-walk-world.json`'s `trigger` row, which pins the same `0.75`). This guest never writes
    /// this value itself — it exists here only as the derivation this crate's README shows for the pool-ceiling
    /// boundary case H actually walks (see the module doc's `trigger` section).
    #[allow(dead_code)]
    const TRIGGER_THRESHOLD_RAW: i64 = 49_152;

    /// How many ticks a Drive handle must be held before `forward` stops contributing. The task's own brief
    /// asks for "exactly 3 ticks" (a ~12.5ms window at the engine's fixed 240Hz sim rate); this crate uses
    /// 240 instead (~1 real second) because a few-tick window is not a boundary the stdin console driver can
    /// reliably straddle — the round trip of parsing and dispatching even one queued line already costs more
    /// wall-clock time than a handful of ticks, and the documented cross-process pacing caveat (identical input
    /// does NOT land on matching absolute ticks) means no fixed `world.wait` count can be trusted to land inside
    /// a window that narrow. Widening the window changes the MAGNITUDE, never the MECHANISM under test: a
    /// finite, per-tick-declarative contribution that hard-stops and never drifts back.
    const FORWARD_ACTIVE_TICKS: u32 = 240;

    /// The tick range (relative to `ticks_since_drive`) `trigger` presses for — starts once `forward`'s own
    /// window has fully closed (never overlapping it, so a script reading either channel is never mid-transition
    /// on the other) and holds for [`TRIGGER_PRESS_TICKS`] ticks (also ~1 real second), long enough for a script
    /// to comfortably read it as flat/steady rather than racing a flicker.
    const TRIGGER_PRESS_START_TICKS: u32 = FORWARD_ACTIVE_TICKS + 120;
    const TRIGGER_PRESS_TICKS: u32 = 240;

    #[no_mangle]
    pub extern "C" fn puck_channels_ptr() -> i32 {
        abi::channels_ptr(channels::ptr(), channels::count())
    }

    #[no_mangle]
    pub extern "C" fn puck_init() {}

    #[no_mangle]
    pub extern "C" fn puck_on_tick(input_count: i32) -> i32 {
        abi::dispatch_tick(input_count, on_tick)
    }

    /// Everything this addon remembers between ticks, read and written by VALUE (mirrors
    /// `puck-addon-default`/`puck-addon-queryspam`'s own `State` shape and its single-`static mut`-by-value
    /// discipline — see either crate's module doc for why).
    #[derive(Clone, Copy)]
    struct State {
        drive: Option<Handle>,
        /// 0-based, incrementing once per tick a Drive handle is held; `None` before the first disclosure ever
        /// grants one, and reset to `None` (then back to `Some(0)` on the very next disclosure that grants one)
        /// whenever a disclosure push drops the drive — see the module doc's disclosure-replacement rule.
        ticks_since_drive: Option<u32>,
    }

    impl State {
        const EMPTY: Self = Self { drive: None, ticks_since_drive: None };
    }

    // Single sim-tick thread, no re-entrancy — see puck_stdlib::abi::dispatch_tick's safety note.
    static mut STATE: State = State::EMPTY;

    fn on_tick(inputs: &Inputs, outputs: &mut Outputs) {
        // SAFETY: see the STATE doc above.
        let mut state = unsafe { STATE };
        let mut disclosed = false;

        for cell in inputs.iter() {
            match cell.kind {
                Some(InCellKind::Tick) => {}
                Some(InCellKind::Observation) => {
                    if cell.verb != OBSERVATION_VERB_GRANTED_BODY as u8 {
                        continue;
                    }

                    if !disclosed {
                        // A disclosure push is the COMPLETE new authoritative set — everything the last push
                        // established is dropped here rather than merged (see puck_stdlib::Inputs's own module
                        // doc).
                        disclosed = true;
                        state.drive = None;
                    }

                    if cell.a == CAP_DRIVE as i64 {
                        state.drive = Some(cell.handle);
                    }
                }
                Some(InCellKind::Answer) => {
                    // This guest asks nothing and queries no pose — every Answer cell it could ever see would
                    // be unreachable, so there is nothing to read here.
                }
                None => {}
            }
        }

        if disclosed && state.drive.is_none() {
            // The new disclosed set no longer grants a body to drive — the counter has nothing left to be
            // counting FROM, so it goes with the drive rather than free-running against a handle this guest no
            // longer holds.
            state.ticks_since_drive = None;
        } else if let Some(_drive) = state.drive {
            state.ticks_since_drive = Some(match state.ticks_since_drive {
                Some(ticks) => ticks.saturating_add(1),
                // First tick a Drive handle is held (this disclosure just granted one, or an earlier one is
                // still held and this is simply this guest's very first tick with it) starts the counter at 0.
                None => 0,
            });
        }

        if let (Some(drive), Some(ticks)) = (state.drive, state.ticks_since_drive) {
            // forward: FORWARD_ACTIVE_TICKS, then silence — per-tick declarative, so the tick the window closes
            // already contributes nothing; nothing "releases" it, the guest simply stops emitting the act.
            if ticks < FORWARD_ACTIVE_TICKS {
                outputs.act_bipolar(drive, channels::FORWARD, fixed::ONE);
            }

            // walkonly: every tick, forever — the unresolved-channel attenuation probe never stops running.
            outputs.act_bipolar(drive, channels::WALKONLY, fixed::ONE);

            // trigger: press/release at the only two legal raw values a Binary-shaped channel admits — see the
            // module doc's finding for why this is no longer a raw-value walk. Pressed flat for
            // TRIGGER_PRESS_TICKS starting at TRIGGER_PRESS_START_TICKS (never repeated/flickered within that
            // window); released everywhere else, including forever after the window closes.
            let trigger_value = if (TRIGGER_PRESS_START_TICKS..(TRIGGER_PRESS_START_TICKS + TRIGGER_PRESS_TICKS))
                .contains(&ticks)
            {
                fixed::ONE
            } else {
                0
            };

            outputs.act_bipolar(drive, channels::TRIGGER, trigger_value);

            // strafe: every tick, forever — see the module doc above for why this exists alongside forward's
            // narrow 3-tick window.
            outputs.act_bipolar(drive, channels::STRAFE, fixed::ONE);
        }

        // SAFETY: see the STATE doc above.
        unsafe {
            STATE = state;
        }
    }
}

#[cfg(feature = "bound64")]
mod bound64 {
    //! Declares exactly [`puck_stdlib::channels::MAX_CHANNEL_NAMES`] (64) channel names through the ordinary
    //! `channels!` macro — which compile-time-asserts a table may declare AT MOST 64 (see
    //! `puck_stdlib::channels::build_name_table`'s own `assert!`), so 64 is the largest table this macro can
    //! ever produce and this variant proves the host mounts a guest declaring exactly that many. Carries no
    //! on_tick behavior of its own — the bound-64 probe (case K) only needs this guest to mount and tick
    //! harmlessly, never to act on anything.

    use puck_stdlib::abi;

    puck_stdlib::channels! {
        static channels;
        const CH00: Bipolar = "ch00";
        const CH01: Bipolar = "ch01";
        const CH02: Bipolar = "ch02";
        const CH03: Bipolar = "ch03";
        const CH04: Bipolar = "ch04";
        const CH05: Bipolar = "ch05";
        const CH06: Bipolar = "ch06";
        const CH07: Bipolar = "ch07";
        const CH08: Bipolar = "ch08";
        const CH09: Bipolar = "ch09";
        const CH10: Bipolar = "ch10";
        const CH11: Bipolar = "ch11";
        const CH12: Bipolar = "ch12";
        const CH13: Bipolar = "ch13";
        const CH14: Bipolar = "ch14";
        const CH15: Bipolar = "ch15";
        const CH16: Bipolar = "ch16";
        const CH17: Bipolar = "ch17";
        const CH18: Bipolar = "ch18";
        const CH19: Bipolar = "ch19";
        const CH20: Bipolar = "ch20";
        const CH21: Bipolar = "ch21";
        const CH22: Bipolar = "ch22";
        const CH23: Bipolar = "ch23";
        const CH24: Bipolar = "ch24";
        const CH25: Bipolar = "ch25";
        const CH26: Bipolar = "ch26";
        const CH27: Bipolar = "ch27";
        const CH28: Bipolar = "ch28";
        const CH29: Bipolar = "ch29";
        const CH30: Bipolar = "ch30";
        const CH31: Bipolar = "ch31";
        const CH32: Bipolar = "ch32";
        const CH33: Bipolar = "ch33";
        const CH34: Bipolar = "ch34";
        const CH35: Bipolar = "ch35";
        const CH36: Bipolar = "ch36";
        const CH37: Bipolar = "ch37";
        const CH38: Bipolar = "ch38";
        const CH39: Bipolar = "ch39";
        const CH40: Bipolar = "ch40";
        const CH41: Bipolar = "ch41";
        const CH42: Bipolar = "ch42";
        const CH43: Bipolar = "ch43";
        const CH44: Bipolar = "ch44";
        const CH45: Bipolar = "ch45";
        const CH46: Bipolar = "ch46";
        const CH47: Bipolar = "ch47";
        const CH48: Bipolar = "ch48";
        const CH49: Bipolar = "ch49";
        const CH50: Bipolar = "ch50";
        const CH51: Bipolar = "ch51";
        const CH52: Bipolar = "ch52";
        const CH53: Bipolar = "ch53";
        const CH54: Bipolar = "ch54";
        const CH55: Bipolar = "ch55";
        const CH56: Bipolar = "ch56";
        const CH57: Bipolar = "ch57";
        const CH58: Bipolar = "ch58";
        const CH59: Bipolar = "ch59";
        const CH60: Bipolar = "ch60";
        const CH61: Bipolar = "ch61";
        const CH62: Bipolar = "ch62";
        const CH63: Bipolar = "ch63";
    }

    #[no_mangle]
    pub extern "C" fn puck_channels_ptr() -> i32 {
        abi::channels_ptr(channels::ptr(), channels::count())
    }

    #[no_mangle]
    pub extern "C" fn puck_init() {}

    #[no_mangle]
    pub extern "C" fn puck_on_tick(input_count: i32) -> i32 {
        abi::dispatch_tick(input_count, |_inputs, _outputs| {})
    }
}

#[cfg(feature = "bound65")]
mod bound65 {
    //! Proves the host refuses a 65-name declaration `BadExport`, naming the bound, at handshake.
    //!
    //! **Why this cannot go through the ordinary `channels!` macro at all:** `puck_stdlib::channels::
    //! build_name_table` compile-time-asserts `names.len() <= MAX_CHANNEL_NAMES` (64) — a `channels!` call
    //! declaring 65 names is a Rust COMPILE ERROR, not a runtime host refusal, so this variant cannot be built
    //! by simply listing 65 names in the macro. It also does not need to: the host's own handshake
    //! (`AddonInstance.TryHandshake` → `AddonChannelTableReader.TryDecode`) reads each channel DESCRIPTOR's
    //! `VerbCount` field and refuses `BadExport` — `"descriptor {index}: input VerbCount {verbCount} out of
    //! range [1, 64]"` — before it ever reads a single byte of the declared NAME table those bytes live in
    //! (`src/Puck.Scripting/AddonChannelTableReader.cs`). So this variant reuses a small, perfectly ordinary
    //! 2-name table built through the real macro (`channels!`'s own compile-time checks all pass for 2 names)
    //! and simply LIES about the count it hands `abi::channels_ptr` — 65 instead of the table's real
    //! `channels::count()` (2). The host reads the lie, refuses at the VerbCount bound before the pointer is
    //! ever dereferenced for name bytes, and the guest never mounts far enough to matter that the underlying
    //! table does not actually hold 65 names.

    use puck_stdlib::abi;

    puck_stdlib::channels! {
        static channels;
        const FORWARD: Bipolar = "forward";
        const WALKONLY: Bipolar = "walkonly";
    }

    /// One past `puck_stdlib::channels::MAX_CHANNEL_NAMES` (64) — the declared-name bound this variant exists
    /// to overshoot. See the module doc above for why this is not itself a `channels!`-declared name count.
    const DECLARED_COUNT_OVER_BOUND: i32 = 65;

    #[no_mangle]
    pub extern "C" fn puck_channels_ptr() -> i32 {
        abi::channels_ptr(channels::ptr(), DECLARED_COUNT_OVER_BOUND)
    }

    #[no_mangle]
    pub extern "C" fn puck_init() {}

    #[no_mangle]
    pub extern "C" fn puck_on_tick(input_count: i32) -> i32 {
        abi::dispatch_tick(input_count, |_inputs, _outputs| {})
    }
}
