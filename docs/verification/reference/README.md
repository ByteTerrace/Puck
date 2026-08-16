# Reference verification material — not wired to a runner

Scripts here are real stdin scripts against a real console grammar, kept as
reference because turning them into an automated `puck canary` manifest needs
work beyond relocating the file. `docs/verification/canaries/<name>/canary.json`
is mandatory for every directory under `canaries/` (`CanaryManifestLoader`
refuses an orphan directory), so a script without a verified manifest belongs
here instead, never dropped loose under `canaries/`.

## `seamless-four-corners.script.txt`

A single-process circuit around all four Four Corners ground worlds
(`nw` → `ne` → `se` → `sw` → back toward `nw`), each hop driven purely by
`instance:`-addressed console commands against the SAME boot process — no
`--connect`/`authorityWorld` needed, the same shape
`docs/verification/canaries/seamless-adjacency` already proves for one hop.
Run it by hand against `quilt-nw.world.json` to check the whole-circuit claim
live. Promoting it to a canary needs a `canary.json` (positive leg reusing
this script) plus a genuinely discriminating counter-script and assertions
verified against a live run — owed work, not done here.
