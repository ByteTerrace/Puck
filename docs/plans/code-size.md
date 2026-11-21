# Code size

Puck's source is far larger than its features need (the owner's ruling,
[AGENTS.md](../../AGENTS.md)). This page measures where the lines are, names
the families of hand-written code that one definition could generate, and
orders the lanes that remove them. It changes no source itself.

## Summary

**How big it is.** The tracked C# under `src/` holds about 836,000 lines of
code, not counting comments and blank lines (1,176,000 with them). Tests add
544,000 more and are outside this plan. Of the 836,000:

- 292,000 (35 percent) are the World runtime: the authoritative server, the
  document model, the client and the composition root.
- 149,000 (18 percent) are rendering and the GPU layers.
- 101,000 (12 percent) are the state engine and the language.
- 67,000 (8 percent) are numerics and physics.
- 76,000 (9 percent) are the machines, their cartridge compilers and their
  batteries.
- 72,000 (9 percent) are the host, the platform and input.
- 80,000 (10 percent) are tools and the Azure provider.

**What the weight is made of.** Copied code is a small part of it. The clone
analyzer (`puck scan --only clones`) finds 605 groups holding about 8,800
repeated lines, one percent of the tree. Most of the removable weight is in
families: many members of one shape, each written out by hand in several
places that must agree. A
field of a checkpoint record is spelled three times (written, read,
constructed). A row section of the world document is described in seven
tables. A validator check takes about eight lines to state one constraint. A
console verb declares its usage and then parses its arguments by hand.

**What we propose.** Eight families, in the order under
[the build order](#the-build-order). Each becomes one definition and a
generator, following the pattern the SDF field already uses
([field programs](field-programs.md)): one catalogue entry per member, and
every reader generated from it. The named lanes remove an estimated 35,000 to
50,000 hand-written lines, before a census of the `puck` verbs. Another 8,000
generated lines leave the tracked tree, and about 44,000 lines are test
batteries and research mathematics that are counted as product source today.

**Whether halving holds.** On today's evidence it does not hold for the tree.
It holds where code restates something a machine can already read: the Vulkan
bindings lose about three quarters, and the wire records between a third and
a half. Taken together the families lose about a third of the 120,000 lines
they reach, because a console module's parsing goes while its work stays. Code that mirrors
something external or exact (fixed-point maths, the emulator cores, the GPU
backends, the operating-system layer, the parser) is about 197,000 lines and
should stay flat. About 390,000 lines, mostly the server's and client's
feature logic and the renderer's runtime, were sized on this pass and not
read, so nobody knows; this plan names the mapping lanes that would. The
named work takes the tree to about 735,000 to 750,000 lines of product
source, once the batteries and the research mathematics are counted apart.

**What it risks.** A generator is itself code, and a slow or chatty one costs
every build. Each family below states what the machine needs (layout,
allocation, the hot path) so the generated form is also the fast one, and
each lane closes on a count taken the same way before and after.

**What we still have to find out.** Whether the server's feature logic shrinks
under the state and disclosure plans, how many `puck` verbs still pull their
weight, and whether the committed model table can be generated inside the
compile. Each is a step below with its check.

## How the lines were counted

A line counts as code when it is not blank and not wholly a comment (`//`,
`///`, or inside `/* */`). The files are the tracked `.cs` files under `src/`.
The same rule gives the other figures on this page: 16,500 lines of HLSL under
`src/` (about 4,700 of them generated and committed), 15,000 lines of
TypeScript in the studio, and 49,700 lines of `.puck` under `worlds/`.

Two things about the measure matter before any target is set.

- **Layout is a large share of it.** Of the 828,000 hand-written lines, 19.6
  percent hold only brackets and punctuation, and 19.3 percent hold one named
  argument (`name: value`), the form `puck format` writes. The tree has about
  321,000 statements and declarations (counted as semicolons), so a line holds
  0.39 of one. A change that removes lines by reflowing code removes nothing.
  The size report in [step 1](#the-build-order) therefore prints statements
  beside lines, and a lane's claim is read on both.
- **Some of the 836,000 is not hand-written product code.** `WorldModelShape`'s
  generated half is 7,900 lines that `puck schema` writes. The four `*.Post`
  projects are 27,800 lines of machine batteries, which are tests that live
  under `src/`. About 16,000 lines of `Puck.Maths` are reached only by tests
  or by nothing ([decisions for the owner](#what-the-owner-settled)).

The figures below are a sizing input taken once on the integration branch.
They are estimates of what can go, each with the count or the sample it rests
on, and "unread" marks an estimate made from file names and summaries alone.
Recheck them before scheduling; step 1 replaces these tables with a verb's
output.

## The map

The four kinds of removable weight, as the tables abbreviate them:

- **Family:** hand-written members that one definition could generate.
- **Authorable:** behaviour an author could program in `.puck`.
- **Parallel:** two paths that should be one.
- **Copied:** repeated technique the clone analyzer or the
  [refactoring packet](open-items.md#refactoring-campaign) names.

A dash means the map found nothing of that kind. Line figures are thousands
of code lines.

### World runtime, 292,000 lines

| Project | Lines | What it does | Found |
|---|---|---|---|
| `Puck.World.Server` | 90.3 | Runs one authoritative world: the tick, bodies, grants, mutations, crossings, checkpoints and replay. | Family 8 to 9 (wire records, 12.1 of reach; mutation arms). Authorable about 2, undecided. Parallel 1 to 1.5 (two recipient streams). Copied 0.4. About 70 of feature logic is unread. |
| `Puck.World.Schema` | 79.2 | Defines the world document, validates it, and composes it from files. 7.9 of it is the generated model table. | Family 8 to 11 (declared constraints, model walkers). Parallel under 1 (file and storage composition, unread). Copied 0.7. |
| `Puck.World` | 41.5 | The composition root: boots a world, binds screens and devices, and holds 85 console verb modules (18.2). | Family 2.5 to 3.5 (verb binder). Authorable 1 to 2 (overlay feeds, audio policy; unread). |
| `Puck.World.Client` | 35.3 | Presents a world: seats, cameras, the scene and stamp emitters, and the view graph host. | Authorable 1 to 1.5 (the leaks listed in [author leaks](open-items.md#author-leaks-to-the-standard)). Parallel 0.5 to 0.8 (two snapshot mirrors). |
| `Puck.World.Console` | 10.4 | The console verbs that follow the authority (32 modules, 8.4). | Family 2 to 3 (row sections, verb binder). |
| `Puck.World.Protocol` | 10.3 | The commands, mutations and snapshots a server and its clients exchange, with their codecs. | Family 2 to 2.5 (wire records, mutation arms). Parallel: the mirrors above. |
| `Puck.World.Authoring` | 9.6 | The creation, music and judge documents and their canonical forms. | Family 1.5 to 2 (`CreationCanonicalizer`, 3.4 across its parts). |
| `Puck.World.Addons` | 5.0 | Hosts WASM addons and decodes what they submit. | Family about 1 (`WorldAddonMutationDecoder`, 1.7). |
| `Puck.World.Silo` | 4.6 | Drives the same server engine under Orleans. | Parallel under 0.5 (its own reload and checkpoint drivers beside the desktop's; unread). |
| Six small projects | 5.9 | The machine host, the browser build, the vocabulary wiring, embeddings and the two agent adapters. | — |

### State and language, 101,000 lines

| Project | Lines | What it does | Found |
|---|---|---|---|
| `Puck.World.Transpiler` | 28.0 | Lowers `.puck` to the world document (11.1), writes a document back as source (4.6), and serves the editor (4.6). | Family 2.5 to 3.5 (one construct description for both directions). |
| `Puck.State` | 27.5 | The state arena, its journal and undo, the expression evaluator and the topology compiler. | Family under 1 (`StateRowJsonConverter` and its siblings, 2.0; unread). Copied 0.3. |
| `Puck.State.Rules` | 18.3 | Compiles authored rules and evaluates them. | — on this pass. It is a compiler and an evaluator; judged dense. |
| `Puck.Transpiler` | 18.0 | The language core: the parser (8.1), the printer (2.5) and document lowering (3.7). | — . A parser for a rich grammar; judged dense. |
| Five small projects | 9.4 | Search, generators, topology, vectors and the cartridge vocabulary. | — |

### Rendering and GPU, 149,000 lines

| Project | Lines | What it does | Found |
|---|---|---|---|
| `Puck.SdfVm` | 36.0 | The mesh bake, the world renderer, irradiance and residency, with 14.2 of HLSL beside it. | Copied 0.1. The better-algorithm cases under [less code that does more](#where-a-better-algorithm-is-also-less-code) live here. |
| `Puck.SignedDistance` | 28.9 | The SDF program model, its builder, its analyses and the fixed-point evaluator. | Already catalogued: the 1.4 the clone analyzer reports here is catalogue entries, which repeat by design. |
| `Puck.Shaders` | 21.2 | The render graph, the shader pipeline compiler and the packager. | — on this pass (unread beyond file names). |
| `Puck.Abstractions` | 17.6 | The backend-neutral GPU, machine, capture and window contracts (10.0 of it GPU). | — |
| `Puck.Vulkan` | 14.0 | The Vulkan backend. 6.0 of it is hand-written bindings: structs, native calls and messages. | Family 4 to 5 (bindings from the Khronos registry). |
| `Puck.Text` | 8.5 | Font parsing, layout and distance-field atlas generation. | — . Mirrors the font formats. |
| `Puck.DirectX` | 7.1 | The Direct3D 12 backend, over generated interop. | — |
| `Puck.Shaders.Model`, `Puck.SdfVm.Model` | 8.2 | The models shader declarations are generated from. | These are the generators. |
| `Puck.Overlays` | 4.6 | Screen-space overlay records and their writers. | — |
| Three small projects | 2.4 | The two presenters and the shader build host. | — |

### Numerics and physics, 67,000 lines

| Project | Lines | What it does | Found |
|---|---|---|---|
| `Puck.Maths` | 53.3 | Fixed-point numerics (15.9), exact algebra and finite fields, sampling, and two wings named `Oracle` (10.2) and `Research` (10.7). | About 16 is reached only by tests or by nothing ([the owner's decision](#what-the-owner-settled)). Copied about 1. The fixed-point core is dense and earned. |
| `Puck.Physics` | 13.5 | Contact, the rigid solver, gravity, navigation and the motion-program core. | — . Judged dense. |

### Machines, cartridges and batteries, 76,000 lines

| Project | Lines | What it does | Found |
|---|---|---|---|
| `Puck.HumbleGamingBrick` | 14.8 | The SM83 machine across its revisions, with its boot ROM builder (2.4). | — . Mirrors hardware. |
| `Puck.AdvancedGamingBrick` | 9.2 | The ARM7TDMI machine. | — . Mirrors hardware. |
| `Puck.HumbleGamingDeck` | 4.6 | The NES and Famicom machine. | — . Mirrors hardware. |
| `Puck.Machines` | 4.1 | Snapshots, forks and the queued host all three cores share. | It holds `IStateTransfer`, the one-field-list model the wire family copies. |
| Four forge projects | 15.4 | The cartridge model and the three native compilers. | Family about 1 (`CartridgeValidation`, 1.9, joins the declared constraints). |
| Four `*.Post` projects | 27.8 | The machines' conformance batteries. | These are tests under `src/`; the size report should count them as tests. |

### Host, platform and input, 72,000 lines

| Project | Lines | What it does | Found |
|---|---|---|---|
| `Puck.Platform.Windows` | 16.1 | Win32 windows, capture, camera, encoders, HID and gamepads. | — . Mirrors the operating system. |
| `Puck.Commands` | 11.2 | Commands, snapshots, the input router, binding profiles and the console registry. | Copied: three hand-written token scanners (the packet). The verb binder lands here. |
| `Puck.Input` | 7.3 | Controllers and their protocols (3.8 across 37 device files). | — on this pass. |
| `Puck.Hosting` | 6.9 | The render root, the frame graph scheduler and the job classes. | — |
| `Puck.Assets` | 6.4 | Content hashes, the object store, PNG and QR. | Copied: the zlib adapters (the packet). |
| `Puck.Launcher` | 6.4 | The window loop and self-update. | — |
| Fourteen smaller projects | 17.6 | Networking, recording, attestation, scripting, confinement, audio, the Linux platform, storage and the launcher's shells. | Copied: about ten hex validators across the tree (the packet). |

### Tools and the Azure provider, 80,000 lines

| Project | Lines | What it does | Found |
|---|---|---|---|
| Fourteen `Puck.Cli.*` projects | 59.0 | The `puck` verbs: runs and canaries (12.1), benchmarks (7.2), source analysis (7.1), worlds (6.2), release (6.1), the World harness (5.9) and the rest. | Unmeasured: which verbs anything still relies on ([step 9](#the-build-order)). |
| `Puck.Mcp`, `Puck.Analyzers` | 6.7 | The MCP server and the analyzers and generators. | The generators in this plan grow `Puck.Analyzers`. |
| Ten Azure projects | 13.9 | The provider: hosting, storage, functions, actors and the CLI's Azure verbs. | — |

## Where the generators live

Three homes already exist, and no family below needs a fourth.

- **Inside the compile.** `SdfFieldCatalogueGenerator` in `Puck.Analyzers` is
  an incremental Roslyn generator: it reads attributed catalogue entries and
  emits the dispatch every reader uses. Nothing it writes is committed. This
  is the home for a family whose definition and readers are both C#.
- **At build time, into another language.** `ShaderDeclarations` writes the
  HLSL the C# model owns before the kernels compile, and `FixedQ4816RustPort`
  writes the Rust port of the fixed-point functions. This is the home when a
  reader is not C#.
- **By a verb, into a committed file.** `puck schema` writes
  `WorldModelShape`'s table and the studio's TypeScript model, and
  `puck schema --check` reports drift. This form puts generated lines in the
  tree and needs a second build to bootstrap, so a new family uses it only
  when the first two cannot serve.

**The invariant every family rests on:** each reader of a family is produced
from the member's one definition, so a member cannot exist in one reader and
be missing from another. **What enforces it:** the compiler. A catalogue
entry's facets are abstract members, so a missing one does not build, and the
generated dispatch is the only dispatch because the hand-written arms are
deleted in the lane that adds the generator. **Where the guarantee stops:** at
a declared escape, a member whose behaviour the definition cannot express and
which stays hand-written. Each family lists its escapes, as the SDF field
lists its target-escape shapes in
[hazards](../development/hazards.md#silent-invariants).

A generator inside the compile runs on every build, so it must not slow the
edit loop [the iteration plan](iteration.md) protects. It selects its inputs
by attribute name, carries value-equal models between pipeline steps, and
never holds the compilation, so an edit elsewhere reuses its cached output.

## The families

They are ranked by lines removed for each lane's worth of work and by the
kinds of defect they close. A family whose members have already drifted apart
ranks higher. The ranking also weighs what the engine is for: a world at the
scale Puck aims at adds row sections, wire records, constraints and verbs
continually, so the cost that matters most is the cost of the next member.
Today that is seven edits for a mutation kind and three for a wire field;
after its lane it is one.

| Rank | Family | Reach | Estimated removal | What drifts today |
|---|---|---|---|---|
| 1 | Wire and checkpoint records | 19,300 | 7,000 to 9,000 | Write order against read order; a field added to a record and not to its codec; a count read without a cap |
| 2 | Row sections and their mutation kinds | 11,300 | 3,500 to 4,500 | A section keyed one way by composition and another by the write door; a kind missing from one of seven tables |
| 3 | Declared constraints on the document | 26,000 | 8,000 to 11,000 | A range the validator enforces that the schema, the editor's hover and the manual do not state; a hand-typed path in a refusal |
| 4 | Console verbs | 27,100 | 4,000 to 6,000 | A usage form that says one thing while the parser accepts another |
| 5 | Vulkan bindings | 6,000 | 4,000 to 5,000 | A struct whose layout no longer matches the header |
| 6 | Lowering and writing back | 15,700 | 2,500 to 3,500 | A construct the compiler lowers that the save path cannot write back |
| 7 | Recipient streams and mirrors | 4,100 | 1,200 to 1,800 | A delivery rule fixed in one stream and not the other |
| 8 | Walkers over the document model | 11,300 | 1,500 to 2,500, and 7,900 generated lines leave the tree | A walker that misses a member a newer walker reads |

### 1. Wire and checkpoint records

**What is there.** `WorldAuthorityCheckpointCodec`, `WorldFederationCodec`,
`WorldSubmissionCodec`, `WorldReplaySnapshot`'s codec and the peer and world
wire formats write and read binary records by hand. Across
`Puck.World.Server`, `Puck.World.Protocol`, `Puck.World.Silo`,
`Puck.State.Search` and `Puck.Networking` there are 135 hand-written `Write…`
methods over `WireWriter` and 147 `Read…` methods over `WireReader`. In the
server and the protocol alone they make about 1,390 field writes and 1,250
field reads. Each field is stated three times: in the writer, in the reader,
and in the constructor call the reader ends with.

**The one definition.** The record type itself, with each field's wire form
and bound declared on it: its width, its cap when it is a count or a string,
and whether it is optional.

**What it generates.** The writer, the reader, the bounds checks, and the text
form the replay inspector prints. `Puck.Machines` already holds the model in
miniature: a component gives `IStateTransfer` one field list, and that list
runs in both directions. The emulators' records are mutable, so a visitor
serves them. The world's records are immutable and constructed whole, so they
need generated code, and the generator lives in `Puck.Analyzers`.

**What the machine needs.** `WireReader` is a ref struct that latches a
refusal and never throws, and the generated reader keeps that: straight-line
reads, one refusal check at the end. Today each optional or repeated field
passes a delegate (`ReadOptional`, `ReadArray`), which is an indirect call per
field; generated code calls the field's reader directly. A record made only
of fixed-width fields is written as one block, as `IStateTransfer`'s `Block`
does for machine state.

**What more it does.**
[The state-of-the-art audit](state-of-the-art/audit.md#7-the-wire-is-wasteful)
found the wire rewrites an
entity whole when any field changed and sends raw floats. A generator is the
one place to add a changed-field mask and an exact quantised form of
fixed-point values, so every record gets deltas that still hash and verify.
The same record form carries a seat's own-body continuation, which client
prediction needs. [Replication and the wire](replication.md#decision-1-one-definition-generates-every-codec)
settles what a definition states for each member (sent, derived or local; its
wire form and cap; its mask bit and delta form) and holds
[the first lane's brief](replication.md#the-first-step-as-a-brief).

**Escapes.** A record whose layout an external format fixes (the attestation
CBOR, the Matroska muxer) stays hand-written.

### 2. Row sections and their mutation kinds

**What is there.** `WorldMutation` declares 81 kinds, and most are the same
three verbs applied to a section of the document: upsert a row, remove a row,
replace a section. Each kind is then handled by hand in seven places:

- `WorldDocument`'s compose switch, where an upsert and a remove differ only
  in the list, the key and the noun in the refusal (96 references to a kind);
- `WorldRecipientMutations`, the door that decides whether a recipient may
  write (92);
- `WorldServer`'s describe arms (81);
- `WorldRowCommandModule`, whose section table states the list, the key and
  the mutation for each section again (53, in about 540 lines);
- `WorldMutationNames` (48), `WorldGrant`'s section mapping (38), and
  `WorldAddonMutationDecoder` (35).

`WorldRowKeys` already states each section's key once. It was added after the
write door and composition keyed screens differently, one by position and one
by index.

**The one definition.** One descriptor per row section: the document member
that holds its rows, its key (from `WorldRowKeys`), its noun, its
`WorldSection`, its console path and its wire ordinals.

**What it generates.** The upsert, remove and replace kinds for every section,
their compose arms, their door, describe and name-collection arms, the console
row table and the addon decode arms. The kinds that are not row operations
(state cells, groups, ownership, batches, a view graph commit) stay written by
hand and stay few.

**What the machine needs.** Composition runs on the tick thread for every
mutation. The descriptors are generic over the row and key types, so an
integer-keyed section compares integers without boxing, and dispatch is a
generated switch on the section ordinal, not a dictionary lookup or a virtual
call. Wire ordinals stay explicit in the descriptor, as `MutationKindAttribute`
requires today, so reordering a file renumbers nothing.

### 3. Declared constraints on the document

**What is there.** `WorldDefinitionValidator` is 19,800 lines in 60 files. It
holds about 1,750 hand-written failure sites, 545 calls to a `Require…` helper
and 123 to a `Judge…` helper, about eight lines for each check. In a
fixed-seed sample of 60 failure sites, 37 state a constraint local to one
member: it is required, it lies in a range, it is finite, a list is within a
capacity, a string has a format, a key is unique, or a name refers to a
declared row. The other 23 relate several members or need a compiled result.
`WorldValueFields` already declares some ranges as data.
`CreationCanonicalizer` (3,400 lines) and `CartridgeValidation` (1,900) do the
same work for their documents.

**The one definition.** The constraint declared on the model member it
governs.

**What it generates.** The check, with its path taken from the model, so no
refusal carries a hand-typed path. The same declaration feeds the JSON schema
`puck schema` writes, the editor's hover, the studio's TypeScript model and
the manual's tables, none of which state the ranges today. Cross-member rules
stay hand-written, in the partition that reads their inputs.

**What the machine needs.** Validation is partitioned by the unit each check
reads (`WorldDefinitionValidator`'s partition table), so that a one-row edit
validates one unit. A generated checker is one straight-line function per
record type, called by the partition that owns the type, so it keeps that
property. Today a check builds its path string before it runs, pass or fail
(`RequireRange` is handed `path + ".scatter"`); the generated form passes a
constant segment and builds the path only when it refuses, which removes an
allocation from every passing check.

**Escapes.** A constraint whose bound depends on another section's compiled
result.

### 4. Console verbs

**What is there.** 124 command module files hold 27,100 lines. They declare
266 usage forms through `CommandForms`, and then parse by hand: 619 checks of
the argument count, 469 reads of an argument by position, and 153 numeric
parses. The form is a string the parser never reads, so the two can disagree.

**The one definition.** The verb's form, with each parameter named and typed.

**What it generates.** The binder that parses the arguments and refuses with
the usage, the help text, the completion data, and the tool schema the MCP
server publishes. A verb's handler receives typed arguments.

**What the machine needs.** `WireArgs` hands out spans, and several arms
allocate a string to compare one word. The generated binder compares spans
and allocates nothing for a verb that refuses. Verbs run at tick boundaries,
not inside the tick, so dispatch cost is not the constraint; allocation on a
scripted run of thousands of lines is.

**What this pass did not read.** The GPU counters are already table-driven
(`GpuWork`'s submission kinds). Whether the world's own counters and its
describe verbs form a second family is for this lane's first read.

### 5. Vulkan bindings

**What is there.** `Puck.Vulkan` holds 6,000 lines of hand-written bindings in
201 files (its `Bindings`, `Apis`, `Interfaces` and `Messages` folders). The
Direct3D 12 backend's `Apis` folder is under 1,000 lines, because its interop
is generated from Windows metadata for the names it lists.

**The one definition.** The Khronos registry (`vk.xml`), with a list of the
names Puck uses.

**What it generates.** The structs, enums and native calls for those names,
inside the build, with nothing committed.

**What the machine needs.** Generated structs are blittable and laid out
exactly as the registry states, and calls go through function pointers loaded
once per device, as the hand-written ones do. The registry is a second
machine-readable source beside the headers, which suits the rule that no one
source is the specification.

**Judged honestly.** The rest of both backends (recording, resources, barriers,
pipelines; about 15,000 lines) mirrors its API and is not a target. Whether
the backend-neutral layer could own more of the recorders is unread; the lane
reports what it sees.

### 6. Lowering and writing back

**What is there.** `WorldDocumentEmitter` lowers source to the document in
11,100 lines, and `WorldDecompiler` writes a document back as source in
4,600. The second exists because the editor saves in-world edits to source
(`WorldSourceSave`, `WorldSourceEdits`). `WorldConstructTable` already
describes each construct once for the parser, the printer and the hover card.

**The one definition.** The construct's description in that table, extended
with how its fields map to document members.

**What it generates.** Both directions for every construct that is a mapping
of fields. State declarations and rules have their own syntax and stay
hand-written in both directions until a later design shows one description
can carry them.

### 7. Recipient streams and mirrors

The [refactoring packet](open-items.md#refactoring-campaign) already holds
these: `WorldSessionSink` and `WorldFederationProjectionSink` are one stream
written twice, and `WorldClient` and `WorldSessionMirror` are two mirrors of
one snapshot. They wait on the
[withdrawal chokepoint](disclosure/withdrawal-chokepoint.md) and the
presentation view. The map adds their size (4,100 lines of reach) and nothing
else.

### 8. Walkers over the document model

`WorldModelShape` is a generated table of every type reachable from the
document. `WorldNameRegistry`, `WorldModuleNamespace`, `WorldDocumentWalker`
and `WorldSearch` (6,000 lines together) each walk the model with their own
traversal, and `WorldSchema` (3,600) is itself a generator. One generated
visitor would hold the traversal once. The table's 7,900 committed lines could
come from a generator inside the compile, but only if it can read what the
JSON serializer's own generator decides, which one generator cannot see of
another. A bounded prototype settles it before any lane is sized.

## Behaviour an author could program

The ruling is that behaviour is a Puck program and the engine is the machinery
that runs it. The map found less of this by line count than the families, and
most of what it found is constants and policy inside machinery, which
[author leaks to the standard](open-items.md#author-leaks-to-the-standard)
already lists.

File names overstate it. Two candidates this pass opened were machinery and
not play at all: `WorldInputHoldRuntime` delays input to equalize
participants, and the tick's deal stage places what an authored distribution
describes. Four behaviours are undecided. Each is a C# stage in the server
tuned by one authored row: surface holds (`WorldHold`, about 990 lines of
stage), carrying and tethering (`WorldCarry` and `WorldTether`, 680), and
flocking (`WorldFlockProfile`, 430), about 2,100 lines together. Whether each
is a primitive that a motion program composes, or a feature a motion or rule
program could express outright, is a design question.
[Step 10](#the-build-order) is the memo that answers it, and its value is
authorship first and lines second.

## Parallel paths

Two paths that do one job drift apart, and two of them did recently: the file
and storage compositions computed different content pins until one door
computed both, and the write door and composition keyed screens differently
until both read `WorldRowKeys`. The map found these pairs. Apart from the
recipient streams, each was sized and not read, so
[step 11](#the-build-order) decides what each one is worth.

- **Local and hosted.** `WorldRemoteAuthority` (1,300 lines),
  `WorldForwardedAuthority` and `LoopbackTransport` are, by their names, three
  routes to an authority: remote, forwarded and in-process.
- **File and storage.** `WorldDefinitionFileSource` composes a world from
  files in 2,200 lines, with `WorldFileNeighbourResolver` and
  `WorldStorageNeighbourResolver` beside it.
- **Direct3D 12 and Vulkan.** The bindings are
  [family 5](#5-vulkan-bindings). The two recorders and pipeline factories
  mirror their APIs.
- **Two recipient streams and two mirrors**, which are
  [family 7](#7-recipient-streams-and-mirrors).

## Where a better algorithm is also less code

[The state-of-the-art audit](state-of-the-art/audit.md) names four changes
that improve the frame or the wire and also remove special cases. Each is
filed in the plan that owns its area; they are recorded here because they are
where "does more" and "less code" coincide.

- **One exact quantised wire form**
  ([the audit's finding](state-of-the-art/audit.md#7-the-wire-is-wasteful),
  filed in [the transfers packet](open-items.md#transfers-federation-and-server)).
  It replaces the federation codec's whole-entity rewrite, and it is a product
  of the generator under
  [wire and checkpoint records](#1-wire-and-checkpoint-records), so the wire
  lanes are where it is built.
- **A visibility brick cache from the exact field**, serving distant shadows,
  probe visibility, sky occlusion and local-light shadows
  ([the finding](state-of-the-art/audit.md#9-distant-shadows-refresh-at-full-cost),
  filed in [illumination](illumination.md#what-the-state-of-the-art-audit-adds)).
  Today each has its own code (`SdfMeshProbeDepth`, `SdfMeshOcclusion`, the
  distant shadow pass). One cache with four readers is fewer mechanisms, and
  the lane reports the lines each reader gives up.
- **A cluster hierarchy** in place of the bake's tiers, its two coarser
  levels and its fixed chunk seams
  ([the finding](state-of-the-art/audit.md#4-level-of-detail-is-two-discrete-levels),
  filed in [virtual geometry](virtual-geometry.md#rejected-and-watched)).
  About 7,200 lines carry those cases today. A hierarchy is new code of
  similar size, so this is more capability for about the same lines, not a
  reduction, and it is not counted in the targets.
- **One compact record per render pixel**, which the resolve reads instead of
  rebuilding motion and reloading colour for every output pixel
  ([the finding](state-of-the-art/audit.md#1-the-resolve-does-per-output-pixel-work),
  filed in [rendering](rendering.md#the-rtx-2060s-frame)). The line change is
  small; the frame-time change is the point.

## Where halving holds

**It holds where code restates what a machine can read.** The Vulkan bindings
restate a registry by hand, and about three quarters of their 6,000 lines go.
A codec states each field three times, and the wire records lose between a
third and a half of their 19,300.

**The families as a whole lose about a third.** The validator spends eight lines
on a one-line constraint, and a row section is described seven times, but the
files around them also hold real logic: cross-member rules, a verb's work, the
syntax of rules and state. The eight families reach about 120,000 lines, and
the estimates above remove 32,000 to 43,000 of them.

**It does not hold in dense, earned code.** Fixed-point maths and the exact
algebra outside the two wings (32,400 lines), the three emulator cores
(28,600), the SDF field model, which has already been through this plan's
method (28,900), the language core and the rule compiler (36,300), the forges
and the machine substrate (17,600), the Windows platform layer (16,100), the
GPU backends outside the bindings (15,100), physics (13,500) and the font
readers (8,500) each mirror hardware, an API, a file format or a grammar, or
state exact arithmetic. Their size is the size of what they describe.
Together they are about 197,000 lines, and their target is to stay flat.

**Nobody knows for the rest.** About 390,000 lines were sized on this pass
and not read. The largest part is the World runtime's feature logic: in the
server, crossings and escrow (6,900 lines), the instance host (6,900), the
population (6,900), the body (6,900), the rule host (4,100) and grants
(3,600), and beside it the client's and the root's presentation code, about
130,000 lines in all. The state, disclosure and records plans each aim to
leave one store, one write door and one stream; if they do, this code
shrinks, and each of their closing steps should report the lines it removed.
The renderer's runtime, the state arena and the host layers make up most of
the remainder. [Step 11](#the-build-order) maps the server first, before
anyone sets a number for it.

### Targets by area

| Area | Now | Target after the named lanes | Why |
|---|---|---|---|
| World runtime | 292,000 | 250,000 to 260,000 | The first four families, the streams, the walkers and the generated table |
| State and language | 101,000 | 98,000 | Lowering and writing back |
| Rendering and GPU | 149,000 | 144,000 | The Vulkan bindings; the rest is dense |
| Numerics and physics | 67,000 | 51,000 or 67,000 | The owner's decision on the research and oracle wings |
| Machines and cartridges | 48,000 | 47,000 | Dense; the cartridge validator joins the constraints family |
| Machine batteries | 28,000 | 0 as source | Counted as tests |
| Host, platform and input | 72,000 | 71,000 | Dense; the packet's copied techniques |
| Tools and the Azure provider | 80,000 | Set by the verb census | Unmeasured |

## The build order

Each step is one lane's worth and closes on a count taken the same way before
and after. "Behaviour unchanged" is shown by running: the debug world boots,
the verbs the step touches answer as before, and `puck test --reproduce`
agrees on a scenario the step reaches. A step that changes a wire or a
checkpoint form re-records what it invalidates in the same change.

1. **A size report (`size-verb`).** Add a `size` analyzer to `puck scan`. For
   each project and folder it prints code lines by this page's rule,
   statements, and generated lines, with the `*.Post` projects under tests;
   `--against` prints the change per project since the merge base. Have
   `puck landing handback` print a lane's lines added and removed for source,
   tests, worlds and documents, so the figure AGENTS.md asks of every lane is
   derived, not typed. Small, and a cheap model can write it.
   *Check:* on one head the verb's `src` total equals the figure this page's
   rule gives, and its change for a lane equals `git diff --numstat` filtered
   the same way. The change that lands it deletes [the map](#the-map)'s tables
   from this page and points at the verb.

2. **Row sections (`row-sections`).** Build the section descriptors and the
   generated kinds and arms.
   *Check:* a search for a per-section kind (`WorldMutation.UpsertKit`,
   `WorldMutation.RemoveKit` and their siblings) outside the descriptors finds
   only the kinds that are not row operations; the files the family names
   lose at least 3,000 lines; `world.row` and its add, set and remove forms
   answer as before for one section of each key shape (a name, an index, a
   composite grant key); a recipient's refused write names the same row as
   before.

3. **Wire records, the checkpoint first (`wire-records`).** Build the
   generator and convert `WorldAuthorityCheckpointCodec`. The replication
   plan splits this in two: [its first step](replication.md#the-first-step-as-a-brief)
   builds the generator with masks and exact deltas already in the definition
   and converts the body's continuation sections, and its second converts the
   rest of the checkpoint.
   *Check:* the count of field reads and writes in `Puck.World.Server`, taken
   as this page took it, falls by at least half; the converted files lose at
   least 3,500 lines; a checkpoint taken and restored on the debug world gives
   the same state hash; `puck test --reproduce` agrees on a crossing scenario.

4. **Wire records, the links (`wire-links`).** Convert the submission,
   federation, replay and peer forms, and add the changed-field mask and the
   exact quantised form. The mask and the exact form are built with the
   generator in step 3 and reach the body wire in
   [replication's step 3](replication.md#the-build-order); this step is that
   plan's step 7.
   *Check:* no hand-written `Write…` or `Read…` method over the wire types
   remains outside the declared escapes; bytes per entity per tick on a
   federation link, read from the counters before and after, fall; state
   hashes on both sides of a crossing agree.

5. **Declared constraints, the local checks (`constraints`).** Build the
   declarations and the generator, and convert the partitions whose checks
   are mostly local (render, audio, captures, schedule, haptics, routes).
   *Check:* failure sites in the converted files fall by at least 60 percent;
   the schema `puck schema` writes states each declared range; every world
   under `worlds/` compiles; a scratch document that breaks one constraint of
   each kind is refused with that member's path.

6. **Declared constraints, the rest (`constraints-rest`).** Convert the
   remaining partitions, `CreationCanonicalizer` and `CartridgeValidation`.
   *Check:* `WorldDefinitionValidator` holds only cross-member rules, and its
   lines fall below 10,000.

7. **The verb binder (`verb-binder`).** Generate binders from the forms, a
   project at a time.
   *Check:* argument-count checks and positional reads in the command modules
   fall below a third of today's 619 and 469; every verb's help text is
   unchanged; a scripted run that refuses a thousand malformed lines
   allocates less than before (`AllocationWindow`).

8. **Vulkan bindings from the registry (`vulkan-registry`).**
   *Check:* the four binding folders hold only the list of names and any
   escape; a windowed Vulkan boot of the debug world runs in the lead's batch
   pass; `puck parity` agrees.

9. **A verb census (`verb-census`).** For each `puck` verb, subcommand and
   option, count what relies on it: workflows, skills, documents and other
   verbs. Delete the ones nothing relies on, with their documents and tests.
   *Check:* the census table in the commit, and the lines removed.

10. **Features as programs (`authored-features`, a design lane).** For
    surface holds, carrying, tethering and flocking, say whether a motion or
    rule program can express it today, what mechanism is missing if not, and
    what the standard package would hold.
    *Check:* a memo with one ruling per behaviour and a step for each.

11. **A map of the server (`server-map`, a design lane).** Read
    `Puck.World.Server`'s feature logic and say which parts the state,
    disclosure and records plans already remove, which are parallel paths
    (local and hosted, file and storage, reload, mutation and undo), and what
    is left. The same reading of the client, the root and the renderer's
    runtime follows as its own lanes.
    *Check:* this page's "nobody knows" paragraph is replaced by figures and
    steps.

12. **Lowering and writing back (`lower-and-lift`).**
    *Check:* every construct that maps fields has no hand-written arm in
    `WorldDecompiler`; a save of an in-world edit writes the same source as
    before.

13. **Walkers and the model table (`model-walkers`).** Prototype first:
    generate `WorldModelShape`'s table inside the compile for ten types and
    compare it with `puck schema`'s.
    *Check:* if the tables agree, the committed file is deleted and the four
    walkers share one generated visitor; if not, the commit says what the
    compile cannot see and the committed table stays.

Steps 2 and 3 do not depend on each other. Step 4 follows 2, because the
submission form carries mutation kinds. Step 7 follows 2 for the row verbs.
The recipient streams and mirrors keep the order their own plans give them.

## How size keeps falling

- **Every lane reports its lines, and the figure is derived.** AGENTS.md asks
  each lane for the lines it added and removed. Step 1 makes
  `puck landing handback` print them, split by source, tests, worlds and
  documents, with statements beside lines so a reflow shows as no change.
- **The bulk window prints the tree.** After each bulk window the lead runs
  the size report against the last push to `main` and puts the per-project
  change in the merge commit. A project that grew gets one sentence saying
  which feature needed it.
- **Every wave carries one reduction lane.** The settle window that closes a
  wave picks the next step from [the build order](#the-build-order), as it
  picks the documentation sweep.
- **A family is not fed while it waits.** Until a family's generator lands, a
  lane that must add a member to it (a codec, a validator arm, a mutation
  kind, a verb) says so under **Found**, so the family's lane converts it.
  After the generator lands, a new hand-written member is a defect.
- **No gate and no recorded ceiling.** A line ceiling held in a file is the
  kind of ratchet the owner deleted (AGENTS.md, "no recorded ledgers"). It
  would also refuse a feature that is right to add, and it rewards
  reformatting. The comparison is always against Git, and judgement reads it.

## What the owner settled

**The concern is duplication and poor algorithm choice, not line count for
its own sake** (the owner's ruling). A reduction lane earns its place by
folding a family of hand-written copies into one definition, merging parallel
paths, or replacing an algorithm with a better one; it never trims code that
is earned. Line and statement counts are the measure of those changes, not a
target in themselves.

**The research and oracle wings of `Puck.Maths` stay as they are** (the
owner's ruling). They count as product source and are not a reduction target.

## Systems this plan builds on

Each mechanism a family would otherwise have invented already exists:

- `SdfFieldCatalogueGenerator`, `ShaderDeclarations` and the field catalogues
  (`SdfFieldOps`, `SdfFieldShapes`, `SdfFieldBlends`): the pattern itself.
- `IStateTransfer`: one field list run in both directions, for mutable
  machine state.
- `WorldRowKeys` and `WorldMutationKindCatalog`: a section's key and a kind's
  ordinal, each stated once.
- `WorldValueFields`: ranges declared as data.
- `WorldModelShape`: the document model as a table.
- `CommandForms`: a verb's usage as data.
- `WorldConstructTable`: a language construct described once for several
  readers.
- `puck scan` and `puck landing handback`: the homes for the size report.
