# Play expressions

A play expression is a way of playing that is defined by what the avatar's
hands and body do, and only afterwards by what it does to a number. The owner
has named three. Each is chosen for the engine systems it loads.

This page is the family's home. The first expression, the bard, is planned in
full in [instrument and dance play](instrument-play.md). The other two are
open explorations: this page records the owner's words and the questions, and
it holds the place where the lead writes the synthesis of the ideation round
that is running. It proposes no design for either.

## The owner's words

On the bard, the two requests quoted in full in
[its plan](instrument-play.md#what-the-owner-asked-for).

On the other two: "puzzle-healer, and spreadsheet-mage. These are not literal,
just the most abstract + direct way I could think to express the concept.
Solving puzzles or maths or riddles, who knows to do damage, tank, heal,
whatever. We don't have to decide the exact mechanics or which does damage or
heals, etc. We just have to come up with the avatar interactions for now and
work out the gameplay once the engine is capable of expressing such things."

And, on what the names mean: "we have to figure out what they even mean". The
owner's examples of where the answer might lie:

- "a much more refined and improved actiony version of the alchemy game";
- "what might work in a group setting";
- "maybe each class has their own instrument/toy/tool/totem";
- "maybe players mix and match, their class is a result of the current
  configuration".

The intent, in this page's words. The two names are handles for two ideas,
play by solving and play by calculating, and neither is a literal healer or a
literal spreadsheet. What each one is has not been decided. Which one harms,
protects or heals is not asked. The first thing wanted is the picture of the
avatar doing it, and the gameplay follows once the engine can express it.

## Where the family stands

| Expression | State | Where |
|---|---|---|
| The bard: a worn wind instrument and dance fighting, on the world's beat | Planned in full: the systems it rests on, its engine gaps, stages, lanes, research and concept-art brief | [Instrument and dance play](instrument-play.md) |
| Play by solving (the owner's handle: puzzle-healer) | An open exploration | [Below](#the-two-open-explorations) |
| Play by calculating (the owner's handle: spreadsheet-mage) | An open exploration | [Below](#the-two-open-explorations) |

The bard goes first because most of what it needs is built. Its first wave
writes designs for the rig and for sound that the other two will lean on,
whatever they become: a hand that reaches a chosen thing, a press that moves
the hand before the next tick, more shading values a body can drive, and a
rule's cue that reaches every client
([the engine gaps](instrument-play.md#the-engine-gaps)). Those designs are
written for hands in general and carry no instrument's vocabulary.

## The two open explorations

### The questions

These are the owner's questions and the ones they imply. The ideation round
answers them as proposals; the owner chooses.

1. What does play by solving look like on an avatar, in the world, during
   action? What does play by calculating look like?
2. Is the alchemy table the seed? The debug world has one
   ([alchemy](../../worlds/debug/alchemy.md)): a seated board game where two
   elements combine in a cauldron to discover others. What would a faster,
   bodily version of that be?
3. What works in a group? What does a second player add that one player
   cannot do alone?
4. Does each expression have its own object: an instrument, a toy, a tool or
   a totem? The bard's is a worn instrument.
5. Is a class a fixed thing, or the result of what a player has equipped and
   configured at the moment, so that players mix and match?
6. What does each stress in the engine that the bard does not?

### The ideation round

Three memos are being written, one each by Astra, Neon and Fable. The lead
synthesizes them into this page. Until then this section and the next hold no
design.

### The synthesis

*This is the place for the lead's synthesis of the three memos: what each
expression is, its avatar interactions, the systems it rests on with their
citations, the engine gaps it would expose, and what is deferred.*

## What a search of the engine found

These are findings for the round to check and use, not parts of a design.
Each was found by searching for a capability an exploration of solving or
calculating is likely to ask about. A row says what exists and one limit that
was read beside it. Nothing here was reproduced by running.

| A proposal might ask for | What exists | A limit read beside it |
|---|---|---|
| A hand that reaches a point in the world or another body | An effector whose target is a world point in a state cell, or another body's root with an offset (`CreationEffectorTargetDocument`) | The body target is a fixed population index in the document, so a hand cannot reach a body a rule chose a moment ago |
| Pieces that appear, move and vanish under rules | Rule effects that upsert and remove placements (`WorldEffect.UpsertPlacement`); placements attached to a body or swapped on state ([puck-world](../../.claude/skills/puck-world/SKILL.md#limits-worth-knowing)) | A body comes apart only into parts its creation already declares |
| Geometry cut again as a piece moves | A cutter that moves alone and only carves is an operand of the group it cuts, planned again when its snapped pose changes (`CreationMotion.OperandShapes`, `SdfMeshPathBake.Revisit`; [moving blends](moving-blends.md#gap-8-a-cutter-that-moves-against-the-surface-it-cuts)) | Each such move is a new bake of that group, and contact still reads the cut at rest |
| Boards, graphs, rays across cells | Compiled topologies and their queries (`CompiledTopology`, `BoardQueries`) | None read |
| A seeded puzzle that is known to be solvable | Generators with persisted draw cursors (`GeneratorEngine`), and search over journal scopes judged by rules (`ArenaSearch`) | Search's work is priced with the rules' |
| Taking a move back | Arena undo and rule groups (`ArenaUndoGroup`) | None read |
| One instance of a rule program for each of many players | Pools of typed records (`StatePool`; [records and pools](records-and-pools.md)) | Worst-case rule work is priced when the world loads (`RuleWorkBudget`, `RuleCapacity.MaxWorkUnitsPerTick`) |
| Liquids routed through channels | A flow lattice with reactions and a rule effect that paints its cells (`FieldLattice`, `WorldEffect.PaintField`) | The water plan's open gaps ([water](water.md)) |
| A line with a length between two bodies | A tether that caps distance and keeps swing (`FixedTetherConstraint`) | One length between two points; no rope with segments. Whether the tether is drawn was not read |
| A fact about what a surface blocks | Deterministic nearest-surface queries on the contact field (`FixedSurfaceQuery`) | The simulation never reads the renderer, so light and shadow as a rule's fact must be computed this way |
| Many small lights | Lights are authored rows the frame shades | A frame holds 128 (`SdfLights.MaxLights`) |
| Transparent pieces, and pieces that glow | A glass material with its own pass; emission by lane on an opaque material ([rendering skill](../../.claude/skills/rendering/SKILL.md#which-dispatch-owns-the-code)) | The skill's glass contract: exact for up to three front faces along a ray, and absent from the irradiance trace |
| Letters on a surface in the world | Embossed and engraved text runs in a creation (`TextRunDocument`) | The string is a literal that is baked, so text that changes is a new bake |
| Text that changes on a surface in the world | Nothing yet | It is the last step of [the UI overhaul](ui.md), behind its text, tree and interaction steps |
| A line spoken aloud | Text speech from a HUD text element (`TextSpeechSource`) | Placed at the listener, not at a speaker in the world |
| Typed text from a player | Text input events in the timed history (`WorldInputEventKind`) | No keyboard on screen for a pad ([input packet](open-items.md#input-channels)) |
| A drawn gesture | Input patterns over presses and axis crossings in time (`WorldInputPattern`) | No recognizer for a path in two dimensions |
| Knowing what the pointer is on | The frame's picker for presentation (`SdfWorldPicker`), the pointer ray carried in the intent for the authority (`PlayerIntent.SourceRay`), and the tabletop's pointer cell (`WorldTabletopFrame.TryPointerCell`) | A pointer selects one thing; selecting several is an editor package that is not built ([editor](editor.md)) |
| Marking a target | An effect that designates a body into a named register (`WorldEffect.Designate`) | None read |
| An effect on bodies in an area | Interaction rows over a bounded spatial query (`WorldInteraction`) | None read |
| Different players seeing different parts | Per-recipient views over row and cell visibility (`WorldRecipientView`) | The [disclosure plan](disclosure.md)'s write door is unfinished |
| A grid drawn from live state | Authored render-graph panes (`views.graphs`) | A state lattice bound into such a pass exists only in a quarantined world, and [the port plan](port-experimental.md) wires it into the debug world |
| A view of something else on a surface | Screens that show a machine, a camera, a session or the desktop ([engagement](../../.claude/skills/puck-world/references/engagement.md)) | None read |
| A formula | The expression language rules compile and run (`ExpressionProgram`, `RuleCompiler`) | A player's change to rules or interactions is refused by its kind before it compiles (`WorldRecipientMutations`, `WorldSectionFacts.IsProgram`) |
| A sandboxed program with a budget | Scripting guests charged by fuel (`WorldAddonRuntime`) | A second language beside the rule language |
| A saved composition of operations | Authored `commands` rows that run under the caller's own grants ([commands](../reference/commands.md#document-authored-commands)) | None read |
| Something a character carries from world to world | The owned identity document, which carries a look and a kit that each world may limit ([the game](../decisions/play.md#the-game)) | None read |

One question in these findings is the engine's and not an author's: whether a
player's own program may ever run on the authority. Today only the authority
authors a rule. The
[disclosure plan](disclosure.md#3a-a-declaration-is-judged-inside-a-scope-its-declarer-reads-whole)
designs a grant for declaring inside a scope the declarer reads whole, which
is the nearest existing design. The question is the owner's if a proposal
needs it, and no work waits on it now.
