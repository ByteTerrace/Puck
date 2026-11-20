# The authoring loop

An author changes one value in a `.puck` file and waits to see it. This memo
follows that wait from the keystroke to the frame and back to the file, says
where the time and memory go as the code stands, and designs the loop Puck
should have. It is the design behind step 3 of
[the iteration plan](../iteration.md#order-of-work). It stops at the World-side
door a small edit enters by, which that plan's
[live edit path](../iteration.md#the-live-edit-path-step-3) describes, and at
the bake's keys.

## Summary

**What is wrong today.** The memo's first profile found that the compiler was
not the slow part: everything after it was done for the whole world on every
edit, and half of a warm whole-world compile was bookkeeping
([what was measured](#what-was-measured)). Steps 1 and 2 removed the
bookkeeping and put every host behind one compile session, and parts of steps
3 and 4 made a file edit a keyed delta: a one-colour edit now recompiles one
source, merges the one root member it writes, and installs as a presentation
install with no rule compile, keeping every live edit the source did not also
change. What is still wrong:

- **The unit is a root member, held only in memory.** A changed member merges
  its whole list again across the layers that write it, and a process's first
  recomposition of a persisted image merges the root whole.
- **Validation is whole.** Every install but a state-value one validates the
  whole candidate, and so does every `world.row.set` that changes more than
  state values.
- **A file reload still requires an operator.** A published world's granted
  author needs section-scoped admission. The tape still records the source
  path and pin.
- **Caches are keyed by path and by assembly build identity**, so two checkouts
  share nothing and any rebuild of a compiler assembly empties the compile
  cache.
- **The save refuses the debug world**, which writes nearly every row through
  templates, and the decompiler prints a spelling the compiler refuses.
- **There are still several of everything.** The cartridge vocabulary orders
  its own compiler stages, three forms describe a document change, four
  identities say which code built a cached product, and five copies of the CLI
  run on one machine, most with a compile cache partition of their own.

**What this memo proposes.**

1. One **compile session**: a single type that owns sources, parses, the name
   index, each source's lowered document and each root's composed rows. The
   CLI, the World, the language server and the browser engine each hold one
   and reach the compiler's stages through nothing else.
2. An edit is **one keyed delta** from the file to the tape. The session
   produces it from the sources that changed, the authority applies it through
   the ordinary mutation door, the replay tape records it, a remote authority
   receives the same bytes, and a release is the same delta at a larger size.
3. The file edit and the live edit **meet in the middle**. A saved file
   becomes a delta between two versions of the source, never a replacement of
   the live document, so it does not discard what a person changed in the
   World. A save from the World becomes source edits chosen by where each value
   was written, proved by compiling them, with comments and layout untouched.
4. Caches are **keyed by content**: what a product was made from and the code
   that made it, never a path, an assembly's build identity or a second pass
   that re-reads every input.
5. The CLI's verbs are **thin hosts** of the session for one-shot work (a
   check, a release, a test). The edit loop belongs to the World and the
   editor, and `puck compile --watch` is deleted.

**What the owner settled** ([below](#what-the-owner-settled)): a granted
author may live-edit a published world; saved files drive the running World
first; World Studio stays a browser sandbox and utility.

**Where the build order stands** ([below](#build-order)): steps 1 and 2 are
built; steps 3 and 4 are built in part, with what is left named in each; steps
5 to 11 are not started.

## What was measured

The readings below were taken for this memo, before any build step, with a
private Release build of the CLI and a headless debug World started from the
same checkout. Shares and counts do not depend on machine load. Times do, so
the memo gives them only as a scale; the commit that adds this memo carries
the exact figures and commands. They are the profile the design answers, not
the code's state today: since step 1 a warm compile of the debug world reads
about 200 files where it read about 38,000 and allocates about 1 GB, and since
step 3 a one-colour edit merges 2 compositions where it merged 59. The defects
the table shows in `world.reload` (the full install, the reset deals, the
refusal after a live edit) are fixed.

| What ran | What it showed |
|---|---|
| `puck compile worlds/debug/debug.puck --validate --measure`, first run for this compiler build | 8.1 GB allocated, 2.2 GB peak working set, tens of seconds. It wrote about 130 cache entries totalling 75 MiB, one of them a 26 MiB composed image |
| The same command again, every source now a cache hit | 5.0 GB allocated, 2.0 to 2.3 GB peak. Faster than the first run by less than the machine's load moved either figure |
| A sampled trace of that second run (`dotnet-trace collect`, thread-time samples of the main thread) | Root source compile 4%. Composition 60%, of which choosing which file carries each imported name 25%, checking that held images still stand 12%, tracking where each value was written about 13%, and the merge itself 6%. Reading the composed JSON into the typed model 17%. The validator 18%, of which curve compilation 7% |
| `puck compile --validate --measure` of one leaf source (`day.puck`, `valley.puck`) | A third to two thirds of a second, 5 to 62 MB allocated |
| `puck lint worlds/debug/debug.puck` | The same order as the warm compile: it composes and validates the same world |
| `world.reload` in a headless debug World with no source change | About the time of a warm compile. A full install each time, a 179 to 841 byte patch, the journal cleared, and the dealt card placements reset (6038 placements to 6021). Body 0's pose survived |
| A one-colour edit to `day.puck`, then `world.reload` | A full install. One reload's work, from `world.counters world.boot` before and after: 2 sources compiled, 53 parses, 59 compositions, 74 documents read, 3 validations, 2 rule compilations, 6 arena builds |
| `world.row.set creations lawn-oak-0 document.palette[0].color …`, twice, with reads | Both edits and their reads answered inside one tool call. The counters after the first read one presentation install and one validation more, with no compile, no rule compile and no arena build |
| The same `world.row.set`, then `world.reload` | Refused: `world.reload.delta-unrepresentable:` with no reason, until the live edits were undone ([found](#defects-found-on-the-way)) |
| A source with a shade program, compiled, decompiled and compiled again | `puck decompile` wrote `shade: "program/<hash>"`, which the compiler refuses as PUCK126 ([found](#defects-found-on-the-way)) |
| `host.memory` after a boot and four reloads in one World | 22.6 GB allocated in under three minutes of uptime, 45 full collections |

Three things were not measured, and each is owned below: what a reload
does to baked meshes and settled lighting in a windowed World, whether World
Studio can still compose the debug world inside a browser's memory, and
quiet-machine timings before and after the first build step
([verification requests](#verification-requests)).

## The loop today

### The language and its compiler

A `.puck` source goes through four stages, ordered by
`WorldCompiler.Compile`:

1. **Parse.** `PuckParser` turns text into a tree that keeps comments and
   blank lines as trivia.
2. **Import walk.** `ModuleResolver.ImportModules` follows the source's
   imports. An import that names a module library (a source that emits no
   document) is resolved by copying that library's `let` and `template`
   declarations into the importing tree, renamed when the import has an alias.
   An import that names a document is left for composition. Each compile
   parses the libraries it reads afresh: the walk's `ImportContext` lives for
   one call, so every document that imports the standard's libraries parses
   them again on a cold compile.
3. **Lowering.** `WorldDocumentEmitter` evaluates the tree to one JSON
   document (or several, for a source that declares worlds) and a source map
   from each JSON pointer to the span that wrote it. A `DocumentEvaluationBudget`
   caps the work one compile may do.
4. **Canonical form.** `WorldDocumentMembers.Canonicalize` refuses unknown
   members and writes each typed value one way.

One `DocumentEvaluationBudget` covers a compile: `ModuleResolver.ImportModules`
charges it for the declarations the walk brings in, and the lowering goes on
charging the same budget, so `--measure` reports the whole compile's work.

A world is more than one source. After its own compile, a root is
**composed**: `WorldDefinitionFileSource.TryComposeChainWithImports` merges
the root's `basis` chain and every imported document, each compiled in turn
through the compile session (`WorldCompileSession`), into one JSON tree
(`WorldDocumentBasis.TryMergeInto`). The composed tree is serialized, read
into a `WorldDefinition`, **validated** whole
(`WorldDefinitionValidator.TryValidateLocally`) and, at a boot or a reload,
**admitted** (`WorldDefinitionAdmission`, which compiles the rules and
programs).

Where the time goes follows from that shape. A leaf source compiles in a
fraction of a second. The root's composition, read and validation cost the
same whether one colour changed or everything did, and they are the loop.

The profile above found three costs that were bookkeeping, not work the world
needs, about half of the warm compile. Build step 1 removed them, and the
code now works this way:

- **Choosing a name's file reads no source again.** An import names a
  document, and any source in the directory may emit that name, so
  `WorldCompileSession.TrySelectCarrier` asks `WorldSourceIndex.Claims` for
  every claim in the directory. `Claims` holds each directory's claims while
  its listing and every source's digest are the ones they were built from,
  and holds what a source declares by the digest of its bytes.
- **Proving a cached product still stands costs a stat per input.**
  `CompileInputs` remembers each file's digest and each directory's listing
  under the stamp (existence, length, last write, creation) the path had when it was
  read, and answers `CompileInput.StillHolds`, `CompileInputs.Digest` and
  `CompileInputs.List` from that memory while the stamp holds. A stamp too
  recent to vouch for a same-tick write (`CompileInputs.RacyStampWindow`) is
  never trusted. `WorldSourceWatch` polls through the same memory.
- **A composition tracks where each value was written only when asked.**
  `WorldSemanticValidator.ValidateComposedWorld` composes without a
  `WorldDocumentOrigins`. A finding the source's own layer does not author
  composes the world again with origins, once, to be placed in the source
  that wrote it (`WorldFindingLocator`). A save and staging still compose
  with origins, since they read every one.

### The caches

| Cache | Holds | Keyed on | What goes wrong |
|---|---|---|---|
| Held declarations (`WorldSourceIndex`) | What each source declares it emits | The digest of the source's bytes; on disk, also the compiler's identity | Fine as a table. The reads that feed it are the cost above |
| Held compiles (`WorldCompileSession.TryCompile`, in memory and under the `compilations` directory) | One source's documents and every file fact its compile read | The compiler's identity, the source's full path, and whether tests are lowered. Served only while every recorded input re-reads the same | The path is in the key, so two checkouts holding the same bytes share nothing. The identity is the build identity of every non-framework assembly the compiler references (`WorldCompileSession.CompilerIdentity`), so any rebuild of any of them empties the cache. A source is compiled twice when one caller wants tests and another does not |
| Held compositions (`WorldDefinitionFileSource.HoldComposedImage`, persisted through `IWorldCompositionStore`) | A composed document whole, with its chain's bytes | The resolved path, the machine catalog's fingerprint and the source type; valid while every input re-reads the same | Whole-document images: the debug root's is 26 MiB, written again for each compiler build. In memory an image keeps the layers it composed from (`WorldDefinitionFileSource.ComposedLayers`), so a recomposition merges again only the root members a changed layer writes; a persisted image keeps no layers, so a new process's first recomposition merges whole |
| Compiled worlds (`CompiledWorldCache`, `.puckb`) | The drawn definition, assets and curves, as chunks | A file named for the document's path; each chunk by the code that derives it (`CompiledWorldChunks.EngineBuild`, a closure over the methods the producers reach) and its recorded inputs | Keyed by reached code, which is the right identity, but one chunk covers the whole definition, so one row's change derives it all again |
| The device cache (`WorldCacheRoots`) | The three directories above for compiles, compiled worlds and meshes, per user or under `--cache-dir` | — | The compile directory is bounded at 256 MiB (`WorldCompileSession.MaxPersistedBytes`). One compiler build's entries for the debug world are about 75 MiB, so three or four builds fill it and a machine with several worktrees evicts constantly |
| The mesh store (`SdfMeshStore`) | Baked meshes | Geometry (`SdfMeshGeometryBuilder`) and the bake's identity; each bake stage's products by that stage's own code closure | Not this memo's: the iteration plan's step 2 keyed the stages |
| The shade program table (a document's root `shades`) | Each authored shading program once | The program's name and the SHA-256 of its canonical form (`ShadeReference.KeyOf`) | Nothing. It is the model the others should follow: identity is content, so equal programs are one row wherever they are written |
| Specialized shading kernels (`SdfWorldResidency.PrepareShade`, the shader cache) | Compiled kernels for the active programs | The programs' identities, the op table's fingerprint and the kernel sources' content | Nothing for this loop: the interpreter shows an edited program on the next frame and the specialized kernel follows |

Four different identities answer "which code made this": assembly build
identities for held compiles, a reached-method closure for compiled worlds,
another reached-method closure for the bake, and an include closure for
shaders. The [puck-world skill](../../../.claude/skills/puck-world/SKILL.md)
already states the rule that a whole assembly's identity is not a derivation
key; the compile cache does not follow it.

### The CLI

`puck compile`, `puck lint`, `puck format`, `puck test`, `puck decompile`,
`puck migrate` and `puck lsp` all sit on the same assemblies as the World, so
there is one implementation of each stage, and since build step 2 every host
reaches it through one `WorldCompileSession` (`WorldCompiler` is internal to
it). What still differs is the order a host asks the session for things:

- `CompileCommand.ExecuteWorldCompilation` compiles the root through the
  session's `Compile`, unheld, then composes and validates when `--validate`
  is given. It does not lint.
- `puck lint` and the language server run `WorldSourceDiagnostics`: the
  session's `CompileBuffer`, lint, compose, validate, then lint references
  over the composed world.
- A boot runs the session's `TryCompile`, then its `TryLoadForAdmission`. A
  reload runs `WorldDefinitionLoader.TryLoadFileForEdit` over the session as
  its document source, which composes, draws and settles the source without
  admitting it.
- `world.save` runs its own load, compose and proof (`WorldSourceSave`),
  through the session.
- `puck test` and `SweepRoute.TryLoad` compile through the shared session
  without holding the generated worlds (`TryCompile` with `hold: false`).

Each run is a new process. Startup is cheap (`puck --help` and a leaf's lint
or format answer in about half a second), so a one-shot check of one file is
fine. A check of the world is not: the process starts cold, holds nothing
from the last run, and does the whole-world work above. Agents run exactly
that check after every `.puck` edit, on a machine where several lanes each do
the same.

The World's `WorldSourceWatch` is the one watcher: it follows every file fact
the compile read, the standard package's included. `puck compile --watch`,
which watched only the source's own directory, was deleted in build step 2.

Five copies of the CLI can run on one machine. Each is a separate build, and
a build that differs in any compiler assembly has its own partition of the
compile cache:

| Copy | Who runs it | How it goes stale |
|---|---|---|
| The global tool on `PATH` | An editor's language server, a terminal | It keeps its revision while the checkout moves. `puck --version` warns when the commit differs, even for a commit that changed no code, and the cure is a manual pack and reinstall |
| The checkout's `src/Puck.Cli/bin/Release` | The MCP hook, by its `puck-build-ready` stamp | A running process holds the assemblies, so the next build cannot replace them |
| `.claude/mcp-install` | The MCP adapter, through the launcher stub | The stub follows activated builds and rolls back a build that fails to prove itself |
| A lane's private copy | A lane's verbs | Copied by hand after every merge, by a rule in the verification skill |
| The World build store | `puck test`, `puck canary`, `puck_world_start` | Keyed by the content of the World's source closure (`WorldArtifactKey`), so it is never stale, only missing |

The last row is the mechanism that works: a content key, built once, run from
a store that no build writes into. The other four are procedures around its
absence. A stale global tool is not only an interruption for agents. It is the
language server an author's editor runs, and an old one reports errors for
members the current vocabulary accepts.

### The World

- **`world.reload`** (`WorldReload`) compiles, composes, draws and settles the
  current source on the thread pool without admitting it
  (`WorldDefinitionLoader.TryLoadFileForEdit`), answers a source that still
  composes to the revision the authority holds (`WorldServer.SourceRevision`)
  as unchanged, and otherwise cuts the delta between the two revisions from
  their member bytes (`WorldReloadDelta.Between` over
  `WorldDefinitionMembers`). The authority reconstructs the next revision off
  its tick (`WorldRebuildPreparation`), merges it over the live document
  (`WorldReloadDelta.TryMergeOver`, the source winning and naming each row both
  changed), validates the result once and installs it by which root members
  changed (`WorldDocument.ReloadInstallKindOf`): a colour edit is a
  presentation install with no rule compile. The sender still serializes both
  revisions once to slice them. A file reload adds its accepted overlay to
  the undo journal and preserves body poses and unrelated live edits.
- **`world.watch on`** (`WorldSourceWatch`) polls the stamps of every file
  fact the last compile read and queues an ordinary `world.reload` after a
  quiet moment. A local operator's source boot seeds the watch with the
  dependency answers the loader used.
- **`world.load`** validates another file and submits its whole definition,
  refusing over unsaved edits unless forced.
- **Document mutations** (`world.row.set`, `world.row.add`, `world.row.remove`,
  `world.row.step`) compose one change over the installed document, validate
  the candidate and install it by kind. `WorldDocument.InstallKindOf` answers
  value, presentation or full, and a creation edit that changes shading alone
  (`WorldPrototype.DiffersInShadingAlone`) keeps the arena, the rules and the
  population. This is the door a small edit should use. It still validates
  the whole candidate, and it takes JSON where a source takes units and
  expressions.
- **`world.save`** (`WorldSaveSnapshot`, `WorldSourceSave`) writes the live
  document back into the sources that author it
  ([round-tripping](#round-tripping), below).
- **`world.shaders.reload`** recompiles the SDF kernel sources a tree carries
  for the running backend, through the shader cache the build shares. It is
  the engine developer's loop and is manual. An authored shading program is
  document data and follows the document's loop.

A live edit and a reload compose: the reload merges over the live document,
and a row both changed takes the source's value and is named in the verdict.

What a reload does to baked meshes and settled lighting was not measured
here, since it needs a windowed World
([verification requests](#verification-requests)).

### The editor

- **VS Code** (`editors/vscode`) starts `puck lsp` from `PATH`. It carries a
  TextMate grammar of its own for colouring before the server answers, a
  second description of the language's tokens that nothing generates.
- **The language server** is one class, `PuckLanguageServer`, hosted over
  stdio by `puck lsp` and inside the browser engine by `BrowserLanguageServer`.
  It diagnoses in two tiers through `WorldSourceDiagnostics`: the source tier
  compiles the buffer through the session's `CompileBuffer`, and the semantic
  tier composes and validates. Which open document reads which file is the
  session's own record of what each tier read (`CompileInputs`), at any depth,
  so an edit to a library re-diagnoses every open document that imports it
  however indirectly. A hover or a completion over a state row reads the
  buffer's held compile rather than compiling it again. Completion and the
  outline still parse the buffer again.
- **A fragment is not checked against its world.** Most sources are fragments
  that some root composes. The semantic tier validates a root only, so an
  author editing `valley.puck` learns of a refusal in the composed world when
  the World reloads, not in the editor. World Studio closes this for itself by
  checking the manifest's root; the language server has no notion of the
  package a file belongs to.
- **World Studio** runs the same compiler, built for the browser
  (`Puck.World.Browser`), in two workers: one hosts the language server, one
  composes, validates and previews. It shares no cache with the CLI or the
  World, and holds its compiles in memory. Its preview is the state and rule
  engine with no renderer, so it cannot show a look. Its drafts live in
  browser storage: nothing reaches the author's files or a running World.

### Round-tripping

`world.save` is closer to the target than any other part of the loop. It
diffs the live snapshot against the composed source, asks the composition's
origins which document authored each changed value, and has
`WorldSourceEdits.TryRewrite` replace only the changed spans, so every
comment, `let` and blank line outside them is kept. A value read from a `let`
is written in the `let`. It compiles the rewritten sources and composes the
tree again to prove the result before writing anything.

Its limits are these:

- **A generated row refuses.** A row a template, a compile-time `for` or a
  module wrote is refused by name. The debug world writes nearly everything
  that way, as [ergonomic is optimal](../../decisions/state-and-language.md#ergonomic-is-optimal)
  encourages, and `world.save` refuses on the debug world outright
  ([open items](../open-items.md#edit-to-running-hot-reload-and-boot)).
- **A whole-row rewrite prints what the compiler refuses.** When a changed
  node has no value span of its own, the row is printed by `WorldDecompiler`,
  which has no case for a shading program and writes its table key as a
  string. The compiler refuses that spelling, so the save's proof fails.
  `puck decompile` has the same defect on its own.
- **It costs several whole-world compiles.** Each touched source is compiled
  at least three times and the tree composed twice.
- **It writes over whatever is on disk.** Nothing checks that a file still
  holds the bytes the spans were computed from.
- **The source is not always the author's file.** A composition boots from
  staged copies and saves into a sidecar JSON beside the source
  (`PuckWorldLoader.TryBootComposition`). A hosted world's origin is a store.
  World Studio's is browser storage.

[The modeling plan](../modeling-authoring.md#decision-4-what-the-running-world-contributes)
already settled the principle the fix rests on: a lowering keeps a per-field
inverse, or the save refuses it by name.

### Remote and published worlds

A world reaches production by a release. `puck world prepare` packages the
composed documents, `puck world release prepare` writes a content-addressed
manifest, and `puck world release deploy` moves a managed group to it. The
[runtime plan](../runtime-and-delivery.md) already describes the transition
as this memo would: the authored delta between two releases is computed apart
from live state, applied only as admitted changes through validation and the
authority, kept by row identity, and refused where it conflicts with a live
edit.

A live edit can reach a remote authority today. `world.reload` submits its
patch over the same wire as any submission, and the authority prepares it
against the declarations the patch names. The trust rule is coarse: a rebuild
is refused unless the sender is the operator
(`WorldDocument.ApplyRebuild`). A document mutation is finer. It is admitted
against the acting principal's grants and the write footprint it declares
(`WorldRecipientView.Admits`). [The disclosure plan](../disclosure.md#3a-a-declaration-is-judged-inside-a-scope-its-declarer-reads-whole)
designs the grant that lets a seat edit the world's own document.

The authority holds compiled documents. The sources stay with the author, so
whatever compiles them has to run where the author is.

### Determinism and recording

An edit is a change to the authority's document at a tick, and it is input:
the tick it arrives on is recorded, as a console line's is.

- A mutation is recorded on the replay tape whole.
- A reload is recorded as its kind, its origin path and the content hash of
  what was read (`WorldDocument.ApplyRebuild`). The tape never holds the
  document. A replay reads the same path again and refuses by name when the
  file has moved. A session with edits therefore replays only against a
  working tree that still holds each recorded version of each file.
- The compiler runs outside the simulation. What rule 4 needs from it is that
  the same sources give the same document on every machine, which
  `puck compile --tree --check` shows across processes.

### Several authors at once

- **A person and a file.** A reload merges the file's change over the live
  document, so a person's live edits survive except on a row the file also
  changed, where the file wins and the verdict names the row. The reload still
  clears the journal, so those edits can no longer be undone one by one.
- **Two writers of one file.** `world.save` writes its target without
  checking it still holds what the save read.
- **An agent beside a person.** An agent's `puck_exec` mutations in the
  owner's World are stamped as the console, so they are not distinguishable
  from the owner's console lines and cannot be undone as a set. The
  [editor plan](../editor.md#decisions) already decides that undo is per
  principal.
- **Several lanes on one machine** share the per-user compile directory,
  which the bound above lets them evict from each other.

## The target loop

### One invariant

> At every revision, each root's document is the one a compile of the same
> file contents from nothing would produce, and the delta between two
> revisions, merged over the first, reproduces the second.

Everything below is built behind it. [Invariants and what enforces them](#invariants-and-what-enforces-them)
says what holds it and where the guarantee stops.

### One compile session

**The problem.** Five drivers order the stages, each with its own caching,
and no process keeps what the last compile learned in a form the next can use
for less than the whole world.

**The proposal.** One type, the compile session (`WorldCompileSession`). It is
what the compile cache, `WorldSourceIndex`, the document composer, the source
loader and the held images in `WorldDefinitionFileSource` become when merged,
not a new layer beside them; step 2 merged the first, third and fourth. It keeps the existing project
seams. Files, parses and the import walk belong to the vocabulary-neutral
core, `Puck.Transpiler`, so the cartridge vocabulary compiles through the
same session and stops being a second driver
(`CompileCommand.CompileCartridge`). The world's lowering stays in
`Puck.World.Transpiler`. The half that turns layered documents into rows, a
typed definition and a delta lives in `Puck.World.Schema`, where composition
lives today, so a host that holds only JSON documents (a hosted world read
from its store) uses the same rows without the transpiler. It owns:

- **File facts.** Each file's content digest and each directory's listing,
  remembered under the file's size and modification stamp. This is
  `WorldSourceWatch`'s technique, moved to `CompileInputs` where every reader
  shares it.
- **Parses**, by content digest, shared by every source that imports a
  library.
- **The name index** of each directory, derived from the parses and valid
  while the listing and stamps hold.
- **Each source's lowered document** and source map, with what it read.
- **Each root's rows.** For every section and row key of the composed world:
  the row's canonical digest, the layer that wrote it and the typed row.
  The session does not hold a composed JSON tree. A row is merged from the
  layers that write it when it is asked for, so memory follows the sources.
- **The revision.** A counter that moves when any root's rows do.

Its doors are few: open a root, tell it which files or buffers changed, and
take from it a revision's delta, typed definition, diagnostics and the place
a value was written. A stage is reached by handing it what only the session
can make: the import walk and the lowering take the session's reader, not a
path, so a caller that holds no session has nothing to pass
(`WorldCompiler.Compile` and `ModuleResolver.ImportModules` lose their
path-taking forms). The whole-tree composition
(`WorldDefinitionFileSource.TryComposeChainWithImports`) is replaced by the
row composition and deleted, so no caller is left a second way in.

Every host holds one:

| Host | Lifetime | Reads sources from |
|---|---|---|
| A `puck` verb (`compile`, `lint`, `test`, `decompile`, `migrate`) | One process | The file system, warmed from the content store |
| `puck lsp` | The editor's session | Open buffers over the file system |
| The World | The run | The file system, through the source watch |
| The browser engine | The page | The mounted in-memory workspace |

**Why a library and not a service.** A compile daemon that the CLI, the
editor and the World all call would keep one warm state per machine. It is
rejected: the browser cannot use one, it is one more long-lived process whose
build can be stale (the problem the MCP install chain exists to manage), and
once a compile costs what the edit costs there is little warmth left to
share. Processes share finished products through the content store instead.

**What it costs.** The merged types have many callers; the change is
wide and mostly mechanical. The risk is a host that keeps a private path to a
stage. Taking the path-taking forms away makes that a compile error.

### Compiling in proportion to the edit

**The problem.** A one-value edit recomposes, re-reads and revalidates the
world.

**The proposal, in three layers.** Each layer stands alone and each is
measured before the next is built.

1. **A changed source, its rows, and nothing else.** When a file changes, the
   session lowers the sources that read it and compares each new document
   with the one it held, row by row. A source whose document is unchanged
   stops there. For each changed row key the session merges that key again
   across the layers that write it, reads the one typed row, and splices it
   into the previous definition, which shares every other row. The delta is
   the set of rows whose digest moved. Sources that must be lowered again
   are independent of each other and are lowered in parallel.
2. **Validation of what the delta can reach.** Each validator belongs to a
   partition that declares the parts it owns and reads
   (`WorldDefinitionValidator.Partitions`), and a run that reads past them
   refuses. A delta that touches creations runs the partitions that read
   creations; within the creations, a row whose change differs in shading
   alone is judged alone. A delta that reaches the `document` partition, which
   still owns most sections, runs it whole, off the tick, as before. The
   mutation door runs the same runner (`TryValidateChange`).
3. **Template expansions kept by what they read.** After layer 1, an edit to
   a shared `let` in a library still lowers every source that imports the
   library, where the same value written as a literal lowers one. That makes
   a factored value cost more to tune than a pasted one, which
   [ergonomic is optimal](../../decisions/state-and-language.md#ergonomic-is-optimal)
   calls a compiler defect. The fix keeps each template expansion and each
   `let` by the digest of its declaration, its evaluated arguments and the
   names it read, so only the calls that read the changed declaration expand
   again. `WorldShadeLowering` already keeps shading programs this way within
   one compile.

**Why not a rewrite as a query-based compiler.** The established design for
an incremental compiler is per-declaration units with recorded dependencies
and early cutoff, as in
[Zig's incremental compilation](https://mlugg.co.uk/posts/incremental-compilation-internals/).
It would be the design to take if the cost were spread across the pipeline.
The profile says it is not: 96% of the warm compile comes after the lowering.
Layers 1 and 2 remove that. Layer 3 is the per-declaration idea applied where
the ruling requires it, inside the existing lowering.

**Where an order matters.** A keyed row's place in its list can decide an
ordinal (bodies are numbered by placement order). An inserted or removed row
is part of the delta with its position, and the authority treats it as it
treats the same mutation today, which may be a full install. The session
never hides an order change to keep a delta small.

**What it costs and risks.** A missed dependency serves a stale row and
nothing reports it. That is the dangerous direction, and the invariant's
enforcement is aimed at it.

### An edit is one keyed delta

**The problem.** Three forms describe a document change: the keyed JSON
overlay `WorldDocumentBasis.Diff` produces (used by the reload's wire delta,
by the save and by a JSON world that names a basis), the typed
`WorldMutation`, and the pointer list `WorldSourceEdits` walks. A file edit
is cut into the first between two source revisions, member by member, and
applied as a rebuild.

**The proposal.** The keyed overlay is the one form an edit travels in. It
exists, it is proved by merging, and its bytes are already the wire format.
What changes is where it comes from and where it goes:

| Stage | Today | Target |
|---|---|---|
| Made by | The sender, from the root members whose bytes moved between two composed revisions (`WorldReloadDelta.Between` over `WorldDefinitionMembers`) | The session, from the rows whose digest moved between two revisions of the source |
| Describes | The source revision the authority took against the new one | The same |
| Pinned by | Content pins of both revisions | The same pins, kept as a digest over row digests, so checking them costs what the delta costs |
| Applied as | One journal entry for the accepted source edit, installed by `WorldDocument.ReloadInstallKindOf` | Section-scoped admission for granted authors on published worlds |
| A row both changed | The source's value wins and the verdict names the row | The same for an author's save, since saved files drive the running World first ([what the owner settled](#what-the-owner-settled)); a release over a live world keeps the runtime plan's refusal by row |
| Recorded as | A path and a content hash | The delta's bytes |
| Sent to a remote authority as | The patch, operator only | The same bytes, admitted like any mutation |

Describing the change between two source revisions, never against the live
document, is what lets the file and the World be edited together, and that
part is built: the authority holds the source revision it last took
(`WorldServer.SourceRevision`), every row the live session changed and the
file did not keeps its live value, and a state row is seeded again only when
the delta changes its declaration. Section-scoped admission and tape
recording of the delta remain.

This is the release transition at the size of one save. The runtime plan's
authored delta between two releases, applied as admitted changes and refused
on a conflicting live edit, is the same computation by the same session
through the same door. Puck needs one of these, not a small one for editing
and a large one for shipping.

`world.reset` and `world.load` stay as the verbs that read a source afresh and
discard what the session changed.

**The seam with the iteration plan.** Its
[live edit path](../iteration.md#the-door-the-authoring-loop-asks-for) owns
the door: how a small change is admitted, classified and shown. The door takes
a keyed delta with the revision it was made against (`WorldRebuildRequest`
carrying a `WorldReloadDelta`), which is all this memo needs; where the two
differ in shape, the door wins and the session adapts.

### Saving an edit to source

**The problem.** The save refuses most rows of a world written with
templates, prints a spelling the compiler refuses when it rewrites a row
whole, costs several whole-world compiles and can overwrite another writer's
change.

**The proposal.** The save is the edit path run backwards, on the same
session.

1. **What to save** is the delta between the source revision the authority
   last took and the live document: the journal's net effect, already keyed
   by row.
2. **Where each value was written.** The source map today records a span, a
   value span and a module instance. It gains the *derivation* of each
   lowered value, one of:
   - a literal in the row, edited in place;
   - a `let`, edited at the `let`;
   - a template argument that reached the output unchanged, edited at the
     call that passed it;
   - a default or a literal inside a template body, which every expansion
     shares;
   - a computed value, editable only where its lowering declares an inverse
     ([the modeling plan's rule](../modeling-authoring.md#decision-4-what-the-running-world-contributes));
   - a generated value with no author-facing site.
3. **How far the edit reaches.** Before writing, the session applies the
   candidate source edit and takes the delta it produces. If that delta is
   exactly the one being saved, the edit is right. If it changes other rows
   too, as an edit to a shared `let` or a template body does, the save names
   those rows and writes only when the author asks for the shared edit. This
   replaces today's proof by recompiling the world with a proof that costs
   what the edit costs.
4. **What cannot be placed** is refused by name with the generator's
   location, as today. The JSON delta over the source as its basis stays the
   fallback the refusal offers, never the default
   ([the editor plan's decision](../editor.md#decisions)).
5. **A new row** made by duplicating a generated one is written as its
   generator's call with the new id, not as a printed row.
6. **Text.** Only the chosen spans change, as now. The statements the save
   touched are printed through `PuckPrinter`, so a file that was at the
   formatter's fixed point stays there, and nothing else in the file moves.
7. **Files.** Each file is written only if it still holds the digest the
   session computed its spans from. Otherwise the session takes the file's
   new content as a source edit first, then saves over that.

The decompiler prints a shading program as the `surface`, `light` and
`compose` block the grammar already accepts, read back from the program's
canonical graph. A table key in a source would be a spelling no author can
read or edit, so admitting the key string is the wrong fix.

**What it costs.** The derivation has to be carried through template
expansion and `with`, which the lowering does not do today. Until a lowering
carries it, that value is "generated" and refuses, so the step can land one
construct at a time.

### What each cache becomes

| Today | Becomes |
|---|---|
| Held declarations, held compiles, the composer's input checks | The session's file facts and parses in memory. On disk, one content store: a product is named by the digest of the code that made it and the digests of what it read, so the same bytes compiled in two checkouts are one entry and a lookup is a key comparison with no second read of the inputs |
| Assembly build identities as the compiler's identity, with any applied hot-reload update folded in | A reached-method closure over the compiler (`CodeClosure`, as `CompiledWorldChunks.EngineBuild` and the bake's stage keys use), with the update still folded in. An engine edit the compiler does not reach keeps every compile |
| Whole composed images | Gone. A root persists a small manifest of its row digests; the rows are content-addressed entries shared between roots and builds |
| The compiled world's whole-definition chunk | Per-row derived products keyed by the row's digest and the producer's closure: a curve's compiled spline by its curve row, an asset by its asset row |
| Two compiles of one source, with and without tests | One product. A test block's world is a row set derived from it |
| The 256 MiB bound on compiles | A bound per store sized in worlds and builds, counted and reported, since entries shared by content shrink what one more build adds |
| The mesh store, the shade table, the shader cache | Unchanged by this memo. The session's contract with the bake is that a row whose digest did not move presents the same geometry key |

### The CLI's role

- A verb is a **host of the session** for work that ends: check this file,
  check this package, build this release tree, run these tests. It holds no
  driver of its own.
- **The edit loop is not the CLI's.** The World watches the sources it booted
  from and the editor's language server follows its buffers. A world booted from a source
  tree by its local operator watches it from boot; an explicit `world.watch
  off` stops it.
- **A world check is cheap from a cold process** once the content store holds
  rows and validation results: the process loads the root's manifest, lowers
  the files whose digests moved and validates what the delta reaches.
- **A long-lived host answers faster still.** An agent that edits sources
  beside a running World reads the World's own diagnostics for the revision.
  The MCP adapter can ask the World, and needs no session of its own.
- **One identity for a built tool.** The CLI is built into the same
  content-keyed store as the World (`WorldArtifactKey` over the CLI's
  closure) and `puck` on `PATH` is the launcher stub, which resolves the
  checkout it is run in to that checkout's build and builds on a miss. The
  global reinstall procedure, the hand-made lane copy and the commit
  comparison in `puck --version` go. An author with a released tool and no
  checkout runs the released build, as now.

### The editor

- The language server holds a session. Completion and the outline read the
  parse and the lowered rows the session already has. Its dependency table goes.
- A fragment is diagnosed in the world that composes it. The session knows a
  file's package from the manifest beside it and reports the composed world's
  findings on the file and line that wrote the row, because the delta carries
  where each row came from.
- The World can serve the language server from its own session over an
  attachment, the channel the MCP adapter already uses. An editor attached to
  a running World then shares its compiles, sees live values on hover, and is
  the editor program [in-world editing](../open-items.md#in-world-editing)
  asks for with nothing new behind it. `puck lsp` attaches when a World is
  running the package and stands alone otherwise.
- The TextMate grammar is generated from the vocabulary, or deleted in favour
  of the server's semantic tokens.

### Remote authorities, trust and scale

Asked at the scale the purpose sets: every zone, character and server is a
world with its own authority, and many authors work at once.

- **The sources stay with the author; the authority takes deltas.** A zone's
  author compiles on their own machine and submits the delta to the zone's
  authority. The authority never trusts it: it validates what the delta
  reaches and admits it against the author's grants, as it does a builder's
  placement. A grant over one plot's rows lets that author live-edit that
  plot and nothing else. No operator is needed for a look tweak.
- **A shared package is pinned.** An edit to a package many worlds import
  produces a delta for each root in the author's own tree. Another author's
  world takes it when that author moves their pin, by the same door.
- **The world cannot grow without proportional cost.** The debug world is
  under two megabytes of source and already needs two gigabytes and tens of
  seconds to check. A world a hundred times larger cannot be checked whole at
  all.
- **A session with edits replays from its tape alone**, because the tape
  holds each delta. A replay or a restore across a code revision refuses with
  the reason, as [in-world editing](../open-items.md#in-world-editing)
  already asks.
- **An agent is a principal.** Each attachment acts under an identity of its
  own, so an agent's edits beside a person are attributed and undo as a set.

## Options weighed

Each row names what was considered, the evidence, the failures it closes and
the ruling the choice rests on. The failures are the ways the loop lets an
author down: a small edit waits on whole-world work (wait), a stale product
is served (stale), two hosts disagree (disagree), an edit is lost (lost), an
edit cannot be saved (unsaved), the loop's garbage stalls the World (stall),
a session with edits does not replay (replay), a spelling costs more to tune
(spelling), and lanes evict each other's caches (thrash).

| Decision | Options | Evidence | Chosen, and what it closes | Ruling |
|---|---|---|---|---|
| Where compiler state lives | Per-driver caches (today); a machine-wide compile service; one session type per host over a content store; the World as the only host | Five drivers, three dependency graphs. The browser cannot reach a service. The MCP install chain shows what a long-lived tool process costs to keep current | One session type per host. Closes disagree; makes wait and stall solvable in one place | Rule 7: a second implementation is a defect. Puck plays nice with its host |
| How fine the units are | Whole world (today); changed source then changed rows; per declaration throughout; a query-based rewrite | 96% of a warm compile is after lowering. A leaf lowers in a fraction of a second | Changed source then changed rows, with kept template expansions as a later layer. Closes wait, stall, spelling | Changes pay dividends: each layer is measured and each opens the next. Ergonomic is optimal for the third |
| How a file edit reaches the authority | Replace the document (`world.reload`); a delta against the live document (today's wire form); a delta between source revisions through the mutation door | The measured refusal after a live edit; the journal cleared and deals reset on an unchanged reload; `world.row.set` landing as a presentation install | The delta between source revisions. Closes wait, lost, replay | One pipeline for change ([puck-world](../../../.claude/skills/puck-world/SKILL.md)); the runtime plan's release delta |
| The delta's form | The keyed overlay; a list of typed mutations; text diffs | The overlay exists, is proved by merge and is the wire format. Mutations are what the door runs. Text diffs carry no meaning to an authority | The overlay end to end, lowered to mutations at the door. Closes disagree | Rule 7 |
| Saving to source | Refuse generated rows (today); print whole rows; edit where the value was written and prove it; always write a JSON overlay | The debug world refuses outright. A printed row loses the author's template and trips the shade defect | Edit where the value was written, refuse by name what has no site, overlay only as the named fallback. Closes unsaved, lost | The editor plan's save decision; the modeling plan's inverse rule; authors rule |
| Cache identity | Path, assembly identity and a re-read of inputs (today); content of inputs and reached code | Path keys share nothing across checkouts. One debug compile writes 75 MiB against a 256 MiB bound. The validity pass is 12% of a warm compile | Content and reached code. Closes thrash, stale, part of wait | A derived product is a cache, identity computed from inputs ([decision](../../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth)) |
| The CLI in the loop | A watch verb beside the World's; a daemon-backed CLI; verbs as one-shot hosts | `--watch` misses imports outside its directory and writes a file nothing reads | One-shot hosts; the loop is the World's and the editor's. Closes disagree | `puck` verbs are tooling |
| Which CLI runs | Global tool, bin, MCP install, lane copy, build store (today); one content-keyed store behind a stub | Four of the five are procedures for staleness. The fifth is never stale | One store and the stub. Closes disagree | Rule 7; durable knowledge lives in the repository, not in a machine's installed tool |

## Duplicates to remove

| Two of | Where | One home |
|---|---|---|
| Drivers that order the stages | The world's are on the session (step 2). Left: the cartridge vocabulary's `CompileCommand.CompileCartridge`, `CartridgeLanguageServices` and `CartridgeSource` | The session |
| In-memory compile caches | The held images in `WorldDefinitionFileSource`, beside the session's held compiles | The session |
| Compiles of one source | The document compile and the tests compile | One product |
| Forms of a document change | The keyed overlay (which `WorldReloadDelta` now carries member by member), `WorldMutation`, `WorldSourceEdits`'s pointer walk, `WorldProjectionDelta` | The keyed overlay, made by the session |
| Code identities for a derived product | Assembly identities in `WorldCompileSession`, the closures in `CompiledWorldChunks` and the bake, the shader include closure | One reached-code helper, used per stage |
| Validations of the same bytes | A preview's `ComposeSource` then `Compile` in the browser engine. A reload's sender no longer validates; its authority validates whole | Narrowed validation, once per side that must not trust the other |
| Ways to run the checkout's CLI | The global tool, the lane copy, the MCP install, the build store | The build store behind the stub |
| Token grammars | `PuckParser` and the VS Code TextMate file | The parser, with the grammar generated or dropped |

## Invariants and what enforces them

| Invariant | Enforced by | Where the guarantee stops |
|---|---|---|
| A revision's document is what a compile from nothing produces | There is one path. A compile from nothing is the session with no held state, so no second implementation exists to disagree | A lowering that depends on something it did not read through the session |
| Every read a product depends on is in its key | The stages receive files, parses and declarations only from the session's reader, which records the read. The stage assemblies have no other route to the file system: the direct reads that remain today (`WorldSourceSave`) move behind the reader, and a banned-call check on the transpiler projects holds it | A dependency that is not a read: evaluation order, a process-wide table. `puck compile --tree --check` and the law below see those |
| A delta merged over its base reproduces its target | The session proves each delta by merging it, as `WorldReloadDelta.Between` does member by member, comparing row digests | Nothing: a delta that fails the proof is not sent |
| Incremental equals clean over real edits | A law that replays a recorded sequence of source edits through one session and compares each revision's row digests with a fresh session's. It is an invariant test running cannot show, and it claims equality because incremental work claims to change nothing | Edit shapes the corpus lacks. The corpus grows with each defect |
| An authority never trusts a sender's validation | The authority validates what the delta reaches itself. Narrowed validators take a view that holds only the sections they declared, so a validator cannot read a section it did not name | A cross-row rule whose reach is declared too small. This is a trust boundary and takes an adversarial review until a check covers it |
| A save writes only what its author asked for | The save applies its source edit in the session and compares the resulting delta with the one it was asked to save, before writing | Nothing: an edit that reaches further is named, not written |
| A save never overwrites a change it did not read | Each file is written only against the digest its spans were computed from | Two saves racing inside one file system write, which `AtomicFile` already orders |

## Build order

Each step names the reading that shows it worked. The loop clock
(`world.iteration`) is the instrument: it already counts a loop's compiles,
compositions, root members merged, documents read and validations, and a count
a check names that it does not yet report is added to it, never to a second
instrument. Step 9 needs nothing from the others and can start at any time;
the rest follow the order given unless a step says otherwise.

Steps 1 and 2 are built. The three bookkeeping costs are gone
([the language and its compiler](#the-language-and-its-compiler) describes the
code now), and `WorldCompileSession` is the one door into the compiler for every
host, with library parses shared by content (`PuckSourceTable`). What step 2
did not merge goes elsewhere: the held composed images in
`WorldDefinitionFileSource` go with step 6's content keys, `WorldSourceIndex`
stays a static index the session reads, and the cartridge vocabulary's own
stage order is in [the duplicates](#duplicates-to-remove).

3. **Deltas from changed sources.** Built in part: a value edit recompiles
   only the source it is in, because a compile rests on what a sibling it
   resolved a name in declares (`CompileInputKind.Declaration`), not on the
   sibling's bytes; a recomposition reuses every layer that composed to the
   same image and merges again only the root members a changed layer writes
   (`WorldDefinitionFileSource.ComposedLayers`, counted as
   `world.boot.composition-members`); and the incremental-equals-clean law is
   `IncrementalCompositionLawTests`. Left: the unit is a root member, not a
   keyed row, so a changed member merges its whole list again; changed sources
   are not lowered in parallel; layers are held in memory only, so a process's
   first recomposition of a persisted image merges whole; and the delta is cut
   by the reload's sender from two composed revisions rather than emitted by
   the session from the rows that moved, so the sender still reads the whole
   next revision into the typed model and draws it
   (`WorldDefinitionLoader.TryLoadFileForEdit`). *Check:* for a one-value
   edit, the first after a boot included, the session's counters read one
   source lowered, a handful of rows merged and no whole-document
   serialization, and the incremental-equals-clean law passes over an edit
   corpus drawn from the debug world.
4. **Join the door.** The local file edit path is built. `WorldSourceWatch`
   submits a saved edit through the reload boundary. The authority merges the
   change over the live document, names conflicts where the source wins and
   records the accepted overlay as one undo entry. Presentation delivery
   carries changed creation and shade rows, and value delivery uses changed
   snapshot rows. The [live edit path](../iteration.md#the-live-edit-path-step-3)
   describes the mechanisms and counters. Section-scoped admission for remote
   authors stays with the [published-world decision](#what-the-owner-settled).
   Left: in the debug world a one-colour save still delivers every creation in
   the roster, because `WorldPresentationRows.Between` finds moved rows by
   instance and the reloaded roster arrives with new instances for rows the
   edit left alone; and `world.undo` of a reload replays the journal as a full
   install that delivers the whole definition. *Check:* a one-colour save in
   the debug world reads one row in `world.boot.presentation-rows-delivered`
   per primed sink, and undoing it is a presentation install.
5. **Narrow validation**, for the delta and for `world.row.set` alike:
   sections first, then rows by digest. Built in part: validation is one table
   of partitions (`WorldDefinitionValidator.Partitions`), each declaring the
   parts it owns and reads, with reads recorded and enforced; a document
   mutation and a presentation reload run only the units their change reaches
   (`TryValidateChange`), a palette edit validates its one creation, and
   `world.boot.validations` counts units. Left: the `document` partition still
   owns most sections and reads the whole document, so a placement, kit or
   state declaration edit validates whole, and a full reload admits the whole
   document ([the disclosure plan's checkpoint](../disclosure.md#validation-partition-checkpoint)
   names the order). *Check:* a one-colour palette edit reads one unit in
   `world.boot.validations`, and a load reads every unit
   (`WorldDefinitionValidator.UnitsIn`).
6. **Content keys.** One store, products named by input digests and reached
   code, composed images and path keys deleted, the bound restated. Not
   started: a held compile is still keyed by the compiler's assembly build
   identities and the source's full path (`WorldCompileSession.CompilerIdentity`).
   The reached code comes from the walker the compiled world's chunks and the
   bake's stage keys already share (`CodeClosure`), never a third. *Check:* two
   checkouts at one commit share every entry (hits counted, no misses), and an
   edit to a renderer file misses no compile.
7. **Record the delta.** The tape holds a file edit's delta bytes. Not
   started: a recorded reload is still its kind, origin path and content hash,
   and a replay reads the file again. *Check:* `puck test --reproduce` over a
   scenario that edits a source mid-run passes with the working tree restored
   to its starting state.
8. **Save by derivation.** Carry derivations for literals, `let`s and
   template arguments; bound each save by the delta it produces; write
   against file digests; decompile shading programs as blocks; fix the debug
   world's refusal. Not started: `WorldDecompiler` still has no case for a
   shading program. *Check:* in the debug world, a palette colour changed by
   `world.row.set` on a template-written creation saves into the call that
   passed it, `puck format --check` still passes, `git diff` shows that one
   span, and the decompile round trip holds over the shade corpus.
9. **One built CLI.** The CLI in the build store, the stub as `puck`, the
   reinstall and lane-copy procedures deleted from the skills. *Check:* in a
   fresh worktree `puck --version` names that worktree's sources with no
   manual step, and a rebuild while the language server runs succeeds.
10. **Keep template expansions.** *Check:* a value tuned through a shared
    `let` and the same value as a literal reach the frame with the same
    counted lowering work.
11. **The editor on the World's session.** The language server over an
    attachment, fragments diagnosed in their world, the grammar generated.
    *Check:* an error introduced in `valley.puck` that only the composed
    world refuses appears on its line in the editor without a reload.

## What the owner settled

1. **A granted author may live-edit a published world (the owner's ruling).**
   The edit is a delta through the world's own door under its own grants,
   durable in its journal, and a release refuses to overwrite a live row it
   conflicts with, as the runtime plan already requires. The grant is the one
   the disclosure plan designs. Every zone being a world with its own authors is
   the point of the model.
2. **Saved files drive the running World first (the lead's call, which the
   owner delegated).** The file is the truth and Git sees it; type-through
   preview from an unsaved buffer is a later opt-in the author switches on.
3. **World Studio stays a browser sandbox and utility (the owner's ruling):**
   a visitor reads and tries a world's rules over official builds with nothing
   installed. It does not become the editor for a running World; an editor
   attached to a World's session, step 11, is its own tool and shares the one
   compiler rather than World Studio's preview engine.
4. **A World a local operator boots from a source tree watches its sources from
   boot (the lead's call):** `world.watch` starts on for that case and stays off
   for a published or remote authority.

## Verification requests

- **What a reload costs presentation.** A windowed debug World: a one-colour
  change in `day.puck`, then `world.reload`. Read its `world.iteration` line
  and `host.log category sdf.mesh-bake`. Expected with today's code: no group
  bakes, `builds=0`, and a relight of the whole window, since the materials
  key moves. It gives step 4 its first seconds for a saved look edit, and says
  how much of what remains is the lighting work in
  [the iteration plan](../iteration.md#order-of-work).
- **World Studio on the debug world.** Run the studio's island check on the
  debug world as it stands. The native compile peaks above 2 GB, which is
  near what a browser allows a 32-bit WebAssembly module (4 GB at most), and
  the browser engine's README still quotes figures from when the debug world
  composed to a few hundred kilobytes. It decides whether the studio needs
  the session's row-level memory before anything else there, and whoever
  runs it refreshes that README's table.
- **Steps 1 to 4 on a quiet machine.** Frozen builds from before step 1 and
  from the current tip, `puck compile worlds/debug/debug.puck --validate
  --measure` three times each with the cache warm, and in a headless World
  one `world.reload` of a one-colour edit to `day.puck`, the first after boot
  and a second. The counts already decided the steps; the timing says what the
  author gets from them, and the first-after-boot reload says what the
  persisted image's missing layers cost.

## Defects found on the way

The first and second are recorded in
[open items](../open-items.md#edit-to-running-hot-reload-and-boot); the rest
are removed by the step named or carried by a verification request.

- `puck decompile` writes a source the compiler refuses when the document
  holds a shading program. Step 8 fixes it.
- `WorldCompileSession.CompilerIdentity` uses assembly build identities, which
  the puck-world skill rules out as a derivation key. Step 6 fixes it.
- The browser engine's README measures a debug world two orders of magnitude
  smaller than today's. The World Studio verification request refreshes it.

## Systems considered

Before proposing the session, the delta and the save, the code that already
does each job was read. The searches were text searches over `src` for the
capability and the technique, not only the expected name.

| Capability | What exists | Verdict |
|---|---|---|
| Compile a source once and reuse it | `WorldCompileSession` (once the compile cache, the document composer and the source loader), `WorldSourceIndex`, the held images in `WorldDefinitionFileSource`, `CompiledWorldCache` | They are the session's parts. The session merges them and adds row-level state; it is not a new cache beside them |
| Know what a compile read | `CompileInputs`, `CompileInputLog` | Reused as the one read door, extended with a stamp-trusting digest |
| Trust a file by its stamp | `CompileInputs` (`Current`, `Digest`, `List`), which `WorldSourceWatch` polls through | The one home, used by every reader |
| Watch a dependency set | `DependencyWatch`, `WorldSourceWatch` | Reused. `puck compile --watch` was the duplicate and is deleted |
| Describe a document change by key | `WorldDocumentBasis.Diff` and `TryMerge`, `WorldReloadDelta`, `WorldProjectionDelta` | The overlay is reused as the one form |
| Apply a small change and rebuild only what it reaches | `WorldMutation`, `WorldDocument.InstallKindOf`, `WorldPrototype.DiffersInShadingAlone`, `WorldDocumentWalker.Share` | Reused as the door, which the iteration plan's live edit path extends |
| Validate only what a change reaches | `WorldDefinitionValidator.TryValidateTouchedStateRows`, `WorldDocumentReads` | The touched-row walk was the precedent; the document-read recording became the partitions' enforcement |
| Write an edit back into source text | `WorldSourceSave`, `WorldSourceEdits`, `WorldDocumentOrigins`, `SourceOrigin`, `PuckPrinter` | Reused. Step 8 adds derivations and the bounded proof |
| Identify the code behind a product | `CodeClosure`, `AssemblyReach`, `CodeRevision`, `SdfMeshBakeClosure` | `CodeClosure` is the one to use; `AssemblyReach` as a key is the defect |
| Keep one program per content | `ShadeReference.KeyOf`, `WorldShadeLowering` | The model for content keys and for kept expansions |
| Run a build keyed by its sources | `WorldArtifactKey`, the World build store, `CandidateCliInstall`, the launcher stub | The store and the stub are reused for the CLI |
| Serve the language to an editor | `PuckLanguageServer`, `BrowserLanguageServer`, `LspCommand` | One class already; it moves onto the session |
