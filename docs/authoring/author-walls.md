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
| Diagnostics from imported libraries landed on the open file's URI or lost their authoring path/span (template evaluation errors, nested imports, import cycles, caller arguments inside templates) | Multi-file `.puck` worlds and `puck lsp` (`PuckLanguageServer.Diagnostics`, `DocumentLowering`, `ModuleResolver`) | `DiagnosticBag.PushSource` and `DocumentEvaluationException` now carry `SourcePath` and use-site `Related` through `TraverseImports`, template expansion, and binding evaluation (attributing caller arguments back to the caller); cycles report `PUCK017` at the importing node; `PuckLanguageServer` groups published diagnostics by target URI with `relatedInformation`. |
| Template call errors gave no suggestions, highlighted the whole call for bad arguments, and shared `PUCK048`; unresolved references were silent under `--strict` | Template calls and root worlds (`DocumentLowering`, `PuckLinter.References`) | Unknown templates (`PUCK134`) and unknown/duplicate template arguments (`PUCK135`, per argument span with parameter list) carry `DocumentNearestNames` suggestions; unresolved root references (`PUCK_LINT_005`..`008`) are warnings with nearest-name suggestions; "document arm" wording replaced. |
| LSP lacked cross-import hover, go-to-definition and quick fixes, offered retired `compareState`/`compareValue` and out-of-context tokens, compiled synchronously on `row.` completion, and sent full-file no-op format edits | `puck lsp` authoring loop (`PuckLanguageServer`, `PuckHoverInfo`, `PuckSymbolResolver`) | Added `textDocument/definition` and hover across imports, `textDocument/codeAction` for `PUCK040` and nearest-name suggestions, context-aware completion with author `let`/template/import symbols and without retired gates, non-evicting AST/last-compile `row.` lookup, and empty edits when already formatted. |
| Authored errors leaked engine internals (`addState`, `prototypes[i].doc.` prefixes, `Basis/import composition refused`, raw `.NET` parser exceptions), `PUCK035` missed imports-only worlds, emitter stopped at the first error, and `500ms` slipped past the seconds-unit gate | Rules, creation validation, imports-only composition, and multi-error compiles (`PuckParser.Rules`, `WorldDocumentMembers`, `WorldFindingLocator`, `WorldDocumentEmitter`) | Rewrote rule, member (`PUCK136`), creation, and composition diagnostics in authored terms; validators return structured `WorldValidationFinding(Path, Message)` pointers; `PUCK035` locates `/imports/<i>`; emitter recovers per statement; `ParseRhs` converts all time units via `UnitConversion`. |
| Inline test console command hung preparation (`when { seat1: world.state.cell.set marker 0 7 }` on one line) | Scenario tests (`TestCommandLawTests`, `scenario-cost`) | `PuckParser.Tests` now stops a `when` command line at an unquoted closing `}` at brace depth 0, evaluates `ReadRestOfTestLine` before null-guarded diagnostic calls, and guarantees forward progress in every test recovery loop. |
| Template `let`s inside `prototypes` (and `placements`, `state.world`, `document`, `for`) refused with `PUCK036` | `worlds/standard/water.puck` (`riverChannel` had to hoist `channelLength`, `channelCosine`, `channelTopHalf`) | `DocumentLowering.ExpandTemplate` skips indexed `let`/`template` statements; `Expand`, `ExpandForStatements` and `LowerBlockToObject` bind local `let`s in child scopes. |
| `with` accepted only an object literal (`base with { ... }`), refusing `base with overrides` | `worlds/standard/water.puck` (`waterViews` could not default `material: waterMaterial` by merging a view parameter) | `WithExpressionNode.Overrides` accepts any postfix expression; `waterViews` merges `defaultView with raw` and callers drop redundant `material: waterMaterial`. |
| `enum` inside a template (or module) was silently dropped, failing later with `PUCK119` | `worlds/standard/camera.puck` (`chaseCamera` wrote a raw `enums [ { ... } ]` row) | `ExpandTemplateInvocation` indexes template types into the document's enum table, `ProcessStatement` registers dynamic enums, and `ExpandModuleUse` materializes module enums. |
| `for` could not produce list elements where `map` did | `worlds/standard/water.puck` (`waterViews` wrote `lattices: map(views, ...)`) | Array literals now admit `for item in seq { ... }` and `for (item, index) in seq { ... }` (with optional local `let`s), lowering to the same array as `map`/`flatMap`. |
| Repeated `exports { ... }` blocks overwrote earlier ones instead of merging | `worlds/standard/camera.puck` (`chaseCamera` had to repeat `` `camera-boom` `` after calling `cameraResponse()`) | Root `exports { ... }` blocks merge facet lists through the same path as `export` statements; `cameraResponse()` and `chaseCamera()` use `export binding` directly. |
| Rule `local` key read priced one unit above the inline expression (`local k = a + 1; cards[k]` vs `cards[a + 1]`) | `world.budget.rules --why` (`rule-faults-2` audit) | `ExpressionSpelling.TryParse` lowers a bare local read in brackets to `$local:<name>` (`LocalKeyFact`) and `RuleCompiler.CompileKeyExpression` collapses single-local `$expr:` keys to `LocalKeyFact`; stale implicit-local prose in [rules.md](../reference/state/rules.md#resolve-a-cell-key) removed. |
| Record-like fixed tables had to count their own cells by hand (`table play capacity(25)`) | `worlds/debug/chess.puck` (`table play`) | `capacity(auto)` on `table` and `pile` lowers to the authored entries' count (refusing zero authored entries). |
| `schedule row in <parameter>` refused (PUCK001) because the statement form read only a literal number with a unit | `worlds/standard/typing.puck` (the typing play wrote `scheduleState(..., delaySeconds: param)` instead) | `schedule row in <delay>` reads its delay with the member grammar and lowers it against `delaySeconds` as the call form does, so a parameter, arithmetic, a unit or a bare number of seconds all work; pair `schedule-delay`. PUCK010 is retired. |
| `push`, `draw`, `deal`, `shuffle`, `claim` and `for each` read their row or pool as a bare word, so a template parameter, an interpolated name, a module instance's row and a `$zones[…]` selector never resolved (unknown row `pile`, or a refusal at validation), and `deal` took only a literal count | `worlds/debug/freecell.puck` and `klondike.puck` (the gather and deal templates wrote `transform transfer(from: pile, …, selector: First)`) | The four whole-row effects read their row as an assignment reads its target and lower it through the same resolver; `claim` and `for each` take a binding that holds the pool's name; `deal` reads its count as a value; the printer and the decompiler spell a name the bare form cannot carry backquoted; pair `effect-row-parameter`. |
| A library imported under an alias changed values: a function's parameter, a list `for`'s variable or a body `let` named like a library constant read the constant (`m.scaled(10)` gave 6 for `size * 3`), and two unaliased routes to one library under one alias refused with `Duplicate constant` | Any aliased library (`ModuleResolver`) | The import rewriter takes scope from the one binder description hover and go-to-definition read (`SyntaxScopes`), and an import without an alias adds nothing to the namespace a library is loaded once under; pairs `aliased-import-binders` and `aliased-import-diamond`. |
| A member spelling a rule-body keyword could not be written or was broken by the formatter (`` `release`: 2 `` printed `release: 2`, which read back as the statement) | Rule, scope, decision and option bodies (`PuckParser.Rules`, `PuckPrinter`) | A construct body opens a statement with a keyword only from `PuckParser.ConstructBodyKeywords`, the one list the printer quotes from; `name:` is a member whatever it spells; and the printer writes the backquoted escape those bodies read. |
| `hash: auto` was refused on tune, patch, music and table rows (`PUCK030`: `'auto'` does not match the canonical sha256) | `worlds/debug/score.puck` (every pin pasted from an error) | `WorldDocumentMembers` resolves `hash: auto` on those rows through `WorldAssetRowLoader.TryCanonicalHash`, the load-and-canonicalize step the validator, the embedder and the official packaging share. |
| `PUCK_LINT_005` read a binding context's `state: "1"` as a state row name | `worlds/standard/typing.puck` (an `allow` acknowledged it) | The lint's generic `state` walk skips a binding overlay's `document`, a binding profile whose context `state` is a family state value. |
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
