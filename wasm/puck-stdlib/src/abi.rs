//! Raw addon ABI machinery: the three static byte regions a module exposes at fixed offsets — the
//! guest→host output ring, the host→guest input ring, and the channel descriptor table that names
//! what those rings carry — the raw pointer/capacity accessors over them, and [`dispatch_tick`], a
//! callable entry helper that hands the tick's input cells to the addon's closure and returns the
//! output-cell count the host expects `puck_on_tick` to return.
//!
//! **This module deliberately declares no `#[no_mangle]` exports.** A linker is free to drop a
//! symbol from a statically-linked `rlib` if nothing in the final crate graph references it, so a
//! `#[no_mangle] extern "C"` function placed here could silently vanish from a consuming `cdylib`'s
//! compiled `.wasm` — there is no compiler error, just a missing export the host's load-time
//! pre-flight then rejects. The frozen `puck_*` export names live only in the crate that actually
//! produces the shipped module (`puck-addon-default/src/lib.rs`), each a one-line delegate into a
//! function below; verify the built artifact's export list with `wasm-tools print`, never by
//! assuming the rlib's exports "come along for the ride".
//!
//! **Guest output crosses only as an `on_tick` return.** The host zeroes the output ring before
//! EVERY tick, the first one included, so cells written from `puck_init` are erased before anything
//! reads them — emitting from init is a silent no-op by design, not an ordering hazard to work
//! around. Init sets up guest state; the first tick speaks.
//!
//! Authors never edit this file for a normal addon — [`crate::Inputs`]/[`crate::Outputs`] are the
//! typed surface to write against; the ring CAPACITIES here are this crate's own budgets, while
//! every cell SIZE and per-field OFFSET they index through is a GENERATED mirror of the live
//! `Puck.Scripting.AddonAbi` — see `crate::abi_generated`'s module doc — so nothing here can
//! silently drift from the host.

use crate::abi_generated::{
    ChannelKind, CHANNEL_DESCRIPTOR_OFFSET_KIND, CHANNEL_DESCRIPTOR_OFFSET_RESERVED0,
    CHANNEL_DESCRIPTOR_OFFSET_RESERVED1, CHANNEL_DESCRIPTOR_OFFSET_VERB_COUNT,
    CHANNEL_DESCRIPTOR_OFFSET_VERB_TABLE_PTR, MAX_CHANNELS, MAX_IN_CELLS, MAX_OUT_CELLS,
    REQUEST_VERB_COUNT,
};
use crate::{Inputs, Outputs};

// Bytes per guest-written output cell, bytes per host-written input cell, bytes per channel
// descriptor, and the exact-match ABI version handshake value — GENERATED mirrors of
// `Puck.Scripting.AddonAbi.OutCellBytes`/`InCellBytes`/`ChannelDescriptorBytes`/`AbiVersion`; see
// `abi_generated`'s module doc. The host requires ABI_VERSION exactly and faults the addon at load
// time (`AbiMismatch`) on any other value — that mismatch is a stale-artifact detector, never a
// compatibility surface: there is one addon ABI, and a module built against an older shape is
// simply stale.
pub use crate::abi_generated::{ABI_VERSION, CHANNEL_DESCRIPTOR_BYTES, IN_CELL_BYTES, OUT_CELL_BYTES};

/// Output-cell capacity this module reserves at [`out_ptr`] — this crate's OWN budget, not a mirror
/// of anything on the host: the assertion just below enforces the one host-side constraint that
/// bounds it ([`crate::abi_generated::MAX_OUT_CELLS`]), and the host's load-time pre-flight
/// independently rejects an `out_cap()` above that same ceiling. Eight is comfortable headroom for
/// a clamp-walk-class addon (the default addon emits at most four cells per tick — an ask, a pose
/// query, a move act, and a digital act); raise it only if a single tick can plausibly emit more.
pub const OUT_CAP: usize = 8;

/// Input-cell capacity this module reserves at [`in_ptr`] — again this crate's own budget, bounded
/// by [`crate::abi_generated::MAX_IN_CELLS`]. It is also a live per-tick BUDGET, not just a buffer
/// size: the host writes at most this many cells per batch and refuses the answers that would
/// overflow it with `QuotaExhausted`, so an addon that asks more questions per tick than fit here
/// gets refusals rather than a wider batch. Sixteen holds the mandatory tick cell, a disclosure
/// push, and a four-part pose answer several times over.
pub const IN_CAP: usize = 16;

const _: () = assert!(
    OUT_CAP <= MAX_OUT_CELLS,
    "puck-stdlib reserves more output cells than AddonAbi.MaxOutCells allows"
);

const _: () = assert!(
    IN_CAP <= MAX_IN_CELLS,
    "puck-stdlib reserves more input cells than AddonAbi.MaxInCells allows"
);

/// Descriptor INDEX of this module's `Input` channel — the `Channel` byte an `Act` carrying a
/// declared channel's verb writes. This is a position in the table [`channels_ptr`] lays out, chosen
/// by this crate, NOT a wire value: the ABI's channel *kinds* are
/// [`crate::ChannelKind`]'s discriminants, and the mapping from index to kind is exactly what the
/// descriptor table exists to state.
pub const CHANNEL_INPUT: u8 = 0;

/// Descriptor INDEX of this module's `Request` channel — the `Channel` byte an `Ask` or a query
/// `Act` writes. See [`CHANNEL_INPUT`] on why this is a table position, not a wire value.
pub const CHANNEL_REQUEST: u8 = 1;

/// Descriptor INDEX of this module's `Response` channel. Host-written only: nothing the guest emits
/// ever carries this byte, but the descriptor must be declared — `Request` without `Response`
/// refuses the mount, because the pair is one facility.
pub const CHANNEL_RESPONSE: u8 = 2;

/// Number of channel descriptors this module declares — the fixed Simulation shape
/// `Input`/`Request`/`Response`, in that order.
pub const CHANNEL_COUNT: usize = 3;

const _: () = assert!(
    CHANNEL_COUNT <= MAX_CHANNELS,
    "puck-stdlib declares more channels than AddonAbi.MaxChannels allows"
);

static mut OUT_RING: [u8; OUT_CAP * OUT_CELL_BYTES] = [0; OUT_CAP * OUT_CELL_BYTES];
static mut IN_RING: [u8; IN_CAP * IN_CELL_BYTES] = [0; IN_CAP * IN_CELL_BYTES];
static mut CHANNELS: [u8; CHANNEL_COUNT * CHANNEL_DESCRIPTOR_BYTES] =
    [0; CHANNEL_COUNT * CHANNEL_DESCRIPTOR_BYTES];

/// Byte offset of the guest→host output ring. A consuming `cdylib`'s
/// `#[no_mangle] pub extern "C" fn puck_out_ptr` should do nothing but return this.
#[must_use]
pub fn out_ptr() -> i32 {
    // Taking the address of a static via addr_of! does not read or alias its contents, so this
    // needs no unsafe block (unlike dereferencing it, below in dispatch_tick).
    core::ptr::addr_of!(OUT_RING) as i32
}

/// Count of 32-byte output cells reserved at [`out_ptr`] ([`OUT_CAP`]). A consuming `cdylib`'s
/// `#[no_mangle] pub extern "C" fn puck_out_cap` should do nothing but return this.
#[must_use]
pub fn out_cap() -> i32 {
    OUT_CAP as i32
}

/// Byte offset of the host→guest input ring. A consuming `cdylib`'s
/// `#[no_mangle] pub extern "C" fn puck_in_ptr` should do nothing but return this.
#[must_use]
pub fn in_ptr() -> i32 {
    // Taking the address of a static via addr_of! does not read or alias its contents, so this
    // needs no unsafe block (unlike dereferencing it, below in dispatch_tick).
    core::ptr::addr_of!(IN_RING) as i32
}

/// Count of 32-byte input cells reserved at [`in_ptr`] ([`IN_CAP`]) — the host writes at most this
/// many cells per batch. A consuming `cdylib`'s
/// `#[no_mangle] pub extern "C" fn puck_in_cap` should do nothing but return this.
#[must_use]
pub fn in_cap() -> i32 {
    IN_CAP as i32
}

/// Fills and returns the byte offset of this module's channel descriptor table: three 16-byte
/// entries in the fixed order `Input`, `Request`, `Response` — the indices [`CHANNEL_INPUT`],
/// [`CHANNEL_REQUEST`], [`CHANNEL_RESPONSE`]. A consuming `cdylib`'s
/// `#[no_mangle] pub extern "C" fn puck_channels_ptr` should do nothing but call this with its own
/// `channels::ptr()`/`channels::count()`.
///
/// **The channel-name table is an argument, not something this crate can know.** The declared names
/// are the `Input` channel's verb table, and they live in the CONSUMING crate's
/// [`channels!`](crate::channels) expansion — a static this crate cannot name at const time. So the
/// consumer passes its own table's offset and row count in, and this function stitches them into
/// the descriptor.
///
/// Populating the table on the getter is sound because the host always calls `puck_channels_ptr`
/// before reading the table (mount step 5, before `puck_init` and before the first tick), so the
/// bytes are written strictly before anyone reads them.
#[must_use]
pub fn channels_ptr(names_ptr: i32, names_count: i32) -> i32 {
    // SAFETY: the host calls the mount-time getters and `puck_on_tick` from the single sim-tick
    // thread that owns this module's Store/Instance, and never re-enters, so this is the only live
    // reference to the descriptor table for the duration of the call.
    let table = unsafe { &mut *core::ptr::addr_of_mut!(CHANNELS) };

    // Input: its verb table is the consuming crate's declared channel-name table, and its verb
    // count is that table's row count — the host resolves each row against its own channel table.
    // Unlike the old source vocabulary, an unresolved name never refuses the mount: it is
    // report-and-inert (see `Puck.Scripting.IAddonChannelResolver`'s remarks).
    write_descriptor(
        table,
        CHANNEL_INPUT as usize,
        ChannelKind::Input,
        names_count as u16,
        names_ptr as u32,
    );

    // Request: a closed numeric vocabulary owned by the ABI, so it carries no verb table. Verbs are
    // 0-based ORDINALS (unlike the kind discriminants, which are 1-based so that a zeroed cell is
    // malformed rather than plausible), and the verb count is the EXCLUSIVE upper bound of the
    // range this module speaks. Pinned to the FULL generated vocabulary size (`REQUEST_VERB_COUNT`)
    // rather than to any one verb's own ordinal + 1 — a module built against an older stdlib that
    // declared fewer verbs keeps its own smaller count (PREFIX GROWTH: the vocabulary may only ever
    // grow, so an older module's narrower declaration stays legal), but THIS crate always advertises
    // everything it can actually emit, `Outputs::submit_mutation` included.
    write_descriptor(
        table,
        CHANNEL_REQUEST as usize,
        ChannelKind::Request,
        REQUEST_VERB_COUNT as u16,
        0,
    );

    // Response: host-written only, so both its verb count and its verb table pointer must be zero.
    write_descriptor(table, CHANNEL_RESPONSE as usize, ChannelKind::Response, 0, 0);

    core::ptr::addr_of!(CHANNELS) as i32
}

/// Count of channel descriptors laid out at [`channels_ptr`] ([`CHANNEL_COUNT`]). A consuming
/// `cdylib`'s `#[no_mangle] pub extern "C" fn puck_channels_count` should do nothing but return
/// this.
#[must_use]
pub fn channels_count() -> i32 {
    CHANNEL_COUNT as i32
}

/// Writes one 16-byte channel descriptor at table slot `index`, every field little-endian at its
/// generated offset and both reserved fields explicitly zeroed — the host treats a non-zero
/// reserved field as malformed, which is exactly what makes a later field addition detectable
/// rather than silently reinterpreted.
fn write_descriptor(table: &mut [u8], index: usize, kind: ChannelKind, verb_count: u16, verb_table_ptr: u32) {
    let offset = index * CHANNEL_DESCRIPTOR_BYTES;
    let descriptor = &mut table[offset..(offset + CHANNEL_DESCRIPTOR_BYTES)];

    descriptor[CHANNEL_DESCRIPTOR_OFFSET_KIND] = kind as u8;
    descriptor[CHANNEL_DESCRIPTOR_OFFSET_RESERVED0] = 0; // MUST be zero
    descriptor[CHANNEL_DESCRIPTOR_OFFSET_VERB_COUNT..(CHANNEL_DESCRIPTOR_OFFSET_VERB_COUNT + 2)]
        .copy_from_slice(&verb_count.to_le_bytes());
    descriptor[CHANNEL_DESCRIPTOR_OFFSET_VERB_TABLE_PTR..(CHANNEL_DESCRIPTOR_OFFSET_VERB_TABLE_PTR + 4)]
        .copy_from_slice(&verb_table_ptr.to_le_bytes());
    descriptor[CHANNEL_DESCRIPTOR_OFFSET_RESERVED1..(CHANNEL_DESCRIPTOR_OFFSET_RESERVED1 + 8)]
        .copy_from_slice(&0u64.to_le_bytes()); // MUST be zero
}

/// Hands the tick's `input_count` host-written cells to `tick` as a typed [`Inputs`] view, together
/// with an [`Outputs`] writer over the output ring, and returns the number of output cells written
/// — the value the host expects `puck_on_tick` to return. A consuming `cdylib`'s
/// `#[no_mangle] pub extern "C" fn puck_on_tick` should do nothing but call this with its own
/// `on_tick` function and return the result.
///
/// `input_count` is clamped to `0..=IN_CAP`. A host writing beyond the capacity this module
/// declared would be violating the contract it read at mount, and the honest response to that is a
/// saturated view of the cells that actually exist, never a read past the end of the ring.
///
/// # Safety invariants relied upon
/// The host calls `puck_on_tick` only from the single sim-tick thread that owns this module's
/// `Store`/`Instance` — one `Store`/`Instance` per addon, touched from one thread — and never
/// re-enters mid-tick, so these are the only live references to either ring for the duration of the
/// call.
pub fn dispatch_tick<F>(input_count: i32, tick: F) -> i32
where
    F: FnOnce(&Inputs, &mut Outputs),
{
    let count = input_count.clamp(0, IN_CAP as i32) as usize;

    // SAFETY: see the function doc above — single sim-tick thread, no re-entrancy.
    let in_bytes = unsafe { &*core::ptr::addr_of!(IN_RING) };
    // SAFETY: as above.
    let out_bytes = unsafe { &mut *core::ptr::addr_of_mut!(OUT_RING) };

    let inputs = Inputs::new(&in_bytes[..(count * IN_CELL_BYTES)]);
    let mut outputs = Outputs::new(out_bytes);

    tick(&inputs, &mut outputs);

    outputs.len() as i32
}
