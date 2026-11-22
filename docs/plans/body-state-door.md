# A body's state behind one write door

The design memo for the storage of a body's simulation continuation. A body's
state moves, section by section, behind holders whose versions say whether it
changed, so the authority hash encodes only what moved and the rule host visits
only the bodies whose facts did. Until a section is behind its holder, every
hash encodes it again for every active body. Each decision names its options,
the evidence, the failure modes each option closes, the favoured option, the
strongest case against it, the invariant and the one place that enforces it.
Steps D1 to D4 are built; [the steps](#steps) list what remains. It supersedes
the design paragraphs of
[the test runner packet's item](open-items.md#test-runner-and-replay).

Labels: **[opened]** means this memo read the symbol at this tree;
**[measured]** means a count this memo's lane read from a headless debug world
(the method is under [what a tick changes](#what-a-tick-changes), the full
readings are in the commit that lands this memo); **[spike]** means a
compile-only prototype this lane ran and deleted; **[inference]** means this
memo's reasoning; **[unconfirmed]** means nobody has checked it. A plan's
citations are leads: recheck the symbol before building from one.

## Summary

- **State moves into sections, and a section has one door.** A body's
  continuation is grouped into value-type sections (pose, motion, clocks, input
  images, the action track and so on). Each lives inside a small holder that
  hands it out two ways: a read that the compiler will not let anyone write
  through, and a write that moves the section's version. The section type is
  constrained to hold no reference, so it cannot contain an array whose
  elements escape the door; per-channel arrays become inline arrays and
  variable-length ones get a holder of their own.
- **The compiler holds the door; an analyzer holds five narrow leftovers.**
  Every write shape the body uses today either compiles only through the write
  door or is refused. What the compiler cannot see is a short, closed list an
  analyzer checks at a declaration or a call.
- **A version says a section may have changed; its bytes say whether it did.**
  The body step writes most sections on every tick whether or not a value
  changes, so a version alone would re-encode nearly a whole body. The holder
  keeps the bytes its digest was taken from and compares before it encodes.
  A section that was written back to the same value costs a memory compare,
  and no writer has to remember to compare first.
- **The digest is cached per section, and nothing cached reads outside one.**
  The encoder of a section is a static function of that section's value. A
  slot's digest folds its sections' digests and nothing else. The three things
  a body version cannot cover (the profile, the field observation and the
  action registers) are folded beside the slots, by content, on every fold.
- **Contacts get the same door through a `Puck.Physics` change.** A manifold's
  persistent slot state becomes a value the body stores; the solver works on
  step scratch and each contact is stored back through its owner's door,
  whoever stepped.
- **Moving bodies benefit, because most of a body does not change.** A
  recorded tick encodes 5.3 MB for 590 bodies. One version per body cuts that
  about sixfold; sections with versions alone cut it about sevenfold; sections
  whose holders compare cut it 70 to 120 times, to between 44,000 and 78,000
  bytes a tick, beside about 740,000 bytes of raw compares.
- **The same slots feed the rule host.** Its fact derivation visits the slots
  that were written instead of comparing every slot on each of eleven passes a
  tick. Every input of a rule-visible answer sits behind a holder, the answer
  is a static function of those holders, and a full comparison checks the
  gated one wherever the hash is checked.
- **The proof is the existing switch.** A recompute folds every section afresh
  and names the body and section whose cached digest disagrees; `puck test
  --reproduce` runs it on every replayed tick. Every other hash samples one
  slot, one segment and one profile in rotation.
- **The rule latch, the input history and the action state names follow the
  same pattern, each in its own store.**

## What exists

### The fold and what it reads

`WorldPopulation.AppendContinuationHash` builds each active slot's checkpoint
entry as a view (`WorldPopulation.EntryCheckpoint`, which calls
`WorldBody.CaptureSections`), digests each section through its own static
encoder (`WorldAuthorityCheckpointCodec.SectionDigest`) and folds the digests
(step D1). The larger sections not yet behind a body holder (pose, motion,
clocks, contacts, and the entry's producer, steering, cadence and navigation)
fold through `WorldContinuationMemo`: each fold copies the slot's image of them
into holders of its own, which compare content and encode only what moved. It
has no version, so it pays a copy and a compare per section per fold that the
holders of D5 to D7 save; as each of those steps moves a section's storage
behind the body's own holder, that section leaves the memo. The rest (admission,
tape, program, motion recency, designations, route) still encode on every fold
**[opened]** (`src/Puck.World.Server/WorldPopulation.Checkpoint.cs:28`):

```csharp
    /// holder hands out, which is cached while the section is unchanged. The larger sections the step writes directly
    /// fold through the slot's <see cref="WorldContinuationMemo"/>, which encodes one only when its content differs
    /// from the content its cached digest was taken from; the rest encode afresh on every fold. A recompute reads
    /// neither cache.</remarks>
```

The view hands out the body's live arrays **[opened]**
(`WorldBody.Table`):

```csharp
    // A table as a section holds it: the live array for a view, a copy for a capture.
    private static ReadOnlyMemory<T> Table<T>(bool alias, T[] live) => ((alias || (live.Length == 0))
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
it. The per-site list (file and line for every row below) is in the commit.

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
- **State outside the body.** Each is answered under
  [decision 3](#decision-3-the-digest-cache-and-its-proof) or below.
  - The rigid contact manifolds are objects the population and the solver
    mutate (`WorldRigidContact`, `FixedRigidManifold`,
    `FixedPairManifoldSlotTable`), reached by reference from outside any body
    method.
  - The action state registers are arena rows (`WorldActionStateLane`).
  - The contact-field observation compares a counter on the body with one on
    the population, and both are process-local
    (`WorldPopulation.ContactFieldVersion`).
  - The profile is a `WorldIdentity` that several bodies can share and that
    changes with no body write (`WorldIdentity.SetMoveSpeed`,
    `WorldIdentity.SetIdentity`, `WorldIdentity.WriteState`).

Fifty-six body members are not captured. Most are compiled configuration
(`FixedMotionTuning`, the collider) or scratch. Two kinds need a decision:

- **Facts a rule reads that no capture carried.** `WorldBody.Facts` answers
  through `WorldBody.FactHolds`, which reads the medium flags, and before D1
  a checkpoint left those for the body's next advance to derive again. A
  sleeping body is not advanced (`WorldBody.TryDeferSleepingAdvance`), and a
  tick's drains run before the population advances, so a restored run
  answered `inmedium` differently from the live one until the body next
  stepped. D1 put the flags in the Motion section, which a checkpoint
  carries and `WorldBody.RestoreSections` writes back.
  [Decision 4](#decision-4-who-else-follows-the-versions) moves every input
  of a rule-visible answer behind a holder.
- **Configuration a rule's answer depends on.** The walkable threshold
  (`WorldBody.SetContactConfiguration`) and the kit's hold table decide
  `holdingunwalkable` and `unsupported`; neither is a write to body state.
  The same decision puts them behind a holder that is versioned and not
  encoded.

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

A slot is 9,021 bytes on average. The 36 ranges the counters split it into,
grouped as the sections of [decision 2](#decision-2-one-version-or-many-and-what-a-moved-version-costs)
group them:

| Part of the encoding (the counters' ranges) | Bytes a slot | Share | Slots a tick in which it changed |
|---|---|---|---|
| Action track: timer ticks 772, timer values 772, previous bits 100, pending presses 872, lane latches and recency 1,644 | 4,160 | 46 % | 2 (the previous bits) |
| Input images that arrive: submitted 266, held 265, producer 266, transfer-held 266, the crossing's held image 265, and the relative remainders, program name and source 12 | 1,340 | 15 % | 0 |
| Input read-backs: the engaged image 265, read-held 265, read-composed 265, read-scalar with the mapping and epoch 275 | 1,070 | 12 % | 11 (engaged, composed and scalar; the same slots) |
| Pose and motion: position and yaw 32, transfer motion 104, rate carries 160, residue motion 106, hold, tether and home 150, rigid state 69 | 621 | 7 % | 21 (pose and residue motion), 10 to 12 (the rest) |
| Rigid contacts | 630 | 7 % | 0 |
| Action state registers (also folded by the arena) | 371 | 4 % | 0 |
| Autonomy steering image (entry) | 266 | 3 % | 7 |
| Script actors | 204 | 2 % | 0 |
| Producer and flock runtime (entry) | 104 | 1 % | 7 (flock), 1 (producer) |
| Entry head: generation, flags, placement, grants, identity | 96 | 1 % | 0 |
| Clocks: the up-turn frame and remainders 53, sleep 17, the advance latch 2, motion recency 4 | 76 | 0.8 % | 80 (the frame), 11 (the latch) |
| Autonomy cadence counters (entry) | 48 | 0.5 % | 28 |
| Navigation 26, tape 9, the profile's absence 1 | 36 | 0.4 % | 0 |

The bytes that changed, at the counters' finest split, were 24,289 a tick,
0.46 % of what the fold encodes. A second window (ticks 2,100 to 2,400) reads
the same, with a rigid body awake in one tick of six and a parked body's bytes
changing six times in three hundred ticks. No body in the headless world has a
profile, so the profile's range is one byte a slot.

**What the step writes whether or not it changes.** About twenty bodies are
moving; about eighty more are advanced every tick and stand still. A version
that moves on write access sees every one of them as changed, because
`WorldBody.Advance` writes these on every advanced tick of a kit body
**[opened]**:

| Section | Written every advanced tick by |
|---|---|
| Pose | `m_previousPosition = m_position` at the top of `WorldBody.Advance` |
| Input read-backs | The engaged intent written in `WorldBody.Advance`; the three read-back images at the end of `WorldBody.NextIntent` |
| Input images | The submitted and producer images and their flags cleared in `WorldBody.NextIntent`; the held image cleared in `WorldBody.Advance` |
| Action | The previous-bit image and every lane timer, each written for every lane (below) |
| Clocks | The motion recency clocks at the top of `WorldBody.ExecuteProgram`; the idle count in `WorldBody.UpdateSleepEligibility`; the turn budget `WorldBody.SteerUpToward` integrates before it knows whether there is a turn to make |
| Motion | The motion program's own integration **[inference]**; `m_affectingSubject = -1` in `WorldBody.Advance` |

The Action writes, as an example of the shape
(`WorldBody.Advance`):

```csharp
        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            ActionEdit.PreviousBit[ordinal] = (intent[ordinal] >= m_channelThresholds[ordinal]);
        }
```

And the turn budget (`WorldBody.SteerUpToward`):

```csharp
        var budget = accumulator.Integrate(
            elapsedTicks: stepTicks,
            ratePerSecond: halfRate
        );
```

Three of the input images also change within a tick and are back to their
default by the fold: a producer's intent is staged before the step and cleared
inside it. A compare taken at the write, or at the end of the step, would
still see them as changed.

**The read-back images are state.** The composed image
(`BodyInputReadbacksSection.ReadComposed`) aims a targeted planar impulse
(`WorldBody.ApplyTargetedEffect`) and answers a rule's `$pointer`
(`WorldRuleHost.ReadPointerRay`); the scalar image
(`BodyInputReadbacksSection.ReadScalar`) answers a rule's channel read
(`WorldBody.ReadChannelScalar`); the held image
(`BodyInputReadbacksSection.ReadHeld`) seeds the held image a crossing
carries (`WorldBody.CaptureTransferState`)
**[opened]**. They stay in the continuation, and the eleven slots a tick in
which they change are part of the true cost.

### The same state, folded more than once

The authoritative hash's components overlap the continuation
(`WorldPersistence.AppendStateHashComponent`) **[opened]**:

- `WorldStateHashComponent.PopulationPose` folds each pose
  (`WorldReplaySnapshot.HashState`).
- `WorldStateHashComponent.BodyActionState` folds each lane's latch and
  recency, hashes every register definition's name per body
  (`WorldBody.AppendActionStateHash`) and folds each profile's records
  (`WorldStateHashComposition.AppendIdentityRecords`).
- `WorldStateHashComponent.Flock` folds the producer and autonomy runtime
  (`WorldPopulation.AppendFlockStateHash`).
- `WorldStateHashComponent.Navigation` folds each route
  (`WorldPopulation.AppendNavigationStateHash`).
- `WorldStateHashComponent.Arena` folds the register values. Before D1 the
  continuation encoded them again per body; a replay scope's fold still does,
  for the bodies it names.
- `WorldStateHashComponent.BodyContinuation` also folds every parked body's
  index and gravity on every fold (`WorldPopulation.AppendSleepStateHash`).

### What already works this way

- **The arena.** `StateArena.RowVersion` moves only when a row's committed
  bytes differ. The versions and the order the rows last moved in are private
  to one type whose only mutators are a bump and a reset (`IndexVersions`),
  so nothing moves a version without its readers hearing of it; any number of
  readers each get the distinct rows that moved since their own place
  (`StateArena.TryReadMoves`), at a constant cost per move and with no log to
  overflow. A row's digest is cached by its version, and a segment's digest
  holds until one of its rows refolds (`StateArena.ComputeHash`).
- **The cross-check.** `StateArena.RecomputeHash` and
  `WorldStateHashComposition.RecomputeAuthoritative` fold with nothing cached,
  `WorldStateHashComposition.DescribeStaleDigest` names the component that
  disagrees, `WorldReplayProbe.VerifiesIncrementalHash` runs it on every tick
  `puck test --reproduce` replays, and `StateArena.CheckDigests` samples one
  row and one segment on every other hash and drops the whole cache on a
  mismatch (`WorldPersistence.StaleDigest`).
- **A digest named by an immutable value.** The declaration component's digest
  is kept beside the definition it was folded from and compared by reference
  (`WorldPersistence.DeclarationDigest`), and an identity's records are
  digested once per snapshot **[opened]**
  (`src/Puck.World.Server/WorldStateHashComposition.Pools.cs:34`):

  ```csharp
    /// <summary>Folds an identity's owned records, caching the immutable snapshot's digest between writes.</summary>
  ```

  Neither needs a version: a value that is replaced and never mutated is its
  own key.
- **A setter that is the only writer.** The population's `Entry` already keeps
  its occupancy behind the `field` keyword **[opened]**
  (`src/Puck.World.Server/WorldPopulation.cs:472`):

  ```csharp
        public bool Active {
            get;
            set {
                if (field == value) {
                    return;
                }
  ```

- **A solver that works on a view and a body that commits.** A rigid body
  hands the solver a view of itself (`WorldBody.RigidSolverHandle`) and writes
  the solved motion back inside its own method (`WorldBody.CommitRigidStep`).
  The contacts are the half of the rigid step that does not work this way.
- **A discontinuity door.** `WorldPopulation.DisturbBody` is the one place a
  teleport, a collider change or a slot letting its body go wakes what rests on
  the body.
- **Versions derived from the value.** The rule host states its own rule
  **[opened]** (`src/Puck.World.Server/WorldRuleHost.FactVersions.cs:7`):

  ```csharp
    // The host sources a rule schedule can name (FactRead.Source). Each one's versions are derived from the value the
    // rule read answers, never recorded by a writer: RefreshFacts compares what each body answers now with what it
    // answered at the last refresh, so no door that moves a body can forget to move its version.
  ```

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
| F10 | A cached combination (a slot or segment digest) that outlives a change to one of its inputs | Silent stale digest that a per-section check passes |
| F11 | A derived answer (a fact version) gated on versions that do not cover everything it reads | Silent missed rule wake, identical in a recording and its replay |
| F12 | A process-local value in an encoded image, or a restore that drops a fixup a copy does not perform | A digest that depends on the process; a restored body that sleeps or wakes unlike the live one |

## Decision 1: the door

**What must be decided.** How the storage is grouped so that a write which
bypasses the version does not compile, arrays, mutable structs and contact
manifolds included, and what forces a new field to be covered.

**Options.**

| Option | Evidence | Cost | Fails at | Closes |
|---|---|---|---|---|
| 1a. A field-backed property per field (the `field` keyword), whose setter compares and moves the version | The `Entry` setters above; the backing field has no name, so nothing else can write it | About a hundred properties; every call that mutates a struct in place rewritten by hand | F2: an element write through an array property compiles **[spike]**. F3 and F8: a mutating call on a struct property compiles and mutates a copy **[spike]**, so each of the 62 existing accumulator calls becomes a silent no-op unless it is rewritten. F4: an ordinary field beside the properties is untracked | F1 for scalars; F7 for free |
| 1b. Generated accessors: a source generator implements partial properties and the capture functions from one annotated declaration | It removes 1a's boilerplate and closes F4 for annotated members | A generator in every build of the server project; the property shape keeps 1a's holes | F2, F3, F8 as 1a | F1, F4 |
| 1c. An analyzer over plain fields: every write to a marked field must pass a helper | `OutcomeAnalyzer` and `StaleCaptureAnalyzer` are precedent for build-error rules | Small to write | It must list every write shape there is: assignment, compound, increment, `ref`, `out`, a `ref` local, a mutating call, an element, an alias local, a span over the array, a callee that writes its argument. The list is open, and the old cache failed on exactly a shape nobody listed | F1 to F3 only as far as the list reaches |
| 1d. Sections of value-type state inside a holder that exposes a read-only reference and a version-moving writable reference | The compiler's own rules for `ref readonly` refuse every write shape through the read side but two **[spike]**; every existing shape compiles unchanged through the write side | The body's reads and writes are renamed section by section (about 870 reads and 600 write sites); inline arrays for the per-channel state; a `Puck.Physics` change for the contacts | F3 through the read side (a lost write, never a stale digest); F9 | F1, F2 and F8 by the compiler; F4 by the static encoder and a rule at the declaration; F3 and F9 by a rule at the call |
| 1e. No door: keep a shadow copy of each section and compare bytes at the fold | Correct by construction, since the cache key is the content | The fold stays proportional to the population, and the state is held twice | Scale: a compare of every byte of every body on every hash | F1 to F6, at that price |
| 1f. Body state as arena rows | The arena already has the door, the log, the digests and the checks | Every field write on the body step's path becomes a journalled arena write; quaternions, intent images and accumulators are not cell kinds | The step's cost; the cell model | F1 to F6 |

**The spikes.** One harness compiled each statement against a section holder, a
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

A second harness compiled the contact and static-function shapes: a body's
contacts as a read-only span of records that each hold an inline array of
sixteen slot states, and an encoder and a derived answer as static interface
members **[spike]**. `body.Contacts` is the array holder's read side from outside the
body.

| Statement | Result |
|---|---|
| `body.Contacts[0].Partner = 1` | Refused (CS8332) |
| `body.Contacts[0].Slots[0].NormalImpulseRaw = 1` | Refused (CS8332) |
| `ref var slot = ref body.Contacts[0].Slots[0]` | Refused (CS8329) |
| `Mutate(ref body.Contacts[0].Slots[0])` | Refused (CS8329) |
| `body.Contacts[0].Slots[0] = default`; `body.Contacts[0] = default` | Refused (CS8331) |
| `Span<SlotState> slots = body.Contacts[0].Slots` | Refused (CS9164) |
| `ref readonly var slot = ref body.Contacts[0].Slots[0]`; a `ReadOnlySpan` of the slots | Compiles |
| A section type that holds an `object` field, as a section holder's argument | Refused (CS8377) |
| A static `Encode(in Pose section, ...)` or `Derive(in Pose a, in Motion b)` that reads an instance field of the body | Refused (CS0120) |
| **`body.Contacts[0].Slots[0].Bump()`, a mutating method** | **Compiles, mutates a copy** (DOOR001) |
| **`MemoryMarshal.GetReference(body.Contacts)` then assign** | **Compiles** (DOOR002) |

The bold rows are why the grouping matters. An array field inside a section
is refused by constraining the section type to hold no reference at all
(`where T : unmanaged`): the compiler then rejects the array at the section's
declaration, and per-channel state uses an inline array, whose elements the
read side protects like any other field. State whose length the kit decides
cannot sit in such a section, so it gets a holder of its own instead of the
embedded wrapper the first spike also tried. A mutating call through the read
side cannot make a digest stale, because the stored bytes did not change; it
loses the write, which is F3, and one rule at the call site refuses it.

**Favoured: 1d**, with 1a kept only for a setter that has side effects of its
own (`Entry`'s occupancy and its body, and the body's profile binding), 1e
kept inside the holder for the sections whose version moved
([decision 2](#decision-2-one-version-or-many-and-what-a-moved-version-costs)),
and 1c's analyzer reduced to five rules over declarations and calls, none of
which has to enumerate write shapes.

### The design

The types live in the project both the rule engine and the world server
reference (`Puck.State`), named here for the build lane to keep or change:

- **A section holder** (`Versioned<T> where T : unmanaged`). It owns one
  section privately. Its read property returns `ref readonly T`. Its write
  property moves the section's version, notes the body's slot (below), and
  returns `ref T`. It also owns the section's digest: the digest is produced
  only by the holder's own method, from a static encoder, so no caller can
  store one. The body holds each holder in a `readonly` field.
- **An array holder** (`VersionedArray<T> where T : unmanaged`) for state whose
  length the kit or the step decides (the recency tables, the register
  bookkeeping, the tape ring, a route's path, the owned contacts). It reads as
  a `ReadOnlySpan<T>` and writes as a `Span<T>` that moves the version.
- **A record holder** for the few sections that carry an immutable reference
  (a program or producer name, a script actor's principal, the admission
  strings and grants, the hold table). It is the same door without the
  `unmanaged` constraint; rule DOOR003 below holds what the constraint cannot.
- **A slot binding and the population's notes.** The holder takes where it
  notes as a constructor argument, never the shared versions themselves. A
  body owns one slot binding that all its holders share;
  `WorldBody.BindPopulation` points it at the body's slot and the
  population's notes, and a body outside a population notes nothing. A write
  notes the slot once per publish: the first write since the last publish
  appends the binding to the list of the slot's partition, and later writes
  see a flag on the binding and append nothing. The partition is the slot
  divided by `WorldPopulation.HashSegmentSlots`, which
  [the parallel tick](engine-job-system.md#the-contract) already fixes as the
  body partition, so a worker that owns a partition touches only that
  partition's list and its own bodies' bindings. A publish drains the lists
  in partition order into the shared versions (`IndexVersions`, the arena's
  row-version type as a shared type keyed by an index) and is their only
  caller. Every reader
  publishes before it reads, under the authority gate, so the shared versions
  keep one writer whatever the tick forks, and a note cannot be lost between a
  write and a read. An entry's holders share a binding for the entry's slot;
  `Entry`'s setters note the slot themselves (`WorldPopulation.BodyEntryChanged`).
- **A derived view** (`Derived<TResult>`). It answers a value computed from
  holders: its read takes the holders themselves and a type whose static
  member computes the answer from their read sides, recomputes when a
  holder's version moved, and otherwise returns what it kept. The function is
  static and receives only what the holders hold, so the versions compared are
  the versions of exactly the values read; there is no second list to keep in
  step. [Decision 4](#decision-4-who-else-follows-the-versions) uses it for
  the facts.

The body reaches a section through two private accessors, so a converted line
reads almost as it did:

```csharp
private readonly Versioned<BodyPose> m_pose;

private ref readonly BodyPose Pose => ref m_pose.Read;
private ref BodyPose PoseEdit => ref m_pose.Write;

PoseEdit.Position = next;                         // was: m_position = next
MotionEdit.PositionCarry.Integrate(...);          // was: m_positionAccumulator.Integrate(...)
SteerUpToward(accumulator: ref ClockEdit.UpTurn); // was: ref m_upTurnAccumulator
```

A write-side reference is used within one statement or handed to one call. It
is never bound to a `ref` local (DOOR005), so a writable reference does not
stay alive across the calls of a method body.

### Capture and restore

A section is captured by copying it and restored by assigning it, and a field
added to a section is captured, restored, versioned and encoded without
another line. Three users capture a body today, and they do not become the
same thing:

- **A checkpoint.** The entry is the sections, encoded by their static
  encoders, and an entry tail for what is not section state: the observation
  bit, the profile's projection and the register values
  ([decision 3](#the-uncached-parts)). `WorldBody.CaptureIntegrationResidue`
  and `WorldBody.ApplyIntegrationResidue` go, with their field lists. Restore
  is copies plus four named fixups, in this order, in one body method:
  1. **Rebuild what is not a section.** The motion program is selected from
     the restored program name. `WorldBody.SetBodyMotionProgram` resets state
     as it switches, so it runs before any copy and no copy has to undo it.
  2. **Copy every section.** Each copy is the last word on its fields; the
     wake that `WorldBody.ApplyTransferState` performs has no counterpart,
     because the Clocks section carries the sleep state the live body had.
  3. **Rebase the process-local values.** The body's field-observation
     counter is set from the entry tail's observation bit and the restored
     population's current counter, as `WorldBody.RestoreSections` rebases it.
  4. **Adopt at the population.** Island membership, the contact referrers and
     the work sets are rebuilt from the restored sections, as
     `WorldPopulation.RestoreSleepingIslands` does.
- **The refused sweep's snapshot.** `WorldBody.CaptureMotion` copies the
  sections into the step scratch and `WorldBody.RestoreMotion` runs fixup 1
  (a step can switch the program) and then assigns them back. It is the same
  body in the same process, so the scratch also keeps the process-local
  counter raw and fixup 3 is a plain assignment. The snapshot keeps no
  hand-written list of motion facts: each is a section field or a labelled
  diagnostic.
- **A crossing.** The image a crossing carries to another authority stays a
  hand-written projection (`WorldBodyTransferState`,
  `WorldBody.ApplyTransferState`), because a crossing deliberately carries
  less than a checkpoint and applies it with side effects of its own (the
  wake, the continuum latch). It reads the sections' read sides and writes
  through their doors.

Two encoded values today are not functions of stored state, and both change:

- **The yaw.** The entry encodes `WorldBody.FixedYaw`, which chooses between
  the stored yaw and the orientation's by the motion program. The Pose
  section stores both and its encoder writes both.
- **The field observation.** The body's counter is process-local, so it is not
  in a section and no encoder sees it. The image carries one bit, computed at
  the fold or the capture.

### The contacts

**What exists.** A body owns a list of `WorldRigidContact` objects, each with a
`FixedRigidManifold` whose `FixedPairManifoldSlotTable` holds sixteen slots.
The population asks a body for a contact and writes the manifold it gets back
(`WorldBody.RigidContact`), and the solver writes every manifold it is handed
(`FixedRigidWorld.Step`). Of two rigid bodies the lower index owns the pair
(`WorldRigidContacts.Owner`), and the pair loop writes the owner's manifold
when only the partner stepped (`WorldPopulation.SolveRigidContacts`)
**[opened]**.

An unprepared rigid owner takes that path when a carried body owns a pair
with a higher-index free rigid body. Carried bodies enter the contact set as
static solver sides, and their owned manifolds join the solve without putting
them in the integration roster (`WorldPopulation.ResolveDynamicContacts`,
`WorldPopulation.SolveRigidContacts`; the server law
`ACarriedBodyPushesWithItsVelocityThroughTheSolveInEitherIndexOrder` exercises
both owner orders). A body that teleports or leaves its slot also releases the
contacts other bodies own with it (`WorldPopulation.ForgetRigidContacts`), and
a restore writes slots through the same objects
(`WorldPopulation.ApplyRigidContactImages`).

So a contact's state is written from outside its owner, for an owner that may
not be stepping, through an object graph `ref readonly` does not reach. A slot
also mixes two kinds of field: nine that persist and a checkpoint carries
(occupancy, source and feature identity, both anchors, the normal, normal
and friction impulses, and the idle count), and eighteen the
solver derives again each step. Whether a contact was touched this step is a
third kind: a flag on the object that the free and carried owners' release passes
clear (`WorldPopulation.ReleaseUntouchedRigidContacts`) and that no
checkpoint carries.

**Options.**

| Option | Cost | Fails at | Closes |
|---|---|---|---|
| C1. Keep the objects; the population takes the writable manifolds once a step for the bodies it solves | None | The unprepared owner above, and a restore's or a release's write to another body's contact: the door is a convention | Nothing the compiler holds |
| C2. Store whole slots as values behind the body's holder; the solver writes them in place through an accessor it calls per pass | A solver generic over a slot source; a door call per manifold per pass | The body stores the solver's eighteen derived fields for good, and the encoder must skip them, which is a list of exemptions where an omitted field hides (F4) | F5 for contacts, by the compiler |
| C3. Split the slot. The body stores the persistent state as values behind its holder; the solver works on step scratch; each associated contact is loaded from its owner's read side and stored back through its owner's door | A `Puck.Physics` change: the state struct, a step that takes spans, load and store; two copies of sixteen small structs per associated contact per step | A store that is forgotten loses a warm start (F3, visible in the solve), never a digest | F5 for contacts, by the compiler; no exemption list, because the stored type is exactly the continuation |

**Favoured: C3.** It is the shape the rigid body's own motion already has (a
view in, a commit back), and it leaves the solver's layout free to change.

**The design.**

- **`Puck.Physics`.** The nine persistent fields become one public unmanaged
  struct (the slot state), and sixteen of them an inline array (the manifold
  state). `FixedPairManifoldSlot` holds the slot state as one field beside the
  per-step terms, so loading and storing a slot is one assignment and a field
  added to the state is carried by it. `FixedRigidManifold` and
  `FixedPairManifoldSlotTable` stop being objects a caller keeps:
  `FixedRigidWorld.Step` takes the step's manifolds as a span of header
  values (the two solver bodies, the pair's coefficients, the readings) and
  their slots as one span of sixteen per manifold, and
  `FixedManifoldSlotCore.Associate` takes a span. Association reads only the
  persistent state, as `FixedManifoldSlotCore`'s own summary says, so a table
  loaded from a body associates exactly as the kept one does.
- **The body.** `WorldRigidContact` goes. The body's owned contacts are
  records of a partner and a manifold state, ascending by partner, in an
  array holder. From outside the body they read as a `ReadOnlySpan`. Finding
  or creating a contact, storing one, releasing one and restoring the set are
  body methods that write through the holder.
- **The population.** The step keeps a list of the contacts it associated
  (owner, partner, the manifold's place in the step's spans) and the spans
  themselves, both scratch. For every pair it associates, it loads the
  owner's state into the next scratch manifold, associates, and after the
  solve stores the state back through the owner's method, whether or not the
  owner was solved. Whether a contact was touched this step is the step's
  list, not a flag the body keeps, and the readers that ask what a slot did
  this step (`WorldPopulation.RigidSupported`,
  `WorldPopulation.LoadedSurfacePoint`,
  `WorldPopulation.ReleaseUntouchedRigidContacts`) read the step's list and
  spans.
- **The image.** `WorldRigidContactSlotImage` and the copy that builds it
  (`WorldBody.CopyRigidContactImages`) go: the Contacts section is its own
  image, and its encoder writes the occupied slots.

**Invariant.** A body's contact state changes only through that body's
holder. **Enforced by** the compiler: the records are unmanaged values inside
an array holder, the holder's storage is private, the read side is a
`ReadOnlySpan`, and no type the body hands out holds a reference into it. The
second spike's refused rows are the shapes; a new caller cannot write a
contact without calling a body method, and every such method notes its own
body. **Where it stops.** The solver's scratch is a copy, so a store that is
skipped loses that step's impulses for the next warm start: a visible change
in the solve, and a law compares the stored state with the solved scratch for
every associated contact (step D6). **The failing sequence** the review
gave, an unprepared owner whose pair is hit, ends in a store through the
owner's method, which moves the owner's Contacts version and notes its slot;
the release of another owner's contact, which is reachable today, ends the
same way.

### The analyzer

`Puck.Analyzers`, beside `OutcomeAnalyzer`:

| Rule | Refuses | Why the compiler cannot |
|---|---|---|
| DOOR001 | A non-`readonly` instance member invoked on a value reached through a holder's read side | The compiler copies the struct and calls the member on the copy |
| DOOR002 | A call to a member that turns a read-only reference into a writable one: `Unsafe.AsRef`, `MemoryMarshal.GetReference`, `MemoryMarshal.CreateSpan`, `MemoryMarshal.GetArrayDataReference`, `MemoryMarshal.AsMemory`, `ImmutableCollectionsMarshal.AsArray` and `ImmutableCollectionsMarshal.AsImmutableArray`. In a project that declares a holder or a section type, every such call outside the holder types themselves; in any other project, every such call whose type argument is a section type, a field type of one, or an open type parameter | They exist to defeat `readonly` |
| DOOR003 | A field of a record-holder section whose type is not `unmanaged`, `string`, a `readonly struct` of such fields or an immutable array of them | No constraint expresses deep immutability |
| DOOR004 | A mutable instance field of a state owner (`WorldBody`, `Entry`) that is neither a holder nor labelled with one of: derived, scratch, diagnostic, configuration, process-local, another store's | The compiler does not know which fields are state |
| DOOR005 | A `ref` local or `ref` field initialised from a holder's write side | A writable reference that outlives its statement can be written after a reader has run |

DOOR002 reads the member called and its type arguments, never where a value
came from, so it needs no dataflow. `Puck.State` and its sibling projects,
`Puck.Physics` and `Puck.World.Server` call none of the listed members today
**[opened]**, so the rule lands with an empty exception list.
`Puck.World.Schema` calls one, once, over an array of offsets a placement deal
has just built; that project declares no section type, so the call is outside
the rule. `CollectionsMarshal` is not listed: a holder stores no `List<T>`, so
that type cannot reach a section, and the server's existing uses stay.

Each rule reads a declaration or one call, so it is a closed check: it does
not have to recognise every way a value can be written.

### The invariant, its door and where it stops

**Invariant.** The digest a holder hands out equals the digest of the
section's current bytes.

**Enforced by** the holder. The section is a private field of the holder; the
read side is `ref readonly`; the section type holds no reference; the write
side moves the version before it returns; and the digest is produced only by
the holder's own method, which encodes afresh unless the version is the one
the digest was taken at, or the bytes equal the bytes the digest was taken
from. The encoder is a static function over `in T`, so a digest cannot depend
on anything outside its section.

**Where it stops.**

- A mutating call through the read side loses the write (DOOR001). The digest
  stays right.
- A reinterpreting cast bypasses the door on purpose (DOOR002).
- A record-holder section is as safe as its field types (DOOR003).
- A writable reference written after a fold would leave an old digest under a
  current version. DOOR005 bounds a write-side reference to one statement or
  one call, so this needs a callee that is handed a `ref` to section state and
  reaches a fold before it writes. The population counts its open mutating
  phases (the advance loops, the contact passes, the tether and carry passes),
  and the fold refuses to run while the count is not zero; that replaces the
  pending-output test of `WorldPopulation.RequireCapturePoint`, which a fold
  early in a step would pass. Nothing refuses a callee outside a phase that
  holds such a reference across a reader; the recompute and the fact
  cross-check find it on any replayed tick it happens in.
- State that is in no section is not hashed and not restored. DOOR004 makes
  the author label it, and step D8's law fails for a wrong label.
- A value another store owns is that store's to version: the registers by the
  arena, the profile by its own content, the field observation by the
  population. [Decision 3](#the-uncached-parts) folds each without caching
  it under a body version.

**Strongest case against.** It renames most of a nine-thousand-line type and
changes a solver's interface for a cost only a recording pays. The answer: the
rename is mechanical and lands a section at a time; the residue's capture and
apply functions and the contact image (hand-written lists that must agree with
the codec) are deleted; and the same notes remove the rule host's per-pass
scan, which every played tick pays.

## Decision 2: one version or many, and what a moved version costs

**What must be decided.** Whether a body has one version or one per section,
and, since the step writes most sections on every tick, what a moved version
costs.

**Options.** Bytes are a recorded tick's, for the 590 slots of
[the measured window](#what-a-tick-changes).

| Option | Bytes encoded a tick | Fails at | Closes |
|---|---|---|---|
| Today | 5,322,564 **[measured]** | | |
| 2a. One version per body, moved on write access | 857,427 **[measured]**: every awake kit body, whole. About a sixth | A moving or merely advanced body saves nothing | The 488 parked bodies |
| 2b. A version per section, moved on write access, and nothing else | About 745,000 **[inference]**: 102 awake bodies times the 7,267 bytes of the six sections the step writes every tick, and the entry sections on change. About a seventh | The step's unconditional writes (F7): Pose, Motion, Clocks, both Input sections and Action move for every advanced body, so sections buy little over 2a | The parked bodies; Bookkeeping, Tape and Contacts of the awake ones |
| 2c. 2b, and every unconditional writer compares before it writes | About 44,000 to 78,000, if every such writer is found | A list of writers kept by hand: a new unconditional write silently costs its section again, and a value staged before the step and cleared inside it moves a version twice with nothing changed at the fold | What 2b closes, while the list holds |
| 2d. 2b, and the holder compares a moved section with the bytes its digest was taken from before it encodes | 43,816 to about 78,000 **[inference]**, and about 741,000 bytes of raw compare | The awake bodies' sections are held twice; the compare is proportional to what was written, not to what changed | F7 for the encode, whatever is written and by whom |
| 2e. A version per field | The same bytes as 2d at best | A hundred versions and digests a body; the digest of an eight-byte field costs more than the field | Nothing 2d leaves |

**The arithmetic for 2d.** A section encodes when its bytes changed since its
digest. From the measured ranges, a tick's encodes are: Pose and Motion 621 ×
21.3 slots, Clocks 76 × 79.5, the Input read-backs 1,070 × 11.0, Action 4,160
× 2.1, Producer 104 × 7, Steering 266 × 7 and Cadence 48 × 28: 43,816 bytes,
121 times fewer than today. That takes the slots in which a section's ranges
changed to be the same slots, which the equal counts suggest; if they are all
different slots the sum is about 78,000, 68 times fewer. The floor is the
24,289 bytes that changed at the finest split.

**Favoured: 2d.** The old note said a moving body cannot benefit because its
pose changes every tick. That holds for 2a. A walking body changes about 700
bytes of pose, motion and clocks and leaves its action track and most of its
input images alone; what stops 2b from showing it is that the step rewrites
those sections with the values they hold.

**The content check, inside the holder.** The version is a hint that a
section may have changed. When a fold asks a holder for its digest:

1. The version is the one the digest was taken at: the cached digest.
2. Otherwise the holder compares the section's raw bytes with the bytes it
   kept from its last encode. Equal: the cached digest stands for the current
   version, and the counter of filtered compares moves.
3. Otherwise it encodes, keeps the digest, and keeps the bytes.

The bytes are kept from a section's second encode on, so a body that never
moves again holds nothing twice: the parked bodies cost no memory, and the
awake bodies of the debug world about 740 KB. The compare is of raw memory, so
a difference in padding can only cause an encode, never suppress one; equal
bytes are an equal value. An array holder compares length and elements; a
record holder compares fields, where an immutable array compares by
reference. This is option 1e, bounded to the sections whose version moved.

**Invariant.** A cached digest is handed out only under a version it was
taken at, or over bytes equal to the bytes it was taken from. **Enforced by**
the holder: the kept bytes and the cached digest are private to it and written
together in step 3, the only place either is written. **Where it stops.** The
version test of step 1 trusts the door; the checked read below does not.

**The sections.** The grouping rule is that fields which change together live
together, and anything that ticks while the body idles gets the smallest
section it can. Bytes are the measured ranges'; the Pose and Motion split is
**[inference]**.

| Section | Holds | Bytes encoded | Slots a tick whose bytes changed | Written every advanced tick today |
|---|---|---|---|---|
| Pose | Position, both yaws (the stored yaw and the orientation), up, the previous position, scale | About 110 of the 621 | 21 | Yes |
| Motion | Velocities, followers, rate remainders, grounded, hold, attitude, tether, rigid rest state, the medium flags, the affecting subject | About 510 of the 621 | 21 | Yes |
| Clocks | The up-turn remainders and frame, sleep idle and since ticks, island membership, the advance latch, the motion recency clocks, overlay and rest-hold ticks | 76 | 80 | Yes |
| Input images | The submitted, held, producer and transfer-held images and their flags, the crossing's held image, the source, the mapping and epoch | 1,340 | 0 | Yes |
| Input read-backs | The engaged image and the read-held, read-composed and read-scalar images | 1,070 | 11 | Yes |
| Action | The per-channel timers, values, bits and pending presses; each lane's latch, held facts and recency | 4,160 | 2 | Yes |
| Bookkeeping | Register dirty flags and operands (an array holder); durable input and script actors (record holders over immutable tables, because their rows hold a writer id and a principal) | 204 and the bookkeeping beside the registers | 0 | No |
| Tape and program | The scripted tape, a pending continuum, the motion program's name | About 10 | 0 | No |
| Contacts | The owned contact records, behind the array holder | 630 on average | 0 while parked | No |
| Admission (entry) | Generation, flags, kit index and selections, placement, mobility, park, spawn, travel turn, identity; the grant templates and revoked keys as immutable sorted arrays; the designations in an array holder | 96 | 0 | No |
| Look (entry) | Body colour, catalog rig, look index: captured and restored, not folded, as today | Not folded | | No |
| Producer (entry) | Producer and flock runtime | 104 | 7 | **[unconfirmed]** |
| Steering (entry) | The autonomy steering image | 266 | 7 | **[unconfirmed]** |
| Cadence (entry) | The autonomy cadence counters | 48 | 28 | **[unconfirmed]** |
| Navigation (entry) | The route and its path | 26 | 0 | No |

One more section is not continuation. The **configuration** section holds
what a derived answer reads of the compiled kit and collision (the walkable
threshold, the hold table, whether the kit is rigid): versioned, never
encoded, because the declaration component covers what it is compiled from.

A kit with no action track or input (a rigid body) may leave those holders
absent, which the encoder writes as one byte; the build lane decides it with
the Deck's memory in view.

The build lane may split or merge sections; the per-section counters in
[the steps](#steps) are the judge. Two splits this table already takes from
the measurements: the input images in two (the eleven slots a tick whose
read-backs change then encode 1,070 bytes, not 2,410), and steering apart from
the producer.

**Writing only on change is worth a step of its own, after the rest.** With
the content check the encode no longer depends on it. What it still buys:

- **The compares.** About 741,000 bytes a tick fall toward the sections that
  changed.
- **The slots readers visit.** A standing body that writes nothing is not
  noted, so the fold and the fact derivation do not visit it: about 102 slots
  a tick fall toward the twenty that move.
- **The kept bytes.** A section that is never written keeps none.

It is step D12, sized Medium, in the physics lane: the writers in
[the table of unconditional writes](#what-a-tick-changes) compare before they
write (about a dozen sites for Action and the two Input sections), the turn
budget integrates behind the turn test, and a count that moves every tick
takes the form the rules already use for a deadline (a due tick, not a
remainder decremented each tick), so an advanced but idle body reads as
unchanged. Moving the integrate changes a turn's first-tick budget, an output
change rule 4 allows. The filtered-compare counter names what is left to fix
and is D12's measure; nothing before D12 waits on it.

**Strongest case against.** The compare is work proportional to what was
written. It is a memory compare of sections the step has just touched, it
runs only on a tick that takes a hash, and D12 removes most of it; the
alternative, 2c, makes the payoff depend on every future writer.

## Decision 3: the digest cache and its proof

**The cache.** Each holder keeps the digest of its section, the version it was
taken at and, once it has moved, the bytes it was taken from, so a body that
is replaced takes its cache with it and a new body starts with none. A slot's
digest folds its sections' digests in a fixed order and nothing else. The
population keeps one digest per slot and one per segment of
`WorldPopulation.HashSegmentSlots` slots. The whole-population fold is a
reader of the slot notes: it publishes them, refolds the slots that were
written since its place and marks their segments, and refolds every slot when
the versions were reset (a rebuilt population). A slot that takes or loses its
body is noted by `Entry`'s own setters. Every cache carries the population's
cache epoch, and healing is one increment of it.

### The uncached parts

Three things are part of a body's continuation and cannot be keyed by a body
version. None of them enters a slot digest, so no cached digest depends on
them; each is folded beside the segments, on every fold, by a rule that has no
version to forget.

- **The field observation.** The image keeps whether a resting body has seen
  the contact field as it now stands; the counters behind that are
  process-local. An awake body's observation is current by construction, the
  parked rigid bodies observe together through the population's own
  observation, and an asleep kit body compares its own counter
  (`WorldPopulation.EntryCheckpoint` states the three cases). The fold appends
  the population's one bit, then, for each asleep kit body in the population's
  awake work set, its slot and its bit. That set is the one the step itself
  walks every tick to defer those bodies (`WorldBody.TryDeferSleepingAdvance`),
  so the fold adds no walk the tick does not already make, and it encodes
  nothing.
- **The profile.** `WorldBody.Profile`'s setter becomes the one writer of the
  binding and keeps a set of profiled slots on the population, as `Entry`'s
  setters keep the work sets. For each profiled slot the fold takes
  `WorldIdentity.Project`, compares it with the projection its cached digest
  was taken from (the two rates by value, the strings by value, the records
  and the facts row by reference), and encodes it only when they differ. The
  key is the content, so an identity edit that writes no body
  (`WorldIdentity.SetMoveSpeed`), and one identity shared by several seats,
  need nothing from the door: the next fold sees a different projection. An
  identity whose document was replaced selects its records again, so the
  reference differs and the projection encodes once more even when the
  records are the same; that is the safe direction. The projection folds the
  records, so `WorldStateHashComponent.BodyActionState` stops folding them a
  second time (step D9).
- **The registers.** They are arena rows, the arena's component already folds
  them behind `StateArena.RowVersion`, and encoding them again per body is 4 %
  of today's bytes. They leave the whole-population fold. A replay scope's
  fold and a crossing's image still read them, because those compare one
  body's image and not the arena.

**Invariant.** Nothing cached under a body version reads a value outside a
holder. **Enforced by** the slot digest's signature: it is a static function
of the section digests. **Where it stops.** The profile's key trusts that a
projected reference is replaced and never mutated in place, which
`WorldIdentity.WriteState` does and `WorldStateHashComposition.AppendIdentityRecords`
already relies on; an in-place mutation would be missed by the key and found
by the sample and the recompute below.

### What proves the cache equals a full fold

- **The structure**, above: a digest is handed out only under its version or
  over its bytes, and the encoder reads nothing else.
- **The checked read.** `WorldReplayProbe.VerifiesIncrementalHash` puts the
  population in a checked mode. While it holds, every holder's digest read
  also encodes afresh and compares with what it hands out, and a difference is
  reported with the body, the section, the version and both digests, in the
  words `StateArena.CheckDigests` uses for a row. The check is in the holder's
  read, so it covers every reader of a section digest, not one fold: the
  whole-population fold, and the replay scope's fold
  (`WorldScopedReplayRuntime.Hash`), which reads section digests, never a slot
  or segment digest, and encodes its bodies' tails afresh.
- **The recompute.** `WorldPersistence.AppendStateHashComponent` already takes
  a `recompute` flag; the continuation arm starts honouring it. A recompute
  folds every slot and segment from fresh section digests and every profile
  from a fresh encode, and reads no cache. `puck test --reproduce` compares it
  with the incremental hash on the restored start and every replayed tick.
- **The sample.** Every other authority hash checks, in rotation: one slot
  (each section re-encoded against its cache, and the slot's digest refolded
  against the population's), one segment (refolded from its slots' digests
  against its own), and one profiled slot (re-encoded against its cache). A
  mismatch at any level is narrated once as `WorldPersistence.StaleDigest`
  does and healed by moving the cache epoch, which drops every section, slot,
  segment and profile digest at once; the hash is then taken afresh.
- **A coverage law for the codecs.** For each section type, setting each field
  in turn to a non-default value must change the section's digest, and a
  written section must read back equal. It is a few lines of reflection over
  the section structs, and it is what stops a field added to a section from
  being left out of its encoder. It has no exemption list: a process-local or
  derived value is not in a section.

**Where the proof stops.** The recompute checks the ticks a scenario replays.
Every step runs the go, chess and garden tests; a step that moves locomotion
or input sections also runs `worlds/debug/walkthrough-tests.puck`, whose
routes drive a seat by its controller program. A write shape no scenario runs
is covered by the compiler, not by the recompute. The sample bounds a stale
section, slot or segment to one rotation of the population's capacity, and a
stale profile to one rotation of the profiled slots.

**Counters** (`world.counters world.tick`, all deterministic): section encodes
and bytes per section; compared bytes and filtered compares per section (a
moved version over unchanged bytes, which is F7 made visible and names the
writer for D12); unchanged encodes (a section that encoded to the digest it
had, which should be a body's first move only); slot refolds; segment
refolds; contact stores.

## Decision 4: who else follows the versions

### The rule host's fact derivation

`WorldRuleHost.DeriveFacts` compares every slot's liveness, pose and facts on
each of eleven passes a tick (4,528 slot visits **[measured]**). Its versions
are derived from the value, and that stays.

**Options.**

| Option | Fails at | Closes |
|---|---|---|
| R1. Keep the full scan | The cost, on every played tick | F11, by having no gate |
| R2. Visit the moved slots and run the comparison only where the Pose or Motion version moved | F11: the facts also read the medium flags, the walkable threshold and the hold table. A retune that flips `holdingunwalkable` on a standing body moves neither version, the rule never wakes, and a replay agrees with the recording | The cost |
| R3. Make every input of a rule-visible answer a holder's value, compute the answer by a static function of those holders, visit every slot that any holder noted, and check the gated pass against the full scan wherever the hash is checked | A mutating reader inside a step (below) | F11, by the compiler for the inputs and by the cross-check for the gate |

**Favoured: R3.**

- **The inputs move behind holders.** The medium flags and the affecting
  subject join the Motion section (a checkpoint then carries them, which
  closes the restore gap under [every writer](#every-writer)). The walkable
  threshold, the hold table and the kit's rigid flag move into the
  configuration section, so `WorldBody.SetContactConfiguration` and a retune
  write through a door and note the slot.
- **The answer is a static function.** `WorldBody.FactHolds` becomes a static
  function of the Motion and configuration sections' values, the one
  predicate the action gates, the snapshot and the rule host all call.
  `WorldBody.Facts` answers through a derived view over those two holders.
  The pose the rule host compares is built by a static function of the Pose
  section. A static function cannot read a body field (CS0120 **[spike]**),
  and the derived view's read takes holders and nothing else, so an input
  that is not behind a door cannot reach a rule-visible answer.
- **The gate is the slot, not a section.** The derivation publishes the notes
  and visits every slot noted since its place, then runs today's comparisons
  for those slots. It does not ask which section moved: any holder of the
  body or its entry notes the slot. The fact and pose versions keep their
  meaning; they move when the answer changes.
- **The cross-check.** Under the recompute flag, after each derivation, the
  host runs today's full comparison over every slot and fails with the slot
  and the source when a slot the gated pass skipped answers differently from
  what it last answered. Every other derivation samples one skipped slot in
  rotation, narrates a mismatch once and heals by taking every slot as moved.
  This is the check the reproduce run was missing: a gated miss is the same in
  a recording and its replay, and a comparison inside one run sees it.
- **Inside a step.** A rule read can reach the derivation from inside a body's
  step, where a write may be in flight. While the population's count of open
  phases is not zero, the derivation runs the full comparison and keeps its
  place, so it trusts no version there.

**Invariant.** A slot the derivation skips answers what it answered at the
last derivation. **Enforced by** the derived view's read (holders in, a
static function over their read sides) and the holders' notes. **Where it
stops.** At a static function that reads mutable static state, which no rule
here refuses, and at the open-phase case above, which the full comparison
covers.

**The failing sequence** the review gave: a body holds a surface and writes
no Pose or Motion; a `collision` edit moves the walkable threshold. The write
goes through the configuration holder, the slot is noted, the next derivation
visits it, the derived view recomputes because the configuration version
moved, the answer differs, and the fact version moves.

### The slot versions are the arena's type, shared

`IndexVersions` is the version half of a door: a version per index, private
storage, two mutators, and an order threaded through the indices so each
reader is handed every index that moved since its place, once, however often
it moved. Its writers are single-threaded with one another, which is why
holders never call it: they note into partition-owned lists
(`SlotBinding`, `SlotNotes`), and the publish is its one writer
([the design](#the-design)). It was internal to the arena and named for rows;
D2 made it a shared type keyed by an index, with the arena on it, and a
population takes one instance keyed by slot through its notes. Nothing was
added to it.

### The overlapping components

`PopulationPose` becomes the fold of the Pose digests, `BodyActionState` the
fold of the Action digests (lane latch and recency are in that section) and
one declaration digest per kit, and the per-body halves of `Flock` and
`Navigation` the Producer, Steering, Cadence and Navigation digests. Four
whole-population walks a tick become reads of digests already taken. The
identity records leave `BodyActionState` because the profile part of the
continuation folds them.

## Decision 5: the next recording costs

| Cost | What it does today **[opened]** | Folds into this design? | What it needs |
|---|---|---|---|
| `RuleLatch.AppendStateHash` | Per fold and per compiled rule: hashes the rule's name, finds its state by name, copies and sorts its bindings, and hashes each key-bound binding's key name | The pattern, in its own store | A digest of each rule's name held with its state (`RuleLatch.States` already aligns states with the compiled array); each key name's digest held by ordinal in `CellKeyTable`; a per-rule digest cached by a version on the edge bits. The binding table now holds one entry per binding, with an optional held bit apart from its unhashed schedule cache. `RuleLatch` keeps bit writes behind its journal and lifecycle doors; `RuleState.IsHeld` exposes only a read. D10 adds versioned digests to that door. The cache is also keyed by the key table's identity, since a replaced table moves ordinals |
| `WorldInputOwner.AppendStateHash` | Per fold and per owner: every retained history record, every switch, source and hold, and a claim bit per record per pattern group | The door types, in its own store | The owner's scalars in a section holder and its history ring and claim tables in array holders, with a digest per owner. Its writers have not been inventoried **[unconfirmed]**; that inventory is the step's first act |
| `WorldBody.AppendActionStateHash` | Per fold and per body: hashes every register definition's name, then the lane runtime | Yes, entirely | The definitions are per kit and never change once compiled: one digest per compiled register file, as the declaration component is named by its definition. The lane runtime is the Action section's digest |
| `WorldPopulation.AppendSleepStateHash` | Per fold: every parked body's index and solved gravity, and the event set | The pattern, in the population | Island membership has two writers (`WorldPopulation.ParkIslandMember`, `WorldPopulation.WakeIsland`); a digest per island kept by those two, folded by root. It lands with step D7 |

## Determinism

The simulation's values do not change in steps D1 to D11, with one named
exception: a restored body carries its medium flags (D1). A contact's touched
flag becomes the step's scratch at D6; it could outlive its step only for an
owner that is associated and not solved, which
[the contacts](#the-contacts) reads as unreachable today, and D6's counter of
stores by whether the owner stepped confirms it. The hash's
composition changes twice, each a deliberate correction under rule 4: at D1
(the slot's bytes become section digests; the registers and the crossing's
derived held image leave; the stored yaw, the medium flags and the travel
turn join; the observation and the profile fold beside the segments), and at
D9 (the overlapping components read section digests).
Between those, each step claims to change no hash, so the cheap check
applies: a recording taken with `puck test --record` on the step before must
match under `--compare`. D12 changes a turn's first tick and says so.

## Steps

Each is one lane, in this order. Counts are the evidence; a timing is a
request for the lead's quiet window (frozen builds before D1 and after D7, a
headless debug world with a recording armed, the per-tick cost of
`WorldStateHashComposition.HashAuthoritative`). The byte figures are a tick's,
over ticks 300 to 600 of the headless debug world with a recording armed.

| Step | Size | Lands | Door | Check |
|---|---|---|---|---|
| D5. Pose, Motion, Clocks, Tape and the configuration | Large | The remaining body fields; the facts as a static function behind a derived view, with the walkable threshold and hold table in the configuration holder; the checkpoint restore and the step snapshot as section copies with the four fixups; the crossing's image as a projection; the residue pair deleted | Their holders | The same; these ranges' 416,540 bytes fall to about 19,000; the walkthrough routes reproduce; a refused sweep still restores (the sweep refusal laws); a law that capture, a contact-field rebuild and a second capture give one digest; the hall tests reproduce |
| D6. Contacts | Medium | In `Puck.Physics`: the slot state struct, `FixedRigidWorld.Step` over spans, association over a span, the manifold and slot-table objects gone. In the server: contact records in the body's array holder, `WorldRigidContact` and the slot image gone, the step's association list and scratch | The Contacts holder | The same; the physics laws for the rigid solver; a law that after a rigid step every associated contact's stored state equals its solved scratch, and that every body whose stored contacts changed was noted; the contact-store counter split by whether the owner stepped; chess and go reproduce with parked islands waking and parking |
| D7. The entry and the reader | Medium | The Admission, Producer, Steering, Cadence and Navigation sections on `Entry`, with the grants and revoked keys as immutable arrays; the profile binding's setter and the profiled set; the fold as a reader of the notes; slot and segment digests cached under the cache epoch; the three-level sample; the open-phase count in place of the pending-output test; the island digests | Their holders | The same; parked slots encode nothing over the window; encoded bytes a tick are between 44,000 and 78,000; slot refolds equal the slots noted; a law that a fold called inside an open phase throws |
| D8. Every field labelled | Small | DOOR004 and the labels on what is left outside the sections | The analyzer | The build; a law that runs a scripted scene twice, overwriting every scratch and diagnostic field of every body with other values at each capture point in one run, and requires equal hashes; a law that a derived or configuration field of a restored body equals the live body's at the capture point |
| D9. The followers | Medium | The fact derivation on the notes with its full-scan cross-check and sample; the pose, action, flock and navigation components from section digests; the identity records out of the action component | The notes | Fact slot visits a tick fall from 4,528 to about the slots written (about 102 after the body step, and those a rule effect writes between passes); zero stale facts on every replayed tick; rule due, evaluation, firing and skip counts unchanged; a scenario in which a `collision` edit flips `holdingunwalkable` on a standing body and the rule watching it fires on the next pass; reproduce passes (hashes change once here) |
| D10. The rule latch | Medium | Name digests and the per-rule digest over the unified binding entry and its edge-bit door | The latch's holder | Latch and undo laws; the rule-faults tests reproduce; the recompute covers the latch |
| D11. The input owners | Medium | The writer inventory, then the holders and a digest per owner | Their holders | The input laws; the walkthrough routes reproduce |
| D12. The step writes on change | Medium | The unconditional writers of Action and the Input sections compare first; the turn budget behind the turn test; counts that tick while idle as due ticks | No new door | Filtered compares and compared bytes fall toward the changed sections; the fold's and the derivation's visited slots fall toward the moving bodies; the walkthrough and hall tests reproduce, with the turn's first-tick change re-recorded |

Steps D1 to D4 are built: every section is a capture image with a static
encoder and reader, the holder types and the DOOR analyzers exist, and the
Action and the two Input sections live behind holders. No step is a stopgap for
a later one. The static encoders are what makes the coverage half of the
invariant true before any field moves, and every later step only changes where
a section's value is stored. D12 is the one step the payoff does not need.

## Systems considered

| System | Verdict | Why |
|---|---|---|
| `StateArena.RowVersion` and its row digests | The pattern, not the store | Version, cached digest, segment digest, recompute and sample are copied in shape; body fields as arena cells (1f) would journal the body step |
| `IndexVersions` (the arena's row versions) and `StateArena.TryReadMoves` | Reused, lifted to a shared type | A version per index behind two mutators, with reader places and each moved index handed over once; the population keys one by slot, and the notes' publish is its one writer |
| `StateArena.CheckDigests`, `WorldPersistence.StaleDigest` | Reused | The sample, its narration and its heal are the same check for another cache |
| `WorldStateHashComposition.RecomputeAuthoritative`, `WorldReplayProbe.VerifiesIncrementalHash` | Reused | The existing switch; the continuation arm starts honouring the flag it is already passed |
| `WorldPersistence.DeclarationDigest`, `WorldStateHashComposition.AppendIdentityRecords` | The pattern for the profile | A digest keyed by the immutable value it was taken from needs no version |
| `WorldIdentity.FactsRevision` | Considered, not used | It moves for facts and state rows and not for the name, colour or an unbacked rate; a revision that covered every projected field would still be a writer's to remember |
| The `field` keyword (`StateSection`'s frozen lists, `Entry`'s occupancy) | Kept where it is, and used for the profile binding | Right for a scalar whose setter has a side effect; fails arrays and mutable structs (the spike) |
| `WorldPopulation.DisturbBody`, `WorldBody.WakeIfResting` | Unchanged | They wake a body; they are not a version, and the door does not replace them |
| `WorldBody.RigidSolverHandle`, `WorldBody.CommitRigidStep` | The pattern for the contacts | The solver works on a view and the body commits; the contacts take the same shape |
| The former pair-slot persistent comparison and table digest | Already deleted | Both were unused hand-written field lists; the proposed slot state struct will make that list a type |
| `OutcomeAnalyzer`, `StaleCaptureAnalyzer` | The home and the precedent for DOOR001 to DOOR005 | Build-error rules over calls and declarations |
| `WorldBodyTransferState`, `WorldBodyIntegrationResidue` | The residue replaced by the sections; the transfer record kept as the crossing's projection | Two hand-written field lists that must agree with the codec |
| `WorldBody.StepScratch` | Reused, simpler | It already holds two whole records and says a field added to either is covered; sections make that true for all of them |
| `ChannelValues`, `ChannelCeilingValues` | The precedent for inline per-channel storage | Already inline arrays sized by the channel limits |
| The rule host's value-derived versions (`WorldRuleHost.DeriveFacts`) | Kept | The gate decides which slots are compared; the versions still move by value |
| `IJournalParticipant` | Considered, not used | It restores side state on a rewind; a body step is not a journal scope |
| The partition-owned logs of [the parallel tick](engine-job-system.md#the-contract) | Followed | A holder's note is a partition-owned write, replayed at the publish |
| `CodeDerivedTable`, and the weak table behind `WorldStateHashComposition.AppendIdentityRecords` | Considered for the profile, not used | Each keys a product by one immutable object; a projection is several values, so the fold keeps the projection beside its digest for each profiled slot |
| `WorldBody.BodySupportMemo` | The precedent for an answer keyed by everything it reads | It is keyed by values and a derivation count inside one step; the derived view is the same idea over holders' versions, as a shared type |
| The live-value memo of `WorldStateDocumentValues` (keyed by `StateArena.RowGeneration`) and the live read of `DocumentSpatialValue` (keyed by its text) | The same two keys, a version and a content, each written at its site | Neither is a shared type; D2's lane judges, when it writes the derived view, whether the generation-keyed memo moves onto it |
| A generic versioned holder, a derived-value cache, a partition-owned note list | None exists | `puck declarations src --kind class,struct,record --name Versioned` and `--name Tracked` return nothing; `--name Derived` and `--name Memo` return the three rows above and memos nested in the one type that uses them (`RuleLatch.BindingMemo`, `WorldTick.DealMemo` and others), none a shared type; `--name Partition` returns the fork's interface and no log; `puck search` for a `ref readonly` accessor property and for `UnscopedRef` under `src` returns nothing |

## Puck-native options beside the borrowed ones

| Borrowed | Puck-native beside it | Taken |
|---|---|---|
| A smart pointer that flags a component changed on mutable access (Bevy's `Mut<T>`) | The same door, with the digest and the version inside the holder so a replaced body cannot reuse a digest | Both: 1d is the borrowed door |
| Change versions per chunk and component type, moved when write access is requested (Unity's entities) | Versions per body section, grouped by measured change rate, with one order of moved slots every reader follows from its own place | The native grouping |
| Writing only when the value differs (`set_if_neq`), at each writer | The holder comparing a moved section with the bytes its digest was taken from, so no writer has to | The native one first; the borrowed one as D12, for the compares it saves |
| A content hash as the cache key (1e) | The version as the hint and the content as the key, for the sections whose version moved | The native one |
| A change tick stamped by each system that writes | Answers computed by a static function of versioned holders, so the stamp cannot cover less than the function reads | The native one |

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
- The two agree on the trade a door on write access makes: it is cheap and
  exact about what did not change, and it over-reports what did. This memo
  takes the door for the first half and the content check for the second.

## What would change the answer

- **The kept bytes cost too much memory.** They double the sections of every
  body that has moved. If a population's awake bodies reach a count where that
  shows on the Deck, the holder keeps a 128-bit fingerprint of the raw bytes
  in their place, which is sixteen bytes a section and stronger than the
  64-bit digest it guards.
- **The compares show in a profile before D12.** D12 moves forward, starting
  with the writers the filtered-compare counter ranks highest.
- **A parked body's sections show encodes or compares.** Something writes to
  resting bodies each tick; the counter names the section, and the writer is
  fixed as the four missed writes were.
- **The profiled slots or the asleep kit bodies reach a count where their
  per-fold walk shows.** Each takes segment digests of its own, keyed by the
  set's writer.
- **Loading and storing contacts shows in a pile of awake rigid bodies.** The
  load copies occupied slots only, or the solver takes the owner's state by
  reference through a per-manifold accessor (option C2's access with option
  C3's split type).
- **Inline per-channel arrays cost the step.** A section copy per step
  snapshot is about four kilobytes for the Action section; if a profile shows
  it, the snapshot copies only the sections the step can write.
- **The Deck's memory.** The holders add an object per section per body. If
  the body count grows by orders of magnitude, the holders' storage moves into
  population-owned arrays behind the same two accessors.

## Open questions and their owners

| Question | Owner | What decides it |
|---|---|---|
| Whether any owner is associated without being solved today, which this memo reads as unreachable (a carried body is not in the contact set) | D6's lane | The contact-store counter split by whether the owner stepped, over the rigid scenes: it reads zero for owners that did not step, or it names the case |
| Whether the entry's producer, steering and cadence state is written every advanced tick | D7's lane | The filtered-compare counter for those sections |
| How many filtered compares remain after D7, and which writers own them | D12's lane | The counter, by section |
| Which key table and rule set changes must drop the latch's cache | D10's lane | The recompute over the rule-faults tests and a `world.reload` |
| The input owners' writers | D11's lane | The inventory |

