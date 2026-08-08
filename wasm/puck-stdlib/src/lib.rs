//! Puck's addon WASM standard library.
//!
//! Every addon depends on this crate rather than reimplementing its plumbing: the typed [`Inputs`]
//! reader and [`Outputs`] writer over the ABI's two 32-byte cell rings — never raw byte offsets —
//! the channel-name declaration API in [`channels`], which lays out the `Input` channel's declared
//! verb table, and the bit-exact `FixedQ4816` mirror in [`fixed`]. `abi` owns the static ring and
//! channel-descriptor regions those views sit on and exposes [`abi::dispatch_tick`], the callable
//! entry helper a consuming addon's `puck_on_tick` shim delegates into.
//!
//! The shape to hold in mind: each tick the host writes a batch of input cells — one `Tick` cell,
//! then any disclosures, then the answers to the cells this addon wrote LAST tick — and the addon
//! writes a batch of output cells, `Act`s that drive a subject and `Ask`s that request authority
//! over one. Correlation between the two directions is by ORDINAL: every emit method returns the
//! ordinal of the cell it wrote, and next tick's answer carries that same ordinal back. Authority
//! is never assumed — a [`Handle`] arrives from the host, through a disclosure or an answer, and an
//! act without one has nothing to act through.
//!
//! This crate is deliberately an `rlib`, not a `cdylib` — see `abi`'s module doc for why the frozen
//! `#[no_mangle] pub extern "C" fn puck_*` exports live in the *consuming* crate
//! (`puck-addon-default`, or your own addon crate) instead of here.

pub mod abi;
// Not part of the crate's public surface — a GENERATED mirror of the host's closed wire sets (the
// cell/channel/subject/verdict enums, the capability mask bits, and `AddonAbi`'s layout constants);
// `abi`/`channels`/this file re-export or consume its items under their own established names, so
// an addon author never reaches this module directly. See its own module doc for what it mirrors
// and why.
mod abi_generated;
pub mod channels;
pub mod fixed;
// Not part of the crate's public surface — fixed.rs re-exports the six functions this module
// implements under its own name, so an addon author never reaches this module directly.
mod fixed_generated;

#[cfg(test)]
mod fixed_tests;
#[cfg(test)]
mod fixed_vectors;

// The ABI's closed wire sets, re-exported under the crate root so an addon author never writes a
// raw discriminant byte: the two output-cell kinds and three input-cell kinds, the channel kinds a
// descriptor declares, the subject kinds an `Ask` may name, and the verdict an answer carries. All
// GENERATED from their C# originals; see `abi_generated`'s module doc.
//
// The numbering convention across the whole ABI, worth reading once: DISCRIMINANTS — cell kind,
// channel kind, subject kind — are 1-BASED, so that zero is invalid and a zeroed cell decodes as
// malformed rather than as a plausible record. ORDINALS — verbs, answer part indices, channel
// descriptor indices, batch ordinals — are 0-BASED, because they are positions in a table or a
// batch, and a position has no reason to be reserved. Verdict is the one set that is neither: its
// zero means "this kind carries no verdict".
pub use crate::abi_generated::{ChannelKind, InCellKind, OutCellKind, SubjectKind, Verdict};

// The capability mask bits an `Ask` requests in its `B` lane — frozen independently of the host's
// own capability ordinals, so these values are the wire, not a projection of an internal enum. An
// `Ask` names exactly one. GENERATED; see `abi_generated`'s module doc. `CAP_RESERVED` (bit 2,
// formerly `CAP_PRESENT`) is a PERMANENTLY RESERVED HOLE — the host maps it to no capability, so an
// `Ask` naming it always resolves as unheld; never spend it on a new capability.
pub use crate::abi_generated::{CAP_CONTROL, CAP_DRIVE, CAP_EDIT, CAP_MUTATE, CAP_OBSERVE, CAP_RESERVED};

// The closed request/observation verb vocabulary, and the pinned part count of a body-pose answer.
// An addon needs these to READ what the host sends — matching a disclosure's verb, counting an
// answer's parts — even though [`Outputs`]'s emit methods write them on its behalf, so the whole
// family is re-exported rather than the two names today's default addon happens to touch:
// cherry-picking a closed set is how the next consumer ends up retyping an ordinal. GENERATED; see
// `abi_generated`'s module doc.
pub use crate::abi_generated::{
    OBSERVATION_VERB_EVENT_COLLISION_BEGIN, OBSERVATION_VERB_EVENT_COLLISION_END,
    OBSERVATION_VERB_EVENT_GAP, OBSERVATION_VERB_EVENT_MACHINE_MEMORY_CHANGED,
    OBSERVATION_VERB_EVENT_REGION_ENTER, OBSERVATION_VERB_EVENT_REGION_EXIT,
    OBSERVATION_VERB_EVENT_ROUTE_DISENGAGED, OBSERVATION_VERB_EVENT_ROUTE_ENGAGED,
    OBSERVATION_VERB_EVENT_SEAT_JOIN, OBSERVATION_VERB_EVENT_SEAT_LEAVE,
    OBSERVATION_VERB_GRANTED_BODY, REQUEST_VERB_BODY_POSE, REQUEST_VERB_BODY_POSE_ANSWER_PARTS,
    REQUEST_VERB_SUBMIT_MUTATION, REQUEST_VERB_SUBMIT_MUTATION_ANSWER_PARTS,
};

use crate::abi_generated::{
    IN_CELL_OFFSET_A, IN_CELL_OFFSET_B, IN_CELL_OFFSET_CHANNEL, IN_CELL_OFFSET_HANDLE_GENERATION,
    IN_CELL_OFFSET_HANDLE_INDEX, IN_CELL_OFFSET_KIND, IN_CELL_OFFSET_ORDINAL, IN_CELL_OFFSET_VERB,
    IN_CELL_OFFSET_VERDICT, INPUT_VERB_RESERVED_BITS, OUT_CELL_OFFSET_A, OUT_CELL_OFFSET_B,
    OUT_CELL_OFFSET_C, OUT_CELL_OFFSET_CHANNEL, OUT_CELL_OFFSET_HANDLE_GENERATION,
    OUT_CELL_OFFSET_HANDLE_INDEX, OUT_CELL_OFFSET_KIND, OUT_CELL_OFFSET_VERB,
};

/// A host-minted handle to a subject the addon may act through — the `(index, generation)` pair the
/// ABI validates at APPLICATION against the live table, never at decode.
///
/// **An addon never fabricates one.** A handle arrives from the host, either pushed as a
/// `GrantedBody` [`InCellKind::Observation`] or minted in the answer to an [`Outputs::ask`], and
/// carries exactly the authority the grant table gave it. The generation is what makes a revoked
/// handle fail on its very next use (`Verdict::StaleHandle`) rather than quietly addressing whoever
/// now occupies the slot — so hold a handle across ticks freely, but read the verdicts.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Handle {
    /// The subject's slot in the host's handle table.
    pub index: u16,
    /// The slot's generation counter at mint time.
    pub generation: u16,
}

impl Handle {
    /// The all-zero handle an `Ask` writes into its unused handle fields — an `Ask` names a subject
    /// by kind and index, not by a handle it does not yet have.
    const UNUSED: Self = Self { index: 0, generation: 0 };
}

/// One decoded host-written input cell. Every field is read at its frozen offset, little-endian —
/// an addon author never indexes the bytes.
///
/// Two fields decode to `Option` because the wire may carry a value this build does not know:
/// [`InCellKind`] and [`Verdict`] both grow as data, and an addon that matched an unknown byte onto
/// a known variant would be inventing a fact. `None` means exactly "this build has no name for that
/// byte" — skip the cell rather than guessing.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct InCell {
    /// What the cell is: a `Tick`, an `Answer` to one of last tick's output cells, or an
    /// `Observation` the host pushed unprompted. `None` for an unrecognized kind byte.
    pub kind: Option<InCellKind>,
    /// The descriptor index of the channel this cell belongs to (see `abi::CHANNEL_*`).
    pub channel: u8,
    /// On an `Answer`, which output cell of the addon's PREVIOUS batch this answers — the ordinal
    /// the emit method returned. Zero on other kinds.
    pub ordinal: u16,
    /// The handle carried by the cell: the minted subject on an allowed `Answer`, the disclosed
    /// subject on an `Observation`. Zero-valued when the kind carries none.
    pub handle: Handle,
    /// The authorization outcome on an `Answer`; `Verdict::None` on kinds that carry none, and
    /// `None` for an unrecognized verdict byte.
    pub verdict: Option<Verdict>,
    /// The channel-relative verb: the observation verb on an `Observation`, the 0-based PART INDEX
    /// on a multi-part answer, zero on a refusal. Verbs are 0-based ordinals, so zero is a legal
    /// verb — read [`verdict`](InCell::verdict) to tell a refusal from a first part, never this.
    pub verb: u8,
    /// First payload lane — meaning is per (kind, channel, verb); fixed-point lanes carry raw
    /// `FixedQ4816` bits.
    pub a: i64,
    /// Second payload lane, as [`a`](InCell::a).
    pub b: i64,
}

impl InCell {
    /// Decodes one cell from exactly [`abi::IN_CELL_BYTES`] bytes at the frozen offsets.
    fn decode(bytes: &[u8]) -> Self {
        Self {
            kind: decode_in_cell_kind(bytes[IN_CELL_OFFSET_KIND]),
            channel: bytes[IN_CELL_OFFSET_CHANNEL],
            ordinal: read_u16(bytes, IN_CELL_OFFSET_ORDINAL),
            handle: Handle {
                index: read_u16(bytes, IN_CELL_OFFSET_HANDLE_INDEX),
                generation: read_u16(bytes, IN_CELL_OFFSET_HANDLE_GENERATION),
            },
            verdict: decode_verdict(bytes[IN_CELL_OFFSET_VERDICT]),
            verb: bytes[IN_CELL_OFFSET_VERB],
            a: read_i64(bytes, IN_CELL_OFFSET_A),
            b: read_i64(bytes, IN_CELL_OFFSET_B),
        }
    }
}

/// Typed, read-only, non-allocating view over the input cells the host wrote for this tick — cells
/// `[0, input_count)` of the input ring, in the order the host wrote them.
///
/// That order is itself a fact: exactly one `Tick` cell first, then any `Observation` disclosures,
/// then `Answer` cells ascending by (ordinal, part). Cells are parsed on iteration, so iterating
/// twice costs twice and stores nothing.
///
/// **Every answer arrives in the NEXT tick's batch, uniformly** — including denials, and including
/// denials of input-channel acts. There is no same-tick refusal to wait for: emit, remember the
/// ordinal, and read the verdict next tick.
///
/// **No answer at all is starvation, not denial.** When the host's per-batch ring budget runs out,
/// the answer groups that no longer fit are DROPPED — no cell, no verdict. A request whose ordinal
/// never comes back was starved, and retrying it is correct; a denial is a cell that says so.
///
/// **A disclosure push is a complete set, never a split one.** If a batch carries any `GrantedBody`
/// observation, that batch's `GrantedBody` cells are the whole new authoritative set: replace every
/// previously disclosed handle with exactly this set, and treat a handle that is absent from it as
/// gone. Folding a push into what was already held — rather than replacing — is how an addon ends
/// up acting through authority the host has withdrawn.
///
/// **A multi-part answer arrives whole, inside one batch, its parts contiguous and ascending.** So
/// part-assembly state is per-batch by construction: carrying a half-assembled answer across ticks
/// is a bug, not resilience.
pub struct Inputs<'a> {
    bytes: &'a [u8],
}

impl<'a> Inputs<'a> {
    pub(crate) fn new(bytes: &'a [u8]) -> Self {
        Self { bytes }
    }

    /// The number of cells the host wrote this tick.
    #[must_use]
    pub fn len(&self) -> usize {
        self.bytes.len() / abi::IN_CELL_BYTES
    }

    /// Whether the host wrote no cells this tick. Never true in a well-formed batch — the `Tick`
    /// cell is mandatory.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// Iterates this tick's cells in host-written order, decoding each on demand.
    pub fn iter(&self) -> impl Iterator<Item = InCell> + '_ {
        self.bytes.chunks_exact(abi::IN_CELL_BYTES).map(InCell::decode)
    }
}

/// Typed, fixed-stride, non-allocating writer over the addon's output ring. Authors call
/// `act_bipolar`/`act_binary`/`act_unipolar`/`ask_body`/`ask_section`/`query_pose` — never write a
/// cell's bytes directly, so the layouts the host pins can never be violated from this file.
///
/// **Every emit method returns the ORDINAL of the cell it wrote**, and that ordinal is the addon's
/// only correlation key: next tick's answer carries it back (see [`InCell::ordinal`]). Remember the
/// ordinals of the cells whose answers matter; discard the rest.
///
/// **Only an `on_tick` return makes output cross.** The host zeroes this ring before every tick,
/// the first included, so a cell written from `puck_init` is erased before anyone reads it. Writing
/// from init is a silent no-op by design.
///
/// **The host faults the instance on a malformed or out-of-domain cell — stickily, for the whole
/// batch.** There is no warning tier and no clamp: a bad batch stops the addon. The writers below
/// close that off by construction where they can (a Binary act's `A` is one of exactly two values,
/// `B` and `C` are always zero on every channel act), and the analog writers carry `debug_assert`s
/// for the one bound only the caller can hold. Clamp before emitting, as the default addon does; a
/// writer that clamped silently would be changing the addon's meaning behind its back.
///
/// **Every channel act is PER-TICK, declarative, with no phase.** The host holds no lane state
/// across ticks: an addon that stops emitting an act stops contributing on that channel, whether
/// the channel is analog or a pressed/released control — a jump act with `A = fixed::ONE` means
/// pressed THIS TICK, and re-emitting it every tick it should read held is the caller's job, not
/// something the host remembers on the caller's behalf.
pub struct Outputs<'a> {
    bytes: &'a mut [u8],
    count: usize,
}

impl<'a> Outputs<'a> {
    pub(crate) fn new(bytes: &'a mut [u8]) -> Self {
        Self { bytes, count: 0 }
    }

    /// The number of cells written so far this tick — the value `puck_on_tick` returns to the host.
    #[must_use]
    pub fn len(&self) -> usize {
        self.count
    }

    /// Whether no cells have been written yet this tick.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.count == 0
    }

    /// The number of 32-byte cells reserved (`abi::OUT_CAP`).
    #[must_use]
    pub fn capacity(&self) -> usize {
        self.bytes.len() / abi::OUT_CELL_BYTES
    }

    /// Emits a Bipolar act against a declared two-sided channel, driving `drive`. The raw value must
    /// lie within `[-fixed::ONE, fixed::ONE]`.
    ///
    /// PER-TICK: an addon that stops emitting this act stops contributing on this channel, matching
    /// a seat's analog-clear behaviour.
    pub fn act_bipolar(&mut self, drive: Handle, channel: channels::ChannelHandle<channels::Bipolar>, value_raw: i64) -> u16 {
        debug_assert!(value_raw.abs() <= fixed::ONE, "a Bipolar act's A lane must lie within [-ONE, ONE]");

        self.act_input(drive, channel.index(), value_raw)
    }

    /// Emits a Binary act against a declared pressed/released channel, driving `drive`. The `A` lane
    /// is a pure BOOLEAN lane — literally `0` or [`fixed::ONE`], never an arbitrary fixed-point value
    /// — taking a `bool` here is what makes the host's pinned domain unrepresentable to violate,
    /// since it faults the instance on any other value.
    ///
    /// PER-TICK, with NO phase: `pressed = true` means pressed THIS tick, nothing more. The host
    /// holds no lane state between ticks — re-emit every tick the control should read held.
    pub fn act_binary(&mut self, drive: Handle, channel: channels::ChannelHandle<channels::Binary>, pressed: bool) -> u16 {
        let value_raw = if pressed { fixed::ONE } else { fixed::ZERO };

        self.act_input(drive, channel.index(), value_raw)
    }

    /// Emits a Unipolar act against a declared one-sided channel, driving `drive`. The raw value must
    /// lie within `[0, fixed::ONE]`. Pinned for a future channel; the interim host table declares
    /// none today.
    pub fn act_unipolar(&mut self, drive: Handle, channel: channels::ChannelHandle<channels::Unipolar>, value_raw: i64) -> u16 {
        debug_assert!((0..=fixed::ONE).contains(&value_raw), "a Unipolar act's A lane must lie within [0, ONE]");

        self.act_input(drive, channel.index(), value_raw)
    }

    /// Requests one capability over a BODY subject, named by its 0-based entity index — the addon's
    /// way of asking for authority it was not handed. The host resolves the request as
    /// `requested AND granted`, so an addon asking for more than its principal holds receives less,
    /// never more, and the answer carries the verdict either way.
    ///
    /// The mask must have exactly ONE bit set: one capability, one handle, one answer. The `u64`
    /// shape is there so multi-capability asks can be admitted later under multi-part framing
    /// without a break — it is not permission to set two bits today.
    ///
    /// A body's index is a live population TABLE POSITION, not a renumberable enum, so it keeps the
    /// plain-ordinal shape [`Self::ask_section`] deliberately does not.
    pub fn ask_body(&mut self, body_index: i64, capability: u64) -> u16 {
        debug_assert!(
            capability.count_ones() == 1,
            "an Ask's capability mask must have exactly one bit set — one capability, one handle"
        );

        // The mask crosses as the `B` lane's raw bit pattern; `as i64` reinterprets, never converts.
        self.push_cell(
            OutCellKind::Ask,
            abi::CHANNEL_REQUEST,
            Handle::UNUSED,
            SubjectKind::Body as u16,
            body_index,
            capability as i64,
            0,
        )
    }

    /// Requests one capability over a document SECTION subject, named by its declared NAME (a
    /// `Puck.World.Protocol.WorldSection` member, matched case-insensitively by the host) — NEVER by
    /// its numeric ordinal. This is the ONLY way this crate exposes to ask over a section: there is
    /// no sibling ordinal-taking method left to reach for, so a guest cannot bake a stale
    /// `WorldSection` ordinal the way a prior generation of addons did (the section vocabulary was
    /// renumbered by a host migration, and every guest that pinned the old ordinal silently asked
    /// over the WRONG section afterward — no fault, no refusal). Naming the section by TEXT closes
    /// that class structurally: there is nothing left for a renumbering to invalidate.
    ///
    /// `name`'s UTF-8 bytes cross as a `(ptr, len)` pair into THIS guest's own linear memory — the
    /// same convention [`Self::submit_mutation`] already uses for a payload, reused here rather than
    /// inventing a fresh one. The host copies the bytes out synchronously within this same call
    /// (before this method returns), so `name` needs no `'static` lifetime and nothing needs to
    /// outlive the call. Must be non-empty and at most
    /// [`crate::abi_generated::MAX_SECTION_NAME_BYTES`] bytes; the host refuses an unrecognized name
    /// BY NAME rather than resolving it to an unintended member.
    ///
    /// The mask must have exactly ONE bit set, matching [`Self::ask_body`]'s own rule.
    pub fn ask_section(&mut self, name: &str, capability: u64) -> u16 {
        debug_assert!(
            capability.count_ones() == 1,
            "an Ask's capability mask must have exactly one bit set — one capability, one handle"
        );
        debug_assert!(
            !name.is_empty() && (name.len() <= crate::abi_generated::MAX_SECTION_NAME_BYTES),
            "a section name must be non-empty and at most MAX_SECTION_NAME_BYTES UTF-8 bytes"
        );

        // wasm32-unknown-unknown pointers ARE the linear-memory byte offset the host's own
        // `AddonInstance.TryCopyMemory` indexes with — the cast widens without reinterpreting, exactly
        // like `submit_mutation`'s identical cast for a mutation payload.
        let ptr = name.as_ptr() as i64;
        let len = name.len() as i64;

        self.push_cell(
            OutCellKind::Ask,
            abi::CHANNEL_REQUEST,
            Handle::UNUSED,
            SubjectKind::Section as u16,
            ptr,
            capability as i64,
            len,
        )
    }

    /// Submits ONE mutation act with a RAW `(ptr, len)` pair the caller supplies directly, bypassing
    /// [`Self::submit_mutation`]'s `payload.as_ptr()`/`payload.len()` derivation. EXISTS ONLY for a
    /// battery guest deliberately constructing a wire-level pointer-safety violation (a negative
    /// pointer, or a length that overflows past the guest's own real linear memory) — no well-formed
    /// guest ever has a reason to reach for this over [`Self::submit_mutation`], because a safe Rust
    /// slice can never itself BE malformed in either of these ways.
    pub fn submit_mutation_raw(&mut self, mutate: Handle, kind_ordinal: u8, ptr: i64, len: i64) -> u16 {
        self.push_cell(
            OutCellKind::Act,
            abi::CHANNEL_REQUEST,
            mutate,
            REQUEST_VERB_SUBMIT_MUTATION as u16,
            kind_ordinal as i64,
            ptr,
            len,
        )
    }

    /// Submits ONE mutation payload through a Mutate handle over a document section — the addon
    /// mutation seam's own act. `kind_ordinal` is the declared `WorldMutation` kind ordinal (the
    /// World host's `MutationKindAttribute.Ordinal`, `0..=63`); `payload` is the UTF-8 JSON bytes,
    /// already written into THIS guest's own linear memory by the caller (this method only names the
    /// region — pointer and length — for the host's pointer-safety copy; it never copies the bytes
    /// itself, and `payload` must stay valid until `puck_on_tick` returns, since the host reads it
    /// synchronously within THIS SAME call, at decode time, not later).
    ///
    /// Answers exactly ONE cell next tick, carrying [`Verdict::Applied`] or a refusal — never a pose,
    /// never a multi-part answer. `mutate` must be a handle carrying the Mutate capability over a
    /// SECTION subject (from [`Self::ask`] with [`SubjectKind::Section`] and [`CAP_MUTATE`]); a
    /// handle over any other subject/capability is refused as stale.
    pub fn submit_mutation(&mut self, mutate: Handle, kind_ordinal: u8, payload: &[u8]) -> u16 {
        // wasm32-unknown-unknown pointers ARE the linear-memory byte offset the host's own
        // `AddonInstance.TryCopyMemory` indexes with — the cast widens without reinterpreting.
        let ptr = payload.as_ptr() as i64;
        let len = payload.len() as i64;

        self.push_cell(
            OutCellKind::Act,
            abi::CHANNEL_REQUEST,
            mutate,
            REQUEST_VERB_SUBMIT_MUTATION as u16,
            kind_ordinal as i64,
            ptr,
            len,
        )
    }

    /// Asks the host for the pose of the subject `observe` names — position and orientation. The
    /// answer spans FOUR cells next tick, all carrying this call's ordinal, their `verb` the 0-based
    /// part index: `(posX, posY)`, `(posZ, 0)`, `(quatX, quatY)`, `(quatZ, quatW)`, every lane raw
    /// `FixedQ4816` bits. Orientation is the body's canonical quaternion, never a yaw scalar.
    ///
    /// `observe` must be a handle carrying the Observe capability; a Drive handle is refused, and
    /// the refusal arrives as a single zero-payload answer carrying its verdict.
    pub fn query_pose(&mut self, observe: Handle) -> u16 {
        self.push_cell(
            OutCellKind::Act,
            abi::CHANNEL_REQUEST,
            observe,
            REQUEST_VERB_BODY_POSE as u16,
            0,
            0,
            0,
        )
    }

    /// Emits one input-channel `Act` through the given Drive handle, packing the declared channel
    /// index into the verb. The low `INPUT_VERB_RESERVED_BITS` bits are REQUIRED ZERO — there is no
    /// phase to pack alongside the ordinal any more — and the declared index always fits above them,
    /// the declared table being bounded by `channels::MAX_CHANNEL_NAMES`.
    fn act_input(&mut self, drive: Handle, declared_index: u16, value_raw: i64) -> u16 {
        let verb = declared_index << INPUT_VERB_RESERVED_BITS;

        self.push_cell(OutCellKind::Act, abi::CHANNEL_INPUT, drive, verb, value_raw, 0, 0)
    }

    /// Writes one 32-byte output cell at the frozen offsets, little-endian, and returns its ordinal
    /// — the cell's index in this tick's batch, which is what next tick's answer correlates against.
    #[allow(clippy::too_many_arguments)]
    fn push_cell(
        &mut self,
        kind: OutCellKind,
        channel: u8,
        handle: Handle,
        verb: u16,
        a: i64,
        b: i64,
        c: i64,
    ) -> u16 {
        let cap = self.capacity();

        assert!(
            self.count < cap,
            "output ring full ({cap} cells reserved) — raise abi::OUT_CAP or emit fewer cells per \
             tick"
        );

        let ordinal = self.count as u16;
        let offset = self.count * abi::OUT_CELL_BYTES;
        let cell = &mut self.bytes[offset..(offset + abi::OUT_CELL_BYTES)];

        cell[OUT_CELL_OFFSET_KIND] = kind as u8;
        cell[OUT_CELL_OFFSET_CHANNEL] = channel;
        cell[OUT_CELL_OFFSET_HANDLE_INDEX..(OUT_CELL_OFFSET_HANDLE_INDEX + 2)]
            .copy_from_slice(&handle.index.to_le_bytes());
        cell[OUT_CELL_OFFSET_HANDLE_GENERATION..(OUT_CELL_OFFSET_HANDLE_GENERATION + 2)]
            .copy_from_slice(&handle.generation.to_le_bytes());
        cell[OUT_CELL_OFFSET_VERB..(OUT_CELL_OFFSET_VERB + 2)].copy_from_slice(&verb.to_le_bytes());
        cell[OUT_CELL_OFFSET_A..(OUT_CELL_OFFSET_A + 8)].copy_from_slice(&a.to_le_bytes());
        cell[OUT_CELL_OFFSET_B..(OUT_CELL_OFFSET_B + 8)].copy_from_slice(&b.to_le_bytes());
        cell[OUT_CELL_OFFSET_C..(OUT_CELL_OFFSET_C + 8)].copy_from_slice(&c.to_le_bytes());

        self.count += 1;

        ordinal
    }
}

/// Maps a raw kind byte onto a known [`InCellKind`], or `None` when this build has no name for it.
/// Written against the generated variants rather than literals so the wire values live in exactly
/// one place.
fn decode_in_cell_kind(value: u8) -> Option<InCellKind> {
    match value {
        v if v == InCellKind::Tick as u8 => Some(InCellKind::Tick),
        v if v == InCellKind::Answer as u8 => Some(InCellKind::Answer),
        v if v == InCellKind::Observation as u8 => Some(InCellKind::Observation),
        _ => None,
    }
}

/// Maps a raw verdict byte onto a known [`Verdict`], or `None` when this build has no name for it.
/// The verdict set grows as data — a new denial reason is not a break — so an unknown byte is a
/// cell to skip, never one to read optimistically as allowed.
fn decode_verdict(value: u8) -> Option<Verdict> {
    match value {
        v if v == Verdict::None as u8 => Some(Verdict::None),
        v if v == Verdict::HeldConcrete as u8 => Some(Verdict::HeldConcrete),
        v if v == Verdict::HeldWildcard as u8 => Some(Verdict::HeldWildcard),
        v if v == Verdict::HeldAsReserver as u8 => Some(Verdict::HeldAsReserver),
        v if v == Verdict::NoHold as u8 => Some(Verdict::NoHold),
        v if v == Verdict::BeatenByReserver as u8 => Some(Verdict::BeatenByReserver),
        v if v == Verdict::AttenuatedToEmpty as u8 => Some(Verdict::AttenuatedToEmpty),
        v if v == Verdict::NoSuchSubject as u8 => Some(Verdict::NoSuchSubject),
        v if v == Verdict::QuotaExhausted as u8 => Some(Verdict::QuotaExhausted),
        v if v == Verdict::StaleHandle as u8 => Some(Verdict::StaleHandle),
        v if v == Verdict::Applied as u8 => Some(Verdict::Applied),
        v if v == Verdict::MalformedPayload as u8 => Some(Verdict::MalformedPayload),
        v if v == Verdict::PayloadTooLarge as u8 => Some(Verdict::PayloadTooLarge),
        v if v == Verdict::Rejected as u8 => Some(Verdict::Rejected),
        _ => None,
    }
}

/// Reads a little-endian `u16` at `offset`. A short slice cannot occur — every caller passes a full
/// cell — so a failed conversion falls back to zero rather than trapping the whole instance.
fn read_u16(bytes: &[u8], offset: usize) -> u16 {
    u16::from_le_bytes(bytes[offset..(offset + 2)].try_into().unwrap_or_default())
}

/// Reads a little-endian `i64` at `offset`. See [`read_u16`] on the fallback.
fn read_i64(bytes: &[u8], offset: usize) -> i64 {
    i64::from_le_bytes(bytes[offset..(offset + 8)].try_into().unwrap_or_default())
}
