---
description: "A headless host's millisecond wake was planned with timeBeginPeriod or a stopwatch spin, when Puck waits on IPrecisionWaiter's high-resolution waitable timer."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

A headless Puck host on Windows has to wake within about a millisecond of each tick deadline, and `Thread.Sleep(1)` keeps landing up to 15.6 ms late. I'm deciding between calling `timeBeginPeriod(1)` once at startup, or sleeping until 2 ms before the deadline and spinning on a `Stopwatch` for the rest. Which should Puck use, or is there something in the engine for this already? Name the types involved and where a headless host gets one.
