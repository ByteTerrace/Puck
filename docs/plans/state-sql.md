# SQL-flavored state authoring

Puck's state model — keyed rows, slots, traits, domains, visibility, and the rules
that write them — is powerful and unfamiliar. Most developers already think in
tables, columns, `UPDATE … WHERE`, and constraints. This plan adds a SQL-flavored
dialect to `.puck` documents that compiles to exactly the rows and rules the
native syntax already produces.

The dialect is not SQL. There is no database, no query engine, and nothing runs
at compile time except lowering. It borrows SQL's spelling wherever SQL's meaning
matches Puck's, uses a clearly Puck-flavored clause wherever it does not, and
never gives familiar SQL syntax a different meaning. The goal is that a developer
reading or writing it feels at home, and that every refusal teaches the Puck
concept behind it.

The [world transpiler guide](../../src/Puck.World.Transpiler/README.md) owns
current language behavior and becomes the owner of this dialect's reference once
it lands. The [state reference](../reference/state.md) owns the row, cell, trait,
and rule model this dialect spells. [Concise state authoring](state-authoring.md)
owns the native `table`/`slot`/`pile`/`grid` declarations this dialect must lower
identically to.

## Implementation status

**Status:** Complete. Checked against `239770787`.

The dialect is implemented:

- **Parsing and lowering.** The lexer and parser live in
  `src/Puck.World.Transpiler/Sql/`. `WorldDocumentEmitter.Sql.cs` lowers to native
  rows and rules.
- **Vocabulary.** `DocumentVocabularyResolver` chooses the vocabulary from the
  document's top-level `schema:` for `compile`, `lint`, and `lsp`.
- **Decompiling.** `WorldDecompiler.Sql.cs` projects to SQL only what re-lowers
  identically in the context of the whole document. `ShippedWorldsParityTests`
  asserts zero diagnostics under `--sql`.
- **Language server.** `PuckSqlLsp.cs` serves completion and hover inside parsed SQL
  block spans.
- **Vectors.** Vector columns, literals, `dot`/`similarity`/`identical`, copies, and
  `nearest` queries (`ORDER BY … <=> … LIMIT`) lower and decompile.

What remains is resolving SQL `embed(...)` literals through the embedding lock file,
which [Embeddings in state](state-embeddings.md) owns.

`src/Puck.Dashboard/src/portal/src/components/SqlEditor.tsx` is an unrelated
DuckDB query editor over published files; it is not a starting point.

## Principles

1. **Spelling only.** The dialect lowers to the same `state.world` rows and
   `rules` JSON the native syntax produces. A SQL form and its independently
   authored native equivalent compile byte-identically.
2. **Hermetic and deterministic.** No new package references, no native code, no
   I/O, no clock, no randomness. Lowering and validation honor the cancellation token
   passed by the caller.
3. **Close to SQL, never falsely SQL.** Where SQL's meaning matches, use SQL's
   spelling. Where it doesn't, use a distinct Puck clause (`ADVANCE … PER SECOND`,
   `ON OVERFLOW SATURATE`). A construct whose SQL meaning Puck cannot honor is
   refused, not approximated.
4. **Refuse by name.** Dedicated refusal codes PUCK070–PUCK076 with exact token
   spans inside the SQL text, supplemented by shared state declaration codes
   (PUCK051 for duplicate names/keys, PUCK053 for invalid default/SET literals,
   PUCK054 for capacity, PUCK058 for inexact rates, PUCK059 for unknown references).
5. **Owned by the world vocabulary.** `Puck.Transpiler` stays schema-agnostic. It
   provides the `IDocumentVocabulary.IsEmbeddedLanguage` hook; the world
   vocabulary registers `sql`, supplies its lexer, and owns parsing and lowering.
   The cartridge vocabulary never sees `sql`.
6. **Native syntax stays first-class.** SQL is an alternative spelling for a
   covered subset. Anything outside it is authored natively in the same
   document.

## Placement

A `sql { … }` block may appear at document root. Its table and slot statements
contribute rows to `state.world`, and its rule statements contribute to `rules`,
in statement order, merged with native declarations in document order. Extend
the existing mixed-form refusal (`StateWorldSectionMixed`) rather than adding a
second one. Keywords are case-insensitive; identifiers are case-sensitive, as
every Puck name is.

## State: tables and slots

```sql
sql {
    CREATE TABLE fighters (
        id     TEXT PRIMARY KEY,
        hp     INT   NOT NULL DEFAULT 100 CHECK (hp BETWEEN 0 AND 100) ON OVERFLOW SATURATE,
        mana   INT   DEFAULT 0 ADVANCE 5 PER SECOND,
        speed  FIXED DEFAULT 1.5,
        alive  BOOL  DEFAULT TRUE,
        title  TEXT
    ) CAPACITY 32;

    INSERT INTO fighters (id, hp, title) VALUES
        ('hero',   80, 'Hero'),
        ('goblin', 30, 'Goblin');

    DECLARE gold INT DEFAULT 10 CHECK (gold >= 0);

    CREATE TABLE deck    (card TEXT PRIMARY KEY);
    CREATE TABLE discard (card TEXT PRIMARY KEY REFERENCES deck) ORDERED CAPACITY 52;

    CREATE POLICY ON hand1 FOR SELECT TO seat1;
    CREATE POLICY ON hand2 FOR SELECT TO READERS FROM audience2;
}
```

| SQL | Lowers to |
|---|---|
| `CREATE TABLE t (key PRIMARY KEY, c …, …)` | One keyed row per non-key column, named `t` + `C` in camelCase (`fightersHp`). Every row carries the same keys. |
| `c TYPE ROW name` | Names that column's row explicitly. Needed to spell existing worlds whose row names do not follow the convention. |
| Key-only table `CREATE TABLE t (k PRIMARY KEY)` | One `Bool` presence row named `t`, one `true` cell per inserted key. |
| `REFERENCES other` on the key column, plus `ORDERED` | `domain: { $type: keysOf, row: other, ordered: true }` (a pile). Without `ORDERED`, `ordered` is omitted. |
| `INT`/`INTEGER`, `FIXED`/`DECIMAL`/`NUMERIC`, `BOOL`/`BOOLEAN`, `TEXT`/`VARCHAR(n)`/`CHAR(n)` | `Int`, `Fixed`, `Bool`, `Text`. A length is accepted and ignored. `REAL`/`FLOAT`/`DOUBLE` are refused, naming `FIXED`: state holds no floats. |
| `TEXT`/`INT PRIMARY KEY` | Cell keys: the literal text, or an `INT` key's invariant decimal. Keys are validated as cell names at lowering. |
| Composite `PRIMARY KEY (a, b)` | Refused: a cell has one key. |
| `DEFAULT v` | The cell value for any inserted key that omits the column. |
| No `DEFAULT` and omitted in `INSERT` | No cell for that key in that row: `NULL` is an absent cell. `NOT NULL` refuses the omission. |
| `CHECK (c BETWEEN a AND b)`, `CHECK (c >= a)`, `CHECK (c <= b)`, `CHECK (c >= a AND c <= b)` | `min`/`max`. An `INT` column also admits `>` and `<` (±1). Any other `CHECK` is refused. |
| `ON OVERFLOW SATURATE` | `overflow: Saturate`. The default, `Refuse`, matches SQL: a write that violates the constraint is refused. |
| `ADVANCE r PER SECOND` | `advance`, with `r` reduced exactly as the native `advance(perSecond:)` does. |
| `DYNAMICS name` | The `dynamics` trait naming a dynamics row. |
| Table option `CAPACITY n` | `capacity` on every row of the table. |
| No rows inserted and no `CAPACITY` | `domain: keys`, as the native `table` declaration emits. |
| `DECLARE name TYPE [DEFAULT v] [CHECK …] [ADVANCE …]` | A slot. |
| `CREATE POLICY ON row FOR SELECT TO a, b` | `visibility.readers`. `TO READERS FROM r` sets `visibility.readersFrom`. A policy may name a table, applying to each of its rows. |

Rows emit in column order, and cells in `INSERT` order (not sorted), so a SQL
table and the equivalent native declarations compile identically.

**Stays native in this work:** `cycle`, `draw`, `valuesFrom`, `inverse`, `phase`,
`phaseOf`, `knowledge`, `evicts`, `gatesDrive`, ring domains, grids and
topologies, generators, patterns, search, and body and identity state. A SQL
clause attempting any of them is refused, naming the native spelling.

## Rules

```sql
sql {
    CREATE RULE regen EVERY TICK AS
        UPDATE fighters SET mana = mana + 1 WHERE mana < 100;

    CREATE RULE levelUp ON ENTER AS
        UPDATE fighters SET level = level + 1, xp = 0 WHERE xp >= 100;

    CREATE RULE payToll EVERY TICK AS
    BEGIN ATOMIC
        UPDATE players SET gold = gold - 5 WHERE id = 'hero';
        INSERT INTO tolls (id, paid) VALUES ('bridge', TRUE);
    EXCEPTION
        UPDATE players SET banned = TRUE WHERE id = 'hero';
    END;

    CREATE RULE heal EVERY TICK AS
        IF gold >= 10 THEN
            UPDATE fighters SET hp = hp + 10 WHERE id = 'hero';
        ELSE
            UPDATE fighters SET hp = hp + 1 WHERE id = 'hero';
        END IF;
}
```

| SQL | Lowers to |
|---|---|
| `EVERY TICK` / `ON ENTER` | `mode: Level` / `mode: Edge`. |
| `UPDATE t SET c = c + e` / `SET c = e` | `addState` / `setState` on column `c`'s row. |
| `WHERE key = 'k'` (the table's key column, a literal) | The effects target key `k`, with no `forEach`. |
| Any other `WHERE`, or none | `forEach` over the table's keys, with the remaining predicate as the gate and the bound key as each write's key. |
| `WHERE` predicates: `=`, `<>`, `<`, `<=`, `>`, `>=`, `AND`, `OR`, `NOT`, parentheses | `compareState`/`compareValue`, `all`, `any`, `not`, through the same predicate lowering `when` gates use. |
| More than one `SET` assignment in one `UPDATE` | A `transaction`: a SQL statement is atomic. |
| `INSERT INTO t (…) VALUES (…)` | `setState` for each supplied or defaulted column at that key, in one `transaction`. |
| `DELETE FROM t WHERE key = 'k'` | `removeStateCell` on every row of the table at `k`, in one `transaction`. |
| `BEGIN ATOMIC … [EXCEPTION …] END` | `transaction`, with the `EXCEPTION` statements as `onFailure`. |
| `IF p THEN … [ELSIF p THEN …] [ELSE …] END IF` | The `if` effect, nesting for `ELSIF`. |
| `other.c` inside a statement over `t` | A read of column `c` in table `other` at the same key. Inside the dialect `table.column` has SQL's meaning, not native dot access's `row.key`. |
| `(SELECT c FROM other WHERE key = 'k')` | A read of `other`'s column `c` at literal key `k`. |
| Arithmetic and comparison | `Puck.State` expression semantics: fixed-point `Fixed`, and an overflow refused by name, not SQL numeric semantics. |

Push, countdown, schedule, transform, generate, bindings, zones, and decisions
stay native. A rule body is either wholly SQL or wholly native.

## Decisions this work must honor

1. **Set updates may not read what they write for other keys.** `forEach` fires
   bindings in order, and a later binding sees an earlier binding's writes; SQL
   reads a snapshot taken before the statement. A set-based `UPDATE` (lowered to
   `forEach`) may read the bound key's cells, and any column it does not write.
   Reading a column it writes at another key (an aggregate, a subquery, a
   self-join) is refused, naming the difference. The engine's evaluation order
   does not change.
2. **`NULL` is an absent cell.** `IS NULL`/`IS NOT NULL` lower to the existing
   absent-cell predicate when `Puck.State` has one. Otherwise they are refused.
   Comparisons against an absent cell follow Puck's absent-fact rules, and the
   reference documentation says so where SQL's three-valued logic differs.
3. **Aggregates** (`COUNT`, `MIN`, `MAX`, `SUM`) are admitted only where
   `Puck.State.ExpressionVocabulary` already has an equivalent over a row.
   Everything else — `GROUP BY`, `HAVING`, `ORDER BY`, `LIMIT`, `JOIN … ON`
   across different key sets, `UNION`, window functions, triggers, views,
   `ALTER`, `DROP` — is refused.
4. **No second meaning for native dot access.** Outside `sql { }`, `row.key`
   keeps its meaning. Inside, `table.column` has SQL's.

## Tooling

- **Formatter.** `puck fmt` must understand the block: it re-indents SQL lines as
  a unit and never alters text inside SQL string literals or `--`/`/* */`
  comments. The line-based `PuckFormatter` treats `'…'`, `--`, and braces inside
  them as code today, so that must be handled before the block is formattable.
  Formatting never changes what a document compiles to.
- **Language server.** Completion for keywords, table names, and columns;
  hover on a table or column naming the row it lowers to (`fightersHp`) and its
  traits; diagnostics mapped to spans inside the block.
- **VS Code.** Embed `source.sql` highlighting for `sql { }` in
  `editors/vscode/syntaxes/puck.tmLanguage.json`.
- **Decompiler.** Native output stays the default. `puck decompile --sql` prints
  `state.world` and `rules` in the dialect wherever the whole row group or rule
  is representable, and falls back to native blocks for the rest. Rows are grouped
  into a table only when they share a key set.
- **Lint.** `puck lint` resolves tables and columns the same way it resolves rows.

## Documentation

- The world transpiler README gains the dialect reference: statements, the
  mapping tables above, refusals, and the decisions.
- `docs/reference/dsl.md` and the `puck-dsl` skill (`references/grammar.md`,
  `references/diagnostics.md`) route to it.
- `docs/reference/state/rules.md` and `data-model.md` link to the SQL spelling of
  each concept they explain.
- Every new diagnostic code is declared once in `PuckDiagnosticCodes.cs` with
  its meaning.

## Complete when

- The SQLite facade and its package references are gone, and no
  `packages.lock.json` references SQLite.
- Every table, slot, pile, policy, and rule form in this plan has a test that
  compiles it byte-identically to an independently authored native document.
- Every refusal has a test asserting its code and its span inside the SQL text.
- `puck decompile --sql` over every JSON world in
  `src/Puck.World/Assets/worlds` recompiles byte-identically, alongside the
  existing shipped-world parity tests.
- Formatting a SQL sample is idempotent and leaves compiled output unchanged.
- A sample world under `src/Puck.World.Transpiler/Samples` authored in the
  dialect compiles with `puck compile --validate` and `puck lint --strict`, boots
  in `Puck.World` (`--exit-after-seconds`), and shows a SQL rule firing through
  `world.rule.trace`.
- `puck architecture --check` and `puck lengths` pass, and `Puck.Transpiler`
  references no world concept.
