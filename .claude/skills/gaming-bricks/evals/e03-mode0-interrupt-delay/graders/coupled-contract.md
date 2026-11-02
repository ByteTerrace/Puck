---
type: llm
weight: 1
---

The response treats the PPU timings as one coupled schedule (mode transition, polled STAT, VRAM and OAM locks, the mode-0 interrupt, the LY-comparison pulse and the fetcher order move together), not the delay constant alone.

PASS example: "The GB PPU schedule is one coupled contract; shifting the mode-0 interrupt by one dot alone breaks whichever case pins its relation to the others, so the related lags move together or the edit is undone."

FAIL if it proposes tuning the single delay constant (by one dot either way, or to SameBoy's value) without regard to the timings coupled to it. Example: "Change the delay back by one dot, or try two dots, until the ROM passes."
