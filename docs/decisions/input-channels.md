# Input channels

An action world needs movement and flight controls while a seated board player
needs cursor movement, confirm, and cancel. These meanings share devices, but
they need not consume storage or remain actionable together. A rhythm world
also needs the time of each press, including several presses between simulation
steps. This decision gives all three cases one authoritative input model.

The owner asks for more channels, economical reuse, and support for action,
shooting, fighting, rhythm, board, and UI worlds. The accepted direction is
typed packed values, shared action names, composable simulation modes, authored
continuity across mode switches, and deterministic input history with sub-tick
timing. The representation and algorithms below refine that direction.

**Implementation status:** this is the target contract. The runtime currently
has sixteen Q48.16 channel slots, body-derived edges, and presentation binding
contexts. The [implementation steps](#implementation-steps) track the migration;
examples using `inputModes`, `inputHistory`, or `inputPatterns` below describe
new vocabulary, not syntax an existing build accepts.

## Semantic names and typed values

A composed world declares each action once in `channels`. Modules refer to that
name in bindings, kit actions, rules, engagement maps, and grants. Common names
belong in an imported document shared by the modules that use them. The engine
does not inject an FPS, avatar, or gamepad vocabulary into an empty world.
Unrelated actions retain separate names. In particular, a machine's Select
button and a board's cancel operation have different meanings.

Names resolve at compilation to a logical channel id and descriptor. A logical
id identifies a meaning throughout the composed document. A storage address is
the descriptor's bank and slot for one active mode combination. Documents,
bindings, permissions, history, and tools never accept storage ordinals as
names. Reload invalidates compiled handles whose document layout changes.

| Shape | Stored representation | Lifetime and combination |
|---|---|---|
| `Bipolar` | Signed Q48.16 scalar, range −1 through 1 | Held level; accumulate contributions and clamp once. |
| `Unipolar` | Signed Q48.16 scalar, range 0 through 1 | Held level; preserve the existing maximum composition. |
| `Binary` | One bit in a `ulong` | Held level; trusted held values combine by OR. Weighted contributions still use the existing fixed-point fold and threshold before packing. |
| `Relative` | Signed Q32.32 scalar in the numeric bank | Displacement since the previous step; sum once, consume once, then zero. Never clamp to a normalized stick range or carry as held. |

There are **32 numeric slots and 64 button slots per active input owner**.
Relative channels use the numeric slots: an FPS can have two relative look
components and thirty other numeric components. The numeric bank is 32 inline
64-bit values (256 bytes); the held button bank is 8 bytes. Thus the value
payload has a 264-byte ceiling before pointer-ray, source-contribution and
submission metadata.
Packing binary state does not require allocating 64 fixed-point held values.
Preallocated fixed-point scratch for contribution weighting is a separate cost.

An input owner is an admitted, generation-bearing seat/session identity. It
may control no body, one body, or routed targets. A body's folded intent and
kit state remain body state; source history belongs to its input owner. Body
indices, local-seat counts, and devices do not define channel identity.

The numeric bank uses typed accessors: normalized values read as `FixedQ4816`,
relative values as `FixedQ3232`. Reading the same raw bits as the other type is
invalid. Shape, units, range, and conversion are compiled into handles. A
relative channel declares its unit and positive maximum absolute displacement
per step; accumulation uses a widened integer and one final saturation to that
authored bound. Conversion to a Q48.16 simulation angle happens once at its
consumer, through `Puck.Maths`, with ties to even. The host applies authored
device sensitivity before submitting displacement; a relative value is never
multiplied by step duration. A stick's look rate remains a held axis and is
integrated using the world's step duration.

An integrating consumer retains any fractional conversion remainder in
authoritative state, or accumulates its angle in Q32.32 before narrowing. Tiny
aim samples must not disappear merely because each step rounds to Q48.16.

Explicit ordered press/release events accompany the held image. Deriving just
one edge from two images loses a press and release within the same tick. For
buttons, rise/fall summary masks remain useful for existing kit readers, but
the ordered events are the source for timing and sequence rules. Held images
and events must agree; the server refuses a malformed batch atomically.

Reach, consent, held support, and declared-channel masks become separate
numeric and button masks (`uint` and `ulong`) for an active mapping. Authored
permissions resolve logical names first, then compile these masks for each
mapping. A reused slot never inherits another action's grant. Target registers
have their own existing value storage and receive a separate reach set; they
do not consume the input budget. Full-width mask construction uses the Maths
bit helpers, including the 64-bit case, rather than shifting a word by 64.

## Modes are simulation state

`inputModes` declares independent families, one selected state per family, and
the names live in each state. `global` names are always eligible. A family
selector reads authoritative state at a tick boundary: an engagement kind, an
active kit, an enum/counter body register with a finite declared value set, or
a finite seat/session state cell. A numeric register may instead declare
disjoint exhaustive ranges mapped to finite states (for example zero versus
positive flight fuel); validation proves the partition over its declared
envelope. This derives a selection from the existing state without a second
mutable mode register. Selectors cannot read camera layouts, wall
time, client pages, or arbitrary presentation predicates. A seat without a body
uses the selector's declared default. Unknown selector values refuse the state
transition before applying it; they do not silently choose a convenient mode.

This proposed `.puck` shape illustrates composition; the referenced actions
and kit/state names must also be declared by the composed document:

```text
inputModes {
  global ["interact", "pause", "lookX", "lookY"]
  families [
    {
      name: "form"
      selector { kind: ActiveKit }
      default: "unembodied"
      suspendedBy [
        { family: "engagement", states ["tabletop", "machine"] }
      ]
      states [
        { name: "unembodied", values [], channels [] }
        { name: "walking", values ["moth"], channels ["forward", "strafe", "jump", "run", "rise", "flight"] }
        { name: "flying", values ["moth-flight"], channels ["forward", "strafe", "rise", "run", "flight"] }
      ]
    }
    {
      name: "engagement"
      selector { kind: Engagement }
      default: "none"
      states [
        { name: "none", values ["none"], channels [] }
        { name: "tabletop", values ["tabletop"], channels ["forward", "strafe", "confirm", "cancel"] }
        { name: "machine", values ["machine"], channels ["start", "select"] }
      ]
    }
  ]
}
```

The live set is the union of global and the selected, unsuspended states'
names. A family's optional `suspendedBy` list suppresses its channels when any
named family has one of the listed selected states. It reads selections, never
other families' suppression results, so evaluation has no cycle or priority.
The family still has a selected state and remains part of the tuple. Unknown
family/state references and self-suppression refuse at compilation. Repeating
one name in the live union costs one slot. Global eligibility does not bypass a
grant, route, or pause policy. `pause` denotes a declared action; a binding to
an administrative pause command remains a command under its existing authority.
Look channels exist only in worlds that declare them. The debug world's
presentation camera controls need not become simulation channels.

Union alone does not disable walking when seated. For the debug world,
`suspendedBy` suppresses the form's channels while the engagement family
supplies cursor movement. It cannot rely on the board page omitting jump.
An action cannot have two different live role bindings in one combination.
Several modules may observe the same action, but dispatch to kits and routed
targets remains explicit through the existing control applications.

Families default to all their states being mutually composable across families.
An optional `combinations` list contains tuples of state names, exactly one per
family; when present, it is the complete allowed set. This is a finite state
constraint enforced on every mutation, kit selection, engagement, reload, and
seat transition, not an author's unverified claim about reachability. Every
initial tuple must belong to it. A forbidden transition refuses atomically.
Without that list, the compiler conservatively checks the Cartesian product;
it never attempts to prove arbitrary rule predicates unreachable.

For independent selectors, authors can keep the global + form + engagement
union. For mutually exclusive controls they declare suppression or partition
by an authoritative finite control-state register and constrain the allowed
tuples. Neither choice introduces a genre-specific engine state.
Binding `contexts`, held pages, chords, inherited pages, and toggles remain
`Puck.Commands.BindingProfile.Compile`'s responsibility. The simulation publishes
the selected tuple to bindings in windowed and headless hosts alike. A page
switch alone changes no simulation mode.

## Compiling and validating storage

Compilation produces an immutable catalog of logical names and an immutable
mapping for each allowed tuple. Compilation and reload may allocate; a tick
selects a mapping and indexes preallocated banks.

1. Compose declarations; reject conflicting shape, unit, threshold, or role
   definitions for one name. Assign logical ids in ordinal name order and
   compile consumer references once. Imported declarations resolve to their
   owning declaration rather than creating copies.
2. Enumerate allowed tuples in ordinal family/state order. Deduplicate their
   live names. Validate role conflicts and the 32/64 budgets for each tuple.
   A failure names the tuple, bank, required count, ceiling, and every live
   channel charged to that bank. Numeric and button limits are independent.
3. Build interference sets per bank: two names interfere when any allowed
   tuple contains both. Assign preferred slots in descending interference
   degree, tie-breaking by ordinal name; choose the lowest available slot.
   Mutually exclusive actions can therefore share a preferred slot.
4. Emit each tuple's mapping. Reserve globals first in ordinal name order,
   then assign remaining live names their unused preferred slot when within
   budget, otherwise the lowest free slot. Identical mappings are interned.
   A tuple that passed step 2 always fits. Each mapping includes inverse names,
   typed accessors, role/kit bindings, and projected permission masks.

A fixed global graph colouring is insufficient: an odd cycle can require
three colours even though each allowed combination contains only two actions.
The compiled per-tuple fallback prevents an artificial capacity refusal in
that case. Slot addresses may differ across tuples; the epoch barrier and
logical-name continuity rules below make this safe. Each mapping includes
logical-id lookup and each declared transition compiles its keep set once.
A switch copies only admitted continuing actions through those handles into
cleared destination banks; no slot-to-slot identity or runtime graph search is
assumed. Checkpoint and transport metadata identify the selected mapping.

The finite compiler also needs admission bounds. Start with 4,096 logical
channels and 4,096 allowed tuples, independently of the active 32/64 budget;
logical ids and mapping ids fit `ushort`. Reject an expansion beyond the
compiled-catalog budget by family and cardinality, before materializing it.
Expose those costs through `world.budget`; they are load-time resource limits,
not a justification to charge dormant actions as live channels.

## Switching releases input

Switching releases input **by default**. A document may keep an action's held
level and toggle when its meaning continues. Reuse of a storage slot never
carries its previous value into another action. Keeping an action is an explicit
authoring choice, checked at the authority; globals have no implicit exemption.
There is no suspended toggle that silently resumes after an ineligible mode.

### Why the policy is authored

The owner's instruction is that the engine is "truly generic", must not
"design us into any corners", and uses channels "intelligently" where many
states have no use for a set of them. The owner also requests restoration of
autorun and autofly. Those are owner requirements; the default and the mechanism
below are design conclusions from them, not quotations of an owner-selected
reset algorithm. [AGENTS.md](../../AGENTS.md#puck-is-pre-release) distinguishes
owner rulings from agent-written conventions.

| Choice | Guideline that drives it |
|---|---|
| Declare continuity per action and directed family transition, with no engine genre names | The owner's generic-engine requirement above, and the ratified [game charter](../game/design.md#design-decisions): "every capability it needs lands as a game-agnostic primitive". Different worlds can keep fire, charge or a note without inheriting the Moth's locomotion policy. |
| Release by default; explicitly keep unchanged movement and auto-actions in the debug world | The same requirement includes board and UI worlds, where entry must not act, and the owner's restored auto-controls must remain useful. The charter says "Design is **feel-first**". A conservative omission default protects new transitions; explicit continuity preserves feel where the author knows the meaning. No guideline prescribes one universal reset for every genre. |
| Keep eligibility separate from continuity; never retain an inactive action or changed meaning | The owner's intelligent-use requirement calls for states where channels do not apply. Suppression removes authority as well as storage; continuity cannot bypass it. |
| Extend binding toggles, state selectors and the ordered mutation path | [AGENTS.md rule 7](../../AGENTS.md#core-rules): "Find the system before building one." Existing pages, contexts and destination-owned toggles supply the host mechanism; finite authoritative selectors and continuity admission are the gap. |
| Tape transitions and continuation state; preserve a held note's original time without replaying its press | [AGENTS.md rule 4](../../AGENTS.md#core-rules): "Same document and same input give bit-identical state on every run, machine and backend at a fixed code version." Host-only latch memory cannot decide the resumed result. |
| Change the current document/wire contract and every caller together | [AGENTS.md rule 5](../../AGENTS.md#core-rules): "No compat aliases, deprecation steps, migration shims or read-side tolerance for retired data shapes". No alternate legacy reset mode is retained. |

Supporting design evidence, rather than additional owner quotations:
[engine design](engine-design.md#documents-describe-content) puts game-specific
policies in authored content; [play](play.md#creatures-and-scale) keeps a closed
declarative vocabulary; [state and language](state-and-language.md#the-language)
requires document projection and printing; its [capacity rules](state-and-language.md#capacities)
price bounded storage and work. The [authoring guide](../authoring/README.md#source-document-and-running-state)
separates source, document and admitted runtime state. The
[documentation register](../development/documentation.md#write-examples-and-claims-precisely)
requires examples to distinguish target vocabulary from current behavior.
Together these drive a small data shape, compiled masks, named refusals and
read-back in existing tools, rather than callbacks or another interpreter.

### Document shape and validation

Each `inputModes.families` row may declare `transitions`. This target `.puck`
fragment extends the illustrative `form` family above; both states admit `rise`
because the same vertical-travel request can start flight and then climb:

```text
transitions [
  { from: "walking", to: "flying", keep ["forward", "strafe", "run", "rise"] }
  { from: "flying", to: "walking", keep ["forward", "strafe", "run"] }
]
```

`from` and `to` name different states of that family. `keep` is a set of logical
channel names, applying equally to physical holds and destination-owned toggle
contributions. An omitted transition or omitted name releases that action; an
empty list explicitly releases all. The reverse direction is authored
separately. With several changed families, an action must appear in **every**
changed family's keep set. Unchanged families add no restriction. A disappearing
action is always released, including when a family's channels are suppressed.
For example, keeping `run` in a form transition cannot defeat an engagement
transition with an empty keep set. Page changes use existing binding behavior
and do not consult this table unless authoritative mode state also changes.

Continuity requires the same logical id, shape, units, range, threshold and
semantic role, live and authorized on both sides, with the same input-owner
generation and controlled-target generation. A different kit may implement the
same movement or fire request differently; that is allowed. A different action,
role or routed recipient is not continuity. Authors give unrelated operations
different names; a shared direction name does not make walking and moving a
board cursor the same dispatch. A slot match or matching display label is never
evidence. A physical source rebound to another destination also rearms before
it can feed that destination, even if the old action continues from another
source. Retention does not grant a new consumer an old edge.

Compilation/whole-candidate validation refuses unknown family-state or channel
references, duplicate `(from, to)` rows or keep names, self-transitions,
`Relative` entries in `keep`, gaps/overlaps in selector ranges, and a keep entry
whose action cannot be live with the same contract on both sides of any allowed
tuple pair for that transition.
Pairs where another family suppresses it simply release it; a declaration with
no eligible pair is an authoring error. A statically known changed role is
refused, not treated as proof of continuity. Diagnostics name the source/module
span, family, from/to states, action and incompatible field or inactivity
reason, with a witness tuple when relevant: for example,
`form walking -> flying: keep jump: inactive in destination flying`.
Dynamic target/grant changes release the affected action with that reason in
read-back; they never widen a keep set. The source lowers to one document
member and prints back through the existing vocabulary and printer.

Compile family-transition keep sets and mapping lookups, not a quadratic table
of every pair of world tuples. On a switch, intersect changed-family sets,
filter admitted contributions, and fold at most the old live 32 numeric and 64
button actions into preallocated scratch through logical ids. Account for
declared transition rows, masks, bounded source contributions and continuation
records in the existing document memory/work budgets and `world.budget`;
reject over-budget declarations before allocating them.

### Epoch barrier and continuing holds

The server drains accepted control-state mutations, computes the tuple, and
checks for a switch before admitting that tick's gameplay input. Each committed
switch advances the input epoch, even when the mappings happen to be identical.
An intermediate transition cannot be collapsed away: walking to board to
walking in one tick still releases walking input. A kit or control-target
replacement that changes dispatch also advances the epoch when tuple labels
stay the same; without a declared state transition it releases affected input.

The barrier discards queued old-epoch batches, relative accumulators and old
rise/fall summaries. It clears destination banks, then seeds only eligible
continuing levels, prior levels, toggle contributions and held-action
continuation from the last admitted state. Filtering happens before the fold:
a released source cannot ride along in another source's kept value. Continuing
input has **no neutral tick and no new press**. Its ongoing charge or hold
duration remains intact. Released input is neutral immediately and ends through the consumer's
cancellation path; cancellation is not a gameplay release that fires a charged
shot or scores a note. Already launched world effects remain ordinary world
state. No rule, target or kit activation gets to read an old edge a second time.

An engagement press is consumed by the transition and cannot confirm a board
choice or operate a machine. More generally, every transition-causing edge is
consumed, including sibling edges from that same physical activation. If a
physical hold's effective destination/role set changes (Space/South changing
from jump to climb), its direct contributions rearm together. Independent
destination-owned toggles are not that physical hold. A continuing level is
distinct from that edge: autofly's admitted `rise` can
start flight and keep climbing without invoking its press behavior again.
The transition action's own edge latch stays spent until neutral; released
controls cannot be promoted into a fresh press by a cleared consumer latch.

If a rule changes mode during a step, install the barrier with that firing's
successful commit; a refused firing installs nothing. Later consumers see
only continuing levels under the new mapping, with no old edge. Earlier effects
are not undone or executed again. Fresh input for the new epoch is admitted
from the following tick. The ordinary deterministic mutation/rule order decides
multiple switches and preserves atomicity.

The authority publishes the new epoch, mapping, retained logical actions and
per-source continuation/rearm decisions. Hosts drop only released contributions.
Each released or rebound level source
must return to neutral and be actuated again; a toggle additionally clears its
latch and needs a fresh toggle activation. A kept toggle stays latched even if
its opener's page is closed. The same contract covers local, synthetic and
remote producers; a stale epoch batch is refused, not relabelled. After receiving
the barrier, producers reconcile fresh observed levels against the retained
baseline, so a release whose old batch was discarded is delivered once in the
new epoch rather than leaving a hold stuck. Relative sources discard
pre-barrier displacement and admit fresh displacement from the
next tick. Clients cannot choose modes or retention by writing an epoch.

Record a `ModeSwitch` boundary with old/new tuples and epochs, retained/released
logical ids and reasons. Ordinary event queries and partial sequence matches
start in the new epoch. Held continuation is the deliberate exception: one
bounded record per continuing held action keeps its original press id/time,
held-since time and consumption state, so a later release closes the same note
or charge. Expose that record through the existing history operand surface as
`inputHeldSince(owner, name)` and `inputHeldPress(owner, name)`, guarded by a
current held level. `inputHeldPress` is the original event identity, not a new
matchable edge. It remains available when the ring evicts the original press;
it cannot resurrect a buffered combo or consumed press. A real release carries
that hold identity for endpoint pairing. A reset cancels the record without a
scoring release. These records and their bytes are in the checkpoint/hash and
budget; partial multi-event patterns do not cross the epoch.

Disconnect, input-owner generation replacement, loss of an admitted input
route, cross-authority transfer and history overflow are unconditional resets.
An actual host input-capture loss is a recorded reset too; ordinary OS focus
changes are not invented as capture loss where the host intentionally keeps
reading devices. A changed routed recipient releases its actions even if it
has the same mode label. Reload recompiles names and resets actions whose
definition, binding destination, transition policy or dispatch contract changed.
An unchanged action under an unchanged tuple has no mode transition to apply
and retains state; if reload also changes a selection, both definitions must
admit the transition and keep that action. No-op reloads preserve state.
No action retains through revocation of its grant.

## Wire, replay, and state

The submission envelope remains the source of actor and target authority. The
channel payload uses the shared `WorldWireCodec` leaf for local input, network
input, replay, checkpoints, forwarded input, and held input. Each leaf is
length-delimited and decoded with the existing bounded wire reader.

| Field in wire order | Encoding |
|---|---|
| Mapping revision, input epoch | `uint32`, `uint64`; authority-issued, checked against the installed definition and owner |
| Mapping id, assigned simulation tick | `uint16`, `uint64` |
| Numeric slot count and values | `byte` count followed by that many signed 64-bit raw values in mapping slot order; exactly the mapping's used extent, holes zero |
| Button levels | `uint64`; unused bits zero |
| Pointer ray | Existing 0/1 presence byte, followed when present by six raw Q48.16 scalars |
| Contribution count and entries | `uint16` count, then admitted source handle (`uint16`), logical channel id (`uint16`), kind (`byte`: level, toggle or relative), raw value (`int64`) and activation id (`uint64`); duplicates refuse; count bounded by the admitted owner profile |
| Event count | `uint16`, checked against the document's per-step limit and remaining bytes before any allocation |
| Each event | `uint16` logical channel id, `byte` kind, `int64` raw value, `uint64` capture simulation tick, `int64` raw Q48.16 sub-tick seconds, `uint32` source sequence, `uint64` activation id |

Wire event kinds are `Press`, `Release`, and `AxisSample`. The server derives
named `AxisCrossing` history records by applying the compiled threshold and
hysteresis predicates to the ordered samples. Press/release raw values are
exactly one/zero; axis samples use their declared fixed-point shape.
The event logical id resolves through the recorded definition, not through
whichever name currently occupies a slot. A source sequence orders equal-time
events and deduplicates retransmissions within an input epoch. The host merges
its devices into one owner sequence; the envelope's admitted owner identifies
the source. Sequence exhaustion requires a new epoch before reuse. Batches preserve
all transitions; the held image is their final state. Analog samples needed by
authored crossings are processed in order at the host rather than inferred
from the final axis alone. The server checks declared shape, bounds, monotonic
sequence, timestamp range, authorized owner, epoch, and live channel membership.
It validates button transitions against prior admitted levels and the final
image; it does not trust a claimed impossible press/release sequence. Axis
crossings use declared thresholds and the accepted sample path.

Contribution entries are the complete bounded sparse image behind the submitted
levels, not additional values to sum over them. The authority folds them by
the existing composition policy and refuses a mismatch with the final image
or events. Zero entries report neutral for sources waiting to rearm; omitted
sources contribute zero but do not prove physical neutral. A source handle is
registered through the existing owner/profile admission: a direct logical
binding source or a destination-owned toggle, with its channel and role
contract. The registry is bounded by the compiled profile's contributions
(never more than 65,535 wire handles), costs bytes/work at admission and is
captured with the definition; no physical-device name becomes a rule operand.
Only active contributions and explicit neutral observations travel each tick;
inactive profile entries allocate no per-tick records. Synthetic producers
register equivalent semantic sources. Relative entries carry only this step's
displacement and never become held continuation. Retargeting a handle invalidates
its held state and requires rearming. All sibling outputs of one physical
activation carry the same activation id, authority-scoped to
that owner and epoch; zero denotes a sample without a discrete activation,
and nonzero ids are not reused within an epoch. Events still have distinct
sequence numbers. This lets transition consumption suppress sibling edges
without suppressing an unrelated toggle.

The host still uses `BindingProfile.Compile` and `InputRouter` for physical
activation. The authority validates registered destinations, grants, shape,
epoch, neutral/rearm transitions and final fold, and records accepted changes.
Continuation retains the admitted values, never an unacknowledged host flip.
Snapshot/restore publishes accepted destination latches back to the producer
so its next toggle flips the restored state. Source rearm and activation
continuation are session tape/checkpoint data through the same input-owner
leaf, alongside the authority's per-source armed state and accepted neutral
observations.
The authority can validate the protocol, not prove what hardware a remote
producer actually observed. A fresh device/session attachment starts neutral;
restoring simulation does not attach physical devices. Release events obtain
their hold's original press identity from admitted continuation at the server.

Scripted presses and producer input use the same admission path with offset
zero unless the script explicitly supplies an admitted sub-tick time. They
cannot bypass history, epoch checks, or release rules. Pure snapshot producers
synthesize transitions at the interval boundary. Repeated reads of one device
sample cannot create repeated presses.

The first typed-storage step uses one mapping for all declared channels and
does not add unimplemented event fields. Later steps change the current leaf
directly. There is one current format, no dual decoder or retired aliases.
Tapes affected by a format change are re-recorded by the recording verbs or
deleted if no longer useful. Generated schema/vocabulary outputs come from
their verbs. No committed shape fingerprint or layout hash is added.

The server records admission decisions and assigned ticks for timing-bearing
input. The authoritative hash and checkpoint include levels, prior levels,
epoch, selected tuple, admitted source registry/contributions, per-source rearm
and per-action held continuation, switch/cancellation outcomes, input sequence
state, history ring contents and cursor, matching consumption state, calibration,
and any clock state they read.
Derived mapping tables are rebuilt from the captured definition. A checkpoint
mid-hold or mid-pattern resumes identically. Cross-authority transfer resolves
names at the destination and starts a new epoch; it never copies source slots
or lets a partial pattern span unrelated world clocks.

Existing pooled contribution policy remains authoritative. A non-owner's
contribution cannot forge the owner's history. Owner history records admitted
semantic input; body action edges derive from the final authorized fold. A
world that needs a cooperative folded-input pattern explicitly reads a body
input history populated after that fold. The ordinary case allocates only the
declared owner histories, not one ring for every body in the population.

## Sub-tick time and clocks

The host quantizes a device event once to a target simulation interval and an
offset in Q48.16 seconds. One unit is about 15.26 microseconds, comfortably
below a millisecond. The offset is nonnegative and strictly less than the
interval's exact duration; rounding onto the endpoint carries into the next
interval. Conversion uses widened rational arithmetic and ties to even. A
device without event timestamps gets its observation time; read-back names
that lower precision. Quantization does not claim to repair polling latency.

An input timestamp is the assigned simulation tick plus this fixed offset,
not an absolute wall-clock value. Each interval's exact start and width use
the existing 50,400 Hz engine clock (`FixedTickConversion`). A rate change
starts a new interval at the current engine time; it never reinterprets older
ticks using the new rate. Checkpoints/history retain interval engine-time
coordinates needed by their events. Paused or rate-zero worlds advance no
gameplay input time; their existing administrative commands still work. Resume
starts neutral and cannot score accumulated wall-time presses.

Remote clock synchronization maps capture time to a simulation interval at
ingress. An on-time event is applied in that interval. A late event is admitted
to the next open tick, retains its captured engine-time coordinate and a late
flag, and is ordered for processing by admitted tick, capture time, source
identity, and sequence. Already executed state is not silently rewound. The
world authors admissible lateness and a judgement policy (capture time or
admission time); those choices and the admission outcome are taped. Future or
past timestamps outside the admitted horizon refuse. Remote capture precision
and trust are a transport policy, not a claim that the server observed a device.

The event leaf's tick/sub-tick pair describes capture time. A batch's assigned
tick describes processing time; the enclosing recorded interval table supplies
capture interval coordinates even when the two ticks differ. Local on-time
input normally has the same pair. Within one owner, capture times must be
monotonic with source sequence; genuinely concurrent sources use stable source
identity as a tie-break. Equal fixed timestamps can be treated as simultaneous
by a rule without erasing their deterministic processing order.

Each owner has a signed, bounded Q48.16 calibration offset in seconds, initially
zero. Positive calibration adds to the captured timestamp. Its authored bound,
value, and changes are authority state and replay data. Calibration changes
start a new input epoch; they do not rewrite historical timestamps. Rule
judgement computes `capturedTime + calibration - targetTime` in a widened
intermediate, rounding once to Q48.16 seconds. Calibration may put the judged
time outside its capture interval; that does not move the input's processing
tick or bypass lateness admission.

World elapsed time comes from engine ticks; beat judgement reads the existing
music clock and its authored tempo/epoch/transition state. Extend the existing
clock operand to evaluate at an event's captured time, including clock segments
retained for admitted late events. Do not compare an old press to the music
clock's position at the end of the current tick. A rhythm document defines its
judgement windows, lane names, hold rules, beat targets, and calibration policy.
Audio presentation latency can inform calibration but never changes the
simulation clock from an audio callback.

## History and rule surface

`inputHistory` declares retention and work bounds per owner: retained duration,
event capacity, maximum events per step, and any named axis crossing predicates
with press/release thresholds. Start defaults at 256 retained events and 128
events per step; authors size these with their shortest simulation rate,
maximum input density, and buffering window. These are explicit independent
budgets, not a four-player or sixty-Hz assumption. Allocate rings and admission
scratch on owner admission. Rule matching uses bounded reusable scratch.

The `uint16` event count bounds a batch to 65,535 wire events even if a document
requests more; validation refuses such a request. Admission preflights the
derived crossing count as well as received events before committing either
levels or history. Multiple crossing predicates cannot bypass the ring budget.

Expired records are evicted in sequence order. If a new batch would evict
events still inside the retention window or exceed the step budget, refuse the
whole batch with a named capacity outcome, release the owner, and begin a new
epoch. Never silently truncate a chord or drop a release while preserving its
press. Record that outcome so replay reproduces it. Report occupancy, refused
batches, and allocation counts through the existing budget/counter surfaces.

The following target operand surface extends the existing `channel` operand
and rule expression IR; it does not create a second rule interpreter:

| Operand or effect | Meaning |
|---|---|
| `channel(owner, name)` | Current admitted level; binary reads 0 or 1. An inactive declared name reads neutral. An unknown name is a compile error. |
| `inputEdge(owner, name, kind, occurrence)` | Whether the indexed newest matching event exists in the current epoch and retained window. |
| `inputTime(owner, name, kind, occurrence)` | That event's fixed seconds in the world clock, with calibration applied. Requires a successful edge guard; missing events cannot masquerade as time zero. |
| `inputAge(owner, name, kind, occurrence)` | Current world time minus event time in fixed seconds. Frame buffers additionally compare admitted simulation ticks exactly. |
| `inputHeldSince(owner, name)`, `inputHeldPress(owner, name)` | Original held-since time and press identity of a current hold, including explicitly kept continuity across epochs. Require a held-level guard; these do not expose a fresh edge or a buffered sequence. |
| `inputMatch(owner, pattern)` | Whether the named compiled pattern has an unconsumed match; provides its first/last event ids, ticks, and times to the rule frame. |
| `consumeInput(owner, pattern)` | Marks that matched event range consumed for this pattern through the normal rule transaction; a refused firing does not consume it. |

Owners are typed references resolved by the existing rule frame (seat/session,
or explicitly a folded body history); literals naming a physical device are
not a rule surface. Several events within one tick are accessible by occurrence
or by a bounded `forEach` event iterator. A Level rule over only the latest
press is insufficient for multiple rhythm notes in one step.

`inputPatterns` contains named ordered steps, maximum total and inter-step
duration, optional exact tick gaps, simultaneous groups and their fixed-time
tolerance, held predicates, and consumption policy. Steps reference semantic
press/release events or named directional crossings. Direction predicates may
read an authored deterministic facing reference at event admission, allowing
relative fighting motions; the history records the resolved crossing, so a
later turn cannot reinterpret an earlier down-forward input. Bounds, threshold
hysteresis, and overlap policy are document data. A same-timestamp group is
order independent; steps outside a group are strictly ordered. Repeated
prefixes use the existing sequence-matching principles in binding activators,
but operate on authoritative events, not physical source strings.

Matching selects the earliest ending eligible sequence, then the latest start,
then lexicographic event ids for an exact tie. An event may participate in
several patterns unless an authored consumption group claims it; priorities
within such a group are explicit document order. Pure reads do not consume.
The cursor/claimed ranges are checkpointed and hashed. A buffered press remains
eligible until its document window expires or its consuming effect succeeds.
Held-note rules pair the hold's original press identity with its real release
and read their separate timestamps; `inputHeldPress`/`inputHeldSince` expose a
kept hold whose press predates the current epoch or ring retention window.

## Migration of live documents and consumers

All sixteen live channel rows are accounted for below. The debug package's
other worlds inherit the Moth basis or the shared imports; no quarantined world
is promoted to live status to demonstrate this work.

| Existing declaration | Target meaning and owner |
|---|---|
| Moth `forward`, `strafe` | Shared directional axes; retain names and motion roles. Locomotion and tabletop cursor rules use different dispatch contracts, so engagement releases them even though names are shared. |
| Moth `turn` | Shared normalized turn-rate axis; retain its role. It is distinct from relative look displacement. |
| Moth `faceX`, `faceY`, `faceZ` | Shared fixed-point facing vector components; retain role names, charge three numeric slots. |
| Moth `rise` | Shared vertical movement axis; keep its role and enable it in forms that use it. |
| Moth `jump`, `flight`, `run` | Shared binary actions; kit/form modes determine their eligibility. |
| Moth `start`, `select` | Machine input meanings, enabled by machine engagement; never board confirm/cancel aliases. |
| Moth `interact` | Shared global context/leave action, retaining existing engagement interception. |
| `suit.puck` `suit` | Named suit-toggle action, distinct from the `suit` body register; enabled where changing form is legal. |
| `noclip.puck` `noclip` | Named debug traversal-toggle action, distinct from its register; enabled by authored debug modes. |
| `chess.puck` `boardPick` | Shared `confirm`; rename all bindings, rule operands, tests, icons, and inspection expectations. |
| Board cancel binding to Moth `select` | New shared `cancel` declaration; rewrite the board binding and its rule operand, delete the Select coupling and ceiling comment. |

The initial typed-storage step therefore has seven numeric axes and ten
buttons in the debug world. Shared declarations move to one imported input
document; the Moth, suit, noclip, and chess modules consume its names. The
package's basis, garden, night field, and standalone test compositions must
resolve that owner without redeclaring channels. Confirm and cancel are reusable
semantic names even before modal packing lands.

Binding migration covers the Moth's base layer, `controls.puck`, `pad.puck`,
`pad-kit.puck` templates, chess's tabletop context/page, device and desktop
overlays, identity binding overlays, and live rebind validation. Preserve source
lists, modifiers, held/chord ordering, inheritance, toggle ownership, label/icon
resolution, and command grants. Update semantic destinations; do not turn page
selection into simulation mode authority or rename commands merely because
they eventually write a channel. Auto-run and auto-fly remain ordinary
destination-owned toggles of `forward` and `rise`, and follow the same authored
continuity as a held source. A page change alone leaves their latches intact.

### Debug-world transition policy

The following is the C3/C4 migration contract, not a claim that the current
sixteen-slot runtime implements epochs. Declare finite authoritative form,
flight and engagement selections; an active kit alone cannot distinguish a
suited Moth walking from the same kit flying. Flight eligibility follows the
existing flight-fuel register through the selector's zero/positive partition,
not the camera page, a shadow mode register or an enumeration of every fuel
value. The `rise` action remains eligible in a suited walking state: the Moth's
existing `fly-1` rule uses it to start flight. The out-of-suit pilot has no such
action except while noclip is active. All form changes below remain subject to
their existing mutation gates.

| Transition | Authored continuing input | Released input and reason |
|---|---|---|
| Enter or leave a screen or chess board; change the engaged target | None; engagement transitions keep an empty set | All held channels and toggles, including run, autorun (`forward`) and autofly (`rise`). The recipient or dispatch changes. The entry/leave activation is consumed; held movement cannot navigate the board or drive a machine, and leaving never resumes a dormant auto-action. |
| Enter flight in the suit | `forward`, `strafe`, `turn`, facing components, `run`, `rise` | `jump`, `flight`, `interact` and other unlisted actions release. A jump must not turn into climb through a shared physical button. An already admitted independent rise/autofly request keeps climbing after it starts flight; its triggering activation is spent. |
| Leave flight for suited walking, including loss of flight availability | `forward`, `strafe`, `turn`, facing components, `run` | `rise` and autofly clear, even though walking admits rise to request a later takeoff. This prevents a Level rule immediately turning flight back on. Jump and transition buttons require neutral/re-actuation. |
| Suit on/off while grounded and not engaged | `forward`, `strafe`, `turn`, facing components, `run` | `rise`/autofly, `jump`, and the suit/flight/interaction buttons clear. Run and autorun keep the same movement request; flight does not exist in the pilot kit. The existing suit mutation's grounded gate remains. |
| Enter noclip from a walking form | `forward`, `strafe`, `turn`, facing components, `run` | `rise`/autofly and other unlisted actions clear; entering free flight alone does not start climbing. Run/autorun continue on the same body. |
| Enter noclip from suited flight, or leave noclip into suited flight | The movement set above plus `rise` | The vertical request keeps its meaning and recipient, so held rise/autofly may continue; transition buttons and jump clear. |
| Leave noclip into a non-flying form, or change suit while noclip stays active | The movement set above; also `rise` when both sides remain noclip | A destination without vertical travel clears rise/autofly. A suit change that leaves noclip active keeps vertical travel on the same body. Other unlisted actions clear. |

The movement set includes held run and its toggle, and both held forward and
autorun. The same rules apply in either direction only where the table says so;
author both directed rows. If form and flight change together, each changed
family's row must permit each continuing name. Engagement's empty keep set
always wins. Bindings whose destination changes at a shared source rearm even
when another source keeps that action. Thus Space/South held for jump cannot
start a new rise contribution, while an independently latched autofly can
continue. Suit/noclip/flight body registers are world state, not input-side
toggle latches: releasing their input does not flip those registers back.
The existing movement, fuel, collision and engagement rules continue to own
the resulting pose and motion.

These authored choices implement the owner's restored control set and the
charter's feel-first requirement while taking the generic-engine requirement
literally: a weapon swap may keep `fire`, a stance change may keep `charge`,
and a rhythm section change may keep a lane hold without giving any of those
names or policies to the engine.

Every consumer migrates in the same step as its representation: schema and
validation, `WorldChannelTable`, role handles, kit action tables, rule operands,
contribution folds, engagement routing, addon reach/consent and act decoding,
input holds, session latches, producers, body edge state, snapshots, transfers,
checkpoint leaves, replay inspection, and client binding compilation. The
transfer bounds in `WorldWireLeaves` must distinguish logical declarations
from active storage. Search source and live fixtures for `ChannelLimits`,
`ChannelValues`, and raw mask arithmetic before declaring the migration done.

`body.rig` changes only its channel naming/read-back, preserving the independently
owned motion fields. `replay.inspect`, `world.affordances`, `body.channels`, the
controller bar, and verb help report action name, type, current mode, value,
and inactivity reason where relevant. `player.bindings` and `world.affordances`
also show the selected family's directed transition, effective keep/release
decision and its document source; `body.channels` shows held/toggled, retained,
cancelled or awaiting-neutral state and the reason. The controller bar keeps
toggle/continuation indication separate from physical button depression, and
marks an inactive or awaiting-neutral action. `replay.inspect` names the switch
tick/epoch, consumed activation, kept hold origin and cancellations. These are
extensions of existing read-backs, not another diagnostic command family.
A tape reports names from its captured definition. Raw slots are optional
diagnostic detail, never the primary label.
Inactive bindings cannot dispatch even if a UI page still displays them.

## Genre check

These are design checks, not claims of implemented sample games.

| World | Values and modes | Timing and rule requirement | Model check |
|---|---|---|---|
| Third-person action | Movement/facing axes, jump/run/flight buttons; global + form + engagement | Keep authored run/autorun through compatible locomotion changes; keep autofly into flight but clear it on flight exit; no jump-to-climb or board leakage | Debug migration exercises directional keep rows, changed-recipient release and consumed transition edges. |
| Twin-stick shooter | Independent movement and aim axes; fire and ability buttons | Keep held fire across an authored ability-mode change; buffered presses do not cross its epoch | Neither motion roles nor names force aim to follow movement; continuity is per action. |
| FPS | Q32.32 relative look, normalized movement, many buttons; weapon modes | Keep held fire through a weapon swap when it remains the same fire request on the same controlled body; switching does not fabricate a new shot edge | Relative displacement is always consumed/discarded at the barrier, never held; keep is invalid for Relative. No frame-rate multiplier; 64 buttons use no numeric slots. |
| 2D fighting | Direction axes, attack buttons, stance modes | A declared kept charge survives stance change with its original held-since time; changed attack meanings cancel | Epochs reset buffered combos, while per-action continuation preserves charge; facing-relative events retain their admission meaning. |
| Rhythm | Authored lane buttons/axes; section, play and menu modes | Keep a lane hold through a section transition with original press/time and eventual release; menu entry cancels without scoring a release | Continuation records survive ring eviction; no synthetic press or lost hold duration. Ordered sub-tick events and historical clock segments retain judgement precision. |
| Turn-based board | Shared direction axes, confirm/cancel, global leave | A seated press chooses once; entry press cannot choose a piece; locomotion and auto-actions release on entry and exit | Board dispatch differs even for a shared direction name. Dormant avatar actions use no live budget and cannot revive on leaving. |
| Menu/UI-only | Seat/session actions without bodies or motion roles | Authors may keep navigation repeat between compatible panels; confirm releases by default and the opener is consumed | No avatar, physics, device, fixed seat count or particular positive rate is required. A rate-zero world uses administrative commands until gameplay steps start. |

## Implementation steps

Each step is independently reviewable and leaves one current representation.
The [open items](../plans/open-items.md#input-channels) carry completion status.

1. **Typed storage and shared semantics.** Replace sixteen uniform values with
   32 numeric values and 64 packed buttons. Split masks and target-register
   reach. Migrate every consumer and wire/checkpoint leaf together. Declare
   shared debug inputs once, rename board pick to confirm, and restore cancel.
   Keep all declared channels live in the single mapping at this stage.
2. **Relative input.** Add the `Relative` shape and Q32.32 accessors in the
   numeric bank, ordered host accumulation and authored units/sensitivity/bounds.
   Demonstrate displacement conservation, single consumption, and no tick-rate
   scaling; keep stick-rate behavior unchanged.
3. **Authoritative modes and allocation.** Add finite selectors, composed tuples,
   validation, compiled mappings, projected permissions and directed per-family
   keep sets, with named invalid-continuity refusals and budget read-back. Wire
   authoritative mode publication to headless and windowed bindings, and migrate
   the debug form/flight/engagement modes. Show a named over-budget refusal and
   a world whose declared vocabulary exceeds its live storage budget. Author the debug
   migration table's movement and auto-action choices explicitly.
4. **Switch barrier and continuity.** Implement epoch checking, release by
   default, logical-id carry only for admitted keep sets, consumed transition
   activations, cancellation and neutral rearming for released/rebound sources.
   Keep held/toggle levels and continuation without a neutral tick or repeated
   edge; admit bounded source provenance and tape/checkpoint accepted
   contributions, toggle and rearm state. Cover reload/disconnect/routing resets
   and checkpoint/hash continuity. Demonstrate
   kept run/autorun, autofly starting flight without cancelling itself, cleared
   autofly on flight exit, and neutral board/screen entry and exit. Steps 3 and 4
   are delivered together if enabling modal slot reuse would otherwise expose
   old input; there is no intermediate release where reusable slots lack the barrier.
5. **Timed ingress and history.** Add ordered events, exact interval mapping,
   lateness admission, calibration, bounded rings, overflow outcomes, and all
   replay/checkpoint/hash fields. Demonstrate a press and release in one tick,
   equal-time ordering, ring wrap, and a mid-session replay restore. Extend C4's
   held continuation with original event identity/time and cancellation outcomes.
6. **Rule patterns and clocks.** Extend the existing operand/effect compiler
   with edge iteration, matching, consumption, tick buffers, held predicates,
   and event-time clock judgement. Demonstrate a motion input, simultaneous
   presses, buffered action, and sub-millisecond boundary judgement at two
   different valid simulation rates, plus a kept rhythm hold and fighting charge
   spanning a mode switch with no fabricated edge. Add no genre-specific engine
   opcode.
7. **Read-back and cleanup.** Complete current-mode naming across tools and the
   controller bar, remove superseded ordinal surfaces and stale docs, and
   verify the genre table against executable examples. Each earlier step still
   updates the read-backs and docs it directly changes; this step closes the
   combined inspection and authoring experience.

For each implementation step, build the whole solution without warnings;
regenerate affected schema, vocabulary, and verb artifacts through the private
candidate CLI; run every `worlds/debug/*-tests.puck` with `puck test --reproduce`
and the Transpiler, Cli.Worlds, Schema, Server, and Client CPU suites. Verify
world behavior by running the real World with `--devices none`, using the
GPU/heavy-suite leases where needed. Re-record affected tapes through verbs.
Measure steady-tick allocation and counts for held input, event bursts, and
history matching. Runtime capacity, releases, replay identity, and exact fixed
results deserve checks; internal array shapes do not become compatibility gates.

## Existing foundations

The implementation extends these owners rather than introducing parallel paths:

- [Channel schema and compiled table](../../src/Puck.World.Schema/WorldChannel.cs),
  [intent storage](../../src/Puck.World.Schema/PlayerIntent.cs), and
  [permission masks](../../src/Puck.World.Schema/ChannelPolicy.cs).
- [Binding composition](../../.claude/skills/puck-world/references/documents-binding.md)
  and [state ownership](../../.claude/skills/puck-world/references/documents-state.md).
- [Tick admission order](../../src/Puck.World.Server/WorldTick.Step.cs),
  [contribution folds](../../src/Puck.World.Server/WorldTick.Channels.cs), and
  [music clock read-back](../../src/Puck.World.Server/WorldTick.Music.cs).
- [Shared wire leaves](../../src/Puck.World.Protocol/Protocol/WorldWireCodec.cs),
  [transfer leaves](../../src/Puck.World.Server/WorldWireLeaves.cs), and
  [replay and checkpoint contracts](../../.claude/skills/puck-world/references/replay.md).
- [Physical binding sequences](../../src/Puck.Commands/BindingActivatorDefinition.cs)
  and [the existing rule operand representation](../../src/Puck.State/Operands/StateChannelRef.cs).

The source search finds no authoritative input-history or sub-tick input-event
type in the live or experimental C# trees; physics substeps are unrelated.
Existing binding sequences and session/world replay history supply reusable
mechanisms, but neither exposes a seat's semantic event history to simulation
rules. That is the named gap the history steps address.
