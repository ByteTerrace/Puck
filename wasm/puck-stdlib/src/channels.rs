//! The channel-name declaration API — how an addon tells the host which named channels it will emit
//! scalar acts against.
//!
//! An addon is a **virtual input device**: it owns no closed vocabulary of its own. Instead it
//! declares a handful of NAMES, and the host resolves each one, once at handshake, against the boot
//! world document's compiled channel table (see `Puck.World.Server.WorldAddonChannelResolver`, the
//! C# host's single home for that table). A resolved name lands on either a fixed engine motion role
//! (one of `PlayerIntent`'s six role ordinals) or a per-kit composition trigger (`jump`, `dash`, …),
//! whichever the declaring world document's row claims — a different world document declares a
//! different table through the SAME resolver class.
//!
//! **The channel table is world data, not crate data — this module never hand-lists it.** A
//! different world document declares a different set through the SAME resolver, so a name, a
//! count, or an effect restated here would be a second source the boot document could drift out
//! from under with no build error to catch it — exactly the defect this crate's own doc used to
//! carry (it once listed five channels while the shipped default world had already grown to eight).
//! For the shipped default world's current table (name, shape, and host-side intent effect), read
//! `Assets/worlds/nexus.world.json`'s `channels` array directly, or its rendered form in
//! `src/Puck.Scripting/README.md`.
//!
//! **Declaring a name the host table lacks is never a mount fault.** An unresolved declaration is
//! report-and-inert: the host names it once at mount, and an act naming it answers
//! `Verdict::AttenuatedToEmpty` every tick rather than faulting the instance. That is a change from
//! the old source-id vocabulary this replaces, which refused the whole mount on an unrecognized
//! declaration.
//!
//! **This table IS the `Input` channel's verb table.** It is not reached through exports of its own
//! — it is reached through the channel descriptor table, whose `Input` entry carries this table's
//! offset and row count. Unlike the fixed-stride table it replaces, entries here are
//! VARIABLE-LENGTH — a length byte followed by that many UTF-8 bytes, packed with no padding — so
//! the row order is still wire-visible (reordering a `channels!` table changes what the addon
//! emits) but the table's total byte length is data, not `row_count * 64`.
//!
//! [`channels!`](crate::channels) is the only author-facing entry point. It lays out the packed name
//! table the host reads once at mount and hands back one typed [`ChannelHandle`] constant per
//! declared channel — an addon author never counts a slot, computes a byte offset, or writes a
//! numeric index by hand. Everything in this module besides the macro and
//! [`ChannelHandle`]/[`Bipolar`]/[`Binary`]/[`Unipolar`] is plumbing the macro expands into; reach
//! for those four names and the macro, not the raw functions.
//!
//! ```
//! puck_stdlib::channels! {
//!     static channels;
//!     /// Camera-relative floor-plane forward speed.
//!     const FORWARD: Bipolar = "forward";
//!     /// Latched jump press, this tick only.
//!     const JUMP: Binary = "jump";
//! }
//! ```
//!
//! generates a module named `channels` (pick any name) holding:
//! - `channels::FORWARD: ChannelHandle<Bipolar>` / `channels::JUMP: ChannelHandle<Binary>` — pass
//!   these by value to [`crate::Outputs`]'s typed emit methods (`act_bipolar`, `act_binary`,
//!   `act_unipolar`), along with the Drive handle the act drives through.
//! - `channels::ptr() -> i32` / `channels::count() -> i32` — pass these two straight into
//!   [`crate::abi::channels_ptr`] from your crate's `puck_channels_ptr` shim, which is what stitches
//!   this table into the `Input` channel's descriptor. There are no `puck_channels_*` exports beyond
//!   the ABI's own; the descriptor table is the one door.
//!
//! `Kind` must be one of [`Bipolar`], [`Binary`], or [`Unipolar`] — purely a LOCAL, compile-time hint
//! this crate cannot check against the host: the host resolves the actual shape from its own table
//! by NAME, independent of what this crate declares. A mismatched hint compiles cleanly and faults
//! the instance at the first out-of-domain act, exactly like any other domain violation. A declared
//! name must be non-empty, at most [`MAX_CHANNEL_NAME_BYTES`] UTF-8 bytes, and unique within the
//! table — every one of those is checked at compile time inside the `channels!` call, not discovered
//! later as a host-side mount fault. There is no public way to construct a [`ChannelHandle`] outside
//! a `channels!` expansion, so emitting against an undeclared index is not something ordinary addon
//! code can even attempt to write.

use core::marker::PhantomData;

// The maximum length in UTF-8 bytes of one declared channel name, and the largest number of
// channels a single module may declare — the `Input` channel descriptor's verb count is
// `0..=MAX_CHANNEL_NAMES`, and that ceiling must never exceed 64 because the host's per-channel
// masks are u64-wide. GENERATED mirrors of `Puck.Scripting.AddonAbi.MaxChannelNameBytes`/
// `MaxChannelNames`; see `crate::abi_generated`'s module doc.
pub use crate::abi_generated::{MAX_CHANNEL_NAMES, MAX_CHANNEL_NAME_BYTES};

/// Marker: a two-sided analog value, `A` in `[-fixed::ONE, fixed::ONE]`.
pub struct Bipolar;

/// Marker: a pressed/released control. `A` is exactly `0` or [`crate::fixed::ONE`] — a fixed-point
/// literal, never a boolean `0`/`1`.
pub struct Binary;

/// Marker: a one-sided analog value, `A` in `[0, fixed::ONE]`. Pinned for a future channel; the
/// interim host table declares none today.
pub struct Unipolar;

/// A compile-time-checked reference into a [`channels!`]-declared name table.
///
/// `Kind` (one of [`Bipolar`], [`Binary`], [`Unipolar`]) records the shape this crate BELIEVES the
/// channel carries, so [`crate::Outputs`]'s typed emit methods only accept the handle kind each one
/// requires. `index` is assigned by [`channels!`] in declaration order — an addon author never
/// writes it by hand, and it is the DECLARED ordinal an act's verb carries (distinct from the HOST
/// ordinal the name resolves to, which this crate never learns).
pub struct ChannelHandle<Kind> {
    index: u16,
    _kind: PhantomData<Kind>,
}

impl<Kind> ChannelHandle<Kind> {
    /// Constructs a handle for the given table-slot index. Called only by the code [`channels!`]
    /// expands into — never call this directly: a hand-built handle's index isn't backed by any row
    /// [`channels!`] laid out, so emitting through it writes a declared ordinal the host's declared
    /// table doesn't actually have at that slot.
    #[doc(hidden)]
    #[must_use]
    pub const fn __new(index: u16) -> Self {
        Self { index, _kind: PhantomData }
    }

    /// The declared table-slot index this handle refers to.
    #[must_use]
    pub const fn index(&self) -> u16 {
        self.index
    }
}

impl<Kind> Clone for ChannelHandle<Kind> {
    fn clone(&self) -> Self {
        *self
    }
}

impl<Kind> Copy for ChannelHandle<Kind> {}

/// Lays out `N` packed channel-name entries (a length byte, then that many UTF-8 bytes, no padding)
/// from `names` into a `TOTAL`-byte buffer. Validates, at compile time, that the table doesn't
/// exceed [`MAX_CHANNEL_NAMES`] and that every name is non-empty, fits within
/// [`MAX_CHANNEL_NAME_BYTES`], and is unique. Called only by [`channels!`]'s expansion — not part of
/// this crate's public API.
#[doc(hidden)]
pub const fn build_name_table<const TOTAL: usize>(names: &[&str]) -> [u8; TOTAL] {
    assert!(names.len() <= MAX_CHANNEL_NAMES, "a puck_stdlib::channels! table may declare at most 64 channel names");

    let mut table = [0u8; TOTAL];
    let mut pos = 0;
    let mut i = 0;

    while i < names.len() {
        let bytes = names[i].as_bytes();

        assert!(!bytes.is_empty(), "a declared channel name must not be empty");
        assert!(
            bytes.len() <= MAX_CHANNEL_NAME_BYTES,
            "a declared channel name must be at most 64 UTF-8 bytes long"
        );

        table[pos] = bytes.len() as u8;
        pos += 1;

        let mut j = 0;
        while j < bytes.len() {
            table[pos] = bytes[j];
            pos += 1;
            j += 1;
        }

        i += 1;
    }

    // Quadratic, but N <= MAX_CHANNEL_NAMES (64) and this only ever runs at compile time.
    let mut a = 0;
    while a < names.len() {
        let mut b = a + 1;
        while b < names.len() {
            if str_eq(names[a], names[b]) {
                panic!("a puck_stdlib::channels! table declares the same channel name more than once");
            }
            b += 1;
        }
        a += 1;
    }

    table
}

const fn str_eq(a: &str, b: &str) -> bool {
    let ab = a.as_bytes();
    let bb = b.as_bytes();

    if ab.len() != bb.len() {
        return false;
    }

    let mut i = 0;

    while i < ab.len() {
        if ab[i] != bb[i] {
            return false;
        }

        i += 1;
    }

    true
}

/// Declares a fixed table of channel names this addon emits acts against, and a typed
/// [`ChannelHandle`] constant for each. See the [module documentation](crate::channels) for a full
/// usage example.
///
/// This is the only author-facing way to add a row to the channel-name table the host reads at
/// mount, through the `Input` channel's descriptor. The crate lays out the packed entries and
/// assigns each handle's index in declaration order — an addon author never counts a slot or writes
/// an index by hand.
#[macro_export]
macro_rules! channels {
    (
        static $table:ident;
        $(
            $(#[$item_meta:meta])*
            const $name:ident : $kind:ident = $id:expr;
        )+
    ) => {
        #[allow(non_snake_case)]
        pub mod $table {
            #[allow(unused_imports)]
            use super::*;

            const NAMES: &[&str] = &[ $($id),+ ];
            /// Number of channel names declared in this table.
            pub const COUNT: usize = NAMES.len();
            const TOTAL_BYTES: usize = 0usize $(+ (1 + $id.len()))+;

            static TABLE: [u8; TOTAL_BYTES] =
                $crate::channels::build_name_table::<TOTAL_BYTES>(NAMES);

            /// Byte offset of the channel-name table — the `Input` channel's verb table. Pass this
            /// into `puck_stdlib::abi::channels_ptr` from your crate's `puck_channels_ptr`
            /// `#[no_mangle]` export shim.
            #[inline]
            #[must_use]
            pub fn ptr() -> i32 {
                // Taking the address of a static via addr_of! does not read or alias its
                // contents, so this needs no unsafe block — mirrors
                // puck_stdlib::abi::out_ptr's pattern for the same reason.
                core::ptr::addr_of!(TABLE) as i32
            }

            /// Number of declared channel names — the `Input` channel descriptor's verb count. Pass
            /// this into `puck_stdlib::abi::channels_ptr` alongside [`ptr`].
            #[inline]
            #[must_use]
            pub fn count() -> i32 {
                COUNT as i32
            }

            $crate::__puck_channels_handles!(0usize; $( $(#[$item_meta])* $name : $kind ),+ );
        }
    };
}

/// Implementation detail of [`channels!`] — recursively assigns sequential indices to each declared
/// handle constant. Not part of the public API; never invoke this directly.
#[macro_export]
#[doc(hidden)]
macro_rules! __puck_channels_handles {
    (
        $idx:expr;
        $(#[$item_meta:meta])* $name:ident : $kind:ident
        $(, $(#[$rest_meta:meta])* $rest_name:ident : $rest_kind:ident)*
    ) => {
        $(#[$item_meta])*
        pub const $name: $crate::channels::ChannelHandle<$crate::channels::$kind> =
            $crate::channels::ChannelHandle::__new($idx as u16);

        $crate::__puck_channels_handles!(
            $idx + 1usize;
            $($(#[$rest_meta])* $rest_name : $rest_kind),*
        );
    };
    ($idx:expr;) => {};
}
