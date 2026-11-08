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
reserve `MaxLive` slots, with explicit left and right endpoint columns; the
Cartesian endpoint universe validates declarations and never sizes storage
(`StateCatalog`, `StateArena.Pools`).
Finding a free slot still scans occupancy words up to the declared capacity;
one trailing-zero instruction does not make a multiword search constant-time.

Generated rows are the current representation that [S10](state-and-language.md#s10--the-state-review)
replaces with entities as records in pools. Generations survive while slots are
free. A generated, protected generation row
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
same expanded rows, while serialization keeps the authored shape. S10's migration
uses one caller map: field access, journals, disclosure, hashing, identity
transfer, checkpoint, and rewind all move together to the pool representation.
Value-only
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

Pairs have `MaxLive`-sized storage and explicit endpoint columns, independent
of the Cartesian identity universe determined by endpoint capacities. Direction
and self-pair policy are explicit. Pair handles carry their slot generation;
endpoint handles are generation-checked before claim, and endpoint lifetime
changes invalidate pairs before a replacement lifetime can inherit them;
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

## What remains

The single-pool model, World integration, bounded pairs, logical enum carriers,
identity-owned record transfer and the record families' migration are built.
What remains is [remote home adoption](#remote-home-adoption) and the row traits
records do not carry: record fields carry bounds, defaults, enums, vector
spaces and advancing numeric fields, while `dynamics`, `cycle`, `phase`,
`visibility` and `knowledge` stay row-only, and a world that needs one keeps a
row.

The cases any change here keeps working: middle-slot release and reclaim, stale
handles, dead-slot generations, generation exhaustion, nested claim rollback,
release and reclaim during iteration, typed defaults, malformed snapshots,
protected-row mutations, name collisions, section replacement, checkpoint and
export reload, pair cascades, body capacity changes, and identity travel.
Warmed claim, read, release and rewind allocate nothing, and non-cascading
mutation journal bytes stay constant across 4, 256 and 4096 slots; capacity
scans and dependent-pair cascades stay explicitly priced. Equal live values
alone are not the fresh-state hash criterion: allocator generations and
retained continuation must also agree, as recorded in the
[generation decision](../decisions/state-and-language.md). The proof worlds
that exercised records (Arena, Paddleball, rulepush) are quarantined under
`experimental/`, so the debug world's pooled game entities are where these run.

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

The shape is decided ([what a visited world may write](../decisions/worlds-and-federation.md#what-a-visited-world-may-write);
the MMO round's step 14): write-back is a signed submission over (authority,
package, fact), never the unsigned adoption colocated seats use, and the
reader's issuer policy maps an authority namespace to the packages it may
write, refusing by name a package no authority is mapped to; in process the
map is the identity. A settled class never comes home this way: it goes to the
settlement door, and home holds the receipt. Needs the hub plan's write door
(H9, whose rule `TryAdopt` applies to what a body carries), its durable award
commit (H16), and the identity abstraction
([settlement packet](open-items.md#settlement-trust-roles-and-revocation),
step 10).

**Check:** a law over two real authorities that a badge a parlor writes onto a
visitor's record is on the owned identity after the visitor walks home across a
federation link, and that a forged claim and an unsigned carried write adopt
nothing; replay of the arrival reproduces the bound projection. With the
authority-to-packages map: a federated garden signs an award under the night
field's package name, the reader maps the garden's authority to the garden
package only, and the award is refused by name.
