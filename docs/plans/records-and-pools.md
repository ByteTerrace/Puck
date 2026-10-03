# Records and pools

This is the implementation contract for [S7](state-and-language.md#s7--records-and-pools).
Records describe typed fields; pools own bounded live instances. Their storage,
rollback, persistence, and admission belong to `Puck.State`. World supplies
enum-to-logical-body carrier mappings and durable identity ownership. The language
supplies lexical bindings and structurally typed pool-field references.

## Identity and continuation

A pool instance is identified by its pool, slot, and generation. Slots are chosen
in ascending free-slot order. Domain and field rows reserve one fixed position
per identity slot, with presence bits marking live cells. Claim, release, and
rewind never shift another instance or rebuild a pool row's key index. Pair rows
already reserve their full identity universe; this layout adds no cell capacity.
Finding a free slot still scans occupancy words up to the declared capacity;
one trailing-zero instruction does not make a multiword search constant-time.

Generations survive while slots are free. A generated, protected generation row
contains every slot, including dead ones, and participates in ordinary arena
journaling and hashing. Release advances the generation; exhaustion refuses
atomically rather than wrapping. A stale instance reads absent and cannot write.
Runtime handles belong to their arena/catalog context; persisted references use
stable pool names and explicit slot/generation, never process-local ordinals.

Canonical hash equality requires identical declarations and complete continuation
state: live identities, generations of live and dead slots, values, and any
observable metadata. A reclaimed generation-one instance is not identical to a
fresh generation-zero instance. Rewind restores the complete prior state.

All possible slot key names are declaration-owned catalog seeds. Generated row
names are deterministic, collision-checked, and inaccessible to ordinary mutation.
Record defaults use a typed, source-generated JSON representation of `CellValue`.
Compilation admits defaults against their kind and bounds, including Boolean
envelopes, before claims can copy them into fixed slots.
Record fields and seed collections own immutable snapshots of their inputs.

The arena exposes `CellCount` as a count only. `TryNextCell` is the one traversal
of held cells for every row shape: it starts from cursor zero, visits ascending
physical slots, skips holes, and ends with the default key. A structural mutation
or relayout invalidates a cursor. Generated pool rows have no meaningful public
position, so positional reads and key lookups throw `InvalidOperationException` there; ordinary ordered,
keyed, slot, lattice, and ring rows retain positional operations for their authored
order. A ring's held-cell count follows presence, even when an imported history
cursor differs from it. Ring traversal uses physical slots; `ReadWord` keeps the
oldest-to-newest chronology. Lattice words keep their empty positions.

## Document and runtime boundaries

The source document retains records and pools, not generated rows. A pool has
either authored initial instances or an explicit runtime snapshot. A snapshot
contains every generation and every live instance's fields. It is validated as a
whole; it cannot introduce incomplete instances or smuggle generated rows through
ordinary state input. Export, reload, checkpoint, and arena replacement preserve it.

Expansion has one owner and one declaration order. Every row consumer sees the
same expanded rows, while serialization keeps the authored shape. Value-only
updates must not reuse stale expanded values from a shape-compatible catalog.
Declaration hashes include pool capacity, record shape, defaults, and ownership.

Bounds are independent: instance capacity, total generated row count, distinct
reserved key count, arena bytes, variable-length payloads, and work/journal
budgets are each admitted before mutation. Shared numeric key names count once
in the arena-wide key table. Vector storage and journal work include dimensions.

## Effects and bindings

`claim pool as x { ... }` has a lexical effect body. The fresh handle occupies a
typed binding register distinct from an enclosing iteration or claim. Defaults
are installed before initializer effects run in order. Any refusal rolls back
the firing, including allocation, generations, fields, and dependent state.

`release x` validates the complete handle. Qualified field access is a typed
`StateChannelRef.PoolField`: source prints `x.field` or `pool[slot].field`, while
the document carries binding/field or pool/slot/field as separate members. It
never invents a string naming a generated row. A lexical reference holds the
selected generation; a static slot reference resolves the slot's current live
generation on each read or write. Pool iteration snapshots complete handles
before the pass, skips released or replaced handles, and never visits a newly
claimed replacement through an old snapshot entry.

Membership and generation rows participate in dependency collection, search
position keys, and cache invalidation. Allocation metadata lives in the same
journal as fields. Claim and release charge fixed-slot journal entries, field entries
and vector components, occupancy-word scans, and cascades. They have no live-count
multiplier for row movement. Snapshot iteration visits occupancy words and live
bits; endpoint release still scans dependent pair pools and journals each released
relationship. These scans remain in the admitted work price.

## Pairs and ownership

Pairs have a bounded identity universe determined by their endpoint capacities.
Direction and self-pair policy are explicit. The identity universe and maximum
live pair count must not be conflated. Endpoint lifetime changes invalidate pairs;
cascades are deterministic, bounded, atomic, and priced. Pair dependencies cannot
form cycles. Pair lookup reads never mint names.

One enum field on a record associates each logical instance with a body role.
`properties.carriers` maps every member of that enum exactly once to one named
single-inhabitant placement, one local seat, or an explicit detached binding.
Physical body indices are resolved from those logical targets at runtime and are
never stored in pool identity or record state. Interaction geometry reads the
resolved body while effects address the logical instance through their lexical
left/right bindings. Release removes the old lifetime; reclaim reads the new
lifetime's current enum value. There is no second carrier mode.

Identity-owned records are the capacity-one pools that `identity.records` selects.
They cross a seam as part of the identity projection (`WorldIdentityProjection`,
written by `WorldIdentityProjectionWire`), alongside the identity's facts row;
the owned document never travels. Their typed fields are not squeezed into the
integer-only identity fact lane (`WorldIdentityFactLane`). Receivers validate
record shapes and values (`WorldIdentityRecords.Validate`) and refuse a payload
that cannot be represented rather than discarding fields. A colocated local seat
coming home adopts the carried records into its owned identity
(`WorldIdentity.TryAdopt`); a remote round trip does not, which
[the remote home adoption step](#remote-home-adoption) closes.
[Worlds and federation](../architecture/worlds.md#joining-authority-and-admission)
owns the crossing rules.

## Implementation order and evidence

1. Land the complete single-pool model, expansion, arena operations, journal,
   export/import, scoped rule bindings, language projection, and refusal laws.
2. Integrate World publication, replacement, checkpoints, declaration hashing,
   module composition, and cache invalidation. Exercise real host scenarios.
3. Add bounded pairs, logical enum carriers, and identity-owned record transfer.
4. Migrate static record families and Paddleball/Arena. Remove superseded regex paths
   after their supported behavior has a typed replacement. Paddleball and Arena
   are migrated, and no regex path for a record family remains.
5. Run affected Release suites, architecture/vocabulary/schema checks, authored
   scenarios and real-World verification. Re-record changed baselines only after
   independent behavior and deterministic replay checks pass.
6. Close [remote home adoption](#remote-home-adoption).

Regression cases include middle-slot release/reclaim, stale handles, dead-slot
generations, generation exhaustion, nested claim rollback, release/reclaim during
iteration, typed defaults, malformed snapshots, protected-row mutations, name
collisions, section replacement, checkpoint/export reload, pair cascades, body
capacity changes, and identity travel. Warmed claim/release and judged-search
workloads must retain bounded work and avoid per-candidate allocation.

The fixed-slot gate compares equal record widths at 4, 256, and 4096 slots:
non-cascading mutation journal bytes must stay constant, and a warmed
claim/read/release/rewind candidate must allocate zero bytes. Test sparse ordinary
and pair slots across occupancy-word boundaries, including retained undo after
reload. Use the Chinese Checkers judged candidate plus rewind for the ordinary-row
performance check; record repeated before/after samples and reject a repeatable
slowdown. Capacity scans and dependent-pair cascades remain explicitly priced.

The [records-pools canary](../../tests/Puck.World.Canaries/records-pools/canary.json)
exercises the real executable: an ordinary state mutation triggers claim, field
write, release, reclaim with defaults, and deterministic replay. The shipped-game
behavior laws check Paddleball scoring and Arena elimination independently of recorded
export hashes, so a changed baseline cannot hide a migration regression.

Current status: the engine and source-language substrate includes
generation-aware handles, fixed identity-slot rows, snapshots, pairs, lexical effects,
round-trip projection, structurally typed pool-field references, enums on record
fields, and advancing numeric fields with persisted clocks. Logical enum carriers
cover reclaimed generations and pair-pool carriers. Both Arena and Paddleball use this
path; Arena keeps fighter health, respawn continuation, pickup state, and carrier
roles in its records, and Paddleball keeps its ball and paddle roles and state there.
Identity-owned record transfer and pair interactions have World integration laws
(`WorldIdentityRecordLawTests` and `CrossingIdentityPrivacyLawTests` for transfer,
adoption, rollback and replay; `WorldPoolCarrierLawTests` for carriers and pair
pools through the host evaluator). Record fields carry bounds, defaults, enums,
vector spaces and advancing numeric fields; the other row traits (`dynamics`,
`cycle`, `phase`, `visibility` and `knowledge`) stay row-only, and a world that
needs one keeps a row.

Retained turns use a load-time row closure and bounded depth. Claim/release rows
(domain, generation, and fields, including real advancing-field clocks) share the
same retained segment as ordinary writes. The ring reserves its closed depth plus
one pending segment, hashes and checkpoints its continuation, refuses or
invalidates turns that write outside the declared closure, and restores a turn
atomically through `rewindGroup`. The tree includes a 32-turn, 256-slot
release/reclaim stress law and the executable records-pools canary; coordinated
Release, generated-schema/vocabulary/name-registry, real-host, and replay checks
remain the acceptance gate for this working tree. Equal live values alone are not
the fresh-state hash criterion: allocator generations and retained continuation
must also agree, as recorded in the
[generation decision](../decisions/state-and-language.md). The authored rulepush
scenario is met by the [rulepush package](../../worlds/rulepush/README.md). Its tests
cover selective push, blocking, overlap, noun rewrite and undo. Its canaries
check deterministic replay and an eight-turn undo whose state hash matches the
earlier turn.

## Remote home adoption

**Owns:** the federation reservation leaf and its decode in
`Puck.World.Server`, the identity projection's provenance, and
`WorldIdentity.TryAdopt`'s remote caller.

**Gap:** when a traveller's round trip crosses a real network, the owned identity
at home adopts none of the facts and records the traveller carried back. A federation reservation has no field that can ask for a
local seat, so a decoded reservation is always a peer admission, and a remote
incarnation claim is unauthenticated. The facts and records another world wrote
onto the traveller are unsigned, and provenance attestation for carried state does
not exist. Only a colocated local seat coming home adopts what it carried.

**Delivers:** a remote home arrival that proves it is the seat this authority
minted, by an authenticated incarnation claim the reservation carries, and a
signed provenance for the facts and records a visited world wrote, so the owned
identity adopts through `TryAdopt` only what the owner accepts. A remote arrival
without that proof stays a peer admission and adopts nothing. The tape, the
crossing log and the checkpoint keep recording the projection the arrival bound.

**Check:** a law over two real authorities that a badge a parlor writes onto a
visitor's record is on the owned identity after the visitor walks home across a
federation link, and that a forged claim and an unsigned carried write adopt
nothing; replay of the arrival reproduces the bound projection.
