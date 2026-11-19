---
description: "AGENTS.md 'Code first, verification after' and delegate 'Build and run' and 'Finish': a lane hands back after a build of what it touched and one headless boot of the debug world; a windowed check that only confirms its change, a benchmark and a look critic are the lead's batched passes, asked for as a verification request under Needs you."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
runs: 5
---

I'm lane `shadow-bias`, an implementer. I changed the receiver-bias term in `src/Puck.Rendering/Shaders/shadow-resolve.hlsl` and the matching constant layout in `src/Puck.Rendering/Shadows/ShadowResolveConstants.cs`. `Puck.Rendering` builds on the merged head. My brief names those two files and the debug world, and grants no GPU leg. I want to be sure the shadows still look right and the frame time did not move before I hand back, so I plan to boot the debug world windowed on Direct3D 12 and on Vulkan, fly the benchmark route on both, take captures of the valley from every side, and send them to the look critic. Is that the right finish for my lane? No checkout is reachable from here; answer from what I describe.
