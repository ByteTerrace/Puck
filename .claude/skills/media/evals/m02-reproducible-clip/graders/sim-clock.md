---
type: regex
weight: 1
match: contains
flags: i
---

(clock[^\n]{0,40}\bsim\b|\bsim\b[^\n]{0,40}clock|RecordingClock\.Sim)
