# DSL and cartridge release hardening

The September 2026 review found semantic inconsistencies across language
lowering, editor tooling, and native cartridge generation. The corrected subset
is the current baseline. This brief proposes the release evidence and shared
semantic boundaries that should keep those fixes fixed while the language grows;
it is not a new language specification.

The [language guide](../../src/Puck.Transpiler/README.md),
[cartridge vocabulary](../../src/Puck.GamingBricks.Transpiler/README.md) and
[forge guide](../../src/Puck.GamingBricks.Forge/README.md) own current behavior.
The [retail-scale plan](retail-scale-cartridges.md) owns future procedures, typed
regions, interrupts, banking, assets and arithmetic expansion. The
[shader evolution plan](shader-pipeline-evolution.md) owns GPU authoring.

## Where to resume

The review's follow-up recorded 880 passing tests, three release packages and
isolated host insertion/shutdown on both cartridge targets. It corrected wide
comparisons, alignment and persistence, scene reads, queue bounds, integer
precision, lexical bindings, runtime expression lowering, diagnostics,
literal-preserving formatting, evaluation limits and CLI/LSP routing. At 1,000
elements, the indexed-map allocation probe fell from 89.2 MB to 1.37 MB. These
are historical observations, not performance ceilings or a release certificate.

The shader and machine work subsequently reached checkpoint
`af91b442f39046726b5f6a92852eef52d962802c`. Before release, select one current
candidate and run the combined integration evidence below. Do not redo the
original defect fixes simply because their review findings survive here.

One precise earlier observation still needs an explicit disposition in the
release evidence: inserting cartridges before the full default world's first
tick stalled. Later machine work reported successful default alias resolution
and headless operation proofs, which is useful evidence but does not by itself
reproduce that exact ordering. Re-run that trigger against the chosen candidate;
record either its closure or a current reproducer.

## Preserve the architectural decisions

Keep the language grammar separate from World and cartridge vocabularies.
Compile, lint and the language server must dispatch from the same schema and
report the same semantic refusal at an authored span. Formatting must preserve
literal meaning; decompilation must preserve admitted document semantics. Source
macros and templates remain distinct from runtime cartridge procedures.

Use immutable compile-time values and lexical scopes. Unit information must
survive caching so each destination is checked independently. Preserve exact
admitted integers through comparison, sorting, grouping and folding; do not
round them through floating point. Read a cached collection without cloning it,
and copy only when output ownership requires it. Structural hashing must agree
with structural equality and preserve first-occurrence order for `distinct`.
Budgets and cancellation must cover evaluation, expansion and output copying,
with refusals before bulk allocations.

Reuse `Puck.State` expression and predicate vocabulary, while stating target
numeric domains and hardware-specific refusals explicitly. Mandatory physical
capacity and advisory performance costing remain separate. Queue arithmetic
must saturate at the first impossible capacity; implicit writes such as loads
and clock reads belong in effect analysis, not just assignment statements.

The original review proposed shared typed intermediate information carrying
width, layout, evaluation context, source span and read/write effects. Parts of
that proposal now have concrete homes:
[CartridgeStateLayout](../../src/Puck.GamingBricks.Forge/CartridgeStateLayout.cs)
serves native storage, initialization and persistence, and
[CartridgeEffects](../../src/Puck.GamingBricks.Forge/CartridgeEffects.cs) serves
capacity and costing. Start by identifying metadata still reconstructed in
multiple consumers. Extend those homes or introduce a small validated plan only
where it removes that duplication; do not build a parallel general-purpose IR.

Before adding a procedure, wide operation or memory shape, define its operand
width, overflow policy, storage layout, scene-snapshot read context, effects and
source origin once. Both native emitters and static analyses should consume
that decision. A full IR is a means to this end, not a prerequisite for shipping
the already-corrected subset.

## Execution order and exit evidence

| Order | Work | Exit evidence |
|---|---|---|
| D1 | Audit the fixed semantic boundaries and any remaining duplicated metadata | Every admitted operation has one explicit interpretation; unsupported target combinations receive located refusals. |
| D2 | Make regression combinations durable | Expected-value tests cover both native targets and language tools, including interacting features rather than isolated happy paths. |
| D3 | Measure lowering and cancellation under bounded large inputs | Indexed reads scale with output work; structural deduplication avoids a growing linear scan; limit and cancellation failures leave no partial output. |
| D4 | Verify one packaged release candidate through World | All shipped source/document pairs agree; both targets pass actual authoring, insertion, persistence and restart workflows. |
| D5 | Expand the language using the retail plan | Each new feature carries the same semantic, native execution and release evidence, with no second vocabulary or cost model. |

D1/D2 and packaging preparation can proceed independently. New language growth
should not obscure a failing D4 case. Use the following interaction matrix when
checking D2 coverage; keep the cases in tests rather than claiming permanent
coverage through a prose checklist:

- Mixed byte/wide declaration order, all comparison branches, upper-byte save
  contents, partial saved state and repeated restoration.
- Byte versus wide literals, zero divisors, shift bounds, overflow and admitted
  integer values above the exact range of a floating-point representation.
- Scene changes combined with nested array indices; loads or other implicit
  writes combined with apparently exclusive guards; nested queue-producing loops.
- Template defaults, shadowing and loop/lambda capture; units used in several
  destination orders; expressions in properties, actions and generated objects.
- Raw/interpolated strings with whitespace and blank lines; unknown templates,
  unsupported rows, invalid function domains and refusal through convenience APIs.
- Compile/lint/LSP agreement, correct output extensions and source spans,
  cancellation, expansion limits and every shipped source/document pair.

Native verification has three separate questions. Repeated execution on one
target proves repeatability. CGB/AGB comparison uses normalized authored state
at matched completed game-frame boundaries, not raw hardware snapshots.
Independent expected results prove that both targets did not repeat the same
mistake. Exercise generated ROMs on the emulators; JSON or ROM hashes alone do
not establish authored behavior.

## Candidate release evidence

Run the applicable language, World-vocabulary, cartridge-vocabulary and both
native forge suites; regenerate or verify all shipped authored source/document
pairs. For emulator changes, use their own live conformance routing rather than
assuming forge tests replace hardware conformance. Record exact commands and
results against the candidate, not an aggregate of runs from different revisions.

Run the actual World executable for save/open/build/export/play, named machine
insertion and reset, save persistence across restart, failure/refusal paths and
clean shutdown. Include the early default-world insertion trigger above. Check
packaged firmware, cartridge admission, schema/name registries and extension
composition together with their owning workstreams. Exercise intended published
artifacts and toolchain discovery, including Native AOT if that is the release
form; a development build is separate evidence.

Keep baseline inputs, expected observations, candidate/tool identities and test
results in the game's dated milestone record. A remaining failure needs a
reproducer and a concrete closure condition. The
[machine plan](machine-extensions.md), [group and recovery plan](group-finder.md)
and [Reference game design](../game/design.md) retain ownership of their wider roadmaps.
