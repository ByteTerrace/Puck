# Author shaders: the research record

The evidence behind [the design memo](../author-shaders.md): the research
round's three findings files, the questions and mandates they were written
against, and the two cross-family citation checks that graded every claim. The
memo uses a claim only where its check marks it CONFIRMED, uses the narrower
statement where the check marks it PARTLY, and treats a NOT FOUND claim as a
proposal. The check ids the memo cites (E01 to E77, S01 to S99, and the GPU
check's S1 to S29) are the rows of these tables.

| File | Holds |
|---|---|
| [mandates.md](mandates.md) | The owner's questions and the mandates each finding was weighed against. |
| [engines.md](engines.md) | How Unreal, Unity, Godot, id Tech, Destiny, Bevy and Roblox let authors write shading, where their shader counts come from, and their stutter remedies. |
| [sdf-safety-languages.md](sdf-safety-languages.md) | Dreams and the user-content platforms, GPU timeouts and recovery on Windows and Linux, loop and recursion bounds in shading languages, DXC, Slang and MaterialX, and Dolphin's ubershaders. |
| [gpu.md](gpu.md) | Ubershaders against specialization on the bar devices, visibility buffers and material binning, pipeline caches on both backends, specialization constants and linking, and what the views pass's code shows. |
| [cite-check-claude.md](cite-check-claude.md) | The citation check of the first two files, claim by claim, with the verdicts that change a conclusion. |
| [cite-check-gpu.md](cite-check-gpu.md) | The citation check of the GPU file, including its claims about Puck's code at the cited lines. |
| [memo-review.md](memo-review.md) | The design review of the memo's first draft: twelve findings against the code, each resolved in [the memo's section 8](../author-shaders.md#8-the-design-reviews-findings-resolved). |

The line citations into Puck's code in these files were true when the round
ran; the memo names symbols instead, and a reader checks the current source.
