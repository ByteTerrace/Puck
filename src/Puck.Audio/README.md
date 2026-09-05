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
commits on the next boundary `MusicClock` reports; a segment's conditional
audio layers recompute whole every `Step`, level-triggered off that tick's
edges rather than queued like a transition — `ActiveLayerTuneIds` names every
tune active this tick; a segment's director embellishments fire instantaneously
on a matching edge, recorded in `LastEmbellishmentPatchId`/`LastEmbellishmentTick`),
. Neither type references
`WorldEventFeed`/`WorldEventEdge` (the project that declares them sits above
this one in the layering) — `Puck.World.Server.MusicDirectorFactory` compiles
an authored `puck.music.v1` document into these shapes and
projects one tick's `WorldEventFeed.Edges` into `MusicSenseEdge`s at the
`WorldServer.Step` call site, immediately after `Collect()`. A rhythm hit
window is an authored `compareState` range over the `$clock:<music>:phaseError`
world-rule operand (the signed tick distance from `MusicClock`'s current
position to the nearest beat) rather than a dedicated grader type.

`VoiceBabbler` is the same tier's third primitive: `ComputeTriggerTicks`
turns a caller-estimated syllable count plus an identity's authored cadence
into the trigger tick of each syllable's short pitched voice — cadence-spaced,
with a bounded forward-only jitter drawn from one `Pcg32XshRr` stream seeded
`(state: utteranceOrdinal, stream: identitySeed)`. Like `MusicClock` it
never estimates a syllable count from text or chooses which
`VoicePatch` a trigger voices — both are the caller's job. Playback wiring is
landed on top of it: `Puck.World.Client.WorldAudioDirector.TriggerBabble`
resolves the delivered definition's single `WorldIdentityDefinition.Voice`
selectors, drives `ComputeTriggerTicks` for the utterance's tick schedule, and
fires one seeded `VoiceSynth` trigger per syllable as its delay elapses
(`AdvanceBabbleSchedule`) through the reserved `voice.babble` cue token
(`WorldAudioCue.EventTokens`) — never one sustained tone for the whole
utterance (proved by `WorldVoiceSynthTests.BabbleUtteranceFiresOneDistinctSeededTriggerPerSyllableBitIdenticalAcrossTwoFreshPairings`
and `...NeverCollapsesToASingleSustainedToneForMultipleSyllables`). `voice.state`/
`voice.babble` are its read-back/debug-trigger verbs. Two things stay open,
both later work outside this wiring: no producer yet estimates an utterance's
syllable count from dialogue/caption text (a presentation/content concern),
and a babbling identity has no live-body correlation yet, so every syllable
voices listener-placed rather than at a resolved world position — the same
simplification `WorldAudioDirector.SubmitEmbellishment` already takes for a
fire-time-chosen patch with no world site.

Built on `Puck.Hosting` (which itself carries `Puck.Abstractions` and
`Puck.Commands`) plus `Puck.Maths` for every fixed-point primitive; nothing
else. In particular, `Puck.Audio` parses no document — a `puck.synth.v1`
patch crosses into the mixer's `VoicePatch` struct through the host's patch
factory, which stays in `Puck.World` alongside the rest of the presentation glue (the tune host, the
render device) documented in [`Puck.World`'s Audio/ folder](../Puck.World/Audio/README.md).

## Layering

`Puck.Audio` sits on Engine services, same row as `Puck.Physics`, with an
exact-equality closure the architecture gate enforces:
`Puck.Abstractions;Puck.Assets;Puck.Commands;Puck.Hosting;Puck.Maths` — no
`Puck.World.Authoring` edge (no document parsing), no presentation
or backend row. The authoritative server references it directly (its machine
host boots every machine at `MachineAudioRate.SampleRate`).

## Verifying

`dotnet build Puck.slnx -c Release` plus `puck architecture` (the exact
closure above); `dotnet test tests/Puck.Audio.Tests` for the mixer/synth and
`MusicClock`/`MusicDirector`/`VoiceBabbler` laws. Behavioral verification runs
the real windowed `Puck.World` and reads
`audio.state`/`audio.emitters`/`speaker.state`/`voice.state`/`music.state`
over stdin — there is no build-only gate for a world-audio feature.
`tests/Puck.World.Canaries/voice-babble` proves the playback wiring end to
end: triggering an identity's babble fires four DISTINCT syllable triggers
(never collapsing to one) and the mix produces measurable signal
(`audio.state`'s `peak`); the discriminating leg (no trigger) proves neither
ever happens on its own.
`tests/Puck.World.Canaries/music-region-transition` proves a region crossing
fires exactly one segment transition, with the tick it fired on, and that the
transition fires the `music.transition` cue token (`WorldServer.MusicTransitionTap`,
wired to `WorldAudioDirector.SubmitCue` in `WorldPostBuildWiring`) — the crossing a
world's `audio.cues` table binds a stinger patch to.
`tests/Puck.World.Canaries/music-conditional-layer-and-embellishment` proves a
director embellishment fires exactly once off a matching sense edge (the
`music.embellishment` cue token, keyed by the embellishment's own authored
patch id — never an `audio.cues` row, since that table can only bind one
patch per token) and that an unconditional layer stays active throughout; a
conditional layer's own per-tick activation is proved with exact tick control
by `MusicDirectorTests` and `MusicJudgeReplayReDerivabilityLawTests`, not
re-proved against a canary's wall-clock-uncertain pacing.
