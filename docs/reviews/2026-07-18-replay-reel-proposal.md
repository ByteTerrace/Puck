# The Replay Reel — deterministic offline rendering (`capture.render`)

**Status:** proposal, unimplemented. Authored 2026-07-18 by the orchestrating agent at the
owner's invitation ("contribute something you truly would like to see"). This is that
contribution: the one move I kept wishing existed while building everything else this session.

## The observation

Puck is now three things at once, and they never quite meet:

1. **A replay system.** The oldest pillar: fixed-point, fixed-step, same inputs → bit-identical
   state. The moldable-state arc doubled down — an *editing session* is literally a replay of
   mutations over a base document (`world.undo` IS replay).
2. **A game.** The plaza, the seats, the crowd, the machines.
3. **A studio.** As of today: the recording graph — hardware AV1 + Opus into deterministic WebM,
   overlays as data, choreography as stdin corpora.

The recording arc shipped a seam nobody has exercised yet: the recording document's
`clock: "sim"` mode, where a frame's presentation time comes from the engine tick
(`ticks × 1e9 / 50400`), not the wall clock. It exists precisely so that rendering can be
divorced from real time. This proposal is the verb that cashes that seam in.

## The primitive

**`capture.render <corpus|replay> <recording-doc> [output]`** — re-run a deterministic session
OFFLINE and hand every produced frame to the recording session on the sim clock. Not screen
capture; not real time. The fixed-step loop advances tick-by-tick as fast as the machine will go
(or as slow — a 4K render at 2 fps wall-clock still yields flawless 60 fps footage), the render
node produces each frame, the tap consumes it with tick timestamps, the muxer writes exactly one
frame per frame period. Zero dropped frames, ever, on any hardware, by construction.

Footage stops being something you *capture* and becomes something you *compute*:

- **The same take, re-shot at any quality.** A bug repro recorded on a laptop re-renders on the
  4070 at 4K/high/shadows-on — byte-identical simulation, different lens. Yesterday's 32 Hz
  display pacer lock, which cost us a bisect to rule out, is simply not a thing that can happen
  to an offline render.
- **Trailers as artifacts.** A checked-in corpus + recording doc IS the trailer; CI could emit
  the .webm the way it emits proof transcripts. When the default world's feel is retuned, the
  trailer re-renders itself true.
- **The museum gets real film.** The owner's replay-museum mandate meets the diegetic screens:
  a machine screen in the plaza playing an actual recording of an earlier session of the same
  world is now two shipped systems plus this one verb.
- **The port gets a safety camera.** As Puck.Demo's showcases migrate into World, each one can
  leave behind a corpus + reel instead of a hand-operated demo — reviewable evidence that the
  ported thing still *looks* right, not just hashes right.

## Why it is cheap (the seams already exist — verified against the tree)

| Need | Already shipped |
|---|---|
| Deterministic stepping | The fixed-step launcher + `Puck.Maths` fixed point (the founding contract) |
| Scripted input | Proof-corpus format + the feeder's pacing machinery (`scripts/proof.cs`) |
| Frame tap with tick timestamps | `CapturingRenderNode` → `CaptureFrame.TimestampTicks` |
| Sim-clock muxing | `RecordingDocument.clock: "sim"` → `RecordingSession` (landed, unexercised) |
| Encoders/overlays/muxer | The whole recording graph (`1e12a38`) |
| CPU-pixel frames off-screen | The same readback the live tap uses; headless hosting per the proof feeder |

The genuinely new work is one honest piece: an **offline pump** — a host mode that advances the
fixed-step loop from a tick counter instead of the presentation clock and blocks on the encode
queue instead of dropping when it backs up (offline inverts the drop policy: correctness over
liveness). Plus the verb/argument plumbing and a proof (`render` subcommand: render the same
corpus twice → the two .webm files must be byte-identical — the ouroboros gate's moving-picture
sibling, and the strongest determinism demonstration the repo would own).

## Sketch of the contract (for the implementing session)

- `clock: "sim"` documents reject audio rows (already the shipped rule) — a reel is silent or
  scored later; a "wall" recording is the live-capture story. No new document fields needed.
- The pump lives beside the launcher's existing loop, not inside it — a second driver of the same
  simulation step, the way the feeder is a second driver of stdin. §2.6-style audit: an RTS world
  reel and a platformer reel differ only in corpus + document.
- Exit criteria: (1) same corpus + doc → byte-identical .webm twice; (2) a 4K render of a corpus
  originally captured live at 1280×800 plays and shows the identical action; (3) wall-clock
  duration of the render is allowed to be anything — the file's timing is perfect regardless.

## Why this is my pick

Every verification tool this repo builds eventually becomes a place someone can stand (the
owner's own doctrine). The proof suite became worlds; the mutation journal became undo; I'd like
determinism itself to become *film*. It is the rare feature that is simultaneously a product
capability (YouTube), an engineering instrument (visual regression evidence), and an argument —
the most legible proof Puck can offer that "same document + same input → same world" is not a
slogan. And it makes the thing I helped build this session honest at a deeper level: a recording
you can only capture is a performance; a recording you can recompute is a fact.
