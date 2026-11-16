# A body's state behind one write door

The design memo for the storage of a body's simulation continuation. Today
nothing can say a body is unchanged, so every authority hash encodes every
active body again, and the rule host compares every body's pose and facts on
every pass. Each decision names its options, the evidence, the failure modes
each option closes, the favoured option, the strongest case against it, the
invariant and the one place that enforces it. It supersedes the design
paragraphs of [the refactoring packet's item](open-items.md#refactoring-campaign)
and [the test runner packet's order](open-items.md#test-runner-and-replay). No
build lane starts before a review from another family has read it;
[its answers](#review-answers) go at the end.

Labels: **[opened]** means this memo read the symbol at this tree;
**[measured]** means a count this memo's lane read from a headless debug world
(the method is under [what a tick changes](#what-a-tick-changes), the full
readings are in the commit that lands this memo); **[spike]** means a
compile-only prototype this lane ran and deleted; **[inference]** means this
memo's reasoning; **[unconfirmed]** means nobody has checked it. A plan's
citations are leads: recheck the symbol before building from one.

## Summary

- **State moves into sections, and a section has one door.** A body's
  continuation is grouped into a dozen value-type sections (pose, motion,
  clocks, input images, the action track and so on). Each lives inside a small
  holder that hands it out two ways: a read that the compiler will not let
  anyone write through, and a write that moves the section's version. The
  section type is constrained to hold no reference, so it cannot contain an
  array whose elements escape the door; per-channel arrays become inline
  arrays and variable-length ones get a holder of their own.
- **The compiler holds the door; an analyzer holds four narrow leftovers.**
  Every write shape the body uses today (assignment, compound assignment, a
  `ref` or `out` pass, a method on a mutable struct, an array element) either
  compiles only through the write door or is refused. What the compiler cannot
  see is a short, closed list that an analyzer checks at the declaration or the
  call: a mutating method called through the read side (a lost write, never a
  stale digest), a reinterpreting cast, a reference-typed field in a section,
  and a body field that is in no section and carries no label.
- **The digest is cached per section, and the fold is static over sections.**
  The encoder of a section is a static function of that section's value, so it
  cannot read a field that is outside the door. A slot's digest folds its
  sections' digests and a small live tail; a segment's digest folds its slots'.
- **Moving bodies benefit, because most of a body does not move.** A slot
  encodes to about 9 KB, and 78 % of that is per-channel action arrays and
  input images. In the debug world a recorded tick encodes 5.3 MB for 590
  bodies; one version per body would cut that about sixfold (the 488 parked
  bodies), and versions per section cut it about ninetyfold, because a walking
  body re-encodes a few hundred bytes of pose and motion rather than all nine
  kilobytes.
- **The same versions feed the rule host.** Its fact derivation visits the
  bodies whose pose or contact section moved instead of comparing every slot on
  each of eleven passes a tick.
- **The proof is the existing switch.** A recompute folds every section afresh
  and names the body and section whose cached digest disagrees; `puck test
  --reproduce` runs it on every replayed tick, and every other hash samples one
  slot in rotation, exactly as the arena's row digests are checked today.
- **The rule latch, the input history and the action state names follow the
  same pattern, each in its own store.** The latch needs a door on its edge
  bits and digests of names that never change; the input owners reuse the door
  types; the action state names stop folding per body.

## What exists

### The fold and what it reads

`WorldPopulation.AppendContinuationHash` builds each active slot's checkpoint
entry as a view (`WorldPopulation.EntryCheckpoint`, which calls
`WorldBody.ViewTransferState` and `WorldBody.CaptureIntegrationResidue`),
writes it through `WorldAuthorityCheckpointCodec.WriteContinuationEntry` and
hashes the bytes. Its own remark states the problem **[opened]**
(`src/Puck.World.Server/WorldPopulation.Checkpoint.cs:23`):

```csharp
    /// <remarks>Every slot encodes afresh on every fold. A body's continuation spans dozens of fields and arrays its
    /// step, commands, contacts and tethers write directly, so no version covers it; a cached slot digest would need
    /// every one of those writes to pass one door that moves a version.</remarks>
```

The view hands out the body's live arrays **[opened]**
(`src/Puck.World.Server/WorldBody.Transfer.cs:117`):

```csharp
    // A view's array is the live one; a capture's is its own copy, taken as Take takes one.
    private static T[] Own<T>(bool alias, CaptureBuffers? buffers, T[] live) => (alias
```

A rest-epoch cache of this fold was deleted after a review found four writes to
sleeping bodies that it missed ([the job system memo](engine-job-system.md#the-incremental-state-hash));
those four now wake the body (`WorldBody.WakeIfResting`).

### Every writer

The inventory was taken by binding `src/Puck.World.Server` with the compiler
and classifying every reference to an instance field or stored property of
`WorldBody`, its nested state carriers, the population's `Entry`,
`BodyProducerState`, `BodyAutonomyState` and `BodyNavigationState`
**[measured]**. A member counts as continuation when a capture function reads
it. The per-site list (file and line for every row below) is in the hand-back
and the commit.

| Owner | Stored members | In the continuation | Writes | Calls on a mutable value | `ref` or `out` | Array handed to a callee | Alias kept |
|---|---|---|---|---|---|---|---|
| `WorldBody` | 163 | 97 (and 10 that are view scratch or addresses) | 442 | 73 | 12 | 45 | 2 |
| Its lane runtime and tape segment structs | 6 | 6 | 17 | 0 | 0 | 3 | 0 |
| `Entry` | 39 | 26 | 195 | 19 | 5 | 4 | 7 |
| `BodyProducerState` | 17 | 16 | 65 | 0 | 0 | 0 | 0 |
| `BodyAutonomyState` | 8 | 8 | 14 | 0 | 10 | 0 | 0 |
| `BodyNavigationState` | 7 | 7 | 23 | 1 | 3 | 0 | 1 |

By shape, the body's 107 captured members are 30 scalars, 33 immutable structs,
14 mutable structs (the rate accumulators and the tether), 23 arrays and 7
references. Three shapes are the ones a plain property cannot hold:

- **Arrays, 23 of them.** Eight are sized by `ChannelLimits.MaxChannels` and
  written by element (`WorldBody.MergeLaneTimer`, `WorldBody.PressChannel`,
  `WorldBody.Stop`, the timers in `WorldBody.Advance`); the rest are sized by
  the kit's register file or its recency tables. Forty-five sites hand a whole
  array to a callee (`Array.Clear`, `CopyClamped`, the view's `Own`).
- **Mutable structs, 14.** Sixty-two calls mutate an accumulator in place
  (`FixedRateAccumulator.Integrate` and its `Reset`), and three sites pass
  one by `ref` (`WorldBody.SteerUpToward` takes its accumulator that way); the
  other nine `ref` sites alias an array element, the position or view scratch.
- **State outside the body.** The rigid contact manifolds are objects the
  solver mutates (`WorldRigidContact`); the action state registers are arena
  rows (`WorldActionStateLane`); the contact-field observation is derived from
  population versions (`WorldPopulation.ContactFieldVersion`); the profile is a
  mutable `WorldIdentity`.

Fifty-six body members are not captured. Most are compiled configuration
(`FixedMotionTuning`, the collider) or scratch. Six are facts a step restores
by hand after a refused sweep and a checkpoint leaves to be derived again
(`WorldBody.MotionFacts`: the contact count, the obstruction witness, the
medium flags); whether a rule can read one between a restore and the next step
is **[unconfirmed]** and listed under [open questions](#open-questions-and-their-owners).

### What a tick changes

A headless debug world with `replay.record` armed, read with `world.counters
world.tick` at ticks 300 and 600 through `world.at`, with temporary counters
that compared each slot's encoded bytes, range by range, with the same slot's
bytes at the previous fold **[measured]**. The counts are load-independent and
match the tick lane's: four whole-population folds a tick (four authorities
run in the process), 590 slots encoded, 5,322,564 bytes.

| Slots per tick | Count | Bytes encoded | Slots whose bytes changed |
|---|---|---|---|
| Parked rigid bodies | 488 | 4,465,137 | 0 |
| Awake kit bodies | 102 | 857,427 | 96 |
| Asleep kit bodies, awake rigid bodies | 0 | 0 | 0 |

A slot is 9,021 bytes on average, and its bytes divide like this:

| Part of the encoding | Bytes a slot | Share | Slots a tick in which it changed |
|---|---|---|---|
| Action track: channel timers and their values, previous bits, pending presses, lane latches and recency, script actors | 4,364 | 48 % | 2 (the previous bits) |
| Input images: submitted, held, producer, transfer-held, three read-backs, the engaged image, the autonomy steering image, the transfer's held image | 2,664 | 30 % | 11 (two read-backs and the engaged image), 7 (the steering image) |
| Rigid contacts | 630 | 7 % | 0 |
| Action state registers (also folded by the arena) | 371 | 4 % | 0 |
| Pose and motion: position, yaw, velocities, remainders, followers, hold, tether, rigid state | 621 | 7 % | 21 |
| Up-turn frame and remainders | 53 | 0.6 % | 80 |
| Autonomy cadence counters | 48 | 0.5 % | 28 |
| Producer and flock runtime | 104 | 1 % | 7 |
| Entry head: generation, flags, placement, grants, identity | 96 | 1 % | 0 |
| Navigation, tape, sleep, recency, profile | 70 | 0.8 % | 0 |

Read against the 36 ranges the counters split a slot into, the bytes that
changed were 24,289 a tick, 0.46 % of what the fold encodes. A second window
(ticks 2,100 to 2,400) reads the same, with a rigid body awake in one tick of
six and a parked body's bytes changing six times in three hundred ticks.

Two things stand out. About twenty bodies are moving; about eighty more are
advanced every tick and stand still, and their bytes change only because
`WorldBody.SteerUpToward` integrates its turn budget before it knows whether
there is a turn to make **[inference]**
(`src/Puck.World.Server/WorldBody.Step.cs:1286`):

```csharp
        var budget = accumulator.Integrate(
            elapsedTicks: stepTicks,
            ratePerSecond: halfRate
        );
```

And two of the input images that change every tick for eleven bodies are the
read-backs `body.channels` prints, which the simulation never reads.

### The same state, folded four more times

The authoritative hash's components overlap the continuation
(`WorldPersistence.AppendStateHashComponent`) **[opened]**:
`WorldStateHashComponent.PopulationPose` folds each pose
(`WorldReplaySnapshot.HashState`), `WorldStateHashComponent.BodyActionState`
folds each lane's latch and recency and hashes every register definition's
name per body (`WorldBody.AppendActionStateHash`),
`WorldStateHashComponent.Flock` folds the producer and autonomy runtime
(`WorldPopulation.AppendFlockStateHash`), `WorldStateHashComponent.Navigation`
folds each route (`WorldPopulation.AppendNavigationStateHash`), and the
`WorldStateHashComponent.Arena` component folds the register values the
continuation also encodes.

### What already works this way

- **The arena.** `StateArena.RowVersion` moves only when a row's committed
  bytes differ, every move passes one door that also feeds a log any number of
  readers follow from their own place (`StateArena.TryReadMoves`), a row's
  digest is cached by its version, and a segment's digest holds until one of
  its rows refolds (`StateArena.ComputeHash`).
- **The cross-check.** `StateArena.RecomputeHash` and
  `WorldStateHashComposition.RecomputeAuthoritative` fold with nothing cached,
  `WorldStateHashComposition.DescribeStaleDigest` names the component that
  disagrees, `WorldReplayProbe.VerifiesIncrementalHash` runs it on every tick
  `puck test --reproduce` replays, and `StateArena.CheckDigests` samples one
  row and one segment on every other hash (`WorldPersistence.StaleDigest`).
- **A setter that is the only writer.** The population's `Entry` already keeps
  its occupancy behind the `field` keyword **[opened]**
  (`src/Puck.World.Server/WorldPopulation.cs:451`):

  ```csharp
        public bool Active {
            get;
            set {
                if (field == value) {
                    return;
                }
  ```

- **A discontinuity door.** `WorldPopulation.DisturbBody` is the one place a
  teleport, a collider change or a slot letting its body go wakes what rests on
  the body.

## Failure modes

| | Failure | Kind |
|---|---|---|
| F1 | A write that does not move the version: a plain field assignment, as the four missed writes were | Silent stale digest |
| F2 | An array element written through a reference handed out for reading, or through a local that aliases the array | Silent stale digest |
| F3 | A mutable struct mutated through a copy: the call compiles and the stored value does not change | Silent lost write |
| F4 | A new field that is state and is not versioned, encoded or restored | Silent, until a restore diverges |
| F5 | A value the digest reads that no body version covers: the field observation, the registers, the profile, a manifold the solver wrote | Silent stale digest |
| F6 | A fold beside a write, or a write through a reference taken before a fold | Silent stale digest |
| F7 | A version that moves when nothing changed, so a resting or standing body folds again | Lost payoff, visible in counters |
| F8 | The conversion itself changing behaviour | Silent |
| F9 | A deliberate reinterpretation (`Unsafe.AsRef`, a span cast) | Bypass on purpose |

## Decision 1: the door

**What must be decided.** How the storage is grouped so that a write which
bypasses the version does not compile, arrays and mutable structs included, and
what forces a new field to be covered.

**Options.**

| Option | Evidence | Cost | Fails at | Closes |
|---|---|---|---|---|
| 1a. A field-backed property per field (the `field` keyword), whose setter compares and moves the version | The `Entry` setters above; the backing field has no name, so nothing else can write it | About a hundred properties; every call that mutates a struct in place rewritten by hand | F2: an element write through an array property compiles **[spike]**. F3 and F8: a mutating call on a struct property compiles and mutates a copy **[spike]**, so each of the 62 existing accumulator calls becomes a silent no-op unless it is rewritten. F4: an ordinary field beside the properties is untracked | F1 for scalars; F7 for free |
| 1b. Generated accessors: a source generator implements partial properties and the capture functions from one annotated declaration | It removes 1a's boilerplate and closes F4 for annotated members | A generator in every build of the server project; the property shape keeps 1a's holes | F2, F3, F8 as 1a | F1, F4 |
| 1c. An analyzer over plain fields: every write to a marked field must pass a helper | `OutcomeAnalyzer` and `StaleCaptureAnalyzer` are precedent for build-error rules | Small to write | It must list every write shape there is: assignment, compound, increment, `ref`, `out`, a `ref` local, a mutating call, an element, an alias local, a span over the array, a callee that writes its argument. The list is open, and the old cache failed on exactly a shape nobody listed | F1 to F3 only as far as the list reaches |
| 1d. Sections of value-type state inside a holder that exposes a read-only reference and a version-moving writable reference | The compiler's own rules for `ref readonly` refuse every write shape through the read side but two **[spike]**; every existing shape compiles unchanged through the write side | The body's reads and writes are renamed section by section (about 870 reads and 600 write sites); inline arrays for the per-channel state | F3 through the read side (a lost write, never a stale digest); F9 | F1, F2 and F8 by the compiler; F4 by the static encoder and a rule at the declaration; F3 and F9 by a rule at the call |
| 1e. No door: keep a shadow copy of each section and compare bytes at the fold | Correct by construction, since the cache key is the content | The fold stays proportional to the population, and the state is held twice | Scale: a compare of every byte of every body on every hash | F1 to F6, at that price |
| 1f. Body state as arena rows | The arena already has the door, the log, the digests and the checks | Every field write on the body step's path becomes a journalled arena write; quaternions, intent images and accumulators are not cell kinds | The step's cost; the cell model | F1 to F6 |

**The spike.** One harness compiled each statement against a section holder, a
field-keyword class and an inline array, and printed the diagnostics **[spike]**.

| Statement | Through the read side | Through the write side | On a `field` property |
|---|---|---|---|
| Assign a field; compound assign | Refused (CS8332) | Compiles | Compiles (the setter runs) |
| Assign a field of a nested struct | Refused (CS8332) | Compiles | Refused (CS1612) |
| Pass a field or a nested struct by `ref` | Refused (CS8330) | Compiles | Refused (CS0206) |
| Write an inline array's element; take a `ref` to it | Refused (CS8331, CS8329) | Compiles | Not applicable |
| Write through an embedded array wrapper's writable span (an extension taking `ref this`) | Refused (CS8330) | Compiles | Not applicable |
| Assign the whole section; take a `ref` local of it | Refused (CS8331, CS8329) | Compiles | Not applicable |
| Replace the holder | Refused (CS0191, the field is `readonly`) | | |
| **Call a mutating method on a nested struct** | **Compiles, mutates a copy** | Compiles, mutates in place | **Compiles, mutates a copy** |
| **Write an element of an array field** | **Compiles** | Compiles | **Compiles** |
| `Unsafe.AsRef(in ...)` then assign | Compiles | | |

The two bold rows are why the grouping matters. An array field inside a section
is refused by constraining the section type to hold no reference at all
(`where T : unmanaged`): the compiler then rejects the array at the section's
declaration, and per-channel state uses an inline array, whose elements the
read side protects like any other field. State whose length the kit decides
cannot sit in such a section, so it gets a holder of its own instead of the
embedded wrapper the spike also tried. A mutating call through the read side
cannot make a digest stale, because the stored bytes did not change; it loses
the write, which is F3, and one rule at the call site refuses it.

**Favoured: 1d**, with 1a kept only for a setter that has side effects of its
own (`Entry`'s occupancy and its body), and 1c's analyzer reduced to four
rules over declarations and calls, none of which has to enumerate write shapes.

### The design

Three small types, in the project both the rule engine and the world server
reference (`Puck.State`), named here for the build lane to keep or change:

- **A section holder** (`Versioned<T> where T : unmanaged`). It owns one
  section privately. Its read property returns `ref readonly T`. Its write
  property moves the version, notes the slot in the population's move log, and
  returns `ref T`. The body holds each holder in a `readonly` field.
- **An array holder** (`VersionedArray<T> where T : unmanaged`) for state whose
  length the kit decides (the recency tables, the register bookkeeping, the
  tape ring, a route's path). It reads as a `ReadOnlySpan<T>` and writes as a
  `Span<T>` that moves the version.
- **A record holder** for the few sections that carry an immutable reference
  (a program or producer name, a script actor's principal, the admission
  strings and grants). It is the same door without the `unmanaged` constraint;
  rule DOOR003 below holds what the constraint cannot.

The body reaches a section through two private accessors, so a converted line
reads almost as it did:

```csharp
private readonly Versioned<BodyMotion> m_motion;

private ref readonly BodyMotion Motion => ref m_motion.Read;
private ref BodyMotion MotionEdit => ref m_motion.Write;

MotionEdit.Position = next;                       // was: m_position = next
MotionEdit.PositionCarry.Integrate(...);          // was: m_positionAccumulator.Integrate(...)
SteerUpToward(accumulator: ref ClockEdit.UpTurn); // was: ref m_upTurnAccumulator
```

Capture, restore and the sweep refusal's snapshot become whole-section copies
(`scratch.Motion = Motion`, `MotionEdit = scratch.Motion`), which replaces the
field-by-field bodies of `WorldBody.TransferState`,
`WorldBody.CaptureIntegrationResidue`, `WorldBody.ApplyTransferState` and
`WorldBody.ApplyIntegrationResidue`. A field added to a section is captured,
restored and versioned without another line. The image a crossing carries to
another authority stays a projection of the sections
(`WorldBodyTransferState`), because a crossing deliberately carries less than a
checkpoint.

The rigid contacts get a door of the same kind: the body's contact set hands
out read-only slots, and the population takes the writable manifolds once per
rigid step for the bodies it solves, which moves that body's contact version.

The analyzer (`Puck.Analyzers`, beside `OutcomeAnalyzer`):

| Rule | Refuses | Why the compiler cannot |
|---|---|---|
| DOOR001 | A non-`readonly` instance member invoked on a value reached through a holder's read side | The compiler copies the struct and calls the member on the copy |
| DOOR002 | `Unsafe.AsRef`, `MemoryMarshal` and `CollectionsMarshal` applied to a read-side value or span | They exist to defeat `readonly` |
| DOOR003 | A field of a record-holder section whose type is not `unmanaged`, `string`, a `readonly struct` of such fields or an immutable array of them | No constraint expresses deep immutability |
| DOOR004 | A mutable instance field of a state owner (`WorldBody`, `Entry`) that is neither a holder nor labelled derived, scratch or diagnostic | The compiler does not know which fields are state |

Each rule reads a declaration or one call, so it is a closed check: it does
not have to recognise every way a value can be written.

### The invariant, its door and where it stops

**Invariant.** A section's cached digest equals the digest of its current
bytes whenever its version equals the version the digest was taken at.

**Enforced by** the holder: the section is a private field of the holder, the
read side is `ref readonly`, the section type holds no reference, and the
write side moves the version before it returns. The encoder is a static
function over `in T`, so a digest cannot depend on anything outside a section
or the live tail the fold reads every time.

**Where it stops.**

- A mutating call through the read side loses the write (DOOR001). The digest
  stays right.
- A reinterpreting cast bypasses the door on purpose (DOOR002).
- A record-holder section is as safe as its field types (DOOR003).
- A writable reference taken before a fold and written after it would leave an
  old digest under a new version. A `ref` cannot outlive its method, the fold
  runs between steps under `WorldServer.AuthorityGate`, and
  `WorldPopulation.AppendContinuationHash` already refuses to run away from a
  capture point, so this needs a fold called from inside a body method. Nothing
  enforces that it never is; the recompute below finds it.
- State that is in no section is not hashed and not restored (DOOR004 makes
  the author label it; a wrong label shows when a restored run diverges under
  `puck test --reproduce`).
- The parallel tick writes a partition's bodies on one worker, so a version is
  partition-owned. The move log is shared, so each partition would note into a
  list of its own that the ordered merge appends **[inference]**; the fold
  itself already runs outside the tick's writers
  ([the read boundary](engine-job-system.md#the-incremental-state-hash)).

**Strongest case against.** It renames most of a nine-thousand-line type for a
cost only a recording pays. The answer: the rename is mechanical and lands a
section at a time; the field-by-field capture and apply functions (about six
hundred lines that must agree with the codec by hand) are deleted; every step
snapshot gets cheaper; and the versions also remove the rule host's
per-pass scan, which every played tick pays.

## Decision 2: one version or many

**What must be decided.** Whether a body has one version, so only resting
bodies are skipped, or one per section.

**Options.**

| Option | Evidence **[measured]** | Fails at | Closes |
|---|---|---|---|
| 2a. One version per body | 96 of 102 awake bodies change every tick, so they re-encode whole: 857,427 bytes a tick, about a sixth of today | A moving or merely advanced body saves nothing; one clock that ticks while idle (F7) costs the whole body | The 488 parked bodies |
| 2b. A version per section, grouped by how often the fields change | The changed ranges are 24,289 bytes a tick at the finest split measured; a dozen sections come to about 60,000 **[inference]** from the ranges' sizes and change counts | A field placed in the wrong section costs that section's bytes each tick, which the counters show | The parked bodies, and all but a few hundred bytes of every awake one |
| 2c. A version per field | The same bytes as 2b at best | A hundred versions and digests a body; the digest of an eight-byte field costs more than the field | Nothing 2b leaves |

**Favoured: 2b.** The old note said a moving body cannot benefit because its
pose changes every tick. That holds for 2a only: a walking body changes its
pose and motion (about 650 bytes) and leaves its action track and input images
(about 7,000 bytes) alone.

**The sections.** The grouping rule is that fields which change together live
together, and anything that ticks while the body idles gets the smallest
section it can.

| Section | Holds | Bytes encoded | Changes |
|---|---|---|---|
| Pose | Position, yaw, orientation, up, the previous position | About 100 | While moving |
| Motion | Velocities, followers, rate remainders, grounded, hold, attitude, tether, rigid rest state | About 550 | While moving |
| Clocks | The up-turn remainders and frame, sleep idle and since ticks, the advance latch, recency clocks, overlay and rest-hold ticks | About 80 | Every advanced tick today |
| Input | The intent images and their flags, the mapping and epoch, the source | About 2,400 | When input arrives |
| Action | The per-channel timers, values, bits, pending presses, latches | About 4,100 | On a press or a running timer |
| Bookkeeping | Register dirty flags and operands, durable input, script actors, lane recency (array holders) | About 500 | On an action |
| Tape | The scripted tape and a pending continuum | About 10 | On a command |
| Contacts | The rigid contact set, behind its own door | 630 on average | While a rigid body is awake |
| Admission (entry) | Generation, flags, placement, mobility, park, spawn, grants, identity | About 100 | On admission |
| Producer (entry) | Producer, flock and steering runtime | About 370 | On a steering update |
| Cadence (entry) | The autonomy cadence counters | About 50 | Every advanced tick |
| Navigation (entry) | The route and its path | About 30 | On a route change |

The build lane may split or merge these; the per-section counters in
[the steps](#steps) are the judge.

**Clocks are worth a step of their own later.** A count that moves every tick
makes its section change every tick. The rules already avoid this: a deadline
is a cell holding a due tick, never a remainder decremented each tick. The
same form (a due tick, or a start tick, instead of a running count) would let
an advanced but idle body read as unchanged. That is the body step's design,
not this door's; the Clocks and Cadence sections keep its cost to about 130
bytes a body until it is done.

**Strongest case against.** More sections mean more digests to combine per
slot. The slot digest is itself cached and refolds only for a slot the move
log names, so a tick that changes nothing folds no slot.

## Decision 3: the digest cache and its proof

**The cache.** Each holder keeps the digest of its section and the version it
was taken at, so a body that is replaced takes its cache with it and a new
body starts with none. A slot's digest folds its sections' digests in a fixed
order and then the live tail. A segment of
`WorldPopulation.HashSegmentSlots` slots keeps its digest until one of its
slots refolds, as the arena's segments do. The whole-population fold follows
the move log: it refolds the slots the log names, and every slot when it has
fallen behind the log or the population was rebuilt.

**The live tail** is what a body version cannot cover, read on every refold and
small by design:

- **Presence.** Whether the slot holds an active body, as today.
- **The contact-field observation.** It compares the body's observed version
  with the population's. When either population version moved since the last
  fold, every slot digest refolds from its cached section digests; the
  sections do not re-encode.
- **The profile's projection.** A mutable object with its own state
  (`WorldIdentity`), present on seats only.

**The registers leave the whole-population fold.** They are arena rows, the
arena's component already folds them behind `StateArena.RowVersion`, and
encoding them again per body is 4 % of today's bytes. A replay scope's fold
(`WorldPopulation.AppendContinuationHash` over named bodies) and a crossing's
image still read them, because those compare one body's image and not the
arena.

**What proves the cache equals a full fold.**

- **The structure**, above: version unmoved means bytes unmoved, and the
  encoder reads nothing else.
- **The recompute.** `WorldPersistence.AppendStateHashComponent` already takes
  a `recompute` flag; the continuation arm passes it down, and a recompute
  encodes every section of every slot and reads no cached digest. A
  disagreement is reported through `WorldStateHashComposition.DescribeStaleDigest`,
  with the body, the section, the version and both digests, in the words
  `StateArena.CheckDigests` uses for a row. `puck test --reproduce` runs it on
  the restored start and every replayed tick
  (`WorldReplayProbe.VerifiesIncrementalHash`).
- **The sample.** Every other authority hash re-encodes the next slot in
  rotation and compares each of its sections with the cache, and narrates and
  heals a mismatch as `WorldPersistence.StaleDigest` does.
- **A coverage law for the codecs.** For each section type, setting each field
  in turn to a non-default value must change the section's digest, and a
  written section must read back equal. It is a few lines of reflection over
  the section structs, and it is what stops a field added to a section from
  being left out of its encoder.

**Where the proof stops.** The recompute checks the ticks a scenario replays.
Every step runs the go, chess and garden tests; a step that moves locomotion
or input sections also runs `worlds/debug/walkthrough-tests.puck`, whose
routes drive a seat by its controller program. A write shape no scenario runs
is covered by the compiler, not by the recompute.

**Counters** (`world.counters world.tick`, all deterministic): section encodes
and bytes per section, slot refolds, segment refolds, and unchanged encodes
(a section that re-encoded to the digest it already had, which is F7 made
visible and names the writer to fix).

## Decision 4: who else follows the versions

- **The rule host's fact derivation.** `WorldRuleHost.DeriveFacts` compares
  every slot's pose and facts on each of eleven passes a tick (4,528 slot
  visits **[measured]**). It becomes a reader of the move log: it visits the
  slots noted since its place, compares the Pose and Motion versions it last
  saw, and runs its present comparison only for those, so its pose and fact
  versions keep their meaning (they move when the answer changes).
- **The move log is the arena's, lifted.** `StateArena.TryReadMoves` is a ring
  with a sequence, a floor and any number of readers. The population needs the
  same thing keyed by slot, with one addition: a slot noted again before any
  reader has passed its last note is not appended twice, or a body that writes
  a hundred fields a step would flood the ring. One type serves both; the
  arena's readers see fewer duplicates and nothing else.
- **The overlapping components.** `PopulationPose` becomes the fold of the
  Pose digests, `BodyActionState` the fold of the Action digests and one
  declaration digest per kit, and the per-body halves of `Flock` and
  `Navigation` the Producer, Cadence and Navigation digests. Four
  whole-population walks a tick become reads of digests already taken.

## Decision 5: the next recording costs

| Cost | What it does today **[opened]** | Folds into this design? | What it needs |
|---|---|---|---|
| `RuleLatch.AppendStateHash` | Per fold and per compiled rule: hashes the rule's name, finds its state by name, copies and sorts its bindings, and hashes each key-bound binding's key name | The pattern, in its own store | A digest of each rule's name held with its state (`RuleLatch.States` already aligns states with the compiled array); each key name's digest held by ordinal in `CellKeyTable`; a per-rule digest cached by a version on the edge bits. All nine writes of `RuleLatch.RuleState.Held` are already inside `RuleLatch`, so the door is to stop exposing the dictionary and give it three mutators that move the version. It lands with [the two-dictionary merge](open-items.md#refactoring-campaign). The cache is also keyed by the key table's identity, since a replaced table moves ordinals |
| `WorldInputOwner.AppendStateHash` | Per fold and per owner: every retained history record, every switch, source and hold, and a claim bit per record per pattern group | The door types, in its own store | The owner's scalars in a section holder and its history ring and claim tables in array holders, with a digest per owner. Its writers have not been inventoried **[unconfirmed]**; that inventory is the step's first act |
| `WorldBody.AppendActionStateHash` | Per fold and per body: hashes every register definition's name, then the lane runtime | Yes, entirely | The definitions are per kit and never change once compiled: one digest per compiled register file, as the declaration component is named by its definition. The lane runtime is the Action section's digest |

## Determinism

The simulation's values do not change: a section holds the same fields, and
the body step reads and writes them in the same order. The hash's composition
changes twice, each a deliberate correction under rule 4: once when the slot's
bytes become section digests and the registers leave (step D1), and once when
the overlapping components read section digests (step D9). Between those, each
step claims to change no hash, so the cheap check applies: a recording taken
with `puck test --record` on the step before must match under `--compare`.

## Steps

Each is one lane, in this order. Counts are the evidence; a timing is a
request for the lead's quiet window (frozen builds before D1 and after D7, a
headless debug world with a recording armed, the per-tick cost of
`WorldStateHashComposition.HashAuthoritative`).

| Step | Size | Lands | Door | Check |
|---|---|---|---|---|
| D1. Sections as images | Medium | The section structs as capture images built from today's fields; a static encoder per section; the slot digest as a fold of section digests and the live tail; the registers out of the whole-population fold; the per-section counters; the codec coverage law | None yet: every section encodes fresh | `puck test --reproduce --jobs 1` on the go, chess and garden tests; the counters over ticks 300 to 600 with a recording armed read 590 slots and this memo's per-range bytes, less the registers' 218,886 a tick; the law that a captured population's digest equals the live fold |
| D2. The door types | Small | The section, array and record holders; the move log lifted from the arena with the arena on it; DOOR001 to DOOR003; a law that compiles the spike's statements and asserts each diagnostic | The holders, used by nothing in the body yet | The refusal law; the state laws; `puck test --reproduce` on the rule-faults and snapshot-partitions tests with the rule counters unchanged |
| D3. The Action section | Medium | The per-channel arrays as inline arrays in a holder; the bookkeeping arrays in array holders | The Action holder | Hashes equal D1's (`puck test --compare` against D1's `--record`); encoded bytes fall by about 2.5 MB a tick; zero stale digests on every replayed tick; unchanged encodes near zero |
| D4. The Input section | Medium | The intent images and flags | The Input holder | The same, with about 1.4 MB a tick fewer bytes; the walkthrough routes reproduce |
| D5. Pose, Motion, Clocks and Tape | Large | The remaining body fields; the capture, apply and step-snapshot functions as section copies; the crossing's image as a projection | Their holders | The same; the walkthrough routes reproduce; a refused sweep still restores (the sweep refusal laws) |
| D6. Contacts | Medium | The contact set's read view and the population's writable take | The contact set | The same; chess and go reproduce with parked islands waking and parking |
| D7. The entry | Medium | Admission, Producer, Cadence and Navigation sections on `Entry`; the fold following the move log; slot and segment digests cached | Their holders | The same; parked slots encode nothing over the window; encoded bytes a tick are the changed sections' (tens of kilobytes); slot refolds equal the slots the log named |
| D8. Every field labelled | Small | DOOR004 and the labels on what is left outside the sections | The analyzer | The build |
| D9. The followers | Medium | The fact derivation on the move log; the pose, action, flock and navigation components from section digests | The move log | Fact slot visits a tick fall from 4,528 to the moved slots times the passes; rule due, evaluation, firing and skip counts unchanged; reproduce passes (hashes change once here) |
| D10. The rule latch | Medium | Name digests, the edge-bit door and the per-rule digest, with the two-dictionary merge | The latch's holder | Latch and undo laws; the rule-faults tests reproduce; the recompute covers the latch |
| D11. The input owners | Medium | The writer inventory, then the holders and a digest per owner | Their holders | The input laws; the walkthrough routes reproduce |

No step is a stopgap for a later one. D1's static encoders are what makes the
coverage half of the invariant true before any field moves, and every later
step only changes where a section's value is stored.

## Systems considered

| System | Verdict | Why |
|---|---|---|
| `StateArena.RowVersion` and its row digests | The pattern, not the store | Version, cached digest, segment digest, recompute and sample are copied in shape; body fields as arena cells (1f) would journal the body step |
| `StateArena.TryReadMoves` (the move feed) | Reused, lifted to a shared type | The same ring with reader places; one addition, noted under decision 4 |
| `StateArena.CheckDigests`, `WorldPersistence.StaleDigest` | Reused | The sample and its narration are the same check for another cache |
| `WorldStateHashComposition.RecomputeAuthoritative`, `WorldReplayProbe.VerifiesIncrementalHash` | Reused | The existing switch; the continuation arm starts honouring the flag it is already passed |
| The `field` keyword (`StateSection`'s frozen lists, `Entry`'s occupancy) | Kept where it is | Right for a scalar whose setter has a side effect; fails arrays and mutable structs (the spike) |
| `WorldPopulation.DisturbBody`, `WorldBody.WakeIfResting` | Unchanged | They wake a body; they are not a version, and the door does not replace them |
| `OutcomeAnalyzer`, `StaleCaptureAnalyzer` | The home and the precedent for DOOR001 to DOOR004 | Build-error rules over calls and declarations |
| `WorldBodyTransferState`, `WorldBodyIntegrationResidue` | Replaced as storage images; the transfer record kept as the crossing's projection | Two hand-written field lists that must agree with the codec |
| `WorldBody.StepScratch` | Reused, simpler | It already holds two whole records and says a field added to either is covered; sections make that true for all of them |
| `ChannelValues`, `ChannelCeilingValues` | The precedent for inline per-channel storage | Already inline arrays sized by the channel limits |
| `IJournalParticipant` | Considered, not used | It restores side state on a rewind; a body step is not a journal scope |
| A generic versioned holder | None exists | `puck declarations src --kind class,struct,record --name Versioned` and `--name Tracked` return nothing; `puck search` for a `ref readonly` accessor property and for `UnscopedRef` under `src` returns nothing |

## Puck-native options beside the borrowed ones

| Borrowed | Puck-native beside it | Taken |
|---|---|---|
| A smart pointer that flags a component changed on mutable access (Bevy's `Mut<T>`) | The same door, with the digest and the version inside the holder so a replaced body cannot reuse a digest | Both: 1d is the borrowed door |
| Change versions per chunk and component type, moved when write access is requested (Unity's entities) | Versions per body section, grouped by measured change rate, feeding one move log every reader follows | The native grouping |
| Writing only when the value differs (`set_if_neq`) | Counting the encodes that changed nothing, and fixing the writer the count names | The count first; a compare at a writer where it pays |
| A content hash as the cache key (1e) | The version as the key and the content compare as the check | The native one, at scale |

### Sources

- Bevy, `DetectChangesMut`
  (<https://docs.rs/bevy/latest/bevy/ecs/change_detection/trait.DetectChangesMut.html>):
  "Change detection occurs on mutable dereference, and does not consider
  whether or not a value is actually equal"; `set_if_neq` "is useful to ensure
  change detection is only triggered when the underlying value changes".
  Opened.
- Unity, the entities manual's version numbers
  (<https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/systems-version-numbers.html>):
  a chunk's change version per component type is set when the array is
  accessed for writing; "This doesn't guarantee that anything has changed, only
  that it might have changed". Opened.
- The two agree on the trade this memo takes: a door on write access is cheap
  and exact about what did not change, and over-reports what did.

## What would change the answer

- **The unchanged-encode count is high after D5.** If advanced bodies re-encode
  Motion every tick because the step rewrites fields with the values they
  hold, the writers the count names compare before they write, or the holder
  keeps a shadow of that one section and compares before it encodes (1e,
  bounded to the sections that need it).
- **A parked body's sections show encodes.** Something writes to resting
  bodies each tick; the counter names the section, and the writer is fixed as
  the four missed writes were.
- **The review finds a write shape the spike did not try.** It joins the
  refusal law before D3.
- **Inline per-channel arrays cost the step.** A section copy per step snapshot
  is about four kilobytes for the Action section; if a profile shows it, the
  snapshot copies only the sections the step can write.
- **The Deck's memory.** The holders add an object per section per body. If
  the body count grows by orders of magnitude, the holders' storage moves into
  population-owned arrays behind the same two accessors.

## Open questions and their owners

| Question | Owner | What decides it |
|---|---|---|
| How many encodes a write version adds over the bytes that truly changed | D3 to D5's lanes | The unchanged-encode counter over ticks 300 to 600 |
| Whether a rule can read a derived motion fact (the medium flags, the contact count) between a restore and the next step | D8's lane, when it labels them | A restore mid-scenario under `puck test --reproduce` with a rule reading `physics` facts |
| Whether the read-back images belong in the continuation | The input lane, with D4 | Whether any replay claim reads `body.channels` after a restore |
| The up-turn budget integrating on a standing body | The physics lane | The Clocks section's change count after the integrate moves behind the turn test |
| Which key table and rule set changes must drop the latch's cache | D10's lane | The recompute over the rule-faults tests and a `world.reload` |
| The input owners' writers | D11's lane | The inventory |

## Review answers

No review has read this memo yet. Each finding is answered here, one by one,
with the section it changed.
