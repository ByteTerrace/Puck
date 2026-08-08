//! **BATTERY-ONLY GUEST.** Never shipped, no shipped world pins it. Exists solely so
//! `docs/verification/lane-present-deletion/run.ps1` can prove the host refuses a channel
//! descriptor naming a RETIRED `AddonChannelKind` ordinal (4, formerly `Geometry`; 5, formerly
//! `Overlay` — both retired permanently with the rest of the lane axis, owner ruling 2026-08-02) —
//! by the ORDINARY undefined-kind refusal `AddonChannelTableReader.TryDecode` already gives every
//! unrecognized byte, never a special case carved out for the retired values.
//!
//! Every export shim below delegates to `puck_stdlib::abi` exactly like `puck-addon-queryspam`'s
//! (see its module doc for why the frozen `#[no_mangle]` names must live in the `cdylib`, not the
//! `rlib`), with ONE deliberate exception: `puck_channels_ptr` calls `abi::channels_ptr` to lay out
//! the ordinary three-descriptor table and then POKES the `Response` descriptor's `Kind` byte
//! (table offset `CHANNEL_RESPONSE * CHANNEL_DESCRIPTOR_BYTES`, field offset 0) to `4` — a value no
//! defined `AddonChannelKind` member carries. `Response` is the last descriptor the reader visits, so
//! `Input`/`Request` decode cleanly first and the refusal is unambiguously about the corrupted entry:
//! `descriptor 2: channel kind 4 is not defined`.

use puck_stdlib::abi;

puck_stdlib::channels! {
    static channels;
    /// Declared only because the `Input` channel's descriptor requires a nonempty verb table —
    /// this addon never ticks far enough to emit anything against it (the mount itself refuses).
    const FORWARD: Bipolar = "forward";
}

/// The byte offset, within a channel descriptor, of its `Kind` field — mirrors
/// `Puck.Scripting.AddonAbi.ChannelDescriptorOffsets.Kind` (always `0`; asserted structurally by the
/// crate this depends on, never restated as a second literal here beyond this one deliberate poke).
const CHANNEL_DESCRIPTOR_OFFSET_KIND: usize = 0;

/// A wire value no defined `AddonChannelKind` member carries. Was `Geometry` (Presentation-lane,
/// pinned but never served) before the lane-axis deletion retired it permanently; kept here as the
/// deliberately-stale byte a mount must refuse.
const RETIRED_CHANNEL_KIND: u8 = 4;

#[no_mangle]
pub extern "C" fn puck_abi_version() -> i32 {
    abi::ABI_VERSION
}

#[no_mangle]
pub extern "C" fn puck_out_ptr() -> i32 {
    abi::out_ptr()
}

#[no_mangle]
pub extern "C" fn puck_out_cap() -> i32 {
    abi::out_cap()
}

#[no_mangle]
pub extern "C" fn puck_in_ptr() -> i32 {
    abi::in_ptr()
}

#[no_mangle]
pub extern "C" fn puck_in_cap() -> i32 {
    abi::in_cap()
}

/// Lays out the ordinary three-descriptor table, then corrupts the `Response` descriptor's `Kind`
/// byte to [`RETIRED_CHANNEL_KIND`] — see the module doc for why this descriptor and not another.
/// SAFETY: single-threaded WASM, called by the host before `puck_init` and before any tick, so no
/// other code observes the table between the ordinary write and this poke.
#[no_mangle]
pub extern "C" fn puck_channels_ptr() -> i32 {
    let table_ptr = abi::channels_ptr(channels::ptr(), channels::count());
    let response_kind_ptr = (table_ptr as usize
        + ((abi::CHANNEL_RESPONSE as usize) * (abi::CHANNEL_DESCRIPTOR_BYTES as usize))
        + CHANNEL_DESCRIPTOR_OFFSET_KIND) as *mut u8;

    unsafe {
        *response_kind_ptr = RETIRED_CHANNEL_KIND;
    }

    table_ptr
}

#[no_mangle]
pub extern "C" fn puck_channels_count() -> i32 {
    abi::channels_count()
}

/// Unreachable in practice — the corrupted descriptor table refuses the mount before `puck_init`
/// (mount step 5 decodes the table; `puck_init` is step 9) — kept only so the export exists.
#[no_mangle]
pub extern "C" fn puck_init() {}

/// Unreachable in practice for the same reason as [`puck_init`] — the addon never admits.
#[no_mangle]
pub extern "C" fn puck_on_tick(_input_count: i32) -> i32 {
    0
}
