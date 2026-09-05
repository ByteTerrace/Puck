# Puck.State

The state and rule engine of a deterministic fixed-step simulation, as a
library with no world, body, rendering, or presentation concept. A **rule**
reads and writes named **state rows** through a bounded postfix **expression**
that also carries an infix spelling; every value is exact integer or Q48.16
fixed-point arithmetic over `Puck.Maths`; and the lookup tables, atomic state
transforms, validated identifiers, and reserved fact channels a rule can name
are all data a document declares. `Puck.World.Schema` consumes and extends this
package for the world document, so a card game, a turn-based resolver, or
another engine's frontend can run authoritative rules over `Puck.Maths` and
`Puck.State` alone.

`dotnet pack` produces `ByteTerrace.Puck.State`; the first NuGet.org release has
not been published yet. The project depends on `ByteTerrace.Puck.Abstractions`,
`ByteTerrace.Puck.Assets`, `ByteTerrace.Puck.Maths`, and
`ByteTerrace.Puck.Physics`, each named directly, so the package's declared
dependencies are the whole closure rather than part of it plus whatever another
package happens to carry along.

## ⚖️ Licensing

ByteTerrace.Puck is source-available and dual-licensed. It is not open source.
The default is the
[PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0),
under which noncommercial use is free: study, hobby projects, research,
evaluation, and use by any school, university, public research organization,
charity, or government body. Shipping or operating it commercially requires a
paid commercial license from ByteTerrace, whatever the size of the user.

Both documents ride inside the package.
[`LICENSE.md`](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md) is the
binding noncommercial license;
[`LICENSING.md`](https://github.com/ByteTerrace/Puck/blob/main/LICENSING.md) is
the plain-language summary of who needs which, and how to ask for commercial
terms.

## 📦 What the package holds today

The extraction is the campaign's "`Puck.State`: the state and rule engine as a
standalone deterministic library" charter in `docs/campaign.md`, landing in
three phases. This is the first: the pure pieces, moved with no behaviour
change and every type keeping its `World` prefix, so each diff reads as a move.

- *Expressions:* `WorldValueExpression` and its `WorldValueToken` postfix
  vocabulary (constants, state reads, arithmetic, comparison, bit and board
  operations, `select`), `WorldExpressionSyntax` (the infix spelling and its
  inverse — syntax only, no second evaluator), `WorldValueExpressionJsonConverter`
  (reads either spelling, writes each back in its own), `WorldExpressionOp` (the
  compiled opcode), and `WorldExpressionArithmetic` (the allocation-free Int and
  Q48.16 evaluator every opcode lowers to).
- *Tables:* `TableDocument` (`puck.table.v1`), `TableEntryDocument`, and
  `TableCanonicalizer` (validate → normalize → canonicalize), with
  `WorldTableRow` as the name/source/hash reference a document pins one by.
- *State transforms:* the `WorldStateTransform` union (`transfer`, `setRay`,
  `shuffle`, `sortZone`, `sortKeyed`, `writeSet`, `push`, `observe`),
  `WorldZoneSelector`, and `WorldSortKey`.
- *Identifiers:* `WorldSafeName` and `WorldCellName` (validated at
  construction, refusing by name), the `WorldOwnedWorldFileName` id↔file-name
  mapping the length ceiling derives from, their JSON converters, and the
  `TryParseStringJsonConverter<T>` shape they share.
- *Reserved channels:* `WorldRuleFacts`, the `$`-prefixed fact channels a rule
  may compare against instead of a declared row (`$tick`, `$population`,
  `$reduce:`, `$table:`, `$bind:`, `$distance:`, …). The library names them; a
  host answers them.
- *Literals:* `WorldStateNumericLiteral`, the one decimal→Q48.16 conversion
  every authored constant and table value crosses.

## 🧭 What it will hold

The charter's next two phases, in order:

1. *The extension seams.* The operand and effect unions become registries the
   world project fills with its own arms, each arm carrying its own cost so the
   work sheet stays derived; the compiler compiles state effects itself and
   dispatches the rest; the budget, dataflow, hazards, and trace move with the
   compiler.
2. *The evaluator*, behind a state-host interface — the mutation door, the
   journal, checkpoints, and the fact reader the operands answer through —
   that `WorldServer` implements. Only then do the `World` prefixes go, in one
   rename sweep.

Refused by the charter: a compatibility shim between old and new spellings at
any phase, and moving the evaluator before the seams exist.

## 🔗 Where the rest lives

The compiler (`WorldRuleCompiler`), the operand and effect unions, the state
section's row vocabulary, and the validators stay in
[`src/Puck.World.Schema`](../Puck.World.Schema/README.md) until their phase;
the evaluator stays in [`src/Puck.World.Server`](../Puck.World.Server/README.md).
`tests/Puck.State.Tests` proves the expression syntax's parse/print laws; the
converter and schema facts that need the world document's JSON context stay in
`tests/Puck.World.Schema.Tests`.
