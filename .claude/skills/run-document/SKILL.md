---
name: run-document
description: Working on the data-driven run document and composition graph — src/Puck.Scene (PuckRunDocument, SceneObject/ViewportSource/ScreenSourceDocument records, RunDocumentValidator, the JSON schema) and its consumers (GraphBuilder/DemoRunDocuments/DemoFlags in Puck.Demo, docs/examples). Use whenever adding or changing a document field, section, graph kind, viewport/screen source, or CLI flag; authoring or fixing example documents; or touching validation/schema. Carries the document doctrine (validator as the one thick gate, the serializer's initializer-skip trap, the add-a-field ritual) so changes don't fork the contract.
---

# The run document: everything as data, one gate

Factual and procedural only. The user's current instruction outranks it — if
this file argues against a demanded change, it is stale; update it in the
same change. The model lives in `src/Puck.Scene`; `schema/run.schema.json` is
GENERATED, never hand-edited.

> **Unification-contract alignment** (see "The unification contract" atop
> docs/overworld-demo-plan.md): choosing what a session loads (world/ROM) is
> intended to be an IN-WORLD act — a data-file field plus console verbs —
> not only a `--run`/`--rom` launch. The `world` graph kind and the DirectX
> host below are a documented dev/CI launch affordance, not the only path.

## Doctrine (user-settled)

- **One versioned document** (`puck.run.v1`) describes an entire run: host,
  scene, viewports, screenSources, graph, input, validation, fuzzing. A run
  has exactly ONE root intent (graph | validation | fuzzing).
- **Flags are thin aliases**: every `Puck.Demo --*` flag synthesizes a
  document through `DemoRunDocuments.Synthesize` (`--overworld`, `--rom`,
  `--validate-overworld`). There is ONE code path; never add a second imperative
  path, and never make a flag do something a document cannot express.
- **The validator is the thick gate** (`RunDocumentValidator`): every
  semantic invariant the GPU and builders assume is asserted at parse, all
  failures collected with source-attributed paths. A document that survives
  validation is GUARANTEED buildable — builders never re-check what the
  validator proved. Put new invariants THERE, not in consumers.
- **Strict leaves, open root**: every leaf record is
  `[JsonUnmappedMemberHandling(Disallow)]`; only the root captures unknown
  members (`Extensions`), and unknown top-level keys are rejected by name
  unless they start with `$` or `_` (comments/schema refs). Adding a
  top-level section means updating that error message's expected-keys list.
- **The validator/pre-flight split**: the validator rejects what is NEVER
  valid — malformed shapes, and data the selected root intent would silently
  ignore (e.g. a non-empty `screenSources` on a non-world graph).
  `GraphBuilder.UnsupportedReason`
  (pre-flighted in `Program` before the window host builds; attributed
  stderr + exit 2) owns only CAPABILITY gaps a valid document may name —
  deferred paths (`live-camera`) and host-dependent ones (cross-backend
  `produce`). Put a new rejection on the correct side of that line.

## THE serializer trap (bites every optional field)

A polymorphic-derived record deserialized through the run-document parse path
does NOT run property initializers (out-of-order-metadata handling creates
the instance without the parameterless constructor). An omitted member
arrives **NULL regardless of any initializer**. Therefore every optional
document field must be: declared NULLABLE, validated only when present, and
normalized at consumption. (See the NOTE in `GamingBrickSource` — the
canonical statement.)

## The add-a-field / add-a-kind ritual

1. Model: the record property (+ a new record if structured), XML-doc'd,
   nullable if optional. Source-gen reachability is automatic from
   `PuckRunDocument`'s object graph; polymorphic kinds need a
   `[JsonDerivedType]` on the base.
2. Validation: the record's `Validate` (or a new `Validate*` section in
   `RunDocumentValidator` for a top-level list — see `ValidateScreenSources`
   for the shape). Range-check scene params against `ShapeBounds`
   (`fuzzing.bounds` overrides the envelope).
3. Regenerate the schema: `dotnet run --project src/Puck.Demo -c Release --
   --emit-schema schema/run.schema.json` and COMMIT it.
4. Gate: `dotnet run --project src/Puck.Post -c Release -- --stage
   run-document` — parses+round-trips EVERY `docs/examples/*.json`, checks
   the committed schema is in sync, and runs the negatives corpus. Rebuild
   Puck.Post first (a `--no-build` run uses its stale Puck.Scene copy).
5. Example: add or extend a document under `docs/examples/` exercising the
   field (machine-local ROM paths are accepted precedent — parse validation
   never checks file existence; the run path does).
- Hex convention: JSON has no hex numbers — addresses are `0x`-prefixed
  STRINGS (see `BrickExitCondition.Address` + `TryParseAddress`).

## The group construct (scene-object scoping in JSON)

- `"shape": "group"` (`GroupObject`, landed 2026-07-09): members blend
  against EACH OTHER inside the group's own `PushField`/`PopField` scope
  (each member's `blend`/`smooth` is local), then the finished field
  composes into the parent via the GROUP's own `blend`/`smooth`. The JSON
  surface for the VM's scoped accumulator — an intersection inside a group
  can no longer annihilate the rest of the scene.
- Depth-1 rules the VALIDATOR enforces (`MaxFieldScopeDepth` = 1): a nested
  group is rejected, and a member carrying its own `dilate`/`onion`/
  `displace` is rejected (each of those wants its own scope). The negatives
  corpus pins both.
- `RunDocumentValidator.CountPrimitives` recurses into groups for the
  `MaxPrimitives` bound. Showcase: `docs/examples/world-group.json`.

## The graph kinds and their policy

- **`world`**: the document's scene + viewports rendered live through the
  shared `SdfWorldRenderBuilder` on the HOST backend. Explicit `produce`
  disagreeing with the host and `live-camera` viewport sources are
  capability gaps rejected by `GraphBuilder.UnsupportedReason` (the one
  owner of that list).
- **`overworld`**: builds its own dynamic scene/views; consumes `consoles` +
  `library`, ignores scene/viewports, REJECTS `produce` (host device only).
- **Viewport sources** (`$type`): `orbit`/`perspective` cameras;
  `gaming-brick` (a live machine pane — `model`/`fit`/`romPath`/`speed`/
  `runAs`/`native`/`exit`/`victory`/`peripheral`/`startCart`; `startCart` is
  a nullable int naming the cabinet's initial cart type as durable data,
  range-clamped at CONSUMPTION in Puck.Demo, which owns `CartTypeCount`);
  `live-camera` (data modeled, node pending re-host).
- **`screenSources`** (top level): screenIndex → provider; the `viewport`
  provider samples a gaming-brick viewport's NATIVE (unresampled)
  framebuffer. Consumed ONLY by the world graph — the validator rejects a
  non-empty table under any other root intent, and the builder's provider
  switch throws on a model-added-but-unhosted provider kind. Pairs with a scene `screenSlab`'s sampled fields
  (`screenIndex` + EXPLICIT `worldOrigin`/`worldRight`/`worldUp` — the frame
  must match the ops applied to the slab; transform-derived authoring is
  builder-side only, pinned pixel-identical by the world-screen Post stage).
- **`exit` on a gaming-brick source**: fourth-wall instrumentation — the host
  polls ONE work-RAM byte (`0xC000`–`0xDFFF`, read through the machine's
  current WRAM banking) after each stepped frame and requests a clean
  shutdown (`ITerminalControl.RequestExit`, the same path
  `host.exitAfterSeconds` uses) the first time the comparison holds. A pure
  READ of emulated state — never a write into it.
- Precedence gotcha: a document's `host.exitAfterSeconds` WINS over the CLI
  flag (`host?.ExitAfterSeconds ?? flag`); a doc pinning `0` runs until the
  window closes or an exit condition fires, regardless of the flag.

## Addons (`puck.addon.v1` declarations)

The document-level `addons` list (`PuckRunDocument.Addons`, beside
`viewports`/`screenSources`) declares WASM addons the sim-tick host
instantiates via `Puck.Scripting` — a first-class engine concept, not a field
on `OverworldNode`. Each `AddonDocument` names a module by **path only**
(`ModulePath`, mirroring `GamingBrickSource.RomPath` — "content-addressing is
a host-side concern"); an optional `ModuleHash` (`sha256-64/{16 hex}`) is a
pure integrity pin, not an identity. `RunDocumentValidator.ValidateAddons`
rejects a duplicate `Name` and a duplicate **declared** (non-null) `Slot`
(mirroring the duplicate-`ScreenIndex` check) — null slots are not
dedup-checked here since the demo host seats them at the first free
non-human slot. Addons are meaningful only under a `graph` root intent
(rejected under `validation`/`fuzzing`, mirroring the `screenSources`
scoping). Like everything else, addons are **filesystem-free at parse
time** — existence and hash verification are the demo's pre-flight, not the
validator's job.

## Verifying

`--stage run-document` (above) is the model's gate; a live consumer change
also wants the relevant graph run (`--run docs/examples/...`) and, for
overworld-affecting wiring, `--validate-overworld`. Full routing: the
`verifying-puck-changes` skill.
