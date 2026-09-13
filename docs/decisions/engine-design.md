# Engine design decisions

Puck's design centers on validated documents, an authoritative simulation, and
separate presentation. These choices guide new work; the
[overview](../overview.md) introduces the resulting engine, and
[plans](../plans/README.md) describe proposed extensions.

## Documents describe content

The authored document is the primary content artifact. Authoring tools and
console operations produce the same document model. Content
changes pass through composition and whole-candidate validation before becoming
active. Accepted mutations can be recorded in a journal and replayed through
the normal apply path. Saving and recovery must preserve those semantics.

Prefer existing data and operations when they express a new feature clearly.
For example, a body can be controlled through permissions without classifying
its geometry as a player or NPC. An anchor describes a camera's pose, while a
rig describes how it frames a view. These are examples of separating independent
choices; they are not a prohibition on adding a justified type or document field.

The engine provides general mechanisms; game-specific names and policies belong
in authored content. Authority and admission rules must stay explicit when a
new opcode, document shape or addon interface is introduced.

## Simulation and presentation have different contracts

Replay depends on a fixed code version, a world definition, and recorded inputs.
Simulation uses defined numeric semantics and reproducible random streams.
External observations must enter at an ordered, recorded boundary. Presentation
may interpolate and use floating-point arithmetic without changing authoritative
state. See the [architecture](../architecture/README.md) and
[Maths guide](../../src/Puck.Maths/README.md) for implementation details.

A correction can intentionally change results across code versions. Update any
affected fixtures and replay expectations with evidence that the corrected
behavior remains reproducible. Do not preserve an incorrect result merely to
keep an old hash. Runtime versions that affect execution budgets, such as addon
fuel accounting, are part of the replay environment and need explicit control.

Rendering comparisons use suitable image tolerances and content checks;
pixel-for-pixel equality is not the default requirement across GPU backends.
Passing a comparison does not establish correctness without an independently
meaningful expected result. Audio mixes in fixed point for reproducible results; that mixing contract is
separate from presentation devices and their availability.

## Producers compose through explicit contracts

A screen consumes an image supplied by a producer. The renderer need not know
whether it came from an emulator, a camera, another view, or a shader pipeline.
A surface that participates in shared visibility needs additional contracts;
an image alone does not imply geometry or depth.

Vulkan and Direct3D 12 implement neutral GPU interfaces. Select the backend
when building the render host; a document must not silently combine live GPU
resources from different APIs. Cross-backend composition is not an admitted
shortcut around resource ownership. Hardware ray-query experiments likewise
do not replace the primary SDF traversal merely because a device supports them.

The earlier SDF-only scope is a description of the established world-rendering
path, not a restriction on the programmable pipeline foundation. General mesh
import, shared mesh/SDF visibility, and broader material authoring remain
explicitly sequenced proposals in the
[rendering evolution plan](../plans/shader-pipeline-evolution.md). That plan
must update affected contracts when those features become implemented.

The initial repeated-placement audio design also excluded a separate emitter
for every visual copy. A change to that choice needs a defined spatial-audio
and resource-budget contract rather than following the render instance count
implicitly.

## Configuration and operations remain discoverable

Durable configuration belongs in documents. Live operations belong in the
running application's command interface, available through the console and
process stdin/stdout. Launch flags select boot concerns such as the backend,
world and presentation mode; they do not replace a content authoring model.
Diagnostic environment variables are documented with development tooling.

The intended authoring experience supports work within one session. A missing
in-session operation is a planned integration task, not evidence that an
underlying library is absent. The [reference-game design](../game/design.md)
and [game development plan](../plans/game-development.md) own that experience.

## Verification must match the claim

Validate physical limits and report useful failures rather than silently
truncating authored content. A measured capacity, an advisory performance
estimate, and a regression threshold serve different purposes and must be
identified as such. Device failures and unsupported features should have
explicit recovery or refusal behavior.

Use tests for the contracts they actually exercise, and run World for behavior
that depends on its composition and command processing. The quarantined
experimental engine Post harness is not an active verification gate. Current
emulator batteries and other live suites have their own scope; see
[development](../development/README.md).

Keep current limitations in the owning guide. Dated test results belong with
their candidate and environment, rather than in a hand-maintained table that
implies permanent certification of every feature. Readers should still be able
to learn what the engine does without inspecting source code first.

## Internal contracts can be corrected

Puck's internal names and document shapes can be changed with their callers,
examples, and verification. Do not accumulate migration aliases solely to retain
an accidental internal design. Package and release promises, when made, need
their own explicit compatibility policy rather than being inferred from an
internal schema's version label.
