//! **The blank-slate campaign's senses-lane PROOF GUEST** (lane A — observation-cell event delivery).
//! Never shipped, pinned by no shipped world. Demonstrates the region-events family end to end: on
//! `Observation` cells the host pushes once mounted under an `observe region:<name> events:<n>`
//! grant, this guest writes a VISIBLE reaction — an `UpsertHudPanel` mutation over `section:hud` —
//! so a console script can read the effect through `world.hud` rather than trusting only the host's
//! own stderr narration.
//!
//! **Mount requirement:** `world.grant addon:<name> observe region:<name> budget:<n> events:<n>`
//! (`Server.WorldGrants.TryGrant`'s `metered` list is `Observe`/`Drive`/`Mutate` checked before any
//! subject-kind distinction, so an untrusted principal's Observe row needs `budget:<n>` regardless of
//! subject — a region subject carrying no query meaning only adds the `events:<n>` requirement on top,
//! it does not waive the dispatch budget) PLUS
//! `world.grant addon:<name> mutate section:hud verbs:UpsertHudPanel budget:<n>` (the HUD write —
//! reuses the SAME `puck-addon-hudbuilder`-established Mutate/Ask/SubmitMutation sequence).
//!
//! **Behavior, in one state machine:** ask for the Mutate handle over `section:hud` once, at mount;
//! from then on, EVERY tick that carries an `EventRegionEnter`/`EventRegionExit` Observation cell for
//! the watched region submits a fresh `UpsertHudPanel` whose text names the edge ("entered"/"exited")
//! and this mount's own lifetime `EventGap` count (`AddonAbi.ObservationVerbs.EventGap`) — the
//! overflow doctrine's resync signal, surfaced here so a runner can assert it stayed zero across an
//! ordinary walk-in/walk-out without reading host stderr. A guest reacting to MULTIPLE edges over its
//! lifetime (not a one-shot probe) is the point: the proof is that this keeps working every time the
//! body crosses the boundary, not just once.
//!
//! **Section ask is name-keyed, not ordinal-keyed.** This guest used to bake `WorldSection.Hud`'s
//! declaration-order ordinal (`23`) as a literal constant, which a prior host-side renumbering left
//! silently stale (Hud moved to 22 — a still-defined member, so the guest asked over the WRONG
//! section with no fault, no refusal). `Outputs::ask_section` closes that class structurally: the
//! guest now names the section by TEXT (`HUD_SECTION_NAME`), resolved host-side against the live
//! `WorldSection` vocabulary — see `puck-addon-arcade`'s identical fix for the full account.

use puck_stdlib::{
    abi, Handle, InCellKind, Inputs, Outputs, CAP_MUTATE, OBSERVATION_VERB_EVENT_GAP,
    OBSERVATION_VERB_EVENT_REGION_ENTER, OBSERVATION_VERB_EVENT_REGION_EXIT,
};

// A declared Input channel with a nonempty verb table is required by the handshake even though this
// guest never drives a body — mirrors puck-addon-hudbuilder's identical shim.
puck_stdlib::channels! {
    static channels;
    const UNUSED: Bipolar = "unused";
}

/// `Puck.World.Protocol.WorldSection.Hud`'s declared NAME — the `Ask`'s subject, name-keyed rather
/// than ordinal-keyed (`Outputs::ask_section`). This constant used to carry Hud's declaration-order
/// ORDINAL (`23`), which a prior `WorldSection` renumbering left silently stale (Hud moved to 22 —
/// still a defined member, so the guest asked over the wrong section with no fault, no refusal). A
/// name has no ordinal to strand; see `puck-addon-arcade`'s identical fix for the full account.
const HUD_SECTION_NAME: &str = "Hud";

/// `[MutationKind(ordinal: 44, section: WorldSection.Hud)] WorldMutation.UpsertHudPanel`'s declared
/// dispatch ordinal.
const KIND_UPSERT_HUD_PANEL: u8 = 41;

/// The one HUD panel this guest ever writes — a single text row, overwritten (whole-row upsert, never
/// a field poke) on every edge.
const PANEL_ID: &str = "eventwatch";

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

#[no_mangle]
pub extern "C" fn puck_channels_ptr() -> i32 {
    abi::channels_ptr(channels::ptr(), channels::count())
}

#[no_mangle]
pub extern "C" fn puck_channels_count() -> i32 {
    abi::channels_count()
}

#[no_mangle]
pub extern "C" fn puck_init() {}

#[no_mangle]
pub extern "C" fn puck_on_tick(input_count: i32) -> i32 {
    abi::dispatch_tick(input_count, on_tick)
}

/// Builds the one panel this guest ever writes: a single text row naming the edge and this mount's
/// own lifetime gap count.
fn panel_json(edge: &str, gap: i64) -> String {
    format!(
        r#"{{"id":"{PANEL_ID}","rect":{{"x":0.02,"y":0.02,"width":0.3,"height":0.12}},"layer":"over","style":"panel","elements":[{{"id":"v","kind":"text","rect":{{"x":0.0,"y":0.0,"width":1.0,"height":1.0}},"style":"primary","text":"{edge} gap:{gap}"}}]}}"#
    )
}

#[derive(Clone, Copy)]
struct State {
    mutate: Option<Handle>,
    pending_ask: Option<u16>,
    /// This mount's own lifetime EventGap count, as last reported by the host (see
    /// `AddonAbi.ObservationVerbs.EventGap`'s own doc) — zero until the host's first gap cell, which
    /// only ever arrives once the ring has actually dropped something.
    gap: i64,
}

impl State {
    const EMPTY: Self = Self { mutate: None, pending_ask: None, gap: 0 };
}

// Single sim-tick thread, no re-entrancy — see puck_stdlib::abi::dispatch_tick's safety note. State is
// Copy — a value read here, a value written at the bottom, mirroring every other addon crate here.
static mut STATE: State = State::EMPTY;

fn on_tick(inputs: &Inputs, outputs: &mut Outputs) {
    // SAFETY: see the STATE doc above.
    let mut state = unsafe { STATE };
    let mut edge: Option<&'static str> = None;

    for cell in inputs.iter() {
        match cell.kind {
            Some(InCellKind::Observation) => {
                if cell.verb == OBSERVATION_VERB_EVENT_REGION_ENTER as u8 {
                    edge = Some("entered");
                } else if cell.verb == OBSERVATION_VERB_EVENT_REGION_EXIT as u8 {
                    edge = Some("exited");
                } else if cell.verb == OBSERVATION_VERB_EVENT_GAP as u8 {
                    state.gap = cell.a;
                }
            }
            Some(InCellKind::Answer) => {
                if state.pending_ask == Some(cell.ordinal) {
                    state.pending_ask = None;

                    if matches!(cell.verdict, Some(v) if v.is_allowed()) {
                        state.mutate = Some(cell.handle);
                    }
                }
                // Every SubmitMutation act this guest fires is genuinely fire-and-forget (each edge
                // is a fresh, independent whole-row upsert) — there is nothing further to correlate
                // an UpsertHudPanel's own Answer back to.
            }
            Some(InCellKind::Tick) | None => {}
        }
    }

    if state.mutate.is_none() && state.pending_ask.is_none() {
        state.pending_ask = Some(outputs.ask_section(HUD_SECTION_NAME, CAP_MUTATE));
    }

    if let (Some(mutate), Some(edge)) = (state.mutate, edge) {
        let payload = panel_json(edge, state.gap);

        let _ = outputs.submit_mutation(mutate, KIND_UPSERT_HUD_PANEL, payload.as_bytes());
    }

    // SAFETY: see the STATE doc above.
    unsafe {
        STATE = state;
    }
}
