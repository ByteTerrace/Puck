# Puck.State.Rebuild.Corpus

The author-expression gate for the state system rebuild: every `.puck` source
this repository ships compiles clean, decompiles, and recompiles to the same
document, and every shipped world source compiles to the document beside it.

The rebuild is free to change every C# type and the lowered document shape
(rule 5). It is not free to change what an author wrote. This project holds the
corpus that says so, and every rebuild package runs it. It drives the
transpiler in process, so it needs no booted world and no GPU.

```
dotnet test tests/Puck.State.Rebuild.Corpus -c Release
```

The corpus is three trees, enumerated where they live rather than copied:

- `src/Puck.World/Assets/worlds/**/*.puck` — the shipped worlds, including the
  nine games decompiled from hand-authored JSON.
- `worlds/**/*.puck` — the asset packages. A source emitting several worlds
  (`rulepush.puck`, `beacons.puck`) decompiles as one composition
  (`WorldDecompiler.DecompileComposition`) and each world must come back as it
  was. A source emitting no world of its own is a module fragment, held through
  a source of its package that imports it
  (`EveryPackageFragmentIsImportedByASourceOfItsPackage`).
- `src/Puck.World.Transpiler/Samples/*.puck` — the transpiler's worked
  examples, the same top-level set `SamplesCompileTests` compiles. The
  `modules/` and `extensions/` subdirectories hold fragments, which are not
  root documents.

The committed cartridges round-trip in `tests/Puck.GamingBricks.Transpiler.Tests`
(`TestEveryCommittedCartridgeSurvivesADecompileAndRecompile`), beside the gate
that holds each to the document committed with it.

A source the round trip cannot close on is listed in
`CorpusSources.RoundTripExemptions` with the reason it cannot, and stays under
the document gate. `ExemptSourceStillFailsForItsReason` fails when a source
round-trips again or fails for another reason than its row names, so the ledger
only shrinks. Two sources are exempt today, both because they expand to a
document whose decompiled form is longer than the parser's source limit:
`moth-courtyard.puck` and `worlds/genesis/cards.basis.puck`.

The inventory below counts the first and third trees.
The plan this serves is [the state system rebuild](../../docs/plans/state-rebuild.md).

## Inventory

[inventory.md](inventory.md) counts every construct the corpus uses: each effect, predicate, transform and
domain arm (every arm of each union is listed, so one no source uses reads zero), each row trait, overflow policy,
draw source and draw mode, each rule-group shape, each reserved operand channel a rule reads, the pattern, set and
table sections, and each syntax node type the parsed sources carry. `CorpusInventory` walks each compiled document
by the position a construct occupies, never by text, so a discriminator two unions share is counted under the one
its position belongs to. The page is generated, and `CorpusInventoryTests` fails when it disagrees with the corpus;
re-record it with:

```
puck baselines corpus-inventory
```

The same test holds each game package's claims: every construct a package names as what it proves must be counted
in that package's own source. Tetromino claims a staged rule group, a scheduled deadline, a cycle trait, a pattern
and its `match` read, a weighted draw that restarts on exhaustion, and a saturating row.

## Decisions the corpus informed

Two state-rebuild decisions were measured against this corpus: that a rule firing
is atomic, with a `transaction` as a savepoint inside it (D4), and that a
comparison against a fractional literal is exact, folded at compile time (D6).
[State and language decisions](../../docs/decisions/state-and-language.md)
records both and what the corpus showed.
