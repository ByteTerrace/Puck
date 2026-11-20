# Author walls

Under [Ergonomic is optimal](../decisions/state-and-language.md#ergonomic-is-optimal),
every place an author hits a wall or reaches for a clumsier form is a compiler
or engine defect, never a style note. This log records walls found while
authoring the debug world, the standard package, the shipped games and the
[`sdf-authoring`](../../.claude/skills/sdf-authoring/SKILL.md) evals, tracking
what is closed and what remains open.

Add new walls here as they appear, and move them to **Closed** when their fix
and spelling-equivalence pair land.

## Closed

| Wall | Where it bit | Fix |
|---|---|---|
| Inline test console command hung preparation (`when { seat1: world.state.cell.set marker 0 7 }` on one line) | Scenario tests (`TestCommandLawTests`, `scenario-cost`) | `PuckParser.Tests` now stops a `when` command line at an unquoted closing `}` at brace depth 0, evaluates `ReadRestOfTestLine` before null-guarded diagnostic calls, and guarantees forward progress in every test recovery loop. |
| Template `let`s inside `prototypes` (and `placements`, `state.world`, `document`, `for`) refused with `PUCK036` | `worlds/standard/water.puck` (`riverChannel` had to hoist `channelLength`, `channelCosine`, `channelTopHalf`) | `DocumentLowering.ExpandTemplate` skips indexed `let`/`template` statements; `Expand`, `ExpandForStatements` and `LowerBlockToObject` bind local `let`s in child scopes. |
| `with` accepted only an object literal (`base with { ... }`), refusing `base with overrides` | `worlds/standard/water.puck` (`waterViews` could not default `material: waterMaterial` by merging a view parameter) | `WithExpressionNode.Overrides` accepts any postfix expression; `waterViews` merges `defaultView with raw` and callers drop redundant `material: waterMaterial`. |
| `enum` inside a template (or module) was silently dropped, failing later with `PUCK119` | `worlds/standard/camera.puck` (`chaseCamera` wrote a raw `enums [ { ... } ]` row) | `ExpandTemplateInvocation` indexes template types into the document's enum table, `ProcessStatement` registers dynamic enums, and `ExpandModuleUse` materializes module enums. |
| `for` could not produce list elements where `map` did | `worlds/standard/water.puck` (`waterViews` wrote `lattices: map(views, ...)`) | Array literals now admit `for item in seq { ... }` and `for (item, index) in seq { ... }` (with optional local `let`s), lowering to the same array as `map`/`flatMap`. |
| Repeated `exports { ... }` blocks overwrote earlier ones instead of merging | `worlds/standard/camera.puck` (`chaseCamera` had to repeat `` `camera-boom` `` after calling `cameraResponse()`) | Root `exports { ... }` blocks merge facet lists through the same path as `export` statements; `cameraResponse()` and `chaseCamera()` use `export binding` directly. |
| Rule `local` key read priced one unit above the inline expression (`local k = a + 1; cards[k]` vs `cards[a + 1]`) | `world.budget.rules --why` (`rule-faults-2` audit) | `ExpressionSpelling.TryParse` lowers a bare local read in brackets to `$local:<name>` (`LocalKeyFact`) and `RuleCompiler.CompileKeyExpression` collapses single-local `$expr:` keys to `LocalKeyFact`; stale implicit-local prose in [rules.md](../reference/state/rules.md#resolve-a-cell-key) removed. |
| Record-like fixed tables had to count their own cells by hand (`table play capacity(25)`) | `worlds/debug/chess.puck` (`table play`) | `capacity(auto)` on `table` and `pile` lowers to the authored entries' count (refusing zero authored entries). |
| Early S11 walls: `function`, `with { }`, `rule template`, `let` record values at typed positions, `flatMap`, `range`/`map`, compile-time `for` in rule bodies, compound assignment (`-=`), shade CSE/DCE/structural folding | Shipped games, debug world, standard package | Closed in [S11](../plans/state-and-language.md#s11--ergonomic-is-optimal) with pairs in `tests/Puck.World.Transpiler.Tests/SpellingEquivalence/`. |

## Open

Tracked in [Language and authoring](../plans/open-items.md#language-and-authoring)
and the [`sdf-authoring` evals](../../.claude/skills/sdf-authoring/SKILL.md):

- **Cross-representation cost corpus**: pairs whose two spellings cannot share
  one JSON document (static placements vs one creation, `wall - opening` vs box
  tiling, mirrored pair vs `symmetry` fold, union shape order, `x += 3` vs
  `x = x + 3`) need a cost corpus over bake groups and the work sheet beside the
  document corpus.
- **Re-expanding a rule template over a basis**: a rule whose effects write one
  key twice (`claim-expire` in `worlds/debug/pond.puck`) refuses when merged by
  `key` across a basis; decide whether effect lists stay ordered actions rather
  than a keyed set.
- **Authoring-time refusal for unreferenced `test` world blocks**: a
  `garden { ... }` expectation naming a document the world does not reference is
  only refused when `puck test` stages the world, not in `puck compile`,
  `puck lint` or the language server.
- **Work-sheet over-pricing**: `companion-interest-*` (~170 running-minimum
  rules lacking a nearest-point reduction), dynamic-key writes charged against
  every pinned cell in `CountWriters`, and live-zone-table transfers pricing
  disclosure by the square of the piles.
- **Unused declarations in module libraries**: `puck lint` does not report library
  `let`s or templates that no source in a directory reads.
- **Imported module named `roles`**: re-verify whether a module named `roles`
  still triggers `PUCK031` on its importer.
- **Sculpting and validation walls surfaced by `sdf-authoring` evals**:
  - `Morph` shape-level blend (`s10-morph-blend`): `puck compile --validate`
    admits a `Morph` that fails at SDF emission; only `puck creation stats`
    catches it.
  - `Capsule` half-length under scale (`motion-nod`): scaled `Capsule` ended
    short of author expectation; document exact `Capsule` scale semantics in the
    shapes reference.
  - `Creation`-scope subtraction across unrelated parts (`s08-subtraction-trap`)
    and `detail` flags not reducing shape budget (`s07-over-budget`): clear
    authoring diagnostics and remedies remain guarded by the evals.
- **Unreproduced parser/printer edge cases**: string literal on the left of a
  relational gate; header `when` gate making a body's `local` report `PUCK012`;
  `not` losing span/recursion depth; unary `-`/`~` before `not` precedence;
  `PuckPrinter` dropping `!= 0` from a grouped comparison gate.
