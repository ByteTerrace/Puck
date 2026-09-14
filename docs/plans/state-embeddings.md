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

Checked against `e19bb5b3b` plus the uncommitted working tree.

### Implemented

Keep the following, and build on it without reworking it.

- **SQL dialect and tooling.**
  - `DocumentVocabularyResolver` (in `Puck.Transpiler`) chooses a vocabulary from the
    top-level `schema:` token. `CliVocabularyResolver` supplies it to `compile`,
    `lint`, and `lsp`, and `PuckLanguageServer` takes it by constructor.
  - `WorldDecompiler.Sql.cs` projects to SQL only what re-lowers identically, and it
    verifies rules against the whole candidate document.
  - The SQL vector spelling — `VECTOR(space)`, `EVICTS`, `embed(...)`,
    `vector(...)`, `dot`/`similarity`/`identical`, and `INSERT … SELECT … ORDER BY
    … <=> … LIMIT` lowering to a `transformState`-wrapped `nearest` — parses,
    lowers, and decompiles, with tests.
- **Maths.** `SignedByteVectorFunctions` has `Dot` and `SumOfSquares` (SIMD rungs
  with a scalar reference, lanes folded every 256 blocks), plus `CosineQ16`,
  `AdmissionTolerance`, `IsUnitAdmissible`, `TryNormalize`, and `TryQuantizeUnit`.
  The six `signed-byte-vectors.*` laws pass at the default tier.
- **State types:**
  - `CellKind.Vector` and `StateValueKind.Vector`;
  - `StateVector`, with byte equality and a base64url JSON converter;
  - `StateSpace`, with `HasSameIdentity`;
  - `StateCell.Vector` and `StateRow.Space`;
  - `IStateSection.Spaces`, `StateSection.Spaces`, and `WorldStateSection.Spaces`;
  - the `StateCapacity` vector constants;
  - JSON arms for vector cells and `space`, and a schema branch that forbids `min`
    and `max` on vectors.
- **Frame:**
  - `FrameRowKind.Vector` with `VectorOffset`, `Dimensions`, `VectorCells`, and
    `VectorCellStart`, which `Fits` compares;
  - one contiguous `sbyte` buffer, with a per-cell load cache, zeroing, a byte
    journal behind a `long`-journal sentinel, `CopyFrom`, and the `StateFrameHash`
    fold;
  - `TryStoredVector`, which falls back to row cells when a row is not framed;
  - `TryWriteVector`;
  - `TryApplyVector` for `ResolvedVectorTransform.Copy`, `Mix`, and `Mean`, without
    allocation.

  `WorldServer.RuleFrame`, `WorldServer.Step` (search), and `BrowserSession` pass
  `WorldStateSpaces.Find` as the space resolver. Tests: `VectorFrameTests`.
- **Transforms and resolved forms:**
  - `StateTransform.Mix`, `Mean`, `Nearest`, `Remember`, and `VectorTerm`;
  - the `VectorTransforms` kernels (`TryMix`, `TryMean`, `SelectNearest`,
    `TryRemember`);
  - `ResolvedVectorTransform` (thresholds pre-parsed to raw values);
  - `StateMutation.ApplyVector` and `StateMutation.UpsertCell.Vector`, which
    `FrameHost` routes.

  Tests: `VectorTransformTests`.
- **Expression and effect types, not yet used by the compiler:**
  - `ExpressionOp.Dot`, `Similarity`, and `Identical`;
  - `ValueToken.VectorCall` with `VectorOperandToken`, parsed by `ExpressionSpelling`
    (including `vector("…")`);
  - `CompiledVectorOperand` and `VectorCallOperand`;
  - `VectorCopyEffect`, `VectorMixEffect`, `VectorMeanEffect`, `VectorNearestEffect`,
    and `VectorRememberEffect`;
  - `CompiledCellRef.Kind` (for `Text` key indirection);
  - `ActionEffect.SetState.Vector`.
- **Refusals and codes.** The vector `RuleRefusal` members exist with their doors, and
  `PuckDiagnosticCodes` holds `PUCK077`–`PUCK088` and `PUCK_LINT_010` exactly as
  tabled in Part 2.
- **Names.** `WorldNameRegistry` registers the transform fields.

### Open defects in existing code

Fix these first. Each has a done condition.

1. **Effect costs don't match the cost table in Part 1.** The facts return
   `2 + dimensions` (copy), `2 + terms × dimensions` (mix),
   `2 + capacity × dimensions` (mean and remember), and
   `2 + capacity × dimensions + k × capacity` (nearest). Make every
   `EffectFact.Cost` and `VectorCallOperand.Cost` return exactly the table's value,
   including the `similarity` factor of 3 for `nearest` into `Fixed`/`Text` and for
   `remember`. **Done** when a test asserts each cost at 256 dimensions.
2. **`CompiledVectorOperand.TryReadSpan` re-parses literal keys on every read.** Use
   `CellKey` for literal and slot keys, and parse only a dynamically resolved key.
   **Done** when a literal-key `dot` evaluates with no allocation.
3. **SQL `embed(...)` is a stub.** `WorldDocumentEmitter.Sql.cs` writes `""` into
   `vector` and `embed("…")` into `query` when no lock entry resolves. Resolve
   through `EmbeddingLock` (work step 5), and write only `vector("<base64url>")` or
   raw base64url into JSON. **Done** when a SQL `embed` literal lowers to the same
   bytes as the native literal.
4. **A stale comment.** The `SqlFunctionCallExpression` comment in `StateSqlAst.cs`
   names `cosine`; it should name `similarity`.

### Not started

- rule compilation and evaluation;
- `Text` key indirection;
- the schema validator and composition checks;
- the world server's mutation, routing, sparse transforms, and hashing;
- console, agent bridge, MCP, and HUD;
- the `Puck.Embeddings` and `Puck.World.Embeddings` projects;
- `puck embed` and the lock file;
- native `.puck` lowering, decompiler, and language server for vectors;
- runtime embedding connections;
- samples and docs.

## Work order

1. Fix the open defects 1, 2, and 4.
2. **Rule compilation and evaluation.** Compile `VectorCall`, copy, `mix`, `mean`,
   `nearest`, and `remember` into the existing facts. Add `Text` key indirection.
   Have evaluation submit `ApplyVector` or `UpsertCell.Vector`.
3. **`Puck.World.Schema`:** validation, composition, disclosure, and the remaining
   `CellKind` arms.
4. **`Puck.World.Protocol` and `Puck.World.Server`:** the mutation, compose, routing,
   sparse transforms, and hashing. Then the console, agent bridge, MCP, HUD, and
   browser surfaces.
5. **`Puck.Embeddings`, `EmbeddingLock`, and `puck embed`** (`--check`, `probe`).
   This also fixes defect 3.
6. **Native `.puck`:** lowering, decompiler, language server, and linter.
7. **Runtime embedding connections** and `Puck.World.Embeddings`.
8. **Finish:** samples, docs, and the full command list in Part 3.

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
- An in-process model (ONNX and similar); only the fixture and HTTP providers ship.
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
- **Ceilings.** `capacity × dimensions ≤ 65536` per row, and at most 4 MiB across all
  vector rows.
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
both rows are known (`VectorOperandNotVector`, `VectorSpaceMismatch`), and at the
validator for authored transforms.

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
| `CREATE TABLE t (key TEXT PRIMARY KEY, embedding VECTOR(lore)) CAPACITY 128 EVICTS` | a `Vector` row |
| `INSERT INTO t (key, embedding) VALUES ('a', embed('text'))` | a vector cell; `vector('…')` spells bytes |
| `WHERE similarity(a.embedding, b.embedding) > 0.6`, `dot(...)`, `identical(...)` | expression functions |
| `UPDATE t SET embedding = u.embedding WHERE …` | copy |
| `INSERT INTO r (key, score) SELECT key, similarity(embedding, q.embedding) FROM t [WHERE …] ORDER BY embedding <=> q.embedding [DESC] LIMIT k` | `nearest`; `WHERE similarity(...) >= x` is `threshold`, `DESC` is `farthest`, a `Bool` column is `where`, `key <> 'x'` is `exclude` |

`mix`, `mean`, and `remember` have no SQL spelling.

## Rule compilation and application

- **Operands.** `RuleCompiler` resolves every vector operand once into a
  `CompiledVectorOperand`, checking spaces and cost as it does.
- **Expression calls.** A `VectorCall` compiles to one `CompiledExpressionToken` whose
  operand is a `VectorCallOperand`. `RuleWorkBudget.ExpressionCost` uses that
  operand's cost.
- **Effects.** Copy and the four transforms compile to the `Vector*Effect` facts. At
  evaluation each resolves keys against the evaluator's bindings and submits
  `StateMutation.ApplyVector` (or `UpsertCell.Vector` for a literal or copied write),
  with concrete ordinals, keys, and `StateVector` constants.
- **Journal and replay.** `WorldServer.RuleFrame.MapStateMutation` maps the resolved
  form to `WorldMutation.TransformState` or `UpsertStateCell`, naming literal keys and
  `vector("…")` literals, so the journal and replay record concrete operations.
- **Routing** in `WorldServer.RuleHost.cs` `IRuleHost.TryApply`:
  - copy, `mix`, or `mean` into an existing framed cell applies on the frame and queues
    the mutation;
  - an absent key goes cross-row;
  - `nearest` and `remember` always go cross-row.

  `FrameHost` applies the frame cases and refuses the rest.

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
  { "format": 1, "spaces": { "lore": { "model": "text-embedding-3-small", "revision": "1", "dimensions": 256,
    "entries": { "<lowercase hex SHA-256 of UTF-8 text>": { "text": "…", "vector": "<base64url>" } } } } }
  ```
- **Hermetic compilation.**
  - `puck compile`, `puck lint`, the language server, and `PuckWorldLoader` call
    `EmbeddingLock.TryLoad(rootSourcePath)` and pass the result to
    `WorldDocumentEmitter.LowerWithDiagnostics(embeddings: …)`. The emitter does no I/O
    for it.
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
  1. collects embedded texts per space;
  2. prunes unused entries and spaces;
  3. requests missing or stale texts in batches;
  4. quantizes them;
  5. writes the lock only when its bytes change.
- **`--check`** reports missing, stale, and unused entries, and exits 1 on any without
  contacting a provider.
- **`probe <path> "text" [--space n] [--against table] [--top n]`** ranks similarity
  against a table's vectors or the space's locked texts. It needs a provider only when
  the text itself is not locked.
- **Options:**
  - `--provider fixture|openai-compatible`;
  - `--endpoint <url>`;
  - `--api-key-env <NAME>`;
  - `--api-key-header <name>`: default `Authorization` with `Bearer`; `api-key` sends
    the raw key;
  - `--omit-dimensions`;
  - `--batch-size <1..2048>`, default 64;
  - `--timeout-seconds <n>`, default 60.

  Exit codes: 0 success, 1 failed check or provider refusal, 2 usage or I/O.
- **Providers** live in `src/Puck.Embeddings` and implement
  `IEmbeddingProvider.EmbedAsync(EmbeddingIdentity identity, IReadOnlyList<string> texts, CancellationToken)`,
  returning one `double[]` per text. `EmbeddingIdentity` is `(Model, Revision, Dimensions)`.
  - **`fixture`** answers only model `puck-fixture`. Component `i` is signed byte
    `i mod 32` of
    `SHA-256(model ‖ 0 ‖ revision ‖ 0 ‖ dimensions ‖ 0 ‖ text ‖ 0 ‖ ⌊i/32⌋)`, with
    `-128 → -127`, then normalized.
  - **`openai-compatible`** posts `{ model, input[], dimensions, encoding_format: "float" }`
    to `<endpoint>/embeddings` and reads `data[].embedding` by `index`.
    - A count or length mismatch, a non-finite value, or a non-success status fails
      with the status and at most 512 characters of body.
    - The key is read only from the named environment variable and never printed.

## Writing and inspecting at runtime

- **The mutation.** `WorldMutation.UpsertStateCell` gains `StateVector? Vector`,
  exclusive with `Text`, `RawToken`, `CycleTokens`, and a non-zero `Value`. Admission is
  unchanged.
- **Composition.** `TryComposeCellUpsert` checks the kind, the dimensions, `Set`, and
  reserved keys, then evicts through `StateCellWriter.TryComposeVectorCell`.
- **Writers:**
  - `world.state.cell.set <row> <key> <base64url>` for a live `Vector` row;
  - `WorldAgentBridge.WriteVectorAsync(string row, string? key, ReadOnlyMemory<sbyte> components, CancellationToken)`;
  - `RemoteMcpHost` tool `puck_state_vector_write` (`row`, `key`, `vector`).

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
  - `embedding.openai-compatible` takes `endpoint`, `model`, `revision`, `dimensions`,
    `apiKeyEnvironment`, `apiKeyHeader`, and `omitDimensions`.
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
  allocates only the queued `StateVector`.
- **Measurement.** Report, with the change:
  - flagship tick timing, with no regression beyond noise;
  - `nearest` over 256 × 256 costing under 100 µs in Release, including the cross-row
    compose.

# Part 2: remaining implementation map

- **`CellKind` switches.** Every `switch` on `CellKind` handles `Vector` explicitly;
  find them with `puck references` on `Puck.State.CellKind`.
- **File length.** Keep every file under the length ledger.

## `Puck.State`

- **Compilation** (`RuleCompiler.Expressions.cs`, `RuleCompiler.Effects.cs`,
  `RuleCompiler.Operands.cs`):
  - compile `VectorCall` and vector destinations, and the four transforms, into
    `VectorCallOperand` and the `Vector*Effect` facts;
  - refuse arithmetic effects on vector rows;
  - validate `mix` terms, `where` rows, `exclude`, `k`, and thresholds.
- **Evaluation** (`RuleEvaluation.cs`, `RuleEvaluator.Effects.cs`):
  - the effects submit `ApplyVector` or `UpsertCell.Vector`;
  - `Text` key indirection resolves through `CompiledCellRef.Kind`;
  - `RuleWorkBudget.ExpressionCost` uses the operand's cost.
- **Composition helper.** `StateCellWriter.TryComposeVectorCell`.
- **Trace.** Records for vector calls, `nearest` results, and `remember` verdicts.

## `Puck.World.Schema`

- **`WorldDefinitionValidator.State.cs`:**
  - spaces;
  - that each `Vector` row names a space, and no other kind does;
  - the trait, domain, and effect refusals;
  - the row and section ceilings;
  - per-cell dimensions;
  - the HUD refusal.
- **`WorldDefinitionValidator.Mutations.cs`:** validation of the four transforms.
- **Composition** (`WorldDocumentBasis.cs`, `WorldImport.cs`, and owned-identity
  composition): refuse space identity mismatches.
- **`WorldStateDisclosure.cs`:** `WorldObservedCell.Vector`.
- **Other `CellKind` sites:** `Vector` arms in `WorldStateBindingContext.cs`,
  `BindableValue.cs`, `WorldStateDocumentValues.cs`, `WorldOperandKinds.cs`, and
  `WorldSearch.cs`.

## `Puck.World.Protocol`, `Puck.World.Server`, surfaces

- **`WorldMutation.UpsertStateCell.Vector`** and the vector arm of
  `TryComposeCellUpsert`.
- **`WorldServer.RuleFrame.cs` `MapStateMutation`:** map `ApplyVector` and
  `UpsertCell.Vector`.
- **`WorldServer.RuleHost.cs`:** routing.
- **`WorldStateTransforms.cs`:** add the touched-rows and apply arms, with sparse
  implementations in a new `WorldStateTransforms.Vectors.cs`.
- **`WorldRuntimeStateHash.cs`:** vector folding.
- **`WorldStateCommandModule.cs`:** digest echo, base64url `cell.set`, space in
  `DescribeRow`, `world.state.similar`, and trace output.
- **`WorldHudBindingResolver.cs`:** an explicit `Vector` arm, and a throwing `default`.
- **`WorldAgentBridge.WriteVectorAsync`** and **`RemoteMcpHost`
  `puck_state_vector_write`.**
- **Browser.** `BrowserRuleReader.cs` and `BrowserJsonConverters.cs` get `Vector`
  arms.
- **Extensions.** The `WorldExtensionConfiguration.Embeddings` settings,
  `WorldExtensionEmbedding.cs`, `WorldConfiguredExtensions.Embeddings.cs`, and the
  `world.extensions` read-back.

## New projects

- **`src/Puck.Embeddings`** references only `Puck.Maths`. It holds the providers,
  `EmbeddingIdentity`, batching, and quantization.
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

Existing suites (the Maths laws, `VectorFrameTests`, `VectorTransformTests`,
`DocumentVocabularyTests`, and the SQL emitter, decompiler, and LSP tests) stay green.

- **`tests/Puck.State.Tests`:**
  - `StateVectorTests` and `StateVectorJsonTests`;
  - `VectorExpressionTests`: `dot`, `similarity`, and `identical` in a gate, a binding,
    an effect operand, and a `Score` program; literals with space inference;
    absent-cell failure; mismatch refusals; `cosine(x)` still evaluating as trig in the
    same rule; exact costs;
  - `VectorEffectTests`: compiled copy, `mix` over `$each`, `mean` with `where`,
    `nearest` into `Int`/`Fixed`/`Text` (ties, both threshold directions, `where`,
    `exclude`, `farthest`, fewer than `k`, empty source, `k` bounds), `remember`
    (skips a near-duplicate, ignores its own key), and each effect's exact cost;
  - `TextKeyIndirectionTests`, `VectorRefusalTests` (every trait, domain, effect, and
    refusal door), and `VectorTraceTests`.
- **`tests/Puck.World.Schema.Tests`:** `WorldStateSpacesValidationTests` (spaces,
  default space, ceilings, composition mismatch) and `WorldStateDisclosureVectorTests`.
- **`tests/Puck.World.Tests`:**
  - `VectorStateLawTests`:
    - granted and ungranted writes;
    - undo;
    - checkpoint round trip;
    - `world.save` and reload;
    - an evicting table replayed twice with identical `stateHash`;
    - frame `mix` equal to the cross-row path;
    - `nearest` and `remember` cross-row;
    - a 65536-byte row through a Presentation-tier projection;
    - digest echo;
    - `world.state.similar` and its visibility.
  - `ShippedWorldHashStabilityTests`: shipped worlds have empty vector buffers and
    unchanged authoritative hashes.
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
- **`tests/Puck.World.Browser.Tests`:** `StateHash` covers vector bytes; `nearest` and
  `remember` are refused.
- **`tests/Puck.World.Agents.Tests`:** `WriteVectorAsync` with granted and ungranted
  principals.
- **`tests/Puck.World.Transpiler.Tests`:**
  - `EmbeddingDeclaration{Parser,Emitter,Decompiler,Lsp,Formatter}Tests`: every
    declaration, literal, operand, transform, and function; every diagnostic with its
    span; lock-aware round trips;
  - a SQL `embed` literal lowering to the same bytes as the native literal;
  - `SamplesCompileTests` picks up both samples.
- **`tests/Puck.Cli.Tests`:**
  - `EmbedCommandTests`: byte-identical fixture locks; pruning; offline `--check` and
    `probe`; offline compile; `PUCK079`; decompile with and without the lock;
  - `OpenAiCompatibleEmbeddingProviderTests` against an in-process `HttpListener`:
    request shape, `index` order, both key headers, `--omit-dimensions`, failure exits,
    and the key never printed.

## Commands

Run these in order; each must exit 0.

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
