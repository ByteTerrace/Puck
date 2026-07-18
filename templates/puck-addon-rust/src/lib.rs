//! Puck `puck.addon.v1` WASM addon template.
//!
//! Authors write only two things: the body of `on_tick` (and, optionally, `on_init`) below, using
//! the typed [`Snapshot`] reader and [`Commands`] writer — never raw byte offsets — and the pad
//! IDs in [`pad`]. `abi.rs` wires those typed views over the frozen ABI's static memory regions;
//! `fixed.rs` is the bit-exact `FixedQ4816` mirror all values on the wire are expressed in.
//!
//! The example below is the Rust twin of the demo's "autopilot ghost": dead-reckoning walk to a
//! fixed target position, then one latched `PadNorth` press, exactly as described in the design
//! brief (plan-of-record E.5) — sub + clamp only, no mul/div/sqrt/normalize needed.

pub mod abi;
pub mod fixed;

#[cfg(test)]
mod fixed_tests;

/// The frozen virtual-pad vocabulary (`puck.addon.v1` A.5) — a closed, ABI-versioned set; do not
/// invent new IDs, they are meaningless to the host.
pub mod pad {
    /// `PadMove` — Axis2D; camera-relative floor-plane strafe (X) / forward (Y).
    pub const MOVE: u16 = 0;
    /// `PadSouth` — Digital.
    pub const SOUTH: u16 = 1;
    /// `PadEast` — Digital.
    pub const EAST: u16 = 2;
    /// `PadWest` — Digital.
    pub const WEST: u16 = 3;
    /// `PadNorth` — Digital.
    pub const NORTH: u16 = 4;
    /// `PadShoulderLeft` — Digital.
    pub const SHOULDER_LEFT: u16 = 5;
    /// `PadShoulderRight` — Digital.
    pub const SHOULDER_RIGHT: u16 = 6;
    /// `PadTriggerLeft` — Axis1D.
    pub const TRIGGER_LEFT: u16 = 7;
    /// `PadTriggerRight` — Axis1D.
    pub const TRIGGER_RIGHT: u16 = 8;
    /// `PadRightStick` — Axis2D.
    pub const RIGHT_STICK: u16 = 9;
}

/// Digital button bit positions in `Snapshot::buttons()` (`puck.addon.v1` A.4) — independent of
/// the `pad` module's output numbering above; this is the *input* view of the addon's own roster
/// slot.
#[repr(u32)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PadButton {
    /// Bit 0.
    South = 0,
    /// Bit 1.
    East = 1,
    /// Bit 2.
    West = 2,
    /// Bit 3.
    North = 3,
    /// Bit 4.
    ShoulderLeft = 4,
    /// Bit 5.
    ShoulderRight = 5,
    /// Bit 6.
    TriggerLeft = 6,
    /// Bit 7.
    TriggerRight = 7,
    /// Bit 8.
    DpadUp = 8,
    /// Bit 9.
    DpadDown = 9,
    /// Bit 10.
    DpadLeft = 10,
    /// Bit 11.
    DpadRight = 11,
    /// Bit 12.
    Start = 12,
    /// Bit 13.
    Select = 13,
    /// Bit 14.
    StickL = 14,
    /// Bit 15.
    StickR = 15,
}

/// Mirrors the host's `CommandPhase` (`puck.addon.v1` A.3 offset 2) — never write a raw byte,
/// always a variant of this enum.
#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CommandPhase {
    /// The gesture began this tick (an edge).
    Started = 0,
    /// The gesture is ongoing (held).
    Active = 1,
    /// The gesture ended normally this tick (an edge).
    Completed = 2,
    /// The gesture was canceled this tick (an edge).
    Canceled = 3,
}

/// Typed, read-only view over the 40-byte host-written snapshot region (`puck.addon.v1` A.2).
/// Every accessor here reads the exact field offsets the ABI freezes — never index the bytes
/// directly.
pub struct Snapshot<'a> {
    bytes: &'a [u8; abi::SNAPSHOT_BYTES],
}

impl<'a> Snapshot<'a> {
    pub(crate) fn from_bytes(bytes: &'a [u8; abi::SNAPSHOT_BYTES]) -> Self {
        Self { bytes }
    }

    /// The engine tick this snapshot was written for (50400 Hz timebase; offset 0).
    #[must_use]
    pub fn tick(&self) -> u64 {
        i64::from_le_bytes(self.bytes[0..8].try_into().unwrap_or_default()) as u64
    }

    /// Local-space X (strafe axis), raw `FixedQ4816` bits (offset 8).
    #[must_use]
    pub fn pos_local_x(&self) -> i64 {
        i64::from_le_bytes(self.bytes[8..16].try_into().unwrap_or_default())
    }

    /// Local-space Y (up axis), raw `FixedQ4816` bits (offset 16).
    #[must_use]
    pub fn pos_local_y(&self) -> i64 {
        i64::from_le_bytes(self.bytes[16..24].try_into().unwrap_or_default())
    }

    /// Local-space Z (forward axis; cabinets sit at negative Z), raw `FixedQ4816` bits (offset 24).
    #[must_use]
    pub fn pos_local_z(&self) -> i64 {
        i64::from_le_bytes(self.bytes[24..32].try_into().unwrap_or_default())
    }

    /// The addon's own roster slot's digital-button bitfield (offset 32; `puck.addon.v1` A.4).
    #[must_use]
    pub fn buttons(&self) -> u32 {
        u32::from_le_bytes(self.bytes[32..36].try_into().unwrap_or_default())
    }

    /// Whether the given button is currently held in this snapshot's `buttons()` bitfield.
    #[must_use]
    pub fn button_held(&self, button: PadButton) -> bool {
        (self.buttons() & (1u32 << (button as u32))) != 0
    }
}

/// Typed, fixed-stride, non-allocating writer over the guest's command output region
/// (`puck.addon.v1` A.3). Authors call `push_move`/`push_digital`/`push_axis1d` — never write a
/// record's bytes directly, so the reserved-must-be-zero fields and the Digital/Axis1D
/// `valueY == 0` invariant (A.8) can never be violated from this file.
pub struct Commands<'a> {
    bytes: &'a mut [u8],
    count: usize,
}

impl<'a> Commands<'a> {
    pub(crate) fn new(bytes: &'a mut [u8]) -> Self {
        Self { bytes, count: 0 }
    }

    /// The number of records written so far this tick — the value `puck_on_tick` returns to the
    /// host.
    #[must_use]
    pub fn len(&self) -> usize {
        self.count
    }

    /// Whether no records have been written yet this tick.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.count == 0
    }

    /// The number of 24-byte slots reserved (`abi::COMMANDS_CAP`).
    #[must_use]
    pub fn capacity(&self) -> usize {
        self.bytes.len() / abi::COMMAND_RECORD_BYTES
    }

    /// Emits a `PadMove` (Axis2D) record — X = strafe, Y = forward, already camera-relative
    /// floor-plane, matching the host's `PlayerIntent.Move` frame exactly. **Do not negate Y** —
    /// the raw-pad path negates because it reads raw stick space; an addon speaks the intent's own
    /// frame (plan-of-record A.5).
    pub fn push_move(&mut self, x_raw: i64, y_raw: i64) {
        self.push_record(pad::MOVE, CommandPhase::Active, x_raw, y_raw);
    }

    /// Emits a Digital record (`PadSouth`/`PadEast`/`PadWest`/`PadNorth`/`PadShoulderLeft`/
    /// `PadShoulderRight`) with the given phase. `pressed` selects `fixed::ONE` or `fixed::ZERO`
    /// for `valueX`; `valueY` is always zero, as the ABI requires for Digital records.
    pub fn push_digital(&mut self, pad_id: u16, phase: CommandPhase, pressed: bool) {
        let value_x = if pressed { fixed::ONE } else { fixed::ZERO };

        self.push_record(pad_id, phase, value_x, fixed::ZERO);
    }

    /// Emits an Axis1D record (`PadTriggerLeft`/`PadTriggerRight`) — `valueY` is always zero, as
    /// the ABI requires for Axis1D records.
    pub fn push_axis1d(&mut self, pad_id: u16, value_raw: i64) {
        self.push_record(pad_id, CommandPhase::Active, value_raw, fixed::ZERO);
    }

    fn push_record(&mut self, pad_id: u16, phase: CommandPhase, value_x: i64, value_y: i64) {
        let cap = self.capacity();

        assert!(
            self.count < cap,
            "Commands buffer full ({cap} slots reserved) — raise abi::COMMANDS_CAP or emit fewer \
             records per tick"
        );

        let offset = self.count * abi::COMMAND_RECORD_BYTES;
        let record = &mut self.bytes[offset..(offset + abi::COMMAND_RECORD_BYTES)];

        record[0..2].copy_from_slice(&pad_id.to_le_bytes());
        record[2] = phase as u8;
        record[3] = 0; // reserved0 — MUST be zero (puck.addon.v1 A.3/A.8)
        record[4..8].copy_from_slice(&0u32.to_le_bytes()); // reserved1 — MUST be zero
        record[8..16].copy_from_slice(&value_x.to_le_bytes());
        record[16..24].copy_from_slice(&value_y.to_le_bytes());

        self.count += 1;
    }
}

// --- Example addon: the ghost's dead-reckoning clamp-walk (plan-of-record E.5) --------------

// The center cabinet in the demo's single-room overworld sits at local (X=0, Z=-6.6); the raw
// FixedQ4816 bits below are FixedQ4816::from_double(-6.6) computed by hand: -6.6 * 65536 =
// -432537.6, rounded to the nearest integer (no tie — .6 is unambiguous) = -432538.
const TARGET_X_RAW: i64 = 0;
const TARGET_Z_RAW: i64 = -432_538;

// The proximity-to-interact range, per axis (~1.8 in Q16): 1.8 * 65536 = 117964.8, rounded = 117965.
const PROXIMITY_RAW: i64 = 117_965;

// One-shot latch so the `PadNorth` interact press fires exactly once, per plan-of-record E.5
// ("latched via a guest global"). Like the ABI buffers in abi.rs, this is only ever touched from
// the single sim-tick thread that calls puck_on_tick.
static mut INTERACTED: bool = false;

/// Optional guest setup hook, called once before the first tick. The template's example needs no
/// setup; override this if your addon does.
pub fn on_init() {}

/// Called once per sim tick. Reads the host-written `snapshot`, writes zero or more records into
/// `commands` via `push_move`/`push_digital`/`push_axis1d`. Replace this body with your own addon.
pub fn on_tick(snapshot: &Snapshot, commands: &mut Commands) {
    let dx = fixed::sub(TARGET_X_RAW, snapshot.pos_local_x());
    let dz = fixed::sub(TARGET_Z_RAW, snapshot.pos_local_z());

    // Per-axis clamp only — no mul/div/sqrt/normalize needed; a per-axis clamp already guarantees
    // each component's magnitude is <= 1, and the sim clamps overall move magnitude anyway.
    let move_x = fixed::clamp(dx, fixed::NEGATIVE_ONE, fixed::ONE);
    let move_y = fixed::clamp(dz, fixed::NEGATIVE_ONE, fixed::ONE);

    commands.push_move(move_x, move_y);

    let close = (dx.abs() < PROXIMITY_RAW) && (dz.abs() < PROXIMITY_RAW);

    // SAFETY: single sim-tick thread, no re-entrancy — see abi.rs's puck_on_tick SAFETY note.
    let already_interacted = unsafe { INTERACTED };

    if close && !already_interacted {
        commands.push_digital(pad::NORTH, CommandPhase::Started, true);

        // SAFETY: single sim-tick thread, no re-entrancy — see abi.rs's puck_on_tick SAFETY note.
        unsafe {
            INTERACTED = true;
        }
    }
}
