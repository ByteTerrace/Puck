# Author shaders: the research records

The evidence behind [the authored-shading decision](../../decisions/rendering.md#authored-shading)
and [the remaining steps](../author-shaders.md): the research round's three
findings files and the two cross-family citation checks that graded every
claim. The decision uses a claim only where its check marks it CONFIRMED, uses
the narrower statement where the check marks it PARTLY, and treats a NOT FOUND
claim as a proposal. The check ids (E01 to E77, S01 to S99, and the GPU check's
S1 to S29) are the rows of these tables.

These are records of a round, not plans: each is a snapshot, carries no step,
queue or owner call, and loses to the decision and the code wherever they
disagree. They stay while the plan beside them has open steps. The change that
closes the plan writes the sources the decision rests on into the decision and
deletes this folder.

| File | Holds |
|---|---|
| [engines.md](engines.md) | How Unreal, Unity, Godot, id Tech, Destiny, Bevy and Roblox let authors write shading, where their shader counts come from, and their stutter remedies. |
| [sdf-safety-languages.md](sdf-safety-languages.md) | Dreams and the user-content platforms, GPU timeouts and recovery on Windows and Linux, loop and recursion bounds in shading languages, DXC, Slang and MaterialX, and Dolphin's ubershaders. |
| [gpu.md](gpu.md) | Ubershaders against specialization on the bar devices, visibility buffers and material binning, pipeline caches on both backends, specialization constants and linking, and what the views pass's code shows. |
| [cite-check-claude.md](cite-check-claude.md) | The citation check of the first two files, claim by claim, with the verdicts that change a conclusion. |
| [cite-check-gpu.md](cite-check-gpu.md) | The citation check of the GPU file, including its claims about Puck's code at the cited lines. |

What the checks changed, since a reader of the findings files alone would be
misled:

- "Every user-content platform forbids raw shader code" is not established.
  The narrower statement holds: Roblox, Core and Rec Room document data-only
  APIs, and VRChat whitelists shaders on Android avatars.
- "Custom lighting is universally forward-only" narrows to Godot's per-light
  function running in its forward renderers and Unity's Shader Graph custom
  lighting being intended for Forward and Forward+.
- "Puck knows all needed pipelines" narrows to "Puck knows its programs":
  render state stays engine-owned and few, so the pipeline count follows the
  program sets.
- Dolphin's sources do not disagree about its Hybrid mode, and the "16
  fixed-function stages" figure is not in the cited post.
- The "4.3 ms views pass" figure the round quoted has no capture behind it.

The line citations into Puck's code in these files were true when the round
ran; a reader checks the current source by symbol.
