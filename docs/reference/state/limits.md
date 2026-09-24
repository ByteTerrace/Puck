# Limits and capacities

Every part of the state system has a ceiling, so a document's worst-case memory
and work are known before it runs. This article lists those ceilings in one
place, grouped by area. Each row names the constant that holds the value, so you
can find it in the source or the [API reference](../../api/index.md), and links
to the article that explains the mechanism the limit bounds.

## How limits behave

A few rules apply to every limit on this page:

- **Limits are checked early.** Declaration limits are checked when the catalog
  compiles or a document loads, rule limits when a rule compiles, and storage
  limits when the arena is built or imports data. A write that would cross a
  runtime ceiling, such as the journal ceiling, is refused and its firing
  rewinds.
- **Limits follow the representation.** Many ceilings come from how a value is
  stored rather than from a separate policy. A board mask holds 64 cells because
  it's one 64-bit integer, and a transfer can move at most 4,096 tokens because
  that's the most cells a domain can hold.
- **Limits are engine-wide.** Each one bounds a whole document, and none of
  them is a per-world setting or a fixed-size buffer. When content that fits
  comfortably on its own crosses a ceiling after it's merged into a larger
  document, raise the ceiling that was hit and keep the content as it is.

> [!NOTE]
> Work units are heuristic prices that aren't calibrated against CPU time. A
> document that stays under `RuleCapacity.MaxWorkUnitsPerTick` has a bounded
> amount of rule work per tick, but that bound doesn't guarantee how long a tick
> takes. See
> [Rule analysis, scheduling, and work budgets](analysis.md).

## Declarations

| Limit | Value | Constant | Explained in |
|---|---|---|---|
| Rows per section, after pools expand | 1,024 | `StateCapacity.MaxRows` | [Rows, cells, and values](data-model.md) |
| Cells per row | 4,096 | `StateCapacity.MaxCellsPerRow` | [Rows, cells, and values](data-model.md#cell-ceilings) |
| Room for a slot or keyed row with no declared capacity | 128 cells | `StateCapacity.DefaultCellRoom` | [Rows, cells, and values](data-model.md#cell-ceilings) |
| Text value length | 1,024 UTF-16 code units | `StateCapacity.MaxTextValueLength` | [Rows, cells, and values](data-model.md) |
| Enums per section | 256 | `StateCapacity.MaxEnums` | [Rows, cells, and values](data-model.md#enums) |
| Members per enum | 1,024 | `StateCapacity.MaxEnumMembers` | [Rows, cells, and values](data-model.md#enums) |
| Row families per section | 256 | `StateCapacity.MaxFamilies` | [Rows, cells, and values](data-model.md#row-families) |
| Identifier length | 244 characters | `SafeName.MaxLength` | [Rows, cells, and values](data-model.md#names) |
| Visibility readers per policy | 32 | `StateCapacity.MaxVisibilityReaders` | [Row and cell behavior](traits.md#control-what-observers-learn) |
| Length of one visibility reader | 256 characters | `StateCapacity.MaxVisibilityReaderLength` | [Row and cell behavior](traits.md#control-what-observers-learn) |
| Provenance text per cell | 256 characters | `StateCapacity.MaxProvenanceLength` | [The state arena](arena.md#byte-accounting) |

## Storage

| Limit | Value | Constant | Explained in |
|---|---|---|---|
| Total arena size | 64 MiB | `ArenaCapacity.MaxBytes` | [The state arena](arena.md#byte-accounting) |
| Undo record held by open journal scopes | 16 MiB | `ArenaCapacity.MaxJournalBytes` | [The state arena](arena.md#the-journal-ceiling) |
| Distinct key names per key table | 65,536 | `StateCapacity.MaxCellKeys` | [The state arena](arena.md#cell-keys-and-the-key-ledger) |
| Byte charge per retained key name | 96 bytes plus 2 per UTF-16 code unit | none (internal) | [The state arena](arena.md#cell-keys-and-the-key-ledger) |
| Participant and identity lane sizes | 256 each by default | `ArenaCapacity.DefaultParticipants`, `ArenaCapacity.DefaultIdentities` | [The state arena](arena.md) |
| Slots in a world's body and identity lanes combined | 128 | `StateCapacity.MaxBodySlots` | [State in Puck.World](worlds.md#lanes) |

## Records and pools

| Limit | Value | Constant | Explained in |
|---|---|---|---|
| Identity universe of one pool (a pair pool's is left capacity times right capacity) | 4,096 | `StateCapacity.MaxCellsPerRow` | [Records, pools, and handles](records-and-pools.md#identity-universe-and-live-capacity) |
| Live pool bindings in one rule | 32 | `StateCapacity.MaxInstanceBindings` | [Records, pools, and handles](records-and-pools.md#bindings-and-atomic-claims) |
| Generations per slot | `long.MaxValue` | none | [Records, pools, and handles](records-and-pools.md#claim-release-and-generations) |

## Topologies and boards

| Limit | Value | Constant | Explained in |
|---|---|---|---|
| Cells per topology or board | 4,096 | `TopologyCompilation.MaxCells` | [Topologies and boards](topologies.md) |
| Directions and aliases per topology | 64 | `TopologyCompilation.MaxDirections` | [Topologies and boards](topologies.md) |
| Topologies per document | 64 | `TopologyCompilation.MaxTopologies` | [Topologies and boards](topologies.md) |
| Hex radius | 36, the largest radius whose cell count fits 4,096 | `TopologyCompilation.MaxHexRadius` | [Topologies and boards](topologies.md#hex-cells) |
| Penrose inflations | 9 | `TilingGenerator.MaxPenroseInflations` | [Topologies and boards](topologies.md#generated-tilings) |
| Cells in a board mask | 64 | `BoardMask.MaxCells` | [Topologies and boards](topologies.md#work-with-board-masks) |

## Expressions

| Limit | Value | Constant | Explained in |
|---|---|---|---|
| Tokens per program or subprogram | 256 | `RuleCapacity.MaxExpressionTokens` | [Reads and expressions](expressions.md#subprograms-and-calls) |
| Subprograms per program | 64 | `RuleCapacity.MaxSubprograms` | [Reads and expressions](expressions.md#subprograms-and-calls) |
| Arguments per function call | 16 | `RuleExpressions.MaxArguments` | [Reads and expressions](expressions.md#subprograms-and-calls) |
| Constant-folded subtree size | 65,536 steps | `RuleWorkBudget.MaxFoldSteps` | [Reads and expressions](expressions.md#constant-folding-and-faults) |
| Infix text length and nesting | 16,384 characters, 256 levels | `ExpressionSpelling.MaxLength`, `ExpressionSpelling.MaxNesting` | [Reads and expressions](expressions.md) |
| Hilbert order | 1 to 31 | none (internal) | [Reads and expressions](expressions.md#function-reference) |
| `factorial` argument | 20 | none (the result must fit 64 bits) | [Reads and expressions](expressions.md#function-reference) |
| Subset universe | 64 elements | none (one 64-bit mask) | [Reads and expressions](expressions.md#function-reference) |
| Packed arrangement length | 16 elements | none (four bits per element) | [Reads and expressions](expressions.md#function-reference) |
| `primeAt` index | the first 256 primes | none | [Reads and expressions](expressions.md#function-reference) |
| Tokens ranked by `arrangementRank` | 20 | `StateReader.MaxArrangementTokens` | [Reads and expressions](expressions.md#reductions) |

## Patterns

| Limit | Value | Constant | Explained in |
|---|---|---|---|
| Patterns per document | 256 | `PatternCapacity.MaxRows` | [Patterns](patterns.md) |
| Symbols per pattern | 32 | `PatternCapacity.MaxSymbols` | [Patterns](patterns.md#symbols-and-the-refined-alphabet) |
| Compiled states per pattern | 64 by default, at most 1,024 | `PatternCapacity.DefaultStates`, `PatternCapacity.MaxStates` | [Patterns](patterns.md#how-a-pattern-compiles) |
| Repetition count | 128 | `PatternCapacity.MaxRepeat` | [Patterns](patterns.md) |
| Word length | 4,096 | `PatternCapacity.MaxWord` | [Patterns](patterns.md) |
| All compiled pattern tables | 4 MiB | `PatternCapacity.MaxTableBytes` | [Patterns](patterns.md#how-a-pattern-compiles) |

## Rules and rule groups

| Limit | Value | Constant | Explained in |
|---|---|---|---|
| Rule work per tick | 4,000,000 work units | `RuleCapacity.MaxWorkUnitsPerTick` | [Rule analysis, scheduling, and work budgets](analysis.md) |
| Locals per rule, including implicit ones | 64 | `RuleCapacity.MaxLocalsPerRule` | [Rules and firing](rules.md) |
| Top-level effects per rule | 256 | `RuleCapacity.MaxEffectsPerRule` | [Rules and firing](rules.md) |
| Effects in a transaction or its `onFailure` list | 256 | `RuleCapacity.MaxTransactionEffects` | [Rules and firing](rules.md#recover-from-a-refused-step) |
| Gate nesting depth | 64 | `RuleCapacity.MaxPredicateNesting` | [Rules and firing](rules.md#read-a-compile-refusal) |
| Gate tokens | 1,024 | `RuleCapacity.MaxPredicateTokens` | [Rules and firing](rules.md#read-a-compile-refusal) |
| Evaluations one trace captures | 256 | `RuleEvaluator.MaxTraceEvaluations` | [Rules and firing](rules.md#trace-a-rule-and-read-the-refusal-ledger) |
| Members per rule group | 256 | `RuleGroupCapacity.MaxMembers` | [Rule groups and turn undo](rule-groups.md) |
| Fixpoint passes per tick | 8 by default, at most 256 | `RuleGroupCapacity.DefaultPasses`, `RuleGroupCapacity.MaxPasses` | [Rule groups and turn undo](rule-groups.md#fixpoint-groups) |
| Retained turn history | bounded by the journal ceiling | `ArenaCapacity.MaxJournalBytes` | [Rule groups and turn undo](rule-groups.md#size-the-history) |

## Transforms

| Limit | Value | Constant | Explained in |
|---|---|---|---|
| Tokens moved by one transfer | 4,096 | `StateTransferCapacity.MaxTransferCount` | [State transforms](transforms.md#move-tokens-with-transfer) |
| Tokens reordered by `arrange` | 20 | `StateReader.MaxArrangementTokens` | [State transforms](transforms.md#arrange) |
| Board cells written by `writeSet` | 64 | `BoardMask.MaxCells` | [State transforms](transforms.md#writeset) |

## Generators

| Limit | Value | Constant | Explained in |
|---|---|---|---|
| Named sources per document | 256 | `GeneratorCapacity.MaxDeclaredSources` | [Generators and draw sites](generators.md#declare-a-source) |
| Weighted outcomes per source | 256 | `GeneratorCapacity.MaxWeightedOutcomes` | [Generators and draw sites](generators.md#source-families) |
| Range bounds | 32-bit signed integers | `GeneratorCapacity.MinRangeBound`, `GeneratorCapacity.MaxRangeBound` | [Generators and draw sites](generators.md#source-families) |
| Markov contexts | 64 | `GeneratorCapacity.MaxContexts` | [Generators and draw sites](generators.md#generate-text-with-a-markov-source) |
| Alternatives per Markov context | 256 | `GeneratorCapacity.MaxAlternativesPerContext` | [Generators and draw sites](generators.md#generate-text-with-a-markov-source) |
| Tokens in one Markov emission | 256 | `GeneratorCapacity.MaxEmissionBound` | [Generators and draw sites](generators.md#generate-text-with-a-markov-source) |
| Markov token length | 64 | `GeneratorCapacity.MaxTokenLength` | [Generators and draw sites](generators.md#generate-text-with-a-markov-source) |
| Units in one drawn set | 256 | `GeneratorCapacity.MaxEntriesPerSet` | [Generators and draw sites](generators.md#decide-what-exhaustion-means) |
| Drawn masks per draw site | 256 (8 KiB) | `ArenaCapacity.MaxDrawnMasks` | [Generators and draw sites](generators.md#declare-a-source) |
| Extended equidistribution table size | 2 to 4,096 | `GeneratorCapacity.MinExtendedTableSize`, `GeneratorCapacity.MaxExtendedTableSize` | [Generators and draw sites](generators.md#extend-a-sources-equidistribution) |

## Vectors

| Limit | Value | Constant | Explained in |
|---|---|---|---|
| Embedding spaces per section | 64 | `StateCapacity.MaxVectorSpaces` | [Vectors and embedding spaces](vectors.md#declare-a-space-and-vector-rows) |
| Dimensions per space | 8 to 1,024 | `StateCapacity.MinVectorDimensions`, `StateCapacity.MaxVectorDimensions` | [Vectors and embedding spaces](vectors.md#declare-a-space-and-vector-rows) |
| Space name, model, and revision length | 128 characters each | `StateSpace.MaxNameLength`, `EmbeddingIdentity.MaxModelLength`, `EmbeddingIdentity.MaxRevisionLength` | [Vectors and embedding spaces](vectors.md#declare-a-space-and-vector-rows) |
| One vector row (cells times dimensions) | 65,536 bytes | `StateCapacity.MaxVectorRowBytes` | [Vectors and embedding spaces](vectors.md#what-a-vector-row-can-carry) |
| All vector rows in a section | 4 MiB | `StateCapacity.MaxVectorSectionBytes` | [Vectors and embedding spaces](vectors.md#what-a-vector-row-can-carry) |
| `nearest` results | 256 | `StateCapacity.MaxNearestResults` | [Vectors and embedding spaces](vectors.md#nearest) |
| `mix` terms and weight magnitude | 32 terms, weight at most 1,000 | `StateCapacity.MaxMixTerms`, `StateCapacity.MaxMixWeight` | [Vectors and embedding spaces](vectors.md#mix) |

## Search

| Limit | Value | Constant | Explained in |
|---|---|---|---|
| Jobs per document | 16 | `SearchCapacity.MaxJobs` | [Search](search.md) |
| Candidate shapes per job | 16 | `SearchCapacity.MaxShapesPerJob` | [Search](search.md) |
| Promotion codes per promote shape | 16 | `SearchCapacity.MaxPromotions` | [Search](search.md) |
| Search depth | 64 plies | `SearchCapacity.MaxDepth` | [Search](search.md) |
| Candidates judged per step | 4,096 | `SearchCapacity.MaxNodesPerTick` | [Search](search.md#work-is-priced-in-units) |
| Transposition entries per scored negamax job | 8,192 | `SearchCapacity.TranspositionEntries` | [Search](search.md#negamax) |
| Tree search node pool | 8,192 | `SearchCapacity.TreeNodes` | [Search](search.md#monte-carlo-tree-search) |
| Tree search iterations | 65,536 | `SearchCapacity.MaxIterations` | [Search](search.md#monte-carlo-tree-search) |
| Chance outcomes per plan | 256 | `SearchCapacity.MaxChanceOutcomes` | [Search](search.md#account-for-chance) |
| Legal-destination mask | 64 cells | `BoardMask.MaxCells` | [Search](search.md#read-what-a-job-produces) |

## Time and traits

| Quantity | Value | Constant | Explained in |
|---|---|---|---|
| Engine ticks per second | 50,400 | `FixedTickConversion.TicksPerSecond` | [Row and cell behavior](traits.md#engine-ticks-and-simulation-ticks) |
| Dynamics frequency | above 0, at most 100 Hz | `DynamicsRow.MaxFrequencyHz` | [Row and cell behavior](traits.md#smooth-a-value-for-presentation) |
| Dynamics damping | 0 to 16 | `DynamicsRow.MaxDamping` | [Row and cell behavior](traits.md#smooth-a-value-for-presentation) |
| Dynamics response | -4 to 4 | `DynamicsRow.MinResponse`, `DynamicsRow.MaxResponse` | [Row and cell behavior](traits.md#smooth-a-value-for-presentation) |

## Next steps

- [The state arena](arena.md#byte-accounting): see how a document's bytes are
  counted against the arena ceiling.
- [Rule analysis, scheduling, and work budgets](analysis.md): see how a tick's
  rule work is priced.
- [State and rules overview](../state.md): return to the map of the state
  manual.
