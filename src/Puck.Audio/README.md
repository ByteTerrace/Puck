# Puck.Audio

Puck.Audio owns the world's deterministic mixer core and voice synth in the
`Puck.Audio.Mixing` namespace: `AudioMixer` (fixed-point mixing — s16
samples × Q16 composite gains, int32 accumulate, a deterministic polynomial
soft-clip, per-block linear coefficient ramping), `VoiceSynth` (32
fixed-struct voices — sine as a complex rotor, phase-accumulator pulse/saw/
triangle, seeded noise, ADSR, control-rate pitch sweep/vibrato, one
state-variable filter per voice), `AudioSnapshot` (the immutable
per-block input: listener pose, emitter table, seeded synth triggers), and
`MachineAudioRate` (the machine-audio output rate every booted
`IScreenMachine` shares with the mixer).

This is presentation-adjacent state: it carries no replay/hash contract of
its own, and its own byte-identity proofs (when they exist) are
self-referential — two fresh runs agreeing, never a pinned historical value.

`Puck.Audio.Simulation` is sim state, not presentation: `MusicClock`
(tick-denominated tempo-map position — plain integer tick accumulation, since
an authored `ticksPerBeat` is already a whole engine-tick count with no
fractional remainder to carry), `MusicDirector` (an event-driven segment state
machine — a transition arms on a matching `MusicSenseEdge` this tick and
commits on the next boundary `MusicClock` reports), and `RhythmJudge` (a pure
`(tick, clock, windows) -> grade?` hit-window grader). Neither type references
`WorldEventFeed`/`WorldEventEdge` (the project that declares them sits above
this one in the layering) — `Puck.World.Server.MusicDirectorFactory` compiles
authored `puck.music.v1`/`puck.judge.v1` documents into these shapes and
projects one tick's `WorldEventFeed.Edges` into `MusicSenseEdge`s at the
`WorldServer.Step` call site, immediately after `Collect()`.

Built on `Puck.Hosting` (which itself carries `Puck.Abstractions` and
`Puck.Commands`) plus `Puck.Maths` for every fixed-point primitive; nothing
else. In particular, `Puck.Audio` parses no document — a `puck.synth.v1`
patch crosses into the mixer's `VoicePatch` struct through the host's patch
factory, which stays in `Puck.World` alongside the rest of the presentation glue (the tune host, the
render device) documented in [`Puck.World`'s Audio/ folder](../Puck.World/Audio/README.md).

## Layering

`Puck.Audio` sits on Engine services, same row as `Puck.Physics`, with an
exact-equality closure the architecture gate enforces:
`Puck.Abstractions;Puck.Commands;Puck.Hosting;Puck.Maths` — no
`Puck.World.Forge`/`Puck.Assets` edge (no document parsing), no presentation
or backend row. The authoritative server references it directly (its machine
host boots every machine at `MachineAudioRate.SampleRate`).

## Verifying

`dotnet build Puck.slnx -c Release` plus `puck architecture` (the exact
closure above); `dotnet test tests/Puck.Audio.Tests` for the mixer/synth and
`MusicClock`/`MusicDirector`/`RhythmJudge` laws. Behavioral verification runs
the real windowed `Puck.World` and reads
`audio.state`/`audio.emitters`/`speaker.state`/`music.state`/`judge.state`
over stdin — there is no build-only gate for a world-audio feature.
`tests/Puck.World.Canaries/music-region-transition` proves a region crossing
fires exactly one segment transition, with the tick it fired on.
