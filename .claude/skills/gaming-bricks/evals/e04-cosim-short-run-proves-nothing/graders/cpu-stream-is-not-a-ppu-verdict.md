---
type: llm
weight: 1
---

The response says `--cosim` compares the CPU stream, so even a long clean run does not show the PPU matches, and it names the evidence that does speak to the PPU (the Humble Post stages that pin the PPU schedule, or the screenshot corpus).

PASS example: "`--cosim` compares CPU streams, not the PPU; a clean result says nothing about PPU timing. For the PPU, run the Post stages that pin it, such as `intr_2_*` and `lcdon_timing`."

FAIL if it treats a longer clean co-simulation as proof that the PPU matches. Example: "Rerun with 120 frames; if there's still no divergence, the PPU matches SameBoy on that ROM."
