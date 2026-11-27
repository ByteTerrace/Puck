# Compiler diagnostics

This page explains every diagnostic the `.puck` compiler, the linter and the world validator report. Each entry gives
the cause, an example that triggers it and the fix, in the manner of the Microsoft Learn compiler error pages (for
example [CS0103](https://learn.microsoft.com/dotnet/csharp/misreading/cs0103)). A compile prints the page and anchor
under each message (`Learn more: docs/reference/diagnostics.md#puck016`), so you can open the entry for the code you
see.

## How a message is written

Every message says what is wrong, where, and how to fix it:

- The first sentence names the problem and the thing involved, in single quotes: `'height' is not a member of ...`.
- The location is the file, line and column the report prints. A fault in an imported source is reported against that
  source, with the line that imported it attached.
- The next sentence says how to fix it. Where one exists, it gives the corrected form or a nearest-name suggestion
  (`Did you mean 'height'?`).

The compiler builds the wording in one place, `DiagnosticText`, so the entries below read the same way as the
messages. A message that looks different from its entry is a defect worth reporting.

### Reading a code

| Prefix | Meaning |
|---|---|
| `PUCK001` to `PUCK140` | A compiler or validator finding. Severity is Error unless the entry says Warning or Information. |
| `PUCK_LINT_nnn` | A linter finding from `puck lint`. Information unless the entry says Warning. |

A Warning or Information finding is acknowledged, never silenced, by writing `allow CODE "why"` on the line before the
statement ([PUCK124](#puck124)). An Error cannot be acknowledged.

## Syntax and structure

### PUCK001

The source does not follow the grammar. This is the code for a syntax error with no more specific code.

Cause: a token is missing, misplaced or misspelled. A parse error reads `Expected X, but found 'Y'`, and the span
points at where the parser stopped. After an error the parser skips to the end of the statement and carries on, so a
second error at a later line is a real one: the compiler drops the stray `}` that a failed block leaves behind rather
than reporting it again.

Example:

```puck
rule "open" {
  when state.door == 0
  state.door = 1
```

Fix: close the block (`}`) and check the line the span points at. Fix the first error before the later ones.

### PUCK002

An operand inside a rule (a `when` gate, a score or an effect's right-hand side) does not parse as an expression.

Cause: the operand text is checked by the rule language's own grammar, which differs from the document grammar.

Example: `when state.hp <` (nothing after the operator).

Fix: complete or correct the expression the message quotes. The message ends with the parse error's own words.

### PUCK003

A place that takes exactly one state row received something else, such as an expression or a whole row where a cell
was named.

Example: `remove state.cards[k] + 1`.

Fix: name one row, with its key if the row is keyed: `remove cards[k]`.

### PUCK005

A row's kind annotation names something that is not a cell kind.

Fix: use one of the kinds the message lists (`Int`, `Fixed`, `Bool`, `Text`, `Vector`), or, to name an enum, declare it
with `enum Name { ... }` first.

### PUCK006

Reserved. Nothing reports this code now: a `local` takes its kind from its expression, and writing one is
[PUCK108](#puck108).

### PUCK007

A `local` has no initializer, or its initializer is empty.

Example: `local count`.

Fix: write `=` and an expression: `local count = state.items + 1`.

### PUCK008

An effect statement (`push`, `remove`, `schedule`) is written outside a rule body.

Fix: move it inside the `rule { }` it belongs to.

### PUCK009

The right-hand side of an effect has a shape the effect cannot carry: a string given to `push`, or a duration given to
anything but `=`, `+=` and `-=`.

Fix: `push` takes a number; assign text with `=`; write a duration only with `=`, `+=` or `-=`.

### PUCK011

A `rule` or `rules` block has no name.

Example: `rule { ... }`.

Fix: name it: `rule "open-door" { ... }`.

### PUCK012

A rule or option has more than one `when`.

Fix: join the conditions with `and` or `or` in a single `when`.

### PUCK013

A decision construct is outside the block it belongs to, or an `option` has no score.

Example: an `option` written in a rule body, or `option "rest" { }` with no `score`.

Fix: put `option` and `interrupt` inside a `decision`, put `when` and `local` inside a rule, and give each option a
`score <expression>`.

### PUCK014

A `transaction` has more than one `onFailure` block, or an `onFailure` stands outside a transaction.

Fix: keep one `onFailure`, inside the transaction it recovers.

### PUCK018

A reserved word is written where a name is expected.

Fix: rename it, or write the name in backquotes (`` `when` ``).

### PUCK019

A `transaction` is nested in another `transaction`.

Fix: move the inner effects into the outer transaction, or make the two siblings.

### PUCK026

A `rule` has no effect, so it can change nothing.

Fix: add an effect (an assignment, `push`, `remove` or `schedule`), or delete the rule.

### PUCK029

A `decision` does not say how often it is made.

Fix: add `periodSeconds`, as in `decision "wander" periodSeconds 2 { ... }`.

### PUCK028

A placement writes both the bare `solid` flag and a `solid { }` block.

Fix: keep one. The bare flag is the default solid; the block overrides it.

### PUCK036

A section block holds a statement its grammar has no place for.

Cause: each section (`placements`, `prototypes` and the rest) accepts only its own statements. A statement that did not
parse draws its parse error alone.

Fix: the message lists what the section admits. Write one of those forms.

### PUCK037

A control-flow statement the document's rule shape cannot carry. A world rule is straight-line, so `repeat` and
`break` have nothing to lower onto.

Fix: a rule already runs once per matching subject, so iterate with `forEach`; a fixed count unrolls with a
compile-time `for`, or folds into one effect (`x += 3`).

### PUCK038

A call-form gate such as `key(left, held)` is used in a world document, whose gates are comparisons only.

Fix: write a comparison: `when state.held == 1`.

### PUCK046

A `for` emits rows, and its body assigns a field, so every iteration would write the same field.

Example:

```puck
for i in range(0, 4) {
  positions: [i, 0, 0]
}
```

Fix: build the value where the field is written, with `map`: `positions: map(range(0, 4), i => [i, 0, 0])`.

### PUCK099

A `stabilize` or `workflow` block cannot be lowered: its pass ceiling is malformed, a member cannot be named, or a step
shape a staged cursor does not carry (`repeatStep`, `forEachStep`, a `rule` block inside a step).

Fix: write a `step` whose body is the step's own `when` and effects, give each step a distinct name, and give
`maxPasses` a value that is a positive integer at compile time.

### PUCK100

A rule's name is a template parameter's own identifier, so every instantiation would mint the same name.

Example: inside `template t(name) { rule name { ... } }`.

Fix: use an interpolated name, `rule $"{name}-open" { }`, or move the rule outside the template.

### PUCK103

A `transform` is written with a result label.

Fix: write `transform nearest(from: bag, query: deck, into: hp, k: 3)`; the destination is an argument of the call.

### PUCK123

A section block holds only its rows.

Example: `kits { rows [ ... ] }`.

Fix: write the array: `kits [ ... ]`.

## Names, imports and templates

### PUCK015

A `let` is read before it is declared, or never declared.

Fix: declare it with `let name = ...` earlier in the same or an enclosing scope, or fix the spelling.

### PUCK016

An import cannot be resolved. Either no file carries the name, the file does not parse, or the file is neither a source
nor a document.

Cause: the importer cannot tell what the named file is. When the file does not parse, the message carries the file's
own first errors with their file, line and column.

Example:

```text
error PUCK016: The imported source 'shell' does not parse, so the importer cannot tell whether it is a library or a document. It reports:
  worlds/debug/shell.puck(12,5): error PUCK001: Expected '}' closing block for 'rule', but found 'let'.
  Fix the error in 'worlds/debug/shell.puck' and compile again.
```

Fix: for a missing file, check the name and that the file exists beside the importing source; an import names the file
without its extension. For a file that does not parse, fix the errors the message lists.

### PUCK017

The imports are malformed. This covers a cycle, a library that imports a document, a duplicate alias and an import
graph that nests too deep.

Example: `a.puck` imports `b`, and `b.puck` imports `a`.

Fix: move what the two share into a third library that both import. A library emits no document, so import a document
from the source that composes it, never from a library.

### PUCK020

An addon module is not a well-formed WebAssembly binary: its header, a section length, or an import or export entry
runs past the bytes it claims.

Fix: rebuild the module with a WebAssembly toolchain, and check that the file the document names is the build output.

### PUCK021

An addon imports a host function that needs a capability the document does not declare.

Fix: add the capability request the message names, or remove the import from the module.

### PUCK023

An addon pin is wrong: the pinned hash does not match the module's computed hash, a hash is not spelled as an authored
pin, or the module for an automatic pin cannot be found.

Fix: write `hash: auto`, or `hash: "sha256/<64 lowercase hex>"` for a digest the file really has. Quoted `"auto"`, bare
digests and other prefixes are refused by name.

### PUCK033

A `shape` or `placement` reuses an id another row of the same collection already uses.

Fix: give each row its own id.

### PUCK034

A shape's `parent` names no sibling shape in the same `shapes` array. This is a Warning.

Fix: use the name of another shape in the same array. The message suggests the nearest names.

### PUCK035

A basis or import graph cannot be composed: a link or the entry world cannot be carried, or a basis is missing or
cyclic.

Fix: the message names the reason. A composition boots into one world, so mark only one `entry world`.

### PUCK041

A compile-time call is refused, or a name is neither a compile-time function nor a construct the vocabulary has.

Cause: a collection function (`map`, `filter`, `reduce`, `range`, `length`, `concat` and the rest) received arguments
that cannot be evaluated when the document compiles, or has the wrong number or kind of arguments.

Example: `range(0)`, or `min(a, b)` where the spelling is `minimum`.

Fix: pass compile-time values (literals, `let` constants and results of other functions), match the form the message
quotes, and spell a function exactly as the vocabulary names it. Nothing at run time, such as state, can be an
argument.

### PUCK042

A lambda is written where no collection function takes one.

Example: `thickness: x => x * 2`.

Fix: pass the lambda as an argument of one of the builtins that takes one (`map`, `filter`, `reduce`, `flatMap`, `groupBy`, `sort`, `any`, `all`, `count`, `sum`, `scan`, `indexOf`, `find`); the message lists them. A function that reads values only, such as `sine`, takes none.

### PUCK043

An index cannot be resolved when the document compiles.

Cause: the target is not an array, object or string; the index is not a whole number the compiler knows; or the index
is outside the target.

Example: `let items = [1, 2, 3]` then `items[3]`.

Fix: index with a whole number from 0 to the length minus one. Arrays count from zero.

### PUCK044

A `for` walks something that is not an array known at compile time.

Fix: walk an array literal, a `let` that holds one, or the result of `range` or `map`.

### PUCK045

A template or `for` nests past the expansion limit. A template that calls itself looks like this from inside the
expander.

Fix: remove the self-call, or express the repetition with `for` or `map`.

### PUCK047

Compilation work, source size, collection size or nesting depth exceeds a limit.

Fix: split the source, the generated data or the nesting into smaller pieces.

### PUCK048

A compile-time value is invalid: an operator is applied to the wrong kind of operands, a division by zero, a shift out
of range, a result outside the numeric range, a function called wrongly, or a value cyclic.

Example: `let ratio = 4 / 0`.

Fix: the message names the operator and the operands. Use values of the kind it names.

### PUCK110

A call argument that holds a name, a cell key, a value expression or a word from a closed vocabulary is written as a
quoted string.

Example: `setState(key: "score", ...)`.

Fix: write it bare, as the message shows: `key: score`. An interpolated string computes a name and is admitted there.

### PUCK111

A call argument that holds free text is written as a bare word.

Example: `label: ready`.

Fix: quote the text: `label: "ready"`. If you meant a constant, declare it with `let ready = ...`.

### PUCK112

A member that holds a value expression is written as an interpolated string.

Fix: write the expression itself. An interpolated string computes a name, a key, a number or text, never an
expression.

### PUCK113

A name uses a spelling the compiler reserves for names it generates: a `$` inside a name or a `~` anywhere in one.

Fix: rename it without `$` or `~`.

### PUCK114

A `when`, a `decision` or a `rule` is written inside a `for` in a rule body. A rule has one gate and one decision.

Fix: write the condition in the rule's own `when`, or in an `if` inside the loop. Stamp whole rules with a `for`
outside any rule body.

### PUCK115

An enum member is spelled like a `let` or a module parameter in the same scope, or two enums in scope declare the same
member and it is read bare.

Fix: rename one, or read the member qualified with its enum, written enum name, a dot, then the member name.

### PUCK116

A placement id contains `:` or is exactly `$each`.

Fix: choose an id without `:`, since channels use it to separate their arguments.

### PUCK117

A module alias is spelled like a row, pool, `let` or module parameter of the scope that uses it.

Fix: rename the alias or the other name, so `alias.name` reads one thing.

### PUCK121

An import spells a file instead of naming what the file carries.

Example: `import "skies.puck"`.

Fix: `import "skies"`.

### PUCK124

An `allow` line cannot acknowledge anything. It names a code the compiler does not report, gives no reason, or stands
where no statement follows it.

Example: `allow PUCK122` on a line by itself.

Fix: `allow PUCK122 "the blend moves on purpose"`, directly above the row it acknowledges. An Error cannot be
acknowledged.

### PUCK125

A constant, a template or a template parameter is declared twice in one scope.

Fix: rename or delete one. The second declaration is the one refused; the first stands.

### PUCK134

A template or module invocation names a template or module that is not in scope.

Example: `pilar(width: 2)` where the template is `pillar`.

Fix: declare the template, add the `import` that provides it, or fix the spelling. The message suggests the nearest
names.

### PUCK135

A template invocation passes an unknown, repeated or excess argument.

Example: `pillar(width: 2, heigh: 4)`.

Fix: use the parameter names the message lists. A parameter is given once, by position or by name.

### PUCK136

A member written in a document is not in the schema.

Cause: the name is misspelled, or the member exists in a newer schema than this build of the compiler. The message
names the type it was looked up in and where it was written.

Example:

```text
error PUCK136: 'passes' is not a member of WorldRenderGraph in this build's schema, at 'render.graph'. If the member is
new, this build of the compiler or the engine may be older than the document: rebuild the CLI and the engine from the
same source and compile again.
```

Fix: check the spelling against the nearest names the message suggests. If the name is right and new, rebuild the `puck`
CLI and the engine from the same checkout.

### PUCK140

A member of an object is written twice where the two writings cannot combine.

Cause: two arrays append and two objects merge member by member, so a member both set would silently drop the
earlier one.

Fix: write each member once, or merge the two writings.

## Values and units

### PUCK024

A value carries a unit in a field the dimension table does not cover.

Fix: remove the unit and write the number alone.

### PUCK025

A value's dimension is not the field's, or a unit is not a unit.

Example: `radius: 2m * 3m` (an area in a length field), or `angle: 4m/s`.

Fix: change the arithmetic or the unit so the value has the field's dimension. The message names both. A unit is one of
`m cm mm s ms min h hz deg rad % pct lx lm cd nit K`, or several joined by `/` and raised with `^`.

### PUCK137

Two dimensions meet where they must agree: a sum, a comparison, a choice's two arms, `minimum`'s arguments, `cosine`
of a length, or a `let` against its annotation.

Example: `2m + 3s`.

Fix: convert one side, or remove the unit from the side that should be a plain number. A bare number never mismatches.

## State declarations

### PUCK049

A `table`, `slot`, `pile` or `grid` is declared outside `state.world`.

Fix: move the declaration under `state.world`. `state.body` and `state.identity` take no declaration syntax.

### PUCK050

`state.world` is written both as an array (`world [ ]`) and as a declaration block (`world { }`).

Fix: use one form. Several declaration blocks compose, so a template's block beside the document's own is fine.

### PUCK051

A row name is declared twice in `state.world`, or a cell key twice in one row.

Fix: rename or delete one.

### PUCK052

A row name or cell key starts with `$`, which is reserved for engine-minted names.

Fix: rename it.

### PUCK053

A default or `bounds` argument does not fit the row's kind, or a declaration's `= value` is not the shape its row takes.

Example: `slot ready = 3` for a Bool row, `initial: a > b` where the comparison is not a constant, `table scores = 5`,
or `pile deck of cards = 3`.

Fix: write a literal of the row's kind: whole number for Int, decimal for Fixed, `true` or `false` for Bool, quoted text
for Text. The message names the kind and what was written. A table's `= value` is an array, keyed `0`, `1`, ... in
order, or an object, keyed by its members; a pile's is an array of its starting token keys.

### PUCK054

A table's `capacity(...)` is smaller than its authored cells.

Fix: raise the capacity to at least the number of cells, or remove cells.

### PUCK055

A modifier is not admitted by the declaration's shape or kind: `bounds` or `advance` on a Bool or Text row, `capacity`
on a slot.

Fix: remove the modifier, or change the row to a kind that admits it.

### PUCK056

A row or cell has two behaviors: a repeated `advance` or `bounds`, or a cell with `advance` and `behavior(none)`.

Fix: keep one.

### PUCK057

A modifier name is unknown, or its arguments are not the shape the modifier takes.

Fix: use a modifier the message lists, with the arguments it names.

### PUCK058

An `advance(perSecond: ...)` rate does not reduce to an exact 64-bit fraction.

Fix: write a rate with a smaller or terminating decimal, such as `0.25`.

### PUCK059

A `pile` or `grid` names another row that is not declared.

Fix: declare the row in `state.world`, or fix the name.

### PUCK060

A `pile` or `grid` names a row that exists but has the wrong shape: a pile's `of` row that is not a plain token domain,
a `positions` row that is not an integer `keysOf` row, or an `inverse` pair whose keys disagree.

Fix: name a row of the shape the message states.

### PUCK061

A `pile` token or a `grid` cell key is declared twice.

Fix: remove the duplicate.

### PUCK062

A pile's capacity exceeds the number of tokens in its domain.

Fix: lower the capacity to the token count or less, or add tokens.

### PUCK063

A `grid` has a kind other than `Int` or `Bool`.

Fix: use `Int` or `Bool`.

### PUCK064

A `grid` cell key is not a whole-number cell ordinal inside `0..width*depth-1`.

Fix: use a cell number inside the grid.

### PUCK065

A `grid` combines `inverse(...)` with authored cells.

Fix: keep one. A derived board takes its cells from its tokens and codes rows.

### PUCK066

A `grid` reuses a topology name another grid or `state.lattices` already declares.

Fix: rename one grid.

### PUCK089

A family size is not a positive integer known at compile time.

Fix: write the size as a literal or a `let` constant from 1 to the row limit.

### PUCK090

A compile-time family index names no member of the family.

Fix: use one of the family's indexes, which the message lists.

### PUCK091

An enum, or an enum member, is not declared.

Example: a read of the member `Smoke` of an enum `Element` that declares no such member.

Fix: declare it with `enum Element { Air, Smoke }`. The message lists the members and suggests the nearest.

### PUCK092

A record field is not declared, or its value does not match.

Fix: use a field the record declares.

### PUCK095

Derived state declarations form a cycle.

Fix: make one of them read a source row instead of the other derived row.

### PUCK096

An enum member or a record field is declared twice.

Fix: remove or rename the duplicate.

### PUCK097

A pattern's `match:` language or a symbol's value band is refused by the pattern algebra.

Fix: write a pattern over the declared symbols, with each symbol's value band least first.

### PUCK098

A pattern needs more machine states than its row budgets.

Fix: simplify the pattern, or raise `maxStates` up to the limit the message names.

### PUCK101

A `set` declaration is refused by the cell-set algebra.

Fix: write the set from rows, keys and the set operators the message names.

### PUCK102

A family's index set cannot be carried: a duplicate index, a descending range, or a count outside the family's bounds.

Fix: list each index once, ascending, within the bounds.

### PUCK104

A `test` has a malformed shape: a member other than `given`, `when` and `expect`, one written twice or out of order, no
expectation, a `with` naming a template, or two tests generating the same world.

Fix: write one `given`, `when` and `expect` in that order, name a module (not a template) after `with`, and give tests
distinct names.

### PUCK105

A line in a `test` cannot be carried by the generated world: a `given` that is not a cell assigned a literal, a `when`
step that is neither `ticks <n>` nor `seat<n>: <command>`, a command outside the scheduled steps, or an `expect` that
names a document no reference of the world reaches.

Fix: write the line as the message shows. A test acts as a seat, never the console, so that it exercises authority.

## Vectors and embeddings

### PUCK077

An embedding space is malformed: an unknown or missing field, dimensions outside 8 to 1024, a repeated name, or more
than 16 spaces.

Fix: declare each space once with `model`, `revision` and `dimensions`.

### PUCK078

A Vector row names no space and the document has no default, names an undeclared space, or a non-Vector row names a
space.

Fix: declare the space in `spaces`, or give the row `space(name)`.

### PUCK079

A text embedded with `embed(...)` has no entry in the lock file.

Fix: run `puck embed lock <path>` on the source, and commit the lock file it writes.

### PUCK080

The lock file was made for a different definition of the space (its model, revision or dimensions changed).

Fix: run `puck embed lock <path>` again.

### PUCK081

A vector literal is not base64url, has the wrong length, holds -128, or fails admission.

Fix: write `vector("...")` with a base64url value of exactly the space's dimension count.

### PUCK082

A `vector(...)` or `embed(...)` is written where no vector is admitted.

Fix: use it where a Vector row is read or written, or in a vector operation such as `nearest`.

### PUCK083

A vector row, or all vector rows together, exceed the size limit: 64 KiB for one row, 4 MiB in all.

Fix: lower the row's capacity or its space's dimensions.

### PUCK084

A vector operation names a non-vector operand, mixes spaces, or gives `nearest` or `remember` a shape, `k` or
threshold it does not admit.

Fix: use Vector operands of one space, `k` from 1 to 64 and thresholds from 0 to 1.

### PUCK085

An `embed(...)` has no operand, destination or default to take its space from.

Fix: declare a default space, or write it where its destination row names one.

### PUCK086

`embeds` is written on something that is not a Text table, or names a colliding row.

Fix: put `embeds` on a Text table, and choose a companion name no other row uses.

### PUCK087

A `mix` has too few or too many terms, or a weight out of range or zero.

Fix: use 1 to 8 terms, each weighted by a nonzero integer from -1000 to 1000.

### PUCK088

A vector filter's `where` is not a keyed Bool row, or its `exclude` is malformed.

Fix: name a Bool row with keys for `where`.

## Rendering and creations

### PUCK027

A `shape` names a primitive that does not exist.

Example: `shape Spere { ... }`.

Fix: write the primitive exactly as it is named, with its capitals. The message suggests the nearest names.

### PUCK119

A state row or a record field names an enum the composed world does not declare.

Fix: declare it with `enum Name { ... }` here or in the basis. A cell kind (`: Int`) is never written; see
[PUCK107](#puck107).

### PUCK106

An operand spells a reserved channel with colons.

Example: `$reduce:count:zone`.

Fix: write the channel as a call, `count(zone)`, and a rule local by its bare name. The message shows the spelling to
use.

### PUCK107

A row declaration spells its kind (`: Int`, `: Fixed`).

Fix: remove the annotation. The compiler infers the kind from the row's cells, value and modifiers. A word after `:`
names an enum.

### PUCK108

A `local` spells its kind.

Fix: remove the annotation. The kind is inferred from the expression.

### PUCK120

A graph row's bound parameter is not written as `parameter pass.member = value` inside its `graph` block.

Fix: write one `parameter <pass>.<member> = <value>` line per member inside the `graph` block, never a raw
`parameters { }`.

### PUCK122

Warning. An animated creation's blend group joins, by a blend other than `Union`, shapes that move independently. The
seam stretches as they move.

Fix: where the motion is meant, write `allow PUCK122 "why"` on the line before the row. Otherwise give both shapes one
motion owner.

### PUCK126

A shade name or phase is outside the fixed presentation interface, or a shade is written in a form it does not take.

Example: `shade { fragment { } }`, where the phases are `surface`, `light` and `compose`.

Fix: write a shade template call, a block of `surface`, `light` and `compose` phases, an admitted program, or a list of
them.

It also refuses a `gradient` whose height reads an operation with no derivative (`shape`, `lines`, `refract`, `tilt`,
or a gradient op of its point), turns its point by a rotation that varies with the point, or holds another
`gradient`. Fix: build the height from arithmetic, the smooth functions, `lattice` and `cells`
([slopes of a height](dsl.md#slopes-of-a-height-gradient-and-relief)).

### PUCK127

Shade operand types or ranges disagree: a literal with the wrong number of components, a value that is not a finite
number, a lane selector outside 0 to 3.

Fix: write literals with the components the type takes, and a selector from 0 to 3.

### PUCK128

A shade expression reads a value that is not defined, or a binding that reads itself.

Fix: define the value with `let` before using it, or fix the spelling.

### PUCK129

A shade program exceeds its static work or size capacity.

Fix: simplify the program: fewer loop iterations, fewer operations.

### PUCK130

A runtime parameter is used where it would shape the program: a loop range, a shape descriptor or a lane selector.

Fix: mark the parameter `structural` in the template's parameter list.

### PUCK131

Warning. An animated creation's ungrouped shape declares a blend other than `Union`. The pooled bake draws it as a hard
union.

Fix: give the shape and the shapes it blends into one `group`.

### PUCK132

Warning. A creation's driver authors a swing, slide or clip pose on a shape its `parts` mask does not cover, so the
motion never plays.

Fix: add a part that covers the shape to the driver's `parts`, or remove the motion.

### PUCK133

Warning. A later effector writes a shape an earlier effector's tip hangs from, so the later one can carry the earlier tip
off its target.

Fix: intended when the later contact should win. Otherwise reorder the effectors or split their chains.

### PUCK138

A creation's render-lane member names a lane no `render.lanes` row declares.

Fix: add a row with that name to `render.lanes`, or write a name it declares. The message lists the declared lanes.

### PUCK139

`render.lanes` declares a name twice, or declares more lanes than the world holds (four).

Example: five rows in `render.lanes`.

Fix: rename or delete the duplicate; keep at most four lanes, or let effects that never animate together share one
lane. The message lists the lanes that were written.

## Validation of the composed world

### PUCK030

The world validator refused the composed document.

Cause: the validator checks the whole world after composition: numeric ranges, references between sections, capacities
and shapes. Its message names the member in the path the author reads (for example `render.shadowCoverage.near`), says
what is wrong with the value, and says what the value must be.

Example:

```text
error PUCK030: 'render.farDistance' is 0.5, which is outside [1, 8192]. Write a finite number within [1, 8192].
```

Fix: apply the correction the message gives. The report is placed on the line of the source that wrote the member, even
when a basis or import wrote it.

Capacity messages name what used the capacity. For example, `render.lights` holds at most 128 lights; the message lists
how many are written, which index is the first that does not fit, and how many of each kind the section holds.

### PUCK031

The schema rejected the document: a value of the wrong kind, a missing required member, or a name not in an enumerated
set.

Example:

```text
error PUCK031: 'host.fullscreen' takes true or false, but the document holds a number. Write true or false there.
```

Fix: write a value of the kind the message names. When the cause is a comparison written where a value belongs, write
the value it would produce, or a state row that holds it.

### PUCK032

The lowered document could not be read back for validation.

Cause: a compiler defect, since the lowering produced a shape the schema cannot deserialize.

Fix: report it with the source that triggers it.

### PUCK118

Information. The validation of a world deferred a check to the host that runs it, because the host diagnosing the source
carries no catalog for it (a machine engine, a post-process package, a probe kind).

Fix: none needed. A deferred check never fails a compile.

## Linter findings

### PUCK_LINT_001

A `let` is declared and never read.

Fix: delete it, or use it.

### PUCK_LINT_004

A colour is written inline.

Fix: declare it once with `let name = #rrggbb` and use the name, so a colour changes in one place.

### PUCK_LINT_005

A state-row name resolves to no declared row. The message suggests the nearest rows.

Fix: declare the row in `state.world`, or fix the spelling. A document that composes a basis cannot be checked
standalone, so the linter reports this as a note.

### PUCK_LINT_006

A `prototypeId` names no declared prototype.

Fix: declare the prototype, or fix the name.

### PUCK_LINT_007

A placement's parent names no placement.

Fix: name a placement the document declares.

### PUCK_LINT_008

A camera, view or spawn-point reference names nothing declared.

Fix: declare it, or fix the name.

### PUCK_LINT_009

A reserved-channel prefix matches no known channel.

Fix: use the prefix the message suggests.

### PUCK_LINT_010

Warning. A vector `mix` term contributes less than 1/64 of the total weight.

Fix: raise its weight, drop the term, or average over a history table instead.

### PUCK_LINT_011

Warning. A division or remainder sits in an arm of a choice (`?:`). A choice evaluates both arms, so a zero divisor
faults even when the arm is not the one selected.

Fix: guard the operation with an `if`/`else` effect or a short-circuit gate (`and`, `or`).
