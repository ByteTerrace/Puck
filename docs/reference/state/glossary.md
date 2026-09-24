# Glossary

This glossary defines the terms the state manual uses. Each entry links to the
article that explains the term in context.

## A

**absent**<br/>
A read whose dynamic key names no cell. It isn't zero; only `isAbsent` and `??` accept it. [Learn more](expressions.md#answer-an-absent-read).

**admission**<br/>
The check a host makes, usually when it installs its rules, that it serves every facet each rule needs. A rule that fails admission is refused with the name of the missing facet. [Learn more](hosting.md#serve-host-facts-through-facets).

**advance**<br/>
A trait that accumulates a value at an exact rate per second between writes. [Learn more](traits.md#accumulate-a-value-over-time).

**allowance**<br/>
The work units a search job may spend in one step. When the allowance runs out, the job suspends and resumes on a later step. [Learn more](search.md#work-is-priced-in-units).

**alpha-beta pruning**<br/>
A negamax optimization that skips replies that can't change the choice. [Learn more](search.md#negamax).

**arena**<br/>
The columnar store that holds every cell of every lane while the simulation runs. Also called the state arena. [Learn more](arena.md#how-the-arena-stores-state).

**arm**<br/>
An effect that leaves the arena. It's queued during the firing, checked before the commit, and handled after it. [Learn more](rules.md#effects-that-leave-the-arena).

## B

**base and epoch**<br/>
For an advancing cell, the stored value and the engine tick the accumulation is measured from. [Learn more](traits.md#accumulate-a-value-over-time).

**binding (pool)**<br/>
A lexical name in a rule that holds one generation-checked handle to a pool instance. [Learn more](records-and-pools.md#use-pools-in-rules).

**board mask**<br/>
A set of at most 64 board cells packed into one Int. [Learn more](topologies.md#work-with-board-masks).

**board query**<br/>
A bounded question about a board row, such as a ray or a shape. [Learn more](topologies.md#query-a-board).

**board row**<br/>
A row with one value per cell of a topology. [Learn more](topologies.md#boards-start-with-a-topology).

## C

**candidate**<br/>
A proposed change judged inside a journal scope and then rewound. [Learn more](arena.md#evaluate-a-candidate).

**carrier (expressions)**<br/>
The numeric kind, Int or Fixed, that an untyped local computes in. [Learn more](expressions.md#locals-and-their-kinds).

**carrier (world)**<br/>
A mapping from each member of a pool record's enum field to a placement or a seat, which binds a pool instance to a body for interactions. [Learn more](worlds.md#carriers).

**catalog**<br/>
The compiled, immutable description of a section's declarations. It turns names into handles. [Learn more](arena.md#the-catalog-and-its-descriptors).

**cell**<br/>
One value in a row, addressed by its key. [Learn more](data-model.md#rows-and-cells).

**chance node**<br/>
A search ply whose result is the weighted average over a baked table of outcomes. [Learn more](search.md#account-for-chance).

**cursor (draw site)**<br/>
The number of samples a draw site has consumed. [Learn more](generators.md#seeds-cursors-and-seeking).

**cursor (staged group)**<br/>
The step a staged rule group evaluates next. [Learn more](rule-groups.md#staged-groups).

## D

**delivered arm**<br/>
An arm that fires after its firing commits. A delivery that refuses is counted and undoes nothing. [Learn more](rules.md#effects-that-leave-the-arena).

**disclosure tier**<br/>
How much of a world a peer receives: the whole document, a projection without the state section, or frames only. [Learn more](worlds.md#delivery-to-clients).

**domain**<br/>
The rule that decides which keys a row admits: slot, keys, keysOf, cellsOf, or ring. [Learn more](data-model.md#choose-an-addressing-shape).

**draw site**<br/>
A row that receives drawn values and keeps its own cursor and drawn masks. [Learn more](generators.md#share-a-distribution-keep-separate-histories).

**drawn masks**<br/>
The record of which units of a set a draw site has used up. [Learn more](generators.md#decide-what-exhaustion-means).

## E

**Edge**<br/>
A trigger mode in which a rule fires only when its gate opens after being closed. [Learn more](rules.md#choose-level-or-edge).

**embedding space**<br/>
The model, revision, and dimension count that give a family of vectors its meaning. [Learn more](vectors.md#declare-a-space-and-vector-rows).

**engine tick**<br/>
One step of the fixed 50,400-per-second clock that advancing values read. [Learn more](traits.md#engine-ticks-and-simulation-ticks).

**enum**<br/>
A closed set of named members in which member *i* is the value *i*. [Learn more](data-model.md#enums).

**expression**<br/>
A combination of numeric operands, compiled to a postfix program. [Learn more](expressions.md#follow-a-value-from-its-source).

**extension vocabulary**<br/>
The operand, key, effect, and predicate families a host registers so the rule compiler understands facts and effects only that host serves. [Learn more](hosting.md#extend-the-vocabulary).

## F

**facet**<br/>
An interface a host implements to serve reads or effects that only it understands. [Learn more](hosting.md#serve-host-facts-through-facets).

**fact**<br/>
A compiled object that answers a read or describes an effect. [Learn more](hosting.md#the-fact-families).

**firing**<br/>
One gate opening for one binding. A firing lands in one journal scope, so it's atomic. [Learn more](rules.md#make-a-firing-atomic).

## G

**gate**<br/>
The condition that must hold for a rule to fire. [Learn more](rules.md#the-parts-of-a-rule).

**generation**<br/>
The per-slot counter that distinguishes one lifetime of a pool slot from the next. [Learn more](records-and-pools.md#claim-release-and-generations).

**generator**<br/>
A declaration of how to choose a value: the source, its outcomes, and its exhaustion mode. [Learn more](generators.md#share-a-distribution-keep-separate-histories).

## H

**handle**<br/>
A catalog-bound reference to a row descriptor, or to a pool instance. [Learn more](arena.md#handles-and-their-lifetime).

**hazard**<br/>
A pair of rules whose document order can decide a result, because one reads or sets a cell the other writes in the same tick. [Learn more](analysis.md#find-order-sensitive-pairs).

**host**<br/>
The application that runs rules. It supplies the tick, serves facets, and delivers effects that leave the arena. [Learn more](hosting.md).

**host-owned row**<br/>
A row with a descriptor but no arena storage, served by a host facet. [Learn more](hosting.md#serve-rows-from-your-own-storage).

## I

**identity slot**<br/>
One fixed storage position for one possible instance of a pool. [Learn more](records-and-pools.md#claim-release-and-generations).

**identity universe**<br/>
Every identity a pool could hold. It sizes the pool's storage. [Learn more](records-and-pools.md#identity-universe-and-live-capacity).

**iterative deepening**<br/>
Searching to depth 1, then 2, and so on up to the declared depth, so the answer always comes from the deepest completed pass. [Learn more](search.md#negamax).

## J

**journal scope**<br/>
A region of arena writes that's kept or taken back as a unit. [Learn more](arena.md#journal-scopes).

**judge**<br/>
The rules a search job runs inside a candidate's scope to decide whether the candidate is a legal move. [Learn more](search.md#what-a-candidate-is).

## K

**key**<br/>
The name that addresses a cell inside its row. [Learn more](data-model.md#rows-and-cells).

**key ledger**<br/>
The arena's table of key names. A committed name stays reserved for the arena's lifetime. [Learn more](arena.md#cell-keys-and-the-key-ledger).

**knowledge row**<br/>
A row that remembers, per token, what an observer last saw. [Learn more](traits.md#remember-what-was-seen).

## L

**lane**<br/>
The owner of a row: the document, a participant, or an identity. [Learn more](arena.md#how-the-arena-stores-state).

**latch**<br/>
The record of what each rule's gate did last time. It makes Edge mode possible and is part of simulation state. [Learn more](rules.md#choose-level-or-edge).

**Level**<br/>
A trigger mode in which a rule fires on every evaluation where its gate holds. [Learn more](rules.md#choose-level-or-edge).

**local**<br/>
A value a rule computes once per evaluation, before its gate. [Learn more](rules.md#the-parts-of-a-rule).

## M

**max-n**<br/>
A search for three or more seats in which each ply maximizes the moving seat's own score. [Learn more](search.md#max-n-with-per-seat-scores).

**mint**<br/>
Admitting a new key name into an arena's key table while the simulation runs. [Learn more](arena.md#cell-keys-and-the-key-ledger).

## N

**negamax**<br/>
A two-sided game-tree search that flips the score's perspective at each ply. [Learn more](search.md#negamax).

## O

**operand**<br/>
One answer read from state, such as a cell value, the tick, or a reduction. [Learn more](expressions.md#follow-a-value-from-its-source).

## P

**packed index**<br/>
Several coordinates or choices stored in one integer. [Learn more](expressions.md#function-reference).

**pair pool**<br/>
A pool of relationships between two live instances. [Learn more](records-and-pools.md#relationships-with-pair-pools).

**pattern**<br/>
A description of which sequences of values are accepted, compiled to a small state machine. [Learn more](patterns.md#declare-a-pattern).

**phase guard**<br/>
A generation counter that lets a host reject a submission made against an older state. [Learn more](traits.md#reject-a-stale-submission).

**pile**<br/>
An ordered `keysOf` row whose cell order is the pile's order. [Learn more](data-model.md#model-a-small-game).

**ply**<br/>
One move deeper into a searched future. [Learn more](search.md#compare-futures).

**pool**<br/>
A bounded set of live instances of a record. [Learn more](records-and-pools.md#declare-a-record-and-a-pool).

**publication**<br/>
The routine that installs arena changes and updates every consumer that keeps its own copy of a state value. [Learn more](hosting.md#publish-arena-changes-in-one-place).

## R

**record**<br/>
A named group of typed fields with defaults. [Learn more](records-and-pools.md#declare-a-record-and-a-pool).

**reduction**<br/>
An aggregate over a row's cells, such as a sum, a count, or a maximum. [Learn more](expressions.md#reductions).

**refusal**<br/>
An operation that couldn't compile, evaluate, or land. A refused firing rewinds and is counted. [Learn more](rules.md#a-closed-gate-versus-a-refusal).

**row**<br/>
A named collection of values of one kind. [Learn more](data-model.md#rows-and-cells).

**row family**<br/>
One name for a group of sibling rows selected by index. [Learn more](data-model.md#row-families).

**row version**<br/>
A per-row counter that moves when the row's committed values change. [Learn more](arena.md#row-versions-and-generations).

**rule group**<br/>
A set of rules run as one unit, either to a fixpoint or as staged steps. [Learn more](rule-groups.md#run-rules-as-a-group).

## S

**savepoint**<br/>
A nested journal scope inside a firing. A `transaction` is a savepoint. [Learn more](rules.md#recover-from-a-refused-step).

**search job**<br/>
A declared search that walks candidate moves, judges each with your own rules, and writes its answer to ordinary rows. [Learn more](search.md#a-first-search-job).

**section**<br/>
The whole declared state of a document: rows, topologies, enums, records, pools, and more. [Learn more](data-model.md#the-state-section).

**settle**<br/>
Re-stamping cells when a row's behavior is re-declared, so their current values carry over. [Learn more](traits.md#re-authoring-settles-cells).

**slot**<br/>
A row that holds a single value under the reserved key `$value`. [Learn more](data-model.md#slots-and-keyed-rows).

**stale handle**<br/>
A handle from an earlier lifetime of its pool slot. It can't read or write the slot's new occupant. [Learn more](records-and-pools.md#claim-release-and-generations).

**stamp (search)**<br/>
A hash of every row except the search jobs' outputs, compared at each step to decide whether a job restarts. [Learn more](search.md#restarts-and-time).

**state transform**<br/>
One effect that makes a structured change, such as moving tokens between piles. [Learn more](transforms.md#one-implementation-for-every-caller).

**symmetry image**<br/>
Where a cell lands under one rotation or reflection of its topology. [Learn more](topologies.md#how-a-topology-compiles).

## T

**table**<br/>
Static lookup data that lives outside simulation state. [Learn more](expressions.md#tables).

**topology**<br/>
A discrete space of numbered cells and the connections between them. [Learn more](topologies.md#boards-start-with-a-topology).

**trait**<br/>
Behavior layered onto a row or cell, such as a range, an advance, or a visibility policy. [Learn more](traits.md#traits-at-a-glance).

**transactional arm**<br/>
An arm prepared before its firing commits and installed with it, so it lands with the firing's writes or not at all. [Learn more](rules.md#effects-that-leave-the-arena).

**transposition table**<br/>
A cache of search results for positions reached by more than one path. [Learn more](search.md#negamax).

**tree search**<br/>
A sampling search that uses UCB1 to choose which move to explore and seeded playouts to score it. [Learn more](search.md#monte-carlo-tree-search).

**turn**<br/>
One complete run of an undo-enabled rule group, which can later be rewound. [Learn more](rule-groups.md#what-a-turn-is).

## V

**visibility policy**<br/>
Who can read a row or cell, and what hidden cells reveal to everyone else. [Learn more](traits.md#control-what-observers-learn).

**volatile rule**<br/>
A rule the scheduler always evaluates, because its answer can change without any row version moving. [Learn more](analysis.md#skip-rules-whose-inputs-havent-changed).

## W

**word**<br/>
The whole sequence of values a pattern reads. [Learn more](patterns.md#where-a-patterns-values-come-from).

**work sheet**<br/>
The static worst-case price of the rule work a document can do in one tick, checked against the per-tick ceiling. [Learn more](analysis.md#price-a-ticks-rules).

**work unit**<br/>
The heuristic price the compiler assigns to rule work, used to bound each tick. [Learn more](analysis.md#what-a-work-unit-means).

## Z

**zone table**<br/>
A rule's indexed list of ordered piles, which lets one rule serve several piles. [Learn more](rules.md#serve-several-piles-with-one-rule).

## Next steps

- [State and rules overview](../state.md): see how these ideas fit together.
- [Limits and capacities](limits.md): look up the ceiling on each part of the system.
