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

The running engine is the visual editor. Puck has no separate scene editor or
content-creation tool: selecting, placing, sculpting and inspecting happen
inside a live World, through its console, views and HUD, and produce the same
document an author could write by hand. A missing editing affordance is a
feature of the World, not a reason for a second application.

Prefer existing data and operations when they express a new feature clearly.
For example, a body can be controlled through permissions without classifying
its geometry as a player or NPC. An anchor describes a camera's pose, while a
rig describes how it frames a view. These are examples of separating independent
choices; they are not a prohibition on adding a justified type or document field.

The engine provides general mechanisms; game-specific names and policies belong
in authored content. Authority and admission rules must stay explicit when a
new opcode, document shape or addon interface is introduced.

## A document owns the paths it names

A world document is self-contained and movable. Every relative path it authors
resolves beside the document that authored it: a basis, an import, a
neighbour reference, an asset row's source, an addon module, a pipeline or
graph source, a probe track, a machine's content, and the window icon. A
document and the files it names therefore move together, whether they are
copied into a build's output, staged under a state directory, or saved
somewhere new.

The engine's own content is different. The default world, fonts, shaders and
probe kinds ship beside the executable and resolve there. The two kinds of
lookup never share a resolver, and neither falls back to the other: a
document-relative path that is missing beside its document is refused, rather
than found by accident in the executable's directory.

A document read from somewhere other than a file, such as standard input, an
in-memory build or a wire delivery, has no directory. It can still name
absolute paths, but a relative one is refused by name at validation.

## Simulation and presentation have different contracts

Replay depends on a fixed code version, a world definition, and recorded inputs.
Simulation uses defined numeric semantics and reproducible random streams.
External observations must enter at an ordered, recorded boundary. Presentation
may interpolate and use floating-point arithmetic without changing authoritative
state. See the [architecture](../architecture/README.md) and
[Maths guide](../reference/maths.md) for implementation details.

A correction can intentionally change results across code versions. Update any
affected fixtures and replay expectations with evidence that the corrected
behavior remains reproducible. Do not preserve an incorrect result merely to
keep an old hash. Runtime versions that affect execution budgets, such as addon
fuel accounting, are part of the replay environment and need explicit control.

This register decides what presentation may do with a value. What of a world's
document presentation code may observe at all, and the transports that carry it,
is decided in
[the presentation view](runtime-and-delivery.md#the-presentation-view).

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
shortcut around resource ownership. Hardware ray tracing likewise does not
replace the primary SDF traversal merely because a device supports it.

The earlier SDF-only scope is a description of the established world-rendering
path, not a restriction on the programmable pipeline foundation. General mesh
import, shared mesh/SDF visibility, and broader material authoring remain
explicitly sequenced proposals in the
[rendering programme](../plans/rendering.md). That plan
must update affected contracts when those features become implemented.

The initial repeated-placement audio design also excluded a separate emitter
for every visual copy. A change to that choice needs a defined spatial-audio
and resource-budget contract rather than following the render instance count
implicitly.

## An actor is one type, and a grant names more than actors

Every surface that acts carries one identity type, `Principal` in
`Puck.Commands`: a console, a seat, an addon, a network peer, or the world's own
authored program. The command router stamps it, the submission wire and the
replay tape carry it, and a mutation admits against it, so no layer translates
one identity into another and no conversion can drift. Its default value names
no one: a dispatch that reaches a handler without an ingress door stamping it is
refused rather than treated as an anonymous caller. Only an ingress door mints a
principal; a handler reads the one its context carries.

A grant row can name more than an actor. A group holds rows its members reach,
and another world document holds rows the cross-document channel reads. Those
two never act, so they are not principals: a grant's holder is a separate type,
`Grantee` in the world schema, which is a principal, a group, or a document.
Because no acting surface accepts a grantee, a group or a document acting is a
type error rather than a run-time refusal.
## Configuration and operations remain discoverable

Durable configuration belongs in documents. Live operations belong in the
running application's command interface, available through the console and
process stdin/stdout. Launch flags select boot concerns such as the backend,
world and presentation mode; they do not replace a content authoring model.
Diagnostic environment variables are documented with development tooling.

The intended authoring experience supports work within one session. A missing
in-session operation is a planned integration task, not evidence that an
underlying library is absent. The [reference-game design](../game/design.md)
and the [play programme](../plans/play.md) own that experience.

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
