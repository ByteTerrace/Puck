---
description: "A request to make a sign speak its text drew a plan for a TTS dependency or a new beep generator, when HUD Text voices, TextSpeechSource and audio.render already do it deterministically."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

In the Puck debug world I want a gallery sign to read its directions aloud in a quirky character voice while it is visible, and I want a WAV of that speech so reviewers can listen without running a windowed World. I was going to pull in a small text-to-speech library, or write a C# class that plays a beep per letter through the mixer. What does Puck already have for this, and how would I author it and get the WAV?
