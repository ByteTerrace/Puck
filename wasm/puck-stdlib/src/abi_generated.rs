//! GENERATED — do not hand-edit. Regenerate with:
//!
//! ```text
//! dotnet run --project src/Puck.Cli -c Release -- wasm-stdlib
//! ```
//!
//! Mirrored bit-for-bit from the live C# host and read by name — never retyped — so this module
//! cannot silently drift from the types it mirrors:
//!
//! - [`OutCellKind`], [`InCellKind`], [`ChannelKind`], [`SubjectKind`], and [`Verdict`] mirror
//!   `Puck.Scripting.AddonOutCellKind`, `AddonInCellKind`, `AddonChannelKind`, `AddonSubjectKind`,
//!   and `AddonVerdict` respectively — the addon ABI's wire value sets, pinned independently of any
//!   consumer enum. `Verdict::is_allowed` mirrors `Puck.Scripting.AddonVerdicts.IsAllowed`, generated
//!   from the live predicate rather than hand-listed.
//! - `CAP_*` mirror `Puck.Scripting.AddonCapabilityMask` (`src/Puck.Scripting/AddonCapabilityMask.cs`)
//!   as raw `u64` mask values, not bit positions.
//! - The remaining constants mirror every public constant on `Puck.Scripting.AddonAbi`
//!   (`src/Puck.Scripting/AddonAbi.cs`) and its nested `OutCellOffsets`/`InCellOffsets`/
//!   `ChannelDescriptorOffsets`/`RequestVerbs`/`ObservationVerbs` classes: the frozen byte layout,
//!   sizes, version, and budgets the addon ABI freezes. `OUT_CELL_OFFSET_*`/`IN_CELL_OFFSET_*`/
//!   `CHANNEL_DESCRIPTOR_OFFSET_*`/`REQUEST_VERB_*`/`OBSERVATION_VERB_*` carry their nested class's
//!   name as a prefix; every other constant keeps `AddonAbi`'s own name.
//!
//! This mirrors `AddonAbi`'s COMPLETE public constant surface, not just the fields this
//! crate's typed accessors happen to consume today — cherry-picking which constants to
//! generate would reintroduce exactly the drift risk this file exists to close, the moment a
//! future consumer needs one that generation skipped. `#![allow(dead_code)]` accordingly:
//! this is a private module (see `lib.rs`), so a `pub const`/`pub enum` variant unused
//! in-crate today is expected, not a mistake.

#![allow(dead_code)]

/// Mirrors `Puck.Scripting.AddonOutCellKind` (`src/Puck.Scripting/AddonOutCellKind.cs`).
#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OutCellKind {
    /// `Puck.Scripting.AddonOutCellKind.Act` (`1`).
    Act = 1,
    /// `Puck.Scripting.AddonOutCellKind.Ask` (`2`).
    Ask = 2,
}

/// Mirrors `Puck.Scripting.AddonInCellKind` (`src/Puck.Scripting/AddonInCellKind.cs`).
#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum InCellKind {
    /// `Puck.Scripting.AddonInCellKind.Tick` (`1`).
    Tick = 1,
    /// `Puck.Scripting.AddonInCellKind.Answer` (`2`).
    Answer = 2,
    /// `Puck.Scripting.AddonInCellKind.Observation` (`3`).
    Observation = 3,
}

/// Mirrors `Puck.Scripting.AddonChannelKind` (`src/Puck.Scripting/AddonChannelKind.cs`).
#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ChannelKind {
    /// `Puck.Scripting.AddonChannelKind.Input` (`1`).
    Input = 1,
    /// `Puck.Scripting.AddonChannelKind.Request` (`2`).
    Request = 2,
    /// `Puck.Scripting.AddonChannelKind.Response` (`3`).
    Response = 3,
}

/// Mirrors `Puck.Scripting.AddonSubjectKind` (`src/Puck.Scripting/AddonSubjectKind.cs`).
#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SubjectKind {
    /// `Puck.Scripting.AddonSubjectKind.Body` (`1`).
    Body = 1,
    /// `Puck.Scripting.AddonSubjectKind.Screen` (`2`).
    Screen = 2,
    /// `Puck.Scripting.AddonSubjectKind.Section` (`3`).
    Section = 3,
    /// `Puck.Scripting.AddonSubjectKind.Profile` (`4`).
    Profile = 4,
}

/// Mirrors `Puck.Scripting.AddonVerdict` (`src/Puck.Scripting/AddonVerdict.cs`).
#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Verdict {
    /// `Puck.Scripting.AddonVerdict.None` (`0`).
    None = 0,
    /// `Puck.Scripting.AddonVerdict.HeldConcrete` (`1`).
    HeldConcrete = 1,
    /// `Puck.Scripting.AddonVerdict.HeldWildcard` (`2`).
    HeldWildcard = 2,
    /// `Puck.Scripting.AddonVerdict.HeldAsReserver` (`3`).
    HeldAsReserver = 3,
    /// `Puck.Scripting.AddonVerdict.NoHold` (`4`).
    NoHold = 4,
    /// `Puck.Scripting.AddonVerdict.BeatenByReserver` (`5`).
    BeatenByReserver = 5,
    /// `Puck.Scripting.AddonVerdict.AttenuatedToEmpty` (`6`).
    AttenuatedToEmpty = 6,
    /// `Puck.Scripting.AddonVerdict.NoSuchSubject` (`7`).
    NoSuchSubject = 7,
    /// `Puck.Scripting.AddonVerdict.QuotaExhausted` (`8`).
    QuotaExhausted = 8,
    /// `Puck.Scripting.AddonVerdict.StaleHandle` (`9`).
    StaleHandle = 9,
    /// `Puck.Scripting.AddonVerdict.Applied` (`10`).
    Applied = 10,
    /// `Puck.Scripting.AddonVerdict.MalformedPayload` (`11`).
    MalformedPayload = 11,
    /// `Puck.Scripting.AddonVerdict.PayloadTooLarge` (`12`).
    PayloadTooLarge = 12,
    /// `Puck.Scripting.AddonVerdict.Rejected` (`13`).
    Rejected = 13,
}

impl Verdict {
    /// Mirrors `Puck.Scripting.AddonVerdicts.IsAllowed`, generated from the live predicate.
    pub fn is_allowed(self) -> bool {
        matches!(self, Verdict::HeldConcrete | Verdict::HeldWildcard | Verdict::HeldAsReserver | Verdict::Applied)
    }
}

// `Puck.Scripting.AddonCapabilityMask` (`src/Puck.Scripting/AddonCapabilityMask.cs`) — the addon ABI's Ask capability-mask bit values.
/// `AddonCapabilityMask.All` (`63`).
pub const CAP_ALL: u64 = 63;
/// `AddonCapabilityMask.Control` (`8`).
pub const CAP_CONTROL: u64 = 8;
/// `AddonCapabilityMask.Drive` (`1`).
pub const CAP_DRIVE: u64 = 1;
/// `AddonCapabilityMask.Edit` (`32`).
pub const CAP_EDIT: u64 = 32;
/// `AddonCapabilityMask.Mutate` (`16`).
pub const CAP_MUTATE: u64 = 16;
/// `AddonCapabilityMask.Observe` (`2`).
pub const CAP_OBSERVE: u64 = 2;
/// `AddonCapabilityMask.Reserved` (`4`).
pub const CAP_RESERVED: u64 = 4;

// `Puck.Scripting.AddonAbi` constants (`src/Puck.Scripting/AddonAbi.cs`).
/// `AddonAbi.AbiVersion` (`1`).
pub const ABI_VERSION: i32 = 1;
/// `AddonAbi.ChannelDescriptorBytes` (`16`).
pub const CHANNEL_DESCRIPTOR_BYTES: usize = 16;
/// `AddonAbi.DefaultFuelPerTick` (`1000000`).
pub const DEFAULT_FUEL_PER_TICK: i64 = 1000000;
/// `AddonAbi.InCellBytes` (`32`).
pub const IN_CELL_BYTES: usize = 32;
/// `AddonAbi.InputVerbReservedBits` (`2`).
pub const INPUT_VERB_RESERVED_BITS: usize = 2;
/// `AddonAbi.InputVerbReservedMask` (`3`).
pub const INPUT_VERB_RESERVED_MASK: usize = 3;
/// `AddonAbi.MaxChannelNameBytes` (`64`).
pub const MAX_CHANNEL_NAME_BYTES: usize = 64;
/// `AddonAbi.MaxChannelNames` (`64`).
pub const MAX_CHANNEL_NAMES: usize = 64;
/// `AddonAbi.MaxChannels` (`8`).
pub const MAX_CHANNELS: usize = 8;
/// `AddonAbi.MaxInCells` (`64`).
pub const MAX_IN_CELLS: usize = 64;
/// `AddonAbi.MaxMutationBytesPerTickAllAddons` (`65536`).
pub const MAX_MUTATION_BYTES_PER_TICK_ALL_ADDONS: usize = 65536;
/// `AddonAbi.MaxMutationBytesPerTickPerAddon` (`16384`).
pub const MAX_MUTATION_BYTES_PER_TICK_PER_ADDON: usize = 16384;
/// `AddonAbi.MaxMutationPayloadBytes` (`8192`).
pub const MAX_MUTATION_PAYLOAD_BYTES: usize = 8192;
/// `AddonAbi.MaxOutCells` (`63`).
pub const MAX_OUT_CELLS: usize = 63;
/// `AddonAbi.MaxSectionNameBytes` (`32`).
pub const MAX_SECTION_NAME_BYTES: usize = 32;
/// `AddonAbi.MaxStackBytes` (`524288`).
pub const MAX_STACK_BYTES: usize = 524288;
/// `AddonAbi.One` (`65536`).
pub const ONE: i64 = 65536;
/// `AddonAbi.OutCellBytes` (`32`).
pub const OUT_CELL_BYTES: usize = 32;

// `Puck.Scripting.AddonAbi.OutCellOffsets` — the guest→host output cell field offsets.
/// `AddonAbi.OutCellOffsets.A` (`8`).
pub const OUT_CELL_OFFSET_A: usize = 8;
/// `AddonAbi.OutCellOffsets.B` (`16`).
pub const OUT_CELL_OFFSET_B: usize = 16;
/// `AddonAbi.OutCellOffsets.C` (`24`).
pub const OUT_CELL_OFFSET_C: usize = 24;
/// `AddonAbi.OutCellOffsets.Channel` (`1`).
pub const OUT_CELL_OFFSET_CHANNEL: usize = 1;
/// `AddonAbi.OutCellOffsets.HandleGeneration` (`4`).
pub const OUT_CELL_OFFSET_HANDLE_GENERATION: usize = 4;
/// `AddonAbi.OutCellOffsets.HandleIndex` (`2`).
pub const OUT_CELL_OFFSET_HANDLE_INDEX: usize = 2;
/// `AddonAbi.OutCellOffsets.Kind` (`0`).
pub const OUT_CELL_OFFSET_KIND: usize = 0;
/// `AddonAbi.OutCellOffsets.Verb` (`6`).
pub const OUT_CELL_OFFSET_VERB: usize = 6;

// `Puck.Scripting.AddonAbi.InCellOffsets` — the host→guest input cell field offsets.
/// `AddonAbi.InCellOffsets.A` (`16`).
pub const IN_CELL_OFFSET_A: usize = 16;
/// `AddonAbi.InCellOffsets.B` (`24`).
pub const IN_CELL_OFFSET_B: usize = 24;
/// `AddonAbi.InCellOffsets.Channel` (`1`).
pub const IN_CELL_OFFSET_CHANNEL: usize = 1;
/// `AddonAbi.InCellOffsets.HandleGeneration` (`6`).
pub const IN_CELL_OFFSET_HANDLE_GENERATION: usize = 6;
/// `AddonAbi.InCellOffsets.HandleIndex` (`4`).
pub const IN_CELL_OFFSET_HANDLE_INDEX: usize = 4;
/// `AddonAbi.InCellOffsets.Kind` (`0`).
pub const IN_CELL_OFFSET_KIND: usize = 0;
/// `AddonAbi.InCellOffsets.Ordinal` (`2`).
pub const IN_CELL_OFFSET_ORDINAL: usize = 2;
/// `AddonAbi.InCellOffsets.Reserved0` (`10`).
pub const IN_CELL_OFFSET_RESERVED0: usize = 10;
/// `AddonAbi.InCellOffsets.Reserved1` (`12`).
pub const IN_CELL_OFFSET_RESERVED1: usize = 12;
/// `AddonAbi.InCellOffsets.Verb` (`9`).
pub const IN_CELL_OFFSET_VERB: usize = 9;
/// `AddonAbi.InCellOffsets.Verdict` (`8`).
pub const IN_CELL_OFFSET_VERDICT: usize = 8;

// `Puck.Scripting.AddonAbi.ChannelDescriptorOffsets` — the channel descriptor table field offsets.
/// `AddonAbi.ChannelDescriptorOffsets.Kind` (`0`).
pub const CHANNEL_DESCRIPTOR_OFFSET_KIND: usize = 0;
/// `AddonAbi.ChannelDescriptorOffsets.Reserved0` (`1`).
pub const CHANNEL_DESCRIPTOR_OFFSET_RESERVED0: usize = 1;
/// `AddonAbi.ChannelDescriptorOffsets.Reserved1` (`8`).
pub const CHANNEL_DESCRIPTOR_OFFSET_RESERVED1: usize = 8;
/// `AddonAbi.ChannelDescriptorOffsets.VerbCount` (`2`).
pub const CHANNEL_DESCRIPTOR_OFFSET_VERB_COUNT: usize = 2;
/// `AddonAbi.ChannelDescriptorOffsets.VerbTablePtr` (`4`).
pub const CHANNEL_DESCRIPTOR_OFFSET_VERB_TABLE_PTR: usize = 4;

// `Puck.Scripting.AddonAbi.RequestVerbs` — the Request channel's closed numeric vocabulary.
/// `AddonAbi.RequestVerbs.BodyPose` (`0`).
pub const REQUEST_VERB_BODY_POSE: usize = 0;
/// `AddonAbi.RequestVerbs.BodyPoseAnswerParts` (`4`).
pub const REQUEST_VERB_BODY_POSE_ANSWER_PARTS: usize = 4;
/// `AddonAbi.RequestVerbs.Count` (`3`).
pub const REQUEST_VERB_COUNT: usize = 3;
/// `AddonAbi.RequestVerbs.Designate` (`2`).
pub const REQUEST_VERB_DESIGNATE: usize = 2;
/// `AddonAbi.RequestVerbs.DesignateAnswerParts` (`1`).
pub const REQUEST_VERB_DESIGNATE_ANSWER_PARTS: usize = 1;
/// `AddonAbi.RequestVerbs.SubmitMutation` (`1`).
pub const REQUEST_VERB_SUBMIT_MUTATION: usize = 1;
/// `AddonAbi.RequestVerbs.SubmitMutationAnswerParts` (`1`).
pub const REQUEST_VERB_SUBMIT_MUTATION_ANSWER_PARTS: usize = 1;

// `Puck.Scripting.AddonAbi.ObservationVerbs` — the host-written disclosure verb vocabulary.
/// `AddonAbi.ObservationVerbs.EventCollisionBegin` (`5`).
pub const OBSERVATION_VERB_EVENT_COLLISION_BEGIN: usize = 5;
/// `AddonAbi.ObservationVerbs.EventCollisionEnd` (`6`).
pub const OBSERVATION_VERB_EVENT_COLLISION_END: usize = 6;
/// `AddonAbi.ObservationVerbs.EventGap` (`10`).
pub const OBSERVATION_VERB_EVENT_GAP: usize = 10;
/// `AddonAbi.ObservationVerbs.EventMachineMemoryChanged` (`9`).
pub const OBSERVATION_VERB_EVENT_MACHINE_MEMORY_CHANGED: usize = 9;
/// `AddonAbi.ObservationVerbs.EventRegionEnter` (`1`).
pub const OBSERVATION_VERB_EVENT_REGION_ENTER: usize = 1;
/// `AddonAbi.ObservationVerbs.EventRegionExit` (`2`).
pub const OBSERVATION_VERB_EVENT_REGION_EXIT: usize = 2;
/// `AddonAbi.ObservationVerbs.EventRouteDisengaged` (`8`).
pub const OBSERVATION_VERB_EVENT_ROUTE_DISENGAGED: usize = 8;
/// `AddonAbi.ObservationVerbs.EventRouteEngaged` (`7`).
pub const OBSERVATION_VERB_EVENT_ROUTE_ENGAGED: usize = 7;
/// `AddonAbi.ObservationVerbs.EventSeatJoin` (`3`).
pub const OBSERVATION_VERB_EVENT_SEAT_JOIN: usize = 3;
/// `AddonAbi.ObservationVerbs.EventSeatLeave` (`4`).
pub const OBSERVATION_VERB_EVENT_SEAT_LEAVE: usize = 4;
/// `AddonAbi.ObservationVerbs.GrantedBody` (`0`).
pub const OBSERVATION_VERB_GRANTED_BODY: usize = 0;

