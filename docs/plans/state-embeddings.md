# Embeddings in state

An embedding is a list of numbers that places a piece of content — a line of
dialogue, an item's lore, an event, something a player typed — in a space where
similar content lands close together. This plan makes embeddings ordinary,
deterministic `Puck.State` values: an author writes text, and rules remember it,
compare it, mix it, and recall the closest matches, with bit-identical replay.

The simulation never runs a model:

- **Authored text** resolves once, in `puck embed`, into a committed lock file that
  compilation reads without network access.
- **Text that appears at runtime** (chat, names, agent notes) is embedded by an
  operator-approved host service outside the tick. The vector enters state as a
  recorded contribution, so replay reads the recorded bytes and never calls a model.

This is the only plan for the feature. It records what exists, the defects still
open in that code, and everything that remains. When code and this plan disagree,
the plan wins; update the code, its tests, and its docs to match.

The [state reference](../reference/state.md) owns the row, cell, and rule model;
[`Puck.Maths`](../../src/Puck.Maths/README.md) owns the kernels; the
[world transpiler guide](../../src/Puck.World.Transpiler/README.md) owns `.puck`
spelling; [declarative service extensions](../../src/Puck.World.Server/ExtensionConfiguration.md)
own host service composition.

## Implementation status

**Status:** Complete. All phases implemented, tested, and verified across all law suites and repository gates. Checked against `397bc104c`.

### Implemented

Keep the following, and build on it without reworking it.

- **SQL dialect and tooling.** `DocumentVocabularyResolver` selects the vocabulary for
  `compile`, `lint`, and `lsp`; `WorldDecompiler.Sql.cs` projects only what re-lowers
  identically; the SQL vector spelling parses, lowers, and decompiles.
- **Maths.** `SignedByteVectorFunctions` (`Dot`, `SumOfSquares`, `CosineQ16`,
  `IsUnitAdmissible`, `TryNormalize`, `TryQuantizeUnit`) with the six
  `signed-byte-vectors.*` laws.
- **State.** `CellKind.Vector`, `StateVector`, `StateSpace`, the frame vector buffer,
  the `mix`/`mean`/`nearest`/`remember` transforms and kernels, and
  `ResolvedVectorTransform`.
- **Rules** (`RuleCompiler.Vectors.cs`, `RuleEvaluator.Vectors.cs`): vector calls, copy
  (submitted as `UpsertCell.Vector`), the four transforms, exact costs, compile-time
  refusals including `VectorExcludeKey`, `Text` key indirection, and trace records for
  vector calls, `nearest` writes, and `remember` verdicts.
- **Schema.** Spaces, row space resolution, every trait, domain, cell, HUD, response,
  search, and binding refusal, ceilings through `CellCeiling`, space identity across
  basis, imports, and owned worlds, and `WorldObservedCell.Vector`.
- **Server and surfaces.**
  - `UpsertStateCell.Vector`, `MapStateMutation`, frame routing, and the sparse
    transforms in `WorldStateTransforms.Vectors.cs`, which check space identity,
    `where`, `into` shape, and `capacity`; `Subjects` names only the written row;
  - hashing, the `world.state` digest echo, and `world.state.similar`;
  - `WorldAgentBridge.WriteVectorAsync`, `puck_state_vector_write` (it runs
    `world.state.cell.set`), browser vector reads and writes, and the addon decoder's
    named `Vector` refusal.
- **Embedding tooling.**
  - `src/Puck.Embeddings`: `EmbeddingIdentity`, the fixture and `azure-openai`
    generators on `Microsoft.Extensions.AI`, batching, and quantization;
  - `EmbeddingLock` in `Puck.World.Transpiler`;
  - `puck embed` (`--check`, `probe`) for texts in SQL blocks;
  - `puck compile`, `puck lint`, and `PuckWorldLoader` load the lock, and SQL `embed(...)`
    resolves through it.
- **Tests** for all of the above in `Puck.State.Tests`, `Puck.World.Schema.Tests`,
  `Puck.World.Tests` (`VectorStateLawTests`, `ShippedWorldHashStabilityTests`),
  `Puck.World.Browser.Tests`, `Puck.World.Agents.Tests`, `Puck.World.Transpiler.Tests`
  (`StateSqlEmbeddingLoweringTests`), and `Puck.Cli.Tests` (`EmbedCommandTests`,
  `OpenAiEmbeddingGeneratorTests`, `RemoteMcpTests`).

### Open defects

Fix these in order. Each has a done condition.

1. **Server fixes lack their tests.** Add to `VectorStateLawTests`:
   - a compiled rule `memories[$each] = events[$each]` fired into an absent key, reading
     back the copied bytes;
   - `world.state.transform` refusing, by name, a cross-space operand, a `where` that is
     not a keyed `Bool` row, a keyed `nearest` `into` without `capacity`, and a `Text`
     `into` that is not a slot or has `k` other than `1`;
   - a principal holding Edit only on `into` applying `mix`, `mean`, `nearest`, and
     `remember` through `world.state.transform`.
2. **Weak tests.** Each must fail if the behavior it names breaks.
   - `WorldAgentBridgeTests`: drive `WriteVectorAsync` through a real `WorldServer` and
     assert the granted write lands and the ungranted one does not.
   - `RemoteMcpTests`: send a real base64url vector (no prefix) through
     `puck_state_vector_write` into a session that runs the real `world.state.cell.set`,
     for a granted and an ungranted principal.
   - `Vector_EvictingTableReplayDeterminism`: record the run with `WorldReplayTape` and
     verify the replay's `stateHash`.
   - `VectorRemember_SkipsNearDuplicate_AndIgnoresOwnKey`: `k1` holds `A`; remember `k1`
     from `A′`, which is within `unlessWithin` of `A` but has different bytes; assert
     `k1` now holds `A′`.
   - `VectorNearest_MalformedExclude_FailsEvaluation`: compile a rule whose `exclude` is
     `$cell:<row>:<key>` over a `Text` cell holding a non-`CellName`, and assert the
     effect fails.
   - Rename `ShippedWorldHashStabilityTests` to state what it proves: shipped worlds
     allocate no vector buffer and hash reproducibly.
3. **Unresolved SQL `embed(...)` still emits a value.** `FormatQueryOperand` and the other
   `TryResolveEmbeddedText` callers in `WorldDocumentEmitter.Sql.cs` return `""` when the
   lock has no entry. Report `PUCK079` and emit no operand. **Done** when lowering a
   missing entry produces `PUCK079` and a JSON document is never written.
4. **`puck embed` can erase a lock.** `EmbedCommand.TryDiscoverSpacesAndTexts` reads the
   lowered document without checking `HasErrors`, and it collects texts only from SQL
   blocks, so a native document yields no texts and pruning deletes every entry.
   - Have lowering report every embedded text per space, from both dialects, and make
     `puck embed` use that report. Delete the SQL-only AST walk.
   - Exit 2 without writing when lowering has errors, including a parse failure.

   **Done** when `puck embed` on a document with lowering errors leaves its lock
   byte-identical and exits 2.
5. **The language server ignores the lock.** Every `LowerWithDiagnostics` call in
   `PuckLanguageServer.cs` passes `embeddings: EmbeddingLock.TryLoad(sourcePath)`.
   **Done** when an LSP diagnostics test reports `PUCK079` for an unlocked `embed`.
6. **`Puck.World.Transpiler` references `Puck.Embeddings`.** `EmbeddingLock` uses only
   `EmbeddingIdentity`. Key lock spaces by `StateSpace` identity (`HasSameIdentity`) and
   remove the project reference, so the transpiler and `Puck.World` carry no provider
   packages. **Done** when neither project's `packages.lock.json` lists `OpenAI`,
   `Microsoft.Extensions.AI`, or an `Azure.*` package that `Puck.Embeddings` brings.
7. **The generators don't meet the provider contract** in Part 1 (Authoring and
   `puck embed`, Providers):
   - every API key path is gone: no key option, environment variable, header, or
     `ApiKeyCredential`, in `Puck.Embeddings`, `EmbedCommand`, and their tests;
   - `OpenAiEmbeddingGeneratorFactory` authenticates only with a `TokenCredential`,
     defaulting to `DefaultAzureCredential`;
   - the generator checks the answer count and every answer's length against the
     request, and refuses a non-finite component;
   - a non-success response reports its status code and at most 512 characters of
     body, read from the response;
   - no message or exception, inner exceptions included, carries a token;
   - `FixtureEmbeddingGenerator` normalizes its signed bytes with
     `SignedByteVectorFunctions.TryNormalize`, with no floating point.

   **Done** when `OpenAiEmbeddingGeneratorTests`, using a stub `TokenCredential`, covers
   the bearer token on the request, a count mismatch, a length mismatch, an out-of-order
   `index`, `--batch-size` splitting, and a 500 response whose message holds the status
   and body but not the token.
8. **`EmbeddingLock.PruneEntries` and `PruneSpaces` are unused.** `EmbedCommand` prunes
   inline. Make `EmbedCommand` call them.
9. **`EmbedCommandTests` gaps.** Add a fixture lock compared byte-for-byte against a
   committed expected file, and assert the `PUCK079` code, not only the exit status.

### Not started

- native `.puck` lowering, decompiler (`puck decompile --embeddings`), language server
  vocabulary, and `PUCK_LINT_010`;
- runtime embedding connections and `src/Puck.World.Embeddings`;
- samples, docs, and the performance measurements.

## Work order

1. Fix open defects 1–9.
2. **Native `.puck`:** lowering, decompiler, language server, and linter, with the
   `EmbeddingDeclaration*` tests. The decompiler prints a form only once lowering
   accepts it, and `puck decompile --embeddings` passes the lock to it.
   `ShippedWorldsParityTests`, `FormatterRoundTripTests`, and
   `StateSqlDeclarationDecompilerTests` pass at every commit.
3. **Runtime embedding connections** and `Puck.World.Embeddings`.
4. **Finish:** samples, docs, performance measurements, and the full command list in
   Part 3.

Run each step's tests before starting the next.

## What authors get

| Pattern | How it is built |
|---|---|
| An NPC remembers and recalls what matters now | `remember` stores an event in an evicting table unless a near-duplicate exists; `nearest` recalls memories closest to the current situation. |
| Factions, moods, relationships drift | `mix` for fast drift; `mean` over an evicting history table for exact slow drift; a `similarity` threshold gates behavior. |
| Move away, contrast, analogy | `mix` with negative weights. |
| Items, recipes, hints find their kin | `nearest` over a catalog, with `where` to restrict candidates and `exclude` to skip the item itself. |
| Dialogue that fits the moment | `table lines : Text embeds(lineVectors)`; `nearest` writes the best key into a `Text` slot; `lines[$cell:reply:$value]` reads the line. |
| An NPC understands a player | A chat table is a runtime embedding request table; its vectors feed `nearest`. |
| Semantic word games | Guesses scored with `similarity`, including `farthest` for "coldest". |
| Checks against an inline concept | `when similarity(stance[guards], embed("danger")) > 0.6`. |
| Agents with world memory | An agent writes a vector or a `Text` note, and reads back what `nearest` recalled. |

Search `Score` programs can read `dot` and `similarity`. Authors tune thresholds with
`puck embed probe` and `world.state.similar`; `world.rule.trace` prints every
similarity a rule computed.

## Out of scope

- Model inference in the tick, compiler, linter, formatter, or language server.
- An in-process model (ONNX and similar); only the fixture and `azure-openai` providers ship.
- API keys. Every provider authenticates with identity.
- Generated text; providers return vectors only.
- Approximate nearest-neighbour indexes.
- Vector values in general expressions (bindings, component reads, arithmetic), and
  distance or norm functions.
- `nearest`, `remember`, and key-minting vector writes inside `FrameHost` (browser
  session and search). They are refused by name.
- Runtime embedding in the browser session or a remote-client boot.
- Vector writes from addon guests and cartridges. `WorldAddonMutationDecoder` and the
  cartridge vocabulary refuse `Vector` by name.

# Part 1: the contract

## Embedding spaces

```puck
state {
    spaces {
        space lore { model: "text-embedding-3-small"  revision: "1"  dimensions: 256 }
    }
}
```

```json
"state": { "spaces": [ { "name": "lore", "model": "text-embedding-3-small", "revision": "1", "dimensions": 256 } ], "world": [ ... ] }
```

- **Fields.**
  - `name` is a `CellName`.
  - `model` and `revision` are non-empty strings of at most 128 characters.
  - `dimensions` is in `[8, 1024]`.
  - A document has at most 16 spaces, with unique names.
- **Revision.** Changing `model`, `revision`, or `dimensions` makes the space's lock
  entries stale.
- **Rows.** A `Vector` row names its space. Vectors are comparable only within one
  space.
- **Default space.** With exactly one declared space, `space(...)` may be omitted in
  `.puck` and SQL. Compiled JSON always names the space.
- **Composition.**
  - Imported modules' spaces compose under their alias, and `exports` govern them as
    they govern rows.
  - Two declarations of one composed space name must satisfy `HasSameIdentity`, or
    composition refuses by space name. This covers basis, import, and an owned
    identity document against its world.
  - The data-model reference explains recovery after a model change:
    1. re-bake with `puck embed`;
    2. rebuild saved vector rows with copy rules;
    3. clear runtime results so their connection embeds again.

## Representation and admission

- **Cells.** Every cell of a `Vector` row holds exactly its space's `dimensions`
  components as a `StateVector`.
- **Components** are `sbyte` values in `[-127, 127]`; `-128` is refused.
- **Admission.** With `S` the sum of squares and `n` the dimensions, a vector is
  admitted when `|floorSqrt(S) − 127| ≤ (ceilSqrt(n) + 1) / 2 + 1`. Zero and
  unnormalized vectors are refused.
- **`dot`** is the exact integer dot product.
- **`similarity`** is exact cosine similarity in Q48.16, computed by
  `SignedByteVectorFunctions.CosineQ16`:
  - `raw = sign(dot) × roundHalfEven(|dot| << 32, floorSqrt((Sa × Sb) << 32))`;
  - the result is clamped to `[-65536, 65536]`;
  - self-similarity is exactly `1.0`.
- **Normalizing** (`TryNormalize`): given `|c_i| ≤ 2^24`, it computes
  `r = floorSqrt(S << 32)` and
  `q_i = sign(c_i) × roundHalfEven((127 × |c_i|) << 16, r)`. The result is admitted by
  construction.
- **Quantizing** a provider vector (`TryQuantizeUnit`, outside the simulation only):
  - every component must be finite with `|x| ≤ 8`;
  - each becomes `(long)Math.Round(x × 2^20, ToEven)`;
  - the result is then normalized.

**Naming.** `cosine(x)` is the existing scalar trigonometric function. The vector
function is `similarity`. The vector functions `dot`, `similarity`, and `identical` are
not `ExpressionOperators` entries. `ExpressionSpelling` parses them into
`ValueToken.VectorCall`, and the document-time evaluator in `Puck.Transpiler` refuses
them with `PUCK041`.

## Shapes and traits

- **Shapes.** A `Vector` row is a keyed table or a slot. `capacity`, `evicts`,
  `visibility`, cells, and `space` apply.
- **Refused** by both the validator and the transpiler:
  - the traits `min`, `max`, `overflow`, `advance`, `dynamics`, `cycle`, `draw`,
    `valuesFrom`, `inverse`, `phase`, `phaseOf`, `gatesDrive`, and `field`;
  - every other domain;
  - `addState`, `countdownState`, `scheduleState`, and `pushState`;
  - HUD bindings.
- **Ceilings.** `StateRow.CellCeiling × dimensions ≤ 65536` per row, and at most 4 MiB
  across all vector rows.
- **`evicts`.** The `table` declaration has an `evicts` modifier for every row kind,
  and it requires `capacity`.

## Operations

**Vector operands** are either:

- a cell reference: `row` (a slot), `row[key]`, `row[$each]`, `row[$value]`, or
  `row[$cell:<row>:<key>]`, resolved like `setState.key`; or
- a literal: `vector("<base64url>")` or `embed("text"[, space: name])`.

A literal takes its space from:
- the other operand (`dot`, `similarity`, `identical`);
- the destination (copy, `mix`, `mean`, `remember`);
- `from` (a `nearest` query);
- otherwise the default space.

With none of those, it is `PUCK085`. Compiled JSON always holds
`vector("<base64url>")`.

**`Text` key indirection.** `$cell:<row>:<key>` reads a `Text` cell's text as the key
when that row is `Text` (`CompiledCellRef.Kind`). Text that is not a `CellName` fails
with `KeyIndirectionInvalid`.

**Mismatches.** A non-vector operand or a space mismatch refuses: at compile time when
both rows are known (`VectorOperandNotVector`, `VectorSpaceMismatch`), and in
`WorldStateTransforms` for console-submitted transforms. Spaces match by
`HasSameIdentity`.

Costs, in rule work units, where `d` is the space's dimensions:

| Operation | Form | Result | Cost |
|---|---|---|---|
| `dot(a, b)` | expression | `Int` | `2 + d` |
| `similarity(a, b)` | expression | `Fixed` in `[-1, 1]` | `2 + 3d` |
| `identical(a, b)` | expression | `Int` `1`/`0` | `2 + d` |
| copy `row[key] = <operand>` | effect | one cell | `d` |
| `mix` | transform | one cell | `(terms + 1) × d` |
| `mean` | transform | one cell | `(capacity(from) + 1) × d`, `+ capacity(from)` with `where` |
| `nearest` into `Int` | transform | keyed table | `capacity(from) × (d + 2)`, `+ capacity(from)` with `where` |
| `nearest` into `Fixed` or `Text` | transform | keyed table or slot | `capacity(from) × (3d + 2)`, `+ capacity(from)` with `where` |
| `remember` | transform | one cell or none | `capacity(into) × 3d + d` |

**Reads.** An absent cell fails the read, so the rule does not fire. A zero vector is
never substituted.

**Copy.** JSON is `setState` with `expression: "other[key]"`, or with
`vector: "<base64url>"` for a literal.

**`mix`** writes the normalized sum of 1 to 8 weighted terms:

```puck
transform m = mix(into: "stance[$each]", terms: [
    { from: "stance[$each]", weight: 3 }
    { from: "events[ambush]", weight: 1 }
    { from: embed("calm"), weight: -1 }
])
```

- Each weight is an integer in `[-1000, 1000]` and not zero.
- A zero sum fails with `VectorMixZero` and leaves `into` unchanged.
- `PUCK_LINT_010` warns when a literal weight's share `|w| / Σ|w|` is below 1/64. The
  message names `mean` over a history table as the exact alternative.

**`mean`** writes the normalized sum of a table's candidates:
`transform profile = mean(from: memories, into: "self", where: important)`. With no
candidates, or a zero sum, it fails with `VectorMeanEmpty`.

**`nearest`** writes the closest cells:

```puck
transform recall = nearest(from: memories, query: "situation", into: recalled, k: 3,
    threshold: 0.5, where: aboutCaravan, exclude: "$each", farthest: false)
```

- **Ranking** is total: better score first, then key in ordinal order.
- **Scores.** The kind of `into` decides what is scored and written:
  - an `Int` keyed table scores and writes `dot`;
  - a `Fixed` keyed table scores and writes `similarity`;
  - a `Text` slot requires `k: 1`, scores `similarity`, and writes the best key, or
    the empty string when there is no candidate.
- **`threshold`** uses the score's units. It is a lower bound, or an upper bound when
  `farthest` is true.
- **`where`** is a keyed `Bool` row; a candidate needs `true` under its own key.
- **`exclude`** is a key, spelled like `setState.key`.
- **`farthest`** ranks lower scores first.
- **Writing.** A keyed `into` is replaced in one mutation by `key = source key`,
  `value = score`. It must declare `capacity`.
- **`k`** is in `[1, min(capacity(into), 64)]`.

**`remember`** stores unless a near-duplicate exists:
`transform note = remember(into: memories, key: "$each", from: "events[$each]", unlessWithin: 0.9)`.

- Nothing is written when any other key has `similarity ≥ unlessWithin`, and the trace
  records that key.
- Otherwise it upserts, with eviction.
- `unlessWithin` is in `[0, 1]`.

**Document form** (the records in `StateOperations.cs`):

```json
{ "$type": "transformState", "transform": { "$type": "nearest", "from": "memories", "query": "situation", "into": "recalled", "k": 3, "threshold": "0.5", "where": "aboutCaravan", "exclude": "$each", "farthest": false } }
```

- Operand fields are operand-spelled strings: one state token or one vector literal.
- `threshold` and `unlessWithin` are canonical decimal strings in the score's kind.

**SQL spelling** (lowered and decompiled exactly as native):

| SQL | Native |
|---|---|
| `CREATE TABLE t (id TEXT PRIMARY KEY, embedding VECTOR(lore)) CAPACITY 128 EVICTS` | a `Vector` row |
| `INSERT INTO t (id, embedding) VALUES ('a', embed('text'))` | a vector cell; `vector('…')` spells bytes |
| `WHERE similarity(a.embedding, b.embedding) > 0.6`, `dot(...)`, `identical(...)` | expression functions |
| `UPDATE t SET embedding = u.embedding WHERE …` | copy |
| `INSERT INTO r (id, score) SELECT id, similarity(embedding, q.embedding) FROM t [WHERE …] ORDER BY embedding <=> q.embedding [DESC] LIMIT k` | `nearest`; `WHERE similarity(...) >= x` is `threshold`, `DESC` is `farthest`, a `Bool` column is `where`, `id <> 'x'` is `exclude` |

`mix`, `mean`, and `remember` have no SQL spelling.

## Rule compilation and application

- **Operands.** `RuleCompiler` resolves every vector operand once into a
  `CompiledVectorOperand`, checking spaces and cost as it does.
- **Expression calls.** A `VectorCall` compiles to one `CompiledExpressionToken` whose
  operand is a `VectorCallOperand`. `RuleWorkBudget.ExpressionCost` uses that
  operand's cost.
- **Effects.** Copy and the four transforms compile to the `Vector*Effect` facts. At
  evaluation each resolves keys against the evaluator's bindings.
  - Copy reads its source and submits `StateMutation.UpsertCell.Vector` carrying a
    `StateVector`.
  - `mix`, `mean`, `nearest`, and `remember` submit `StateMutation.ApplyVector` with
    concrete ordinals, keys, and `StateVector` constants.
- **Journal and replay.** `WorldServer.RuleFrame.MapStateMutation` maps
  `UpsertCell.Vector` to `UpsertStateCell` with `Vector`, and `ApplyVector` to
  `TransformState`. It names literal keys and `vector("…")` literals, and writes
  `threshold` and `unlessWithin` as canonical decimals in the score's kind, so the
  journal and replay record concrete operations.
- **Routing** in `WorldServer.RuleHost.cs` `IRuleHost.TryApply`:
  - a vector write, `mix`, or `mean` into an existing framed cell applies on the frame
    and queues the mutation;
  - an absent key goes cross-row;
  - `nearest` and `remember` always go cross-row.

  `FrameHost` applies the frame cases and refuses the rest.
- **Grants.** `WorldStateTransforms.Subjects` names only the row a transform writes.

## Storage, hashing, persistence

- **Canonical JSON.** A cell is `{ "key": "a", "value": "<base64url>" }`, and a slot's
  value is the same string. Regenerate the schemas and generated TypeScript whenever
  the model changes.
- **Hashing.** `WorldRuntimeStateHash.AppendWorld` and `HashCapture` append each vector
  cell's dimensions and bytes, only when vectors exist. `StateFrameHash` already folds
  the frame buffer.
- **Journal, replay, checkpoint, save.** These carry vectors through mutation and
  definition JSON. `WorldAuthorityCheckpointCodec.SupportedVersion` is unchanged, and a
  round-trip test proves it. `WorldSessionCapture.SettleRow` copies vector cells
  unchanged.
- **Disclosure.** `WorldObservedCell` gains `StateVector? Vector`, copied for readers
  and omitted exactly as `Value` is.
- **Echo.** `world.state` prints `vector[<d>] #<StateVector.ComputeDigest as 8 hex>`,
  never components.

## Authoring and `puck embed`

- **Literals.**
  - `embed("text")` resolves through the lock, and `vector("<base64url>")` spells
    bytes.
  - A plain string as a `Vector` cell value or slot default means `embed`.
  - A literal where no vector operand or value is admitted is `PUCK082`.
- **`embeds`.** `table lines : Text embeds(lineVectors[, space: name]) { … }` declares
  the `Text` row and a `Vector` row embedding each line under the same key. `capacity`,
  `evicts`, and `visibility` apply to both. Misuse is `PUCK086`.
- **Lock file.** `<stem>.embeddings.json` sits beside the root source (the file name
  without `.puck`) and covers the root's import graph. A module linted alone is never
  refused for a missing entry. It is written with LF line endings, two-space indentation,
  a trailing newline, and ordinal order:

  ```json
  { "format": 1, "spaces": { "lore": { "dimensions": 256,
    "entries": { "<lowercase hex SHA-256 of UTF-8 text>": { "text": "…", "vector": "<base64url>" } },
    "model": "text-embedding-3-small", "revision": "1" } } }
  ```
- **Hermetic compilation.**
  - `puck compile`, `puck lint`, the language server, and `PuckWorldLoader` call
    `EmbeddingLock.TryLoad(rootSourcePath)` and pass the result to
    `WorldDocumentEmitter.LowerWithDiagnostics(embeddings: …)`. The emitter does no I/O
    for it.
  - `EmbeddingLock` identifies a space by `StateSpace`. `Puck.World.Transpiler` does not
    reference `Puck.Embeddings`.
  - Lowering reports every embedded text it resolved or missed, per space, from both
    dialects. `puck embed` works from that report.
  - A missing entry is `PUCK079` and a stale space is `PUCK080`; both name the text and
    `puck embed`.
  - Compiled JSON holds bytes, so boot never reads the lock and `puck fmt` never does.
- **Decompiling.** `puck decompile` uses `<output stem>.embeddings.json`, or
  `--embeddings <path>`:
  - it prints `embed("text")` when exactly one entry of the space holds the bytes, and
    `vector("…")` otherwise;
  - it prints an `embeds` pair when the two rows share space, keys, capacity, evicts, and
    visibility, and every vector is its text's entry;
  - it omits `space(...)` for a single-space document.
- **`puck embed <path>`** takes a root file, or a directory of roots. It:
  1. lowers the root and exits 2, writing nothing, if lowering has errors;
  2. collects the embedded texts lowering reports per space;
  3. prunes unused entries and spaces;
  4. requests missing or stale texts in batches;
  5. quantizes them;
  6. writes the lock only when its bytes change.
- **`--check`** reports missing, stale, and unused entries, and exits 1 on any without
  contacting a provider.
- **`probe <path> "text" [--space n] [--against table] [--top n]`** ranks similarity
  against a table's vectors or the space's locked texts. It needs a provider only when
  the text itself is not locked.
- **Options:**
  - `--provider fixture|azure-openai`;
  - `--endpoint <url>`, the Azure OpenAI resource;
  - `--deployment <name>`, default the space's `model`;
  - `--omit-dimensions`;
  - `--batch-size <1..2048>`, default 64;
  - `--timeout-seconds <n>`, default 60.

  Exit codes: 0 success, 1 failed check or provider refusal, 2 usage or I/O.
- **Providers** live in `src/Puck.Embeddings` as
  `IEmbeddingGenerator<string, Embedding<float>>` implementations
  (`Microsoft.Extensions.AI`). `EmbeddingIdentity` is `(Model, Revision, Dimensions)`.
  - **`fixture`** (`FixtureEmbeddingGenerator`) answers only model `puck-fixture`.
    Component `i` is signed byte `i mod 32` of
    `SHA-256(model ‖ 0 ‖ revision ‖ 0 ‖ dimensions ‖ 0 ‖ text ‖ 0 ‖ ⌊i/32⌋)`, with
    `-128 → -127`, normalized by `SignedByteVectorFunctions.TryNormalize`. It uses no
    floating point.
  - **`azure-openai`** (`Azure.AI.OpenAI` with `Microsoft.Extensions.AI.OpenAI`) calls
    the deployment's embeddings endpoint, honouring `--omit-dimensions`, and orders
    answers by `index`.
    - It authenticates with a `TokenCredential`, `DefaultAzureCredential` unless one is
      supplied. No provider accepts an API key.
    - An answer count or length mismatch, or a non-finite component, fails.
    - A non-success response fails with its status code and at most 512 characters of
      its body.
    - No message or exception, inner exceptions included, carries a token.
  - Provider answers are quantized with `SignedByteVectorFunctions.TryQuantizeUnit`.

## Writing and inspecting at runtime

- **The mutation.** `WorldMutation.UpsertStateCell` gains `StateVector? Vector`,
  exclusive with `Text`, `RawToken`, `CycleTokens`, and a non-zero `Value`. Admission is
  unchanged.
- **Composition.** `TryComposeCellUpsert` checks the kind, the dimensions, `Set`, and
  reserved keys, then evicts through `StateCellWriter.TryComposeVectorCell`.
- **Writers:**
  - `world.state.cell.set <row> <key> <base64url>` for a live `Vector` row;
  - `WorldAgentBridge.WriteVectorAsync(string row, string? key, ReadOnlyMemory<sbyte> components, CancellationToken)`;
  - the MCP tool `puck_state_vector_write` (`row`, `key`, `vector` as base64url), which
    runs `world.state.cell.set`.

  Agents quantize with `TryQuantizeUnit`.
- **Inspection:**
  - **`world.state.similar <row> <key> <table> [top]`** prints ranked `similarity` and
    `dot`. It has immediate routing, reads through the caller's visibility, and never
    writes.
  - **`world.rule.trace`** prints every vector call's value, what `nearest` wrote, and
    `remember`'s verdict with its matching key.

## Runtime text embedding

**Configuration.** `WorldExtensionConfiguration` gains
`IReadOnlyList<WorldExtensionEmbeddingSettings>? Embeddings`. All other members follow
the existing configuration:

```json
"embeddings": [
  { "name": "chat", "provider": "local", "client": "addon:embedder", "space": "lore",
    "requests": "said", "results": "saidVectors", "status": "saidStatus",
    "maximumItems": 64, "batchSize": 64, "retryTicks": 1200, "cacheEntries": 4096 }
]
```

- **Provider types.**
  - `embedding.azure-openai` takes `endpoint`, `deployment`, `model`, `revision`,
    `dimensions`, and `omitDimensions`, and authenticates with
    `DefaultAzureCredential`.
  - `embedding.fixture` takes `model: "puck-fixture"`, `revision`, and `dimensions`.
- **Identity check.** Composition refuses unless the provider identity equals the
  space's.
- **Tables.**
  - `requests` is an ordinary `Text` table, `results` an ordinary `Vector` table in the
    space, and `status` an optional ordinary `Int` table.
  - Each has capacity of at least `maximumItems` and belongs to one connection.
- **Bounds:** at most 16 connections; `maximumItems` in `[1, 128]`; `batchSize` in
  `[1, 2048]`; `retryTicks ≥ 1`; `cacheEntries` in `[0, 65536]`.

**Delivery.** Each `scanEveryTicks` scan, for each connection with no call in flight:

1. Read `requests` through `Observe`, skipping hidden cells.
2. Select keys with no result, or whose text differs from the text last submitted,
   up to `maximumItems` in cell order. Write `status = 0` for each.
3. Answer from a least-recently-used cache keyed by model, revision, dimensions, and
   the text's SHA-256.
4. Call the provider for the rest, in batches, on the shared worker.
5. Submit one recorded `WorldMutation.Batch` through `WorldRecordedExtension.Submit`,
   containing:
   - vector upserts with `status = 3`;
   - `status = 4` for refused keys;
   - an `ExpectedCells` guard on each request's text, so a request that changed
     mid-call is dropped and selected again.
6. A failed key waits `retryTicks` or a text change.

**Lifecycle.**
- The connection never removes result or status cells.
- Nothing enters `WorldExternalOperationJournal`.
- After a restart, unanswered requests are selected again. Live replay revokes the
  client.
- The configuration file is the operator's approval to send request text to the
  endpoint.

**Read-back.** `world.extensions` prints, per connection:
- the provider identity and space;
- selected, cached, submitted, and failed counts;
- the last call's tick, and whether a call is in flight;
- the last failure's exception type.

**Code.**
- `Puck.World.Server/WorldExtensionEmbedding.cs`:
  - `IWorldConfiguredEmbeddingProvider.BindEmbedding(JsonElement)` returns an owned
    `IWorldEmbeddingSource`;
  - that source exposes `EmbeddingIdentity Identity` and
    `EmbedAsync(IReadOnlyList<string>, CancellationToken)`, returning one
    `EmbeddingAnswer` (a `StateVector` or a bounded refusal) per text.
- Delivery lives in `WorldConfiguredExtensions.Embeddings.cs`.
- `src/Puck.World.Embeddings` registers both provider types the way
  `AzureWorldExtension` does.

## Performance contract

- **Worlds without vector rows** do no new per-tick work, allocate no vector buffer, and
  keep their `stateHash`, parity hashes, and `puck landing` output.
- **Reads and writes.** Vector reads allocate nothing. A vector write or transform
  allocates only its queued mutation and `StateVector`.
- **Measurement.** Report, with the change:
  - flagship tick timing, with no regression beyond noise;
  - `nearest` over 256 × 256 costing under 100 µs in Release, including the cross-row
    compose.

# Part 2: remaining implementation map

- **`CellKind` switches.** Every `switch` on `CellKind` handles `Vector` explicitly;
  find them with `puck references` on `Puck.State.CellKind`.
- **File length.** Keep every file under the length ledger.

## New projects

- **`src/Puck.Embeddings`** references `Puck.Maths`, `Microsoft.Extensions.AI`,
  `Microsoft.Extensions.AI.OpenAI`, `Azure.AI.OpenAI`, and `Azure.Identity`. It holds the generators, `EmbeddingIdentity`,
  batching, and quantization. Only `Puck.Cli` and `Puck.World.Embeddings` reference it.
- **`src/Puck.World.Embeddings`** references `Puck.World.Server` and `Puck.Embeddings`,
  in the layer of `Puck.World.Azure`. Install it wherever `Puck.World.Azure` is
  installed.
- Each declares `PuckKind` and `PuckLayer` and has a routing README. Regenerate
  `docs/project-map.md` with `puck architecture --map`.

## Transpiler and CLI

- **`Puck.World.Transpiler`**, new `Lowering/WorldDocumentEmitter.Embeddings.cs`:
  - the `spaces` block and default space;
  - `Vector` in `AdmittedCellKinds`;
  - the `space`, `evicts`, and `embeds` modifiers;
  - string, `embed`, and `vector` values, and vector operands in rules and transforms,
    with space inference;
  - `mix` terms as object literals;
  - ceilings;
  - `LowerWithDiagnostics(embeddings: …)`.
- **Lock model.** New `Embeddings/EmbeddingLock.cs`, with `TryLoad`, `TryGet`,
  `TryFindText`, and a deterministic `Write`.
- **Decompiler** (`WorldDecompiler.State.cs` and the rule and transform printers):
  vector rows, spaces, lock-aware literals, `embeds` pairs, `evicts`, vector operands,
  and the four transforms.
- **Language server.** New `Lsp/PuckEmbeddingLsp.cs`:
  - completions and hover for `spaces`, `space`, `Vector`, `evicts`, `embeds`,
    `embed`, `vector`, `dot`, `similarity`, `identical`, `mix`, `mean`, `nearest`,
    and `remember`;
  - document symbols for spaces;
  - hover on embedded text showing its lock status and its three nearest locked texts.
- **Linter:** `PUCK_LINT_010`.
- **`Puck.Transpiler` document-time evaluator:** refuse the vector functions with
  `PUCK041`.
- **`Puck.Cli`:**
  - a new `Transpiler/EmbedCommand.cs` (`PuckEmbedCommand`), registered in
    `PuckRootCommand.Create()`;
  - `CompileCommand`, `LintCommand`, `LspCommand`, and `PuckWorldLoader` load the lock;
  - `DecompileCommand` gains `--embeddings`.
- **Samples:**
  - `Samples/embeddings.world.puck` with its lock (model `puck-fixture`);
  - `Samples/embeddings-runtime.world.puck` with its lock;
  - `src/Puck.World/Assets/hosting/embeddings.extensions.example.json`, using
    `embedding.fixture`.
- **Dashboard.** Run `npm run types:generate` in `src/Puck.Dashboard/src/portal`.

## Diagnostics

| Code | Constant | Meaning |
|---|---|---|
| `PUCK077` | `EmbeddingSpaceInvalid` | A space is malformed: an unknown or missing field, dimensions out of range, a duplicate name, or over the limit. |
| `PUCK078` | `EmbeddingSpaceUnknown` | A `Vector` row has no space and no default, or names an undeclared space; or a non-`Vector` row names a space. |
| `PUCK079` | `EmbeddingLockMissing` | An embedded text has no lock entry; run `puck embed`. |
| `PUCK080` | `EmbeddingLockStale` | The lock's model, revision, or dimensions differ from the space. |
| `PUCK081` | `VectorLiteralInvalid` | A `vector` literal is not base64url, has the wrong length, holds `-128`, or fails admission. |
| `PUCK082` | `VectorLiteralMisplaced` | `embed` or `vector` appears where no vector operand or value is admitted. |
| `PUCK083` | `VectorRowTooLarge` | A row or section byte ceiling is exceeded. |
| `PUCK084` | `VectorOperandMismatch` | A non-vector operand, a space mix, or a `nearest`/`remember` shape, `k`, or threshold that isn't admitted. |
| `PUCK085` | `EmbeddingSpaceAmbiguous` | An `embed` literal has no space source. |
| `PUCK086` | `EmbedsInvalid` | `embeds` is on a non-`Text` table, or its name collides. |
| `PUCK087` | `VectorMixInvalid` | A `mix` term count or weight is out of range, or a weight is zero. |
| `PUCK088` | `VectorFilterInvalid` | `where` is not a keyed `Bool` row, or `exclude` is malformed. |
| `PUCK_LINT_010` | `VectorMixStall` | A literal `mix` weight's share is below 1/64. |

Refused traits reuse `PUCK055`. A `Vector` kind outside `table`/`slot` reuses `PUCK063`.

## `.puck` examples

Authored content:

```puck
state {
    spaces {
        space lore { model: "text-embedding-3-small"  revision: "1"  dimensions: 256 }
    }
    world {
        table events : Vector {
            ambush = "Bandits ambushed the caravan on the north road"
            gift = "A stranger shared bread and water with the guards"
        }
        table memories : Vector capacity(128) evicts { }
        table stance : Vector capacity(4) {
            guards = "Wary but fair"
        }
        table lines : Text embeds(lineVectors) {
            warn = "Stay close to the wagons tonight."
            thank = "Your kindness will not be forgotten."
        }
        slot situation : Vector = "Travellers approach the gate at dusk"
        table recalled : Fixed capacity(3) { }
        slot reply : Text = ""
        slot caravanAttacked : Int = 0
        slot hostile : Int = 0
    }
}

rule "remember-ambush" {
    mode: Edge
    when caravanAttacked == 1
    transform note = remember(into: memories, key: "ambush", from: "events[ambush]", unlessWithin: 0.9)
    situation = events[ambush]
    transform drift = mix(into: "stance[guards]", terms: [
        { from: "stance[guards]", weight: 3 }
        { from: "events[ambush]", weight: 1 }
    ])
    transform recall = nearest(from: memories, query: "situation", into: recalled, k: 3, threshold: 0.5)
    transform speak = nearest(from: lineVectors, query: "situation", into: reply, k: 1)
}

rule "turn-hostile" {
    mode: Edge
    when similarity(stance[guards], embed("danger and betrayal")) > 0.55
    hostile = 1
}
```

Runtime text:

```puck
state {
    spaces {
        space lore { model: "puck-fixture"  revision: "1"  dimensions: 256 }
    }
    world {
        table said : Text capacity(32) evicts { }
        table saidVectors : Vector capacity(32) evicts { }
        table saidStatus : Int capacity(32) evicts { }
        table intents : Text embeds(intentVectors) {
            directions = "Where is the road north?"
            rumours = "Have you heard anything strange lately?"
            threat = "Hand over your coin or else"
        }
        table answers : Text {
            directions = "Follow the river until the old mill."
            rumours = "Lights in the marsh, three nights running."
            threat = "Guards!"
        }
        slot heard : Text = ""
    }
}

rule "understand" {
    forEach: "saidStatus"
    mode: Edge
    when saidStatus[$each] == 3
    transform intent = nearest(from: intentVectors, query: "saidVectors[$each]", into: heard, k: 1, threshold: 0.6)
}
```

A reply rule speaks `answers[$cell:heard:$value]`. Every form lowers byte-identically to
its JSON, and the decompiler prints it back when the lock is present.

# Part 3: tests and commands

## Tests to add

Every existing suite stays green, including the vector tests listed under Implemented
and the tests the open defects add. Add:

- **`tests/Puck.World.Tests`:**
  - `EmbeddingConnectionLawTests` (`embedding.fixture`):
    - composition refusals;
    - request to vector with status 3 in one batch;
    - a cache hit makes no call;
    - a changed text is never answered stale;
    - a refusal gives status 4 and waits `retryTicks`;
    - removing a request stops work;
    - replay reproduces bytes without the provider, with identical `stateHash`;
    - live replay revokes the client;
    - a `ChatCommandModule` line is embedded.
- **`tests/Puck.World.Transpiler.Tests`:**
  - `EmbeddingDeclaration{Parser,Emitter,Decompiler,Lsp,Formatter}Tests`: every
    declaration, literal, operand, transform, and function; every diagnostic with its
    span; lock-aware round trips;
  - a native `embed` literal lowering to the same bytes as the SQL literal;
  - `SamplesCompileTests` picks up both samples.
- **`tests/Puck.Cli.Tests`:** `EmbedCommandTests` for a native document (texts from
  `embed`, plain-string `Vector` values, and `embeds`), and `puck decompile` with and
  without the lock.

## Commands

Run these in order. Each must exit 0, except `Puck.World.Tests` and `Puck.Cli.Tests`,
which already fail tests unrelated to embeddings: record each suite's failing set before
starting, and finish with nothing failing outside it.

```bash
dotnet build -c Release
```

```bash
dotnet test -c Release tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj
```

```bash
dotnet test -c Release tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj --settings tests/Puck.Maths.Tests/deep.runsettings
```

```bash
dotnet test -c Release tests/Puck.State.Tests/Puck.State.Tests.csproj
```

```bash
dotnet test -c Release tests/Puck.World.Schema.Tests/Puck.World.Schema.Tests.csproj
```

```bash
dotnet test -c Release tests/Puck.World.Tests/Puck.World.Tests.csproj
```

```bash
dotnet test -c Release tests/Puck.World.Browser.Tests/Puck.World.Browser.Tests.csproj
```

```bash
dotnet test -c Release tests/Puck.World.Agents.Tests/Puck.World.Agents.Tests.csproj
```

```bash
dotnet test -c Release tests/Puck.World.Protocol.Tests/Puck.World.Protocol.Tests.csproj
```

```bash
dotnet test -c Release tests/Puck.World.Transpiler.Tests/Puck.World.Transpiler.Tests.csproj
```

```bash
dotnet test -c Release tests/Puck.Cli.Tests/Puck.Cli.Tests.csproj
```

```bash
puck compile src/Puck.World/Assets/worlds/moth-courtyard.puck --validate
```

```bash
puck schema --check
```

```bash
npm --prefix src/Puck.Dashboard/src/portal run check:types
```

```bash
puck embed --check src/Puck.World.Transpiler/Samples/embeddings.world.puck
```

```bash
puck embed probe src/Puck.World.Transpiler/Samples/embeddings.world.puck "Stay close to the wagons tonight." --against lineVectors
```

```bash
puck compile src/Puck.World.Transpiler/Samples/embeddings.world.puck --validate
```

```bash
puck fmt src/Puck.World.Transpiler/Samples --check
```

```bash
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World.Transpiler/Samples/embeddings.world.puck --exit-after-seconds 2
```

```bash
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World.Transpiler/Samples/embeddings-runtime.world.puck --extensions-config-file src/Puck.World/Assets/hosting/embeddings.extensions.example.json --exit-after-seconds 5
```

```bash
puck parity
```

```bash
puck landing
```

```bash
puck lengths
```

```bash
puck architecture --map
```

```bash
puck architecture --check
```

# Part 4: complete when

- **Defects:** the open defects are fixed and every test above passes, including the
  Maths laws at the deep tier.
- **Shipped worlds** keep their `stateHash`, parity hashes, and canary output. The
  performance measurements are reported with the change.
- **Authored sample:** it boots.
  - After `world.state.cell.set caravanAttacked $value 1`, `world.rule.trace` shows
    `remember-ambush` and `turn-hostile` with their similarity values.
  - `world.state recalled` lists `ambush`, and `world.state reply` names a line key.
  - `world.state.similar stance guards events` ranks both events.
- **Runtime sample:** it boots with the example configuration. After
  `world.state.cell.set said hello Where is the road north?`, `world.state saidStatus`
  shows `hello = 3` within the scan cadence, and `world.rule.trace` shows `understand`.
- **Tooling:** `puck embed --check` passes offline, and fixture locks regenerate
  byte-identically.
- **Docs**, each updated in the same change:
  - `docs/reference/state/data-model.md`: the `Vector` kind, spaces, ceilings,
    quantized drift and `mean`, and model-change recovery;
  - `docs/reference/state/expressions.md`: `dot`, `similarity`, `identical`, and the
    `cosine` distinction;
  - `docs/reference/state/rules.md`: operands, copy, the four transforms, and `Text`
    key indirection;
  - `docs/reference/state/frames.md`: the vector buffer and routing;
  - `docs/reference/dsl.md`: declarations, `embeds`, and literals;
  - `docs/reference/cli.md`: `puck embed`, `probe`, and `puck decompile --embeddings`;
  - `src/Puck.World.Transpiler/README.md`, including the SQL vector forms and
    diagnostics;
  - `src/Puck.Maths/README.md` and `docs/reference/maths.md`;
  - `src/Puck.World.Server/ExtensionConfiguration.md` ("Embed text at runtime") and
    `ExtensionHosting.md`;
  - the `Puck.Embeddings` and `Puck.World.Embeddings` READMEs;
  - the `puck-dsl` skill (`SKILL.md`, `references/grammar.md`,
    `references/diagnostics.md`) and the `puck-world` skill
    (`references/documents-state.md`);
  - `docs/project-map.md`, regenerated;
  - `docs/plans/state-sql.md`, marked complete.
- **Plan status:** this plan's status names the commit the work landed on.
