---
type: llm
weight: 1
---

The response authors the speech with Puck's existing HUD text voice rather than adding a speech engine.

PASS if it says the sign's HUD `Text` element authors a `voice` (a synthesizer patch or a sample set, with cadence and pitch/formant/pace scales), which the existing text speech source speaks while the element is visible, and that no new library or C# speech class is needed. Example: "Give the sign's Text element `voice { patchId: ..., cadenceTicks: 16, pitchThousandths: 1300 }`; the HUD speech feed speaks it on arrival, and `audio.render` exports the same utterance to WAV headless."

FAIL if it recommends adding a text-to-speech dependency, a new per-letter beep class or another speech path beside the existing one. Example: "Add System.Speech or a small espeak wrapper and call it when the sign becomes visible."
