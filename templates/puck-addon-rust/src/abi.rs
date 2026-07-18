//! Raw `puck.addon.v1` ABI surface: the `#[no_mangle]` pointer/cap/version exports plus
//! `puck_on_tick`/`puck_init`, wired over two static byte buffers that live in this module's own
//! linear memory. Authors never edit this file for a normal addon — `Snapshot`/`Commands` in
//! `lib.rs` are the typed surface to write against; this file only encodes the exact byte
//! offsets/sizes from the ABI tables (plan-of-record A.1-A.3) so nothing here can silently drift
//! from them.

use crate::{Commands, Snapshot};

/// Bytes in the host-written snapshot region (`puck.addon.v1` A.2).
pub const SNAPSHOT_BYTES: usize = 40;
/// Bytes per guest-written command record (`puck.addon.v1` A.3).
pub const COMMAND_RECORD_BYTES: usize = 24;
/// Command-slot capacity this module reserves at `puck_commands_ptr()`. Must stay
/// `<= 64` (`AddonAbi.MaxCommandRecords` on the host) — the host's load-time pre-flight rejects a
/// larger `puck_commands_cap()`. Eight is comfortable headroom for a clamp-walk-class addon (the
/// example in `lib.rs` emits at most two records per tick); raise it only if a single tick can
/// plausibly emit more than eight virtual-pad records.
pub const COMMANDS_CAP: usize = 8;

static mut SNAPSHOT: [u8; SNAPSHOT_BYTES] = [0; SNAPSHOT_BYTES];
static mut COMMANDS: [u8; COMMANDS_CAP * COMMAND_RECORD_BYTES] = [0; COMMANDS_CAP * COMMAND_RECORD_BYTES];

/// Exact-match ABI version handshake (`puck.addon.v1` A.6 step 4) — the host requires `1` exactly
/// and faults the addon at load time (`AbiMismatch`) on any other value, so this must never change
/// within a `puck.addon.v1` module.
#[no_mangle]
pub extern "C" fn puck_abi_version() -> i32 {
    1
}

/// Byte offset of the 40-byte snapshot region the host writes into each tick.
#[no_mangle]
pub extern "C" fn puck_snapshot_ptr() -> i32 {
    // Taking the address of a static via addr_of! does not read or alias its contents, so this
    // needs no unsafe block (unlike dereferencing it, below in puck_on_tick).
    core::ptr::addr_of!(SNAPSHOT) as i32
}

/// Byte offset of the command output region this module writes into each tick.
#[no_mangle]
pub extern "C" fn puck_commands_ptr() -> i32 {
    // Taking the address of a static via addr_of! does not read or alias its contents, so this
    // needs no unsafe block (unlike dereferencing it, below in puck_on_tick).
    core::ptr::addr_of!(COMMANDS) as i32
}

/// Count of 24-byte command slots reserved at `puck_commands_ptr()`.
#[no_mangle]
pub extern "C" fn puck_commands_cap() -> i32 {
    COMMANDS_CAP as i32
}

/// Optional guest setup hook (`puck.addon.v1` A.1) — called once after instantiation, before the
/// first `puck_on_tick`. Forwards to the author-supplied `crate::on_init`.
#[no_mangle]
pub extern "C" fn puck_init() {
    crate::on_init();
}

/// Reads the host-written snapshot, calls the author's `crate::on_tick`, and returns the number of
/// command records written — the ONE nullary call the host makes every sim tick
/// (plan-of-record A.7).
#[no_mangle]
pub extern "C" fn puck_on_tick() -> i32 {
    // SAFETY: `puck_on_tick` is only ever invoked by the host from the single sim-tick thread
    // (plan-of-record A.7 — one Store/Instance per addon, touched from one thread), and this
    // function never re-enters itself or calls back into the host mid-tick, so these are the only
    // live references to either static for the duration of this call.
    let snapshot_bytes = unsafe { &*core::ptr::addr_of!(SNAPSHOT) };
    let commands_bytes = unsafe { &mut *core::ptr::addr_of_mut!(COMMANDS) };

    let snapshot = Snapshot::from_bytes(snapshot_bytes);
    let mut commands = Commands::new(commands_bytes);

    crate::on_tick(&snapshot, &mut commands);

    commands.len() as i32
}
