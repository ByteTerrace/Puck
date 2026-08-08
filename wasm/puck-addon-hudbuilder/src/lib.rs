//! **BATTERY GUEST for the addon mutation seam** (Phase-3 plan L6 — `RequestVerbs.SubmitMutation`,
//! the six-stage `ResolveMutations` dispatch door, verb-mask grant rows). Never shipped, pinned by
//! no shipped world. FIVE build-time variants (Cargo features — see `Cargo.toml`), the
//! `puck-addon-channelwalk`-established option; exactly one must be selected, enforced below.
//!
//! - `main`: asks for a Mutate handle over `section:hud`, and once granted, submits an
//!   `UpsertHudPanel` act, reads its Answer, and — ONLY on `Verdict::Applied` — submits a SECOND,
//!   chained `UpsertHudElement` act adding a second element to the SAME panel (the chained-edit
//!   timing probe), then goes quiet forever.
//! - `spam`/`badkind`/`badjson`/`hugepayload`: each deliberately triggers exactly ONE dispatch-door
//!   refusal (`QuotaExhausted`/`AttenuatedToEmpty`/`MalformedPayload`/`PayloadTooLarge`
//!   respectively), reads the OBSERVED verdict back off its own Answer cell, and REPORTS it by
//!   submitting a second, well-formed act that writes the verdict's name into a dedicated report
//!   panel (`report-<variant>`) — so a console script asserts what the GUEST ITSELF saw on the
//!   wire, not merely what the host's own log line claims. See each module's own doc for its exact
//!   mechanism.
//!
//! **Why the JSON payloads are static string literals (or, for the report panels, `format!` over a
//! small closed set of `&'static str` verdict names) rather than built with a general serializer:**
//! this addon's whole point is to exercise the HOST's decode path with a KNOWN shape; nothing about
//! what it sends needs runtime structure beyond substituting the observed verdict's name.
//!
//! **Mount requirement, per variant** (see each module's own doc for its exact grant grammar):
//! `world.grant addon:<name> mutate section:hud verbs:<...> budget:<n>` — the manifest below only
//! DESIGNATES the request; the grant table decides what actually holds.

#[cfg(not(any(feature = "main", feature = "spam", feature = "badkind", feature = "badjson", feature = "hugepayload")))]
compile_error!("puck-addon-hudbuilder: select exactly one of the `main`, `spam`, `badkind`, `badjson`, or `hugepayload` features");

#[cfg(any(
    all(feature = "main", feature = "spam"),
    all(feature = "main", feature = "badkind"),
    all(feature = "main", feature = "badjson"),
    all(feature = "main", feature = "hugepayload"),
    all(feature = "spam", feature = "badkind"),
    all(feature = "spam", feature = "badjson"),
    all(feature = "spam", feature = "hugepayload"),
    all(feature = "badkind", feature = "badjson"),
    all(feature = "badkind", feature = "hugepayload"),
    all(feature = "badjson", feature = "hugepayload"),
))]
compile_error!("puck-addon-hudbuilder: `main`, `spam`, `badkind`, `badjson`, and `hugepayload` are mutually exclusive — select exactly one");

use puck_stdlib::abi;

/// `Puck.World.Protocol.WorldSection.Hud`'s declared NAME — the `Ask`'s subject, name-keyed rather
/// than ordinal-keyed (`Outputs::ask_section`), so a future `WorldSection` renumbering has no baked
/// ordinal left to strand (see `puck-addon-arcade`'s doc for the drift class this closes; this
/// guest's own ordinal happened to still be correct, but it shared the same stale-by-construction
/// shape and migrates for the same reason).
const HUD_SECTION_NAME: &str = "Hud";

/// `[MutationKind(ordinal: 41, section: WorldSection.Hud)] WorldMutation.UpsertHudPanel`'s declared
/// dispatch ordinal.
const KIND_UPSERT_HUD_PANEL: u8 = 41;
/// `[MutationKind(ordinal: 42, section: WorldSection.Hud)] WorldMutation.RemoveHudPanel`'s declared
/// dispatch ordinal — deliberately NOT granted to the `badkind` variant, so an act naming it is
/// refused by the deciding row's verb mask.
const KIND_REMOVE_HUD_PANEL: u8 = 42;
/// `[MutationKind(ordinal: 43, section: WorldSection.Hud)] WorldMutation.UpsertHudElement`'s declared
/// dispatch ordinal.
const KIND_UPSERT_HUD_ELEMENT: u8 = 43;

/// The maximum payload size the host's dispatch door admits without a `PayloadTooLarge` refusal
/// (`Puck.Scripting.AddonAbi.MaxMutationPayloadBytes`). Mirrored here as a literal — this crate has
/// no generated ABI mirror dependency beyond `puck-stdlib`'s own re-exports, and this ONE constant
/// is not among them (it never needs to be read by a well-behaved guest; only `hugepayload`
/// deliberately overshoots it).
const MAX_MUTATION_PAYLOAD_BYTES: usize = 8192;

/// Renders a wire `Verdict` (or its absence) as the exact name the host's own `AddonVerdict` enum
/// gives it, for a report panel's text — shared by every sabotage variant, so a console script
/// asserts against ONE stable vocabulary regardless of which variant produced it.
fn verdict_name(verdict: Option<puck_stdlib::Verdict>) -> &'static str {
    use puck_stdlib::Verdict;

    match verdict {
        None => "Unrecognized",
        Some(Verdict::None) => "None",
        Some(Verdict::HeldConcrete) => "HeldConcrete",
        Some(Verdict::HeldWildcard) => "HeldWildcard",
        Some(Verdict::HeldAsReserver) => "HeldAsReserver",
        Some(Verdict::NoHold) => "NoHold",
        Some(Verdict::BeatenByReserver) => "BeatenByReserver",
        Some(Verdict::AttenuatedToEmpty) => "AttenuatedToEmpty",
        Some(Verdict::NoSuchSubject) => "NoSuchSubject",
        Some(Verdict::QuotaExhausted) => "QuotaExhausted",
        Some(Verdict::StaleHandle) => "StaleHandle",
        Some(Verdict::Applied) => "Applied",
        Some(Verdict::MalformedPayload) => "MalformedPayload",
        Some(Verdict::PayloadTooLarge) => "PayloadTooLarge",
        Some(Verdict::Rejected) => "Rejected",
    }
}

/// Builds one `UpsertHudPanel` report payload: a single-element panel at `(x, y)` whose text IS the
/// observed verdict's name — the one artifact every sabotage variant produces, read back by
/// `world.hud` after the run. `panel_id` is distinct per variant so all four (plus `main`'s own
/// `hudbuilder` panel) can coexist in ONE world document without colliding.
fn report_json(panel_id: &str, x: f32, y: f32, verdict: Option<puck_stdlib::Verdict>) -> String {
    report_json_text(panel_id, x, y, verdict_name(verdict))
}

/// The general form `report_json` delegates to — takes the report TEXT directly, for a variant
/// (like `badjson`'s case sweep) whose report is more than one bare verdict name (e.g. naming which
/// of several sub-cases disagreed).
fn report_json_text(panel_id: &str, x: f32, y: f32, text: &str) -> String {
    format!(
        r#"{{"id":"{panel_id}","rect":{{"x":{x},"y":{y},"width":0.3,"height":0.12}},"layer":"over","style":"panel","elements":[{{"id":"v","kind":"text","rect":{{"x":0.0,"y":0.0,"width":1.0,"height":1.0}},"style":"primary","text":"{text}"}}]}}"#
    )
}

// The four shims IDENTICAL across every variant — the ring geometry, ABI version, and channel
// descriptor COUNT are stdlib budgets untouched by which behavior module is compiled in.
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
pub extern "C" fn puck_channels_count() -> i32 {
    abi::channels_count()
}

// puck_channels_ptr / puck_init / puck_on_tick are PER-VARIANT (each module below declares its own
// `channels!` table and `on_tick`) — mirrors puck-addon-channelwalk's identical split.

#[cfg(feature = "main")]
mod main_behavior {
    //! The functional guest — see the crate doc's `main` bullet for the full walkthrough.

    use puck_stdlib::{abi, CAP_MUTATE, Handle, InCellKind, Inputs, Outputs, Verdict};

    // A declared Input channel with a nonempty verb table is required by the handshake even though
    // this guest never drives a body.
    puck_stdlib::channels! {
        static channels;
        const UNUSED: Bipolar = "unused";
    }

    const PANEL_JSON: &str = r#"{"id":"hudbuilder","rect":{"x":0.02,"y":0.02,"width":0.3,"height":0.12},"layer":"over","style":"panel","elements":[{"id":"line1","kind":"text","rect":{"x":0.0,"y":0.0,"width":1.0,"height":0.5},"style":"primary","text":"hudbuilder"}]}"#;
    const ELEMENT_JSON: &str = r#"{"panelId":"hudbuilder","element":{"id":"line2","kind":"text","rect":{"x":0.0,"y":0.5,"width":1.0,"height":0.5},"style":"accent","text":"applied"}}"#;

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

    /// This guest's whole state machine, in declaration order — a straight-line progression with no
    /// branch back to an earlier phase, because a chained edit is a ONE-SHOT proof, not a loop.
    #[derive(Clone, Copy, PartialEq, Eq)]
    enum Phase {
        AwaitingHandle,
        HandleHeld,
        AwaitingFirstApplied(u16),
        FirstApplied,
        AwaitingSecondAnswer(u16),
        Done,
    }

    #[derive(Clone, Copy)]
    struct State {
        mutate: Option<Handle>,
        pending_ask: Option<u16>,
        phase: Phase,
    }

    impl State {
        const EMPTY: Self = Self { mutate: None, pending_ask: None, phase: Phase::AwaitingHandle };
    }

    static mut STATE: State = State::EMPTY;

    fn on_tick(inputs: &Inputs, outputs: &mut Outputs) {
        // SAFETY: see puck_stdlib::abi::dispatch_tick's safety note — single sim-tick thread, no
        // re-entrancy. State is Copy — a value read here, a value write at the bottom.
        let mut state = unsafe { STATE };

        for cell in inputs.iter() {
            match cell.kind {
                Some(InCellKind::Tick) => {}
                Some(InCellKind::Observation) => {}
                Some(InCellKind::Answer) => {
                    if state.pending_ask == Some(cell.ordinal) {
                        state.pending_ask = None;

                        if matches!(cell.verdict, Some(verdict) if verdict.is_allowed()) {
                            state.mutate = Some(cell.handle);
                            state.phase = Phase::HandleHeld;
                        }

                        continue;
                    }

                    if let Phase::AwaitingFirstApplied(ordinal) = state.phase {
                        if cell.ordinal == ordinal {
                            state.phase = if cell.verdict == Some(Verdict::Applied) {
                                Phase::FirstApplied
                            } else {
                                Phase::Done
                            };
                        }
                    }

                    if let Phase::AwaitingSecondAnswer(ordinal) = state.phase {
                        if cell.ordinal == ordinal {
                            state.phase = Phase::Done;
                        }
                    }
                }
                None => {}
            }
        }

        match state.phase {
            Phase::AwaitingHandle => {
                if state.pending_ask.is_none() {
                    state.pending_ask =
                        Some(outputs.ask_section(super::HUD_SECTION_NAME, CAP_MUTATE));
                }
            }
            Phase::HandleHeld => {
                if let Some(mutate) = state.mutate {
                    let ordinal = outputs.submit_mutation(mutate, super::KIND_UPSERT_HUD_PANEL, PANEL_JSON.as_bytes());

                    state.phase = Phase::AwaitingFirstApplied(ordinal);
                }
            }
            Phase::FirstApplied => {
                if let Some(mutate) = state.mutate {
                    let ordinal = outputs.submit_mutation(mutate, super::KIND_UPSERT_HUD_ELEMENT, ELEMENT_JSON.as_bytes());

                    state.phase = Phase::AwaitingSecondAnswer(ordinal);
                }
            }
            Phase::AwaitingFirstApplied(_) | Phase::AwaitingSecondAnswer(_) | Phase::Done => {}
        }

        unsafe {
            STATE = state;
        }
    }
}

#[cfg(feature = "badkind")]
mod badkind {
    //! Grant grammar: `world.grant addon:<name> mutate section:hud verbs:UpsertHudPanel budget:4`
    //! (`RemoveHudPanel` DELIBERATELY OMITTED from `verbs:`). Once the Mutate handle is granted, this
    //! guest submits `RemoveHudPanel`(42) with an otherwise well-formed payload — the mask check
    //! (dispatch-door stage 2) refuses it as `AttenuatedToEmpty` BEFORE decode ever runs, so the
    //! payload's content is irrelevant. Reads that verdict off its own Answer, then reports it via a
    //! well-formed `UpsertHudPanel` act (kind 41, which IS granted) into panel `report-badkind`, then
    //! goes quiet.

    use puck_stdlib::{abi, CAP_MUTATE, Handle, InCellKind, Inputs, Outputs, Verdict};

    puck_stdlib::channels! {
        static channels;
        const UNUSED: Bipolar = "unused";
    }

    const BAD_JSON: &str = r#"{"id":"nonexistent"}"#;

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

    #[derive(Clone, Copy, PartialEq, Eq)]
    enum Phase {
        AwaitingHandle,
        HandleHeld,
        AwaitingBadAnswer(u16),
        ReadyToReport(Option<Verdict>),
        AwaitingReportAnswer(u16),
        Done,
    }

    #[derive(Clone, Copy)]
    struct State {
        mutate: Option<Handle>,
        pending_ask: Option<u16>,
        phase: Phase,
    }

    impl State {
        const EMPTY: Self = Self { mutate: None, pending_ask: None, phase: Phase::AwaitingHandle };
    }

    static mut STATE: State = State::EMPTY;

    fn on_tick(inputs: &Inputs, outputs: &mut Outputs) {
        // SAFETY: see puck_stdlib::abi::dispatch_tick's safety note.
        let mut state = unsafe { STATE };

        for cell in inputs.iter() {
            if cell.kind != Some(InCellKind::Answer) {
                continue;
            }

            if state.pending_ask == Some(cell.ordinal) {
                state.pending_ask = None;

                if matches!(cell.verdict, Some(v) if v.is_allowed()) {
                    state.mutate = Some(cell.handle);
                    state.phase = Phase::HandleHeld;
                }

                continue;
            }

            if let Phase::AwaitingBadAnswer(ordinal) = state.phase {
                if cell.ordinal == ordinal {
                    state.phase = Phase::ReadyToReport(cell.verdict);
                }
            }

            if let Phase::AwaitingReportAnswer(ordinal) = state.phase {
                if cell.ordinal == ordinal {
                    state.phase = Phase::Done;
                }
            }
        }

        match state.phase {
            Phase::AwaitingHandle => {
                if state.pending_ask.is_none() {
                    state.pending_ask =
                        Some(outputs.ask_section(super::HUD_SECTION_NAME, CAP_MUTATE));
                }
            }
            Phase::HandleHeld => {
                if let Some(mutate) = state.mutate {
                    let ordinal =
                        outputs.submit_mutation(mutate, super::KIND_REMOVE_HUD_PANEL, BAD_JSON.as_bytes());

                    state.phase = Phase::AwaitingBadAnswer(ordinal);
                }
            }
            Phase::ReadyToReport(observed) => {
                if let Some(mutate) = state.mutate {
                    let report = super::report_json("report-badkind", 0.02, 0.20, observed);
                    let ordinal = outputs.submit_mutation(mutate, super::KIND_UPSERT_HUD_PANEL, report.as_bytes());

                    state.phase = Phase::AwaitingReportAnswer(ordinal);
                }
            }
            Phase::AwaitingBadAnswer(_) | Phase::AwaitingReportAnswer(_) | Phase::Done => {}
        }

        unsafe {
            STATE = state;
        }
    }
}

#[cfg(feature = "badjson")]
mod badjson {
    //! Grant grammar: `world.grant addon:<name> mutate section:hud verbs:UpsertHudPanel budget:4`.
    //! Once granted, this guest walks a FIXED SEQUENCE of six deliberately malformed
    //! `UpsertHudPanel`(41, which IS granted) payloads — ONE case per tick, in order, each read back
    //! off its own Answer before the next is sent — every one of which the JSON-payload door must
    //! refuse `MalformedPayload`: (0) an unterminated object (invalid syntax), (1) invalid UTF-8
    //! bytes, (2) a duplicate `"id"` member, (3) an unknown member (`"bogus"`), (4) nesting past
    //! `JsonDocument`'s own depth ceiling, (5) a non-finite numeric LITERAL (`NaN` — not valid JSON
    //! token under this decoder's parse options, so it fails at the SAME parse stage as the others,
    //! never reaching the decoder's own separate finite-checks). Reports `MalformedPayload` into
    //! panel `report-badjson` ONLY when EVERY case answered `MalformedPayload`; otherwise reports
    //! which case index disagreed and what it actually observed, so a runner assertion failure names
    //! the offending case instead of reading a bare "wrong verdict".

    use puck_stdlib::{abi, CAP_MUTATE, Handle, InCellKind, Inputs, Outputs, Verdict};

    puck_stdlib::channels! {
        static channels;
        const UNUSED: Bipolar = "unused";
    }

    const CASE_COUNT: usize = 6;

    /// Case 4's payload is built once, at startup, via `puck_init` — 100 levels of nested objects,
    /// past `System.Text.Json.JsonReaderOptions.MaxDepth`'s own default (64) — never a `'static`
    /// literal, because a source file this repetitive is worse than the eight-line loop that builds
    /// it once and is done.
    static mut DEEP_NESTING: Option<String> = None;

    fn case_payload(index: usize) -> std::borrow::Cow<'static, [u8]> {
        use std::borrow::Cow;

        match index {
            // Case 0: unterminated object — invalid syntax, the simplest parse failure.
            0 => Cow::Borrowed(br#"{"id":"broken", "rect": {"#.as_slice()),
            // Case 1: invalid UTF-8 — a byte sequence JsonDocument.Parse cannot even decode as text,
            // never mind as JSON (0xFF/0xFE are never valid UTF-8 lead or continuation bytes).
            1 => Cow::Borrowed(&[0x7B, 0xFF, 0xFE, 0x7D][..]),
            // Case 2: a duplicate "id" member — syntactically valid JSON, refused by UniqueMembers.
            2 => Cow::Borrowed(br#"{"id":"a","id":"b","rect":{"x":0,"y":0,"width":1,"height":1},"layer":"over","style":"panel","elements":[]}"#.as_slice()),
            // Case 3: an unknown member ("bogus") — refused by RequireNoUnknownMembers.
            3 => Cow::Borrowed(br#"{"id":"a","rect":{"x":0,"y":0,"width":1,"height":1},"layer":"over","style":"panel","elements":[],"bogus":true}"#.as_slice()),
            // Case 4: nesting past JsonDocument's own depth ceiling — built once at init.
            4 => {
                // SAFETY: single sim-tick thread, no re-entrancy — see puck_stdlib::abi::dispatch_tick's
                // safety note. Raw-pointer read (never a `&'static mut` reference) avoids the
                // shared-reference-to-mutable-static hazard the borrow checker warns about.
                let deep = unsafe { (*core::ptr::addr_of!(DEEP_NESTING)).as_ref() }.expect("puck_init built this");

                Cow::Borrowed(deep.as_bytes())
            }
            // Case 5: a non-finite numeric LITERAL ("NaN") — not a legal JSON token under this
            // decoder's parse options (AllowNamedFloatingPointLiterals is never set), so this fails
            // at PARSE, the same stage as cases 0/1, never reaching RequireFinite's own check.
            _ => Cow::Borrowed(br#"{"id":"a","rect":{"x":NaN,"y":0,"width":1,"height":1},"layer":"over","style":"panel","elements":[]}"#.as_slice()),
        }
    }

    #[no_mangle]
    pub extern "C" fn puck_channels_ptr() -> i32 {
        abi::channels_ptr(channels::ptr(), channels::count())
    }

    #[no_mangle]
    pub extern "C" fn puck_init() {
        let mut deep = String::new();

        for _ in 0..100 {
            deep.push_str(r#"{"a":"#);
        }

        deep.push('1');

        for _ in 0..100 {
            deep.push('}');
        }

        // SAFETY: puck_init runs once, before the first tick, on the same single sim-tick thread
        // every other STATE access in this module uses.
        unsafe {
            DEEP_NESTING = Some(deep);
        }
    }

    #[no_mangle]
    pub extern "C" fn puck_on_tick(input_count: i32) -> i32 {
        abi::dispatch_tick(input_count, on_tick)
    }

    #[derive(Clone, Copy, PartialEq, Eq)]
    enum Phase {
        AwaitingHandle,
        /// `usize` = the NEXT case index to send (0..=CASE_COUNT once all are sent).
        CaseHeld(usize),
        AwaitingCaseAnswer(usize, u16),
        ReadyToReport,
        AwaitingReportAnswer(u16),
        Done,
    }

    #[derive(Clone, Copy)]
    struct State {
        mutate: Option<Handle>,
        pending_ask: Option<u16>,
        phase: Phase,
        /// Set the moment ANY case answers something other than `MalformedPayload` — carries the
        /// offending case index and what it actually observed, for the report text.
        first_mismatch: Option<(usize, Option<Verdict>)>,
    }

    impl State {
        const EMPTY: Self = Self {
            mutate: None,
            pending_ask: None,
            phase: Phase::AwaitingHandle,
            first_mismatch: None,
        };
    }

    static mut STATE: State = State::EMPTY;

    fn on_tick(inputs: &Inputs, outputs: &mut Outputs) {
        // SAFETY: see puck_stdlib::abi::dispatch_tick's safety note.
        let mut state = unsafe { STATE };

        for cell in inputs.iter() {
            if cell.kind != Some(InCellKind::Answer) {
                continue;
            }

            if state.pending_ask == Some(cell.ordinal) {
                state.pending_ask = None;

                if matches!(cell.verdict, Some(v) if v.is_allowed()) {
                    state.mutate = Some(cell.handle);
                    state.phase = Phase::CaseHeld(0);
                }

                continue;
            }

            if let Phase::AwaitingCaseAnswer(index, ordinal) = state.phase {
                if cell.ordinal == ordinal {
                    if (cell.verdict != Some(Verdict::MalformedPayload)) && state.first_mismatch.is_none() {
                        state.first_mismatch = Some((index, cell.verdict));
                    }

                    state.phase = if (index + 1) < CASE_COUNT {
                        Phase::CaseHeld(index + 1)
                    } else {
                        Phase::ReadyToReport
                    };
                }
            }

            if let Phase::AwaitingReportAnswer(ordinal) = state.phase {
                if cell.ordinal == ordinal {
                    state.phase = Phase::Done;
                }
            }
        }

        match state.phase {
            Phase::AwaitingHandle => {
                if state.pending_ask.is_none() {
                    state.pending_ask =
                        Some(outputs.ask_section(super::HUD_SECTION_NAME, CAP_MUTATE));
                }
            }
            Phase::CaseHeld(index) => {
                if let Some(mutate) = state.mutate {
                    let payload = case_payload(index);
                    let ordinal = outputs.submit_mutation(mutate, super::KIND_UPSERT_HUD_PANEL, &payload);

                    state.phase = Phase::AwaitingCaseAnswer(index, ordinal);
                }
            }
            Phase::ReadyToReport => {
                if let Some(mutate) = state.mutate {
                    let text = match state.first_mismatch {
                        None => String::from("MalformedPayload"),
                        Some((index, observed)) => {
                            format!("FAIL-case{index}-{}", super::verdict_name(observed))
                        }
                    };
                    let report = super::report_json_text("report-badjson", 0.02, 0.35, &text);
                    let ordinal = outputs.submit_mutation(mutate, super::KIND_UPSERT_HUD_PANEL, report.as_bytes());

                    state.phase = Phase::AwaitingReportAnswer(ordinal);
                }
            }
            Phase::AwaitingCaseAnswer(_, _) | Phase::AwaitingReportAnswer(_) | Phase::Done => {}
        }

        unsafe {
            STATE = state;
        }
    }
}

#[cfg(feature = "hugepayload")]
mod hugepayload {
    //! Grant grammar: `world.grant addon:<name> mutate section:hud verbs:UpsertHudPanel budget:4`.
    //! Once granted, walks FOUR pointer/length edge cases, one per tick, each read back off its own
    //! Answer before the next fires:
    //! - **Case 0 — exactly `MAX_MUTATION_PAYLOAD_BYTES` (8192)**: a REAL, well-formed, VALID
    //!   `UpsertHudPanel` payload padded to exactly the ceiling. Expects `Verdict::Applied` — the
    //!   size gate admits the boundary value itself, and the decode that follows succeeds too.
    //! - **Case 1 — exactly 8193**: the SAME real payload, one byte over. Expects
    //!   `Verdict::PayloadTooLarge`, refused on SIZE ALONE before a single byte is read out of guest
    //!   memory (content is irrelevant past the ceiling, so this need not even be valid JSON).
    //! - **Case 2 — a NEGATIVE pointer**: `Outputs::submit_mutation_raw(mutate, kind, -1, 10)` — a
    //!   wire-level shape a SAFE Rust slice can never itself produce (`payload.as_ptr()` is always a
    //!   valid, non-negative offset into the guest's own memory), so this is the one case in this
    //!   crate that reaches for the raw escape hatch on purpose. Expects `Verdict::MalformedPayload`
    //!   — `AddonInstance.TryCopyMemory`'s own `pointer < 0` check, before any memory read.
    //! - **Case 3 — an OVERFLOWED end**: `submit_mutation_raw(mutate, kind, 10_000_000, 10)` — a
    //!   pointer far past this guest's actual (tiny) linear memory, with a small, in-ceiling length.
    //!   Expects `Verdict::MalformedPayload` — the overflow-checked `[ptr, ptr+len)` range exceeds
    //!   the guest's REAL memory length, refused before any read.
    //!
    //! Reports `PayloadTooLarge` into panel `report-hugepayload` (the size-refusal case is this
    //! variant's OWN name and the one every other case exists to bound against) ONLY when all four
    //! cases matched their expected verdict; otherwise reports which case index disagreed and what
    //! it actually observed.

    use puck_stdlib::{abi, CAP_MUTATE, Handle, InCellKind, Inputs, Outputs, Verdict};

    puck_stdlib::channels! {
        static channels;
        const UNUSED: Bipolar = "unused";
    }

    const CASE_COUNT: usize = 4;

    /// Builds a REAL, well-formed `UpsertHudPanel` payload padded (via its `text` field) to exactly
    /// `target_len` bytes — used for cases 0 and 1, which test the SIZE ceiling itself, not decode
    /// correctness, so the payload must be genuinely valid to isolate that.
    fn exact_size_payload(target_len: usize) -> String {
        // Targets the SAME `report-hugepayload` panel id the eventual verdict report also targets
        // (see the module doc: the world-scope HUD panel budget is 4 for the whole document, shared
        // across all four sabotage variants in this landing's combined world — no slot to spare on
        // a throwaway probe-only panel; a distinct id here silently pushed the document to 5 panels
        // and starved the FINAL report of a slot the first time this ran).
        const PREFIX: &str = r#"{"id":"report-hugepayload","rect":{"x":0.02,"y":0.50,"width":0.3,"height":0.12},"layer":"over","style":"panel","elements":[{"id":"v","kind":"text","rect":{"x":0.0,"y":0.0,"width":1.0,"height":1.0},"style":"primary","text":""#;
        const SUFFIX: &str = r#""}]}"#;

        let base_len = PREFIX.len() + SUFFIX.len();
        let pad_len = target_len.saturating_sub(base_len);
        let mut payload = String::with_capacity(target_len);

        payload.push_str(PREFIX);
        // A single bulk fill (`repeat`'s internal `memset`), never a per-character push loop: this
        // guest runs under a metered per-tick FUEL budget, and Wasmtime charges per instruction —
        // 8000+ individual `String::push` calls (each its own UTF-8 encode/bounds-check/capacity
        // check) measurably approached the default 1,000,000/tick ceiling and tripped `OutOfFuel`
        // during this exact case the first time this guest ran; `repeat` does the identical fill in
        // one pass.
        payload.push_str(&"a".repeat(pad_len));
        payload.push_str(SUFFIX);
        payload
    }

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

    #[derive(Clone, Copy, PartialEq, Eq)]
    enum Phase {
        AwaitingHandle,
        CaseHeld(usize),
        AwaitingCaseAnswer(usize, u16),
        ReadyToReport,
        AwaitingReportAnswer(u16),
        Done,
    }

    #[derive(Clone, Copy)]
    struct State {
        mutate: Option<Handle>,
        pending_ask: Option<u16>,
        phase: Phase,
        first_mismatch: Option<(usize, Option<Verdict>)>,
    }

    impl State {
        const EMPTY: Self = Self {
            mutate: None,
            pending_ask: None,
            phase: Phase::AwaitingHandle,
            first_mismatch: None,
        };
    }

    static mut STATE: State = State::EMPTY;

    fn on_tick(inputs: &Inputs, outputs: &mut Outputs) {
        // SAFETY: see puck_stdlib::abi::dispatch_tick's safety note.
        let mut state = unsafe { STATE };

        for cell in inputs.iter() {
            if cell.kind != Some(InCellKind::Answer) {
                continue;
            }

            if state.pending_ask == Some(cell.ordinal) {
                state.pending_ask = None;

                if matches!(cell.verdict, Some(v) if v.is_allowed()) {
                    state.mutate = Some(cell.handle);
                    state.phase = Phase::CaseHeld(0);
                }

                continue;
            }

            if let Phase::AwaitingCaseAnswer(index, ordinal) = state.phase {
                if cell.ordinal == ordinal {
                    // Case 0 expects Applied; cases 1-3 expect PayloadTooLarge/MalformedPayload —
                    // named per-case here rather than a single blanket comparison, so a passing
                    // case 0 that happens to equal case 1's expectation could never mask a real
                    // divergence.
                    let expected = match index {
                        0 => Some(Verdict::Applied),
                        1 => Some(Verdict::PayloadTooLarge),
                        _ => Some(Verdict::MalformedPayload),
                    };

                    if (cell.verdict != expected) && state.first_mismatch.is_none() {
                        state.first_mismatch = Some((index, cell.verdict));
                    }

                    state.phase = if (index + 1) < CASE_COUNT {
                        Phase::CaseHeld(index + 1)
                    } else {
                        Phase::ReadyToReport
                    };
                }
            }

            if let Phase::AwaitingReportAnswer(ordinal) = state.phase {
                if cell.ordinal == ordinal {
                    state.phase = Phase::Done;
                }
            }
        }

        match state.phase {
            Phase::AwaitingHandle => {
                if state.pending_ask.is_none() {
                    state.pending_ask =
                        Some(outputs.ask_section(super::HUD_SECTION_NAME, CAP_MUTATE));
                }
            }
            Phase::CaseHeld(index) => {
                if let Some(mutate) = state.mutate {
                    let ordinal = match index {
                        0 => {
                            let payload = exact_size_payload(super::MAX_MUTATION_PAYLOAD_BYTES);

                            outputs.submit_mutation(mutate, super::KIND_UPSERT_HUD_PANEL, payload.as_bytes())
                        }
                        1 => {
                            let payload = exact_size_payload(super::MAX_MUTATION_PAYLOAD_BYTES + 1);

                            outputs.submit_mutation(mutate, super::KIND_UPSERT_HUD_PANEL, payload.as_bytes())
                        }
                        2 => outputs.submit_mutation_raw(mutate, super::KIND_UPSERT_HUD_PANEL, -1, 10),
                        _ => outputs.submit_mutation_raw(mutate, super::KIND_UPSERT_HUD_PANEL, 10_000_000, 10),
                    };

                    state.phase = Phase::AwaitingCaseAnswer(index, ordinal);
                }
            }
            Phase::ReadyToReport => {
                if let Some(mutate) = state.mutate {
                    let text = match state.first_mismatch {
                        None => String::from("PayloadTooLarge"),
                        Some((index, observed)) => {
                            format!("FAIL-case{index}-{}", super::verdict_name(observed))
                        }
                    };
                    let report = super::report_json_text("report-hugepayload", 0.02, 0.50, &text);
                    let ordinal = outputs.submit_mutation(mutate, super::KIND_UPSERT_HUD_PANEL, report.as_bytes());

                    state.phase = Phase::AwaitingReportAnswer(ordinal);
                }
            }
            Phase::AwaitingCaseAnswer(_, _) | Phase::AwaitingReportAnswer(_) | Phase::Done => {}
        }

        unsafe {
            STATE = state;
        }
    }
}

#[cfg(feature = "spam")]
mod spam {
    //! Grant grammar: `world.grant addon:<name> mutate section:hud verbs:UpsertHudPanel budget:2`
    //! (a SMALL budget is the point — the runner controls it). Once the Mutate handle is granted,
    //! this guest floods the GUEST'S OWN FULL OUTPUT-RING CAPACITY (`puck_stdlib::abi::OUT_CAP`, 8)
    //! of well-formed `UpsertHudPanel` acts EVERY tick — all eight targeting the SAME panel id,
    //! `report-spam` (never a distinct id per act: `WorldHudCapacity.MaxWorldPanels` is 4 for the
    //! WHOLE document, and four sabotage variants sharing one world each own exactly one panel slot,
    //! with none to spare on a throwaway flood target). With `budget:2` granted, the
    //! per-`(addon, section)` dispatch-door budget (stage 3) admits only the first two of each
    //! batch's eight and refuses the other six `QuotaExhausted` — and this is also the RESERVATION
    //! saturation proof the task names: this guest tracks all EIGHT of last tick's ordinals and
    //! requires EVERY one of them to answer with a REAL verdict this tick — never fewer, and never a
    //! refused one silently read back as `Applied` — before it will trust what it observed. Only once
    //! flooding has been proven saturation-safe for one whole batch AND at least one of that batch's
    //! eight answered `QuotaExhausted` does this guest stop and report; a missing answer or a
    //! misread verdict reports a `SATURATION-FAIL` text instead, naming what actually happened.

    use puck_stdlib::{abi, CAP_MUTATE, Handle, InCellKind, Inputs, Outputs, Verdict};

    puck_stdlib::channels! {
        static channels;
        const UNUSED: Bipolar = "unused";
    }

    /// The flood act's OWN payload — targets the SAME `report-spam` panel id the eventual verdict
    /// report also targets (see the module doc for why: the world-scope HUD panel budget is 4 for
    /// the whole document, shared across all four sabotage variants in this landing's combined
    /// world, with no slot to spare on a throwaway flood-only panel). Content is a placeholder;
    /// `ReadyToReport` overwrites it with the observed verdict's name once flooding stops.
    const FLOOD_JSON: &str = r#"{"id":"report-spam","rect":{"x":0.02,"y":0.65,"width":0.3,"height":0.12},"layer":"over","style":"panel","elements":[{"id":"v","kind":"text","rect":{"x":0.0,"y":0.0,"width":1.0,"height":1.0},"style":"primary","text":"flooding"}]}"#;

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

    /// This guest's own declared output-ring capacity — the FULL flood width. `puck_stdlib::abi::
    /// OUT_CAP` (not a crate-local override), since this variant delegates `puck_out_cap` straight
    /// through, same as `main`/`badkind`/`badjson`/`hugepayload`.
    const FLOOD_WIDTH: usize = puck_stdlib::abi::OUT_CAP;

    #[derive(Clone, Copy, PartialEq, Eq)]
    enum Phase {
        AwaitingHandle,
        /// Flooding every tick; the `FLOOD_WIDTH` ordinals it last sent (always this tick's cells
        /// `0..FLOOD_WIDTH`, so they are read back off next tick's Answers at those SAME positions).
        Flooding([Option<u16>; FLOOD_WIDTH]),
        ReadyToReport,
        AwaitingReportAnswer(u16),
        Done,
    }

    #[derive(Clone, Copy)]
    struct State {
        mutate: Option<Handle>,
        pending_ask: Option<u16>,
        phase: Phase,
        /// `None` while nothing to report yet. `Some(true)` once one whole flooded batch answered
        /// EVERY one of its `FLOOD_WIDTH` ordinals AND at least one was `QuotaExhausted` — the
        /// saturation-safety proof. `Some(false)` the moment that invariant is ever violated (a
        /// missing answer, or a verdict this guest cannot reconcile with either "admitted" or
        /// "refused QuotaExhausted") — carries how many of `FLOOD_WIDTH` answers actually arrived.
        result: Option<Result<(), usize>>,
    }

    impl State {
        const EMPTY: Self = Self { mutate: None, pending_ask: None, phase: Phase::AwaitingHandle, result: None };
    }

    static mut STATE: State = State::EMPTY;

    fn on_tick(inputs: &Inputs, outputs: &mut Outputs) {
        // SAFETY: see puck_stdlib::abi::dispatch_tick's safety note.
        let mut state = unsafe { STATE };
        // Per-tick scratch: for the batch this tick's Answers respond to, which of its
        // `FLOOD_WIDTH` ordinals have been seen, and whether any carried QuotaExhausted.
        let mut seen = [false; FLOOD_WIDTH];
        let mut any_quota_exhausted = false;
        let mut any_unexpected = false;

        for cell in inputs.iter() {
            if cell.kind != Some(InCellKind::Answer) {
                continue;
            }

            if state.pending_ask == Some(cell.ordinal) {
                state.pending_ask = None;

                if matches!(cell.verdict, Some(v) if v.is_allowed()) {
                    state.mutate = Some(cell.handle);
                    state.phase = Phase::Flooding([None; FLOOD_WIDTH]);
                }

                continue;
            }

            if let Phase::Flooding(sent) = state.phase {
                for (index, ordinal) in sent.iter().enumerate() {
                    if *ordinal == Some(cell.ordinal) {
                        seen[index] = true;

                        match cell.verdict {
                            Some(Verdict::QuotaExhausted) => any_quota_exhausted = true,
                            Some(Verdict::Applied) => {}
                            _ => any_unexpected = true,
                        }
                    }
                }
            }

            if let Phase::AwaitingReportAnswer(ordinal) = state.phase {
                if cell.ordinal == ordinal {
                    state.phase = Phase::Done;
                }
            }
        }

        if let Phase::Flooding(sent) = state.phase {
            // Only judge a batch once this guest has actually SENT one (every ordinal slot
            // populated) — the very first Flooding entry, right after the ask's own Answer, has
            // sent nothing yet and this tick's Answers cannot possibly be about it.
            if sent.iter().all(Option::is_some) {
                let seen_count = seen.iter().filter(|&&value| value).count();

                if seen_count < FLOOD_WIDTH {
                    state.result = Some(Err(seen_count));
                    state.phase = Phase::ReadyToReport;
                } else if any_unexpected {
                    state.result = Some(Err(FLOOD_WIDTH));
                    state.phase = Phase::ReadyToReport;
                } else if any_quota_exhausted {
                    state.result = Some(Ok(()));
                    state.phase = Phase::ReadyToReport;
                }
                // else: every ordinal answered Applied (budget not yet exhausted this batch, e.g.
                // a stale batch from before the grant's budget applied) — keep flooding.
            }
        }

        match state.phase {
            Phase::AwaitingHandle => {
                if state.pending_ask.is_none() {
                    state.pending_ask =
                        Some(outputs.ask_section(super::HUD_SECTION_NAME, CAP_MUTATE));
                }
            }
            Phase::Flooding(_) => {
                if let Some(mutate) = state.mutate {
                    let mut sent = [None; FLOOD_WIDTH];

                    for slot in sent.iter_mut() {
                        *slot = Some(outputs.submit_mutation(mutate, super::KIND_UPSERT_HUD_PANEL, FLOOD_JSON.as_bytes()));
                    }

                    state.phase = Phase::Flooding(sent);
                }
            }
            Phase::ReadyToReport => {
                if let Some(mutate) = state.mutate {
                    let text = match state.result {
                        Some(Ok(())) => String::from("QuotaExhausted"),
                        Some(Err(seen_count)) => format!("SATURATION-FAIL-{seen_count}-of-{FLOOD_WIDTH}"),
                        None => String::from("SATURATION-FAIL-no-result"),
                    };
                    let report = super::report_json_text("report-spam", 0.02, 0.65, &text);
                    let ordinal = outputs.submit_mutation(mutate, super::KIND_UPSERT_HUD_PANEL, report.as_bytes());

                    state.phase = Phase::AwaitingReportAnswer(ordinal);
                }
            }
            Phase::AwaitingReportAnswer(_) | Phase::Done => {}
        }

        unsafe {
            STATE = state;
        }
    }
}
